using System;
using System.IO;
using System.Security.Cryptography;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// BloomFilter implements a classic Bloom filter. A bloom filter has a non-zero
    /// probability of false positives and a zero probability of false negatives.
    /// </summary>
    public class BloomFilter : IFilter, IBinaryPersistable<BloomFilter>
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
        private uint count { get; set; }
        /// <summary>
        /// How an item's hash becomes the positions it sets and tests.
        /// </summary>
        internal Placement Placement { get; private set; }

        /// <summary>
        /// Creates a new Bloom filter optimized to store n items with a specified target
        /// false-positive rate.
        /// </summary>
        /// <param name="n">Number of items to store.</param>
        /// <param name="fpRate">Desired false positive rate.</param>
        /// <param name="hash">
        /// The hash function to use, or null for the default. Passing it here is the
        /// only way to have one hash cover everything the structure will ever hold:
        /// once anything has been added, the hash can no longer be replaced.
        /// </param>
        public BloomFilter(uint n, double fpRate, Func<ReadOnlySpan<byte>, ulong>? hash = null)
            : this(n, fpRate, hash, null)
        {
        }

        /// <summary>
        /// Creates a filter as the public constructor does, placing its bits as
        /// <paramref name="placement"/> says, or as its size calls for when that is null.
        /// </summary>
        /// <remarks>
        /// The choice is made by size, and a filter large enough for it to matter is too
        /// large to build in numbers in a test. This lets a test have a small filter
        /// place its bits the way a large one does.
        /// </remarks>
        internal BloomFilter(
            uint n, double fpRate, Func<ReadOnlySpan<byte>, ulong>? hash, Placement? placement)
        {
            Guard.ValidItemCount(n, nameof(n));
            Guard.ValidFalsePositiveRate(fpRate, nameof(fpRate));

            var m = Utils.OptimalM(n, fpRate);
            var k = Utils.OptimalK(fpRate);
            Buckets = new Buckets(m, 1);
            Hash = hash ?? Defaults.GetDefaultHashFunction();
            this.m = m;
            this.k = k;
            this.Placement = placement ?? BitPlacement.For(m);
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
        /// <returns></returns>
        public uint Count()
        {
            return this.count;
        }

        /// <summary>
        /// Returns the current estimated ratio of set bits.
        /// </summary>
        /// <returns>The current estimated ratio of set bits.</returns>
        public double EstimatedFillRatio()
        {
            return 1 - Math.Exp((-(double)this.count * (double)this.k) / (double)this.m);
        }

        /// <summary>
        /// Returns the ratio of set bits.
        /// </summary>
        /// <returns>The ratio of set bits.</returns>
        public double FillRatio()
        {
            uint sum = 0;
            for (uint i = 0; i < this.Buckets.count; i++)
            {
                sum += this.Buckets.Get(i);
            }
            return (double)sum / (double)this.m;
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
                this.Buckets.Set(positions[i], 1);
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
                this.Buckets.Set(idx, 1);
            }

            this.count++;
            return member;
        }

        /// <summary>
        /// Restores the Bloom filter to its original state. It returns the filter to
        /// allow for chaining.
        /// </summary>
        /// <returns>The reset bloom filter.</returns>
        public BloomFilter Reset()
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
            payload.WriteUInt32(this.count);
            payload.WriteUInt32(this.Buckets.count);
            payload.WriteByte(this.Buckets.BucketSize);
            payload.WriteBytes(this.Buckets.RawData);

            PersistenceFormat.Write(
                stream,
                StructureId.BloomFilter,
                PersistenceFormat.Identify(this.Hash),
                payload.WrittenSpan);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The filter that was written.</returns>
        public static BloomFilter ReadFrom(Stream stream)
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
        public static BloomFilter ReadFrom(Stream stream, ReadOptions options)
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
        public static BloomFilter ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
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
        public static BloomFilter ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, hash, options);
        }

        private static BloomFilter Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.BloomFilter);
            var payload = PersistenceFormat.Read(stream, StructureId.BloomFilter, out var hashId, budget);
            var reader = new PayloadReader(payload, budget);

            var m = reader.ReadUInt32();
            var k = reader.ReadUInt32();
            var count = reader.ReadUInt32();
            var bucketCount = reader.ReadUInt32();
            var bucketSize = reader.ReadByte();
            var data = reader.ReadBytes();
            reader.ExpectEnd();

            if (m == 0)
            {
                throw new InvalidDataException(
                    "Filter has a capacity of zero bits, which no filter this library " +
                    "builds can have; it divides by that on first use.");
            }

            if (bucketCount != m)
            {
                throw new InvalidDataException(
                    $"Filter has a capacity of {m} bits and {bucketCount} buckets to " +
                    "hold them, which do not describe the same filter.");
            }

            return new BloomFilter
            {
                Buckets = Buckets.Restore(bucketCount, bucketSize, data),
                m = m,
                k = k,
                count = count,
                Hash = PersistenceFormat.ResolveOrThrow(hashId, hash),
            };
        }

        /// <summary>
        /// Used only by <see cref="Read"/>, which sets every field itself. The public
        /// constructor derives m and k from a target false positive rate, which is not
        /// how a filter being restored gets them.
        /// </summary>
        private BloomFilter()
        {
            this.Buckets = null!;
        }

        /// <summary>
        /// Combines another filter into this one, so that it holds everything both held.
        /// </summary>
        /// <param name="other">The filter to combine in.</param>
        /// <returns>This filter, so calls can be chained.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// The two were built with different dimensions or different hash functions, so
        /// their contents do not describe the same positions.
        /// </exception>
        /// <remarks>
        /// The result is exactly what adding everything to one of them would have
        /// produced, so the false positive rate is that of a filter holding the union
        /// rather than either input's.
        /// <para>
        /// The item count becomes the sum, which overstates the union whenever the two
        /// shared elements. There is no way to know how many they shared, so the count
        /// of a merged filter is an upper bound.
        /// </para>
        /// </remarks>
        public BloomFilter Merge(BloomFilter other)
        {
            ArgumentNullException.ThrowIfNull(other);

            if (this.m != other.m || this.k != other.k)
            {
                throw new ArgumentException(
                    $"Cannot merge a filter of {other.m} bits and {other.k} hash " +
                    $"functions into one of {this.m} and {this.k}. The two describe " +
                    "different positions.", nameof(other));
            }

            Guard.SameHashFunction(this.Hash, other.Hash, nameof(other));

            this.Buckets.Union(other.Buckets);
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
            Guard.HashMayBeReplaced(this.count == 0, nameof(BloomFilter));

            this.Hash = h;
        }
    }
}
