using System;
using System.Collections.Generic;

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
    /// no lengths written down.
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

        /// <summary>
        /// The fragments standing for base-three digits nought, one and two.
        /// </summary>
        /// <remarks>
        /// The order is the paper's and is not the obvious one -- digit one is
        /// <c>10</c> and digit two is <c>01</c>. Any assignment of three patterns to
        /// three digits would encode and decode correctly, so this exists to match the
        /// published encoding rather than because the arithmetic requires it.
        /// </remarks>
        private static readonly byte[] DigitToFragment = { 0b00, 0b10, 0b01 };

        /// <summary>
        /// Which base-three digit a fragment stands for, or -1 for the delimiter.
        /// </summary>
        private static int FragmentToDigit(byte fragment) => fragment switch
        {
            0b00 => 0,
            0b10 => 1,
            0b01 => 2,
            _ => -1,
        };

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
        /// Encodes the overflowing part of a count as fragments, ending with the
        /// delimiter.
        /// </summary>
        /// <remarks>
        /// Digits run least significant first, so decoding can accumulate as it reads
        /// and a longer number simply has more fragments before its delimiter.
        /// </remarks>
        internal static List<byte> EncodeExtension(ulong overflow)
        {
            var fragments = new List<byte>();

            if (overflow == 0)
            {
                // Nought still takes one digit; an empty extension would be
                // indistinguishable from a counter that never overflowed.
                fragments.Add(DigitToFragment[0]);
            }
            else
            {
                var remaining = overflow;
                while (remaining > 0)
                {
                    fragments.Add(DigitToFragment[(int)(remaining % 3)]);
                    remaining /= 3;
                }
            }

            fragments.Add(Delimiter);
            return fragments;
        }

        /// <summary>
        /// Reads back the value an extension holds, starting at a fragment index.
        /// </summary>
        /// <param name="fragments">The pool the extension lives in.</param>
        /// <param name="start">Where this extension begins.</param>
        /// <param name="length">How many fragments it occupied, delimiter included.</param>
        internal static ulong DecodeExtension(
            IReadOnlyList<byte> fragments, int start, out int length)
        {
            var value = 0UL;
            var place = 1UL;
            var at = start;

            while (at < fragments.Count)
            {
                var digit = FragmentToDigit(fragments[at]);
                at++;

                if (digit < 0)
                {
                    length = at - start;
                    return value;
                }

                value += place * (ulong)digit;
                place *= 3;
            }

            // A pool that ends without a delimiter is a corrupt one; the caller is
            // told how far the read got rather than being given a value that looks
            // complete.
            length = at - start;
            throw new InvalidOperationException(
                $"An extension beginning at fragment {start} runs to the end of the " +
                "pool without a delimiter, so its length cannot be known.");
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

        /// <summary>
        /// Rebuilds a count from its stub and the value its extension held.
        /// </summary>
        internal static ulong Rebuild(ulong stub, ulong overflow, int stubBits) =>
            (overflow << stubBits) | stub;
    }
}
