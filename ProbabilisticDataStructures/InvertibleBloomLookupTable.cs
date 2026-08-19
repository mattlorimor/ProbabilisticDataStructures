using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Recovers <b>which</b> keys two sets differ by, rather than how many.
    /// </summary>
    /// <remarks>
    /// Goodrich and Mitzenmacher, "Invertible Bloom Lookup Tables" (2011), used here for
    /// the set reconciliation Eppstein et al. describe.
    /// <para>
    /// The trick is that the cells are subtractable. Each key is xored into several
    /// cells along with a count; subtracting one table from another leaves a table
    /// holding only the keys they disagree about, and that table can be <b>peeled</b> --
    /// repeatedly find a cell holding exactly one key, take it out, and repeat.
    /// </para>
    /// <para>
    /// So the cost is proportional to the size of the <b>difference</b>, not of the sets.
    /// Two replicas holding a million keys each and differing by ten can reconcile by
    /// exchanging a table sized for ten. That is unlike everything else here, which is
    /// sized against how much data there is.
    /// </para>
    /// <para>
    /// It can fail, and says so. If the difference is larger than the table was sized
    /// for, peeling stalls with keys left over and <see cref="TryDecode"/> returns false
    /// rather than a partial answer.
    /// </para>
    /// </remarks>
    public class InvertibleBloomLookupTable : IBinaryPersistable<InvertibleBloomLookupTable>
    {
        /// <summary>
        /// How many cells each key is placed in. Four is the usual choice: peeling
        /// succeeds below about 1.3 times the difference in cells, and more hashes buy
        /// little beyond that.
        /// </summary>
        private const int HashCount = 4;

        /// <summary>
        /// Cells per expected difference. Complete listing needs more than c4 = 1.295
        /// cells per entry -- the 2-core threshold for four hashes, Table 1 of the
        /// paper (1.222 is the three-hash figure); 1.5 leaves room.
        /// </summary>
        internal const double CellsPerDifference = 1.5;

        private uint cells;

        /// <summary>How many cells the table was provisioned with, for tests.</summary>
        internal uint CellCount => this.cells;
        private int keySize;
        private long[] counts = null!;
        private byte[] keySums = null!;
        private ulong[] hashSums = null!;

        internal Func<ReadOnlySpan<byte>, ulong> Hash { get; set; } = null!;

        /// <summary>
        /// Creates a table sized to recover a given number of differences.
        /// </summary>
        /// <param name="expectedDifferences">
        /// How many keys the two sets are expected to differ by. <b>Not</b> how many keys
        /// they hold -- that is the point of the structure, and sizing it against the set
        /// size would waste almost all of it.
        /// </param>
        /// <param name="keySize">
        /// The size of every key in bytes. Keys are combined by exclusive-or, so they all
        /// have to be the same width.
        /// </param>
        /// <param name="hash">The hash function to use, or null for the default.</param>
        public InvertibleBloomLookupTable(
            uint expectedDifferences, int keySize, Func<ReadOnlySpan<byte>, ulong>? hash = null)
        {
            Guard.ValidItemCount(expectedDifferences, nameof(expectedDifferences));

            if (keySize <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(keySize), keySize, "Keys must be at least one byte wide.");
            }

            this.keySize = keySize;
            this.cells = (uint)Math.Max(HashCount, Math.Ceiling(expectedDifferences * CellsPerDifference));
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();

            this.counts = new long[this.cells];
            this.keySums = new byte[(long)this.cells * keySize];
            this.hashSums = new ulong[this.cells];
        }

        /// <summary>
        /// Adds a key to the table.
        /// </summary>
        /// <param name="key">The key, which must be the table's key size.</param>
        /// <returns>The table, to allow chaining.</returns>
        public InvertibleBloomLookupTable Add(byte[] key)
        {
            ArgumentNullException.ThrowIfNull(key);

            return this.Add(key.AsSpan());
        }

        /// <inheritdoc cref="Add(byte[])"/>
        public InvertibleBloomLookupTable Add(ReadOnlySpan<byte> key)
        {
            this.Apply(key, 1);
            return this;
        }

        /// <summary>
        /// Removes a key from the table.
        /// </summary>
        /// <param name="key">The key, which must be the table's key size.</param>
        /// <returns>The table, to allow chaining.</returns>
        public InvertibleBloomLookupTable Remove(byte[] key)
        {
            ArgumentNullException.ThrowIfNull(key);

            return this.Remove(key.AsSpan());
        }

        /// <inheritdoc cref="Remove(byte[])"/>
        public InvertibleBloomLookupTable Remove(ReadOnlySpan<byte> key)
        {
            this.Apply(key, -1);
            return this;
        }

        /// <summary>
        /// Adds or removes a key, which are the same operation with opposite signs --
        /// which is what makes a table subtractable.
        /// </summary>
        private void Apply(ReadOnlySpan<byte> key, long delta)
        {
            if (key.Length != this.keySize)
            {
                throw new ArgumentException(
                    $"Keys are {this.keySize} bytes here and this one is {key.Length}. " +
                    "They are combined by exclusive-or, so they all have to be the same " +
                    "width.",
                    nameof(key));
            }

            var hash = this.Hash(key);

            foreach (var cell in this.CellsFor(hash))
            {
                this.counts[cell] += delta;
                this.hashSums[cell] ^= hash;
                Xor(this.keySums.AsSpan(cell * this.keySize, this.keySize), key);
            }
        }

        /// <summary>
        /// The cells a key occupies. Distinct, so that a key xored into the same cell
        /// twice does not cancel itself out.
        /// </summary>
        private int[] CellsFor(ulong hash)
        {
            var chosen = new int[HashCount];
            var found = 0;
            var probe = hash;

            while (found < HashCount)
            {
                probe = Mix(probe);
                var cell = (int)(probe % this.cells);

                var seen = false;
                for (var i = 0; i < found; i++)
                {
                    if (chosen[i] == cell)
                    {
                        seen = true;
                        break;
                    }
                }

                if (!seen)
                {
                    chosen[found++] = cell;
                }
            }

            return chosen;
        }

        private static void Xor(Span<byte> into, ReadOnlySpan<byte> value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                into[i] ^= value[i];
            }
        }

        private static ulong Mix(ulong h)
        {
            h ^= h >> 33;
            h *= 0xff51afd7ed558ccdUL;
            h ^= h >> 33;
            h *= 0xc4ceb9fe1a85ec53UL;
            h ^= h >> 33;
            return h;
        }

        /// <summary>
        /// Recovers the keys the table holds.
        /// </summary>
        /// <param name="added">
        /// Keys present a net positive number of times -- in the left set and not the
        /// right, for a table produced by <see cref="Subtract"/>.
        /// </param>
        /// <param name="removed">Keys present a net negative number of times.</param>
        /// <returns>
        /// Whether everything was recovered. False means the table held more than it was
        /// sized for and peeling stalled, in which case the two lists are the part that
        /// came out before it did, which is not an answer.
        /// </returns>
        public bool TryDecode(out IReadOnlyList<byte[]> added, out IReadOnlyList<byte[]> removed)
        {
            var counts = (long[])this.counts.Clone();
            var keySums = (byte[])this.keySums.Clone();
            var hashSums = (ulong[])this.hashSums.Clone();

            var positive = new List<byte[]>();
            var negative = new List<byte[]>();

            var peelable = new Stack<int>();
            for (var cell = 0; cell < this.cells; cell++)
            {
                if (IsPure(counts, keySums, hashSums, cell, this.keySize, this.Hash))
                {
                    peelable.Push(cell);
                }
            }

            while (peelable.Count > 0)
            {
                var cell = peelable.Pop();

                if (!IsPure(counts, keySums, hashSums, cell, this.keySize, this.Hash))
                {
                    continue;
                }

                var key = keySums.AsSpan(cell * this.keySize, this.keySize).ToArray();
                var sign = counts[cell];

                (sign > 0 ? positive : negative).Add(key);

                var hash = this.Hash(key);
                foreach (var touched in this.CellsFor(hash))
                {
                    counts[touched] -= sign;
                    hashSums[touched] ^= hash;
                    Xor(keySums.AsSpan(touched * this.keySize, this.keySize), key);

                    if (IsPure(counts, keySums, hashSums, touched, this.keySize, this.Hash))
                    {
                        peelable.Push(touched);
                    }
                }
            }

            added = positive;
            removed = negative;

            // Anything left means peeling ran out of cells holding exactly one key while
            // keys remained, which is the failure this structure is honest about.
            foreach (var count in counts)
            {
                if (count != 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Whether a cell holds exactly one key, which is what makes it peelable.
        /// </summary>
        /// <remarks>
        /// The count alone is not enough: several keys can leave a count of one between
        /// them. The hash of what is in the cell has to match the xored hashes too, and
        /// that is what makes a false peel vanishingly unlikely rather than merely rare.
        /// </remarks>
        private static bool IsPure(
            long[] counts, byte[] keySums, ulong[] hashSums, int cell, int keySize,
            Func<ReadOnlySpan<byte>, ulong> hash)
        {
            if (counts[cell] != 1 && counts[cell] != -1)
            {
                return false;
            }

            return hash(keySums.AsSpan(cell * keySize, keySize)) == hashSums[cell];
        }

        /// <summary>
        /// A new table holding what this one has that the other does not, and the
        /// reverse, with everything they share cancelled out.
        /// </summary>
        /// <param name="other">The table to subtract. Neither is modified.</param>
        /// <returns>A table of the difference, ready to be decoded.</returns>
        /// <remarks>
        /// This is what the counts are for. A key added to both tables contributes +1 to
        /// a cell in one and +1 in the other, so subtracting leaves 0 and its xored key
        /// cancels itself. What survives is exactly what the two disagree about --
        /// however large the sets were.
        /// </remarks>
        /// <exception cref="ArgumentNullException">The other table is null.</exception>
        /// <exception cref="ArgumentException">
        /// The tables have different dimensions, key sizes, or hash functions.
        /// </exception>
        public InvertibleBloomLookupTable Subtract(InvertibleBloomLookupTable other)
        {
            ArgumentNullException.ThrowIfNull(other);

            if (this.cells != other.cells || this.keySize != other.keySize)
            {
                throw new ArgumentException(
                    $"Tables must be the same shape to be subtracted: this one has " +
                    $"{this.cells} cells of {this.keySize}-byte keys and the other " +
                    $"{other.cells} of {other.keySize}. A key lands in different cells " +
                    "under each, so nothing would cancel.",
                    nameof(other));
            }

            Guard.SameHashFunction(this.Hash, other.Hash, nameof(other));

            var result = new InvertibleBloomLookupTable(1, this.keySize)
            {
                cells = this.cells,
                Hash = this.Hash,
                counts = new long[this.cells],
                keySums = new byte[(long)this.cells * this.keySize],
                hashSums = new ulong[this.cells],
            };

            for (var cell = 0; cell < this.cells; cell++)
            {
                result.counts[cell] = this.counts[cell] - other.counts[cell];
                result.hashSums[cell] = this.hashSums[cell] ^ other.hashSums[cell];
            }

            for (var i = 0; i < this.keySums.Length; i++)
            {
                result.keySums[i] = (byte)(this.keySums[i] ^ other.keySums[i]);
            }

            return result;
        }

        /// <summary>
        /// The table's storage in bytes.
        /// </summary>
        /// <remarks>
        /// Proportional to the differences it was sized for, not to the sets it was
        /// built from, which is the whole point of the structure.
        /// </remarks>
        public ulong SizeInBytes()
        {
            return (ulong)(this.counts.LongLength * sizeof(long))
                + (ulong)this.keySums.LongLength
                + (ulong)(this.hashSums.LongLength * sizeof(ulong));
        }

        /// <summary>
        /// Writes the table to a stream.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = new PayloadWriter();
            payload.WriteUInt32(this.cells);
            payload.WriteUInt32((uint)this.keySize);

            var counts = new byte[(long)this.cells * sizeof(long)];
            var hashes = new byte[(long)this.cells * sizeof(ulong)];
            for (var cell = 0; cell < this.cells; cell++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(
                    counts.AsSpan(cell * sizeof(long)), this.counts[cell]);
                BinaryPrimitives.WriteUInt64LittleEndian(
                    hashes.AsSpan(cell * sizeof(ulong)), this.hashSums[cell]);
            }

            // Counts are signed: a subtracted table is mostly negative, and reading them
            // back unsigned would turn every one of those into an enormous positive.
            payload.WriteBytes(counts);
            payload.WriteBytes(this.keySums);
            payload.WriteBytes(hashes);

            PersistenceFormat.Write(
                stream, StructureId.InvertibleBloomLookupTable,
                PersistenceFormat.Identify(this.Hash), payload.WrittenSpan);
        }

        /// <summary>
        /// Reads a table written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The table that was written.</returns>
        public static InvertibleBloomLookupTable ReadFrom(Stream stream)
        {
            return Read(stream, null);
        }

        /// <summary>
        /// Reads a table written by <see cref="WriteTo"/>, using the supplied hash
        /// function rather than the one named in the payload.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function the table was built with.</param>
        /// <returns>The table that was written.</returns>
        public static InvertibleBloomLookupTable ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            return Read(stream, hash);
        }

        private static InvertibleBloomLookupTable Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(
                stream, StructureId.InvertibleBloomLookupTable, out var hashId);
            var reader = new PayloadReader(payload);

            var cells = reader.ReadUInt32();
            var keySize = (int)reader.ReadUInt32();
            var counts = reader.ReadBytes();
            var keySums = reader.ReadBytes();
            var hashes = reader.ReadBytes();
            reader.ExpectEnd();

            if (cells < HashCount)
            {
                throw new InvalidDataException(
                    $"Table has {cells} cells, and a key occupies {HashCount} of them.");
            }

            if (keySize <= 0)
            {
                throw new InvalidDataException(
                    $"Table has a key size of {keySize} bytes, and a key is at least one " +
                    "byte wide.");
            }

            if (counts.LongLength != (long)cells * sizeof(long)
                || hashes.LongLength != (long)cells * sizeof(ulong)
                || keySums.LongLength != (long)cells * keySize)
            {
                throw new InvalidDataException(
                    $"Table has {cells} cells of {keySize}-byte keys, and its counts, " +
                    "keys and hashes do not come to the byte counts that implies.");
            }

            var table = new InvertibleBloomLookupTable(1, keySize)
            {
                cells = cells,
                Hash = PersistenceFormat.ResolveOrThrow(hashId, hash),
                counts = new long[cells],
                keySums = keySums,
                hashSums = new ulong[cells],
            };

            for (var cell = 0; cell < cells; cell++)
            {
                table.counts[cell] = BinaryPrimitives.ReadInt64LittleEndian(
                    counts.AsSpan(cell * sizeof(long)));
                table.hashSums[cell] = BinaryPrimitives.ReadUInt64LittleEndian(
                    hashes.AsSpan(cell * sizeof(ulong)));
            }

            return table;
        }

        /// <summary>The number of cells the table holds.</summary>
        public uint Cells() => this.cells;

        /// <summary>The width of every key, in bytes.</summary>
        public int KeySize() => this.keySize;
    }
}
