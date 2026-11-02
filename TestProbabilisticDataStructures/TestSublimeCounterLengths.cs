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

        /// <summary>
        /// The tally puts each counter at its own bit length, from nought for a counter
        /// holding nothing to sixty-four for one with its top bit set.
        /// </summary>
        /// <remarks>
        /// The tuning built on the tally prices a length by what its extension costs, and
        /// the cost of 63 bits and of 64 differ by a fragment only some of the time, so a
        /// tally that put a counter one slot short could not be told from the outside: with
        /// 64-bit counters counted as 63-bit ones, every test above still passed, and only
        /// the tuning chosen for some rows (a stub a bit narrower, a chunk a counter
        /// fewer) was different. So this asks the tally itself. Each length has the
        /// smallest counter that has it and the largest, and a counter of nought, whose
        /// length is nought, is counted too.
        /// </remarks>
        [TestMethod]
        public void TestTheTallyPutsEveryCounterAtItsOwnLength()
        {
            var smallest = new ulong[65];
            var largest = new ulong[65];
            for (var length = 1; length <= 64; length++)
            {
                smallest[length] = 1UL << (length - 1);
                largest[length] = ulong.MaxValue >> (64 - length);
            }

            // Two rows, so that the tally is shown to run across rows and not only along one.
            var tally = SublimeCountMinSketch.CounterLengths(new[] { smallest, largest });

            Assert.AreEqual(65, tally.Length, "a length runs from nought to sixty-four");
            Assert.AreEqual(2L, tally[0], "the two counters of nought");
            for (var length = 1; length <= 64; length++)
            {
                Assert.AreEqual(2L, tally[length],
                    $"the smallest and the largest counter of {length} bits");
            }
        }

        /// <summary>
        /// A counter the fold would take below nought is nought.
        /// </summary>
        /// <remarks>
        /// The two halves started from the record and have each lost counts since, so their
        /// sum can be below it. Deletions are what bring a sketch to a fold, and an
        /// unmatched one is how the sum comes out under the record. No test reached this:
        /// with the floor taken out of the fold, the whole Sublime, Vale and persistence
        /// suites still passed. The state is built by hand, as the one below is.
        /// </remarks>
        [TestMethod]
        public void TestFoldingBackNeverGoesBelowNought()
        {
            var sketch = Wide();
            SetField(sketch, "expansionLimit", 1UL);
            sketch.Add(Key(1));
            Assert.AreEqual(2048, sketch.Width, "the add should have doubled the row");

            var records = (System.Collections.IList)typeof(SublimeCountMinSketch)
                .GetField("records", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(sketch)!;
            var record = ((ValeCounterArray[])records[records.Count - 1]!)[0];
            var row = RowsOf(sketch)[0];

            // The counters to set are two that the removal will not touch: the add raised
            // exactly one counter from nought, in each half, and the removal lowers that one.
            var raised = -1;
            for (var j = 0; j < 1024; j++)
            {
                if (row.Get(j) == 1)
                {
                    raised = j;
                }
            }
            Assert.AreNotEqual(-1, raised, "the add should have raised a counter");
            var below = (raised + 100) % 1024;
            var above = (raised + 200) % 1024;

            // 3 + 4 gathered against 10 to start from: seven less than it started, so nought.
            record.Set(below, 10);
            row.Set(below, 3);
            row.Set(below + 1024, 4);
            // 17 against 10: seven more than it started, which is what the fold keeps.
            record.Set(above, 10);
            row.Set(above, 8);
            row.Set(above + 1024, 9);

            sketch.Remove(Key(1));

            Assert.AreEqual(1024, sketch.Width, "the removal should have folded the row back");
            var folded = RowsOf(sketch)[0];
            Assert.AreEqual(0UL, folded.Get(below));
            Assert.AreEqual(7UL, folded.Get(above));
        }

        /// <summary>
        /// A sketch that widened and then has the same key removed folds back to the row it
        /// started as, counters of any size included.
        /// </summary>
        /// <remarks>
        /// This is not about tuning, and it did not throw. The fold adds a counter to its
        /// copy and takes off the record it started from, and it did that in signed longs, so
        /// a result of 2^63 or more was negative and "nothing is allowed to fall below nought"
        /// made it nought: a counter of 2^63 became 0, and the sketch answered a count of
        /// nought for a key it had counted 2^63 times, which a Count-Min sketch must never
        /// do. The sums wrap and come out right as long as the result fits a long, which is
        /// why 2^62 and 2^63 - 10 are the controls.
        /// </remarks>
        [TestMethod]
        [DataRow(1UL << 62)]
        [DataRow((1UL << 63) - 10)]
        [DataRow(1UL << 63)]
        [DataRow((1UL << 63) + 7)]
        [DataRow(ulong.MaxValue - 1)]
        public void TestFoldingBackKeepsCountersOfEveryLength(ulong value)
        {
            var sketch = Wide();
            var row = RowsOf(sketch)[0];
            for (var j = 0; j < 1024; j++)
            {
                row.Set(j, Sparse(j, new[] { value }));
            }

            // The add doubles the rows, and the removal of the same key takes the count back
            // under the fold-back limit, so every counter should be where it was.
            SetField(sketch, "expansionLimit", 1UL);
            sketch.Add(Key(1));
            Assert.AreEqual(2048, sketch.Width, "the add should have doubled the row");
            sketch.Remove(Key(1));

            Assert.AreEqual(1024, sketch.Width, "the removal should have folded the row back");
            var folded = RowsOf(sketch)[0];
            for (var j = 0; j < 1024; j++)
            {
                Assert.AreEqual(Sparse(j, new[] { value }), folded.Get(j), $"counter {j}");
            }
        }

        /// <summary>
        /// A counter the fold would take past the largest count stops there, as the sketch
        /// has no larger count to give; it does not wrap round to something small.
        /// </summary>
        /// <remarks>
        /// Two halves that each hold the largest count and started from nothing add up to
        /// twice it. That cannot be reached by adding -- it is 2^65 adds -- so the state is
        /// built by hand, and what is asked is only the direction: a count that cannot be
        /// held must not be reported as less than the sketch has seen.
        /// </remarks>
        [TestMethod]
        public void TestFoldingBackStopsAtTheLargestCountRatherThanWrapping()
        {
            var sketch = Wide();
            SetField(sketch, "expansionLimit", 1UL);
            sketch.Add(Key(1));
            Assert.AreEqual(2048, sketch.Width, "the add should have doubled the row");

            var records = (System.Collections.IList)typeof(SublimeCountMinSketch)
                .GetField("records", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(sketch)!;
            var record = ((ValeCounterArray[])records[records.Count - 1]!)[0];
            var row = RowsOf(sketch)[0];
            const int J = 100;
            record.Set(J, 0);
            row.Set(J, ulong.MaxValue);
            row.Set(J + 1024, ulong.MaxValue);

            sketch.Remove(Key(1));

            Assert.AreEqual(1024, sketch.Width, "the removal should have folded the row back");
            Assert.AreEqual(ulong.MaxValue, RowsOf(sketch)[0].Get(J));
        }

        /// <summary>
        /// An add that lands on a counter at the largest count leaves it there, and the one
        /// below it takes its last step up to it: the counter does not wrap to nought.
        /// </summary>
        /// <remarks>
        /// A counter can be at the largest count without 2^64 adds, from a payload or from
        /// the fold above, and an add to it came out as nought. The estimate is the smallest
        /// of a key's counters, so the nought is the estimate, for a key the sketch has
        /// counted at least 2^64 times: an undercount, which a Count-Min sketch never gives.
        /// Every counter of a one-row sketch is set, so that the key lands on one whatever
        /// the hash makes of it, and the row is read from a payload because that is how a
        /// caller meets it.
        /// </remarks>
        [TestMethod]
        [DataRow(ulong.MaxValue, ulong.MaxValue)]
        [DataRow(ulong.MaxValue - 1, ulong.MaxValue)]
        [DataRow(ulong.MaxValue - 2, ulong.MaxValue - 1)]
        public void TestAnAddToTheLargestCountLeavesItThere(ulong held, ulong expected)
        {
            var payload = WithCounters(Wide().ToByteArray(), _ => held);
            var read = SublimeCountMinSketch.ReadFrom(new MemoryStream(payload));

            read.Add(Key(1));

            Assert.AreEqual(expected, read.Count(Key(1)), "the estimate after the add");

            // The add raises the one counter the key lands on, if it can, and no other.
            var row = RowsOf(read)[0];
            var raised = 0;
            for (var j = 0; j < 1024; j++)
            {
                var now = row.Get(j);
                Assert.IsTrue(now == held || now == expected, $"counter {j} held {held} and now holds {now}");
                raised += now == held ? 0 : 1;
            }
            Assert.AreEqual(held == expected ? 0 : 1, raised, "the counters the add raised");
        }

        /// <summary>
        /// One row at the largest count does not drag the estimate down, wherever in the
        /// sketch that row is.
        /// </summary>
        /// <remarks>
        /// The estimate is the smallest of the rows, so a row that wrapped to nought is the
        /// estimate whatever the others hold: this is how one row is enough, and why the
        /// test has rows that are not at the largest count to be told apart from. An add
        /// raises each of them by one and leaves the saturated row where it is, so the
        /// estimate goes from the others' count to one more. The row that is saturated runs
        /// through the first, a middle one and the last, so that no row is exempt from it.
        /// </remarks>
        [TestMethod]
        [DataRow(0)]
        [DataRow(2)]
        [DataRow(4)]
        public void TestOneRowAtTheLargestCountLeavesTheEstimateWhereTheOthersPutIt(int saturated)
        {
            var sketch = new SublimeCountMinSketch(0.01, 0.5, 1.0 / 16);
            var rows = RowsOf(sketch);
            Assert.AreEqual(5, rows.Length, "the test is about the position of a row among several");
            Assert.AreEqual(1024, sketch.Width);
            for (var i = 0; i < rows.Length; i++)
            {
                for (var j = 0; j < 1024; j++)
                {
                    rows[i].Set(j, i == saturated ? ulong.MaxValue : 40);
                }
            }
            Assert.AreEqual(40UL, sketch.Count(Key(1)), "the estimate before the add");

            sketch.Add(Key(1));

            Assert.AreEqual(41UL, sketch.Count(Key(1)), "the estimate after the add");
        }

        /// <summary>
        /// The widest stub a chunk of the fewest counters can have and still keep the pool
        /// the array requires: the widest the constructor accepts at that many counters.
        /// </summary>
        private static int WidestStubAtTheFewestCounters()
        {
            var widest = 0;
            for (var s = ValeCounterArray.MinStubBits; s <= ValeCounterArray.MaxStubBits; s++)
            {
                try
                {
                    _ = new ValeCounterArray(
                        ValeCounterArray.MinCountersPerChunk, ValeCounterArray.MinCountersPerChunk, s);
                    widest = s;
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            }

            // The answer is a stub with room to spare either side, so that a search which
            // stopped at the first or accepted the last would be noticed.
            Assert.IsTrue(widest > ValeCounterArray.MinStubBits && widest < ValeCounterArray.MaxStubBits,
                $"a widest stub of {widest} is not a finding");
            return widest;
        }

        /// <summary>
        /// A row in which no tuning satisfies the bound is tuned anyway, to the best of the
        /// tunings there are.
        /// </summary>
        /// <remarks>
        /// Tuning looks for a layout whose chunks will not spill, and a row in which every
        /// counter is this large has none: at the fewest counters a chunk keeps a stub of
        /// at most 27 bits, and sixteen counters of 28 bits or more then want more extension
        /// than the pool left holds. It answers with "the widest stub with a pool left" at
        /// the fewest counters -- and the answer it gave, a stub of 32 bits, leaves no pool
        /// at all, so the array it was meant for refused it with an
        /// <see cref="ArgumentOutOfRangeException"/> naming a chunk "short of the 24 a chunk
        /// is required to keep". The widest stub the constructor accepts at that many counters
        /// is what the comment promised, and the tails arrays carry what the pools cannot.
        /// <para>
        /// This is not a defect of the longest counters: 2^27 is the smallest value found to
        /// do it, in a row of nothing else. It is here because a row of counters of 2^63 and
        /// more, once it could be tallied, is one of the rows that does.
        /// </para>
        /// </remarks>
        [TestMethod]
        [DataRow(1UL << 27)]
        [DataRow(1UL << 40)]
        [DataRow(1UL << 62)]
        [DataRow(1UL << 63)]
        [DataRow(ulong.MaxValue)]
        public void TestARowOfNothingButLargeCountsReadsBackAtTheBestTuningThereIs(ulong value)
        {
            var payload = WithCounters(Wide().ToByteArray(), _ => value);

            var read = SublimeCountMinSketch.ReadFrom(new MemoryStream(payload));

            CollectionAssert.AreEqual(payload, read.ToByteArray());
            Assert.AreEqual(value, read.Count(Key(1)));
            var row = RowsOf(read)[0];
            Assert.AreEqual(ValeCounterArray.MinCountersPerChunk, row.CountersPerChunk);
            Assert.AreEqual(WidestStubAtTheFewestCounters(), row.StubBits);
            for (var j = 0; j < 1024; j++)
            {
                Assert.AreEqual(value, row.Get(j), $"counter {j}");
            }
        }

        /// <summary>
        /// The same row, widened by an add rather than read.
        /// </summary>
        [TestMethod]
        [DataRow(1UL << 27)]
        [DataRow(1UL << 62)]
        [DataRow(1UL << 63)]
        [DataRow(ulong.MaxValue - 1)]
        public void TestWideningARowOfNothingButLargeCountsCopiesThemAcross(ulong value)
        {
            var sketch = Wide();
            var row = RowsOf(sketch)[0];
            for (var j = 0; j < 1024; j++)
            {
                row.Set(j, value);
            }

            SetField(sketch, "expansionLimit", 1UL);
            sketch.Add(Key(1));

            Assert.AreEqual(2048, sketch.Width, "the add should have doubled the row");
            var widened = RowsOf(sketch)[0];
            var raised = 0;
            for (var j = 0; j < 1024; j++)
            {
                var now = widened.Get(j);
                Assert.AreEqual(now, widened.Get(j + 1024), $"counter {j} was not copied");
                Assert.IsTrue(now == value || now == value + 1,
                    $"counter {j} held {value} and now holds {now}");
                raised += (int)(now - value);
            }
            Assert.AreEqual(1, raised, "the add should raise one counter, by one");
        }
    }
}
