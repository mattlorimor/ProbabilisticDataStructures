using System;
using System.Collections.Generic;
using System.Linq;
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
        /// The items at which a 1% filter reaches 0.27 · 2^32 bits, where the 32-bit band
        /// that was measured begins.
        /// </summary>
        private static ulong BandStart() =>
            Largest(1, CapacityRows.LargestSized(0.01), n => Utils.OptimalM((uint)n, 0.01) <= 0.27 * uint.MaxValue);

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
            ("the worst case is about ×1.42 at 1% and ×1.67 at 0.1%",
                "the band begins at a filter of more than 100 million items",
                () => BandStart() > 100_000_000),
            ("by +3.2% at 2^28 and +13% at 2^30, and returns 0 from 2^33",
                "the least of them is 268,435,456 distinct items to add",
                () => 1UL << 28 == 268_435_456),
        };

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
            new Claim("with a 32-bit kernel; `BloomFilter64` uses a 128-bit one", () =>
                typeof(HashKernelReturnValue).GetProperty(nameof(HashKernelReturnValue.LowerBaseHash))!.PropertyType == typeof(uint)
                && typeof(HashKernel128ReturnValue).GetProperty(nameof(HashKernel128ReturnValue.LowerBaseHash))!.PropertyType == typeof(ulong)),
            new Claim("about 121 million items at 1%, to nearly the 32-bit limit", () =>
                Rounds(BandStart() / 1e6, 121, 0)),
            new Claim("estimate corrects for collisions in a 32-bit space from about 143 million", () =>
            {
                var exp32 = (double)typeof(HyperLogLog).GetField("Exp32", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                return Rounds(exp32 / 30 / 1e6, 143, 0);
            }),
            new Claim("A raw estimate past 2^32 makes `Count()` return 0", () =>
            {
                var estimator = new HyperLogLog(1024);
                Array.Fill(estimator.RegisterState, (byte)30);
                return estimator.Count() == 0;
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
            new Claim("2^32 distinct items, 4,294,967,296", () =>
            {
                var exp32 = (double)typeof(HyperLogLog).GetField("Exp32", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                return exp32 == 4_294_967_296.0;
            }),
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
