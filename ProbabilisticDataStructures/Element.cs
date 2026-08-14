using System;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// An element held by <see cref="TopK"/>, with the frequency it was last seen at.
    /// </summary>
    /// <remarks>
    /// Both members are read-only to callers. <see cref="TopK.Elements"/> hands back the
    /// objects the structure is holding rather than copies, and when the data was a
    /// writable array a caller writing to what they were given corrupted the structure:
    /// the same array is the key the heap is indexed by, so changing its contents
    /// changed what it hashes to without the index being rebuilt, and the element became
    /// unreachable. The next arrival of the same data was then held a second time.
    /// </remarks>
    public class Element
    {
        /// <summary>
        /// The element's data.
        /// </summary>
        public ReadOnlyMemory<byte> Data { get; internal set; }

        /// <summary>
        /// How many times the element has been seen, as the sketch counts it.
        /// </summary>
        public UInt64 Freq { get; internal set; }
    }
}
