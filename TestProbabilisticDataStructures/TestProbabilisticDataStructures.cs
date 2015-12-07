using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestProbabilisticDataStructures
    {
        /// <summary>
        /// Ensures that correct math is performed for OptimalM().
        /// </summary>
        [TestMethod]
        public void TestOptimalM()
        {
            var optimalM = OptimalM(100, 0.01);
            Assert.AreEqual(959, optimalM);

            optimalM = OptimalM(100, 0.5);
            Assert.AreEqual(145, optimalM);
        }

        /// <summary>
        /// Ensures that correct math is performed for OptimalK().
        /// </summary>
        [TestMethod]
        public void TestOptimalK()
        {
            var optimalK = OptimalK(0.01);
            Assert.AreEqual(7, optimalK);

            optimalK = OptimalK(0.0001);
            Assert.AreEqual(14, optimalK);
        }

        /// <summary>
        /// Helper method to get OptimalM().
        /// </summary>
        /// <param name="n"></param>
        /// <param name="fpRate"></param>
        /// <returns></returns>
        private int OptimalM(int n, double fpRate)
        {
            return ProbabilisticDataStructures
                .ProbabilisticDataStructures.OptimalM(n, fpRate);
        }

        /// <summary>
        /// Helper method to get OptimalK().
        /// </summary>
        /// <param name="fpRate"></param>
        /// <returns></returns>
        private int OptimalK(double fpRate)
        {
            return ProbabilisticDataStructures
                .ProbabilisticDataStructures.OptimalK(fpRate);
        }
    }
}
