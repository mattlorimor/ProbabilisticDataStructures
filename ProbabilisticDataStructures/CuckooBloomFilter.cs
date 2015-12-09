using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// CuckooFilter implements a Cuckoo Bloom filter as described by Andersen, Kaminsky,
    /// and Mitzenmacher in Cuckoo Filter: Practically Better Than Bloom:
    ///
    /// http://www.pdl.cmu.edu/PDL-FTP/FS/cuckoo-conext2014.pdf
    ///
    /// A Cuckoo Filter is a Bloom filter variation which provides support for removing
    /// elements without significantly degrading space and performance. It works by using
    /// a cuckoo hashing scheme for inserting items. Instead of storing the elements
    /// themselves, it stores their fingerprints which also allows for item removal
    /// without false negatives (if you don't attempt to remove an item not contained in
    /// the filter).
    ///
    /// For applications that store many items and target moderately low false-positive
    /// rates, cuckoo filters have lower space overhead than space-optimized Bloom filters.
    /// </summary>
    public class CuckooBloomFilter
    {
        /// <summary>
        /// The maximum number of relocations to attempt when inserting an element before
        /// considering the filter full.
        /// </summary>
        private const int MAX_NUM_KICKS = 500;

        public byte[][][] Buckets { get; private set; }
        /// <summary>
        /// Hash algorithm.
        /// </summary>
        private HashAlgorithm hash { get; set; }
        /// <summary>
        /// Number of buckets
        /// </summary>
        private uint m { get; set; }
        /// <summary>
        /// Number of entries per bucket
        /// </summary>
        public uint b { get; private set; }
        /// <summary>
        /// Length of fingerprints (in bytes)
        /// </summary>
        private uint f { get; set; }
        /// <summary>
        /// Number of items in the filter
        /// </summary>
        private uint count { get; set; }
        /// <summary>
        /// Filter capacity
        /// </summary>
        private uint n { get; set; }

        /// <summary>
        /// Creates a new Cuckoo Bloom filter optimized to store n items with a specified
        /// target false-positive rate.
        /// </summary>
        /// <param name="n">Number of items to store</param>
        /// <param name="fpRate">Target false-positive rate</param>
        public CuckooBloomFilter(uint n, double fpRate)
        {
            var b = (uint)4;
            var f = this.calculateF(b, fpRate);
            var m = this.power2(n / f * 8);
            var buckets = new byte[m][][];

            for (uint i = 0; i < m; i++)
            {
                buckets[i] = new byte[b][];
            }

            this.Buckets = buckets;
            this.hash = HashAlgorithm.Create("MD5");
            this.m = m;
            this.b = b;
            this.f = f;
            this.n = n;
        }

        /// <summary>
        /// Returns the number of buckets.
        /// </summary>
        /// <returns>The number of buckets</returns>
        public uint BucketCount()
        {
            return this.m;
        }

        /// <summary>
        /// Returns the number of items the filter can store.
        /// </summary>
        /// <returns>The number of items the filter can store</returns>
        public uint Capacity()
        {
            return this.n;
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
        /// Will test for membership of the data and returns true if it is a member,
        /// false if not. This is a probabilistic test, meaning there is a non-zero
        /// probability of false positives.
        /// </summary>
        /// <param name="data">The data to test for</param>
        /// <returns>Whether or not the data is a member</returns>
        public bool Test(byte[] data)
        {
            var components = this.components(data);
            var i1 = components.Item1;
            var i2 = components.Item2;
            var f = components.Item3;

            // If either bucket containsf, it's a member.
            var b1 = this.Buckets[i1 % this.m];
            foreach (var sequence in b1)
            {
                if (sequence != null)
                    if (Enumerable.SequenceEqual(sequence, f))
                        return true;
            }
            var b2 = this.Buckets[i2 % this.m];
            foreach (var sequence in b2)
            {
                if (sequence != null)
                    if (Enumerable.SequenceEqual(sequence, f))
                        return true;
            }
            return false;
        }

        /// <summary>
        /// Will add the data to the Cuckoo Filter. It returns false if the filter is
        /// full. If the filter is full, an item is removed to make room for the new
        /// item. This introduces a possibility for false negatives. To avoid this, use
        /// Count and Capacity to check if the filter is full before adding an item.
        /// </summary>
        /// <param name="data"></param>
        /// <returns>
        /// True if the add was successful. False if the filter is full.
        /// </returns>
        public bool Add(byte[] data)
        {
            var components = this.components(data);
            var i1 = components.Item1;
            var i2 = components.Item2;
            var f = components.Item3;
            return this.add(i1, i2, f);
        }

        /// <summary>
        /// Equivalent to calling Test followed by Add. It returns (true, false) if the
        /// data is a member, (false, add()) if not. False is returned if the filter is
        /// full. If the filter is full, an item is removed to make room for the new
        /// item. This introduces a possibility for false negatives. To avoid this, use
        /// Count and Capacity to check if the filter is full before adding an item.
        /// </summary>
        /// <returns>
        /// (true, false) if the data is a member, (false, add()) if not
        /// </returns>
        public Tuple<bool, bool> TestAndAdd(byte[] data)
        {
            var components = this.components(data);
            var i1 = components.Item1;
            var i2 = components.Item2;
            var f = components.Item3;

            // If either bucket contains f, it's a member.
            var b1 = this.Buckets[i1 % this.m];
            foreach (var sequence in b1)
            {
                if (sequence != null)
                    if (Enumerable.SequenceEqual(sequence, f))
                        return Tuple.Create(true, false);
            }
            var b2 = this.Buckets[i2 % this.m];
            foreach (var sequence in b2)
            {
                if (sequence != null)
                    if (Enumerable.SequenceEqual(sequence, f))
                        return Tuple.Create(true, false);
            }

            return Tuple.Create(false, this.add(i1, i2, f));
        }

        /// <summary>
        /// Will test for membership of the data and remove it from the filter if it
        /// exists. Returns true if the data was a member, false if not.
        /// </summary>
        /// <param name="data">Data to test for and remove</param>
        /// <returns>Whether the data was a member or not</returns>
        public bool TestAndRemove(byte[] data)
        {
            var components = this.components(data);
            var i1 = components.Item1;
            var i2 = components.Item2;
            var f = components.Item3;

            // Try to remove from bucket[i1].
            var b1 = this.Buckets[i1 % this.m];
            var idx = this.IndexOf(b1, f);
            if (idx != -1)
            {
                b1[idx] = null;
                this.count--;
                return true;
            }

            // Try to remove from bucket[i2].
            var b2 = this.Buckets[i2 % this.m];
            idx = this.IndexOf(b2, f);
            if (idx != -1)
            {
                b2[idx] = null;
                this.count--;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Restores the Bloom filter to its original state. It returns the filter to
        /// allow for chaining.
        /// </summary>
        /// <returns>The CuckooBloomFilter</returns>
        public CuckooBloomFilter Reset()
        {
            var buckets = new byte[this.m][][];
            for (uint i = 0; i < this.m; i++)
            {
                buckets[i] = new byte[this.b][];
            }
            this.Buckets = buckets;
            this.count = 0;
            return this;
        }

        /// <summary>
        /// Indicates if the given fingerprint is contained in one of the bucket's
        /// entries.
        /// </summary>
        /// <param name="f">Fingerprint</param>
        /// <returns>
        /// Whether or not the fingerprint is contained in one of the bucket's entries.
        /// </returns>
        private bool Contains(byte[][] bucket, byte[] f)
        {
            return this.IndexOf(bucket, f) != 1;
        }

        /// <summary>
        /// Returns the entry index of the given fingerprint or -1 if it's not in the
        /// bucket.
        /// </summary>
        /// <param name="f">Fingerprint</param>
        /// <returns>The entry index of the fingerprint or -1 if it's not in the
        /// bucket</returns>
        private int IndexOf(byte[][] bucket, byte[] f)
        {
            for (int i = 0; i < bucket.Count(); i++)
            {
                var sequence = bucket[i];
                if (sequence != null)
                {
                    if (Enumerable.SequenceEqual(f, bucket[i]))
                    {
                        return i;
                    }
                }
            }
            return -1;
        }

        /// <summary>
        /// Returns the index of the next available entry in the bucket or -1 if it's
        /// full.
        /// </summary>
        /// <returns></returns>
        private int GetEmptyEntry(byte[][] bucket)
        {
            for (int i = 0; i < bucket.Count(); i++)
            {
                if (bucket[i] == null)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Will insert the fingerprint into the filter returning false if the filter is
        /// full.
        /// </summary>
        /// <param name="i1"></param>
        /// <param name="i2"></param>
        /// <param name="f"></param>
        /// <returns>
        /// True if the insert was successful. False if the filter is full
        /// </returns>
        private bool add(uint i1, uint i2, byte[] f)
        {
            // Try to insert into bucket[i1].
            var b1 = this.Buckets[i1 % this.m];
            var idx = this.GetEmptyEntry(b1);
            if (idx != -1)
            {
                b1[idx] = f;
                this.count++;
                return true;
            }

            // Try to insert into bucket[i2].
            var b2 = this.Buckets[i2 % this.m];
            var ids = this.GetEmptyEntry(b2);
            if (idx != -1)
            {
                b2[idx] = f;
                this.count++;
                return true;
            }

            // Must relocate existing items.
            var i = i1;
            var rand = new Random();
            for (int n = 0; n < MAX_NUM_KICKS; n++)
            {
                var bucketIdx = i % this.m;
                var entryIdx = rand.Next((int)this.b);
                var tempF = f;
                f = this.Buckets[bucketIdx][entryIdx];
                this.Buckets[bucketIdx][entryIdx] = tempF;
                i = i ^ ProbabilisticDataStructures.ToBigEndianUInt32(this.computeHash(f));
                var b = this.Buckets[i % this.m];

                idx = this.GetEmptyEntry(b);
                if (idx != -1)
                {
                    b[idx] = f;
                    this.count++;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns the two hash values used to index into the buckets and the
        /// fingerprint for the given element.
        /// </summary>
        /// <param name="data">Data</param>
        /// <returns>The two hash values used to index into the buckets and the
        /// fingerprint for the given data</returns>
        private Tuple<uint, uint, byte[]> components(byte[] data)
        {
            var hash = this.computeHash(data);
            var f = hash.Take((int)this.f).ToArray();
            var i1 = ProbabilisticDataStructures.ToBigEndianUInt32(hash);
            var i2 = ProbabilisticDataStructures.ToBigEndianUInt32(this.computeHash(f));

            return Tuple.Create<uint, uint, byte[]>(i1, i2, f);
        }

        /// <summary>
        /// Returns a 32-bit hash value for the given data.
        /// </summary>
        /// <param name="data">Data</param>
        /// <returns>32-bit hash value</returns>
        private byte[] computeHash(byte[] data)
        {
            var hash = new Hash(this.hash);
            hash.ComputeHash(data);
            var sum = hash.Sum();
            return sum;
        }

        /// <summary>
        /// Sets the hashing function used in the filter.
        /// </summary>
        /// <param name="h">The HashAlgorithm to use.</param>
        public void SetHash(HashAlgorithm h)
        {
            this.hash = h;
        }

        /// <summary>
        /// Returns the optimal fingerprint length in bytes for the given bucket size and
        /// false-positive rate epsilon.
        /// </summary>
        /// <param name="b">Bucket size</param>
        /// <param name="epsilon">False positive rate</param>
        /// <returns>The optimal fingerprint length</returns>
        private uint calculateF(uint b, double epsilon)
        {
            var f = (uint)Math.Ceiling(Math.Log(2 * b / epsilon));
            f = f / 8;
            if (f <= 0)
            {
                f = 1;
            }
            return f;
        }

        /// <summary>
        /// Calculates the next power of two for the given value.
        /// </summary>
        /// <param name="x">Value</param>
        /// <returns>The next power of two for the given value</returns>
        private uint power2(uint x)
        {
            x--;
            x |= x >> 1;
            x |= x >> 2;
            x |= x >> 4;
            x |= x >> 8;
            x |= x >> 16;
            x |= x >> 32;
            x++;
            return x;
        }
    }
}
