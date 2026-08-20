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
        /// Overwrites a single byte at an offset within the payload and repairs the
        /// checksum.
        /// </summary>
        private static byte[] PokeByte(byte[] original, int payloadOffset, byte value)
        {
            var bytes = (byte[])original.Clone();
            bytes[PayloadStart + payloadOffset] = value;
            RepairChecksum(bytes);
            return bytes;
        }

        /// <summary>
        /// Overwrites a u16 at an offset within the payload and repairs the checksum.
        /// </summary>
        private static byte[] PokeUInt16(byte[] original, int payloadOffset, ushort value)
        {
            var bytes = (byte[])original.Clone();
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(PayloadStart + payloadOffset), value);
            RepairChecksum(bytes);
            return bytes;
        }

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
    
        /// <summary>
        /// A tuple sketch to corrupt. Its prefix is fixed, so the offsets are: the
        /// retained size at 0, the way it folds values at 4, the sampling threshold at
        /// 5, and how many keys it holds at 13. The keys follow at 17, and their
        /// summaries after all of them.
        /// </summary>
        private static byte[] TuplePayload()
        {
            var sketch = new TupleSketch(64);
            for (var i = 0; i < 5_000; i++)
            {
                sketch.Add(Key($"user-{i}"), 1.0);
            }
            return sketch.ToByteArray();
        }

        /// <summary>
        /// A way of folding values this library does not have describes summaries that
        /// were built by something else.
        /// </summary>
        [TestMethod]
        public void TestTupleSketchWithAnUnknownPolicyIsRefused()
        {
            AssertRefused(
                () => Persistence.FromByteArray<TupleSketch>(PokeByte(TuplePayload(), 4, 99)),
                "folding");
        }

        /// <summary>
        /// Every key a tuple sketch keeps is strictly below its threshold, which is what
        /// makes the threshold usable as the sampling rate.
        /// </summary>
        [TestMethod]
        public void TestTupleSketchWithAKeyAboveItsThresholdIsRefused()
        {
            var payload = TuplePayload();
            var held = BinaryPrimitives.ReadUInt32LittleEndian(
                payload.AsSpan(PayloadStart + 13));

            // The last key, raised past the threshold while staying in order.
            var lastKey = 17 + (((int)held - 1) * sizeof(ulong));

            AssertRefused(
                () => Persistence.FromByteArray<TupleSketch>(
                    PokeUInt64(payload, lastKey, ulong.MaxValue - 1)),
                "threshold");
        }

        /// <summary>
        /// A tuple sketch writes its keys in increasing order, so a payload whose keys
        /// are not is not one it wrote.
        /// </summary>
        [TestMethod]
        public void TestTupleSketchWithKeysOutOfOrderIsRefused()
        {
            AssertRefused(
                () => Persistence.FromByteArray<TupleSketch>(
                    // The second key dropped to nought, below the first.
                    PokeUInt64(TuplePayload(), 17 + sizeof(ulong), 0)),
                "order");
        }

        /// <summary>
        /// A tuple sketch trims once it reaches twice what it retains, so it never holds
        /// more than that.
        /// </summary>
        [TestMethod]
        public void TestTupleSketchHoldingMoreThanItCouldIsRefused()
        {
            AssertRefused(
                () => Persistence.FromByteArray<TupleSketch>(
                    PokeUInt32(TuplePayload(), 13, 900_000)),
                "retains");
        }

        /// <summary>
        /// A summary that is not a number would spread to every total its key took part
        /// in.
        /// </summary>
        [TestMethod]
        public void TestTupleSketchWithASummaryThatIsNotANumberIsRefused()
        {
            var payload = TuplePayload();
            var held = BinaryPrimitives.ReadUInt32LittleEndian(
                payload.AsSpan(PayloadStart + 13));

            var firstSummary = 17 + ((int)held * sizeof(ulong));

            AssertRefused(
                () => Persistence.FromByteArray<TupleSketch>(
                    PokeUInt64(payload, firstSummary,
                        BitConverter.DoubleToUInt64Bits(double.NaN))),
                "number");
        }
    
        /// <summary>
        /// A set sketch to corrupt. Its prefix is fixed: the base at 0, the hash rate at
        /// 8, the register count at 16, the ceiling at 20, and the registers from 24.
        /// </summary>
        private static byte[] SetSketchPayload()
        {
            var sketch = new SetSketch(64, 1.001, 20, 1_000);
            for (var i = 0; i < 100; i++)
            {
                sketch.Add(Key($"item-{i}"));
            }
            return sketch.ToByteArray();
        }

        /// <summary>
        /// A register above the ceiling the sketch was built with is not a value it can
        /// write, and taken at face value it drags every estimate the sketch makes.
        /// </summary>
        [TestMethod]
        public void TestSetSketchWithARegisterAboveItsCeilingIsRefused()
        {
            AssertRefused(
                () => Persistence.FromByteArray<SetSketch>(
                    PokeUInt16(SetSketchPayload(), 24, 1_002)),
                "ceiling");
        }

        /// <summary>
        /// At a base of one the register values would not move with the cardinality at
        /// all, and the estimators are only claimed up to two.
        /// </summary>
        [TestMethod]
        public void TestSetSketchWithAnImpossibleBaseIsRefused()
        {
            foreach (var b in new[] { 1.0, 0.5, 3.0, double.NaN })
            {
                AssertRefused(
                    () => Persistence.FromByteArray<SetSketch>(
                        PokeUInt64(SetSketchPayload(), 0,
                            BitConverter.DoubleToUInt64Bits(b))),
                    "base");
            }
        }

        /// <summary>
        /// A register holds two bytes and has to hold one more than the ceiling.
        /// </summary>
        [TestMethod]
        public void TestSetSketchWithAnImpossibleCeilingIsRefused()
        {
            AssertRefused(
                () => Persistence.FromByteArray<SetSketch>(
                    PokeUInt32(SetSketchPayload(), 20, ushort.MaxValue)),
                "ceiling");
        }

        /// <summary>
        /// A sketch with no registers has nothing to estimate from.
        /// </summary>
        [TestMethod]
        public void TestSetSketchWithNoRegistersIsRefused()
        {
            AssertRefused(
                () => Persistence.FromByteArray<SetSketch>(
                    PokeUInt32(SetSketchPayload(), 16, 0)),
                "registers");
        }

        /// <summary>
        /// A Sublime sketch to corrupt. Its prefix is fixed: delta at 0, the growth
        /// exponent at 8, the size factor at 16, the row count at 24, the width at 28,
        /// and the counts that follow.
        /// </summary>
        private static byte[] SublimePayload()
        {
            var sketch = new SublimeCountMinSketch(0.02);
            for (var i = 0; i < 6_000; i++)
            {
                sketch.Add(Key($"flow-{i % 40}"));
            }
            return sketch.ToByteArray();
        }

        /// <summary>
        /// A width is a power of two, because the low bits of a hash pick the counter.
        /// A width that is not one sends every query to a counter the writer never used.
        /// </summary>
        [TestMethod]
        public void TestSublimeCountMinSketchWithAWidthThatIsNotAPowerOfTwoIsRefused()
        {
            AssertRefused(
                () => Persistence.FromByteArray<SublimeCountMinSketch>(
                    PokeUInt32(SublimePayload(), 28, 100)),
                "power of two");
        }

        /// <summary>
        /// A sketch with no rows reports every element as seen the maximum number of
        /// times.
        /// </summary>
        [TestMethod]
        public void TestSublimeCountMinSketchWithNoRowsIsRefused()
        {
            AssertRefused(
                () => Persistence.FromByteArray<SublimeCountMinSketch>(
                    PokeUInt32(SublimePayload(), 24, 0)),
                "rows");
        }

        /// <summary>
        /// The growth exponent has to lie between nought and one; at one the sketch
        /// would grow as fast as the stream it summarises.
        /// </summary>
        [TestMethod]
        public void TestSublimeCountMinSketchWithAnImpossibleGrowthIsRefused()
        {
            foreach (var growth in new[] { 0.0, 1.0, 2.0, double.NaN })
            {
                AssertRefused(
                    () => Persistence.FromByteArray<SublimeCountMinSketch>(
                        PokeUInt64(SublimePayload(), 8,
                            BitConverter.DoubleToUInt64Bits(growth))),
                    "growth exponent");
            }
        }

        /// <summary>
        /// The guard the private sketch exists for. A counter is a draw from a
        /// continuous distribution plus a whole number of hits, so it is non-integral
        /// with probability one and stays so for the sketch's whole life. A payload
        /// whose every counter is an exact integer is a plain Count-Min Sketch wearing
        /// this one's name -- it protects nobody, and reading it would hand back a
        /// structure whose entire contract is silently false.
        /// <para>
        /// The payload is rewritten counter by counter with the noise floored away,
        /// which is precisely what a sketch built with the mechanism disabled would
        /// have written, and the checksum repaired so the guard is what refuses it.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestAPrivateSketchWithNoNoiseIsRefused()
        {
            var sketch = new PrivateCountMinSketch(16, 3, 0.5, seed: 99);
            for (var i = 0; i < 200; i++)
            {
                sketch.Add(Key($"item-{i}"));
            }

            var bytes = sketch.ToByteArray();

            // Shape is four u32/u64 fields: width, depth, rho, count.
            const int CountersAt = 4 + 4 + 8 + 8;
            for (var i = 0; i < 16 * 3; i++)
            {
                var at = PayloadStart + CountersAt + (i * 8);
                var counter = BitConverter.ToDouble(bytes, at);
                BinaryPrimitives.WriteDoubleLittleEndian(
                    bytes.AsSpan(at), Math.Floor(counter));
            }
            RepairChecksum(bytes);

            AssertRefused(
                () => Persistence.FromByteArray<PrivateCountMinSketch>(bytes),
                "exact integer");
        }

        /// <summary>
        /// The same guard reached through a DPSW payload, which holds its private
        /// sketches inline. Every counter in every nested sketch is floored, which is
        /// what a window built with the mechanism disabled would have written.
        /// <para>
        /// The payload is walked field by field rather than scanned at a fixed stride:
        /// a stride guesses at where the doubles are, and the first draft of this test
        /// guessed wrong, flooring the window length itself and getting refused for
        /// the wrong reason. Walking it means the bytes edited are exactly the
        /// counters and nothing else.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestADpswWindowWithNoNoiseIsRefused()
        {
            var sketch = new DpswSketch(
                window: 256, rho: 1.0, alpha: 0.5, width: 16, depth: 3, seed: 7);
            for (var i = 0; i < 400; i++)
            {
                sketch.Add(Key($"item-{i % 40}"));
            }

            var bytes = sketch.ToByteArray();
            var planLength = 1 + (2 * (sketch.Checkpointing.Length - 1));

            // window(8) rho(8) alpha(8) substreamSize(4) width(4) depth(4) position(8)
            var at = PayloadStart + 44;
            var substreamCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
            at += 4;

            var floored = 0;
            for (var s = 0u; s < substreamCount; s++)
            {
                at += 12;  // start(8) held(4)

                for (var p = 0; p < planLength; p++)
                {
                    var width = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
                    var depth = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
                    at += 24;  // width(4) depth(4) rho(8) count(8)

                    for (var c = 0; c < width * depth; c++)
                    {
                        var counter = BitConverter.ToDouble(bytes, at);
                        BinaryPrimitives.WriteDoubleLittleEndian(
                            bytes.AsSpan(at), Math.Floor(counter));
                        if (counter != Math.Floor(counter))
                        {
                            floored++;
                        }
                        at += 8;
                    }
                }
            }

            Assert.AreEqual(bytes.Length - 4, at,
                "the walk did not land exactly on the trailing checksum, so the " +
                "layout assumed here is not the layout written.");
            Assert.IsGreaterThan(0, floored,
                "no non-integral counter was found, so nothing was disabled and the " +
                "guard below is asserted on air.");

            RepairChecksum(bytes);

            AssertRefused(
                () => Persistence.FromByteArray<DpswSketch>(bytes),
                "exact integer");
        }

        /// <summary>
        /// A window whose substreams do not follow one another in order. Substreams
        /// divide the stream in sequence, and a payload claiming two that overlap or
        /// repeat describes a stream that never happened -- the estimates would then
        /// double-count an item across the two, which is both wrong and a privacy
        /// claim the budget split was never made for.
        /// </summary>
        [TestMethod]
        public void TestADpswWindowWithOutOfOrderSubstreamsIsRefused()
        {
            var (bytes, layout) = SmallDpswPayload();

            Assert.IsGreaterThan(1u, layout.SubstreamCount,
                "the payload must hold at least two substreams for their ordering to " +
                "be corruptible at all.");

            // Point the second substream back at the first one's start.
            var firstStart = BinaryPrimitives.ReadUInt64LittleEndian(
                bytes.AsSpan(layout.SubstreamAt(0)));
            var poked = PokeUInt64(bytes, layout.SubstreamAt(1) - PayloadStart, firstStart);

            AssertRefused(
                () => Persistence.FromByteArray<DpswSketch>(poked),
                "does not follow the one before it");
        }

        /// <summary>
        /// A window claiming a substream holds more items than a substream can. The
        /// count decides which items a query attributes to that substream, so a
        /// payload inflating it moves the window boundary without moving any counter.
        /// </summary>
        [TestMethod]
        public void TestADpswWindowWithAnOverfullSubstreamIsRefused()
        {
            var (bytes, layout) = SmallDpswPayload();

            var poked = PokeUInt32(
                bytes, layout.SubstreamAt(0) + 8 - PayloadStart, (uint)layout.SubstreamSize + 1);

            AssertRefused(
                () => Persistence.FromByteArray<DpswSketch>(poked),
                "of a possible");
        }

        /// <summary>
        /// And one claiming a substream holds nothing. A substream exists because
        /// something was added to it; an empty one is a plan with no stream under it.
        /// </summary>
        [TestMethod]
        public void TestADpswWindowWithAnEmptySubstreamIsRefused()
        {
            var (bytes, layout) = SmallDpswPayload();

            var poked = PokeUInt32(bytes, layout.SubstreamAt(0) + 8 - PayloadStart, 0);

            AssertRefused(
                () => Persistence.FromByteArray<DpswSketch>(poked),
                "of a possible");
        }

        /// <summary>Where each substream begins within a DPSW payload.</summary>
        private sealed record DpswLayout(
            uint SubstreamCount, int SubstreamSize, int PlanLength, uint Width, uint Depth)
        {
            /// <summary>
            /// Header is window(8) rho(8) alpha(8) substreamSize(4) width(4) depth(4)
            /// position(8), then the substream count; each substream is start(8)
            /// held(4) followed by its plan's sketches, each of which is width(4)
            /// depth(4) rho(8) count(8) and then its counters.
            /// </summary>
            internal int SubstreamAt(int index) =>
                PayloadStart + 48
                + (index * (12 + (PlanLength * (24 + ((int)(Width * Depth) * 8)))));
        }

        /// <summary>
        /// A window small enough to poke at, with several substreams so their ordering
        /// can be corrupted, and its layout worked out alongside it.
        /// </summary>
        private static (byte[] Bytes, DpswLayout Layout) SmallDpswPayload()
        {
            var sketch = new DpswSketch(
                window: 64, rho: 4.0, alpha: 0.6, width: 4, depth: 2, seed: 11);
            for (var i = 0; i < 90; i++)
            {
                sketch.Add(Key($"item-{i % 9}"));
            }

            var bytes = sketch.ToByteArray();
            var layout = new DpswLayout(
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(PayloadStart + 44)),
                sketch.SubstreamSize,
                1 + (2 * (sketch.Checkpointing.Length - 1)),
                4, 2);

            Assert.AreEqual(bytes.Length - 4, layout.SubstreamAt((int)layout.SubstreamCount),
                "the layout assumed here does not reach the trailing checksum, so " +
                "every offset below points somewhere else.");

            return (bytes, layout);
        }

        /// <summary>
        /// Overwrites the hash id in the envelope header and repairs the checksum. The
        /// hash id lives before the payload, so the poking helpers above cannot reach
        /// it.
        /// </summary>
        private static byte[] PokeHashId(byte[] original, ushort hashId)
        {
            var bytes = (byte[])original.Clone();
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), hashId);
            RepairChecksum(bytes);
            return bytes;
        }

        /// <summary>
        /// A filter's declared bit count and the buckets it carries have to describe the
        /// same filter. They are written separately, so a payload can claim one and
        /// carry the other.
        /// </summary>
        [TestMethod]
        public void TestCountingFilterWhoseSizeDisagreesWithItsBucketsIsRefused()
        {
            var bytes = Filled(new CountingBloomFilter(200, 4, 0.01)).ToByteArray();

            AssertRefused(
                () => Persistence.FromByteArray<CountingBloomFilter>(PokeUInt32(bytes, 0, 12_345)),
                "do not describe the same filter");
        }

        /// <summary>
        /// The deletable filter's data region and its buckets, likewise.
        /// </summary>
        [TestMethod]
        public void TestDeletableFilterWhoseSizeDisagreesWithItsBucketsIsRefused()
        {
            var bytes = Filled(new DeletableBloomFilter(200, 10, 0.01)).ToByteArray();

            AssertRefused(
                () => Persistence.FromByteArray<DeletableBloomFilter>(PokeUInt32(bytes, 0, 12_345)),
                "do not describe the same filter");
        }

        /// <summary>
        /// A stable filter with no hash functions sets no cells and tests none, so it
        /// answers no to everything rather than failing.
        /// </summary>
        [TestMethod]
        public void TestStableFilterWithNoHashFunctionsIsRefused()
        {
            // u32 m, then k.
            const int KOffset = 4;

            var bytes = Filled(new StableBloomFilter(200, 2, 0.01, seed: 5)).ToByteArray();

            AssertRefused(
                () => Persistence.FromByteArray<StableBloomFilter>(PokeUInt32(bytes, KOffset, 0)),
                "no hash functions");
        }

        /// <summary>
        /// The inverse filter divides by its capacity to pick a slot, so a capacity of
        /// zero is a division by zero on the first add rather than a smaller filter.
        /// </summary>
        [TestMethod]
        public void TestInverseFilterWithNoCapacityIsRefused()
        {
            var bytes = Filled(new InverseBloomFilter(64)).ToByteArray();

            AssertRefused(
                () => Persistence.FromByteArray<InverseBloomFilter>(PokeUInt32(bytes, 0, 0)),
                "capacity of zero");
        }

        /// <summary>
        /// An entry whose slot index is past the end of the filter would be written
        /// outside the array it belongs to.
        /// </summary>
        [TestMethod]
        public void TestInverseFilterHoldingAnEntryPastItsCapacityIsRefused()
        {
            // u32 capacity, u32 occupied, then the first entry's slot index.
            const int FirstSlotOffset = 8;

            var bytes = Filled(new InverseBloomFilter(64)).ToByteArray();

            AssertRefused(
                () => Persistence.FromByteArray<InverseBloomFilter>(
                    PokeUInt32(bytes, FirstSlotOffset, 100_000)),
                "beyond its capacity");
        }

        /// <summary>
        /// A sketch with no rows reports every element as seen ulong.MaxValue times,
        /// which is the emptiest possible sketch answering as confidently as a full one.
        /// </summary>
        [TestMethod]
        public void TestCountMinSketchWithNoRowsIsRefused()
        {
            // f64 epsilon, f64 delta, u32 width, then depth.
            const int DepthOffset = 8 + 8 + 4;

            var bytes = FilledSketch().ToByteArray();

            AssertRefused(
                () => Persistence.FromByteArray<CountMinSketch>(PokeUInt32(bytes, DepthOffset, 0)),
                "no rows");
        }

        /// <summary>
        /// A register index is taken from the top bits of a hash, so a register count
        /// that is not a power of two cannot be indexed at all.
        /// </summary>
        [TestMethod]
        public void TestHyperLogLogWithARegisterCountThatIsNotAPowerOfTwoIsRefused()
        {
            var estimator = new HyperLogLog(64);
            for (var i = 0; i < 40; i++) estimator.Add(Key($"item-{i}"));

            AssertRefused(
                () => Persistence.FromByteArray<HyperLogLog>(
                    PokeUInt32(estimator.ToByteArray(), 0, 100)),
                "not a power of two");
        }

        /// <summary>
        /// The fingerprint width decides how many bytes each entry occupies, so a width
        /// this library does not build would be read at the wrong stride.
        /// </summary>
        [TestMethod]
        public void TestBinaryFuseFilterWithAnImpossibleFingerprintWidthIsRefused()
        {
            // u32 keys, u32 segment length, u32 segment count, u64 seed, then the width.
            const int WidthOffset = 4 + 4 + 4 + 8;

            var bytes = BinaryFuseFilter.Build(
                Enumerable.Range(0, 40).Select(i => Key($"item-{i}"))).ToByteArray();

            AssertRefused(
                () => Persistence.FromByteArray<BinaryFuseFilter>(PokeByte(bytes, WidthOffset, 4)),
                "8 or 16 bits wide");
        }

        /// <summary>
        /// The relative accuracy is the whole of a DDSketch's promise: it fixes the
        /// bucket boundaries, so a value outside nought to one describes no sketch.
        /// </summary>
        [TestMethod]
        public void TestDDSketchWithAnImpossibleAccuracyIsRefused()
        {
            var sketch = new DDSketch(0.1);
            for (var i = 1; i <= 40; i++) sketch.Add(i);

            AssertRefused(
                () => Persistence.FromByteArray<DDSketch>(
                    PokeUInt64(sketch.ToByteArray(), 0, BitConverter.DoubleToUInt64Bits(2.0))),
                "does not describe a sketch");
        }

        /// <summary>
        /// A precision outside the buildable range would size the register array to
        /// something the estimator's own constants do not describe.
        /// </summary>
        [TestMethod]
        public void TestHyperLogLogPlusWithAnImpossiblePrecisionIsRefused()
        {
            var bytes = FilledHllPlus().ToByteArray();

            AssertRefused(
                () => Persistence.FromByteArray<HyperLogLogPlus>(PokeUInt32(bytes, 0, 3)),
                "and this library builds");
        }

        /// <summary>
        /// There are two representations and no third. A payload naming one this library
        /// does not have would otherwise be read as whichever the reader defaulted to.
        /// </summary>
        [TestMethod]
        public void TestHyperLogLogPlusWithAnUnknownRepresentationIsRefused()
        {
            // u32 precision, then the representation byte.
            const int RepresentationOffset = 4;

            var bytes = FilledHllPlus().ToByteArray();

            AssertRefused(
                () => Persistence.FromByteArray<HyperLogLogPlus>(
                    PokeByte(bytes, RepresentationOffset, 7)),
                "only the sparse one and the dense one");
        }

        /// <summary>
        /// The table is indexed by the quotient bits, so zero of them indexes nothing.
        /// </summary>
        [TestMethod]
        public void TestQuotientFilterWithNoQuotientBitsIsRefused()
        {
            var filter = new QuotientFilter(100, 0.01);
            for (var i = 0; i < 40; i++) filter.Add(Key($"item-{i}"));

            AssertRefused(
                () => Persistence.FromByteArray<QuotientFilter>(
                    PokeUInt32(filter.ToByteArray(), 0, 0)),
                "between 1 and 32");
        }

        /// <summary>
        /// A value at or above theta is one the sampling that produced the rest would
        /// have thrown away, so its presence says the payload was not written by this.
        /// </summary>
        [TestMethod]
        public void TestThetaSketchHoldingAValueAboveItsThresholdIsRefused()
        {
            // u32 k, u64 theta, u32 held, then the first value.
            const int FirstValueOffset = 4 + 8 + 4;

            var sketch = new ThetaSketch(16);
            for (var i = 0; i < 200; i++) sketch.Add(Key($"item-{i}"));

            AssertRefused(
                () => Persistence.FromByteArray<ThetaSketch>(
                    PokeUInt64(sketch.ToByteArray(), FirstValueOffset, ulong.MaxValue)),
                "at or above its theta");
        }

        /// <summary>
        /// A signature is a fingerprint and nothing else, so the only thing that can be
        /// wrong about it is which hash built it -- and comparing fingerprints from
        /// different hashes gives a number that means nothing.
        /// </summary>
        [TestMethod]
        public void TestSimHashSignatureBuiltWithAnotherHashIsRefused()
        {
            var bytes = SimHash.Signature(new[] { "a", "b", "c" }).ToByteArray();

            // 2 is the id for a structure that hashes nothing, which a signature is not.
            AssertRefused(
                () => Persistence.FromByteArray<SimHashSignature>(PokeHashId(bytes, 2)),
                "this version builds them with XxHash3");
        }

        /// <summary>
        /// A sketch with no rows has no cells to count in, and would answer every query
        /// from an empty median.
        /// </summary>
        [TestMethod]
        public void TestCountSketchWithNoRowsIsRefused()
        {
            // u32 width, then depth.
            const int DepthOffset = 4;

            var sketch = new CountSketch(0.5, 0.5);
            for (var i = 0; i < 40; i++) sketch.Add(Key($"item-{i}"), i % 5);

            AssertRefused(
                () => Persistence.FromByteArray<CountSketch>(
                    PokeUInt32(sketch.ToByteArray(), DepthOffset, 0)),
                "no cells to count in");
        }

        /// <summary>
        /// A key occupies several cells at once, so a table with fewer cells than that
        /// cannot hold one key.
        /// </summary>
        [TestMethod]
        public void TestIbltWithFewerCellsThanAKeyOccupiesIsRefused()
        {
            var table = new InvertibleBloomLookupTable(8, 8);
            for (var i = 0; i < 6; i++)
            {
                var key = new byte[8];
                key[0] = (byte)i;
                table.Add(key);
            }

            AssertRefused(
                () => Persistence.FromByteArray<InvertibleBloomLookupTable>(
                    PokeUInt32(table.ToByteArray(), 0, 2)),
                "a key occupies");
        }

        /// <summary>
        /// A key size of zero leaves the stored keys no width, so every key in the table
        /// would be the same empty one.
        /// </summary>
        [TestMethod]
        public void TestIbltWithNoKeyWidthIsRefused()
        {
            // u32 cells, then the key size.
            const int KeySizeOffset = 4;

            var table = new InvertibleBloomLookupTable(8, 8);
            table.Add(new byte[8]);

            AssertRefused(
                () => Persistence.FromByteArray<InvertibleBloomLookupTable>(
                    PokeUInt32(table.ToByteArray(), KeySizeOffset, 0)),
                "a key is at least one");
        }

        /// <summary>
        /// The value width decides how wide each cell is, so one this library does not
        /// build would be read at the wrong stride.
        /// </summary>
        [TestMethod]
        public void TestBloomierFilterWithAnImpossibleValueWidthIsRefused()
        {
            // u32 keys, u32 segment length, u32 segment count, u64 seed, then value bits.
            const int ValueBitsOffset = 4 + 4 + 4 + 8;

            var bytes = BloomierFilter.Build(
                Enumerable.Range(0, 40).Select(i =>
                    new System.Collections.Generic.KeyValuePair<byte[], ulong>(
                        Key($"item-{i}"), (ulong)i)),
                8).ToByteArray();

            AssertRefused(
                () => Persistence.FromByteArray<BloomierFilter>(
                    PokeUInt32(bytes, ValueBitsOffset, 50)),
                "between 1 and 40 bits");
        }

        private static T Filled<T>(T filter) where T : IFilter
        {
            for (var i = 0; i < 40; i++) filter.Add(Key($"item-{i}"));
            return filter;
        }

        private static CountMinSketch FilledSketch()
        {
            var sketch = new CountMinSketch(0.1, 0.5);
            for (var i = 0; i < 40; i++) sketch.Add(Key($"item-{i % 5}"));
            return sketch;
        }

        private static HyperLogLogPlus FilledHllPlus()
        {
            var estimator = new HyperLogLogPlus(6);
            for (var i = 0; i < 40; i++) estimator.Add(Key($"item-{i}"));
            return estimator;
        }

        /// <summary>
        /// Every structure is refused at least one payload that is internally absurd.
        /// </summary>
        /// <remarks>
        /// Unlike the sweeps in <see cref="TestPersistenceAllStructures"/>, this cannot
        /// be one loop: what is absurd differs per structure, because the fields differ.
        /// So the coverage is a map from each structure to the test that carries it, and
        /// both halves are checked -- the roster comes from <see cref="StructureId"/>,
        /// which a new structure cannot leave itself off of, and each named test has to
        /// exist, so an entry pointing at a test someone renamed or deleted fails rather
        /// than silently vouching for nothing.
        /// <para>
        /// The drift here ran backwards from the sweeps: the structures added most
        /// recently had the most field-level guards tested, and fifteen of the oldest --
        /// including HyperLogLog, DDSketch and ThetaSketch -- had none at all. Their
        /// readers had the guards; nothing exercised them.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestEveryStructureRefusesSomeAbsurdPayload()
        {
            var carriedBy = new (StructureId Id, string Test)[]
            {
                (StructureId.BloomFilter, nameof(TestFormatVersionZeroIsRefused)),
                (StructureId.BloomFilter64, nameof(TestAbsurdBucketArrayCountIsRefused)),
                (StructureId.CountingBloomFilter,
                    nameof(TestCountingFilterWhoseSizeDisagreesWithItsBucketsIsRefused)),
                (StructureId.DeletableBloomFilter,
                    nameof(TestDeletableFilterWhoseSizeDisagreesWithItsBucketsIsRefused)),
                (StructureId.PartitionedBloomFilter,
                    nameof(TestAbsurdPartitionCountIsRefused)),
                (StructureId.ScalableBloomFilter,
                    nameof(TestAbsurdContainedFilterCountIsRefused)),
                (StructureId.StableBloomFilter,
                    nameof(TestStableFilterWithNoHashFunctionsIsRefused)),
                (StructureId.InverseBloomFilter,
                    nameof(TestInverseFilterWithNoCapacityIsRefused)),
                (StructureId.CuckooBloomFilter,
                    nameof(TestAbsurdCuckooBucketCountIsRefused)),
                (StructureId.CountMinSketch, nameof(TestCountMinSketchWithNoRowsIsRefused)),
                (StructureId.HyperLogLog,
                    nameof(TestHyperLogLogWithARegisterCountThatIsNotAPowerOfTwoIsRefused)),
                (StructureId.TopK, nameof(TestAbsurdTopKSizeIsRefused)),
                (StructureId.MinHashSignature, nameof(TestAbsurdSignatureLengthIsRefused)),
                (StructureId.BinaryFuseFilter,
                    nameof(TestBinaryFuseFilterWithAnImpossibleFingerprintWidthIsRefused)),
                (StructureId.DDSketch,
                    nameof(TestDDSketchWithAnImpossibleAccuracyIsRefused)),
                (StructureId.HyperLogLogPlus,
                    nameof(TestHyperLogLogPlusWithAnImpossiblePrecisionIsRefused)),
                (StructureId.QuotientFilter,
                    nameof(TestQuotientFilterWithNoQuotientBitsIsRefused)),
                (StructureId.ThetaSketch,
                    nameof(TestThetaSketchHoldingAValueAboveItsThresholdIsRefused)),
                (StructureId.SimHashSignature,
                    nameof(TestSimHashSignatureBuiltWithAnotherHashIsRefused)),
                (StructureId.CountSketch, nameof(TestCountSketchWithNoRowsIsRefused)),
                (StructureId.InvertibleBloomLookupTable,
                    nameof(TestIbltWithFewerCellsThanAKeyOccupiesIsRefused)),
                (StructureId.BloomierFilter,
                    nameof(TestBloomierFilterWithAnImpossibleValueWidthIsRefused)),
                (StructureId.HeavyKeeper, nameof(TestHeavyKeeperTrackingNothingIsRefused)),
                (StructureId.VarOpt, nameof(TestVarOptKeepingNothingIsRefused)),
                (StructureId.UltraLogLog,
                    nameof(TestUltraLogLogWithUnsupportedPrecisionIsRefused)),
                (StructureId.Grafite,
                    nameof(TestGrafiteWithADegenerateMultiplierIsRefused)),
                (StructureId.InfiniFilter, nameof(TestInfiniFilterWithNoTablesIsRefused)),
                (StructureId.MementoFilter,
                    nameof(TestMementoFilterWithAnAbsurdMementoWidthIsRefused)),
                (StructureId.SublimeCountMinSketch,
                    nameof(TestSublimeCountMinSketchWithAWidthThatIsNotAPowerOfTwoIsRefused)),
                (StructureId.SetSketch,
                    nameof(TestSetSketchWithARegisterAboveItsCeilingIsRefused)),
                (StructureId.TupleSketch,
                    nameof(TestTupleSketchWithAnUnknownPolicyIsRefused)),
                (StructureId.PrivateCountMinSketch,
                    nameof(TestAPrivateSketchWithNoNoiseIsRefused)),
                (StructureId.DpswSketch, nameof(TestADpswWindowWithNoNoiseIsRefused)),
            };

            foreach (var (id, test) in carriedBy)
            {
                var method = typeof(TestPersistenceHostilePayloads).GetMethod(test);
                Assert.IsNotNull(method,
                    $"{id} is said to be covered by {test}, which is not a test here");
                Assert.IsTrue(
                    method.GetCustomAttributes(typeof(TestMethodAttribute), false).Length > 0,
                    $"{id} is said to be covered by {test}, which is not a test method");
            }

            StructureRoster.AssertCoversEveryStructure(
                "absurd payloads", carriedBy.Select(c => c.Id));
        }

    }
}
