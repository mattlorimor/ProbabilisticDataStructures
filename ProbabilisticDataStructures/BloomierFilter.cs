using System;
using System.Collections.Generic;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// An approximate map: it stores a value for each key without storing the keys.
    /// </summary>
    /// <remarks>
    /// Chazelle, Kilian, Rubinfeld and Tal, "The Bloomier Filter" (2004), built here on
    /// the peeling construction <see cref="BinaryFuseFilter"/> uses.
    /// <para>
    /// Every other structure here answers whether a key is present. This answers what
    /// value goes with it, and never stores the key itself -- which is why it is smaller
    /// than a dictionary and why the set is fixed at construction.
    /// </para>
    /// <para>
    /// <b>A note on the classic form.</b> A Bloomier filter as originally described
    /// returns an arbitrary value for a key it was not built from, with no way to tell
    /// that apart from a real answer. That is a sharper edge than a Bloom filter's false
    /// positive: a wrong value that looks right. This stores a fingerprint alongside each
    /// value so an absent key is rejected instead, at 2^-8 per lookup, which turns the
    /// failure back into the bounded kind this library states elsewhere. It costs one byte
    /// per cell.
    /// </para>
    /// </remarks>
    public class BloomierFilter : IBinaryPersistable<BloomierFilter>
    {
        private const int Arity = 3;
        private const int FingerprintBits = 8;
        private const int MaxBuildAttempts = 100;
        private const uint MaxSegmentLength = 262144;

        private uint segmentLength;
        private uint segmentLengthMask;
        private uint segmentCount;
        private uint segmentCountLength;
        private uint arrayLength;
        private ulong seed;
        private uint size;
        private int valueBits;
        private int stride;
        private ulong cellMask;
        private ulong valueMask;
        private byte[] cells = null!;

        internal Func<ReadOnlySpan<byte>, ulong> Hash { get; set; } = null!;

        private BloomierFilter()
        {
        }

        /// <summary>
        /// Builds a map over the given key-value pairs.
        /// </summary>
        /// <param name="pairs">
        /// The keys and their values. Keys must be distinct; a repeated key with a
        /// different value has no consistent answer and is refused.
        /// </param>
        /// <param name="valueBits">
        /// How many bits each value takes, from 1 to 40. A value that does not fit is
        /// refused rather than truncated.
        /// </param>
        /// <param name="hash">The hash function to use, or null for the default.</param>
        /// <returns>A map answering for exactly those keys.</returns>
        /// <remarks>
        /// <b>A key is its 64-bit hash.</b> The filter does not keep keys, only what the
        /// hash of each says, so two different keys whose hashes are equal are one key to
        /// it. If they have the same value that costs nothing: the map answers with it for
        /// either, and counts them once. If they have different values the map cannot hold
        /// both, and the build is refused with an <see cref="ArgumentException"/> that says
        /// so, as it does for one key given twice with different values.
        /// <para>
        /// The chance of that is chance, and it is not small for a large map. For n keys the
        /// expected number of colliding pairs is n(n-1) / 2^65, so the chance that some two
        /// keys collide is, at most (it needs different values too):
        /// </para>
        /// <list type="table">
        /// <listheader><term>Keys</term><description>Chance two share a hash</description></listheader>
        /// <item><term>1 million</term><description>0.000003%</description></item>
        /// <item><term>10 million</term><description>0.0003%</description></item>
        /// <item><term>100 million</term><description>0.03%</description></item>
        /// <item><term>318 million, the most 40-bit values build</term><description>0.27%</description></item>
        /// <item><term>636 million, the most 16-bit values build</term><description>1.1%</description></item>
        /// <item><term>954 million, the most 8-bit values build</term><description>2.4%</description></item>
        /// <item><term>2^32, which no map builds, for reference</term><description>39%</description></item>
        /// </list>
        /// <para>
        /// Building again with a different <paramref name="hash"/> moves the collision, so a
        /// refusal at these sizes is a reason to try another hash function and not a sign
        /// that the data is wrong. Widening the identity would be a change to the format,
        /// which this does not make.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// A key appears twice with different values, or two different keys have the same
        /// 64-bit hash and different values.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// A value is wider than <paramref name="valueBits"/>, or the map would have more
        /// distinct keys than one array of cells holds: 954 million at values of 8 bits or
        /// fewer, 636 million to 16 bits and 318 million to 40. That is refused at the
        /// first key past the most, without reading the rest of the pairs, so a set that
        /// also gives a key two values is refused for whichever of the two comes first.
        /// </exception>
        public static BloomierFilter Build(
            IEnumerable<KeyValuePair<byte[], ulong>> pairs,
            int valueBits,
            Func<ReadOnlySpan<byte>, ulong>? hash = null)
        {
            ArgumentNullException.ThrowIfNull(pairs);

            if (valueBits < 1 || valueBits > 40)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(valueBits), valueBits,
                    "Values are between 1 and 40 bits wide, so that a cell holding one " +
                    "plus its fingerprint fits in a long.");
            }

            return Build(pairs, valueBits, hash, MaxKeys(valueBits));
        }

        /// <summary>
        /// Builds a map as <see cref="Build(IEnumerable{KeyValuePair{byte[], ulong}}, int, Func{ReadOnlySpan{byte}, ulong}?)"/>
        /// does, which calls this, refusing a set of more than <paramref name="keyLimit"/>
        /// distinct keys.
        /// </summary>
        /// <param name="pairs">The keys and their values.</param>
        /// <param name="valueBits">How many bits each value takes, from 1 to 40.</param>
        /// <param name="hash">The hash function to use, or null for the default.</param>
        /// <param name="keyLimit">
        /// The most distinct keys to build for. The public build passes
        /// <see cref="MaxKeys"/>; a test lowers it, as it lowers the quotient-bit limit of
        /// <see cref="InfiniFilter"/>, to reach the refusal without gigabytes of keys.
        /// </param>
        internal static BloomierFilter Build(
            IEnumerable<KeyValuePair<byte[], ulong>> pairs,
            int valueBits,
            Func<ReadOnlySpan<byte>, ulong>? hash,
            uint keyLimit)
        {
            var hashFunction = hash ?? Defaults.GetDefaultHashFunction();
            var filter = new BloomierFilter
            {
                Hash = hashFunction,
                valueBits = valueBits,
                valueMask = valueBits == 64 ? ulong.MaxValue : (1UL << valueBits) - 1,
            };

            var entries = Collect(pairs, hashFunction, filter.valueMask, valueBits, keyLimit);
            filter.Shape((uint)entries.Count);
            filter.Populate(entries);

            return filter;
        }

        /// <summary>
        /// Hashes each key and checks the values fit, refusing a key that appears twice
        /// with different values, and a set of more than <paramref name="keyLimit"/>
        /// distinct keys as soon as it passes it.
        /// </summary>
        private static List<(ulong Key, ulong Value)> Collect(
            IEnumerable<KeyValuePair<byte[], ulong>> pairs,
            Func<ReadOnlySpan<byte>, ulong> hash,
            ulong valueMask,
            int valueBits,
            uint keyLimit)
        {
            var seen = new Dictionary<ulong, ulong>();

            foreach (var pair in pairs)
            {
                ArgumentNullException.ThrowIfNull(pair.Key, nameof(pairs));

                if (pair.Value > valueMask)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(pairs), pair.Value,
                        $"A value of {pair.Value} does not fit in {valueBits} bits. " +
                        "Widen the filter rather than letting it be truncated.");
                }

                var key = hash(pair.Key);
                var known = seen.TryGetValue(key, out var existing);

                if (known && existing != pair.Value)
                {
                    // Two entries are one key to this filter, which knows a key by its
                    // 64-bit hash alone and keeps no keys. That is a repeated key, or two
                    // different keys that happen to hash alike, and the filter cannot tell
                    // which. It used to say only the first, which for the second sends the
                    // caller looking for a repeat that is not there.
                    throw new ArgumentException(
                        $"Two entries have the same 64-bit hash and different values, " +
                        $"{existing} and {pair.Value}. Either a key appears twice with " +
                        "different values, or two different keys collided. The filter knows " +
                        "a key by its 64-bit hash alone and does not keep the keys, so a " +
                        "map cannot hold both values and could not tell you which one it " +
                        "dropped. If the keys are distinct, this is chance, about n^2 / 2^65 " +
                        "for n keys: 0.03% at 100 million keys, 2.4% at 954 million, the " +
                        "most 8-bit values build, and 39% at 2^32. Build again with a " +
                        "different hash function, which moves the collision elsewhere.",
                        nameof(pairs));
                }

                // Refused at the first key past the limit, so that what is held to say so
                // is the limit's worth of keys and the pairs are read no further. Collecting
                // them all first held a set too large whole, in this and in the list below,
                // before the count was compared: past the most a map of 8-bit values holds,
                // more than 42 GB. A set with too many keys and a conflict is refused for
                // whichever comes first.
                if (!known && seen.Count == keyLimit)
                {
                    throw TooManyKeys(keyLimit, valueBits);
                }

                seen[key] = pair.Value;
            }

            var entries = new List<(ulong, ulong)>(seen.Count);
            foreach (var entry in seen)
            {
                entries.Add((entry.Key, entry.Value));
            }

            return entries;
        }

        /// <summary>
        /// The refusal of a set with more distinct keys than a map is built for.
        /// </summary>
        private static ArgumentOutOfRangeException TooManyKeys(uint keyLimit, int valueBits)
        {
            return new ArgumentOutOfRangeException("pairs", keyLimit + 1L,
                $"The pairs hold more than {keyLimit:N0} distinct keys, the most a map of " +
                $"{valueBits}-bit values is built for: the cells of more are more than one " +
                $"array holds, {Array.MaxLength:N0} bytes. The pairs were read no further. " +
                "Build several maps over parts of the set instead.");
        }

        /// <summary>
        /// Works out the segment geometry, as <see cref="BinaryFuseFilter"/> does.
        /// </summary>
        private void Shape(uint keyCount)
        {
            var geometry = Geometry(keyCount);
            var stride = StrideFor(this.valueBits);

            // Refused before the array is asked for, which failed with an OverflowException
            // from 2^31 bytes and an OutOfMemoryException just under.
            var bytes = (long)geometry.ArrayLength * stride;
            if (bytes > Array.MaxLength)
            {
                throw new ArgumentOutOfRangeException("pairs", keyCount,
                    $"{keyCount:N0} distinct keys at {this.valueBits} bits a value need " +
                    $"{bytes:N0} bytes of cells, and one array holds {Array.MaxLength:N0}. " +
                    $"The most is {MaxKeys(this.valueBits):N0} keys. Build several maps over " +
                    "parts of the set instead.");
            }

            this.size = keyCount;
            this.segmentLength = geometry.SegmentLength;
            this.segmentLengthMask = this.segmentLength - 1;
            this.segmentCount = geometry.SegmentCount;
            this.arrayLength = geometry.ArrayLength;
            this.segmentCountLength = this.segmentCount * this.segmentLength;

            var cellBits = FingerprintBits + this.valueBits;
            this.stride = stride;
            this.cellMask = (1UL << cellBits) - 1;
            this.cells = new byte[bytes];
        }

        /// <summary>
        /// The bytes a cell takes: the fingerprint and the value, rounded up to whole
        /// bytes.
        /// </summary>
        private static int StrideFor(int valueBits)
        {
            return (FingerprintBits + valueBits + 7) / 8;
        }

        /// <summary>
        /// The segment length, segment count and array length for a set of the given size,
        /// which are <see cref="BinaryFuseFilter"/>'s. Pure, so that the limit on the array
        /// can be asked of it without allocating.
        /// </summary>
        private static (uint SegmentLength, uint SegmentCount, uint ArrayLength) Geometry(
            uint keyCount)
        {
            var segmentLength = keyCount == 0
                ? 4
                : Math.Min(1u << (int)Math.Floor(Math.Log(keyCount) / Math.Log(3.33) + 2.25), MaxSegmentLength);

            var sizeFactor = keyCount <= 1
                ? 0
                : Math.Max(1.125, 0.875 + (0.25 * Math.Log(1000000.0) / Math.Log(keyCount)));
            var capacity = keyCount <= 1 ? 0 : (uint)Math.Round(keyCount * sizeFactor);

            uint arrayLength;
            unchecked
            {
                var initialSegments =
                    ((capacity + segmentLength - 1) / segmentLength) - (Arity - 1);
                arrayLength = (initialSegments + Arity - 1) * segmentLength;
            }

            var segmentCount = (arrayLength + segmentLength - 1) / segmentLength;
            segmentCount = segmentCount <= Arity - 1 ? 1 : segmentCount - (Arity - 1);
            arrayLength = (segmentCount + Arity - 1) * segmentLength;
            return (segmentLength, segmentCount, arrayLength);
        }

        /// <summary>
        /// The bytes of cells a map of this many keys and this value width has.
        /// </summary>
        internal static long CellBytes(uint keyCount, int valueBits)
        {
            return (long)Geometry(keyCount).ArrayLength * StrideFor(valueBits);
        }

        /// <summary>
        /// The most distinct keys a map of this value width is built for: the most whose
        /// cells are one array. About 954 million at values of 8 bits, 636 million at 16
        /// and 318 million at 40. The count of keys is an int, so the search stops there.
        /// </summary>
        internal static uint MaxKeys(int valueBits)
        {
            uint low = 0;
            uint high = int.MaxValue;
            while (low < high)
            {
                var middle = low + ((high - low + 1) / 2);
                if (CellBytes(middle, valueBits) <= Array.MaxLength)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return low;
        }

        /// <summary>
        /// Solves the system so that each key's three cells combine to its fingerprint
        /// and value, retrying with a fresh seed when the peel does not consume every key.
        /// </summary>
        private void Populate(List<(ulong Key, ulong Value)> entries)
        {
            if (this.size == 0)
            {
                return;
            }

            var keys = new ulong[this.size];
            var values = new ulong[this.size];
            for (var i = 0; i < entries.Count; i++)
            {
                keys[i] = entries[i].Key;
                values[i] = entries[i].Value;
            }

            var order = new ulong[this.size + 1];
            var orderValue = new ulong[this.size + 1];
            var orderPosition = new byte[this.size];
            var alone = new uint[this.arrayLength];
            var t2count = new byte[this.arrayLength];
            var t2hash = new ulong[this.arrayLength];
            var t2value = new ulong[this.arrayLength];

            var blockBits = 1;
            while ((1u << blockBits) < this.segmentCount)
            {
                blockBits++;
            }

            var block = 1u << blockBits;
            var startPos = new uint[block];
            var rng = new SeededRandom(0x726b2b9d438b9d4d);
            this.seed = rng.Next();
            order[this.size] = 1;

            for (var attempt = 0; ; attempt++)
            {
                if (attempt >= MaxBuildAttempts)
                {
                    throw new InvalidOperationException(
                        $"Could not build a Bloomier filter over {this.size} keys in " +
                        $"{MaxBuildAttempts} attempts.");
                }

                if (this.TryPeel(keys, values, order, orderValue, orderPosition, alone,
                                 t2count, t2hash, t2value, startPos, block, blockBits))
                {
                    break;
                }

                Array.Clear(order, 0, (int)this.size);
                Array.Clear(orderValue, 0, (int)this.size);
                Array.Clear(t2count, 0, (int)this.arrayLength);
                Array.Clear(t2hash, 0, (int)this.arrayLength);
                Array.Clear(t2value, 0, (int)this.arrayLength);
                this.seed = rng.Next();
            }

            this.Assign(order, orderValue, orderPosition);
        }

        private bool TryPeel(
            ulong[] keys, ulong[] values, ulong[] order, ulong[] orderValue,
            byte[] orderPosition, uint[] alone, byte[] t2count, ulong[] t2hash,
            ulong[] t2value, uint[] startPos, uint block, int blockBits)
        {
            for (uint i = 0; i < block; i++)
            {
                startPos[i] = (uint)(((ulong)i * this.size) >> blockBits);
            }

            var maskBlock = block - 1;
            for (uint i = 0; i < this.size; i++)
            {
                var hash = Murmur64(keys[i] + this.seed);
                var segment = hash >> (64 - blockBits);

                while (order[startPos[segment]] != 0)
                {
                    segment++;
                    segment &= maskBlock;
                }

                order[startPos[segment]] = hash;
                orderValue[startPos[segment]] = values[i];
                startPos[segment]++;
            }

            var overflowed = false;
            for (uint i = 0; i < this.size; i++)
            {
                var hash = order[i];
                for (var position = 0; position < Arity; position++)
                {
                    var index = this.PositionOf(position, hash);
                    t2count[index] += 4;
                    t2count[index] ^= (byte)position;
                    t2hash[index] ^= hash;
                    t2value[index] ^= orderValue[i];
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
                var value = t2value[index];
                var found = (byte)(t2count[index] & 3);

                orderPosition[peeled] = found;
                order[peeled] = hash;
                orderValue[peeled] = value;
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
                    t2value[otherIndex] ^= value;
                }
            }

            return peeled == this.size;
        }

        /// <summary>
        /// Walks the peel backwards, giving each key the cell that makes its three
        /// combine to its fingerprint and value.
        /// </summary>
        private void Assign(ulong[] order, ulong[] orderValue, byte[] orderPosition)
        {
            Span<uint> positions = stackalloc uint[Arity + 2];

            for (var i = this.size; i-- > 0;)
            {
                var hash = order[i];
                var found = orderPosition[i];

                positions[0] = this.PositionOf(0, hash);
                positions[1] = this.PositionOf(1, hash);
                positions[2] = this.PositionOf(2, hash);
                positions[3] = positions[0];
                positions[4] = positions[1];

                var wanted = (FingerprintOf(hash) << this.valueBits) | (orderValue[i] & this.valueMask);
                var value = wanted
                    ^ this.CellAt(positions[found + 1])
                    ^ this.CellAt(positions[found + 2]);

                this.SetCell(positions[found], value);
            }
        }

        /// <summary>
        /// The value stored for a key, if the filter holds it.
        /// </summary>
        /// <param name="key">The key to look up.</param>
        /// <param name="value">The value found, or zero.</param>
        /// <returns>
        /// False if the key is certainly absent. True if it is probably present, in which
        /// case the value is the one it was built with -- or, once in 256 lookups of an
        /// absent key, a value belonging to nothing.
        /// </returns>
        public bool TryGetValue(byte[] key, out ulong value)
        {
            ArgumentNullException.ThrowIfNull(key);

            return this.TryGetValue(key.AsSpan(), out value);
        }

        /// <inheritdoc cref="TryGetValue(byte[], out ulong)"/>
        public bool TryGetValue(ReadOnlySpan<byte> key, out ulong value)
        {
            value = 0;

            if (this.size == 0)
            {
                return false;
            }

            var hash = Murmur64(this.Hash(key) + this.seed);

            var h0 = (uint)Math.BigMul(hash, this.segmentCountLength, out _);
            var h1 = h0 + this.segmentLength;
            var h2 = h1 + this.segmentLength;
            h1 ^= (uint)((hash >> 18) & this.segmentLengthMask);
            h2 ^= (uint)(hash & this.segmentLengthMask);

            var combined = this.CellAt(h0) ^ this.CellAt(h1) ^ this.CellAt(h2);

            if ((combined >> this.valueBits) != FingerprintOf(hash))
            {
                return false;
            }

            value = combined & this.valueMask;
            return true;
        }

        private ulong CellAt(uint index)
        {
            ulong cell = 0;
            var at = (long)index * this.stride;

            for (var i = 0; i < this.stride; i++)
            {
                cell |= (ulong)this.cells[at + i] << (8 * i);
            }

            return cell & this.cellMask;
        }

        private void SetCell(uint index, ulong value)
        {
            value &= this.cellMask;
            var at = (long)index * this.stride;

            for (var i = 0; i < this.stride; i++)
            {
                this.cells[at + i] = (byte)(value >> (8 * i));
            }
        }

        private uint PositionOf(int position, ulong hash)
        {
            var index = Math.BigMul(hash, this.segmentCountLength, out _);
            index += (ulong)position * this.segmentLength;

            var low = hash & ((1UL << 36) - 1);
            index ^= (low >> (36 - (18 * position))) & this.segmentLengthMask;

            return (uint)index;
        }

        private static ulong FingerprintOf(ulong hash)
        {
            return (byte)(hash ^ (hash >> 32));
        }

        private static byte Mod3(int x) => (byte)(x > 2 ? x - 3 : x);

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
        /// Writes the map to a stream.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = new PayloadWriter();
            payload.WriteUInt32(this.size);
            payload.WriteUInt32(this.segmentLength);
            payload.WriteUInt32(this.segmentCount);
            payload.WriteUInt64(this.seed);
            payload.WriteUInt32((uint)this.valueBits);
            payload.WriteBytes(this.cells);

            PersistenceFormat.Write(
                stream, StructureId.BloomierFilter, PersistenceFormat.Identify(this.Hash),
                payload.WrittenSpan);
        }

        /// <summary>
        /// Reads a map written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The map that was written.</returns>
        public static BloomierFilter ReadFrom(Stream stream)
        {
            return Read(stream, null, ReadOptions.Default);
        }

        /// <summary>
        /// Reads a structure written by <see cref="WriteTo"/>, allocating no more than the
        /// options' memory budget allows. <see cref="ReadFrom(Stream)"/> reads under
        /// <see cref="ReadOptions.Default"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="options">The most memory the read may allocate.</param>
        /// <returns>The structure that was written.</returns>
        /// <exception cref="InvalidDataException">
        /// Reading the payload would allocate more than the budget allows, among the other
        /// reasons a payload is refused. Its inner exception is then a
        /// <see cref="ReadBudgetExceededException"/>.
        /// </exception>
        public static BloomierFilter ReadFrom(Stream stream, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, null, options);
        }

        /// <summary>
        /// Reads a map written by <see cref="WriteTo"/>, using the supplied hash
        /// function rather than the one named in the payload.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function the map was built with.</param>
        /// <returns>The map that was written.</returns>
        public static BloomierFilter ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            return Read(stream, hash, ReadOptions.Default);
        }

        /// <summary>
        /// Reads a structure as
        /// <see cref="ReadFrom(Stream, Func{ReadOnlySpan{byte}, ulong})"/> does, allocating
        /// no more than the options' memory budget allows.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function, as for the overload without options.</param>
        /// <param name="options">The most memory the read may allocate.</param>
        /// <returns>The structure that was written.</returns>
        /// <exception cref="InvalidDataException">
        /// Reading the payload would allocate more than the budget allows, among the other
        /// reasons a payload is refused. Its inner exception is then a
        /// <see cref="ReadBudgetExceededException"/>.
        /// </exception>
        public static BloomierFilter ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, hash, options);
        }

        private static BloomierFilter Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.BloomierFilter);
            var payload = PersistenceFormat.Read(
                stream, StructureId.BloomierFilter, out var hashId, budget, Price,
                hash, PersistenceFormat.CheckResolvable);
            var reader = new PayloadReader(payload, budget);

            var size = reader.ReadUInt32();
            var segmentLength = reader.ReadUInt32();
            var segmentCount = reader.ReadUInt32();
            var seed = reader.ReadUInt64();
            var valueBits = (int)reader.ReadUInt32();
            var cells = reader.ReadBytes();
            reader.ExpectEnd();

            if (valueBits < 1 || valueBits > 40)
            {
                throw new InvalidDataException(
                    $"Map has {valueBits}-bit values, and this library builds them " +
                    "between 1 and 40 bits.");
            }

            if (segmentLength == 0 || (segmentLength & (segmentLength - 1)) != 0)
            {
                throw new InvalidDataException(
                    $"Map has a segment length of {segmentLength}, which is not a power " +
                    "of two, so it does not describe a map this library builds.");
            }

            if (segmentCount == 0)
            {
                throw new InvalidDataException("Map has no segments to hold anything.");
            }

            var arrayLength = ((ulong)segmentCount + Arity - 1) * segmentLength;
            var stride = (FingerprintBits + valueBits + 7) / 8;

            if ((ulong)cells.LongLength != arrayLength * (ulong)stride)
            {
                throw new InvalidDataException(
                    $"Map has {segmentCount} segments of {segmentLength} at {stride} " +
                    $"bytes, needing {arrayLength * (ulong)stride} bytes of cells, and " +
                    $"carries {cells.LongLength}.");
            }

            return new BloomierFilter
            {
                size = size,
                segmentLength = segmentLength,
                segmentLengthMask = segmentLength - 1,
                segmentCount = segmentCount,
                segmentCountLength = segmentCount * segmentLength,
                arrayLength = (uint)arrayLength,
                seed = seed,
                valueBits = valueBits,
                stride = stride,
                cellMask = (1UL << (FingerprintBits + valueBits)) - 1,
                valueMask = (1UL << valueBits) - 1,
                cells = cells,
                Hash = PersistenceFormat.ResolveOrThrow(hashId, hash),
            };
        }

        /// <summary>
        /// Prices what <see cref="Read"/> allocates beyond the envelope: the copy of the
        /// cells, which the map keeps. See <see cref="PayloadPrice"/>.
        /// </summary>
        internal static void Price(ref PayloadReader reader, ushort version)
        {
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt64();
            reader.ReadUInt32();
            reader.SkipBytes();
        }

        /// <summary>The number of distinct keys the map was built from.</summary>
        public uint Count() => this.size;

        /// <summary>How many bits each value occupies.</summary>
        public int ValueBits() => this.valueBits;

        /// <summary>The map's storage in bytes.</summary>
        public ulong SizeInBytes() => (ulong)this.cells.LongLength;
    }
}
