using System;
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
    }
}
