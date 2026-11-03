using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

            // The check the reader runs on every run passes every run the codec writes.
            codec.Check(fields);

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

        /// <summary>
        /// The slots of one box of more than two keys, laid out by hand from the layout the
        /// codec documents: the fingerprint beside the smallest key, a zero fingerprint beside
        /// the largest, then the given values packed end to end across fields of
        /// <c>fingerprint + 1 + memento</c> bits, from the low bit of the first up. The values
        /// are the count, in memento-sized pieces, and the keys between.
        /// </summary>
        private static List<ulong> LongBox(
            int fingerprintBits, int mementoBits, ulong fingerprint, ulong smallest, ulong largest,
            params (ulong Value, int Bits)[] packed)
        {
            var fieldBits = fingerprintBits + 1 + mementoBits;
            var words = new List<ulong>();
            var position = 0L;
            foreach (var (value, bits) in packed)
            {
                for (var i = 0; i < bits; i++, position++)
                {
                    var word = (int)(position / fieldBits);
                    while (words.Count <= word)
                    {
                        words.Add(0);
                    }
                    words[word] |= ((value >> i) & 1) << (int)(position % fieldBits);
                }
            }

            var fields = new List<ulong> { (fingerprint << mementoBits) | smallest, largest };
            fields.AddRange(words);
            return fields;
        }

        /// <summary>
        /// Asserts that a run is refused, by both the decoder and the check the reader runs,
        /// for the reason named; and if the decoder does not refuse it, says what it decoded.
        /// </summary>
        private static void AssertRefused(KeepsakeCodec codec, List<ulong> fields, string because)
        {
            List<KeepsakeBox> boxes;
            try
            {
                boxes = codec.Decode(fields);
            }
            catch (InvalidDataException e)
            {
                StringAssert.Contains(e.Message, because);
                var check = Assert.ThrowsExactly<InvalidDataException>(() => codec.Check(fields),
                    "the decoder refuses this run and the reader's check lets it through");
                Assert.AreEqual(e.Message, check.Message, "the check refuses this run for another reason");
                return;
            }

            Assert.Fail($"A run of {fields.Count} slots that should be refused ({because}) decoded as " +
                $"{boxes.Count} box(es) holding {string.Join(", ", boxes.Select(b => b.Mementos.Count))} " +
                $"keys, {boxes.Sum(b => b.Mementos.Count(m => m == 0))} of them zero.");
        }

        /// <summary>
        /// The hand layout is the codec's: a box of four keys written by hand and by the codec
        /// are the same slots, at two memento widths. Without this the refusals below could be
        /// of runs the codec would never write for some other reason than the one named.
        /// </summary>
        [TestMethod]
        public void TestALongBoxLaidOutByHandIsTheOneTheCodecWrites()
        {
            var codec = new KeepsakeCodec(8, 6);
            CollectionAssert.AreEqual(
                codec.Encode(new List<KeepsakeBox> { Box(5, 1, 3, 4, 9) }),
                LongBox(8, 6, 5, 1, 9, (2, 6), (3, 6), (4, 6)));

            var wide = new KeepsakeCodec(8, 32);
            CollectionAssert.AreEqual(
                wide.Encode(new List<KeepsakeBox> { Box(5, 7, 8, 9) }),
                LongBox(8, 32, 5, 7, 9, (1, 32), (8, 32)));
            Assert.AreEqual(3, wide.Decode(LongBox(8, 32, 5, 7, 9, (1, 32), (8, 32)))[0].Mementos.Count);
        }

        /// <summary>
        /// A count past what an int holds is refused. With 32-bit mementos one piece of the
        /// count reaches 2^32 - 1, which is the escape, so 2^32 - 1 is written as that piece
        /// and a zero one, and a run holding it claims 2^32 - 1 keys between the smallest and
        /// the largest with nothing after the count. The count was cast to an int, which made
        /// it -1, so the box decoded as two keys and the slots after the count were read as the
        /// next box: answers silently wrong, and no exception.
        /// </summary>
        [TestMethod]
        public void TestACountPastWhatAnIntHoldsIsRefused()
        {
            AssertRefused(new KeepsakeCodec(8, 32),
                LongBox(8, 32, 5, 7, 9, (uint.MaxValue, 32), (0, 32)),
                "more keys than its run holds");
        }

        /// <summary>
        /// A count claiming more keys than its run has bits for is refused. Here three thousand,
        /// in one 12-bit piece, in a run of four slots of 21 bits, with room after the count for
        /// two. Reads past the end of the run gave zeros, so it decoded as a box of 3,002 keys,
        /// nearly all of them zero, a pass over the memento for each; the same claim near 2^31
        /// is a list of sixteen gibibytes, at query time.
        /// </summary>
        [TestMethod]
        public void TestACountLongerThanItsRunIsRefusedRatherThanPaddedWithZeros()
        {
            AssertRefused(new KeepsakeCodec(8, 12),
                LongBox(8, 12, 5, 7, 4000, (3000, 12), (100, 12)),
                "more keys than its run holds");

            // One more than the run holds, and exactly what it holds, which reads.
            var codec = new KeepsakeCodec(8, 6);
            AssertRefused(codec, LongBox(8, 6, 5, 1, 9, (2, 6), (3, 6)), "more keys than its run holds");
            Assert.AreEqual(3, codec.Decode(LongBox(8, 6, 5, 1, 9, (1, 6), (3, 6)))[0].Mementos.Count);
        }

        /// <summary>
        /// A box marked as holding more than two keys whose run ends at the marker is refused:
        /// its count would be read from past the end of the run, which gave zeros, so it
        /// decoded silently as a box of two. The codec always writes a count after the marker.
        /// </summary>
        [TestMethod]
        public void TestACountPastTheEndOfItsRunIsRefused()
        {
            var codec = new KeepsakeCodec(8, 6);
            AssertRefused(codec, new List<ulong> { (5UL << 6) | 1, 9 }, "past the end of its run");
            AssertRefused(codec, new List<ulong> { (3UL << 6) | 2, (3UL << 6) | 4, (5UL << 6) | 1, 9 },
                "past the end of its run");
        }

        /// <summary>
        /// The bit position in a packed run is a long. A run's bits are its slots times the
        /// width of a field, and a run as long as a table of 2^31 slots passes an int's worth;
        /// the bound on the count above keeps every read inside the run, so a position that
        /// wrapped could not be reached at a size a test builds.
        /// </summary>
        [TestMethod]
        public void TestThePositionInAPackedRunIsALong()
        {
            var run = typeof(KeepsakeCodec).GetNestedType("BitRun", BindingFlags.NonPublic);
            Assert.IsNotNull(run, "the codec's bit cursor has been renamed; point this test at it");
            var position = run.GetField("position", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(position, "the bit cursor's position has been renamed; point this test at it");
            Assert.AreEqual(typeof(long), position.FieldType);
        }
    }
}
