using System;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// A structure that can be written to a stream and read back.
    /// </summary>
    /// <typeparam name="TSelf">The implementing type.</typeparam>
    /// <remarks>
    /// The format is documented in FORMAT.md and is stable across versions of this
    /// library: a payload is either read as it was written or refused, never guessed at.
    /// <para>
    /// Reading is declared here as a static member so that callers can persist a
    /// structure without naming its type, which is what <see cref="Persistence"/> uses.
    /// </para>
    /// </remarks>
    public interface IBinaryPersistable<TSelf> where TSelf : IBinaryPersistable<TSelf>
    {
        /// <summary>
        /// Writes this structure to a stream.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        void WriteTo(Stream stream);

        /// <summary>
        /// Reads a structure written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The structure that was written.</returns>
        /// <exception cref="InvalidDataException">
        /// The payload is not one of these, is a later format version, is a different
        /// structure, is corrupted, or was written while a hash function set through
        /// SetHash was in use. The last case needs the overload that supplies one.
        /// </exception>
        static abstract TSelf ReadFrom(Stream stream);

        /// <summary>
        /// Reads a structure written by <see cref="WriteTo"/>, using the supplied hash
        /// function rather than the one named in the payload.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">
        /// The hash function the structure was using when it was written. Supplying a
        /// different one produces a structure that answers no to everything it holds.
        /// </param>
        /// <returns>The structure that was written.</returns>
        static abstract TSelf ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash);
    }

    /// <summary>
    /// Reads and writes persistable structures as byte arrays, for callers who have no
    /// stream to hand.
    /// </summary>
    public static class Persistence
    {
        /// <summary>
        /// Returns this structure as a byte array.
        /// </summary>
        /// <typeparam name="T">The structure's type.</typeparam>
        /// <param name="structure">The structure to write.</param>
        /// <returns>The bytes that <see cref="IBinaryPersistable{TSelf}.WriteTo"/> writes.</returns>
        public static byte[] ToByteArray<T>(this T structure) where T : IBinaryPersistable<T>
        {
            ArgumentNullException.ThrowIfNull(structure);

            using var stream = new MemoryStream();
            structure.WriteTo(stream);
            return stream.ToArray();
        }

        /// <summary>
        /// Reads a structure from bytes written by <see cref="ToByteArray{T}"/>.
        /// </summary>
        /// <typeparam name="T">The structure's type.</typeparam>
        /// <param name="data">The bytes to read.</param>
        /// <returns>The structure that was written.</returns>
        public static T FromByteArray<T>(byte[] data) where T : IBinaryPersistable<T>
        {
            ArgumentNullException.ThrowIfNull(data);

            using var stream = new MemoryStream(data, writable: false);
            return T.ReadFrom(stream);
        }

        /// <summary>
        /// Reads a structure from bytes written by <see cref="ToByteArray{T}"/>, using
        /// the supplied hash function rather than the one named in the payload.
        /// </summary>
        /// <typeparam name="T">The structure's type.</typeparam>
        /// <param name="data">The bytes to read.</param>
        /// <param name="hash">
        /// The hash function the structure was using when it was written.
        /// </param>
        /// <returns>The structure that was written.</returns>
        public static T FromByteArray<T>(byte[] data, Func<ReadOnlySpan<byte>, ulong> hash)
            where T : IBinaryPersistable<T>
        {
            ArgumentNullException.ThrowIfNull(data);
            ArgumentNullException.ThrowIfNull(hash);

            using var stream = new MemoryStream(data, writable: false);
            return T.ReadFrom(stream, hash);
        }
    }
}
