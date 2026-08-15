using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;

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
        /// The highest format version this library can read. Readers refuse anything
        /// above it, since a later version may mean the payload differently.
        /// </summary>
        internal const ushort MaxSupportedVersion = 2;

        /// <summary>
        /// The version a payload is written at unless its structure asks for another.
        /// </summary>
        /// <remarks>
        /// The version travels in each payload rather than being a property of the
        /// library, so a structure whose layout changes bumps only its own. Version 2
        /// exists for the two filters that gained a stored random-generator state in
        /// 6.0.0; the other eleven still write version 1 and stay readable by 3.x
        /// onwards. Raising all of them together would have made every payload
        /// unreadable to older versions to record a change that eleven of them did not
        /// have.
        /// </remarks>
        internal const ushort DefaultVersion = 1;

        /// <summary>
        /// The version at which <see cref="StableBloomFilter"/> and
        /// <see cref="CuckooBloomFilter"/> began storing their random generator's state,
        /// so that a restored filter resumes its draw sequence rather than restarting it.
        /// </summary>
        internal const ushort RandomStateVersion = 2;

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
        /// The stream does not hold a payload of this format, holds a later version of
        /// it, holds a different structure, or has been corrupted.
        /// </exception>
        internal static byte[] Read(Stream stream, StructureId expected, out HashId hash)
        {
            return Read(stream, expected, out hash, out _);
        }

        /// <summary>
        /// Reads and validates an envelope, also reporting the format version it was
        /// written at, for structures whose payload layout depends on it.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// The stream does not hold a payload of this format, holds a later version of
        /// it, holds a different structure, or has been corrupted.
        /// </exception>
        internal static byte[] Read(
            Stream stream, StructureId expected, out HashId hash, out ushort version)
        {
            var header = ReadExactly(stream, HeaderLength, "header");

            if (!header.AsSpan(0, MagicLength).SequenceEqual(Magic))
            {
                throw new InvalidDataException(
                    "The stream does not begin with a probabilistic data structure " +
                    "payload; its first bytes are not the expected marker.");
            }

            version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
            if (version == 0 || version > MaxSupportedVersion)
            {
                throw new InvalidDataException(
                    $"Payload is format version {version}, and this library reads up to " +
                    $"version {MaxSupportedVersion}. It was written by a later version.");
            }

            var structure = (StructureId)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
            if (structure != expected)
            {
                throw new InvalidDataException(
                    $"Payload holds a {Describe(structure)} and was read as a " +
                    $"{Describe(expected)}.");
            }

            hash = (HashId)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));

            var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(10));
            if (length > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"Payload claims a length of {length} bytes, which cannot be read " +
                    "into a single array.");
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

            return payload;
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

            return hash is null ? T.ReadFrom(buffer) : T.ReadFrom(buffer, hash);
        }

        /// <summary>
        /// A ceiling on counts read from a payload before anything is allocated for
        /// them, so that a corrupted length cannot ask for an enormous allocation
        /// before the read that would have failed gets a chance to.
        /// </summary>
        internal const uint MaxNestedCount = 1_000_000;

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
