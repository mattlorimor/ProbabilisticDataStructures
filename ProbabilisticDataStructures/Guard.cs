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
        /// A sketch's epsilon fixes the width of its matrix, so it has to be positive.
        /// How wide that makes it is a different question, which depends on the sketch:
        /// see <see cref="SketchWidth"/>.
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
        }

        /// <summary>
        /// The most counters a row of a sketch can hold: a row is one array.
        /// </summary>
        internal static uint MaxSketchWidth => (uint)Array.MaxLength;

        /// <summary>
        /// The most rows a sketch sized by delta can have: ceil(ln(1 / delta)) for the
        /// smallest delta whose reciprocal is a finite double, which is 710.
        /// </summary>
        internal static uint MaxSketchRows => (uint)Math.Ceiling(Math.Log(double.MaxValue));

        /// <summary>
        /// The width a sketch's epsilon asks for, refused when no array holds it.
        /// </summary>
        /// <param name="width">
        /// What epsilon makes the width, as a double and before it is converted: e / epsilon
        /// for a count-min sketch, 1 / epsilon squared for a count sketch.
        /// </param>
        /// <param name="epsilon">The epsilon it came from.</param>
        /// <param name="paramName">The parameter it came from.</param>
        /// <returns>The width.</returns>
        /// <remarks>
        /// The width is worked out in a double and cast afterwards, and a cast of anything
        /// past what the target holds is not an error but a saturated number, which the
        /// allocation then failed on. An epsilon of infinity has the opposite fault: a
        /// width of none, which divides by zero on the first add.
        /// </remarks>
        internal static uint SketchWidth(double width, double epsilon, string paramName)
        {
            if (!(width >= 1))
            {
                throw new ArgumentOutOfRangeException(paramName, epsilon,
                    $"Epsilon of {epsilon} is so large that the matrix has no columns, and " +
                    "an item has nowhere to be counted.");
            }

            if (!(width <= MaxSketchWidth))
            {
                throw new ArgumentOutOfRangeException(paramName, epsilon,
                    $"Epsilon of {epsilon} needs rows {width:N0} counters wide, past the " +
                    $"{MaxSketchWidth:N0} that one array holds. Use a larger epsilon.");
            }

            return (uint)width;
        }

        /// <summary>
        /// The rows a sketch's delta asks for, refused when delta has no reciprocal.
        /// </summary>
        /// <returns>ceil(ln(1 / delta)).</returns>
        /// <remarks>
        /// Below about 5.56e-309 the reciprocal of a double is infinite, and so is the row
        /// count, which used to be converted to an integer anyway and failed in the
        /// allocation. Delta must already be valid (see <see cref="ValidSketchDelta"/>).
        /// </remarks>
        internal static uint SketchRows(double delta, string paramName)
        {
            var rows = Math.Ceiling(Math.Log(1 / delta));
            if (!(rows <= MaxSketchRows))
            {
                throw new ArgumentOutOfRangeException(paramName, delta,
                    $"A delta of {delta} needs ln(1 / delta) rows, and its reciprocal is " +
                    $"past the largest double. The deepest sketch is {MaxSketchRows} rows, " +
                    $"at a delta of about {1 / double.MaxValue:E2}.");
            }

            return (uint)rows;
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
        /// <summary>
        /// Rejects a HeavyKeeper decay base at or below one. The decay probability is
        /// b^-C: at b = 1 every mismatch decays with certainty regardless of the
        /// counter, so no bucket could hold anything against competition, and below
        /// one a bucket decays more readily the larger its count grows, which inverts
        /// the structure's premise.
        /// </summary>
        internal static void ValidDecayBase(double b, string paramName)
        {
            if (double.IsNaN(b) || double.IsInfinity(b) || b <= 1.0)
            {
                throw new ArgumentOutOfRangeException(paramName, b,
                    "The decay base must be a finite number greater than one; the " +
                    "decay probability is base^-count, and any other base decays " +
                    "every bucket or none.");
            }
        }

        /// <summary>
        /// Rejects an item weight that is not a positive finite number. A weight of
        /// zero would be an item that cannot be sampled and cannot affect an estimate,
        /// a negative weight would make inclusion probabilities negative, and NaN or
        /// infinity would poison every threshold computed after it.
        /// </summary>
        internal static void ValidWeight(double weight, string paramName)
        {
            if (double.IsNaN(weight) || double.IsInfinity(weight) || weight <= 0.0)
            {
                throw new ArgumentOutOfRangeException(paramName, weight,
                    "An item's weight must be a positive finite number; sampling " +
                    "probabilities are proportional to weight, and any other value " +
                    "describes an item that cannot be drawn.");
            }
        }

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
        /// A scalable filter builds each contained filter after the first for this many
        /// times the last one's items, so zero would size them for none.
        /// </summary>
        internal static void ValidGrowthFactor(uint growth, string paramName)
        {
            if (growth == 0)
            {
                throw new ArgumentOutOfRangeException(paramName, growth,
                    "The growth factor must be at least 1. Each contained filter after the " +
                    "first is built for this many times the items of the one before, so 0 " +
                    "would size them for none; 1 builds every one for the hint.");
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
        /// <summary>
        /// A relative accuracy sets the ratio between one bucket and the next, and only
        /// describes a sketch when it sits strictly between zero and one.
        /// </summary>
        /// <param name="accuracy">The accuracy to check.</param>
        /// <param name="paramName">The parameter it came from.</param>
        internal static void ValidRelativeAccuracy(double accuracy, string paramName)
        {
            if (!(accuracy > 0 && accuracy < 1))
            {
                throw new ArgumentOutOfRangeException(
                    paramName, accuracy,
                    "Relative accuracy must be greater than 0 and less than 1. At zero " +
                    "every value would need its own bucket, and at one or above a " +
                    "bucket would be allowed to report anything at all.");
            }
        }

    }
}
