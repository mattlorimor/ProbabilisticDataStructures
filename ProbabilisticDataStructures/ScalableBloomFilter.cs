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
    /// <para>
    /// The growth has a ceiling, and it is not memory. The i-th filter is built at
    /// <c>fpRate * r^i</c>, and a Bloom filter sizes its hash count from the reciprocal
    /// of its rate, which is not a number once the rate is below
    /// <c>1 / double.MaxValue</c>, about 5.6e-309. So the filter holds at most
    /// <c>floor(ln(fpRate * double.MaxValue) / ln(1 / r)) + 1</c> filters: 3,161 at a
    /// rate of 1% and a ratio of 0.8, 6,693 at a ratio of 0.9. Each holds about as many
    /// items as the hint, so at a hint of a thousand that is 3.16 million items. An
    /// <see cref="Add(ReadOnlySpan{byte})"/> that needs a filter beyond the ceiling
    /// throws an <see cref="InvalidOperationException"/> before it changes anything, and
    /// the filter goes on answering queries and can be written. A larger hint raises the
    /// number of items the ceiling allows in proportion; <see cref="BloomFilter64"/>
    /// sizes one filter in 64 bits and has no ceiling of this kind.
    /// </para>
    /// </summary>
    public class ScalableBloomFilter : IFilter, IBinaryPersistable<ScalableBloomFilter>
    {
        /// <summary>
        /// Filters with geometrically decreasing error rates
        /// </summary>
        internal List<PartitionedBloomFilter> Filters { get; set; }

        /// <summary>
        /// The most filters a scalable filter can hold: they are a list, and a list
        /// holds at most <see cref="Array.MaxLength"/>.
        /// </summary>
        /// <remarks>
        /// There is no constructor argument for this to check. A filter grows by adding
        /// filters, each at a rate tightened by r, and with r near one it can go on
        /// adding them until the list itself refuses. The reader accepts as many as
        /// the list holds, so whatever a filter grew to reads back. It used to stop at
        /// a million, which a filter with a hint of one, a rate of one half and a ratio
        /// of 0.999999 passes after about 1.7 million adds.
        /// </remarks>
        internal static int MaxFilters => Array.MaxLength;

        /// <summary>
        /// How many filters a scalable filter with these rates can hold: the i-th is built
        /// at <c>fpRate * r^i</c>, and that is possible while the rate's reciprocal is a
        /// number. Never more than <see cref="MaxFilters"/>.
        /// </summary>
        /// <remarks>
        /// Found by the closed form, <c>floor(ln(fpRate * double.MaxValue) / ln(1 / r))
        /// + 1</c>, and then checked against the arithmetic <see cref="AddFilter"/> uses,
        /// one step either way, so that a rate which rounds across the edge cannot make
        /// the two disagree about whether a filter can be built.
        /// </remarks>
        internal static int FilterCeiling(double fpRate, double r)
        {
            static bool CanBuild(double rate) => rate > 0 && !double.IsInfinity(1 / rate);

            var estimate = Math.Floor(Math.Log(fpRate * double.MaxValue) / -Math.Log(r));
            var last = (int)Math.Clamp(estimate, -1, MaxFilters - 1);

            while (last >= 0 && !CanBuild(fpRate * Math.Pow(r, last)))
            {
                last--;
            }

            while (last < MaxFilters - 1 && CanBuild(fpRate * Math.Pow(r, last + 1)))
            {
                last++;
            }

            return last + 1;
        }
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

            // Not even the first filter can be built at a rate whose reciprocal is not a
            // number, and that arrived as an OverflowException from inside the sizing.
            if (FilterCeiling(fpRate, r) == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(fpRate), fpRate,
                    $"A false positive rate of {fpRate} is below 1 / double.MaxValue, about " +
                    "5.6e-309, so its reciprocal is not a number and a Bloom filter at that " +
                    "rate cannot be sized.");
            }

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

            return this.Test(data.AsSpan());
        }

        /// <inheritdoc cref="Test(byte[])"/>
        public bool Test(ReadOnlySpan<byte> data)
        {
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

            return this.Add(data.AsSpan());
        }

        /// <inheritdoc cref="Add(byte[])"/>
        public IFilter Add(ReadOnlySpan<byte> data)
        {
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

            return this.TestAndAdd(data.AsSpan());
        }

        /// <inheritdoc cref="TestAndAdd(byte[])"/>
        public bool TestAndAdd(ReadOnlySpan<byte> data)
        {
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
        public static ScalableBloomFilter ReadFrom(Stream stream, ReadOptions options)
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
        public static ScalableBloomFilter ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
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
        public static ScalableBloomFilter ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, hash, options);
        }

        private static ScalableBloomFilter Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.ScalableBloomFilter);
            var payload = PersistenceFormat.Read(stream, StructureId.ScalableBloomFilter, out _, budget);
            var reader = new PayloadReader(payload, budget);

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

            // As many as the filter can grow to, so that every filter it grows reads
            // back; then checked against the bytes behind them, each a nested payload,
            // before the list is sized by the count.
            if (filterCount > MaxFilters)
            {
                throw new InvalidDataException(
                    $"Filter claims {filterCount} contained filters, beyond anything " +
                    $"this library builds: it holds them in a list of at most {MaxFilters}.");
            }
            reader.RequireHolds(
                filterCount, PersistenceFormat.MinNestedLength, "contained filters");

            budget.Charge(ReadBudget.Sum(
                ReadBudget.ArrayOf(filterCount, sizeof(long)), ReadBudget.SmallObject));
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
            var ceiling = FilterCeiling(this.FP, this.R);
            if (this.Filters.Count >= ceiling)
            {
                throw new InvalidOperationException(
                    $"This filter holds {this.Filters.Count} filters, the most its rates " +
                    $"allow. Each new filter's false positive rate is the last one's times " +
                    $"{this.R}, and the next would be {this.FP * Math.Pow(this.R, this.Filters.Count):E3}, " +
                    "whose reciprocal is larger than any double, so no Bloom filter can be " +
                    "sized for it. Nothing was added and the filter is unchanged; it can " +
                    "still be queried and written. A larger hint lets the same number of " +
                    "filters hold more items, and BloomFilter64 sizes one filter in 64 bits " +
                    "and has no ceiling of this kind.");
            }

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
