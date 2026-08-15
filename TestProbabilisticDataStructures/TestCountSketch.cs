using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestCountSketch
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        [TestMethod]
        public void TestANewSketchHasSeenNothing()
        {
            var sketch = new CountSketch(0.01, 0.01);

            Assert.AreEqual(0L, sketch.Count(Key("anything")));
        }

        [TestMethod]
        public void TestSomethingAddedIsCounted()
        {
            var sketch = new CountSketch(0.01, 0.01);

            for (var i = 0; i < 500; i++)
            {
                sketch.Add(Key("alpha"));
            }

            Assert.AreEqual(500L, sketch.Count(Key("alpha")));
        }
        /// <summary>
        /// The reason to have this beside <see cref="CountMinSketch"/>.
        /// <para>
        /// A Count-Min Sketch takes the minimum of cells that collisions can only have
        /// pushed up, so its error grows with the <b>total weight</b> of the stream. Ask
        /// it about something rare in a stream carrying two million observations and the
        /// answer is mostly other items. This takes a median of signed cells, so
        /// collisions cancel rather than accumulate.
        /// </para>
        /// <para>
        /// The two size differently for the same epsilon -- Count-Min bounds its error
        /// against the stream's L1 norm and this one against its L2 norm -- so comparing
        /// them at equal epsilon would hand one of them several times the memory. These
        /// are matched on <b>shape</b>, and the test asserts that before comparing
        /// anything.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestARareItemIsNotInflatedByTheRestOfTheStream()
        {
            var countMin = new CountMinSketch(0.001, 0.01);
            var countSketch = new CountSketch(0.0192, 0.01);

            Assert.AreEqual(countMin.Depth, countSketch.Depth(), "the two are not the same shape");
            Assert.IsLessThan(1.05, (double)countSketch.Width() / countMin.Width,
                $"widths differ too much to compare: {countMin.Width} against {countSketch.Width()}");

            // Twenty thousand items into about 2,700 columns, so a collision is the
            // situation rather than bad luck.
            for (var item = 0; item < 20_000; item++)
            {
                var key = Key($"item{item}");
                for (var i = 0; i < 100; i++)
                {
                    countMin.Add(key);
                    countSketch.Add(key);
                }
            }

            for (var i = 0; i < 10; i++)
            {
                countMin.Add(Key("rare"));
                countSketch.Add(Key("rare"));
            }

            var minError = Math.Abs((long)countMin.Count(Key("rare")) - 10);
            var sketchError = Math.Abs(countSketch.Count(Key("rare")) - 10);

            Assert.IsGreaterThan(100L, minError,
                $"Count-Min was only off by {minError} here, so this comparison no longer " +
                "demonstrates anything -- find a stream where its bias shows");

            Assert.IsLessThan(minError / 2, sketchError,
                $"asked about an item seen 10 times among 2,000,000 observations: " +
                $"Count-Min was off by {minError}, this was off by {sketchError}");
        }

        /// <summary>
        /// And where the stream really is dominated by a few heavy items, Count-Min is
        /// accurate about those -- which is what stops the test above from being read as
        /// "Count-Min is worse". It is not. It is worse at one thing.
        /// </summary>
        [TestMethod]
        public void TestBothAreAccurateAboutAGenuineHeavyHitter()
        {
            var countMin = new CountMinSketch(0.001, 0.01);
            var countSketch = new CountSketch(0.0192, 0.01);

            for (var item = 0; item < 20_000; item++)
            {
                var key = Key($"item{item}");
                var weight = item < 50 ? 2000 : 1;
                for (var i = 0; i < weight; i++)
                {
                    countMin.Add(key);
                    countSketch.Add(key);
                }
            }

            Assert.IsLessThan(200L, Math.Abs((long)countMin.Count(Key("item0")) - 2000));
            Assert.IsLessThan(200L, Math.Abs(countSketch.Count(Key("item0")) - 2000));
        }

        /// <summary>
        /// Errors go both ways, which is what "unbiased" means and what a Count-Min
        /// Sketch cannot do -- its estimate is never below the truth.
        /// </summary>
        [TestMethod]
        public void TestErrorsFallOnBothSidesOfTheTruth()
        {
            var sketch = new CountSketch(0.01, 0.01);

            for (var item = 0; item < 20_000; item++)
            {
                sketch.Add(Key($"item{item}"));
            }

            var over = 0;
            var under = 0;
            for (var item = 0; item < 20_000; item++)
            {
                var estimate = sketch.Count(Key($"item{item}"));
                if (estimate > 1) over++;
                if (estimate < 1) under++;
            }

            Assert.IsGreaterThan(0, under,
                "no estimate ever fell below the truth, which is a one-sided error and " +
                "the thing CountMinSketch already does");
            Assert.IsGreaterThan(0, over);
        }

        /// <summary>
        /// Removal, which a Count-Min Sketch cannot do: its cells can only rise, so
        /// subtracting from one that a collision inflated takes the count below where it
        /// should be and leaves it there.
        /// </summary>
        [TestMethod]
        public void TestThingsCanBeRemovedAgain()
        {
            var sketch = new CountSketch(0.01, 0.01);

            sketch.Add(Key("alpha"), 500);
            Assert.AreEqual(500L, sketch.Count(Key("alpha")));

            sketch.Add(Key("alpha"), -200);
            Assert.AreEqual(300L, sketch.Count(Key("alpha")));

            sketch.Add(Key("alpha"), -300);
            Assert.AreEqual(0L, sketch.Count(Key("alpha")));

            // And past zero, since a stream of departures is as real as one of arrivals.
            sketch.Add(Key("alpha"), -50);
            Assert.AreEqual(-50L, sketch.Count(Key("alpha")));
        }

        [TestMethod]
        public void TestMergeCountsBothStreams()
        {
            var a = new CountSketch(0.01, 0.01);
            var b = new CountSketch(0.01, 0.01);

            a.Add(Key("shared"), 300);
            b.Add(Key("shared"), 200);
            b.Add(Key("onlyB"), 400);

            a.Merge(b);

            Assert.AreEqual(500L, a.Count(Key("shared")));
            Assert.AreEqual(400L, a.Count(Key("onlyB")));

            // The sketch merged in is untouched.
            Assert.AreEqual(200L, b.Count(Key("shared")));
        }

        [TestMethod]
        public void TestMergeRejectsADifferentShape()
        {
            var ex = Assert.ThrowsExactly<ArgumentException>(
                () => new CountSketch(0.01, 0.01).Merge(new CountSketch(0.05, 0.01)));
            StringAssert.Contains(ex.Message, "must match");

            Assert.ThrowsExactly<ArgumentNullException>(
                () => new CountSketch(0.01, 0.01).Merge(null!));
        }

        [TestMethod]
        public void TestRoundTripsThroughPersistence()
        {
            var sketch = new CountSketch(0.05, 0.01);
            for (var i = 0; i < 2000; i++)
            {
                sketch.Add(Key($"k{i}"), i % 7);
            }

            // A negative cell, which is the case a sketch storing unsigned counts could
            // not carry back.
            sketch.Add(Key("negative"), -5000);

            var restored = Persistence.FromByteArray<CountSketch>(sketch.ToByteArray());

            Assert.AreEqual(sketch.Width(), restored.Width());
            Assert.AreEqual(sketch.Depth(), restored.Depth());
            Assert.AreEqual(sketch.Count(Key("negative")), restored.Count(Key("negative")));

            for (var i = 0; i < 2000; i++)
            {
                Assert.AreEqual(sketch.Count(Key($"k{i}")), restored.Count(Key($"k{i}")),
                    $"the restored sketch disagreed about k{i}");
            }

            // And keeps counting.
            sketch.Add(Key("later"), 9);
            restored.Add(Key("later"), 9);
            Assert.AreEqual(sketch.Count(Key("later")), restored.Count(Key("later")));
        }

        [TestMethod]
        public void TestAnImpossiblePayloadIsRefused()
        {
            var clean = new CountSketch(0.05, 0.01).Add(Key("a")).ToByteArray();

            var bad = (byte[])clean.Clone();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(14), 0);
            var crc = new System.IO.Hashing.Crc32();
            crc.Append(bad.AsSpan(4, bad.Length - 8));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                bad.AsSpan(bad.Length - 4), crc.GetCurrentHashAsUInt32());

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<CountSketch>(bad));
            StringAssert.Contains(ex.Message, "cells");
        }

        [TestMethod]
        public void TestBadArgumentsAreRefused()
        {
            foreach (var bad in new[] { 0.0, -0.1, double.NaN })
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CountSketch(bad, 0.01));
            }

            foreach (var bad in new[] { 0.0, 1.0, 2.0, -0.5, double.NaN })
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CountSketch(0.01, bad));
            }

            var sketch = new CountSketch(0.01, 0.01);
            Assert.ThrowsExactly<ArgumentNullException>(() => sketch.Add(null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => sketch.Count(null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => sketch.SetHash(null!));

            sketch.Add(Key("a"));
            Assert.ThrowsExactly<InvalidOperationException>(() => sketch.SetHash(d => 1UL));
        }

    }
}
