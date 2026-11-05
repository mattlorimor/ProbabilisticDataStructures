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

        // ---- Adding one table of counters to another (#232) -------------------------------

        /// <summary>
        /// A pair of tables holding every pair of values the width can: bucket i + 1 of the
        /// first holds i / (max + 1) and of the second i mod (max + 1), so there is no choice
        /// of pairs to be unlucky in, and the sums run from nought to twice the maximum.
        /// </summary>
        /// <remarks>
        /// The first and last buckets hold nought and one, so that an add which skipped either
        /// end of the table leaves a bucket that should have moved: every pair in order begins
        /// with nought and nought and ends with the maximum and the maximum, and adding either
        /// changes nothing.
        /// </remarks>
        private static (Buckets Mine, Buckets Theirs, Buckets64 Mine64, Buckets64 Theirs64) EveryPair(
            byte bucketSize, Func<Func<Buckets64>, Buckets64> build)
        {
            var max = (1u << bucketSize) - 1;
            var count = (max + 1) * (max + 1) + 2;
            var mine = new Buckets(count, bucketSize);
            var theirs = new Buckets(count, bucketSize);
            var mine64 = build(() => new Buckets64(count, bucketSize));
            var theirs64 = build(() => new Buckets64(count, bucketSize));
            for (uint i = 0; i < count; i++)
            {
                var (a, b) = PairAt(i, count, max);
                mine.Set(i, (byte)a);
                theirs.Set(i, (byte)b);
                mine64.Set(i, (byte)a);
                theirs64.Set(i, (byte)b);
            }

            return (mine, theirs, mine64, theirs64);
        }

        /// <summary>The pair of values <see cref="EveryPair"/> puts in bucket <paramref name="i"/>.</summary>
        private static (uint A, uint B) PairAt(uint i, uint count, uint max) =>
            i == 0 || i == count - 1 ? (0, 1) : ((i - 1) / (max + 1), (i - 1) % (max + 1));

        /// <summary>
        /// Adding a 64-bit table of counters to another adds each counter to its counterpart
        /// and holds the sum at the maximum a counter carries, exactly as
        /// <see cref="Buckets.Add"/> does, which is the oracle: it is what a counting filter's
        /// merge has always done, and what <c>CountingBloomFilter.Merge</c> does now that its
        /// counters are 64-bit tables. The other table is left as it was.
        /// </summary>
        /// <remarks>
        /// Every width, and every pair of values each holds, so that sums both under the
        /// maximum and past it are added, which is checked both ways. Only eight-bit counters
        /// tell the clamp in the sum from the one in <c>Set</c>: below eight bits twice the
        /// maximum still fits a byte, and <c>Set</c> holds it at the maximum anyway, while at
        /// eight a sum past 255 cast to a byte wraps, and is written as what it wrapped to.
        /// The widths of three, five, six and seven bits straddle bytes.
        /// </remarks>
        [TestMethod]
        [DataRow((byte)1)]
        [DataRow((byte)2)]
        [DataRow((byte)3)]
        [DataRow((byte)4)]
        [DataRow((byte)5)]
        [DataRow((byte)6)]
        [DataRow((byte)7)]
        [DataRow((byte)8)]
        public void TestAddingBuckets64AddsEachCounterAndHoldsItAtTheMaximum(byte bucketSize)
        {
            var (mine, theirs, mine64, theirs64) = EveryPair(bucketSize, make => make());
            var max = mine.MaxBucketValue();

            mine.Add(theirs);
            Assert.AreSame(mine64, mine64.Add(theirs64), "Add does not return the table it added to");

            var held = 0;
            var under = 0;
            for (uint i = 0; i < mine.count; i++)
            {
                var (a, b) = PairAt(i, mine.count, max);
                Assert.AreEqual(mine.Get(i), mine64.Get(i), $"{bucketSize}-bit bucket {i}: {a} + {b}");
                Assert.AreEqual(Math.Min(a + b, (uint)max), mine64.Get(i), $"{bucketSize}-bit bucket {i}: {a} + {b}, held at {max}");
                Assert.AreEqual(b, theirs64.Get(i), $"{bucketSize}-bit bucket {i} of the table added was changed");
                if (a + b > max) held++; else under++;
            }

            Assert.IsGreaterThan(0, held, "no sum passed the maximum, so the clamp was never needed");
            Assert.IsGreaterThan(0, under, "every sum passed the maximum, so nothing was added");
        }

        /// <summary>
        /// The add is a bucket at a time through each table's own layout, so tables split into
        /// arrays at different sizes add as tables of one array do: lowered to arrays of 16
        /// bytes on either side or both, three-bit counters straddle arrays as well as bytes
        /// (bucket 42 holds bits 126 to 128, the last two of the first array and the first of
        /// the second).
        /// </summary>
        [TestMethod]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(true, true)]
        public void TestAddingBuckets64OfSeveralArraysAddsAsOneArrayDoes(bool mineLowered, bool theirsLowered)
        {
            const byte BucketSize = 3;
            var (mine, theirs, _, _) = EveryPair(BucketSize, make => make());
            Buckets64 Build(bool lowered) =>
                lowered ? WithArrayBits(4, () => new Buckets64(mine.count, BucketSize)) : new Buckets64(mine.count, BucketSize);
            var mine64 = Build(mineLowered);
            var theirs64 = Build(theirsLowered);
            for (uint i = 0; i < mine.count; i++)
            {
                mine64.Set(i, (byte)mine.Get(i));
                theirs64.Set(i, (byte)theirs.Get(i));
            }

            Assert.IsGreaterThan(1, (mineLowered ? mine64 : theirs64).RawData.Count(array => array.Length > 0),
                "premise: a lowered table fills several arrays");

            mine.Add(theirs);
            mine64.Add(theirs64);
            for (uint i = 0; i < mine.count; i++)
            {
                Assert.AreEqual(mine.Get(i), mine64.Get(i), $"bucket {i}");
            }
        }

        /// <summary>
        /// Two 64-bit tables that disagree about how many counters there are, or how wide
        /// each is, are refused, and the receiver is left as it was, as
        /// <see cref="TestBuckets.TestCombiningBucketsOfDifferentGeometryIsRefused"/> holds
        /// <see cref="Buckets.Add"/> to: each field varied alone, the other table larger and
        /// smaller. Reachable only from inside the library, since the counting filter's merge
        /// checks its geometry first.
        /// </summary>
        [TestMethod]
        [DataRow(8UL, (byte)4, 9UL, (byte)4, "count only, the other larger")]
        [DataRow(9UL, (byte)4, 8UL, (byte)4, "count only, the other smaller")]
        [DataRow(8UL, (byte)4, 8UL, (byte)3, "width only, the other narrower")]
        [DataRow(8UL, (byte)3, 8UL, (byte)4, "width only, the other wider")]
        public void TestAddingBuckets64OfDifferentGeometryIsRefused(
            ulong thisCount, byte thisSize, ulong otherCount, byte otherSize, string what)
        {
            var before = new Buckets64(thisCount, thisSize);
            for (ulong i = 0; i < thisCount; i++)
            {
                before.Set(i, (byte)(i % before.MaxBucketValue() + 1));
            }
            var snapshot = before.RawData.Select(array => array.ToArray()).ToArray();
            var other = new Buckets64(otherCount, otherSize);
            for (ulong i = 0; i < otherCount; i++)
            {
                other.Set(i, 1);
            }

            Assert.ThrowsExactly<ArgumentException>(() => before.Add(other),
                $"Add must refuse 64-bit tables that differ in {what}.");
            Assert.HasCount(snapshot.Length, before.RawData);
            for (var a = 0; a < snapshot.Length; a++)
            {
                CollectionAssert.AreEqual(snapshot[a], before.RawData[a],
                    $"A refused Add ({what}) must leave the receiver as it found it.");
            }
        }
    }
}
