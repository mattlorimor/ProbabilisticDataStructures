using System;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// A document's SimHash fingerprint: one fixed-width value whose bits stand for the
    /// document's weighted term vector.
    /// </summary>
    /// <remarks>
    /// Unlike a <see cref="MinHashSignature"/>, which is k values, this is one. Two
    /// documents are near-duplicates when their fingerprints differ in few bits, so the
    /// whole comparison is an exclusive-or and a bit count.
    /// </remarks>
    public sealed class SimHashSignature : IBinaryPersistable<SimHashSignature>, IPayloadBody
    {
        /// <summary>
        /// Wraps a fingerprint that has already been computed, or stored elsewhere.
        /// </summary>
        /// <param name="value">The fingerprint.</param>
        /// <remarks>
        /// A fingerprint is only comparable against one built the same way, so this is
        /// for a value that came from <see cref="SimHash.Signature"/> -- here, or in
        /// another process, or out of a column in a database.
        /// </remarks>
        public SimHashSignature(ulong value)
        {
            this.Value = value;
        }

        /// <summary>
        /// The fingerprint itself.
        /// </summary>
        public ulong Value { get; }

        /// <summary>
        /// Writes this signature to a stream, in the format documented in FORMAT.md.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream) => PersistenceFormat.Write(stream, this);

        EnvelopeHeader IPayloadBody.WritePayload(PayloadWriter payload)
        {
            payload.WriteUInt64(this.Value);

            // Named rather than left open, as with a MinHash signature and for the same
            // reason: a fingerprint only means anything against another built the same
            // way, and there is no overload that lets one be built differently.
            return new(StructureId.SimHashSignature, HashId.XxHash3_64);
        }

        /// <summary>
        /// Reads a signature written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The signature that was written.</returns>
        public static SimHashSignature ReadFrom(Stream stream)
        {
            return Read(stream, ReadOptions.Default);
        }

        /// <summary>
        /// Reads a structure written by <see cref="WriteTo"/>, allocating no more than the
        /// options' memory budget allows. <see cref="ReadFrom(Stream)"/> reads under
        /// <see cref="ReadOptions.Default"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="options">The most memory the read may allocate.</param>
        /// <returns>The structure that was written.</returns>
        /// <exception cref="InvalidDataException">
        /// Reading the payload would allocate more than the budget allows, among the other
        /// reasons a payload is refused. Its inner exception is then a
        /// <see cref="ReadBudgetExceededException"/>.
        /// </exception>
        public static SimHashSignature ReadFrom(Stream stream, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, options);
        }

        /// <summary>
        /// Reads a signature written by <see cref="WriteTo"/>. The hash argument is
        /// ignored: a fingerprint's hash is fixed by the format, since two are only
        /// comparable when both were built with the same one.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">Ignored.</param>
        /// <returns>The signature that was written.</returns>
        public static SimHashSignature ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            return Read(stream, ReadOptions.Default);
        }

        /// <summary>
        /// Reads a structure as
        /// <see cref="ReadFrom(Stream, Func{ReadOnlySpan{byte}, ulong})"/> does, allocating
        /// no more than the options' memory budget allows.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function, as for the overload without options.</param>
        /// <param name="options">The most memory the read may allocate.</param>
        /// <returns>The structure that was written.</returns>
        /// <exception cref="InvalidDataException">
        /// Reading the payload would allocate more than the budget allows, among the other
        /// reasons a payload is refused. Its inner exception is then a
        /// <see cref="ReadBudgetExceededException"/>.
        /// </exception>
        public static SimHashSignature ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, options);
        }

        private static SimHashSignature Read(Stream stream, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.SimHashSignature);
            return PersistenceFormat.Read(
                stream, StructureId.SimHashSignature, budget, Price, null, CheckHash,
                ReadPayload);
        }

        /// <summary>
        /// Reads the payload of the envelope <c>Read</c> opened, once the envelope has
        /// been checked. See <see cref="PayloadParse{T}"/>.
        /// </summary>
        private static SimHashSignature ReadPayload(
            ref PayloadReader reader, ushort version, HashId hashId,
            Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            // Charged before the field is read, as its price prices it without reading the
            // field, so that a payload refused for the field has been charged its price.
            reader.Budget.Charge(ObjectCost);
            var value = reader.ReadUInt64();
            reader.ExpectEnd();

            return new SimHashSignature(value);
        }

        /// <summary>
        /// Refuses a payload naming any hash but XxHash3, the one this structure
        /// builds with, whatever hash was supplied. See <see cref="HashCheck"/>.
        /// </summary>
        private static void CheckHash(HashId recorded, Func<ReadOnlySpan<byte>, ulong>? _)
        {
            if (recorded != HashId.XxHash3_64)
            {
                throw new InvalidDataException(
                    $"Signature was built with hash function {(ushort)recorded}, and this " +
                    "version builds them with XxHash3. Comparing fingerprints built with " +
                    "different hash functions gives a number that means nothing.");
            }
        }

        /// <summary>
        /// Prices what <see cref="Read"/> allocates beyond the envelope, which nothing a
        /// payload claims can size: the fingerprint is one fixed field, and the signature
        /// holds it. See <see cref="PayloadPrice"/>.
        /// </summary>
        internal static void Price(ref PayloadReader reader, ushort version)
        {
            reader.Budget.Price(ObjectCost);
        }

        /// <summary>What a read charges for the signature itself, measured at 24 bytes (#346).</summary>
        private static long ObjectCost => ReadBudget.Object(24);
    }
}
