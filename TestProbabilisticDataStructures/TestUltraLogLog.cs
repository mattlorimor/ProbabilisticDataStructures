using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Tests for <see cref="UltraLogLog"/>, against Otmar Ertl, "UltraLogLog: A
    /// Practical and More Space-Efficient Alternative to HyperLogLog for Approximate
    /// Distinct Counting" (VLDB 2024), and the reference implementation in
    /// dynatrace-oss/hash4j.
    /// </summary>
    /// <remarks>
    /// Two things carry most of the weight here. The first is that the estimator's
    /// coefficients are derived rather than copied, so the derivation is checked
    /// against the values the paper prints. The second is that a sketch folded down
    /// to a coarser precision is not merely close to one built at that precision --
    /// it is the same bytes, which is a far sharper thing to test than an estimate.
    /// </remarks>
    [TestClass]
    public class TestUltraLogLog
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        private static byte[] Element(int i) => Key($"element-{i}");

        /// <summary>
        /// Below the minimum precision the register index would consume hash bits the
        /// update value needs, and above the maximum the registers alone run past what
        /// this library builds.
        /// </summary>
        [TestMethod]
        public void TestPrecisionOutsideTheSupportedRangeIsRefused()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new UltraLogLog(2));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new UltraLogLog(27));
        }

        /// <summary>
        /// The estimator's four coefficients are computed from tau by the paper's
        /// equation (16) rather than transcribed. This checks the derivation against
        /// the values the paper and the reference implementation both print.
        /// </summary>
        /// <remarks>
        /// The point of deriving them is that one published constant can be checked
        /// and five cannot: a typo in a transcribed coefficient would shift every
        /// estimate slightly and look like ordinary sampling error. Here it would
        /// fail outright.
        /// </remarks>
        [TestMethod]
        public void TestEtaCoefficientsMatchThePublishedValues()
        {
            var published = new[]
            {
                4.663135422063788, 2.1378502137958524,
                2.781144650979996, 0.9824082545153715,
            };

            Assert.AreEqual(4, UltraLogLog.Eta.Length);
            for (var j = 0; j < published.Length; j++)
            {
                var relative = Math.Abs(UltraLogLog.Eta[j] - published[j]) / published[j];
                Assert.IsTrue(relative < 1e-12,
                    $"Derived eta_{j} was {UltraLogLog.Eta[j]:R} against the " +
                    $"published {published[j]:R}, a relative difference of " +
                    $"{relative:E2}. Equation (16) and the printed values must agree.");
            }
        }

        /// <summary>
        /// A register's contribution to the estimate, as this implementation computes
        /// it, against values taken from the reference implementation's table.
        /// </summary>
        /// <remarks>
        /// hash4j stores 236 of these as literals. They are generated here from tau
        /// and the coefficients, so a handful of the published entries are checked to
        /// confirm the generating expression is the one the table was built from.
        /// </remarks>
        [TestMethod]
        public void TestRegisterContributionsMatchTheReferenceTable()
        {
            // The first eight entries of hash4j's REGISTER_CONTRIBUTIONS, and the
            // last, indexed from the precision-dependent offset.
            var published = new (int Index, double Value)[]
            {
                (0, 0.8484061093359406), (1, 0.38895829052007685),
                (2, 0.5059986252327467), (3, 0.17873835725405993),
                (4, 0.48074234060273024), (5, 0.22040001471443574),
                (6, 0.2867199572932749), (7, 0.10128061935935387),
                (235, 8.792568765435867E-16),
            };

            foreach (var (index, value) in published)
            {
                var computed = UltraLogLog.Eta[index & 3] *
                    Math.Pow(2.0, -UltraLogLog.Tau * (3 + (index >> 2)));
                var relative = Math.Abs(computed - value) / value;
                Assert.IsTrue(relative < 1e-12,
                    $"Contribution {index} computed as {computed:R} against the " +
                    $"reference table's {value:R}, a relative difference of " +
                    $"{relative:E2}.");
            }
        }

        /// <summary>
        /// While the count is small enough that collisions are unlikely, the estimate
        /// is exact. An estimator that were merely close here would be wrong: the
        /// corrections for sparsely populated registers exist precisely so that the
        /// small end is not approximated.
        /// </summary>
        [TestMethod]
        public void TestSmallCountsAreExact()
        {
            var sketch = new UltraLogLog(12);

            Assert.AreEqual(0UL, sketch.Count(), "An empty sketch counted nothing.");

            for (var i = 0; i < 10; i++)
            {
                sketch.Add(Element(i));
                Assert.AreEqual((ulong)(i + 1), sketch.Count(),
                    $"After {i + 1} distinct elements the estimate should still be " +
                    "exact at this precision.");
            }
        }

        /// <summary>
        /// The measured error over many independent sketches matches the error the
        /// theory predicts, which is sqrt(V/m). This is the property the structure
        /// exists for, and the one a subtly wrong estimator would miss.
        /// </summary>
        [TestMethod]
        public void TestMeasuredErrorMatchesTheTheoreticalError()
        {
            const int trials = 120;
            const int distinct = 50000;

            foreach (var precision in new uint[] { 8, 10, 12 })
            {
                var errors = new double[trials];
                for (var t = 0; t < trials; t++)
                {
                    var sketch = new UltraLogLog(precision);
                    for (var i = 0; i < distinct; i++)
                    {
                        sketch.Add(Key($"t{t}-e{i}"));
                    }
                    errors[t] = ((double)sketch.Count() - distinct) / distinct;
                }

                var measured = Math.Sqrt(errors.Sum(e => e * e) / trials);
                var theoretical = Math.Sqrt(UltraLogLog.V / (1 << (int)precision));

                Assert.IsTrue(measured < theoretical * 1.5,
                    $"At p={precision} the measured relative error over {trials} " +
                    $"sketches was {measured:P3} against a predicted " +
                    $"{theoretical:P3}. An estimator delivering materially worse " +
                    "than its own bound is not the estimator described.");
                Assert.IsTrue(measured > theoretical * 0.5,
                    $"At p={precision} the measured error {measured:P3} was far " +
                    $"below the predicted {theoretical:P3}. That is not good news: " +
                    "it means the test is not measuring what it thinks it is.");
            }
        }

        /// <summary>
        /// The structure is more accurate per register than <see cref="HyperLogLog"/>,
        /// which is the whole reason it exists. Both are given the same number of
        /// registers and the same stream.
        /// </summary>
        [TestMethod]
        public void TestItBeatsHyperLogLogAtEqualRegisterCounts()
        {
            const int trials = 60;
            const int distinct = 50000;
            const uint precision = 10;
            const uint registers = 1u << (int)precision;

            var ultraErrors = new double[trials];
            var hyperErrors = new double[trials];
            for (var t = 0; t < trials; t++)
            {
                var ultra = new UltraLogLog(precision);
                var hyper = new HyperLogLog(registers);
                for (var i = 0; i < distinct; i++)
                {
                    var element = Key($"c{t}-e{i}");
                    ultra.Add(element);
                    hyper.Add(element);
                }
                ultraErrors[t] = ((double)ultra.Count() - distinct) / distinct;
                hyperErrors[t] = ((double)hyper.Count() - distinct) / distinct;
            }

            var ultraError = Math.Sqrt(ultraErrors.Sum(e => e * e) / trials);
            var hyperError = Math.Sqrt(hyperErrors.Sum(e => e * e) / trials);

            Assert.IsTrue(ultraError < hyperError,
                $"With {registers} registers each, UltraLogLog's error was " +
                $"{ultraError:P3} and HyperLogLog's {hyperError:P3}. The extra two " +
                "bits per register are supposed to buy accuracy; if they do not, " +
                "they are only costing space.");
        }

        /// <summary>
        /// Adding an element the structure already holds changes nothing at all. The
        /// register update is a union of bits, so a repeated element cannot move it.
        /// </summary>
        [TestMethod]
        public void TestAddingIsIdempotent()
        {
            var sketch = new UltraLogLog(10);
            for (var i = 0; i < 5000; i++)
            {
                sketch.Add(Element(i));
            }

            var before = sketch.ToByteArray();
            for (var round = 0; round < 3; round++)
            {
                for (var i = 0; i < 5000; i++)
                {
                    sketch.Add(Element(i));
                }
            }

            CollectionAssert.AreEqual(before, sketch.ToByteArray(),
                "Re-adding every element three times over changed the sketch, so " +
                "the count depends on how often elements arrive rather than on how " +
                "many of them are distinct.");
        }

        /// <summary>
        /// Merging two sketches gives exactly the sketch the union of their elements
        /// would have produced -- the same bytes, not merely a similar estimate.
        /// </summary>
        [TestMethod]
        public void TestMergingEqualsBuildingFromTheUnion()
        {
            var left = new UltraLogLog(10);
            var right = new UltraLogLog(10);
            var union = new UltraLogLog(10);

            for (var i = 0; i < 20000; i++)
            {
                (i % 2 == 0 ? left : right).Add(Element(i));
                union.Add(Element(i));
            }

            CollectionAssert.AreEqual(union.ToByteArray(), left.Merge(right).ToByteArray(),
                "A merge must be indistinguishable from having added everything to " +
                "one sketch.");
        }

        /// <summary>
        /// The result does not depend on the order sketches are merged in.
        /// </summary>
        [TestMethod]
        public void TestMergingIsOrderIndependent()
        {
            static (UltraLogLog Left, UltraLogLog Right) Build()
            {
                var left = new UltraLogLog(10);
                var right = new UltraLogLog(10);
                for (var i = 0; i < 8000; i++)
                {
                    (i % 2 == 0 ? left : right).Add(Element(i));
                }
                return (left, right);
            }

            var (l1, r1) = Build();
            var (l2, r2) = Build();

            CollectionAssert.AreEqual(
                l1.Merge(r1).ToByteArray(), r2.Merge(l2).ToByteArray());
        }

        /// <summary>
        /// A sketch built at a fine precision and folded down to a coarser one is
        /// byte for byte the sketch that precision would have produced from the same
        /// stream.
        /// </summary>
        /// <remarks>
        /// This is stronger than it may look, and it is what makes folding safe to
        /// offer at all. A register records the absolute position of the bit a run of
        /// zeros stopped at, so the same element yields the same position at every
        /// precision; the registers a fold has to collapse away contribute the exact
        /// position their index implies. Had the register stored the length of the
        /// run instead -- the more obvious choice, and the one HyperLogLog makes --
        /// the fold would be an approximation and this test would fail.
        /// </remarks>
        [TestMethod]
        public void TestFoldingDownEqualsBuildingAtTheCoarserPrecision()
        {
            foreach (var (fine, coarse) in new[] { (14u, 10u), (12u, 11u), (16u, 8u) })
            {
                var built = new UltraLogLog(fine);
                var direct = new UltraLogLog(coarse);
                for (var i = 0; i < 100000; i++)
                {
                    built.Add(Element(i));
                    direct.Add(Element(i));
                }

                var folded = new UltraLogLog(coarse).Merge(built);

                CollectionAssert.AreEqual(direct.ToByteArray(), folded.ToByteArray(),
                    $"Folding p={fine} down to p={coarse} produced a different " +
                    $"sketch from building at p={coarse} directly.");
            }
        }

        /// <summary>
        /// A coarse sketch cannot be merged into a finer one. The register indices it
        /// never recorded cannot be invented, and a result that pretended otherwise
        /// would report an error-free estimate of something it never saw.
        /// </summary>
        [TestMethod]
        public void TestMergingIntoAFinerSketchIsRefused()
        {
            var fine = new UltraLogLog(12);
            var coarse = new UltraLogLog(8);
            for (var i = 0; i < 1000; i++)
            {
                fine.Add(Element(i));
                coarse.Add(Element(i));
            }

            Assert.ThrowsExactly<ArgumentException>(() => fine.Merge(coarse));
        }

        /// <summary>
        /// Two sketches that hash differently cannot be merged: every register sits
        /// where its hash put it.
        /// </summary>
        [TestMethod]
        public void TestMergingDifferentHashesIsRefused()
        {
            var first = new UltraLogLog(10);
            var second = new UltraLogLog(10, data => 12345UL);

            Assert.ThrowsExactly<ArgumentException>(() => first.Merge(second));
        }

        /// <summary>
        /// A sketch written and read back is the same sketch, and goes on counting
        /// the way the original would have.
        /// </summary>
        [TestMethod]
        public void TestRoundTripIsByteExactAndKeepsCounting()
        {
            var sketch = new UltraLogLog(12);
            for (var i = 0; i < 50000; i++)
            {
                sketch.Add(Element(i));
            }

            var written = sketch.ToByteArray();
            var restored = UltraLogLog.ReadFrom(new MemoryStream(written));

            CollectionAssert.AreEqual(written, restored.ToByteArray());
            Assert.AreEqual(sketch.Count(), restored.Count());
            Assert.AreEqual(sketch.Precision(), restored.Precision());

            for (var i = 50000; i < 60000; i++)
            {
                sketch.Add(Element(i));
                restored.Add(Element(i));
            }

            Assert.AreEqual(sketch.Count(), restored.Count(),
                "A restored sketch must keep estimating, not merely report the " +
                "number it was carrying.");
        }

        /// <summary>
        /// Reset returns the sketch to the state it was built in.
        /// </summary>
        [TestMethod]
        public void TestResetEmptiesTheSketch()
        {
            var sketch = new UltraLogLog(10);
            for (var i = 0; i < 10000; i++)
            {
                sketch.Add(Element(i));
            }

            sketch.Reset();

            Assert.AreEqual(0UL, sketch.Count());
            CollectionAssert.AreEqual(new UltraLogLog(10).ToByteArray(),
                sketch.ToByteArray());
        }

        /// <summary>
        /// Sizing by error rate gives a sketch whose stated error is at least as good
        /// as the one asked for.
        /// </summary>
        [TestMethod]
        public void TestSizingByErrorRate()
        {
            foreach (var requested in new[] { 0.05, 0.02, 0.01, 0.005 })
            {
                var sketch = UltraLogLog.NewDefault(requested);
                Assert.IsTrue(sketch.RelativeError() <= requested,
                    $"Asked for {requested:P2}, got a sketch stating " +
                    $"{sketch.RelativeError():P3}.");
            }

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => UltraLogLog.NewDefault(0.0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => UltraLogLog.NewDefault(1.0));
        }

        /// <summary>
        /// The hash cannot be replaced once anything has been added, because
        /// everything already counted sits where the old hash put it.
        /// </summary>
        [TestMethod]
        public void TestHashCannotBeReplacedAfterAdding()
        {
            var sketch = new UltraLogLog(10);
            sketch.SetHash(data => 42UL);

            sketch.Add(Element(1));

            Assert.ThrowsExactly<InvalidOperationException>(
                () => sketch.SetHash(data => 7UL));
        }

        /// <summary>
        /// Packing and unpacking a register is a round trip for every value an
        /// insertion can produce, and an empty register unpacks to nothing.
        /// </summary>
        [TestMethod]
        public void TestPackAndUnpackAreInverse()
        {
            Assert.AreEqual(0UL, UltraLogLog.Unpack(0),
                "An untouched register stands for no update values at all.");

            for (var position = 2; position <= 63; position++)
            {
                for (var low = 0; low < 4; low++)
                {
                    var register = (byte)((position << 2) | low);
                    var unpacked = UltraLogLog.Unpack(register);
                    Assert.AreEqual(register, UltraLogLog.Pack(unpacked),
                        $"Register {register} (position {position}, low bits " +
                        $"{low}) did not survive a pack of its own unpacking.");
                }
            }
        }
    }
}
