using System;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestCountMinSketch
    {
        private static byte[] A_BYTES = Encoding.ASCII.GetBytes("a");
        private static byte[] B_BYTES = Encoding.ASCII.GetBytes("b");
        private static byte[] C_BYTES = Encoding.ASCII.GetBytes("c");
        private static byte[] D_BYTES = Encoding.ASCII.GetBytes("d");
        private static byte[] X_BYTES = Encoding.ASCII.GetBytes("x");

        /// <summary>
        /// Ensures that TotalCount returns the number of items added to the sketch.
        /// </summary>
        [TestMethod]
        public void TestCMSTotalCount()
        {
            var cms = new CountMinSketch(0.001, 0.99);

            for (int i = 0; i < 100; i++)
            {
                cms.Add(Encoding.ASCII.GetBytes(i.ToString()));
            }

            var count = cms.TotalCount();
            Assert.AreEqual(100u, count);
        }

        /// <summary>
        /// Ensures that Add adds to the set and Count returns the correct approximation.
        /// </summary>
        [TestMethod]
        public void TestCMSAddAndCount()
        {
            var cms = new CountMinSketch(0.001, 0.99);

            var addedCms = cms.Add(A_BYTES);
            Assert.AreSame(cms, addedCms);

            cms.Add(B_BYTES);
            cms.Add(C_BYTES);
            cms.Add(B_BYTES);
            cms.Add(D_BYTES);
            cms.Add(A_BYTES).Add(A_BYTES);

            var count = cms.Count(A_BYTES);
            Assert.AreEqual(3u, count);

            count = cms.Count(B_BYTES);
            Assert.AreEqual(2u, count);

            count = cms.Count(C_BYTES);
            Assert.AreEqual(1u, count);

            count = cms.Count(D_BYTES);
            Assert.AreEqual(1u, count);

            count = cms.Count(X_BYTES);
            Assert.AreEqual(0u, count);
        }

        /// <summary>
        /// Ensures that Merge combines the two sketches.
        /// </summary>
        [TestMethod]
        public void TestCMSMerge()
        {
            var cms = new CountMinSketch(0.001, 0.99);
            cms.Add(B_BYTES);
            cms.Add(C_BYTES);
            cms.Add(B_BYTES);
            cms.Add(D_BYTES);
            cms.Add(A_BYTES).Add(A_BYTES);

            var other = new CountMinSketch(0.001, 0.99);
            other.Add(B_BYTES);
            other.Add(C_BYTES);
            other.Add(B_BYTES);

            var wasMerged = cms.Merge(other);
            Assert.IsTrue(wasMerged);

            var count = cms.Count(A_BYTES);
            Assert.AreEqual(2u, count);

            count = cms.Count(B_BYTES);
            Assert.AreEqual(4u, count);

            count = cms.Count(C_BYTES);
            Assert.AreEqual(2u, count);

            count = cms.Count(D_BYTES);
            Assert.AreEqual(1u, count);

            count = cms.Count(X_BYTES);
            Assert.AreEqual(0u, count);
        }

        /// <summary>
        /// Ensures that Reset restores the sketch to its original state.
        /// </summary>
        [TestMethod]
        public void TestCMSReset()
        {
            var cms = new CountMinSketch(0.001, 0.99);
            cms.Add(B_BYTES);
            cms.Add(C_BYTES);
            cms.Add(B_BYTES);
            cms.Add(D_BYTES);
            cms.Add(A_BYTES).Add(A_BYTES);

            var resetCms = cms.Reset();
            Assert.AreSame(cms, resetCms);

            for (uint i = 0; i < cms.Depth; i++)
            {
                for (int j = 0; j < cms.Width; j++)
                {
                    if (cms.Matrix[i][j] != 0)
                    {
                        Assert.Fail("Expected matrix to be completely empty.");
                    }
                }
            }
        }

        [TestMethod]
        public void BenchmarkCMSAdd()
        {
            var n = 100000;
            var cms = new CountMinSketch(0.001, 0.99);
            var data = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                data[i] = Encoding.ASCII.GetBytes(i.ToString());
            }

            for (int i = 0; i < n; i++)
            {
                cms.Add(data[i]);
            }
        }

        [TestMethod]
        public void BenchmarkCMSCount()
        {
            var n = 100000;
            var cms = new CountMinSketch(0.001, 0.99);
            var data = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                var byteArray = Encoding.ASCII.GetBytes(i.ToString());
                data[i] = byteArray;
                cms.Add(byteArray);
            }

            for (int i = 0; i < n; i++)
            {
                cms.Add(data[i]);
            }
        }

        // TODO: Implement these later.
        // TestCMSSerialization
        // BenchmarkCMSWriteDataTo
        // BenchmarkCMSReadDataFrom
    }
}
