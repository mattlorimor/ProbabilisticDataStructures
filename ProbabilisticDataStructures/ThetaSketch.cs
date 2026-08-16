using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Estimates how many distinct items a stream held, and supports union,
    /// intersection and difference between sketches.
    /// </summary>
    public class ThetaSketch : IBinaryPersistable<ThetaSketch>
    {
        private uint nominalEntries;

        /// <summary>
        /// The hash values kept, all of them below <see cref="theta"/>.
        /// </summary>
        /// <remarks>
        /// A plain buffer rather than a set. It may hold duplicates between compactions,
        /// which the sort removes -- and a sort is already how the sketch decides what to
        /// discard, so deduplicating costs nothing extra. A hash set would spend roughly
        /// three times the memory of the values themselves on entry overhead, which
        /// matters here because memory is this structure's weak side against
        /// <see cref="HyperLogLogPlus"/>.
        /// <para>
        /// Once compacted the values are sorted and distinct, which also makes union,
        /// intersection and difference linear merges rather than lookups.
        /// </para>
        /// </remarks>
        private ulong[] values;

        private int held;

        /// <summary>Whether <see cref="values"/> is sorted and free of duplicates.</summary>
        private bool compact = true;

        /// <summary>
        /// The threshold below which a hash is kept, as a point in the hash's own range.
        /// <para>
        /// It starts at the top of the range, meaning everything is kept and the sketch
        /// is exact. Once more values arrive than the sketch is allowed to hold, it drops
        /// to the value that separates the ones kept from the ones discarded -- which is
        /// also the sampling rate, since a hash is uniform over the range and so lands
        /// below theta with probability theta.
        /// </para>
        /// </summary>
        private ulong theta = ulong.MaxValue;

        internal Func<ReadOnlySpan<byte>, ulong> Hash { get; set; } = null!;

        /// <summary>
        /// Creates a sketch retaining the given number of hash values.
        /// </summary>
        /// <param name="nominalEntries">How many values the sketch keeps.</param>
        public ThetaSketch(uint nominalEntries)
        {
            Guard.ValidItemCount(nominalEntries, nameof(nominalEntries));
            this.nominalEntries = nominalEntries;
            this.values = new ulong[Math.Min(16, this.Limit)];
            this.Hash = Defaults.GetDefaultHashFunction();
        }

        /// <summary>
        /// Adds data to the sketch. Returns the sketch to allow for chaining.
        /// </summary>
        /// <param name="data">The data to add.</param>
        /// <returns>The sketch.</returns>
        public ThetaSketch Add(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            return this.Add(data.AsSpan());
        }

        /// <inheritdoc cref="Add(byte[])"/>
        public ThetaSketch Add(ReadOnlySpan<byte> data)
        {
            var hash = this.Hash(data);

            if (hash >= this.theta)
            {
                return this;
            }

            if (this.held == this.values.Length)
            {
                this.Compact();

                // Compacting to make room can trim, and trimming lowers theta. The
                // hash was checked against the theta that stood before the trim, so
                // it has to be checked again: stored past the new theta, it would be
                // a sample the sampling rate never applied to -- and a value the
                // persistence reader rightly refuses, leaving a sketch that cannot
                // read its own bytes.
                if (hash >= this.theta)
                {
                    return this;
                }
            }

            this.values[this.held++] = hash;
            this.compact = false;

            return this;
        }

        /// <summary>
        /// A new sketch of everything in either this sketch or the other.
        /// </summary>
        /// <param name="other">The sketch to combine with. Neither is modified.</param>
        /// <returns>A sketch of the union.</returns>
        /// <remarks>
        /// Both sketches sampled their items by keeping the hashes below their own
        /// theta, so the combined sketch can only be trusted below the lower of the two.
        /// Everything above it was sampled by one sketch and not the other, and keeping
        /// it would count those items at a rate the estimate does not apply.
        /// </remarks>
        public ThetaSketch Union(ThetaSketch other)
        {
            ArgumentNullException.ThrowIfNull(other);
            this.RequireCompatible(other);
            this.EnsureCompact();
            other.EnsureCompact();

            var theta = Math.Min(this.theta, other.theta);
            var merged = new ulong[this.held + other.held];
            int i = 0, j = 0, n = 0;

            while (i < this.held || j < other.held)
            {
                ulong value;

                if (j >= other.held || (i < this.held && this.values[i] <= other.values[j]))
                {
                    value = this.values[i++];
                    if (j < other.held && other.values[j] == value)
                    {
                        j++;
                    }
                }
                else
                {
                    value = other.values[j++];
                }

                if (value < theta)
                {
                    merged[n++] = value;
                }
            }

            return From(this.nominalEntries, this.Hash, theta, merged, n);
        }

        /// <summary>
        /// A new sketch of everything in both this sketch and the other.
        /// </summary>
        /// <param name="other">The sketch to intersect with. Neither is modified.</param>
        /// <returns>A sketch of the intersection.</returns>
        /// <remarks>
        /// This is what a <see cref="HyperLogLog"/> cannot do. Both sketches sampled by
        /// keeping hashes below their theta, and a hash below the lower of the two thetas
        /// was sampled by <b>both</b> -- so an item in both sets appears in both samples
        /// or in neither, and counting the values they share estimates the intersection
        /// at the same sampling rate everything else is estimated at.
        /// <para>
        /// Read the error carefully. It scales with the size of the sets, not with the
        /// size of the intersection, so a small intersection between large sets carries
        /// an absolute error that can exceed the answer. That is still far better than
        /// subtracting one cardinality estimate from another, which carries the error of
        /// both, but it is not a small relative error just because the sketches are
        /// accurate.
        /// </para>
        /// </remarks>
        public ThetaSketch Intersect(ThetaSketch other)
        {
            ArgumentNullException.ThrowIfNull(other);
            this.RequireCompatible(other);
            this.EnsureCompact();
            other.EnsureCompact();

            var theta = Math.Min(this.theta, other.theta);
            var shared = new ulong[Math.Min(this.held, other.held)];
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
                        shared[n++] = this.values[i];
                    }

                    i++;
                    j++;
                }
            }

            return From(this.nominalEntries, this.Hash, theta, shared, n);
        }

        /// <summary>
        /// A new sketch of everything in this sketch but not the other.
        /// </summary>
        /// <param name="other">The sketch to subtract. Neither is modified.</param>
        /// <returns>A sketch of the difference.</returns>
        public ThetaSketch Difference(ThetaSketch other)
        {
            ArgumentNullException.ThrowIfNull(other);
            this.RequireCompatible(other);
            this.EnsureCompact();
            other.EnsureCompact();

            var theta = Math.Min(this.theta, other.theta);
            var only = new ulong[this.held];
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
                    only[n++] = this.values[i];
                }

                i++;
            }

            return From(this.nominalEntries, this.Hash, theta, only, n);
        }

        /// <summary>
        /// Refuses sketches whose values do not mean the same thing.
        /// </summary>
        private void RequireCompatible(ThetaSketch other)
        {
            if (this.nominalEntries != other.nominalEntries)
            {
                throw new ArgumentException(
                    "Sketches must retain the same number of values to be combined: " +
                    $"this one retains {this.nominalEntries} and the other " +
                    $"{other.nominalEntries}. The retained size must match.",
                    nameof(other));
            }

            Guard.SameHashFunction(this.Hash, other.Hash, nameof(other));
        }

        /// <summary>
        /// The estimated number of distinct items.
        /// </summary>
        public ulong Count()
        {
            this.EnsureCompact();

            // Nothing has been discarded, so the sketch holds every distinct item it saw.
            if (this.theta == ulong.MaxValue)
            {
                return (ulong)this.held;
            }

            return (ulong)Math.Round(this.held / this.Fraction());
        }

        /// <summary>
        /// The sketch's storage in bytes, which is all it occupies beyond a handful of
        /// fields.
        /// </summary>
        /// <remarks>
        /// Exact rather than measured, because it is one array of one primitive type.
        /// That is the point of holding the values in a buffer rather than a set: what
        /// the sketch costs is the values themselves and nothing per value besides.
        /// </remarks>
        public ulong SizeInBytes()
        {
            return (ulong)this.values.Length * sizeof(ulong);
        }

        /// <summary>
        /// Sets the hashing function used by the sketch.
        /// </summary>
        /// <param name="h">The hash function to use.</param>
        /// <exception cref="InvalidOperationException">
        /// Anything has been added. The hash cannot be replaced then, because every value
        /// already retained was produced by the old one, and the two would be compared
        /// against each other by any set operation.
        /// </exception>
        public void SetHash(Func<ReadOnlySpan<byte>, ulong> h)
        {
            ArgumentNullException.ThrowIfNull(h);
            Guard.HashMayBeReplaced(this.held == 0, nameof(ThetaSketch));
            this.Hash = h;
        }

        /// <summary>
        /// The number of hash values the sketch is currently holding.
        /// </summary>
        public uint Retained()
        {
            this.EnsureCompact();
            return (uint)this.held;
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
            payload.WriteUInt64(this.theta);
            payload.WriteUInt32((uint)this.held);

            // Already sorted and distinct, which makes a payload whose values are not in
            // order detectably not one of these.
            for (var i = 0; i < this.held; i++)
            {
                payload.WriteUInt64(this.values[i]);
            }

            PersistenceFormat.Write(
                stream,
                StructureId.ThetaSketch,
                PersistenceFormat.Identify(this.Hash),
                payload.WrittenSpan);
        }

        /// <summary>
        /// Reads a sketch written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The sketch that was written.</returns>
        public static ThetaSketch ReadFrom(Stream stream)
        {
            return Read(stream, null);
        }

        /// <summary>
        /// Reads a sketch written by <see cref="WriteTo"/>, using the supplied hash
        /// function rather than the one named in the payload.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function the sketch was using.</param>
        /// <returns>The sketch that was written.</returns>
        public static ThetaSketch ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            return Read(stream, hash);
        }

        private static ThetaSketch Read(Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(stream, StructureId.ThetaSketch, out var hashId);
            var reader = new PayloadReader(payload);

            var nominalEntries = reader.ReadUInt32();
            var theta = reader.ReadUInt64();
            var held = reader.ReadUInt32();

            if (nominalEntries == 0 || nominalEntries > PersistenceFormat.MaxNestedCount)
            {
                throw new InvalidDataException(
                    $"Sketch retains {nominalEntries} values, which is not a size this " +
                    "library builds.");
            }

            // A sketch trims once it reaches twice its retained size, so it never holds
            // more than that.
            if (held > 2 * nominalEntries)
            {
                throw new InvalidDataException(
                    $"Sketch holds {held} values and retains {nominalEntries}, and one " +
                    "never holds more than twice what it retains.");
            }

            var sketch = new ThetaSketch(nominalEntries)
            {
                theta = theta,
                Hash = PersistenceFormat.ResolveOrThrow(hashId, hash),
                values = new ulong[Math.Max(16, (int)held)],
            };

            var previous = 0UL;
            for (var i = 0u; i < held; i++)
            {
                var value = reader.ReadUInt64();

                if (i > 0 && value <= previous)
                {
                    throw new InvalidDataException(
                        "Sketch's values are not in increasing order, so they are not " +
                        "the distinct set it would have written.");
                }

                if (value >= theta)
                {
                    // Versions through 6.0.0 could store exactly one value at or above
                    // theta: Add checked the hash before compacting to make room, and
                    // compacting can lower theta past it. Each trim discards any such
                    // value a previous trim let in before possibly admitting its own,
                    // so at most one survives, and the sort puts it last. It was never
                    // a valid sample -- theta's fraction was not applied to it -- so
                    // it is dropped rather than kept, and anything beyond that one
                    // trailing value is not this library's output.
                    if (i == held - 1)
                    {
                        previous = value;
                        continue;
                    }

                    throw new InvalidDataException(
                        $"Sketch holds the value {value} at or above its theta of " +
                        $"{theta}, which is the threshold it keeps values below.");
                }

                sketch.values[sketch.held++] = value;
                previous = value;
            }

            reader.ExpectEnd();
            return sketch;
        }

        /// <summary>
        /// Theta as a fraction of the hash range, which is the probability that any
        /// given item is one of the ones kept.
        /// </summary>
        private double Fraction()
        {
            return this.theta / 18446744073709551616.0;
        }

        /// <summary>The most values the sketch ever holds before trimming.</summary>
        private int Limit => (int)(2 * this.nominalEntries);

        private void EnsureCompact()
        {
            if (!this.compact)
            {
                this.Compact();
            }
        }

        /// <summary>
        /// Sorts the buffer, drops duplicates, and either trims to the smallest values
        /// worth keeping or makes room for more.
        /// </summary>
        private void Compact()
        {
            Array.Sort(this.values, 0, this.held);

            var distinct = 0;
            for (var i = 0; i < this.held; i++)
            {
                if (i == 0 || this.values[i] != this.values[i - 1])
                {
                    this.values[distinct++] = this.values[i];
                }
            }

            this.held = distinct;
            this.compact = true;

            if (distinct >= this.Limit)
            {
                // The first discarded value becomes theta, so everything kept is
                // strictly below it and the sampling rate is exactly what was applied.
                this.theta = this.values[this.nominalEntries];
                this.held = (int)this.nominalEntries;
                return;
            }

            if (distinct > this.values.Length / 2 && this.values.Length < this.Limit)
            {
                Array.Resize(ref this.values, Math.Min(this.values.Length * 2, this.Limit));
            }
        }

        /// <summary>
        /// Builds a sketch from values already sorted and distinct, trimming if there
        /// are more than it may keep.
        /// </summary>
        private static ThetaSketch From(
            uint nominalEntries, Func<ReadOnlySpan<byte>, ulong> hash, ulong theta,
            ulong[] sorted, int count)
        {
            var sketch = new ThetaSketch(nominalEntries) { Hash = hash, theta = theta };

            if (count >= sketch.Limit)
            {
                sketch.theta = sorted[nominalEntries];
                count = (int)nominalEntries;
            }

            sketch.values = new ulong[Math.Max(16, count)];
            Array.Copy(sorted, sketch.values, count);
            sketch.held = count;
            sketch.compact = true;

            return sketch;
        }
    }
}
