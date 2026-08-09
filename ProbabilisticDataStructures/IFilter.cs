namespace ProbabilisticDataStructures
{
    /// <summary>
    /// The operations common to every filter in this library: testing for
    /// membership, adding data, and the combination of the two.
    /// </summary>
    /// <remarks>
    /// Implementations are <b>not thread-safe</b>, and no operation is synchronized --
    /// including <see cref="Test"/>, because implementations reuse a single
    /// <see cref="System.Security.Cryptography.HashAlgorithm"/> instance across calls
    /// and that type is not thread-safe either. Callers needing concurrent access must
    /// serialize every operation externally, or give each thread its own filter.
    /// </remarks>
    public interface IFilter
    {
        /// <summary>
        /// Will test for membership of the data and returns true if it is a member,
        /// false if not.
        /// </summary>
        /// <param name="data">The data to test for.</param>
        /// <returns>Whether or not the data is probably contained in the filter.</returns>
        bool Test(byte[] data);
        /// <summary>
        /// Add will add the data to the Bloom filter. It returns the filter to allow
        /// for chaining.
        /// </summary>
        /// <param name="data">The data to add.</param>
        /// <returns>The filter.</returns>
        IFilter Add(byte[] data);
        /// <summary>
        /// Is equivalent to calling Test followed by Add. It returns true if the data is
        /// a member, false if not.
        /// </summary>
        /// <param name="data">The data to test for and add if it doesn't exist.</param>
        /// <returns>Whether or not the data was probably contained in the filter.</returns>
        bool TestAndAdd(byte[] data);
    }
}
