using System;
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
        /// The two sizes of k compared. At the larger the old heap, a list and a dictionary
        /// pre-sized to k, was about 44 bytes for each unit of k, so the two differed by about
        /// 44 MB; now neither allocates anything for k until elements arrive.
        /// </summary>
        private const uint SmallK = 10_000;
        private const uint LargeK = 1_000_000;

        /// <summary>
        /// How far apart the two may be. The structures are the same apart from k, so they
        /// allocate the same; this is room for nothing more than an object's padding, and is
        /// a thousandth of what the old heap differed by.
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
    }
}
