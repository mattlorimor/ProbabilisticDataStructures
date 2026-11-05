using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;
using static TestProbabilisticDataStructures.CapacityModel;

namespace TestProbabilisticDataStructures
{
    internal static partial class CapacityRows
    {
        private static byte[] Key(long i)
        {
            var key = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(key, i);
            return key;
        }

        /// <summary>The rows built from the filters that are not the Bloom filters proper.</summary>
        internal static IEnumerable<CapacityRow> Membership()
        {
            foreach (var row in Inverse())
            {
                yield return row;
            }

            yield return ScalableRow(1000, 0.01, "1%", 0.8, 2);
            yield return ScalableRow(1000, 0.01, "1%", 0.9, 2);
            yield return ScalableRow(1000, 0.01, "1%", 0.8, 1);
            yield return ScalableRow(1000, 0.01, "1%", 0.9, 1);
            yield return CuckooRow(0.01, "1%");
            yield return CuckooRow(0.001, "0.1%");
            yield return QuotientRow(0.01, "1%");
            yield return QuotientRow(0.001, "0.1%");
            yield return FuseRow(BinaryFuseWidth.Eight);
            yield return FuseRow(BinaryFuseWidth.Sixteen);
            yield return BloomierRow(8);
            yield return BloomierRow(16);
            yield return BloomierRow(40);
        }

        // ---- InverseBloomFilter ----------------------------------------------------------

        // The occupied count follows the u32 capacity and the placement byte.
        private static long Occupied(byte[] inverse) =>
            BinaryPrimitives.ReadUInt32LittleEndian(inverse.AsSpan(14 + sizeof(uint) + 1));

        /// <summary>
        /// The slots are an array of references, so the capacity is the longest array.
        /// Allocating one that long is not a test; one past it is refused before anything
        /// is allocated, which shows the limit is the array's.
        /// </summary>
        private static string InverseBuilds()
        {
            Assert.AreEqual((uint)Array.MaxLength, InverseBloomFilter.MaxCapacity);
            Assert.Throws<ArgumentOutOfRangeException>(() => new InverseBloomFilter((uint)Array.MaxLength + 1));
            return Exact((ulong)Array.MaxLength);
        }

        internal static IEnumerable<CapacityRow> Inverse()
        {
            ChargeLine Empty() => ChargeLine.Fit("InverseBloomFilter", 64,
                new[] { 100_000u, 200_000u, 400_000u, 800_000u }
                    .Select(c => ((long)c, ChargeOf(StructureId.InverseBloomFilter, new InverseBloomFilter(c))))
                    .ToArray());

            yield return Row(StructureId.InverseBloomFilter, "capacity (slots), empty",
                InverseBuilds,
                () => LoadsAbout(Empty(), uint.MaxValue, c => c));

            yield return Row(StructureId.InverseBloomFilter, "capacity (slots), every slot holding an 8-byte item",
                InverseBuilds,
                () =>
                {
                    // Each occupied slot costs the same whatever the capacity: measured as
                    // the difference a fill makes, and the fill is the one the filter keeps.
                    var empty = new InverseBloomFilter(100_000);
                    var filled = new InverseBloomFilter(100_000);
                    for (var i = 0L; i < 400_000; i++)
                    {
                        filled.Add(Key(i));
                    }

                    var occupied = Occupied(filled.ToByteArray());
                    Assert.IsGreaterThan(90_000L, occupied, "the fill left a tenth of the slots empty");
                    var each = (ChargeOf(StructureId.InverseBloomFilter, filled)
                        - ChargeOf(StructureId.InverseBloomFilter, empty)) / (double)occupied;
                    Assert.AreEqual(48.0, each, 0.5, "an occupied slot is no longer charged 48 bytes");

                    var full = Empty().Plus(48, "InverseBloomFilter full");
                    return LoadsAbout(full, uint.MaxValue, c => c);
                });
        }

        // ---- ScalableBloomFilter ---------------------------------------------------------

