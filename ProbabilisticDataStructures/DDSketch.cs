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
            return Read(stream, null);
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
            return Read(stream, hash);
        }

        private static DDSketch Read(Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(stream, StructureId.DDSketch, out var hashId);

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

            var reader = new PayloadReader(payload);

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

                if (bytes.Length % sizeof(ulong) != 0)
                {
                    throw new InvalidDataException(
                        $"The {which} buckets are {bytes.Length} bytes, which is not a " +
                        "whole number of counts.");
                }

                var span = bytes.Length / sizeof(ulong);
                var store = new Store();

                if (span == 0)
                {
                    return store;
                }

                if ((long)minIndex + span - 1 > int.MaxValue)
                {
                    throw new InvalidDataException(
                        $"The {which} buckets start at {minIndex} and run past the " +
                        "largest index a bucket can have.");
                }

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
