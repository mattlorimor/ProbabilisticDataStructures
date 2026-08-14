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

            // ArgumentException, not the bare Exception this used to throw: a caller
            // wanting to fall back to some other merge could not catch that one
            // without catching every unrelated failure alongside it.
            var ex = Assert.Throws<ArgumentException>(() => a.Merge(b));
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

            var ex = Assert.Throws<ArgumentException>(() => a.Merge(b));
            StringAssert.Contains(ex.Message, "width must match");
        }

        /// <summary>
        /// A false positive rate must sit strictly between zero and one. Previously
        /// these surfaced as an OverflowException from a numeric conversion deep in
        /// the sizing math, which reported something true about the machinery and
        /// nothing about the mistake.
        /// </summary>
        [TestMethod]
        public void TestFilterRejectsFalsePositiveRateOutsideZeroToOne()
        {
            foreach (var bad in new[] { 0.0, 1.0, -0.5, 2.0, double.NaN })
            {
                var ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => new BloomFilter(100, bad),
                    $"a false positive rate of {bad} should be rejected");
                Assert.AreEqual("fpRate", ex.ParamName);
            }

            // A rate inside the range is accepted.
            _ = new BloomFilter(100, 0.5);
        }

        /// <summary>
        /// Sizing a filter for zero items produced one with no buckets, which
        /// constructed successfully and then threw DivideByZeroException on first
        /// use -- a failure reported far from its cause.
        /// </summary>
        [TestMethod]
        public void TestFilterRejectsZeroItemCount()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BloomFilter(0, 0.01));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BloomFilter64(0, 0.01));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PartitionedBloomFilter(0, 0.01));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CuckooBloomFilter(0, 0.01));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CountingBloomFilter(0, 4, 0.01));
            Assert.Throws<ArgumentOutOfRangeException>(() => new DeletableBloomFilter(0, 10, 0.01));
            Assert.Throws<ArgumentOutOfRangeException>(() => new StableBloomFilter(0, 1, 0.01));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ScalableBloomFilter(0, 0.01, 0.8));
            // A capacity of zero left nowhere to put anything and divided by it.
            Assert.Throws<ArgumentOutOfRangeException>(() => new InverseBloomFilter(0));
            // A top-0 has no room for anything and indexed its empty heap.
            Assert.Throws<ArgumentOutOfRangeException>(() => new TopK(0.001, 0.01, 0));

            // Epsilon fixes the sketch's width as e / epsilon. Zero and below asked
            // for infinite width and surfaced as OverflowException or
            // DivideByZeroException from the conversion.
            foreach (var epsilon in new[] { 0.0, -0.1, double.NaN })
            {
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => new CountMinSketch(epsilon, 0.01));
            }

            // Delta is a probability of failure, and the depth ln(1 / delta) is only
            // positive below one. At one and above the matrix had no rows, which did
            // not fail: Count minimises over no rows and returns its initial value, so
            // every element was reported as seen ulong.MaxValue times.
            foreach (var delta in new[] { 0.0, 1.0, 2.0, -0.5, double.NaN })
            {
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => new CountMinSketch(0.001, delta));
            }
        }

        /// <summary>
        /// The deletable filter splits its bits between data and collision
        /// information, so the collision count has to leave room for the data.
        /// <para>
        /// Without this check the subtraction m - r underflowed a uint: constructing
        /// with more collision bits than the filter has silently allocated about
        /// 512 MB and reported a capacity of over four billion.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestDeletableFilterRejectsCollisionRegionLargerThanFilter()
        {
            var m = Utils.OptimalM(100, 0.01);

            var ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => new DeletableBloomFilter(100, m + 1, 0.01));
            Assert.AreEqual("r", ex.ParamName);

            Assert.Throws<ArgumentOutOfRangeException>(() => new DeletableBloomFilter(100, m, 0.01));
            Assert.Throws<ArgumentOutOfRangeException>(() => new DeletableBloomFilter(100, 0, 0.01));

            // A value leaving room for the data region is accepted.
            var f = new DeletableBloomFilter(100, 10, 0.01);
            Assert.IsLessThanOrEqualTo(m, f.Capacity());
        }
    }
}
