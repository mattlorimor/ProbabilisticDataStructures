using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The metadata of the quotient-family tables, and what the code that walks it does
    /// when it is inconsistent.
    /// </summary>
    /// <remarks>
    /// The quotient filter and the Infini and Memento tables find a quotient's run as the
    /// paper does: walk back while the slot is shifted, then forward, pairing each
    /// occupied quotient with the next run. Those loops end only because the three bits
    /// agree with each other. On a table where they do not, the search walks round the
    /// table for ever, and so does a search asked for a quotient no run belongs to.
    /// <para>
    /// External anchor: Bender et al., "Don't Thrash: How to Cache Your Hash on Flash",
    /// PVLDB 5(11), 2012. Section 3 (p. 1630) defines is_occupied, is_continuation and
    /// is_shifted, keeps runs in the order of their quotients, and prints Figure 2, a
    /// ten-slot table with its three bits per slot; Figure 3 (p. 1631) is May-Contain, the
    /// walk the run-start searches follow. Read from vldb.org on 2026-11-03.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestQuotientTableMetadata
    {
        private const ulong Occupied = 1;
        private const ulong Continuation = 2;
        private const ulong Shifted = 4;

        /// <summary>
        /// Figure 2's bits, slot by slot, in this library's order: occupied the low bit,
        /// then continuation, then shifted. The figure prints them in that order, as
        /// 000 100 011 100 111 011 101 001 011 000, for the entries a and b of quotient 1,
        /// c, d and e of quotient 3, f of 4, and g and h of 6, in slots 1 to 8.
        /// </summary>
        private static readonly ulong[] Figure2 =
        [
            0,
            Occupied,
            Continuation | Shifted,
            Occupied,
            Occupied | Continuation | Shifted,
            Continuation | Shifted,
            Occupied | Shifted,
            Shifted,
            Continuation | Shifted,
            0,
        ];

        /// <summary>
        /// One table of 16 slots of each kind, reached through the library's own slot
        /// writer and run-start search.
        /// </summary>
        private sealed record Table(
            string Kind, uint Slots, Action<uint, ulong> Write, Func<uint, uint> RunStart, Func<long> Reads);

        private static Table Make(string kind)
        {
            Table table;
            switch (kind)
            {
                case "quotient":
                    var filter = new QuotientFilter(10, 0.01);
                    table = new Table(kind, filter.SlotCount, filter.WriteSlot, filter.FindRunStart, () => filter.SlotReads);
                    break;
                case "infini":
                    var infini = new InfiniSegment(4, 8);
                    table = new Table(kind, infini.Slots, infini.WriteSlot, infini.FindRunStart, () => infini.SlotReads);
                    break;
                case "memento":
                    var memento = new MementoSegment(4, 8, 6);
                    table = new Table(kind, memento.Slots, memento.WriteSlot, memento.FindRunStart, () => memento.SlotReads);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }

            Assert.AreEqual(16u, table.Slots, $"{kind}: premise, a table of 16 slots.");
            return table;
        }

        /// <summary>
        /// Writes the given metadata into the first slots of a table, every other slot
        /// empty, each entry with a field of its slot number plus one. The fields are
        /// nothing to a run-start search; they are there so that no entry is all zeros.
        /// </summary>
        private static void Lay(Table table, ulong[] metadata)
        {
            for (var slot = 0u; slot < table.Slots; slot++)
            {
                var bits = slot < metadata.Length ? metadata[slot] : 0;
                table.Write(slot, bits == 0 ? 0 : bits | ((slot + 1UL) << 3));
            }
        }

        /// <summary>
        /// Runs a walk on a thread of its own and returns what it threw and the slot reads
        /// it made, failing by count if it is still walking once its table has read
        /// <paramref name="budget"/> slots. A walk that goes on goes on reading slots, so
        /// the count is what bounds this test, and no clock does. A walk that spins leaves
        /// its thread spinning, since a thread cannot be stopped from outside; it is a
        /// background thread, and goes when the test run does.
        /// </summary>
        private static (Exception Thrown, long Reads) RunBounded(Func<long> reads, long budget, Action walk)
        {
            var before = reads();
            var run = new BoundedRun(walk);
            var thread = new Thread(run.Go) { IsBackground = true };
            thread.Start();

            while (!run.Finished)
            {
                var spent = reads() - before;
                if (spent > budget)
                {
                    Assert.Fail($"The walk was still going after {spent:N0} slot reads, past a " +
                        $"budget of {budget:N0}: it is circling the table.");
                }
                Thread.Yield();
            }

            return (run.Thrown, reads() - before);
        }

        private sealed class BoundedRun(Action walk)
        {
            private volatile bool finished;

            internal bool Finished => this.finished;

            internal Exception Thrown { get; private set; }

            internal void Go()
            {
                try
                {
                    walk();
                }
                catch (Exception e)
                {
                    this.Thrown = e;
                }
                finally
                {
                    this.finished = true;
                }
            }
        }

        /// <summary>
        /// The run-start search finds the runs Figure 2 shows, in each kind of table: the
        /// figure's ten slots, then six empty ones. Quotient 1's run begins at its own slot,
        /// as does 3's; 4's run, f, has been pushed to slot 6 by d and e; and 6's, g, to
        /// slot 7 by f. This is the control for the lap tests below: a bound that fired on
        /// a table the paper prints would fail here.
        /// </summary>
        [TestMethod]
        [DataRow("quotient")]
        [DataRow("infini")]
        [DataRow("memento")]
        public void TestTheRunStartSearchFindsTheRunsOfThePapersFigure2(string kind)
        {
            var table = Make(kind);
            Lay(table, Figure2);

            Assert.AreEqual(1u, table.RunStart(1), $"{kind}: quotient 1, a.");
            Assert.AreEqual(3u, table.RunStart(3), $"{kind}: quotient 3, c.");
            Assert.AreEqual(6u, table.RunStart(4), $"{kind}: quotient 4, f.");
            Assert.AreEqual(7u, table.RunStart(6), $"{kind}: quotient 6, g.");
        }

        /// <summary>
        /// Each of the run-start search's three walks stops after one lap of the table and
        /// throws, where it used to circle the table for ever.
        /// <list type="bullet">
        /// <item><c>back</c>: every slot is shifted, so the walk back to a cluster's start
        /// never finds one.</item>
        /// <item><c>run</c>: every slot is a continuation, slot 0 included, so the walk past a
        /// run never reaches the next run.</item>
        /// <item><c>bucket</c>: Figure 2, which is consistent, asked for quotient 2, which is
        /// inside the cluster and owns no run: the walk forward through the occupied
        /// quotients goes past 2 without stopping. This is what the merge's entry walk did
        /// on #218's surviving mutant, which skipped the occupancy test.</item>
        /// </list>
        /// Each throws within a lap of each walk it makes: the reads are held to three laps,
        /// so a walk that went on until some other walk's bound caught it fails here too.
        /// Measured, the same in each kind: 16 slot reads for back, 17 for run and 25 for
        /// bucket, of the 48 allowed.
        /// </summary>
        [TestMethod]
        [DataRow("quotient", "back")]
        [DataRow("quotient", "run")]
        [DataRow("quotient", "bucket")]
        [DataRow("infini", "back")]
        [DataRow("infini", "run")]
        [DataRow("infini", "bucket")]
        [DataRow("memento", "back")]
        [DataRow("memento", "run")]
        [DataRow("memento", "bucket")]
        public void TestTheRunStartSearchStopsAfterOneLapOfTheTable(string kind, string walk)
        {
            var table = Make(kind);
            uint quotient;
            var metadata = new ulong[16];
            switch (walk)
            {
                case "back":
                    Array.Fill(metadata, Shifted);
                    Lay(table, metadata);
                    quotient = 5;
                    break;
                case "run":
                    Array.Fill(metadata, Continuation | Shifted);
                    metadata[0] = Occupied | Continuation;
                    metadata[1] = Occupied | Continuation | Shifted;
                    Lay(table, metadata);
                    quotient = 1;
                    break;
                default:
                    Lay(table, Figure2);
                    quotient = 2;
                    break;
            }

            var (thrown, reads) = RunBounded(table.Reads, 64 * table.Slots, () => table.RunStart(quotient));
            Console.WriteLine($"{kind} {walk}: {reads} slot reads, then {thrown?.GetType().Name}");

            Assert.IsInstanceOfType<InvalidOperationException>(thrown,
                $"{kind} {walk}: the search returned, or threw something else: {thrown}");
            StringAssert.Contains(thrown.Message, $"quotient {quotient}", $"{kind} {walk}");
            StringAssert.Contains(thrown.Message, "one lap", $"{kind} {walk}");
            Assert.IsLessThanOrEqualTo(3 * (long)table.Slots, reads,
                $"{kind} {walk}: the search read more than three laps of slots before it stopped.");
        }
    }
}
