using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Tests for the keepsake box encoding <see cref="MementoFilter"/> packs a run
    /// with, from Eslami and Dayan (SIGMOD 2025), section 4.
    /// </summary>
    /// <remarks>
    /// The encoding writes down no boundaries. Where one box ends and the next begins
    /// is recovered from a zero fingerprint that cannot occur naturally, so almost
    /// every way of getting this wrong produces a run that still decodes -- into the
    /// wrong boxes. That makes round-tripping the thing to test, across every shape of
    /// run rather than a few chosen ones.
    /// </remarks>
    [TestClass]
    public class TestKeepsakeCodec
    {
        private static void AssertRoundTrips(
            int fingerprintBits, int mementoBits, List<KeepsakeBox> boxes)
        {
            var codec = new KeepsakeCodec(fingerprintBits, mementoBits);
            var fields = codec.Encode(boxes);
            var back = codec.Decode(fields);

            Assert.AreEqual(boxes.Count, back.Count,
                $"A run of {boxes.Count} boxes came back as {back.Count}. The " +
                "boundaries between them are inferred, so a miscount means two boxes " +
                "were run together or one was split.");

            for (var i = 0; i < boxes.Count; i++)
            {
                Assert.AreEqual(boxes[i].Fingerprint, back[i].Fingerprint,
                    $"Box {i} came back with the wrong fingerprint.");
                CollectionAssert.AreEqual(boxes[i].Mementos, back[i].Mementos,
                    $"Box {i} came back holding different keys.");
            }
        }

        private static KeepsakeBox Box(ulong fingerprint, params ulong[] mementos)
        {
            var box = new KeepsakeBox { Fingerprint = fingerprint };
            box.Mementos.AddRange(mementos.OrderBy(m => m));
            return box;
        }

        /// <summary>
        /// One box of every length from one key to well past the point where the
        /// layout changes, which is where the boundaries are most likely to be got
        /// wrong.
        /// </summary>
        [TestMethod]
        public void TestABoxOfEveryLengthRoundTrips()
        {
            const int fingerprintBits = 8;
            const int mementoBits = 6;

            for (var length = 1; length <= 40; length++)
            {
                var mementos = Enumerable.Range(0, length)
                    .Select(i => (ulong)(i * 63 / Math.Max(length - 1, 1)))
                    .ToArray();

                AssertRoundTrips(fingerprintBits, mementoBits,
                    new List<KeepsakeBox> { Box(37, mementos) });
            }
        }

        /// <summary>
        /// The three layouts sit at one, two and three keys, so those are the two
        /// boundaries the encoding turns on.
        /// </summary>
        [TestMethod]
        public void TestTheLayoutBoundariesAreWhereTheyShouldBe()
        {
            var codec = new KeepsakeCodec(8, 6);

            var one = codec.Encode(new List<KeepsakeBox> { Box(9, 5) });
            var two = codec.Encode(new List<KeepsakeBox> { Box(9, 5, 6) });
            var three = codec.Encode(new List<KeepsakeBox> { Box(9, 5, 6, 7) });

            Assert.AreEqual(1, one.Count, "One key should take one slot.");
            Assert.AreEqual(2, two.Count, "Two keys should take a slot each.");
            Assert.IsTrue(three.Count >= 3,
                "Three keys need the overflow layout, so at least three slots.");

            // The second slot of the overflow layout carries a zero fingerprint, which
            // is the marker the reader looks for. Nothing else may.
            Assert.AreNotEqual(0UL, two[1] >> 6,
                "A two-key box repeats its fingerprint; a zero there would be read as " +
                "an overflow marker.");
            Assert.AreEqual(0UL, three[1] >> 6,
                "The overflow marker is a zero fingerprint in the second slot.");
        }

        /// <summary>
        /// Several boxes in one run, of mixed lengths.
        /// </summary>
        [TestMethod]
        public void TestSeveralBoxesInOneRunRoundTrip()
        {
            var runs = new List<List<KeepsakeBox>>
            {
                new() { Box(1, 0), Box(2, 3), Box(9, 1, 2) },
                new() { Box(3, 1, 2, 3, 4), Box(4, 7) },
                new() { Box(1, 5), Box(2, 1, 2, 3), Box(3, 4, 9), Box(200, 0, 1, 2, 3, 4, 5) },
                new() { Box(17, 0, 63), Box(18, 0, 1, 62, 63) },
            };

            foreach (var run in runs)
            {
                AssertRoundTrips(8, 6, run);
            }
        }

        /// <summary>
        /// A box may hold the same memento more than once, because the same key added
        /// twice is stored twice -- the choice the rest of the library makes so that
        /// removing once does not remove both.
        /// </summary>
        [TestMethod]
        public void TestRepeatedMementosSurvive()
        {
            AssertRoundTrips(8, 6, new List<KeepsakeBox> { Box(11, 4, 4, 4, 9) });
            AssertRoundTrips(8, 6, new List<KeepsakeBox> { Box(11, 7, 7) });
        }

        /// <summary>
        /// A box longer than the count field can hold in one piece, which is what the
        /// count's escape mechanism exists for.
        /// </summary>
        /// <remarks>
        /// With four-bit mementos the count is written four bits at a time, so a box of
        /// more than fifteen keys needs a second piece and one of more than thirty a
        /// third. This is rare in practice and therefore exactly the sort of thing that
        /// goes untested and then fails on somebody's skewed data.
        /// </remarks>
        [TestMethod]
        public void TestABoxTooLongForASingleCountRoundTrips()
        {
            const int mementoBits = 4;

            foreach (var length in new[] { 16, 17, 32, 33, 60, 100 })
            {
                var mementos = Enumerable.Range(0, length)
                    .Select(i => (ulong)(i % 16))
                    .OrderBy(m => m)
                    .ToArray();

                AssertRoundTrips(8, mementoBits,
                    new List<KeepsakeBox> { Box(5, mementos) });
            }
        }

        /// <summary>
        /// The extreme memento values, which are where a mask that is one bit wrong
        /// shows up.
        /// </summary>
        [TestMethod]
        public void TestTheWidestAndNarrowestValuesSurvive()
        {
            foreach (var mementoBits in new[] { 1, 2, 6, 12 })
            {
                var top = (1UL << mementoBits) - 1;
                AssertRoundTrips(8, mementoBits, new List<KeepsakeBox>
                {
                    Box(1, 0, top),
                    Box(2, top, top, top),
                    Box(3, 0, 0, 0, top),
                });
            }
        }

        /// <summary>
        /// The packed part of a long box spans slot boundaries rather than restarting
        /// in each slot, which is the whole reason it is more compact than one slot
        /// per key.
        /// </summary>
        [TestMethod]
        public void TestALongBoxCostsLessThanASlotPerKey()
        {
            const int fingerprintBits = 8;
            const int mementoBits = 6;
            var codec = new KeepsakeCodec(fingerprintBits, mementoBits);

            var mementos = Enumerable.Range(0, 60).Select(i => (ulong)i).ToArray();
            var slots = codec.Encode(new List<KeepsakeBox> { Box(5, mementos) }).Count;

            Assert.IsTrue(slots < 60,
                $"Sixty keys sharing a block packed into {slots} slots. Sharing one " +
                "fingerprint across them is the point of this encoding; if it costs a " +
                "slot each there is nothing gained over storing them separately.");

            // A slot is 15 bits here and a memento 6, so the packed keys should cost
            // well under half a slot each.
            Assert.IsTrue(slots <= 30,
                $"Sixty keys took {slots} slots, which is more than the packing should " +
                "need.");
        }

        /// <summary>
        /// Boxes are separated by the zero marker alone, not by a decrease in
        /// fingerprint, so a run whose boxes are out of order still reads back
        /// correctly.
        /// </summary>
        /// <remarks>
        /// The paper delimits boxes by *any* decrease in fingerprint, which makes the
        /// increasing order an invariant the encoding depends on. Keying on zero
        /// instead costs nothing -- zero is unusable as a fingerprint either way, since
        /// every age counter carries a set bit -- and buys a codec that cannot be
        /// corrupted by a caller getting the order wrong. The filter still keeps its
        /// runs ordered, so that a lookup can stop early; it simply no longer depends
        /// on that for correctness.
        /// </remarks>
        [TestMethod]
        public void TestBoxesAreSeparatedByTheMarkerRatherThanByOrder()
        {
            var descending = new List<KeepsakeBox> { Box(9, 1), Box(2, 3) };
            AssertRoundTrips(8, 6, descending);

            var mixed = new List<KeepsakeBox>
            {
                Box(200, 1, 2, 3), Box(5, 9), Box(60, 0, 63),
            };
            AssertRoundTrips(8, 6, mixed);
        }

        /// <summary>
        /// Two boxes that happen to share a fingerprint are read back as one box
        /// holding both their keys, which is the same thing said differently.
        /// </summary>
        [TestMethod]
        public void TestBoxesSharingAFingerprintMergeHarmlessly()
        {
            var codec = new KeepsakeCodec(8, 6);
            var separate = new List<KeepsakeBox> { Box(7, 1), Box(7, 4) };

            var back = codec.Decode(codec.Encode(separate));

            Assert.AreEqual(1, back.Count,
                "Two boxes with one fingerprint are indistinguishable from one box " +
                "with both keys, because a fingerprint is all that identifies a box.");
            CollectionAssert.AreEqual(new List<ulong> { 1, 4 }, back[0].Mementos,
                "Merging them must not lose a key: both positions still answer.");
        }
    }
}
