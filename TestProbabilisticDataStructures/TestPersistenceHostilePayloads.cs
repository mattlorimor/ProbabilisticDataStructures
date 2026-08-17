using System;
using System.Linq;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Payloads that are internally absurd but whose checksum is correct.
    /// <para>
    /// The corruption sweep in <see cref="TestPersistenceAllStructures"/> flips single
    /// bits and leaves the trailing CRC alone, so every one of its cases dies at the
    /// checksum. That proves the checksum works and nothing beyond it: no count read out
    /// of a payload is ever acted on, because the read never gets that far.
    /// </para>
    /// <para>
    /// The guards behind the checksum are what stands between a reader and a payload
    /// someone edited on purpose -- a file on disk is not a trusted input just because
    /// this library wrote one there once. These tests repair the CRC after editing, so
    /// the guard is what has to refuse them.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestPersistenceHostilePayloads
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        /// <summary>The payload begins after the fixed-size envelope header.</summary>
        private const int PayloadStart = 14;

        /// <summary>
        /// Overwrites a u32 at an offset within the payload and repairs the checksum, so
        /// that what refuses the result is a guard rather than the CRC.
        /// </summary>
        private static byte[] PokeUInt32(byte[] original, int payloadOffset, uint value)
        {
            var bytes = (byte[])original.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(PayloadStart + payloadOffset), value);
            RepairChecksum(bytes);
            return bytes;
        }

        /// <summary>
        /// Overwrites a u64 at an offset within the payload and repairs the checksum --
        /// the eight-byte sibling of <see cref="PokeUInt32"/>, for doubles poked by
        /// their bit pattern.
        /// </summary>
        private static byte[] PokeUInt64(byte[] original, int payloadOffset, ulong value)
        {
            var bytes = (byte[])original.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(
                bytes.AsSpan(PayloadStart + payloadOffset), value);
            RepairChecksum(bytes);
            return bytes;
        }

        /// <summary>
        /// Recomputes the trailing CRC over bytes 4 through 14 + n, as a writer would.
        /// </summary>
        private static void RepairChecksum(byte[] bytes)
        {
            var crc = new Crc32();
            crc.Append(bytes.AsSpan(4, bytes.Length - 8));
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(bytes.Length - 4), crc.GetCurrentHashAsUInt32());
        }

        /// <summary>
        /// Asserts a payload is refused, and that the message names the reason, so that a
        /// guard which stops firing is not covered for by some later incidental failure.
        /// </summary>
        private static void AssertRefused(Func<object> read, string expected)
        {
            var ex = Assert.ThrowsExactly<InvalidDataException>(() => read());
            StringAssert.Contains(ex.Message, expected);
        }

        /// <summary>
        /// Version 0 is reserved and never written. Accepting it would mean accepting a
        /// zeroed header as a valid empty structure.
        /// </summary>
        [TestMethod]
        public void TestFormatVersionZeroIsRefused()
        {
            var bytes = new BloomFilter(100, 0.01).ToByteArray();
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 0);
            RepairChecksum(bytes);

            AssertRefused(() => Persistence.FromByteArray<BloomFilter>(bytes), "version 0");
        }

        /// <summary>
        /// A payload length above int.MaxValue cannot be read into one array. It is
        /// refused on the strength of the declared length alone, before the allocation
        /// the checksum would have caught it at.
        /// </summary>
        [TestMethod]
        public void TestAPayloadLengthTooLargeToReadIsRefused()
        {
            var bytes = new BloomFilter(100, 0.01).ToByteArray();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), 0x8000_0000);

            // Deliberately not repairing the checksum: the point is that the length is
            // refused before anything is allocated for it, which is only true if the
            // check runs before the read the CRC would follow.
            AssertRefused(
                () => Persistence.FromByteArray<BloomFilter>(bytes),
                "cannot be read into a single array");
        }

        /// <summary>
        /// Buckets64 stores its bits across several arrays, and the array count is read
        /// before any of them are allocated.
        /// </summary>
        [TestMethod]
        public void TestAbsurdBucketArrayCountIsRefused()
        {
            // u64 m, u32 k, u64 count, then the Buckets64: u64 count, u8 size, u32 arrays.
            const int ArrayCountOffset = 8 + 4 + 8 + 8 + 1;

            var bytes = new BloomFilter64(1000, 0.01).ToByteArray();
            AssertRefused(
                () => Persistence.FromByteArray<BloomFilter64>(
                    PokeUInt32(bytes, ArrayCountOffset, 900_000_000)),
                "beyond anything this library builds");
        }

        /// <summary>
        /// A partitioned filter allocates one bucket array per partition.
        /// </summary>
        [TestMethod]
        public void TestAbsurdPartitionCountIsRefused()
        {
            // u32 m, u32 k, u32 s, u32 count, then the partition count.
            const int PartitionCountOffset = 16;

            var bytes = new PartitionedBloomFilter(1000, 0.01).ToByteArray();

            AssertRefused(
                () => Persistence.FromByteArray<PartitionedBloomFilter>(
                    PokeUInt32(bytes, PartitionCountOffset, 900_000_000)),
                "partitions");

            // Zero is refused from the other side: a filter with no partitions would set
            // no bits and answer no to everything rather than fail.
            AssertRefused(
                () => Persistence.FromByteArray<PartitionedBloomFilter>(
                    PokeUInt32(bytes, PartitionCountOffset, 0)),
                "partitions");
        }

        /// <summary>
        /// A scalable filter holds a list of whole nested filters, which is the most
        /// expensive count in the format to take on trust.
        /// </summary>
        [TestMethod]
        public void TestAbsurdContainedFilterCountIsRefused()
        {
            // f64 r, f64 fp, f64 p, u32 hint, then the filter count.
            const int FilterCountOffset = 8 + 8 + 8 + 4;

            var f = new ScalableBloomFilter(100, 0.01, 0.8);
            for (int i = 0; i < 500; i++) f.Add(Key($"w{i}"));

            AssertRefused(
                () => Persistence.FromByteArray<ScalableBloomFilter>(
                    PokeUInt32(f.ToByteArray(), FilterCountOffset, 900_000_000)),
                "contained filters");
        }

        /// <summary>
        /// The cuckoo filter's bucket count sizes its whole fingerprint array.
        /// </summary>
        [TestMethod]
        public void TestAbsurdCuckooBucketCountIsRefused()
        {
            var bytes = new CuckooBloomFilter(1000, 0.01).ToByteArray();

            AssertRefused(
                () => Persistence.FromByteArray<CuckooBloomFilter>(
                    PokeUInt32(bytes, 0, 900_000_000)),
                "buckets");
        }

        /// <summary>
        /// TopK's k sizes its heap.
        /// </summary>
        [TestMethod]
        public void TestAbsurdTopKSizeIsRefused()
        {
            var t = new TopK(0.1, 0.5, 3);
            for (int i = 0; i < 40; i++) t.Add(Key($"w{i % 5}"));

            AssertRefused(
                () => Persistence.FromByteArray<TopK>(PokeUInt32(t.ToByteArray(), 0, 900_000_000)),
                "elements");
        }

        /// <summary>
        /// A signature's length is both its allocation and the number of hash functions
        /// it claims were used, so zero is as wrong as absurdly large.
        /// </summary>
        [TestMethod]
        public void TestAbsurdSignatureLengthIsRefused()
        {
            var bytes = MinHash.Signature(new[] { "a", "b", "c" }, 8).ToByteArray();

            AssertRefused(
                () => Persistence.FromByteArray<MinHashSignature>(
                    PokeUInt32(bytes, 0, 900_000_000)),
                "not a signature this library builds");

            AssertRefused(
                () => Persistence.FromByteArray<MinHashSignature>(PokeUInt32(bytes, 0, 0)),
                "not a signature this library builds");
        }

        /// <summary>
        /// A Memento payload for the tests below to corrupt. Its prefix is fixed, so
        /// field offsets are too: the key count at 0, the expansion count at 8, the
        /// memento width at 12, the number of tables at 16, and the first table's
        /// address bits at 20, fingerprint bits at 24 and entry count at 28.
        /// </summary>
        private static byte[] MementoPayload()
        {
            var filter = new MementoFilter(64, 6, initialCapacity: 8);
            for (ulong i = 0; i < 150; i++)
            {
                filter.Add(i * 5);
            }
            return filter.ToByteArray();
        }

        /// <summary>
        /// A memento wider than a key's low bits could ever be describes a split this
        /// library never makes.
        /// </summary>
        [TestMethod]
        public void TestMementoFilterWithAnAbsurdMementoWidthIsRefused()
        {
            AssertRefused(
                () => Persistence.FromByteArray<MementoFilter>(
                    PokeUInt32(MementoPayload(), 12, 40)),
                "memento bits");
        }

        /// <summary>
        /// A filter with no tables has nothing to ask.
        /// </summary>
        [TestMethod]
        public void TestMementoFilterWithNoTablesIsRefused()
        {
            AssertRefused(
                () => Persistence.FromByteArray<MementoFilter>(
                    PokeUInt32(MementoPayload(), 16, 0)),
                "no tables at all");
        }

        /// <summary>
        /// A table claiming more entries than slots describes a table that cannot
        /// exist: it stores one slot per entry.
        /// </summary>
        [TestMethod]
        public void TestMementoFilterHoldingMoreThanItsSlotsIsRefused()
        {
            AssertRefused(
                () => Persistence.FromByteArray<MementoFilter>(
                    PokeUInt32(MementoPayload(), 28, 900_000)),
                "one entry per slot");
        }

        /// <summary>
        /// A table whose shape disagrees with the words it carries would read past
        /// what the payload holds.
        /// </summary>
        [TestMethod]
        public void TestMementoFilterWithAMismatchedShapeIsRefused()
        {
            AssertRefused(
                () => Persistence.FromByteArray<MementoFilter>(
                    PokeUInt32(MementoPayload(), 20, 12)),
                "carries");
        }

        /// <summary>
        /// An InfiniFilter payload for the tests below to corrupt. Its prefix is
        /// fixed, so field offsets are too: the item count at 0, the expansion count
        /// at 8, the number of tables at 12, and then per table its address bits,
        /// fingerprint bits, entry count and word count from 16.
        /// </summary>
        private static byte[] InfiniFilterPayload()
        {
            var filter = new InfiniFilter(initialCapacity: 8, fingerprintBits: 6);
            for (var i = 0; i < 120; i++)
            {
                filter.Add(Key($"item-{i}"));
            }
            return filter.ToByteArray();
        }

        /// <summary>
        /// A filter with no tables has nothing to ask, and every query begins by
        /// asking the first one.
        /// </summary>
        [TestMethod]
        public void TestInfiniFilterWithNoTablesIsRefused()
        {
            var bytes = PokeUInt32(InfiniFilterPayload(), 12, 0);
            AssertRefused(
                () => Persistence.FromByteArray<InfiniFilter>(bytes),
                "no tables at all");
        }

        /// <summary>
        /// A chain longer than a 64-bit hash could ever justify would allocate a
        /// table per claimed link before anything checked whether they exist.
        /// </summary>
        [TestMethod]
        public void TestInfiniFilterWithAnAbsurdChainIsRefused()
        {
            var bytes = PokeUInt32(InfiniFilterPayload(), 12, 900_000);
            AssertRefused(
                () => Persistence.FromByteArray<InfiniFilter>(bytes),
                "longer than a 64-bit hash");
        }

        /// <summary>
        /// A table claiming more entries than it has slots describes a quotient
        /// filter that cannot exist: it stores one entry per slot.
        /// </summary>
        [TestMethod]
        public void TestInfiniFilterHoldingMoreThanItsSlotsIsRefused()
        {
            // The first table's entry count sits at offset 24, after the two
            // eight-byte and two four-byte fields that precede it.
            var bytes = PokeUInt32(InfiniFilterPayload(), 24, 900_000);
            AssertRefused(
                () => Persistence.FromByteArray<InfiniFilter>(bytes),
                "one entry per slot");
        }

        /// <summary>
        /// A table whose address and fingerprint together outrun the hash could not
        /// have been built by any insertion.
        /// </summary>
        [TestMethod]
        public void TestInfiniFilterWithMoreBitsThanTheHashIsRefused()
        {
            var bytes = PokeUInt32(InfiniFilterPayload(), 16, 39);
            AssertRefused(
                () => Persistence.FromByteArray<InfiniFilter>(bytes),
                "carries");
        }

        /// <summary>
        /// A Grafite payload for the tests below to corrupt. Its scalar prefix is
        /// fixed, so field offsets are too: the reduced universe at 0, the hash
        /// multiplier at 8, its addend at 16, the key count at 24, the largest
        /// promised range at 32, and the low-bit split at 40.
        /// </summary>
        private static byte[] GrafitePayload()
        {
            var keys = Enumerable.Range(0, 60).Select(i => (ulong)(i * 37));
            return Grafite.Build(keys, 0.05, 8, seed: 11).ToByteArray();
        }

        /// <summary>
        /// A multiplier of zero collapses the hash's block term, so every block is
        /// shifted the same way and a query one reduced universe from a key collides
        /// with it every time. That is the attack the filter exists to survive.
        /// </summary>
        [TestMethod]
        public void TestGrafiteWithADegenerateMultiplierIsRefused()
        {
            var bytes = PokeUInt64(GrafitePayload(), 8, 0);
            AssertRefused(
                () => Persistence.FromByteArray<Grafite>(bytes),
                "hash parameters this library never draws");
        }

        /// <summary>
        /// A filter holding a hash code outside its own reduced universe describes a
        /// state hashing cannot produce, and would put the encoding's high parts past
        /// the end of the bitvector that indexes them.
        /// </summary>
        [TestMethod]
        public void TestGrafiteWithACodeOutsideItsUniverseIsRefused()
        {
            // The reduced universe, shrunk below the codes already stored in it.
            var bytes = PokeUInt64(GrafitePayload(), 0, 2);
            AssertRefused(
                () => Persistence.FromByteArray<Grafite>(bytes),
                "outside its own");
        }

        /// <summary>
        /// A split wider than a hash code is not a split at all, and the mask it
        /// implies would read low bits that were never written.
        /// </summary>
        [TestMethod]
        public void TestGrafiteWithAnImpossibleSplitIsRefused()
        {
            var bytes = PokeUInt32(GrafitePayload(), 40, 65);
            AssertRefused(
                () => Persistence.FromByteArray<Grafite>(bytes),
                "sixty-four bits wide");
        }

        /// <summary>
        /// An UltraLogLog payload for the tests below to corrupt: precision 5, so
        /// thirty-two registers. Its layout is fixed, so field offsets are too: the
        /// precision at 0, the register count at 4, and the registers themselves
        /// from 8.
        /// </summary>
        private static byte[] UltraLogLogPayload()
        {
            var sketch = new UltraLogLog(5);
            for (var i = 0; i < 200; i++)
            {
                sketch.Add(Key($"item-{i}"));
            }
            return sketch.ToByteArray();
        }

        /// <summary>
        /// A precision this library never builds would size the register array to
        /// something the estimator's per-precision factors do not cover.
        /// </summary>
        [TestMethod]
        public void TestUltraLogLogWithUnsupportedPrecisionIsRefused()
        {
            AssertRefused(
                () => Persistence.FromByteArray<UltraLogLog>(
                    PokeUInt32(UltraLogLogPayload(), 0, 2)),
                "only builds precisions");
            AssertRefused(
                () => Persistence.FromByteArray<UltraLogLog>(
                    PokeUInt32(UltraLogLogPayload(), 0, 27)),
                "only builds precisions");
        }

        /// <summary>
        /// A precision that disagrees with the number of registers carried would
        /// index the array by a mask it was not sized for.
        /// </summary>
        [TestMethod]
        public void TestUltraLogLogWithMismatchedRegisterCountIsRefused()
        {
            var bytes = PokeUInt32(UltraLogLogPayload(), 0, 6);
            AssertRefused(
                () => Persistence.FromByteArray<UltraLogLog>(bytes), "and carries");
        }

        /// <summary>
        /// A register recording a bit position below the index bits cannot have come
        /// from an insertion at this precision, and would put the estimator outside
        /// the range its corrections cover.
        /// </summary>
        [TestMethod]
        public void TestUltraLogLogWithImpossibleRegisterIsRefused()
        {
            // The registers begin at payload offset 8, after the precision and the
            // register count. Written little-endian, 4 sets the first register to a
            // recorded position of 1 and leaves the next three empty; precision 5
            // can never record below position 4.
            var bytes = PokeUInt32(UltraLogLogPayload(), 8, 4);
            AssertRefused(
                () => Persistence.FromByteArray<UltraLogLog>(bytes),
                "cannot have come from an insertion");
        }

        /// <summary>
        /// A VarOpt payload for the tests below to corrupt: k = 5, past capacity, so
        /// it is sampling. Its scalar prefix is fixed, so field offsets are too:
        /// k at 0, the generator state at 4, the count of items seen at 12, the
        /// exact-region size at 20, the threshold-region size at 24, and the
        /// threshold region's total weight at 28.
        /// </summary>
        private static byte[] VarOptPayload()
        {
            var sample = new VarOpt(5, seed: 7);
            for (var i = 0; i < 60; i++)
            {
                sample.Add(Key($"item-{i}"), 1.0 + (i % 8));
            }
            return sample.ToByteArray();
        }

        /// <summary>
        /// A sample that keeps nothing answers nothing, so the reader refuses to
        /// build one.
        /// </summary>
        [TestMethod]
        public void TestVarOptKeepingNothingIsRefused()
        {
            var bytes = PokeUInt32(VarOptPayload(), 0, 0);
            AssertRefused(
                () => Persistence.FromByteArray<VarOpt>(bytes), "keeps no samples");
        }

        /// <summary>
        /// A sample claiming to hold more items than it has room for would read
        /// past the end of its own arrays.
        /// </summary>
        [TestMethod]
        public void TestVarOptHoldingMoreThanItsRoomIsRefused()
        {
            var bytes = PokeUInt32(VarOptPayload(), 24, 200);
            AssertRefused(
                () => Persistence.FromByteArray<VarOpt>(bytes), "with room for");
        }

        /// <summary>
        /// The threshold is the region's weight divided by its size, so a weight
        /// that is not a positive number puts every adjusted weight in the sample
        /// beyond meaning while each item still looks reasonable.
        /// </summary>
        [TestMethod]
        public void TestVarOptWithUnusableThresholdWeightIsRefused()
        {
            var bytes = PokeUInt64(VarOptPayload(), 28,
                BitConverter.DoubleToUInt64Bits(double.NaN));
            AssertRefused(
                () => Persistence.FromByteArray<VarOpt>(bytes),
                "no positive weights can sum to");
        }

        /// <summary>
        /// Sampling only begins once more items have been seen than are kept, so a
        /// payload that is sampling while claiming to have seen fewer describes a
        /// state this structure never reaches.
        /// </summary>
        [TestMethod]
        public void TestVarOptSamplingWithoutEnoughItemsIsRefused()
        {
            var bytes = PokeUInt64(VarOptPayload(), 12, 3);
            AssertRefused(
                () => Persistence.FromByteArray<VarOpt>(bytes),
                "takes more items than fit");
        }

        /// <summary>
        /// A HeavyKeeper payload for the tests below to corrupt: k = 5, two arrays of
        /// sixteen buckets. Its payload layout is fixed, so field offsets are too:
        /// k at 0, width at 4, depth at 8, decay at 12, and -- after the 36-byte
        /// scalar prefix, 64 bytes of fingerprints and 256 of counters -- the tracked
        /// element count at 356.
        /// </summary>
        private static byte[] HeavyKeeperPayload()
        {
            var hk = new HeavyKeeper(5, 16, seed: 7);
            for (var i = 0; i < 60; i++)
            {
                hk.Add(Key($"item-{i % 8}"));
            }
            return hk.ToByteArray();
        }

        /// <summary>
        /// A HeavyKeeper that tracks no elements indexes its empty heap on the first
        /// add, so the reader refuses to build one.
        /// </summary>
        [TestMethod]
        public void TestHeavyKeeperTrackingNothingIsRefused()
        {
            var bytes = PokeUInt32(HeavyKeeperPayload(), 0, 0);
            AssertRefused(
                () => Persistence.FromByteArray<HeavyKeeper>(bytes), "tracks no");
        }

        /// <summary>
        /// A HeavyKeeper claiming more tracked elements than it has room for is
        /// refused before the reader loads a single one of them.
        /// </summary>
        [TestMethod]
        public void TestHeavyKeeperHoldingMoreThanItsRoomIsRefused()
        {
            var bytes = PokeUInt32(HeavyKeeperPayload(), 356, 200);
            AssertRefused(
                () => Persistence.FromByteArray<HeavyKeeper>(bytes), "with room for");
        }

        /// <summary>
        /// A decay base of one decays every bucket on every mismatch, so no bucket
        /// could hold anything; this library never writes such a structure, and the
        /// reader refuses to believe it did.
        /// </summary>
        [TestMethod]
        public void TestHeavyKeeperWithFlatDecayIsRefused()
        {
            var bytes = PokeUInt64(HeavyKeeperPayload(), 12,
                BitConverter.DoubleToUInt64Bits(1.0));
            AssertRefused(
                () => Persistence.FromByteArray<HeavyKeeper>(bytes), "decay base");
        }

        /// <summary>
        /// A width whose product with depth is beyond anything this library builds is
        /// refused before a single array is allocated for it.
        /// </summary>
        [TestMethod]
        public void TestHeavyKeeperClaimingAbsurdWidthIsRefused()
        {
            var bytes = PokeUInt32(HeavyKeeperPayload(), 4, 1 << 30);
            AssertRefused(
                () => Persistence.FromByteArray<HeavyKeeper>(bytes), "buckets, beyond");
        }
    }
}
