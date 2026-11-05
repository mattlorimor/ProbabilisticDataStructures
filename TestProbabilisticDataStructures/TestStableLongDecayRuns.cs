using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// A stable filter whose decay run is as long as the filter or longer: p at least m.
    /// </summary>
    /// <remarks>
    /// The constructor picks p from 1/k - 1/m, which is zero for a filter with as many
    /// hash functions as cells -- three cells at 1%, or ten at one in a million -- and p
    /// came out as 4,294,967,295. An add then decremented that many cells one at a time,
    /// round and round a ring of a few, which is billions of steps per add: in practice a
    /// filter that never finishes its first add. A payload says what p is and the reader
    /// takes it, so a payload holding such a p, which 6.2.0 writes for such a filter,
    /// read back as one too.
    /// <para>
    /// Nothing is refused. A run of p cells around a ring of m takes every cell down
    /// floor(p / m) times and the p mod m cells from its start once more, each no lower
    /// than zero, and the decay now does exactly that, in at most min(p, m) cell updates.
    /// These tests hold it to the loop: by running the loop beside it where the loop is
    /// short, and, where it is billions of steps long, by what any run that takes every
    /// cell down at least its maximum number of times leaves -- every cell at zero.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestStableLongDecayRuns
    {
        private const int PayloadStart = 14;
        // Version 14: u64 m, u32 k, then u32 p.
        private const int POffset = 12;

        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        /// <summary>The cells the filter's own add sets for <paramref name="data"/>.</summary>
        private static HashSet<uint> CellsOf(StableBloomFilter f, byte[] data)
        {
            var positions = Positions64.Of(data, Defaults.GetDefaultHashFunction(), f.Placement, f.Cells());
            var cells = new HashSet<uint>();
            for (uint i = 0; i < f.K(); i++)
            {
                cells.Add(checked((uint)positions[i]));
            }

            return cells;
        }

        /// <summary>
        /// An add to a filter with as many hash functions as cells ends, and leaves what
        /// the loop would have: every cell taken down billions of times, which is to zero,
        /// and then the item's own cells set to the maximum. Each filter's cells start at
        /// the maximum, so a cell the decay missed would show.
        /// </summary>
        [TestMethod]
        [Timeout(15000)]
        public void TestAnAddToAFilterWithAsManyHashFunctionsAsCellsEnds()
        {
            int emptied = 0, kept = 0;
            foreach (var (m, d, fp) in new (uint, byte, double)[] { (3, 1, 0.01), (10, 2, 1e-6) })
            {
                var f = new StableBloomFilter(m, d, fp);
                Assert.AreEqual(m, f.K(), $"{m} cells at {fp} no longer has as many hash functions as cells");
                Assert.AreEqual(uint.MaxValue, f.P(),
                    $"{m} cells at {fp} no longer decrements 4,294,967,295 cells an add");

                foreach (var word in new[] { "alpha", "beta", "gamma" })
                {
                    for (uint c = 0; c < m; c++)
                    {
                        f.cells.Set(c, f.Max);
                    }

                    var item = Key(word);
                    var own = CellsOf(f, item);
                    f.Add(item);

                    for (uint c = 0; c < m; c++)
                    {
                        var expected = own.Contains(c) ? f.Max : 0u;
                        Assert.AreEqual(expected, f.cells.Get(c),
                            $"{m} cells, after adding {word}: cell {c}");
                        if (expected == 0)
                        {
                            emptied++;
                        }
                        else
                        {
                            kept++;
                        }
                    }
                }
            }

            Assert.IsGreaterThan(0, emptied, "no cell outside an item's own was looked at");
            Assert.IsGreaterThan(0, kept, "no item's own cell was looked at");
        }

        /// <summary>
        /// The plain loop the decay replaces: p cells from the start, each down by one and
        /// no lower than zero.
        /// </summary>
        private static void PlainLoop(Buckets cells, uint m, ulong p, uint start)
        {
            for (ulong i = 0; i < p; i++)
            {
                cells.Increment((uint)((start + i) % m), -1);
            }
        }

        private static Buckets Filled(uint m, byte d, Random random)
        {
            var cells = new Buckets(m, d);
            var max = cells.MaxBucketValue();
            for (uint c = 0; c < m; c++)
            {
                cells.Set(c, (byte)random.Next(max + 1));
            }

            return cells;
        }

        /// <summary>
        /// A payload saying the run is longer than the filter, with its checksum resealed,
        /// reads, and an add to it does what the loop would: at 107 decrements an add
        /// over 50 two-bit cells, which goes round twice and seven cells more, and at
        /// 163, which takes every cell down at least three times, by running the loop
        /// beside it; and at 4,294,967,295, the p 6.2.0 writes for a filter with as many
        /// hash functions as cells, by every cell ending at zero apart from the item's.
        /// </summary>
        [TestMethod]
        [Timeout(15000)]
        public void TestAPayloadWithARunLongerThanTheFilterReadsAndAdds()
        {
            const uint m = 50;
            var source = new StableBloomFilter(m, 2, 0.01, seed: 5);
            for (var i = 0; i < 40; i++)
            {
                source.Add(Key($"word-{i}"));
            }

            var stored = source.ToByteArray();
            Assert.AreEqual(m, BinaryPrimitives.ReadUInt32LittleEndian(stored.AsSpan(PayloadStart)),
                "the cell count is no longer the payload's first field");
            Assert.AreEqual(source.P(), BinaryPrimitives.ReadUInt32LittleEndian(stored.AsSpan(PayloadStart + POffset)),
                "p is no longer the payload's third field");

            var nonzero = 0;
            foreach (var p in new[] { 2 * m + 7, 3 * m + 13, uint.MaxValue })
            {
                var bytes = (byte[])stored.Clone();
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(PayloadStart + POffset), p);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4),
                    Crc32.HashToUInt32(bytes.AsSpan(4, bytes.Length - 8)));

                var f = Persistence.FromByteArray<StableBloomFilter>(bytes);
                Assert.AreEqual(p, f.P(), "the payload's p was not the one read");

                // What the add will do, worked out beside it: the decay from the start the
                // stored generator's next draw gives, then the item's cells set.
                var state = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bytes.Length - 20));
                var start = StableBloomFilter.DecayStart(new SeededRandom(state).Next(), m);
                var expected = new Buckets(m, 2);
                for (uint c = 0; c < m; c++)
                {
                    expected.Set(c, (byte)f.cells.Get(c));
                    if (f.cells.Get(c) != 0)
                    {
                        nonzero++;
                    }
                }

                if (p < 1_000_000)
                {
                    PlainLoop(expected, m, p, checked((uint)start));
                }
                else
                {
                    // Billions of decrements over 50 cells take each one down far more
                    // than its maximum of three times.
                    for (uint c = 0; c < m; c++)
                    {
                        expected.Set(c, 0);
                    }
                }

                var item = Key("after");
                foreach (var c in CellsOf(f, item))
                {
                    expected.Set(c, expected.MaxBucketValue());
                }

                f.Add(item);
                for (uint c = 0; c < m; c++)
                {
                    Assert.AreEqual(expected.Get(c), f.cells.Get(c), $"p = {p:N0}: cell {c}");
                }
            }

            Assert.IsGreaterThan(0, nonzero, "the payload's cells were all empty, so no decay could show");
        }

        /// <summary>
        /// The decay is the plain loop, cell for cell, at every combination tried of cell
        /// width, cell count, run length and start: runs shorter than the filter, as long
        /// as it, longer, and long enough to take every cell down by its maximum or more,
        /// where a run capped at m would leave cells the loop empties.
        /// </summary>
        [TestMethod]
        public void TestALongDecayRunIsThePlainLoop()
        {
            var random = new Random(191);
            int shorter = 0, equal = 0, longer = 0, emptying = 0, differed = 0;
            foreach (byte d in new byte[] { 1, 2, 3, 4, 8 })
            {
                foreach (uint m in new uint[] { 1, 2, 3, 7, 10, 64 })
                {
                    var max = (1u << d) - 1;
                    var lengths = new ulong[]
                    {
                        0, 1, m - 1, m, m + 1, 2 * m + 3, m * max - 1, m * max, m * max + 5,
                        3 * m * max + 1,
                    }.Distinct();
                    foreach (var p in lengths)
                    {
                        foreach (var start in new uint[] { 0, 1, m - 1, m / 2 }.Where(s => s < m).Distinct())
                        {
                            var loop = Filled(m, d, random);
                            var decay = new Buckets64(m, d);
                            for (uint c = 0; c < m; c++)
                            {
                                decay.Set(c, (byte)loop.Get(c));
                            }

                            var before = Enumerable.Range(0, (int)m).Select(c => loop.Get((uint)c)).ToArray();
                            PlainLoop(loop, m, p, start);
                            StableBloomFilter.DecayRun(decay, m, (uint)p, start);

                            for (uint c = 0; c < m; c++)
                            {
                                Assert.AreEqual(loop.Get(c), decay.Get(c),
                                    $"d = {d}, m = {m}, p = {p}, start {start}: cell {c}");
                            }

                            if (Enumerable.Range(0, (int)m).Any(c => loop.Get((uint)c) != before[c]))
                            {
                                differed++;
                            }

                            if (p < m)
                            {
                                shorter++;
                            }
                            else if (p == m)
                            {
                                equal++;
                            }
                            else
                            {
                                longer++;
                            }

                            if (p >= m * max)
                            {
                                emptying++;
                            }
                        }
                    }
                }
            }

            Assert.IsGreaterThan(0, shorter, "no run was shorter than its filter");
            Assert.IsGreaterThan(0, equal, "no run was as long as its filter");
            Assert.IsGreaterThan(0, longer, "no run was longer than its filter");
            Assert.IsGreaterThan(0, emptying, "no run took every cell down by its maximum");
            Assert.IsGreaterThan(0, differed, "no run changed a cell, so the comparison saw nothing");
        }

        /// <summary>
        /// An add, through <c>Add</c> or <c>TestAndAdd</c>, makes min(p, m) cell updates:
        /// p for an ordinary filter, as the loop always made, and m for one whose run is
        /// longer than the filter.
        /// </summary>
        /// <remarks>
        /// The counts are exact, since the work is. A decay that visited every cell on
        /// every add would give every answer right, because a cell outside the run is
        /// updated by nothing, and would cost a filter of a million cells a million
        /// updates an add. Only a count can tell.
        /// </remarks>
        [TestMethod]
        public void TestAnAddMakesMinOfPAndMCellUpdates()
        {
            foreach (var (f, expected) in new (StableBloomFilter, uint)[]
            {
                (new StableBloomFilter(1000, 2, 0.01, seed: 1), 0),
                (new StableBloomFilter(3, 1, 0.01), 3),
                (new StableBloomFilter(10, 2, 1e-6), 10),
            })
            {
                var perAdd = expected == 0 ? f.P() : expected;
                if (expected == 0)
                {
                    Assert.IsLessThan(f.Cells(), f.P(), "the ordinary filter's run is not shorter than it");
                    Assert.IsGreaterThan(0u, f.P(), "the ordinary filter decays nothing");
                }
                else
                {
                    Assert.IsGreaterThan(f.Cells(), f.P(), $"{f.Cells()} cells: the run is not longer than the filter");
                }

                for (var i = 0; i < 60; i++)
                {
                    f.Add(Key($"add-{i}"));
                    f.TestAndAdd(Key($"test-and-add-{i}"));
                }

                Assert.AreEqual(120L * perAdd, f.DecayUpdates,
                    $"{f.Cells()} cells, p = {f.P():N0}: cell updates over 120 adds");
            }
        }

        /// <summary>
        /// Runs far too long for the loop to run beside them take every cell down far more
        /// than its maximum, which is to zero -- including a single cell taken down
        /// 4,294,967,295 times, a count no int holds.
        /// </summary>
        /// <remarks>
        /// A filter of one cell cannot show this: its one cell is also the one its add sets
        /// back to the maximum. So it is held here, on the run itself.
        /// </remarks>
        [TestMethod]
        public void TestARunTooLongToStepEmptiesEveryCell()
        {
            foreach (byte d in new byte[] { 1, 2, 8 })
            {
                foreach (uint m in new uint[] { 1, 2, 3, 10 })
                {
                    foreach (var p in new uint[] { (uint)int.MaxValue + 1, uint.MaxValue })
                    {
                        var cells = new Buckets64(m, d);
                        for (uint c = 0; c < m; c++)
                        {
                            cells.Set(c, cells.MaxBucketValue());
                        }

                        StableBloomFilter.DecayRun(cells, m, p, m - 1);
                        for (uint c = 0; c < m; c++)
                        {
                            Assert.AreEqual(0u, cells.Get(c), $"d = {d}, m = {m}, p = {p:N0}: cell {c}");
                        }
                    }
                }
            }
        }
    }
}
