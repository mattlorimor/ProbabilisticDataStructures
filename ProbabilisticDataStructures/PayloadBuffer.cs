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

        internal PayloadReader(ReadOnlySpan<byte> payload, ReadBudget budget)
        {
            this.payload = payload;
            this.offset = 0;
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
            return Take(1)[0];
        }

        internal ushort ReadUInt16()
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        }

        internal uint ReadUInt32()
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        }

        internal ulong ReadUInt64()
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        }

        internal double ReadDouble()
        {
            return BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
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
        /// The bytes not yet read.
        /// </summary>
        internal int Remaining => this.payload.Length - this.offset;

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
            if (this.offset != this.payload.Length)
            {
                throw new InvalidDataException(
                    $"Payload has {this.payload.Length - this.offset} bytes left over " +
                    $"after reading {this.offset} of {this.payload.Length}. It does not " +
                    "have the shape this structure expects.");
            }
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
}
