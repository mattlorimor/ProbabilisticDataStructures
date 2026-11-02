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
        /// The format versions each structure has defined, which are exactly the ones its
        /// reader reads. This is the one place that says so.
        /// </summary>
        /// <remarks>
        /// A version is a property of a structure's layout, not of the library, so a
        /// structure's reader reads the versions that structure has written and no
        /// others. Accepting every version up to the highest anywhere would read a
        /// version some other structure defined as though it were this one's newest
        /// layout;
        /// <see cref="Read(Stream, StructureId, out HashId, out ushort, ReadBudget)"/> refuses
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
            [StructureId.BloomFilter] = [DefaultVersion],
            [StructureId.BloomFilter64] = [DefaultVersion],
            [StructureId.CountingBloomFilter] = [DefaultVersion],
            [StructureId.DeletableBloomFilter] = [DefaultVersion],
            [StructureId.PartitionedBloomFilter] = [DefaultVersion],
            [StructureId.ScalableBloomFilter] = [DefaultVersion, ScalableGrowthVersion],
            [StructureId.StableBloomFilter] =
                [DefaultVersion, RandomStateVersion, StableOriginalStateVersion],
            [StructureId.InverseBloomFilter] = [DefaultVersion],
            [StructureId.CuckooBloomFilter] =
                [DefaultVersion, RandomStateVersion, CuckooPackedVersion,
                 CuckooOriginalStateVersion],
            [StructureId.CountMinSketch] = [DefaultVersion],
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
            [StructureId.HeavyKeeper] = [DefaultVersion],
            [StructureId.VarOpt] = [DefaultVersion],
            [StructureId.UltraLogLog] = [DefaultVersion],
            [StructureId.Grafite] = [DefaultVersion],
            [StructureId.InfiniFilter] = [DefaultVersion],
            [StructureId.MementoFilter] = [DefaultVersion],
            [StructureId.SublimeCountMinSketch] = [DefaultVersion],
            [StructureId.SetSketch] = [DefaultVersion, SetSketchVariantVersion],
            [StructureId.TupleSketch] = [DefaultVersion],
            [StructureId.PrivateCountMinSketch] = [DefaultVersion],
            [StructureId.DpswSketch] = [DefaultVersion],
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
        /// Reads and validates an envelope, returning its payload and the hash that was
        /// in use when it was written.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// The stream does not hold a payload of this format, holds a format version
        /// the structure never defined (a later one included), holds a different
        /// structure, or has been corrupted.
        /// </exception>
        internal static byte[] Read(
            Stream stream, StructureId expected, out HashId hash, ReadBudget budget)
        {
            return Read(stream, expected, out hash, out _, budget);
        }

        /// <summary>
        /// Reads and validates an envelope, also reporting the format version it was
        /// written at, for structures whose payload layout depends on it.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// The stream does not hold a payload of this format, holds a format version
        /// the structure never defined (a later one included), holds a different
        /// structure, or has been corrupted.
        /// </exception>
        internal static byte[] Read(
            Stream stream, StructureId expected, out HashId hash, out ushort version,
            ReadBudget budget)
        {
            var header = ReadExactly(stream, HeaderLength, "header");

            if (!header.AsSpan(0, MagicLength).SequenceEqual(Magic))
            {
                throw new InvalidDataException(
                    "The stream does not begin with a probabilistic data structure " +
                    "payload; its first bytes are not the expected marker.");
            }

            version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));

            // Before the structure is compared, as it always was: a version this library
            // has never heard of may lay the header out differently, and then there is
            // no structure id to trust.
            if (version == 0 || version > MaxSupportedVersion)
            {
                throw RefuseVersion(expected, version);
            }

            var structure = (StructureId)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
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
                throw RefuseVersion(expected, version);
            }

            hash = (HashId)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));

            var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(10));
            if (length > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"Payload claims a length of {length} bytes, which cannot be read " +
                    "into a single array.");
            }

            // The payload is the first thing a header's claim allocates, and it is
            // charged before it is: a header claiming two gigabytes is refused on that
            // claim, not after the read has made room for it. The header and checksum
            // buffers and the checksum's state come with it.
            budget.Charge(EnvelopeCost(length));

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

            return payload;
        }

        /// <summary>
        /// The refusal for a format version the structure being read does not define,
        /// which names the structure, the version it was handed and the versions it
        /// does read.
        /// </summary>
        private static InvalidDataException RefuseVersion(StructureId expected, ushort version)
        {
            var reads = DescribeVersions(ReadableVersions(expected));

            var why = version == 0
                ? "Version 0 is reserved and never written."
                : version > MaxSupportedVersion
                    ? "It was written by a later version of this library."
                    : $"{expected} has never been written at that version, so the " +
                      "header does not describe a payload this library produced.";

            return new InvalidDataException(
                $"Payload is format version {version}, and {expected} is read at " +
                $"{reads}. {why}");
        }

        /// <summary>
        /// Returns the hash function an identifier names, or null if the identifier
        /// names one this library cannot supply.
        /// </summary>
        internal static Func<ReadOnlySpan<byte>, ulong>? Resolve(HashId hash)
        {
            return hash switch
            {
                HashId.XxHash3_64 => Defaults.GetDefaultHashFunction(),
                _ => null,
            };
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

            var resolved = Resolve(hash);
            if (resolved is not null)
            {
                return resolved;
            }

            if (hash == HashId.Custom)
            {
                throw new InvalidDataException(
                    "This structure was written while using a hash function set through " +
                    "SetHash, which cannot be recorded. Read it with the overload that " +
                    "takes a hash function, supplying the same one. Reading it with the " +
                    "default would not fail: the structure would answer no to everything " +
                    "and look empty rather than wrong.");
            }

            throw new InvalidDataException(
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
        internal static void WriteBuckets(PayloadWriter payload, Buckets buckets)
        {
            payload.WriteUInt32(buckets.count);
            payload.WriteByte(buckets.BucketSize);
            payload.WriteBytes(buckets.RawData);
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
            var arrayCount = reader.ReadUInt32();

            if (arrayCount > MaxNestedCount)
            {
                throw new InvalidDataException(
                    $"Bucket data claims {arrayCount} arrays, beyond anything this " +
                    "library builds.");
            }

            reader.Budget.Charge(ReadBudget.Sum(
                ReadBudget.ArrayOf(arrayCount, sizeof(long)), ReadBudget.SmallObject));
            var arrays = new byte[arrayCount][];
            for (uint i = 0; i < arrayCount; i++)
            {
                arrays[i] = reader.ReadBytes();
            }

            return Buckets64.Restore(count, bucketSize, arrays);
        }

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
        /// </remarks>
        internal static void WriteNested<T>(PayloadWriter payload, T structure)
            where T : IBinaryPersistable<T>
        {
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
            // it, and the nested envelope then charges its own payload.
            reader.Budget.Charge(NestedReadCost);
            var options = new ReadOptions(reader.Budget);
            return hash is null ? T.ReadFrom(buffer, options) : T.ReadFrom(buffer, hash, options);
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
        internal static long EnvelopeCost(uint length) => ReadBudget.Sum(
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
                    throw new InvalidDataException(
                        $"The stream ended after {read} of the {count} bytes the " +
                        $"{what} needs. The data has been truncated.");
                }
                read += n;
            }

            return buffer;
        }

        private static string Describe(StructureId structure)
        {
            return Enum.IsDefined(structure)
                ? structure.ToString()
                : $"structure of unknown type {(ushort)structure}";
        }
    }
}
