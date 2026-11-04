using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The size of a DDSketch's payload, which the store worked out in an int.
    /// </summary>
    /// <remarks>
    /// <c>Store.WriteTo</c> copied its run of buckets into a byte array sized
    /// <c>span * 8</c> in an <c>int</c>, so a run of 2^28 buckets or more -- two
    /// gibibytes of counts -- made a negative length, and the write failed with an
    /// <see cref="OverflowException"/> from the allocation; from 2^29 the product wrapped to
    /// something small and positive, and the loop that filled it ran off the end instead.
    /// Such a sketch can be built and used: the constructor allows a run of
    /// <see cref="Array.MaxLength"/> buckets, 16 GiB of counts. It cannot be made a byte
    /// array, since its payload would not fit one, and from 2^28 buckets of one sign it
    /// cannot be written at all yet, since that sign's run is longer than any run a payload
    /// holds; both are things the payload writer says clearly rather than as an overflow.
    /// A run that long is not something a test builds -- the smallest unwritable one is
    /// two gibibytes of counts, and holding it in memory is the cost of building it -- so the
    /// write path is run on a sketch whose store has been told it spans more than it holds,
    /// which the write must refuse before it looks at what is there. The size it refuses
    /// by is the count its own <c>WriteTo</c> makes of the payload (#216), not a formula
    /// kept beside the layout.
    /// </remarks>
    [TestClass]
    public class TestDDSketchPayloadSize
    {
        /// <summary>
        /// The most a payload can hold, restated: the largest array less the envelope's
        /// header and checksum.
        /// </summary>
        private const long LargestPayload = 2_147_483_591L - 14 - 4;

        /// <summary>
        /// A payload is five eight-byte fields, then two stores of a four-byte index of
        /// the first bucket, a four-byte length and a count of eight bytes for each
        /// bucket in the run. Restated from FORMAT.md, not read from the code.
        /// </summary>
        private const long Fixed = 5 * 8 + 2 * (4 + 4);

        /// <summary>
        /// The most buckets, between both signs, that a payload holds.
        /// </summary>
        private static readonly long LargestRun = (LargestPayload - Fixed) / 8;

        /// <summary>
        /// A run's bytes are its buckets times eight, in 64 bits. The rows from 2^28
        /// buckets up are the ones that wrapped: the first of them is exactly 2^31 bytes,
        /// 2^29 made zero, and the largest run an array holds made a number that is
        /// nothing like it.
        /// </summary>
        [TestMethod]
        [DataRow(1, 8L)]
        [DataRow(1_000, 8_000L)]
        [DataRow(268_435_455, 2_147_483_640L)]
        [DataRow(268_435_456, 2_147_483_648L)]
        [DataRow(268_435_457, 2_147_483_656L)]
        [DataRow(536_870_912, 4_294_967_296L)]
        [DataRow(536_870_922, 4_294_967_376L)]
        [DataRow(2_147_483_591, 17_179_868_728L)]
        public void TestARunsBytesAreCountedInSixtyFourBits(int span, long bytes)
        {
            Assert.AreEqual(bytes, DDSketch.BytesOf(span));
        }

        /// <summary>
        /// The length a write counts its payload at is the length it then writes, and the
        /// layout FORMAT.md gives. The sketches are small enough to write, and the count is
        /// what the large ones are refused by, so this is what ties the refusal to the
        /// layout. It is watched as <c>WriteTo</c> makes it, so a count the write did not
        /// make would not vouch for it.
        /// </summary>
        [TestMethod]
        public void TestThePayloadCountedIsWhatASketchWrites()
        {
            const double accuracy = 0.01;
            var logGamma = Math.Log((1 + accuracy) / (1 - accuracy));

            // The bucket a value is in is the one whose upper bound is the first power of
            // gamma at or above it, so a run from 1 to 1,000 is this many buckets, and a
            // lone value is one.
            var thousand = (long)Math.Ceiling(Math.Log(1000.0) / logGamma) + 1;
            var tiny = (long)Math.Ceiling(Math.Log(0.001) / logGamma);
            var huge = (long)Math.Ceiling(Math.Log(1e9) / logGamma);
            Assert.IsGreaterThan(100L, thousand, "a run of buckets, not one");
            Assert.IsLessThan(0L, tiny, "a negative index is an ordinary one");

            var nothing = new DDSketch(accuracy);

            var zeros = new DDSketch(accuracy);
            zeros.Add(0.0);

            var oneSide = new DDSketch(accuracy);
            oneSide.Add(1.0);

            var run = new DDSketch(accuracy);
            run.Add(1.0);
            run.Add(1000.0);

            var both = new DDSketch(accuracy);
            both.Add(1.0);
            both.Add(1000.0);
            both.Add(-0.001);
            both.Add(-1e9);

            foreach (var (sketch, buckets) in new[]
            {
                (nothing, 0L), (zeros, 0L), (oneSide, 1L), (run, thousand),
                (both, thousand + huge - tiny + 1),
            })
            {
                var (counted, written) = CountedAndWritten(sketch);
                Assert.AreEqual(Fixed + 8 * buckets, written, $"{buckets} buckets written");
                Assert.AreEqual(written, counted, $"{buckets} buckets counted");
            }
        }

        /// <summary>
        /// A sketch whose payload would pass the longest an envelope holds is refused
        /// before anything is allocated for it, with the writer's exception: the size it
        /// would have been and the size one can be. The largest run that fits is counted
        /// within the ceiling, and the two signs share the one payload, so it is the sum of
        /// their runs that counts: each of two halves of a run that fits is no reason to
        /// refuse, and each of two halves of a run that does not is no reason to accept.
        /// </summary>
        /// <remarks>
        /// Each sketch holds a value of each sign its row needs and is told that value's run
        /// is as long as the row says. A run that fits is two gibibytes, which a write would
        /// build, so it is counted, as the write counts it, and held to the ceiling the write
        /// holds the count to; a run that does not is written, and refused.
        /// </remarks>
        [TestMethod]
        public void TestASketchTooLargeToWriteIsRefusedNamingTheSize()
        {
            Assert.AreEqual(268_435_439L, LargestRun, "the figure the rows below are built on");

            foreach (var (positive, negative) in new[]
            {
                (LargestRun, 0L),
                (0L, LargestRun),
                (LargestRun / 2, LargestRun - LargestRun / 2),
                (0L, 0L),
                (1L, 1L),
            })
            {
                var sketch = Claiming(positive, negative, out var shape);
                foreach (var (store, member, claimed) in shape)
                {
                    store.GetType().GetProperty(member, BindingFlags.NonPublic | BindingFlags.Instance)!
                        .SetValue(store, claimed);
                }

                var counter = PayloadWriter.Counter();
                ((IPayloadBody)sketch).WritePayload(counter);
                Assert.AreEqual(Fixed + 8 * (positive + negative), counter.Length,
                    $"runs of {positive} and {negative} buckets counted");
                PersistenceFormat.CheckPayloadLength(counter.Length);
            }

            // A sign's run longer than one array is no reason to refuse (#228): the payload is
            // past one array too, and ToByteArray refuses it for that, naming it; WriteTo would
            // write it in the large form, and is not asked, since its body would write the run
            // it was told of from the counts the sketch holds.
            foreach (var (positive, negative) in new[]
            {
                (LargestRun + 1, 0L),
                (0L, LargestRun + 1),
                (LargestRun / 2 + 1, LargestRun - LargestRun / 2),
                (LargestRun - LargestRun / 2, LargestRun / 2 + 1),
                (150_000_000L, 150_000_000L),
                ((long)Array.MaxLength, 0L),
                ((long)Array.MaxLength, (long)Array.MaxLength),
            })
            {
                var sketch = Claiming(positive, negative, out var shape);
                TestPayloadCount.AssertStreamedWhileClaiming(sketch, Fixed + 8 * (positive + negative), null, shape);
            }
        }

        /// <summary>
        /// A sign's run past one array is counted for the large form, in 64 bits, and not
        /// overflowed. A sketch holding one value is told its run is longer than it is, which
        /// is as much as a test can do of a run no suite can afford: its <c>ToByteArray</c>
        /// refuses it, whichever sign is long, naming its payload in the ordinary envelope,
        /// while its count declares the large form's, both signs' runs with a <c>u64</c>
        /// length (#228). It does not look at the counts to do it -- there are 64 of them --
        /// so a count that tried would fail some other way, as the old arithmetic did: an
        /// overflow at 300,000,000 buckets, and a loop off the end of the array it sized for
        /// 2^29. <c>WriteTo</c> is not asked: in the large form it would write the run it was
        /// told of.
        /// </summary>
        [TestMethod]
        [DataRow("positive", 300_000_000)]
        [DataRow("negative", 300_000_000)]
        [DataRow("positive", 536_870_912)]
        [DataRow("negative", 536_870_912)]
        public void TestARunPastOneArrayIsCountedForTheLargeFormNotOverflowed(string side, int span)
        {
            var sketch = new DDSketch(0.01);
            var value = side == "positive" ? 1.0 : -1.0;
            sketch.Add(value);
            var before = Written(sketch);

            // Index 0 is the only bucket the value has, so this makes the run [0, span).
            SetMaxIndex(sketch, side, span - 1);
            var counted = new List<long>();
            PersistenceFormat.Counted = (body, length) =>
            {
                if (ReferenceEquals(body, sketch)) counted.Add(length);
            };

            try
            {
                var ex = Assert.ThrowsExactly<InvalidOperationException>(() => sketch.ToByteArray());
                StringAssert.Contains(ex.Message, (Fixed + 8L * span).ToString("N0"), ex.Message);
            }
            finally
            {
                PersistenceFormat.Counted = null;
                SetMaxIndex(sketch, side, 0);
            }

            Assert.HasCount(1, counted, "the byte array's write did not count its payload, once");
            Assert.AreEqual(Fixed + 8L * span + 8, counted[0],
                "the count is not the large form's, with the lengths of both signs' runs a u64");

            // The sketch is as it was: told its real run again, it writes what it wrote.
            CollectionAssert.AreEqual(before, Written(sketch));
            Assert.AreEqual(1UL, sketch.Count());
        }

        /// <summary>
        /// The reader never multiplies: the number of buckets it allocates for is the
        /// number of bytes it was given divided by eight, and the run's length is checked
        /// against the payload before anything is allocated for it. So a run claimed at
        /// the longest an array holds, and at the longest a payload holds, with no bytes
        /// behind it, is refused for the bytes it lacks, and not as an overflow or a size.
        /// This passes before the fix as well; it says the read side needed none.
        /// </summary>
        [TestMethod]
        [DataRow(800u)]
        [DataRow(2_147_483_512u)]
        [DataRow(2_147_483_584u)]
        public void TestAReadOfARunTheStoreLacksFailsForTheBytesItLacks(uint claimed)
        {
            var payload = new PayloadWriter();
            payload.WriteDouble(0.01);
            payload.WriteUInt64(0);
            payload.WriteUInt64(0);
            payload.WriteDouble(0.0);
            payload.WriteDouble(0.0);
            payload.WriteUInt32(0);
            payload.WriteUInt32(claimed);

            using var stream = new MemoryStream();
            PersistenceFormat.Write(stream, StructureId.DDSketch, HashId.None, payload.WrittenSpan);

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => DDSketch.ReadFrom(new MemoryStream(stream.ToArray()), ReadOptions.Unlimited));
            StringAssert.Contains(ex.Message, $"while reading {claimed} more");
        }

        private static byte[] Written(DDSketch sketch)
        {
            using var stream = new MemoryStream();
            sketch.WriteTo(stream);
            return stream.ToArray();
        }

        /// <summary>
        /// The length the sketch's <c>WriteTo</c> counts its payload at, and the length of
        /// the payload it writes.
        /// </summary>
        private static (long Counted, long Written) CountedAndWritten(DDSketch sketch)
        {
            var counted = new List<long>();
            byte[] bytes;
            PersistenceFormat.Counted = (body, length) =>
            {
                if (ReferenceEquals(body, sketch)) counted.Add(length);
            };
            try
            {
                bytes = Written(sketch);
            }
            finally
            {
                PersistenceFormat.Counted = null;
            }

            Assert.HasCount(1, counted, "the write did not count its payload, once");
            return (counted[0], bytes.Length - 18);
        }

        /// <summary>
        /// A sketch with a value at index 0 of each sign whose run is to be longer than
        /// nothing, and the last index of each such run to tell its store, for the
        /// sketch to claim runs of these lengths. With no shape claimed, a store holding a
        /// value spans the one bucket.
        /// </summary>
        private static DDSketch Claiming(
            long positive, long negative, out (object Owner, string Member, object Claimed)[] shape)
        {
            var sketch = new DDSketch(0.01);
            var claimed = new List<(object, string, object)>();
            foreach (var (side, span, value) in new[] { ("positive", positive, 1.0), ("negative", negative, -1.0) })
            {
                if (span == 0)
                {
                    continue;
                }

                sketch.Add(value);
                claimed.Add((StoreOf(sketch, side), "MaxIndex", checked((int)(span - 1))));
            }

            shape = claimed.ToArray();
            return sketch;
        }

        private static object StoreOf(DDSketch sketch, string side) =>
            typeof(DDSketch).GetField(side, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sketch)!;

        /// <summary>
        /// Tells one of the sketch's stores that its last bucket is a different one.
        /// </summary>
        private static void SetMaxIndex(DDSketch sketch, string side, int index)
        {
            var store = typeof(DDSketch)
                .GetField(side, BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(sketch)!;
            store.GetType()
                .GetProperty("MaxIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(store, index);
        }
    }
}
