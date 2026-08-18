using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Tests for the Count-Min Sketch of Sublime (Eslami, Bercea, Pagh and Dayan,
    /// SIGMOD 2026).
    /// </summary>
    [TestClass]
    public class TestSublimeCountMinSketch
    {
        private static byte[] Key(int i) => Encoding.UTF8.GetBytes("k" + i);

        /// <summary>
        /// A skewed stream: a few keys take most of the draws.
        /// </summary>
        private static IEnumerable<int> Skewed(int events, int keys, int seed)
        {
            var random = new Random(seed);
            for (var i = 0; i < events; i++)
            {
                var key = (int)(keys * Math.Pow(random.NextDouble(), 6));
                yield return key >= keys ? keys - 1 : key;
            }
        }

        /// <summary>
        /// The sketch never reports a count lower than the true one.
        /// </summary>
        /// <remarks>
        /// This is the Count-Min guarantee, and it is the only thing about a sketch's
        /// answers that is guaranteed rather than probable. Nothing in Sublime is
        /// allowed to break it: not the encoding, which must give back what it was
        /// given, and not expansion, which must carry counts across.
        /// </remarks>
        [TestMethod]
        public void TestCountsAreNeverTooLow()
        {
            var sketch = new SublimeCountMinSketch(0.01);
            var truth = new Dictionary<int, ulong>();

            foreach (var key in Skewed(200_000, 20_000, 3))
            {
                sketch.Add(Key(key));
                truth.TryGetValue(key, out var had);
                truth[key] = had + 1;
            }

            foreach (var (key, want) in truth)
            {
                Assert.IsTrue(sketch.Count(Key(key)) >= want,
                    $"Key {key} was added {want} times but the sketch reports " +
                    $"{sketch.Count(Key(key))}, which a Count-Min sketch may never do.");
            }
        }

        /// <summary>
        /// The arrays double when the stream has grown enough to warrant it, and a
        /// key's count is exact across every doubling.
        /// </summary>
        /// <remarks>
        /// The thresholds are the size function's, not a measurement's: the default
        /// keeps the width at about the square root of the stream's length, so a width
        /// of w is due to change once the stream reaches w squared. Starting at 64,
        /// that is 4,096, then 16,384, then 65,536.
        /// <para>
        /// Only one key is added, so its count is the whole stream and any loss across
        /// an expansion shows up immediately and exactly.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestTheArraysDoubleAndCarryTheirCountsAcross()
        {
            var sketch = new SublimeCountMinSketch(0.01);
            var key = Key(1);

            Assert.AreEqual(64, sketch.Width, "Each array starts at one cache line.");

            var widths = new List<int> { sketch.Width };
            for (var added = 1UL; added <= 100_000; added++)
            {
                sketch.Add(key);

                Assert.AreEqual(added, sketch.Count(key),
                    $"After {added} additions the count should be exact; only one key " +
                    "has ever been added, so nothing can be colliding with it.");

                if (sketch.Width != widths[^1])
                {
                    widths.Add(sketch.Width);
                    Assert.AreEqual((ulong)(widths[^2] * widths[^2]), added,
                        $"A width of {widths[^2]} should last until the stream " +
                        $"reaches {widths[^2] * widths[^2]} keys.");
                }
            }

            CollectionAssert.AreEqual(new List<int> { 64, 128, 256, 512 }, widths,
                "The arrays should have doubled at 4,096, 16,384 and 65,536, and a " +
                "width of 512 should still be holding at 100,000 -- it lasts until " +
                "262,144.");
        }

        /// <summary>
        /// The counters stay narrow as the sketch grows, which is what the variable
        /// length encoding is for.
        /// </summary>
        /// <remarks>
        /// Measured. A sketch that expands is a sketch whose counters are mostly small,
        /// and this library's own Count-Min sketch would spend sixty-four bits on each
        /// of them. The bound is loose on purpose: what matters is that the figure
        /// stays near a dozen as the sketch grows by a factor of thirty-two, not what
        /// it is exactly.
        /// </remarks>
        [TestMethod]
        public void TestCountersStayNarrowAsTheSketchGrows()
        {
            var sketch = new SublimeCountMinSketch(0.01);

            var seen = 0;
            var widthAtLastCheck = 0;
            foreach (var key in Skewed(300_000, 100_000, 5))
            {
                sketch.Add(Key(key));

                if (sketch.Width == widthAtLastCheck)
                {
                    continue;
                }
                widthAtLastCheck = sketch.Width;
                seen++;

                var bits = (double)sketch.SizeInBytes * 8 / (sketch.Width * sketch.Depth);
                Assert.IsTrue(bits < 20,
                    $"At a width of {sketch.Width} the counters take {bits:F1} bits " +
                    "each, where the encoding should be keeping them near twelve.");
            }

            Assert.IsTrue(seen >= 5,
                $"The sketch only reached {seen} widths, so this checked almost nothing.");
        }

        /// <summary>
        /// The sketch estimates better than a fixed-size sketch given the same memory.
        /// </summary>
        /// <remarks>
        /// This is the paper's claim, and the reason to prefer Sublime to a Count-Min
        /// sketch sized in advance. Both hold the same stream in the same number of
        /// bytes and the same number of rows; the fixed sketch spends them on
        /// sixty-four-bit counters, and Sublime spends them on many more counters that
        /// are only as wide as they need to be.
        /// <para>
        /// Measured at a mean absolute error of 263 against the fixed sketch's 684.
        /// The bound below is deliberately slack -- half, not a quarter -- because what
        /// is being pinned is the direction of the result, and the margin has been
        /// growing with the stream rather than shrinking.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestEstimatesBeatAFixedSketchOfTheSameSize()
        {
            var sketch = new SublimeCountMinSketch(0.01);
            var truth = new Dictionary<int, ulong>();

            foreach (var key in Skewed(100_000, 100_000, 3))
            {
                sketch.Add(Key(key));
                truth.TryGetValue(key, out var had);
                truth[key] = had + 1;
            }

            // A plain sketch of the same depth, given the same bytes to spend on
            // sixty-four-bit counters.
            var columns = (int)(sketch.SizeInBytes / sizeof(ulong) / sketch.Depth);
            var fixedWidth = new ulong[sketch.Depth][];
            for (var i = 0; i < sketch.Depth; i++)
            {
                fixedWidth[i] = new ulong[columns];
            }

            var hash = Defaults.GetDefaultHashFunction();
            foreach (var key in Skewed(100_000, 100_000, 3))
            {
                var kernel = Utils.HashKernel(Key(key), hash);
                for (uint i = 0; i < sketch.Depth; i++)
                {
                    fixedWidth[i][(kernel.LowerBaseHash + kernel.UpperBaseHash * i)
                        % (uint)columns]++;
                }
            }

            var sublimeError = 0.0;
            var fixedError = 0.0;
            foreach (var (key, want) in truth)
            {
                sublimeError += sketch.Count(Key(key)) - want;

                var kernel = Utils.HashKernel(Key(key), hash);
                var best = ulong.MaxValue;
                for (uint i = 0; i < sketch.Depth; i++)
                {
                    best = Math.Min(best, fixedWidth[i]
                        [(kernel.LowerBaseHash + kernel.UpperBaseHash * i) % (uint)columns]);
                }
                fixedError += best - want;
            }

            Assert.IsTrue(sublimeError < fixedError * 0.5,
                $"Sublime was out by {sublimeError / truth.Count:F1} on average and " +
                $"the fixed sketch by {fixedError / truth.Count:F1}, in the same " +
                $"{sketch.SizeInBytes} bytes.");
        }

        /// <summary>
        /// A sketch whose counters crowd their chunks lays them out again, rather than
        /// letting the chunks fall back to tails arrays.
        /// </summary>
        /// <remarks>
        /// Expansion alone does not cover this. A stream of a few keys repeated many
        /// times grows the counts without growing the number of keys enough to expand,
        /// so the chunks fill while the width stays put. That is the case the tails
        /// fallback exists for and the case retuning exists to avoid.
        /// <para>
        /// Measured both ways: with retuning the counters take 640 bytes, and with it
        /// disabled the chunks fall back and take 3,040. The width is unchanged either
        /// way, so a bound on the bytes at a fixed width is what tells them apart.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestCrowdedChunksAreLaidOutAgainRatherThanFallingBack()
        {
            var sketch = new SublimeCountMinSketch(0.01);

            const int Distinct = 40;
            for (var step = 0; step < 4000; step++)
            {
                sketch.Add(Key(step % Distinct));
            }

            Assert.AreEqual(64, sketch.Width,
                "Four thousand keys is not enough to expand, which is the point: the " +
                "counters grew and the width did not.");

            Assert.IsTrue(sketch.SizeInBytes <= 1024,
                $"The counters took {sketch.SizeInBytes} bytes. Chunks that crowd and " +
                "are not laid out again fall back to tails arrays, which measured at " +
                "3,040 bytes for this stream.");

            // Forty keys over sixty-four counters collide, so the counts are not all
            // exact. They may not be low, though, and laying the counters out again
            // must not lose any of them.
            for (var key = 0; key < Distinct; key++)
            {
                Assert.IsTrue(sketch.Count(Key(key)) >= 100,
                    $"Key {key} was added a hundred times but reports " +
                    $"{sketch.Count(Key(key))}.");
            }
        }

        /// <summary>
        /// Growing faster gives more counters and closer estimates.
        /// </summary>
        [TestMethod]
        public void TestAFasterGrowthGivesMoreCounters()
        {
            var slow = new SublimeCountMinSketch(0.01, 0.4, 1.0);
            var fast = new SublimeCountMinSketch(0.01, 0.7, 1.0);

            foreach (var key in Skewed(100_000, 50_000, 8))
            {
                slow.Add(Key(key));
                fast.Add(Key(key));
            }

            Assert.IsTrue(fast.Width > slow.Width,
                $"A growth exponent of 0.7 reached a width of {fast.Width} where 0.4 " +
                $"reached {slow.Width}; the faster one should have expanded further.");
            Assert.IsTrue(fast.SizeInBytes > slow.SizeInBytes);
        }

        /// <summary>
        /// The number of rows follows from delta, and does not change as the sketch
        /// grows.
        /// </summary>
        /// <remarks>
        /// Depth is the one dimension Sublime leaves alone. Width tracks the stream, so
        /// the error falls as the stream grows; the confidence that an estimate is
        /// within that error comes from the rows, and the paper keeps it steady on
        /// purpose so that a query means the same thing early and late.
        /// </remarks>
        [TestMethod]
        public void TestDepthFollowsFromDelta()
        {
            Assert.AreEqual(3, new SublimeCountMinSketch(0.1).Depth);
            Assert.AreEqual(5, new SublimeCountMinSketch(0.01).Depth);
            Assert.AreEqual(7, new SublimeCountMinSketch(0.001).Depth);

            var sketch = new SublimeCountMinSketch(0.01);
            foreach (var key in Skewed(20_000, 5_000, 6))
            {
                sketch.Add(Key(key));
            }
            Assert.IsTrue(sketch.Width > 64, "The sketch should have expanded.");
            Assert.AreEqual(5, sketch.Depth, "Expanding must not change the depth.");
        }

        /// <summary>
        /// The tuning keeps a chunk's extension pool between the bounds the paper sets
        /// for it, whatever the stream does.
        /// </summary>
        /// <remarks>
        /// A chunk is a cache line whether its bits go to stubs or to a pool, so these
        /// bounds cost nothing in space and are not about space. Below the floor a
        /// chunk has too little room to hold the extensions it will be given and falls
        /// back to tails; above the ceiling it is holding counts in extensions that
        /// stubs would have held in half the bits, which is slower on every insertion
        /// that carries.
        /// </remarks>
        [TestMethod]
        public void TestTheTuningKeepsThePoolWithinItsBounds()
        {
            var sketch = new SublimeCountMinSketch(0.01);

            var checks = 0;
            var width = 0;
            var tuning = (0, 0);
            foreach (var key in Skewed(300_000, 100_000, 9))
            {
                sketch.Add(Key(key));

                if (sketch.Width == width
                    && tuning == (sketch.CountersPerChunk, sketch.StubBits))
                {
                    continue;
                }
                width = sketch.Width;
                tuning = (sketch.CountersPerChunk, sketch.StubBits);
                checks++;

                var pool = ValeCounterArray.ChunkBits
                    - sketch.CountersPerChunk * (sketch.StubBits + 1) - 1;

                Assert.IsTrue(pool >= 2 * ValeCounterArray.MinPoolFragments,
                    $"{sketch.CountersPerChunk} counters with {sketch.StubBits}-bit " +
                    $"stubs leave a pool of {pool} bits, below the floor.");
                Assert.IsTrue(pool <= 128,
                    $"{sketch.CountersPerChunk} counters with {sketch.StubBits}-bit " +
                    $"stubs leave a pool of {pool} bits, above the 128 worth keeping.");
            }

            Assert.IsTrue(checks >= 5,
                $"Only {checks} tunings were seen, so this checked almost nothing.");
        }

        /// <summary>
        /// A smaller size factor starts the sketch larger.
        /// </summary>
        [TestMethod]
        public void TestTheSizeFactorSetsTheStartingWidth()
        {
            Assert.AreEqual(64, new SublimeCountMinSketch(0.01, 0.5, 1.0).Width);
            Assert.AreEqual(128, new SublimeCountMinSketch(0.01, 0.5, 0.5).Width);
            Assert.AreEqual(256, new SublimeCountMinSketch(0.01, 0.5, 0.25).Width);
        }

        /// <summary>
        /// Removing takes counts back out.
        /// </summary>
        [TestMethod]
        public void TestRemovingTakesCountsBackOut()
        {
            var sketch = new SublimeCountMinSketch(0.01);
            var key = Key(42);

            for (var i = 0; i < 5000; i++)
            {
                sketch.Add(key);
            }
            Assert.AreEqual(5000UL, sketch.Count(key));

            for (var i = 0; i < 3000; i++)
            {
                sketch.Remove(key);
            }
            Assert.AreEqual(2000UL, sketch.Count(key));
            Assert.AreEqual(2000UL, sketch.TotalCount());
        }

        /// <summary>
        /// Resetting empties the sketch and returns it to its starting size.
        /// </summary>
        [TestMethod]
        public void TestResettingEmptiesTheSketch()
        {
            var sketch = new SublimeCountMinSketch(0.01);
            foreach (var key in Skewed(20_000, 5_000, 2))
            {
                sketch.Add(Key(key));
            }
            Assert.IsTrue(sketch.Width > 64, "The sketch should have expanded by now.");

            sketch.Reset();

            Assert.AreEqual(64, sketch.Width);
            Assert.AreEqual(0UL, sketch.TotalCount());
            Assert.AreEqual(0UL, sketch.Count(Key(1)));
        }

        /// <summary>
        /// The parameters are checked when the sketch is built.
        /// </summary>
        [TestMethod]
        public void TestBadParametersAreRefused()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new SublimeCountMinSketch(0.01, 0, 1.0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new SublimeCountMinSketch(0.01, 1.0, 1.0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new SublimeCountMinSketch(0.01, 0.5, 0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new SublimeCountMinSketch(0.01, 0.5, double.NaN));

            var sketch = new SublimeCountMinSketch(0.01);
            Assert.ThrowsExactly<ArgumentNullException>(() => sketch.Add((byte[])null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => sketch.Count((byte[])null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => sketch.Remove((byte[])null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => sketch.SetHash(null!));
        }

        /// <summary>
        /// The hash cannot be changed once the sketch holds something.
        /// </summary>
        [TestMethod]
        public void TestTheHashCannotBeReplacedAfterAdding()
        {
            var sketch = new SublimeCountMinSketch(0.01);
            sketch.SetHash(Defaults.GetDefaultHashFunction());

            sketch.Add(Key(1));

            Assert.ThrowsExactly<InvalidOperationException>(
                () => sketch.SetHash(Defaults.GetDefaultHashFunction()));
        }
    }
}
