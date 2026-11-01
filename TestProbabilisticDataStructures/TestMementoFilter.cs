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

        private static ulong LowHashBits(ulong prefix, int bits) =>
            MementoFilter.HashPrefix(prefix) & ((1UL << bits) - 1);

        /// <summary>
        /// Removing a key takes the matching box with the longest fingerprint, so a key
        /// added before an expansion keeps its only record when a key added after it is
        /// removed.
        /// </summary>
        /// <remarks>
        /// The rule is the paper's, not this implementation's. Section 5, "Deletions":
        /// "When deleting a key y, Memento filter removes a memento equal to m(y) from
        /// the keepsake box with the longest matching fingerprint. The reason is that
        /// deleting a memento with a shorter associated fingerprint may cause false
        /// negatives." The authors' reference implementation (n3slami/Memento_Filter,
        /// checked 2026-11-01) states the same rule in include/memento.h, in the comment
        /// above <c>qf_delete_single</c>: "the keepsake box with the longest matching
        /// fingerprint". What its code does depends on the branch. On <c>master</c>
        /// (ded520c), the dynamic-only filter, fingerprints have one fixed width and
        /// <c>next_matching_fingerprint_in_run</c> tests them for equality, so the
        /// backwards walk over the matching boxes in <c>qf_delete_single</c>
        /// (src/memento.c) chooses among boxes with equal fingerprints and does not
        /// exercise longest-match. On <c>expandable</c> (797a382), where each expansion
        /// shortens a fingerprint by a bit and a length marker is stored with it, the
        /// same function matches a stored fingerprint over its own length and a run is
        /// sorted shorter before longer, so the same backwards walk reaches the longest
        /// matching box first, which is the rule this test holds.
        /// <para>
        /// These are the keys #145 was reported with. Four slots and two-bit
        /// fingerprints put the first expansion at the fourth add. Key 0 arrives before
        /// it, and its box remembers four bits of its block's hash; key 26 arrives
        /// after, and its box remembers five. The two blocks agree on the first four
        /// bits and not on the fifth, and both keys sit at memento zero. So removing 26
        /// matches both boxes, and only the younger one is 26's alone. The older box
        /// comes first in the run, and taking it leaves key 0 with nothing to answer
        /// for it.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestRemovalTakesTheLongestMatchingFingerprint()
        {
            // One memento bit, so key k is at position k & 1 of block k >> 1.
            var filter = new MementoFilter(
                maxRangeSize: 2, fingerprintBits: 2, initialCapacity: 1);
            Assert.AreEqual(4UL, filter.Capacity(), "Premise: four slots.");
            Assert.AreEqual(LowHashBits(0, 4), LowHashBits(13, 4),
                "Premise: blocks 0 and 13 agree on four bits of hash.");
            Assert.AreNotEqual(LowHashBits(0, 5), LowHashBits(13, 5),
                "Premise: blocks 0 and 13 differ in the fifth bit.");

            filter.Add(0);
            filter.Add(2);
            filter.Add(4);
            Assert.AreEqual(0u, filter.ExpansionCount(), "Premise: no expansion yet.");
            filter.Add(26);
            Assert.AreEqual(1u, filter.ExpansionCount(),
                "Premise: key 26 arrived after the first expansion.");
            Assert.IsTrue(filter.Test(0), "Premise: key 0 is present before any removal.");

            Assert.IsTrue(filter.TestAndRemove(26),
                "Key 26 was added, so removing it succeeds.");
            Assert.IsTrue(filter.Test(0),
                "Key 0 was never removed and now tests absent. Removing 26 took the " +
                "older, shorter box, which was 0's only record, instead of the younger " +
                "box that was 26's alone.");
        }

        /// <summary>
        /// Across the chain of tables, removal takes the newest table's match, because it
        /// remembers more of the hash than any match in a later table -- even when its
        /// own fingerprint has run out.
        /// </summary>
        /// <remarks>
        /// The paper says only that deletions "must probe all the filters" (section 5,
        /// "Unbounded Expansions"). Which match to take across them follows from its
        /// deletion rule once "longest" counts everything a match remembers: address and
        /// fingerprint together, since the tables have different address widths. An
        /// entry reaches a later table by going void, and then it remembers no more of
        /// its hash than the address of the table it left, which has doubled since.
        /// Everything still in that table remembers more. InfiniFilter's reference
        /// implementation, whose chaining Memento borrows, deletes in this order: the
        /// main filter, then the secondary, then the chain newest first
        /// (nivdayan/FilterLibrary, filters/ChainedInfiniFilter.java, <c>delete</c>,
        /// checked 2026-11-01).
        /// <para>
        /// The scenario sets the two readings of "longest" against each other. Key j
        /// arrives first and goes void at the third expansion. In the second table it
        /// is its block's only record, with a full two-bit fingerprint over a two-bit
        /// address: four bits of hash. Key k arrives after the first expansion. By the
        /// third it has spent its whole fingerprint, but over a five-bit address: five
        /// bits. The two blocks agree on the four bits j's entry remembers, and both
        /// keys sit at memento zero, so removing k matches both. Taking the second
        /// table's entry, by asking the tables oldest first or by comparing fingerprint
        /// lengths alone, removes j's only record.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestRemovalAcrossTheChainTakesTheNewerTablesMatch()
        {
            var prefixes = Enumerable.Range(0, 4096).Select(p => (ulong)p).ToArray();
            var j = prefixes[0];
            var k = prefixes.First(
                p => p != j && LowHashBits(p, 4) == LowHashBits(j, 4));

            // Fillers drive the expansions. They never share a quotient with j or k at
            // any table size, and no two share a box, so each costs one slot and the
            // expansions come where the premises below say.
            var fillers = prefixes
                .Where(p => LowHashBits(p, 2) != LowHashBits(j, 2))
                .GroupBy(p => LowHashBits(p, 7))
                .Select(g => g.First())
                .Take(11)
                .ToArray();
            Assert.HasCount(11, fillers, "Premise: eleven fillers.");

            // One memento bit, so block p's key at memento zero is p << 1.
            var filter = new MementoFilter(
                maxRangeSize: 2, fingerprintBits: 2, initialCapacity: 1);
            filter.Add(j << 1);
            filter.Add(fillers[0] << 1);
            filter.Add(fillers[1] << 1);
            Assert.AreEqual(0u, filter.ExpansionCount(),
                "Premise: j arrived before any expansion.");
            filter.Add(k << 1);
            Assert.AreEqual(1u, filter.ExpansionCount(),
                "Premise: k arrived after the first.");
            for (var i = 2; i < fillers.Length; i++)
            {
                filter.Add(fillers[i] << 1);
            }
            Assert.AreEqual(3u, filter.ExpansionCount(), "Premise: three expansions.");
            Assert.AreEqual(2, filter.ChainLength(),
                "Premise: j's box went void into a second table.");

            var hj = MementoFilter.HashPrefix(j);
            var hk = MementoFilter.HashPrefix(k);
            var newer = filter.Segments[0];
            var older = filter.Segments[1];
            Assert.IsFalse(newer.HasMementoBetween(hj, 0, 0),
                "Premise: j has no record in the first table.");
            Assert.IsTrue(older.HasMementoBetween(hj, 0, 0),
                "Premise: j's only record is in the second table.");
            Assert.IsTrue(older.HasMementoBetween(hk, 0, 0),
                "Premise: k matches j's entry in the second table.");
            Assert.AreEqual(newer.FingerprintBits, MatchingAges(newer, hk, 0).Single(),
                "Premise: k's box in the first table has spent its whole fingerprint.");
            Assert.AreEqual(0, MatchingAges(older, hk, 0).Single(),
                "Premise: the second table's match carries a full fingerprint, so " +
                "comparing fingerprint lengths alone would choose it.");

            Assert.IsTrue(filter.TestAndRemove(k << 1),
                "k was added, so removing it succeeds.");
            Assert.IsTrue(filter.Test(j << 1),
                "j was never removed and now tests absent. Removing k took j's only " +
                "record from the second table instead of k's own box in the first, " +
                "which remembers five bits of hash to the second table's four.");
        }

        /// <summary>
        /// The ages of the boxes in a table that could answer for a key: those whose
        /// fingerprint matches its block's hash and that hold its memento.
        /// </summary>
        private static List<int> MatchingAges(
            MementoSegment segment, ulong hash, ulong memento)
        {
            var (quotient, _) = segment.Split(hash);
            return segment.Codec.Decode(segment.ReadRun(quotient))
                .Where(b => segment.FingerprintMatches(b.Fingerprint, hash) &&
                            b.Mementos.Contains(memento))
                .Select(b => segment.AgeOfFluid(b.Fingerprint))
                .ToList();
        }

        /// <summary>
        /// Long churn on small filters, through expansions and down the chain, never
        /// loses a key that is still held.
        /// </summary>
        /// <remarks>
        /// The two tests above are the shortest case of each rule. This runs them where
        /// they meet everything else: boxes of several keys, removals that empty a box,
        /// and voids shed into tables that expand in turn. The key universe is small, so
        /// blocks are shared and mementos repeat, which is what lets one removal match
        /// more than one entry. The run counts the removals that did, within one table
        /// and across tables, and refuses to pass if either kind never came up.
        /// </remarks>
        [TestMethod]
        public void TestChurnThroughExpansionsNeverLosesAHeldKey()
        {
            // Adds are this many in eight steps, the rest removals. A filter whose
            // fingerprints last four expansions needs its early keys to outlive four
            // doublings before anything goes void, so the last case adds faster.
            var cases = new (ulong Range, int Bits, uint Initial, long Universe, int Adds,
                int Seed)[]
            {
                (2, 2, 1, 256, 5, 1451),
                (4, 3, 4, 1024, 5, 1452),
                (16, 4, 4, 8192, 6, 1453),
            };
            const int Steps = 3000;

            foreach (var (range, bits, initial, universe, adds, seed) in cases)
            {
                var filter = new MementoFilter(range, bits, initial);
                var mementoBits = filter.MementoBits;
                var mementoMask = (1UL << mementoBits) - 1;
                var held = new List<ulong>();
                var schedule = new Random(seed);
                var longestChain = 1;
                var contestedInOneTable = 0;
                var contestedAcrossTables = 0;

                for (var step = 0; step < Steps; step++)
                {
                    if (held.Count == 0 || schedule.Next(8) < adds)
                    {
                        var added = (ulong)schedule.NextInt64(universe);
                        filter.Add(added);
                        held.Add(added);
                        longestChain = Math.Max(longestChain, filter.ChainLength());
                        continue;
                    }

                    var index = schedule.Next(held.Count);
                    var removed = held[index];
                    held[index] = held[^1];
                    held.RemoveAt(held.Count - 1);

                    var hash = MementoFilter.HashPrefix(removed >> mementoBits);
                    var tablesMatching = 0;
                    foreach (var segment in filter.Segments)
                    {
                        var matches =
                            MatchingAges(segment, hash, removed & mementoMask).Count;
                        if (matches > 1)
                        {
                            contestedInOneTable++;
                        }
                        if (matches > 0)
                        {
                            tablesMatching++;
                        }
                    }
                    if (tablesMatching > 1)
                    {
                        contestedAcrossTables++;
                    }

                    var what =
                        $"range {range}, {bits}-bit fingerprints, seed {seed}, step {step}";
                    Assert.IsTrue(filter.TestAndRemove(removed),
                        $"{what}: key {removed} is held, so removing it succeeds.");

                    foreach (var key in new HashSet<ulong>(held))
                    {
                        Assert.IsTrue(filter.Test(key),
                            $"{what}: removing {removed} left key {key} testing absent, " +
                            "though it is still held.");
                    }
                }

                Assert.AreEqual((ulong)held.Count, filter.Count(),
                    $"range {range}: the count drifted from the keys held.");
                Console.WriteLine($"range {range}, {bits}-bit: {filter.ExpansionCount()} " +
                    $"expansions, longest chain {longestChain}, {held.Count} held; " +
                    $"removals matching several boxes in a table {contestedInOneTable}, " +
                    $"in several tables {contestedAcrossTables}");
                Assert.IsGreaterThanOrEqualTo(2u, filter.ExpansionCount(),
                    $"Premise: the range {range} filter expanded.");
                Assert.IsGreaterThanOrEqualTo(2, longestChain,
                    $"Premise: the range {range} filter shed into a second table.");
                Assert.IsGreaterThan(0, contestedInOneTable,
                    $"Premise: some range {range} removal matched two boxes in a table.");
                Assert.IsGreaterThan(0, contestedAcrossTables,
                    $"Premise: some range {range} removal matched in several tables.");
            }
        }
    }
}
