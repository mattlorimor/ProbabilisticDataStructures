using System;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Argument checks shared by the filter constructors.
    /// <para>
    /// Without these, invalid arguments did not fail where they were passed. A false
    /// positive rate of zero surfaced as an OverflowException from a numeric
    /// conversion deep in the sizing math; a capacity of zero produced a filter that
    /// constructed successfully and then threw DivideByZeroException on first use;
    /// and a collision-bit count larger than the filter underflowed a uint, quietly
    /// allocating hundreds of megabytes. Each of those reported something true about
    /// the machinery and nothing about the mistake.
    /// </para>
    /// </summary>
    internal static class Guard
    {
        /// <summary>
        /// A false positive rate has to sit strictly between zero and one.
        /// </summary>
        /// <remarks>
        /// Zero is unachievable and makes the optimal size infinite. One asks for a
        /// filter with no discriminating power: the sizing math yields zero bits and
        /// zero hash functions, so the filter stores nothing and reports every
        /// element as present. Both ends are rejected rather than accepted and left
        /// to behave strangely later.
        /// </remarks>
        internal static void ValidFalsePositiveRate(double fpRate, string paramName)
        {
            if (double.IsNaN(fpRate) || fpRate <= 0.0 || fpRate >= 1.0)
            {
                throw new ArgumentOutOfRangeException(paramName, fpRate,
                    "False positive rate must be greater than 0 and less than 1. " +
                    "A rate of 0 cannot be achieved, and a rate of 1 describes a filter " +
                    "that reports every element as present.");
            }
        }

        /// <summary>
        /// A filter has to be sized for at least one item; sizing for none yields a
        /// filter of zero bits, which divides by zero the first time it is used.
        /// </summary>
        internal static void ValidItemCount(ulong n, string paramName)
        {
            if (n == 0)
            {
                throw new ArgumentOutOfRangeException(paramName, n,
                    "A filter must be sized for at least one item. Sizing for zero " +
                    "produces a filter with no buckets, which fails on first use.");
            }
        }

        /// <summary>
        /// The deletable filter partitions its bits into data and collision regions,
        /// so the collision count has to leave room for the data.
        /// </summary>
        internal static void ValidCollisionRegionCount(uint r, uint m, string paramName)
        {
            if (r == 0 || r >= m)
            {
                throw new ArgumentOutOfRangeException(paramName, r,
                    $"The number of collision-information bits must be greater than 0 and " +
                    $"less than the filter's {m} bits, since the two share the same space. " +
                    "A larger value underflows the data region.");
            }
        }
    }
}
