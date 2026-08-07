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
        /// MD5 is used here for bucket indexing, not for any security purpose. The
        /// choice is load-bearing for compatibility: the hash kernel is required to
        /// produce byte-for-byte identical results to the Go BoomFilters project
        /// (https://github.com/tylertreat/BoomFilters), and changing the algorithm
        /// would invalidate every persisted filter. Do not "upgrade" this to SHA-2.
        /// </remarks>
        /// <returns>The default hashing algorithm for the library</returns>
        internal static HashAlgorithm GetDefaultHashAlgorithm()
        {
            return MD5.Create();
        }
    }
}
