using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;
using System.Text;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestCountingBloomFilter
    {
        private static byte[] A_BYTES = Encoding.ASCII.GetBytes("a");
        private static byte[] B_BYTES = Encoding.ASCII.GetBytes("b");
        private static byte[] C_BYTES = Encoding.ASCII.GetBytes("c");
        private static byte[] X_BYTES = Encoding.ASCII.GetBytes("x");

        /// <summary>
        /// Ensures that Capacity() returns the number of bits, m, in the Bloom filter.
        /// </summary>
        [TestMethod]
        public void TestCountingCapacity()
        {
            var f = new CountingBloomFilter(100, 0.1);
            var capacity = f.Capacity();

            Assert.AreEqual(480u, capacity);
        }

        /// <summary>
        /// Ensures that K() returns the number of hash functions in the Bloom Filter.
        /// </summary>
        [TestMethod]
        public void TestCountingK()
        {
            var f = new CountingBloomFilter(100, 0.1);
            var k = f.K();

            Assert.AreEqual(4u, k);
        }

        /// <summary>
        /// Ensures that Count returns the number of items added to the filter.
        /// </summary>
        [TestMethod]
        public void TestCountingCount()
        {
            var f = new CountingBloomFilter(100, 0.1);
            for (uint i = 0; i < 10; i++)
            {
                f.Add(Encoding.ASCII.GetBytes(i.ToString()));
            }

            for (int i = 0; i < 5; i++)
            {
                f.TestAndRemove(Encoding.ASCII.GetBytes(i.ToString()));
            }

            var count = f.Count;
            Assert.AreEqual(5u, count);
        }

        /// <summary>
        /// Ensures that Test, Add, and TestAndAdd behave correctly.
        /// </summary>
        [TestMethod]
        public void TestCountingTestAndAdd()
        {
            var f = new CountingBloomFilter(100, 0.01);

            // 'a' is not in the filter.
            if (f.Test(A_BYTES))
            {
                Assert.Fail("'a' should not be a member");
            }

            var addedF = f.Add(A_BYTES);
            Assert.AreSame(f, addedF, "Returned BloomFilter should be the same instance");

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
        /// Ensures that TestAndRemove behaves correctly.
        /// </summary>
        [TestMethod]
        public void TestCountingTestAndRemove()
        {
            var f = new CountingBloomFilter(100, 0.01);

            // 'a' is not in the filter.
            if (f.TestAndRemove(A_BYTES))
            {
                Assert.Fail("'a' should not be a member");
            }

            f.Add(Encoding.ASCII.GetBytes("a"));

            // 'a' is now in the filter.
            if (!f.TestAndRemove(A_BYTES))
            {
                Assert.Fail("'a' should be a member");
            }

            // 'a' is no longer in the filter.
            if (f.TestAndRemove(A_BYTES))
            {
                Assert.Fail("'a' should not be a member");
            }
        }

        /// <summary>
        /// Ensures that Reset sets every bit to zero and the count is zero.
        /// </summary>
        [TestMethod]
        public void TestCountingReset()
        {
            var f = new CountingBloomFilter(100, 0.1);
            for (int i = 0; i < 1000; i++)
            {
                f.Add(Encoding.ASCII.GetBytes(i.ToString()));
            }

            var resetF = f.Reset();
            Assert.AreSame(f, resetF, "Returned CountingBloomFilter should be the same instance");

            for (uint i = 0; i < f.Buckets.Count; i++)
            {
                if (f.Buckets.Get(i) != 0)
                {
                    Assert.Fail("Expected all bits to be unset");
                }
            }

            Assert.AreEqual(0u, f.Count);
        }

        [TestMethod]
        public void BenchmarkCountingAdd()
        {
            var n = 100000;
            var f = new CountingBloomFilter(100000, 0.1);
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
        public void BenchmarkCountingTest()
        {
            var n = 100000;
            var f = new CountingBloomFilter(100000, 0.1);
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
        public void BenchmarkCountingTestAndAdd()
        {
            var n = 100000;
            var f = new CountingBloomFilter(100000, 0.1);
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

        [TestMethod]
        public void BenchmarkCountingTestAndRemove()
        {
            var n = 100000;
            var f = new CountingBloomFilter(100000, 0.1);
            var data = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                data[i] = Encoding.ASCII.GetBytes(i.ToString());
            }

            for (int i = 0; i < n; i++)
            {
                f.TestAndRemove(data[i]);
            }
        }
    }
}
