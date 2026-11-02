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
        /// <exception cref="InvalidOperationException">
        /// The structure is too large to write: its payload would be longer than the
        /// largest one the format holds, 2,147,483,573 bytes. The structure is
        /// unchanged. This is the one thing a structure can be built but not written
        /// for.
        /// </exception>
        void WriteTo(Stream stream);

        /// <summary>
        /// Reads a structure written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The structure that was written.</returns>
        /// <exception cref="InvalidDataException">
        /// The payload is not one of these, is a format version this structure never
        /// defined (a later one included), is a different structure, is corrupted, or
        /// was written while a hash function set through SetHash was in use. The last
        /// case needs the overload that supplies one.
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

        /// <summary>
        /// Reads a structure written by <see cref="WriteTo"/>, allocating no more than the
        /// options' memory budget allows.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="options">The most memory the read may allocate.</param>
        /// <returns>The structure that was written.</returns>
        /// <exception cref="InvalidDataException">
        /// Reading the payload would allocate more than the budget allows, among the other
        /// reasons a payload is refused. Its inner exception is then a
        /// <see cref="ReadBudgetExceededException"/>.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// The type implements this interface without implementing a budgeted read.
        /// </exception>
        /// <remarks>
        /// Virtual, with a default, rather than abstract, so that a type outside this
        /// library implementing the interface goes on compiling and loading without it.
        /// The default refuses rather than reading without a budget: a caller who passed
        /// one asked for a bound, and quietly not applying it would be worse than saying
        /// so. Every structure in this library implements it.
        /// </remarks>
        static virtual TSelf ReadFrom(Stream stream, ReadOptions options) =>
            throw new NotSupportedException(
                $"{typeof(TSelf).Name} does not implement reading under a memory budget. " +
                "Read it with ReadFrom(Stream), which applies none.");

        /// <summary>
        /// Reads a structure written by <see cref="WriteTo"/>, using the supplied hash
        /// function rather than the one named in the payload, and allocating no more than
        /// the options' memory budget allows.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function the structure was using when it was written.</param>
        /// <param name="options">The most memory the read may allocate.</param>
        /// <returns>The structure that was written.</returns>
        /// <exception cref="InvalidDataException">
        /// Reading the payload would allocate more than the budget allows, among the other
        /// reasons a payload is refused. Its inner exception is then a
        /// <see cref="ReadBudgetExceededException"/>.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// The type implements this interface without implementing a budgeted read.
        /// </exception>
        static virtual TSelf ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options) =>
            throw new NotSupportedException(
                $"{typeof(TSelf).Name} does not implement reading under a memory budget. " +
                "Read it with ReadFrom(Stream, Func<ReadOnlySpan<byte>, ulong>), which " +
                "applies none.");
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

        /// <summary>
        /// Reads a structure from bytes written by <see cref="ToByteArray{T}"/>,
        /// allocating no more than the options' memory budget allows.
        /// </summary>
        /// <typeparam name="T">The structure's type.</typeparam>
        /// <param name="data">The bytes to read.</param>
        /// <param name="options">The most memory the read may allocate.</param>
        /// <returns>The structure that was written.</returns>
        /// <exception cref="InvalidDataException">
        /// Reading the payload would allocate more than the budget allows, among the other
        /// reasons a payload is refused. Its inner exception is then a
        /// <see cref="ReadBudgetExceededException"/>.
        /// </exception>
        public static T FromByteArray<T>(byte[] data, ReadOptions options)
            where T : IBinaryPersistable<T>
        {
            ArgumentNullException.ThrowIfNull(data);
            ArgumentNullException.ThrowIfNull(options);

            using var stream = new MemoryStream(data, writable: false);
            return T.ReadFrom(stream, options);
        }

        /// <summary>
        /// Reads a structure from bytes written by <see cref="ToByteArray{T}"/>, using
        /// the supplied hash function rather than the one named in the payload, and
        /// allocating no more than the options' memory budget allows.
        /// </summary>
        /// <typeparam name="T">The structure's type.</typeparam>
        /// <param name="data">The bytes to read.</param>
        /// <param name="hash">
        /// The hash function the structure was using when it was written.
        /// </param>
        /// <param name="options">The most memory the read may allocate.</param>
        /// <returns>The structure that was written.</returns>
        /// <exception cref="InvalidDataException">
        /// Reading the payload would allocate more than the budget allows, among the other
        /// reasons a payload is refused. Its inner exception is then a
        /// <see cref="ReadBudgetExceededException"/>.
        /// </exception>
        public static T FromByteArray<T>(
            byte[] data, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
            where T : IBinaryPersistable<T>
        {
            ArgumentNullException.ThrowIfNull(data);
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);

            using var stream = new MemoryStream(data, writable: false);
            return T.ReadFrom(stream, hash, options);
        }
    }
}
