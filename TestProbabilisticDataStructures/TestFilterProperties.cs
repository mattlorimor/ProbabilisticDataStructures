using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The two guarantees a Bloom-family filter actually makes: it never denies
    /// something it accepted, and its false positives stay near the configured rate.
    /// <para>
    /// Neither was covered. Every behavioral test in the suite used three or four
    /// elements, which is far too few for either property to be observable -- the
    /// Cuckoo filter returned false negatives for years while its tests passed,
    /// because none of them inserted enough to trigger relocation.
    /// </para>
    /// <para>
    /// Inputs are generated deterministically rather than randomly, so a failure is
    /// reproducible. Bounds are deliberately loose: these exist to catch a hash that
    /// distributes badly or sizing math that is wrong, not to police the exact rate,
    /// and a test that fails on ordinary variation is worse than no test.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestFilterProperties
    {
        private const uint N = 10000;
        private const double TargetFpRate = 0.01;

        /// <summary>How far over the target rate a measurement may drift before failing.</summary>
        private const double FpTolerance = 3.0;

        private static byte[] Member(int i) => Encoding.ASCII.GetBytes($"member-{i}");
        private static byte[] NonMember(int i) => Encoding.ASCII.GetBytes($"absent-{i}");

        /// <summary>
        /// Adds N members, then requires every one of them to be found. Any failure
        /// here is a false negative, which these filters must never produce.
        /// </summary>
        private static void AssertNoFalseNegatives(string name, Func<byte[], bool> test, Action<byte[]> add)
        {
            for (int i = 0; i < N; i++)
            {
                add(Member(i));
            }

            var missing = new List<int>();
            for (int i = 0; i < N; i++)
            {
                if (!test(Member(i)))
                {
                    missing.Add(i);
                }
            }

            Assert.IsEmpty(missing,
                $"{name} produced {missing.Count} false negatives out of {N} inserted elements. " +
                "A filter must never deny an element it accepted.");
        }

        /// <summary>
        /// Measures the observed false-positive rate against elements that were never
        /// added, and requires it to stay within a multiple of the configured target.
        /// </summary>
        private static double MeasureFalsePositiveRate(Func<byte[], bool> test)
        {
            var falsePositives = 0;
            for (int i = 0; i < N; i++)
            {
                if (test(NonMember(i)))
                {
                    falsePositives++;
                }
            }
            return (double)falsePositives / N;
        }

        private static void AssertFalsePositiveRateNearTarget(string name, double observed)
        {
            Assert.IsLessThanOrEqualTo(TargetFpRate * FpTolerance, observed,
                $"{name} observed a false-positive rate of {observed:P2} against a configured " +
                $"target of {TargetFpRate:P2}. A rate this far above target points at the hash " +
                "distributing poorly or at the filter's sizing math, not at ordinary variation.");
        }

        [TestMethod]
        public void TestBloomFilterProperties()
        {
            var f = new BloomFilter(N, TargetFpRate);
            AssertNoFalseNegatives("BloomFilter", f.Test, d => f.Add(d));
            AssertFalsePositiveRateNearTarget("BloomFilter", MeasureFalsePositiveRate(f.Test));
        }

        [TestMethod]
        public void TestBloomFilter64Properties()
        {
            var f = new BloomFilter64(N, TargetFpRate);
            AssertNoFalseNegatives("BloomFilter64", f.Test, d => f.Add(d));
            AssertFalsePositiveRateNearTarget("BloomFilter64", MeasureFalsePositiveRate(f.Test));
        }

        [TestMethod]
        public void TestCountingBloomFilterProperties()
        {
            var f = CountingBloomFilter.NewDefaultCountingBloomFilter(N, TargetFpRate);
            AssertNoFalseNegatives("CountingBloomFilter", f.Test, d => f.Add(d));
            AssertFalsePositiveRateNearTarget("CountingBloomFilter", MeasureFalsePositiveRate(f.Test));
        }

        [TestMethod]
        public void TestPartitionedBloomFilterProperties()
        {
            var f = new PartitionedBloomFilter(N, TargetFpRate);
            AssertNoFalseNegatives("PartitionedBloomFilter", f.Test, d => f.Add(d));
            AssertFalsePositiveRateNearTarget("PartitionedBloomFilter", MeasureFalsePositiveRate(f.Test));
        }

        [TestMethod]
        public void TestDeletableBloomFilterProperties()
        {
            var f = new DeletableBloomFilter(N, 100, TargetFpRate);
            AssertNoFalseNegatives("DeletableBloomFilter", f.Test, d => f.Add(d));
            AssertFalsePositiveRateNearTarget("DeletableBloomFilter", MeasureFalsePositiveRate(f.Test));
        }

        /// <summary>
        /// The scalable filter grows by appending further filters as it fills, so a
        /// lookup has to consult every one of them. Inserting well past the initial
        /// hint is what makes a missed sub-filter observable.
        /// <para>
        /// Its false-positive bound is compound rather than flat. Each added filter
        /// takes a tighter error rate than the last, and the total is the sum of that
        /// geometric series: P is bounded by P0 / (1 - r) for tightening ratio r.
        /// With r = 0.8 that is five times the configured rate, so exceeding the base
        /// rate here is correct behavior rather than a defect -- the first version of
        /// this test asserted the flat bound and failed at a legitimate 3.64%.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestScalableBloomFilterPropertiesAcrossGrowth()
        {
            const double r = 0.8;
            var f = new ScalableBloomFilter(1000, TargetFpRate, r);

            AssertNoFalseNegatives("ScalableBloomFilter", f.Test, d => f.Add(d));

            var observed = MeasureFalsePositiveRate(f.Test);
            var compoundBound = TargetFpRate / (1 - r);

            Assert.IsLessThanOrEqualTo(compoundBound * 1.5, observed,
                $"ScalableBloomFilter observed {observed:P2}. Its compound bound is " +
                $"P0/(1-r) = {compoundBound:P2}; materially exceeding that points at the " +
                "growth path rather than at ordinary variation.");
        }

        /// <summary>
        /// The Cuckoo filter can refuse an insert when it is full, so only elements it
        /// accepted are required to be present.
        /// </summary>
        [TestMethod]
        public void TestCuckooFilterProperties()
        {
            var f = new CuckooBloomFilter(N, TargetFpRate);

            var accepted = new List<byte[]>();
            for (int i = 0; i < N; i++)
            {
                var data = Member(i);
                if (f.TestAndAdd(data).Added)
                {
                    accepted.Add(data);
                }
            }

            Assert.IsGreaterThan(0, accepted.Count);

            var missing = 0;
            foreach (var data in accepted)
            {
                if (!f.Test(data))
                {
                    missing++;
                }
            }
            Assert.AreEqual(0, missing,
                $"CuckooBloomFilter produced {missing} false negatives out of {accepted.Count} accepted.");

            AssertFalsePositiveRateNearTarget("CuckooBloomFilter", MeasureFalsePositiveRate(f.Test));
        }
    }
}
