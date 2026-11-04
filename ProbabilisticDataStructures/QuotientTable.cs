using System;

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
        /// <summary>
        /// What a run-start search throws when one of its walks has gone all the way round
        /// the table without finding what it walks to.
        /// </summary>
        /// <remarks>
        /// On a table whose metadata is consistent, asked for an occupied quotient, none
        /// of the walks comes back to where it began: the walk back stops at the start of
        /// the quotient's cluster, and the walks forward stop inside it. So a walk that
        /// does has met a table no operation leaves, or been asked for a quotient no run
        /// belongs to, and would otherwise circle the table for ever.
        /// </remarks>
        internal static InvalidOperationException Lapped(uint quotient, uint slots) => new(
            $"The run of quotient {quotient} was not found in one lap of the table's {slots} " +
            "slots. Either no run belongs to that quotient, or the table's metadata is " +
            "inconsistent, which no operation on a filter leaves it.");
    }
}
