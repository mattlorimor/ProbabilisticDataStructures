using System;
using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Which bucket a value falls in, pinned value by value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A payload records the index its first bucket sits at and the counts from there
    /// upward, but not the mapping that produced those indices. So the function from
    /// value to index is part of the format even though no byte of it is written down.
    /// Change it and every stored sketch starts reporting different numbers for the same
    /// counts, with nothing in the file to say so. <see cref="TestPersistenceFormatStability"/>
    /// cannot catch that: its fixtures still read, and still hold the counts they held.
    /// </para>
    /// <para>
    /// This is also the one place in the library where a whole number that reaches a
    /// payload is derived from <c>Math.Log</c>. Unlike the four basic operations and
    /// <c>Math.Sqrt</c>, a logarithm is not required to be correctly rounded, and may
    /// differ in its last bit between one platform's libm and another's. A value sitting
    /// within that last bit of a bucket boundary could therefore be filed differently on
    /// two machines. These tests run on x64 Linux, x64 Windows and arm64 macOS, which
    /// makes agreement across architectures something CI checks rather than something
    /// the library hopes for.
    /// </para>
    /// <para>
    /// The expected indices below were not produced by running this library and writing
    /// the answers down. They were derived from the paper's definition -- bucket i holds
    /// (gamma^(i-1), gamma^i], so the index is ceil(log(v)/log(gamma)) -- and computed
    /// separately from it, so a wrong rounding here would show up rather than be
    /// enshrined. That is all the derivation buys: it was done on one machine, and says
    /// nothing by itself about any other. What establishes agreement across platforms is
    /// this file running on all three of them.
    /// </para>
    /// <para>
    /// How much room there is between these values and a boundary was measured rather
    /// than assumed. Perturbing the logarithm by a whole ulp -- more than a libm
    /// difference plausibly amounts to -- moves none of the values below, at either
    /// accuracy. The one exception is 1.0, and only because its logarithm is exactly
    /// zero, so nudging it moves it off a boundary it sits exactly on; every conforming
    /// platform returns that zero, so nothing moves there either. Landing close enough
    /// to a boundary for the last bit to decide takes roughly one value in ten billion.
    /// What this file pins, then, is the mapping against being changed, not against
    /// drifting: drift of the size a logarithm can produce does not reach these values.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestDDSketchBucketStability
    {
        private const int PayloadStart = 14;

        /// <summary>
        /// Where the positive store's first bucket index sits: past the accuracy, the
        /// total count, the zero count, and the two bounds.
        /// </summary>
        private const int FirstPositiveIndexAt = PayloadStart + 8 + 8 + 8 + 8 + 8;

        /// <summary>
        /// Values spanning a microsecond to ten seconds at one percent accuracy, which
        /// is the shape of the latency data this structure exists for.
        /// </summary>
        private static readonly (double Value, int Index)[] AtOnePercent =
        {
            (1e-06, -690),
            (1e-05, -575),
            (0.0001, -460),
            (0.001, -345),
            (0.01, -230),
            (0.1, -115),
            (0.25, -69),
            (0.5, -34),
            (1.0, 0),
            (1.5, 21),
            (2.0, 35),
            (3.0, 55),
            (5.0, 81),
            (10.0, 116),
            (42.0, 187),
            (100.0, 231),
            (250.0, 277),
            (1000.0, 346),
            (1024.0, 347),
            (9999.0, 461),
            (10000.0, 461),
        };

        /// <summary>
        /// The same question at a tenth of the bucket width. Indices grow by the same
        /// factor, and a tighter accuracy is where a last-bit difference in the logarithm
        /// has the most room to matter.
        /// </summary>
        private static readonly (double Value, int Index)[] AtATenthOfAPercent =
        {
            (0.001, -3453),
            (0.5, -346),
            (1.0, 0),
            (2.0, 347),
            (10.0, 1152),
            (250.0, 2761),
            (1000.0, 3454),
        };

        /// <summary>
        /// The divisor every bucket index is computed against, for the accuracies most
        /// likely to be asked for, pinned to the bit. These are the correctly rounded
        /// logarithms, computed to sixty digits and rounded once, rather than whatever
        /// the machine that wrote this file happened to produce: a platform whose libm
        /// is a bit out here fails rather than being enshrined.
        /// </summary>
        private static readonly (double Accuracy, ulong LogGammaBits)[] Divisors =
        {
            (0.1, 0x3FC9AF93CD234415UL),
            (0.05, 0x3FB99F11CD5F7097UL),
            (0.02, 0x3FA47B9447A9A9F8UL),
            (0.01, 0x3F947B0E059D057DUL),
            (0.005, 0x3F847AEC7708D35BUL),
            (0.002, 0x3F70624F4172E1A9UL),
            (0.001, 0x3F60624E2E91ECAFUL),
        };

        [TestMethod]
        public void TestBucketIndicesHaveNotMovedAtOnePercent()
        {
            AssertBucketsAreWhereTheyWere(0.01, AtOnePercent);
        }

        [TestMethod]
        public void TestBucketIndicesHaveNotMovedAtATenthOfAPercent()
        {
            AssertBucketsAreWhereTheyWere(0.001, AtATenthOfAPercent);
        }

        /// <summary>
        /// That the number being read above is the bucket index, established without
        /// computing a logarithm to do it.
        /// </summary>
        [TestMethod]
        public void TestTheNumberBeingReadIsTheBucketIndex()
        {
            // Bucket i holds (gamma^(i-1), gamma^i], so at one percent bucket 0 is
            // (0.98019, 1], bucket 1 is (1, 1.02020] and bucket -1 is (0.96078, 0.98019].
            // Each value below sits well inside its bucket rather than near an edge, so
            // these hold whatever the last bit of a logarithm does. If FirstPositiveIndexAt
            // pointed at some other field, the corpus tests above would be pinning that
            // field's stability instead of the index's.
            Assert.AreEqual(0, StoredIndexOf(0.01, 1.0));
            Assert.AreEqual(1, StoredIndexOf(0.01, 1.01));

            // Also the signed round trip: an index is written as a u32 and goes negative
            // for everything below one, which is where most latency data lives.
            Assert.AreEqual(-1, StoredIndexOf(0.01, 0.97));
        }

        /// <summary>
        /// That the divisor itself is the same number everywhere, which the pinned values
        /// above cannot establish.
        /// </summary>
        /// <remarks>
        /// <para>
        /// gamma is <c>(1+a)/(1-a)</c>: two additions and a division, each correctly
        /// rounded by IEEE 754, so it is the same number on every platform by
        /// construction. Its logarithm is not, and this is the one place where a last-bit
        /// difference does not stay small. Every bucket index is a value's logarithm
        /// divided by this one, so being an ulp out shifts every quotient at once, by a
        /// relative 2^-52 amplified by the quotient's own magnitude -- which runs to the
        /// hundreds at one percent and the thousands at a tenth of one. That is around
        /// one value in 10^13 landing in a different bucket, five orders of magnitude
        /// likelier than a per-value difference in <c>Math.Log</c>, and correlated across
        /// the whole sketch rather than scattered.
        /// </para>
        /// <para>
        /// Pinning values cannot catch it. Twenty-eight of them at those odds would
        /// notice such a divergence about one time in 10^12, so the corpus above would
        /// pass on a platform where every sketch was quietly bucketing differently. This
        /// is the guard for the channel that carries almost all of the risk.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestTheDivisorIsTheSameNumberOnEveryPlatform()
        {
            Assert.IsGreaterThan(0, Divisors.Length,
                "the table is empty, so every assertion made about it below is vacuous.");

            foreach (var (accuracy, expected) in Divisors)
            {
                Assert.AreEqual(expected, BitConverter.DoubleToUInt64Bits(DivisorFor(accuracy)),
                    $"the divisor for accuracy {accuracy:R} is not the correctly rounded " +
                    "logarithm of its gamma on this platform. Every bucket index divides " +
                    "by this number, so a difference here is not one value in the wrong " +
                    "bucket: it is every sketch this platform builds disagreeing with " +
                    "every sketch built anywhere else.");
            }
        }

        /// <summary>
        /// The divisor a sketch actually holds. Read off the sketch rather than recomputed
        /// here, so that changing how the constructor derives it fails this test instead of
        /// slipping past a copy of the old formula.
        /// </summary>
        private static double DivisorFor(double accuracy)
        {
            var field = typeof(DDSketch).GetField(
                "logGamma", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(field,
                "DDSketch no longer has a logGamma field. If the divisor was renamed, this " +
                "test should follow it. If it is now derived some other way, the pinned " +
                "bits need deriving again from that -- not updating to whatever the new " +
                "code produces.");

            return (double)field.GetValue(new DDSketch(accuracy))!;
        }

        private static void AssertBucketsAreWhereTheyWere(
            double accuracy, (double Value, int Index)[] corpus)
        {
            Assert.IsGreaterThan(0, corpus.Length,
                "the corpus is empty, so every assertion made about it below is vacuous.");

            foreach (var (value, expected) in corpus)
            {
                Assert.AreEqual(expected, StoredIndexOf(accuracy, value),
                    $"the bucket holding {value:R} at accuracy {accuracy:R} moved. " +
                    "Stored sketches record bucket indices rather than the mapping, so " +
                    "this changes what payloads already written down mean.");
            }
        }

        /// <summary>
        /// The bucket index a value is actually filed under, read back out of the payload
        /// rather than through a quantile. A quantile would report the bucket's midpoint,
        /// which is computed with <c>Math.Pow</c>, and a difference there would be
        /// indistinguishable from a difference in the index.
        /// </summary>
        private static int StoredIndexOf(double accuracy, double value)
        {
            var sketch = new DDSketch(accuracy);
            sketch.Add(value);

            using var stream = new MemoryStream();
            sketch.WriteTo(stream);

            return unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                stream.ToArray().AsSpan(FirstPositiveIndexAt)));
        }
    }
}
