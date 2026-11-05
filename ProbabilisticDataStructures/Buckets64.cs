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
        internal const uint maxArraySize = 1U << MaxArrayBits;

        /// <summary>The bits of <see cref="maxArraySize"/>: arrays are 2^30 bytes.</summary>
        private const int MaxArrayBits = 30;

        /// <summary>
        /// Smaller arrays for the buckets built or restored on the calling thread, of
        /// 2^this bytes, from 0 to 30, so that the suite can build buckets of several arrays
        /// at sizes it can hold; null otherwise, as it is in every thread but a test's.
        /// </summary>
        /// <remarks>
        /// Per thread because the suite runs its tests side by side, and every other test's
        /// buckets, and so its payloads, must be laid out as they always are. The size is read
        /// once, when buckets are built or restored, and they keep it: they index their arrays
        /// as they were laid out on whatever thread uses them afterwards, and a reset clears
        /// the arrays they have. The size is not written, so a payload of buckets of several
        /// lowered arrays holds more arrays than its count implies at the real size, and a
        /// reader on a thread that has not lowered the size to the same refuses it.
        /// </remarks>
        [ThreadStatic]
        internal static int? LoweredArrayBits;

        /// <summary>
        /// The bits of the size of the arrays buckets are built and restored at on this
        /// thread: <see cref="MaxArrayBits"/>, unless the suite has lowered it.
        /// </summary>
        private static int ArrayBits => LoweredArrayBits ?? MaxArrayBits;

        /// <summary>
        /// The bytes of every array but the last, for buckets built or restored on this
        /// thread: <see cref="maxArraySize"/>, unless the suite has lowered it.
        /// </summary>
        internal static ulong ArraySize => 1UL << ArrayBits;

        /// <summary>
        /// The most buckets this type holds. The arrays are held in one array, so there
        /// are at most <see cref="Array.MaxLength"/> of them, and a bucket count has as
        /// many arrays as it has multiples of <see cref="ArraySize"/>, plus one. Past
        /// this the count of arrays does not fit, and was cast to an int that wrapped.
        /// </summary>
        internal static ulong MaxCount => (ulong)Array.MaxLength * ArraySize - 1;

        /// <summary>
        /// The bits of the size of these buckets' arrays, read from <see cref="ArrayBits"/>
        /// when they were built or restored, so that indexing them does not depend on the
        /// thread that uses them.
        /// </summary>
        private readonly int arrayBits;

        /// <summary>
        /// One less than the size of these buckets' arrays, the low bits of a byte's index
        /// that place it in its array. Held rather than worked out on every read and write.
        /// </summary>
        private readonly ulong arrayMask;
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
        /// <remarks>
        /// The arrays read are kept, and checked against the shape the count implies
        /// without allocating it. Restoring used to allocate that shape, compare, and
        /// throw it away: a second copy of the filter's bits, and one a payload's count
        /// alone could size before the comparison refused it.
        /// </remarks>
        internal static Buckets64 Restore(ulong count, byte bucketSize, byte[][] data)
        {
            CheckStoredBucketSize(bucketSize);

            if (count > MaxCount)
            {
                throw new InvalidDataException(
                    $"{count:N0} buckets are more than the {MaxCount:N0} this type holds, " +
                    "which is beyond anything this library builds.");
            }

            var arrayBits = ArrayBits;
            var arraySize = 1UL << arrayBits;
            var arrays = count / arraySize + 1;
            if ((ulong)data.Length != arrays)
            {
                throw new InvalidDataException(
                    $"{count} buckets of {bucketSize} bits need {arrays} " +
                    $"arrays and {data.Length} were stored.");
            }

            var bytesLeft = (count * bucketSize + 7) / 8;
            for (int i = 0; i < data.Length; i++)
            {
                var needed = Math.Min(bytesLeft, arraySize);
                if ((ulong)data[i].Length != needed)
                {
                    throw new InvalidDataException(
                        $"Array {i} of the bucket data needs {needed} " +
                        $"bytes and {data[i].Length} were stored.");
                }
                bytesLeft -= needed;
            }

            return new Buckets64(count, bucketSize, data, arrayBits);
        }

        /// <summary>
        /// Lays out buckets held as one array, as a <see cref="Buckets"/> holds them and a
        /// payload before format version 14 stores them, as the arrays of this type: the
        /// array is kept as the first where it fits one, and split into arrays of
        /// <see cref="ArraySize"/> where it does not.
        /// </summary>
        /// <param name="count">Number of buckets.</param>
        /// <param name="bucketSize">Number of bits per bucket.</param>
        /// <param name="data">
        /// The packed buckets, already checked to be the length the count and bucket size
        /// imply. A bucket is packed in it where a Buckets64 packs it, so its bytes are the
        /// bytes of the arrays one after another.
        /// </param>
        /// <remarks>
        /// Every filter of one-bit buckets stored before version 14 holds at most 2^32 - 1
        /// bits, half an array, so is kept whole. Wider buckets can run longer than an
        /// array, and are then copied into arrays of their own once, which a read is charged
        /// for. The arrays past the data, which the count implies, hold nothing and are the
        /// one empty array.
        /// </remarks>
        internal static Buckets64 FromOneArray(ulong count, byte bucketSize, byte[] data)
        {
            var arraySize = ArraySize;
            var arrays = new byte[count / arraySize + 1][];
            if ((ulong)data.Length <= arraySize)
            {
                arrays[0] = data;
                for (var i = 1; i < arrays.Length; i++)
                {
                    arrays[i] = Array.Empty<byte>();
                }
            }
            else
            {
                var at = 0UL;
                for (var i = 0; i < arrays.Length; i++)
                {
                    var piece = Math.Min((ulong)data.Length - at, arraySize);
                    arrays[i] = piece == 0 ? Array.Empty<byte>() : data.AsSpan((int)at, (int)piece).ToArray();
                    at += piece;
                }
            }

            return Restore(count, bucketSize, arrays);
        }

        private Buckets64(ulong count, byte bucketSize, byte[][] data, int arrayBits)
        {
            this.arrayBits = arrayBits;
            this.arrayMask = (1UL << arrayBits) - 1;
            this.count = count;
            this.bucketSize = bucketSize;
            this.arrayCount = data.Length;
            this.Data = data;
            this.Max = (1 << bucketSize) - 1;
        }

        /// <summary>
        /// Refuses a bucket width read from a payload that no bucket has, as the payload's
        /// fault, before the width shapes anything.
        /// </summary>
        /// <exception cref="InvalidDataException">The width is none, or more than a byte.</exception>
        /// <remarks>
        /// The constructors' guard is about a parameter, and a payload is not one: a stored
        /// width of 0 or 9 is what a damaged or hostile payload says, and is refused as
        /// every payload the library will not read is.
        /// </remarks>
        private static void CheckStoredBucketSize(byte bucketSize)
        {
            if (bucketSize == 0 || bucketSize > MaxBucketSizeBits)
            {
                throw new InvalidDataException(
                    $"Bucket data claims buckets of {bucketSize} bits, and a bucket is 1 to " +
                    $"{MaxBucketSizeBits} bits wide.");
            }
        }

        private static void ValidBucketSize(byte bucketSize)
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
        }

        /// <summary>
        /// Refuses a size this type cannot build, naming the parameter the caller knows
        /// it by.
        /// </summary>
        /// <param name="count">Number of buckets.</param>
        /// <param name="bucketSize">Number of bits per bucket.</param>
        /// <param name="paramName">The parameter the refusal is reported against.</param>
        /// <param name="request">
        /// What was asked for, as the caller knows it, in place of the bare count of
        /// buckets: a sentence's worth, without its full stop.
        /// </param>
        /// <param name="remedy">What the caller can change, as a sentence.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The buckets would need more arrays than the outer array holds, or the width is
        /// not one this type holds.
        /// </exception>
        internal static void CheckSize(
            ulong count, byte bucketSize, string paramName,
            string? request = null, string? remedy = null)
        {
            ValidBucketSize(bucketSize);

            if (count > MaxCount)
            {
                throw new ArgumentOutOfRangeException(paramName, count,
                    $"{request ?? $"{count:N0} buckets"}. That is {count / ArraySize + 1:N0} " +
                    $"arrays of up to {ArraySize:N0} bytes, and the arrays are held in one, " +
                    $"which holds at most {Array.MaxLength:N0}: {MaxCount:N0} buckets.{(remedy is null ? "" : " " + remedy)}");
            }
        }

        /// <summary>
        /// Creates a new Buckets64 with the provided number of buckets where each bucket
        /// is the specified number of bits.
        /// </summary>
        /// <param name="count">Number of buckets.</param>
        /// <param name="bucketSize">Number of bits per bucket.</param>
        internal Buckets64(ulong count, byte bucketSize)
        {
            CheckSize(count, bucketSize, nameof(count));

            this.arrayBits = ArrayBits;
            this.arrayMask = (1UL << this.arrayBits) - 1;
            this.count = count;
            this.bucketSize = bucketSize;
            AllocateArray(count, bucketSize);
            this.Max = (1 << bucketSize) - 1;
        }

        [MemberNotNull(nameof(Data))]
        private void AllocateArray(ulong count, byte bucketSize)
        {
            var largest = 1UL << this.arrayBits;
            this.arrayCount = (int)(count / largest + 1);
            this.Data = new byte[this.arrayCount][];
            var bytesToAllocate = (count * bucketSize + 7) / 8;
            for (int i = 0; i < this.arrayCount; i++)
            {
                var arraySize = Math.Min(bytesToAllocate, largest);
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
        internal Buckets64 Increment(ulong bucket, int delta)
        {
            int val = (int)(Get(bucket) + delta);

            if (val > this.Max)
                val = this.Max;
            else if (val < 0)
                val = 0;

            Set(bucket, (byte)val);
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
        /// Sets every bucket to the larger of its own value and the matching bucket in
        /// <paramref name="other"/>. See <see cref="Buckets.Union"/>.
        /// </summary>
        internal Buckets64 Union(Buckets64 other)
        {
            if (this.count != other.count || this.bucketSize != other.bucketSize)
            {
                throw new ArgumentException(
                    $"Cannot union {this.count} buckets of {this.bucketSize} bits with " +
                    $"{other.count} of {other.bucketSize}.", nameof(other));
            }

            if (this.bucketSize == 1)
            {
                for (int a = 0; a < this.Data.Length; a++)
                {
                    var mine = this.Data[a];
                    var theirs = other.Data[a];
                    for (int i = 0; i < mine.Length; i++)
                    {
                        mine[i] |= theirs[i];
                    }
                }

                return this;
            }

            for (ulong i = 0; i < this.count; i++)
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
        /// Adds each of <paramref name="other"/>'s counters to its counterpart, holding at
        /// the maximum a bucket can carry. See <see cref="Buckets.Add"/>, whose rule this is.
        /// </summary>
        /// <remarks>
        /// A bucket at a time, through each table's own layout, so two tables split into
        /// arrays at different sizes add as two of one array do. The sum is held at the
        /// maximum before it is narrowed: at eight bits a sum can pass 255, and cast to a
        /// byte first it would wrap to a small count, under what the two counters stood for.
        /// </remarks>
        internal Buckets64 Add(Buckets64 other)
        {
            if (this.count != other.count || this.bucketSize != other.bucketSize)
            {
                throw new ArgumentException(
                    $"Cannot add {this.count} buckets of {this.bucketSize} bits to " +
                    $"{other.count} of {other.bucketSize}.", nameof(other));
            }

            for (ulong i = 0; i < this.count; i++)
            {
                var sum = this.Get(i) + other.Get(i);
                this.Set(i, sum > this._max ? this._max : (byte)sum);
            }

            return this;
        }

        /// <summary>
        /// Restores the Buckets64 to the original state. Returns itself to allow for
        /// chaining.
        /// </summary>
        /// <returns>The Buckets64 object the reset operation was performed on.</returns>
        /// <remarks>
        /// Each array is cleared where it is, as <see cref="Buckets.Reset"/> clears its one.
        /// Allocating them again built the whole filter anew at every reset, gigabytes for a
        /// large one (#297). The arrays are always the number and lengths the count and
        /// bucket size imply -- the constructor allocates them so and <see cref="Restore"/>
        /// refuses any other -- and nothing outside holds them: <see cref="RawData"/> is read
        /// by the writer that asks for it and dropped, and the arrays a restore keeps are
        /// the copies its reader made.
        /// </remarks>
        internal Buckets64 Reset()
        {
            foreach (var array in this.Data)
            {
                Array.Clear(array);
            }

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

            var dataArray = this.Data[byteIndex >> this.arrayBits];
            var dataArrayByteIndex = byteIndex & this.arrayMask;
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

            var dataArray = this.Data[(uint)(byteIndex >> this.arrayBits)];
            var dataArrayByteIndex = (uint)(byteIndex & this.arrayMask);
            int bitMask = (1 << length) - 1;
            dataArray[dataArrayByteIndex] =
                (byte)((dataArray[dataArrayByteIndex]) & ~(bitMask << byteOffset));
            dataArray[dataArrayByteIndex] =
                (byte)((dataArray[dataArrayByteIndex]) | ((bits & bitMask) << byteOffset));
        }
    }
}
