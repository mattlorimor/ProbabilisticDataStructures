using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
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
    /// A range filter's table is a uint's worth of slots, so 2^31 is the largest it can be,
    /// and 31 bits of address the most it can have. An InfiniFilter's was too, until its
    /// slots, entries and indices were made 64-bit (#231). Its table is now as wide as one
    /// array holds its words, which depends on the fingerprint, and the three things that
    /// stopped it at 2^31 are each met: the slot arithmetic is 64-bit, and is asked here of
    /// slots past 2^32 without a table being built; a payload past 2 GiB is written in the
    /// large form of the envelope, at format version 13, whose counts are <c>u64</c>s; and a
    /// doubling walks the old table in place rather than listing its entries first (#238),
    /// which into 2^34 slots would be 103 GB.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestExpandingTableLimits
    {
        private const int PayloadStart = 14;

        private static byte[] Key(int i) => Encoding.UTF8.GetBytes("k" + i);

        /// <summary>
        /// A payload of one table at the given width of address, hand-written. At version 1
        /// it has the words that the old reader's arithmetic expected -- its slot count was
        /// <c>1u &lt;&lt; quotientBits</c>, which wraps -- so that the width is the only
        /// thing wrong with it. At version 13, an InfiniFilter's, it has the words of 2^q
        /// slots, and its counts of entries and words are <c>u64</c>s, as FORMAT.md lays it
        /// out.
        /// </summary>
        internal static byte[] OnePayload(
            StructureId id, int quotientBits, int fingerprintBits, bool includeWords,
            int mementoBits = 3, ushort version = 1, ulong entries = 0)
        {
            var slots = version == 1 ? 1u << quotientBits : 1UL << quotientBits;
            var bitsPerSlot = id == StructureId.MementoFilter
                ? fingerprintBits + 1 + mementoBits + 3
                : fingerprintBits + 1 + 3;
            var words = ((slots * (ulong)bitsPerSlot) >> 6) + 2;

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
            if (version == 1)
            {
                payload.WriteUInt32((uint)entries);
                payload.WriteUInt32((uint)words);
            }
            else
            {
                payload.WriteUInt64(entries);
                payload.WriteUInt64(words);
            }

            if (includeWords)
            {
                for (var w = 0UL; w < words; w++)
                {
                    payload.WriteUInt64(0);
                }
            }

            var stream = new MemoryStream();
            PersistenceFormat.Write(
                stream, id,
                id == StructureId.MementoFilter ? HashId.None : HashId.XxHash3_64,
                payload.WrittenSpan, version);
            return stream.ToArray();
        }

        /// <summary>
        /// The widest table of <paramref name="fingerprintBits"/>-bit fingerprints whose words
        /// fit one array, worked out in arbitrary precision from FORMAT.md's layout: its slots
        /// end to end, the fingerprint, its age counter's bit and three of metadata each, and
        /// two words more, at most <see cref="Array.MaxLength"/>.
        /// </summary>
        internal static int WidestInfiniTable(int fingerprintBits)
        {
            var quotientBits = 2;
            while (((BigInteger.One << (quotientBits + 1)) * (fingerprintBits + 4) / 64) + 2 <= Array.MaxLength)
            {
                quotientBits++;
            }
            return quotientBits;
        }

        /// <summary>
        /// From version 13 an InfiniFilter's table is as wide as one array holds its words
        /// (#231), where it was 2^31 slots for every fingerprint: a slot count of 2^q needed
        /// q of at most 31 to be a <c>u32</c>. The oracle is
        /// <see cref="WidestInfiniTable"/>, at every fingerprint the constructor takes. A
        /// payload claiming that table without its words is read past every check of its shape
        /// and refused for the words it lacks, before they are priced, under the default budget
        /// as under any (it was refused for their cost until the price checked them), and
        /// one claiming the table a bit wider is refused for its shape, since no array holds it.
        /// The sweep has to see
        /// more than one widest table, and one past 2^31 slots, or it could not tell the limit
        /// from a constant, nor from the old one.
        /// </summary>
        [TestMethod]
        public void TestAnInfiniFilterTableIsAsWideAsOneArrayHoldsItsWords()
        {
            var widest = new HashSet<int>();
            for (var f = 2; f <= 32; f++)
            {
                var q = WidestInfiniTable(f);
                widest.Add(q);

                var atTheLimit = Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<InfiniFilter>(
                    OnePayload(StructureId.InfiniFilter, q, f, includeWords: false, version: 13)));
                Assert.IsNotInstanceOfType<ReadBudgetExceededException>(atTheLimit.InnerException,
                    $"a table of 2^{q} slots of {f}-bit fingerprints without its words was refused for its cost: {atTheLimit.Message}");
                StringAssert.Contains(atTheLimit.Message, "words, beyond what it can hold",
                    $"a table of 2^{q} slots of {f}-bit fingerprints should be refused for the words it lacks");

                // Full, a count of 2^q entries, which past 2^32 only a u64 holds, is a count the
                // table can have; one more is not.
                var full = Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<InfiniFilter>(
                    OnePayload(StructureId.InfiniFilter, q, f, includeWords: false, version: 13, entries: 1UL << q)));
                Assert.IsNotInstanceOfType<ReadBudgetExceededException>(full.InnerException,
                    $"a full table of 2^{q} slots of {f}-bit fingerprints without its words was refused for its cost: {full.Message}");
                StringAssert.Contains(full.Message, "words, beyond what it can hold",
                    $"a full table of 2^{q} slots of {f}-bit fingerprints should be refused for the words it lacks");
                var over = Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<InfiniFilter>(
                    OnePayload(StructureId.InfiniFilter, q, f, includeWords: false, version: 13, entries: (1UL << q) + 1)));
                StringAssert.Contains(over.Message, "entries in", $"2^{q} + 1 entries in 2^{q} slots of {f}-bit fingerprints");

                var wider = Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<InfiniFilter>(
                    OnePayload(StructureId.InfiniFilter, q + 1, f, includeWords: false, version: 13)));
                StringAssert.Contains(wider.Message, "one array",
                    $"a table of 2^{q + 1} slots of {f}-bit fingerprints should be refused for its shape");
            }

            Assert.IsGreaterThan(1, widest.Count, "every fingerprint had the same widest table, so the limit could be a constant");
            Assert.IsGreaterThan(31, widest.Max(), "no table was wider than 2^31 slots, the old limit");
        }

        /// <summary>
        /// A table past 2^31 slots is gigabytes, so the slot arithmetic every read, write and
        /// walk of a table runs is asked of the tables the filter stops at without building
        /// them (#231): where a slot's bits begin, the slots after and before it, the quotient
        /// and fingerprint a hash yields, where a doubling moves an entry, and where a void
        /// entry lands in a table down the chain of any width from 2^31 slots to this one's,
        /// which a payload's chain can hold. The oracle is arbitrary precision, at the
        /// first and last slots and either side of every power of two from 2^31 up, where an
        /// index narrowed to 32 bits, or a product of it, loses its top bits. The last slot of
        /// each widest table has to end within its words, the next word included, which a
        /// read of a slot that straddles two words takes. The sweep has to reach slots past
        /// 2^32, or it could not see a slot narrowed to a <c>uint</c>.
        /// </summary>
        [TestMethod]
        public void TestASlotPastTwoToTheThirtyOneIsPlacedAndSplitInSixtyFourBits()
        {
            var pastUInt = 0;
            for (var f = 2; f <= 32; f++)
            {
                var q = WidestInfiniTable(f);
                var bits = f + 4;
                var mask = (1UL << q) - 1;
                var size = BigInteger.One << q;
                var slots = new List<ulong> { 0, 1, mask - 1, mask };
                for (var p = 31; p < q; p++)
                {
                    slots.AddRange(new[] { (1UL << p) - 1, 1UL << p, (1UL << p) + 1 });
                }

                // Above the slot, bits that differ from one another and from fingerprint to
                // fingerprint, so a fingerprint taken from the wrong place is a different one.
                var above = 0x9E3779B97F4A7C15UL ^ (ulong)f;
                foreach (var slot in slots)
                {
                    var what = $"slot {slot:N0} of 2^{q}, {f}-bit fingerprints";
                    var bit = (BigInteger)slot * bits;
                    var (word, offset) = InfiniSegment.Locate(slot, bits);
                    Assert.AreEqual(bit / 64, (BigInteger)word, $"{what}: its word");
                    Assert.AreEqual(bit % 64, (BigInteger)offset, $"{what}: its bit in the word");
                    Assert.AreEqual((slot + BigInteger.One) % size, (BigInteger)InfiniSegment.Next(slot, mask), $"{what}: the next");
                    Assert.AreEqual((slot + size - 1) % size, (BigInteger)InfiniSegment.Prev(slot, mask), $"{what}: the one before");

                    var hash = slot | (above << q);
                    var (quotient, fingerprint) = InfiniSegment.Split(hash, q, f);
                    Assert.AreEqual(slot, quotient, $"{what}: the quotient of a hash");
                    Assert.AreEqual(((BigInteger)hash >> q) % (BigInteger.One << f), (BigInteger)fingerprint,
                        $"{what}: the fingerprint of a hash");

                    Assert.AreEqual(slot + size, (BigInteger)InfiniSegment.Doubled(slot, 0b101, q),
                        $"{what}: moved by a doubling, its fingerprint's low bit set");
                    Assert.AreEqual(slot, InfiniSegment.Doubled(slot, 0b110, q),
                        $"{what}: moved by a doubling, its fingerprint's low bit clear");
                    for (var t = 31; t <= q; t++)
                    {
                        var (into, carried) = InfiniSegment.Shed(slot, t);
                        Assert.AreEqual(slot % (BigInteger.One << t), (BigInteger)into, $"{what}: shed into 2^{t} slots");
                        Assert.AreEqual((BigInteger)slot >> t, (BigInteger)carried, $"{what}: carried into 2^{t} slots");
                    }

                    if (slot > uint.MaxValue)
                    {
                        pastUInt++;
                    }
                }

                var last = InfiniSegment.Locate(mask, bits);
                Assert.IsLessThan(InfiniSegment.Words(q, f), last.Word + 1L,
                    $"2^{q} slots of {f}-bit fingerprints: the last slot's next word is past the table's words");
            }

            Assert.IsGreaterThan(0, pastUInt, "no slot past what a uint holds was asked");
        }

        /// <summary>
        /// The sizes an InfiniFilter is given and gives are 64-bit (#231): its initial
        /// capacity, its count and its capacity. So are a table's slots and entries, every
        /// slot, quotient and index a table takes and every quotient and address the filter
        /// takes, and the run starts and neighbours a table returns. The filter's own index is
        /// a table's place in its chain, which is not a slot. The members are found by
        /// reflection, so one added later with a 32-bit index fails here; there have to be
        /// some, or nothing was checked.
        /// </summary>
        [TestMethod]
        public void TestAnInfiniFiltersSizesAndIndicesAreSixtyFourBit()
        {
            var capacities = typeof(InfiniFilter).GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Where(p => p.Name == "initialCapacity")
                .ToArray();
            Assert.IsNotEmpty(capacities, "no public constructor takes an initial capacity");
            foreach (var parameter in capacities)
            {
                Assert.AreEqual(typeof(ulong), parameter.ParameterType, $"{parameter.Member}: initialCapacity");
            }

            Assert.AreEqual(typeof(ulong), typeof(InfiniFilter).GetMethod(nameof(InfiniFilter.Count))!.ReturnType, "Count()");
            Assert.AreEqual(typeof(ulong), typeof(InfiniFilter).GetMethod(nameof(InfiniFilter.Capacity))!.ReturnType, "Capacity()");

            const BindingFlags declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var name in new[] { nameof(InfiniSegment.Slots), nameof(InfiniSegment.Count) })
            {
                Assert.AreEqual(typeof(ulong), typeof(InfiniSegment).GetProperty(name, declared)!.PropertyType, $"InfiniSegment.{name}");
            }

            var indices = typeof(InfiniSegment).GetMethods(declared)
                .SelectMany(m => m.GetParameters())
                .Where(p => p.Name is "slot" or "quotient" or "index")
                .Concat(typeof(InfiniFilter).GetMethods(declared)
                    .SelectMany(m => m.GetParameters())
                    .Where(p => p.Name is "quotient" or "address"))
                .ToArray();
            Assert.IsGreaterThan(5, indices.Length, "almost no index was found, so the sweep checked nothing");
            foreach (var parameter in indices)
            {
                Assert.AreEqual(typeof(ulong), parameter.ParameterType,
                    $"{parameter.Member.DeclaringType!.Name}.{parameter.Member.Name}({parameter.Name})");
            }

            foreach (var name in new[] { "FindRunStart", "Next", "Prev" })
            {
                var method = typeof(InfiniSegment).GetMethods(declared).Where(m => m.Name == name).ToArray();
                Assert.IsNotEmpty(method, $"InfiniSegment has no {name}");
                Assert.IsTrue(method.All(m => m.ReturnType == typeof(ulong)), $"InfiniSegment.{name} returns a {method[0].ReturnType.Name}");
            }

            foreach (var (name, type) in new[]
            {
                (nameof(InfiniSegment.Split), typeof((ulong, ulong))),
                (nameof(InfiniSegment.Entries), typeof(IEnumerable<(ulong, ulong)>)),
            })
            {
                var method = typeof(InfiniSegment).GetMethods(declared).Where(m => m.Name == name).ToArray();
                Assert.IsNotEmpty(method, $"InfiniSegment has no {name}");
                Assert.IsTrue(method.All(m => m.ReturnType == type), $"InfiniSegment.{name} returns a {method[0].ReturnType}");
            }
        }

        /// <summary>
        /// A range filter's table cannot have more than 31 bits of address, and nor could an
        /// InfiniFilter's at version 1, when its slot count was a <c>u32</c>: a payload
        /// claiming more is refused with that said. The first three are the widths whose
        /// shifted slot count is a small number the old reader sized words for and accepted;
        /// the last is refused by the old reader too, for a limit of 40 that nothing builds.
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
        /// The widest a table can be is read past every check of its size: a payload claiming
        /// a table of 2^31 slots without its words is refused under the default budget for the
        /// words it lacks, not for its size, and before anything is priced for them. It was
        /// refused for the gigabytes it would cost until the price checked the words were there
        /// before pricing them. The same payload one bit wider is refused for its size.
        /// </summary>
        [TestMethod]
        public void TestATableAtTheLimitIsRefusedForTheWordsItLacksNotForItsSize()
        {
            // An InfiniFilter's table at version 1, whose slot count was a u32; at version 13
            // its tables are read to their own limits, in
            // TestAnInfiniFilterTableIsAsWideAsOneArrayHoldsItsWords.
            var limit = InfiniFilter.NarrowQuotientBits;

            var infini = OnePayload(StructureId.InfiniFilter, limit, 4, includeWords: false);
            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<InfiniFilter>(infini));
            Assert.IsNotInstanceOfType<ReadBudgetExceededException>(ex.InnerException,
                $"a table of 2^{limit} slots without its words was refused for its cost: {ex.Message}");
            StringAssert.Contains(ex.Message, "words, beyond what it can hold",
                $"a table of 2^{limit} slots should be refused for the words it lacks");

            var memento = OnePayload(StructureId.MementoFilter, MementoFilter.MaxQuotientBits, 4, includeWords: false);
            ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<MementoFilter>(memento));
            Assert.IsNotInstanceOfType<ReadBudgetExceededException>(ex.InnerException,
                $"a table of 2^{MementoFilter.MaxQuotientBits} slots without its words was refused for its cost: {ex.Message}");
            StringAssert.Contains(ex.Message, "words, beyond what it can hold",
                $"a table of 2^{MementoFilter.MaxQuotientBits} slots should be refused for the words it lacks");
        }

        /// <summary>
        /// A large-form header, and the fields of a <see cref="MementoFilter"/> payload of one
        /// table, whose length claims the table's words after the fields. The words are not given:
        /// a stream of <c>Length</c> bytes that holds <c>Head</c> reads as the whole payload, its
        /// words zeros that are never held.
        /// </summary>
        private static (byte[] Head, long Length, int Fields, long Words) MementoClaimedInTheLargeForm(
            int quotientBits, int fingerprintBits, int mementoBits)
        {
            var ordinary = OnePayload(StructureId.MementoFilter, quotientBits, fingerprintBits, includeWords: false, mementoBits: mementoBits);
            var fields = ordinary[PayloadStart..^4];
            long words = BinaryPrimitives.ReadUInt32LittleEndian(fields.AsSpan(fields.Length - sizeof(uint)));
            var claimed = fields.Length + words * sizeof(ulong);
            var envelope = LargeForm.Enveloped(ordinary, fields);
            BinaryPrimitives.WriteUInt64LittleEndian(envelope.AsSpan(10), (ulong)claimed);
            return (LargeForm.Reseal(envelope)[..^4], PersistenceFormat.LargeEnvelopeLength + claimed, fields.Length, words);
        }

        /// <summary>
        /// A <see cref="MementoFilter"/>'s table is one array of words, so a table whose words no
        /// array holds is refused for that by name, by its read and its price alike, before
        /// anything is priced or allocated for it. One shape has such a table: 2^31 slots of 64
        /// bits, 2^31 + 2 words. No payload in the ordinary envelope holds that many, and the check
        /// that a payload holds a table's words refused one for them; but a payload in the large
        /// form of 17 GB holds them, nothing refused it, and its reader went on to allocate them.
        /// </summary>
        /// <remarks>
        /// Each claim is a large-form header naming the whole payload, on a stream that claims
        /// every byte of it and holds only the fields, so no word is built (#226's
        /// <see cref="TestLargeEnvelopeReading.Sparse"/>). It is read under no budget and under the
        /// default one, and its price walked alone. And the other direction, where the limit
        /// falls: the widest table an array does hold, 2^31 slots of 63 bits, is taken by its price
        /// whole and priced for its words, and is refused under the default budget for its cost,
        /// not for its shape. It is not read under no budget, which would allocate its 17 GB. The
        /// claim too wide is refused by name in the ordinary envelope as well, before its missing
        /// words are counted.
        /// </remarks>
        [TestMethod]
        public void TestAMementoTableNoArrayHoldsIsRefusedByNameBeforeAnythingIsPricedOrAllocated()
        {
            // A fingerprint of 28 bits and a memento of 32 make slots of 64 bits; one of 27, 63.
            const int Widest = 31;
            Assert.AreEqual(Widest, MementoFilter.MaxQuotientBits, "premise: a table has at most 2^31 slots");
            Assert.AreEqual(64, MementoSegment.SlotBits(28, 32), "premise: the claim too wide has slots of 64 bits");
            Assert.AreEqual(63, MementoSegment.SlotBits(27, 32), "premise: the widest claim that fits has slots of 63 bits");

            var tooWide = MementoClaimedInTheLargeForm(Widest, 28, 32);
            var fits = MementoClaimedInTheLargeForm(Widest, 27, 32);
            Assert.IsGreaterThan((long)Array.MaxLength, tooWide.Words, "premise: 2^31 slots of 64 bits are more words than an array holds");
            Assert.IsLessThanOrEqualTo((long)Array.MaxLength, fits.Words, "premise: 2^31 slots of 63 bits are words an array holds");

            (Exception Thrown, long Allocated, long Furthest) Read(
                (byte[] Head, long Length, int Fields, long Words) claim, ReadOptions options)
            {
                TestLargeEnvelopeReading.Sparse stream = null;
                Exception thrown = null;
                var allocated = NoGCRegion.Allocated(() =>
                {
                    stream = new TestLargeEnvelopeReading.Sparse(claim.Head, claim.Length);
                    try
                    {
                        MementoFilter.ReadFrom(stream, options);
                        thrown = null;
                    }
                    catch (Exception e)
                    {
                        thrown = e;
                    }

                    return null;
                });
                return (thrown, allocated, stream.Furthest);
            }

            (InvalidDataException Stopped, long Priced) Walk((byte[] Head, long Length, int Fields, long Words) claim)
            {
                var payload = claim.Length - PersistenceFormat.LargeEnvelopeLength;
                var budget = ReadBudget.For(ReadOptions.Unlimited, StructureId.MementoFilter);
                var walk = new PayloadReader(
                    new PayloadSource(new TestLargeEnvelopeReading.Sparse(claim.Head[PersistenceFormat.LargeHeaderLength..], payload)),
                    payload, budget, wide: true, streamed: true);
                try
                {
                    MementoFilter.Price(ref walk, PersistenceFormat.DefaultVersion);
                    return (null, budget.Charged);
                }
                catch (InvalidDataException e)
                {
                    return (e, budget.Charged);
                }
            }

            var problems = new List<string>();

            // The claim too wide, priced alone and then read: refused by name, nothing priced
            // or allocated for the words, and none of them read.
            var price = Walk(tooWide);
            if (price.Stopped is null || !price.Stopped.Message.Contains("one array", StringComparison.Ordinal))
            {
                problems.Add($"2^31 slots of 64 bits: the price was not refused for its shape, and stopped: {price.Stopped?.Message ?? "at its end"}");
            }
            // The filter and its list are made before any table is read, and priced there
            // (#346); nothing past them is.
            if (price.Priced != MementoFilter.FilterCost(1))
            {
                problems.Add($"2^31 slots of 64 bits: the price priced {price.Priced - MementoFilter.FilterCost(1):N0} bytes " +
                    "past the filter and its list for a table no array holds");
            }

            foreach (var (name, options) in new[] { ("no budget", ReadOptions.Unlimited), ("the default budget", ReadOptions.Default) })
            {
                var (thrown, allocated, furthest) = Read(tooWide, options);
                if (thrown is not InvalidDataException refused)
                {
                    problems.Add($"2^31 slots of 64 bits, under {name}: {(thrown is null ? "read" : $"threw {thrown.GetType().Name}: {thrown.Message}")}");
                    continue;
                }

                if (refused.InnerException is ReadBudgetExceededException)
                {
                    problems.Add($"2^31 slots of 64 bits, under {name}: refused for its cost: {refused.Message}");
                }
                else if (!refused.Message.Contains("one array", StringComparison.Ordinal))
                {
                    problems.Add($"2^31 slots of 64 bits, under {name}: refused for another reason than its shape: {refused.Message}");
                }
                else if (price.Stopped is not null && !refused.Message.StartsWith(price.Stopped.Message, StringComparison.Ordinal))
                {
                    problems.Add($"2^31 slots of 64 bits, under {name}: the read refuses it otherwise than its price: {refused.Message}");
                }

                if (allocated > 64L << 10)
                {
                    problems.Add($"2^31 slots of 64 bits, under {name}: the read allocated {allocated:N0} bytes before it was refused");
                }
                if (furthest > PersistenceFormat.LargeHeaderLength + tooWide.Fields)
                {
                    problems.Add($"2^31 slots of 64 bits, under {name}: the read went to byte {furthest:N0}, past the fields");
                }
            }

            // The same claim in the ordinary envelope, which cannot hold its words.
            var ordinary = OnePayload(StructureId.MementoFilter, Widest, 28, includeWords: false, mementoBits: 32);
            var held = Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<MementoFilter>(ordinary));
            if (!held.Message.Contains("one array", StringComparison.Ordinal))
            {
                problems.Add($"2^31 slots of 64 bits, in the ordinary envelope: refused for another reason than its shape: {held.Message}");
            }

            // The widest that fits: priced whole, and refused for that under the default budget.
            var fitting = Walk(fits);
            if (fitting.Stopped is not null)
            {
                problems.Add($"2^31 slots of 63 bits: the price did not walk the table an array holds: {fitting.Stopped.Message}");
            }
            else if (fitting.Priced < fits.Words * sizeof(ulong) || fitting.Priced >= 2 * fits.Words * sizeof(ulong))
            {
                // Once: the words are read straight into the table (#327), where they were
                // read into an array of their own and copied, and priced twice over.
                problems.Add($"2^31 slots of 63 bits: the price came to {fitting.Priced:N0} bytes, not the table's words once");
            }

            var (fitsThrown, fitsAllocated, _) = Read(fits, ReadOptions.Default);
            if (fitsThrown is not InvalidDataException { InnerException: ReadBudgetExceededException })
            {
                problems.Add($"2^31 slots of 63 bits, under the default budget: not refused for its cost, but " +
                    $"{(fitsThrown is null ? "read" : $"{fitsThrown.GetType().Name}: {fitsThrown.Message}")}");
            }
            else if (fitsAllocated > 64L << 10)
            {
                problems.Add($"2^31 slots of 63 bits, under the default budget: the read allocated {fitsAllocated:N0} bytes before it was refused");
            }

            Assert.IsEmpty(problems, $"at {problems.Count} checks: " + string.Join(" | ", problems));
        }

        /// <summary>
        /// An expansion that would take the table past the limit is refused before the
        /// filter changes: no entry moved, no table replaced, nothing counted. The limit
        /// is lowered to the width of a table a test can fill; the filter's own is as wide as
        /// one array holds the words of its fingerprint's table.
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
        /// An InfiniFilter's table stops doubling where its words would no longer fit one array
        /// (#231), and the add that would double it past there is refused before the filter
        /// changes, saying so. The array is lowered on this thread to the words of 2^10 slots
        /// of 8-bit fingerprints, through the hook every table's limit is worked out from, so
        /// the table is one a test fills; the same array holds 2^11 slots of 2-bit ones, so the
        /// narrower fingerprint stops a doubling later, each at its own. The filter starts a
        /// doubling below its limit, so every key is in the one table when it stops.
        /// </summary>
        [TestMethod]
        [DataRow(8, 10)]
        [DataRow(2, 11)]
        public void TestAnInfiniFilterStopsDoublingWhereItsTableWouldNotFitAnArray(int fingerprintBits, int widest)
        {
            InfiniSegment.LoweredLongestTable = InfiniSegment.Words(10, 8);
            try
            {
                Assert.AreEqual(widest, InfiniFilter.QuotientBitsLimitFor(fingerprintBits),
                    "premise: the widest table the lowered array holds");
                var filter = new InfiniFilter(initialCapacity: 3UL << (widest - 3), fingerprintBits: fingerprintBits);
                Assert.AreEqual(widest - 1, filter.Segments[0].QuotientBits, "premise: a doubling below the limit");

                var added = 0;
                InvalidOperationException refused = null;
                while (refused is null && added < 1 << (widest + 1))
                {
                    try
                    {
                        filter.Add(Key(added));
                        added++;
                    }
                    catch (InvalidOperationException ex)
                    {
                        refused = ex;
                    }
                }

                Assert.IsNotNull(refused, "the filter never refused");
                Assert.AreEqual(3 << (widest - 2), added, "the table did not stop at three quarters of the widest");
                Assert.AreEqual(1u, filter.ExpansionCount());
                Assert.HasCount(1, filter.Segments, "premise: every key is in the one table");
                Assert.AreEqual(widest, filter.Segments[0].QuotientBits);
                StringAssert.Contains(refused.Message, "one array");
                StringAssert.Contains(refused.Message, (1UL << widest).ToString("N0"));

                var before = filter.ToByteArray();
                Assert.ThrowsExactly<InvalidOperationException>(() => filter.Add(Key(added)));
                CollectionAssert.AreEqual(before, filter.ToByteArray(), "a refused add changed the filter");
                for (var i = 0; i < added; i++)
                {
                    Assert.IsTrue(filter.Test(Key(i)), $"key {i} was lost");
                }

                // The table at the limit has exactly the words the array holds, and is read
                // back: the reader refuses only a table with more.
                Assert.AreEqual(InfiniSegment.LongestTable, filter.Segments[0].TableWords,
                    "premise: the table fills the lowered array to its last word");
                var restored = Persistence.FromByteArray<InfiniFilter>(before);
                CollectionAssert.AreEqual(before, restored.ToByteArray(), "the table at its limit did not read back as itself");
            }
            finally
            {
                InfiniSegment.LoweredLongestTable = null;
            }
        }

        /// <summary>
        /// The constructor takes its capacity as the 64-bit number it is (#231): one past what a
        /// <c>uint</c> holds starts the table as wide as the fingerprint allows, where narrowed to
        /// 32 bits it would be one item and start the narrowest. The array is lowered on this
        /// thread, so that the widest is a table the test can build.
        /// </summary>
        [TestMethod]
        public void TestAnInfiniFilterStartsAsWideAsACapacityPastAUIntNeeds()
        {
            InfiniSegment.LoweredLongestTable = InfiniSegment.Words(10, 8);
            try
            {
                var filter = new InfiniFilter((1UL << 32) + 1, fingerprintBits: 8);
                Assert.AreEqual(10, filter.Segments[0].QuotientBits);
                Assert.AreEqual(1UL << 10, filter.Capacity());
            }
            finally
            {
                InfiniSegment.LoweredLongestTable = null;
            }
        }

        /// <summary>
        /// Every table in the chain is held to its own limit before a doubling, not only the
        /// first. A doubling can double any of them, a table down the chain when the entries
        /// it sheds fill it, and that happens part way through the walk of the first, so a
        /// table that could not double would fail the walk with entries already moved. A
        /// payload's tables are checked one by one, so a filter read back can hold a table of
        /// another fingerprint at its limit behind a first table far from its own. Under the
        /// lowered array, a table of 2^10 slots of 8-bit fingerprints, at its limit, is put
        /// behind a filter of four slots of 2-bit ones: the add that would double the first is
        /// refused, naming the table that stops it, and the filter is unchanged.
        /// </summary>
        [TestMethod]
        public void TestAnInfiniFilterDoublingIsRefusedForATableDownTheChainAtItsLimit()
        {
            InfiniSegment.LoweredLongestTable = InfiniSegment.Words(10, 8);
            try
            {
                var filter = new InfiniFilter(initialCapacity: 3, fingerprintBits: 2);
                filter.Segments.Add(new InfiniSegment(10, 8));
                Assert.AreEqual(2, filter.Segments[0].QuotientBits, "premise: a first table of four slots");
                Assert.AreEqual(10, InfiniFilter.QuotientBitsLimitFor(8), "premise: the second table is at its limit");

                for (var i = 0; i < 3; i++)
                {
                    filter.Add(Key(i));
                }

                var before = filter.ToByteArray();
                var ex = Assert.ThrowsExactly<InvalidOperationException>(() => filter.Add(Key(3)));
                StringAssert.Contains(ex.Message, "1,024 slots");
                StringAssert.Contains(ex.Message, "8-bit fingerprints");
                CollectionAssert.AreEqual(before, filter.ToByteArray(), "a refused add changed the filter");
                Assert.AreEqual(0u, filter.ExpansionCount());
            }
            finally
            {
                InfiniSegment.LoweredLongestTable = null;
            }
        }

        /// <summary>
        /// A read is priced before it allocates, by a walk of the payload its reader's checks
        /// are shared with (TESTING.md, "A refusal's number is held to loading"), so the walk
        /// has to take every table the reader does. A version 13 payload claiming each
        /// fingerprint's widest table, without its words, is walked past every check of the
        /// table's shape, and refused for the words the payload lacks, as its reader refuses
        /// it, with nothing priced for them. A walk that stopped at the table's shape would
        /// refuse a table its reader reads; one that priced the words before checking them was
        /// refused for its budget, where the payload is short.
        /// </summary>
        [TestMethod]
        public void TestAPriceTakesATableAsWideAsItsFingerprintAllowsAndPricesNoWordsItLacks()
        {
            for (var f = 2; f <= 32; f++)
            {
                var q = WidestInfiniTable(f);
                var envelope = OnePayload(StructureId.InfiniFilter, q, f, includeWords: false, version: 13);
                var budget = ReadBudget.For(ReadOptions.Unlimited, StructureId.InfiniFilter);
                var walk = new PayloadReader(envelope.AsSpan(PayloadStart, envelope.Length - PayloadStart - 4), budget);

                InvalidDataException ex = null;
                try
                {
                    InfiniFilter.Price(ref walk, PersistenceFormat.InfiniWideVersion);
                }
                catch (InvalidDataException e)
                {
                    ex = e;
                }

                Assert.IsNotNull(ex, $"2^{q} slots of {f}-bit fingerprints: the walk read words the payload does not have");
                StringAssert.Contains(ex.Message, "words, beyond what it can hold", $"2^{q} slots of {f}-bit fingerprints: {ex.Message}");

                var read = Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<InfiniFilter>(envelope));
                Assert.AreEqual(ex.Message, read.Message, $"2^{q} slots of {f}-bit fingerprints: the read refuses it for another reason");

                // The table is twice its words and more, read into one array and copied into the
                // table; none of it is priced for a payload that does not hold the words. The
                // filter and its list are made before any table is read, and priced there (#346).
                Assert.AreEqual(InfiniFilter.FilterCost(1), budget.Charged,
                    $"2^{q} slots of {f}-bit fingerprints: the walk priced words the payload does not hold, or not the filter");
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
        /// the saturation, straddling each power of two and its three-quarter point. The
        /// range filter's slots are the narrowest, the default and the widest whose table can
        /// have 2^31 slots; a slot of 64 bits stops a doubling short, in
        /// <see cref="TestASixtyFourBitSlotStartsAndStopsItsTableAtTheWidthItsWordsAllow"/>.
        /// </summary>
        [TestMethod]
        public void TestTheFirstTableStartsAtTheWidthsItAlwaysDid()
        {
            var mementoSlots = new[]
            {
                MementoSegment.SlotBits(2, 1), MementoSegment.SlotBits(8, 8), MementoSegment.MaxSlotBits - 1,
            };

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

                for (var f = 2; f <= 32; f++)
                {
                    Assert.AreEqual(was, InfiniFilter.InitialQuotientBits(capacity, f),
                        $"an initial capacity of {capacity} no longer starts a table of {f}-bit fingerprints where it did");
                }

                foreach (var slotBits in mementoSlots)
                {
                    Assert.AreEqual(was, MementoFilter.InitialQuotientBits(capacity, slotBits),
                        $"an initial capacity of {capacity} no longer starts a table of {slotBits}-bit slots where it did");
                }
                compared++;
            }

            Assert.IsGreaterThan(100, compared, "the sweep compared almost nothing");
        }

        /// <summary>
        /// A capacity past three quarters of 2^31 asks the range filter for a table it cannot
        /// have, and starts at the widest instead -- the capacity is where the filter
        /// starts, not a limit on it, and not an error.
        /// </summary>
        [TestMethod]
        [DataRow(1_610_612_737u)]
        [DataRow(3_000_000_000u)]
        [DataRow(uint.MaxValue)]
        public void TestACapacityPastTheWidestTableStartsAtTheWidest(uint capacity)
        {
            Assert.AreEqual(MementoFilter.MaxQuotientBits,
                MementoFilter.InitialQuotientBits(capacity, MementoSegment.SlotBits(8, 8)));
        }

        /// <summary>
        /// An InfiniFilter's capacity is a <c>ulong</c> (#231), and starts its table as wide as
        /// that capacity needs and its fingerprint allows. At every fingerprint the
        /// constructor takes, the capacity that fills the widest table
        /// (<see cref="WidestInfiniTable"/>) to three quarters starts there, as does one past
        /// it, and the largest capacity there is; the one that fills the table half the size
        /// starts at that, and one item more needs the widest. So each width is straddled, and
        /// so is the clamp. The widths reached have to pass 2^32 slots, whose three quarters is
        /// past what a <c>uint</c> capacity can ask for, or a capacity narrowed to 32 bits
        /// would pass.
        /// </summary>
        [TestMethod]
        public void TestAnInfiniFilterCapacityStartsItsTableAsWideAsItsFingerprintAllows()
        {
            var reached = new HashSet<int>();
            for (var f = 2; f <= 32; f++)
            {
                var widest = WidestInfiniTable(f);
                var fills = 3UL << (widest - 2);
                var half = 3UL << (widest - 3);

                Assert.AreEqual(widest, InfiniFilter.InitialQuotientBits(fills, f), $"{fills:N0} items, {f}-bit fingerprints");
                Assert.AreEqual(widest, InfiniFilter.InitialQuotientBits(fills + 1, f), $"{fills + 1:N0} items, {f}-bit fingerprints");
                Assert.AreEqual(widest, InfiniFilter.InitialQuotientBits(ulong.MaxValue, f), $"the largest capacity, {f}-bit fingerprints");
                Assert.AreEqual(widest - 1, InfiniFilter.InitialQuotientBits(half, f), $"{half:N0} items, {f}-bit fingerprints");
                Assert.AreEqual(widest, InfiniFilter.InitialQuotientBits(half + 1, f), $"{half + 1:N0} items, {f}-bit fingerprints");
                reached.Add(widest);
            }

            Assert.IsGreaterThan(32, reached.Max(), "no table was started past 2^32 slots, which a uint capacity cannot ask for");
            Assert.IsGreaterThan(1, reached.Count, "every fingerprint started at one width, so the limit could be a constant");
        }

        /// <summary>
        /// The limit an InfiniFilter holds a table to is the widest whose words fit one array,
        /// at every fingerprint the constructor takes, worked out by the test's own oracle in
        /// arbitrary precision; and the most any table has is the narrowest fingerprint's.
        /// </summary>
        [TestMethod]
        public void TestAnInfiniFilterTableStopsWhereItsWordsStopFittingAnArray()
        {
            for (var f = 2; f <= 32; f++)
            {
                Assert.AreEqual(WidestInfiniTable(f), InfiniFilter.QuotientBitsLimitFor(f), $"{f}-bit fingerprints");
            }

            Assert.AreEqual(WidestInfiniTable(2), InfiniFilter.MaxQuotientBits);
            Assert.AreEqual(Enumerable.Range(2, 31).Max(WidestInfiniTable), InfiniFilter.MaxQuotientBits,
                "a wider fingerprint has a wider table than the narrowest");
        }

        /// <summary>
        /// A range filter's table is one array of words: its slots end to end and two words
        /// more, worked out in 64 bits, at every slot width from one bit to 64 and every table
        /// a slot count allows. The oracle is the same sum in arbitrary precision, so a product
        /// taken in 32 bits, which 2^31 slots of more than one bit overflow, fails here. Every
        /// segment is then built with that many words, at every width a segment can have, so
        /// the count the widest table is worked out from below is the count the tables are
        /// allocated by.
        /// </summary>
        [TestMethod]
        public void TestATablesWordsAreItsSlotsEndToEndAndTwoMore()
        {
            for (var slotBits = 1; slotBits <= MementoSegment.MaxSlotBits; slotBits++)
            {
                for (var q = 0; q <= MementoFilter.MaxQuotientBits; q++)
                {
                    var oracle = ((BigInteger.One << q) * slotBits / 64) + 2;
                    Assert.AreEqual(oracle, (BigInteger)MementoSegment.Words(1u << q, slotBits),
                        $"2^{q} slots of {slotBits} bits");
                }
            }

            var built = new HashSet<int>();
            for (var fingerprintBits = 2; fingerprintBits <= 32; fingerprintBits++)
            {
                for (var mementoBits = 1; mementoBits <= 32; mementoBits++)
                {
                    var slotBits = MementoSegment.SlotBits(fingerprintBits, mementoBits);
                    if (slotBits > MementoSegment.MaxSlotBits)
                    {
                        continue;
                    }

                    foreach (var q in new[] { 2, 7 })
                    {
                        var segment = new MementoSegment(q, fingerprintBits, mementoBits);
                        Assert.AreEqual(MementoSegment.Words(segment.Slots, slotBits), (long)segment.Data.Length,
                            $"a table of 2^{q} slots of {slotBits} bits ({fingerprintBits}-bit fingerprint, " +
                            $"{mementoBits}-bit memento) is not allocated the words it is counted as");
                    }
                    built.Add(slotBits);
                }
            }

            var narrowest = MementoSegment.SlotBits(2, 1);
            CollectionAssert.AreEquivalent(
                Enumerable.Range(narrowest, MementoSegment.MaxSlotBits - narrowest + 1).ToArray(),
                built.ToArray(), "a slot width a segment can have was not built");
        }

        /// <summary>
        /// The widest table a slot width allows is the widest whose words fit in one array,
        /// and no wider than a slot count holds, at every width from one bit to 64. The oracle
        /// scans down from 2^31 slots for the first whose words, in arbitrary precision, are
        /// at most <see cref="Array.MaxLength"/>. A slot of 64 bits is the width that stops a
        /// doubling short: 2^31 of them are 2^31 + 2 words, more than an array holds, where
        /// 2^31 slots of 63 bits fit. The sweep has to see both answers, or it could not tell
        /// the limit from a constant.
        /// </summary>
        [TestMethod]
        public void TestTheWidestTableForASlotWidthIsTheWidestWhoseWordsFitInAnArray()
        {
            var answers = new HashSet<int>();
            for (var slotBits = 1; slotBits <= MementoSegment.MaxSlotBits; slotBits++)
            {
                var oracle = MementoFilter.MaxQuotientBits;
                while (((BigInteger.One << oracle) * slotBits / 64) + 2 > Array.MaxLength)
                {
                    oracle--;
                }

                Assert.AreEqual(oracle, MementoFilter.QuotientBitsLimitFor(slotBits),
                    $"the widest table of {slotBits}-bit slots");
                answers.Add(oracle);
            }

            Assert.Contains(MementoFilter.MaxQuotientBits, answers,
                "no width reached the widest table a slot count holds");
            Assert.IsGreaterThan(1, answers.Count,
                "every width allowed the same table, so the sweep cannot tell the limit from a constant");
        }

        /// <summary>
        /// A segment whose words are more than an array holds is refused by name, before
        /// anything is allocated. 2^31 slots of 64 bits threw a bare OverflowException from
        /// the allocation, one doubling before the refusal the filter documents. No filter
        /// builds that segment now; this is what stops anything else doing so.
        /// </summary>
        [TestMethod]
        public void TestASegmentTooLargeForAnArrayIsRefusedByName()
        {
            var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MementoSegment(31, 28, 32));
            Assert.AreEqual("quotientBits", ex.ParamName);
            StringAssert.Contains(ex.Message, "array");
        }

        /// <summary>
        /// The same for an InfiniFilter's table, which is as wide as one array holds its words
        /// (#231): one a bit wider than that is refused by name before anything is allocated,
        /// at every fingerprint, where it would otherwise fail in the allocation. The widest is
        /// gigabytes, so it is not built here; a table of 2^20 slots is, and holds the words it
        /// is counted as.
        /// </summary>
        [TestMethod]
        public void TestAnInfiniTableTooLargeForAnArrayIsRefusedByName()
        {
            for (var f = 2; f <= 32; f++)
            {
                var q = WidestInfiniTable(f);
                var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new InfiniSegment(q + 1, f),
                    $"2^{q + 1} slots of {f}-bit fingerprints");
                Assert.AreEqual("quotientBits", ex.ParamName);
                StringAssert.Contains(ex.Message, "one array");
            }

            var built = new InfiniSegment(20, 8);
            Assert.AreEqual(InfiniSegment.Words(20, 8), built.Data.LongLength);
        }

        /// <summary>
        /// A filter of 64-bit slots starts its first table at no more than 2^30 slots; one of
        /// 63-bit slots can start at 2^31. A table of 2^30 such slots is eight gibibytes, so
        /// this is the sizing the constructor calls rather than a filter built. The
        /// capacities straddle the three-quarter point of 2^30 slots: 805,306,368 fills that
        /// table to its threshold, and one more asks for 2^31.
        /// </summary>
        [TestMethod]
        public void TestASixtyFourBitSlotStartsItsTableNoWiderThanItsWordsAllow()
        {
            const uint fillsTwoToTheThirty = 805_306_368;
            Assert.AreEqual(30, MementoFilter.InitialQuotientBits(fillsTwoToTheThirty, 63));
            Assert.AreEqual(30, MementoFilter.InitialQuotientBits(fillsTwoToTheThirty, 64));
            Assert.AreEqual(31, MementoFilter.InitialQuotientBits(fillsTwoToTheThirty + 1, 63));
            Assert.AreEqual(30, MementoFilter.InitialQuotientBits(fillsTwoToTheThirty + 1, 64),
                "a capacity past three quarters of 2^30 starts a table of 64-bit slots at 2^31");
            Assert.AreEqual(30, MementoFilter.InitialQuotientBits(uint.MaxValue, 64));
        }

        /// <summary>
        /// A filter of 64-bit slots stops doubling at 2^30 slots, built or read; one of 63-bit
        /// slots goes on to 2^31. The two pairs that make a 64-bit slot are a range of 2^32
        /// with a 28-bit fingerprint and a range of 2^28 with a 32-bit one. The stop is the
        /// limit the filter holds, which an expansion refuses to pass
        /// (<see cref="TestAMementoFilterExpansionPastTheLimitIsRefusedBeforeAnythingChanges"/>),
        /// since the table it stops at is eight gibibytes.
        /// </summary>
        [TestMethod]
        public void TestASixtyFourBitSlotStopsDoublingWhereItsWordsAllow()
        {
            foreach (var (range, fingerprintBits, limit) in new[]
            {
                (1UL << 32, 28, 30), (1UL << 28, 32, 30), (1UL << 32, 27, 31), (1UL << 27, 32, 31),
            })
            {
                var filter = new MementoFilter(range, fingerprintBits, initialCapacity: 4);
                var slotBits = MementoSegment.SlotBits(fingerprintBits, filter.MementoBits);
                Assert.AreEqual(limit, filter.QuotientBitsLimit,
                    $"a filter of {slotBits}-bit slots lets its table double to the wrong width");

                var read = Persistence.FromByteArray<MementoFilter>(filter.ToByteArray());
                Assert.AreEqual(limit, read.QuotientBitsLimit,
                    $"a filter of {slotBits}-bit slots, read back, lets its table double to the wrong width");
            }
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
