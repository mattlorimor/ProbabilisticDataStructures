using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Tests for DPSW-Sketch (Wang, Wang and Chen, KDD 2024).
    /// </summary>
    /// <remarks>
    /// The privacy guarantee is the authors' theorem and nothing here proves it. What
    /// these tests hold is the structural condition the theorem rests on -- that the
    /// budgets spent on any one item come to no more than the whole -- along with the
    /// window behaviour and the accuracy, which are ordinary claims measurable in the
    /// ordinary way. The noise itself is held to its distribution in
    /// <see cref="TestPrivateCountMinSketch"/>.
    /// </remarks>
    [TestClass]
    public class TestDpswSketch
    {
        private static byte[] Key(int i) => Encoding.UTF8.GetBytes("k" + i);

        /// <summary>
        /// No item is ever covered by sketches whose budgets add up to more than the
        /// budget for the whole structure.
        /// </summary>
        /// <remarks>
        /// This is the assertion that makes the privacy claim mean anything, and it is
        /// the one thing about the privacy that a test can check. Every sketch covering
        /// an item spends budget on that item, and zero-concentrated privacy composes
        /// by adding, so if the sketches covering some position summed past the budget,
        /// the structure would be less private than it says by exactly that factor --
        /// invisibly, since every estimate would look the same.
        /// <para>
        /// The split is a geometric series arranged to sum exactly: the whole-substream
        /// sketch takes 2a - a^2 and each checkpoint pair takes a^(j-2)(1-a)^3/2, and
        /// those come to one. Reading the exponent as (1-a)^(3/2) instead -- which the
        /// paper's typesetting invites -- sums to about twice the budget, which is half
        /// the noise the mechanism needs.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestNoItemIsChargedMoreThanTheWholeBudget()
        {
            foreach (var (window, alpha, beta) in new[]
            {
                (10_000L, 0.5, 0.5),
                (10_000L, 0.75, 0.5),
                (1_000L, 0.5, 0.6),
                (100_000L, 0.5, 0.7),
                (100_000L, 0.4, 0.6),
            })
            {
                const double Budget = 1.0;
                var sketch = new DpswSketch(window, Budget, alpha, beta, 64, 3, seed: 1);

                for (var position = 1; position <= sketch.SubstreamSize; position++)
                {
                    var spent = sketch.BudgetSpentAt(position);

                    Assert.IsTrue(spent <= Budget + 1e-9,
                        $"At window {window}, alpha {alpha} and beta {beta}, position " +
                        $"{position} of a substream is covered by sketches whose " +
                        $"budgets come to {spent:F6} against a budget of {Budget}. " +
                        "The structure is less private than it claims by that factor.");
                }

                // And the budget is actually being spent rather than hoarded: a split
                // that gave everything away to one sketch would also pass the bound.
                var best = 0.0;
                for (var position = 1; position <= sketch.SubstreamSize; position++)
                {
                    best = Math.Max(best, sketch.BudgetSpentAt(position));
                }
                Assert.IsTrue(best > Budget * 0.8,
                    $"The most any position is charged is {best:F6} of a budget of " +
                    $"{Budget}, so most of the budget is going unused and the sketches " +
                    "are noisier than they need to be.");
            }
        }

        /// <summary>
        /// With the noise turned down, the window itself is accurate.
        /// </summary>
        /// <remarks>
        /// This separates the two things that can make an estimate wrong. A sliding
        /// window built from fixed checkpoints can only approximate where the window
        /// starts, and a Count-Min Sketch collides; both are here whatever the privacy
        /// budget. Setting the budget absurdly high leaves those two alone and takes
        /// the noise away, and what is left is under one percent -- so every larger
        /// error seen at a real budget is the noise and not the window.
        /// </remarks>
        [TestMethod]
        public void TestWithoutNoiseTheWindowItselfIsAccurate()
        {
            const long Window = 5_000;

            foreach (var beta in new[] { 0.5, 0.7 })
            {
                var sketch = new DpswSketch(Window, 1e6, 0.5, beta, 512, 5, seed: 4);

                for (var i = 1; i <= 20_000; i++)
                {
                    sketch.Add(i % 4 == 0 ? Key(1) : Key(1_000 + (i % 400)));
                }

                var truth = Window / 4.0;
                var estimate = sketch.Count(Key(1));

                Assert.AreEqual(truth, estimate, truth * 0.05,
                    $"With the noise turned down, a window of {Window} estimated " +
                    $"{estimate:F1} against a truth of {truth}. That gap is the window " +
                    "approximation and the collisions, and it should be small.");
            }
        }

        /// <summary>
        /// Items that have left the window stop being counted.
        /// </summary>
        /// <remarks>
        /// The whole point of a sliding window. The estimate for an item seen only long
        /// ago should come back near nought -- and it may well come back below nought,
        /// since the answer is a sum of noisy sketches with nothing real in them.
        /// </remarks>
        [TestMethod]
        public void TestItemsThatLeftTheWindowAreForgotten()
        {
            const long Window = 5_000;
            var sketch = new DpswSketch(Window, 5.0, 0.5, 0.7, 512, 5, seed: 6);

            for (var i = 0; i < 3_000; i++)
            {
                sketch.Add(Key(42));
            }
            var whileInside = sketch.Count(Key(42));
            Assert.IsTrue(whileInside > 2_000,
                $"While inside the window the item read {whileInside:F1} of 3,000.");

            for (var i = 0; i < 20_000; i++)
            {
                sketch.Add(Key(9_000 + i));
            }

            var afterwards = sketch.Count(Key(42));
            Assert.IsTrue(Math.Abs(afterwards) < Window * 0.1,
                $"An item last seen 20,000 items ago still reads {afterwards:F1} in a " +
                $"window of {Window}.");
        }

        /// <summary>
        /// Items inside the window are counted.
        /// </summary>
        [TestMethod]
        public void TestItemsInsideTheWindowAreCounted()
        {
            const long Window = 5_000;
            var sketch = new DpswSketch(Window, 5.0, 0.5, 0.7, 512, 5, seed: 8);

            for (var i = 0; i < 20_000; i++)
            {
                sketch.Add(Key(9_000 + i));
            }
            for (var i = 0; i < 1_000; i++)
            {
                sketch.Add(Key(42));
            }

            var estimate = sketch.Count(Key(42));
            Assert.AreEqual(1_000.0, estimate, 400,
                $"A thousand recent additions read {estimate:F1}.");
        }

        /// <summary>
        /// Tightening the guarantee costs accuracy.
        /// </summary>
        /// <remarks>
        /// If it did not, the noise would not be reaching the answers.
        /// </remarks>
        [TestMethod]
        public void TestTighterPrivacyCostsAccuracy()
        {
            const long Window = 5_000;
            var errors = new List<double>();

            foreach (var rho in new[] { 100.0, 5.0, 0.5 })
            {
                var sketch = new DpswSketch(Window, rho, 0.5, 0.7, 512, 5, seed: 4);
                for (var i = 1; i <= 20_000; i++)
                {
                    sketch.Add(i % 4 == 0 ? Key(1) : Key(1_000 + (i % 400)));
                }

                errors.Add(Math.Abs(sketch.Count(Key(1)) - (Window / 4.0)));
            }

            Assert.IsTrue(errors[2] > errors[0],
                $"A budget of 0.5 was out by {errors[2]:F1} and one of 100 by " +
                $"{errors[0]:F1}. Privacy is meant to cost accuracy.");
        }

        /// <summary>
        /// Heavy hitters come back and light items do not.
        /// </summary>
        [TestMethod]
        public void TestHeavyHittersAreFound()
        {
            const long Window = 4_000;
            var sketch = new DpswSketch(Window, 20.0, 0.5, 0.7, 512, 5, seed: 12);

            // One item takes a third of the stream, another a fifth, the rest are rare.
            for (var i = 1; i <= 20_000; i++)
            {
                if (i % 3 == 0)
                {
                    sketch.Add(Key(1));
                }
                else if (i % 5 == 0)
                {
                    sketch.Add(Key(2));
                }
                else
                {
                    sketch.Add(Key(1_000 + (i % 500)));
                }
            }

            var candidates = new List<byte[]> { Key(1), Key(2) };
            for (var i = 0; i < 100; i++)
            {
                candidates.Add(Key(1_000 + i));
            }

            var heavy = sketch.HeavyHitters(candidates, 0.15);

            var wanted = Encoding.UTF8.GetString(Key(1));
            var found = new List<string>();
            foreach (var item in heavy)
            {
                found.Add(Encoding.UTF8.GetString(item));
            }

            CollectionAssert.Contains(found, wanted,
                "The item taking a third of the window was not found.");
            Assert.IsTrue(heavy.Count < 10,
                $"{heavy.Count} items were called heavy at a threshold of fifteen " +
                "percent, and at most a couple can be.");
        }

        /// <summary>
        /// The structure reports the memory it is using, and it is a lot.
        /// </summary>
        /// <remarks>
        /// Worth an assertion because the cost is easy to underestimate: a private
        /// sliding window keeps hundreds of sketches, since it cannot forget by
        /// overwriting a counter without leaking what that counter used to hold.
        /// </remarks>
        [TestMethod]
        public void TestTheMemoryIsReportedAndSubstantial()
        {
            var sketch = new DpswSketch(5_000, 5.0, 0.5, 0.7, 512, 5, seed: 4);
            for (var i = 0; i < 20_000; i++)
            {
                sketch.Add(Key(i % 400));
            }

            Assert.AreEqual(
                (long)sketch.SketchesHeld * 512 * 5 * sizeof(double),
                sketch.SizeInBytes);
            Assert.IsTrue(sketch.SketchesHeld > 100,
                $"Only {sketch.SketchesHeld} sketches are being kept, which is fewer " +
                "than a window of this size needs.");
        }

        /// <summary>
        /// A checkpoint factor that would leave a sketch drowning in its own noise is
        /// refused when the structure is built.
        /// </summary>
        /// <remarks>
        /// The budget for the j-th checkpoint falls as the factor to the power of j
        /// while the range it covers falls only as one minus the factor, so a small
        /// factor leaves the last checkpoints with almost nothing. At 0.25 on a window
        /// of four thousand the leanest sketch gets about a sixty-billionth of the
        /// budget, giving it a noise deviation of half a million against a window of
        /// four thousand; queries that land on it come back wrong by thousands, and
        /// only sometimes, which is harder to notice than being always wrong.
        /// </remarks>
        [TestMethod]
        public void TestACheckpointFactorThatDrownsInNoiseIsRefused()
        {
            foreach (var alpha in new[] { 0.1, 0.2, 0.25 })
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => new DpswSketch(4_000, 20.0, alpha, 0.7, 512, 5),
                    $"A checkpoint factor of {alpha} should be refused on this window.");
            }

            // And the ones that work are not refused.
            foreach (var alpha in new[] { 0.4, 0.5, 0.75 })
            {
                _ = new DpswSketch(4_000, 20.0, alpha, 0.7, 512, 5);
            }
        }

        /// <summary>
        /// Memory stops growing once the window is full.
        /// </summary>
        /// <remarks>
        /// A sliding window that never lets go is not a sliding window. The structure
        /// forgets by dropping whole substreams once every item in them has left the
        /// window, and nothing else reclaims anything -- a query that simply ignored
        /// expired substreams would give every right answer while growing without
        /// bound, which is the failure this catches.
        /// </remarks>
        [TestMethod]
        public void TestMemoryStopsGrowingOnceTheWindowIsFull()
        {
            var sketch = new DpswSketch(4_000, 20.0, 0.5, 0.7, 512, 5, seed: 1);

            var settled = 0;
            for (var i = 1; i <= 100_000; i++)
            {
                sketch.Add(Key(i % 500));

                if (i == 25_000)
                {
                    settled = sketch.SketchesHeld;
                }
                else if (i > 25_000 && i % 25_000 == 0)
                {
                    Assert.IsTrue(sketch.SketchesHeld <= settled * 1.2,
                        $"After {i} items the structure keeps {sketch.SketchesHeld} " +
                        $"sketches where it kept {settled} at 25,000. A window that " +
                        "keeps growing is not forgetting.");
                }
            }

            Assert.IsTrue(sketch.SizeInBytes < 16L * 1024 * 1024,
                $"A window of 4,000 settled at {sketch.SizeInBytes / 1024 / 1024} MB.");
        }

        /// <summary>
        /// The substream still being filled is read from its widest sketch, not its
        /// narrowest.
        /// </summary>
        /// <remarks>
        /// A range that has not finished holds exactly the items that have arrived, so
        /// it can be read now; the paper's query rule passes it over in favour of a
        /// short checkpoint sketch holding a small share of the budget. Reading the
        /// widest one instead is correct, costs no privacy, and is much quieter.
        /// Measured over 351 query points, the mean error is -30 rather than -51 and
        /// the fifth percentile -60 rather than -77. The bound below is inside what the
        /// narrower reading achieves, so it fails if that behaviour comes back.
        /// </remarks>
        [TestMethod]
        public void TestTheFillingSubstreamIsReadFromItsWidestSketch()
        {
            var errors = new List<double>();

            for (ulong seed = 1; seed <= 3; seed++)
            {
                var sketch = new DpswSketch(4_000, 20.0, 0.5, 0.7, 512, 5, seed);

                for (var i = 1; i <= 24_000; i++)
                {
                    sketch.Add(i % 3 == 0 ? Key(1) : Key(1_000 + (i % 500)));

                    if (i > 8_000 && i % 137 == 0)
                    {
                        errors.Add(sketch.Count(Key(1)) - (4_000 / 3.0));
                    }
                }
            }

            var mean = 0.0;
            foreach (var error in errors)
            {
                mean += error;
            }
            mean /= errors.Count;

            Assert.IsTrue(mean > -40,
                $"The mean error over {errors.Count} query points is {mean:F1}. " +
                "Reading the filling substream from a narrow checkpoint sketch instead " +
                "of its widest gives about -51.");
        }

        /// <summary>
        /// The parameters are checked.
        /// </summary>
        [TestMethod]
        public void TestBadParametersAreRefused()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new DpswSketch(0, 1.0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new DpswSketch(100, 0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new DpswSketch(100, double.NaN));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new DpswSketch(100, 1.0, 0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new DpswSketch(100, 1.0, 1.0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new DpswSketch(100, 1.0, 0.25, 0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new DpswSketch(100, 1.0, 0.25, 1.0));

            var sketch = new DpswSketch(100, 1.0);
            Assert.ThrowsExactly<ArgumentNullException>(() => sketch.Add((byte[])null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => sketch.Count((byte[])null!));
            Assert.ThrowsExactly<ArgumentNullException>(
                () => sketch.HeavyHitters(null!, 0.1));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => sketch.HeavyHitters(new List<byte[]>(), 0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => sketch.HeavyHitters(new List<byte[]>(), 1.5));
        }

        /// <summary>
        /// A window round-trips exactly as far as its state goes: same shape, same
        /// position, same sketches, and the same answer for every item.
        /// </summary>
        [TestMethod]
        public void TestRoundTripsThroughPersistenceExactly()
        {
            var original = new DpswSketch(
                window: 512, rho: 1.0, alpha: 0.5, width: 32, depth: 3, seed: 2024);
            for (var i = 0; i < 900; i++)
            {
                original.Add(Key(i % 60));
            }

            var restored = Persistence.FromByteArray<DpswSketch>(original.ToByteArray());

            Assert.AreEqual(original.Window, restored.Window);
            Assert.AreEqual(original.Rho, restored.Rho);
            Assert.AreEqual(original.Position, restored.Position);
            Assert.AreEqual(original.SubstreamSize, restored.SubstreamSize);
            Assert.AreEqual(original.SketchesHeld, restored.SketchesHeld);
            Assert.AreEqual(original.SizeInBytes, restored.SizeInBytes);
            CollectionAssert.AreEqual(original.Checkpointing, restored.Checkpointing);

            for (var i = 0; i < 60; i++)
            {
                Assert.AreEqual(original.Count(Key(i)), restored.Count(Key(i)), 1e-9,
                    $"item {i} must estimate identically after a round trip.");
            }
        }

        /// <summary>
        /// A restored window keeps counting, across the substream boundaries still
        /// ahead of it -- which is the point of re-seeding on read rather than handing
        /// back something query-only. The new substreams get their noise from a fresh
        /// generator, and that is sound because they cover items disjoint from
        /// everything already written: differential privacy composes in parallel over
        /// disjoint data, taking a maximum rather than a sum.
        /// </summary>
        [TestMethod]
        public void TestARestoredWindowKeepsCountingAcrossSubstreams()
        {
            var original = new DpswSketch(
                window: 512, rho: 1.0, alpha: 0.5, width: 32, depth: 3, seed: 77);
            for (var i = 0; i < 100; i++)
            {
                original.Add(Key(i % 20));
            }

            var restored = Persistence.FromByteArray<DpswSketch>(original.ToByteArray());
            var substreamsBefore = restored.SketchesHeld;

            for (var i = 0; i < 400; i++)
            {
                restored.Add(Key(7));
            }

            Assert.AreEqual(500, restored.Position,
                "a restored window must go on counting from where it left off.");
            Assert.IsGreaterThan(substreamsBefore, restored.SketchesHeld,
                "400 further items must have opened at least one new substream, or " +
                "the fresh generator was never exercised and this proves nothing.");

            // 400 of the 500 items in the window are item 7, plus whatever the first
            // hundred contributed. The estimate is noisy and unclamped, so the
            // assertion is that it is in the right country, not on the nose.
            var estimate = restored.Count(Key(7));
            Assert.IsGreaterThan(300.0, estimate,
                $"item 7 was added 400 times after the read and estimates " +
                $"{estimate:F1}; the sketches built after a round trip are not " +
                "counting into the window.");
        }

        /// <summary>
        /// The cost of not writing the generator down, stated as a test so that nobody
        /// later mistakes it for a bug. Two windows built from the same seed agree
        /// exactly; write one, read it back, and the pair diverge as soon as a new
        /// substream is opened, because the restored one draws its noise from a fresh
        /// unpredictable generator. Reproducibility across a round trip is precisely
        /// what persisting the generator would buy, and precisely what must not be
        /// bought at that price.
        /// </summary>
        [TestMethod]
        public void TestReproducibilityDoesNotSurviveARoundTrip()
        {
            static DpswSketch Build() => new DpswSketch(
                window: 512, rho: 1.0, alpha: 0.5, width: 32, depth: 3, seed: 31337);

            var twin = Build();
            var original = Build();
            for (var i = 0; i < 100; i++)
            {
                twin.Add(Key(i % 20));
                original.Add(Key(i % 20));
            }

            Assert.AreEqual(twin.Count(Key(3)), original.Count(Key(3)), 1e-12,
                "two windows from the same seed must agree before any round trip, or " +
                "the divergence below is not the round trip's doing.");

            var restored = Persistence.FromByteArray<DpswSketch>(original.ToByteArray());
            for (var i = 0; i < 400; i++)
            {
                twin.Add(Key(11));
                restored.Add(Key(11));
            }

            Assert.AreNotEqual(twin.Count(Key(11)), restored.Count(Key(11)),
                "the restored window drew the same noise as the seeded twin, which " +
                "means the generator state crossed the payload -- the one value that " +
                "must never be written down.");
        }
    }
}
