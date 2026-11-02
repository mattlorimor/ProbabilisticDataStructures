using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Payloads written by the version that introduced each structure's layout, checked
    /// in byte-for-byte. The format is documented as stable, and this is what makes that
    /// a promise rather than an intention: a change that stops old data being readable
    /// fails here rather than in somebody's storage.
    /// <para>
    /// If one of these fails, the fix is not to regenerate the fixture. It is either to
    /// keep reading the old layout or to raise the format version and read both.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestPersistenceFormatStability
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        private static readonly string[] Words =
            { "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa" };

        /// <summary>
        /// The plain filter's own version 1 payload, read and placed as it was written:
        /// with 32-bit values, which is what every version 1 payload holds. It holds the
        /// first five words, not all ten.
        /// </summary>
        [TestMethod]
        public void TestStoredBloomFilterStillReads()
        {
            var f = BloomFilter.ReadFrom(Fixture("bloomfilter-v1.bin"));

            Assert.AreEqual(959u, f.Capacity());
            Assert.AreEqual(7u, f.K());
            Assert.AreEqual(5u, f.Count());
            Assert.AreEqual(Placement.Narrow, f.Placement);
            foreach (var word in Words.Take(5))
            {
                Assert.IsTrue(f.Test(Key(word)), $"the stored filter no longer finds {word}");
            }
        }

        /// <summary>
        /// Version 8 is the version 1 layout with its bits placed by 64-bit values, which
        /// a new filter of 2^27 bits or more is. The five version 8 fixtures were
        /// assembled by hand from FORMAT.md and the 64-bit scheme -- XxHash3 of the item,
        /// XxHash3 of that value's eight little-endian bytes, positions
        /// <c>(first + i * second) mod m</c> in 64-bit arithmetic -- without the library,
        /// so that reading them holds the reader to the format and not to the writer.
        /// Each holds the same ten words as the version 1 fixtures, in a filter of the same
        /// size, small enough to be a fixture; the version and not the size says how it
        /// was placed.
        /// </summary>
        [TestMethod]
        public void TestStoredBloomFilterAtVersionEightStillReads()
        {
            var f = BloomFilter.ReadFrom(Fixture("bloomfilter-v8.bin"));

            Assert.AreEqual(959u, f.Capacity());
            Assert.AreEqual(7u, f.K());
            Assert.AreEqual(10u, f.Count());
            Assert.AreEqual(Placement.Wide, f.Placement);
            AssertAllPresent(f.Test);
            AssertLostWhenReadAs(1, "bloomfilter-v8.bin", b => BloomFilter.ReadFrom(new MemoryStream(b)).Test);
        }

        [TestMethod]
        public void TestStoredCountingBloomFilterAtVersionEightStillReads()
        {
            var f = CountingBloomFilter.ReadFrom(Fixture("countingbloomfilter-v8.bin"));

            Assert.AreEqual(959u, f.Capacity());
            Assert.AreEqual(7u, f.K());
            Assert.AreEqual(10u, f.Count());
            Assert.AreEqual(Placement.Wide, f.Placement);
            AssertAllPresent(f.Test);
            AssertLostWhenReadAs(1, "countingbloomfilter-v8.bin", b => CountingBloomFilter.ReadFrom(new MemoryStream(b)).Test);

            // The counters came back where the 64-bit scheme put them: each word removes
            // once and is gone.
            Assert.IsTrue(f.TestAndRemove(Key("alpha")));
            Assert.IsFalse(f.Test(Key("alpha")));
        }

        [TestMethod]
        public void TestStoredDeletableBloomFilterAtVersionEightStillReads()
        {
            var f = DeletableBloomFilter.ReadFrom(Fixture("deletablebloomfilter-v8.bin"));

            Assert.AreEqual(949u, f.Capacity());
            Assert.AreEqual(7u, f.K());
            Assert.AreEqual(10u, f.Count());
            Assert.AreEqual(Placement.Wide, f.Placement);
            AssertAllPresent(f.Test);
            AssertLostWhenReadAs(1, "deletablebloomfilter-v8.bin", b => DeletableBloomFilter.ReadFrom(new MemoryStream(b)).Test);
            Assert.IsTrue(f.TestAndRemove(Key("alpha")));
        }

        [TestMethod]
        public void TestStoredPartitionedBloomFilterAtVersionEightStillReads()
        {
            var f = PartitionedBloomFilter.ReadFrom(Fixture("partitionedbloomfilter-v8.bin"));

            Assert.AreEqual(959u, f.Capacity());
            Assert.AreEqual(7u, f.K());
            Assert.AreEqual(10u, f.Count());
            Assert.AreEqual(Placement.Wide, f.Placement);
            AssertAllPresent(f.Test);
            AssertLostWhenReadAs(1, "partitionedbloomfilter-v8.bin", b => PartitionedBloomFilter.ReadFrom(new MemoryStream(b)).Test);
        }

        /// <summary>
        /// The stable filter's version 8 is its version 5 layout placed by 64-bit values:
        /// the cells, where the generator had got to and where it began. Assembled by
        /// hand like the others, with the generator's draws and the decay restated as
        /// well, from the filter the version 5 fixture holds.
        /// </summary>
        [TestMethod]
        public void TestStoredStableBloomFilterAtVersionEightStillReads()
        {
            var f = StableBloomFilter.ReadFrom(Fixture("stablebloomfilter-v8.bin"));

            Assert.AreEqual(1000u, f.Cells());
            Assert.AreEqual(3u, f.K());
            Assert.AreEqual(35u, f.P());
            Assert.AreEqual(Placement.Wide, f.Placement);
            AssertAllPresent(f.Test);
            AssertLostWhenReadAs(5, "stablebloomfilter-v8.bin", b => StableBloomFilter.ReadFrom(new MemoryStream(b)).Test);

            // It resumes the sequence it was stored partway through, placing as it did...
            var twin = StoredWideStableTwin();
            for (var i = 0; i < 300; i++)
            {
                f.Add(Key($"more{i}"));
                twin.Add(Key($"more{i}"));
            }

            CollectionAssert.AreEqual(twin.ToByteArray(), f.ToByteArray(),
                "the restored filter did not carry on the sequence it was stored partway through");

            // ...and a reset takes it back to its seed, and keeps its placement.
            f.Reset();
            CollectionAssert.AreEqual(
                new StableBloomFilter(1000, 2, 0.01, null, 7, Placement.Wide).ToByteArray(), f.ToByteArray(),
                "Reset did not return the restored filter to its stored original state");
        }

        /// <summary>
        /// The stored payload, relabelled as the version its layout had before version 8
        /// and read, loses words: read as placed with 32-bit values, the filter looks for
        /// them where they were not put. That is what makes the fixture a witness to the
        /// placement and not only to the layout.
        /// </summary>
        private static void AssertLostWhenReadAs(ushort version, string fixture, Func<byte[], Func<byte[], bool>> read)
        {
            var test = read(Relabeled(ReadFixture(fixture), version));
            var lost = Words.Count(word => !test(Key(word)));
            Assert.IsGreaterThan(0, lost,
                $"{fixture} read as version {version} still finds every word, so it cannot tell the placements apart");
        }

        /// <summary>
        /// A never-saved filter in the state the version 8 stable fixture holds.
        /// </summary>
        private static StableBloomFilter StoredWideStableTwin()
        {
            var twin = new StableBloomFilter(1000, 2, 0.01, null, 7, Placement.Wide);
            foreach (var word in Words)
            {
                twin.Add(Key(word));
            }

            return twin;
        }

        [TestMethod]
        public void TestStoredBloomFilter64StillReads()
        {
            var f = BloomFilter64.ReadFrom(Fixture("bloomfilter64-v1.bin"));

            Assert.AreEqual(959ul, f.Capacity());
            Assert.AreEqual(7u, f.K());
            Assert.AreEqual(10ul, f.Count());
            AssertAllPresent(f.Test);
        }

        [TestMethod]
        public void TestStoredCountingBloomFilterStillReads()
        {
            var f = CountingBloomFilter.ReadFrom(Fixture("countingbloomfilter-v1.bin"));

            Assert.AreEqual(959u, f.Capacity());
            Assert.AreEqual(7u, f.K());
            Assert.AreEqual(10u, f.Count());
            AssertAllPresent(f.Test);

            // The counters came back, not just the occupancy: each word removes once
            // and is gone.
            Assert.IsTrue(f.TestAndRemove(Key("alpha")));
            Assert.IsFalse(f.Test(Key("alpha")));
        }

        [TestMethod]
        public void TestStoredDeletableBloomFilterStillReads()
        {
            var f = DeletableBloomFilter.ReadFrom(Fixture("deletablebloomfilter-v1.bin"));

            Assert.AreEqual(949u, f.Capacity());
            Assert.AreEqual(7u, f.K());
            Assert.AreEqual(10u, f.Count());
            AssertAllPresent(f.Test);

            // Region size is recomputed on read rather than stored, so this also
            // exercises that the recomputation agrees with what wrote the payload.
            Assert.IsTrue(f.TestAndRemove(Key("alpha")));
        }

        [TestMethod]
        public void TestStoredPartitionedBloomFilterStillReads()
        {
            var f = PartitionedBloomFilter.ReadFrom(Fixture("partitionedbloomfilter-v1.bin"));

            Assert.AreEqual(959u, f.Capacity());
            Assert.AreEqual(7u, f.K());
            Assert.AreEqual(10u, f.Count());
            AssertAllPresent(f.Test);
        }

        [TestMethod]
        public void TestStoredScalableBloomFilterStillReads()
        {
            var f = ScalableBloomFilter.ReadFrom(Fixture("scalablebloomfilter-v1.bin"));

            Assert.AreEqual(1053u, f.Capacity());
            Assert.AreEqual(7u, f.K());

            for (int i = 0; i < 100; i++)
            {
                Assert.IsTrue(f.Test(Key($"s{i}")), $"the stored filter no longer finds s{i}");
            }

            // The contained filters came back in order with their fill ratios intact,
            // so it still grows rather than stopping where it was written.
            for (int i = 100; i < 400; i++)
            {
                f.Add(Key($"s{i}"));
            }

            Assert.IsGreaterThan(1053u, f.Capacity(), "the restored filter did not grow");
        }

        [TestMethod]
        public void TestStoredStableBloomFilterStillReads()
        {
            var f = StableBloomFilter.ReadFrom(Fixture("stablebloomfilter-v1.bin"));

            Assert.AreEqual(1000u, f.Cells());
            Assert.AreEqual(3u, f.K());
            Assert.AreEqual(35u, f.P());
            AssertAllPresent(f.Test);
        }

        /// <summary>
        /// The version 2 layout, which added the stored generator state in 6.0.0. The
        /// version 1 fixture above is not replaced by this one: both have to keep
        /// reading, which is the whole of what raising the version bought.
        /// </summary>
        [TestMethod]
        public void TestStoredStableBloomFilterAtVersionTwoStillReads()
        {
            var f = StableBloomFilter.ReadFrom(Fixture("stablebloomfilter-v2.bin"));

            Assert.AreEqual(1000u, f.Cells());
            Assert.AreEqual(3u, f.K());
            Assert.AreEqual(35u, f.P());
            AssertAllPresent(f.Test);
        }

        /// <summary>
        /// A version 2 payload stores the generator's position and nothing about where it
        /// began, and what it stores is read as the position it is: a restored filter
        /// carries on the sequence a never-saved twin is partway through, rather than
        /// starting one of its own.
        /// </summary>
        [TestMethod]
        public void TestStoredStableBloomFilterAtVersionTwoResumesItsSequence()
        {
            var f = StableBloomFilter.ReadFrom(Fixture("stablebloomfilter-v2.bin"));
            var twin = StoredStableTwin();

            for (var i = 0; i < 300; i++)
            {
                f.Add(Key($"more{i}"));
                twin.Add(Key($"more{i}"));
            }

            // Everything but the original state and the CRC after it, which are the part
            // a version 2 payload never held: the twin knows where it began and the
            // restored filter takes the position it was read at.
            CollectionAssert.AreEqual(twin.ToByteArray()[..^12], f.ToByteArray()[..^12],
                "the restored filter did not carry on the sequence it was stored partway through");
        }

        /// <summary>
        /// The version 5 layout, which added where the generator began. Hand-assembled
        /// from the version 2 fixture rather than written by the library, so reading it
        /// is held to the layout and not to whatever the writer happens to produce: the
        /// version 2 payload, the seed 7 as one more u64, a version of 5 and a CRC to
        /// match.
        /// </summary>
        [TestMethod]
        public void TestStoredStableBloomFilterAtVersionFiveStillReads()
        {
            var f = StableBloomFilter.ReadFrom(Fixture("stablebloomfilter-v5.bin"));

            Assert.AreEqual(1000u, f.Cells());
            Assert.AreEqual(3u, f.K());
            Assert.AreEqual(35u, f.P());
            AssertAllPresent(f.Test);

            // It resumes the sequence it was stored partway through...
            var twin = StoredStableTwin();
            for (var i = 0; i < 300; i++)
            {
                f.Add(Key($"more{i}"));
                twin.Add(Key($"more{i}"));
            }

            CollectionAssert.AreEqual(twin.ToByteArray(), f.ToByteArray(),
                "the restored filter did not carry on the sequence it was stored partway through");

            // ...and a reset takes it back to the seed it was built with, which only the
            // stored original state can say.
            f.Reset();
            CollectionAssert.AreEqual(
                new StableBloomFilter(1000, 2, 0.01, seed: 7).ToByteArray(), f.ToByteArray(),
                "Reset did not return the restored filter to its stored original state");
        }

        /// <summary>
        /// A never-saved filter in the state the stable fixtures were written from.
        /// </summary>
        private static StableBloomFilter StoredStableTwin()
        {
            var twin = new StableBloomFilter(1000, 2, 0.01, seed: 7);
            foreach (var word in Words)
            {
                twin.Add(Key(word));
            }

            return twin;
        }

        /// <summary>
        /// A payload is read as the layout its version names, and refused when the bytes
        /// are not that layout, even with a correct checksum. The version 2 fixture is
        /// the version 5 layout without its last field, so labelled 5 it is short.
        /// </summary>
        [TestMethod]
        public void TestAVersionFivePayloadMissingTheOriginalStateIsRefused()
        {
            var bytes = Relabeled(ReadFixture("stablebloomfilter-v2.bin"), 5);

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => StableBloomFilter.ReadFrom(new MemoryStream(bytes)));
            StringAssert.Contains(ex.Message, "ended after");
        }

        /// <summary>
        /// And the other way: the version 5 fixture labelled 2 carries a field that
        /// version does not have, which is a payload that is not what it says, not a
        /// version 2 payload with something extra to ignore.
        /// </summary>
        [TestMethod]
        public void TestAVersionTwoPayloadWithAnOriginalStateIsRefused()
        {
            var bytes = Relabeled(ReadFixture("stablebloomfilter-v5.bin"), 2);

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => StableBloomFilter.ReadFrom(new MemoryStream(bytes)));
            StringAssert.Contains(ex.Message, "bytes left over");
        }

        /// <summary>
        /// The newest version this library reads is the one after which it refuses. The
        /// general refusal test uses a version far beyond any, which cannot notice the
        /// ceiling sitting one too high. The newest fixture of each filter that has
        /// raised the ceiling is relabelled one past it, and the message names the
        /// ceiling so that one too low is told apart from one too high.
        /// </summary>
        [TestMethod]
        public void TestTheVersionAfterTheNewestIsRefused()
        {
            // Nine is one past the newest, version 8. It is written out rather than
            // computed from the library's ceiling, which is the thing being checked.
            var stable = Relabeled(ReadFixture("stablebloomfilter-v8.bin"), 9);
            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => StableBloomFilter.ReadFrom(new MemoryStream(stable)));
            StringAssert.Contains(ex.Message, "format version 9");
            StringAssert.Contains(ex.Message, "written by a later version");

            var cuckoo = Relabeled(ReadFixture("cuckoobloomfilter-v6.bin"), 9);
            ex = Assert.ThrowsExactly<InvalidDataException>(
                () => CuckooBloomFilter.ReadFrom(new MemoryStream(cuckoo)));
            StringAssert.Contains(ex.Message, "format version 9");
            StringAssert.Contains(ex.Message, "written by a later version");

            var bloom = Relabeled(ReadFixture("bloomfilter-v8.bin"), 9);
            ex = Assert.ThrowsExactly<InvalidDataException>(
                () => BloomFilter.ReadFrom(new MemoryStream(bloom)));
            StringAssert.Contains(ex.Message, "format version 9");
            StringAssert.Contains(ex.Message, "written by a later version");
        }

        /// <summary>
        /// A version 3 payload stores the generator's position and nothing about where it
        /// began, and what it stores is read as the position it is: a restored filter
        /// carries on the eviction sequence a never-saved twin is partway through,
        /// rather than starting one of its own. It is also the layout the fingerprints
        /// were packed at, so the width comes back in bits and the words are all found.
        /// </summary>
        [TestMethod]
        public void TestStoredCuckooBloomFilterAtVersionThreeResumesItsSequence()
        {
            var f = CuckooBloomFilter.ReadFrom(Fixture("cuckoobloomfilter-v3.bin"));
            var twin = StoredCuckooTwin();

            Assert.AreEqual(100u, f.Capacity());
            Assert.AreEqual(10u, f.FingerprintBits);
            AssertAllPresent(f.Test);

            for (var i = 0; i < 300; i++)
            {
                f.Add(Key($"more{i}"));
                twin.Add(Key($"more{i}"));
            }

            // Everything but the original state and the CRC after it, which are the part
            // a version 3 payload never held: the twin knows where it began and the
            // restored filter takes the position it was read at.
            CollectionAssert.AreEqual(twin.ToByteArray()[..^12], f.ToByteArray()[..^12],
                "the restored filter did not carry on the sequence it was stored partway through");
        }

        /// <summary>
        /// The version 6 layout, which added where the generator began. Hand-assembled
        /// from the version 3 fixture rather than written by the library, so reading it
        /// is held to the layout and not to whatever the writer happens to produce: the
        /// version 3 payload, the seed 7 as one more u64, a version of 6 and a CRC to
        /// match.
        /// </summary>
        [TestMethod]
        public void TestStoredCuckooBloomFilterAtVersionSixStillReads()
        {
            var f = CuckooBloomFilter.ReadFrom(Fixture("cuckoobloomfilter-v6.bin"));

            Assert.AreEqual(100u, f.Capacity());
            Assert.AreEqual(110u, f.Count());
            Assert.AreEqual(10u, f.FingerprintBits);
            AssertAllPresent(f.Test);

            // It resumes the sequence it was stored partway through...
            var twin = StoredCuckooTwin();
            for (var i = 0; i < 300; i++)
            {
                f.Add(Key($"more{i}"));
                twin.Add(Key($"more{i}"));
            }

            CollectionAssert.AreEqual(twin.ToByteArray(), f.ToByteArray(),
                "the restored filter did not carry on the sequence it was stored partway through");

            // ...and a reset takes it back to the seed it was built with, which only the
            // stored original state can say.
            f.Reset();
            CollectionAssert.AreEqual(
                new CuckooBloomFilter(100, 0.01, seed: 7).ToByteArray(), f.ToByteArray(),
                "Reset did not return the restored filter to its stored original state");
        }

        /// <summary>
        /// A never-saved filter in the state the cuckoo fixtures from version 3 on were
        /// written from: the words go in first so they are all findable, then a load
        /// heavy enough to make buckets collide and the relocation path run -- but short
        /// of saturating the filter, which would refuse inserts rather than relocate.
        /// </summary>
        private static CuckooBloomFilter StoredCuckooTwin()
        {
            var twin = new CuckooBloomFilter(100, 0.01, seed: 7);
            foreach (var word in Words)
            {
                twin.Add(Key(word));
            }

            for (int i = 0; i < 100; i++)
            {
                twin.Add(Key($"w{i}"));
            }

            return twin;
        }

        /// <summary>
        /// A payload is read as the layout its version names, and refused when the bytes
        /// are not that layout, even with a correct checksum. The version 3 fixture is
        /// the version 6 layout without its last field, so labelled 6 it is short.
        /// </summary>
        [TestMethod]
        public void TestAVersionSixCuckooPayloadMissingTheOriginalStateIsRefused()
        {
            var bytes = Relabeled(ReadFixture("cuckoobloomfilter-v3.bin"), 6);

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => CuckooBloomFilter.ReadFrom(new MemoryStream(bytes)));
            StringAssert.Contains(ex.Message, "ended after");
        }

        /// <summary>
        /// And the other way: the version 6 fixture labelled 3 carries a field that
        /// version does not have, which is a payload that is not what it says, not a
        /// version 3 payload with something extra to ignore.
        /// </summary>
        [TestMethod]
        public void TestAVersionThreeCuckooPayloadWithAnOriginalStateIsRefused()
        {
            var bytes = Relabeled(ReadFixture("cuckoobloomfilter-v6.bin"), 3);

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => CuckooBloomFilter.ReadFrom(new MemoryStream(bytes)));
            StringAssert.Contains(ex.Message, "bytes left over");
        }

        /// <summary>
        /// The frame with another format version in its header and the checksum repaired,
        /// so that what refuses it is the reading and not the CRC.
        /// </summary>
        private static byte[] Relabeled(byte[] frame, ushort version)
        {
            var bytes = (byte[])frame.Clone();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), version);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(bytes.Length - 4),
                System.IO.Hashing.Crc32.HashToUInt32(bytes.AsSpan(4, bytes.Length - 8)));
            return bytes;
        }

        /// <summary>
        /// A filter written before the fingerprints were packed. It restores at eight
        /// bits per stored byte, which is not a compatibility mode but the same filter
        /// it always was: the fingerprint value is the same low bits of the same
        /// digest, and hashing it to find its partner bucket sees the same bytes.
        /// <para>
        /// Membership alone would not prove that. A filter that lost its width and
        /// answered true to everything would pass an all-present check, so this also
        /// pins the reconstructed width and requires the filter to still reject.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestStoredCuckooBloomFilterAtVersionTwoStillReads()
        {
            var f = CuckooBloomFilter.ReadFrom(Fixture("cuckoobloomfilter-v2.bin"));

            Assert.AreEqual(100u, f.Capacity());
            Assert.AreEqual(16u, f.FingerprintBits,
                "the stored filter recorded two bytes, so it must come back as a " +
                "sixteen-bit filter -- a narrower one would compute different " +
                "fingerprints for the same items and find none of them");
            AssertAllPresent(f.Test);

            var absent = 0;
            for (var i = 0; i < 500; i++)
            {
                if (!f.Test(Key($"never-added-{i}"))) absent++;
            }

            Assert.IsGreaterThan(400, absent,
                $"only {absent} of 500 unseen items were rejected. A restored filter " +
                "that says yes to everything would pass a membership check while " +
                "holding nothing.");
        }

        [TestMethod]
        public void TestStoredBinaryFuseFilterStillReads()
        {
            var f = BinaryFuseFilter.ReadFrom(Fixture("binaryfusefilter-v1.bin"));

            Assert.AreEqual(10u, f.Count());
            Assert.AreEqual(BinaryFuseWidth.Eight, f.Width());
            AssertAllPresent(f.Test);
        }

        /// <summary>
        /// Both of the estimator's representations, which are different payload layouts
        /// behind one structure id, so both have to keep reading.
        /// </summary>
        [TestMethod]
        public void TestStoredHyperLogLogPlusStillReads()
        {
            var sparse = HyperLogLogPlus.ReadFrom(Fixture("hyperloglogplus-sparse-v1.bin"));
            Assert.AreEqual(10ul, sparse.Count(), "the sparse form counts exactly");
            Assert.IsTrue(sparse.IsSparse);

            var dense = HyperLogLogPlus.ReadFrom(Fixture("hyperloglogplus-dense-v1.bin"));
            Assert.IsFalse(dense.IsSparse);
            Assert.AreEqual(14u, dense.Precision());

            // Within the nominal error for 2^14 registers over 50,000 items.
            Assert.IsLessThan(0.02, Math.Abs((double)dense.Count() - 50000) / 50000);
        }

        /// <summary>
        /// A sparse payload at the smallest precision, holding the most it holds there:
        /// 16 hashes, 128 bytes standing in for 16 bytes of registers. That is the
        /// documented deviation from the paper, whose rule would convert after 2, and it
        /// is what 6.2.0 wrote -- these are the bytes that writer produced, its sparse
        /// path unchanged since 6.0.0. Today's writer is pinned to the same bytes in
        /// <see cref="TestWritingStillProducesTheStoredBytes"/>.
        /// <para>
        /// It has to come back exactly as written, counting exactly. It is also full,
        /// which is where #149 lived: a repeat must leave it alone, and the first new
        /// item converts it.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestStoredHyperLogLogPlusAtTheSmallestPrecisionStillReads()
        {
            var bytes = ReadFixture("hyperloglogplus-sparse-p4-v1.bin");
            var estimator = Persistence.FromByteArray<HyperLogLogPlus>(bytes);

            Assert.AreEqual(4u, estimator.Precision());
            Assert.IsTrue(estimator.IsSparse, "a stored sparse payload came back dense");
            Assert.AreEqual(16ul, estimator.Count(), "the stored sparse form counts exactly");

            for (var i = 0; i < 16; i++)
            {
                estimator.Add(Key($"w{i}"));
                Assert.IsTrue(estimator.IsSparse,
                    $"repeating stored item w{i} converted the estimator to dense");
            }

            CollectionAssert.AreEqual(bytes, estimator.ToByteArray(),
                "repeating the stored items changed the stored estimator");

            estimator.Add(Key("w16"));
            Assert.IsFalse(estimator.IsSparse,
                "a new item did not convert an estimator holding as many hashes as it may");
        }

        /// <summary>
        /// Loaded past its retained size, so the fixture pins the sampled case rather
        /// than the exact one -- theta itself has to come back, or every later estimate
        /// is scaled wrongly.
        /// </summary>
        [TestMethod]
        public void TestStoredBloomierFilterStillReads()
        {
            var filter = BloomierFilter.ReadFrom(Fixture("bloomierfilter-v1.bin"));

            Assert.AreEqual(10u, filter.Count());
            Assert.AreEqual(8, filter.ValueBits());

            // Each word maps to its position, so a map that read back its cells but lost
            // its seed would answer with the wrong values rather than with none.
            for (var i = 0; i < Words.Length; i++)
            {
                Assert.IsTrue(filter.TryGetValue(Key(Words[i]), out var value), Words[i]);
                Assert.AreEqual((ulong)i, value, $"{Words[i]} came back with the wrong value");
            }
        }

        [TestMethod]
        public void TestStoredInvertibleBloomLookupTableStillReads()
        {
            var table = InvertibleBloomLookupTable.ReadFrom(Fixture("iblt-v1.bin"));

            Assert.AreEqual(15u, table.Cells());
            Assert.AreEqual(8, table.KeySize());

            // The keys come back out, which is the whole point -- a table that read back
            // its counts but not its xored keys would decode to nothing and look fine.
            Assert.IsTrue(table.TryDecode(out var held, out var owed));
            Assert.HasCount(6, held);
            Assert.HasCount(0, owed);
        }

        [TestMethod]
        public void TestStoredCountSketchStillReads()
        {
            var sketch = CountSketch.ReadFrom(Fixture("countsketch-v1.bin"));

            Assert.AreEqual(100u, sketch.Width());
            Assert.AreEqual(5u, sketch.Depth());

            // Signed cells, which is the thing this sketch stores that a Count-Min one
            // does not, so a payload read back as unsigned would come out wrong here.
            Assert.AreEqual(-500L, sketch.Count(Key("removed")));
            Assert.AreEqual(300L, sketch.Count(Key("alpha")));
        }

        [TestMethod]
        public void TestStoredHeavyKeeperStillReads()
        {
            var keeper = HeavyKeeper.ReadFrom(Fixture("heavykeeper-v1.bin"));

            Assert.AreEqual(200UL, keeper.N);
            Assert.HasCount(5, keeper.Elements());

            // Twelve flows through thirty-two buckets: the ones that held on read
            // back at or near their true counts, and flow-4 -- evicted from every
            // bucket by the contest -- reads back as zero, which is this structure's
            // honest answer for the evicted. A reader that lost the fingerprints
            // would zero everyone; one that lost the counters would zero no one.
            Assert.AreEqual(17UL, keeper.Count(Key("flow-0")));
            Assert.AreEqual(17UL, keeper.Count(Key("flow-7")));
            Assert.AreEqual(16UL, keeper.Count(Key("flow-11")));
            Assert.AreEqual(0UL, keeper.Count(Key("flow-4")));
            Assert.AreEqual(1UL, keeper.Count(Key("flow-5")));
        }

        [TestMethod]
        public void TestStoredMementoFilterStillReads()
        {
            var filter = MementoFilter.ReadFrom(Fixture("mementofilter-v1.bin"));

            Assert.AreEqual(800UL, filter.Count());
            Assert.AreEqual(64UL, filter.MaxRangeSize);
            Assert.AreEqual(1024UL, filter.Capacity());

            // Keys nine apart, so both the keys and the gaps between them are known.
            for (ulong i = 0; i < 800; i++)
            {
                Assert.IsTrue(filter.Test(i * 9),
                    $"stored filter no longer holds {i * 9}");
            }

            // The eight positions between the first two keys are empty, and the
            // mementos are exact, so the filter says so rather than guessing.
            Assert.IsFalse(filter.TestRange(1, 8),
                "The gap between the first two keys reads as occupied, which means " +
                "the mementos did not survive the round trip.");
        }

        [TestMethod]
        public void TestStoredInfiniFilterStillReads()
        {
            var filter = InfiniFilter.ReadFrom(Fixture("infinifilter-v1.bin"));

            Assert.AreEqual(2000UL, filter.Count());
            Assert.AreEqual(7u, filter.ExpansionCount());

            // Two tables: four-bit fingerprints send the oldest entries down the
            // chain, so this fixture pins the multi-table layout rather than only the
            // simple case.
            Assert.AreEqual(2, filter.ChainLength());
            Assert.AreEqual(4224UL, filter.Capacity());

            // Every item still answers, including those that were moved between
            // tables -- a reader that lost the second table would fail here rather
            // than merely reading something.
            for (var i = 0; i < 2000; i++)
            {
                Assert.IsTrue(filter.Test(Key($"item-{i}")),
                    $"stored filter no longer holds item-{i}");
            }
        }

        [TestMethod]
        public void TestStoredTupleSketchStillReads()
        {
            var sketch = TupleSketch.ReadFrom(Fixture("tuplesketch-v1.bin"));

            Assert.AreEqual(SummaryPolicy.Sum, sketch.Policy);
            Assert.AreEqual(308U, sketch.Retained());

            // Five thousand distinct keys, each added four times at two apiece, so the
            // total should come out at eight times the count.
            Assert.AreEqual(5_000.0, sketch.Count(), 5_000 * 0.15,
                "the stored sketch no longer estimates the keys put into it");
            Assert.AreEqual(sketch.Count() * 8.0, sketch.Total(), 8.0,
                "the stored sketch's summaries no longer match its keys");
        }

        /// <summary>
        /// The stored private sketch still reads, counters and all. The estimate is
        /// pinned exactly rather than to a tolerance: the bytes are fixed, so the
        /// noise in them is fixed, and the whole point of writing the counters rather
        /// than a seed is that the answer cannot move.
        /// </summary>
        /// <summary>
        /// Why the two private structures have a read fixture but no write fixture.
        /// <para>
        /// Their payloads are mostly Gaussian noise, and that noise is drawn through
        /// <c>Math.Log</c>, which .NET does not guarantee to be bit-identical across
        /// platforms -- only <c>Math.Sqrt</c> and plain arithmetic are correctly
        /// rounded. A byte-exact write fixture is therefore not a well-defined promise
        /// for them: the first version of this file had one, it passed on macOS, and
        /// it failed on Linux and Windows at a single counter, one unit apart in the
        /// low byte of a double.
        /// </para>
        /// <para>
        /// Reading is unaffected, which is why the read fixtures stay and are the
        /// direction that matters: loading a payload compares and adds stored doubles
        /// and touches no transcendental function, so a stored sketch answers
        /// identically everywhere. What the write side can still promise is the
        /// <i>layout</i> -- that the payload is exactly its header, shape and
        /// counters, with no room for anything else. That is pinned here by length,
        /// which is what would catch a field added, dropped, or a seed quietly
        /// written; and a field reordered without changing the length is caught by the
        /// read fixtures, which would then decode the old bytes into the wrong places.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestTheNoisyStructuresPinTheirLayoutRatherThanTheirBytes()
        {
            var priv = new PrivateCountMinSketch(64, 4, 0.5, seed: 20260818);
            for (var i = 0; i < 5000; i++)
            {
                priv.Add(Key($"item-{i % 250}"));
            }

            // 14 bytes of header, 4 of checksum; body is width, depth (4 each), rho,
            // count (8 each), then one double per counter.
            Assert.HasCount(
                14 + 4 + 4 + 4 + 8 + 8 + (64 * 4 * 8), priv.ToByteArray(),
                "the private sketch's payload is no longer exactly its header, shape " +
                "and counters, so something else is being written -- and the one " +
                "thing that must never be written is the seed.");

            var dpsw = new DpswSketch(
                window: 128, rho: 1.0, alpha: 0.5, width: 8, depth: 2, seed: 20260818);
            for (var i = 0; i < 256; i++)
            {
                dpsw.Add(Key($"item-{i % 20}"));
            }

            // Header is window, rho, alpha (8, 8, 8), substreamSize, width, depth
            // (4 each), position (8), then the substream count (4); each substream is
            // start (8) and held (4) followed by one sketch body per planned segment.
            var planLength = 1 + (2 * (dpsw.Checkpointing.Length - 1));
            var sketchBody = 4 + 4 + 8 + 8 + (8 * 2 * 8);
            var substreams = dpsw.SketchesHeld / planLength;

            Assert.AreEqual(dpsw.SketchesHeld, substreams * planLength,
                "the sketches held are not a whole number of plans, so the layout " +
                "asserted below is not the layout written.");
            Assert.HasCount(
                14 + 4 + 44 + 4 + (substreams * (12 + (planLength * sketchBody))),
                dpsw.ToByteArray(),
                "the window's payload is no longer exactly its header, its substreams " +
                "and their counters. A generator state written alongside them would " +
                "show up here.");
        }

        [TestMethod]
        public void TestStoredPrivateCountMinSketchStillReads()
        {
            var sketch = PrivateCountMinSketch.ReadFrom(
                Fixture("privatecountminsketch-v1.bin"));

            Assert.AreEqual(64U, sketch.Width);
            Assert.AreEqual(4U, sketch.Depth);
            Assert.AreEqual(0.5, sketch.Rho);
            Assert.AreEqual(5000UL, sketch.TotalCount());

            // Two hundred and fifty distinct keys, each added twenty times.
            Assert.AreEqual(60.99662491197003, sketch.Count(Key("item-0")),
                "the stored sketch no longer answers with the noise it was written " +
                "with, which means the counters are being read differently");
        }

        /// <summary>
        /// The stored window still reads, with its substreams, its checkpoint plan --
        /// which is recomputed on read rather than stored -- and its estimates. If the
        /// plan derivation ever changes, this fails, which is the point: the stored
        /// counters are laid out in plan order, so a different plan reads them into
        /// the wrong segments while the bytes stay perfectly valid.
        /// </summary>
        [TestMethod]
        public void TestStoredDpswSketchStillReads()
        {
            var sketch = DpswSketch.ReadFrom(Fixture("dpswsketch-v1.bin"));

            Assert.AreEqual(128L, sketch.Window);
            Assert.AreEqual(1.0, sketch.Rho);
            Assert.AreEqual(256L, sketch.Position);
            Assert.AreEqual(30, sketch.SubstreamSize);
            Assert.HasCount(6, sketch.Checkpointing);
            Assert.AreEqual(55, sketch.SketchesHeld);

            Assert.AreEqual(21.1711091838772, sketch.Count(Key("item-0")),
                "the stored window no longer answers as written");
            Assert.AreEqual(6.784935661383209, sketch.Count(Key("item-7")),
                "the stored window no longer answers as written");
        }

        [TestMethod]
        public void TestStoredSetSketchStillReads()
        {
            var sketch = SetSketch.ReadFrom(Fixture("setsketch-v1.bin"));

            Assert.AreEqual(512, sketch.Registers);
            Assert.AreEqual(1.01, sketch.Base);
            Assert.AreEqual(20.0, sketch.Rate);
            Assert.AreEqual(40_000, sketch.MaxRegisterValue);

            // Twenty thousand elements, held to the error 512 registers implies.
            Assert.AreEqual(20_000.0, sketch.Cardinality(), 20_000 * 0.15,
                "the stored sketch no longer estimates what was put into it");

            // And it still compares: a sketch of the same elements is identical to it.
            var rebuilt = new SetSketch(512, 1.01, 20, 40_000);
            for (var i = 0; i < 20_000; i++)
            {
                rebuilt.Add(Key($"item-{i}"));
            }
            Assert.AreEqual(1.0, sketch.Jaccard(rebuilt),
                "the stored sketch disagrees with one built the same way now");
        }

        /// <summary>
        /// The version 4 layout, which added the construction a sketch was built with.
        /// Hand-assembled from the version 1 fixture rather than written by the library,
        /// so reading it is held to the layout and not to whatever the writer happens to
        /// produce: the version 1 payload with the second construction's byte after the
        /// ceiling, a version of 4, a length one longer and a CRC to match. The registers
        /// are the ones the version 1 fixture holds, so both read as the same sketch but
        /// for the construction.
        /// </summary>
        [TestMethod]
        public void TestStoredSetSketchAtVersionFourStillReads()
        {
            var sketch = SetSketch.ReadFrom(Fixture("setsketch-v4.bin"));
            var first = SetSketch.ReadFrom(Fixture("setsketch-v1.bin"));

            Assert.AreEqual(SetSketchVariant.SetSketch2, sketch.Variant);
            Assert.AreEqual(SetSketchVariant.SetSketch1, first.Variant);

            Assert.AreEqual(512, sketch.Registers);
            Assert.AreEqual(1.01, sketch.Base);
            Assert.AreEqual(20.0, sketch.Rate);
            Assert.AreEqual(40_000, sketch.MaxRegisterValue);

            // The same registers, which a reader that mistook the added byte for the
            // first of them would shift by one: the estimate comes from them alone, and
            // is the one the version 1 fixture gives.
            Assert.AreEqual(first.Cardinality(), sketch.Cardinality(),
                "the stored sketch's registers were not read as the ones in the payload");
        }

        [TestMethod]
        public void TestStoredSublimeCountMinSketchStillReads()
        {
            var sketch = SublimeCountMinSketch.ReadFrom(
                Fixture("sublimecountminsketch-v1.bin"));

            Assert.AreEqual(128, sketch.Width);
            Assert.AreEqual(4, sketch.Depth);
            Assert.AreEqual(5500UL, sketch.TotalCount());

            // Forty flows, each added 150 times and the first five hundred additions
            // taken back out again.
            for (var i = 0; i < 40; i++)
            {
                Assert.IsTrue(sketch.Count(Key($"flow-{i}")) >= 137,
                    $"stored sketch reports {sketch.Count(Key($"flow-{i}"))} for " +
                    $"flow-{i}, below what was put in");
            }

            // The record of the expansion came back, so the sketch can still fold.
            for (var i = 0; i < 3000; i++)
            {
                sketch.Remove(Key($"flow-{i % 40}"));
            }
            Assert.AreEqual(64, sketch.Width,
                "the stored sketch no longer carries the record it needs to fold back");
        }

        [TestMethod]
        public void TestStoredGrafiteStillReads()
        {
            var filter = Grafite.ReadFrom(Fixture("grafite-v1.bin"));

            Assert.AreEqual(500UL, filter.Count());

            // Keys spaced 1009 apart, so the stored keys and the gaps between them
            // are both known exactly. A reader that lost the hash parameters, the
            // reduced universe or a word of the encoding would move these answers.
            Assert.IsTrue(filter.Test(0));
            Assert.IsTrue(filter.Test(1009));
            Assert.IsFalse(filter.Test(504500), "past the last key");
            Assert.IsFalse(filter.Test(1, 100), "a gap between two keys");
            Assert.IsTrue(filter.Test(2000, 2100), "a range holding the third key");

            // Not one of the 1008 non-keys below the second key reads as present, so
            // the filter's answers here are the keys themselves rather than noise.
            var positives = 0;
            for (ulong candidate = 1; candidate < 1009; candidate++)
            {
                if (filter.Test(candidate))
                {
                    positives++;
                }
            }
            Assert.AreEqual(0, positives);
        }

        [TestMethod]
        public void TestStoredUltraLogLogStillReads()
        {
            var sketch = UltraLogLog.ReadFrom(Fixture("ultraloglog-v1.bin"));

            Assert.AreEqual(8u, sketch.Precision());
            Assert.AreEqual(256u, sketch.M());

            // 10,000 distinct elements, estimated at this precision as 10,988. The
            // exact number is what matters here rather than its accuracy: it is a
            // function of every register in the payload, so a reader that dropped a
            // register, shifted the encoding, or mixed up the two low bits would
            // land somewhere else.
            Assert.AreEqual(10988UL, sketch.Count());
        }

        [TestMethod]
        public void TestStoredVarOptStillReads()
        {
            var sample = VarOpt.ReadFrom(Fixture("varopt-v1.bin"));

            Assert.AreEqual(202UL, sample.N);
            Assert.AreEqual(6u, sample.SampleCount);

            // The whole point of the structure is that this number is exact, so a
            // reader that rounded it, or that lost the threshold region's total,
            // would show up here rather than in a tolerance.
            Assert.AreEqual(1350.0, sample.TotalWeight, 0.0);

            // Two items outweighed the threshold and are held as themselves; the
            // other four share it. A reader that mixed the regions would report the
            // threshold for the whale, or 500 for a light item.
            var byName = sample.Samples().ToDictionary(
                e => Encoding.ASCII.GetString(e.Data.Span), e => e.Weight);

            Assert.AreEqual(500.0, byName["whale"], 0.0);
            Assert.AreEqual(250.0, byName["kraken"], 0.0);
            Assert.AreEqual(150.0, byName["flow-4"], 0.0);
            Assert.AreEqual(150.0, byName["flow-5"], 0.0);
            Assert.AreEqual(150.0, byName["flow-6"], 0.0);
            Assert.AreEqual(150.0, byName["flow-10"], 0.0);
        }

        /// <summary>
        /// A VarOpt sample that 6.2.0 wrote before it had begun sampling: six items
        /// with weights 3, 1, 2, 1, 3 and 2, held in the order they arrived, which is
        /// not a heap. The transition to sampling heaps them from that order, so the
        /// reader restores it as written rather than re-heaping it -- the 6.2.0 reader
        /// re-heaped it, and both the bytes and what was later sampled could change.
        /// </summary>
        [TestMethod]
        public void TestStoredExactVarOptStillReadsInArrivalOrder()
        {
            var stored = ReadFixture("varopt-exact-v1.bin");
            var sample = VarOpt.ReadFrom(new MemoryStream(stored));

            Assert.AreEqual(6UL, sample.N);
            Assert.AreEqual(6u, sample.SampleCount);
            Assert.AreEqual(12.0, sample.TotalWeight, 0.0);
            CollectionAssert.AreEqual(stored, sample.ToByteArray(),
                "the stored exact sample did not come back as it was written");

            // And it goes on as a sample that was never written out. The exact phase
            // draws nothing, so the same seed and the same six items rebuild the
            // state 6.2.0 saved, and the two must agree item by item from there.
            var neverSaved = ExactVarOpt();
            for (var i = 0; i < 30; i++)
            {
                var weight = 1.0 + (i % 3);
                sample.Add(Key($"later-{i}"), weight);
                neverSaved.Add(Key($"later-{i}"), weight);
                CollectionAssert.AreEqual(neverSaved.ToByteArray(), sample.ToByteArray(),
                    $"the stored sample diverged from one never written, {i + 1} items on");
            }
        }

        /// <summary>
        /// 6.2.0 could write a VarOpt payload its own reader refused: a sample that had
        /// begun sampling, merged into an empty one keeping the same number of items,
        /// was recorded as an exact sample of eight items that had seen twelve. The
        /// payload does not say which of its items stood in for dropped ones, so it
        /// cannot be restored without guessing -- the input it came from even held an
        /// item at exactly the threshold weight. It is refused, and the refusal says
        /// where the payload came from.
        /// </summary>
        [TestMethod]
        public void TestStoredVarOptFromA620MergeIsRefusedWithItsCause()
        {
            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => VarOpt.ReadFrom(Fixture("varopt-badmerge-v1.bin")));

            StringAssert.Contains(ex.Message, "6.2.0",
                "a refusal of a payload this library wrote should say why it exists");
        }

        [TestMethod]
        public void TestStoredSimHashSignatureStillReads()
        {
            var signature = SimHashSignature.ReadFrom(Fixture("simhashsignature-v1.bin"));

            // The fingerprint the current implementation computes for the same words, so
            // a change to the hash or the weighting fails here rather than in somebody's
            // stored index.
            Assert.AreEqual(SimHash.Signature(Words).Value, signature.Value);
            Assert.AreEqual(1f, SimHash.Similarity(signature, SimHash.Signature(Words)));
        }

        [TestMethod]
        public void TestStoredThetaSketchStillReads()
        {
            var sketch = ThetaSketch.ReadFrom(Fixture("thetasketch-v1.bin"));

            // It trims to 16 and then refills, so where it stops depends on the stream.
            Assert.AreEqual(30u, sketch.Retained());

            // 210 is what this sketch estimates for the 200 items it was given, and
            // pinning the estimate rather than the truth is the point: a theta that came
            // back wrong would scale this and nothing else would notice.
            Assert.AreEqual(210ul, sketch.Count());

            // And still combines with a sketch built now.
            var other = new ThetaSketch(16);
            for (var i = 100; i < 300; i++)
            {
                other.Add(Key($"w{i}"));
            }

            Assert.IsGreaterThan(0ul, sketch.Intersect(other).Count(),
                "the stored sketch shares nothing with a set it overlaps");
        }

        [TestMethod]
        public void TestStoredQuotientFilterStillReads()
        {
            var filter = QuotientFilter.ReadFrom(Fixture("quotientfilter-v1.bin"));

            Assert.AreEqual(10u, filter.Count());
            AssertAllPresent(filter.Test);

            // The metadata came back, not just the remainders: removing still finds the
            // right entry and leaves the rest of the cluster alone.
            Assert.IsTrue(filter.TestAndRemove(Key("alpha")));
            Assert.IsFalse(filter.Test(Key("alpha")));
            Assert.IsTrue(filter.Test(Key("beta")));
        }

        /// <summary>
        /// 6.0.0 through 6.2.0 sized a table for more items than a 31-bit quotient can
        /// index with a 32-bit one, and the slot count shifted to one slot. The payload
        /// such a filter wrote says 32 quotient bits and carries the one slot's bytes,
        /// which is the table it was, so it reads as that and goes on being the full
        /// one-slot filter it always was. The constructor no longer builds one; the
        /// bytes are the fixture, written by 6.2.0's constructor for 2,000,000,000 items
        /// at a rate of 1e-6 with "alpha" added, and they stay readable.
        /// </summary>
        [TestMethod]
        public void TestStoredQuotientFilterFromAnOversizedRequestStillReads()
        {
            var stored = ReadFixture("quotientfilter-q32-v1.bin");
            var filter = QuotientFilter.ReadFrom(Fixture("quotientfilter-q32-v1.bin"));

            Assert.AreEqual(1u, filter.Count());
            Assert.AreEqual(1u, filter.Capacity());
            Assert.IsTrue(filter.Test(Key("alpha")));

            // The remainder is wide enough that these absent keys, which were checked
            // against the bytes when the fixture was made, do not collide with it.
            for (var i = 0; i < 1000; i++)
            {
                Assert.IsFalse(filter.Test(Key($"absent-{i}")));
            }

            CollectionAssert.AreEqual(stored, filter.ToByteArray(),
                "the filter does not write back the bytes it was read from");

            // Still the one-slot table it was: full with one entry, usable once emptied.
            Assert.ThrowsExactly<InvalidOperationException>(() => filter.Add(Key("beta")));
            Assert.IsTrue(filter.TestAndRemove(Key("alpha")));
            filter.Add(Key("beta"));
            Assert.IsTrue(filter.Test(Key("beta")));
        }

        [TestMethod]
        public void TestStoredDDSketchStillReads()
        {
            var sketch = DDSketch.ReadFrom(Fixture("ddsketch-v1.bin"));

            Assert.AreEqual(100ul, sketch.Count());
            Assert.AreEqual(0.01, sketch.RelativeAccuracy());
            Assert.AreEqual(1.0, sketch.Min());
            Assert.AreEqual(100.0, sketch.Max());

            // The buckets came back, not just the totals: the median of 1..100 is 50,
            // within the accuracy the sketch was built with.
            Assert.IsLessThanOrEqualTo(0.01, Math.Abs(sketch.Quantile(0.5) - 50.0) / 50.0);
        }

        [TestMethod]
        public void TestStoredInverseBloomFilterStillReads()
        {
            var f = InverseBloomFilter.ReadFrom(Fixture("inversebloomfilter-v1.bin"));

            Assert.AreEqual(64u, f.Capacity());

            // Nine of the ten survived being written; the tenth was displaced by a
            // later word before the filter was ever stored, which is what this filter
            // does. Pinning the exact number keeps a restore that quietly loses one
            // more from passing.
            Assert.AreEqual(9, Words.Count(w => f.Test(Key(w))));
        }

        [TestMethod]
        public void TestStoredCuckooBloomFilterStillReads()
        {
            var f = CuckooBloomFilter.ReadFrom(Fixture("cuckoobloomfilter-v1.bin"));

            Assert.AreEqual(100u, f.Capacity());
            Assert.AreEqual(10u, f.Count());
            AssertAllPresent(f.Test);

            // Fingerprints landed where the relocation logic expects them, so it still
            // takes inserts.
            for (int i = 0; i < 50; i++)
            {
                f.Add(Key($"later{i}"));
            }
        }

        [TestMethod]
        public void TestStoredHyperLogLogStillReads()
        {
            var h = HyperLogLog.ReadFrom(Fixture("hyperloglog-v1.bin"));

            // b and alpha are derived on read rather than stored, so this pins that the
            // derivation still agrees with what wrote the registers.
            Assert.AreEqual(18ul, h.Count());
        }

        [TestMethod]
        public void TestStoredTopKStillReads()
        {
            var t = TopK.ReadFrom(Fixture("topk-v1.bin"));

            var elements = t.Elements()
                .Select(e => (Encoding.ASCII.GetString(e.Data.Span), e.Freq))
                .ToArray();

            CollectionAssert.AreEqual(
                new[] { ("t2", 3ul), ("t3", 4ul), ("t4", 5ul) }, elements);
        }

        [TestMethod]
        public void TestStoredMinHashSignatureStillReads()
        {
            var signature = MinHashSignature.ReadFrom(Fixture("minhashsignature-v1.bin"));

            Assert.AreEqual(16, signature.Length);
            Assert.AreEqual(31797598974978550ul, signature.Values[0]);
            Assert.AreEqual(661719492639586765ul, signature.Values[15]);

            // A stored signature has to still compare against one computed now, which
            // is the whole reason to store one.
            var recomputed = MinHash.Signature(
                new[] { "alpha", "beta", "gamma", "delta", "epsilon" }, 16);

            Assert.AreEqual(1.0f, MinHash.Similarity(signature, recomputed),
                "a signature stored by an earlier version no longer matches one " +
                "computed now for the same bag");
        }

        /// <summary>
        /// And that what this library writes today is still what it wrote then. The
        /// tests above would keep passing if writing changed and reading kept up with
        /// it; this pins the bytes themselves.
        /// <para>
        /// The stable and cuckoo filters are absent because neither is reproducible:
        /// one decrements randomly chosen cells and the other evicts a randomly chosen
        /// entry, from unseeded generators. Their payloads are still read above, which
        /// is the half of the promise that matters -- stored data keeps working.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestWritingStillProducesTheStoredBytes()
        {
            var bloom = new BloomFilter(100, 0.01);
            var bloom64 = new BloomFilter64(100, 0.01);
            var counting = new CountingBloomFilter(100, 4, 0.01);
            var deletable = new DeletableBloomFilter(100, 10, 0.01);
            var partitioned = new PartitionedBloomFilter(100, 0.01);
            var inverse = new InverseBloomFilter(64);

            // The plain filter's fixture holds the first five words.
            foreach (var word in Words.Take(5))
            {
                bloom.Add(Key(word));
            }

            foreach (var word in Words)
            {
                var key = Key(word);
                bloom64.Add(key);
                counting.Add(key);
                deletable.Add(key);
                partitioned.Add(key);
                inverse.Add(key);
            }

            AssertBytes("bloomfilter-v1.bin", bloom.ToByteArray());
            AssertBytes("bloomfilter64-v1.bin", bloom64.ToByteArray());
            AssertBytes("countingbloomfilter-v1.bin", counting.ToByteArray());
            AssertBytes("deletablebloomfilter-v1.bin", deletable.ToByteArray());
            AssertBytes("partitionedbloomfilter-v1.bin", partitioned.ToByteArray());
            AssertBytes("inversebloomfilter-v1.bin", inverse.ToByteArray());

            var scalable = new ScalableBloomFilter(20, 0.01, 0.8);
            for (int i = 0; i < 100; i++)
            {
                scalable.Add(Key($"s{i}"));
            }

            AssertBytes("scalablebloomfilter-v1.bin", scalable.ToByteArray());

            var hll = new HyperLogLog(64);
            for (int i = 0; i < 20; i++)
            {
                hll.Add(Key($"h{i}"));
            }

            AssertBytes("hyperloglog-v1.bin", hll.ToByteArray());

            var topK = new TopK(0.1, 0.5, 3);
            for (int i = 0; i < 5; i++)
            {
                for (int j = 0; j <= i; j++)
                {
                    topK.Add(Key($"t{i}"));
                }
            }

            AssertBytes("topk-v1.bin", topK.ToByteArray());

            AssertBytes("minhashsignature-v1.bin", MinHash.Signature(
                new[] { "alpha", "beta", "gamma", "delta", "epsilon" }, 16).ToByteArray());

            // Seeded, so the generator's stored position is fixed and the whole payload
            // is reproducible. Neither of these could be pinned before 6.0.0: their
            // generators were unseeded and nothing they did reached the payload.
            //
            // v5, not v2: the payload also records where the generator began, which is
            // what lets Reset return to it. The v2 fixture stays for the read-side
            // tests -- bytes 6.0.0 wrote must load forever -- and the v5 one was
            // assembled from it by hand, so this holds the writer to the layout
            // rather than to itself.
            var stable = new StableBloomFilter(1000, 2, 0.01, seed: 7);
            foreach (var word in Words)
            {
                stable.Add(Key(word));
            }

            AssertBytes("stablebloomfilter-v5.bin", stable.ToByteArray());

            // The build is deterministic -- a fixed seed sequence over a fixed set --
            // which is what lets its bytes be pinned at all.
            AssertBytes("binaryfusefilter-v1.bin",
                BinaryFuseFilter.Build(Words.Select(Key)).ToByteArray());

            var sketch = new DDSketch(0.01);
            for (var i = 1; i <= 100; i++)
            {
                sketch.Add(i);
            }

            AssertBytes("ddsketch-v1.bin", sketch.ToByteArray());

            var sparseHll = new HyperLogLogPlus(14);
            foreach (var word in Words)
            {
                sparseHll.Add(Key(word));
            }

            AssertBytes("hyperloglogplus-sparse-v1.bin", sparseHll.ToByteArray());

            // Precision 4 holding the most it may. The floor that keeps small counts
            // exact there, against the paper's rule, writes what 6.2.0 wrote.
            var smallestHll = new HyperLogLogPlus(4);
            for (var i = 0; i < 16; i++)
            {
                smallestHll.Add(Key($"w{i}"));
            }

            AssertBytes("hyperloglogplus-sparse-p4-v1.bin", smallestHll.ToByteArray());

            var quotient = new QuotientFilter(100, 0.01);
            foreach (var word in Words)
            {
                quotient.Add(Key(word));
            }

            AssertBytes("quotientfilter-v1.bin", quotient.ToByteArray());

            var theta = new ThetaSketch(16);
            for (var i = 0; i < 200; i++)
            {
                theta.Add(Key($"w{i}"));
            }

            // v1b, not v1: fixing the trim-boundary defect (an Add whose compaction
            // lowered theta past the pending hash still stored it) changed the state
            // this stream produces -- transient out-of-range values altered when later
            // compactions fired. Same format, second writer generation. The v1 fixture
            // stays for the read-side test: bytes 6.0.0 wrote must load forever.
            AssertBytes("thetasketch-v1b.bin", theta.ToByteArray());

            AssertBytes("simhashsignature-v1.bin", SimHash.Signature(Words).ToByteArray());

            var countSketch = new CountSketch(0.1, 0.01);
            countSketch.Add(Key("alpha"), 300);
            countSketch.Add(Key("removed"), -500);

            AssertBytes("countsketch-v1.bin", countSketch.ToByteArray());

            var keeper = new HeavyKeeper(5, 32, depth: 2, decay: 1.08, seed: 99);
            for (var i = 0; i < 200; i++)
            {
                keeper.Add(Key($"flow-{i % 12}"));
            }
            AssertBytes("heavykeeper-v1.bin", keeper.ToByteArray());

            var memento = new MementoFilter(
                maxRangeSize: 64, fingerprintBits: 8, initialCapacity: 16);
            for (ulong i = 0; i < 800; i++)
            {
                memento.Add(i * 9);
            }

            AssertBytes("mementofilter-v1.bin", memento.ToByteArray());

            var infini = new InfiniFilter(initialCapacity: 16, fingerprintBits: 4);
            for (var i = 0; i < 2000; i++)
            {
                infini.Add(Key($"item-{i}"));
            }

            AssertBytes("infinifilter-v1.bin", infini.ToByteArray());

            var tuple = new TupleSketch(256);
            for (var i = 0; i < 20_000; i++)
            {
                tuple.Add(Key($"user-{i % 5000}"), 2.0);
            }

            AssertBytes("tuplesketch-v1.bin", tuple.ToByteArray());

            var setSketch = new SetSketch(512, 1.01, 20, 40_000);
            for (var i = 0; i < 20_000; i++)
            {
                setSketch.Add(Key($"item-{i}"));
            }

            AssertBytes("setsketch-v1.bin", setSketch.ToByteArray());

            // The two private structures have no write fixture, deliberately. See
            // TestTheNoisyStructuresPinTheirLayoutRatherThanTheirBytes.


            var sublime = new SublimeCountMinSketch(0.02);
            for (var i = 0; i < 6000; i++)
            {
                sublime.Add(Key($"flow-{i % 40}"));
            }
            for (var i = 0; i < 500; i++)
            {
                sublime.Remove(Key($"flow-{i % 40}"));
            }

            AssertBytes("sublimecountminsketch-v1.bin", sublime.ToByteArray());

            AssertBytes("grafite-v1.bin", Grafite.Build(
                Enumerable.Range(0, 500).Select(i => (ulong)(i * 1009)),
                0.02, 32, seed: 2024).ToByteArray());

            var ultra = new UltraLogLog(8);
            for (var i = 0; i < 10000; i++)
            {
                ultra.Add(Key($"element-{i}"));
            }

            AssertBytes("ultraloglog-v1.bin", ultra.ToByteArray());

            var varopt = new VarOpt(6, seed: 99);
            for (var i = 0; i < 200; i++)
            {
                varopt.Add(Key($"flow-{i % 12}"), 1.0 + (i % 5));
            }
            varopt.Add(Key("whale"), 500.0);
            varopt.Add(Key("kraken"), 250.0);

            AssertBytes("varopt-v1.bin", varopt.ToByteArray());
            AssertBytes("varopt-exact-v1.bin", ExactVarOpt().ToByteArray());

            // Fifteen cells, stated outright: it is what ten differences were provisioned
            // when this fixture was written, and the fixture pins the writer's bytes at
            // that size whatever the sizing rule gives for ten today (#150).
            var iblt = InvertibleBloomLookupTable.WithCells(15, 8);
            for (var i = 0; i < 6; i++)
            {
                var key = new byte[8];
                key[0] = (byte)i;
                iblt.Add(key);
            }

            AssertBytes("iblt-v1.bin", iblt.ToByteArray());

            AssertBytes("bloomierfilter-v1.bin", BloomierFilter.Build(
                Words.Select((w, i) => new System.Collections.Generic.KeyValuePair<byte[], ulong>(Key(w), (ulong)i)),
                8).ToByteArray());

            var denseHll = new HyperLogLogPlus(14);
            for (var i = 0; i < 50000; i++)
            {
                denseHll.Add(Key($"n{i}"));
            }

            AssertBytes("hyperloglogplus-dense-v1.bin", denseHll.ToByteArray());

            // The load runs the relocation path, so the stored generator has moved from
            // its seed and the pin covers a generator that moved rather than one still
            // sitting at its start.
            var cuckoo = StoredCuckooTwin();

            // v6, not v3: the payload also records where the generator began, which is
            // what lets Reset return to it. The v2 and v3 fixtures stay for the
            // read-side tests -- bytes 6.0.1 and 6.1.0 wrote must load forever -- and
            // the v6 one was assembled from the v3 one by hand, so this holds the
            // writer to the layout rather than to itself.
            AssertBytes("cuckoobloomfilter-v6.bin", cuckoo.ToByteArray());
        }

        /// <summary>
        /// The filters placed with 64-bit values, as one of 2^27 bits or more is, write the
        /// version 8 payloads assembled by hand: the writer is held to the format and the
        /// restated scheme rather than to itself.
        /// </summary>
        [TestMethod]
        public void TestWritingAWideFilterProducesTheStoredVersionEightBytes()
        {
            var bloom = new BloomFilter(100, 0.01, null, Placement.Wide);
            var counting = new CountingBloomFilter(100, 4, 0.01, null, Placement.Wide);
            var deletable = new DeletableBloomFilter(100, 10, 0.01, null, Placement.Wide);
            var partitioned = new PartitionedBloomFilter(100, 0.01, null, Placement.Wide);
            foreach (var word in Words)
            {
                var key = Key(word);
                bloom.Add(key);
                counting.Add(key);
                deletable.Add(key);
                partitioned.Add(key);
            }

            AssertBytes("bloomfilter-v8.bin", bloom.ToByteArray());
            AssertBytes("countingbloomfilter-v8.bin", counting.ToByteArray());
            AssertBytes("deletablebloomfilter-v8.bin", deletable.ToByteArray());
            AssertBytes("partitionedbloomfilter-v8.bin", partitioned.ToByteArray());
            AssertBytes("stablebloomfilter-v8.bin", StoredWideStableTwin().ToByteArray());
        }

        /// <summary>
        /// The structure id in every stored payload is the one the format assigns, which
        /// is what stops a payload being read as the wrong structure. Reading the byte
        /// directly rather than through the enum, so that renumbering the enum shows up
        /// here rather than passing quietly.
        /// </summary>
        [TestMethod]
        public void TestStoredStructureIdsAreUnchanged()
        {
            var expected = new (string Fixture, int Id, int Version)[]
            {
                ("bloomfilter-v1.bin", 1, 1),
                ("bloomfilter64-v1.bin", 2, 1),
                ("countingbloomfilter-v1.bin", 3, 1),
                ("deletablebloomfilter-v1.bin", 4, 1),
                ("partitionedbloomfilter-v1.bin", 5, 1),
                ("scalablebloomfilter-v1.bin", 6, 1),
                ("stablebloomfilter-v1.bin", 7, 1),
                ("inversebloomfilter-v1.bin", 8, 1),
                ("cuckoobloomfilter-v1.bin", 9, 1),
                ("countminsketch-v1.bin", 10, 1),
                ("hyperloglog-v1.bin", 11, 1),
                ("topk-v1.bin", 12, 1),
                ("minhashsignature-v1.bin", 13, 1),
                ("stablebloomfilter-v2.bin", 7, 2),
                ("stablebloomfilter-v5.bin", 7, 5),
                ("bloomfilter-v8.bin", 1, 8),
                ("countingbloomfilter-v8.bin", 3, 8),
                ("deletablebloomfilter-v8.bin", 4, 8),
                ("partitionedbloomfilter-v8.bin", 5, 8),
                ("stablebloomfilter-v8.bin", 7, 8),
                ("cuckoobloomfilter-v2.bin", 9, 2),
                ("cuckoobloomfilter-v3.bin", 9, 3),
                ("cuckoobloomfilter-v6.bin", 9, 6),
                ("binaryfusefilter-v1.bin", 14, 1),
                ("ddsketch-v1.bin", 15, 1),
                ("hyperloglogplus-sparse-v1.bin", 16, 1),
                ("hyperloglogplus-dense-v1.bin", 16, 1),
                ("hyperloglogplus-sparse-p4-v1.bin", 16, 1),
                ("quotientfilter-v1.bin", 17, 1),
                ("quotientfilter-q32-v1.bin", 17, 1),
                ("thetasketch-v1.bin", 18, 1),
                ("thetasketch-v1b.bin", 18, 1),
                ("simhashsignature-v1.bin", 19, 1),
                ("countsketch-v1.bin", 20, 1),
                ("iblt-v1.bin", 21, 1),
                ("bloomierfilter-v1.bin", 22, 1),
                ("heavykeeper-v1.bin", 23, 1),
                ("varopt-v1.bin", 24, 1),
                ("varopt-exact-v1.bin", 24, 1),
                ("varopt-badmerge-v1.bin", 24, 1),
                ("ultraloglog-v1.bin", 25, 1),
                ("grafite-v1.bin", 26, 1),
                ("infinifilter-v1.bin", 27, 1),
                ("mementofilter-v1.bin", 28, 1),
            };

            foreach (var (fixture, id, version) in expected)
            {
                var bytes = ReadFixture(fixture);
                Assert.AreEqual(id, bytes[6] | (bytes[7] << 8),
                    $"{fixture} no longer carries structure id {id}");
                Assert.AreEqual(version, bytes[4] | (bytes[5] << 8),
                    $"{fixture} is no longer format version {version}");
            }

            // The version travels per payload, so the eleven structures that did not
            // change layout must still be writing version 1. Raising all of them
            // together would have made every payload unreadable to 5.x to record a
            // change that eleven of them did not have.
            Assert.AreEqual(1, new BloomFilter(100, 0.01).ToByteArray()[4],
                "an unchanged structure started writing a later format version");
            Assert.AreEqual(5, new StableBloomFilter(100, 2, 0.01).ToByteArray()[4],
                "the stable filter is not writing the version its layout needs");
            Assert.AreEqual(6, new CuckooBloomFilter(100, 0.01).ToByteArray()[4],
                "the cuckoo filter is not writing the version its layout needs");
        }

        /// <summary>
        /// The sample stored as varopt-exact-v1.bin, built the way 6.2.0 built it.
        /// </summary>
        private static VarOpt ExactVarOpt()
        {
            var sample = new VarOpt(8, seed: 148);
            var weights = new[] { 3.0, 1.0, 2.0, 1.0, 3.0, 2.0 };
            for (var i = 0; i < weights.Length; i++)
            {
                sample.Add(Key($"early-{i}"), weights[i]);
            }
            return sample;
        }

        private static void AssertAllPresent(Func<byte[], bool> test)
        {
            foreach (var word in Words)
            {
                Assert.IsTrue(test(Key(word)), $"the stored structure no longer finds {word}");
            }
        }

        private static void AssertBytes(string fixture, byte[] written)
        {
            CollectionAssert.AreEqual(ReadFixture(fixture), written,
                $"the bytes written for {fixture} have changed since the format was introduced");
        }

        private static Stream Fixture(string name)
        {
            return new MemoryStream(ReadFixture(name), writable: false);
        }

        private static byte[] ReadFixture(string name)
        {
            using var resource = typeof(TestPersistenceFormatStability).Assembly
                .GetManifestResourceStream($"TestProbabilisticDataStructures.fixtures.{name}")
                ?? throw new InvalidOperationException($"fixture {name} is not embedded");

            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            return buffer.ToArray();
        }
    }
}
