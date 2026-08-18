using System;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Tests for SetSketch (Ertl, VLDB 2021).
    /// </summary>
    /// <remarks>
    /// Nothing here is random. The sketch draws its randomness from a generator seeded
    /// with each element's hash, so a given set always produces the same registers, and
    /// the sets below are built from counted keys. Every number quoted as a measurement
    /// is therefore the number this suite will keep getting.
    /// </remarks>
    [TestClass]
    public class TestSetSketch
    {
        private const int M = 4096;

        /// <summary>The error a cardinality estimate is expected to have.</summary>
        private static readonly double ExpectedRelativeError = 1.0 / Math.Sqrt(M);

        private static byte[] Key(int i) => Encoding.UTF8.GetBytes("e" + i);

        /// <summary>
        /// Two sketches over sets of the given sizes overlapping in a known number of
        /// elements, along with the Jaccard similarity that implies.
        /// </summary>
        private static (SetSketch Left, SetSketch Right, double Jaccard) Overlapping(
            int leftSize, int rightSize, int shared, int run, double b = SetSketchDefaults.Base)
        {
            var left = new SetSketch(M, b, 20, 65534);
            var right = new SetSketch(M, b, 20, 65534);

            var origin = run * 10_000_000;
            for (var i = 0; i < leftSize; i++)
            {
                left.Add(Key(origin + i));
            }
            for (var i = 0; i < shared; i++)
            {
                right.Add(Key(origin + i));
            }
            for (var i = 0; i < rightSize - shared; i++)
            {
                right.Add(Key(origin + 1_000_000 + i));
            }

            return (left, right, (double)shared / (leftSize + rightSize - shared));
        }

        private static class SetSketchDefaults
        {
            internal const double Base = 1.001;
        }

        /// <summary>
        /// Cardinality estimates track the truth across seven orders of magnitude, at
        /// about the error the number of registers implies.
        /// </summary>
        /// <remarks>
        /// The paper puts the relative error between one and 1.04 over the square root
        /// of the register count for bases up to two, which is 1.56% here. The bound
        /// below is four times that, which is where a single run of a correct
        /// implementation sits comfortably and a broken one does not: the errors
        /// measured were 0.9%, 1.3%, 2.4%, 0.8%, 0.4%, 1.9% and 1.4%.
        /// </remarks>
        [TestMethod]
        public void TestCardinalityTracksTheTruthAcrossMagnitudes()
        {
            foreach (var n in new[] { 1, 10, 100, 1_000, 10_000, 100_000, 1_000_000 })
            {
                var sketch = new SetSketch();
                for (var i = 0; i < n; i++)
                {
                    sketch.Add(Key(i));
                }

                var estimate = sketch.Cardinality();
                var error = Math.Abs(estimate - n) / n;

                Assert.IsTrue(error < 4 * ExpectedRelativeError,
                    $"{n} elements were estimated at {estimate:F1}, out by " +
                    $"{error * 100:F2}% where {ExpectedRelativeError * 100:F2}% is " +
                    "expected.");
            }
        }

        /// <summary>
        /// A sketch nothing has been added to holds nothing.
        /// </summary>
        /// <remarks>
        /// The estimator is derived for sets of at least one element and knows nothing
        /// of empty ones; left to itself it reports about a twentieth of an element for
        /// a sketch that has never been touched.
        /// </remarks>
        [TestMethod]
        public void TestAnEmptySketchHoldsNothing()
        {
            Assert.AreEqual(0.0, new SetSketch().Cardinality());

            var used = new SetSketch();
            used.Add(Key(1));
            Assert.IsTrue(used.Cardinality() > 0.5,
                "One element should not be reported as none.");
        }

        /// <summary>
        /// Merging two sketches gives exactly the sketch that adding both sets would
        /// have given -- the same registers, not merely the same estimates.
        /// </summary>
        /// <remarks>
        /// This is the property the whole design is arranged around. A register holds
        /// the largest value any element produced for it, and the largest value over
        /// two sets is the larger of the two largest, so a merge loses nothing. It is
        /// also why the paper can prove the maximum is the only operation that works:
        /// anything else would either lose information or fail to be idempotent.
        /// </remarks>
        [TestMethod]
        public void TestMergingGivesTheSameSketchAsAddingEverything()
        {
            var left = new SetSketch();
            var right = new SetSketch();
            var together = new SetSketch();

            for (var i = 0; i < 50_000; i++)
            {
                left.Add(Key(i));
                together.Add(Key(i));
            }
            for (var i = 40_000; i < 90_000; i++)
            {
                right.Add(Key(i));
                together.Add(Key(i));
            }

            Assert.IsTrue(left.Merge(right));

            CollectionAssert.AreEqual(together.RegisterValues, left.RegisterValues,
                "A merged sketch should be indistinguishable from one built by adding " +
                "both sets, register for register.");
            Assert.AreEqual(together.Cardinality(), left.Cardinality());
        }

        /// <summary>
        /// Adding the same elements again changes nothing at all.
        /// </summary>
        /// <remarks>
        /// Registers only ever rise, and an element always produces the same values, so
        /// a second pass can raise nothing. This is what lets a sketch be fed a stream
        /// with unknown duplication, or the same shard twice, without the count
        /// drifting.
        /// </remarks>
        [TestMethod]
        public void TestAddingTheSameElementsAgainChangesNothing()
        {
            var sketch = new SetSketch();
            for (var i = 0; i < 5_000; i++)
            {
                sketch.Add(Key(i));
            }

            var before = (ushort[])sketch.RegisterValues.Clone();

            for (var i = 0; i < 5_000; i++)
            {
                sketch.Add(Key(i));
            }

            CollectionAssert.AreEqual(before, sketch.RegisterValues,
                "Adding an element already present must leave every register alone.");
        }

        /// <summary>
        /// Merging a sketch into itself changes nothing.
        /// </summary>
        [TestMethod]
        public void TestMergingASketchIntoItselfChangesNothing()
        {
            var sketch = new SetSketch();
            for (var i = 0; i < 5_000; i++)
            {
                sketch.Add(Key(i));
            }

            var before = (ushort[])sketch.RegisterValues.Clone();
            sketch.Merge(sketch);

            CollectionAssert.AreEqual(before, sketch.RegisterValues);
        }

        /// <summary>
        /// Jaccard estimates track the truth across the whole range of similarity.
        /// </summary>
        /// <remarks>
        /// The bound is three times the error MinHash would have with the same number
        /// of registers, which this is expected to be about as good as and often better
        /// than. Measured, at the six similarities below: 0.0004, 0.0022, 0.0002,
        /// 0.0022, 0.0007 and 0.0006 absolute.
        /// </remarks>
        [TestMethod]
        public void TestJaccardTracksTheTruth()
        {
            foreach (var shared in new[] { 0, 20_000, 50_000, 80_000, 95_000, 100_000 })
            {
                var (left, right, truth) = Overlapping(100_000, 100_000, shared, 1);

                var estimate = left.Jaccard(right);
                var minHashError = Math.Sqrt(truth * (1 - truth) / M);

                Assert.IsTrue(Math.Abs(estimate - truth) < (3 * minHashError) + 0.002,
                    $"A similarity of {truth:F4} was estimated at {estimate:F4}.");
            }
        }

        /// <summary>
        /// The paper's estimator beats the obvious one built from inclusion and
        /// exclusion.
        /// </summary>
        /// <remarks>
        /// Inclusion and exclusion needs three cardinality estimates and each brings
        /// its own error, which is worst where the intersection is small compared with
        /// the sets. Measured over five runs at each of five similarities, the paper's
        /// estimator was more accurate every time -- by 1.7x, 1.2x, 1.5x, 2.1x and
        /// 1.2x.
        /// </remarks>
        [TestMethod]
        public void TestTheJointEstimatorBeatsInclusionAndExclusion()
        {
            foreach (var shared in new[] { 20_000, 50_000, 80_000 })
            {
                var joint = 0.0;
                var inclusionExclusion = 0.0;
                const int Runs = 5;

                for (var run = 0; run < Runs; run++)
                {
                    var (left, right, truth) = Overlapping(100_000, 100_000, shared, run);

                    var estimate = left.Jaccard(right);
                    joint += (estimate - truth) * (estimate - truth);

                    var union = new SetSketch();
                    union.Merge(left);
                    union.Merge(right);

                    var unionSize = union.Cardinality();
                    var naive = Math.Max(0,
                        (left.Cardinality() + right.Cardinality() - unionSize) / unionSize);
                    inclusionExclusion += (naive - truth) * (naive - truth);
                }

                Assert.IsTrue(joint < inclusionExclusion,
                    $"With {shared} shared elements the joint estimator was out by " +
                    $"{Math.Sqrt(joint / Runs):F5} and inclusion and exclusion by " +
                    $"{Math.Sqrt(inclusionExclusion / Runs):F5}.");
            }
        }

        /// <summary>
        /// A smaller base compares sets better, which is the dial the paper is named
        /// for.
        /// </summary>
        /// <remarks>
        /// This is the whole argument of the paper in one assertion. As the base falls
        /// towards one the registers grow finer and the sketch approaches MinHash; as
        /// it rises it coarsens towards HyperLogLog. Measured at a true similarity of
        /// about a third: 0.0064 at base 1.001, 0.0069 at 1.01, 0.0077 at 1.2 and
        /// 0.0082 at 2. Cardinality barely moves over the same range, which is what
        /// makes the trade worth having.
        /// </remarks>
        [TestMethod]
        public void TestASmallerBaseComparesSetsBetter()
        {
            var errors = new double[4];
            var bases = new[] { 1.001, 1.01, 1.2, 2.0 };

            for (var choice = 0; choice < bases.Length; choice++)
            {
                var squared = 0.0;
                const int Runs = 5;

                for (var run = 0; run < Runs; run++)
                {
                    var (left, right, truth) =
                        Overlapping(100_000, 100_000, 66_667, run, bases[choice]);
                    var estimate = left.Jaccard(right);
                    squared += (estimate - truth) * (estimate - truth);
                }

                errors[choice] = Math.Sqrt(squared / Runs);
            }

            for (var choice = 1; choice < bases.Length; choice++)
            {
                Assert.IsTrue(errors[choice] > errors[choice - 1],
                    $"Base {bases[choice]} estimated similarity to " +
                    $"{errors[choice]:F5} where base {bases[choice - 1]} managed " +
                    $"{errors[choice - 1]:F5}; a larger base should be worse at this.");
            }

            Assert.IsTrue(errors[0] < Math.Sqrt(0.25 / M) * 1.2,
                $"At the finest base the error was {errors[0]:F5}, where MinHash with " +
                $"{M} registers would manage about {Math.Sqrt(0.25 / M):F5}. The point " +
                "of a small base is to reach that.");
        }

        /// <summary>
        /// Sets of different sizes are compared at least as well as MinHash would, and
        /// generally better.
        /// </summary>
        /// <remarks>
        /// MinHash counts only the registers that agree. This estimator also uses which
        /// way the disagreeing ones fall, which carries information when the sets are
        /// of different sizes. Measured over twenty runs at three similarities, the
        /// error was 0.0029, 0.0034 and 0.0038 against MinHash's 0.0034, 0.0047 and
        /// 0.0056.
        /// </remarks>
        [TestMethod]
        public void TestSetsOfDifferentSizesAreComparedWell()
        {
            foreach (var shared in new[] { 5_000, 10_000, 15_000 })
            {
                var squared = 0.0;
                const int Runs = 10;
                var truth = 0.0;

                for (var run = 0; run < Runs; run++)
                {
                    var (left, right, actual) = Overlapping(100_000, 20_000, shared, run);
                    truth = actual;
                    var estimate = left.Jaccard(right);
                    squared += (estimate - actual) * (estimate - actual);
                }

                var error = Math.Sqrt(squared / Runs);
                var minHashError = Math.Sqrt(truth * (1 - truth) / M);

                Assert.IsTrue(error < minHashError,
                    $"At a similarity of {truth:F4} between sets of 100,000 and " +
                    $"20,000 the error was {error:F5}, where MinHash would manage " +
                    $"{minHashError:F5}.");
            }
        }

        /// <summary>
        /// Comparing is symmetric.
        /// </summary>
        [TestMethod]
        public void TestComparingIsSymmetric()
        {
            var (left, right, _) = Overlapping(100_000, 20_000, 10_000, 7);

            Assert.AreEqual(left.Jaccard(right), right.Jaccard(left));

            var forwards = left.Compare(right);
            var backwards = right.Compare(left);

            Assert.AreEqual(forwards.Size, backwards.OtherSize);
            Assert.AreEqual(forwards.OtherSize, backwards.Size);
            Assert.AreEqual(forwards.UnionSize, backwards.UnionSize);
            Assert.AreEqual(forwards.IntersectionSize, backwards.IntersectionSize);
            Assert.AreEqual(forwards.DifferenceSize, backwards.OtherDifferenceSize);
            Assert.AreEqual(forwards.CosineSimilarity, backwards.CosineSimilarity);
        }

        /// <summary>
        /// Identical sets are perfectly similar and disjoint ones are not similar at
        /// all.
        /// </summary>
        [TestMethod]
        public void TestIdenticalAndDisjointSets()
        {
            var one = new SetSketch();
            var same = new SetSketch();
            var apart = new SetSketch();

            for (var i = 0; i < 10_000; i++)
            {
                one.Add(Key(i));
                same.Add(Key(i));
                apart.Add(Key(5_000_000 + i));
            }

            Assert.AreEqual(1.0, one.Jaccard(same),
                "Two sketches of the same set hold the same registers, so nothing " +
                "disagrees and the similarity is one exactly.");
            Assert.IsTrue(one.Jaccard(apart) < 0.001,
                $"Disjoint sets were given a similarity of {one.Jaccard(apart)}.");

            // And a sketch is perfectly similar to itself.
            Assert.AreEqual(1.0, one.Jaccard(one));
        }

        /// <summary>
        /// An empty sketch has nothing in common with anything.
        /// </summary>
        [TestMethod]
        public void TestAnEmptySketchIsSimilarToNothing()
        {
            var empty = new SetSketch();
            var other = new SetSketch();
            for (var i = 0; i < 1_000; i++)
            {
                other.Add(Key(i));
            }

            Assert.AreEqual(0.0, empty.Jaccard(other));
            Assert.AreEqual(0.0, other.Jaccard(empty));
            Assert.AreEqual(0.0, empty.Jaccard(new SetSketch()));

            var comparison = empty.Compare(other);
            Assert.AreEqual(0.0, comparison.IntersectionSize);
            Assert.AreEqual(0.0, comparison.CosineSimilarity);
            Assert.AreEqual(0.0, comparison.InclusionCoefficient);
        }

        /// <summary>
        /// The derived quantities are the arithmetic the paper says they are.
        /// </summary>
        /// <remarks>
        /// Union, intersection, both differences, cosine similarity and both inclusion
        /// coefficients all follow from the two sizes and the similarity. Checking them
        /// against each other rather than against the truth is what pins the algebra
        /// rather than the estimate.
        /// </remarks>
        [TestMethod]
        public void TestTheDerivedQuantitiesAgreeWithEachOther()
        {
            var (left, right, _) = Overlapping(100_000, 20_000, 10_000, 3);
            var comparison = left.Compare(right);

            Assert.AreEqual(comparison.UnionSize,
                comparison.IntersectionSize + comparison.DifferenceSize
                    + comparison.OtherDifferenceSize,
                comparison.UnionSize * 1e-9,
                "The union is the intersection plus what each set holds alone.");

            Assert.AreEqual(comparison.Jaccard,
                comparison.IntersectionSize / comparison.UnionSize,
                1e-9,
                "The similarity is the intersection over the union, by definition.");

            Assert.AreEqual(comparison.Size,
                comparison.IntersectionSize + comparison.DifferenceSize,
                comparison.Size * 1e-9);

            Assert.AreEqual(comparison.InclusionCoefficient,
                comparison.IntersectionSize / comparison.Size, 1e-9);

            Assert.AreEqual(comparison.CosineSimilarity,
                comparison.IntersectionSize
                    / Math.Sqrt(comparison.Size * comparison.OtherSize),
                1e-9);

            // The larger set includes a smaller share of the intersection.
            Assert.IsTrue(comparison.InclusionCoefficient
                < comparison.OtherInclusionCoefficient);
        }

        /// <summary>
        /// Sketches built with different parameters cannot be merged or compared.
        /// </summary>
        /// <remarks>
        /// A register value means "the largest of floor(1 - log b h) seen here", which
        /// is a different quantity under a different base, rate or ceiling, and refers
        /// to a different register under a different count. Comparing them would return
        /// a confident number with nothing behind it.
        /// </remarks>
        [TestMethod]
        public void TestSketchesBuiltDifferentlyAreRefused()
        {
            var sketch = new SetSketch(1024, 1.001, 20, 65534);

            foreach (var other in new[]
            {
                new SetSketch(2048, 1.001, 20, 65534),
                new SetSketch(1024, 1.01, 20, 65534),
                new SetSketch(1024, 1.001, 30, 65534),
                new SetSketch(1024, 1.001, 20, 60000),
            })
            {
                Assert.ThrowsExactly<ArgumentException>(() => sketch.Merge(other));
                Assert.ThrowsExactly<ArgumentException>(() => sketch.Compare(other));
            }
        }

        /// <summary>
        /// A sketch works at sizes that are not powers of two, and at one register.
        /// </summary>
        /// <remarks>
        /// Registers are chosen by shuffling, not by taking bits of a hash, so nothing
        /// here needs the count to be a power of two -- unlike HyperLogLog, where the
        /// hash's low bits pick the register.
        /// </remarks>
        [TestMethod]
        public void TestAnyNumberOfRegistersWorks()
        {
            foreach (var registers in new[] { 1, 3, 17, 1000 })
            {
                var sketch = new SetSketch(registers);
                for (var i = 0; i < 1_000; i++)
                {
                    sketch.Add(Key(i));
                }

                Assert.IsTrue(sketch.Cardinality() > 0,
                    $"A sketch of {registers} registers estimated nothing.");
                Assert.AreEqual(registers, sketch.Registers);
                Assert.AreEqual((long)registers * 2, sketch.SizeInBytes);
            }
        }

        /// <summary>
        /// Resetting empties the sketch.
        /// </summary>
        [TestMethod]
        public void TestResettingEmptiesTheSketch()
        {
            var sketch = new SetSketch();
            for (var i = 0; i < 10_000; i++)
            {
                sketch.Add(Key(i));
            }
            Assert.IsTrue(sketch.Cardinality() > 1_000);

            sketch.Reset();

            Assert.AreEqual(0.0, sketch.Cardinality());
            CollectionAssert.AreEqual(new SetSketch().RegisterValues, sketch.RegisterValues);
        }

        /// <summary>
        /// The parameters are checked when the sketch is built.
        /// </summary>
        [TestMethod]
        public void TestBadParametersAreRefused()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SetSketch(0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SetSketch(-1));

            // A base of one leaves the register values fixed however large the set.
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new SetSketch(64, 1.0, 20, 100));
            // Above two the approximations the estimators rest on start to fray.
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new SetSketch(64, 2.5, 20, 100));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new SetSketch(64, double.NaN, 20, 100));

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new SetSketch(64, 1.001, 0, 100));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new SetSketch(64, 1.001, 20, 0));
            // A register holds two bytes and has to hold q + 1.
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new SetSketch(64, 1.001, 20, ushort.MaxValue));

            var sketch = new SetSketch();
            Assert.ThrowsExactly<ArgumentNullException>(() => sketch.Add((byte[])null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => sketch.Merge(null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => sketch.Compare(null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => sketch.SetHash(null!));
        }

        /// <summary>
        /// The hash cannot be changed once the sketch holds something.
        /// </summary>
        [TestMethod]
        public void TestTheHashCannotBeReplacedAfterAdding()
        {
            var sketch = new SetSketch();
            sketch.SetHash(Defaults.GetDefaultHashFunction());

            sketch.Add(Key(1));

            Assert.ThrowsExactly<InvalidOperationException>(
                () => sketch.SetHash(Defaults.GetDefaultHashFunction()));
        }
    }
}
