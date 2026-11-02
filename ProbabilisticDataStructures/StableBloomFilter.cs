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
    /// <remarks>
    /// A filter of 2^27 cells or more places items with 64-bit hash values; a smaller one
    /// places with 32-bit values, as it always did. Every save is format version 8, which
    /// records the placement in a byte of its own and which libraries before this one
    /// cannot read; the stable filter has no item count to widen. Placing a
    /// large filter with 32-bit values crowds its first cells and raises its false
    /// positive rate, as it does for <see cref="BloomFilter"/>, where the details are. A
    /// filter read back keeps the scheme its payload was placed with, and
    /// <see cref="Reset"/> keeps it too.
    /// </remarks>
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
        /// <summary>
        /// How an item's hash becomes the cells it sets and tests.
        /// </summary>
        internal Placement Placement { get; private set; }

        private SeededRandom random;

        /// <summary>
        /// Where the generator stood when the filter was built, which is what
        /// <see cref="Reset"/> returns it to. It is stored with the filter because the
        /// position the generator has reached cannot say where it began: a filter read
        /// back from a payload would otherwise have nothing to return to.
        /// </summary>
        private ulong originalRandomState;

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
            : this(m, d, fpRate, hash, seed, null)
        {
        }

        /// <summary>
        /// Creates a filter as the public constructor does, placing items in its cells as
        /// <paramref name="placement"/> says, or as its size calls for when that is null.
        /// </summary>
        /// <remarks>
        /// The choice is made by size, and a filter large enough for it to matter is too
        /// large to build in numbers in a test. This lets a test have a small filter
        /// place items the way a large one does.
        /// </remarks>
        internal StableBloomFilter(uint m, byte d, double fpRate,
            Func<ReadOnlySpan<byte>, ulong>? hash, int? seed, Placement? placement)
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

            Buckets.CheckSize(m, d, nameof(m),
                $"A stable filter of {m:N0} cells of {d} bits",
                "Use fewer cells or narrower ones.");
            var cells = new Buckets(m, d);

            this.Hash = hash ?? Defaults.GetDefaultHashFunction();
            this.random = seed is null
                ? SeededRandom.Unpredictable()
                : new SeededRandom((ulong)seed.Value);
            this.originalRandomState = this.random.State;
            this.M = m;
            this.k = k;
            this.p = OptimalStableP(m, k, d, fpRate);
            this.Max = cells.MaxBucketValue();
            this.cells = cells;
            this.IndexBuffer = new uint[k];
            this.Placement = placement ?? BitPlacement.For(m);
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

            // The generator is left at its default, zero, and so is the state it
            // returns to on Reset. With p at zero its draws decay nothing, but they are
            // still drawn and stored, so a reset filter has to match a new one in them.
            return new StableBloomFilter
            {
                Hash = Defaults.GetDefaultHashFunction(),
                M = m,
                k = k,
                p = 0,
                Max = cells.MaxBucketValue(),
                cells = cells,
                IndexBuffer = new uint[k],
                Placement = BitPlacement.For(m),
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
            var positions = Positions.Of(data, this.Hash, this.Placement, this.M);

            // If any of the K cells are 0, then it's not a member.
            for (uint i = 0; i < this.k; i++)
            {
                if (this.cells.Get(positions[i]) == 0)
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

            var positions = Positions.Of(data, this.Hash, this.Placement, this.M);

            // Set the K cells to max.
            for (uint i = 0; i < this.k; i++)
            {
                this.cells.Set(positions[i], this.Max);
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
            var positions = Positions.Of(data, this.Hash, this.Placement, this.M);
            var member = true;

            // If any of the K cells are 0, then it's not a member.
            for (uint i = 0; i < this.k; i++)
            {
                this.IndexBuffer[i] = positions[i];
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
        /// Restores the Stable Bloom Filter to its original state: every cell empty and
        /// the random generator back where it stood when the filter was built. It
        /// returns the filter to allow for chaining.
        /// </summary>
        /// <remarks>
        /// A reset filter is a new one, byte for byte as <see cref="WriteTo"/> writes
        /// it, and it makes the choices a new one would: a stream added to it takes it
        /// exactly where the same stream took the filter the first time. For a seeded
        /// filter those are the choices its seed gives. For one built without a seed they
        /// are the unpredictable ones it was built with, so resetting it repeatedly
        /// repeats them rather than drawing new ones; build a new filter for that.
        /// <para>
        /// The starting state is stored with the filter, so this holds for one that has
        /// been written and read back. A payload written before it was recorded has no
        /// starting state to return to. A filter read from one takes the generator
        /// position it was read at as its starting state, so Reset returns it there,
        /// emptied, and writing it out and reading it back does not move that point.
        /// </para>
        /// <para>
        /// Reset empties the filter and does not reconfigure it, so a hash function
        /// supplied through <see cref="SetHash"/> stays.
        /// </para>
        /// </remarks>
        /// <returns>The reset bloom filter.</returns>
        public StableBloomFilter Reset()
        {
            this.cells.Reset();
            this.random = new SeededRandom(this.originalRandomState);
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
            BitPlacement.Write(payload, this.Placement);
            PersistenceFormat.WriteBuckets(payload, this.cells);
            payload.WriteUInt64(this.random.State);
            payload.WriteUInt64(this.originalRandomState);

            PersistenceFormat.Write(
                stream,
                StructureId.StableBloomFilter,
                PersistenceFormat.Identify(this.Hash),
                payload.WrittenSpan,
                PersistenceFormat.PlacementVersion);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The filter that was written.</returns>
        public static StableBloomFilter ReadFrom(Stream stream)
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
        public static StableBloomFilter ReadFrom(Stream stream, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, null, options);
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
        public static StableBloomFilter ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, hash, options);
        }

        private static StableBloomFilter Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.StableBloomFilter);
            var payload = PersistenceFormat.Read(
                stream, StructureId.StableBloomFilter, out var hashId, out var version, budget);
            var reader = new PayloadReader(payload, budget);

            var m = reader.ReadUInt32();
            var k = reader.ReadUInt32();
            var p = reader.ReadUInt32();
            // From the payload, never from m: a payload before version 8 of any size was
            // placed with 32-bit values, and a version 8 one says how it was placed.
            var placement = BitPlacement.Read(ref reader, version);
            var cells = PersistenceFormat.ReadBuckets(ref reader);

            // Version 1 predates the stored generator state. Such a filter resumes with
            // an unpredictable one: its cells are exactly right, and only the sequence of
            // cells it will decay next is unknowable, which is the most that can be
            // recovered from a payload that never recorded it.
            var random = version >= PersistenceFormat.RandomStateVersion
                ? new SeededRandom(reader.ReadUInt64())
                : SeededRandom.Unpredictable();

            // Versions before the original state was recorded have none to read. Such a
            // filter takes the position it was read at as its beginning, so Reset
            // returns it there and writing it out again records that same point: the
            // earliest the payload supports, rather than a seed it cannot name.
            var originalRandomState = version >= PersistenceFormat.StableOriginalStateVersion
                ? reader.ReadUInt64()
                : random.State;

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

            // The scratch buffer below is sized by the hash count the payload claims.
            budget.Charge(ReadBudget.ArrayOf(k, sizeof(uint)));

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
                originalRandomState = originalRandomState,
                // Version 8 is the version 5 layout with the placement byte, so the two
                // reads of the generator above already take it to have both states.
                Placement = placement,
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