        /// <summary>
        /// The items a contained filter takes before the next is started: the next filter
        /// is started by the Add after the one that finds this one at its fill ratio, so a
        /// filter takes the items it takes to get there. Its partitions are
        /// <paramref name="slots"/> bits each.
        /// </summary>
        private static ulong ItemsBeforeNext(ulong slots)
        {
            var items = (ulong)Math.Ceiling(slots * Math.Log(2));
            while (1 - Math.Exp(-(double)items / slots) < Defaults.FILL_RATIO)
            {
                items++;
            }

            while (items > 1 && 1 - Math.Exp(-(double)(items - 1) / slots) >= Defaults.FILL_RATIO)
            {
                items--;
            }

            return items;
        }

        /// <summary>
        /// The contained filters a scalable filter grows to, in order, as each one is
        /// sized: the items it takes before the next is started, the bytes of bits it
        /// holds, and its payload. Ends where the filter's own sizing, which is asked and
        /// not restated, says the next cannot be built, so no filter is allocated.
        /// </summary>
        /// <remarks>
        /// The payload is the version 14 layout in FORMAT.md, worked out rather than
        /// written, since the filters at the end are far past any memory: m, k, s, the
        /// placement byte, the count and the partition count, then each partition as a
        /// 64-bit table, its count, width and array count, then each array's length and
        /// bytes, as many arrays as its count implies (#232).
        /// <see cref="CheckShapePayloads"/> holds it to filters that can be built.
        /// </remarks>
        internal static List<(ulong Items, ulong Units, ulong Payload)> Shape(uint hint, double fp, double r, uint growth)
        {
            // The filter is asked how large its next filter is, and answers by how many it
            // has, so it is given placeholders for the ones it would have.
            var scalable = new ScalableBloomFilter(hint, fp, r, growth);
            scalable.Filters.Clear();
            var steps = new List<(ulong, ulong, ulong)>();
            while (true)
            {
                var rate = fp * Math.Pow(r, scalable.Filters.Count);
                ulong capacity;
                try
                {
                    capacity = scalable.NextCapacity(rate);
                }
                catch (InvalidOperationException)
                {
                    return steps;
                }

                scalable.Filters.Add(Placeholder);
                var k = Utils.OptimalK(rate);
                var slots = ProbabilisticDataStructures.PartitionedBloomFilter.PartitionSize(Utils.OptimalM64(capacity, rate), k);
                var payload = 8 + 4 + 8 + 1 + 8 + 4 + k * (8 + 1 + 4 + 4 * (slots / Buckets64.maxArraySize + 1) + TrueBytes(slots, 1));
                steps.Add((ItemsBeforeNext(slots), k * TrueBytes(slots, 1), payload));
            }
        }

        /// <summary>
        /// The payload of a scalable filter of the first <paramref name="filters"/> filters
        /// of its shape, at version 14: f64 r, fp and p, the u64 hint, the u32 growth factor
        /// and filter count, then each filter's u32 length and its whole envelope.
        /// </summary>
        internal static ulong ScalablePayload(List<(ulong Items, ulong Units, ulong Payload)> shape, int filters) =>
            shape.Take(filters).Aggregate(8UL + 8 + 8 + 8 + 4 + 4, (sum, s) => sum + 4 + 18 + s.Payload);

        /// <summary>
        /// The worked-out payloads <see cref="Shape"/> gives, held to real ones wherever the
        /// filter can be built: each contained filter's alone, and the scalable filter's
        /// with that many.
        /// </summary>
        internal static void CheckShapePayloads(uint hint, double fp, double r, uint growth, int built)
        {
            var shape = ShapeOf(hint, fp, r, growth);
            var filter = new ScalableBloomFilter(hint, fp, r, growth);
            var items = 0UL;
            for (var f = 1; f <= built; f++)
            {
                while (filter.Filters.Count < f)
                {
                    filter.Add(Key((long)items++));
                }

                Assert.AreEqual(shape[f - 1].Payload, (ulong)PayloadOf(filter.Filters[f - 1]),
                    $"filter {f - 1}'s payload is not what its shape works out");
                Assert.AreEqual(ScalablePayload(shape, f), (ulong)PayloadOf(filter),
                    $"the payload of {f} filters is not what the shape works out");
            }
        }

