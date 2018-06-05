﻿using System;
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
    public class StableBloomFilter : IFilter
    {
        /// <summary>
        /// Filter data
        /// </summary>
        internal Buckets cells { get; set; }
        /// <summary>
        /// Hash algorightm
        /// </summary>
        private HashAlgorithm Hash { get; set; }
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
        private uint[] IndexBuffer { get; set; }

        private Random random = new Random();

        /// <summary>
        /// Empty constructor
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
        public StableBloomFilter(uint m, byte d, double fpRate)
        {
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

            this.Hash = Defaults.GetDefaultHashAlgorithm();
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
                Hash = Defaults.GetDefaultHashAlgorithm(),
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
        /// Sets the hashing function used in the filter.
        /// </summary>
        /// <param name="h">The HashAlgorithm to use.</param>
        // TODO: Add SetHash to the IFilter interface?
        public void SetHash(HashAlgorithm h)
        {
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
            var r = random.Next((int)this.M);
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
