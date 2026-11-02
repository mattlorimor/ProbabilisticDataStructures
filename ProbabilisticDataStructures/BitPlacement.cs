using System;

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
    /// The scheme a payload was placed with is a meaning of its bits, not of its size,
    /// so it travels as the format version: version 8 says 64-bit, anything earlier
    /// 32-bit. A filter placed with 32-bit values goes on writing the version it always
    /// did, so a library from before version 8 reads every such filter it could build.
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
        /// at 0.1%, a fifth of what rounding <c>k</c> up already adds at 1%. Lowering the
        /// threshold would cost libraries from before version 8 the ability to read
        /// filters of sizes they have always built, for a difference no measurement here
        /// could see.
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
        /// The placement a payload of <paramref name="version"/> was written with.
        /// </summary>
        /// <remarks>
        /// From the version alone, never from the size the payload records: a version 1
        /// payload of any size was placed with 32-bit values, and reading it as its size
        /// would call for would look for every item where it was not put.
        /// </remarks>
        internal static Placement At(ushort version) =>
            version == PersistenceFormat.WidePlacementVersion ? Placement.Wide : Placement.Narrow;

        /// <summary>
        /// The version a filter placed with <paramref name="placement"/> writes, given
        /// the one its layout calls for otherwise.
        /// </summary>
        internal static ushort Version(Placement placement, ushort layoutVersion) =>
            placement == Placement.Wide ? PersistenceFormat.WidePlacementVersion : layoutVersion;

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
