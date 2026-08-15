using System;
using System.Collections.Generic;
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
