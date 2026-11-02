using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;
using static TestProbabilisticDataStructures.CapacityModel;

namespace TestProbabilisticDataStructures
{
    internal static partial class CapacityRows
    {
        /// <summary>The rows built from the estimators, the sketches and the samples.</summary>
        internal static IEnumerable<CapacityRow> Sketches()
        {
            yield return HyperLogLogRow();
            yield return HyperLogLogPlusRow();
            yield return UltraLogLogRow();
            yield return ThetaRow();
            yield return TupleRow();
            yield return SetSketchRow();
            yield return MinHashRow();
            yield return SimHashRow();
            yield return CountMinRow();
            yield return CountSketchRow();
            yield return PrivateCountMinRow();
            yield return SublimeRow(0.9);
            yield return TopKElementsRow();
            yield return TopKSketchRow();
            yield return HeavyKeeperRow();
            yield return DDSketchRow();
            yield return VarOptRow(false);
            yield return VarOptRow(true);
            yield return DpswRow();
        }

        private static ulong Power2Below(Func<ulong, bool> fits, int from = 1, int to = 40) =>
            1UL << Enumerable.Range(from, to - from + 1).Last(k => fits(1UL << k));

        // ---- Cardinality ------------------------------------------------------------------

        private static CapacityRow HyperLogLogRow() => Row(
            StructureId.HyperLogLog, "registers m (a power of two)",
            () =>
            {
                // The registers are one array of bytes, and the largest power of two
                // an array holds is 2^30; the next is refused before anything is allocated.
                Assert.IsLessThan((ulong)Array.MaxLength, 1UL << 30);
                Assert.IsGreaterThan((ulong)Array.MaxLength, 1UL << 31);
                Assert.Throws<OverflowException>(() => new HyperLogLog(1u << 31));
                return Exact(1UL << 30);
            },
            () =>
            {
                var line = ChargeLine.Fit("HyperLogLog", 64,
                    Enumerable.Range(12, 5).Select(p =>
                        ((long)(1L << p), ChargeOf(StructureId.HyperLogLog, new HyperLogLog(1u << p)))).ToArray());
                return Exact(Power2Below(m => line.Surely(m)));
            });

