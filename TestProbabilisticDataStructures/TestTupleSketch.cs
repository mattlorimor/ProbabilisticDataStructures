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
        /// </summary>
        public static IEnumerable<object[]> RetainedSizesAroundTheStartingBuffer =>
            Enumerable.Range(1, 9).Select(k => new object[] { (uint)k });

        private const int ContinuationStreams = 20;
        private const int ContinuationLength = 200;
        private const int ContinuationKeys = 150;

        /// <summary>
        /// The streams the continuation tests split. Keys repeat, so the part of a
        /// stream after a split folds new values into summaries the sketch was handed
        /// as well as into ones it built itself.
        /// </summary>
        private static byte[] Record(int stream, int i) =>
            Encoding.UTF8.GetBytes($"t{stream}-item-{i % ContinuationKeys}");

        /// <summary>
        /// Whole numbers, so that every fold is exact in any order. The sort that
        /// gathers a key's values before folding them is not stable, the order it
        /// leaves them in depends on what else is in the buffer, and floating-point
        /// addition is not associative -- so with fractional values the bytes could
        /// depend on when compactions fired as well as on which values were folded.
        /// With whole numbers they pin only the second.
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

                for (var i = 0; i < ContinuationLength; i++)
                {
                    both.Add(Record(t, i), ValueOf(i));
                    union.Add(Record(t, i), ValueOf(i));
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
        /// </para>
        /// </summary>
        [TestMethod]
        [DynamicData(nameof(RetainedSizesAroundTheStartingBuffer))]
        public void TestNoPathGivesTheBufferMoreRoomThanTheSketchMayHold(uint k)
        {
            var allowance = 2ul * k * (sizeof(ulong) + sizeof(double));

            var fed = new TupleSketch(k);
            var other = new TupleSketch(k);
            for (var i = 0; i < ContinuationLength; i++)
            {
                fed.Add(Record(0, i), ValueOf(i));
                other.Add(Record(1, i), ValueOf(i));
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

            for (var t = 0; t < ContinuationStreams; t++)
            {
                var unsplit = new TupleSketch(k, policy);
                for (var i = 0; i < ContinuationLength; i++)
                {
                    unsplit.Add(Record(t, i), ValueOf(i));
                }
                var expected = unsplit.ToByteArray();

                var prefix = new TupleSketch(k, policy);
                for (var split = 0; split < ContinuationLength; split++)
                {
                    var before = prefix.ToByteArray();
                    var carried = carryOn(prefix);
                    CollectionAssert.AreEqual(before, carried.ToByteArray(),
                        $"k = {k}, stream {t}, split {split}: a sketch {how} must start as " +
                        "the sketch it came from, or what follows compares two states.");

                    var thetaAtSplit = carried.ThetaValue;
                    for (var i = split; i < ContinuationLength; i++)
                    {
                        carried.Add(Record(t, i), ValueOf(i));
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

                    prefix.Add(Record(t, split), ValueOf(split));
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
    }
}
