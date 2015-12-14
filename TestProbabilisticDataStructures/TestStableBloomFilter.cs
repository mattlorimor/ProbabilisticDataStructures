using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;
using System.Text;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestStableBloomFilter
    {
        private static byte[] A_BYTES = Encoding.ASCII.GetBytes("a");
        private static byte[] B_BYTES = Encoding.ASCII.GetBytes("b");
        private static byte[] C_BYTES = Encoding.ASCII.GetBytes("c");
        private static byte[] X_BYTES = Encoding.ASCII.GetBytes("x");

        /// <summary>
        /// Ensures that NewUnstableBloomFilter creates a Stable Bloom Filter with p=0,
        /// max=1 and k hash functions.
        /// </summary>
        [TestMethod]
        public void TestNewUnstableBloomFilter()
        {
            var f = StableBloomFilter.NewUnstableBloomFilter(100, 0.1);
            var k = ProbabilisticDataStructures.Utils.OptimalK(0.1);

            Assert.AreEqual(k, f.K());
            Assert.AreEqual(100u, f.M);
            Assert.AreEqual(0u, f.P());
            Assert.AreEqual(1u, f.Max);
        }

        /// <summary>
        /// Ensures that Cells returns the number of cells, m, in the Stable Bloom
        /// Filter.
        /// </summary>
        [TestMethod]
        public void TestStableCells()
        {
            var f = new StableBloomFilter(100, 1, 0.1);

            Assert.AreEqual(100u, f.Cells());
        }

        /// <summary>
        /// Ensures that K returns the number of hash functions in the Stable Bloom
        /// Filter.
        /// </summary>
        [TestMethod]
        public void TestStableK()
        {
            var f = new StableBloomFilter(100, 1, 0.01);

            Assert.AreEqual(3u, f.K());
        }

        /// <summary>
        /// Ensures that Test, Add, and TestAndAdd behave correctly.
        /// </summary>
        [TestMethod]
        public void TestStableTestAndAdd()
        {
            var f = StableBloomFilter.NewDefaultStableBloomFilter(10000, 0.01);

            // 'a' is not in the filter.
            if (f.Test(A_BYTES))
            {
                Assert.Fail("'a' should not be a member");
            }

            var addedF = f.Add(A_BYTES);
            Assert.AreSame(f, addedF, "Returned StableBloomFilter should be the same instance");

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

            // 'a' should have been evicted
            if (f.Test(A_BYTES))
            {
                Assert.Fail("'a' should not be a member");
            }
        }

        /// <summary>
        /// Ensures that StablePoint returns the expected fraction of zeros for large
        /// iterations.
        /// </summary>
        [TestMethod]
        public void TestStablePoint()
        {
            var f = StableBloomFilter.NewDefaultStableBloomFilter(10000, 0.1);
            for (int i = 0; i < 1000000; i++)
            {
                f.TestAndAdd(Encoding.ASCII.GetBytes(i.ToString()));
            }

            var zeros = 0;
            for (uint i = 0; i < f.M; i++)
            {
                if (f.cells.Get(i) == 0)
                {
                    zeros++;
                }
            }

            var actual = Math.Round((double)((double)zeros / (double)f.M), 1, MidpointRounding.AwayFromZero);
            var expected = Math.Round(f.StablePoint(), 1, MidpointRounding.AwayFromZero);

            Assert.AreEqual(expected, actual);

            // A classic Bloom filter is a special case of SBF where P is 0 and max is 1.
            // It doesn't have a stable point.
            var bf = StableBloomFilter.NewUnstableBloomFilter(1000, 0.1);
            var stablePoint = bf.StablePoint();
            if (stablePoint != 0)
            {
                Assert.Fail(string.Format("Expected stable point 0, got {0}", stablePoint));
            }
        }

        // Ensures that FalsePositiveRate returns the upper bound on false positives
        // for stable filters.
        [TestMethod]
        public void TestStableFalsePositiveRate()
        {
            var f = StableBloomFilter.NewDefaultStableBloomFilter(1000, 0.01);
            var fps = Math.Round(f.FalsePositiveRate(), 2, MidpointRounding.AwayFromZero);
            Assert.IsFalse(fps > 0.01);

            // Classic Bloom filters have an unbound rate of false positives. Once they
            // become full, every query returns a false positive.
            var bf = StableBloomFilter.NewUnstableBloomFilter(1000, 0.01);
            fps = bf.FalsePositiveRate();
            Assert.AreEqual(1.0, fps);
        }

        /// <summary>
        /// Ensures that Reset sets every cell to zero
        /// </summary>
        [TestMethod]
        public void TestStableReset()
        {
            var f = StableBloomFilter.NewDefaultStableBloomFilter(1000, 0.01);
            for (int i = 0; i < 1000; i++)
            {
                f.TestAndAdd(Encoding.ASCII.GetBytes(i.ToString()));
            }

            var resetF = f.Reset();
            Assert.AreSame(f, resetF, "Returned StableBloomFilter should be the same instance");

            for (uint i = 0; i < f.M; i++)
            {
                var cell = f.cells.Get(i);
                if (cell != 0)
                {
                    Assert.Fail(string.Format("Expected zero cell, got {0}", cell));
                }
            }
        }
    }

    [TestClass]
    public class BenchmarkStableBloomFilter
    {
        private StableBloomFilter f;
        private uint n;
        private byte[][] data;

        [TestInitialize()]
        public void Testinitialize()
        {
            n = 100000;
            f = StableBloomFilter.NewDefaultStableBloomFilter(n, 0.01);
            data = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                data[i] = Encoding.ASCII.GetBytes(i.ToString());
            }
        }

        [TestCleanup()]
        public void TestCleanup()
        {
            f = null;
            n = 0;
            data = null;
        }

        [TestMethod]
        public void BenchmarkStableAdd()
        {
            for (int i = 0; i < n; i++)
            {
                f.Add(data[i]);
            }
        }

        [TestMethod]
        public void BenchmarkStableTest()
        {
            for (int i = 0; i < n; i++)
            {
                f.Test(data[i]);
            }
        }

        [TestMethod]
        public void BenchmarkStableTestAndAdd()
        {
            for (int i = 0; i < n; i++)
            {
                f.TestAndAdd(data[i]);
            }
        }
    }

    [TestClass]
    public class BenchmarkUnstableBloomFilter
    {
        private StableBloomFilter f;
        private uint n;
        private byte[][] data;

        [TestInitialize()]
        public void Testinitialize()
        {
            n = 100000;
            f = StableBloomFilter.NewUnstableBloomFilter(n, 0.1);
            data = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                data[i] = Encoding.ASCII.GetBytes(i.ToString());
            }
        }

        [TestCleanup()]
        public void TestCleanup()
        {
            f = null;
            n = 0;
            data = null;
        }

        [TestMethod]
        public void BenchmarkUnstableAdd()
        {
            for (int i = 0; i < n; i++)
            {
                f.Add(data[i]);
            }
        }

        [TestMethod]
        public void BenchmarkUnstableTest()
        {
            for (int i = 0; i < n; i++)
            {
                f.Test(data[i]);
            }
        }

        [TestMethod]
        public void BenchmarkUnstableTestAndAdd()
        {
            for (int i = 0; i < n; i++)
            {
                f.TestAndAdd(data[i]);
            }
        }
    }
}
