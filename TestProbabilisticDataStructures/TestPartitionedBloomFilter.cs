using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;
using System.Linq;
using System.Text;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestPartitionedBloomFilter
    {
        /// <summary>
        /// States whose payload <see cref="TestPayloadCount"/> holds the count to: a run per
        /// partition, so filters of different partition counts and sizes.
        /// </summary>
        internal static System.Collections.Generic.IEnumerable<PayloadState> PayloadStates()
        {
            yield return PayloadState.Of(
                "10 at 50%, empty", new PartitionedBloomFilter(10, 0.5),
                PersistenceFormat.Bloom64Version);

            var filled = new PartitionedBloomFilter(200, 0.001);
            for (var i = 0; i < 40; i++) filled.Add(Encoding.ASCII.GetBytes($"w{i}"));
            yield return PayloadState.Of(
                "200 at 0.1%, filled", filled, PersistenceFormat.Bloom64Version);
        }

        /// <summary>
        /// A partition is the bits over the hash count, rounded up, worked out in integers
        /// since #232 because a <c>double</c> cannot hold every 64-bit count: past 2^53 one
        /// more bit than a power of two rounds back to it, so the ceiling of the quotient
        /// through a double is a bit short at a hash count of one, and at the largest count it
        /// rounds to 2^64, which no <c>ulong</c> holds. Held at the corners, against the
        /// ceiling worked out in arbitrary precision; and below 2^32 bits, where every filter
        /// before #232 was built, against the ceiling through a double it replaced, on the
        /// corners and a seeded sample of sizes at every hash count a filter can have.
        /// </summary>
        [TestMethod]
        public void TestAPartitionIsTheBitsOverTheHashCountRoundedUp()
        {
            static System.Numerics.BigInteger Ceiling(ulong m, uint k) => (m + (System.Numerics.BigInteger)k - 1) / k;

            var counts = new uint[] { 1, 2, 3, 7, 10, 1023, Utils.MaxHashCount };
            var corners = new ulong[] { 1, 2, 6, 7, 8, 1023, 1024, 1025, uint.MaxValue - 1, uint.MaxValue,
                (ulong)uint.MaxValue + 1, (1UL << 53) + 1, ulong.MaxValue - 1, ulong.MaxValue };
            foreach (var k in counts)
            {
                foreach (var m in corners)
                {
                    Assert.AreEqual(Ceiling(m, k), (System.Numerics.BigInteger)PartitionedBloomFilter.PartitionSize(m, k),
                        $"{m:N0} bits over {k} hash functions");
                }
            }

            // The double it replaced is short past 2^53, which is why it was replaced.
            Assert.AreEqual(1UL << 53, (ulong)System.Math.Ceiling((double)((1UL << 53) + 1) / 1),
                "premise: a double holds 2^53 + 1, so the integers were not needed");

            var random = new System.Random(232);
            for (var k = 1u; k <= Utils.MaxHashCount; k++)
            {
                foreach (var m in corners.Where(m => m <= uint.MaxValue).Concat(Enumerable.Range(0, 64).Select(_ => (ulong)random.NextInt64(1, 1L << 32))))
                {
                    Assert.AreEqual((ulong)System.Math.Ceiling((double)m / k), PartitionedBloomFilter.PartitionSize(m, k),
                        $"{m:N0} bits over {k} hash functions is not the partition the double gave");
                }
            }
        }

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
                Assert.Fail($"Expected less than or equal to 0.5, got {ratio}");
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

            // As with the classic filter, the exact figure depends on whether the
            // hash function's probes collide, so bound it instead of pinning it.
            var upperBound = (double)(4 * f.K()) / f.Capacity();
            Assert.IsGreaterThan(0.0, ratio);
            Assert.IsLessThanOrEqualTo(upperBound, ratio);
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
                for (uint i = 0; i < partition.count; i++)
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