        /// <summary>
        /// A filter standing for one a scalable filter would have, empty and tiny, so that
        /// the scalable filter can count them and total what they hold.
        /// </summary>
        internal static readonly PartitionedBloomFilter Placeholder = new(1, 0.5);

        private static readonly Dictionary<(uint, double, double, uint), List<(ulong Items, ulong Units, ulong Payload)>> ShapeCache = new();

        internal static List<(ulong Items, ulong Units, ulong Payload)> ShapeOf(uint hint, double fp, double r, uint growth)
        {
            lock (ShapeCache)
            {
                if (!ShapeCache.TryGetValue((hint, fp, r, growth), out var shape))
                {
                    shape = Shape(hint, fp, r, growth);
                    ShapeCache[(hint, fp, r, growth)] = shape;
                }

                return shape;
            }
        }

        /// <summary>
        /// The contained filters of a scalable filter that does not grow them, in order,
        /// with what a read of each alone is charged. Every filter is built, so this is the
        /// measured side of <see cref="Shape"/>, and it is kept for the filters whose
        /// partitions, a thousand to a filter by the end, make their charge more than their bits.
        /// </summary>
        private static List<(ulong Items, long Charge)> ScalableSteps(uint hint, double fp, double r)
        {
            var steps = new List<(ulong, long)>();
            var totalLength = 0L;
            for (var i = 0; ; i++)
            {
                PartitionedBloomFilter filter;
                try
                {
                    filter = new PartitionedBloomFilter(hint, fp * Math.Pow(r, i));
                }
                catch (OverflowException)
                {
                    return steps;
                }

                var slots = (uint)Math.Ceiling((double)filter.BitCount / filter.PartitionCount);
                var items = ItemsBeforeNext(slots);

                // Read nested, a filter is also in the outer payload and copied out of it once more,
                // each the length of its own envelope.
                var length = filter.ToByteArray().Length;
                totalLength += length;
                steps.Add((items, ChargeOf(StructureId.PartitionedBloomFilter, filter) + 2L * length));
                Facts[$"scalable bytes ratio {hint} {fp} {r}"] = (double)steps.Sum(s => s.Item2) / totalLength;
                Facts[$"scalable length {hint} {fp} {r}"] = totalLength;
            }
        }

        private static readonly Dictionary<(uint, double, double), List<(ulong Items, long Charge)>> ScalableCache = new();

        internal static List<(ulong Items, long Charge)> Steps(uint hint, double fp, double r)
        {
            lock (ScalableCache)
            {
                if (!ScalableCache.TryGetValue((hint, fp, r), out var steps))
                {
                    steps = ScalableSteps(hint, fp, r);
                    ScalableCache[(hint, fp, r)] = steps;
                }

                return steps;
            }
        }

        internal static CapacityRow ScalableRow(uint hint, double fp, string fpLabel, double r, uint growth) => Row(
            StructureId.ScalableBloomFilter, $"items added, hint {hint:N0}, growth {growth}, fp {fpLabel}, r {r}",
            () =>
            {
                // Items added before the filter that cannot be sized is wanted; the Add
                // that wants it throws.
                var shape = ShapeOf(hint, fp, r, growth);
                var total = shape.Aggregate(0UL, (sum, s) => sum + s.Items);
                if (growth > 1)
                {
                    // Sized in 64 bits since #232, the filters a growing one doubles to pass
                    // any memory long before their sizing refuses one: the last alone holds
                    // more than a pebibyte.
                    Assert.IsGreaterThan(1UL << 50, shape[^1].Units);
                    return Memory;
                }

                if (growth == 1)
                {
                    // The same, found by building every filter.
                    Assert.AreEqual(total, Steps(hint, fp, r).Aggregate(0UL, (sum, s) => sum + s.Items));
                }

                return About(total, total, 2);
            },
            () => growth == 1 ? ScalableLoadsUngrown(hint, fp, r) : ScalableLoadsGrown(hint, fp, r, growth));

