using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;
using System.Security.Cryptography;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestHash128
    {
        [TestMethod]
        public void TestConstructor()
        {
            var hashingAlgorithm = ProbabilisticDataStructures.Defaults.GetDefaultHashAlgorithm();
            var hash = new Hash128(hashingAlgorithm);

            Assert.AreEqual(HashAlgorithm.Create("MD5").GetType(), hash.HashAlgorithm.GetType());
        }

        [TestMethod]
        public void TestComputeHashMD5()
        {
            var data = new byte[] { 0, 1, 2, 3 };
            var hashingAlgorithm = HashAlgorithm.Create("MD5");
            var hash = new Hash128(hashingAlgorithm);

            var hashString = hash.ComputeHash(data);
            Assert.AreEqual("37B59AFD592725F9305E484A5D7F5168", hashString);
        }

        [TestMethod]
        public void TestComputeHashSHA256()
        {
            var data = new byte[] { 0, 1, 2, 3 };
            var hashingAlgorithm = HashAlgorithm.Create("SHA256");
            var hash = new Hash128(hashingAlgorithm);

            var hashString = hash.ComputeHash(data);
            Assert.AreEqual("054EDEC1D0211F624FED0CBCA9D4F9400B0E491C43742AF2C5B0ABEBF0C990D8", hashString);
        }

        [TestMethod]
        public void TestSumMD5()
        {
            var data = new byte[] { 0, 1, 2, 3 };
            var expectedSum = new byte[] {
                249,
                37,
                39,
                89,
                253,
                154,
                181,
                55,
                104,
                81,
                127,
                93,
                74,
                72,
                94,
                48
            };
            var hashingAlgorithm = HashAlgorithm.Create("MD5");
            var hash = new Hash128(hashingAlgorithm);
            var sum = hash.Sum(hash.ComputeHash(data));
            CollectionAssert.AreEqual(expectedSum, sum);
        }

        [TestMethod]
        public void TestSHA256()
        {
            var data = new byte[] { 0, 1, 2, 3 };
            var expectedSum = new byte[] {
                98,
                31,
                33,
                208,
                193,
                222,
                78,
                5,
                64,
                249,
                212,
                169,
                188,
                12,
                237,
                79
            };
            var hashingAlgorithm = HashAlgorithm.Create("SHA256");
            var hash = new Hash128(hashingAlgorithm);
            var sum = hash.Sum(hash.ComputeHash(data));
            CollectionAssert.AreEqual(expectedSum, sum);
        }
    }
}
