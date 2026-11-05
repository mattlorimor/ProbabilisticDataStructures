using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
        /// States whose payload <see cref="TestPayloadCount"/> holds the count to: the rows
        /// are the depth and width, and each record of an earlier state is written at its
        /// own width, so a fresh sketch, a deeper one, and one that has expanded and holds
        /// records. The writer has one version, which stores each state's tuning (#280).
        /// </summary>
        internal static IEnumerable<PayloadState> PayloadStates()
        {
            var version = PersistenceFormat.SublimeTuningVersion;
            yield return PayloadState.Of("fresh, one row", new SublimeCountMinSketch(0.5, 0.5, 1.0), version);

            var deeper = new SublimeCountMinSketch(0.01);
            for (var i = 0; i < 40; i++) deeper.Add(Key(i));
            yield return PayloadState.Of("five rows, a few keys", deeper, version);

            var expanded = new SublimeCountMinSketch(0.1);
            for (var i = 0; expanded.Width <= 128; i++) expanded.Add(Key(i));
            yield return PayloadState.Of($"expanded to {expanded.Width}, with records", expanded, version);
        }

        /// <summary>
        /// A Sublime sketch's payload is its parameters, dimensions, count and limits and the
        /// number of its records, sixty bytes, then each record and the rows as two bytes of
        /// tuning and counts of eight bytes, with no run around them (#216, #280). The sketch
        /// packs its counters, so what it holds can be far less than its payload; the writer
        /// wrote the counts one at a time and only the buffer's growth checked a size. One row
        /// of 2^28 is the first power of two past the ceiling, and five rows of the widest a
        /// sketch grows to are past it many times over, and past 32 bits.
        /// </summary>
        [TestMethod]
        [DataRow(0.5, 1, 1 << 28, 2_147_483_710L)]
        [DataRow(0.01, 5, SublimeCountMinSketch.MaxWidth, 42_949_673_022L)]
        public void TestASketchTooLargeToWriteIsRefusedNamingItsWholeSize(
            double delta, int depth, int width, long payload)
        {
            Assert.AreEqual(60 + 2 + 8L * depth * width, payload, "the row's payload is miscomputed");

            var sketch = new SublimeCountMinSketch(delta, 0.5, 1.0);
            Assert.AreEqual(depth, sketch.Depth, "the row's depth is not the sketch's");
            for (var i = 0; i < 40; i++) sketch.Add(Key(i));

            TestPayloadCount.AssertRefusedWhileClaiming(sketch, payload, (sketch, "width", width));
        }

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

        private static ValeCounterArray[] RowsOf(SublimeCountMinSketch sketch)
        {
            var field = typeof(SublimeCountMinSketch).GetField("rows",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "the sketch has no field rows");
            return (ValeCounterArray[])field.GetValue(sketch)!;
        }

        /// <summary>
        /// The counter a key raises in each row, found by adding it to an empty twin.
        /// </summary>
        private static int[] ColumnsOf(byte[] key, Func<SublimeCountMinSketch> twin)
        {
            var sketch = twin();
            sketch.Add(key);
            var rows = RowsOf(sketch);
            var columns = new int[rows.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                columns[i] = -1;
                for (var j = 0; j < rows[i].Count; j++)
                {
                    if (rows[i].Get(j) != 0)
                    {
                        columns[i] = j;
                    }
                }
                Assert.AreNotEqual(-1, columns[i], $"the key raised nothing in row {i}");
            }
            return columns;
        }

        /// <summary>
        /// Fills the pool of the chunk holding <paramref name="target"/> with as many of the
        /// shortest extensions as it holds, and leaves the target one count short of needing
        /// one more.
        /// </summary>
        private static void OneCountFromSpilling(ValeCounterArray row, int target)
        {
            var first = target / row.CountersPerChunk * row.CountersPerChunk;
            var shortest = 1UL << row.StubBits;
            var fit = row.PoolFragments / ValeCounter.ExtensionLength(shortest >> row.StubBits);
            var filled = 0;
            for (var j = first; filled < fit; j++)
            {
                Assert.IsTrue(j < first + row.CountersPerChunk, "the chunk is too short to fill");
                if (j != target)
                {
                    row.Set(j, shortest);
                    filled++;
                }
            }
            row.Set(target, shortest - 1);
        }

        /// <summary>
        /// A sketch is laid out again at the add that puts more than three in a hundred of its
        /// chunks on tails, counted over every row, and not at an add that leaves it at or
        /// under that.
        /// </summary>
        /// <remarks>
        /// The test above shows that crowding is answered; this pins when. Three rows of 4,096
        /// counters at the starting tuning are 183 chunks, so five on tails are within the
        /// fraction and six are past it. Four chunks are put on tails by hand, all in the
        /// first two rows, and two more are packed one count short of spilling, each where a
        /// different key lands: the first key's add takes the sketch to five and must leave
        /// the rows as they are, and the second's takes it to six and must lay them out.
        /// <para>
        /// Each step straddles a way of getting the rule wrong that every other test passed:
        /// laying out at any add that puts a chunk on tails, whatever the fraction; taking the
        /// fraction of one row's chunks rather than all of them, which five already passes;
        /// and counting only the last row's tails, which here never changes. The keys are
        /// placed by watching which counters they raise, so the test does not restate how a
        /// hash picks a column.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestASketchIsLaidOutAgainAtTheAddThatTakesItPastTheFraction()
        {
            Func<SublimeCountMinSketch> make = () =>
                new SublimeCountMinSketch(0.05, 0.5, ValeCounterArray.DefaultCountersPerChunk / 6144.0);
            var sketch = make();
            var rows = RowsOf(sketch);
            Assert.AreEqual(4096, sketch.Width);
            Assert.AreEqual(3, rows.Length);
            var perChunk = rows[0].CountersPerChunk;
            var chunks = rows.Sum(r => r.ChunkCount);
            Assert.AreEqual(183, chunks, "three rows of 4,096 counters, 68 to a chunk");
            Assert.IsTrue(5 <= chunks * 0.03 && 6 > chunks * 0.03,
                "five chunks on tails should be within three in a hundred and six past it");

            // A key whose first-row chunk can be packed, and a second that shares no chunk
            // with it in any row and whose second-row chunk can be packed.
            int ChunkOf(int column) => column / perChunk;
            bool Packable(int column) => ChunkOf(column) < rows[0].ChunkCount - 1;
            var first = Key(0);
            var firstColumns = ColumnsOf(first, make);
            for (var k = 1; !Packable(firstColumns[0]); k++)
            {
                first = Key(k);
                firstColumns = ColumnsOf(first, make);
            }
            byte[] second = null;
            int[] secondColumns = null;
            for (var k = 1000; second == null; k++)
            {
                var columns = ColumnsOf(Key(k), make);
                if (Packable(columns[1])
                    && Enumerable.Range(0, 3).All(i => ChunkOf(columns[i]) != ChunkOf(firstColumns[i])))
                {
                    second = Key(k);
                    secondColumns = columns;
                }
            }

            // Two chunks on tails in each of the first two rows, away from both keys.
            for (var i = 0; i < 2; i++)
            {
                var spilled = 0;
                for (var c = 0; spilled < 2; c++)
                {
                    if (c == ChunkOf(firstColumns[i]) || c == ChunkOf(secondColumns[i]))
                    {
                        continue;
                    }
                    for (var j = c * perChunk; j < (c + 1) * perChunk; j++)
                    {
                        rows[i].Set(j, 1UL << 40);
                    }
                    spilled++;
                }
            }
            OneCountFromSpilling(rows[0], firstColumns[0]);
            OneCountFromSpilling(rows[1], secondColumns[1]);
            int OnTails() => RowsOf(sketch).Sum(r => r.ChunksWithTails);
            Assert.AreEqual(4, OnTails(), "four chunks should be on tails, none in the last row");
            Assert.AreEqual(0, rows[2].ChunksWithTails);

            sketch.Add(first);

            Assert.AreEqual(5, OnTails(), "the first add should have put its chunk on tails");
            Assert.AreEqual(4096, sketch.Width);
            for (var i = 0; i < rows.Length; i++)
            {
                Assert.AreSame(rows[i], RowsOf(sketch)[i],
                    $"row {i} was laid out again with five of {chunks} chunks on tails, " +
                    "which is within three in a hundred");
            }

            sketch.Add(second);

            Assert.AreEqual(4096, sketch.Width);
            for (var i = 0; i < rows.Length; i++)
            {
                Assert.AreNotSame(rows[i], RowsOf(sketch)[i],
                    $"row {i} was not laid out again with six of {chunks} chunks on tails, " +
                    "which is past three in a hundred");
            }
            Assert.IsTrue(sketch.Count(first) >= 1 && sketch.Count(second) >= 1);
        }

        /// <summary>
        /// A sketch with exactly three in a hundred of its chunks on tails is not past the
        /// fraction: the add that puts the third of a hundred chunks on tails leaves the rows as
        /// they are, and the add that puts the fourth there lays them out again.
        /// </summary>
        /// <remarks>
        /// The test above has 183 chunks, where three in a hundred is 5.49, so no count of chunks
        /// on tails sits on the fraction, and the rule passed there whether it asked for more
        /// than the fraction or for at least it (#275). The fraction is a whole number of chunks
        /// only where the chunks number a multiple of a hundred, and this is the fewest at the
        /// coarsest chunking: a hundred rows at the starting width of 64, one chunk to a row, so
        /// the fraction is three chunks, which is three exactly in floating point too.
        /// <para>
        /// Two rows' chunks are put on tails by hand, and the first two rows' chunks are packed
        /// one count short of spilling, each where a different key lands, so the first key's add
        /// takes the sketch to three chunks on tails and the second's to four. Every other
        /// increment either key takes stops at a stub with room, or lands in a chunk already on
        /// tails, or carries without lengthening its extension.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestASketchAtExactlyTheFractionIsNotLaidOutAgain()
        {
            Func<SublimeCountMinSketch> make = () => new SublimeCountMinSketch(Math.Exp(-99.5));
            var sketch = make();
            var rows = RowsOf(sketch);
            Assert.AreEqual(64, sketch.Width);
            Assert.AreEqual(100, rows.Length);
            var chunks = rows.Sum(r => r.ChunkCount);
            Assert.AreEqual(100, chunks, "a hundred rows of 64 counters, one chunk each");
            Assert.AreEqual(3.0, chunks * 0.03, "three in a hundred of the chunks is not three chunks");

            // Two keys that raise different counters in the second row, where the second is
            // packed: the first key's add must not move that row's chunk.
            var first = Key(0);
            var firstColumns = ColumnsOf(first, make);
            byte[] second = null;
            int[] secondColumns = null;
            for (var k = 1; second == null; k++)
            {
                var columns = ColumnsOf(Key(k), make);
                if (columns[1] != firstColumns[1])
                {
                    second = Key(k);
                    secondColumns = columns;
                }
            }

            for (var i = 2; i < 4; i++)
            {
                for (var j = 0; j < rows[i].Count; j++)
                {
                    rows[i].Set(j, 1UL << 40);
                }
            }
            OneCountFromSpilling(rows[0], firstColumns[0]);
            OneCountFromSpilling(rows[1], secondColumns[1]);
            int OnTails() => RowsOf(sketch).Sum(r => r.ChunksWithTails);
            Assert.AreEqual(2, OnTails(), "two chunks should be on tails, in the third and fourth rows");

            sketch.Add(first);

            Assert.AreEqual(3, OnTails(), "the first add should have put its chunk on tails");
            Assert.AreEqual(64, sketch.Width);
            for (var i = 0; i < rows.Length; i++)
            {
                Assert.AreSame(rows[i], RowsOf(sketch)[i],
                    $"row {i} was laid out again with three of {chunks} chunks on tails, " +
                    "which is three in a hundred and not past it");
            }

            sketch.Add(second);

            Assert.AreEqual(64, sketch.Width);
            for (var i = 0; i < rows.Length; i++)
            {
                Assert.AreNotSame(rows[i], RowsOf(sketch)[i],
                    $"row {i} was not laid out again with four of {chunks} chunks on tails, " +
                    "which is past three in a hundred");
            }
            Assert.IsTrue(sketch.Count(first) >= 1 && sketch.Count(second) >= 1);
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
        /// A sketch read back reports the tuning its rows were built with, not the
        /// default it started from.
        /// </summary>
        /// <remarks>
        /// The reader tunes the rows from the counts it reads, so what it chooses is
        /// what the writer chose only while the counts are the ones the writer chose it
        /// for. That is true at the moment a sketch lays its rows out, and not after
        /// more adds, which change the counts and leave the tuning where it was. So the
        /// sketch is written at those moments, found as the existing tuning test finds
        /// them: the width changes, or the tuning does, and both are a layout.
        /// <para>
        /// The tuning is internal and nothing public reads it, so a reader that never
        /// recorded it answered every query correctly and wrote the same bytes. Only an
        /// inspection of the read-back sketch sees the difference, which is why this
        /// asks the read-back sketch directly. The records of earlier states are tuned
        /// separately, at their own widths, and are not what the properties describe.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestASketchReadBackReportsTheTuningItsRowsWereBuiltWith()
        {
            var sketch = new SublimeCountMinSketch(0.01);
            var startingWidth = sketch.Width;
            var defaults = (
                ValeCounterArray.DefaultCountersPerChunk, ValeCounterArray.DefaultStubBits);

            // Not the first state: a sketch that has never laid its rows out reports the
            // default it was built with, which says nothing about a reader's choice.
            var width = sketch.Width;
            var tuning = (sketch.CountersPerChunk, sketch.StubBits);

            var checks = 0;
            var grown = 0;
            var away = new HashSet<(int, int)>();
            var adds = 0;
            foreach (var key in Skewed(300_000, 100_000, 9))
            {
                sketch.Add(Key(key));
                adds++;

                if (sketch.Width == width
                    && tuning == (sketch.CountersPerChunk, sketch.StubBits))
                {
                    continue;
                }
                width = sketch.Width;
                tuning = (sketch.CountersPerChunk, sketch.StubBits);
                checks++;
                if (width > startingWidth)
                {
                    grown++;
                }
                if (tuning != defaults)
                {
                    away.Add(tuning);
                }

                var stream = new MemoryStream();
                sketch.WriteTo(stream);
                stream.Position = 0;
                var read = SublimeCountMinSketch.ReadFrom(stream);

                Assert.AreEqual(tuning, (read.CountersPerChunk, read.StubBits),
                    $"After {adds:N0} adds the sketch, at a width of {width:N0}, held " +
                    $"{tuning.Item1} counters to a chunk with {tuning.Item2}-bit stubs. " +
                    $"Read back, it reports {read.CountersPerChunk} and {read.StubBits}.");
            }

            // Away from the default, or a reader that never recorded anything passes; and
            // after an expansion, so that there are records for a reader to mistake for
            // the rows.
            Assert.IsTrue(away.Count >= 5,
                $"Only {away.Count} tunings other than the default were seen, so this " +
                "checked almost nothing.");
            Assert.IsTrue(grown >= 3,
                $"Only {grown} of {checks} sketches written had expanded.");
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
        /// The arrays fold back in half once most of the stream has been deleted, and
        /// the counts they carry are neither lost nor doubled.
        /// </summary>
        /// <remarks>
        /// This is the case the record of each expansion exists for, and the sharpest
        /// test of it. An expansion gives both halves of an array the same values, so
        /// simply adding the halves together on the way back down would count
        /// everything inserted before the expansion twice. Here that would turn 2,559
        /// into something near 6,600.
        /// <para>
        /// Only one key is ever added, so nothing can collide with it and the count is
        /// exact. The thresholds are the size function's: a width of 128 folds back
        /// below the average of the two thresholds already crossed, 4,096 and 1,024,
        /// which is 2,560.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestFoldingBackNeitherLosesNorDoublesCounts()
        {
            var sketch = new SublimeCountMinSketch(0.01);
            var key = Key(7);

            for (var i = 0; i < 5000; i++)
            {
                sketch.Add(key);
            }
            Assert.AreEqual(128, sketch.Width, "The arrays should have doubled at 4,096.");
            Assert.AreEqual(5000UL, sketch.Count(key));

            for (var i = 0; i < 2441; i++)
            {
                sketch.Remove(key);
            }

            Assert.AreEqual(64, sketch.Width,
                "Falling to 2,559 keys should have folded the arrays back.");
            Assert.AreEqual(2559UL, sketch.Count(key),
                "The count should be what is left after the deletions. Twice that " +
                "would mean the expansion's copies were added to each other.");
        }

        /// <summary>
        /// Folding back keeps the Count-Min guarantee over a real stream.
        /// </summary>
        /// <remarks>
        /// Counters shared by several keys make the arithmetic of a contraction less
        /// tidy than one key makes it look, and a counter can be left below where it
        /// started once deletions are in play. None of that is allowed to leave an
        /// estimate below the truth.
        /// </remarks>
        [TestMethod]
        public void TestFoldingBackKeepsEstimatesAtOrAboveTheTruth()
        {
            var sketch = new SublimeCountMinSketch(0.01);
            var truth = new Dictionary<int, ulong>();
            var added = new List<int>();

            foreach (var key in Skewed(40_000, 2_000, 3))
            {
                sketch.Add(Key(key));
                added.Add(key);
                truth.TryGetValue(key, out var had);
                truth[key] = had + 1;
            }
            Assert.IsTrue(sketch.Width > 64, "The sketch should have expanded.");
            var grown = sketch.SizeInBytes;

            for (var i = added.Count - 1; i >= 2000; i--)
            {
                sketch.Remove(Key(added[i]));
                truth[added[i]]--;
            }

            Assert.AreEqual(64, sketch.Width,
                "Deleting all but two thousand keys should have folded the arrays " +
                "back to where they started.");
            Assert.IsTrue(sketch.SizeInBytes < grown,
                $"The sketch still takes {sketch.SizeInBytes} bytes where it took " +
                $"{grown} at its largest; folding back should have given memory up.");

            foreach (var (key, want) in truth)
            {
                Assert.IsTrue(sketch.Count(Key(key)) >= want,
                    $"Key {key} is left at {want} but the sketch reports " +
                    $"{sketch.Count(Key(key))}, below the truth.");
            }
        }

        /// <summary>
        /// A sketch that has folded back grows again as though it had never expanded.
        /// </summary>
        [TestMethod]
        public void TestASketchThatFoldedBackGrowsAgain()
        {
            var sketch = new SublimeCountMinSketch(0.01);
            var key = Key(3);

            for (var i = 0; i < 5000; i++)
            {
                sketch.Add(key);
            }
            for (var i = 0; i < 2441; i++)
            {
                sketch.Remove(key);
            }
            Assert.AreEqual(64, sketch.Width);

            for (var i = 0; i < 20_000; i++)
            {
                sketch.Add(key);
            }

            Assert.AreEqual(256, sketch.Width,
                "22,559 keys should have taken the arrays through 4,096 and 16,384.");
            Assert.AreEqual(22_559UL, sketch.Count(key));
        }

        /// <summary>
        /// A sketch sitting on an expansion threshold does not resize on every update.
        /// </summary>
        /// <remarks>
        /// This is the whole reason the threshold to fold back is the average of the
        /// two already crossed rather than the one just crossed. Were it the one just
        /// crossed, a sketch that had expanded at 4,096 keys would fold back on the
        /// next deletion and expand again on the next insertion, rebuilding every
        /// counter twice per pair of updates. The authors' own implementation does
        /// exactly that; the paper says not to.
        /// </remarks>
        [TestMethod]
        public void TestSittingOnAThresholdDoesNotResizeEveryUpdate()
        {
            var sketch = new SublimeCountMinSketch(0.01);
            var key = Key(2);

            for (var i = 0; i < 4096; i++)
            {
                sketch.Add(key);
            }
            Assert.AreEqual(128, sketch.Width);

            for (var i = 0; i < 200; i++)
            {
                sketch.Remove(key);
                Assert.AreEqual(128, sketch.Width,
                    $"The sketch folded back after removal {i + 1}, at " +
                    $"{sketch.TotalCount()} keys, which is far above the 2,560 it " +
                    "should hold to.");

                sketch.Add(key);
                Assert.AreEqual(128, sketch.Width);
            }

            Assert.AreEqual(4096UL, sketch.Count(key));
        }

        /// <summary>
        /// Removing more than was ever added leaves the counts low, not absurd.
        /// </summary>
        /// <remarks>
        /// The sketch cannot tell that a removal is unmatched, and the paper does not
        /// ask it to. What it must not do is fold back into a counter that has gone
        /// below where the expansion left it and read the result as an enormous
        /// positive number.
        /// </remarks>
        [TestMethod]
        public void TestUnmatchedRemovalsDoNotProduceAbsurdCounts()
        {
            var sketch = new SublimeCountMinSketch(0.01);

            for (var i = 0; i < 5000; i++)
            {
                sketch.Add(Key(i % 300));
            }
            Assert.AreEqual(128, sketch.Width);

            // Take out far more than went in, including keys never added at all.
            for (var i = 0; i < 4000; i++)
            {
                sketch.Remove(Key(i % 600));
            }

            Assert.AreEqual(64, sketch.Width, "The sketch should have folded back.");

            for (var key = 0; key < 600; key++)
            {
                Assert.IsTrue(sketch.Count(Key(key)) < 100_000,
                    $"Key {key} reports {sketch.Count(Key(key))}, which is a counter " +
                    "that went below nought and was read as unsigned.");
            }
        }

        /// <summary>
        /// A sketch at its starting size has nothing to fold back to.
        /// </summary>
        [TestMethod]
        public void TestASketchThatNeverGrewDoesNotFoldBack()
        {
            var sketch = new SublimeCountMinSketch(0.01);
            var key = Key(1);

            for (var i = 0; i < 100; i++)
            {
                sketch.Add(key);
            }
            for (var i = 0; i < 200; i++)
            {
                sketch.Remove(key);
            }

            Assert.AreEqual(64, sketch.Width);
            Assert.AreEqual(0UL, sketch.TotalCount());
            Assert.AreEqual(0UL, sketch.Count(key));
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
        /// A sketch survives a round trip through a stream, records and all.
        /// </summary>
        /// <remarks>
        /// The records of earlier states matter as much as the counters: a sketch read
        /// back without them could still grow but could never fold back, so it would
        /// answer the same and behave differently.
        /// </remarks>
        [TestMethod]
        public void TestASketchSurvivesARoundTrip()
        {
            var sketch = new SublimeCountMinSketch(0.01);
            var truth = new Dictionary<int, ulong>();
            foreach (var key in Skewed(50_000, 5_000, 12))
            {
                sketch.Add(Key(key));
                truth.TryGetValue(key, out var had);
                truth[key] = had + 1;
            }
            Assert.IsTrue(sketch.Width > 64, "The sketch should have expanded.");

            var stream = new MemoryStream();
            sketch.WriteTo(stream);
            stream.Position = 0;
            var read = SublimeCountMinSketch.ReadFrom(stream);

            Assert.AreEqual(sketch.Width, read.Width);
            Assert.AreEqual(sketch.Depth, read.Depth);
            Assert.AreEqual(sketch.TotalCount(), read.TotalCount());

            foreach (var (key, want) in truth)
            {
                Assert.AreEqual(sketch.Count(Key(key)), read.Count(Key(key)),
                    $"Key {key}, added {want} times, reads back differently.");
            }

            // The records came back too, so the sketch can still fold.
            var widthBefore = read.Width;
            for (var i = 0; i < 45_000; i++)
            {
                read.Remove(Key(1));
            }
            Assert.IsTrue(read.Width < widthBefore,
                "A sketch read back should still fold when its stream is deleted.");
        }

        /// <summary>
        /// A sketch that has never expanded survives a round trip.
        /// </summary>
        [TestMethod]
        public void TestASketchWithNoRecordsSurvivesARoundTrip()
        {
            var sketch = new SublimeCountMinSketch(0.01);
            sketch.Add(Key(1));

            var stream = new MemoryStream();
            sketch.WriteTo(stream);
            stream.Position = 0;
            var read = SublimeCountMinSketch.ReadFrom(stream);

            Assert.AreEqual(64, read.Width);
            Assert.AreEqual(1UL, read.TotalCount());
            Assert.AreEqual(1UL, read.Count(Key(1)));
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