        /// <summary>
        /// A filter that does not grow its filters is read as them one after another, each
        /// as a read of its own plus the same fixed charge for being nested. The fixed
        /// charges are found from real reads of a few filters, and the sum is then taken
        /// over the filters, each charged as the filter it is.
        /// </summary>
        private static string ScalableLoadsUngrown(uint hint, double fp, double r)
        {
            var steps = Steps(hint, fp, r);
            var counts = new[] { 1, 2, 3, 4 };
            var measured = counts.Select(f =>
            {
                var filter = new ScalableBloomFilter(hint, fp, r, 1);
                var items = steps.Take(f - 1).Aggregate(0UL, (sum, s) => sum + s.Items) + 1;
                for (var i = 0UL; i < items; i++)
                {
                    filter.Add(Key((long)i));
                }

                Assert.HasCount(f, filter.Filters, "the filter did not grow as the sum of its steps says");
                return (Filters: f, Charge: ChargeOf(StructureId.ScalableBloomFilter, filter));
            }).ToArray();

            var line = ChargeLine.Fit("ScalableBloomFilter's fixed charges", 32 * 4,
                measured.Select(m => ((long)m.Filters,
                    m.Charge - steps.Take(m.Filters).Sum(s => s.Charge))).ToArray());

            // The items that make the most filters read under the budget.
            ulong ItemsThatFit(bool surely)
            {
                var charge = 0L;
                var items = 0UL;
                var most = 0UL;
                for (var f = 1; f <= steps.Count; f++)
                {
                    charge += steps[f - 1].Charge;
                    items += steps[f - 1].Items;
                    var fits = surely
                        ? line.Surely((ulong)f, Budget - charge)
                        : line.Possibly((ulong)f, Budget - charge);
                    if (!fits)
                    {
                        break;
                    }

                    most = items;
                }

                return most;
            }

            return About(ItemsThatFit(true), ItemsThatFit(false));
        }

        /// <summary>
        /// A filter that grows its filters is read mostly as its last few, which are the
        /// largest by far, so the read is a line in the bytes of bits the contained filters
        /// hold; the line is found from real reads of the first few filters, and the number
        /// of filters it stops at has to be the same whichever way the slack goes.
        /// </summary>
        private static string ScalableLoadsGrown(uint hint, double fp, double r, uint growth)
        {
            var shape = ShapeOf(hint, fp, r, growth);
            var points = Enumerable.Range(2, 10).Select(f =>
            {
                var filter = new ScalableBloomFilter(hint, fp, r, growth);
                var items = shape.Take(f - 1).Aggregate(0UL, (sum, s) => sum + s.Items) + 1;
                for (var i = 0UL; i < items; i++)
                {
                    filter.Add(Key((long)i));
                }

                Assert.HasCount(f, filter.Filters, "the filter did not grow as the sum of its steps says");
                return ((long)shape.Take(f).Aggregate(0UL, (sum, s) => sum + s.Units),
                    ChargeOf(StructureId.ScalableBloomFilter, filter));
            }).ToArray();

            // The partitions of each filter cost a few bytes each beyond their bits, which
            // is a few thousand bytes over the filters measured.
            var line = ChargeLine.Fit($"ScalableBloomFilter growth {growth}", 12_000, points);

            ulong ItemsThatFit(bool surely)
            {
                var units = 0UL;
                var items = 0UL;
                var most = 0UL;
                foreach (var step in shape)
                {
                    units += step.Units;
                    items += step.Items;
                    if (!(surely ? line.Surely(units) : line.Possibly(units)))
                    {
                        break;
                    }

                    most = items;
                }

                return most;
            }

            var low = ItemsThatFit(true);
            var high = ItemsThatFit(false);
            Assert.AreEqual(low, high, "the slack leaves the number of filters the read stops at undecided");
            Facts[$"scalable growth {growth} {r} loads"] = low;

            return About(low, high, 2);
        }

