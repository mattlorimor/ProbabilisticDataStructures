using System;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// An item held by <see cref="VarOpt"/>, with the adjusted weight that is its
    /// unbiased estimate.
    /// </summary>
    /// <remarks>
    /// Both members are read-only to callers for the same reason
    /// <see cref="Element"/>'s are: the data is the structure's own storage handed
    /// back as a view rather than a copy, and a caller writing through it would be
    /// rewriting the sample itself.
    /// </remarks>
    public class WeightedElement
    {
        /// <summary>
        /// The item's data.
        /// </summary>
        public ReadOnlyMemory<byte> Data { get; internal set; }

        /// <summary>
        /// The item's adjusted weight: its original weight if it survived on weight
        /// alone, or the shared threshold if it survived by luck. Summing these over
        /// any subset of the sample estimates that subset's true weight, without bias.
        /// </summary>
        public double Weight { get; internal set; }
    }
}
