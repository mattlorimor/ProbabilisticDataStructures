using System;
using System.Buffers.Binary;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Builds a payload as a growable little-endian byte buffer.
    /// </summary>
    /// <remarks>
    /// Little-endian throughout, and explicitly so, because the layout is a file format
    /// rather than a memory dump: a payload written on one machine has to read the same
    /// on another. BinaryPrimitives is used in preference to BitConverter for that
    /// reason, since BitConverter follows whatever the running architecture does.
    /// </remarks>
    internal sealed class PayloadWriter
    {
        private byte[] buffer = new byte[256];
        private int length;

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

        internal void WriteDouble(double value)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(Reserve(8), value);
        }

        /// <summary>
        /// Writes a length-prefixed run of bytes.
        /// </summary>
        internal void WriteBytes(ReadOnlySpan<byte> value)
        {
            WriteUInt32((uint)value.Length);
            value.CopyTo(Reserve(value.Length));
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
        /// The payload would be longer than the largest one that can be written.
        /// </exception>
        /// <remarks>
        /// All of it in 64 bits. In 32, doubling wrapped negative from a gibibyte up and
        /// the buffer was left exactly full, so that each write after it copied the whole
        /// payload; and the sum of the length and the write wrapped too, so a payload
        /// past the largest array failed in whatever way a negative length fails. Doubling
        /// is clamped to the largest payload, so that the buffer reaches it in one copy and
        /// every write up to it after that costs nothing.
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

        private Span<byte> Reserve(int count)
        {
            var capacity = GrownCapacity(this.buffer.Length, this.length, count);
            if (capacity != this.buffer.Length)
            {
                var grown = new byte[capacity];
                this.buffer.AsSpan(0, this.length).CopyTo(grown);
                this.buffer = grown;
            }

            var span = this.buffer.AsSpan(this.length, count);
            this.length += count;
            return span;
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
        /// </remarks>
        internal PayloadReader Slice(int length)
        {
            RequireAhead(length);
            if (this.source is null)
            {
                var slice = new PayloadReader(this.payload.Slice(this.offset, length), this.Budget);
                this.offset += length;
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
