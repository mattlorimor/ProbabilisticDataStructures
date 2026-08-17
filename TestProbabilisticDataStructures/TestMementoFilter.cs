using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Tests for <see cref="MementoFilter"/>, against Eslami and Dayan, "Memento
    /// Filter: A Fast, Dynamic, and Robust Range Filter" (SIGMOD 2025).
    /// </summary>
    /// <remarks>
    /// What this structure claims over <see cref="Grafite"/> is that it can be changed
    /// after it is built without giving up robustness, so the tests that matter are
    /// the ones combining the two: that the false positive rate on queries aimed at
    /// the keys is no worse than on random ones, and that it stays that way after
    /// inserts, deletes and growth.
    /// </remarks>
    [TestClass]
    public class TestMementoFilter
    {
        private static SortedSet<ulong> RandomKeys(int count, long universe, int seed)
        {
            var random = new Random(seed);
            var keys = new SortedSet<ulong>();
            while (keys.Count < count)
            {
                keys.Add((ulong)random.NextInt64(0, universe));
            }
            return keys;
        }

        /// <summary>
        /// A range of one is a point query, which an ordinary filter answers in less
        /// space; beyond 2^32 the mementos cost more than the keys they describe.
        /// </summary>
        [TestMethod]
        public void TestUnusableRangeSizeIsRefused()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new MementoFilter(1, 8));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new MementoFilter((1UL << 32) + 1, 8));
        }

        /// <summary>
        /// A range that runs backwards is a caller error rather than an empty range.
        /// </summary>
        [TestMethod]
        public void TestBackwardsRangeIsRefused()
        {
            var filter = new MementoFilter(256, 8);
            filter.Add(10);

            Assert.ThrowsExactly<ArgumentException>(() => filter.TestRange(10, 5));
        }

        /// <summary>
        /// The contract a filter cannot break, for points and for ranges, through
        /// enough insertions that the filter has grown several times.
        /// </summary>
        [TestMethod]
        public void TestThereAreNoFalseNegatives()
        {
            var keys = RandomKeys(50000, 10_000_000_000, seed: 42);
            var filter = new MementoFilter(maxRangeSize: 256, fingerprintBits: 8,
                initialCapacity: 1024);
            foreach (var key in keys)
            {
                filter.Add(key);
            }

            Assert.IsTrue(filter.ExpansionCount() >= 4,
                "Premise: the filter grew several times while being filled.");

            foreach (var key in keys)
            {
                Assert.IsTrue(filter.Test(key),
                    $"The stored key {key} was reported absent.");
            }

            var ordered = keys.ToArray();
            var random = new Random(11);
            for (var i = 0; i < 20000; i++)
            {
                var key = ordered[random.Next(ordered.Length)];
                var low = key - (ulong)random.Next(0, 100);
                var high = key + (ulong)random.Next(0, 100);
                Assert.IsTrue(filter.TestRange(low, high),
                    $"The range [{low}, {high}] holds the stored key {key} and was " +
                    "reported empty.");
            }
        }

        /// <summary>
        /// The claim that makes this worth having: queries aimed squarely at the keys
        /// are no more likely to be wrong than queries placed at random.
        /// </summary>
        /// <remarks>
        /// This is the workload that collapses the heuristic range filters, and the
        /// reason it does not collapse this one is that a memento is a piece of the
        /// key rather than a hash of it. A query just past a key is compared against
        /// where that key actually sits inside its block, so being near the data buys
        /// an adversary nothing.
        /// </remarks>
        [TestMethod]
        public void TestCorrelatedQueriesAreNoWorseThanRandomOnes()
        {
            var keys = RandomKeys(50000, 10_000_000_000, seed: 3);
            var filter = new MementoFilter(256, 8, 1024);
            foreach (var key in keys)
            {
                filter.Add(key);
            }

            var random = new Random(5);
            var randomPositives = 0;
            var randomTrials = 0;
            while (randomTrials < 20000)
            {
                var low = (ulong)random.NextInt64(0, 10_000_000_000);
                var high = low + 63;
                if (keys.GetViewBetween(low, high).Count != 0)
                {
                    continue;
                }
                randomTrials++;
                if (filter.TestRange(low, high))
                {
                    randomPositives++;
                }
            }

            var nearPositives = 0;
            var nearTrials = 0;
            foreach (var key in keys)
            {
                var low = key + 1;
                var high = key + 64;
                if (keys.GetViewBetween(low, high).Count != 0)
                {
                    continue;
                }
                nearTrials++;
                if (filter.TestRange(low, high))
                {
                    nearPositives++;
                }
            }

            var randomRate = (double)randomPositives / randomTrials;
            var nearRate = (double)nearPositives / nearTrials;

            Assert.IsTrue(nearTrials > 10000, "Premise: most gaps were truly empty.");
            Assert.IsTrue(randomRate > 0.0,
                "Premise: random ranges produce some false positives, or there is no " +
                "baseline to compare against.");
            Assert.IsTrue(nearRate <= randomRate * 3.0,
                $"Ranges placed immediately after each key were wrong {nearRate:P3} " +
                $"of the time against {randomRate:P3} for random ranges. A gap of " +
                "that size is what a filter looks like when its accuracy depends on " +
                "queries not resembling the data.");
        }

        /// <summary>
        /// A range that holds nothing but sits inside a block that does is answered
        /// empty. Without the mementos it could not be.
        /// </summary>
        [TestMethod]
        public void TestAnEmptyRangeInsideAnOccupiedBlockIsAnsweredEmpty()
        {
            var filter = new MementoFilter(maxRangeSize: 256, fingerprintBits: 12);

            // One key near the bottom of its block; the block spans 0 to 255.
            filter.Add(5);

            Assert.IsTrue(filter.TestRange(0, 10), "The range holds the key.");
            Assert.IsFalse(filter.TestRange(100, 200),
                "This range is inside the same block as the stored key but nowhere " +
                "near it. A filter storing only the block would answer possibly here, " +
                "which is exactly what the mementos exist to avoid.");
        }

        /// <summary>
        /// Removing a key takes away that key and nothing else, and the filter goes on
        /// answering correctly afterwards.
        /// </summary>
        [TestMethod]
        public void TestRemovingLeavesEverythingElseIntact()
        {
            var filter = new MementoFilter(256, 8, 1024);
            for (ulong i = 0; i < 40000; i++)
            {
                filter.Add(i * 7);
            }

            var removed = 0;
            for (ulong i = 0; i < 40000; i += 2)
            {
                if (filter.TestAndRemove(i * 7))
                {
                    removed++;
                }
            }

            Assert.AreEqual(20000, removed,
                "Every key removed here was added, so every removal should have found " +
                "something.");

            for (ulong i = 1; i < 40000; i += 2)
            {
                Assert.IsTrue(filter.Test(i * 7),
                    $"Key {i * 7} was never removed but is now reported absent.");
            }
        }

        /// <summary>
        /// The filter grows rather than filling up, and keys added before a growth
        /// survive it.
        /// </summary>
        [TestMethod]
        public void TestItGrowsAndKeepsWhatItHad()
        {
            var filter = new MementoFilter(256, 8, initialCapacity: 64);
            var startingCapacity = filter.Capacity();

            for (ulong i = 0; i < 50000; i++)
            {
                filter.Add(i * 3);
            }

            Assert.IsTrue(filter.Capacity() > startingCapacity * 100,
                "The filter did not grow to hold fifty thousand keys.");
            Assert.IsTrue(filter.ExpansionCount() >= 8, "Premise: it doubled often.");

            for (ulong i = 0; i < 50000; i++)
            {
                Assert.IsTrue(filter.Test(i * 3),
                    $"Key {i * 3} was lost during growth.");
            }
        }

        /// <summary>
        /// A range wider than the filter was promised is still answered without false
        /// negatives, by looking at every block it covers.
        /// </summary>
        [TestMethod]
        public void TestRangesWiderThanPromisedStillFindTheirKeys()
        {
            var filter = new MementoFilter(maxRangeSize: 64, fingerprintBits: 10);
            filter.Add(10000);

            // Twenty blocks wide, where the filter promised one.
            Assert.IsTrue(filter.TestRange(9800, 10200),
                "A range wider than the promised maximum spans several blocks, and " +
                "the key sits in one of the middle ones. Accuracy may suffer over a " +
                "range this wide; correctness may not.");
            Assert.IsFalse(filter.TestRange(20000, 20400),
                "A wide range holding nothing should still come back empty.");
        }

        /// <summary>
        /// Keys arriving in any order must all be found, including when they land in
        /// one block back to front.
        /// </summary>
        /// <remarks>
        /// A box keeps its keys in order because the encoding stores the smallest and
        /// largest at the front and uses them to rule a box out before reading the
        /// rest. Insert them unsorted and those two stop being the extremes, so a
        /// query is excluded from a box that does hold its answer -- a false negative.
        /// <para>
        /// Every other test here happened to add keys ascending, which kept boxes
        /// ordered whether the code sorted them or not. This one does not.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestKeysArrivingOutOfOrderAreStillFound()
        {
            var filter = new MementoFilter(maxRangeSize: 256, fingerprintBits: 12);

            // One block, filled back to front, then shuffled arrivals in another.
            var descending = new ulong[] { 250, 200, 150, 100, 50, 10, 1 };
            foreach (var key in descending)
            {
                filter.Add(key);
            }

            var shuffled = new ulong[] { 700, 500, 900, 300, 800, 400, 600 };
            foreach (var key in shuffled)
            {
                filter.Add(key);
            }

            foreach (var key in descending)
            {
                Assert.IsTrue(filter.Test(key),
                    $"Key {key} was added and then reported absent. Its block was " +
                    "filled back to front.");
            }
            foreach (var key in shuffled)
            {
                Assert.IsTrue(filter.Test(key), $"Key {key} was lost.");
            }

            // Ranges covering only part of a block must still find what is inside
            // them, which is what the ordering of a box is for.
            Assert.IsTrue(filter.TestRange(240, 255), "The range holds 250.");
            Assert.IsTrue(filter.TestRange(0, 20), "The range holds 1 and 10.");
            Assert.IsFalse(filter.TestRange(251, 255), "Nothing sits above 250.");
            Assert.IsFalse(filter.TestRange(201, 249), "Nothing sits between 200 and 250.");
        }

        /// <summary>
        /// Keys sharing a block cost less than keys that do not, which is the entire
        /// point of packing them into one box.
        /// </summary>
        /// <remarks>
        /// This also watches the accounting. The table doubles when its slots are
        /// three quarters used, so a filter that miscounted how many it had freed
        /// would expand on a schedule of its own and this comparison would flatten
        /// out.
        /// </remarks>
        [TestMethod]
        public void TestSharingABlockCostsLessThanNotSharing()
        {
            ulong TableSizeFor(int keysPerBlock)
            {
                const int keys = 128000;
                var filter = new MementoFilter(256, 8, initialCapacity: 1024);
                var blocks = keys / keysPerBlock;
                for (var block = 0; block < blocks; block++)
                {
                    for (var j = 0; j < keysPerBlock; j++)
                    {
                        filter.Add(((ulong)block * 256) + (ulong)(j * 251 % 256));
                    }
                }
                return filter.SizeInBytes();
            }

            var spread = TableSizeFor(1);
            var shared = TableSizeFor(64);

            // Measured at almost exactly half: 655,376 bytes against 327,696. The
            // slack is for the spare word each table carries past its slots, which
            // is what stops the ratio being a clean two.
            Assert.IsTrue(shared <= spread * 0.55,
                $"The same keys took {spread} bytes spread one to a block and " +
                $"{shared} bytes packed sixty-four to a block, a ratio of " +
                $"{(double)spread / shared:F2}. Sharing a fingerprint across a " +
                "block's keys is what this encoding is for; if it costs the same " +
                "either way it is not happening.");
        }

        /// <summary>
        /// A filter written and read back is the same filter, and goes on growing the
        /// way the original would have.
        /// </summary>
        [TestMethod]
        public void TestRoundTripIsByteExactAndKeepsWorking()
        {
            var filter = new MementoFilter(256, 8, 1024);
            for (ulong i = 0; i < 40000; i++)
            {
                filter.Add(i * 11);
            }

            var written = filter.ToByteArray();
            var restored = MementoFilter.ReadFrom(new MemoryStream(written));

            CollectionAssert.AreEqual(written, restored.ToByteArray());
            Assert.AreEqual(filter.Count(), restored.Count());
            Assert.AreEqual(filter.MaxRangeSize, restored.MaxRangeSize);

            for (ulong i = 0; i < 40000; i++)
            {
                Assert.IsTrue(restored.Test(i * 11), $"restored filter lost {i * 11}");
            }

            for (ulong i = 40000; i < 80000; i++)
            {
                filter.Add(i * 11);
                restored.Add(i * 11);
            }

            CollectionAssert.AreEqual(filter.ToByteArray(), restored.ToByteArray(),
                "A restored filter must go on making the same decisions, expansions " +
                "included.");
        }

        /// <summary>
        /// The filter holds numbers rather than bytes, so a caller offering a hash
        /// function has misunderstood what they are reading.
        /// </summary>
        [TestMethod]
        public void TestReadingWithASuppliedHashIsRefused()
        {
            var filter = new MementoFilter(256, 8);
            filter.Add(1);

            Assert.ThrowsExactly<InvalidDataException>(
                () => MementoFilter.ReadFrom(
                    new MemoryStream(filter.ToByteArray()), data => 0UL));
        }

        /// <summary>
        /// Reset returns the filter to the size it was built at.
        /// </summary>
        [TestMethod]
        public void TestResetShrinksBackToTheStartingSize()
        {
            var filter = new MementoFilter(256, 8, initialCapacity: 64);
            var startingCapacity = filter.Capacity();

            for (ulong i = 0; i < 20000; i++)
            {
                filter.Add(i);
            }

            filter.Reset();

            Assert.AreEqual(0UL, filter.Count());
            Assert.AreEqual(startingCapacity, filter.Capacity());
            Assert.AreEqual(0u, filter.ExpansionCount());
            Assert.IsFalse(filter.Test(1));
        }

        /// <summary>
        /// A key's memento is its own low bits, so two keys in the same block are told
        /// apart exactly rather than probabilistically.
        /// </summary>
        [TestMethod]
        public void TestKeysInOneBlockAreDistinguishedExactly()
        {
            var filter = new MementoFilter(maxRangeSize: 256, fingerprintBits: 12);

            // Every key in one block, at known positions.
            foreach (var position in new ulong[] { 3, 40, 41, 200, 255 })
            {
                filter.Add(position);
            }

            foreach (var position in new ulong[] { 3, 40, 41, 200, 255 })
            {
                Assert.IsTrue(filter.Test(position));
            }

            // The gaps between them are known to be empty, and the filter says so.
            Assert.IsFalse(filter.TestRange(4, 39));
            Assert.IsFalse(filter.TestRange(42, 199));
            Assert.IsFalse(filter.TestRange(201, 254));
        }
    }
}
