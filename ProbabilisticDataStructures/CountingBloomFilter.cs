using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

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
    /// Counting Bloom Filters are useful for cases where elements are both added
    /// and removed from the data set. Since they use n-bit buckets, CBFs use
    /// roughly n-times more memory than traditional Bloom filters.
    /// </summary>
    public class CountingBloomFilter : IFilter
    {
        /// <summary>
        /// Filter data
        /// </summary>
        internal Buckets Buckets { get; set; }
        /// <summary>
        /// Hash algorithm
        /// </summary>
        private HashAlgorithm Hash { get; set; }
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
        /// Buffer used to cache indices
        /// </summary>
        private uint[] indexBuffer { get; set; }

        /// <summary>
        /// Creates a new Counting Bloom Filter optimized to store n-items with a
        /// specified target false-positive rate and bucket size. If you don't know how
        /// many bits to use for buckets, use NewDefaultCountingBloomFilter for a
        /// sensible default.
        /// </summary>
        /// <param name="n">Number of items to store.</param>
        /// <param name="b">Bucket size.</param>
        /// <param name="fpRate">Desired false positive rate.</param>
        public CountingBloomFilter(uint n, byte b, double fpRate)
        {
            var m = Utils.OptimalM(n, fpRate);
            var k = Utils.OptimalK(fpRate);
            this.Buckets =  new Buckets(m, b);
            this.Hash = Defaults.GetDefaultHashAlgorithm();
            this.m = m;
            this.k = k;
            this.indexBuffer = new uint[k];
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
        /// <returns></returns>
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
            var hashKernel = Utils.HashKernel(data, this.Hash);
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
            var hashKernel = Utils.HashKernel(data, this.Hash);
            var lower = hashKernel.LowerBaseHash;
            var upper = hashKernel.UpperBaseHash;

            // Set the K bits.
            for (uint i = 0; i < this.k; i++)
            {
                this.Buckets.Increment((lower + upper * i) % this.m, 1);
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
            var hashKernel = Utils.HashKernel(data, this.Hash);
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
            var hashKernel = Utils.HashKernel(data, this.Hash);
            var lower = hashKernel.LowerBaseHash;
            var upper = hashKernel.UpperBaseHash;
            var member = true;

            // Set the K bits.
            for (uint i = 0; i < this.k; i++)
            {
                this.indexBuffer[i] = (lower + upper * i) % this.m;
                if (this.Buckets.Get(this.indexBuffer[i]) == 0)
                {
                    member = false;
                }
            }

            if (member)
            {
                foreach (var idx in this.indexBuffer)
                {
                    this.Buckets.Increment(idx, -1);
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
        public CountingBloomFilter Reset()
        {
            this.Buckets.Reset();
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
            this.Hash = h;
        }
    }
}
