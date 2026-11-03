using System;
using System.IO;
using System.Security.Cryptography;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// CountingBloomFilter implements a Counting Bloom Filter as described by Fan,
    /// Cao, Almeida, and Broder in Summary Cache: A Scalable Wide-Area Web Cache
    /// Sharing Protocol:
    ///
    /// http://pages.cs.wisc.edu/~jussara/papers/00ton.pdf
    ///
    /// A Counting Bloom Filter (CBF) provides a way to remove elements by using an
    /// array of n-bit buckets. When an element is added, the respective buckets are
    /// incremented. To remove an element, the respective buckets are decremented. A
    /// query checks that each of the respective buckets are non-zero. Because CBFs
    /// allow elements to be removed, they introduce a non-zero probability of false
    /// negatives in addition to the possibility of false positives.
    ///
    /// That probability has one source, and it is unavoidable: removing an element
    /// that was never added. A filter cannot tell such a removal from a real one, so
    /// it decrements counters that other elements still depend on. Only remove what
    /// was added.
    ///
    /// Counters that reach their maximum are a separate matter. Such a counter has
    /// stopped tracking how many elements it stands for, so it is never decremented
    /// again and the elements covering it become permanently unremovable. This costs
    /// space rather than correctness, and is the reason removals and additions do not
    /// balance. Wider buckets saturate later.
    ///
    /// Counting Bloom Filters are useful for cases where elements are both added
    /// and removed from the data set. Since they use n-bit buckets, CBFs use
    /// roughly n-times more memory than traditional Bloom filters.
    /// </summary>
    /// <remarks>
    /// A filter of 2^27 counters or more places items with 64-bit hash values; a smaller
    /// one places with 32-bit values, as it always did. Every save is format version 8,
    /// which records the placement in a byte of its own and the item count in 64 bits, and
    /// which libraries before this one cannot read.
    /// Placing a large filter with 32-bit values crowds its first counters and raises its
    /// false positive rate, as it does for <see cref="BloomFilter"/>, where the details
    /// are. A filter read back keeps the scheme its payload was placed with, and the two
    /// schemes do not merge.
    /// </remarks>
    public class CountingBloomFilter : IFilter, IBinaryPersistable<CountingBloomFilter>
    {
        /// <summary>
        /// Filter data
        /// </summary>
        internal Buckets Buckets { get; set; }
        /// <summary>
        /// Hash algorithm
        /// </summary>
        private Func<ReadOnlySpan<byte>, ulong> Hash { get; set; } = null!;
        /// <summary>
        /// Filter size
        /// </summary>
        private uint m { get; set; }
        /// <summary>
        /// Number of hash functions
        /// </summary>
        private uint k { get; set; }
        /// <summary>
        /// Number of items added
        /// </summary>
        private ulong count { get; set; }
        /// <summary>
        /// Buffer used to cache indices
        /// </summary>
        private uint[] indexBuffer { get; set; }
        /// <summary>
        /// How an item's hash becomes the counters it moves and tests.
        /// </summary>
        internal Placement Placement { get; private set; }

        /// <summary>
        /// Creates a new Counting Bloom Filter optimized to store n-items with a
        /// specified target false-positive rate and bucket size. If you don't know how
        /// many bits to use for buckets, use NewDefaultCountingBloomFilter for a
        /// sensible default.
        /// </summary>
        /// <param name="n">Number of items to store.</param>
        /// <param name="b">Bucket size.</param>
        /// <param name="fpRate">Desired false positive rate.</param>
        /// <param name="hash">
        /// The hash function to use, or null for the default. Passing it here is the
        /// only way to have one hash cover everything the structure will ever hold:
        /// once anything has been added, the hash can no longer be replaced.
        /// </param>
        public CountingBloomFilter(uint n, byte b, double fpRate, Func<ReadOnlySpan<byte>, ulong>? hash = null)
            : this(n, b, fpRate, hash, null)
        {
        }

        /// <summary>
        /// Creates a filter as the public constructor does, placing its counters as
        /// <paramref name="placement"/> says, or as its size calls for when that is null.
        /// </summary>
        /// <remarks>
        /// The choice is made by size, and a filter large enough for it to matter is too
        /// large to build in numbers in a test. This lets a test have a small filter
        /// place its counters the way a large one does.
        /// </remarks>
        internal CountingBloomFilter(
            uint n, byte b, double fpRate, Func<ReadOnlySpan<byte>, ulong>? hash, Placement? placement)
        {
            Guard.ValidItemCount(n, nameof(n));
            Guard.ValidFalsePositiveRate(fpRate, nameof(fpRate));

            var m = Utils.OptimalM(n, fpRate);
            var k = Utils.OptimalK(fpRate);

            // The counters are one array: from four-bit counters up, fewer than the
            // largest filter a 32-bit count addresses fit it. Refused here against n,
            // which is what the caller chose, rather than by the array's own count.
            Buckets.CheckSize(m, b, nameof(n),
                $"Holding {n:N0} items at a false positive rate of {fpRate} needs " +
                $"{m:N0} counters of {b} bits",
                "Use fewer items, a looser false positive rate or narrower counters.");
            this.Buckets =  new Buckets(m, b);
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();
            this.m = m;
            this.k = k;
            this.indexBuffer = new uint[k];
            this.Placement = placement ?? BitPlacement.For(m);
        }

        /// <summary>
        /// Creates a new Counting Bloom Filter optimized to store n items with a
        /// specified target false-positive rate. Buckets are allocated four bits.
        /// </summary>
        /// <param name="n">Number of items to store.</param>
        /// <param name="fpRate">Desired false positive rate.</param>
        /// <returns>Default CountingBloomFilter</returns>
        public static CountingBloomFilter NewDefaultCountingBloomFilter(
            uint n,
            double fpRate)
        {
            return new CountingBloomFilter(n, 4, fpRate);
        }

        /// <summary>
        /// Returns the Bloom filter capacity, m.
        /// </summary>
        /// <returns>The Bloom filter capacity, m.</returns>
        public uint Capacity()
        {
            return this.m;
        }

        /// <summary>
        /// Returns the number of hash functions.
        /// </summary>
        /// <returns>The number of hash functions.</returns>
        public uint K()
        {
            return this.k;
        }

        /// <summary>
        /// Returns the number of items in the filter.
        /// </summary>
        /// <returns>The number of items added and not removed.</returns>
        /// <remarks>
        /// A 64-bit count, so that it crosses 2^32 rather than wrapping to zero, and comes
        /// back down across it as items are removed. Before the release after 6.2.0 it was
        /// a <c>uint</c>.
        /// <para>
        /// It stops at zero. <see cref="TestAndRemove(byte[])"/> returns true whenever
        /// every counter of the item is set, which an item that was never added can meet
        /// by chance and an item that was added can meet after it has been removed as
        /// often as it was added, because a counter that reached its maximum is never
        /// decremented. Each such removal takes one off the count, so after removing items
        /// that were never added the count can be lower than the number of items the
        /// filter holds: an undercount, where before it wrapped to 2^64 - 1. Remove only
        /// what was added and it is exact. A wrapped count saved by an earlier release
        /// is read back as it was stored.
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
            var positions = Positions.Of(data, this.Hash, this.Placement, this.m);

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
            var positions = Positions.Of(data, this.Hash, this.Placement, this.m);

            // Set the K bits.
            for (uint i = 0; i < this.k; i++)
            {
                this.Buckets.Increment(positions[i], 1);
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
            var positions = Positions.Of(data, this.Hash, this.Placement, this.m);
            var member = true;

            // If any of the K bits are not set, then it's not a member.
            for (uint i = 0; i < this.k; i++)
            {
                var idx = positions[i];
                if (this.Buckets.Get(idx) == 0)
                {
                    member = false;
                }
                this.Buckets.Increment(idx, 1);
            }

            this.count++;
            return member;
        }

        /// <summary>
        /// Will test for membership of the data and remove it from the filter if it
        /// exists. Returns true if the data was a member, false if not.
        /// </summary>
        /// <param name="data">The data to check for and remove.</param>
        /// <returns>Whether or not the data was in the filter before removal.</returns>
        public bool TestAndRemove(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            return this.TestAndRemove(data.AsSpan());
        }

        /// <inheritdoc cref="TestAndRemove(byte[])"/>
        public bool TestAndRemove(ReadOnlySpan<byte> data)
        {
            var positions = Positions.Of(data, this.Hash, this.Placement, this.m);
            var member = true;

            // Set the K bits.
            for (uint i = 0; i < this.k; i++)
            {
                this.indexBuffer[i] = positions[i];
                if (this.Buckets.Get(this.indexBuffer[i]) == 0)
                {
                    member = false;
                }
            }

            if (member)
            {
                // A counter that reached its maximum stopped counting, so it no longer
                // knows how many elements it stands for. Decrementing it would assume
                // it does, and the count it resumes from is too low by however many
                // increments were dropped -- enough of those and the counter reaches
                // zero while elements that need it are still present. Deleting without
                // introducing false negatives is the whole of what a counting filter
                // adds to a plain one, so a saturated counter is left alone instead.
                // The cost is that its space is never reclaimed, which is the lesser
                // of the two: the filter stays correct and merely gets fuller.
                var max = this.Buckets.MaxBucketValue();
                foreach (var idx in this.indexBuffer)
                {
                    if (this.Buckets.Get(idx) < max)
                    {
                        this.Buckets.Increment(idx, -1);
                    }
                }

                // The answer above is "every counter is set", which is not "this item
                // was added and is now gone": a saturated counter is never decremented,
                // so an item removed as often as it was added still answers true, and
                // so does a false positive. Taken below zero, a ulong is 2^64 - 1.
                if (this.count > 0)
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
        public CountingBloomFilter Reset()
        {
            this.Buckets.Reset();
            this.count = 0;
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
            payload.WriteUInt32(this.m);
            payload.WriteUInt32(this.k);
            BitPlacement.Write(payload, this.Placement);
            payload.WriteUInt64(this.count);
            PersistenceFormat.WriteBuckets(payload, this.Buckets);

            PersistenceFormat.Write(
                stream,
                StructureId.CountingBloomFilter,
                PersistenceFormat.Identify(this.Hash),
                payload.WrittenSpan,
                PersistenceFormat.PlacementVersion);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The filter that was written.</returns>
        public static CountingBloomFilter ReadFrom(Stream stream)
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
        public static CountingBloomFilter ReadFrom(Stream stream, ReadOptions options)
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
        public static CountingBloomFilter ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
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
        public static CountingBloomFilter ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, hash, options);
        }

        private static CountingBloomFilter Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.CountingBloomFilter);
            var payload = PersistenceFormat.Read(
                stream, StructureId.CountingBloomFilter, out var hashId, out var version, budget, Price,
                hash, PersistenceFormat.CheckResolvable);
            var reader = new PayloadReader(payload, budget);

            var m = reader.ReadUInt32();
            var k = reader.ReadUInt32();
            // From the payload, never from m: a version 1 payload of any size was placed
            // with 32-bit values, and a version 8 one says how it was placed.
            var placement = BitPlacement.Read(ref reader, version);
            var count = BitPlacement.ReadCount(ref reader, version);
            var buckets = PersistenceFormat.ReadBuckets(ref reader);
            reader.ExpectEnd();
            CheckBuckets(m, buckets.count);

            // The scratch buffer below is sized by the hash count the payload claims.
            budget.Charge(ScratchCost(k));

            return new CountingBloomFilter
            {
                Buckets = buckets,
                m = m,
                k = k,
                count = count,
                // Scratch space, sized from k rather than stored: it holds nothing
                // between calls.
                indexBuffer = new uint[k],
                Hash = PersistenceFormat.ResolveOrThrow(hashId, hash),
                Placement = placement,
            };
        }

        /// <summary>
        /// Prices what <see cref="Read"/> allocates beyond the envelope: the counters and
        /// the scratch buffer. See <see cref="PayloadPrice"/>.
        /// </summary>
        internal static void Price(ref PayloadReader reader, ushort version)
        {
            var m = reader.ReadUInt32();
            var k = reader.ReadUInt32();
            BitPlacement.Read(ref reader, version);
            BitPlacement.ReadCount(ref reader, version);
            var buckets = PersistenceFormat.PriceBuckets(ref reader);
            reader.ExpectEnd();
            CheckBuckets(m, buckets);
            reader.Budget.Price(ScratchCost(k));
        }

        private static void CheckBuckets(uint m, uint buckets)
        {
            if (m == 0 || buckets != m)
            {
                throw new InvalidDataException(
                    $"Filter has a capacity of {m} bits and {buckets} buckets to " +
                    "hold them, which do not describe the same filter.");
            }
        }

        /// <summary>The scratch buffer of a filter of <paramref name="k"/> hash functions.</summary>
        private static long ScratchCost(uint k) => ReadBudget.ArrayOf(k, sizeof(uint));

        /// <summary>
        /// Used only by <see cref="Read"/>, which sets every field itself.
        /// </summary>
        private CountingBloomFilter()
        {
            this.Buckets = null!;
            this.indexBuffer = null!;
        }

        /// <summary>
        /// Combines another filter into this one, adding its counters to their
        /// counterparts.
        /// </summary>
        /// <param name="other">The filter to combine in.</param>
        /// <returns>This filter, so calls can be chained.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// The two were built with different dimensions, different counter widths, or
        /// different hash functions, or their counters were placed by different schemes:
        /// a filter read from a payload written before format version 8 places with
        /// 32-bit values whatever its size, and a new filter of 2^27 counters or more with
        /// 64-bit values.
        /// </exception>
        /// <remarks>
        /// Counters are added rather than maxed, since a counting filter records how
        /// many elements landed on each position and a merged filter has to be
        /// removable from as many times as the two inputs together were.
        /// <para>
        /// Sums hold at the maximum a counter can carry. That matters more here than
        /// elsewhere: a counter which reaches its maximum is never decremented again,
        /// because it has stopped tracking what it stands for and resuming from the
        /// ceiling produces false negatives. Merging can carry a counter over that line
        /// when neither input was near it, so a merged filter can hold elements that
        /// are permanently unremovable when neither input did. Wider counters saturate
        /// later.
        /// </para>
        /// </remarks>
        public CountingBloomFilter Merge(CountingBloomFilter other)
        {
            ArgumentNullException.ThrowIfNull(other);

            if (this.m != other.m || this.k != other.k)
            {
                throw new ArgumentException(
                    $"Cannot merge a filter of {other.m} counters and {other.k} hash " +
                    $"functions into one of {this.m} and {this.k}. The two describe " +
                    "different positions.", nameof(other));
            }

            if (this.Buckets.BucketSize != other.Buckets.BucketSize)
            {
                throw new ArgumentException(
                    $"Cannot merge {other.Buckets.BucketSize}-bit counters into " +
                    $"{this.Buckets.BucketSize}-bit ones.", nameof(other));
            }

            BitPlacement.Same(this.Placement, other.Placement, nameof(other));
            Guard.SameHashFunction(this.Hash, other.Hash, nameof(other));

            this.Buckets.Add(other.Buckets);
            this.count += other.count;
            return this;
        }

        /// <summary>
        /// Sets the hashing function used in the filter.
        /// </summary>
        /// <param name="h">The hash function to use.</param>
        // TODO: Add SetHash to the IFilter interface?
        public void SetHash(Func<ReadOnlySpan<byte>, ulong> h)
        {
            ArgumentNullException.ThrowIfNull(h);
            Guard.HashMayBeReplaced(this.count == 0, nameof(CountingBloomFilter));

            this.Hash = h;
        }
    }
}
