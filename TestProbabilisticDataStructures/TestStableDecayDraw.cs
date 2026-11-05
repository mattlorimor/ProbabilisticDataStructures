using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Where the stable filter's decay lands: the cell a run of p decrements starts at, and
    /// the cells the run covers, at cell counts far too large to build a filter of.
    /// </summary>
    /// <remarks>
    /// Each add decrements p consecutive cells from a start drawn from the generator's
    /// next 64 bits, and the stable point is worked out on the assumption that every cell
    /// is decremented with probability p/m. Whether it is depends only on how many of the
    /// 2^64 inputs start a run at each cell, which can be counted exactly without drawing
    /// anything. The draw does not decrease as its input grows, so the inputs that start
    /// a run at cell r are those from the least input reaching r up to the least reaching
    /// r + 1, and a binary search over the inputs finds each of those boundaries in 64
    /// calls of the draw.
    /// <para>
    /// The 32-bit multiply-and-shift these filters used to draw with takes only the top
    /// 32 bits of the input, so its counts are multiples of 2^32, floor(2^32 / m) or one
    /// more of them: past 2^31 cells some starts were drawn twice as often as others, and
    /// a cell, reached from p starts, was decremented at one of two rates up to 1/p
    /// apart. The 64-bit draw's counts are floor(2^64 / m) or one more, so every start
    /// and every cell is within 2^-32 of its share. Each test runs the measurement on the
    /// 32-bit draw too, restated here, and requires it to see that draw as uneven: that
    /// is what shows the measurement can see an uneven draw at the sizes it is run at,
    /// rather than passing because it cannot.
    /// </para>
    /// <para>
    /// The run's own index arithmetic is checked directly rather than through a filter.
    /// Its defect was a 32-bit sum that wrapped when m is within p of 2^32, and a filter
    /// of that size is 512 MiB whose draw would have to land in the top p of four billion
    /// starts before the sum wrapped once.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestStableDecayDraw
    {
        private static readonly UInt128 TwoTo64 = (UInt128)1 << 64;

        /// <summary>
        /// The library's draw at the sizes these tests count over, all below 2^32: the start
        /// has been a <c>ulong</c> since the filter was sized in 64 bits (#232), and below
        /// 2^32 cells it is below 2^32 too.
        /// </summary>
        private static uint DecayStart32(ulong next, uint m) => checked((uint)StableBloomFilter.DecayStart(next, m));

        /// <summary>A signed percentage, with a zero that rounds from either side shown as 0%.</summary>
        private const string Percent = "+0.####%;-0.####%;0%";

        /// <summary>
        /// Large cell counts that are not powers of two: about a billion, just past 2^31,
        /// 2^32 / 1.5 and three quarters of 2^32 (where the 32-bit draw was most uneven),
        /// the size where its effect on the false positive rate was worst for one-bit
        /// cells at 1%, the largest four-bit filter and the largest filter there is.
        /// </summary>
        private static readonly uint[] LargeCellCounts =
        {
            1_000_000_007, 2_147_483_649, 2_863_311_531, 3_221_225_472, 4_092_589_533,
            4_294_967_182, uint.MaxValue,
        };

        /// <summary>
        /// Run lengths the constructor picks: one-bit cells at 50%, 10% and 1%, and two-bit
        /// and three-bit cells at 1%.
        /// </summary>
        private static readonly uint[] RunLengths = { 1, 4, 10, 35, 85 };

        /// <summary>
        /// Four-bit cells at 1%: the shortest run the constructor picks that reaches past
        /// 2^32 from the top of the largest four-bit filter, which is 2^32 - 114 cells.
        /// Only run at the two sizes where its index can pass 2^32. Elsewhere it is no
        /// different from the others, and at three quarters of 2^32, a multiple of three,
        /// the 32-bit draw happens to be exact for it, which the vacuity check would
        /// rightly refuse.
        /// </summary>
        private const uint LongRun = 183;

        private static IEnumerable<uint> RunLengthsFor(uint m) =>
            m > uint.MaxValue - LongRun ? RunLengths.Append(LongRun) : RunLengths;

        /// <summary>
        /// The 32-bit draw, restated: the top 32 bits of the input, multiplied by m and
        /// shifted down.
        /// </summary>
        internal static uint Narrow(ulong next, uint m) =>
            (uint)(((ulong)(uint)(next >> 32) * m) >> 32);

        /// <summary>
        /// How many of the 2^64 inputs start a run at each cell, for one draw and one m,
        /// with the boundaries found so far kept for reuse. VarOpt's eviction draw is
        /// counted with this too, in <see cref="TestVarOptEvictionDraw"/>.
        /// </summary>
        internal sealed class InputCounts
        {
            private readonly Func<ulong, uint, uint> draw;
            private readonly uint m;
            private readonly Dictionary<ulong, UInt128> first = new();

            public InputCounts(Func<ulong, uint, uint> draw, uint m)
            {
                this.draw = draw;
                this.m = m;
            }

            /// <summary>The number of inputs whose run starts at <paramref name="r"/>.</summary>
            public BigInteger At(ulong r) => (BigInteger)(this.First(r + 1) - this.First(r));

            /// <summary>
            /// The least input whose start is at least <paramref name="r"/>: zero for the
            /// first cell and 2^64 past the last. The search relies on the draw not
            /// decreasing, so the boundary it finds is checked on both sides.
            /// </summary>
            private UInt128 First(ulong r)
            {
                if (r == 0)
                {
                    return 0;
                }

                if (r >= this.m)
                {
                    return TwoTo64;
                }

                if (this.first.TryGetValue(r, out var found))
                {
                    return found;
                }

                UInt128 lo = 0, hi = TwoTo64;
                while (lo < hi)
                {
                    var mid = lo + (hi - lo) / 2;
                    if (this.draw((ulong)mid, this.m) >= r)
                    {
                        hi = mid;
                    }
                    else
                    {
                        lo = mid + 1;
                    }
                }

                if (lo < TwoTo64)
                {
                    Assert.IsGreaterThanOrEqualTo((uint)r, this.draw((ulong)lo, this.m),
                        $"m = {this.m}: the boundary found for start {r} does not reach it");
                }

                if (lo > 0)
                {
                    Assert.IsLessThan((uint)r, this.draw((ulong)(lo - 1), this.m),
                        $"m = {this.m}: the input before the boundary found for start {r} " +
                        "already reaches it, so the draw is not ordered and this count is wrong");
                }

                this.first[r] = lo;
                return lo;
            }
        }

        /// <summary>
        /// The starts a test looks at: the first and last 64 cells, and 64 more spread
        /// across the rest.
        /// </summary>
        internal static IEnumerable<ulong> SampledStarts(uint m)
        {
            for (ulong r = 0; r < 64; r++)
            {
                yield return r;
                yield return m - 1 - r;
            }

            for (ulong k = 1; k <= 64; k++)
            {
                yield return k * (m / 65) + k;
            }
        }

        /// <summary>
        /// How far a count of inputs is from an exact share of 2^64, as count * m - share,
        /// where the share is <paramref name="cells"/> * 2^64: a count within one input of
        /// its share is under m away. Over 2^64, it is the error as a fraction of the share.
        /// </summary>
        internal static BigInteger Excess(BigInteger count, uint m, uint cells) =>
            count * m - (BigInteger)cells * (BigInteger)TwoTo64;

        private static double Relative(BigInteger excess, uint cells) =>
            (double)excess / ((double)cells * Math.Pow(2, 64));

        /// <summary>
        /// The starts whose count is more than one input from 2^64 / m, with the worst
        /// relative error among them.
        /// </summary>
        internal static (int Uneven, int Checked, double Worst) StartsOffTheirShare(
            Func<ulong, uint, uint> draw, uint m)
        {
            var counts = new InputCounts(draw, m);
            int uneven = 0, seen = 0;
            var worst = 0.0;
            foreach (var r in SampledStarts(m).Distinct())
            {
                seen++;
                var excess = Excess(counts.At(r), m, 1);
                if (BigInteger.Abs(excess) >= m)
                {
                    uneven++;
                }

                var relative = Relative(excess, 1);
                if (Math.Abs(relative) > Math.Abs(worst))
                {
                    worst = relative;
                }
            }

            return (uneven, seen, worst);
        }

        /// <summary>
        /// Each start is drawn by floor(2^64 / m) of the 2^64 inputs or by one more, so it
        /// is within 2^-32 of its share of the draw -- at every large cell count, not only
        /// at a power of two. The 32-bit draw had each start within one of 2^32 inputs of
        /// its share of those, which at three quarters of 2^32 cells is a factor of two
        /// between starts.
        /// </summary>
        [TestMethod]
        public void TestADecayRunStartsAtEachCellEqually()
        {
            var failures = new List<string>();
            foreach (var m in LargeCellCounts)
            {
                var (uneven, seen, worst) = StartsOffTheirShare(DecayStart32, m);
                if (uneven > 0)
                {
                    failures.Add($"m = {m:N0}: {uneven} of {seen} starts more than one input " +
                        $"from 2^64 / m, worst {worst.ToString(Percent)} of its share");
                }

                // The measurement sees an uneven draw: every sampled start of the 32-bit
                // one is off its share by a multiple of 2^32 inputs, which is more than m.
                var narrow = StartsOffTheirShare(Narrow, m);
                Assert.AreEqual(narrow.Checked, narrow.Uneven,
                    $"m = {m:N0}: the 32-bit draw was measured as even at " +
                    $"{narrow.Checked - narrow.Uneven} starts, so this measurement cannot " +
                    "see the defect it is looking for");
            }

            // ...and an even one: at a power of two both draws give every start exactly
            // 2^64 / m inputs.
            const uint powerOfTwo = 1u << 31;
            foreach (var draw in new Func<ulong, uint, uint>[] { DecayStart32, Narrow })
            {
                var counts = new InputCounts(draw, powerOfTwo);
                foreach (var r in SampledStarts(powerOfTwo))
                {
                    Assert.AreEqual(BigInteger.Zero, Excess(counts.At(r), powerOfTwo, 1),
                        $"at 2^31 cells start {r} was not drawn by exactly 2^33 inputs");
                }
            }

            Assert.IsEmpty(failures, "the decay start is not drawn evenly:\n" +
                string.Join("\n", failures));
        }

        /// <summary>
        /// The cells whose decrement count is more than one input from p * 2^64 / m, the
        /// rates seen among them, and how many runs crossed the last cell and how many
        /// crossed 2^32 on the way.
        /// </summary>
        private static (int Uneven, int Checked, SortedSet<double> Rates, int Wrapped, int PastTwoTo32)
            CellsOffTheirShare(Func<ulong, uint, uint> draw, uint m, uint p)
        {
            var mid = (ulong)m / 2;
            var targets = new HashSet<ulong>();
            for (ulong j = 0; j < 2 * (ulong)p; j++)
            {
                targets.Add(j);
                targets.Add(m - 1 - j);
                targets.Add(mid + j);
            }

            // Every start that reaches a target, wherever the run's indexing sends it: the
            // top 3p, which includes every run that crosses the last cell, the first 2p and
            // a window around the middle.
            var starts = new HashSet<ulong>();
            for (ulong s = 0; s < 3 * (ulong)p; s++)
            {
                starts.Add(m - 1 - s);
            }

            for (ulong s = 0; s < 2 * (ulong)p; s++)
            {
                starts.Add(s);
            }

            for (var s = mid - p; s < mid + 2 * (ulong)p; s++)
            {
                starts.Add(s);
            }

            var counts = new InputCounts(draw, m);
            var reached = targets.ToDictionary(j => j, _ => BigInteger.Zero);
            int wrapped = 0, pastTwoTo32 = 0;
            foreach (var r in starts)
            {
                var count = counts.At(r);
                if (r + p - 1 >= m)
                {
                    wrapped++;
                }

                if (r + p - 1 >= 1UL << 32)
                {
                    pastTwoTo32++;
                }

                for (uint i = 0; i < p; i++)
                {
                    var cell = StableBloomFilter.DecayCell((uint)r, i, m);
                    if (reached.ContainsKey(cell))
                    {
                        reached[cell] += count;
                    }
                }
            }

            int uneven = 0;
            var rates = new SortedSet<double>();
            foreach (var (_, count) in reached)
            {
                var excess = Excess(count, m, p);
                if (BigInteger.Abs(excess) >= m)
                {
                    uneven++;
                    rates.Add(Math.Round(Relative(excess, p), 6));
                }
            }

            return (uneven, reached.Count, rates, wrapped, pastTwoTo32);
        }

        /// <summary>
        /// Every cell looked at is decremented by p * 2^64 / m of the 2^64 inputs to within
        /// one, so within 2^-32 / p of p/m as a share of it: at the start of the filter,
        /// where the runs that cross the last cell land, at its end and in its middle.
        /// With the 32-bit draw a cell's count was floor(p * 2^32 / m) or one more times
        /// 2^32, two rates up to 1/p apart, and within p of 2^32 cells the 32-bit sum of a
        /// run's index wrapped and sent decrements to the wrong cells.
        /// </summary>
        [TestMethod]
        public void TestCellsAreDecayedAtTheSameRateAtLargeCellCounts()
        {
            var failures = new List<string>();
            var wrappedRuns = 0;
            var runsPastTwoTo32 = 0;
            foreach (var m in LargeCellCounts)
            {
                foreach (var p in RunLengthsFor(m))
                {
                    var cells = CellsOffTheirShare(DecayStart32, m, p);
                    wrappedRuns += cells.Wrapped;
                    runsPastTwoTo32 += cells.PastTwoTo32;
                    if (cells.Uneven > 0)
                    {
                        failures.Add($"m = {m:N0}, p = {p}: {cells.Uneven} of {cells.Checked} " +
                            "cells more than one input from p * 2^64 / m, at " +
                            $"{cells.Rates.Count} rates from {cells.Rates.Min.ToString(Percent)} " +
                            $"to {cells.Rates.Max.ToString(Percent)} of p/m");
                    }

                    // The measurement sees an uneven draw: the 32-bit one, through the same
                    // run indexing, misses its share at every cell looked at.
                    var narrow = CellsOffTheirShare(Narrow, m, p);
                    Assert.AreEqual(narrow.Checked, narrow.Uneven,
                        $"m = {m:N0}, p = {p}: the 32-bit draw was measured as even at " +
                        $"{narrow.Checked - narrow.Uneven} cells, so this measurement cannot " +
                        "see the defect it is looking for");
                }
            }

            // The runs that cross the last cell were counted, and so were runs whose index
            // passes 2^32, where a 32-bit sum wraps.
            Assert.IsGreaterThan(0, wrappedRuns, "no run crossed the last cell");
            Assert.IsGreaterThan(0, runsPastTwoTo32, "no run's index passed 2^32");

            Assert.IsEmpty(failures, "cells are not decayed at the same rate:\n" +
                string.Join("\n", failures));
        }

        /// <summary>
        /// An add decrements exactly the run <see cref="StableBloomFilter.DecayStart"/> and
        /// <see cref="StableBloomFilter.DecayCell"/> name for the generator's next draw,
        /// and nothing else, so the tests above, which call those two directly, vouch for
        /// the decay an add does.
        /// </summary>
        /// <remarks>
        /// The 32-bit draw and the 64-bit one agree on all but about m / 2^33 of draws, so
        /// a filter that drew some other way would agree with this one almost always. Seed
        /// 7660 at 1,000,003 cells is the first seed whose first draw starts a run one cell
        /// later than the 32-bit draw did: at 885,279, not 885,278, found by searching the
        /// seeds in order. The test checks that this is still so before relying on it.
        /// Every cell starts at its maximum, so the run is the cells one below it, apart
        /// from the add's own cells, which it sets back to the maximum.
        /// </remarks>
        [TestMethod]
        public void TestAnAddDecaysTheRunDecayStartNames()
        {
            const uint m = 1_000_003;
            const int seed = 7660;
            var f = new StableBloomFilter(m, 2, 0.01, seed: seed);
            var max = f.Max;
            for (uint c = 0; c < m; c++)
            {
                f.cells.Set(c, max);
            }

            var next = new SeededRandom((ulong)seed).Next();
            var start = StableBloomFilter.DecayStart(next, m);
            Assert.AreEqual(Narrow(next, m) + 1, start,
                "this seed's first draw no longer starts a run one cell after the 32-bit draw's, " +
                "so it cannot tell the two apart");

            var run = new HashSet<uint>();
            for (uint i = 0; i < f.P(); i++)
            {
                run.Add(checked((uint)StableBloomFilter.DecayCell(start, i, m)));
            }

            f.Add(new byte[] { 1, 9, 1 });

            var decayed = new List<uint>();
            for (uint c = 0; c < m; c++)
            {
                var value = f.cells.Get(c);
                if (value == max)
                {
                    continue;
                }

                Assert.IsTrue(run.Contains(c) && value == max - 1u,
                    $"cell {c:N0} holds {value} after one add, where the run from {start:N0} " +
                    $"leaves {max - 1} in its {f.P()} cells and {max} everywhere else");
                decayed.Add(c);
            }

            // The add sets its k cells back to the maximum after the decay, so up to k of
            // the run can show none.
            Assert.IsGreaterThanOrEqualTo((int)(f.P() - f.K()), decayed.Count,
                $"only {decayed.Count} of the run's {f.P()} cells were decremented");
            Assert.IsTrue(decayed.Contains(checked((uint)start)),
                $"the run's first cell, {start:N0}, was not decremented");
        }

        /// <summary>
        /// The i-th cell of a run that starts at r is r + i, less m once that passes the
        /// last cell: at the end of the largest filters, where r + i passes 2^32, as well as
        /// at sizes where it cannot. Run lengths reach past the longest the constructor picks
        /// for a filter that size, which is a few hundred.
        /// </summary>
        [TestMethod]
        public void TestADecayRunWrapsAtTheLastCell()
        {
            var sizes = new uint[] { uint.MaxValue, uint.MaxValue - 1, 4_294_967_182, 3_221_225_472, 1_000, 7 };
            var wrong = new List<string>();
            int crossedLast = 0, crossedTwoTo32 = 0;
            foreach (var m in sizes)
            {
                foreach (var r in new ulong[] { m - 1, m - 2, m - 3, m / 2, 0 }.Where(r => r < m))
                {
                    for (uint i = 0; i < 600 && i < m; i++)
                    {
                        var sum = r + i;
                        var expected = sum < m ? sum : sum - m;
                        if (sum >= m)
                        {
                            crossedLast++;
                        }

                        if (sum >= 1UL << 32)
                        {
                            crossedTwoTo32++;
                        }

                        var got = StableBloomFilter.DecayCell((uint)r, i, m);
                        if (got != expected)
                        {
                            wrong.Add($"m = {m:N0}, start {r:N0}, i = {i}: cell {got:N0}, not {expected:N0}");
                        }
                    }
                }
            }

            Assert.IsGreaterThan(0, crossedLast, "no run crossed the last cell");
            Assert.IsGreaterThan(0, crossedTwoTo32, "no run's index passed 2^32");
            Assert.IsEmpty(wrong, $"{wrong.Count} run cells in the wrong place, the first " +
                $"{Math.Min(5, wrong.Count)}:\n" + string.Join("\n", wrong.Take(5)));
        }
    }
}
