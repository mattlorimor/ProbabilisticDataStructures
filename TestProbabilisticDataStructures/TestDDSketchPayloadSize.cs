using System;
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
    /// <see cref="Array.MaxLength"/> buckets, 16 GiB of counts. It cannot be written,
    /// since its payload would not fit an envelope, and that is a thing the payload writer
    /// says clearly (see <see cref="TestPayloadWriterGrowth"/>) rather than as an overflow.
    /// A run that long is not something a test builds -- the smallest unwritable one is
    /// two gibibytes of counts, and holding it in memory is the cost of building it -- so the
    /// arithmetic that sizes it is reached directly, and the write path through it is run
    /// on a sketch whose store has been told it spans more than it holds, which the write
    /// must refuse before it looks at what is there.
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
        /// The length the hook gives a payload is the length a sketch really writes. The
        /// sketches are small enough to write, and the hook is the arithmetic the large
        /// ones are refused by, so this is what ties its constants to the layout rather
        /// than to each other. It passes before the fix as well.
        /// </summary>
        [TestMethod]
        public void TestThePayloadLengthIsWhatASketchWrites()
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

            Assert.AreEqual(Fixed, WrittenPayload(nothing));
            Assert.AreEqual(DDSketch.PayloadLength(0, 0), WrittenPayload(nothing));
            Assert.AreEqual(DDSketch.PayloadLength(0, 0), WrittenPayload(zeros));
            Assert.AreEqual(DDSketch.PayloadLength(1, 0), WrittenPayload(oneSide));
            Assert.AreEqual(Fixed + 8, WrittenPayload(oneSide));
            Assert.AreEqual(DDSketch.PayloadLength(thousand, 0), WrittenPayload(run));
            Assert.AreEqual(Fixed + 8 * thousand, WrittenPayload(run));
            Assert.AreEqual(DDSketch.PayloadLength(thousand, huge - tiny + 1), WrittenPayload(both));
            Assert.AreEqual(Fixed + 8 * (thousand + huge - tiny + 1), WrittenPayload(both));
        }

        /// <summary>
        /// A sketch whose payload would pass the longest an envelope holds is refused
        /// before anything is allocated for it, with the writer's exception: the size it
        /// would have been and the size one can be. The largest run that fits is not, and
        /// the two signs share the one payload, so it is the sum of their runs that
        /// counts: each of two halves of a run that fits is no reason to refuse, and each
        /// of two halves of a run that does not is no reason to accept.
        /// </summary>
        [TestMethod]
        public void TestASketchTooLargeToWriteIsRefusedNamingTheSize()
        {
            Assert.AreEqual(268_435_439L, LargestRun, "the figure the rows below are built on");

            DDSketch.CheckWritable(LargestRun, 0);
            DDSketch.CheckWritable(0, LargestRun);
            DDSketch.CheckWritable(LargestRun / 2, LargestRun - LargestRun / 2);
            DDSketch.CheckWritable(0, 0);
            DDSketch.CheckWritable(1, 1);

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
                var would = Fixed + 8 * (positive + negative);
                var ex = Assert.ThrowsExactly<InvalidOperationException>(
                    () => DDSketch.CheckWritable(positive, negative),
                    $"runs of {positive} and {negative} buckets");
                StringAssert.Contains(ex.Message, would.ToString("N0"),
                    "the refusal should say how large the payload would have been");
                StringAssert.Contains(ex.Message, LargestPayload.ToString("N0"),
                    "the refusal should say how large a payload can be");
            }
        }

        /// <summary>
        /// The refusal is the write's own and not only the hook's. A sketch holding one
        /// value is told its run is longer than it is, which is as much as a test can do
        /// of a run no suite can afford, and then asked to write itself: it must refuse,
        /// whichever sign is long, with the writer's exception and nothing on the stream.
        /// It does not look at the counts to do it -- there are 64 of them -- so a write
        /// that tried would fail some other way, as the old arithmetic did: an overflow
        /// at 300,000,000 buckets, and a loop off the end of the array it sized for 2^29.
        /// </summary>
        [TestMethod]
        [DataRow("positive", 300_000_000)]
        [DataRow("negative", 300_000_000)]
        [DataRow("positive", 536_870_912)]
        [DataRow("negative", 536_870_912)]
        public void TestAWriteOfARunNoEnvelopeHoldsIsRefusedNotOverflowed(string side, int span)
        {
            var sketch = new DDSketch(0.01);
            var value = side == "positive" ? 1.0 : -1.0;
            sketch.Add(value);
            var before = Written(sketch);

            // Index 0 is the only bucket the value has, so this makes the run [0, span).
            SetMaxIndex(sketch, side, span - 1);

            using var stream = new MemoryStream();
            var ex = Assert.ThrowsExactly<InvalidOperationException>(() => sketch.WriteTo(stream));
            var would = Fixed + 8L * span;
            StringAssert.Contains(ex.Message, would.ToString("N0"));
            StringAssert.Contains(ex.Message, LargestPayload.ToString("N0"));
            Assert.AreEqual(0L, stream.Length, "nothing should have been written");

            // The sketch is as it was: told its real run again, it writes what it wrote.
            SetMaxIndex(sketch, side, 0);
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

        private static long WrittenPayload(DDSketch sketch) => Written(sketch).Length - 18;

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
