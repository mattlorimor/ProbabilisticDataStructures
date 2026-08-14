using System;
using System.IO;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Buckets64 is a fast, space-efficient array of buckets where each bucket can store
    /// up to a configured maximum value.
    /// </summary>
    /// <remarks>
    /// Internal: every member on this type already was, so as a public type it offered
    /// a consumer nothing but a name in their completion list. It is the packed array
    /// the filters are built on, not part of what this library is for.
    /// </remarks>
    internal class Buckets64
    {
        /// <summary>
        /// The widest bucket supported, in bits.
        /// </summary>
        /// <remarks>
        /// A bucket's maximum value is held in a byte, so eight bits is the widest
        /// that can be fully used. The bit-packing itself would handle more --
        /// GetBits and SetBits span byte boundaries correctly -- but a wider bucket
        /// would allocate the extra space and still cap its value at 255, so the
        /// memory would be paid for and unusable.
        ///
        /// Upstream Go BoomFilters has the same cap, reached differently: its max
        /// field is a uint8, so (1 &lt;&lt; bucketSize) - 1 wraps to 255 for any size of
        /// eight or more rather than being clamped. Rejecting the argument is
        /// preferred here over silently accepting a request that cannot be honored.
        /// </remarks>
        private const byte MaxBucketSizeBits = 8;

        // The largest C# array to create; the largest power of 2 that C# can support.
        private const uint maxArraySize = 1U << 30;
        private byte[][] Data { get; set; }
        private int arrayCount { get; set; }
        private byte bucketSize { get; set; }
        private byte _max;
        private int Max
        {
            get
            {
                return _max;
            }
            set
            {
                // Capping at 255 is correct and matches upstream: Go's Buckets
                // stores max in a uint8, so the same expression wraps to 255 there.
                // The constructor now rejects bucket sizes that would reach this
                // branch, so it remains only as a guard.
                if (value > byte.MaxValue)
                    _max = byte.MaxValue;
                else
                    _max = (byte)value;
            }
        }
        internal ulong count { get; set; }

        /// <summary>
        /// The packed bucket data, for persistence. Held as several arrays rather than
        /// one because a 64-bit filter can need more bytes than a single array holds.
        /// </summary>
        internal byte[][] RawData => this.Data;

        /// <summary>
        /// The width of each bucket, in bits, for persistence.
        /// </summary>
        internal byte BucketSize => this.bucketSize;

        /// <summary>
        /// Rebuilds a Buckets64 from data previously taken from <see cref="RawData"/>.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// The data is not shaped the way the count and bucket size imply.
        /// </exception>
        internal static Buckets64 Restore(ulong count, byte bucketSize, byte[][] data)
        {
            var buckets = new Buckets64(count, bucketSize);

            if (data.Length != buckets.Data.Length)
            {
                throw new InvalidDataException(
                    $"{count} buckets of {bucketSize} bits need {buckets.Data.Length} " +
                    $"arrays and {data.Length} were stored.");
            }

            for (int i = 0; i < data.Length; i++)
            {
                if (data[i].Length != buckets.Data[i].Length)
                {
                    throw new InvalidDataException(
                        $"Array {i} of the bucket data needs {buckets.Data[i].Length} " +
                        $"bytes and {data[i].Length} were stored.");
                }
            }

            buckets.Data = data;
            return buckets;
        }

        /// <summary>
        /// Creates a new Buckets64 with the provided number of buckets where each bucket
        /// is the specified number of bits.
        /// </summary>
        /// <param name="count">Number of buckets.</param>
        /// <param name="bucketSize">Number of bits per bucket.</param>
        internal Buckets64(ulong count, byte bucketSize)
        {
            if (bucketSize == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bucketSize), bucketSize,
                    "Bucket size must be at least 1 bit. A zero-bit bucket holds no " +
                    "value and allocates no storage, so reading one indexes an empty " +
                    "array.");
            }

            if (bucketSize > MaxBucketSizeBits)
            {
                throw new ArgumentOutOfRangeException(nameof(bucketSize), bucketSize,
                    $"Bucket size must be at most {MaxBucketSizeBits} bits. A bucket's " +
                    "maximum value is stored in a byte, so wider buckets would allocate " +
                    "the extra space without being able to hold a larger value.");
            }

            this.count = count;
            this.bucketSize = bucketSize;
            AllocateArray(count, bucketSize);
            this.Max = (1 << bucketSize) - 1;
        }

        [MemberNotNull(nameof(Data))]
        private void AllocateArray(ulong count, byte bucketSize)
        {
            this.arrayCount = (int)(count / maxArraySize + 1);
            this.Data = new byte[this.arrayCount][];
            var bytesToAllocate = (count * bucketSize + 7) / 8;
            for (int i = 0; i < this.arrayCount; i++)
            {
                var arraySize = Math.Min(bytesToAllocate, maxArraySize);
                this.Data[i] = new byte[arraySize];
                bytesToAllocate -= arraySize;
            }
        }

        /// <summary>
        /// Returns the maximum value that can be stored in a bucket.
        /// </summary>
        /// <returns>The bucket max value.</returns>
        internal byte MaxBucketValue()
        {
            return this._max;
        }

        /// <summary>
        /// Increment the value in the specified bucket by the provided delta. A bucket
        /// can be decremented by providing a negative delta.
        /// <para>
        ///     The value is clamped to zero and the maximum bucket value. Returns itself
        ///     to allow for chaining.
        /// </para>
        /// </summary>
        /// <param name="bucket">The bucket to increment.</param>
        /// <param name="delta">The amount to increment the bucket by.</param>
        /// <returns>The modified bucket.</returns>
        internal Buckets64 Increment(uint bucket, int delta)
        {
            int val = (int)(GetBits(bucket * this.bucketSize, this.bucketSize) + delta);

            if (val > this.Max)
                val = this.Max;
            else if (val < 0)
                val = 0;

            SetBits((uint)bucket * (uint)this.bucketSize, this.bucketSize, (uint)val);
            return this;
        }

        /// <summary>
        /// Set the bucket value. The value is clamped to zero and the maximum bucket
        /// value. Returns itself to allow for chaining.
        /// </summary>
        /// <param name="bucket">The bucket to change the value of.</param>
        /// <param name="value">The value to set.</param>
        /// <returns>The modified bucket.</returns>
        internal Buckets64 Set(ulong bucket, byte value)
        {
            if (value > this._max)
                value = this._max;

            SetBits(bucket * this.bucketSize, this.bucketSize, value);
            return this;
        }

        /// <summary>
        /// Returns the value in the specified bucket.
        /// </summary>
        /// <param name="bucket">The bucket to get.</param>
        /// <returns>The specified bucket.</returns>
        internal uint Get(ulong bucket)
        {
            return GetBits(bucket * this.bucketSize, this.bucketSize);
        }

        /// <summary>
        /// Restores the Buckets64 to the original state. Returns itself to allow for
        /// chaining.
        /// </summary>
        /// <returns>The Buckets64 object the reset operation was performed on.</returns>
        internal Buckets64 Reset()
        {
            AllocateArray(this.count, this.bucketSize);
            return this;
        }

        /// <summary>
        /// Returns the bits at the specified offset and length.
        /// </summary>
        /// <param name="offset">The position to start reading at.</param>
        /// <param name="length">The distance to read from the offset.</param>
        /// <returns>The bits at the specified offset and length.</returns>
        internal uint GetBits(ulong offset, int length)
        {
            ulong byteIndex = offset / 8;
            int byteOffset = (int)(offset % 8);

            if ((byteOffset + length) > 8)
            {
                int rem = 8 - byteOffset;
                return GetBits(offset, rem)
                    | (GetBits(offset + (ulong)rem, length - rem) << rem);
            }

            var dataArray = this.Data[byteIndex / maxArraySize];
            var dataArrayByteIndex = byteIndex % maxArraySize;
            int bitMask = (1 << length) - 1;
            return (uint)((dataArray[dataArrayByteIndex] & (bitMask << byteOffset)) >> byteOffset);
        }

        /// <summary>
        /// Sets bits at the specified offset and length.
        /// </summary>
        /// <param name="offset">The position to start writing at.</param>
        /// <param name="length">The distance to write from the offset.</param>
        /// <param name="bits">The bits to write.</param>
        internal void SetBits(ulong offset, int length, uint bits)
        {
            ulong byteIndex = offset / 8;
            int byteOffset = (int)(offset % 8);

            if ((byteOffset + length) > 8)
            {
                int rem = 8 - byteOffset;
                SetBits(offset, (byte)rem, bits);
                SetBits(offset + (ulong)rem, length - rem, bits >> rem);
                return;
            }

            var dataArray = this.Data[(uint)(byteIndex / maxArraySize)];
            var dataArrayByteIndex = (uint)(byteIndex % maxArraySize);
            int bitMask = (1 << length) - 1;
            dataArray[dataArrayByteIndex] =
                (byte)((dataArray[dataArrayByteIndex]) & ~(bitMask << byteOffset));
            dataArray[dataArrayByteIndex] =
                (byte)((dataArray[dataArrayByteIndex]) | ((bits & bitMask) << byteOffset));
        }
    }
}
