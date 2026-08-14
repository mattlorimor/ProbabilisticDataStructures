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

        /// <summary>
        /// Whether a hash function is the one this library installs, rather than one a
        /// caller supplied through SetHash.
        /// </summary>
        /// <remarks>
        /// Compares the method a delegate points at, not the delegate itself. Each call
        /// to <see cref="GetDefaultHashFunction"/> creates a fresh delegate over the
        /// same method, so reference equality would answer no to a filter that had
        /// never been given anything else.
        /// <para>
        /// This is what persistence uses to decide whether a structure's hash can be
        /// named in the payload it writes. Getting it wrong in the cautious direction
        /// costs a caller an explicit argument when reading; getting it wrong the other
        /// way would let a custom hash be recorded as the default and silently replaced.
        /// A delegate that wraps the default in something else is therefore not the
        /// default, which is the answer this gives.
        /// </para>
        /// </remarks>
        internal static bool IsDefaultHashFunction(Func<ReadOnlySpan<byte>, ulong> hash)
        {
            return hash is not null
                && hash.Target is null
                && hash.Method == DefaultHashMethod;
        }

        private static readonly System.Reflection.MethodInfo DefaultHashMethod =
            ((Func<ReadOnlySpan<byte>, ulong>)DefaultHash).Method;
    }
}
