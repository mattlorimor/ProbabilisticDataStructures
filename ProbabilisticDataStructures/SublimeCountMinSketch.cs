using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// SublimeCountMinSketch implements the Count-Min Sketch of Sublime, from Eslami,
    /// Bercea, Pagh and Dayan, Sublime: Sublinear Error and Space for Unbounded Skewed
    /// Streams (SIGMOD 2026).
    /// </summary>
    /// <remarks>
    /// A Count-Min Sketch has to be sized before it has seen anything. Too small and
    /// its estimates drift with the stream; too large and most of it is spent on
    /// counters holding nothing. Either way the error grows without bound as the stream
    /// does, because a fixed number of counters shared among ever more keys can do
    /// nothing else.
    /// <para>
    /// Sublime gives up the fixed size. Its arrays start at a single cache line and
    /// double whenever the stream has grown enough to warrant it, so the number of
    /// counters tracks the stream's length rather than a guess made in advance. With
    /// the default growth the arrays hold about the square root of the stream's length,
    /// which brings the expected error down to the square root as well, where a
    /// fixed-size sketch would have it growing linearly.
    /// </para>
    /// <para>
    /// The counters themselves are stored by <see cref="ValeCounterArray"/>, which
    /// gives a counter only as many bits as its count needs. That matters more here
    /// than it would elsewhere: a sketch that keeps expanding is a sketch whose
    /// counters are mostly small, and paying a fixed width for each of them is what
    /// makes an unbounded sketch expensive.
    /// </para>
    /// <para>
    /// Expansion copies each array onto itself, so a key that hashed to a counter
    /// before an expansion hashes either to that same counter or to its copy
    /// afterwards. This is why the arrays double rather than grow by some other factor,
    /// and why the counts already gathered survive it.
    /// </para>
    /// <para>
    /// A row holds at most 2^30 counters, the largest power of two an int holds. At the
    /// default growth the rows reach it after 2^60 keys, and at a growth exponent of 0.9
    /// after about 10^10. An <see cref="Add(ReadOnlySpan{byte})"/> that would double the
    /// rows past it throws an <see cref="InvalidOperationException"/> before it changes
    /// anything: the sketch goes on answering queries and can be written, and keys can be
    /// removed. A size factor that would start a row past it is refused by the
    /// constructor.
    /// </para>
    /// </remarks>
    public class SublimeCountMinSketch : IBinaryPersistable<SublimeCountMinSketch>
    {
        /// <summary>
        /// One counter array per row, all of the same width.
        /// </summary>
        private ValeCounterArray[] rows;

        /// <summary>
        /// How many counters each row holds. Always a power of two, so that the low
        /// bits of a hash pick a counter and an expansion leaves that choice intact.
        /// </summary>
        private int width;

        /// <summary>
        /// How many keys have been added.
        /// </summary>
        private ulong count;

        /// <summary>
        /// The number of keys at which the arrays next double.
        /// </summary>
        private ulong expansionLimit;

        /// <summary>
        /// The number of keys at which the arrays fold back in half, or nought if they
        /// have never doubled.
        /// </summary>
        private ulong contractionLimit;

        /// <summary>
        /// The rows as they stood just before each expansion, most recent last.
        /// </summary>
        /// <remarks>
        /// Folding an array's halves onto each other by adding them would count the
        /// keys inserted before the expansion twice, since the expansion gave both
        /// halves the same starting values. Keeping what those values were is what
        /// lets a contraction subtract them out again. The records are a geometric
        /// series, so holding all of them costs less than the sketch itself.
        /// </remarks>
        private readonly List<ValeCounterArray[]> records = new List<ValeCounterArray[]>();

        private double growthExponent;
        private double sizeFactor;
        private double delta;

        private int countersPerChunk;
        private int stubBits;

        private Func<ReadOnlySpan<byte>, ulong> Hash { get; set; }

        /// <summary>
        /// The fraction of a sketch's chunks that may fall back to tails arrays before
        /// its counters are laid out again.
        /// </summary>
        /// <remarks>
        /// A chunk on tails has stopped paying only for what it uses, so a sketch with
        /// many of them is a sketch whose stub length no longer suits its counts.
        /// </remarks>
        private const double TailsFractionBeforeRetuning = 0.03;

        /// <summary>
        /// The largest extension pool worth giving a chunk.
        /// </summary>
        /// <remarks>
        /// A pool larger than this is space that would be better spent on stubs, which
        /// hold a count in half the room an extension needs.
        /// </remarks>
        private const int MaxPoolFragments = 64;

        /// <summary>
        /// The bits in a counter, and so the longest bit length one can have.
        /// </summary>
        /// <remarks>
        /// A counter is a <c>ulong</c> and every <c>ulong</c> is a valid one, so a length
        /// runs from nought, for a counter holding nothing, to this. The tally is one slot
        /// longer than this for that reason; it was sixty-four slots, which left no room
        /// for the counters of 2^63 and more.
        /// </remarks>
        private const int CounterBits = 64;

        /// <summary>
        /// The most rows a sketch can have: ceil(ln(1 / delta)) for the smallest delta
        /// whose reciprocal is still a finite double.
        /// </summary>
        /// <remarks>
        /// Any smaller delta has an infinite reciprocal, and so no row count at all;
        /// the constructor refuses it rather than converting infinity to one. The
        /// reader refuses more rows than this, so a sketch the constructor builds is
        /// one the reader reads. It used to refuse more than sixty-four, which a delta
        /// of 1e-28 -- an ordinary double -- already passes.
        /// </remarks>
        internal static readonly int MaxDepth = (int)Math.Ceiling(Math.Log(double.MaxValue));

        /// <summary>
        /// The most counters a row can hold. A width is a power of two held in an int,
        /// and the largest of those is 2^30.
        /// </summary>
        internal const int MaxWidth = 1 << 30;

        /// <summary>
        /// The widest this sketch lets a row grow. It is <see cref="MaxWidth"/>; a test
        /// lowers it to reach the refusal at the limit without building a sketch
        /// of a gigabyte.
        /// </summary>
        internal int WidthLimit { get; set; } = MaxWidth;

        /// <summary>
        /// The width a row starts at: a chunk's worth of counters over the size factor,
        /// rounded down to a power of two.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The size factor asks for a row of 2^31 counters or more, which no row holds.
        /// </exception>
        /// <remarks>
        /// It used to start at <see cref="MaxWidth"/> for any size factor that asked for
        /// more, quietly, so that a sketch the caller believed was larger had a row of a
        /// gibibyte that could not widen. Just under 2^31 rounds down to 2^30 and is fine.
        /// </remarks>
        internal static int StartingWidth(int countersPerChunk, double sizeFactor)
        {
            var wanted = Math.Max(1.0, countersPerChunk / sizeFactor);
            if (wanted >= 2.0 * MaxWidth)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeFactor), sizeFactor,
                    $"A size factor of {sizeFactor} starts each row at {wanted:N0} counters, " +
                    $"and a row holds at most {MaxWidth:N0}: a width is a power of two held " +
                    "in an int, and 2^30 is the largest of those. A size factor above " +
                    $"{countersPerChunk / (2.0 * MaxWidth):G3} starts a row at a width it can hold.");
            }

            return 1 << BitOperations.Log2((uint)wanted);
        }

        /// <summary>
        /// Creates a sketch whose estimates hold with probability at least 1 - delta
        /// and whose arrays grow as the square root of the stream's length.
        /// </summary>
        /// <param name="delta">Probability of exceeding the error the sketch allows.</param>
        /// <param name="hash">
        /// The hash function to use, or null for the default.
        /// </param>
        public SublimeCountMinSketch(double delta, Func<ReadOnlySpan<byte>, ulong>? hash = null)
            : this(delta, 0.5, 1.0, hash)
        {
        }

        /// <summary>
        /// Creates a sketch, choosing how fast it grows.
        /// </summary>
        /// <param name="delta">Probability of exceeding the error the sketch allows.</param>
        /// <param name="growthExponent">
        /// How the counters grow with the stream: the arrays are kept at about
        /// n to this power, over the size factor. Half, the default, gives arrays of
        /// about the square root of the stream's length and an expected error of the
        /// same order. Larger values buy accuracy with memory; the exponent cannot
        /// reach one, at which point the sketch would be storing the stream.
        /// </param>
        /// <param name="sizeFactor">
        /// How many counters the sketch starts with, as a divisor: one gives a single
        /// cache line per row, a half gives two, and so on. Smaller is larger, up to a row
        /// of 2^30 counters, which a size factor of about 6e-8 starts at; a smaller one is
        /// refused.
        /// </param>
        /// <param name="hash">
        /// The hash function to use, or null for the default.
        /// </param>
        public SublimeCountMinSketch(
            double delta,
            double growthExponent,
            double sizeFactor,
            Func<ReadOnlySpan<byte>, ulong>? hash = null)
        {
            Guard.ValidSketchDelta(delta, nameof(delta));

            if (growthExponent <= 0 || growthExponent >= 1)
            {
                throw new ArgumentOutOfRangeException(nameof(growthExponent),
                    "The arrays grow as the stream's length raised to this power, " +
                    "which has to lie between nought and one. At one the sketch would " +
                    "grow as fast as the stream it is summarising.");
            }

            if (sizeFactor <= 0 || double.IsNaN(sizeFactor) || double.IsInfinity(sizeFactor))
            {
                throw new ArgumentOutOfRangeException(nameof(sizeFactor),
                    "The starting size is a cache line divided by this, so it has to " +
                    "be a positive number.");
            }

            this.delta = delta;
            this.growthExponent = growthExponent;
            this.sizeFactor = sizeFactor;
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();

            this.countersPerChunk = ValeCounterArray.DefaultCountersPerChunk;
            this.stubBits = ValeCounterArray.DefaultStubBits;

            // The paper starts each array at one chunk, scaled by the size factor. A
            // width has to be a power of two for a hash's low bits to pick a counter,
            // so the chunk's worth is rounded down to one.
            this.width = StartingWidth(this.countersPerChunk, sizeFactor);

            var rows = Math.Ceiling(Math.Log(1 / delta));
            if (rows > MaxDepth)
            {
                // Only a delta so small that its reciprocal is infinite gets here, and
                // turning infinity into a row count used to fail as an overflow from
                // inside the arithmetic.
                throw new ArgumentOutOfRangeException(nameof(delta), delta,
                    $"A delta of {delta} needs ln(1 / delta) rows, and its reciprocal is " +
                    $"past the largest double. The deepest sketch is {MaxDepth} rows, at " +
                    $"a delta of about {1 / double.MaxValue}.");
            }

            var depth = (uint)rows;
            this.rows = new ValeCounterArray[Math.Max(1, depth)];
            for (var i = 0; i < this.rows.Length; i++)
            {
                this.rows[i] = new ValeCounterArray(
                    this.width, this.countersPerChunk, this.stubBits);
            }

            this.expansionLimit = LimitFor(this.width);
            this.contractionLimit = 0;
        }

        /// <summary>
        /// Used only by <see cref="Read"/>, which sets every field itself. The public
        /// constructor sizes the arrays from the parameters, which is not how a sketch
        /// being restored gets its size.
        /// </summary>
        private SublimeCountMinSketch()
        {
            this.rows = Array.Empty<ValeCounterArray>();
            this.Hash = null!;
            this.countersPerChunk = ValeCounterArray.DefaultCountersPerChunk;
            this.stubBits = ValeCounterArray.DefaultStubBits;
        }

        /// <summary>
        /// How many counters each row currently holds. This grows with the stream.
        /// </summary>
        public int Width => this.width;

        /// <summary>
        /// How many rows the sketch keeps, which follows from delta and does not
        /// change.
        /// </summary>
        public int Depth => this.rows.Length;

        /// <summary>
        /// How many counters currently share a chunk, and how wide their fixed parts
        /// are. These are the two the sketch retunes as it grows.
        /// </summary>
        internal int CountersPerChunk => this.countersPerChunk;

        /// <inheritdoc cref="CountersPerChunk"/>
        internal int StubBits => this.stubBits;

        /// <summary>
        /// Returns the probability that an estimate exceeds the error allowed.
        /// </summary>
        public double Delta() => this.delta;

        /// <summary>
        /// Returns the number of keys added.
        /// </summary>
        public ulong TotalCount() => this.count;

        /// <summary>
        /// The expected error of an estimate at the stream's current length.
        /// </summary>
        /// <remarks>
        /// This is the Count-Min bound, e * n / w, at the width the sketch has grown
        /// to. Unlike a fixed-size sketch, where the same quantity climbs in step with
        /// the stream, here the width climbs too.
        /// </remarks>
        public double Epsilon() => Math.E * this.count / this.width;

        /// <summary>
        /// How many bytes the counters occupy.
        /// </summary>
        public long SizeInBytes
        {
            get
            {
                var total = 0L;
                foreach (var row in this.rows)
                {
                    total += row.SizeInBytes;
                }
                return total;
            }
        }

        /// <summary>
        /// Adds the data to the sketch.
        /// </summary>
        /// <remarks>
        /// A counter holds at most <c>ulong.MaxValue</c>, and an add to one that has got
        /// there leaves it there rather than wrapping it to nought, so an estimate never
        /// falls for want of room. That takes 2^64 adds to one counter, or a counter read
        /// from a payload or folded back to it.
        /// </remarks>
        /// <param name="data">The data to add.</param>
        /// <returns>The sketch.</returns>
        public SublimeCountMinSketch Add(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            return this.Add(data.AsSpan());
        }

        /// <inheritdoc cref="Add(byte[])"/>
        public SublimeCountMinSketch Add(ReadOnlySpan<byte> data)
        {
            // An add that would widen the rows past what a row holds is refused before it
            // touches anything. Left to the expansion at its end, it would have
            // incremented the counters and the count and then failed to widen, leaving a
            // sketch that had taken the add and not the growth it called for.
            if (this.count + 1 >= this.expansionLimit && (long)this.width * 2 > this.WidthLimit)
            {
                throw new InvalidOperationException(
                    $"This sketch holds {this.count:N0} keys, and the next would double its " +
                    $"rows from {this.width:N0} counters to {(long)this.width * 2:N0}. A row " +
                    $"holds at most {this.WidthLimit:N0}: a width is a power of two held in an " +
                    "int, and 2^30 is the largest of those. Nothing was added and the sketch " +
                    "is unchanged; it can still be queried and written, and keys can be " +
                    "removed. To count more, grow more slowly -- a smaller growth exponent, " +
                    "or a larger size factor -- or spread the stream over several sketches " +
                    "and add their counts.");
            }

            var kernel = Utils.HashKernel(data, this.Hash);

            for (var i = 0; i < this.rows.Length; i++)
            {
                this.rows[i].Increment(ColumnOf(kernel, i));
            }

            this.count++;

            if (this.count >= this.expansionLimit)
            {
                Expand();
            }
            else
            {
                RetuneIfCrowded();
            }

            return this;
        }

        /// <summary>
        /// Takes the data back out of the sketch.
        /// </summary>
        /// <remarks>
        /// Removing something never added, or removing it more often than it was added,
        /// leaves the sketch holding counts that are too low rather than refusing. The
        /// sketch does not know what it holds, so it cannot tell the difference.
        /// </remarks>
        /// <param name="data">The data to remove.</param>
        /// <returns>The sketch.</returns>
        public SublimeCountMinSketch Remove(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            return this.Remove(data.AsSpan());
        }

        /// <inheritdoc cref="Remove(byte[])"/>
        public SublimeCountMinSketch Remove(ReadOnlySpan<byte> data)
        {
            var kernel = Utils.HashKernel(data, this.Hash);

            for (var i = 0; i < this.rows.Length; i++)
            {
                this.rows[i].Decrement(ColumnOf(kernel, i));
            }

            if (this.count > 0)
            {
                this.count--;
            }

            if (this.count < this.contractionLimit && this.records.Count > 0)
            {
                Contract();
            }

            return this;
        }

        /// <summary>
        /// Returns the approximate count for the data.
        /// </summary>
        /// <param name="data">The data to count.</param>
        public ulong Count(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            return this.Count(data.AsSpan());
        }

        /// <inheritdoc cref="Count(byte[])"/>
        public ulong Count(ReadOnlySpan<byte> data)
        {
            var kernel = Utils.HashKernel(data, this.Hash);
            var smallest = ulong.MaxValue;

            for (var i = 0; i < this.rows.Length; i++)
            {
                smallest = Math.Min(smallest, this.rows[i].Get(ColumnOf(kernel, i)));
            }

            return smallest;
        }

        /// <summary>
        /// Restores the sketch to its original state.
        /// </summary>
        public SublimeCountMinSketch Reset()
        {
            this.countersPerChunk = ValeCounterArray.DefaultCountersPerChunk;
            this.stubBits = ValeCounterArray.DefaultStubBits;

            this.width = StartingWidth(this.countersPerChunk, this.sizeFactor);

            for (var i = 0; i < this.rows.Length; i++)
            {
                this.rows[i] = new ValeCounterArray(
                    this.width, this.countersPerChunk, this.stubBits);
            }

            this.records.Clear();
            this.count = 0;
            this.expansionLimit = LimitFor(this.width);
            this.contractionLimit = 0;
            return this;
        }

        /// <summary>
        /// Sets the hashing function used in the sketch.
        /// </summary>
        /// <param name="h">The hash function to use.</param>
        public void SetHash(Func<ReadOnlySpan<byte>, ulong> h)
        {
            ArgumentNullException.ThrowIfNull(h);
            Guard.HashMayBeReplaced(this.count == 0, nameof(SublimeCountMinSketch));
            this.Hash = h;
        }


        /// <summary>
        /// Writes the sketch to a stream.
        /// </summary>
        /// <remarks>
        /// The payload holds the counts, not the way they are packed. Packing depends
        /// on two parameters the sketch retunes as it runs, so a payload carrying the
        /// packed bits would be a payload that only the version that wrote it could
        /// safely believe -- and a corrupt one would not look corrupt, it would decode
        /// into different counters. Counts cost more room and every ulong is a valid
        /// one. The sketch packs them again on the way in, at a tuning chosen to suit
        /// them.
        /// </remarks>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = new PayloadWriter();
            payload.WriteDouble(this.delta);
            payload.WriteDouble(this.growthExponent);
            payload.WriteDouble(this.sizeFactor);
            payload.WriteUInt32((uint)this.rows.Length);
            payload.WriteUInt32((uint)this.width);
            payload.WriteUInt64(this.count);
            payload.WriteUInt64(this.expansionLimit);
            payload.WriteUInt64(this.contractionLimit);

            // The records of earlier states, oldest first, each half the width of the
            // one after it. Without them the sketch could grow but never fold back.
            payload.WriteUInt32((uint)this.records.Count);
            foreach (var record in this.records)
            {
                payload.WriteUInt32((uint)record[0].Count);
                WriteCounters(payload, record);
            }

            WriteCounters(payload, this.rows);

            PersistenceFormat.Write(
                stream,
                StructureId.SublimeCountMinSketch,
                PersistenceFormat.Identify(this.Hash),
                payload.WrittenSpan);
        }

        private static void WriteCounters(PayloadWriter payload, ValeCounterArray[] rows)
        {
            foreach (var row in rows)
            {
                for (var j = 0; j < row.Count; j++)
                {
                    payload.WriteUInt64(row.Get(j));
                }
            }
        }

        /// <summary>
        /// Reads a sketch written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        public static SublimeCountMinSketch ReadFrom(Stream stream)
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
        public static SublimeCountMinSketch ReadFrom(Stream stream, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, null, options);
        }

        /// <summary>
        /// Reads a sketch written by <see cref="WriteTo"/>, installing a hash function.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function the sketch was written with.</param>
        public static SublimeCountMinSketch ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
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
        public static SublimeCountMinSketch ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, hash, options);
        }

        private static SublimeCountMinSketch Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.SublimeCountMinSketch);
            var payload = PersistenceFormat.Read(
                stream, StructureId.SublimeCountMinSketch, out var hashId, budget, Price,
                hash, PersistenceFormat.CheckResolvable);
            var reader = new PayloadReader(payload, budget);

            var delta = reader.ReadDouble();
            var growthExponent = reader.ReadDouble();
            var sizeFactor = reader.ReadDouble();
            var depth = reader.ReadUInt32();
            var width = reader.ReadUInt32();
            CheckShape(delta, growthExponent, sizeFactor, depth, width);

            var sketch = new SublimeCountMinSketch
            {
                delta = delta,
                growthExponent = growthExponent,
                sizeFactor = sizeFactor,
                Hash = PersistenceFormat.ResolveOrThrow(hashId, hash),
            };

            sketch.count = reader.ReadUInt64();
            sketch.expansionLimit = reader.ReadUInt64();
            sketch.contractionLimit = reader.ReadUInt64();

            var recordCount = reader.ReadUInt32();
            var expected = FirstRecordWidth(recordCount, width);

            for (var i = 0; i < recordCount; i++)
            {
                var recordWidth = reader.ReadUInt32();
                CheckRecord(i, recordWidth, expected);

                sketch.width = (int)recordWidth;
                sketch.records.Add(ReadCounters(ref reader, (int)depth, (int)recordWidth));
                expected *= 2;
            }

            sketch.width = (int)width;
            sketch.rows = ReadCounters(ref reader, (int)depth, (int)width);
            reader.ExpectEnd();

            // The constructor left the defaults, and the rows were built at a tuning chosen
            // from the counts. The properties describe the rows, not the records of earlier
            // states, which were tuned separately at their own widths. A sketch has at
            // least one row, and every row is built at the same tuning.
            sketch.countersPerChunk = sketch.rows[0].CountersPerChunk;
            sketch.stubBits = sketch.rows[0].StubBits;

            return sketch;
        }

        /// <summary>
        /// Prices what <see cref="Read"/> allocates beyond the envelope: for the rows and
        /// for each record of an earlier state, the plain counts read and the packed
        /// counters they are tuned for and moved into. See <see cref="PayloadPrice"/>.
        /// </summary>
        internal static void Price(ref PayloadReader reader, ushort version)
        {
            var delta = reader.ReadDouble();
            var growthExponent = reader.ReadDouble();
            var sizeFactor = reader.ReadDouble();
            var depth = reader.ReadUInt32();
            var width = reader.ReadUInt32();
            CheckShape(delta, growthExponent, sizeFactor, depth, width);

            reader.ReadUInt64();
            reader.ReadUInt64();
            reader.ReadUInt64();

            var recordCount = reader.ReadUInt32();
            var expected = FirstRecordWidth(recordCount, width);

            for (var i = 0; i < recordCount; i++)
            {
                var recordWidth = reader.ReadUInt32();
                CheckRecord(i, recordWidth, expected);
                PriceCounters(ref reader, (int)depth, (int)recordWidth);
                expected *= 2;
            }

            PriceCounters(ref reader, (int)depth, (int)width);
        }

        /// <summary>
        /// Prices what <see cref="ReadCounters"/> allocates, reading the counts it reads
        /// for what tuning them chooses, and moves past them.
        /// </summary>
        private static void PriceCounters(ref PayloadReader reader, int depth, int forWidth)
        {
            reader.Budget.Price(CountsCost(depth, forWidth));

            Span<long> lengths = stackalloc long[CounterLengthCount];
            for (var i = 0L; i < (long)depth * forWidth; i++)
            {
                Tally(lengths, reader.ReadUInt64());
            }

            reader.Budget.Price(PackedCost(depth, forWidth, Tune(lengths).CountersPerChunk));
        }

        /// <summary>The rows of plain counts one state is read into.</summary>
        private static long CountsCost(int depth, int forWidth) => ReadBudget.Sum(
            ReadBudget.ArrayOf((ulong)depth, sizeof(long)),
            ReadBudget.Times((ulong)depth, ReadBudget.ArrayOf((ulong)forWidth, sizeof(ulong))));

        /// <summary>The rows of packed counters one state is moved into, at a tuning.</summary>
        private static long PackedCost(int depth, int forWidth, int chunkCounters) => ReadBudget.Sum(
            ReadBudget.ArrayOf((ulong)depth, sizeof(long)),
            ReadBudget.Times((ulong)depth, ValeCounterArray.RestoreCost(forWidth, chunkCounters)));

        /// <summary>
        /// The width the first record of <paramref name="recordCount"/> must have, after
        /// checking a sketch <paramref name="width"/> wide can have doubled that often.
        /// </summary>
        private static uint FirstRecordWidth(uint recordCount, uint width)
        {
            if (recordCount > 30)
            {
                throw new InvalidDataException(
                    $"Sketch carries {recordCount} records of earlier states. Each " +
                    "stands for a doubling, and thirty doublings is more counters " +
                    "than a row may hold.");
            }

            var expected = width;
            for (var i = 0; i < recordCount; i++)
            {
                expected /= 2;
            }
            if (expected == 0)
            {
                throw new InvalidDataException(
                    $"Sketch carries {recordCount} records but is only {width} " +
                    "counters wide, so it cannot have doubled that many times.");
            }

            return expected;
        }

        private static void CheckRecord(int i, uint recordWidth, uint expected)
        {
            if (recordWidth != expected)
            {
                throw new InvalidDataException(
                    $"Record {i} claims {recordWidth} counters where the " +
                    $"doublings that follow it require {expected}.");
            }
        }

        /// <summary>
        /// Checks the shape a payload claims, as both the read and its price do before
        /// any counters are allocated.
        /// </summary>
        private static void CheckShape(
            double delta, double growthExponent, double sizeFactor, uint depth, uint width)
        {
            if (double.IsNaN(delta) || delta <= 0 || delta >= 1)
            {
                throw new InvalidDataException(
                    $"Sketch claims a delta of {delta}, and a probability of failure " +
                    "lies between nought and one.");
            }
            if (double.IsNaN(growthExponent) || growthExponent <= 0 || growthExponent >= 1)
            {
                throw new InvalidDataException(
                    $"Sketch claims a growth exponent of {growthExponent}, which has " +
                    "to lie between nought and one.");
            }
            if (double.IsNaN(sizeFactor) || double.IsInfinity(sizeFactor) || sizeFactor <= 0)
            {
                throw new InvalidDataException(
                    $"Sketch claims a size factor of {sizeFactor}, which has to be " +
                    "a positive number.");
            }
            // The constructor's own limit, so that every sketch it builds reads back.
            if (depth == 0 || depth > MaxDepth)
            {
                throw new InvalidDataException(
                    $"Sketch claims {depth} rows. A sketch has at least one, and more " +
                    $"than {MaxDepth} would mean a delta whose reciprocal no double can " +
                    "express.");
            }
            if (width == 0 || width > MaxWidth || BitOperations.PopCount(width) != 1)
            {
                throw new InvalidDataException(
                    $"Sketch claims {width:N0} counters a row. A width is a power of two " +
                    "-- the low bits of a hash pick the counter -- no larger than " +
                    $"{MaxWidth:N0}, the largest an int holds, and this is not one.");
            }
        }

        /// <summary>
        /// Reads one state's counts and packs them.
        /// </summary>
        /// <remarks>
        /// The reader is passed by reference because it is a ref struct holding its own
        /// position. Taken by value it would read the right bytes and leave the caller
        /// where it started, which looks like a payload with everything left over.
        /// </remarks>
        private static ValeCounterArray[] ReadCounters(
            ref PayloadReader reader, int depth, int forWidth)
        {
            // Each row of plain counts is allocated before it is read.
            reader.Budget.Charge(CountsCost(depth, forWidth));
            var values = new ulong[depth][];
            for (var i = 0; i < depth; i++)
            {
                values[i] = new ulong[forWidth];
                for (var j = 0; j < forWidth; j++)
                {
                    values[i][j] = reader.ReadUInt64();
                }
            }

            var (chunkCounters, stub) = Tune(values);

            // And then packed, at the tuning just chosen.
            reader.Budget.Charge(PackedCost(depth, forWidth, chunkCounters));
            var rows = new ValeCounterArray[depth];
            for (var i = 0; i < depth; i++)
            {
                rows[i] = new ValeCounterArray(forWidth, chunkCounters, stub);
                for (var j = 0; j < forWidth; j++)
                {
                    if (values[i][j] != 0)
                    {
                        rows[i].Set(j, values[i][j]);
                    }
                }
            }

            return rows;
        }

        /// <summary>
        /// Which counter in a row the data falls in.
        /// </summary>
        /// <remarks>
        /// The width is a power of two and the low bits of the hash pick the counter,
        /// so doubling the width leaves a key on either the counter it had or the copy
        /// of it in the new half. Taking the remainder by a width that was not a power
        /// of two would not have that property, and every count gathered before an
        /// expansion would be stranded on the wrong counter.
        /// </remarks>
        private int ColumnOf(in HashKernelReturnValue kernel, int row) =>
            (int)((kernel.LowerBaseHash + kernel.UpperBaseHash * (uint)row)
                & (uint)(this.width - 1));

        /// <summary>
        /// The number of keys at which arrays of the given width should double.
        /// </summary>
        /// <remarks>
        /// The paper keeps the width at about n to the growth exponent, over the size
        /// factor, so the width is due to change once n reaches the inverse of that.
        /// </remarks>
        private ulong LimitFor(int forWidth)
        {
            var keys = Math.Pow(forWidth * this.sizeFactor, 1 / this.growthExponent);
            return keys >= ulong.MaxValue ? ulong.MaxValue : (ulong)Math.Ceiling(keys);
        }

        /// <summary>
        /// Doubles every row, copying each counter into the new half.
        /// </summary>
        /// <remarks>
        /// A key whose hash chose counter j now chooses j or j + w, and both hold what
        /// j held. Its count is therefore preserved, at the cost of being counted twice
        /// over the sketch as a whole -- which is the error the paper accepts, and
        /// which shrinks in importance as the keys inserted after the expansion come to
        /// outnumber those inserted before it.
        /// </remarks>
        private void Expand()
        {
            var doubled = this.width * 2;

            this.records.Add(this.rows);
            Retune(doubled);

            this.expansionLimit = LimitFor(doubled);
            this.contractionLimit = ContractionLimitFor(doubled);
        }

        /// <summary>
        /// The number of keys below which arrays of the given width fold back in half.
        /// </summary>
        /// <remarks>
        /// The paper puts this at the average of the two expansion thresholds already
        /// crossed, deliberately far below the one ahead, so that a sketch sitting near
        /// a threshold does not resize on every second update. The authors' own
        /// implementation instead contracts at the threshold just crossed, which is the
        /// thrashing the paper describes avoiding, so this follows the paper.
        /// </remarks>
        private ulong ContractionLimitFor(int forWidth)
        {
            var half = LimitFor(Math.Max(1, forWidth / 2));
            var quarter = LimitFor(Math.Max(1, forWidth / 4));
            return half / 2 + quarter / 2;
        }

        /// <summary>
        /// Folds every row back in half, keeping the updates made since the expansion
        /// that doubled it.
        /// </summary>
        /// <remarks>
        /// Both halves start an expansion holding what the single array held, so what
        /// each has gathered since is its current value less that starting value. A
        /// counter's new value is those two gains added back to the value it started
        /// from, which is the same as the two halves added together with the record
        /// taken off once.
        /// <para>
        /// A counter can end up below where it started, since deletions are what brings
        /// a sketch here in the first place, so nothing is allowed to fall below nought.
        /// Nor past the largest count a counter holds, which the sum of two halves can
        /// reach. The sum is taken in 128 bits, unsigned: in signed longs, as it once was,
        /// a counter of 2^63 or more came out negative and was folded to nought.
        /// </para>
        /// </remarks>
        private void Contract()
        {
            var record = this.records[^1];
            this.records.RemoveAt(this.records.Count - 1);

            var halved = this.width / 2;
            var folded = new ulong[this.rows.Length][];
            for (var i = 0; i < this.rows.Length; i++)
            {
                folded[i] = new ulong[halved];
                for (var j = 0; j < halved; j++)
                {
                    var gathered = (UInt128)this.rows[i].Get(j) + this.rows[i].Get(j + halved);
                    var started = (UInt128)record[i].Get(j);
                    var kept = gathered > started ? gathered - started : UInt128.Zero;
                    folded[i][j] = kept > ulong.MaxValue ? ulong.MaxValue : (ulong)kept;
                }
            }

            this.width = halved;
            LayOut(folded);

            this.expansionLimit = LimitFor(halved);
            this.contractionLimit = this.records.Count == 0
                ? 0
                : ContractionLimitFor(halved);
        }

        /// <summary>
        /// Lays the counters out again, at a width and a tuning suited to what they now
        /// hold.
        /// </summary>
        private void Retune(int newWidth)
        {
            var values = new ulong[this.rows.Length][];
            for (var i = 0; i < this.rows.Length; i++)
            {
                values[i] = new ulong[newWidth];
                for (var j = 0; j < newWidth; j++)
                {
                    values[i][j] = this.rows[i].Get(j & (this.width - 1));
                }
            }

            this.width = newWidth;
            LayOut(values);
        }

        /// <summary>
        /// Stores the given counts, choosing a tuning to suit them.
        /// </summary>
        private void LayOut(ulong[][] values)
        {
            var (chunkCounters, stub) = Tune(values);

            var rebuilt = new ValeCounterArray[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                var row = new ValeCounterArray(this.width, chunkCounters, stub);
                for (var j = 0; j < this.width; j++)
                {
                    if (values[i][j] != 0)
                    {
                        row.Set(j, values[i][j]);
                    }
                }
                rebuilt[i] = row;
            }

            this.rows = rebuilt;
            this.countersPerChunk = chunkCounters;
            this.stubBits = stub;
        }

        /// <summary>
        /// Lays the counters out again if too many chunks have given up on their pools.
        /// </summary>
        private void RetuneIfCrowded()
        {
            var chunks = 0;
            var onTails = 0;
            foreach (var row in this.rows)
            {
                chunks += row.ChunkCount;
                onTails += row.ChunksWithTails;
            }

            if (onTails > chunks * TailsFractionBeforeRetuning)
            {
                Retune(this.width);
            }
        }

        /// <summary>
        /// Chooses how many counters share a chunk and how wide their stubs are.
        /// </summary>
        /// <remarks>
        /// The paper's rule, and worth stating because the parameters are not a detail:
        /// with the wrong ones the counters can take several times the room a
        /// fixed-width array would. More counters per chunk is always better for space
        /// -- a chunk is a cache line whatever it holds -- so the search runs from the
        /// most crowded downwards and takes the first tuning that will not spill.
        /// <para>
        /// Whether it will spill is a question about a sum of independent contributions:
        /// each counter lands in one chunk of many, and contributes the length of its
        /// extension if it lands in this one. That gives an expected total and a
        /// variance per chunk, and Chebyshev's inequality bounds the fraction of chunks
        /// whose total runs past the pool. Tunings that put that fraction above three
        /// in a hundred are passed over.
        /// </para>
        /// </remarks>
        private static (int CountersPerChunk, int StubBits) Tune(ulong[][] values) =>
            Tune(CounterLengths(values));

        /// <summary>
        /// <see cref="Tune(ulong[][])"/>, from how many counters hold a value of each bit
        /// length, which is all it looks at: so that a price can tune counters it has only
        /// read past.
        /// </summary>
        private static (int CountersPerChunk, int StubBits) Tune(ReadOnlySpan<long> lengths)
        {
            var counters = 0L;
            foreach (var atLength in lengths)
            {
                counters += atLength;
            }

            if (counters == 0)
            {
                return (ValeCounterArray.DefaultCountersPerChunk,
                    ValeCounterArray.DefaultStubBits);
            }

            // The widest stub the fewest counters can have with a pool left, which is the
            // answer if nothing else is. It is found by the loop below, from the same two
            // tests that decide which stubs it tries, and not named here: the widest stub
            // there is, MaxStubBits, leaves no pool at sixteen counters, and the array
            // refuses it.
            var widestStub = ValeCounterArray.MinStubBits;

            for (var c = ValeCounterArray.MaxCountersPerChunk;
                 c >= ValeCounterArray.MinCountersPerChunk;
                 c--)
            {
                var chunks = Math.Max(1.0, (double)counters / c);

                for (var s = ValeCounterArray.MinStubBits;
                     s <= ValeCounterArray.MaxStubBits;
                     s++)
                {
                    var poolBits = ValeCounterArray.ChunkBits - c * (s + 1) - 1;

                    // A pool this large is space that stubs would use better, so try a
                    // wider stub; one this small is not worth keeping, so try fewer
                    // counters instead.
                    if (poolBits > 2 * MaxPoolFragments)
                    {
                        continue;
                    }
                    if (poolBits < 2 * ValeCounterArray.MinPoolFragments)
                    {
                        break;
                    }

                    if (c == ValeCounterArray.MinCountersPerChunk)
                    {
                        widestStub = s;
                    }

                    var expected = 0.0;
                    var variance = 0.0;
                    for (var bits = s + 1; bits <= CounterBits; bits++)
                    {
                        if (lengths[bits] == 0)
                        {
                            continue;
                        }

                        var cost = ValeCounter.ExtensionLength((1UL << (bits - s)) - 1)
                            * ValeCounter.FragmentBits;
                        var mean = cost / chunks;

                        expected += mean * lengths[bits];
                        variance += ((double)cost * cost / chunks - mean * mean)
                            * lengths[bits];
                    }

                    var room = poolBits - expected;
                    if (room < 0)
                    {
                        continue;
                    }

                    if (room > 0 && variance / (room * room) > TailsFractionBeforeRetuning)
                    {
                        continue;
                    }

                    return (c, s);
                }
            }

            // Nothing satisfied the bound, which means the counts have outgrown what a
            // cache line can hold cheaply. The widest stub with a pool left is the best
            // remaining answer; the tails arrays will carry the rest.
            return (ValeCounterArray.MinCountersPerChunk, widestStub);
        }

        /// <summary>
        /// How many counters hold a value of each bit length, counted over the sketch
        /// as it will be once it has been laid out at the given width.
        /// </summary>
        /// <remarks>
        /// Indexed by length, from nought to <see cref="CounterBits"/> inclusive. Internal
        /// so that a test can ask it what it tallied, which the tuning built on it hides.
        /// </remarks>
        internal static long[] CounterLengths(ulong[][] values)
        {
            var lengths = new long[CounterLengthCount];

            foreach (var row in values)
            {
                foreach (var value in row)
                {
                    Tally(lengths, value);
                }
            }

            return lengths;
        }

        /// <summary>
        /// How many lengths <see cref="CounterLengths"/> tallies counters by: every one
        /// from nought to <see cref="CounterBits"/>.
        /// </summary>
        private const int CounterLengthCount = CounterBits + 1;

        /// <summary>Counts <paramref name="value"/> at its bit length.</summary>
        private static void Tally(Span<long> lengths, ulong value)
        {
            lengths[CounterBits - BitOperations.LeadingZeroCount(value)]++;
        }
    }
}
