using Microsoft.VisualStudio.TestTools.UnitTesting;
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
            Assert.AreEqual(959u, optimalM);

            optimalM = OptimalM(100, 0.5);
            Assert.AreEqual(145u, optimalM);
        }

        /// <summary>
        /// Ensures that correct math is performed for OptimalM64().
        /// </summary>
        [TestMethod]
        public void TestOptimalM64()
        {
            var optimalM = OptimalM64(100, 0.01);
            Assert.AreEqual(959ul, optimalM);

            optimalM = OptimalM64(100, 0.5);
            Assert.AreEqual(145ul, optimalM);

            optimalM = OptimalM64(8589934592ul, 0.0001);
            Assert.AreEqual(164670049045ul, optimalM);
        }

        /// <summary>
        /// Ensures that correct math is performed for OptimalK().
        /// </summary>
        [TestMethod]
        public void TestOptimalK()
        {
            var optimalK = OptimalK(0.01);
            Assert.AreEqual(7u, optimalK);

            optimalK = OptimalK(0.0001);
            Assert.AreEqual(14u, optimalK);
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
                .Utils.HashKernel(data, hashAlgorithm);

            Assert.AreEqual(4254774583u, hashKernel.LowerBaseHash);
            Assert.AreEqual(4179961689u, hashKernel.UpperBaseHash);
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
                .Utils.HashKernel(data, hashAlgorithm);

            Assert.AreEqual(3252571653u, hashKernel.LowerBaseHash);
            Assert.AreEqual(1646207440u, hashKernel.UpperBaseHash);
        }

        /// <summary>
        /// Ensures that HashKernel() returns the proper upper and lower base when using
        /// MD5.
        /// </summary>
        [TestMethod]
        public void TestHashKerne128lMD5()
        {
            var data = new byte[] { 0, 1, 2, 3 };
            var hashAlgorithm = HashAlgorithm.Create("MD5");
            var hashKernel = ProbabilisticDataStructures
                .Utils.HashKernel128(data, hashAlgorithm);

            Assert.AreEqual(7516929291713011248ul, hashKernel.LowerBaseHash);
            Assert.AreEqual(17952798757042697527ul, hashKernel.UpperBaseHash);
        }

        /// <summary>
        /// Ensures that HashKernel() returns the proper upper and lower base when using
        /// SHA256.
        /// </summary>
        [TestMethod]
        public void TestHashKernel128SHA256()
        {
            var data = new byte[] { 0, 1, 2, 3 };
            var hashAlgorithm = HashAlgorithm.Create("SHA256");
            var hashKernel = ProbabilisticDataStructures
                .Utils.HashKernel128(data, hashAlgorithm);

            Assert.AreEqual(4682007113097866575ul, hashKernel.LowerBaseHash);
            Assert.AreEqual(7070407120484453893ul, hashKernel.UpperBaseHash);
        }

        /// <summary>
        /// Helper method to get OptimalM().
        /// </summary>
        /// <param name="n"></param>
        /// <param name="fpRate"></param>
        /// <returns></returns>
        private uint OptimalM(uint n, double fpRate)
        {
            return ProbabilisticDataStructures
                .Utils.OptimalM(n, fpRate);
        }

        /// <summary>
        /// Helper method to get OptimalM64().
        /// </summary>
        /// <param name="n"></param>
        /// <param name="fpRate"></param>
        /// <returns></returns>
        private ulong OptimalM64(ulong n, double fpRate)
        {
            return ProbabilisticDataStructures
                .Utils.OptimalM64(n, fpRate);
        }

        /// <summary>
        /// Helper method to get OptimalK().
        /// </summary>
        /// <param name="fpRate"></param>
        /// <returns></returns>
        private uint OptimalK(double fpRate)
        {
            return ProbabilisticDataStructures
                .Utils.OptimalK(fpRate);
        }
    }
}
