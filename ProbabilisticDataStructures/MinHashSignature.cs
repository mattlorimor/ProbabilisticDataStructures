using System;
using System.Buffers.Binary;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// A fixed-size sketch of a bag of words, from which the resemblance of two bags can
    /// be estimated without either bag.
    /// </summary>
    /// <remarks>
    /// Position i holds the smallest value the i-th hash function takes over the bag.
    /// Two bags agree at a position exactly when the element that minimises that hash is
    /// in both, which happens with probability equal to their resemblance -- so the
    /// fraction of positions that agree estimates it. That is Broder's argument, and the
    /// error falls off as one over the square root of the signature's length.
    /// <para>
    /// The k hash functions are XxHash3 seeded with 0 through k-1. They are a convention
    /// rather than stored state, which is what lets two signatures be compared when they
    /// were computed by different processes, or by different versions of this library.
    /// Changing them would silently invalidate every stored signature, so they are fixed
    /// in the same sense the persistence format is.
    /// </para>
    /// </remarks>
    public sealed class MinHashSignature : IBinaryPersistable<MinHashSignature>
    {
        private readonly ulong[] values;

        internal MinHashSignature(ulong[] values)
        {
            this.values = values;
        }

        /// <summary>
        /// The number of hash functions behind this signature. Larger is more accurate
        /// and larger to store; the error is roughly one over its square root.
        /// </summary>
        public int Length => this.values.Length;

        /// <summary>
        /// The minimum values, one per hash function.
        /// </summary>
        public ReadOnlySpan<ulong> Values => this.values;

        /// <summary>
        /// Writes this signature to a stream, in the format documented in FORMAT.md.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = new PayloadWriter();
            payload.WriteUInt32((uint)this.values.Length);
            foreach (var value in this.values)
            {
                payload.WriteUInt64(value);
            }

            // The hash is not a caller's to choose here, so it is named rather than
            // left open: a signature is only comparable against another built the same
            // way, and there is no overload that lets one be built differently.
            PersistenceFormat.Write(
                stream,
                StructureId.MinHashSignature,
                HashId.XxHash3_64,
                payload.WrittenSpan);
        }

        /// <summary>
        /// Reads a signature written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The signature that was written.</returns>
        public static MinHashSignature ReadFrom(Stream stream)
        {
            return Read(stream);
        }

        /// <summary>
        /// Reads a signature written by <see cref="WriteTo"/>. The hash argument is
        /// ignored: a signature's hash functions are fixed by the format, since two
        /// signatures are only comparable when both were built with the same ones.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">Ignored.</param>
        /// <returns>The signature that was written.</returns>
        public static MinHashSignature ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            return Read(stream);
        }

        private static MinHashSignature Read(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(stream, StructureId.MinHashSignature, out var hashId);

            if (hashId != HashId.XxHash3_64)
            {
                throw new InvalidDataException(
                    $"Signature was built with hash function {(ushort)hashId}, and this " +
                    "version builds them with XxHash3. Comparing signatures built with " +
                    "different hash functions gives a number that means nothing.");
            }

            var reader = new PayloadReader(payload);

            var length = reader.ReadUInt32();
            if (length == 0 || length > PersistenceFormat.MaxNestedCount)
            {
                throw new InvalidDataException(
                    $"Signature claims {length} values, which is not a signature this " +
                    "library builds.");
            }

            var values = new ulong[length];
            for (uint i = 0; i < length; i++)
            {
                values[i] = reader.ReadUInt64();
            }

            reader.ExpectEnd();
            return new MinHashSignature(values);
        }
    }
}
