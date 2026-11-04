using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
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

        // ---- The check a reader runs on every table it restores ---------------------

        /// <summary>
        /// Packs metadata into a table of slots of the given width, by this test's own
        /// packing rather than the library's: slot i from bit i times the width, metadata
        /// in its low three bits, and a field of i + 1 above them in an entry, cut to the
        /// slot. Two spare words follow, as the library's tables have.
        /// </summary>
        private static ulong[] Pack(ulong[] metadata, int bitsPerSlot)
        {
            var words = new ulong[(((long)metadata.Length * bitsPerSlot) >> 6) + 2];
            var mask = bitsPerSlot >= 64 ? ulong.MaxValue : (1UL << bitsPerSlot) - 1;
            for (var i = 0; i < metadata.Length; i++)
            {
                var value = (metadata[i] == 0 ? 0 : metadata[i] | ((ulong)(i + 1) << 3)) & mask;
                for (var b = 0; b < bitsPerSlot; b++)
                {
                    if (((value >> b) & 1) != 0)
                    {
                        var bit = ((long)i * bitsPerSlot) + b;
                        words[bit >> 6] |= 1UL << (int)(bit & 63);
                    }
                }
            }
            return words;
        }

        /// <summary>The library's check on a packed table, as table 3 of a chain.</summary>
        private static void Check(ulong[] metadata, uint count, int bitsPerSlot = 7) =>
            QuotientTable.CheckMetadata(Pack(metadata, bitsPerSlot), bitsPerSlot, (uint)metadata.Length, count, 3);

        /// <summary>
        /// Asserts the check refuses a table, naming the table and the reason, so that a
        /// rule which stops firing is not covered for by another that happens to fire later.
        /// </summary>
        private static void AssertRefused(ulong[] metadata, uint count, string reason)
        {
            var ex = Assert.ThrowsExactly<InvalidDataException>(() => Check(metadata, count),
                $"[{string.Join(",", metadata)}], count {count}: not refused.");
            StringAssert.Contains(ex.Message, "Table 3's metadata is inconsistent");
            StringAssert.Contains(ex.Message, reason);
        }

        /// <summary>
        /// Whether <see cref="QuotientFamilyInvariants.DecodeTable"/>, which decodes a table
        /// with a queue of waiting quotients and none of the library's code, finds it
        /// consistent and holding <paramref name="count"/> entries.
        /// </summary>
        private static bool Consistent(uint slots, Func<uint, ulong> slot, uint count, bool sortedRuns, bool nonZeroFields)
        {
            try
            {
                return QuotientFamilyInvariants.DecodeTable("edited", slots, slot, sortedRuns, nonZeroFields).Count == count;
            }
            catch (AssertFailedException)
            {
                return false;
            }
        }

        private static readonly ulong[] FullOneRun = [Occupied, Continuation | Shifted, Continuation | Shifted, Continuation | Shifted];

        private static readonly ulong[] FullWrapped = [Continuation | Shifted, Continuation | Shifted, Continuation | Shifted, Occupied];

        /// <summary>
        /// Figure 2 passes the check at every slot width a table can have, holding its eight
        /// entries. The widths put its slots at every offset in a word, and some straddle
        /// two words: at 21 bits, slot 3's occupied bit is bit 63 of the first word and
        /// slot 6's shifted bit is bit 0 of the second.
        /// </summary>
        [TestMethod]
        public void TestTheMetadataCheckAcceptsThePapersFigure2AtEverySlotWidth()
        {
            Assert.IsTrue(Consistent(10, i => Figure2[i], 8, sortedRuns: false, nonZeroFields: false),
                "premise: the independent checker reads Figure 2 as consistent.");
            for (var bits = 4; bits <= 64; bits++)
            {
                Check(Figure2, 8, bits);
            }
        }

        /// <summary>
        /// Tables at the corners the check's walk has: a full table whose one cluster start is
        /// its first slot, a full one whose one start is its last slot and whose run wraps
        /// round to the first, an empty table, and tables of one slot, which a quotient
        /// filter payload from 6.2.0 or earlier can be.
        /// </summary>
        [TestMethod]
        public void TestTheMetadataCheckAcceptsTablesWhoseOnlyClusterStartIsAtEitherEnd()
        {
            Check(FullOneRun, 4);
            Check(FullWrapped, 4);
            Check([0, 0, 0, 0], 0);
            Check([Occupied], 1);
            Check([0], 0);
        }

        /// <summary>
        /// Every slot shifted: no slot is a cluster's start, and every walk back to one
        /// circles the table.
        /// </summary>
        [TestMethod]
        public void TestATableWithNoClusterStartIsRefused()
        {
            AssertRefused([Shifted, Shifted, Shifted, Shifted], 4, "no cluster in it has a start");
        }

        /// <summary>
        /// Quotient 2 is occupied and its run has not begun when slot 3 is empty. The shifted
        /// run at slot 4, after the empty slot, then takes it as an owner, so nothing later in
        /// the table catches the gap: only the empty slot does.
        /// </summary>
        [TestMethod]
        public void TestAnEmptySlotWhileAQuotientWaitsForItsRunIsRefused()
        {
            AssertRefused([0, Occupied, Occupied | Continuation | Shifted, 0, Shifted, 0, 0, 0], 3,
                "slot 3 is empty, and 1 of the occupied quotients before it");
        }

        /// <summary>
        /// A run begins at slot 1 marked shifted, and no occupied quotient is waiting for a run:
        /// it has no owner. Quotient 2 is occupied in the next slot and owns no run either, so
        /// a count of waiting quotients that let the first go below zero would come back to
        /// zero at the second, and nothing later would catch either.
        /// </summary>
        [TestMethod]
        public void TestAShiftedRunStartWithNoQuotientWaitingIsRefused()
        {
            AssertRefused([0, Shifted, Occupied | Continuation | Shifted, 0], 2,
                "slot 1 begins a run and is marked shifted, and no occupied quotient before it is waiting");
        }

        /// <summary>
        /// A run begins unshifted at slot 3, in its own slot, while quotient 2 is still waiting
        /// for its run. The shifted run at slot 4 then takes quotient 3, so the counts come out
        /// even and only the run start's shifted bit is wrong.
        /// </summary>
        [TestMethod]
        public void TestARunStartInItsOwnSlotWhileAQuotientWaitsIsRefused()
        {
            AssertRefused([0, Occupied, Occupied | Continuation | Shifted, Occupied, Shifted, 0, 0, 0], 4,
                "slot 3 begins a run in its own slot, and 1 of the occupied quotients before it");
        }

        /// <summary>
        /// A continuation straight after an empty slot continues nothing: at the start of the
        /// walk, and after a run that the empty slot ended.
        /// </summary>
        [TestMethod]
        public void TestAContinuationAfterAnEmptySlotIsRefused()
        {
            AssertRefused([0, Continuation | Shifted, 0, 0], 1, "slot 1 continues a run, and the slot before it holds none");
            AssertRefused([0, Occupied, 0, Continuation | Shifted], 2, "slot 3 continues a run, and the slot before it holds none");
        }

        /// <summary>
        /// A continuation is never in its quotient's own slot, since the run began before it,
        /// so it is always shifted.
        /// </summary>
        [TestMethod]
        public void TestAnUnshiftedContinuationIsRefused()
        {
            AssertRefused([0, Occupied, Continuation, 0], 2, "slot 2 continues a run and is not marked shifted");
        }

        /// <summary>
        /// Quotient 2 is occupied and no run is left for it when the walk has gone round: after
        /// an empty first slot, and in a full table whose first slot is its cluster's start.
        /// </summary>
        [TestMethod]
        public void TestAnOccupiedQuotientLeftWithoutARunIsRefused()
        {
            AssertRefused([0, Occupied, Occupied | Continuation | Shifted, Continuation | Shifted], 3, "1 of its occupied quotients own no run");
            AssertRefused([Occupied, Occupied | Continuation | Shifted, Continuation | Shifted, Continuation | Shifted], 4,
                "1 of its occupied quotients own no run");
        }

        /// <summary>
        /// The count a payload stores for a table must be the entries its metadata places. A
        /// count lower than the entries lets a full table take another entry, whose insert then
        /// looks for an empty slot for ever.
        /// </summary>
        [TestMethod]
        public void TestATableWhoseCountIsNotItsEntriesIsRefused()
        {
            AssertRefused(Figure2, 7, "counts 7 entries and its slots hold 8");
            AssertRefused(Figure2, 9, "counts 9 entries and its slots hold 8");
            AssertRefused(FullOneRun, 3, "counts 3 entries and its slots hold 4");
        }

        /// <summary>
        /// One bit flipped anywhere in the metadata of Figure 2, or of either full table, makes
        /// a table the independent checker refuses, and so does the library's check. At 21
        /// bits a slot, so that two of Figure 2's slots straddle a word.
        /// </summary>
        [TestMethod]
        public void TestEverySingleBitFlipOfConsistentMetadataIsRefused()
        {
            foreach (var (table, count) in new[] { (Figure2, 8u), (FullOneRun, 4u), (FullWrapped, 4u) })
            {
                for (var slot = 0; slot < table.Length; slot++)
                {
                    for (var bit = 0; bit < 3; bit++)
                    {
                        var edited = (ulong[])table.Clone();
                        edited[slot] ^= 1UL << bit;
                        var what = $"[{string.Join(",", table)}] slot {slot} bit {bit}";

                        Assert.IsFalse(Consistent((uint)edited.Length, i => edited[i], count, false, false),
                            $"{what}: premise, the independent checker finds the flip consistent.");
                        var ex = Assert.ThrowsExactly<InvalidDataException>(() => Check(edited, count, 21), $"{what}: not refused.");
                        StringAssert.Contains(ex.Message, "Table 3's metadata is inconsistent", what);
                    }
                }
            }
        }

        // ---- The readers ------------------------------------------------------------

        private const int PayloadStart = 14;

        private static void Reseal(byte[] bytes)
        {
            var crc = new Crc32();
            crc.Append(bytes.AsSpan(4, bytes.Length - 8));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), crc.GetCurrentHashAsUInt32());
        }

        /// <summary>One table of a payload: where its words start, its shape, and its count.</summary>
        private sealed record StoredTable(int Words, int CountAt, uint Slots, int BitsPerSlot, uint Count);

        /// <summary>A slot of a stored table, read from the payload's bytes bit by bit.</summary>
        private static ulong StoredSlot(byte[] bytes, StoredTable table, uint index)
        {
            var value = 0UL;
            var start = ((long)table.Words * 8) + ((long)index * table.BitsPerSlot);
            for (var b = 0; b < table.BitsPerSlot; b++)
            {
                var at = start + b;
                if (((bytes[at >> 3] >> (int)(at & 7)) & 1) != 0)
                {
                    value |= 1UL << b;
                }
            }
            return value;
        }

        private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));

        /// <summary>
        /// The tables of an Infini or Memento payload, from the first table's fields on: for
        /// each, address bits, fingerprint bits, count and word count, then the words. A
        /// Memento slot also holds the memento, whose width is a field of the filter.
        /// </summary>
        private static List<StoredTable> ChainTables(byte[] bytes, int first, int mementoBits)
        {
            var tables = new List<StoredTable>();
            var count = U32(bytes, first - 4);
            var at = first;
            for (var t = 0; t < count; t++)
            {
                var quotientBits = (int)U32(bytes, at);
                var fingerprintBits = (int)U32(bytes, at + 4);
                var words = (int)U32(bytes, at + 12);
                tables.Add(new StoredTable(at + 16, at + 8, 1u << quotientBits,
                    fingerprintBits + 1 + mementoBits + 3, U32(bytes, at + 8)));
                at += 16 + (words * 8);
            }
            Assert.AreEqual(bytes.Length - 4, at, "premise: the tables run to the checksum.");
            return tables;
        }

        /// <summary>
        /// Flips every metadata bit of every slot of a stored table, one at a time, reseals
        /// the checksum, and asks for each edited payload to be refused for the named table's
        /// metadata. Each edit is first held to the independent checker, which must find it
        /// inconsistent too, so the sweep refuses exactly what that checker does.
        /// </summary>
        private static void AssertEveryFlipRefused(
            string what, byte[] payload, StoredTable table, string name, bool sortedRuns, bool nonZeroFields,
            Func<byte[], object> read)
        {
            Assert.IsTrue(Consistent(table.Slots, i => StoredSlot(payload, table, i), table.Count, sortedRuns, nonZeroFields),
                $"{what}: premise, the unedited table is consistent.");
            Assert.IsNotNull(read(payload), $"{what}: the unedited payload does not read.");

            var refused = 0;
            for (var slot = 0u; slot < table.Slots; slot++)
            {
                for (var bit = 0; bit < 3; bit++)
                {
                    var edited = (byte[])payload.Clone();
                    var at = ((long)table.Words * 8) + ((long)slot * table.BitsPerSlot) + bit;
                    edited[at >> 3] ^= (byte)(1 << (int)(at & 7));
                    Reseal(edited);
                    var where = $"{what}: slot {slot} bit {bit}";

                    Assert.IsFalse(Consistent(table.Slots, i => StoredSlot(edited, table, i), table.Count, sortedRuns, nonZeroFields),
                        $"{where}: premise, the independent checker finds the flip consistent.");
                    var ex = Assert.ThrowsExactly<InvalidDataException>(() => read(edited), $"{where}: not refused.");
                    StringAssert.Contains(ex.Message, $"{name}'s metadata is inconsistent", where);
                    refused++;
                }
            }

            Assert.AreEqual(3 * (int)table.Slots, refused, $"{what}: vacuity, flips refused.");
        }

        /// <summary>
        /// Edits a stored table's count, and asks for the payload to be refused for it.
        /// </summary>
        private static void AssertCountRefused(string what, byte[] payload, StoredTable table, uint count, string name, Func<byte[], object> read)
        {
            var edited = (byte[])payload.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(edited.AsSpan(table.CountAt), count);
            Reseal(edited);
            var ex = Assert.ThrowsExactly<InvalidDataException>(() => read(edited), $"{what}: count {count} not refused.");
            StringAssert.Contains(ex.Message, $"{name}'s metadata is inconsistent", what);
            StringAssert.Contains(ex.Message, $"counts {count} entries and its slots hold {table.Count}", what);
        }

        /// <summary>
        /// A quotient filter whose hash is the key's two bytes as the top of the hash, so a key
        /// is its own fingerprint: quotient above the remainder.
        /// </summary>
        private static (QuotientFilter Filter, Func<ReadOnlySpan<byte>, ulong> Hash) QuotientFilterOf(
            uint n, double rate, int width, params int[] fingerprints)
        {
            Func<ReadOnlySpan<byte>, ulong> hash = k => (ulong)BinaryPrimitives.ReadUInt16LittleEndian(k) << (64 - width);
            var filter = new QuotientFilter(n, rate, hash);
            foreach (var fingerprint in fingerprints)
            {
                var key = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(key, (ushort)fingerprint);
                filter.Add(key);
            }
            return (filter, hash);
        }

        /// <summary>
        /// Figure 2's entries in a quotient filter of 16 slots and 4-bit remainders: a and b
        /// in quotient 1, c, d and e in 3, f in 4, g and h in 6, with remainders 1, 2, 1, 2, 3,
        /// 1, 1, 2.
        /// </summary>
        private static (QuotientFilter Filter, Func<ReadOnlySpan<byte>, ulong> Hash) QuotientFilterOfFigure2() =>
            QuotientFilterOf(10, 1.0 / 16, 8,
                (1 << 4) | 1, (1 << 4) | 2, (3 << 4) | 1, (3 << 4) | 2, (3 << 4) | 3, (4 << 4) | 1, (6 << 4) | 1, (6 << 4) | 2);

        /// <summary>The one table of a quotient filter payload: its words follow four u32s.</summary>
        private static StoredTable QuotientTableOf(byte[] payload) =>
            new(PayloadStart + 16, PayloadStart + 8, 1u << (int)U32(payload, PayloadStart),
                (int)U32(payload, PayloadStart + 4) + 3, U32(payload, PayloadStart + 8));

        /// <summary>
        /// The quotient filter lays Figure 2's entries out exactly as the figure does: its
        /// bits in slots 0 to 9, and each remainder in the slot the figure puts its letter
        /// in. The control for the payload sweeps below, and an anchor of the insert path to
        /// the paper.
        /// </summary>
        [TestMethod]
        public void TestTheQuotientFilterLaysOutFigure2AsThePaperDoes()
        {
            var (filter, _) = QuotientFilterOfFigure2();
            var payload = filter.ToByteArray();
            var table = QuotientTableOf(payload);
            Assert.AreEqual(16u, table.Slots, "premise: 16 slots.");

            var remainders = new ulong[] { 0, 1, 2, 1, 2, 3, 1, 1, 2, 0 };
            for (var slot = 0u; slot < table.Slots; slot++)
            {
                var value = StoredSlot(payload, table, slot);
                Assert.AreEqual(slot < 10 ? Figure2[slot] : 0, value & 7, $"slot {slot}'s bits.");
                Assert.AreEqual(slot < 10 ? remainders[slot] : 0, value >> 3, $"slot {slot}'s remainder.");
            }
        }

        /// <summary>
        /// Every metadata bit of a quotient filter's table, flipped in a payload with the
        /// checksum repaired, is refused when the payload is read: in Figure 2's table, in a
        /// full table of one run, and in a full table whose run wraps round the end, where
        /// one flip leaves no cluster start at all.
        /// </summary>
        [TestMethod]
        public void TestEveryMetadataBitFlippedInAQuotientFilterPayloadIsRefused()
        {
            var shapes = new[]
            {
                ("figure 2", QuotientFilterOfFigure2()),
                ("full, one run", QuotientFilterOf(3, 0.25, 4, 0, 1, 2, 3)),
                ("full, wrapped", QuotientFilterOf(3, 0.25, 4, 12, 13, 14, 15)),
            };

            foreach (var (what, (filter, hash)) in shapes)
            {
                var payload = filter.ToByteArray();
                AssertEveryFlipRefused($"quotient filter, {what}", payload, QuotientTableOf(payload), "The table",
                    sortedRuns: true, nonZeroFields: false, bytes => Persistence.FromByteArray<QuotientFilter>(bytes, hash));
            }
        }

        /// <summary>
        /// A quotient filter payload whose count is one more or one less than its table holds
        /// is refused. One less on a full table is the payload whose next Add would look for
        /// an empty slot for ever: the full check counted one slot still free. (One more on a
        /// full table is more than its slots, which was refused already.)
        /// </summary>
        [TestMethod]
        public void TestAQuotientFilterPayloadWhoseCountIsNotItsEntriesIsRefused()
        {
            var (figure, figureHash) = QuotientFilterOfFigure2();
            var payload = figure.ToByteArray();
            var table = QuotientTableOf(payload);
            Func<byte[], object> read = bytes => Persistence.FromByteArray<QuotientFilter>(bytes, figureHash);
            AssertCountRefused("quotient filter, figure 2", payload, table, table.Count - 1, "The table", read);
            AssertCountRefused("quotient filter, figure 2", payload, table, table.Count + 1, "The table", read);

            var (full, fullHash) = QuotientFilterOf(3, 0.25, 4, 0, 1, 2, 3);
            payload = full.ToByteArray();
            table = QuotientTableOf(payload);
            Assert.AreEqual(table.Slots, table.Count, "premise: the table is full.");
            AssertCountRefused("quotient filter, full", payload, table, table.Count - 1, "The table",
                bytes => Persistence.FromByteArray<QuotientFilter>(bytes, fullHash));
        }

        /// <summary>
        /// An InfiniFilter with two-bit fingerprints, filled until its entries have gone void
        /// and been shed into a second table.
        /// </summary>
        private static byte[] InfiniChain()
        {
            var filter = new InfiniFilter(initialCapacity: 8, fingerprintBits: 2);
            for (var i = 0; filter.Segments.Count < 2; i++)
            {
                Assert.IsLessThan(10_000, i, "premise: the chain never grew a second table.");
                filter.Add(Encoding.ASCII.GetBytes($"c10-{i}"));
            }
            return filter.ToByteArray();
        }

        /// <summary>
        /// Every metadata bit of every table of an InfiniFilter, flipped in a payload with the
        /// checksum repaired, is refused when it is read, naming the table: the reader checks
        /// each table, and not only the first.
        /// </summary>
        [TestMethod]
        public void TestEveryMetadataBitFlippedInAnInfiniFilterPayloadIsRefused()
        {
            var payload = InfiniChain();
            var tables = ChainTables(payload, PayloadStart + 16, 0);
            Assert.IsGreaterThanOrEqualTo(2, tables.Count, "premise: a chain of two tables or more.");

            for (var t = 0; t < tables.Count; t++)
            {
                AssertEveryFlipRefused($"infini table {t}", payload, tables[t], $"Table {t}",
                    sortedRuns: true, nonZeroFields: true, bytes => Persistence.FromByteArray<InfiniFilter>(bytes));
            }
        }

        /// <summary>
        /// An InfiniFilter payload with a table's count one more or one less than the table
        /// holds is refused, for every table of the chain.
        /// </summary>
        [TestMethod]
        public void TestAnInfiniFilterPayloadWhoseCountIsNotItsEntriesIsRefused()
        {
            var payload = InfiniChain();
            var tables = ChainTables(payload, PayloadStart + 16, 0);
            Func<byte[], object> read = bytes => Persistence.FromByteArray<InfiniFilter>(bytes);
            for (var t = 0; t < tables.Count; t++)
            {
                Assert.IsGreaterThan(0u, tables[t].Count, $"premise: table {t} holds an entry.");
                AssertCountRefused($"infini table {t}", payload, tables[t], tables[t].Count - 1, $"Table {t}", read);
                AssertCountRefused($"infini table {t}", payload, tables[t], tables[t].Count + 1, $"Table {t}", read);
            }
        }

        /// <summary>
        /// A MementoFilter with two-bit fingerprints and a range of 64, keys spread over many
        /// blocks, filled until a second table has been made.
        /// </summary>
        private static byte[] MementoChain()
        {
            var filter = new MementoFilter(64, 2, initialCapacity: 8);
            for (var i = 0UL; filter.Segments.Count < 2; i++)
            {
                Assert.IsLessThan(10_000UL, i, "premise: the chain never grew a second table.");
                filter.Add(i * 37);
            }
            return filter.ToByteArray();
        }

        /// <summary>
        /// Every metadata bit of every table of a MementoFilter, flipped in a payload with the
        /// checksum repaired, is refused when it is read, for the metadata and naming the
        /// table: the check runs before the reader decodes a run, which it does by walking
        /// the metadata.
        /// </summary>
        [TestMethod]
        public void TestEveryMetadataBitFlippedInAMementoFilterPayloadIsRefused()
        {
            var payload = MementoChain();
            var tables = ChainTables(payload, PayloadStart + 20, (int)U32(payload, PayloadStart + 12));
            Assert.IsGreaterThanOrEqualTo(2, tables.Count, "premise: a chain of two tables or more.");

            for (var t = 0; t < tables.Count; t++)
            {
                AssertEveryFlipRefused($"memento table {t}", payload, tables[t], $"Table {t}",
                    sortedRuns: false, nonZeroFields: false, bytes => Persistence.FromByteArray<MementoFilter>(bytes));
            }
        }

        /// <summary>
        /// A MementoFilter payload with a table's count of slots used one more or one less than
        /// the table uses is refused, for every table of the chain.
        /// </summary>
        [TestMethod]
        public void TestAMementoFilterPayloadWhoseCountIsNotItsEntriesIsRefused()
        {
            var payload = MementoChain();
            var tables = ChainTables(payload, PayloadStart + 20, (int)U32(payload, PayloadStart + 12));
            Func<byte[], object> read = bytes => Persistence.FromByteArray<MementoFilter>(bytes);
            for (var t = 0; t < tables.Count; t++)
            {
                Assert.IsGreaterThan(0u, tables[t].Count, $"premise: table {t} holds an entry.");
                AssertCountRefused($"memento table {t}", payload, tables[t], tables[t].Count - 1, $"Table {t}", read);
                AssertCountRefused($"memento table {t}", payload, tables[t], tables[t].Count + 1, $"Table {t}", read);
            }
        }
    }
}
