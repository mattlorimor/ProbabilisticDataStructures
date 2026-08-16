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

            var hashFunction = hash ?? Defaults.GetDefaultHashFunction();
            var filter = new BloomierFilter
            {
                Hash = hashFunction,
                valueBits = valueBits,
                valueMask = valueBits == 64 ? ulong.MaxValue : (1UL << valueBits) - 1,
            };

            var entries = Collect(pairs, hashFunction, filter.valueMask, valueBits);
            filter.Shape((uint)entries.Count);
            filter.Populate(entries);

            return filter;
        }

        /// <summary>
        /// Hashes each key and checks the values fit, refusing a key that appears twice
        /// with different values.
        /// </summary>
        private static List<(ulong Key, ulong Value)> Collect(
            IEnumerable<KeyValuePair<byte[], ulong>> pairs,
            Func<ReadOnlySpan<byte>, ulong> hash,
            ulong valueMask,
            int valueBits)
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

                if (seen.TryGetValue(key, out var existing) && existing != pair.Value)
                {
                    throw new ArgumentException(
                        $"A key appears twice with different values, {existing} and " +
                        $"{pair.Value}. A map cannot hold both, and the filter does not " +
                        "keep the keys, so it could not tell you which one it dropped.",
                        nameof(pairs));
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
        /// Works out the segment geometry, as <see cref="BinaryFuseFilter"/> does.
        /// </summary>
        private void Shape(uint keyCount)
        {
            this.size = keyCount;
            this.segmentLength = keyCount == 0
                ? 4
                : Math.Min(1u << (int)Math.Floor(Math.Log(keyCount) / Math.Log(3.33) + 2.25), MaxSegmentLength);

            this.segmentLengthMask = this.segmentLength - 1;

            var sizeFactor = keyCount <= 1
                ? 0
                : Math.Max(1.125, 0.875 + (0.25 * Math.Log(1000000.0) / Math.Log(keyCount)));
            var capacity = keyCount <= 1 ? 0 : (uint)Math.Round(keyCount * sizeFactor);

            unchecked
            {
                var initialSegments =
                    ((capacity + this.segmentLength - 1) / this.segmentLength) - (Arity - 1);
                this.arrayLength = (initialSegments + Arity - 1) * this.segmentLength;
            }

            this.segmentCount = (this.arrayLength + this.segmentLength - 1) / this.segmentLength;
            this.segmentCount = this.segmentCount <= Arity - 1 ? 1 : this.segmentCount - (Arity - 1);
            this.arrayLength = (this.segmentCount + Arity - 1) * this.segmentLength;
            this.segmentCountLength = this.segmentCount * this.segmentLength;

            var cellBits = FingerprintBits + this.valueBits;
            this.stride = (cellBits + 7) / 8;
            this.cellMask = (1UL << cellBits) - 1;
            this.cells = new byte[(long)this.arrayLength * this.stride];
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
            return Read(stream, null);
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
            return Read(stream, hash);
        }

        private static BloomierFilter Read(Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(stream, StructureId.BloomierFilter, out var hashId);
            var reader = new PayloadReader(payload);

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

        /// <summary>The number of distinct keys the map was built from.</summary>
        public uint Count() => this.size;

        /// <summary>How many bits each value occupies.</summary>
        public int ValueBits() => this.valueBits;

        /// <summary>The map's storage in bytes.</summary>
        public ulong SizeInBytes() => (ulong)this.cells.LongLength;
    }
}
