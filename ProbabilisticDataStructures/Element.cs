using System;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// An element in a Top-K structure, pairing a value with the frequency it has
    /// been observed with.
    /// </summary>
    public class Element
    {
        /// <summary>
        /// The element's data.
        /// </summary>
        public byte[] Data { get; set; }
        /// <summary>
        /// The frequency the data has been observed with.
        /// </summary>
        public UInt64 Freq { get; set; }
    }
}
