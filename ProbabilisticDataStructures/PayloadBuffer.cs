using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Builds a payload as a growable little-endian byte buffer, counts one, or streams one.
    /// </summary>
    /// <remarks>
    /// Little-endian throughout, and explicitly so, because the layout is a file format
    /// rather than a memory dump: a payload written on one machine has to read the same
    /// on another. BinaryPrimitives is used in preference to BitConverter for that
    /// reason, since BitConverter follows whatever the running architecture does.
    /// <para>
    /// A writer from <see cref="Counter"/> stores nothing. It adds up, in 64 bits, how long
    /// the same writes would make the payload, so a structure's body run through it is
    /// sized by its own layout, with nothing built, before a writer that stores it is
    /// allocated (<see cref="IPayloadBody"/>). A run whose length is known before its bytes
    /// are, such as a table held in some other form than bytes, is begun with
    /// <see cref="BeginRun"/>: counted whole there, it costs the count nothing however long
    /// it is.
    /// </para>
    /// <para>
    /// A writer from <see cref="Streaming"/> holds a buffer of
    /// <see cref="StreamBufferLength"/> bytes and no more. It empties the buffer into a
    /// stream whenever the next write would not fit, and appends each piece to a running
    /// checksum as it goes, so that a payload in the large form of the envelope, which can
    /// be longer than any array, is written without being held (#225). Its runs' lengths
    /// are the large form's <c>u64</c>s (#228), and a nested envelope is written into it as
    /// it is produced, with that envelope's own checksum carried over its payload beside
    /// the outer one. A run longer than the buffer goes to the stream as it is, after what
    /// the buffer holds. The writer is
    /// given the length the count declared in the header already written, and refuses a
    /// write that would take the payload past it, at that write, which is not made.
    /// </para>
    /// </remarks>
    internal sealed class PayloadWriter
    {
        /// <summary>
        /// How much of a payload a streaming writer holds before it empties the buffer into
        /// the stream: small against any payload the large form is written for, and under
        /// the 85,000 bytes from which an array goes on the large object heap.
        /// </summary>
        internal const int StreamBufferLength = 64 * 1024;

        /// <summary>
        /// A smaller buffer for the streaming writers made on the calling thread, so that
        /// the suite can cut a payload into many pieces; null otherwise, as it is in every
        /// thread but a test's. At least eight bytes, the widest field.
        /// </summary>
        [ThreadStatic]
        internal static int? LoweredStreamBufferLength;

        /// <summary>
        /// A lower bound for <see cref="LongestRun"/> on the calling thread, so that the
        /// suite can show a run refused at lengths it can build; null otherwise.
        /// </summary>
        [ThreadStatic]
        internal static long? LoweredLongestRun;

        /// <summary>
        /// The longest run a payload in the ordinary envelope can hold:
        /// <see cref="PayloadReader.LongestRun"/>, the largest array, which is the most a
        /// reader of a held payload takes a run into, unless the suite has lowered it on this
        /// thread.
        /// </summary>
        /// <remarks>
        /// A run's length prefix there is a <c>u32</c>, and a run past this would be written
        /// with a length its reader refuses, or, from 2^32 bytes, a length that had wrapped,
        /// which no check downstream would see: the count and the write agree on the bytes,
        /// and the checksum covers the wrong length as faithfully as the right one. So a run
        /// past it is refused by the count of an ordinary payload, before anything is written.
        /// An ordinary payload is shorter than this anyway unless the suite has lowered it. In
        /// the large form a run's length is a <c>u64</c> (#228), and this does not bound it:
        /// there a run is bounded by what its reader can price and its structure can hold.
        /// </remarks>
        internal static long LongestRun => LoweredLongestRun ?? PayloadReader.LongestRun;

        private byte[] buffer;
        private int length;

        // Whether a run's length is written as a u64, as it is in the large form, or as the
        // ordinary envelope's u32: the form of the envelope being written, which a nested
        // one sets for its own payload and puts back.
        private bool wide;

        // A counter's: what the writes so far would have written, and the longest run among
        // them. Its buffer is the scratch a field is written into to be counted, and nothing
        // reads it.
        private readonly bool counting;
        private long counted;
        private long longest;

        // The checksums of the nested envelopes being written, innermost last, and how much
        // of the buffer has been appended to them: a nested envelope's checksum covers the
        // bytes of its own payload, wherever in the outer one they fall.
        private List<Crc32>? nested;
        private int nestedFrom;

        // A streaming writer's: the stream its buffer is emptied into, the running checksum
        // each piece is appended to on the way, the header and length the count gave, the
        // second already declared in the stream, and how many bytes of the payload have gone
        // to the stream. Its buffer holds the bytes written since it was last emptied.
        private readonly Stream? sink;
        private readonly Crc32? crc;
        private readonly EnvelopeHeader header;
        private readonly long declared;
        private long flushed;

        internal PayloadWriter()
            : this(256)
        {
        }

        /// <summary>
        /// A writer whose buffer starts at <paramref name="capacity"/> bytes: a payload's
        /// counted length, so that it is allocated once, at the size it ends at.
        /// </summary>
        internal PayloadWriter(int capacity)
        {
            this.buffer = new byte[capacity];
        }

        private PayloadWriter(bool counting, bool wide)
        {
            this.buffer = new byte[sizeof(ulong)];
            this.counting = counting;
            this.wide = wide;
        }

        private PayloadWriter(Stream sink, Crc32 crc, EnvelopeHeader header, long declared)
        {
            this.buffer = new byte[LoweredStreamBufferLength ?? StreamBufferLength];
            this.sink = sink;
            this.crc = crc;
            this.header = header;
            this.declared = declared;
            this.wide = true;
        }

        /// <summary>
        /// A writer that stores nothing and counts what it is given, with a run's length the
        /// <c>u64</c> of the large form when <paramref name="wide"/> and the ordinary
        /// envelope's <c>u32</c> otherwise.
        /// </summary>
        internal static PayloadWriter Counter(bool wide = false) => new(counting: true, wide);

        /// <summary>
        /// A writer that writes what it is given to <paramref name="sink"/>, a buffer at a
        /// time, appending every byte to <paramref name="crc"/> as it goes. It writes the
        /// large form, so a run's length is a <c>u64</c>.
        /// </summary>
        /// <param name="sink">The stream the payload is written to, after its header.</param>
        /// <param name="crc">The running checksum the header began.</param>
        /// <param name="header">What the count said the body writes, for a refusal to name.</param>
        /// <param name="declared">The payload's counted length, declared in the header.</param>
        internal static PayloadWriter Streaming(Stream sink, Crc32 crc, EnvelopeHeader header, long declared) =>
            new(sink, crc, header, declared);

        /// <summary>
        /// Whether this writer counts rather than stores.
        /// </summary>
        internal bool IsCounting => this.counting;

        /// <summary>
        /// Whether a run's length is written as the large form's <c>u64</c>, rather than as
        /// the ordinary envelope's <c>u32</c>: set for the payload of a nested envelope by the
        /// form that envelope is in, and put back after it.
        /// </summary>
        internal bool IsWide
        {
            get => this.wide;
            set => this.wide = value;
        }

        /// <summary>The longest run a counter has counted.</summary>
        internal long LongestCounted => this.longest;

        /// <summary>
        /// How long the payload is: the bytes written, or, for a counter, the bytes the
        /// same writes would have written.
        /// </summary>
        internal long Length => this.counting ? this.counted : this.flushed + this.length;

        /// <summary>
        /// Puts a counter's count back to <paramref name="length"/>, what it was before a
        /// nested body was counted into it to measure that body.
        /// </summary>
        internal void Rewind(long length)
        {
            this.counted = length;
        }

        /// <summary>
        /// How many bytes of the payload a streaming writer has written to its stream.
        /// </summary>
        internal long Streamed => this.flushed;

        internal ReadOnlySpan<byte> WrittenSpan => this.buffer.AsSpan(0, this.length);

        internal void WriteByte(byte value)
        {
            Reserve(1)[0] = value;
        }

        internal void WriteUInt16(ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value);
        }

        internal void WriteUInt32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);
        }

        internal void WriteUInt64(ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), value);
        }

        internal void WriteInt64(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);
        }

        internal void WriteDouble(double value)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(Reserve(8), value);
        }

        /// <summary>
        /// Writes a length-prefixed run of bytes.
        /// </summary>
        internal void WriteBytes(ReadOnlySpan<byte> value)
        {
            if (BeginRun(value.Length))
            {
                Append(value);
            }
        }

        /// <summary>
        /// Writes bytes as they are, with no length prefix: the bytes of a run begun with
        /// <see cref="BeginRun"/>, or of a block begun with <see cref="BeginBlock"/>.
        /// </summary>
        internal void WriteRaw(ReadOnlySpan<byte> value)
        {
            if (BeginBlock(value.Length))
            {
                Append(value);
            }
        }

        /// <summary>
        /// Writes the length prefix of a run whose bytes the caller then writes itself,
        /// and says whether to write them.
        /// </summary>
        /// <param name="length">How many bytes the run holds, known before any is written.</param>
        /// <returns>
        /// False from a counter, which has counted the run whole here, its prefix and its
        /// bytes, so that the caller skips them and the count builds nothing however long
        /// the run is. True otherwise, and the caller then writes exactly
        /// <paramref name="length"/> bytes.
        /// </returns>
        /// <remarks>
        /// The length is taken in 64 bits so that a counter can count a run longer than
        /// a payload holds, which is how such a payload comes to be refused naming its
        /// whole size. The prefix is a <c>u64</c> in the large form and a <c>u32</c> in the
        /// ordinary envelope (#228). A counter keeps the longest run it counts, which the
        /// count of an ordinary payload holds to <see cref="LongestRun"/>, before anything is
        /// written; a payload that would need a longer run is past the ordinary envelope, and
        /// in the large form, unless the bound has been lowered. So a <c>u32</c> prefix is
        /// only ever written for a run that fits one.
        /// </remarks>
        internal bool BeginRun(long length)
        {
            if (this.counting)
            {
                this.longest = Math.Max(this.longest, length);
            }

            if (this.wide)
            {
                WriteUInt64((ulong)length);
            }
            else
            {
                WriteUInt32((uint)length);
            }

            return BeginBlock(length);
        }

        /// <summary>
        /// As <see cref="BeginRun"/>, for bytes with no length prefix before them: fields
        /// whose total length is known before they are written.
        /// </summary>
        internal bool BeginBlock(long length)
        {
            if (this.counting)
            {
                Count(length);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Refuses a run longer than a payload in the ordinary envelope can hold, naming its
        /// length, before anything is written.
        /// </summary>
        /// <param name="length">The run's length, in bytes, without its prefix.</param>
        /// <exception cref="InvalidOperationException">
        /// The run is longer than <see cref="LongestRun"/>.
        /// </exception>
        internal static void CheckRun(long length)
        {
            if (length > LongestRun)
            {
                throw new InvalidOperationException(
                    $"The payload would hold a run of {length:N0} bytes, and no single run in a " +
                    $"payload in the ordinary envelope can be longer than {LongestRun:N0} bytes, " +
                    "the most a reader takes into one array. The structure is intact and can " +
                    "still be used, but it is too large to write in that envelope. Keep less in " +
                    "it, or split what it holds across several.");
            }
        }

        /// <summary>
        /// Begins carrying <paramref name="checksum"/> over every byte written from here,
        /// until <see cref="EndChecksum"/>: the checksum of a nested envelope, over its
        /// payload, which is written into this one (#228).
        /// </summary>
        internal void BeginChecksum(Crc32 checksum)
        {
            this.SettleChecksums();
            (this.nested ??= new List<Crc32>()).Add(checksum);
        }

        /// <summary>
        /// Stops carrying the innermost checksum <see cref="BeginChecksum"/> began, once every
        /// byte written since has been appended to it.
        /// </summary>
        internal void EndChecksum()
        {
            this.SettleChecksums();
            this.nested!.RemoveAt(this.nested.Count - 1);
        }

        /// <summary>
        /// Appends what has been written to the buffer since the last time to every nested
        /// checksum being carried: before the buffer is emptied, and when one begins or ends.
        /// </summary>
        private void SettleChecksums()
        {
            if (this.nested is { Count: > 0 })
            {
                var written = this.buffer.AsSpan(this.nestedFrom, this.length - this.nestedFrom);
                foreach (var checksum in this.nested)
                {
                    checksum.Append(written);
                }
            }

            this.nestedFrom = this.length;
        }

        /// <summary>
        /// Writes out what a streaming writer still holds. It is the payload's last piece
        /// once the body has written the whole of it.
        /// </summary>
        internal void Flush()
        {
            if (this.length > 0)
            {
                this.SettleChecksums();
                Emit(this.buffer.AsSpan(0, this.length));
                this.length = 0;
                this.nestedFrom = 0;
            }
        }

        /// <summary>
        /// How long the buffer should be once it has to hold <paramref name="count"/> more
        /// bytes after <paramref name="length"/> that are already written: the length it
        /// has if that is enough; otherwise double it, so that growing is amortised, or
        /// exactly enough if doubling is not, and never more than a payload can be.
        /// </summary>
        /// <param name="capacity">The buffer's present length.</param>
        /// <param name="length">How many bytes have been written.</param>
        /// <param name="count">How many more are about to be.</param>
        /// <exception cref="InvalidOperationException">
        /// The payload would be longer than one array holds with its envelope.
        /// </exception>
        /// <remarks>
        /// All of it in 64 bits. In 32, doubling wrapped negative from a gibibyte up and
        /// the buffer was left exactly full, so that each write after it copied the whole
        /// payload; and the sum of the length and the write wrapped too, so a payload
        /// past the largest array failed in whatever way a negative length fails. Doubling
        /// is clamped to the largest payload, so that the buffer reaches it in one copy and
        /// every write up to it after that costs nothing. This buffer holds a payload whole,
        /// so it keeps the ceiling one array sets; a payload past it is streamed instead.
        /// </remarks>
        internal static int GrownCapacity(int capacity, int length, int count)
        {
            var needed = (long)length + count;
            var largest = PersistenceFormat.MaxPayloadLength;

            PersistenceFormat.CheckPayloadLength(needed);

            if (needed <= capacity)
            {
                return capacity;
            }

            return (int)Math.Max(needed, Math.Min(2L * capacity, largest));
        }

        /// <summary>
        /// Adds <paramref name="count"/> bytes to a counter's length, refusing a payload
        /// longer than a stream holds, whose length no envelope could declare.
        /// </summary>
        private void Count(long count)
        {
            if (count > long.MaxValue - this.counted)
            {
                throw new InvalidOperationException(
                    $"The payload would be longer than {long.MaxValue:N0} bytes, more than a " +
                    "stream can hold, so no envelope could declare its length. The structure is " +
                    "intact and can still be used, but it is too large to write.");
            }

            this.counted += count;
        }

        private Span<byte> Reserve(int count)
        {
            if (this.counting)
            {
                // Only a field reaches here from a counter, so the scratch holds it.
                Count(count);
                return this.buffer.AsSpan(0, count);
            }

            if (this.sink is not null)
            {
                // Only a field reaches here from a streaming writer, so an emptied buffer
                // holds it.
                Admit(count);
                if (count > this.buffer.Length - this.length)
                {
                    Flush();
                }
            }
            else
            {
                var capacity = GrownCapacity(this.buffer.Length, this.length, count);
                if (capacity != this.buffer.Length)
                {
                    var grown = new byte[capacity];
                    this.buffer.AsSpan(0, this.length).CopyTo(grown);
                    this.buffer = grown;
                }
            }

            var span = this.buffer.AsSpan(this.length, count);
            this.length += count;
            return span;
        }

        /// <summary>
        /// Writes bytes whose run or block has been begun: into the buffer, or, from a
        /// streaming writer, into the buffer if they fit it once it is emptied, and
        /// otherwise to the stream as they are, after what the buffer held.
        /// </summary>
        private void Append(ReadOnlySpan<byte> value)
        {
            if (this.sink is null)
            {
                value.CopyTo(Reserve(value.Length));
                return;
            }

            Admit(value.Length);
            if (value.Length > this.buffer.Length - this.length)
            {
                Flush();
                if (value.Length > this.buffer.Length)
                {
                    // Past the buffer, so the nested checksums take it here rather than from it.
                    if (this.nested is not null)
                    {
                        foreach (var checksum in this.nested)
                        {
                            checksum.Append(value);
                        }
                    }

                    Emit(value);
                    return;
                }
            }

            value.CopyTo(this.buffer.AsSpan(this.length));
            this.length += value.Length;
        }

        /// <summary>
        /// Refuses a write that would take a streamed payload past the length its header has
        /// declared, before the write is made.
        /// </summary>
        private void Admit(long count)
        {
            if (this.Length + count > this.declared)
            {
                throw PersistenceFormat.WrotePastItsCount(this.header, this.declared, this.Length + count, this.flushed);
            }
        }

        /// <summary>
        /// Writes one piece of a streamed payload, appending it to the running checksum.
        /// </summary>
        private void Emit(ReadOnlySpan<byte> piece)
        {
            this.crc!.Append(piece);
            this.sink!.Write(piece);
            this.flushed += piece.Length;
        }
    }


    /// <summary>
    /// Reads a payload written by <see cref="PayloadWriter"/>, refusing to run off the
    /// end of it.
    /// </summary>
    /// <remarks>
    /// Every read is bounds-checked against the payload rather than trusted, because a
    /// payload is data from outside the process even when the envelope's checksum
    /// matched: a well-formed file can still hold a length that does not describe what
    /// follows it.
    /// <para>
    /// The payload is either held, a span over the array it was read into, or read from its
    /// stream through a <see cref="PayloadSource"/>: by a price of a payload too large for
    /// its budget to be held, and by both passes over a payload in the large form, which is
    /// never held (#226). Read from its stream, the offsets and what remains are 64-bit,
    /// since such a payload can be longer than any array.
    /// </para>
    /// </remarks>
    internal ref struct PayloadReader
    {
        /// <summary>
        /// The longest run a payload can hold, which is the largest array, since a run is
        /// read into one. <see cref="PayloadWriter.LongestRun"/> is held to it.
        /// </summary>
        internal static int LongestRun => Array.MaxLength;

        private readonly ReadOnlySpan<byte> payload;
        private int offset;

        // Set when the payload is read from the stream instead of from memory. The span is
        // then empty, the source's position stands for the offset, and this reader's
        // payload ends at the source position given.
        private readonly PayloadSource? source;
        private readonly long start;
        private readonly long end;

        // Whether a run's length is the large form's u64 rather than the ordinary envelope's
        // u32 (#228), and whether the read this reader belongs to holds no payload, as the
        // large form's does not: its runs of words are read in pieces and its nested
        // envelopes from this reader. The second holds for a nested envelope's payload, in
        // either form, read inside the large form, and not for an ordinary payload priced
        // from its stream, whose read holds it.
        private readonly bool wide;
        private readonly bool streamed;

        // The run of words begun and not yet read to its end: the bytes left of it, and,
        // from a held payload, the copy of it, or from a stream, the buffer a piece is read
        // into.
        private long wordsLeft;
        private byte[]? words;

        internal PayloadReader(ReadOnlySpan<byte> payload, ReadBudget budget, bool wide = false)
        {
            this.payload = payload;
            this.offset = 0;
            this.source = null;
            this.start = 0;
            this.end = 0;
            this.wide = wide;
            this.streamed = false;
            this.wordsLeft = 0;
            this.words = null;
            this.Budget = budget;
        }

        /// <summary>
        /// A reader of the next <paramref name="length"/> bytes of a payload that is not
        /// held: one that prices it, reading fields and skipping runs, or one that reads it,
        /// reading each run from the stream into the array it is returned in.
        /// </summary>
        /// <param name="source">The stream the payload is read from.</param>
        /// <param name="length">How many of its bytes are this reader's.</param>
        /// <param name="budget">The allowance the read draws on.</param>
        /// <param name="wide">Whether a run's length is a <c>u64</c>: the payload is in the large form.</param>
        /// <param name="streamed">
        /// Whether the read holds no payload, as the large form's does not, so that what it
        /// reads is read, and priced, as a read from the stream: false for the price of an
        /// ordinary payload, whose read holds it.
        /// </param>
        internal PayloadReader(PayloadSource source, long length, ReadBudget budget, bool wide = false, bool streamed = false)
        {
            this.payload = default;
            this.offset = 0;
            this.source = source;
            this.start = source.Position;
            this.end = source.Position + length;
            this.wide = wide;
            this.streamed = streamed;
            this.wordsLeft = 0;
            this.words = null;
            this.Budget = budget;
        }

        /// <summary>
        /// Whether the read this reader belongs to holds no payload, as the large form's does
        /// not: a nested envelope in it is read from this reader, rather than copied out.
        /// </summary>
        internal bool IsStreamed => this.streamed;

        /// <summary>
        /// The allowance the read this payload belongs to draws on, so that code handed
        /// only the reader -- a nested structure, a body shared between two readers --
        /// charges the same one.
        /// </summary>
        internal ReadBudget Budget { get; }

        internal byte ReadByte()
        {
            return Next(1)[0];
        }

        internal ushort ReadUInt16()
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(Next(2));
        }

        internal uint ReadUInt32()
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(Next(4));
        }

        internal ulong ReadUInt64()
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(Next(8));
        }

        internal double ReadDouble()
        {
            return BinaryPrimitives.ReadDoubleLittleEndian(Next(8));
        }

        /// <summary>
        /// Reads a length-prefixed run of bytes into an array of its own, which the caller
        /// keeps: copied out of a held payload, and from a payload read from its stream, read
        /// into it straight from the stream, so that nothing holds the run twice.
        /// </summary>
        internal byte[] ReadBytes()
        {
            var count = ReadArrayRunLength();

            // Bounds-checked first, so a run the payload does not hold is refused as
            // truncated; then the array is charged before it is made.
            if (this.source is null)
            {
                var run = Take(count);
                this.Budget.Charge(ReadBudget.ArrayOf((ulong)count, 1));
                return run.ToArray();
            }

            RequireAhead(count);
            this.Budget.Charge(ReadBudget.ArrayOf((ulong)count, 1));
            var bytes = new byte[count];
            this.source.ReadRun(bytes);
            return bytes;
        }

        /// <summary>
        /// Reads a run's length prefix: the one place a run's length is read, by a read and
        /// by a price, whatever the run is read into. A <c>u32</c> in the ordinary envelope,
        /// and a <c>u64</c> in the large form (#228).
        /// </summary>
        private ulong ReadRunLength() => this.wide ? ReadUInt64() : ReadUInt32();

        /// <summary>
        /// Reads the length prefix of a run that is read into one array, refusing a length
        /// no array holds.
        /// </summary>
        /// <remarks>
        /// Past the largest array, not past the largest <c>int</c>. Between them, a held
        /// payload could never hold the run, so the bounds check refused it; a payload read
        /// from its stream can, and the run would then have passed both checks and asked
        /// for an array no runtime makes. A run its structure keeps in storage of its own is
        /// not read into one array, and is not held to one (<see cref="BeginWords"/>).
        /// </remarks>
        private int ReadArrayRunLength()
        {
            var count = ReadRunLength();
            if (count > (ulong)LongestRun)
            {
                throw new InvalidDataException(
                    $"Payload holds a {count}-byte run, which cannot be read into a " +
                    "single array.");
            }

            return (int)count;
        }

        /// <summary>
        /// Reads the length prefix of a run that is not read into one array, held to what
        /// the payload has left rather than to an array.
        /// </summary>
        private long ReadHeldRunLength()
        {
            var count = ReadRunLength();
            if (count > (ulong)this.Remaining)
            {
                throw Ended(count);
            }

            return (long)count;
        }

        /// <summary>
        /// Prices what <see cref="ReadBytes"/> allocates for the run that comes next, after
        /// the same checks, and leaves the reader at the start of the run. Returns its
        /// length.
        /// </summary>
        internal int PriceBytes()
        {
            var count = ReadArrayRunLength();
            RequireAhead(count);
            this.Budget.Price(ReadBudget.ArrayOf((ulong)count, 1));
            return count;
        }

        /// <summary>
        /// Begins a run of eight-byte words that its structure unpacks into storage of its
        /// own, rather than keeping the run as the array it is read into, and returns the
        /// run's length; <see cref="ReadWords"/> then hands it back a piece at a time.
        /// </summary>
        /// <remarks>
        /// A run past one array, which the large form allows (#228), cannot be read into one,
        /// and a structure that keeps its run in words, or in more arrays than one, can hold
        /// it. So a read that holds no payload takes such a run from the stream in pieces of
        /// at most <see cref="PayloadSource.PieceBytes"/>, through one buffer of that length or
        /// the run's, whichever is shorter, charged here. From a held payload the run is
        /// copied out whole, as <see cref="ReadBytes"/> copies a run, and handed back as one
        /// piece, so that the ordinary envelope's read allocates and is charged what it always
        /// was; a run there is never past one array.
        /// </remarks>
        internal long BeginWords()
        {
            if (!this.streamed)
            {
                this.words = ReadBytes();
                this.wordsLeft = this.words.Length;
                return this.wordsLeft;
            }

            var count = ReadHeldRunLength();
            var piece = PieceFor(count);
            this.Budget.Charge(ReadBudget.ArrayOf((ulong)piece, 1));
            this.words = piece == 0 ? Array.Empty<byte>() : new byte[piece];
            this.wordsLeft = count;
            return count;
        }

        /// <summary>
        /// The next piece of the run <see cref="BeginWords"/> began, a whole number of words
        /// when the run is, until it has all been handed back; valid until the next call.
        /// </summary>
        internal ReadOnlySpan<byte> ReadWords()
        {
            var words = this.words!;
            if (!this.streamed)
            {
                var held = words.AsSpan(words.Length - (int)this.wordsLeft);
                this.wordsLeft = 0;
                return held;
            }

            var count = (int)Math.Min(this.wordsLeft, words.Length);
            this.source!.ReadPiece(words, count);
            this.wordsLeft -= count;
            return words.AsSpan(0, count);
        }

        /// <summary>
        /// Prices what <see cref="BeginWords"/> allocates for the run that comes next, after
        /// the same checks, and skips it. Returns its length.
        /// </summary>
        internal long SkipWords()
        {
            if (!this.streamed)
            {
                return SkipBytes();
            }

            var count = ReadHeldRunLength();
            this.Budget.Price(ReadBudget.ArrayOf((ulong)PieceFor(count), 1));
            Skip(count);
            return count;
        }

        /// <summary>The buffer a run of <paramref name="count"/> bytes is read through in pieces.</summary>
        private static int PieceFor(long count) => (int)Math.Min(count, PayloadSource.PieceBytes);

        /// <summary>
        /// The buffer, on its caller's stack, a block (<see cref="ReadBlock"/>) of
        /// <paramref name="count"/> bytes is read through in pieces: no longer than the block,
        /// as a run's is no longer than the run, and no longer than
        /// <see cref="PayloadSource.BlockPieceBytes"/>.
        /// </summary>
        internal static int BlockPieceFor(long count) => (int)Math.Min(count, PayloadSource.BlockPieceBytes);

        /// <summary>
        /// Reads the next <c>piece.Length</c> bytes into <paramref name="piece"/>: a piece of a
        /// block of eight-byte words with no length before it, which its structure unpacks into
        /// storage of its own (#289), copied from a held payload, and read from the stream into
        /// it from one that is not.
        /// </summary>
        /// <remarks>
        /// As <see cref="ReadWords"/> is for a run, with nothing allocated: the piece is the
        /// caller's, on its stack, no longer than <see cref="BlockPieceFor"/> says. So a block read
        /// this way allocates, and is charged, what it did read a word at a time, and each piece
        /// is held to what the payload has left as each word was.
        /// </remarks>
        internal void ReadBlock(scoped Span<byte> piece)
        {
            if (this.source is null)
            {
                Take(piece.Length).CopyTo(piece);
                return;
            }

            RequireAhead(piece.Length);
            this.source.ReadPiece(piece);
        }

        /// <summary>
        /// Reads the length prefix of a run that holds a nested envelope, which a read that
        /// holds no payload reads from this reader rather than copying it out, held to what
        /// the payload has left. Returns its length.
        /// </summary>
        internal long BeginEnvelopeRun() => ReadHeldRunLength();

        /// <summary>
        /// Begins carrying <paramref name="checksum"/> over every byte this reader's stream
        /// gives from here, until <see cref="EndChecksum"/>: a nested envelope's checksum, over
        /// its payload, read from the outer stream.
        /// </summary>
        internal void BeginChecksum(Crc32 checksum) => this.source!.BeginChecksum(checksum);

        /// <summary>Stops carrying the checksum <see cref="BeginChecksum"/> began last.</summary>
        internal void EndChecksum() => this.source!.EndChecksum();

        /// <summary>
        /// Prices what <see cref="ReadBytes"/> allocates for the run that comes next, and
        /// skips it. Returns its length.
        /// </summary>
        internal int SkipBytes()
        {
            var count = PriceBytes();
            Skip(count);
            return count;
        }

        /// <summary>
        /// Moves past <paramref name="count"/> bytes without reading them, refusing to run
        /// off the end as a read would.
        /// </summary>
        internal void Skip(long count)
        {
            RequireAhead(count);
            if (this.source is null)
            {
                this.offset += (int)count;
            }
            else
            {
                this.source.Skip(count);
            }
        }

        /// <summary>
        /// Moves past everything not yet read, so that the reader this one was sliced from
        /// goes on after it.
        /// </summary>
        internal void SkipRest()
        {
            Skip(Remaining);
        }

        /// <summary>
        /// Reads <paramref name="count"/> bytes in place, at most sixteen, for a field
        /// wider than the fixed ones -- a nested envelope's header.
        /// </summary>
        internal ReadOnlySpan<byte> ReadSpan(int count)
        {
            return Next(count);
        }

        /// <summary>
        /// A reader of the next <paramref name="length"/> bytes, drawing on the same
        /// budget, which this one is moved past once that reader has been read to its end
        /// with <see cref="SkipRest"/>.
        /// </summary>
        /// <remarks>
        /// Over a held payload this reader moves past the slice at once. Over one that is
        /// not held both read from the same stream, so this one follows wherever the slice
        /// leaves it, and the slice has to be skipped to its end before this one goes on.
        /// <para>
        /// The length is taken in 64 bits, as a nested large-form envelope claims its own,
        /// and held to what remains before it is narrowed.
        /// </para>
        /// </remarks>
        internal PayloadReader Slice(long length) => Slice(length, this.wide);

        /// <summary>
        /// As <see cref="Slice(long)"/>, for bytes whose runs have lengths as wide as
        /// <paramref name="wide"/> says: a nested envelope's payload, in its own form.
        /// </summary>
        internal PayloadReader Slice(long length, bool wide)
        {
            RequireAhead(length);
            if (this.source is null)
            {
                var slice = new PayloadReader(this.payload.Slice(this.offset, (int)length), this.Budget, wide);
                this.offset += (int)length;
                return slice;
            }

            return new PayloadReader(this.source, length, this.Budget, wide, this.streamed);
        }

        /// <summary>
        /// The bytes not yet read: in 64 bits, as a payload read from its stream can be longer
        /// than an <c>int</c> counts.
        /// </summary>
        internal long Remaining => this.source is null
            ? this.payload.Length - this.offset
            : this.end - this.source.Position;

        /// <summary>How far into its payload the reader is.</summary>
        private long Offset => this.source is null ? this.offset : this.source.Position - this.start;

        /// <summary>How long the reader's payload is.</summary>
        private long Length => this.source is null ? this.payload.Length : this.end - this.start;

        /// <summary>
        /// Refuses to go <paramref name="count"/> bytes further than the payload goes, as
        /// a read past its end is refused.
        /// </summary>
        private void RequireAhead(long count)
        {
            if (count < 0 || count > this.Remaining)
            {
                throw new InvalidDataException(EndedMessage(count.ToString()));
            }
        }

        /// <summary>The refusal of a claim to <paramref name="count"/> more bytes than remain.</summary>
        private InvalidDataException Ended(ulong count) => new(EndedMessage(count.ToString()));

        private string EndedMessage(string count) =>
            $"Payload ended after {this.Length} bytes while reading " +
            $"{count} more at offset {this.Offset}. It is truncated or does not " +
            "have the shape this structure expects.";

        /// <summary>
        /// Refuses a count that the rest of the payload could not hold, before anything
        /// is allocated for it.
        /// </summary>
        /// <param name="count">The count the payload claims.</param>
        /// <param name="bytesEach">The fewest bytes each counted thing is written in.</param>
        /// <param name="what">What is being counted, for the message.</param>
        /// <remarks>
        /// A reader that allocates for a count before reading what it counts lets a few
        /// bytes ask for an enormous allocation, and the old defence was a fixed
        /// ceiling on the count -- which refused payloads this library had written,
        /// because its constructors built past it. Checking the count against the bytes
        /// actually present instead lets a reader accept every size its constructor
        /// builds, since a payload this library wrote holds everything it counts,
        /// while what the reader allocates still follows the payload's size rather than
        /// a claim inside it.
        /// </remarks>
        internal void RequireHolds(ulong count, int bytesEach, string what)
        {
            if (count > (ulong)(this.Remaining / bytesEach))
            {
                throw new InvalidDataException(
                    $"Payload claims {count} {what}, beyond what it can hold: only " +
                    $"{this.Remaining} bytes remain, and each takes at least " +
                    $"{bytesEach}. It is truncated or does not have the shape this " +
                    "structure expects.");
            }
        }

        /// <summary>
        /// Fails unless the whole payload has been consumed, which catches a reader and
        /// writer that disagree about the layout while the checksum still matches.
        /// </summary>
        internal void ExpectEnd()
        {
            if (this.Offset != this.Length)
            {
                throw new InvalidDataException(
                    $"Payload has {this.Length - this.Offset} bytes left over " +
                    $"after reading {this.Offset} of {this.Length}. It does not " +
                    "have the shape this structure expects.");
            }
        }

        /// <summary>
        /// The next <paramref name="count"/> bytes, from the held payload or, when the
        /// payload is not held, from its stream.
        /// </summary>
        private ReadOnlySpan<byte> Next(int count)
        {
            if (this.source is null)
            {
                return Take(count);
            }

            RequireAhead(count);
            return this.source.Take(count);
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            // Against what remains, not offset + count against the length: that sum is
            // an int, and a run claiming within the offset of int.MaxValue wrapped it
            // negative, passed as short of the end, and failed in Slice with an
            // ArgumentOutOfRangeException rather than as the payload ending. The offset
            // never passes the length, so the subtraction cannot wrap.
            if (count < 0 || count > this.payload.Length - this.offset)
            {
                throw new InvalidDataException(
                    $"Payload ended after {this.payload.Length} bytes while reading " +
                    $"{count} more at offset {this.offset}. It is truncated or does not " +
                    "have the shape this structure expects.");
            }

            var span = this.payload.Slice(this.offset, count);
            this.offset += count;
            return span;
        }
    }

    /// <summary>
    /// A payload read from its stream rather than held: a field at a time, its runs skipped
    /// or read straight into the arrays that keep them.
    /// </summary>
    /// <remarks>
    /// Read this way by a price, to name the total of a payload too large for its read's
    /// budget to be held, which is refused whatever the total comes to, so how far into the
    /// stream it is left does not matter. And read this way twice over a payload in the
    /// large form, which is never held (#226): once by its price, which reads the fields the
    /// price depends on and seeks over the rest, and, after <see cref="Restart"/> has taken
    /// it back to the payload's first byte, once by its reader, which takes every byte in
    /// order and appends each to the payload's running checksum as it goes (#227). Runs are
    /// skipped by seeking where the stream can and by reading through a small buffer where
    /// it cannot, so the price of a payload of gigabytes costs a few hundred bytes to work
    /// out wherever its bulk is in runs.
    /// </remarks>
    internal sealed class PayloadSource
    {
        /// <summary>The widest field taken in place: a nested envelope's header.</summary>
        internal const int WidestField = 16;

        /// <summary>
        /// The most of a run a reader that unpacks it into storage of its own takes from the
        /// stream at a time (#228): a whole number of eight-byte words, and under the 85,000
        /// bytes from which an array goes on the large object heap.
        /// </summary>
        internal const int PieceLength = 64 * 1024;

        /// <summary>
        /// A shorter piece for the sources made on the calling thread, so that the suite can
        /// show a run, or a block, read in pieces at lengths it can build; null otherwise, as it is in
        /// every thread but a test's. A whole number of eight-byte words.
        /// </summary>
        [ThreadStatic]
        internal static int? LoweredPieceLength;

        /// <summary>
        /// The longest piece a run is read in on this thread: <see cref="PieceLength"/>, unless
        /// lowered. Either is a whole number of words, so that no word is cut between two
        /// pieces.
        /// </summary>
        internal static int PieceBytes => LoweredPieceLength ?? PieceLength;

        /// <summary>
        /// The most of a block of words with no length before it that a reader takes at a time
        /// (#289). The block is read into a buffer on the reader's stack, so this is kept far
        /// shorter than <see cref="PieceLength"/>, which is on the heap: it is the shortest
        /// length, in powers of two from 1 KiB to 64 KiB, at which a large count-min sketch
        /// read from a file stayed within a tenth of the time it took in 64 KiB pieces. A
        /// whole number of eight-byte words.
        /// </summary>
        internal const int BlockPieceLength = 16 * 1024;

        /// <summary>
        /// The longest piece a block is read in on this thread: <see cref="BlockPieceLength"/>,
        /// or the lowered piece where that is shorter, so that the suite shows a block read in
        /// pieces as it shows a run.
        /// </summary>
        internal static int BlockPieceBytes => Math.Min(PieceBytes, BlockPieceLength);

        private readonly Stream stream;
        private readonly byte[] field = new byte[WidestField];
        private byte[]? skipped;

        // Set once the read that follows a price has begun, and then every byte the source
        // takes is appended to it.
        private Crc32? checksum;

        // The checksums of the nested envelopes being read, innermost first, each appended
        // every byte taken while it is carried, as the payload's own is.
        private NestedChecksum? nested;

        /// <summary>A nested envelope's checksum, and the one around it, if it is nested in another.</summary>
        private sealed class NestedChecksum(Crc32 checksum, NestedChecksum? outer)
        {
            internal Crc32 Checksum { get; } = checksum;

            internal NestedChecksum? Outer { get; } = outer;
        }

        /// <summary>
        /// What carrying a nested envelope's checksum allocates: the checksum's state, and
        /// the link that carries it.
        /// </summary>
        internal static long NestedChecksumCost => ReadBudget.Sum(ReadBudget.SmallObject, ReadBudget.SmallObject);

        internal PayloadSource(Stream stream)
        {
            this.stream = stream;
        }

        /// <summary>How many of the payload's bytes have been read or skipped.</summary>
        internal long Position { get; private set; }

        /// <summary>
        /// Takes the source back to the payload's first byte, at <paramref name="start"/> in
        /// a stream that can seek, once a price has walked it, and from then on appends every
        /// byte it reads to <paramref name="crc"/>: the second of the large form's two passes.
        /// </summary>
        internal void Restart(long start, Crc32 crc)
        {
            this.stream.Position = start;
            this.Position = 0;
            this.checksum = crc;
        }

        /// <summary>
        /// The next <paramref name="count"/> bytes, at most <see cref="WidestField"/>,
        /// until the next call.
        /// </summary>
        /// <exception cref="InvalidDataException">The stream ends first.</exception>
        internal ReadOnlySpan<byte> Take(int count)
        {
            if (count > WidestField)
            {
                throw new InvalidOperationException(
                    $"A payload that is not held is read a field at a time, of at most " +
                    $"{WidestField} bytes, and {count} were asked for. A run is skipped, or " +
                    "read into an array of its own.");
            }

            var span = this.field.AsSpan(0, count);
            this.Fill(span);
            this.Taken(span);
            return span;
        }

        /// <summary>
        /// Begins carrying <paramref name="crc"/> over every byte taken from here, as the
        /// payload's own checksum is carried, until <see cref="EndChecksum"/>.
        /// </summary>
        internal void BeginChecksum(Crc32 crc) => this.nested = new NestedChecksum(crc, this.nested);

        /// <summary>Stops carrying the checksum <see cref="BeginChecksum"/> began last.</summary>
        internal void EndChecksum() => this.nested = this.nested!.Outer;

        /// <summary>
        /// Reads the next <paramref name="count"/> bytes into the start of
        /// <paramref name="piece"/>, through the stream's array overload of <c>Read</c>: a
        /// piece of a run its structure unpacks into storage of its own.
        /// </summary>
        /// <exception cref="InvalidDataException">The stream ends first.</exception>
        internal void ReadPiece(byte[] piece, int count)
        {
            for (var read = 0; read < count;)
            {
                var n = this.stream.Read(piece, read, count - read);
                if (n == 0)
                {
                    throw Truncated();
                }
                read += n;
            }

            this.Taken(piece.AsSpan(0, count));
        }

        /// <summary>
        /// Reads the next <c>piece.Length</c> bytes into <paramref name="piece"/>, a buffer on
        /// its reader's stack, through the stream's span overload of <c>Read</c>: a piece of a
        /// block of words with no length before it, which its structure unpacks into storage of
        /// its own (#289).
        /// </summary>
        /// <exception cref="InvalidDataException">The stream ends first.</exception>
        /// <remarks>
        /// A stream that implements only the array overload is handed a copy rented from the
        /// shared pool, as it is for a field: one as long as the piece, at most
        /// <see cref="BlockPieceBytes"/>, where a run read that way would rent one as long as the run
        /// (<see cref="ReadRun"/>).
        /// </remarks>
        internal void ReadPiece(scoped Span<byte> piece)
        {
            this.Fill(piece);
            this.Taken(piece);
        }

        /// <summary>
        /// Appends bytes just taken to every checksum being carried, and moves past them.
        /// </summary>
        private void Taken(ReadOnlySpan<byte> bytes)
        {
            this.checksum?.Append(bytes);
            for (var carried = this.nested; carried is not null; carried = carried.Outer)
            {
                carried.Checksum.Append(bytes);
            }

            this.Position += bytes.Length;
        }

        /// <summary>
        /// Reads the next <c>run.Length</c> bytes into <paramref name="run"/>, the array that
        /// keeps them, through the stream's array overload of <c>Read</c>.
        /// </summary>
        /// <exception cref="InvalidDataException">The stream ends first.</exception>
        /// <remarks>
        /// The array overload, because a stream that implements only that one is handed a
        /// rented copy as long as the span by the other, which for a run is as long as the
        /// run.
        /// </remarks>
        internal void ReadRun(byte[] run)
        {
            for (var read = 0; read < run.Length;)
            {
                var n = this.stream.Read(run, read, run.Length - read);
                if (n == 0)
                {
                    throw Truncated();
                }
                read += n;
            }

            this.Taken(run);
        }

        /// <summary>Moves past the next <paramref name="count"/> bytes.</summary>
        /// <exception cref="InvalidDataException">The stream ends first.</exception>
        /// <exception cref="InvalidOperationException">
        /// The source is being read for its checksum, which every byte has to reach.
        /// </exception>
        internal void Skip(long count)
        {
            if (this.checksum is not null)
            {
                throw new InvalidOperationException(
                    "A payload read for its checksum takes every byte in turn, and a reader " +
                    $"asked to move past {count} of them. Only a price moves past bytes.");
            }

            if (this.stream.CanSeek)
            {
                if (this.stream.Length - this.stream.Position < count)
                {
                    throw Truncated();
                }
                this.stream.Seek(count, SeekOrigin.Current);
            }
            else
            {
                this.skipped ??= new byte[4096];
                for (var left = count; left > 0; left -= this.skipped.Length)
                {
                    this.Fill(this.skipped.AsSpan(0, (int)Math.Min(left, this.skipped.Length)));
                }
            }

            this.Position += count;
        }

        private void Fill(Span<byte> buffer)
        {
            while (buffer.Length > 0)
            {
                var n = this.stream.Read(buffer);
                if (n == 0)
                {
                    throw Truncated();
                }
                buffer = buffer.Slice(n);
            }
        }

        private static InvalidDataException Truncated() => new(
            "The stream ended before the end of the payload its header claims. The data " +
            "has been truncated.");
    }
}