        // ---- CuckooBloomFilter -------------------------------------------------------------

        private static readonly MethodInfo CuckooFingerprintBits =
            typeof(CuckooBloomFilter).GetMethod("CalculateFingerprintBits", BindingFlags.NonPublic | BindingFlags.Static)!;

        internal static readonly double CuckooLoadFactor = (double)typeof(CuckooBloomFilter)
            .GetField("LoadFactor", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;

        /// <summary>The buckets a filter sized for <paramref name="n"/> items gets: its own sizing.</summary>
        internal static ulong CuckooBuckets(ulong n) => CuckooBloomFilter.Size(n, 0.01).Buckets;

        internal static uint CuckooBits(double fp) =>
            (uint)CuckooFingerprintBits.Invoke(null, new object[] { 4u, fp })!;

        /// <summary>
        /// The most items a filter is built for at <paramref name="fp"/>: its sizing is in 64
        /// bits (#231), so the search runs to half of what a <c>ulong</c> holds, whose buckets
        /// no table reaches, and the limit is the one array its fingerprints are.
        /// </summary>
        internal static ulong CuckooLargest(double fp)
        {
            var most = Largest(1, ulong.MaxValue / 2, n =>
                CuckooBloomFilter.GeometryProblem(CuckooBuckets(n), 4, CuckooBits(fp)) is null);
            Assert.Throws<ArgumentOutOfRangeException>(() => new CuckooBloomFilter(most + 1, fp));
            return most;
        }

        /// <summary>
        /// A filter sized for <c>n</c> at a rate. Its payload is its whole table whatever it
        /// holds (#230), so what it holds moves neither its read nor its save, and one row
        /// covers a filter empty and full: the tables are measured empty, and one of them
        /// filled is held to the same charge.
        /// </summary>
        internal static CapacityRow CuckooRow(double fp, string label) => Row(
            StructureId.CuckooBloomFilter,
            $"n, fp {label}",
            () => Exact(CuckooLargest(fp)),
            () =>
            {
                var tables = Enumerable.Range(14, 4).Select(k => (ulong)Math.Floor(3.8 * (1UL << k))).ToArray();
                foreach (var n in tables)
                {
                    Assert.AreEqual(1UL << Array.IndexOf(tables, n) + 14, CuckooBuckets(n));
                }

                var line = ChargeLine.Fit($"CuckooBloomFilter {label}", 64,
                    tables.Select(n => ((long)CuckooBuckets(n),
                        ChargeOf(StructureId.CuckooBloomFilter, new CuckooBloomFilter(n, fp, seed: 1)))).ToArray());

                var full = new CuckooBloomFilter(tables[0], fp, seed: 1);
                for (var i = 0; i < (int)tables[0]; i++) full.Add(Key(i));
                Assert.IsGreaterThan(tables[0] / 2, full.Count(), "the filter filled is not full enough to tell");
                Assert.AreEqual(line.Points[0].Charge, ChargeOf(StructureId.CuckooBloomFilter, full),
                    "a full filter is charged other than an empty one of its shape");

                // The table doubles, so the number steps, and the step is far from the
                // budget on both sides: the slack cannot move it.
                var buckets = 1UL << Enumerable.Range(1, 40).Last(k => line.Surely(1UL << k));
                Assert.AreEqual(buckets, 1UL << Enumerable.Range(1, 40).Last(k => line.Possibly(1UL << k)));
                Facts[$"cuckoo loads buckets {label}"] = buckets;
                return Exact(Largest(1, CuckooLargest(fp), n => CuckooBuckets(n) <= buckets));
            });

        // ---- QuotientFilter ----------------------------------------------------------------

        internal static CapacityRow QuotientRow(double fp, string label) => Row(
            StructureId.QuotientFilter, $"n, fp {label}",
            () =>
            {
                // The most items at the coarsest rate is three quarters of the widest table.
                var most = ProbabilisticDataStructures.QuotientFilter.MaxItems;
                Assert.AreEqual((uint)ProbabilisticDataStructures.QuotientFilter.MaxQuotientBits,
                    ProbabilisticDataStructures.QuotientFilter.Split(most, 0.5).QuotientBits);
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => ProbabilisticDataStructures.QuotientFilter.Split(most + 1, 0.5));
                return Exact(QuotientLargest(fp));
            },
            () =>
            {
                // The table is 2^q slots of r + 3 bits in 64-bit words, and a power of two,
                // so it is the table that steps the number.
                var (_, r) = ProbabilisticDataStructures.QuotientFilter.Split(1000, fp);
                long Words(int q) => (((1L << q) * (r + 3)) >> 6) + 2;
                var line = ChargeLine.Fit($"QuotientFilter {label}", 64,
                    Enumerable.Range(12, 5).Select(q => (Words(q),
                        ChargeOf(StructureId.QuotientFilter,
                            new ProbabilisticDataStructures.QuotientFilter((ulong)(0.75 * (1L << q)), fp)))).ToArray());
                var q = Enumerable.Range(1, ProbabilisticDataStructures.QuotientFilter.MaxQuotientBits)
                    .Last(q => line.Surely((ulong)Words(q)));
                return Exact(Largest(1, QuotientLargest(fp),
                    n => ProbabilisticDataStructures.QuotientFilter.Split(n, fp).QuotientBits <= q));
            });

