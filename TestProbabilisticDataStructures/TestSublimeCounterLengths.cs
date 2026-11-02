using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Counters of 2^63 and more, which have a bit length of sixty-four.
    /// </summary>
    /// <remarks>
    /// Tuning a sketch's counters starts by tallying how many of them have each bit
    /// length, and the tally was sixty-four slots wide: lengths nought to sixty-three.
    /// A counter of 2^63 or more has a length of sixty-four, so tallying it indexed past
    /// the end and threw <see cref="IndexOutOfRangeException"/> from the middle of
    /// whatever was tuning -- a read, or an add that widened the rows. The packed arrays
    /// themselves had never been the problem: a <see cref="ValeCounterArray"/> stores a
    /// full <c>ulong</c>, and <c>TestValeCounterArray</c> already sets one to
    /// <c>ulong.MaxValue</c> at the default tuning and at the most crowded one, where
    /// the count goes to a tails array.
    /// <para>
    /// A counter that large needs 2^63 adds, so no caller meets one. It is a state of
    /// the sketch all the same -- counts are <c>ulong</c>, and an add has no ceiling below
    /// 2^64 -- and the writer says that every <c>ulong</c> is a valid count, so the reader
    /// takes any a payload holds. These tests put such counters in by hand, in a payload
    /// and in a sketch's own arrays, because adding 2^63 keys is not something a test does.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestSublimeCounterLengths
    {
        private const int PayloadStart = 14;

        /// <summary>
        /// Where the counters of the current arrays begin in the payload of a sketch that
        /// has never expanded: three doubles, the rows and the width, the three counts,
        /// and the number of records, which is nought.
        /// </summary>
        private const int CountersStart = 3 * 8 + 4 + 4 + 3 * 8 + 4;

        /// <summary>
        /// The counters set heavy in a row of 1024 are every 128th, from the fifth: eight
        /// of them, each in a chunk of its own at any tuning the sketch picks. A few heavy
        /// counters among small ones is the shape of a real sketch, and it is the shape
        /// the tuning prices: a row where a sixty-fourth of the counters are this large
        /// has no tuning that satisfies the bound at all, which is its own test below.
        /// </summary>
        private const int HeavyEvery = 128;
        private const int HeavyFirst = 5;

        private static byte[] Key(int i) => Encoding.UTF8.GetBytes("k" + i);

        /// <summary>
        /// Values either side of the step from a length of sixty-three to sixty-four, and
        /// the two ends of it. 2^62 and 2^63 - 1 are the longest that always worked; the
        /// rest are the ones that threw.
        /// </summary>
        private static readonly ulong[] Heavy =
        {
            1UL << 62, (1UL << 63) - 1, 1UL << 63, (1UL << 63) + 1, ulong.MaxValue - 1,
        };

        /// <summary>
        /// A row of 1024 counters with a heavy one every <see cref="HeavyEvery"/>, taking
        /// its values from <paramref name="heavy"/> in turn, and a small number elsewhere.
        /// </summary>
        private static ulong Sparse(int j, ulong[] heavy) =>
            j % HeavyEvery == HeavyFirst ? heavy[j / HeavyEvery % heavy.Length] : (ulong)(j % 7);

        private static ValeCounterArray[] RowsOf(SublimeCountMinSketch sketch)
        {
            var field = typeof(SublimeCountMinSketch).GetField("rows",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "the sketch has no field rows");
            return (ValeCounterArray[])field.GetValue(sketch)!;
        }

        private static void SetField(object target, string name, object value)
        {
            var field = target.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"the sketch has no field {name}");
            field.SetValue(target, value);
        }

        /// <summary>
        /// A single-row sketch of 1024 counters that has never expanded: a size factor of a
        /// sixteenth starts each row at a chunk's worth of counters times sixteen, rounded
        /// down to a power of two.
        /// </summary>
        private static SublimeCountMinSketch Wide() =>
            new SublimeCountMinSketch(0.5, 0.5, 1.0 / 16);

        /// <summary>
        /// Overwrites the counters of a one-row payload and repairs the checksum, so that
        /// what reads the result is the reader and not the CRC.
        /// </summary>
        private static byte[] WithCounters(byte[] original, Func<int, ulong> valueAt)
        {
            var bytes = (byte[])original.Clone();
            var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(PayloadStart + 28));
            Assert.AreEqual(1, (int)BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(PayloadStart + 24)), "the payload should hold a single row");

            for (var j = 0; j < width; j++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(
                    bytes.AsSpan(PayloadStart + CountersStart + 8 * j), valueAt(j));
            }

            var crc = new Crc32();
            crc.Append(bytes.AsSpan(4, bytes.Length - 8));
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(bytes.Length - 4), crc.GetCurrentHashAsUInt32());
            return bytes;
        }

        /// <summary>
        /// A payload with a few counters as large as a counter can be reads, writes back
        /// exactly the bytes it was read from, and packs them where they fit.
        /// </summary>
        /// <remarks>
        /// The step is at 2^63: the counters either side of it, 2^62 and 2^63 - 1, read
        /// before the fix, and the three from 2^63 up threw. Byte identity says the counters
        /// came back as the numbers they were and not as something nearby. The last two
        /// assertions say the tuning priced them: a tuning that tallied a counter of this
        /// length as nothing would pick the most crowded chunks there are, whose pool is too
        /// small for even one such extension, and every heavy counter would be on a tails
        /// array.
        /// </remarks>
        [TestMethod]
        [DataRow(1UL << 62)]
        [DataRow((1UL << 63) - 1)]
        [DataRow(1UL << 63)]
        [DataRow((1UL << 63) + 1)]
        [DataRow(ulong.MaxValue)]
        public void TestARowWithAFewLargeCountsReadsBackAndHasRoomForThem(ulong value)
        {
            var payload = WithCounters(Wide().ToByteArray(), j => Sparse(j, new[] { value }));

            var read = SublimeCountMinSketch.ReadFrom(new MemoryStream(payload));

            Assert.AreEqual(1024, read.Width);
            CollectionAssert.AreEqual(payload, read.ToByteArray(),
                "a sketch read from this payload writes something else");

            var row = RowsOf(read)[0];
            for (var j = 0; j < 1024; j++)
            {
                Assert.AreEqual(Sparse(j, new[] { value }), row.Get(j), $"counter {j}");
            }

            Assert.IsTrue(
                row.PoolFragments >= ValeCounter.ExtensionLength(value >> row.StubBits),
                $"a pool of {row.PoolFragments} fragments cannot hold the extension of {value}");
            Assert.AreEqual(0, row.ChunksWithTails,
                "a heavy counter alone in its chunk should fit that chunk's pool");
        }

        /// <summary>
        /// Counters of every length at once, in one row.
        /// </summary>
        [TestMethod]
        public void TestARowOfEveryLongLengthReadsAndWritesBackUnchanged()
        {
            var payload = WithCounters(Wide().ToByteArray(), j => Sparse(j, Heavy));

            var read = SublimeCountMinSketch.ReadFrom(new MemoryStream(payload));

            CollectionAssert.AreEqual(payload, read.ToByteArray());
            var row = RowsOf(read)[0];
            for (var j = 0; j < 1024; j++)
            {
                Assert.AreEqual(Sparse(j, Heavy), row.Get(j), $"counter {j}");
            }
        }

        /// <summary>
        /// A sketch whose arrays hold such counters writes them, and the payload it writes
        /// reads.
        /// </summary>
        /// <remarks>
        /// The writer reads each counter back out of its packed form and never tallies, so
        /// it was never the part that failed. What failed was its own output: a sketch that
        /// could write a payload its reader then refused with an unrelated exception.
        /// </remarks>
        [TestMethod]
        public void TestASketchHoldingLargeCountersWritesAPayloadItsReaderTakes()
        {
            var sketch = Wide();
            var row = RowsOf(sketch)[0];
            for (var j = 0; j < 1024; j++)
            {
                row.Set(j, Sparse(j, Heavy));
            }

            var payload = sketch.ToByteArray();
            for (var j = 0; j < 1024; j++)
            {
                Assert.AreEqual(Sparse(j, Heavy),
                    BinaryPrimitives.ReadUInt64LittleEndian(
                        payload.AsSpan(PayloadStart + CountersStart + 8 * j)),
                    $"counter {j} was written as another number");
            }

            var read = SublimeCountMinSketch.ReadFrom(new MemoryStream(payload));
            CollectionAssert.AreEqual(payload, read.ToByteArray());
        }

        /// <summary>
        /// The add that doubles the rows lays every counter out again, and survives
        /// counters of every length, copying each into the new half unchanged.
        /// </summary>
        /// <remarks>
        /// This is the path a running sketch takes: the same tally as the reader's, from an
        /// add. The add itself raises one counter by one, so the row after it differs from
        /// the one before in exactly one column, by exactly one, in each half -- and where
        /// that column held 2^63 - 1 the add is what carries it across the step.
        /// </remarks>
        [TestMethod]
        public void TestWideningARowOfLargeCountersCopiesThemAcross()
        {
            var sketch = Wide();
            var row = RowsOf(sketch)[0];
            for (var j = 0; j < 1024; j++)
            {
                row.Set(j, Sparse(j, Heavy));
            }

            // One add from the doubling: the first add takes the count to the limit.
            SetField(sketch, "expansionLimit", 1UL);
            sketch.Add(Key(1));

            Assert.AreEqual(2048, sketch.Width, "the add should have doubled the row");
            var widened = RowsOf(sketch)[0];
            var raised = 0;
            for (var j = 0; j < 1024; j++)
            {
                var was = Sparse(j, Heavy);
                var now = widened.Get(j);
                Assert.AreEqual(now, widened.Get(j + 1024), $"counter {j} was not copied");
                Assert.IsTrue(now == was || now == was + 1,
                    $"counter {j} held {was} and now holds {now}");
                raised += (int)(now - was);
            }
            Assert.AreEqual(1, raised, "the add should raise one counter, by one");
        }
    }
}
