using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Tests for the chunked counter array behind Sublime (Eslami, Bercea, Pagh and
    /// Dayan, SIGMOD 2026), section 4.1.
    /// </summary>
    /// <remarks>
    /// Almost everything here is checked against a plain array of counts. That is the
    /// only honest oracle available: the whole structure is an exercise in storing the
    /// same numbers in less room, so the numbers themselves are what must not change.
    /// The failures worth fearing are not wrong values in isolation but a counter whose
    /// extension shifts on top of its neighbour's, which shows up only when every
    /// counter is read after a long run of writes.
    /// </remarks>
    [TestClass]
    public class TestValeCounterArray
    {
        private const int C = ValeCounterArray.DefaultCountersPerChunk;
        private const int S = ValeCounterArray.DefaultStubBits;

        /// <summary>
        /// A long run of mixed writes leaves every counter holding what a plain array
        /// would hold, in every tuning the parameters allow.
        /// </summary>
        /// <remarks>
        /// The values are deliberately lopsided: mostly small, occasionally enormous.
        /// A uniform workload would keep every extension the same length, and an
        /// extension that never changes length never moves its neighbours.
        /// </remarks>
        [TestMethod]
        public void TestEveryCounterHoldsWhatAPlainArrayWould()
        {
            foreach (var (counters, stubBits) in new[] { (C, S), (16, 4), (92, 4), (32, 8), (24, 16) })
            {
                var count = counters * 5 + 3;
                var array = new ValeCounterArray(count, counters, stubBits);
                var expected = new ulong[count];
                var random = new Random(19);

                for (var step = 0; step < 50000; step++)
                {
                    var i = random.Next(count);
                    switch (random.Next(10))
                    {
                        case 0:
                        case 1:
                        case 2:
                        case 3:
                        case 4:
                        case 5:
                            array.Increment(i);
                            expected[i]++;
                            break;
                        case 6:
                        case 7:
                            array.Decrement(i);
                            if (expected[i] > 0)
                            {
                                expected[i]--;
                            }
                            break;
                        default:
                            var value = random.Next(4) == 0
                                ? (ulong)random.Next(1 << 20) * (ulong)random.Next(1, 4096)
                                : (ulong)random.Next(64);
                            array.Set(i, value);
                            expected[i] = value;
                            break;
                    }
                }

                for (var i = 0; i < count; i++)
                {
                    Assert.AreEqual(expected[i], array.Get(i),
                        $"Counter {i} of {count} disagrees with a plain array at " +
                        $"{counters} counters per chunk and a {stubBits}-bit stub.");
                }
            }
        }

        /// <summary>
        /// Growing and shrinking one counter over and over leaves the rest of its chunk
        /// untouched.
        /// </summary>
        /// <remarks>
        /// Every one of these writes changes the length of the middle counter's
        /// extension, so every one of them shifts the extensions of the counters after
        /// it. This is the narrowest test of the shifting: the neighbours never change,
        /// so anything that moves them is the shift's fault and nothing else's.
        /// </remarks>
        [TestMethod]
        public void TestReshapingOneExtensionLeavesItsNeighboursAlone()
        {
            var array = new ValeCounterArray(C, C, S);
            var expected = new ulong[C];

            // A quiet chunk with a few counters already overflowing around the one that
            // will be disturbed.
            foreach (var i in new[] { 0, 1, 5, 9, 30, 67 })
            {
                array.Set(i, 40 + (ulong)i * 7);
                expected[i] = 40 + (ulong)i * 7;
            }

            var random = new Random(4);
            for (var step = 0; step < 2000; step++)
            {
                var value = (ulong)random.Next(1 << 22);
                array.Set(7, value);
                expected[7] = value;

                for (var i = 0; i < C; i++)
                {
                    Assert.AreEqual(expected[i], array.Get(i),
                        $"Counter {i} changed while counter 7 was being reshaped to " +
                        $"{value} on step {step}.");
                }
            }
        }

        /// <summary>
        /// A chunk falls back to a tails array exactly when its pool cannot hold one
        /// more extension, and keeps its counters through the change.
        /// </summary>
        /// <remarks>
        /// The numbers are the encoding's, not a measurement's. At sixty-eight counters
        /// and a five-bit stub a chunk keeps fifty-one fragments. A count of a million
        /// leaves 31250 above its stub, which is ten base-three digits and a delimiter,
        /// so eleven fragments. Four of those fit in fifty-one and five do not.
        /// </remarks>
        [TestMethod]
        public void TestAChunkFallsBackWhenItsPoolIsFull()
        {
            const ulong Million = 1_000_000;

            var array = new ValeCounterArray(C, C, S);
            Assert.AreEqual(51, array.PoolFragments,
                "A sixty-eight counter chunk with five-bit stubs keeps fifty-one " +
                "fragments for extensions.");
            Assert.AreEqual(11, ValeCounter.ExtensionLength(Million >> S),
                "A million needs eleven fragments above a five-bit stub.");

            for (var i = 0; i < 4; i++)
            {
                array.Set(i, Million);
                Assert.AreEqual(0, array.ChunksWithTails,
                    $"Four extensions of eleven fragments fit in fifty-one, so {i + 1} " +
                    "of them should not have needed a tails array.");
            }

            array.Set(4, Million);
            Assert.AreEqual(1, array.ChunksWithTails,
                "A fifth extension of eleven fragments does not fit in fifty-one.");

            for (var i = 0; i < 5; i++)
            {
                Assert.AreEqual(Million, array.Get(i),
                    $"Counter {i} did not survive the chunk falling back to tails.");
            }
        }

        /// <summary>
        /// A chunk on tails keeps working: it accepts new counters, gives them up, and
        /// still reads back everything it holds.
        /// </summary>
        [TestMethod]
        public void TestAChunkOnTailsStillKeepsItsCounters()
        {
            var array = new ValeCounterArray(C, C, S);
            var expected = new ulong[C];

            for (var i = 0; i < 6; i++)
            {
                array.Set(i, 1_000_000);
                expected[i] = 1_000_000;
            }
            Assert.AreEqual(1, array.ChunksWithTails, "This chunk should be on tails.");

            var random = new Random(23);
            for (var step = 0; step < 20000; step++)
            {
                var i = random.Next(C);
                var value = random.Next(3) == 0
                    ? (ulong)random.Next(1 << 24)
                    : (ulong)random.Next(1 << 4);
                array.Set(i, value);
                expected[i] = value;
            }

            for (var i = 0; i < C; i++)
            {
                Assert.AreEqual(expected[i], array.Get(i),
                    $"Counter {i} disagrees after its chunk fell back to tails.");
            }
        }

        /// <summary>
        /// A counter with a chunk to itself holds any count a ulong can express.
        /// </summary>
        /// <remarks>
        /// Base three costs about a fifth more than binary, so the largest count needs
        /// thirty-nine fragments of the fifty-one a chunk keeps. Nothing is capped and
        /// nothing wraps.
        /// </remarks>
        [TestMethod]
        public void TestALoneCounterHoldsTheLargestCount()
        {
            var array = new ValeCounterArray(C, C, S);

            array.Set(0, ulong.MaxValue);

            Assert.AreEqual(ulong.MaxValue, array.Get(0));
            Assert.AreEqual(0, array.ChunksWithTails,
                "The largest count a ulong holds still fits one chunk's pool.");
        }

        /// <summary>
        /// A count too large for even an empty pool goes to tails rather than being
        /// truncated.
        /// </summary>
        /// <remarks>
        /// The tightest tuning allowed keeps twenty-five fragments, and the largest
        /// count above a four-bit stub needs thirty-nine. This is the one case where a
        /// chunk falls back with nothing in its pool to move.
        /// </remarks>
        [TestMethod]
        public void TestACountTooLargeForAnEmptyPoolGoesToTails()
        {
            var array = new ValeCounterArray(92, 92, 4);
            Assert.IsTrue(
                ValeCounter.ExtensionLength(ulong.MaxValue >> 4) > array.PoolFragments,
                "This tuning is only interesting if one count cannot fit the pool.");

            array.Set(0, ulong.MaxValue);

            Assert.AreEqual(ulong.MaxValue, array.Get(0));
            Assert.AreEqual(1, array.ChunksWithTails);
        }

        /// <summary>
        /// Counters that stay inside their stubs cost nothing beyond it.
        /// </summary>
        /// <remarks>
        /// This is the claim the encoding is built on. A workload that never overflows
        /// a stub should never touch a pool, never allocate a tails array, and occupy
        /// exactly the chunks it was given.
        /// </remarks>
        [TestMethod]
        public void TestASmallCountCostsNothingBeyondItsStub()
        {
            var array = new ValeCounterArray(C * 4, C, S);
            var before = array.SizeInBytes;

            var random = new Random(31);
            for (var step = 0; step < 20000; step++)
            {
                array.Set(random.Next(C * 4), (ulong)random.Next(1 << S));
            }

            Assert.AreEqual(0, array.ChunksWithTails);
            Assert.AreEqual(before, array.SizeInBytes,
                "Counters that fit their stubs should not have grown the array.");
            Assert.AreEqual(4L * ValeCounterArray.ChunkBits / 8, before,
                "Four chunks of a cache line each is all this should ever be.");
        }

        /// <summary>
        /// Under skew, the counters cost less than a fixed-width array holding the same
        /// counts, which is the whole reason for the encoding.
        /// </summary>
        /// <remarks>
        /// Measured, not derived. Two million events over sixty-five thousand counters,
        /// drawn so that a few counters take most of them: the largest count needs
        /// nineteen bits, so a fixed-width array sized after the fact -- which no real
        /// sketch could do, having not seen the stream yet -- would need 155,648 bytes.
        /// The bound below is the measurement with room to move; it is here to catch a
        /// change that costs space, not to pin a number to the byte.
        /// </remarks>
        [TestMethod]
        public void TestSkewedCountsCostLessThanFixedWidthOnes()
        {
            const int Counters = 65536;

            var array = new ValeCounterArray(Counters, C, S);
            var expected = new ulong[Counters];

            var random = new Random(11);
            for (var step = 0; step < 2_000_000; step++)
            {
                // A rough Zipf: most draws land in the first few counters.
                var i = (int)(Counters * Math.Pow(random.NextDouble(), 8));
                if (i >= Counters)
                {
                    i = Counters - 1;
                }

                array.Increment(i);
                expected[i]++;
            }

            var largest = 0UL;
            for (var i = 0; i < Counters; i++)
            {
                Assert.AreEqual(expected[i], array.Get(i), $"Counter {i} disagrees.");
                largest = Math.Max(largest, expected[i]);
            }

            var width = 1;
            while (largest >= 1UL << width)
            {
                width++;
            }

            var fixedWidth = (long)Counters * width / 8;
            Assert.IsTrue(array.SizeInBytes < fixedWidth,
                $"The counters took {array.SizeInBytes} bytes where a {width}-bit " +
                $"fixed-width array would have taken {fixedWidth}.");
            Assert.IsTrue(array.SizeInBytes < 120_000,
                $"The counters took {array.SizeInBytes} bytes, well above the 83,456 " +
                "this workload measured at.");
        }

        /// <summary>
        /// Incrementing agrees with reading, adding one, and writing back.
        /// </summary>
        /// <remarks>
        /// Incrementing takes a short cut when a stub has room left, which is most of
        /// the time and is the paper's reason for stubs at all. The short cut has to be
        /// invisible, including on the step that exhausts a stub and on the steps that
        /// lengthen an extension.
        /// </remarks>
        [TestMethod]
        public void TestIncrementingAgreesWithWritingBack()
        {
            var quick = new ValeCounterArray(C, C, S);
            var slow = new ValeCounterArray(C, C, S);

            for (var step = 0; step < 4000; step++)
            {
                for (var i = 0; i < 3; i++)
                {
                    quick.Increment(i);
                    slow.Set(i, slow.Get(i) + 1);
                }

                for (var i = 0; i < 3; i++)
                {
                    Assert.AreEqual(slow.Get(i), quick.Get(i),
                        $"Counter {i} disagrees after {step + 1} increments.");
                }
            }
        }

        /// <summary>
        /// Decrementing agrees with writing back, and a counter at nought stays there.
        /// </summary>
        [TestMethod]
        public void TestDecrementingAgreesWithWritingBackAndStopsAtNought()
        {
            var array = new ValeCounterArray(C, C, S);

            array.Set(0, 70);
            for (var expected = 69; expected >= 0; expected--)
            {
                array.Decrement(0);
                Assert.AreEqual((ulong)expected, array.Get(0));
            }

            array.Decrement(0);
            Assert.AreEqual(0UL, array.Get(0),
                "A counter at nought has nothing to give up.");
        }

        /// <summary>
        /// A counter at the largest count a ulong holds stays there when it is incremented,
        /// and the one below it takes its last step up to it.
        /// </summary>
        /// <remarks>
        /// The mirror of the floor at nought. Adding one to the largest count is nought in a
        /// ulong, and an array that wrote that back would turn the largest count the sketch
        /// can hold into the smallest. The counters either side and a heavy one in another
        /// chunk are checked as well, since writing nought over a counter that held a
        /// full-length extension frees its fragments, and what was packed beside them is
        /// what would move. The tunings are the ones the round-trip test above uses, so that
        /// the largest count sits in a pool at some of them and on a tails array at others.
        /// </remarks>
        [TestMethod]
        public void TestIncrementingTheLargestCountLeavesItThere()
        {
            foreach (var (counters, stubBits) in new[] { (C, S), (16, 4), (92, 4), (32, 8), (24, 16) })
            {
                var array = new ValeCounterArray(counters * 2, counters, stubBits);
                var where = $"{counters} counters per chunk and a {stubBits}-bit stub";

                const int Target = 1;
                var other = counters + 1;
                array.Set(Target - 1, 7);
                array.Set(Target + 1, 123_456_789);
                array.Set(other, ulong.MaxValue - 1);

                // Two below the largest: one step to the one below it, one to it, and then
                // the steps that must go nowhere.
                array.Set(Target, ulong.MaxValue - 2);
                var expected = new[]
                {
                    ulong.MaxValue - 1, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue,
                };
                for (var step = 0; step < expected.Length; step++)
                {
                    array.Increment(Target);
                    Assert.AreEqual(expected[step], array.Get(Target),
                        $"Step {step + 1} up from the largest count less two, at {where}.");
                    Assert.AreEqual(7UL, array.Get(Target - 1), $"The counter below, at {where}.");
                    Assert.AreEqual(123_456_789UL, array.Get(Target + 1),
                        $"The counter above, at {where}.");
                    Assert.AreEqual(ulong.MaxValue - 1, array.Get(other),
                        $"A heavy counter in the next chunk, at {where}.");
                }
            }
        }

        /// <summary>
        /// Chunks do not reach into one another.
        /// </summary>
        /// <remarks>
        /// A chunk's pool is the last thing in its cache line, so an extension that
        /// grew past the end of one would land in the next chunk's overflows bitmap.
        /// </remarks>
        [TestMethod]
        public void TestOneChunkDoesNotDisturbTheNext()
        {
            var array = new ValeCounterArray(C * 3, C, S);
            var expected = new ulong[C * 3];

            // Everything starts inside its stub, so the outer chunks have empty pools
            // and nothing of their own to blame a wrong value on.
            for (var i = 0; i < C * 3; i++)
            {
                expected[i] = (ulong)(i % (1 << S));
                array.Set(i, expected[i]);
            }
            Assert.AreEqual(0, array.ChunksWithTails);

            // Fill the middle chunk hard enough to force it onto tails.
            for (var i = C; i < C + 8; i++)
            {
                expected[i] = 5_000_000;
                array.Set(i, 5_000_000);
            }
            Assert.AreEqual(1, array.ChunksWithTails, "Only the middle chunk should have given up.");

            for (var i = 0; i < C * 3; i++)
            {
                Assert.AreEqual(expected[i], array.Get(i), $"Counter {i} disagrees.");
            }
        }

        /// <summary>
        /// A tuning that leaves no useful pool is refused when the array is built,
        /// rather than at the first count that overflows.
        /// </summary>
        [TestMethod]
        public void TestATuningWithNoRoomForExtensionsIsRefused()
        {
            // Ninety-two counters with six-bit stubs need 552 bits of a 512-bit chunk.
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new ValeCounterArray(100, 92, 6));

            // Sixteen thirty-two-bit stubs leave 495 bits, but the bitmap and the
            // twenty-four fragments a chunk must keep want more.
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new ValeCounterArray(100, 16, 32));

            // Sixty-eight six-bit stubs leave room for seventeen fragments. That is a
            // pool, just not one worth having: a chunk is required to keep at least
            // twenty-four, which is the floor the paper's own tuning works within and
            // the width its tails pointer needs. A tuning between the two is the only
            // thing that tells the floor apart from a check that the pool is not
            // negative.
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new ValeCounterArray(100, 68, 6));

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new ValeCounterArray(100, ValeCounterArray.MinCountersPerChunk - 1, S));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new ValeCounterArray(100, ValeCounterArray.MaxCountersPerChunk + 1, S));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new ValeCounterArray(100, C, ValeCounterArray.MinStubBits - 1));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new ValeCounterArray(0, C, S));
        }

        /// <summary>
        /// Counters outside the array are refused.
        /// </summary>
        [TestMethod]
        public void TestCountersOutsideTheArrayAreRefused()
        {
            var array = new ValeCounterArray(100, C, S);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => array.Get(100));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => array.Get(-1));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => array.Set(100, 1));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => array.Increment(100));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => array.Decrement(100));
        }

        /// <summary>
        /// A copy holds the same counters packed the same way, and the two change apart: a
        /// write to either, to a chunk on tails, to one moving onto tails or to one in its
        /// pool, leaves the other as it was.
        /// </summary>
        /// <remarks>
        /// Three chunks: the first on tails, the second one add from spilling its pool, and the
        /// third in its pool. A copy sharing the words, a tails array, or the dictionary of
        /// them would show the other's write. Two tunings, so that a copy made at the default
        /// one is caught as well.
        /// </remarks>
        [TestMethod]
        [DataRow(C, S)]
        [DataRow(ValeCounterArray.MaxCountersPerChunk, ValeCounterArray.MinStubBits)]
        public void TestACopyHoldsTheSameCountersAndChangesApartFromTheArray(
            int countersPerChunk, int stubBits)
        {
            const ulong Large = 1UL << 40;
            var array = new ValeCounterArray(3 * countersPerChunk, countersPerChunk, stubBits);
            for (var i = 0; array.ChunksWithTails == 0; i++)
            {
                array.Set(i, Large);
            }
            OneAddFromSpilling(array, countersPerChunk);
            array.Set(2 * countersPerChunk, 7);
            array.Set(2 * countersPerChunk + 1, Large);
            Assert.AreEqual(1, array.ChunksWithTails, "the first chunk alone should be on tails");

            var copy = array.Copy();
            AssertAlike(array, copy, "a copy just made");

            var held = CountsOf(array);
            copy.Set(1, 3 * Large);
            copy.Increment(countersPerChunk);
            copy.Set(2 * countersPerChunk + 1, 5);
            Assert.AreEqual(2, copy.ChunksWithTails, "the copy's second chunk did not spill");
            CollectionAssert.AreEqual(held, CountsOf(array), "a write to the copy changed the array");
            Assert.AreEqual(1, array.ChunksWithTails, "the copy's spill put the array's chunk on tails");

            held = CountsOf(copy);
            array.Set(0, 5 * Large);
            array.Increment(countersPerChunk);
            array.Set(2 * countersPerChunk, 9);
            CollectionAssert.AreEqual(held, CountsOf(copy), "a write to the array changed the copy");
            AssertAlike(array, array.Copy(), "a copy of the array as it now is");
        }

        /// <summary>
        /// Asked before each increment of a long run whether the increment will move its chunk
        /// to a tails array, the array answers what the increment then does, at every kind of
        /// counter the run reaches: a stub with room, a full stub whose longer extension fits
        /// and one whose extension spills, a full stub in a chunk already on tails, and a
        /// counter at the largest count.
        /// </summary>
        /// <remarks>
        /// The sketch lays its rows out again, building them first, at the add whose increments
        /// would put too many chunks on tails, so it has to know that before it takes them; the
        /// answer is checked against the move itself, not against a second reading of the rule.
        /// Each kind is counted, and each must have been seen, so a run that never spilled a
        /// chunk, or never filled a stub, fails rather than agreeing about nothing. The
        /// tunings are the default, the narrowest stubs with the most counters, which spill
        /// soonest, and a pool wide enough that only longer extensions spill it.
        /// </remarks>
        [TestMethod]
        [DataRow(C, S)]
        [DataRow(ValeCounterArray.MaxCountersPerChunk, ValeCounterArray.MinStubBits)]
        [DataRow(40, 6)]
        public void TestAnIncrementSaysBeforehandWhetherItMovesItsChunkToTails(
            int countersPerChunk, int stubBits)
        {
            const int Chunks = 64;
            const int Steps = 400_000;
            var array = new ValeCounterArray(Chunks * countersPerChunk, countersPerChunk, stubBits);
            var mask = (1UL << stubBits) - 1;
            var onTails = new System.Collections.Generic.HashSet<int>();

            // One counter in every eighth chunk starts at the largest count, where an increment
            // changes nothing; at narrow stubs its extension alone spills the chunk.
            for (var chunk = 0; chunk < Chunks; chunk += 8)
            {
                var before = array.ChunksWithTails;
                array.Set(chunk * countersPerChunk + 1, ulong.MaxValue);
                if (array.ChunksWithTails > before)
                {
                    onTails.Add(chunk);
                }
            }

            const int Roomy = 0, Fits = 1, Spills = 2, OnTails = 3, Largest = 4;
            var seen = new int[5];
            var wrong = new System.Collections.Generic.List<string>();
            var random = new Random(242);
            for (var step = 0; step < Steps; step++)
            {
                var j = random.Next(array.Count);
                var chunk = j / countersPerChunk;
                var value = array.Get(j);

                var predicted = array.IncrementMovesToTails(j);
                var before = array.ChunksWithTails;
                array.Increment(j);
                var moved = array.ChunksWithTails - before;

                if (moved != (predicted ? 1 : 0) && wrong.Count < 10)
                {
                    wrong.Add($"step {step}, counter {j} at {value}: predicted {predicted}, moved {moved}");
                }

                var kind = value == ulong.MaxValue ? Largest
                    : (value & mask) != mask ? Roomy
                    : onTails.Contains(chunk) ? OnTails
                    : moved == 1 ? Spills
                    : Fits;
                seen[kind]++;
                if (moved == 1)
                {
                    onTails.Add(chunk);
                }
            }

            Assert.AreEqual(0, wrong.Count, string.Join(Environment.NewLine, wrong));
            var tally = $"roomy {seen[Roomy]}, fits {seen[Fits]}, spills {seen[Spills]}, " +
                $"on tails {seen[OnTails]}, largest {seen[Largest]}";
            Assert.IsTrue(seen.All(n => n > 0), $"a kind of counter went unseen: {tally}");
        }

        /// <summary>
        /// At the edge of a pool, whether an increment moves its chunk to a tails array is
        /// answered as it then happens, and as the pool's size says it must: a carry whose
        /// extension keeps its length fits a pool with no room for a longer one, one that grows
        /// into the pool's last free fragment fits, and one that grows into a pool already full
        /// spills, as does a counter's first extension into one fragment of room.
        /// </summary>
        /// <remarks>
        /// The long run above reaches these only by chance, and passed with the answer taken at
        /// the count as it is rather than one higher, with the counter's own extension left out,
        /// and with a pool filled exactly taken for one too small. Each case is built exactly at the
        /// default tuning: a pool of 51 fragments, two for an extension of an overflow of one or
        /// two (a count of 32 to 95) and three for three to eight. The answer and the move are
        /// both held to what the pool's size says, so a rule that moved early would not pass by
        /// agreeing with itself.
        /// </remarks>
        [TestMethod]
        [DataRow(24, 0, 63UL, false, DisplayName = "keeps its length, 50 of 51 fragments")]
        [DataRow(24, 0, 95UL, false, DisplayName = "grows into the last free fragment")]
        [DataRow(23, 1, 95UL, true, DisplayName = "grows into a pool already full")]
        [DataRow(25, 0, 31UL, true, DisplayName = "a first extension into one fragment of room")]
        public void TestAnIncrementAtTheEdgeOfAPoolIsAnsweredAsThePoolSays(
            int shortest, int longer, ulong value, bool spills)
        {
            Assert.AreEqual(51, new ValeCounterArray(C, C, S).PoolFragments);
            Assert.AreEqual(2, ValeCounter.ExtensionLength(1));
            Assert.AreEqual(2, ValeCounter.ExtensionLength(2));
            Assert.AreEqual(3, ValeCounter.ExtensionLength(3));

            var array = new ValeCounterArray(C, C, S);
            var j = 0;
            for (var n = 0; n < shortest; n++)
            {
                array.Set(j++, 1UL << S);
            }
            for (var n = 0; n < longer; n++)
            {
                array.Set(j++, 3UL << S);
            }
            array.Set(j, value);
            Assert.AreEqual(0, array.ChunksWithTails, "the pool spilled while it was filled");

            Assert.AreEqual(spills, array.IncrementMovesToTails(j), "the answer before the increment");
            array.Increment(j);
            Assert.AreEqual(spills ? 1 : 0, array.ChunksWithTails, "what the increment did");
            Assert.AreEqual(value + 1, array.Get(j), "the count the increment left");
        }

        /// <summary>
        /// An increment that moves its chunk to a tails array reserved for it beforehand leaves
        /// the array as the increment alone leaves it, and allocates nothing: the reservation
        /// holds the array and the room to put it in place. That is what lets an add reserve one
        /// for every chunk its increments move and then take them all without running out of
        /// memory partway (#274).
        /// </summary>
        /// <remarks>
        /// The first chunk is one add from spilling, the second holds an extension, and
        /// <paramref name="onTails"/> chunks after them are on tails already. Between reserving
        /// and moving, a counter of the second chunk is read, which leaves that chunk's pool in
        /// the scratch buffer the move decants from; the move has to read its own. The twin
        /// takes the increment alone, which allocates its tails array, and the two are held to
        /// each other counter by counter. What each allocates is the least of five tries, each
        /// counted in a no-GC region, as a collection running beside one can add to it
        /// (<see cref="NoGCRegion"/>), and the twin's has to be more than nought, so the
        /// measurement can see the allocation it says is gone.
        /// </remarks>
        [TestMethod]
        [DataRow(0, DisplayName = "the first chunk on tails")]
        [DataRow(3, DisplayName = "three chunks on tails already")]
        public void TestAReservedMoveToTailsIsTheIncrementsOwnAndAllocatesNothing(int onTails)
        {
            ValeCounterArray Make()
            {
                var array = new ValeCounterArray((2 + onTails) * C, C, S);
                for (var chunk = 2; chunk < 2 + onTails; chunk++)
                {
                    array.Set(chunk * C, ulong.MaxValue);
                    array.Set(chunk * C + 1, ulong.MaxValue);
                }
                array.Set(C + 3, 1_000);
                OneAddFromSpilling(array, 0);
                Assert.AreEqual(onTails, array.ChunksWithTails, "the chunks set to be on tails");
                Assert.IsTrue(array.IncrementMovesToTails(0), "the first chunk is not one add from spilling");
                return array;
            }

            var reservedBytes = long.MaxValue;
            var aloneBytes = long.MaxValue;
            for (var trial = 0; trial < 5; trial++)
            {
                // Each array is made afresh before each attempt at its measurement, in a
                // no-GC region (#293), since what is measured changes it and an attempt can
                // be repeated.
                ValeCounterArray twin = null;
                aloneBytes = Math.Min(aloneBytes, NoGCRegion.Allocated(
                    () => { twin.Increment(0); },
                    first: () => twin = Make()));

                ValeCounterArray array = null;
                ulong[] tails = null;
                reservedBytes = Math.Min(reservedBytes, NoGCRegion.Allocated(
                    () =>
                    {
                        array.MoveChunkToTails(0, tails);
                        array.Increment(0);
                    },
                    first: () =>
                    {
                        array = Make();
                        tails = array.ReserveTails();
                        Assert.AreEqual(onTails, array.ChunksWithTails, "reserving moved a chunk");
                        Assert.AreEqual(1_000UL, array.Get(C + 3));
                    }));

                AssertAlike(twin, array, $"trial {trial}");
                Assert.AreEqual(onTails + 1, array.ChunksWithTails, $"trial {trial}: chunks on tails");
            }

            Assert.IsTrue(aloneBytes > 0, "the increment alone allocated nothing, so the measurement sees nothing");
            Assert.AreEqual(0L, reservedBytes, $"the reserved move allocated, where the increment alone took {aloneBytes}");
        }

        /// <summary>
        /// Fills the pool of the chunk starting at <paramref name="first"/> with as many of the
        /// shortest extensions as it holds, and leaves its first counter one count short of
        /// needing one more.
        /// </summary>
        private static void OneAddFromSpilling(ValeCounterArray array, int first)
        {
            var shortest = 1UL << array.StubBits;
            var fit = array.PoolFragments / ValeCounter.ExtensionLength(1);
            Assert.IsTrue(fit < array.CountersPerChunk, "the chunk is too short to fill its pool");
            for (var i = 1; i <= fit; i++)
            {
                array.Set(first + i, shortest);
            }
            array.Set(first, shortest - 1);
        }

        private static ulong[] CountsOf(ValeCounterArray array)
        {
            var counts = new ulong[array.Count];
            for (var i = 0; i < counts.Length; i++)
            {
                counts[i] = array.Get(i);
            }
            return counts;
        }

        private static void AssertAlike(ValeCounterArray expected, ValeCounterArray actual, string what)
        {
            CollectionAssert.AreEqual(CountsOf(expected), CountsOf(actual), $"{what}: the counts");
            Assert.AreEqual(expected.CountersPerChunk, actual.CountersPerChunk, $"{what}: counters a chunk");
            Assert.AreEqual(expected.StubBits, actual.StubBits, $"{what}: stub bits");
            Assert.AreEqual(expected.ChunksWithTails, actual.ChunksWithTails, $"{what}: chunks on tails");
            Assert.AreEqual(expected.SizeInBytes, actual.SizeInBytes, $"{what}: bytes");
        }
    }
}
