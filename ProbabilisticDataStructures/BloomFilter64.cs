using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ProbabilisticDataStructures;
using System.Security.Cryptography;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// BloomFilter64 implements a classic Bloom filter. A bloom filter has a non-zero
    /// probability of false positives and a zero probability of false negatives.
    /// </summary>
    public class BloomFilter64 : IFilter
    {
        /// <summary>
        /// Filter data
        /// </summary>
        internal Buckets64 Buckets { get; set; }
        /// <summary>
        /// Hash algorithm
        /// </summary>
        private Func<ReadOnlySpan<byte>, ulong> Hash { get; set; } = null!;
        /// <summary>
        /// Filter size
        /// </summary>
        private ulong m { get; set; }
        /// <summary>
        /// Number of hash functions
        /// </summary>
        private uint k { get; set; }
        /// <summary>
        /// Number of items added
        /// </summary>
        private ulong count { get; set; }

        /// <summary>
        /// Creates a new Bloom filter optimized to store n items with a specified target
        /// false-positive rate.
        /// </summary>
        /// <param name="n">Number of items to store.</param>
        /// <param name="fpRate">Desired false positive rate.</param>
        public BloomFilter64(ulong n, double fpRate)
        {
            Guard.ValidItemCount(n, nameof(n));
            Guard.ValidFalsePositiveRate(fpRate, nameof(fpRate));

            var m = Utils.OptimalM64(n, fpRate);
            var k = Utils.OptimalK(fpRate);
            Buckets = new Buckets64(m, 1);
            Hash = Defaults.GetDefaultHashFunction();
            this.m = m;
            this.k = k;
        }

        /// <summary>
        /// Returns the Bloom filter capacity, m.
        /// </summary>
        /// <returns>The Bloom filter capacity, m.</returns>
        public ulong Capacity()
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
        public ulong Count()
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
            ulong sum = 0;
            for (ulong i = 0; i < this.Buckets.count; i++)
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

            var hashKernel = Utils.HashKernel128(data, this.Hash);
            var lower = hashKernel.LowerBaseHash;
            var upper = hashKernel.UpperBaseHash;

            // If any of the K bits are not set, then it's not a member.
            for (uint i = 0; i < this.k; i++)
            {
                if (this.Buckets.Get((lower + upper * i) % this.m) == 0)
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

            var hashKernel = Utils.HashKernel128(data, this.Hash);
            var lower = hashKernel.LowerBaseHash;
            var upper = hashKernel.UpperBaseHash;

            // Set the K bits.
            for (uint i = 0; i < this.k; i++)
            {
                this.Buckets.Set((lower + upper * i) % this.m, 1);
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

            var hashKernel = Utils.HashKernel128(data, this.Hash);
            var lower = hashKernel.LowerBaseHash;
            var upper = hashKernel.UpperBaseHash;
            var member = true;

            // If any of the K bits are not set, then it's not a member.
            for (uint i = 0; i < this.k; i++)
            {
                var idx = (lower + upper * i) % this.m;
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
        public BloomFilter64 Reset()
        {
            this.Buckets.Reset();
            return this;
        }

        /// <summary>
        /// Sets the hashing function used in the filter.
        /// </summary>
        /// <param name="h">The hash function to use.</param>
        // TODO: Add SetHash to the IFilter interface?
        public void SetHash(Func<ReadOnlySpan<byte>, ulong> h)
        {
            this.Hash = h;
        }
    }
}
