using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Tests for TupleSketch, the theta sketch carrying a value per distinct key.
    /// </summary>
    [TestClass]
    public class TestTupleSketch
    {
        private static byte[] Key(int i) => Encoding.UTF8.GetBytes("user-" + i);

        /// <summary>
        /// A key added several times counts once, and its values are folded rather than
        /// counted separately.
        /// </summary>
        /// <remarks>
        /// This is the whole difference between a tuple sketch and summing a column.
        /// Small enough that nothing is discarded, so both answers are exact and any
        /// discrepancy is the folding's fault rather than the sampling's.
        /// </remarks>
        [TestMethod]
        public void TestRepeatedKeysAreFoldedRatherThanCounted()
        {
            var sketch = new TupleSketch(4096);

            for (var i = 0; i < 500; i++)
            {
                // Each key three times, so a sketch that counted records rather than
                // keys would report 1,500.
                sketch.Add(Key(i), 2.0);
                sketch.Add(Key(i), 3.0);
                sketch.Add(Key(i), 5.0);
            }

            Assert.AreEqual(500UL, sketch.Count(),
                "Five hundred distinct keys were added, three records each.");
            Assert.AreEqual(5_000.0, sketch.Total(), 1e-9,
                "Each key's three values total ten, so five hundred keys total 5,000.");
        }

        /// <summary>
        /// A key's summary does not depend on the order its values arrived in.
        /// </summary>
        /// <remarks>
        /// The folding runs in whatever order a sort leaves things, and across as many
        /// compactions as the sketch happens to perform, so a summary that depended on
        /// order would not be well defined at all. Kept below the size at which the
        /// sketch discards anything, because which keys survive <em>is</em> order
        /// dependent -- see the test that says so.
        /// </remarks>
        [TestMethod]
        public void TestAKeysSummaryDoesNotDependOnOrder()
        {
            var records = new List<(int Key, double Value)>();
            var random = new Random(4);
            for (var i = 0; i < 3_000; i++)
            {
                records.Add((random.Next(100), random.Next(1, 100)));
            }

            var forwards = new TupleSketch(4096);
            var backwards = new TupleSketch(4096);

            foreach (var (key, value) in records)
            {
                forwards.Add(Key(key), value);
            }
            for (var i = records.Count - 1; i >= 0; i--)
            {
                backwards.Add(Key(records[i].Key), records[i].Value);
            }

            Assert.AreEqual(forwards.Count(), backwards.Count());
            Assert.AreEqual(forwards.Total(), backwards.Total(),
                "Totalling the same records in the opposite order gave a different " +
                "answer, so the fold is not order-independent.");
            CollectionAssert.AreEqual(forwards.KeysHeld, backwards.KeysHeld);
            CollectionAssert.AreEqual(forwards.SummariesHeld, backwards.SummariesHeld);
        }

        /// <summary>
        /// Which keys survive does depend on order, exactly as it does for a
        /// <see cref="ThetaSketch"/>.
        /// </summary>
        /// <remarks>
        /// Worth a test because it is a limitation rather than a guarantee, and one
        /// inherited rather than introduced: the sampling threshold lands wherever the
        /// sketch last ran out of room, and where that falls in a stream depends on the
        /// order of the stream. Both structures are checked here so that a change to
        /// either shows up as the two of them disagreeing.
        /// </remarks>
        [TestMethod]
        public void TestWhichKeysSurviveDependsOnOrderJustAsThetaDoes()
        {
            var keys = new List<int>();
            var random = new Random(9);
            for (var i = 0; i < 5_000; i++)
            {
                keys.Add(random.Next(5_000));
            }

            var thetaForwards = new ThetaSketch(256);
            var thetaBackwards = new ThetaSketch(256);
            var tupleForwards = new TupleSketch(256);
            var tupleBackwards = new TupleSketch(256);

            foreach (var key in keys)
            {
                thetaForwards.Add(Key(key));
                tupleForwards.Add(Key(key), 1.0);
            }
            for (var i = keys.Count - 1; i >= 0; i--)
            {
                thetaBackwards.Add(Key(keys[i]));
                tupleBackwards.Add(Key(keys[i]), 1.0);
            }

            Assert.AreNotEqual(thetaForwards.Retained(), thetaBackwards.Retained(),
                "This test exists because a theta sketch keeps a different sample " +
                "depending on order. If that has stopped being true, the tuple sketch " +
                "below should be re-examined rather than this assertion relaxed.");
            Assert.AreNotEqual(tupleForwards.Retained(), tupleBackwards.Retained(),
                "The tuple sketch should inherit that, being the same sampling.");

            // Both remain good estimates of the same thing, which is the point.
            var distinct = new HashSet<int>(keys).Count;
            foreach (var estimate in new[] { tupleForwards.Count(), tupleBackwards.Count() })
            {
                Assert.AreEqual(distinct, (double)estimate, distinct * 0.1,
                    $"{estimate} is not a usable estimate of {distinct} distinct keys.");
            }
        }

        /// <summary>
        /// A summary stays beside the key it belongs to when the sketch sorts and
        /// discards.
        /// </summary>
        /// <remarks>
        /// The keys and their summaries live in two arrays that have to be sorted and
        /// trimmed together. If they ever drift apart the sketch does not fail, it
        /// answers with the wrong key's value -- and the totals stay plausible, because
        /// the same numbers are still being added up. Giving every key a value only it
        /// could have is what makes a swap visible.
        /// </remarks>
        [TestMethod]
        public void TestSummariesStayWithTheirKeysThroughDiscarding()
        {
            var sketch = new TupleSketch(256, SummaryPolicy.Max);
            var hash = Defaults.GetDefaultHashFunction();
            var expected = new Dictionary<ulong, double>();

            for (var i = 0; i < 20_000; i++)
            {
                // The value identifies the key and nothing else.
                double value = i + 1;
                sketch.Add(Key(i), value);
                expected[hash(Key(i))] = value;
            }

            Assert.IsTrue(sketch.Retained() > 200,
                "This is only interesting if the sketch discarded and kept a sample.");

            var keys = sketch.KeysHeld;
            var summaries = sketch.SummariesHeld;

            for (var i = 0; i < keys.Length; i++)
            {
                Assert.AreEqual(expected[keys[i]], summaries[i],
                    $"The key at position {i} is carrying a summary of " +
                    $"{summaries[i]} where its own value was {expected[keys[i]]}.");
            }
        }

        /// <summary>
        /// The total is scaled by the sampling rate, the same way the count is.
        /// </summary>
        /// <remarks>
        /// Once the sketch is discarding, the keys it holds are a uniform sample of the
        /// distinct keys, so their values are a uniform sample of the per-key totals.
        /// With every key worth the same, the total has to track the count exactly --
        /// which is the sharpest available check that the scaling is applied at all,
        /// and applied once.
        /// </remarks>
        [TestMethod]
        public void TestTheTotalIsScaledLikeTheCount()
        {
            var sketch = new TupleSketch(1024);
            for (var i = 0; i < 100_000; i++)
            {
                sketch.Add(Key(i), 7.0);
            }

            Assert.IsTrue(sketch.Retained() < 100_000,
                "This is only interesting once the sketch is sampling.");

            // Within one key's worth: the count is rounded to a whole number of keys
            // and the total is not, so they can differ by up to the value of one key.
            Assert.AreEqual(sketch.Count() * 7.0, sketch.Total(), 7.0,
                "Every key is worth seven, so the total is seven times the count.");
            Assert.AreEqual(100_000.0, sketch.Total() / 7.0, 100_000 * 0.1,
                "The estimate is a long way from the hundred thousand keys added.");
        }

        /// <summary>
        /// A sketch holding everything is exact.
        /// </summary>
        [TestMethod]
        public void TestASketchThatDiscardedNothingIsExact()
        {
            var sketch = new TupleSketch(4096);
            for (var i = 0; i < 1_000; i++)
            {
                sketch.Add(Key(i), i);
            }

            Assert.AreEqual(1_000UL, sketch.Count());
            Assert.AreEqual(1_000 * 999 / 2.0, sketch.Total(), 1e-9);
        }

        /// <summary>
        /// The smallest and largest policies keep the smallest and largest.
        /// </summary>
        [TestMethod]
        public void TestTheOtherPoliciesKeepTheSmallestAndLargest()
        {
            var smallest = new TupleSketch(64, SummaryPolicy.Min);
            var largest = new TupleSketch(64, SummaryPolicy.Max);

            foreach (var value in new[] { 5.0, 2.0, 9.0, 7.0 })
            {
                smallest.Add(Key(1), value);
                largest.Add(Key(1), value);
            }

            Assert.AreEqual(1UL, smallest.Count());
            Assert.AreEqual(2.0, smallest.Total());
            Assert.AreEqual(9.0, largest.Total());
            Assert.AreEqual(SummaryPolicy.Min, smallest.Policy);
        }

        /// <summary>
        /// Union, intersection and difference carry the summaries their keys deserve.
        /// </summary>
        /// <remarks>
        /// Small enough that nothing is discarded, so the arithmetic is exact and the
        /// test is about the set operations rather than about sampling. Keys in both
        /// sides are folded; keys on one side are carried as they stand.
        /// </remarks>
        [TestMethod]
        public void TestSetOperationsFoldTheSummariesTheyShould()
        {
            var left = new TupleSketch(4096);
            var right = new TupleSketch(4096);

            for (var i = 0; i < 200; i++)
            {
                left.Add(Key(i), 2.0);
            }
            for (var i = 100; i < 300; i++)
            {
                right.Add(Key(i), 3.0);
            }

            var union = left.Union(right);
            Assert.AreEqual(300UL, union.Count());
            Assert.AreEqual((100 * 2.0) + (100 * 5.0) + (100 * 3.0), union.Total(), 1e-9,
                "A hundred keys on the left alone at two, a hundred shared at two plus " +
                "three, and a hundred on the right alone at three.");

            var shared = left.Intersect(right);
            Assert.AreEqual(100UL, shared.Count());
            Assert.AreEqual(100 * 5.0, shared.Total(), 1e-9,
                "The shared keys carry both sides' values folded together.");

            var only = left.Difference(right);
            Assert.AreEqual(100UL, only.Count());
            Assert.AreEqual(100 * 2.0, only.Total(), 1e-9,
                "Keys only on the left keep the left's values and nothing else.");

            // Neither original was disturbed.
            Assert.AreEqual(200UL, left.Count());
            Assert.AreEqual(400.0, left.Total(), 1e-9);
            Assert.AreEqual(200UL, right.Count());
            Assert.AreEqual(600.0, right.Total(), 1e-9);
        }

        /// <summary>
        /// Set operations hold up once the sketches are sampling.
        /// </summary>
        [TestMethod]
        public void TestSetOperationsHoldUpWhileSampling()
        {
            var left = new TupleSketch(2048);
            var right = new TupleSketch(2048);

            for (var i = 0; i < 40_000; i++)
            {
                left.Add(Key(i), 2.0);
            }
            for (var i = 20_000; i < 60_000; i++)
            {
                right.Add(Key(i), 3.0);
            }

            var union = left.Union(right);
            Assert.AreEqual(60_000.0, union.Count(), 60_000 * 0.1);
            Assert.AreEqual((20_000 * 2.0) + (20_000 * 5.0) + (20_000 * 3.0),
                union.Total(), 200_000 * 0.15);

            var shared = left.Intersect(right);
            Assert.AreEqual(20_000.0, shared.Count(), 20_000 * 0.15);
            Assert.AreEqual(20_000 * 5.0, shared.Total(), 100_000 * 0.15);
        }

        /// <summary>
        /// A set operation that overflows what the result may keep discards down to it,
        /// and what it keeps is still a sample the sketch can describe.
        /// </summary>
        /// <remarks>
        /// The set operations build their result from scratch and trim it themselves,
        /// which is a second place the sampling threshold gets chosen and one that
        /// ordinary use does not reach: it needs two sketches that each held everything
        /// they saw and that together hold more than the result may. The invariant is
        /// that every key kept is strictly below the threshold, and the reader is what
        /// checks it -- a sketch whose threshold is off by one key writes a payload it
        /// cannot read back.
        /// </remarks>
        [TestMethod]
        public void TestASetOperationThatOverflowsTrimsToASketchItCanStillWrite()
        {
            // Three hundred keys each, below the six hundred at which these sketches
            // discard, so both are exact and their union is not.
            var left = new TupleSketch(256);
            var right = new TupleSketch(256);

            for (var i = 0; i < 300; i++)
            {
                left.Add(Key(i), 2.0);
                right.Add(Key(10_000 + i), 3.0);
            }

            Assert.AreEqual(300UL, left.Count(), "The inputs should not have discarded.");
            Assert.AreEqual(300UL, right.Count());

            var union = left.Union(right);

            Assert.AreEqual(256U, union.Retained(),
                "Six hundred keys is past what a 256-key sketch may hold, so the union " +
                "should have discarded down to it.");
            Assert.AreEqual(600.0, union.Count(), 600 * 0.15);

            var stream = new MemoryStream();
            union.WriteTo(stream);
            stream.Position = 0;
            var read = TupleSketch.ReadFrom(stream);

            Assert.AreEqual(union.Count(), read.Count());
            Assert.AreEqual(union.Total(), read.Total());
        }

        /// <summary>
        /// Combining sketches that sampled at different rates is trusted only at the
        /// lower of the two.
        /// </summary>
        /// <remarks>
        /// One sketch here kept everything it saw and the other kept a small sample.
        /// Above the sampled one's threshold, a key is in the first sketch's list simply
        /// because it existed and absent from the second's because it was never
        /// sampled -- so counting anything up there mixes a census with a survey.
        /// Dropping to the lower threshold throws away real keys, and that is the
        /// point: what is left was sampled the same way on both sides.
        /// <para>
        /// Two sketches fed the same amount will have much the same threshold, so
        /// nothing catches this unless they are deliberately lopsided.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestCombiningSketchesThatSampledDifferentlyUsesTheLowerRate()
        {
            var exact = new TupleSketch(1024);
            for (var i = 0; i < 200; i++)
            {
                exact.Add(Key(i), 1.0);
            }
            Assert.AreEqual(200UL, exact.Count(), "This side should have kept everything.");

            var sampled = new TupleSketch(1024);
            for (var i = 10_000; i < 110_000; i++)
            {
                sampled.Add(Key(i), 1.0);
            }
            Assert.IsTrue(sampled.Retained() < 100_000, "This side should be sampling.");

            var union = exact.Union(sampled);

            // Almost all of the union is the sampled side, so the estimate has to come
            // out near its size rather than near the number of keys actually held.
            Assert.AreEqual(100_200.0, union.Count(), 100_200 * 0.1,
                $"The union reports {union.Count()} distinct keys where about 100,200 " +
                "were added between the two sketches.");
            Assert.AreEqual(100_200.0, union.Total(), 100_200 * 0.1,
                "Every key is worth one, so the total should track the count.");
        }

        /// <summary>
        /// A total over lopsided values carries much more error than the count does.
        /// </summary>
        /// <remarks>
        /// Measured, and worth stating rather than hiding. The sample is uniform over
        /// keys, not weighted by value, so a sketch that happens to keep a few big
        /// spenders reads high and one that misses them reads low. Over five runs where
        /// one key in a hundred was worth a thousand times the rest, the count was out
        /// by half a percent and the total by between 0.9% and 18%.
        /// <para>
        /// The assertion is deliberately weak. Pinning the error tightly would be
        /// pinning the seeds; what is worth holding is that the count stays accurate
        /// while the total does not, because that is the thing a caller has to know.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestATotalOverLopsidedValuesIsMuchLessCertainThanTheCount()
        {
            var worstCount = 0.0;
            var worstTotal = 0.0;

            for (var run = 0; run < 5; run++)
            {
                var sketch = new TupleSketch(4096);
                var random = new Random(run);
                var truth = 0.0;

                for (var i = 0; i < 100_000; i++)
                {
                    var value = random.NextDouble() < 0.01 ? 1_000.0 : 1.0;
                    sketch.Add(Key(i), value);
                    truth += value;
                }

                worstCount = Math.Max(worstCount,
                    Math.Abs(sketch.Count() - 100_000.0) / 100_000.0);
                worstTotal = Math.Max(worstTotal,
                    Math.Abs(sketch.Total() - truth) / truth);
            }

            Assert.IsTrue(worstCount < 0.02,
                $"The count was out by {worstCount * 100:F2}%, which is worse than " +
                "this sampling should manage.");
            Assert.IsTrue(worstTotal > worstCount * 2,
                $"The total was out by {worstTotal * 100:F2}% and the count by " +
                $"{worstCount * 100:F2}%. If lopsided values have stopped costing the " +
                "total more than the count, the documentation warning about it is " +
                "wrong and should be removed.");
        }

        /// <summary>
        /// Sketches that do not mean the same thing are refused.
        /// </summary>
        [TestMethod]
        public void TestIncompatibleSketchesAreRefused()
        {
            var sketch = new TupleSketch(1024);

            foreach (var other in new[]
            {
                new TupleSketch(2048),
                new TupleSketch(1024, SummaryPolicy.Max),
            })
            {
                Assert.ThrowsExactly<ArgumentException>(() => sketch.Union(other));
                Assert.ThrowsExactly<ArgumentException>(() => sketch.Intersect(other));
                Assert.ThrowsExactly<ArgumentException>(() => sketch.Difference(other));
            }
        }

        /// <summary>
        /// The parameters are checked.
        /// </summary>
        [TestMethod]
        public void TestBadParametersAreRefused()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new TupleSketch(0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new TupleSketch(64, (SummaryPolicy)99));

            var sketch = new TupleSketch(64);
            Assert.ThrowsExactly<ArgumentNullException>(
                () => sketch.Add((byte[])null!, 1.0));
            Assert.ThrowsExactly<ArgumentNullException>(() => sketch.Union(null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => sketch.Intersect(null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => sketch.Difference(null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => sketch.SetHash(null!));
        }

        /// <summary>
        /// A value that is not a number is refused rather than stored.
        /// </summary>
        /// <remarks>
        /// It would spread to every total the key ever took part in, and under the
        /// smallest and largest policies it compares as neither, so it would win or
        /// lose depending on which side of the comparison it landed.
        /// </remarks>
        [TestMethod]
        public void TestAValueThatIsNotANumberIsRefused()
        {
            var sketch = new TupleSketch(64);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => sketch.Add(Key(1), double.NaN));

            // Infinities are allowed through: they are a real answer to "what is the
            // largest", and a total that overflows is the caller's arithmetic.
            sketch.Add(Key(1), double.PositiveInfinity);
            Assert.AreEqual(double.PositiveInfinity, sketch.Total());
        }

        /// <summary>
        /// The hash cannot be changed once the sketch holds something.
        /// </summary>
        [TestMethod]
        public void TestTheHashCannotBeReplacedAfterAdding()
        {
            var sketch = new TupleSketch(64);
            sketch.SetHash(Defaults.GetDefaultHashFunction());

            sketch.Add(Key(1), 1.0);

            Assert.ThrowsExactly<InvalidOperationException>(
                () => sketch.SetHash(Defaults.GetDefaultHashFunction()));
        }

        /// <summary>
        /// A sketch survives a round trip through a stream.
        /// </summary>
        [TestMethod]
        public void TestASketchSurvivesARoundTrip()
        {
            var sketch = new TupleSketch(512, SummaryPolicy.Max);
            for (var i = 0; i < 20_000; i++)
            {
                sketch.Add(Key(i), i + 1);
            }

            var stream = new MemoryStream();
            sketch.WriteTo(stream);
            stream.Position = 0;
            var read = TupleSketch.ReadFrom(stream);

            Assert.AreEqual(sketch.Count(), read.Count());
            Assert.AreEqual(sketch.Total(), read.Total());
            Assert.AreEqual(sketch.Retained(), read.Retained());
            Assert.AreEqual(sketch.Policy, read.Policy);
            CollectionAssert.AreEqual(sketch.KeysHeld, read.KeysHeld);
            CollectionAssert.AreEqual(sketch.SummariesHeld, read.SummariesHeld);
        }

        /// <summary>
        /// An empty sketch survives a round trip.
        /// </summary>
        [TestMethod]
        public void TestAnEmptySketchSurvivesARoundTrip()
        {
            var stream = new MemoryStream();
            new TupleSketch(64).WriteTo(stream);
            stream.Position = 0;

            var read = TupleSketch.ReadFrom(stream);

            Assert.AreEqual(0UL, read.Count());
            Assert.AreEqual(0.0, read.Total());
        }

        /// <summary>
        /// The same order-statistic pin TestThetaSketch carries, because TupleSketch
        /// trims with its own copy of the rule: theta becomes the (k+1)-th smallest
        /// distinct hash, the k keys below it survive, and each keeps the summary
        /// that was folded for it. The pairing matters as much as the rule -- a trim
        /// that sorts keys without carrying summaries, or off by one slot, leaves
        /// every surviving key wearing a neighbor's value, which no theta-side test
        /// can notice.
        /// </summary>
        [TestMethod]
        [DataRow(1u)]
        [DataRow(32u)]
        public void TestThetaIsTheKPlusFirstSmallestHashAndSummariesFollow(uint k)
        {
            var sketch = new TupleSketch(k);

            var hashes = new ulong[2 * k];
            var values = new System.Collections.Generic.Dictionary<ulong, double>();
            for (var i = 0; i < 2 * k; i++)
            {
                var key = System.Text.Encoding.ASCII.GetBytes($"order-statistic-{i}");
                hashes[i] = sketch.Hash(key);
                values[hashes[i]] = 10.0 * i;
                sketch.Add(key, 10.0 * i);
            }

            var sorted = hashes.Distinct().OrderBy(h => h).ToArray();
            Assert.HasCount((int)(2 * k), sorted,
                "the 64 keys must hash distinctly, or the stream never reaches the " +
                "trim and theta is asserted on air.");

            Assert.AreEqual(sorted[k], sketch.ThetaValue,
                "theta must be the (k+1)-th smallest hash the stream produced.");
            CollectionAssert.AreEqual(sorted.Take((int)k).ToArray(), sketch.KeysHeld,
                "the survivors must be exactly the k hashes below theta, in order.");

            var keys = sketch.KeysHeld;
            var summaries = sketch.SummariesHeld;
            for (var i = 0; i < keys.Length; i++)
            {
                Assert.AreEqual(values[keys[i]], summaries[i],
                    $"survivor {i} must keep the summary folded for its own key, " +
                    "not a neighbor's.");
            }
        }

        /// <summary>
        /// The retained sizes the continuation tests run at: from the coarsest legal
        /// sketch to just past k = 8, where twice the retained size reaches the sixteen
        /// keys the reader and the set operations made room for. Below that they gave
        /// a sketch more room than it may hold; from there on they could not.
        /// <para>
        /// And three sizes beyond it, for the reason <see cref="TestThetaSketch"/>
        /// gives: from k = 8 on, that the buffer's route to its full size cannot matter
        /// was an argument and not a measurement. They are the sizes where the buffer
        /// doubles more than once (k = 33 goes 16, 32, 64, then stops at 66; k = 100
        /// goes up to 128, then stops at 200) and one where it takes a single cut-short
        /// step (k = 12, from 16 to 24).
        /// </para>
        /// </summary>
        public static IEnumerable<object[]> RetainedSizesAroundTheStartingBuffer =>
            Enumerable.Range(1, 9).Concat(new[] { 12, 33, 100 })
                .Select(k => new object[] { (uint)k });

        private const int ContinuationStreams = 20;

        /// <summary>
        /// How many distinct keys a continuation stream draws on: enough that a sketch
        /// of this size trims more than once, and that a trim is still to come after
        /// most splits. See <see cref="TestThetaSketch"/>.
        /// </summary>
        private static int ContinuationKeys(uint k) => Math.Max(150, 12 * (int)k);

        /// <summary>
        /// Records per stream: every key once, and then the first few again.
        /// </summary>
        private static int ContinuationLength(uint k) => ContinuationKeys(k) + 50;

        /// <summary>
        /// The streams the continuation tests split. Keys repeat, so the part of a
        /// stream after a split folds new values into summaries the sketch was handed
        /// as well as into ones it built itself.
        /// </summary>
        private static byte[] Record(uint k, int stream, int i) =>
            Encoding.UTF8.GetBytes($"t{stream}-item-{i % ContinuationKeys(k)}");

        /// <summary>
        /// Whole numbers, so that every fold is exact in any order. The sort that
        /// gathers a key's values before folding them is not stable, the order it
        /// leaves them in depends on what else is in the buffer, and floating-point
        /// addition is not associative -- so with fractional values the bytes can
        /// depend on when compactions fired as well as on which values were folded.
        /// With whole numbers they pin only the second. What fractional values do
        /// is measured in the tests that begin <c>TestFractional</c>.
        /// </summary>
        private static double ValueOf(int i) => i + 1;

        /// <summary>
        /// Saving a sketch and reading it back must be invisible to everything it
        /// does afterwards: TestThetaSketch's check, for the same defect, because this
        /// sketch sized its buffer by its own copy of the same rules. Each stream is
        /// split at every point, the sketch so far is written and read back, the rest
        /// of the stream goes to what was read, and the result must match a sketch
        /// that saw the whole stream unsaved -- in bytes, count and total.
        /// <para>
        /// <c>Add</c> compacts only when the buffer is full, and only a compaction
        /// trims, so a buffer with room past twice the retained size lets the sketch
        /// hold more than that before trimming, and it settles on a different theta
        /// and a different sample. The reader made room for at least sixteen keys,
        /// which is more than twice the retained size below k = 8. At k = 1 a trim
        /// keeps exactly one key whenever it fires, so there the extra room changed
        /// the storage and not the estimates; the storage is pinned in
        /// <see cref="TestNoPathGivesTheBufferMoreRoomThanTheSketchMayHold"/>.
        /// </para>
        /// </summary>
        [TestMethod]
        [DynamicData(nameof(RetainedSizesAroundTheStartingBuffer))]
        public void TestASketchReadBackGoesOnAsIfItHadNeverBeenSaved(uint k)
        {
            AssertCarriesOnAsTheUnsplitSketch(k, "read back from its own bytes",
                sketch => Persistence.FromByteArray<TupleSketch>(sketch.ToByteArray()));
        }

        /// <summary>
        /// A set operation's result is rebuilt from scratch, and was rebuilt with the
        /// same oversized buffer as a sketch read back. A union with an empty sketch
        /// has exactly the state of the sketch it was taken from, so it is held to that
        /// sketch's stream as it carries on. The intersection and difference tests
        /// beside it do the same for the other two operations, which share the
        /// rebuild but not the merge in front of it.
        /// </summary>
        [TestMethod]
        [DynamicData(nameof(RetainedSizesAroundTheStartingBuffer))]
        public void TestAUnionWithAnEmptySketchGoesOnAsTheSketchItEquals(uint k)
        {
            AssertCarriesOnAsTheUnsplitSketch(k, "unioned with an empty sketch",
                sketch => sketch.Union(new TupleSketch(k)));
        }

        /// <summary>
        /// See <see cref="TestAUnionWithAnEmptySketchGoesOnAsTheSketchItEquals"/>. Run
        /// under <see cref="SummaryPolicy.Max"/>, which folds a key's summary with
        /// itself into the same value, so the intersection's state is the sketch's own.
        /// </summary>
        [TestMethod]
        [DynamicData(nameof(RetainedSizesAroundTheStartingBuffer))]
        public void TestAnIntersectionWithItselfGoesOnAsTheSketchItEquals(uint k)
        {
            AssertCarriesOnAsTheUnsplitSketch(k, "intersected with itself",
                sketch => sketch.Intersect(sketch), SummaryPolicy.Max);
        }

        /// <summary>
        /// See <see cref="TestAUnionWithAnEmptySketchGoesOnAsTheSketchItEquals"/>.
        /// </summary>
        [TestMethod]
        [DynamicData(nameof(RetainedSizesAroundTheStartingBuffer))]
        public void TestADifferenceFromAnEmptySketchGoesOnAsTheSketchItEquals(uint k)
        {
            AssertCarriesOnAsTheUnsplitSketch(k, "less an empty sketch",
                sketch => sketch.Difference(new TupleSketch(k)));
        }

        /// <summary>
        /// The one set operation that trims as it rebuilds: a union of two exact
        /// sketches of k keys each holds twice the retained size, so it trims on the
        /// spot, and lands on exactly the state of one sketch fed both key sets. The
        /// two are then fed the same records and must stay identical.
        /// </summary>
        [TestMethod]
        [DynamicData(nameof(RetainedSizesAroundTheStartingBuffer))]
        public void TestAUnionThatTrimsGoesOnAsTheSketchOfBothStreams(uint k)
        {
            const int Streams = 100;
            var diverged = 0;
            string first = null;

            for (var t = 0; t < Streams; t++)
            {
                var left = new TupleSketch(k);
                var right = new TupleSketch(k);
                var both = new TupleSketch(k);
                for (var i = 0; i < k; i++)
                {
                    left.Add(Encoding.UTF8.GetBytes($"t{t}-left-{i}"), ValueOf(i));
                    both.Add(Encoding.UTF8.GetBytes($"t{t}-left-{i}"), ValueOf(i));
                }
                for (var i = 0; i < k; i++)
                {
                    right.Add(Encoding.UTF8.GetBytes($"t{t}-right-{i}"), ValueOf(i));
                    both.Add(Encoding.UTF8.GetBytes($"t{t}-right-{i}"), ValueOf(i));
                }

                var union = left.Union(right);
                Assert.AreNotEqual(ulong.MaxValue, union.ThetaValue,
                    $"stream {t}: the union must have trimmed while rebuilding, or the " +
                    "path this test exists for never ran.");
                CollectionAssert.AreEqual(both.ToByteArray(), union.ToByteArray(),
                    $"stream {t}: the union must start as the sketch of both key sets, " +
                    "or what follows compares two different states.");

                for (var i = 0; i < ContinuationLength(k); i++)
                {
                    both.Add(Record(k, t, i), ValueOf(i));
                    union.Add(Record(k, t, i), ValueOf(i));
                }

                if (!both.ToByteArray().AsSpan().SequenceEqual(union.ToByteArray())
                    || both.Count() != union.Count() || both.Total() != union.Total())
                {
                    diverged++;
                    first ??= $"stream {t}, estimating {union.Count()} keys totalling " +
                        $"{union.Total()} against {both.Count()} totalling {both.Total()}";
                }
            }

            Assert.AreEqual(0, diverged,
                $"k = {k}: {diverged} of {Streams} unions went on differently from the " +
                $"sketch they started equal to, first {first}. A rebuilt sketch's buffer " +
                "decides when it trims, so it must be sized as a new one's would be.");
        }

        /// <summary>
        /// The mechanism behind the continuation tests, stated directly: no path that
        /// makes a sketch gives it room for more than twice the retained size, because
        /// that is the most it may hold before it trims, and room past it delays the
        /// trim. Asserted through <see cref="TupleSketch.SizeInBytes"/>, which is the
        /// key and summary buffers and nothing else.
        /// <para>
        /// This is what sees the defect at k = 1, where the continuation tests cannot.
        /// A sketch fed past its trim threshold must have grown to exactly the full
        /// allowance, since only a full allowance trims; at k = 9 doubling the starting
        /// buffer passes twice the retained size, so growth that forgot its cap shows.
        /// The larger sizes are where growth has to keep going past its first step.
        /// </para>
        /// </summary>
        [TestMethod]
        [DynamicData(nameof(RetainedSizesAroundTheStartingBuffer))]
        public void TestNoPathGivesTheBufferMoreRoomThanTheSketchMayHold(uint k)
        {
            var allowance = 2ul * k * (sizeof(ulong) + sizeof(double));

            var fed = new TupleSketch(k);
            var other = new TupleSketch(k);
            for (var i = 0; i < ContinuationLength(k); i++)
            {
                fed.Add(Record(k, 0, i), ValueOf(i));
                other.Add(Record(k, 1, i), ValueOf(i));
            }
            Assert.AreNotEqual(ulong.MaxValue, fed.ThetaValue,
                "the fed sketch must have trimmed, or it never needed its allowance.");
            Assert.AreEqual(allowance, fed.SizeInBytes(),
                $"k = {k}: a sketch that has trimmed must have grown to its full " +
                "allowance and no further.");

            var left = new TupleSketch(k);
            var right = new TupleSketch(k);
            for (var i = 0; i < k; i++)
            {
                left.Add(Encoding.UTF8.GetBytes($"left-{i}"), ValueOf(i));
                right.Add(Encoding.UTF8.GetBytes($"right-{i}"), ValueOf(i));
            }

            var made = new (string What, TupleSketch Sketch)[]
            {
                ("a new sketch", new TupleSketch(k)),
                ("an empty sketch read back",
                    Persistence.FromByteArray<TupleSketch>(new TupleSketch(k).ToByteArray())),
                ("a trimmed sketch read back",
                    Persistence.FromByteArray<TupleSketch>(fed.ToByteArray())),
                ("a union", fed.Union(other)),
                ("an intersection", fed.Intersect(other)),
                ("a difference", fed.Difference(other)),
                ("a union that trims as it rebuilds", left.Union(right)),
            };

            var over = made
                .Where(m => m.Sketch.SizeInBytes() > allowance)
                .Select(m => $"{m.What} ({m.Sketch.SizeInBytes()} bytes)")
                .ToList();

            Assert.IsEmpty(over,
                $"k = {k}, allowance {allowance} bytes: {string.Join(", ", over)}. Room " +
                "past twice the retained size delays a sketch's trims, and every " +
                "estimate after them moves.");
        }

        /// <summary>
        /// The other half of the sizing rule: a sketch read back or rebuilt by a set
        /// operation is sized by what it holds, not by the allowance it may grow into.
        /// One holding no more than a new sketch has room for costs exactly what a new
        /// one does. Otherwise a payload that claims a large k and holds almost
        /// nothing would cost its whole allowance the moment it was read.
        /// </summary>
        [TestMethod]
        public void TestASmallSketchReadBackOrRebuiltCostsWhatANewOneDoes()
        {
            const uint K = 4096;
            var fresh = new TupleSketch(K).SizeInBytes();

            var left = new TupleSketch(K);
            var right = new TupleSketch(K);
            for (var i = 0; i < 5; i++)
            {
                left.Add(Key(i), i + 1);
                right.Add(Key(100 + i), i + 1);
            }

            var made = new (string What, TupleSketch Sketch)[]
            {
                ("an empty sketch read back",
                    Persistence.FromByteArray<TupleSketch>(new TupleSketch(K).ToByteArray())),
                ("a small sketch read back",
                    Persistence.FromByteArray<TupleSketch>(left.ToByteArray())),
                ("a union", left.Union(right)),
                ("an intersection", left.Intersect(right)),
                ("a difference", left.Difference(right)),
            };

            foreach (var (what, sketch) in made)
            {
                Assert.AreEqual(fresh, sketch.SizeInBytes(),
                    $"{what} must cost what a new sketch does, not the " +
                    $"{2ul * K * (sizeof(ulong) + sizeof(double))} bytes it may grow to.");
            }
        }

        /// <summary>
        /// Splits each continuation stream at every point, hands the sketch built so
        /// far to <paramref name="carryOn"/>, feeds the rest of the stream to what
        /// comes back, and holds the result to a sketch that saw the whole stream
        /// unsplit: identical bytes, count and total.
        /// </summary>
        private static void AssertCarriesOnAsTheUnsplitSketch(
            uint k, string how, Func<TupleSketch, TupleSketch> carryOn,
            SummaryPolicy policy = SummaryPolicy.Sum)
        {
            int splits = 0, crossed = 0, diverged = 0;
            string first = null;

            var length = ContinuationLength(k);

            for (var t = 0; t < ContinuationStreams; t++)
            {
                // Built once per stream: the larger sizes replay a long stream from
                // every split, and the keys are the same each time.
                var records = Enumerable.Range(0, length)
                    .Select(i => Record(k, t, i))
                    .ToArray();

                var unsplit = new TupleSketch(k, policy);
                for (var i = 0; i < length; i++)
                {
                    unsplit.Add(records[i], ValueOf(i));
                }
                var expected = unsplit.ToByteArray();

                var prefix = new TupleSketch(k, policy);
                for (var split = 0; split < length; split++)
                {
                    var before = prefix.ToByteArray();
                    var carried = carryOn(prefix);
                    CollectionAssert.AreEqual(before, carried.ToByteArray(),
                        $"k = {k}, stream {t}, split {split}: a sketch {how} must start as " +
                        "the sketch it came from, or what follows compares two states.");

                    var thetaAtSplit = carried.ThetaValue;
                    for (var i = split; i < length; i++)
                    {
                        carried.Add(records[i], ValueOf(i));
                    }

                    splits++;
                    if (thetaAtSplit != ulong.MaxValue && carried.ThetaValue < thetaAtSplit)
                    {
                        crossed++;
                    }

                    if (!carried.ToByteArray().AsSpan().SequenceEqual(expected)
                        || carried.Count() != unsplit.Count()
                        || carried.Total() != unsplit.Total())
                    {
                        diverged++;
                        first ??= $"stream {t} split at record {split}, estimating " +
                            $"{carried.Count()} keys totalling {carried.Total()} against " +
                            $"{unsplit.Count()} totalling {unsplit.Total()}";
                    }

                    prefix.Add(records[split], ValueOf(split));
                }
            }

            Console.WriteLine($"k = {k}, {how}: {crossed} of {splits} splits fell " +
                $"between two trims, {diverged} went on differently");

            Assert.AreEqual(0, diverged,
                $"k = {k}: {diverged} of {splits} splits went on differently once {how}, " +
                $"first {first}. The buffer is not in the payload, so a sketch rebuilt " +
                "without one must be sized by the rule a sketch growing its own follows.");

            // The comparison can only see the buffer if the split falls between trims:
            // after one, so the sketch handed on is already sampling, and before
            // another, so the buffer is asked to decide when that one fires. Late
            // splits have no trim left, so this asks for a share of them, not all.
            Assert.IsGreaterThan(splits / 3, crossed,
                $"k = {k}: only {crossed} of {splits} splits fell between two trims, so " +
                "too much of this test compared sketches with no trim left to disagree on.");
        }

        // ------------------------------------------------------------------------
        // Fractional values. The continuation tests above use whole numbers on
        // purpose; these say what happens when the values are not.
        // ------------------------------------------------------------------------

        /// <summary>
        /// The largest partition <c>Array.Sort</c> hands to insertion sort, which is
        /// stable, before it starts partitioning, which is not. It is the runtime's
        /// choice and not a documented one -- the documentation says only that the
        /// sort is unstable -- so tests that depend on it say so, and name what to
        /// look at when one fails.
        /// </summary>
        private const int InsertionSortSize = 16;

        /// <summary>
        /// The retained sizes whose buffer can never exceed the insertion sort's
        /// partition: twice the retained size is the most a sketch holds.
        /// </summary>
        public static IEnumerable<object[]> RetainedSizesWhoseBufferIsSortedStably =>
            Enumerable.Range(1, InsertionSortSize / 2).Select(k => new object[] { (uint)k });

        /// <summary>
        /// The retained sizes whose buffer can be larger: the first one that is, and two
        /// where it is well past it.
        /// </summary>
        public static IEnumerable<object[]> RetainedSizesWhoseBufferIsNotSortedStably =>
            new[] { InsertionSortSize / 2 + 1, 12, 33 }.Select(k => new object[] { (uint)k });

        private const int BurstLength = 4;

        /// <summary>
        /// Keys per fractional stream: enough that every size trims, and several
        /// passes of them.
        /// </summary>
        private static int FractionalKeys(uint k) => Math.Max(40, 6 * (int)k);

        /// <summary>
        /// Three passes over the keys, each arriving in a burst of four. A key's four
        /// arrivals sit together in the buffer, beside whatever its earlier arrivals
        /// folded to, so there are always three or more equal keys to put in some order
        /// -- with fewer the fold is a single commutative addition and cannot tell.
        /// </summary>
        private static int FractionalLength(uint k) => FractionalKeys(k) * BurstLength * 3;

        private static byte[] FractionalRecord(uint k, int stream, int i) =>
            Encoding.UTF8.GetBytes($"t{stream}-item-{(i / BurstLength) % FractionalKeys(k)}");

        /// <summary>
        /// Irrational, and so different in the last bits from one record to the next.
        /// Square root is correctly rounded on every platform, which the logarithm
        /// and the power function are not, so these are the same numbers everywhere.
        /// </summary>
        private static double FractionalValueOf(int i) => Math.Sqrt(i + 1);

        private static readonly double UnitRoundoff = Math.ScaleB(1.0, -53);

        /// <summary>
        /// The bound on how far a left-to-right sum of <paramref name="n"/> positive
        /// numbers can be from the true sum, as a fraction of it: n u / (1 - n u),
        /// Higham, <i>Accuracy and Stability of Numerical Algorithms</i>, section 4.2.
        /// It holds for any order of the terms, so two orders are within twice it of
        /// each other. One more term than there are is allowed for, so that a
        /// compensated reference that is itself a rounding off is inside it.
        /// </summary>
        private static double RoundingBound(int n)
        {
            var nu = (n + 1) * UnitRoundoff;
            return nu / (1 - nu);
        }

        /// <summary>
        /// The reference a sum is held to: compensated, so within a rounding or so of
        /// the true sum whatever the order.
        /// </summary>
        private static double CompensatedSum(IEnumerable<double> values)
        {
            double sum = 0, compensation = 0;
            foreach (var x in values)
            {
                var t = sum + x;
                compensation += Math.Abs(sum) >= Math.Abs(x) ? (sum - t) + x : (x - t) + sum;
                sum = t;
            }
            return sum + compensation;
        }

        /// <summary>
        /// Each retained key's summary against the true total of every value its key
        /// arrived with, within the rounding bound; returns how many were outside it.
        /// A retained key was below theta at every arrival, since theta only falls, so
        /// its summary covers its whole stream.
        /// </summary>
        private static int SummariesOutsideRounding(
            TupleSketch sketch, Dictionary<ulong, (int N, double Total)> truth, out string first)
        {
            var outside = 0;
            first = null;
            var keys = sketch.KeysHeld;
            var summaries = sketch.SummariesHeld;
            for (var j = 0; j < keys.Length; j++)
            {
                var (n, total) = truth[keys[j]];
                var error = Math.Abs(summaries[j] - total);
                if (error > RoundingBound(n) * total)
                {
                    outside++;
                    first ??= $"key {j} of {keys.Length} summed {n} values to {summaries[j]:R}, " +
                        $"{error:R} from {total:R}, past the {RoundingBound(n) * total:R} " +
                        "that rounding allows";
                }
            }
            return outside;
        }

        private static Dictionary<ulong, List<double>> ValuesByKey(
            TupleSketch like, byte[][] records, Func<int, double> value)
        {
            var values = new Dictionary<ulong, List<double>>();
            for (var i = 0; i < records.Length; i++)
            {
                var hash = like.Hash(records[i]);
                if (!values.TryGetValue(hash, out var list))
                {
                    values[hash] = list = new List<double>();
                }
                list.Add(value(i));
            }
            return values;
        }

        private static Dictionary<ulong, (int N, double Total)> TruthOf(
            TupleSketch like, byte[][] records, Func<int, double> value) =>
            ValuesByKey(like, records, value).ToDictionary(
                kv => kv.Key, kv => (kv.Value.Count, CompensatedSum(kv.Value)));

        /// <summary>
        /// Whether the values some key arrives with add up to different bits in
        /// different orders. Without one the fractional tests are whole-number tests
        /// in a costume: any order would be exact, and nothing here could tell.
        /// </summary>
        private static bool SomeKeyIsOrderSensitive(
            TupleSketch like, byte[][] records, Func<int, double> value) =>
            ValuesByKey(like, records, value).Values.Any(list =>
                list.Aggregate(0.0, (a, x) => a + x)
                    != Enumerable.Reverse(list).Aggregate(0.0, (a, x) => a + x));

        /// <summary>
        /// Splits fractional streams at every point, as the continuation tests do, and
        /// returns how many splits there were, how many went on to different bytes, how
        /// many summaries were outside rounding of the true total, and the first of
        /// those. Keys and counts are asserted equal to the unsplit sketch's on the
        /// way: they are exact whatever the values are.
        /// </summary>
        private static (int Splits, int Differ, int Outside, string First) CarryOnWithFractionalSums(
            uint k, int streams)
        {
            var length = FractionalLength(k);
            int splits = 0, differ = 0, outside = 0;
            string first = null;

            for (var t = 0; t < streams; t++)
            {
                var records = Enumerable.Range(0, length)
                    .Select(i => FractionalRecord(k, t, i))
                    .ToArray();

                var unsplit = new TupleSketch(k);
                for (var i = 0; i < length; i++)
                {
                    unsplit.Add(records[i], FractionalValueOf(i));
                }
                var expected = unsplit.ToByteArray();
                var expectedKeys = unsplit.KeysHeld;
                var truth = TruthOf(unsplit, records, FractionalValueOf);

                Assert.IsTrue(SomeKeyIsOrderSensitive(unsplit, records, FractionalValueOf),
                    $"k = {k}, stream {t}: no key's values add up differently in a " +
                    "different order, so this stream cannot tell an exact fold from a " +
                    "rounded one.");

                var prefix = new TupleSketch(k);
                for (var split = 0; split < length; split++)
                {
                    var carried = Persistence.FromByteArray<TupleSketch>(prefix.ToByteArray());
                    for (var i = split; i < length; i++)
                    {
                        carried.Add(records[i], FractionalValueOf(i));
                    }

                    splits++;
                    CollectionAssert.AreEqual(expectedKeys, carried.KeysHeld,
                        $"k = {k}, stream {t}, split {split}: the keys held are not " +
                        "values, so whatever the summaries do they must be the same.");
                    Assert.AreEqual(unsplit.Count(), carried.Count());

                    if (!carried.ToByteArray().AsSpan().SequenceEqual(expected))
                    {
                        differ++;
                    }

                    var bad = SummariesOutsideRounding(carried, truth, out var why);
                    if (bad > 0)
                    {
                        outside += bad;
                        first ??= $"stream {t} split at record {split}: {why}";
                    }

                    prefix.Add(records[split], FractionalValueOf(split));
                }
            }

            return (splits, differ, outside, first);
        }

        /// <summary>
        /// Saving a sketch and carrying on is bit-for-bit invisible with fractional
        /// summed values too, wherever the buffer is sorted stably.
        /// </summary>
        /// <remarks>
        /// The continuation tests use whole numbers because a sum of them is exact in
        /// any order, and the sort that gathers a key's values before folding them is
        /// documented unstable. This is the same comparison with values that are not
        /// exact. It holds while the buffer is small enough for the runtime's sort to
        /// treat as one partition (see <see cref="InsertionSortSize"/>): the sort is
        /// then an insertion sort, a key's values are folded in the order they
        /// arrived, the earlier arrivals' folded entry sitting ahead of the later
        /// ones, and that order does not depend on when compactions fire.
        /// <para>
        /// It is a fact about the runtime and not a guarantee of the sketch, and the
        /// sketch's own contract does not lean on it: see
        /// <see cref="TestFractionalSumsCarryOnToWithinRoundingBeyondTheStableSort"/>.
        /// If this fails and nothing here changed, look at <c>Array.Sort</c> first.
        /// </para>
        /// </remarks>
        [TestMethod]
        [DynamicData(nameof(RetainedSizesWhoseBufferIsSortedStably))]
        public void TestFractionalSumsCarryOnExactlyWhereTheBufferSortsStably(uint k)
        {
            var (splits, differ, outside, first) = CarryOnWithFractionalSums(k, streams: 6);

            Assert.AreEqual(0, outside,
                $"k = {k}: {outside} summaries were further from the true total than " +
                $"rounding allows, first {first}.");
            Assert.AreEqual(0, differ,
                $"k = {k}: {differ} of {splits} splits went on to different bytes with " +
                "fractional values. The buffer is small enough to be sorted by insertion, " +
                "which is stable, so a key's values should fold in arrival order whatever " +
                "the compactions do; if the runtime's sort has changed, this is where it " +
                "shows.");
        }

        /// <summary>
        /// KNOWN LIMITATION, pinned as it is. Past the sizes where the buffer is
        /// sorted stably, saving a sketch and carrying on can change the last bits of
        /// a fractional Sum summary. Keys and counts never change, and every summary
        /// stays within the rounding of its true total.
        /// </summary>
        /// <remarks>
        /// A buffer of more than <see cref="InsertionSortSize"/> entries is sorted by
        /// partitioning, which may swap equal keys. When a key has three or more
        /// entries in the buffer -- its earlier arrivals folded to one, and two or more
        /// since -- the swap changes the order they are added in, and floating-point
        /// addition is not associative. Saving writes a compaction into the sketch
        /// that would not otherwise have happened, which changes which entries share a
        /// buffer, and with it which keys are swapped: so the bytes of a sketch that
        /// was saved and continued differ from those of one that was not, in the last
        /// bits of some summaries and nowhere else.
        /// <para>
        /// Nothing is wrong with the totals. Each summary is within the bound for
        /// summing its values in any order, which is what this holds them to, against
        /// a compensated sum of the same values. What does not hold is the stronger
        /// statement the continuation tests make, that a save is invisible, and the
        /// README's that a key's own summary does not depend on arrival order. Both
        /// are exact for whole numbers and for Min and Max, and true to within
        /// rounding for fractional Sum.
        /// </para>
        /// <para>
        /// The guard on <c>differ</c> is the pin: it fails if the bytes stop
        /// differing, which would mean the fold had been made independent of the
        /// buffer (a stable sort, or a canonical order) and the sizes here should move
        /// to <see cref="TestFractionalSumsCarryOnExactlyWhereTheBufferSortsStably"/>.
        /// </para>
        /// </remarks>
        [TestMethod]
        [DynamicData(nameof(RetainedSizesWhoseBufferIsNotSortedStably))]
        public void TestFractionalSumsCarryOnToWithinRoundingBeyondTheStableSort(uint k)
        {
            var (splits, differ, outside, first) = CarryOnWithFractionalSums(k, streams: 4);

            Console.WriteLine($"k = {k}: {differ} of {splits} splits went on to different bytes");

            Assert.AreEqual(0, outside,
                $"k = {k}: {outside} summaries were further from the true total than " +
                $"rounding allows, first {first}. That is more than a different order " +
                "of addition can explain.");
            Assert.IsGreaterThan(0, differ,
                $"k = {k}: no split went on to different bytes. The limitation this " +
                "test pins has gone: the fold no longer depends on the buffer. Move " +
                "this size to the exact test, and the README's statement that a key's " +
                "own summary does not depend on arrival order can be stated without " +
                "the qualification about rounding.");
        }

        private const int OrderKeys = 100;
        private const int OrderRecords = 3_000;

        private static List<(int Key, double Value)> OrderRecordsWithFractionalValues()
        {
            var records = new List<(int, double)>();
            var random = new Random(4);
            for (var i = 0; i < OrderRecords; i++)
            {
                records.Add((random.Next(OrderKeys), FractionalValueOf(i)));
            }
            return records;
        }

        private static TupleSketch FoldedInOrder(
            IEnumerable<(int Key, double Value)> records, SummaryPolicy policy)
        {
            var sketch = new TupleSketch(4096, policy);
            foreach (var (key, value) in records)
            {
                sketch.Add(Key(key), value);
            }
            return sketch;
        }

        /// <summary>
        /// A key's summary does not depend on the order its values arrived in, to within
        /// the rounding of the addition: the same records forwards and backwards give
        /// the same keys, and every summary within the bound of the true total, but not
        /// always the same bits.
        /// </summary>
        /// <remarks>
        /// The whole-number version of this test is
        /// <see cref="TestAKeysSummaryDoesNotDependOnOrder"/>, and it is exact. This
        /// is the qualification the README's "a key's own summary does not" needs for
        /// Sum: floating-point addition is not associative, so summing the same
        /// fractional values in the opposite order is a different calculation, and no
        /// sort, stable or not, can make it the same one. The guard on the count of
        /// summaries that differ is the pin, as in
        /// <see cref="TestFractionalSumsCarryOnToWithinRoundingBeyondTheStableSort"/>.
        /// </remarks>
        [TestMethod]
        public void TestAKeysFractionalSumDependsOnArrivalOrderOnlyThroughRounding()
        {
            var records = OrderRecordsWithFractionalValues();
            var forwards = FoldedInOrder(records, SummaryPolicy.Sum);
            var backwards = FoldedInOrder(Enumerable.Reverse(records), SummaryPolicy.Sum);

            CollectionAssert.AreEqual(forwards.KeysHeld, backwards.KeysHeld);
            Assert.AreEqual(OrderKeys, forwards.KeysHeld.Length,
                "Premise: every key is held, so the sample does not depend on order and " +
                "the summaries are comparable one for one.");

            var recordsOf = records
                .Select(r => Key(r.Key))
                .ToArray();
            var truth = TruthOf(forwards, recordsOf, i => records[i].Value);

            Assert.AreEqual(0, SummariesOutsideRounding(forwards, truth, out var f), f);
            Assert.AreEqual(0, SummariesOutsideRounding(backwards, truth, out var b), b);

            var f1 = forwards.SummariesHeld;
            var b1 = backwards.SummariesHeld;
            var differing = Enumerable.Range(0, f1.Length).Count(j => f1[j] != b1[j]);
            Assert.IsGreaterThan(0, differing,
                "No key's summary differs between the two orders, so this stream cannot " +
                "tell an order-independent fold from a rounded one -- or the fold is " +
                "now exact in any order, and the README's claim holds without " +
                "qualification.");
        }

        /// <summary>
        /// Min and Max do not round, so with fractional values a key's summary is
        /// exactly the same in either order. This is the part of the README's claim
        /// that holds without qualification, along with whole-number Sum.
        /// </summary>
        [TestMethod]
        [DataRow(SummaryPolicy.Min)]
        [DataRow(SummaryPolicy.Max)]
        public void TestAKeysFractionalMinAndMaxDoNotDependOnOrderAtAll(SummaryPolicy policy)
        {
            var records = OrderRecordsWithFractionalValues();
            var forwards = FoldedInOrder(records, policy);
            var backwards = FoldedInOrder(Enumerable.Reverse(records), policy);

            CollectionAssert.AreEqual(forwards.ToByteArray(), backwards.ToByteArray(),
                $"{policy} of the same fractional values gave different bytes in the " +
                "opposite order, but taking a minimum or maximum does not round.");
        }
    }
}
