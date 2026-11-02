using System;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The derivations HyperLogLog depends on, and the estimator pipeline held
    /// to the paper's printed constants on crafted register states.
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

        /// <summary>
        /// Computes the estimate the paper promises for a register state: the raw
        /// estimator alpha_m * m^2 / sum(2^-M_j) with the printed constants (0.673,
        /// 0.697, 0.709, then 0.7213/(1 + 1.079/m)), and linear counting m*ln(m/V)
        /// when the raw estimate is at most 5m/2 and V registers are zero. Restated
        /// from Figure 3 of Flajolet, Fusy, Gandouet and Meunier (2007), not read back
        /// from the implementation -- that is the entire point. The constants come out
        /// of the paper's integral for the bias of the raw estimator; no behavioral
        /// test can tell 0.673 from 0.697, because both sit inside the estimator's own
        /// spread for any one stream. The alpha substitution is exactly the mutation
        /// the spread tests in TestSetHash caught only in the mean, only at one m.
        /// <para>
        /// Figure 3's third step, -2^32*ln(1 - E/2^32) past 2^32/30, is deliberately
        /// not restated. It corrects for collisions the registers already absorb, and
        /// it inflated every estimate it touched; the estimator no longer applies it,
        /// and TestHyperLogLogRange holds that band and the saturated registers that
        /// replaced it. The states here have no saturated register, where what replaced
        /// it contributes nothing.
        /// </para>
        /// </summary>
        private static double PaperEstimate(byte[] registers)
        {
            var q = 32 - System.Numerics.BitOperations.Log2((uint)registers.Length);
            foreach (var r in registers)
            {
                Assert.IsLessThanOrEqualTo(q, (int)r,
                    "PaperEstimate is the paper's pipeline for states with nothing saturated");
            }

            var m = (double)registers.Length;
            var alpha = registers.Length switch
            {
                16 => 0.673,
                32 => 0.697,
                64 => 0.709,
                _ => 0.7213 / (1.0 + (1.079 / m)),
            };

            var sum = 0.0;
            var zeros = 0;
            foreach (var r in registers)
            {
                sum += Math.Pow(2.0, -r);
                if (r == 0)
                {
                    zeros++;
                }
            }

            var estimate = alpha * m * m / sum;
            if (estimate <= 5.0 / 2.0 * m)
            {
                if (zeros > 0)
                {
                    estimate = m * Math.Log(m / zeros);
                }
            }

            return estimate;
        }

        /// <summary>
        /// The raw-estimator path, at every register count with its own printed
        /// alpha. All registers are set to the same value, which keeps the harmonic
        /// sum exact in floating point (m identical powers of two), so the expected
        /// count is the formula's value to the digit and the assertion is equality,
        /// not tolerance. Every row sits above 5m/2 with no register saturated, so no
        /// correction engages and alpha is the only thing that can move the answer:
        /// swapping 0.673 for 0.697 moves the m = 16 row by 6, and dropping the
        /// 1.079 from the general formula moves the m = 128 row.
        /// </summary>
        [TestMethod]
        [DataRow(16u, (byte)4)]
        [DataRow(32u, (byte)4)]
        [DataRow(64u, (byte)4)]
        [DataRow(128u, (byte)4)]
        public void TestRawEstimateIsThePapersFormula(uint m, byte fill)
        {
            var hll = new HyperLogLog(m);
            Array.Fill(hll.RegisterState, fill);

            var expected = PaperEstimate(hll.RegisterState);
            Assert.IsGreaterThan(5.0 / 2.0 * m, expected,
                $"m={m}: the crafted state must land on the raw path, above 5m/2, " +
                "or this row pins a correction instead of alpha.");

            Assert.AreEqual((ulong)expected, hll.Count(),
                $"m={m}: all registers at {fill} must estimate " +
                $"alpha_m * m * 2^{fill} with the paper's alpha.");
        }

        /// <summary>
        /// The two sides of the 5m/2 threshold, with zero registers present so the
        /// small-range branch has something to do. At m = 16, fifteen registers of 2
        /// put the raw estimate at 36.3, inside the threshold of 40, and linear
        /// counting answers 16*ln(16) = 44 -- the correction *raises* the estimate,
        /// so a dropped branch is a changed answer, not a near miss. Fifteen
        /// registers of 3 put the raw estimate at 59.9, outside the threshold, and
        /// the same zero register must now be ignored. Together the rows pin the
        /// threshold from both sides; a threshold moved to 5m/3 = 26.7 strands the
        /// first row on the wrong branch.
        /// </summary>
        [TestMethod]
        [DataRow((byte)2, 44UL)]
        [DataRow((byte)3, 59UL)]
        public void TestLinearCountingEngagesExactlyAtTheThreshold(byte fill, ulong expected)
        {
            var hll = new HyperLogLog(16);
            Array.Fill(hll.RegisterState, fill);
            hll.RegisterState[7] = 0;

            Assert.AreEqual((ulong)PaperEstimate(hll.RegisterState), hll.Count(),
                $"fill={fill}: the paper pipeline and the implementation must agree.");
            Assert.AreEqual(expected, hll.Count(),
                $"fill={fill}: the pipeline evaluated by hand gives {expected}.");
        }

        /// <summary>
        /// The all-zeros state is the strongest linear-counting case: the raw
        /// estimator sees a harmonic sum of m and answers alpha*m, but the paper
        /// answers m*ln(m/m) = 0, and an empty estimator that reports ten items
        /// would be reporting the estimator's own bias as data.
        /// </summary>
        [TestMethod]
        public void TestAnEmptyEstimatorCountsZero()
        {
            Assert.AreEqual(0UL, new HyperLogLog(16).Count(),
                "an estimator that has seen nothing must say zero, which only the " +
                "small-range correction can make it say.");
        }
    }
}
