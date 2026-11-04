using System;
using System.Buffers.Binary;
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
    /// be longer than any array, is written without being held (#225). A run longer than
    /// the buffer goes to the stream as it is, after what the buffer holds. The writer is
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
        /// The longest run a payload can hold: <c>int.MaxValue</c> bytes, the most a reader
        /// of a run takes (<see cref="PayloadReader.ReadBytes"/>), unless the suite has
        /// lowered it on this thread.
        /// </summary>
        /// <remarks>
        /// A run's length prefix is a <c>u32</c>, so a run past this, which the ceiling on
        /// the ordinary envelope kept out of every payload until the large form, would be
        /// written with a length its reader refuses, and one of 2^32 bytes or more with a
        /// length that had wrapped. No check downstream would see the second: the count and
        /// the write agree on the bytes, and the checksum covers the wrong length as
        /// faithfully as the right one. So a run past it is refused where it is counted,
        /// before anything is written. Widening runs is #228.
        /// </remarks>
        internal static long LongestRun => LoweredLongestRun ?? int.MaxValue;

        private byte[] buffer;
        private int length;

        // A counter's: what the writes so far would have written. Its buffer is the
        // scratch a field is written into to be counted, and nothing reads it.
        private readonly bool counting;
        private long counted;

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

        private PayloadWriter(bool counting)
        {
            this.buffer = new byte[sizeof(ulong)];
            this.counting = counting;
        }

        private PayloadWriter(Stream sink, Crc32 crc, EnvelopeHeader header, long declared)
        {
            this.buffer = new byte[LoweredStreamBufferLength ?? StreamBufferLength];
            this.sink = sink;
            this.crc = crc;
            this.header = header;
            this.declared = declared;
        }

        /// <summary>
        /// A writer that stores nothing and counts what it is given.
        /// </summary>
        internal static PayloadWriter Counter() => new(counting: true);

        /// <summary>
        /// A writer that writes what it is given to <paramref name="sink"/>, a buffer at a
        /// time, appending every byte to <paramref name="crc"/> as it goes.
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
        /// How long the payload is: the bytes written, or, for a counter, the bytes the
        /// same writes would have written.
        /// </summary>
        internal long Length => this.counting ? this.counted : this.flushed + this.length;

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
        /// <exception cref="InvalidOperationException">
        /// The run is longer than <see cref="LongestRun"/>. A counter refuses it, so a write
        /// counted first refuses it before anything is written.
        /// </exception>
        /// <remarks>
        /// The length is taken in 64 bits so that a counter can count a run longer than
        /// a payload holds, which is how such a payload comes to be refused naming its
        /// whole size.
        /// </remarks>
        internal bool BeginRun(long length)
        {
            CheckRun(length);
            WriteUInt32((uint)length);
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
        /// Refuses a run longer than a payload can hold, naming its length, before its
        /// length prefix is written.
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
                    $"payload can be longer than {LongestRun:N0} bytes yet, the most a reader " +
                    "takes into one array. The structure is intact and can still be used, but it " +
                    "is too large to write, to a stream or as a byte array. Keep less in it, or " +
                    "split what it holds across several.");
            }
        }

        /// <summary>
        /// Writes out what a streaming writer still holds. It is the payload's last piece
        /// once the body has written the whole of it.
        /// </summary>
        internal void Flush()
        {
            if (this.length > 0)
            {
                Emit(this.buffer.AsSpan(0, this.length));
                this.length = 0;
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
    /// </remarks>
    internal ref struct PayloadReader
    {
        private readonly ReadOnlySpan<byte> payload;
        private int offset;

        // Set when the payload is read from the stream instead of from memory, which only
        // pricing does, for a payload too large for its budget to be held. The span is
        // then empty, the source's position stands for the offset, and this reader's
        // payload ends at the source position given.
        private readonly PayloadSource? source;
        private readonly long start;
        private readonly long end;

        internal PayloadReader(ReadOnlySpan<byte> payload, ReadBudget budget)
        {
            this.payload = payload;
            this.offset = 0;
            this.source = null;
            this.start = 0;
            this.end = 0;
            this.Budget = budget;
        }

        /// <summary>
        /// A reader of the next <paramref name="length"/> bytes of a payload that is not
        /// held, for pricing it. It reads fields and skips runs; it cannot copy one.
        /// </summary>
        internal PayloadReader(PayloadSource source, long length, ReadBudget budget)
        {
            this.payload = default;
            this.offset = 0;
            this.source = source;
            this.start = source.Position;
            this.end = source.Position + length;
            this.Budget = budget;
        }

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
        /// Reads a length-prefixed run of bytes, copied out so the caller can keep it.
        /// </summary>
        internal byte[] ReadBytes()
        {
            var count = ReadUInt32();
            if (count > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"Payload holds a {count}-byte run, which cannot be read into a " +
                    "single array.");
            }

            // Bounds-checked first, so a run the payload does not hold is refused as
            // truncated; then the copy is charged before it is made.
            var run = Take((int)count);
            this.Budget.Charge(ReadBudget.ArrayOf(count, 1));
            return run.ToArray();
        }

        /// <summary>
        /// Prices what <see cref="ReadBytes"/> allocates for the run that comes next, after
        /// the same checks, and leaves the reader at the start of the run. Returns its
        /// length.
        /// </summary>
        internal int PriceBytes()
        {
            var count = ReadUInt32();
            if (count > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"Payload holds a {count}-byte run, which cannot be read into a " +
                    "single array.");
            }

            RequireAhead((int)count);
            this.Budget.Price(ReadBudget.ArrayOf(count, 1));
            return (int)count;
        }

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
        internal PayloadReader Slice(long length)
        {
            RequireAhead(length);
            if (this.source is null)
            {
                var slice = new PayloadReader(this.payload.Slice(this.offset, (int)length), this.Budget);
                this.offset += (int)length;
                return slice;
            }

            return new PayloadReader(this.source, length, this.Budget);
        }

        /// <summary>
        /// The bytes not yet read.
        /// </summary>
        internal int Remaining => this.source is null
            ? this.payload.Length - this.offset
            : (int)(this.end - this.source.Position);

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
                throw new InvalidDataException(
                    $"Payload ended after {this.Length} bytes while reading " +
                    $"{count} more at offset {this.Offset}. It is truncated or does not " +
                    "have the shape this structure expects.");
            }
        }

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
    /// A payload read from its stream a field at a time rather than held, so that one
    /// too large for its read's budget can still be priced.
    /// </summary>
    /// <remarks>
    /// Only pricing reads this way, and only to name the total in a refusal: the read it
    /// prices is refused whatever the total comes to, so how far into the stream it is
    /// left does not matter. Runs are skipped, by seeking where the stream can and by
    /// reading through a small buffer where it cannot, so the price of a payload of
    /// gigabytes costs a few hundred bytes to work out.
    /// </remarks>
    internal sealed class PayloadSource
    {
        /// <summary>The widest field taken in place: a nested envelope's header.</summary>
        internal const int WidestField = 16;

        private readonly Stream stream;
        private readonly byte[] field = new byte[WidestField];
        private byte[]? skipped;

        internal PayloadSource(Stream stream)
        {
            this.stream = stream;
        }

        /// <summary>How many of the payload's bytes have been read or skipped.</summary>
        internal long Position { get; private set; }

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
                    $"{WidestField} bytes, and {count} were asked for. Only a price reads " +
                    "a payload this way, and a price skips runs rather than copying them.");
            }

            var span = this.field.AsSpan(0, count);
            this.Fill(span);
            this.Position += count;
            return span;
        }

        /// <summary>Moves past the next <paramref name="count"/> bytes.</summary>
        /// <exception cref="InvalidDataException">The stream ends first.</exception>
        internal void Skip(long count)
        {
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
