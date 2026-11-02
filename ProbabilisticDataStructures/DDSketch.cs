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
        /// <remarks>
        /// <b>A bucket is numbered by a 32-bit integer</b>, so an accuracy finer than
        /// about 1.74e-7 cannot bucket every double. Bucket i holds the values up to
        /// gamma^i, and ln(gamma) is about twice the accuracy, so the indices an int
        /// holds run from ln(v) = -2^31 ln(gamma) to +2^31 ln(gamma): at 1e-9 that is
        /// about 0.014 to 73, at 1e-10 about 0.65 to 1.54. <see cref="Add"/> refuses a
        /// value outside the range, where it used to file it in the last bucket there is
        /// and answer with that bucket's value for all of them. The smallest double needs
        /// 744.44 / ln(gamma) indices, which is where the 1.74e-7 comes from.
        /// <para>
        /// The buckets are also one array, from the lowest bucket in use to the highest,
        /// so values spread over more than 2,147,483,591 buckets are refused too, whatever
        /// the accuracy: ln(max / min) over 2 × that × ln(gamma). That is gigabytes of
        /// counts long before it is a limit, and it grows as the logarithm of the spread
        /// of the values rather than with how many were added.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The accuracy is not strictly between zero and one, or is so fine that
        /// (1 + a) / (1 - a) rounds to exactly one and no two values have different
        /// buckets, which is anything below about 5.6e-17.
        /// </exception>
        public DDSketch(double relativeAccuracy)
        {
            Guard.ValidRelativeAccuracy(relativeAccuracy, nameof(relativeAccuracy));

            if (!HasBucketRatio(relativeAccuracy))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(relativeAccuracy), relativeAccuracy,
                    $"A relative accuracy of {relativeAccuracy} is finer than a double can " +
                    "tell one bucket from the next: the ratio between buckets, " +
                    "(1 + a) / (1 - a), rounds to exactly 1, so no value would have a " +
                    "bucket of its own. The finest accuracy a sketch can have is about " +
                    "5.6e-17, and a sketch that fine can only bucket values very near 1.");
            }

            this.relativeAccuracy = relativeAccuracy;
            this.gamma = (1 + relativeAccuracy) / (1 - relativeAccuracy);
            this.logGamma = Math.Log(this.gamma);
        }

        /// <summary>
        /// Whether a double can tell one bucket from the next at this accuracy, which is
        /// whether the ratio between buckets is more than one. The constructor and the
        /// reader both ask, so a payload is refused for the accuracies a sketch is not
        /// built at.
        /// </summary>
        internal static bool HasBucketRatio(double relativeAccuracy)
        {
            return Math.Log((1 + relativeAccuracy) / (1 - relativeAccuracy)) > 0;
        }

        /// <summary>
        /// Whether a run of this many buckets fits in the one array the store holds
        /// them in.
        /// </summary>
        internal static bool SpanFits(long buckets)
        {
            return buckets <= Array.MaxLength;
        }

        /// <summary>
        /// Adds a value to the sketch.
        /// </summary>
        /// <param name="value">The value to add. Negative values and zero are allowed.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The value is NaN or infinite, neither of which has a place in a distribution.
        /// Or its bucket is outside the 32-bit indices, or so far from the buckets already
        /// in use that no array could hold the run between them: see the remarks on the
        /// constructor. The sketch is unchanged.
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
                var index = this.IndexOf(value);
                this.RequireRoom(this.positive, index, value);
                this.positive.Add(index, 1);
            }
            else if (value < 0)
            {
                var index = this.IndexOf(-value);
                this.RequireRoom(this.negative, index, value);
                this.negative.Add(index, 1);
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
                // A long, so that the walk ends at the lowest index an int has instead of
                // wrapping past it.
                double seen = 0;
                for (var i = (long)this.negative.MaxIndex; i >= this.negative.MinIndex; i--)
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
                for (long i = this.positive.MinIndex; i <= this.positive.MaxIndex; i++)
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
        /// describe the same ranges and their counts cannot be added. Or the merged
        /// buckets would span more than one array holds. Either way this sketch is
        /// unchanged.
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

            // Both stores are checked before either is changed, so a merge that cannot
            // be held does not leave one half of it done.
            if (!this.positive.CanHold(other.positive) || !this.negative.CanHold(other.negative))
            {
                throw new ArgumentException(
                    "The merged buckets would run further than one array holds, " +
                    $"{Array.MaxLength:N0} buckets from the lowest in use to the highest. " +
                    "The two sketches hold values too far apart to be one sketch at this " +
                    "relative accuracy.",
                    nameof(other));
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
            var payload = PersistenceFormat.Read(stream, StructureId.DDSketch, out var hashId, budget);

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

            // The constructor's own limit, so that every sketch it builds reads back and
            // none it refuses does.
            if (!HasBucketRatio(accuracy))
            {
                throw new InvalidDataException(
                    $"Sketch has a relative accuracy of {accuracy}, which is finer than a " +
                    "double can tell one bucket from the next: the ratio between buckets " +
                    "rounds to exactly 1. This library builds no sketch that fine.");
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
        /// Which bucket a positive value falls in, refusing a value whose bucket is not a
        /// 32-bit index. Cast to an int unchecked, an index past either end saturates, and
        /// every value beyond it is filed in the same last bucket.
        /// </summary>
        private int IndexOf(double value)
        {
            var index = Math.Ceiling(Math.Log(value) / this.logGamma);

            if (!(index >= int.MinValue && index <= int.MaxValue))
            {
                // Bucket i holds the values up to gamma^i, so the last index an int has
                // holds up to gamma^int.MaxValue, and the first holds what is above
                // gamma^(int.MinValue - 1).
                var highest = Math.Min(Math.Exp(int.MaxValue * this.logGamma), double.MaxValue);
                var lowest = Math.Max(Math.Exp((int.MinValue - 1.0) * this.logGamma), double.Epsilon);

                throw new ArgumentOutOfRangeException(
                    nameof(value), value,
                    $"At a relative accuracy of {this.relativeAccuracy} a bucket is numbered " +
                    "by a 32-bit integer, so only values from about " +
                    $"{lowest:G3} to {highest:G3} (in magnitude) have a bucket, and " +
                    $"{Math.Abs(value):G3} does not. A coarser accuracy reaches further: " +
                    "every double has a bucket from about 1.74e-7.");
            }

            return (int)index;
        }

        /// <summary>
        /// Refuses a value whose bucket is further from the ones in use than one array
        /// holds, before anything has been changed.
        /// </summary>
        private void RequireRoom(Store store, int index, double value)
        {
            if (!store.CanHold(index))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value,
                    "The buckets from the lowest in use to this value's would number more " +
                    $"than the {Array.MaxLength:N0} one array holds, at a relative accuracy " +
                    $"of {this.relativeAccuracy}. Values this far apart need a coarser " +
                    "accuracy, or sketches of their own.");
            }
        }

        /// <summary>
        /// The value a bucket reports: the point within it that is no further than the
        /// relative accuracy from anything the bucket can hold.
        /// </summary>
        /// <remarks>
        /// The midpoint is 2 * gamma^i / (gamma + 1), and written that way the working
        /// leaves the range of a double before the answer does. Twice gamma^i is
        /// infinite from 2^1023 upward, and gamma^i is itself infinite just past the
        /// largest double, though the answer, (1 - a) * gamma^i, is at most the largest
        /// value the bucket holds: a quantile over the largest doubles came back as
        /// infinity. And at the other end, at a coarse accuracy the answer for the
        /// smallest doubles rounds to zero, which is not within the accuracy of anything
        /// positive.
        /// <para>
        /// So an answer that is finite and nonzero is the one it always was, bit for bit,
        /// and the two kinds that are not are replaced. An infinite one is worked out
        /// again without an intermediate outside the doubles: gamma^i as the product of
        /// two halves of the power, each between 1e-162 and 1e155, with (1 - a) between
        /// them. If that is infinite too, the largest double stands in for it: every
        /// value the bucket holds is at most the largest double and above the midpoint
        /// over (1 + a), so the largest double is within the accuracy of all of them. A
        /// zero is the smallest double, for the bucket holds one, so the midpoint was
        /// no more than half of it over (1 - a), and so no value in the bucket is more
        /// than 1 / (1 - a) of it.
        /// </para>
        /// <para>
        /// Among the subnormals the answer is still gamma^i rounded to a multiple of
        /// 4.9e-324 and then rounded again, which can leave it a unit from the nearest
        /// double to the midpoint. It is not corrected, because the same sum answers
        /// correctly for most buckets there and telling which is which needs the bucket's
        /// own largest and smallest doubles; a bucket that answered within the accuracy
        /// keeps its answer.
        /// </para>
        /// </remarks>
        private double ValueAt(long index)
        {
            var value = 2 * Math.Pow(this.gamma, index) / (this.gamma + 1);

            if (value == 0)
            {
                return double.Epsilon;
            }

            if (!double.IsInfinity(value))
            {
                return value;
            }

            var half = index / 2;
            value = Math.Pow(this.gamma, half) * (2 / (this.gamma + 1)) * Math.Pow(this.gamma, index - half);

            return double.IsInfinity(value) ? double.MaxValue : value;
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

            /// <summary>
            /// The bucket index of the array's first slot. A long because a window around
            /// an index near either end of the ints reaches past it: the first slot of
            /// the window around int.MinValue sits below int.MinValue.
            /// </summary>
            private long offset;

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

            internal void Merge(Store other)
            {
                if (other.IsEmpty)
                {
                    return;
                }

                // A long, so that a run ending at int.MaxValue ends the loop instead of
                // wrapping to int.MinValue and starting again.
                for (long i = other.MinIndex; i <= other.MaxIndex; i++)
                {
                    var amount = other.CountAt(i);
                    if (amount != 0)
                    {
                        this.Add((int)i, amount);
                    }
                }
            }

            /// <summary>
            /// Whether a bucket at this index can be added without the run from the lowest
            /// bucket in use to the highest outgrowing one array.
            /// </summary>
            internal bool CanHold(int index)
            {
                return SpanFits(this.SpanWith(index, index));
            }

            /// <summary>
            /// Whether the other store's buckets can be added to this one's, as
            /// <see cref="CanHold(int)"/> asks of one.
            /// </summary>
            internal bool CanHold(Store other)
            {
                return other.IsEmpty || SpanFits(this.SpanWith(other.MinIndex, other.MaxIndex));
            }

            /// <summary>
            /// How many buckets the run from the lowest in use to the highest would be with
            /// these two indices included.
            /// </summary>
            private long SpanWith(int low, int high)
            {
                var lowest = this.IsEmpty ? low : Math.Min(low, this.MinIndex);
                var highest = this.IsEmpty ? high : Math.Max(high, this.MaxIndex);
                return (long)highest - lowest + 1;
            }

            internal ulong CountAt(long index)
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
                    this.offset = (long)index - (InitialCapacity / 2);
                    return;
                }

                if (index >= this.offset && index < this.offset + this.counts.Length)
                {
                    return;
                }

                var low = Math.Min(index, this.IsEmpty ? index : this.MinIndex);
                var high = Math.Max(index, this.IsEmpty ? index : this.MaxIndex);
                var needed = (long)high - low + 1;

                // Callers have checked the run fits (CanHold), so doubling stops at the
                // largest array rather than asking for one that cannot exist.
                long capacity = this.counts.Length;
                while (capacity < needed)
                {
                    capacity *= 2;
                }

                capacity = Math.Min(capacity, Array.MaxLength);

                var grown = new ulong[capacity];
                var newOffset = low - ((capacity - needed) / 2);

                if (!this.IsEmpty)
                {
                    Array.Copy(
                        this.counts,
                        this.MinIndex - this.offset,
                        grown,
                        this.MinIndex - newOffset,
                        (long)this.MaxIndex - this.MinIndex + 1);
                }

                this.counts = grown;
                this.offset = newOffset;
            }
        }
    }
}
