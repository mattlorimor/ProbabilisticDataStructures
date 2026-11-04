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
    /// <para>
    /// The cells are <c>long</c>s and they <b>saturate</b>: a weighted <c>Add</c> or a
    /// <c>Merge</c> that would take a cell past <see cref="long.MaxValue"/> or
    /// <see cref="long.MinValue"/> leaves it at that end instead of wrapping, and an
    /// estimate that does not fit a <c>long</c> comes back as the nearest end. Totals that
    /// large are rare, but a wrapped cell would give an estimate of the wrong sign or size
    /// and nothing to say so. What saturating costs is that a cell at an end no longer
    /// records how far past it the count went, so a later subtraction from it is off by
    /// that much: two adds of <see cref="long.MaxValue"/> followed by one of
    /// <c>-long.MaxValue</c> leave the item near nought and not at
    /// <see cref="long.MaxValue"/>.
    /// </para>
    /// </remarks>
    public class CountSketch : IBinaryPersistable<CountSketch>, IPayloadBody
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
        /// <remarks>
        /// A cell that the count would take past <see cref="long.MaxValue"/> or
        /// <see cref="long.MinValue"/> stops there instead of wrapping, and no longer records
        /// how far past it the count went: see the remarks on this type.
        /// </remarks>
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
                ref var cell = ref this.table[row][column];

                // Subtracted, not added as a negated count: the negation of long.MinValue
                // does not fit, and the difference often does (-1 less long.MinValue is
                // long.MaxValue), so it is the difference that is held to the range.
                cell = sign > 0 ? SaturatingAdd(cell, count) : SaturatingSubtract(cell, count);
            }

            return this;
        }

        /// <summary>
        /// <paramref name="a"/> plus <paramref name="b"/>, or the end of the range it would
        /// have gone past.
        /// </summary>
        private static long SaturatingAdd(long a, long b)
        {
            var sum = unchecked(a + b);

            // The sum wrapped exactly when both operands have a sign the sum does not,
            // and the end it went past is the one the operands lean toward.
            return ((a ^ sum) & (b ^ sum)) < 0
                ? (a < 0 ? long.MinValue : long.MaxValue)
                : sum;
        }

        /// <summary>
        /// <paramref name="a"/> less <paramref name="b"/>, or the end of the range it would
        /// have gone past.
        /// </summary>
        private static long SaturatingSubtract(long a, long b)
        {
            var difference = unchecked(a - b);

            // The difference wrapped exactly when the operands differ in sign and the
            // difference does not keep the sign of the first, which is the end it went past.
            return ((a ^ b) & (a ^ difference)) < 0
                ? (a < 0 ? long.MinValue : long.MaxValue)
                : difference;
        }

        /// <summary>
        /// A cell as a row reads it: as it stands in a row that adds, negated in a row
        /// that subtracts. The negation of <see cref="long.MinValue"/> is one past the
        /// largest count and stops there.
        /// </summary>
        private static long Signed(long sign, long cell)
        {
            return sign > 0 ? cell : cell == long.MinValue ? long.MaxValue : -cell;
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
                estimates[row] = Signed(sign, this.table[row][column]);
            }

            Array.Sort(estimates);

            // The median, which is what makes one unlucky row unable to carry the answer.
            // The two middle estimates of an even depth are summed in 128 bits, since two
            // above 2^62 do not add in 64, and halved by truncation toward zero, which is
            // what an integer division does and what this has always returned.
            return this.depth % 2 == 1
                ? estimates[this.depth / 2]
                : (long)(((Int128)estimates[(this.depth / 2) - 1] + estimates[this.depth / 2]) / 2);
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
        /// with the same sign, so adding the tables cell for cell is adding the streams. A
        /// cell the sum would take past <see cref="long.MaxValue"/> or
        /// <see cref="long.MinValue"/> stops there, as it does in <c>Add</c>, and the merge is
        /// then no longer exact: see the remarks on this type.
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
                    this.table[row][column] = SaturatingAdd(
                        this.table[row][column], other.table[row][column]);
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
        public void WriteTo(Stream stream) => PersistenceFormat.Write(stream, this);

        EnvelopeHeader IPayloadBody.WritePayload(PayloadWriter payload)
        {
            payload.WriteUInt32(this.width);
            payload.WriteUInt32(this.depth);

            // Signed, unlike a Count-Min Sketch's cells, so they are written as such
            // rather than reinterpreted on the way back. One run, sized by the shape.
            if (payload.BeginRun((long)this.width * this.depth * sizeof(long)))
            {
                foreach (var row in this.table)
                {
                    foreach (var cell in row)
                    {
                        payload.WriteInt64(cell);
                    }
                }
            }

            return new(StructureId.CountSketch, PersistenceFormat.Identify(this.Hash));
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
            return PersistenceFormat.Read(
                stream, StructureId.CountSketch, budget, Price, hash, PersistenceFormat.CheckResolvable,
                ReadPayload);
        }

        /// <summary>
        /// Reads the payload of the envelope <c>Read</c> opened, once the envelope has
        /// been checked. See <see cref="PayloadParse{T}"/>.
        /// </summary>
        private static CountSketch ReadPayload(
            ref PayloadReader reader, ushort version, HashId hashId,
            Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            var budget = reader.Budget;

            var width = reader.ReadUInt32();
            var depth = reader.ReadUInt32();

            // The cells are one run, which in the large form can be longer than one array
            // (#228): read in pieces, straight into the rows, when the payload is not held,
            // and copied out whole when it is.
            var stored = reader.BeginWords();
            CheckCells(width, depth, stored);

            var sketch = new CountSketch(0.5, 0.5)
            {
                width = width,
                depth = depth,
                Hash = PersistenceFormat.ResolveOrThrow(hashId, hash),
            };

            budget.Charge(RowsCost(width, depth));
            sketch.table = new long[depth][];
            for (var row = 0; row < depth; row++)
            {
                sketch.table[row] = new long[width];
            }

            var filling = 0;
            var column = 0;
            while (filling < depth)
            {
                var piece = reader.ReadWords();
                for (var at = 0; at < piece.Length; at += sizeof(long))
                {
                    sketch.table[filling][column] = BinaryPrimitives.ReadInt64LittleEndian(piece[at..]);
                    if (++column == width)
                    {
                        column = 0;
                        filling++;
                    }
                }
            }

            reader.ExpectEnd();
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
            var stored = reader.SkipWords();
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
