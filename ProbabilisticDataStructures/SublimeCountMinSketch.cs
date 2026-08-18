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
        /// cache line per row, a half gives two, and so on. Smaller is larger.
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
            var wanted = Math.Max(1.0, this.countersPerChunk / sizeFactor);
            this.width = 1 << BitOperations.Log2((uint)Math.Min(wanted, 1 << 30));

            var depth = (uint)Math.Ceiling(Math.Log(1 / delta));
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

            var wanted = Math.Max(1.0, this.countersPerChunk / this.sizeFactor);
            this.width = 1 << BitOperations.Log2((uint)Math.Min(wanted, 1 << 30));

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
            return Read(stream, null);
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
            return Read(stream, hash);
        }

        private static SublimeCountMinSketch Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(
                stream, StructureId.SublimeCountMinSketch, out var hashId);
            var reader = new PayloadReader(payload);

            var delta = reader.ReadDouble();
            var growthExponent = reader.ReadDouble();
            var sizeFactor = reader.ReadDouble();
            var depth = reader.ReadUInt32();
            var width = reader.ReadUInt32();

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
            if (depth == 0 || depth > 64)
            {
                throw new InvalidDataException(
                    $"Sketch claims {depth} rows. A sketch has at least one, and " +
                    "sixty-four would mean a delta no double can express.");
            }
            if (width == 0 || width > 1 << 30 || BitOperations.PopCount(width) != 1)
            {
                throw new InvalidDataException(
                    $"Sketch claims {width} counters a row. A width is a power of two " +
                    "-- the low bits of a hash pick the counter -- and this is not.");
            }

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

            for (var i = 0; i < recordCount; i++)
            {
                var recordWidth = reader.ReadUInt32();
                if (recordWidth != expected)
                {
                    throw new InvalidDataException(
                        $"Record {i} claims {recordWidth} counters where the " +
                        $"doublings that follow it require {expected}.");
                }

                sketch.width = (int)recordWidth;
                sketch.records.Add(ReadCounters(ref reader, (int)depth, (int)recordWidth));
                expected *= 2;
            }

            sketch.width = (int)width;
            sketch.rows = ReadCounters(ref reader, (int)depth, (int)width);
            reader.ExpectEnd();

            return sketch;
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
        /// a sketch here in the first place, so the arithmetic is done signed and
        /// nothing is allowed to fall below nought.
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
                    var kept = (long)this.rows[i].Get(j)
                        + (long)this.rows[i].Get(j + halved)
                        - (long)record[i].Get(j);
                    folded[i][j] = kept > 0 ? (ulong)kept : 0;
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
        private static (int CountersPerChunk, int StubBits) Tune(ulong[][] values)
        {
            var lengths = CounterLengths(values);

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

                    var expected = 0.0;
                    var variance = 0.0;
                    for (var bits = s + 1; bits < 64; bits++)
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
            return (ValeCounterArray.MinCountersPerChunk, ValeCounterArray.MaxStubBits);
        }

        /// <summary>
        /// How many counters hold a value of each bit length, counted over the sketch
        /// as it will be once it has been laid out at the given width.
        /// </summary>
        private static long[] CounterLengths(ulong[][] values)
        {
            var lengths = new long[64];

            foreach (var row in values)
            {
                foreach (var value in row)
                {
                    lengths[64 - BitOperations.LeadingZeroCount(value)]++;
                }
            }

            return lengths;
        }
    }
}
