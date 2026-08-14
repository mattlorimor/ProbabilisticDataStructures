using System;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Buckets is a fast, space-efficient array of buckets where each bucket can store
    /// up to a configured maximum value.
    /// </summary>
    /// <remarks>
    /// Internal: every member on this type already was, so as a public type it offered
    /// a consumer nothing but a name in their completion list. It is the packed array
    /// the filters are built on, not part of what this library is for.
    /// </remarks>
    internal class Buckets
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

        private byte[] Data { get; set; }
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
        internal uint count { get; set; }

        /// <summary>
        /// The packed bucket data, for persistence. Exposed rather than copied because
        /// this is the largest thing a filter holds.
        /// </summary>
        internal ReadOnlySpan<byte> RawData => this.Data;

        /// <summary>
        /// The width of each bucket, in bits, for persistence.
        /// </summary>
        internal byte BucketSize => this.bucketSize;

        /// <summary>
        /// Rebuilds a Buckets from data previously taken from <see cref="RawData"/>.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// The data is not the length the count and bucket size imply.
        /// </exception>
        internal static Buckets Restore(uint count, byte bucketSize, byte[] data)
        {
            var buckets = new Buckets(count, bucketSize);

            if (data.Length != buckets.Data.Length)
            {
                throw new InvalidDataException(
                    $"{count} buckets of {bucketSize} bits need {buckets.Data.Length} " +
                    $"bytes and {data.Length} were stored.");
            }

            buckets.Data = data;
            return buckets;
        }

        /// <summary>
        /// Creates a new Buckets with the provided number of buckets where each bucket
        /// is the specified number of bits.
        /// </summary>
        /// <param name="count">Number of buckets.</param>
        /// <param name="bucketSize">Number of bits per bucket.</param>
        internal Buckets(uint count, byte bucketSize)
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
            this.Data = new byte[(count * bucketSize + 7) / 8];
            this.bucketSize = bucketSize;
            this.Max = (1 << bucketSize) - 1;
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
        internal Buckets Increment(uint bucket, int delta)
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
        internal Buckets Set(uint bucket, byte value)
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
        internal uint Get(uint bucket)
        {
            return GetBits(bucket * this.bucketSize, this.bucketSize);
        }

        /// <summary>
        /// Sets every bucket to the larger of its own value and the matching bucket in
        /// <paramref name="other"/>.
        /// </summary>
        /// <remarks>
        /// For single-bit buckets this is a bitwise OR, which is what unioning two
        /// Bloom filters means. For wider ones it takes the maximum rather than the
        /// sum, which is what a set union wants: a bucket records whether something
        /// landed there, not how many times.
        /// </remarks>
        internal Buckets Union(Buckets other)
        {
            if (this.count != other.count || this.bucketSize != other.bucketSize)
            {
                throw new ArgumentException(
                    $"Cannot union {this.count} buckets of {this.bucketSize} bits with " +
                    $"{other.count} of {other.bucketSize}.", nameof(other));
            }

            if (this.bucketSize == 1)
            {
                // The common case, and worth taking whole bytes at a time rather than
                // one bit at a time.
                for (int i = 0; i < this.Data.Length; i++)
                {
                    this.Data[i] |= other.Data[i];
                }

                return this;
            }

            for (uint i = 0; i < this.count; i++)
            {
                var theirs = other.Get(i);
                if (theirs > this.Get(i))
                {
                    this.Set(i, (byte)theirs);
                }
            }

            return this;
        }

        /// <summary>
        /// Adds each of <paramref name="other"/>'s counters to its counterpart, holding
        /// at the maximum a bucket can carry.
        /// </summary>
        /// <remarks>
        /// A counter that reaches its maximum has stopped tracking how many elements it
        /// stands for and is never decremented again, which is what keeps a counting
        /// filter's deletions from producing false negatives. Adding two filters can
        /// carry a counter over that line when neither was near it, and the clamp is
        /// what keeps the invariant.
        /// </remarks>
        internal Buckets Add(Buckets other)
        {
            if (this.count != other.count || this.bucketSize != other.bucketSize)
            {
                throw new ArgumentException(
                    $"Cannot add {this.count} buckets of {this.bucketSize} bits to " +
                    $"{other.count} of {other.bucketSize}.", nameof(other));
            }

            for (uint i = 0; i < this.count; i++)
            {
                var sum = this.Get(i) + other.Get(i);
                this.Set(i, sum > this._max ? this._max : (byte)sum);
            }

            return this;
        }

        /// <summary>
        /// Restores the Buckets to the original state. Returns itself to allow for
        /// chaining.
        /// </summary>
        /// <returns>The Buckets object the reset operation was performed on.</returns>
        internal Buckets Reset()
        {
            this.Data = new byte[(this.count * this.bucketSize + 7) / 8];
            return this;
        }

        /// <summary>
        /// Returns the bits at the specified offset and length.
        /// </summary>
        /// <param name="offset">The position to start reading at.</param>
        /// <param name="length">The distance to read from the offset.</param>
        /// <returns>The bits at the specified offset and length.</returns>
        internal uint GetBits(uint offset, int length)
        {
            uint byteIndex = offset / 8;
            int byteOffset = (int)(offset % 8);

            if ((byteOffset + length) > 8)
            {
                int rem = 8 - byteOffset;
                return GetBits(offset, rem)
                    | (GetBits((uint)(offset + rem), length - rem) << rem);
            }

            int bitMask = (1 << length) - 1;
            return (uint)((this.Data[byteIndex] & (bitMask << byteOffset)) >> byteOffset);
        }

        /// <summary>
        /// Sets bits at the specified offset and length.
        /// </summary>
        /// <param name="offset">The position to start writing at.</param>
        /// <param name="length">The distance to write from the offset.</param>
        /// <param name="bits">The bits to write.</param>
        internal void SetBits(uint offset, int length, uint bits)
        {
            uint byteIndex = offset / 8;
            int byteOffset = (int)(offset % 8);

            if ((byteOffset + length) > 8)
            {
                int rem = 8 - byteOffset;
                SetBits(offset, (byte)rem, bits);
                SetBits((uint)(offset + rem), length - rem, bits >> rem);
                return;
            }

            int bitMask = (1 << length) - 1;
            this.Data[byteIndex] =
                (byte)((this.Data[byteIndex]) & ~(bitMask << byteOffset));
            this.Data[byteIndex] =
                (byte)((this.Data[byteIndex]) | ((bits & bitMask) << byteOffset));
        }
    }
}
