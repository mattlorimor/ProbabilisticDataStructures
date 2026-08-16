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
using System.IO;
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
    /// own copy on <see cref="Add(byte[])"/> so that a caller reusing their buffer cannot
    /// change what it holds. It is not thread-safe: the original swaps the stored
    /// value atomically, and this reads and writes the slot in two steps.
    /// </summary>
    public class InverseBloomFilter : IFilter, IBinaryPersistable<InverseBloomFilter>
    {
        private byte[][] Array { get; set; }
        internal Func<ReadOnlySpan<byte>, ulong> Hash { get; set; } = null!;
        private uint capacity { get; set; }

        /// <summary>
        /// Whether any slot is occupied, which is what decides if the hash can
        /// still be replaced. Derived rather than tracked, so that a filter read
        /// back from a payload answers this the way the one that wrote it would.
        /// </summary>
        private bool IsEmpty
        {
            get
            {
                foreach (var slot in this.Array)
                {
                    if (slot is not null)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Instantiates an InverseBloomFilter with the specified capacity.
        /// </summary>
        /// <param name="capacity">The capacity of the filter</param>
        /// <param name="hash">
        /// The hash function to use, or null for the default. Passing it here is the
        /// only way to have one hash cover everything the structure will ever hold:
        /// once anything has been added, the hash can no longer be replaced.
        /// </param>
        public InverseBloomFilter(uint capacity, Func<ReadOnlySpan<byte>, ulong>? hash = null)
        {
            Guard.ValidItemCount(capacity, nameof(capacity));

            this.Array = new byte[capacity][];
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();
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

            return this.Test(data.AsSpan());
        }

        /// <inheritdoc cref="Test(byte[])"/>
        /// <remarks>
        /// Allocation-free: the query is hashed and then compared against the stored
        /// bytes in place. Only <see cref="Add(ReadOnlySpan{byte})"/> must copy.
        /// </remarks>
        public bool Test(ReadOnlySpan<byte> data)
        {
            var index = this.Index(data);
            var val = this.Array[index];
            if (val == null)
            {
                return false;
            }
            return data.SequenceEqual(val);
        }

        /// <summary>
        /// Will add the data to the filter. It returns the filter to allow for chaining.
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public IFilter Add(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            return this.Add(data.AsSpan());
        }

        /// <inheritdoc cref="Add(byte[])"/>
        /// <remarks>
        /// Copies. This filter answers by comparing stored bytes rather than by
        /// hashing alone, so it must keep what it is given, and a span promises
        /// nothing about how long its memory stays valid or unchanged.
        /// </remarks>
        public IFilter Add(ReadOnlySpan<byte> data)
        {
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

            return this.TestAndAdd(data.AsSpan());
        }

        /// <inheritdoc cref="TestAndAdd(byte[])"/>
        /// <remarks>Copies, for the reason given on <see cref="Add(ReadOnlySpan{byte})"/>.</remarks>
        public bool TestAndAdd(ReadOnlySpan<byte> data)
        {
            var index = this.Index(data);
            var oldId = this.GetAndSet(index, data);
            if (oldId == null)
            {
                return false;
            }
            return data.SequenceEqual(oldId);
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
        private byte[] GetAndSet(uint index, ReadOnlySpan<byte> data)
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
            this.Array[index] = data.ToArray();

            return oldData;
        }

        /// <summary>
        /// Returns the array index for the given data.
        /// </summary>
        /// <param name="data">The data to find the index for</param>
        /// <returns>The array index for the given data</returns>
        private uint Index(ReadOnlySpan<byte> data)
        {
            var index = this.ComputeHashSum32(data) % this.capacity;
            return index;
        }

        /// <summary>
        /// Returns a 32-bit hash value for the given data.
        /// </summary>
        /// <param name="data">Data</param>
        /// <returns>32-bit hash value</returns>
        private uint ComputeHashSum32(ReadOnlySpan<byte> data)
        {
            return (uint)(this.Hash(data) & 0xffffffff);
        }

        /// <summary>
        /// Writes this filter to a stream, in the format documented in FORMAT.md.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = new PayloadWriter();
            payload.WriteUInt32(this.capacity);

            // Only the occupied slots, each with its index. This filter is the one that
            // stores the data rather than hashing it, so a full-length run of empties
            // would be most of a large, mostly idle filter's payload.
            var occupied = 0u;
            foreach (var slot in this.Array)
            {
                if (slot is not null)
                {
                    occupied++;
                }
            }

            payload.WriteUInt32(occupied);
            for (uint i = 0; i < this.capacity; i++)
            {
                var slot = this.Array[i];
                if (slot is not null)
                {
                    payload.WriteUInt32(i);
                    payload.WriteBytes(slot);
                }
            }

            PersistenceFormat.Write(
                stream,
                StructureId.InverseBloomFilter,
                PersistenceFormat.Identify(this.Hash),
                payload.WrittenSpan);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The filter that was written.</returns>
        public static InverseBloomFilter ReadFrom(Stream stream)
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
        public static InverseBloomFilter ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            return Read(stream, hash);
        }

        private static InverseBloomFilter Read(Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(stream, StructureId.InverseBloomFilter, out var hashId);
            var reader = new PayloadReader(payload);

            var capacity = reader.ReadUInt32();
            if (capacity == 0)
            {
                throw new InvalidDataException(
                    "Filter has a capacity of zero, leaving nowhere to put anything; it " +
                    "divides by that on first use.");
            }

            var occupied = reader.ReadUInt32();
            if (occupied > capacity)
            {
                throw new InvalidDataException(
                    $"Filter claims {occupied} occupied slots in a capacity of {capacity}.");
            }

            var slots = new byte[capacity][];
            for (uint n = 0; n < occupied; n++)
            {
                var index = reader.ReadUInt32();
                if (index >= capacity)
                {
                    throw new InvalidDataException(
                        $"Filter holds an entry at slot {index}, beyond its capacity of " +
                        $"{capacity}.");
                }

                slots[index] = reader.ReadBytes();
            }

            reader.ExpectEnd();

            return new InverseBloomFilter
            {
                Array = slots,
                capacity = capacity,
                Hash = PersistenceFormat.ResolveOrThrow(hashId, hash),
            };
        }

        /// <summary>
        /// Used only by <see cref="Read"/>, which sets every field itself.
        /// </summary>
        private InverseBloomFilter()
        {
            this.Array = null!;
        }

        /// <summary>
        /// Sets the hashing function used in the filter.
        /// </summary>
        /// <param name="h">The hash function to use.</param>
        // TODO: Add SetHash to the IFilter interface?
        public void SetHash(Func<ReadOnlySpan<byte>, ulong> h)
        {
            ArgumentNullException.ThrowIfNull(h);
            Guard.HashMayBeReplaced(this.IsEmpty, nameof(InverseBloomFilter));

            this.Hash = h;
        }
    }
}
