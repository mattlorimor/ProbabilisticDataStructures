using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Grafite answers whether any stored key falls in a range, as described by Costa,
    /// Ferragina and Vinciguerra in Grafite: Taming Adversarial Queries with Optimal
    /// Range Filters (SIGMOD 2024).
    /// </summary>
    /// <remarks>
    /// Every other filter here answers "have I seen this one key?". Grafite answers
    /// "is there anything at all between a and b?" -- the question an index asks before
    /// it decides whether a block is worth reading, and the one a Bloom filter cannot
    /// answer without being asked about every key in the range.
    /// <para>
    /// What distinguishes it from the range filters that came before is where its
    /// false positive rate comes from. SuRF, Rosetta and their relatives bound theirs
    /// empirically, on query workloads that do not look like the keys; when the queries
    /// are correlated with the data -- which is the normal case, not the adversarial
    /// one, since people query near where their data is -- those rates collapse.
    /// Grafite's bound is a theorem about its choice of hash function and holds for any
    /// query sequence whatsoever, chosen by an adversary who has seen the keys but not
    /// the seed.
    /// </para>
    /// <para>
    /// The construction is unusually simple for what it delivers. Keys are hashed into
    /// a universe of size r = n*L/e by a function that is locality preserving -- keys
    /// near each other stay near each other -- so a range of keys becomes a range of
    /// hash codes, and the question becomes whether any stored code lies in an
    /// interval. Those codes are then stored in Elias-Fano, which is within a couple of
    /// bits per key of the information-theoretic minimum.
    /// </para>
    /// <para>
    /// The filter is built once from a known set and never changes, like
    /// <see cref="BinaryFuseFilter"/>. Keys are numbers rather than bytes: a range
    /// query is a question about order, and bytes have none that a caller would agree
    /// with.
    /// </para>
    /// </remarks>
    public class Grafite : IBinaryPersistable<Grafite>
    {
        /// <summary>
        /// The Mersenne prime 2^61 - 1, the modulus of the pairwise-independent hash.
        /// Large enough to exceed any reduced universe this builds, and a Mersenne
        /// prime so the reduction needs no division.
        /// </summary>
        private const ulong Prime = (1UL << 61) - 1;

        /// <summary>
        /// The size of the reduced universe, r = n*L/e.
        /// </summary>
        internal ulong R { get; set; }

        /// <summary>
        /// The multiplier of the pairwise-independent hash.
        /// </summary>
        internal ulong C1 { get; set; }

        /// <summary>
        /// The addend of the pairwise-independent hash.
        /// </summary>
        internal ulong C2 { get; set; }

        /// <summary>
        /// How many low bits of each hash code are stored verbatim.
        /// </summary>
        internal int LowBits { get; set; }

        /// <summary>
        /// The low parts of the hash codes, packed <see cref="LowBits"/> bits each.
        /// </summary>
        internal ulong[] Lows { get; set; }

        /// <summary>
        /// The high parts, as a bitvector with one bit set per code.
        /// </summary>
        internal ulong[] Highs { get; set; }

        /// <summary>
        /// How many bits of <see cref="Highs"/> are in use.
        /// </summary>
        internal int HighBitCount { get; set; }

        /// <summary>
        /// Running count of zeros at the start of each word of <see cref="Highs"/>,
        /// so a select can binary search rather than scan.
        /// </summary>
        internal int[] ZerosBefore { get; set; }

        /// <summary>
        /// How many distinct hash codes are stored.
        /// </summary>
        internal int CodeCount { get; set; }

        /// <summary>
        /// The smallest and largest stored hash codes, which reject most queries
        /// without touching the encoding at all.
        /// </summary>
        internal ulong FirstCode { get; set; }
        internal ulong LastCode { get; set; }

        /// <summary>
        /// How many keys were given at build time.
        /// </summary>
        internal ulong KeyCount { get; set; }

        /// <summary>
        /// The largest range size the false positive rate was promised for.
        /// </summary>
        internal ulong MaxRange { get; set; }

        private Grafite()
        {
            this.Lows = Array.Empty<ulong>();
            this.Highs = Array.Empty<ulong>();
            this.ZerosBefore = Array.Empty<int>();
        }

        /// <summary>
        /// Builds a filter over the given keys.
        /// </summary>
        /// <param name="keys">
        /// The keys the filter will answer about. Duplicates and order do not matter.
        /// </param>
        /// <param name="falsePositiveRate">
        /// The false positive rate to hold for ranges of <paramref name="maxRangeSize"/>
        /// keys. Shorter ranges do better in proportion: a range of length l is wrong
        /// with probability at most l/maxRangeSize of this.
        /// </param>
        /// <param name="maxRangeSize">
        /// The largest range the rate is promised for. Larger ranges still answer
        /// correctly -- there are never false negatives -- but their false positive
        /// rate grows in proportion.
        /// </param>
        /// <param name="seed">
        /// Seed for the hash choice, or null to seed unpredictably. The bound holds
        /// against an adversary who knows the keys and not the seed, so a fixed seed
        /// is for reproducing a build, not for production.
        /// </param>
        public static Grafite Build(
            IEnumerable<ulong> keys,
            double falsePositiveRate,
            ulong maxRangeSize,
            ulong? seed = null)
        {
            ArgumentNullException.ThrowIfNull(keys);
            Guard.ValidFalsePositiveRate(falsePositiveRate, nameof(falsePositiveRate));
            if (maxRangeSize == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxRangeSize), maxRangeSize,
                    "The largest range must span at least one key; a filter promised " +
                    "nothing about any range answers nothing about any.");
            }

            var distinct = new SortedSet<ulong>(keys);
            var n = (ulong)distinct.Count;

            var filter = new Grafite
            {
                KeyCount = n,
                MaxRange = maxRangeSize,
            };

            if (n == 0)
            {
                // Nothing is in any range, and every query can say so with certainty.
                filter.R = 1;
                filter.C1 = 1;
                return filter;
            }

            // r = n * L / e, the reduced universe. Computed in floating point and
            // clamped: the product overflows for large inputs long before the filter
            // stops being useful, and a universe wider than the keys' own buys
            // nothing.
            var wanted = n * (double)maxRangeSize / falsePositiveRate;
            var r = wanted >= Prime ? Prime - 1 : (ulong)Math.Ceiling(wanted);
            r = Math.Max(r, 2);
            filter.R = r;

            var random = seed is null
                ? SeededRandom.Unpredictable()
                : new SeededRandom(seed.Value);

            // c1 must not be zero, or the hash ignores the block entirely.
            filter.C1 = (random.Next() % (Prime - 1)) + 1;
            filter.C2 = random.Next() % Prime;

            var codes = new SortedSet<ulong>();
            foreach (var key in distinct)
            {
                codes.Add(filter.HashOf(key));
            }

            filter.Encode(codes);
            return filter;
        }

        /// <summary>
        /// Whether any stored key might equal the given key. A point query is a range
        /// of one.
        /// </summary>
        /// <param name="key">The key to look for.</param>
        public bool Test(ulong key)
        {
            return Test(key, key);
        }

        /// <summary>
        /// Whether any stored key might fall in [low, high], both ends included. False
        /// when the range is certainly empty; true when it might not be.
        /// </summary>
        /// <param name="low">The low end of the range, included.</param>
        /// <param name="high">The high end of the range, included.</param>
        public bool Test(ulong low, ulong high)
        {
            if (low > high)
            {
                throw new ArgumentException(
                    $"The range [{low}, {high}] runs backwards. A range filter cannot " +
                    "guess which end was meant.",
                    nameof(low));
            }

            if (this.CodeCount == 0)
            {
                return false;
            }

            // A range at least as wide as the reduced universe covers every hash code
            // there is, so no answer but "possibly" is safe.
            var width = (UInt128)high - low + 1;
            if (width >= this.R)
            {
                return true;
            }

            // The hash is only locality preserving within a block of r consecutive
            // keys: each block carries its own offset. A range that straddles a
            // boundary is therefore two runs of hash codes with unrelated offsets,
            // and testing it as one interval can miss a key that is genuinely there.
            // Since the range is narrower than a block it straddles at most one
            // boundary, so at most two tests are needed.
            var lowBlock = low / this.R;
            var highBlock = high / this.R;
            if (lowBlock == highBlock)
            {
                return TestWithinBlock(low, high);
            }

            var boundary = highBlock * this.R;
            return TestWithinBlock(low, boundary - 1) || TestWithinBlock(boundary, high);
        }

        /// <summary>
        /// The range test proper, for a range that lies within one block. There the
        /// hash is a shift, so the keys of the range map onto one cyclic interval of
        /// hash codes and the test is exact.
        /// </summary>
        private bool TestWithinBlock(ulong low, ulong high)
        {
            var hashLow = HashOf(low);
            var hashHigh = HashOf(high);

            if (hashLow <= hashHigh)
            {
                return AnyCodeInRange(hashLow, hashHigh);
            }

            // The interval wrapped around the end of the reduced universe, so it is
            // the two pieces at either end rather than the middle.
            return AnyCodeInRange(0, hashHigh) || AnyCodeInRange(hashLow, this.R - 1);
        }

        /// <summary>
        /// Whether any stored hash code lies in [low, high].
        /// </summary>
        private bool AnyCodeInRange(ulong low, ulong high)
        {
            if (low > this.LastCode || high < this.FirstCode)
            {
                return false;
            }

            var index = LowerBound(low);
            return index < this.CodeCount && CodeAt(index) <= high;
        }

        /// <summary>
        /// The hash: a pairwise-independent function of the key's block, plus the key
        /// itself, over the reduced universe.
        /// </summary>
        /// <remarks>
        /// Adding the key rather than hashing it is what preserves locality, and
        /// locality is what turns a range of keys into a range of hash codes. The
        /// block term is what keeps two far-apart keys from colliding predictably: it
        /// is redrawn, in effect, every r keys.
        /// </remarks>
        internal ulong HashOf(ulong key)
        {
            var block = key / this.R;
            var scrambled = (ulong)(((UInt128)this.C1 * block + this.C2) % Prime);
            return (ulong)(((UInt128)scrambled + key) % this.R);
        }

        /// <summary>
        /// Encodes the sorted hash codes in Elias-Fano.
        /// </summary>
        /// <remarks>
        /// Each code is split. The low bits go into a packed array, verbatim. The high
        /// bits go into a bitvector as a gap encoding: the ith code sets the bit at
        /// (high part + i), so the high parts are recovered by counting. The whole
        /// thing costs about log2(L/e) + 2 bits per key, which is within a constant of
        /// what any structure answering this question must spend.
        /// </remarks>
        private void Encode(SortedSet<ulong> codes)
        {
            var n = codes.Count;
            this.CodeCount = n;
            this.FirstCode = codes.Min;
            this.LastCode = codes.Max;

            // Split where the high parts are about as numerous as the codes, which is
            // what balances the two halves of the encoding.
            this.LowBits = Math.Max(0, BitOperations.Log2(this.R / (ulong)n));
            var lowMask = this.LowBits == 64 ? ulong.MaxValue : (1UL << this.LowBits) - 1;

            this.Lows = new ulong[((long)n * this.LowBits / 64) + 2];
            var highBits = (int)(this.LastCode >> this.LowBits) + n + 1;
            this.HighBitCount = highBits;
            this.Highs = new ulong[(highBits / 64) + 1];

            var i = 0;
            foreach (var code in codes)
            {
                WriteLow(i, code & lowMask);
                var high = (int)(code >> this.LowBits) + i;
                this.Highs[high / 64] |= 1UL << (high % 64);
                i++;
            }

            BuildSelectIndex();
        }

        /// <summary>
        /// Records how many zeros precede each word of the high bitvector, so that
        /// finding the nth zero is a binary search rather than a walk.
        /// </summary>
        private void BuildSelectIndex()
        {
            this.ZerosBefore = new int[this.Highs.Length + 1];
            var zeros = 0;
            for (var w = 0; w < this.Highs.Length; w++)
            {
                this.ZerosBefore[w] = zeros;
                var bitsInWord = Math.Min(64, this.HighBitCount - (w * 64));
                if (bitsInWord <= 0)
                {
                    continue;
                }
                var mask = bitsInWord == 64 ? ulong.MaxValue : (1UL << bitsInWord) - 1;
                zeros += bitsInWord - BitOperations.PopCount(this.Highs[w] & mask);
            }
            this.ZerosBefore[this.Highs.Length] = zeros;
        }

        /// <summary>
        /// The position of the nth zero in the high bitvector, counting from zero.
        /// </summary>
        private int SelectZero(int n)
        {
            // Which word holds it: the last one whose preceding zeros do not exceed n.
            var low = 0;
            var high = this.Highs.Length - 1;
            while (low < high)
            {
                var middle = (low + high + 1) / 2;
                if (this.ZerosBefore[middle] <= n)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }

            var remaining = n - this.ZerosBefore[low];
            var word = ~this.Highs[low];
            for (var bit = 0; bit < 64; bit++)
            {
                if ((word & (1UL << bit)) != 0)
                {
                    if (remaining == 0)
                    {
                        return (low * 64) + bit;
                    }
                    remaining--;
                }
            }

            return this.HighBitCount;
        }

        /// <summary>
        /// The index of the first stored code at least as large as the given value, or
        /// the code count if there is none.
        /// </summary>
        private int LowerBound(ulong value)
        {
            if (value <= this.FirstCode)
            {
                return 0;
            }
            if (value > this.LastCode)
            {
                return this.CodeCount;
            }

            var wantedHigh = (int)(value >> this.LowBits);
            var lowMask = this.LowBits == 64 ? ulong.MaxValue : (1UL << this.LowBits) - 1;
            var wantedLow = value & lowMask;

            // Codes whose high part is exactly wantedHigh occupy one run. The bit
            // vector is a unary histogram of the high parts -- c ones then a zero,
            // for each high value in turn -- so the kth zero sits at k plus the
            // number of codes whose high part is at most k, and the runs are read
            // off from a pair of those.
            var start = wantedHigh == 0
                ? 0
                : SelectZero(wantedHigh - 1) - (wantedHigh - 1);
            var end = SelectZero(wantedHigh) - wantedHigh;

            // Within the run the codes are sorted by their low parts, so the first one
            // not below the wanted low part is a binary search away.
            var low = start;
            var high = end;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (ReadLow(middle) < wantedLow)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            // If nothing in the run reaches the wanted low part, the search lands on
            // the index just past it -- which is the first code with a larger high
            // part, and so the answer either way.
            return low;
        }

        /// <summary>
        /// Rebuilds the code at an index from its two halves. Internal so that the
        /// tests can read the encoding back directly rather than only through query
        /// answers.
        /// </summary>
        internal ulong CodeAt(int index)
        {
            var high = SelectOne(index) - index;
            return ((ulong)high << this.LowBits) | ReadLow(index);
        }

        /// <summary>
        /// The position of the nth set bit in the high bitvector.
        /// </summary>
        private int SelectOne(int n)
        {
            // Ones and zeros account for every bit, so the index built for zeros
            // serves for ones too.
            var low = 0;
            var high = this.Highs.Length - 1;
            while (low < high)
            {
                var middle = (low + high + 1) / 2;
                if (OnesBefore(middle) <= n)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }

            var remaining = n - OnesBefore(low);
            var word = this.Highs[low];
            for (var bit = 0; bit < 64; bit++)
            {
                if ((word & (1UL << bit)) != 0)
                {
                    if (remaining == 0)
                    {
                        return (low * 64) + bit;
                    }
                    remaining--;
                }
            }

            return this.HighBitCount;
        }

        private int OnesBefore(int word)
        {
            return Math.Min(word * 64, this.HighBitCount) - this.ZerosBefore[word];
        }

        private void WriteLow(int index, ulong value)
        {
            if (this.LowBits == 0)
            {
                return;
            }

            var bit = (long)index * this.LowBits;
            var word = (int)(bit / 64);
            var offset = (int)(bit % 64);
            this.Lows[word] |= value << offset;
            if (offset + this.LowBits > 64)
            {
                this.Lows[word + 1] |= value >> (64 - offset);
            }
        }

        private ulong ReadLow(int index)
        {
            if (this.LowBits == 0)
            {
                return 0;
            }

            var bit = (long)index * this.LowBits;
            var word = (int)(bit / 64);
            var offset = (int)(bit % 64);
            var mask = this.LowBits == 64 ? ulong.MaxValue : (1UL << this.LowBits) - 1;
            var value = this.Lows[word] >> offset;
            if (offset + this.LowBits > 64)
            {
                value |= this.Lows[word + 1] << (64 - offset);
            }
            return value & mask;
        }

        /// <summary>
        /// The number of distinct keys the filter was built from.
        /// </summary>
        public ulong Count() => this.KeyCount;

        /// <summary>
        /// The size of the encoded hash codes, in bytes.
        /// </summary>
        public ulong SizeInBytes()
        {
            return (ulong)((this.Lows.Length + this.Highs.Length) * 8);
        }

        /// <summary>
        /// The false positive rate promised for a range of the given size.
        /// </summary>
        /// <param name="rangeSize">The range size to ask about.</param>
        public double FalsePositiveRate(ulong rangeSize)
        {
            if (this.KeyCount == 0)
            {
                return 0.0;
            }
            return Math.Min(1.0, (double)this.KeyCount * rangeSize / this.R);
        }

        /// <summary>
        /// Writes the filter to a stream in the library's persistence format.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = new PayloadWriter();
            payload.WriteUInt64(this.R);
            payload.WriteUInt64(this.C1);
            payload.WriteUInt64(this.C2);
            payload.WriteUInt64(this.KeyCount);
            payload.WriteUInt64(this.MaxRange);
            payload.WriteUInt32((uint)this.LowBits);
            payload.WriteUInt32((uint)this.CodeCount);
            payload.WriteUInt32((uint)this.HighBitCount);
            payload.WriteUInt64(this.FirstCode);
            payload.WriteUInt64(this.LastCode);

            payload.WriteUInt32((uint)this.Lows.Length);
            foreach (var word in this.Lows)
            {
                payload.WriteUInt64(word);
            }
            payload.WriteUInt32((uint)this.Highs.Length);
            foreach (var word in this.Highs)
            {
                payload.WriteUInt64(word);
            }

            PersistenceFormat.Write(
                stream, StructureId.Grafite, HashId.None, payload.WrittenSpan);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The filter that was written.</returns>
        public static Grafite ReadFrom(Stream stream)
        {
            return Read(stream, null);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>. The filter takes numbers
        /// rather than bytes, so supplying a hash function is refused rather than
        /// ignored.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">Not used, and refused if supplied.</param>
        /// <returns>The filter that was written.</returns>
        public static Grafite ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            return Read(stream, hash);
        }

        private static Grafite Read(Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(stream, StructureId.Grafite, out var hashId);

            if (hash is not null)
            {
                throw new InvalidDataException(
                    "A Grafite filter holds numbers rather than bytes -- a range is a " +
                    "question about order -- so it cannot be read with a supplied " +
                    "hash function. Read it with the overload that takes none.");
            }
            if (hashId != HashId.None)
            {
                throw new InvalidDataException(
                    $"Payload names hash function {(ushort)hashId}, and a Grafite " +
                    "filter does not hash bytes. It was not written by this structure.");
            }

            var reader = new PayloadReader(payload);
            var filter = new Grafite
            {
                R = reader.ReadUInt64(),
                C1 = reader.ReadUInt64(),
                C2 = reader.ReadUInt64(),
                KeyCount = reader.ReadUInt64(),
                MaxRange = reader.ReadUInt64(),
                LowBits = (int)reader.ReadUInt32(),
                CodeCount = (int)reader.ReadUInt32(),
                HighBitCount = (int)reader.ReadUInt32(),
                FirstCode = reader.ReadUInt64(),
                LastCode = reader.ReadUInt64(),
            };

            if (filter.R < 1)
            {
                throw new InvalidDataException(
                    "Filter claims a reduced universe of nothing, which every key " +
                    "would hash into and no range could be tested against.");
            }
            if (filter.C1 == 0 || filter.C1 >= Prime || filter.C2 >= Prime)
            {
                throw new InvalidDataException(
                    "Filter carries hash parameters this library never draws: the " +
                    "multiplier must be non-zero and both must fall below the modulus.");
            }
            if (filter.LowBits > 64)
            {
                throw new InvalidDataException(
                    $"Filter splits its codes at {filter.LowBits} low bits, and a " +
                    "code is sixty-four bits wide.");
            }
            if (filter.CodeCount < 0 || (ulong)filter.CodeCount > filter.KeyCount)
            {
                throw new InvalidDataException(
                    $"Filter holds {filter.CodeCount} hash codes for " +
                    $"{filter.KeyCount} keys, and hashing never produces more codes " +
                    "than the keys it was given.");
            }
            if (filter.CodeCount > 0 && filter.FirstCode > filter.LastCode)
            {
                throw new InvalidDataException(
                    $"Filter's smallest code {filter.FirstCode} exceeds its largest " +
                    $"{filter.LastCode}.");
            }
            if (filter.CodeCount > 0 && filter.LastCode >= filter.R)
            {
                throw new InvalidDataException(
                    $"Filter holds the code {filter.LastCode}, outside its own " +
                    $"reduced universe of {filter.R}.");
            }

            var lowWords = reader.ReadUInt32();
            if (lowWords > PersistenceFormat.MaxNestedCount)
            {
                throw new InvalidDataException(
                    $"Filter claims {lowWords} words of low bits, beyond anything " +
                    "this library builds.");
            }
            filter.Lows = new ulong[lowWords];
            for (var i = 0; i < lowWords; i++)
            {
                filter.Lows[i] = reader.ReadUInt64();
            }

            var highWords = reader.ReadUInt32();
            if (highWords > PersistenceFormat.MaxNestedCount)
            {
                throw new InvalidDataException(
                    $"Filter claims {highWords} words of high bits, beyond anything " +
                    "this library builds.");
            }
            filter.Highs = new ulong[highWords];
            for (var i = 0; i < highWords; i++)
            {
                filter.Highs[i] = reader.ReadUInt64();
            }

            reader.ExpectEnd();

            if (filter.HighBitCount > highWords * 64)
            {
                throw new InvalidDataException(
                    $"Filter claims {filter.HighBitCount} bits of high parts in " +
                    $"{highWords} words, which hold {highWords * 64}.");
            }

            filter.BuildSelectIndex();
            return filter;
        }
    }
}
