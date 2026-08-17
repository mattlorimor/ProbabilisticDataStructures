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

            var slots = (uint)Math.Max(4.0,
                Math.Pow(2, Math.Ceiling(Math.Log2(initialCapacity / ExpansionThreshold))));
            this.Segments.Add(new MementoSegment(
                (int)Math.Log2(slots), fingerprintBits, this.MementoBits));
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
            var (quotient, fingerprint) = this.Active.Split(hash);
            this.Active.Insert(quotient, 0, fingerprint, MementoOf(key));
            this.Added++;
            return this;
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
        /// A memento is a piece of the key rather than a hash of it, so a match on
        /// both fingerprint and memento is a much stronger statement than a
        /// fingerprint match alone. That is what makes removing sound here: the entry
        /// taken away stood for a key at exactly this position in exactly this block.
        /// </remarks>
        public bool TestAndRemove(ulong key)
        {
            var hash = HashPrefix(PrefixOf(key));
            var memento = MementoOf(key);

            foreach (var segment in this.Segments)
            {
                if (segment.Remove(hash, memento))
                {
                    this.Added--;
                    return true;
                }
            }
            return false;
        }

        private void Expand()
        {
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

            foreach (var (quotient, field) in old.Entries())
            {
                var age = old.AgeOf(field);
                var memento = old.MementoOf(field);

                if (age >= old.FingerprintBits)
                {
                    voided.Add((quotient, memento));
                    continue;
                }

                var fingerprint = old.FingerprintOf(field, age);
                var moved = quotient + ((fingerprint & 1) == 1 ? old.Slots : 0u);
                grown.Insert(moved, age + 1, fingerprint >> 1, memento);
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
                target.Insert(quotient, age, carried, memento);
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
        public static MementoFilter ReadFrom(Stream stream) => Read(stream, null);

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
            return Read(stream, hash);
        }

        private static MementoFilter Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(
                stream, StructureId.MementoFilter, out var hashId);

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

            var reader = new PayloadReader(payload);
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

                if (quotientBits < 2 || quotientBits > 40)
                {
                    throw new InvalidDataException(
                        $"Table {i} claims {quotientBits} bits of address, and this " +
                        "library builds tables between 2 and 40.");
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
