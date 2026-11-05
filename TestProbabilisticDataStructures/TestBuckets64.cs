using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
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
            var buckets = new Buckets64(10000, 8);
            for (uint i = 0; i < buckets.count; i++)
            {
                buckets.Increment(i % 10000, 1);
            }
        }

        [TestMethod]
        public void BenchmarkBuckets64Set()
        {
            var buckets = new Buckets64(10000, 8);
            for (uint i = 0; i < buckets.count; i++)
            {
                buckets.Set(i % 10000, 1);
            }
        }

        [TestMethod]
        public void BenchmarkBuckets64Get()
        {
            var buckets = new Buckets64(10000, 8);
            for (uint i = 0; i < buckets.count; i++)
            {
                buckets.Get(i % 10000);
            }
        }

        // ---- The lowered array size (#297) --------------------------------------------------

        /// <summary>
        /// Runs <paramref name="work"/> with the buckets built or restored on this thread split
        /// into arrays of 2^<paramref name="bits"/> bytes, and puts the size back afterwards.
        /// </summary>
        internal static T WithArrayBits<T>(int bits, Func<T> work)
        {
            var before = Buckets64.LoweredArrayBits;
            Buckets64.LoweredArrayBits = bits;
            try
            {
                return work();
            }
            finally
            {
                Buckets64.LoweredArrayBits = before;
            }
        }

        /// <summary>Runs <paramref name="work"/> on a new thread, rethrowing what it threw.</summary>
        private static T OnAnotherThread<T>(Func<T> work)
        {
            T result = default;
            ExceptionDispatchInfo thrown = null;
            var other = new Thread(() =>
            {
                try { result = work(); }
                catch (Exception e) { thrown = ExceptionDispatchInfo.Capture(e); }
            });
            other.Start();
            other.Join();
            thrown?.Throw();
            return result;
        }

        private static int[] Lengths(Buckets64 buckets) => buckets.RawData.Select(array => array.Length).ToArray();

        /// <summary>
        /// Unlowered, buckets are split into arrays of 2^30 bytes, as every stored payload's
        /// are. Lowered, the size is lowered for the thread that lowered it and no other, since
        /// the suite runs its tests side by side and every other test's buckets must stay as
        /// they are. The most buckets the type holds follows the size, so that the count of
        /// arrays still fits the array holding them: at arrays of a byte, 2^31 buckets are
        /// refused before anything is allocated, where at the real size they are 2^28 bytes.
        /// </summary>
        [TestMethod]
        public void TestTheArraySizeIsTheRealOneUnlessLoweredOnThisThread()
        {
            const ulong Count = 3 * 4096 + 5;
            Assert.IsNull(Buckets64.LoweredArrayBits);
            Assert.AreEqual((ulong)Buckets64.maxArraySize, Buckets64.ArraySize);
            Assert.AreEqual((ulong)Array.MaxLength * Buckets64.maxArraySize - 1, Buckets64.MaxCount);
            CollectionAssert.AreEqual(new[] { 12_293 }, Lengths(new Buckets64(Count, 8)));

            WithArrayBits(12, () =>
            {
                Assert.AreEqual(4096UL, Buckets64.ArraySize);
                Assert.AreEqual((ulong)Array.MaxLength * 4096 - 1, Buckets64.MaxCount);
                CollectionAssert.AreEqual(new[] { 4096, 4096, 4096, 5 }, Lengths(new Buckets64(Count, 8)));

                var (size, lengths) = OnAnotherThread(() => (Buckets64.ArraySize, Lengths(new Buckets64(Count, 8))));
                Assert.AreEqual((ulong)Buckets64.maxArraySize, size, "a size lowered on one thread was seen on another");
                CollectionAssert.AreEqual(new[] { 12_293 }, lengths,
                    "buckets built on another thread were split at the size lowered on this one");
                return 0;
            });

            Assert.IsNull(Buckets64.LoweredArrayBits, "the size was not put back");
            Assert.AreEqual((ulong)Buckets64.maxArraySize, Buckets64.ArraySize, "the size was not put back");

            WithArrayBits(0, () =>
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Buckets64(1UL << 31, 1)));
        }

        /// <summary>
        /// Buckets split at a lowered size answer for every bucket as buckets of the real size
        /// do, on the thread that built them, on another, and once the size is put back: they
        /// keep the size they were built at, and a reset on another thread clears every array
        /// they have. Three-bit buckets, so that some straddle a byte, and with arrays of 4 KiB
        /// one straddles two arrays: bucket 10,922 holds bits 32,766 to 32,768, the last two of
        /// the first array and the first of the second.
        /// </summary>
        [TestMethod]
        public void TestBucketsOfLoweredArraysAnswerAsTheRealOnesDo()
        {
            const ulong Count = 16_391;
            static byte Value(ulong i) => (byte)((i * 5 + 3) % 8);
            static void Fill(Buckets64 buckets, ulong from, ulong to)
            {
                for (var i = from; i < to; i++) buckets.Set(i, Value(i));
            }

            var real = new Buckets64(Count, 3);
            Fill(real, 0, Count);
            var lowered = WithArrayBits(12, () =>
            {
                var built = new Buckets64(Count, 3);
                Fill(built, 0, Count / 2);
                return built;
            });
            CollectionAssert.AreEqual(new[] { 6_147 }, Lengths(real), "premise: the real buckets are one array");
            CollectionAssert.AreEqual(new[] { 4096, 2051, 0, 0, 0 }, Lengths(lowered), "premise: the lowered buckets are several");

            OnAnotherThread(() =>
            {
                Fill(lowered, Count / 2, Count);
                return 0;
            });
            for (var i = 0UL; i < Count; i++)
            {
                Assert.AreEqual(real.Get(i), lowered.Get(i), $"bucket {i}, read with the size put back");
            }
            WithArrayBits(12, () =>
            {
                for (var i = 0UL; i < Count; i++)
                {
                    Assert.AreEqual(real.Get(i), lowered.Get(i), $"bucket {i}, read with the size lowered");
                }
                return 0;
            });

            OnAnotherThread(() => lowered.Reset());
            CollectionAssert.AreEqual(new[] { 4096, 2051, 0, 0, 0 }, Lengths(lowered), "the reset changed the arrays");
            Assert.IsTrue(lowered.RawData.All(array => array.AsSpan().IndexOfAnyExcept((byte)0) < 0),
                "a reset on another thread left a bucket set");
        }

        /// <summary>
        /// A 64-bit Bloom filter built, written, read and reset with its arrays lowered is one
        /// filter throughout: its payload reads back as the bytes it was read from, the copy
        /// answers as a filter of the real size with the same keys does, and the copy's reset
        /// clears every array. The payload holds more arrays than its count implies at the
        /// real size, so a reader on a thread that has not lowered the size refuses it for
        /// that: a payload of buckets of several lowered arrays is never one the library stores.
        /// </summary>
        [TestMethod]
        public void TestABloomFilter64OfLoweredArraysIsOneFilterThroughout()
        {
            static byte[] Key(int i) => BitConverter.GetBytes(i);
            var real = new BloomFilter64(10_000, 0.01);
            for (var i = 0; i < 1_000; i++) real.Add(Key(i));

            var bytes = WithArrayBits(10, () =>
            {
                var filter = new BloomFilter64(10_000, 0.01);
                for (var i = 0; i < 1_000; i++) filter.Add(Key(i));
                Assert.IsGreaterThan(1, filter.Buckets.RawData.Count(array => array.Length > 0),
                    "premise: the bits fill several arrays");

                var written = filter.ToByteArray();
                var read = Persistence.FromByteArray<BloomFilter64>(written);
                CollectionAssert.AreEqual(written, read.ToByteArray(), "the read filter writes other bytes");
                for (var i = 0; i < 2_000; i++)
                {
                    Assert.AreEqual(real.Test(Key(i)), read.Test(Key(i)), $"key {i}");
                }

                read.Reset();
                Assert.IsTrue(read.Buckets.RawData.All(array => array.AsSpan().IndexOfAnyExcept((byte)0) < 0),
                    "the reset left a bit set");
                return written;
            });

            var refused = OnAnotherThread(() =>
                Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<BloomFilter64>(bytes)));
            StringAssert.Contains(refused.Message, "arrays and");
        }
    }
}
