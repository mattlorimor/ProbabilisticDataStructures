using System;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Estimates how many distinct keys a stream held <em>and</em> what their values add
    /// up to, and supports union, intersection and difference between sketches.
    /// </summary>
    /// <remarks>
    /// A <see cref="ThetaSketch"/> answers "how many distinct users were there". This
    /// answers "how many distinct users were there, and what did they spend between
    /// them", from one pass and one structure. It is the tuple sketch of the
    /// DataSketches library: the same sampling, with a value carried alongside every
    /// hash that survives it.
    /// <para>
    /// The sampling is what makes both answers possible at once. A hash is kept when it
    /// falls below theta, and because a hash is uniform, that happens with probability
    /// theta -- so the keys kept are a uniform sample of the distinct keys, and the
    /// values riding along with them are a uniform sample of the per-key totals.
    /// Dividing either count by the sampling rate estimates the whole.
    /// </para>
    /// <para>
    /// The value is per <em>distinct key</em>, not per record. Adding the same user
    /// twice folds their two values together under the
    /// <see cref="SummaryPolicy"/> rather than counting them as two users or two
    /// separate amounts. That is the whole difference from summing a column: this
    /// deduplicates as it goes, at a size that does not grow with the stream.
    /// </para>
    /// </remarks>
    public class TupleSketch : IBinaryPersistable<TupleSketch>
    {
        private uint nominalEntries;

        /// <summary>
        /// The hash values kept, all below <see cref="theta"/>.
        /// </summary>
        private ulong[] values;

        /// <summary>
        /// The summary for each kept hash, in the same order.
        /// </summary>
        /// <remarks>
        /// A second array rather than an array of pairs. The two are sorted, trimmed and
        /// written together and must never drift apart, which is the one thing that can
        /// go quietly wrong here: a sketch whose summaries no longer line up with their
        /// keys gives answers that look entirely reasonable.
        /// </remarks>
        private double[] summaries;

        private int held;

        /// <summary>Whether the values are sorted, distinct, and their summaries folded.</summary>
        private bool compact = true;

        /// <summary>
        /// The threshold below which a hash is kept, and so also the sampling rate.
        /// </summary>
        private ulong theta = ulong.MaxValue;

        private readonly SummaryPolicy policy;

        internal Func<ReadOnlySpan<byte>, ulong> Hash { get; set; } = null!;

        /// <summary>
        /// Creates a sketch retaining the given number of keys.
        /// </summary>
        /// <param name="nominalEntries">How many keys the sketch keeps.</param>
        /// <param name="policy">How several values for one key are folded together.</param>
        public TupleSketch(uint nominalEntries, SummaryPolicy policy = SummaryPolicy.Sum)
        {
            Guard.ValidItemCount(nominalEntries, nameof(nominalEntries));

            if (!Enum.IsDefined(policy))
            {
                throw new ArgumentOutOfRangeException(nameof(policy),
                    $"{policy} is not a way of folding values this sketch knows.");
            }

            this.nominalEntries = nominalEntries;
            this.policy = policy;
            this.values = new ulong[Math.Min(16, this.Limit)];
            this.summaries = new double[this.values.Length];
            this.Hash = Defaults.GetDefaultHashFunction();
        }

        /// <summary>How several values for one key are folded together.</summary>
        public SummaryPolicy Policy => this.policy;

        /// <summary>
        /// The keys the sketch is holding, so that tests can check a summary still sits
        /// beside the key it belongs to.
        /// </summary>
        internal ulong[] KeysHeld
        {
            get
            {
                this.EnsureCompact();
                return this.values[..this.held];
            }
        }

        /// <summary>
        /// The summaries the sketch is holding, in the same order as
        /// <see cref="KeysHeld"/>.
        /// </summary>
        internal double[] SummariesHeld
        {
            get
            {
                this.EnsureCompact();
                return this.summaries[..this.held];
            }
        }

        /// <summary>
        /// Adds a key and its value. Returns the sketch to allow for chaining.
        /// </summary>
        /// <param name="data">The key.</param>
        /// <param name="value">The value to fold into that key's summary.</param>
        public TupleSketch Add(byte[] data, double value)
        {
            ArgumentNullException.ThrowIfNull(data);
            return this.Add(data.AsSpan(), value);
        }

        /// <inheritdoc cref="Add(byte[], double)"/>
        public TupleSketch Add(ReadOnlySpan<byte> data, double value)
        {
            if (double.IsNaN(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value),
                    "A summary cannot be built from something that is not a number: it " +
                    "would spread to every total the key ever takes part in, and " +
                    "compare as neither larger nor smaller under Min or Max.");
            }

            var hash = this.Hash(data);

            if (hash >= this.theta)
            {
                return this;
            }

            if (this.held == this.values.Length)
            {
                this.Compact();

                // Compacting to make room can trim, and trimming lowers theta, so the
                // hash has to be checked against the theta that now stands.
                if (hash >= this.theta)
                {
                    return this;
                }
            }

            this.values[this.held] = hash;
            this.summaries[this.held] = value;
            this.held++;
            this.compact = false;

            return this;
        }

        /// <summary>
        /// The estimated number of distinct keys.
        /// </summary>
        public ulong Count()
        {
            this.EnsureCompact();

            if (this.theta == ulong.MaxValue)
            {
                return (ulong)this.held;
            }

            return (ulong)Math.Round(this.held / this.Fraction());
        }

        /// <summary>
        /// The estimated total of the summaries across every distinct key.
        /// </summary>
        /// <remarks>
        /// Under <see cref="SummaryPolicy.Sum"/> this is the total of the values added,
        /// counting each distinct key's contributions once however often the key
        /// appeared. Under the other policies it is the total of the per-key smallest
        /// or largest.
        /// <para>
        /// It carries the sampling error of the keys, not of the values. A sketch that
        /// happens to keep the largest spenders will read high and one that keeps the
        /// smallest will read low, by rather more than the count is out by -- the count
        /// is a sum of ones, and this is a sum of whatever the values happen to be. It
        /// is an estimate of a total, not a total.
        /// </para>
        /// </remarks>
        public double Total()
        {
            this.EnsureCompact();

            var total = 0.0;
            for (var i = 0; i < this.held; i++)
            {
                total += this.summaries[i];
            }

            return this.theta == ulong.MaxValue ? total : total / this.Fraction();
        }

        /// <summary>
        /// A new sketch of every key in either sketch, their summaries folded where a
        /// key is in both.
        /// </summary>
        /// <param name="other">The sketch to combine with. Neither is modified.</param>
        public TupleSketch Union(TupleSketch other)
        {
            ArgumentNullException.ThrowIfNull(other);
            this.RequireCompatible(other);
            this.EnsureCompact();
            other.EnsureCompact();

            var theta = Math.Min(this.theta, other.theta);
            var keys = new ulong[this.held + other.held];
            var folded = new double[keys.Length];
            int i = 0, j = 0, n = 0;

            while (i < this.held || j < other.held)
            {
                ulong key;
                double summary;

                if (j >= other.held || (i < this.held && this.values[i] <= other.values[j]))
                {
                    key = this.values[i];
                    summary = this.summaries[i];
                    i++;

                    if (j < other.held && other.values[j] == key)
                    {
                        summary = Fold(summary, other.summaries[j], this.policy);
                        j++;
                    }
                }
                else
                {
                    key = other.values[j];
                    summary = other.summaries[j];
                    j++;
                }

                if (key < theta)
                {
                    keys[n] = key;
                    folded[n] = summary;
                    n++;
                }
            }

            return From(this, theta, keys, folded, n);
        }

        /// <summary>
        /// A new sketch of the keys in both sketches, their summaries folded.
        /// </summary>
        /// <param name="other">The sketch to intersect with. Neither is modified.</param>
        public TupleSketch Intersect(TupleSketch other)
        {
            ArgumentNullException.ThrowIfNull(other);
            this.RequireCompatible(other);
            this.EnsureCompact();
            other.EnsureCompact();

            var theta = Math.Min(this.theta, other.theta);
            var size = Math.Min(this.held, other.held);
            var keys = new ulong[size];
            var folded = new double[size];
            int i = 0, j = 0, n = 0;

            while (i < this.held && j < other.held)
            {
                if (this.values[i] < other.values[j])
                {
                    i++;
                }
                else if (this.values[i] > other.values[j])
                {
                    j++;
                }
                else
                {
                    if (this.values[i] < theta)
                    {
                        keys[n] = this.values[i];
                        folded[n] = Fold(this.summaries[i], other.summaries[j], this.policy);
                        n++;
                    }

                    i++;
                    j++;
                }
            }

            return From(this, theta, keys, folded, n);
        }

        /// <summary>
        /// A new sketch of the keys in this sketch but not the other, keeping this
        /// sketch's summaries.
        /// </summary>
        /// <param name="other">The sketch to subtract. Neither is modified.</param>
        public TupleSketch Difference(TupleSketch other)
        {
            ArgumentNullException.ThrowIfNull(other);
            this.RequireCompatible(other);
            this.EnsureCompact();
            other.EnsureCompact();

            var theta = Math.Min(this.theta, other.theta);
            var keys = new ulong[this.held];
            var kept = new double[this.held];
            int i = 0, j = 0, n = 0;

            while (i < this.held)
            {
                while (j < other.held && other.values[j] < this.values[i])
                {
                    j++;
                }

                var absent = j >= other.held || other.values[j] != this.values[i];

                if (absent && this.values[i] < theta)
                {
                    keys[n] = this.values[i];
                    kept[n] = this.summaries[i];
                    n++;
                }

                i++;
            }

            return From(this, theta, keys, kept, n);
        }

        /// <summary>
        /// How many keys the sketch is holding, before any estimation.
        /// </summary>
        public uint Retained()
        {
            this.EnsureCompact();
            return (uint)this.held;
        }

        /// <summary>
        /// The sketch's storage in bytes.
        /// </summary>
        public ulong SizeInBytes()
        {
            return ((ulong)this.values.Length * sizeof(ulong))
                + ((ulong)this.summaries.Length * sizeof(double));
        }

        /// <summary>
        /// Sets the hashing function used by the sketch.
        /// </summary>
        /// <param name="h">The hash function to use.</param>
        public void SetHash(Func<ReadOnlySpan<byte>, ulong> h)
        {
            ArgumentNullException.ThrowIfNull(h);
            Guard.HashMayBeReplaced(this.held == 0, nameof(TupleSketch));
            this.Hash = h;
        }

        /// <summary>
        /// Folds two values for the same key into one.
        /// </summary>
        private static double Fold(double left, double right, SummaryPolicy policy) =>
            policy switch
            {
                SummaryPolicy.Min => Math.Min(left, right),
                SummaryPolicy.Max => Math.Max(left, right),
                _ => left + right,
            };

        /// <summary>
        /// Refuses sketches whose values do not mean the same thing.
        /// </summary>
        private void RequireCompatible(TupleSketch other)
        {
            if (this.nominalEntries != other.nominalEntries)
            {
                throw new ArgumentException(
                    "Sketches must retain the same number of keys to be combined: " +
                    $"this one retains {this.nominalEntries} and the other " +
                    $"{other.nominalEntries}.",
                    nameof(other));
            }

            if (this.policy != other.policy)
            {
                throw new ArgumentException(
                    $"This sketch folds values by {this.policy} and the other by " +
                    $"{other.policy}. Combining them would produce summaries that were " +
                    "each built one way and folded another.",
                    nameof(other));
            }

            Guard.SameHashFunction(this.Hash, other.Hash, nameof(other));
        }

        private double Fraction()
        {
            return this.theta / 18446744073709551616.0;
        }

        private int Limit => (int)(2 * this.nominalEntries);

        private void EnsureCompact()
        {
            if (!this.compact)
            {
                this.Compact();
            }
        }

        /// <summary>
        /// Sorts the keys, folds the repeats, and trims to size.
        /// </summary>
        /// <remarks>
        /// This is where a tuple sketch differs from a theta sketch. A theta sketch
        /// drops a repeated hash, because one is as good as another; here the repeats
        /// carry values that have to be folded together, and dropping one would lose an
        /// amount rather than a duplicate.
        /// </remarks>
        private void Compact()
        {
            Array.Sort(this.values, this.summaries, 0, this.held);

            var distinct = 0;
            for (var i = 0; i < this.held; i++)
            {
                if (i > 0 && this.values[i] == this.values[i - 1])
                {
                    this.summaries[distinct - 1] =
                        Fold(this.summaries[distinct - 1], this.summaries[i], this.policy);
                    continue;
                }

                this.values[distinct] = this.values[i];
                this.summaries[distinct] = this.summaries[i];
                distinct++;
            }

            this.held = distinct;
            this.compact = true;

            if (distinct >= this.Limit)
            {
                // The first discarded key becomes theta, so everything kept is strictly
                // below it and the sampling rate is exactly what was applied.
                this.theta = this.values[this.nominalEntries];
                this.held = (int)this.nominalEntries;
                return;
            }

            if (distinct > this.values.Length / 2 && this.values.Length < this.Limit)
            {
                var grown = Math.Min(this.values.Length * 2, this.Limit);
                Array.Resize(ref this.values, grown);
                Array.Resize(ref this.summaries, grown);
            }
        }

        /// <summary>
        /// Builds a sketch from keys already sorted and distinct, trimming if there are
        /// more than it may keep.
        /// </summary>
        private static TupleSketch From(
            TupleSketch like, ulong theta, ulong[] keys, double[] folded, int count)
        {
            var sketch = new TupleSketch(like.nominalEntries, like.policy)
            {
                Hash = like.Hash,
                theta = theta,
            };

            if (count >= sketch.Limit)
            {
                sketch.theta = keys[like.nominalEntries];
                count = (int)like.nominalEntries;
            }

            var size = Math.Max(16, count);
            sketch.values = new ulong[size];
            sketch.summaries = new double[size];
            Array.Copy(keys, sketch.values, count);
            Array.Copy(folded, sketch.summaries, count);
            sketch.held = count;
            sketch.compact = true;

            return sketch;
        }

        /// <summary>
        /// Writes the sketch to a stream.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            this.EnsureCompact();

            var payload = new PayloadWriter();
            payload.WriteUInt32(this.nominalEntries);
            payload.WriteByte((byte)this.policy);
            payload.WriteUInt64(this.theta);
            payload.WriteUInt32((uint)this.held);

            // Keys first and then summaries, rather than interleaved, so that a payload
            // whose keys are not in increasing order is detectably not one of these
            // without having to step over the values between them.
            for (var i = 0; i < this.held; i++)
            {
                payload.WriteUInt64(this.values[i]);
            }
            for (var i = 0; i < this.held; i++)
            {
                payload.WriteDouble(this.summaries[i]);
            }

            PersistenceFormat.Write(
                stream,
                StructureId.TupleSketch,
                PersistenceFormat.Identify(this.Hash),
                payload.WrittenSpan);
        }

        /// <summary>
        /// Reads a sketch written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        public static TupleSketch ReadFrom(Stream stream)
        {
            return Read(stream, null);
        }

        /// <summary>
        /// Reads a sketch written by <see cref="WriteTo"/>, installing a hash function.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function the sketch was written with.</param>
        public static TupleSketch ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            return Read(stream, hash);
        }

        private static TupleSketch Read(Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(
                stream, StructureId.TupleSketch, out var hashId);
            var reader = new PayloadReader(payload);

            var nominalEntries = reader.ReadUInt32();
            var policy = (SummaryPolicy)reader.ReadByte();
            var theta = reader.ReadUInt64();
            var held = reader.ReadUInt32();

            if (nominalEntries == 0 || nominalEntries > PersistenceFormat.MaxNestedCount)
            {
                throw new InvalidDataException(
                    $"Sketch retains {nominalEntries} keys, which is not a size this " +
                    "library builds.");
            }

            if (!Enum.IsDefined(policy))
            {
                throw new InvalidDataException(
                    $"Sketch folds values by {(byte)policy}, which is not a way of " +
                    "folding this library knows.");
            }

            // A sketch trims once it reaches twice what it retains, so it never holds
            // more than that.
            if (held > 2 * nominalEntries)
            {
                throw new InvalidDataException(
                    $"Sketch holds {held} keys and retains {nominalEntries}, and one " +
                    "never holds more than twice what it retains.");
            }

            var size = Math.Max(16, (int)held);
            var sketch = new TupleSketch(nominalEntries, policy)
            {
                theta = theta,
                Hash = PersistenceFormat.ResolveOrThrow(hashId, hash),
                values = new ulong[size],
                summaries = new double[size],
            };

            var previous = 0UL;
            for (var i = 0u; i < held; i++)
            {
                var key = reader.ReadUInt64();

                if (i > 0 && key <= previous)
                {
                    throw new InvalidDataException(
                        "Sketch's keys are not in increasing order, so they are not " +
                        "the distinct set it would have written.");
                }

                if (key >= theta)
                {
                    throw new InvalidDataException(
                        $"Key {key} is at or above the sketch's threshold of {theta}, " +
                        "so it is a key the sampling that produced the rest would have " +
                        "discarded.");
                }

                sketch.values[i] = key;
                previous = key;
            }

            for (var i = 0u; i < held; i++)
            {
                var summary = reader.ReadDouble();

                if (double.IsNaN(summary))
                {
                    throw new InvalidDataException(
                        $"The summary for key {i} is not a number, which this sketch " +
                        "refuses on the way in and so cannot have written.");
                }

                sketch.summaries[i] = summary;
            }

            reader.ExpectEnd();

            sketch.held = (int)held;
            sketch.compact = true;
            return sketch;
        }
    }
}
