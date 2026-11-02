using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
                for (var i = 0; i < filter.Capacity() * 4; i++)
                {
                    filter.Add(Key($"x{i}"));
                    accepted.Add($"x{i}");
                }
            });

            StringAssert.Contains(ex.Message, "full");

            // It filled up rather than giving up early.
            Assert.IsGreaterThan(filter.Capacity() * 3 / 4, (uint)accepted.Count,
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

            // One past what a payload may say. 33 is the width the old constructor wrote
            // for the largest item counts, and the reader refused it then as now; 32 is
            // read, and has its own test beside the stored payloads.
            AssertRefused(Poke(clean, 0, 33), "quotient bits");
            AssertRefused(Poke(clean, 4, 0), "not a split");
            AssertRefused(Poke(clean, 4, 60), "not a split");
            AssertRefused(Poke(clean, 8, 999999), "slots");
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
        // so r cannot exceed 61 whatever q is. And a table has 2^q slots counted in a uint,
        // so q cannot exceed 31: 2^32 is one more than a uint holds.

        private const int HashBits = 64;
        private const int MetadataBits = 3;
        private const int LargestSlotBits = 64;
        private const int LargestQuotientBits = 31;

        // Three quarters of the 2^31 slots the largest table has, the load factor the
        // filter is sized for: the most items it will be asked to hold.
        private const uint LargestItemCount = 3u << 29;

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
        /// More items than a table the slot count can index is refused, and the refusal
        /// names the most it will take. The table's size is a power of two held in a
        /// uint, so it stops at 2^31 slots, and an item count past three quarters of
        /// that needs 2^32, which shifted into a table of one slot and then two.
        /// </summary>
        [TestMethod]
        public void TestAnItemCountPastTheLargestTableIsRefused()
        {
            var named = LargestItemCount.ToString(CultureInfo.InvariantCulture);

            foreach (var n in new[] { LargestItemCount + 1, 2_000_000_000u, uint.MaxValue })
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
        /// of 2^31 slots is gigabytes, so the sizing is asked directly. It is the same
        /// sizing the constructor runs, so what it accepts here is what the constructor
        /// would build.
        /// </summary>
        [TestMethod]
        public void TestTheLargestTableAndTheFinestRateAreStillSized()
        {
            Assert.IsLessThanOrEqualTo((ulong)uint.MaxValue, 1UL << LargestQuotientBits,
                "a table of this many slots has to be countable in a uint");
            Assert.IsGreaterThan((ulong)uint.MaxValue, 1UL << (LargestQuotientBits + 1),
                "the next width up is the one a uint cannot count, which is why it is the limit");

            Assert.AreEqual((31u, 1u), QuotientFilter.Split(LargestItemCount, 0.5));
            // And the step below it, so the widest width is what the count needs and not
            // simply the most that is allowed: 3 * 2^28 items fill 2^30 slots exactly.
            Assert.AreEqual((30u, 1u), QuotientFilter.Split(3u << 28, 0.5));
            Assert.AreEqual((31u, 1u), QuotientFilter.Split((3u << 28) + 1, 0.5));

            // Where the two limits meet: the widest table still leaves 33 bits of hash,
            // and a rate that needs them is sized.
            var finest = Math.ScaleB(1.0, -(HashBits - LargestQuotientBits));
            Assert.AreEqual((31u, 33u), QuotientFilter.Split(LargestItemCount, finest));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => QuotientFilter.Split(LargestItemCount, finest / 2));
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
