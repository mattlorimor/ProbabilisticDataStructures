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
    /// A Sublime sketch's total count at the top of its range.
    /// </summary>
    /// <remarks>
    /// The total is a <c>ulong</c>, and an add incremented it with nothing at the top, so
    /// an add to a sketch holding <c>ulong.MaxValue</c> keys took <c>TotalCount</c> to
    /// nought, <c>Epsilon</c> with it, and the schedule of doublings back to its start.
    /// The counters were not affected; they stop at the largest count already.
    /// <para>
    /// No stream gets a sketch there in one lifetime, and a payload whose total does not
    /// fit the rest of it is refused (see <c>TestPersistenceHostilePayloads</c>). But the
    /// count at which the rows next double is worked out as (width * size factor) to the
    /// power 1 / growth, and where that is 2^64 or more it is held as
    /// <c>ulong.MaxValue</c>. A sketch that grows slowly has such a count from the start,
    /// so its total can lawfully be anything, and a payload of one with its total edited
    /// to the top is a sketch that has seen that many keys. The sketch here is one: 64
    /// counters at a size factor of 1 and a growth of 0.09 double at 64^(1/0.09), about
    /// 2^66.7 keys.
    /// </para>
    /// <para>
    /// That count stands for a doubling past any count a <c>ulong</c> holds, so it is
    /// never reached: the add that took such a sketch's total to <c>ulong.MaxValue</c>
    /// doubled its rows, and so did every add after a removal there, because the rows
    /// twice as wide double at the same held count and the total meets it again.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestSublimeTotalCount
    {
        private const int PayloadStart = 14;

        /// <summary>
        /// Three f64, the u32 row count and the u32 width, then the u64 total and the u64
        /// count at which the rows next double.
        /// </summary>
        private const int TotalAt = 32;
        private const int DoublingAt = 40;

        private static byte[] Key(int i) => Encoding.UTF8.GetBytes("k" + i);

        /// <summary>
        /// The slow sketch above, read from a payload whose total is
        /// <paramref name="total"/>: one field edited, and the checksum repaired, so that
        /// what reads it is the reader.
        /// </summary>
        private static SublimeCountMinSketch SlowSketchHolding(ulong total)
        {
            var sketch = new SublimeCountMinSketch(0.5, 0.09, 1.0);
            for (var i = 0; i < 10; i++)
            {
                sketch.Add(Key(i));
            }

            var bytes = sketch.ToByteArray();
            Assert.AreEqual(64, sketch.Width);
            Assert.AreEqual(ulong.MaxValue,
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(PayloadStart + DoublingAt)),
                "the sketch should double at a count past any ulong, held at the largest");

            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(PayloadStart + TotalAt), total);
            var crc = new Crc32();
            crc.Append(bytes.AsSpan(4, bytes.Length - 8));
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(bytes.Length - 4), crc.GetCurrentHashAsUInt32());

            return SublimeCountMinSketch.ReadFrom(new MemoryStream(bytes));
        }

        /// <summary>
        /// An add to a sketch holding the largest total leaves it there, and the one below
        /// it takes its last step up: the total does not wrap to nought, nor the expected
        /// error with it.
        /// </summary>
        [TestMethod]
        public void TestATotalAtTheLargestCountStaysThere()
        {
            var sketch = SlowSketchHolding(ulong.MaxValue - 1);

            for (var add = 1; add <= 3; add++)
            {
                sketch.Add(Key(100 + add));
                Assert.AreEqual(ulong.MaxValue, sketch.TotalCount(), $"the total after add {add}");
                Assert.AreEqual(Math.E * ulong.MaxValue / sketch.Width, sketch.Epsilon(),
                    $"the expected error after add {add}");
            }
        }

        /// <summary>
        /// A doubling due at a count past any <c>ulong</c> never comes: not at the add that
        /// takes the total to the largest count, and not at each add that takes it back
        /// there after a removal.
        /// </summary>
        [TestMethod]
        public void TestADoublingPastTheLargestCountNeverComes()
        {
            var sketch = SlowSketchHolding(ulong.MaxValue - 1);

            sketch.Add(Key(1));
            Assert.AreEqual(64, sketch.Width,
                "the add that took the total to the largest count doubled the rows");

            for (var pair = 1; pair <= 5; pair++)
            {
                sketch.Remove(Key(1));
                sketch.Add(Key(1));
                Assert.AreEqual(64, sketch.Width,
                    $"a removal and an add at the largest total doubled the rows, at pair {pair}");
            }

            Assert.AreEqual(ulong.MaxValue, sketch.TotalCount());
        }

        /// <summary>
        /// The refusal to widen a row past the widest is for an add that would double the
        /// rows, and the add that takes the total to the largest count doubles nothing, so
        /// at the widest row it is taken. The refusal and the doubling have to agree on
        /// which adds double: a refusal that worked it out for itself refused this add for
        /// a doubling that would not have come.
        /// </summary>
        [TestMethod]
        public void TestTheAddToTheLargestTotalIsTakenAtTheWidestRow()
        {
            var sketch = SlowSketchHolding(ulong.MaxValue - 1);
            sketch.WidthLimit = sketch.Width;

            sketch.Add(Key(1));

            Assert.AreEqual(ulong.MaxValue, sketch.TotalCount());
            Assert.AreEqual(64, sketch.Width);
        }

        /// <summary>
        /// The state the tests above leave a sketch in, its total at the largest count
        /// and its next doubling there too, is one a sketch can be in, so it reads, and
        /// writes back the bytes it was read from.
        /// </summary>
        [TestMethod]
        public void TestASketchHoldingTheLargestTotalReadsBack()
        {
            var sketch = SlowSketchHolding(ulong.MaxValue);
            Assert.AreEqual(ulong.MaxValue, sketch.TotalCount());

            var bytes = sketch.ToByteArray();
            var back = SublimeCountMinSketch.ReadFrom(new MemoryStream(bytes));
            Assert.AreEqual(ulong.MaxValue, back.TotalCount());
            CollectionAssert.AreEqual(bytes, back.ToByteArray());
        }
    }
}
