using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Covers the library's failure modes.
    /// <para>
    /// Every throw site in the library was previously unexercised -- the suite
    /// contained no exception assertions at all -- so nothing stopped an argument
    /// check from being weakened or removed unnoticed.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestErrorPaths
    {
        /// <summary>
        /// HyperLogLog indexes registers by masking, so a register count that is not
        /// a power of two would silently produce a skewed distribution rather than
        /// an obvious failure. The constructor rejects it instead.
        /// </summary>
        [TestMethod]
        public void TestHyperLogLogRejectsNonPowerOfTwoRegisterCount()
        {
            var ex = Assert.Throws<ArgumentException>(() => new HyperLogLog(3));
            StringAssert.Contains(ex.Message, "power of two");
        }

        [TestMethod]
        public void TestHyperLogLogAcceptsPowerOfTwoRegisterCount()
        {
            // The boundary either side of the rejected case above.
            _ = new HyperLogLog(2);
            _ = new HyperLogLog(4);
            _ = new HyperLogLog(1024);
        }

        /// <summary>
        /// Merging estimators with different register counts cannot produce a
        /// meaningful union, so it is refused rather than approximated.
        /// </summary>
        [TestMethod]
        public void TestHyperLogLogMergeRejectsMismatchedRegisterCounts()
        {
            var a = new HyperLogLog(16);
            var b = new HyperLogLog(32);

            var ex = Assert.Throws<ArgumentException>(() => a.Merge(b));
            StringAssert.Contains(ex.Message, "registers must match");
        }

        [TestMethod]
        public void TestHyperLogLogMergeAcceptsMatchingRegisterCounts()
        {
            var a = new HyperLogLog(16);
            var b = new HyperLogLog(16);

            Assert.IsTrue(a.Merge(b));
        }

        /// <summary>
        /// Count-Min Sketch matrices only combine cell-for-cell, so a depth
        /// mismatch is refused.
        /// </summary>
        [TestMethod]
        public void TestCountMinSketchMergeRejectsMismatchedDepth()
        {
            // depth is ceil(ln(1/delta)), so the deltas must differ enough to cross
            // an integer boundary -- 0.99 and 0.999 both yield depth 1, which is why
            // the guard below asserts on depth rather than on delta.
            var a = new CountMinSketch(0.001, 0.99);
            var b = new CountMinSketch(0.001, 0.01);
            Assert.AreNotEqual(a.Depth, b.Depth, "test needs sketches of differing depth");

            var ex = Assert.Throws<Exception>(() => a.Merge(b));
            StringAssert.Contains(ex.Message, "depth must match");
        }

        /// <summary>
        /// As above, for a width mismatch.
        /// </summary>
        [TestMethod]
        public void TestCountMinSketchMergeRejectsMismatchedWidth()
        {
            // Same delta so depth matches and the width check is what fires.
            var a = new CountMinSketch(0.001, 0.99);
            var b = new CountMinSketch(0.01, 0.99);
            Assert.AreEqual(a.Depth, b.Depth, "test needs matching depth so width is checked");
            Assert.AreNotEqual(a.Width, b.Width, "test needs sketches of differing width");

            var ex = Assert.Throws<Exception>(() => a.Merge(b));
            StringAssert.Contains(ex.Message, "width must match");
        }

        /// <summary>
        /// Records that a zero false-positive rate is rejected, and how.
        /// <para>
        /// OptimalM divides by the log of the rate, which overflows when converting
        /// the resulting infinity to a uint. OverflowException is not a good way to
        /// report an invalid argument -- ArgumentOutOfRangeException would say what
        /// the caller did wrong -- but it is the current behavior, and pinning it
        /// means a future change to argument validation is a deliberate one.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestBloomFilterRejectsZeroFalsePositiveRate()
        {
            Assert.Throws<OverflowException>(() => new BloomFilter(100, 0.0));
        }
    }
}
