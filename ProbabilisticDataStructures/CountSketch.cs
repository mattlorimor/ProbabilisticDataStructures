using System;
using System.Buffers.Binary;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Estimates how often each item has been seen, without the one-sided bias a
    /// <see cref="CountMinSketch"/> carries.
    /// </summary>
    /// <remarks>
    /// Charikar, Chen and Farach-Colton, "Finding Frequent Items in Data Streams" (2002).
    /// <para>
    /// Each row hashes an item to a cell and to a <b>sign</b>, and the estimate is the
    /// median of the signed cells. Collisions therefore cancel in expectation rather than
    /// accumulate, which is the whole difference: a Count-Min Sketch takes the minimum of
    /// cells that can only have been pushed up, so it never undercounts and usually
    /// overcounts. This is unbiased, and wrong in both directions.
    /// </para>
    /// <para>
    /// Two consequences worth knowing. Estimates can come back <b>negative</b>, which a
    /// count never is -- it means the true count is near zero and the noise went the other
    /// way. And decrements work, because a negative update is just an update, where
    /// Count-Min cannot meaningfully subtract once collisions have inflated a cell.
    /// </para>
    /// </remarks>
    public class CountSketch : IBinaryPersistable<CountSketch>
    {
        private uint width;
        private uint depth;
        private long[][] table = null!;

        internal Func<ReadOnlySpan<byte>, ulong> Hash { get; set; } = null!;

        /// <summary>
        /// Creates a sketch with the given accuracy and confidence.
        /// </summary>
        /// <param name="epsilon">
        /// The error, as a fraction of the stream's Euclidean norm. See the remarks on
        /// this type for why that differs from a Count-Min Sketch's epsilon.
        /// </param>
        /// <param name="delta">The probability that an estimate exceeds that error.</param>
        /// <param name="hash">The hash function to use, or null for the default.</param>
        public CountSketch(double epsilon, double delta, Func<ReadOnlySpan<byte>, ulong>? hash = null)
        {
            Guard.ValidSketchEpsilon(epsilon, nameof(epsilon));
            Guard.ValidSketchDelta(delta, nameof(delta));

            this.width = (uint)Math.Ceiling(1.0 / (epsilon * epsilon));
            this.depth = (uint)Math.Ceiling(Math.Log(1 / delta));
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();

            this.table = new long[this.depth][];
            for (var i = 0; i < this.depth; i++)
            {
                this.table[i] = new long[this.width];
            }
        }

        /// <summary>
        /// Records one occurrence of the data.
        /// </summary>
        /// <param name="data">The data seen.</param>
        /// <returns>The sketch, to allow chaining.</returns>
        public CountSketch Add(byte[] data)
        {
            return this.Add(data, 1);
        }

        /// <summary>
        /// Records the data as having been seen a given number of times, which may be
        /// negative.
        /// </summary>
        /// <param name="data">The data seen.</param>
        /// <param name="count">How many times, negative to remove.</param>
        /// <returns>The sketch, to allow chaining.</returns>
        public CountSketch Add(byte[] data, long count)
        {
            ArgumentNullException.ThrowIfNull(data);

            var hash = this.Hash(data);

            for (var row = 0u; row < this.depth; row++)
            {
                var (column, sign) = this.Locate(hash, row);
                this.table[row][column] += sign * count;
            }

            return this;
        }

        /// <summary>
        /// The estimated number of times the data has been seen.
        /// </summary>
        /// <param name="data">The data to estimate.</param>
        /// <returns>
        /// The estimate, which may be negative when the true count is near zero.
        /// </returns>
        public long Count(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            var hash = this.Hash(data);
            var estimates = new long[this.depth];

            for (var row = 0u; row < this.depth; row++)
            {
                var (column, sign) = this.Locate(hash, row);
                estimates[row] = sign * this.table[row][column];
            }

            Array.Sort(estimates);

            // The median, which is what makes one unlucky row unable to carry the answer.
            return this.depth % 2 == 1
                ? estimates[this.depth / 2]
                : (estimates[(this.depth / 2) - 1] + estimates[this.depth / 2]) / 2;
        }

        /// <summary>
        /// Which cell of a row an item falls in, and whether it is added or subtracted
        /// there.
        /// </summary>
        private (uint Column, long Sign) Locate(ulong hash, uint row)
        {
            // One hash, split per row, rather than hashing the data once per row.
            var mixed = Mix(hash + (row * 0x9E3779B97F4A7C15UL));

            return ((uint)(mixed % this.width), (mixed & (1UL << 63)) != 0 ? 1L : -1L);
        }

        private static ulong Mix(ulong h)
        {
            h ^= h >> 33;
            h *= 0xff51afd7ed558ccdUL;
            h ^= h >> 33;
            h *= 0xc4ceb9fe1a85ec53UL;
            h ^= h >> 33;
            return h;
        }

        /// <summary>
        /// Combines another sketch into this one, so it counts both streams.
        /// </summary>
        /// <param name="other">The sketch to merge in, which is left unchanged.</param>
        /// <returns>This sketch, to allow chaining.</returns>
        /// <remarks>
        /// Exact, because two sketches of the same shape place an item in the same cell
        /// with the same sign, so adding the tables cell for cell is adding the streams.
        /// </remarks>
        /// <exception cref="ArgumentNullException">The other sketch is null.</exception>
        /// <exception cref="ArgumentException">
        /// The sketches have different dimensions or hash functions.
        /// </exception>
        public CountSketch Merge(CountSketch other)
        {
            ArgumentNullException.ThrowIfNull(other);

            if (this.width != other.width || this.depth != other.depth)
            {
                throw new ArgumentException(
                    "Sketches must have the same dimensions to be merged: this one is " +
                    $"{this.depth} by {this.width} and the other {other.depth} by " +
                    $"{other.width}. The shape must match.",
                    nameof(other));
            }

            Guard.SameHashFunction(this.Hash, other.Hash, nameof(other));

            for (var row = 0; row < this.depth; row++)
            {
                for (var column = 0; column < this.width; column++)
                {
                    this.table[row][column] += other.table[row][column];
                }
            }

            return this;
        }

        /// <summary>
        /// Sets the hashing function used by the sketch.
        /// </summary>
        /// <param name="h">The hash function to use.</param>
        /// <exception cref="InvalidOperationException">Anything has been added.</exception>
        public void SetHash(Func<ReadOnlySpan<byte>, ulong> h)
        {
            ArgumentNullException.ThrowIfNull(h);
            Guard.HashMayBeReplaced(this.IsEmpty, nameof(CountSketch));
            this.Hash = h;
        }

        private bool IsEmpty
        {
            get
            {
                foreach (var row in this.table)
                {
                    foreach (var cell in row)
                    {
                        if (cell != 0)
                        {
                            return false;
                        }
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Writes the sketch to a stream.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = new PayloadWriter();
            payload.WriteUInt32(this.width);
            payload.WriteUInt32(this.depth);

            // Signed, unlike a Count-Min Sketch's cells, so they are written as such
            // rather than reinterpreted on the way back.
            var bytes = new byte[(long)this.width * this.depth * sizeof(long)];
            var at = 0;
            foreach (var row in this.table)
            {
                foreach (var cell in row)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), cell);
                    at += sizeof(long);
                }
            }

            payload.WriteBytes(bytes);

            PersistenceFormat.Write(
                stream, StructureId.CountSketch, PersistenceFormat.Identify(this.Hash),
                payload.WrittenSpan);
        }

        /// <summary>
        /// Reads a sketch written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The sketch that was written.</returns>
        public static CountSketch ReadFrom(Stream stream)
        {
            return Read(stream, null);
        }

        /// <summary>
        /// Reads a sketch written by <see cref="WriteTo"/>, using the supplied hash
        /// function rather than the one named in the payload.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function the sketch was using.</param>
        /// <returns>The sketch that was written.</returns>
        public static CountSketch ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            return Read(stream, hash);
        }

        private static CountSketch Read(Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(stream, StructureId.CountSketch, out var hashId);
            var reader = new PayloadReader(payload);

            var width = reader.ReadUInt32();
            var depth = reader.ReadUInt32();
            var bytes = reader.ReadBytes();
            reader.ExpectEnd();

            if (width == 0 || depth == 0)
            {
                throw new InvalidDataException(
                    $"Sketch is {depth} rows by {width} columns, leaving no cells to " +
                    "count in.");
            }

            var expected = (long)width * depth * sizeof(long);
            if (bytes.LongLength != expected)
            {
                throw new InvalidDataException(
                    $"Sketch is {depth} by {width}, which needs {expected} bytes of " +
                    $"cells, and carries {bytes.LongLength}.");
            }

            var sketch = new CountSketch(0.5, 0.5)
            {
                width = width,
                depth = depth,
                Hash = PersistenceFormat.ResolveOrThrow(hashId, hash),
            };

            sketch.table = new long[depth][];
            var at = 0;
            for (var row = 0; row < depth; row++)
            {
                sketch.table[row] = new long[width];
                for (var column = 0; column < width; column++)
                {
                    sketch.table[row][column] = BinaryPrimitives.ReadInt64LittleEndian(
                        bytes.AsSpan(at));
                    at += sizeof(long);
                }
            }

            return sketch;
        }

        /// <summary>The number of cells in each row.</summary>
        public uint Width() => this.width;

        /// <summary>The number of rows.</summary>
        public uint Depth() => this.depth;
    }
}
