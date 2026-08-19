using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The metadata walks in the quotient-style filters are while-true loops whose
    /// termination rests on the three-bit invariants, and the fuse builder retries
    /// until construction succeeds. A defect in either does not fail -- it spins,
    /// or it quietly does table-sized work per operation while still answering
    /// every query correctly. Wall clocks cannot adjudicate that class: a timeout
    /// mostly measures the machine (see the Stryker table in TESTING.md), and this
    /// suite's machine sleeps mid-run. So these tests bound the *work*, counted
    /// deterministically, and leave the clock out of it.
    /// <para>
    /// Ceilings are probed-plus-slack on deterministic workloads; floors are
    /// vacuity guards proving the counted path actually ran. Two structures need
    /// no counters and are noted here rather than tested: the Vale counter walks
    /// are all for-loops bounded by the pool size (no while loop exists in either
    /// file), and HLL++'s sigma/tau iterations converge by floating-point
    /// fixed-point, where the only reachable divergence input (x = 1) is guarded
    /// and the guard is pinned by the estimator anchors. Memento and Infini
    /// segment counters reset when expansion rebuilds a segment, so their tests
    /// measure expansion-free spans only.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestBoundedWork
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        /// <summary>
        /// The quotient filter's work must be cluster-sized, not table-sized. At
        /// 10,000 keys in 16,384 slots the probed costs are 4.0 slot reads per
        /// add, 5.9 per lookup, and 64.6 per remove -- the remove rebuilds its
        /// whole cluster through the ordinary insert path, which is where the
        /// quadratic lives and why its ceiling is an order looser. The ceilings
        /// are probed plus half; a walk that restarts, re-scans, or wanders past
        /// its cluster shows up here while every answer stays right.
        /// </summary>
        [TestMethod]
        public void TestQuotientFilterWorkIsClusterSizedNotTableSized()
        {
            var filter = new QuotientFilter(10000, 0.01);

            for (int i = 0; i < 10000; i++)
            {
                filter.Add(Key($"in-{i}"));
            }
            var afterFill = filter.SlotReads;

            for (int i = 0; i < 5000; i++)
            {
                filter.Test(Key(i % 2 == 0 ? $"in-{i}" : $"out-{i}"));
            }
            var afterLookups = filter.SlotReads;

            for (int i = 0; i < 2000; i++)
            {
                filter.TestAndRemove(Key($"in-{i}"));
            }
            var afterRemoves = filter.SlotReads;

            var fill = afterFill / 10000.0;
            var lookup = (afterLookups - afterFill) / 5000.0;
            var remove = (afterRemoves - afterLookups) / 2000.0;
            Console.WriteLine($"reads/op: fill={fill:F1} lookup={lookup:F1} remove={remove:F1}");

            Assert.IsGreaterThanOrEqualTo(2.0, fill,
                "adds are reading almost nothing, so the counter is not counting " +
                "and every ceiling below is asserted on air.");
            Assert.IsLessThanOrEqualTo(6.0, fill,
                $"an add costs {fill:F1} slot reads against a probed 4.0; the " +
                "insert walk is doing more than its run's worth of work.");

            Assert.IsGreaterThanOrEqualTo(2.0, lookup, "lookups read almost nothing.");
            Assert.IsLessThanOrEqualTo(9.0, lookup,
                $"a lookup costs {lookup:F1} slot reads against a probed 5.9; a " +
                "scan that runs past its cluster answers correctly and pays here.");

            Assert.IsGreaterThanOrEqualTo(5.0, remove,
                "removes are not paying for their cluster rebuild, so the rebuild " +
                "path did not run and this measures nothing.");
            Assert.IsLessThanOrEqualTo(100.0, remove,
                $"a remove costs {remove:F1} slot reads against a probed 64.6.");
        }

        /// <summary>
        /// A cuckoo filter refuses an insert by running out of kicks, and the
        /// budget is the paper's: kMaxCuckooCount = 500 in the authors' reference
        /// implementation (efficient/cuckoofilter, verified against the source
        /// 2026-08-18; the reference parks the last victim in a cache where this
        /// implementation undoes the displacement trail instead, a difference the
        /// source documents). The refusing insert must spend exactly its budget --
        /// failure by count, not by clock -- and the eviction machinery must have
        /// actually engaged before it, or the refusal being tested is an empty
        /// table refusing nothing.
        /// </summary>
        [TestMethod]
        public void TestCuckooRefusalArrivesByCountNotByClock()
        {
            Assert.AreEqual(500, CuckooBloomFilter.MaxKicks,
                "the eviction budget must be the paper's 500; a smaller budget " +
                "refuses streams the filter was sized for, and nothing else in " +
                "the suite measures the constant itself.");

            var filter = new CuckooBloomFilter(64, 0.01);
            long kicksOnRefusal = -1;
            int added = 0;

            for (int i = 0; i < 100000; i++)
            {
                var before = filter.KickSteps;
                if (!filter.Add(Key($"k-{i}")))
                {
                    kicksOnRefusal = filter.KickSteps - before;
                    break;
                }
                added++;
            }

            Console.WriteLine($"added={added} kicksOnRefusal={kicksOnRefusal}");

            Assert.IsGreaterThanOrEqualTo(64, added,
                "the filter refused before reaching its own capacity, so the " +
                "refusal below is not the one overloading is meant to produce.");
            Assert.AreEqual((long)CuckooBloomFilter.MaxKicks, kicksOnRefusal,
                "a refused insert must have spent exactly the kick budget: fewer " +
                "means it gave up early, more means the bound is not the bound.");
        }

        /// <summary>
        /// The fuse builder must succeed on its first attempt for ordinary sets --
        /// the 1.125 sizing factor exists to make retries rare, so attempts
        /// creeping past one is the sizing quietly failing -- and duplicate keys
        /// must not send it retrying at all: HashDistinctly collapses them before
        /// construction, and without it two identical keys make peeling impossible
        /// and every one of the 100 attempts fails. That failure mode is the
        /// bounded-work discipline in one line: with the guard gone this test dies
        /// at MaxBuildAttempts, by count, instead of hanging a mutation run.
        /// </summary>
        [TestMethod]
        public void TestFuseBuildsFirstTryAndAbsorbsDuplicates()
        {
            foreach (var n in new uint[] { 100, 10000, 1000000 })
            {
                var keys = new List<byte[]>();
                for (uint i = 0; i < n; i++)
                {
                    keys.Add(Key($"f-{i}"));
                }
                var filter = BinaryFuseFilter.Build(keys);
                Assert.AreEqual(1, filter.AttemptsUsed,
                    $"n={n}: construction took {filter.AttemptsUsed} attempts; at " +
                    "the reference sizing a retry on a clean set is the sizing " +
                    "arithmetic quietly wrong.");
            }

            var duplicated = BinaryFuseFilter.Build(new List<byte[]>
            {
                Key("a"), Key("a"), Key("b"),
            });
            Assert.AreEqual(1, duplicated.AttemptsUsed,
                "duplicates must be collapsed before construction, not fought " +
                "through retries.");
            Assert.IsTrue(duplicated.Test(Key("a")),
                "the duplicated key must still be present after deduplication.");
            Assert.IsTrue(duplicated.Test(Key("b")),
                "the singleton key must survive alongside the collapsed pair.");
        }

        /// <summary>
        /// What a Memento add costs, measured instead of assumed. Every add
        /// rewrites its whole cluster through the ordinary insert path -- the
        /// source documents the choice -- so the price is cluster-shaped: probed
        /// at 44.7 slot reads per add at load 0.11, and 2,269 at load just under
        /// the 0.75 expansion threshold, where the filter spends its working life.
        /// Both regimes are pinned; the near-threshold floor doubles as the proof
        /// that the expensive regime was actually reached. Infini, which inserts
        /// without rebuilding, is pinned lean at its probed 1.5.
        /// </summary>
        [TestMethod]
        public void TestMementoAddPriceIsTheClusterRebuild()
        {
            var roomy = new MementoFilter(256, 8, 16384);
            for (ulong i = 0; i < 5000; i++)
            {
                roomy.Add(i * 37);
            }
            Assert.HasCount(1, roomy.Segments, "a roomy filter must not expand.");
            var perAdd = roomy.Segments[0].SlotReads / 5000.0;
            Console.WriteLine($"roomy: load={roomy.Segments[0].Load:F3} perAdd={perAdd:F1}");
            Assert.IsGreaterThanOrEqualTo(10.0, perAdd,
                "adds this cheap mean the rebuild path did not run.");
            Assert.IsLessThanOrEqualTo(90.0, perAdd,
                $"an add costs {perAdd:F1} reads at low load against a probed 44.7.");

            var tight = new MementoFilter(256, 8, 1024);
            ulong key = 0;
            while (tight.Segments[0].Load < 0.70)
            {
                tight.Add(key * 37);
                key++;
            }
            var baseline = tight.Segments[0].SlotReads;
            for (int i = 0; i < 50; i++)
            {
                tight.Add(key * 37);
                key++;
            }
            Assert.HasCount(1, tight.Segments,
                "the measured span crossed an expansion, which resets the counter " +
                "and voids the measurement; lower the span.");
            var nearFull = (tight.Segments[0].SlotReads - baseline) / 50.0;
            Console.WriteLine($"near threshold: load={tight.Segments[0].Load:F3} perAdd={nearFull:F1}");
            Assert.IsGreaterThanOrEqualTo(100.0, nearFull,
                "near the expansion threshold an add must pay for a long cluster; " +
                "a cost this low means the near-full regime was never reached.");
            Assert.IsLessThanOrEqualTo(6000.0, nearFull,
                $"an add costs {nearFull:F1} reads near threshold against a " +
                "probed 2,269; past this the rebuild is doing more than one " +
                "cluster's worth of work.");

            var infini = new InfiniFilter(65536, 8);
            for (int i = 0; i < 20000; i++)
            {
                infini.Add(Key($"i-{i}"));
            }
            Assert.AreEqual(0u, infini.Expansions, "the roomy Infini must not expand.");
            var infiniPerAdd = infini.Segments.Sum(s => s.SlotReads) / 20000.0;
            Console.WriteLine($"infini perAdd={infiniPerAdd:F1}");
            Assert.IsGreaterThanOrEqualTo(1.0, infiniPerAdd, "the counter counted nothing.");
            Assert.IsLessThanOrEqualTo(6.0, infiniPerAdd,
                $"an Infini add costs {infiniPerAdd:F1} reads against a probed 1.5; " +
                "its insert does not rebuild clusters and must not start to.");
        }
    }
}
