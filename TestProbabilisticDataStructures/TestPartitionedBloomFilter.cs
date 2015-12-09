using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;
using System.Text;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestPartitionedBloomFilter
    {
        private static byte[] A_BYTES = Encoding.ASCII.GetBytes("a");
        private static byte[] B_BYTES = Encoding.ASCII.GetBytes("b");
        private static byte[] C_BYTES = Encoding.ASCII.GetBytes("c");
        private static byte[] X_BYTES = Encoding.ASCII.GetBytes("x");

        /// <summary>
        /// Ensures that Capacity() returns the number of bits, m, in the Bloom filter.
        /// </summary>
        [TestMethod]
        public void TestPartitionedCapacity()
        {
            var f = new PartitionedBloomFilter(100, 0.1);
            var capacity = f.Capacity();

            Assert.AreEqual(480u, capacity);
        }

        /// <summary>
        /// Ensures that K() returns the number of hash functions in the Bloom Filter.
        /// </summary>
        [TestMethod]
        public void TestPartitionedK()
        {
            var f = new PartitionedBloomFilter(100, 0.1);
            var k = f.K();

            Assert.AreEqual(4u, k);
        }

        /// <summary>
        /// Ensures that Count returns the number of items added to the filter.
        /// </summary>
        [TestMethod]
        public void TestPartitionedCount()
        {
            var f = new PartitionedBloomFilter(100, 0.1);
            for (uint i = 0; i < 10; i++)
            {
                f.Add(Encoding.ASCII.GetBytes(i.ToString()));
            }

            var count = f.Count();
            Assert.AreEqual(10u, count);
        }

        /// <summary>
        /// Ensures that EstimatedFillRatio returns the correct approximation.
        /// </summary>
        [TestMethod]
        public void TestPartitionedEstimatedFillRatio()
        {
            var f = new PartitionedBloomFilter(100, 0.5);
            for (uint i = 0; i < 100; i++)
            {
                f.Add(Encoding.ASCII.GetBytes(i.ToString()));
            }

            var ratio = f.EstimatedFillRatio();
            if (ratio > 0.5)
            {
                Assert.Fail("Expected less than or equal to 0.5, got {0}", ratio);
            }
        }

        /// <summary>
        /// Ensures that FillRatio returns the ratio of set bits.
        /// </summary>
        [TestMethod]
        public void TestPartitionedFillRatio()
        {
            var f = new PartitionedBloomFilter(100, 0.1);
            f.Add(A_BYTES);
            f.Add(B_BYTES);
            f.Add(C_BYTES);
            f.Add(X_BYTES);

            var ratio = f.FillRatio();
            Assert.AreEqual(0.03125, ratio);
        }

        /// <summary>
        /// Ensures that Test, Add, and TestAndAdd behave correctly.
        /// </summary>
        [TestMethod]
        public void TestPartitionedBloomTestAndAdd()
        {
            var f = new PartitionedBloomFilter(100, 0.01);

            // 'a' is not in the filter.
            if (f.Test(A_BYTES))
            {
                Assert.Fail("'a' should not be a member");
            }

            var addedF = f.Add(A_BYTES);
            Assert.AreSame(f, addedF, "Returned PartitionedBloomFilter should be the same instance");

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

            for (int i = 0; i < 1000000; i++)
            {
                f.TestAndAdd(Encoding.ASCII.GetBytes(i.ToString()));
            }

            // 'x' should be a false positive.
            if (!f.Test(X_BYTES))
            {
                Assert.Fail("'x' should be a member");
            }
        }

        /// <summary>
        /// Ensures that Reset sets every bit to zero.
        /// </summary>
        [TestMethod]
        public void TestPartitionedBloomReset()
        {
            var f = new PartitionedBloomFilter(100, 0.1);
            for (int i = 0; i < 1000; i++)
            {
                f.Add(Encoding.ASCII.GetBytes(i.ToString()));
            }

            var resetF = f.Reset();
            Assert.AreSame(f, resetF, "Returned PartitionedBloomFilter should be the same instance");

            foreach (var partition in f.Partitions)
            {
                for (uint i = 0; i < partition.Count; i++)
                {
                    if (partition.Get(0) != 0)
                    {
                        Assert.Fail("Expected all bits to be unset");
                    }
                }
            }
        }

        [TestMethod]
        public void BenchmarkPartitionedBloomAdd()
        {
            var n = 100000;
            var f = new PartitionedBloomFilter(100000, 0.1);
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
        public void BenchmarkPartitionedBloomTest()
        {
            var n = 100000;
            var f = new PartitionedBloomFilter(100000, 0.1);
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
        public void BenchmarkPartitionedBloomTestAndAdd()
        {
            var n = 100000;
            var f = new PartitionedBloomFilter(100000, 0.1);
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
