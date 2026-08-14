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
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// ScalableBloomFilter implements a Scalable Bloom Filter as described by
    /// Almeida, Baquero, Preguica, and Hutchison in Scalable Bloom Filters:
    ///
    /// http://gsd.di.uminho.pt/members/cbm/ps/dbloom.pdf
    ///
    /// A Scalable Bloom Filter dynamically adapts to the number of elements in the
    /// data set while enforcing a tight upper bound on the false-positive rate.
    /// This works by adding Bloom filters with geometrically decreasing
    /// false-positive rates as filters become full. The tightening ratio, r,
    /// controls the filter growth. The compounded probability over the whole series
    /// converges to a target value, even accounting for an infinite series.
    ///
    /// Scalable Bloom Filters are useful for cases where the size of the data set
    /// isn't known a priori and memory constraints aren't of particular concern.
    /// For situations where memory is bounded, consider using Inverse or Stable
    /// Bloom Filters.
    /// </summary>
    public class ScalableBloomFilter : IFilter, IBinaryPersistable<ScalableBloomFilter>
    {
        /// <summary>
        /// Filters with geometrically decreasing error rates
        /// </summary>
        internal List<PartitionedBloomFilter> Filters { get; set; }
        /// <summary>
        /// Tightening ratio
        /// </summary>
        internal double R { get; set; }
        /// <summary>
        /// Target false-positive rate
        /// </summary>
        internal double FP { get; set; }
        /// <summary>
        /// Partition fill ratio
        /// </summary>
        private double P { get; set; }
        /// <summary>
        /// Filter size hint
        /// </summary>
        internal uint Hint { get; set; }
        /// <summary>
        /// The hash every contained filter is built with, or null for the default.
        /// Held because filters are added as the structure grows, long after the
        /// constructor has run.
        /// </summary>
        private Func<ReadOnlySpan<byte>, ulong>? Hash { get; set; }

        /// <summary>
        /// Whether anything has been added. A scalable filter that has grown holds more
        /// than the filter it started with; the first one having a count is the other
        /// way it can be non-empty.
        /// </summary>
        private bool IsEmpty => this.Filters.Count == 1 && this.Filters[0].Count() == 0;

        /// <summary>
        /// Creates a new Scalable Bloom Filter with the specified target false-positive
        /// rate and tightening ratio. Use NewDefaultScalableBloomFilter if you don't
        /// want to calculate all these parameters.
        /// </summary>
        /// <param name="hint"></param>
        /// <param name="fpRate"></param>
        /// <param name="r"></param>
        /// <param name="hash">
        /// The hash function to use, or null for the default. Passing it here is the
        /// only way to have one hash cover everything the structure will ever hold:
        /// once anything has been added, the hash can no longer be replaced.
        /// </param>
        public ScalableBloomFilter(uint hint, double fpRate, double r,
            Func<ReadOnlySpan<byte>, ulong>? hash = null)
        {
            Guard.ValidItemCount(hint, nameof(hint));
            Guard.ValidFalsePositiveRate(fpRate, nameof(fpRate));
            Guard.ValidTighteningRatio(r, nameof(r));

            this.Filters = new List<PartitionedBloomFilter>();
            this.R = r;
            this.FP = fpRate;
            this.P = Defaults.FILL_RATIO;
            this.Hint = hint;
            this.Hash = hash;

            this.AddFilter();
        }

        /// <summary>
        /// Creates a new Scalable Bloom Filter with the specified target false-positive
        /// rate and an optimal tightening ratio.
        /// </summary>
        /// <param name="fpRate"></param>
        public static ScalableBloomFilter NewDefaultScalableBloomFilter(double fpRate)
        {
            return new ScalableBloomFilter(10000, fpRate, 0.8);
        }

        /// <summary>
        /// Returns the current Scalable Bloom Filter capacity, which is the sum of the
        /// capacities for the contained series of Bloom filters.
        /// </summary>
        /// <returns>The current Scalable Bloom Filter capacity</returns>
        public uint Capacity()
        {
            var capacity = 0u;
            foreach (var filter in this.Filters)
            {
                capacity += filter.Capacity();
            }
            return capacity;
        }

        /// <summary>
        /// Returns the number of hash functions used in each Bloom filter.
        /// </summary>
        /// <returns>The number of hash functions used in each Bloom filter</returns>
        public uint K()
        {
            return this.Filters[0].K();
        }

        /// <summary>
        /// Returns the average ratio of set bits across every filter.
        /// </summary>
        /// <returns>The average ratio of set bits across every filter</returns>
        public double FillRatio()
        {
            var sum = 0.0;
            foreach (var filter in this.Filters)
            {
                sum += filter.FillRatio();
            }
            return (double)sum / this.Filters.Count();
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

            // Querying is made by testing for the presence in each filter.
            foreach (var filter in this.Filters)
            {
                if (filter.Test(data))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Add will add the data to the Bloom filter. It returns the filter to allow
        /// for chaining.
        /// </summary>
        /// <param name="data">The data to add</param>
        /// <returns>The ScalableBloomFilter</returns>
        public IFilter Add(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            var idx = this.Filters.Count() - 1;

            // If the last filter has reached its fill ratio, add a new one.
            if (this.Filters[idx].EstimatedFillRatio() >= this.P)
            {
                this.AddFilter();
                idx++;
            }

            this.Filters[idx].Add(data);
            return this;
        }

        /// <summary>
        /// Is equivalent to calling Test followed by Add. It returns true if the data
        /// is a member, false if not.
        /// </summary>
        /// <param name="data">The data to test for and add</param>
        /// <returns>Whether or not the data was present before adding it</returns>
        public bool TestAndAdd(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            var member = this.Test(data);
            this.Add(data);
            return member;
        }

        /// <summary>
        /// Writes this filter to a stream, in the format documented in FORMAT.md.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = new PayloadWriter();
            payload.WriteDouble(this.R);
            payload.WriteDouble(this.FP);
            payload.WriteDouble(this.P);
            payload.WriteUInt32(this.Hint);

            // Each contained filter keeps its own envelope rather than being flattened
            // into this one. It costs eighteen bytes each and means a filter added
            // later cannot disagree with this one about its own layout or its hash.
            payload.WriteUInt32((uint)this.Filters.Count);
            foreach (var filter in this.Filters)
            {
                PersistenceFormat.WriteNested(payload, filter);
            }

            PersistenceFormat.Write(
                stream,
                StructureId.ScalableBloomFilter,
                PersistenceFormat.Identify(this.Filters[0].Hash),
                payload.WrittenSpan);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The filter that was written.</returns>
        public static ScalableBloomFilter ReadFrom(Stream stream)
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
        public static ScalableBloomFilter ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            return Read(stream, hash);
        }

        private static ScalableBloomFilter Read(Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(stream, StructureId.ScalableBloomFilter, out _);
            var reader = new PayloadReader(payload);

            var r = reader.ReadDouble();
            var fp = reader.ReadDouble();
            var p = reader.ReadDouble();
            var hint = reader.ReadUInt32();

            var filterCount = reader.ReadUInt32();
            if (filterCount == 0)
            {
                throw new InvalidDataException(
                    "Filter holds no contained filters. A scalable filter always has at " +
                    "least the one it started with, and indexes it on every add.");
            }

            if (filterCount > PersistenceFormat.MaxNestedCount)
            {
                throw new InvalidDataException(
                    $"Filter claims {filterCount} contained filters, beyond anything " +
                    "this library builds.");
            }

            var filters = new List<PartitionedBloomFilter>((int)filterCount);
            for (uint i = 0; i < filterCount; i++)
            {
                filters.Add(PersistenceFormat.ReadNested<PartitionedBloomFilter>(ref reader, hash));
            }

            reader.ExpectEnd();

            return new ScalableBloomFilter
            {
                Filters = filters,
                R = r,
                FP = fp,
                P = p,
                Hint = hint,
            };
        }

        /// <summary>
        /// Used only by <see cref="Read"/>, which sets every field itself.
        /// </summary>
        private ScalableBloomFilter()
        {
            this.Filters = null!;
        }

        /// <summary>
        /// Sets the hashing function used in the filter.
        /// </summary>
        /// <param name="h">The hash function to use.</param>
        // TODO: Add SetHash to the IFilter interface?
        public void SetHash(Func<ReadOnlySpan<byte>, ulong> h)
        {
            ArgumentNullException.ThrowIfNull(h);

            // Checked here rather than left to the contained filters, so that the
            // failure names this filter and nothing is half converted before one of
            // them refuses.
            Guard.HashMayBeReplaced(this.IsEmpty, nameof(ScalableBloomFilter));

            this.Hash = h;
            foreach (var filter in this.Filters)
            {
                filter.SetHash(h);
            }
        }

        /// <summary>
        /// Restores the Bloom filter to its original state. It returns the filter to
        /// allow for chaining.
        /// </summary>
        /// <returns>The reset bloom filter.</returns>
        public ScalableBloomFilter Reset()
        {
            // Reset empties the filter; it does not reconfigure it. Every filter added
            // after the first takes its hash from Filters[0], so dropping the list
            // without carrying the hash across would quietly restore the default and
            // leave a caller who set their own hashing elsewhere than they left it.
            this.Hash = this.Filters[0].Hash;

            this.Filters = new List<PartitionedBloomFilter>();
            this.AddFilter();
            return this;
        }

        /// <summary>
        /// Adds a new Bloom filter with a restricted false-positive rate to the
        /// Scalable Bloom Filter
        /// </summary>
        internal void AddFilter()
        {
            var fpRate = this.FP * Math.Pow(this.R, this.Filters.Count());
            // The hash is passed to the filter rather than set on it afterwards: a
            // filter is only safe to re-hash while empty, and building it with the
            // right one removes the window entirely.
            var inherited = this.Filters.Count > 0 ? this.Filters[0].Hash : this.Hash;
            var p = new PartitionedBloomFilter(this.Hint, fpRate, inherited);
            this.Filters.Add(p);
        }
    }
}