        private static CapacityRow HyperLogLogPlusRow() => Row(
            StructureId.HyperLogLogPlus, "registers 2^p, p up to 18",
            () =>
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new HyperLogLogPlus(HyperLogLogPlus.MaxPrecision + 1));
                return Exact(1UL << (int)HyperLogLogPlus.MaxPrecision);
            },
            () =>
            {
                // A dense estimator at every precision it can have, measured where it is
                // cheap to make one dense and carried to the largest.
                var line = ChargeLine.Fit("HyperLogLogPlus", 128,
                    Enumerable.Range(9, 4).Select(p =>
                    {
                        var estimator = new HyperLogLogPlus((uint)p);
                        for (var i = 0; i < 20 << p; i++) estimator.Add(Key(i));
                        return ((long)(1L << p), ChargeOf(StructureId.HyperLogLogPlus, estimator));
                    }).ToArray());
                Assert.IsTrue(line.Surely(1UL << (int)HyperLogLogPlus.MaxPrecision),
                    "the largest dense estimator no longer loads under the default budget");
                return Always;
            });

        private static CapacityRow UltraLogLogRow() => Row(
            StructureId.UltraLogLog, "registers 2^p, p up to 26",
            () =>
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new UltraLogLog(UltraLogLog.MaxPrecision + 1));
                return Exact(1UL << (int)UltraLogLog.MaxPrecision);
            },
            () =>
            {
                var line = ChargeLine.Fit("UltraLogLog", 64,
                    Enumerable.Range(12, 5).Select(p =>
                        ((long)(1L << p), ChargeOf(StructureId.UltraLogLog, new UltraLogLog((uint)p)))).ToArray());
                Assert.IsTrue(line.Surely(1UL << (int)UltraLogLog.MaxPrecision),
                    "the largest UltraLogLog no longer loads under the default budget");
                return Always;
            });

        private static CapacityRow ThetaRow() => Row(
            StructureId.ThetaSketch, "k, values retained",
            () =>
            {
                Assert.IsNotNull(new ThetaSketch(ThetaSketch.MaxNominalEntries));
                Assert.Throws<ArgumentOutOfRangeException>(() => new ThetaSketch(ThetaSketch.MaxNominalEntries + 1));
                return Exact(ThetaSketch.MaxNominalEntries);
            },
            () =>
            {
                var line = ChargeLine.Fit("ThetaSketch", 64,
                    new[] { 4096u, 16384u, 65536u }.Select(k =>
                    {
                        var sketch = new ThetaSketch(k);
                        for (var i = 0; i < 4 * k; i++) sketch.Add(Key(i));
                        Assert.IsGreaterThanOrEqualTo(k, sketch.Retained(), "the sketch is not saturated");
                        return ((long)sketch.Retained(), ChargeOf(StructureId.ThetaSketch, sketch));
                    }).ToArray());

                // A sketch is written holding k values just after it trims and up to twice
                // that less one before the next, so k loads whatever it holds when 2k - 1 do.
                var written = new ThetaSketch(1000);
                for (var i = 0; i < 100_000; i++) written.Add(Key(i));
                Assert.IsGreaterThan(1000u, written.Retained(), "a saturated sketch is written holding just k");
                Assert.IsLessThanOrEqualTo(1999u, written.Retained(), "a sketch is written holding more than 2k - 1");
                return LoadsAbout(line, ThetaSketch.MaxNominalEntries, k => 2 * k - 1);
            });

        private static CapacityRow TupleRow() => Row(
            StructureId.TupleSketch, "k, keys retained",
            () =>
            {
                Assert.IsNotNull(new TupleSketch(TupleSketch.MaxNominalEntries));
                Assert.Throws<ArgumentOutOfRangeException>(() => new TupleSketch(TupleSketch.MaxNominalEntries + 1));
                return Exact(TupleSketch.MaxNominalEntries);
            },
            () =>
            {
                var line = ChargeLine.Fit("TupleSketch", 64,
                    new[] { 4096u, 16384u, 65536u }.Select(k =>
                    {
                        var sketch = new TupleSketch(k);
                        for (var i = 0; i < 4 * k; i++) sketch.Add(Key(i), 1.0);
                        Assert.IsGreaterThanOrEqualTo(k, sketch.Retained(), "the sketch is not saturated");
                        return ((long)sketch.Retained(), ChargeOf(StructureId.TupleSketch, sketch));
                    }).ToArray());
                return LoadsAbout(line, TupleSketch.MaxNominalEntries, k => 2 * k - 1);
            });

        // ---- Similarity -------------------------------------------------------------------

        private static CapacityRow SetSketchRow() => Row(
            StructureId.SetSketch, "registers m",
            () =>
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new SetSketch(SetSketch.MaxRegisters + 1));
                return Exact((ulong)SetSketch.MaxRegisters);
            },
            () =>
            {
                var line = ChargeLine.Fit("SetSketch", 64,
                    new[] { 4096, 16384, 65536, 262144 }.Select(m =>
                        ((long)m, ChargeOf(StructureId.SetSketch, new SetSketch(m)))).ToArray());
                return LoadsAbout(line, (ulong)SetSketch.MaxRegisters, m => m);
            });

        private static CapacityRow MinHashRow() => Row(
            StructureId.MinHashSignature, "k, values in the signature",
            () =>
            {
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => MinHash.Signature(new[] { "a" }, MinHashSignature.MaxValues + 1));
                return Exact((ulong)MinHashSignature.MaxValues);
            },
            () =>
            {
                var line = ChargeLine.Fit("MinHashSignature", 64,
                    new[] { 100_000, 200_000, 400_000, 800_000 }.Select(k =>
                        ((long)k, ChargeOf(StructureId.MinHashSignature, MinHash.Signature(new[] { "a", "b", "c" }, k)))).ToArray());
                return LoadsAbout(line, (ulong)MinHashSignature.MaxValues, k => k);
            });

        private static CapacityRow SimHashRow() => Row(
            StructureId.SimHashSignature, "none: one 64-bit value",
            () => Always,
            () =>
            {
                Assert.IsLessThan(1024L, ChargeOf(StructureId.SimHashSignature, new SimHashSignature(42)));
                return Always;
            });

        // ---- Frequency --------------------------------------------------------------------

        private static CapacityRow CountMinRow() => Row(
            StructureId.CountMinSketch, "width w = ⌈e/ε⌉ per row, δ 1% (5 rows)",
            () =>
            {
                // One past the longest array a row can be: refused when the first row is
                // allocated, which is before anything of that size exists.
                var epsilon = Math.E / (Array.MaxLength + 0.5);
                Assert.AreEqual(Array.MaxLength + 1.0, Math.Ceiling(Math.E / epsilon));
                Assert.Throws<OutOfMemoryException>(() => new CountMinSketch(epsilon, 0.01));
                return Exact((ulong)Array.MaxLength);
            },
            () =>
            {
                var line = ChargeLine.Fit("CountMinSketch", 64,
                    new[] { 1e-5, 5e-6, 2.5e-6, 1.25e-6 }.Select(e =>
                    {
                        var sketch = new CountMinSketch(e, 0.01);
                        return ((long)sketch.Width * sketch.Depth, ChargeOf(StructureId.CountMinSketch, sketch));
                    }).ToArray());
                var depth = new CountMinSketch(0.01, 0.01).Depth;
                return LoadsAbout(line, (ulong)Array.MaxLength, w => w * depth);
            });

        private static CapacityRow CountSketchRow() => Row(
            StructureId.CountSketch, "width w = ⌈1/ε²⌉ per row, δ 1% (5 rows)",
            () =>
            {
                var epsilon = 1 / Math.Sqrt(Array.MaxLength + 0.5);
                Assert.AreEqual(Array.MaxLength + 1.0, Math.Ceiling(1.0 / (epsilon * epsilon)));
                Assert.Throws<OutOfMemoryException>(() => new CountSketch(epsilon, 0.01));
                return Exact((ulong)Array.MaxLength);
            },
            () =>
            {
                var line = ChargeLine.Fit("CountSketch", 64,
                    new[] { 3e-3, 2e-3, 1.5e-3, 1e-3 }.Select(e =>
                    {
                        var sketch = new CountSketch(e, 0.01);
                        return ((long)sketch.Width() * sketch.Depth(), ChargeOf(StructureId.CountSketch, sketch));
                    }).ToArray());
                var depth = new CountSketch(0.5, 0.01).Depth();
                return LoadsAbout(line, (ulong)Array.MaxLength, w => w * depth);
            });

        private static CapacityRow PrivateCountMinRow() => Row(
            StructureId.PrivateCountMinSketch, "width w per row, depth 5",
            () =>
            {
                Assert.Throws<OutOfMemoryException>(
                    () => new PrivateCountMinSketch((uint)Array.MaxLength + 1, 5, 1.0, seed: 1));
                return Exact((ulong)Array.MaxLength);
            },
            () =>
            {
                var line = ChargeLine.Fit("PrivateCountMinSketch", 64,
                    new[] { 100_000u, 200_000u, 400_000u, 800_000u }.Select(w =>
                        ((long)w * 5, ChargeOf(StructureId.PrivateCountMinSketch,
                            new PrivateCountMinSketch(w, 5, 1.0, seed: 1)))).ToArray());
                return LoadsAbout(line, (ulong)Array.MaxLength, w => w * 5);
            });

        private static readonly MethodInfo SublimeLimitFor =
            typeof(SublimeCountMinSketch).GetMethod("LimitFor", BindingFlags.NonPublic | BindingFlags.Instance)!;

        /// <summary>The items a sketch adds before its rows double from <paramref name="width"/>.</summary>
        internal static ulong SublimeItemsToDouble(SublimeCountMinSketch sketch, int width) =>
            (ulong)SublimeLimitFor.Invoke(sketch, new object[] { width })!;

        private static CapacityRow SublimeRow(double growth) => Row(
            StructureId.SublimeCountMinSketch, "width w per row (a power of two), δ 1% (5 rows)",
            () =>
            {
                // The width is an int that doubles, so 2^30 is the last it can be.
                Assert.AreEqual(typeof(int),
                    typeof(SublimeCountMinSketch).GetField("width", BindingFlags.NonPublic | BindingFlags.Instance)!.FieldType);
                return Exact(1UL << 30);
            },
            () =>
            {
                // Widths are measured on a sketch grown by adding items until it doubles,
                // which is cheap at the small widths and says what a row costs.
                var points = Enumerable.Range(12, 4).Select(p =>
                {
                    var sketch = new SublimeCountMinSketch(0.01, growth, 1.0);
                    var items = SublimeItemsToDouble(sketch, 1 << (p - 1));
                    for (var i = 0UL; i < items; i++) sketch.Add(Key((long)i));
                    Assert.AreEqual(1 << p, sketch.Width, "the sketch did not grow to the width it was meant to");
                    return ((long)sketch.Depth * sketch.Width, ChargeOf(StructureId.SublimeCountMinSketch, sketch));
                }).ToArray();

                // What a counter costs to read depends on how the counters pack, which
                // depends on the counts they hold: some fifty-three bytes a counter at
                // these, and within about a percent across the widths measured. So the line
                // carries that percent, and the width it steps to is the estimate.
                var line = ChargeLine.Fit("SublimeCountMinSketch", points[^1].Item2 / 100, points);
                var depth = (ulong)new SublimeCountMinSketch(0.01).Depth;
                var last = Enumerable.Range(1, 30).Last(p => line.Surely(depth << p));
                Assert.AreEqual(last + 1,
                    Enumerable.Range(1, 30).First(p => !line.Possibly(depth << p)),
                    "the percent of slack leaves the width the read stops at undecided");
                return Measured(1UL << 21, () => 1UL << last, 0,
                    "what a counter costs to read depends on how the counters pack, so the width the read stops at is within a tenth of the budget of the next one",
                    () => line.At(depth << (last + 1)) < Budget * 1.10);
            });

        private static CapacityRow TopKElementsRow() => Row(
            StructureId.TopK, "k, elements held (8-byte), ε 0.001, δ 1%",
            () =>
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new TopK(0.01, 0.01, (uint)ElementHeap.MaxCapacity + 1));
                return Exact((ulong)ElementHeap.MaxCapacity);
            },
            () =>
            {
                var line = ChargeLine.Fit("TopK", 64,
                    new[] { 10_000u, 20_000u, 40_000u, 80_000u }.Select(k =>
                    {
                        var topK = new TopK(0.001, 0.01, k);
                        for (var i = 0L; i < 2L * k; i++) topK.Add(Key(i));
                        Assert.HasCount((int)k, topK.Elements(), "the top-k is not full");
                        return ((long)k, ChargeOf(StructureId.TopK, topK));
                    }).ToArray());
                return LoadsAbout(line, (ulong)ElementHeap.MaxCapacity, k => k);
            });

        private static CapacityRow TopKSketchRow() => Row(
            StructureId.TopK, "width w of the count-min sketch inside, δ 1% (5 rows)",
            () => Exact((ulong)Array.MaxLength),
            () =>
            {
                var line = ChargeLine.Fit("TopK's sketch", 128,
                    new[] { 1e-5, 5e-6, 2.5e-6, 1.25e-6 }.Select(e =>
                    {
                        var sketch = new CountMinSketch(e, 0.01);
                        var topK = new TopK(e, 0.01, 10);
                        for (var i = 0; i < 20; i++) topK.Add(Key(i));
                        return ((long)sketch.Width * sketch.Depth, ChargeOf(StructureId.TopK, topK));
                    }).ToArray());
                var depth = new CountMinSketch(0.01, 0.01).Depth;
                return LoadsAbout(line, (ulong)Array.MaxLength, w => w * depth);
            });

        private static CapacityRow HeavyKeeperRow() => Row(
            StructureId.HeavyKeeper, "width w per row, depth 2",
            () =>
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new HeavyKeeper(10, HeavyKeeper.MaxWidth + 1, 2, seed: 1));
                return Exact(HeavyKeeper.MaxWidth);
            },
            () =>
            {
                var line = ChargeLine.Fit("HeavyKeeper", 128,
                    new[] { 100_000u, 200_000u, 400_000u, 800_000u }.Select(w =>
                    {
                        var keeper = new HeavyKeeper(100, w, 2, seed: 1);
                        keeper.Add(Key(1));
                        return ((long)w * 2, ChargeOf(StructureId.HeavyKeeper, keeper));
                    }).ToArray());
                return LoadsAbout(line, HeavyKeeper.MaxWidth, w => w * 2);
            });

        // ---- Distributions and samples ----------------------------------------------------

        internal static readonly MethodInfo DDSketchIndexOf =
            typeof(DDSketch).GetMethod("IndexOf", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static CapacityRow DDSketchRow() => Row(
            StructureId.DDSketch, "bucket span, values of one sign",
            () => Memory,
            () =>
            {
                var line = ChargeLine.Fit("DDSketch", 128,
                    new[] { 1e-3, 3e-4, 1e-4, 3e-5 }.Select(alpha =>
                    {
                        var sketch = new DDSketch(alpha);
                        sketch.Add(1.0);
                        sketch.Add(1e100);
                        var span = (int)DDSketchIndexOf.Invoke(sketch, new object[] { 1e100 })! + 1;
                        return ((long)span, ChargeOf(StructureId.DDSketch, sketch));
                    }).ToArray());
                return LoadsAbout(line, 1UL << 32, span => span);
            });

        private static CapacityRow VarOptRow(bool full) => Row(
            StructureId.VarOpt, full ? "k, sample full of 8-byte items" : "k, sample empty",
            () =>
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new VarOpt(VarOpt.MaxK + 1, 1));
                return Exact(VarOpt.MaxK);
            },
            () =>
            {
                var line = ChargeLine.Fit(full ? "VarOpt full" : "VarOpt empty", 128,
                    new[] { 10_000u, 20_000u, 40_000u, 80_000u }.Select(k =>
                    {
                        var sample = new VarOpt(k, 1);
                        if (full)
                        {
                            for (var i = 0L; i < 2L * k; i++) sample.Add(Key(i));
                            Assert.AreEqual(k, sample.SampleCount, "the sample is not full");
                        }

                        return ((long)k, ChargeOf(StructureId.VarOpt, sample));
                    }).ToArray());
                return LoadsAbout(line, VarOpt.MaxK, k => k);
            });

        // ---- DpswSketch -------------------------------------------------------------------

        /// <summary>
        /// The window at which a sketch that has seen a whole window is charged the
        /// default budget, carried out from windows the suite can fill: the charge of a
        /// full window grows as a power of the window (sketches held grow as the number
        /// of substreams times the segments in each), so a line through the logarithms of
        /// three windows is extended.
        /// </summary>
        private const long DpswLargestWindow = 32_000;

        private static double DpswWindowEstimate()
        {
            var windows = new[] { 2_000L, 8_000L, DpswLargestWindow };
            var charges = windows.Select(w =>
            {
                var sketch = new DpswSketch(w, 1.0, width: 512, depth: 5, seed: 1);
                for (var i = 0L; i < w + w / 2; i++) sketch.Add(Key(i % 5000));
                return (double)ChargeOf(StructureId.DpswSketch, sketch);
            }).ToArray();

            var exponent = Math.Log(charges[2] / charges[0]) / Math.Log((double)windows[2] / windows[0]);
            var middle = charges[0] * Math.Pow((double)windows[1] / windows[0], exponent);
            Assert.AreEqual(charges[1], middle, charges[1] * 0.2,
                "the charge of a full window does not grow as a power of the window");
            return windows[2] * Math.Pow(Budget / charges[2], 1 / exponent);
        }

        private static CapacityRow DpswRow() => Row(
            StructureId.DpswSketch, "window (items), width 512, depth 5",
            () => Memory,
            () => Measured(1_500_000_000, DpswWindowEstimate, 0.35,
                "only a stream that has filled a window of that many items reaches the state that is read, and the suite fills windows of at most 32,000 items",
                () => DpswLargestWindow * 10_000.0 < 1_500_000_000));
    }
}
