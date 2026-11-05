using System;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// What the quotient filter and the Infini and Memento tables share about the three
    /// metadata bits of a slot.
    /// </summary>
    /// <remarks>
    /// Each keeps its own table, packed the same way: a slot is its metadata in the low
    /// three bits -- occupied, continuation, shifted, as Bender et al. define them in
    /// "Don't Thrash: How to Cache Your Hash on Flash" (2012), section 3 -- and its field
    /// above them. Each finds a quotient's run with the walk of the paper's Figure 3, and
    /// the walk ends only because those bits agree with one another.
    /// </remarks>
    internal static class QuotientTable
    {
        private const ulong OccupiedBit = 1;
        private const ulong ContinuationBit = 2;
        private const ulong ShiftedBit = 4;

        /// <summary>
        /// Refuses a table whose metadata is inconsistent, or whose entries are not the count
        /// its payload stores. Every reader in the family runs it on each table it restores,
        /// before any run in that table is walked.
        /// </summary>
        /// <param name="data">The table's words.</param>
        /// <param name="bitsPerSlot">The width of a slot, metadata included.</param>
        /// <param name="slots">The slot count.</param>
        /// <param name="count">The entries the payload says the table holds.</param>
        /// <param name="table">The table's place in its chain, or -1 for a filter's only table.</param>
        /// <remarks>
        /// One lap of the table, starting where a decoder may start: at a slot whose entry is
        /// not shifted, or at an empty one. On the way it keeps a count of the occupied
        /// quotients whose runs have not yet begun, and holds every slot to what that count
        /// says it must be.
        /// <list type="bullet">
        /// <item>An empty slot ends a cluster, and no run continues past one, so no occupied
        /// quotient may still be waiting at it.</item>
        /// <item>A run begins at each entry that is not a continuation, and belongs to the
        /// earliest occupied quotient still waiting. That is the slot's own quotient exactly
        /// when none was waiting before it, and an entry is shifted exactly when it is not in
        /// its own quotient's slot, so a run's first entry is shifted exactly when a quotient
        /// was waiting before it. An unshifted first entry then has the occupied bit as its only
        /// bit, so its own quotient is waiting for it, and a run always has an owner.</item>
        /// <item>A continuation follows an entry of the same run, and is never in its own
        /// quotient's slot, so it follows an entry and is shifted.</item>
        /// <item>Once round, no occupied quotient is still waiting.</item>
        /// </list>
        /// These are the rules <c>QuotientFamilyInvariants</c> in the tests holds every
        /// table to after every operation, where a queue of the waiting quotients decodes the
        /// table. A count is enough here: all that is asked of the earliest waiting quotient
        /// is whether it is the slot's own, and that is whether any was waiting before it.
        /// A table that passes has a run for every occupied quotient and an occupied quotient
        /// for every run, in the order the paper keeps them, so the run-start search's walks
        /// each end inside one lap.
        /// <para>
        /// An entry is a slot with any of the three bits set, so the entries are counted on
        /// the way too, and must be the stored count. A count below them would let a full
        /// table take another entry, whose insert would look for an empty slot for ever.
        /// </para>
        /// <para>
        /// It reads the words directly, allocates nothing, and reads each slot at most twice:
        /// once looking for where to start, which is the first slot unless that is shifted,
        /// and once on the lap.
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidDataException">The table is refused, naming the slot.</exception>
        internal static void CheckMetadata(ulong[] data, int bitsPerSlot, ulong slots, ulong count, int table)
        {
            var start = 0UL;
            while ((Metadata(data, bitsPerSlot, start) & ShiftedBit) != 0)
            {
                if (++start == slots)
                {
                    throw Inconsistent(table,
                        $"every one of its {slots} slots holds a shifted entry, so no cluster in it has a start");
                }
            }

            var waiting = 0UL;
            var entries = 0UL;
            var inRun = false;
            var slot = start;
            for (var seen = 0UL; seen < slots; seen++)
            {
                var bits = Metadata(data, bitsPerSlot, slot);
                var before = waiting;
                if ((bits & OccupiedBit) != 0)
                {
                    waiting++;
                }

                if (bits == 0)
                {
                    if (waiting != 0)
                    {
                        throw Inconsistent(table,
                            $"slot {slot} is empty, and {waiting} of the occupied quotients before it in its " +
                            "cluster are still waiting for a run");
                    }

                    inRun = false;
                }
                else if ((bits & ContinuationBit) == 0)
                {
                    var shifted = (bits & ShiftedBit) != 0;
                    if (shifted && before == 0)
                    {
                        throw Inconsistent(table,
                            $"slot {slot} begins a run and is marked shifted, and no occupied quotient before " +
                            "it is waiting for a run");
                    }

                    if (!shifted && before != 0)
                    {
                        throw Inconsistent(table,
                            $"slot {slot} begins a run in its own slot, and {before} of the occupied quotients " +
                            "before it are still waiting for a run");
                    }

                    waiting--;
                    entries++;
                    inRun = true;
                }
                else
                {
                    if (!inRun)
                    {
                        throw Inconsistent(table, $"slot {slot} continues a run, and the slot before it holds none");
                    }

                    if ((bits & ShiftedBit) == 0)
                    {
                        throw Inconsistent(table, $"slot {slot} continues a run and is not marked shifted");
                    }

                    entries++;
                }

                slot = slot + 1 == slots ? 0 : slot + 1;
            }

            if (waiting != 0)
            {
                throw Inconsistent(table, $"{waiting} of its occupied quotients own no run");
            }

            if (entries != count)
            {
                throw Inconsistent(table, $"it counts {count} entries and its slots hold {entries}");
            }
        }

        /// <summary>
        /// A slot's three metadata bits, read from the words as the tables pack them. The
        /// second word's part is shifted in two steps so that a slot starting at bit 0 of its
        /// word, which takes nothing from the next, is a shift by 64 and not by none; every
        /// table carries spare words, so the next word is always there.
        /// </summary>
        private static ulong Metadata(ulong[] data, int bitsPerSlot, ulong slot)
        {
            var bit = slot * (ulong)bitsPerSlot;
            var word = (int)(bit >> 6);
            var offset = (int)(bit & 63);
            return ((data[word] >> offset) | ((data[word + 1] << 1) << (63 - offset))) & 7;
        }

        private static InvalidDataException Inconsistent(int table, string what) => new(
            $"{(table < 0 ? "The table" : $"Table {table}")}'s metadata is inconsistent: {what}. A " +
            "filter's operations never leave a table so, and the payload is damaged or was not " +
            "written by this library.");

        /// <summary>
        /// What a run-start search throws when one of its walks has gone all the way round
        /// the table without finding what it walks to.
        /// </summary>
        /// <remarks>
        /// On a table whose metadata is consistent, asked for an occupied quotient, none
        /// of the walks comes back to where it began: the walk back stops at the start of
        /// the quotient's cluster, and the walks forward stop inside it. So a walk that
        /// does has met a table no operation leaves, or been asked for a quotient no run
        /// belongs to, and would otherwise circle the table for ever. A table read from a
        /// payload has passed <see cref="CheckMetadata"/>.
        /// </remarks>
        internal static InvalidOperationException Lapped(ulong quotient, ulong slots) => new(
            $"The run of quotient {quotient} was not found in one lap of the table's {slots} " +
            "slots. Either no run belongs to that quotient, or the table's metadata is " +
            "inconsistent, which no operation on a filter leaves it.");
    }
}
