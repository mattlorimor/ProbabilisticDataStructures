using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Memento answers range emptiness questions about a set that keeps changing, as
    /// described by Eslami and Dayan in Memento Filter: A Fast, Dynamic, and Robust
    /// Range Filter (SIGMOD 2025).
    /// </summary>
    /// <remarks>
    /// <see cref="Grafite"/> answers the same question -- is there anything in
    /// [a, b]? -- with a bound that holds against any workload, but it is built once
    /// from a known set and never changes. Memento gives up nothing on robustness and
    /// adds what Grafite cannot do: insert, delete, and grow.
    /// <para>
    /// It works by cutting each key in two. The low bits, called the memento, are as
    /// many as the largest range you mean to ask about; everything above them is the
    /// prefix. The prefix partitions the universe into blocks the width of that
    /// largest range, and the filter stores, for each occupied block, a fingerprint of
    /// the block together with the mementos of the keys inside it -- the exact
    /// positions those keys occupy within their block.
    /// </para>
    /// <para>
    /// That makes a range query two lookups rather than a search. Because the range is
    /// no wider than a block, it touches at most two of them; the filter finds those
    /// blocks and asks whether any memento in them lands inside the range. The
    /// mementos are what keep it honest: a filter storing only prefixes would answer
    /// "possibly" for any range touching an occupied block, however far the keys
    /// inside it are from the range actually asked about.
    /// </para>
    /// <para>
    /// Because a memento is a piece of the key rather than a hash of it, the mapping
    /// from key to stored bits runs one way only. That is what makes deletion sound
    /// here where it is not in a prefix-only design, and it is the whole reason this
    /// structure can be used behind a B-tree rather than only behind a
    /// write-once index.
    /// </para>
    /// <para>
    /// Keys are numbers rather than bytes, as with <see cref="Grafite"/>: a range is a
    /// question about order, and bytes have none a caller would agree with.
    /// </para>
    /// </remarks>
    public class MementoFilter : IBinaryPersistable<MementoFilter>
    {
        private const double ExpansionThreshold = 0.75;

        /// <summary>
        /// The tables, newest first, exactly as <see cref="InfiniFilter"/> keeps
        /// them.
        /// </summary>
        internal List<MementoSegment> Segments { get; set; } =
            new List<MementoSegment>();

        /// <summary>
        /// How many bits of each key are kept verbatim as its position within a
        /// block.
        /// </summary>
        internal int MementoBits { get; set; }

        /// <summary>
        /// How many keys have been added, less those removed.
        /// </summary>
        internal ulong Added { get; set; }

        /// <summary>
        /// How many times the active table has doubled.
        /// </summary>
        internal uint Expansions { get; set; }

        private MementoSegment Active => this.Segments[0];

        /// <summary>
        /// The largest range the filter answers about without losing accuracy.
        /// </summary>
        public ulong MaxRangeSize => 1UL << this.MementoBits;

        /// <summary>
        /// Creates a filter that grows as it is filled.
        /// </summary>
        /// <param name="maxRangeSize">
        /// The widest range you mean to ask about. This sets how many low bits of each
        /// key are stored verbatim: a wider promise costs a bit per key per doubling
        /// of the range. Ranges wider than this are still answered without false
        /// negatives, at the cost of touching more blocks.
        /// </param>
        /// <param name="fingerprintBits">
        /// Bits of fingerprint per block. This sets how often two unrelated blocks are
        /// confused for one another, which is the filter's main source of false
        /// positives.
        /// </param>
        /// <param name="initialCapacity">
        /// Roughly how many keys to make room for before the first expansion.
        /// </param>
        public MementoFilter(
            ulong maxRangeSize = 256,
            int fingerprintBits = 8,
            uint initialCapacity = 1024)
        {
            if (maxRangeSize < 2 || maxRangeSize > (1UL << 32))
            {
                throw new ArgumentOutOfRangeException(nameof(maxRangeSize),
                    maxRangeSize,
                    "The largest range must be between 2 and 2^32. A range of one is " +
                    "a point query, which an ordinary filter answers in less space, " +
                    "and beyond 2^32 the mementos cost more than the keys.");
            }
            if (fingerprintBits < 2 || fingerprintBits > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(fingerprintBits),
                    fingerprintBits,
                    "The fingerprint must be between 2 and 32 bits.");
            }
            Guard.ValidItemCount(initialCapacity, nameof(initialCapacity));

            this.MementoBits = (int)Math.Ceiling(Math.Log2(maxRangeSize));

            this.Segments.Add(new MementoSegment(
                InitialQuotientBits(initialCapacity), fingerprintBits, this.MementoBits));
        }

        /// <summary>
        /// The most address bits a table has: its slot count is a uint holding 2^q, and
        /// 2^32 is not one.
        /// </summary>
        internal static int MaxQuotientBits => 31;

        /// <summary>
        /// The most address bits this filter lets a table grow to. It is
        /// <see cref="MaxQuotientBits"/>; a test lowers it to reach the refusal at the
        /// limit without building a table of gigabytes.
        /// </summary>
        internal int QuotientBitsLimit { get; set; } = MaxQuotientBits;

        /// <summary>
        /// The address bits the first table gets for an initial capacity: enough slots
        /// that the capacity fills no more than the expansion threshold of them, a
        /// power of two, and at least four.
        /// </summary>
        internal static int InitialQuotientBits(uint initialCapacity)
        {
            // Clamped at the widest table rather than left to a uint that saturates: the
            // capacity is where the filter starts and not a limit on it, so one that asks
            // for more than the widest table starts at the widest.
            var wanted = Math.Ceiling(Math.Log2(initialCapacity / ExpansionThreshold));
            return (int)Math.Clamp(wanted, 2, MaxQuotientBits);
        }

        private MementoFilter(bool empty)
        {
        }

        /// <summary>
        /// The hash of a block's prefix. Keys are numbers here, so this mixes one
        /// rather than hashing bytes.
        /// </summary>
        /// <remarks>
        /// SplitMix64's finalizer, which is the mixing step of the generator
        /// <see cref="SeededRandom"/> uses. It is not seeded: unlike
        /// <see cref="Grafite"/>, whose guarantee rests on an adversary not knowing
        /// the hash, this filter's accuracy comes from the mementos, which are the
        /// key's own bits and cannot be gamed by choosing keys.
        /// </remarks>
        internal static ulong HashPrefix(ulong prefix)
        {
            var z = prefix + 0x9E3779B97F4A7C15;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            return z ^ (z >> 31);
        }

        private ulong PrefixOf(ulong key) => key >> this.MementoBits;

        private ulong MementoOf(ulong key) => key & ((1UL << this.MementoBits) - 1);

        /// <summary>
        /// Adds a key. Returns the filter to allow for chaining.
        /// </summary>
        /// <param name="key">The key to add.</param>
        public MementoFilter Add(ulong key)
        {
            if (this.Active.Load >= ExpansionThreshold)
            {
                Expand();
            }

            var hash = HashPrefix(PrefixOf(key));
            PlaceMemento(this.Active, hash, 0, MementoOf(key));
            this.Added++;
            return this;
        }

        /// <summary>
        /// Adds one memento to the block a hash names. The table does the work; this
        /// only turns a hash into the quotient and fingerprint it names, so that
        /// there is one implementation of editing a box rather than two that have to
        /// be kept in step.
        /// </summary>
        private static void PlaceMemento(
            MementoSegment segment, ulong hash, int age, ulong memento)
        {
            var (quotient, fingerprint) = segment.Split(hash);
            segment.InsertMemento(quotient, age, fingerprint, memento);
        }

        /// <summary>
        /// Whether the filter might hold the key.
        /// </summary>
        /// <param name="key">The key to test.</param>
        public bool Test(ulong key) => TestRange(key, key);

        /// <summary>
        /// Whether any stored key might fall in [low, high], both ends included. False
        /// is certain; true is not.
        /// </summary>
        /// <param name="low">The low end of the range, included.</param>
        /// <param name="high">The high end of the range, included.</param>
        public bool TestRange(ulong low, ulong high)
        {
            if (low > high)
            {
                throw new ArgumentException(
                    $"The range [{low}, {high}] runs backwards. A range filter cannot " +
                    "guess which end was meant.",
                    nameof(low));
            }

            var firstBlock = PrefixOf(low);
            var lastBlock = PrefixOf(high);
            var lowMemento = MementoOf(low);
            var highMemento = MementoOf(high);

            if (firstBlock == lastBlock)
            {
                return BlockHasMementoBetween(firstBlock, lowMemento, highMemento);
            }

            // The range runs off the end of its first block and into the last one, so
            // each end is tested against the part of its own block the range covers.
            var mementoMask = (1UL << this.MementoBits) - 1;
            if (BlockHasMementoBetween(firstBlock, lowMemento, mementoMask) ||
                BlockHasMementoBetween(lastBlock, 0, highMemento))
            {
                return true;
            }

            // Any block wholly inside the range is a positive if it holds anything at
            // all. A range no wider than the promised maximum spans at most two
            // blocks, so usually there are none of these.
            for (var block = firstBlock + 1; block < lastBlock; block++)
            {
                if (BlockHasMementoBetween(block, 0, mementoMask))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether a block holds a key whose position within it falls in the given
        /// span.
        /// </summary>
        private bool BlockHasMementoBetween(ulong block, ulong low, ulong high)
        {
            var hash = HashPrefix(block);
            foreach (var segment in this.Segments)
            {
                if (segment.HasMementoBetween(hash, low, high))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Removes a key if it is present.
        /// </summary>
        /// <param name="key">The key to remove.</param>
        /// <returns>Whether the key was present, and so was removed.</returns>
        /// <remarks>
        /// A memento is a piece of the key rather than a hash of it, so a matching
        /// memento means a key at exactly this position within its block. Which block
        /// is only as certain as the fingerprint. A box that has lived through
        /// expansions has given up fingerprint bits, and it stands for every block
        /// whose hash agrees with it on the bits it has left. So one removal can match
        /// several boxes, and they are nested: each remembers a longer stretch of the
        /// same hash than the last, so every block a longer match stands for, a shorter
        /// one stands for too.
        /// <para>
        /// The entry removed is therefore the longest match, the one that remembers the
        /// most of its hash, as Eslami and Dayan specify for deletion. Removing a
        /// shorter match could take away the only record of a key that was never
        /// removed, which is a false negative. The shorter matches left behind go on
        /// answering for whatever else they stood for.
        /// </para>
        /// <para>
        /// What a match remembers is its address and its fingerprint together. Within
        /// one table every box at a quotient has the same address, so the youngest
        /// box is the longest. Across the chain the tables are asked newest first,
        /// and that order is itself longest first. An entry reaches a later table only
        /// by going void, and then it remembers no more of its hash than the address of
        /// the table it left, which has doubled since. Everything still in that table
        /// remembers at least the table's current address, which is longer. So the
        /// first table with a match holds the longest one, even when its fingerprint
        /// is shorter than a later table's.
        /// </para>
        /// </remarks>
        public bool TestAndRemove(ulong key)
        {
            var hash = HashPrefix(PrefixOf(key));
            var memento = MementoOf(key);

            foreach (var segment in this.Segments)
            {
                var (quotient, _) = segment.Split(hash);
                var codec = segment.Codec;
                var boxes = codec.Decode(segment.ReadRun(quotient));

                var longest = -1;
                var youngest = int.MaxValue;
                for (var i = 0; i < boxes.Count; i++)
                {
                    if (!segment.FingerprintMatches(boxes[i].Fingerprint, hash) ||
                        !boxes[i].Mementos.Contains(memento))
                    {
                        continue;
                    }

                    var age = segment.AgeOfFluid(boxes[i].Fingerprint);
                    if (age < youngest)
                    {
                        youngest = age;
                        longest = i;
                    }
                }

                if (longest < 0)
                {
                    continue;
                }

                var mementos = boxes[longest].Mementos;
                mementos.RemoveAt(mementos.IndexOf(memento));
                if (mementos.Count == 0)
                {
                    boxes.RemoveAt(longest);
                }

                segment.RewriteRun(quotient, codec.Encode(boxes));
                this.Added--;
                return true;
            }
            return false;
        }

        private void Expand()
        {
            // Refused before anything is built or moved, so that the filter is exactly
            // what it was. The widest table in the chain decides, which is the first one
            // -- the tables behind it are narrower by more than the fingerprint they were
            // made from -- but the check does not rest on that.
            var widest = 0;
            foreach (var table in this.Segments)
            {
                widest = Math.Max(widest, table.QuotientBits);
            }

            if (widest + 1 > this.QuotientBitsLimit)
            {
                throw new InvalidOperationException(
                    $"The filter's widest table has {1UL << widest:N0} slots and is at its " +
                    "expansion threshold, and doubling it would pass the " +
                    $"{1UL << this.QuotientBitsLimit:N0} a table can have: a table's slot " +
                    "count is a 32-bit number holding 2^q, and 2^32 is not one. Nothing was " +
                    "added and the filter is unchanged; it can still be queried and " +
                    "written, and removing keys makes room for more. To hold more keys, " +
                    "split them over several filters.");
            }

            ExpandTable(0);
            this.Expansions++;
        }

        /// <summary>
        /// Doubles one table, ageing every entry that lived through it and moving
        /// aside those with nothing left to spend.
        /// </summary>
        /// <remarks>
        /// The address bit an expansion needs comes from the fingerprint, never from
        /// the memento. The memento is a piece of the key rather than of its hash, so
        /// spending it would not merely cost accuracy -- it would move key data into
        /// an address and answer about a key nobody stored.
        /// </remarks>
        private void ExpandTable(int index)
        {
            var old = this.Segments[index];

            if (old.QuotientBits + 1 + old.FingerprintBits > 64)
            {
                throw new InvalidOperationException(
                    $"A table cannot expand past {old.Slots} slots: its address and " +
                    "fingerprint together would need more bits than the hash has.");
            }

            var grown = new MementoSegment(
                old.QuotientBits + 1, old.FingerprintBits, old.MementoBits);
            var voided = new List<(uint Address, ulong Memento)>();

            for (uint quotient = 0; quotient < old.Slots; quotient++)
            {
                foreach (var box in old.Codec.Decode(old.ReadRun(quotient)))
                {
                    var age = old.AgeOfFluid(box.Fingerprint);
                    var fingerprint = old.FingerprintOfFluid(box.Fingerprint, age);

                    if (age >= old.FingerprintBits)
                    {
                        foreach (var memento in box.Mementos)
                        {
                            voided.Add((quotient, memento));
                        }
                        continue;
                    }

                    // The address bit comes from the fingerprint. Every key of the box
                    // shares that fingerprint, so the whole box moves together.
                    var moved = quotient + ((fingerprint & 1) == 1 ? old.Slots : 0u);
                    foreach (var memento in box.Mementos)
                    {
                        grown.InsertMemento(moved, age + 1, fingerprint >> 1, memento);
                    }
                }
            }

            this.Segments[index] = grown;

            if (voided.Count > 0)
            {
                ShedInto(index + 1, voided, old.QuotientBits, old.FingerprintBits);
            }
        }

        /// <summary>
        /// Moves entries that have run out of fingerprint into the next table down,
        /// expanding it first if it has filled up.
        /// </summary>
        private void ShedInto(
            int index,
            List<(uint Address, ulong Memento)> entries,
            int addressBits,
            int fingerprintBits)
        {
            if (this.Segments.Count == index)
            {
                this.Segments.Add(new MementoSegment(
                    Math.Max(2, addressBits - fingerprintBits),
                    fingerprintBits,
                    this.MementoBits));
            }

            foreach (var (address, memento) in entries)
            {
                if (this.Segments[index].Load >= ExpansionThreshold)
                {
                    ExpandTable(index);
                }

                var target = this.Segments[index];
                var quotient = (uint)(address & (target.Slots - 1));
                var carried = (ulong)address >> target.QuotientBits;
                var spare = addressBits - target.QuotientBits;
                var age = Math.Clamp(fingerprintBits - spare, 0, fingerprintBits);
                target.InsertMemento(quotient, age, carried, memento);
            }
        }

        /// <summary>
        /// How many keys the filter holds.
        /// </summary>
        public ulong Count() => this.Added;

        /// <summary>
        /// How many slots the filter has across every table.
        /// </summary>
        public ulong Capacity()
        {
            var total = 0UL;
            foreach (var segment in this.Segments)
            {
                total += segment.Slots;
            }
            return total;
        }

        /// <summary>
        /// How many times the filter has doubled.
        /// </summary>
        public uint ExpansionCount() => this.Expansions;

        /// <summary>
        /// How many tables the chain holds.
        /// </summary>
        public int ChainLength() => this.Segments.Count;

        /// <summary>
        /// The tables' size in bytes.
        /// </summary>
        public ulong SizeInBytes()
        {
            var total = 0UL;
            foreach (var segment in this.Segments)
            {
                total += (ulong)segment.Data.Length * 8;
            }
            return total;
        }

        /// <summary>
        /// Restores the filter to its original state.
        /// </summary>
        public MementoFilter Reset()
        {
            var first = this.Segments[0];
            var quotientBits = first.QuotientBits - (int)this.Expansions;
            this.Segments = new List<MementoSegment>
            {
                new MementoSegment(
                    Math.Max(2, quotientBits), first.FingerprintBits, this.MementoBits),
            };
            this.Added = 0;
            this.Expansions = 0;
            return this;
        }

        /// <summary>
        /// Writes the filter to a stream in the library's persistence format.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = new PayloadWriter();
            payload.WriteUInt64(this.Added);
            payload.WriteUInt32(this.Expansions);
            payload.WriteUInt32((uint)this.MementoBits);
            payload.WriteUInt32((uint)this.Segments.Count);

            foreach (var segment in this.Segments)
            {
                payload.WriteUInt32((uint)segment.QuotientBits);
                payload.WriteUInt32((uint)segment.FingerprintBits);
                payload.WriteUInt32(segment.Count);
                payload.WriteUInt32((uint)segment.Data.Length);
                foreach (var word in segment.Data)
                {
                    payload.WriteUInt64(word);
                }
            }

            PersistenceFormat.Write(
                stream, StructureId.MementoFilter, HashId.None, payload.WrittenSpan);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        public static MementoFilter ReadFrom(Stream stream) =>
            Read(stream, null, ReadOptions.Default);

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
        public static MementoFilter ReadFrom(Stream stream, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, null, options);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>. The filter holds numbers
        /// rather than bytes, so supplying a hash function is refused.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">Not used, and refused if supplied.</param>
        public static MementoFilter ReadFrom(
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
        public static MementoFilter ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, hash, options);
        }

        private static MementoFilter Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.MementoFilter);
            var payload = PersistenceFormat.Read(
                stream, StructureId.MementoFilter, out var hashId, budget);

            if (hash is not null)
            {
                throw new InvalidDataException(
                    "A Memento filter holds numbers rather than bytes -- a range is a " +
                    "question about order -- so it cannot be read with a supplied " +
                    "hash function. Read it with the overload that takes none.");
            }
            if (hashId != HashId.None)
            {
                throw new InvalidDataException(
                    $"Payload names hash function {(ushort)hashId}, and a Memento " +
                    "filter mixes numbers rather than hashing bytes. It was not " +
                    "written by this structure.");
            }

            var reader = new PayloadReader(payload, budget);
            var added = reader.ReadUInt64();
            var expansions = reader.ReadUInt32();
            var mementoBits = (int)reader.ReadUInt32();
            var segmentCount = reader.ReadUInt32();

            if (mementoBits < 1 || mementoBits > 32)
            {
                throw new InvalidDataException(
                    $"Filter claims {mementoBits} memento bits, and this library " +
                    "builds between 1 and 32.");
            }
            if (segmentCount == 0)
            {
                throw new InvalidDataException(
                    "Filter carries no tables at all, and every query begins by " +
                    "asking the first one.");
            }
            if (segmentCount > 64)
            {
                throw new InvalidDataException(
                    $"Filter claims a chain of {segmentCount} tables, longer than a " +
                    "64-bit hash could ever justify.");
            }

            var filter = new MementoFilter(true)
            {
                Added = added,
                Expansions = expansions,
                MementoBits = mementoBits,
                Segments = new List<MementoSegment>((int)segmentCount),
            };

            for (var i = 0; i < segmentCount; i++)
            {
                var quotientBits = (int)reader.ReadUInt32();
                var fingerprintBits = (int)reader.ReadUInt32();
                var count = reader.ReadUInt32();
                var words = reader.ReadUInt32();

                if (quotientBits < 2 || quotientBits > MaxQuotientBits)
                {
                    throw new InvalidDataException(
                        $"Table {i} claims {quotientBits} bits of address, and this " +
                        $"library builds tables between 2 and {MaxQuotientBits}: a table's " +
                        "slot count is a 32-bit number holding 2^q, and 2^32 is not one.");
                }
                if (fingerprintBits < 2 || fingerprintBits > 32)
                {
                    throw new InvalidDataException(
                        $"Table {i} claims {fingerprintBits}-bit fingerprints, and " +
                        "this library builds between 2 and 32.");
                }
                if (quotientBits + fingerprintBits > 64)
                {
                    throw new InvalidDataException(
                        $"Table {i} would need {quotientBits + fingerprintBits} bits " +
                        "of hash to address and fingerprint a block, and a hash has " +
                        "sixty-four.");
                }

                var slots = 1u << quotientBits;
                if (count > slots)
                {
                    throw new InvalidDataException(
                        $"Table {i} holds {count} entries in {slots} slots, and it " +
                        "stores one entry per slot.");
                }

                var expected =
                    (((long)slots * (fingerprintBits + 1 + mementoBits + 3)) >> 6) + 2;
                if (words != expected)
                {
                    throw new InvalidDataException(
                        $"Table {i} carries {words} words where its shape needs " +
                        $"{expected}.");
                }

                // Read into one array, then copied into the table the segment builds.
                budget.Charge(ReadBudget.Sum(
                    ReadBudget.Times(2, ReadBudget.ArrayOf(words, sizeof(ulong))),
                    ReadBudget.SmallObject));
                var data = new ulong[words];
                for (var w = 0; w < words; w++)
                {
                    data[w] = reader.ReadUInt64();
                }

                filter.Segments.Add(MementoSegment.Restore(
                    quotientBits, fingerprintBits, mementoBits, count, data));
            }

            reader.ExpectEnd();
            return filter;
        }
    }
}
