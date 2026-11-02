using System;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// A sketch of a stream of numbers that answers what its distribution looks like:
    /// the median, the 99th percentile, the shape of the tail.
    /// </summary>
    /// <remarks>
    /// Masson, Rim and Lee, "DDSketch: A Fast and Fully-Mergeable Quantile Sketch with
    /// Relative-Error Guarantees" (2019).
    /// <para>
    /// Its guarantee is on the <b>value</b> rather than the rank: the answer to a
    /// quantile query is within a fixed relative accuracy of the true value at that
    /// quantile. That is the guarantee latency measurement actually wants -- "the p99 is
    /// within 1% of the truth" rather than "within 1% of the right rank", which says
    /// nothing about how far off the number is when the tail is steep.
    /// </para>
    /// <para>
    /// It works by bucketing values logarithmically. Bucket i holds the values in
    /// (gamma^(i-1), gamma^i] for gamma = (1+a)/(1-a), and reports their midpoint, which
    /// is within a of every value the bucket can hold. Nothing about that is
    /// probabilistic: the counts are exact, so the only error is the bucket's width.
    /// </para>
    /// <para>
    /// This is the first structure here that takes numbers rather than bytes, and so the
    /// first that never hashes anything. It has no <c>SetHash</c>, and its payload
    /// records that it uses no hash rather than naming one.
    /// </para>
    /// </remarks>
    public class DDSketch : IBinaryPersistable<DDSketch>
    {
        private double relativeAccuracy;
        private double gamma;
        private double logGamma;

        private ulong count;
        private ulong zeroCount;
        private double min = double.PositiveInfinity;
        private double max = double.NegativeInfinity;

        private Store positive = new Store();
        private Store negative = new Store();

        /// <summary>
        /// Creates a sketch whose answers are within the given relative accuracy of the
        /// true values.
        /// </summary>
        /// <param name="relativeAccuracy">
        /// The fraction by which an answer may differ from the truth, strictly between
        /// zero and one. 0.01 means every quantile comes back within 1% of its real
        /// value.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The accuracy is not strictly between zero and one.
        /// </exception>
        public DDSketch(double relativeAccuracy)
        {
            Guard.ValidRelativeAccuracy(relativeAccuracy, nameof(relativeAccuracy));

            this.relativeAccuracy = relativeAccuracy;
            this.gamma = (1 + relativeAccuracy) / (1 - relativeAccuracy);
            this.logGamma = Math.Log(this.gamma);
        }

        /// <summary>
        /// Adds a value to the sketch.
        /// </summary>
        /// <param name="value">The value to add. Negative values and zero are allowed.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The value is NaN or infinite, neither of which has a place in a distribution.
        /// </exception>
        public void Add(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value,
                    "A sketch holds numbers. Accepting this one would put every later " +
                    "answer beyond recovery without anything saying which value did it.");
            }

            if (value > 0)
            {
                this.positive.Add(this.IndexOf(value), 1);
            }
            else if (value < 0)
            {
                this.negative.Add(this.IndexOf(-value), 1);
            }
            else
            {
                this.zeroCount++;
            }

            this.count++;

            if (value < this.min)
            {
                this.min = value;
            }

            if (value > this.max)
            {
                this.max = value;
            }
        }

        /// <summary>
        /// The value at the given quantile, within the sketch's relative accuracy.
        /// </summary>
        /// <param name="q">The quantile, from 0 for the smallest to 1 for the largest.</param>
        /// <returns>The value at that quantile.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The quantile is outside 0 to 1.</exception>
        /// <exception cref="InvalidOperationException">The sketch holds nothing.</exception>
        public double Quantile(double q)
        {
            if (double.IsNaN(q) || q < 0 || q > 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(q), q, "A quantile runs from 0 to 1.");
            }

            if (this.count == 0)
            {
                throw new InvalidOperationException(
                    "The sketch holds nothing, so it has no quantiles. Returning zero " +
                    "or NaN would be a number a caller could plot without noticing it " +
                    "means there was no data.");
            }

            var rank = q * (this.count - 1);

            // Negative values, largest magnitude first: those are the smallest values in
            // the distribution, so they come first when walking it in order.
            if (rank < this.negative.Total)
            {
                double seen = 0;
                for (var i = this.negative.MaxIndex; i >= this.negative.MinIndex; i--)
                {
                    seen += this.negative.CountAt(i);
                    if (seen > rank)
                    {
                        return -this.ValueAt(i);
                    }
                }
            }

            rank -= this.negative.Total;

            if (rank < this.zeroCount)
            {
                return 0;
            }

            rank -= this.zeroCount;

            {
                double seen = 0;
                for (var i = this.positive.MinIndex; i <= this.positive.MaxIndex; i++)
                {
                    seen += this.positive.CountAt(i);
                    if (seen > rank)
                    {
                        return this.ValueAt(i);
                    }
                }
            }

            // Only reachable if the accumulated rank fell fractionally short of the
            // total, which the largest value answers correctly anyway.
            return this.max;
        }

        /// <summary>
        /// Combines another sketch into this one, so that it answers for both streams.
        /// </summary>
        /// <param name="other">The sketch to merge in, which is left unchanged.</param>
        /// <returns>This sketch, to allow chaining.</returns>
        /// <remarks>
        /// Merging is exact. Two sketches of the same accuracy bucket identically, so
        /// combining them is adding counts bucket for bucket -- the merged sketch is the
        /// one that would have been built by feeding it both streams, not an
        /// approximation of it.
        /// </remarks>
        /// <exception cref="ArgumentNullException">The other sketch is null.</exception>
        /// <exception cref="ArgumentException">
        /// The sketches have different relative accuracies, so their buckets do not
        /// describe the same ranges and their counts cannot be added.
        /// </exception>
        public DDSketch Merge(DDSketch other)
        {
            ArgumentNullException.ThrowIfNull(other);

            if (this.relativeAccuracy != other.relativeAccuracy)
            {
                throw new ArgumentException(
                    "Sketches must have been built with the same relative accuracy to " +
                    $"be merged: this one has {this.relativeAccuracy} and the other " +
                    $"{other.relativeAccuracy}. Their buckets cover different ranges, " +
                    "so adding the counts would combine values that are not comparable. " +
                    "The relative accuracy must match.",
                    nameof(other));
            }

            if (other.count == 0)
            {
                return this;
            }

            this.positive.Merge(other.positive);
            this.negative.Merge(other.negative);
            this.zeroCount += other.zeroCount;
            this.count += other.count;

            if (other.min < this.min)
            {
                this.min = other.min;
            }

            if (other.max > this.max)
            {
                this.max = other.max;
            }

            return this;
        }

        /// <summary>
        /// How many bucket slots are currently allocated across both stores, which is
        /// what the sketch costs in memory beyond a handful of fields.
        /// </summary>
        internal int BucketsAllocated()
        {
            return this.positive.Capacity + this.negative.Capacity;
        }

        /// <summary>
        /// The number of values added.
        /// </summary>
        public ulong Count()
        {
            return this.count;
        }

        /// <summary>
        /// The relative accuracy the sketch was built with.
        /// </summary>
        public double RelativeAccuracy()
        {
            return this.relativeAccuracy;
        }

        /// <summary>
        /// The smallest value added, exactly.
        /// </summary>
        /// <remarks>
        /// Kept rather than bucketed. It costs one double, it is asked for constantly,
        /// and an approximate answer to "what was the smallest" is worse than the exact
        /// one already in hand.
        /// </remarks>
        /// <exception cref="InvalidOperationException">The sketch holds nothing.</exception>
        public double Min()
        {
            return this.count == 0
                ? throw new InvalidOperationException("The sketch holds nothing.")
                : this.min;
        }

        /// <summary>
        /// The largest value added, exactly.
        /// </summary>
        /// <exception cref="InvalidOperationException">The sketch holds nothing.</exception>
        public double Max()
        {
            return this.count == 0
                ? throw new InvalidOperationException("The sketch holds nothing.")
                : this.max;
        }

        /// <summary>
        /// Writes the sketch to a stream.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = new PayloadWriter();
            payload.WriteDouble(this.relativeAccuracy);
            payload.WriteUInt64(this.count);
            payload.WriteUInt64(this.zeroCount);
            payload.WriteDouble(this.min);
            payload.WriteDouble(this.max);
            this.positive.WriteTo(payload);
            this.negative.WriteTo(payload);

            // No hash to name. Gamma and the log of it follow from the accuracy, and the
            // store bounds follow from the counts, so none of them are stored.
            PersistenceFormat.Write(
                stream, StructureId.DDSketch, HashId.None, payload.WrittenSpan);
        }

        /// <summary>
        /// Reads a sketch written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The sketch that was written.</returns>
        public static DDSketch ReadFrom(Stream stream)
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
        public static DDSketch ReadFrom(Stream stream, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, null, options);
        }

        /// <summary>
        /// Reads a sketch written by <see cref="WriteTo"/>. The sketch does not hash,
        /// so supplying a hash function is refused rather than ignored.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">Not used, and refused if supplied.</param>
        /// <returns>The sketch that was written.</returns>
        public static DDSketch ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
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
        public static DDSketch ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, hash, options);
        }

        private static DDSketch Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.DDSketch);
            var payload = PersistenceFormat.Read(stream, StructureId.DDSketch, out var hashId, budget, Price);

            if (hash is not null)
            {
                throw new InvalidDataException(
                    "A DDSketch does not hash anything -- it holds numbers rather than " +
                    "bytes -- so it cannot be read with a supplied hash function. Read " +
                    "it with the overload that takes none.");
            }

            if (hashId != HashId.None)
            {
                throw new InvalidDataException(
                    $"Payload names hash function {(ushort)hashId}, and a DDSketch does " +
                    "not hash anything. It was not written by this structure.");
            }

            var reader = new PayloadReader(payload, budget);

            var accuracy = reader.ReadDouble();
            var count = reader.ReadUInt64();
            var zeroCount = reader.ReadUInt64();
            var min = reader.ReadDouble();
            var max = reader.ReadDouble();
            var positive = Store.ReadFrom(ref reader, "positive");
            var negative = Store.ReadFrom(ref reader, "negative");
            reader.ExpectEnd();

            if (!(accuracy > 0 && accuracy < 1))
            {
                throw new InvalidDataException(
                    $"Sketch has a relative accuracy of {accuracy}, which does not " +
                    "describe a sketch: it must be greater than zero and less than one.");
            }

            // The stores and the zero count are the whole of what was added, so a total
            // that disagrees with them would put every quantile at the wrong rank while
            // each individual bucket still looked reasonable.
            var held = positive.Total + negative.Total + zeroCount;
            if (held != count)
            {
                throw new InvalidDataException(
                    $"Sketch claims {count} values and its buckets hold {held}, so they " +
                    "do not add up to the same sketch.");
            }

            if (count > 0 && min > max)
            {
                throw new InvalidDataException(
                    $"Sketch has a smallest value of {min} above its largest of {max}.");
            }

            return new DDSketch(accuracy)
            {
                count = count,
                zeroCount = zeroCount,
                min = min,
                max = max,
                positive = positive,
                negative = negative,
            };
        }

        /// <summary>
        /// Prices what <see cref="Read"/> allocates beyond the envelope: each store's run
        /// and the window it is restored into. See <see cref="PayloadPrice"/>.
        /// </summary>
        internal static void Price(ref PayloadReader reader, ushort version)
        {
            reader.ReadDouble();
            reader.ReadUInt64();
            reader.ReadUInt64();
            reader.ReadDouble();
            reader.ReadDouble();
            Store.Price(ref reader, "positive");
            Store.Price(ref reader, "negative");
        }

        /// <summary>
        /// Which bucket a positive value falls in.
        /// </summary>
        private int IndexOf(double value)
        {
            return (int)Math.Ceiling(Math.Log(value) / this.logGamma);
        }

        /// <summary>
        /// The value a bucket reports: the point within it that is no further than the
        /// relative accuracy from anything the bucket can hold.
        /// </summary>
        private double ValueAt(int index)
        {
            return 2 * Math.Pow(this.gamma, index) / (this.gamma + 1);
        }

        /// <summary>
        /// Counts per bucket index, held in one contiguous array that grows to fit the
        /// range of indices seen.
        /// </summary>
        /// <remarks>
        /// Contiguous rather than a dictionary because the quantile walk visits buckets
        /// in index order, which an array gives for free and a dictionary would have to
        /// sort for on every query.
        /// </remarks>
        private sealed class Store
        {
            private const int InitialCapacity = 64;

            private ulong[] counts = Array.Empty<ulong>();
            private int offset;

            internal int MinIndex { get; private set; } = int.MaxValue;

            internal int MaxIndex { get; private set; } = int.MinValue;

            internal ulong Total { get; private set; }

            internal bool IsEmpty => this.Total == 0;

            internal int Capacity => this.counts.Length;

            /// <summary>
            /// Writes the occupied run of buckets: where it starts, and the counts from
            /// there upward. The bounds and the total follow from that.
            /// </summary>
            internal void WriteTo(PayloadWriter payload)
            {
                if (this.IsEmpty)
                {
                    payload.WriteUInt32(0);
                    payload.WriteBytes(ReadOnlySpan<byte>.Empty);
                    return;
                }

                var span = this.MaxIndex - this.MinIndex + 1;
                var bytes = new byte[span * sizeof(ulong)];

                for (var i = 0; i < span; i++)
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                        bytes.AsSpan(i * sizeof(ulong)),
                        this.counts[this.MinIndex - this.offset + i]);
                }

                // A bucket index is signed, and the low ones are ordinary for values
                // below one.
                payload.WriteUInt32(unchecked((uint)this.MinIndex));
                payload.WriteBytes(bytes);
            }

            internal static Store ReadFrom(ref PayloadReader reader, string which)
            {
                var minIndex = unchecked((int)reader.ReadUInt32());

                // Length-prefixed, so a payload cannot claim more buckets than it
                // carries: the read fails before anything is allocated for them.
                var bytes = reader.ReadBytes();

                var span = SpanOf(minIndex, bytes.Length, which);
                var store = new Store();

                if (span == 0)
                {
                    return store;
                }

                // One array for the whole run, sized from it. Left to Add, the window
                // would be reallocated every time the next bucket passed its edge,
                // re-centred with half the room each time, which comes to many times the
                // run's size in allocations for a dense one.
                reader.Budget.Charge(ReadBudget.ArrayOf((ulong)span, sizeof(ulong)));
                store.Reserve(minIndex, span);

                for (var i = 0; i < span; i++)
                {
                    var amount = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                        bytes.AsSpan(i * sizeof(ulong)));

                    if (amount != 0)
                    {
                        store.Add(minIndex + i, amount);
                    }
                }

                return store;
            }

            /// <summary>
            /// Prices what <see cref="ReadFrom"/> allocates: the copy of the run, and the
            /// window it is restored into.
            /// </summary>
            internal static void Price(ref PayloadReader reader, string which)
            {
                var minIndex = unchecked((int)reader.ReadUInt32());
                var span = SpanOf(minIndex, reader.SkipBytes(), which);
                if (span > 0)
                {
                    reader.Budget.Price(ReadBudget.ArrayOf((ulong)span, sizeof(ulong)));
                }
            }

            /// <summary>
            /// How many buckets a run of <paramref name="length"/> bytes starting at
            /// <paramref name="minIndex"/> holds, after checking it can be read as them.
            /// </summary>
            private static int SpanOf(int minIndex, int length, string which)
            {
                if (length % sizeof(ulong) != 0)
                {
                    throw new InvalidDataException(
                        $"The {which} buckets are {length} bytes, which is not a " +
                        "whole number of counts.");
                }

                var span = length / sizeof(ulong);
                if (span > 0 && (long)minIndex + span - 1 > int.MaxValue)
                {
                    throw new InvalidDataException(
                        $"The {which} buckets start at {minIndex} and run past the " +
                        "largest index a bucket can have.");
                }

                return span;
            }

            internal void Merge(Store other)
            {
                if (other.IsEmpty)
                {
                    return;
                }

                for (var i = other.MinIndex; i <= other.MaxIndex; i++)
                {
                    var amount = other.CountAt(i);
                    if (amount != 0)
                    {
                        this.Add(i, amount);
                    }
                }
            }

            internal ulong CountAt(int index)
            {
                return index < this.MinIndex || index > this.MaxIndex
                    ? 0
                    : this.counts[index - this.offset];
            }

            internal void Add(int index, ulong amount)
            {
                this.Fit(index);
                this.counts[index - this.offset] += amount;
                this.Total += amount;

                if (index < this.MinIndex)
                {
                    this.MinIndex = index;
                }

                if (index > this.MaxIndex)
                {
                    this.MaxIndex = index;
                }
            }

            /// <summary>
            /// Sizes an empty store's window to exactly <paramref name="span"/> buckets from
            /// <paramref name="lowest"/>, for a reader about to add a run that long.
            /// </summary>
            internal void Reserve(int lowest, int span)
            {
                this.counts = new ulong[span];
                this.offset = lowest;
            }

            /// <summary>
            /// Makes room for an index, moving the window if it falls outside.
            /// </summary>
            private void Fit(int index)
            {
                if (this.counts.Length == 0)
                {
                    this.counts = new ulong[InitialCapacity];
                    this.offset = index - (InitialCapacity / 2);
                    return;
                }

                if (index >= this.offset && index < this.offset + this.counts.Length)
                {
                    return;
                }

                var low = Math.Min(index, this.IsEmpty ? index : this.MinIndex);
                var high = Math.Max(index, this.IsEmpty ? index : this.MaxIndex);
                var needed = (long)high - low + 1;

                long capacity = this.counts.Length;
                while (capacity < needed)
                {
                    capacity *= 2;
                }

                var grown = new ulong[capacity];
                var newOffset = low - (int)((capacity - needed) / 2);

                if (!this.IsEmpty)
                {
                    Array.Copy(
                        this.counts,
                        this.MinIndex - this.offset,
                        grown,
                        this.MinIndex - newOffset,
                        this.MaxIndex - this.MinIndex + 1);
                }

                this.counts = grown;
                this.offset = newOffset;
            }
        }
    }
}
