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
        /// <summary>The rows built from the filters whose table doubles, and from Grafite and the IBLT.</summary>
        internal static IEnumerable<CapacityRow> Tables()
        {
            yield return InfiniRow(8);
            yield return InfiniRow(16);
            yield return MementoRow(256, 8);
            yield return MementoRow(65536, 8);
            yield return GrafiteRow(0.01, "1%", 64);
            yield return GrafiteRow(0.001, "0.1%", 64);
            yield return GrafiteRow(0.01, "1%", 1024);
            yield return IbltRow(8);
            yield return IbltRow(32);
        }

        // ---- InfiniFilter and MementoFilter ------------------------------------------------

        private static readonly double InfiniThreshold = (double)typeof(InfiniFilter)
            .GetField("ExpansionThreshold", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;

        private static readonly double MementoThreshold = (double)typeof(MementoFilter)
            .GetField("ExpansionThreshold", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;

        /// <summary>
        /// The most a quotient table of this kind can have in a uint's worth of slots: it
        /// expands when three quarters full, so it is the items that fill the largest.
        /// </summary>
        private static ulong ItemsAtTable(double threshold, int quotientBits) =>
            (ulong)(threshold * (1UL << quotientBits));

        internal static CapacityRow InfiniRow(int f) => Row(
            StructureId.InfiniFilter, $"items added, {f}-bit fingerprints",
            () =>
            {
                // A table has a uint's worth of slots, so none has more than 2^31 addresses
                // bits; the expansion past that makes a table of one slot.
                Assert.AreEqual(typeof(uint),
                    typeof(InfiniSegment).GetProperty(nameof(InfiniSegment.Slots), BindingFlags.Instance | BindingFlags.NonPublic)!.PropertyType);
                Assert.AreEqual(1u, new InfiniSegment(32, f).Slots,
                    "a table of 32 address bits is no longer a table of one slot");
                return Exact(ItemsAtTable(InfiniThreshold, 31));
            },
            () =>
            {
                long Words(int q) => (((long)(1u << q)) * (f + 4) >> 6) + 2;
                ChargeLine Line() => ChargeLine.Fit($"InfiniFilter {f}", 64,
                    Enumerable.Range(12, 4).Select(q =>
                    {
                        var filter = new InfiniFilter((uint)ItemsAtTable(InfiniThreshold, q), f);
                        filter.Add(Key(1));
                        Assert.HasCount(1, filter.Segments);
                        Assert.AreEqual(1u << q, filter.Segments[0].Slots);
                        return (Words(q), ChargeOf(StructureId.InfiniFilter, filter));
                    }).ToArray());
                var line = Line();

                // The tables below the main one hold what has aged out of it: a few
                // entries in 2^F, which is what the line leaves out.
                var grown = new InfiniFilter(1, f);
                var items = (int)ItemsAtTable(InfiniThreshold, 12);
                for (var i = 0; i < items; i++) grown.Add(Key(i));
                Assert.AreEqual(1u << 12, grown.Segments[0].Slots, "the filter is not at the table the line was measured at");
                if (f <= 8)
                {
                    // An entry ages out after F expansions, which a table this small
                    // has seen only for the narrower fingerprints.
                    Assert.IsGreaterThan(1, grown.Segments.Count, "no entry aged out, so nothing was measured of the tables below");
                }

                Assert.IsLessThan(line.At(Words(12)) * 1.10, (double)ChargeOf(StructureId.InfiniFilter, grown),
                    "the tables below the main one cost more than a tenth of it");

                var q = Enumerable.Range(1, 31).Last(q => line.Surely((ulong)Words(q)));
                Assert.IsGreaterThan((double)Budget, line.Lower(Words(q + 1)), "the next table up is not out of reach");
                return Exact(ItemsAtTable(InfiniThreshold, q));
            });

        internal static CapacityRow MementoRow(ulong range, int f) => Row(
            StructureId.MementoFilter, $"items added, one per block, range {range:N0}, {f}-bit fingerprints",
            () =>
            {
                var mementoBits = (int)Math.Ceiling(Math.Log2(range));
                Assert.AreEqual(typeof(uint),
                    typeof(MementoSegment).GetProperty(nameof(MementoSegment.Slots), BindingFlags.Instance | BindingFlags.NonPublic)!.PropertyType);
                Assert.AreEqual(1u, new MementoSegment(32, f, mementoBits).Slots,
                    "a table of 32 address bits is no longer a table of one slot");
                return Exact(ItemsAtTable(MementoThreshold, 31));
            },
            () =>
            {
                var mementoBits = (int)Math.Ceiling(Math.Log2(range));
                long Words(int q) => (((long)(1u << q)) * (f + 1 + mementoBits + 3) >> 6) + 2;
                var line = ChargeLine.Fit($"MementoFilter {range}", 64,
                    Enumerable.Range(12, 4).Select(q =>
                    {
                        var filter = new MementoFilter(range, f, (uint)ItemsAtTable(MementoThreshold, q));
                        filter.Add(1);
                        Assert.HasCount(1, filter.Segments);
                        Assert.AreEqual(1u << q, filter.Segments[0].Slots);
                        return (Words(q), ChargeOf(StructureId.MementoFilter, filter));
                    }).ToArray());

                // The tables below the main one hold what has aged out of it, which the line
                // leaves out; grown through enough expansions to have some, they add a
                // sliver. One block per key: the keys are further apart than the range.
                var grown = new MementoFilter(range, f, 1);
                var items = (int)ItemsAtTable(MementoThreshold, 12);
                for (var i = 0; i < items; i++) grown.Add((ulong)i * 7919);
                Assert.AreEqual(1u << 12, grown.Segments[0].Slots, "the filter is not at the table the line was measured at");
                Assert.IsLessThan(line.At(Words(12)) * 1.05, (double)ChargeOf(StructureId.MementoFilter, grown),
                    "the tables below the main one cost more than a twentieth of it");

                var q = Enumerable.Range(1, 31).Last(q => line.Surely((ulong)Words(q)));
                Assert.IsGreaterThan((double)Budget, line.Lower(Words(q + 1)), "the next table up is not out of reach");
                Assert.IsLessThan((double)Budget, line.Upper(Words(q)) * 1.05,
                    "the table is within a twentieth of the budget, so the tables below it could decide");
                return Exact(ItemsAtTable(MementoThreshold, q));
            });

        // ---- Grafite ---------------------------------------------------------------------

        private static IEnumerable<ulong> GrafiteKeys(int n) => Enumerable.Range(0, n).Select(i => (ulong)i * 1009);

        /// <summary>
        /// The most high bits a filter over <paramref name="n"/> keys can have: the keys
        /// plus the high part of the largest code, which is at most the reduced
        /// universe, n * L / fp, split where the high parts are as many as the keys. A
        /// filter whose largest code falls short of the universe has fewer, and which
        /// does depends on the keys, so this is the worst case.
        /// </summary>
        internal static ulong GrafiteMostHighBits(ulong n, double fp, ulong range, out int low)
        {
            var r = (ulong)Math.Ceiling(n * (double)range / fp);
            low = System.Numerics.BitOperations.Log2(r / n);
            return (r >> low) + n + 1;
        }

        /// <summary>
        /// The units a read of the worst-case filter is charged: sixteen bytes for each
        /// word of low bits and twenty for each of high bits, whose select index and own
        /// copy come with them.
        /// </summary>
        private static ulong GrafiteUnits(ulong n, double fp, ulong range)
        {
            var bits = GrafiteMostHighBits(n, fp, range, out var low);
            return 16 * (n * (ulong)low / 64 + 2) + 20 * (bits / 64 + 1);
        }

        internal static CapacityRow GrafiteRow(double fp, string label, ulong range) => Row(
            StructureId.Grafite, $"keys, fp {label}, ranges up to {range:N0}, worst case",
            () =>
            {
                // The worst case really is one: no filter has more high bits than it says.
                foreach (var n in new[] { 20_000, 100_000, 400_000 })
                {
                    var real = Grafite.Build(GrafiteKeys(n), fp, range, seed: 7).HighBitCount;
                    Assert.IsLessThanOrEqualTo((long)GrafiteMostHighBits((ulong)n, fp, range, out _), real);
                }

                // The high bits are addressed by an int.
                var most = Largest(1, 1UL << 40, n => GrafiteMostHighBits(n, fp, range, out _) <= Grafite.MaxHighBits);
                return About(most, most);
            },
            () =>
            {
                // A read is charged by the words it holds: found over several filters, and
                // then for the worst case at each size.
                var samples = new[] { 20_000, 100_000, 400_000, 800_000 }.Select(n =>
                {
                    var filter = Grafite.Build(GrafiteKeys(n), fp, range, seed: 7);
                    return (Units: 16L * filter.Lows.Length + 20L * filter.Highs.Length,
                        Charge: ChargeOf(StructureId.Grafite, filter));
                }).ToArray();
                var line = ChargeLine.Fit("Grafite", 64, samples);
                Assert.AreEqual(1.0, line.Slope, "a word is not charged what it was");
                return LoadsAbout(line, 1UL << 34, n => GrafiteUnits(n, fp, range));
            });

        // ---- InvertibleBloomLookupTable ------------------------------------------------------

        internal static CapacityRow IbltRow(int keySize) => Row(
            StructureId.InvertibleBloomLookupTable, $"expected differences, {keySize}-byte keys",
            () => Exact(Largest(1, uint.MaxValue, d =>
            {
                try
                {
                    InvertibleBloomLookupTable.ValidTableSize(
                        InvertibleBloomLookupTable.CellsProvisionedFor((uint)d), keySize);
                    return true;
                }
                catch (ArgumentOutOfRangeException)
                {
                    return false;
                }
            })),
            () =>
            {
                var line = ChargeLine.Fit($"InvertibleBloomLookupTable {keySize}", 64,
                    new[] { 20_000u, 40_000u, 80_000u, 160_000u }.Select(d =>
                        ((long)InvertibleBloomLookupTable.CellsProvisionedFor(d),
                         ChargeOf(StructureId.InvertibleBloomLookupTable, new InvertibleBloomLookupTable(d, keySize)))).ToArray());
                return LoadsAbout(line, 1UL << 31, d => InvertibleBloomLookupTable.CellsProvisionedFor((uint)d));
            });
    }
}
