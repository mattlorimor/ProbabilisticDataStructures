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
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

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
    public class ScalableBloomFilter : IFilter
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
        /// Creates a new Scalable Bloom Filter with the specified target false-positive
        /// rate and tightening ratio. Use NewDefaultScalableBloomFilter if you don't
        /// want to calculate all these parameters.
        /// </summary>
        /// <param name="hint"></param>
        /// <param name="fpRate"></param>
        /// <param name="r"></param>
        public ScalableBloomFilter(uint hint, double fpRate, double r)
        {
            this.Filters = new List<PartitionedBloomFilter>();
            this.R = r;
            this.FP = fpRate;
            this.P = ProbabilisticDataStructures.FILL_RATIO;
            this.Hint = hint;

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
            var member = this.Test(data);
            this.Add(data);
            return member;
        }

        /// <summary>
        /// Sets the hashing function used in the filter.
        /// </summary>
        /// <param name="h">The HashAlgorithm to use.</param>
        // TODO: Add SetHash to the IFilter interface?
        public void SetHash(HashAlgorithm h)
        {
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
            var p = new PartitionedBloomFilter(this.Hint, fpRate);
            if (this.Filters.Count() > 0)
            {
                p.SetHash(this.Filters[0].Hash);
            }
            this.Filters.Add(p);
        }
    }
}
