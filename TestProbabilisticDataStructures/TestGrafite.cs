using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Tests for <see cref="Grafite"/>, against Costa, Ferragina and Vinciguerra,
    /// "Grafite: Taming Adversarial Queries with Optimal Range Filters" (SIGMOD 2024).
    /// </summary>
    /// <remarks>
    /// The claim that makes this filter worth having is not that its false positive
    /// rate is low but that it is *bounded by a theorem*, for any query sequence at
    /// all. So the rate is measured here on the queries that break the heuristic range
    /// filters -- ranges placed hard against the keys -- rather than on the uniform
    /// random ranges that flatter them.
    /// <para>
    /// One deliberate departure from the authors' reference implementation is
    /// pinned by <see cref="TestRangesStraddlingABlockBoundaryHaveNoFalseNegatives"/>;
    /// the reasoning is in the class documentation for <see cref="Grafite.Test(ulong, ulong)"/>.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestGrafite
    {
        private static SortedSet<ulong> RandomKeys(int count, ulong universe, int seed)
        {
            var random = new Random(seed);
            var keys = new SortedSet<ulong>();
            while (keys.Count < count)
            {
                keys.Add((ulong)random.NextInt64(0, (long)universe));
            }
            return keys;
        }

        /// <summary>
        /// A rate of zero or one describes a filter that is either impossible or
        /// pointless.
        /// </summary>
        [TestMethod]
        public void TestInvalidFalsePositiveRateIsRefused()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => Grafite.Build(new ulong[] { 1, 2 }, 0.0, 16));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => Grafite.Build(new ulong[] { 1, 2 }, 1.0, 16));
        }

        /// <summary>
        /// A filter promised nothing about any range size has no rate to deliver.
        /// </summary>
        [TestMethod]
        public void TestZeroMaxRangeSizeIsRefused()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => Grafite.Build(new ulong[] { 1, 2 }, 0.01, 0));
        }

        /// <summary>
        /// A range that runs backwards is a caller error rather than an empty range:
        /// guessing which end was meant would answer a question nobody asked.
        /// </summary>
        [TestMethod]
        public void TestBackwardsRangeIsRefused()
        {
            var filter = Grafite.Build(new ulong[] { 1, 2, 3 }, 0.01, 16);

            Assert.ThrowsExactly<ArgumentException>(() => filter.Test(10, 5));
        }

        /// <summary>
        /// A filter over no keys answers every range with certainty, because every
        /// range really is empty.
        /// </summary>
        [TestMethod]
        public void TestEmptyFilterAnswersEverythingEmpty()
        {
            var filter = Grafite.Build(Array.Empty<ulong>(), 0.01, 16);

            Assert.AreEqual(0UL, filter.Count());
            Assert.IsFalse(filter.Test(0));
            Assert.IsFalse(filter.Test(0, ulong.MaxValue));
        }

        /// <summary>
        /// The contract a filter cannot break: a key that was stored is never reported
        /// absent, and neither is a range containing one.
        /// </summary>
        [TestMethod]
        public void TestThereAreNoFalseNegatives()
        {
            var keys = RandomKeys(5000, 1_000_000_000, seed: 42);
            var filter = Grafite.Build(keys, 0.01, 64, seed: 7);
            var ordered = keys.ToArray();
            var random = new Random(11);

            foreach (var key in ordered)
            {
                Assert.IsTrue(filter.Test(key),
                    $"The stored key {key} was reported absent.");
            }

            for (var i = 0; i < 5000; i++)
            {
                var key = ordered[random.Next(ordered.Length)];
                var low = key - (ulong)random.Next(0, 30);
                var high = key + (ulong)random.Next(0, 30);
                Assert.IsTrue(filter.Test(low, high),
                    $"The range [{low}, {high}] contains the stored key {key} and was " +
                    "reported empty.");
            }
        }

        /// <summary>
        /// The false positive rate holds on ranges that really are empty, at the size
        /// it was promised for.
        /// </summary>
        [TestMethod]
        public void TestFalsePositiveRateHoldsAtThePromisedRangeSize()
        {
            const double epsilon = 0.02;
            const ulong rangeSize = 64;
            var keys = RandomKeys(20000, 100_000_000_000, seed: 3);
            var filter = Grafite.Build(keys, epsilon, rangeSize, seed: 99);

            var random = new Random(5);
            var positives = 0;
            var trials = 0;
            while (trials < 20000)
            {
                var low = (ulong)random.NextInt64(0, 100_000_000_000);
                var high = low + rangeSize - 1;
                if (keys.GetViewBetween(low, high).Count != 0)
                {
                    continue;
                }
                trials++;
                if (filter.Test(low, high))
                {
                    positives++;
                }
            }

            var measured = (double)positives / trials;

            Assert.IsTrue(positives > 0,
                "Not one empty range came back positive over 20,000 tries, which " +
                "means this test is not measuring a false positive rate at all.");
            Assert.IsTrue(measured <= epsilon * 1.25,
                $"The measured false positive rate was {measured:P3} against a " +
                $"promised {epsilon:P3}. The promise is a theorem, not an average, so " +
                "exceeding it is a defect rather than bad luck.");
        }

        /// <summary>
        /// Shorter ranges do proportionally better: the bound for a range of length l
        /// is l/L of the headline rate, because a false positive needs a collision
        /// with one of the l points in the range.
        /// </summary>
        [TestMethod]
        public void TestShorterRangesAreProportionallyMoreAccurate()
        {
            const double epsilon = 0.05;
            const ulong maxRange = 256;
            var keys = RandomKeys(20000, 100_000_000_000, seed: 13);
            var filter = Grafite.Build(keys, epsilon, maxRange, seed: 17);

            double MeasureAt(ulong length, int seed)
            {
                var random = new Random(seed);
                var positives = 0;
                var trials = 0;
                while (trials < 20000)
                {
                    var low = (ulong)random.NextInt64(0, 100_000_000_000);
                    var high = low + length - 1;
                    if (keys.GetViewBetween(low, high).Count != 0)
                    {
                        continue;
                    }
                    trials++;
                    if (filter.Test(low, high))
                    {
                        positives++;
                    }
                }
                return (double)positives / trials;
            }

            var atFull = MeasureAt(maxRange, 21);
            var atQuarter = MeasureAt(maxRange / 4, 23);

            Assert.IsTrue(atFull > 0 && atQuarter > 0,
                "Premise: both range sizes produce some false positives, or the " +
                "comparison below is between two zeros.");
            Assert.IsTrue(atQuarter < atFull,
                $"A quarter-length range was wrong {atQuarter:P3} of the time against " +
                $"{atFull:P3} for a full-length one. The bound is proportional to the " +
                "range length, so the shorter range should do better.");
            Assert.IsTrue(atQuarter <= (epsilon / 4) * 1.5,
                $"A range of a quarter the promised length was wrong {atQuarter:P3}, " +
                $"where the bound allows {epsilon / 4:P3}.");
        }

        /// <summary>
        /// The property the filter exists for: the bound holds when the queries are
        /// correlated with the keys, which is the case that collapses the heuristic
        /// range filters and the normal case in practice, since people look for data
        /// near the data they have.
        /// </summary>
        [TestMethod]
        public void TestCorrelatedQueriesStillHoldTheBound()
        {
            const double epsilon = 0.02;
            const ulong rangeSize = 64;

            // Keys spread far apart, so the gap just above each one is genuinely
            // empty and a query aimed there is a fair test rather than a hit.
            var random = new Random(29);
            var keys = new SortedSet<ulong>();
            while (keys.Count < 20000)
            {
                keys.Add((ulong)random.NextInt64(0, 100_000_000) * 1000);
            }
            var filter = Grafite.Build(keys, epsilon, rangeSize, seed: 31);

            var positives = 0;
            var trials = 0;
            foreach (var key in keys)
            {
                var low = key + 1;
                var high = key + rangeSize;
                if (keys.GetViewBetween(low, high).Count != 0)
                {
                    continue;
                }
                trials++;
                if (filter.Test(low, high))
                {
                    positives++;
                }
            }

            var measured = (double)positives / trials;

            Assert.IsTrue(trials > 10000, "Premise: most gaps were genuinely empty.");
            Assert.IsTrue(measured <= epsilon * 1.25,
                $"Queries placed immediately after each key were wrong {measured:P3} " +
                $"of the time against a promised {epsilon:P3}. This is exactly the " +
                "workload the filter claims to survive.");
        }

        /// <summary>
        /// A range straddling a block boundary must not lose keys.
        /// </summary>
        /// <remarks>
        /// The hash shifts each block of r consecutive keys by its own amount, so it
        /// preserves locality only within a block. A range crossing a boundary becomes
        /// two runs of hash codes with unrelated offsets, and testing it as a single
        /// interval can miss a key that is genuinely inside it -- a false negative,
        /// which is the one error a filter may never make. The authors' reference
        /// implementation tests the range as one interval; this one splits it.
        /// <para>
        /// The parameters here make r small, so that boundary crossings are frequent
        /// and the failure would show up rather than hiding behind a one-in-r chance.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestRangesStraddlingABlockBoundaryHaveNoFalseNegatives()
        {
            var failures = 0;

            for (ulong seed = 0; seed < 300; seed++)
            {
                // Ten keys, a range size of one and a rate of a half put r at 20, so
                // the second block begins at key 20.
                var keys = new ulong[] { 3, 7, 11, 14, 18, 19, 21, 25, 30, 33 };
                var filter = Grafite.Build(keys, 0.5, 1, seed: seed);

                Assert.AreEqual(20UL, filter.R,
                    "Premise: these parameters put the block boundary at 20. If the " +
                    "reduced universe changed, this test is no longer straddling one.");

                // The range holds 18 and 19 from the first block and 21 from the
                // second. Neither endpoint is itself a key, which matters: a key
                // sitting on an endpoint hashes to the endpoint's own code and would
                // be found however the range was tested, hiding the defect.
                if (!filter.Test(17, 22))
                {
                    failures++;
                }
            }

            Assert.AreEqual(0, failures,
                $"{failures} of 300 hash choices reported an occupied range empty. " +
                "A range crossing a block boundary is two runs of hash codes with " +
                "different offsets and has to be tested as two ranges.");
        }

        /// <summary>
        /// Every answer the filter gives, checked against what the definition says it
        /// should be, over a small enough universe to enumerate exhaustively.
        /// </summary>
        /// <remarks>
        /// The filter must answer "possibly" exactly when some stored key hashes to
        /// the same code as some point of the query range -- that is what testing a
        /// range against the stored codes means, and it is a statement about the hash
        /// alone. Computing it by brute force gives an oracle that knows nothing about
        /// blocks, wrapped intervals or Elias-Fano, so any of those going wrong shows
        /// up as a disagreement rather than having to be anticipated by a test written
        /// for it.
        /// </remarks>
        [TestMethod]
        public void TestAnswersMatchAnExhaustiveOracle()
        {
            var keys = new ulong[] { 3, 7, 11, 14, 18, 19, 21, 25, 30, 33 };

            for (ulong seed = 0; seed < 40; seed++)
            {
                // A rate of a half over ten keys puts the reduced universe at 20, so
                // ranges wrap it and straddle its blocks constantly rather than once
                // in a million queries.
                var filter = Grafite.Build(keys, 0.5, 1, seed: seed);
                var codes = keys.Select(filter.HashOf).ToHashSet();

                for (ulong low = 0; low <= 45; low++)
                {
                    for (ulong length = 1; length <= 6; length++)
                    {
                        var high = low + length - 1;

                        var expected = false;
                        for (var point = low; point <= high && !expected; point++)
                        {
                            expected = codes.Contains(filter.HashOf(point));
                        }

                        Assert.AreEqual(expected, filter.Test(low, high),
                            $"seed {seed}, range [{low}, {high}]: the filter said " +
                            $"{filter.Test(low, high)} where a stored key shares a " +
                            $"hash code with a point of the range is {expected}.");
                    }
                }
            }
        }

        /// <summary>
        /// An adversary who knows everything about the filter except its seed cannot
        /// manufacture false positives.
        /// </summary>
        /// <remarks>
        /// The attack this defends against is placing a query exactly one reduced
        /// universe away from a known key. Were the hash simply "key modulo r" -- which
        /// is locality preserving, gives no false negatives, and passes every test
        /// above -- those two would always collide and the attack would succeed every
        /// time. The block term exists to break exactly that, and this measures
        /// whether it does.
        /// </remarks>
        [TestMethod]
        public void TestQueriesOffsetByTheReducedUniverseAreNotSystematicHits()
        {
            const double epsilon = 0.02;
            var keys = RandomKeys(5000, 100_000_000, seed: 107);
            var filter = Grafite.Build(keys, epsilon, 64, seed: 109);

            var positives = 0;
            var trials = 0;
            foreach (var key in keys)
            {
                // One whole reduced universe above a known key: the point that
                // collides under a hash that forgot to scramble the block.
                var target = key + filter.R;
                if (keys.Contains(target))
                {
                    continue;
                }
                trials++;
                if (filter.Test(target))
                {
                    positives++;
                }
            }

            var measured = (double)positives / trials;

            Assert.IsTrue(trials > 4000, "Premise: the offset points are not keys.");
            Assert.IsTrue(measured <= epsilon,
                $"Points placed exactly one reduced universe above each key came " +
                $"back positive {measured:P3} of the time. Anything approaching " +
                "certainty here means the hash is predictable from the structure's " +
                "own parameters, which is the attack this filter exists to survive.");
        }

        /// <summary>
        /// A range spanning more than two blocks must not lose the keys in the middle
        /// of it.
        /// </summary>
        /// <remarks>
        /// Splitting a range at one boundary only works when the range is narrower
        /// than a block, which is why anything at least as wide as the reduced
        /// universe is answered "possibly" outright rather than split. Without that
        /// guard a wide range would be split into two pieces that each still straddle
        /// a boundary, and tested as though they did not -- so the keys sitting in the
        /// blocks between the ends could go unreported.
        /// </remarks>
        [TestMethod]
        public void TestRangesSpanningSeveralBlocksHaveNoFalseNegatives()
        {
            var failures = 0;

            for (ulong seed = 0; seed < 300; seed++)
            {
                // Few keys and a wide rate put the blocks at five keys each, so the
                // range below covers four of them. Keeping the key count low matters:
                // with many keys some code lands in almost any interval by chance,
                // and the range would come back positive for the wrong reason.
                var keys = new ulong[] { 7, 8, 40, 41 };
                var filter = Grafite.Build(keys, 0.9, 1, seed: seed);

                Assert.AreEqual(5UL, filter.R, "Premise: the blocks are five keys wide.");

                // Sixteen keys wide against blocks of five, and it holds 7 and 8.
                if (!filter.Test(0, 15))
                {
                    failures++;
                }
            }

            Assert.AreEqual(0, failures,
                $"{failures} of 300 hash choices reported a range holding two of the " +
                "keys empty. A range wider than a block cannot be tested by splitting " +
                "it at a single boundary.");
        }

        /// <summary>
        /// A range at least as wide as the reduced universe covers every hash code
        /// there is, so the only safe answer is that it might not be empty.
        /// </summary>
        [TestMethod]
        public void TestRangesWiderThanTheReducedUniverseAnswerPossible()
        {
            var keys = new ulong[] { 5, 100, 5000 };
            var filter = Grafite.Build(keys, 0.1, 4, seed: 3);

            Assert.IsTrue(filter.Test(0, filter.R),
                "A range spanning the whole reduced universe cannot be shown empty.");
            Assert.IsTrue(filter.Test(0, ulong.MaxValue),
                "Nor can the widest range there is.");
        }

        /// <summary>
        /// The encoding is a round trip: every code that went in comes back, in order.
        /// </summary>
        /// <remarks>
        /// Elias-Fano splits each code between a packed array and a bitvector, and the
        /// two halves are recovered by counting rather than by being stored together.
        /// This checks the recovery directly, instead of inferring it from query
        /// answers that a broken encoding could still get right by luck.
        /// </remarks>
        [TestMethod]
        public void TestEliasFanoRecoversEveryCode()
        {
            var keys = RandomKeys(2000, 10_000_000, seed: 71);
            var filter = Grafite.Build(keys, 0.01, 32, seed: 73);

            var expected = keys.Select(filter.HashOf).Distinct().OrderBy(c => c).ToArray();

            Assert.AreEqual(expected.Length, filter.CodeCount,
                "The filter stored a different number of codes than the keys hash to.");

            for (var i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i], filter.CodeAt(i),
                    $"The code at index {i} came back as {filter.CodeAt(i)} rather " +
                    $"than {expected[i]}.");
            }
        }

        /// <summary>
        /// The space used is close to what the paper says it should be, which is what
        /// makes the structure worth its complexity over a sorted array of hashes.
        /// </summary>
        [TestMethod]
        public void TestSpaceIsCloseToTheTheoreticalBound()
        {
            const double epsilon = 0.01;
            const ulong maxRange = 64;
            var keys = RandomKeys(50000, 100_000_000_000, seed: 37);
            var filter = Grafite.Build(keys, epsilon, maxRange, seed: 41);

            var bitsPerKey = filter.SizeInBytes() * 8.0 / filter.Count();
            var predicted = Math.Log2(maxRange / epsilon) + 2.0;

            Assert.IsTrue(bitsPerKey <= predicted * 1.1,
                $"The filter spends {bitsPerKey:F2} bits per key where the encoding " +
                $"predicts {predicted:F2}. Elias-Fano is the reason to prefer this to " +
                "a sorted array, and it has to actually deliver.");
        }

        /// <summary>
        /// A point query is a range of one, and must answer the same way.
        /// </summary>
        [TestMethod]
        public void TestPointQueryIsARangeOfOne()
        {
            var keys = RandomKeys(1000, 1_000_000, seed: 53);
            var filter = Grafite.Build(keys, 0.01, 16, seed: 59);

            for (ulong key = 0; key < 2000; key++)
            {
                Assert.AreEqual(filter.Test(key, key), filter.Test(key),
                    $"The point query for {key} disagreed with the range [{key}, {key}].");
            }
        }

        /// <summary>
        /// The same keys and the same seed give the same filter, which is what makes
        /// every measurement in this file reproducible.
        /// </summary>
        [TestMethod]
        public void TestSameSeedGivesTheSameFilter()
        {
            var keys = RandomKeys(500, 1_000_000, seed: 61);

            var first = Grafite.Build(keys, 0.01, 16, seed: 67);
            var second = Grafite.Build(keys, 0.01, 16, seed: 67);

            CollectionAssert.AreEqual(first.ToByteArray(), second.ToByteArray());
        }

        /// <summary>
        /// A filter written and read back is the same filter, and answers every query
        /// the way the original did.
        /// </summary>
        [TestMethod]
        public void TestRoundTripIsByteExactAndAnswersTheSame()
        {
            var keys = RandomKeys(5000, 100_000_000, seed: 79);
            var filter = Grafite.Build(keys, 0.01, 64, seed: 83);

            var written = filter.ToByteArray();
            var restored = Grafite.ReadFrom(new MemoryStream(written));

            CollectionAssert.AreEqual(written, restored.ToByteArray());
            Assert.AreEqual(filter.Count(), restored.Count());

            var random = new Random(89);
            for (var i = 0; i < 5000; i++)
            {
                var low = (ulong)random.NextInt64(0, 100_000_000);
                var high = low + (ulong)random.Next(0, 64);
                Assert.AreEqual(filter.Test(low, high), restored.Test(low, high),
                    $"The restored filter disagreed about [{low}, {high}].");
            }
        }

        /// <summary>
        /// The filter takes numbers rather than bytes, so a caller offering a hash
        /// function has misunderstood what they are reading.
        /// </summary>
        [TestMethod]
        public void TestReadingWithASuppliedHashIsRefused()
        {
            var filter = Grafite.Build(new ulong[] { 1, 2, 3 }, 0.01, 16, seed: 97);
            var bytes = filter.ToByteArray();

            Assert.ThrowsExactly<InvalidDataException>(
                () => Grafite.ReadFrom(new MemoryStream(bytes), data => 0UL));
        }

        /// <summary>
        /// The stated rate for a range size is the one the filter was built to, and
        /// scales with the range as the bound does.
        /// </summary>
        [TestMethod]
        public void TestStatedRateTracksTheRangeSize()
        {
            var keys = RandomKeys(1000, 10_000_000, seed: 101);
            var filter = Grafite.Build(keys, 0.01, 64, seed: 103);

            Assert.AreEqual(0.01, filter.FalsePositiveRate(64), 1e-9);
            Assert.AreEqual(0.005, filter.FalsePositiveRate(32), 1e-9);
            Assert.AreEqual(1.0, filter.FalsePositiveRate(ulong.MaxValue), 1e-9,
                "A range wider than the universe cannot be excluded at all.");
        }
    }
}