        /// <summary>
        /// The most items a quotient filter is built for at a rate: past 2^31 slots the
        /// table, one array of words, bounds the remainder, so a finer rate stops at a
        /// smaller table than the widest. The constructor's own sizing, asked at each count;
        /// it refuses a count from the first one it cannot build onwards.
        /// </summary>
        internal static ulong QuotientLargest(double fp) =>
            Largest(1, ProbabilisticDataStructures.QuotientFilter.MaxItems, n =>
            {
                try
                {
                    ProbabilisticDataStructures.QuotientFilter.Split(n, fp);
                    return true;
                }
                catch (ArgumentOutOfRangeException)
                {
                    return false;
                }
            });

        // ---- BinaryFuseFilter and BloomierFilter ----------------------------------------------

        private static readonly MethodInfo FuseSegmentLength =
            typeof(BinaryFuseFilter).GetMethod("SegmentLengthFor", BindingFlags.NonPublic | BindingFlags.Static)!;

        /// <summary>
        /// The cells a fuse-style filter for <paramref name="keys"/> keys has: the
        /// reference implementation's sizing, which both filters share. Checked against
        /// the filters' own sizing, which allocates, at sizes up to tens of millions.
        /// </summary>
        internal static ulong FuseCells(ulong keys)
        {
            const uint MaxSegmentLength = 262144;
            const uint Arity = 3;
            var n = (uint)keys;
            var segment = Math.Min((uint)FuseSegmentLength.Invoke(null, new object[] { n })!, MaxSegmentLength);
            var factor = n <= 1 ? 0 : Math.Max(1.125, 0.875 + 0.25 * Math.Log(1000000.0) / Math.Log(n));
            var capacity = n <= 1 ? 0 : (uint)Math.Round(n * factor);
            uint length, count;
            unchecked
            {
                length = (capacity + segment - 1) / segment - (Arity - 1);
                length = (length + Arity - 1) * segment;
            }

            count = (length + segment - 1) / segment;
            count = count <= Arity - 1 ? 1 : count - (Arity - 1);
            return (ulong)(count + Arity - 1) * segment;
        }

