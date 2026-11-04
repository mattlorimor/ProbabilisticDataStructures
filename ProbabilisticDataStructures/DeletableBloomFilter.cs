using System;
using System.IO;
using System.Security.Cryptography;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// DeletableBloomFilter implements a Deletable Bloom Filter as described by
    /// Rothenberg, Macapuna, Verdi, Magalhaes in The Deletable Bloom filter - A new
    /// member of the Bloom family:
    ///
    /// http://arxiv.org/pdf/1005.0352.pdf
    ///
    /// A Deletable Bloom Filter compactly stores information on collisions when
    /// inserting elements. This information is used to determine if elements are
    /// deletable. This design enables false-negative-free deletions at a fraction
    /// of the cost in memory consumption.
    ///
    /// Deletable Bloom Filters are useful for cases which require removing elements
    /// but cannot allow false negatives. This means they can be safely swapped in
    /// place of traditional Bloom filters.
    /// </summary>
    /// <remarks>
    /// A filter whose data region is 2^27 bits or more places items with 64-bit hash
    /// values; a smaller one places with 32-bit values, as it always did. Every save is
    /// format version 8, which records the placement in a byte of its own and the item
    /// count in 64 bits, and which libraries before this one cannot read.
    /// Placing a large filter with 32-bit values crowds the start of its data region
    /// and raises its false positive rate, as it does for <see cref="BloomFilter"/>, where
    /// the details are. A filter read back keeps the scheme its payload was placed with.
    /// </remarks>
    public class DeletableBloomFilter : IFilter, IBinaryPersistable<DeletableBloomFilter>, IPayloadBody
    {
        /// <summary>
        /// Filter data
        /// </summary>
        internal Buckets Buckets { get; set; }
        /// <summary>
        /// Filter collision data
        /// </summary>
        internal Buckets Collisions { get; set; }
        /// <summary>
        /// Hash algorithm
        /// </summary>
        private Func<ReadOnlySpan<byte>, ulong> Hash { get; set; } = null!;
        /// <summary>
        /// Filter size
        /// </summary>
        private uint M { get; set; }
        /// <summary>
        /// Number of bits in a region
        /// </summary>
        private uint RegionSize { get; set; }
        /// <summary>
        /// Number of hash functions
        /// </summary>
        private uint k { get; set; }
        /// <summary>
        /// Number of items in the filter
        /// </summary>
        private ulong count { get; set; }
        /// <summary>
        /// Buffer used to cache indices
        /// </summary>
        private uint[] IndexBuffer { get; set; }
        /// <summary>
        /// How an item's hash becomes the positions it sets and tests in the data region.
        /// </summary>
        internal Placement Placement { get; private set; }

        /// <summary>
        /// NewDeletableBloomFilter creates a new DeletableBloomFilter optimized to store
        /// n items with a specified target false-positive rate. The r value determines
        /// the number of bits to use to store collision information. This controls the
        /// deletability of an element. Refer to the paper for selecting an optimal value.
        /// </summary>
        /// <param name="n">Number of items</param>
        /// <param name="r">Number of bits to use to store collision information</param>
        /// <param name="fpRate">Desired false positive rate</param>
        /// <param name="hash">
        /// The hash function to use, or null for the default. Passing it here is the
        /// only way to have one hash cover everything the structure will ever hold:
        /// once anything has been added, the hash can no longer be replaced.
        /// </param>
        public DeletableBloomFilter(uint n, uint r, double fpRate, Func<ReadOnlySpan<byte>, ulong>? hash = null)
            : this(n, r, fpRate, hash, null)
        {
        }

        /// <summary>
        /// Creates a filter as the public constructor does, placing its bits as
        /// <paramref name="placement"/> says, or as the size of its data region calls for
        /// when that is null.
        /// </summary>
        /// <remarks>
        /// The choice is made by size, and a filter large enough for it to matter is too
        /// large to build in numbers in a test. This lets a test have a small filter
        /// place its bits the way a large one does.
        /// </remarks>
        internal DeletableBloomFilter(
            uint n, uint r, double fpRate, Func<ReadOnlySpan<byte>, ulong>? hash, Placement? placement)
        {
            Guard.ValidItemCount(n, nameof(n));
            Guard.ValidFalsePositiveRate(fpRate, nameof(fpRate));

            var m = Utils.OptimalM(n, fpRate);
            var k = Utils.OptimalK(fpRate);

            // r shares the filter's bits with the data region, so it has to leave
            // room; a larger value underflows m - r and allocates enormously.
            Guard.ValidCollisionRegionCount(r, m, nameof(r));


            this.Buckets = new Buckets(m - r, 1);
            this.Collisions = new Buckets(r, 1);
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();
            this.M = m - r;
            // Rounded up, not down. The data region rarely divides evenly into r
            // regions, and rounding down leaves a remainder that maps to region
            // index r -- one past the last collision bucket. Rounding up keeps
            // every index inside the array: for RegionSize >= M/r, the largest
            // index (M - 1) / RegionSize is at most r(M - 1)/M, which is below r.
            // The final region is correspondingly shorter, which is the intent:
            // regions partition the data bits, so the last one holds what is left.
            //
            // Rounding up also keeps RegionSize non-zero. r is only required to be
            // smaller than m, so it may exceed the data region m - r, and dividing
            // down would then floor to zero and fault on the first Add.
            this.RegionSize = (this.M + r - 1) / r;
            this.k = k;
            this.IndexBuffer = new uint[k];
            this.Placement = placement ?? BitPlacement.For(this.M);
        }

        /// <summary>
        /// Returns the Bloom filter capacity, m.
        /// </summary>
        /// <returns>The Bloom filter capacity, m</returns>
        public uint Capacity()
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
        /// Returns the number of items added to the filter.
        /// </summary>
        /// <returns>The number of items added to the filter and not removed</returns>
        /// <remarks>
        /// A 64-bit count, so that it crosses 2^32 rather than wrapping to zero, and comes
        /// back down across it as items are removed. Before the release after 6.2.0 it was
        /// a <c>uint</c>.
        /// <para>
        /// A removal takes one off the count only when it cleared a bit. One that is
        /// refused, because every region of the item has collided, returns true and
        /// leaves the item a member, and leaves the count alone: the item is still held.
        /// </para>
        /// <para>
        /// The count also stops at zero. <see cref="TestAndRemove(byte[])"/> returns true
        /// whenever every bit of the item is set, which an item that was never added can
        /// meet by chance, and its removal clears bits that other items set. After
        /// removing items that were never added the count can therefore be lower than the
        /// number of items the filter holds: an undercount, where before it wrapped to
        /// 2^64 - 1. A wrapped count saved by an earlier release is read back as it was
        /// stored.
        /// </para>
        /// </remarks>
        public ulong Count()
        {
            return this.count;
        }

        /// <summary>
        /// Will test for membership of the data and returns true if it is a member,
        /// false if not. This is a probabilistic test, meaning there is a non-zero
        /// probability of false positives but a zero probability of false negatives.
        /// </summary>
        /// <param name="data">The data to search for.</param>
        /// <returns>Whether or not the data is maybe contained in the filter.</returns>
        public bool Test(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            return this.Test(data.AsSpan());
        }

        /// <inheritdoc cref="Test(byte[])"/>
        public bool Test(ReadOnlySpan<byte> data)
        {
            var positions = Positions.Of(data, this.Hash, this.Placement, this.M);

            // If any of the K bits are not set, then it's not a member.
            for (uint i = 0; i < this.k; i++)
            {
                if (this.Buckets.Get(positions[i]) == 0)
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
            var positions = Positions.Of(data, this.Hash, this.Placement, this.M);

            // Set the K bits.
            for (uint i = 0; i < this.k; i++)
            {
                var idx = positions[i];
                if (this.Buckets.Get(idx) != 0)
                {
                    // Collision, set corresponding region bit.
                    this.Collisions.Set(idx / this.RegionSize, 1);
                }
                else
                {
                    this.Buckets.Set(idx, 1);
                }
            }

            this.count++;
            return this;
        }

        /// <summary>
        /// Is equivalent to calling Test followed by Add. It returns true if the data is
        /// a member, false if not.
        /// </summary>
        /// <param name="data">The data to test for and add if it doesn't exist.</param>
        /// <returns>Whether or not the data was probably contained in the filter.</returns>
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

            // If any of the K bits are not set, then it's not a member.
            for (uint i = 0; i < this.k; i++)
            {
                var idx = positions[i];
                if (this.Buckets.Get(idx) == 0)
                {
                    member = false;
                }
                else
                {
                    // Collision, set corresponding region bit.
                    this.Collisions.Set(idx / this.RegionSize, 1);
                }
                this.Buckets.Set(idx, 1);
            }

            this.count++;
            return member;
        }

        /// <summary>
        /// Will test for membership of the data and remove it from the filter if it
        /// exists. Returns true if the data was a member, false if not.
        /// </summary>
        /// <param name="data">The data to test for and remove</param>
        /// <returns>Whether or not the data was a member before this call</returns>
        public bool TestAndRemove(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            return this.TestAndRemove(data.AsSpan());
        }

        /// <inheritdoc cref="TestAndRemove(byte[])"/>
        public bool TestAndRemove(ReadOnlySpan<byte> data)
        {
            var positions = Positions.Of(data, this.Hash, this.Placement, this.M);
            var member = true;

            // Set the K bits.
            for (uint i = 0; i < this.k; i++)
            {
                var idx = positions[i];
                this.IndexBuffer[i] = idx;
                if (this.Buckets.Get(idx) == 0)
                {
                    member = false;
                }
            }

            if (member)
            {
                var cleared = false;
                foreach (var idx in this.IndexBuffer)
                {
                    if (this.Collisions.Get(idx / this.RegionSize) == 0)
                    {
                        // Clear only bits located in collision-free zones.
                        this.Buckets.Set(idx, 0);
                        cleared = true;
                    }
                }

                // A deletion that cleared nothing, every one of the item's regions having
                // collided, removed nothing: the item is still a member and is still
                // counted. One that cleared a bit has taken the item out of the filter,
                // as far as this filter can, and the answer above is only "every bit is
                // set", which a false positive meets too, so the count stops at zero
                // rather than taking a ulong to 2^64 - 1.
                if (cleared && this.count > 0)
                {
                    this.count--;
                }
            }

            return member;
        }

        /// <summary>
        /// Restores the Bloom filter to its original state. It returns the filter to
        /// allow for chaining.
        /// </summary>
        /// <returns>The reset bloom filter.</returns>
        public DeletableBloomFilter Reset()
        {
            this.Buckets.Reset();
            this.Collisions.Reset();
            this.count = 0;
            return this;
        }

        /// <summary>
        /// Writes this filter to a stream, in the format documented in FORMAT.md.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream) => PersistenceFormat.Write(stream, this);

        EnvelopeHeader IPayloadBody.WritePayload(PayloadWriter payload)
        {
            payload.WriteUInt32(this.M);
            payload.WriteUInt32(this.k);
            BitPlacement.Write(payload, this.Placement);
            payload.WriteUInt64(this.count);
            PersistenceFormat.WriteBuckets(payload, this.Buckets);
            PersistenceFormat.WriteBuckets(payload, this.Collisions);

            return new(
                StructureId.DeletableBloomFilter, PersistenceFormat.Identify(this.Hash),
                PersistenceFormat.PlacementVersion);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The filter that was written.</returns>
        public static DeletableBloomFilter ReadFrom(Stream stream)
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
        public static DeletableBloomFilter ReadFrom(Stream stream, ReadOptions options)
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
        public static DeletableBloomFilter ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
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
        public static DeletableBloomFilter ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, hash, options);
        }

        private static DeletableBloomFilter Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.DeletableBloomFilter);
            return PersistenceFormat.Read(
                stream, StructureId.DeletableBloomFilter, budget, Price, hash, PersistenceFormat.CheckResolvable,
                ReadPayload);
        }

        /// <summary>
        /// Reads the payload of the envelope <c>Read</c> opened, once the envelope has
        /// been checked. See <see cref="PayloadParse{T}"/>.
        /// </summary>
        private static DeletableBloomFilter ReadPayload(
            ref PayloadReader reader, ushort version, HashId hashId,
            Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            var budget = reader.Budget;

            var m = reader.ReadUInt32();
            var k = reader.ReadUInt32();
            // From the payload, never from m: a version 1 payload of any size was placed
            // with 32-bit values, and a version 8 one says how it was placed.
            var placement = BitPlacement.Read(ref reader, version);
            var count = BitPlacement.ReadCount(ref reader, version);
            var buckets = PersistenceFormat.ReadBuckets(ref reader);
            var collisions = PersistenceFormat.ReadBuckets(ref reader);
            reader.ExpectEnd();
            CheckRegions(m, buckets.count, collisions.count);
            PersistenceFormat.CheckHashCount(k);

            // The scratch buffer below is sized by the hash count the payload claims.
            budget.Charge(ScratchCost(k));

            return new DeletableBloomFilter
            {
                Buckets = buckets,
                Collisions = collisions,
                M = m,
                k = k,
                count = count,
                // Derived rather than stored, deliberately. This is a function of the
                // filter's dimensions, and that function was wrong before 3.1.0:
                // storing what it computed would have carried the defect into stored
                // data, where no later fix could reach it.
                RegionSize = (m + collisions.count - 1) / collisions.count,
                IndexBuffer = new uint[k],
                Hash = PersistenceFormat.ResolveOrThrow(hashId, hash),
                Placement = placement,
            };
        }

        /// <summary>
        /// Prices what <see cref="Read"/> allocates beyond the envelope: the two bucket
        /// regions and the scratch buffer. See <see cref="PayloadPrice"/>.
        /// </summary>
        internal static void Price(ref PayloadReader reader, ushort version)
        {
            var m = reader.ReadUInt32();
            var k = reader.ReadUInt32();
            BitPlacement.Read(ref reader, version);
            BitPlacement.ReadCount(ref reader, version);
            var buckets = PersistenceFormat.PriceBuckets(ref reader);
            var collisions = PersistenceFormat.PriceBuckets(ref reader);
            reader.ExpectEnd();
            CheckRegions(m, buckets, collisions);
            PersistenceFormat.CheckHashCount(k);
            reader.Budget.Price(ScratchCost(k));
        }

        internal static void CheckRegions(uint m, uint buckets, uint collisions)
        {
            if (m == 0 || buckets != m)
            {
                throw new InvalidDataException(
                    $"Filter has a data region of {m} bits and {buckets} buckets " +
                    "to hold them, which do not describe the same filter.");
            }

            if (collisions == 0)
            {
                throw new InvalidDataException(
                    "Filter has no collision regions, which is what makes its deletions " +
                    "safe; it divides by that count on first use.");
            }

            // The two regions are the halves of one filter of m bits, and sizing refuses an
            // m past uint.MaxValue, so no filter built here has regions that come to more.
            // The reader derives a region's size as (m + collisions - 1) / collisions in 32
            // bits, so a sum past 2^32 wraps the numerator, makes the size zero, and the
            // first Add that finds a bit set divides by it. Checked here, where Read and
            // Price both pass, so the refusal comes before either region is allocated.
            if ((ulong)m + collisions > uint.MaxValue)
            {
                throw new InvalidDataException(
                    $"Filter has a data region of {m:N0} bits and {collisions:N0} collision " +
                    $"regions, which together are {(ulong)m + collisions:N0} bits. A filter " +
                    $"addresses at most {uint.MaxValue:N0}, so no filter this library builds " +
                    "is this large; its region size would wrap to zero, and it divides by " +
                    "that on first use.");
            }
        }

        /// <summary>The scratch buffer of a filter of <paramref name="k"/> hash functions.</summary>
        private static long ScratchCost(uint k) => ReadBudget.ArrayOf(k, sizeof(uint));

        /// <summary>
        /// Used only by <see cref="Read"/>, which sets every field itself.
        /// </summary>
        private DeletableBloomFilter()
        {
            this.Buckets = null!;
            this.Collisions = null!;
            this.IndexBuffer = null!;
        }

        /// <summary>
        /// Sets the hashing function used in the filter.
        /// </summary>
        /// <param name="h">The hash function to use.</param>
        // TODO: Add SetHash to the IFilter interface?
        public void SetHash(Func<ReadOnlySpan<byte>, ulong> h)
        {
            ArgumentNullException.ThrowIfNull(h);
            Guard.HashMayBeReplaced(this.count == 0, nameof(DeletableBloomFilter));

            this.Hash = h;
        }
    }
}
