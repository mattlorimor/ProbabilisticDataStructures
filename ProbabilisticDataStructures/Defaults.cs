using System.Security.Cryptography;
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("TestProbabilisticDataStructures")]
[assembly: InternalsVisibleTo("ProbabilisticDataStructures.ThreadSafe")]

namespace ProbabilisticDataStructures
{
    public static class Defaults
    {
        public const double FILL_RATIO = 0.5;

        /// <summary>
        /// Returns the default hashing algorithm for the library.
        /// </summary>
        /// <returns>The default hashing algorithm for the library</returns>
        internal static HashAlgorithm GetDefaultHashAlgorithm()
        {
            return HashAlgorithm.Create("MD5");
        }
    }
}
