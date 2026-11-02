using System;
using System.IO;
using System.Security.Cryptography;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// CountMinSketch implements a Count-Min Sketch as described by Cormode and
    /// Muthukrishnan in An Improved Data Stream Summary: The Count-Min Sketch and its
    /// Applications:
    ///
    /// http://dimacs.rutgers.edu/~graham/pubs/papers/cm-full.pdf
    ///
    /// A Count-Min Sketch (CMS) is a probabilistic data structure which approximates
    /// the frequency of events in a data stream. Unlike a hash map, a CMS uses
    /// sub-linear space at the expense of a configurable error factor. Similar to
    /// Counting Bloom filters, items are hashed to a series of buckets, which increment
    /// a counter. The frequency of an item is estimated by taking the minimum of each of
    /// the item's respective counter values.
    ///
    /// Count-Min Sketches are useful for counting the frequency of events in massive
    /// data sets or unbounded streams online. In these situations, storing the entire
    /// data set or allocating counters for every event in memory is impractical. It may
    /// be possible for offline processing, but real-time processing requires fast,
    /// space-efficient solutions like the CMS. For approximating set cardinality, refer
    /// to the HyperLogLog.
    /// </summary>
    /// <remarks>
    /// A sketch 2^27 columns wide or wider, an epsilon of about 2.03e-8 or finer, places
    /// its counts with 64-bit hash values; a narrower one places with 32-bit values, as it
    /// always did. Every save is format version 9, which records the placement in a byte of
    /// its own, and which libraries before this one cannot read. Before this, every sketch
    /// placed with 32-bit values, and a wide one gave the first <c>2^32 mod w</c> columns
    /// of each row more of the stream than the rest, so items counted there were
    /// overcounted by more than the bound assumes: about 3% more just past 2^27, a quarter
    /// more at 2^29.8, and half as much again from a third of 2^32. A sketch read back keeps
    /// the scheme its payload was placed with, so one written before is still that sketch;
    /// add its items to a new sketch to have them counted evenly. The two schemes do not
    /// merge.
    /// </remarks>
    public class CountMinSketch : IBinaryPersistable<CountMinSketch>
    {
        /// <summary>
        /// Count matrix
        /// </summary>
        internal UInt64[][] Matrix { get; set; }
        /// <summary>
        /// Matrix width
        /// </summary>
        internal uint Width { get; set; }
        /// <summary>
        /// Matrix depth
        /// </summary>
        internal uint Depth { get; set; }
        /// <summary>
        /// Number of items added
        /// </summary>
        private UInt64 count { get; set; }
        /// <summary>
        /// Relative-accuracy factor
        /// </summary>
        private double epsilon { get; set; }
        /// <summary>
        /// Probability of exceeding the accuracy epsilon allows
        /// </summary>
        private double delta { get; set; }
        /// <summary>
        /// Hash function
        /// </summary>
        private Func<ReadOnlySpan<byte>, ulong> Hash { get; set; } = null!;

        /// <summary>
        /// The hash function in use, so that a structure holding a sketch can record
        /// which one it was built with.
        /// </summary>
        internal Func<ReadOnlySpan<byte>, ulong> HashFunction => this.Hash;

        /// <summary>
        /// How an item's hash becomes the column it is counted in, in each row.
        /// </summary>
        internal Placement Placement { get; private set; }

        /// <summary>
        /// The most counters a row can hold, which is the most an array holds.
        /// The constructor refuses an epsilon that asks for more, and the reader refuses
        /// a payload that claims more.
        /// </summary>
        internal static uint MaxWidth => Guard.MaxSketchWidth;

        /// <summary>
        /// The most rows a sketch can have: ceil(ln(1 / delta)) at the smallest delta whose
        /// reciprocal is a finite double.
        /// </summary>
        internal static uint MaxDepth => Guard.MaxSketchRows;

        /// <summary>
        /// Creates a new Count-Min Sketch whose estimates exceed the true count by no
        /// more than epsilon times the total count, with probability at least
        /// 1 - delta. Delta is therefore a probability of failure: a smaller delta is a
        /// deeper, more reliable sketch. Both parameters affect the space and time
        /// complexity.
        /// </summary>
        /// <remarks>
        /// Each row is ceil(e / epsilon) counters and is one array, so the widest is
        /// 2,147,483,591, an epsilon of about 1.27e-9, and a sketch of 16 GiB a row. There
        /// are ceil(ln(1 / delta)) rows, at most 710, for a delta of about 5.6e-309.
        /// </remarks>
        /// <param name="epsilon">Relative-accuracy factor</param>
        /// <param name="delta">Probability of exceeding that overestimate</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Epsilon is not positive, or asks for a row no array holds or for none at all.
        /// Delta is not strictly between zero and one, or is so small that its reciprocal,
        /// and so the row count, is infinite.
        /// </exception>
        /// <param name="hash">
        /// The hash function to use, or null for the default. Passing it here is the
        /// only way to have one hash cover everything the structure will ever hold:
        /// once anything has been added, the hash can no longer be replaced.
        /// </param>
        public CountMinSketch(double epsilon, double delta, Func<ReadOnlySpan<byte>, ulong>? hash = null)
            : this(epsilon, delta, hash, null)
        {
        }

        /// <summary>
        /// Creates a sketch as the public constructor does, placing its counts as
        /// <paramref name="placement"/> says, or as its width calls for when that is null.
        /// </summary>
        /// <remarks>
        /// The choice is made by width, and a sketch wide enough for it to matter is a
        /// gigabyte a row. This lets a test have a small sketch place its counts the way a
        /// wide one will.
        /// </remarks>
        internal CountMinSketch(
            double epsilon, double delta, Func<ReadOnlySpan<byte>, ulong>? hash, Placement? placement)
        {
            Guard.ValidSketchEpsilon(epsilon, nameof(epsilon));
            Guard.ValidSketchDelta(delta, nameof(delta));

            // Both refused before anything is allocated: a row is one array, and a delta
            // whose reciprocal is infinite has no row count to convert.
            var width = Guard.SketchWidth(Math.Ceiling(Math.E / epsilon), epsilon, nameof(epsilon));
            var depth = Guard.SketchRows(delta, nameof(delta));
            this.Matrix = new UInt64[depth][];

            for (int i = 0; i < depth; i++)
            {
               this.Matrix[i] = new UInt64[width];
            }

            this.Width = width;
            this.Depth = depth;
            this.epsilon = epsilon;
            this.delta = delta;
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();
            this.Placement = placement ?? PlacementFor(width);
        }

        /// <summary>
        /// The placement a new sketch <paramref name="width"/> columns wide is built with.
        /// </summary>
        /// <remarks>
        /// The constructor's choice, reachable on its own so that the widths at which it
        /// changes can be checked without allocating sketches of those widths: 64-bit values
        /// from <see cref="BitPlacement.WideFrom"/> columns, and 32-bit ones, as every sketch
        /// always had, below it.
        /// </remarks>
        internal static Placement PlacementFor(uint width) => BitPlacement.For(width);

        /// <summary>
        /// The columns <paramref name="data"/> is counted in under
        /// <paramref name="placement"/>, in a sketch <paramref name="width"/> wide: row
        /// <c>i</c> takes the <c>i</c>th.
        /// </summary>
        /// <remarks>
        /// What <see cref="Add(ReadOnlySpan{byte})"/> and
        /// <see cref="Count(ReadOnlySpan{byte})"/> use, reachable without a sketch so that
        /// a test can ask it at widths no test can allocate.
        /// </remarks>
        internal static Positions ColumnsOf(
            ReadOnlySpan<byte> data, Func<ReadOnlySpan<byte>, ulong> hash, Placement placement, uint width) =>
            Positions.Of(data, hash, placement, width);

        /// <summary>
        /// Returns the relative-accuracy factor, epsilon.
        /// </summary>
        /// <returns>The relative-accuracy factor, epsilon</returns>
        public double Epsilon()
        {
            return this.epsilon;
        }

        /// <summary>
        /// Returns the relative-accuracy probability, delta.
        /// </summary>
        /// <returns>The relative-accuracy probability, delta</returns>
        public double Delta()
        {
            return this.delta;
        }

        /// <summary>
        /// Returns the number of items added to the sketch.
        /// </summary>
        /// <returns>The number of items added to the sketch.</returns>
        public UInt64 TotalCount()
        {
            return this.count;
        }

        /// <summary>
        /// Add the data to the set. Returns the CountMinSketch to allow for chaining.
        /// </summary>
        /// <param name="data">The data to add.</param>
        /// <returns>The CountMinSketch</returns>
        public CountMinSketch Add(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            return this.Add(data.AsSpan());
        }

        /// <inheritdoc cref="Add(byte[])"/>
        public CountMinSketch Add(ReadOnlySpan<byte> data)
        {
            var columns = ColumnsOf(data, this.Hash, this.Placement, this.Width);

            // Increment count in each row.
            for (uint i = 0; i < this.Depth; i++)
            {
                this.Matrix[i][columns[i]]++;
            }

            this.count++;
            return this;
        }

        /// <summary>
        /// Returns the approximate count for the specified item, correct within
        /// epsilon * total count with a probability of delta.
        /// </summary>
        /// <param name="data"></param>
        /// <returns>The data to count.</returns>
        public UInt64 Count(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            return this.Count(data.AsSpan());
        }

        /// <inheritdoc cref="Count(byte[])"/>
        public UInt64 Count(ReadOnlySpan<byte> data)
        {
            var columns = ColumnsOf(data, this.Hash, this.Placement, this.Width);
            var count = UInt64.MaxValue;

            for (uint i = 0; i < this.Depth; i++)
            {
                count = Math.Min(count, this.Matrix[i][columns[i]]);
            }

            return count;
        }

        /// <summary>
        /// Combines this CountMinSketch with another. Returns a bool if the merge was
        /// successful. Throws an exception if the matrix width and depth are not equal.
        /// </summary>
        /// <param name="other">The CountMinSketch to merge with the current
        /// instance.</param>
        /// <returns>True if successful.</returns>
        /// <exception cref="ArgumentException">
        /// The two were built with different dimensions or different hash functions, or
        /// their counts were placed by different schemes, so their cells do not count the
        /// same items. A sketch read from a payload written before format version 9 places
        /// with 32-bit values whatever its width, and a new sketch 2^27 columns wide or
        /// wider with 64-bit values, so two such sketches of the same width do not merge.
        /// </exception>
        public bool Merge(CountMinSketch other)
        {
            ArgumentNullException.ThrowIfNull(other);

            Guard.SameHashFunction(this.Hash, other.Hash, nameof(other));

            // ArgumentException rather than Exception: a caller who wants to fall back
            // to merging some other way cannot catch the bare one without catching
            // every unrelated failure alongside it.
            if (this.Depth != other.Depth)
            {
                throw new ArgumentException(
                    $"Matrix depth must match. This sketch is {this.Depth} rows deep " +
                    $"and the other is {other.Depth}; depth follows from delta, so the " +
                    "two sketches were built with different ones.", nameof(other));
            }

            if (this.Width != other.Width)
            {
                throw new ArgumentException(
                    $"Matrix width must match. This sketch is {this.Width} columns wide " +
                    $"and the other is {other.Width}; width follows from epsilon, so the " +
                    "two sketches were built with different ones.", nameof(other));
            }

            BitPlacement.SameColumns(this.Placement, other.Placement, nameof(other));

            for (uint i = 0; i < this.Depth; i++)
            {
                for (int j = 0; j < this.Width; j++)
                {
                    this.Matrix[i][j] += other.Matrix[i][j];
                }
            }

            this.count += other.count;
            return true;
        }

        /// <summary>
        /// Restores the CountMinSketch to its original state. It returns itself to allow
        /// for chaining.
        /// </summary>
        /// <returns>The CountMinSketch</returns>
        public CountMinSketch Reset()
        {
            this.Matrix = new UInt64[this.Depth][];
            for (uint i = 0; i < this.Depth; i++)
            {
                this.Matrix[i] = new UInt64[this.Width];
            }

            this.count = 0;
            return this;
        }

        /// <summary>
        /// Sets the hashing function used in the filter.
        /// </summary>
        /// <param name="h">The hash function to use.</param>
        public void SetHash(Func<ReadOnlySpan<byte>, ulong> h)
        {
            ArgumentNullException.ThrowIfNull(h);
            Guard.HashMayBeReplaced(this.count == 0, nameof(CountMinSketch));

            this.Hash = h;
        }

        /// <summary>
        /// Writes this sketch to a stream, in the format documented in FORMAT.md.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = new PayloadWriter();
            payload.WriteDouble(this.epsilon);
            payload.WriteDouble(this.delta);
            payload.WriteUInt32(this.Width);
            payload.WriteUInt32(this.Depth);
            BitPlacement.Write(payload, this.Placement);
            payload.WriteUInt64(this.count);

            // Row by row, each cell little-endian. The matrix is the bulk of a sketch,
            // so this is where a payload's size comes from: depth * width * 8 bytes.
            for (uint i = 0; i < this.Depth; i++)
            {
                for (uint j = 0; j < this.Width; j++)
                {
                    payload.WriteUInt64(this.Matrix[i][j]);
                }
            }

            PersistenceFormat.Write(
                stream,
                StructureId.CountMinSketch,
                PersistenceFormat.Identify(this.Hash),
                payload.WrittenSpan,
                PersistenceFormat.ColumnPlacementVersion);
        }

        /// <summary>
        /// Reads a sketch written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The sketch that was written.</returns>
        public static CountMinSketch ReadFrom(Stream stream)
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
        public static CountMinSketch ReadFrom(Stream stream, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, null, options);
        }

        /// <summary>
        /// Reads a sketch written by <see cref="WriteTo"/>, using the supplied hash
        /// function rather than the one named in the payload.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function the sketch was written with.</param>
        /// <returns>The sketch that was written.</returns>
        public static CountMinSketch ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
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
        public static CountMinSketch ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, hash, options);
        }

        private static CountMinSketch Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.CountMinSketch);
            var payload = PersistenceFormat.Read(
                stream, StructureId.CountMinSketch, out var hashId, out var version, budget, Price,
                hash, PersistenceFormat.CheckResolvable);
            var reader = new PayloadReader(payload, budget);

            var epsilon = reader.ReadDouble();
            var delta = reader.ReadDouble();
            var width = reader.ReadUInt32();
            var depth = reader.ReadUInt32();
            // From the payload, never from the width: a version 1 payload of any width was
            // placed with 32-bit values, and a version 9 one says how it was placed.
            var placement = BitPlacement.Read(ref reader, version, PersistenceFormat.ColumnPlacementVersion);
            var count = reader.ReadUInt64();

            // The stored dimensions are authoritative, not epsilon and delta. They are
            // what the sketch indexes by, and recomputing them would silently relocate
            // every cell if the sizing were ever adjusted. Epsilon and delta are kept
            // only because Epsilon() and Delta() report them.
            CheckMatrix(width, depth);

            // Each row is allocated before it is read.
            budget.Charge(RowsCost(width, depth));
            var matrix = new UInt64[depth][];
            for (uint i = 0; i < depth; i++)
            {
                matrix[i] = new UInt64[width];
                for (uint j = 0; j < width; j++)
                {
                    matrix[i][j] = reader.ReadUInt64();
                }
            }

            reader.ExpectEnd();

            return new CountMinSketch
            {
                Matrix = matrix,
                Width = width,
                Depth = depth,
                count = count,
                epsilon = epsilon,
                delta = delta,
                Hash = PersistenceFormat.ResolveOrThrow(hashId, hash),
                Placement = placement,
            };
        }

        /// <summary>
        /// Prices what <see cref="Read"/> allocates beyond the envelope: the matrix. See
        /// <see cref="PayloadPrice"/>.
        /// </summary>
        internal static void Price(ref PayloadReader reader, ushort version)
        {
            reader.ReadDouble();
            reader.ReadDouble();
            var width = reader.ReadUInt32();
            var depth = reader.ReadUInt32();
            BitPlacement.Read(ref reader, version, PersistenceFormat.ColumnPlacementVersion);
            reader.ReadUInt64();
            CheckMatrix(width, depth);
            reader.Budget.Price(RowsCost(width, depth));
        }

        private static void CheckMatrix(uint width, uint depth)
        {
            if (width == 0 || depth == 0)
            {
                throw new InvalidDataException(
                    $"Sketch has a {depth} by {width} matrix. A sketch with no rows " +
                    "reports every element as seen ulong.MaxValue times, and one with " +
                    "no columns divides by zero.");
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
        }

        /// <summary>A matrix of <paramref name="depth"/> rows of <paramref name="width"/> counters.</summary>
        private static long RowsCost(uint width, uint depth) => ReadBudget.Sum(
            ReadBudget.ArrayOf(depth, sizeof(long)),
            ReadBudget.Times(depth, ReadBudget.ArrayOf(width, sizeof(ulong))));

        /// <summary>
        /// Used only by <see cref="Read"/>, which sets every field itself. The public
        /// constructor derives the matrix dimensions from epsilon and delta, which is
        /// not how a sketch being restored gets them.
        /// </summary>
        private CountMinSketch()
        {
            this.Matrix = null!;
        }
    }
}
