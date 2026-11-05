using System;
using System.Buffers.Binary;
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
    public class InfiniFilter : IBinaryPersistable<InfiniFilter>, IPayloadBody
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
            ulong initialCapacity = 1024,
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

            var quotientBits = InitialQuotientBits(initialCapacity, fingerprintBits);

            this.Segments.Add(new InfiniSegment(quotientBits, fingerprintBits));
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();
        }

        /// <summary>
        /// The most address bits any table has: the most a table of the narrowest fingerprint
        /// the constructor takes can have, <see cref="QuotientBitsLimitFor(int)"/> two bits.
        /// </summary>
        internal static int MaxQuotientBits { get; } = QuotientBitsLimitFor(2, Array.MaxLength);

        /// <summary>
        /// The most address bits this filter lets a table grow to, beside
        /// <see cref="QuotientBitsLimitFor(int)"/> that table's fingerprint. It is
        /// <see cref="MaxQuotientBits"/>, which no table reaches before its own limit; a test
        /// lowers it to reach the refusal at the limit without building a table of gigabytes.
        /// </summary>
        internal int QuotientBitsLimit { get; set; } = MaxQuotientBits;

        /// <summary>
        /// The most address bits a table of fingerprints this wide can have: the widest whose
        /// words fit in the one array a table is (#231). That is 2^34 slots for the narrowest
        /// fingerprints and 2^31 for the widest, where every table stopped at 2^31 while its
        /// slot count was a <c>u32</c>. The constructor starts the first table no wider, and an
        /// expansion stops there.
        /// </summary>
        /// <remarks>
        /// The hash never binds first: an entry needs the address and the fingerprint from one
        /// 64-bit hash, and the widest table has bits to spare for any fingerprint it allows.
        /// It bounds the scan all the same, so that the scan ends whatever the words come to:
        /// with the slot count worked out in 32 bits, which wraps, the words never pass an
        /// array, and the scan spun in the type's static initializer, which every filter waits
        /// on (#231).
        /// </remarks>
        internal static int QuotientBitsLimitFor(int fingerprintBits) =>
            QuotientBitsLimitFor(fingerprintBits, InfiniSegment.LongestTable);

        /// <summary>
        /// As <see cref="QuotientBitsLimitFor(int)"/>, for an array of at most
        /// <paramref name="longest"/> words: the real one's, for
        /// <see cref="MaxQuotientBits"/>, whatever a test has lowered on the thread that
        /// first asks for it.
        /// </summary>
        private static int QuotientBitsLimitFor(int fingerprintBits, long longest)
        {
            var quotientBits = 2;
            while (quotientBits + 1 + fingerprintBits <= 64
                && InfiniSegment.Words(quotientBits + 1, fingerprintBits) <= longest)
            {
                quotientBits++;
            }
            return quotientBits;
        }

        /// <summary>
        /// The address bits the first table gets for an initial capacity: enough slots
        /// that the capacity fills no more than the expansion threshold of them, a
        /// power of two, at least four, and no more than a table of the fingerprint can have.
        /// </summary>
        internal static int InitialQuotientBits(ulong initialCapacity, int fingerprintBits)
        {
            // Clamped at the widest table rather than left to an integer that saturates: the
            // capacity is where the filter starts and not a limit on it, so one that asks
            // for more than the widest table starts at the widest.
            var wanted = Math.Ceiling(Math.Log2(initialCapacity / ExpansionThreshold));
            return (int)Math.Clamp(wanted, 2, QuotientBitsLimitFor(fingerprintBits));
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
            // what it was. Each table is held to the limit of its own fingerprint, since a
            // payload's tables are each checked on their own and a filter read back can hold
            // tables of other fingerprints. The first table is the one that reaches its limit,
            // the tables behind it being narrower by more than the fingerprint they were made
            // from, but the check does not rest on that: a doubling can double any of them.
            foreach (var table in this.Segments)
            {
                var own = QuotientBitsLimitFor(table.FingerprintBits);
                var limit = Math.Min(this.QuotientBitsLimit, own);
                if (table.QuotientBits + 1 > limit)
                {
                    var why = limit < own
                        ? "the limit this filter was given"
                        : $"a table is one array of words, and {1UL << (limit + 1):N0} slots of " +
                          $"{table.FingerprintBits + 4} bits are more words than an array holds";
                    throw new InvalidOperationException(
                        $"The filter's table of {1UL << table.QuotientBits:N0} slots is as wide " +
                        $"as a table of {table.FingerprintBits}-bit fingerprints can be, and " +
                        "doubling the filter would take it past the " +
                        $"{1UL << limit:N0} slots it can have: {why}. Nothing was added and the " +
                        "filter is unchanged; it can still be queried and written, and removing " +
                        "keys makes room for more. To hold more keys, split them over several " +
                        "filters, or use a narrower fingerprint, whose table can be wider.");
                }
            }

            ExpandTable(0);
            this.Expansions++;
        }

        /// <summary>
        /// Moves an entry that has run out of fingerprint into the next table down the
        /// chain, making that table if there is none and expanding it first if it has
        /// filled up.
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
            int index, ulong address, int addressBits, int fingerprintBits)
        {
            if (this.Segments.Count == index)
            {
                // The next table down holds roughly one entry for every 2^F above it,
                // which is how many survive long enough to go void.
                this.Segments.Add(new InfiniSegment(
                    Math.Max(2, addressBits - fingerprintBits), fingerprintBits));
            }

            if (this.Segments[index].Load >= ExpansionThreshold)
            {
                ExpandTable(index);
            }

            var target = this.Segments[index];
            var (quotient, carried) = InfiniSegment.Shed(address, target.QuotientBits);

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

            // The old table's entries are read in place as the walk reaches them, which
            // holds because nothing here writes to it: the loop writes to the grown table
            // and to the tables after this one in the chain, and the old one is dropped
            // once the walk is over.
            //
            // A void entry is moved down the chain as the walk reaches it. They were listed
            // by the walk and moved after it, and nothing bounds how many there are. They
            // are moved in the same order, and moving one reads and writes only the tables
            // after this one, never the old table or the grown one, so the chain ends as it
            // did.
            foreach (var (quotient, field) in old.Entries())
            {
                var age = old.AgeOf(field);
                if (age >= old.FingerprintBits)
                {
                    ShedInto(index + 1, quotient, old.QuotientBits, old.FingerprintBits);
                    continue;
                }

                var fingerprint = old.FingerprintOf(field, age);
                grown.Insert(InfiniSegment.Doubled(quotient, fingerprint, old.QuotientBits), age + 1, fingerprint >> 1);
            }

            this.Segments[index] = grown;
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
            // The filter goes back to the one table it was built with: the first table's
            // fingerprint, and its address less a bit for each doubling.
            var first = this.Segments[0];
            var quotientBits = Math.Max(2, first.QuotientBits - (int)this.Expansions);
            var fingerprintBits = first.FingerprintBits;

            // A table already that shape is cleared where it is and the others dropped, so the
            // reset needs no memory and cannot fail for want of it. A filter that has not grown
            // always holds one, its only table, and one that has grown can, in its chain (#298).
            // Otherwise the table is built before anything is cleared or dropped, so that a reset
            // that cannot get the memory leaves the filter as it was (#242), at the cost of
            // holding it beside the tables it replaces.
            var kept = TableShaped(quotientBits, fingerprintBits);
            if (kept == null)
            {
                this.BeforeAllocating?.Invoke();
                kept = new InfiniSegment(quotientBits, fingerprintBits);
            }
            else
            {
                kept.Clear();
            }

            this.Segments[0] = kept;
            this.Segments.RemoveRange(1, this.Segments.Count - 1);
            this.Added = 0;
            this.Expansions = 0;
            return this;
        }

        /// <summary>
        /// The first table in the chain of this shape, or null if none is.
        /// </summary>
        private InfiniSegment? TableShaped(int quotientBits, int fingerprintBits)
        {
            foreach (var table in this.Segments)
            {
                // Both widths are compared because a payload's tables are each checked on
                // their own, so a filter read back can hold tables of other fingerprints.
                if (table.QuotientBits == quotientBits && table.FingerprintBits == fingerprintBits)
                {
                    return table;
                }
            }

            return null;
        }

        /// <summary>
        /// Called before the table a reset builds when it holds none of the shape it returns
        /// to, which is all a reset allocates. It is null; a test sets it to throw
        /// an <see cref="OutOfMemoryException"/> there, to reach a reset that cannot get its
        /// memory without a heap limit. Nothing else the filter allocates passes through it.
        /// </summary>
        internal Action? BeforeAllocating { get; set; }

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
        public void WriteTo(Stream stream) => PersistenceFormat.Write(stream, this);

        EnvelopeHeader IPayloadBody.WritePayload(PayloadWriter payload)
        {
            payload.WriteUInt64(this.Added);
            payload.WriteUInt32(this.Expansions);
            payload.WriteUInt32((uint)this.Segments.Count);

            // Every save is version 13, whose counts of entries and words are 64-bit, so a
            // table of any width is written in one layout (#231).
            foreach (var segment in this.Segments)
            {
                payload.WriteUInt32((uint)segment.QuotientBits);
                payload.WriteUInt32((uint)segment.FingerprintBits);
                payload.WriteUInt64(segment.Count);

                // The table's words, counted from the segment's two widths.
                var words = segment.TableWords;
                payload.WriteUInt64((ulong)words);
                if (payload.BeginBlock(words * sizeof(ulong)))
                {
                    foreach (var word in segment.Data)
                    {
                        payload.WriteUInt64(word);
                    }
                }
            }

            return new(StructureId.InfiniFilter, PersistenceFormat.Identify(this.Hash),
                PersistenceFormat.InfiniWideVersion);
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
            return PersistenceFormat.Read(
                stream, StructureId.InfiniFilter, budget, Price, hash, PersistenceFormat.CheckResolvable,
                ReadPayload);
        }

        /// <summary>
        /// Reads the payload of the envelope <c>Read</c> opened, once the envelope has
        /// been checked. See <see cref="PayloadParse{T}"/>.
        /// </summary>
        private static InfiniFilter ReadPayload(
            ref PayloadReader reader, ushort version, HashId hashId,
            Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            var budget = reader.Budget;

            var added = reader.ReadUInt64();
            var expansions = reader.ReadUInt32();
            var segmentCount = reader.ReadUInt32();
            CheckChain(segmentCount);

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
                var (count, words) = ReadCounts(ref reader, version);
                CheckSegment(i, version, quotientBits, fingerprintBits, count, words);

                // The payload has to hold the words before they are charged or allocated, as
                // the price checks before it prices them, so that a payload without them is
                // refused for that under any budget, and a table of any width is never
                // allocated for a few bytes.
                reader.RequireHolds(words, sizeof(ulong), "words");

                // The segment's table is allocated, and the words, which have no length before
                // them, are read straight into it. Read into an array of their own and copied
                // into the table, every table was held, and charged, twice (#313). The shape's
                // check holds the words to what one array holds.
                budget.Charge(SegmentCost(words));
                var segment = InfiniSegment.Restore(quotientBits, fingerprintBits, count);
                ReadWords(ref reader, segment.Data);

                // Every walk of a table rests on its metadata being consistent, and the
                // expansion on its count being its entries.
                segment.CheckMetadata(i);
                filter.Segments.Add(segment);
            }

            CheckExpansions(expansions, filter.Segments[0].QuotientBits);
            reader.ExpectEnd();

            filter.Hash = PersistenceFormat.ResolveOrThrow(hashId, hash);
            return filter;
        }

        /// <summary>
        /// Prices what <see cref="Read"/> allocates beyond the envelope: each table, which its
        /// words are read straight into. See <see cref="PayloadPrice"/>.
        /// </summary>
        internal static void Price(ref PayloadReader reader, ushort version)
        {
            reader.ReadUInt64();
            reader.ReadUInt32();
            var segmentCount = reader.ReadUInt32();
            CheckChain(segmentCount);

            for (var i = 0; i < segmentCount; i++)
            {
                var quotientBits = (int)reader.ReadUInt32();
                var fingerprintBits = (int)reader.ReadUInt32();
                var (count, words) = ReadCounts(ref reader, version);
                CheckSegment(i, version, quotientBits, fingerprintBits, count, words);
                reader.RequireHolds(words, sizeof(ulong), "words");

                reader.Budget.Price(SegmentCost(words));
                reader.Skip((long)words * sizeof(ulong));
            }
        }

        /// <summary>
        /// A table's count of entries and of words: <c>u32</c>s at version 1, <c>u64</c>s from
        /// version 13, where a table can have more slots than a <c>u32</c> counts.
        /// </summary>
        private static (ulong Count, ulong Words) ReadCounts(ref PayloadReader reader, ushort version) =>
            version >= PersistenceFormat.InfiniWideVersion
                ? (reader.ReadUInt64(), reader.ReadUInt64())
                : (reader.ReadUInt32(), reader.ReadUInt32());

        /// <summary>One table, its words read straight into it, and the segment that holds it.</summary>
        private static long SegmentCost(ulong words) => ReadBudget.Sum(
            ReadBudget.ArrayOf(words, sizeof(ulong)),
            ReadBudget.SmallObject);

        /// <summary>
        /// Reads as many words as <paramref name="into"/> holds, which have no length before
        /// them, straight into it, a piece at a time through a buffer on the stack (#289):
        /// copied from a held payload, and read from the stream from one that is not. Nothing
        /// is allocated for them, so a table is not held twice (#313).
        /// </summary>
        private static void ReadWords(ref PayloadReader reader, Span<ulong> into)
        {
            var left = (long)into.Length * sizeof(ulong);
            Span<byte> piece = stackalloc byte[PayloadReader.BlockPieceFor(left)];
            var at = 0;
            while (left > 0)
            {
                var bytes = piece[..(int)Math.Min(left, piece.Length)];
                reader.ReadBlock(bytes);
                left -= bytes.Length;
                for (var i = 0; i < bytes.Length; i += sizeof(ulong))
                {
                    into[at++] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[i..]);
                }
            }
        }

        private static void CheckChain(uint segmentCount)
        {
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
        }

        /// <summary>
        /// Refuses an expansion count the first table could not have come from. The first table
        /// starts with at least two bits of address and gains one with each expansion, which is
        /// all the count counts, so <see cref="Reset"/>, which takes the table the filter was
        /// built with to be the first less a bit for each expansion, then builds one the
        /// constructor builds; a count above it once reset a filter to a table it never had.
        /// </summary>
        /// <remarks>
        /// The count is held to the first table alone. The tables behind it double on their
        /// own, uncounted, so their address bits say nothing about it, and the chain's length
        /// rests on how long an entry's fingerprint lasts, which is the filter's choice, while
        /// the reader takes the tables behind the first as they come, each checked on its own.
        /// </remarks>
        private static void CheckExpansions(uint expansions, int quotientBits)
        {
            if (expansions > quotientBits - 2)
            {
                throw new InvalidDataException(
                    $"Filter claims {expansions:N0} expansions, and its first table has " +
                    $"{quotientBits} bits of address. A first table starts with at least 2 and " +
                    $"gains one with each expansion, so this one has been through at most " +
                    $"{quotientBits - 2}.");
            }
        }

        /// <summary>
        /// Checks the shape a table claims, as both the read and its price do before its
        /// words are allocated.
        /// </summary>
        private static void CheckSegment(
            int i, ushort version, int quotientBits, int fingerprintBits, ulong count, ulong words)
        {
            // A table at version 1 counted its slots with a u32, so it had at most 31 bits of
            // address, and no writer wrote a wider one. From version 13 a table is as wide as
            // one array holds its words, and the widest of any fingerprint bounds the address
            // before the words are worked out from it.
            if (version < PersistenceFormat.InfiniWideVersion)
            {
                if (quotientBits < 2 || quotientBits > NarrowQuotientBits)
                {
                    throw new InvalidDataException(
                        $"Table {i} claims {quotientBits} bits of address, and a table at version " +
                        $"{version} has between 2 and {NarrowQuotientBits}: its slot count was a " +
                        "32-bit number holding 2^q, and 2^32 is not one. A wider table is written " +
                        $"at version {PersistenceFormat.InfiniWideVersion}.");
                }
            }
            else if (quotientBits < 2 || quotientBits > MaxQuotientBits)
            {
                throw new InvalidDataException(
                    $"Table {i} claims {quotientBits} bits of address, and this library builds " +
                    $"tables between 2 and {MaxQuotientBits}: a table is one array of words, and " +
                    $"{1UL << (MaxQuotientBits + 1):N0} slots of the narrowest fingerprint are more " +
                    "words than an array holds.");
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

            var slots = 1UL << quotientBits;
            if (count > slots)
            {
                throw new InvalidDataException(
                    $"Table {i} holds {count} entries in {slots} slots, and a " +
                    "quotient filter stores one entry per slot.");
            }

            var expectedWords = InfiniSegment.Words(quotientBits, fingerprintBits);
            if (words != (ulong)expectedWords)
            {
                throw new InvalidDataException(
                    $"Table {i} carries {words} words where its shape needs " +
                    $"{expectedWords}.");
            }

            if (expectedWords > InfiniSegment.LongestTable)
            {
                throw new InvalidDataException(
                    $"Table {i} claims {slots:N0} slots of {fingerprintBits + 4} bits, which are " +
                    $"{expectedWords:N0} words, and a table is one array, which holds at most " +
                    $"{InfiniSegment.LongestTable:N0}. No table of {fingerprintBits}-bit fingerprints " +
                    $"is wider than 2^{QuotientBitsLimitFor(fingerprintBits)} slots.");
            }
        }

        /// <summary>
        /// The most address bits a table had at version 1, where its slot count was a
        /// <c>u32</c> holding 2^q.
        /// </summary>
        internal const int NarrowQuotientBits = 31;
    }
}
