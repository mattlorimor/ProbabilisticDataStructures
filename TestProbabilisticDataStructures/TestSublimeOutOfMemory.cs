using System;
using System.Collections.Generic;
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
    /// <see cref="SublimeCountMinSketch.BeforeAllocatingRow"/>, that every row a doubling or
    /// a folding allocates passes through on its way to the allocation. Each test fails those
    /// allocations one at a time, the first, then the second, and so on until the resize
    /// gets all it asks for, and holds the sketch to what it was at every one of them.
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
                sketch.BeforeAllocatingRow = hook.Before;
                var threw = RunsOutOfMemory(() => sketch.Add(Doubler));
                sketch.BeforeAllocatingRow = null;

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
        /// A folding that runs out of memory leaves the rows, their width and the record they
        /// fold back by as they were, so the sketch reads back and can still fold. The removal
        /// that called for it is taken before the rows fold, and stays taken: it is the folding
        /// that is all or nothing. The next removal folds the rows into what they would have
        /// been had nothing failed.
        /// </summary>
        [TestMethod]
        public void TestAFoldingThatRunsOutOfMemoryLeavesTheRowsAsTheyWere()
        {
            var twin = OneShortOfFolding();
            twin.Remove(Folder);
            Assert.AreEqual(64, twin.Width, "the 2,441st removal did not fold the rows");
            twin.Remove(Folder);
            var folded = twin.ToByteArray();

            var wrong = new List<string>();
            var failures = 0;
            for (var failing = 0; ; failing++)
            {
                Assert.IsTrue(failing < MostAllocations,
                    $"a folding made more than {MostAllocations} allocations");

                var sketch = OneShortOfFolding();
                var hook = new FailAt(failing);
                sketch.BeforeAllocatingRow = hook.Before;
                var threw = RunsOutOfMemory(() => sketch.Remove(Folder));
                sketch.BeforeAllocatingRow = null;

                if (!threw)
                {
                    Assert.AreEqual(failing, hook.Seen,
                        "the removal finished without making the allocation it was to fail");
                    Assert.AreEqual(64, sketch.Width, "a folding that got its memory did not fold");
                    break;
                }

                failures++;
                var at = $"Failing allocation {failing}";
                Check(wrong, $"{at}, the total", 2_559UL, () => sketch.TotalCount());
                Check(wrong, $"{at}, the count", 2_559UL, () => sketch.Count(Folder));
                Check(wrong, $"{at}, the width", 128, () => sketch.Width);
                Check(wrong, $"{at}, the records", 1, () => RecordsOf(sketch));
                ReadsBack(wrong, $"{at}, the payload read back", sketch);

                Check(wrong, $"{at}, the next removal", true, () => Remove(sketch, Folder));
                Check(wrong, $"{at}, then the width", 64, () => sketch.Width);
                Check(wrong, $"{at}, then the records", 0, () => RecordsOf(sketch));
                Same(wrong, $"{at}, then the payload", folded, () => sketch.ToByteArray());
                ReadsBack(wrong, $"{at}, then the payload read back", sketch);
            }

            Assert.IsTrue(failures > twin.Depth,
                $"the sweep failed {failures} allocations of a folding of {twin.Depth} rows");
            Assert.AreEqual(0, wrong.Count, string.Join(Environment.NewLine, wrong));
        }

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

        private static bool Remove(SublimeCountMinSketch sketch, byte[] key)
        {
            sketch.Remove(key);
            return true;
        }

        private static int RecordsOf(SublimeCountMinSketch sketch)
        {
            var field = typeof(SublimeCountMinSketch).GetField("records",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "the sketch has no field records");
            return ((List<ValeCounterArray[]>)field.GetValue(sketch)!).Count;
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
        /// that writes anything else.
        /// </summary>
        private static void ReadsBack(List<string> wrong, string what, SublimeCountMinSketch sketch)
        {
            try
            {
                var written = sketch.ToByteArray();
                var read = Persistence.FromByteArray<SublimeCountMinSketch>(written);
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
