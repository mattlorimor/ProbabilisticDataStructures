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
    public class Grafite : IBinaryPersistable<Grafite>, IPayloadBody
    {
        /// <summary>
        /// The Mersenne prime 2^61 - 1, the modulus of the pairwise-independent hash.
        /// Large enough to exceed any reduced universe this builds, and a Mersenne
        /// prime so the reduction needs no division.
        /// </summary>
        private const ulong Prime = (1UL << 61) - 1;

        /// <summary>
        /// The largest reduced universe a filter is built with, 2^61 - 2: the hash
        /// reduces modulo <see cref="Prime"/>, so a universe is no wider than that.
        /// </summary>
        internal const ulong MaxUniverse = Prime - 1;

        /// <summary>
        /// The first universe the hash cannot address, 2^61, as a double. The largest it can
        /// is <see cref="MaxUniverse"/>; as a double the one below 2^61 is 2^61 - 256, so
        /// "less than this" is exactly "at most the cap" for a size that comes from the
        /// build's arithmetic.
        /// </summary>
        private const double FirstUnaddressableUniverse = 2305843009213693952.0;

        /// <summary>
        /// The reduced universe a request wants, n × L / e, before it is rounded up.
        /// </summary>
        internal static double UniverseWanted(ulong keys, ulong maxRangeSize, double falsePositiveRate)
        {
            return keys * (double)maxRangeSize / falsePositiveRate;
        }

        /// <summary>
        /// Whether the hash can address a universe of this size.
        /// </summary>
        internal static bool UniverseFits(double wanted)
        {
            return wanted < FirstUnaddressableUniverse;
        }

        /// <summary>
        /// The largest range size <see cref="Build"/> accepts for this many keys at this
        /// rate, or zero if it accepts none: even a range of one key then wants more
        /// universe than the hash addresses. Found by search against the build's own test
        /// (<see cref="UniverseFits"/>), since the arithmetic is in doubles and the edge
        /// is wherever they put it.
        /// </summary>
        internal static ulong LargestRangeSize(ulong keys, double falsePositiveRate)
        {
            if (!UniverseFits(UniverseWanted(keys, 1, falsePositiveRate)))
            {
                return 0;
            }

            ulong low = 1;
            ulong high = ulong.MaxValue;
            while (low < high)
            {
                var middle = low + ((high - low + 1) / 2);
                if (UniverseFits(UniverseWanted(keys, middle, falsePositiveRate)))
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return low;
        }

        /// <summary>
        /// Refuses a request whose universe the hash cannot address, saying what would work.
        /// </summary>
        private static void RefuseUniverse(
            ulong keys, ulong maxRangeSize, double falsePositiveRate, double wanted)
        {
            var largest = LargestRangeSize(keys, falsePositiveRate);

            if (largest == 0)
            {
                // No range size helps: a range of one key is already too much.
                throw new ArgumentOutOfRangeException(
                    nameof(falsePositiveRate), falsePositiveRate,
                    $"{keys:N0} keys at a false positive rate of {falsePositiveRate:R} need a " +
                    $"reduced universe of about {wanted:E3} even for ranges of one key, and " +
                    $"the hash addresses at most {MaxUniverse:N0}. The finest rate that many " +
                    $"keys can have is a little over {keys / FirstUnaddressableUniverse:E3}. " +
                    "Ask for a coarser rate, or build several filters over parts of the keys.");
            }

            // The rate that would let this range through, if any does: a rate is a
            // probability, so a range so wide that it wants one of 1 or more has no rate.
            var neededRate = keys * (double)maxRangeSize / FirstUnaddressableUniverse;
            var alternative = neededRate < 1
                ? $"A wider range needs a rate above about {neededRate:E3}, or fewer keys."
                : "No rate lets that many keys have a range that wide; use fewer keys.";

            throw new ArgumentOutOfRangeException(
                nameof(maxRangeSize), maxRangeSize,
                $"{keys:N0} keys at a false positive rate of {falsePositiveRate:R} for ranges " +
                $"of {maxRangeSize:N0} need a reduced universe of about {wanted:E3}, and the " +
                $"hash addresses at most {MaxUniverse:N0}. The largest range that works for " +
                $"{keys:N0} keys at that rate is {largest:N0}. {alternative}");
        }

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
        /// The most bits of high parts a filter can have: they are addressed by int
        /// positions, and their count is an int.
        /// </summary>
        /// <remarks>
        /// <see cref="Build"/> refuses a key set that would need more, and the reader
        /// refuses more words of high bits than this many bits fill, so a filter one
        /// builds the other reads. The reader used to stop at a million words of
        /// either kind.
        /// </remarks>
        internal const int MaxHighBits = int.MaxValue;

        /// <summary>
        /// The most words of high bits a filter can have: enough for
        /// <see cref="MaxHighBits"/> bits, as <see cref="Encode"/> sizes them.
        /// </summary>
        internal const uint MaxHighWords = MaxHighBits / 64 + 1;

        /// <summary>
        /// The most words of low bits a filter can have: they are one array.
        /// </summary>
        /// <remarks>
        /// <see cref="Build"/> never comes near this -- its keys are counted in an int
        /// and the low bits per key shrink as the keys grow -- so it needs no check of
        /// its own there. The reader's limit is the array's.
        /// </remarks>
        internal static uint MaxLowWords => (uint)Array.MaxLength;

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
        /// with probability at most l/maxRangeSize of this. See the remarks for when
        /// this cannot be held.
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
        /// <remarks>
        /// <b>The universe has to fit the hash.</b> The keys are hashed into a universe of
        /// n × maxRangeSize / falsePositiveRate, and the hash works modulo the prime
        /// 2^61 - 1, so the universe is at most 2^61 - 2. A request that needs more -- many
        /// keys, a wide range or a very small rate -- is refused, and the message says
        /// what would work: the largest range for that many keys at that rate, or, when
        /// not even a range of one key fits, the finest rate those keys can have, a little
        /// over n / 2^61. The limit is the build's own, so the rate asked for is the rate
        /// <see cref="FalsePositiveRate"/> reports, and not one that was quietly given up.
        /// Earlier releases built such a filter at the largest universe instead, and one
        /// they wrote still reads.
        /// <para>
        /// For four keys at 1%, ranges can be up to about 5.8e15 keys wide; for a million
        /// keys at 1%, about 2.3e10; and a point query, a range of one, works for any
        /// number of keys up to about 2.3e16 at 1%.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The rate is not strictly between zero and one, the range is zero, or the universe
        /// the keys need is past 2^61 - 2: naming <paramref name="maxRangeSize"/> when a
        /// smaller range would work and <paramref name="falsePositiveRate"/> when no range
        /// would.
        /// </exception>
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

            // r = n * L / e, the reduced universe. Computed in floating point, because the
            // product overflows an integer for large inputs long before it stops being a
            // meaningful request. The hash works modulo a prime of 2^61 - 1, so it cannot
            // address a universe past MaxUniverse, and a request that wants one is refused
            // rather than quietly built at the cap, where the rate it delivers is not the
            // rate that was asked for.
            var wanted = UniverseWanted(n, maxRangeSize, falsePositiveRate);
            if (!UniverseFits(wanted))
            {
                RefuseUniverse(n, maxRangeSize, falsePositiveRate, wanted);
            }

            // Under 2^61 as a double means at most 2^61 - 256, so this is within the cap.
            var r = Math.Max((ulong)Math.Ceiling(wanted), 2);
            filter.R = r;

            filter.Fill(distinct, seed);
            return filter;
        }

        /// <summary>
        /// Builds a filter over a universe of exactly the given size, for tests: what an
        /// earlier release built when it capped a request the builder now refuses. The
        /// universe is at most <see cref="MaxUniverse"/>, as every release has held it,
        /// and a filter made this way writes a payload the reader must go on reading.
        /// </summary>
        internal static Grafite BuildAtUniverse(
            IEnumerable<ulong> keys, ulong universe, ulong maxRangeSize, ulong? seed = null)
        {
            var distinct = new SortedSet<ulong>(keys);
            if (distinct.Count == 0 || universe < 2 || universe > MaxUniverse)
            {
                throw new ArgumentException(
                    "A filter at an exact universe needs keys and a universe from 2 to " +
                    $"{MaxUniverse}.", nameof(universe));
            }

            var filter = new Grafite
            {
                KeyCount = (ulong)distinct.Count,
                MaxRange = maxRangeSize,
                R = universe,
            };
            filter.Fill(distinct, seed);
            return filter;
        }

        /// <summary>
        /// Draws the hash and stores the hash codes of the keys, in a filter whose universe
        /// is set.
        /// </summary>
        private void Fill(SortedSet<ulong> distinct, ulong? seed)
        {
            var random = seed is null
                ? SeededRandom.Unpredictable()
                : new SeededRandom(seed.Value);

            // c1 must not be zero, or the hash ignores the block entirely.
            this.C1 = (random.Next() % (Prime - 1)) + 1;
            this.C2 = random.Next() % Prime;

            var codes = new SortedSet<ulong>();
            foreach (var key in distinct)
            {
                codes.Add(this.HashOf(key));
            }

            this.Encode(codes);
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
            this.LowBits = SplitFor(this.R, n);
            var lowMask = this.LowBits == 64 ? ulong.MaxValue : (1UL << this.LowBits) - 1;

            // In a long, and refused past an int: the high bits are addressed by int
            // positions and their count is an int. Summed in an int, this wrapped
            // negative once the keys numbered in the high hundreds of millions.
            var layout = LayoutOf(n, this.LowBits, this.LastCode);
            if (layout.HighBits > MaxHighBits)
            {
                throw new ArgumentException(
                    $"{n} keys over this range need {layout.HighBits} bits of high parts, " +
                    $"past the {MaxHighBits} a filter can address. Build several filters " +
                    "over parts of the key space instead.", "keys");
            }

            this.Lows = new ulong[layout.LowWords];
            this.HighBitCount = (int)layout.HighBits;
            this.Highs = new ulong[layout.HighWords];

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
        /// Where <see cref="Encode"/> splits this many codes over a universe of this size,
        /// in low bits: where the high parts are about as numerous as the codes.
        /// </summary>
        internal static int SplitFor(ulong universe, int codeCount)
        {
            return Math.Max(0, BitOperations.Log2(universe / (ulong)codeCount));
        }

        /// <summary>
        /// The widest split any filter has, which is also the widest the reader takes: the
        /// largest universe over the fewest codes. Worked from <see cref="SplitFor"/>, the
        /// split the builder makes, so that nothing it builds is refused and nothing it
        /// could not build is read. It is below a code's width, 64, which is what makes a
        /// shift by the split a shift.
        /// </summary>
        internal static int MaxLowBits => SplitFor(MaxUniverse, 1);

        /// <summary>
        /// What <see cref="Encode"/> makes of this many codes, the largest of them given,
        /// split at this many low bits: the bits of high parts in use, and the words of low
        /// bits and of high bits that hold them. The reader asks the same question of the
        /// codes a payload says it holds, and refuses a payload that answers it differently,
        /// so a count the codes do not give is refused rather than guessed at. A filter of
        /// no codes is not encoded at all (<see cref="Build"/> leaves it as it is) and has
        /// none of these.
        /// </summary>
        /// <remarks>
        /// In 64 bits, so that none of it wraps: the largest code is below the universe,
        /// which is below 2^61, so its high part and the sum of that with the codes are
        /// well inside a long. Whether the bits fit an int is the caller's to ask. The split
        /// is at most <see cref="MaxLowBits"/>, so shifting by it is a shift.
        /// </remarks>
        internal static (long HighBits, long LowWords, long HighWords) LayoutOf(
            int codeCount, int lowBits, ulong lastCode)
        {
            if (codeCount == 0)
            {
                return (0, 0, 0);
            }

            var highPart = lastCode >> lowBits;
            var highBits = (long)highPart + codeCount + 1;
            return (highBits, ((long)codeCount * lowBits / 64) + 2, (highBits / 64) + 1);
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
            return SizeOfWords(this.Lows.Length, this.Highs.Length);
        }

        /// <summary>
        /// The bytes in this many words of low bits and of high bits. A function of its
        /// own so that a test can reach the sizes where the arithmetic matters, which are
        /// filters of gigabytes.
        /// </summary>
        internal static ulong SizeOfWords(int lowWords, int highWords)
        {
            // Each in 64 bits before they are added: two arrays of a billion words are
            // sixteen gigabytes, and an int holds neither the sum nor the product.
            return (((ulong)lowWords) + (ulong)highWords) * sizeof(ulong);
        }

        /// <summary>
        /// The false positive rate the filter delivers for a range of the given size: at
        /// most n × rangeSize / r for n keys and a reduced universe of r, and never more
        /// than 1.
        /// </summary>
        /// <remarks>
        /// For a range up to the one the filter was built for this is the rate it was
        /// built for, to within the rounding of the universe: a request whose universe
        /// the hash cannot address is refused (see <see cref="Build"/>) and not built at
        /// something smaller. A range wider than that was built for has a proportionally
        /// higher rate, up to 1.
        /// </remarks>
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
        public void WriteTo(Stream stream) => PersistenceFormat.Write(stream, this);

        EnvelopeHeader IPayloadBody.WritePayload(PayloadWriter payload)
        {
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

            // Both arrays' words, counted from the codes as the reader expects them.
            var layout = LayoutOf(this.CodeCount, this.LowBits, this.LastCode);
            payload.WriteUInt32((uint)layout.LowWords);
            if (payload.BeginBlock(layout.LowWords * sizeof(ulong)))
            {
                foreach (var word in this.Lows)
                {
                    payload.WriteUInt64(word);
                }
            }
            payload.WriteUInt32((uint)layout.HighWords);
            if (payload.BeginBlock(layout.HighWords * sizeof(ulong)))
            {
                foreach (var word in this.Highs)
                {
                    payload.WriteUInt64(word);
                }
            }

            return new(StructureId.Grafite, HashId.None);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The filter that was written.</returns>
        public static Grafite ReadFrom(Stream stream)
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
        public static Grafite ReadFrom(Stream stream, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, null, options);
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
        public static Grafite ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, hash, options);
        }

        private static Grafite Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.Grafite);
            return PersistenceFormat.Read(
                stream, StructureId.Grafite, budget, Price, hash, CheckHash,
                ReadPayload);
        }

        /// <summary>
        /// Reads the payload of the envelope <c>Read</c> opened, once the envelope has
        /// been checked. See <see cref="PayloadParse{T}"/>.
        /// </summary>
        private static Grafite ReadPayload(
            ref PayloadReader reader, ushort version, HashId hashId,
            Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            var budget = reader.Budget;

            var r = reader.ReadUInt64();
            var c1 = reader.ReadUInt64();
            var c2 = reader.ReadUInt64();
            var keyCount = reader.ReadUInt64();
            var maxRange = reader.ReadUInt64();
            // The split and the bit count stay unsigned until they are checked: from 2^31
            // up they are negative as ints, and the limits on them are ones a negative
            // number passes.
            var lowBits = reader.ReadUInt32();
            var codeCount = (int)reader.ReadUInt32();
            var highBitCount = reader.ReadUInt32();
            var firstCode = reader.ReadUInt64();
            var lastCode = reader.ReadUInt64();
            var layout = CheckFields(
                r, c1, c2, keyCount, lowBits, codeCount, highBitCount, firstCode, lastCode);

            var filter = new Grafite
            {
                R = r,
                C1 = c1,
                C2 = c2,
                KeyCount = keyCount,
                MaxRange = maxRange,
                LowBits = (int)lowBits,
                CodeCount = codeCount,
                HighBitCount = (int)highBitCount,
                FirstCode = firstCode,
                LastCode = lastCode,
            };

            // The builder's own limits, so that every filter it builds reads back. Both
            // arrays are written whole, so each is checked against the bytes behind it
            // before it is allocated, and against the size the codes give it.
            var lowWords = ReadLowWords(ref reader, layout.LowWords);
            budget.Charge(LowsCost(lowWords));
            filter.Lows = new ulong[lowWords];
            for (var i = 0; i < lowWords; i++)
            {
                filter.Lows[i] = reader.ReadUInt64();
            }

            var highWords = ReadHighWords(ref reader, layout.HighWords);
            // And the select index built over them below, a count per word.
            budget.Charge(HighsCost(highWords));
            filter.Highs = new ulong[highWords];
            for (var i = 0; i < highWords; i++)
            {
                filter.Highs[i] = reader.ReadUInt64();
            }

            reader.ExpectEnd();

            filter.BuildSelectIndex();
            filter.CheckEnds();
            return filter;
        }

        /// <summary>
        /// Refuses a filter whose recorded smallest and largest codes are not the first and
        /// last of the codes it encodes. A query turns away anything outside them before it
        /// looks at the encoding, so an end that is not the encoding's loses the keys
        /// between the two, or records a code the filter does not hold.
        /// </summary>
        /// <remarks>
        /// Here and not in <see cref="Price"/>, which prices the words and skips them: the
        /// two ends are found by selecting in the high bits, which needs the words held and
        /// the index over them built, and a price that built them would be the read.
        /// </remarks>
        private void CheckEnds()
        {
            if (this.CodeCount == 0)
            {
                return;
            }

            var first = CodeAt(0);
            if (first != this.FirstCode)
            {
                throw new InvalidDataException(
                    $"Filter records its smallest code as {this.FirstCode}, but the encoding " +
                    $"it holds begins with {first}.");
            }

            var last = CodeAt(this.CodeCount - 1);
            if (last != this.LastCode)
            {
                throw new InvalidDataException(
                    $"Filter records its largest code as {this.LastCode}, but the encoding " +
                    $"it holds ends with {last}.");
            }
        }

        /// <summary>
        /// Refuses a supplied hash, and a payload naming any hash but none: this
        /// structure hashes nothing. See <see cref="HashCheck"/>.
        /// </summary>
        private static void CheckHash(HashId recorded, Func<ReadOnlySpan<byte>, ulong>? supplied)
        {
            if (supplied is not null)
            {
                throw new InvalidDataException(
                    "A Grafite filter holds numbers rather than bytes -- a range is a " +
                    "question about order -- so it cannot be read with a supplied " +
                    "hash function. Read it with the overload that takes none.");
            }
            if (recorded != HashId.None)
            {
                throw new InvalidDataException(
                    $"Payload names hash function {(ushort)recorded}, and a Grafite " +
                    "filter does not hash bytes. It was not written by this structure.");
            }
        }

        /// <summary>
        /// Prices what <see cref="Read"/> allocates beyond the envelope: the words of low
        /// bits, the words of high bits, and the select index over them. See
        /// <see cref="PayloadPrice"/>.
        /// </summary>
        internal static void Price(ref PayloadReader reader, ushort version)
        {
            var r = reader.ReadUInt64();
            var c1 = reader.ReadUInt64();
            var c2 = reader.ReadUInt64();
            var keyCount = reader.ReadUInt64();
            _ = reader.ReadUInt64();
            var lowBits = reader.ReadUInt32();
            var codeCount = (int)reader.ReadUInt32();
            var highBitCount = reader.ReadUInt32();
            var firstCode = reader.ReadUInt64();
            var lastCode = reader.ReadUInt64();
            var layout = CheckFields(
                r, c1, c2, keyCount, lowBits, codeCount, highBitCount, firstCode, lastCode);

            var lowWords = ReadLowWords(ref reader, layout.LowWords);
            reader.Budget.Price(LowsCost(lowWords));
            reader.Skip(lowWords * sizeof(ulong));

            reader.Budget.Price(HighsCost(ReadHighWords(ref reader, layout.HighWords)));
        }

        /// <summary>
        /// Checks the fields a payload leads with, as both the read and its price do,
        /// in the same order, before anything is allocated for the words that follow.
        /// Returns the words the codes give each array, which the counts that follow them
        /// are held to.
        /// </summary>
        /// <remarks>
        /// The split and the count of bits of high parts arrive as the unsigned numbers
        /// they are stored as, since from 2^31 up they are negative once they are ints.
        /// </remarks>
        private static (long LowWords, long HighWords) CheckFields(
            ulong r, ulong c1, ulong c2, ulong keyCount, uint lowBits, int codeCount,
            uint highBitCount, ulong firstCode, ulong lastCode)
        {
            if (r < 1)
            {
                throw new InvalidDataException(
                    "Filter claims a reduced universe of nothing, which every key " +
                    "would hash into and no range could be tested against.");
            }
            // The builder's own limit, so that nothing it refuses reads. Every release has
            // capped the universe here, so a payload written for a request that is now
            // refused carries exactly this, and reads.
            if (r > MaxUniverse)
            {
                throw new InvalidDataException(
                    $"Filter claims a reduced universe of {r}, beyond anything this " +
                    $"library builds: the hash works modulo 2^61 - 1, so it is at most " +
                    $"{MaxUniverse}.");
            }
            if (c1 == 0 || c1 >= Prime || c2 >= Prime)
            {
                throw new InvalidDataException(
                    "Filter carries hash parameters this library never draws: the " +
                    "multiplier must be non-zero and both must fall below the modulus.");
            }
            if (lowBits > 64)
            {
                throw new InvalidDataException(
                    $"Filter splits its codes at {lowBits} low bits, and a " +
                    "code is sixty-four bits wide.");
            }
            // The builder's own limit again: its universe is below 2^61, so it never splits
            // wider than this, and a wider split is read as a shift that is not the one meant.
            if (lowBits > MaxLowBits)
            {
                throw new InvalidDataException(
                    $"Filter splits its codes at {lowBits} low bits, beyond anything this " +
                    $"library builds: a universe is at most {MaxUniverse}, so the widest " +
                    $"split is {MaxLowBits}.");
            }
            if (codeCount < 0 || (ulong)codeCount > keyCount)
            {
                throw new InvalidDataException(
                    $"Filter holds {codeCount} hash codes for " +
                    $"{keyCount} keys, and hashing never produces more codes " +
                    "than the keys it was given.");
            }
            if (codeCount > 0 && firstCode > lastCode)
            {
                throw new InvalidDataException(
                    $"Filter's smallest code {firstCode} exceeds its largest " +
                    $"{lastCode}.");
            }
            if (codeCount > 0 && lastCode >= r)
            {
                throw new InvalidDataException(
                    $"Filter holds the code {lastCode}, outside its own " +
                    $"reduced universe of {r}.");
            }
            if (highBitCount > MaxHighBits)
            {
                throw new InvalidDataException(
                    $"Filter claims {highBitCount} bits of high parts, past the " +
                    $"{MaxHighBits} a filter can address.");
            }

            // What the builder sets from the codes, as it sets it: a payload that says
            // otherwise is not one it wrote, and what it answers is not the keys' answers.
            if (codeCount == 0 && (lowBits != 0 || highBitCount != 0))
            {
                throw new InvalidDataException(
                    $"Filter holds no hash codes, yet claims a split at {lowBits} low bits " +
                    $"and {highBitCount} bits of high parts. A filter of no keys has neither.");
            }
            var layout = LayoutOf(codeCount, (int)lowBits, lastCode);
            if (highBitCount != layout.HighBits)
            {
                throw new InvalidDataException(
                    $"Filter claims {highBitCount} bits of high parts, but its {codeCount} " +
                    $"hash codes, split at {lowBits} low bits and ending at {lastCode}, " +
                    $"take exactly {layout.HighBits}.");
            }

            return (layout.LowWords, layout.HighWords);
        }

        private static uint ReadLowWords(ref PayloadReader reader, long expected)
        {
            var lowWords = reader.ReadUInt32();
            if (lowWords > MaxLowWords)
            {
                throw new InvalidDataException(
                    $"Filter claims {lowWords} words of low bits, beyond anything " +
                    $"this library builds: they are one array, of at most {MaxLowWords}.");
            }
            reader.RequireHolds(lowWords, sizeof(ulong), "words of low bits");
            if (lowWords != expected)
            {
                throw new InvalidDataException(
                    $"Filter claims {lowWords} words of low bits, but the hash codes it " +
                    $"holds, at the split it claims, fill exactly {expected}.");
            }
            return lowWords;
        }

        private static uint ReadHighWords(ref PayloadReader reader, long expected)
        {
            var highWords = reader.ReadUInt32();
            if (highWords > MaxHighWords)
            {
                throw new InvalidDataException(
                    $"Filter claims {highWords} words of high bits, beyond anything " +
                    $"this library builds: its high parts fit in {MaxHighWords}.");
            }
            reader.RequireHolds(highWords, sizeof(ulong), "words of high bits");
            if (highWords != expected)
            {
                throw new InvalidDataException(
                    $"Filter claims {highWords} words of high bits, but the bits of high " +
                    $"parts it claims fill exactly {expected}.");
            }
            return highWords;
        }

        /// <summary>The words of low bits.</summary>
        private static long LowsCost(uint lowWords) => ReadBudget.ArrayOf(lowWords, sizeof(ulong));

        /// <summary>The words of high bits, and the select index's count per word.</summary>
        private static long HighsCost(uint highWords) => ReadBudget.Sum(
            ReadBudget.ArrayOf(highWords, sizeof(ulong)),
            ReadBudget.ArrayOf((ulong)highWords + 1, sizeof(int)));
    }
}
