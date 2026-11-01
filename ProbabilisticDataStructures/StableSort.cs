using System;
using System.Buffers;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Sorts keys with the values that ride along with them, leaving entries with equal
    /// keys in the order they were in.
    /// </summary>
    /// <remarks>
    /// <see cref="Array.Sort{TKey, TValue}(TKey[], TValue[], int, int)"/> is documented
    /// as unstable, and above the partition the runtime hands to insertion sort it is:
    /// partitioning can swap equal keys. For a <see cref="ThetaSketch"/> that cannot
    /// matter, because a repeated hash is a repeated hash whichever copy is kept. For a
    /// <see cref="TupleSketch"/> each copy carries a value, the values are folded in the
    /// order the sort leaves them, and floating-point addition is not associative, so
    /// the order is part of the answer.
    /// <para>
    /// This is a merge sort, which is stable because a merge can be told to take the
    /// earlier run on a tie. It sorts runs by insertion and merges them, copying only
    /// the earlier run of each merge aside, so it needs room for about half of what it
    /// sorts. That room comes from the stack when it is small and from the shared array
    /// pool when it is not, and either way it is not allocated afresh on every call.
    /// </para>
    /// </remarks>
    internal static class StableSort
    {
        /// <summary>
        /// The longest run sorted by insertion alone: past it a merge is cheaper than
        /// the shifting an insertion sort does.
        /// </summary>
        internal const int InsertionRun = 16;

        /// <summary>
        /// The most scratch entries, of each array, taken from the stack rather than the
        /// pool.
        /// </summary>
        internal const int StackScratch = 256;

        /// <summary>
        /// Sorts the first <paramref name="count"/> entries of <paramref name="keys"/>
        /// into ascending order, moving the entry of <paramref name="items"/> at each
        /// position with its key. Entries with equal keys stay in the order they were in.
        /// </summary>
        /// <param name="keys">The keys. Entries from <paramref name="count"/> on are not touched.</param>
        /// <param name="items">What travels with each key, in the same positions.</param>
        /// <param name="ordered">
        /// How many of the leading entries are already in ascending order, which is
        /// known to a caller that sorts a buffer it keeps appending to. They are merged
        /// with the rest rather than sorted again, and an equal key among the rest comes
        /// after theirs.
        /// </param>
        /// <param name="count">How many entries to sort.</param>
        internal static void Sort(ulong[] keys, double[] items, int ordered, int count)
        {
            // One entry is in order by itself, and treating it as a run to merge would
            // only copy it aside.
            if (ordered < 2)
            {
                ordered = 0;
            }

            var rest = count - ordered;
            if (rest <= 0)
            {
                return;
            }

            // The earlier run of a merge is what gets copied aside: the ordered prefix
            // for the last merge, and at most half of what is being sorted for the
            // others.
            var scratch = Math.Max(ordered, rest / 2);

            ulong[]? rentedKeys = null;
            double[]? rentedItems = null;

            try
            {
                Span<ulong> keyScratch = scratch <= StackScratch
                    ? stackalloc ulong[scratch]
                    : (rentedKeys = ArrayPool<ulong>.Shared.Rent(scratch));
                Span<double> itemScratch = scratch <= StackScratch
                    ? stackalloc double[scratch]
                    : (rentedItems = ArrayPool<double>.Shared.Rent(scratch));

                var allKeys = keys.AsSpan(0, count);
                var allItems = items.AsSpan(0, count);

                MergeSort(allKeys[ordered..], allItems[ordered..], keyScratch, itemScratch);

                if (ordered > 0)
                {
                    Merge(allKeys, allItems, ordered, keyScratch, itemScratch);
                }
            }
            finally
            {
                if (rentedKeys is not null)
                {
                    ArrayPool<ulong>.Shared.Return(rentedKeys);
                }

                if (rentedItems is not null)
                {
                    ArrayPool<double>.Shared.Return(rentedItems);
                }
            }
        }

        private static void MergeSort(
            Span<ulong> keys, Span<double> items, Span<ulong> keyScratch, Span<double> itemScratch)
        {
            if (keys.Length <= InsertionRun)
            {
                InsertionSort(keys, items);
                return;
            }

            var middle = keys.Length / 2;
            MergeSort(keys[..middle], items[..middle], keyScratch, itemScratch);
            MergeSort(keys[middle..], items[middle..], keyScratch, itemScratch);
            Merge(keys, items, middle, keyScratch, itemScratch);
        }

        /// <summary>
        /// Stable because an entry moves past another only when that one's key is
        /// strictly greater.
        /// </summary>
        private static void InsertionSort(Span<ulong> keys, Span<double> items)
        {
            for (var i = 1; i < keys.Length; i++)
            {
                var key = keys[i];
                var item = items[i];
                var j = i - 1;

                while (j >= 0 && keys[j] > key)
                {
                    keys[j + 1] = keys[j];
                    items[j + 1] = items[j];
                    j--;
                }

                keys[j + 1] = key;
                items[j + 1] = item;
            }
        }

        /// <summary>
        /// Merges the two ascending runs either side of <paramref name="middle"/>.
        /// </summary>
        /// <remarks>
        /// The earlier run is copied aside and merged back from there, which leaves the
        /// later run where it is: the position being written never passes the position
        /// being read in it. On a tie the earlier run's entry goes first, which is the
        /// whole of the stability.
        /// </remarks>
        private static void Merge(
            Span<ulong> keys, Span<double> items, int middle,
            Span<ulong> keyScratch, Span<double> itemScratch)
        {
            // Already in order, which a buffer made of a sorted prefix and a sorted
            // tail often is across the join. Equal keys either side of it are in order.
            if (keys[middle - 1] <= keys[middle])
            {
                return;
            }

            keys[..middle].CopyTo(keyScratch);
            items[..middle].CopyTo(itemScratch);

            int earlier = 0, later = middle, written = 0;

            while (earlier < middle && later < keys.Length)
            {
                if (keys[later] < keyScratch[earlier])
                {
                    keys[written] = keys[later];
                    items[written] = items[later];
                    later++;
                }
                else
                {
                    keys[written] = keyScratch[earlier];
                    items[written] = itemScratch[earlier];
                    earlier++;
                }

                written++;
            }

            // Whatever is left of the later run is already in its place.
            while (earlier < middle)
            {
                keys[written] = keyScratch[earlier];
                items[written] = itemScratch[earlier];
                earlier++;
                written++;
            }
        }
    }
}
