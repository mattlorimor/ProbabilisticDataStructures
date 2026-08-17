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

        private Span<byte> Reserve(int count)
        {
            if (this.length + count > this.buffer.Length)
            {
                var grown = new byte[Math.Max(this.buffer.Length * 2, this.length + count)];
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

        internal PayloadReader(ReadOnlySpan<byte> payload)
        {
            this.payload = payload;
            this.offset = 0;
        }

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

            return Take((int)count).ToArray();
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
            if (count < 0 || this.offset + count > this.payload.Length)
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
