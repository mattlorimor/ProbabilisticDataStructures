using System;
using System.IO;
using System.Security.Cryptography;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// StableBloomFilter implements a Stable Bloom Filter as described by Deng and
    /// Rafiei in Approximately Detecting Duplicates for Streaming Data using Stable
    /// Bloom Filters:
    ///
    /// http://webdocs.cs.ualberta.ca/~drafiei/papers/DupDet06Sigmod.pdf
    ///
    /// A Stable Bloom Filter (SBF) continuously evicts stale information so that it
    /// has room for more recent elements. Like traditional Bloom filters, an SBF
    /// has a non-zero probability of false positives, which is controlled by
    /// several parameters. Unlike the classic Bloom filter, an SBF has a tight
    /// upper bound on the rate of false positives while introducing a non-zero rate
    /// of false negatives. The false-positive rate of a classic Bloom filter
    /// eventually reaches 1, after which all queries result in a false positive.
    /// The stable-point property of an SBF means the false-positive rate
    /// asymptotically approaches a configurable fixed constant. A classic Bloom
    /// filter is actually a special case of SBF where the eviction rate is zero, so
    /// this package provides support for them as well.
    ///
    /// Stable Bloom Filters are useful for cases where the size of the data set
    /// isn't known a priori, which is a requirement for traditional Bloom filters,
    /// and memory is bounded.  For example, an SBF can be used to deduplicate
    /// events from an unbounded event stream with a specified upper bound on false
    /// positives and minimal false negatives.
    /// </summary>
    public class StableBloomFilter : IFilter, IBinaryPersistable<StableBloomFilter>
    {
        /// <summary>
        /// Filter data
        /// </summary>
        internal Buckets cells { get; set; } = null!;
        /// <summary>
        /// Hash algorightm
        /// </summary>
        private Func<ReadOnlySpan<byte>, ulong> Hash { get; set; } = null!;
        /// <summary>
        /// Number of cells
        /// </summary>
        internal uint M { get; set; }
        /// <summary>
        /// Number of cells to decrement
        /// </summary>
        private uint p { get; set; }
        /// <summary>
        /// Number of hash functions
        /// </summary>
        private uint k { get; set; }
        /// <summary>
        /// Cell max value
        /// </summary>
        internal byte Max { get; set; }
        /// <summary>
        /// Buffer used to cache indices
        /// </summary>
        private uint[] IndexBuffer { get; set; } = null!;

        private SeededRandom random;

        /// <summary>
        /// Whether any cell has been set, which is what decides if the hash can
        /// still be replaced. Derived rather than tracked, so that a filter read
        /// back from a payload answers this the way the one that wrote it would.
        /// </summary>
        private bool IsEmpty
        {
            get
            {
                for (uint i = 0; i < this.cells.count; i++)
                {
                    if (this.cells.Get(i) != 0)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Used by NewUnstableBloomFilter, which populates every member through an
        /// object initializer. The compiler cannot see that, so the members it sets
        /// are marked null-forgiving at their declarations.
        /// </summary>
        private StableBloomFilter() { }

        /// <summary>
        /// Creates a new Stable Bloom Filter with m cells and d bits allocated per cell
        /// optimized for the target false-positive rate. Use NewDefaultStableFilter if
        /// you don't want to calculate d.
        /// </summary>
        /// <param name="m">Number of cells to decrement</param>
        /// <param name="d">Bits per cell</param>
        /// <param name="fpRate">Desired false-positive rate</param>
        /// <param name="hash">
        /// The hash function to use, or null for the default. Passing it here is the
        /// only way to have one hash cover everything the structure will ever hold:
        /// once anything has been added, the hash can no longer be replaced.
        /// </param>
        /// <param name="seed">
        /// A seed for the random choices this filter makes, or null to seed it
        /// unpredictably. Supplying one makes the filter reproducible, which is what
        /// makes its behavior assertable rather than only describable.
        /// </param>
        public StableBloomFilter(uint m, byte d, double fpRate,
            Func<ReadOnlySpan<byte>, ulong>? hash = null, int? seed = null)
        {
            Guard.ValidItemCount(m, nameof(m));
            Guard.ValidFalsePositiveRate(fpRate, nameof(fpRate));

            var k = Utils.OptimalK(fpRate) / 2;
            if (k > m)
            {
                k = m;
            }
            else if (k <= 0)
            {
                k = 1;
            }

            var cells = new Buckets(m, d);

            this.Hash = hash ?? Defaults.GetDefaultHashFunction();
            this.random = seed is null
                ? SeededRandom.Unpredictable()
                : new SeededRandom((ulong)seed.Value);
            this.M = m;
            this.k = k;
            this.p = OptimalStableP(m, k, d, fpRate);
            this.Max = cells.MaxBucketValue();
            this.cells = cells;
            this.IndexBuffer = new uint[k];
        }

        /// <summary>
        /// Creates a new Stable Bloom Filter with m 1-bit cells and which is optimized
        /// for cases where there is no prior knowledge of the input data stream while
        /// maintaining an upper bound using the provided rate of false positives.
        /// </summary>
        /// <param name="m">Number of cells to decrement</param>
        /// <param name="fpRate">Desired false-positive rate</param>
        public static StableBloomFilter NewDefaultStableBloomFilter(uint m, double fpRate)
        {
            return new StableBloomFilter(m, 1, fpRate);
        }

        /// <summary>
        /// Creates a new special case of Stable Bloom Filter which is a traditional
        /// Bloom filter with m bits and an optimal number of hash functions for the
        /// target false-positive rate. Unlike the stable variant, data is not evicted
        /// and a cell contains a maximum of 1 hash value.
        /// </summary>
        /// <param name="m">Number of cells to decrement</param>
        /// <param name="fpRate">Desired false-positive rate</param>
        /// <returns></returns>
        public static StableBloomFilter NewUnstableBloomFilter(uint m, double fpRate)
        {
            var cells = new Buckets(m, 1);
            var k = Utils.OptimalK(fpRate);

            return new StableBloomFilter
            {
                Hash = Defaults.GetDefaultHashFunction(),
                M = m,
                k = k,
                p = 0,
                Max = cells.MaxBucketValue(),
                cells = cells,
                IndexBuffer = new uint[k]
            };
        }

        /// <summary>
        /// Returns the number of cells in the Stable Bloom Filter.
        /// </summary>
        /// <returns>The number of cells in the Stable Bloom Filter</returns>
        public uint Cells()
        {
            return this.M;
        }

        /// <summary>
        /// Returns the number of hash functions.
        /// </summary>
        /// <returns>The number of hash functions</returns>
        public uint K()
        {
            return this.k;
        }

        /// <summary>
        /// Returns the number of cells decremented on ever add.
        /// </summary>
        /// <returns></returns>
        public uint P()
        {
            return this.p;
        }

        /// <summary>
        /// Returns the limit of the expected fraction of zeros in the Stable Bloom
        /// Filter when the number of iterations goes to infinity. When this limit is
        /// reached, the Stable Bloom Filter is considered stable.
        /// </summary>
        /// <returns>
        /// The limit of the expected fraction of zeros in the SBF as the number of
        /// iterations approaches infinity.
        /// </returns>
        public double StablePoint()
        {
            var subDenom = this.p * (1.0 / (double)this.k - 1.0 / (double)this.M);
            var denom = 1.0 + 1.0 / (double)subDenom;
            var b = 1.0 / denom;

            return Math.Pow(b, this.Max);
        }

        /// <summary>
        /// Returns the upper bound on false positives when the filter has become stable.
        /// </summary>
        /// <returns>
        /// The upper bound on false positives when the filter has become stable
        /// </returns>
        public double FalsePositiveRate()
        {
            return Math.Pow(1 - this.StablePoint(), this.k);
        }

        /// <summary>
        /// Will test for membership of the data and returns true if it is a member,
        /// false if not. This is a probabilistic test, meaning there is a non-zero
        /// probability of false positives but a zero probability of false negatives.
        /// </summary>
        /// <param name="data">The data to search for.</param>
        /// <returns>Whether or not the data is maybe contained in the filter</returns>
        public bool Test(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            return this.Test(data.AsSpan());
        }

        /// <inheritdoc cref="Test(byte[])"/>
        public bool Test(ReadOnlySpan<byte> data)
        {
            var hashKernel = Utils.HashKernel(data, this.Hash);
            var lower = hashKernel.LowerBaseHash;
            var upper = hashKernel.UpperBaseHash;

            // If any of the K cells are 0, then it's not a member.
            for (uint i = 0; i < this.k; i++)
            {
                if (this.cells.Get((lower + upper * i) % this.M) == 0)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Will add the data to the Bloom filter. It returns the filter to allow
        /// for chaining.
        /// </summary>
        /// <param name="data">The data to add.</param>
        /// <returns>The filter.</returns>
        public IFilter Add(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            return this.Add(data.AsSpan());
        }

        /// <inheritdoc cref="Add(byte[])"/>
        public IFilter Add(ReadOnlySpan<byte> data)
        {
            // Randomly decrement p cells to make room for new elements.
            this.Decrement();

            var hashKernel = Utils.HashKernel(data, this.Hash);
            var lower = hashKernel.LowerBaseHash;
            var upper = hashKernel.UpperBaseHash;

            // Set the K cells to max.
            for (uint i = 0; i < this.k; i++)
            {
                this.cells.Set((lower + upper * i) % this.M, this.Max);
            }

            return this;
        }

        /// <summary>
        /// Equivalent to calling Test followed by Add. It returns true if the data is a
        /// member, false if not.
        /// </summary>
        /// <param name="data">The data to test for and add.</param>
        /// <returns>Whether or not the data was present before adding.</returns>
        public bool TestAndAdd(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            return this.TestAndAdd(data.AsSpan());
        }

        /// <inheritdoc cref="TestAndAdd(byte[])"/>
        public bool TestAndAdd(ReadOnlySpan<byte> data)
        {
            var hashKernel = Utils.HashKernel(data, this.Hash);
            var lower = hashKernel.LowerBaseHash;
            var upper = hashKernel.UpperBaseHash;
            var member = true;

            // If any of the K cells are 0, then it's not a member.
            for (uint i = 0; i < this.k; i++)
            {
                this.IndexBuffer[i] = (lower + upper * i) % this.M;
                if (this.cells.Get(this.IndexBuffer[i]) == 0)
                {
                    member = false;
                }
            }

            // Randomly decrement p cells to make room for new elements.
            this.Decrement();

            // Set the K cells to max.
            foreach (var idx in this.IndexBuffer)
            {
                this.cells.Set(idx, this.Max);
            }

            return member;
        }

        /// <summary>
        /// Restores the Stable Bloom Filter to its original state. It returns the filter to
        /// allow for chaining.
        /// </summary>
        /// <returns>The reset bloom filter.</returns>
        public StableBloomFilter Reset()
        {
            this.cells.Reset();
            return this;
        }

        /// <summary>
        /// Writes this filter to a stream, in the format documented in FORMAT.md.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = new PayloadWriter();
            payload.WriteUInt32(this.M);
            payload.WriteUInt32(this.k);
            payload.WriteUInt32(this.p);
            PersistenceFormat.WriteBuckets(payload, this.cells);
            payload.WriteUInt64(this.random.State);

            PersistenceFormat.Write(
                stream,
                StructureId.StableBloomFilter,
                PersistenceFormat.Identify(this.Hash),
                payload.WrittenSpan,
                PersistenceFormat.RandomStateVersion);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The filter that was written.</returns>
        public static StableBloomFilter ReadFrom(Stream stream)
        {
            return Read(stream, null);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>, using the supplied hash
        /// function rather than the one named in the payload.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function the filter was written with.</param>
        /// <returns>The filter that was written.</returns>
        public static StableBloomFilter ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            return Read(stream, hash);
        }

        private static StableBloomFilter Read(Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(
                stream, StructureId.StableBloomFilter, out var hashId, out var version);
            var reader = new PayloadReader(payload);

            var m = reader.ReadUInt32();
            var k = reader.ReadUInt32();
            var p = reader.ReadUInt32();
            var cells = PersistenceFormat.ReadBuckets(ref reader);

            // Version 1 predates the stored generator state. Such a filter resumes with
            // an unpredictable one: its cells are exactly right, and only the sequence of
            // cells it will decay next is unknowable, which is the most that can be
            // recovered from a payload that never recorded it.
            var random = version >= PersistenceFormat.RandomStateVersion
                ? new SeededRandom(reader.ReadUInt64())
                : SeededRandom.Unpredictable();

            reader.ExpectEnd();

            if (m == 0 || cells.count != m)
            {
                throw new InvalidDataException(
                    $"Filter has {m} cells by its own account and {cells.count} to hold " +
                    "them, which do not describe the same filter.");
            }

            if (k == 0)
            {
                throw new InvalidDataException(
                    "Filter has no hash functions, so it would set no cells and test none.");
            }

            return new StableBloomFilter
            {
                cells = cells,
                M = m,
                k = k,
                p = p,
                // Follows from the cell width, which the cells carry.
                Max = cells.MaxBucketValue(),
                IndexBuffer = new uint[k],
                Hash = PersistenceFormat.ResolveOrThrow(hashId, hash),
                random = random,
            };
        }

        /// <summary>
        /// Sets the hashing function used in the filter.
        /// </summary>
        /// <param name="h">The hash function to use.</param>
        // TODO: Add SetHash to the IFilter interface?
        public void SetHash(Func<ReadOnlySpan<byte>, ulong> h)
        {
            ArgumentNullException.ThrowIfNull(h);
            Guard.HashMayBeReplaced(this.IsEmpty, nameof(StableBloomFilter));

            this.Hash = h;
        }

        /// <summary>
        /// Will decrement a random cell and (p-1) adjacent cells by 1. This is faster
        /// than generating p random numbers. Although the processes of picking the p
        /// cells are not independent, each cell has a probability of p/m for being
        /// picked at each iteration, which means the properties still hold.
        /// </summary>
        private void Decrement()
        {
            var r = this.random.NextBelow(this.M);
            for (uint i = 0; i < this.p; i++)
            {
                var idx = (r + i) % this.M;
                this.cells.Increment((uint)idx, -1);
            }
        }

        /// <summary>
        /// Returns the optimal number of cells to decrement, p, per iteration for the
        /// provided parameters of an SBF.
        /// </summary>
        /// <param name="m">Number of cells</param>
        /// <param name="k">Number of hash functions</param>
        /// <param name="d">Bits per cell</param>
        /// <param name="fpRate">Desired false-positive rate</param>
        /// <returns>Optimal number of cells to decrement</returns>
        private static uint OptimalStableP(uint m, uint k, byte d, double fpRate)
        {
            var max = Math.Pow(2, d) - 1;
            var subDenom = Math.Pow(1 - Math.Pow(fpRate, 1.0 / k), 1.0 / max);
            var denom = (1.0 / subDenom - 1) * (1.0 / k - 1.0 / m);

            var p = 1.0 / denom;
            if (p <= 0)
            {
                p = 1;
            }

            return (uint)p;
        }
    }
}
