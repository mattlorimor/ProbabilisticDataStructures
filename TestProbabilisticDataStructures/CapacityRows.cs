using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;
using static TestProbabilisticDataStructures.CapacityModel;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// One row of the documented table: a structure, what its numbers count and the
    /// settings they were worked out for, and the two numbers written as table cells.
    /// </summary>
    /// <param name="Id">The structure.</param>
    /// <param name="Counts">What the numbers count, with the settings that fix them.</param>
    /// <param name="Builds">The largest the constructor builds, as the cell reads.</param>
    /// <param name="Loads">The largest that loads under the default budget, as the cell reads.</param>
    internal sealed record CapacityRow(
        StructureId Id, string Counts, Func<string> Builds, Func<string> Loads)
    {
        /// <summary>The largest that can be saved, as the cell reads.</summary>
        internal Func<string> Saves { get; init; }
    }

    /// <summary>
    /// Every row of the table of what each structure holds, worked out from the library.
    /// </summary>
    internal static partial class CapacityRows
    {
        private static CapacityRow Row(
            StructureId id, string counts, Func<string> builds, Func<string> loads) =>
            new(id, counts, builds, loads);

        /// <summary>
        /// The cell for a structure that is bounded by memory before anything in the code:
        /// whatever its constructor checks is far past what a machine holds.
        /// </summary>
        internal const string Memory = "memory";

        /// <summary>
        /// The cell for a structure that loads at every size it can be built at, which is
        /// checked at the largest.
        /// </summary>
        internal const string Always = "any";

        // ---- Searching with a model ----------------------------------------------------

        /// <summary>
        /// The most a structure sized by <paramref name="unitsOf"/> loads, as the range the
        /// line's slack allows, written to <paramref name="decimals"/> places.
        /// </summary>
        private static string LoadsAbout(
            ChargeLine line, ulong most, Func<ulong, ulong> unitsOf, int decimals = 2)
        {
            var low = Largest(1, most, n => line.Surely(unitsOf(n)));
            var high = Largest(1, most, n => line.Possibly(unitsOf(n)));
            return About(low, high, decimals);
        }

        // ---- Bloom family: sized for n items ------------------------------------------

        private static bool TryOptimalM(ulong n, double fp, out uint m)
        {
            m = 0;
            if (n > uint.MaxValue)
            {
                return false;
            }

            try
            {
                m = Utils.OptimalM((uint)n, fp);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        /// <summary>The most items <see cref="Utils.OptimalM"/> sizes a filter for.</summary>
        internal static ulong LargestSized(double fp) =>
            Largest(1, uint.MaxValue, n => TryOptimalM(n, fp, out _));

        /// <summary>
        /// The most items a 32-bit filter is built for: the most
        /// <see cref="Utils.OptimalM"/> sizes, less the few sizes at the top whose bucket
        /// array <c>Buckets</c> truncates. <paramref name="region"/> bits of the filter
        /// are not in the data array.
        /// </summary>
        internal static ulong LargestSound(double fp, int bits, uint region = 0) =>
            Largest(region + 1UL, LargestSized(fp), n =>
            {
                TryOptimalM(n, fp, out var m);
                return m > region && BucketsAreSound(m - region, bits);
            });

        private static ulong BitsOf(ulong n, double fp, int bits, uint region = 0)
        {
            TryOptimalM(n, fp, out var m);
            return m > region ? TrueBytes(m - region, bits) : 0;
        }

        internal static CapacityRow BloomFilter(double fp, string label) => Row(
            StructureId.BloomFilter, $"n, fp {label}",
            () => Exact(LargestSound(fp, 1)),
            () =>
            {
                var line = ChargeLine.Fit("BloomFilter", 64,
                    new[] { 100_000u, 200_000u, 400_000u, 800_000u }
                        .Select(n => ((long)TrueBytes(Utils.OptimalM(n, fp), 1),
                            ChargeOf(StructureId.BloomFilter, new BloomFilter(n, fp))))
                        .ToArray());
                return LoadsAbout(line, LargestSound(fp, 1), n => BitsOf(n, fp, 1));
            });

        internal static CapacityRow BloomFilter64(double fp, string label) => Row(
            StructureId.BloomFilter64, $"n, fp {label}",
            () =>
            {
                // The sizing arithmetic reaches far past any memory, and so does the most
                // the bit arrays hold, which the constructor refuses past: 2^60 bits is
                // 128 PiB.
                Assert.IsGreaterThan(0UL, Utils.OptimalM64(1UL << 50, fp));
                Assert.IsGreaterThan(1UL << 60, Buckets64.MaxCount);
                return Memory;
            },
            () =>
            {
                var line = ChargeLine.Fit("BloomFilter64", 64,
                    new[] { 100_000UL, 200_000UL, 400_000UL, 800_000UL }
                        .Select(n => ((long)TrueBytes(Utils.OptimalM64(n, fp), 1),
                            ChargeOf(StructureId.BloomFilter64, new BloomFilter64(n, fp))))
                        .ToArray())
                    // Past 2^30 buckets the bits are in more than one array, and each array
                    // is charged a few more bytes; none can be built here to measure them.
                    .Widen(5 * 48);
                return LoadsAbout(line, 1UL << 40, n => TrueBytes(Utils.OptimalM64(n, fp), 1));
            });

        internal static CapacityRow CountingBloomFilter(byte b, double fp, string label) => Row(
            StructureId.CountingBloomFilter, $"n, {b}-bit counters, fp {label}",
            () =>
            {
                // Sized in 64 bits since #232, on a 64-bit table, as BloomFilter64 is: the
                // sizing and the table both reach far past any memory.
                Assert.IsGreaterThan(0UL, Utils.OptimalM64(1UL << 50, fp));
                Assert.IsGreaterThan(1UL << 60, Buckets64.MaxCount);
                return Memory;
            },
            () =>
            {
                var line = ChargeLine.Fit("CountingBloomFilter", 64,
                    new[] { 100_000UL, 200_000UL, 400_000UL, 800_000UL }
                        .Select(n => ((long)TrueBytes(Utils.OptimalM64(n, fp), b),
                            ChargeOf(StructureId.CountingBloomFilter, new CountingBloomFilter(n, b, fp))))
                        .ToArray())
                    // Past 2^30 counters they are in more than one array, and each array is
                    // charged a few more bytes; none can be built here to measure them.
                    .Widen(5 * 48);
                return LoadsAbout(line, 1UL << 40, n => TrueBytes(Utils.OptimalM64(n, fp), b));
            });

        internal static CapacityRow DeletableBloomFilter(uint r, double fp, string label) => Row(
            StructureId.DeletableBloomFilter, $"n, r {r:N0}, fp {label}",
            () =>
            {
                // Sized in 64 bits since #232, both regions on 64-bit tables, as the counting
                // filter's counters are.
                Assert.IsGreaterThan(0UL, Utils.OptimalM64(1UL << 50, fp));
                Assert.IsGreaterThan(1UL << 60, Buckets64.MaxCount);
                return Memory;
            },
            () =>
            {
                var line = ChargeLine.Fit("DeletableBloomFilter", 64,
                    new[] { 100_000UL, 200_000UL, 400_000UL, 800_000UL }
                        .Select(n => ((long)TrueBytes(Utils.OptimalM64(n, fp) - r, 1),
                            ChargeOf(StructureId.DeletableBloomFilter, new DeletableBloomFilter(n, r, fp))))
                        .ToArray())
                    // Past 2^33 bits the data region is in more than one array, and each array
                    // is charged a few more bytes; none can be built here to measure them.
                    .Widen(5 * 48);
                return LoadsAbout(line, 1UL << 40, n => Utils.OptimalM64(n, fp) is var m && m > r ? TrueBytes(m - r, 1) : 0);
            });

        internal static CapacityRow PartitionedBloomFilter(double fp, string label) => Row(
            StructureId.PartitionedBloomFilter, $"n, fp {label}",
            () => Exact(LargestSized(fp)),
            () =>
            {
                var k = Utils.OptimalK(fp);
                ulong Bytes(ulong n)
                {
                    TryOptimalM(n, fp, out var m);
                    return k * TrueBytes((ulong)Math.Ceiling((double)m / k), 1);
                }

                var line = ChargeLine.Fit("PartitionedBloomFilter", 64 + 24 * k,
                    new[] { 100_000u, 200_000u, 400_000u, 800_000u }
                        .Select(n => ((long)Bytes(n),
                            ChargeOf(StructureId.PartitionedBloomFilter, new PartitionedBloomFilter(n, fp))))
                        .ToArray());
                return LoadsAbout(line, LargestSized(fp), Bytes);
            });

        internal static CapacityRow StableBloomFilter(byte d, double fp, string label) => Row(
            StructureId.StableBloomFilter, $"m cells, {d}-bit cells, fp {label}",
            () => Exact(Largest(1, uint.MaxValue, m => BucketsAreSound(m, d))),
            () =>
            {
                var line = ChargeLine.Fit("StableBloomFilter", 64,
                    new[] { 100_000u, 200_000u, 400_000u, 800_000u }
                        .Select(m => ((long)TrueBytes(m, d),
                            ChargeOf(StructureId.StableBloomFilter, new StableBloomFilter(m, d, fp, seed: 1))))
                        .ToArray());
                var sound = Largest(1, uint.MaxValue, m => BucketsAreSound(m, d));
                return LoadsAbout(line, sound, m => TrueBytes(m, d));
            });

        /// <summary>The rows built from the Bloom filters.</summary>
        internal static IEnumerable<CapacityRow> Bloom()
        {
            yield return BloomFilter(0.01, "1%");
            yield return BloomFilter(0.001, "0.1%");
            yield return BloomFilter64(0.01, "1%");
            yield return BloomFilter64(0.001, "0.1%");
            yield return CountingBloomFilter(4, 0.01, "1%");
            yield return CountingBloomFilter(8, 0.01, "1%");
            yield return DeletableBloomFilter(1000, 0.01, "1%");
            yield return PartitionedBloomFilter(0.01, "1%");
            yield return StableBloomFilter(1, 0.01, "1%");
            yield return StableBloomFilter(3, 0.01, "1%");
            yield return StableBloomFilter(8, 0.01, "1%");
        }
    }
}
