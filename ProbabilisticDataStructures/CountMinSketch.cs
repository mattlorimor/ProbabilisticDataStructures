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
        /// Relative-accuracy probability
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
        /// Creates a new Count-Min Sketch whose relative accuracy is within a factor of
        /// epsilon with probability delta. Both of these parameters affect the space and
        /// time complexity.
        /// </summary>
        /// <param name="epsilon">Relative-accuracy factor</param>
        /// <param name="delta">Relative-accuracy probability</param>
        /// <param name="hash">
        /// The hash function to use, or null for the default. Passing it here is the
        /// only way to have one hash cover everything the structure will ever hold:
        /// once anything has been added, the hash can no longer be replaced.
        /// </param>
        public CountMinSketch(double epsilon, double delta, Func<ReadOnlySpan<byte>, ulong>? hash = null)
        {
            Guard.ValidSketchEpsilon(epsilon, nameof(epsilon));
            Guard.ValidSketchDelta(delta, nameof(delta));

            var width = (uint)(Math.Ceiling(Math.E / epsilon));
            var depth = (uint)(Math.Ceiling(Math.Log(1 / delta)));
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
        }

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

            var hashKernel = Utils.HashKernel(data, this.Hash);
            var lower = hashKernel.LowerBaseHash;
            var upper = hashKernel.UpperBaseHash;

            // Increment count in each row.
            for (uint i = 0; i < this.Depth; i++)
            {
                this.Matrix[i][(lower + upper * i) % this.Width]++;
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

            var hashKernel = Utils.HashKernel(data, this.Hash);
            var lower = hashKernel.LowerBaseHash;
            var upper = hashKernel.UpperBaseHash;
            var count = UInt64.MaxValue;

            for (uint i = 0; i < this.Depth; i++)
            {
                count = Math.Min(count, this.Matrix[i][(lower + upper * i) % this.Width]);
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
                payload.WrittenSpan);
        }

        /// <summary>
        /// Reads a sketch written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The sketch that was written.</returns>
        public static CountMinSketch ReadFrom(Stream stream)
        {
            return Read(stream, null);
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
            return Read(stream, hash);
        }

        private static CountMinSketch Read(Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(stream, StructureId.CountMinSketch, out var hashId);
            var reader = new PayloadReader(payload);

            var epsilon = reader.ReadDouble();
            var delta = reader.ReadDouble();
            var width = reader.ReadUInt32();
            var depth = reader.ReadUInt32();
            var count = reader.ReadUInt64();

            // The stored dimensions are authoritative, not epsilon and delta. They are
            // what the sketch indexes by, and recomputing them would silently relocate
            // every cell if the sizing were ever adjusted. Epsilon and delta are kept
            // only because Epsilon() and Delta() report them.
            if (width == 0 || depth == 0)
            {
                throw new InvalidDataException(
                    $"Sketch has a {depth} by {width} matrix. A sketch with no rows " +
                    "reports every element as seen ulong.MaxValue times, and one with " +
                    "no columns divides by zero.");
            }

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
            };
        }

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
