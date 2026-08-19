using System;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Every promise, tested where the guards stop protecting it: the coarsest
    /// parameters a caller can legally ask for. Fine parameters hide off-by-ones --
    /// SetSketch's default base of 1.001 sat on two of them worth 19% to 100% at
    /// coarse settings -- because at fine settings a one-unit error is a rounding
    /// nuisance and at coarse settings it is the whole answer. The suites' bound
    /// tests run these promises at sensible parameters; these run them at the edge,
    /// with vacuity guards proving the coarseness actually engaged.
    /// <para>
    /// Where a bound is Markov-loose, narrowing hides inside it here exactly as it
    /// does at fine parameters, and the geometry pins own those mutations; each
    /// test's header says which layer owns what.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestCoarseCorners
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        /// <summary>
        /// Count-Min at epsilon = delta = 0.5: one row of six counters, the
        /// smallest sketch the formulas produce, where the contract "the estimate
        /// exceeds the truth by more than epsilon*N with probability at most delta"
        /// has no second row to hide behind. The stream is deliberately skewed --
        /// one key carrying five sixths of the mass -- because on a uniform stream
        /// no light key's error can reach epsilon*N and the delta side of the
        /// contract is never exercised at all.
        /// <para>
        /// Probed at an exceed fraction of 0.1755 over 2,000 light-key queries:
        /// real exceedances, comfortably inside delta. The gap from 0.5 is the
        /// looseness of Markov's inequality, which means a moderately narrowed
        /// sketch still passes this contract -- the geometry pin owns narrowing;
        /// this test owns the contract's existence at depth one.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestCountMinAtItsCoarsestHoldsTheDeltaContract()
        {
            int exceeded = 0, queries = 0;

            for (int t = 0; t < 40; t++)
            {
                var cms = new CountMinSketch(0.5, 0.5);
                long n = 0;

                for (int i = 0; i < 5000; i++)
                {
                    cms.Add(Key($"t{t}-heavy"));
                    n++;
                }
                for (int k = 0; k < 50; k++)
                {
                    for (int i = 0; i < 20; i++)
                    {
                        cms.Add(Key($"t{t}-light-{k}"));
                        n++;
                    }
                }

                var threshold = 0.5 * n;
                for (int k = 0; k < 50; k++)
                {
                    var error = (double)cms.Count(Key($"t{t}-light-{k}")) - 20;
                    if (error > threshold)
                    {
                        exceeded++;
                    }
                    queries++;
                }
            }

            var fraction = (double)exceeded / queries;
            Console.WriteLine($"exceed fraction={fraction:F4}");

            Assert.IsGreaterThanOrEqualTo(0.05, fraction,
                "almost no light key collided with the heavy one, so the delta side " +
                "of the contract was never exercised and this test proves nothing.");
            Assert.IsLessThanOrEqualTo(0.5, fraction,
                "the fraction of estimates in error by more than epsilon*N must not " +
                "exceed delta -- that pair of numbers is the entire contract, and " +
                "at depth one there is no second row to rescue it.");
        }

        /// <summary>
        /// Count Sketch at epsilon = delta = 0.5: four counters, one row. What
        /// survives at that size is the property the signs exist for -- the
        /// estimate is unbiased, because every colliding key lands with a random
        /// sign and cancels in expectation. A Count-Min sketch of the same size
        /// overestimates by the whole colliding mass; a Count Sketch that lost its
        /// signs becomes one, and at width four the difference is 125 counts
        /// against a standard error of 1.25.
        /// <para>
        /// Probed at a mean error of 0.79, which is 0.6 standard errors from zero.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestCountSketchAtItsCoarsestIsStillUnbiased()
        {
            const int Trials = 2000;
            double sum = 0;

            for (int t = 0; t < Trials; t++)
            {
                var cs = new CountSketch(0.5, 0.5);
                cs.Add(Key($"t{t}-target"), 50);
                for (int k = 0; k < 20; k++)
                {
                    cs.Add(Key($"t{t}-other-{k}"), 25);
                }
                sum += cs.Count(Key($"t{t}-target")) - 50;
            }

            var mean = sum / Trials;
            Console.WriteLine($"mean error={mean:F3}");

            // sd per trial is about 56 (twenty +/-25 collisions at rate 1/4), so
            // the standard error over 2,000 trials is 1.25 and the window is four
            // of those. The all-plus mutation sits a hundred standard errors out.
            Assert.IsLessThanOrEqualTo(5.0, Math.Abs(mean),
                $"the mean error over {Trials} keyed trials was {mean:F2}; more " +
                "than five counts of systematic offset at width four means the " +
                "signs are not cancelling, which is the one thing Count Sketch " +
                "buys over Count-Min.");
        }

        /// <summary>
        /// A Bloom filter asked for a 50% false-positive rate is one hash over a
        /// bit array it will half fill: the coarsest filter that is still a
        /// filter. Its promise is saturated by construction -- fill lands at
        /// exactly 1 - e^(-ln 2) = 1/2 -- so the measured rate must sit *at* the
        /// promise, not merely under it. Under it is a sizing defect wearing
        /// accuracy's clothes, exactly as a DDSketch far inside its bound would
        /// be; over it is the ordinary kind of broken.
        /// <para>
        /// Probed at 0.5130 over 20,000 probes of a 10,000-item filter. The window
        /// is [0.47, 0.55]: the fill itself is one random draw, worth about half a
        /// point of standard deviation on top of the probe noise, and k is an
        /// integer -- the nearest wrong geometries land at 0.38 (an unrounded m)
        /// and 0.5625 (k = 2), both outside it.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestABloomFilterAskedForHalfDeliversHalf()
        {
            var filter = new BloomFilter(10000, 0.5);
            for (int i = 0; i < 10000; i++)
            {
                filter.Add(Key($"in-{i}"));
            }

            int falsePositives = 0;
            for (int i = 0; i < 20000; i++)
            {
                if (filter.Test(Key($"out-{i}")))
                {
                    falsePositives++;
                }
            }

            var rate = (double)falsePositives / 20000;
            Console.WriteLine($"measured FP={rate:F4}");

            Assert.IsGreaterThanOrEqualTo(0.47, rate,
                $"measured {rate:P2} against an asked-for 50%: a rate this far " +
                "under the promise means the filter is bigger than the caller " +
                "asked to pay for.");
            Assert.IsLessThanOrEqualTo(0.55, rate,
                $"measured {rate:P2} against an asked-for 50%.");
        }

        /// <summary>
        /// A cuckoo filter at a 50% rate carries four-bit fingerprints -- sixteen
        /// values, so every bucket pair it probes is a coin-flip room of
        /// collisions. Two layers assert here. The contract: the measured rate
        /// stays under the asked-for 0.5. The behavior: at this deterministic
        /// stream's load of 0.61 the expected rate is 1-(15/16)^(2b*load), about
        /// 0.27, and the ceiling of 0.35 is that plus margin -- tight enough that
        /// a fingerprint one bit narrower (0.48) fails behavior while still
        /// sneaking under the contract, which is Markov looseness again and why
        /// the behavioral ceiling exists.
        /// <para>
        /// Probed at 0.2562 with all 10,000 inserts accepted.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestACuckooFilterAtFourBitFingerprintsHoldsItsRate()
        {
            var filter = new CuckooBloomFilter(10000, 0.5);

            int inserted = 0;
            for (int i = 0; i < 10000; i++)
            {
                if (filter.Add(Key($"in-{i}")))
                {
                    inserted++;
                }
            }
            Assert.IsGreaterThanOrEqualTo(9500, inserted,
                "the filter refused the stream it was sized for, so the measured " +
                "rate below would describe a half-empty table.");

            int falsePositives = 0;
            for (int i = 0; i < 20000; i++)
            {
                if (filter.Test(Key($"out-{i}")))
                {
                    falsePositives++;
                }
            }

            var rate = (double)falsePositives / 20000;
            Console.WriteLine($"inserted={inserted} measured FP={rate:F4}");

            Assert.IsGreaterThanOrEqualTo(0.15, rate,
                $"measured {rate:P2}: four-bit fingerprints under 10,000 items " +
                "cannot be this quiet; the collision pressure this corner exists " +
                "for did not engage.");
            Assert.IsLessThanOrEqualTo(0.35, rate,
                $"measured {rate:P2} against an expected 0.27 at this load; past " +
                "0.35 the fingerprints are narrower or dirtier than four clean " +
                "bits deliver.");
            Assert.IsLessThanOrEqualTo(0.5, rate,
                $"measured {rate:P2} against the asked-for 50% -- the contract " +
                "itself, kept separate so a behavioral drift and a broken promise " +
                "read as different failures.");
        }

        /// <summary>
        /// VarOpt at capacity one is the estimator stripped to its defining
        /// equation: one survivor, carrying the entire stream's weight, chosen
        /// with probability proportional to its own. Both halves are checkable --
        /// the carried weight exactly (it is an invariant, not an estimate), the
        /// selection probability distributionally: the subset estimate of the
        /// heavy item must average its true weight across seeds.
        /// <para>
        /// Probed at 2,000 of 2,000 trials carrying exactly the total, and a mean
        /// estimate of 99.84 for a weight-100 item -- 0.2 standard errors from
        /// truth. The window is four standard errors (3.0); an estimator that
        /// sampled uniformly instead of by weight would average 37.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestVarOptAtCapacityOneIsStillWeightProportional()
        {
            const int Trials = 2000;
            double sum = 0;

            for (int t = 0; t < Trials; t++)
            {
                var varOpt = new VarOpt(1, (ulong)(t + 1));
                varOpt.Add(Key("a"), 1);
                varOpt.Add(Key("b"), 10);
                varOpt.Add(Key("c"), 100);

                var samples = varOpt.Samples();
                Assert.HasCount(1, samples,
                    $"seed {t + 1}: a capacity-one sketch must hold exactly one " +
                    "sample.");
                Assert.AreEqual(111.0, samples[0].Weight, 1e-9,
                    $"seed {t + 1}: the lone survivor must carry the whole " +
                    "stream's weight -- this is the invariant the estimator is " +
                    "built on, exact per trial, not an average.");

                sum += varOpt.EstimateSubset(
                    d => Encoding.ASCII.GetString(d.Span) == "c");
            }

            var mean = sum / Trials;
            Console.WriteLine($"mean estimate={mean:F3}");

            Assert.IsLessThanOrEqualTo(3.0, Math.Abs(mean - 100.0),
                $"the weight-100 item's subset estimate averaged {mean:F2} over " +
                $"{Trials} seeds; the sampler is no longer choosing survivors in " +
                "proportion to their weight.");
        }
    }
}