        private static void CheckFuseCells()
        {
            foreach (var keys in new uint[] { 5_000, 123_456, 1_000_000, 5_000_000, 25_000_000 })
            {
                var fuse = (BinaryFuseFilter)RuntimeHelpers.GetUninitializedObject(typeof(BinaryFuseFilter));
                typeof(BinaryFuseFilter).GetField("width", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(fuse, BinaryFuseWidth.Eight);
                typeof(BinaryFuseFilter).GetMethod("Shape", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(fuse, new object[] { keys });
                Assert.AreEqual((uint)FuseCells(keys), fuse.ArrayLengthChosen,
                    $"the fuse sizing, restated here, gives {FuseCells(keys):N0} cells for {keys:N0} keys, and the filter's own does not");

                var bloomier = (BloomierFilter)RuntimeHelpers.GetUninitializedObject(typeof(BloomierFilter));
                typeof(BloomierFilter).GetField("valueBits", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(bloomier, 8);
                typeof(BloomierFilter).GetMethod("Shape", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(bloomier, new object[] { keys });
                Assert.AreEqual((uint)FuseCells(keys),
                    (uint)typeof(BloomierFilter).GetField("arrayLength", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .GetValue(bloomier)!,
                    $"the fuse sizing, restated here, gives {FuseCells(keys):N0} cells for {keys:N0} keys, and the Bloomier filter's own does not");
            }
        }

        internal static CapacityRow FuseRow(BinaryFuseWidth width) => Row(
            StructureId.BinaryFuseFilter, $"keys, {(int)width}-bit fingerprints",
            () =>
            {
                // The fingerprints are one array of bytes, so the most keys are those whose
                // cells, by the fuse sizing restated above, fit it. Found by search, and
                // held to the filter's own search, which uses its own sizing.
                var stride = (ulong)((int)width / 8);
                var most = Largest(1, int.MaxValue, n => FuseCells(n) * stride <= (ulong)Array.MaxLength);
                Assert.AreEqual(most, (ulong)ProbabilisticDataStructures.BinaryFuseFilter.MaxKeys(width));
                return Exact(most);
            },
            () =>
            {
                CheckFuseCells();
                var stride = (int)width / 8;
                var line = ChargeLine.Fit($"BinaryFuseFilter {(int)width}-bit", 64,
                    new[] { 100_000, 200_000, 400_000, 800_000 }.Select(n =>
                    {
                        var filter = ProbabilisticDataStructures.BinaryFuseFilter.Build(
                            Enumerable.Range(0, n).Select(i => Key(i)), width);
                        return ((long)filter.ArrayLengthChosen * stride,
                            ChargeOf(StructureId.BinaryFuseFilter, filter));
                    }).ToArray());
                return LoadsAbout(line, uint.MaxValue, n => FuseCells(n) * (ulong)stride);
            });

        internal static CapacityRow BloomierRow(int valueBits) => Row(
            StructureId.BloomierFilter, $"keys, {valueBits}-bit values",
            () =>
            {
                // The cells are one array of bytes, a fingerprint and a value to a cell.
                var stride = (ulong)((8 + valueBits + 7) / 8);
                var most = Largest(1, int.MaxValue, n => FuseCells(n) * stride <= (ulong)Array.MaxLength);
                Assert.AreEqual(most, (ulong)ProbabilisticDataStructures.BloomierFilter.MaxKeys(valueBits));
                return Exact(most);
            },
            () =>
            {
                var stride = (8 + valueBits + 7) / 8;
                var line = ChargeLine.Fit($"BloomierFilter {valueBits}-bit", 64,
                    new[] { 100_000, 200_000, 400_000, 800_000 }.Select(n =>
                    {
                        var filter = ProbabilisticDataStructures.BloomierFilter.Build(
                            Enumerable.Range(0, n).Select(i =>
                                new KeyValuePair<byte[], ulong>(Key(i), (ulong)(i % 200))), valueBits);
                        var cells = (uint)typeof(ProbabilisticDataStructures.BloomierFilter)
                            .GetField("arrayLength", BindingFlags.NonPublic | BindingFlags.Instance)!
                            .GetValue(filter)!;
                        return ((long)cells * stride, ChargeOf(StructureId.BloomierFilter, filter));
                    }).ToArray());
                return LoadsAbout(line, uint.MaxValue, n => FuseCells(n) * (ulong)stride);
            });
    }
}
