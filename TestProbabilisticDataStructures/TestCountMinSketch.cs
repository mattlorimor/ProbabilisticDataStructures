using System;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestCountMinSketch
    {
        /// <summary>
        /// States whose payload <see cref="TestPayloadCount"/> holds the count to: the length
        /// is the shape's, so the coarsest and a wider, deeper one, and both placements,
        /// which write their own byte.
        /// </summary>
        internal static System.Collections.Generic.IEnumerable<PayloadState> PayloadStates()
        {
            var version = PersistenceFormat.ColumnPlacementVersion;
            yield return PayloadState.Of("coarsest, empty", new CountMinSketch(0.5, 0.5), version);

            foreach (var placement in new[] { Placement.Narrow, Placement.Wide })
            {
                var sketch = new CountMinSketch(0.1, 0.01, null, placement);
                for (var i = 0; i < 200; i++) sketch.Add(Encoding.ASCII.GetBytes($"w{i % 30}"));
                yield return PayloadState.Of($"28 x 5, {placement}, filled", sketch, version);
            }
        }

        private static byte[] A_BYTES = Encoding.ASCII.GetBytes("a");
        private static byte[] B_BYTES = Encoding.ASCII.GetBytes("b");
        private static byte[] C_BYTES = Encoding.ASCII.GetBytes("c");
        private static byte[] D_BYTES = Encoding.ASCII.GetBytes("d");
        private static byte[] X_BYTES = Encoding.ASCII.GetBytes("x");

        /// <summary>
        /// Ensures that TotalCount returns the number of items added to the sketch.
        /// </summary>
        [TestMethod]
        public void TestCMSTotalCount()
        {
            var cms = new CountMinSketch(0.001, 0.01);

            for (int i = 0; i < 100; i++)
            {
                cms.Add(Encoding.ASCII.GetBytes(i.ToString()));
            }

            var count = cms.TotalCount();
            Assert.AreEqual(100u, count);
        }

        /// <summary>
        /// Ensures that Add adds to the set and Count returns the correct approximation.
        /// </summary>
        [TestMethod]
        public void TestCMSAddAndCount()
        {
            var cms = new CountMinSketch(0.001, 0.01);

            var addedCms = cms.Add(A_BYTES);
            Assert.AreSame(cms, addedCms);

            cms.Add(B_BYTES);
            cms.Add(C_BYTES);
            cms.Add(B_BYTES);
            cms.Add(D_BYTES);
            cms.Add(A_BYTES).Add(A_BYTES);

            var count = cms.Count(A_BYTES);
            Assert.AreEqual(3u, count);

            count = cms.Count(B_BYTES);
            Assert.AreEqual(2u, count);

            count = cms.Count(C_BYTES);
            Assert.AreEqual(1u, count);

            count = cms.Count(D_BYTES);
            Assert.AreEqual(1u, count);

            count = cms.Count(X_BYTES);
            Assert.AreEqual(0u, count);
        }

        /// <summary>
        /// Ensures that Merge combines the two sketches.
        /// </summary>
        [TestMethod]
        public void TestCMSMerge()
        {
            var cms = new CountMinSketch(0.001, 0.01);
            cms.Add(B_BYTES);
            cms.Add(C_BYTES);
            cms.Add(B_BYTES);
            cms.Add(D_BYTES);
            cms.Add(A_BYTES).Add(A_BYTES);

            var other = new CountMinSketch(0.001, 0.01);
            other.Add(B_BYTES);
            other.Add(C_BYTES);
            other.Add(B_BYTES);

            var wasMerged = cms.Merge(other);
            Assert.IsTrue(wasMerged);

            var count = cms.Count(A_BYTES);
            Assert.AreEqual(2u, count);

            count = cms.Count(B_BYTES);
            Assert.AreEqual(4u, count);

            count = cms.Count(C_BYTES);
            Assert.AreEqual(2u, count);

            count = cms.Count(D_BYTES);
            Assert.AreEqual(1u, count);

            count = cms.Count(X_BYTES);
            Assert.AreEqual(0u, count);
        }

        /// <summary>
        /// Ensures that Reset restores the sketch to its original state.
        /// </summary>
        [TestMethod]
        public void TestCMSReset()
        {
            var cms = new CountMinSketch(0.001, 0.01);
            cms.Add(B_BYTES);
            cms.Add(C_BYTES);
            cms.Add(B_BYTES);
            cms.Add(D_BYTES);
            cms.Add(A_BYTES).Add(A_BYTES);

            var resetCms = cms.Reset();
            Assert.AreSame(cms, resetCms);

            for (uint i = 0; i < cms.Depth; i++)
            {
                for (int j = 0; j < cms.Width; j++)
                {
                    if (cms.Matrix[i][j] != 0)
                    {
                        Assert.Fail("Expected matrix to be completely empty.");
                    }
                }
            }
        }

        [TestMethod]
        public void BenchmarkCMSAdd()
        {
            var n = 100000;
            var cms = new CountMinSketch(0.001, 0.01);
            var data = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                data[i] = Encoding.ASCII.GetBytes(i.ToString());
            }

            for (int i = 0; i < n; i++)
            {
                cms.Add(data[i]);
            }
        }

        [TestMethod]
        public void BenchmarkCMSCount()
        {
            var n = 100000;
            var cms = new CountMinSketch(0.001, 0.01);
            var data = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                var byteArray = Encoding.ASCII.GetBytes(i.ToString());
                data[i] = byteArray;
                cms.Add(byteArray);
            }

            for (int i = 0; i < n; i++)
            {
                cms.Add(data[i]);
            }
        }

        // ---- A counter stops at the largest count rather than wrapping -----------------

        /// <summary>
        /// A sketch merged into itself doubles every counter and its total, so one that
        /// has counted a single key holds 2^n of it after n merges, and passes what a
        /// <c>ulong</c> holds at the 64th. Wrapped, that merge took the key's estimate and
        /// the total to nought, an undercount from a structure that never gives one. Both
        /// stop at the largest instead, and neither falls on the way.
        /// </summary>
        [TestMethod]
        public void TestSelfMergesNeverLowerAnEstimateOrTheTotal()
        {
            var cms = new CountMinSketch(0.001, 0.01);
            cms.Add(A_BYTES);

            var estimate = cms.Count(A_BYTES);
            var total = cms.TotalCount();
            Assert.AreEqual(1UL, estimate, "one add is one in every counter of the key");
            Assert.AreEqual(1UL, total);

            for (var merge = 1; merge <= 64; merge++)
            {
                cms.Merge(cms);

                // 2^n is exact up to 2^63, and 2^64 is not a count: the 64th merge stops.
                var expected = merge < 64 ? 1UL << merge : ulong.MaxValue;
                var estimateNow = cms.Count(A_BYTES);
                var totalNow = cms.TotalCount();

                Assert.IsGreaterThanOrEqualTo(estimate, estimateNow,
                    $"merge {merge} lowered the key's estimate from {estimate} to {estimateNow}");
                Assert.IsGreaterThanOrEqualTo(total, totalNow,
                    $"merge {merge} lowered the total from {total} to {totalNow}");
                Assert.AreEqual(expected, estimateNow, $"the key's estimate after merge {merge}");
                Assert.AreEqual(expected, totalNow, $"the total after merge {merge}");

                estimate = estimateNow;
                total = totalNow;
            }

            Assert.AreEqual(ulong.MaxValue, estimate);
            Assert.AreEqual(ulong.MaxValue, total);
        }

        /// <summary>
        /// An add to a sketch a merge has taken to the largest count leaves the key's
        /// estimate and the total there. Wrapped, the add took both to nought, which is
        /// what saturating only in <c>Merge</c> would leave.
        /// </summary>
        [TestMethod]
        public void TestAnAddAfterSelfMergesLeavesTheEstimateAndTheTotalAtTheLargest()
        {
            var cms = new CountMinSketch(0.001, 0.01);
            cms.Add(A_BYTES);
            for (var merge = 0; merge < 64; merge++)
            {
                cms.Merge(cms);
            }

            Assert.AreEqual(ulong.MaxValue, cms.Count(A_BYTES), "the sketch is not at the largest to begin with");
            Assert.AreEqual(ulong.MaxValue, cms.TotalCount());

            cms.Add(A_BYTES);

            Assert.AreEqual(ulong.MaxValue, cms.Count(A_BYTES), "an add to the key's counters at the largest");
            Assert.AreEqual(ulong.MaxValue, cms.TotalCount(), "an add to the total at the largest");

            // A key the merges never reached is counted as it always was.
            cms.Add(B_BYTES);
            Assert.AreEqual(1UL, cms.Count(B_BYTES));
            Assert.AreEqual(ulong.MaxValue, cms.TotalCount());
        }

        /// <summary>
        /// A counter at the largest count stays there when it is added to, in whichever
        /// row it is. The estimate is the least of a key's counters, so a wrapped one is
        /// the estimate: with one row at the largest and the others at 40, an add must give
        /// 41, and a counter that wrapped gives 0 where a sketch never undercounts. Each
        /// row takes its turn, so a row the loop skipped does not pass.
        /// </summary>
        [TestMethod]
        public void TestAnAddToASaturatedCounterInAnyRowLeavesItThere()
        {
            var depth = new CountMinSketch(0.001, 0.01).Depth;
            Assert.IsGreaterThan(1u, depth, "one row cannot show which of several is the one that wrapped");

            for (var saturated = 0; saturated < depth; saturated++)
            {
                var cms = new CountMinSketch(0.001, 0.01);
                for (var row = 0; row < depth; row++)
                {
                    Array.Fill(cms.Matrix[row], row == saturated ? ulong.MaxValue : 40UL);
                }

                Assert.AreEqual(40UL, cms.Count(A_BYTES), "the estimate before the add");

                cms.Add(A_BYTES);

                Assert.AreEqual(41UL, cms.Count(A_BYTES),
                    $"with row {saturated} at the largest and the others at 40, an add is 41");
            }
        }

        /// <summary>
        /// The counters and the total reach the largest count by an add that takes them
        /// from one short of it, and stay there after the next. A stop that came one count
        /// early would leave them a count short of the largest for good, which no test that
        /// starts at the largest sees. One short is 2 + 4 + ... + 2^63, the sketches a
        /// single add becomes in 1 to 63 self-merges, summed by merging; the merges sum to
        /// below the largest every time, so nothing here has stopped yet.
        /// </summary>
        [TestMethod]
        public void TestAnAddTakesTheCountersAndTheTotalToTheLargestAndNoFurther()
        {
            var powers = new CountMinSketch(0.001, 0.01);
            powers.Add(A_BYTES);
            var sum = new CountMinSketch(0.001, 0.01);
            for (var doublings = 1; doublings <= 63; doublings++)
            {
                powers.Merge(powers);
                sum.Merge(powers);
            }

            Assert.AreEqual(ulong.MaxValue - 1, sum.Count(A_BYTES), "2 + 4 + ... + 2^63 in every counter of the key");
            Assert.AreEqual(ulong.MaxValue - 1, sum.TotalCount(), "and in the total");

            sum.Add(A_BYTES);
            Assert.AreEqual(ulong.MaxValue, sum.Count(A_BYTES), "one add from one short");
            Assert.AreEqual(ulong.MaxValue, sum.TotalCount(), "one add from one short, in the total");

            sum.Add(A_BYTES);
            Assert.AreEqual(ulong.MaxValue, sum.Count(A_BYTES), "an add at the largest");
            Assert.AreEqual(ulong.MaxValue, sum.TotalCount(), "an add at the largest, in the total");
        }

        /// <summary>
        /// Merging a sketch whose counters are near the largest into one that adds more
        /// than is left stops them at the largest, and counters that the sum does not carry
        /// past it are the sum, exact. The second half is what stops a merge that saturates
        /// too readily from passing: one that takes a sum equal to the counter it started
        /// from, which is every counter the other sketch did not touch, for an overflow.
        /// </summary>
        [TestMethod]
        public void TestMergingTwoSketchesStopsAtTheLargestAndIsExactBelowIt()
        {
            const ulong headroom = 2;
            var cms = new CountMinSketch(0.001, 0.01);
            foreach (var row in cms.Matrix)
            {
                Array.Fill(row, ulong.MaxValue - headroom);
            }

            var other = new CountMinSketch(0.001, 0.01);
            for (var i = 0; i < 5; i++)
            {
                other.Add(A_BYTES);
            }

            cms.Merge(other);

            Assert.AreEqual(ulong.MaxValue, cms.Count(A_BYTES),
                "five more than the headroom of two in every counter of the key");

            // B's counters were not touched, unless they share every row with A.
            Assert.AreEqual(ulong.MaxValue - headroom, cms.Count(B_BYTES),
                "counters the other sketch added nothing to are as they were");
            Assert.AreEqual(5UL, cms.TotalCount());

            // And a sum that lands on the largest exactly is the largest, not past it.
            var exact = new CountMinSketch(0.001, 0.01);
            foreach (var row in exact.Matrix)
            {
                Array.Fill(row, ulong.MaxValue - headroom);
            }

            var two = new CountMinSketch(0.001, 0.01);
            two.Add(A_BYTES).Add(A_BYTES);
            exact.Merge(two);
            Assert.AreEqual(ulong.MaxValue, exact.Count(A_BYTES));
            Assert.AreEqual(ulong.MaxValue - headroom, exact.Count(B_BYTES));
        }

        // ---- A constructor refuses what it cannot build ------------------------------

        /// <summary>
        /// Each row is one array, so a width past what an array holds is refused, naming
        /// epsilon. Until now a width up to 2^32 - 1 was allowed by the guard on epsilon
        /// and then failed inside the allocation, with an <see cref="OutOfMemoryException"/>
        /// just past the limit and an <see cref="OverflowException"/> from 2^31 up.
        /// An epsilon of infinity had no width at all, and built a sketch that divided by
        /// zero on its first add.
        /// </summary>
        [TestMethod]
        public void TestAWidthNoArrayHoldsIsRefusedBeforeAnythingIsAllocated()
        {
            var epsilons = new[]
            {
                // The width is Array.MaxLength + 1.
                Math.E / (Array.MaxLength + 0.5),
                // 2.7 billion, between an array's limit and what a uint holds.
                1e-9,
                1e-12,
                double.Epsilon,
                double.PositiveInfinity,
            };

            foreach (var epsilon in epsilons)
            {
                var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => new CountMinSketch(epsilon, 0.5), $"an epsilon of {epsilon}");
                Assert.AreEqual("epsilon", ex.ParamName);
            }
        }

        /// <summary>
        /// The depth is ln(1 / delta), so a delta whose reciprocal is infinite has none.
        /// Below about 5.56e-309 it used to be converted to a row count anyway, and failed
        /// in the allocation. The deepest sketch is 710 rows, and still builds.
        /// </summary>
        [TestMethod]
        public void TestADeltaWithNoReciprocalIsRefused()
        {
            foreach (var delta in new[] { double.Epsilon, 1e-310, 5.5e-309 })
            {
                Assert.IsTrue(double.IsPositiveInfinity(1 / delta));
                var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => new CountMinSketch(0.5, delta), $"a delta of {delta}");
                Assert.AreEqual("delta", ex.ParamName);
            }

            foreach (var delta in new[] { 5.6e-309, 1e-308 })
            {
                var deepest = new CountMinSketch(0.5, delta);
                Assert.AreEqual(710u, deepest.Depth, $"a delta of {delta}");
                deepest.Add(A_BYTES).Add(A_BYTES);
                Assert.AreEqual(2UL, deepest.Count(A_BYTES));
            }
        }

        // TODO: Implement these later.
        // TestCMSSerialization
        // BenchmarkCMSWriteDataTo
        // BenchmarkCMSReadDataFrom
    }
}
