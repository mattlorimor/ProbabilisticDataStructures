using System;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The property a Stable Bloom Filter exists for: under an unbounded stream its
    /// false-positive rate approaches a fixed ceiling, where a classic filter's climbs
    /// to certainty.
    /// <para>
    /// The existing coverage asserted the value of FalsePositiveRate(), which checks a
    /// formula against itself. This measures the rate the filter actually exhibits and
    /// compares it to what the filter claims.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestStableFilterProperty
    {
        private const int StreamLength = 500000;
        private const int Probes = 20000;

        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        private static double MeasureFalsePositiveRate(Func<byte[], bool> test)
        {
            var falsePositives = 0;
            for (int i = 0; i < Probes; i++)
            {
                if (test(Key($"absent-{i}")))
                {
                    falsePositives++;
                }
            }
            return (double)falsePositives / Probes;
        }

        /// <summary>
        /// Feeding far more elements than the filter has cells must not drive the
        /// false-positive rate past the bound the filter reports.
        /// </summary>
        [TestMethod]
        public void TestStableFilterHoldsItsFalsePositiveCeiling()
        {
            var f = StableBloomFilter.NewDefaultStableBloomFilter(10000, 0.01);
            var claimed = f.FalsePositiveRate();

            for (int i = 0; i < StreamLength; i++)
            {
                f.TestAndAdd(Key($"stream-{i}"));
            }

            var measured = MeasureFalsePositiveRate(f.Test);

            Assert.IsLessThanOrEqualTo(claimed * 2, measured,
                $"after {StreamLength:N0} insertions into 10,000 cells the filter measured " +
                $"{measured:P2} against the {claimed:P2} it reports as its bound. A stable " +
                "filter's rate must converge rather than climb.");
        }

        /// <summary>
        /// The contrast that motivates the structure. The unstable variant is a
        /// classic Bloom filter -- no eviction -- so the same stream saturates it.
        /// If this ever stopped saturating, the stable variant's result above would
        /// no longer be evidence of anything.
        /// </summary>
        [TestMethod]
        public void TestUnstableVariantSaturatesUnderTheSameStream()
        {
            var f = StableBloomFilter.NewUnstableBloomFilter(10000, 0.01);

            for (int i = 0; i < StreamLength; i++)
            {
                f.TestAndAdd(Key($"stream-{i}"));
            }

            var measured = MeasureFalsePositiveRate(f.Test);

            Assert.IsGreaterThan(0.9, measured,
                $"a classic filter given {StreamLength:N0} elements in 10,000 cells should be " +
                $"effectively full, but measured {measured:P2}.");
        }

        /// <summary>
        /// Eviction is biased toward recency: the tail of the stream survives at a
        /// higher rate than its head. This is a false negative by design, and the
        /// direction is what matters rather than the magnitude.
        /// </summary>
        [TestMethod]
        public void TestStableFilterFavorsRecentElements()
        {
            var f = StableBloomFilter.NewDefaultStableBloomFilter(10000, 0.01);

            for (int i = 0; i < StreamLength; i++)
            {
                f.TestAndAdd(Key($"stream-{i}"));
            }

            var recent = 0;
            var oldest = 0;
            for (int i = 0; i < 1000; i++)
            {
                if (f.Test(Key($"stream-{StreamLength - 1 - i}"))) recent++;
                if (f.Test(Key($"stream-{i}"))) oldest++;
            }

            Assert.IsGreaterThan(oldest, recent,
                $"recent elements ({recent}/1000) should outlive the oldest ({oldest}/1000).");
        }
    }
}
