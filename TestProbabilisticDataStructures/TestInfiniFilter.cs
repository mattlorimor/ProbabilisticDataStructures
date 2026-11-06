using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Tests for <see cref="InfiniFilter"/>, against Dayan, Bercea, Reviriego and
    /// Pagh, "InfiniFilter: Expanding Filters to Infinity and Beyond" (SIGMOD 2023).
    /// </summary>
    /// <remarks>
    /// Two claims carry the structure. The first is the one every filter makes and
    /// this one has more chances to break: no false negatives, through any number of
    /// expansions and however many times an entry has been moved down the chain. The
    /// second is the paper's own: that the false positive rate grows with the
    /// logarithm of the item count rather than with the count. A filter that merely
    /// grew would satisfy the first and miss the point of the second.
    /// </remarks>
    [TestClass]
    public class TestInfiniFilter
    {
        private static byte[] Key(int i) => Encoding.ASCII.GetBytes($"key-{i}");

        private static byte[] Absent(int i) => Encoding.ASCII.GetBytes($"absent-{i}");

        /// <summary>
        /// States whose payload <see cref="TestPayloadCount"/> holds the count to: each
        /// segment's widths size its table, so two fingerprints, each empty and expanded: in
        /// place at thirteen bits, and at two into the chain of segments that fingerprints
        /// spent by expanding leave.
        /// </summary>
        internal static System.Collections.Generic.IEnumerable<PayloadState> PayloadStates()
        {
            foreach (var (fingerprintBits, keys, chained) in new[] { (2, 2_000, true), (13, 400, false) })
            {
                var segments = chained ? "several segments" : "one segment";
                yield return PayloadState.Of($"{fingerprintBits} bits, empty",
                    new InfiniFilter(initialCapacity: 32, fingerprintBits: fingerprintBits),
                    PersistenceFormat.InfiniWideVersion);

                var expanded = new InfiniFilter(initialCapacity: 32, fingerprintBits: fingerprintBits);
                for (var i = 0; i < keys; i++) expanded.Add(Key(i));
                Assert.IsGreaterThan(0u, expanded.Expansions, "Premise: the filter expanded.");
                Assert.AreEqual(chained, expanded.Segments.Count > 1,
                    $"Premise: the filter expanded into {segments}.");
                yield return PayloadState.Of($"{fingerprintBits} bits, expanded into {segments}", expanded,
                    PersistenceFormat.InfiniWideVersion);
            }
        }

        /// <summary>
        /// A fingerprint too short leaves nothing to be accurate with and sends
        /// entries down the chain almost at once; one too long cannot fit beside the
        /// address in a 64-bit hash once the filter has grown.
        /// </summary>
        [TestMethod]
        public void TestUnusableFingerprintSizeIsRefused()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new InfiniFilter(1024, 1));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new InfiniFilter(1024, 33));
        }

        /// <summary>
        /// A filter with room for nothing has nowhere to put the first item.
        /// </summary>
        [TestMethod]
        public void TestZeroInitialCapacityIsRefused()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new InfiniFilter(0, 8));
        }

        /// <summary>
        /// The contract a filter cannot break, tested well past the point where the
        /// filter has outgrown its original size many times over.
        /// </summary>
        [TestMethod]
        public void TestThereAreNoFalseNegativesThroughManyExpansions()
        {
            const int items = 200000;
            var filter = new InfiniFilter(initialCapacity: 64, fingerprintBits: 8);

            for (var i = 0; i < items; i++)
            {
                filter.Add(Key(i));
            }

            Assert.IsTrue(filter.ExpansionCount() >= 10,
                $"Premise: the filter expanded from 64 to hold {items} items, which " +
                $"takes at least ten doublings. It reported " +
                $"{filter.ExpansionCount()}.");

            for (var i = 0; i < items; i++)
            {
                Assert.IsTrue(filter.Test(Key(i)),
                    $"Item {i} was added and then reported absent. Every expansion " +
                    "rewrites every entry, and a filter that loses one during that " +
                    "is worse than useless.");
            }
        }

        /// <summary>
        /// Entries that run out of fingerprint are moved into a second table, and
        /// then a third. They must go on answering.
        /// </summary>
        /// <remarks>
        /// A short fingerprint is what forces this: an entry can survive only as many
        /// expansions as it has fingerprint bits, so four bits sends the oldest
        /// entries down the chain early and often. This is the path where a lost item
        /// is most likely, because a void entry keeps nothing but its address.
        /// </remarks>
        [TestMethod]
        public void TestEntriesSurviveBeingMovedDownTheChain()
        {
            const int items = 100000;
            var filter = new InfiniFilter(initialCapacity: 32, fingerprintBits: 4);

            for (var i = 0; i < items; i++)
            {
                filter.Add(Key(i));
            }

            Assert.IsTrue(filter.ChainLength() >= 3,
                "Premise: four-bit fingerprints over this many items should have " +
                $"built a chain of at least three tables. It has " +
                $"{filter.ChainLength()}, so the migration path is not being tested.");

            for (var i = 0; i < items; i++)
            {
                Assert.IsTrue(filter.Test(Key(i)),
                    $"Item {i} was lost, most likely while being moved between " +
                    "tables in the chain.");
            }
        }

        /// <summary>
        /// The paper's claim: the false positive rate grows with the logarithm of the
        /// item count. This is what separates InfiniFilter from simply sacrificing a
        /// fingerprint bit from every entry at every expansion, which would make the
        /// rate grow with the count itself.
        /// </summary>
        [TestMethod]
        public void TestFalsePositiveRateGrowsLogarithmicallyNotLinearly()
        {
            double RateAt(int items)
            {
                var filter = new InfiniFilter(initialCapacity: 64, fingerprintBits: 10);
                for (var i = 0; i < items; i++)
                {
                    filter.Add(Key(i));
                }

                var positives = 0;
                const int trials = 50000;
                for (var i = 0; i < trials; i++)
                {
                    if (filter.Test(Absent(i)))
                    {
                        positives++;
                    }
                }
                return (double)positives / trials;
            }

            var small = RateAt(2000);
            var large = RateAt(200000);

            Assert.IsTrue(small > 0.0,
                "Premise: the smaller filter produces some false positives, or the " +
                "ratio below is meaningless.");

            // A hundredfold more data. Were the rate linear in the count it would be
            // a hundred times worse; logarithmic growth roughly doubles it, since the
            // number of expansions grows by lg(100) which is under seven.
            var growth = large / small;
            Assert.IsTrue(growth < 10.0,
                $"A hundredfold increase in items took the false positive rate from " +
                $"{small:P3} to {large:P3}, a factor of {growth:F1}. Growth anywhere " +
                "near a hundredfold would mean every entry is paying for every " +
                "expansion, which is the design this one exists to improve on.");
            Assert.IsTrue(large > small,
                $"The rate did not grow at all ({small:P3} to {large:P3}). It is " +
                "supposed to grow slowly, not to be flat -- a flat reading here " +
                "usually means the measurement is wrong.");
        }

        /// <summary>
        /// The filter grows rather than refusing, which is the whole difference from
        /// <see cref="QuotientFilter"/>.
        /// </summary>
        [TestMethod]
        public void TestItGrowsInsteadOfFillingUp()
        {
            var filter = new InfiniFilter(initialCapacity: 64, fingerprintBits: 8);
            var startingCapacity = filter.Capacity();

            for (var i = 0; i < 20000; i++)
            {
                filter.Add(Key(i));
            }

            Assert.IsTrue(filter.Capacity() > startingCapacity * 100,
                $"The filter began with {startingCapacity} slots and holds " +
                $"{filter.Capacity()} after 20,000 items, which is not the growth " +
                "twenty thousand items require.");
            Assert.AreEqual(20000UL, filter.Count());
        }

        /// <summary>
        /// Removing an item must not remove anything else. Fingerprints in one run
        /// can have different lengths, and a shorter one stands for more keys, so
        /// taking away the wrong match would silently strand a key that was never
        /// deleted.
        /// </summary>
        [TestMethod]
        public void TestRemovingDoesNotStrandOtherItems()
        {
            const int items = 50000;
            var filter = new InfiniFilter(initialCapacity: 64, fingerprintBits: 8);

            for (var i = 0; i < items; i++)
            {
                filter.Add(Key(i));
            }

            var removed = 0;
            for (var i = 0; i < items; i += 2)
            {
                if (filter.TestAndRemove(Key(i)))
                {
                    removed++;
                }
            }

            Assert.AreEqual(items / 2, removed,
                "Every item removed here was added, so every removal should have " +
                "found something.");

            for (var i = 1; i < items; i += 2)
            {
                Assert.IsTrue(filter.Test(Key(i)),
                    $"Item {i} was never removed but is now reported absent, so " +
                    "removing its neighbours took it with them.");
            }
        }

        /// <summary>
        /// Removing something the filter never held is refused rather than performed,
        /// because the fingerprint it would match belongs to some other item.
        /// </summary>
        [TestMethod]
        public void TestRemovingSomethingAbsentIsRefused()
        {
            var filter = new InfiniFilter(initialCapacity: 64, fingerprintBits: 12);
            for (var i = 0; i < 1000; i++)
            {
                filter.Add(Key(i));
            }

            var before = filter.Count();
            var removedSomething = false;
            for (var i = 0; i < 200; i++)
            {
                if (filter.TestAndRemove(Absent(i)))
                {
                    removedSomething = true;
                }
            }

            // A false positive can make this true, but the count must never fall by
            // more than the removals that actually reported success.
            Assert.IsTrue(filter.Count() <= before);
            if (!removedSomething)
            {
                Assert.AreEqual(before, filter.Count(),
                    "Nothing reported as removed, yet the count fell.");
            }
        }

        /// <summary>
        /// A table with no empty slot left is refused rather than spun on.
        /// </summary>
        /// <remarks>
        /// The insert path walks forward until it finds somewhere empty, and in a full
        /// table there is nowhere, so the walk circles the table forever. It reached
        /// that state during development, through a path in the chain that had no way
        /// to expand, and it presented as a run that simply never returned. The filter
        /// expands before it can happen; this pins the behaviour if it ever does.
        /// </remarks>
        [TestMethod]
        public void TestAFullTableIsRefusedRatherThanSpunOn()
        {
            // Sixteen slots, filled exactly.
            var segment = new InfiniSegment(4, 8);
            for (uint slot = 0; slot < segment.Slots; slot++)
            {
                segment.Insert(slot, 0, slot);
            }

            Assert.AreEqual(segment.Slots, segment.Count);
            Assert.ThrowsExactly<InvalidOperationException>(
                () => segment.Insert(0, 0, 1),
                "Inserting into a full table must fail rather than loop.");
        }

        /// <summary>
        /// The age counter and the fingerprint share one field, and the counter is
        /// self-delimiting so the fingerprint's length can be read back without being
        /// stored.
        /// </summary>
        [TestMethod]
        public void TestAgeAndFingerprintSurviveTheSharedField()
        {
            const int fingerprintBits = 8;
            var segment = new InfiniSegment(4, fingerprintBits);

            for (var age = 0; age <= fingerprintBits; age++)
            {
                var remaining = fingerprintBits - age;
                var fingerprint = remaining == 0
                    ? 0UL
                    : (1UL << remaining) - 1;

                var field = segment.EncodeField(age, fingerprint);

                Assert.AreEqual(age, segment.AgeOf(field),
                    $"An entry encoded at age {age} read back as age " +
                    $"{segment.AgeOf(field)}.");
                Assert.AreEqual(fingerprint, segment.FingerprintOf(field, age),
                    $"The fingerprint at age {age} did not survive encoding.");
                Assert.AreEqual(age == fingerprintBits, segment.IsVoid(field),
                    $"An entry at age {age} of {fingerprintBits} was judged " +
                    "void incorrectly; only one that has spent every bit is void.");
            }
        }

        /// <summary>
        /// An expansion ages every entry that lived through it by exactly one, and
        /// leaves entries added afterwards at their full fingerprint.
        /// </summary>
        [TestMethod]
        public void TestOnlyEntriesPresentForAnExpansionPayForIt()
        {
            var filter = new InfiniFilter(initialCapacity: 16, fingerprintBits: 10);

            // Fill past the threshold so that at least one expansion happens.
            for (var i = 0; i < 200; i++)
            {
                filter.Add(Key(i));
            }
            var expansions = filter.ExpansionCount();
            Assert.IsTrue(expansions >= 2, "Premise: the filter has expanded.");

            var active = filter.Segments[0];
            var ages = active.Entries()
                .Select(entry => active.AgeOf(entry.Field))
                .ToArray();

            Assert.IsTrue(ages.Any(age => age == 0),
                "Entries added since the last expansion should still be at age zero " +
                "with their whole fingerprint. If none are, every entry is being " +
                "aged rather than only those that were present.");
            Assert.IsTrue(ages.All(age => age <= expansions),
                $"An entry claims to have survived more expansions than the " +
                $"{expansions} that have happened.");
            Assert.IsTrue(ages.Any(age => age > 0),
                "Premise: some entries did live through an expansion.");
        }

        /// <summary>
        /// No stored entry ever encodes as zero, which reserves zero to mean
        /// something else.
        /// </summary>
        /// <remarks>
        /// The age counter terminates in a one rather than a zero purely so that this
        /// holds. It is not needed by anything here: the Memento range filter builds
        /// on this substrate and needs a value that cannot be a real entry, to mark
        /// the boundary of a variable-length group of keys sharing a fingerprint.
        /// Zero is that value, and it is only available because every counter carries
        /// a set bit.
        /// <para>
        /// Measured before the counter was changed, three stored fields in a hundred
        /// thousand came out as zero, so this is a property that had to be arranged
        /// rather than one that held by luck.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestNoStoredEntryEncodesAsZero()
        {
            var filter = new InfiniFilter(initialCapacity: 64, fingerprintBits: 8);
            for (var i = 0; i < 100000; i++)
            {
                filter.Add(Key(i));
            }

            var checkedEntries = 0;
            foreach (var segment in filter.Segments)
            {
                foreach (var (_, field) in segment.Entries())
                {
                    checkedEntries++;
                    Assert.AreNotEqual(0UL, field,
                        "A stored entry encoded as zero, which is the value reserved " +
                        "to mean 'no entry here'. An age counter must always carry a " +
                        "set bit.");
                }
            }

            Assert.IsTrue(checkedEntries > 50000,
                $"Premise: this examined {checkedEntries} stored entries. Too few " +
                "would mean the sweep is not reaching the tables.");

            // The youngest and oldest encodings are the two ends of the range, so
            // check them directly rather than hoping the sweep happened to include
            // them.
            var probe = new InfiniSegment(4, 8);
            for (var age = 0; age <= 8; age++)
            {
                Assert.AreNotEqual(0UL, probe.EncodeField(age, 0),
                    $"An entry at age {age} with a zero fingerprint encoded as zero.");
            }
        }

        /// <summary>
        /// A filter written and read back is the same filter, and goes on growing the
        /// way the original would have.
        /// </summary>
        [TestMethod]
        public void TestRoundTripIsByteExactAndKeepsGrowing()
        {
            var filter = new InfiniFilter(initialCapacity: 64, fingerprintBits: 8);
            for (var i = 0; i < 60000; i++)
            {
                filter.Add(Key(i));
            }

            var written = filter.ToByteArray();
            var restored = InfiniFilter.ReadFrom(new MemoryStream(written));

            CollectionAssert.AreEqual(written, restored.ToByteArray());
            Assert.AreEqual(filter.Count(), restored.Count());
            Assert.AreEqual(filter.ChainLength(), restored.ChainLength());

            for (var i = 0; i < 60000; i++)
            {
                Assert.IsTrue(restored.Test(Key(i)), $"restored filter lost item {i}");
            }

            // And keeps expanding, rather than merely holding what it was given.
            for (var i = 60000; i < 140000; i++)
            {
                filter.Add(Key(i));
                restored.Add(Key(i));
            }

            CollectionAssert.AreEqual(filter.ToByteArray(), restored.ToByteArray(),
                "A restored filter must go on making the same decisions the original " +
                "goes on to make, expansions included.");
        }

        /// <summary>
        /// A read holds each table once and is charged for it once (#313). In the ordinary
        /// envelope, what it allocates and is charged beyond the payload it holds is the tables
        /// it builds, read into them and not first into arrays of their own and copied; in the
        /// large form, where the payload is not held, it is the tables alone.
        /// </summary>
        /// <remarks>
        /// Two-bit fingerprints are the narrowest, and an entry's goes void after the fewest
        /// expansions, so the chain behind the first table is longest: tables of 2^16, 2^13,
        /// 2^10 and 2^7 slots, 56,320 bytes of words. Probed on master at baf83ea, a read held
        /// every table twice and allocated 728 bytes besides its payload and both copies in the
        /// ordinary envelope, and 808 besides both copies in the large form, and was charged 416
        /// and 496 besides them. The slack, 4 KiB, is several times that and short of the first
        /// table and of the second, so a second copy of either fails it, as one of every table
        /// does. The reads are counted in a no-GC region (#293).
        /// </remarks>
        [TestMethod]
        public void TestAReadHoldsEachTableOnce()
        {
            const long Slack = 4 * 1024;

            var filter = new InfiniFilter(initialCapacity: 16, fingerprintBits: 2);
            for (var i = 0; i < 48_000; i++)
            {
                filter.Add(Key(i));
            }

            long TableBytes(InfiniSegment segment) => ReadBudget.ArrayOf((ulong)segment.Data.Length, sizeof(ulong));
            var tables = filter.Segments.Sum(TableBytes);
            Assert.IsGreaterThan(2, filter.Segments.Count, "premise: the chain does not hold tables behind the first");
            Assert.IsGreaterThan(Slack, TableBytes(filter.Segments[1]), "premise: a copy of the second table could hide in the slack");

            var bytes = filter.ToByteArray();
            long payload = bytes.Length - PersistenceFormat.EnvelopeLength;
            InfiniFilter.ReadFrom(new MemoryStream(bytes, writable: false));
            var ordinary = NoGCRegion.Allocated(() => InfiniFilter.ReadFrom(new MemoryStream(bytes, writable: false)));
            Assert.IsGreaterThanOrEqualTo(payload + tables, ordinary,
                $"the read allocated {ordinary:N0} bytes, less than the {payload:N0}-byte payload it holds and the {tables:N0} bytes of its tables");
            Assert.IsLessThanOrEqualTo(payload + tables + Slack, ordinary,
                $"the read allocated {ordinary:N0} bytes, {ordinary - payload - tables:N0} more than its payload and its tables");

            var charged = CapacityModel.ChargeOf<InfiniFilter>(StructureId.InfiniFilter, bytes);
            Assert.IsGreaterThanOrEqualTo(payload + tables, charged,
                $"the read was charged {charged:N0} bytes, less than its payload and its tables");
            Assert.IsLessThanOrEqualTo(payload + tables + Slack, charged,
                $"the read was charged {charged:N0} bytes, {charged - payload - tables:N0} more than its payload and its tables");

            var large = LargeForm.WriteLarge(filter);
            Assert.IsTrue(LargeForm.IsLarge(large), "the filter was not written in the large form");
            InfiniFilter.ReadFrom(new MemoryStream(large, writable: false));
            var streamed = NoGCRegion.Allocated(() => InfiniFilter.ReadFrom(new MemoryStream(large, writable: false)));
            Assert.IsGreaterThanOrEqualTo(tables, streamed,
                $"the read of the large form allocated {streamed:N0} bytes, less than the {tables:N0} bytes of its tables");
            Assert.IsLessThanOrEqualTo(tables + Slack, streamed,
                $"the read of the large form allocated {streamed:N0} bytes, {streamed - tables:N0} more than its tables");

            var budget = new ReadBudget(long.MaxValue, StructureId.InfiniFilter);
            CollectionAssert.AreEqual(bytes,
                InfiniFilter.ReadFrom(new MemoryStream(large, writable: false), new ReadOptions(budget)).ToByteArray(),
                "the large form does not read back as the filter");
            Assert.IsGreaterThanOrEqualTo(tables, budget.Charged,
                $"the read of the large form was charged {budget.Charged:N0} bytes, less than its tables");
            Assert.IsLessThanOrEqualTo(tables + Slack, budget.Charged,
                $"the read of the large form was charged {budget.Charged:N0} bytes, {budget.Charged - tables:N0} more than its tables");
        }

        /// <summary>
        /// A read charges each segment at least what restoring it allocates, and less than a
        /// small object past that (#329): its table, and the segment, whose fields are more than
        /// one small object allows for. The restores are counted in a no-GC region.
        /// </summary>
        /// <remarks>
        /// Probed on 05a5669, a segment came to 80 bytes beyond its table, and was charged
        /// <see cref="ReadBudget.SmallObject"/>, 64: 16 bytes a segment under what the read
        /// allocated, which the slack of every read-budget sample hid. The shapes are the
        /// narrowest table, where the segment is the largest share of what is allocated, the
        /// widest fingerprint, and two between. The first restore of each is not counted, so that
        /// what the runtime does once is not taken for the segment.
        /// </remarks>
        [TestMethod]
        public void TestASegmentIsChargedWhatRestoringItAllocates()
        {
            foreach (var (q, f) in new[] { (2, 2), (2, 32), (10, 8), (16, 16) })
            {
                var charged = InfiniFilter.SegmentCost((ulong)InfiniSegment.Words(q, f));
                InfiniSegment.Restore(q, f, 1);
                var allocated = NoGCRegion.Allocated(() => InfiniSegment.Restore(q, f, 1));
                Assert.IsLessThanOrEqualTo(charged, allocated,
                    $"a segment of 2^{q} slots of {f}-bit fingerprints allocated {allocated:N0} bytes to restore, " +
                    $"and a read is charged {charged:N0} for it");
                Assert.IsLessThan(ReadBudget.SmallObject, charged - allocated,
                    $"a segment of 2^{q} slots of {f}-bit fingerprints is charged {charged:N0} bytes, " +
                    $"{charged - allocated:N0} past the {allocated:N0} restoring it allocated");
            }
        }

        /// <summary>
        /// Reset returns the filter to the size it was built at, not merely to empty.
        /// </summary>
        [TestMethod]
        public void TestResetShrinksBackToTheStartingSize()
        {
            var filter = new InfiniFilter(initialCapacity: 64, fingerprintBits: 8);
            var startingCapacity = filter.Capacity();

            for (var i = 0; i < 50000; i++)
            {
                filter.Add(Key(i));
            }

            filter.Reset();

            Assert.AreEqual(0UL, filter.Count());
            Assert.AreEqual(1, filter.ChainLength());
            Assert.AreEqual(0u, filter.ExpansionCount());
            Assert.AreEqual(startingCapacity, filter.Capacity(),
                "A filter that kept its grown table after a reset would be holding " +
                "memory for items it no longer has.");
            Assert.IsFalse(filter.Test(Key(1)));
        }

        /// <summary>
        /// The hash cannot be replaced once anything has been added, because
        /// everything already stored sits where the old hash put it.
        /// </summary>
        [TestMethod]
        public void TestHashCannotBeReplacedAfterAdding()
        {
            var filter = new InfiniFilter(initialCapacity: 64, fingerprintBits: 8);
            filter.SetHash(data => 42UL);

            filter.Add(Key(1));

            Assert.ThrowsExactly<InvalidOperationException>(
                () => filter.SetHash(data => 7UL));
        }

        /// <summary>
        /// Adding the same item twice stores it twice, so that removing it once
        /// leaves it present -- the same choice <see cref="QuotientFilter"/> makes,
        /// for the same reason.
        /// </summary>
        [TestMethod]
        public void TestRepeatsAreStoredSeparately()
        {
            var filter = new InfiniFilter(initialCapacity: 64, fingerprintBits: 12);

            filter.Add(Key(1));
            filter.Add(Key(1));

            Assert.AreEqual(2UL, filter.Count());
            Assert.IsTrue(filter.TestAndRemove(Key(1)));
            Assert.IsTrue(filter.Test(Key(1)),
                "One of the two copies remains, so the item is still present.");
        }

        /// <summary>
        /// A key whose hash is the key itself, so a test can say which bits of hash two
        /// keys share instead of searching for keys that happen to.
        /// </summary>
        private static byte[] HashKey(ulong hash)
        {
            var bytes = new byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, hash);
            return bytes;
        }

        private static ulong HashOfKey(ReadOnlySpan<byte> key) =>
            BinaryPrimitives.ReadUInt64LittleEndian(key);

        /// <summary>
        /// The ages of the entries a table holds at a hash's quotient, which are the
        /// entries that could answer for it.
        /// </summary>
        private static int[] AgesAtTheQuotientOf(InfiniSegment segment, ulong hash)
        {
            var (quotient, _) = segment.Split(hash);
            return segment.Entries()
                .Where(e => e.Quotient == quotient)
                .Select(e => segment.AgeOf(e.Field))
                .ToArray();
        }

        /// <summary>
        /// Across the chain of tables, removal takes the newest table's match, because
        /// it remembers more of the hash than any match in a later table -- even when
        /// its own fingerprint has run out.
        /// </summary>
        /// <remarks>
        /// Within one table <c>InfiniSegment.Remove</c> takes the match with the
        /// longest fingerprint, for the reason it documents: a shorter one stands for
        /// more keys, and removing it could take the only record of one that was never
        /// deleted. Across tables the filter asks them newest first, and "longest" has
        /// to count everything a match remembers -- address and fingerprint together,
        /// since the tables have different address widths. An entry reaches a later
        /// table by going void, and then it remembers no more of its hash than the
        /// address of the table it left, which has doubled since. Everything still in
        /// that table remembers more. <see cref="MementoFilter"/> chains its tables the
        /// same way and has the same test, for the same reason.
        /// <para>
        /// The scenario sets the two readings of "longest" against each other, with
        /// the hash as the key so the bits are chosen rather than found. Key j arrives
        /// first and goes void at the third expansion. In the second table it is its
        /// run's only record, with a full two-bit fingerprint over a two-bit address:
        /// four bits of hash. Key k arrives after the first expansion. By the third it
        /// has spent its whole fingerprint, but over a five-bit address: five bits. The
        /// two agree on the four bits j's entry remembers, so removing k matches both.
        /// Taking the second table's entry, by asking the tables oldest first or by
        /// comparing fingerprint lengths alone, removes j's only record. The keys
        /// differ at the fifth bit, so k's own entry in the first table does not also
        /// answer for j and hide the loss.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestRemovalAcrossTheChainTakesTheNewerTablesMatch()
        {
            const ulong J = 0b00000;
            const ulong K = 0b10000;

            // Fillers drive the expansions. None shares a quotient with j or k at any
            // table size, because their two lowest bits differ from theirs, so nothing
            // but j's and k's own entries is in the way of the premises below, and
            // the expansions come where they say.
            var fillers = Enumerable.Range(1, 63)
                .Select(h => (ulong)h)
                .Where(h => (h & 3) != (J & 3))
                .Take(11)
                .ToArray();
            Assert.HasCount(11, fillers, "Premise: eleven fillers.");

            var filter = new InfiniFilter(
                initialCapacity: 1, fingerprintBits: 2, hash: HashOfKey);
            filter.Add(HashKey(J));
            filter.Add(HashKey(fillers[0]));
            filter.Add(HashKey(fillers[1]));
            Assert.AreEqual(0u, filter.ExpansionCount(),
                "Premise: j arrived before any expansion.");
            filter.Add(HashKey(K));
            Assert.AreEqual(1u, filter.ExpansionCount(),
                "Premise: k arrived after the first.");
            for (var i = 2; i < fillers.Length; i++)
            {
                filter.Add(HashKey(fillers[i]));
            }
            Assert.AreEqual(3u, filter.ExpansionCount(), "Premise: three expansions.");
            Assert.AreEqual(2, filter.ChainLength(),
                "Premise: j went void into a second table.");

            var newer = filter.Segments[0];
            var older = filter.Segments[1];
            Assert.IsFalse(newer.Holds(J),
                "Premise: j has no record in the first table.");
            Assert.IsTrue(older.Holds(J),
                "Premise: j's only record is in the second table.");
            Assert.IsTrue(older.Holds(K),
                "Premise: k matches j's entry in the second table.");
            Assert.AreEqual(newer.FingerprintBits, AgesAtTheQuotientOf(newer, K).Single(),
                "Premise: k's entry in the first table has spent its whole fingerprint.");
            Assert.AreEqual(0, AgesAtTheQuotientOf(older, K).Single(),
                "Premise: the second table's match carries a full fingerprint, so " +
                "comparing fingerprint lengths alone would choose it.");
            Assert.IsGreaterThan(older.QuotientBits + older.FingerprintBits,
                newer.QuotientBits,
                "Premise: the first table's address is longer than everything the " +
                "second table's match remembers, which is the whole case for it.");

            var held = filter.Count();
            Assert.IsTrue(filter.TestAndRemove(HashKey(K)),
                "k was added, so removing it succeeds.");

            Assert.AreEqual(held - 1, filter.Count(), "Removing k removes one entry.");
            Assert.IsFalse(newer.Holds(K),
                "k's own entry, in the first table, must be the one that went.");
            Assert.IsTrue(filter.Test(HashKey(J)),
                "j was never removed and now tests absent. Removing k took j's only " +
                "record from the second table instead of k's own entry in the first, " +
                "which remembers five bits of hash to the second table's four.");
        }

        /// <summary>
        /// A filter whose payload would pass the longest an envelope holds is refused by its
        /// <c>ToByteArray</c> before anything is built for it, naming the size it would be
        /// (#216). Its payload is the count, the expansions and the segment count, sixteen
        /// bytes, then for each segment its two widths, four bytes each, its count and word
        /// count, eight bytes each from version 13, and eight bytes a word. One segment as wide
        /// as its fingerprint allows, the size an expansion reaches, is past the ceiling at any
        /// fingerprint: at eight bits that is 2^33 slots, past what a <c>u32</c> counts, and at
        /// thirty-two 2^31, whose words times eight are past 2^33, a few bytes in 32 bits. Too
        /// large to build here, so the segment of a small filter claims the shape.
        /// </summary>
        [TestMethod]
        [DataRow(8, 1_610_612_738L)]
        [DataRow(32, 1_207_959_554L)]
        public void TestAFilterTooLargeToWriteIsRefusedNamingItsWholeSize(int fingerprintBits, long words)
        {
            var widest = InfiniFilter.QuotientBitsLimitFor(fingerprintBits);
            Assert.AreEqual(words, (((1L << widest) * (fingerprintBits + 4)) >> 6) + 2,
                "the row's table is miscounted");

            var filter = new InfiniFilter(initialCapacity: 64, fingerprintBits: fingerprintBits);
            for (var i = 0; i < 40; i++) filter.Add(Key(i));
            Assert.HasCount(1, filter.Segments, "Premise: the filter has not expanded.");

            TestPayloadCount.AssertRefusedWhileClaiming(filter, 16 + 24 + 8 * words,
                (filter.Segments[0], "QuotientBits", widest));
        }
    }
}
