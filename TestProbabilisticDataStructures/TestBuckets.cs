using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Direct tests for the bucket array every counting structure sits on. Most of
    /// its behavior is covered many times over through the filters, but some of it is
    /// reachable only from inside the library, so no test through a public structure
    /// can exercise it: every structure's persistence reader validates lengths before
    /// Restore runs, none constructs zero-bit buckets, each filter's merge checks its
    /// own geometry before calling <c>Union</c> or <c>Add</c>, and the only structures
    /// that union buckets use one-bit ones. The mutation sweep reported the first two
    /// as untested and, for once among its reports, was right; the other two are the
    /// same shape and are closed the same way.
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

        /// <summary>
        /// Two bucket arrays that disagree about either how many buckets there are or
        /// how wide each is describe different positions, and combining them would
        /// pair up buckets that mean different things.
        /// </summary>
        /// <remarks>
        /// Reachable only from inside the library. The filters that combine buckets
        /// check their own geometry first and throw their own exception, so a guard
        /// here that went missing, or that required <em>both</em> fields to differ
        /// before refusing, would change nothing a public test can see. Each field is
        /// varied alone for that reason: a pair differing in both is refused by either
        /// spelling of the condition and tells the two apart not at all. Without the
        /// guard a pair either combines quietly or, where the other array is the
        /// shorter, runs off the end of it with a different exception; the test
        /// rejects both, and the quiet case is the one it most needs to see.
        /// </remarks>
        [TestMethod]
        [DataRow(8u, (byte)4, 9u, (byte)4, "count only, the other larger")]
        [DataRow(9u, (byte)4, 8u, (byte)4, "count only, the other smaller")]
        [DataRow(8u, (byte)4, 8u, (byte)3, "width only, the other narrower")]
        [DataRow(8u, (byte)3, 8u, (byte)4, "width only, the other wider")]
        [DataRow(8u, (byte)1, 8u, (byte)2, "width only, one-bit against two-bit")]
        public void TestCombiningBucketsOfDifferentGeometryIsRefused(
            uint thisCount, byte thisSize, uint otherCount, byte otherSize, string what)
        {
            var before = new Buckets(thisCount, thisSize);
            for (uint i = 0; i < thisCount; i++)
            {
                before.Set(i, (byte)(i % before.MaxBucketValue() + 1));
            }
            var snapshot = before.RawData.ToArray();
            var other = new Buckets(otherCount, otherSize);

            Assert.ThrowsExactly<ArgumentException>(() => before.Union(other),
                $"Union must refuse buckets that differ in {what}.");
            CollectionAssert.AreEqual(snapshot, before.RawData.ToArray(),
                $"A refused Union ({what}) must leave the receiver as it found it.");

            Assert.ThrowsExactly<ArgumentException>(() => before.Add(other),
                $"Add must refuse buckets that differ in {what}.");
            CollectionAssert.AreEqual(snapshot, before.RawData.ToArray(),
                $"A refused Add ({what}) must leave the receiver as it found it.");
        }

        /// <summary>
        /// Unioning wider buckets takes each bucket's larger value. It does not OR the
        /// bits, which is what one-bit buckets do and what a byte-at-a-time shortcut
        /// would do to all of them.
        /// </summary>
        /// <remarks>
        /// Reachable only from inside the library: the structures that union buckets
        /// are the Bloom filters, and their buckets are one bit wide. For a one-bit
        /// bucket the maximum and the OR are the same thing, which is why they get
        /// away with the whole-byte path; for anything wider they are not, and 5 | 3
        /// is 7 while the larger of the two is 5.
        /// <para>
        /// Every pair of values the width can hold is unioned, one pair per bucket, so
        /// there is no choice of pairs to be unlucky in. A strided sample was tried
        /// first and failed its own vacuity guard at the narrow widths: two strides
        /// that are not independent mod 4 only ever produce pairs whose OR is their
        /// maximum. The widths include 3 and 5, which straddle byte boundaries -- a
        /// union that takes the larger bucket reads and writes through
        /// <c>Get</c> and <c>Set</c>, and a straddling bucket is where those go wrong.
        /// </para>
        /// </remarks>
        [TestMethod]
        [DataRow((byte)2)]
        [DataRow((byte)3)]
        [DataRow((byte)4)]
        [DataRow((byte)5)]
        [DataRow((byte)8)]
        public void TestUnioningWiderBucketsTakesTheLargerNotTheOr(byte bucketSize)
        {
            var values = (uint)new Buckets(1, bucketSize).MaxBucketValue() + 1;
            var count = values * values;
            var ours = new Buckets(count, bucketSize);
            var theirs = new Buckets(count, bucketSize);

            var orDiffers = 0;
            for (uint i = 0; i < count; i++)
            {
                var a = i / values;
                var b = i % values;
                ours.Set(i, (byte)a);
                theirs.Set(i, (byte)b);
                if ((a | b) != Math.Max(a, b))
                {
                    orDiffers++;
                }
            }
            Assert.IsGreaterThan(0, orDiffers,
                "No pair has an OR that differs from its larger value, so nothing " +
                "here could tell the two apart.");

            var returned = ours.Union(theirs);

            Assert.AreSame(ours, returned, "Union returns the receiver to allow chaining.");
            for (uint i = 0; i < count; i++)
            {
                var a = i / values;
                var b = i % values;
                Assert.AreEqual(Math.Max(a, b), ours.Get(i),
                    $"{bucketSize}-bit buckets holding {a} and {b} must union to the " +
                    $"larger, not their OR {a | b}.");
            }

            for (uint i = 0; i < count; i++)
            {
                Assert.AreEqual(i % values, theirs.Get(i),
                    "Union must leave its argument as it found it.");
            }
        }

        /// <summary>
        /// One-bit buckets union as a bitwise OR, taking whole bytes at a time, and
        /// that is the same as taking the larger of each pair. Held to the per-bucket
        /// statement so the shortcut and the general path cannot drift apart.
        /// </summary>
        [TestMethod]
        public void TestUnioningOneBitBucketsIsTheOrOfEachPair()
        {
            // Not a multiple of eight, so the last byte is partly used.
            const uint Count = 77;
            var ours = new Buckets(Count, 1);
            var theirs = new Buckets(Count, 1);
            var expected = new uint[Count];

            for (uint i = 0; i < Count; i++)
            {
                var a = (i % 3 == 0) ? 1u : 0u;
                var b = (i % 5 == 0) ? 1u : 0u;
                ours.Set(i, (byte)a);
                theirs.Set(i, (byte)b);
                expected[i] = a | b;
            }

            ours.Union(theirs);

            for (uint i = 0; i < Count; i++)
            {
                Assert.AreEqual(expected[i], ours.Get(i), $"Bucket {i}.");
            }
        }
    }
}
