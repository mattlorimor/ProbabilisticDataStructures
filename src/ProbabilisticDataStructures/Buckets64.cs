using System;
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
    public class Buckets64
    {
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
                // TODO: Figure out this truncation thing.
                // I'm not sure if MaxValue is always supposed to be capped at 255 via
                // a byte conversion or not...
                if (value > byte.MaxValue)
                    _max = byte.MaxValue;
                else
                    _max = (byte)value;
            }
        }
        internal ulong count { get; set; }

        /// <summary>
        /// Creates a new Buckets64 with the provided number of buckets where each bucket
        /// is the specified number of bits.
        /// </summary>
        /// <param name="count">Number of buckets.</param>
        /// <param name="bucketSize">Number of bits per bucket.</param>
        internal Buckets64(ulong count, byte bucketSize)
        {
            this.count = count;
            this.bucketSize = bucketSize;
            AllocateArray(count, bucketSize);
            this.Max = (1 << bucketSize) - 1;
        }

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
