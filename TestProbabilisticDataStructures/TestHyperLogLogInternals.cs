using System;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The derivations HyperLogLog depends on, rather than its estimate.
    /// </summary>
    [TestClass]
    public class TestHyperLogLogInternals
    {
        /// <summary>
        /// b splits a 32-bit hash into a register index and the bits rho scans, so it
        /// has to be exactly log2(m). Deriving it as Ceiling(Log(m, 2)) is not exact:
        /// at m = 2^29 the floating-point logarithm lands just above 29 and the
        /// ceiling returns 30, leaving k = 32 - b too small and letting the register
        /// index run past the end of the register array.
        /// <para>
        /// Every power of two a caller can pass is checked, since the failure affects
        /// exactly one of them and would be invisible in a spot check.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestRegisterIndexAlwaysFitsTheRegisterArray()
        {
            // 2^30 registers is already a gigabyte, so the loop verifies the
            // arithmetic rather than allocating each size.
            for (int exponent = 1; exponent <= 30; exponent++)
            {
                var m = (uint)Math.Pow(2, exponent);
                var b = (uint)System.Numerics.BitOperations.Log2(m);

                Assert.AreEqual((uint)exponent, b,
                    $"b for m = 2^{exponent} must be exactly {exponent}");

                // The index is the top b bits of a 32-bit hash, so its largest value
                // must still be inside the register array.
                var k = 32 - b;
                var largestIndex = (ulong)uint.MaxValue >> (int)k;
                Assert.IsLessThanOrEqualTo((ulong)m - 1, largestIndex,
                    $"a register index derived with b = {b} would run past {m} registers");
            }
        }

        /// <summary>
        /// Zero registers passes a naive power-of-two test, because 0 - 1 underflows
        /// to all ones. It is rejected before that check can admit it.
        /// </summary>
        [TestMethod]
        public void TestZeroRegisterCountIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new HyperLogLog(0));

            // Still rejected for the original reason when it is not a power of two.
            Assert.Throws<ArgumentException>(() => new HyperLogLog(3));

            // And a valid size still constructs.
            _ = new HyperLogLog(16);
        }

        /// <summary>
        /// The estimator counts distinct elements, so repeating one must not move the
        /// estimate.
        /// </summary>
        [TestMethod]
        public void TestRepeatedElementsDoNotInflateTheEstimate()
        {
            var hll = HyperLogLog.NewDefaultHyperLogLog(0.01);
            const int distinct = 1000;

            for (int i = 0; i < distinct; i++)
            {
                for (int repeat = 0; repeat < 50; repeat++)
                {
                    hll.Add(Encoding.ASCII.GetBytes($"dup-{i}"));
                }
            }

            var estimate = (double)hll.Count();
            var error = Math.Abs(estimate - distinct) / distinct;

            Assert.IsLessThanOrEqualTo(0.10, error,
                $"estimate {estimate} for {distinct} distinct elements added 50 times each; " +
                "repeats must not be counted.");
        }

        /// <summary>
        /// The estimate should track the true cardinality within the error the chosen
        /// accuracy implies. The bound is the standard error, not a hard ceiling, so
        /// this allows generous headroom and exists to catch a broken estimator rather
        /// than to police the constant.
        /// </summary>
        [TestMethod]
        public void TestEstimateStaysWithinItsErrorBound()
        {
            const int distinct = 100000;

            foreach (var target in new[] { 0.05, 0.01 })
            {
                var hll = HyperLogLog.NewDefaultHyperLogLog(target);
                for (int i = 0; i < distinct; i++)
                {
                    hll.Add(Encoding.ASCII.GetBytes($"item-{i}"));
                }

                var estimate = (double)hll.Count();
                var error = Math.Abs(estimate - distinct) / distinct;

                Assert.IsLessThanOrEqualTo(target * 3, error,
                    $"target {target:P0}: estimated {estimate:N0} against {distinct:N0}, " +
                    $"error {error:P2}");
            }
        }
    }
}
