using System;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("TestProbabilisticDataStructures")]

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Library-wide defaults shared by the filter implementations.
    /// </summary>
    public static class Defaults
    {
        /// <summary>
        /// The target fraction of set bits used when calculating optimal filter
        /// sizes. See <see cref="Utils.OptimalM(uint, double)"/>.
        /// </summary>
        public const double FILL_RATIO = 0.5;

        /// <summary>
        /// The default hash function: XxHash3, a non-cryptographic 64-bit hash.
        /// </summary>
        /// <remarks>
        /// Filters need a fast, well-distributed hash, not a cryptographic one.
        /// XxHash3 is roughly twenty times faster end-to-end than the MD5 this
        /// library used previously, and returns 64 bits directly rather than a
        /// digest buffer that has to be sliced apart.
        ///
        /// It is not resistant to chosen-input attacks. An adversary who controls
        /// the data being inserted can provoke collisions and inflate the observed
        /// false-positive rate. MD5 was no better in this respect, only slower;
        /// callers needing that property should supply a keyed hash such as
        /// SipHash through the relevant filter's SetHash.
        /// </remarks>
        internal static ulong DefaultHash(ReadOnlySpan<byte> data)
        {
            return XxHash3.HashToUInt64(data);
        }

        /// <summary>
        /// Returns the default hash function for the library.
        /// </summary>
        internal static Func<ReadOnlySpan<byte>, ulong> GetDefaultHashFunction()
        {
            return DefaultHash;
        }
    }
}
