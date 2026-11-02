using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
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
            new Claim("It is written at format version 8, which 6.2.0 and earlier refuse", () =>
                new BloomFilter(100, 0.01, null, Placement.Wide).ToByteArray()[4] == 8
                && PersistenceFormat.WidePlacementVersion == 8
                && PersistenceFormat.SetSketchVariantVersion == 4
                && new[] { PersistenceFormat.RandomStateVersion, PersistenceFormat.CuckooPackedVersion }
                    .All(v => v < PersistenceFormat.SetSketchVariantVersion)),
            new Claim("A smaller filter is placed and written as before", () =>
            {
                var small = new BloomFilter(100, 0.01);
                return small.Placement == Placement.Narrow
                    && small.ToByteArray()[4] == 1
                    && new StableBloomFilter(100, 2, 0.01).ToByteArray()[4] == 5;
            }),
            new Claim("Below 2^27 the excess is at most about 0.07% of the rate at 1%", () =>
                Rounds(LargestFoldBelowWide() * 100, 0.07, 2)),
            new Claim("keeps the 32-bit placement, and the rate that goes with it, whatever its size", () =>
                Enumerable.Range(1, 7).All(v => BitPlacement.At((ushort)v) == Placement.Narrow)
                && BitPlacement.At(8) == Placement.Wide),
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
            new Claim("`WriteTo` of one throws an `InvalidOperationException` naming the size it would be and the size one can be, with the structure unchanged", () =>
            {
                var ex = Assert.Throws<InvalidOperationException>(
                    () => QuotientFilter.CheckWritable((int)((MaxPayload - 16) / 8) + 1));
                return ex.Message.Contains(MaxPayload.ToString("N0"), StringComparison.Ordinal)
                    && ex.Message.Contains((MaxPayload + 3).ToString("N0"), StringComparison.Ordinal)
                    && Assert.Throws<InvalidOperationException>(
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

            // ---- Grafite ------------------------------------------------------------------
            new Claim("and between 2 and 3 for the high parts", () =>
                new[] { (0.01, 64UL), (0.001, 64UL), (0.01, 1024UL) }.All(s =>
                {
                    var perKey = (double)CapacityRows.GrafiteMostHighBits(10_000_000, s.Item1, s.Item2, out _) / 10_000_000;
                    return perKey >= 2 && perKey < 3;
                })),
            new Claim("at most 2,147,483,647 bits of them", () => Grafite.MaxHighBits == 2_147_483_647),

            // ---- InvertibleBloomLookupTable -----------------------------------------------
            new Claim("It has 1.5 cells for each", () => InvertibleBloomLookupTable.CellsPerDifference == 1.5),

            // ---- ScalableBloomFilter -------------------------------------------------------
            new Claim("A growth factor of 2 is what a filter built without one takes", () =>
                ScalableBloomFilter.DefaultGrowth == 2),
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
            new Claim("The largest is about half a megabyte", () =>
                Rounds(ChargeLine.Known["HyperLogLogPlus"].At(1 << 18) / 1_000_000, 0.5, 1)),
            new Claim("a read is charged three times the registers", () =>
                Near(Slope("UltraLogLog"), 3, 0.01)),
            new Claim("12 bytes a register", () => Near(Slope("SetSketch"), 12, 0.01)),

            // ---- Frequency ------------------------------------------------------------------
            new Claim("counts to 18 quintillion", () => Rounds(ulong.MaxValue / 1e18, 18, 0)),
            new Claim("5 at δ = 1%. A read charges 16 bytes a counter", () =>
                new CountMinSketch(0.01, 0.01).Depth == 5 && Near(Slope("CountMinSketch"), 16, 0.01)),
            new Claim("A read charges 24 bytes a counter.", () => Near(Slope("CountSketch"), 24, 0.01)),
            new Claim("A read charges 16 bytes a counter.", () => Near(Slope("PrivateCountMinSketch"), 16, 0.01)),
            new Claim("20 bytes a bucket to read", () => Near(Slope("HeavyKeeper"), 20, 0.01)),
            new Claim("a width of 2^14 from 67,108,864 items and 2^15 from 268,435,456", () =>
            {
                var sketch = new SublimeCountMinSketch(0.01);
                return DoublesAt(sketch, 1 << 13) == 67_108_864 && DoublesAt(sketch, 1 << 14) == 268_435_456;
            }),
            new Claim("a width of 2^22 at 10,568,984 items", () =>
                DoublesAt(new SublimeCountMinSketch(0.01, 0.9, 1.0), 1 << 21) == 10_568_984),

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
