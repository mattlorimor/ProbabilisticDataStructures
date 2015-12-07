using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;
using System.Security.Cryptography;

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
        /// Ensures that HashKernel() returns the proper upper and lower base when using
        /// MD5.
        /// </summary>
        [TestMethod]
        public void TestHashKernelMD5()
        {
            var data = new byte[] { 0, 1, 2, 3 };
            var hashAlgorithm = HashAlgorithm.Create("MD5");
            var hashKernel = ProbabilisticDataStructures
                .ProbabilisticDataStructures.HashKernel(data, hashAlgorithm);

            Assert.AreEqual(4254774583u, hashKernel.Item1);
            Assert.AreEqual(4179961689u, hashKernel.Item2);
        }

        /// <summary>
        /// Ensures that HashKernel() returns the proper upper and lower base when using
        /// SHA256.
        /// </summary>
        [TestMethod]
        public void TestHashKernelSHA256()
        {
            var data = new byte[] { 0, 1, 2, 3 };
            var hashAlgorithm = HashAlgorithm.Create("SHA256");
            var hashKernel = ProbabilisticDataStructures
                .ProbabilisticDataStructures.HashKernel(data, hashAlgorithm);

            Assert.AreEqual(3252571653u, hashKernel.Item1);
            Assert.AreEqual(1646207440u, hashKernel.Item2);
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
