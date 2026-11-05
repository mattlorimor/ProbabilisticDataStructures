using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Numerics;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Every save of a <see cref="CuckooBloomFilter"/> is its whole table, at format version
    /// 11 (#230), with its sizes in 64 bits (#231): the packed fingerprint words and the map of
    /// which entries hold one, word by word, whatever the filter holds.
    /// </summary>
    /// <remarks>
    /// The payload is decoded here from FORMAT.md's layout, by <see cref="Decode"/>, and not
    /// by the library's reader, so a writer and reader that agreed with each other and not
    /// with the document would fail here. The refusals are of payloads the writer produced,
    /// with one field edited and the checksum resealed, each at the coarsest table the edit
    /// can be made in.
    /// </remarks>
    [TestClass]
    public class TestCuckooDenseLayout
    {
        private const int PayloadStart = 14;

        // FORMAT.md's version 11 layout: u64 m, u32 b, u32 f, u64 count, u64 n, then the
        // packed fingerprint words, the map's words, and the two generator states.
        private const int BucketsAt = 0;
        private const int BitsAt = 12;
        private const int CountAt = 16;
        private const int WordsAt = 32;

        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        /// <summary>A version 11 payload, as FORMAT.md lays it out.</summary>
        private sealed record Dense(
            ulong M, uint B, uint F, ulong Count, ulong N,
            ulong[] Fingerprints, ulong[] Map, ulong State, ulong Original)
        {
            internal ulong Entries => this.M * this.B;

            internal ulong FingerprintOf(ulong entry)
            {
                var value = BigInteger.Zero;
                for (uint bit = 0; bit < this.F; bit++)
                {
                    var at = entry * this.F + bit;
                    if ((this.Fingerprints[at / 64] >> (int)(at % 64) & 1) != 0)
                    {
                        value |= BigInteger.One << (int)bit;
                    }
                }

                return (ulong)value;
            }

            internal bool Holds(ulong entry) => (this.Map[entry / 64] >> (int)(entry % 64) & 1) != 0;
        }

        /// <summary>
        /// The words FORMAT.md gives a table of <paramref name="entries"/> entries of
        /// <paramref name="bits"/> bits: the fingerprints packed end to end, rounded up to a
        /// whole word, and a bit for each entry, rounded up likewise.
        /// </summary>
        private static (ulong Fingerprints, ulong Map) WordsOf(ulong entries, uint bits) =>
            ((entries * bits + 63) / 64, (entries + 63) / 64);

        /// <summary>
        /// Decodes an ordinary envelope holding a version 11 cuckoo payload by FORMAT.md's
        /// layout, after checking it is one and that it is exactly as long as its fields say.
        /// </summary>
        private static Dense Decode(byte[] frame)
        {
            Assert.AreEqual(11, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4)),
                "the filter was not written at version 11, the version whose payload is the whole table");
            Assert.AreEqual(9, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6)), "not a cuckoo filter");
            var payload = frame.AsSpan(PayloadStart, (int)BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(10)));

            var m = BinaryPrimitives.ReadUInt64LittleEndian(payload[BucketsAt..]);
            var b = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
            var f = BinaryPrimitives.ReadUInt32LittleEndian(payload[BitsAt..]);
            var count = BinaryPrimitives.ReadUInt64LittleEndian(payload[CountAt..]);
            var n = BinaryPrimitives.ReadUInt64LittleEndian(payload[24..]);
            var (packed, map) = WordsOf(m * b, f);
            Assert.AreEqual(WordsAt + 8 * (long)(packed + map) + 16, payload.Length,
                $"a payload of {m} buckets of {b} entries at {f} bits is not its fields, {packed} " +
                $"words of fingerprints, {map} of the map and the two generator states");

            ulong[] Words(int at, ulong count) => Enumerable.Range(0, (int)count)
                .Select(i => BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(PayloadStart + at + 8 * i)))
                .ToArray();

            var tail = WordsAt + 8 * (int)(packed + map);
            return new Dense(m, b, f, count, n,
                Words(WordsAt, packed), Words(WordsAt + 8 * (int)packed, map),
                BinaryPrimitives.ReadUInt64LittleEndian(payload[tail..]),
                BinaryPrimitives.ReadUInt64LittleEndian(payload[(tail + 8)..]));
        }

        /// <summary>The frame with the checksum worked out again, as a writer would.</summary>
        private static byte[] Resealed(byte[] bytes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4),
                Crc32.HashToUInt32(bytes.AsSpan(4, bytes.Length - 8)));
            return bytes;
        }

        private static byte[] PokeUInt64(byte[] original, int payloadOffset, ulong value)
        {
            var bytes = (byte[])original.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(PayloadStart + payloadOffset), value);
            return Resealed(bytes);
        }

        /// <summary>The frame with one bit of the payload's words flipped, and resealed.</summary>
        private static byte[] FlipWordBit(byte[] original, ulong word, int bit)
        {
            var bytes = (byte[])original.Clone();
            var at = bytes.AsSpan(PayloadStart + WordsAt + 8 * (int)word);
            BinaryPrimitives.WriteUInt64LittleEndian(at, BinaryPrimitives.ReadUInt64LittleEndian(at) ^ (1UL << bit));
            return Resealed(bytes);
        }

        /// <summary>The frame with one bit of the fingerprints flipped: bit <paramref name="at"/> of the packed words.</summary>
        private static byte[] FlipFingerprintBit(byte[] original, ulong at) => FlipWordBit(original, at / 64, (int)(at % 64));

        private static void AssertRefused(byte[] payload, string expected, ReadOptions options = null)
        {
            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<CuckooBloomFilter>(payload, options ?? ReadOptions.Default));
            StringAssert.Contains(ex.Message, expected);
        }

        private static CuckooBloomFilter Read(byte[] payload) => Persistence.FromByteArray<CuckooBloomFilter>(payload);

        /// <summary>A filter with as many items added as take, up to <paramref name="items"/>.</summary>
        private static CuckooBloomFilter Holding(CuckooBloomFilter filter, int items, string prefix = "item")
        {
            for (var i = 0; i < items; i++)
            {
                filter.Add(Key($"{prefix}-{i}"));
            }

            return filter;
        }

        /// <summary>
        /// The tables the layout is checked at, each the coarsest of its kind: one bucket at
        /// the narrowest fingerprint the constructor makes, four bits, at a usual ten, which
        /// leaves 24 bits of its one word past the last entry, and at the widest, 64, a word an
        /// entry; eight buckets, whose fingerprints end on a word and whose map does not; and
        /// two of the sizes the suite reaches for, at 1% and 0.1%.
        /// </summary>
        private static readonly (ulong N, double Fp)[] Tables =
        {
            (1, 0.5), (1, 0.01), (1, 1e-19), (16, 0.01), (100, 0.01), (1000, 0.001),
        };

        /// <summary>
        /// The whole table is written, whatever the filter holds: an empty filter, a light
        /// one and a full one of the same shape write payloads of one length, the length
        /// FORMAT.md gives the shape. Each is decoded by the layout and agrees with the
        /// filter: its fields, the entries its map marks, and nothing set where nothing is.
        /// </summary>
        [TestMethod]
        public void TestEverySaveIsTheWholeTableWhateverItHolds()
        {
            foreach (var (n, fp) in Tables)
            {
                var lengths = new HashSet<int>();
                var counts = new HashSet<ulong>();
                foreach (var items in new[] { 0, 1, (int)n, 4 * (int)n + 64 })
                {
                    var filter = Holding(new CuckooBloomFilter((uint)n, fp, seed: 3), items);
                    var name = $"n {n} at {fp}, {items} added, {filter.Count()} held";
                    var bytes = filter.ToByteArray();
                    var dense = Decode(bytes);

                    Assert.AreEqual((ulong)filter.BucketCount(), dense.M, name);
                    Assert.AreEqual(4u, dense.B, name);
                    Assert.AreEqual(filter.FingerprintBits, dense.F, name);
                    Assert.AreEqual((ulong)filter.Count(), dense.Count, name);
                    Assert.AreEqual((ulong)filter.Capacity(), dense.N, name);

                    var held = 0UL;
                    for (ulong e = 0; e < dense.Entries; e++)
                    {
                        if (dense.Holds(e))
                        {
                            held++;
                        }
                        else
                        {
                            Assert.AreEqual(0UL, dense.FingerprintOf(e), $"{name}: entry {e} is empty and holds a fingerprint");
                        }
                    }

                    Assert.AreEqual(dense.Count, held, $"{name}: the map does not mark one entry for each item");

                    var usedBits = (int)(dense.Entries * dense.F % 64);
                    if (usedBits != 0)
                    {
                        Assert.AreEqual(0UL, dense.Fingerprints[^1] >> usedBits, $"{name}: bits are set past the last entry");
                    }

                    var usedMap = (int)(dense.Entries % 64);
                    if (usedMap != 0)
                    {
                        Assert.AreEqual(0UL, dense.Map[^1] >> usedMap, $"{name}: the map marks places past the last entry");
                    }

                    lengths.Add(bytes.Length);
                    counts.Add(dense.Count);
                }

                // Both ways: one length for every load, and the loads were not all one.
                Assert.HasCount(1, lengths, $"n {n} at {fp}: the payload's length moves with what the filter holds");
                Assert.IsGreaterThan(2, counts.Count, $"n {n} at {fp}: the filters did not hold different numbers of items");
                Assert.Contains(0UL, counts, $"n {n} at {fp}: no empty filter was written");
            }
        }

        /// <summary>
        /// A removal leaves nothing behind it in the table. An entry is emptied by clearing
        /// its bit in the map, and a payload that is the whole table would carry the
        /// fingerprint left in it -- the item's, after it was removed -- so a filter emptied by
        /// removals would write other bytes than a filter never added to, and its own reader,
        /// which refuses a fingerprint in an entry marked empty, would refuse them. So the
        /// table of a filter emptied by removals is the table of one never added to, and so is
        /// one reset.
        /// </summary>
        [TestMethod]
        public void TestAFilterEmptiedByRemovalsWritesTheTableOfOneNeverAddedTo()
        {
            foreach (var (n, fp) in Tables)
            {
                var never = Decode(new CuckooBloomFilter((uint)n, fp, seed: 3).ToByteArray());

                var filter = Holding(new CuckooBloomFilter((uint)n, fp, seed: 3), 4 * (int)n + 64);
                Assert.IsGreaterThan(0UL, (ulong)filter.Count(), $"n {n} at {fp}: nothing was added");
                var full = Decode(filter.ToByteArray());
                Assert.IsTrue(full.Fingerprints.Any(w => w != 0), $"n {n} at {fp}: the full table holds no fingerprint bits");

                for (var i = 4 * (int)n + 63; i >= 0; i--)
                {
                    filter.TestAndRemove(Key($"item-{i}"));
                }

                Assert.AreEqual(0UL, (ulong)filter.Count(), $"n {n} at {fp}: removing everything added left items");
                var emptied = Decode(filter.ToByteArray());
                CollectionAssert.AreEqual(never.Fingerprints, emptied.Fingerprints,
                    $"n {n} at {fp}: the fingerprints of removed items were written");
                CollectionAssert.AreEqual(never.Map, emptied.Map, $"n {n} at {fp}");

                var reset = Decode(Holding(new CuckooBloomFilter((uint)n, fp, seed: 3), 4 * (int)n + 64).Reset().ToByteArray());
                CollectionAssert.AreEqual(never.Fingerprints, reset.Fingerprints,
                    $"n {n} at {fp}: a reset filter wrote the fingerprints it held before");
            }
        }

        /// <summary>
        /// What a read of a version 11 payload allocates beyond the payload it holds is the
        /// table it builds, read into it word by word and not first copied out of the payload
        /// as a run would be (#230). In the large form, where the payload is not held, it is
        /// the table alone.
        /// </summary>
        /// <remarks>
        /// The table is about 180 KB. Probed on master at 006484a, a version 6 read of filters
        /// from one bucket to 131,072 allocated 414 to 671 bytes besides its payload and table
        /// in the ordinary envelope, and 488 to 495 in the large form: the envelope, the filter
        /// and its generator. The slack, 4 KiB, is several times that and far short of the
        /// table, so a copy of the table, or a buffer of the read's own as long as one of its
        /// pieces on the heap, fails it. The reads are counted in a no-GC region (#293).
        /// </remarks>
        [TestMethod]
        public void TestADenseReadHoldsItsTableOnceAndDoesNotCopyIt()
        {
            const long Slack = 4 * 1024;

            var filter = Holding(new CuckooBloomFilter(100_000, 0.01, seed: 1), 5000);
            var bytes = filter.ToByteArray();
            Decode(bytes);
            long payload = bytes.Length - PersistenceFormat.EnvelopeLength;
            var table = (long)filter.SizeInBytes();
            Assert.IsGreaterThan(100_000L, table, "the table is too small for a copy of it to stand out");

            var ordinary = NoGCRegion.Allocated(() => CuckooBloomFilter.ReadFrom(new MemoryStream(bytes, writable: false)));
            Assert.IsGreaterThanOrEqualTo(payload + table, ordinary,
                $"the read allocated {ordinary:N0} bytes, less than the {payload:N0}-byte payload it holds and the {table:N0}-byte table");
            Assert.IsLessThanOrEqualTo(payload + table + Slack, ordinary,
                $"the read allocated {ordinary:N0} bytes, {ordinary - payload - table:N0} more than its payload and its table");

            var large = LargeForm.WriteLarge(filter);
            Assert.IsTrue(LargeForm.IsLarge(large), "the filter was not written in the large form");
            var streamed = NoGCRegion.Allocated(() => CuckooBloomFilter.ReadFrom(new MemoryStream(large, writable: false)));
            Assert.IsGreaterThanOrEqualTo(table, streamed,
                $"the read of the large form allocated {streamed:N0} bytes, less than the {table:N0}-byte table");
            Assert.IsLessThanOrEqualTo(table + Slack, streamed,
                $"the read of the large form allocated {streamed:N0} bytes, {streamed - table:N0} more than its table");

            CollectionAssert.AreEqual(bytes, CuckooBloomFilter.ReadFrom(new MemoryStream(large, writable: false)).ToByteArray(),
                "the large form does not read back as the filter");
        }

        /// <summary>
        /// The reader takes a table past the 2^31 entries it used to stop at, and refuses one
        /// only for the bytes it does not hold (#231). A filter of one bucket, its bucket count
        /// raised to 2^31: 2^33 entries of ten bits, which one array of words holds, so the
        /// payload's 72 bytes are what refuse it, under the default budget and under none. The
        /// table one past that, 2^32 buckets, needs more words than one array holds, and is
        /// refused as beyond anything the library builds.
        /// </summary>
        /// <remarks>
        /// And at the corner where 64-bit arithmetic wraps by construction: 2^62 buckets of
        /// four entries are 2^64 entries, none at all in a <c>u64</c>, and 2^63 are two of
        /// those. A shape worked out in 64 bits finds a table of no entries there, and reads
        /// the payload's words as something else; each is refused for its size.
        /// </remarks>
        [TestMethod]
        public void TestADenseTablePastTheOldCeilingIsRefusedOnlyForTheBytesItLacks()
        {
            var bytes = Holding(new CuckooBloomFilter(1, 0.01, seed: 1), 4).ToByteArray();
            Assert.AreEqual(1UL, Decode(bytes).M);
            Assert.AreEqual(10u, Decode(bytes).F);

            foreach (var options in new[] { ReadOptions.Default, ReadOptions.Unlimited })
            {
                AssertRefused(PokeUInt64(bytes, BucketsAt, 1UL << 31), "beyond what it can hold", options);
                AssertRefused(PokeUInt64(bytes, BucketsAt, 1UL << 32), "beyond anything this library builds", options);
                AssertRefused(PokeUInt64(bytes, BucketsAt, 1UL << 62), "beyond anything this library builds", options);
                AssertRefused(PokeUInt64(bytes, BucketsAt, 1UL << 63), "beyond anything this library builds", options);
            }

            Assert.AreEqual(4UL, (ulong)Read(bytes).Count(), "the unedited payload does not read");
        }

        /// <summary>
        /// The bucket count is a <c>u64</c>, and all of it is read. A count with a bit above
        /// the low 32 set is not a power of two, though its low 32 bits are the filter's own
        /// 32 buckets: a reader that took only those would read the filter.
        /// </summary>
        [TestMethod]
        public void TestADenseBucketCountIsReadInSixtyFourBits()
        {
            var bytes = Holding(new CuckooBloomFilter(100, 0.01, seed: 1), 20).ToByteArray();
            Assert.AreEqual(32UL, Decode(bytes).M);

            foreach (var buckets in new[] { (1UL << 32) | 32, (1UL << 40) | 32, (1UL << 63) | 32, 3UL, 31UL, 33UL })
            {
                AssertRefused(PokeUInt64(bytes, BucketsAt, buckets), "is not a power of two");
            }

            Assert.AreEqual(32UL, (ulong)Read(bytes).BucketCount());
        }

        /// <summary>
        /// The item count is a <c>u64</c> and has to be the entries the map marks, all of it:
        /// one more or one fewer, nought, the most a <c>u64</c> holds, and the count with
        /// 2^32 added, which a comparison in 32 bits would let through.
        /// </summary>
        [TestMethod]
        public void TestADenseItemCountIsHeldToItsMapInSixtyFourBits()
        {
            var bytes = Holding(new CuckooBloomFilter(100, 0.01, seed: 1), 20).ToByteArray();
            var held = Decode(bytes).Count;
            Assert.AreEqual(20UL, held);

            foreach (var count in new[] { 0UL, held - 1, held + 1, (1UL << 32) + held, ulong.MaxValue })
            {
                AssertRefused(PokeUInt64(bytes, CountAt, count), $"item count is {count}");
            }

            Assert.AreEqual(held, (ulong)Read(bytes).Count());
        }

        /// <summary>
        /// Nothing past the last entry is set, in the fingerprints or in the map, since the
        /// writer never sets it, and a reader refuses either. One bucket of four ten-bit
        /// entries, all held: bit 39 of the one word of fingerprints is the last entry's top
        /// bit, and changing it reads as a filter with that fingerprint, while bit 40 is past
        /// it and refused, as is bit 63. Its map's four bits are its entries, and the bit
        /// after them is refused; the last of them, set on an empty filter with a count of
        /// one, reads as one item whose fingerprint is nought.
        /// </summary>
        [TestMethod]
        public void TestADenseTableWithBitsPastItsLastEntryIsRefused()
        {
            var full = Holding(new CuckooBloomFilter(1, 0.01, seed: 1), 4).ToByteArray();
            var dense = Decode(full);
            Assert.AreEqual(4UL, dense.Count, "the one bucket is not full");
            Assert.HasCount(1, dense.Fingerprints);

            Assert.AreEqual(4UL, (ulong)Read(FlipFingerprintBit(full, 39)).Count(), "the last entry's top bit was refused");
            AssertRefused(FlipFingerprintBit(full, 40), "past its last entry");
            AssertRefused(FlipFingerprintBit(full, 63), "past its last entry");

            var empty = new CuckooBloomFilter(1, 0.01, seed: 1).ToByteArray();
            var mapWord = (ulong)Decode(empty).Fingerprints.Length;
            var oneItem = PokeUInt64(empty, CountAt, 1);
            Assert.AreEqual(1UL, (ulong)Read(FlipWordBit(oneItem, mapWord, 3)).Count(), "the map's last entry was refused");
            AssertRefused(FlipWordBit(oneItem, mapWord, 4), "past its last entry");
            AssertRefused(FlipWordBit(oneItem, mapWord, 63), "past its last entry");
        }

        /// <summary>
        /// An entry the map marks empty holds no fingerprint, since a removal clears what it
        /// held and a reset clears the table, and a reader refuses one that does. Each place
        /// the check can look is edited: the first and last bit of 64 empty entries, whose
        /// fingerprints are a whole number of words, and an empty entry among held ones.
        /// The same bit in a held entry reads, as a filter with that fingerprint. At four, ten
        /// and 64 bits, so that the 64 entries' words are one, several and one an entry.
        /// </summary>
        [TestMethod]
        public void TestADenseTableWithAFingerprintInAnEntryItMarksEmptyIsRefused()
        {
            foreach (var fp in new[] { 0.5, 0.01, 1e-19 })
            {
                var filter = Holding(new CuckooBloomFilter(1000, fp, seed: 1), 5);
                var bytes = filter.ToByteArray();
                var dense = Decode(bytes);
                var name = $"{dense.F} bits";
                Assert.AreEqual(5UL, dense.Count, name);

                var clear = Enumerable.Range(0, dense.Map.Length).First(k => dense.Map[k] == 0);
                var first = (ulong)clear * 64 * dense.F;
                AssertRefused(FlipFingerprintBit(bytes, first), "marks empty");
                AssertRefused(FlipFingerprintBit(bytes, first + 64 * dense.F - 1), "marks empty");

                var some = Enumerable.Range(0, dense.Map.Length).First(k => dense.Map[k] != 0);
                var held = Enumerable.Range(0, 64).Select(j => (ulong)some * 64 + (ulong)j).First(dense.Holds);
                var empty = Enumerable.Range(0, 64).Select(j => (ulong)some * 64 + (ulong)j).First(e => !dense.Holds(e));
                AssertRefused(FlipFingerprintBit(bytes, empty * dense.F), "marks empty");
                AssertRefused(FlipFingerprintBit(bytes, empty * dense.F + dense.F - 1), "marks empty");

                var changed = Read(FlipFingerprintBit(bytes, held * dense.F + dense.F - 1));
                Assert.AreEqual(5UL, (ulong)changed.Count(), $"{name}: a held entry's fingerprint was refused");
                CollectionAssert.AreNotEqual(bytes, changed.ToByteArray(), $"{name}: the edit did not change the filter");
            }
        }

        /// <summary>
        /// A payload whose words stop short of the table, or go on past the generator states,
        /// is refused: the length is the fields' and nothing else.
        /// </summary>
        [TestMethod]
        public void TestADenseTableShortOrLongIsRefused()
        {
            var bytes = Holding(new CuckooBloomFilter(100, 0.01, seed: 1), 20).ToByteArray();
            Decode(bytes);

            byte[] Payload(int change)
            {
                var length = bytes.Length - PersistenceFormat.EnvelopeLength + change;
                var frame = new byte[length + PersistenceFormat.EnvelopeLength];
                bytes.AsSpan(0, PayloadStart + Math.Min(length, bytes.Length - PersistenceFormat.EnvelopeLength)).CopyTo(frame);
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(10), (uint)length);
                return Resealed(frame);
            }

            AssertRefused(Payload(-8), "beyond what it can hold");
            AssertRefused(Payload(8), "left over");
        }

        /// <summary>
        /// The sizes a caller sees are 64 bits (#231): the number of items the filter is built
        /// for, and the buckets, capacity and count it reports. Each was a <c>uint</c>, which no
        /// table past 2^32 entries can report or be asked for in.
        /// </summary>
        [TestMethod]
        public void TestTheCuckooFilterSizesAreSixtyFourBits()
        {
            var type = typeof(CuckooBloomFilter);
            var constructor = type.GetConstructors().Single();
            Assert.AreEqual(typeof(ulong), constructor.GetParameters().Single(p => p.Name == "n").ParameterType, "n");

            foreach (var size in new[] { "BucketCount", "Capacity", "Count" })
            {
                Assert.AreEqual(typeof(ulong), type.GetMethod(size, Type.EmptyTypes)!.ReturnType, size);
            }
        }
    }
}
