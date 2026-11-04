using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// A Sublime sketch whose rows cannot get the memory to double or to fold back.
    /// </summary>
    /// <remarks>
    /// Doubling the rows allocates every counter of the doubled rows at once, eight bytes
    /// each as plain counts and then again packed: 5 GiB of plain counts to double five rows
    /// to 2^27 counters. At wide rows an <see cref="OutOfMemoryException"/> is the likely way
    /// for an add to fail. The add that doubled the rows counted its key first, then appended
    /// the record of the rows it was about to double, then allocated, so a doubling that ran
    /// out of memory left the key counted though the add threw, a record of a doubling that
    /// had not happened, and the next doubling where it was, so that every add after it tried
    /// again and appended another record. The width was set between the plain counts and the
    /// packed rows, so a failure between them left the width doubled over rows that had not.
    /// Folding back took its record off before it allocated, and set the width at the same
    /// point. Under a real heap limit of 400 MiB, master b29d881 threw at the 4,504th add to a
    /// sketch of five rows of 2^23 counters, and again at each of two more tries of the same
    /// key, which it then counted three times, holding three records at the width it started
    /// at. That run is a manual one; the issue, #213, has the recipe.
    /// <para>
    /// A test cannot set a heap limit from inside its own process, so the sketch has a hook,
    /// <see cref="SublimeCountMinSketch.BeforeAllocating"/>, that every row a doubling or
    /// a folding allocates passes through on its way to the allocation, and every tails array
    /// a row of the sketch allocates. Each test fails those allocations one at a time, the
    /// first, then the second, and so on until the resize gets all it asks for, and holds the
    /// sketch to what it was at every one of them.
    /// </para>
    /// <para>
    /// #242 found four paths that #213's fix left changing part of the sketch. The doubling
    /// took its key's increments in the rows that became its record after building the
    /// doubled rows, and an increment can move a chunk to a tails array. A removal that folded
    /// the rows took its decrement before it built the folded ones; an add that crowded the
    /// rows into being laid out again counted its key before it laid them out; and a reset set
    /// the width and the tuning, then replaced the rows one at a time. The later sweeps hold
    /// each of them to the same rule as the first: whatever allocation fails, the sketch is as
    /// it was before the call, in its payload and in its rows and how they are packed.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestSublimeOutOfMemory
    {
        /// <summary>
        /// A bound on each sweep, so that it ends by count. It is far past the rows a resize
        /// of a sketch this deep allocates.
        /// </summary>
        private const int MostAllocations = 1_000;

        private static byte[] Key(int i) => Encoding.UTF8.GetBytes("k" + i);

        /// <summary>The key whose add doubles the rows.</summary>
        private static readonly byte[] Doubler = Key(7);

        /// <summary>The one key of the sketch that folds back.</summary>
        private static readonly byte[] Folder = Key(3);

        /// <summary>
        /// The default sketch, 64 counters wide, one add short of doubling: at a size factor
        /// of one and a growth of one half, its rows double at the 4,096th key.
        /// </summary>
        private static SublimeCountMinSketch OneShortOfDoubling()
        {
            var sketch = new SublimeCountMinSketch(0.01);
            for (var i = 0; i < 4_095; i++)
            {
                sketch.Add(Key(i % 500));
            }

            Assert.AreEqual(64, sketch.Width, "the sketch doubled before the 4,096th key");
            return sketch;
        }

        /// <summary>
        /// A doubling that runs out of memory leaves the sketch exactly as it was before the
        /// add: the key not counted, no record, the width where it was and the next doubling
        /// still due at the add tried again. That add counts the key once and doubles the
        /// rows once, into the sketch an add that never failed makes.
        /// </summary>
        [TestMethod]
        public void TestADoublingThatRunsOutOfMemoryLeavesTheSketchAsItWas()
        {
            var twin = OneShortOfDoubling();
            twin.Add(Doubler);
            Assert.AreEqual(128, twin.Width, "the 4,096th key did not double the rows");
            var doubled = twin.ToByteArray();

            var wrong = new List<string>();
            var failures = 0;
            for (var failing = 0; ; failing++)
            {
                Assert.IsTrue(failing < MostAllocations,
                    $"a doubling made more than {MostAllocations} allocations");

                var sketch = OneShortOfDoubling();
                var before = sketch.ToByteArray();
                var hook = new FailAt(failing);
                sketch.BeforeAllocating = hook.Before;
                var threw = RunsOutOfMemory(() => sketch.Add(Doubler));
                sketch.BeforeAllocating = null;

                if (!threw)
                {
                    Assert.AreEqual(failing, hook.Seen,
                        "the add finished without making the allocation it was to fail");
                    CollectionAssert.AreEqual(doubled, sketch.ToByteArray(),
                        "a doubling that got all its memory differs from the twin's");
                    break;
                }

                failures++;
                var at = $"Failing allocation {failing}";
                Check(wrong, $"{at}, the total", 4_095UL, () => sketch.TotalCount());
                Check(wrong, $"{at}, the width", 64, () => sketch.Width);
                Check(wrong, $"{at}, the records", 0, () => RecordsOf(sketch));
                Same(wrong, $"{at}, the payload", before, () => sketch.ToByteArray());
                ReadsBack(wrong, $"{at}, the payload read back", sketch);

                Check(wrong, $"{at}, the add tried again", true, () => Add(sketch, Doubler));
                Check(wrong, $"{at}, then the total", 4_096UL, () => sketch.TotalCount());
                Check(wrong, $"{at}, then the width", 128, () => sketch.Width);
                Check(wrong, $"{at}, then the records", 1, () => RecordsOf(sketch));
                Same(wrong, $"{at}, then the payload", doubled, () => sketch.ToByteArray());
                ReadsBack(wrong, $"{at}, then the payload read back", sketch);
            }

            // Every row is allocated twice, as plain counts and then packed. A sweep that failed
            // no more allocations than there are rows never reached the packed ones, which is
            // where the width used to be set too early.
            Assert.IsTrue(failures > twin.Depth,
                $"the sweep failed {failures} allocations of a doubling of {twin.Depth} rows");
            Assert.AreEqual(0, wrong.Count, string.Join(Environment.NewLine, wrong));
        }

        /// <summary>
        /// The default sketch with one key, doubled at 4,096 and taken back down to one removal
        /// short of folding: from 5,000 to 2,560, where the 2,441st removal takes it below the
        /// 2,560 that rows of 128 fold back under. One key, so that every count is exact.
        /// </summary>
        private static SublimeCountMinSketch OneShortOfFolding()
        {
            var sketch = new SublimeCountMinSketch(0.01);
            for (var i = 0; i < 5_000; i++)
            {
                sketch.Add(Folder);
            }
            for (var i = 0; i < 2_440; i++)
            {
                sketch.Remove(Folder);
            }

            Assert.AreEqual(128, sketch.Width, "the sketch did not double, or folded early");
            Assert.AreEqual(1, RecordsOf(sketch));
            return sketch;
        }

        /// <summary>
        /// A removal whose folding runs out of memory leaves the sketch exactly as it was: the
        /// key not taken out, the rows, their width and the record they fold back by as they
        /// were. Removing the key again removes it once, and folds the rows into what one
        /// removal that never failed makes.
        /// </summary>
        /// <remarks>
        /// The removal used to take the key out of the rows before it built the folded ones,
        /// so a folding that failed left the key removed though the removal threw, and the
        /// removal tried again took it out twice.
        /// </remarks>
        [TestMethod]
        public void TestARemovalThatRunsOutOfMemoryFoldingLeavesTheSketchAsItWas()
        {
            var twin = OneShortOfFolding();
            twin.Remove(Folder);
            Assert.AreEqual(64, twin.Width, "the 2,441st removal did not fold the rows");
            Assert.AreEqual(0, RecordsOf(twin), "the folding kept its record");
            Assert.AreEqual(2_559UL, twin.Count(Folder), "the removal did not take the key out once");

            var wrong = new List<string>();
            var failures = FailEachAllocation(
                wrong, OneShortOfFolding, sketch => sketch.Remove(Folder), twin);
            Assert.AreEqual(0, wrong.Count, string.Join(Environment.NewLine, wrong));

            // A folding allocates each row twice, as plain counts and then packed, and a tails
            // array for any chunk of the packed rows that outgrows its pool.
            Assert.AreEqual(2 * twin.Depth + OnTails(RowsOf(twin)), failures,
                "the sweep did not fail every allocation the folding makes");
        }

        /// <summary>
        /// Keys placed by hand: <see cref="At"/> gives the key whose hash, under this function,
        /// is its column in every row.
        /// </summary>
        private static ulong Identity(ReadOnlySpan<byte> key) =>
            BinaryPrimitives.ReadUInt64LittleEndian(key);

        /// <summary>
        /// The key that <see cref="Identity"/> puts at <paramref name="column"/> in every row:
        /// the low half of its hash is the column and the high half nought, so that the step
        /// between rows is nought too.
        /// </summary>
        private static byte[] At(int column)
        {
            var key = new byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(key, (ulong)column);
            return key;
        }

        /// <summary>The column whose counter is left one add from spilling its chunk.</summary>
        private const int Spiller = 0;

        /// <summary>
        /// A sketch of three rows whose first chunk has as full a pool as the shortest
        /// extensions make it, in every row, with the counter at <see cref="Spiller"/> one add
        /// from needing one more.
        /// </summary>
        /// <remarks>
        /// A chunk at the starting tuning holds 68 counters with stubs of five bits, which
        /// leaves a pool of 51 fragments: room for 25 extensions of the shortest length, two
        /// fragments, which every count from 32 to 95 takes. So 25 counters at 32 fill it to 50
        /// fragments, and the counter at 31 that the next add takes to 32 needs two more, which
        /// the pool does not have: that add moves the chunk to a tails array, in every row.
        /// Every count is added, so this is a sketch that a stream builds, and the numbers come
        /// from the tuning rather than from here.
        /// </remarks>
        private static SublimeCountMinSketch OneAddFromSpilling(double sizeFactor)
        {
            var sketch = new SublimeCountMinSketch(0.05, 0.5, sizeFactor, Identity);
            var row = RowsOf(sketch)[0];
            var shortest = 1 << row.StubBits;
            var fit = row.PoolFragments / ValeCounter.ExtensionLength(1);
            for (var column = Spiller + 1; column <= Spiller + fit; column++)
            {
                for (var i = 0; i < shortest; i++)
                {
                    sketch.Add(At(column));
                }
            }
            for (var i = 1; i < shortest; i++)
            {
                sketch.Add(At(Spiller));
            }

            Assert.AreEqual(0, OnTails(RowsOf(sketch)), "a chunk spilled while its pool was filled");
            Assert.AreEqual(ValeCounterArray.DefaultCountersPerChunk, sketch.CountersPerChunk,
                "the sketch was laid out again while its pool was filled");
            return sketch;
        }

        /// <summary>
        /// <see cref="OneAddFromSpilling"/> at rows of 128, with a key in their second chunk
        /// added until the next add doubles them: at a size factor of a half the rows double at
        /// the 4,096th key, and the key at <see cref="Spiller"/> is that key.
        /// </summary>
        private static SublimeCountMinSketch OneShortOfDoublingAtTheSpiller()
        {
            const double SizeFactor = 0.5;
            var sketch = OneAddFromSpilling(SizeFactor);
            Assert.AreEqual(128, sketch.Width);

            var filler = At(ValeCounterArray.DefaultCountersPerChunk + 32);
            var doubling = SublimeCountMinSketch.LimitFor(sketch.Width, SizeFactor, 0.5);
            while (sketch.TotalCount() + 1 < doubling)
            {
                sketch.Add(filler);
            }

            Assert.AreEqual(4_095UL, sketch.TotalCount());
            Assert.AreEqual(128, sketch.Width, "the rows doubled before the key at the spiller");
            Assert.AreEqual(0, OnTails(RowsOf(sketch)), "a chunk spilled before the doubling");
            return sketch;
        }

        /// <summary>
        /// The doubling counts its key in the rows that become its record, and that increment
        /// can move a chunk to a tails array, which allocates; here it does, in every row. A
        /// doubling that runs out of memory at any allocation, that one included, leaves the
        /// sketch exactly as it was, and the add tried again doubles it into what the twin's
        /// add made.
        /// </summary>
        /// <remarks>
        /// The doubling used to take those increments in the old rows themselves, after the
        /// doubled rows were built, so a failure there left the sketch partly changed: the rows
        /// before it counted, and in the row where it failed a counter whose stub had been
        /// written and whose high bits had not.
        /// </remarks>
        [TestMethod]
        public void TestADoublingThatRunsOutOfMemoryCountingItsKeyInTheRecordLeavesTheSketchAsItWas()
        {
            var twin = OneShortOfDoublingAtTheSpiller();
            twin.Add(At(Spiller));
            Assert.AreEqual(256, twin.Width, "the key at the spiller did not double the rows");
            var record = RecordRowsOf(twin)[0];
            for (var i = 0; i < record.Length; i++)
            {
                Assert.AreEqual(1, record[i].ChunksWithTails,
                    $"counting the key in row {i} of the record did not spill its chunk");
            }

            var wrong = new List<string>();
            var failures = FailEachAllocation(
                wrong, OneShortOfDoublingAtTheSpiller, sketch => sketch.Add(At(Spiller)), twin,
                Identity);
            Assert.AreEqual(0, wrong.Count, string.Join(Environment.NewLine, wrong));

            // A doubling allocates each row three times, as plain counts, packed, and copied
            // into the record, and a tails array for each chunk of the packed rows or of the
            // record that outgrows its pool.
            Assert.AreEqual(3 * twin.Depth + OnTails(RowsOf(twin)) + OnTails(record), failures,
                "the sweep did not fail every allocation the doubling makes");
        }

        /// <summary>
        /// A doubling of rows whose every counter is too large for any tuning to keep in the
        /// pools: the doubled rows are packed at the tuning the sketch falls back to, with
        /// every chunk on a tails array, and each of those arrays is allocated while the rows
        /// are built. A doubling that runs out of memory at any of them, or anywhere else,
        /// leaves the sketch exactly as it was.
        /// </summary>
        /// <remarks>
        /// The other sweeps' rows are packed with nothing on tails, so this is the one that
        /// fails the allocations the packing itself makes. The counters are set by hand, as
        /// the stream that would raise every counter to 2^40 is not one a test can add.
        /// </remarks>
        [TestMethod]
        public void TestADoublingThatRunsOutOfMemoryPackingItsRowsOntoTailsLeavesTheSketchAsItWas()
        {
            static SublimeCountMinSketch Make()
            {
                var sketch = OneShortOfDoubling();
                foreach (var row in RowsOf(sketch))
                {
                    for (var j = 0; j < row.Count; j++)
                    {
                        row.Set(j, 1UL << 40);
                    }
                }
                return sketch;
            }

            var copied = OnTails(RowsOf(Make()));
            var twin = Make();
            twin.Add(Doubler);
            Assert.AreEqual(128, twin.Width, "the 4,096th key did not double the rows");
            var rows = RowsOf(twin);
            Assert.IsTrue(rows.All(row => row.ChunksWithTails == row.ChunkCount),
                "the doubled rows were not packed with every chunk on tails");

            var wrong = new List<string>();
            var failures = FailEachAllocation(wrong, Make, sketch => sketch.Add(Doubler), twin);
            Assert.AreEqual(0, wrong.Count, string.Join(Environment.NewLine, wrong));

            // Each row three times, as above, and every chunk of the packed rows. The record's
            // chunks were on tails before the doubling, so it copies those arrays rather than
            // allocating new ones, and the key's increment in it needs none.
            var record = RecordRowsOf(twin)[0];
            Assert.AreEqual(copied, OnTails(record), "the key's increment spilled a chunk of the record");
            Assert.AreEqual(3 * twin.Depth + OnTails(rows), failures,
                "the sweep did not fail every allocation the doubling makes");
        }

        /// <summary>
        /// An add that does not double the rows but crowds them, by moving more of their chunks
        /// to tails arrays than three in a hundred, lays them out again. One that runs out of
        /// memory doing so leaves the sketch exactly as it was: the key not counted, and the
        /// rows, packed as they were, in place. The add tried again counts the key once and
        /// lays the rows out as the twin's add did.
        /// </summary>
        /// <remarks>
        /// The add used to take its increments in the rows, moving the crowded chunks to tails
        /// arrays as it went, and count the key, and only then lay the rows out again, so a
        /// failure there left the key counted though the add threw. At rows of 64 every row is
        /// one chunk, so the add that spills the first chunk crowds them.
        /// <para>
        /// The add takes at once the increments that stop at a stub with room, and takes them
        /// back if the others crowd the rows. So one row may be given room, by setting its
        /// counter one lower by hand: the first, which takes its increment before any row finds
        /// a full stub, a middle one or the last, which take theirs after, or none. Each is laid
        /// out with the key counted once.
        /// </para>
        /// </remarks>
        [TestMethod]
        [DataRow(-1, DisplayName = "no row with room")]
        [DataRow(0, DisplayName = "room in the first row")]
        [DataRow(1, DisplayName = "room in the middle row")]
        [DataRow(2, DisplayName = "room in the last row")]
        public void TestACrowdedAddThatRunsOutOfMemoryLayingTheRowsOutLeavesTheSketchAsItWas(int roomy)
        {
            SublimeCountMinSketch Make()
            {
                var sketch = OneAddFromSpilling(1.0);
                if (roomy >= 0)
                {
                    var row = RowsOf(sketch)[roomy];
                    row.Set(Spiller, row.Get(Spiller) - 1);
                }
                return sketch;
            }

            var twin = Make();
            Assert.AreEqual(64, twin.Width);
            var rows = RowsOf(twin).ToArray();
            var before = rows.Select(row => row.Get(Spiller)).ToArray();
            twin.Add(At(Spiller));
            Assert.AreEqual(64, twin.Width, "the add doubled the rows");
            for (var i = 0; i < rows.Length; i++)
            {
                Assert.AreNotSame(rows[i], RowsOf(twin)[i],
                    $"row {i} was not laid out again by the add that spilled the others' chunks");
                Assert.AreEqual(before[i] + 1, RowsOf(twin)[i].Get(Spiller),
                    $"row {i} did not count the key once");
            }

            var wrong = new List<string>();
            var failures = FailEachAllocation(
                wrong, Make, sketch => sketch.Add(At(Spiller)), twin, Identity);
            Assert.AreEqual(0, wrong.Count, string.Join(Environment.NewLine, wrong));

            // Laying the rows out again allocates each twice, as plain counts and then packed,
            // and a tails array for any chunk of the packed rows that outgrows its pool.
            Assert.AreEqual(2 * twin.Depth + OnTails(RowsOf(twin)), failures,
                "the sweep did not fail every allocation the add makes");
        }

        /// <summary>
        /// The crowded add counts its key into the counts it lays out, not through an
        /// increment, so it stops at the largest count itself: a counter there stays there, one
        /// below it takes its last step up, and the estimate is what the other rows give.
        /// </summary>
        /// <remarks>
        /// The first row's counter at the spiller is set by hand, and its extension alone
        /// spills that row's chunk, so the add moves the other two rows' chunks and crowds the
        /// rows as before. Without the stop, a counter at the largest count wrapped to nought,
        /// and with it the estimate, for a key counted at least 2^64 times.
        /// </remarks>
        [TestMethod]
        [DataRow(ulong.MaxValue, ulong.MaxValue)]
        [DataRow(ulong.MaxValue - 1, ulong.MaxValue)]
        public void TestACrowdedAddLeavesACounterAtTheLargestCountThere(ulong held, ulong expected)
        {
            var sketch = OneAddFromSpilling(1.0);
            var rows = RowsOf(sketch).ToArray();
            rows[0].Set(Spiller, held);
            Assert.AreEqual(1, OnTails(rows), "the first row's chunk did not spill");
            var raised = rows[1].Get(Spiller) + 1;

            sketch.Add(At(Spiller));

            for (var i = 0; i < rows.Length; i++)
            {
                Assert.AreNotSame(rows[i], RowsOf(sketch)[i], $"row {i} was not laid out again");
            }
            Assert.AreEqual(expected, RowsOf(sketch)[0].Get(Spiller), "the first row's counter");
            Assert.AreEqual(raised, sketch.Count(At(Spiller)), "the estimate after the add");
        }

        /// <summary>
        /// An add whose increment fills a row's stub, with room for it, has not put that row's
        /// chunk on tails, even where the row's next increment would: only the rows whose
        /// stubs were already full can move a chunk, and here none of them does, so the add
        /// lays nothing out.
        /// </summary>
        /// <remarks>
        /// Set by hand, each row's first chunk at 50 of its pool's 51 fragments. The first row's
        /// counter at the spiller is at 30, so the add fills its stub at 31, from where the next
        /// add would need two more fragments; the other two rows' counters are at 63, whose
        /// stubs are full and whose increment to 64 keeps its extension two fragments long. Asked
        /// of the first row after its stub was raised, the question finds a full stub one add
        /// from spilling, and an add that crowds the rows where none was moved.
        /// </remarks>
        [TestMethod]
        public void TestAnAddThatFillsAStubDoesNotCountItsChunkAsSpilling()
        {
            var sketch = new SublimeCountMinSketch(0.05, 0.5, 1.0, Identity);
            var rows = RowsOf(sketch).ToArray();
            var shortest = 1UL << rows[0].StubBits;
            for (var i = 0; i < rows.Length; i++)
            {
                var fill = i == 0 ? 25 : 24;
                for (var column = Spiller + 1; column <= Spiller + fill; column++)
                {
                    rows[i].Set(column, shortest);
                }
                rows[i].Set(Spiller, i == 0 ? shortest - 2 : 2 * shortest - 1);
            }
            Assert.AreEqual(0, OnTails(rows), "a chunk spilled while its pool was filled");

            sketch.Add(At(Spiller));

            for (var i = 0; i < rows.Length; i++)
            {
                Assert.AreSame(rows[i], RowsOf(sketch)[i],
                    $"row {i} was laid out again by an add that moved no chunk");
            }
            Assert.AreEqual(0, OnTails(rows), "the add put a chunk on tails");
            Assert.AreEqual(shortest - 1, rows[0].Get(Spiller), "the first row's counter");
            Assert.AreEqual(2 * shortest, rows[1].Get(Spiller), "the second row's counter");
            Assert.AreEqual(2 * shortest, rows[2].Get(Spiller), "the third row's counter");
        }

        /// <summary>
        /// The removal that folds the rows takes its key out of the counts it folds, not
        /// through a decrement, so it stops at nought itself: a key never added leaves its
        /// counters at nought, and the rows fold as they do for any other key never added.
        /// </summary>
        /// <remarks>
        /// One key, at column 1, doubles the rows and is then removed to one short of folding
        /// them, as <see cref="OneShortOfFolding"/> does; the removals that fold them are of keys
        /// at columns 5 and 9, which hold nought in every row. Without the stop, the counter
        /// such a key landed on came out of the folding at the largest count.
        /// </remarks>
        [TestMethod]
        public void TestAFoldingRemovalOfAKeyNeverAddedLeavesItsCountersAtNought()
        {
            static SublimeCountMinSketch OneShortOfFoldingAtColumnOne()
            {
                var sketch = new SublimeCountMinSketch(0.01, 0.5, 1.0, Identity);
                for (var i = 0; i < 5_000; i++)
                {
                    sketch.Add(At(1));
                }
                for (var i = 0; i < 2_440; i++)
                {
                    sketch.Remove(At(1));
                }

                Assert.AreEqual(128, sketch.Width, "the sketch did not double, or folded early");
                Assert.AreEqual(1, RecordsOf(sketch));
                return sketch;
            }

            var one = OneShortOfFoldingAtColumnOne();
            Assert.AreEqual(0UL, one.Count(At(5)));
            one.Remove(At(5));
            var other = OneShortOfFoldingAtColumnOne();
            other.Remove(At(9));

            Assert.AreEqual(64, one.Width, "the removal did not fold the rows");
            Assert.AreEqual(0UL, one.Count(At(5)), "the key never added after the folding");
            Assert.AreEqual(2_560UL, one.Count(At(1)),
                "the key added, which the removal did not touch, after the folding");
            Assert.AreEqual(2_559UL, one.TotalCount());
            CollectionAssert.AreEqual(other.ToByteArray(), one.ToByteArray(),
                "two keys never added folded the rows differently");
        }

        /// <summary>
        /// A reset that runs out of memory building its rows leaves the sketch exactly as it
        /// was, its width, rows, records and tuning included, and the reset tried again leaves
        /// the sketch a new one would be.
        /// </summary>
        /// <remarks>
        /// The reset used to set the tuning and the width first and then replace the rows one at
        /// a time, so a failure left the starting width over rows that had not shrunk to it.
        /// The sketch reset is one that has doubled and holds a record, so that every one of
        /// those has something to go back to.
        /// </remarks>
        [TestMethod]
        public void TestAResetThatRunsOutOfMemoryLeavesTheSketchAsItWas()
        {
            var twin = OneShortOfFolding();
            twin.Reset();
            var fresh = new SublimeCountMinSketch(0.01);
            CollectionAssert.AreEqual(fresh.ToByteArray(), twin.ToByteArray(),
                "a reset sketch writes what a new one does not");
            var unlike = new List<string>();
            PackedAlike(unlike, "A reset sketch", fresh, twin);
            Assert.AreEqual(0, unlike.Count, string.Join(Environment.NewLine, unlike));

            var wrong = new List<string>();
            var failures = FailEachAllocation(
                wrong, OneShortOfFolding, sketch => sketch.Reset(), twin);
            Assert.AreEqual(0, wrong.Count, string.Join(Environment.NewLine, wrong));

            // A reset allocates each row once, empty, so none of them is on tails.
            Assert.AreEqual(twin.Depth, failures,
                "the sweep did not fail every allocation the reset makes");
        }

        /// <summary>
        /// Fails each allocation an operation makes in turn, on a sketch built afresh each time
        /// by <paramref name="make"/>, and holds the sketch to what it was before the operation
        /// after every failure. Each time, it then tries the operation again, and holds the
        /// sketch to <paramref name="twin"/>, which <paramref name="make"/> built and the
        /// operation changed with nothing failing. Returns how many allocations it failed,
        /// which is how many the operation makes.
        /// </summary>
        private static int FailEachAllocation(
            List<string> wrong,
            Func<SublimeCountMinSketch> make,
            Action<SublimeCountMinSketch> operation,
            SublimeCountMinSketch twin,
            Func<ReadOnlySpan<byte>, ulong> hash = null)
        {
            var after = twin.ToByteArray();
            for (var failing = 0; ; failing++)
            {
                Assert.IsTrue(failing < MostAllocations,
                    $"the operation made more than {MostAllocations} allocations");

                var sketch = make();
                var before = new State(sketch);
                var hook = new FailAt(failing);
                sketch.BeforeAllocating = hook.Before;
                var threw = RunsOutOfMemory(() => operation(sketch));
                sketch.BeforeAllocating = null;

                if (!threw)
                {
                    Assert.AreEqual(failing, hook.Seen,
                        "the operation finished without making the allocation it was to fail");
                    Same(wrong, "With every allocation made, the payload", after,
                        () => sketch.ToByteArray());
                    PackedAlike(wrong, "With every allocation made", twin, sketch);
                    return failing;
                }

                var at = $"Failing allocation {failing}";
                before.Holds(wrong, at, sketch);
                ReadsBack(wrong, $"{at}, the payload read back", sketch, hash);

                Check(wrong, $"{at}, the operation tried again", true, () =>
                {
                    operation(sketch);
                    return true;
                });
                Same(wrong, $"{at}, then the payload", after, () => sketch.ToByteArray());
                PackedAlike(wrong, $"{at}, then", twin, sketch);
                ReadsBack(wrong, $"{at}, then the payload read back", sketch, hash);
            }
        }

        /// <summary>
        /// What a sketch is, for <see cref="FailEachAllocation"/> to hold it to after a failure:
        /// its payload, and what the payload leaves out, which rows it holds and how they are
        /// packed.
        /// </summary>
        private sealed class State
        {
            private readonly ulong total;
            private readonly int width;
            private readonly int records;
            private readonly byte[] payload;
            private readonly (int, int) tuning;
            private readonly ValeCounterArray[] rows;
            private readonly int[] onTails;

            internal State(SublimeCountMinSketch sketch)
            {
                this.total = sketch.TotalCount();
                this.width = sketch.Width;
                this.records = RecordsOf(sketch);
                this.payload = sketch.ToByteArray();
                this.tuning = (sketch.CountersPerChunk, sketch.StubBits);
                this.rows = RowsOf(sketch).ToArray();
                this.onTails = this.rows.Select(row => row.ChunksWithTails).ToArray();
            }

            /// <summary>Notes everything about the sketch that is not as it was.</summary>
            internal void Holds(List<string> wrong, string at, SublimeCountMinSketch sketch)
            {
                Check(wrong, $"{at}, the total", this.total, () => sketch.TotalCount());
                Check(wrong, $"{at}, the width", this.width, () => sketch.Width);
                Check(wrong, $"{at}, the records", this.records, () => RecordsOf(sketch));
                Same(wrong, $"{at}, the payload", this.payload, () => sketch.ToByteArray());
                Check(wrong, $"{at}, the tuning", this.tuning,
                    () => (sketch.CountersPerChunk, sketch.StubBits));

                var rows = RowsOf(sketch);
                for (var i = 0; i < this.rows.Length; i++)
                {
                    var row = i;
                    Check(wrong, $"{at}, row {row} is the row it was", true,
                        () => ReferenceEquals(this.rows[row], rows[row]));
                    Check(wrong, $"{at}, row {row}'s chunks on tails", this.onTails[row],
                        () => rows[row].ChunksWithTails);
                }
            }
        }

        /// <summary>
        /// Notes whether the sketch is packed differently from <paramref name="expected"/>: at
        /// another tuning, in another size, or with other chunks on tails, its records'
        /// included. None of that is in the payload.
        /// </summary>
        private static void PackedAlike(
            List<string> wrong, string at, SublimeCountMinSketch expected, SublimeCountMinSketch sketch)
        {
            Check(wrong, $"{at}, the tuning", (expected.CountersPerChunk, expected.StubBits),
                () => (sketch.CountersPerChunk, sketch.StubBits));
            Check(wrong, $"{at}, the size in bytes", expected.SizeInBytes, () => sketch.SizeInBytes);
            Check(wrong, $"{at}, the chunks on tails", TailsOf(expected), () => TailsOf(sketch));
        }

        /// <summary>How many chunks of each row and of each record's rows are on tails.</summary>
        private static string TailsOf(SublimeCountMinSketch sketch) =>
            string.Join(" / ", RecordRowsOf(sketch).Append(RowsOf(sketch))
                .Select(rows => string.Join(",", rows.Select(row => row.ChunksWithTails))));

        private static int OnTails(IEnumerable<ValeCounterArray> rows) =>
            rows.Sum(row => row.ChunksWithTails);

        /// <summary>
        /// Throws an <see cref="OutOfMemoryException"/> at the allocation numbered
        /// <c>failing</c>, counting from nought, and counts the allocations it has seen.
        /// </summary>
        private sealed class FailAt
        {
            private readonly int failing;

            internal FailAt(int failing)
            {
                this.failing = failing;
            }

            internal int Seen { get; private set; }

            internal void Before()
            {
                if (this.Seen++ == this.failing)
                {
                    throw new OutOfMemoryException(
                        $"The test failed allocation {this.failing}.");
                }
            }
        }

        private static bool RunsOutOfMemory(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch (OutOfMemoryException)
            {
                return true;
            }
        }

        private static bool Add(SublimeCountMinSketch sketch, byte[] key)
        {
            sketch.Add(key);
            return true;
        }

        private static int RecordsOf(SublimeCountMinSketch sketch) => RecordRowsOf(sketch).Count;

        private static List<ValeCounterArray[]> RecordRowsOf(SublimeCountMinSketch sketch) =>
            (List<ValeCounterArray[]>)FieldOf(sketch, "records");

        private static ValeCounterArray[] RowsOf(SublimeCountMinSketch sketch) =>
            (ValeCounterArray[])FieldOf(sketch, "rows");

        private static object FieldOf(SublimeCountMinSketch sketch, string name)
        {
            var field = typeof(SublimeCountMinSketch).GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"the sketch has no field {name}");
            return field.GetValue(sketch)!;
        }

        /// <summary>
        /// Notes what is wrong with <paramref name="actual"/>, including a throw, so that a
        /// sweep reports everything a broken sketch does and not only the first.
        /// </summary>
        private static void Check<T>(List<string> wrong, string what, T expected, Func<T> actual)
        {
            try
            {
                var got = actual();
                if (!EqualityComparer<T>.Default.Equals(expected, got))
                {
                    wrong.Add($"{what} is {got}, where it should be {expected}.");
                }
            }
            catch (Exception e)
            {
                wrong.Add($"{what} threw {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// <see cref="Check{T}"/>, for a payload.
        /// </summary>
        private static void Same(List<string> wrong, string what, byte[] expected, Func<byte[]> actual)
        {
            try
            {
                Differs(wrong, what, expected, actual());
            }
            catch (Exception e)
            {
                wrong.Add($"{what} threw {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// Notes whether the sketch's payload fails to read back, or reads back as a sketch
        /// that writes anything else. A sketch built with a hash of the test's own is read back
        /// with it.
        /// </summary>
        private static void ReadsBack(
            List<string> wrong, string what, SublimeCountMinSketch sketch,
            Func<ReadOnlySpan<byte>, ulong> hash = null)
        {
            try
            {
                var written = sketch.ToByteArray();
                var read = hash == null
                    ? Persistence.FromByteArray<SublimeCountMinSketch>(written)
                    : Persistence.FromByteArray<SublimeCountMinSketch>(written, hash);
                Differs(wrong, what, written, read.ToByteArray());
            }
            catch (Exception e)
            {
                wrong.Add($"{what} threw {e.GetType().Name}: {e.Message}");
            }
        }

        private static void Differs(List<string> wrong, string what, byte[] expected, byte[] got)
        {
            var at = 0;
            while (at < expected.Length && at < got.Length && expected[at] == got[at])
            {
                at++;
            }

            if (at < expected.Length || at < got.Length)
            {
                wrong.Add($"{what} differs from byte {at}, and is {got.Length} bytes " +
                    $"where it should be {expected.Length}.");
            }
        }
    }
}
