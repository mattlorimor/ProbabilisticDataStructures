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
        /// A counting filter's payload is its twenty-six bytes, then its bucket table whole in
        /// a run. The table is as long as one array can be, and at eight bits a bucket, or
        /// four, a table that long takes the payload past the most one can be:
        /// 2,147,483,548 buckets of eight bits is the first past it. The buckets are claimed
        /// by reflection on a small filter, since the run is counted from the table's count
        /// and width, as its reader checks it.
        /// </summary>
        [TestMethod]
        [DataRow((byte)8, 2_147_483_548u, 2_147_483_574L)]
        [DataRow((byte)8, 2_147_483_591u, 2_147_483_617L)]
        [DataRow((byte)4, 4_294_967_182u, 2_147_483_617L)]
        public void TestACountingFilterTooLargeToWriteIsRefusedNamingItsWholeSize(
            byte b, uint m, long payload)
        {
            Assert.IsLessThanOrEqualTo(Buckets.MaxCount(b), m,
                "no filter holds this many buckets of this width");
            Assert.AreEqual(26 + Buckets.DataLength(m, b), payload, "the row's payload is miscomputed");
            Assert.AreEqual(PersistenceFormat.MaxPayloadLength, 26 + Buckets.DataLength(2_147_483_547u, 8),
                "one bucket fewer than the first row does not fill a payload exactly");

            var f = new CountingBloomFilter(10, b, 0.5);
            f.Add(A_BYTES);

            TestPayloadCount.AssertRefusedWhileClaiming(f, payload, (f, "m", m), (f.Buckets, "count", m));
        }

        /// <summary>
        /// Ensures that Capacity() returns the number of bits, m, in the Bloom filter.
        /// </summary>
        [TestMethod]
        public void TestCountingCapacity()
        {
            var f = CountingBloomFilter.NewDefaultCountingBloomFilter(100, 0.1);
            var capacity = f.Capacity();

            Assert.AreEqual(480u, capacity);
        }

        /// <summary>
        /// Ensures that K() returns the number of hash functions in the Bloom Filter.
        /// </summary>
        [TestMethod]
        public void TestCountingK()
        {
            var f = CountingBloomFilter.NewDefaultCountingBloomFilter(100, 0.1);
            var k = f.K();

            Assert.AreEqual(4u, k);
        }

        /// <summary>
        /// Ensures that Count returns the number of items added to the filter.
        /// </summary>
        [TestMethod]
        public void TestCountingCount()
        {
            var f = CountingBloomFilter.NewDefaultCountingBloomFilter(100, 0.1);
            for (uint i = 0; i < 10; i++)
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
        public void TestCountingTestAndAdd()
        {
            var f = CountingBloomFilter.NewDefaultCountingBloomFilter(100, 0.01);

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
            var f = CountingBloomFilter.NewDefaultCountingBloomFilter(100, 0.01);

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
            var f = CountingBloomFilter.NewDefaultCountingBloomFilter(100, 0.1);
            for (int i = 0; i < 1000; i++)
            {
                f.Add(Encoding.ASCII.GetBytes(i.ToString()));
            }

            var resetF = f.Reset();
            Assert.AreSame(f, resetF, "Returned CountingBloomFilter should be the same instance");

            for (uint i = 0; i < f.Buckets.count; i++)
            {
                if (f.Buckets.Get(i) != 0)
                {
                    Assert.Fail("Expected all bits to be unset");
                }
            }

            Assert.AreEqual(0u, f.Count());
        }

        [TestMethod]
        public void BenchmarkCountingAdd()
        {
            var n = 100000;
            var f = CountingBloomFilter.NewDefaultCountingBloomFilter(100000, 0.1);
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
            var f = CountingBloomFilter.NewDefaultCountingBloomFilter(100000, 0.1);
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
            var f = CountingBloomFilter.NewDefaultCountingBloomFilter(100000, 0.1);
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
            var f = CountingBloomFilter.NewDefaultCountingBloomFilter(100000, 0.1);
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
