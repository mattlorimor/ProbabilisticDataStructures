using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
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
        /// Version 8 is each filter's earlier layout with a placement byte after the fields
        /// that size it -- 0 for 32-bit values, 1 for 64-bit ones -- and the item count,
        /// where there is one, widened to a u64. Every save of these filters is version 8.
        /// There are two fixtures of each, one placed each way, assembled by hand from
        /// FORMAT.md and the two schemes without the library, so that reading them holds
        /// the reader to the format and not to the writer. The 32-bit one holds what the
        /// filter's earlier fixture holds -- the plain filter's the first five words, the
        /// rest all ten -- so it is that payload saved again; the 64-bit one holds all ten.
        /// </summary>
        [TestMethod]
        public void TestStoredBloomFilterAtVersionEightStillReads()
        {
            foreach (var (fixture, placement, words) in Placed("bloomfilter", 5))
            {
                var f = BloomFilter.ReadFrom(Fixture(fixture));

                Assert.AreEqual(959u, f.Capacity());
                Assert.AreEqual(7u, f.K());
                Assert.AreEqual((ulong)words, (ulong)f.Count());
                Assert.AreEqual(placement, f.Placement);
                AssertAllPresent(f.Test, words);
                AssertLostWhenPlacedTheOtherWay(fixture, PlainPlacement, b => BloomFilter.ReadFrom(new MemoryStream(b)).Test, words);
            }
        }

        [TestMethod]
        public void TestStoredCountingBloomFilterAtVersionEightStillReads()
        {
            foreach (var (fixture, placement, words) in Placed("countingbloomfilter", 10))
            {
                var f = CountingBloomFilter.ReadFrom(Fixture(fixture));

                Assert.AreEqual(959u, f.Capacity());
                Assert.AreEqual(7u, f.K());
                Assert.AreEqual(10UL, (ulong)f.Count());
                Assert.AreEqual(placement, f.Placement);
                AssertAllPresent(f.Test, words);
                AssertLostWhenPlacedTheOtherWay(fixture, PlainPlacement, b => CountingBloomFilter.ReadFrom(new MemoryStream(b)).Test, words);

                // The counters came back where their scheme put them: each word removes
                // once and is gone, and the count goes down with it.
                Assert.IsTrue(f.TestAndRemove(Key("alpha")));
                Assert.IsFalse(f.Test(Key("alpha")));
                Assert.AreEqual(9UL, (ulong)f.Count());
            }
        }

        [TestMethod]
        public void TestStoredDeletableBloomFilterAtVersionEightStillReads()
        {
            foreach (var (fixture, placement, words) in Placed("deletablebloomfilter", 10))
            {
                var f = DeletableBloomFilter.ReadFrom(Fixture(fixture));

                Assert.AreEqual(949u, f.Capacity());
                Assert.AreEqual(7u, f.K());
                Assert.AreEqual(10UL, (ulong)f.Count());
                Assert.AreEqual(placement, f.Placement);
                AssertAllPresent(f.Test, words);
                AssertLostWhenPlacedTheOtherWay(fixture, PlainPlacement, b => DeletableBloomFilter.ReadFrom(new MemoryStream(b)).Test, words);
                Assert.IsTrue(f.TestAndRemove(Key("alpha")));
                Assert.AreEqual(9UL, (ulong)f.Count());
            }
        }

        [TestMethod]
        public void TestStoredPartitionedBloomFilterAtVersionEightStillReads()
        {
            foreach (var (fixture, placement, words) in Placed("partitionedbloomfilter", 10))
            {
                var f = PartitionedBloomFilter.ReadFrom(Fixture(fixture));

                Assert.AreEqual(959u, f.Capacity());
                Assert.AreEqual(7u, f.K());
                Assert.AreEqual(10UL, (ulong)f.Count());
                Assert.AreEqual(placement, f.Placement);
                AssertAllPresent(f.Test, words);
                AssertLostWhenPlacedTheOtherWay(fixture, PartitionedPlacement, b => PartitionedBloomFilter.ReadFrom(new MemoryStream(b)).Test, words);
            }
        }

        /// <summary>
        /// The stable filter's version 8 is its version 5 layout with the placement byte
        /// after <c>m</c>, <c>k</c> and <c>p</c>: it has no item count. Assembled by hand
        /// like the others, with the generator's draws and the decay restated as well,
        /// from the filter the version 5 fixture holds.
        /// </summary>
        [TestMethod]
        public void TestStoredStableBloomFilterAtVersionEightStillReads()
        {
            foreach (var (fixture, placement, words) in Placed("stablebloomfilter", 10))
            {
                var f = StableBloomFilter.ReadFrom(Fixture(fixture));

                Assert.AreEqual(1000u, f.Cells());
                Assert.AreEqual(3u, f.K());
                Assert.AreEqual(35u, f.P());
                Assert.AreEqual(placement, f.Placement);
                AssertAllPresent(f.Test, words);
                AssertLostWhenPlacedTheOtherWay(fixture, StablePlacement, b => StableBloomFilter.ReadFrom(new MemoryStream(b)).Test, words);

                // It resumes the sequence it was stored partway through, placing as it did...
                var twin = StoredStableTwin(placement);
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
                    new StableBloomFilter(1000, 2, 0.01, null, 7, placement).ToByteArray(), f.ToByteArray(),
                    "Reset did not return the restored filter to its stored original state");
            }
        }

        /// <summary>
        /// A payload written before version 8 reads, as it always did, and saves again as
        /// version 8 with its placement byte saying 32-bit: the version 1 fixtures, and the
        /// stable filter's version 5 one, save as the version 8 fixtures placed that way,
        /// byte for byte. The earlier fixtures are also exactly the version 8 ones with the
        /// placement byte taken out and the count narrowed, which is what the tests that
        /// build an earlier payload rely on.
        /// </summary>
        [TestMethod]
        public void TestAnEarlierPayloadSavesAgainAsVersionEight()
        {
            // The counting, deletable and stable filters save as version 14 now, which their
            // TestAnEarlier...PayloadSavesAgainAsVersionFourteen tests hold, so their rows have
            // no save: each version 8 fixture is still exactly the earlier one with the byte.
            var earlier = new (string Earlier, string Current, ushort Version, int Placement, int Count, Func<byte[], byte[]> Resave)[]
            {
                ("bloomfilter-v1.bin", "bloomfilter-v8-narrow.bin", 1, PlainPlacement, PlainPlacement + 1,
                    b => BloomFilter.ReadFrom(new MemoryStream(b)).ToByteArray()),
                ("countingbloomfilter-v1.bin", "countingbloomfilter-v8-narrow.bin", 1, PlainPlacement, PlainPlacement + 1, null),
                ("deletablebloomfilter-v1.bin", "deletablebloomfilter-v8-narrow.bin", 1, PlainPlacement, PlainPlacement + 1, null),
                ("partitionedbloomfilter-v1.bin", "partitionedbloomfilter-v8-narrow.bin", 1, PartitionedPlacement, PartitionedPlacement + 1,
                    b => PartitionedBloomFilter.ReadFrom(new MemoryStream(b)).ToByteArray()),
                ("stablebloomfilter-v5.bin", "stablebloomfilter-v8-narrow.bin", 5, StablePlacement, -1, null),
            };

            foreach (var (old, current, version, placement, count, resave) in earlier)
            {
                CollectionAssert.AreEqual(ReadFixture(old),
                    TestBitPlacement.Downgraded(ReadFixture(current), version, placement, count),
                    $"{current} without its placement byte, and with its count narrowed, is not {old}");
                if (resave is not null)
                {
                    AssertBytes(current, resave(ReadFixture(old)));
                }
            }
        }

        // Where the placement byte sits in each version 8 payload, after the envelope's
        // fourteen bytes: after u32 m and u32 k for the plain, counting and deletable
        // filters, and after a third u32 -- s, or p -- for the partitioned and stable ones.
        private const int PlainPlacement = 8;
        private const int PartitionedPlacement = 12;
        private const int StablePlacement = 12;

        // And in each version 14 payload: after u64 m and u32 k for the counting and
        // deletable filters, then u64 s for the partitioned one and u32 p for the stable one.
        private const int CountingPlacement14 = 12;
        private const int DeletablePlacement14 = 12;
        private const int StablePlacement14 = 16;
        private const int PartitionedPlacement14 = 20;

        /// <summary>
        /// The two version 14 fixtures of a filter, with what each was placed with and how
        /// many of the words it holds: its version 8 fixtures, re-laid by hand.
        /// </summary>
        private static (string Fixture, Placement Placement, int Words)[] PlacedAt14(string name, int narrowWords) => new[]
        {
            ($"{name}-v14-narrow.bin", Placement.Narrow, narrowWords),
            ($"{name}-v14-wide.bin", Placement.Wide, 10),
        };

        /// <summary>
        /// Version 14 is each filter's version 8 layout with its sizes widened to a u64 and
        /// its buckets written as a <c>Buckets64</c> (#232), which records a u64 count and
        /// its arrays, each a run of its own. Its fixtures are the version 8 ones re-laid by
        /// hand from FORMAT.md, outside the library, by a script that moves the fields and
        /// copies the packed bits, so they hold the filters the version 8 ones hold and the
        /// reader is held to the format rather than to the writer.
        /// </summary>
        [TestMethod]
        public void TestStoredCountingBloomFilterAtVersionFourteenStillReads()
        {
            foreach (var (fixture, placement, words) in PlacedAt14("countingbloomfilter", 10))
            {
                var f = CountingBloomFilter.ReadFrom(Fixture(fixture));

                Assert.AreEqual(959UL, (ulong)f.Capacity());
                Assert.AreEqual(7u, f.K());
                Assert.AreEqual(10UL, f.Count());
                Assert.AreEqual(placement, f.Placement);
                AssertAllPresent(f.Test, words);
                AssertLostWhenPlacedTheOtherWay(fixture, CountingPlacement14, b => CountingBloomFilter.ReadFrom(new MemoryStream(b)).Test, words);

                Assert.IsTrue(f.TestAndRemove(Key("alpha")));
                Assert.IsFalse(f.Test(Key("alpha")));
                Assert.AreEqual(9UL, f.Count());
            }
        }

        /// <summary>
        /// Every save of the counting filter is version 14: the version 1 fixture, and the
        /// version 8 ones placed each way, save again as the version 14 fixture placed as
        /// they were, byte for byte.
        /// </summary>
        [TestMethod]
        public void TestAnEarlierCountingPayloadSavesAgainAsVersionFourteen()
        {
            static byte[] Resave(byte[] b) => CountingBloomFilter.ReadFrom(new MemoryStream(b)).ToByteArray();

            AssertBytes("countingbloomfilter-v14-narrow.bin", Resave(ReadFixture("countingbloomfilter-v1.bin")));
            AssertBytes("countingbloomfilter-v14-narrow.bin", Resave(ReadFixture("countingbloomfilter-v8-narrow.bin")));
            AssertBytes("countingbloomfilter-v14-wide.bin", Resave(ReadFixture("countingbloomfilter-v8-wide.bin")));

            // And the version 14 fixtures narrowed back by FORMAT.md's two layouts, in C#
            // here rather than the script that made them, are the version 8 ones.
            foreach (var placed in new[] { "narrow", "wide" })
            {
                CollectionAssert.AreEqual(ReadFixture($"countingbloomfilter-v8-{placed}.bin"),
                    TestBitPlacement.Narrowed(ReadFixture($"countingbloomfilter-v14-{placed}.bin"), "mubqt"),
                    $"the version 14 {placed} fixture narrowed is not the version 8 one");
            }
        }

        /// <summary>
        /// The deletable filter's version 14 fixtures, re-laid by hand from its version 8
        /// ones as the counting filter's are, with both of its regions a <c>Buckets64</c>.
        /// </summary>
        [TestMethod]
        public void TestStoredDeletableBloomFilterAtVersionFourteenStillReads()
        {
            foreach (var (fixture, placement, words) in PlacedAt14("deletablebloomfilter", 10))
            {
                var f = DeletableBloomFilter.ReadFrom(Fixture(fixture));

                Assert.AreEqual(949UL, (ulong)f.Capacity());
                Assert.AreEqual(7u, f.K());
                Assert.AreEqual(10UL, (ulong)f.Count());
                Assert.AreEqual(placement, f.Placement);
                AssertAllPresent(f.Test, words);
                AssertLostWhenPlacedTheOtherWay(fixture, DeletablePlacement14, b => DeletableBloomFilter.ReadFrom(new MemoryStream(b)).Test, words);
                Assert.IsTrue(f.TestAndRemove(Key("alpha")));
                Assert.AreEqual(9UL, (ulong)f.Count());
            }
        }

        /// <summary>
        /// Every save of the deletable filter is version 14, as the counting filter's is.
        /// </summary>
        [TestMethod]
        public void TestAnEarlierDeletablePayloadSavesAgainAsVersionFourteen()
        {
            static byte[] Resave(byte[] b) => DeletableBloomFilter.ReadFrom(new MemoryStream(b)).ToByteArray();

            AssertBytes("deletablebloomfilter-v14-narrow.bin", Resave(ReadFixture("deletablebloomfilter-v1.bin")));
            AssertBytes("deletablebloomfilter-v14-narrow.bin", Resave(ReadFixture("deletablebloomfilter-v8-narrow.bin")));
            AssertBytes("deletablebloomfilter-v14-wide.bin", Resave(ReadFixture("deletablebloomfilter-v8-wide.bin")));

            foreach (var placed in new[] { "narrow", "wide" })
            {
                CollectionAssert.AreEqual(ReadFixture($"deletablebloomfilter-v8-{placed}.bin"),
                    TestBitPlacement.Narrowed(ReadFixture($"deletablebloomfilter-v14-{placed}.bin"), "mubqtt"),
                    $"the version 14 {placed} fixture narrowed is not the version 8 one");
            }
        }

        /// <summary>
        /// The stable filter's version 14 fixtures, re-laid by hand from its version 8 ones
        /// with m a <c>u64</c> and its cells a <c>Buckets64</c>. It reads, resumes the decay
        /// sequence it was stored partway through, and resets to its stored original state,
        /// as the version 8 fixture does.
        /// </summary>
        [TestMethod]
        public void TestStoredStableBloomFilterAtVersionFourteenStillReads()
        {
            foreach (var (fixture, placement, words) in PlacedAt14("stablebloomfilter", 10))
            {
                var f = StableBloomFilter.ReadFrom(Fixture(fixture));

                Assert.AreEqual(1000UL, (ulong)f.Cells());
                Assert.AreEqual(3u, f.K());
                Assert.AreEqual(35u, f.P());
                Assert.AreEqual(placement, f.Placement);
                AssertAllPresent(f.Test, words);
                AssertLostWhenPlacedTheOtherWay(fixture, StablePlacement14, b => StableBloomFilter.ReadFrom(new MemoryStream(b)).Test, words);

                var twin = StoredStableTwin(placement);
                for (var i = 0; i < 300; i++)
                {
                    f.Add(Key($"more{i}"));
                    twin.Add(Key($"more{i}"));
                }

                CollectionAssert.AreEqual(twin.ToByteArray(), f.ToByteArray(),
                    "the restored filter did not carry on the sequence it was stored partway through");
                f.Reset();
                CollectionAssert.AreEqual(
                    new StableBloomFilter(1000, 2, 0.01, null, 7, placement).ToByteArray(), f.ToByteArray(),
                    "Reset did not return the restored filter to its stored original state");
            }
        }

        /// <summary>
        /// Every save of the stable filter is version 14: the version 5 fixture, and the
        /// version 8 ones placed each way, save again as the version 14 fixture placed as
        /// they were, and the version 14 fixtures narrowed back are the version 8 ones.
        /// </summary>
        [TestMethod]
        public void TestAnEarlierStablePayloadSavesAgainAsVersionFourteen()
        {
            static byte[] Resave(byte[] b) => StableBloomFilter.ReadFrom(new MemoryStream(b)).ToByteArray();

            AssertBytes("stablebloomfilter-v14-narrow.bin", Resave(ReadFixture("stablebloomfilter-v5.bin")));
            AssertBytes("stablebloomfilter-v14-narrow.bin", Resave(ReadFixture("stablebloomfilter-v8-narrow.bin")));
            AssertBytes("stablebloomfilter-v14-wide.bin", Resave(ReadFixture("stablebloomfilter-v8-wide.bin")));

            foreach (var placed in new[] { "narrow", "wide" })
            {
                CollectionAssert.AreEqual(ReadFixture($"stablebloomfilter-v8-{placed}.bin"),
                    TestBitPlacement.Narrowed(ReadFixture($"stablebloomfilter-v14-{placed}.bin"), "muubtqq"),
                    $"the version 14 {placed} fixture narrowed is not the version 8 one");
            }
        }

        /// <summary>
        /// The partitioned filter's version 14 fixtures, re-laid by hand from its version 8
        /// ones with m and s <c>u64</c>s and each partition a <c>Buckets64</c>.
        /// </summary>
        [TestMethod]
        public void TestStoredPartitionedBloomFilterAtVersionFourteenStillReads()
        {
            foreach (var (fixture, placement, words) in PlacedAt14("partitionedbloomfilter", 10))
            {
                var f = PartitionedBloomFilter.ReadFrom(Fixture(fixture));

                Assert.AreEqual(959UL, (ulong)f.Capacity());
                Assert.AreEqual(7u, f.K());
                Assert.AreEqual(10UL, (ulong)f.Count());
                Assert.AreEqual(placement, f.Placement);
                AssertAllPresent(f.Test, words);
                AssertLostWhenPlacedTheOtherWay(fixture, PartitionedPlacement14, b => PartitionedBloomFilter.ReadFrom(new MemoryStream(b)).Test, words);
            }
        }

        /// <summary>
        /// Every save of the partitioned filter is version 14, and the version 14 fixtures
        /// narrowed back are the version 8 ones.
        /// </summary>
        [TestMethod]
        public void TestAnEarlierPartitionedPayloadSavesAgainAsVersionFourteen()
        {
            static byte[] Resave(byte[] b) => PartitionedBloomFilter.ReadFrom(new MemoryStream(b)).ToByteArray();

            AssertBytes("partitionedbloomfilter-v14-narrow.bin", Resave(ReadFixture("partitionedbloomfilter-v1.bin")));
            AssertBytes("partitionedbloomfilter-v14-narrow.bin", Resave(ReadFixture("partitionedbloomfilter-v8-narrow.bin")));
            AssertBytes("partitionedbloomfilter-v14-wide.bin", Resave(ReadFixture("partitionedbloomfilter-v8-wide.bin")));

            foreach (var placed in new[] { "narrow", "wide" })
            {
                CollectionAssert.AreEqual(ReadFixture($"partitionedbloomfilter-v8-{placed}.bin"),
                    TestBitPlacement.Narrowed(ReadFixture($"partitionedbloomfilter-v14-{placed}.bin"), "mumbqn"),
                    $"the version 14 {placed} fixture narrowed is not the version 8 one");
            }
        }

        /// <summary>
        /// The scalable filter's version 14 fixtures: its version 7 fixture, which grows by
        /// 2, and its version 1 one, which adds every filter at its hint, re-laid by hand
        /// with the hint a <c>u64</c>, the growth factor written even when it is 1, and each
        /// contained filter a partitioned filter at version 14. Each reads, finds what it
        /// held, and goes on growing as a filter that was never saved does. Both save again
        /// from their earlier fixtures as these.
        /// </summary>
        [TestMethod]
        public void TestStoredScalableBloomFilterAtVersionFourteenStillReads()
        {
            foreach (var (fixture, earlier, growth) in new[]
            {
                ("scalablebloomfilter-v14.bin", "scalablebloomfilter-v7.bin", 2u),
                ("scalablebloomfilter-v14-g1.bin", "scalablebloomfilter-v1.bin", 1u),
            })
            {
                var f = ScalableBloomFilter.ReadFrom(Fixture(fixture));
                Assert.AreEqual(growth, (uint)f.Growth, fixture);
                Assert.AreEqual(20UL, (ulong)f.Hint, fixture);
                for (int i = 0; i < 100; i++)
                {
                    Assert.IsTrue(f.Test(Key($"s{i}")), $"{fixture} no longer finds s{i}");
                }

                AssertBytes(fixture, ScalableBloomFilter.ReadFrom(Fixture(earlier)).ToByteArray());

                var twin = new ScalableBloomFilter(20, 0.01, 0.8, growth);
                for (int i = 0; i < 400; i++)
                {
                    if (i >= 100) f.Add(Key($"s{i}"));
                    twin.Add(Key($"s{i}"));
                }

                CollectionAssert.AreEqual(twin.ToByteArray(), f.ToByteArray(),
                    $"{fixture} grew differently from a filter that was never saved");
            }
        }

        /// <summary>
        /// The two version 8 fixtures of a filter, with what each was placed with and how
        /// many of the words it holds.
        /// </summary>
        private static (string Fixture, Placement Placement, int Words)[] Placed(string name, int narrowWords) => new[]
        {
            ($"{name}-v8-narrow.bin", Placement.Narrow, narrowWords),
            ($"{name}-v8-wide.bin", Placement.Wide, 10),
        };

        /// <summary>
        /// The stored payload with its placement byte saying the other scheme, and the
        /// checksum repaired, loses words: the filter looks for them where they were not
        /// put. That is what makes each fixture a witness to its placement and not only to
        /// its layout.
        /// </summary>
        private static void AssertLostWhenPlacedTheOtherWay(
            string fixture, int placementOffset, Func<byte[], Func<byte[], bool>> read, int words)
        {
            var bytes = ReadFixture(fixture);
            bytes[14 + placementOffset] ^= 1;
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(bytes.Length - 4),
                Crc32.HashToUInt32(bytes.AsSpan(4, bytes.Length - 8)));

            var test = read(bytes);
            var lost = Words.Take(words).Count(word => !test(Key(word)));
            Assert.IsGreaterThan(0, lost,
                $"{fixture} read as placed the other way still finds every word, so it cannot tell the placements apart");
        }

        /// <summary>
        /// A never-saved filter in the state the version 8 stable fixtures hold.
        /// </summary>
        private static StableBloomFilter StoredStableTwin(Placement placement)
        {
            var twin = new StableBloomFilter(1000, 2, 0.01, null, 7, placement);
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

            Assert.AreEqual(1053ul, f.Capacity());
            Assert.AreEqual(7u, f.K());
            var stored = f.Filters.Count;

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

            Assert.IsGreaterThan(1053ul, f.Capacity(), "the restored filter did not grow");

            // And it grows the way it always did. A version 1 payload carries no growth
            // factor because every filter then added its filters at the hint, so the
            // filters it adds after reading are sized for 20 items each -- not for the
            // default growth a new filter is given. The twin was built that way and
            // never saved; the two must agree byte for byte, and still write version 1.
            Assert.AreEqual(1u, f.Growth, "a version 1 payload read as a growing filter");
            Assert.IsGreaterThan(stored, f.Filters.Count,
                "no filter was added after reading, so nothing below is checked");
            for (int i = stored; i < f.Filters.Count; i++)
            {
                Assert.AreEqual(Utils.OptimalM(20, 0.01 * Math.Pow(0.8, i)), f.Filters[i].BitCount,
                    $"filter {i}, added after reading, is not sized for the hint");
            }

            var twin = new ScalableBloomFilter(20, 0.01, 0.8, 1);
            for (int i = 0; i < 400; i++)
            {
                twin.Add(Key($"s{i}"));
            }

            CollectionAssert.AreEqual(twin.ToByteArray(), f.ToByteArray(),
                "the restored filter grew differently from one that was never saved");
            Assert.AreEqual(1, f.ToByteArray()[4], "the restored filter no longer writes version 1");
        }

        /// <summary>
        /// Version 7 adds the growth factor, so a filter whose contained filters grow
        /// goes on growing by it after a save and a load. The fixture holds three filters
        /// sized for 20, 40 and 80 items -- the growth is in the payload as well as in
        /// the factor -- and the filters added after reading go on doubling.
        /// </summary>
        [TestMethod]
        public void TestStoredScalableBloomFilterAtVersionSevenStillReads()
        {
            var f = ScalableBloomFilter.ReadFrom(Fixture("scalablebloomfilter-v7.bin"));

            Assert.AreEqual(2u, f.Growth);
            Assert.AreEqual(20u, f.Hint);
            Assert.AreEqual(3, f.Filters.Count);
            CollectionAssert.AreEqual(new uint[] { 192, 402, 842 }, f.Filters.Select(x => x.BitCount).ToArray());
            CollectionAssert.AreEqual(new ulong[] { 20, 41, 39 }, f.Filters.Select(x => x.Count()).ToArray());

            for (int i = 0; i < 100; i++)
            {
                Assert.IsTrue(f.Test(Key($"s{i}")), $"the stored filter no longer finds s{i}");
            }

            for (int i = 100; i < 400; i++)
            {
                f.Add(Key($"s{i}"));
            }

            Assert.IsGreaterThan(3, f.Filters.Count, "nothing was added after reading, so nothing below was checked");
            for (int i = 0; i < f.Filters.Count; i++)
            {
                Assert.AreEqual(Utils.OptimalM(20u << i, 0.01 * Math.Pow(0.8, i)), f.Filters[i].BitCount,
                    $"filter {i} is not sized for 20*2^{i} items");
            }

            var twin = new ScalableBloomFilter(20, 0.01, 0.8, 2);
            for (int i = 0; i < 400; i++)
            {
                twin.Add(Key($"s{i}"));
            }

            CollectionAssert.AreEqual(twin.ToByteArray(), f.ToByteArray(),
                "the restored filter grew differently from one that was never saved");
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
            // Fifteen is one past the newest, version 14. It is written out rather than
            // computed from the library's ceiling, which is the thing being checked.
            var newest = new (string Fixture, Func<Stream, object> Read)[]
            {
                ("countingbloomfilter-v14-narrow.bin", s => CountingBloomFilter.ReadFrom(s)),
                ("deletablebloomfilter-v14-narrow.bin", s => DeletableBloomFilter.ReadFrom(s)),
                ("stablebloomfilter-v8-narrow.bin", s => StableBloomFilter.ReadFrom(s)),
                ("stablebloomfilter-v14-narrow.bin", s => StableBloomFilter.ReadFrom(s)),
                ("partitionedbloomfilter-v14-narrow.bin", s => PartitionedBloomFilter.ReadFrom(s)),
                ("scalablebloomfilter-v14.bin", s => ScalableBloomFilter.ReadFrom(s)),
                ("cuckoobloomfilter-v6.bin", s => CuckooBloomFilter.ReadFrom(s)),
                ("bloomfilter-v8-narrow.bin", s => BloomFilter.ReadFrom(s)),
                ("scalablebloomfilter-v7.bin", s => ScalableBloomFilter.ReadFrom(s)),
                ("countminsketch-v9-narrow.bin", s => CountMinSketch.ReadFrom(s)),
                ("privatecountminsketch-v9-narrow.bin", s => PrivateCountMinSketch.ReadFrom(s)),
                ("heavykeeper-v9-narrow.bin", s => HeavyKeeper.ReadFrom(s)),
                ("inversebloomfilter-v9-narrow.bin", s => InverseBloomFilter.ReadFrom(s)),
                ("dpswsketch-v9-narrow.bin", s => DpswSketch.ReadFrom(s)),
                ("sublimecountminsketch-v10.bin", s => SublimeCountMinSketch.ReadFrom(s)),
            };

            foreach (var (fixture, read) in newest)
            {
                var bytes = Relabeled(ReadFixture(fixture), 15);
                var ex = Assert.ThrowsExactly<InvalidDataException>(() => read(new MemoryStream(bytes)), fixture);
                StringAssert.Contains(ex.Message, "format version 15", fixture);
                StringAssert.Contains(ex.Message, "written by a later version", fixture);
            }
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
            Assert.AreEqual(Placement.Narrow, keeper.Placement);
        }

        // ---- Version 9: the sketches' placement byte ----------------------------------

        // Where the placement byte sits in each version 9 payload, after the envelope's
        // fourteen bytes: after f64 epsilon, f64 delta, u32 width and u32 depth for the
        // count-min sketch; after u32 width and u32 depth for the private sketch, whose
        // body a DPSW window holds inline; after u32 k, u32 width and u32 depth for
        // HeavyKeeper; and after u32 capacity for the inverse filter.
        private const int CountMinPlacement = 24;
        private const int PrivatePlacement = 8;
        private const int KeeperPlacement = 12;
        private const int InversePlacement = 4;

        /// <summary>
        /// The stored payload with the placement byte at <paramref name="payloadOffset"/>
        /// saying the other scheme, and the checksum repaired.
        /// </summary>
        private static byte[] PlacedTheOtherWay(string fixture, int payloadOffset)
        {
            var bytes = ReadFixture(fixture);
            bytes[14 + payloadOffset] ^= 1;
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(bytes.Length - 4),
                Crc32.HashToUInt32(bytes.AsSpan(4, bytes.Length - 8)));
            return bytes;
        }

        /// <summary>
        /// Version 9 is each sketch's version 1 layout with a placement byte after the
        /// fields that size it -- 0 for 32-bit values, 1 for 64-bit ones. Every save of the
        /// count-min sketches, HeavyKeeper, the inverse filter and a DPSW window is
        /// version 9. There are two fixtures of the count-min sketch, one placed each way,
        /// assembled by hand from FORMAT.md and the two schemes without the library, so that
        /// reading them holds the reader to the format and not to the writer. The 32-bit
        /// one is the version 1 fixture saved again; the 64-bit one holds the same stream,
        /// item{i} added i + 1 times, in the columns the 64-bit scheme gives at a width of
        /// 272, which is not a power of two. Read with its byte swapped, it counts none of
        /// it.
        /// </summary>
        [TestMethod]
        public void TestStoredCountMinSketchAtVersionNineStillReads()
        {
            foreach (var (fixture, placement) in new[]
                     { ("countminsketch-v9-narrow.bin", Placement.Narrow), ("countminsketch-v9-wide.bin", Placement.Wide) })
            {
                var sketch = CountMinSketch.ReadFrom(Fixture(fixture));

                Assert.AreEqual((272u, 5u), (sketch.Width, sketch.Depth), fixture);
                Assert.AreEqual(55ul, sketch.TotalCount(), fixture);
                Assert.AreEqual(0.01, sketch.Epsilon(), fixture);
                Assert.AreEqual(0.01, sketch.Delta(), fixture);
                Assert.AreEqual(placement, sketch.Placement, fixture);
                for (var i = 0; i < 10; i++)
                {
                    Assert.AreEqual((ulong)(i + 1), sketch.Count(Key($"item{i}")), $"{fixture} no longer counts item{i}");
                }

                var other = Persistence.FromByteArray<CountMinSketch>(PlacedTheOtherWay(fixture, CountMinPlacement));
                Assert.IsTrue(Enumerable.Range(0, 10).Any(i => other.Count(Key($"item{i}")) != (ulong)(i + 1)),
                    $"{fixture} read as placed the other way still counts every item, so it cannot tell the placements apart");
            }
        }

        /// <summary>
        /// The private sketch's version 9 is its version 1 body with the placement byte
        /// after the width and depth. The 32-bit fixture is the version 1 fixture saved
        /// again, and answers as that one does. The 64-bit one was assembled from it by
        /// hand: its noise is each stored counter less the count the 32-bit scheme put
        /// there, which comes back exactly, and the same stream counted where the 64-bit
        /// scheme puts it. The width is 64, a power of two, where the two schemes agree on
        /// the first row, so the answers placed the other way move only where another row
        /// sets them -- as they do for item-0.
        /// </summary>
        [TestMethod]
        public void TestStoredPrivateCountMinSketchAtVersionNineStillReads()
        {
            var narrow = PrivateCountMinSketch.ReadFrom(Fixture("privatecountminsketch-v9-narrow.bin"));
            var wide = PrivateCountMinSketch.ReadFrom(Fixture("privatecountminsketch-v9-wide.bin"));

            foreach (var sketch in new[] { narrow, wide })
            {
                Assert.AreEqual(64U, sketch.Width);
                Assert.AreEqual(4U, sketch.Depth);
                Assert.AreEqual(0.5, sketch.Rho);
                Assert.AreEqual(5000UL, sketch.TotalCount());
            }

            Assert.AreEqual(Placement.Narrow, narrow.Placement);
            Assert.AreEqual(60.99662491197003, narrow.Count(Key("item-0")),
                "the stored sketch saved again no longer answers as the version 1 one does");

            Assert.AreEqual(Placement.Wide, wide.Placement);
            Assert.AreEqual(18.383767869097625, wide.Count(Key("item-0")),
                "the stored sketch placed with 64-bit values no longer answers with the noise it was written with");
            Assert.AreEqual(81.37182146650557, wide.Count(Key("item-249")),
                "the stored sketch placed with 64-bit values no longer answers with the noise it was written with");

            var other = Persistence.FromByteArray<PrivateCountMinSketch>(
                PlacedTheOtherWay("privatecountminsketch-v9-wide.bin", PrivatePlacement));
            Assert.AreEqual(60.99662491197003, other.Count(Key("item-0")),
                "placed the other way, the 64-bit fixture should answer from the 32-bit columns, where item-0's " +
                "smallest counter is the one the version 1 fixture answers with");
        }

        /// <summary>
        /// HeavyKeeper's version 9 is its version 1 layout with the placement byte after
        /// k, the width and the depth. The 32-bit fixture is the version 1 fixture saved
        /// again, and answers as that one does. The 64-bit one was assembled by hand: ten
        /// tracked, two arrays of 52, decay 1.08, seed 99, the ten words fed round by round
        /// so that word i arrives i + 1 times, at a width where no bucket is contested, so
        /// no decay was drawn and the generator is at its seed. Every count is exact, and
        /// placed the other way it finds none of them.
        /// </summary>
        [TestMethod]
        public void TestStoredHeavyKeeperAtVersionNineStillReads()
        {
            var narrow = HeavyKeeper.ReadFrom(Fixture("heavykeeper-v9-narrow.bin"));
            Assert.AreEqual(Placement.Narrow, narrow.Placement);
            Assert.AreEqual(200UL, narrow.N);
            Assert.HasCount(5, narrow.Elements());
            Assert.AreEqual(17UL, narrow.Count(Key("flow-0")));
            Assert.AreEqual(16UL, narrow.Count(Key("flow-11")));
            Assert.AreEqual(0UL, narrow.Count(Key("flow-4")));

            var wide = HeavyKeeper.ReadFrom(Fixture("heavykeeper-v9-wide.bin"));
            Assert.AreEqual(Placement.Wide, wide.Placement);
            Assert.AreEqual(52u, wide.Width);
            Assert.AreEqual(55UL, wide.N);
            CollectionAssert.AreEqual(
                Words.Select((w, i) => (w, (ulong)(i + 1))).ToArray(),
                wide.Elements().Select(e => (Encoding.ASCII.GetString(e.Data.Span), e.Freq)).ToArray());
            for (var i = 0; i < Words.Length; i++)
            {
                Assert.AreEqual((ulong)(i + 1), wide.Count(Key(Words[i])), $"the stored structure no longer counts {Words[i]}");
            }

            var other = Persistence.FromByteArray<HeavyKeeper>(PlacedTheOtherWay("heavykeeper-v9-wide.bin", KeeperPlacement));
            Assert.IsTrue(Words.All(w => other.Count(Key(w)) == 0),
                "the 64-bit fixture read as placed the other way still finds a word, so it cannot tell the placements apart");
        }

        /// <summary>
        /// The inverse filter's version 9 is its version 1 layout with the placement byte
        /// after the capacity. The 32-bit fixture is the version 1 fixture saved again: a
        /// capacity of 64, a power of two, where the two schemes give every item the same
        /// slot, so it answers the same either way. The 64-bit one was assembled by hand: a
        /// capacity of 100 holding the ten words in the slots the 64-bit hash modulo 100
        /// gives, all different. Placed the other way it holds none of them.
        /// </summary>
        [TestMethod]
        public void TestStoredInverseBloomFilterAtVersionNineStillReads()
        {
            var narrow = InverseBloomFilter.ReadFrom(Fixture("inversebloomfilter-v9-narrow.bin"));
            Assert.AreEqual(64u, narrow.Capacity());
            Assert.AreEqual(Placement.Narrow, narrow.Placement);
            Assert.AreEqual(9, Words.Count(w => narrow.Test(Key(w))));

            var wide = InverseBloomFilter.ReadFrom(Fixture("inversebloomfilter-v9-wide.bin"));
            Assert.AreEqual(100u, wide.Capacity());
            Assert.AreEqual(Placement.Wide, wide.Placement);
            AssertAllPresent(wide.Test);

            var other = Persistence.FromByteArray<InverseBloomFilter>(
                PlacedTheOtherWay("inversebloomfilter-v9-wide.bin", InversePlacement));
            Assert.AreEqual(0, Words.Count(w => other.Test(Key(w))),
                "the 64-bit fixture read as placed the other way still holds a word, so it cannot tell the placements apart");
        }

        /// <summary>
        /// A DPSW window's version 9 is its version 1 layout with each private sketch body
        /// at version 9: the version 1 fixture saved again, with a placement byte saying
        /// 32-bit in each of its fifty-five bodies. It answers as the version 1 fixture
        /// does, so every body was read where it is.
        /// </summary>
        [TestMethod]
        public void TestStoredDpswSketchAtVersionNineStillReads()
        {
            var sketch = DpswSketch.ReadFrom(Fixture("dpswsketch-v9-narrow.bin"));

            Assert.AreEqual(128L, sketch.Window);
            Assert.AreEqual(256L, sketch.Position);
            Assert.AreEqual(55, sketch.SketchesHeld);
            Assert.IsTrue(sketch.Sketches().All(s => s.Placement == Placement.Narrow));
            Assert.AreEqual(21.1711091838772, sketch.Count(Key("item-0")),
                "the stored window saved again no longer answers as the version 1 one does");
            Assert.AreEqual(6.784935661383209, sketch.Count(Key("item-7")),
                "the stored window saved again no longer answers as the version 1 one does");
        }

        /// <summary>
        /// A top-k keeps its sketch in an envelope of its own, so its own layout did not
        /// change and it is still version 1. Its write-side fixture, v1b, is the v1 fixture
        /// with the sketch saved again at version 9, placed with 32-bit values, by the same
        /// program outside the library; it reads as v1 does.
        /// </summary>
        [TestMethod]
        public void TestStoredTopKWithItsSketchAtVersionNineStillReads()
        {
            var t = TopK.ReadFrom(Fixture("topk-v1b.bin"));

            CollectionAssert.AreEqual(
                new[] { ("t2", 3ul), ("t3", 4ul), ("t4", 5ul) },
                t.Elements().Select(e => (Encoding.ASCII.GetString(e.Data.Span), e.Freq)).ToArray());
            Assert.AreEqual(Placement.Narrow, t.Sketch.Placement);
        }

        /// <summary>
        /// A payload written before version 9 reads, as it always did, and saves again as
        /// version 9 with its placement byte saying 32-bit: each version 1 fixture saves as
        /// the version 9 fixture placed that way, byte for byte, and the top-k's as v1b. The
        /// earlier fixtures are also exactly the version 9 ones with the placement byte
        /// taken out, which is what the tests that build an earlier payload rely on. So a
        /// structure below the threshold counts in the cells it always did and writes the
        /// counters it always wrote: these are the bytes earlier releases wrote.
        /// </summary>
        [TestMethod]
        public void TestAnEarlierSketchPayloadSavesAgainAsVersionNine()
        {
            var earlier = new (string Earlier, string Current, int Placement, Func<byte[], byte[]> Resave)[]
            {
                ("countminsketch-v1.bin", "countminsketch-v9-narrow.bin", CountMinPlacement,
                    b => CountMinSketch.ReadFrom(new MemoryStream(b)).ToByteArray()),
                ("privatecountminsketch-v1.bin", "privatecountminsketch-v9-narrow.bin", PrivatePlacement,
                    b => PrivateCountMinSketch.ReadFrom(new MemoryStream(b)).ToByteArray()),
                ("heavykeeper-v1.bin", "heavykeeper-v9-narrow.bin", KeeperPlacement,
                    b => HeavyKeeper.ReadFrom(new MemoryStream(b)).ToByteArray()),
                ("inversebloomfilter-v1.bin", "inversebloomfilter-v9-narrow.bin", InversePlacement,
                    b => InverseBloomFilter.ReadFrom(new MemoryStream(b)).ToByteArray()),
            };

            foreach (var (old, current, placement, resave) in earlier)
            {
                CollectionAssert.AreEqual(ReadFixture(old), TestSketchPlacement.Downgraded(ReadFixture(current), placement),
                    $"{current} without its placement byte is not {old}");
                AssertBytes(current, resave(ReadFixture(old)));
            }

            AssertBytes("dpswsketch-v9-narrow.bin", DpswSketch.ReadFrom(Fixture("dpswsketch-v1.bin")).ToByteArray());
            AssertBytes("topk-v1b.bin", TopK.ReadFrom(Fixture("topk-v1.bin")).ToByteArray());
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

            // 14 bytes of header, 4 of checksum; body is width, depth (4 each), the
            // placement byte, rho, count (8 each), then one double per counter.
            Assert.HasCount(
                14 + 4 + 4 + 4 + 1 + 8 + 8 + (64 * 4 * 8), priv.ToByteArray(),
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
            var sketchBody = 4 + 4 + 1 + 8 + 8 + (8 * 2 * 8);
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

        /// <summary>
        /// Version 10 stores, before each state's counters, the tuning a read packs them at
        /// (#280). Its fixture was assembled outside the library, from FORMAT.md alone: the
        /// version 1 fixture's payload with a byte for the counters to a chunk and a byte for
        /// the stub width before the record's counters and before the rows', each worked out
        /// from that state's counters by the rule the Sublime section states. So it reads as
        /// the sketch the version 1 fixture holds, packed at the tuning it stores, and the
        /// version 1 fixture saves again as it, byte for byte; and without its tuning it is
        /// the version 1 fixture.
        /// </summary>
        [TestMethod]
        public void TestStoredSublimeCountMinSketchAtVersionTenReadsAsTheOneBeforeIt()
        {
            var stored = ReadFixture("sublimecountminsketch-v10.bin");
            var earlier = ReadFixture("sublimecountminsketch-v1.bin");
            var sketch = SublimeCountMinSketch.ReadFrom(new MemoryStream(stored));
            var before = SublimeCountMinSketch.ReadFrom(new MemoryStream(earlier));

            Assert.AreEqual(before.Width, sketch.Width);
            Assert.AreEqual(before.Depth, sketch.Depth);
            Assert.AreEqual(before.TotalCount(), sketch.TotalCount());
            for (var i = 0; i < 40; i++)
            {
                Assert.AreEqual(before.Count(Key($"flow-{i}")), sketch.Count(Key($"flow-{i}")),
                    $"flow-{i} is counted differently at version 10");
            }

            // The rows' tuning is the two bytes before their counters, the last thing in
            // the payload.
            var rowsTuning = stored.Length - 4 - 8 * sketch.Depth * sketch.Width - 2;
            Assert.AreEqual((stored[rowsTuning], stored[rowsTuning + 1]),
                ((byte)sketch.CountersPerChunk, (byte)sketch.StubBits),
                "the rows were not packed at the tuning the payload stores");

            // One record of 64 counters a row, four rows: its tuning follows its width.
            const int RecordTuning = 14 + 3 * 8 + 2 * 4 + 3 * 8 + 4 + 4;
            var withoutTuning = stored[14..RecordTuning]
                .Concat(stored[(RecordTuning + 2)..rowsTuning])
                .Concat(stored[(rowsTuning + 2)..^4])
                .ToArray();
            CollectionAssert.AreEqual(earlier[14..^4], withoutTuning,
                "the version 10 fixture without its tuning is not the version 1 fixture's payload");

            AssertBytes("sublimecountminsketch-v10.bin", before.ToByteArray());
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

        /// <summary>
        /// 6.2.0 and every release before it could build a bucket array for a filter
        /// whose bits came to 2^32 or more, with a byte count worked out in 32 bits and
        /// wrapped: a table of a few kilobytes, or none, for a filter of hundreds of
        /// megabytes. The filter's adds threw wherever they landed past the table, after
        /// setting what lay before it. The three stored payloads are what the
        /// constructors wrote for 448,089,842 items at 1%, for 56,011,471 items of
        /// eight-bit counters at 1%, and for 1,431,655,763 three-bit stable cells, each
        /// empty because an add to one could not be relied on to finish.
        /// <para>
        /// The table is not the filter, and the payload does not say which of its
        /// buckets were lost, so reading it would be guessing. It is refused, and the
        /// refusal says where the payload came from.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestStoredFiltersWhoseBucketBytesWrappedAreRefusedWithTheirCause()
        {
            var stored = new (string Name, Func<Stream, object> Read)[]
            {
                ("bloomfilter-wrapped-v1.bin", s => BloomFilter.ReadFrom(s)),
                ("countingbloomfilter-wrapped-v1.bin", s => CountingBloomFilter.ReadFrom(s)),
                ("stablebloomfilter-wrapped-v5.bin", s => StableBloomFilter.ReadFrom(s)),
            };

            foreach (var (name, read) in stored)
            {
                var ex = Assert.ThrowsExactly<InvalidDataException>(
                    () => read(Fixture(name)), name);

                StringAssert.Contains(ex.Message, "32 bits",
                    $"the refusal of {name} should say why the table is short");
            }
        }

        /// <summary>
        /// 6.2.0 and every release before it built a Memento filter whose slots are 64
        /// bits -- a 32-bit range with a 28-bit fingerprint, say -- with a mask that
        /// shifted a ulong by 64 and so came to zero. Every key it was given was counted and
        /// none stored. The stored payload is that filter after 200 keys: it says 200 are
        /// held, in a table whose every word is zero. It answers no to all of them.
        /// <para>
        /// Nothing in it can be recovered, and reading it would hand back a filter that
        /// claims to hold keys and says it does not, which is the one thing a filter must not
        /// do. It is refused, and the refusal says where the payload came from.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestStoredMementoFilterThatLostEveryKeyIsRefusedWithItsCause()
        {
            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => MementoFilter.ReadFrom(Fixture("mementofilter-lostkeys-v1.bin")));

            StringAssert.Contains(ex.Message, "mask",
                "the refusal should say why the table is empty");
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
            Assert.AreEqual(Placement.Narrow, f.Placement);
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

            AssertBytes("bloomfilter-v8-narrow.bin", bloom.ToByteArray());
            AssertBytes("bloomfilter64-v1.bin", bloom64.ToByteArray());
            AssertBytes("countingbloomfilter-v14-narrow.bin", counting.ToByteArray());
            AssertBytes("deletablebloomfilter-v14-narrow.bin", deletable.ToByteArray());
            AssertBytes("partitionedbloomfilter-v14-narrow.bin", partitioned.ToByteArray());

            // Version 9 since the sketches and the inverse filter began recording their
            // placement; the version 1 fixture is read, above.
            AssertBytes("inversebloomfilter-v9-narrow.bin", inverse.ToByteArray());

            // Every save of a scalable filter is version 14 (#232), with its hint a u64 and
            // its growth factor written even when it is 1, and its contained filters are
            // partitioned filters at version 14 in envelopes of their own. A filter that
            // adds every filter at its hint is pinned to v14-g1, the v1 fixture re-laid by
            // hand outside the library; the v1 fixture is read, above.
            var scalable = new ScalableBloomFilter(20, 0.01, 0.8, 1);
            for (int i = 0; i < 100; i++)
            {
                scalable.Add(Key($"s{i}"));
            }

            AssertBytes("scalablebloomfilter-v14-g1.bin", scalable.ToByteArray());

            // One that grows is pinned to v14, the v7 fixture re-laid the same way. The v7
            // fixture was assembled from FORMAT.md's layout outside the library's writer --
            // the outer fields by hand, each contained filter built for 20*2^i items
            // directly -- so this holds the writer to the layout rather than to itself.
            var growing = new ScalableBloomFilter(20, 0.01, 0.8, 2);
            for (int i = 0; i < 100; i++)
            {
                growing.Add(Key($"s{i}"));
            }

            AssertBytes("scalablebloomfilter-v14.bin", growing.ToByteArray());

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

            // v1b, not v1: the top-k's own layout is unchanged, but its sketch is in an
            // envelope of its own, which is version 9 since the count-min sketch began
            // recording its placement. v1b is the v1 fixture with the sketch rewritten at
            // version 9 by hand, outside the library; the v1 fixture is read, above.
            AssertBytes("topk-v1b.bin", topK.ToByteArray());

            AssertBytes("minhashsignature-v1.bin", MinHash.Signature(
                new[] { "alpha", "beta", "gamma", "delta", "epsilon" }, 16).ToByteArray());

            // Seeded, so the generator's stored position is fixed and the whole payload
            // is reproducible. Neither of these could be pinned before 6.0.0: their
            // generators were unseeded and nothing they did reached the payload.
            //
            // v8, not v5 or v2: the payload also records where the generator began, which
            // is what lets Reset return to it, and from v8 how the cells were placed. The
            // v2 and v5 fixtures stay for the read-side tests -- bytes 6.0.0 and its
            // successors wrote must load forever -- and the v8 one was assembled by hand,
            // so this holds the writer to the layout rather than to itself. The plain,
            // counting, deletable and partitioned filters above are pinned to their v8
            // fixtures for the same reason, and their v1 fixtures are read.
            var stable = new StableBloomFilter(1000, 2, 0.01, seed: 7);
            foreach (var word in Words)
            {
                stable.Add(Key(word));
            }

            AssertBytes("stablebloomfilter-v14-narrow.bin", stable.ToByteArray());

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
            AssertBytes("heavykeeper-v9-narrow.bin", keeper.ToByteArray());

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

            // v10, not v1: the payload also stores each state's tuning (#280). The v1
            // fixture is what 6.2.0 wrote, and is read by TestStoredSublimeCountMinSketchStillReads.
            AssertBytes("sublimecountminsketch-v10.bin", sublime.ToByteArray());

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

            AssertBytes("bloomfilter-v8-wide.bin", bloom.ToByteArray());
            AssertBytes("countingbloomfilter-v14-wide.bin", counting.ToByteArray());
            AssertBytes("deletablebloomfilter-v14-wide.bin", deletable.ToByteArray());
            AssertBytes("partitionedbloomfilter-v14-wide.bin", partitioned.ToByteArray());
            AssertBytes("stablebloomfilter-v14-wide.bin", StoredStableTwin(Placement.Wide).ToByteArray());
        }

        /// <summary>
        /// The sketches placed with 64-bit values, as one 2^27 columns wide or more is, write
        /// the version 9 payloads assembled by hand: the writer is held to the format and the
        /// restated scheme rather than to itself. The private sketch has no write fixture,
        /// for the reason <see cref="TestTheNoisyStructuresPinTheirLayoutRatherThanTheirBytes"/>
        /// gives.
        /// </summary>
        [TestMethod]
        public void TestWritingAWideSketchProducesTheStoredVersionNineBytes()
        {
            var sketch = new CountMinSketch(0.01, 0.01, null, Placement.Wide);
            for (var i = 0; i < 10; i++)
            {
                for (var j = 0; j <= i; j++)
                {
                    sketch.Add(Key($"item{i}"));
                }
            }

            AssertBytes("countminsketch-v9-wide.bin", sketch.ToByteArray());

            var keeper = new HeavyKeeper(10, 52, 2, 1.08, 99, null, Placement.Wide);
            for (var round = 0; round < Words.Length; round++)
            {
                for (var i = round; i < Words.Length; i++)
                {
                    keeper.Add(Key(Words[i]));
                }
            }

            AssertBytes("heavykeeper-v9-wide.bin", keeper.ToByteArray());

            var inverse = new InverseBloomFilter(100, null, Placement.Wide);
            foreach (var word in Words)
            {
                inverse.Add(Key(word));
            }

            AssertBytes("inversebloomfilter-v9-wide.bin", inverse.ToByteArray());
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
                ("scalablebloomfilter-v1b.bin", 6, 1),
                ("scalablebloomfilter-v7.bin", 6, 7),
                ("scalablebloomfilter-v7b.bin", 6, 7),
                ("scalablebloomfilter-v14.bin", 6, 14),
                ("scalablebloomfilter-v14-g1.bin", 6, 14),
                ("stablebloomfilter-v1.bin", 7, 1),
                ("inversebloomfilter-v1.bin", 8, 1),
                ("cuckoobloomfilter-v1.bin", 9, 1),
                ("countminsketch-v1.bin", 10, 1),
                ("hyperloglog-v1.bin", 11, 1),
                ("topk-v1.bin", 12, 1),
                ("minhashsignature-v1.bin", 13, 1),
                ("stablebloomfilter-v2.bin", 7, 2),
                ("stablebloomfilter-v5.bin", 7, 5),
                ("bloomfilter-v8-narrow.bin", 1, 8),
                ("bloomfilter-v8-wide.bin", 1, 8),
                ("countingbloomfilter-v8-narrow.bin", 3, 8),
                ("countingbloomfilter-v8-wide.bin", 3, 8),
                ("deletablebloomfilter-v8-narrow.bin", 4, 8),
                ("deletablebloomfilter-v8-wide.bin", 4, 8),
                ("deletablebloomfilter-v14-narrow.bin", 4, 14),
                ("deletablebloomfilter-v14-wide.bin", 4, 14),
                ("partitionedbloomfilter-v8-narrow.bin", 5, 8),
                ("partitionedbloomfilter-v8-wide.bin", 5, 8),
                ("partitionedbloomfilter-v14-narrow.bin", 5, 14),
                ("partitionedbloomfilter-v14-wide.bin", 5, 14),
                ("stablebloomfilter-v8-narrow.bin", 7, 8),
                ("stablebloomfilter-v8-wide.bin", 7, 8),
                ("stablebloomfilter-v14-narrow.bin", 7, 14),
                ("stablebloomfilter-v14-wide.bin", 7, 14),
                ("countingbloomfilter-v14-narrow.bin", 3, 14),
                ("countingbloomfilter-v14-wide.bin", 3, 14),
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
                ("mementofilter-lostkeys-v1.bin", 28, 1),
                ("bloomfilter-wrapped-v1.bin", 1, 1),
                ("countingbloomfilter-wrapped-v1.bin", 3, 1),
                ("stablebloomfilter-wrapped-v5.bin", 7, 5),
                ("ultraloglog-v1.bin", 25, 1),
                ("grafite-v1.bin", 26, 1),
                ("infinifilter-v1.bin", 27, 1),
                ("mementofilter-v1.bin", 28, 1),
                ("countminsketch-v9-narrow.bin", 10, 9),
                ("countminsketch-v9-wide.bin", 10, 9),
                ("privatecountminsketch-v1.bin", 32, 1),
                ("privatecountminsketch-v9-narrow.bin", 32, 9),
                ("privatecountminsketch-v9-wide.bin", 32, 9),
                ("heavykeeper-v9-narrow.bin", 23, 9),
                ("heavykeeper-v9-wide.bin", 23, 9),
                ("inversebloomfilter-v9-narrow.bin", 8, 9),
                ("inversebloomfilter-v9-wide.bin", 8, 9),
                ("dpswsketch-v1.bin", 33, 1),
                ("dpswsketch-v9-narrow.bin", 33, 9),
                ("topk-v1b.bin", 12, 1),
            };

            foreach (var (fixture, id, version) in expected)
            {
                var bytes = ReadFixture(fixture);
                Assert.AreEqual(id, bytes[6] | (bytes[7] << 8),
                    $"{fixture} no longer carries structure id {id}");
                Assert.AreEqual(version, bytes[4] | (bytes[5] << 8),
                    $"{fixture} is no longer format version {version}");
            }

            // The version travels per payload, so a structure whose layout did not change
            // goes on writing version 1. Raising all of them together would have made every
            // payload unreadable to 5.x to record a change most of them did not have. The
            // five 32-bit Bloom filters did change: every save of theirs is version 8,
            // which records their placement and a 64-bit count.
            Assert.AreEqual(1, new BloomFilter64(100, 0.01).ToByteArray()[4],
                "an unchanged structure started writing a later format version");
            Assert.AreEqual(8, new BloomFilter(100, 0.01).ToByteArray()[4],
                "a small BloomFilter is not writing the version its layout needs");
            Assert.AreEqual(14, new StableBloomFilter(100, 2, 0.01).ToByteArray()[4],
                "the stable filter is not writing the version its layout needs");
            Assert.AreEqual(6, new CuckooBloomFilter(100, 0.01).ToByteArray()[4],
                "the cuckoo filter is not writing the version its layout needs");

            // The sketches that place by width record their placement from version 9, and
            // every save of theirs is version 9. The top-k keeps its sketch in an envelope of
            // its own, so its own version stays 1.
            Assert.AreEqual(9, new CountMinSketch(0.1, 0.1).ToByteArray()[4],
                "a small CountMinSketch is not writing the version its layout needs");
            Assert.AreEqual(9, new PrivateCountMinSketch(16, 2, 1.0, seed: 1).ToByteArray()[4],
                "a small PrivateCountMinSketch is not writing the version its layout needs");
            Assert.AreEqual(9, new HeavyKeeper(2, 16).ToByteArray()[4],
                "a small HeavyKeeper is not writing the version its layout needs");
            Assert.AreEqual(9, new InverseBloomFilter(16).ToByteArray()[4],
                "a small InverseBloomFilter is not writing the version its layout needs");
            Assert.AreEqual(9, new DpswSketch(128, 1.0, width: 8, depth: 2, seed: 1).Add(Key("a")).ToByteArray()[4],
                "a DpswSketch is not writing the version its layout needs");
            Assert.AreEqual(1, new TopK(0.1, 0.1, 3).ToByteArray()[4],
                "a top-k, whose layout did not change, started writing a later format version");

            // The scalable filter writes version 7 only when it has a growth factor to
            // record, so one that adds every filter at its hint stays readable by
            // libraries that predate the choice.
            Assert.AreEqual(1, new ScalableBloomFilter(100, 0.01, 0.8, 1).ToByteArray()[4],
                "a scalable filter that does not grow started writing a later format version");
            Assert.AreEqual(7, new ScalableBloomFilter(100, 0.01, 0.8, 2).ToByteArray()[4],
                "a scalable filter that grows is not writing the version its layout needs");
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

        private static void AssertAllPresent(Func<byte[], bool> test) => AssertAllPresent(test, Words.Length);

        private static void AssertAllPresent(Func<byte[], bool> test, int words)
        {
            foreach (var word in Words.Take(words))
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

        internal static byte[] ReadFixture(string name)
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
