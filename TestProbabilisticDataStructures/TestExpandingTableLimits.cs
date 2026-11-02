using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// How large the tables of an <see cref="InfiniFilter"/> and a
    /// <see cref="MementoFilter"/> can grow, and what happens at the edge.
    /// </summary>
    /// <remarks>
    /// Both double their table when it fills, and a table's slot count was
    /// <c>1u &lt;&lt; quotientBits</c>. A shift by 32 or more is a shift by that count
    /// modulo 32 in C#, so a table of 2^32 slots was built with one, and one of 2^33 with
    /// two. The tables double to 2^28 slots at about a hundred million items, so a table of
    /// 2^32 slots is reached at about 1.6 billion items added: big, but a machine builds it.
    /// <para>
    /// What happened there was measured in pieces, because the whole is a table of a
    /// gigabyte and a billion and a half inserts. A segment built at 32 bits of address had
    /// one slot, so an insert at a quotient past the first few threw
    /// IndexOutOfRangeException. An expansion into it would have thrown part way through
    /// moving the old table's entries, and since the filter keeps its old table until the
    /// new one is built, the filter survived, with an exception that said nothing of why.
    /// The reader was worse: it read a table of 32 to 40 bits of address as a table of one
    /// to 256 slots (the shift wrapped there too), so a payload claiming 2^40 slots and
    /// holding the words for 256 was accepted and built a segment that disagreed with
    /// itself.
    /// </para>
    /// <para>
    /// A table is a uint's worth of slots, so 2^31 is the largest it can be, and 31 bits of
    /// address the most it can have: the same limit the quotient filter has, for the same
    /// reason. Beyond that is not just wider slot arithmetic: a table of 2^32 slots cannot
    /// be written either, its payload being three gibibytes at the narrowest fingerprint
    /// and an envelope holding two, and expanding into it would list 1.6 billion entries to
    /// move.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestExpandingTableLimits
    {
        private const int PayloadStart = 14;

        private static byte[] Key(int i) => Encoding.UTF8.GetBytes("k" + i);

        /// <summary>
        /// A payload of one table at the given width of address, hand-written, with the
        /// words that the old reader's arithmetic expected -- its slot count was
        /// <c>1u &lt;&lt; quotientBits</c>, which wraps -- so that the width is the only
        /// thing wrong with it.
        /// </summary>
        internal static byte[] OnePayload(
            StructureId id, int quotientBits, int fingerprintBits, bool includeWords,
            int mementoBits = 3)
        {
            var slots = 1u << quotientBits;
            var bitsPerSlot = id == StructureId.MementoFilter
                ? fingerprintBits + 1 + mementoBits + 3
                : fingerprintBits + 1 + 3;
            var words = (uint)((((long)slots * bitsPerSlot) >> 6) + 2);

            var payload = new PayloadWriter();
            payload.WriteUInt64(0);                 // added
            payload.WriteUInt32(0);                 // expansions
            if (id == StructureId.MementoFilter)
            {
                payload.WriteUInt32((uint)mementoBits);
            }

            payload.WriteUInt32(1);                 // tables
            payload.WriteUInt32((uint)quotientBits);
            payload.WriteUInt32((uint)fingerprintBits);
            payload.WriteUInt32(0);                 // entries
            payload.WriteUInt32(words);
            if (includeWords)
            {
                for (var w = 0; w < words; w++)
                {
                    payload.WriteUInt64(0);
                }
            }

            var stream = new MemoryStream();
            PersistenceFormat.Write(
                stream, id,
                id == StructureId.MementoFilter ? HashId.None : HashId.XxHash3_64,
                payload.WrittenSpan);
            return stream.ToArray();
        }

        /// <summary>
        /// A table cannot have more than 31 bits of address, and a payload claiming more is
        /// refused with that said. The first three are the widths whose shifted slot count
        /// is a small number the old reader sized words for and accepted; the last is
        /// refused by the old reader too, for a limit of 40 that nothing builds.
        /// </summary>
        [TestMethod]
        [DataRow(32)]
        [DataRow(33)]
        [DataRow(40)]
        [DataRow(41)]
        public void TestATableWithMoreAddressBitsThanASlotCountHoldsIsRefused(int quotientBits)
        {
            var infini = OnePayload(StructureId.InfiniFilter, quotientBits, 4, includeWords: true);
            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<InfiniFilter>(infini));
            StringAssert.Contains(ex.Message, "between 2 and 31");

            var memento = OnePayload(StructureId.MementoFilter, quotientBits, 4, includeWords: true);
            ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<MementoFilter>(memento));
            StringAssert.Contains(ex.Message, "between 2 and 31");
        }

        /// <summary>
        /// The widest a table can be is read as far as the memory it costs: a table of 2^31
        /// slots is gigabytes, which the default budget refuses for its cost and not its
        /// size. The same payload one bit wider is refused for its size.
        /// </summary>
        [TestMethod]
        public void TestATableAtTheLimitIsRefusedForWhatItCostsNotForItsSize()
        {
            var limit = InfiniFilter.MaxQuotientBits;

            var infini = OnePayload(StructureId.InfiniFilter, limit, 4, includeWords: false);
            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<InfiniFilter>(infini));
            Assert.IsInstanceOfType<ReadBudgetExceededException>(ex.InnerException,
                $"a table of 2^{limit} slots should be refused for its cost: {ex.Message}");

            var memento = OnePayload(StructureId.MementoFilter, MementoFilter.MaxQuotientBits, 4, includeWords: false);
            ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<MementoFilter>(memento));
            Assert.IsInstanceOfType<ReadBudgetExceededException>(ex.InnerException,
                $"a table of 2^{limit} slots should be refused for its cost: {ex.Message}");
        }

        /// <summary>
        /// An expansion that would take the table past the limit is refused before the
        /// filter changes: no entry moved, no table replaced, nothing counted. The limit
        /// is lowered to the width of a table a test can fill; the filter's own is 31.
        /// </summary>
        [TestMethod]
        [DataRow(2)]
        [DataRow(3)]
        public void TestAnInfiniFilterExpansionPastTheLimitIsRefusedBeforeAnythingChanges(int limit)
        {
            var filter = new InfiniFilter(initialCapacity: 3, fingerprintBits: 8) { QuotientBitsLimit = limit };

            // A table of 2^q slots is expanded when it is 3/4 full, on the add after.
            var added = 0;
            var refusedAt = -1;
            for (var i = 0; i < 1000 && refusedAt < 0; i++)
            {
                try
                {
                    filter.Add(Key(i));
                    added++;
                }
                catch (InvalidOperationException)
                {
                    refusedAt = i;
                }
            }

            Assert.IsGreaterThanOrEqualTo(0, refusedAt, "the filter never refused");
            Assert.AreEqual(3 * (1 << (limit - 2)), added,
                "the table did not stop at three quarters of the slots the limit allows");
            Assert.AreEqual((uint)(limit - 2), filter.ExpansionCount());

            var before = filter.ToByteArray();
            var ex = Assert.ThrowsExactly<InvalidOperationException>(() => filter.Add(Key(refusedAt)));
            StringAssert.Contains(ex.Message, (1 << limit).ToString("N0"));
            Assert.ThrowsExactly<InvalidOperationException>(() => filter.Add(Key(refusedAt + 1)));

            Assert.AreEqual((ulong)added, filter.Count());
            Assert.AreEqual((uint)(limit - 2), filter.ExpansionCount());
            Assert.AreEqual((ulong)(1 << limit), filter.Capacity());
            CollectionAssert.AreEqual(before, filter.ToByteArray(), "a refused add changed the filter");
            for (var i = 0; i < added; i++)
            {
                Assert.IsTrue(filter.Test(Key(i)), $"key {i} was lost");
            }
        }

        /// <summary>
        /// The same for the range filter, which keeps its keys in boxes: an add of a key
        /// in a new box is what fills a slot.
        /// </summary>
        [TestMethod]
        [DataRow(2)]
        [DataRow(3)]
        public void TestAMementoFilterExpansionPastTheLimitIsRefusedBeforeAnythingChanges(int limit)
        {
            var filter = new MementoFilter(2, fingerprintBits: 8, initialCapacity: 3) { QuotientBitsLimit = limit };

            var added = 0;
            var refusedAt = -1;
            for (var i = 0; i < 1000 && refusedAt < 0; i++)
            {
                try
                {
                    filter.Add((ulong)i * 1_000);
                    added++;
                }
                catch (InvalidOperationException)
                {
                    refusedAt = i;
                }
            }

            Assert.IsGreaterThanOrEqualTo(0, refusedAt, "the filter never refused");
            Assert.AreEqual(3 * (1 << (limit - 2)), added,
                "the table did not stop at three quarters of the slots the limit allows");
            Assert.AreEqual((uint)(limit - 2), filter.ExpansionCount());

            var before = filter.ToByteArray();
            var ex = Assert.ThrowsExactly<InvalidOperationException>(
                () => filter.Add((ulong)refusedAt * 1_000));
            StringAssert.Contains(ex.Message, (1 << limit).ToString("N0"));
            Assert.ThrowsExactly<InvalidOperationException>(() => filter.Add(1_000_003));

            Assert.AreEqual((ulong)added, filter.Count());
            Assert.AreEqual((uint)(limit - 2), filter.ExpansionCount());
            Assert.AreEqual((ulong)(1 << limit), filter.Capacity());
            CollectionAssert.AreEqual(before, filter.ToByteArray(), "a refused add changed the filter");
            for (var i = 0; i < added; i++)
            {
                Assert.IsTrue(filter.Test((ulong)i * 1_000), $"key {i} was lost");
            }
        }

        /// <summary>
        /// A refused filter is still a filter: removing keys brings it under the
        /// threshold, and then an add goes through.
        /// </summary>
        [TestMethod]
        public void TestARefusedInfiniFilterTakesAnAddAgainOnceKeysAreRemoved()
        {
            var filter = new InfiniFilter(initialCapacity: 3, fingerprintBits: 8) { QuotientBitsLimit = 2 };
            for (var i = 0; i < 3; i++)
            {
                filter.Add(Key(i));
            }

            Assert.ThrowsExactly<InvalidOperationException>(() => filter.Add(Key(3)));
            Assert.IsTrue(filter.TestAndRemove(Key(0)));
            filter.Add(Key(3));
            Assert.IsTrue(filter.Test(Key(3)));
        }

        /// <summary>
        /// Where the first table starts for an initial capacity. The constructor rounded
        /// the capacity up to a power of two in a uint, which saturated rather than
        /// wrapped for the largest, and read 31 out of the saturated value; the same 31
        /// is now a limit written down, and the widths below it are what they were. The
        /// sweep is the old expression restated, over every capacity that does not reach
        /// the saturation, straddling each power of two and its three-quarter point.
        /// </summary>
        [TestMethod]
        public void TestTheFirstTableStartsAtTheWidthsItAlwaysDid()
        {
            var capacities = new List<uint> { 1, 2, 3, 4, 5, 6 };
            for (var bit = 2; bit <= 30; bit++)
            {
                var p = 1u << bit;
                capacities.AddRange(new[] { p - 1, p, p + 1, p / 4 * 3 - 1, p / 4 * 3, p / 4 * 3 + 1 });
            }

            var compared = 0;
            foreach (var capacity in capacities.Where(c => c > 0 && c <= 1_610_612_736u).Distinct())
            {
                var slots = (uint)Math.Max(4.0,
                    Math.Pow(2, Math.Ceiling(Math.Log2(capacity / 0.75))));
                var was = (int)Math.Log2(slots);

                Assert.AreEqual(was, InfiniFilter.InitialQuotientBits(capacity),
                    $"an initial capacity of {capacity} no longer starts the table where it did");
                Assert.AreEqual(was, MementoFilter.InitialQuotientBits(capacity));
                compared++;
            }

            Assert.IsGreaterThan(100, compared, "the sweep compared almost nothing");
        }

        /// <summary>
        /// A capacity past three quarters of 2^31 asks for a table the filter cannot
        /// have, and starts at the widest instead -- the capacity is where the filter
        /// starts, not a limit on it, and not an error.
        /// </summary>
        [TestMethod]
        [DataRow(1_610_612_737u)]
        [DataRow(3_000_000_000u)]
        [DataRow(uint.MaxValue)]
        public void TestACapacityPastTheWidestTableStartsAtTheWidest(uint capacity)
        {
            Assert.AreEqual(InfiniFilter.MaxQuotientBits, InfiniFilter.InitialQuotientBits(capacity));
            Assert.AreEqual(MementoFilter.MaxQuotientBits, MementoFilter.InitialQuotientBits(capacity));
        }

        /// <summary>
        /// The tables that follow the first in a chain are narrower than the one before,
        /// by more than the fingerprint they were made from, at every point measured: the
        /// chain never has a table at the limit under an active table that is not. That
        /// is why one check at the top of an expansion keeps every table in the chain
        /// inside it. The chain here is the fastest growing there is, the narrowest
        /// fingerprint and the smallest start, at 150,000 items.
        /// </summary>
        [TestMethod]
        public void TestEveryTableInAChainIsNarrowerThanTheOneBeforeIt()
        {
            var infini = new InfiniFilter(1, fingerprintBits: 2);
            var memento = new MementoFilter(2, fingerprintBits: 2, initialCapacity: 1);
            var states = 0;

            for (var i = 1; i <= 150_000; i++)
            {
                infini.Add(Key(i));
                memento.Add((ulong)i * 977);

                if ((i & (i - 1)) != 0)
                {
                    continue;
                }

                for (var t = 1; t < infini.Segments.Count; t++)
                {
                    Assert.IsLessThan(infini.Segments[t - 1].QuotientBits, infini.Segments[t].QuotientBits);
                    states++;
                }

                for (var t = 1; t < memento.Segments.Count; t++)
                {
                    Assert.IsLessThan(memento.Segments[t - 1].QuotientBits, memento.Segments[t].QuotientBits);
                    states++;
                }
            }

            Assert.IsGreaterThan(5, infini.Segments.Count, "the chain never grew, so this measured nothing");
            Assert.IsGreaterThan(20, states);
        }
    }
}
