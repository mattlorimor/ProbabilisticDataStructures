using System;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestCountMinSketch
    {
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
