using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The derivations behind what CAPACITY.md says about HyperLogLog's bias are
    /// themselves held to something outside the repository, so that the claims built on
    /// them are anchored and not circular.
    /// </summary>
    [TestClass]
    public class TestHyperLogLogBias
    {
        /// <summary>
        /// The integral that defines alpha_m reproduces the three constants the paper
        /// prints for small m, to the digits it prints them to, and its limit is
        /// 1 / (2 ln 2). The paper's printed values were not read from this repository
        /// and the limit was not derived from it, so an integral that agrees with both is
        /// the integral of the paper's definition, and then its values at larger m are
        /// what the claims compare the library's constant to.
        /// </summary>
        [TestMethod]
        [DataRow(16u, 0.673)]
        [DataRow(32u, 0.697)]
        [DataRow(64u, 0.709)]
        public void TestTheIntegralReproducesThePapersPrintedConstants(uint m, double printed)
        {
            Assert.AreEqual(printed, Math.Round(HyperLogLogBias.ExactAlpha(m), 3),
                $"alpha_{m} from the paper's integral is not the {printed} the paper prints");
        }

        /// <summary>
        /// At a billion registers the integral is the limit to better than a part in 10^8,
        /// which also shows the quadrature is fine enough to resolve the 6.6 x 10^-5 that
        /// separates the library's constant from the exact one.
        /// </summary>
        [TestMethod]
        public void TestTheIntegralTendsToTheLimit()
        {
            var exact = HyperLogLogBias.ExactAlpha(1u << 30);
            Assert.IsLessThan(1e-8, Math.Abs((exact / HyperLogLogBias.LimitAlpha) - 1),
                $"alpha at 2^30 registers is {exact}, and the limit 1 / (2 ln 2) is {HyperLogLogBias.LimitAlpha}");
            Assert.IsGreaterThan(6.5e-5, 1 - (0.7213 / HyperLogLogBias.LimitAlpha),
                "the rounding the claims are about must be larger than the quadrature can hide");
        }

        /// <summary>
        /// The closed form for the expected raw estimate tends to the truth: for a
        /// register that has seen many items the expected 2^-M is 1 / (2 ln 2 λ) (alpha
        /// is defined to make it so), and for none it is 1. A closed form that did not
        /// tend to both would give a band that never closes.
        /// </summary>
        [TestMethod]
        public void TestTheClosedFormTendsToTheTruthAtBothEnds()
        {
            Assert.AreEqual(1.0, HyperLogLogBias.ExpectedInverseValue(0), 1e-15,
                "a register that has seen nothing is at zero, and 2^0 is 1");
            Assert.IsLessThan(1e-3, Math.Abs(HyperLogLogBias.RawRatio(1000, HyperLogLogBias.LimitAlpha) - 1),
                "at a thousand items to a register the raw estimate is the truth, to the small oscillation it has");
        }
    }
}
