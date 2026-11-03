using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// What <c>Count()</c> says on the two Bloom filters that remove, once a removal has
    /// returned true more often than items were added.
    /// <para>
    /// <c>TestAndRemove</c> returns true whenever every one of the item's positions is set,
    /// which is not the same as "this item was added and is now gone": a counter that
    /// saturated is never decremented, so a counting filter still answers true to an item
    /// removed as often as it was added; a deletable filter's bits in a collided region
    /// stay set, so it does too; and either one answers true to a false positive. The
    /// count came down by one each time, below zero, and a <c>ulong</c> that goes below
    /// zero is 2^64 - 1. These tests hold it at zero, and, for the deletable filter, hold
    /// it where it is when a removal took nothing out.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestRemovalCount
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        // ------------------------------------------------------------------ counting

        /// <summary>
        /// Four-bit counters stop at 15, so an item added 16 times is still found after 16
        /// removals, and the 17th returns true and used to take the count below zero.
        /// </summary>
        [TestMethod]
        public void TestCountingCountStopsAtZeroWhenRemovedMoreOftenThanAdded()
        {
            var f = new CountingBloomFilter(10, 4, 0.01);
            var a = Key("a");
            for (int i = 0; i < 16; i++)
            {
                f.Add(a);
            }
            Assert.AreEqual(16ul, f.Count());

            for (int i = 0; i < 17; i++)
            {
                Assert.IsTrue(f.TestAndRemove(a),
                    $"removal {i + 1}: the counters saturated at 15, so the item still tests " +
                    "present however often it is removed, and this is the over-removal.");
            }

            Assert.AreEqual(0ul, f.Count());
        }

        /// <summary>
        /// The count is a number a caller reads, saves and adds to. Stopping at zero has to
        /// leave it counting from zero, which a count that had wrapped does not.
        /// </summary>
        [TestMethod]
        public void TestCountingCountCountsFromZeroAfterOverRemoval()
        {
            var f = new CountingBloomFilter(10, 4, 0.01);
            var a = Key("a");
            for (int i = 0; i < 16; i++)
            {
                f.Add(a);
            }
            for (int i = 0; i < 17; i++)
            {
                f.TestAndRemove(a);
            }

            f.Add(Key("b"));
            f.Add(Key("c"));
            f.TestAndAdd(Key("d"));
            Assert.AreEqual(3ul, f.Count());
        }

        /// <summary>
        /// A payload stores the count, so a wrapped one is saved and comes back wrapped.
        /// </summary>
        [TestMethod]
        public void TestCountingCountSurvivesARoundTripAfterOverRemoval()
        {
            var f = new CountingBloomFilter(10, 4, 0.01);
            var a = Key("a");
            for (int i = 0; i < 16; i++)
            {
                f.Add(a);
            }
            for (int i = 0; i < 17; i++)
            {
                f.TestAndRemove(a);
            }

            var back = Persistence.FromByteArray<CountingBloomFilter>(f.ToByteArray());
            Assert.AreEqual(0ul, back.Count());
        }

        // ------------------------------------------------------------------ deletable

        /// <summary>
        /// The hash that lets a test choose positions. A 32-bit placement takes the low half
        /// as the first position and the high half as the step to each next one.
        /// </summary>
        private static ulong Hash(uint first, uint step) => ((ulong)step << 32) | first;

        /// <summary>
        /// A, X and Y for a filter whose k is 2. A sits at bits 3 and 4. X sits at bit 3
        /// twice and Y at bit 4 twice, so each tests present while A is the only item
        /// added, and each removes one of A's bits. P sits at bits 3 and 40, which are in
        /// different regions of a filter of 10, and Q at bits 40 and 100, so that adding Q
        /// after P finds bit 40 set.
        /// </summary>
        private static ulong ScriptedHash(ReadOnlySpan<byte> data) => (char)data[0] switch
        {
            'A' => Hash(3, 1),      // bits 3, 4: region 0
            'X' => Hash(3, 0),      // bits 3, 3
            'Y' => Hash(4, 0),      // bits 4, 4
            'P' => Hash(3, 37),     // bits 3, 40: regions 0 and 1
            'Q' => Hash(40, 60),    // bits 40, 100: regions 1 and 3
            _ => throw new InvalidOperationException("an item the script has no positions for"),
        };

        private static DeletableBloomFilter ScriptedFilter() => new(100, 10, 0.25, ScriptedHash);

        /// <summary>
        /// A removal that takes bits out of the filter takes them from an item that was
        /// counted, but two false positives can take out more than one item's worth. A
        /// holds two bits; X and Y are each answered true and each clear one. The count was
        /// 1, then 0, then below it.
        /// </summary>
        [TestMethod]
        public void TestDeletableCountStopsAtZeroWhenBitsStillClear()
        {
            var d = ScriptedFilter();
            Assert.AreEqual(2u, d.K());

            d.Add(Key("A"));
            Assert.AreEqual(1ul, d.Count());

            Assert.IsTrue(d.TestAndRemove(Key("X")), "bit 3 is set, so X is a false positive");
            Assert.IsTrue(d.Buckets.Get(3) == 0 && d.Buckets.Get(4) != 0,
                "X cleared bit 3 and left bit 4 for Y");
            Assert.AreEqual(0ul, d.Count());

            Assert.IsTrue(d.TestAndRemove(Key("Y")), "bit 4 is set, so Y is a false positive");
            Assert.IsTrue(d.Buckets.Get(4) == 0,
                "Y cleared a bit, so this removal did take something out and only the " +
                "count had nothing left to give.");
            Assert.AreEqual(0ul, d.Count());
        }

        /// <summary>
        /// The second add of an item finds its bits set and marks their regions, so no bit
        /// of it can be cleared again. Removing it is refused: the answer is still true,
        /// nothing leaves the filter, the item stays a member, and so the count of what the
        /// filter holds stays where it was.
        /// </summary>
        [TestMethod]
        public void TestDeletableCountIsLeftAloneByARefusedDeletion()
        {
            var d = new DeletableBloomFilter(10, 4, 0.01);
            var a = Key("a");
            d.Add(a);
            d.Add(a);
            Assert.AreEqual(2ul, d.Count());

            var before = SetBits(d);
            for (int i = 0; i < 3; i++)
            {
                Assert.IsTrue(d.TestAndRemove(a), $"removal {i + 1}: the item is still a member");
                Assert.AreEqual(before, SetBits(d),
                    $"removal {i + 1}: every region has collided, so no bit may clear.");
                Assert.AreEqual(2ul, d.Count(), $"removal {i + 1}: nothing was removed");
            }
            Assert.IsTrue(d.Test(a), "a refused deletion leaves the item a member");
        }

        /// <summary>
        /// The same filter, with a removal that does clear: the count comes down for it,
        /// and a second removal of the item, which finds it gone, leaves it alone.
        /// </summary>
        [TestMethod]
        public void TestDeletableCountFallsForAClearedDeletionAndNotForAnAbsentOne()
        {
            var d = new DeletableBloomFilter(10, 4, 0.01);
            var a = Key("a");
            d.Add(a);
            Assert.AreEqual(1ul, d.Count());

            Assert.IsTrue(d.TestAndRemove(a));
            Assert.AreEqual(0ul, d.Count());
            Assert.IsFalse(d.TestAndRemove(a), "the first removal cleared every bit of it");
            Assert.AreEqual(0ul, d.Count());
        }

        /// <summary>
        /// A deletion that clears some of an item's bits and refuses the rest has still
        /// removed it from part of the filter, and is counted. P's bit 3 is in a region
        /// nothing collided in; its bit 40 is in region 1, which Q's add marked.
        /// </summary>
        [TestMethod]
        public void TestDeletableCountFallsForADeletionThatClearsOnlySomeBits()
        {
            var d = ScriptedFilter();
            d.Add(Key("P"));
            d.Add(Key("Q"));
            Assert.AreEqual(2ul, d.Count());
            Assert.IsTrue(d.Collisions.Get(1) != 0 && d.Collisions.Get(0) == 0,
                "Q found bit 40 set, and region 1 is the one marked");

            Assert.IsTrue(d.TestAndRemove(Key("P")));
            Assert.IsTrue(d.Buckets.Get(3) == 0 && d.Buckets.Get(40) != 0,
                "P's bit in the unmarked region cleared and its bit in region 1 did not");
            Assert.AreEqual(1ul, d.Count());
        }

        private static long SetBits(DeletableBloomFilter d)
        {
            long set = 0;
            for (uint i = 0; i < d.Buckets.count; i++)
            {
                if (d.Buckets.Get(i) != 0) set++;
            }
            return set;
        }

        // ------------------------------------------------------------------ the property

        private static void AssertWithinTheAdds(string name, int step, ulong adds, ulong count) =>
            Assert.IsLessThanOrEqualTo(adds, count,
                $"{name} step {step}: the count is {count} after {adds} adds.");

        /// <summary>
        /// What the filters promise whatever is asked of them: they cannot hold more items
        /// than were added, so <c>Count()</c> never exceeds the number of adds. The stream
        /// removes items held, items held again after already being removed, and items
        /// never added, in a filter small enough that most of them test present; it is the
        /// sort of use "remove it if it exists" invites. It runs in phases of a few adds
        /// and many more removals, so the count is taken to zero again and again.
        /// <para>
        /// Two-bit counters, because four-bit ones saturate only at a load this stream
        /// does not reach, and a counter that never saturates is decremented by every
        /// removal and cannot be over-removed.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestCountingCountNeverExceedsTheAddsUnderOverRemoval()
        {
            var f = new CountingBloomFilter(20, 2, 0.5);
            var rand = new Random(101);
            var held = new List<string>();
            ulong adds = 0;
            var overRemoved = 0;
            var step = 0;

            for (int phase = 0; phase < 60; phase++)
            {
                // A few adds, some of them of an item already held...
                for (int i = 0, n = 3 + rand.Next(10); i < n; i++, step++)
                {
                    string key;
                    if (held.Count > 0 && rand.Next(4) == 0)
                    {
                        key = held[rand.Next(held.Count)];
                    }
                    else
                    {
                        key = $"new-{step}";
                        held.Add(key);
                    }
                    f.Add(Key(key));
                    adds++;
                    AssertWithinTheAdds(nameof(CountingBloomFilter), step, adds, f.Count());
                }

                // ...then far more removals than there were adds. Held items stay on the
                // list, so one is asked for again and again, and a third of the asks are
                // for items never added.
                for (int i = 0; i < 80; i++, step++)
                {
                    var key = rand.Next(3) > 0 ? held[rand.Next(held.Count)] : $"absent-{step}";
                    var atZero = f.Count() == 0;
                    if (f.TestAndRemove(Key(key)) && atZero)
                    {
                        overRemoved++;
                    }
                    AssertWithinTheAdds(nameof(CountingBloomFilter), step, adds, f.Count());
                }
            }

            // Vacuity: the property is empty if no removal was ever answered true at a
            // count of zero, which is the only place the count could go below it.
            Console.WriteLine($"{adds} adds, {overRemoved} removals answered true at a count of zero");
            Assert.IsGreaterThan(0, overRemoved,
                "no removal was answered true at a count of zero, so the count was never " +
                "asked to stay above nothing.");
        }

        /// <summary>
        /// Items named <c>at-J</c> put every one of their positions on bit J, the others
        /// are scattered by a hash of their bytes, and either is a legal hash. A scattered
        /// item sets k bits, and each <c>at-J</c> at a set bit tests present and clears
        /// one, so k removals of items never added can follow every add.
        /// </summary>
        private static ulong PlacedOrScattered(ReadOnlySpan<byte> data)
        {
            if (data.Length > 3 && data[0] == (byte)'a' && data[1] == (byte)'t' && data[2] == (byte)'-')
            {
                return uint.Parse(Encoding.ASCII.GetString(data[3..]));
            }

            ulong h = 14695981039346656037UL;
            foreach (var b in data)
            {
                h ^= b;
                h *= 1099511628211UL;
            }
            return ((ulong)((h >> 40) % 11 + 1) << 32) | (uint)h;
        }

        /// <summary>
        /// The same property for the deletable filter, which only counts a removal that
        /// cleared a bit. Each phase starts from a reset, because a region that has
        /// collided stays collided and a filter that has seen a few phases protects every
        /// bit; adds items, then asks, of every bit set, for an item never added that sits
        /// exactly on it. Each of those is answered true and clears the bit, so there are
        /// more of them than there were adds. The adds include items added twice, whose
        /// removal is refused.
        /// </summary>
        [TestMethod]
        public void TestDeletableCountNeverExceedsTheAddsUnderOverRemoval()
        {
            var f = new DeletableBloomFilter(40, 20, 0.25, PlacedOrScattered);
            Assert.IsGreaterThan(1u, f.K(), "a filter of one hash sets a bit per add and so cannot be over-removed");

            var overRemoved = 0;
            var refused = 0;
            var step = 0;
            for (int phase = 0; phase < 40; phase++)
            {
                f.Reset();
                ulong adds = 0;
                for (int i = 0, n = 3 + phase % 10; i < n; i++, step++)
                {
                    f.Add(Key($"item-{phase}-{i}"));
                    adds++;
                    if (i % 4 == 0)
                    {
                        f.Add(Key($"item-{phase}-{i}"));
                        adds++;
                    }
                    AssertWithinTheAdds(nameof(DeletableBloomFilter), step, adds, f.Count());
                }

                for (uint bit = 0; bit < f.Buckets.count; bit++, step++)
                {
                    if (f.Buckets.Get(bit) == 0) continue;

                    var atZero = f.Count() == 0;
                    var bitsBefore = SetBits(f);
                    var countBefore = f.Count();
                    if (!f.TestAndRemove(Key($"at-{bit}"))) continue;

                    var cleared = SetBits(f) < bitsBefore;
                    if (cleared && atZero) overRemoved++;
                    if (!cleared)
                    {
                        refused++;
                        Assert.AreEqual(countBefore, f.Count(),
                            $"step {step}: a refused deletion cleared nothing and must leave the count.");
                    }
                    AssertWithinTheAdds(nameof(DeletableBloomFilter), step, adds, f.Count());
                }
            }

            // Vacuity, both ways: a removal that cleared a bit at a count of zero is the
            // place the count could go below it, and a refused one is the place it must
            // not move.
            Console.WriteLine($"{overRemoved} clearing removals at a count of zero, {refused} refused");
            Assert.IsGreaterThan(0, overRemoved,
                "no removal cleared a bit at a count of zero, so the count was never asked " +
                "to stay above nothing.");
            Assert.IsGreaterThan(0, refused, "no removal was refused, so the rule for those was not exercised.");
        }
    }
}
