using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// What a structure allocates to be built, reset, merged or grown, where that used to be
    /// far more than the structure needs: the change half of
    /// <see href="https://github.com/mattlorimor/ProbabilisticDataStructures/issues/219">#219</see>.
    /// </summary>
    /// <remarks>
    /// Every figure is the least of several measurements with
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/>, as <c>TestReadBudget</c> measures a
    /// read: the counter is exact for what the thread allocates, but a collection that lands
    /// mid-measurement can add a retired allocation context's worth, and that noise only ever
    /// adds. None of these builds anything near the sizes the issue describes; each holds the
    /// allocation to what does not grow with the size, at a size where the old allocation is
    /// megabytes.
    /// </remarks>
    [TestClass]
    public class TestPeakMemory
    {
        /// <summary>
        /// The least of several measurements of what <paramref name="act"/> allocates on this
        /// thread. What it returns is kept alive until it is measured, so that a constructor's
        /// result is not a candidate for collection while it is being counted.
        /// </summary>
        private static long Allocated(Func<object> act, int repeats = 5)
        {
            var least = long.MaxValue;
            for (var i = 0; i < repeats; i++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var result = act();
                var after = GC.GetAllocatedBytesForCurrentThread();
                GC.KeepAlive(result);
                least = Math.Min(least, after - before);
            }

            return least;
        }

        private static byte[] Key(int i) => BitConverter.GetBytes(i);

        // ---- TopK and HeavyKeeper: the heap ------------------------------------------------

        /// <summary>
        /// The two sizes of k compared. The old heap, a list and a dictionary pre-sized to k,
        /// was some 44 to 50 bytes for each unit of k as the dictionary's prime fell, and the
        /// two differed by 49 MB; now neither allocates anything for k until elements arrive.
        /// </summary>
        private const uint SmallK = 10_000;
        private const uint LargeK = 1_000_000;

        /// <summary>
        /// How far apart the two may be. The structures are the same apart from k, so they
        /// allocate the same, to the byte; this is room for nothing more than an object's
        /// padding, and a ten-thousandth of what the old heap differed by.
        /// </summary>
        private const long HeapSlack = 4096;

        /// <summary>
        /// Holds each operation's allocation at the larger k to its allocation at the smaller,
        /// and reports every operation that differs rather than the first.
        /// </summary>
        private static void AssertNotSizedByK(
            string structure, (string Operation, long Small, long Large)[] measured)
        {
            var sized = measured
                .Where(m => Math.Abs(m.Large - m.Small) >= HeapSlack)
                .Select(m => $"{m.Operation}: {m.Large:N0} bytes at k = {LargeK:N0} and " +
                    $"{m.Small:N0} at k = {SmallK:N0}")
                .ToArray();

            Assert.IsEmpty(sized,
                $"a {structure}'s heap is sized by k rather than by what it holds, and differs " +
                $"by more than {HeapSlack:N0} bytes between the two: " + string.Join("; ", sized));
        }

        /// <summary>
        /// A top-k is built, reset and merged without allocating anything for k: its heap
        /// starts empty and grows as elements arrive, and k bounds it by comparison. The
        /// elements held are a handful, so a merge's rebuilt heap is sized by them.
        /// </summary>
        [TestMethod]
        public void TestATopKAllocatesNothingForKUntilElementsArrive()
        {
            var built = (Small: Allocated(() => new TopK(0.001, 0.01, SmallK)),
                Large: Allocated(() => new TopK(0.001, 0.01, LargeK)));

            var resetSmall = Filled(new TopK(0.001, 0.01, SmallK));
            var resetLarge = Filled(new TopK(0.001, 0.01, LargeK));
            var reset = (Small: Allocated(() => resetSmall.Reset()), Large: Allocated(() => resetLarge.Reset()));

            var (intoSmall, fromSmall) = (Filled(new TopK(0.001, 0.01, SmallK)), Filled(new TopK(0.001, 0.01, SmallK)));
            var (intoLarge, fromLarge) = (Filled(new TopK(0.001, 0.01, LargeK)), Filled(new TopK(0.001, 0.01, LargeK)));
            var merged = (Small: Allocated(() => intoSmall.Merge(fromSmall)), Large: Allocated(() => intoLarge.Merge(fromLarge)));

            // The merge kept what both held, so it did rebuild a heap.
            Assert.HasCount(20, intoLarge.Elements(), "premise: the merge holds every element either held");

            AssertNotSizedByK("top-k", new[]
            {
                ("built", built.Small, built.Large),
                ("reset", reset.Small, reset.Large),
                ("merged", merged.Small, merged.Large),
            });

            static TopK Filled(TopK topK)
            {
                for (var i = 0; i < 20; i++)
                {
                    topK.Add(Key(i));
                }

                return topK;
            }
        }

        /// <summary>
        /// A HeavyKeeper is built and reset without allocating anything for k, as a top-k is.
        /// </summary>
        [TestMethod]
        public void TestAHeavyKeeperAllocatesNothingForKUntilElementsArrive()
        {
            var built = (Small: Allocated(() => new HeavyKeeper(SmallK, 1024, seed: 1)),
                Large: Allocated(() => new HeavyKeeper(LargeK, 1024, seed: 1)));

            var resetSmall = Filled(new HeavyKeeper(SmallK, 1024, seed: 1));
            var resetLarge = Filled(new HeavyKeeper(LargeK, 1024, seed: 1));
            Assert.IsNotEmpty(resetLarge.Elements(), "premise: there is a heap to reset");
            var reset = (Small: Allocated(() => resetSmall.Reset()), Large: Allocated(() => resetLarge.Reset()));

            AssertNotSizedByK("HeavyKeeper", new[]
            {
                ("built", built.Small, built.Large),
                ("reset", reset.Small, reset.Large),
            });

            static HeavyKeeper Filled(HeavyKeeper keeper)
            {
                for (var i = 0; i < 20; i++)
                {
                    keeper.Add(Key(i));
                }

                return keeper;
            }
        }

        // ---- Buckets: Reset ----------------------------------------------------------------

        /// <summary>
        /// A bucket array of 16 MiB, so that a reset that allocated a second one would show
        /// as megabytes, far past the slack.
        /// </summary>
        private const uint ResetBuckets = 16 * 1024 * 1024;

        /// <summary>
        /// Resetting a bucket array clears it where it is. A reset that allocated the new
        /// array before the old one was released needed both at once, twice the filter, which
        /// at the largest 8-bit counting or stable filter is 4 GiB. Each measured reset clears
        /// buckets set just before it, from the first to the last, and the array is then
        /// asked to be every byte zero and the length it was.
        /// </summary>
        [TestMethod]
        public void TestResettingBucketsAllocatesNothing()
        {
            var buckets = new Buckets(ResetBuckets, 8);
            var length = buckets.RawData.Length;

            var allocated = Allocated(() =>
            {
                Dirty(buckets);
                return buckets.Reset();
            });

            Assert.AreEqual(length, buckets.RawData.Length, "the reset array is not the length it was");
            Assert.IsTrue(buckets.RawData.IndexOfAnyExcept((byte)0) < 0, "the reset left a bucket set");
            Assert.IsLessThan(1024L, allocated,
                $"resetting {length:N0} bytes of buckets allocated {allocated:N0} bytes: the " +
                "reset built a new array rather than clearing the one it had");

            // Not vacuous: the buckets each reset cleared were set.
            Dirty(buckets);
            Assert.AreEqual(255u, buckets.Get(0));
            Assert.AreEqual(255u, buckets.Get(ResetBuckets - 1));

            static void Dirty(Buckets buckets)
            {
                for (var i = 0u; i < ResetBuckets; i += 4099)
                {
                    buckets.Set(i, 255);
                }

                buckets.Set(ResetBuckets - 1, 255);
            }
        }

        // ---- CountMinSketch and TopK: Reset --------------------------------------------------

        /// <summary>
        /// A count-min sketch of two rows of about a million counters, 16 MiB, so that a reset
        /// that allocated a second matrix would show as megabytes, far past the slack.
        /// </summary>
        private const double ResetEpsilon = Math.E / (1 << 20);
        private const double ResetDelta = 0.2;

        /// <summary>
        /// Resetting a count-min sketch clears its rows where they are. A reset that allocated
        /// a new matrix while the old one was still held needed both at once, twice the
        /// sketch: 16 GiB more for each row at the widest row the constructor takes. Each
        /// measured reset clears counters set just before it, in the first row and the last,
        /// and the sketch is then asked to have every counter nought, the shape it had, and a
        /// total of nought.
        /// </summary>
        [TestMethod]
        public void TestResettingACountMinSketchAllocatesNothing()
        {
            var sketch = new CountMinSketch(ResetEpsilon, ResetDelta);
            Assert.AreEqual(2u, sketch.Depth, "premise: the sketch's rows");
            for (var i = 0; i < 100; i++)
            {
                sketch.Add(Key(i));
            }

            var matrix = (long)sketch.Width * sketch.Depth * sizeof(ulong);
            var allocated = Allocated(() =>
            {
                DirtySketch(sketch);
                return sketch.Reset();
            });

            AssertCleared(sketch);
            Assert.IsLessThan(1024L, allocated,
                $"resetting a sketch of {matrix:N0} bytes of counters allocated {allocated:N0} " +
                "bytes: the reset built a new matrix rather than clearing the one it had");

            // Not vacuous: the counters each reset cleared were set.
            DirtySketch(sketch);
            Assert.AreEqual(1UL, sketch.Matrix[0][0]);
            Assert.AreEqual(1UL, sketch.Matrix[sketch.Depth - 1][sketch.Width - 1]);
        }

        /// <summary>
        /// A top-k's reset resets its sketch, so it is held to the same: a reset of a top-k
        /// over the 16 MiB sketch above allocates its empty heap and nothing for the sketch.
        /// The heap starts empty and is not sized by k, which
        /// <see cref="TestATopKAllocatesNothingForKUntilElementsArrive"/> holds.
        /// </summary>
        [TestMethod]
        public void TestResettingATopKAllocatesNothingForItsSketch()
        {
            var topK = new TopK(ResetEpsilon, ResetDelta, 10);
            for (var i = 0; i < 20; i++)
            {
                topK.Add(Key(i));
            }

            Assert.IsNotEmpty(topK.Elements(), "premise: there is a heap to reset");
            var sketch = topK.Sketch;
            var matrix = (long)sketch.Width * sketch.Depth * sizeof(ulong);
            var allocated = Allocated(() =>
            {
                DirtySketch(topK.Sketch);
                return topK.Reset();
            });

            Assert.IsEmpty(topK.Elements(), "the reset kept an element");
            AssertCleared(topK.Sketch);
            Assert.IsLessThan(1024L, allocated,
                $"resetting a top-k over {matrix:N0} bytes of counters allocated {allocated:N0} " +
                "bytes: its sketch's reset built a new matrix rather than clearing the one it had");
        }

        /// <summary>Sets a counter at each end of a sketch, without allocating.</summary>
        private static void DirtySketch(CountMinSketch sketch)
        {
            sketch.Matrix[0][0] = 1;
            sketch.Matrix[sketch.Depth - 1][sketch.Width - 1] = 1;
        }

        private static void AssertCleared(CountMinSketch sketch)
        {
            Assert.AreEqual(0UL, sketch.TotalCount(), "the reset kept the total");
            Assert.HasCount((int)sketch.Depth, sketch.Matrix, "the reset sketch has another depth");
            foreach (var row in sketch.Matrix)
            {
                Assert.AreEqual((int)sketch.Width, row.Length, "a reset row is not the width it was");
                Assert.IsTrue(row.AsSpan().IndexOfAnyExcept(0UL) < 0, "the reset left a counter set");
            }
        }

        // ---- InfiniFilter: doubling ----------------------------------------------------------

        /// <summary>
        /// A doubling allocates the doubled table and next to nothing else. It used to list
        /// every entry of the old table first, 16 bytes an entry, which at the doubling into
        /// 2^31 slots was 12.9 GB beside the two tables; the old table is not changed while it
        /// is read, so its entries can be walked in place.
        /// </summary>
        /// <remarks>
        /// A table of 2^16 slots doubles at its 49,152nd entry, so a list of them is 786,432
        /// bytes. Each of several filters, built alike, is filled to the brink and given the
        /// add that doubles it, and the least that any of those adds allocated past the new
        /// table is held to the slack: the new table's object, the list of entries that went
        /// void (none do at a first doubling), and the walk.
        /// </remarks>
        [TestMethod]
        public void TestAnInfiniFilterDoublesWithoutListingItsEntries()
        {
            const int quotientBits = 16;
            const int entries = 3 << (quotientBits - 2);
            var keys = Enumerable.Range(0, entries + 1).Select(Key).ToArray();

            var least = long.MaxValue;
            for (var run = 0; run < 5; run++)
            {
                var filter = new InfiniFilter(initialCapacity: entries, fingerprintBits: 8);
                Assert.AreEqual(quotientBits, filter.Segments[0].QuotientBits, "premise: the table's size");
                for (var i = 0; i < entries; i++)
                {
                    filter.Add(keys[i]);
                }

                Assert.AreEqual(0u, filter.ExpansionCount(), "premise: the table is full and has not doubled");
                Assert.AreEqual((uint)entries, filter.Segments[0].Count);

                var before = GC.GetAllocatedBytesForCurrentThread();
                filter.Add(keys[entries]);
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.AreEqual(1u, filter.ExpansionCount(), "premise: that add doubled the table");
                Assert.AreEqual(quotientBits + 1, filter.Segments[0].QuotientBits);
                Assert.HasCount(1, filter.Segments, "premise: nothing went void");

                var table = (long)filter.Segments[0].Data.Length * sizeof(ulong);
                least = Math.Min(least, allocated - table);
            }

            Assert.IsLessThan(4096L, least,
                $"the add that doubled a table of {entries:N0} entries allocated {least:N0} " +
                "bytes past the doubled table: the entries were listed before they were moved");
        }

        /// <summary>
        /// A doubling that voids entries moves them down the chain without listing them
        /// first. It used to collect the void entries' quotients in a list, 4 bytes an entry
        /// with the list's growth, and nothing bounds how many there are: about a seventh of
        /// a table of 2-bit fingerprints at its doubling.
        /// </summary>
        /// <remarks>
        /// Two-bit fingerprints are the narrowest the constructor takes, and so where entries
        /// go void soonest. Grown from 16 slots, the filter's active table reaches 2^16
        /// slots with three tables behind it, and the add that doubles it voids 7,032 of its
        /// entries; every table in the chain doubles once with it and a fifth is made. Each
        /// of several filters, built alike, is given that add, and the least that any of
        /// them allocated past the tables it made is held to the slack: the tables' objects
        /// and the walks. Every table made is held after the add, since each one in the chain
        /// is asked to have doubled once and no more, so no table is missing from the sum.
        /// </remarks>
        [TestMethod]
        public void TestAnInfiniFilterDoublingMovesTheEntriesItVoidsWithoutListingThem()
        {
            const int quotientBits = 16;
            const int fingerprintBits = 2;
            var least = long.MaxValue;
            var voided = 0;

            for (var run = 0; run < 5; run++)
            {
                var filter = new InfiniFilter(initialCapacity: 16, fingerprintBits: fingerprintBits);
                var added = 0;
                while (filter.Segments[0].QuotientBits < quotientBits || filter.Segments[0].Load < 0.75)
                {
                    filter.Add(Key(added++));
                }

                var active = filter.Segments[0];
                Assert.AreEqual(quotientBits, active.QuotientBits, "premise: the table's size");
                voided = active.Entries().Count(entry => active.IsVoid(entry.Field));
                var before = filter.Segments.ToArray();
                var held = before.Sum(segment => (long)segment.Count);
                var last = Key(added);

                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                filter.Add(last);
                var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

                var after = filter.Segments.ToArray();
                Assert.AreEqual(quotientBits + 1, after[0].QuotientBits, "premise: that add doubled the table");
                Assert.HasCount(before.Length + 1, after, "premise: the voided entries made a table");
                for (var i = 0; i < before.Length; i++)
                {
                    Assert.AreEqual(before[i].QuotientBits + 1, after[i].QuotientBits,
                        $"premise: table {i} of the chain doubled once with the active one");
                }

                Assert.AreEqual(Math.Max(2, before[^1].QuotientBits - fingerprintBits), after[^1].QuotientBits,
                    "premise: the table made is the size it was made at");

                Assert.AreEqual(held + 1, after.Sum(segment => (long)segment.Count),
                    "the doubling lost or duplicated an entry");

                var tables = after.Sum(segment => (long)segment.Data.Length * sizeof(ulong));
                least = Math.Min(least, allocated - tables);
            }

            // Not vacuous: the doubling voided enough entries that a list of them, 4 bytes an
            // entry, is at least four times the slack.
            Assert.IsGreaterThan(4096, voided, "premise: the doubling voids entries");
            Assert.IsLessThan(4096L, least,
                $"the add that doubled a table of 2^{quotientBits} slots and voided {voided:N0} " +
                $"entries allocated {least:N0} bytes past the tables it made: the void entries " +
                "were listed before they were moved");
        }

        // ---- BloomierFilter: the key limit ---------------------------------------------------

        /// <summary>
        /// A Bloomier build refuses a set with more distinct keys than it is built for as soon
        /// as the pairs pass the limit, so what it holds to say so is the limit's worth of
        /// keys and no more. It used to collect every pair into a dictionary and then a list,
        /// about 44 bytes a key, before comparing the count with the limit: more than 42 GB
        /// for a set past the most a map of 8-bit values holds. The limit is lowered through
        /// the build the public one calls, and the pairs, a hundred times the limit, count
        /// how many were read.
        /// </summary>
        [TestMethod]
        public void TestABloomierBuildStopsReadingAtTheFirstKeyPastItsLimit()
        {
            const uint limit = 1_000;
            var pairs = new CountedPairs(100 * (int)limit);

            var allocated = Allocated(() =>
            {
                pairs.Read = 0;
                return Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => BloomierFilter.Build(pairs, 8, null, limit));
            });

            Assert.IsLessThanOrEqualTo(limit + 1, (uint)pairs.Read,
                $"the build read {pairs.Read:N0} pairs, and allocated {allocated:N0} bytes, to " +
                $"refuse more than {limit:N0} keys");
            // A dictionary of the limit's keys is 28 bytes an entry, in tables that double as
            // they fill, so under 128 bytes a key of the limit with the refusal; measured, 104.
            // The bound is twice that. Collecting all the pairs first allocated 10 MB, about
            // forty times the bound.
            Assert.IsLessThan(256L * limit, allocated,
                $"refusing more than {limit:N0} keys allocated {allocated:N0} bytes: every pair " +
                "was collected before the count was compared with the limit");
        }

        // ---- BloomierFilter: the pairs -------------------------------------------------------

        /// <summary>
        /// The keys a Bloomier build is measured at: a power of two, so that arrays which grow
        /// by doubling end exactly the size of what they hold and the measurement is of what
        /// the build keeps, not of where the count fell between two doublings.
        /// </summary>
        private const int BuildKeys = 1 << 16;

        /// <summary>
        /// A Bloomier build holds the distinct pairs once. It used to collect them into a
        /// dictionary and then copy them into a list, 28 bytes a key at the least for the one
        /// and 16 for the other, before the peel copied them again into the arrays it reads:
        /// about 42 GB for the two at the most keys a map of 8-bit values holds.
        /// </summary>
        /// <remarks>
        /// What is measured is everything a build of 2^16 distinct keys allocates, the least
        /// of five: the cells it keeps, the peel's arrays and the pairs. The pairs allocate
        /// nothing themselves (<see cref="CountedPairs"/>).
        /// </remarks>
        [TestMethod]
        public void TestABloomierBuildHoldsItsPairsOnce()
        {
            var pairs = new CountedPairs(BuildKeys);
            BloomierFilter map = null;
            var allocated = Allocated(() => map = BloomierFilter.Build(pairs, 8));
            var perKey = (double)allocated / BuildKeys;

            Assert.AreEqual((uint)BuildKeys, map.Count(), "premise: every key was built");
            for (var i = 0; i < BuildKeys; i += 997)
            {
                Assert.IsTrue(map.TryGetValue(Key(i), out var value), $"key {i} is missing");
                Assert.AreEqual((ulong)(i & 0xFF), value, $"key {i} came back with the wrong value");
            }

            // Not vacuous: the build allocated at least the cells it keeps.
            var cells = BloomierFilter.CellBytes(BuildKeys, 8);
            Assert.IsGreaterThan(cells, allocated, "premise: the measurement is of a build");
            // Measured with the pairs in a dictionary and then a list, 138.5 bytes a key. Held
            // once, in the two arrays the peel reads with an index of them beside, 92.3: the
            // arrays and the index as they double, the peel's arrays and the cells. The bound
            // is a quarter more than that.
            Assert.IsLessThan(115.0, perKey,
                $"a build of {BuildKeys:N0} keys allocated {allocated:N0} bytes, {perKey:F1} a " +
                "key: the pairs were held in more than one collection of every pair");
        }

        /// <summary>
        /// Distinct pairs, as many as asked for, counting how many have been read. Each key is
        /// written into one buffer, which a build may do because it hashes a key when it
        /// reads it and keeps only the hash, so that the pairs allocate nothing per pair and
        /// what is measured is the build's.
        /// </summary>
        private sealed class CountedPairs(int count) : IEnumerable<KeyValuePair<byte[], ulong>>
        {
            internal int Read { get; set; }

            public IEnumerator<KeyValuePair<byte[], ulong>> GetEnumerator()
            {
                var key = new byte[sizeof(int)];
                for (var i = 0; i < count; i++)
                {
                    BitConverter.TryWriteBytes(key, i);
                    this.Read++;
                    yield return new KeyValuePair<byte[], ulong>(key, (ulong)(i & 0xFF));
                }
            }

            IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
        }

        // ---- DpswSketch: the checkpoints -----------------------------------------------------

        /// <summary>
        /// A substream's checkpoints are built by a walk that keeps the offsets it chooses,
        /// so building them allocates the array they end as and nothing else. They were
        /// thinned from a list of every offset in the substream, which at the largest window
        /// the constructor takes was 8.6 GB of list for a few dozen checkpoints.
        /// </summary>
        /// <remarks>
        /// A substream of two million at factors from fine to coarse, held to its array to
        /// within its header and padding; and a sketch over a window of a billion, whose
        /// substreams are of 1,995,263 items, built in a few kilobytes where the list of every
        /// offset was 16 MB with its growth.
        /// </remarks>
        [TestMethod]
        public void TestADpswSketchBuildsItsCheckpointsWithoutAListOfEveryOffset()
        {
            const int substream = 2_000_000;
            var listed = new List<string>();
            foreach (var alpha in new[] { 0.01, 0.5, 0.9 })
            {
                var count = DpswSketch.CheckpointCount(substream, alpha);
                var array = 24L + ((4L * count + 7) & ~7L);
                var allocated = Allocated(() => DpswSketch.Checkpoints(substream, alpha));
                if (allocated - array >= 64)
                {
                    listed.Add($"a substream of {substream:N0} at alpha {alpha}: {allocated:N0} " +
                        $"bytes for {count:N0} checkpoints, an array of {array:N0}");
                }
            }

            Assert.AreEqual(1_995_263, DpswSketch.SubstreamSizeFor(1_000_000_000, 0.7), "premise: the substream");
            var window = Allocated(() => new DpswSketch(1_000_000_000, 1.0, seed: 1));
            if (window >= 64 * 1024)
            {
                listed.Add($"a sketch over a window of a billion: {window:N0} bytes to be built");
            }

            Assert.IsEmpty(listed,
                "checkpoints allocated more than their array, as if thinned from a list of " +
                "every offset in a substream: " + string.Join("; ", listed));
        }
    }
}
