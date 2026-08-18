using System;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// The variable-length counter encoding behind Sublime, from Eslami, Bercea, Pagh
    /// and Dayan, Sublime: Sublinear Error and Space for Unbounded Skewed Streams
    /// (SIGMOD 2026).
    /// </summary>
    /// <remarks>
    /// A Count-Min sketch gives every counter the same width, which has to be wide
    /// enough for the largest count it might ever hold. Real streams are skewed: most
    /// counters stay small, and the width spent on them is spent on leading zeros.
    /// <para>
    /// This encoding splits a counter in two. The low bits live in a fixed-width
    /// <em>stub</em>, which is all a small count ever needs. A count too large for its
    /// stub sets a bit in an overflow bitmap and stores its high bits separately, in a
    /// variable-length <em>extension</em>. Counters that stay small cost a stub;
    /// counters that grow pay for their growth and nothing more.
    /// </para>
    /// <para>
    /// An extension is a run of two-bit fragments holding the high part in base three,
    /// least significant digit first, terminated by a fragment of two set bits. Base
    /// three rather than four is what makes the terminator possible: three of the four
    /// bit patterns carry digits, and the fourth is free to mean "the number ends
    /// here". That is what lets extensions of different lengths sit side by side with
    /// no lengths written down. It is also what lets a whole word be searched for
    /// terminators at once, since two adjacent set bits occur nowhere else.
    /// </para>
    /// <para>
    /// Fragments are addressed by their bit position in a packed pool rather than held
    /// as a list. That is how <see cref="ValeCounterArray"/> stores them, and reading
    /// and writing them in place is what keeps a counter and its extension inside one
    /// cache line.
    /// </para>
    /// </remarks>
    internal static class ValeCounter
    {
        /// <summary>
        /// How many bits one fragment occupies.
        /// </summary>
        internal const int FragmentBits = 2;

        /// <summary>
        /// The fragment marking the end of an extension: the one bit pattern base
        /// three leaves spare.
        /// </summary>
        internal const byte Delimiter = 0b11;

        private const int WordBits = 64;
        private const ulong FragmentMask = 0b11;

        /// <summary>
        /// Which base-three digit a fragment stands for, or -1 for the delimiter.
        /// </summary>
        /// <remarks>
        /// A fragment holds its digit directly: nought, one and two are the two-bit
        /// values 0, 1 and 2, and 3 is left over to be the delimiter. Reading the paper
        /// as though its figures put the most significant bit first suggests instead
        /// that digit one is stored as <c>10</c> and digit two as <c>01</c>, which is a
        /// misreading: the figures run least significant bit first throughout. Section
        /// 5's worked example settles it -- a stub holding 21 is drawn as
        /// <c>111111 101010</c> and incrementing it gives <c>111111 011010</c>, which is
        /// 22 only if the leftmost bit drawn is the lowest one.
        /// </remarks>
        private static int FragmentToDigit(byte fragment) =>
            fragment == Delimiter ? -1 : fragment;

        /// <summary>
        /// The part of a count that fits in a stub of the given width.
        /// </summary>
        internal static ulong StubOf(ulong count, int stubBits) =>
            count & ((1UL << stubBits) - 1);

        /// <summary>
        /// The part of a count that does not fit in a stub, which is nought when the
        /// counter has not overflowed.
        /// </summary>
        internal static ulong OverflowOf(ulong count, int stubBits) =>
            count >> stubBits;

        /// <summary>
        /// Whether a count needs an extension at this stub width.
        /// </summary>
        internal static bool Overflows(ulong count, int stubBits) =>
            OverflowOf(count, stubBits) != 0;

        /// <summary>
        /// Rebuilds a count from its stub and the value its extension held.
        /// </summary>
        internal static ulong Rebuild(ulong stub, ulong overflow, int stubBits) =>
            (overflow << stubBits) | stub;

        /// <summary>
        /// Reads the fragment at a bit position in a pool.
        /// </summary>
        internal static byte FragmentAt(ReadOnlySpan<ulong> pool, int at) =>
            (byte)((pool[at / WordBits] >> (at % WordBits)) & FragmentMask);

        /// <summary>
        /// Writes the fragment at a bit position in a pool.
        /// </summary>
        internal static void SetFragment(Span<ulong> pool, int at, byte fragment)
        {
            var word = at / WordBits;
            var shift = at % WordBits;
            pool[word] = (pool[word] & ~(FragmentMask << shift))
                | ((ulong)fragment << shift);
        }

        /// <summary>
        /// Writes the overflowing part of a count into a pool as an extension, ending
        /// with the delimiter, and says how many bits it took.
        /// </summary>
        /// <remarks>
        /// Digits run least significant first, so decoding can accumulate as it reads
        /// and a longer number simply has more fragments before its delimiter. Nought
        /// still takes a digit; an empty extension would be indistinguishable from a
        /// counter that never overflowed.
        /// </remarks>
        internal static int WriteExtension(Span<ulong> pool, int at, ulong overflow)
        {
            var bits = 0;
            var remaining = overflow;

            do
            {
                SetFragment(pool, at + bits, (byte)(remaining % 3));
                remaining /= 3;
                bits += FragmentBits;
            }
            while (remaining > 0);

            SetFragment(pool, at + bits, Delimiter);
            return bits + FragmentBits;
        }

        /// <summary>
        /// Reads back the value an extension holds.
        /// </summary>
        /// <param name="pool">The pool the extension lives in.</param>
        /// <param name="at">Where this extension begins, in bits.</param>
        /// <param name="limit">The bit position the pool ends at.</param>
        /// <param name="bits">How many bits it occupied, delimiter included.</param>
        internal static ulong DecodeExtension(
            ReadOnlySpan<ulong> pool, int at, int limit, out int bits)
        {
            var value = 0UL;
            var place = 1UL;
            var b = at;

            while (b < limit)
            {
                var digit = FragmentToDigit(FragmentAt(pool, b));
                b += FragmentBits;

                if (digit < 0)
                {
                    bits = b - at;
                    return value;
                }

                value += place * (ulong)digit;
                place *= 3;
            }

            // A pool that ends without a delimiter is a corrupt one; the caller is
            // refused a value rather than given one that looks complete.
            bits = b - at;
            throw new InvalidOperationException(
                $"An extension beginning at bit {at} runs to the end of the pool " +
                "without a delimiter, so its length cannot be known.");
        }

        /// <summary>
        /// How many fragments a count's extension will occupy, delimiter included.
        /// </summary>
        internal static int ExtensionLength(ulong overflow)
        {
            var digits = 1;
            var remaining = overflow;
            while (remaining > 2)
            {
                remaining /= 3;
                digits++;
            }
            return digits + 1;
        }
    }
}
