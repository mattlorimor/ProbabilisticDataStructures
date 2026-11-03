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
        /// <remarks>
        /// Each row is ceil(1 / epsilon^2) counters and is one array, so the widest is
        /// 2,147,483,591, an epsilon of about 2.16e-5. There are ceil(ln(1 / delta)) rows,
        /// at most 710, for a delta of about 5.6e-309.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Epsilon is not positive, or asks for a row no array holds or for none at all.
        /// Delta is not strictly between zero and one, or is so small that its reciprocal,
        /// and so the row count, is infinite.
        /// </exception>
        public CountSketch(double epsilon, double delta, Func<ReadOnlySpan<byte>, ulong>? hash = null)
        {
            Guard.ValidSketchEpsilon(epsilon, nameof(epsilon));
            Guard.ValidSketchDelta(delta, nameof(delta));

            // Refused before anything is allocated. The width is 1 / epsilon squared, which
            // passes what an array holds long before epsilon is small, and a delta whose
            // reciprocal is infinite has no row count to convert.
            this.width = Guard.SketchWidth(
                Math.Ceiling(1.0 / (epsilon * epsilon)), epsilon, nameof(epsilon));
            this.depth = Guard.SketchRows(delta, nameof(delta));
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

        /// <inheritdoc cref="Add(byte[])"/>
        public CountSketch Add(ReadOnlySpan<byte> data)
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

            return this.Add(data.AsSpan(), count);
        }

        /// <inheritdoc cref="Add(byte[], long)"/>
        public CountSketch Add(ReadOnlySpan<byte> data, long count)
        {
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

            return this.Count(data.AsSpan());
        }

        /// <inheritdoc cref="Count(byte[])"/>
        public long Count(ReadOnlySpan<byte> data)
        {
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
            return Read(stream, null, ReadOptions.Default);
        }

        /// <summary>
        /// Reads a structure written by <see cref="WriteTo"/>, allocating no more than the
        /// options' memory budget allows. <see cref="ReadFrom(Stream)"/> reads under
        /// <see cref="ReadOptions.Default"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="options">The most memory the read may allocate.</param>
        /// <returns>The structure that was written.</returns>
        /// <exception cref="InvalidDataException">
        /// Reading the payload would allocate more than the budget allows, among the other
        /// reasons a payload is refused. Its inner exception is then a
        /// <see cref="ReadBudgetExceededException"/>.
        /// </exception>
        public static CountSketch ReadFrom(Stream stream, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, null, options);
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
            return Read(stream, hash, ReadOptions.Default);
        }

        /// <summary>
        /// Reads a structure as
        /// <see cref="ReadFrom(Stream, Func{ReadOnlySpan{byte}, ulong})"/> does, allocating
        /// no more than the options' memory budget allows.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function, as for the overload without options.</param>
        /// <param name="options">The most memory the read may allocate.</param>
        /// <returns>The structure that was written.</returns>
        /// <exception cref="InvalidDataException">
        /// Reading the payload would allocate more than the budget allows, among the other
        /// reasons a payload is refused. Its inner exception is then a
        /// <see cref="ReadBudgetExceededException"/>.
        /// </exception>
        public static CountSketch ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, hash, options);
        }

        private static CountSketch Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.CountSketch);
            var payload = PersistenceFormat.Read(
                stream, StructureId.CountSketch, out var hashId, budget, Price,
                hash, PersistenceFormat.CheckResolvable);
            var reader = new PayloadReader(payload, budget);

            var width = reader.ReadUInt32();
            var depth = reader.ReadUInt32();
            var bytes = reader.ReadBytes();
            reader.ExpectEnd();
            CheckCells(width, depth, bytes.LongLength);

            var sketch = new CountSketch(0.5, 0.5)
            {
                width = width,
                depth = depth,
                Hash = PersistenceFormat.ResolveOrThrow(hashId, hash),
            };

            budget.Charge(RowsCost(width, depth));
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

        /// <summary>
        /// Prices what <see cref="Read"/> allocates beyond the envelope: the copy of the
        /// cells, and the table they are unpacked into. See <see cref="PayloadPrice"/>.
        /// </summary>
        internal static void Price(ref PayloadReader reader, ushort version)
        {
            var width = reader.ReadUInt32();
            var depth = reader.ReadUInt32();
            var stored = reader.SkipBytes();
            reader.ExpectEnd();
            CheckCells(width, depth, stored);
            reader.Budget.Price(RowsCost(width, depth));
        }

        /// <summary>
        /// The most counters a row can hold, which is the most an array holds. The
        /// constructor refuses an epsilon that asks for more, and the reader refuses a
        /// payload that claims more.
        /// </summary>
        internal static uint MaxWidth => Guard.MaxSketchWidth;

        /// <summary>
        /// The most rows a sketch can have: ceil(ln(1 / delta)) at the smallest delta whose
        /// reciprocal is a finite double.
        /// </summary>
        internal static uint MaxDepth => Guard.MaxSketchRows;

        /// <summary>
        /// Refuses dimensions no sketch has, or whose cells are not the bytes a payload
        /// carries.
        /// </summary>
        /// <remarks>
        /// The limits come first, so that the product below cannot wrap: it is at most
        /// 2,147,483,591 by 710 by 8 bytes, about 1.2e13. Unchecked, a width of 2^31 and a
        /// depth of 2^30 came to 2^64, which is nought, and agreed with a payload whose cell
        /// run was empty. The limits also keep the reader to what the constructor builds: a
        /// sketch of a million rows would make every <c>Count</c> allocate and sort a
        /// million estimates.
        /// </remarks>
        internal static void CheckCells(uint width, uint depth, long stored)
        {
            if (width == 0 || depth == 0)
            {
                throw new InvalidDataException(
                    $"Sketch is {depth} rows by {width} columns, leaving no cells to " +
                    "count in.");
            }

            // The constructor's own limits, so that every sketch it builds reads back and
            // a few bytes cannot ask for a row that no array could hold.
            if (width > MaxWidth)
            {
                throw new InvalidDataException(
                    $"Sketch claims rows {width} counters wide, beyond anything this library " +
                    $"builds: a row is one array, of at most {MaxWidth}.");
            }

            if (depth > MaxDepth)
            {
                throw new InvalidDataException(
                    $"Sketch claims {depth} rows, beyond anything this library builds: it " +
                    $"builds at most {MaxDepth}, for the smallest delta with a finite reciprocal.");
            }

            var expected = (ulong)width * depth * sizeof(long);
            if ((ulong)stored != expected)
            {
                throw new InvalidDataException(
                    $"Sketch is {depth} by {width}, which needs {expected} bytes of " +
                    $"cells, and carries {stored}.");
            }
        }

        /// <summary>A table of <paramref name="depth"/> rows of <paramref name="width"/> cells.</summary>
        private static long RowsCost(uint width, uint depth) => ReadBudget.Sum(
            ReadBudget.ArrayOf(depth, sizeof(long)),
            ReadBudget.Times(depth, ReadBudget.ArrayOf(width, sizeof(long))));

        /// <summary>The number of cells in each row.</summary>
        public uint Width() => this.width;

        /// <summary>The number of rows.</summary>
        public uint Depth() => this.depth;
    }
}
