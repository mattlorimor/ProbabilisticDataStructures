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
    /// Tests for <see cref="VarOpt"/>, against Cohen, Duffield, Kaplan, Lund and
    /// Thorup, "Stream Sampling for Variance-Optimal Estimation of Subset Sums"
    /// (SODA 2009).
    /// </summary>
    /// <remarks>
    /// The paper's defining properties are what these test. Property (ii) is the
    /// sample size, exactly min(k, n). Property (i) is the threshold rule: an item
    /// heavier than the threshold is held at its own weight, everything else that
    /// survives at the threshold, and the resulting estimator is unbiased for every
    /// subset. The consequence the paper draws out as VΣ = 0 -- that the adjusted
    /// weights sum to the true total exactly, not approximately -- is the sharpest
    /// thing here to test, since floating point is the only thing that can blur it.
    /// </remarks>
    [TestClass]
    public class TestVarOpt
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        private static string Name(ReadOnlyMemory<byte> data) =>
            Encoding.ASCII.GetString(data.Span);

        /// <summary>
        /// A sample that keeps nothing answers nothing.
        /// </summary>
        [TestMethod]
        public void TestZeroKIsRefused()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new VarOpt(0));
        }

        /// <summary>
        /// Inclusion probability is proportional to weight, so a weight of zero
        /// describes an item that can never be drawn, and a negative one an item
        /// with negative probability.
        /// </summary>
        [TestMethod]
        public void TestNonPositiveWeightIsRefused()
        {
            var sample = new VarOpt(10, seed: 1);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => sample.Add(Key("a"), 0.0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => sample.Add(Key("a"), -1.0));
        }

        /// <summary>
        /// A weight that is not a number would poison every threshold computed after
        /// it, and infinity would make every other item's share of the total zero.
        /// </summary>
        [TestMethod]
        public void TestNonFiniteWeightIsRefused()
        {
            var sample = new VarOpt(10, seed: 1);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => sample.Add(Key("a"), double.NaN));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => sample.Add(Key("a"), double.PositiveInfinity));
        }

        /// <summary>
        /// Property (ii): the sample holds everything while it fits, and exactly k
        /// once it does not.
        /// </summary>
        [TestMethod]
        public void TestSampleSizeIsExactlyMinOfKAndCount()
        {
            var sample = new VarOpt(8, seed: 3);

            for (var i = 0; i < 8; i++)
            {
                sample.Add(Key($"item-{i}"), 1.0 + i);
                Assert.AreEqual((uint)(i + 1), sample.SampleCount,
                    "While the stream still fits, every item seen is still held.");
            }

            for (var i = 8; i < 200; i++)
            {
                sample.Add(Key($"item-{i}"), 1.0 + i);
                Assert.AreEqual(8u, sample.SampleCount,
                    "Once sampling begins the size never wavers: exactly one item " +
                    "is dropped for every one that arrives.");
            }
        }

        /// <summary>
        /// Under capacity there is nothing to estimate: every item is held at the
        /// weight it arrived with, and any subset sum is exact.
        /// </summary>
        [TestMethod]
        public void TestUnderCapacityEverythingIsExact()
        {
            var sample = new VarOpt(16, seed: 5);
            var weights = new Dictionary<string, double>();

            for (var i = 0; i < 16; i++)
            {
                var weight = 0.5 + (i * 1.25);
                weights[$"item-{i}"] = weight;
                sample.Add(Key($"item-{i}"), weight);
            }

            foreach (var element in sample.Samples())
            {
                Assert.AreEqual(weights[Name(element.Data)], element.Weight, 0.0,
                    "An item held under capacity carries the weight it arrived " +
                    "with, bit for bit.");
            }

            var evens = sample.EstimateSubset(d => int.Parse(Name(d)[5..]) % 2 == 0);
            var expected = weights.Where(w => int.Parse(w.Key[5..]) % 2 == 0)
                .Sum(w => w.Value);
            Assert.AreEqual(expected, evens, 1e-12,
                "Under capacity the estimate of a subset is that subset.");
        }

        /// <summary>
        /// VΣ = 0. The adjusted weights sum to the total weight of the stream
        /// exactly -- the estimate of the whole is not an estimate. Every drop hands
        /// the dropped item's weight to the survivors, which is what makes this hold
        /// rather than merely hold closely.
        /// </summary>
        [TestMethod]
        public void TestTotalWeightIsPreservedExactly()
        {
            var sample = new VarOpt(32, seed: 11);
            var random = new Random(4);
            var total = 0.0;

            for (var i = 0; i < 5000; i++)
            {
                // Weights spanning four orders of magnitude, so the total is a sum
                // floating point has every chance to lose track of.
                var weight = Math.Pow(10, random.NextDouble() * 4) * 0.001;
                total += weight;
                sample.Add(Key($"item-{i}"), weight);
            }

            var samples = sample.Samples();
            var summed = samples.Sum(s => s.Weight);

            Assert.AreEqual(32, samples.Length);
            Assert.AreEqual(total, sample.TotalWeight, total * 1e-12,
                "The structure's own total must track what went in.");
            Assert.AreEqual(total, summed, total * 1e-12,
                "Summing the sample's adjusted weights estimates the stream's " +
                "total weight with no error at all: this is the paper's V-sigma = 0.");
        }

        /// <summary>
        /// Property (i), the heavy half: an item heavier than the threshold is held
        /// at its own weight, and everything held at the threshold is no heavier
        /// than it. A sample where these mixed would be reporting weights it never
        /// saw for items it did.
        /// </summary>
        [TestMethod]
        public void TestHeavyItemsKeepExactWeightsAndLightOnesShareTau()
        {
            var sample = new VarOpt(16, seed: 13);

            // A few genuine heavyweights among many small items, so the threshold
            // settles well below the largest weights.
            for (var i = 0; i < 400; i++)
            {
                sample.Add(Key($"small-{i}"), 1.0);
            }
            for (var i = 0; i < 4; i++)
            {
                sample.Add(Key($"huge-{i}"), 10000.0);
            }

            var tau = sample.Tau;
            Assert.IsTrue(tau > 0.0, "Premise: the sample is past capacity and has " +
                "a threshold. Without one this test would assert nothing.");

            var heavy = sample.Samples().Where(s => Name(s.Data).StartsWith("huge")).ToArray();
            Assert.AreEqual(4, heavy.Length,
                "Items this much heavier than the threshold are held with " +
                "certainty: their inclusion probability min(1, w/tau) is 1.");
            foreach (var element in heavy)
            {
                Assert.AreEqual(10000.0, element.Weight, 0.0,
                    "An item above the threshold is its own estimate.");
                Assert.IsTrue(element.Weight > tau,
                    "An item held at its exact weight must be above the threshold; " +
                    "that is what being held exactly means.");
            }

            var light = sample.Samples().Where(s => Name(s.Data).StartsWith("small")).ToArray();
            Assert.IsTrue(light.Length > 0, "Premise: some light items survived.");
            foreach (var element in light)
            {
                Assert.AreEqual(tau, element.Weight, 0.0,
                    "Below the threshold survival is luck, and luck is priced the " +
                    "same for everyone: one shared adjusted weight.");
            }
        }

        /// <summary>
        /// The estimator is unbiased: over many independent samples of the same
        /// stream, the mean estimate of a subset converges on that subset's true
        /// weight. Tested against the standard error of the mean rather than a
        /// fixed tolerance, so the band means what it says.
        /// </summary>
        [TestMethod]
        public void TestSubsetEstimatesAreUnbiased()
        {
            const int trials = 2000;
            var weights = new double[800];
            var random = new Random(17);
            for (var i = 0; i < weights.Length; i++)
            {
                weights[i] = (random.NextDouble() * 5.0) + 0.01;
            }
            var truth = weights.Where((_, i) => i % 2 == 0).Sum();

            var estimates = new double[trials];
            for (var t = 0; t < trials; t++)
            {
                var sample = new VarOpt(16, seed: (ulong)(1000 + t));
                for (var i = 0; i < weights.Length; i++)
                {
                    sample.Add(Key($"k-{i}"), weights[i]);
                }
                estimates[t] = sample.EstimateSubset(
                    d => int.Parse(Name(d)[2..]) % 2 == 0);
            }

            var mean = estimates.Average();
            var variance = estimates.Sum(e => (e - mean) * (e - mean)) / (trials - 1);
            var standardError = Math.Sqrt(variance / trials);

            Assert.IsTrue(standardError > 0.0,
                "Premise: the estimates vary. If every trial returned the same " +
                "number this test would be checking arithmetic, not unbiasedness.");
            Assert.IsTrue(estimates.Any(e => e < truth) && estimates.Any(e => e > truth),
                "Premise: estimates fall on both sides of the truth. A bound only " +
                "ever approached from one side would pass the test below while " +
                "being biased.");

            var deviation = Math.Abs(mean - truth) / standardError;
            Assert.IsTrue(deviation < 4.0,
                $"The mean of {trials} estimates was {mean:F3} against a true " +
                $"{truth:F3}, {deviation:F2} standard errors away. Beyond four the " +
                "estimator is biased, not unlucky.");
        }

        /// <summary>
        /// Property (i), stated as a probability: an item's chance of being in the
        /// sample is min(1, w/tau). With k = 1 the whole sample is one item and the
        /// rule is directly countable -- a heavier item is kept in proportion to its
        /// share of the total weight.
        /// </summary>
        [TestMethod]
        public void TestInclusionIsProportionalToWeight()
        {
            const int trials = 10000;
            var heavyKept = 0;

            for (var t = 0; t < trials; t++)
            {
                var sample = new VarOpt(1, seed: (ulong)t);
                sample.Add(Key("light"), 1.0);
                sample.Add(Key("heavy"), 3.0);

                var held = sample.Samples();
                Assert.AreEqual(1, held.Length);
                if (Name(held[0].Data) == "heavy")
                {
                    heavyKept++;
                }
            }

            // p = 3/4, so the count is Binomial(10000, 0.75): mean 7500, sd 43.3.
            // The band is four standard deviations, which a correct implementation
            // leaves alone about 99.99% of the time.
            Assert.IsTrue(heavyKept > 7327 && heavyKept < 7673,
                $"The heavier of two items was kept {heavyKept} times in {trials}, " +
                "against the 7500 its share of the weight entitles it to. Inclusion " +
                "is meant to be proportional to weight.");
        }

        /// <summary>
        /// Equal weights make VarOpt the textbook uniform reservoir: every item ever
        /// seen is equally likely to be in the sample. The paper makes this claim
        /// explicitly -- VarOpt reduces to the standard scheme on unit weights.
        /// </summary>
        [TestMethod]
        public void TestUnitWeightsGiveUniformSampling()
        {
            const int trials = 4000;
            const int streamLength = 40;
            const int k = 4;
            var counts = new int[streamLength];

            for (var t = 0; t < trials; t++)
            {
                var sample = new VarOpt(k, seed: (ulong)(50000 + t));
                for (var i = 0; i < streamLength; i++)
                {
                    sample.Add(Key($"u-{i}"), 1.0);
                }
                foreach (var element in sample.Samples())
                {
                    counts[int.Parse(Name(element.Data)[2..])]++;
                }
            }

            // Each item is kept with probability k/n = 0.1: mean 400 per item,
            // sd = sqrt(4000 * 0.1 * 0.9) = 19. Four sd is +/- 76.
            foreach (var (count, index) in counts.Select((c, i) => (c, i)))
            {
                Assert.IsTrue(count > 324 && count < 476,
                    $"Item {index} was kept {count} times in {trials} trials, " +
                    "against 400 expected. Under unit weights no position in the " +
                    "stream may be favoured over any other.");
            }
        }

        /// <summary>
        /// The paper's recurrence: a sample of samples is a sample of the union.
        /// Merging must leave both the exact total and the sample size intact.
        /// </summary>
        [TestMethod]
        public void TestMergeKeepsTheTotalExactAndTheSizeRight()
        {
            var left = new VarOpt(12, seed: 19);
            var right = new VarOpt(12, seed: 23);
            var random = new Random(29);
            var total = 0.0;

            for (var i = 0; i < 600; i++)
            {
                var weight = (random.NextDouble() * 3.0) + 0.01;
                total += weight;
                (i < 300 ? left : right).Add(Key($"m-{i}"), weight);
            }

            var merged = left.Merge(right);

            Assert.AreEqual(12u, merged.SampleCount,
                "A merge of two full samples is still a sample of k items.");
            Assert.AreEqual(600UL, merged.N,
                "The merged sample has seen everything both inputs saw.");
            Assert.AreEqual(total, merged.TotalWeight, total * 1e-12,
                "The union's adjusted weights still sum to the true total: the " +
                "recurrence preserves what the insertion path preserves.");
            Assert.AreEqual(total, merged.Samples().Sum(s => s.Weight), total * 1e-12,
                "And the sample itself still reports it.");
        }

        /// <summary>
        /// The recurrence's real claim is about estimates, not totals: a subset
        /// estimate taken from the merge of two samples is unbiased for the union.
        /// </summary>
        [TestMethod]
        public void TestMergedSamplesEstimateSubsetsWithoutBias()
        {
            const int trials = 1500;
            var weights = new double[400];
            var random = new Random(31);
            for (var i = 0; i < weights.Length; i++)
            {
                weights[i] = (random.NextDouble() * 4.0) + 0.05;
            }
            var truth = weights.Where((_, i) => i % 3 == 0).Sum();

            var estimates = new double[trials];
            for (var t = 0; t < trials; t++)
            {
                var left = new VarOpt(10, seed: (ulong)(70000 + (2 * t)));
                var right = new VarOpt(10, seed: (ulong)(70001 + (2 * t)));
                for (var i = 0; i < weights.Length; i++)
                {
                    (i < 200 ? left : right).Add(Key($"s-{i}"), weights[i]);
                }
                estimates[t] = left.Merge(right).EstimateSubset(
                    d => int.Parse(Name(d)[2..]) % 3 == 0);
            }

            var mean = estimates.Average();
            var variance = estimates.Sum(e => (e - mean) * (e - mean)) / (trials - 1);
            var standardError = Math.Sqrt(variance / trials);

            Assert.IsTrue(standardError > 0.0, "Premise: the estimates vary.");
            Assert.IsTrue(estimates.Any(e => e < truth) && estimates.Any(e => e > truth),
                "Premise: estimates fall on both sides of the truth.");

            var deviation = Math.Abs(mean - truth) / standardError;
            Assert.IsTrue(deviation < 4.0,
                $"The mean of {trials} merged estimates was {mean:F3} against a " +
                $"true {truth:F3}, {deviation:F2} standard errors away. The " +
                "recurrence is meant to preserve unbiasedness.");
        }

        /// <summary>
        /// Merging samples of different k is refused. The recurrence holds because
        /// equal k's guarantee the union exceeds k whenever both inputs sampled; with
        /// unequal k's the result can keep sampled items at exact weights, which is
        /// not a VarOpt sample of anything and reports no error while being wrong.
        /// </summary>
        [TestMethod]
        public void TestMergingDifferentKIsRefused()
        {
            var left = new VarOpt(10, seed: 37);
            var right = new VarOpt(20, seed: 41);
            for (var i = 0; i < 50; i++)
            {
                left.Add(Key($"l-{i}"), 1.0);
                right.Add(Key($"r-{i}"), 1.0);
            }

            Assert.ThrowsExactly<ArgumentException>(() => left.Merge(right));
        }

        /// <summary>
        /// Merging an empty sample changes nothing, and merging into an empty one is
        /// the same as having fed it the other's items.
        /// </summary>
        [TestMethod]
        public void TestMergingWithAnEmptySample()
        {
            var filled = new VarOpt(8, seed: 43);
            for (var i = 0; i < 5; i++)
            {
                filled.Add(Key($"e-{i}"), 1.0 + i);
            }
            var before = filled.ToByteArray();

            filled.Merge(new VarOpt(8, seed: 47));
            CollectionAssert.AreEqual(before, filled.ToByteArray(),
                "Merging an empty sample adds nothing, so it must change nothing -- " +
                "including the random state, which no drop decision was made from.");

            var intoEmpty = new VarOpt(8, seed: 53);
            intoEmpty.Merge(filled);
            Assert.AreEqual(5u, intoEmpty.SampleCount);
            Assert.AreEqual(5UL, intoEmpty.N);
            Assert.AreEqual(filled.TotalWeight, intoEmpty.TotalWeight, 1e-12);
        }

        /// <summary>
        /// A sample merged into itself must read its own state as it was rather than
        /// as it is being rebuilt. The items are the structure's own storage, so an
        /// implementation that fed them in place would be reading arrays it had
        /// already overwritten.
        /// </summary>
        [TestMethod]
        public void TestSelfMergeReadsTheOldStateNotTheNewOne()
        {
            var sample = new VarOpt(6, seed: 59);
            for (var i = 0; i < 60; i++)
            {
                sample.Add(Key($"self-{i}"), 1.0 + (i % 5));
            }
            var totalBefore = sample.TotalWeight;

            sample.Merge(sample);

            Assert.AreEqual(6u, sample.SampleCount);
            Assert.AreEqual(120UL, sample.N);
            Assert.AreEqual(totalBefore * 2.0, sample.TotalWeight, totalBefore * 1e-12,
                "Merging a sample into itself doubles the weight it represents; " +
                "reading storage mid-rebuild would double something else.");
            foreach (var element in sample.Samples())
            {
                Assert.IsTrue(Name(element.Data).StartsWith("self-"),
                    "Every item in the result must still be an item that was added.");
            }
        }

        /// <summary>
        /// The generator's position is part of the state. A sample written and read
        /// back continues its sequence; one that restarted would make the same drop
        /// decisions after every load, and a sample checkpointed on a schedule would
        /// stop being a random sample at all.
        /// </summary>
        [TestMethod]
        public void TestRandomStateResumesRatherThanRestarts()
        {
            var original = new VarOpt(10, seed: 61);
            for (var i = 0; i < 500; i++)
            {
                original.Add(Key($"r-{i}"), 1.0 + (i % 7));
            }

            var restored = VarOpt.ReadFrom(new MemoryStream(original.ToByteArray()));

            for (var i = 500; i < 900; i++)
            {
                original.Add(Key($"r-{i}"), 1.0 + (i % 7));
                restored.Add(Key($"r-{i}"), 1.0 + (i % 7));
            }

            CollectionAssert.AreEqual(original.ToByteArray(), restored.ToByteArray(),
                "A restored sample must make the same decisions the original goes " +
                "on to make, which requires the generator to resume where it was.");
        }

        /// <summary>
        /// A sample written and read back is the same sample, byte for byte.
        /// </summary>
        [TestMethod]
        public void TestRoundTripIsByteExact()
        {
            var sample = new VarOpt(24, seed: 67);
            var random = new Random(71);
            for (var i = 0; i < 2000; i++)
            {
                sample.Add(Key($"rt-{i}"), (random.NextDouble() * 100.0) + 0.001);
            }

            var written = sample.ToByteArray();
            var restored = VarOpt.ReadFrom(new MemoryStream(written));

            CollectionAssert.AreEqual(written, restored.ToByteArray());
            Assert.AreEqual(sample.TotalWeight, restored.TotalWeight, 0.0,
                "The total is the one number a reader must not round.");
            Assert.AreEqual(sample.N, restored.N);
            CollectionAssert.AreEqual(
                sample.Samples().Select(s => Name(s.Data)).OrderBy(n => n).ToArray(),
                restored.Samples().Select(s => Name(s.Data)).OrderBy(n => n).ToArray());
        }

        /// <summary>
        /// Reset returns the structure to the state it was constructed in, and the
        /// generator with it -- what remains would otherwise be a sample of a stream
        /// that has supposedly not happened.
        /// </summary>
        [TestMethod]
        public void TestResetEmptiesTheSample()
        {
            var sample = new VarOpt(10, seed: 73);
            for (var i = 0; i < 300; i++)
            {
                sample.Add(Key($"x-{i}"), 1.0 + i);
            }

            sample.Reset();

            Assert.AreEqual(0u, sample.SampleCount);
            Assert.AreEqual(0UL, sample.N);
            Assert.AreEqual(0.0, sample.TotalWeight, 0.0);
            Assert.AreEqual(0, sample.Samples().Length);
            Assert.AreEqual(0.0, sample.EstimateSubset(_ => true), 0.0);
        }

        /// <summary>
        /// The span overload copies what it is given: the structure keeps the items
        /// it samples, so a caller reusing their buffer must not be able to rewrite
        /// the sample from underneath it.
        /// </summary>
        [TestMethod]
        public void TestSpanContentsAreCopied()
        {
            var sample = new VarOpt(4, seed: 79);
            var buffer = Key("original");

            sample.Add(buffer.AsSpan(), 5.0);
            Array.Fill(buffer, (byte)'z');

            Assert.AreEqual("original", Name(sample.Samples()[0].Data),
                "The sample must hold what it was shown, not a window onto the " +
                "caller's buffer.");
        }

        /// <summary>
        /// An item heavier than everything before it belongs in the sample with
        /// certainty, at its own weight, however late it arrives.
        /// </summary>
        [TestMethod]
        public void TestAnOverwhelminglyHeavyLateArrivalIsKept()
        {
            var sample = new VarOpt(5, seed: 83);
            for (var i = 0; i < 1000; i++)
            {
                sample.Add(Key($"tiny-{i}"), 1.0);
            }

            sample.Add(Key("colossus"), 1e9);

            var held = sample.Samples().Single(s => Name(s.Data) == "colossus");
            Assert.AreEqual(1e9, held.Weight, 0.0,
                "An item whose weight dwarfs the threshold is included with " +
                "probability one and estimated as itself.");
        }

        /// <summary>
        /// Weights spanning many orders of magnitude are the case where the
        /// threshold arithmetic is most likely to lose an item or a fraction of the
        /// total. The invariants must hold there too.
        /// </summary>
        [TestMethod]
        public void TestExtremeWeightRatiosKeepTheInvariants()
        {
            var sample = new VarOpt(16, seed: 89);
            var total = 0.0;

            for (var i = 0; i < 2000; i++)
            {
                // Alternating dust and boulders: 1e-6 against 1e6.
                var weight = i % 2 == 0 ? 1e-6 : 1e6;
                total += weight;
                sample.Add(Key($"w-{i}"), weight);
            }

            Assert.AreEqual(16u, sample.SampleCount);
            Assert.AreEqual(total, sample.TotalWeight, total * 1e-12);

            var tau = sample.Tau;
            foreach (var element in sample.Samples())
            {
                Assert.IsTrue(element.Weight > 0.0 && !double.IsNaN(element.Weight),
                    "No item may come out of the sample weightless or unnumbered.");
                Assert.IsTrue(element.Weight >= tau - (tau * 1e-12),
                    "Nothing in the sample may sit below the threshold: below it " +
                    "the item would have been dropped.");
            }
        }

        /// <summary>
        /// Two samples given the same seed and the same stream make the same
        /// decisions, which is what makes every seeded test here reproducible.
        /// </summary>
        [TestMethod]
        public void TestSameSeedSameStreamSameSample()
        {
            var first = new VarOpt(12, seed: 97);
            var second = new VarOpt(12, seed: 97);

            var random = new Random(101);
            for (var i = 0; i < 800; i++)
            {
                var weight = (random.NextDouble() * 9.0) + 0.5;
                first.Add(Key($"d-{i}"), weight);
                second.Add(Key($"d-{i}"), weight);
            }

            CollectionAssert.AreEqual(first.ToByteArray(), second.ToByteArray());
        }

        /// <summary>
        /// Adding without a weight is adding at weight one, which is the uniform
        /// reservoir case and must not be a different code path.
        /// </summary>
        [TestMethod]
        public void TestUnweightedAddIsWeightOne()
        {
            var implicitly_ = new VarOpt(10, seed: 103);
            var explicitly = new VarOpt(10, seed: 103);

            for (var i = 0; i < 200; i++)
            {
                implicitly_.Add(Key($"u-{i}"));
                explicitly.Add(Key($"u-{i}"), 1.0);
            }

            CollectionAssert.AreEqual(
                implicitly_.ToByteArray(), explicitly.ToByteArray());
            Assert.AreEqual(200.0, implicitly_.TotalWeight, 1e-12);
        }
    }
}
