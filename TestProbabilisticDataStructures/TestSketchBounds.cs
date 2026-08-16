using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The guarantee a frequency sketch actually makes, as opposed to the mechanics
    /// of adding and counting.
    /// <para>
    /// Count-Min's contract is two-sided: it never reports less than the truth, and
    /// with probability at least 1 - delta it reports no more than the truth plus
    /// epsilon times the total count. Only the first half was covered, and that half
    /// is the trivial one -- a sketch that returned ulong.MaxValue for every query
    /// passed it. Nothing measured the ceiling, which is the half the sizing math
    /// exists to deliver.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestSketchBounds
    {
        /// <summary>Overestimate allowed, as a fraction of the total count.</summary>
        private const double Epsilon = 0.001;

        /// <summary>
        /// Probability of exceeding that overestimate. This is a failure probability:
        /// a smaller delta is a deeper sketch.
        /// </summary>
        private const double Delta = 0.01;

        private const int DistinctItems = 20000;

        private static byte[] Key(int i) => Encoding.UTF8.GetBytes($"item-{i}");

        /// <summary>
        /// A skewed stream: a few items appear often, most appear once. Frequency
        /// estimation is uninteresting on a uniform stream, and the paper's bound is
        /// stated against the total count, which a skewed stream concentrates.
        /// </summary>
        private static Dictionary<int, ulong> BuildStream(CountMinSketch cms)
        {
            var truth = new Dictionary<int, ulong>(DistinctItems);
            for (int i = 0; i < DistinctItems; i++)
            {
                var times = (ulong)Math.Max(1, 1000 / (i + 1));
                var key = Key(i);
                for (ulong t = 0; t < times; t++)
                {
                    cms.Add(key);
                }
                truth[i] = times;
            }
            return truth;
        }

        [TestMethod]
        public void TestCountMinSketchStaysWithinItsErrorBound()
        {
            var cms = new CountMinSketch(Epsilon, Delta);
            var truth = BuildStream(cms);

            var total = cms.TotalCount();
            var allowed = Epsilon * total;

            var violations = 0;
            var overcounted = 0;
            var overcountSum = 0.0;

            foreach (var (i, times) in truth)
            {
                var estimate = cms.Count(Key(i));

                Assert.IsGreaterThanOrEqualTo(times, estimate,
                    $"item-{i} was added {times} times but the sketch reported " +
                    $"{estimate}. A Count-Min Sketch must never undercount.");

                var overcount = estimate - times;
                overcountSum += overcount;
                if (overcount > 0) overcounted++;
                if (overcount > allowed) violations++;
            }

            var perRowMean = (double)total / cms.Width;
            var meanOvercount = overcountSum / truth.Count;
            var violationRate = (double)violations / truth.Count;

            Console.WriteLine($"width={cms.Width} depth={cms.Depth} total={total}");
            Console.WriteLine($"allowed (eps*N)={allowed:F2}  per-row mean (N/w)={perRowMean:F3}");
            Console.WriteLine($"mean overcount={meanOvercount:F3}  ratio to per-row mean={meanOvercount / perRowMean:F4}");
            Console.WriteLine($"overcounted={overcounted}/{truth.Count}  violations={violations} rate={violationRate:P4}");

            Assert.IsGreaterThan(0, overcounted,
                "No item was overcounted at all, so the error bound is satisfied " +
                "trivially and this test proves nothing about the sizing math.");

            Assert.IsLessThanOrEqualTo(Delta, violationRate,
                $"{violations} of {truth.Count} estimates exceeded the truth by more " +
                $"than epsilon*N ({allowed:F2}), a rate of {violationRate:P4} against " +
                $"a configured failure probability of {Delta:P2}.");
        }

        [TestMethod]
        public void TestCountMinSketchGeometryMatchesThePaper()
        {
            var cms = new CountMinSketch(Epsilon, Delta);

            Assert.AreEqual((uint)Math.Ceiling(Math.E / Epsilon), cms.Width,
                "Width must be ceil(e/epsilon). The epsilon*N bound follows from " +
                "that width by Markov's inequality; any other width silently " +
                "changes the error the sketch delivers.");

            Assert.AreEqual((uint)Math.Ceiling(Math.Log(1 / Delta)), cms.Depth,
                "Depth must be ceil(ln(1/delta)). Each row is an independent chance " +
                "to miss the bound, so the depth is what makes delta true.");
        }

        /// <summary>
        /// TopK sits on a Count-Min Sketch, so it inherits that sketch's overestimate.
        /// The condition under which it still answers correctly is a gap: if the k-th
        /// and (k+1)-th frequencies differ by more than epsilon*N, no amount of
        /// overcounting inside the bound can reorder them.
        /// <para>
        /// The existing exactness test sizes the sketch so wide that the stream is
        /// counted exactly, which exercises the heap but never the sketch. Here the
        /// stream has ten times more distinct items than the sketch has columns, so
        /// collisions are forced by pigeonhole and the gap is doing the work.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestTopKRecoversTheTrueTopKWhenTheGapExceedsTheErrorBound()
        {
            const uint K = 10;
            const int TailItems = 30000;

            // Head frequencies descend by 100, well clear of epsilon*N below.
            var head = new Dictionary<string, int>();
            for (int i = 0; i < K; i++)
            {
                head[$"head-{i}"] = 2000 - (i * 100);
            }

            var stream = new List<string>();
            foreach (var (name, times) in head)
            {
                for (int t = 0; t < times; t++) stream.Add(name);
            }
            for (int i = 0; i < TailItems; i++)
            {
                stream.Add($"tail-{i}");
            }

            var rand = new Random(17);
            for (int i = stream.Count - 1; i > 0; i--)
            {
                int j = rand.Next(i + 1);
                (stream[i], stream[j]) = (stream[j], stream[i]);
            }

            var topK = new TopK(Epsilon, Delta, K);
            // Same geometry, same stream: used only to show the sketch really is
            // under collision pressure, so a pass here is not the exact case again.
            var witness = new CountMinSketch(Epsilon, Delta);
            foreach (var name in stream)
            {
                var key = Encoding.UTF8.GetBytes(name);
                topK.Add(key);
                witness.Add(key);
            }

            var total = stream.Count;
            var allowed = Epsilon * total;
            var minHeadGap = 100;

            var inflatedTail = 0;
            for (int i = 0; i < TailItems; i++)
            {
                if (witness.Count(Encoding.UTF8.GetBytes($"tail-{i}")) > 1) inflatedTail++;
            }

            Console.WriteLine($"N={total} allowed(eps*N)={allowed:F2} minHeadGap={minHeadGap}");
            Console.WriteLine($"distinct={head.Count + TailItems} width={witness.Width} depth={witness.Depth}");
            Console.WriteLine($"tail items overcounted by the witness sketch: {inflatedTail}/{TailItems}");

            Assert.IsGreaterThan(0, inflatedTail,
                "No tail item was overcounted, so the sketch counted this stream " +
                "exactly and the gap condition was never tested.");

            Assert.IsLessThan(minHeadGap, allowed,
                $"The test is only meaningful while epsilon*N ({allowed:F2}) stays " +
                $"below the gap between adjacent head frequencies ({minHeadGap}).");

            var got = topK.Elements()
                .Select(e => Encoding.UTF8.GetString(e.Data.Span))
                .ToHashSet();

            Assert.IsTrue(got.SetEquals(head.Keys.ToHashSet()),
                $"missing {string.Join(", ", head.Keys.Except(got))}, " +
                $"unexpected {string.Join(", ", got.Except(head.Keys))}. Every head " +
                $"item leads the tail by far more than epsilon*N ({allowed:F2}), so " +
                "the overestimate cannot account for a swap.");

            var freqs = topK.Elements().Select(e => e.Freq).ToArray();
            CollectionAssert.AreEqual(freqs.OrderBy(x => x).ToArray(), freqs,
                "Elements() must be ascending by frequency.");
        }

        /// <summary>
        /// DDSketch's relative-error bound is deterministic, not probabilistic: there
        /// is no delta, and every quantile it returns must sit within the requested
        /// relative accuracy of the true one. A single violation is a defect, not an
        /// unlucky draw, so this asserts on every quantile rather than on a rate.
        /// <para>
        /// Spanning six orders of magnitude is the point. The guarantee is relative,
        /// which is what separates this from a sketch with an absolute bound -- the
        /// error at 1e6 is allowed to be a million times the error at 1, and the
        /// bucket boundaries are geometric to match. A structure that quietly fell
        /// back to linear bucketing would still look correct on a narrow range.
        /// </para>
        /// </summary>
        [TestMethod]
        [DataRow(0.01)]
        [DataRow(0.02)]
        [DataRow(0.05)]
        public void TestDDSketchHoldsItsRelativeErrorOnEveryQuantile(double accuracy)
        {
            var sketch = new DDSketch(accuracy);

            // Log-uniform over [1, 1e6]: the range where a relative guarantee differs
            // most from an absolute one.
            var values = new List<double>();
            var rand = new Random(23);
            for (int i = 0; i < 20000; i++)
            {
                var v = Math.Pow(10, rand.NextDouble() * 6);
                values.Add(v);
                sketch.Add(v);
            }
            values.Sort();

            Assert.AreEqual((ulong)values.Count, sketch.Count());

            var worst = 0.0;
            var worstAt = 0.0;

            foreach (var q in new[] { 0.0, 0.01, 0.05, 0.25, 0.5, 0.75, 0.9, 0.95, 0.99, 1.0 })
            {
                // Lower-index convention, matching what the sketch reports.
                var idx = (int)Math.Floor(q * (values.Count - 1));
                var truth = values[idx];
                var got = sketch.Quantile(q);

                var relative = Math.Abs(got - truth) / truth;
                if (relative > worst) { worst = relative; worstAt = q; }

                Assert.IsLessThanOrEqualTo(accuracy, relative,
                    $"q={q}: the sketch reported {got:G6} against a true value of " +
                    $"{truth:G6}, a relative error of {relative:P3} where {accuracy:P2} " +
                    "was requested. DDSketch's bound is deterministic -- this is not " +
                    "a tail event.");
            }

            Console.WriteLine($"accuracy={accuracy} worst relative error={worst:P4} at q={worstAt}");

            // The bound is not just respected, it is nearly saturated, and that is
            // predictable rather than incidental. A value at the top of bucket
            // (gamma^(i-1), gamma^i] is reported as the midpoint 2*gamma^i/(gamma+1),
            // a relative error of (gamma-1)/(gamma+1), and substituting
            // gamma = (1+a)/(1-a) leaves exactly a. So the worst error over a range
            // this wide should come close to the accuracy requested -- landing far
            // below it would mean the buckets are finer than asked for, which is a
            // sizing defect even though it looks like accuracy.
            Assert.IsGreaterThan(accuracy * 0.5, worst,
                $"The worst relative error was {worst:P4} against a requested " +
                $"{accuracy:P2}. Over six orders of magnitude some value should land " +
                "near a bucket edge, so an error this small means the bucketing is " +
                "finer than the accuracy called for -- or that it is not geometric.");
        }

        /// <summary>
        /// A HyperLogLog's accuracy claim is not about any single count -- it is that
        /// the estimate has relative standard error 1.04/sqrt(m) across independent
        /// streams. One count landing close proves nothing, because the estimator is
        /// unbiased and any single draw can land anywhere in the distribution. Only the
        /// spread over many streams tests what m was chosen for.
        /// <para>
        /// The existing HyperLogLog tests each check a single count against a loose
        /// tolerance, which a filter with a quarter of its registers would still pass
        /// most of the time. This measures the distribution instead.
        /// </para>
        /// <para>
        /// Nothing here is random: each trial's stream is keyed by its index, so the
        /// measurement is identical on every run and a failure is reproducible rather
        /// than a bad draw. The bounds are set from measured values -- the observed
        /// ratios are 0.98, 1.05 and 1.03 -- and widened to about three and a half
        /// standard errors of the spread estimator, which is sd/sqrt(2(T-1)).
        /// </para>
        /// </summary>
        [TestMethod]
        [DataRow(256u)]
        [DataRow(1024u)]
        [DataRow(4096u)]
        public void TestHyperLogLogDeliversItsRelativeStandardError(uint m)
        {
            const int Trials = 60;
            const int N = 50000;

            var errors = new List<double>(Trials);
            for (int t = 0; t < Trials; t++)
            {
                var hll = new HyperLogLog(m);
                for (int i = 0; i < N; i++)
                {
                    hll.Add(Encoding.UTF8.GetBytes($"t{t}-item-{i}"));
                }
                errors.Add(((double)hll.Count() - N) / N);
            }

            var mean = errors.Average();
            var spread = Math.Sqrt(errors.Sum(e => (e - mean) * (e - mean)) / (Trials - 1));
            var predicted = 1.04 / Math.Sqrt(m);
            var ratio = spread / predicted;

            Console.WriteLine($"m={m} predicted={predicted:P3} observed={spread:P3} " +
                $"ratio={ratio:F3} bias={mean:P3}");

            Assert.IsGreaterThanOrEqualTo(0.75, ratio,
                $"m={m}: the estimate's spread was {spread:P3} against a predicted " +
                $"{predicted:P3}. Landing well inside the prediction means the " +
                "registers are doing more work than m accounts for, which points at " +
                "the estimator rather than at good luck.");

            Assert.IsLessThanOrEqualTo(1.35, ratio,
                $"m={m}: the estimate's spread was {spread:P3} against a predicted " +
                $"{predicted:P3}. A HyperLogLog that misses its relative standard " +
                "error is delivering the accuracy of a smaller register array than " +
                "the caller paid for.");

            // The estimator is unbiased, so the mean error should sit within a few
            // standard errors of zero. A systematic offset is what a wrong alpha
            // looks like, and it hides completely in the spread.
            var standardError = spread / Math.Sqrt(Trials);
            Assert.IsLessThanOrEqualTo(4 * standardError, Math.Abs(mean),
                $"m={m}: the mean relative error was {mean:P3}, more than four " +
                $"standard errors ({standardError:P3}) from zero. The estimator is " +
                "meant to be unbiased; a consistent offset is a bias-correction " +
                "constant that does not match the register count.");
        }

        /// <summary>
        /// Shared spread measurement: runs independent streams and returns the mean
        /// and standard deviation of the relative error. Streams are keyed by trial
        /// index rather than drawn randomly, so every number here reproduces exactly.
        /// </summary>
        private static (double Mean, double Spread) MeasureSpread(int trials, Func<int, double> trial)
        {
            var errors = new List<double>(trials);
            for (int t = 0; t < trials; t++)
            {
                errors.Add(trial(t));
            }
            var mean = errors.Average();
            var spread = Math.Sqrt(errors.Sum(e => (e - mean) * (e - mean)) / (trials - 1));
            return (mean, spread);
        }

        /// <summary>
        /// HyperLogLog++ replaces the original estimator with Ertl's, which removes the
        /// range corrections rather than tuning them. What it must not do is give up
        /// the accuracy the register count buys: the relative standard error is still
        /// 1.04/sqrt(m), and an estimator change that quietly widened the distribution
        /// would leave every single-count test passing.
        /// </summary>
        [TestMethod]
        [DataRow(8u)]
        [DataRow(10u)]
        [DataRow(12u)]
        public void TestHyperLogLogPlusDeliversItsRelativeStandardError(uint precision)
        {
            const int Trials = 80;
            const int N = 50000;
            var m = 1u << (int)precision;

            var (mean, spread) = MeasureSpread(Trials, t =>
            {
                var hll = new HyperLogLogPlus(precision);
                for (int i = 0; i < N; i++)
                {
                    hll.Add(Encoding.UTF8.GetBytes($"t{t}-i{i}"));
                }
                return ((double)hll.Count() - N) / N;
            });

            var predicted = 1.04 / Math.Sqrt(m);
            var ratio = spread / predicted;
            Console.WriteLine($"p={precision} m={m} predicted={predicted:P3} " +
                $"observed={spread:P3} ratio={ratio:F3} bias={mean:P3}");

            Assert.IsGreaterThanOrEqualTo(0.75, ratio,
                $"p={precision}: spread {spread:P3} against predicted {predicted:P3}.");
            Assert.IsLessThanOrEqualTo(1.35, ratio,
                $"p={precision}: spread {spread:P3} against predicted {predicted:P3}. " +
                "The Ertl estimator must not cost accuracy the registers already paid " +
                "for.");

            var standardError = spread / Math.Sqrt(Trials);
            Assert.IsLessThanOrEqualTo(4 * standardError, Math.Abs(mean),
                $"p={precision}: mean relative error {mean:P3} is more than four " +
                $"standard errors ({standardError:P3}) from zero. Ertl's estimator is " +
                "meant to be unbiased across the whole range, which is the reason for " +
                "dropping the original's corrections.");
        }

        /// <summary>
        /// MinHash estimates the Jaccard index by the fraction of signature positions
        /// that agree, so each position is a Bernoulli trial with success probability J
        /// and the estimate's standard error is sqrt(J(1-J)/k). That formula is the
        /// whole reason to pick one k over another.
        /// <para>
        /// The bags are much larger than k on purpose. A signature longer than the set
        /// it summarizes is degenerate, and at k=1024 against 1000-element bags the
        /// measured spread misses the prediction by a quarter for that reason alone.
        /// </para>
        /// </summary>
        [TestMethod]
        [DataRow(64)]
        [DataRow(256)]
        [DataRow(1024)]
        public void TestMinHashDeliversTheJaccardStandardError(int k)
        {
            const int Trials = 80;
            const int BagSize = 6000;
            const int Overlap = 3000;
            var jaccard = (double)Overlap / (2 * BagSize - Overlap);

            var (mean, spread) = MeasureSpread(Trials, t =>
            {
                var a = Enumerable.Range(0, BagSize).Select(i => $"t{t}-w{i}").ToArray();
                var b = Enumerable.Range(BagSize - Overlap, BagSize)
                    .Select(i => $"t{t}-w{i}").ToArray();
                return MinHash.Similarity(MinHash.Signature(a, k),
                    MinHash.Signature(b, k)) - jaccard;
            });

            var predicted = Math.Sqrt(jaccard * (1 - jaccard) / k);
            var ratio = spread / predicted;
            Console.WriteLine($"k={k} J={jaccard:F4} predicted={predicted:F5} " +
                $"observed={spread:F5} ratio={ratio:F3} bias={mean:F5}");

            Assert.IsGreaterThanOrEqualTo(0.7, ratio,
                $"k={k}: spread {spread:F5} against predicted {predicted:F5}.");
            Assert.IsLessThanOrEqualTo(1.4, ratio,
                $"k={k}: spread {spread:F5} against predicted {predicted:F5}. Each " +
                "signature position is one Bernoulli trial, so a wider spread means " +
                "the positions are not independent or not uniform.");

            var standardError = spread / Math.Sqrt(Trials);
            Assert.IsLessThanOrEqualTo(4 * standardError, Math.Abs(mean),
                $"k={k}: mean error {mean:F5} is more than four standard errors " +
                $"({standardError:F5}) from zero. The estimator is the fraction of " +
                "agreeing positions, which is unbiased for J by construction.");
        }

        /// <summary>
        /// A theta sketch's nominal accuracy is 1/sqrt(k). This implementation does
        /// better than that, and the reason is structural rather than lucky: theta is
        /// set to the k-th smallest hash while the buffer holds up to 2k values, so the
        /// estimate is built from somewhere between k and 2k samples rather than
        /// exactly k.
        /// <para>
        /// Measured ratios against nominal are 0.765, 0.698 and 0.640 for k of 256,
        /// 1024 and 4096. So the assertion is one-sided: the sketch must be at least as
        /// accurate as the k it was asked for. Pinning it to the measured ratio instead
        /// would freeze an implementation detail -- the buffer policy -- into a test
        /// about the guarantee.
        /// </para>
        /// </summary>
        [TestMethod]
        [DataRow(256u)]
        [DataRow(1024u)]
        [DataRow(4096u)]
        public void TestThetaSketchIsAtLeastAsAccurateAsItsNominalEntries(uint k)
        {
            const int Trials = 40;
            const int N = 50000;

            var (mean, spread) = MeasureSpread(Trials, t =>
            {
                var sketch = new ThetaSketch(k);
                for (int i = 0; i < N; i++)
                {
                    sketch.Add(Encoding.UTF8.GetBytes($"t{t}-i{i}"));
                }
                return ((double)sketch.Count() - N) / N;
            });

            var nominal = 1.0 / Math.Sqrt(k);
            var ratio = spread / nominal;
            Console.WriteLine($"k={k} nominal={nominal:P3} observed={spread:P3} " +
                $"ratio={ratio:F3} bias={mean:P3}");

            Assert.IsLessThanOrEqualTo(1.25, ratio,
                $"k={k}: spread {spread:P3} against a nominal {nominal:P3}. The sketch " +
                "is delivering less accuracy than the entry count it was asked for.");

            // Better than nominal is expected, but not arbitrarily better: that would
            // mean theta sampling never engaged and the sketch simply kept everything,
            // in which case this measures an exact count and proves nothing.
            Assert.IsGreaterThanOrEqualTo(0.35, ratio,
                $"k={k}: spread {spread:P3} is far below the nominal {nominal:P3}, " +
                "which suggests the sketch retained the whole stream instead of " +
                "sampling it.");

            var standardError = spread / Math.Sqrt(Trials);
            Assert.IsLessThanOrEqualTo(4 * standardError, Math.Abs(mean),
                $"k={k}: mean relative error {mean:P3} is more than four standard " +
                $"errors ({standardError:P3}) from zero.");
        }

        /// <summary>
        /// The sketch's entire premise is that it costs the same whatever it is shown.
        /// </summary>
        [TestMethod]
        public void TestThetaSketchMemoryDoesNotGrowWithTheStream()
        {
            var small = new ThetaSketch(1024);
            var large = new ThetaSketch(1024);

            for (int i = 0; i < 20000; i++) small.Add(Encoding.UTF8.GetBytes($"i{i}"));
            for (int i = 0; i < 400000; i++) large.Add(Encoding.UTF8.GetBytes($"i{i}"));

            Console.WriteLine($"20k items: {small.SizeInBytes()} bytes, " +
                $"400k items: {large.SizeInBytes()} bytes");

            Assert.AreEqual(small.SizeInBytes(), large.SizeInBytes(),
                "Twenty times the stream must not cost a byte more. A theta sketch " +
                "that grew with its input would have no advantage over the set it " +
                "is standing in for.");
        }

        /// <summary>
        /// Ertl's estimator earns its place in the range where many registers are still
        /// empty. The original HyperLogLog switched to linear counting there and back
        /// again, and the switch is what the 2013 work showed to be the error's main
        /// source; Ertl replaces it with a sigma term that is continuous across the
        /// whole range.
        /// <para>
        /// That term is unreachable at high cardinality. Once every register is
        /// occupied counts[0] is zero, so the sigma correction contributes nothing and
        /// deleting it changes not one digit -- the higher-cardinality test above
        /// passes bit-identically with it removed. This is the regime that exercises
        /// it: past the sparse threshold of m/8, so the estimator is genuinely running,
        /// but with thousands of registers still empty.
        /// </para>
        /// <para>
        /// The assertion is on bias rather than spread. The spread here is better than
        /// the asymptotic 1.04/sqrt(m), because that figure assumes n is large next to
        /// m, so holding it to the asymptote would assert nothing. Staying unbiased
        /// while most registers are empty is the property the correction exists for.
        /// </para>
        /// </summary>
        [TestMethod]
        [DataRow(700)]
        [DataRow(1500)]
        [DataRow(3000)]
        [DataRow(6000)]
        public void TestHyperLogLogPlusStaysUnbiasedWhileRegistersAreStillEmpty(int n)
        {
            const uint Precision = 12;
            const int Trials = 80;
            var m = 1u << (int)Precision;

            var (mean, spread) = MeasureSpread(Trials, t =>
            {
                var hll = new HyperLogLogPlus(Precision);
                for (int i = 0; i < n; i++)
                {
                    hll.Add(Encoding.UTF8.GetBytes($"t{t}-i{i}"));
                }
                return ((double)hll.Count() - n) / n;
            });

            var expectedEmpty = m * Math.Exp(-(double)n / m);
            var standardError = spread / Math.Sqrt(Trials);

            Console.WriteLine($"n={n} empty registers~{expectedEmpty:F0} of {m} " +
                $"spread={spread:P3} bias={mean:P3} se={standardError:P3}");

            Assert.IsGreaterThan(m / 100.0, expectedEmpty,
                $"n={n}: too few registers are expected to remain empty for this to " +
                "test the correction at all.");

            Assert.IsLessThanOrEqualTo(4 * standardError, Math.Abs(mean),
                $"n={n}: the mean relative error was {mean:P3}, more than four " +
                $"standard errors ({standardError:P3}) from zero, with roughly " +
                $"{expectedEmpty:F0} of {m} registers still empty. A biased estimate " +
                "here is the failure the sigma term exists to prevent.");

            Assert.IsLessThanOrEqualTo(1.25 * (1.04 / Math.Sqrt(m)), spread,
                $"n={n}: spread {spread:P3} exceeds the asymptotic 1.04/sqrt(m), " +
                "which the estimator should beat while registers remain empty.");
        }
    }
}
