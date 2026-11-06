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
        /// <summary>
        /// States whose payload <see cref="TestPayloadCount"/> holds the count to: the key
        /// size sizes one of the three runs, so keys from one byte, and a subtracted table,
        /// whose counts are negative.
        /// </summary>
        internal static IEnumerable<PayloadState> PayloadStates()
        {
            foreach (var keySize in new[] { 1, 7, 8, 13 })
            {
                yield return PayloadState.Of($"{keySize}-byte keys, empty",
                    new InvertibleBloomLookupTable(4, keySize));

                var table = new InvertibleBloomLookupTable(4, keySize);
                var other = new InvertibleBloomLookupTable(4, keySize);
                for (var i = 0; i < 5; i++)
                {
                    var key = new byte[keySize];
                    key[0] = (byte)(i + 1);
                    table.Add(key);
                    key[^1] ^= 0x80;
                    other.Add(key);
                }

                yield return PayloadState.Of($"{keySize}-byte keys, filled", table);
                yield return PayloadState.Of($"{keySize}-byte keys, subtracted", other.Subtract(table));
            }
        }

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
            const uint SizedFor = 20;
            const int KeySize = 8;

            var mine = new InvertibleBloomLookupTable(SizedFor, KeySize);
            var theirs = new InvertibleBloomLookupTable(SizedFor, KeySize);

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

            // The table that carried the answer is tiny next to the sets it came from,
            // which is the property and not a nicety: what it costs follows the
            // difference, not the sets. So the assertions are stated against the
            // difference, and say nothing about how many cells a table is given. That
            // rule is pinned, from the paper and exactly, in TestStructureGeometry.
            //
            // This used to be a flat bound of a thousand bytes, which was comfortable
            // when written and sat just under the table's size once the sizing rule
            // changed (#156): a test about reconciliation that failed on a one-cell
            // edit to a constant it was never about.
            //
            // First: none of the shared keys changed what the table costs. It is
            // exactly the size of an empty one sized for the same difference. Nothing
            // in the structure grows, so this is cheap, but it is the claim itself,
            // and it would be the first thing to notice a table that came to scale
            // with what was put in it.
            Assert.AreEqual(
                new InvertibleBloomLookupTable(SizedFor, KeySize).SizeInBytes(),
                difference.SizeInBytes(),
                $"reconciling two sets of {Shared} changed the size of the table that " +
                "carried the answer, so its cost no longer comes from the difference.");

            // Second: that size is small, as a ceiling per difference it was sized
            // for. Three cells each. The most any table needs at these sizes is about
            // two and a half cells per difference (2.4 at ten, falling from there;
            // the measurements are beside CellsPerDifference), so three leaves the
            // sizing rule room to move inside what it can justify, and still fails a
            // table several times larger than the problem. The cell is the unit
            // because its width follows the key size, and so the bytes follow.
            Assert.IsLessThanOrEqualTo(3u * SizedFor, difference.CellCount,
                $"the table took {difference.CellCount} cells ({difference.SizeInBytes()} " +
                $"bytes) to reconcile two sets of {Shared} that differ by ten, sized for " +
                $"{SizedFor}: more than three cells for every difference it was sized for.");
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
        /// Reconciles <paramref name="trials"/> pairs of tables sized for
        /// <paramref name="differences"/> keys, with that many keys in the difference, and
        /// counts the ones that listed the whole of it.
        /// <para>
        /// Both tables hold a few keys the other has too, so the subtraction has something
        /// to cancel. The rest of the difference is split between the two sides as evenly
        /// as it divides, or all on the first side. The keys are keyed by trial, so every
        /// count reproduces exactly.
        /// </para>
        /// </summary>
        private static (int Complete, int Failed, int WrongSuccesses, long KeysListed)
            ReconcileAtTheSizedDifference(uint differences, int trials, bool allOnOneSide)
        {
            const int Shared = 8;

            var onlyMineCount = allOnOneSide ? (int)differences : (int)((differences + 1) / 2);
            var onlyTheirsCount = (int)differences - onlyMineCount;

            var complete = 0;
            var wrongSuccesses = 0;
            long keysListed = 0;

            for (var t = 0; t < trials; t++)
            {
                var mine = new InvertibleBloomLookupTable(differences, 8);
                var theirs = new InvertibleBloomLookupTable(differences, 8);

                for (var j = 0; j < Shared; j++)
                {
                    var both = Key(t * 1_000_000L + 900_000 + j);
                    mine.Add(both);
                    theirs.Add(both);
                }

                var onlyMine = new HashSet<long>();
                for (var i = 0; i < onlyMineCount; i++)
                {
                    var x = t * 1_000_000L + i;
                    mine.Add(Key(x));
                    onlyMine.Add(x);
                }

                var onlyTheirs = new HashSet<long>();
                for (var i = 0; i < onlyTheirsCount; i++)
                {
                    var y = t * 1_000_000L + 500_000 + i;
                    theirs.Add(Key(y));
                    onlyTheirs.Add(y);
                }

                var decoded = mine.Subtract(theirs).TryDecode(out var added, out var removed);

                var gotMine = added.Select(Value).ToHashSet();
                var gotTheirs = removed.Select(Value).ToHashSet();
                var whole = gotMine.Count == added.Count && gotTheirs.Count == removed.Count
                    && gotMine.SetEquals(onlyMine) && gotTheirs.SetEquals(onlyTheirs);

                if (decoded && whole)
                {
                    complete++;
                    keysListed += added.Count + removed.Count;
                }
                else if (decoded)
                {
                    wrongSuccesses++;
                }
            }

            return (complete, trials - complete, wrongSuccesses, keysListed);
        }

        /// <summary>
        /// A table sized for a number of differences lists that many, completely, with
        /// probability at least 0.99 -- at every size, the smallest included (#150).
        /// <para>
        /// The 0.99 is the figure Eppstein, Goodrich, Uyeda and Varghese, "What's the
        /// Difference?" (SIGCOMM 2011), section 6.1 and Figure 7, use for the overhead a
        /// table needs: "the minimum space overhead (IBF cells/delta) required to
        /// completely recover the set difference with 99% certainty". Their section 6.1
        /// adds that "Deltas below 200 all require at least 50% overhead" and that "beyond
        /// deltas of 1000, the memory overhead reaches an asymptote". Goodrich and
        /// Mitzenmacher (arXiv:1101.2245v3), section 2.4, give that asymptote -- c4 =
        /// 1.295 cells per entry for four hashes, with a failure probability that is
        /// asymptotic too -- and section 5 says that bounds for specific numbers of
        /// entries and cells are the "finite-length analysis" problem, which they did not
        /// attempt. So the small cases are measured, here and in the rule's own
        /// documentation, rather than derived. Checked against both papers' text,
        /// November 2026; the figure's values were read off the plot by eye.
        /// </para>
        /// <para>
        /// Each row is a difference to be sized for, how many reconciliations to run, and
        /// whether the whole difference is on one side. The rows are the coarsest legal
        /// corner (one difference), the sizes where a table of the old size could not
        /// list the difference at all (two to four), a size in the middle where it
        /// managed two in three (twenty), one just under the promise where it was about
        /// to reach it (a hundred), the last size the small-table allowance applies to
        /// and the first it does not, and a large one. The fourth column is a floor on
        /// failures, below.
        /// </para>
        /// <para>
        /// The promise is the floor: 0.99 of the trials. Probed on these keys, every row
        /// clears it by more than six standard errors of its own rate, so a change that
        /// redraws the keys still clears it; the rows are not tuned to it. Beside it, three
        /// guards against passing without testing. <b>Every success listed exactly the
        /// difference</b>: the keys listed across the successes add up to the size of the
        /// difference times their number, so no row passed on empty differences. <b>No
        /// success was wrong</b>, the property of #146 held inside the promise. And <b>a
        /// table that is not too big</b>: where the probe has failures to spare, at least
        /// ten must still happen, which is the fewest probed less four standard errors. A
        /// table sized past its promise would clear the floor with room to spare and give
        /// nothing away, so this is the half of the bound that notices.
        /// </para>
        /// </summary>
        [TestMethod]
        [DataRow(1u, 20000, false, 0)]
        [DataRow(2u, 20000, false, 0)]
        [DataRow(3u, 20000, false, 0)]
        [DataRow(3u, 20000, true, 0)]
        [DataRow(4u, 20000, false, 0)]
        [DataRow(4u, 20000, true, 0)]
        [DataRow(20u, 10000, false, 10)]
        [DataRow(20u, 10000, true, 10)]
        [DataRow(100u, 10000, false, 10)]
        [DataRow(128u, 10000, false, 10)]
        [DataRow(129u, 10000, false, 10)]
        [DataRow(1000u, 1000, false, 0)]
        public void TestATableSizedForADifferenceListsItAtLeastNinetyNinePercentOfTheTime(
            uint differences, int trials, bool allOnOneSide, int minFailures)
        {
            const double Promise = 0.99;

            var (complete, failed, wrongSuccesses, keysListed) =
                ReconcileAtTheSizedDifference(differences, trials, allOnOneSide);

            var where = $"a difference of {differences}" + (allOnOneSide ? " all on one side" : "");

            Assert.IsGreaterThanOrEqualTo((int)Math.Ceiling(Promise * trials), complete,
                $"{where}, from tables sized for {differences}: only {complete} of {trials} " +
                $"reconciliations listed all of it, where the promise is {Promise}");

            Assert.AreEqual(0, wrongSuccesses,
                $"{where}: {wrongSuccesses} decodes reported success with the wrong listing");

            Assert.AreEqual((long)complete * differences, keysListed,
                $"{where}: the successes did not each list {differences} keys, so they were " +
                "not reconciling the difference this test thinks it is");

            Assert.IsGreaterThanOrEqualTo(minFailures, failed,
                $"{where}: only {failed} of {trials} reconciliations failed, so a table " +
                "sized for this many differences is bigger than the promise needs, and the " +
                "floor above would have passed whatever its size");
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
            // Twelve cells, stated outright rather than as a number of differences so
            // the geometry holds when the sizing rule changes (#150): the pair below was
            // found by search against it.
            var mine = InvertibleBloomLookupTable.WithCells(12, 8).Add(Encoding.ASCII.GetBytes("k0000000"));
            var theirs = InvertibleBloomLookupTable.WithCells(12, 8).Add(Encoding.ASCII.GetBytes("k0000307"));
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
            // Four keys each way in twelve cells: small enough that keys sharing their
            // cells is common, which is what this test is about. Stated outright rather
            // than as a number of differences so the geometry holds when the sizing rule
            // changes (#150); a table sized for eight now would have room to spare and
            // meet the case far less often.
            const int Trials = 5000;
            const int PerSide = 4;

            var succeeded = 0;
            var cancelledPartway = 0;

            for (var t = 0; t < Trials; t++)
            {
                var mine = InvertibleBloomLookupTable.WithCells(12, 8);
                var theirs = InvertibleBloomLookupTable.WithCells(12, 8);
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
                    var left = InvertibleBloomLookupTable.WithCells(12, 8);
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
        /// The table has four cells, so a key occupies every one of them whatever the hash,
        /// and the keys are 0 and <see cref="long.MinValue"/>, which differ only in the top
        /// bit of their last byte. So the residue sits at the end of every cell, where a
        /// check that walked the key sums by cell rather than by byte would not look.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestKeysWhoseHashesCollideAreNotReportedAsNoDifference()
        {
            var table = InvertibleBloomLookupTable.WithCells(4, 8, _ => 0x9E3779B97F4A7C15UL)
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
        /// The hash sums are what is left when the keys themselves cancel. A table of four
        /// cells has every key occupying all four, so 1 and 2 against 4 and 7 -- which xor
        /// to zero -- leave every count and every key sum at zero, and only the hashes say
        /// anything is there.
        /// </summary>
        [TestMethod]
        public void TestKeysWhoseBytesCancelAreNotReportedAsNoDifference()
        {
            long[] mine = [1, 2];
            long[] theirs = [4, 7];

            var table = InvertibleBloomLookupTable.WithCells(4, 8);
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
        /// The most cells a table can have, restated here rather than read back from the
        /// table. The counts, the hash sums and the keys are each one array with a slot
        /// for every cell, and no array holds more elements than <c>Array.MaxLength</c>.
        /// </summary>
        private static readonly uint MostCells = (uint)Array.MaxLength;

        /// <summary>
        /// The most differences a table can be sized for: the largest d whose cells,
        /// rounded up, are no more than that. At this size the rule is a plain 1.5 cells
        /// a difference, so d is two thirds of the cells, rounded down.
        /// </summary>
        private static readonly uint MostDifferences = (uint)((ulong)MostCells * 2 / 3);

        /// <summary>
        /// Differences past what a table can be sized for, each a different way the old
        /// arithmetic went wrong. The cell count was worked out as a double and cast to a
        /// uint, and then each array was allocated from it. On .NET 10 the cast saturates
        /// rather than wraps, so nothing was sized too small, but the failure was whatever
        /// the allocator made of a count nobody had checked.
        /// </summary>
        private static (uint Differences, string Why)[] TooManyDifferences() => new[]
        {
            (MostDifferences + 1u, "the first count past the largest table"),
            (1_431_655_765u, "the first count whose cells no longer fit an int"),
            (2_863_311_530u, "the last count whose cells fit a uint exactly"),
            (2_863_311_531u, "the first whose cells overflow a uint, which the cast saturates"),
            (uint.MaxValue, "the largest count there is"),
        };

        /// <summary>
        /// The cell count of the largest table, and the count of differences that gives
        /// it, are accepted. This is the half of the bound that stops a guard from
        /// refusing too much, and it pins the two constants above to the sizing rule: one
        /// more difference needs a cell more than the table can have.
        /// </summary>
        [TestMethod]
        public void TestTheLargestTableThatCanBeSizedForIsAccepted()
        {
            Assert.AreEqual(
                MostCells, InvertibleBloomLookupTable.CellsProvisionedFor(MostDifferences));

            Assert.AreEqual(
                (double)MostCells + 1,
                Math.Ceiling((MostDifferences + 1L) * InvertibleBloomLookupTable.CellsPerDifference),
                "the largest count of differences is not the last one whose cells fit");
        }

        /// <summary>
        /// A number of differences that needs more cells than a table can have is refused
        /// as the argument it is, rather than saturated into a cell count and left for the
        /// allocator to refuse.
        /// </summary>
        [TestMethod]
        public void TestSizingForMoreDifferencesThanATableCanHoldIsRefused()
        {
            foreach (var (differences, why) in TooManyDifferences())
            {
                var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => InvertibleBloomLookupTable.CellsProvisionedFor(differences),
                    $"{differences} differences, {why}");

                Assert.AreEqual("expectedDifferences", ex.ParamName, why);
                Assert.AreEqual(differences, ex.ActualValue, why);

                // The message names the limit, in cells and in the differences it comes to,
                // so that whoever reads it knows how far to come down.
                StringAssert.Contains(ex.Message, $"{MostCells}", why);
                StringAssert.Contains(ex.Message, $"{MostDifferences}", why);
            }
        }

        /// <summary>
        /// The same refusal reaches the public constructor, and is the same exception
        /// whichever way the count overflowed.
        /// </summary>
        [TestMethod]
        public void TestAConstructorGivenTooManyDifferencesIsRefused()
        {
            foreach (var (differences, why) in TooManyDifferences())
            {
                var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => new InvertibleBloomLookupTable(differences, 8),
                    $"{differences} differences, {why}");

                Assert.AreEqual("expectedDifferences", ex.ParamName, why);
            }
        }

        /// <summary>
        /// The keys are stored in a single array of the cells times the key size, so a key
        /// width that is fine for a small table is too much for a large one. The refusal
        /// names the key size, and says both how wide a key could be for these cells and
        /// how many cells keys this wide allow.
        /// <para>
        /// The first case is the ordinary one: eight-byte keys and a few hundred million
        /// differences, whose counts and hash sums are an array each that an allocator
        /// would give, and whose keys are not. The others make the keys the only thing
        /// wrong with a small table. Before the check the keys' array was the one that
        /// failed, as an OverflowException or an OutOfMemoryException depending on which
        /// side of an int the byte count fell, and only after the counts had been
        /// allocated.
        /// </para>
        /// </summary>
        [TestMethod]
        [DataRow(300_000_000u, 8)]
        [DataRow(1000u, 1_431_656)]
        [DataRow(1u, 143_165_573)]
        [DataRow(1u, int.MaxValue)]
        public void TestKeysTooWideForTheCellsAreRefused(uint differences, int keySize)
        {
            var cells = InvertibleBloomLookupTable.CellsProvisionedFor(differences);

            var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new InvertibleBloomLookupTable(differences, keySize));

            Assert.AreEqual("keySize", ex.ParamName);
            Assert.AreEqual(keySize, ex.ActualValue);
            StringAssert.Contains(ex.Message, $"{Array.MaxLength}");
            StringAssert.Contains(ex.Message, $"{Array.MaxLength / cells}",
                "the message should say how wide a key can be for this many cells");
            StringAssert.Contains(ex.Message, $"{Array.MaxLength / keySize}",
                "the message should say how many cells keys this wide allow");

            // The hook that builds a table of an exact size goes through the same check.
            var hooked = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => InvertibleBloomLookupTable.WithCells(cells, keySize));
            Assert.AreEqual("keySize", hooked.ParamName);
        }

        /// <summary>
        /// The hook that builds a table of an exact size is bounded as the constructor is,
        /// so a test cannot reach a size the library itself refuses.
        /// </summary>
        [TestMethod]
        public void TestAnExactSizeBeyondWhatATableCanHaveIsRefused()
        {
            var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => InvertibleBloomLookupTable.WithCells(uint.MaxValue, 1));

            Assert.AreEqual("cells", ex.ParamName);
            StringAssert.Contains(ex.Message, $"{MostCells}");
        }

        /// <summary>
        /// The size check at both edges, asked directly because a table at the edge is
        /// gigabytes and a test is no place to build one. Each pair is the last that
        /// passes and the first that does not, so a limit that is one too tight or one
        /// too loose fails on the side it moved to.
        /// <para>
        /// The cases that decide the keys' bound are the ones where the cells times the key
        /// size comes to exactly what an array can hold, which is what separates "more
        /// than" from "at least". That needs a factorisation of the array limit, so the
        /// precondition is asserted: if the limit ever moves, this says so instead of
        /// quietly testing nothing at the edge.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestTheSizeCheckRefusesExactlyWhatNoArrayCouldHold()
        {
            // Cells at the edge of the cell bound, with the narrowest keys.
            InvertibleBloomLookupTable.ValidTableSize(MostCells, 1);
            var tooMany = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => InvertibleBloomLookupTable.ValidTableSize(MostCells + 1u, 1));
            Assert.AreEqual("cells", tooMany.ParamName);

            // The widest key each cell count allows, and the next width. Eight cells is
            // the one whose next width comes to exactly one byte over the limit, which a
            // check that was one byte loose would let through.
            Assert.AreEqual((long)Array.MaxLength + 1, 8L * (Array.MaxLength / 8 + 1),
                "the array limit has moved, and eight cells no longer land a byte over it");

            foreach (var cells in new[] { 4u, 8u, 15u, 1500u, MostCells })
            {
                var widest = (int)(Array.MaxLength / cells);

                InvertibleBloomLookupTable.ValidTableSize(cells, widest);

                var tooWide = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => InvertibleBloomLookupTable.ValidTableSize(cells, widest + 1),
                    $"{cells} cells with keys one byte wider than they allow");
                Assert.AreEqual("keySize", tooWide.ParamName);
            }

            // Cells times key size exactly the array limit, both ways round, and each one
            // more.
            const uint Factor = 11;
            var other = (uint)(Array.MaxLength / Factor);
            Assert.AreEqual((long)Array.MaxLength, (long)Factor * other,
                "the array limit no longer factors this way; choose another pair");

            InvertibleBloomLookupTable.ValidTableSize(other, (int)Factor);
            InvertibleBloomLookupTable.ValidTableSize(Factor, (int)other);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => InvertibleBloomLookupTable.ValidTableSize(other + 1u, (int)Factor));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => InvertibleBloomLookupTable.ValidTableSize(other, (int)Factor + 1));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => InvertibleBloomLookupTable.ValidTableSize(Factor, (int)other + 1));

            // A product that wraps a uint to nothing, or to something small, is still far
            // too many bytes: the multiplication has to be wide enough not to wrap.
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => InvertibleBloomLookupTable.ValidTableSize(1u << 16, 1 << 16));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => InvertibleBloomLookupTable.ValidTableSize(1u << 16, (1 << 16) + 1));
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

        /// <summary>
        /// FNV-1a, standing in for a hash function of the caller's. A method rather than a
        /// lambda, so that two tables given it compare as using the same one.
        /// </summary>
        private static ulong Fnv(ReadOnlySpan<byte> key)
        {
            var hash = 14695981039346656037UL;
            foreach (var b in key)
            {
                hash = (hash ^ b) * 1099511628211UL;
            }

            return hash;
        }

        /// <summary>
        /// A cell holds a single key when hashing what it holds gives the hash it
        /// recorded, so the difference of two tables built with a hash function of the
        /// caller's has to be built with that function too. A difference that fell back to
        /// the default would find no cell it could peel, and report an unfinished decode
        /// of a table that holds one key on each side.
        /// </summary>
        [TestMethod]
        public void TestASubtractedTableKeepsTheHashFunctionItsInputsUse()
        {
            var mine = new InvertibleBloomLookupTable(20, 8, Fnv);
            var theirs = new InvertibleBloomLookupTable(20, 8, Fnv);

            for (var i = 0; i < 200; i++)
            {
                mine.Add(Key(i));
                theirs.Add(Key(i));
            }

            mine.Add(Key(1_001));
            theirs.Add(Key(1_002));

            var difference = mine.Subtract(theirs);

            Assert.AreEqual(mine.Hash, difference.Hash);
            Assert.IsTrue(difference.TryDecode(out var onlyMine, out var onlyTheirs),
                "the difference could not be peeled, which is what a table hashing its " +
                "cells with another function than the keys were placed by looks like");
            CollectionAssert.AreEquivalent(new[] { 1_001L }, onlyMine.Select(Value).ToArray());
            CollectionAssert.AreEquivalent(new[] { 1_002L }, onlyTheirs.Select(Value).ToArray());
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

        /// <summary>
        /// A read holds the counts and the hash sums once and is charged for them once (#335). In
        /// the ordinary envelope, what it allocates and is charged beyond the payload it holds is
        /// the table's three arrays, the counts and hash sums read into theirs and not first into
        /// arrays of their own and copied, and the key sums the run they are read as; in the large
        /// form, where the payload is not held, it is the three arrays alone, with no buffer on
        /// the heap for the pieces the counts and hash sums are read in.
        /// </summary>
        /// <remarks>
        /// A table for 10,000 differences, of more than 15,000 cells, so that its counts and its
        /// hash sums are each longer than a piece of a run on the heap
        /// (<see cref="PayloadSource.PieceLength"/>) and a buffer of one is a whole piece. The
        /// slack, 4 KiB, is a twentieth of the piece and less of either run, so a copy of any one
        /// of them fails it. Each figure is held from below too, to the payload and the three
        /// arrays, so that a measurement of nothing cannot pass. The reads are counted in a no-GC
        /// region (#293).
        /// </remarks>
        [TestMethod]
        public void TestAReadHoldsItsCountsAndHashSumsOnce()
        {
            const long Slack = 4 * 1024;

            var table = new InvertibleBloomLookupTable(10_000, 8);
            for (var i = 0; i < 5_000; i++)
            {
                table.Add(Key(i));
            }

            var cells = table.Cells();
            var bytes = table.ToByteArray();
            long payload = bytes.Length - PersistenceFormat.EnvelopeLength;
            var arrays = ReadBudget.Sum(
                ReadBudget.ArrayOf(cells, sizeof(long)),
                ReadBudget.ArrayOf((ulong)cells * (ulong)table.KeySize(), 1),
                ReadBudget.ArrayOf(cells, sizeof(ulong)));
            Assert.IsGreaterThan(PayloadSource.PieceLength + Slack, (long)cells * sizeof(long),
                "premise: the counts are no longer than a piece on the heap");

            InvertibleBloomLookupTable.ReadFrom(new MemoryStream(bytes, writable: false));
            var ordinary = NoGCRegion.Allocated(() => InvertibleBloomLookupTable.ReadFrom(new MemoryStream(bytes, writable: false)));
            var charged = CapacityModel.ChargeOf<InvertibleBloomLookupTable>(StructureId.InvertibleBloomLookupTable, bytes);

            var large = LargeForm.WriteLarge(table);
            Assert.IsTrue(LargeForm.IsLarge(large), "the table was not written in the large form");
            InvertibleBloomLookupTable.ReadFrom(new MemoryStream(large, writable: false));
            var streamed = NoGCRegion.Allocated(() => InvertibleBloomLookupTable.ReadFrom(new MemoryStream(large, writable: false)));
            var budget = new ReadBudget(long.MaxValue, StructureId.InvertibleBloomLookupTable);
            CollectionAssert.AreEqual(bytes,
                InvertibleBloomLookupTable.ReadFrom(new MemoryStream(large, writable: false), new ReadOptions(budget)).ToByteArray(),
                "the large form does not read back as the table");

            Console.WriteLine(
                $"[HOLDS ONCE] InvertibleBloomLookupTable: payload {payload:N0}, arrays {arrays:N0}; ordinary read {ordinary:N0}, " +
                $"charged {charged:N0}; large-form read {streamed:N0}, charged {budget.Charged:N0}");

            Assert.IsGreaterThanOrEqualTo(payload + arrays, ordinary,
                $"the read allocated {ordinary:N0} bytes, less than the {payload:N0}-byte payload it holds and the {arrays:N0} bytes of arrays");
            Assert.IsLessThanOrEqualTo(payload + arrays + Slack, ordinary,
                $"the read allocated {ordinary:N0} bytes, {ordinary - payload - arrays:N0} more than its payload and its arrays");
            Assert.IsGreaterThanOrEqualTo(payload + arrays, charged,
                $"the read was charged {charged:N0} bytes, less than its payload and its arrays");
            Assert.IsLessThanOrEqualTo(payload + arrays + Slack, charged,
                $"the read was charged {charged:N0} bytes, {charged - payload - arrays:N0} more than its payload and its arrays");

            Assert.IsGreaterThanOrEqualTo(arrays, streamed,
                $"the read of the large form allocated {streamed:N0} bytes, less than the {arrays:N0} bytes of arrays");
            Assert.IsLessThanOrEqualTo(arrays + Slack, streamed,
                $"the read of the large form allocated {streamed:N0} bytes, {streamed - arrays:N0} more than its arrays");
            Assert.IsGreaterThanOrEqualTo(arrays, budget.Charged,
                $"the read of the large form was charged {budget.Charged:N0} bytes, less than its arrays");
            Assert.IsLessThanOrEqualTo(arrays + Slack, budget.Charged,
                $"the read of the large form was charged {budget.Charged:N0} bytes, {budget.Charged - arrays:N0} more than its arrays");
        }
    }
}
