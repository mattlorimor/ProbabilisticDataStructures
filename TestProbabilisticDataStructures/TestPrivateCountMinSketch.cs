using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Tests for the private Count-Min Sketch behind DPSW-Sketch (Wang, Wang and Chen,
    /// KDD 2024).
    /// </summary>
    /// <remarks>
    /// This is the first structure in this library whose contract is a privacy
    /// guarantee, and it changes what the tests are for. Everywhere else, a claim is an
    /// error rate and a test measures it. Here the claim is that an observer cannot
    /// tell whether any one record was in the stream, and that is a theorem about the
    /// mechanism -- the authors' theorem, resting on the noise having a particular
    /// distribution at a particular scale.
    /// <para>
    /// No test here proves that theorem, and none is written as though it did. What
    /// these tests do is hold the implementation to the distribution the theorem
    /// assumes: its centre, its spread, its shape, and how its spread moves with the
    /// budget and the depth. That is the part that can be checked, and it is the part
    /// that can silently be wrong -- a mechanism at half the required noise looks
    /// exactly like a working one and protects nobody.
    /// </para>
    /// <para>
    /// Everything is seeded, so every figure quoted is the figure this suite will keep
    /// getting rather than one that happened once.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestPrivateCountMinSketch
    {
        private static byte[] Key(int i) => Encoding.UTF8.GetBytes("k" + i);

        private static byte[] Key(string s) => Encoding.UTF8.GetBytes(s);

        private static double[] NoiseOf(PrivateCountMinSketch sketch) =>
            sketch.Counters.SelectMany(row => row).ToArray();

        private static (double Mean, double Variance) MomentsOf(double[] values)
        {
            var mean = values.Average();
            var variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Length - 1);
            return (mean, variance);
        }

        /// <summary>
        /// The noise has the variance the Gaussian mechanism requires.
        /// </summary>
        /// <remarks>
        /// This is the single most important assertion in this file. The privacy
        /// argument is the Gaussian mechanism at the l2-sensitivity of a Count-Min
        /// Sketch, which is the square root of twice the depth; the mechanism then
        /// wants a variance of the sensitivity squared over twice the budget, which is
        /// the depth over the budget. Get this wrong by a factor and the sketch is
        /// exactly as accurate as it should be, exactly as fast, and not private.
        /// <para>
        /// Measured at ratios of 0.998, 0.998, 0.993 and 0.988 to the required
        /// variance across the four shapes below.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestTheNoiseHasTheVarianceTheMechanismRequires()
        {
            foreach (var (width, depth, rho) in new[]
            {
                (2000u, 5u, 1.0),
                (2000u, 5u, 0.1),
                (2000u, 10u, 1.0),
                (5000u, 3u, 0.5),
            })
            {
                var sketch = new PrivateCountMinSketch(width, depth, rho, seed: 7);
                var (_, variance) = MomentsOf(NoiseOf(sketch));

                var required = depth / rho;

                Assert.AreEqual(required, variance, required * 0.05,
                    $"At depth {depth} and a budget of {rho} the noise must have a " +
                    $"variance of {required}, and it has {variance:F4}. A mechanism at " +
                    "the wrong scale is not the mechanism the privacy proof is about.");

                Assert.AreEqual(Math.Sqrt(required), sketch.NoiseDeviation, 1e-12,
                    "The sketch reports a deviation it did not draw from.");
            }
        }

        /// <summary>
        /// The noise is centred on nothing.
        /// </summary>
        /// <remarks>
        /// Noise with a mean is a constant offset an observer can subtract, and it
        /// biases every estimate in the same direction. The bound is three standard
        /// errors of the mean, which is where a correct implementation sits and a
        /// shifted one does not.
        /// </remarks>
        [TestMethod]
        public void TestTheNoiseIsCentredOnNothing()
        {
            foreach (var seed in new ulong[] { 1, 2, 3, 4, 5 })
            {
                var sketch = new PrivateCountMinSketch(2000, 5, 1.0, seed);
                var noise = NoiseOf(sketch);
                var (mean, variance) = MomentsOf(noise);

                var standardError = Math.Sqrt(variance / noise.Length);

                Assert.IsTrue(Math.Abs(mean) < 3 * standardError,
                    $"The noise from seed {seed} averages {mean:F4}, which is more " +
                    $"than three standard errors ({3 * standardError:F4}) from nought.");
            }
        }

        /// <summary>
        /// The noise is shaped like a Gaussian, not merely spread like one.
        /// </summary>
        /// <remarks>
        /// The variance test above would pass just as happily on uniform noise of the
        /// right width, or on a sum of a few uniforms, and neither is what the privacy
        /// proof is about -- the Gaussian mechanism's guarantee comes from the tails of
        /// a Gaussian. Skewness and kurtosis separate them: a Gaussian has a skewness
        /// of nought and a kurtosis of three, where a uniform distribution has a
        /// kurtosis of 1.8 and a Laplace of six.
        /// <para>
        /// Measured at a skewness of -0.014 and a kurtosis of 2.97.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestTheNoiseIsShapedLikeAGaussian()
        {
            var sketch = new PrivateCountMinSketch(4000, 10, 1.0, seed: 21);
            var noise = NoiseOf(sketch);
            var (mean, variance) = MomentsOf(noise);
            var deviation = Math.Sqrt(variance);

            var skewness = noise.Sum(v => Math.Pow((v - mean) / deviation, 3))
                / noise.Length;
            var kurtosis = noise.Sum(v => Math.Pow((v - mean) / deviation, 4))
                / noise.Length;

            Assert.IsTrue(Math.Abs(skewness) < 0.1,
                $"The noise has a skewness of {skewness:F4}; a Gaussian has none.");
            Assert.IsTrue(Math.Abs(kurtosis - 3) < 0.2,
                $"The noise has a kurtosis of {kurtosis:F4}, where a Gaussian has 3, a " +
                "uniform distribution 1.8 and a Laplace 6.");

            // And the tails are where a Gaussian's guarantee lives, so check them
            // rather than trusting two moments: about two thirds within one deviation,
            // 95% within two, 99.7% within three.
            foreach (var (multiple, share) in new[] { (1.0, 0.6827), (2.0, 0.9545), (3.0, 0.9973) })
            {
                var within = noise.Count(v => Math.Abs(v - mean) < multiple * deviation)
                    / (double)noise.Length;

                Assert.AreEqual(share, within, 0.01,
                    $"{within:P2} of the noise lies within {multiple} deviations where " +
                    $"a Gaussian puts {share:P2}.");
            }
        }

        /// <summary>
        /// The noise grows as the budget shrinks, in the proportion the mechanism sets.
        /// </summary>
        /// <remarks>
        /// The variance is the depth over the budget, so tenfold less budget is tenfold
        /// more variance. A mechanism that took the budget into account in the wrong
        /// direction, or not at all, would still pass a test written at a single
        /// budget.
        /// </remarks>
        [TestMethod]
        public void TestTheNoiseScalesWithTheBudgetAndTheDepth()
        {
            var baseline = MomentsOf(NoiseOf(
                new PrivateCountMinSketch(4000, 5, 1.0, seed: 13))).Variance;

            var tenthBudget = MomentsOf(NoiseOf(
                new PrivateCountMinSketch(4000, 5, 0.1, seed: 13))).Variance;
            Assert.AreEqual(10.0, tenthBudget / baseline, 0.05,
                "A tenth of the budget should be ten times the variance.");

            var doubleDepth = MomentsOf(NoiseOf(
                new PrivateCountMinSketch(4000, 10, 1.0, seed: 13))).Variance;
            Assert.AreEqual(2.0, doubleDepth / baseline, 0.1,
                "Twice the depth is twice the sensitivity squared, so twice the " +
                "variance.");

            // The width has nothing to do with it: more counters is not more privacy.
            var wider = MomentsOf(NoiseOf(
                new PrivateCountMinSketch(16000, 5, 1.0, seed: 13))).Variance;
            Assert.AreEqual(1.0, wider / baseline, 0.05,
                "Widening a sketch should not change the noise on each counter.");
        }

        /// <summary>
        /// Every counter gets its own draw.
        /// </summary>
        /// <remarks>
        /// One draw shared across a row, or across the sketch, would be far easier to
        /// subtract back off and would not be the mechanism at all. Neighbouring
        /// counters should also be uncorrelated, which a generator reused in step
        /// across rows would not be.
        /// </remarks>
        [TestMethod]
        public void TestEveryCounterGetsItsOwnDraw()
        {
            var sketch = new PrivateCountMinSketch(2000, 5, 1.0, seed: 31);

            Assert.AreEqual(NoiseOf(sketch).Length, NoiseOf(sketch).Distinct().Count(),
                "Two counters share a value, so they did not get their own draws.");

            // Correlation between the first two rows, which a generator running in step
            // would make one.
            var first = sketch.Counters[0];
            var second = sketch.Counters[1];
            var meanFirst = first.Average();
            var meanSecond = second.Average();

            var covariance = 0.0;
            for (var i = 0; i < first.Length; i++)
            {
                covariance += (first[i] - meanFirst) * (second[i] - meanSecond);
            }
            covariance /= first.Length;

            var correlation = covariance
                / (Math.Sqrt(MomentsOf(first).Variance) * Math.Sqrt(MomentsOf(second).Variance));

            Assert.IsTrue(Math.Abs(correlation) < 0.05,
                $"The first two rows of noise correlate at {correlation:F4}.");
        }

        /// <summary>
        /// The noise is drawn once, when the sketch is built, and never again.
        /// </summary>
        /// <remarks>
        /// This is a privacy property rather than a performance one, and it is the
        /// easiest to get wrong by writing the obvious thing. Noise drawn afresh on each
        /// query can be averaged away by asking the same question enough times, and a
        /// guarantee that dissolves under repetition is no guarantee. Asking twice must
        /// give the same answer, exactly.
        /// </remarks>
        [TestMethod]
        public void TestTheNoiseIsDrawnOnceAndNotPerQuery()
        {
            var sketch = new PrivateCountMinSketch(1000, 5, 1.0, seed: 5);
            for (var i = 0; i < 1_000; i++)
            {
                sketch.Add(Key(i % 100));
            }

            var first = sketch.Count(Key(7));
            for (var i = 0; i < 100; i++)
            {
                Assert.AreEqual(first, sketch.Count(Key(7)),
                    "Asking the same question twice gave two answers, so the noise is " +
                    "being drawn per query and can be averaged away.");
            }

            // The same goes for an item that was never added.
            var absent = sketch.Count(Key(999_999));
            Assert.AreEqual(absent, sketch.Count(Key(999_999)));
        }

        /// <summary>
        /// Two sketches of the same data differ, and the same seed reproduces one.
        /// </summary>
        [TestMethod]
        public void TestTheSeedDecidesTheNoiseAndNothingElseDoes()
        {
            var one = new PrivateCountMinSketch(500, 4, 1.0, seed: 100);
            var same = new PrivateCountMinSketch(500, 4, 1.0, seed: 100);
            var other = new PrivateCountMinSketch(500, 4, 1.0, seed: 101);

            CollectionAssert.AreEqual(NoiseOf(one), NoiseOf(same),
                "The same seed should give the same noise.");
            CollectionAssert.AreNotEqual(NoiseOf(one), NoiseOf(other),
                "Two seeds should give different noise; a sketch whose noise does not " +
                "depend on its seed is a sketch everyone can subtract.");

            for (var i = 0; i < 100; i++)
            {
                one.Add(Key(i));
                same.Add(Key(i));
            }

            Assert.AreEqual(one.Count(Key(3)), same.Count(Key(3)));
        }

        /// <summary>
        /// Converting between the two ways of writing a privacy budget round-trips.
        /// </summary>
        /// <remarks>
        /// Policies are written in epsilon and delta; the mechanism wants rho. Getting
        /// the conversion wrong in the safe direction wastes accuracy and in the unsafe
        /// direction quietly breaks the promise, so it is worth pinning in both
        /// directions.
        /// </remarks>
        [TestMethod]
        public void TestTheTwoWaysOfWritingABudgetAgree()
        {
            foreach (var (epsilon, delta) in new[]
            {
                (1.0, 1e-6), (0.1, 1e-6), (3.0, 1e-9), (0.5, 1e-5),
            })
            {
                var rho = PrivateCountMinSketch.BudgetFor(epsilon, delta);
                var back = PrivateCountMinSketch.EpsilonFor(rho, delta);

                Assert.AreEqual(epsilon, back, epsilon * 1e-9,
                    $"A budget for epsilon {epsilon} converts back to {back}.");
                Assert.IsTrue(rho > 0 && rho < epsilon,
                    $"A zero-concentrated budget of {rho} for an epsilon of {epsilon} " +
                    "is not in the range the conversion can produce.");
            }

            // A tighter epsilon is a smaller budget.
            Assert.IsTrue(
                PrivateCountMinSketch.BudgetFor(0.1, 1e-6)
                    < PrivateCountMinSketch.BudgetFor(1.0, 1e-6));
        }

        /// <summary>
        /// Estimates are usable, and get less so as the guarantee tightens.
        /// </summary>
        /// <remarks>
        /// The point of the structure is that it answers the question at all. Measured
        /// over a skewed stream of a hundred thousand items: a mean absolute error of
        /// 2.9 at a budget of 10, 4.1 at 1, and 7.5 at 0.1. Most of the error at the
        /// loose end is ordinary Count-Min collision rather than noise, which is why
        /// the figures rise more slowly than the noise does.
        /// </remarks>
        [TestMethod]
        public void TestEstimatesAreUsableAndDegradeAsPrivacyTightens()
        {
            var errors = new List<double>();

            foreach (var rho in new[] { 10.0, 1.0, 0.1 })
            {
                var sketch = new PrivateCountMinSketch(2000, 5, rho, seed: 3);
                var truth = new Dictionary<int, int>();
                var random = new Random(1);

                for (var i = 0; i < 100_000; i++)
                {
                    var key = (int)(2000 * Math.Pow(random.NextDouble(), 3));
                    sketch.Add(Key(key));
                    truth.TryGetValue(key, out var seen);
                    truth[key] = seen + 1;
                }

                var total = 0.0;
                foreach (var (key, count) in truth)
                {
                    total += Math.Abs(sketch.Count(Key(key)) - count);
                }
                errors.Add(total / truth.Count);
            }

            Assert.IsTrue(errors[0] < 5,
                $"At a loose budget the mean error was {errors[0]:F2}, which is worse " +
                "than the collisions alone should cost.");
            Assert.IsTrue(errors[2] > errors[0],
                $"A tighter budget of 0.1 gave a mean error of {errors[2]:F2} against " +
                $"{errors[0]:F2} at 10. Privacy is supposed to cost accuracy; if it " +
                "does not, the noise is not reaching the estimates.");
            Assert.IsTrue(errors[2] < 50,
                $"At the tightest budget the mean error was {errors[2]:F2}, which is " +
                "too far off to be useful.");
        }

        /// <summary>
        /// An item never added can be reported below nought, and that is not a fault.
        /// </summary>
        /// <remarks>
        /// A plain Count-Min Sketch never undercounts. This one does, because the
        /// counters start below nought as often as above it, and taking the smallest of
        /// several pulls the answer further down still. Callers have to know: the
        /// one-sided guarantee people rely on from Count-Min is gone, and it is the
        /// price of the noise rather than a defect in it.
        /// </remarks>
        [TestMethod]
        public void TestAnItemNeverAddedCanReadBelowNothing()
        {
            var sketch = new PrivateCountMinSketch(2000, 5, 1.0, seed: 11);

            var readings = new List<double>();
            for (var i = 0; i < 2_000; i++)
            {
                readings.Add(sketch.Count(Key(500_000 + i)));
            }

            Assert.IsTrue(readings.Any(r => r < 0),
                "No absent item read below nought, which a noisy minimum should do " +
                "most of the time.");
            Assert.IsTrue(readings.Average() < 0,
                $"Absent items averaged {readings.Average():F2}. Taking the smallest of " +
                "several noisy counters should pull the answer below nought on average.");
        }

        /// <summary>
        /// The parameters are checked.
        /// </summary>
        [TestMethod]
        public void TestBadParametersAreRefused()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new PrivateCountMinSketch(0, 5, 1.0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new PrivateCountMinSketch(100, 0, 1.0));

            foreach (var rho in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => new PrivateCountMinSketch(100, 5, rho),
                    $"A budget of {rho} should be refused.");
            }

            foreach (var delta in new[] { 0.0, 1.0, -0.5, double.NaN })
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => PrivateCountMinSketch.EpsilonFor(1.0, delta));
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => PrivateCountMinSketch.BudgetFor(1.0, delta));
            }

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => PrivateCountMinSketch.EpsilonFor(0, 1e-6));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => PrivateCountMinSketch.BudgetFor(0, 1e-6));

            var sketch = new PrivateCountMinSketch(100, 5, 1.0);
            Assert.ThrowsExactly<ArgumentNullException>(() => sketch.Add((byte[])null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => sketch.Count((byte[])null!));
        }

        /// <summary>
        /// A sketch built without a seed still gets noise, and different noise each time.
        /// </summary>
        /// <remarks>
        /// The seed exists for these tests. A deployment must not supply one, because
        /// anyone who knows it can subtract the noise back off and recover the counters
        /// exactly -- so the unseeded path is the one that matters and it has to work.
        /// </remarks>
        [TestMethod]
        public void TestAnUnseededSketchGetsItsOwnNoise()
        {
            var one = NoiseOf(new PrivateCountMinSketch(500, 4, 1.0));
            var other = NoiseOf(new PrivateCountMinSketch(500, 4, 1.0));

            CollectionAssert.AreNotEqual(one, other,
                "Two unseeded sketches drew the same noise, so the noise is not " +
                "unpredictable and can be subtracted off.");

            var (_, variance) = MomentsOf(one);
            Assert.AreEqual(4.0, variance, 4.0 * 0.15,
                "An unseeded sketch drew noise of the wrong scale.");
        }

        /// <summary>
        /// A sketch round-trips exactly: same shape, same budget, same count, and the
        /// same answer for every item, because the counters carry their noise and the
        /// noise is what is written.
        /// </summary>
        [TestMethod]
        public void TestRoundTripsThroughPersistenceExactly()
        {
            var original = new PrivateCountMinSketch(64, 4, 0.5, seed: 12345);
            for (var i = 0; i < 500; i++)
            {
                original.Add(Key($"item-{i % 50}"));
            }

            var restored = Persistence.FromByteArray<PrivateCountMinSketch>(
                original.ToByteArray());

            Assert.AreEqual(original.Width, restored.Width);
            Assert.AreEqual(original.Depth, restored.Depth);
            Assert.AreEqual(original.Rho, restored.Rho);
            Assert.AreEqual(original.TotalCount(), restored.TotalCount());
            Assert.AreEqual(original.NoiseDeviation, restored.NoiseDeviation);

            for (var i = 0; i < 50; i++)
            {
                Assert.AreEqual(
                    original.Count(Key($"item-{i}")), restored.Count(Key($"item-{i}")),
                    $"item-{i} must estimate identically after a round trip.");
            }

            for (var i = 0; i < original.Depth; i++)
            {
                CollectionAssert.AreEqual(
                    original.Counters[i], restored.Counters[i],
                    $"row {i} must be restored counter for counter, noise included.");
            }
        }

        /// <summary>
        /// A restored sketch keeps counting. Adding touches no randomness -- it
        /// increments counters that already carry their noise -- so a sketch with no
        /// seed is a fully working sketch, not a read-only snapshot. This is the whole
        /// reason the seed can be left out of the payload without crippling it.
        /// </summary>
        [TestMethod]
        public void TestARestoredSketchKeepsCounting()
        {
            var original = new PrivateCountMinSketch(64, 4, 0.5, seed: 999);
            for (var i = 0; i < 100; i++)
            {
                original.Add(Key("steady"));
            }

            var restored = Persistence.FromByteArray<PrivateCountMinSketch>(
                original.ToByteArray());

            var before = restored.Count(Key("steady"));
            for (var i = 0; i < 100; i++)
            {
                restored.Add(Key("steady"));
                original.Add(Key("steady"));
            }

            Assert.AreEqual(before + 100, restored.Count(Key("steady")), 1e-9,
                "a restored sketch must count exactly as it did before being written.");
            Assert.AreEqual(200UL, restored.TotalCount());
            Assert.AreEqual(
                original.Count(Key("steady")), restored.Count(Key("steady")), 1e-9,
                "the restored sketch and the one it came from must stay in step, " +
                "since neither draws randomness while counting.");
        }

        /// <summary>
        /// The payload must not carry the seed, and the strongest available statement
        /// of that is a size argument: the payload is exactly the header, the four
        /// shape fields and the counters, with no room left for anything else. An
        /// eight-byte seed appended -- the obvious way for one to creep in -- would
        /// show up here as surely as it would show up in a diff.
        /// </summary>
        [TestMethod]
        public void TestThePayloadHasNoRoomForASeed()
        {
            var sketch = new PrivateCountMinSketch(32, 3, 0.25, seed: 4242);
            sketch.Add(Key("one"));

            // Envelope is 14 bytes of header and 4 of checksum; the body is
            // width, depth (4 each), rho, count (8 each), then the counters.
            var expected = 14 + 4 + 4 + 4 + 8 + 8 + (32 * 3 * 8);

            Assert.HasCount(expected, sketch.ToByteArray(),
                "the payload is not exactly its header, shape and counters, which " +
                "means something else is being written -- and the one thing that " +
                "must never be written is the seed.");
        }
    }
}
