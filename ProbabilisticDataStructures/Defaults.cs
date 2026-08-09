using System.Security.Cryptography;
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
        /// Returns the default hashing algorithm for the library.
        /// </summary>
        /// <remarks>
        /// MD5 is used here for bucket indexing, not for any security purpose, so
        /// analyzer warnings about it being cryptographically broken do not apply.
        ///
        /// Changing the algorithm changes where every element lands, so it would
        /// invalidate any filter a caller has persisted and is a breaking change.
        /// It is not, however, required for compatibility with Go BoomFilters:
        /// that project hashes with FNV-1a, so filters from the two libraries were
        /// never interchangeable regardless of what is chosen here.
        /// </remarks>
        /// <returns>The default hashing algorithm for the library</returns>
        internal static HashAlgorithm GetDefaultHashAlgorithm()
        {
            return MD5.Create();
        }
    }
}
