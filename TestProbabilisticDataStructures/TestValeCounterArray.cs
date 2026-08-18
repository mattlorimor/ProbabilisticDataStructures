using System;
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
    }
}
