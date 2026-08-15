using System;
using System.IO;
using System.Linq;
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
    public class CuckooBloomFilter : IBinaryPersistable<CuckooBloomFilter>
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

        /// <summary>
        /// Every fingerprint, packed end to end. Entry j of bucket i begins at
        /// (i * B + j) * F.
        /// </summary>
        /// <remarks>
        /// One array rather than a jagged one. Holding each fingerprint in its own
        /// byte[] cost a 24-byte object header on two bytes of payload, plus an array
        /// of references per bucket: a filter sized for 100,000 items took 3.8 MB
        /// holding nothing and 11.5 MB holding them, against 256 KB of actual
        /// fingerprints.
        /// </remarks>
        internal byte[] Fingerprints { get; set; }

        /// <summary>
        /// One bit per entry, saying whether it holds a fingerprint.
        /// </summary>
        /// <remarks>
        /// A separate bit rather than treating an all-zero fingerprint as empty. That
        /// would be smaller, but a fingerprint is taken from a hash and can legitimately
        /// be all zero, so it would need forcing to something else -- which changes
        /// which bucket the element's alternate index resolves to, and so cannot be
        /// applied to fingerprints already written by an earlier version. Six percent of
        /// the fingerprint bytes buys reading every payload ever written unchanged.
        /// </remarks>
        internal Buckets Occupied { get; set; }
        /// <summary>
        /// Hash algorithm.
        /// </summary>
        private Func<ReadOnlySpan<byte>, ulong> Hash { get; set; } = null!;
        /// <summary>
        /// Number of buckets
        /// </summary>
        private uint M { get; set; }
        /// <summary>
        /// Number of entries per bucket
        /// </summary>
        internal uint B { get; set; }
        /// <summary>
        /// Length of fingerprints (in bytes)
        /// </summary>
        private uint F { get; set; }
        /// <summary>
        /// Number of items in the filter
        /// </summary>
        private uint count { get; set; }
        /// <summary>
        /// Filter capacity
        /// </summary>
        private uint N { get; set; }

        private SeededRandom random;

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
        public CuckooBloomFilter(uint n, double fpRate,
            Func<ReadOnlySpan<byte>, ulong>? hash = null, int? seed = null)
        {
            Guard.ValidItemCount(n, nameof(n));
            Guard.ValidFalsePositiveRate(fpRate, nameof(fpRate));

            var b = (uint)4;
            var f = CalculateF(b, fpRate);

            // Buckets needed to hold n items at b entries each, with room to spare so
            // that inserts still find a home near capacity: a cuckoo filter's load
            // factor is around 0.95 for four-entry buckets, and inserts start failing
            // as it is approached. Rounded up to a power of two, which the index
            // arithmetic requires.
            //
            // This used to be Power2(n / f * 8), which is not a bucket count at all.
            // The 8 undoes a division by 8 in CalculateF that had already floored to
            // nothing, and what came out was roughly eight buckets per item rather than
            // one per four items -- thirty-two times more than needed. A filter asked
            // for 100,000 items allocated 122 MB while holding nothing, against 124 KB
            // for a Bloom filter of the same capacity and rate.
            var m = Power2((uint)Math.Ceiling(n / (b * LoadFactor)));
            var entries = (ulong)m * b;
            if (entries * f > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(n), n,
                    $"A filter for {n} items at this rate needs {entries * f} bytes of " +
                    "fingerprints, which cannot be held in a single array.");
            }

            this.Fingerprints = new byte[entries * f];
            this.Occupied = new Buckets((uint)entries, 1);
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();
            this.random = seed is null
                ? SeededRandom.Unpredictable()
                : new SeededRandom((ulong)seed.Value);
            this.M = m;
            this.B = b;
            this.F = f;
            this.N = n;
        }

        /// <summary>
        /// Returns the number of buckets.
        /// </summary>
        /// <returns>The number of buckets</returns>
        public uint BucketCount()
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
        public uint Capacity()
        {
            return this.N;
        }

        /// <summary>
        /// Returns the number of items in the filter.
        /// </summary>
        /// <returns>The number of items in the filter</returns>
        public uint Count()
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

            var components = this.GetComponents(data);
            var i1 = components.Hash1;
            var i2 = components.Hash2;
            var f = components.Fingerprint;

            // If either bucket contains f, it's a member.
            return Contains(i1 % this.M, f) || Contains(i2 % this.M, f);
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

            var components = this.GetComponents(data);
            var i1 = components.Hash1;
            var i2 = components.Hash2;
            var f = components.Fingerprint;

            // If either bucket contains f, it's a member.
            if (Contains(i1 % this.M, f) || Contains(i2 % this.M, f))
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

            var components = this.GetComponents(data);
            var i1 = components.Hash1;
            var i2 = components.Hash2;
            var f = components.Fingerprint;

            // Try bucket[i1], then bucket[i2]. Clearing the occupancy bit is what
            // empties an entry; the fingerprint bytes are left where they are and
            // overwritten by whatever lands there next.
            foreach (var bucket in stackalloc[] { i1 % this.M, i2 % this.M })
            {
                var idx = IndexOf(bucket, f);
                if (idx != -1)
                {
                    SetOccupied(bucket, (uint)idx, false);
                    this.count--;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Restores the Bloom filter to its original state. It returns the filter to
        /// allow for chaining.
        /// </summary>
        /// <returns>The CuckooBloomFilter</returns>
        public CuckooBloomFilter Reset()
        {
            // Only the occupancy needs clearing: an entry nothing claims is never
            // read, so the fingerprint bytes left behind are unreachable.
            this.Occupied.Reset();
            this.count = 0;
            return this;
        }

        /// <summary>
        /// Writes this filter to a stream, in the format documented in FORMAT.md.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = new PayloadWriter();
            payload.WriteUInt32(this.M);
            payload.WriteUInt32(this.B);
            payload.WriteUInt32(this.F);
            payload.WriteUInt32(this.count);
            payload.WriteUInt32(this.N);

            // Occupied slots only, each located by bucket and entry. A cuckoo filter
            // sized for a load it has not reached is mostly empty slots.
            var occupied = 0u;
            for (uint i = 0; i < this.M; i++)
            {
                for (uint j = 0; j < this.B; j++)
                {
                    if (IsOccupied(i, j))
                    {
                        occupied++;
                    }
                }
            }

            payload.WriteUInt32(occupied);
            for (uint i = 0; i < this.M; i++)
            {
                for (uint j = 0; j < this.B; j++)
                {
                    if (IsOccupied(i, j))
                    {
                        payload.WriteUInt32(i);
                        payload.WriteUInt32(j);
                        payload.WriteBytes(EntryAt(i, j));
                    }
                }
            }

            payload.WriteUInt64(this.random.State);

            PersistenceFormat.Write(
                stream,
                StructureId.CuckooBloomFilter,
                PersistenceFormat.Identify(this.Hash),
                payload.WrittenSpan,
                PersistenceFormat.RandomStateVersion);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The filter that was written.</returns>
        public static CuckooBloomFilter ReadFrom(Stream stream)
        {
            return Read(stream, null);
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
            return Read(stream, hash);
        }

        private static CuckooBloomFilter Read(Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(
                stream, StructureId.CuckooBloomFilter, out var hashId, out var version);
            var reader = new PayloadReader(payload);

            var m = reader.ReadUInt32();
            var b = reader.ReadUInt32();
            var f = reader.ReadUInt32();
            var count = reader.ReadUInt32();
            var n = reader.ReadUInt32();

            if (m == 0 || b == 0)
            {
                throw new InvalidDataException(
                    $"Filter has {m} buckets of {b} entries, leaving nowhere to put " +
                    "anything.");
            }

            if (m > PersistenceFormat.MaxNestedCount)
            {
                throw new InvalidDataException(
                    $"Filter claims {m} buckets, beyond anything this library builds.");
            }

            var entries = (ulong)m * b;
            if (entries * f > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"Filter claims {m} buckets of {b} entries at {f} bytes, which " +
                    "cannot be held in a single array.");
            }

            var fingerprints = new byte[entries * f];
            var occupancy = new Buckets((uint)entries, 1);

            var occupied = reader.ReadUInt32();
            if (occupied > m * (ulong)b)
            {
                throw new InvalidDataException(
                    $"Filter claims {occupied} occupied entries in {m} buckets of {b}.");
            }

            for (uint e = 0; e < occupied; e++)
            {
                var bucket = reader.ReadUInt32();
                var entry = reader.ReadUInt32();

                if (bucket >= m || entry >= b)
                {
                    throw new InvalidDataException(
                        $"Filter holds an entry at bucket {bucket} slot {entry}, outside " +
                        $"its {m} buckets of {b}.");
                }

                var fingerprint = reader.ReadBytes();
                if (fingerprint.Length != f)
                {
                    throw new InvalidDataException(
                        $"Filter holds a {fingerprint.Length}-byte fingerprint where its " +
                        $"own fingerprint size is {f}.");
                }

                fingerprint.CopyTo(fingerprints.AsSpan((int)((bucket * b + entry) * f)));
                occupancy.Set(bucket * b + entry, 1);
            }

            // Version 1 predates the stored generator state. Such a filter resumes with
            // an unpredictable one: its fingerprints are exactly right, and only the
            // sequence of entries it will evict next is unknowable.
            var random = version >= PersistenceFormat.RandomStateVersion
                ? new SeededRandom(reader.ReadUInt64())
                : SeededRandom.Unpredictable();

            reader.ExpectEnd();

            return new CuckooBloomFilter
            {
                Fingerprints = fingerprints,
                Occupied = occupancy,
                M = m,
                B = b,
                F = f,
                count = count,
                N = n,
                Hash = PersistenceFormat.ResolveOrThrow(hashId, hash),
                random = random,
            };
        }

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
        /// Indicates if the given fingerprint is contained in one of the bucket's
        /// entries.
        /// </summary>
        /// <returns>
        /// Whether or not the fingerprint is contained in one of the bucket's entries.
        /// </returns>
        /// <summary>
        /// The bytes of one entry, addressed directly in the packed array.
        /// </summary>
        private Span<byte> EntryAt(uint bucket, uint entry)
        {
            return this.Fingerprints.AsSpan((int)((bucket * this.B + entry) * this.F), (int)this.F);
        }

        /// <summary>
        /// Whether an entry holds a fingerprint.
        /// </summary>
        private bool IsOccupied(uint bucket, uint entry)
        {
            return this.Occupied.Get(bucket * this.B + entry) != 0;
        }

        private void SetOccupied(uint bucket, uint entry, bool occupied)
        {
            this.Occupied.Set(bucket * this.B + entry, occupied ? (byte)1 : (byte)0);
        }

        /// <summary>
        /// Whether a bucket holds this fingerprint.
        /// </summary>
        private bool Contains(uint bucket, ReadOnlySpan<byte> f)
        {
            return IndexOf(bucket, f) != -1;
        }

        /// <summary>
        /// Returns the entry index of the given fingerprint or -1 if it's not in the
        /// bucket.
        /// </summary>
        /// <returns>The entry index of the fingerprint or -1 if it's not in the
        /// bucket</returns>
        /// <summary>
        /// Where this fingerprint sits in a bucket, or -1.
        /// </summary>
        private int IndexOf(uint bucket, ReadOnlySpan<byte> f)
        {
            for (uint entry = 0; entry < this.B; entry++)
            {
                if (IsOccupied(bucket, entry) && EntryAt(bucket, entry).SequenceEqual(f))
                {
                    return (int)entry;
                }
            }

            return -1;
        }

        /// <summary>
        /// Returns the index of the next available entry in the bucket or -1 if it's
        /// full.
        /// </summary>
        /// <returns></returns>
        /// <summary>
        /// A free entry in a bucket, or -1 if it is full.
        /// </summary>
        private int GetEmptyEntry(uint bucket)
        {
            for (uint entry = 0; entry < this.B; entry++)
            {
                if (!IsOccupied(bucket, entry))
                {
                    return (int)entry;
                }
            }

            return -1;
        }

        /// <summary>
        /// Will insert the fingerprint into the filter returning false if the filter is
        /// full.
        /// </summary>
        /// <param name="i1">The element's first candidate bucket.</param>
        /// <param name="i2">The element's second candidate bucket.</param>
        /// <param name="f">The fingerprint to insert.</param>
        /// <returns>
        /// True if the insert was successful. False if the filter is full
        /// </returns>
        private bool Insert(uint i1, uint i2, byte[] f)
        {
            // Try to insert into bucket[i1], then bucket[i2].
            foreach (var bucket in stackalloc[] { i1 % this.M, i2 % this.M })
            {
                var free = GetEmptyEntry(bucket);
                if (free != -1)
                {
                    Place(bucket, (uint)free, f);
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
            // this filter, like every other here, promises not to produce.
            Span<uint> trailBucket = stackalloc uint[MAX_NUM_KICKS];
            Span<uint> trailEntry = stackalloc uint[MAX_NUM_KICKS];

            // The fingerprint in hand and the one just displaced, both on the stack.
            // Copying between them rather than allocating a fresh array per kick: the
            // loop runs up to MAX_NUM_KICKS times and again to unwind, so an allocation
            // here is a thousand of them on a filter that is refusing the insert anyway.
            Span<byte> current = stackalloc byte[(int)this.F];
            Span<byte> displaced = stackalloc byte[(int)this.F];
            f.CopyTo(current);

            var i = i1;
            var depth = 0;

            for (int n = 0; n < MAX_NUM_KICKS; n++)
            {
                var bucketIdx = i % this.M;
                var entryIdx = this.random.NextBelow(this.B);

                // The loop only relocates out of a bucket with no free entry, so every
                // entry in it is occupied and what comes out is a real fingerprint.
                EntryAt(bucketIdx, entryIdx).CopyTo(displaced);
                Place(bucketIdx, entryIdx, current);

                trailBucket[depth] = bucketIdx;
                trailEntry[depth] = entryIdx;
                depth++;

                displaced.CopyTo(current);
                i = i ^ ComputeHashSum32(current);

                var alternate = i % this.M;
                var free = GetEmptyEntry(alternate);
                if (free != -1)
                {
                    Place(alternate, (uint)free, current);
                    this.count++;
                    return true;
                }
            }

            // Out of kicks. Undo every displacement, in reverse, so the filter holds
            // exactly what it held before this call, and refuse the new element rather
            // than silently keeping it in place of one that was already there.
            for (int n = depth - 1; n >= 0; n--)
            {
                var bucket = trailBucket[n];
                var entry = trailEntry[n];

                EntryAt(bucket, entry).CopyTo(displaced);
                Place(bucket, entry, current);
                displaced.CopyTo(current);
            }

            return false;
        }

        /// <summary>
        /// Writes a fingerprint into an entry and marks it occupied.
        /// </summary>
        private void Place(uint bucket, uint entry, ReadOnlySpan<byte> f)
        {
            f.CopyTo(EntryAt(bucket, entry));
            SetOccupied(bucket, entry, true);
        }

        /// <summary>
        /// Returns the two hash values used to index into the buckets and the
        /// fingerprint for the given element.
        /// </summary>
        /// <param name="data">Data</param>
        /// <returns>The two hash values used to index into the buckets and the
        /// fingerprint for the given data</returns>
        private Components GetComponents(byte[] data)
        {
            // The hash supplies 64 bits. The fingerprint is taken from its bytes,
            // as it was previously taken from the leading bytes of a digest.
            Span<byte> digest = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(digest, this.Hash(data));

            byte[] f = digest.Slice(0, Math.Min((int)this.F, digest.Length)).ToArray();

            var i1 = this.ComputeHashSum32(digest);

            // The two candidate buckets form a pair: i2 must be i1 XOR the
            // fingerprint hash, so either index recovers the other. The relocation
            // loop in Insert depends on it, computing an element's alternate bucket
            // as i ^ ComputeHashSum32(f).
            var i2 = i1 ^ this.ComputeHashSum32(f);

            return Components.Create(f, i1, i2);
        }

        /// <summary>
        /// Copies the first <paramref name="length"/> bytes of <paramref name="source"/>,
        /// clamped to its length. Matches what Take(length).ToArray() produced, which
        /// returned a short array rather than throwing when the source was smaller.
        /// </summary>
        private static byte[] Slice(ReadOnlySpan<byte> source, int length)
        {
            return source.Slice(0, Math.Min(length, source.Length)).ToArray();
        }

        /// <summary>
        /// Returns the sum of the hash.
        /// </summary>
        /// <param name="data">Data</param>
        /// <returns>32-bit hash value</returns>
        private uint ComputeHashSum32(byte[] data)
        {
            return this.ComputeHashSum32((ReadOnlySpan<byte>)data);
        }

        /// <summary>
        /// Returns the low 32 bits of the hash of the given data.
        /// </summary>
        /// <param name="data">Data</param>
        /// <returns>32-bit hash value</returns>
        private uint ComputeHashSum32(ReadOnlySpan<byte> data)
        {
            return (uint)(this.Hash(data) & 0xffffffff);
        }


        /// <summary>
        /// Returns the optimal fingerprint length in bytes for the given bucket size and
        /// false-positive rate epsilon.
        /// </summary>
        /// <param name="b">Bucket size</param>
        /// <param name="epsilon">False positive rate</param>
        /// <returns>The optimal fingerprint length</returns>
        /// <summary>
        /// The fingerprint size, in bytes, that holds the false positive rate to
        /// epsilon. A cuckoo filter's rate is about 2b / 2^f for a fingerprint of f
        /// bits, so f is log2(2b / epsilon), rounded up to whole bytes.
        /// </summary>
        /// <remarks>
        /// Both steps used to be wrong in the same direction. The logarithm was natural
        /// rather than base two, which understates the bits needed by a factor of
        /// ln(2); and the conversion to bytes divided rather than rounding up, which
        /// floors to zero for every rate this library accepts and was then clamped to
        /// one. The result was a one-byte fingerprint whatever epsilon was asked for --
        /// eight bits, for a rate of about 3%, whether the caller wanted 1% or 0.001%.
        /// <para>
        /// Whole bytes mean the rate delivered is usually better than the rate asked
        /// for, since the last partial byte is rounded up rather than away.
        /// </para>
        /// </remarks>
        private static uint CalculateF(uint b, double epsilon)
        {
            var bits = Math.Ceiling(Math.Log2(2 * b / epsilon));
            var bytes = (uint)Math.Ceiling(bits / 8);

            // The hash supplies 64 bits, so a fingerprint cannot be wider than eight
            // bytes however small a rate is asked for. Anything past that would be
            // storage reserved for bits that never arrive.
            return Math.Clamp(bytes, 1u, 8u);
        }

        /// <summary>
        /// The share of a cuckoo filter's entries that can be filled before inserts
        /// start being refused. Four-entry buckets reach about this in practice.
        /// </summary>
        private const double LoadFactor = 0.95;

        /// <summary>
        /// Calculates the next power of two for the given value.
        /// </summary>
        /// <param name="x">Value</param>
        /// <returns>The next power of two for the given value</returns>
        private static uint Power2(uint x)
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

        private struct Components
        {
            internal byte[] Fingerprint;
            internal uint Hash1;
            internal uint Hash2;

            internal static Components Create(byte[] fingerprint, uint hash1, uint hash2)
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
