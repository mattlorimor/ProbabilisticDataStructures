using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestCountSketch
    {
        /// <summary>
        /// States whose payload <see cref="TestPayloadCount"/> holds the count to: the length
        /// is the shape's, so the coarsest and a deeper one, and cells of either sign.
        /// </summary>
        internal static System.Collections.Generic.IEnumerable<PayloadState> PayloadStates()
        {
            yield return PayloadState.Of("4 x 1, empty", new CountSketch(0.5, 0.5));

            var coarse = new CountSketch(0.5, 0.5);
            for (var i = 0; i < 40; i++) coarse.Add(Key($"w{i}"), i % 5 - 2);
            yield return PayloadState.Of("4 x 1, counts of either sign", coarse);

            var deep = new CountSketch(0.1, 0.01);
            for (var i = 0; i < 200; i++) deep.Add(Key($"w{i}"), i % 7 - 3);
            yield return PayloadState.Of("100 x 5, counts of either sign", deep);
        }

        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        [TestMethod]
        public void TestANewSketchHasSeenNothing()
        {
            var sketch = new CountSketch(0.01, 0.01);

            Assert.AreEqual(0L, sketch.Count(Key("anything")));
        }

        /// <summary>
        /// The width is 1 / epsilon squared, which passes what an array holds long before
        /// epsilon is small: an epsilon of 1e-5 wants ten billion counters. The guard on
        /// epsilon checked the width of a Count-Min Sketch, e / epsilon, so this width was
        /// cast to a uint that saturated and then failed in the allocation. It is refused,
        /// naming epsilon, as is an epsilon of infinity, whose width is none.
        /// </summary>
        [TestMethod]
        public void TestAWidthNoArrayHoldsIsRefusedBeforeAnythingIsAllocated()
        {
            var epsilons = new[]
            {
                // The width is Array.MaxLength + 1.
                1 / Math.Sqrt(Array.MaxLength + 0.5),
                1e-5,
                1e-7,
                double.PositiveInfinity,
            };

            foreach (var epsilon in epsilons)
            {
                var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => new CountSketch(epsilon, 0.5), $"an epsilon of {epsilon}");
                Assert.AreEqual("epsilon", ex.ParamName);
            }
        }

        /// <summary>
        /// A delta whose reciprocal is infinite has no depth, and used to fail converting
        /// it to a row count. The deepest sketch is 710 rows, and still builds.
        /// </summary>
        [TestMethod]
        public void TestADeltaWithNoReciprocalIsRefused()
        {
            foreach (var delta in new[] { double.Epsilon, 1e-310, 5.5e-309 })
            {
                var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => new CountSketch(0.5, delta), $"a delta of {delta}");
                Assert.AreEqual("delta", ex.ParamName);
            }

            foreach (var delta in new[] { 5.6e-309, 1e-308 })
            {
                var deepest = new CountSketch(0.5, delta);
                Assert.AreEqual(710u, deepest.Depth(), $"a delta of {delta}");
                deepest.Add(Key("a"), 3);
                Assert.AreEqual(3L, deepest.Count(Key("a")));
            }
        }

        [TestMethod]
        public void TestSomethingAddedIsCounted()
        {
            var sketch = new CountSketch(0.01, 0.01);

            for (var i = 0; i < 500; i++)
            {
                sketch.Add(Key("alpha"));
            }

            Assert.AreEqual(500L, sketch.Count(Key("alpha")));
        }
        /// <summary>
        /// The reason to have this beside <see cref="CountMinSketch"/>.
        /// <para>
        /// A Count-Min Sketch takes the minimum of cells that collisions can only have
        /// pushed up, so its error grows with the <b>total weight</b> of the stream. Ask
        /// it about something rare in a stream carrying two million observations and the
        /// answer is mostly other items. This takes a median of signed cells, so
        /// collisions cancel rather than accumulate.
        /// </para>
        /// <para>
        /// The two size differently for the same epsilon -- Count-Min bounds its error
        /// against the stream's L1 norm and this one against its L2 norm -- so comparing
        /// them at equal epsilon would hand one of them several times the memory. These
        /// are matched on <b>shape</b>, and the test asserts that before comparing
        /// anything.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestARareItemIsNotInflatedByTheRestOfTheStream()
        {
            var countMin = new CountMinSketch(0.001, 0.01);
            var countSketch = new CountSketch(0.0192, 0.01);

            Assert.AreEqual(countMin.Depth, countSketch.Depth(), "the two are not the same shape");
            Assert.IsLessThan(1.05, (double)countSketch.Width() / countMin.Width,
                $"widths differ too much to compare: {countMin.Width} against {countSketch.Width()}");

            // Twenty thousand items into about 2,700 columns, so a collision is the
            // situation rather than bad luck.
            for (var item = 0; item < 20_000; item++)
            {
                var key = Key($"item{item}");
                for (var i = 0; i < 100; i++)
                {
                    countMin.Add(key);
                    countSketch.Add(key);
                }
            }

            for (var i = 0; i < 10; i++)
            {
                countMin.Add(Key("rare"));
                countSketch.Add(Key("rare"));
            }

            var minError = Math.Abs((long)countMin.Count(Key("rare")) - 10);
            var sketchError = Math.Abs(countSketch.Count(Key("rare")) - 10);

            Assert.IsGreaterThan(100L, minError,
                $"Count-Min was only off by {minError} here, so this comparison no longer " +
                "demonstrates anything -- find a stream where its bias shows");

            Assert.IsLessThan(minError / 2, sketchError,
                $"asked about an item seen 10 times among 2,000,000 observations: " +
                $"Count-Min was off by {minError}, this was off by {sketchError}");
        }

        /// <summary>
        /// And where the stream really is dominated by a few heavy items, Count-Min is
        /// accurate about those -- which is what stops the test above from being read as
        /// "Count-Min is worse". It is not. It is worse at one thing.
        /// </summary>
        [TestMethod]
        public void TestBothAreAccurateAboutAGenuineHeavyHitter()
        {
            var countMin = new CountMinSketch(0.001, 0.01);
            var countSketch = new CountSketch(0.0192, 0.01);

            for (var item = 0; item < 20_000; item++)
            {
                var key = Key($"item{item}");
                var weight = item < 50 ? 2000 : 1;
                for (var i = 0; i < weight; i++)
                {
                    countMin.Add(key);
                    countSketch.Add(key);
                }
            }

            Assert.IsLessThan(200L, Math.Abs((long)countMin.Count(Key("item0")) - 2000));
            Assert.IsLessThan(200L, Math.Abs(countSketch.Count(Key("item0")) - 2000));
        }

        /// <summary>
        /// Errors go both ways, which is what "unbiased" means and what a Count-Min
        /// Sketch cannot do -- its estimate is never below the truth.
        /// </summary>
        [TestMethod]
        public void TestErrorsFallOnBothSidesOfTheTruth()
        {
            var sketch = new CountSketch(0.01, 0.01);

            for (var item = 0; item < 20_000; item++)
            {
                sketch.Add(Key($"item{item}"));
            }

            var over = 0;
            var under = 0;
            for (var item = 0; item < 20_000; item++)
            {
                var estimate = sketch.Count(Key($"item{item}"));
                if (estimate > 1) over++;
                if (estimate < 1) under++;
            }

            Assert.IsGreaterThan(0, under,
                "no estimate ever fell below the truth, which is a one-sided error and " +
                "the thing CountMinSketch already does");
            Assert.IsGreaterThan(0, over);
        }

        /// <summary>
        /// Removal, which a Count-Min Sketch cannot do: its cells can only rise, so
        /// subtracting from one that a collision inflated takes the count below where it
        /// should be and leaves it there.
        /// </summary>
        [TestMethod]
        public void TestThingsCanBeRemovedAgain()
        {
            var sketch = new CountSketch(0.01, 0.01);

            sketch.Add(Key("alpha"), 500);
            Assert.AreEqual(500L, sketch.Count(Key("alpha")));

            sketch.Add(Key("alpha"), -200);
            Assert.AreEqual(300L, sketch.Count(Key("alpha")));

            sketch.Add(Key("alpha"), -300);
            Assert.AreEqual(0L, sketch.Count(Key("alpha")));

            // And past zero, since a stream of departures is as real as one of arrivals.
            sketch.Add(Key("alpha"), -50);
            Assert.AreEqual(-50L, sketch.Count(Key("alpha")));
        }

        [TestMethod]
        public void TestMergeCountsBothStreams()
        {
            var a = new CountSketch(0.01, 0.01);
            var b = new CountSketch(0.01, 0.01);

            a.Add(Key("shared"), 300);
            b.Add(Key("shared"), 200);
            b.Add(Key("onlyB"), 400);

            a.Merge(b);

            Assert.AreEqual(500L, a.Count(Key("shared")));
            Assert.AreEqual(400L, a.Count(Key("onlyB")));

            // The sketch merged in is untouched.
            Assert.AreEqual(200L, b.Count(Key("shared")));
        }

        [TestMethod]
        public void TestMergeRejectsADifferentShape()
        {
            var ex = Assert.ThrowsExactly<ArgumentException>(
                () => new CountSketch(0.01, 0.01).Merge(new CountSketch(0.05, 0.01)));
            StringAssert.Contains(ex.Message, "must match");

            Assert.ThrowsExactly<ArgumentNullException>(
                () => new CountSketch(0.01, 0.01).Merge(null!));
        }

        [TestMethod]
        public void TestRoundTripsThroughPersistence()
        {
            var sketch = new CountSketch(0.05, 0.01);
            for (var i = 0; i < 2000; i++)
            {
                sketch.Add(Key($"k{i}"), i % 7);
            }

            // A negative cell, which is the case a sketch storing unsigned counts could
            // not carry back.
            sketch.Add(Key("negative"), -5000);

            var restored = Persistence.FromByteArray<CountSketch>(sketch.ToByteArray());

            Assert.AreEqual(sketch.Width(), restored.Width());
            Assert.AreEqual(sketch.Depth(), restored.Depth());
            Assert.AreEqual(sketch.Count(Key("negative")), restored.Count(Key("negative")));

            for (var i = 0; i < 2000; i++)
            {
                Assert.AreEqual(sketch.Count(Key($"k{i}")), restored.Count(Key($"k{i}")),
                    $"the restored sketch disagreed about k{i}");
            }

            // And keeps counting.
            sketch.Add(Key("later"), 9);
            restored.Add(Key("later"), 9);
            Assert.AreEqual(sketch.Count(Key("later")), restored.Count(Key("later")));
        }

        [TestMethod]
        public void TestAnImpossiblePayloadIsRefused()
        {
            var clean = new CountSketch(0.05, 0.01).Add(Key("a")).ToByteArray();

            var bad = (byte[])clean.Clone();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(14), 0);
            var crc = new System.IO.Hashing.Crc32();
            crc.Append(bad.AsSpan(4, bad.Length - 8));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                bad.AsSpan(bad.Length - 4), crc.GetCurrentHashAsUInt32());

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<CountSketch>(bad));
            StringAssert.Contains(ex.Message, "cells");
        }

        [TestMethod]
        public void TestBadArgumentsAreRefused()
        {
            foreach (var bad in new[] { 0.0, -0.1, double.NaN })
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CountSketch(bad, 0.01));
            }

            foreach (var bad in new[] { 0.0, 1.0, 2.0, -0.5, double.NaN })
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CountSketch(0.01, bad));
            }

            var sketch = new CountSketch(0.01, 0.01);
            Assert.ThrowsExactly<ArgumentNullException>(() => sketch.Add(null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => sketch.Count(null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => sketch.SetHash(null!));

            sketch.Add(Key("a"));
            Assert.ThrowsExactly<InvalidOperationException>(() => sketch.SetHash(d => 1UL));
        }

        // ---- Counts at the ends of the long range ----------------------------------------

        /// <summary>The payload begins after the fixed-size envelope header.</summary>
        private const int PayloadStart = 14;

        /// <summary>
        /// The cells follow the payload's width, its depth and the length of the run that
        /// holds them, a u32 each.
        /// </summary>
        private const int CellsStart = PayloadStart + 3 * sizeof(uint);

        /// <summary>
        /// The cells of a sketch, read back out of its payload, which is the one place the
        /// table is visible from outside.
        /// </summary>
        private static long[][] Cells(CountSketch sketch)
        {
            var bytes = sketch.ToByteArray();
            var cells = new long[sketch.Depth()][];
            var at = CellsStart;
            for (var row = 0; row < cells.Length; row++)
            {
                cells[row] = new long[sketch.Width()];
                for (var column = 0; column < cells[row].Length; column++)
                {
                    cells[row][column] = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));
                    at += sizeof(long);
                }
            }

            return cells;
        }

        /// <summary>
        /// A sketch of the shape of <paramref name="template"/> holding exactly these
        /// cells: its payload with the cells replaced and the checksum repaired.
        /// </summary>
        private static CountSketch WithCells(CountSketch template, long[][] cells)
        {
            var bytes = template.ToByteArray();
            var at = CellsStart;
            foreach (var row in cells)
            {
                foreach (var cell in row)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), cell);
                    at += sizeof(long);
                }
            }

            var crc = new Crc32();
            crc.Append(bytes.AsSpan(4, bytes.Length - 8));
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(bytes.Length - 4), crc.GetCurrentHashAsUInt32());
            return Persistence.FromByteArray<CountSketch>(bytes);
        }

        /// <summary>
        /// The cell each row puts a key in and the sign it adds there. One add of 1 to an
        /// empty sketch of the same shape leaves a single cell per row holding the sign.
        /// </summary>
        private static (int Column, long Sign)[] Placement(double epsilon, double delta, byte[] key)
        {
            return Cells(new CountSketch(epsilon, delta).Add(key))
                .Select(row =>
                {
                    var column = Array.FindIndex(row, cell => cell != 0);
                    return (column, row[column]);
                })
                .ToArray();
        }

        /// <summary>
        /// A weighted add that took a cell past the end of the long range wrapped it. Two
        /// adds of the largest count left a cell at -2 (and, for a row that subtracts, at
        /// +2), so the count of an item seen 1.8e19 times came back as -2, which reads as
        /// the ordinary noise this sketch's estimates carry. A cell now stops at the end.
        /// </summary>
        /// <remarks>
        /// A single key in a fresh sketch has no collisions, so every row holds it alone.
        /// The depths run from one row to five, odd and even, because an even depth takes
        /// its median another way.
        /// </remarks>
        [TestMethod]
        [DataRow(0.5, 1u)]
        [DataRow(0.2, 2u)]
        [DataRow(0.05, 3u)]
        [DataRow(0.03, 4u)]
        [DataRow(0.01, 5u)]
        public void TestWeightedAddsStopAtTheLargestCountInsteadOfWrapping(double delta, uint rows)
        {
            var sketch = new CountSketch(0.5, delta);
            Assert.AreEqual(rows, sketch.Depth());

            sketch.Add(Key("a"), long.MaxValue);
            Assert.AreEqual(long.MaxValue, sketch.Count(Key("a")));

            sketch.Add(Key("a"), long.MaxValue);
            Assert.AreEqual(long.MaxValue, sketch.Count(Key("a")),
                "two adds of the largest count wrapped the cells");

            // And it stays there, rather than the next add taking it round again.
            sketch.Add(Key("a"), 1);
            Assert.AreEqual(long.MaxValue, sketch.Count(Key("a")));

            // The other end. A row that subtracts holds +long.MaxValue, whose negation is
            // one above the smallest count, so the estimate is one of those two.
            var down = new CountSketch(0.5, delta);
            down.Add(Key("a"), long.MinValue);
            down.Add(Key("a"), long.MinValue);
            var estimate = down.Count(Key("a"));
            Assert.IsTrue(estimate == long.MinValue || estimate == long.MinValue + 1,
                $"two adds of the smallest count came back as {estimate}");
        }

        /// <summary>
        /// A row that subtracts adds <c>-count</c>, and the negation of the smallest long
        /// does not fit: it stayed the smallest long, so a row meant to hold the largest
        /// count held the smallest one.
        /// </summary>
        [TestMethod]
        public void TestTheNegationOfTheSmallestCountSaturates()
        {
            // The sign each row gives a key varies with the key, so several are tried and
            // the test is refused if none of them reached a row that subtracts.
            var subtracting = 0;
            for (var i = 0; i < 16; i++)
            {
                var key = Key($"key{i}");
                var placement = Placement(0.5, 0.01, key);
                var cells = Cells(new CountSketch(0.5, 0.01).Add(key, long.MinValue));

                for (var row = 0; row < placement.Length; row++)
                {
                    var (column, sign) = placement[row];
                    if (sign < 0)
                    {
                        subtracting++;
                    }

                    Assert.AreEqual(
                        sign > 0 ? long.MinValue : long.MaxValue, cells[row][column],
                        $"key{i}, row {row}, which adds {sign} times the count");
                }
            }

            Assert.IsGreaterThan(0, subtracting, "no key reached a row that subtracts");
        }

        /// <summary>
        /// A cell at the smallest long, read by a row that subtracts, estimates one past
        /// the largest: the negation overflows, and it saturates like the sum does. Without
        /// that the estimate kept the sign of the cell and came back as the smallest count.
        /// </summary>
        [TestMethod]
        public void TestTheEstimateOfAFullNegativeCellInARowThatSubtractsSaturates()
        {
            var checkedARowThatSubtracts = false;
            for (var i = 0; i < 16; i++)
            {
                var key = Key($"key{i}");
                var placement = Placement(0.5, 0.01, key);

                // Every row's estimate is long.MaxValue: the cells are the sign times it, and
                // the smallest long where that is one past the end.
                var cells = placement
                    .Select(p => Enumerable.Repeat(0L, 4).ToArray())
                    .ToArray();
                for (var row = 0; row < cells.Length; row++)
                {
                    cells[row][placement[row].Column] =
                        placement[row].Sign > 0 ? long.MaxValue : long.MinValue;
                    checkedARowThatSubtracts |= placement[row].Sign < 0;
                }

                var sketch = WithCells(new CountSketch(0.5, 0.01), cells);
                Assert.AreEqual(long.MaxValue, sketch.Count(key), $"key{i}");
            }

            Assert.IsTrue(checkedARowThatSubtracts, "no key reached a row that subtracts");
        }

        /// <summary>
        /// An even number of rows averages the two middle estimates, and the sum of two
        /// estimates above 2^62 wrapped before it was halved although no cell had. One add
        /// of 5e18 to a sketch of two rows came back as -4,223,372,036,854,775,808.
        /// </summary>
        [TestMethod]
        [DataRow(0.2, 2u)]
        [DataRow(0.03, 4u)]
        public void TestTheMedianOfAnEvenDepthDoesNotWrapItsSum(double delta, uint rows)
        {
            var sketch = new CountSketch(0.5, delta);
            Assert.AreEqual(rows, sketch.Depth());

            sketch.Add(Key("a"), 5_000_000_000_000_000_000L);
            Assert.AreEqual(5_000_000_000_000_000_000L, sketch.Count(Key("a")));

            var below = new CountSketch(0.5, delta).Add(Key("a"), -5_000_000_000_000_000_000L);
            Assert.AreEqual(-5_000_000_000_000_000_000L, below.Count(Key("a")));
        }

        /// <summary>
        /// The two middle estimates are halved by truncation toward zero, as an integer
        /// division does, so that a result which did not overflow is the one it always was.
        /// The estimates of one item agree in parity until a cell saturates, so odd sums
        /// are only reached by setting the cells directly.
        /// </summary>
        [TestMethod]
        public void TestTheMedianOfTwoRowsTruncatesTowardZeroAtTheEdges()
        {
            // A key both of whose rows add, so any estimate is a cell as it stands.
            var key = Enumerable.Range(0, 64).Select(i => Key($"key{i}"))
                .First(k => Placement(0.5, 0.2, k).All(p => p.Sign > 0));
            var placement = Placement(0.5, 0.2, key);

            var cases = new (long First, long Second, long Median)[]
            {
                (3, 4, 3),
                (-3, -4, -3),
                (-3, 4, 0),
                (3, -4, 0),
                (-4, 3, 0),
                (long.MaxValue, long.MaxValue, long.MaxValue),
                (long.MaxValue, long.MaxValue - 1, long.MaxValue - 1),
                (long.MinValue, long.MinValue, long.MinValue),
                (long.MinValue, long.MinValue + 1, long.MinValue + 1),
                (long.MaxValue, long.MinValue, 0),
                (long.MaxValue, long.MinValue + 1, 0),
                (long.MaxValue, -1, long.MaxValue / 2),
                (long.MinValue, 1, long.MinValue / 2 + 1),
            };

            foreach (var (first, second, median) in cases)
            {
                var cells = new[] { new long[4], new long[4] };
                cells[0][placement[0].Column] = first;
                cells[1][placement[1].Column] = second;

                var sketch = WithCells(new CountSketch(0.5, 0.2), cells);
                Assert.AreEqual(median, sketch.Count(key), $"the median of {first} and {second}");
            }
        }

        /// <summary>
        /// A merge adds cells, and wrapped them just as an add did: a sketch holding the
        /// largest count merged into itself held -2 in every cell.
        /// </summary>
        [TestMethod]
        [DataRow(0.5, 1u)]
        [DataRow(0.2, 2u)]
        [DataRow(0.01, 5u)]
        public void TestMergeStopsAtTheLargestCountInsteadOfWrapping(double delta, uint rows)
        {
            var sketch = new CountSketch(0.5, delta);
            Assert.AreEqual(rows, sketch.Depth());
            sketch.Add(Key("a"), long.MaxValue);

            sketch.Merge(sketch);
            Assert.AreEqual(long.MaxValue, sketch.Count(Key("a")));

            // Two sketches rather than one against itself.
            var left = new CountSketch(0.5, delta).Add(Key("a"), -long.MaxValue);
            var right = new CountSketch(0.5, delta).Add(Key("a"), -long.MaxValue);
            left.Merge(right);
            var estimate = left.Count(Key("a"));
            Assert.IsTrue(estimate == long.MinValue || estimate == long.MinValue + 1,
                $"two merged streams of -long.MaxValue came back as {estimate}");

            // The sketch merged in is left as it was.
            Assert.AreEqual(-long.MaxValue, right.Count(Key("a")));
        }

        /// <summary>
        /// What saturating costs, written down so that it is a decision and not a surprise:
        /// a cell at the end of the range no longer records how far past it the count went,
        /// so subtracting from it is off by that much. The count above the largest long is
        /// not recoverable, and the documentation says so.
        /// </summary>
        [TestMethod]
        public void TestASaturatedCellDoesNotRecordHowFarPastTheEndItWent()
        {
            // One row, so the estimate is the cell as that row reads it, and the sign the
            // row gives a key decides which end the cell stopped at.
            var added = 0;
            var subtracted = 0;
            for (var i = 0; i < 16; i++)
            {
                var key = Key($"key{i}");
                var sign = Placement(0.5, 0.5, key)[0].Sign;
                var sketch = new CountSketch(0.5, 0.5);
                Assert.AreEqual(1u, sketch.Depth());

                // Seen twice the largest count, less once the largest count: the largest
                // count. The cell stopped at the end after the second add, so the
                // subtraction takes it to nothing where it should have left the largest
                // count. A row that subtracts stopped at the smallest long, which is one
                // further from nought, and so reads one over.
                sketch.Add(key, long.MaxValue);
                sketch.Add(key, long.MaxValue);
                sketch.Add(key, -long.MaxValue);
                Assert.AreEqual(sign > 0 ? 0L : 1L, sketch.Count(key), $"key{i}, which adds {sign}");

                if (sign > 0)
                {
                    added++;
                }
                else
                {
                    subtracted++;
                }
            }

            Assert.IsGreaterThan(0, added, "no key reached a row that adds");
            Assert.IsGreaterThan(0, subtracted, "no key reached a row that subtracts");
        }

        /// <summary>
        /// A row that subtracts takes the count off its cell, and when the count is the
        /// smallest long the negation that would turn it into an addition does not fit.
        /// The cell is held to the range after the difference is worked out and not
        /// before: -5 less the smallest long is 9,223,372,036,854,775,803, a count that
        /// fits and that clamping the negation first would have made one too small.
        /// </summary>
        [TestMethod]
        public void TestTheSmallestCountIsTakenOffWholeAndNotNegatedFirst()
        {
            var subtracting = 0;
            for (var i = 0; i < 16; i++)
            {
                var key = Key($"key{i}");
                var placement = Placement(0.5, 0.01, key);

                foreach (var (start, rowThatAdds, rowThatSubtracts) in new[]
                {
                    (-5L, long.MinValue, long.MaxValue - 4),
                    (5L, 5L + long.MinValue, long.MaxValue),
                })
                {
                    var cells = placement
                        .Select(p => new long[4])
                        .ToArray();
                    for (var row = 0; row < cells.Length; row++)
                    {
                        cells[row][placement[row].Column] = start;
                    }

                    var sketch = WithCells(new CountSketch(0.5, 0.01), cells);
                    sketch.Add(key, long.MinValue);

                    var after = Cells(sketch);
                    for (var row = 0; row < cells.Length; row++)
                    {
                        var (column, sign) = placement[row];
                        if (sign < 0)
                        {
                            subtracting++;
                        }

                        Assert.AreEqual(
                            sign > 0 ? rowThatAdds : rowThatSubtracts, after[row][column],
                            $"key{i}, row {row}, which adds {sign} times the count, from {start}");
                    }
                }
            }

            Assert.IsGreaterThan(0, subtracting, "no key reached a row that subtracts");
        }

        /// <summary>
        /// Long sequences of weighted adds and merges at the ends of the long range, held
        /// to a model that does the arithmetic in 128 bits and clamps every cell to the
        /// long range after every step. Every cell and every item's estimate must match
        /// after each step, so a result that did not overflow is the one an unchecked
        /// sum gives, and one that did is the end of the range.
        /// </summary>
        /// <remarks>
        /// The sketches are one to three columns wide, so that unrelated items share
        /// cells and a large positive count meets a large negative one. The tally fails
        /// the run if it never saturated a cell at either end or, at an even depth, never
        /// took a median whose sum is beyond a long.
        /// </remarks>
        [TestMethod]
        [DataRow(1.0, 0.5, 1u)]
        [DataRow(1.0, 0.2, 2u)]
        [DataRow(0.9, 0.05, 3u)]
        [DataRow(0.7, 0.03, 4u)]
        [DataRow(0.8, 0.01, 5u)]
        public void TestSequencesAtTheEndsOfTheRangeMatchAnInt128Model(
            double epsilon, double delta, uint rows)
        {
            var weights = new[]
            {
                long.MaxValue, long.MinValue, -long.MaxValue,
                5_000_000_000_000_000_000L, -5_000_000_000_000_000_000L,
                1L << 62, -(1L << 62), 1, -1, 7, -3,
            };
            var keys = Enumerable.Range(0, 6).Select(i => Key($"key{i}")).ToArray();
            var placements = keys.Select(k => Placement(epsilon, delta, k)).ToArray();

            var sketch = new CountSketch(epsilon, delta);
            Assert.AreEqual(rows, sketch.Depth());
            var width = (int)sketch.Width();

            var model = new Int128[rows][];
            var side = new Int128[rows][];
            for (var row = 0; row < rows; row++)
            {
                model[row] = new Int128[width];
                side[row] = new Int128[width];
            }

            var other = new CountSketch(epsilon, delta);
            var random = new Random(211);
            var high = 0;
            var low = 0;
            var wide = 0;

            for (var step = 0; step < 400; step++)
            {
                var kind = random.Next(10);
                if (kind < 6)
                {
                    var key = random.Next(keys.Length);
                    var weight = weights[random.Next(weights.Length)];
                    sketch.Add(keys[key], weight);
                    AddToModel(model, placements[key], weight);
                }
                else if (kind < 8)
                {
                    var key = random.Next(keys.Length);
                    var weight = weights[random.Next(weights.Length)];
                    other.Add(keys[key], weight);
                    AddToModel(side, placements[key], weight);
                }
                else if (kind == 8)
                {
                    sketch.Merge(other);
                    for (var row = 0; row < rows; row++)
                    {
                        for (var column = 0; column < width; column++)
                        {
                            model[row][column] = Clamp(model[row][column] + side[row][column]);
                        }
                    }
                }
                else
                {
                    sketch.Merge(sketch);
                    for (var row = 0; row < rows; row++)
                    {
                        for (var column = 0; column < width; column++)
                        {
                            model[row][column] = Clamp(model[row][column] * 2);
                        }
                    }
                }

                var cells = Cells(sketch);
                for (var row = 0; row < rows; row++)
                {
                    for (var column = 0; column < width; column++)
                    {
                        Assert.AreEqual((long)model[row][column], cells[row][column],
                            $"step {step}, row {row}, column {column}");
                    }
                }

                for (var key = 0; key < keys.Length; key++)
                {
                    Assert.AreEqual(ModelCount(model, placements[key]), sketch.Count(keys[key]),
                        $"step {step}, key{key}");
                }
            }

            Assert.IsGreaterThan(0, high, "no cell was ever saturated at the largest count");
            Assert.IsGreaterThan(0, low, "no cell was ever saturated at the smallest count");
            if (rows % 2 == 0)
            {
                Assert.IsGreaterThan(0, wide, "no median ever summed to more than a long holds");
            }

            Int128 Clamp(Int128 value)
            {
                if (value > long.MaxValue)
                {
                    high++;
                    return long.MaxValue;
                }

                if (value < long.MinValue)
                {
                    low++;
                    return long.MinValue;
                }

                return value;
            }

            void AddToModel(Int128[][] cells, (int Column, long Sign)[] placement, long weight)
            {
                for (var row = 0; row < placement.Length; row++)
                {
                    var (column, sign) = placement[row];
                    cells[row][column] = Clamp(cells[row][column] + (Int128)sign * weight);
                }
            }

            long ModelCount(Int128[][] cells, (int Column, long Sign)[] placement)
            {
                var estimates = new Int128[placement.Length];
                for (var row = 0; row < placement.Length; row++)
                {
                    var (column, sign) = placement[row];
                    estimates[row] = (Int128)sign * cells[row][column];
                    estimates[row] = estimates[row] > long.MaxValue
                        ? long.MaxValue
                        : estimates[row];
                }

                Array.Sort(estimates);
                if (estimates.Length % 2 == 1)
                {
                    return (long)estimates[estimates.Length / 2];
                }

                var sum = estimates[(estimates.Length / 2) - 1] + estimates[estimates.Length / 2];
                if (sum > long.MaxValue || sum < long.MinValue)
                {
                    wide++;
                }

                return (long)(sum / 2);
            }
        }

        /// <summary>
        /// A read holds the counters once and is charged for them once (#335). In the ordinary
        /// envelope, what it allocates and is charged beyond the payload it holds is the rows it
        /// builds, the counters read into them and not first into an array of their own and
        /// copied; in the large form, where the payload is not held, it is the rows alone, with
        /// no buffer on the heap for the pieces the counters are read in.
        /// </summary>
        /// <remarks>
        /// Three rows of 10,000 counters, 80,000 bytes a row and 240,000 in all, so that each row
        /// is longer than a piece of a run on the heap (<see cref="PayloadSource.PieceLength"/>)
        /// and a buffer of one is a whole piece. The slack, 4 KiB, is a twentieth of the piece
        /// and less of the counters, so a copy of either fails it. Each figure is held from below
        /// too, to the payload and the rows, so that a measurement of nothing cannot pass. The
        /// reads are counted in a no-GC region (#293).
        /// </remarks>
        [TestMethod]
        public void TestAReadHoldsItsCountersOnce()
        {
            const long Slack = 4 * 1024;

            var sketch = new CountSketch(0.01, 0.05);
            for (var i = 0; i < 30_000; i++)
            {
                sketch.Add(Key($"k{i}"), (i % 7) - 3);
            }

            Assert.AreEqual((10_000u, 3u), (sketch.Width(), sketch.Depth()), "premise: the sketch is not of three rows of 10,000");
            var bytes = sketch.ToByteArray();
            long payload = bytes.Length - PersistenceFormat.EnvelopeLength;
            var rows = ReadBudget.Sum(
                ReadBudget.ArrayOf(sketch.Depth(), sizeof(long)),
                ReadBudget.Times(sketch.Depth(), ReadBudget.ArrayOf(sketch.Width(), sizeof(long))));
            Assert.IsGreaterThan(PayloadSource.PieceLength + Slack, (long)sketch.Width() * sizeof(long),
                "premise: a row is no longer than a piece on the heap");

            CountSketch.ReadFrom(new MemoryStream(bytes, writable: false));
            var ordinary = NoGCRegion.Allocated(() => CountSketch.ReadFrom(new MemoryStream(bytes, writable: false)));
            var charged = CapacityModel.ChargeOf<CountSketch>(StructureId.CountSketch, bytes);

            var large = LargeForm.WriteLarge(sketch);
            Assert.IsTrue(LargeForm.IsLarge(large), "the sketch was not written in the large form");
            CountSketch.ReadFrom(new MemoryStream(large, writable: false));
            var streamed = NoGCRegion.Allocated(() => CountSketch.ReadFrom(new MemoryStream(large, writable: false)));
            var budget = new ReadBudget(long.MaxValue, StructureId.CountSketch);
            CollectionAssert.AreEqual(bytes,
                CountSketch.ReadFrom(new MemoryStream(large, writable: false), new ReadOptions(budget)).ToByteArray(),
                "the large form does not read back as the sketch");

            Console.WriteLine(
                $"[HOLDS ONCE] CountSketch: payload {payload:N0}, rows {rows:N0}; ordinary read {ordinary:N0}, " +
                $"charged {charged:N0}; large-form read {streamed:N0}, charged {budget.Charged:N0}");

            Assert.IsGreaterThanOrEqualTo(payload + rows, ordinary,
                $"the read allocated {ordinary:N0} bytes, less than the {payload:N0}-byte payload it holds and the {rows:N0} bytes of rows");
            Assert.IsLessThanOrEqualTo(payload + rows + Slack, ordinary,
                $"the read allocated {ordinary:N0} bytes, {ordinary - payload - rows:N0} more than its payload and its rows");
            Assert.IsGreaterThanOrEqualTo(payload + rows, charged,
                $"the read was charged {charged:N0} bytes, less than its payload and its rows");
            Assert.IsLessThanOrEqualTo(payload + rows + Slack, charged,
                $"the read was charged {charged:N0} bytes, {charged - payload - rows:N0} more than its payload and its rows");

            Assert.IsGreaterThanOrEqualTo(rows, streamed,
                $"the read of the large form allocated {streamed:N0} bytes, less than the {rows:N0} bytes of rows");
            Assert.IsLessThanOrEqualTo(rows + Slack, streamed,
                $"the read of the large form allocated {streamed:N0} bytes, {streamed - rows:N0} more than its rows");
            Assert.IsGreaterThanOrEqualTo(rows, budget.Charged,
                $"the read of the large form was charged {budget.Charged:N0} bytes, less than its rows");
            Assert.IsLessThanOrEqualTo(rows + Slack, budget.Charged,
                $"the read of the large form was charged {budget.Charged:N0} bytes, {budget.Charged - rows:N0} more than its rows");
        }

        /// <summary>
        /// A count sketch's price prices its rows before it checks that the payload ends after
        /// its cells, as its reader charges the rows before it checks, so a payload holding a
        /// byte past the cells is refused for that byte under exactly its own price, in either
        /// envelope (#343).
        /// </summary>
        /// <remarks>
        /// The cells are the payload's last run, so a cut that holds them holds the payload
        /// whole, and no cut of a sweep can tell where the rows are priced. A byte left over can:
        /// a price that checked the end first would stop there having priced nothing for the
        /// rows, the read would be admitted at the envelope alone, and its reader, charging the
        /// rows before it finds the byte, would be refused for a budget its own price fits. The
        /// sketch is the coarsest the constructor builds, at ε = δ = 0.5. A quotient filter is
        /// held to the same in
        /// <see cref="TestQuotientFilter.TestAFilterWithAByteLeftOverIsRefusedForItUnderItsOwnPrice"/>,
        /// and every structure's read, though not its price, in <see cref="TestBytesLeftOver"/>.
        /// </remarks>
        [TestMethod]
        public void TestASketchWithAByteLeftOverIsRefusedForItUnderItsOwnPrice()
        {
            var sketch = new CountSketch(0.5, 0.5);
            sketch.Add(Key("a"), 1);
            var ordinary = sketch.ToByteArray();
            var large = LargeForm.WriteLarge(sketch);
            Assert.IsFalse(LargeForm.IsLarge(ordinary), "premise: the sketch was not written in the ordinary envelope");
            Assert.IsTrue(LargeForm.IsLarge(large), "premise: the sketch was not written in the large form");
            foreach (var whole in new[] { ordinary, large })
            {
                TestBytesLeftOver.AssertRefusedForTheByteUnderItsOwnPrice(StructureId.CountSketch, CountSketch.Price,
                    (bytes, options) => Persistence.FromByteArray<CountSketch>(bytes, options), whole);
            }
        }
    }
}
