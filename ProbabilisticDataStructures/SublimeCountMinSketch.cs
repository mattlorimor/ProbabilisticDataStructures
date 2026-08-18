using System;
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
    public class SublimeCountMinSketch
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

        private readonly double growthExponent;
        private readonly double sizeFactor;
        private readonly double delta;

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

            this.count = 0;
            this.expansionLimit = LimitFor(this.width);
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
            Retune(doubled);
            this.expansionLimit = LimitFor(doubled);
        }

        /// <summary>
        /// Lays the counters out again, at a width and a tuning suited to what they now
        /// hold.
        /// </summary>
        private void Retune(int newWidth)
        {
            var (chunkCounters, stub) = Tune(newWidth);

            var rebuilt = new ValeCounterArray[this.rows.Length];
            for (var i = 0; i < this.rows.Length; i++)
            {
                var row = new ValeCounterArray(newWidth, chunkCounters, stub);
                for (var j = 0; j < newWidth; j++)
                {
                    var value = this.rows[i].Get(j & (this.width - 1));
                    if (value != 0)
                    {
                        row.Set(j, value);
                    }
                }
                rebuilt[i] = row;
            }

            this.rows = rebuilt;
            this.width = newWidth;
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
        private (int CountersPerChunk, int StubBits) Tune(int newWidth)
        {
            var lengths = CounterLengths(newWidth);

            var counters = 0L;
            foreach (var atLength in lengths)
            {
                counters += atLength;
            }

            if (counters == 0)
            {
                return (this.countersPerChunk, this.stubBits);
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
        private long[] CounterLengths(int newWidth)
        {
            var lengths = new long[64];

            foreach (var row in this.rows)
            {
                for (var j = 0; j < newWidth; j++)
                {
                    var value = row.Get(j & (this.width - 1));
                    lengths[64 - BitOperations.LeadingZeroCount(value)]++;
                }
            }

            return lengths;
        }
    }
}
