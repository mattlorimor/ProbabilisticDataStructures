using System;
using System.Collections.Generic;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// InfiniFilter is a filter that grows with the data instead of being sized for it
    /// in advance, as described by Dayan, Bercea, Reviriego and Pagh in InfiniFilter:
    /// Expanding Filters to Infinity and Beyond (SIGMOD 2023).
    /// </summary>
    /// <remarks>
    /// Every other filter here needs to be told how many items it will hold. Guess low
    /// and the false positive rate climbs past what was asked for;
    /// <see cref="QuotientFilter"/> refuses outright once its slots are full. Guess
    /// high and the memory is spent whether or not the data ever arrives.
    /// <para>
    /// The obvious fix -- build a bigger filter and refill it -- needs the original
    /// keys, which is exactly what a filter exists to avoid keeping.
    /// <see cref="ScalableBloomFilter"/> avoids that by stacking a new filter beside
    /// the old one each time, so a query has to ask all of them and the false positive
    /// rates compound. InfiniFilter instead doubles one table in place: each entry
    /// gives up one fingerprint bit, which becomes the extra address bit the larger
    /// table needs. Nothing has to be rehashed, because an entry's address is a prefix
    /// of its hash and a longer prefix is one bit further along the same hash.
    /// </para>
    /// <para>
    /// Spending a fingerprint bit costs accuracy, and the paper's contribution is in
    /// how that cost is spread. Each slot carries a unary age counter saying how many
    /// expansions it has lived through, so only the entries that were actually present
    /// for an expansion pay for it. Since every expansion doubles the capacity, most
    /// entries at any moment are young and carry their full fingerprint, and the false
    /// positive rate grows with the logarithm of the item count rather than with the
    /// count itself.
    /// </para>
    /// <para>
    /// An entry that has lived through enough expansions runs out of fingerprint
    /// altogether. Those are moved into a second, smaller filter keyed by what is left
    /// of them, which in turn expands and eventually sheds its own, so the structure is
    /// a short chain of tables rather than one. That is what lets it keep growing
    /// indefinitely; the cost is that a query asks each table in the chain, and the
    /// chain grows logarithmically.
    /// </para>
    /// </remarks>
    public class InfiniFilter : IBinaryPersistable<InfiniFilter>
    {
        /// <summary>
        /// The fraction of slots that may be occupied before the filter doubles.
        /// </summary>
        /// <remarks>
        /// A quotient filter's runs lengthen sharply as it fills, so this trades
        /// memory for the cost of a lookup. Three quarters is where
        /// <see cref="QuotientFilter"/> sits and where the paper's measurements are
        /// taken.
        /// </remarks>
        private const double ExpansionThreshold = 0.75;

        /// <summary>
        /// The tables, newest first. Insertions go to the first; a query asks all of
        /// them.
        /// </summary>
        internal List<InfiniSegment> Segments { get; set; } = new List<InfiniSegment>();

        /// <summary>
        /// How many items have been added, counting repeats.
        /// </summary>
        internal ulong Added { get; set; }

        /// <summary>
        /// How many times the active table has doubled.
        /// </summary>
        internal uint Expansions { get; set; }

        /// <summary>
        /// Hash function.
        /// </summary>
        internal Func<ReadOnlySpan<byte>, ulong> Hash { get; set; }

        /// <summary>
        /// The active table, which is the one insertions go to.
        /// </summary>
        private InfiniSegment Active => this.Segments[0];

        /// <summary>
        /// Creates a filter that starts small and grows as it is filled.
        /// </summary>
        /// <param name="initialCapacity">
        /// Roughly how many items to make room for before the first expansion. The
        /// filter is not limited to this; it is where it starts.
        /// </param>
        /// <param name="fingerprintBits">
        /// Bits of fingerprint given to a freshly inserted entry. This sets the false
        /// positive rate -- about 2^-fingerprintBits for a young entry -- and also how
        /// many expansions an entry can survive before it runs out and is moved down
        /// the chain.
        /// </param>
        /// <param name="hash">
        /// The hash function to use, or null for the default.
        /// </param>
        public InfiniFilter(
            uint initialCapacity = 1024,
            int fingerprintBits = 8,
            Func<ReadOnlySpan<byte>, ulong>? hash = null)
        {
            Guard.ValidItemCount(initialCapacity, nameof(initialCapacity));
            if (fingerprintBits < 2 || fingerprintBits > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(fingerprintBits),
                    fingerprintBits,
                    "The fingerprint must be between 2 and 32 bits. Below two there " +
                    "is no accuracy to speak of and an entry goes void almost at " +
                    "once; above thirty-two the slot is wider than the hash can fill " +
                    "once the address has taken its share.");
            }

            var quotientBits = InitialQuotientBits(initialCapacity);

            this.Segments.Add(new InfiniSegment(quotientBits, fingerprintBits));
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();
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

        private InfiniFilter(bool empty)
        {
            this.Hash = null!;
        }

        /// <summary>
        /// Adds data to the filter, expanding it if it has filled up. Returns the
        /// filter to allow for chaining.
        /// </summary>
        /// <param name="data">The data to add.</param>
        public InfiniFilter Add(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            return Add(data.AsSpan());
        }

        /// <summary>
        /// Adds data to the filter, expanding it if it has filled up. Returns the
        /// filter to allow for chaining.
        /// </summary>
        /// <param name="data">The data to add.</param>
        public InfiniFilter Add(ReadOnlySpan<byte> data)
        {
            if (this.Active.Load >= ExpansionThreshold)
            {
                Expand();
            }

            var hash = this.Hash(data);
            var (quotient, fingerprint) = this.Active.Split(hash);

            // Repeats are stored rather than collapsed, for the reason
            // QuotientFilter's are: nothing here can tell one item added twice from
            // two items whose fingerprints agree, so collapsing the first would make a
            // later removal of one answer no for the other.
            this.Active.Insert(quotient, 0, fingerprint);
            this.Added++;
            return this;
        }

        /// <summary>
        /// Whether the filter might hold the data. False is certain; true is not.
        /// </summary>
        /// <param name="data">The data to test.</param>
        public bool Test(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            return Test(data.AsSpan());
        }

        /// <summary>
        /// Whether the filter might hold the data. False is certain; true is not.
        /// </summary>
        /// <param name="data">The data to test.</param>
        public bool Test(ReadOnlySpan<byte> data)
        {
            var hash = this.Hash(data);
            foreach (var segment in this.Segments)
            {
                if (segment.Holds(hash))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Removes data from the filter if it is present.
        /// </summary>
        /// <param name="data">The data to remove.</param>
        /// <returns>Whether the data was present, and so was removed.</returns>
        /// <remarks>
        /// Removing something the filter never held is refused rather than performed,
        /// because a fingerprint that matches only as a false positive belongs to some
        /// other item, and deleting it would make that item answer no.
        /// </remarks>
        public bool TestAndRemove(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            return TestAndRemove(data.AsSpan());
        }

        /// <summary>
        /// Removes data from the filter if it is present.
        /// </summary>
        /// <param name="data">The data to remove.</param>
        /// <returns>Whether the data was present, and so was removed.</returns>
        public bool TestAndRemove(ReadOnlySpan<byte> data)
        {
            var hash = this.Hash(data);
            foreach (var segment in this.Segments)
            {
                if (segment.Remove(hash))
                {
                    this.Added--;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Doubles the active table. The chain's tables expand by the same path, so
        /// there is one description of what an expansion means rather than two that
        /// have to be kept in step.
        /// </summary>
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
        /// Moves entries that have run out of fingerprint into the next table down the
        /// chain, expanding that table first if it has filled up.
        /// </summary>
        /// <remarks>
        /// A void entry is not nothing: its address in the table it came from is the
        /// low bits of its hash, and that is a fingerprint by another name. Splitting
        /// that address the same way any other entry is split -- some of it addressing
        /// the smaller table, the rest of it a fingerprint -- makes the next table down
        /// an ordinary filter over the same hashes, which is why a query can ask it the
        /// same question it asks the active one.
        /// <para>
        /// Every table in the chain sheds into the one after it by this same path,
        /// including the deep ones. Giving the deeper tables their own expansion was
        /// the thing this originally lacked: they filled up, and an insert into a full
        /// table walks forever looking for a slot that is not there.
        /// </para>
        /// </remarks>
        private void ShedInto(
            int index, List<uint> addresses, int addressBits, int fingerprintBits)
        {
            if (this.Segments.Count == index)
            {
                // The next table down holds roughly one entry for every 2^F above it,
                // which is how many survive long enough to go void.
                this.Segments.Add(new InfiniSegment(
                    Math.Max(2, addressBits - fingerprintBits), fingerprintBits));
            }

            foreach (var address in addresses)
            {
                if (this.Segments[index].Load >= ExpansionThreshold)
                {
                    ExpandTable(index);
                }

                var target = this.Segments[index];
                var quotient = (uint)(address & (target.Slots - 1));
                var carried = (ulong)address >> target.QuotientBits;

                // What is left of the address after the target's own addressing is the
                // fingerprint. A shorter one would mean the entry arrives already aged
                // by the difference, so that it keeps matching on exactly the bits it
                // has rather than on bits nobody stored.
                //
                // That branch is not currently reachable: a chain table is created
                // with room for a full fingerprint, and it doubles more slowly than
                // the table feeding it, so the spare only ever grows. A sweep over
                // fingerprints of 2 to 12 bits and starting sizes of 1 to 1024, at
                // 300,000 items each, never took it. It is kept because the
                // alternative -- assuming a full fingerprint -- would store bits that
                // were never there and lose the entry, and the sizing it depends on is
                // exactly the sort of thing a later change would adjust.
                var spare = addressBits - target.QuotientBits;
                var age = Math.Clamp(fingerprintBits - spare, 0, fingerprintBits);
                target.Insert(quotient, age, carried);
            }
        }

        /// <summary>
        /// Doubles one table, ageing every entry that lived through it by one and
        /// moving aside those with nothing left to spend.
        /// </summary>
        private void ExpandTable(int index)
        {
            var old = this.Segments[index];

            if (old.QuotientBits + 1 + old.FingerprintBits > 64)
            {
                throw new InvalidOperationException(
                    $"A table in the chain cannot expand past {old.Slots} slots: its " +
                    "address and fingerprint together would need more bits than the " +
                    "hash has.");
            }

            var grown = new InfiniSegment(old.QuotientBits + 1, old.FingerprintBits);
            var voided = new List<uint>();

            foreach (var (quotient, field) in old.Entries())
            {
                var age = old.AgeOf(field);
                if (age >= old.FingerprintBits)
                {
                    voided.Add(quotient);
                    continue;
                }

                var fingerprint = old.FingerprintOf(field, age);
                var moved = quotient + ((fingerprint & 1) == 1 ? old.Slots : 0u);
                grown.Insert(moved, age + 1, fingerprint >> 1);
            }

            this.Segments[index] = grown;

            if (voided.Count > 0)
            {
                ShedInto(index + 1, voided, old.QuotientBits, old.FingerprintBits);
            }
        }

        /// <summary>
        /// How many items have been added, less those removed. Repeats count
        /// separately.
        /// </summary>
        public ulong Count() => this.Added;

        /// <summary>
        /// How many slots the filter currently has across every table in its chain.
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
        /// How many tables the chain holds. One until entries start running out of
        /// fingerprint, then slowly more.
        /// </summary>
        public int ChainLength() => this.Segments.Count;

        /// <summary>
        /// The tables' size in bytes, which is all the filter occupies beyond a
        /// handful of fields.
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
        /// Restores the filter to its original state. Returns the filter to allow for
        /// chaining.
        /// </summary>
        public InfiniFilter Reset()
        {
            var first = this.Segments[0];
            var quotientBits = first.QuotientBits - (int)this.Expansions;
            this.Segments = new List<InfiniSegment>
            {
                new InfiniSegment(Math.Max(2, quotientBits), first.FingerprintBits),
            };
            this.Added = 0;
            this.Expansions = 0;
            return this;
        }

        /// <summary>
        /// Sets the hash function, which is refused once anything has been added.
        /// </summary>
        /// <param name="h">The hash function.</param>
        public void SetHash(Func<ReadOnlySpan<byte>, ulong> h)
        {
            ArgumentNullException.ThrowIfNull(h);
            Guard.HashMayBeReplaced(this.Added == 0, nameof(InfiniFilter));
            this.Hash = h;
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
                stream,
                StructureId.InfiniFilter,
                PersistenceFormat.Identify(this.Hash),
                payload.WrittenSpan);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The filter that was written.</returns>
        public static InfiniFilter ReadFrom(Stream stream)
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
        public static InfiniFilter ReadFrom(Stream stream, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, null, options);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>, using the supplied hash
        /// function rather than the one named in the payload.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function the filter was written with.</param>
        /// <returns>The filter that was written.</returns>
        public static InfiniFilter ReadFrom(
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
        public static InfiniFilter ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, hash, options);
        }

        private static InfiniFilter Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.InfiniFilter);
            var payload = PersistenceFormat.Read(
                stream, StructureId.InfiniFilter, out var hashId, budget);
            var reader = new PayloadReader(payload, budget);

            var added = reader.ReadUInt64();
            var expansions = reader.ReadUInt32();
            var segmentCount = reader.ReadUInt32();

            if (segmentCount == 0)
            {
                throw new InvalidDataException(
                    "Filter carries no tables at all, and every query begins by " +
                    "asking the first one.");
            }
            if (segmentCount > 64)
            {
                throw new InvalidDataException(
                    $"Filter claims a chain of {segmentCount} tables. The chain grows " +
                    "with the logarithm of the item count, so this is longer than a " +
                    "64-bit hash could ever justify.");
            }

            var filter = new InfiniFilter(true)
            {
                Added = added,
                Expansions = expansions,
                Segments = new List<InfiniSegment>((int)segmentCount),
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
                        "of hash to address and fingerprint an entry, and a hash has " +
                        "sixty-four.");
                }

                var slots = 1u << quotientBits;
                if (count > slots)
                {
                    throw new InvalidDataException(
                        $"Table {i} holds {count} entries in {slots} slots, and a " +
                        "quotient filter stores one entry per slot.");
                }

                var expectedWords =
                    ((((long)slots * (fingerprintBits + 1 + 3)) >> 6) + 2);
                if (words != expectedWords)
                {
                    throw new InvalidDataException(
                        $"Table {i} carries {words} words where its shape needs " +
                        $"{expectedWords}.");
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

                filter.Segments.Add(InfiniSegment.Restore(
                    quotientBits, fingerprintBits, count, data));
            }

            reader.ExpectEnd();

            filter.Hash = PersistenceFormat.ResolveOrThrow(hashId, hash);
            return filter;
        }
    }
}
