using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

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
    /// rather than a partial answer. At the difference it was sized for it does so at
    /// most one time in a hundred.
    /// </para>
    /// <para>
    /// Add each key at most once per side. The cells count additions rather than record
    /// membership, so a repeat is not absorbed. A key added twice to one table leaves a
    /// count of two, which peeling never takes out, and <see cref="TryDecode"/> returns
    /// false. A key added twice to one table and once to the other is listed as a
    /// difference although both sets hold it. And two different keys that share all their
    /// cells cancel completely if one is added twice to each table, or if one is added
    /// twice and the other removed twice in one, leaving every field zero, so the decode
    /// reports an empty difference for sets that differ. Deduplicate before adding.
    /// </para>
    /// </remarks>
    public class InvertibleBloomLookupTable : IBinaryPersistable<InvertibleBloomLookupTable>, IPayloadBody
    {
        /// <summary>
        /// How many cells each key is placed in. Four is the usual choice. Peeling lists
        /// everything only when the table has more than about 1.3 cells per difference,
        /// which is the asymptotic threshold for four hashes (see
        /// <see cref="CellsPerDifference"/>), and more hashes would not lower that
        /// threshold, only make each key touch more cells.
        /// </summary>
        private const int HashCount = 4;

        /// <summary>
        /// Cells per expected difference once a table is big enough for the asymptote to
        /// be the whole story. Complete listing needs more than c4 = 1.295 cells per
        /// entry -- the 2-core threshold for four hashes, Table 1 of the paper (1.222 is
        /// the three-hash figure); 1.5 leaves room.
        /// </summary>
        /// <remarks>
        /// <b>What sizing promises (#150).</b> A table sized for d differences lists all
        /// of them, whichever sides they fall on, with probability at least 0.99, at
        /// every d. The 0.99 is the figure Eppstein, Goodrich, Uyeda and Varghese,
        /// "What's the Difference?" (SIGCOMM 2011), section 6.1 and Figure 7, use for the
        /// overhead a table needs.
        /// <para>
        /// The threshold is asymptotic -- so is Goodrich and Mitzenmacher's failure
        /// probability, and their section 5 leaves finite sizes as an open "finite-length
        /// analysis" -- and small tables need well over it. Measured here, with the cell
        /// placement below and four hashes, the cells needed for 0.99 are about 2.4 per
        /// difference at ten differences, 1.7 at forty, 1.5 at a hundred, and 1.36 at a
        /// thousand, settling towards the threshold beyond. Figure 7 has the same curve
        /// for four hashes, read off by eye: 2.3, 1.6, 1.5 and 1.4. Its text says "Deltas
        /// below 200 all require at least 50% overhead", which is to say this 1.5 alone
        /// is a floor for small differences, not what they need.
        /// </para>
        /// <para>
        /// Between sixteen and a hundred differences the measured need stays within about
        /// a cell of 1.4 d + 12. A table below 129 differences therefore gets 1.4 d + 13
        /// cells, rounded up, wherever that is more than 1.5 d; from 129 differences up
        /// the two meet and it is 1.5 d, as it always was. The extra cell is margin.
        /// Measured at that size, the lowest rate of complete listing at any d from 1 to
        /// 260 was 0.9948, for a difference split between the sides and, over the first
        /// sixty-four, for one all on a side as well. With the allowance a cell smaller
        /// the lowest is 0.9927, and with two cells fewer 0.989, under the promise.
        /// Beyond a thousand the sampled sizes listed every time, as the asymptotic
        /// analysis says they should.
        /// </para>
        /// <para>
        /// So a table sized for one difference has fifteen cells, where four would do: a
        /// single line cannot follow the curve at both ends, and the cost at the small end
        /// is a few hundred bytes. Nothing gets fewer cells than it did before, and a
        /// table sized for 129 differences or more is the size it always was.
        /// </para>
        /// </remarks>
        internal const double CellsPerDifference = 1.5;

        /// <summary>
        /// The slope of the line small tables follow, in cells per difference. See
        /// <see cref="CellsPerDifference"/> for what it was fitted to.
        /// </summary>
        private const double SmallTableCellsPerDifference = 1.4;

        /// <summary>
        /// The cells added to that line: its intercept, plus a cell of margin.
        /// </summary>
        private const int SmallTableExtraCells = 13;

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
        /// <para>
        /// A table sized for this many lists a difference of this many completely, on
        /// whichever sides it falls, with probability at least 0.99, however small the
        /// number; a smaller difference only helps, since keys that peel completely still
        /// do with some of them gone. The rest of the time <see cref="TryDecode"/>
        /// returns false. Small numbers get disproportionately many cells to keep that
        /// promise -- a table sized for one difference has fifteen -- and from 129 up it
        /// is 1.5 cells per difference.
        /// </para>
        /// </param>
        /// <param name="keySize">
        /// The size of every key in bytes. Keys are combined by exclusive-or, so they all
        /// have to be the same width.
        /// </param>
        /// <param name="hash">The hash function to use, or null for the default.</param>
        public InvertibleBloomLookupTable(
            uint expectedDifferences, int keySize, Func<ReadOnlySpan<byte>, ulong>? hash = null)
            : this(keySize, hash, CellsProvisionedFor(expectedDifferences))
        {
        }

        /// <summary>
        /// The one place a table is allocated, whether its size came from a number of
        /// differences or was given outright.
        /// </summary>
        private InvertibleBloomLookupTable(
            int keySize, Func<ReadOnlySpan<byte>, ulong>? hash, uint cells,
            byte[]? keySums = null, long[]? counts = null, ulong[]? hashSums = null)
        {
            if (keySize <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(keySize), keySize, "Keys must be at least one byte wide.");
            }

            ValidTableSize(cells, keySize);

            this.keySize = keySize;
            this.cells = cells;
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();

            // A reader that already holds the counts, keys and hashes, at the lengths its cell
            // count implies, hands them over rather than have them allocated and then replaced.
            this.counts = counts ?? new long[this.cells];
            this.keySums = keySums ?? new byte[(long)this.cells * keySize];
            this.hashSums = hashSums ?? new ulong[this.cells];
        }

        /// <summary>
        /// How many cells a table sized for a number of differences gets: the larger of
        /// the asymptotic rule and the small-table line, both described at
        /// <see cref="CellsPerDifference"/>. The line is always at least HashCount, so
        /// no floor is needed beside it.
        /// </summary>
        internal static uint CellsProvisionedFor(uint expectedDifferences)
        {
            Guard.ValidItemCount(expectedDifferences, nameof(expectedDifferences));

            var asymptotic = Math.Ceiling(expectedDifferences * CellsPerDifference);
            var small = Math.Ceiling(expectedDifferences * SmallTableCellsPerDifference)
                + SmallTableExtraCells;

            var cells = Math.Max(asymptotic, small);

            // Checked as a double, ahead of the cast. Past what a uint holds the cast
            // saturates instead of wrapping, so a count too large for any table comes out
            // looking like any other cell count and is left for an allocator to refuse.
            if (cells > MaxCells)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expectedDifferences), expectedDifferences,
                    $"Sizing for {expectedDifferences} differences needs {cells:F0} cells, " +
                    $"and a table can have at most {MaxCells}: its counts and hash sums " +
                    "each take an array with a slot per cell, and no array holds more " +
                    $"than that. That allows {MaxExpectedDifferences} differences at " +
                    "most, and fewer for keys wider than a byte. Size the table for fewer.");
            }

            return (uint)cells;
        }

        /// <summary>
        /// The most cells a table can have. The counts, the hash sums and the keys are each
        /// one array with a slot, or a key, for every cell, and an array cannot hold more
        /// than <see cref="Array.MaxLength"/> elements, so that is the bound whatever the
        /// key size. Keys wider than a byte reach the array's bound sooner, which
        /// <see cref="ValidTableSize"/> checks.
        /// </summary>
        internal static uint MaxCells => (uint)Array.MaxLength;

        /// <summary>
        /// The most differences a table can be sized for: the largest whose cells, by the
        /// plain rate that applies at this size, are no more than <see cref="MaxCells"/>.
        /// </summary>
        internal static uint MaxExpectedDifferences => (uint)(MaxCells / CellsPerDifference);

        /// <summary>
        /// Refuses a table whose arrays cannot exist. <see cref="CellsProvisionedFor"/>
        /// bounds the cells that follow from a number of differences; this bounds the
        /// cells themselves, for a size given outright, and the keys, which are stored in
        /// one array of the cells times the key size and so limit the two together.
        /// Everything the table allocates comes after it.
        /// </summary>
        internal static void ValidTableSize(uint cells, int keySize)
        {
            if (cells > MaxCells)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cells), cells,
                    $"A table of {cells} cells is more than one can have, which is " +
                    $"{MaxCells}: its counts and hash sums each take an array with a " +
                    "slot per cell, and no array holds more than that.");
            }

            var keyBytes = (long)cells * keySize;
            if (keyBytes > Array.MaxLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(keySize), keySize,
                    $"{cells} cells of {keySize}-byte keys need {keyBytes} bytes for the " +
                    $"keys, which is more than the {Array.MaxLength} a single array can " +
                    $"hold. With this many cells the keys can be at most " +
                    $"{Array.MaxLength / cells} bytes wide; keys this wide fit in at most " +
                    $"{Array.MaxLength / keySize} cells, which is a table sized for fewer " +
                    "differences.");
            }
        }

        /// <summary>
        /// A table with exactly this many cells, for tests that need one of a size the
        /// sizing rule would not choose -- the four cells every key fills, or the size
        /// an earlier version gave for a number of differences. It allocates through the
        /// same constructor as the public one.
        /// </summary>
        internal static InvertibleBloomLookupTable WithCells(
            uint cells, int keySize, Func<ReadOnlySpan<byte>, ulong>? hash = null)
        {
            if (cells < HashCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cells), cells,
                    $"A key occupies {HashCount} cells, so a table needs at least that many.");
            }

            return new InvertibleBloomLookupTable(keySize, hash, cells);
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
        /// Whether everything was recovered: true only when, with the listed keys taken
        /// out, nothing at all is left in the table. False means peeling stalled with keys
        /// still in it -- likely once it holds more than it was sized for, and at most one
        /// time in a hundred at the difference it was sized for -- in which case the two
        /// lists are the part that came out before it did, which is not an answer.
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

            // Peeling stops when no cell holds exactly one key, which is success only if
            // nothing at all is left. Empty means every field, not only the counts: two
            // keys on opposite sides that share their cells cancel each other's counts and
            // leave both of themselves in the sums, so a table still holding keys can have
            // every count at zero. Eppstein et al., "What's the Difference?" (2011),
            // Algorithm 3, fails the decode on any cell whose idSum, hashSum or count is
            // non-zero.
            for (var cell = 0; cell < this.cells; cell++)
            {
                if (counts[cell] != 0)
                {
                    return false;
                }

                if (hashSums[cell] != 0)
                {
                    return false;
                }
            }

            if (keySums.AsSpan().ContainsAnyExcept((byte)0))
            {
                return false;
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
        /// <para>
        /// The two must have the same number of cells, and for a table sized for fewer
        /// than 129 expected differences that number depends on the version of the
        /// library that built it: a table built by 6.2.0 or earlier has fewer cells than
        /// one built now for the same number of differences, because small tables were
        /// given more cells in the release after it. Such a pair is refused. Build both
        /// with one version, and rebuild a stored small table rather than subtracting it
        /// from a new one.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">The other table is null.</exception>
        /// <exception cref="ArgumentException">
        /// The tables have different dimensions, key sizes, or hash functions. Different
        /// dimensions include a pair built by different versions of the library for
        /// fewer than 129 expected differences.
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
                    "under each, so nothing would cancel. If the two were built by " +
                    "different versions of this library, that may be the cause: for " +
                    "fewer than 129 expected differences the versions give different " +
                    "cell counts, so rebuild both with one version.",
                    nameof(other));
            }

            Guard.SameHashFunction(this.Hash, other.Hash, nameof(other));

            var result = new InvertibleBloomLookupTable(this.keySize, this.Hash, this.cells);

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
        public void WriteTo(Stream stream) => PersistenceFormat.Write(stream, this);

        EnvelopeHeader IPayloadBody.WritePayload(PayloadWriter payload)
        {
            payload.WriteUInt32(this.cells);
            payload.WriteUInt32((uint)this.keySize);

            // Three runs, each sized by the shape the header gives. Counts are signed: a
            // subtracted table is mostly negative, and reading them back unsigned would
            // turn every one of those into an enormous positive.
            if (payload.BeginRun((long)this.cells * sizeof(long)))
            {
                foreach (var count in this.counts)
                {
                    payload.WriteInt64(count);
                }
            }

            if (payload.BeginRun((long)this.cells * this.keySize))
            {
                payload.WriteRaw(this.keySums);
            }

            if (payload.BeginRun((long)this.cells * sizeof(ulong)))
            {
                foreach (var hash in this.hashSums)
                {
                    payload.WriteUInt64(hash);
                }
            }

            return new(StructureId.InvertibleBloomLookupTable, PersistenceFormat.Identify(this.Hash));
        }

        /// <summary>
        /// Reads a table written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The table that was written.</returns>
        public static InvertibleBloomLookupTable ReadFrom(Stream stream)
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
        public static InvertibleBloomLookupTable ReadFrom(Stream stream, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, null, options);
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
        public static InvertibleBloomLookupTable ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, hash, options);
        }

        private static InvertibleBloomLookupTable Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.InvertibleBloomLookupTable);
            return PersistenceFormat.Read(
                stream, StructureId.InvertibleBloomLookupTable, budget, Price, hash, PersistenceFormat.CheckResolvable,
                ReadPayload);
        }

        /// <summary>
        /// Reads the payload of the envelope <c>Read</c> opened, once the envelope has
        /// been checked. See <see cref="PayloadParse{T}"/>.
        /// </summary>
        private static InvertibleBloomLookupTable ReadPayload(
            ref PayloadReader reader, ushort version, HashId hashId,
            Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            var budget = reader.Budget;

            var cells = reader.ReadUInt32();
            var keySize = (int)reader.ReadUInt32();
            var keyBytes = (long)cells * keySize;
            var wordBytes = (long)cells * sizeof(long);

            // Counts and hash sums are arrays of their own, each read from its run, which in the
            // large form can be longer than one array (#228). They are read straight into the
            // array, a piece at a time through a buffer on the stack, whether the payload is held
            // or not. Copied out of a held payload whole and then into the arrays, both were held,
            // and charged, twice (#335). The key sums are the run as it is read. Each run is held
            // to the shape as it comes, the ones still to come taken to be as the shape says.
            var counted = reader.BeginWordsInPlace();
            CheckShape(cells, keySize, counted, keyBytes, wordBytes);
            budget.Charge(ReadBudget.ArrayOf(cells, sizeof(long)));
            var counts = new long[cells];

            // A count is a signed word stored as its two's complement, so the counts are read as
            // the unsigned words that share their bits.
            reader.ReadWords(MemoryMarshal.Cast<long, ulong>(counts.AsSpan()));

            var keySums = reader.ReadBytes();
            CheckShape(cells, keySize, counted, keySums.LongLength, wordBytes);

            var hashed = reader.BeginWordsInPlace();
            CheckShape(cells, keySize, counted, keySums.LongLength, hashed);
            budget.Charge(ReadBudget.ArrayOf(cells, sizeof(ulong)));
            var hashSums = new ulong[cells];
            reader.ReadWords(hashSums);

            reader.ExpectEnd();
            budget.Charge(ObjectCost);
            return new InvertibleBloomLookupTable(
                keySize, PersistenceFormat.ResolveOrThrow(hashId, hash), cells, keySums, counts, hashSums);
        }

        /// <summary>What a read charges for the table itself, measured at 56 bytes (#346).</summary>
        internal static long ObjectCost => ReadBudget.Object(56);

        /// <summary>
        /// Prices what <see cref="Read"/> allocates beyond the envelope: the key sums, kept as the
        /// array their run is read into, and the counts and hash sums read straight from theirs
        /// into arrays of their own, each where
        /// <see cref="ReadPayload"/> charges it and after the checks it makes first. See
        /// <see cref="PayloadPrice"/>.
        /// </summary>
        /// <remarks>
        /// Where as well as how much. A price that finds its payload ending early is set
        /// aside, and the read admitted at what it had priced by then, so an array the reader
        /// charges before the price reaches it is drawn on a budget nothing priced. Both were
        /// priced once all three runs had been walked and the payload's end found, and a
        /// payload cut short in the key sums or the hash sums, or with bytes left over after
        /// them, was then refused for a budget its own price fitted, rather than as malformed.
        /// </remarks>
        internal static void Price(ref PayloadReader reader, ushort version)
        {
            var cells = reader.ReadUInt32();
            var keySize = (int)reader.ReadUInt32();
            var keyBytes = (long)cells * keySize;
            var wordBytes = (long)cells * sizeof(long);

            var counted = reader.BeginWordsInPlace();
            reader.Skip(counted);
            CheckShape(cells, keySize, counted, keyBytes, wordBytes);
            reader.Budget.Price(ReadBudget.ArrayOf(cells, sizeof(long)));

            var keySums = reader.SkipBytes();
            CheckShape(cells, keySize, counted, keySums, wordBytes);

            var hashed = reader.BeginWordsInPlace();
            reader.Skip(hashed);
            CheckShape(cells, keySize, counted, keySums, hashed);
            reader.Budget.Price(ReadBudget.ArrayOf(cells, sizeof(ulong)));

            reader.ExpectEnd();
            reader.Budget.Price(ObjectCost);
        }

        private static void CheckShape(uint cells, int keySize, long counts, long keySums, long hashes)
        {
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

            if (counts != (long)cells * sizeof(long)
                || hashes != (long)cells * sizeof(ulong)
                || keySums != (long)cells * keySize)
            {
                throw new InvalidDataException(
                    $"Table has {cells} cells of {keySize}-byte keys, and its counts, " +
                    "keys and hashes do not come to the byte counts that implies.");
            }
        }

        /// <summary>The number of cells the table holds.</summary>
        public uint Cells() => this.cells;

        /// <summary>The width of every key, in bytes.</summary>
        public int KeySize() => this.keySize;
    }
}
