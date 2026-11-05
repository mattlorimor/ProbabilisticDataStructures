using System;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;
using static TestProbabilisticDataStructures.CapacityModel;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The largest of each structure that can be saved. Every structure is written into one
    /// array, framed in another, and read back from a third, so none can be saved with a
    /// payload past <see cref="CapacityModel.MaxPayload"/>, whatever it can be built at.
    /// </summary>
    /// <remarks>
    /// A payload is exact in the size of what it holds, so the fit of it is made without
    /// slack and the numbers are as exact as the sizing that carries them. Where the
    /// payload of the largest build is far under the ceiling the cell says so, and says so
    /// from the fit and not from the charge, which is at least twice it.
    /// </remarks>
    internal static partial class CapacityRows
    {
        private static readonly Regex Numbers = new(@"[\d,]+(?:\.\d+)?");

        private static string AfterFp(string counts) =>
            Regex.Match(counts, @"fp ([\d.]+%)").Groups[1].Value;

        private static double Fp(string counts) =>
            AfterFp(counts) == "0.1%" ? 0.001 : 0.01;

        private static string AsBuiltIf(bool fits, string why)
        {
            Assert.IsTrue(fits, $"the largest build no longer saves: {why}");
            return AsBuilt;
        }

        /// <summary>
        /// Whether the largest build's payload is under the ceiling, because the read of it
        /// is charged more than once the payload and is under the ceiling itself.
        /// </summary>
        private static string AsBuiltByCharge(string line, double units) =>
            AsBuiltIf(ChargeLine.Known[line].Upper(units) <= MaxPayload,
                $"a read of the {line} is charged {ChargeLine.Known[line].Upper(units):N0} bytes, which passes it");

        private static string AsBuiltByPayload(string line, StructureId id, double units) =>
            AsBuiltIf(PayloadLine(line, id).Upper(units) <= MaxPayload, $"the payload of the {line} passes it");

        private static ulong LargestUnder(ChargeLine payload, ulong most, Func<ulong, double> unitsOf) =>
            Largest(1, most, n => payload.Upper(unitsOf(n)) <= MaxPayload);

        private static string Saved(ChargeLine payload, ulong most, Func<ulong, double> unitsOf) =>
            Exact(LargestUnder(payload, most, unitsOf));

        /// <summary>How each row is worked out for the largest that can be saved.</summary>
        internal static Func<string> SavesFor(CapacityRow row) => row.Id switch
        {
            StructureId.BloomFilter => () =>
            {
                var fp = Fp(row.Counts);
                TryOptimalM(LargestSound(fp, 1), fp, out var m);
                return AsBuiltByCharge("BloomFilter", TrueBytes(m, 1));
            },
            StructureId.BloomFilter64 => () =>
            {
                var fp = Fp(row.Counts);
                var small = new BloomFilter64(100_000, fp);
                var fixedBytes = PayloadOf(small) - (long)TrueBytes(small.Capacity(), 1) - 4;
                var most = Largest(1, 1UL << 40, n =>
                {
                    var m = Utils.OptimalM64(n, fp);
                    return fixedBytes + (long)TrueBytes(m, 1) + 4 * (long)(m / Buckets64.maxArraySize + 1) <= MaxPayload;
                });
                return About(most, most, 2);
            },
            StructureId.CountingBloomFilter => () =>
            {
                var b = byte.Parse(Regex.Match(row.Counts, @"(\d)-bit").Groups[1].Value);
                var payload = PayloadLine("CountingBloomFilter", StructureId.CountingBloomFilter);
                var most = LargestSound(0.01, b);
                return payload.Upper(BitsOf(most, 0.01, b)) <= MaxPayload
                    ? AsBuilt
                    : Saved(payload, most, n => BitsOf(n, 0.01, b));
            },
            StructureId.DeletableBloomFilter => () =>
            {
                TryOptimalM(LargestSound(0.01, 1, 1000), 0.01, out var m);
                return AsBuiltByCharge("DeletableBloomFilter", TrueBytes(m - 1000, 1));
            },
            StructureId.PartitionedBloomFilter => () =>
            {
                var k = Utils.OptimalK(0.01);
                TryOptimalM(LargestSized(0.01), 0.01, out var m);
                return AsBuiltByCharge("PartitionedBloomFilter", k * TrueBytes((ulong)Math.Ceiling((double)m / k), 1));
            },
            StructureId.StableBloomFilter => () =>
            {
                var d = byte.Parse(Regex.Match(row.Counts, @"(\d)-bit").Groups[1].Value);
                var payload = PayloadLine("StableBloomFilter", StructureId.StableBloomFilter);
                var most = Largest(1, uint.MaxValue, m => BucketsAreSound(m, d));
                return payload.Upper(TrueBytes(most, d)) <= MaxPayload
                    ? AsBuilt
                    : Saved(payload, most, m => TrueBytes(m, d));
            },
            StructureId.InverseBloomFilter => () =>
            {
                if (!row.Counts.Contains("every slot", StringComparison.Ordinal))
                {
                    // Only the occupied slots are written, so an empty filter is its header.
                    return AsBuiltIf(PayloadOf(new InverseBloomFilter(1000)) < 64, "an empty filter writes more than its header");
                }

                var empty = new InverseBloomFilter(100_000);
                var filled = new InverseBloomFilter(100_000);
                for (var i = 0L; i < 400_000; i++) filled.Add(Key(i));
                var each = (PayloadOf(filled) - PayloadOf(empty)) / (double)Occupied(filled.ToByteArray());
                Assert.AreEqual(Math.Round(each), each, 0.001, "an occupied slot is not a whole number of bytes");
                Facts["inverse slot bytes"] = each;
                return Exact((ulong)((MaxPayload - PayloadOf(empty)) / (long)each));
            },
            StructureId.ScalableBloomFilter => () =>
            {
                var r = row.Counts.Contains("r 0.9", StringComparison.Ordinal) ? 0.9 : 0.8;
                if (row.Counts.Contains("growth 1,", StringComparison.Ordinal))
                {
                    return AsBuiltIf(Facts[$"scalable length 1000 0.01 {r}"] <= MaxPayload, "the contained filters come to more than the ceiling");
                }

                // Each contained filter is its partitions' bits, a little over, and the filter is
                // all of them: a tenth is room enough for the lengths and headers.
                var units = ShapeOf(1000, 0.01, r, 2).Aggregate(0UL, (sum, s) => sum + s.Units);
                return AsBuiltIf(units * 1.1 <= MaxPayload, "the contained filters come to more than the ceiling");
            },
            StructureId.CuckooBloomFilter => () =>
            {
                // The whole table is written, whatever the filter holds (#230): the payloads
                // of the tables the read was fitted to, empty, are a line in their buckets,
                // and the largest table under the ceiling gives the most items.
                var fp = Fp(row.Counts);
                var label = AfterFp(row.Counts);
                var payload = PayloadLine($"CuckooBloomFilter {label}", StructureId.CuckooBloomFilter);
                var buckets = 1UL << Enumerable.Range(1, 40).Last(k => payload.Upper(1UL << k) <= MaxPayload);
                Facts[$"cuckoo saves buckets {label}"] = buckets;
                var most = Largest(1, CuckooLargest(fp), n => CuckooBuckets(n) <= buckets);
                return most == CuckooLargest(fp) ? AsBuilt : Exact(most);
            },
            StructureId.QuotientFilter => () =>
            {
                var fp = Fp(row.Counts);
                var payload = PayloadLine($"QuotientFilter {AfterFp(row.Counts)}", StructureId.QuotientFilter);
                var (_, r) = QuotientFilter.Split(1000, fp);
                long Words(int q) => (((long)(1u << q)) * (r + 3) >> 6) + 2;
                var q = Enumerable.Range(1, 31).Last(q => payload.Upper(Words(q)) <= MaxPayload);
                return Exact(Largest(1, QuotientFilter.MaxItems, n => QuotientFilter.Split((uint)n, fp).QuotientBits <= q));
            },
            StructureId.BinaryFuseFilter => () =>
            {
                var width = int.Parse(Regex.Match(row.Counts, @"(\d+)-bit").Groups[1].Value);
                var payload = PayloadLine($"BinaryFuseFilter {width}-bit", StructureId.BinaryFuseFilter);
                var stride = (ulong)width / 8;
                var most = LargestUnder(payload, 1UL << 31, n => FuseCells(n) * stride);
                return About(most, most, 2);
            },
            StructureId.BloomierFilter => () =>
            {
                var bits = int.Parse(Regex.Match(row.Counts, @"(\d+)-bit").Groups[1].Value);
                var payload = PayloadLine($"BloomierFilter {bits}-bit", StructureId.BloomierFilter);
                var stride = (ulong)((8 + bits + 7) / 8);
                var most = LargestUnder(payload, 1UL << 31, n => FuseCells(n) * stride);
                return About(most, most, 2);
            },
            StructureId.Grafite => () =>
            {
                var fp = Fp(row.Counts);
                var range = ulong.Parse(Regex.Match(row.Counts, @"up to ([\d,]+)").Groups[1].Value.Replace(",", ""));
                var n = Largest(1, 1UL << 40, k => GrafiteMostHighBits(k, fp, range, out _) <= Grafite.MaxHighBits);
                // Sixteen bytes a word of low bits and twenty a word of high bits is the read;
                // eight of each is the payload, which is at most half of it.
                return AsBuiltIf(ChargeLine.Known["Grafite"].Upper(GrafiteUnits(n, fp, range)) / 2 <= MaxPayload,
                    "the worst case Grafite is more than the ceiling");
            },
            StructureId.InvertibleBloomLookupTable => () =>
            {
                var keySize = int.Parse(Regex.Match(row.Counts, @"(\d+)-byte").Groups[1].Value);
                var payload = PayloadLine($"InvertibleBloomLookupTable {keySize}", StructureId.InvertibleBloomLookupTable);
                return Saved(payload, uint.MaxValue, d =>
                {
                    try
                    {
                        return InvertibleBloomLookupTable.CellsProvisionedFor((uint)d);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        return double.PositiveInfinity;
                    }
                });
            },
            StructureId.InfiniFilter => () => TableSaves(row, StructureId.InfiniFilter, InfiniThreshold,
                (f, _) => $"InfiniFilter {f}", (q, f, _) => (((long)(1u << q)) * (f + 4) >> 6) + 2),
            StructureId.MementoFilter => () => TableSaves(row, StructureId.MementoFilter, MementoThreshold,
                (f, range) => $"MementoFilter {range}",
                (q, f, range) => (((long)(1u << q)) * (f + 1 + (int)Math.Ceiling(Math.Log2(range)) + 3) >> 6) + 2),
            StructureId.HyperLogLog => () => AsBuiltByPayload("HyperLogLog", StructureId.HyperLogLog, 1UL << 30),
            StructureId.HyperLogLogPlus => () => AsBuiltByPayload("HyperLogLogPlus", StructureId.HyperLogLogPlus, 1UL << 18),
            StructureId.UltraLogLog => () => AsBuiltByPayload("UltraLogLog", StructureId.UltraLogLog, 1UL << 26),
            StructureId.ThetaSketch => () => HeldSaves("ThetaSketch", StructureId.ThetaSketch),
            StructureId.TupleSketch => () => HeldSaves("TupleSketch", StructureId.TupleSketch),
            StructureId.SetSketch => () => Saved(PayloadLine("SetSketch", StructureId.SetSketch), (ulong)Array.MaxLength, m => m),
            StructureId.MinHashSignature => () => Saved(PayloadLine("MinHashSignature", StructureId.MinHashSignature), (ulong)Array.MaxLength, k => k),
            StructureId.SimHashSignature => () => AsBuiltIf(PayloadOf(new SimHashSignature(42)) < 64, "a signature is more than a word"),
            StructureId.CountMinSketch => () => Saved(PayloadLine("CountMinSketch", StructureId.CountMinSketch), (ulong)Array.MaxLength, w => w * 5),
            StructureId.CountSketch => () => Saved(PayloadLine("CountSketch", StructureId.CountSketch), (ulong)Array.MaxLength, w => w * 5),
            StructureId.PrivateCountMinSketch => () => Saved(PayloadLine("PrivateCountMinSketch", StructureId.PrivateCountMinSketch), (ulong)Array.MaxLength, w => w * 5),
            StructureId.SublimeCountMinSketch => () =>
            {
                // The payload is the counts, but a sketch also keeps the counts it had before
                // each doubling, so it is a tenth off a line in the width, and the width it
                // stops at is a doubling, far from the ceiling on both sides.
                var biggest = ChargeLine.Known["SublimeCountMinSketch"].Points
                    .Max(p => Payloads[(StructureId.SublimeCountMinSketch, p.Charge)]);
                var payload = PayloadLine("SublimeCountMinSketch", StructureId.SublimeCountMinSketch, biggest / 10);
                var depth = (ulong)new SublimeCountMinSketch(0.01).Depth;
                var last = Enumerable.Range(1, 30).Last(p => payload.Upper(depth << p) <= MaxPayload);
                Assert.IsGreaterThan((double)MaxPayload, payload.Lower(depth << (last + 1)), "the next width is not out of reach");
                return Exact(1UL << last);
            },
            StructureId.TopK => () => row.Counts.StartsWith("k,", StringComparison.Ordinal)
                ? Saved(PayloadLine("TopK", StructureId.TopK), (ulong)ElementHeap.MaxCapacity, k => k)
                : Saved(PayloadLine("TopK's sketch", StructureId.TopK), (ulong)Array.MaxLength, w => w * 5),
            StructureId.HeavyKeeper => () => Saved(PayloadLine("HeavyKeeper", StructureId.HeavyKeeper), HeavyKeeper.MaxWidth, w => w * 2),
            StructureId.DDSketch => () =>
            {
                var most = LargestUnder(PayloadLine("DDSketch", StructureId.DDSketch), 1UL << 32, span => span);
                return About(most, most, 2);
            },
            StructureId.VarOpt => () => row.Counts.Contains("empty", StringComparison.Ordinal)
                ? AsBuiltByPayload("VarOpt empty", StructureId.VarOpt, VarOpt.MaxK)
                : Saved(PayloadLine("VarOpt full", StructureId.VarOpt), VarOpt.MaxK, k => k),
            StructureId.DpswSketch => () => Measured(100_000_000_000, DpswPayloadWindowEstimate, 0.8,
                "only a stream that has filled a window of that many items reaches the state that is written, and the suite fills windows of at most 32,000 items",
                () => DpswLargestWindow * 1e6 < 100_000_000_000),
            _ => throw new NotSupportedException($"{row.Id} has no way of working out what can be saved"),
        };

        /// <summary>
        /// The held values a theta or tuple sketch is written with are at most twice its
        /// <c>k</c> less one, so the largest <c>k</c> for which any state saves is half of
        /// what the payload holds, rounded up.
        /// </summary>
        private static string HeldSaves(string line, StructureId id)
        {
            var payload = PayloadLine(line, id);
            var held = LargestUnder(payload, uint.MaxValue, h => h);
            return Exact((held + 1) / 2);
        }

        /// <summary>
        /// A table that doubles saves while its words fit, as it reads while its charge does:
        /// the tables beneath the main one are a sliver, allowed for as a twentieth.
        /// </summary>
        private static string TableSaves(
            CapacityRow row, StructureId id, double threshold,
            Func<int, ulong, string> lineName, Func<int, int, ulong, long> words)
        {
            var f = int.Parse(Regex.Match(row.Counts, @"(\d+)-bit").Groups[1].Value);
            var range = id == StructureId.MementoFilter
                ? ulong.Parse(Regex.Match(row.Counts, @"range ([\d,]+)").Groups[1].Value.Replace(",", ""))
                : 0UL;
            var payload = PayloadLine(lineName(f, range), id);
            var q = Enumerable.Range(1, 31).Last(q => payload.Upper(words(q, f, range)) * 1.05 <= MaxPayload);
            Assert.IsGreaterThan((double)MaxPayload, payload.Lower(words(q + 1, f, range)),
                "the next table up is not out of reach");
            return Exact(ItemsAtTable(threshold, q));
        }

        private static double DpswPayloadWindowEstimate()
        {
            var windows = new[] { 2_000L, 8_000L, DpswLargestWindow };
            var payloads = windows.Select(w =>
            {
                var sketch = new DpswSketch(w, 1.0, alpha: 0.5, beta: 0.7, width: 512, depth: 5, seed: 1);
                for (var i = 0L; i < w + w / 2; i++) sketch.Add(Key(i % 5000));
                return (double)PayloadOf(sketch);
            }).ToArray();

            var exponent = Math.Log(payloads[2] / payloads[0]) / Math.Log((double)windows[2] / windows[0]);
            return windows[2] * Math.Pow(MaxPayload / payloads[2], 1 / exponent);
        }

        private static long PayloadOf<T>(T structure) where T : IBinaryPersistable<T> =>
            structure.ToByteArray().Length - 18;
    }
}
