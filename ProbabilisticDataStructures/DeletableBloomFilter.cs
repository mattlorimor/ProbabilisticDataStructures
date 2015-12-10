using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

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
    public class DeletableBloomFilter : IFilter
    {
        /// <summary>
        /// Filter data
        /// </summary>
        internal Buckets buckets { get; set; }
        /// <summary>
        /// Filter collision data
        /// </summary>
        internal Buckets collisions { get; set; }
        /// <summary>
        /// Hash algorithm
        /// </summary>
        private HashAlgorithm hash { get; set; }
        /// <summary>
        /// Filter size
        /// </summary>
        private uint m { get; set; }
        /// <summary>
        /// Number of bits in a region
        /// </summary>
        private uint regionSize { get; set; }
        /// <summary>
        /// Number of hash functions
        /// </summary>
        private uint k { get; set; }
        /// <summary>
        /// Number of items in the filter
        /// </summary>
        private uint count { get; set; }
        /// <summary>
        /// Buffer used to cache indices
        /// </summary>
        private uint[] indexBuffer { get; set; }

        /// <summary>
        /// NewDeletableBloomFilter creates a new DeletableBloomFilter optimized to store
        /// n items with a specified target false-positive rate. The r value determines
        /// the number of bits to use to store collision information. This controls the
        /// deletability of an element. Refer to the paper for selecting an optimal value.
        /// </summary>
        /// <param name="n">Number of items</param>
        /// <param name="r">Number of bits to use to store collision information</param>
        /// <param name="fpRate">Desired false positive rate</param>
        public DeletableBloomFilter(uint n, uint r, double fpRate)
        {
            var m = ProbabilisticDataStructures.OptimalM(n, fpRate);
            var k = ProbabilisticDataStructures.OptimalK(fpRate);

            this.buckets = new Buckets(m - r, 1);
            this.collisions = new Buckets(r, 1);
            this.hash = HashAlgorithm.Create("MD5");
            this.m = m - r;
            this.regionSize = (m - r) / r;
            this.k = k;
            this.indexBuffer = new uint[k];
        }

        /// <summary>
        /// Returns the Bloom filter capacity, m.
        /// </summary>
        /// <returns>The Bloom filter capacity, m</returns>
        public uint Capacity()
        {
            return this.m;
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
        /// <returns>The number of items added to the filter</returns>
        public uint Count()
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
            var hashKernel = ProbabilisticDataStructures.HashKernel(data, this.hash);
            var lower = hashKernel.Item1;
            var upper = hashKernel.Item2;

            // If any of the K bits are not set, then it's not a member.
            for (uint i = 0; i < this.k; i++)
            {
                if (this.buckets.Get((lower + upper * i) % this.m) == 0)
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
            var hashKernel = ProbabilisticDataStructures.HashKernel(data, this.hash);
            var lower = hashKernel.Item1;
            var upper = hashKernel.Item2;

            // Set the K bits.
            for (uint i = 0; i < this.k; i++)
            {
                var idx = (lower + upper * i) % this.m;
                if (this.buckets.Get(idx) != 0)
                {
                    // Collision, set corresponding region bit.
                    this.collisions.Set(idx / this.regionSize, 1);
                }
                else
                {
                    this.buckets.Set(idx, 1);
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
            var hashKernel = ProbabilisticDataStructures.HashKernel(data, this.hash);
            var lower = hashKernel.Item1;
            var upper = hashKernel.Item2;
            var member = true;

            // If any of the K bits are not set, then it's not a member.
            for (uint i = 0; i < this.k; i++)
            {
                var idx = (lower + upper * i) % this.m;
                if (this.buckets.Get(idx) == 0)
                {
                    member = false;
                }
                else
                {
                    // Collision, set corresponding region bit.
                    this.collisions.Set(idx / this.regionSize, 1);
                }
                this.buckets.Set(idx, 1);
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
            var hashKernel = ProbabilisticDataStructures.HashKernel(data, this.hash);
            var lower = hashKernel.Item1;
            var upper = hashKernel.Item2;
            var member = true;

            // Set the K bits.
            for (uint i = 0; i < this.k; i++)
            {
                var idx = (lower + upper * i) % this.m;
                this.indexBuffer[i] = idx;
                if (this.buckets.Get(idx) == 0)
                {
                    member = false;
                }
            }

            if (member)
            {
                foreach (var idx in this.indexBuffer)
                {
                    if (this.collisions.Get(idx / this.regionSize) == 0)
                    {
                        // Clear only bits located in collision-free zones.
                        this.buckets.Set(idx, 0);
                    }
                }
                this.count--;
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
            this.buckets.Reset();
            this.collisions.Reset();
            this.count = 0;
            return this;
        }

        /// <summary>
        /// Sets the hashing function used in the filter.
        /// </summary>
        /// <param name="h">The HashAlgorithm to use.</param>
        // TODO: Add SetHash to the IFilter interface?
        public void SetHash(HashAlgorithm h)
        {
            this.hash = h;
        }
    }
}
