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
        /// States whose payload <see cref="TestPayloadCount"/> holds the count to: each
        /// segment's widths and the memento's size its table, so two ranges and fingerprints,
        /// each empty and expanded: in place at eight bits, and at two into the chain of
        /// segments that fingerprints spent by expanding leave.
        /// </summary>
        internal static IEnumerable<PayloadState> PayloadStates()
        {
            foreach (var (range, fingerprintBits, keys, chained) in
                new[] { (256UL, 2, 2_000, true), (1UL << 20, 8, 400, false) })
            {
                var segments = chained ? "several segments" : "one segment";
                yield return PayloadState.Of($"range {range}, {fingerprintBits} bits, empty",
                    new MementoFilter(range, fingerprintBits, initialCapacity: 32));

                var expanded = new MementoFilter(range, fingerprintBits, initialCapacity: 32);
                foreach (var key in RandomKeys(keys, 1L << 40, 7)) expanded.Add(key);
                Assert.IsGreaterThan(0u, expanded.Expansions, "Premise: the filter expanded.");
                Assert.AreEqual(chained, expanded.Segments.Count > 1,
                    $"Premise: the filter expanded into {segments}.");
                yield return PayloadState.Of(
                    $"range {range}, {fingerprintBits} bits, expanded into {segments}", expanded);
            }
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
        /// A read holds each table once and is charged for it once (#327), as an
        /// <see cref="InfiniFilter"/> read is (#313). In the ordinary envelope, what it allocates
        /// and is charged beyond the payload it holds is the tables it builds, read into them and
        /// not first into arrays of their own and copied; in the large form, where the payload is
        /// not held, it is the tables alone.
        /// </summary>
        /// <remarks>
        /// Two-bit fingerprints are the narrowest, and an entry's runs out after the fewest
        /// expansions, so a filter built for 2,048 keys and given 48,000 keeps a table behind the
        /// first, of the size it had when they ran out. The slack, 4 KiB, is short of the first
        /// table and of the second, which a premise checks, so a second copy of either fails it,
        /// as one of every table does. Each figure is held from below too, to the payload and the tables,
        /// so that a measurement of nothing cannot pass. The reads are counted in a no-GC region
        /// (#293).
        /// </remarks>
        [TestMethod]
        public void TestAReadHoldsEachTableOnce()
        {
            const long Slack = 4 * 1024;

            var filter = new MementoFilter(64, 2, initialCapacity: 2048);
            for (ulong i = 0; i < 48_000; i++)
            {
                filter.Add(i * 7919);
            }

            long TableBytes(MementoSegment segment) => ReadBudget.ArrayOf((ulong)segment.Data.Length, sizeof(ulong));
            var tables = filter.Segments.Sum(TableBytes);
            Assert.IsGreaterThan(1, filter.Segments.Count, "premise: the chain does not hold tables behind the first");
            Assert.IsGreaterThan(Slack, TableBytes(filter.Segments[1]),
                $"premise: a copy of the second table could hide in the slack: tables of {string.Join(", ", filter.Segments.Select(TableBytes))} bytes");

            var bytes = filter.ToByteArray();
            long payload = bytes.Length - PersistenceFormat.EnvelopeLength;
            MementoFilter.ReadFrom(new MemoryStream(bytes, writable: false));
            var ordinary = NoGCRegion.Allocated(() => MementoFilter.ReadFrom(new MemoryStream(bytes, writable: false)));
            var charged = CapacityModel.ChargeOf<MementoFilter>(StructureId.MementoFilter, bytes);

            var large = LargeForm.WriteLarge(filter);
            Assert.IsTrue(LargeForm.IsLarge(large), "the filter was not written in the large form");
            MementoFilter.ReadFrom(new MemoryStream(large, writable: false));
            var streamed = NoGCRegion.Allocated(() => MementoFilter.ReadFrom(new MemoryStream(large, writable: false)));
            var budget = new ReadBudget(long.MaxValue, StructureId.MementoFilter);
            CollectionAssert.AreEqual(bytes,
                MementoFilter.ReadFrom(new MemoryStream(large, writable: false), new ReadOptions(budget)).ToByteArray(),
                "the large form does not read back as the filter");

            Console.WriteLine(
                $"[HOLDS ONCE] MementoFilter: tables of {string.Join(", ", filter.Segments.Select(TableBytes))} bytes, {tables:N0} in all, payload {payload:N0}; " +
                $"ordinary read {ordinary:N0}, charged {charged:N0}; large-form read {streamed:N0}, charged {budget.Charged:N0}");

            Assert.IsGreaterThanOrEqualTo(payload + tables, ordinary,
                $"the read allocated {ordinary:N0} bytes, less than the {payload:N0}-byte payload it holds and the {tables:N0} bytes of its tables");
            Assert.IsLessThanOrEqualTo(payload + tables + Slack, ordinary,
                $"the read allocated {ordinary:N0} bytes, {ordinary - payload - tables:N0} more than its payload and its tables");
            Assert.IsGreaterThanOrEqualTo(payload + tables, charged,
                $"the read was charged {charged:N0} bytes, less than its payload and its tables");
            Assert.IsLessThanOrEqualTo(payload + tables + Slack, charged,
                $"the read was charged {charged:N0} bytes, {charged - payload - tables:N0} more than its payload and its tables");

            Assert.IsGreaterThanOrEqualTo(tables, streamed,
                $"the read of the large form allocated {streamed:N0} bytes, less than the {tables:N0} bytes of its tables");
            Assert.IsLessThanOrEqualTo(tables + Slack, streamed,
                $"the read of the large form allocated {streamed:N0} bytes, {streamed - tables:N0} more than its tables");
            Assert.IsGreaterThanOrEqualTo(tables, budget.Charged,
                $"the read of the large form was charged {budget.Charged:N0} bytes, less than its tables");
            Assert.IsLessThanOrEqualTo(tables + Slack, budget.Charged,
                $"the read of the large form was charged {budget.Charged:N0} bytes, {budget.Charged - tables:N0} more than its tables");
        }

        /// <summary>
        /// A read charges each segment at least what restoring it allocates, and less than a
        /// small object past that: its table, and the segment with its codec, which come to more
        /// than one small object allows for. The restores are counted in a no-GC region.
        /// </summary>
        /// <remarks>
        /// Probed on 5752769, a segment and its codec came to 120 bytes beyond the table, at
        /// tables of 2^2, 2^8 and 2^12 slots, and were charged
        /// <see cref="ReadBudget.SmallObject"/>, 64, beside two tables. Charged one table (#327),
        /// they would be 56 bytes a segment under what the read allocates. The shapes are the
        /// narrowest table, where the segment is the largest share of what is allocated, the
        /// widest fingerprint, and two between. The first restore of each is not counted, so that
        /// what the runtime does once is not taken for the segment.
        /// </remarks>
        [TestMethod]
        public void TestASegmentIsChargedWhatRestoringItAllocates()
        {
            foreach (var (q, f, m) in new[] { (2, 2, 1), (2, 32, 8), (8, 8, 3), (12, 16, 3) })
            {
                var words = (uint)MementoSegment.Restore(q, f, m, 1).Data.Length;
                var charged = MementoFilter.SegmentCost(words);
                var allocated = NoGCRegion.Allocated(() => MementoSegment.Restore(q, f, m, 1));
                Assert.IsLessThanOrEqualTo(charged, allocated,
                    $"a segment of 2^{q} slots, {f}-bit fingerprints and {m}-bit mementos allocated {allocated:N0} bytes " +
                    $"to restore, and a read is charged {charged:N0} for it");
                Assert.IsLessThan(ReadBudget.SmallObject, charged - allocated,
                    $"a segment of 2^{q} slots, {f}-bit fingerprints and {m}-bit mementos is charged {charged:N0} bytes, " +
                    $"{charged - allocated:N0} past the {allocated:N0} restoring it allocated");
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
        /// The limit on the blocks a range tests belongs to the instance: it starts at the
        /// default, is the caller's to set, survives a reset, and is not written with the
        /// filter, so a filter read back has the default. At a limit of none, a range of two
        /// blocks is still exact, since neither is inside it; a range of three answers
        /// possibly while the filter holds a key, and no once it does not.
        /// </summary>
        [TestMethod]
        public void TestTheBlockLimitIsTheInstancesAndNotThePayloads()
        {
            var filter = new MementoFilter(maxRangeSize: 64, fingerprintBits: 10);
            Assert.AreEqual(MementoFilter.DefaultMaxInteriorBlocks, filter.MaxInteriorBlocks);

            filter.MaxInteriorBlocks = 0;
            filter.Add(10000);
            Assert.AreEqual(0UL, filter.MaxInteriorBlocks);

            // 10000 is in block 156; 9984 to 10111 is all of blocks 156 and 157.
            Assert.IsTrue(filter.TestRange(9984, 10111));
            Assert.IsFalse(filter.TestRange(10001, 10111), "two blocks with nothing in the range were not tested exactly");
            Assert.IsTrue(filter.TestRange(20000, 20200), "a range past a limit of none must answer possibly");

            var read = Persistence.FromByteArray<MementoFilter>(filter.ToByteArray());
            Assert.AreEqual(MementoFilter.DefaultMaxInteriorBlocks, read.MaxInteriorBlocks,
                "the limit was written with the filter");

            filter.Reset();
            Assert.AreEqual(0UL, filter.MaxInteriorBlocks, "a reset changed the caller's limit");
            Assert.IsFalse(filter.TestRange(20000, 20200), "an empty filter answered possibly");
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

        /// <summary>
        /// A filter whose payload would pass the longest an envelope holds is refused by its
        /// <c>WriteTo</c> before anything is built for it, naming the size it would be
        /// (#216). Its payload is the count, the expansions, the memento's width and the
        /// segment count, twenty bytes, then for each segment its two widths, its count and
        /// its word count, and eight bytes a word. A segment at the filter's own limit on its
        /// address is past the ceiling: 2^31 slots of twenty bits for a range of 256, and
        /// 2^30 of sixty-four for the widest slot, whose words times eight are 2^33 and
        /// sixteen, sixteen in 32 bits. Too large to build here, so the segment of a small
        /// filter claims the shape.
        /// </summary>
        [TestMethod]
        [DataRow(256UL, 8, 671_088_642L)]
        [DataRow(1UL << 32, 28, 1_073_741_826L)]
        public void TestAFilterTooLargeToWriteIsRefusedNamingItsWholeSize(
            ulong maxRangeSize, int fingerprintBits, long words)
        {
            var filter = new MementoFilter(maxRangeSize, fingerprintBits, initialCapacity: 64);
            var slotBits = MementoSegment.SlotBits(fingerprintBits, filter.MementoBits);
            Assert.AreEqual(words, (((1L << filter.QuotientBitsLimit) * slotBits) >> 6) + 2,
                "the row's table is miscounted");

            for (ulong i = 0; i < 40; i++) filter.Add(i * 7919);
            Assert.HasCount(1, filter.Segments, "Premise: the filter has not expanded.");

            TestPayloadCount.AssertRefusedWhileClaiming(filter, 20 + 16 + 8 * words,
                (filter.Segments[0], "QuotientBits", filter.QuotientBitsLimit));
        }
    }
}
