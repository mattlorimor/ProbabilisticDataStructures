using System;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// How a filter in the Bloom family turns an item's hash into the positions it sets
    /// and tests.
    /// </summary>
    internal enum Placement : byte
    {
        /// <summary>
        /// The 32-bit scheme: the hash's low and high 32 bits, combined as
        /// <c>low + i * high</c> in 32-bit arithmetic and reduced modulo the number of
        /// positions. Every payload before format version 8 was placed this way, and a
        /// filter below <see cref="BitPlacement.WideFrom"/> positions still is.
        /// </summary>
        Narrow,

        /// <summary>
        /// The 64-bit scheme <see cref="BloomFilter64"/> uses: the hash, and the hash of
        /// its eight little-endian bytes, combined as <c>first + i * second</c> in 64-bit
        /// arithmetic and reduced modulo the number of positions.
        /// </summary>
        Wide,
    }

    /// <summary>
    /// Which placement a filter uses, and what says so in a payload.
    /// </summary>
    /// <remarks>
    /// The 32-bit scheme reduces a uniform 32-bit value modulo <c>m</c>. Unless <c>m</c>
    /// divides 2^32, that gives the first <c>2^32 mod m</c> positions
    /// <c>floor(2^32 / m) + 1</c> preimages and the rest one fewer, so those positions
    /// fill faster and a query lands on them as often as an item did. The false positive
    /// rate rises with the spread: a filter of three quarters of 2^32 bits sized for 1%
    /// measured 1.42%, and 0.17% at 0.1%. The excess is a sawtooth in <c>m</c>, zero at
    /// the powers of two and highest where <c>2^32 mod m</c> is half of <c>m</c>, and its
    /// peaks fall as <c>1 / (q (q + 1))</c> with <c>q = floor(2^32 / m)</c>. 64-bit values
    /// fold too, by a part in 2^32 at most, which no measurement can see.
    /// <para>
    /// The scheme a payload was placed with is a meaning of its bits, not of its size, so
    /// it travels with them: from format version 8, every save of these filters carries a
    /// placement byte, and a payload before version 8 was placed with 32-bit values. A
    /// payload from before version 8 saved again keeps its scheme in the byte, whatever
    /// its size.
    /// </para>
    /// </remarks>
    internal static class BitPlacement
    {
        /// <summary>
        /// The fewest positions at which a new filter places with 64-bit values.
        /// </summary>
        /// <remarks>
        /// Chosen from measurement, at the 1% rate the defect was reported at, as the
        /// power of two below which no departure from the promised rate could be measured.
        /// Just below 2^28, where the fold is worst for <c>floor(2^32 / m) = 16</c>, a
        /// 32-bit filter measured 1.0065% against 1.0041% for a
        /// <see cref="BloomFilter64"/> of the same size: twelve standard errors apart, over
        /// four filters of each and 4 billion queries. Just below 2^27, at the worst point
        /// for 32, the difference was 0.0006% with a standard error of 0.00025% over 8
        /// billion queries, which is not resolved, against the 0.0007% the fold predicts.
        /// The steps between the two peak between those heights, so a threshold at 2^27
        /// leaves every step that could be measured above it. A power of two also puts the
        /// change where the fold is zero: a filter just below 2^27 folds less than one at
        /// the worst point further down.
        /// <para>
        /// Below it the fold's excess is at most about 0.07% of the rate at 1% and 0.11%
        /// at 0.1%, a fifth of what rounding <c>k</c> up already adds at 1%, which is why
        /// the smaller filters keep the scheme they always had.
        /// </para>
        /// </remarks>
        internal const ulong WideFrom = 1UL << 27;

        /// <summary>
        /// The placement a new filter whose positions are reduced modulo
        /// <paramref name="positions"/> is built with.
        /// </summary>
        /// <remarks>
        /// The constructors' choice, reachable on its own so that the sizes at which it
        /// changes can be checked without allocating filters of those sizes.
        /// </remarks>
        internal static Placement For(ulong positions) =>
            positions >= WideFrom ? Placement.Wide : Placement.Narrow;

        /// <summary>
        /// The placement byte's value for 32-bit placement.
        /// </summary>
        private const byte NarrowByte = 0;

        /// <summary>
        /// The placement byte's value for 64-bit placement.
        /// </summary>
        private const byte WideByte = 1;

        /// <summary>
        /// Writes the placement byte a version 8 payload carries after the fields that
        /// size the filter.
        /// </summary>
        internal static void Write(PayloadWriter payload, Placement placement) =>
            payload.WriteByte(placement == Placement.Wide ? WideByte : NarrowByte);

        /// <summary>
        /// Reads how a payload of <paramref name="version"/> was placed: from its
        /// placement byte at version 8, and the 32-bit scheme before it, which has no byte.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// The byte names no scheme this library knows.
        /// </exception>
        /// <remarks>
        /// Never from the size the payload records: a version 1 payload of any size was
        /// placed with 32-bit values, and reading it as its size would call for would look
        /// for every item where it was not put.
        /// </remarks>
        internal static Placement Read(ref PayloadReader reader, ushort version)
        {
            if (version < PersistenceFormat.PlacementVersion)
            {
                return Placement.Narrow;
            }

            var value = reader.ReadByte();
            return value switch
            {
                NarrowByte => Placement.Narrow,
                WideByte => Placement.Wide,
                _ => throw new InvalidDataException(
                    $"Payload names placement {value}, which this library does not know: " +
                    $"{NarrowByte} is the 32-bit scheme and {WideByte} the 64-bit one. Reading " +
                    "it as either would look for every item where it may not have been put."),
            };
        }

        /// <summary>
        /// Reads a filter's item count: a u64 at version 8, a u32 before it.
        /// </summary>
        internal static ulong ReadCount(ref PayloadReader reader, ushort version) =>
            version >= PersistenceFormat.PlacementVersion ? reader.ReadUInt64() : reader.ReadUInt32();

        /// <summary>
        /// Refuses to combine two filters whose bits were placed by different schemes.
        /// </summary>
        /// <exception cref="ArgumentException">They were.</exception>
        /// <remarks>
        /// The same item sets different positions under each, so a union would hold
        /// neither filter's items where either looks for them: an old payload read back
        /// and a new filter of the same size are the case that reaches this.
        /// </remarks>
        internal static void Same(Placement mine, Placement theirs, string paramName)
        {
            if (mine != theirs)
            {
                throw new ArgumentException(
                    $"Cannot merge a filter whose bits were placed {Describe(theirs)} into one " +
                    $"placed {Describe(mine)}. The same item sets different bits in each, so " +
                    "their union would hold neither filter's items where it looks for them. " +
                    "Add the items to a new filter instead.",
                    paramName);
            }
        }

        private static string Describe(Placement placement) => placement == Placement.Wide
            ? "with 64-bit values, as a new filter of 2^27 positions or more is"
            : "with 32-bit values, as a smaller filter, or any written before format version 8, is";
    }

    /// <summary>
    /// The positions of one item in a filter, or a partition of one, of <c>m</c>
    /// positions, under one placement.
    /// </summary>
    /// <remarks>
    /// Every filter that places by <see cref="Placement"/> asks this for its positions
    /// rather than computing them itself, so that what a test of it shows is what the
    /// filters do.
    /// </remarks>
    internal readonly struct Positions
    {
        private readonly ulong first;
        private readonly ulong step;
        private readonly uint m;
        private readonly bool wide;

        private Positions(ulong first, ulong step, uint m, bool wide)
        {
            this.first = first;
            this.step = step;
            this.m = m;
            this.wide = wide;
        }

        /// <summary>
        /// The positions <paramref name="data"/> takes under
        /// <paramref name="placement"/>, among <paramref name="m"/>.
        /// </summary>
        internal static Positions Of(
            ReadOnlySpan<byte> data, Func<ReadOnlySpan<byte>, ulong> hash, Placement placement, uint m)
        {
            if (placement == Placement.Wide)
            {
                var wide = Utils.HashKernel128(data, hash);
                return new Positions(wide.LowerBaseHash, wide.UpperBaseHash, m, wide: true);
            }

            var narrow = Utils.HashKernel(data, hash);
            return new Positions(narrow.LowerBaseHash, narrow.UpperBaseHash, m, wide: false);
        }

        /// <summary>
        /// The <paramref name="i"/>th position, below <c>m</c>.
        /// </summary>
        internal uint this[uint i] => this.wide
            ? (uint)((this.first + this.step * i) % this.m)
            : ((uint)this.first + (uint)this.step * i) % this.m;
    }
}
