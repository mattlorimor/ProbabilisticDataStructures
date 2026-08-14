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
        /// Two structures can only be combined if they hash the same way.
        /// </summary>
        /// <remarks>
        /// Everything a structure holds sits where its hash function put it, so
        /// combining two that hash differently produces something that answers
        /// confidently about positions neither of them meant. Nothing about the result
        /// looks wrong afterwards, which is why this is checked rather than documented.
        /// <para>
        /// Delegates compare by method and target, so two conversions of the same
        /// method -- including the default, and including one passed to both
        /// constructors -- are equal. Two separately written lambdas with identical
        /// bodies are not, and are refused: the fix is to pass one hash function to
        /// both structures rather than to write it twice.
        /// </para>
        /// </remarks>
        internal static void SameHashFunction(
            Func<ReadOnlySpan<byte>, ulong> first,
            Func<ReadOnlySpan<byte>, ulong> second,
            string paramName)
        {
            if (!first.Equals(second))
            {
                throw new ArgumentException(
                    "The two structures use different hash functions. Everything each " +
                    "one holds sits where its own hash put it, so combining them would " +
                    "produce a structure that answers about positions neither of them " +
                    "meant. Build both with the same hash function -- passing the same " +
                    "one to each, rather than writing it out twice.", paramName);
            }
        }

        /// <summary>
        /// A structure's hash function can only be replaced while it holds nothing.
        /// </summary>
        /// <remarks>
        /// Everything a structure has stored was placed by the hash it was holding at
        /// the time, and replacing that hash does not move any of it. Every lookup then
        /// goes somewhere else, so the structure answers no to everything it holds while
        /// still reporting that it holds it. It does not look broken, it looks empty,
        /// which is the reason this is refused rather than documented.
        /// <para>
        /// Prefer passing the hash to the constructor. This exists for callers who
        /// cannot, and is safe only before anything has been added.
        /// </para>
        /// </remarks>
        internal static void HashMayBeReplaced(bool isEmpty, string structureName)
        {
            if (!isEmpty)
            {
                throw new InvalidOperationException(
                    $"The hash function cannot be replaced once a {structureName} holds " +
                    "anything. What it has stored was placed by the hash it had then, " +
                    "and replacing that hash does not move it: every lookup would go " +
                    "somewhere else and the structure would answer no to everything it " +
                    "holds. Pass the hash function to the constructor instead.");
            }
        }

        /// <summary>
        /// A count-min sketch's epsilon fixes the width of its matrix, as e / epsilon,
        /// so it has to be positive and not so small that the width is unbuildable.
        /// </summary>
        internal static void ValidSketchEpsilon(double epsilon, string paramName)
        {
            if (double.IsNaN(epsilon) || epsilon <= 0.0)
            {
                throw new ArgumentOutOfRangeException(paramName, epsilon,
                    "Epsilon must be greater than 0. It bounds the sketch's " +
                    "overestimate as a fraction of the total count, and a bound of " +
                    "zero or less asks for a matrix of infinite width.");
            }

            var width = Math.Ceiling(Math.E / epsilon);
            if (width > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(paramName, epsilon,
                    $"Epsilon of {epsilon} needs a matrix {width} columns wide, which " +
                    $"exceeds the {uint.MaxValue} addressable. Use a larger epsilon.");
            }
        }

        /// <summary>
        /// A count-min sketch's delta is the probability that its estimate exceeds the
        /// error epsilon allows, so it is a probability of failure and not of success.
        /// </summary>
        /// <remarks>
        /// The matrix depth is ln(1 / delta), which is only positive below one. At one
        /// and above it is zero or negative and the matrix has no rows at all, and a
        /// sketch with no rows does not fail loudly: Count takes a minimum over an
        /// empty set of rows and returns the initial value, so every element is
        /// reported as having been seen ulong.MaxValue times.
        /// </remarks>
        internal static void ValidSketchDelta(double delta, string paramName)
        {
            if (double.IsNaN(delta) || delta <= 0.0 || delta >= 1.0)
            {
                throw new ArgumentOutOfRangeException(paramName, delta,
                    "Delta must be greater than 0 and less than 1. It is the " +
                    "probability that an estimate exceeds the error epsilon allows, " +
                    "so it is a probability of failure: a smaller delta is a deeper, " +
                    "more reliable sketch.");
            }
        }

        /// <summary>
        /// A scalable filter tightens each new filter's false positive rate by this
        /// ratio, so it has to be a proper fraction.
        /// </summary>
        /// <remarks>
        /// The structure's guarantee is a compound false positive rate bounded by
        /// P0 / (1 - r), which is the sum of a geometric series and only converges for
        /// a ratio below one. At exactly one the rate never tightens and the compound
        /// rate grows without bound with every filter added; above one each filter is
        /// looser than the last. Either way the filter still works and simply stops
        /// honoring the rate that was asked for, which is worse than refusing.
        /// </remarks>
        internal static void ValidTighteningRatio(double r, string paramName)
        {
            if (double.IsNaN(r) || r <= 0.0 || r >= 1.0)
            {
                throw new ArgumentOutOfRangeException(paramName, r,
                    "The tightening ratio must be greater than 0 and less than 1. Each " +
                    "new filter's false positive rate is the previous one's scaled by " +
                    "this ratio, and the compound rate is only bounded when it shrinks.");
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
