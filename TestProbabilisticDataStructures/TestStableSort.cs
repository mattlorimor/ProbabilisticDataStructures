using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Tests for the sort a tuple sketch uses to gather a key's values before folding
    /// them: stable for equal keys, and the values stay beside their keys.
    /// </summary>
    /// <remarks>
    /// Each entry's item is its position before the sort, so one comparison against
    /// the oracle checks both properties. The oracle is LINQ's <c>OrderBy</c>, which is
    /// documented stable and shares no code with the sort under test.
    /// </remarks>
    [TestClass]
    public class TestStableSort
    {
        /// <summary>
        /// Lengths either side of each place the sort changes what it does: the longest
        /// run it sorts by insertion alone, the first length that merges, runs that
        /// merge into runs that merge, and the lengths whose scratch no longer fits on
        /// the stack and comes from the pool instead.
        /// </summary>
        public static IEnumerable<object[]> Lengths()
        {
            var run = StableSort.InsertionRun;
            var stack = StableSort.StackScratch;

            return new[]
            {
                0, 1, 2, 3,
                run - 1, run, run + 1,
                2 * run - 1, 2 * run, 2 * run + 1,
                4 * run + 3,
                2 * stack, 2 * stack + 1, 2 * stack + 2, 2 * stack + 3,
                8 * stack + 5,
            }.Select(n => new object[] { n });
        }

        /// <summary>
        /// How many distinct keys the entries draw on: one, so every pair ties; two, so
        /// ties are most of what there is; a handful; and as many as entries, which is
        /// mostly distinct and so mostly a test of the ordering.
        /// </summary>
        private static IEnumerable<int> KeyRanges(int length) =>
            new[] { 1, 2, 8, Math.Max(1, length) }.Distinct();

        private static (ulong[] Keys, double[] Items) Draw(int length, int range, int seed)
        {
            var random = new Random(seed);
            var keys = new ulong[length];
            var items = new double[length];
            for (var i = 0; i < length; i++)
            {
                keys[i] = (ulong)random.Next(range) * 1_000_003UL;
                items[i] = i;
            }
            return (keys, items);
        }

        private static (ulong[] Keys, double[] Items) Oracle(ulong[] keys, double[] items, int count)
        {
            var order = Enumerable.Range(0, count).OrderBy(i => keys[i]).ToArray();
            var sortedKeys = keys.ToArray();
            var sortedItems = items.ToArray();
            for (var i = 0; i < count; i++)
            {
                sortedKeys[i] = keys[order[i]];
                sortedItems[i] = items[order[i]];
            }
            return (sortedKeys, sortedItems);
        }

        private static void AssertSortedStably(
            ulong[] keys, double[] items, int ordered, int count, string what)
        {
            var (expectedKeys, expectedItems) = Oracle(keys, items, count);

            StableSort.Sort(keys, items, ordered, count);

            CollectionAssert.AreEqual(expectedKeys, keys, $"{what}: the keys are not sorted.");
            CollectionAssert.AreEqual(expectedItems, items,
                $"{what}: the items are not where a stable sort puts them, so either " +
                "equal keys were reordered or an item lost its key.");
        }

        /// <summary>
        /// Equal keys keep the order they arrived in, and every item stays with its key.
        /// </summary>
        [TestMethod]
        [DynamicData(nameof(Lengths))]
        public void TestEqualKeysKeepTheOrderTheyWereIn(int length)
        {
            foreach (var range in KeyRanges(length))
            {
                var (keys, items) = Draw(length, range, seed: 11);

                AssertSortedStably(keys, items, 0, length, $"{length} entries, {range} keys");
            }
        }

        /// <summary>
        /// A prefix already in order is merged with the rest rather than sorted again,
        /// and a key in both comes out with the prefix's entry first. That is the order
        /// a tuple sketch folds in: what the last compaction left, then what has arrived
        /// since.
        /// </summary>
        [TestMethod]
        [DynamicData(nameof(Lengths))]
        public void TestAnOrderedPrefixIsMergedAheadOfEqualKeysAfterIt(int rest)
        {
            var stack = StableSort.StackScratch;

            // Prefix lengths that need no merge, one entry which is a run in itself,
            // and ones either side of the scratch the stack can hold.
            foreach (var ordered in new[] { 0, 1, 2, 5, StableSort.InsertionRun + 1, stack, stack + 1, 3 * stack })
            {
                var length = ordered + rest;

                // The prefix is distinct and ascending, as a compaction leaves it; the
                // rest draws on the same keys, so most of it ties with something in it.
                var random = new Random(23);
                var keys = new ulong[length];
                var items = new double[length];
                for (var i = 0; i < ordered; i++)
                {
                    keys[i] = (ulong)i * 2;
                    items[i] = i;
                }
                for (var i = ordered; i < length; i++)
                {
                    keys[i] = (ulong)random.Next(Math.Max(1, ordered)) * 2;
                    items[i] = i;
                }

                AssertSortedStably(keys, items, ordered, length,
                    $"{ordered} ordered then {rest} more");
            }
        }

        /// <summary>
        /// Entries past the count are not the sort's to touch: a sketch's buffer is
        /// longer than what it holds.
        /// </summary>
        [TestMethod]
        [DynamicData(nameof(Lengths))]
        public void TestEntriesPastTheCountAreLeftAlone(int count)
        {
            const int Tail = 40;
            var (keys, items) = Draw(count + Tail, range: 4, seed: 5);

            // The oracle leaves everything past the count as it was, and the whole
            // array is compared.
            AssertSortedStably(keys, items, 0, count, $"{count} of {count + Tail} entries");
        }

        /// <summary>
        /// The ends of the range a sort could mishandle: already in order, in reverse,
        /// and every key the same. The first is where the merge takes its shortcut, the
        /// second is what it does the most work on, and the third is all ties.
        /// </summary>
        [TestMethod]
        [DynamicData(nameof(Lengths))]
        public void TestSortedReversedAndAllEqualInputs(int length)
        {
            var ascending = Enumerable.Range(0, length).Select(i => (ulong)i).ToArray();
            var descending = ascending.Reverse().ToArray();
            var equal = new ulong[length];
            Array.Fill(equal, 7UL);

            foreach (var (what, source) in new[]
            {
                ("ascending", ascending), ("descending", descending), ("all equal", equal),
            })
            {
                var keys = source.ToArray();
                var items = Enumerable.Range(0, length).Select(i => (double)i).ToArray();
                AssertSortedStably(keys, items, 0, length, $"{length} entries, {what}");
            }
        }
    }
}
