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
    /// A row holds at most 2^30 counters, the largest power of two an int holds, and the
    /// rows reach it when the count reaches <c>(2^30 · s)^(1/g)</c> for the growth exponent
    /// g and size factor s. At the defaults that is 2^60 keys, and at a growth exponent of
    /// 0.9 about 10^10. A larger exponent stops much sooner: at a size factor of one, an
    /// exponent past 0.9375 stops below 2^32 keys, about 3.2 billion at 0.95 and 1.3
    /// billion at 0.99. An <see cref="Add(ReadOnlySpan{byte})"/> that would double the
    /// rows past it throws an <see cref="InvalidOperationException"/> before it changes
    /// anything: the sketch goes on answering queries and can be written, and keys can be
    /// removed. A size factor that would start a row past it is refused by the
    /// constructor.
    /// </para>
    /// </remarks>
    public class SublimeCountMinSketch : IBinaryPersistable<SublimeCountMinSketch>, IPayloadBody
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
        /// Called before each row of counters that a doubling, a folding, a repacking or a
        /// reset allocates, plain counts and packed rows alike, and before each tails array
        /// that a row of the sketch allocates for a chunk that has outgrown its pool. It is
        /// null; a test sets it to throw an <see cref="OutOfMemoryException"/> at one of them,
        /// to reach a resize that cannot get its memory without building a sketch of
        /// gigabytes.
        /// </summary>
        /// <remarks>
        /// Setting it hands it to every row the sketch holds, and the sketch hands it to every
        /// row it builds. The records of earlier states are left out: they are only read, so
        /// they never allocate.
        /// </remarks>
        internal Action? BeforeAllocating
        {
            get => this.beforeAllocating;
            set
            {
                this.beforeAllocating = value;
                foreach (var row in this.rows)
                {
                    row.BeforeAllocatingTails = value;
                }
            }
        }

        private Action? beforeAllocating;

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
        /// from a payload or folded back to it. <see cref="TotalCount"/> stops there too,
        /// which only a sketch whose rows double past any count a <c>ulong</c> holds can
        /// reach, after 2^64 - 1 adds.
        /// <para>
        /// The add that doubles the rows builds the doubled rows, and the copy of the rows it
        /// keeps to fold them back by, before it changes anything. The doubled rows are the
        /// most a sketch allocates at once, eight bytes a counter before they are packed, and
        /// if it cannot get everything it throws the <see cref="OutOfMemoryException"/> with
        /// the sketch as it was before the add: the key is not counted, so adding it again
        /// counts it once. So does an add that crowds the rows, by putting more than three in
        /// a hundred of their chunks on tails arrays, into being laid out again: it builds them
        /// laid out, the key counted, before it changes anything. And so does an add that puts
        /// chunks on tails arrays without crowding the rows: it allocates the arrays before it
        /// takes any of its increments.
        /// </para>
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
            // The add that brings the count to the limit is the one that doubles the rows.
            // A limit held at the largest ulong stands for one past any count a ulong
            // holds (see LimitFor), and is never reached: doubling there, the rows twice as
            // wide were due at the same held limit, so every add that took the count back
            // up to it after a removal doubled them again.
            var expands = this.expansionLimit != ulong.MaxValue
                && this.count + 1 >= this.expansionLimit;

            // An add that would widen the rows past what a row holds is refused before it
            // touches anything. Left to the expansion at its end, it would have
            // incremented the counters and the count and then failed to widen, leaving a
            // sketch that had taken the add and not the growth it called for.
            if (expands && (long)this.width * 2 > this.WidthLimit)
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

            // The add that doubles the rows takes its key only once the doubled rows have
            // been built, which is the part that can run out of memory.
            if (expands)
            {
                Expand(kernel);
                return this;
            }

            // So does an add whose increments move chunks to tails arrays, which allocates them,
            // or crowds the rows into being laid out again. Most of an add's increments stop at
            // a stub with room, which can neither allocate nor move a chunk, so those are taken
            // at once, and only a row whose stub is full is asked whether its increment would
            // move its chunk. An add that would move one takes the others back, exactly, before
            // it allocates anything, so that the rows are built, or the tails arrays allocated,
            // from the sketch as it was.
            for (var i = 0; i < this.rows.Length; i++)
            {
                if (!this.rows[i].IncrementInStub(ColumnOf(kernel, i)))
                {
                    if (TakeFullStubsOrRepack(kernel, i))
                    {
                        return this;
                    }

                    break;
                }
            }

            // The count stops at the largest a ulong holds, as a counter does. It wrapped
            // to nought, and took the total, the expected error and the schedule of
            // doublings back to their start with it.
            this.count = OneMore(this.count);
            return this;
        }

        /// <summary>
        /// Takes the rest of an add whose increment in row <paramref name="first"/> found its
        /// stub full, the rows before it having taken theirs in their stubs: the remaining rows'
        /// increments, or, if the increments past a full stub would move chunks to tails
        /// arrays, none of them until it has what that needs. Then it takes back those already
        /// taken in their stubs, and either lays the rows out again with the key counted, if
        /// the moves would crowd them, or allocates the tails arrays and only then takes every
        /// row's increment. Returns whether it laid the rows out, which counts the key;
        /// otherwise the add counts it.
        /// </summary>
        /// <remarks>
        /// Apart from <see cref="Add(ReadOnlySpan{byte})"/> so that an add whose increments
        /// all stop at a stub, which is most of them, pays for none of it.
        /// </remarks>
        private bool TakeFullStubsOrRepack(in HashKernelReturnValue kernel, int first)
        {
            // The row that found its stub full finds it full again, and changes nothing.
            Span<bool> full = stackalloc bool[this.rows.Length];
            for (var i = first; i < this.rows.Length; i++)
            {
                full[i] = !this.rows[i].IncrementInStub(ColumnOf(kernel, i));
            }

            var moving = 0;
            for (var i = 0; i < this.rows.Length; i++)
            {
                if (full[i] && this.rows[i].IncrementMovesToTails(ColumnOf(kernel, i)))
                {
                    moving++;
                }
            }

            // An increment past a full stub that moves no chunk allocates nothing, so those
            // are taken where they lie, as the stubs' were.
            if (moving == 0)
            {
                for (var i = 0; i < this.rows.Length; i++)
                {
                    if (full[i])
                    {
                        this.rows[i].Increment(ColumnOf(kernel, i));
                    }
                }

                return false;
            }

            // The rest allocate, laying the rows out or moving the chunks, and either can run out
            // of memory, so the sketch is put back as it was before the add before either does.
            for (var i = 0; i < this.rows.Length; i++)
            {
                if (!full[i])
                {
                    this.rows[i].TakeBackFromStub(ColumnOf(kernel, i));
                }
            }

            if (Crowded(ChunksOnTails() + moving))
            {
                Repack(kernel);
                return true;
            }

            TakeMovingIncrements(kernel);
            return false;
        }

        /// <summary>
        /// Takes an add none of whose increments has been taken and some of which move a chunk
        /// to a tails array, without crowding the rows: it allocates each of those arrays
        /// first, and only then takes every row's increment, which then allocates nothing.
        /// </summary>
        /// <remarks>
        /// So an add that runs out of memory here leaves the sketch as it was, and the add
        /// tried again counts the key once. It used to take each row's increment in turn, and
        /// the increment that moved a chunk allocated its tails array after writing the
        /// counter's stub, so a failure there left that counter at its stub carried and its
        /// high bits not, nought for a counter of 31, and the estimate with it, below what was
        /// added; the rows before it had counted the key (#274).
        /// </remarks>
        private void TakeMovingIncrements(in HashKernelReturnValue kernel)
        {
            var tails = new ulong[]?[this.rows.Length];
            for (var i = 0; i < this.rows.Length; i++)
            {
                if (this.rows[i].IncrementMovesToTails(ColumnOf(kernel, i)))
                {
                    tails[i] = this.rows[i].ReserveTails();
                }
            }

            // Allocated; from here the increments are taken, and nothing allocates.
            for (var i = 0; i < this.rows.Length; i++)
            {
                var column = ColumnOf(kernel, i);
                if (tails[i] is { } reserved)
                {
                    this.rows[i].MoveChunkToTails(column, reserved);
                }

                this.rows[i].Increment(column);
            }
        }

        /// <summary>
        /// One more than a count, stopping at the largest a <c>ulong</c> holds, as an
        /// increment of a counter does.
        /// </summary>
        private static ulong OneMore(ulong count) =>
            count == ulong.MaxValue ? count : count + 1;

        /// <summary>
        /// Takes the data back out of the sketch.
        /// </summary>
        /// <remarks>
        /// Removing something never added, or removing it more often than it was added,
        /// leaves the sketch holding counts that are too low rather than refusing. The
        /// sketch does not know what it holds, so it cannot tell the difference.
        /// <para>
        /// The removal that folds the rows back builds the folded rows, with the key taken out
        /// of them, before it changes anything. If it cannot get the memory it throws the
        /// <see cref="OutOfMemoryException"/> with the sketch as it was before the removal:
        /// the key is not taken out, so removing it again removes it once.
        /// </para>
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
            var after = this.count > 0 ? this.count - 1 : 0;

            // The removal that takes the count below the folding point folds the rows, and
            // takes the key out of the folded rows as it builds them, which is the part that
            // can run out of memory.
            if (after < this.contractionLimit && this.records.Count > 0)
            {
                Contract(kernel, after);
                return this;
            }

            for (var i = 0; i < this.rows.Length; i++)
            {
                this.rows[i].Decrement(ColumnOf(kernel, i));
            }

            this.count = after;
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
        /// <remarks>
        /// It builds the rows it starts again with before it changes anything, so a reset that
        /// cannot get the memory for them throws the <see cref="OutOfMemoryException"/> with
        /// the sketch as it was. It used to set the tuning and the starting width first and
        /// then replace the rows one at a time, so a failure left rows of the old width under
        /// the new one, and a sketch that could not be written.
        /// </remarks>
        public SublimeCountMinSketch Reset()
        {
            var starting = StartingWidth(ValeCounterArray.DefaultCountersPerChunk, this.sizeFactor);
            var fresh = new ValeCounterArray[this.rows.Length];
            for (var i = 0; i < fresh.Length; i++)
            {
                fresh[i] = NewRow(
                    starting, ValeCounterArray.DefaultCountersPerChunk, ValeCounterArray.DefaultStubBits);
            }

            // Built; from here the sketch is changed, and nothing allocates.
            Install(new Layout(
                fresh, starting, ValeCounterArray.DefaultCountersPerChunk, ValeCounterArray.DefaultStubBits));
            this.records.Clear();
            this.count = 0;
            this.expansionLimit = LimitFor(starting);
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
        /// <para>
        /// Since version 10 that tuning is written before each state's counts: the one the
        /// counts call for, which is not always the one the sketch holds them at, since it
        /// retunes only when its rows crowd. So a read can be priced from it without the
        /// counts, and the payload stays a function of the counts alone. The reader packs at
        /// it and refuses a payload whose counts call for another (#280).
        /// </para>
        /// </remarks>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream) => PersistenceFormat.Write(stream, this);

        EnvelopeHeader IPayloadBody.WritePayload(PayloadWriter payload)
        {
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
                WriteTuning(payload, record);
                WriteCounters(payload, record, record[0].Count);
            }

            WriteTuning(payload, this.rows);
            WriteCounters(payload, this.rows, this.width);

            return new(StructureId.SublimeCountMinSketch, PersistenceFormat.Identify(this.Hash),
                PersistenceFormat.SublimeTuningVersion);
        }

        /// <summary>
        /// Writes the tuning a read packs one state's counts at, a byte each for the counters
        /// to a chunk and the stub width: the tuning those counts call for, worked out as a
        /// read works it out, so that a price can be worked out from it without reading them.
        /// </summary>
        /// <remarks>
        /// A counter counts the two bytes without working the tuning out, which would read
        /// every counter of the state: what the bytes hold does not change how many there
        /// are.
        /// </remarks>
        private static void WriteTuning(PayloadWriter payload, ValeCounterArray[] rows)
        {
            var (chunkCounters, stub) = payload.IsCounting ? (0, 0) : Tune(CounterLengths(rows));
            payload.WriteByte((byte)chunkCounters);
            payload.WriteByte((byte)stub);
        }

        /// <summary>
        /// Writes rows of counts, eight bytes each with no run around them, sized by the
        /// width written for them, so that a counter counts them without unpacking any.
        /// </summary>
        private static void WriteCounters(PayloadWriter payload, ValeCounterArray[] rows, int width)
        {
            if (!payload.BeginBlock((long)rows.Length * width * sizeof(ulong)))
            {
                return;
            }

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
            return PersistenceFormat.Read(
                stream, StructureId.SublimeCountMinSketch, budget, Price, hash, PersistenceFormat.CheckResolvable,
                ReadPayload);
        }

        /// <summary>
        /// Reads the payload of the envelope <c>Read</c> opened, once the envelope has
        /// been checked. See <see cref="PayloadParse{T}"/>.
        /// </summary>
        private static SublimeCountMinSketch ReadPayload(
            ref PayloadReader reader, ushort version, HashId hashId,
            Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            var delta = reader.ReadDouble();
            var growthExponent = reader.ReadDouble();
            var sizeFactor = reader.ReadDouble();
            var depth = reader.ReadUInt32();
            var width = reader.ReadUInt32();
            CheckShape(ref reader, delta, growthExponent, sizeFactor, depth, width);

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
            CheckTotals(sketch.count, sketch.expansionLimit, width, sizeFactor, growthExponent);

            var recordCount = reader.ReadUInt32();
            var expected = FirstRecordWidth(recordCount, width);

            for (var i = 0; i < recordCount; i++)
            {
                var recordWidth = reader.ReadUInt32();
                CheckRecord(i, recordWidth, expected);

                sketch.width = (int)recordWidth;
                sketch.records.Add(ReadCounters(ref reader, version, (int)depth, (int)recordWidth, i));
                expected *= 2;
            }

            sketch.width = (int)width;
            sketch.rows = ReadCounters(ref reader, version, (int)depth, (int)width, -1);
            reader.ExpectEnd();

            // The constructor left the defaults, and the rows were built at the tuning chosen
            // from the counts, which since version 10 the payload also stores. The properties
            // describe the rows, not the records of earlier states, which were tuned separately
            // at their own widths. A sketch has at least one row, and every row is built at the
            // same tuning.
            sketch.countersPerChunk = sketch.rows[0].CountersPerChunk;
            sketch.stubBits = sketch.rows[0].StubBits;

            return sketch;
        }

        /// <summary>
        /// Prices what <see cref="Read"/> allocates beyond the envelope: for the rows and
        /// for each record of an earlier state, the plain counts read and the packed
        /// counters they are tuned for and moved into. See <see cref="PayloadPrice"/>.
        /// </summary>
        /// <remarks>
        /// What the packed counters are charged depends on the counters to a chunk and on
        /// nothing else the counts hold, since each chunk is charged a tails array whether
        /// it spills or not. From version 10 the payload stores the tuning before each
        /// state's counts, and the price reads it and seeks past them, so a price reads the
        /// fields of a payload and not its bulk; the reader refuses a stored tuning the counts
        /// do not call for, so the price and the charges are the same sums. Before version 10
        /// the counts are the only record of their tuning, and are read for it.
        /// </remarks>
        internal static void Price(ref PayloadReader reader, ushort version)
        {
            var delta = reader.ReadDouble();
            var growthExponent = reader.ReadDouble();
            var sizeFactor = reader.ReadDouble();
            var depth = reader.ReadUInt32();
            var width = reader.ReadUInt32();
            CheckShape(ref reader, delta, growthExponent, sizeFactor, depth, width);

            var count = reader.ReadUInt64();
            var expansionLimit = reader.ReadUInt64();
            reader.ReadUInt64();
            CheckTotals(count, expansionLimit, width, sizeFactor, growthExponent);

            var recordCount = reader.ReadUInt32();
            var expected = FirstRecordWidth(recordCount, width);

            for (var i = 0; i < recordCount; i++)
            {
                var recordWidth = reader.ReadUInt32();
                CheckRecord(i, recordWidth, expected);
                PriceCounters(ref reader, version, (int)depth, (int)recordWidth);
                expected *= 2;
            }

            PriceCounters(ref reader, version, (int)depth, (int)width);
        }

        /// <summary>
        /// Prices what <see cref="ReadCounters"/> allocates for one state, and moves past
        /// its counts: from the tuning the payload stores, seeking past the counts, or before
        /// version 10, reading the counts for what tuning them chooses.
        /// </summary>
        private static void PriceCounters(ref PayloadReader reader, ushort version, int depth, int forWidth)
        {
            var counts = (long)depth * forWidth;
            if (StoresTuning(version))
            {
                var (chunkCounters, _) = ReadTuning(ref reader);
                reader.Budget.Price(CountsCost(depth, forWidth));
                reader.Budget.Price(PackedCost(depth, forWidth, chunkCounters));
                reader.Skip(counts * sizeof(ulong));
                return;
            }

            reader.Budget.Price(CountsCost(depth, forWidth));

            Span<long> lengths = stackalloc long[CounterLengthCount];
            for (var i = 0L; i < counts; i++)
            {
                Tally(lengths, reader.ReadUInt64());
            }

            reader.Budget.Price(PackedCost(depth, forWidth, Tune(lengths).CountersPerChunk));
        }

        /// <summary>Whether a payload of this version stores each state's tuning before its counts.</summary>
        private static bool StoresTuning(ushort version) => version >= PersistenceFormat.SublimeTuningVersion;

        /// <summary>
        /// Reads the tuning a payload stores before a state's counts, and checks it is one a
        /// chunk holds, as both the read and its price do before they use it: the price works
        /// the packed rows' cost out from it, and a chunk of no counters divides by nought.
        /// Whether it is the tuning the counts call for only the counts can say, and the read
        /// asks them.
        /// </summary>
        private static (int CountersPerChunk, int StubBits) ReadTuning(ref PayloadReader reader)
        {
            int chunkCounters = reader.ReadByte();
            int stub = reader.ReadByte();
            if (chunkCounters < ValeCounterArray.MinCountersPerChunk
                || chunkCounters > ValeCounterArray.MaxCountersPerChunk
                || stub < ValeCounterArray.MinStubBits
                || stub > ValeCounterArray.MaxStubBits)
            {
                throw new InvalidDataException(
                    $"Sketch stores a tuning of {chunkCounters} counters to a chunk with {stub}-bit " +
                    $"stubs. A chunk holds between {ValeCounterArray.MinCountersPerChunk} and " +
                    $"{ValeCounterArray.MaxCountersPerChunk} counters, with stubs of " +
                    $"{ValeCounterArray.MinStubBits} to {ValeCounterArray.MaxStubBits} bits.");
            }

            return (chunkCounters, stub);
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
        /// any counters are priced or allocated.
        /// </summary>
        private static void CheckShape(
            ref PayloadReader reader,
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

            // Every counter of the current rows is written, as a plain u64, so they are
            // checked against the bytes behind them before any row is priced or allocated:
            // a few bytes claiming millions of counters had the reader allocate a row of
            // them before it found them missing. The rows are the last thing in the payload,
            // after the totals and any records of earlier states, so the bytes left here
            // hold at least their counters. Each record is half the width of the state after
            // it, so the records together hold fewer counters than the rows, and what is
            // allocated for them is bounded by the same bytes. The product of two u32 cannot
            // wrap a ulong, and the check divides. The read makes it as well as the price,
            // since a price that throws does not stop the read.
            reader.RequireHolds((ulong)depth * width, sizeof(ulong), "counters");
        }

        /// <summary>
        /// Checks that the count of keys and the count at which the rows next double fit
        /// the shape <see cref="CheckShape"/> passed and each other, as both the read and
        /// its price do once they have read them.
        /// </summary>
        /// <remarks>
        /// Every sketch has its rows due to double at <see cref="LimitFor(int)"/> of its
        /// width, and holds fewer keys than that: the add that reaches it doubles the rows
        /// and sets the next doubling past it. A payload breaking either was not written by
        /// one. Taken anyway, a count at the largest <c>ulong</c> wrapped to nought on the
        /// next add, and a count past the rows' doubling doubled them on every add until
        /// their own doubling passed it, from a payload of a few kilobytes as far as rows
        /// of 2^30 counters.
        /// <para>
        /// The doubling is not held to exactly the count the width gives, because that is
        /// worked out with <see cref="Math.Pow"/>, which is not the same in its last bit
        /// on every platform, and a sketch written on one is read on another. The rows
        /// twice as wide double at about twice the count or more, so a payload is
        /// refused only past that, which no difference in the last bit reaches. A
        /// doubling held at the largest <c>ulong</c> is one no count reaches, so any count
        /// fits beneath it, the largest included.
        /// </para>
        /// </remarks>
        private static void CheckTotals(
            ulong count, ulong expansionLimit, uint width, double sizeFactor, double growthExponent)
        {
            var twice = LimitFor(2.0 * width, sizeFactor, growthExponent);
            if (expansionLimit > twice)
            {
                throw new InvalidDataException(
                    $"Sketch claims its rows next double at {expansionLimit:N0} keys. Rows " +
                    $"{width:N0} counters wide, at its size factor and growth exponent, double " +
                    $"at {LimitFor(width, sizeFactor, growthExponent):N0}, and this is past " +
                    $"even {twice:N0}, where rows twice as wide would.");
            }
            if (count >= expansionLimit && expansionLimit != ulong.MaxValue)
            {
                throw new InvalidDataException(
                    $"Sketch claims {count:N0} keys, with its rows due to double at " +
                    $"{expansionLimit:N0}. The add that reaches that count doubles them and " +
                    "sets the next doubling past it, so a sketch holds fewer keys than the " +
                    "count at which it next doubles.");
            }
        }

        /// <summary>
        /// Reads one state's counts and packs them: at the tuning the payload stores for
        /// them, which has to be the one they call for, or before version 10 at that one.
        /// </summary>
        /// <param name="reader">The payload, at the state's tuning or, before version 10, its counts.</param>
        /// <param name="version">The payload's version.</param>
        /// <param name="depth">The rows in the state.</param>
        /// <param name="forWidth">The counters in each of its rows.</param>
        /// <param name="record">Which record of an earlier state this is, or -1 for the rows.</param>
        /// <remarks>
        /// The reader is passed by reference because it is a ref struct holding its own
        /// position. Taken by value it would read the right bytes and leave the caller
        /// where it started, which looks like a payload with everything left over.
        /// <para>
        /// A stored tuning other than the one the counts call for is refused rather than
        /// packed at, though the counts would pack at it: the price came from it, and the
        /// sketch read would write another payload than the one it was read from, since a
        /// payload stores the tuning its counts call for. The tuning is worked out with plain
        /// arithmetic, which unlike <see cref="Math.Pow"/> rounds alike on every platform, so
        /// a payload written on one reads on another.
        /// </para>
        /// </remarks>
        private static ValeCounterArray[] ReadCounters(
            ref PayloadReader reader, ushort version, int depth, int forWidth, int record)
        {
            (int CountersPerChunk, int StubBits)? stored =
                StoresTuning(version) ? ReadTuning(ref reader) : null;

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

            var calledFor = Tune(values);
            if (stored is { } tuning && tuning != calledFor)
            {
                throw new InvalidDataException(
                    $"Sketch stores {(record < 0 ? "its rows" : $"record {record}")} at a tuning of " +
                    $"{tuning.CountersPerChunk} counters to a chunk with {tuning.StubBits}-bit stubs, " +
                    $"and its counters call for {calledFor.CountersPerChunk} with " +
                    $"{calledFor.StubBits}-bit stubs. A payload stores the tuning its counters call " +
                    "for, so this one was not written as it stands.");
            }
            var (chunkCounters, stub) = stored ?? calledFor;

            // And then packed, at the tuning stored, or just chosen.
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
        private ulong LimitFor(int forWidth) =>
            LimitFor(forWidth, this.sizeFactor, this.growthExponent);

        /// <inheritdoc cref="LimitFor(int)"/>
        /// <remarks>
        /// A number of keys of 2^64 or more is held as <c>ulong.MaxValue</c>, which stands
        /// for a doubling no count reaches, and <see cref="Add(ReadOnlySpan{byte})"/> never
        /// doubles at it. The width is a double so that the reader can ask about rows twice
        /// the widest; at any width a row has, it is the same arithmetic. Internal so that
        /// the count at which a sketch stops, this at the widest row, is asked of the rule
        /// itself by the tests of what the documentation says about it.
        /// </remarks>
        internal static ulong LimitFor(double forWidth, double sizeFactor, double growthExponent)
        {
            var keys = Math.Pow(forWidth * sizeFactor, 1 / growthExponent);
            return keys >= ulong.MaxValue ? ulong.MaxValue : (ulong)Math.Ceiling(keys);
        }

        /// <summary>
        /// Takes the add of the key that calls for the doubling, and doubles every row,
        /// copying each counter into the new half.
        /// </summary>
        /// <remarks>
        /// A key whose hash chose counter j now chooses j or j + w, and both hold what
        /// j held. Its count is therefore preserved, at the cost of being counted twice
        /// over the sketch as a whole -- which is the error the paper accepts, and
        /// which shrinks in importance as the keys inserted after the expansion come to
        /// outnumber those inserted before it.
        /// <para>
        /// The key is counted before the rows double, as it always was: in the rows that
        /// become the record, and so in both copies of each of its counters. But everything
        /// the doubling allocates is built first, and nothing in the sketch is changed until
        /// it is, so a doubling that runs out of memory leaves the sketch as it was. It used to
        /// count the key, append the record and then allocate, and a failure left the key
        /// counted though the add threw, a record of a doubling that had not happened, and the
        /// next doubling where it was, so that every add after it tried again and appended
        /// another; the payload such a sketch wrote, its own reader refused.
        /// </para>
        /// <para>
        /// The record is a copy of the rows with the key counted in it, built with the doubled
        /// rows, and what is left once both are built only puts them in place. The record used
        /// to be the old rows themselves, with the key's increments taken in them after the
        /// doubled rows were built. An increment can move a chunk to a tails array, which
        /// allocates, and one that ran out of memory there left the rows before it counted
        /// and, in its own row, a counter whose stub had been written and whose high bits had
        /// not.
        /// </para>
        /// </remarks>
        private void Expand(in HashKernelReturnValue kernel)
        {
            var doubled = this.width * 2;

            var laidOut = LayOutDoubled(kernel, doubled);
            var record = CountedCopyOfRows(kernel);
            this.records.EnsureCapacity(this.records.Count + 1);

            // Everything the doubling needs has been built, and only from here is the sketch
            // changed, which allocates nothing. The count is below the one the rows double at,
            // which is below the largest a ulong holds, so it has room.
            this.count++;
            this.records.Add(record);
            Install(laidOut);

            this.expansionLimit = LimitFor(doubled);
            this.contractionLimit = ContractionLimitFor(doubled);
        }

        /// <summary>
        /// The rows doubled, with each counter copied into the new half and the key counted
        /// in both copies of each of its counters.
        /// </summary>
        /// <remarks>
        /// The plain counts the rows are packed from, eight bytes a counter and the largest
        /// thing a doubling allocates, are held only here, so that nothing holds them once
        /// the rows are packed and the record is copied after.
        /// </remarks>
        private Layout LayOutDoubled(in HashKernelReturnValue kernel, int doubled)
        {
            var values = CountsAt(doubled);
            for (var i = 0; i < this.rows.Length; i++)
            {
                // Both copies of the key's counter, stopping at the largest count as an
                // increment does.
                var column = ColumnOf(kernel, i);
                var counted = OneMore(values[i][column]);
                values[i][column] = counted;
                values[i][column + this.width] = counted;
            }

            return LayOut(values, doubled);
        }

        /// <summary>
        /// A copy of the rows with the key counted in them, for the record a doubling keeps of
        /// the rows it doubles.
        /// </summary>
        private ValeCounterArray[] CountedCopyOfRows(in HashKernelReturnValue kernel)
        {
            var copies = new ValeCounterArray[this.rows.Length];
            for (var i = 0; i < this.rows.Length; i++)
            {
                copies[i] = CopyOfRow(this.rows[i]);
                copies[i].Increment(ColumnOf(kernel, i));
            }

            return copies;
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
        /// Takes the removal of the key that calls for the folding, and folds every row back
        /// in half, keeping the updates made since the expansion that doubled it.
        /// </summary>
        /// <param name="kernel">The hash of the key being removed.</param>
        /// <param name="after">The count once the key is removed.</param>
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
        /// <para>
        /// The folded rows are built before anything is changed, the key taken out of them as
        /// they are, so a removal whose folding runs out of memory leaves the sketch as it
        /// was, and the removal tried again removes the key once. It used to take the record
        /// off first and set the width before packing the rows, so a failure lost the record,
        /// and with it the folding, or left the width halved over rows that were not. And
        /// until #242 the removal was taken in the rows before they were folded, so a folding
        /// that failed left the key removed though the removal threw.
        /// </para>
        /// </remarks>
        private void Contract(in HashKernelReturnValue kernel, ulong after)
        {
            var record = this.records[^1];

            var halved = this.width / 2;
            var folded = new ulong[this.rows.Length][];
            for (var i = 0; i < this.rows.Length; i++)
            {
                var column = ColumnOf(kernel, i);
                folded[i] = NewCounts(halved);
                for (var j = 0; j < halved; j++)
                {
                    var gathered = (UInt128)Removed(i, j, column) + Removed(i, j + halved, column);
                    var started = (UInt128)record[i].Get(j);
                    var kept = gathered > started ? gathered - started : UInt128.Zero;
                    folded[i][j] = kept > ulong.MaxValue ? ulong.MaxValue : (ulong)kept;
                }
            }

            var laidOut = LayOut(folded, halved);

            // Built; from here the sketch is changed, and nothing allocates.
            this.count = after;
            this.records.RemoveAt(this.records.Count - 1);
            Install(laidOut);

            this.expansionLimit = LimitFor(halved);
            this.contractionLimit = this.records.Count == 0
                ? 0
                : ContractionLimitFor(halved);
        }

        /// <summary>
        /// Counter <paramref name="j"/> of a row as removing a key leaves it: one less at the
        /// key's own counter, <paramref name="column"/>, stopping at nought as a decrement
        /// does, and as it is everywhere else.
        /// </summary>
        private ulong Removed(int row, int j, int column)
        {
            var value = this.rows[row].Get(j);
            return j == column && value != 0 ? value - 1 : value;
        }

        /// <summary>
        /// Takes the add of a key that crowds the rows, and lays the counters out again at
        /// their width, the key counted, at a tuning suited to what they then hold.
        /// </summary>
        /// <remarks>
        /// The rows are built, the key's counters included, before anything is changed, so an
        /// add whose repacking runs out of memory leaves the sketch as it was, and the add tried
        /// again counts the key once. Until #242 the add took its increments in the rows first,
        /// moving the crowded chunks to tails arrays as it went, and counted the key, and only
        /// then laid the rows out, so a failure left the key counted though the add threw.
        /// Before #213 this doubled the rows as well, and set the width before it built them,
        /// so a doubling that failed building them left the width doubled over rows that were
        /// not.
        /// </remarks>
        private void Repack(in HashKernelReturnValue kernel)
        {
            var values = CountsAt(this.width);
            for (var i = 0; i < this.rows.Length; i++)
            {
                var column = ColumnOf(kernel, i);
                values[i][column] = OneMore(values[i][column]);
            }

            var laidOut = LayOut(values, this.width);

            // Built; from here the sketch is changed, and nothing allocates.
            this.count = OneMore(this.count);
            Install(laidOut);
        }

        /// <summary>
        /// The rows' counts as plain counts, at the given width: each counter copied into
        /// every place in the wider rows that a key choosing it may now choose.
        /// </summary>
        private ulong[][] CountsAt(int forWidth)
        {
            var values = new ulong[this.rows.Length][];
            for (var i = 0; i < this.rows.Length; i++)
            {
                values[i] = NewCounts(forWidth);
                for (var j = 0; j < forWidth; j++)
                {
                    values[i][j] = this.rows[i].Get(j & (this.width - 1));
                }
            }

            return values;
        }

        /// <summary>
        /// Rows that <see cref="LayOut"/> has built, with the width and the tuning they were
        /// built at, for <see cref="Install"/> to put in place.
        /// </summary>
        private readonly record struct Layout(
            ValeCounterArray[] Rows, int Width, int CountersPerChunk, int StubBits);

        /// <summary>
        /// Packs the given counts into rows of their own, choosing a tuning to suit them.
        /// </summary>
        /// <remarks>
        /// It changes nothing in the sketch. What it builds is put in place by
        /// <see cref="Install"/>, so a resize that runs out of memory here, or building the
        /// counts it packs, leaves the sketch as it was.
        /// </remarks>
        private Layout LayOut(ulong[][] values, int forWidth)
        {
            var (chunkCounters, stub) = Tune(values);

            var rebuilt = new ValeCounterArray[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                var row = NewRow(forWidth, chunkCounters, stub);
                for (var j = 0; j < forWidth; j++)
                {
                    if (values[i][j] != 0)
                    {
                        row.Set(j, values[i][j]);
                    }
                }
                rebuilt[i] = row;
            }

            return new Layout(rebuilt, forWidth, chunkCounters, stub);
        }

        /// <summary>
        /// Puts rows that <see cref="LayOut"/> built in place. It only assigns, so it cannot
        /// fail partway.
        /// </summary>
        private void Install(in Layout layout)
        {
            this.rows = layout.Rows;
            this.width = layout.Width;
            this.countersPerChunk = layout.CountersPerChunk;
            this.stubBits = layout.StubBits;
        }

        /// <summary>
        /// A row of plain counts for a resize to fill, allocated past
        /// <see cref="BeforeAllocating"/>.
        /// </summary>
        private ulong[] NewCounts(int forWidth)
        {
            this.BeforeAllocating?.Invoke();
            return new ulong[forWidth];
        }

        /// <summary>
        /// A row of packed counters for a resize or a reset to fill, allocated past
        /// <see cref="BeforeAllocating"/>, which it is handed in turn.
        /// </summary>
        private ValeCounterArray NewRow(int forWidth, int chunkCounters, int stub)
        {
            this.BeforeAllocating?.Invoke();
            return new ValeCounterArray(forWidth, chunkCounters, stub)
            {
                BeforeAllocatingTails = this.BeforeAllocating,
            };
        }

        /// <summary>
        /// A copy of a row, allocated past <see cref="BeforeAllocating"/>, which it is handed
        /// in turn.
        /// </summary>
        private ValeCounterArray CopyOfRow(ValeCounterArray row)
        {
            this.BeforeAllocating?.Invoke();
            var copy = row.Copy();
            copy.BeforeAllocatingTails = this.BeforeAllocating;
            return copy;
        }

        /// <summary>
        /// Whether this many chunks on tails arrays, across every row, is too many: an add
        /// that puts a chunk on tails and leaves this many lays the counters out again.
        /// </summary>
        /// <remarks>
        /// Only an add that puts a chunk on tails can call for this. Between layouts
        /// nothing else puts one there, so a sketch already past the fraction before an
        /// add was left past it by its last layout -- which is what the fallback tuning
        /// does, when no tuning meets the bound <see cref="Tune(ReadOnlySpan{long})"/> sets
        /// and the tails arrays carry what the pools cannot. Laying the same counts out
        /// again picks the same tuning and puts the same chunks back on tails, so asking
        /// after every add laid out the whole sketch on every add, for nothing. Where every
        /// layout leaves no more than the fraction on tails, as an ordinary sketch's does,
        /// an add can take the sketch past it only by putting a chunk on tails, so the
        /// counters are laid out again at exactly the adds they always were.
        /// <para>
        /// The add asks each row whose stub is full whether its increment would put a chunk on
        /// tails, and asks this of the count that would leave, before it takes any increment
        /// it could not take back. It used to take the increments, count again and then ask,
        /// and the answer is the same: each row's increment moves at most its own chunk, an
        /// increment that stops at a stub moves none, and the others move it exactly when the
        /// row says so.
        /// </para>
        /// </remarks>
        private bool Crowded(int onTails)
        {
            var chunks = 0;
            foreach (var row in this.rows)
            {
                chunks += row.ChunkCount;
            }

            return onTails > chunks * TailsFractionBeforeRetuning;
        }

        /// <summary>
        /// How many chunks, across every row, have fallen back to tails arrays.
        /// </summary>
        private int ChunksOnTails()
        {
            var onTails = 0;
            foreach (var row in this.rows)
            {
                onTails += row.ChunksWithTails;
            }

            return onTails;
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
        /// <para>
        /// Internal so that a test writing counts into a payload by hand can store the
        /// tuning they call for, as it repairs the checksum, and have the reader read them.
        /// </para>
        /// </remarks>
        internal static (int CountersPerChunk, int StubBits) Tune(ulong[][] values) =>
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
        /// <see cref="CounterLengths(ulong[][])"/> for counters already packed, as the writer
        /// has them, so that it tunes the counts it writes as a read of them will.
        /// </summary>
        private static long[] CounterLengths(ValeCounterArray[] rows)
        {
            var lengths = new long[CounterLengthCount];

            foreach (var row in rows)
            {
                for (var j = 0; j < row.Count; j++)
                {
                    Tally(lengths, row.Get(j));
                }
            }

            return lengths;
        }

        /// <summary>
        /// How many lengths <see cref="CounterLengths(ulong[][])"/> tallies counters by: every one
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
