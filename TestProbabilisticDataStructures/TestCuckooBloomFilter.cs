using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;
using System.Text;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestCuckooBloomFilter
    {
        private static byte[] A_BYTES = Encoding.ASCII.GetBytes("a");
        private static byte[] B_BYTES = Encoding.ASCII.GetBytes("b");
        private static byte[] C_BYTES = Encoding.ASCII.GetBytes("c");
        private static byte[] X_BYTES = Encoding.ASCII.GetBytes("x");

        /// <summary>
        /// Ensures that Buckets returns the number of buckets, m, in the Cuckoo Filter.
        /// </summary>
        [TestMethod]
        public void TestCuckooBuckets()
        {
            var f = new CuckooBloomFilter(100, 0.1);
            var buckets = f.BucketCount();

            Assert.AreEqual(1024u, buckets);
        }

        /// <summary>
        /// Ensures that Capacity returns the expected filter capacity.
        /// </summary>
        [TestMethod]
        public void TestCuckooCapacity()
        {
            var f = new CuckooBloomFilter(100, 0.1);
            var capacity = f.Capacity();

            Assert.AreEqual(100u, capacity);
        }

        /// <summary>
        /// Ensures that Count returns the number of items added to the filter.
        /// </summary>
        [TestMethod]
        public void TestCuckooCount()
        {
            var f = new CuckooBloomFilter(100, 0.1);
            for (int i = 0; i < 10; i++)
            {
                f.Add(Encoding.ASCII.GetBytes(i.ToString()));
            }

            for (int i = 0; i < 5; i++)
            {
                f.TestAndRemove(Encoding.ASCII.GetBytes(i.ToString()));
            }

            var count = f.Count();
            Assert.AreEqual(5u, count);
        }

        /// <summary>
        /// Ensures that Test, Add, and TestAndAdd behave correctly.
        /// </summary>
        [TestMethod]
        public void TestCuckooTestAndAdd()
        {
            var f = new CuckooBloomFilter(100, 0.1);

            // 'a' is not in the filter.
            if (f.Test(A_BYTES))
            {
                Assert.Fail("'a' should not be a member");
            }

            if (!f.Add(A_BYTES))
            {
                Assert.Fail("Should return true");
            }

            // 'a' is now in the filter.
            if (!f.Test(A_BYTES))
            {
                Assert.Fail("'a' should be a member");
            }

            // 'a' is still in the filter.
            var testAndAdd = f.TestAndAdd(A_BYTES);
            if (!testAndAdd.WasAlreadyAMember)
            {
                Assert.Fail("'a' should be a member");
            }
            // Should not have added
            Assert.IsFalse(testAndAdd.Added);

            // 'b' is not in the filter.
            testAndAdd = f.TestAndAdd(B_BYTES);
            if (testAndAdd.WasAlreadyAMember)
            {
                Assert.Fail("'b' should not be a member");
            }
            // Should add
            Assert.IsTrue(testAndAdd.Added);

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
                f.Add(Encoding.ASCII.GetBytes(i.ToString()));
            }

            // Filter should be full.
            testAndAdd = f.TestAndAdd(X_BYTES);
            // Make sure not there
            Assert.IsFalse(testAndAdd.WasAlreadyAMember);
            // Make sure didn't add
            Assert.IsFalse(testAndAdd.Added);
        }

        /// <summary>
        /// Ensures that TestAndRemove behaves correctly.
        /// </summary>
        [TestMethod]
        public void TestCuckooTestAndRemove()
        {
            var f = new CuckooBloomFilter(100, 0.1);

            // 'a' is not in the filter.
            if (f.Test(A_BYTES))
            {
                Assert.Fail("'a' should not be a member");
            }

            f.Add(A_BYTES);

            // 'a' is now in the filter.
            if (!f.TestAndRemove(A_BYTES))
            {
                Assert.Fail("'a' should be a member");
            }

            // 'a' is no longer in the filter.
            if (f.Test(A_BYTES))
            {
                Assert.Fail("'a' should not be a member");
            }
        }

        /// <summary>
        /// Ensures that Reset clears all buckets and the count is zero.
        /// </summary>
        [TestMethod]
        public void TestCuckooReset()
        {
            var f = new CuckooBloomFilter(100, 0.1);
            for (int i = 0; i < 1000; i++)
            {
                f.Add(Encoding.ASCII.GetBytes(i.ToString()));
            }

            var resetFilter = f.Reset();
            Assert.AreSame(f, resetFilter);

            for (int i = 0; i < f.BucketCount(); i++)
            {
                for (uint j = 0; j < f.B; j++)
                {
                    if (f.Buckets[i][j] != null)
                    {
                        Assert.Fail("Exected all buckets to be cleared");
                    }
                }
            }

            Assert.AreEqual(0u, f.Count());
        }

        [TestMethod]
        public void BenchmarkCuckooAdd()
        {
            var n = 100000u;
            var f = new CuckooBloomFilter(n, 0.1);
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
        public void BenchmarkCuckooTest()
        {
            var n = 100000u;
            var f = new CuckooBloomFilter(n, 0.1);
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
        public void BenchmarkCuckooTestAndAdd()
        {
            var n = 100000u;
            var f = new CuckooBloomFilter(n, 0.1);
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
        public void BenchmarkCuckooTestAndRemove()
        {
            var n = 100000u;
            var f = new CuckooBloomFilter(n, 0.1);
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
