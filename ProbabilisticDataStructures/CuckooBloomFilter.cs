using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// CuckooFilter implements a Cuckoo Bloom filter as described by Andersen, Kaminsky,
    /// and Mitzenmacher in Cuckoo Filter: Practically Better Than Bloom:
    ///
    /// http://www.pdl.cmu.edu/PDL-FTP/FS/cuckoo-conext2014.pdf
    ///
    /// A Cuckoo Filter is a Bloom filter variation which provides support for removing
    /// elements without significantly degrading space and performance. It works by using
    /// a cuckoo hashing scheme for inserting items. Instead of storing the elements
    /// themselves, it stores their fingerprints which also allows for item removal
    /// without false negatives (if you don't attempt to remove an item not contained in
    /// the filter).
    ///
    /// For applications that store many items and target moderately low false-positive
    /// rates, cuckoo filters have lower space overhead than space-optimized Bloom filters.
    /// </summary>
    public class CuckooBloomFilter : IBinaryPersistable<CuckooBloomFilter>, IPayloadBody
    {
        /// <summary>
        /// Size of the stack buffer used when hashing. 64 bytes holds the largest
        /// digest any standard <see cref="HashAlgorithm"/> produces (SHA-512).
        /// </summary>
        private const int MaxStackHashSize = 64;

        /// <summary>
        /// The maximum number of relocations to attempt when inserting an element before
        /// considering the filter full.
        /// </summary>
        private const int MAX_NUM_KICKS = 500;

        /// <summary>The eviction budget, exposed so tests can pin it to the paper.</summary>
        internal static int MaxKicks => MAX_NUM_KICKS;

        /// <summary>
        /// Every eviction kick ever performed, so the work-bound tests can assert
        /// that a refused insert spent exactly its budget -- failure by count,
        /// not by clock.
        /// </summary>
        internal long KickSteps { get; private set; }

        /// <summary>
        /// Every fingerprint, packed end to end. Entry j of bucket i is entry number
        /// i * B + j (<see cref="EntryNumber"/>), and its bits begin at that number times F.
        /// </summary>
        /// <remarks>
        /// One array rather than a jagged one. Holding each fingerprint in its own
        /// byte[] cost a 24-byte object header on two bytes of payload, plus an array
        /// of references per bucket: a filter sized for 100,000 items took 3.8 MB
        /// holding nothing and 11.5 MB holding them, against 256 KB of actual
        /// fingerprints.
        /// <para>
        /// An entry nothing holds is all zero: a removal clears the fingerprint it
        /// leaves, and a reset clears the table. A payload is the whole table (#230), so
        /// what an empty entry held would otherwise be written, and a filter emptied by
        /// removals would not write the bytes of one never added to.
        /// </para>
        /// </remarks>
        internal ulong[] Fingerprints { get; set; }

        /// <summary>
        /// One bit per entry, saying whether it holds a fingerprint: entry e is bit e % 64
        /// of word e / 64. The bits past the last entry are never set.
        /// </summary>
        /// <remarks>
        /// A separate bit rather than treating an all-zero fingerprint as empty. That
        /// would be smaller, but a fingerprint is taken from a hash and can legitimately
        /// be all zero, so it would need forcing to something else -- which changes
        /// which bucket the element's alternate index resolves to, and so cannot be
        /// applied to fingerprints already written by an earlier version. Six percent of
        /// the fingerprint bytes buys reading every payload ever written unchanged.
        /// <para>
        /// Words of its own rather than a <see cref="Buckets"/> of one-bit buckets, whose
        /// count is a <c>uint</c> (#231): one bit an entry is never more words than the
        /// fingerprints, which are at least a bit an entry, so the one array the
        /// fingerprints fit in bounds this too. It is written as it is held, word by word.
        /// </para>
        /// </remarks>
        internal ulong[] Occupied { get; set; }
        /// <summary>
        /// Hash algorithm.
        /// </summary>
        private Func<ReadOnlySpan<byte>, ulong> Hash { get; set; } = null!;
        /// <summary>
        /// Number of buckets, a power of two.
        /// </summary>
        internal ulong M { get; set; }
        /// <summary>
        /// Number of entries per bucket
        /// </summary>
        internal uint B { get; set; }
        /// <summary>
        /// Width of fingerprints, in bits. A lookup compares against 2b stored
        /// fingerprints, so an accidental match happens at 2b/2^bits -- which is what
        /// makes this the filter's false-positive rate.
        /// </summary>
        internal uint FingerprintBits { get; set; }

        /// <summary>
        /// The bytes a fingerprint occupies when it is hashed, which is the width
        /// rounded up. Entries are stored packed at the bit width; this is only the
        /// shape handed to the hash function when computing an entry's partner bucket.
        /// </summary>
        private int HashWidth => (int)((this.FingerprintBits + 7) / 8);

        /// <summary>All ones at the fingerprint's width.</summary>
        private ulong FingerprintMask =>
            this.FingerprintBits >= 64 ? ulong.MaxValue : (1UL << (int)this.FingerprintBits) - 1;

        /// <summary>The bytes the packed fingerprint array occupies.</summary>
        internal ulong FingerprintBytes() => (ulong)this.Fingerprints.LongLength * sizeof(ulong);
        /// <summary>
        /// Number of items in the filter
        /// </summary>
        private ulong count { get; set; }
        /// <summary>
        /// Filter capacity
        /// </summary>
        private ulong N { get; set; }

        private SeededRandom random;

        /// <summary>
        /// Where the generator stood when the filter was built, which is what
        /// <see cref="Reset"/> returns it to. It is stored with the filter because the
        /// position the generator has reached cannot say where it began: a filter read
        /// back from a payload would otherwise have nothing to return to.
        /// </summary>
        private ulong originalRandomState;

        /// <summary>
        /// Creates a new Cuckoo Bloom filter optimized to store n items with a specified
        /// target false-positive rate.
        /// </summary>
        /// <param name="n">Number of items to store</param>
        /// <param name="fpRate">Target false-positive rate</param>
        /// <param name="hash">
        /// The hash function to use, or null for the default. Passing it here is the
        /// only way to have one hash cover everything the structure will ever hold:
        /// once anything has been added, the hash can no longer be replaced.
        /// </param>
        /// <param name="seed">
        /// A seed for the random choices this filter makes, or null to seed it
        /// unpredictably. Supplying one makes the filter reproducible, which is what
        /// makes its behavior assertable rather than only describable.
        /// </param>
        public CuckooBloomFilter(ulong n, double fpRate,
            Func<ReadOnlySpan<byte>, ulong>? hash = null, ulong? seed = null)
        {
            Guard.ValidItemCount(n, nameof(n));
            Guard.ValidFalsePositiveRate(fpRate, nameof(fpRate));

            var (m, bits) = Size(n, fpRate);
            if (GeometryProblem(m, EntriesPerBucket, bits) is { } problem)
            {
                throw new ArgumentOutOfRangeException(nameof(n), n,
                    $"A filter for {n} items at this rate needs {m} buckets of {EntriesPerBucket} " +
                    $"entries at {bits} bits, and {problem}");
            }

            var entries = m * EntriesPerBucket;
            this.Fingerprints = new ulong[WordsFor(entries, bits)];
            this.Occupied = new ulong[MapWords(entries)];
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();
            this.random = seed is null
                ? SeededRandom.Unpredictable()
                : new SeededRandom(seed.Value);
            this.originalRandomState = this.random.State;
            this.M = m;
            this.B = EntriesPerBucket;
            this.FingerprintBits = bits;
            this.N = n;
        }

        /// <summary>The entries in each bucket the constructor makes.</summary>
        private const uint EntriesPerBucket = 4;

        /// <summary>
        /// The buckets and fingerprint width a filter for <paramref name="n"/> items at
        /// <paramref name="fpRate"/> gets, which is the constructor's sizing, worked out
        /// without building anything.
        /// </summary>
        /// <remarks>
        /// Buckets needed to hold n items at b entries each, with room to spare so that
        /// inserts still find a home near capacity: a cuckoo filter's load factor is
        /// around 0.95 for four-entry buckets, and inserts start failing as it is
        /// approached. Rounded up to a power of two, which the index arithmetic requires.
        /// In 64 bits (#231): the largest <paramref name="n"/> a <c>ulong</c> holds is
        /// under 2^63 buckets, so the rounding cannot wrap, and what the table that comes
        /// out can hold is <see cref="GeometryProblem"/>'s to say.
        /// <para>
        /// This used to be Power2(n / f * 8), which is not a bucket count at all. The 8
        /// undoes a division by 8 in CalculateF that had already floored to nothing, and
        /// what came out was roughly eight buckets per item rather than one per four
        /// items -- thirty-two times more than needed. A filter asked for 100,000 items
        /// allocated 122 MB while holding nothing, against 124 KB for a Bloom filter of
        /// the same capacity and rate.
        /// </para>
        /// </remarks>
        internal static (ulong Buckets, uint Bits) Size(ulong n, double fpRate) =>
            (Power2((ulong)Math.Ceiling(n / (EntriesPerBucket * LoadFactor))),
             CalculateFingerprintBits(EntriesPerBucket, fpRate));

        /// <summary>
        /// Returns the number of buckets.
        /// </summary>
        /// <returns>The number of buckets</returns>
        public ulong BucketCount()
        {
            return this.M;
        }

        /// <summary>
        /// Returns the number of items the filter was sized for.
        /// </summary>
        /// <remarks>
        /// This is the count passed to the constructor, and the load the false positive
        /// rate was chosen to hold at. It is not a hard limit: buckets are allocated in
        /// powers of two and with room above the load factor, so a filter will usually
        /// accept somewhat more than this before it starts refusing inserts. It used to
        /// accept thirty to fifty times more, which is what issue 47 was about.
        /// </remarks>
        /// <returns>The number of items the filter was sized for</returns>
        public ulong Capacity()
        {
            return this.N;
        }

        /// <summary>
        /// The filter's storage in bytes: one packed fingerprint per entry, plus one
        /// occupancy bit per entry.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="Capacity"/>, which reports how many items the filter
        /// is sized for rather than how much room it takes. Fixed once constructed --
        /// since 5.2.0 the fingerprints live in one array allocated up front, so this
        /// does not change as the filter fills.
        /// </remarks>
        /// <returns>The number of bytes the filter occupies.</returns>
        public ulong SizeInBytes()
        {
            return this.FingerprintBytes() + (ulong)this.Occupied.LongLength * sizeof(ulong);
        }

        /// <summary>
        /// Returns the number of items in the filter.
        /// </summary>
        /// <returns>The number of items in the filter</returns>
        public ulong Count()
        {
            return this.count;
        }

        /// <summary>
        /// Will test for membership of the data and returns true if it is a member,
        /// false if not. This is a probabilistic test, meaning there is a non-zero
        /// probability of false positives.
        /// </summary>
        /// <param name="data">The data to test for</param>
        /// <returns>Whether or not the data is a member</returns>
        public bool Test(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            return this.Test(data.AsSpan());
        }

        /// <inheritdoc cref="Test(byte[])"/>
        public bool Test(ReadOnlySpan<byte> data)
        {
            var components = this.GetComponents(data);
            var i1 = components.Hash1;
            var i2 = components.Hash2;
            var f = components.Fingerprint;

            // If either bucket contains f, it's a member.
            return Contains(BucketOf(i1, this.M), f) || Contains(BucketOf(i2, this.M), f);
        }

        /// <summary>
        /// Will add the data to the Cuckoo Filter. It returns false if the filter is
        /// full. If the filter is full, an item is removed to make room for the new
        /// item. This introduces a possibility for false negatives. To avoid this, use
        /// Count and Capacity to check if the filter is full before adding an item.
        /// </summary>
        /// <param name="data"></param>
        /// <returns>
        /// True if the add was successful. False if the filter is full.
        /// </returns>
        public bool Add(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            return this.Add(data.AsSpan());
        }

        /// <inheritdoc cref="Add(byte[])"/>
        public bool Add(ReadOnlySpan<byte> data)
        {
            var components = this.GetComponents(data);
            var i1 = components.Hash1;
            var i2 = components.Hash2;
            var f = components.Fingerprint;
            return this.Insert(i1, i2, f);
        }

        /// <summary>
        /// Equivalent to calling Test followed by Add. It returns (true, false) if the
        /// data is a member, (false, add()) if not. False is returned if the filter is
        /// full. If the filter is full, an item is removed to make room for the new
        /// item. This introduces a possibility for false negatives. To avoid this, use
        /// Count and Capacity to check if the filter is full before adding an item.
        /// </summary>
        /// <returns>
        /// (true, false) if the data is a member, (false, add()) if not
        /// </returns>
        public TestAndAddReturnValue TestAndAdd(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            return this.TestAndAdd(data.AsSpan());
        }

        /// <inheritdoc cref="TestAndAdd(byte[])"/>
        public TestAndAddReturnValue TestAndAdd(ReadOnlySpan<byte> data)
        {
            var components = this.GetComponents(data);
            var i1 = components.Hash1;
            var i2 = components.Hash2;
            var f = components.Fingerprint;

            // If either bucket contains f, it's a member.
            if (Contains(BucketOf(i1, this.M), f) || Contains(BucketOf(i2, this.M), f))
            {
                return TestAndAddReturnValue.Create(true, false);
            }

            return TestAndAddReturnValue.Create(false, this.Insert(i1, i2, f));
        }

        /// <summary>
        /// Will test for membership of the data and remove it from the filter if it
        /// exists. Returns true if the data was a member, false if not.
        /// </summary>
        /// <param name="data">Data to test for and remove</param>
        /// <returns>Whether the data was a member or not</returns>
        public bool TestAndRemove(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            return this.TestAndRemove(data.AsSpan());
        }

        /// <inheritdoc cref="TestAndRemove(byte[])"/>
        public bool TestAndRemove(ReadOnlySpan<byte> data)
        {
            var components = this.GetComponents(data);
            var i1 = components.Hash1;
            var i2 = components.Hash2;
            var f = components.Fingerprint;

            // Try bucket[i1], then bucket[i2]. Clearing the occupancy bit is what
            // empties an entry, and the fingerprint is cleared with it: an empty entry
            // holds nothing, so that a payload, which is the whole table, does not carry
            // what a removed item left behind.
            foreach (var bucket in stackalloc[] { BucketOf(i1, this.M), BucketOf(i2, this.M) })
            {
                var idx = IndexOf(bucket, f);
                if (idx != -1)
                {
                    var entry = EntryNumber(bucket, (uint)idx, this.B);
                    SetOccupied(this.Occupied, entry, false);
                    WriteEntryAt(entry, 0);

                    // The count is the entries the filter holds, so an entry being here
                    // means it is at least one. Never taken below zero all the same: a
                    // ulong wraps to 18,446,744,073,709,551,615, which Count would then
                    // report.
                    if (this.count > 0)
                    {
                        this.count--;
                    }

                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Restores the Cuckoo Bloom filter to its original state: every entry empty and
        /// the random generator back where it stood when the filter was built. It
        /// returns the filter to allow for chaining.
        /// </summary>
        /// <remarks>
        /// A reset filter is a new one, byte for byte as <see cref="WriteTo"/> writes
        /// it, and it makes the choices a new one would: a stream added to it takes it
        /// exactly where the same stream took the filter the first time, evictions
        /// included. For a seeded filter those are the choices its seed gives. For one
        /// built without a seed they are the unpredictable ones it was built with, so
        /// resetting it repeatedly repeats them rather than drawing new ones; build a
        /// new filter for that.
        /// <para>
        /// The starting state is stored with the filter, so this holds for one that has
        /// been written and read back. A payload written before it was recorded has no
        /// starting state to return to. A filter read from one takes the generator
        /// position it was read at as its starting state, so Reset returns it there,
        /// emptied, and writing it out and reading it back does not move that point.
        /// </para>
        /// <para>
        /// Reset empties the filter and does not reconfigure it, so a hash function
        /// supplied through <see cref="SetHash"/> stays.
        /// </para>
        /// </remarks>
        /// <returns>The CuckooBloomFilter</returns>
        public CuckooBloomFilter Reset()
        {
            // The fingerprints as well as the map, in place: an entry nothing claims is
            // never read, but a payload is the whole table, and a reset filter writes
            // the bytes a new one does.
            Array.Clear(this.Occupied);
            Array.Clear(this.Fingerprints);
            this.count = 0;
            this.random = new SeededRandom(this.originalRandomState);
            return this;
        }

        /// <summary>
        /// Writes this filter to a stream, in the format documented in FORMAT.md.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream) => PersistenceFormat.Write(stream, this);

        EnvelopeHeader IPayloadBody.WritePayload(PayloadWriter payload)
        {
            payload.WriteUInt64(this.M);
            payload.WriteUInt32(this.B);
            payload.WriteUInt32(this.FingerprintBits);
            payload.WriteUInt64(this.count);
            payload.WriteUInt64(this.N);

            // The whole table, as it is held (#230): the packed fingerprints, then the map
            // of which entries hold one, each word a u64, with no length before them, since
            // the fields just written give it. Its size is the table's whatever the filter
            // holds, about (f + 1) / 8 bytes an entry, so a counter counts it from the
            // fields and does not walk it. The spare word the fingerprints are allocated
            // with is not part of the table and is not written.
            var entries = this.M * this.B;
            var packed = PackedWords(entries, this.FingerprintBits);
            if (payload.BeginBlock((long)(packed + MapWords(entries)) * sizeof(ulong)))
            {
                for (var i = 0; i < (int)packed; i++)
                {
                    payload.WriteUInt64(this.Fingerprints[i]);
                }

                foreach (var word in this.Occupied)
                {
                    payload.WriteUInt64(word);
                }
            }

            payload.WriteUInt64(this.random.State);
            payload.WriteUInt64(this.originalRandomState);

            return new(
                StructureId.CuckooBloomFilter, PersistenceFormat.Identify(this.Hash),
                PersistenceFormat.CuckooDenseVersion);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The filter that was written.</returns>
        public static CuckooBloomFilter ReadFrom(Stream stream)
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
        public static CuckooBloomFilter ReadFrom(Stream stream, ReadOptions options)
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
        public static CuckooBloomFilter ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
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
        public static CuckooBloomFilter ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, hash, options);
        }

        private static CuckooBloomFilter Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.CuckooBloomFilter);
            return PersistenceFormat.Read(
                stream, StructureId.CuckooBloomFilter, budget, Price, hash, PersistenceFormat.CheckResolvable,
                ReadPayload);
        }

        /// <summary>
        /// Reads the payload of the envelope <c>Read</c> opened, once the envelope has
        /// been checked. See <see cref="PayloadParse{T}"/>.
        /// </summary>
        private static CuckooBloomFilter ReadPayload(
            ref PayloadReader reader, ushort version, HashId hashId,
            Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            var budget = reader.Budget;

            var (m, b, f, count, n) = ReadFields(ref reader, version);
            var bits = CheckShape(m, b, f, version);
            var entries = m * b;
            var whole = version >= PersistenceFormat.CuckooDenseVersion;
            if (whole)
            {
                RequireTable(ref reader, entries, bits);
            }

            // The whole table, and the map of which entries hold something: what a
            // filter of this shape is, however few entries the payload carries.
            budget.Charge(TableCost(entries, bits));
            var fingerprints = new ulong[WordsFor(entries, bits)];
            var occupancy = new ulong[MapWords(entries)];

            ulong filled;
            if (whole)
            {
                // Read straight into the table, a piece at a time, as it was written.
                ReadWords(ref reader, fingerprints.AsSpan(0, (int)PackedWords(entries, bits)));
                ReadWords(ref reader, occupancy);
                CheckTable(fingerprints, occupancy, entries, bits);
                filled = CountOccupied(occupancy);
            }
            else
            {
                filled = ReadEntries(ref reader, version, fingerprints, occupancy, m, b, f, bits);
            }

            // The writer's count is the entries it holds, since each item is one. A count
            // below that lets a removal take it under zero, and a count of zero lets the
            // hash be replaced under entries it was used to place.
            if (count != filled)
            {
                throw new InvalidDataException(
                    $"Filter's item count is {count}, but its table holds {filled} " +
                    "entries, one for each item.");
            }

            // Version 1 predates the stored generator state. Such a filter resumes with
            // an unpredictable one: its fingerprints are exactly right, and only the
            // sequence of entries it will evict next is unknowable.
            var random = version >= PersistenceFormat.RandomStateVersion
                ? new SeededRandom(reader.ReadUInt64())
                : SeededRandom.Unpredictable();

            // Versions before the original state was recorded have none to read. Such a
            // filter takes the position it was read at as its beginning, so Reset
            // returns it there and writing it out again records that same point: the
            // earliest the payload supports, rather than a seed it cannot name.
            var originalRandomState = version >= PersistenceFormat.CuckooOriginalStateVersion
                ? reader.ReadUInt64()
                : random.State;

            reader.ExpectEnd();

            return new CuckooBloomFilter
            {
                Fingerprints = fingerprints,
                Occupied = occupancy,
                M = m,
                B = b,
                FingerprintBits = bits,
                count = count,
                N = n,
                Hash = PersistenceFormat.ResolveOrThrow(hashId, hash),
                random = random,
                originalRandomState = originalRandomState,
            };
        }

        /// <summary>
        /// The fields that size a filter and say what it holds, in the order every version
        /// writes them: the buckets, the entries in each, the fingerprint's width, the items
        /// held and the items it was built for. From version 11 (#231) the buckets, the
        /// items held and the items it was built for are each a <c>u64</c>.
        /// </summary>
        private static (ulong M, uint B, uint F, ulong Count, ulong N) ReadFields(
            ref PayloadReader reader, ushort version)
        {
            if (version >= PersistenceFormat.CuckooDenseVersion)
            {
                return (reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadUInt32(),
                    reader.ReadUInt64(), reader.ReadUInt64());
            }

            return (reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(),
                reader.ReadUInt32(), reader.ReadUInt32());
        }

        /// <summary>
        /// Refuses a payload too short to hold the table its fields give, and the two
        /// generator states after it, before anything is priced or allocated for it. From
        /// version 11 the table is in the payload (#230), so what a read allocates follows
        /// what the payload holds, and a few bytes claiming a large table are refused for
        /// the bytes they lack, whatever the budget.
        /// </summary>
        private static void RequireTable(ref PayloadReader reader, ulong entries, uint bits) =>
            reader.RequireHolds(PackedWords(entries, bits) + MapWords(entries) + 2, sizeof(ulong),
                "words of table and generator state");

        /// <summary>
        /// Reads as many words as <paramref name="into"/> holds, which have no length before
        /// them, straight into it, a piece at a time through a buffer on the stack (#289):
        /// copied from a held payload, and read from the stream from one that is not. Nothing
        /// is allocated for them, so the table is not held twice (#230).
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

        /// <summary>
        /// Checks a table read whole holds only what the writer leaves in one: nothing past
        /// its last entry, in the fingerprints or in the map, and no fingerprint in an entry
        /// the map marks empty, since a removal clears what it empties and a reset clears
        /// everything. The writer cannot have produced either, and a filter read from it
        /// would write it back.
        /// </summary>
        private static void CheckTable(ulong[] fingerprints, ulong[] occupancy, ulong entries, uint bits)
        {
            var packed = PackedWords(entries, bits);
            var used = (int)(entries * bits % 64);
            if (used != 0 && fingerprints[packed - 1] >> used != 0)
            {
                throw new InvalidDataException(
                    $"Filter has fingerprint bits set past its last entry: its {entries} " +
                    $"entries of {bits} bits leave {64 - used} bits of their last word unused.");
            }

            var mapped = (int)(entries % 64);
            if (mapped != 0 && occupancy[^1] >> mapped != 0)
            {
                throw new InvalidDataException(
                    $"Filter marks a place occupied past its last entry: it has {entries} " +
                    "entries, and a bit of its map after them is set.");
            }

            // Sixty-four entries are 64 * bits bits, which is exactly bits words, so each
            // word of the map covers the entries of whole words of fingerprints of their own.
            for (var word = 0; word < occupancy.Length; word++)
            {
                var held = occupancy[word];
                if (held == ulong.MaxValue)
                {
                    continue;
                }

                var first = (ulong)word * 64;
                var here = Math.Min(64, entries - first);
                if (held == 0 && here == 64
                    && fingerprints.AsSpan(BitOf(first, bits).Word, (int)bits).IndexOfAnyExcept(0UL) < 0)
                {
                    continue;
                }

                var empty = ~held & (here == 64 ? ulong.MaxValue : (1UL << (int)here) - 1);
                while (empty != 0)
                {
                    var entry = first + (ulong)BitOperations.TrailingZeroCount(empty);
                    if (EntryIn(fingerprints, bits, entry) != 0)
                    {
                        throw new InvalidDataException(
                            $"Filter holds a fingerprint in entry {entry}, which its map " +
                            "marks empty.");
                    }

                    empty &= empty - 1;
                }
            }
        }

        /// <summary>
        /// Reads the entries a payload before version 11 lists, each placed by the bucket and
        /// entry it carries, into the table and the map, and returns how many it filled.
        /// </summary>
        private static ulong ReadEntries(
            ref PayloadReader reader, ushort version, ulong[] fingerprints, ulong[] occupancy,
            ulong m, uint b, uint f, uint bits)
        {
            var occupied = ReadOccupied(ref reader, m, b);

            // One buffer for the whole loop: widening a stored fingerprint needs eight
            // bytes of scratch, and allocating them per entry grows the stack by the
            // number of entries a payload claims to hold.
            Span<byte> widened = stackalloc byte[8];

            for (uint e = 0; e < occupied; e++)
            {
                var bucket = reader.ReadUInt32();
                var entry = reader.ReadUInt32();
                CheckEntry(bucket, entry, m, b);

                ulong fingerprint;
                if (version >= PersistenceFormat.CuckooPackedVersion)
                {
                    fingerprint = 0;
                    for (var shift = 0; shift < (bits + 7) / 8; shift++)
                    {
                        fingerprint |= (ulong)reader.ReadByte() << (shift * 8);
                    }

                    if (bits < 64 && fingerprint >= (1UL << (int)bits))
                    {
                        throw new InvalidDataException(
                            $"Filter holds the fingerprint {fingerprint}, which does " +
                            $"not fit the {bits} bits its own width allows.");
                    }
                }
                else
                {
                    var stored = reader.ReadBytes();
                    CheckStored(stored.Length, f);

                    widened.Clear();
                    stored.CopyTo(widened);
                    fingerprint = BinaryPrimitives.ReadUInt64LittleEndian(widened);
                }

                var number = EntryNumber(bucket, entry, b);
                WriteEntryIn(fingerprints, bits, number, fingerprint);
                SetOccupied(occupancy, number, true);
            }

            // The entries held, counted from the map rather than from the list: a list
            // that names a place twice fills it once, the second fingerprint in place of
            // the first. The filter then holds fewer entries than it listed.
            var filled = CountOccupied(occupancy);
            if (filled != occupied)
            {
                throw new InvalidDataException(
                    $"Filter lists {occupied} entries but they fill {filled} places in " +
                    "its table: a bucket and entry is named more than once.");
            }

            return filled;
        }

        /// <summary>
        /// Prices what <see cref="Read"/> allocates beyond the envelope: the whole table
        /// and its occupancy map, and, before fingerprints were packed, the copy of each
        /// stored one. See <see cref="PayloadPrice"/>.
        /// </summary>
        internal static void Price(ref PayloadReader reader, ushort version)
        {
            var (m, b, f, _, _) = ReadFields(ref reader, version);
            var bits = CheckShape(m, b, f, version);
            var entries = m * b;
            if (version >= PersistenceFormat.CuckooDenseVersion)
            {
                RequireTable(ref reader, entries, bits);
            }

            reader.Budget.Price(TableCost(entries, bits));
            if (version >= PersistenceFormat.CuckooPackedVersion)
            {
                // Packed entries are fixed fields, and a table read whole is read into the
                // table priced; nothing past the table is allocated.
                return;
            }

            var occupied = ReadOccupied(ref reader, m, b);
            for (uint e = 0; e < occupied; e++)
            {
                CheckEntry(reader.ReadUInt32(), reader.ReadUInt32(), m, b);
                CheckStored(reader.SkipBytes(), f);
            }
        }

        private static uint ReadOccupied(ref PayloadReader reader, ulong m, uint b)
        {
            var occupied = reader.ReadUInt32();
            if (occupied > m * b)
            {
                throw new InvalidDataException(
                    $"Filter claims {occupied} occupied entries in {m} buckets of {b}.");
            }
            return occupied;
        }

        /// <summary>
        /// The entries marked occupied in the map, which has a bit for each.
        /// </summary>
        private static ulong CountOccupied(ulong[] occupancy)
        {
            // Bits past the last entry are never set, so every set bit is an entry.
            ulong filled = 0;
            foreach (var word in occupancy)
            {
                filled += (ulong)BitOperations.PopCount(word);
            }
            return filled;
        }

        private static void CheckEntry(uint bucket, uint entry, ulong m, uint b)
        {
            if (bucket >= m || entry >= b)
            {
                throw new InvalidDataException(
                    $"Filter holds an entry at bucket {bucket} slot {entry}, outside " +
                    $"its {m} buckets of {b}.");
            }
        }

        /// <summary>
        /// Checks a fingerprint stored before packing is the width the filter names, as
        /// the read does before the next is copied.
        /// </summary>
        private static void CheckStored(int length, uint f)
        {
            if (length != f)
            {
                throw new InvalidDataException(
                    $"Filter holds a {length}-byte fingerprint where its " +
                    $"own fingerprint size is {f}.");
            }
        }

        /// <summary>
        /// Checks the shape a payload claims, as both the read and its price do before
        /// the table is allocated, and returns its fingerprint width in bits.
        /// </summary>
        private static uint CheckShape(ulong m, uint b, uint f, ushort version)
        {
            if (m == 0 || b == 0)
            {
                throw new InvalidDataException(
                    $"Filter has {m} buckets of {b} entries, leaving nowhere to put " +
                    "anything.");
            }

            // An item's other bucket is found by combining its first with a hash of its
            // fingerprint and reducing by the bucket count, and the first is found again
            // from the other end only when that count is a power of two. That is why the
            // constructor rounds up to one. With any other, a relocation can leave an entry
            // in a bucket no lookup visits, and the filter loses an item it accepted.
            if (!BitOperations.IsPow2(m))
            {
                throw new InvalidDataException(
                    $"Filter has {m} buckets, which is not a power of two, and the " +
                    "index arithmetic of a cuckoo filter needs one.");
            }

            // Version 2 and earlier recorded a width in bytes; version 3 records it in
            // bits. A filter written before packing restores at eight bits per stored
            // byte, which reproduces exactly the fingerprints it held: the value is
            // the same low bits of the same digest, and hashing it to find its partner
            // bucket sees the same bytes it always did.
            var bits = version >= PersistenceFormat.CuckooPackedVersion ? f : f * 8;

            if (bits == 0 || bits > 64)
            {
                throw new InvalidDataException(
                    $"Filter claims a fingerprint width of {bits} bits, and one is " +
                    "between 1 and 64 -- the bits a hash supplies.");
            }

            // The same limit the constructor applies, so that every filter it builds
            // reads back. The table is allocated whole, and before version 11 the payload
            // holds only the occupied entries, so a payload can ask for as large a table
            // as the constructor would build, and no larger.
            if (GeometryProblem(m, b, bits) is { } problem)
            {
                throw new InvalidDataException(
                    $"Filter claims {m} buckets of {b} entries at {bits} bits, beyond " +
                    $"anything this library builds: {problem}");
            }

            return bits;
        }

        /// <summary>
        /// The table of <paramref name="entries"/> fingerprints of <paramref name="bits"/>
        /// bits, and the map of which hold something.
        /// </summary>
        private static long TableCost(ulong entries, uint bits) => ReadBudget.Sum(
            ReadBudget.ArrayOf(WordsFor(entries, bits), sizeof(ulong)),
            ReadBudget.ArrayOf(MapWords(entries), sizeof(ulong)));

        /// <summary>
        /// Used only by <see cref="Read"/>, which sets every field itself.
        /// </summary>
        private CuckooBloomFilter()
        {
            this.Fingerprints = null!;
            this.Occupied = null!;
        }

        /// <summary>
        /// Sets the hashing function used in the filter.
        /// </summary>
        /// <param name="h">The hash function to use.</param>
        public void SetHash(Func<ReadOnlySpan<byte>, ulong> h)
        {
            ArgumentNullException.ThrowIfNull(h);
            Guard.HashMayBeReplaced(this.count == 0, nameof(CuckooBloomFilter));

            this.Hash = h;
        }

        /// <summary>
        /// The number of entry <paramref name="entry"/> of bucket <paramref name="bucket"/>:
        /// its place in the fingerprints, at that number times their width, and in the map.
        /// In 64 bits (#231), since a table can have more entries than a <c>uint</c> numbers,
        /// and every place in the table is found through it.
        /// </summary>
        internal static ulong EntryNumber(ulong bucket, uint entry, uint entriesPerBucket) =>
            bucket * entriesPerBucket + entry;

        /// <summary>
        /// The bucket an index lands in among <paramref name="buckets"/>, a power of two: the
        /// index's low bits, as many as the count has.
        /// </summary>
        /// <remarks>
        /// The index is 64 bits (#231). It was the low 32 bits of the same hash, so up to 2^32
        /// buckets it lands where it always did, and no stored entry moves; past that, the bits
        /// above them reach the buckets 32 bits could not. A mask rather than the remainder it
        /// was, which a power of two makes the same and which in 64 bits would be a division
        /// on every lookup.
        /// </remarks>
        internal static ulong BucketOf(ulong index, ulong buckets) => index & (buckets - 1);

        /// <summary>
        /// The fingerprint at entry number <paramref name="entry"/> of a packed table of
        /// <paramref name="bits"/>-bit fingerprints.
        /// </summary>
        private static ulong EntryIn(ulong[] fingerprints, uint bits, ulong entry)
        {
            var (word, offset) = BitOf(entry, bits);

            var value = fingerprints[word] >> offset;
            var taken = 64 - offset;

            if (taken < bits)
            {
                value |= fingerprints[word + 1] << taken;
            }

            return value & (bits >= 64 ? ulong.MaxValue : (1UL << (int)bits) - 1);
        }

        /// <summary>
        /// Writes the fingerprint at entry number <paramref name="entry"/> of a packed table,
        /// leaving its neighbours in the same words untouched.
        /// </summary>
        private static void WriteEntryIn(ulong[] fingerprints, uint bits, ulong entry, ulong fingerprint)
        {
            var mask = bits >= 64 ? ulong.MaxValue : (1UL << (int)bits) - 1;
            fingerprint &= mask;

            var (word, offset) = BitOf(entry, bits);

            fingerprints[word] = (fingerprints[word] & ~(mask << offset)) | (fingerprint << offset);

            var taken = 64 - offset;
            if (taken < bits)
            {
                var high = (1UL << ((int)bits - taken)) - 1;
                fingerprints[word + 1] = (fingerprints[word + 1] & ~high) | (fingerprint >> taken);
            }
        }

        /// <summary>
        /// Where entry number <paramref name="entry"/> of a packed table of
        /// <paramref name="bits"/>-bit entries begins: the word, and the bit within it, least
        /// significant first. Worked out in 64 bits (#231): a table of 2^33 ten-bit entries
        /// has bits up to 2^36, and the word is within one array whenever the table is. The
        /// map of which entries hold a fingerprint is such a table, of one-bit entries.
        /// </summary>
        internal static (int Word, int Offset) BitOf(ulong entry, uint bits)
        {
            var bit = entry * bits;
            return ((int)(bit >> 6), (int)(bit & 63));
        }

        /// <summary>Whether the map marks entry number <paramref name="entry"/> as holding a fingerprint.</summary>
        private static bool Holds(ulong[] map, ulong entry)
        {
            var (word, offset) = BitOf(entry, 1);
            return (map[word] >> offset & 1) != 0;
        }

        /// <summary>Marks entry number <paramref name="entry"/> in the map as holding a fingerprint or not.</summary>
        private static void SetOccupied(ulong[] map, ulong entry, bool occupied)
        {
            var (at, offset) = BitOf(entry, 1);
            var bit = 1UL << offset;
            ref var word = ref map[at];
            word = occupied ? word | bit : word & ~bit;
        }

        /// <summary>
        /// The bytes of one entry, addressed directly in the packed array.
        /// </summary>
        private ulong EntryAt(ulong entry) => EntryIn(this.Fingerprints, this.FingerprintBits, entry);

        /// <summary>
        /// Writes one entry into the packed array, leaving its neighbours in the same
        /// words untouched.
        /// </summary>
        private void WriteEntryAt(ulong entry, ulong fingerprint) =>
            WriteEntryIn(this.Fingerprints, this.FingerprintBits, entry, fingerprint);

        /// <summary>
        /// A fingerprint as the bytes the hash sees, and the whole of the hash of them. Its
        /// partner bucket is derived from it, so this shape is part of the filter's
        /// behavior, not a detail: at a width of 8n bits it is the same n bytes the
        /// byte-aligned filter hashed, which is what lets a payload written before packing
        /// restore unchanged.
        /// </summary>
        private ulong HashOfFingerprint(ulong fingerprint)
        {
            // An inline array, not a stackalloc: a method with a stackalloc cannot be
            // inlined and carries a zeroed frame with a stack check, and this runs once
            // for every add and test and again on every relocation step.
            EightBytes buffer = default;
            Span<byte> bytes = buffer;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, fingerprint);
            return this.Hash(bytes[..this.HashWidth]);
        }

        /// <summary>
        /// Whether a bucket holds this fingerprint.
        /// </summary>
        private bool Contains(ulong bucket, ulong f)
        {
            return IndexOf(bucket, f) != -1;
        }

        /// <summary>
        /// Where this fingerprint sits in a bucket, or -1.
        /// </summary>
        private long IndexOf(ulong bucket, ulong f)
        {
            for (uint entry = 0; entry < this.B; entry++)
            {
                var number = EntryNumber(bucket, entry, this.B);
                if (Holds(this.Occupied, number) && EntryAt(number) == f)
                {
                    return entry;
                }
            }

            return -1;
        }

        /// <summary>
        /// A free entry in a bucket, or -1 if it is full.
        /// </summary>
        private long GetEmptyEntry(ulong bucket)
        {
            for (uint entry = 0; entry < this.B; entry++)
            {
                if (!Holds(this.Occupied, EntryNumber(bucket, entry, this.B)))
                {
                    return entry;
                }
            }

            return -1;
        }

        /// <summary>
        /// Will insert the fingerprint into the filter returning false if the filter is
        /// full.
        /// </summary>
        /// <param name="i1">The element's first candidate bucket, before it is reduced.</param>
        /// <param name="i2">The element's second candidate bucket, before it is reduced.</param>
        /// <param name="f">The fingerprint to insert.</param>
        /// <returns>
        /// True if the insert was successful. False if the filter is full
        /// </returns>
        private bool Insert(ulong i1, ulong i2, ulong f)
        {
            // Try to insert into bucket[i1], then bucket[i2].
            foreach (var bucket in stackalloc[] { BucketOf(i1, this.M), BucketOf(i2, this.M) })
            {
                var free = GetEmptyEntry(bucket);
                if (free != -1)
                {
                    Place(EntryNumber(bucket, (uint)free, this.B), f);
                    this.count++;
                    return true;
                }
            }

            // Must relocate existing items. Each step displaces whatever is in a chosen
            // entry, puts the fingerprint in hand there, and looks for a home for what
            // was displaced.
            //
            // Where each displacement happened is recorded, because the loop can run out
            // of kicks while still holding one. Dropping it there loses an element the
            // filter had already accepted, which is a false negative -- the one thing
            // this filter, like every other here, promises not to produce. The record is
            // the entry's number, which says its bucket and its place in it.
            Span<ulong> trail = stackalloc ulong[MAX_NUM_KICKS];

            // The fingerprint in hand and the one just displaced. Values now, not
            // buffers: packing made a fingerprint a number, so the relocation loop
            // carries it in a register instead of copying bytes between two stack
            // spans on every kick.
            var current = f;
            var displaced = 0UL;

            var i = i1;
            var depth = 0;

            for (int n = 0; n < MAX_NUM_KICKS; n++)
            {
                this.KickSteps++;

                var entry = EntryNumber(BucketOf(i, this.M), this.random.NextBelow(this.B), this.B);

                // The loop only relocates out of a bucket with no free entry, so every
                // entry in it is occupied and what comes out is a real fingerprint.
                displaced = EntryAt(entry);
                Place(entry, current);

                trail[depth] = entry;
                depth++;

                current = displaced;
                i ^= HashOfFingerprint(current);

                var alternate = BucketOf(i, this.M);
                var free = GetEmptyEntry(alternate);
                if (free != -1)
                {
                    Place(EntryNumber(alternate, (uint)free, this.B), current);
                    this.count++;
                    return true;
                }
            }

            // Out of kicks. Undo every displacement, in reverse, so the filter holds
            // exactly what it held before this call, and refuse the new element rather
            // than silently keeping it in place of one that was already there.
            for (int n = depth - 1; n >= 0; n--)
            {
                var entry = trail[n];

                displaced = EntryAt(entry);
                Place(entry, current);
                current = displaced;
            }

            return false;
        }

        /// <summary>
        /// Writes a fingerprint into an entry and marks it occupied.
        /// </summary>
        private void Place(ulong entry, ulong f)
        {
            WriteEntryAt(entry, f);
            SetOccupied(this.Occupied, entry, true);
        }

        /// <summary>
        /// Returns the two hash values used to index into the buckets and the
        /// fingerprint for the given element.
        /// </summary>
        /// <param name="data">Data</param>
        /// <returns>The two hash values used to index into the buckets and the
        /// fingerprint for the given data</returns>
        internal Components GetComponents(ReadOnlySpan<byte> data)
        {
            // The hash supplies 64 bits. The fingerprint is taken from its bytes,
            // as it was previously taken from the leading bytes of a digest. The eight
            // bytes are an inline array, not a stackalloc, for the reason given in
            // HashOfFingerprint: this is every add and test.
            EightBytes digestBytes = default;
            Span<byte> digest = digestBytes;
            BinaryPrimitives.WriteUInt64LittleEndian(digest, this.Hash(data));

            // The low bits of the digest, which at a width of 8n bits are exactly the
            // n leading digest bytes the byte-aligned filter used.
            var f = BinaryPrimitives.ReadUInt64LittleEndian(digest) & this.FingerprintMask;

            // The whole of the digest's hash, where it was its low 32 bits: those are
            // unchanged, and up to 2^32 buckets they are the only bits a bucket is chosen
            // by (see BucketOf), so every entry stored before is where it was (#231).
            var i1 = this.Hash(digest);

            // The two candidate buckets form a pair: i2 must be i1 XOR the
            // fingerprint hash, so either index recovers the other. The relocation
            // loop in Insert depends on it, computing an element's alternate bucket
            // as i ^ HashOfFingerprint(f).
            var i2 = i1 ^ this.HashOfFingerprint(f);

            return Components.Create(f, i1, i2);
        }

        /// <summary>
        /// The fingerprint width, in bits, that holds the false positive rate to
        /// epsilon. A cuckoo filter's rate is about 2b / 2^f for a fingerprint of f
        /// bits, so f is log2(2b / epsilon), rounded up to a whole bit and clamped to
        /// between 1 and the hash's 64.
        /// </summary>
        /// <param name="b">Entries per bucket</param>
        /// <param name="epsilon">False positive rate</param>
        /// <returns>The fingerprint width in bits</returns>
        /// <remarks>
        /// This used to return bytes, and both of its steps were wrong in the same
        /// direction. The logarithm was natural rather than base two, which understates
        /// the bits needed by a factor of ln(2); and the conversion to bytes divided
        /// rather than rounding up, which floors to zero for every rate this library
        /// accepts and was then clamped to one. The result was a one-byte fingerprint
        /// whatever epsilon was asked for -- eight bits, for a rate of about 3%, whether
        /// the caller wanted 1% or 0.001%.
        /// <para>
        /// Fingerprints are now stored packed at exactly this width, so the rate
        /// delivered is better than the rate asked for only by what rounding up to a
        /// whole bit gives, until the clamp at 64 bits is reached.
        /// </para>
        /// </remarks>
        private static uint CalculateFingerprintBits(uint b, double epsilon)
        {
            var bits = (uint)Math.Ceiling(Math.Log2(2 * b / epsilon));

            // The hash supplies 64 bits, so a fingerprint cannot be wider than that
            // however small a rate is asked for. Anything past it would be storage
            // reserved for bits that never arrive.
            return Math.Clamp(bits, 1u, 64u);
        }

        /// <summary>
        /// The 64-bit words the table's fingerprints fill, packed end to end: what a
        /// payload writes of them.
        /// </summary>
        private static ulong PackedWords(ulong entries, uint bits) => (entries * bits + 63) / 64;

        /// <summary>
        /// The 64-bit words of the map of which entries hold a fingerprint, a bit an entry.
        /// </summary>
        private static ulong MapWords(ulong entries) => (entries + 63) / 64;

        /// <summary>
        /// The 64-bit words allocated to pack the given number of entries at the given
        /// width. One spare, so that an entry ending exactly at a word boundary can
        /// still be read by the two-word path without a bounds check on every access.
        /// </summary>
        private static ulong WordsFor(ulong entries, uint bits) => PackedWords(entries, bits) + 1;

        /// <summary>
        /// Why a table of this shape cannot be built, or null if it can. Shared by the
        /// constructor and the reader, which is what keeps them from disagreeing.
        /// </summary>
        /// <remarks>
        /// The one limit is that the fingerprints are one array of 64-bit words. The map of
        /// which entries hold one is a bit an entry and they are at least a bit each, so it
        /// is never more words than they are, and fits whenever they do. Entries used to be
        /// numbered with a <c>uint</c>, which stopped a table at 2^31 of them; they are
        /// numbered in 64 bits now (#231), and a table of 2^33 ten-bit entries fits.
        /// </remarks>
        /// <param name="buckets">The number of buckets.</param>
        /// <param name="entriesPerBucket">The entries in each.</param>
        /// <param name="bits">The width of a fingerprint.</param>
        internal static string? GeometryProblem(ulong buckets, ulong entriesPerBucket, uint bits)
        {
            // In 128 bits. A payload's bucket count is a u64 from version 11, and the
            // product of two u64s, or its bits, can pass what one holds: 2^62 buckets of
            // four entries are 2^64 entries, which a ulong holds as none.
            var entries = (UInt128)buckets * entriesPerBucket;
            if ((entries * bits + 63) / 64 + 1 > (ulong)Array.MaxLength)
            {
                return $"{entries * bits} bits of fingerprints are more 64-bit words " +
                    $"than the {Array.MaxLength} one array holds.";
            }

            return null;
        }

        /// <summary>
        /// The share of a cuckoo filter's entries that can be filled before inserts
        /// start being refused. Four-entry buckets reach about this in practice.
        /// </summary>
        private const double LoadFactor = 0.95;

        /// <summary>
        /// Calculates the next power of two for the given value, in 64 bits (#231).
        /// </summary>
        /// <param name="x">Value</param>
        /// <returns>The next power of two for the given value</returns>
        private static ulong Power2(ulong x)
        {
            x--;
            x |= x >> 1;
            x |= x >> 2;
            x |= x >> 4;
            x |= x >> 8;
            x |= x >> 16;
            x |= x >> 32;
            x++;
            return x;
        }

        /// <summary>
        /// An item's fingerprint and its two candidate buckets, before they are reduced to
        /// the table's buckets.
        /// </summary>
        internal struct Components
        {
            internal ulong Fingerprint;
            internal ulong Hash1;
            internal ulong Hash2;

            internal static Components Create(ulong fingerprint, ulong hash1, ulong hash2)
            {
                return new Components
                {
                    Fingerprint = fingerprint,
                    Hash1 = hash1,
                    Hash2 = hash2
                };
            }
        }

        /// <summary>
        /// The outcome of a <see cref="CuckooBloomFilter.TestAndAdd(byte[])"/> call.
        /// </summary>
        public struct TestAndAddReturnValue
        {
            /// <summary>
            /// Whether the data was already a member of the filter.
            /// </summary>
            public bool WasAlreadyAMember { get; private set; }
            /// <summary>
            /// Whether the data was added. This is false when the data was already a
            /// member, and also when the filter was full.
            /// </summary>
            public bool Added { get; private set; }

            internal static TestAndAddReturnValue Create(bool wasAlreadyAMember, bool added)
            {
                return new TestAndAddReturnValue
                {
                    WasAlreadyAMember = wasAlreadyAMember,
                    Added = added
                };
            }
        }
    }
}
