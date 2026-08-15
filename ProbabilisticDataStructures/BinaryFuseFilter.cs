using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// The width of a binary fuse filter's fingerprints, in bits, which is the only
    /// thing that sets its false positive rate.
    /// </summary>
    /// <remarks>
    /// The rate is 2^-width, so the choice is between roughly 0.39% at one byte per
    /// entry and 0.0015% at two. There is nothing in between: a fingerprint narrower
    /// than a byte would have to be packed across byte boundaries, which costs more in
    /// lookup than it saves in space.
    /// </remarks>
    public enum BinaryFuseWidth : byte
    {
        /// <summary>One byte per entry, for a false positive rate of about 0.39%.</summary>
        Eight = 8,

        /// <summary>Two bytes per entry, for a false positive rate of about 0.0015%.</summary>
        Sixteen = 16,
    }
    /// <summary>
    /// A binary fuse filter: a membership filter for a set that is known in full when
    /// the filter is built and never changes afterwards.
    /// </summary>
    /// <remarks>
    /// Graf and Lemire, "Binary Fuse Filters: Fast and Smaller Than Xor Filters" (2022),
    /// which follows their earlier xor filters.
    /// <para>
    /// Every other filter in this library is built empty and added to. This one is not,
    /// and cannot be: constructing it solves a system of equations over the whole set at
    /// once, so there is no <c>Add</c> and no way to write one. What that buys is a
    /// filter around 13% smaller than a Bloom filter at the same false positive rate,
    /// answering in three memory accesses rather than a loop over k hash functions.
    /// </para>
    /// <para>
    /// It therefore does not implement <see cref="IFilter"/>, whose contract is mostly
    /// about adding. A caller with a fixed set -- a blocklist, a shipped index, a
    /// compiled artifact -- pays nothing here for the incremental insertion they were
    /// never going to use.
    /// </para>
    /// <para>
    /// Construction is peeling-based and can fail on a given seed, which is handled by
    /// retrying with another. The failure probability per attempt is small enough that
    /// exhausting <see cref="MaxBuildAttempts"/> of them means something is wrong with
    /// the input rather than with the luck.
    /// </para>
    /// </remarks>
    public class BinaryFuseFilter : IBinaryPersistable<BinaryFuseFilter>
    {
        /// <summary>
        /// The number of positions each key occupies, and so the number of memory
        /// accesses a lookup costs. Three is the arity the paper's parameters are
        /// tuned for.
        /// </summary>
        private const int Arity = 3;

        /// <summary>
        /// How many seeds to try before giving up. Construction fails for a seed with
        /// probability well under a percent, so reaching this many failures in a row is
        /// not bad luck -- it means the input is not what the sizing assumed.
        /// </summary>
        private const int MaxBuildAttempts = 100;

        /// <summary>
        /// The paper's segment length cap. Beyond it the working set stops fitting in
        /// cache and the construction slows without getting smaller.
        /// </summary>
        private const uint MaxSegmentLength = 262144;

        private uint segmentLength;
        private uint segmentLengthMask;
        private uint segmentCount;
        private uint segmentCountLength;
        private uint arrayLength;
        private ulong seed;
        private uint size;
        private BinaryFuseWidth width;
        private byte[] fingerprints = null!;

        /// <summary>
        /// How many seeds the peel needed. One means it succeeded first time, which is
        /// the overwhelmingly common case; the tests use this to find the sizes where
        /// the retry path actually runs rather than assuming it does.
        /// </summary>
        internal int AttemptsUsed { get; private set; }

        /// <summary>Bytes per fingerprint, which the width fixes at one or two.</summary>
        private int Stride => (int)this.width / 8;

        /// <summary>The bits a fingerprint keeps, given the width.</summary>
        private ushort Mask => this.width == BinaryFuseWidth.Eight ? (ushort)0xFF : (ushort)0xFFFF;

        internal Func<ReadOnlySpan<byte>, ulong> Hash { get; set; } = null!;

        private BinaryFuseFilter()
        {
        }

        /// <summary>
        /// Builds a filter holding exactly the given items.
        /// </summary>
        /// <param name="items">
        /// The set the filter will answer for. Duplicates are collapsed; the order they
        /// arrive in does not affect what the filter holds.
        /// </param>
        /// <returns>A filter that answers yes to every one of those items.</returns>
        /// <exception cref="ArgumentNullException">
        /// The sequence, or one of its items, is null.
        /// </exception>
        public static BinaryFuseFilter Build(IEnumerable<byte[]> items)
        {
            return Build(items, BinaryFuseWidth.Eight, null);
        }

        /// <summary>
        /// Builds a filter holding exactly the given items, with the given fingerprint
        /// width.
        /// </summary>
        /// <param name="items">The set the filter will answer for.</param>
        /// <param name="width">
        /// The fingerprint width, which fixes the false positive rate at 2^-width.
        /// </param>
        /// <returns>A filter that answers yes to every one of those items.</returns>
        public static BinaryFuseFilter Build(IEnumerable<byte[]> items, BinaryFuseWidth width)
        {
            return Build(items, width, null);
        }

        /// <summary>
        /// Builds a filter holding exactly the given items, wide enough to meet the
        /// given false positive rate.
        /// </summary>
        /// <param name="items">The set the filter will answer for.</param>
        /// <param name="fpRate">
        /// The worst false positive rate the caller will accept. The narrowest width
        /// that meets it is used, so the rate delivered is at least as good as the one
        /// asked for and usually better -- only two widths exist, and the gap between
        /// them is a factor of 256.
        /// </param>
        /// <returns>A filter that answers yes to every one of those items.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The rate is not between zero and one, or is tighter than the widest
        /// fingerprint can deliver.
        /// </exception>
        public static BinaryFuseFilter Build(IEnumerable<byte[]> items, double fpRate)
        {
            return Build(items, WidthFor(fpRate), null);
        }

        /// <summary>
        /// The narrowest width whose rate is no worse than the one asked for.
        /// </summary>
        private static BinaryFuseWidth WidthFor(double fpRate)
        {
            Guard.ValidFalsePositiveRate(fpRate, nameof(fpRate));

            if (RateFor(BinaryFuseWidth.Eight) <= fpRate)
            {
                return BinaryFuseWidth.Eight;
            }

            if (RateFor(BinaryFuseWidth.Sixteen) <= fpRate)
            {
                return BinaryFuseWidth.Sixteen;
            }

            throw new ArgumentOutOfRangeException(
                nameof(fpRate), fpRate,
                $"A false positive rate of {fpRate} is tighter than the widest " +
                $"fingerprint delivers, which is {RateFor(BinaryFuseWidth.Sixteen)}. " +
                "Widening further would cost more per entry than a Bloom filter at the " +
                "same rate, which is the point at which this structure stops being the " +
                "one to use.");
        }

        private static double RateFor(BinaryFuseWidth width)
        {
            return Math.Pow(2, -(int)width);
        }

        /// <summary>
        /// Builds a filter holding exactly the given items, hashing them with the
        /// supplied function rather than the default.
        /// </summary>
        /// <param name="items">The set the filter will answer for.</param>
        /// <param name="width">
        /// The fingerprint width, which fixes the false positive rate at 2^-width.
        /// </param>
        /// <param name="hash">
        /// The hash function to use, or null for the default. There is no SetHash on
        /// this filter: the set is hashed during construction, so a hash chosen
        /// afterwards could not apply to anything already held.
        /// </param>
        /// <returns>A filter that answers yes to every one of those items.</returns>
        public static BinaryFuseFilter Build(
            IEnumerable<byte[]> items,
            BinaryFuseWidth width,
            Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(items);

            if (!Enum.IsDefined(width))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(width), width, "Fingerprints are one or two bytes wide.");
            }

            var hashFunction = hash ?? Defaults.GetDefaultHashFunction();
            var keys = HashDistinctly(items, hashFunction);

            var filter = new BinaryFuseFilter { Hash = hashFunction, width = width };
            filter.Shape((uint)keys.Length);
            filter.Populate(keys);
            return filter;
        }

        /// <summary>
        /// Hashes every item and reduces the result to the distinct keys, which is what
        /// the construction actually operates on.
        /// </summary>
        /// <remarks>
        /// Sorting to find duplicates costs O(n log n) where the reference
        /// implementation detects them during the peel and stays O(n). It is worth the
        /// difference here: the filter is built once and read many times, sorting a few
        /// million longs is quick, and the alternative threads duplicate bookkeeping
        /// through the most delicate loop in the algorithm.
        /// <para>
        /// Two distinct items that hash alike are one key, because nothing downstream
        /// could tell them apart anyway.
        /// </para>
        /// </remarks>
        private static ulong[] HashDistinctly(
            IEnumerable<byte[]> items, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            var keys = new List<ulong>();
            foreach (var item in items)
            {
                ArgumentNullException.ThrowIfNull(item, nameof(items));
                keys.Add(hash(item));
            }

            var array = keys.ToArray();
            Array.Sort(array);

            var distinct = 0;
            for (var i = 0; i < array.Length; i++)
            {
                if (i == 0 || array[i] != array[i - 1])
                {
                    array[distinct++] = array[i];
                }
            }

            Array.Resize(ref array, distinct);
            return array;
        }

        /// <summary>
        /// Works out the segment geometry for a set of the given size, following the
        /// paper's parameters.
        /// </summary>
        private void Shape(uint keyCount)
        {
            this.size = keyCount;
            this.segmentLength = keyCount == 0 ? 4 : SegmentLengthFor(keyCount);

            if (this.segmentLength > MaxSegmentLength)
            {
                this.segmentLength = MaxSegmentLength;
            }

            this.segmentLengthMask = this.segmentLength - 1;

            var sizeFactor = keyCount <= 1 ? 0 : SizeFactorFor(keyCount);
            var capacity = keyCount <= 1 ? 0 : (uint)Math.Round(keyCount * sizeFactor);

            // The subtraction underflows for a capacity below one segment, and the
            // multiplication below wraps it back to zero, which the segment count check
            // then turns into a single segment. That is the reference implementation's
            // behavior and the small-set sizing depends on it, so it is spelled out as
            // deliberate rather than left to look like an oversight.
            unchecked
            {
                var initialSegments =
                    (capacity + this.segmentLength - 1) / this.segmentLength - (Arity - 1);
                this.arrayLength = (initialSegments + Arity - 1) * this.segmentLength;
            }

            this.segmentCount =
                (this.arrayLength + this.segmentLength - 1) / this.segmentLength;
            this.segmentCount = this.segmentCount <= Arity - 1
                ? 1
                : this.segmentCount - (Arity - 1);

            this.arrayLength = (this.segmentCount + Arity - 1) * this.segmentLength;
            this.segmentCountLength = this.segmentCount * this.segmentLength;
            this.fingerprints = new byte[(long)this.arrayLength * this.Stride];
        }

        private static uint SegmentLengthFor(uint keyCount)
        {
            return 1u << (int)Math.Floor(Math.Log(keyCount) / Math.Log(3.33) + 2.25);
        }

        private static double SizeFactorFor(uint keyCount)
        {
            return Math.Max(1.125, 0.875 + 0.25 * Math.Log(1000000.0) / Math.Log(keyCount));
        }

        /// <summary>
        /// Solves the system for the given keys, retrying with a fresh seed when the
        /// peel does not consume every key.
        /// </summary>
        private void Populate(ulong[] keys)
        {
            if (this.size == 0)
            {
                return;
            }

            var reverseOrder = new ulong[this.size + 1];
            var reverseH = new byte[this.size];
            var alone = new uint[this.arrayLength];
            var t2count = new byte[this.arrayLength];
            var t2hash = new ulong[this.arrayLength];

            var blockBits = 1;
            while ((1u << blockBits) < this.segmentCount)
            {
                blockBits++;
            }

            var block = 1u << blockBits;
            var startPos = new uint[block];

            // The seed sequence is fixed, so building the same set twice gives the same
            // filter. Nothing here needs unpredictability: the seed exists to give the
            // peel another arrangement to try, not to be unguessable.
            var rng = new SeededRandom(0x726b2b9d438b9d4d);
            this.seed = rng.Next();

            // Guards the scan below, which walks forward until it finds a free slot.
            reverseOrder[this.size] = 1;

            for (var attempt = 0; ; attempt++)
            {
                if (attempt >= MaxBuildAttempts)
                {
                    throw new InvalidOperationException(
                        $"Could not build a binary fuse filter over {this.size} keys in " +
                        $"{MaxBuildAttempts} attempts. Each attempt fails independently " +
                        "and rarely, so this many failures means the keys are not what " +
                        "the sizing assumes rather than that the seeds were unlucky.");
                }

                this.AttemptsUsed = attempt + 1;

                if (this.TryPeel(keys, reverseOrder, reverseH, alone, t2count, t2hash,
                                 startPos, block, blockBits))
                {
                    break;
                }

                Array.Clear(reverseOrder, 0, (int)this.size);
                Array.Clear(t2count, 0, (int)this.arrayLength);
                Array.Clear(t2hash, 0, (int)this.arrayLength);
                this.seed = rng.Next();
            }

            this.Assign(reverseOrder, reverseH);
        }

        /// <summary>
        /// One attempt at the peel: distributes the keys, then repeatedly removes a key
        /// that is alone in one of its three positions. Succeeds if every key comes out.
        /// </summary>
        private bool TryPeel(
            ulong[] keys, ulong[] reverseOrder, byte[] reverseH, uint[] alone,
            byte[] t2count, ulong[] t2hash, uint[] startPos, uint block, int blockBits)
        {
            for (uint i = 0; i < block; i++)
            {
                // As a 32-bit multiply this would overflow for large sets.
                startPos[i] = (uint)(((ulong)i * this.size) >> blockBits);
            }

            var maskBlock = block - 1;
            for (uint i = 0; i < this.size; i++)
            {
                var hash = Murmur64(keys[i] + this.seed);
                var segment = hash >> (64 - blockBits);

                while (reverseOrder[startPos[segment]] != 0)
                {
                    segment++;
                    segment &= maskBlock;
                }

                reverseOrder[startPos[segment]] = hash;
                startPos[segment]++;
            }

            // Each slot accumulates a count in the high bits and the xor of which of the
            // three positions each key occupied in the low two, so that a slot holding
            // exactly one key says which key and where.
            var overflowed = false;
            for (uint i = 0; i < this.size; i++)
            {
                var hash = reverseOrder[i];

                for (var position = 0; position < Arity; position++)
                {
                    var index = this.PositionOf(position, hash);
                    t2count[index] += 4;
                    t2count[index] ^= (byte)position;
                    t2hash[index] ^= hash;

                    overflowed |= t2count[index] < 4;
                }
            }

            if (overflowed)
            {
                return false;
            }

            uint queued = 0;
            for (uint i = 0; i < this.arrayLength; i++)
            {
                alone[queued] = i;
                queued += (t2count[i] >> 2) == 1 ? 1u : 0u;
            }

            uint peeled = 0;
            while (queued > 0)
            {
                queued--;
                var index = alone[queued];

                if ((t2count[index] >> 2) != 1)
                {
                    continue;
                }

                var hash = t2hash[index];
                var found = (byte)(t2count[index] & 3);

                reverseH[peeled] = found;
                reverseOrder[peeled] = hash;
                peeled++;

                for (var offset = 1; offset <= 2; offset++)
                {
                    var other = Mod3(found + offset);
                    var otherIndex = this.PositionOf(other, hash);

                    alone[queued] = otherIndex;
                    queued += (t2count[otherIndex] >> 2) == 2 ? 1u : 0u;

                    t2count[otherIndex] -= 4;
                    t2count[otherIndex] ^= other;
                    t2hash[otherIndex] ^= hash;
                }
            }

            return peeled == this.size;
        }

        /// <summary>
        /// Walks the peel backwards, giving each key the fingerprint that makes its
        /// three positions xor to zero. Reverse order is what makes this work: a key is
        /// assigned only after every key that was peeled on top of it.
        /// </summary>
        private void Assign(ulong[] reverseOrder, byte[] reverseH)
        {
            Span<uint> positions = stackalloc uint[Arity + 2];

            for (var i = this.size; i-- > 0;)
            {
                var hash = reverseOrder[i];
                var found = reverseH[i];

                positions[0] = this.PositionOf(0, hash);
                positions[1] = this.PositionOf(1, hash);
                positions[2] = this.PositionOf(2, hash);
                positions[3] = positions[0];
                positions[4] = positions[1];

                var value = this.FingerprintOf(hash)
                    ^ this.FingerprintAt(positions[found + 1])
                    ^ this.FingerprintAt(positions[found + 2]);

                this.SetFingerprintAt(positions[found], (ushort)value);
            }
        }

        /// <summary>
        /// Where a key's nth copy lives: a base position from the whole array, offset by
        /// one segment per position, then perturbed within that segment.
        /// </summary>
        private uint PositionOf(int position, ulong hash)
        {
            var index = Math.BigMul(hash, this.segmentCountLength, out _);
            index += (ulong)position * this.segmentLength;

            var low = hash & ((1UL << 36) - 1);
            index ^= (low >> (36 - 18 * position)) & this.segmentLengthMask;

            return (uint)index;
        }

        private ushort FingerprintOf(ulong hash)
        {
            return (ushort)((hash ^ (hash >> 32)) & this.Mask);
        }

        private ushort FingerprintAt(uint index)
        {
            return this.width == BinaryFuseWidth.Eight
                ? this.fingerprints[index]
                : BinaryPrimitives.ReadUInt16LittleEndian(
                    this.fingerprints.AsSpan((int)index * 2));
        }

        private void SetFingerprintAt(uint index, ushort value)
        {
            if (this.width == BinaryFuseWidth.Eight)
            {
                this.fingerprints[index] = (byte)value;
                return;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(
                this.fingerprints.AsSpan((int)index * 2), value);
        }

        private static byte Mod3(int x)
        {
            return (byte)(x > 2 ? x - 3 : x);
        }

        /// <summary>
        /// The mixing the paper specifies, which is not the library's hash function: the
        /// key has already been hashed by the time it gets here, and this is what lets a
        /// failed peel be retried by changing the seed alone.
        /// </summary>
        private static ulong Murmur64(ulong h)
        {
            h ^= h >> 33;
            h *= 0xff51afd7ed558ccdUL;
            h ^= h >> 33;
            h *= 0xc4ceb9fe1a85ec53UL;
            h ^= h >> 33;
            return h;
        }

        /// <summary>
        /// Whether the data is probably in the set the filter was built from.
        /// </summary>
        /// <param name="data">The data to test for.</param>
        /// <returns>
        /// False if the data is certainly absent, true if it is probably present.
        /// </returns>
        public bool Test(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            // A filter over no keys holds nothing, and says so. Left to the arithmetic
            // it would answer yes at its nominal false positive rate, which is allowed
            // but useless: every slot is zero, so a probe whose own fingerprint is zero
            // xors to zero and looks like a member of the empty set.
            if (this.size == 0)
            {
                return false;
            }

            var hash = Murmur64(this.Hash(data) + this.seed);
            var fingerprint = this.FingerprintOf(hash);

            var h0 = (uint)Math.BigMul(hash, this.segmentCountLength, out _);
            var h1 = h0 + this.segmentLength;
            var h2 = h1 + this.segmentLength;

            h1 ^= (uint)((hash >> 18) & this.segmentLengthMask);
            h2 ^= (uint)(hash & this.segmentLengthMask);

            fingerprint ^= (ushort)(this.FingerprintAt(h0)
                ^ this.FingerprintAt(h1)
                ^ this.FingerprintAt(h2));

            return fingerprint == 0;
        }

        /// <summary>
        /// Writes the filter to a stream.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        /// <remarks>
        /// The segment geometry is mostly derived rather than stored: the mask, the
        /// segment count length and the array length all follow from the segment length
        /// and count, and deriving them on read means a payload cannot disagree with
        /// itself about them.
        /// </remarks>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = new PayloadWriter();
            payload.WriteUInt32(this.size);
            payload.WriteUInt32(this.segmentLength);
            payload.WriteUInt32(this.segmentCount);
            payload.WriteUInt64(this.seed);
            payload.WriteByte((byte)this.width);
            payload.WriteBytes(this.fingerprints);

            PersistenceFormat.Write(
                stream,
                StructureId.BinaryFuseFilter,
                PersistenceFormat.Identify(this.Hash),
                payload.WrittenSpan);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The filter that was written.</returns>
        public static BinaryFuseFilter ReadFrom(Stream stream)
        {
            return Read(stream, null);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>, using the supplied hash
        /// function rather than the one named in the payload.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function the filter was built with.</param>
        /// <returns>The filter that was written.</returns>
        public static BinaryFuseFilter ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            return Read(stream, hash);
        }

        private static BinaryFuseFilter Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(
                stream, StructureId.BinaryFuseFilter, out var hashId);
            var reader = new PayloadReader(payload);

            var size = reader.ReadUInt32();
            var segmentLength = reader.ReadUInt32();
            var segmentCount = reader.ReadUInt32();
            var seed = reader.ReadUInt64();
            var width = (BinaryFuseWidth)reader.ReadByte();
            var fingerprints = reader.ReadBytes();
            reader.ExpectEnd();

            if (!Enum.IsDefined(width))
            {
                throw new InvalidDataException(
                    $"Filter has {(byte)width}-bit fingerprints, and this library builds " +
                    "them 8 or 16 bits wide.");
            }

            // The mask is what confines a position to its segment, and it is only a mask
            // if the length is a power of two. A payload claiming otherwise would put
            // keys where a lookup does not go.
            if (segmentLength == 0 || (segmentLength & (segmentLength - 1)) != 0)
            {
                throw new InvalidDataException(
                    $"Filter has a segment length of {segmentLength}, which is not a " +
                    "power of two, so it does not describe a filter this library builds.");
            }

            if (segmentCount == 0)
            {
                throw new InvalidDataException("Filter has no segments to hold anything.");
            }

            var arrayLength = ((ulong)segmentCount + Arity - 1) * segmentLength;
            var stride = (int)width / 8;

            if ((ulong)fingerprints.LongLength != arrayLength * (ulong)stride)
            {
                throw new InvalidDataException(
                    $"Filter has {segmentCount} segments of {segmentLength} at " +
                    $"{stride} bytes, which needs {arrayLength * (ulong)stride} bytes " +
                    $"of fingerprints, and carries {fingerprints.LongLength}.");
            }

            return new BinaryFuseFilter
            {
                size = size,
                segmentLength = segmentLength,
                segmentLengthMask = segmentLength - 1,
                segmentCount = segmentCount,
                segmentCountLength = segmentCount * segmentLength,
                arrayLength = (uint)arrayLength,
                seed = seed,
                width = width,
                fingerprints = fingerprints,
                Hash = PersistenceFormat.ResolveOrThrow(hashId, hash),
            };
        }

        /// <summary>
        /// The number of distinct keys the filter was built from.
        /// </summary>
        public uint Count()
        {
            return this.size;
        }

        /// <summary>
        /// The fingerprint width the filter was built with.
        /// </summary>
        public BinaryFuseWidth Width()
        {
            return this.width;
        }

        /// <summary>
        /// The filter's nominal false positive rate, which its width alone decides.
        /// </summary>
        public double FalsePositiveRate()
        {
            return RateFor(this.width);
        }

        /// <summary>
        /// The size of the filter's fingerprint array in bytes, which is all of what it
        /// occupies beyond a handful of fields.
        /// </summary>
        public ulong SizeInBytes()
        {
            return (ulong)this.fingerprints.LongLength;
        }
    }
}
