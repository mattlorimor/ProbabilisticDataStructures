using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;
using System.Text;
using System.Security.Cryptography;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestScalableBloomFilter
    {
        private static byte[] A_BYTES = Encoding.ASCII.GetBytes("a");
        private static byte[] B_BYTES = Encoding.ASCII.GetBytes("b");
        private static byte[] C_BYTES = Encoding.ASCII.GetBytes("c");
        private static byte[] X_BYTES = Encoding.ASCII.GetBytes("x");

        [TestMethod]
        public void TestNewDefaultScalableBloomFilter()
        {
            var f = ScalableBloomFilter.NewDefaultScalableBloomFilter(0.1);

            Assert.AreEqual(0.1, f.FP);
            Assert.AreEqual(10000u, f.Hint);
            Assert.AreEqual(0.8, f.R);
        }

        [TestMethod]
        public void TestScalableBloomCapacity()
        {
            var f = new ScalableBloomFilter(1, 0.1, 1);
            f.AddFilter();
            f.AddFilter();

            var capacity = f.Capacity();
            Assert.AreEqual(15u, capacity);
        }

        // Ensures that K returns the number of hash functions used in each Bloom filter.
        [TestMethod]
        public void TestScalableBloomK()
        {
            var f = new ScalableBloomFilter(10, 0.1, 0.8);

            var k = f.K();
            Assert.AreEqual(4u, k);
        }

        /// <summary>
        /// Ensures that FillRatio returns the average fill ratio of the contained
        /// filters.
        /// </summary>
        [TestMethod]
        public void TestScalableFillRatio()
        {
            var f = new ScalableBloomFilter(100, 0.1, 0.8);
            f.SetHash(HashAlgorithm.Create("MD5"));
            for (int i = 0; i < 200; i++)
            {
                f.Add(Encoding.ASCII.GetBytes(i.ToString()));
            }

            var fillRatio = f.FillRatio();
            if (fillRatio > 0.5)
            {
                Assert.Fail(string.Format("Expected less than or equal to 0.5, got {0}", fillRatio));
            }
        }

        /// <summary>
        /// Ensures that Test, Add, and TestAndAdd behave correctly.
        /// </summary>
        [TestMethod]
        public void TestScalableBloomTestAndAdd()
        {
            var f = new ScalableBloomFilter(1000, 0.01, 0.8);

            // 'a' is not in the filter.
            if (f.Test(A_BYTES))
            {
                Assert.Fail("'a' should not be a member");
            }

            var addedF = f.Add(A_BYTES);
            Assert.AreSame(f, addedF, "Returned ScalableBloomFilter should be the same instance");

            // 'a' is now in the filter.
            if (!f.Test(A_BYTES))
            {
                Assert.Fail("'a' should be a member");
            }

            // 'a' is still in the filter.
            if (!f.TestAndAdd(A_BYTES))
            {
                Assert.Fail("'a' should be a member");
            }

            // 'b' is not in the filter.
            if (f.TestAndAdd(B_BYTES))
            {
                Assert.Fail("'b' should not be a member");
            }

            // 'a' is still in the filter.
            if (!f.Test(A_BYTES))
            {
                Assert.Fail("'a' should be a member");
            }

            // 'b' is now in the filter.
            if (!f.Test(B_BYTES))
            {
                Assert.Fail("'b' should be a member");
            }

            // 'c' is not in the filter.
            if (f.Test(C_BYTES))
            {
                Assert.Fail("'c' should not be a member");
            }

            for (int i = 0; i < 10000; i++)
            {
                f.TestAndAdd(Encoding.ASCII.GetBytes(i.ToString()));
            }

            // 'x' should not be a false positive.
            if (f.Test(X_BYTES))
            {
                Assert.Fail("'x' should be a member");
            }
        }

        /// <summary>
        /// Ensures that Reset sets every bit to zero.
        /// </summary>
        [TestMethod]
        public void TestScalableBloomReset()
        {
            var f = new ScalableBloomFilter(10, 0.1, 0.8);
            for (int i = 0; i < 1000; i++)
            {
                f.Add(Encoding.ASCII.GetBytes(i.ToString()));
            }

            var count = f.Filters.Count;
            Assert.IsTrue(count > 1, string.Format("Expected more than 1 filter, got {0}", count));

            var resetF = f.Reset();
            Assert.AreSame(f, resetF, "Returned ScalableBloomFilter should be the same instance");

            count = f.Filters.Count;
            Assert.IsTrue(count == 1, string.Format("Expected 1 filter, got {0}", count));

            foreach(var partition in f.Filters[0].Partitions)
            {
                for (uint i = 0; i < partition.count; i++)
                {
                    if (partition.Get(i) != 0)
                    {
                        Assert.Fail("Expected all bits to be unset");
                    } 
                }
            }
        }

        [TestMethod]
        public void BenchmarkScalableBloomAdd()
        {
            var n = 100000;
            var f = new ScalableBloomFilter(100000, 0.1, 0.8);
            var data = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                data[i] = Encoding.ASCII.GetBytes(i.ToString());
            }

            for (int i = 0; i < n; i++)
            {
                f.Add(data[i]);
            }
        }

        [TestMethod]
        public void BenchmarkScalableBloomTest()
        {
            var n = 100000;
            var f = new ScalableBloomFilter(100000, 0.1, 0.8);
            var data = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                data[i] = Encoding.ASCII.GetBytes(i.ToString());
            }

            for (int i = 0; i < n; i++)
            {
                f.Test(data[i]);
            }
        }

        [TestMethod]
        public void BenchmarkScalableBloomTestAndAdd()
        {
            var n = 100000;
            var f = new ScalableBloomFilter(100000, 0.1, 0.8);
            var data = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                data[i] = Encoding.ASCII.GetBytes(i.ToString());
            }

            for (int i = 0; i < n; i++)
            {
                f.TestAndAdd(data[i]);
            }
        }
    }
}
