using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// How wide a row of a Sublime sketch can be, and what happens at the edge.
    /// </summary>
    /// <remarks>
    /// A row is a <see cref="ValeCounterArray"/> of 2^k counters, cut into chunks of one
    /// cache line, and a counter is found by the bit its chunk begins at. That bit was
    /// <c>chunk * 512</c> in an <c>int</c>, which wraps at 2^22 chunks, 285 million
    /// counters at the default tuning: any row wider than that read and wrote the
    /// wrong place for the counters in its upper part, and threw for most of them.
    /// Measured on the tree this was written against, a sketch given a size factor of
    /// 1e-9 -- a row starts at 2^30 counters, 963 MiB -- threw IndexOutOfRangeException
    /// from its first <c>Add</c>, and from <c>Count</c> as well, for a key whose hash
    /// chose a counter in the upper half. The width the constructor clamps to, and the
    /// reader accepts, is 2^30: the largest power of two an int holds, and the width
    /// property is an int. So the structure works up to it, once the offset is 64 bits,
    /// and growth past it has to be refused.
    /// <para>
    /// A row of a billion counters is not something a test builds, so what is checked
    /// here is the arithmetic that locates a chunk, the choice of a starting width, and
    /// the refusal to grow, which takes a width limit a test can lower.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestSublimeWidth
    {
        private const int PayloadStart = 14;

        private static byte[] Key(int i) => Encoding.UTF8.GetBytes("k" + i);

        /// <summary>
        /// A chunk begins at its index times 512 bits. Past the chunk that begins at 2^31
        /// that does not fit an int, and the rows from 4,194,304 are the ones that wrapped.
        /// The chunk at 8,388,608, counter 570,425,344 at the default tuning, begins at 2^32
        /// and wrapped to the first bit of the array: a write to it landed on counter 0.
        /// </summary>
        [TestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(4_194_303)]
        [DataRow(4_194_304)]
        [DataRow(8_388_608)]
        [DataRow(15_798_135)]
        [DataRow(67_108_863)]
        public void TestAChunksFirstBitIsCountedInSixtyFourBits(int chunk)
        {
            Assert.AreEqual(chunk * 512L, ValeCounterArray.ChunkStart(chunk));
        }

        /// <summary>
        /// The widest row is 2^30 counters at the narrowest chunk, 67,108,864 chunks, and
        /// the bit its last chunk begins at is 34 billion.
        /// </summary>
        [TestMethod]
        public void TestTheWidestRowsLastChunkIsAddressable()
        {
            var chunks = (long)SublimeCountMinSketch.MaxWidth / ValeCounterArray.MinCountersPerChunk;
            Assert.AreEqual(67_108_864L, chunks);
            Assert.AreEqual(34_359_737_856L, ValeCounterArray.ChunkStart((int)chunks - 1));
        }

        /// <summary>
        /// A row starts at a chunk's worth of counters over the size factor, rounded down to
        /// a power of two. The largest a row can be is 2^30, so a size factor that asks for
        /// 2^31 or more is not one the sketch can honour, and the constructor silently
        /// started at 2^30 instead -- a gibibyte for each row, for a sketch the caller
        /// believed was larger and which then could not be used. It is refused against the
        /// parameter that chose it. Just under 2^31 rounds down to 2^30, which is allowed.
        /// </summary>
        [TestMethod]
        public void TestAStartingWidthPastTheWidestRowIsRefusedNamingTheSizeFactor()
        {
            // 64 counters over 2^-25 is 2^31 exactly.
            var tooSmall = Math.ScaleB(1.0, -25);
            var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => SublimeCountMinSketch.StartingWidth(64, tooSmall));
            Assert.AreEqual("sizeFactor", ex.ParamName);

            Assert.AreEqual(1 << 30, SublimeCountMinSketch.StartingWidth(64, tooSmall * 1.0000001));
            Assert.AreEqual(1 << 30, SublimeCountMinSketch.StartingWidth(64, Math.ScaleB(1.0, -24)));
            Assert.AreEqual(64, SublimeCountMinSketch.StartingWidth(68, 1.0));

            // And through the constructor, which asks the same question before it allocates.
            Assert.AreEqual("sizeFactor", Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new SublimeCountMinSketch(0.5, 0.5, 1e-9)).ParamName);
        }

        /// <summary>
        /// A sketch whose rows cannot get any wider refuses the add that would have
        /// widened them, before it changes anything: not the counters, not the count, not
        /// the width, and not the record a widening keeps. The limit is lowered to the
        /// starting width so the refusal is reached in the 4,096th add; the sketch's own
        /// limit is 2^30.
        /// </summary>
        [TestMethod]
        [DataRow(64, 4_096)]
        [DataRow(128, 16_384)]
        public void TestAGrowthTheRowCannotMakeIsRefusedBeforeAnythingChanges(
            int widthLimit, int refusedAt)
        {
            // A size factor of 1 starts at 64 counters, and 0.5 growth doubles at width^2.
            var sketch = new SublimeCountMinSketch(0.5, 0.5, 1.0) { WidthLimit = widthLimit };

            var added = 0;
            while (added < refusedAt - 1)
            {
                sketch.Add(Key(added % 500));
                added++;
            }

            Assert.AreEqual(widthLimit, sketch.Width, "the sketch is not at the width the limit allows");
            var before = sketch.ToByteArray();
            var total = sketch.TotalCount();

            for (var attempt = 0; attempt < 3; attempt++)
            {
                var ex = Assert.ThrowsExactly<InvalidOperationException>(
                    () => sketch.Add(Key(7)));
                StringAssert.Contains(ex.Message, widthLimit.ToString("N0"),
                    "the refusal should name the width the row is at");
            }

            Assert.AreEqual(total, sketch.TotalCount());
            Assert.AreEqual(widthLimit, sketch.Width);
            CollectionAssert.AreEqual(before, sketch.ToByteArray(),
                "a refused add changed the sketch");
        }

        /// <summary>
        /// The refusal at the real limit. A row of 2^30 counters is a gibibyte, but the
        /// refusal is reached before any row is touched, so a sketch told that its rows are
        /// that wide, and that its next add is the one that widens them, takes it to the
        /// check without building them. The product it makes is the one that wrapped:
        /// 2^30 doubled does not fit an int, and a check made in an int found the doubled
        /// width negative and let the add through to the expansion that overflowed.
        /// </summary>
        [TestMethod]
        public void TestARowAtTheWidestCannotDouble()
        {
            var sketch = new SublimeCountMinSketch(0.5, 0.5, 1.0);
            SetField(sketch, "width", SublimeCountMinSketch.MaxWidth);
            SetField(sketch, "expansionLimit", 1UL);

            var ex = Assert.ThrowsExactly<InvalidOperationException>(() => sketch.Add(Key(1)));
            StringAssert.Contains(ex.Message, SublimeCountMinSketch.MaxWidth.ToString("N0"));
            StringAssert.Contains(ex.Message, (2L * SublimeCountMinSketch.MaxWidth).ToString("N0"));
            Assert.AreEqual(0UL, sketch.TotalCount());
        }

        private static void SetField(object target, string name, object value)
        {
            var field = target.GetType().GetField(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"the sketch has no field {name}");
            field.SetValue(target, value);
        }

        /// <summary>
        /// The refusal is for the add that would widen the row and for none before it:
        /// a sketch one add short of the width it cannot pass takes that add.
        /// </summary>
        [TestMethod]
        public void TestTheAddJustBeforeTheRefusalIsTaken()
        {
            var sketch = new SublimeCountMinSketch(0.5, 0.5, 1.0) { WidthLimit = 64 };
            for (var i = 0; i < 4_095; i++)
            {
                sketch.Add(Key(i % 500));
            }

            Assert.AreEqual(4_095UL, sketch.TotalCount());
            Assert.AreEqual(64, sketch.Width);
        }

        /// <summary>
        /// A width past 2^30 is a power of two and was refused with a message that says it
        /// is not one. It names the limit now. At the limit itself the reader gets as far as
        /// the memory it would need, which the default budget refuses -- eight gigabytes of
        /// counts for one row -- so it is refused for its cost and not its size.
        /// </summary>
        [TestMethod]
        public void TestAWidthPastTheLimitIsRefusedNamingTheLimit()
        {
            // f64 delta, f64 growth exponent, f64 size factor, u32 rows, then the width.
            const int WidthOffset = 28;

            var bytes = new SublimeCountMinSketch(0.5).ToByteArray();

            var past = PokeUInt32(bytes, WidthOffset, 1u << 31);
            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => SublimeCountMinSketch.ReadFrom(new MemoryStream(past)));
            StringAssert.Contains(ex.Message, "no larger than");

            var atTheLimit = PokeUInt32(bytes, WidthOffset, 1u << 30);
            var refused = Assert.ThrowsExactly<InvalidDataException>(
                () => SublimeCountMinSketch.ReadFrom(new MemoryStream(atTheLimit)));
            Assert.DoesNotContain("no larger than", refused.Message);
        }

        private static byte[] PokeUInt32(byte[] original, int payloadOffset, uint value)
        {
            var bytes = (byte[])original.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(PayloadStart + payloadOffset), value);

            var crc = new Crc32();
            crc.Append(bytes.AsSpan(4, bytes.Length - 8));
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(bytes.Length - 4), crc.GetCurrentHashAsUInt32());
            return bytes;
        }
    }
}
