using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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

        /// <summary>
        /// A counting filter whose counters fill several lowered arrays is one filter
        /// throughout, as a <see cref="BloomFilter64"/> is: given the same keys as a filter of
        /// the real size, it removes, merges, counts and answers as that one does and holds
        /// the same counters, one of them held at its maximum by the merge. Its payload reads
        /// back as the bytes it was read from, the reset of the copy clears every array, and a
        /// reader that has not lowered the size refuses the payload, which holds more arrays
        /// than its count implies at the real size.
        /// </summary>
        [TestMethod]
        public void TestACountingFilterOfLoweredArraysIsOneFilterThroughout()
        {
            static byte[] Key(int i) => BitConverter.GetBytes(i);
            static (CountingBloomFilter Filter, bool[] Removed, bool[] Answers) Exercised()
            {
                var first = new CountingBloomFilter(10_000, 4, 0.01);
                var second = new CountingBloomFilter(10_000, 4, 0.01);
                for (var i = 0; i < 1_000; i++) first.Add(Key(i));
                for (var i = 500; i < 1_500; i++) second.Add(Key(i));
                for (var i = 0; i < 10; i++)
                {
                    first.Add(Key(-1));
                    second.Add(Key(-1));
                }

                var removed = Enumerable.Range(0, 400).Select(i => first.TestAndRemove(Key(3 * i))).ToArray();
                first.Merge(second);
                return (first, removed, Enumerable.Range(-1, 3_000).Select(i => first.Test(Key(i))).ToArray());
            }

            var (real, realRemoved, realAnswers) = Exercised();
            Assert.Contains(real.Buckets.MaxBucketValue(),
                Enumerable.Range(0, checked((int)real.Buckets.count)).Select(i => real.Buckets.Get((ulong)i)).ToArray(),
                "premise: the merge holds a counter at its maximum");

            var (filter, bytes, removed, answers) = WithArrayBits(10, () =>
            {
                var (exercised, removedHere, answersHere) = Exercised();
                Assert.IsGreaterThan(1, exercised.Buckets.RawData.Count(array => array.Length > 0),
                    "premise: the counters fill several arrays");

                var written = exercised.ToByteArray();
                var read = Persistence.FromByteArray<CountingBloomFilter>(written);
                CollectionAssert.AreEqual(written, read.ToByteArray(), "the read filter writes other bytes");

                read.Reset();
                Assert.IsTrue(read.Buckets.RawData.All(array => array.AsSpan().IndexOfAnyExcept((byte)0) < 0),
                    "the reset left a counter set");
                return (exercised, written, removedHere, answersHere);
            });

            CollectionAssert.AreEqual(realRemoved, removed, "the filters removed other keys");
            CollectionAssert.AreEqual(realAnswers, answers, "the filters answer otherwise");
            Assert.AreEqual(real.Count(), filter.Count());
            Assert.AreEqual(real.Buckets.count, filter.Buckets.count);
            for (ulong i = 0; i < real.Buckets.count; i++)
            {
                Assert.AreEqual(real.Buckets.Get(i), filter.Buckets.Get(i), $"counter {i}");
            }

            var refused = OnAnotherThread(() =>
                Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<CountingBloomFilter>(bytes)));
            StringAssert.Contains(refused.Message, "arrays and");
        }

        /// <summary>
        /// A counting filter saved before version 14 holds its counters as one run, which
        /// is kept as the first array of a 64-bit table when it fits one and split into arrays
        /// of the table's size when it does not. Unsplit, the run is the first array and the
        /// rest are empty. Split, the counters are the same: with arrays of 16 bytes, the
        /// filter read holds the counters of the one read at the real size, and saves as it
        /// does once both are narrowed back to version 8. Either way it is charged what it
        /// was priced at, and the split, which copies the run once, is charged what it
        /// allocates beyond the unsplit read, to the byte.
        /// </summary>
        [TestMethod]
        [DataRow("countingbloomfilter-v1.bin")]
        [DataRow("countingbloomfilter-v8-narrow.bin")]
        [DataRow("countingbloomfilter-v8-wide.bin")]
        public void TestAnEarlierCountingPayloadIsLaidOutAsOneTableOfArrays(string fixture)
        {
            const int Bits = 4, Size = 16;
            var bytes = TestPersistenceFormatStability.ReadFixture(fixture);
            (CountingBloomFilter Filter, long Charged, long Allocated) Read()
            {
                var budget = new ReadBudget(long.MaxValue, StructureId.CountingBloomFilter);
                var read = Persistence.FromByteArray<CountingBloomFilter>(bytes, new ReadOptions(budget));

                // A charge past the price raises it, so the price alone is what a budget of
                // nothing is refused for.
                var priced = ((ReadBudgetExceededException)Assert.ThrowsExactly<InvalidDataException>(
                    () => Persistence.FromByteArray<CountingBloomFilter>(bytes, new ReadOptions(0))).InnerException!).RequiredBytes;
                Assert.AreEqual(priced, budget.Spent, $"{fixture} was priced at one sum and charged another");
                var allocated = TestReadBudget.Allocated(() => Persistence.FromByteArray<CountingBloomFilter>(bytes));
                return (read, budget.Spent, allocated);
            }

            var real = Read();
            var run = checked((int)((real.Filter.Buckets.count * real.Filter.Buckets.BucketSize + 7) / 8));
            var arrays = real.Filter.Buckets.RawData;
            Assert.AreEqual(run, arrays[0].Length, "the run is not the first array");
            Assert.IsTrue(arrays.Skip(1).All(array => array.Length == 0), "an array past the first holds something");

            var split = WithArrayBits(Bits, Read);
            var pieces = split.Filter.Buckets.RawData.Where(array => array.Length > 0).ToArray();
            Assert.AreEqual((run + Size - 1) / Size, pieces.Length, "premise: the run was split into arrays of 16 bytes");
            Assert.IsTrue(pieces.All(piece => piece.Length <= Size), "a piece is longer than an array");
            Assert.AreEqual(run, pieces.Sum(piece => piece.Length));
            for (ulong i = 0; i < real.Filter.Buckets.count; i++)
            {
                Assert.AreEqual(real.Filter.Buckets.Get(i), split.Filter.Buckets.Get(i), $"counter {i}");
            }

            CollectionAssert.AreEqual(TestBitPlacement.Narrowed(real.Filter.ToByteArray(), "mubqt"),
                TestBitPlacement.Narrowed(split.Filter.ToByteArray(), "mubqt"), "the split filter saves another filter");

            Console.WriteLine($"[LAID-OUT] {fixture}: run {run}, charged {real.Charged} and {split.Charged}, " +
                $"allocated {real.Allocated} and {split.Allocated}");
            Assert.IsGreaterThan(0L, split.Charged - real.Charged, "premise: the split costs something");
            Assert.AreEqual(split.Allocated - real.Allocated, split.Charged - real.Charged,
                "the split was charged other than what it allocated beyond the unsplit read");
        }

        /// <summary>
        /// A deletable filter whose two regions fill several lowered arrays is one filter
        /// throughout: given the same keys as a filter of the real size, it adds, removes
        /// (some refused for their collided regions) and answers as that one does, and holds
        /// the same bits in both regions. Its payload reads back as the bytes it was read
        /// from, and a reader that has not lowered the size refuses it.
        /// </summary>
        [TestMethod]
        public void TestADeletableFilterOfLoweredArraysIsOneFilterThroughout()
        {
            static byte[] Key(int i) => BitConverter.GetBytes(i);
            static (DeletableBloomFilter Filter, bool[] Removed, bool[] Answers) Exercised()
            {
                var filter = new DeletableBloomFilter(10_000, 1000, 0.01);
                for (var i = 0; i < 3_000; i++) filter.Add(Key(i));
                var removed = Enumerable.Range(0, 1_000).Select(i => filter.TestAndRemove(Key(3 * i))).ToArray();
                return (filter, removed, Enumerable.Range(0, 6_000).Select(i => filter.Test(Key(i))).ToArray());
            }

            static void AssertSame(Buckets64 expected, Buckets64 actual, string what)
            {
                Assert.AreEqual(expected.count, actual.count, what);
                for (ulong i = 0; i < expected.count; i++)
                {
                    Assert.AreEqual(expected.Get(i), actual.Get(i), $"{what}, bit {i}");
                }
            }

            var (real, realRemoved, realAnswers) = Exercised();
            var (filter, bytes, removed, answers) = WithArrayBits(4, () =>
            {
                var (exercised, removedHere, answersHere) = Exercised();
                Assert.IsGreaterThan(1, exercised.Buckets.RawData.Count(array => array.Length > 0),
                    "premise: the data region fills several arrays");
                Assert.IsGreaterThan(1, exercised.Collisions.RawData.Count(array => array.Length > 0),
                    "premise: the collision regions fill several arrays");

                var written = exercised.ToByteArray();
                CollectionAssert.AreEqual(written, Persistence.FromByteArray<DeletableBloomFilter>(written).ToByteArray(),
                    "the read filter writes other bytes");
                return (exercised, written, removedHere, answersHere);
            });

            var stillThere = Enumerable.Range(0, 1_000).Select(i => realAnswers[3 * i]).ToArray();
            Assert.Contains(true, stillThere, "premise: some removed keys are still members, refused for their regions");
            Assert.Contains(false, stillThere, "premise: some removals cleared their keys");
            CollectionAssert.AreEqual(realRemoved, removed, "the filters removed other keys");
            CollectionAssert.AreEqual(realAnswers, answers, "the filters answer otherwise");
            Assert.AreEqual(real.Count(), filter.Count());
            AssertSame(real.Buckets, filter.Buckets, "the data region");
            AssertSame(real.Collisions, filter.Collisions, "the collision regions");

            var refused = OnAnotherThread(() =>
                Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<DeletableBloomFilter>(bytes)));
            StringAssert.Contains(refused.Message, "arrays and");
        }

        /// <summary>
        /// A deletable filter saved before version 14 holds each region as one run, and each
        /// is laid out as a 64-bit table's arrays, as the counting filter's counters are: at
        /// arrays of a byte both regions are split, hold the bits of the filter read at the
        /// real size and save as it does once narrowed back to version 8; the read is priced
        /// at what it is charged, and the splits are charged what they allocate beyond the
        /// unsplit read, to the byte.
        /// </summary>
        [TestMethod]
        [DataRow("deletablebloomfilter-v1.bin")]
        [DataRow("deletablebloomfilter-v8-narrow.bin")]
        [DataRow("deletablebloomfilter-v8-wide.bin")]
        public void TestAnEarlierDeletablePayloadIsLaidOutAsTablesOfArrays(string fixture)
        {
            var bytes = TestPersistenceFormatStability.ReadFixture(fixture);
            (DeletableBloomFilter Filter, long Charged, long Allocated) Read()
            {
                var budget = new ReadBudget(long.MaxValue, StructureId.DeletableBloomFilter);
                var read = Persistence.FromByteArray<DeletableBloomFilter>(bytes, new ReadOptions(budget));
                var priced = ((ReadBudgetExceededException)Assert.ThrowsExactly<InvalidDataException>(
                    () => Persistence.FromByteArray<DeletableBloomFilter>(bytes, new ReadOptions(0))).InnerException!).RequiredBytes;
                Assert.AreEqual(priced, budget.Spent, $"{fixture} was priced at one sum and charged another");
                var allocated = TestReadBudget.Allocated(() => Persistence.FromByteArray<DeletableBloomFilter>(bytes));
                return (read, budget.Spent, allocated);
            }

            var real = Read();
            var split = WithArrayBits(0, Read);
            foreach (var (what, of) in new (string, Func<DeletableBloomFilter, Buckets64>)[]
            {
                ("the data region", f => f.Buckets), ("the collision regions", f => f.Collisions),
            })
            {
                var (kept, laid) = (of(real.Filter), of(split.Filter));
                Assert.AreEqual(1, kept.RawData.Count(array => array.Length > 0), $"{what} is not one array at the real size");
                Assert.IsGreaterThan(1, laid.RawData.Count(array => array.Length > 0), $"premise: {what} was split");
                Assert.IsTrue(laid.RawData.All(array => array.Length <= 1), $"{what} has a piece longer than an array");
                for (ulong i = 0; i < kept.count; i++)
                {
                    Assert.AreEqual(kept.Get(i), laid.Get(i), $"{what}, bit {i}");
                }
            }

            CollectionAssert.AreEqual(TestBitPlacement.Narrowed(real.Filter.ToByteArray(), "mubqtt"),
                TestBitPlacement.Narrowed(split.Filter.ToByteArray(), "mubqtt"), "the split filter saves another filter");
            Assert.IsGreaterThan(0L, split.Charged - real.Charged, "premise: the split costs something");
            Assert.AreEqual(split.Allocated - real.Allocated, split.Charged - real.Charged,
                "the split was charged other than what it allocated beyond the unsplit read");
        }

        /// <summary>
        /// A stable filter whose cells fill several lowered arrays is one filter throughout:
        /// seeded alike and given the same keys as a filter of the real size, it decays the
        /// same cells, its runs crossing from one array into the next, and answers and holds
        /// as that one does. Its payload reads back as the bytes it was read from, the copy's
        /// reset clears every array, and a reader that has not lowered the size refuses it.
        /// </summary>
        [TestMethod]
        public void TestAStableFilterOfLoweredArraysIsOneFilterThroughout()
        {
            static byte[] Key(int i) => BitConverter.GetBytes(i);
            static (StableBloomFilter Filter, bool[] Answers) Exercised()
            {
                var filter = new StableBloomFilter(2_000, 3, 0.01, null, 11);
                for (var i = 0; i < 3_000; i++) filter.Add(Key(i));
                return (filter, Enumerable.Range(0, 6_000).Select(i => filter.Test(Key(i))).ToArray());
            }

            var (real, realAnswers) = Exercised();
            var (filter, bytes, answers, reset) = WithArrayBits(4, () =>
            {
                var (exercised, answersHere) = Exercised();
                Assert.IsGreaterThan(1, exercised.cells.RawData.Count(array => array.Length > 0),
                    "premise: the cells fill several arrays");

                var written = exercised.ToByteArray();
                var read = Persistence.FromByteArray<StableBloomFilter>(written);
                CollectionAssert.AreEqual(written, read.ToByteArray(), "the read filter writes other bytes");
                read.Reset();
                return (exercised, written, answersHere, read);
            });

            Assert.Contains(false, realAnswers.Take(3_000).ToArray(), "premise: the filter forgot some keys");
            CollectionAssert.AreEqual(realAnswers, answers, "the filters answer otherwise");
            Assert.AreEqual(real.cells.count, filter.cells.count);
            for (ulong i = 0; i < real.cells.count; i++)
            {
                Assert.AreEqual(real.cells.Get(i), filter.cells.Get(i), $"cell {i}");
            }

            Assert.IsTrue(reset.cells.RawData.All(array => array.AsSpan().IndexOfAnyExcept((byte)0) < 0),
                "the reset left a cell set");
            var refused = OnAnotherThread(() =>
                Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<StableBloomFilter>(bytes)));
            StringAssert.Contains(refused.Message, "arrays and");
        }

        /// <summary>
        /// A stable filter saved before version 14 holds its cells as one run, laid out as a
        /// 64-bit table's arrays as the counting filter's counters are: at arrays of a byte
        /// the cells are split, hold what the read at the real size holds and save as it
        /// does once narrowed back to version 8; the read is priced at what it is charged,
        /// and the split is charged what it allocates beyond the unsplit read, to the byte.
        /// </summary>
        [TestMethod]
        [DataRow("stablebloomfilter-v1.bin")]
        [DataRow("stablebloomfilter-v5.bin")]
        [DataRow("stablebloomfilter-v8-narrow.bin")]
        [DataRow("stablebloomfilter-v8-wide.bin")]
        public void TestAnEarlierStablePayloadIsLaidOutAsATableOfArrays(string fixture)
        {
            var bytes = TestPersistenceFormatStability.ReadFixture(fixture);
            (StableBloomFilter Filter, long Charged, long Allocated) Read()
            {
                var budget = new ReadBudget(long.MaxValue, StructureId.StableBloomFilter);
                var read = Persistence.FromByteArray<StableBloomFilter>(bytes, new ReadOptions(budget));
                var priced = ((ReadBudgetExceededException)Assert.ThrowsExactly<InvalidDataException>(
                    () => Persistence.FromByteArray<StableBloomFilter>(bytes, new ReadOptions(0))).InnerException!).RequiredBytes;
                Assert.AreEqual(priced, budget.Spent, $"{fixture} was priced at one sum and charged another");
                var allocated = TestReadBudget.Allocated(() => Persistence.FromByteArray<StableBloomFilter>(bytes));
                return (read, budget.Spent, allocated);
            }

            var real = Read();
            var split = WithArrayBits(0, Read);
            Assert.AreEqual(1, real.Filter.cells.RawData.Count(array => array.Length > 0), "the cells are not one array at the real size");
            Assert.IsGreaterThan(1, split.Filter.cells.RawData.Count(array => array.Length > 0), "premise: the cells were split");
            for (ulong i = 0; i < real.Filter.cells.count; i++)
            {
                Assert.AreEqual(real.Filter.cells.Get(i), split.Filter.cells.Get(i), $"cell {i}");
            }

            // A version 1 payload has no stored generator state, so each read takes an
            // unpredictable one, and only its cells are compared.
            if (!fixture.EndsWith("v1.bin", StringComparison.Ordinal))
            {
                CollectionAssert.AreEqual(TestBitPlacement.Narrowed(real.Filter.ToByteArray(), "muubtqq"),
                    TestBitPlacement.Narrowed(split.Filter.ToByteArray(), "muubtqq"), "the split filter saves another filter");
            }

            Assert.IsGreaterThan(0L, split.Charged - real.Charged, "premise: the split costs something");
            Assert.AreEqual(split.Allocated - real.Allocated, split.Charged - real.Charged,
                "the split was charged other than what it allocated beyond the unsplit read");
        }

        /// <summary>
        /// A partitioned filter whose partitions each fill several lowered arrays is one
        /// filter throughout: given the same keys as a filter of the real size, and merged
        /// with another, it answers as that one does and holds the same bits in every
        /// partition. Its payload reads back as the bytes it was read from, and a reader that
        /// has not lowered the size refuses it.
        /// </summary>
        [TestMethod]
        public void TestAPartitionedFilterOfLoweredArraysIsOneFilterThroughout()
        {
            static byte[] Key(int i) => BitConverter.GetBytes(i);
            static (PartitionedBloomFilter Filter, bool[] Answers) Exercised()
            {
                var filter = new PartitionedBloomFilter(10_000, 0.01);
                var other = new PartitionedBloomFilter(10_000, 0.01);
                for (var i = 0; i < 3_000; i++) filter.Add(Key(i));
                for (var i = 3_000; i < 5_000; i++) other.Add(Key(i));
                filter.Merge(other);
                return (filter, Enumerable.Range(0, 10_000).Select(i => filter.Test(Key(i))).ToArray());
            }

            var (real, realAnswers) = Exercised();
            var (filter, bytes, answers) = WithArrayBits(4, () =>
            {
                var (exercised, answersHere) = Exercised();
                Assert.IsTrue(exercised.Partitions.All(p => p.RawData.Count(array => array.Length > 0) > 1),
                    "premise: every partition fills several arrays");

                var written = exercised.ToByteArray();
                CollectionAssert.AreEqual(written, Persistence.FromByteArray<PartitionedBloomFilter>(written).ToByteArray(),
                    "the read filter writes other bytes");
                return (exercised, written, answersHere);
            });

            Assert.Contains(false, realAnswers, "premise: some keys are not members");
            CollectionAssert.AreEqual(realAnswers, answers, "the filters answer otherwise");
            Assert.AreEqual(real.Count(), filter.Count());
            Assert.AreEqual(real.Partitions.Length, filter.Partitions.Length);
            for (var p = 0; p < real.Partitions.Length; p++)
            {
                Assert.AreEqual(real.Partitions[p].count, filter.Partitions[p].count, $"partition {p}");
                for (ulong i = 0; i < real.Partitions[p].count; i++)
                {
                    Assert.AreEqual(real.Partitions[p].Get(i), filter.Partitions[p].Get(i), $"partition {p}, bit {i}");
                }
            }

            var refused = OnAnotherThread(() =>
                Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<PartitionedBloomFilter>(bytes)));
            StringAssert.Contains(refused.Message, "arrays and");
        }

        /// <summary>
        /// A partitioned filter saved before version 14 holds each partition as one run, and
        /// each is laid out as a 64-bit table's arrays, as the counting filter's counters
        /// are: at arrays of a byte every partition is split, holds the bits of the filter
        /// read at the real size and saves as it does once narrowed back to version 8; the
        /// read is priced at what it is charged, and the splits are charged what they
        /// allocate beyond the unsplit read, to the byte.
        /// </summary>
        [TestMethod]
        [DataRow("partitionedbloomfilter-v1.bin")]
        [DataRow("partitionedbloomfilter-v8-narrow.bin")]
        [DataRow("partitionedbloomfilter-v8-wide.bin")]
        public void TestAnEarlierPartitionedPayloadIsLaidOutAsTablesOfArrays(string fixture)
        {
            var bytes = TestPersistenceFormatStability.ReadFixture(fixture);
            (PartitionedBloomFilter Filter, long Charged, long Allocated) Read()
            {
                var budget = new ReadBudget(long.MaxValue, StructureId.PartitionedBloomFilter);
                var read = Persistence.FromByteArray<PartitionedBloomFilter>(bytes, new ReadOptions(budget));
                var priced = ((ReadBudgetExceededException)Assert.ThrowsExactly<InvalidDataException>(
                    () => Persistence.FromByteArray<PartitionedBloomFilter>(bytes, new ReadOptions(0))).InnerException!).RequiredBytes;
                Assert.AreEqual(priced, budget.Spent, $"{fixture} was priced at one sum and charged another");
                var allocated = TestReadBudget.Allocated(() => Persistence.FromByteArray<PartitionedBloomFilter>(bytes));
                return (read, budget.Spent, allocated);
            }

            var real = Read();
            var split = WithArrayBits(0, Read);
            for (var p = 0; p < real.Filter.Partitions.Length; p++)
            {
                var (kept, laid) = (real.Filter.Partitions[p], split.Filter.Partitions[p]);
                Assert.AreEqual(1, kept.RawData.Count(array => array.Length > 0), $"partition {p} is not one array at the real size");
                Assert.IsGreaterThan(1, laid.RawData.Count(array => array.Length > 0), $"premise: partition {p} was split");
                Assert.IsTrue(laid.RawData.All(array => array.Length <= 1), $"partition {p} has a piece longer than an array");
                for (ulong i = 0; i < kept.count; i++)
                {
                    Assert.AreEqual(kept.Get(i), laid.Get(i), $"partition {p}, bit {i}");
                }
            }

            CollectionAssert.AreEqual(TestBitPlacement.Narrowed(real.Filter.ToByteArray(), "mumbqn"),
                TestBitPlacement.Narrowed(split.Filter.ToByteArray(), "mumbqn"), "the split filter saves another filter");
            Assert.IsGreaterThan(0L, split.Charged - real.Charged, "premise: the split costs something");
            Assert.AreEqual(split.Allocated - real.Allocated, split.Charged - real.Charged,
                "the split was charged other than what it allocated beyond the unsplit read");
        }

        /// <summary>
        /// Past 2^32 cells a decay run's start and its cells are 64-bit (#232), which no
        /// filter in the suite can be built large enough to show, so the two functions are
        /// held to it directly: the start is the high half of the 128-bit product, which
        /// reaches past 2^32 and stays below m, and a run wraps from the last cell to the
        /// first. Below 2^32 cells they give what the 32-bit ones gave, which
        /// TestStableDecayDraw holds.
        /// </summary>
        [TestMethod]
        public void TestADecayRunPastTwoToTheThirtyTwoCellsIsPlacedInSixtyFourBits()
        {
            var m = (1UL << 40) + 5;
            Assert.AreEqual(0UL, StableBloomFilter.DecayStart(0, m));
            Assert.AreEqual(m - 1, StableBloomFilter.DecayStart(ulong.MaxValue, m));
            Assert.AreEqual(m / 2, StableBloomFilter.DecayStart(1UL << 63, m));
            Assert.IsGreaterThan((ulong)uint.MaxValue, StableBloomFilter.DecayStart(1UL << 63, m), "premise: a start past 2^32");

            Assert.AreEqual(m - 1, StableBloomFilter.DecayCell(m - 2, 1, m));
            Assert.AreEqual(0UL, StableBloomFilter.DecayCell(m - 1, 1, m));
            Assert.AreEqual(2UL, StableBloomFilter.DecayCell(m - 1, 3, m));
            Assert.AreEqual((1UL << 32) + 1, StableBloomFilter.DecayCell(1UL << 32, 1, m));
        }

        /// <summary>
        /// The one boundary in laying out a run of buckets as a 64-bit table, at arrays of 16
        /// bytes: runs of 15 and 16 eight-bit buckets fit the first array and are kept, the
        /// 16's second array empty, and one of 17 is split into 16 bytes and 1, which copies
        /// it. Each is read with the buckets it holds and priced at what it is charged. What
        /// each allocates beyond its charge is, to the byte, what reading a table stored at
        /// version 14 allocates beyond its own: the two readers build the same table, so
        /// what differs is the arrays, and each is charged for the arrays it allocates.
        /// </summary>
        [TestMethod]
        public void TestARunIsChargedWhatLayingItOutAsATableAllocates()
        {
            const int Size = 16;
            static byte[] Run(int length)
            {
                // Version 8: u32 count, u8 width, then the run's u32 length and its bytes.
                var payload = new List<byte>(BitConverter.GetBytes((uint)length)) { 8 };
                payload.AddRange(BitConverter.GetBytes((uint)length));
                payload.AddRange(Enumerable.Range(1, length).Select(i => (byte)i));
                return payload.ToArray();
            }

            static byte[] Table(int length)
            {
                // Version 14: u64 count, u8 width, u32 array count, then each array's run.
                var arrays = length / Size + 1;
                var payload = new List<byte>(BitConverter.GetBytes((ulong)length)) { 8 };
                payload.AddRange(BitConverter.GetBytes((uint)arrays));
                for (var a = 0; a < arrays; a++)
                {
                    var piece = Math.Clamp(length - Size * a, 0, Size);
                    payload.AddRange(BitConverter.GetBytes((uint)piece));
                    payload.AddRange(Enumerable.Range(Size * a + 1, piece).Select(i => (byte)i));
                }

                return payload.ToArray();
            }

            static (Buckets64 Buckets, long Beyond) Read(byte[] payload, TableReader read)
            {
                ReadBudget budget = null;
                var allocated = NoGCRegion.Allocated(() =>
                {
                    var reader = new PayloadReader(payload, budget);
                    return read(ref reader);
                }, () => budget = new ReadBudget(long.MaxValue, StructureId.CountingBloomFilter));
                var again = new PayloadReader(payload, new ReadBudget(long.MaxValue, StructureId.CountingBloomFilter));
                return (read(ref again), allocated - budget.Spent);
            }

            (int[] Lengths, long Beyond, long Stored) LaidOut(int length)
            {
                var payload = Run(length);
                var priced = new ReadBudget(long.MaxValue, StructureId.CountingBloomFilter);
                var pricer = new PayloadReader(payload, priced);
                Assert.AreEqual((ulong)length, PersistenceFormat.PriceBucketsAs64(ref pricer), "the price read another count");

                var charged = new ReadBudget(long.MaxValue, StructureId.CountingBloomFilter);
                var once = new PayloadReader(payload, charged);
                PersistenceFormat.ReadBucketsAs64(ref once);
                Assert.AreEqual(priced.Charged, charged.Spent, $"a run of {length} was priced at one sum and charged another");

                var (buckets, beyond) = Read(payload, PersistenceFormat.ReadBucketsAs64);
                var (stored, storedBeyond) = Read(Table(length), PersistenceFormat.ReadBuckets64);
                for (ulong i = 0; i < (ulong)length; i++)
                {
                    Assert.AreEqual((uint)i + 1, buckets.Get(i), $"bucket {i} of a run of {length}");
                    Assert.AreEqual((uint)i + 1, stored.Get(i), $"bucket {i} of the stored table of {length}");
                }

                Console.WriteLine($"[RUN] {length}: beyond its charge {beyond}, and stored at version 14 {storedBeyond}");
                return (buckets.RawData.Select(array => array.Length).ToArray(), beyond, storedBeyond);
            }

            var (shorter, kept, split) = WithArrayBits(4, () => (LaidOut(Size - 1), LaidOut(Size), LaidOut(Size + 1)));
            CollectionAssert.AreEqual(new[] { Size - 1 }, shorter.Lengths, "a run shorter than an array was not kept whole");
            CollectionAssert.AreEqual(new[] { Size, 0 }, kept.Lengths, "a run as long as an array was not kept whole");
            CollectionAssert.AreEqual(new[] { Size, 1 }, split.Lengths, "a run a byte longer was not split at the array");

            // The stored tables of 15 and 17 hold no empty array, and those are the
            // reference: a stored table's reader charges an empty run as the array of
            // nothing it would be, and allocates none, so the stored table of 16 is charged
            // a little more than it allocates, where the run laid out as it holds no such array.
            Assert.AreEqual(shorter.Stored, split.Stored, "premise: the stored tables allocate beyond their charge alike");
            foreach (var (what, layout) in new[] { ("shorter", shorter), ("kept", kept), ("split", split) })
            {
                Assert.AreEqual(split.Stored, layout.Beyond,
                    $"the {what} run allocated other than it was charged, beyond what a stored table does");
            }
        }

        /// <summary>
        /// A run's price checks it as its reader does, before it prices the table its count
        /// implies. A version 8 counting payload whose bucket count is edited to the most
        /// four-bit buckets one array holds, against its run of 480 bytes, is refused for
        /// that by its reader. At arrays of 16 bytes that count implies an array of 2^28
        /// arrays, 2 GiB, which a price that did not check the run first would charge, and
        /// under a budget of a mebibyte the read would be refused for the budget instead.
        /// </summary>
        [TestMethod]
        public void TestARunWhoseCountDisagreesIsRefusedForThatUnderABudget()
        {
            // Version 8: u32 m, u32 k, u8 placement, u64 count, then the run's u32 count.
            const int RunCount = 17;
            var bytes = TestPersistenceFormatStability.ReadFixture("countingbloomfilter-v8-narrow.bin");
            Assert.AreEqual(959u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(14 + RunCount)), "premise: the run's count");
            var edited = TestPersistenceHostilePayloads.PokeUInt32(bytes, RunCount, Buckets.MaxCount(4));

            var refused = WithArrayBits(4, () => Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<CountingBloomFilter>(edited, new ReadOptions(1 << 20))));
            Assert.IsNotInstanceOfType<ReadBudgetExceededException>(refused.InnerException, "the run was refused for the budget");
            StringAssert.Contains(refused.Message, "bytes and 480 were stored");
        }

        private delegate Buckets64 TableReader(ref PayloadReader reader);

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
