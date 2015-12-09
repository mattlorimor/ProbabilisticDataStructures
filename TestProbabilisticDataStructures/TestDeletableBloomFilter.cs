using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;
using System.Text;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestDeletableBloomFilter
    {
        private static byte[] A_BYTES = Encoding.ASCII.GetBytes("a");
        private static byte[] B_BYTES = Encoding.ASCII.GetBytes("b");
        private static byte[] C_BYTES = Encoding.ASCII.GetBytes("c");
        private static byte[] X_BYTES = Encoding.ASCII.GetBytes("x");

        /// <summary>
        /// Ensures that Capacity() returns the number of bits, m, in the Bloom filter.
        /// </summary>
        [TestMethod]
        public void TestDeletableCapacity()
        {
            var d = new DeletableBloomFilter(100, 10, 0.1);
            var capacity = d.Capacity();

            Assert.AreEqual(470u, capacity);
        }

        /// <summary>
        /// Ensures that K() returns the number of hash functions in the Bloom Filter.
        /// </summary>
        [TestMethod]
        public void TestDeletableK()
        {
            var d = new DeletableBloomFilter(100, 10, 0.1);
            var k = d.K();

            Assert.AreEqual(4u, k);
        }

        /// <summary>
        /// Ensures that Count returns the number of items added to the filter.
        /// </summary>
        [TestMethod]
        public void TestDeletableCount()
        {
            var d = new DeletableBloomFilter(100, 10, 0.1);
            for (uint i = 0; i < 10; i++)
            {
                d.Add(Encoding.ASCII.GetBytes(i.ToString()));
            }

            for (int i = 0; i < 5; i++)
            {
                d.TestAndRemove(Encoding.ASCII.GetBytes(i.ToString()));
            }

            var count = d.Count();
            Assert.AreEqual(5u, count);
        }

        /// <summary>
        /// Ensures that Test, Add, and TestAndAdd behave correctly.
        /// </summary>
        [TestMethod]
        public void TestDeletableTestAndAdd()
        {
            var d = new DeletableBloomFilter(100, 10, 0.1);

            // 'a' is not in the filter.
            if (d.Test(A_BYTES))
            {
                Assert.Fail("'a' should not be a member");
            }

            var addedF = d.Add(A_BYTES);
            Assert.AreSame(d, addedF, "Returned CountingBloomFilter should be the same instance");

            // 'a' is now in the filter.
            if (!d.Test(A_BYTES))
            {
                Assert.Fail("'a' should be a member");
            }

            // 'a' is still in the filter.
            if (!d.TestAndAdd(A_BYTES))
            {
                Assert.Fail("'a' should be a member");
            }

            // 'b' is not in the filter.
            if (d.TestAndAdd(B_BYTES))
            {
                Assert.Fail("'b' should not be a member");
            }

            // 'a' is still in the filter.
            if (!d.Test(A_BYTES))
            {
                Assert.Fail("'a' should be a member");
            }

            // 'b' is now in the filter.
            if (!d.Test(B_BYTES))
            {
                Assert.Fail("'b' should be a member");
            }

            // 'c' is not in the filter.
            if (d.Test(C_BYTES))
            {
                Assert.Fail("'c' should not be a member");
            }

            for (int i = 0; i < 1000000; i++)
            {
                d.TestAndAdd(Encoding.ASCII.GetBytes(i.ToString()));
            }

            // 'x' should be a false positive.
            if (!d.Test(X_BYTES))
            {
                Assert.Fail("'x' should be a member");
            }
        }

        /// <summary>
        /// Ensures that TestAndRemove behaves correctly.
        /// </summary>
        [TestMethod]
        public void TestDeletableTestAndRemove()
        {
            var d = new DeletableBloomFilter(100, 10, 0.1);

            // 'a' is not in the filter.
            if (d.TestAndRemove(A_BYTES))
            {
                Assert.Fail("'a' should not be a member");
            }

            d.Add(A_BYTES);

            // 'a' is now in the filter.
            if (!d.TestAndRemove(A_BYTES))
            {
                Assert.Fail("'a' should be a member");
            }

            // 'a' is no longer in the filter.
            if (d.TestAndRemove(A_BYTES))
            {
                Assert.Fail("'a' should not be a member");
            }
        }

        /// <summary>
        /// Ensures that Reset sets every bit to zero.
        /// </summary>
        [TestMethod]
        public void TestDeletableReset()
        {
            var d = new DeletableBloomFilter(100, 10, 0.1);
            for (int i = 0; i < 1000; i++)
            {
                d.Add(Encoding.ASCII.GetBytes(i.ToString()));
            }

            var resetF = d.Reset();
            Assert.AreSame(d, resetF, "Returned DeletableBloomFilter should be the same instance");

            for (uint i = 0; i < d.Buckets.Count; i++)
            {
                if (d.Buckets.Get(i) != 0)
                {
                    Assert.Fail("Expected all bits to be unset");
                }
            }

            for (uint i = 0; i < d.Collisions.Count; i++)
            {
                if (d.Collisions.Get(i) != 0)
                {
                    Assert.Fail("Expected all bits to be unset");
                }
            }

            var count = d.Count();
            Assert.AreEqual(0u, count);
        }

        [TestMethod]
        public void BenchmarkDeletableAdd()
        {
            var n = 100000;
            var d = new DeletableBloomFilter(100, 10, 0.1);
            var data = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                data[i] = Encoding.ASCII.GetBytes(i.ToString());
            }

            for (int i = 0; i < n; i++)
            {
                d.Add(data[i]);
            }
        }

        [TestMethod]
        public void BenchmarkDeletableTest()
        {
            var n = 100000;
            var d = new DeletableBloomFilter(100, 10, 0.1);
            var data = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                data[i] = Encoding.ASCII.GetBytes(i.ToString());
            }

            for (int i = 0; i < n; i++)
            {
                d.Test(data[i]);
            }
        }

        [TestMethod]
        public void BenchmarkDeletableTestAndAdd()
        {
            var n = 100000;
            var d = new DeletableBloomFilter(100, 10, 0.1);
            var data = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                data[i] = Encoding.ASCII.GetBytes(i.ToString());
            }

            for (int i = 0; i < n; i++)
            {
                d.TestAndAdd(data[i]);
            }
        }

        [TestMethod]
        public void BenchmarkDeletableTestAndRemove()
        {
            var n = 100000;
            var d = new DeletableBloomFilter(100, 10, 0.1);
            var data = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                data[i] = Encoding.ASCII.GetBytes(i.ToString());
            }

            for (int i = 0; i < n; i++)
            {
                d.TestAndRemove(data[i]);
            }
        }
    }
}
