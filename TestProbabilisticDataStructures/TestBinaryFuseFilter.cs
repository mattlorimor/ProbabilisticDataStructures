using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The binary fuse filter's contract, which differs from every other filter here in
    /// one way that shapes all of it: the set is fixed at construction. There is no Add.
    /// </summary>
    [TestClass]
    public class TestBinaryFuseFilter
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        private static byte[][] Keys(int count, string prefix = "k") =>
            Enumerable.Range(0, count).Select(i => Key($"{prefix}{i}")).ToArray();

        /// <summary>
        /// The one absolute promise. A false positive is allowed and a false negative is
        /// not: the structure would be unusable as a filter if a member could answer no.
        /// <para>
        /// Run across a range of sizes because the construction's shape changes with
        /// them -- the segment length is derived from the set size, and small sets take
        /// paths that large ones do not.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestEveryMemberIsFound()
        {
            foreach (var size in new[] { 1, 2, 3, 7, 8, 9, 100, 1000, 10000, 50000 })
            {
                var items = Keys(size);
                var filter = BinaryFuseFilter.Build(items);

                foreach (var item in items)
                {
                    Assert.IsTrue(filter.Test(item),
                        $"a filter of {size} items did not find one of its own members");
                }
            }
        }

        /// <summary>
        /// Construction is peeling-based and can fail, which is handled by retrying with
        /// a different seed. Sets of awkward sizes are where that shows up, so this walks
        /// every size across a range rather than sampling round numbers.
        /// </summary>
        [TestMethod]
        public void TestEverySmallSizeBuilds()
        {
            for (int size = 0; size <= 200; size++)
            {
                var items = Keys(size);
                var filter = BinaryFuseFilter.Build(items);

                Assert.AreEqual((uint)size, filter.Count(),
                    $"a filter built from {size} items reported a different count");

                foreach (var item in items)
                {
                    Assert.IsTrue(filter.Test(item), $"size {size} lost a member");
                }
            }
        }

        /// <summary>
        /// An empty set is a filter that answers no to everything, rather than a failure
        /// or a filter that answers yes.
        /// </summary>
        [TestMethod]
        public void TestAnEmptySetBuildsAndHoldsNothing()
        {
            var filter = BinaryFuseFilter.Build(Array.Empty<byte[]>());

            Assert.AreEqual(0u, filter.Count());

            foreach (var probe in Keys(1000, "probe"))
            {
                Assert.IsFalse(filter.Test(probe), "an empty filter claimed to hold something");
            }
        }

        /// <summary>
        /// Duplicates are a property of the caller's data, not a mistake to refuse. The
        /// filter holds a set, so the count is of distinct items.
        /// </summary>
        [TestMethod]
        public void TestDuplicatesAreCollapsed()
        {
            var items = new[] { Key("a"), Key("b"), Key("a"), Key("c"), Key("b"), Key("a") };
            var filter = BinaryFuseFilter.Build(items);

            Assert.AreEqual(3u, filter.Count(), "duplicates were counted separately");

            foreach (var item in items)
            {
                Assert.IsTrue(filter.Test(item));
            }
        }

        /// <summary>
        /// A set given in a different order is the same set, and must answer the same
        /// way. The construction's peeling order depends on the order keys arrive in, so
        /// this is not free.
        /// </summary>
        [TestMethod]
        public void TestOrderDoesNotChangeWhatTheFilterHolds()
        {
            var items = Keys(5000);
            var shuffled = items.OrderBy(x => Encoding.ASCII.GetString(x)).ToArray();

            var a = BinaryFuseFilter.Build(items);
            var b = BinaryFuseFilter.Build(shuffled);

            Assert.AreEqual(a.Count(), b.Count());

            foreach (var probe in Keys(5000, "probe").Concat(items))
            {
                Assert.AreEqual(a.Test(probe), b.Test(probe),
                    "two orderings of the same set disagreed");
            }
        }

        [TestMethod]
        public void TestBuildRejectsNull()
        {
            Assert.ThrowsExactly<ArgumentNullException>(
                () => BinaryFuseFilter.Build(null!));

            // A null item is not the same as an empty one, and silently hashing it as
            // empty would make two different sets look alike.
            Assert.ThrowsExactly<ArgumentNullException>(
                () => BinaryFuseFilter.Build(new byte[][] { Key("a"), null! }));
        }

        [TestMethod]
        public void TestTestRejectsNull()
        {
            var filter = BinaryFuseFilter.Build(Keys(10));
            Assert.ThrowsExactly<ArgumentNullException>(() => filter.Test(null!));
        }
        /// <summary>
        /// The other half of the contract, and the test that stops the one above from
        /// passing vacuously: a filter that answered yes to everything would satisfy
        /// "every member is found" perfectly.
        /// <para>
        /// An 8-bit fingerprint gives a nominal rate of 2^-8, about 0.39%. The bound
        /// here is loose enough not to be flaky and far tighter than a filter with a
        /// broken lookup could reach.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestFalsePositiveRateIsNearTheNominalRate()
        {
            var filter = BinaryFuseFilter.Build(Keys(50000, "member"));

            var probes = Keys(200000, "absent");
            var positives = probes.Count(filter.Test);
            var rate = (double)positives / probes.Length;

            Assert.IsLessThan(0.01, rate,
                $"false positive rate was {rate:P3}, far above the nominal 0.39%");
            Assert.IsGreaterThan(0.0005, rate,
                $"false positive rate was {rate:P3}, suspiciously far below the nominal " +
                "0.39% -- a lookup that never matches would also pass the members test");
        }

        /// <summary>
        /// The reason to have this structure at all. A Bloom filter at the same false
        /// positive rate should be meaningfully larger.
        /// </summary>
        [TestMethod]
        public void TestItIsSmallerThanABloomFilterAtTheSameRate()
        {
            const uint n = 100000;

            var fuse = BinaryFuseFilter.Build(Keys((int)n));
            var bloom = new BloomFilter(n, fuse.FalsePositiveRate());

            // A Bloom filter's bits, as bytes, against the fuse filter's whole array.
            var bloomBytes = bloom.Capacity() / 8;

            Assert.IsLessThan(bloomBytes, fuse.SizeInBytes(),
                $"the fuse filter took {fuse.SizeInBytes()} bytes against the Bloom " +
                $"filter's {bloomBytes}, so it is not buying anything");
        }

        /// <summary>
        /// A wider fingerprint is the only thing that lowers the false positive rate,
        /// which is why the width is what a caller chooses.
        /// </summary>
        [TestMethod]
        public void TestAWiderFingerprintLowersTheFalsePositiveRate()
        {
            var members = Keys(20000, "member");
            var probes = Keys(200000, "absent");

            var narrow = BinaryFuseFilter.Build(members, BinaryFuseWidth.Eight);
            var wide = BinaryFuseFilter.Build(members, BinaryFuseWidth.Sixteen);

            foreach (var member in members)
            {
                Assert.IsTrue(wide.Test(member), "the 16-bit filter lost a member");
            }

            var narrowRate = (double)probes.Count(narrow.Test) / probes.Length;
            var wideRate = (double)probes.Count(wide.Test) / probes.Length;

            Assert.IsLessThan(narrowRate / 10, wideRate,
                $"16-bit gave {wideRate:P4} against 8-bit's {narrowRate:P4}, which is " +
                "not the two orders of magnitude a doubled fingerprint should buy");

            // And it costs twice the space, which is the trade being made.
            Assert.AreEqual(narrow.SizeInBytes() * 2, wide.SizeInBytes());
        }

        /// <summary>
        /// A caller who thinks in false positive rates rather than fingerprint widths
        /// gets the narrowest width that meets their rate, rounded in the safe
        /// direction.
        /// </summary>
        [TestMethod]
        public void TestATargetRatePicksAWidth()
        {
            // 0.39% is exactly what 8 bits gives; anything looser still gets 8, because
            // there is nothing narrower.
            Assert.AreEqual(BinaryFuseWidth.Eight,
                BinaryFuseFilter.Build(Keys(1000), 0.05).Width());
            Assert.AreEqual(BinaryFuseWidth.Eight,
                BinaryFuseFilter.Build(Keys(1000), 0.004).Width());

            // Tighter than 8 bits can give, so it rounds up to 16 rather than silently
            // delivering a worse rate than asked for.
            Assert.AreEqual(BinaryFuseWidth.Sixteen,
                BinaryFuseFilter.Build(Keys(1000), 0.001).Width());

            // The delivered rate is never worse than the requested one.
            foreach (var requested in new[] { 0.05, 0.01, 0.004, 0.001, 0.0001 })
            {
                var filter = BinaryFuseFilter.Build(Keys(1000), requested);
                Assert.IsLessThanOrEqualTo(requested, filter.FalsePositiveRate(),
                    $"asking for {requested} delivered a worse rate");
            }
        }

        /// <summary>
        /// A rate no width can reach is refused, rather than quietly capped at the best
        /// available.
        /// </summary>
        [TestMethod]
        public void TestARateNoWidthCanReachIsRefused()
        {
            var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => BinaryFuseFilter.Build(Keys(1000), 1e-9));
            Assert.AreEqual("fpRate", ex.ParamName);

            foreach (var bad in new[] { 0.0, 1.0, -0.5, 2.0, double.NaN })
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => BinaryFuseFilter.Build(Keys(10), bad));
            }
        }

        /// <summary>
        /// Building the same set twice gives the same filter. Nothing here needs
        /// unpredictability -- the seed exists to give a failed peel another arrangement
        /// to try, not to be unguessable -- and a caller shipping a filter as a build
        /// artifact needs the bytes to be reproducible.
        /// </summary>
        [TestMethod]
        public void TestBuildingTheSameSetTwiceGivesTheSameFilter()
        {
            var items = Keys(10000);

            var a = BinaryFuseFilter.Build(items);
            var b = BinaryFuseFilter.Build(items);

            CollectionAssert.AreEqual(a.ToByteArray(), b.ToByteArray(),
                "two builds of the same set produced different filters");
        }

        /// <summary>
        /// The peel does not always succeed on the first seed, and the retry path resets
        /// a good deal of state before trying again. Getting that reset wrong would
        /// produce a filter built from a mixture of two attempts, which is why this
        /// pins a size that actually retries rather than trusting the path is covered.
        /// <para>
        /// 74 keys is the smallest such size. Across 0..3000 there are 74 of them and
        /// none needs more than five seeds, so the attempt limit is not close to being
        /// the thing that decides whether a build succeeds.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestASetThatNeedsASecondSeedStillBuildsCorrectly()
        {
            var items = Keys(74);
            var filter = BinaryFuseFilter.Build(items);

            Assert.IsGreaterThan(1, filter.AttemptsUsed,
                "74 keys no longer needs a retry, so this test no longer covers the " +
                "retry path -- find a size that does rather than deleting it");

            foreach (var item in items)
            {
                Assert.IsTrue(filter.Test(item), "a filter that retried lost a member");
            }

            Assert.AreEqual(74u, filter.Count());
        }

        /// <summary>
        /// A restored filter answers every question the way the original did, including
        /// the false positives: a filter that disagrees there is a different filter,
        /// even where the answer is allowed to be yes.
        /// </summary>
        [TestMethod]
        public void TestRoundTripsThroughPersistence()
        {
            foreach (var width in new[] { BinaryFuseWidth.Eight, BinaryFuseWidth.Sixteen })
            {
                var members = Keys(20000, "member");
                var original = BinaryFuseFilter.Build(members, width);
                var restored = Persistence.FromByteArray<BinaryFuseFilter>(original.ToByteArray());

                Assert.AreEqual(original.Count(), restored.Count());
                Assert.AreEqual(original.Width(), restored.Width());
                Assert.AreEqual(original.SizeInBytes(), restored.SizeInBytes());

                foreach (var probe in members.Concat(Keys(20000, "absent")))
                {
                    Assert.AreEqual(original.Test(probe), restored.Test(probe),
                        $"the {width} filter disagreed after being restored");
                }
            }
        }

        [TestMethod]
        public void TestAnEmptyFilterRoundTrips()
        {
            var restored = Persistence.FromByteArray<BinaryFuseFilter>(
                BinaryFuseFilter.Build(Array.Empty<byte[]>()).ToByteArray());

            Assert.AreEqual(0u, restored.Count());
            Assert.IsFalse(restored.Test(Key("anything")));
        }

        /// <summary>
        /// The filter names the hash it was built with and refuses to substitute
        /// another, as every structure here does. It has no SetHash to guard, because
        /// the set is hashed during construction -- so the hash is chosen there instead,
        /// which is the only place it could apply to everything the filter holds.
        /// </summary>
        [TestMethod]
        public void TestACustomHashIsNotSubstituted()
        {
            Func<ReadOnlySpan<byte>, ulong> custom = d => (ulong)d.Length * 2654435761UL;

            var bytes = BinaryFuseFilter
                .Build(Keys(1000), BinaryFuseWidth.Eight, custom)
                .ToByteArray();

            Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<BinaryFuseFilter>(bytes));

            Assert.IsNotNull(Persistence.FromByteArray<BinaryFuseFilter>(bytes, custom));
        }

        /// <summary>
        /// A payload whose geometry does not describe a filter this library builds is
        /// refused rather than read into something that answers confidently and wrongly.
        /// The checksum is repaired after each edit, so a guard is what has to catch it.
        /// </summary>
        [TestMethod]
        public void TestAnImpossibleGeometryIsRefused()
        {
            var clean = BinaryFuseFilter.Build(Keys(1000)).ToByteArray();

            // The segment length is a mask, and only confines a position to its segment
            // if it is a power of two.
            AssertRefused(Poke(clean, 4, 1000), "not a power of two");

            // No segments at all.
            AssertRefused(Poke(clean, 8, 0), "no segments");

            // A geometry needing more fingerprints than the payload carries.
            AssertRefused(Poke(clean, 8, 64), "fingerprints");

            // A width the library does not build.
            var badWidth = (byte[])clean.Clone();
            badWidth[14 + 20] = 12;
            RepairChecksum(badWidth);
            AssertRefused(badWidth, "8 or 16 bits wide");
        }

        private static void AssertRefused(byte[] payload, string expected)
        {
            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<BinaryFuseFilter>(payload));
            StringAssert.Contains(ex.Message, expected);
        }

        /// <summary>Overwrites a u32 in the payload and repairs the checksum.</summary>
        private static byte[] Poke(byte[] original, int payloadOffset, uint value)
        {
            var bytes = (byte[])original.Clone();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(14 + payloadOffset), value);
            RepairChecksum(bytes);
            return bytes;
        }

        private static void RepairChecksum(byte[] bytes)
        {
            var crc = new System.IO.Hashing.Crc32();
            crc.Append(bytes.AsSpan(4, bytes.Length - 8));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(bytes.Length - 4), crc.GetCurrentHashAsUInt32());
        }

    }
}
