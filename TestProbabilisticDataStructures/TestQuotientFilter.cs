using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestQuotientFilter
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        /// <summary>
        /// States whose payload <see cref="TestPayloadCount"/> holds the count to: the two
        /// widths size the table, so two splits, each empty and filled, all written at
        /// version 12; and the one-slot table read from version 1, which is written back at
        /// version 1 with its count in 32 bits.
        /// </summary>
        internal static IEnumerable<PayloadState> PayloadStates()
        {
            foreach (var (n, rate) in new[] { (100u, 0.01), (5_000u, 0.0001) })
            {
                yield return PayloadState.Of($"{n} at {rate}, empty", new QuotientFilter(n, rate),
                    PersistenceFormat.QuotientWideVersion);

                var filled = new QuotientFilter(n, rate);
                for (var i = 0; i < n / 2; i++) filled.Add(Key($"k{i}"));
                yield return PayloadState.Of($"{n} at {rate}, half full", filled, PersistenceFormat.QuotientWideVersion);
            }

            yield return PayloadState.Of("one slot, read from version 1",
                QuotientFilter.ReadFrom(new MemoryStream(QuotientFilterFixture("quotientfilter-q32-v1.bin"))),
                PersistenceFormat.DefaultVersion);
        }

        [TestMethod]
        public void TestANewFilterHoldsNothing()
        {
            var filter = new QuotientFilter(1000, 0.01);

            Assert.IsFalse(filter.Test(Key("anything")));
            Assert.AreEqual(0u, filter.Count());
        }
        [TestMethod]
        public void TestAnAddedItemIsFound()
        {
            var filter = new QuotientFilter(1000, 0.01);
            filter.Add(Key("alpha"));

            Assert.IsTrue(filter.Test(Key("alpha")));
            Assert.AreEqual(1u, filter.Count());
        }

        /// <summary>
        /// The defining promise: a false positive is allowed and a false negative is not.
        /// </summary>
        [TestMethod]
        public void TestEveryAddedItemIsFound()
        {
            var filter = new QuotientFilter(1000, 0.01);

            for (var i = 0; i < 750; i++)
            {
                filter.Add(Key($"item{i}"));
            }

            for (var i = 0; i < 750; i++)
            {
                Assert.IsTrue(filter.Test(Key($"item{i}")), $"item{i} was lost");
            }

            Assert.AreEqual(750u, filter.Count());
        }

        /// <summary>
        /// The other half of the promise, and what stops the test above from passing
        /// vacuously: a filter answering yes to everything satisfies "every added item
        /// is found" perfectly.
        /// </summary>
        [TestMethod]
        public void TestAbsentItemsAreMostlyAbsent()
        {
            var filter = new QuotientFilter(10000, 0.01);

            for (var i = 0; i < 7500; i++)
            {
                filter.Add(Key($"in{i}"));
            }

            var positives = 0;
            for (var i = 0; i < 50000; i++)
            {
                if (filter.Test(Key($"out{i}")))
                {
                    positives++;
                }
            }

            var rate = (double)positives / 50000;
            Assert.IsLessThan(0.02, rate, $"false positive rate was {rate:P2}");
        }

        [TestMethod]
        public void TestARemovedItemIsGone()
        {
            var filter = new QuotientFilter(1000, 0.01);
            filter.Add(Key("alpha"));
            filter.Add(Key("beta"));

            Assert.IsTrue(filter.TestAndRemove(Key("alpha")));

            Assert.IsFalse(filter.Test(Key("alpha")));
            Assert.IsTrue(filter.Test(Key("beta")), "removing alpha took beta with it");
            Assert.AreEqual(1u, filter.Count());

            // Removing what is not there says so, and changes nothing.
            Assert.IsFalse(filter.TestAndRemove(Key("alpha")));
            Assert.AreEqual(1u, filter.Count());
        }

        /// <summary>
        /// Deleting from a quotient filter moves other elements, so the failure that
        /// matters is not losing what was deleted -- it is losing a neighbour that
        /// happened to share a cluster with it.
        /// <para>
        /// Runs adds and removes against a set that knows the truth, at a load high
        /// enough that clusters are long, and checks every survivor rather than the one
        /// just touched. A single lost element fails this.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestDeletingDoesNotDisturbTheRestOfTheCluster()
        {
            var filter = new QuotientFilter(2000, 0.01);
            var present = new List<string>();
            var random = new Random(7);
            var next = 0;

            for (var i = 0; i < 1400; i++)
            {
                var item = $"item{next++}";
                filter.Add(Key(item));
                present.Add(item);
            }

            for (var step = 0; step < 4000; step++)
            {
                if (present.Count > 700 && random.Next(2) == 0)
                {
                    var index = random.Next(present.Count);
                    var victim = present[index];
                    present.RemoveAt(index);

                    Assert.IsTrue(filter.TestAndRemove(Key(victim)),
                        $"step {step}: {victim} was held but could not be removed");
                }
                else
                {
                    var item = $"item{next++}";
                    filter.Add(Key(item));
                    present.Add(item);
                }

                if (step % 200 == 0)
                {
                    foreach (var item in present)
                    {
                        Assert.IsTrue(filter.Test(Key(item)),
                            $"step {step}: {item} was lost while other items moved");
                    }
                }
            }

            foreach (var item in present)
            {
                Assert.IsTrue(filter.Test(Key(item)), $"{item} was lost by the end");
            }

            Assert.AreEqual((uint)present.Count, filter.Count());
        }

        /// <summary>
        /// A quotient filter has a hard capacity: every entry occupies a slot, so a full
        /// table cannot take another however long it looks for room.
        /// <para>
        /// This differs from <see cref="CuckooBloomFilter"/>, whose insert can fail at
        /// high load because its eviction gives up, and which reports that by returning
        /// false. Here a refusal means the caller has overrun the size they asked for,
        /// which is a mistake rather than an expected outcome, so it is raised rather
        /// than returned -- a returned false is only as good as the caller's willingness
        /// to look at it, and silently dropping data is the failure this library has
        /// spent the most effort removing.
        /// </para>
        /// <para>
        /// The timeout is deliberate: the failure being guarded against is a search for
        /// room that never ends, which without it hangs the suite rather than failing it.
        /// </para>
        /// </summary>
        [TestMethod]
        [Timeout(15000)]
        public void TestAFullFilterRefusesRatherThanSearchingForever()
        {
            var filter = new QuotientFilter(100, 0.01);
            var accepted = new List<string>();

            var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            {
                for (var i = 0UL; i < filter.Capacity() * 4; i++)
                {
                    filter.Add(Key($"x{i}"));
                    accepted.Add($"x{i}");
                }
            });

            StringAssert.Contains(ex.Message, "full");

            // It filled up rather than giving up early.
            Assert.IsGreaterThan(filter.Capacity() * 3 / 4, (ulong)accepted.Count,
                $"the filter refused after only {accepted.Count} of {filter.Capacity()} slots");

            // And everything it did accept is still there.
            foreach (var item in accepted)
            {
                Assert.IsTrue(filter.Test(Key(item)), $"{item} was lost as the filter filled");
            }
        }

        /// <summary>
        /// Every addition is stored, including a repeat of something already held, so it
        /// takes as many removals to empty an item out as it took additions to put it in.
        /// <para>
        /// Collapsing repeats is the obvious alternative and is wrong. Nothing here can
        /// tell the same item added twice from two different items whose fingerprints
        /// agree -- that is what a fingerprint is -- so collapsing the first collapses
        /// the second too, and removing either of those two items then makes the filter
        /// answer <b>no</b> for the other. A false negative is the one answer a filter
        /// is never allowed to give, and it is worth a slot per repeat to avoid.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestEachAdditionNeedsItsOwnRemoval()
        {
            var filter = new QuotientFilter(1000, 0.01);

            filter.Add(Key("alpha"));
            filter.Add(Key("alpha"));

            Assert.AreEqual(2u, filter.Count());

            Assert.IsTrue(filter.TestAndRemove(Key("alpha")));
            Assert.IsTrue(filter.Test(Key("alpha")),
                "one removal undid both additions, which is how a collision loses an item");

            Assert.IsTrue(filter.TestAndRemove(Key("alpha")));
            Assert.IsFalse(filter.Test(Key("alpha")));
        }

        /// <summary>
        /// What this has that <see cref="CuckooBloomFilter"/> does not. A cuckoo
        /// filter's fingerprints only mean anything relative to the bucket they landed
        /// in, so two of them cannot be combined; a quotient filter's entries carry
        /// their quotient in their position, so every fingerprint can be recovered and
        /// re-placed somewhere else.
        /// </summary>
        [TestMethod]
        public void TestMergeHoldsEverythingFromBoth()
        {
            var a = new QuotientFilter(2000, 0.01);
            var b = new QuotientFilter(2000, 0.01);

            for (var i = 0; i < 600; i++)
            {
                a.Add(Key($"a{i}"));
                b.Add(Key($"b{i}"));
            }

            a.Merge(b);

            Assert.AreEqual(1200u, a.Count());

            for (var i = 0; i < 600; i++)
            {
                Assert.IsTrue(a.Test(Key($"a{i}")), $"a{i} was lost by the merge");
                Assert.IsTrue(a.Test(Key($"b{i}")), $"b{i} did not survive the merge");
            }

            // The filter merged in is untouched, so it can be merged somewhere else too.
            Assert.AreEqual(600u, b.Count());
            Assert.IsFalse(b.Test(Key("a0")), "the merge wrote into the wrong filter");
        }

        /// <summary>
        /// A filter merged into itself holds everything twice: exactly what a twin that
        /// was given every item twice holds, to the byte.
        /// </summary>
        /// <remarks>
        /// This is the behaviour <c>Merge</c> keeps a snapshot of the entries for. Every
        /// insert shifts the entries after it along their cluster, so a merge that read
        /// the table lazily while writing into the same table would meet entries it had
        /// already moved. A filter holding 600 is sparse, and one holding half the slots
        /// leaves clusters long enough for the shifting to reach far; the byte
        /// comparison covers every slot either way, which a count and a handful of
        /// lookups would not.
        /// </remarks>
        [TestMethod]
        public void TestAFilterMergedIntoItselfHoldsEverythingTwice()
        {
            var slots = new QuotientFilter(2000, 0.01).SlotCount;

            // A hundred entries are listed and the rest copy the table, which is the
            // cheaper of the two for them (TestAFilterMergedIntoItselfTakesTheCheaperOfItsEntriesAndItsTable).
            foreach (var held in new[] { 100, 600, (int)(slots / 2) })
            {
                var filter = new QuotientFilter(2000, 0.01);
                var twin = new QuotientFilter(2000, 0.01);

                for (var i = 0; i < held; i++)
                {
                    filter.Add(Key($"item{i}"));
                    twin.Add(Key($"item{i}"));
                    twin.Add(Key($"item{i}"));
                }

                var returned = filter.Merge(filter);

                Assert.AreSame(filter, returned, "the merge did not return the filter it was called on");
                Assert.AreEqual((ulong)(2 * held), filter.Count(), $"{held} entries merged into themselves");
                CollectionAssert.AreEqual(twin.ToByteArray(), filter.ToByteArray(),
                    $"{held} entries merged into themselves did not leave what adding each twice does");

                for (var i = 0; i < held; i++)
                {
                    Assert.IsTrue(filter.Test(Key($"item{i}")), $"item{i} was lost by the self-merge");
                }
            }
        }

        /// <summary>
        /// The most a filter can merge into itself is the half of its slots it can hold
        /// twice over. That leaves a table with no empty slot, and the next entry over
        /// the half is refused before anything is written.
        /// </summary>
        [TestMethod]
        public void TestAFilterMergedIntoItselfIsRefusedPastHalfItsSlots()
        {
            var filter = new QuotientFilter(2000, 0.01);
            var slots = filter.SlotCount;

            for (var i = 0UL; i < slots / 2; i++)
            {
                filter.Add(Key($"item{i}"));
            }

            filter.Merge(filter);

            Assert.AreEqual(slots, filter.Count(), "half the slots, merged into themselves, did not fill the table");
            Assert.ThrowsExactly<InvalidOperationException>(() => filter.Add(Key("one more")));

            var small = new QuotientFilter(2000, 0.01);
            for (var i = 0UL; i <= slots / 2; i++)
            {
                small.Add(Key($"item{i}"));
            }

            var before = small.ToByteArray();
            Assert.ThrowsExactly<InvalidOperationException>(() => small.Merge(small));
            CollectionAssert.AreEqual(before, small.ToByteArray(), "a refused self-merge changed the filter");
        }

        /// <summary>
        /// A filter merged into itself reads the table it writes, so it takes what it reads
        /// first, and it takes the cheaper of two ways to: its entries listed, sixteen bytes
        /// each, or a copy of its table. It used to list them whatever they cost, and past
        /// 2^31 slots (#231) a filter can hold more entries than one list does, at sixteen
        /// bytes each where a slot costs at most eight: listing a table that full cannot be
        /// done, and copying it can.
        /// </summary>
        /// <remarks>
        /// The table is 2^20 slots of ten bits, 163,842 words and 1,310,736 bytes, which is what
        /// 81,921 entries list in. A thousand entries list in 16,000, and a quarter of the
        /// slots in 4,194,304; a quarter, so that the merge leaves the table half full, since
        /// each insert into a table near full walks a cluster nearly as long as the table.
        /// Between them, 81,000 and 82,500 straddle where the two cost the same, each far
        /// enough from it that the wrong way costs more than the slack: they are what tells
        /// the sixteen bytes an entry the choice is made by from a figure a little off. Each
        /// self-merge is held to the cheaper of the two and a slack, measured in a no-GC
        /// region on a filter read from a payload each time so that every attempt merges the
        /// same table; the dearer is past that bound in each, so taking the wrong one fails,
        /// either way. Probed: 16,168 bytes for a thousand entries, and 1,310,984 for a
        /// quarter of the slots, so the slack is some sixteen times what either way adds.
        /// </remarks>
        [TestMethod]
        public void TestAFilterMergedIntoItselfTakesTheCheaperOfItsEntriesAndItsTable()
        {
            const long Slack = 4096;

            foreach (var held in new[] { 1_000, 81_000, 82_500, 1 << 18 })
            {
                var filter = new QuotientFilter(400_000, 0.01);
                for (var i = 0; i < held; i++)
                {
                    filter.Add(Key($"item{i}"));
                }

                Assert.AreEqual(1UL << 20, filter.SlotCount, "premise: 2^20 slots");
                Assert.AreEqual(10u, filter.BitsPerSlot(), "premise: slots of ten bits");
                var tableBytes = 8L * ((((1L << 20) * 10) >> 6) + 2);
                var listBytes = 16L * held;
                var cheaper = Math.Min(tableBytes, listBytes);
                Assert.IsGreaterThan(cheaper + Slack, Math.Max(tableBytes, listBytes),
                    $"{held} entries: premise, the dearer way is past the bound");

                var payload = filter.ToByteArray();
                QuotientFilter merged = null;
                var allocated = NoGCRegion.Allocated(
                    () => merged.Merge(merged),
                    first: () => merged = Persistence.FromByteArray<QuotientFilter>(payload));

                Assert.AreEqual((ulong)(2 * held), merged.Count(), $"{held} entries: the merge did not double them");
                Assert.IsTrue(merged.Test(Key("item0")) && merged.Test(Key($"item{held - 1}")));
                Assert.IsLessThan(cheaper + Slack, allocated,
                    $"{held} entries merged into themselves allocated {allocated:N0} bytes, where listing them " +
                    $"costs {listBytes:N0} and copying the table {tableBytes:N0}");
            }
        }

        /// <summary>
        /// Merging reads the other filter's entries as it goes rather than listing them
        /// first: the allocation of a merge does not grow with the entries merged in.
        /// </summary>
        /// <remarks>
        /// Listing the entries first cost 16 bytes each (a <c>uint</c> and a <c>ulong</c>,
        /// padded) on top of both tables, and for a count of 2^31 or more it could not be
        /// built at all. Measured on the sample below: 4,000,056 bytes, 16.0 an entry,
        /// with the list; 56, the walk's own state, without it. The bound is 16 KiB,
        /// which is a fraction of a byte an entry here, so no cost that scales with the
        /// entries can hide in it, and well above the few kilobytes of noise below.
        /// <para>
        /// The target is read from a payload each time so that every repeat merges into
        /// the same table, and the measurement is the least of five, each in a no-GC region,
        /// as <see cref="TestReadBudget"/> takes it: a background collection running beside
        /// the merge makes the counter report kilobytes the merge did not ask for
        /// (<see cref="NoGCRegion"/>).
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestMergeAllocatesNothingPerEntryMergedIn()
        {
            const int each = 250_000;

            var target = new QuotientFilter(400_000, 0.01);
            var source = new QuotientFilter(400_000, 0.01);
            for (var i = 0; i < each; i++)
            {
                target.Add(Key($"t{i}"));
                source.Add(Key($"s{i}"));
            }

            var payload = target.ToByteArray();
            var least = long.MaxValue;
            for (var run = 0; run < 5; run++)
            {
                QuotientFilter merged = null;
                least = Math.Min(least, NoGCRegion.Allocated(
                    () => merged.Merge(source),
                    first: () => merged = Persistence.FromByteArray<QuotientFilter>(payload)));

                // The merge did the work the allocation is being asked about.
                Assert.AreEqual((uint)(2 * each), merged.Count());
                Assert.IsTrue(merged.Test(Key("t0")) && merged.Test(Key("s0")));
                Assert.IsTrue(merged.Test(Key($"s{each - 1}")) && merged.Test(Key($"t{each - 1}")));
            }

            Assert.IsLessThan(16 * 1024, least,
                $"merging in {each:N0} entries allocated {least:N0} bytes, " +
                $"{(double)least / each:N1} an entry: the entries were held while they were merged");
        }

        [TestMethod]
        public void TestMergeRejectsFiltersItCannotCombine()
        {
            var ex = Assert.ThrowsExactly<ArgumentException>(
                () => new QuotientFilter(1000, 0.01).Merge(new QuotientFilter(1000, 0.0001)));
            StringAssert.Contains(ex.Message, "must match");

            Assert.ThrowsExactly<ArgumentException>(
                () => new QuotientFilter(1000, 0.01).Merge(new QuotientFilter(50000, 0.01)));

            Assert.ThrowsExactly<ArgumentNullException>(
                () => new QuotientFilter(1000, 0.01).Merge(null!));
        }

        [TestMethod]
        public void TestRoundTripsThroughPersistence()
        {
            var filter = new QuotientFilter(2000, 0.01);
            for (var i = 0; i < 1200; i++)
            {
                filter.Add(Key($"k{i}"));
            }

            // Removed before writing, so the payload carries a table that has had
            // entries moved back as well as forward.
            filter.TestAndRemove(Key("k5"));

            var restored = Persistence.FromByteArray<QuotientFilter>(filter.ToByteArray());

            Assert.AreEqual(filter.Count(), restored.Count());
            Assert.AreEqual(filter.Capacity(), restored.Capacity());

            for (var i = 0; i < 1200; i++)
            {
                Assert.AreEqual(filter.Test(Key($"k{i}")), restored.Test(Key($"k{i}")),
                    $"the restored filter disagreed about k{i}");
            }

            // And keeps working, rather than merely reading back.
            restored.Add(Key("later"));
            Assert.IsTrue(restored.Test(Key("later")));
            Assert.IsTrue(restored.TestAndRemove(Key("k9")));
            Assert.IsFalse(restored.Test(Key("k9")));
        }

        /// <summary>
        /// A read holds the table once and is charged for it once (#328). In the ordinary
        /// envelope, what it allocates and is charged beyond the payload it holds is the table
        /// it builds, its words read into it and not first into an array of their own and
        /// copied; in the large form, where the payload is not held, it is the table alone,
        /// with no buffer on the heap for the pieces the words are read in.
        /// </summary>
        /// <remarks>
        /// A table of 2^16 slots at 1%, 81,936 bytes of words, longer than a piece of a run on
        /// the heap (<see cref="PayloadSource.PieceLength"/>), so that a buffer of one is a
        /// whole piece. The slack, 4 KiB, is a twentieth of the table and of the piece, so a
        /// copy of either fails it. Each figure is held from below too, to the payload and the
        /// table, so that a measurement of nothing cannot pass. The reads are counted in a
        /// no-GC region (#293).
        /// </remarks>
        [TestMethod]
        public void TestAReadHoldsItsTableOnce()
        {
            const long Slack = 4 * 1024;

            var filter = new QuotientFilter(40_000, 0.01);
            for (var i = 0; i < 30_000; i++)
            {
                filter.Add(Key($"k{i}"));
            }

            Assert.AreEqual(1UL << 16, filter.Capacity(), "premise: the table is not of 2^16 slots");
            var bytes = filter.ToByteArray();
            long payload = bytes.Length - PersistenceFormat.EnvelopeLength;
            var table = ReadBudget.ArrayOf(
                (ulong)QuotientFilter.TableWords(filter.Capacity(), QuotientFilter.Split(40_000, 0.01).RemainderBits), sizeof(ulong));
            Assert.IsGreaterThan(PayloadSource.PieceLength + Slack, table, "premise: the table is no longer than a piece on the heap");

            QuotientFilter.ReadFrom(new MemoryStream(bytes, writable: false));
            var ordinary = NoGCRegion.Allocated(() => QuotientFilter.ReadFrom(new MemoryStream(bytes, writable: false)));
            var charged = CapacityModel.ChargeOf<QuotientFilter>(StructureId.QuotientFilter, bytes);

            var large = LargeForm.WriteLarge(filter);
            Assert.IsTrue(LargeForm.IsLarge(large), "the filter was not written in the large form");
            QuotientFilter.ReadFrom(new MemoryStream(large, writable: false));
            var streamed = NoGCRegion.Allocated(() => QuotientFilter.ReadFrom(new MemoryStream(large, writable: false)));
            var budget = new ReadBudget(long.MaxValue, StructureId.QuotientFilter);
            CollectionAssert.AreEqual(bytes,
                QuotientFilter.ReadFrom(new MemoryStream(large, writable: false), new ReadOptions(budget)).ToByteArray(),
                "the large form does not read back as the filter");

            Console.WriteLine(
                $"[HOLDS ONCE] QuotientFilter: payload {payload:N0}, table {table:N0}; ordinary read {ordinary:N0}, " +
                $"charged {charged:N0}; large-form read {streamed:N0}, charged {budget.Charged:N0}");

            Assert.IsGreaterThanOrEqualTo(payload + table, ordinary,
                $"the read allocated {ordinary:N0} bytes, less than the {payload:N0}-byte payload it holds and the {table:N0}-byte table");
            Assert.IsLessThanOrEqualTo(payload + table + Slack, ordinary,
                $"the read allocated {ordinary:N0} bytes, {ordinary - payload - table:N0} more than its payload and its table");
            Assert.IsGreaterThanOrEqualTo(payload + table, charged,
                $"the read was charged {charged:N0} bytes, less than its payload and its table");
            Assert.IsLessThanOrEqualTo(payload + table + Slack, charged,
                $"the read was charged {charged:N0} bytes, {charged - payload - table:N0} more than its payload and its table");

            Assert.IsGreaterThanOrEqualTo(table, streamed,
                $"the read of the large form allocated {streamed:N0} bytes, less than the {table:N0}-byte table");
            Assert.IsLessThanOrEqualTo(table + Slack, streamed,
                $"the read of the large form allocated {streamed:N0} bytes, {streamed - table:N0} more than its table");
            Assert.IsGreaterThanOrEqualTo(table, budget.Charged,
                $"the read of the large form was charged {budget.Charged:N0} bytes, less than its table");
            Assert.IsLessThanOrEqualTo(table + Slack, budget.Charged,
                $"the read of the large form was charged {budget.Charged:N0} bytes, {budget.Charged - table:N0} more than its table");
        }

        /// <summary>
        /// A read charges the filter it builds at least what building it allocates, and less
        /// than a small object past that (#336): its table, and beyond the table the filter,
        /// which comes to more than one small object allows for. The builds are counted in a
        /// no-GC region.
        /// </summary>
        /// <remarks>
        /// Probed on 6ce3fe1, the least of seven builds in a no-GC region, all seven agreeing,
        /// came to 136 bytes beyond the table at every shape here and at two more, 2^20 slots
        /// and a remainder of 29 bits in the one-slot table: the filter, 96 bytes, which is what
        /// the constructor alone allocates beyond its table at four shapes, and the 40-byte
        /// table of two words that the constructor gave the one-item filter <c>Build</c> started
        /// from. The read charged them as <see cref="ReadBudget.SmallObject"/>, 64: 72 bytes a
        /// read under what it allocated, which the slack of every read-budget sample hid, and
        /// then as three, 192. Since <c>Build</c> makes the filter at the payload's widths
        /// (#343), it allocates the filter alone beyond the table, 96 bytes, which
        /// <see cref="TestBuildingTheFilterAReadMakesAllocatesOnlyTheFilterAndItsTable"/> holds it
        /// to, and the read charges two small objects, 128: 32 past it, where three would be 96
        /// and one 32 short. The shapes are the narrowest table, where the filter is the
        /// largest share of what is allocated, the widest remainder, the issue's filter for 100
        /// items at 1%, a table of 2^16 slots, and the one-slot table a payload of version 1
        /// holds for 32 quotient bits. The first build of each is not counted, so that what the
        /// runtime does once is not taken for the filter.
        /// </remarks>
        [TestMethod]
        public void TestAReadIsChargedWhatBuildingItsFilterAllocates()
        {
            var hash = Defaults.GetDefaultHashFunction();
            foreach (var (q, r, wrapped) in new (uint, uint, bool)[]
            {
                (1, 1, false), (2, 61, false), (8, 7, false), (16, 16, false), (32, 7, true),
            })
            {
                var slots = QuotientFilter.Build(q, r, wrapped, hash).SlotCount;
                var shape = $"a filter of {slots:N0} slots of {r}-bit remainders{(wrapped ? ", at version 1," : "")}";
                var charged = QuotientFilter.TableCost(slots, r);
                var allocated = NoGCRegion.Allocated(() => QuotientFilter.Build(q, r, wrapped, hash));
                Assert.IsLessThanOrEqualTo(charged, allocated,
                    $"{shape} allocated {allocated:N0} bytes to build for a read, and a read is charged {charged:N0} for it");
                Assert.IsLessThan(ReadBudget.SmallObject, charged - allocated,
                    $"{shape} is charged {charged:N0} bytes, {charged - allocated:N0} past the {allocated:N0} building it allocated");
            }
        }

        /// <summary>
        /// Building the filter a read makes allocates the filter and the table shaped to the
        /// payload, and nothing else (#343): beyond that table, <c>Build</c> allocates exactly
        /// what the constructor allocates beyond its own, which is the filter object. The
        /// builds are counted in a no-GC region.
        /// </summary>
        /// <remarks>
        /// <c>Build</c> went through <c>new QuotientFilter(1, 0.5, hash)</c>, whose table of two
        /// words, 40 bytes, it threw away when it shaped the filter to the payload, so every
        /// read made a table it never used: 136 bytes beyond the table, where the constructor
        /// allocates 96 beyond its own (#336). The constructor's figure is measured here, at
        /// the narrowest table and at one of 2^17 slots, rather than written down, so the test
        /// holds <c>Build</c> to the filter object whatever size the runtime makes it, and the
        /// two shapes have to agree, so that a table is not mistaken for the object. Exact
        /// rather than bounded: a table of words costs exactly its header and its words, so
        /// any difference is something else allocated. The slots each table should have are
        /// worked out from the widths, not read back from the filter built. The shapes and
        /// the warm-up are those of <see cref="TestAReadIsChargedWhatBuildingItsFilterAllocates"/>.
        /// </remarks>
        [TestMethod]
        public void TestBuildingTheFilterAReadMakesAllocatesOnlyTheFilterAndItsTable()
        {
            var hash = Defaults.GetDefaultHashFunction();
            static long TableOf(ulong slots, uint r) =>
                ReadBudget.ArrayOf((ulong)QuotientFilter.TableWords(slots, r), sizeof(ulong));

            var objects = new List<long>();
            foreach (var (n, rate) in new (ulong, double)[] { (1, 0.5), (50_000, 0.001) })
            {
                var (q, r) = QuotientFilter.Split(n, rate);
                _ = new QuotientFilter(n, rate, hash);
                var built = NoGCRegion.Allocated(() => new QuotientFilter(n, rate, hash));
                objects.Add(built - TableOf(1UL << (int)q, r));
            }

            var filter = objects[0];
            Assert.AreEqual(filter, objects[1],
                $"the constructor allocated {objects[0]:N0} bytes beyond its table at one shape and {objects[1]:N0} at another");
            Assert.IsGreaterThan(0L, filter,
                $"the constructor allocated {filter:N0} bytes beyond its table, so there is no filter object to hold a build to");

            foreach (var (q, r, wrapped) in new (uint, uint, bool)[]
            {
                (1, 1, false), (2, 61, false), (8, 7, false), (16, 16, false), (32, 7, true),
            })
            {
                var slots = wrapped ? 1UL : 1UL << (int)q;
                Assert.AreEqual(slots, QuotientFilter.Build(q, r, wrapped, hash).SlotCount,
                    $"a filter built from {q} quotient bits{(wrapped ? " at version 1" : "")} has the wrong slots");
                var shape = $"a filter of {slots:N0} slots of {r}-bit remainders{(wrapped ? ", at version 1," : "")}";
                var table = TableOf(slots, r);
                var allocated = NoGCRegion.Allocated(() => QuotientFilter.Build(q, r, wrapped, hash));
                Assert.AreEqual(table + filter, allocated,
                    $"{shape} allocated {allocated:N0} bytes to build for a read: {allocated - table:N0} beyond its " +
                    $"table of {table:N0}, where the filter the constructor makes is {filter:N0}");
            }
        }

        /// <summary>
        /// A payload of every version a quotient filter reads is priced at what its reader
        /// charges (#343). Walked on its own, the price reads the count at the width its
        /// version gives and prices the table and the filter the reader charges for, and the
        /// read, refused under a budget of nothing, names that and the envelope, loads under
        /// it, and is refused under a byte less.
        /// </summary>
        /// <remarks>
        /// A price that read the count as a <c>u64</c> at every version passed the whole suite
        /// before this test (#343). A payload of version 1 counts in a <c>u32</c>, so that price
        /// read the run's length into the count and the table's first bytes as the run's length,
        /// and refused the payload. A refused price is set aside: the read is admitted at what
        /// was priced by then, the envelope alone, and its reader then charges the table. So
        /// the one-slot table, refused under a budget of nothing, named 192 bytes, where its
        /// read takes 360. Each half of the test fails on that price by itself. The payloads are
        /// the three stored fixtures, two of version 1 and one of version 12, and a filter
        /// written now, and the versions they cover are held to the ones a quotient filter
        /// reads, so that a version added without a payload here fails.
        /// </remarks>
        [TestMethod]
        public void TestAPayloadOfEveryVersionIsPricedAtWhatItsReaderCharges()
        {
            var now = new QuotientFilter(100, 0.01);
            for (var i = 0; i < 50; i++) now.Add(Key($"k{i}"));
            var payloads = new (string Name, byte[] Bytes)[]
            {
                ("one slot", QuotientFilterFixture("quotientfilter-q32-v1.bin")),
                ("quotientfilter-v1.bin", QuotientFilterFixture("quotientfilter-v1.bin")),
                ("quotientfilter-v12.bin", QuotientFilterFixture("quotientfilter-v12.bin")),
                ("100 at 1%, half full, written now", now.ToByteArray()),
            };

            var versions = new HashSet<ushort>();
            foreach (var (name, bytes) in payloads)
            {
                var version = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
                versions.Add(version);
                var payload = bytes[14..^4];
                var remainderBits = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4));
                var unlimited = Persistence.FromByteArray<QuotientFilter>(bytes);
                var cost = QuotientFilter.TableCost(unlimited.SlotCount, remainderBits);
                var what = $"{name}, at version {version}";

                var budget = new ReadBudget(long.MaxValue, StructureId.QuotientFilter);
                var reader = new PayloadReader(payload, budget);
                try
                {
                    QuotientFilter.Price(ref reader, version);
                }
                catch (InvalidDataException e)
                {
                    Assert.Fail($"{what}: its price refused it, so it is set aside and the read admitted at less: {e.Message}");
                }
                Assert.AreEqual(cost, budget.Charged,
                    $"{what}: its price priced {budget.Charged:N0} bytes, and its reader charges {cost:N0} for the table and the filter");

                var total = ReadBudget.Sum(PersistenceFormat.EnvelopeCost((ulong)payload.Length), cost);
                var nothing = Assert.ThrowsExactly<InvalidDataException>(
                    () => Persistence.FromByteArray<QuotientFilter>(bytes, new ReadOptions(0)), $"{what} loaded under nothing");
                Assert.IsInstanceOfType<ReadBudgetExceededException>(nothing.InnerException, $"{what}: {nothing.Message}");
                Assert.AreEqual(total, ((ReadBudgetExceededException)nothing.InnerException).RequiredBytes,
                    $"{what}, refused under a budget of nothing, named the wrong total");
                CollectionAssert.AreEqual(unlimited.ToByteArray(),
                    Persistence.FromByteArray<QuotientFilter>(bytes, new ReadOptions(total)).ToByteArray(),
                    $"{what}, read under the {total:N0} bytes it named, is not the filter it reads as under no limit");
                var under = Assert.ThrowsExactly<InvalidDataException>(
                    () => Persistence.FromByteArray<QuotientFilter>(bytes, new ReadOptions(total - 1)), $"{what} loaded a byte under {total:N0}");
                Assert.IsInstanceOfType<ReadBudgetExceededException>(under.InnerException, $"{what}: {under.Message}");
            }

            CollectionAssert.AreEquivalent(PersistenceFormat.ReadableVersions(StructureId.QuotientFilter).ToArray(), versions.ToArray(),
                "the payloads' versions are not exactly the ones a quotient filter reads");
        }

        /// <summary>
        /// A quotient filter's price prices its table before it checks that the payload ends
        /// after it, as its reader charges the table before it checks, so a payload holding a
        /// byte past the table is refused for that byte under exactly its own price, in either
        /// envelope (#343).
        /// </summary>
        /// <remarks>
        /// The table is the payload's last run, so a cut that holds it holds the payload whole,
        /// and no cut of a sweep can tell where it is priced. A byte left over can: a price that
        /// checked the end first would stop there having priced nothing for the table, the read
        /// would be admitted at the envelope alone, and its reader, charging the table before it
        /// finds the byte, would be refused for a budget its own price fits. Under a byte less
        /// than that price it is refused for its budget, naming the price, so the budget the read
        /// is held to has no room in it. The table is the smallest the constructor builds. An
        /// invertible table is held to the same in
        /// <c>TestAnInvertibleTableWithAByteLeftOverIsRefusedForItUnderItsOwnPrice</c>, and every
        /// structure's read, though not its price, in <see cref="TestBytesLeftOver"/>.
        /// </remarks>
        [TestMethod]
        public void TestAFilterWithAByteLeftOverIsRefusedForItUnderItsOwnPrice()
        {
            var filter = new QuotientFilter(1, 0.5);
            filter.Add(Key("a"));
            var ordinary = filter.ToByteArray();
            var large = LargeForm.WriteLarge(filter);
            Assert.IsFalse(LargeForm.IsLarge(ordinary), "premise: the filter was not written in the ordinary envelope");
            Assert.IsTrue(LargeForm.IsLarge(large), "premise: the filter was not written in the large form");
            foreach (var whole in new[] { ordinary, large })
            {
                TestBytesLeftOver.AssertRefusedForTheByteUnderItsOwnPrice(StructureId.QuotientFilter, QuotientFilter.Price,
                    (bytes, options) => Persistence.FromByteArray<QuotientFilter>(bytes, options), whole);
            }
        }

        [TestMethod]
        public void TestTheHashIsSettledOnceAnythingIsAdded()
        {
            Func<ReadOnlySpan<byte>, ulong> custom = d => (ulong)d.Length;

            var fresh = new QuotientFilter(100, 0.01);
            fresh.SetHash(custom);

            var used = new QuotientFilter(100, 0.01);
            used.Add(Key("a"));
            Assert.ThrowsExactly<InvalidOperationException>(() => used.SetHash(custom));

            // Emptying it makes the hash replaceable again, because there is then
            // nothing that was placed by the old one.
            used.Reset();
            Assert.AreEqual(0u, used.Count());
            Assert.IsFalse(used.Test(Key("a")));
            used.SetHash(custom);
        }

        [TestMethod]
        public void TestBadArgumentsAreRefused()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new QuotientFilter(0, 0.01));

            foreach (var bad in new[] { 0.0, 1.0, -0.5, 2.0, double.NaN })
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => new QuotientFilter(100, bad));
            }

            var filter = new QuotientFilter(100, 0.01);
            Assert.ThrowsExactly<ArgumentNullException>(() => filter.Add(null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => filter.Test(null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => filter.TestAndRemove(null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => filter.SetHash(null!));
        }

        /// <summary>
        /// A payload that does not describe a table this library builds is refused. The
        /// checksum is repaired after each edit, so a guard has to catch it.
        /// </summary>
        [TestMethod]
        public void TestAnImpossiblePayloadIsRefused()
        {
            var filter = new QuotientFilter(1000, 0.01);
            for (var i = 0; i < 100; i++)
            {
                filter.Add(Key($"k{i}"));
            }

            var clean = filter.ToByteArray();

            AssertRefused(Poke(clean, 0, 0), "quotient bits");
            AssertRefused(Poke(clean, 0, 40), "quotient bits");

            // One past what a payload of version 12, which every save is, may say: the
            // widest table is 2^34 slots. Version 1 stops at 32, which is read as the
            // one-slot table earlier constructors wrapped to, and has its own test beside
            // the stored payloads; TestQuotientFilterLimitIsShared refuses 33 there.
            AssertRefused(Poke(clean, 0, LargestQuotientBits + 1), "quotient bits");
            AssertRefused(Poke(clean, 4, 0), "not a split");
            AssertRefused(Poke(clean, 4, 60), "not a split");
            AssertRefused(Poke(clean, 8, 999999), "slots");
        }

        /// <summary>
        /// The two widths are <c>u32</c>s, and the reader added them in 32 bits, so a remainder
        /// within the quotient's width of 2^32 wrapped the sum to a few bits and passed the
        /// check that the widths split the hash. The slot's width, the remainder and three
        /// bits of metadata, wraps the same way in the table's word count, to 0, 1 or 2 bits
        /// from the last three remainders. A table of eight slots or fewer, small enough to
        /// be two words at its own width, is still two words at those, so its payload with
        /// only the remainder edited read: two slots of version 12 at a remainder of
        /// 2^32 - 1 were two slots of 2 bits, and a key added to them was then not found. The
        /// one slot of version 1 read the same way. A larger table was refused, but for its
        /// bytes or for its array, never for its split.
        /// <para>
        /// Held at the two small tables and at the stored fixtures of both versions, either
        /// side of 64 bits and either side of the wrap, which moves with the quotient: a sum
        /// of 2^32 - 1 is refused for the split whatever the arithmetic, and 2^32 is the
        /// first that a 32-bit sum wraps.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestASplitPastTheHashIsRefusedWhereA32BitSumOfItsWidthsWraps()
        {
            const string why = "not a split of a";
            var payloads = new (string Name, int Version, byte[] Bytes)[]
            {
                ("two slots", 12, new QuotientFilter(1, 0.5).ToByteArray()),
                ("one slot", 1, QuotientFilterFixture("quotientfilter-q32-v1.bin")),
                ("quotientfilter-v1.bin", 1, QuotientFilterFixture("quotientfilter-v1.bin")),
                ("quotientfilter-v12.bin", 12, QuotientFilterFixture("quotientfilter-v12.bin")),
            };

            foreach (var (name, expectedVersion, bytes) in payloads)
            {
                var quotientBits = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(14));
                var version = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
                Assert.AreEqual(expectedVersion, version, $"premise: {name} is at version {expectedVersion}");

                // A split of exactly 64 bits is one; what refuses it, if anything does, is the
                // bytes the edit left behind.
                try
                {
                    Persistence.FromByteArray<QuotientFilter>(Poke(bytes, 4, 64 - quotientBits));
                }
                catch (InvalidDataException ex)
                {
                    Assert.DoesNotContain(why, ex.Message, $"{name}: a split of 64 bits was refused as no split");
                }

                foreach (var remainderBits in new ulong[]
                    { 65 - quotientBits, (1UL << 32) - quotientBits - 1, (1UL << 32) - quotientBits, uint.MaxValue })
                {
                    var ex = Assert.ThrowsExactly<InvalidDataException>(
                        () => Persistence.FromByteArray<QuotientFilter>(Poke(bytes, 4, (uint)remainderBits)),
                        $"{name} with {remainderBits} remainder bits read");
                    StringAssert.Contains(ex.Message, why, $"{name} with {remainderBits} remainder bits");
                }
            }
        }

        [TestMethod]
        public void TestTheTableIsSizedForTheItemsAndTheRate()
        {
            var filter = new QuotientFilter(10000, 0.01);

            // Every slot carries its remainder plus three metadata bits, so the whole
            // table is that many bits per slot and nothing else.
            Assert.AreEqual(
                (ulong)filter.Capacity() * filter.BitsPerSlot() / 8,
                filter.SizeInBytes(),
                "the table costs more than its slots account for");

            // Sized past the item count, since a quotient filter's runs lengthen as it
            // fills and lookups walk them.
            Assert.IsGreaterThan(10000u, filter.Capacity());
        }

        // What the constructor will build, restated as plain arithmetic rather than read
        // back from the filter. A fingerprint is the top q + r bits of a 64-bit hash, so
        // q + r cannot exceed 64. A slot is r + 3 bits read and written through one ulong,
        // so r cannot exceed 61 whatever q is. And a table of 2^q slots is one array of
        // 64-bit words, floor(2^q (r + 3) / 64) + 2 of them, and an array holds
        // 2,147,483,591: at 2^35 slots of the narrowest slot, four bits, that is 2^31 + 2,
        // so q cannot exceed 34, and past 31 the array also bounds r.

        private const int HashBits = 64;
        private const int MetadataBits = 3;
        private const int LargestSlotBits = 64;
        private const long LargestArray = 2_147_483_591;
        private const int LargestQuotientBits = 34;

        // Three quarters of the 2^34 slots the largest table has, the load factor the
        // filter is sized for: the most items it will be asked to hold.
        private const ulong LargestItemCount = 3UL << 32;

        /// <summary>FORMAT.md's words of a table of 2^q slots of r + 3 bits.</summary>
        private static long Words(int q, int r) => (((1L << q) * (r + MetadataBits)) >> 6) + 2;

        /// <summary>
        /// The quotient width for n items: the fewest bits whose table, kept to three
        /// quarters full, holds them. Restated in integers, so that it agrees with the
        /// filter's own logarithm only if both are right.
        /// </summary>
        private static int QuotientBitsFor(uint n)
        {
            var q = 1;
            while (4UL * n > 3UL << q)
            {
                q++;
            }

            return q;
        }

        /// <summary>
        /// The widest remainder a filter sized for n items can hold: what the hash has
        /// left after the quotient, and never more than a slot can carry.
        /// </summary>
        private static int FinestRemainderBits(uint n)
        {
            return Math.Min(LargestSlotBits - MetadataBits, HashBits - QuotientBitsFor(n));
        }

        /// <summary>
        /// Item counts at and just past every step in the quotient width, for the widths
        /// a test can afford. n = 3 * 2^(q-2) fills a table of 2^q slots to its load
        /// factor exactly and one more needs the next width, and the width is a ceiling,
        /// so a count that avoids the steps says nothing about where the limit falls.
        /// Across them the limit moves from the slot (n up to 6) to the hash (beyond).
        /// </summary>
        private static IEnumerable<uint> CountsAcrossTheSteps()
        {
            yield return 1;
            for (var q = 2; q <= 12; q++)
            {
                var step = 3u << (q - 2);
                yield return step;
                yield return step + 1;
            }
        }

        /// <summary>
        /// At the finest rate a filter of each size can honour, the fingerprint is as wide
        /// as the hash lets it be and the filter must still behave: every stored
        /// fingerprint is the one the hash gives, every answer is the model's, and the
        /// payload reads back. Fingerprints wider than the hash used to collapse to a few
        /// of its top bits, which answered yes to every key at a measured rate of 1.00.
        /// </summary>
        [TestMethod]
        public void TestTheFinestRateAFilterCanHonourIsHonoured()
        {
            const int Probes = 2000;
            var hash = Defaults.GetDefaultHashFunction();

            foreach (var n in CountsAcrossTheSteps())
            {
                var q = QuotientBitsFor(n);
                var r = FinestRemainderBits(n);
                var filter = new QuotientFilter(n, Math.ScaleB(1.0, -r));

                Assert.AreEqual((uint)(r + MetadataBits), filter.BitsPerSlot(),
                    $"n = {n}: the remainder is not the widest this size allows.");
                Assert.AreEqual(1u << q, filter.Capacity(), $"n = {n}: the table's size.");

                var keys = new List<byte[]>();
                var model = new HashSet<ulong>();
                var held = new List<ulong>();
                for (var i = 0u; i < n; i++)
                {
                    var key = Key($"n{n}-item-{i}");
                    keys.Add(key);
                    filter.Add(key);

                    var fingerprint = hash(key) >> (HashBits - (q + r));
                    model.Add(fingerprint);
                    held.Add(fingerprint);
                }

                var stored = QuotientFamilyInvariants.CheckQuotientFilter(filter, out var payload);
                CollectionAssert.AreEqual(held.OrderBy(f => f).ToArray(), stored,
                    $"n = {n}: the table does not hold the top {q + r} bits of each hash.");

                foreach (var key in keys)
                {
                    Assert.IsTrue(filter.Test(key), $"n = {n}: an added item is not found.");
                }

                // The absent keys' answers are the model's, collisions included, and
                // at this width the model predicts that nothing collides. That is the
                // measured rate, against the 1.00 the collapse gave.
                var hits = 0;
                for (var i = 0; i < Probes; i++)
                {
                    var probe = Key($"n{n}-absent-{i}");
                    var expected = model.Contains(hash(probe) >> (HashBits - (q + r)));
                    Assert.AreEqual(expected, filter.Test(probe),
                        $"n = {n}: absent key {i} has {(expected ? "a" : "no")} " +
                        "fingerprint collision with what is stored.");
                    hits += expected ? 1 : 0;
                }

                Assert.AreEqual(0, hits, $"n = {n}: the model itself predicts false positives.");

                var back = Persistence.FromByteArray<QuotientFilter>(payload);
                CollectionAssert.AreEqual(payload, back.ToByteArray(),
                    $"n = {n}: the payload does not read back as the table it was.");
                foreach (var key in keys)
                {
                    Assert.IsTrue(back.Test(key), $"n = {n}: the restored filter lost an item.");
                }

                foreach (var key in keys)
                {
                    Assert.IsTrue(filter.TestAndRemove(key), $"n = {n}: a held item would not go.");
                }

                Assert.AreEqual(0u, filter.Count());
                Assert.AreEqual(0, QuotientFamilyInvariants.CheckQuotientFilter(filter, out _).Length,
                    $"n = {n}: the table is not empty after everything was removed.");
            }
        }

        /// <summary>
        /// A rate finer than the fingerprint can hold is refused rather than built, and
        /// the refusal names the finest rate that would have been. The bound is derived
        /// from each count, at and across every step in the quotient width, and probed
        /// just past it two ways: a whole bit finer, and one percent finer, where the
        /// remainder's ceiling takes it over by exactly one bit. The extremes follow,
        /// including rates so small that their reciprocal is not a number.
        /// </summary>
        [TestMethod]
        public void TestARateFinerThanTheFingerprintCanHoldIsRefused()
        {
            foreach (var n in CountsAcrossTheSteps())
            {
                var finest = Math.ScaleB(1.0, -FinestRemainderBits(n));
                var named = finest.ToString("R", CultureInfo.InvariantCulture);

                foreach (var tooFine in new[] { finest / 2, finest * 0.99, 1e-300, 1e-320, double.Epsilon })
                {
                    var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                        () => new QuotientFilter(n, tooFine),
                        $"n = {n}, rate {tooFine:R}: a rate finer than {named} was accepted.");

                    Assert.AreEqual("fpRate", ex.ParamName);
                    Assert.AreEqual(tooFine, ex.ActualValue);
                    StringAssert.Contains(ex.Message, named,
                        $"n = {n}: the refusal does not say what rate would have worked.");
                }
            }

            // The case that was reported: 1,000 items need 11 quotient bits, which leaves
            // 53 for the remainder, so 1e-15 is fine and 1e-16 is a bit past it.
            _ = new QuotientFilter(1000, 1e-15);
            foreach (var tooFine in new[] { 1e-16, 1e-17 })
            {
                var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => new QuotientFilter(1000, tooFine));
                Assert.AreEqual("fpRate", ex.ParamName);
            }
        }

        /// <summary>
        /// More items than the largest table is sized for is refused, and the refusal
        /// names the most it will take. The table is one array of 64-bit words, so it stops
        /// at 2^34 slots, and an item count past three quarters of that needs 2^35. Until
        /// #231 the slot count was a uint, which stopped it at 2^31, and a count past that
        /// shifted into a table of one slot and then two.
        /// </summary>
        [TestMethod]
        public void TestAnItemCountPastTheLargestTableIsRefused()
        {
            var named = LargestItemCount.ToString(CultureInfo.InvariantCulture);

            foreach (var n in new[] { LargestItemCount + 1, 13_000_000_000UL, ulong.MaxValue })
            {
                var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => new QuotientFilter(n, 0.5),
                    $"n = {n} is more than the largest table is sized for and was accepted.");

                Assert.AreEqual("n", ex.ParamName);
                Assert.AreEqual(n, ex.ActualValue);
                StringAssert.Contains(ex.Message, named,
                    $"n = {n}: the refusal does not say how many items would have worked.");
            }
        }

        /// <summary>
        /// The other side of both limits, without building what they describe: a table
        /// of 2^31 slots or more is gigabytes, so the sizing is asked directly. It is the
        /// same sizing the constructor runs, so what it accepts here is what the
        /// constructor would build.
        /// </summary>
        [TestMethod]
        public void TestTheLargestTableAndTheFinestRateAreStillSized()
        {
            Assert.IsLessThanOrEqualTo(LargestArray, Words(LargestQuotientBits, 1),
                "the narrowest slots of the widest table have to fit one array");
            Assert.IsGreaterThan(LargestArray, Words(LargestQuotientBits + 1, 1),
                "the next width up is one whose narrowest slots no array holds, which is why it is the limit");

            Assert.AreEqual((34u, 1u), QuotientFilter.Split(LargestItemCount, 0.5));
            // And the step below it, so the widest width is what the count needs and not
            // simply the most that is allowed: 3 * 2^31 items fill 2^33 slots exactly.
            Assert.AreEqual((33u, 1u), QuotientFilter.Split(3UL << 31, 0.5));
            Assert.AreEqual((34u, 1u), QuotientFilter.Split((3UL << 31) + 1, 0.5));
            // And the step from 2^31 slots to 2^32, which a slot count in a uint wrapped.
            Assert.AreEqual((31u, 1u), QuotientFilter.Split(3UL << 29, 0.5));
            Assert.AreEqual((32u, 1u), QuotientFilter.Split((3UL << 29) + 1, 0.5));

            // Where the limits meet, at the widest tables: at 2^31 slots the 33 bits the
            // hash leaves, which one array holds, and past it the widest remainder one
            // array holds. Each pair is held to the arithmetic here, in both directions, and
            // the item count that fills its table is sized at that rate and refused, naming
            // it, one bit finer.
            foreach (var (q, r) in new[] { (31, 33), (32, 28), (33, 12), (34, 4) })
            {
                Assert.IsLessThanOrEqualTo(LargestArray, Words(q, r), $"2^{q} slots of {r + MetadataBits} bits");
                Assert.IsTrue(r == HashBits - q || Words(q, r + 1) > LargestArray,
                    $"2^{q} slots: a remainder of {r + 1} bits is a split of the hash and fits one array");

                var n = 3UL << (q - 2);
                var finest = Math.ScaleB(1.0, -r);
                Assert.AreEqual(((uint)q, (uint)r), QuotientFilter.Split(n, finest), $"2^{q} slots");
                var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => QuotientFilter.Split(n, finest / 2), $"2^{q} slots: a rate one bit finer was sized");
                StringAssert.Contains(ex.Message, finest.ToString("R", CultureInfo.InvariantCulture),
                    $"2^{q} slots: the refusal does not name the finest rate that works");

                // And it names the limit that binds, which is the hash at 2^31 slots and the
                // array past it; each is held to the other's absence, so a refusal that always
                // names one of them fails at the other's width.
                var array = $"one array of 64-bit words, which holds at most {LargestArray} of them";
                var hash = $"The {q} bits that pick a slot leave {r} of the hash's {HashBits} for the remainder";
                if (r == HashBits - q)
                {
                    StringAssert.Contains(ex.Message, hash, $"2^{q} slots: the hash binds, and the refusal says otherwise");
                    Assert.DoesNotContain("one array", ex.Message, $"2^{q} slots: the array does not bind here");
                }
                else
                {
                    StringAssert.Contains(ex.Message, array, $"2^{q} slots: the array binds, and the refusal says otherwise");
                    Assert.DoesNotContain("bits that pick a slot leave", ex.Message, $"2^{q} slots: the hash leaves more than this");
                }
            }
        }

        /// <summary>
        /// Past 2^32 slots a quotient has more bits than a uint holds, and the cut of a hash
        /// keeps every one of them (#231). At each width from 31 to 34, at its widest
        /// remainder, the quotient is the hash's top q bits and the remainder the r bits below
        /// them, restated here in shifts of the hash itself; the hashes set the top bit, the
        /// bit at 2^32 of the quotient, or every bit. No table that wide is built here; the cut
        /// is the one every add and lookup goes through. The one-slot table of version 1 has a
        /// mask of no bits, and cuts every hash to quotient 0 and the 20 bits below its 32.
        /// </summary>
        [TestMethod]
        public void TestAHashIsCutIntoAQuotientOfEveryBitPastThirtyTwo()
        {
            var hashes = new[] { ulong.MaxValue, 1UL << 63, 0x9E3779B97F4A7C15UL, 0x0123456789ABCDEFUL, 1UL << 31 };
            foreach (var (q, r) in new[] { (31, 33), (32, 28), (33, 12), (34, 4) })
            {
                var slotMask = (1UL << q) - 1;
                var remainderMask = (1UL << r) - 1;
                foreach (var hash in hashes.Append(1UL << (64 - q)))
                {
                    var (quotient, remainder) = QuotientFilter.Cut(hash, (uint)q, (uint)r, slotMask, remainderMask);
                    Assert.AreEqual(hash >> (64 - q), quotient, $"q {q}: the quotient of {hash:X16}");
                    Assert.AreEqual((hash >> (64 - q - r)) & remainderMask, remainder, $"q {q}: the remainder of {hash:X16}");
                }
            }

            Assert.IsGreaterThan((ulong)uint.MaxValue,
                QuotientFilter.Cut(ulong.MaxValue, 33, 12, (1UL << 33) - 1, (1UL << 12) - 1).Quotient,
                "premise: a quotient of 33 bits passes what a uint holds");

            foreach (var hash in hashes)
            {
                Assert.AreEqual((0UL, (hash >> 12) & ((1UL << 20) - 1)),
                    QuotientFilter.Cut(hash, 32, 20, 0, (1UL << 20) - 1), $"the one-slot table's cut of {hash:X16}");
            }
        }

        /// <summary>
        /// A filter merges only with one of the same table. Every split names its table but
        /// one: a payload of version 1 at 32 quotient bits is the one slot that a constructor
        /// of 6.2.0 or earlier wrapped a request past its largest table to, and a table of 2^32
        /// slots says 32 bits now. Their quotients mean different slots, so the merge is
        /// refused, whichever way round, before anything is written. A table of 2^32 slots is
        /// too large to build here, so a small filter of the same remainder claims one; the
        /// refusal comes before either table is read.
        /// </summary>
        [TestMethod]
        public void TestAOneSlotTableFromVersionOneDoesNotMergeWithATableOfTwoToTheThirtyTwoSlots()
        {
            const BindingFlags Fields = BindingFlags.NonPublic | BindingFlags.Instance;
            var stored = QuotientFilterFixture("quotientfilter-q32-v1.bin");
            var wrapped = QuotientFilter.ReadFrom(new MemoryStream(stored));
            Assert.AreEqual(1UL, wrapped.Capacity(), "premise: the stored table is one slot");

            var wide = new QuotientFilter(100, Math.ScaleB(1.0, -20));
            Assert.AreEqual(wrapped.BitsPerSlot(), wide.BitsPerSlot(), "premise: the same remainder");
            typeof(QuotientFilter).GetField("quotientBits", Fields)!.SetValue(wide, 32u);
            typeof(QuotientFilter).GetField("slots", Fields)!.SetValue(wide, 1UL << 32);
            typeof(QuotientFilter).GetField("slotMaskIndex", Fields)!.SetValue(wide, (1UL << 32) - 1);
            var wideBefore = (ulong[])((ulong[])typeof(QuotientFilter).GetField("data", Fields)!.GetValue(wide)).Clone();

            foreach (var (into, from) in new[] { (wrapped, wide), (wide, wrapped) })
            {
                var ex = Assert.ThrowsExactly<ArgumentException>(() => into.Merge(from));
                Assert.AreEqual("other", ex.ParamName);
                StringAssert.Contains(ex.Message, "same table", ex.Message);
            }

            CollectionAssert.AreEqual(stored, wrapped.ToByteArray(), "the refused merge changed the one-slot table");
            CollectionAssert.AreEqual(wideBefore,
                (ulong[])typeof(QuotientFilter).GetField("data", Fields)!.GetValue(wide),
                "the refused merge changed the other table");
        }

        private static byte[] QuotientFilterFixture(string name)
        {
            using var stream = typeof(TestQuotientFilter).Assembly
                .GetManifestResourceStream($"TestProbabilisticDataStructures.fixtures.{name}")!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        private static void AssertRefused(byte[] payload, string expected)
        {
            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<QuotientFilter>(payload));
            StringAssert.Contains(ex.Message, expected);
        }

        private static byte[] Poke(byte[] original, int payloadOffset, uint value)
        {
            var bytes = (byte[])original.Clone();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(14 + payloadOffset), value);

            var crc = new System.IO.Hashing.Crc32();
            crc.Append(bytes.AsSpan(4, bytes.Length - 8));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(bytes.Length - 4), crc.GetCurrentHashAsUInt32());

            return bytes;
        }

    }
}
