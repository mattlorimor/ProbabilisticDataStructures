/*
Original work Copyright (c) 2013 zhenjl
Modified work Copyright (c) 2015 Tyler Treat
Modified work Copyright (c) 2015 Matthew Lorimor

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.
*/

using System;
using System.IO;
using System.Security.Cryptography;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// PartitionedBloomFilter implements a variation of a classic Bloom filter as
    /// described by Almeida, Baquero, Preguica, and Hutchison in Scalable Bloom
    /// Filters:
    ///
    /// http://gsd.di.uminho.pt/members/cbm/ps/dbloom.pdf
    ///
    /// This filter works by partitioning the M-sized bit array into k slices of
    /// size m = M/k bits. Each hash function produces an index over m for its
    /// respective slice. Thus, each element is described by exactly k bits, meaning
    /// the distribution of false positives is uniform across all elements.
    /// </summary>
    public class PartitionedBloomFilter : IFilter, IBinaryPersistable<PartitionedBloomFilter>
    {
        /// <summary>
        /// Partitioned filter data
        /// </summary>
        internal Buckets[] Partitions { get; set; }
        /// <summary>
        /// Hash algorithm
        /// </summary>
        internal Func<ReadOnlySpan<byte>, ulong> Hash { get; set; } = null!;
        /// <summary>
        /// Filter size (divided into k partitions)
        /// </summary>
        private uint M { get; set; }
        /// <summary>
        /// Number of hash functions (and partitions)
        /// </summary>
        private uint k { get; set; }
        /// <summary>
        /// Partition size (m / k)
        /// </summary>
        private uint S { get; set; }
        /// <summary>
        /// Number of items added
        /// </summary>
        private uint count { get; set; }

        /// <summary>
        /// Creates a new partitioned Bloom filter optimized to store n items with a
        /// specified target false-positive rate.
        /// </summary>
        /// <param name="n">Number of items</param>
        /// <param name="fpRate">Desired false-positive rate</param>
        /// <param name="hash">
        /// The hash function to use, or null for the default. Passing it here is the
        /// only way to have one hash cover everything the structure will ever hold:
        /// once anything has been added, the hash can no longer be replaced.
        /// </param>
        public PartitionedBloomFilter(uint n, double fpRate, Func<ReadOnlySpan<byte>, ulong>? hash = null)
        {
            Guard.ValidItemCount(n, nameof(n));
            Guard.ValidFalsePositiveRate(fpRate, nameof(fpRate));

            var m = Utils.OptimalM(n, fpRate);
            var k = Utils.OptimalK(fpRate);
            var partitions = new Buckets[k];
            var s = (uint)Math.Ceiling((double)m / (double)k);

            for (uint i = 0; i < k; i++)
            {
                partitions[i] = new Buckets(s, 1);
            }

            this.Partitions = partitions;
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();
            this.M = m;
            this.k = k;
            this.S = s;
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
        /// Returns the number of items in the filter.
        /// </summary>
        /// <returns>The number of items in the filter</returns>
        public uint Count()
        {
            return this.count;
        }

        /// <summary>
        /// Returns the current estimated ratio of set bits.
        /// </summary>
        /// <returns>The current estimated ratio of set bits</returns>
        public double EstimatedFillRatio()
        {
            return 1 - Math.Exp(-(double)this.count / (double)this.S);
        }

        /// <summary>
        /// Returns the average ratio of set bits across all partitions.
        /// </summary>
        /// <returns>The average ratio of set bitsacross all partitions</returns>
        public double FillRatio()
        {
            var t = (double)0;
            for (uint i = 0; i < this.k; i++)
            {
                uint sum = 0;
                for (uint j = 0; j < this.Partitions[i].count; j++)
                {
                    sum += this.Partitions[i].Get(j);
                }
                t += ((double)sum / (double)this.S);
            }
            return (double)t / (double)this.k;
        }

        /// <summary>
        /// Will test for membership of the data and returns true if it is a
        /// member, false if not. This is a probabilistic test, meaning there is a
        /// non-zero probability of false positives but a zero probability of false
        /// negatives. Due to the way the filter is partitioned, the probability of
        /// false positives is uniformly distributed across all elements.
        /// </summary>
        /// <param name="data">The data to test for</param>
        /// <returns>Whether or not the data was found</returns>
        public bool Test(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            var hashKernel = Utils.HashKernel(data, this.Hash);
            var lower = hashKernel.LowerBaseHash;
            var upper = hashKernel.UpperBaseHash;

            // If any of the K partiion bits are not set, then it's not a member.
            for (uint i = 0; i < this.k; i++)
            {
                if (this.Partitions[i].Get((lower + upper * i) % this.S) == 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Will add the data to the Bloom filter. It returns the filter to allow for
        /// chaining.
        /// </summary>
        /// <param name="data">The data to add</param>
        /// <returns>The PartitionedBloomFilter</returns>
        public IFilter Add(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            var hashKernel = Utils.HashKernel(data, this.Hash);
            var lower = hashKernel.LowerBaseHash;
            var upper = hashKernel.UpperBaseHash;

            // Set the K partition bits.
            for (uint i = 0; i < this.k; i++)
            {
                this.Partitions[i].Set((lower + upper * i) % this.S, 1);
            }

            this.count++;
            return this;
        }

        /// <summary>
        /// Equivalent to calling Test followed by Add. It returns true if the data is a
        /// member, false if not.
        /// </summary>
        /// <param name="data">The data to test for and add</param>
        /// <returns>
        /// Whether the data was present in the filter prior to adding it
        /// </returns>
        public bool TestAndAdd(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            var hashKernel = Utils.HashKernel(data, this.Hash);
            var lower = hashKernel.LowerBaseHash;
            var upper = hashKernel.UpperBaseHash;
            var member = true;

            // If any K partition bits are not set, then it's not a member.
            for (uint i = 0; i < this.k; i++)
            {
                var idx = (lower + upper * i) % this.S;
                if (this.Partitions[i].Get(idx) == 0)
                {
                    member = false;
                }
                this.Partitions[i].Set(idx, 1);
            }

            this.count++;
            return member;
        }

        /// <summary>
        /// Restores the Bloom filter to its original state. It returns the filter
        /// to allow for chaining.
        /// </summary>
        /// <returns>The PartitionedBloomFilter</returns>
        public PartitionedBloomFilter Reset()
        {
            foreach (var partition in this.Partitions)
            {
                partition.Reset();
            }

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
            payload.WriteUInt32(this.M);
            payload.WriteUInt32(this.k);
            payload.WriteUInt32(this.S);
            payload.WriteUInt32(this.count);

            payload.WriteUInt32((uint)this.Partitions.Length);
            foreach (var partition in this.Partitions)
            {
                PersistenceFormat.WriteBuckets(payload, partition);
            }

            PersistenceFormat.Write(
                stream,
                StructureId.PartitionedBloomFilter,
                PersistenceFormat.Identify(this.Hash),
                payload.WrittenSpan);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The filter that was written.</returns>
        public static PartitionedBloomFilter ReadFrom(Stream stream)
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
        public static PartitionedBloomFilter ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            return Read(stream, hash);
        }

        private static PartitionedBloomFilter Read(Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(stream, StructureId.PartitionedBloomFilter, out var hashId);
            var reader = new PayloadReader(payload);

            var m = reader.ReadUInt32();
            var k = reader.ReadUInt32();
            var s = reader.ReadUInt32();
            var count = reader.ReadUInt32();
            var partitionCount = reader.ReadUInt32();

            if (partitionCount == 0 || partitionCount > PersistenceFormat.MaxNestedCount)
            {
                throw new InvalidDataException(
                    $"Filter claims {partitionCount} partitions, which is not a filter " +
                    "this library builds.");
            }

            // One partition per hash function is what makes this filter partitioned;
            // any other number is a payload describing something else.
            if (partitionCount != k)
            {
                throw new InvalidDataException(
                    $"Filter has {k} hash functions and {partitionCount} partitions. A " +
                    "partitioned filter has one partition per hash function.");
            }

            var partitions = new Buckets[partitionCount];
            for (uint i = 0; i < partitionCount; i++)
            {
                partitions[i] = PersistenceFormat.ReadBuckets(ref reader);
                if (partitions[i].count != s)
                {
                    throw new InvalidDataException(
                        $"Partition {i} holds {partitions[i].count} buckets and the " +
                        $"filter's partitions are {s} wide.");
                }
            }

            reader.ExpectEnd();

            if (s == 0)
            {
                throw new InvalidDataException(
                    "Filter has partitions of zero bits, which it divides by on use.");
            }

            return new PartitionedBloomFilter
            {
                Partitions = partitions,
                M = m,
                k = k,
                S = s,
                count = count,
                Hash = PersistenceFormat.ResolveOrThrow(hashId, hash),
            };
        }

        /// <summary>
        /// Used only by <see cref="Read"/>, which sets every field itself.
        /// </summary>
        private PartitionedBloomFilter()
        {
            this.Partitions = null!;
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
        /// of a merged filter is an upper bound. Partitions are unioned one against its
        /// counterpart, so an element's bit stays in the partition its hash chose.
        /// </para>
        /// </remarks>
        public PartitionedBloomFilter Merge(PartitionedBloomFilter other)
        {
            ArgumentNullException.ThrowIfNull(other);

            if (this.M != other.M || this.k != other.k || this.S != other.S)
            {
                throw new ArgumentException(
                    $"Cannot merge a filter of {other.M} bits in {other.k} partitions of " +
                    $"{other.S} into one of {this.M} in {this.k} of {this.S}. The two " +
                    "describe different positions.", nameof(other));
            }

            Guard.SameHashFunction(this.Hash, other.Hash, nameof(other));

            for (int i = 0; i < this.Partitions.Length; i++)
            {
                this.Partitions[i].Union(other.Partitions[i]);
            }

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
            Guard.HashMayBeReplaced(this.count == 0, nameof(PartitionedBloomFilter));

            this.Hash = h;
        }
    }
}
