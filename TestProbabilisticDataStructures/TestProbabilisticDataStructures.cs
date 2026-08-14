using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;
using System.IO.Hashing;
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
        /// Ensures that HashKernel() returns the same upper and lower base
        /// as https://github.com/tylertreat/BoomFilters does when using the
        /// FNV1 hash.
        /// </summary>
        [TestMethod]
        public void TestHashKernelFNV1()
        {
            // FNV1 hash bytes for new byte[] { 0, 1, 2, 3 }
            var hashBytes =
                new byte[]
                {
                    0x15,
                    0x54,
                    0xe0,
                    0x98,
                    0x7f,
                    0x32,
                    0x75,
                    0x44
                };
            var hashKernel = ProbabilisticDataStructures
                .Utils.HashKernelFromHashBytes(hashBytes);
            // Compare against upper and lower base values gotten by
            // calling the HashKernel function from
            // https://github.com/tylertreat/BoomFilters using that library's
            // default FNV1 hash algorithm.
            Assert.AreEqual(2564838421u, hashKernel.LowerBaseHash);
            Assert.AreEqual(1148531327u, hashKernel.UpperBaseHash);
        }

        /// <summary>
        /// Pins the 32-bit kernel for the library's default hash function.
        /// <para>
        /// These values record this library's behavior; they are not shared with any
        /// other implementation. The Go-convention check lives in
        /// TestHashKernelFromHashBytes, which feeds digest bytes directly.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestHashKernelDefault()
        {
            var data = new byte[] { 0, 1, 2, 3 };
            var hashKernel = ProbabilisticDataStructures
                .Utils.HashKernel(data, ProbabilisticDataStructures.Defaults.GetDefaultHashFunction());

            Assert.AreEqual(2776764914u, hashKernel.LowerBaseHash);
            Assert.AreEqual(1624944694u, hashKernel.UpperBaseHash);
        }

        /// <summary>
        /// The kernel is agnostic to the hash it is given: supplying a different
        /// function changes the result and nothing else.
        /// </summary>
        [TestMethod]
        public void TestHashKernelWithSuppliedHashFunction()
        {
            var data = new byte[] { 0, 1, 2, 3 };
            var hashKernel = ProbabilisticDataStructures
                .Utils.HashKernel(data, d => XxHash64.HashToUInt64(d));

            Assert.AreEqual(1146342430u, hashKernel.LowerBaseHash);
            Assert.AreEqual(4291745888u, hashKernel.UpperBaseHash);
        }

        /// <summary>
        /// Pins the 128-bit kernel for the library's default hash function.
        /// </summary>
        [TestMethod]
        public void TestHashKernel128Default()
        {
            var data = new byte[] { 0, 1, 2, 3 };
            var hashKernel = ProbabilisticDataStructures
                .Utils.HashKernel128(data, ProbabilisticDataStructures.Defaults.GetDefaultHashFunction());

            Assert.AreEqual(6979084321315492338ul, hashKernel.LowerBaseHash);
            Assert.AreEqual(11802759203604782174ul, hashKernel.UpperBaseHash);
        }

        /// <summary>
        /// The 128-bit kernel is likewise agnostic to the supplied hash function.
        /// </summary>
        [TestMethod]
        public void TestHashKernel128WithSuppliedHashFunction()
        {
            var data = new byte[] { 0, 1, 2, 3 };
            var hashKernel = ProbabilisticDataStructures
                .Utils.HashKernel128(data, d => XxHash64.HashToUInt64(d));

            Assert.AreEqual(18432908232848821278ul, hashKernel.LowerBaseHash);
            Assert.AreEqual(2251845698506980445ul, hashKernel.UpperBaseHash);
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

        [TestMethod]
        public void TestHashBytesToUInt32()
        {
            var hashBytes =
                new byte[]
                {
                    0x40,
                    0x51,
                    0x62,
                    0x73,
                    0x84,
                    0x95,
                    0xa6,
                    0xb7,
                    0xc8,
                    0xd9,
                    0xea,
                    0xfb
                };
            Assert.AreEqual(0x73625140u, Utils.HashBytesToUInt32(hashBytes, 0));
            Assert.AreEqual(0xb7a69584u, Utils.HashBytesToUInt32(hashBytes, 4));
            Assert.AreEqual(0xfbead9c8u, Utils.HashBytesToUInt32(hashBytes, 8));
        }

        [TestMethod]
        public void TestHashBytesToUInt64()
        {
            var hashBytes =
                new byte[]
                {
                    0x40,
                    0x51,
                    0x62,
                    0x73,
                    0x84,
                    0x95,
                    0xa6,
                    0xb7,
                    0xc8,
                    0xd9,
                    0xea,
                    0xfb
                };
            Assert.AreEqual(0xb7a6958473625140ul, Utils.HashBytesToUInt64(hashBytes, 0));
            Assert.AreEqual(0xfbead9c8b7a69584ul, Utils.HashBytesToUInt64(hashBytes, 4));
        }

        [TestMethod]
        public void TestComputeHashAsStringMD5()
        {
            var data = new byte[] { 0, 1, 2, 3 };
            var hashingAlgorithm = MD5.Create();
            var hashString = Utils.ComputeHashAsString(data, hashingAlgorithm);
            Assert.AreEqual("37B59AFD592725F9305E484A5D7F5168", hashString);
        }

        [TestMethod]
        public void TestComputeHashAsStringSHA256()
        {
            var data = new byte[] { 0, 1, 2, 3 };
            var hashingAlgorithm = SHA256.Create();
            var hashString = Utils.ComputeHashAsString(data, hashingAlgorithm);
            Assert.AreEqual("054EDEC1D0211F624FED0CBCA9D4F9400B0E491C43742AF2C5B0ABEBF0C990D8", hashString);
        }
    }
}
