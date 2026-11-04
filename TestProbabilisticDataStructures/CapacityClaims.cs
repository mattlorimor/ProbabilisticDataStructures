using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;
using static TestProbabilisticDataStructures.CapacityModel;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// A sentence in the prose of CAPACITY.md that states a number or a rule, and the
    /// check that it is true.
    /// </summary>
    /// <param name="Phrase">The words as the document has them. The document must contain them exactly.</param>
    /// <param name="Holds">Whether the library does what the words say.</param>
    internal sealed record Claim(string Phrase, Func<bool> Holds);

    /// <summary>
    /// Everything CAPACITY.md says in words that is a figure and not a heading or a
    /// transition. The table's cells are held to the library by the rows; these are the
    /// formulas, the rules of thumb and the defects the prose gives beside them.
    /// </summary>
    /// <remarks>
    /// Some read the fits the rows were worked out with, so they are evaluated after the
    /// rows are, which is also why a rule of thumb in the prose is the slope the numbers
    /// came from and not a second measurement of it.
    /// </remarks>
    internal static class CapacityClaims
    {
        private static double Slope(string line) => ChargeLine.Known[line].Slope;

        private static bool Near(double actual, double expected, double within = 0.0001) =>
            Math.Abs(actual - expected) <= within;

        private static bool Rounds(double value, double to, int decimals) =>
            Math.Round(value, decimals, MidpointRounding.AwayFromZero) == to;

        private static double BitsPerItem(double fp) => (double)Utils.OptimalM(1_000_000, fp) / 1_000_000;

        private static int BucketBytes(object filter)
        {
            var buckets = (filter.GetType().GetProperty("Buckets", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? filter.GetType().GetProperty("cells", BindingFlags.NonPublic | BindingFlags.Instance))!
                .GetValue(filter)!;
            return ((byte[])typeof(Buckets)
                .GetProperty("Data", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(buckets)!).Length;
        }

        private static bool Fails(Action<byte[]> add) =>
            Enumerable.Range(0, 100).Any(i =>
            {
                try
                {
                    add(Key(i));
                    return false;
                }
                catch (Exception)
                {
                    return true;
                }
            });

        private static byte[] Key(long i) => BitConverter.GetBytes(i);

        /// <summary>
        /// What <paramref name="build"/> allocates on this thread for each unit of its
        /// argument, from the difference between a small and a large one, each the least of
        /// three: a collection landing mid-build only ever adds, as <c>TestReadBudget</c>
        /// measures a read.
        /// </summary>
        private static double AllocatedPer(Func<int, object> build, int small, int large)
        {
            long Least(int size)
            {
                var least = long.MaxValue;
                for (var i = 0; i < 3; i++)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var built = build(size);
                    var after = GC.GetAllocatedBytesForCurrentThread();
                    GC.KeepAlive(built);
                    least = Math.Min(least, after - before);
                }

                return least;
            }

            return (double)(Least(large) - Least(small)) / (large - small);
        }

        /// <summary>
        /// Whether a scalable filter that has <paramref name="filters"/> filters cannot build
        /// the next, and for the reason of this kind: the exception its sizing has inside.
        /// </summary>
        private static bool CeilingIs(uint hint, double fp, double r, uint growth, int filters, Type inner)
        {
            var scalable = new ScalableBloomFilter(hint, fp, r, growth);
            scalable.Filters.Clear();
            for (var i = 0; i < filters; i++) scalable.Filters.Add(CapacityRows.Placeholder);
            var refused = Assert.Throws<InvalidOperationException>(
                () => scalable.NextCapacity(fp * Math.Pow(r, filters)));
            return refused.InnerException is not null && inner.IsInstanceOfType(refused.InnerException);
        }

        /// <summary>
        /// The payload of a sketch whose rows are <see cref="WideFrom"/> columns wide, worked
        /// out from the payloads of two narrow ones of the same depth: what a column of its
        /// rows costs, from their difference, and what does not grow with the width. A
        /// payload is exact in the width, so nothing here is rounded.
        /// </summary>
        /// <param name="build">A sketch about as wide as asked, its width and its bytes.</param>
        private static long PayloadAtWideFrom(Func<uint, (uint Width, byte[] Bytes)> build)
        {
            var (narrow, narrowBytes) = build(1000);
            var (wider, widerBytes) = build(3000);
            var grown = widerBytes.Length - narrowBytes.Length;
            var perColumn = grown / (long)(wider - narrow);
            Assert.AreEqual(grown, perColumn * (wider - narrow), "a column of the sketch is not a whole number of bytes");
            return narrowBytes.Length - EnvelopeBytes() + (perColumn * ((long)WideFrom - narrow));
        }

        private static long CountMinAtWideFrom(int depth) => PayloadAtWideFrom(w =>
        {
            // The rows are ceil(ln(1 / delta)): a delta of 0.5 is one, and of 0.2 two.
            var sketch = new CountMinSketch(Math.E / w, depth == 1 ? 0.5 : 0.2);
            Assert.AreEqual((uint)depth, sketch.Depth);
            return (sketch.Width, sketch.ToByteArray());
        });

        private static long PrivateAtWideFrom(uint depth) => PayloadAtWideFrom(w =>
            (w, new PrivateCountMinSketch(w, depth, 1.0, seed: 1).ToByteArray()));

        private static long KeeperAtWideFrom(uint? depth) => PayloadAtWideFrom(w =>
        {
            var keeper = depth is null ? new HeavyKeeper(5, w) : new HeavyKeeper(5, w, depth.Value);
            return (w, keeper.ToByteArray());
        });

        private static readonly MethodInfo CuckooWords = typeof(CuckooBloomFilter)
            .GetMethod("WordsFor", BindingFlags.NonPublic | BindingFlags.Static)!;

        /// <summary>The 64-bit words a cuckoo filter packs this many entries of this width in: its own sizing.</summary>
        private static ulong WordsFor(ulong entries, uint bits) =>
            (ulong)CuckooWords.Invoke(null, new object[] { entries, bits })!;

        /// <summary>
        /// The bits from which a new 32-bit filter places with 64-bit values, written out
        /// rather than read from the library, which is what the claims about it check.
        /// </summary>
        private const ulong WideFrom = 1UL << 27;

        /// <summary>
        /// The most items a 1% filter is sized for while it is still short of
        /// <see cref="WideFrom"/> bits.
        /// </summary>
        private static ulong LastNarrow() =>
            Largest(1, CapacityRows.LargestSized(0.01), n => Utils.OptimalM((uint)n, 0.01) < WideFrom);

        /// <summary>
        /// How much more than an even placement the 32-bit fold gives a filter of
        /// <paramref name="m"/> bits sized for <paramref name="fp"/>, as a fraction of the
        /// rate: the first <c>2^32 mod m</c> positions take <c>floor(2^32 / m) + 1</c> of the
        /// 2^32 values and the rest one fewer, with an item's positions treated as
        /// independent. This is the model the study in <c>Benchmarks/BloomPlacementStudy.cs</c>
        /// prints beside each measurement, and it predicted every one to within it.
        /// </summary>
        private static double FoldExcess(ulong m, double fp)
        {
            const double TwoTo32 = 4294967296.0;
            var k = Utils.OptimalK(fp);
            var inserts = m * Math.Log(2) * Math.Log(2) / Math.Abs(Math.Log(fp)) * k;
            double Set(double p) => 1 - Math.Exp(inserts * Math.Log(1 - p));

            var q = (1UL << 32) / m;
            var r = (1UL << 32) % m;
            var heavy = (q + 1) / TwoTo32;
            var light = q / TwoTo32;
            var folded = Math.Pow(r * heavy * Set(heavy) + (m - r) * light * Set(light), k);
            return folded / Math.Pow(Set(1.0 / m), k) - 1;
        }

        /// <summary>
        /// The largest <see cref="FoldExcess"/> at 1% of any size below
        /// <see cref="WideFrom"/>, over the teeth of the sawtooth from the one just below it
        /// down: each tooth is the sizes sharing <c>floor(2^32 / m)</c>, and they fall
        /// below it, so the first is where the largest is.
        /// </summary>
        private static double LargestFoldBelowWide()
        {
            var largest = 0.0;
            for (ulong q = 32; q <= 64; q++)
            {
                var low = (1UL << 32) / (q + 1) + 1;
                var high = Math.Min((1UL << 32) / q, WideFrom - 1);
                for (var step = 0; step <= 200; step++)
                {
                    largest = Math.Max(largest, FoldExcess(low + (high - low) * (ulong)step / 200, 0.01));
                }
            }

            return largest;
        }

        /// <summary>
        /// How far short of the truth, as a fraction of it, a SetSketch of
        /// <paramref name="m"/> registers holding many more elements than registers
        /// estimates cardinality, when each element's first value goes to a register drawn
        /// with the 32-bit multiply-and-shift.
        /// </summary>
        /// <remarks>
        /// The model is the one the measurement for #203 used. Of the m registers,
        /// r = 2^32 mod m take first values at (1 + (m - r) / 2^32) / m and the rest at
        /// (1 - r / 2^32) / m. Once the sketch is full a register's value is set
        /// by the first values it takes, and the closed-form estimator returns the truth
        /// times the harmonic mean of the rates over their mean. Worked through, that falls
        /// short by exactly r (m - r) / (2^32 (2^32 + m - 2r)), which is what this computes,
        /// so that nothing cancels at a small m: the leading term is r (m - r) / 2^64.
        /// </remarks>
        private static double ShuffleShortfall(ulong m)
        {
            const double TwoTo32 = 4294967296.0;
            var r = (1UL << 32) % m;
            return (double)r * (m - r) / (TwoTo32 * (TwoTo32 + m - (2.0 * r)));
        }

        private static readonly double TwoTo64 = Math.ScaleB(1.0, 64);
        private static readonly double TwoTo66 = Math.ScaleB(1.0, 66);

        /// <summary>
        /// The shortfall at every register count up to 2^24: the largest it comes to against
        /// the standard error 1 / √m, the farthest it strays from its leading term
        /// r (m - r) / 2^64, and the largest it comes to against (m / 2^33)².
        /// </summary>
        private static readonly Lazy<(double AgainstError, double FromLeading, double AgainstBound)> ShortfallUpTo2To24 = new(() =>
        {
            double againstError = 0, fromLeading = 0, againstBound = 0;
            for (ulong m = 1; m <= 1UL << 24; m++)
            {
                var r = (1UL << 32) % m;
                var shortfall = ShuffleShortfall(m);
                againstError = Math.Max(againstError, shortfall * Math.Sqrt(m));
                againstBound = Math.Max(againstBound, shortfall * TwoTo66 / ((double)m * m));
                if (r != 0)
                {
                    fromLeading = Math.Max(fromLeading, Math.Abs((shortfall * TwoTo64 / ((double)r * (m - r))) - 1));
                }
            }

            return (againstError, fromLeading, againstBound);
        });

        /// <summary>
        /// The largest shortfall of any register count, where it is, and the largest it comes
        /// to against (m / 2^33)². Each tooth of the sawtooth is the counts sharing
        /// floor(2^32 / m), sampled at 2,001 points, from the top one down to 2^32 / 65;
        /// below that the shortfall is at most (m / 2^33)² / (1 - m / 2^32), under a
        /// hundredth of a percent. Around 0.4 · 2^32, where the top tooth peaks, every count
        /// is taken.
        /// </summary>
        private static readonly Lazy<(double Largest, ulong At, double AgainstBound)> ShortfallTeeth = new(() =>
        {
            double largest = 0, againstBound = 0;
            ulong at = 0;
            void See(ulong m)
            {
                var shortfall = ShuffleShortfall(m);
                if (shortfall > largest)
                {
                    largest = shortfall;
                    at = m;
                }

                againstBound = Math.Max(againstBound, shortfall * TwoTo66 / ((double)m * m));
            }

            for (ulong q = 2; q <= 64; q++)
            {
                var low = ((1UL << 32) / (q + 1)) + 1;
                var high = Math.Min((1UL << 32) / q, (ulong)SetSketch.MaxRegisters);
                for (ulong step = 0; step <= 2000; step++)
                {
                    See(low + ((high - low) * step / 2000));
                }
            }

            var peak = (ulong)Math.Round(0.4 * 4294967296.0);
            for (var m = peak - 2000; m <= peak + 2000; m++)
            {
                See(m);
            }

            return (largest, at, againstBound);
        });

        /// <summary>
        /// Sentences of CAPACITY.md that state what was measured at a scale the suite cannot
        /// build, each with the reason it cannot, which is checked. The figures themselves
        /// are the measurement's; what is checked is that they could not have been rebuilt
        /// here.
        /// </summary>
        internal static IReadOnlyList<(string Phrase, string Why, Func<bool> WhyHolds)> Measured() => new (string, string, Func<bool>)[]
        {
            ("a filter sized for 1% measured 1.419% ± 0.006% at m = 0.75 · 2^32 bits",
                "it is the rate of a filter holding more than 300 million items, and a measurement of a rate needs as many probes again",
                () => Utils.OptimalK(0.01) == 7 && 0.75 * 4_294_967_296.0 / BitsPerItem(0.01) > 300_000_000),
            ("and sized for 0.1% it measured 0.173%",
                "it is the rate of a filter holding more than 200 million items, and a measurement of a rate that small needs ten times as many probes",
                () => Utils.OptimalK(0.001) == 10 && 0.75 * 4_294_967_296.0 / BitsPerItem(0.001) > 200_000_000),
            ("At m = 0.75 · 2^32 sized for 1% it measured 1.004%, as `BloomFilter64` did",
                "it is the rate of a filter holding more than 300 million items, as the measurement before the change was",
                () => Utils.OptimalK(0.01) == 7 && 0.75 * 4_294_967_296.0 / BitsPerItem(0.01) > 300_000_000),
            ("too small to measure",
                "the largest excess below 2^27 is at a filter of more than 13 million items, and telling it from no excess at 1% takes more than a billion probes of each",
                () => LastNarrow() > 13_000_000
                    && 9 * 0.99 / (0.01 * Math.Pow(LargestFoldBelowWide(), 2)) > 1_000_000_000),
            ("overestimated past about 143 million distinct items, by +3.2% at 2^28 and +13% at 2^30, and returned 0 from about 5.7 billion (2^32.4)",
                "they describe the estimator before its large-range correction was removed, which the library no longer has: " +
                "a state past the old threshold now counts its raw estimate, and a full sketch its ceiling rather than 0",
                () =>
                {
                    var oldBand = new HyperLogLog(16);
                    Array.Fill(oldBand.RegisterState, (byte)27);
                    return oldBand.Count() == (ulong)(0.673 * 16 * Math.Pow(2, 27))
                        && 0.673 * 16 * Math.Pow(2, 27) > Math.Pow(2, 32) / 30
                        && FullHyperLogLog(1024).Count() > 0;
                }),
            ("A scaled-down simulation of the draw reproduced them, found the shortfall smaller in a sketch holding " +
                "fewer elements, and found no change in Jaccard similarity",
                "it drew with 16 bits, not 32, at 26,214 and 6,241 registers, whose first values then go at the two " +
                "rates the 32-bit draw gives 1,717,960,704 and 409,010,176 registers. A sketch is built with 10 bytes " +
                "a register, so those are sketches of 17 and 4 gigabytes, and a measurement takes hundreds of them. " +
                "The library's draw cannot be narrowed to run the same thing smaller",
                () => new ulong[] { 26_214, 6241 }.All(m => (1UL << 32) % (m << 16) == ((1UL << 16) % m) << 16)
                    && (26_214UL << 16) == 1_717_960_704 && (6241UL << 16) == 409_010_176
                    && (6241UL << 16) * (sizeof(ushort) + (2 * sizeof(uint))) > 4_000_000_000),
        };

        // ---- HyperLogLog ----------------------------------------------------------------

        /// <summary>A sketch whose every register is capped, built through the update Add uses.</summary>
        private static HyperLogLog FullHyperLogLog(uint m)
        {
            var q = 32 - BitOperations.Log2(m);
            var full = new HyperLogLog(m);
            for (uint j = 0; j < m; j++)
            {
                full.AddHash(m == 1 ? 0u : j << q);
            }

            return full;
        }

        /// <summary>
        /// The relative error of a HyperLogLog of <paramref name="m"/> registers at
        /// <paramref name="n"/> distinct items, over Poisson-sampled sketches built through
        /// the update hook, as <see cref="TestHyperLogLogRange"/> builds them: the mean, its
        /// standard error, and the spread as a multiple of the nominal 1.04/sqrt(m).
        /// </summary>
        private static (double Mean, double StandardError, double Ratio) HyperLogLogError(
            uint m, int trials, double n, int seed)
        {
            var random = new Random(seed);
            var errors = new double[trials];
            for (var t = 0; t < trials; t++)
            {
                errors[t] = (TestHyperLogLogRange.PoissonSketch(m, n, random).Count() - n) / n;
            }

            var mean = errors.Average();
            var spread = Math.Sqrt(errors.Sum(e => (e - mean) * (e - mean)) / (trials - 1));
            return (mean, spread / Math.Sqrt(trials), spread / (1.04 / Math.Sqrt(m)));
        }

        /// <summary>
        /// The raw estimate alpha m^2 / sum of 2^-M_j for a register state, as the paper writes
        /// it: a capped register counts as its plain 2^-M, which is what the library's
        /// correction for the cap moves by a part in a million at the sizes used here.
        /// </summary>
        private static double RawEstimate(byte[] registers, double alpha)
        {
            var sum = 0.0;
            foreach (var register in registers)
            {
                sum += Math.Pow(2.0, -register);
            }

            return alpha * registers.Length * (double)registers.Length / sum;
        }

        private static HyperLogLog ExpectedHyperLogLog(uint m, double lambda)
        {
            var hll = new HyperLogLog(m);
            HyperLogLogBias.FillExpected(hll.RegisterState, lambda, 32 - BitOperations.Log2(m));
            return hll;
        }

        /// <summary>
        /// The mean relative error of sketches of real keys, <paramref name="trials"/> streams
        /// of λm distinct items each, keyed by trial, and its standard error.
        /// </summary>
        private static (double Mean, double StandardError) RealHyperLogLogError(uint m, double lambda, int trials)
        {
            var n = (int)(lambda * m);
            var errors = new double[trials];
            for (var t = 0; t < trials; t++)
            {
                var hll = new HyperLogLog(m);
                for (var i = 0; i < n; i++)
                {
                    hll.Add(Encoding.ASCII.GetBytes($"t{t}-item-{i}"));
                }

                errors[t] = (hll.Count() - (double)n) / n;
            }

            var mean = errors.Average();
            var spread = Math.Sqrt(errors.Sum(e => (e - mean) * (e - mean)) / (trials - 1));
            return (mean, spread / Math.Sqrt(trials));
        }

        private static readonly MethodInfo SublimeLimit = typeof(SublimeCountMinSketch)
            .GetMethod("LimitFor", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static ulong DoublesAt(SublimeCountMinSketch sketch, int width) =>
            (ulong)SublimeLimit.Invoke(sketch, new object[] { width })!;

        internal static IReadOnlyList<Claim> All() => new[]
        {
            // ---- Reading the numbers --------------------------------------------------
            new Claim("2,147,483,591 elements", () => Array.MaxLength == 2_147_483_591),
            new Claim("1,073,741,824 bytes", () => ReadOptions.DefaultMemoryBudget == 1_073_741_824),
            new Claim("commit 426348d", () => true),
            new Claim("so the payload is at most 2,147,483,573 bytes: `Array.MaxLength` less the 18 bytes of envelope", () =>
                MaxPayload == 2_147_483_573 && EnvelopeBytes() == 18 && Array.MaxLength - EnvelopeBytes() == MaxPayload),
            new Claim("A structure is written into one `byte[]`", () =>
                typeof(PayloadWriter).GetField("buffer", BindingFlags.NonPublic | BindingFlags.Instance)!.FieldType == typeof(byte[])),
            new Claim("A read is charged at least twice the payload", () =>
            {
                // Every payload line the rows fitted, against the charge line it was fitted with.
                var pairs = ChargeLine.Known
                    .Where(k => k.Key.StartsWith("payload of ", StringComparison.Ordinal) && k.Value.Slope > 0)
                    .Select(k => (Payload: k.Value.Slope, Charge: ChargeLine.Known[k.Key["payload of ".Length..]].Slope))
                    .ToArray();
                return pairs.Length > 10 && pairs.All(p => p.Charge >= 1.95 * p.Payload);
            }),
            // ---- How the 32-bit filters place their bits ------------------------------
            new Claim("position `i` was `(low + i · high) mod m`, with `low` and `high` the hash's two 32-bit halves and the sum taken in 32-bit arithmetic", () =>
                Enumerable.Range(0, 50).All(t =>
                {
                    // Sizes in the band where the fold is largest, so a placement that did
                    // not wrap at 2^32 would come out different.
                    var key = Key(t);
                    var m = 3_000_000_000u + 7_919u * (uint)t;
                    var sum = Defaults.GetDefaultHashFunction()(key);
                    var positions = Positions.Of(key, Defaults.GetDefaultHashFunction(), Placement.Narrow, m);
                    return Enumerable.Range(0, 7).All(i =>
                        positions[(uint)i] == unchecked((uint)sum + (uint)(sum >> 32) * (uint)i) % m);
                })),
            // The rule, and a new filter built either side of it: 16 MiB of bits each.
            new Claim("a new filter whose positions are reduced modulo 2^27 or more", () =>
                BitPlacement.WideFrom == WideFrom
                && BitPlacement.For(WideFrom - 1) == Placement.Narrow
                && BitPlacement.For(WideFrom) == Placement.Wide
                && new BloomFilter((uint)LastNarrow(), 0.01).Placement == Placement.Narrow
                && new BloomFilter((uint)LastNarrow() + 1, 0.01).Placement == Placement.Wide),
            new Claim("16 MiB of bits", () => WideFrom / 8 == 16 * 1024 * 1024),
            new Claim("about 14 million items at 1%", () =>
                Rounds(LastNarrow() / 1e6, 14, 0)
                && Utils.OptimalM((uint)LastNarrow() + 1, 0.01) >= WideFrom),
            new Claim("and for the partitioned filter each partition", () =>
            {
                // One hash function, so one partition, of the threshold exactly or more;
                // then two, in a filter past the threshold whose partitions are not.
                var one = Largest(1, CapacityRows.LargestSized(0.5), n => Utils.OptimalM((uint)n, 0.5) < WideFrom) + 1;
                var single = new PartitionedBloomFilter((uint)one, 0.5).Placement;
                var two = new PartitionedBloomFilter(
                    (uint)Largest(1, CapacityRows.LargestSized(0.3), n => Utils.OptimalM((uint)n, 0.3) < WideFrom * 19 / 10), 0.3);
                return single == Placement.Wide
                    && two.K() == 2 && two.Capacity() > WideFrom && two.Partitions[0].count < WideFrom
                    && two.Placement == Placement.Narrow;
            }),
            new Claim("places them with 64-bit values, exactly as `BloomFilter64` does", () =>
            {
                var wide = new BloomFilter(500, 0.01, null, Placement.Wide);
                var reference = new BloomFilter64(500, 0.01);
                for (var i = 0; i < 500; i++)
                {
                    wide.Add(Key(i));
                    reference.Add(Key(i));
                }

                return reference.Capacity() == wide.Capacity()
                    && Enumerable.Range(0, (int)wide.Capacity())
                        .All(b => wide.Buckets.Get((uint)b) == reference.Buckets.Get((ulong)b));
            }),
            // 6.2.0 wrote versions up to 4, the second SetSketch construction's, and refuses
            // any above the highest it wrote.
            new Claim("A smaller filter is placed as before", () =>
                new BloomFilter(100, 0.01).Placement == Placement.Narrow
                && new CountingBloomFilter(100, 4, 0.01).Placement == Placement.Narrow
                && new DeletableBloomFilter(100, 10, 0.01).Placement == Placement.Narrow
                && new PartitionedBloomFilter(100, 0.01).Placement == Placement.Narrow
                && new StableBloomFilter(100, 2, 0.01).Placement == Placement.Narrow),
            new Claim("Below 2^27 the excess is at most about 0.07% of the rate at 1%", () =>
                Rounds(LargestFoldBelowWide() * 100, 0.07, 2)),
            // Small and large alike: a small filter of each of the five, placed either way.
            new Claim("Every save of these filters, whatever its size, is written at format version 8", () =>
                new[] { Placement.Narrow, Placement.Wide }.All(p =>
                    new BloomFilter(100, 0.01, null, p).ToByteArray()[4] == 8
                    && new CountingBloomFilter(100, 4, 0.01, null, p).ToByteArray()[4] == 8
                    && new DeletableBloomFilter(100, 10, 0.01, null, p).ToByteArray()[4] == 8
                    && new PartitionedBloomFilter(100, 0.01, null, p).ToByteArray()[4] == 8
                    && new StableBloomFilter(100, 2, 0.01, null, 1, p).ToByteArray()[4] == 8)),
            // The plain filter's layout: u32 m, u32 k, the placement byte, the u64 count,
            // then the bucket array, whose count follows.
            new Claim("which records the placement in a byte of its own and the item count in 64 bits", () =>
                new[] { (Placement.Narrow, 0), (Placement.Wide, 1) }.All(p =>
                {
                    var filter = new BloomFilter(100, 0.01, null, p.Item1);
                    for (var i = 0; i < 10; i++) filter.Add(Key(i));
                    var bytes = filter.ToByteArray();
                    return bytes[14 + 8] == p.Item2
                        && System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(14 + 9)) == 10
                        && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(14 + 17)) == filter.Capacity();
                })),
            // 6.2.0 wrote versions up to 4, the second SetSketch construction's, and refuses
            // any above the highest it wrote.
            new Claim("and which 6.2.0 and earlier refuse", () =>
                PersistenceFormat.PlacementVersion == 8
                && PersistenceFormat.SetSketchVariantVersion == 4
                && new[] { PersistenceFormat.RandomStateVersion, PersistenceFormat.CuckooPackedVersion }
                    .All(v => v < PersistenceFormat.SetSketchVariantVersion)),
            // A version 1 payload, the plain filter's earlier layout: no placement byte and a
            // u32 count. It reads placed with 32-bit values, and saved again says so.
            new Claim("keeps the 32-bit placement, and the rate that goes with it, whatever its size, and records it when saved again", () =>
            {
                var old = TestBitPlacement.Downgraded(new BloomFilter(100, 0.01).ToByteArray(), 1, 8, 9);
                var read = Persistence.FromByteArray<BloomFilter>(old);
                var again = read.ToByteArray();
                return System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(old.AsSpan(4)) == 1
                    && read.Placement == Placement.Narrow
                    && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(again.AsSpan(4)) == 8 && again[14 + 8] == 0;
            }),
            new Claim("does not merge with a new filter of the same size", () =>
            {
                var old = new BloomFilter(100, 0.01, null, Placement.Narrow);
                var fresh = new BloomFilter(100, 0.01, null, Placement.Wide);
                try
                {
                    old.Merge(fresh);
                    return false;
                }
                catch (ArgumentException)
                {
                    return true;
                }
            }),
            // ---- How the sketches place their columns -----------------------------------
            new Claim("reducing a 32-bit value modulo the width", () =>
                Enumerable.Range(0, 50).All(t =>
                {
                    // Widths in the band where the fold is largest, so a reduction that
                    // did not wrap at 2^32 would come out different.
                    var key = Key(t);
                    var w = 1_500_000_000u + 7_919u * (uint)t;
                    var hash = Defaults.GetDefaultHashFunction();
                    var sum = hash(key);
                    var columns = CountMinSketch.ColumnsOf(key, hash, Placement.Narrow, w);
                    var buckets = HeavyKeeper.BucketsOf(sum, hash, Placement.Narrow, w);
                    return Enumerable.Range(0, 5).All(i =>
                            columns[(uint)i] == unchecked((uint)sum + (uint)(sum >> 32) * (uint)i) % w
                            && PrivateCountMinSketch.ColumnsOf(key, hash, Placement.Narrow, w)[(uint)i] == columns[(uint)i]
                            && buckets[(uint)i] == columns[(uint)i])
                        && InverseBloomFilter.SlotOf(sum, Placement.Narrow, w) == (uint)sum % w;
                })),
            // floor(2^32 / w) just past 2^27, at 2^29.8, and from a third of 2^32 to the
            // widest an array holds.
            new Claim("about 3% more just past 2^27 columns, a quarter more at 2^29.8, and half as much again from a third of 2^32 to the widest row", () =>
                Rounds(100.0 / ((1UL << 32) / ((1UL << 27) + 1)), 3, 0)
                && (ulong)(4294967296.0 / Math.Pow(2, 29.8)) == 4
                && (1UL << 32) / ((1UL << 32) / 3 + 1) == 2
                && (1UL << 32) / (ulong)Array.MaxLength == 2),
            // Each structure's own choice, either side of the threshold. Built for real
            // there it is a gigabyte, which TestSketchPlacementAtTheThreshold does.
            new Claim("a new one 2^27 columns, buckets or slots wide or wider places with 64-bit values", () =>
                TestSketchPlacement.Kinds().All(k =>
                    k.PlacementFor((uint)WideFrom) == Placement.Wide
                    && k.PlacementFor((uint)Array.MaxLength) == Placement.Wide)),
            new Claim("and a narrower one is placed as before", () =>
                TestSketchPlacement.Kinds().All(k =>
                    k.PlacementFor((uint)WideFrom - 1) == Placement.Narrow
                    && k.PlacementOf(k.Build(null)) == Placement.Narrow)),
            // The width is ceil(e / ε), so it reaches 2^27 at ε = e / 2^27.
            new Claim("A count-min sketch is that wide from an `ε` of about 2.03e-8", () =>
                Rounds(Math.E / WideFrom * 1e8, 2.03, 2)),
            // Eight bytes a counter, and a read charges the payload and the row.
            new Claim("a row of it is a gigabyte, twice that to read, so it reads back only with a budget raised to match", () =>
                WideFrom * sizeof(ulong) == 1UL << 30
                && Near(Slope("CountMinSketch"), 16, 0.01)
                && Slope("CountMinSketch") * WideFrom > ReadOptions.DefaultMemoryBudget),
            // Each worked out from two narrow sketches' payloads and carried to 2^27 columns,
            // with one row beside it, which does fit.
            new Claim("Two rows of it cannot be saved at all: they are a payload of 2,147,483,681 bytes, past the most a payload can be", () =>
                CountMinAtWideFrom(2) == 2_147_483_681 && CountMinAtWideFrom(2) > MaxPayload
                && CountMinAtWideFrom(1) <= MaxPayload),
            new Claim("Two rows of a `PrivateCountMinSketch` that wide are 2,147,483,673 bytes", () =>
                PrivateAtWideFrom(2) == 2_147_483_673 && PrivateAtWideFrom(2) > MaxPayload
                && PrivateAtWideFrom(1) <= MaxPayload),
            new Claim("and two of a `HeavyKeeper`, its default depth, 2,684,354,601", () =>
                new HeavyKeeper(5, 64).Depth == 2
                && KeeperAtWideFrom(null) == 2_684_354_601 && KeeperAtWideFrom(null) > MaxPayload
                && KeeperAtWideFrom(1) <= MaxPayload),
            new Claim("A `TopK` writes its count-min sketch inside its own payload", () =>
                new TopK(0.001, 0.01, 10).ToByteArray().Length > new CountMinSketch(0.001, 0.01).ToByteArray().Length),
            // A window's payload carries a body for every sketch it holds, each a private
            // sketch's payload: two of one row at 2^27 columns already pass the most.
            new Claim("two of the private sketches a `DpswSketch` holds are 2,147,483,698 bytes at that width before anything else it writes", () =>
            {
                var window = new DpswSketch(2000, 1.0, width: 64, depth: 1, seed: 1);
                for (var i = 0; i < 3000; i++) window.Add(Key(i % 500));
                var body = new PrivateCountMinSketch(64, 1, 1.0, seed: 1).ToByteArray().Length - EnvelopeBytes();
                return 2 * PrivateAtWideFrom(1) == 2_147_483_698 && 2 * PrivateAtWideFrom(1) > MaxPayload
                    && window.SketchesHeld >= 2
                    && window.ToByteArray().Length - EnvelopeBytes() >= (long)window.SketchesHeld * body;
            }),
            // A sketch places with 64-bit values only from 2^27 columns, where two rows of any
            // of them pass the most a payload holds and one does not.
            new Claim("So a sketch placed with 64-bit values saves with one row or not at all, and a `DpswSketch` only while it holds one sketch", () =>
                TestSketchPlacement.Kinds().Where(k => k.Name != nameof(InverseBloomFilter))
                    .All(k => k.PlacementFor((uint)WideFrom - 1) == Placement.Narrow)
                && new[] { CountMinAtWideFrom(2), PrivateAtWideFrom(2), KeeperAtWideFrom(2), 2 * PrivateAtWideFrom(1) }
                    .All(two => two > MaxPayload)
                && new[] { CountMinAtWideFrom(1), PrivateAtWideFrom(1), KeeperAtWideFrom(1) }.All(one => one <= MaxPayload)),
            new Claim("and in a sketch's payload that byte is 1 only beside a single row", () =>
                CountMinSketch.PlacementFor((uint)WideFrom - 1) == Placement.Narrow
                && PrivateCountMinSketch.PlacementFor((uint)WideFrom - 1) == Placement.Narrow
                && HeavyKeeper.PlacementFor((uint)WideFrom - 1) == Placement.Narrow
                && CountMinAtWideFrom(2) > MaxPayload && PrivateAtWideFrom(2) > MaxPayload && KeeperAtWideFrom(2) > MaxPayload),
            new Claim("Every save of these is written at format version 9", () =>
                TestSketchPlacement.Kinds().All(k =>
                    new Placement?[] { null, Placement.Narrow, Placement.Wide }.All(p => k.Write(k.Build(p))[4] == 9))
                && new DpswSketch(128, 1.0, width: 8, depth: 2, seed: 1).Add(Key(1)).ToByteArray()[4] == 9),
            // A version 1 payload, the count-min sketch's earlier layout: no placement
            // byte. It reads placed with 32-bit values, and saved again says so.
            new Claim("keeps the 32-bit placement whatever its width, and records it when saved again", () =>
            {
                var old = TestSketchPlacement.Downgraded(new CountMinSketch(0.01, 0.01).ToByteArray(), 24);
                var read = Persistence.FromByteArray<CountMinSketch>(old);
                var again = read.ToByteArray();
                return System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(old.AsSpan(4)) == 1
                    && read.Placement == Placement.Narrow
                    && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(again.AsSpan(4)) == 9 && again[14 + 24] == 0;
            }),
            new Claim("a count-min sketch read so does not merge with a new one of the same width", () =>
            {
                var old = new CountMinSketch(0.01, 0.01, null, Placement.Narrow);
                var fresh = new CountMinSketch(0.01, 0.01, null, Placement.Wide);
                try
                {
                    old.Merge(fresh);
                    return false;
                }
                catch (ArgumentException)
                {
                    return true;
                }
            }),
            new Claim("a slot that holds an item is written as an index, a length and the item", () =>
                Near(Facts["inverse slot bytes"], 16, 0.01)),

            // ---- Where a limit refuses -----------------------------------------------------
            new Claim("4,294,967,182 four-bit counters, or 2,147,483,591 eight-bit ones", () =>
                Buckets.MaxCount(4) == 4_294_967_182 && Buckets.MaxCount(8) == 2_147_483_591
                && Buckets.MaxCount(1) == uint.MaxValue && Buckets.MaxCount(3) == uint.MaxValue),
            new Claim("and the filters refuse more against the parameter that chose it", () =>
            {
                var n = TestBucketSizes.FirstItemCountSizedAbove(Buckets.MaxCount(8), 0.01);
                return Assert.Throws<ArgumentOutOfRangeException>(() => new CountingBloomFilter(n, 8, 0.01)).ParamName == "n"
                    && Assert.Throws<ArgumentOutOfRangeException>(
                        () => new StableBloomFilter(Buckets.MaxCount(8) + 1, 8, 0.01, seed: 1)).ParamName == "m";
            }),
            new Claim("the `Add` that would double it past that throws, with the filter as it was", () =>
            {
                var filter = new InfiniFilter(initialCapacity: 3, fingerprintBits: 8) { QuotientBitsLimit = 2 };
                for (var i = 0; i < 3; i++) filter.Add(Key(i));
                var before = filter.ToByteArray();
                var memento = new MementoFilter(2, fingerprintBits: 8, initialCapacity: 3) { QuotientBitsLimit = 2 };
                for (ulong i = 0; i < 3; i++) memento.Add(i * 1_000);
                var mementoBefore = memento.ToByteArray();
                return InfiniFilter.MaxQuotientBits == 31 && MementoFilter.MaxQuotientBits == 31
                    && Assert.Throws<InvalidOperationException>(() => filter.Add(Key(3))) is not null
                    && Assert.Throws<InvalidOperationException>(() => memento.Add(3_000)) is not null
                    && before.AsSpan().SequenceEqual(filter.ToByteArray())
                    && mementoBefore.AsSpan().SequenceEqual(memento.ToByteArray());
            }),
            new Claim("or 2^30 for a Memento slot of 64 bits", () =>
                MementoFilter.QuotientBitsLimitFor(MementoSegment.MaxSlotBits) == 30
                && new MementoFilter(1UL << 32, 28, initialCapacity: 4).QuotientBitsLimit == 30
                && new MementoFilter(1UL << 32, 27, initialCapacity: 4).QuotientBitsLimit == 31
                && MementoFilter.InitialQuotientBits(uint.MaxValue, MementoSegment.MaxSlotBits) == 30),
            new Claim("2^31 slots of 64 bits are more words than an array holds, so a table of them stops at 2^30 slots", () =>
                MementoSegment.Words(1u << 31, 64) > Array.MaxLength
                && MementoSegment.Words(1u << 31, 63) <= Array.MaxLength
                && MementoSegment.Words(1u << 30, 64) <= Array.MaxLength
                && Enumerable.Range(1, 63).All(bits => MementoFilter.QuotientBitsLimitFor(bits) == 31)
                && MementoFilter.QuotientBitsLimitFor(64) == 30),
            new Claim("so it cannot pass 2^30, and an `Add` that would double it past that throws, with the sketch as it was", () =>
            {
                var sketch = new SublimeCountMinSketch(0.5, 0.5, 1.0);
                typeof(SublimeCountMinSketch).GetField("width", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(sketch, 1 << 30);
                typeof(SublimeCountMinSketch).GetField("expansionLimit", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(sketch, 1UL);
                return typeof(SublimeCountMinSketch).GetField("width", BindingFlags.NonPublic | BindingFlags.Instance)!.FieldType == typeof(int)
                    && SublimeCountMinSketch.MaxWidth == 1 << 30
                    && Assert.Throws<InvalidOperationException>(() => sketch.Add(Key(1))) is not null
                    && sketch.TotalCount() == 0;
            }),
            new Claim("the constructor refuses a fingerprint and a range that make it wider", () =>
            {
                var widest = new MementoFilter(1UL << 32, 28);
                for (ulong i = 0; i < 200; i++) widest.Add(i * 7919);
                return Enumerable.Range(0, 200).All(i => widest.Test((ulong)i * 7919))
                    && Assert.Throws<ArgumentOutOfRangeException>(() => new MementoFilter(1UL << 32, 29)).ParamName == "fingerprintBits";
            }),
            // A structure whose WriteTo counts its payload first (#216), made to claim a shape
            // past the ceiling, is refused naming the whole payload and the ceiling before
            // anything is built for it, and writes what it wrote before once the shape is put
            // back; TestPayloadCount holds every structure with a body to being counted by its
            // WriteTo. The buffer's growth is what refuses a payload of a writer that does not
            // count yet.
            new Claim("`WriteTo` of one throws an `InvalidOperationException` naming the size it would be and the size one can be, with the structure unchanged", () =>
            {
                var countSketch = new CountSketch(0.5, 0.5);
                TestPayloadCount.AssertRefusedWhileClaiming(countSketch, 4_000_000_012,
                    (countSketch, "width", 100_000_000u), (countSketch, "depth", 5u));
                var table = new InvertibleBloomLookupTable(4, 4);
                TestPayloadCount.AssertRefusedWhileClaiming(table, 6_000_000_020, (table, "cells", 300_000_000u));
                var setSketch = new SetSketch(8);
                TestPayloadCount.AssertRefusedWhileClaiming(setSketch, 4_294_967_206, (setSketch, "m", Array.MaxLength));

                // 2^31 slots of five bits, what a filter for 1.6 billion items at 4% is.
                var quotient = new QuotientFilter(100, 0.01);
                TestPayloadCount.AssertRefusedWhileClaiming(quotient, 2_147_483_680,
                    (quotient, "quotientBits", 31u), (quotient, "remainderBits", 5u));

                return Assert.Throws<InvalidOperationException>(
                    () => PayloadWriter.GrownCapacity(1000, int.MaxValue - 1, 10)) is not null;
            }),

            // ---- BloomFilter -------------------------------------------------------------
            new Claim("`m ≈ 1.44 · n · log2(1/fp)` bits", () =>
                new[] { 0.01, 0.001, 0.0001 }.All(fp =>
                    Math.Abs(BitsPerItem(fp) / (1.44 * Math.Log2(1 / fp)) - 1) < 0.01)),
            new Claim("9.6 bits per item at 1%, 14.4 at 0.1%", () =>
                Rounds(BitsPerItem(0.01), 9.6, 1) && Rounds(BitsPerItem(0.001), 14.4, 1)),
            new Claim("7 hash functions at 1% and 10 at 0.1%", () =>
                Utils.OptimalK(0.01) == 7 && Utils.OptimalK(0.001) == 10),
            new Claim("refuses a filter of more than 4,294,967,295 bits", () =>
                uint.MaxValue == 4_294_967_295
                && CapacityRows.LargestSized(0.01) == 448_089_842
                && Utils.OptimalM(448_089_842, 0.01) <= uint.MaxValue
                && Assert.Throws<ArgumentOutOfRangeException>(() => Utils.OptimalM(448_089_843, 0.01)) is not null),
            new Claim("which is 448,089,842 items at 1%", () => CapacityRows.LargestSized(0.01) == 448_089_842),
            new Claim("about 537 megabytes", () => Rounds(ReadOptions.DefaultMemoryBudget / 2 / 1e6, 537, 0)),

            // ---- BloomFilter64 ------------------------------------------------------------
            new Claim("arrays of 2^30 bytes", () => Buckets64.maxArraySize == 1u << 30),
            new Claim("so what stops the constructor is memory, or 2.3e18 bits", () =>
                Rounds(Buckets64.MaxCount / 1e18, 2.3, 1)),
            // The arrays are held in an array, so there are at most Array.MaxLength of them;
            // the first item count past it is refused before anything is allocated.
            new Claim("it refuses a filter of more than 2,305,842,948,010,409,983 bits, about 288 petabytes, naming `n`", () =>
            {
                var most = Largest(1, 1UL << 60, n => Utils.OptimalM64(n, 0.01) <= Buckets64.MaxCount);
                return Buckets64.MaxCount == 2_305_842_948_010_409_983
                    && Buckets64.MaxCount == (ulong)Array.MaxLength * Buckets64.maxArraySize - 1
                    && Rounds(Buckets64.MaxCount / 8.0 / 1e15, 288, 0)
                    && Assert.Throws<ArgumentOutOfRangeException>(() => new BloomFilter64(most + 1, 0.01)).ParamName == "n";
            }),
            new Claim("about 1.2 bytes an item at 1%", () =>
                Rounds((double)Utils.OptimalM64(1_000_000_000, 0.01) / 8 / 1_000_000_000, 1.2, 1)),
            new Claim("at most 2,147,483,573 bytes, which is about 1.79 billion items at 1%", () =>
            {
                const double fp = 0.01;
                var small = new BloomFilter64(100_000, fp);
                var payload = small.ToByteArray().Length - EnvelopeBytes();
                var fixedBytes = payload - (long)TrueBytes(small.Capacity(), 1) - 4;
                var most = Largest(1, 1UL << 40, n =>
                {
                    var m = Utils.OptimalM64(n, fp);
                    return fixedBytes + (long)TrueBytes(m, 1) + 4 * (long)(m / Buckets64.maxArraySize + 1)
                        <= MaxPayload;
                });
                return MaxPayload == 2_147_483_573 && About(most, most, 2) == "≈ 1.79 billion";
            }),

            // ---- PartitionedBloomFilter ---------------------------------------------------
            new Claim("each `m / k` bits", () =>
            {
                var filter = new PartitionedBloomFilter(100_000, 0.01);
                return filter.PartitionCount == Utils.OptimalK(0.01)
                    && filter.Partitions.All(p => p.count == (uint)Math.Ceiling((double)filter.BitCount / filter.PartitionCount));
            }),

            // ---- CuckooBloomFilter --------------------------------------------------------
            new Claim("`n / 3.8` buckets", () => Near(4 * CapacityRows.CuckooLoadFactor, 3.8)),
            new Claim("10 at 1% and 13 at 0.1%", () =>
                CapacityRows.CuckooBits(0.01) == 10 && CapacityRows.CuckooBits(0.001) == 13),
            new Claim("A bucket therefore costs 5.5 bytes at 1%", () => Near(Slope("CuckooBloomFilter 1%"), 5.5, 0.01)),
            new Claim("is written and read at 10 bytes", () => Near(Facts["cuckoo entry bytes 1%"], 10, 0.01)),
            // A rate of 2^-60 is 63 bits exactly, log2(8 / 2^-60); just below it is 64.
            new Claim("That holds up to fingerprints of 63 bits, whose 2^31 entries are 2,113,929,217 words, which one array holds", () =>
                CapacityRows.CuckooBits(Math.ScaleB(1.0, -60)) == 63
                && WordsFor(CuckooBloomFilter.MaxEntries, 63) == 2_113_929_217
                && WordsFor(CuckooBloomFilter.MaxEntries, 63) <= (ulong)Array.MaxLength
                && CuckooBloomFilter.GeometryProblem(CuckooBloomFilter.MaxEntries / 4, 4, 63) is null
                && CapacityRows.CuckooLargest(Math.ScaleB(1.0, -60)) == CapacityRows.CuckooLargest(0.01)),
            new Claim("A rate below about 8.7e-19 has fingerprints of 64 bits, all the hash supplies", () =>
                CapacityRows.CuckooBits(Math.ScaleB(1.0, -60) * (1 - 1e-9)) == 64
                && CapacityRows.CuckooBits(1e-300) == 64
                && Rounds(Math.ScaleB(1.0, -60) * 1e19, 8.7, 1)),
            new Claim("and then 2^31 entries are 2,147,483,649 words, more than one array holds, so the table stops at 2^30 entries " +
                "and `n` at 1,020,054,732, half the table's figure", () =>
                WordsFor(CuckooBloomFilter.MaxEntries, 64) == 2_147_483_649
                && WordsFor(CuckooBloomFilter.MaxEntries, 64) > (ulong)Array.MaxLength
                && CuckooBloomFilter.GeometryProblem(CuckooBloomFilter.MaxEntries / 4, 4, 64) is not null
                && CuckooBloomFilter.GeometryProblem(CuckooBloomFilter.MaxEntries / 8, 4, 64) is null
                && CapacityRows.CuckooLargest(1e-19) == 1_020_054_732
                && CapacityRows.CuckooLargest(0.01) / 2 == 1_020_054_732),

            // ---- QuotientFilter -----------------------------------------------------------
            new Claim("(7 at 1%, 10 at 0.1%)", () =>
                QuotientFilter.Split(1000, 0.01).RemainderBits == 7 && QuotientFilter.Split(1000, 0.001).RemainderBits == 10),
            new Claim("a rate no finer than 2^-33", () =>
                QuotientFilter.Split(QuotientFilter.MaxItems, Math.ScaleB(1.0, -33)).RemainderBits == 33
                && Assert.Throws<ArgumentOutOfRangeException>(
                    () => QuotientFilter.Split(QuotientFilter.MaxItems, Math.ScaleB(1.0, -34))) is not null),
            new Claim("A read is charged three times the table", () => Near(Slope("QuotientFilter 1%"), 24, 0.01)),

            // ---- BinaryFuseFilter and BloomierFilter ----------------------------------------
            new Claim("about 1.126 cells per key", () => Rounds((double)CapacityRows.FuseCells(100_000_000) / 100_000_000, 1.126, 3)),
            new Claim("(a false positive rate of 0.39%)", () => Rounds(100 * Math.Pow(2, -8), 0.39, 2)),
            new Claim("or 2 bytes (0.0015%)", () => Rounds(100 * Math.Pow(2, -16), 0.0015, 4)),
            new Claim("about 2.25 bytes a key for 8-bit fingerprints", () =>
                Rounds(2.0 * CapacityRows.FuseCells(100_000_000) / 100_000_000, 2.25, 2)),
            new Claim("2 bytes at `v = 8`, and 6 at `v = 40`", () => (8 + 8 + 7) / 8 == 2 && (8 + 40 + 7) / 8 == 6),
            new Claim("puts the hash of every item it is given into one list, duplicates and all, and holds " +
                "that list and a sorted copy of it, at least 16 bytes an item, before it can refuse", () =>
            {
                // One item given many times is one key, and the build's memory follows the items.
                var item = new byte[] { 2, 1, 9 };
                return BinaryFuseFilter.Build(Enumerable.Repeat(item, 1000), BinaryFuseWidth.Eight, null).Count() == 1
                    && AllocatedPer(n => BinaryFuseFilter.Build(Enumerable.Repeat(item, n), BinaryFuseWidth.Eight, null),
                        100_000, 1_100_000) >= 16;
            }),
            new Claim("That is 30.5 GB at the 1,908,641,337 keys of the 8-bit limit", () =>
                BinaryFuseFilter.MaxKeys(BinaryFuseWidth.Eight) == 1_908_641_337
                && Rounds(16.0 * BinaryFuseFilter.MaxKeys(BinaryFuseWidth.Eight) / 1e9, 30.5, 1)),
            new Claim("a list holds at most 2,147,483,591 hashes, so a build given more items than that fails " +
                "with an `OutOfMemoryException`, however few of them are distinct", () =>
                // The list's growth asks for an array one longer than the longest there can be,
                // which is refused before anything is allocated. Not run at the scale itself,
                // which is 17 GB of hashes.
                Array.MaxLength == 2_147_483_591
                && Assert.Throws<OutOfMemoryException>(() => new ulong[Array.MaxLength + 1L]) is not null),

            // ---- Grafite ------------------------------------------------------------------
            new Claim("and between 2 and 3 for the high parts", () =>
                new[] { (0.01, 64UL), (0.001, 64UL), (0.01, 1024UL) }.All(s =>
                {
                    var perKey = (double)CapacityRows.GrafiteMostHighBits(10_000_000, s.Item1, s.Item2, out _) / 10_000_000;
                    return perKey >= 2 && perKey < 3;
                })),
            new Claim("at most 2,147,483,647 bits of them", () => Grafite.MaxHighBits == 2_147_483_647),
            new Claim("the distinct keys in one sorted set and their codes in another, 48 bytes a key each, " +
                "about 96 between them, before the high parts are counted", () =>
            {
                // A node of a sorted set is 48 bytes, and the build allocates at least two a key.
                var keys = Enumerable.Range(0, 1_100_000).Select(i => (ulong)i * 1009).ToArray();
                var node = AllocatedPer(n =>
                {
                    var set = new SortedSet<ulong>();
                    for (var i = 0; i < n; i++) set.Add(keys[i]);
                    return set;
                }, 100_000, 1_100_000);
                var build = AllocatedPer(n => Grafite.Build(keys.Take(n), 0.01, 64, seed: 7), 100_000, 1_100_000);
                return Near(node, 48, 0.01) && build >= 2 * node;
            }),
            new Claim("At the 838 million keys of the worst case that is 80 GB", () =>
            {
                var most = Largest(1, 1UL << 40, n =>
                    CapacityRows.GrafiteMostHighBits(n, 0.01, 64, out _) <= Grafite.MaxHighBits);
                return Rounds(most / 1e6, 838, 0) && Rounds(96.0 * most / 1e9, 80, 0);
            }),
            // Built at two settings, the second the finer of the sentence after this one.
            new Claim("The low parts are `⌊log2(L/fp)⌋` bits a key and are saved beside the high ones, eight bytes a word", () =>
                new[] { (0.01, 64UL), (1e-5, 1024UL) }.All(s =>
                {
                    var filter = Grafite.Build(Enumerable.Range(0, 50_000).Select(i => (ulong)i * 1009), s.Item1, s.Item2, seed: 7);
                    var payload = filter.ToByteArray().Length - EnvelopeBytes();
                    return filter.LowBits == (int)Math.Floor(Math.Log2(s.Item2 / s.Item1))
                        && payload >= 8L * (filter.Lows.Length + filter.Highs.Length);
                })),
            new Claim("800 million keys over ranges up to 1,024 at a rate of 1e-5 are 26 low bits a key, 325,000,002 words and 2.6 GB of them", () =>
            {
                const ulong Keys = 800_000_000;
                CapacityRows.GrafiteMostHighBits(Keys, 1e-5, 1024, out var low);
                var words = (Keys * (ulong)low / 64) + 2;
                return low == 26 && words == 325_000_002
                    && (long)words * 8 > MaxPayload && Rounds(words * 8 / 1e9, 2.6, 1)
                    && Grafite.UniverseFits(Grafite.UniverseWanted(Keys, 1024, 1e-5));
            }),
            new Claim("while their high parts, about 2.02e9 bits, are within what an `int` addresses", () =>
            {
                var high = CapacityRows.GrafiteMostHighBits(800_000_000, 1e-5, 1024, out _);
                return Rounds(high / 1e9, 2.02, 2) && high <= Grafite.MaxHighBits;
            }),

            // ---- InvertibleBloomLookupTable -----------------------------------------------
            new Claim("It has 1.5 cells for each", () => InvertibleBloomLookupTable.CellsPerDifference == 1.5),

            // ---- ScalableBloomFilter -------------------------------------------------------
            new Claim("A growth factor of 2 is what a filter built without one takes", () =>
                ScalableBloomFilter.DefaultGrowth == 2),
            new Claim("so a `hint` of more items than one can be sized for at the rate, 448,089,842 at 1%, is refused by the " +
                "constructor with an `ArgumentOutOfRangeException` naming `hint` and the largest that builds", () =>
            {
                var refused = Assert.Throws<ArgumentOutOfRangeException>(() => new ScalableBloomFilter(448_089_843, 0.01, 0.8));
                return CapacityRows.LargestSized(0.01) == 448_089_842
                    && refused.ParamName == "hint"
                    && refused.Message.Contains("448,089,842", StringComparison.Ordinal);
            }),
            new Claim("a payload written before the factor existed reads back with a growth factor of 1", () =>
            {
                // A filter that does not grow its filters writes the layout from before the factor,
                // which carries none, and reads back as one that does not grow them.
                var old = Persistence.FromByteArray<ScalableBloomFilter>(new ScalableBloomFilter(1000, 0.01, 0.8, 1).ToByteArray());
                var grown = Persistence.FromByteArray<ScalableBloomFilter>(new ScalableBloomFilter(1000, 0.01, 0.8, 2).ToByteArray());
                return old.Growth == 1 && grown.Growth == 2;
            }),
            new Claim("each contained filter is sized for `growth` times as many items as the last", () =>
            {
                var scalable = new ScalableBloomFilter(1000, 0.01, 0.8, 3);
                scalable.Filters.Clear();
                scalable.Filters.Add(CapacityRows.Placeholder);
                scalable.Filters.Add(CapacityRows.Placeholder);
                return scalable.NextCapacity(0.01 * 0.64) == 9000;
            }),
            new Claim("it comes after about 3,160 filters", () =>
            {
                // Where the rate underflows is a matter of the last digits of a power of a
                // number near the smallest double, so it is allowed a few filters either way.
                var shape = CapacityRows.ShapeOf(1000, 0.01, 0.8, 1);
                return Math.Abs(shape.Count - 3160) <= 5 && CeilingIs(1000, 0.01, 0.8, 1, shape.Count, typeof(OverflowException));
            }),
            new Claim("the next filter would need more bits than a contained filter addresses", () =>
            {
                var shape = CapacityRows.ShapeOf(1000, 0.01, 0.8, 2);
                return CeilingIs(1000, 0.01, 0.8, 2, shape.Count, typeof(ArgumentOutOfRangeException));
            }),
            new Claim("and the `Add` that needs it throws `InvalidOperationException` with the filter as it was", () =>
            {
                var scalable = new ScalableBloomFilter(1000, 0.01, 0.8, 1);
                scalable.Filters.Clear();
                var shape = CapacityRows.ShapeOf(1000, 0.01, 0.8, 1);
                for (var i = 0; i < shape.Count; i++) scalable.Filters.Add(CapacityRows.Placeholder);
                return Assert.Throws<InvalidOperationException>(
                    () => scalable.NextCapacity(0.01 * Math.Pow(0.8, shape.Count))) is not null && scalable.Filters.Count == shape.Count;
            }),
            new Claim("Reading costs about four times the bytes of the filters", () =>
                Rounds(Facts["scalable bytes ratio 1000 0.01 0.8"], 4, 0)),

            // ---- InfiniFilter and MementoFilter --------------------------------------------
            new Claim("`F + 4` bits", () => ChargeLine.Known.ContainsKey("InfiniFilter 8")),
            new Claim("`F + 1 + m + 3` bits", () => ChargeLine.Known.ContainsKey("MementoFilter 256")),

            // ---- Inverse and the retaining sketches -----------------------------------------
            new Claim("reads back at 8 bytes a slot, and a full one at 56", () =>
                Near(Slope("InverseBloomFilter"), 8, 0.01) && Near(Slope("InverseBloomFilter full"), 56, 0.01)),
            new Claim("is written holding up to 2k - 1 values", () => true),
            new Claim("A read is charged 16 bytes a value", () => Near(Slope("MinHashSignature"), 16, 0.01)),
            new Claim("depth `⌈ln(1/δ)⌉`", () => new CountMinSketch(0.01, 0.01).Depth == 5),
            new Claim("Its five regions are `k + 1` slots each", () => Near(Slope("VarOpt empty"), 40, 0.01)),
            new Claim("allocated whole when the sample is made, before it holds anything, at 40 bytes for each " +
                "unit of `k`", () => Near(AllocatedPer(k => new VarOpt((uint)k, 1), 100_000, 1_100_000), 40, 0.01)),
            new Claim("80 GiB at the largest `k`, 2,147,483,590", () =>
                VarOpt.MaxK == 2_147_483_590 && Rounds(40.0 * (VarOpt.MaxK + 1L) / (1L << 30), 80, 0)),

            // ---- HyperLogLog and the estimators ---------------------------------------------
            new Claim("so a register records a run of at most 32 − log2 m zeros", () =>
                // An all-zero remainder is the longest run there is, and the register keeps it plus one.
                new[] { 0, 4, 10, 14, 20 }.All(b =>
                {
                    var hll = new HyperLogLog(1u << b);
                    hll.AddHash(0);
                    return hll.RegisterState[0] == 32 - b + 1;
                })),
            new Claim("One hash value in 2^32 caps any given register, so after n distinct items each register has " +
                "reached that cap with probability 1 − e^(−n/2^32), whatever m is", () =>
                // Exhaustively, at register counts small enough in remainder to enumerate: of the 2^q
                // remainders that land on register 0, exactly one caps it, and it is picked with
                // probability 2^-b, so a hash caps a given register with probability 2^-32 at every m.
                // After n independent hashes that is 1 - (1 - 2^-32)^n, which is the exponential to
                // within a part in a billion even at 2^33.
                new[] { 16, 20, 24 }.All(b =>
                {
                    var q = 32 - b;
                    var hll = new HyperLogLog(1u << b);
                    for (var remainder = (1u << q) - 1; remainder >= 1; remainder--)
                    {
                        hll.AddHash(remainder);
                    }

                    var belowCap = hll.RegisterState[0] <= q;
                    hll.AddHash(0);
                    return belowCap && hll.RegisterState[0] == q + 1;
                })
                && Math.Abs(Math.Pow(1 - Math.ScaleB(1.0, -32), Math.ScaleB(1.0, 33)) - Math.Exp(-2)) < 1e-9),
            new Claim("A register holds what a wider hash would have given it, cut off at the cap", () =>
            {
                // A 64-bit value's low 32 bits are what the sketch sees. A wider hash would carry the
                // run on into the other 32; the register must be that run, capped at q + 1, which is
                // why two items whose 32 bits collide move it alike unless the run reaches the cap.
                var random = new Random(178);
                var bytes = new byte[8];
                var capped = 0;
                var uncapped = 0;
                foreach (var b in new[] { 4, 10, 14 })
                {
                    var m = 1u << b;
                    var q = 32 - b;
                    var hll = new HyperLogLog(m);
                    for (var i = 0; i < 20_000; i++)
                    {
                        random.NextBytes(bytes);
                        var v = BitConverter.ToUInt64(bytes);
                        var index = (uint)(v >> 59) & (m - 1);
                        var remainder = (uint)v & ((1u << q) - 1);
                        var low = (index << q) | (remainder >> random.Next(q + 1));

                        hll.RegisterState[index] = 0;
                        hll.AddHash(low);

                        var wide = ((ulong)(low << b) << 32) | ((v >> 32) << b);
                        var run = wide == 0 ? 65 : BitOperations.LeadingZeroCount(wide) + 1;
                        if (hll.RegisterState[index] != Math.Min(run, q + 1))
                        {
                            return false;
                        }

                        if (run > q) capped++; else uncapped++;
                    }
                }

                return capped > 1000 && uncapped > 1000;
            }),
            new Claim("`Count()` accounts for the capped registers", () =>
            {
                // Half the registers capped: the estimate is the tau-corrected one, which counting
                // a capped register as a plain 2^-(q+1) would miss by more than a tenth.
                var hll = new HyperLogLog(16);
                for (var i = 0; i < 16; i++)
                {
                    hll.RegisterState[i] = (byte)(i < 8 ? 29 : 28);
                }

                var expected = TestHyperLogLogRange.ExpectedEstimate(hll.RegisterState);
                var asPlain = 0.673 * 256 / ((8 * Math.Pow(2, -28)) + (8 * Math.Pow(2, -29)));
                return Math.Abs(hll.Count() - expected) <= 1 && Math.Abs(asPlain - expected) / expected > 0.1;
            }),
            new Claim("With 16 registers or more the estimate stays close to its nominal error up to about 2^33 " +
                "distinct items, 8,589,934,592", () =>
                // At 2^33, over Poisson-sampled sketches: the mean within four standard errors and
                // the spread within the window TestSketchBounds holds the small range to.
                1UL << 33 == 8_589_934_592
                && new[] { (16u, 4096), (1024u, 256), (16384u, 64) }.All(s =>
                {
                    var (mean, standardError, ratio) = HyperLogLogError(s.Item1, s.Item2, Math.Pow(2, 33), 33 + (int)s.Item1);
                    return Math.Abs(mean) <= 4 * standardError && ratio >= 0.75 && ratio <= 1.35;
                })),
            new Claim("to about 1.5–2 times nominal at 2^34 with 1,024 registers or more", () =>
                new[] { (1024u, 256), (16384u, 64) }.All(s =>
                {
                    var (_, _, ratio) = HyperLogLogError(s.Item1, s.Item2, Math.Pow(2, 34), 34 + (int)s.Item1);
                    return ratio >= 1.4 && ratio <= 2.1;
                })),
            new Claim("about 2^32 × ln m: 11,280,826,639 with 16 registers, 29,737,154,114 with 1,024 and " +
                "41,673,492,339 with 16,384", () =>
                FullHyperLogLog(16).Count() == 11_280_826_639
                && FullHyperLogLog(1024).Count() == 29_737_154_114
                && FullHyperLogLog(16384).Count() == 41_673_492_339
                && new[] { 16u, 1024u, 16384u }.All(m =>
                    Math.Abs(FullHyperLogLog(m).Count() / (Math.Pow(2, 32) * Math.Log(m)) - 1) < 0.06)),
            new Claim("Below 16 registers the ceiling is under 2^33; with one register it is 1,490,120,207", () =>
                new[] { 1u, 2u, 4u, 8u }.All(m => FullHyperLogLog(m).Count() < 1UL << 33)
                && FullHyperLogLog(16).Count() > 1UL << 33
                && FullHyperLogLog(1).Count() == 1_490_120_207),
            new Claim("It never reports 0 once anything has been added", () =>
                // One hash at every register count to 2^16, landing on registers at every place
                // a single item can leave them, and every register capped.
                Enumerable.Range(0, 17).All(b =>
                    new[] { 0u, 1u, 0x80000000u, 0xFFFFFFFFu, 0x12345678u }.All(hash =>
                    {
                        var hll = new HyperLogLog(1u << b);
                        hll.AddHash(hash);
                        return hll.Count() >= 1;
                    })
                    && FullHyperLogLog(1u << b).Count() >= 1UL << b)),
            new Claim("`IsSaturated()` is true when every register is capped, which is when `Count()` is a floor and " +
                "not an estimate", () =>
            {
                // Every register capped, at the smallest and the largest sketch the section gives a
                // ceiling for, and not one register short of it; and the count of a saturated sketch is
                // the ceiling that stays put however much is added.
                var oneShort = FullHyperLogLog(16);
                oneShort.RegisterState[7] = (byte)(32 - BitOperations.Log2(16u));
                var full = FullHyperLogLog(16384);
                var before = full.Count();
                for (uint i = 0; i < 1000; i++)
                {
                    full.AddHash(i * 2654435761u);
                }

                return FullHyperLogLog(16).IsSaturated()
                    && full.IsSaturated()
                    && !oneShort.IsSaturated()
                    && full.Count() == before;
            }),
            new Claim("a sketch is not likely to be full until later still, and the more registers it has the later " +
                "that is", () =>
            {
                // At 2^33 items, where the error starts to grow, 16 registers are full about one time in ten
                // and 1,024 never; at 2^34 they are full three times in four and 1,024 still never.
                bool Full(uint m, int exponent, int trials, int seed)
                {
                    var random = new Random(seed);
                    var saturated = 0;
                    for (var t = 0; t < trials; t++)
                    {
                        saturated += TestHyperLogLogRange.PoissonSketch(m, Math.Pow(2, exponent), random).IsSaturated() ? 1 : 0;
                    }

                    return saturated > trials / 2;
                }

                return !Full(16, 33, 1024, 1)
                    && Full(16, 34, 1024, 2)
                    && !Full(1024, 34, 64, 3)
                    && !Full(16384, 34, 64, 4)
                    && !Full(16384, 35, 64, 5)
                    && Full(16384, 36, 64, 6);
            }),
            new Claim("A caller who wants the earlier warning compares `Count()` with 2^33", () =>
            {
                // A sketch of 1,024 registers is below 2^33 at 2^32 items and above it at 2^34, with
                // errors of a few percent against a factor of four between them.
                var random = new Random(194);
                return Enumerable.Range(0, 64).All(_ =>
                    TestHyperLogLogRange.PoissonSketch(1024, Math.Pow(2, 32), random).Count() < 1UL << 33
                    && TestHyperLogLogRange.PoissonSketch(1024, Math.Pow(2, 34), random).Count() > 1UL << 33);
            }),

            // The estimator's own bias (#215). Each figure is worked out from the library's Count() on
            // the register state the Poisson model expects, or from the constant it is built from, and
            // held to a derivation that does not pass through HyperLogLog.cs: the integral that defines
            // the paper's alpha and the closed form for the expected raw estimate (HyperLogLogBias).
            new Claim("`Count()` uses linear counting while the raw estimate is at most 2.5 · m and a register is " +
                "still zero, and the raw estimate otherwise", () =>
            {
                // The states the model expects after 2.43 m and 2.44 m items, one on each side of the line
                // (their raw estimates are 2.496 m and 2.505 m), and a state of all ones, whose raw
                // estimate is under the line with no register at zero to count.
                const uint M = 1u << 20;
                var alpha = HyperLogLogBias.LibraryAlpha(M);
                var below = ExpectedHyperLogLog(M, 2.43);
                var past = ExpectedHyperLogLog(M, 2.44);
                var ones = new HyperLogLog(M);
                Array.Fill(ones.RegisterState, (byte)1);

                var zeros = below.RegisterState.Count(r => r == 0);
                var counted = M * Math.Log((double)M / zeros);
                var belowRaw = RawEstimate(below.RegisterState, alpha);
                var pastRaw = RawEstimate(past.RegisterState, alpha);
                var onesRaw = RawEstimate(ones.RegisterState, alpha);

                return zeros > 0 && belowRaw <= 2.5 * M && Math.Abs(below.Count() - counted) <= 1
                    && Math.Abs(belowRaw - counted) / counted > 0.02
                    && pastRaw > 2.5 * M && Math.Abs(past.Count() - pastRaw) <= pastRaw * 1e-5
                    && ones.RegisterState.All(r => r == 1) && onesRaw <= 2.5 * M && Math.Abs(ones.Count() - onesRaw) <= 1;
            }),
            new Claim("The constant is the paper's 0.7213, a four-digit rounding of the limit 1 / (2 ln 2) = 0.72135", () =>
                Rounds(HyperLogLogBias.LimitAlpha, 0.7213, 4)
                && Rounds(HyperLogLogBias.LimitAlpha, 0.72135, 5)
                // The integral that defines alpha tends to the same number, and the library's constant is
                // 0.7213 to the finite-m term, which is under a part in 10^9 of it at these sizes.
                && Rounds(HyperLogLogBias.ExactAlpha(1u << 30), 0.72135, 5)
                && new[] { 28, 30 }.All(b => Near(HyperLogLogBias.LibraryAlpha(1u << b), 0.7213, 1e-8))),
            new Claim("so with 2^14 registers or more the raw estimate is 0.0066% low", () =>
            {
                // The library's constant against the integral at every power of two from 2^14 to the
                // largest sketch, and then through Count() on every register at 8, where the estimate
                // is alpha m 2^8 and the truncation to a whole number is a part in 10^8.
                const uint M = 1u << 20;
                var hll = new HyperLogLog(M);
                Array.Fill(hll.RegisterState, (byte)8);
                var viaCount = (hll.Count() / (HyperLogLogBias.ExactAlpha(M) * M * 256)) - 1;

                return Enumerable.Range(14, 17).All(b => Rounds(HyperLogLogBias.AlphaBias(1u << b) * 100, -0.0066, 4))
                    && Rounds(viaCount * 100, -0.0066, 4);
            }),
            new Claim("That is more than the standard error, 1.04 / √m, from about 2^28 registers", () =>
            {
                static double Ratio(int b) => -HyperLogLogBias.AlphaBias(1u << b) / (1.04 / Math.Sqrt(1u << b));

                // The register count at which the two are equal, and the powers of two either side.
                var equal = Math.Pow(1.04 / -HyperLogLogBias.AlphaBias(1u << 28), 2);
                return Rounds(Math.Log2(equal), 28, 0) && Ratio(27) < 1 && Ratio(28) > 1 && Ratio(29) > 1 && Ratio(30) > 1;
            }),
            new Claim("by 2.7% at 2.44 · m distinct items, 1.0% at 3 · m and 0.2% at 4 · m, and it is within 0.01% " +
                "of the truth from 6 · m", () =>
            {
                const uint M = 1u << 20;
                double Closed(double lambda) => HyperLogLogBias.RawRatio(lambda, HyperLogLogBias.LibraryAlpha(M)) - 1;

                // Count() on the state the model expects, and the closed form for the paper's exact limit.
                var figures = new[] { (2.44, 2.7), (3.0, 1.0), (4.0, 0.2) }.All(s =>
                    Rounds(HyperLogLogBias.CountBias(M, s.Item1) * 100, s.Item2, 1)
                    && Rounds((HyperLogLogBias.RawRatio(s.Item1, HyperLogLogBias.LimitAlpha) - 1) * 100, s.Item2, 1));

                // Count() is the closed form to the rounding of a state of a million registers, with the
                // constant it has, and past 6 m both are within a hundredth of a percent. 5 m is not, so
                // that the bound has something to bound.
                var agrees = new[] { 2.44, 3, 4, 4.3, 5, 6, 8 }.All(lambda =>
                    Math.Abs(HyperLogLogBias.CountBias(M, lambda) - Closed(lambda)) < 1e-5);
                var gone = new[] { 6.0, 6.5, 7, 8, 10, 16, 32 }.All(lambda => Math.Abs(HyperLogLogBias.CountBias(M, lambda)) < 1e-4);

                // And in sketches of real keys, which neither the model nor the state it expects has
                // anything to do with: at 3 m the mean error of 200 streams is the closed form's, to
                // within four standard errors, and clearly above nought; at 6 m it is nought.
                var (high, highError) = RealHyperLogLogError(4096, 3, 200);
                var (control, controlError) = RealHyperLogLogError(4096, 6, 200);
                var real = Math.Abs(high - (HyperLogLogBias.RawRatio(3, HyperLogLogBias.LibraryAlpha(4096)) - 1)) <= 4 * highError
                    && high > 0.005 && Math.Abs(control) <= 4 * controlError;

                return figures && agrees && gone && HyperLogLogBias.CountBias(M, 5) > 1e-4 && real;
            }),
            new Claim("With 2^20 registers, whose standard error is 0.10%, the band is larger than that error from " +
                "2.44 · m to about 4.3 · m", () =>
            {
                const uint M = 1u << 20;
                var error = 1.04 / Math.Sqrt(M);
                var alpha = HyperLogLogBias.LibraryAlpha(M);
                double Band(double lambda) => HyperLogLogBias.RawRatio(lambda, alpha) - 1;

                // The closed form is above the error at every step from the switch to the crossing and
                // under it at every step after, to 40 m; and Count() agrees at the ends and just past.
                var lambdas = Enumerable.Range(0, 3757).Select(i => 2.44 + (i * 0.01)).ToArray();
                var crossing = lambdas.First(lambda => Band(lambda) <= error);
                return Rounds(error * 100, 0.10, 2) && Rounds(crossing, 4.3, 1)
                    && lambdas.TakeWhile(lambda => lambda < crossing).All(lambda => Band(lambda) > error)
                    && lambdas.SkipWhile(lambda => lambda < crossing).All(lambda => Band(lambda) <= error)
                    && HyperLogLogBias.CountBias(M, 2.44) > error
                    && HyperLogLogBias.CountBias(M, 4.3) > error
                    && HyperLogLogBias.CountBias(M, 4.4) < error;
            }),
            new Claim("Above about 2^16 registers the worst of the band is more than six times the standard error", () =>
            {
                // The worst the library's Count() gets, on a grid across the switch at 2.434 m, where
                // the raw estimate first passes 2.5 m. It does not depend on the register count, so
                // the count at which it is six standard errors is where the sentence puts its line.
                const uint M = 1u << 16;
                var worst = 0.0;
                for (var lambda = 2.40; lambda <= 2.50; lambda += 0.002)
                {
                    worst = Math.Max(worst, HyperLogLogBias.CountBias(M, lambda));
                }

                var ratio = worst / (1.04 / Math.Sqrt(M));
                return ratio is > 6 and < 7 && Rounds(Math.Log2(Math.Pow(6 * 1.04 / worst, 2)), 16, 0);
            }),
            new Claim("`HyperLogLogPlus` uses the exact limit and Ertl's estimator, which has neither", () =>
            {
                // The constant it is built from, and its Count() on the states the model expects at the
                // largest precision, where it is within three parts in a hundred thousand of the truth
                // all the way across the band that the classic estimator reads 2.7% high in.
                var constant = (double)typeof(HyperLogLogPlus)
                    .GetField("AlphaInfinity", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
                var plus = new HyperLogLogPlus(18);
                for (var i = 0; i < 40_000; i++)
                {
                    plus.Add(Encoding.ASCII.GetBytes($"tau-{i}"));
                }

                return Math.Abs((constant / HyperLogLogBias.LimitAlpha) - 1) < 1e-12 && !plus.IsSparse
                    && new[] { 1.0, 2.0, 2.43, 2.44, 3.0, 4.0, 5.0, 6.0, 8.0 }.All(lambda =>
                        Math.Abs(HyperLogLogBias.PlusBias(plus, lambda)) < 3e-5)
                    && HyperLogLogBias.CountBias(1u << 18, 2.44) > 0.02;
            }),
            new Claim("The largest is about half a megabyte", () =>
                Rounds(ChargeLine.Known["HyperLogLogPlus"].At(1 << 18) / 1_000_000, 0.5, 1)),
            // The largest estimator's error, and NewDefault either side of it.
            new Claim("Its error is `1.04 / √m`, about 0.00203 at the largest, and `NewDefault` refuses a smaller error with an " +
                "`ArgumentOutOfRangeException` naming it, where it used to build the largest", () =>
            {
                var smallest = 1.04 / Math.Sqrt(new HyperLogLogPlus(HyperLogLogPlus.MaxPrecision).M());
                return Rounds(smallest, 0.00203, 5)
                    && HyperLogLogPlus.NewDefault(smallest * (1 + 1e-9)).Precision() == HyperLogLogPlus.MaxPrecision
                    && Assert.Throws<ArgumentOutOfRangeException>(() => HyperLogLogPlus.NewDefault(smallest * (1 - 1e-9))).ParamName == "errorRate";
            }),
            new Claim("a read is charged three times the registers", () =>
                Near(Slope("UltraLogLog"), 3, 0.01)),
            new Claim("The largest has an error of about 9.55e-5, and `NewDefault` refuses a smaller one, naming it", () =>
            {
                var smallest = new UltraLogLog(UltraLogLog.MaxPrecision).RelativeError();
                return Rounds(smallest * 1e5, 9.55, 2)
                    && UltraLogLog.NewDefault(smallest * (1 + 1e-9)).Precision() == UltraLogLog.MaxPrecision
                    && Assert.Throws<ArgumentOutOfRangeException>(() => UltraLogLog.NewDefault(smallest * (1 - 1e-9))).ParamName == "errorRate";
            }),
            new Claim("12 bytes a register", () => Near(Slope("SetSketch"), 12, 0.01)),
            // The defect is there: a sketch sends an element's first value where the 32-bit
            // draw says, at a size and for an element where a 64-bit one would not. When the
            // draw changes, this fails, and so should the rest of the paragraph.
            new Claim("An element's first value goes to a register drawn with a 32-bit multiply-and-shift", () =>
                TestSetSketchShuffle.ShufflesWithThe32BitDraw()),
            // Counted over every 64-bit input at sampled registers: each count is a whole
            // number of 2^32, and that number is floor(2^32 / m) or one more, both seen.
            new Claim("which gives each of `m` registers `floor(2^32 / m)` of the 2^32 values or one more", () =>
                new uint[] { 12_345, 1_000_003 }.All(m =>
                {
                    var counts = new TestStableDecayDraw.InputCounts(TestSetSketchShuffle.Narrow, m);
                    var share = (1UL << 32) / m;
                    var seen = TestStableDecayDraw.SampledStarts(m).Distinct().Select(v =>
                    {
                        var whole = BigInteger.DivRem(counts.At(v), 1UL << 32, out var rest);
                        return rest.IsZero ? (ulong)whole : 0;
                    }).ToHashSet();
                    return seen.SetEquals(new[] { share, share + 1 });
                })),
            new Claim("the estimate falls short by about `r · (m − r) / 2^64`, at most about `(m / 2^33)²`, which is " +
                "nothing at a power of two", () =>
                // Within a percent of the leading term up to 2^24 registers, where it is the
                // rule of thumb's job (past that it strays by a factor of up to m / 2^32 either
                // way), within a twentieth over the bound everywhere, and exactly nought at
                // every power of two a sketch holds.
                ShortfallUpTo2To24.Value.FromLeading < 0.01
                && ShortfallUpTo2To24.Value.AgainstBound <= 1.05
                && ShortfallTeeth.Value.AgainstBound <= 1.05
                && 1 / (1 - (1.0 / 65)) <= 1.05
                && Enumerable.Range(0, 31).All(j => ShuffleShortfall(1UL << j) == 0)),
            new Claim("That is under 2% of the estimate's standard error, `1 / √m`, up to 2^24 registers", () =>
                ShortfallUpTo2To24.Value.AgainstError is < 0.02 and > 0.015),
            new Claim("as large as that error from about 88.5 million", () =>
            {
                // Below 86 million the shortfall is at most (m / 2^33)² / (1 - m / 2^32), which
                // against 1 / √m rises with m and is under one there; from there, every count.
                const ulong From = 86_000_000;
                var below = Math.Pow(From / 8589934592.0, 2) * Math.Sqrt(From) / (1 - (From / 4294967296.0)) < 1;
                ulong first = 0;
                for (var m = From; m < 90_000_000 && first == 0; m++)
                {
                    if (ShuffleShortfall(m) * Math.Sqrt(m) >= 1)
                    {
                        first = m;
                    }
                }

                return below && first > From && Rounds(first / 1e6, 88.5, 1);
            }),
            new Claim("at its largest 4% of the estimate, at 0.4 · 2^32 registers, about 1.72 billion", () =>
            {
                var (largest, at, _) = ShortfallTeeth.Value;
                var belowTheTeeth = Math.Pow(1.0 / 130, 2) / (1 - (1.0 / 65));
                return Rounds(largest * 100, 4, 2)
                    && Math.Abs(at - (0.4 * 4294967296.0)) <= 2000
                    && Rounds(at / 1e9, 1.72, 2)
                    && belowTheTeeth < largest;
            }),

            // ---- Frequency ------------------------------------------------------------------
            new Claim("counts to 18 quintillion", () => Rounds(ulong.MaxValue / 1e18, 18, 0)),
            new Claim("an `Add` to one at the limit leaves it there, and a `Merge` that would take one past it ends at it, so an estimate never falls", () =>
            {
                // A sketch merged into itself is the one way to the limit that costs nothing: a
                // single item, then each merge doubles every counter and the item count.
                var sketch = new CountMinSketch(0.01, 0.01);
                sketch.Add(Key(0));
                for (var merge = 0; merge < 63; merge++) sketch.Merge(sketch);
                var justBelow = sketch.Count(Key(0)) == 1UL << 63 && sketch.TotalCount() == 1UL << 63;

                sketch.Merge(sketch);
                var merged = sketch.Count(Key(0)) == ulong.MaxValue && sketch.TotalCount() == ulong.MaxValue;

                sketch.Add(Key(0));
                return justBelow && merged
                    && sketch.Count(Key(0)) == ulong.MaxValue && sketch.TotalCount() == ulong.MaxValue;
            }),
            new Claim("since a sketch merged into itself doubles every counter, and one holding a single item is at the limit after 64 merges", () =>
            {
                var sketch = new CountMinSketch(0.01, 0.01);
                sketch.Add(Key(0));
                var doubled = true;
                for (var merge = 1; merge <= 64; merge++)
                {
                    sketch.Merge(sketch);
                    var atTheLimit = sketch.Count(Key(0)) == ulong.MaxValue;
                    doubled &= merge < 64 ? sketch.Count(Key(0)) == 1UL << merge && !atTheLimit : atTheLimit;
                }

                return doubled;
            }),
            new Claim("5 at δ = 1%. A read charges 16 bytes a counter", () =>
                new CountMinSketch(0.01, 0.01).Depth == 5 && Near(Slope("CountMinSketch"), 16, 0.01)),
            new Claim("A read charges 24 bytes a counter.", () => Near(Slope("CountSketch"), 24, 0.01)),
            new Claim("A read charges 16 bytes a counter.", () => Near(Slope("PrivateCountMinSketch"), 16, 0.01)),
            // The defaults the measurements were made with, as the constructor declares them.
            new Claim("for the default α of 0.5 and β of 0.7 at a ρ of 1", () =>
            {
                var parameters = typeof(DpswSketch).GetConstructor(new[]
                {
                    typeof(long), typeof(double), typeof(double), typeof(double), typeof(uint), typeof(uint),
                    typeof(ulong?), typeof(Func<ReadOnlySpan<byte>, ulong>),
                })!.GetParameters();
                return (double)parameters.Single(p => p.Name == "alpha").DefaultValue! == 0.5
                    && (double)parameters.Single(p => p.Name == "beta").DefaultValue! == 0.7;
            }),
            // One window filled the same way at two values of α, with all else equal.
            new Claim("A smaller α keeps more checkpoints in each substream, so more sketches and a larger payload for the same window", () =>
            {
                DpswSketch Filled(double alpha)
                {
                    var window = new DpswSketch(4000, 1.0, alpha: alpha, width: 64, depth: 2, seed: 1);
                    for (var i = 0; i < 6000; i++) window.Add(Key(i % 500));
                    return window;
                }

                var finer = Filled(0.5);
                var coarser = Filled(0.75);
                return finer.SketchesHeld > coarser.SketchesHeld
                    && finer.ToByteArray().Length > coarser.ToByteArray().Length;
            }),
            new Claim("sketches of a window are at most as wide and as deep as a `PrivateCountMinSketch` is, 2,147,483,591", () =>
                PrivateCountMinSketch.MaxWidth == 2_147_483_591 && PrivateCountMinSketch.MaxDepth == 2_147_483_591
                && Assert.Throws<ArgumentOutOfRangeException>(
                    () => new DpswSketch(100, 1.0, width: PrivateCountMinSketch.MaxWidth + 1)).ParamName == "width"
                && Assert.Throws<ArgumentOutOfRangeException>(
                    () => new DpswSketch(100, 1.0, depth: PrivateCountMinSketch.MaxDepth + 1)).ParamName == "depth"
                && new DpswSketch(100, 1e9, width: PrivateCountMinSketch.MaxWidth, depth: PrivateCountMinSketch.MaxDepth).SketchesHeld == 0),
            new Claim("20 bytes a bucket to read", () => Near(Slope("HeavyKeeper"), 20, 0.01)),
            new Claim("a width of 2^14 from 67,108,864 items and 2^15 from 268,435,456", () =>
            {
                var sketch = new SublimeCountMinSketch(0.01);
                return DoublesAt(sketch, 1 << 13) == 67_108_864 && DoublesAt(sketch, 1 << 14) == 268_435_456;
            }),
            new Claim("a width of 2^22 at 10,568,984 items", () =>
                DoublesAt(new SublimeCountMinSketch(0.01, 0.9, 1.0), 1 << 21) == 10_568_984),
            // The sketch's own limit is the rule at its settings, and at the widest row an add
            // that brings the count to it is refused, with nothing changed.
            new Claim("So the sketch stops when its count reaches `(2^30 · s)^(1/g)`", () =>
                new[] { (0.5, 1.0), (0.9, 1.0), (0.95, 4.0), (0.7, 0.25) }.All(s =>
                {
                    var sketch = new SublimeCountMinSketch(0.01, s.Item1, s.Item2);
                    var limit = SublimeCountMinSketch.LimitFor(SublimeCountMinSketch.MaxWidth, s.Item2, s.Item1);
                    var fields = BindingFlags.NonPublic | BindingFlags.Instance;
                    typeof(SublimeCountMinSketch).GetField("width", fields)!.SetValue(sketch, SublimeCountMinSketch.MaxWidth);
                    typeof(SublimeCountMinSketch).GetField("expansionLimit", fields)!.SetValue(sketch, limit);
                    typeof(SublimeCountMinSketch).GetField("count", fields)!.SetValue(sketch, limit - 1);
                    return DoublesAt(sketch, 1 << 12) == SublimeCountMinSketch.LimitFor(1 << 12, s.Item2, s.Item1)
                        && Assert.Throws<InvalidOperationException>(() => sketch.Add(Key(1))) is not null
                        && sketch.TotalCount() == limit - 1;
                })
                && Math.Abs(SublimeCountMinSketch.LimitFor(1 << 30, 2.0, 0.9) / Math.Pow(Math.ScaleB(2.0, 30), 1 / 0.9) - 1) < 1e-9),
            new Claim("At the defaults that is 2^60 keys, and at `g` of 0.9 about ten billion", () =>
            {
                var defaults = new SublimeCountMinSketch(0.01);
                var fields = BindingFlags.NonPublic | BindingFlags.Instance;
                var g = (double)typeof(SublimeCountMinSketch).GetField("growthExponent", fields)!.GetValue(defaults)!;
                var s = (double)typeof(SublimeCountMinSketch).GetField("sizeFactor", fields)!.GetValue(defaults)!;
                return SublimeCountMinSketch.LimitFor(SublimeCountMinSketch.MaxWidth, s, g) == 1UL << 60
                    && Rounds(SublimeCountMinSketch.LimitFor(SublimeCountMinSketch.MaxWidth, 1.0, 0.9) / 1e10, 1, 0);
            }),
            new Claim("At a size factor of one it is below 2^32 keys, 4,294,967,296, once `g` passes 0.9375", () =>
                1UL << 32 == 4_294_967_296
                && SublimeCountMinSketch.LimitFor(SublimeCountMinSketch.MaxWidth, 1.0, 0.9376) < 1UL << 32
                && SublimeCountMinSketch.LimitFor(SublimeCountMinSketch.MaxWidth, 1.0, 0.9374) > 1UL << 32),
            new Claim("about 3.2 billion at 0.95 and 1.3 billion at 0.99, so a growth exponent near one stops at a few billion keys", () =>
                Rounds(SublimeCountMinSketch.LimitFor(SublimeCountMinSketch.MaxWidth, 1.0, 0.95) / 1e9, 3.2, 1)
                && Rounds(SublimeCountMinSketch.LimitFor(SublimeCountMinSketch.MaxWidth, 1.0, 0.99) / 1e9, 1.3, 1)),

            // ---- DDSketch ---------------------------------------------------------------------
            new Claim("A read charges 24 bytes a bucket of span", () => Near(Slope("DDSketch"), 24, 0.01)),
            new Claim("which at 1% is a few tens of thousands of buckets", () =>
            {
                var sketch = new DDSketch(0.01);
                var span = (int)CapacityRows.DDSketchIndexOf.Invoke(sketch, new object[] { double.MaxValue })!
                    - (int)CapacityRows.DDSketchIndexOf.Invoke(sketch, new object[] { double.Epsilon })! + 1;
                return span is > 10_000 and < 100_000;
            }),
        };
    }
}
