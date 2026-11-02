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
    /// Each filter added can also be larger than the last, by the growth factor s: the
    /// i-th is built for hint * s^i items. With s of 2 or more the number of filters --
    /// and so the work every Test does, since it asks each of them -- grows with the
    /// logarithm of the items added, and so does the space each item costs. With s = 1
    /// every filter is built for the hint, which is how this filter grew before the
    /// factor existed; the number of filters then grows with the items, and the
    /// tightened rate passes what a double can hold after a few thousand of them.
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
        /// The most filters a scalable filter can hold: they are a list, and a list
        /// holds at most <see cref="Array.MaxLength"/>.
        /// </summary>
        /// <remarks>
        /// There is no constructor argument for this to check. A filter grows by adding
        /// filters, each at a rate tightened by r, and at a growth factor of one with r
        /// near one it can go on adding them until the list itself refuses. The reader
        /// accepts as many as the list holds, so whatever a filter grew to reads back. It
        /// used to stop at a million, which a filter with a hint of one, a rate of one
        /// half and a ratio of 0.999999 passes after about 1.7 million adds. A filter
        /// that grows stops far sooner, at a few dozen, when its next filter would need
        /// more than a contained filter's 32 bits address.
        /// </remarks>
        internal static int MaxFilters => Array.MaxLength;
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
        /// The growth factor a filter is built with when its constructor is not given
        /// one.
        /// </summary>
        /// <remarks>
        /// One constant, so that choosing another default changes this line and nothing
        /// else: every constructor without a growth factor takes it, and the tests that
        /// depend on a particular factor name theirs. It is never what a payload is read
        /// with. A payload from before the factor existed is read with a factor of one,
        /// whatever this says, because one is how the filter that wrote it grew.
        /// </remarks>
        internal const uint DefaultGrowth = 2;

        /// <summary>
        /// How many times the items of the last contained filter the next one is built
        /// for: filter i holds <see cref="Hint"/> * Growth^i. One builds every filter for
        /// the hint.
        /// </summary>
        internal uint Growth { get; private set; }
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
        /// rate and tightening ratio, whose contained filters grow by the default growth
        /// factor. Use NewDefaultScalableBloomFilter if you don't want to calculate all
        /// these parameters, and the overload taking a growth factor to choose one.
        /// </summary>
        /// <param name="hint">How many items the first contained filter is built for.</param>
        /// <param name="fpRate">
        /// The false positive rate the first contained filter is built for. Each filter
        /// after it is built for this rate tightened by <paramref name="r"/> once more,
        /// and the filter as a whole stays below fpRate / (1 - r).
        /// </param>
        /// <param name="r">The tightening ratio, greater than 0 and less than 1.</param>
        /// <param name="hash">
        /// The hash function to use, or null for the default. Passing it here is the
        /// only way to have one hash cover everything the structure will ever hold:
        /// once anything has been added, the hash can no longer be replaced.
        /// </param>
        public ScalableBloomFilter(uint hint, double fpRate, double r,
            Func<ReadOnlySpan<byte>, ulong>? hash = null)
            : this(hint, fpRate, r, DefaultGrowth, hash)
        {
        }

        /// <summary>
        /// Creates a new Scalable Bloom Filter whose contained filters grow by
        /// <paramref name="growth"/>: the first is built for <paramref name="hint"/>
        /// items, and each one after it for <paramref name="growth"/> times as many as
        /// the one before, at a rate tightened by <paramref name="r"/>.
        /// </summary>
        /// <param name="hint">How many items the first contained filter is built for.</param>
        /// <param name="fpRate">
        /// The false positive rate the first contained filter is built for. Each filter
        /// after it is built for this rate tightened by <paramref name="r"/> once more,
        /// and the filter as a whole stays below fpRate / (1 - r) whatever the growth.
        /// </param>
        /// <param name="r">The tightening ratio, greater than 0 and less than 1.</param>
        /// <param name="growth">
        /// How many times larger each contained filter is than the last, at least 1. Two
        /// or four, the factors the paper proposes, keep the number of filters and so the
        /// work of every Test logarithmic in the items added, and the bits each item costs
        /// within a small factor of one filter's; a larger factor builds fewer, larger
        /// filters, and holds more unused space after each one is added. One builds
        /// every filter for the hint, which is how this filter grew before the factor
        /// existed: the number of filters, the cost of a Test and the bits per item then
        /// all grow with the items added, and the filter stops after a few thousand
        /// filters, when the tightened rate passes what a double holds.
        /// </param>
        /// <param name="hash">
        /// The hash function to use, or null for the default. Passing it here is the
        /// only way to have one hash cover everything the structure will ever hold:
        /// once anything has been added, the hash can no longer be replaced.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="hint"/> or <paramref name="growth"/> is zero,
        /// <paramref name="fpRate"/> is not between 0 and 1, or <paramref name="r"/> is
        /// not between 0 and 1.
        /// </exception>
        public ScalableBloomFilter(uint hint, double fpRate, double r, uint growth,
            Func<ReadOnlySpan<byte>, ulong>? hash = null)
        {
            Guard.ValidItemCount(hint, nameof(hint));
            Guard.ValidFalsePositiveRate(fpRate, nameof(fpRate));
            Guard.ValidTighteningRatio(r, nameof(r));
            Guard.ValidGrowthFactor(growth, nameof(growth));

            this.Filters = new List<PartitionedBloomFilter>();
            this.R = r;
            this.FP = fpRate;
            this.P = Defaults.FILL_RATIO;
            this.Hint = hint;
            this.Growth = growth;
            this.Hash = hash;

            this.AddFilter();
        }

        /// <summary>
        /// Creates a new Scalable Bloom Filter with the specified target false-positive
        /// rate, an optimal tightening ratio and the default growth factor.
        /// </summary>
        /// <param name="fpRate"></param>
        public static ScalableBloomFilter NewDefaultScalableBloomFilter(double fpRate)
        {
            return new ScalableBloomFilter(10000, fpRate, 0.8);
        }

        /// <summary>
        /// Returns the current Scalable Bloom Filter capacity, which is the sum of the
        /// capacities for the contained series of Bloom filters, in bits.
        /// </summary>
        /// <remarks>
        /// A <c>ulong</c>, where each contained filter's own capacity is a <c>uint</c>: no
        /// one of them can pass 4,294,967,295 bits, but a filter whose contained filters
        /// grow passes it in total well before it stops growing. In 6.2.0 and earlier
        /// this returned a <c>uint</c>, and such a total wrapped.
        /// </remarks>
        /// <returns>The current Scalable Bloom Filter capacity</returns>
        public ulong Capacity()
        {
            // Summed in 64 bits: each filter's capacity is a uint, so the total of every
            // filter a list can hold still fits.
            var capacity = 0ul;
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
        /// <exception cref="InvalidOperationException">
        /// The filter is at its ceiling: the data needs a new contained filter, and the
        /// next one cannot be built. The filter is unchanged. With a growth factor of 1
        /// that is after a few thousand filters, when the tightened rate passes what a
        /// double holds; with one of 2 or more it is when the next filter would need
        /// more bits than a contained filter's 32 bits address.
        /// </exception>
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

            // The growth factor is written only by a filter that grows, and the version
            // raised only then. A filter that builds every filter for its hint writes the
            // bytes it always wrote, which libraries from before the factor still read.
            // Each contained filter records its own size either way; the factor is what
            // says how large the next one will be.
            var version = PersistenceFormat.DefaultVersion;
            if (this.Growth != 1)
            {
                payload.WriteUInt32(this.Growth);
                version = PersistenceFormat.ScalableGrowthVersion;
            }

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
                payload.WrittenSpan,
                version);
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
            var payload = PersistenceFormat.Read(
                stream, StructureId.ScalableBloomFilter, out _, out var version, budget);
            var reader = new PayloadReader(payload, budget);

            var r = reader.ReadDouble();
            var fp = reader.ReadDouble();
            var p = reader.ReadDouble();
            var hint = reader.ReadUInt32();

            // A payload from before version 7 carries no growth factor because the filter
            // that wrote it had none: every filter then built each of its filters for the
            // hint, so that is how this one goes on. One, and not the default a new
            // filter is given, which would size its next filter as that filter never did.
            var growth = 1u;
            if (version >= PersistenceFormat.ScalableGrowthVersion)
            {
                growth = reader.ReadUInt32();
                if (growth < 2)
                {
                    throw new InvalidDataException(
                        $"Filter claims a growth factor of {growth}. A payload at version " +
                        $"{version} carries one only because its filter grows: zero would " +
                        "size every filter after the first for no items, and a filter " +
                        "growing by one writes version 1.");
                }
            }

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
                Growth = growth,
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
        /// <exception cref="InvalidOperationException">
        /// The next filter cannot be built, and nothing has changed.
        /// </exception>
        internal void AddFilter()
        {
            var fpRate = this.FP * Math.Pow(this.R, this.Filters.Count());
            var capacity = this.NextCapacity(fpRate);
            // The hash is passed to the filter rather than set on it afterwards: a
            // filter is only safe to re-hash while empty, and building it with the
            // right one removes the window entirely.
            var inherited = this.Filters.Count > 0 ? this.Filters[0].Hash : this.Hash;
            var p = new PartitionedBloomFilter(capacity, fpRate, inherited);
            this.Filters.Add(p);
        }

        /// <summary>
        /// How many items the next contained filter is built for, Hint * Growth^i, once
        /// it is known that a filter of that many items at that rate can be built.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// It cannot, which is this filter's ceiling: the capacity is more items than a
        /// contained filter is sized for, the rate is too small to size one from, or the
        /// filter would need more bits than one addresses.
        /// </exception>
        /// <remarks>
        /// Every way out is checked here, before anything is built or added, so that the
        /// add that reaches the ceiling is refused with the filter as it was. The rate
        /// and the bits are asked of the sizing functions the contained filter's
        /// constructor calls, rather than restated, so the two cannot disagree about
        /// where the ceiling is.
        /// </remarks>
        private uint NextCapacity(double fpRate)
        {
            var index = this.Filters.Count;

            // The first filter is the constructor's arguments, checked there, and a
            // first filter too large to build is refused by name as an argument.
            if (index == 0)
            {
                return this.Hint;
            }

            // Multiplied out rather than raised to a power, so that it is exact, and only
            // until it passes what a contained filter is sized for: a factor of two or
            // more gets there within 32 steps, and a factor of one never moves.
            ulong capacity = this.Hint;
            for (var i = 0; i < index && this.Growth > 1 && capacity <= uint.MaxValue; i++)
            {
                capacity *= this.Growth;
            }

            const string OneFilter = "A BloomFilter64 sized for the expected total holds them all in one filter.";

            if (capacity > uint.MaxValue)
            {
                throw this.Ceiling(index,
                    $"would be built for {this.Hint:N0} * {this.Growth:N0}^{index} items, " +
                    $"more items than the {uint.MaxValue:N0} a contained filter is sized for",
                    OneFilter, null);
            }

            try
            {
                _ = Utils.OptimalK(fpRate);
            }
            catch (OverflowException e)
            {
                var slower = this.Growth == 1
                    ? "A larger hint or a growth factor of 2 or more reaches the same items in " +
                      "far fewer filters, and a tightening ratio nearer 1 tightens more slowly. "
                    : "A tightening ratio nearer 1 tightens more slowly. ";
                throw this.Ceiling(index,
                    $"would be built for a false positive rate of {fpRate:G3}, the target " +
                    $"{this.FP} tightened by {this.R} {index} times, and a rate that small " +
                    "has no finite reciprocal to size a filter from",
                    slower + OneFilter, e);
            }

            try
            {
                _ = Utils.OptimalM((uint)capacity, fpRate);
            }
            catch (ArgumentOutOfRangeException e)
            {
                throw this.Ceiling(index,
                    $"would be built for {capacity:N0} items at a false positive rate of " +
                    $"{fpRate:G3}, which needs more bits than the {uint.MaxValue:N0} a " +
                    "contained filter addresses",
                    OneFilter, e);
            }

            return (uint)capacity;
        }

        /// <summary>
        /// The refusal of an add at the ceiling: which filter could not be built and why,
        /// what the filter holds, and what would hold more.
        /// </summary>
        private InvalidOperationException Ceiling(int index, string why, string instead, Exception? inner)
        {
            ulong items = 0;
            foreach (var filter in this.Filters)
            {
                items += filter.Count();
            }

            return new InvalidOperationException(
                "This ScalableBloomFilter cannot add another contained filter, so it cannot " +
                $"take another item. Filter {index} {why}. It holds {items:N0} items in " +
                $"{index} {(index == 1 ? "filter" : "filters")}. {instead}", inner);
        }
    }
}
