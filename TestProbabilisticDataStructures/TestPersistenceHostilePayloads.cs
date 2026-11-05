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
        internal static byte[] PokeUInt32(byte[] original, int payloadOffset, uint value)
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

        internal static byte[] PokeUInt64(byte[] original, int payloadOffset, ulong value)
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
            // u32 m, u32 k, u32 s, u8 placement, u64 count, then the partition count.
            const int PartitionCountOffset = 21;

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
            // f64 r, f64 fp, f64 p, u32 hint, then the filter count at version 1; at
            // version 7 the growth factor comes between the hint and the count.
            const int FilterCountOffset = 8 + 8 + 8 + 4;

            foreach (var (growth, offset) in new[] { (1u, FilterCountOffset), (2u, FilterCountOffset + 4) })
            {
                var f = new ScalableBloomFilter(100, 0.01, 0.8, growth);
                for (int i = 0; i < 500; i++) f.Add(Key($"w{i}"));

                AssertRefused(
                    () => Persistence.FromByteArray<ScalableBloomFilter>(
                        PokeUInt32(f.ToByteArray(), offset, 900_000_000)),
                    "contained filters");
            }
        }

        /// <summary>
        /// A version 7 payload carries a growth factor, and only a filter that grows
        /// writes one: a filter that adds every filter at its hint writes version 1.
        /// Zero would size every filter after the first for no items, and one at
        /// version 7 is a payload this library never produced.
        /// </summary>
        [TestMethod]
        public void TestScalableFilterWithAGrowthFactorItNeverWritesIsRefused()
        {
            const int GrowthOffset = 8 + 8 + 8 + 4;

            var f = new ScalableBloomFilter(100, 0.01, 0.8, 2);
            for (int i = 0; i < 500; i++) f.Add(Key($"w{i}"));
            var bytes = f.ToByteArray();

            Assert.AreEqual(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(PayloadStart + GrowthOffset)),
                "the growth factor is not where this test expects it");

            foreach (var growth in new uint[] { 0, 1 })
            {
                AssertRefused(
                    () => Persistence.FromByteArray<ScalableBloomFilter>(PokeUInt32(bytes, GrowthOffset, growth)),
                    "growth factor");
            }

            // The largest the constructor accepts reads back.
            Assert.AreEqual(uint.MaxValue, Persistence.FromByteArray<ScalableBloomFilter>(
                PokeUInt32(bytes, GrowthOffset, uint.MaxValue)).Growth);
        }

        /// <summary>
        /// The cuckoo filter's bucket count sizes its whole fingerprint array.
        /// </summary>
        /// <remarks>
        /// A power of two, so that it is the size limit that refuses it. A count that is
        /// not one is refused for that before its size is looked at, and this test would
        /// pass on the wrong guard.
        /// </remarks>
        [TestMethod]
        public void TestAbsurdCuckooBucketCountIsRefused()
        {
            var bytes = new CuckooBloomFilter(1000, 0.01).ToByteArray();

            AssertRefused(
                () => Persistence.FromByteArray<CuckooBloomFilter>(
                    PokeUInt32(bytes, 0, 1u << 30)),
                "beyond anything this library builds");
        }

        /// <summary>
        /// Where a cuckoo payload's header fields and first entry begin, within the
        /// payload: the bucket count, entries per bucket, fingerprint width, item count and
        /// capacity, then the number of occupied entries, then the entries.
        /// </summary>
        private const int CuckooItemCountOffset = 12;
        private const int CuckooOccupiedOffset = 20;
        private const int CuckooFirstEntryOffset = 24;

        /// <summary>
        /// A cuckoo filter of five items in distinct places, written, with the layout
        /// the tests below edit checked against it so that an edit lands where it says.
        /// </summary>
        private static byte[] CuckooWithFiveItems()
        {
            var filter = new CuckooBloomFilter(1000, 0.01, seed: 1);
            for (var i = 0; i < 5; i++)
            {
                Assert.IsTrue(filter.Add(Key($"c{i}")));
            }
            var bytes = filter.ToByteArray();

            Assert.AreEqual(5u, BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(PayloadStart + CuckooItemCountOffset)), "the item count is not where this test expects it");
            Assert.AreEqual(5u, BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(PayloadStart + CuckooOccupiedOffset)), "the occupied entries are not where this test expects them");
            return bytes;
        }

        /// <summary>
        /// The bytes one entry occupies in a cuckoo payload: its bucket and entry
        /// indexes, then its fingerprint in the fewest whole bytes that hold the width.
        /// </summary>
        private static int CuckooEntryLength(byte[] bytes)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(PayloadStart + 8));
            return 8 + (int)((bits + 7) / 8);
        }

        /// <summary>
        /// A cuckoo filter finds an item's other bucket by combining the first with a hash
        /// of its fingerprint and reducing the result by the bucket count. The two
        /// agree about where an item lives only when that count is a power of two, which is
        /// why the constructor rounds up to one. With any other, an insert that relocates
        /// an entry can leave it where no lookup looks, and the filter then loses an item
        /// it accepted. The writer never produces one, so the reader refuses it.
        /// </summary>
        /// <remarks>
        /// The filter holds nothing, so what refuses the payload is the count and nothing
        /// beside it. The counts straddle the powers of two they are nearest, and the
        /// powers of two read, the smallest included, so the refusal is not a blanket one.
        /// </remarks>
        [TestMethod]
        public void TestACuckooBucketCountThatIsNotAPowerOfTwoIsRefused()
        {
            var bytes = new CuckooBloomFilter(1000, 0.01).ToByteArray();
            Assert.AreEqual(512u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(PayloadStart)),
                "the bucket count is not where this test expects it");

            foreach (var buckets in new uint[] { 3, 5, 6, 7, 100, 511, 513, 1000, 1023, 1025 })
            {
                AssertRefused(
                    () => Persistence.FromByteArray<CuckooBloomFilter>(PokeUInt32(bytes, 0, buckets)),
                    "is not a power of two");
            }

            foreach (var buckets in new uint[] { 1, 2, 4, 256, 512, 1024 })
            {
                Assert.AreEqual(buckets, Persistence.FromByteArray<CuckooBloomFilter>(
                    PokeUInt32(bytes, 0, buckets)).BucketCount());
            }
        }

        /// <summary>
        /// A cuckoo filter holds one entry for each item, so the count a payload carries is
        /// the number of entries it lists. A count below it lets a removal take the count
        /// under zero, where it wraps to 4,294,967,295; a count of zero over entries lets
        /// the hash be replaced under them, which an occupied filter refuses. A count above
        /// it claims items the filter does not hold. The writer produces neither.
        /// </summary>
        [TestMethod]
        public void TestACuckooItemCountThatDisagreesWithItsEntriesIsRefused()
        {
            var bytes = CuckooWithFiveItems();

            foreach (var count in new uint[] { 0, 1, 4, 6, 7, uint.MaxValue })
            {
                AssertRefused(
                    () => Persistence.FromByteArray<CuckooBloomFilter>(
                        PokeUInt32(bytes, CuckooItemCountOffset, count)),
                    $"item count is {count}");
            }

            Assert.AreEqual(5u, Persistence.FromByteArray<CuckooBloomFilter>(bytes).Count());
        }

        /// <summary>
        /// A payload that lists the same bucket and entry twice is accepted today, and
        /// the two fingerprints are OR-ed into one cell. The count is compared with the
        /// entries filled rather than the entries listed, so the repeat cannot hide in
        /// it, and the repeat is refused for itself. The second case is the one only that
        /// refusal sees: a count that agrees with the cells filled, four, while five
        /// entries are listed.
        /// </summary>
        [TestMethod]
        public void TestACuckooPayloadThatListsAnEntryTwiceIsRefused()
        {
            var bytes = CuckooWithFiveItems();
            var second = CuckooFirstEntryOffset + CuckooEntryLength(bytes);

            var bucket = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(PayloadStart + CuckooFirstEntryOffset));
            var entry = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(PayloadStart + CuckooFirstEntryOffset + 4));
            var repeated = PokeUInt32(PokeUInt32(bytes, second, bucket), second + 4, entry);

            AssertRefused(() => Persistence.FromByteArray<CuckooBloomFilter>(repeated), "named more than once");
            AssertRefused(
                () => Persistence.FromByteArray<CuckooBloomFilter>(PokeUInt32(repeated, CuckooItemCountOffset, 4)),
                "named more than once");
        }

        /// <summary>
        /// TopK's k sizes its heap when it is built. One past the most a heap can hold
        /// is refused; anything up to that is a size the constructor builds, and the
        /// reader sizes its heap by what the payload holds rather than by k.
        /// </summary>
        [TestMethod]
        public void TestAbsurdTopKSizeIsRefused()
        {
            var t = new TopK(0.1, 0.5, 3);
            for (int i = 0; i < 40; i++) t.Add(Key($"w{i % 5}"));

            AssertRefused(
                () => Persistence.FromByteArray<TopK>(
                    PokeUInt32(t.ToByteArray(), 0, ElementHeap.MaxCapacity + 1u)),
                "elements");
        }

        /// <summary>
        /// A signature's length is both its allocation and the number of hash functions
        /// it claims were used, so zero is as wrong as absurdly large. Absurdly large is
        /// longer than an array, which is the most the factory builds.
        /// </summary>
        [TestMethod]
        public void TestAbsurdSignatureLengthIsRefused()
        {
            var bytes = MinHash.Signature(new[] { "a", "b", "c" }, 8).ToByteArray();

            AssertRefused(
                () => Persistence.FromByteArray<MinHashSignature>(
                    PokeUInt32(bytes, 0, (uint)MinHashSignature.MaxValues + 1)),
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
        /// A Memento filter holding keys at the given positions of one block and nothing else,
        /// in a range of 64 with 8-bit fingerprints, so its one table of 16 slots holds one
        /// box, in the only run there is, starting at the box's own quotient. The block is the
        /// first whose quotient is the table's first slot, or its last, which are the two ends
        /// of the reader's walk over the table; at the last the run wraps round to the first.
        /// Returns the payload, the slots of the run in order, for a test to edit one of, and
        /// the block's first key.
        /// </summary>
        private static (byte[] Payload, uint[] Run, ulong FirstKey) OneMementoBox(bool lastSlot, params ulong[] mementos)
        {
            var filter = new MementoFilter(64, 8, initialCapacity: 8);
            var table = filter.Segments[0];
            var target = lastSlot ? table.Slots - 1 : 0;
            var block = 0UL;
            while (table.Split(MementoFilter.HashPrefix(block)).Quotient != target)
            {
                block++;
            }

            foreach (var memento in mementos)
            {
                filter.Add((block * 64) + memento);
            }

            Assert.HasCount(1, filter.Segments);
            Assert.AreSame(table, filter.Segments[0]);
            var fields = table.ReadRun(target);
            var boxes = table.Codec.Decode(fields);
            Assert.HasCount(1, boxes, "the keys are not one box");
            CollectionAssert.AreEqual(mementos, boxes[0].Mementos.ToArray(), "the box does not hold the keys");
            Assert.AreEqual(fields[0], table.ReadField(target), "the run does not start at its quotient");

            var run = Enumerable.Range(0, fields.Count).Select(k => (target + (uint)k) & (table.Slots - 1)).ToArray();
            return (filter.ToByteArray(), run, block * 64);
        }

        /// <summary>
        /// Overwrites bits of one slot's field in the first table of a Memento payload with a
        /// range of 64 and 8-bit fingerprints, and repairs the checksum. A slot there is 18
        /// bits, the three of metadata below the field, and the table's words follow the
        /// filter's 20 bytes and the table's 16, laid end to end from the low bit up.
        /// </summary>
        private static byte[] PokeMementoField(byte[] original, uint slot, int fromBit, int bits, ulong value)
        {
            var bytes = (byte[])original.Clone();
            var start = ((long)slot * MementoSegment.SlotBits(8, 6)) + 3 + fromBit;
            for (var i = 0; i < bits; i++)
            {
                var at = start + i;
                var index = PayloadStart + 36 + (int)(at / 8);
                var mask = (byte)(1 << (int)(at % 8));
                bytes[index] = ((value >> i) & 1) == 1 ? (byte)(bytes[index] | mask) : (byte)(bytes[index] & ~mask);
            }
            RepairChecksum(bytes);
            return bytes;
        }

        /// <summary>
        /// A box of three keys whose count, the low six bits of its third slot, claims 62 keys
        /// between its first and last where its run has bits for one. The reader handed the
        /// table over without decoding a run, so the payload read, and the first query to
        /// decode the box read zeros past the end of the run for the keys it lacked. It is
        /// refused when read, for the box, at the table's first slot and at its last.
        /// </summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void TestMementoFilterBoxClaimingMoreKeysThanItsRunHoldsIsRefused(bool lastSlot)
        {
            var (payload, run, first) = OneMementoBox(lastSlot, 0, 1, 2);
            Assert.HasCount(3, run);
            Assert.IsTrue(Persistence.FromByteArray<MementoFilter>(payload).TestRange(first, first + 2),
                "the unedited payload does not read");

            AssertRefused(
                () => Persistence.FromByteArray<MementoFilter>(PokeMementoField(payload, run[2], 0, 6, 62)),
                "more keys than its run holds");
        }

        /// <summary>
        /// A box of two keys whose second slot's fingerprint is edited to zero, the marker of a
        /// box of more than two, in a run that ends there. Its count would be read from past
        /// the end of the run, which gave zeros, so the payload read and the box decoded as two
        /// keys, silently. It is refused when read, at the table's first slot and at its last.
        /// </summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void TestMementoFilterBoxWhoseCountRunsPastItsRunIsRefused(bool lastSlot)
        {
            var (payload, run, first) = OneMementoBox(lastSlot, 0, 1);
            Assert.HasCount(2, run);
            Assert.IsTrue(Persistence.FromByteArray<MementoFilter>(payload).TestRange(first + 1, first + 1),
                "the unedited payload does not read");

            AssertRefused(
                () => Persistence.FromByteArray<MementoFilter>(PokeMementoField(payload, run[1], 6, 9, 0)),
                "past the end of its run");
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
        /// A table with more bits of address than a slot count holds could not have been
        /// built by any insertion. This used to be refused only because its word count,
        /// worked out from a slot count that had wrapped, disagreed; it is the width that
        /// is refused now. (An address and a fingerprint together outrunning the hash
        /// cannot be reached past this: 31 and 32 bits are 63.)
        /// </summary>
        [TestMethod]
        public void TestInfiniFilterWithMoreAddressBitsThanATableHoldsIsRefused()
        {
            var bytes = PokeUInt32(InfiniFilterPayload(), 16, 39);
            AssertRefused(
                () => Persistence.FromByteArray<InfiniFilter>(bytes),
                "bits of address");
        }

        /// <summary>
        /// A Grafite payload for the tests below to corrupt. Its scalar prefix is
        /// fixed, so field offsets are too: the reduced universe at 0, the hash
        /// multiplier at 8, its addend at 16, the key count at 24, the largest
        /// promised range at 32, the low-bit split at 40, the number of hash codes
        /// at 44, the bits of high parts in use at 48, the smallest and largest codes
        /// at 52 and 60, and the count of words of low bits at 68, which the low words
        /// follow.
        /// </summary>
        private static byte[] GrafitePayload()
        {
            var keys = Enumerable.Range(0, 60).Select(i => (ulong)(i * 37));
            return Grafite.Build(keys, 0.05, 8, seed: 11).ToByteArray();
        }

        private const int GrafiteLowBitsOffset = 40;
        private const int GrafiteCodeCountOffset = 44;
        private const int GrafiteHighBitCountOffset = 48;
        private const int GrafiteFirstCodeOffset = 52;
        private const int GrafiteLastCodeOffset = 60;
        private const int GrafiteLowWordsOffset = 68;

        /// <summary>
        /// A Grafite payload of no keys, which Build leaves without a split, without bits of
        /// high parts and without a word of either array.
        /// </summary>
        private static byte[] EmptyGrafitePayload() =>
            Grafite.Build(Array.Empty<ulong>(), 0.01, 16).ToByteArray();

        /// <summary>
        /// The payload with the number of words in its low bits and in its high bits
        /// changed, each array cut short or padded with zero words to match its new count,
        /// and sealed. Of everything the payload says, only those counts then disagree with
        /// the codes it holds.
        /// </summary>
        private static byte[] GrafiteWithWords(byte[] original, int lowWordsDelta, int highWordsDelta)
        {
            var payload = original.AsSpan(PayloadStart, original.Length - PayloadStart - 4);
            var lowWords = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                payload.Slice(GrafiteLowWordsOffset));
            var lows = payload.Slice(GrafiteLowWordsOffset + 4, lowWords * 8);
            var highWordsOffset = GrafiteLowWordsOffset + 4 + (lowWords * 8);
            var highWords = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                payload.Slice(highWordsOffset));
            var highs = payload.Slice(highWordsOffset + 4, highWords * 8);

            using var rewritten = new MemoryStream();
            rewritten.Write(payload.Slice(0, GrafiteLowWordsOffset));
            WriteWords(rewritten, lows, lowWords + lowWordsDelta);
            WriteWords(rewritten, highs, highWords + highWordsDelta);

            using var sealedPayload = new MemoryStream();
            PersistenceFormat.Write(
                sealedPayload, StructureId.Grafite, HashId.None, rewritten.ToArray());
            return sealedPayload.ToArray();
        }

        /// <summary>A count of words, and that many of them: the given ones, cut or padded with zeros.</summary>
        private static void WriteWords(Stream to, ReadOnlySpan<byte> words, int count)
        {
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)count);
            to.Write(header);

            var kept = Math.Min(words.Length, count * 8);
            to.Write(words.Slice(0, kept));
            to.Write(new byte[(count * 8) - kept]);
        }

        /// <summary>
        /// Asserts that the payload as written reads and that the edited one is refused for
        /// the stated reason, so that the edit is what is refused and not the payload.
        /// </summary>
        private static void AssertGrafiteEditRefused(byte[] original, byte[] edited, string expected)
        {
            Assert.IsNotNull(Persistence.FromByteArray<Grafite>(original),
                "the payload before the edit should read");
            AssertRefused(() => Persistence.FromByteArray<Grafite>(edited), expected);
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
        /// The split is a u32 in the payload and an int in the filter, and a u32 from 2^31
        /// up is negative once it is cast. The reader cast first and then checked that the
        /// split is no wider than a code, which a negative number is not, so the payload
        /// loaded and the filter answered false for keys it holds.
        /// </summary>
        [TestMethod]
        [DataRow(0x8000_0000u)]
        [DataRow(0xFFFF_FFFFu)]
        public void TestGrafiteWithALowBitCountPastAnIntIsRefused(uint lowBits)
        {
            var bytes = PokeUInt32(GrafitePayload(), GrafiteLowBitsOffset, lowBits);
            AssertGrafiteEditRefused(GrafitePayload(), bytes, "sixty-four bits wide");
        }

        /// <summary>
        /// The count of bits of high parts is cast the same way, and the check that it
        /// fits the words behind it was a comparison a negative number passes.
        /// </summary>
        [TestMethod]
        [DataRow(0x8000_0000u)]
        [DataRow(0xFFFF_FFFFu)]
        public void TestGrafiteWithAHighBitCountPastAnIntIsRefused(uint highBitCount)
        {
            var bytes = PokeUInt32(GrafitePayload(), GrafiteHighBitCountOffset, highBitCount);
            AssertGrafiteEditRefused(GrafitePayload(), bytes, "a filter can address");
        }

        /// <summary>
        /// The most bits of high parts a filter can address, 2^31 - 1, is a count the
        /// builder writes at its edge, and the reader takes it: a payload whose single code
        /// gives exactly that many is refused only for the words it does not hold, which are
        /// the 33,554,432 that many bits fill. (The code ends at 2^32 - 6 and is split at one
        /// bit, so its high part is 2^31 - 3, plus the code, plus one.) A count past it is
        /// refused by the tests above. Structural: a filter of that many bits is gigabytes
        /// of words, so the payload claims them and does not carry them.
        /// </summary>
        [TestMethod]
        public void TestGrafiteAtTheMostBitsOfHighPartsIsRefusedOnlyForTheWordsItLacks()
        {
            const int HighWordsOffset = GrafiteLowWordsOffset + 4 + (2 * 8);
            const ulong LastCode = (1UL << 32) - 6;

            var original = Grafite.Build(new ulong[] { 5 }, 0.5, 1, seed: 1).ToByteArray();
            var filter = Persistence.FromByteArray<Grafite>(original);
            Assert.AreEqual(1, filter.CodeCount);
            Assert.AreEqual(1, filter.LowBits, "the code above ends at a high part of 2^31 - 3");
            Assert.AreEqual(2, filter.Lows.Length, "the words the one code gives its low bits");

            // The universe, which the code is below, and the smallest and largest codes
            // (the same code).
            var edge = PokeUInt64(original, 0, 1UL << 33);
            edge = PokeUInt64(edge, GrafiteFirstCodeOffset, LastCode);
            edge = PokeUInt64(edge, GrafiteLastCodeOffset, LastCode);
            edge = PokeUInt32(edge, GrafiteHighBitCountOffset, int.MaxValue);
            edge = PokeUInt32(edge, HighWordsOffset, Grafite.MaxHighWords);

            AssertRefused(() => Persistence.FromByteArray<Grafite>(edge), "bytes remain");
        }

        /// <summary>
        /// A count of bits of high parts that is possible, and that the words behind it can
        /// hold, but that the codes do not give: the builder sets it to the largest high
        /// part, plus the number of codes, plus one, and nothing else is the right number.
        /// The claim stays within the words the payload carries, so it is the codes that
        /// refuse it and not the words.
        /// </summary>
        [TestMethod]
        [DataRow(-1)]
        [DataRow(1)]
        public void TestGrafiteWhoseHighBitCountDisagreesWithItsCodesIsRefused(int offBy)
        {
            var original = GrafitePayload();
            var filter = Persistence.FromByteArray<Grafite>(original);
            var claimed = filter.HighBitCount + offBy;
            Assert.IsTrue(
                claimed > (filter.Highs.Length - 1) * 64 && claimed <= filter.Highs.Length * 64,
                $"{claimed} bits should fit the {filter.Highs.Length} words the payload has, " +
                "and not the one fewer");

            var bytes = PokeUInt32(original, GrafiteHighBitCountOffset, (uint)claimed);
            AssertGrafiteEditRefused(original, bytes, "take exactly");
        }

        /// <summary>
        /// A split that is possible and that the codes do not give. The payload is one of
        /// seven bits: one fewer and one more are as plausible as it, and 60 is the widest
        /// any filter is split at. None of them was refused, and probed against the keys
        /// the filter holds, one more and 60 answered false for every one.
        /// </summary>
        [TestMethod]
        [DataRow(6u)]
        [DataRow(8u)]
        [DataRow(60u)]
        public void TestGrafiteWhoseSplitDisagreesWithItsCodesIsRefused(uint lowBits)
        {
            var original = GrafitePayload();
            Assert.AreEqual(7, Persistence.FromByteArray<Grafite>(original).LowBits,
                "the rows above are the neighbours of this payload's split");

            var bytes = PokeUInt32(original, GrafiteLowBitsOffset, lowBits);
            AssertGrafiteEditRefused(original, bytes, "take exactly");
        }

        /// <summary>
        /// A split wider than a builder ever makes. It splits at the log of the universe
        /// over the codes, and the universe is below 2^61, so no filter has more than 60
        /// low bits. A split of 61 to 64 is not wider than a code, which the limit before
        /// it allowed, and is read by a query as a shift that is not the one that was
        /// meant: C# masks a shift of 64 to a shift of none, so a code is "split" at 64 by
        /// being left whole.
        /// </summary>
        [TestMethod]
        [DataRow(61u)]
        [DataRow(62u)]
        [DataRow(63u)]
        [DataRow(64u)]
        public void TestGrafiteWithASplitPastWhatABuilderMakesIsRefused(uint lowBits)
        {
            var bytes = PokeUInt32(GrafitePayload(), GrafiteLowBitsOffset, lowBits);
            AssertGrafiteEditRefused(
                GrafitePayload(), bytes, "beyond anything this library builds");
        }

        /// <summary>
        /// One code fewer than the filter holds is a count the keys allow, and it leaves a
        /// code at the end of the encoding that the filter no longer knows is there: a key
        /// the filter holds is answered false.
        /// </summary>
        [TestMethod]
        public void TestGrafiteClaimingOneCodeFewerThanItHoldsIsRefused()
        {
            var original = GrafitePayload();
            var codes = Persistence.FromByteArray<Grafite>(original).CodeCount;

            var bytes = PokeUInt32(original, GrafiteCodeCountOffset, (uint)(codes - 1));
            AssertGrafiteEditRefused(original, bytes, "take exactly");
        }

        /// <summary>
        /// The filter of <see cref="GrafitePayload"/> re-encoded at a split of 64 bits, a
        /// payload no builder writes and that agrees with itself in every field: each code
        /// is its own low part, every high part is zero (so the bit count is a one for each
        /// code and the one the encoding ends on), and the arrays are the sizes the codes
        /// give them. Of the sixty codes the high bits fit one word.
        /// </summary>
        private static byte[] GrafiteSplitAtSixtyFour()
        {
            var source = Persistence.FromByteArray<Grafite>(GrafitePayload());
            var codes = source.CodeCount;
            Assert.IsLessThan(64, codes, "the high bits of the codes should fit one word");

            var payload = new PayloadWriter();
            payload.WriteUInt64(source.R);
            payload.WriteUInt64(source.C1);
            payload.WriteUInt64(source.C2);
            payload.WriteUInt64(source.KeyCount);
            payload.WriteUInt64(source.MaxRange);
            payload.WriteUInt32(64);
            payload.WriteUInt32((uint)codes);
            payload.WriteUInt32((uint)codes + 1);
            payload.WriteUInt64(source.FirstCode);
            payload.WriteUInt64(source.LastCode);

            payload.WriteUInt32((uint)codes + 2);
            for (var i = 0; i < codes; i++)
            {
                payload.WriteUInt64(source.CodeAt(i));
            }
            payload.WriteUInt64(0);
            payload.WriteUInt64(0);

            payload.WriteUInt32(1);
            payload.WriteUInt64((1UL << codes) - 1);

            using var stream = new MemoryStream();
            PersistenceFormat.Write(stream, StructureId.Grafite, HashId.None, payload.WrittenSpan);
            return stream.ToArray();
        }

        /// <summary>
        /// The one split past a builder's that a payload can agree with itself at, and the
        /// one that is wrong to read: a query shifts a code by the split to find its high
        /// part, and C# shifts a 64-bit value by a count of 64 as it shifts it by none, so
        /// every code is taken for its own high part. The earlier limit, a split no wider
        /// than a code, allowed it.
        /// </summary>
        [TestMethod]
        public void TestGrafiteWhoseEveryFieldAgreesAtASplitOfSixtyFourIsRefused()
        {
            var bytes = GrafiteSplitAtSixtyFour();
            AssertRefused(
                () => Persistence.FromByteArray<Grafite>(bytes), "beyond anything this library builds");
        }

        /// <summary>
        /// The smallest code is recorded beside the encoding, and a query turns away
        /// anything below it without looking at the encoding at all. A smallest code that
        /// is not the encoding's first is a filter that answers false for the keys whose
        /// codes lie between the two (one above and a hundred above lost one and three of
        /// the sixty), or that records a code it does not hold (one below).
        /// </summary>
        [TestMethod]
        [DataRow(-1L)]
        [DataRow(1L)]
        [DataRow(100L)]
        public void TestGrafiteWhoseSmallestCodeIsNotTheEncodingsFirstIsRefused(long offBy)
        {
            var original = GrafitePayload();
            var first = Persistence.FromByteArray<Grafite>(original).FirstCode;

            var bytes = PokeUInt64(original, GrafiteFirstCodeOffset, (ulong)((long)first + offBy));
            AssertGrafiteEditRefused(original, bytes, "records its smallest code as");
        }

        /// <summary>
        /// The extreme of the same edit: the smallest code recorded as the largest, which
        /// no longer exceeds it, so the order check passes, and which turns away every
        /// code but one (59 of the sixty keys answered false).
        /// </summary>
        [TestMethod]
        public void TestGrafiteWhoseSmallestCodeIsItsLargestIsRefused()
        {
            var original = GrafitePayload();
            var last = Persistence.FromByteArray<Grafite>(original).LastCode;

            var bytes = PokeUInt64(original, GrafiteFirstCodeOffset, last);
            AssertGrafiteEditRefused(original, bytes, "records its smallest code as");
        }

        /// <summary>
        /// And the largest code, which a query also checks first. One below the
        /// encoding's last turns away the key whose code is the last (one of the sixty
        /// answered false), and one above records a code the filter does not hold. The
        /// edits stay within the high part the bit count is worked from, so it is the
        /// recorded code that refuses them and not the count.
        /// </summary>
        [TestMethod]
        [DataRow(-1L)]
        [DataRow(1L)]
        public void TestGrafiteWhoseLargestCodeIsNotTheEncodingsLastIsRefused(long offBy)
        {
            var original = GrafitePayload();
            var filter = Persistence.FromByteArray<Grafite>(original);
            var claimed = (ulong)((long)filter.LastCode + offBy);
            Assert.AreEqual(
                filter.LastCode >> filter.LowBits, claimed >> filter.LowBits,
                "the edit should leave the high part, which the bit count follows, as it was");

            var bytes = PokeUInt64(original, GrafiteLastCodeOffset, claimed);
            AssertGrafiteEditRefused(original, bytes, "records its largest code as");
        }

        /// <summary>
        /// The words of low bits are as many as the codes at their width need, as the
        /// builder sizes them. A count of one more or one fewer, with the array to match,
        /// was read as the filter it was not.
        /// </summary>
        [TestMethod]
        [DataRow(-1)]
        [DataRow(1)]
        public void TestGrafiteWithTheWrongNumberOfWordsOfLowBitsIsRefused(int offBy)
        {
            var original = GrafitePayload();
            AssertGrafiteEditRefused(
                original, GrafiteWithWords(original, offBy, 0), "words of low bits, but");
        }

        /// <summary>
        /// The words of high bits are as many as hold the bits in use. One more, with the
        /// array to match, was read as the filter it was not; one fewer was refused, but
        /// for the bits that the words then fell short of and not for the count.
        /// </summary>
        [TestMethod]
        [DataRow(-1)]
        [DataRow(1)]
        public void TestGrafiteWithTheWrongNumberOfWordsOfHighBitsIsRefused(int offBy)
        {
            var original = GrafitePayload();
            AssertGrafiteEditRefused(
                original, GrafiteWithWords(original, 0, offBy), "words of high bits, but");
        }

        /// <summary>
        /// A filter of no keys has nothing to split or to count: the builder leaves the
        /// split and the bits of high parts at nothing, and a payload that gives either a
        /// value describes an encoding the filter does not have. A split of five bits was
        /// read, on a filter that holds no codes to split.
        /// </summary>
        [TestMethod]
        [DataRow(GrafiteLowBitsOffset, 5u)]
        [DataRow(GrafiteHighBitCountOffset, 1u)]
        public void TestAnEmptyGrafiteThatClaimsAnEncodingIsRefused(int offset, uint value)
        {
            var original = EmptyGrafitePayload();
            AssertGrafiteEditRefused(
                original, PokeUInt32(original, offset, value), "holds no hash codes");
        }

        /// <summary>
        /// And it has no words of either array. The sizes the builder gives a filter of
        /// codes, two words of low bits at the least and one of high bits, do not apply to
        /// a filter of none, which has neither.
        /// </summary>
        [TestMethod]
        [DataRow(1, 0, "words of low bits, but")]
        [DataRow(0, 1, "words of high bits, but")]
        public void TestAnEmptyGrafiteThatCarriesWordsIsRefused(
            int lowWords, int highWords, string expected)
        {
            var original = EmptyGrafitePayload();
            AssertGrafiteEditRefused(
                original, GrafiteWithWords(original, lowWords, highWords), expected);
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
        /// An exact sample holds everything it has seen, so one holding more items
        /// than it claims to have seen describes items that were never added. It is
        /// told apart from the opposite case -- fewer held than seen, which a 6.2.0
        /// merge could write and which a stored fixture pins -- because only that one
        /// has a history worth explaining.
        /// </summary>
        [TestMethod]
        public void TestExactVarOptHoldingMoreThanItHasSeenIsRefused()
        {
            var sample = new VarOpt(5, seed: 7);
            for (var i = 0; i < 4; i++)
            {
                sample.Add(Key($"item-{i}"), 1.0 + i);
            }
            var bytes = PokeUInt64(sample.ToByteArray(), 12, 2);
            AssertRefused(
                () => Persistence.FromByteArray<VarOpt>(bytes),
                "more than it has seen");
        }

        /// <summary>
        /// The other side of the same boundary, at its nearest point: an exact sample
        /// claiming one item more than it holds. This is the shape 6.2.0's merge wrote
        /// -- the issue's own case held eight having seen nine -- and the refusal says
        /// so.
        /// </summary>
        [TestMethod]
        public void TestExactVarOptHoldingOneFewerThanItHasSeenIsRefused()
        {
            var sample = new VarOpt(5, seed: 7);
            for (var i = 0; i < 4; i++)
            {
                sample.Add(Key($"item-{i}"), 1.0 + i);
            }
            var bytes = PokeUInt64(sample.ToByteArray(), 12, 5);
            AssertRefused(
                () => Persistence.FromByteArray<VarOpt>(bytes), "Version 6.2.0");
        }

        /// <summary>
        /// Once sampling, the items held at their own weights are a heap, and the
        /// reader orders them rather than trusting the payload to: an insertion into
        /// a heap that is not one contests the wrong item. The payload here is
        /// written from a sample whose heap was disordered first, so the writer's own
        /// checksum covers it.
        /// </summary>
        [TestMethod]
        public void TestVarOptSamplingHeapOutOfOrderIsReorderedOnRead()
        {
            var sample = new VarOpt(8, seed: 7);
            for (var i = 0; i < 6; i++)
            {
                sample.Add(Key($"heavy-{i}"), 100.0 * (i + 1));
            }
            for (var i = 0; i < 60; i++)
            {
                sample.Add(Key($"light-{i}"), 1.0);
            }
            Assert.IsTrue(sample.LightCount > 0 && sample.HeavyCount >= 2 &&
                sample.HeavyWeights[0] < sample.HeavyWeights[1],
                "Premise: sampling, with a strictly lighter root to swap out of place.");
            (sample.HeavyItems[0], sample.HeavyItems[1]) =
                (sample.HeavyItems[1], sample.HeavyItems[0]);
            (sample.HeavyWeights[0], sample.HeavyWeights[1]) =
                (sample.HeavyWeights[1], sample.HeavyWeights[0]);

            var restored = Persistence.FromByteArray<VarOpt>(sample.ToByteArray());

            for (var i = 1; i < restored.HeavyCount; i++)
            {
                Assert.IsTrue(
                    restored.HeavyWeights[(i - 1) / 2] <= restored.HeavyWeights[i],
                    $"The restored heap is out of order at slot {i}.");
            }
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
            // u32 k, u32 width, u32 depth, u8 placement, f64 decay, u64 generator, u64 n,
            // 37 bytes; 2 x 16 fingerprints of 2 bytes and counters of 8, 320 more; then
            // the element count at 357.
            var bytes = PokeUInt32(HeavyKeeperPayload(), 357, 200);
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
            // u32 k, u32 width, u32 depth, the placement byte, then f64 decay at 13.
            var bytes = PokeUInt64(HeavyKeeperPayload(), 13,
                BitConverter.DoubleToUInt64Bits(1.0));
            AssertRefused(
                () => Persistence.FromByteArray<HeavyKeeper>(bytes), "decay base");
        }

        /// <summary>
        /// A width whose buckets the payload does not hold is refused before a single
        /// array is allocated for it. Two billion buckets is a structure the constructor
        /// builds, so the size itself is not refused; a few hundred bytes claiming them
        /// is.
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
        /// Where the three totals sit in <see cref="SublimePayload"/>: the count at 32, the
        /// count at which the rows next double at 40, and the one at which they fold back
        /// at 48. That sketch is 128 counters wide at a size factor of 1 and a growth of
        /// 0.5, so it doubles at 128 squared, 16,384 keys, and holds 6,000.
        /// </summary>
        private const int SublimeCountAt = 32;
        private const int SublimeDoublingAt = 40;

        /// <summary>
        /// A sketch holds fewer keys than the count at which its rows next double, because
        /// the add that reaches that count doubles them and sets the next doubling past it.
        /// A payload holding as many or more is one no sketch wrote, and one more add to it
        /// went on from there: at <c>ulong.MaxValue</c> the count wrapped to nought, and
        /// <c>TotalCount</c>, <c>Epsilon</c> and the schedule of doublings with it. The edge
        /// is straddled: one key short of the doubling reads, and doubles on its next add.
        /// </summary>
        [TestMethod]
        public void TestSublimeCountMinSketchHoldingAsManyKeysAsItsDoublingIsRefused()
        {
            foreach (var count in new[] { 16_384UL, 16_385UL, ulong.MaxValue })
            {
                AssertRefused(
                    () => Persistence.FromByteArray<SublimeCountMinSketch>(
                        PokeUInt64(SublimePayload(), SublimeCountAt, count)),
                    "fewer keys than");
            }

            var below = Persistence.FromByteArray<SublimeCountMinSketch>(
                PokeUInt64(SublimePayload(), SublimeCountAt, 16_383));
            Assert.AreEqual(16_383UL, below.TotalCount());
            Assert.AreEqual(128, below.Width);
            below.Add(Key("one-more"));
            Assert.AreEqual(16_384UL, below.TotalCount());
            Assert.AreEqual(256, below.Width, "the add that reaches the doubling count doubles the rows");
        }

        /// <summary>
        /// The count at which rows of a given width double follows from the width, the size
        /// factor and the growth exponent: (width * size factor)^(1 / growth). A payload
        /// putting it past even the count at which rows twice as wide would double is one no
        /// sketch wrote, and the sketch it described did not grow when it should: at
        /// <c>ulong.MaxValue</c>, never. The edge is straddled at twice the width, 256
        /// squared, 65,536, the most a payload can say before it is refused. The check is
        /// that loose on purpose: the count is worked out with <c>Math.Pow</c>, whose last
        /// bit is not the same on every platform, and a payload written on one is read on
        /// another. A doubling is a factor of about two or more between the two counts, which
        /// no such difference spans.
        /// </summary>
        [TestMethod]
        public void TestSublimeCountMinSketchDoublingPastWhatItsWidthImpliesIsRefused()
        {
            foreach (var doubling in new[] { 65_537UL, 1UL << 40, ulong.MaxValue })
            {
                AssertRefused(
                    () => Persistence.FromByteArray<SublimeCountMinSketch>(
                        PokeUInt64(SublimePayload(), SublimeDoublingAt, doubling)),
                    "twice as wide");
            }

            var atTheEdge = Persistence.FromByteArray<SublimeCountMinSketch>(
                PokeUInt64(SublimePayload(), SublimeDoublingAt, 65_536));
            Assert.AreEqual(6_000UL, atTheEdge.TotalCount());
            Assert.AreEqual(128, atTheEdge.Width);
        }

        /// <summary>
        /// Where the count at which a Sublime sketch's rows fold back sits, and the number of
        /// records of earlier states after it.
        /// </summary>
        private const int SublimeFoldingAt = 48;
        private const int SublimeRecordsAt = 56;

        /// <summary>
        /// A sketch folds its rows back by the record of the doubling that widened them, so it
        /// holds a count at which they fold back only while it holds a record: nought before its
        /// first doubling and after its last folding, and set afresh by each. A payload claiming
        /// one with no record behind it was read, and written back with the claim in it, though
        /// no folding could happen at it. It is refused. Straddled: the same payload with the
        /// count at nought reads, and a sketch holding a record reads at its own count.
        /// </summary>
        /// <remarks>
        /// 6.2.0 wrote such a payload, measured by hand: its removal took the record off before
        /// it allocated the folded rows, so a folding that ran out of memory left a sketch of
        /// one record with none and its folding count where it was, which it wrote, and which
        /// both it and the reader before this read. Rows of 2^22, the record of rows of 2^21,
        /// under a heap limit of 1 GiB; the payload, 160 MiB, is too large to keep as a fixture.
        /// </remarks>
        [TestMethod]
        public void TestSublimeCountMinSketchFoldingBackWithNoRecordIsRefused()
        {
            var sketch = new SublimeCountMinSketch(0.02);
            for (var i = 0; i < 1_000; i++)
            {
                sketch.Add(Key($"flow-{i % 40}"));
            }
            var unrecorded = sketch.ToByteArray();
            Assert.AreEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(
                unrecorded.AsSpan(PayloadStart + SublimeRecordsAt)), "the sketch doubled");
            Assert.AreEqual(0UL, BinaryPrimitives.ReadUInt64LittleEndian(
                unrecorded.AsSpan(PayloadStart + SublimeFoldingAt)));

            foreach (var folding in new[] { 1UL, 1_000UL, ulong.MaxValue })
            {
                AssertRefused(
                    () => Persistence.FromByteArray<SublimeCountMinSketch>(
                        PokeUInt64(unrecorded, SublimeFoldingAt, folding)),
                    "no record");
            }

            var atNought = Persistence.FromByteArray<SublimeCountMinSketch>(
                PokeUInt64(unrecorded, SublimeFoldingAt, 0));
            Assert.AreEqual(1_000UL, atNought.TotalCount());

            var recorded = SublimePayload();
            Assert.AreEqual(1u, BinaryPrimitives.ReadUInt32LittleEndian(
                recorded.AsSpan(PayloadStart + SublimeRecordsAt)), "the sketch did not double once");
            Assert.AreEqual(2_560UL, BinaryPrimitives.ReadUInt64LittleEndian(
                recorded.AsSpan(PayloadStart + SublimeFoldingAt)));
            var read = Persistence.FromByteArray<SublimeCountMinSketch>(recorded);
            Assert.AreEqual(128, read.Width);
            CollectionAssert.AreEqual(recorded, read.ToByteArray(), "a sketch holding a record reads back as it was");
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

            // Shape is width and depth (u32 each), the placement byte, rho and count (u64
            // each).
            const int CountersAt = 4 + 4 + 1 + 8 + 8;
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
        /// A private sketch's counters are checked against the bytes behind them at the
        /// edges of the check. The unedited sketch, 64 by 2, holds exactly its 128
        /// counters and reads; one column more claims 130, two more than it holds, and is
        /// refused for that.
        /// <para>
        /// And where 32 bits run out: a sketch four rows deep with its width edited to
        /// 2^30 + 1 claims 2^32 + 4 counters, which as a 32-bit product is 4, and four
        /// counters fit. Counted in 64 bits it is refused for the counters it lacks. Counted
        /// in 32 it is priced at 34 GB and refused for its budget instead, or under no
        /// budget would allocate a row of eight gigabytes, which is why it is read only
        /// under the default.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestAPrivateSketchClaimingMoreCountersThanItHoldsIsRefusedAtTheEdges()
        {
            // u32 width at 0.
            var narrow = new PrivateCountMinSketch(64, 2, 1.0, seed: 3).ToByteArray();
            Assert.AreEqual(64u, Persistence.FromByteArray<PrivateCountMinSketch>(narrow).Width,
                "the unedited sketch does not read, so the edit below is not the only lie.");

            AssertRefused(
                () => Persistence.FromByteArray<PrivateCountMinSketch>(PokeUInt32(narrow, 0, 65)),
                "counters, beyond what it can hold");

            // Four rows of 2^30 + 1: 2^32 + 4 counters, and 4 as a product of two u32.
            const uint Wraps = (1u << 30) + 1;
            var deep = new PrivateCountMinSketch(64, 4, 1.0, seed: 3).ToByteArray();

            AssertRefused(
                () => Persistence.FromByteArray<PrivateCountMinSketch>(PokeUInt32(deep, 0, Wraps)),
                "counters, beyond what it can hold");
        }

        /// <summary>
        /// A count-min sketch's counters are checked against the bytes behind them at the
        /// edges of the check, as a private sketch's are. The unedited sketch, 28 by 1,
        /// holds exactly its 28 counters and reads; one column more claims 29, one more
        /// than it holds, and one row more claims 56. Each is refused for that, where it
        /// used to be refused at the first missing counter, "Payload ended", after
        /// allocating the row it was in.
        /// <para>
        /// And where 32 bits run out: a sketch four rows deep with its width edited to
        /// 2^30 + 1 claims 2^32 + 4 counters, which as a 32-bit product is 4, and four
        /// counters fit. Counted in 64 bits it is refused for the counters it lacks. Counted
        /// in 32 it is priced at 34 GB and refused for its budget instead, or under no
        /// budget would allocate a row of eight gigabytes, which is why it is read only
        /// under the default.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestACountMinSketchClaimingMoreCountersThanItHoldsIsRefusedAtTheEdges()
        {
            // f64 epsilon, f64 delta, u32 width at 16, u32 depth at 20. Delta 0.5 is one row.
            var narrow = new CountMinSketch(0.1, 0.5).ToByteArray();
            var read = Persistence.FromByteArray<CountMinSketch>(narrow);
            Assert.AreEqual((28u, 1u), (read.Width, read.Depth),
                "the unedited sketch does not read as 28 by 1, so the edits below are not the only lie.");

            AssertRefused(
                () => Persistence.FromByteArray<CountMinSketch>(PokeUInt32(narrow, 16, 29)),
                "counters, beyond what it can hold");
            AssertRefused(
                () => Persistence.FromByteArray<CountMinSketch>(PokeUInt32(narrow, 20, 2)),
                "counters, beyond what it can hold");

            // Four rows of 2^30 + 1: 2^32 + 4 counters, and 4 as a product of two u32.
            const uint Wraps = (1u << 30) + 1;
            var deep = new CountMinSketch(0.1, 0.02).ToByteArray();
            Assert.AreEqual(4u, Persistence.FromByteArray<CountMinSketch>(deep).Depth,
                "delta 0.02 is not four rows, so the width below does not wrap to four counters.");

            AssertRefused(
                () => Persistence.FromByteArray<CountMinSketch>(PokeUInt32(deep, 16, Wraps)),
                "counters, beyond what it can hold");
        }

        /// <summary>
        /// A Sublime sketch's counters likewise. Its width is a power of two, so the
        /// smallest edit to it doubles the row; and the check is made with the shape,
        /// before the totals and any records are read, so the bytes it holds the counters
        /// to are the current rows' and the 28 of the totals ahead of them. The unedited
        /// sketch, 64 by 1, reads; twice the width, or one row more, claims 128 counters
        /// where 64 and those 28 bytes are, and is refused for that, where it used to be
        /// refused at the first missing counter.
        /// <para>
        /// And where 32 bits run out: a sketch four rows deep with its width edited to 2^30,
        /// the widest it takes, claims 2^32 counters, which as a 32-bit product is none.
        /// Counted in 32 bits it is priced at 34 GB and refused for its budget instead, so
        /// it is read only under the default.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestASublimeSketchClaimingMoreCountersThanItHoldsIsRefusedAtTheEdges()
        {
            // Three f64, u32 depth at 24, u32 width at 28. Delta 0.5 is one row, and an
            // empty sketch has no records to constrain the width.
            var narrow = new SublimeCountMinSketch(0.5).ToByteArray();
            var read = Persistence.FromByteArray<SublimeCountMinSketch>(narrow);
            Assert.AreEqual((64, 1), (read.Width, read.Depth),
                "the unedited sketch does not read as 64 by 1, so the edits below are not the only lie.");

            AssertRefused(
                () => Persistence.FromByteArray<SublimeCountMinSketch>(PokeUInt32(narrow, 28, 128)),
                "counters, beyond what it can hold");
            AssertRefused(
                () => Persistence.FromByteArray<SublimeCountMinSketch>(PokeUInt32(narrow, 24, 2)),
                "counters, beyond what it can hold");

            // Four rows of 2^30: 2^32 counters, and none as a product of two u32.
            var deep = new SublimeCountMinSketch(0.02).ToByteArray();
            Assert.AreEqual(4, Persistence.FromByteArray<SublimeCountMinSketch>(deep).Depth,
                "delta 0.02 is not four rows, so the width below does not wrap to no counters.");

            AssertRefused(
                () => Persistence.FromByteArray<SublimeCountMinSketch>(
                    PokeUInt32(deep, 28, (uint)SublimeCountMinSketch.MaxWidth)),
                "counters, beyond what it can hold");
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
                    at += 25;  // width(4) depth(4) placement(1) rho(8) count(8)

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
        /// A window whose newest substream ends past the items the window says it has
        /// seen. Its start is moved one later, so it still follows the substream before
        /// it and holds what it held, and its last item lands one past the window's
        /// position: items from the future, which no stream put there.
        /// <para>
        /// The unedited payload is the control. Its newest substream ends exactly at the
        /// position, so a check one too strict refuses it, and with it every window saved
        /// part way through a substream.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestADpswWindowWithASubstreamPastItsItemsIsRefused()
        {
            var (bytes, layout) = SmallDpswPayload();
            var (start, held, position) = NewestDpswSubstream(bytes, layout);

            Assert.AreEqual(position, start + held - 1,
                "the newest substream does not end at the window's position, so moving its " +
                "start by one does not take it exactly one past.");
            Assert.AreEqual(position, Persistence.FromByteArray<DpswSketch>(bytes).Position,
                "the unedited payload does not read, so the edit below is not the only lie.");

            var poked = PokeUInt64(
                bytes, layout.SubstreamAt((int)layout.SubstreamCount - 1) - PayloadStart,
                (ulong)(start + 1));

            AssertRefused(
                () => Persistence.FromByteArray<DpswSketch>(poked),
                "the window says it has seen");
        }

        /// <summary>
        /// The same guard where its arithmetic runs out: a substream starting so near the
        /// largest position that its last item, start + held - 1, is past what a long
        /// holds. Such a sum wraps negative, and a check made with it passes, so the
        /// window loaded holding a substream from its future, which <c>Count</c> then
        /// passed over and the next <c>Add</c> expired, every counter with it.
        /// <para>
        /// Three starts straddle the wrap. At <c>long.MaxValue - held + 1</c> the sum is
        /// exactly <c>long.MaxValue</c> and does not wrap; one later it is one past and
        /// wraps; at <c>long.MaxValue</c> it wraps furthest. Each still follows the
        /// substream before it, so the ordering guard passes them all.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestADpswWindowWithASubstreamEndingPastTheLargestPositionIsRefused()
        {
            var (bytes, layout) = SmallDpswPayload();
            var (_, held, _) = NewestDpswSubstream(bytes, layout);

            Assert.IsGreaterThan(1, held,
                "the newest substream holds one item, so no start but the largest wraps " +
                "and the starts below do not straddle anything.");

            var at = layout.SubstreamAt((int)layout.SubstreamCount - 1) - PayloadStart;
            foreach (var start in new[]
            {
                long.MaxValue - held + 1,
                long.MaxValue - held + 2,
                long.MaxValue,
            })
            {
                var poked = PokeUInt64(bytes, at, (ulong)start);

                var ex = Assert.ThrowsExactly<InvalidDataException>(
                    () => Persistence.FromByteArray<DpswSketch>(poked),
                    $"a substream starting at {start} and holding {held} was read");
                StringAssert.Contains(ex.Message, "the window says it has seen",
                    $"a substream starting at {start} was refused for something else");
            }
        }

        /// <summary>
        /// A window at the largest position a long holds reads, since the format can hold
        /// it, and refuses the next add with an <see cref="InvalidOperationException"/>,
        /// unchanged, as a full DDSketch does. The add used to wrap the position to
        /// <c>long.MinValue</c>, and the window's own payload was then refused as having
        /// seen a negative number of items. Adds alone would take 2^63 - 1 items to get
        /// there, so the position is edited into a payload.
        /// <para>
        /// One short, the add is taken and brings the window to the largest position, so
        /// the refusal is not turning away a window with room; and that window, brought
        /// there by an add, refuses the next, unchanged, and still reads.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestADpswWindowAtTheLargestPositionRefusesAnAddAndChangesNothing()
        {
            var (bytes, _) = SmallDpswPayload();

            // window(8) rho(8) alpha(8) substreamSize(4) width(4) depth(4), then position(8).
            var full = Persistence.FromByteArray<DpswSketch>(PokeUInt64(bytes, 36, long.MaxValue));
            Assert.AreEqual(long.MaxValue, full.Position,
                "the window was not read at the position edited in, so the add below is not made there.");
            var before = full.ToByteArray();

            var ex = Assert.ThrowsExactly<InvalidOperationException>(
                () => full.Add(Key("one more")),
                "a window at the largest position took another item");
            StringAssert.Contains(ex.Message, "the most its position holds");
            CollectionAssert.AreEqual(before, full.ToByteArray(),
                "a refused add changed the window");

            var room = Persistence.FromByteArray<DpswSketch>(PokeUInt64(bytes, 36, long.MaxValue - 1));
            room.Add(Key("one more"));
            Assert.AreEqual(long.MaxValue, room.Position,
                "an add one short of the largest position did not reach it");

            var filled = room.ToByteArray();
            Assert.ThrowsExactly<InvalidOperationException>(
                () => room.Add(Key("and another")),
                "a window brought to the largest position by an add took another item");
            CollectionAssert.AreEqual(filled, room.ToByteArray(),
                "a refused add changed a window brought to the largest position by an add");
            Assert.AreEqual(long.MaxValue, Persistence.FromByteArray<DpswSketch>(filled).Position,
                "a window brought to the largest position by an add does not read back there");
        }

        /// <summary>
        /// The newest substream of a DPSW payload: where it starts, how many items it
        /// holds, and the window's position, read from the bytes.
        /// </summary>
        private static (long Start, int Held, long Position) NewestDpswSubstream(
            byte[] bytes, DpswLayout layout)
        {
            // window(8) rho(8) alpha(8) substreamSize(4) width(4) depth(4), then position(8).
            var position = (long)BinaryPrimitives.ReadUInt64LittleEndian(
                bytes.AsSpan(PayloadStart + 36));
            var at = layout.SubstreamAt((int)layout.SubstreamCount - 1);
            var start = (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at));
            var held = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 8));
            return (start, held, position);
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
            /// depth(4) placement(1) rho(8) count(8) and then its counters.
            /// </summary>
            internal int SubstreamAt(int index) =>
                PayloadStart + 48
                + (index * (12 + (PlanLength * (25 + ((int)(Width * Depth) * 8)))));
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
                () => Persistence.FromByteArray<CountingBloomFilter>(PokeUInt64(bytes, 0, 12_345)),
                "do not describe the same filter");
        }

        /// <summary>
        /// From version 14 a filter's size is a <c>u64</c>, and the 32-bit scheme is reduced
        /// modulo a 32-bit count of positions, which every payload placed with it recorded.
        /// A payload placed with 32-bit values among 2^32 positions is refused for that, before
        /// its buckets are read, and one among 2^32 - 1 is not: it is refused only for the
        /// buckets it does not have. So is one placed with 64-bit values among 2^32 more
        /// positions than its buckets, which a size compared in its low 32 bits would match.
        /// The read's price stops where its reader refuses it, at the placement byte, so under
        /// a budget too small for its buckets it is still refused for its placement.
        /// </summary>
        /// <remarks>
        /// Without the refusal, such a filter would reduce its 32-bit sums modulo the low 32
        /// bits of its size, which at 2^32 is a division by nought.
        /// </remarks>
        [TestMethod]
        public void TestAFilterPlacedWithThirtyTwoBitValuesPastTwoToTheThirtyTwoIsRefused()
        {
            const string Placed = "positions with 32-bit values";
            // The offsets of m and of the placement byte in each version 14 payload.
            var reads = new (string Name, byte[] Narrow, byte[] Wide, int Size, int Placement, Func<byte[], ReadOptions, object> ReadUnder)[]
            {
                (nameof(CountingBloomFilter),
                    Filled(new CountingBloomFilter(200, 4, 0.01)).ToByteArray(),
                    Filled(new CountingBloomFilter(200, 4, 0.01, null, Placement.Wide)).ToByteArray(),
                    0, 12, (b, o) => Persistence.FromByteArray<CountingBloomFilter>(b, o)),
                (nameof(DeletableBloomFilter),
                    Filled(new DeletableBloomFilter(200, 10, 0.01)).ToByteArray(),
                    Filled(new DeletableBloomFilter(200, 10, 0.01, null, Placement.Wide)).ToByteArray(),
                    0, 12, (b, o) => Persistence.FromByteArray<DeletableBloomFilter>(b, o)),
                (nameof(StableBloomFilter),
                    Filled(new StableBloomFilter(200, 2, 0.01, null, 1, Placement.Narrow)).ToByteArray(),
                    Filled(new StableBloomFilter(200, 2, 0.01, null, 1, Placement.Wide)).ToByteArray(),
                    0, 16, (b, o) => Persistence.FromByteArray<StableBloomFilter>(b, o)),
            };

            foreach (var (name, narrow, wide, size, placement, readUnder) in reads)
            {
                object read(byte[] b) => readUnder(b, ReadOptions.Unlimited);
                var placed = PokeUInt64(narrow, size, 1UL << 32);
                var ex = Assert.ThrowsExactly<InvalidDataException>(() => read(placed), name);
                StringAssert.Contains(ex.Message, Placed, $"{name} placed with 32-bit values among 2^32 positions");

                // The price stops where the reader does, at the placement byte, as it stops at
                // a byte that names no scheme: under a budget of nothing the two are refused
                // naming the same bytes, which a price that went on to the buckets would not.
                long Priced(byte[] b) => ((ReadBudgetExceededException)Assert.ThrowsExactly<InvalidDataException>(
                    () => readUnder(b, new ReadOptions(0)), name).InnerException!).RequiredBytes;
                Assert.AreEqual(Priced(PokeByte(narrow, placement, 0xFF)), Priced(placed),
                    $"{name} placed with 32-bit values among 2^32 positions was priced past its placement byte");
                var priced = Priced(placed);
                var under = Assert.ThrowsExactly<InvalidDataException>(() => readUnder(placed, new ReadOptions(priced)), name);
                StringAssert.Contains(under.Message, Placed, $"{name} under the {priced:N0} bytes it was priced at");

                var held = BinaryPrimitives.ReadUInt64LittleEndian(wide.AsSpan(PayloadStart + size));
                foreach (var (bytes, positions, what) in new[]
                {
                    (narrow, (ulong)uint.MaxValue, "32-bit values among 2^32 - 1 positions"),
                    (wide, (1UL << 32) + held, "64-bit values among 2^32 more positions than it holds"),
                })
                {
                    var other = Assert.ThrowsExactly<InvalidDataException>(() => read(PokeUInt64(bytes, size, positions)), name);
                    Assert.DoesNotContain(Placed, other.Message, $"{name} placed with {what} was refused for its placement");
                }
            }
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
            // u32 capacity, the placement byte, u32 occupied, then the first entry's slot
            // index.
            const int FirstSlotOffset = 9;

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
        /// A CountSketch payload put together by hand: the width, the depth and a run of
        /// <paramref name="cellBytes"/> zeroed bytes, wrapped in the envelope with the
        /// default hash and a correct checksum. The run is not checked against the
        /// dimensions here, which is the reader's work.
        /// </summary>
        private static byte[] CountSketchPayload(uint width, uint depth, int cellBytes)
        {
            var payload = new PayloadWriter();
            payload.WriteUInt32(width);
            payload.WriteUInt32(depth);
            payload.WriteBytes(new byte[cellBytes]);

            using var stream = new MemoryStream();
            PersistenceFormat.Write(
                stream, StructureId.CountSketch,
                PersistenceFormat.Identify(Defaults.GetDefaultHashFunction()),
                payload.WrittenSpan);
            return stream.ToArray();
        }

        /// <summary>
        /// The reader took any width and depth whose cells came to the bytes the payload
        /// carried, so a sketch of more rows than any constructor builds read back: every
        /// <c>Add</c> loops the rows, and every <c>Count</c> allocates and sorts one
        /// estimate per row. A width of one keeps the cells few enough to carry, and the
        /// default budget admits a depth of millions. The constructor's most is 710 rows,
        /// and the reader now refuses one more.
        /// </summary>
        [TestMethod]
        public void TestCountSketchWithMoreRowsThanAnyConstructorBuildsIsRefused()
        {
            foreach (var depth in new[] { CountSketch.MaxDepth + 1, 100_000u })
            {
                var bytes = CountSketchPayload(1, depth, checked((int)depth * sizeof(long)));

                AssertRefused(
                    () => Persistence.FromByteArray<CountSketch>(bytes),
                    "beyond anything this library builds");
            }
        }

        /// <summary>
        /// A row is one array, so a width past what one holds describes no sketch. The
        /// payload carries no cells at all: the claim is refused for its size, before it
        /// is checked against the bytes behind it.
        /// </summary>
        [TestMethod]
        public void TestCountSketchWithAWiderRowThanAnArrayHoldsIsRefused()
        {
            AssertRefused(
                () => Persistence.FromByteArray<CountSketch>(
                    CountSketchPayload(CountSketch.MaxWidth + 1, 1, 0)),
                "beyond anything this library builds");
        }

        /// <summary>
        /// The bytes the cells need are the width times the depth times eight, and that
        /// product wrapped in 64 bits: a width of 2^31 and a depth of 2^30 need 2^64
        /// bytes, which is nought, and a payload whose cell run was empty agreed with it.
        /// A finite budget refused the result later, for its cost, which is harmless; read
        /// without a budget it went on to allocate what it had been told and ended in an
        /// <see cref="OutOfMemoryException"/> in place of an <see cref="InvalidDataException"/>.
        /// </summary>
        /// <remarks>
        /// Reached through the check itself and through the default budget. Not through
        /// <see cref="ReadOptions.Unlimited"/>, which is the read that would allocate.
        /// </remarks>
        [TestMethod]
        public void TestCountSketchWhoseCellBytesWrapAreRefusedForTheirSize()
        {
            var width = 1u << 31;
            var depth = 1u << 30;

            // The product the old check formed, written to show that it is the wrap.
            Assert.AreEqual(0L, unchecked((long)width * depth * sizeof(long)));

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => CountSketch.CheckCells(width, depth, 0));
            StringAssert.Contains(ex.Message, "beyond anything this library builds");

            AssertRefused(
                () => Persistence.FromByteArray<CountSketch>(CountSketchPayload(width, depth, 0)),
                "beyond anything this library builds");
        }

        /// <summary>
        /// The limits keep the cell bytes far from wrapping in 64 bits, but not the cell
        /// count from wrapping in 32: a width of 2^26 and a depth of 64 are each within
        /// them, and their product is 2^32, which a <c>uint</c> holds as nought. A check
        /// that multiplied the dimensions before widening them agreed with an empty run of
        /// cells for a sketch of 32 GiB.
        /// </summary>
        [TestMethod]
        public void TestCountSketchWhoseCellCountWrapsInThirtyTwoBitsIsRefused()
        {
            var width = 1u << 26;
            var depth = 64u;
            Assert.IsLessThanOrEqualTo(CountSketch.MaxWidth, width);
            Assert.IsLessThanOrEqualTo(CountSketch.MaxDepth, depth);
            Assert.AreEqual(0u, unchecked(width * depth));

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => CountSketch.CheckCells(width, depth, 0));
            StringAssert.Contains(ex.Message, "needs 34359738368 bytes of cells");

            AssertRefused(
                () => Persistence.FromByteArray<CountSketch>(CountSketchPayload(width, depth, 0)),
                "needs 34359738368 bytes of cells");
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
        /// A table's arrays are stored whole, each with a length of its own, and the cell
        /// count and key size are claims about them. The claims are what the reader would
        /// size a table from, so they are checked against the arrays actually present
        /// before anything is allocated from them: a payload cannot make the reader build
        /// a table bigger than the bytes it carries.
        /// </summary>
        [TestMethod]
        public void TestIbltClaimingMoreThanItHoldsIsRefused()
        {
            const int KeySizeOffset = 4;

            var table = new InvertibleBloomLookupTable(8, 8);
            table.Add(new byte[8]);
            var bytes = table.ToByteArray();

            // One cell too many, then counts no array could hold, then the largest a uint
            // can say.
            foreach (var claimed in new[] { table.Cells() + 1, 0x10000000u, 0x80000000u, uint.MaxValue })
            {
                AssertRefused(
                    () => Persistence.FromByteArray<InvertibleBloomLookupTable>(
                        PokeUInt32(bytes, 0, claimed)),
                    "byte counts");
            }

            foreach (var claimed in new[] { 9u, 0x10000000u, 0x7FFFFFFFu })
            {
                AssertRefused(
                    () => Persistence.FromByteArray<InvertibleBloomLookupTable>(
                        PokeUInt32(bytes, KeySizeOffset, claimed)),
                    "byte counts");
            }
        }

        /// <summary>
        /// Each of the three arrays is checked against the cell count by itself, so a
        /// payload whose other two arrays are exactly right is still refused for the one
        /// that is not.
        /// </summary>
        [TestMethod]
        [DataRow(1, 0, 0)]
        [DataRow(-1, 0, 0)]
        [DataRow(0, 1, 0)]
        [DataRow(0, -1, 0)]
        [DataRow(0, 0, 1)]
        [DataRow(0, 0, -1)]
        public void TestIbltWithAnArrayThatDisagreesWithItsCellCountIsRefused(
            int countsOff, int keysOff, int hashesOff)
        {
            const int Cells = 15;
            const int KeySize = 8;

            byte[] Craft(int counts, int keys, int hashes)
            {
                var payload = new PayloadWriter();
                payload.WriteUInt32(Cells);
                payload.WriteUInt32(KeySize);
                payload.WriteBytes(new byte[Cells * sizeof(long) + counts]);
                payload.WriteBytes(new byte[Cells * KeySize + keys]);
                payload.WriteBytes(new byte[Cells * sizeof(ulong) + hashes]);

                var stream = new MemoryStream();
                PersistenceFormat.Write(
                    stream, StructureId.InvertibleBloomLookupTable, HashId.XxHash3_64,
                    payload.WrittenSpan);
                return stream.ToArray();
            }

            // The same layout with every array the right length reads, so what refuses
            // the others is the one length that is wrong.
            Assert.AreEqual(
                (uint)Cells,
                Persistence.FromByteArray<InvertibleBloomLookupTable>(Craft(0, 0, 0)).Cells());

            AssertRefused(
                () => Persistence.FromByteArray<InvertibleBloomLookupTable>(
                    Craft(countsOff, keysOff, hashesOff)),
                "byte counts");
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
