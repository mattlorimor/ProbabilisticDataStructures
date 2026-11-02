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
        /// positions.
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
    /// Which placement a filter uses.
    /// </summary>
    internal static class BitPlacement
    {
        /// <summary>
        /// The placement a new filter whose positions are reduced modulo
        /// <paramref name="positions"/> is built with.
        /// </summary>
        /// <remarks>
        /// The constructors' choice, reachable on its own so that the sizes at which it
        /// changes can be checked without allocating filters of those sizes. Every size
        /// places narrow here, as every filter always has.
        /// </remarks>
        internal static Placement For(ulong positions) => Placement.Narrow;
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
