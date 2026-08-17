using System;
using System.Collections.Generic;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// HeavyKeeper finds the top-k most frequent elements of a stream, as described
    /// by Gong, Yang et al. in HeavyKeeper: An Accurate Algorithm for Finding Top-k
    /// Elephant Flows (USENIX ATC 2018).
    /// </summary>
    public class HeavyKeeper : IBinaryPersistable<HeavyKeeper>
    {
        /// <summary>
        /// Creates a new HeavyKeeper tracking the k most frequent elements.
        /// </summary>
        public HeavyKeeper(uint k, uint width, uint depth = 2, double decay = 1.08,
            ulong? seed = null, Func<ReadOnlySpan<byte>, ulong>? hash = null)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Adds the data to the structure.
        /// </summary>
        public HeavyKeeper Add(byte[] data)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc cref="Add(byte[])"/>
        public HeavyKeeper Add(ReadOnlySpan<byte> data)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Returns the estimated count of the data.
        /// </summary>
        public ulong Count(byte[] data)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc cref="Count(byte[])"/>
        public ulong Count(ReadOnlySpan<byte> data)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Returns the tracked top-k elements from lowest to highest frequency.
        /// </summary>
        public Element[] Elements()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Restores the structure to its original state.
        /// </summary>
        public HeavyKeeper Reset()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// The d (array, bucket, fingerprint) addresses the data maps to.
        /// </summary>
        internal IEnumerable<(uint Array, uint Bucket, ushort Fingerprint)> MappingOf(
            ReadOnlySpan<byte> data)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Writes this structure to a stream, in the format documented in FORMAT.md.
        /// </summary>
        public void WriteTo(Stream stream)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Reads a structure written by <see cref="WriteTo"/>.
        /// </summary>
        public static HeavyKeeper ReadFrom(Stream stream)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Reads a structure written by <see cref="WriteTo"/>, using the supplied hash
        /// function rather than the one named in the payload.
        /// </summary>
        public static HeavyKeeper ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            throw new NotImplementedException();
        }
    }
}
