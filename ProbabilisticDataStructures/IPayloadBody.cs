namespace ProbabilisticDataStructures
{
    /// <summary>
    /// A structure whose payload is written by one body, which its <c>WriteTo</c> runs
    /// twice through <see cref="PersistenceFormat.Write(System.IO.Stream, IPayloadBody)"/>:
    /// once against a <see cref="PayloadWriter.Counter"/>, to size the payload by its own
    /// layout before anything is allocated for it, and once to write it.
    /// </summary>
    /// <remarks>
    /// The size is the layout itself rather than a formula kept beside it, so the two cannot
    /// drift: a payload past what an envelope holds is refused naming the size it would have
    /// been, whatever its layout depends on. The body writes the same fields in the same
    /// order whichever writer it is given. A run whose bytes are not already held as bytes
    /// is begun with <see cref="PayloadWriter.BeginRun"/> and filled only when that says to,
    /// so the count builds nothing; a run of an array the structure already holds is
    /// <see cref="PayloadWriter.WriteBytes"/>, which a counter counts without copying.
    /// </remarks>
    internal interface IPayloadBody
    {
        /// <summary>
        /// Writes this structure's payload, and returns what the envelope around it says.
        /// </summary>
        /// <param name="payload">The writer, which may be a counter.</param>
        /// <returns>The structure id, hash id and format version the payload is written as.</returns>
        EnvelopeHeader WritePayload(PayloadWriter payload);
    }

    /// <summary>
    /// What an envelope says of the payload it holds, besides its length.
    /// </summary>
    /// <param name="Structure">The structure the payload is.</param>
    /// <param name="Hash">The hash it was written with.</param>
    /// <param name="Version">The format version of its layout.</param>
    internal readonly record struct EnvelopeHeader(
        StructureId Structure, HashId Hash, ushort Version = PersistenceFormat.DefaultVersion);
}
