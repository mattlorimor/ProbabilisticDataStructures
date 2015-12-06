using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestBuckets
    {
        /// <summary>
        /// Ensures that Max returns the correct maximum based on the bucket
        /// size.
        /// </summary>
        [TestMethod]
        public void TestMaxBucketValue()
        {
            var b = new Buckets(10, 2);

            var max = b.MaxBucketValue();
            Assert.AreEqual(3, max);
        }

        /// <summary>
        /// Ensures that Count returns the number of buckets.
        /// </summary>
        [TestMethod]
        public void TestBucketsCount()
        {
            var b = new Buckets(10, 2);

            var count = b.Count;
            Assert.AreEqual(10, count);
        }

        /// <summary>
        /// Ensures that Increment increments the bucket value by the correct delta and
        /// clamps to zero and the maximum, Get returns the correct bucket value, and Set
        /// sets the bucket value correctly.
        /// </summary>
        [TestMethod]
        public void TestBucketsIncrementAndGetAndSet()
        {
            var b = new Buckets(5, 2);

            var incrementedB = b.Increment(0, 1);
            Assert.AreSame(b, incrementedB, "Returned Buckets should be the same instance");

            var v = b.Get(0);
            Assert.AreEqual(1, v);

            b.Increment(1, -1);

            v = b.Get(1);
            Assert.AreEqual(0, v);

            var setB = b.Set(2, 100);
            Assert.AreSame(b, setB, "Returned Buckets should be the same instance");

            v = b.Get(2);
            Assert.AreEqual(3, v);

            b.Increment(3, 2);

            v = b.Get(3);
            Assert.AreEqual(2, v);
        }

        /// <summary>
        /// Ensures that Reset restores the Buckets to the original state.
        /// </summary>
        [TestMethod]
        public void TestBucketsReset()
        {
            var b = new Buckets(5, 2);

            for (uint i = 0; i < 5; i++)
            {
                b.Increment(i, 1);
            }

            var resetB = b.Reset();
            Assert.AreSame(b, resetB, "Returned Buckets should be the same instance");

            for (uint i = 0; i < 5; i++)
            {
                var c = b.Get(i);
                Assert.AreEqual(0, c);
            }
        }

        [TestMethod]
        public void BenchmarkBucketsIncrement()
        {
            var buckets = new Buckets(10000, 10);
            for (uint i = 0; i < buckets.Count; i++)
            {
                buckets.Increment(i % 10000, 1);
            }
        }

        [TestMethod]
        public void BenchmarkBucketsSet()
        {
            var buckets = new Buckets(10000, 10);
            for (uint i = 0; i < buckets.Count; i++)
            {
                buckets.Set(i % 10000, 1);
            }
        }

        [TestMethod]
        public void BenchmarkBucketsGet()
        {
            var buckets = new Buckets(10000, 10);
            for (uint i = 0; i < buckets.Count; i++)
            {
                buckets.Get(i % 10000);
            }
        }
    }
}
