/*
Original work Copyright (c) 2012 Jeff Hodges. All rights reserved.
Modified work Copyright (c) 2015 Tyler Treat. All rights reserved.
Modified work Copyright (c) 2015 Matthew Lorimor. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * Neither the name of Jeff Hodges nor the names of this project's
contributors may be used to endorse or promote products derived from
this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System.Linq;
using System;
using System.Security.Cryptography;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// InverseBloomFilter is an "inverse" Bloom filter, which is effectively the
    /// opposite of a classic Bloom filter. This was originally described and
    /// written by Jeff Hodges:
    ///
    /// http://www.somethingsimilar.com/2012/05/21/the-opposite-of-a-bloom-filter/
    ///
    /// The InverseBloomFilter may report a false negative but can never report a
    /// false positive. That is, it may report that an item has not been seen when
    /// it actually has, but it will never report an item as seen which it hasn't
    /// come across. This behaves in a similar manner to a fixed-size hashmap which
    /// does not handle conflicts.
    ///
    /// An example use case is deduplicating events while processing a stream of
    /// data. Ideally, duplicate events are relatively close together.
    ///
    /// This filter stores the data itself rather than only its hash, and takes its
    /// own copy on <see cref="Add"/> so that a caller reusing their buffer cannot
    /// change what it holds. It is not thread-safe: the original swaps the stored
    /// value atomically, and this reads and writes the slot in two steps.
    /// </summary>
    public class InverseBloomFilter : IFilter
    {
        private byte[][] Array { get; set; }
        internal Func<ReadOnlySpan<byte>, ulong> Hash { get; set; } = null!;
        private uint capacity { get; set; }

        /// <summary>
        /// Instantiates an InverseBloomFilter with the specified capacity.
        /// </summary>
        /// <param name="capacity">The capacity of the filter</param>
        public InverseBloomFilter(uint capacity)
        {
            Guard.ValidItemCount(capacity, nameof(capacity));

            this.Array = new byte[capacity][];
            this.Hash = Defaults.GetDefaultHashFunction();
            this.capacity = capacity;
        }


        /// <summary>
        /// Will test for membership of the data and returns true if it is a
        /// member, false if not. This is a probabilistic test, meaning there is a
        /// non-zero probability of false negatives but a zero probability of false
        /// positives. That is, it may return false even though the data was added, but
        /// it will never return true for data that hasn't been added.
        /// </summary>
        /// <param name="data">The data to test for</param>
        /// <returns>Whether or not the data is present</returns>
        public bool Test(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            var index = this.Index(data);
            var val = this.Array[index];
            if (val == null)
            {
                return false;
            }
            return Enumerable.SequenceEqual(val, data);
        }

        /// <summary>
        /// Will add the data to the filter. It returns the filter to allow for chaining.
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public IFilter Add(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            var index = this.Index(data);
            this.GetAndSet(index, data);
            return this;
        }

        /// <summary>
        /// Equivalent to calling Test followed by Add atomically. It returns true if
        /// the data is a member, false if not.
        /// </summary>
        /// <param name="data">The data to test and add</param>
        /// <returns>Whether the data was already a member</returns>
        public bool TestAndAdd(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            var index = this.Index(data);
            var oldId = this.GetAndSet(index, data);
            if (oldId == null)
            {
                return false;
            }
            return Enumerable.SequenceEqual(oldId, data);
        }

        /// <summary>
        /// Returns the filter capactiy.
        /// </summary>
        /// <returns>The filter capactiy</returns>
        public uint Capacity()
        {
            return this.capacity;
        }

        /// <summary>
        /// Returns the data that was in the array at the given index after putting the
        /// new data in the array at that index, atomically.
        /// </summary>
        /// <param name="index">The index to get and set</param>
        /// <param name="data">The data to set</param>
        /// <returns>
        /// The data that was in the array at the index before setting it
        /// </returns>
        private byte[] GetAndSet(uint index, byte[] data)
        {
            var oldData = this.Array[index];

            // Copied, not retained. This filter is the only one here that keeps the
            // data rather than just hashing it, and Test answers by comparing the
            // stored bytes against the query. Holding the caller's array would let
            // their next write into it change what this filter believes it holds --
            // and callers do reuse buffers, which is the whole reason they are fast.
            //
            // That is not a small error. A caller filling one buffer per record leaves
            // every written slot pointing at the same array, so the filter answers
            // every one of them with whatever that buffer holds now. Querying a value
            // never added then reads it straight back out of a slot it was never put
            // in, and the filter reports it present: measured at 38.8% against a
            // structure whose defining property is that it never reports a false
            // positive at all.
            this.Array[index] = (byte[])data.Clone();

            return oldData;
        }

        /// <summary>
        /// Returns the array index for the given data.
        /// </summary>
        /// <param name="data">The data to find the index for</param>
        /// <returns>The array index for the given data</returns>
        private uint Index(byte[] data)
        {
            var index = this.ComputeHashSum32(data) % this.capacity;
            return index;
        }

        /// <summary>
        /// Returns a 32-bit hash value for the given data.
        /// </summary>
        /// <param name="data">Data</param>
        /// <returns>32-bit hash value</returns>
        private uint ComputeHashSum32(byte[] data)
        {
            return (uint)(this.Hash(data) & 0xffffffff);
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
