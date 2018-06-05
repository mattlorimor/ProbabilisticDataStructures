using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestBuckets64
    {
        /// <summary>
        /// Ensures that Max returns the correct maximum based on the bucket
        /// size.
        /// </summary>
        [TestMethod]
        public void TestMaxBucketValue()
        {
            var b = new Buckets64(10, 2);

            var max = b.MaxBucketValue();
            Assert.AreEqual(3, max);
        }

        /// <summary>
        /// Ensures that Count returns the number of buckets.
        /// </summary>
        [TestMethod]
        public void TestBuckets64Count()
        {
            var b = new Buckets64(10, 2);

            var count = b.count;
            Assert.AreEqual(10u, count);
        }

        /// <summary>
        /// Ensures that Increment increments the bucket value by the correct delta and
        /// clamps to zero and the maximum, Get returns the correct bucket value, and Set
        /// sets the bucket value correctly.
        /// </summary>
        [TestMethod]
        public void TestBuckets64IncrementAndGetAndSet()
        {
            var b = new Buckets64(5, 2);

            var incrementedB = b.Increment(0, 1);
            Assert.AreSame(b, incrementedB, "Returned Buckets64 should be the same instance");

            var v = b.Get(0);
            Assert.AreEqual(1u, v);

            b.Increment(1u, -1);

            v = b.Get(1);
            Assert.AreEqual(0u, v);

            var setB = b.Set(2u, 100);
            Assert.AreSame(b, setB, "Returned Buckets64 should be the same instance");

            v = b.Get(2);
            Assert.AreEqual(3u, v);

            b.Increment(3, 2);

            v = b.Get(3);
            Assert.AreEqual(2u, v);
        }

        /// <summary>
        /// Ensures that Reset restores the Buckets64 to the original state.
        /// </summary>
        [TestMethod]
        public void TestBuckets64Reset()
        {
            var b = new Buckets64(5, 2);

            for (uint i = 0; i < 5; i++)
            {
                b.Increment(i, 1);
            }

            var resetB = b.Reset();
            Assert.AreSame(b, resetB, "Returned Buckets64 should be the same instance");

            for (uint i = 0; i < 5; i++)
            {
                var c = b.Get(i);
                Assert.AreEqual(0u, c);
            }
        }

        [TestMethod]
        public void BenchmarkBuckets64Increment()
        {
            var buckets = new Buckets64(10000, 10);
            for (uint i = 0; i < buckets.count; i++)
            {
                buckets.Increment(i % 10000, 1);
            }
        }

        [TestMethod]
        public void BenchmarkBuckets64Set()
        {
            var buckets = new Buckets64(10000, 10);
            for (uint i = 0; i < buckets.count; i++)
            {
                buckets.Set(i % 10000, 1);
            }
        }

        [TestMethod]
        public void BenchmarkBuckets64Get()
        {
            var buckets = new Buckets64(10000, 10);
            for (uint i = 0; i < buckets.count; i++)
            {
                buckets.Get(i % 10000);
            }
        }
    }
}
