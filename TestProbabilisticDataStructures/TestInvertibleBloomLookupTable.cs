using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestInvertibleBloomLookupTable
    {
        private static byte[] Key(long i)
        {
            var key = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(key, i);
            return key;
        }

        private static long Value(byte[] key) => BinaryPrimitives.ReadInt64LittleEndian(key);

        /// <summary>
        /// Whether every count in the table is zero -- the whole of what the decode used to
        /// check before calling a table empty. Read from the written bytes, not from
        /// anything the decoder computes: FORMAT.md puts the payload at byte 14, and this
        /// table's payload opens with a u32 cell count and a u32 key size, then its counts
        /// as a u32 length followed by an i64 per cell.
        /// </summary>
        private static bool CountsAllCancel(InvertibleBloomLookupTable table)
        {
            const int CountsOffset = 14 + 4 + 4 + 4;

            var bytes = table.ToByteArray();
            for (var cell = 0; cell < table.Cells(); cell++)
            {
                if (BinaryPrimitives.ReadInt64LittleEndian(
                        bytes.AsSpan(CountsOffset + cell * sizeof(long))) != 0)
                {
                    return false;
                }
            }

            return true;
        }

        [TestMethod]
        public void TestAnEmptyTableDecodesToNothing()
        {
            var table = new InvertibleBloomLookupTable(expectedDifferences: 100, keySize: 8);

            Assert.IsTrue(table.TryDecode(out var present, out var absent));
            Assert.HasCount(0, present);
            Assert.HasCount(0, absent);
        }

        [TestMethod]
        public void TestWhatWasAddedComesBackOut()
        {
            var table = new InvertibleBloomLookupTable(expectedDifferences: 100, keySize: 8);
            for (var i = 0; i < 50; i++)
            {
                table.Add(Key(i));
            }

            Assert.IsTrue(table.TryDecode(out var present, out var absent),
                "a table holding 50 keys, sized for 100, would not decode");

            CollectionAssert.AreEquivalent(
                Enumerable.Range(0, 50).Select(i => (long)i).ToArray(),
                present.Select(Value).ToArray());
            Assert.HasCount(0, absent);
        }
        /// <summary>
        /// The whole reason for the structure: two sets of a hundred thousand keys that
        /// differ by ten, reconciled by exchanging a table sized for ten.
        /// <para>
        /// Nothing else here can do this. A <see cref="ThetaSketch"/> will tell you the
        /// two sets differ by about ten; this hands you the ten keys.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestTwoLargeSetsDifferingSlightlyAreReconciledCheaply()
        {
            const int Shared = 100_000;

            var mine = new InvertibleBloomLookupTable(expectedDifferences: 20, keySize: 8);
            var theirs = new InvertibleBloomLookupTable(expectedDifferences: 20, keySize: 8);

            for (var i = 0; i < Shared; i++)
            {
                mine.Add(Key(i));
                theirs.Add(Key(i));
            }

            // Five only I have, five only they have.
            for (var i = 0; i < 5; i++)
            {
                mine.Add(Key(1_000_000 + i));
                theirs.Add(Key(2_000_000 + i));
            }

            var difference = mine.Subtract(theirs);

            Assert.IsTrue(difference.TryDecode(out var onlyMine, out var onlyTheirs),
                "a difference of ten would not decode from a table sized for twenty");

            CollectionAssert.AreEquivalent(
                Enumerable.Range(0, 5).Select(i => 1_000_000L + i).ToArray(),
                onlyMine.Select(Value).ToArray());
            CollectionAssert.AreEquivalent(
                Enumerable.Range(0, 5).Select(i => 2_000_000L + i).ToArray(),
                onlyTheirs.Select(Value).ToArray());

            // And the table that carried the answer is tiny next to the sets it came
            // from -- which is the property, not a nicety.
            Assert.IsLessThan(1000ul, difference.SizeInBytes(),
                $"the table took {difference.SizeInBytes()} bytes to reconcile two sets " +
                $"of {Shared}");
        }

        /// <summary>
        /// Sized for too little, it says so rather than returning part of the answer.
        /// A partial reconciliation that looked complete would be far worse than a
        /// refusal, since the caller would act on it.
        /// </summary>
        [TestMethod]
        public void TestTooManyDifferencesFailToDecodeRatherThanMislead()
        {
            var mine = new InvertibleBloomLookupTable(expectedDifferences: 10, keySize: 8);
            var theirs = new InvertibleBloomLookupTable(expectedDifferences: 10, keySize: 8);

            for (var i = 0; i < 1000; i++)
            {
                mine.Add(Key(i));
            }

            for (var i = 0; i < 1000; i++)
            {
                theirs.Add(Key(500_000 + i));
            }

            Assert.IsFalse(mine.Subtract(theirs).TryDecode(out _, out _),
                "a table sized for ten differences claimed to have decoded two thousand");
        }

        /// <summary>
        /// Two keys on opposite sides that land in the same cells cancel each other's
        /// counts in every one of them, and leave both keys xored together in the key and
        /// hash sums. Read by its counts alone, that table is empty, and two sets that
        /// differ by a key each way decoded as identical (#146).
        /// <para>
        /// A table is empty only when every field of every cell is zero. Eppstein,
        /// Goodrich, Uyeda and Varghese, "What's the Difference? Efficient Set
        /// Reconciliation without Prior Context" (SIGCOMM 2011), section 3.1, Algorithm 3:
        /// after peeling, the decode returns FAIL for any cell whose idSum, hashSum or
        /// count is non-zero. Goodrich and Mitzenmacher (arXiv:1101.2245v3, section 3)
        /// say the same of a table that sees deletions it never saw inserted -- a cell is
        /// empty when "the count field is 0, and the keySum and hashkeySum are also 0" --
        /// and a subtracted table is that case, since one side's keys arrive as
        /// deletions. Checked against both papers' text, November 2026.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestKeysCancellingInEveryCountAreNotReportedAsNoDifference()
        {
            var mine = new InvertibleBloomLookupTable(8, 8).Add(Encoding.ASCII.GetBytes("k0000000"));
            var theirs = new InvertibleBloomLookupTable(8, 8).Add(Encoding.ASCII.GetBytes("k0000307"));
            var difference = mine.Subtract(theirs);

            // This pair was found by search because the two keys share all four of their
            // cells. If the hash or the cell placement changes they will not, and the
            // decode below would pass without exercising anything.
            Assert.IsTrue(CountsAllCancel(difference),
                "the pair no longer cancels in every count, so this test is not testing " +
                "the case it names -- find a pair that does");

            Assert.IsFalse(difference.TryDecode(out var onlyMine, out var onlyTheirs),
                "two sets differing by a key each way decoded as having no difference");

            // No cell held exactly one key, so nothing came out before it stalled.
            Assert.HasCount(0, onlyMine);
            Assert.HasCount(0, onlyTheirs);
        }

        /// <summary>
        /// The same cancellation inside a larger difference: peeling takes out every key it
        /// can reach, stalls on keys whose counts cancel, and counts alone then call the
        /// partial listing complete. The caller is handed part of the difference as the
        /// whole of it (#146).
        /// <para>
        /// So every decode here must be right or say it failed, and a failed one may hand
        /// back only keys that are in the difference -- the part that came out before it
        /// stalled.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestADecodeThatSucceedsHasTheWholeDifference()
        {
            // Four keys each way in a table sized for eight: inside the sizing, and small
            // enough that keys sharing their cells is common.
            const int Trials = 5000;
            const int PerSide = 4;

            var succeeded = 0;
            var cancelledPartway = 0;

            for (var t = 0; t < Trials; t++)
            {
                var mine = new InvertibleBloomLookupTable(8, 8);
                var theirs = new InvertibleBloomLookupTable(8, 8);
                var onlyMine = new HashSet<long>();
                var onlyTheirs = new HashSet<long>();

                for (var i = 0; i < PerSide; i++)
                {
                    var x = t * 1_000_000L + i;
                    var y = t * 1_000_000L + 500_000 + i;
                    mine.Add(Key(x));
                    onlyMine.Add(x);
                    theirs.Add(Key(y));
                    onlyTheirs.Add(y);
                }

                var decoded = mine.Subtract(theirs).TryDecode(out var added, out var removed);
                var gotMine = added.Select(Value).ToHashSet();
                var gotTheirs = removed.Select(Value).ToHashSet();

                Assert.IsTrue(
                    gotMine.Count == added.Count && gotTheirs.Count == removed.Count
                        && gotMine.IsSubsetOf(onlyMine) && gotTheirs.IsSubsetOf(onlyTheirs),
                    $"trial {t}: listed a key that is not in the difference, or one twice");

                var whole = gotMine.Count == PerSide && gotTheirs.Count == PerSide;
                if (decoded)
                {
                    Assert.IsTrue(whole,
                        $"trial {t}: reported success having recovered " +
                        $"{gotMine.Count + gotTheirs.Count} of the {2 * PerSide} keys");
                    succeeded++;
                }

                // What did not come out, rebuilt on its own. Peeling takes a key out by
                // the same arithmetic that put it in, so these are the counts the stalled
                // decode was left holding: all zero is the case that used to pass.
                if (!whole && gotMine.Count + gotTheirs.Count > 0)
                {
                    var left = new InvertibleBloomLookupTable(8, 8);
                    foreach (var x in onlyMine.Except(gotMine))
                    {
                        left.Add(Key(x));
                    }

                    foreach (var y in onlyTheirs.Except(gotTheirs))
                    {
                        left.Remove(Key(y));
                    }

                    if (CountsAllCancel(left))
                    {
                        cancelledPartway++;
                    }
                }
            }

            // Vacuity, both ways. The streams are keyed by trial, so these reproduce
            // exactly: probed at 89 partial listings left with every count cancelled and
            // 2,108 complete decodes. Each floor is the probe less about four binomial
            // standard errors (9.4 and 34.9), so a change to the hash or the cell
            // placement, which redraws every trial, still clears it while exercising the
            // same case. The first says the cancellation was met; the second, that the
            // decode did not get there by refusing everything.
            Assert.IsGreaterThanOrEqualTo(50, cancelledPartway,
                $"only {cancelledPartway} decodes stalled with every count cancelled after " +
                "recovering part of the difference, so the bound above was barely tested");
            Assert.IsGreaterThanOrEqualTo(1950, succeeded,
                $"only {succeeded} of {Trials} decodes succeeded");
        }

        /// <summary>
        /// The key sums are what is left when the hashes collide. Under a hash that gives
        /// every key the same value, two keys on opposite sides cancel each other's hashes
        /// as well as their counts, and only the two keys xored together say the table is
        /// not empty.
        /// <para>
        /// The table is sized for two, so its four cells hold every key whatever the hash,
        /// and the keys are 0 and <see cref="long.MinValue"/>, which differ only in the top
        /// bit of their last byte. So the residue sits at the end of every cell, where a
        /// check that walked the key sums by cell rather than by byte would not look.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestKeysWhoseHashesCollideAreNotReportedAsNoDifference()
        {
            var table = new InvertibleBloomLookupTable(2, 8, _ => 0x9E3779B97F4A7C15UL)
                .Add(Key(0))
                .Remove(Key(long.MinValue));

            Assert.AreEqual(4u, table.Cells(), "every key has to be in every cell");
            Assert.IsTrue(CountsAllCancel(table));

            Assert.IsFalse(table.TryDecode(out var added, out var removed),
                "two keys whose hashes collide decoded as having no difference");
            Assert.HasCount(0, added);
            Assert.HasCount(0, removed);
        }

        /// <summary>
        /// The hash sums are what is left when the keys themselves cancel. A table sized
        /// for two differences has four cells and every key occupies all four, so 1 and 2
        /// against 4 and 7 -- which xor to zero -- leave every count and every key sum at
        /// zero, and only the hashes say anything is there.
        /// </summary>
        [TestMethod]
        public void TestKeysWhoseBytesCancelAreNotReportedAsNoDifference()
        {
            long[] mine = [1, 2];
            long[] theirs = [4, 7];

            var table = new InvertibleBloomLookupTable(2, 8);
            foreach (var x in mine)
            {
                table.Add(Key(x));
            }

            foreach (var y in theirs)
            {
                table.Remove(Key(y));
            }

            Assert.AreEqual(4u, table.Cells(), "every key has to be in every cell");
            Assert.AreEqual(0L, mine.Concat(theirs).Aggregate((a, b) => a ^ b),
                "the keys have to cancel in the key sums");
            Assert.IsTrue(CountsAllCancel(table));

            Assert.IsFalse(table.TryDecode(out var added, out var removed),
                "keys whose bytes cancel decoded as having no difference");
            Assert.HasCount(0, added);
            Assert.HasCount(0, removed);
        }

        /// <summary>
        /// And the counts are what is left when a key is added twice, or removed twice:
        /// xoring it in twice takes it back out of the key and hash sums, and only a count
        /// of two says it is there. No cell holds it exactly once, so it cannot be listed,
        /// and the decode has to say so rather than report the table empty. Both signs,
        /// because a subtracted table holds the other side's keys as negatives.
        /// </summary>
        [TestMethod]
        public void TestAKeyAddedOrRemovedTwiceIsNotReportedAsNoDifference()
        {
            var cases = new (string Name, InvertibleBloomLookupTable Table)[]
            {
                ("added", new InvertibleBloomLookupTable(8, 8).Add(Key(5)).Add(Key(5))),
                ("removed", new InvertibleBloomLookupTable(8, 8).Remove(Key(5)).Remove(Key(5))),
            };

            foreach (var (name, table) in cases)
            {
                // Also the check that CountsAllCancel reads counts and not something that
                // happens to be zero: here they are not.
                Assert.IsFalse(CountsAllCancel(table), name);

                Assert.IsFalse(table.TryDecode(out var added, out var removed),
                    $"a key {name} twice decoded as an empty table");
                Assert.HasCount(0, added, name);
                Assert.HasCount(0, removed, name);
            }
        }

        [TestMethod]
        public void TestSubtractingIdenticalSetsLeavesNothing()
        {
            var mine = new InvertibleBloomLookupTable(20, 8);
            var theirs = new InvertibleBloomLookupTable(20, 8);

            for (var i = 0; i < 5000; i++)
            {
                mine.Add(Key(i));
                theirs.Add(Key(i));
            }

            Assert.IsTrue(mine.Subtract(theirs).TryDecode(out var added, out var removed));
            Assert.HasCount(0, added);
            Assert.HasCount(0, removed);
        }

        /// <summary>
        /// Adding and removing the same key leaves the table as it was, which is what
        /// makes subtraction work at all.
        /// </summary>
        [TestMethod]
        public void TestRemovingUndoesAdding()
        {
            var table = new InvertibleBloomLookupTable(20, 8);

            table.Add(Key(1));
            table.Add(Key(2));
            table.Remove(Key(1));

            Assert.IsTrue(table.TryDecode(out var added, out var removed));
            CollectionAssert.AreEquivalent(new[] { 2L }, added.Select(Value).ToArray());
            Assert.HasCount(0, removed);

            // And removing something never added leaves it owed, which is how a
            // subtracted table represents "they have this and I do not".
            table.Remove(Key(9));
            Assert.IsTrue(table.TryDecode(out _, out var owed));
            CollectionAssert.AreEquivalent(new[] { 9L }, owed.Select(Value).ToArray());
        }

        [TestMethod]
        public void TestBadArgumentsAreRefused()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new InvertibleBloomLookupTable(0, 8));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new InvertibleBloomLookupTable(20, 0));

            var table = new InvertibleBloomLookupTable(20, 8);
            Assert.ThrowsExactly<ArgumentNullException>(() => table.Add(null!));

            // Keys are combined by exclusive-or, so a short one would corrupt the table
            // rather than merely be odd.
            var ex = Assert.ThrowsExactly<ArgumentException>(() => table.Add(new byte[4]));
            StringAssert.Contains(ex.Message, "same width");

            Assert.ThrowsExactly<ArgumentException>(
                () => table.Subtract(new InvertibleBloomLookupTable(20, 4)));
            Assert.ThrowsExactly<ArgumentException>(
                () => table.Subtract(new InvertibleBloomLookupTable(50, 8)));
            Assert.ThrowsExactly<ArgumentNullException>(() => table.Subtract(null!));
        }

        /// <summary>
        /// A table is meant to be sent somewhere, so a restored one has to subtract
        /// against a table built by whoever received it.
        /// </summary>
        [TestMethod]
        public void TestRoundTripsAndStillReconciles()
        {
            var mine = new InvertibleBloomLookupTable(20, 8);
            var theirs = new InvertibleBloomLookupTable(20, 8);

            for (var i = 0; i < 10_000; i++)
            {
                mine.Add(Key(i));
                theirs.Add(Key(i));
            }

            mine.Add(Key(999_001));
            theirs.Add(Key(999_002));

            var shipped = Persistence.FromByteArray<InvertibleBloomLookupTable>(mine.ToByteArray());

            Assert.AreEqual(mine.Cells(), shipped.Cells());
            Assert.AreEqual(mine.KeySize(), shipped.KeySize());

            Assert.IsTrue(shipped.Subtract(theirs).TryDecode(out var onlyMine, out var onlyTheirs),
                "a restored table would not reconcile");
            CollectionAssert.AreEquivalent(new[] { 999_001L }, onlyMine.Select(Value).ToArray());
            CollectionAssert.AreEquivalent(new[] { 999_002L }, onlyTheirs.Select(Value).ToArray());
        }

        [TestMethod]
        public void TestAnImpossiblePayloadIsRefused()
        {
            var clean = new InvertibleBloomLookupTable(20, 8).Add(Key(1)).ToByteArray();

            var bad = (byte[])clean.Clone();
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(14 + 4), 0);
            var crc = new System.IO.Hashing.Crc32();
            crc.Append(bad.AsSpan(4, bad.Length - 8));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                bad.AsSpan(bad.Length - 4), crc.GetCurrentHashAsUInt32());

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<InvertibleBloomLookupTable>(bad));
            StringAssert.Contains(ex.Message, "byte");
        }

    }
}
