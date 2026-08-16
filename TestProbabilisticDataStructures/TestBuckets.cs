using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Direct tests for the bucket array every counting structure sits on. Most of
    /// its behavior is covered many times over through the filters, but two guards
    /// and one formula are reachable only from inside the library, so no test
    /// through a public structure can exercise them: every structure's persistence
    /// reader validates lengths before Restore runs, and none constructs zero-bit
    /// buckets. The mutation sweep reported all three as untested and, for once
    /// among its reports, was right.
    /// </summary>
    [TestClass]
    public class TestBuckets
    {
        /// <summary>
        /// The backing array must be ceil(count * bucketSize / 8) bytes. The suite
        /// kills sizing mutations today only as a side effect -- an inflated array
        /// changes persisted payloads and the golden fixtures notice -- which ties
        /// the formula's coverage to the persistence tests' survival. This states it
        /// directly.
        /// </summary>
        [TestMethod]
        [DataRow(9585u, (byte)1)]
        [DataRow(100u, (byte)4)]
        [DataRow(64u, (byte)8)]
        [DataRow(3u, (byte)3)]
        [DataRow(1u, (byte)1)]
        public void TestBackingArrayIsSizedToTheBitCount(uint count, byte bucketSize)
        {
            var buckets = new Buckets(count, bucketSize);

            Assert.AreEqual((count * bucketSize + 7) / 8, (uint)buckets.RawData.Length,
                $"{count} buckets of {bucketSize} bit(s) are {count * bucketSize} " +
                "bits, and the array must hold exactly that many, rounded up to " +
                "whole bytes -- smaller loses the top buckets, larger is memory the " +
                "structure claims it does not use.");
        }

        /// <summary>
        /// A bucket's maximum is all-ones at its width. Wrong in either direction is
        /// quietly catastrophic for a counting filter: too high and Set stops
        /// clamping, so a saturating write wraps through the physical bit mask; too
        /// low and counters saturate early, pinning cells that removal will then
        /// never reclaim.
        /// </summary>
        [TestMethod]
        [DataRow((byte)1, (byte)1)]
        [DataRow((byte)4, (byte)15)]
        [DataRow((byte)8, (byte)255)]
        public void TestTheMaximumIsAllOnesAtTheBucketWidth(byte bucketSize, byte expected)
        {
            Assert.AreEqual(expected, new Buckets(8, bucketSize).MaxBucketValue());
        }

        /// <summary>
        /// Reachable only from inside the library: every structure's reader checks
        /// its payload lengths before handing the bytes here, so this guard exists
        /// for the caller that forgets. It must actually be there when that
        /// happens.
        /// </summary>
        [TestMethod]
        public void TestRestoreRefusesAMismatchedDataLength()
        {
            Assert.ThrowsExactly<InvalidDataException>(() =>
                Buckets.Restore(100, 4, new byte[7]),
                "100 four-bit buckets need 50 bytes; 7 must be refused, not " +
                "silently indexed out of.");
        }

        /// <summary>
        /// Zero-bit buckets can hold nothing and divide nothing; the guard must
        /// refuse them before the sizing arithmetic does something quieter.
        /// </summary>
        [TestMethod]
        public void TestZeroBitBucketsAreRefused()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                new Buckets(100, 0));
        }
    }
}
