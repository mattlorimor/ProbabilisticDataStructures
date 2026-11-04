using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Identifies the structure a persisted payload came from, so that reading a file
    /// into the wrong type fails rather than misinterpreting the bytes.
    /// </summary>
    internal enum StructureId : ushort
    {
        BloomFilter = 1,
        BloomFilter64 = 2,
        CountingBloomFilter = 3,
        DeletableBloomFilter = 4,
        PartitionedBloomFilter = 5,
        ScalableBloomFilter = 6,
        StableBloomFilter = 7,
        InverseBloomFilter = 8,
        CuckooBloomFilter = 9,
        CountMinSketch = 10,
        HyperLogLog = 11,
        TopK = 12,
        MinHashSignature = 13,
        BinaryFuseFilter = 14,
        DDSketch = 15,
        HyperLogLogPlus = 16,
        QuotientFilter = 17,
        ThetaSketch = 18,
        SimHashSignature = 19,
        CountSketch = 20,
        InvertibleBloomLookupTable = 21,
        BloomierFilter = 22,
        HeavyKeeper = 23,
        VarOpt = 24,
        UltraLogLog = 25,
        Grafite = 26,
        InfiniFilter = 27,
        MementoFilter = 28,
        SublimeCountMinSketch = 29,
        SetSketch = 30,
        TupleSketch = 31,
        PrivateCountMinSketch = 32,
        DpswSketch = 33,
    }

    /// <summary>
    /// Identifies the hash function a structure was using when it was written.
    /// </summary>
    /// <remarks>
    /// A structure's answers depend entirely on its hash function, and a delegate
    /// cannot be written to a file. Recording which one was in use is what stops a
    /// reader from installing a different one and returning confident nonsense: a
    /// filter read back under the wrong hash does not look broken, it looks empty.
    /// <para>
    /// The identifier names the algorithm rather than saying "the default", because
    /// the default is not fixed for all time -- this library's was MD5 until 3.0.0.
    /// A reader that does not recognise an identifier refuses the payload instead of
    /// guessing at it.
    /// </para>
    /// </remarks>
    internal enum HashId : ushort
    {
        /// <summary>
        /// A hash function supplied through SetHash. Nothing about it can be written
        /// down, so reading requires the caller to supply it again.
        /// </summary>
        Custom = 0,

        /// <summary>
        /// The 64-bit XxHash3 that <see cref="Defaults"/> installs, current since 3.0.0.
        /// </summary>
        XxHash3_64 = 1,

        /// <summary>
        /// The structure does not hash anything, so there is no hash to record and none
        /// to supply on reading.
        /// </summary>
        /// <remarks>
        /// This is not "the hash is unknown" -- that is <see cref="Custom"/>. It is a
        /// positive statement that the structure takes values rather than bytes, which
        /// <see cref="DDSketch"/> is the first here to do. A reader that is handed a
        /// hash for one of these refuses it rather than ignoring it, because a caller
        /// supplying one has misunderstood something about what they are reading.
        /// </remarks>
        None = 2,
    }

    /// <summary>
    /// Prices the read of a structure's payload, written at <paramref name="version"/>:
    /// walks its fields in the order the structure's reader reads them, and adds to the
    /// reader's budget, through <see cref="ReadBudget.Price(long)"/>, everything the
    /// reader charges for, without allocating any of it.
    /// </summary>
    /// <remarks>
    /// A price reads a field the reader reads, makes the checks the reader makes before it
    /// allocates for that field, by calling the code the reader calls, and prices what the
    /// reader charges, through the helpers the reader charges through. Where the reader
    /// would refuse the payload a price throws too, and what it priced up to there stands:
    /// the reader then refuses the payload with its own explanation, or the budget refuses
    /// what came before, as it did when every charge was made as the read went.
    /// <para>
    /// The reader may be over a held payload or over one read from its stream; a price
    /// reads fields and skips runs, which both allow, and never copies one.
    /// </para>
    /// </remarks>
    internal delegate void PayloadPrice(ref PayloadReader reader, ushort version);

    /// <summary>
    /// Refuses the hash a payload's header names, <paramref name="recorded"/>, if its
    /// reader cannot read it with <paramref name="supplied"/>, the hash the caller gave
    /// it or null, and returns otherwise.
    /// </summary>
    /// <remarks>
    /// Which hash a payload names is in its header, and which one it is read with is the
    /// caller's to say, so nothing past the header bears on it. The envelope checks it
    /// before the read is priced, so that a payload which can never be read is refused
    /// for that under any budget rather than priced, and held, for a read that cannot
    /// happen. Every reader passes one: most <see cref="PersistenceFormat.CheckResolvable"/>,
    /// and a structure with its own rule about hashes, that rule. A static method, so
    /// that passing it allocates nothing.
    /// </remarks>
    internal delegate void HashCheck(HashId recorded, Func<ReadOnlySpan<byte>, ulong>? supplied);

    /// <summary>
    /// Reads a structure from its payload, once
    /// <see cref="PersistenceFormat.Read{T}(Stream, StructureId, ReadBudget, PayloadPrice, Func{ReadOnlySpan{byte}, ulong}, HashCheck, PayloadParse{T})"/>
    /// has opened the envelope around it: the half of a structure's reader that comes
    /// after its envelope.
    /// </summary>
    /// <param name="reader">The payload, from its first byte, drawing on the read's budget.</param>
    /// <param name="version">The format version the envelope names.</param>
    /// <param name="hashId">The hash the envelope names.</param>
    /// <param name="hash">The hash the caller supplied, or null.</param>
    /// <remarks>
    /// The envelope runs it, rather than handing the payload back, so that what is done
    /// after the structure has read its payload is the envelope's to do for every
    /// structure alike. A static method, so that passing it allocates nothing.
    /// </remarks>
    internal delegate T PayloadParse<T>(
        ref PayloadReader reader, ushort version, HashId hashId, Func<ReadOnlySpan<byte>, ulong>? hash);

    /// <summary>
    /// The envelope every persisted structure is wrapped in.
    /// </summary>
    /// <remarks>
    /// The layout is documented in FORMAT.md and is stable: a payload written by any
    /// version of this library can be read by any later one, or is refused outright.
    /// <code>
    ///   offset  size  field
    ///   0       4     magic, "PDS\0"
    ///   4       2     format version
    ///   6       2     structure id
    ///   8       2     hash id
    ///   10      4     payload length
    ///   14      n     payload
    ///   14+n    4     CRC-32 over bytes 4 through 14+n
    /// </code>
    /// The checksum covers the header as well as the payload, so a corrupted length or
    /// structure id is caught by the same check rather than being acted on first.
    /// <para>
    /// A payload longer than that envelope holds is in the large form, marked by
    /// <see cref="LargeFormFlag"/> in the version field:
    /// <code>
    ///   offset  size  field
    ///   0       4     magic, "PDS\0"
    ///   4       2     format version | 0x8000
    ///   6       2     structure id
    ///   8       2     hash id
    ///   10      8     payload length
    ///   18      4     CRC-32 over bytes 4 through 17
    ///   22      n     payload
    ///   22+n    4     CRC-32 over bytes 4 through 17, then 22 through 21+n
    /// </code>
    /// </para>
    /// </remarks>
    internal static class PersistenceFormat
    {
        /// <summary>
        /// Marks the start of a payload. Chosen to be recognisable in a hex dump and
        /// to fail fast on text that was never one of these.
        /// </summary>
        internal static ReadOnlySpan<byte> Magic => "PDS\0"u8;

        /// <summary>
        /// The version a payload is written at unless its structure asks for another.
        /// </summary>
        /// <remarks>
        /// The version travels in each payload rather than being a property of the
        /// library, so a structure whose layout changes bumps only its own and the rest
        /// go on writing version 1, readable by 3.x onwards. Raising all of them
        /// together would make every payload unreadable to older libraries in order to
        /// record a change those payloads did not have.
        /// <para>
        /// Which structures write which version is not stated here on purpose. A count
        /// in a comment is a roster maintained by hand, and this one was wrong for
        /// several releases before anyone read it closely. The versions that exist are
        /// the named constants below, and each is used by the structures that reference
        /// it -- which the compiler keeps true and a comment cannot. The table of
        /// readable versions below is built from them, one entry per structure.
        /// </para>
        /// </remarks>
        internal const ushort DefaultVersion = 1;

        /// <summary>
        /// The version at which <see cref="StableBloomFilter"/> and
        /// <see cref="CuckooBloomFilter"/> began storing their random generator's state,
        /// so that a restored filter resumes its draw sequence rather than restarting it.
        /// </summary>
        internal const ushort RandomStateVersion = 2;

        /// <summary>
        /// The version at which <see cref="StableBloomFilter"/> also stores where its
        /// random generator began, so that <see cref="StableBloomFilter.Reset"/> can
        /// return it there after a save and a load. Earlier payloads hold only the
        /// position the generator had reached, which cannot say where it started.
        /// </summary>
        internal const ushort StableOriginalStateVersion = 5;

        /// <summary>
        /// The version at which <see cref="SetSketch"/> carries the construction it was
        /// built with. Written only by a sketch using the second construction: the
        /// first writes the layout it always wrote, so payloads from before the choice
        /// existed stay readable by libraries from before it existed too.
        /// </summary>
        internal const ushort SetSketchVariantVersion = 4;

        /// <summary>
        /// The version at which a cuckoo filter packs its fingerprints at their exact
        /// bit width rather than rounding each up to whole bytes. Version 2 payloads
        /// record a width in bytes and restore at eight times that many bits, which is
        /// the same filter they always were.
        /// </summary>
        internal const ushort CuckooPackedVersion = 3;

        /// <summary>
        /// The version at which <see cref="CuckooBloomFilter"/> also stores where its
        /// random generator began, so that <see cref="CuckooBloomFilter.Reset"/> can
        /// return it there after a save and a load. Earlier payloads hold only the
        /// position the generator had reached, which cannot say where it started.
        /// </summary>
        internal const ushort CuckooOriginalStateVersion = 6;

        /// <summary>
        /// The version at which <see cref="ScalableBloomFilter"/> stores its growth factor,
        /// so that a restored filter sizes the filters it adds next as it would have. Each
        /// contained filter already records its own size; what an earlier payload cannot
        /// say is how large the next one should be. Written only by a filter that grows:
        /// one that adds every filter at its hint writes the layout it always wrote.
        /// </summary>
        internal const ushort ScalableGrowthVersion = 7;

        /// <summary>
        /// The version at which the five 32-bit Bloom filters record how their bits were
        /// placed, in a byte of its own, and their item count, where they have one, in
        /// 64 bits. The layout is the one each wrote before with those two changes; every
        /// save of these filters is written at it, whatever its size or placement.
        /// </summary>
        /// <remarks>
        /// The placement could not stay implied by the version once every save writes the
        /// same one, and it is not implied by the size: a payload written before this
        /// version was placed with 32-bit values whatever its size, and saved again it
        /// must still say so. See <see cref="BitPlacement"/>.
        /// </remarks>
        internal const ushort PlacementVersion = 8;

        /// <summary>
        /// The version at which <see cref="CountMinSketch"/>, <see cref="PrivateCountMinSketch"/>,
        /// <see cref="HeavyKeeper"/> and <see cref="InverseBloomFilter"/> record how they placed
        /// items among their columns, buckets or slots, in a byte of their own after the
        /// fields that size them, and <see cref="DpswSketch"/>, whose private sketches carry
        /// that byte in the bodies it holds inline. The layout is the one each wrote at
        /// version 1 with that byte added; every save of these is written at it, whatever its
        /// width or placement.
        /// </summary>
        /// <remarks>
        /// The same change <see cref="PlacementVersion"/> made for the Bloom filters, for the
        /// same reason, made later and so given the next number: a version names one change
        /// to the structures it names. A <see cref="TopK"/> holds its sketch in an envelope of
        /// its own, which carries this version, so the top-k's own layout is unchanged. See
        /// <see cref="BitPlacement"/>.
        /// </remarks>
        internal const ushort ColumnPlacementVersion = 9;

        /// <summary>
        /// The format versions each structure has defined, which are exactly the ones its
        /// reader reads. This is the one place that says so.
        /// </summary>
        /// <remarks>
        /// A version is a property of a structure's layout, not of the library, so a
        /// structure's reader reads the versions that structure has written and no
        /// others. Accepting every version up to the highest anywhere would read a
        /// version some other structure defined as though it were this one's newest
        /// layout;
        /// <see cref="Read(Stream, byte[], StructureId, out HashId, out ushort, ReadBudget, PayloadPrice, Func{ReadOnlySpan{byte}, ulong}, HashCheck)"/> refuses
        /// it instead, and says which versions it does read.
        /// <para>
        /// Every structure is listed, including those that have only ever written the
        /// first version, so that a structure added without an entry fails loudly rather
        /// than inheriting one. Adding a version to a structure is one new constant above
        /// and one more element in its entry here; <see cref="MaxSupportedVersion"/>
        /// follows from the table. FORMAT.md's version table is held to this one.
        /// </para>
        /// </remarks>
        private static readonly Dictionary<StructureId, ushort[]> Readable = new()
        {
            [StructureId.BloomFilter] = [DefaultVersion, PlacementVersion],
            [StructureId.BloomFilter64] = [DefaultVersion],
            [StructureId.CountingBloomFilter] = [DefaultVersion, PlacementVersion],
            [StructureId.DeletableBloomFilter] = [DefaultVersion, PlacementVersion],
            [StructureId.PartitionedBloomFilter] = [DefaultVersion, PlacementVersion],
            [StructureId.ScalableBloomFilter] = [DefaultVersion, ScalableGrowthVersion],
            [StructureId.StableBloomFilter] =
                [DefaultVersion, RandomStateVersion, StableOriginalStateVersion,
                 PlacementVersion],
            [StructureId.InverseBloomFilter] = [DefaultVersion, ColumnPlacementVersion],
            [StructureId.CuckooBloomFilter] =
                [DefaultVersion, RandomStateVersion, CuckooPackedVersion,
                 CuckooOriginalStateVersion],
            [StructureId.CountMinSketch] = [DefaultVersion, ColumnPlacementVersion],
            [StructureId.HyperLogLog] = [DefaultVersion],
            [StructureId.TopK] = [DefaultVersion],
            [StructureId.MinHashSignature] = [DefaultVersion],
            [StructureId.BinaryFuseFilter] = [DefaultVersion],
            [StructureId.DDSketch] = [DefaultVersion],
            [StructureId.HyperLogLogPlus] = [DefaultVersion],
            [StructureId.QuotientFilter] = [DefaultVersion],
            [StructureId.ThetaSketch] = [DefaultVersion],
            [StructureId.SimHashSignature] = [DefaultVersion],
            [StructureId.CountSketch] = [DefaultVersion],
            [StructureId.InvertibleBloomLookupTable] = [DefaultVersion],
            [StructureId.BloomierFilter] = [DefaultVersion],
            [StructureId.HeavyKeeper] = [DefaultVersion, ColumnPlacementVersion],
            [StructureId.VarOpt] = [DefaultVersion],
            [StructureId.UltraLogLog] = [DefaultVersion],
            [StructureId.Grafite] = [DefaultVersion],
            [StructureId.InfiniFilter] = [DefaultVersion],
            [StructureId.MementoFilter] = [DefaultVersion],
            [StructureId.SublimeCountMinSketch] = [DefaultVersion],
            [StructureId.SetSketch] = [DefaultVersion, SetSketchVariantVersion],
            [StructureId.TupleSketch] = [DefaultVersion],
            [StructureId.PrivateCountMinSketch] = [DefaultVersion, ColumnPlacementVersion],
            [StructureId.DpswSketch] = [DefaultVersion, ColumnPlacementVersion],
        };

        /// <summary>
        /// The highest format version any structure defines. A payload above it was
        /// written by a later version of this library, which may mean it differently,
        /// and is refused as such.
        /// </summary>
        /// <remarks>
        /// Derived from the table above so that the two cannot disagree. It is not what
        /// decides whether a payload is read: a version at or below it that the
        /// structure being read never defined is refused too.
        /// </remarks>
        internal static readonly ushort MaxSupportedVersion =
            Readable.Values.Max(versions => versions.Max());

        /// <summary>
        /// Every structure's table entry, for a test to hold against the list of
        /// structures and against FORMAT.md.
        /// </summary>
        internal static IReadOnlyDictionary<StructureId, ushort[]> ReadableVersionTable => Readable;

        /// <summary>
        /// The format versions the reader of a structure accepts.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The structure has no entry in the table, which is a defect in this library
        /// rather than in any payload.
        /// </exception>
        internal static IReadOnlyList<ushort> ReadableVersions(StructureId structure)
        {
            return Readable.TryGetValue(structure, out var versions)
                ? versions
                : throw new InvalidOperationException(
                    $"No format versions are declared for {structure}, so nothing of " +
                    "that type can be read. Give it an entry in the table of readable " +
                    "versions in PersistenceFormat.");
        }

        /// <summary>
        /// Words for a set of versions, as "version 1" or "versions 1, 2 and 5".
        /// </summary>
        internal static string DescribeVersions(IReadOnlyList<ushort> versions)
        {
            return versions.Count switch
            {
                0 => "no version",
                1 => $"version {versions[0]}",
                _ => $"versions {string.Join(", ", versions.Take(versions.Count - 1))} " +
                     $"and {versions[^1]}",
            };
        }

        private const int MagicLength = 4;
        private const int HeaderLength = 14;
        private const int ChecksumLength = 4;

        /// <summary>
        /// The bytes the envelope adds around a payload: its header and its checksum.
        /// </summary>
        internal const int EnvelopeLength = HeaderLength + ChecksumLength;

        /// <summary>
        /// The longest payload an envelope can hold. A payload is written as one array
        /// together with the envelope around it, so it is the largest array less the
        /// envelope; the length field is a <c>u32</c>, which would allow more, and no
        /// array does.
        /// </summary>
        internal static int MaxPayloadLength => Array.MaxLength - EnvelopeLength;

        /// <summary>
        /// The bit of the version field that marks the large form of the envelope, for a
        /// payload longer than the ordinary one holds. The format version is the other
        /// fifteen bits, and no version has this one.
        /// </summary>
        /// <remarks>
        /// A flag rather than a version (#223), because a version numbers one change to the
        /// layouts of the structures it names, and the large form changes only the
        /// envelope, for any of them. Every released reader checks the magic and then
        /// refuses a version above its own highest before reading anything else, so it
        /// refuses the large form as written by a later version, which it was; everything
        /// after the version could be laid out afresh. A nested envelope's header is
        /// checked by the same code, so the flag works there too.
        /// </remarks>
        internal const ushort LargeFormFlag = 0x8000;

        /// <summary>
        /// The large form's header: magic, flagged version, structure id, hash id, a
        /// <c>u64</c> payload length, and the header's own checksum.
        /// </summary>
        internal const int LargeHeaderLength = LargeFieldsLength + ChecksumLength;

        /// <summary>The bytes the large form adds around a payload: its header and its checksum.</summary>
        internal const int LargeEnvelopeLength = LargeHeaderLength + ChecksumLength;

        /// <summary>
        /// Where the large header's checksum begins: the end of the fields it covers, which
        /// are the ordinary header's, with the length widened to a <c>u64</c>.
        /// </summary>
        private const int LargeFieldsLength = HeaderLength + sizeof(uint);

        /// <summary>
        /// A lower threshold for the large form, on the calling thread, so that the suite
        /// can write the large form at sizes it can hold; null otherwise, as it is in
        /// every thread but a test's.
        /// </summary>
        /// <remarks>
        /// Per thread because the suite runs its tests side by side, and every other
        /// test's payloads must be written as they always are. The writer reads it through
        /// <see cref="LargeFormThreshold"/>, itself, so a lowered threshold runs the code
        /// that a payload past the real one runs.
        /// </remarks>
        [ThreadStatic]
        internal static long? LoweredLargeFormThreshold;

        /// <summary>
        /// The longest payload written in the ordinary envelope: <see cref="MaxPayloadLength"/>,
        /// unless the suite has lowered it on this thread. A longer one is written in the
        /// large form, and only a longer one, so a payload that fits the ordinary envelope
        /// stays byte for byte what it was, and readable by every release that reads its
        /// version.
        /// </summary>
        internal static long LargeFormThreshold => LoweredLargeFormThreshold ?? MaxPayloadLength;

        /// <summary>Whether a payload of <paramref name="length"/> bytes is written in the large form.</summary>
        internal static bool IsLargeForm(long length) => length > LargeFormThreshold;

        /// <summary>
        /// Refuses a payload longer than an envelope holds, naming the size it would be and
        /// the size one can be, before anything is allocated for it.
        /// </summary>
        /// <param name="length">The payload's length, in bytes, worked out in 64 bits.</param>
        /// <exception cref="InvalidOperationException">
        /// The payload would be longer than <see cref="MaxPayloadLength"/>.
        /// </exception>
        internal static void CheckPayloadLength(long length)
        {
            if (length > MaxPayloadLength)
            {
                throw new InvalidOperationException(
                    $"The payload would reach {length:N0} bytes, and the most one can be is " +
                    $"{MaxPayloadLength:N0}: the largest array a .NET runtime holds, " +
                    $"{Array.MaxLength:N0} bytes, less the {EnvelopeLength} bytes the " +
                    "envelope adds around it in the same array. The structure is intact and " +
                    "can still be used, but it is too large to write; the format has no way " +
                    "to store it. Keep less in it, or split what it holds across several.");
            }
        }

        /// <summary>
        /// Told of each payload <see cref="Write(Stream, IPayloadBody)"/> counts, with the
        /// length counted, on the thread counting it. For the suite, which holds every
        /// structure with a body to being counted by its own <c>WriteTo</c> at the length it
        /// then writes; null otherwise.
        /// </summary>
        [ThreadStatic]
        internal static Action<IPayloadBody, long>? Counted;

        /// <summary>
        /// Writes a structure whose payload is a body: counts the body, refuses a payload
        /// longer than an envelope holds before anything is allocated for it, then writes
        /// the body into a buffer of exactly that length and wraps it in its envelope.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// The payload would be longer than <see cref="MaxPayloadLength"/>, or the body
        /// wrote a different payload from the one it counted. Nothing is written either way.
        /// </exception>
        /// <remarks>
        /// The count is the exact length of the payload, from the layout the body writes,
        /// so it is also the length an envelope can declare before the payload follows it.
        /// The two passes are held to agreeing because a body that counts one payload and
        /// writes another would otherwise be framed as whatever it wrote, under a size it
        /// was never checked at.
        /// <para>
        /// The count also chooses the envelope: the large form for a payload past
        /// <see cref="LargeFormThreshold"/>, and the ordinary one, as it always was, for
        /// any other. Past <see cref="MaxPayloadLength"/> the payload is still refused, as
        /// it cannot be built in one buffer; writing one that long needs the payload
        /// streamed rather than buffered (#225), so here the large form is reached only
        /// with the threshold lowered.
        /// </para>
        /// </remarks>
        internal static void Write(Stream stream, IPayloadBody structure)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var counter = PayloadWriter.Counter();
            var header = structure.WritePayload(counter);
            var length = counter.Length;
            Counted?.Invoke(structure, length);
            CheckPayloadLength(length);

            var payload = new PayloadWriter((int)length);
            var written = structure.WritePayload(payload);
            if (payload.Length != length || written != header)
            {
                throw new InvalidOperationException(
                    $"The {header.Structure} counted a payload of {length:N0} bytes, version " +
                    $"{header.Version}, and then wrote {payload.Length:N0} bytes, version " +
                    $"{written.Version}. Nothing was written: the two have to be one layout.");
            }

            if (IsLargeForm(length))
            {
                WriteLarge(stream, header, payload.WrittenSpan);
            }
            else
            {
                Write(stream, header.Structure, header.Hash, payload.WrittenSpan, header.Version);
            }
        }

        /// <summary>
        /// Writes a payload in the large form: its header, with its own checksum, then the
        /// payload, then the checksum the header's began, carried on over the payload.
        /// </summary>
        /// <remarks>
        /// The payload is held here, as it is for the ordinary envelope; the header and the
        /// final checksum are written by the two calls a writer that streams the payload
        /// puts either side of it (#225).
        /// </remarks>
        internal static void WriteLarge(Stream stream, EnvelopeHeader header, ReadOnlySpan<byte> payload)
        {
            var crc = WriteLargeHeader(stream, header, (ulong)payload.Length);
            crc.Append(payload);
            stream.Write(payload);
            WriteLargeChecksum(stream, crc);
        }

        /// <summary>
        /// Writes the large form's header for a payload of <paramref name="length"/> bytes,
        /// and returns the checksum it began, which the payload is to be appended to as it
        /// is written and which <see cref="WriteLargeChecksum"/> then writes.
        /// </summary>
        /// <remarks>
        /// The header's checksum is the running checksum as it stands at the end of the
        /// header's fields, and the payload is appended to the same running checksum
        /// without it. Appending a CRC-32's own value to what it covers brings it to one
        /// constant state whatever came before, so a checksum carried on over the header's
        /// checksum would no longer depend on the header at all.
        /// </remarks>
        internal static Crc32 WriteLargeHeader(Stream stream, EnvelopeHeader header, ulong length)
        {
            Span<byte> bytes = stackalloc byte[LargeHeaderLength];
            Magic.CopyTo(bytes);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[4..], (ushort)(header.Version | LargeFormFlag));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[6..], (ushort)header.Structure);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[8..], (ushort)header.Hash);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[10..], length);

            var crc = new Crc32();
            crc.Append(bytes[MagicLength..LargeFieldsLength]);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[LargeFieldsLength..], crc.GetCurrentHashAsUInt32());

            stream.Write(bytes);
            return crc;
        }

        /// <summary>
        /// Writes the large form's final checksum: the one <see cref="WriteLargeHeader"/>
        /// began, with the whole payload appended to it.
        /// </summary>
        internal static void WriteLargeChecksum(Stream stream, Crc32 crc)
        {
            Span<byte> bytes = stackalloc byte[ChecksumLength];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, crc.GetCurrentHashAsUInt32());
            stream.Write(bytes);
        }

        /// <summary>
        /// Wraps a payload in the envelope and writes the whole of it to the stream.
        /// </summary>
        internal static void Write(
            Stream stream,
            StructureId structure,
            HashId hash,
            ReadOnlySpan<byte> payload,
            ushort version = DefaultVersion)
        {
            var frame = new byte[HeaderLength + payload.Length + ChecksumLength];

            Magic.CopyTo(frame);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), version);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), (ushort)structure);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), (ushort)hash);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(10), (uint)payload.Length);
            payload.CopyTo(frame.AsSpan(HeaderLength));

            // Everything after the magic, which is not worth checksumming: a payload
            // whose magic is wrong is not this format at all.
            var covered = frame.AsSpan(MagicLength, HeaderLength - MagicLength + payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(
                frame.AsSpan(HeaderLength + payload.Length), Crc32.HashToUInt32(covered));

            stream.Write(frame, 0, frame.Length);
        }

        /// <summary>
        /// Reads and validates an envelope, then reads the structure in it from its
        /// payload with <paramref name="parse"/>, and returns the structure.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// The stream does not hold a payload of this format, holds a format version
        /// the structure never defined (a later one included), holds a different
        /// structure, or has been corrupted; or the structure's reader refused the
        /// payload.
        /// </exception>
        /// <remarks>
        /// <paramref name="price"/>, <paramref name="supplied"/> and
        /// <paramref name="checkHash"/> are as
        /// <see cref="Read(Stream, byte[], StructureId, out HashId, out ushort, ReadBudget, PayloadPrice, Func{ReadOnlySpan{byte}, ulong}, HashCheck)"/>
        /// takes them.
        /// </remarks>
        internal static T Read<T>(
            Stream stream, StructureId expected, ReadBudget budget, PayloadPrice price,
            Func<ReadOnlySpan<byte>, ulong>? supplied, HashCheck checkHash, PayloadParse<T> parse)
        {
            var header = ReadExactly(stream, HeaderLength, "header");
            if (IsLargeHeader(header))
            {
                return ReadLarge(stream, header, expected, budget, price, supplied, checkHash, parse);
            }

            var payload = Read(stream, header, expected, out var hash, out var version, budget, price, supplied, checkHash);
            var reader = new PayloadReader(payload, budget);
            return parse(ref reader, version, hash, supplied);
        }

        /// <summary>
        /// Reads an envelope in the large form, whose first <see cref="HeaderLength"/> bytes
        /// have been read, and the structure in it, and returns the structure only once the
        /// checksum at the end of its payload has matched.
        /// </summary>
        /// <remarks>
        /// The header's own checksum is checked before any field it covers is used, so a
        /// damaged structure id or length is refused as damaged, never acted on, as the
        /// ordinary envelope's single checksum ensures for it. The payload's checksum
        /// follows the payload and is compared once the structure's reader has read the
        /// whole of it (#227): a reader that does not hold the payload cannot check it
        /// sooner, and this is the order such a reader reads in. If it does not match, the
        /// structure just read is discarded and the read refused, so no caller receives
        /// it. A refusal before the checksum, for the payload's shape or its price, says
        /// that the checksum was not reached, and nothing past the point of refusal is
        /// read.
        /// <para>
        /// The payload is held here, read into one array as the ordinary envelope's is, so
        /// one longer than an array is refused for that before it is priced, as an
        /// ordinary header's length is; a reader that streams the large form lifts that
        /// (#226).
        /// </para>
        /// </remarks>
        private static T ReadLarge<T>(
            Stream stream, byte[] first, StructureId expected, ReadBudget budget, PayloadPrice price,
            Func<ReadOnlySpan<byte>, ulong>? supplied, HashCheck checkHash, PayloadParse<T> parse)
        {
            Span<byte> header = stackalloc byte[LargeHeaderLength];
            first.CopyTo(header);
            ReadExactly(stream, header[HeaderLength..], "rest of the large header");
            var length = CheckLargeHeader(header, expected, out var version, out var hash);
            var envelope = EnvelopeCost(length);

            // Held, so refused past one array for that, as the ordinary header's length
            // is, before it is priced as though it could be read.
            if (length > (ulong)Array.MaxLength)
            {
                throw new InvalidDataException(
                    $"Payload claims a length of {length} bytes, which cannot be read " +
                    "into a single array.");
            }

            // The header is vouched for by its own checksum, so the hash it names is
            // checked now, before anything is priced, as an ordinary header's is once its
            // checksum has vouched for it.
            checkHash(hash, supplied);

            var pricing = !budget.IsSettled;
            if (pricing)
            {
                budget.Price(envelope);
                if (budget.Charged > budget.Limit)
                {
                    PriceUnheld(stream, length, version, budget, price);
                    throw ChecksumNotReached(budget.Refusal(budget.Charged), expected);
                }
            }
            else
            {
                budget.Charge(envelope);
            }

            var payload = ReadExactly(stream, (int)length, "payload");
            var crc = new Crc32();
            crc.Append(header[MagicLength..LargeFieldsLength]);

            T structure;
            try
            {
                if (pricing)
                {
                    PriceHeld(payload, version, budget, price, envelope);
                }

                var reader = new PayloadReader(payload, budget);
                structure = parse(ref reader, version, hash, supplied);
                reader.ExpectEnd();
            }
            catch (InvalidDataException refusal)
            {
                throw ChecksumNotReached(refusal, expected);
            }

            var checksum = ReadExactly(stream, ChecksumLength, "checksum");
            crc.Append(payload);

            var expectedCrc = crc.GetCurrentHashAsUInt32();
            var actualCrc = BinaryPrimitives.ReadUInt32LittleEndian(checksum);
            if (expectedCrc != actualCrc)
            {
                throw new InvalidDataException(
                    $"The checksum at the end of the payload does not match: expected " +
                    $"{expectedCrc:X8} and found {actualCrc:X8}. The data has been corrupted " +
                    $"or truncated, and the {expected} read from it has been discarded.");
            }

            return structure;
        }

        /// <summary>
        /// A refusal of a payload in the large form made before its checksum was reached,
        /// saying so: what was refused may be damage the checksum would have named.
        /// </summary>
        /// <remarks>
        /// A refusal for the read's budget keeps its <see cref="ReadBudgetExceededException"/>
        /// as the inner exception, which is how a caller tells it from the rest; any other
        /// becomes the inner exception itself.
        /// </remarks>
        private static InvalidDataException ChecksumNotReached(InvalidDataException refusal, StructureId expected)
        {
            var message =
                $"{refusal.Message} The {expected} is in the large form, whose checksum follows " +
                "its payload, and that checksum was not reached, so whether the bytes were " +
                "damaged is not known.";

            return refusal.InnerException is ReadBudgetExceededException budget
                ? new InvalidDataException(message, budget)
                : new InvalidDataException(message, refusal);
        }

        /// <summary>
        /// Reads and validates an envelope, returning its payload, the hash that was in
        /// use when it was written and the format version it was written at.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// The stream does not hold a payload of this format, holds a format version
        /// the structure never defined (a later one included), holds a different
        /// structure, or has been corrupted.
        /// </exception>
        /// <remarks>
        /// A read is priced whole before it allocates anything a payload claims, beginning
        /// with the payload itself, so that a refusal names everything the read takes and
        /// a budget of that loads it. <paramref name="price"/> is the structure's own
        /// price, which walks the payload's fields as its reader does. When the payload
        /// fits the budget it is read and its checksum checked first, so a corrupted
        /// payload is refused as corrupted, and priced from memory. When the payload
        /// alone is more than the budget it is never held: it is priced from the stream, a
        /// field at a time, its runs skipped, and refused.
        /// <para>
        /// The hash the header names is checked against <paramref name="supplied"/>, by
        /// <paramref name="checkHash"/>, before anything is priced: once the checksum has
        /// vouched for the header when the payload is held, so that a header damaged there
        /// is refused as damaged, and as the header stands when it is not, as everything a
        /// payload too large to hold is priced from does.
        /// </para>
        /// <para>
        /// A structure nested inside another is read under the budget its outer read has
        /// already priced, nested envelope included, so its own reads draw on that.
        /// </para>
        /// </remarks>
        private static byte[] Read(
            Stream stream, byte[] header, StructureId expected, out HashId hash, out ushort version,
            ReadBudget budget, PayloadPrice price, Func<ReadOnlySpan<byte>, ulong>? supplied, HashCheck checkHash)
        {
            var length = CheckHeader(header, expected, out version, out hash);
            var envelope = EnvelopeCost(length);

            var pricing = !budget.IsSettled;
            if (pricing)
            {
                // The payload is the first thing a header's claim allocates, and it is
                // priced before it is: a header claiming two gigabytes is refused on that
                // claim, and on everything else the payload claims, before the read has
                // made room for any of it. The header and checksum buffers and the
                // checksum's state come with it.
                budget.Price(envelope);
                if (budget.Charged > budget.Limit)
                {
                    checkHash(hash, supplied);
                    PriceUnheld(stream, length, version, budget, price);
                    throw budget.Refusal(budget.Charged);
                }
            }
            else
            {
                budget.Charge(envelope);
            }

            var payload = ReadExactly(stream, (int)length, "payload");
            var checksum = ReadExactly(stream, ChecksumLength, "checksum");

            var crc = new Crc32();
            crc.Append(header.AsSpan(MagicLength));
            crc.Append(payload);

            var expectedCrc = crc.GetCurrentHashAsUInt32();
            var actualCrc = BinaryPrimitives.ReadUInt32LittleEndian(checksum);
            if (expectedCrc != actualCrc)
            {
                throw new InvalidDataException(
                    $"Payload checksum does not match: expected {expectedCrc:X8} and " +
                    $"found {actualCrc:X8}. The data has been corrupted or truncated.");
            }

            checkHash(hash, supplied);

            if (pricing)
            {
                PriceHeld(payload, version, budget, price, envelope);
            }

            return payload;
        }

        /// <summary>
        /// Prices a held payload's read with the structure's own price, then admits the
        /// read at that price or refuses it, and charges the envelope, which was priced
        /// before the payload was held, against it.
        /// </summary>
        private static void PriceHeld(
            byte[] payload, ushort version, ReadBudget budget, PayloadPrice price, long envelope)
        {
            var reader = new PayloadReader(payload, budget);
            try
            {
                price(ref reader, version);
            }
            catch (InvalidDataException)
            {
                // Malformed somewhere past what has been priced. Its reader refuses it
                // there, or before, with its own explanation, unless what was priced up
                // to there is already more than the budget.
            }

            budget.Settle();
            budget.Charge(envelope);
        }

        /// <summary>
        /// Prices a payload too large for its budget to be held, reading it from the
        /// stream, so that the refusal can name the whole of the read.
        /// </summary>
        /// <remarks>
        /// Its checksum cannot be checked without reading it all, so the price rests on
        /// fields nothing has vouched for, as the refusal it names always did. A payload
        /// that ends early, or is malformed before its sizes have all been read, is
        /// priced as far as it goes.
        /// </remarks>
        private static void PriceUnheld(
            Stream stream, ulong length, ushort version, ReadBudget budget, PayloadPrice price)
        {
            var reader = new PayloadReader(new PayloadSource(stream), (long)length, budget);
            try
            {
                price(ref reader, version);
            }
            catch (InvalidDataException)
            {
                // What could be priced is what the refusal names.
            }
        }

        /// <summary>
        /// Checks an envelope's magic, the one thing read before anything else, and says
        /// whether its version field carries <see cref="LargeFormFlag"/>: whether its header
        /// is the large form's, which is longer, and has a checksum of its own.
        /// </summary>
        /// <param name="header">At least the first <see cref="HeaderLength"/> bytes of the envelope.</param>
        /// <remarks>
        /// The flag is read before the large header's checksum is checked, unavoidably,
        /// since it says how much header there is to check. A flag flipped on in an
        /// ordinary header has that header read as a large one, whose checksum then fails;
        /// one flipped off in a large header has it read as an ordinary one, which the
        /// ordinary checksum, over other bytes, refuses.
        /// </remarks>
        private static bool IsLargeHeader(ReadOnlySpan<byte> header)
        {
            if (!header.Slice(0, MagicLength).SequenceEqual(Magic))
            {
                throw new InvalidDataException(
                    "The stream does not begin with a probabilistic data structure " +
                    "payload; its first bytes are not the expected marker.");
            }

            return (BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(4)) & LargeFormFlag) != 0;
        }

        /// <summary>
        /// Checks an ordinary envelope's header, after <see cref="IsLargeHeader"/>, as its
        /// reader does and as a price looking into a nested one does, and returns the
        /// payload length it claims.
        /// </summary>
        private static uint CheckHeader(
            ReadOnlySpan<byte> header, StructureId expected, out ushort version, out HashId hash)
        {
            version = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(4));
            CheckVersionAndStructure(header, expected, version, large: false);

            hash = (HashId)BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(8));

            var length = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(10));
            if (length > Array.MaxLength)
            {
                throw new InvalidDataException(
                    $"Payload claims a length of {length} bytes, which cannot be read " +
                    "into a single array.");
            }

            return length;
        }

        /// <summary>
        /// Checks a large-form header, all <see cref="LargeHeaderLength"/> bytes of it, after
        /// <see cref="IsLargeHeader"/>: its checksum first, and then, as an ordinary header
        /// is checked, the version without its flag, the structure and the hash, and returns
        /// the payload length it claims.
        /// </summary>
        /// <remarks>
        /// Nothing the checksum covers is used before it has matched, so a damaged length
        /// or structure id is refused as damage and never acted on, which is what the
        /// ordinary envelope's single checksum ensures for its own header.
        /// </remarks>
        private static ulong CheckLargeHeader(
            ReadOnlySpan<byte> header, StructureId expected, out ushort version, out HashId hash)
        {
            var expectedCrc = Crc32.HashToUInt32(header[MagicLength..LargeFieldsLength]);
            var actualCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[LargeFieldsLength..]);
            if (expectedCrc != actualCrc)
            {
                throw new InvalidDataException(
                    $"The large form's header checksum does not match: expected {expectedCrc:X8} " +
                    $"and found {actualCrc:X8}. The header has been corrupted or truncated, and " +
                    "none of its fields was used.");
            }

            version = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) & ~LargeFormFlag);
            CheckVersionAndStructure(header, expected, version, large: true);

            hash = (HashId)BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);

            // A stream's length and position are a long, and so is every offset a reader
            // works out in one.
            var length = BinaryPrimitives.ReadUInt64LittleEndian(header[10..]);
            if (length > long.MaxValue)
            {
                throw new InvalidDataException(
                    $"Payload claims a length of {length} bytes, more than a stream can hold.");
            }

            return length;
        }

        /// <summary>
        /// Refuses a version the structure being read never defined, a later one or 0
        /// included, and a header naming another structure, in that order.
        /// </summary>
        private static void CheckVersionAndStructure(
            ReadOnlySpan<byte> header, StructureId expected, ushort version, bool large)
        {
            // Before the structure is compared, as it always was: a version this library
            // has never heard of may lay the header out differently, and then there is
            // no structure id to trust.
            if (version == 0 || version > MaxSupportedVersion)
            {
                throw RefuseVersion(expected, version, large);
            }

            var structure = (StructureId)BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(6));
            if (structure != expected)
            {
                throw new InvalidDataException(
                    $"Payload holds a {Describe(structure)} and was read as a " +
                    $"{Describe(expected)}.");
            }

            // After it, because which versions are acceptable depends on which structure
            // this is. A version some other structure defined is not an older or newer
            // layout of this one, and reading it as one is a guess.
            if (!ReadableVersions(expected).Contains(version))
            {
                throw RefuseVersion(expected, version, large);
            }
        }

        /// <summary>
        /// The refusal for a format version the structure being read does not define,
        /// which names the structure, the version it was handed and the versions it
        /// does read; for the large form, the version without its flag.
        /// </summary>
        private static InvalidDataException RefuseVersion(StructureId expected, ushort version, bool large)
        {
            var reads = DescribeVersions(ReadableVersions(expected));

            var why = version == 0
                ? "Version 0 is reserved and never written."
                : version > MaxSupportedVersion
                    ? "It was written by a later version of this library."
                    : $"{expected} has never been written at that version, so the " +
                      "header does not describe a payload this library produced.";

            var form = large ? ", in the large form" : "";
            return new InvalidDataException(
                $"Payload is format version {version}{form}, and {expected} is read at " +
                $"{reads}. {why}");
        }

        /// <summary>
        /// Returns the hash function an identifier names, or null if the identifier
        /// names one this library cannot supply.
        /// </summary>
        internal static Func<ReadOnlySpan<byte>, ulong>? Resolve(HashId hash)
        {
            return IsResolvable(hash) ? Defaults.GetDefaultHashFunction() : null;
        }

        /// <summary>
        /// Whether <see cref="Resolve"/> supplies a hash for an identifier, without making
        /// the delegate it would return.
        /// </summary>
        internal static bool IsResolvable(HashId hash) => hash == HashId.XxHash3_64;

        /// <summary>
        /// The check for a structure that hashes with what it is given, or else with what
        /// its payload names: that <see cref="ResolveOrThrow"/> will settle on a hash,
        /// refusing the payload as it would. It allocates nothing, as it runs for every
        /// structure a scalable filter holds.
        /// </summary>
        internal static void CheckResolvable(HashId recorded, Func<ReadOnlySpan<byte>, ulong>? supplied)
        {
            if (supplied is null && !IsResolvable(recorded))
            {
                throw Unresolvable(recorded);
            }
        }

        /// <summary>
        /// Settles which hash a structure being read should use, given what was
        /// recorded and what the caller supplied.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// The recorded hash cannot be reconstructed and none was supplied.
        /// </exception>
        internal static Func<ReadOnlySpan<byte>, ulong> ResolveOrThrow(
            HashId hash,
            Func<ReadOnlySpan<byte>, ulong>? supplied)
        {
            // A supplied hash wins outright. The caller knows what they wrote with,
            // and this is the only way to read a payload back that used one.
            if (supplied is not null)
            {
                return supplied;
            }

            return Resolve(hash) ?? throw Unresolvable(hash);
        }

        /// <summary>The refusal of a hash that cannot be reconstructed, when none was supplied.</summary>
        private static InvalidDataException Unresolvable(HashId hash)
        {
            if (hash == HashId.Custom)
            {
                return new InvalidDataException(
                    "This structure was written while using a hash function set through " +
                    "SetHash, which cannot be recorded. Read it with the overload that " +
                    "takes a hash function, supplying the same one. Reading it with the " +
                    "default would not fail: the structure would answer no to everything " +
                    "and look empty rather than wrong.");
            }

            return new InvalidDataException(
                $"This structure was written using hash function {(ushort)hash}, which " +
                "this version does not know. It was written by a later version. Supply " +
                "that hash function explicitly to read it anyway.");
        }

        /// <summary>
        /// Returns the identifier for a hash function, by asking whether it is one this
        /// library installed rather than by inspecting it.
        /// </summary>
        internal static HashId Identify(Func<ReadOnlySpan<byte>, ulong> hash)
        {
            return Defaults.IsDefaultHashFunction(hash) ? HashId.XxHash3_64 : HashId.Custom;
        }

        /// <summary>
        /// Writes a <see cref="Buckets"/> into a payload.
        /// </summary>
        /// <remarks>
        /// The run is the table's own array, written from the array it holds rather than a
        /// copy. Its length is the one the count and width give, which is what
        /// <see cref="ReadBuckets"/> holds the run to, so that a counter counts it from the
        /// shape and never touches the bytes.
        /// </remarks>
        internal static void WriteBuckets(PayloadWriter payload, Buckets buckets)
        {
            payload.WriteUInt32(buckets.count);
            payload.WriteByte(buckets.BucketSize);
            if (payload.BeginRun(Buckets.DataLength(buckets.count, buckets.BucketSize)))
            {
                payload.WriteRaw(buckets.RawData);
            }
        }

        /// <summary>
        /// Reads a <see cref="Buckets"/> written by <see cref="WriteBuckets"/>.
        /// </summary>
        internal static Buckets ReadBuckets(ref PayloadReader reader)
        {
            var count = reader.ReadUInt32();
            var bucketSize = reader.ReadByte();
            var data = reader.ReadBytes();

            // The data is the run just copied, charged as it was; Restore keeps it rather
            // than allocating its own, so what is left is the object holding it.
            reader.Budget.Charge(Buckets.ObjectSize);
            return Buckets.Restore(count, bucketSize, data);
        }

        /// <summary>
        /// Prices what <see cref="ReadBuckets"/> allocates, and moves past what it reads.
        /// Returns the bucket count, for a price that checks it as its reader does.
        /// </summary>
        internal static uint PriceBuckets(ref PayloadReader reader)
        {
            var count = reader.ReadUInt32();
            var bucketSize = reader.ReadByte();
            var length = reader.SkipBytes();
            reader.Budget.Price(Buckets.ObjectSize);
            Buckets.CheckRestorable(count, bucketSize, length);
            return count;
        }

        /// <summary>
        /// Refuses a hash count no filter this library builds has: none, or more than
        /// <see cref="Utils.MaxHashCount"/>. The shape check of every Bloom-family reader,
        /// run before anything is allocated or priced from the count.
        /// </summary>
        /// <param name="k">The count the payload names.</param>
        /// <exception cref="InvalidDataException">There are none, or too many.</exception>
        /// <remarks>
        /// A filter of none sets no bit and tests none, so it answers present to every
        /// item. A filter of more than any constructor gives runs that many position
        /// computations on every add and, for a member, every test, and a payload that
        /// names 4,294,967,295 of them is a stalled filter that holds a few bytes. Those
        /// that keep a scratch array of the count would allocate it first.
        /// </remarks>
        internal static void CheckHashCount(uint k)
        {
            if (k == 0)
            {
                throw new InvalidDataException(
                    "Filter has no hash functions, so it would set nothing and test " +
                    "nothing, and report every item present.");
            }

            var most = Utils.MaxHashCount;
            if (k > most)
            {
                throw new InvalidDataException(
                    $"Filter has {k:N0} hash functions, more than the {most:N0} any filter " +
                    "this library builds has. Every add and test would run that many " +
                    "position computations.");
            }
        }

        /// <summary>
        /// Writes a <see cref="Buckets64"/> into a payload.
        /// </summary>
        internal static void WriteBuckets64(PayloadWriter payload, Buckets64 buckets)
        {
            payload.WriteUInt64(buckets.count);
            payload.WriteByte(buckets.BucketSize);

            var arrays = buckets.RawData;
            payload.WriteUInt32((uint)arrays.Length);
            foreach (var array in arrays)
            {
                payload.WriteBytes(array);
            }
        }

        /// <summary>
        /// Reads a <see cref="Buckets64"/> written by <see cref="WriteBuckets64"/>.
        /// </summary>
        internal static Buckets64 ReadBuckets64(ref PayloadReader reader)
        {
            var count = reader.ReadUInt64();
            var bucketSize = reader.ReadByte();
            var arrayCount = ReadArrayCount(ref reader);

            reader.Budget.Charge(ArraysCost(arrayCount));
            var arrays = new byte[arrayCount][];
            for (uint i = 0; i < arrayCount; i++)
            {
                arrays[i] = reader.ReadBytes();
            }

            return Buckets64.Restore(count, bucketSize, arrays);
        }

        /// <summary>
        /// Prices what <see cref="ReadBuckets64"/> allocates, and moves past what it reads.
        /// </summary>
        internal static void PriceBuckets64(ref PayloadReader reader)
        {
            reader.ReadUInt64();
            reader.ReadByte();
            var arrayCount = ReadArrayCount(ref reader);

            reader.Budget.Price(ArraysCost(arrayCount));
            for (uint i = 0; i < arrayCount; i++)
            {
                reader.SkipBytes();
            }
        }

        private static uint ReadArrayCount(ref PayloadReader reader)
        {
            var arrayCount = reader.ReadUInt32();
            if (arrayCount > MaxNestedCount)
            {
                throw new InvalidDataException(
                    $"Bucket data claims {arrayCount} arrays, beyond anything this " +
                    "library builds.");
            }
            return arrayCount;
        }

        /// <summary>The array of a 64-bit table's arrays, and the table around them.</summary>
        private static long ArraysCost(uint arrayCount) => ReadBudget.Sum(
            ReadBudget.ArrayOf(arrayCount, sizeof(long)), ReadBudget.SmallObject);

        /// <summary>
        /// Writes one structure inside another's payload, as a length-prefixed run
        /// holding its own complete envelope.
        /// </summary>
        /// <remarks>
        /// A structure held by another keeps its own marker, version, structure id,
        /// hash id and checksum rather than being flattened into the outer payload.
        /// It costs eighteen bytes against payloads measured in tens of thousands, and
        /// buys three things: the inner structure names its own hash, so a composite
        /// cannot silently disagree with itself about hashing; it can be pulled out and
        /// read on its own; and the outer structure can change what it holds without
        /// the inner layout being part of that change.
        /// <para>
        /// A counter counts the inner structure through its own body, as its own
        /// <c>WriteTo</c> would, with nothing built: the run's length prefix, the
        /// envelope, and the payload inside it. So a structure that holds another is
        /// counted only once the one it holds is an <see cref="IPayloadBody"/>, and the
        /// cast says so if it is not.
        /// </para>
        /// <para>
        /// The envelope counted is the one the inner structure's own write will choose, by
        /// <see cref="IsLargeForm"/> on the inner payload's length, which is the length
        /// its body has just added to the count.
        /// </para>
        /// </remarks>
        internal static void WriteNested<T>(PayloadWriter payload, T structure)
            where T : IBinaryPersistable<T>
        {
            if (payload.IsCounting)
            {
                var before = payload.Length;
                ((IPayloadBody)structure).WritePayload(payload);
                var inner = payload.Length - before;
                payload.BeginBlock(sizeof(uint) + (IsLargeForm(inner) ? LargeEnvelopeLength : EnvelopeLength));
                return;
            }

            using var buffer = new MemoryStream();
            structure.WriteTo(buffer);
            payload.WriteBytes(buffer.ToArray());
        }

        /// <summary>
        /// Reads a structure written by <see cref="WriteNested"/>.
        /// </summary>
        internal static T ReadNested<T>(ref PayloadReader reader, Func<ReadOnlySpan<byte>, ulong>? hash)
            where T : IBinaryPersistable<T>
        {
            var bytes = reader.ReadBytes();
            using var buffer = new MemoryStream(bytes, writable: false);

            // The nested structure draws on the same allowance as the one around it, so
            // that a composite cannot spend its budget once per part. The run it was
            // copied from is charged by ReadBytes; this is the stream and options around
            // it, and the nested envelope then charges its own payload. All of it was
            // priced with the structure around it, by PriceNested.
            reader.Budget.Charge(NestedReadCost);
            var options = new ReadOptions(reader.Budget);
            return hash is null ? T.ReadFrom(buffer, options) : T.ReadFrom(buffer, hash, options);
        }

        /// <summary>
        /// Prices what <see cref="ReadNested"/> allocates for a <paramref name="nested"/>
        /// structure, by looking into its envelope where it sits in the payload, and moves
        /// past it.
        /// </summary>
        /// <remarks>
        /// The run the nested read copies, the stream and options around it, the nested
        /// envelope's payload and buffers, and whatever the nested structure's own price
        /// says, read from the nested payload as it lies inside this one -- the same
        /// sums, in the same order, that the nested read charges. Its header is checked
        /// as its reader checks it, in either form, so a nested envelope that is not the
        /// structure it should be stops the price where the read will stop.
        /// </remarks>
        internal static void PriceNested(ref PayloadReader reader, StructureId nested, PayloadPrice price)
        {
            var run = reader.PriceBytes();
            reader.Budget.Price(NestedReadCost);

            var envelope = reader.Slice(run);

            // Copied out a piece at a time: a payload that is not held hands each piece
            // back in a buffer the next one overwrites.
            Span<byte> header = stackalloc byte[LargeHeaderLength];
            envelope.ReadSpan(HeaderLength).CopyTo(header);

            ulong length;
            ushort version;
            if (IsLargeHeader(header))
            {
                envelope.ReadSpan(LargeHeaderLength - HeaderLength).CopyTo(header[HeaderLength..]);
                length = CheckLargeHeader(header, nested, out version, out _);
            }
            else
            {
                length = CheckHeader(header, nested, out version, out _);
            }

            envelope.Budget.Price(EnvelopeCost(length));

            var payload = envelope.Slice((long)length);
            price(ref payload, version);
            envelope.SkipRest();
        }

        /// <summary>
        /// A ceiling on counts read from a payload before anything is allocated for
        /// them, so that a corrupted length cannot ask for an enormous allocation
        /// before the read that would have failed gets a chance to.
        /// </summary>
        /// <remarks>
        /// Only for counts no constructor can come near. It used to bound sizes that
        /// constructors do build -- buckets, retained entries, signature lengths, heap
        /// sizes -- and so refused, as "beyond anything this library builds", payloads
        /// this library had written. Those readers now share one limit per structure
        /// with the structure's constructor, and check a count against the bytes
        /// actually present (<see cref="PayloadReader.RequireHolds"/>) before
        /// allocating for it.
        /// <para>
        /// What is left here is held to that by tests rather than by this comment: each
        /// remaining use is an exemption in <c>TestLoadWhatItBuilds</c>, with the reason
        /// its constructor cannot reach the ceiling, and the reason is checked.
        /// </para>
        /// </remarks>
        internal const uint MaxNestedCount = 1_000_000;

        /// <summary>
        /// The fewest bytes a structure written by <see cref="WriteNested"/> takes: its
        /// length prefix and an envelope around an empty payload.
        /// </summary>
        internal const int MinNestedLength = sizeof(uint) + HeaderLength + ChecksumLength;

        /// <summary>
        /// What reading an envelope with a payload of <paramref name="length"/> bytes
        /// allocates: the payload, the header and checksum buffers, and the checksum's
        /// state.
        /// </summary>
        /// <remarks>
        /// The same for either form, while both hold the payload: the rest of a large
        /// header is read onto the stack, beside the ordinary header's buffer.
        /// </remarks>
        internal static long EnvelopeCost(ulong length) => ReadBudget.Sum(
            ReadBudget.ArrayOf(length, 1),
            ReadBudget.ArrayOf(HeaderLength, 1),
            ReadBudget.ArrayOf(ChecksumLength, 1),
            ReadBudget.SmallObject);

        /// <summary>
        /// What a nested read allocates around the run it reads from: a stream over it
        /// and the options that carry the shared budget, measured at 64 and 32 bytes on
        /// a 64-bit runtime, and a small object's margin over both. A scalable filter
        /// makes one of these per contained filter, so it is charged near its size
        /// rather than at the general bound for each object.
        /// </summary>
        internal const long NestedReadCost = 128;

        private static byte[] ReadExactly(Stream stream, int count, string what)
        {
            var buffer = new byte[count];
            var read = 0;

            while (read < count)
            {
                var n = stream.Read(buffer, read, count - read);
                if (n == 0)
                {
                    throw Truncated(read, count, what);
                }
                read += n;
            }

            return buffer;
        }

        /// <summary>
        /// Fills a few bytes on the stack, the rest of a large header. Only for a few: a
        /// stream that implements only the array overload of <c>Read</c> is handed a rented
        /// array as long as the span.
        /// </summary>
        private static void ReadExactly(Stream stream, Span<byte> buffer, string what)
        {
            var read = 0;

            while (read < buffer.Length)
            {
                var n = stream.Read(buffer[read..]);
                if (n == 0)
                {
                    throw Truncated(read, buffer.Length, what);
                }
                read += n;
            }
        }

        private static InvalidDataException Truncated(int read, int count, string what) => new(
            $"The stream ended after {read} of the {count} bytes the {what} needs. The data " +
            "has been truncated.");

        private static string Describe(StructureId structure)
        {
            return Enum.IsDefined(structure)
                ? structure.ToString()
                : $"structure of unknown type {(ushort)structure}";
        }
    }
}
