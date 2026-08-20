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
    /// Round-trips every structure, asserted by behavior rather than by comparing
    /// fields. A restored structure has to answer every query the way the original did,
    /// including the ones where the answer is a false positive: a structure that
    /// disagrees there is a different structure, even where the answer is allowed to be
    /// yes.
    /// </summary>
    [TestClass]
    public class TestPersistenceAllStructures
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        private static readonly string[] Present =
            Enumerable.Range(0, 1000).Select(i => $"present-{i}").ToArray();

        private static readonly string[] Absent =
            Enumerable.Range(0, 2000).Select(i => $"absent-{i}").ToArray();

        private static T RoundTrip<T>(T structure) where T : IBinaryPersistable<T>
        {
            return Persistence.FromByteArray<T>(structure.ToByteArray());
        }

        /// <summary>
        /// Asserts a restored filter agrees with the original everywhere, given a way to
        /// query each.
        /// </summary>
        private static void AssertAgreesEverywhere(
            string name, Func<byte[], bool> original, Func<byte[], bool> restored)
        {
            foreach (var word in Present.Concat(Absent))
            {
                Assert.AreEqual(original(Key(word)), restored(Key(word)),
                    $"{name} disagreed about {word} after being restored");
            }
        }

        /// <summary>
        /// Asserts a sweep touched every structure the format knows about.
        /// </summary>
        /// <remarks>
        /// Every roster in this file is written by hand, and a hand-written roster
        /// agrees with itself: it is named "every structure" and means "every structure
        /// whoever last edited it remembered". That is not a hypothetical -- when this
        /// check was first written, all four sweeps below were missing the structures
        /// added most recently, and one of them was asserting that no two structures
        /// share an id across a roster three structures short of the full set.
        /// <para>
        /// <see cref="StructureId"/> is the roster a structure cannot leave itself off
        /// of, because a structure that is not in it cannot be persisted at all. A new
        /// structure therefore fails this until it is either covered or exempted.
        /// </para>
        /// </remarks>
        /// <param name="sweep">Named in the failure, since four sweeps share this.</param>
        /// <param name="covered">The structures the sweep exercised.</param>
        /// <param name="exempt">
        /// Structures the sweep cannot apply to, each with its reason. An exemption is
        /// a claim about the structure rather than a way to quieten this, so it is
        /// checked in turn: one naming a structure the sweep does cover has gone stale
        /// and fails just as loudly as a gap.
        /// </param>
        private static void AssertSweepCoversEveryStructure(
            string sweep,
            IEnumerable<StructureId> covered,
            params (StructureId Id, string Why)[] exempt)
        {
            var seen = covered.ToHashSet();
            var excused = exempt.Select(e => e.Id).ToHashSet();

            Assert.AreEqual(exempt.Length, excused.Count,
                $"the {sweep} sweep exempts a structure twice");

            foreach (var (id, why) in exempt)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(why),
                    $"the {sweep} sweep exempts {id} without saying why");
                Assert.IsFalse(seen.Contains(id),
                    $"the {sweep} sweep exempts {id} and then covers it anyway; " +
                    "the reason given is out of date");
            }

            var missing = Enum.GetValues<StructureId>()
                .Where(id => !seen.Contains(id) && !excused.Contains(id))
                .ToArray();

            Assert.IsEmpty(missing,
                $"the {sweep} sweep is named for every structure but does not reach " +
                string.Join(", ", missing) +
                ". Cover it, or exempt it here with the reason it cannot apply.");
        }

        [TestMethod]
        public void TestBloomFilter64RoundTrips()
        {
            var f = new BloomFilter64(1000, 0.01);
            foreach (var w in Present) f.Add(Key(w));

            var r = RoundTrip(f);

            Assert.AreEqual(f.Capacity(), r.Capacity());
            Assert.AreEqual(f.K(), r.K());
            Assert.AreEqual(f.Count(), r.Count());
            AssertAgreesEverywhere("BloomFilter64", f.Test, r.Test);
        }

        [TestMethod]
        public void TestCountingBloomFilterRoundTrips()
        {
            var f = new CountingBloomFilter(1000, 4, 0.01);
            foreach (var w in Present) f.Add(Key(w));

            var r = RoundTrip(f);

            Assert.AreEqual(f.Capacity(), r.Capacity());
            Assert.AreEqual(f.Count(), r.Count());
            AssertAgreesEverywhere("CountingBloomFilter", f.Test, r.Test);

            // The counters have to come back too, not just the occupancy: a restored
            // filter must remove what the original could remove, and no more.
            foreach (var w in Present.Take(100))
            {
                Assert.AreEqual(f.TestAndRemove(Key(w)), r.TestAndRemove(Key(w)),
                    $"restored filter removed {w} differently");
            }

            AssertAgreesEverywhere("CountingBloomFilter after removals", f.Test, r.Test);
        }

        [TestMethod]
        public void TestDeletableBloomFilterRoundTrips()
        {
            var f = new DeletableBloomFilter(1000, 10, 0.01);
            foreach (var w in Present) f.Add(Key(w));

            var r = RoundTrip(f);

            Assert.AreEqual(f.Capacity(), r.Capacity());
            Assert.AreEqual(f.Count(), r.Count());
            AssertAgreesEverywhere("DeletableBloomFilter", f.Test, r.Test);

            // The collision regions decide which bits a removal may clear, so a restored
            // filter that lost them would delete more than the original does.
            foreach (var w in Present.Take(100))
            {
                Assert.AreEqual(f.TestAndRemove(Key(w)), r.TestAndRemove(Key(w)),
                    $"restored filter removed {w} differently");
            }

            AssertAgreesEverywhere("DeletableBloomFilter after removals", f.Test, r.Test);
        }

        [TestMethod]
        public void TestPartitionedBloomFilterRoundTrips()
        {
            var f = new PartitionedBloomFilter(1000, 0.01);
            foreach (var w in Present) f.Add(Key(w));

            var r = RoundTrip(f);

            Assert.AreEqual(f.Capacity(), r.Capacity());
            Assert.AreEqual(f.K(), r.K());
            Assert.AreEqual(f.Count(), r.Count());
            Assert.AreEqual(f.FillRatio(), r.FillRatio());
            AssertAgreesEverywhere("PartitionedBloomFilter", f.Test, r.Test);
        }

        [TestMethod]
        public void TestStableBloomFilterRoundTrips()
        {
            var f = new StableBloomFilter(10000, 2, 0.01);
            foreach (var w in Present) f.Add(Key(w));

            var r = RoundTrip(f);

            Assert.AreEqual(f.Cells(), r.Cells());
            Assert.AreEqual(f.K(), r.K());
            Assert.AreEqual(f.P(), r.P());
            Assert.AreEqual(f.StablePoint(), r.StablePoint());
            AssertAgreesEverywhere("StableBloomFilter", f.Test, r.Test);
        }

        [TestMethod]
        public void TestInverseBloomFilterRoundTrips()
        {
            var f = new InverseBloomFilter(500);
            foreach (var w in Present) f.Add(Key(w));

            var r = RoundTrip(f);

            Assert.AreEqual(f.Capacity(), r.Capacity());

            // This filter stores the data itself, so the restored one has to hold the
            // same bytes in the same slots, not merely the same number of them.
            AssertAgreesEverywhere("InverseBloomFilter", f.Test, r.Test);

            // At least something survived, or the assertion above would pass on a filter
            // that came back empty.
            Assert.IsGreaterThan(0, Present.Count(w => r.Test(Key(w))),
                "the restored filter holds nothing at all");
        }

        [TestMethod]
        public void TestCuckooBloomFilterRoundTrips()
        {
            var f = new CuckooBloomFilter(1000, 0.01);
            foreach (var w in Present) f.Add(Key(w));

            var r = RoundTrip(f);

            Assert.AreEqual(f.Capacity(), r.Capacity());
            Assert.AreEqual(f.Count(), r.Count());
            AssertAgreesEverywhere("CuckooBloomFilter", f.Test, r.Test);

            // A restored cuckoo filter has to keep taking inserts, which means its
            // fingerprints landed where the relocation logic expects to find them.
            foreach (var w in Enumerable.Range(0, 200).Select(i => $"later-{i}"))
            {
                r.Add(Key(w));
                Assert.IsTrue(r.Test(Key(w)), $"restored filter lost {w} on insert");
            }
        }

        [TestMethod]
        public void TestHyperLogLogRoundTrips()
        {
            var h = new HyperLogLog(1024);
            foreach (var w in Present) h.Add(Key(w));

            var r = RoundTrip(h);

            Assert.AreEqual(h.Count(), r.Count(), "restored estimator gave a different count");

            // And keeps estimating, rather than merely reporting the stored number.
            foreach (var w in Absent) { h.Add(Key(w)); r.Add(Key(w)); }
            Assert.AreEqual(h.Count(), r.Count(), "restored estimator diverged after more adds");
        }

        [TestMethod]
        public void TestScalableBloomFilterRoundTrips()
        {
            // Hinted small against the load, so several filters are added and the
            // nesting is exercised rather than assumed.
            var f = new ScalableBloomFilter(100, 0.01, 0.8);
            foreach (var w in Present) f.Add(Key(w));

            var r = RoundTrip(f);

            Assert.AreEqual(f.Capacity(), r.Capacity());
            Assert.AreEqual(f.K(), r.K());
            Assert.AreEqual(f.FillRatio(), r.FillRatio());
            AssertAgreesEverywhere("ScalableBloomFilter", f.Test, r.Test);

            // It has to keep growing, which means the contained filters came back in
            // order with their fill ratios intact.
            foreach (var w in Enumerable.Range(0, 2000).Select(i => $"later-{i}"))
            {
                f.Add(Key(w));
                r.Add(Key(w));
            }

            Assert.AreEqual(f.Capacity(), r.Capacity(),
                "restored filter grew differently from the original");
            AssertAgreesEverywhere("ScalableBloomFilter after growth", f.Test, r.Test);
        }

        [TestMethod]
        public void TestTupleSketchRoundTrips()
        {
            var f = new TupleSketch(512, SummaryPolicy.Max);
            for (var i = 0; i < 20_000; i++) f.Add(Key($"item-{i}"), i + 1);

            var r = RoundTrip(f);

            Assert.AreEqual(f.Count(), r.Count());
            Assert.AreEqual(f.Total(), r.Total());
            Assert.AreEqual(f.Retained(), r.Retained());
            Assert.AreEqual(f.Policy, r.Policy);
        }

        [TestMethod]
        public void TestPrivateCountMinSketchRoundTrips()
        {
            var f = new PrivateCountMinSketch(128, 4, 0.5, seed: 8);
            for (var i = 0; i < 5_000; i++) f.Add(Key($"item-{i % 200}"));

            var r = RoundTrip(f);

            Assert.AreEqual(f.Width, r.Width);
            Assert.AreEqual(f.Depth, r.Depth);
            Assert.AreEqual(f.Rho, r.Rho);
            Assert.AreEqual(f.TotalCount(), r.TotalCount());
            for (var i = 0; i < 200; i++)
            {
                Assert.AreEqual(f.Count(Key($"item-{i}")), r.Count(Key($"item-{i}")));
            }
        }

        [TestMethod]
        public void TestDpswSketchRoundTrips()
        {
            var f = new DpswSketch(
                window: 400, rho: 1.0, alpha: 0.5, width: 32, depth: 3, seed: 8);
            for (var i = 0; i < 700; i++) f.Add(Key($"item-{i % 40}"));

            var r = RoundTrip(f);

            Assert.AreEqual(f.Window, r.Window);
            Assert.AreEqual(f.Rho, r.Rho);
            Assert.AreEqual(f.Position, r.Position);
            Assert.AreEqual(f.SketchesHeld, r.SketchesHeld);
            for (var i = 0; i < 40; i++)
            {
                Assert.AreEqual(f.Count(Key($"item-{i}")), r.Count(Key($"item-{i}")), 1e-9);
            }
        }

        [TestMethod]
        public void TestSetSketchRoundTrips()
        {
            var f = new SetSketch(256, 1.05, 20, 30_000);
            for (var i = 0; i < 20_000; i++) f.Add(Key($"item-{i}"));

            var r = RoundTrip(f);

            Assert.AreEqual(f.Registers, r.Registers);
            Assert.AreEqual(f.Base, r.Base);
            Assert.AreEqual(f.Cardinality(), r.Cardinality());
            Assert.AreEqual(1.0, f.Jaccard(r), "restored sketch is not the same set");
        }

        [TestMethod]
        public void TestSublimeCountMinSketchRoundTrips()
        {
            var f = new SublimeCountMinSketch(0.01);
            for (var i = 0; i < 30000; i++) f.Add(Key($"item-{i % 4000}"));

            var r = RoundTrip(f);

            Assert.AreEqual(f.Width, r.Width);
            Assert.AreEqual(f.Depth, r.Depth);
            Assert.AreEqual(f.TotalCount(), r.TotalCount());
            for (var i = 0; i < 4000; i++)
            {
                Assert.AreEqual(f.Count(Key($"item-{i}")), r.Count(Key($"item-{i}")),
                    $"restored sketch disagreed about item-{i}");
            }
        }

        [TestMethod]
        public void TestMementoFilterRoundTrips()
        {
            var f = new MementoFilter(256, 8, 1024);
            for (ulong i = 0; i < 30000; i++) f.Add(i * 13);

            var r = RoundTrip(f);

            Assert.AreEqual(f.Count(), r.Count());
            Assert.AreEqual(f.MaxRangeSize, r.MaxRangeSize);
            for (ulong i = 0; i < 30000; i++)
            {
                Assert.IsTrue(r.Test(i * 13), $"restored filter lost {i * 13}");
            }
            for (ulong i = 0; i < 20000; i++)
            {
                Assert.AreEqual(f.TestRange(i * 7, (i * 7) + 40),
                    r.TestRange(i * 7, (i * 7) + 40),
                    $"restored filter disagreed about a range near {i * 7}");
            }
        }

        [TestMethod]
        public void TestInfiniFilterRoundTrips()
        {
            var f = new InfiniFilter(initialCapacity: 64, fingerprintBits: 8);
            foreach (var w in Present) f.Add(Key(w));

            var r = RoundTrip(f);

            Assert.AreEqual(f.Count(), r.Count());
            Assert.AreEqual(f.ChainLength(), r.ChainLength());
            AssertAgreesEverywhere("InfiniFilter", f.Test, r.Test);

            // And keeps growing, which is the thing this filter does that the others
            // do not: a restored one has to expand the way the original would.
            foreach (var w in Absent) { f.Add(Key(w)); r.Add(Key(w)); }
            CollectionAssert.AreEqual(f.ToByteArray(), r.ToByteArray(),
                "restored filter diverged once both were grown further");
        }

        [TestMethod]
        public void TestGrafiteRoundTrips()
        {
            var rand = new Random(19);
            var keys = new SortedSet<ulong>();
            while (keys.Count < 20000) keys.Add((ulong)rand.NextInt64(0, 1_000_000_000));

            var filter = Grafite.Build(keys, 0.01, 64, seed: 23);
            var restored = RoundTrip(filter);

            Assert.AreEqual(filter.Count(), restored.Count());

            // Answers, not fields: a restored filter has to agree everywhere,
            // including where the answer is a false positive.
            foreach (var key in keys)
            {
                Assert.IsTrue(restored.Test(key), "restored filter lost a key");
            }
            for (int i = 0; i < 20000; i++)
            {
                ulong low = (ulong)rand.NextInt64(0, 1_000_000_000);
                ulong high = low + (ulong)rand.Next(0, 64);
                Assert.AreEqual(filter.Test(low, high), restored.Test(low, high),
                    $"restored filter disagreed about [{low}, {high}]");
            }
        }

        [TestMethod]
        public void TestUltraLogLogRoundTrips()
        {
            var sketch = new UltraLogLog(12);
            for (int i = 0; i < 50000; i++)
            {
                sketch.Add(Key($"item-{i}"));
            }

            var restored = RoundTrip(sketch);

            Assert.AreEqual(sketch.Count(), restored.Count(),
                "restored estimator gave a different count");
            Assert.AreEqual(sketch.Precision(), restored.Precision());

            // And keeps estimating, rather than merely reporting the stored number.
            for (int i = 50000; i < 60000; i++)
            {
                sketch.Add(Key($"item-{i}"));
                restored.Add(Key($"item-{i}"));
            }
            Assert.AreEqual(sketch.Count(), restored.Count(),
                "restored estimator diverged after more adds");
        }

        [TestMethod]
        public void TestVarOptRoundTrips()
        {
            var sample = new VarOpt(24, seed: 6);
            var rand = new Random(6);
            for (int i = 0; i < 20000; i++)
            {
                sample.Add(Key($"item-{i}"), (rand.NextDouble() * 50.0) + 0.01);
            }

            var restored = RoundTrip(sample);

            var before = sample.Samples()
                .Select(s => (Encoding.ASCII.GetString(s.Data.Span), s.Weight)).ToArray();
            var after = restored.Samples()
                .Select(s => (Encoding.ASCII.GetString(s.Data.Span), s.Weight)).ToArray();
            CollectionAssert.AreEqual(before, after,
                "the sample changed across the round trip");
            Assert.AreEqual(sample.TotalWeight, restored.TotalWeight, 0.0,
                "the total weight is exact, and a round trip must not round it");
            Assert.AreEqual(sample.N, restored.N);
        }

        [TestMethod]
        public void TestHeavyKeeperRoundTrips()
        {
            var hk = new HeavyKeeper(10, 512, seed: 6);
            var rand = new Random(6);
            for (int i = 0; i < 20000; i++)
            {
                hk.Add(Key($"item-{rand.Next(200)}"));
            }

            var restored = RoundTrip(hk);

            var before = hk.Elements()
                .Select(e => (Encoding.ASCII.GetString(e.Data.Span), e.Freq)).ToArray();
            var after = restored.Elements()
                .Select(e => (Encoding.ASCII.GetString(e.Data.Span), e.Freq)).ToArray();
            CollectionAssert.AreEqual(before, after,
                "the tracked top-k changed across the round trip");

            for (int i = 0; i < 200; i++)
            {
                Assert.AreEqual(hk.Count(Key($"item-{i}")), restored.Count(Key($"item-{i}")),
                    $"item-{i} answers differently after the round trip");
            }
        }

        [TestMethod]
        public void TestTopKRoundTrips()
        {
            var topK = new TopK(0.001, 0.01, 10);
            var rand = new Random(5);
            for (int i = 0; i < 20000; i++)
            {
                topK.Add(Key($"item-{rand.Next(200)}"));
            }

            var restored = RoundTrip(topK);

            var before = topK.Elements()
                .Select(e => (Encoding.ASCII.GetString(e.Data.Span), e.Freq)).ToArray();
            var after = restored.Elements()
                .Select(e => (Encoding.ASCII.GetString(e.Data.Span), e.Freq)).ToArray();

            CollectionAssert.AreEqual(before, after, "restored top-k held different elements");

            // And keeps ranking: the same stream continued on both must keep them equal,
            // which needs the sketch's counts as well as the heap's contents.
            for (int i = 0; i < 20000; i++)
            {
                var key = Key($"item-{rand.Next(200)}");
                topK.Add(key);
                restored.Add(key);
            }

            CollectionAssert.AreEqual(
                topK.Elements().Select(e => (Encoding.ASCII.GetString(e.Data.Span), e.Freq)).ToArray(),
                restored.Elements().Select(e => (Encoding.ASCII.GetString(e.Data.Span), e.Freq)).ToArray(),
                "restored top-k diverged from the original as the stream continued");
        }

        /// <summary>
        /// Empty is a shape the format has to handle as readily as full, and is the one
        /// most likely to be written by accident.
        /// </summary>
        [TestMethod]
        public void TestEveryStructureRoundTripsWhileEmpty()
        {
            var checks = new (StructureId Id, Action Check)[]
            {
                (StructureId.BloomFilter,
                    () => Assert.AreEqual(0u, RoundTrip(new BloomFilter(1000, 0.01)).Count())),
                (StructureId.BloomFilter64,
                    () => Assert.AreEqual(0ul, RoundTrip(new BloomFilter64(1000, 0.01)).Count())),
                (StructureId.CountingBloomFilter,
                    () => Assert.AreEqual(0u, RoundTrip(new CountingBloomFilter(1000, 4, 0.01)).Count())),
                (StructureId.DeletableBloomFilter,
                    () => Assert.AreEqual(0u, RoundTrip(new DeletableBloomFilter(1000, 10, 0.01)).Count())),
                (StructureId.PartitionedBloomFilter,
                    () => Assert.AreEqual(0u, RoundTrip(new PartitionedBloomFilter(1000, 0.01)).Count())),
                (StructureId.CuckooBloomFilter,
                    () => Assert.AreEqual(0u, RoundTrip(new CuckooBloomFilter(1000, 0.01)).Count())),
                (StructureId.HyperLogLog,
                    () => Assert.AreEqual(0ul, RoundTrip(new HyperLogLog(1024)).Count())),

                (StructureId.StableBloomFilter,
                    () => Assert.IsFalse(RoundTrip(new StableBloomFilter(1000, 2, 0.01)).Test(Key("x")))),
                (StructureId.InverseBloomFilter,
                    () => Assert.IsFalse(RoundTrip(new InverseBloomFilter(500)).Test(Key("x")))),
                (StructureId.ScalableBloomFilter,
                    () => Assert.IsFalse(RoundTrip(new ScalableBloomFilter(100, 0.01, 0.8)).Test(Key("x")))),
                (StructureId.TopK,
                    () => Assert.HasCount(0, RoundTrip(new TopK(0.001, 0.01, 10)).Elements())),
                (StructureId.HeavyKeeper,
                    () => Assert.HasCount(0, RoundTrip(new HeavyKeeper(10, 64, seed: 1)).Elements())),
                (StructureId.VarOpt,
                    () => Assert.AreEqual(0u, RoundTrip(new VarOpt(10, seed: 1)).SampleCount)),
                (StructureId.UltraLogLog,
                    () => Assert.AreEqual(0ul, RoundTrip(new UltraLogLog(10)).Count())),
                (StructureId.Grafite,
                    () => Assert.AreEqual(0ul, RoundTrip(Grafite.Build(Array.Empty<ulong>(), 0.01, 16)).Count())),
                (StructureId.InfiniFilter,
                    () => Assert.IsFalse(RoundTrip(new InfiniFilter(64, 8)).Test(Key("x")))),
                (StructureId.MementoFilter,
                    () => Assert.IsFalse(RoundTrip(new MementoFilter(256, 8)).Test(7))),

                (StructureId.CountMinSketch,
                    () => Assert.AreEqual(0ul, RoundTrip(new CountMinSketch(0.01, 0.01)).TotalCount())),
                (StructureId.BinaryFuseFilter,
                    () => Assert.AreEqual(0u, RoundTrip(BinaryFuseFilter.Build(Array.Empty<byte[]>())).Count())),
                (StructureId.DDSketch,
                    () => Assert.AreEqual(0ul, RoundTrip(new DDSketch(0.01)).Count())),
                (StructureId.HyperLogLogPlus,
                    () => Assert.AreEqual(0ul, RoundTrip(new HyperLogLogPlus(14)).Count())),
                (StructureId.QuotientFilter,
                    () => Assert.AreEqual(0u, RoundTrip(new QuotientFilter(1000, 0.01)).Count())),
                (StructureId.ThetaSketch,
                    () => Assert.AreEqual(0ul, RoundTrip(new ThetaSketch(4096)).Count())),
                (StructureId.CountSketch,
                    () => Assert.AreEqual(0L, RoundTrip(new CountSketch(0.05, 0.01)).Count(Key("x")))),
                (StructureId.BloomierFilter,
                    () => Assert.AreEqual(0u, RoundTrip(BloomierFilter.Build(
                        Array.Empty<KeyValuePair<byte[], ulong>>(), 8)).Count())),

                (StructureId.SetSketch,
                    () => Assert.AreEqual(0.0, RoundTrip(new SetSketch(8)).Cardinality())),
                (StructureId.TupleSketch,
                    () => Assert.AreEqual(0ul, RoundTrip(new TupleSketch(16)).Count())),
                (StructureId.SublimeCountMinSketch,
                    () => Assert.AreEqual(0ul, RoundTrip(EmptySublime()).TotalCount())),

                // The two private structures hold noise rather than counts, so an empty
                // one does not answer zero to a query. What has to survive the round
                // trip is the true tally underneath, which no noise is added to.
                (StructureId.PrivateCountMinSketch,
                    () => Assert.AreEqual(0ul,
                        RoundTrip(new PrivateCountMinSketch(8, 2, 0.5, seed: 1)).TotalCount())),
                (StructureId.DpswSketch,
                    () => Assert.AreEqual(0L, RoundTrip(EmptyDpsw()).Position)),

                (StructureId.InvertibleBloomLookupTable, () =>
                {
                    var emptyTable = RoundTrip(new InvertibleBloomLookupTable(10, 8));
                    Assert.AreEqual(15u, emptyTable.Cells());
                    Assert.AreEqual(8, emptyTable.KeySize());
                    Assert.IsTrue(emptyTable.TryDecode(out var nothing, out _));
                    Assert.HasCount(0, nothing);
                }),

                // A signature of an empty bag is the one case here that is not all
                // zeroes: every position holds the identity ulong.MaxValue, which a
                // restore that defaulted the array instead of reading it would turn
                // into 0.
                (StructureId.MinHashSignature, () =>
                {
                    var empty = RoundTrip(MinHash.Signature(Array.Empty<string>(), 8));
                    Assert.AreEqual(1f, MinHash.Similarity(empty, MinHash.Signature(Array.Empty<string>(), 8)));
                }),

                (StructureId.SimHashSignature, () =>
                {
                    var empty = RoundTrip(SimHash.Signature(Array.Empty<string>()));
                    Assert.AreEqual(0, SimHash.HammingDistance(empty, SimHash.Signature(Array.Empty<string>())));
                }),
            };

            foreach (var (id, check) in checks)
            {
                check();
            }

            AssertSweepCoversEveryStructure("empty round trip", checks.Select(c => c.Id));
        }

        // The starting width is a chunk of counters divided by the size factor, so
        // handing it a whole chunk asks for the smallest sketch the paper allows.
        private static SublimeCountMinSketch EmptySublime() =>
            new SublimeCountMinSketch(0.5, 0.5, ValeCounterArray.DefaultCountersPerChunk);

        private static DpswSketch EmptyDpsw() =>
            new DpswSketch(window: 16, rho: 4.0, alpha: 0.6, width: 4, depth: 2, seed: 3);

        /// <summary>
        /// Every structure names the hash it was written with, and refuses to substitute
        /// a different one. A structure restored under the wrong hash does not look
        /// broken, it looks empty.
        /// </summary>
        [TestMethod]
        public void TestEveryStructureRefusesToSubstituteACustomHash()
        {
            Func<ReadOnlySpan<byte>, ulong> custom = data => (ulong)data.Length * 2654435761UL;

            var bloom = new BloomFilter(1000, 0.01); bloom.SetHash(custom);
            var sketch = new CountMinSketch(0.01, 0.01); sketch.SetHash(custom);
            var bloom64 = new BloomFilter64(1000, 0.01); bloom64.SetHash(custom);
            var counting = new CountingBloomFilter(1000, 4, 0.01); counting.SetHash(custom);
            var deletable = new DeletableBloomFilter(1000, 10, 0.01); deletable.SetHash(custom);
            var partitioned = new PartitionedBloomFilter(1000, 0.01); partitioned.SetHash(custom);
            var stable = new StableBloomFilter(1000, 2, 0.01); stable.SetHash(custom);
            var inverse = new InverseBloomFilter(500); inverse.SetHash(custom);
            var cuckoo = new CuckooBloomFilter(1000, 0.01); cuckoo.SetHash(custom);
            var hll = new HyperLogLog(1024); hll.SetHash(custom);
            var scalable = new ScalableBloomFilter(100, 0.01, 0.8); scalable.SetHash(custom);
            var fuse = BinaryFuseFilter.Build(
                Present.Select(Key), BinaryFuseWidth.Eight, custom);
            var countSketch = new CountSketch(0.05, 0.01); countSketch.SetHash(custom);
            countSketch.Add(Key("a"));
            var iblt = new InvertibleBloomLookupTable(10, 8, custom);
            iblt.Add(new byte[8]);
            var bloomier = BloomierFilter.Build(
                Present.Select(w => new KeyValuePair<byte[], ulong>(Key(w), 1UL)),
                8, custom);
            var theta = new ThetaSketch(4096); theta.SetHash(custom);
            foreach (var w in Present) theta.Add(Key(w));
            var quotient = new QuotientFilter(1000, 0.01); quotient.SetHash(custom);
            foreach (var w in Present) quotient.Add(Key(w));
            var hllPlus = new HyperLogLogPlus(14); hllPlus.SetHash(custom);
            foreach (var w in Present) hllPlus.Add(Key(w));

            // These take their hash at construction rather than through SetHash, which
            // for several of them is the only way to install one at all.
            var topK = new TopK(0.001, 0.01, 10, custom); topK.Add(Key("a"));
            var keeper = new HeavyKeeper(10, 64, seed: 1, hash: custom); keeper.Add(Key("a"));
            var ultra = new UltraLogLog(10, custom); ultra.Add(Key("a"));
            var infini = new InfiniFilter(64, 8, custom); infini.Add(Key("a"));
            var sublime = new SublimeCountMinSketch(
                0.5, 0.5, ValeCounterArray.DefaultCountersPerChunk, custom);
            sublime.Add(Key("a"));
            var setSketch = new SetSketch(8, custom); setSketch.Add(Key("a"));
            var tuple = new TupleSketch(16); tuple.SetHash(custom); tuple.Add(Key("a"), 1.0);
            var priv = new PrivateCountMinSketch(8, 2, 0.5, seed: 1, hash: custom);
            priv.Add(Key("a"));
            var dpsw = new DpswSketch(
                window: 16, rho: 4.0, alpha: 0.6, width: 4, depth: 2, seed: 3, hash: custom);
            dpsw.Add(Key("a"));

            var covered = new (StructureId Id, byte[] Bytes, Func<byte[], object> Without, Func<byte[], object> With)[]
            {
                (StructureId.BloomierFilter, bloomier.ToByteArray(), b => Persistence.FromByteArray<BloomierFilter>(b), b => Persistence.FromByteArray<BloomierFilter>(b, custom)),
                (StructureId.InvertibleBloomLookupTable, iblt.ToByteArray(), b => Persistence.FromByteArray<InvertibleBloomLookupTable>(b), b => Persistence.FromByteArray<InvertibleBloomLookupTable>(b, custom)),
                (StructureId.CountSketch, countSketch.ToByteArray(), b => Persistence.FromByteArray<CountSketch>(b), b => Persistence.FromByteArray<CountSketch>(b, custom)),
                (StructureId.ThetaSketch, theta.ToByteArray(), b => Persistence.FromByteArray<ThetaSketch>(b), b => Persistence.FromByteArray<ThetaSketch>(b, custom)),
                (StructureId.QuotientFilter, quotient.ToByteArray(), b => Persistence.FromByteArray<QuotientFilter>(b), b => Persistence.FromByteArray<QuotientFilter>(b, custom)),
                (StructureId.HyperLogLogPlus, hllPlus.ToByteArray(), b => Persistence.FromByteArray<HyperLogLogPlus>(b), b => Persistence.FromByteArray<HyperLogLogPlus>(b, custom)),
                (StructureId.BinaryFuseFilter, fuse.ToByteArray(), b => Persistence.FromByteArray<BinaryFuseFilter>(b), b => Persistence.FromByteArray<BinaryFuseFilter>(b, custom)),
                (StructureId.BloomFilter, bloom.ToByteArray(), b => Persistence.FromByteArray<BloomFilter>(b), b => Persistence.FromByteArray<BloomFilter>(b, custom)),
                (StructureId.CountMinSketch, sketch.ToByteArray(), b => Persistence.FromByteArray<CountMinSketch>(b), b => Persistence.FromByteArray<CountMinSketch>(b, custom)),
                (StructureId.BloomFilter64, bloom64.ToByteArray(), b => Persistence.FromByteArray<BloomFilter64>(b), b => Persistence.FromByteArray<BloomFilter64>(b, custom)),
                (StructureId.CountingBloomFilter, counting.ToByteArray(), b => Persistence.FromByteArray<CountingBloomFilter>(b), b => Persistence.FromByteArray<CountingBloomFilter>(b, custom)),
                (StructureId.DeletableBloomFilter, deletable.ToByteArray(), b => Persistence.FromByteArray<DeletableBloomFilter>(b), b => Persistence.FromByteArray<DeletableBloomFilter>(b, custom)),
                (StructureId.PartitionedBloomFilter, partitioned.ToByteArray(), b => Persistence.FromByteArray<PartitionedBloomFilter>(b), b => Persistence.FromByteArray<PartitionedBloomFilter>(b, custom)),
                (StructureId.StableBloomFilter, stable.ToByteArray(), b => Persistence.FromByteArray<StableBloomFilter>(b), b => Persistence.FromByteArray<StableBloomFilter>(b, custom)),
                (StructureId.InverseBloomFilter, inverse.ToByteArray(), b => Persistence.FromByteArray<InverseBloomFilter>(b), b => Persistence.FromByteArray<InverseBloomFilter>(b, custom)),
                (StructureId.CuckooBloomFilter, cuckoo.ToByteArray(), b => Persistence.FromByteArray<CuckooBloomFilter>(b), b => Persistence.FromByteArray<CuckooBloomFilter>(b, custom)),
                (StructureId.HyperLogLog, hll.ToByteArray(), b => Persistence.FromByteArray<HyperLogLog>(b), b => Persistence.FromByteArray<HyperLogLog>(b, custom)),
                (StructureId.TopK, topK.ToByteArray(), b => Persistence.FromByteArray<TopK>(b), b => Persistence.FromByteArray<TopK>(b, custom)),
                (StructureId.HeavyKeeper, keeper.ToByteArray(), b => Persistence.FromByteArray<HeavyKeeper>(b), b => Persistence.FromByteArray<HeavyKeeper>(b, custom)),
                (StructureId.UltraLogLog, ultra.ToByteArray(), b => Persistence.FromByteArray<UltraLogLog>(b), b => Persistence.FromByteArray<UltraLogLog>(b, custom)),
                (StructureId.InfiniFilter, infini.ToByteArray(), b => Persistence.FromByteArray<InfiniFilter>(b), b => Persistence.FromByteArray<InfiniFilter>(b, custom)),
                (StructureId.SublimeCountMinSketch, sublime.ToByteArray(), b => Persistence.FromByteArray<SublimeCountMinSketch>(b), b => Persistence.FromByteArray<SublimeCountMinSketch>(b, custom)),
                (StructureId.SetSketch, setSketch.ToByteArray(), b => Persistence.FromByteArray<SetSketch>(b), b => Persistence.FromByteArray<SetSketch>(b, custom)),
                (StructureId.TupleSketch, tuple.ToByteArray(), b => Persistence.FromByteArray<TupleSketch>(b), b => Persistence.FromByteArray<TupleSketch>(b, custom)),
                (StructureId.PrivateCountMinSketch, priv.ToByteArray(), b => Persistence.FromByteArray<PrivateCountMinSketch>(b), b => Persistence.FromByteArray<PrivateCountMinSketch>(b, custom)),
                (StructureId.DpswSketch, dpsw.ToByteArray(), b => Persistence.FromByteArray<DpswSketch>(b), b => Persistence.FromByteArray<DpswSketch>(b, custom)),

                // The composite one matters most: its contained filters each name the
                // hash too, so a scalable filter cannot come back half converted.
                (StructureId.ScalableBloomFilter, scalable.ToByteArray(), b => Persistence.FromByteArray<ScalableBloomFilter>(b), b => Persistence.FromByteArray<ScalableBloomFilter>(b, custom)),
            };

            foreach (var (id, bytes, without, with) in covered)
            {
                AssertRefusesThenAccepts(id, bytes, without, with);
            }

            AssertSweepCoversEveryStructure(
                "custom hash",
                covered.Select(c => c.Id),
                (StructureId.DDSketch,
                    "takes numbers rather than bytes, so it hashes nothing and records " +
                    "HashId.None; a reader handed a hash for one is refused instead."),
                (StructureId.MinHashSignature,
                    "hashes with a fixed family chosen by the signature length; there " +
                    "is no parameter through which to substitute another."),
                (StructureId.SimHashSignature,
                    "same: the hash is fixed by the construction rather than supplied."),
                (StructureId.VarOpt,
                    "samples the items themselves rather than hashing them."),
                (StructureId.Grafite,
                    "takes ulong keys and derives its positions from them and a seed, " +
                    "with no hash parameter on Build."),
                (StructureId.MementoFilter,
                    "takes ulong keys likewise, with no hash parameter on the constructor."));
        }

        private static void AssertRefusesThenAccepts(
            StructureId id,
            byte[] bytes,
            Func<byte[], object> withoutHash,
            Func<byte[], object> withHash)
        {
            Assert.ThrowsExactly<InvalidDataException>(() => withoutHash(bytes),
                $"a {id} written under a custom hash was restored without being given one");
            Assert.IsNotNull(withHash(bytes),
                $"a {id} written under a custom hash was refused when handed that hash");
        }

        /// <summary>
        /// Reading one structure as another is refused by the structure id, which every
        /// payload carries. Without it the bytes of one would be read as the fields of
        /// another and produce something that answers confidently and wrongly.
        /// </summary>
        [TestMethod]
        public void TestEveryStructureRefusesToBeReadAsAnother()
        {
            var payloads = new (StructureId Id, byte[] Bytes)[]
            {
                (StructureId.BloomFilter, new BloomFilter(1000, 0.01).ToByteArray()),
                (StructureId.BloomFilter64, new BloomFilter64(1000, 0.01).ToByteArray()),
                (StructureId.CountingBloomFilter, new CountingBloomFilter(1000, 4, 0.01).ToByteArray()),
                (StructureId.DeletableBloomFilter, new DeletableBloomFilter(1000, 10, 0.01).ToByteArray()),
                (StructureId.PartitionedBloomFilter, new PartitionedBloomFilter(1000, 0.01).ToByteArray()),
                (StructureId.ScalableBloomFilter, new ScalableBloomFilter(100, 0.01, 0.8).ToByteArray()),
                (StructureId.StableBloomFilter, new StableBloomFilter(1000, 2, 0.01).ToByteArray()),
                (StructureId.InverseBloomFilter, new InverseBloomFilter(500).ToByteArray()),
                (StructureId.CuckooBloomFilter, new CuckooBloomFilter(1000, 0.01).ToByteArray()),
                (StructureId.CountMinSketch, new CountMinSketch(0.01, 0.01).ToByteArray()),
                (StructureId.HyperLogLog, new HyperLogLog(1024).ToByteArray()),
                (StructureId.TopK, new TopK(0.001, 0.01, 10).ToByteArray()),
                (StructureId.MinHashSignature, MinHash.Signature(new[] { "a" }, 8).ToByteArray()),
                (StructureId.BinaryFuseFilter, BinaryFuseFilter.Build(new[] { Key("a") }).ToByteArray()),
                (StructureId.DDSketch, FilledSketchOfNumbers()),
                (StructureId.HyperLogLogPlus, new HyperLogLogPlus(14).Add(Key("a")).ToByteArray()),
                (StructureId.QuotientFilter, new QuotientFilter(1000, 0.01).Add(Key("a")).ToByteArray()),
                (StructureId.ThetaSketch, new ThetaSketch(4096).Add(Key("a")).ToByteArray()),
                (StructureId.SimHashSignature, SimHash.Signature(new[] { "a" }).ToByteArray()),
                (StructureId.CountSketch, new CountSketch(0.5, 0.5).Add(Key("a")).ToByteArray()),
                (StructureId.InvertibleBloomLookupTable, new InvertibleBloomLookupTable(4, 8).Add(new byte[8]).ToByteArray()),
                (StructureId.BloomierFilter, FilledBloomier()),
                (StructureId.HeavyKeeper, new HeavyKeeper(10, 64, seed: 1).Add(Key("a")).ToByteArray()),
                (StructureId.VarOpt, new VarOpt(10, seed: 1).Add(Key("a")).ToByteArray()),
                (StructureId.UltraLogLog, new UltraLogLog(10).Add(Key("a")).ToByteArray()),
                (StructureId.Grafite, Grafite.Build(new ulong[] { 1, 2, 3 }, 0.01, 16, seed: 1).ToByteArray()),
                (StructureId.InfiniFilter, new InfiniFilter(64, 8).Add(Key("a")).ToByteArray()),
                (StructureId.MementoFilter, new MementoFilter(256, 8).Add(5).ToByteArray()),
                (StructureId.PrivateCountMinSketch,
                    new PrivateCountMinSketch(16, 2, 0.5, seed: 1).Add(Key("a")).ToByteArray()),
                (StructureId.DpswSketch, FilledDpsw()),
                (StructureId.SublimeCountMinSketch, FilledSublime()),
                (StructureId.SetSketch, FilledSetSketch()),
                (StructureId.TupleSketch, FilledTuple()),
            };

            // Read every payload as a BloomFilter; only its own may succeed.
            foreach (var (id, bytes) in payloads)
            {
                if (id == StructureId.BloomFilter)
                {
                    Assert.IsNotNull(Persistence.FromByteArray<BloomFilter>(bytes));
                    continue;
                }

                var ex = Assert.ThrowsExactly<InvalidDataException>(
                    () => Persistence.FromByteArray<BloomFilter>(bytes),
                    $"a {id} payload was accepted as a BloomFilter");
                StringAssert.Contains(ex.Message, id.ToString());
            }

            AssertSweepCoversEveryStructure("read as another", payloads.Select(pl => pl.Id));

            // Every payload carries a distinct structure id, or the check above is
            // weaker than it looks -- and the sweep has to be complete first, or this
            // is only asserting that the structures someone remembered do not collide.
            var ids = payloads.Select(p => p.Bytes[6] | (p.Bytes[7] << 8)).ToArray();
            Assert.AreEqual(payloads.Length, ids.Distinct().Count(),
                "two structures share a structure id");
        }

        /// <summary>
        /// Corruption anywhere in a payload is caught, for every structure, rather than
        /// producing a structure that answers incorrectly.
        /// </summary>
        [TestMethod]
        public void TestCorruptionIsCaughtInEveryStructure()
        {
            var payloads = new (StructureId Id, Func<byte[], object> Read, byte[] Bytes)[]
            {
                (StructureId.BloomFilter64, b => Persistence.FromByteArray<BloomFilter64>(b), Filled(new BloomFilter64(200, 0.01))),
                (StructureId.CountingBloomFilter, b => Persistence.FromByteArray<CountingBloomFilter>(b), Filled(new CountingBloomFilter(200, 4, 0.01))),
                (StructureId.DeletableBloomFilter, b => Persistence.FromByteArray<DeletableBloomFilter>(b), Filled(new DeletableBloomFilter(200, 10, 0.01))),
                (StructureId.PartitionedBloomFilter, b => Persistence.FromByteArray<PartitionedBloomFilter>(b), Filled(new PartitionedBloomFilter(200, 0.01))),
                (StructureId.StableBloomFilter, b => Persistence.FromByteArray<StableBloomFilter>(b), Filled(new StableBloomFilter(200, 2, 0.01))),
                (StructureId.InverseBloomFilter, b => Persistence.FromByteArray<InverseBloomFilter>(b), Filled(new InverseBloomFilter(50))),
                (StructureId.CuckooBloomFilter, b => Persistence.FromByteArray<CuckooBloomFilter>(b), FilledCuckoo()),
                (StructureId.HyperLogLog, b => Persistence.FromByteArray<HyperLogLog>(b), FilledHll()),
                (StructureId.ScalableBloomFilter, b => Persistence.FromByteArray<ScalableBloomFilter>(b), Filled(new ScalableBloomFilter(50, 0.01, 0.8))),
                (StructureId.TopK, b => Persistence.FromByteArray<TopK>(b), FilledTopK()),
                (StructureId.BloomFilter, b => Persistence.FromByteArray<BloomFilter>(b), Filled(new BloomFilter(200, 0.01))),
                (StructureId.CountMinSketch, b => Persistence.FromByteArray<CountMinSketch>(b), FilledSketch()),
                (StructureId.MinHashSignature, b => Persistence.FromByteArray<MinHashSignature>(b), FilledSignature()),
                (StructureId.BinaryFuseFilter, b => Persistence.FromByteArray<BinaryFuseFilter>(b), FilledFuse()),
                (StructureId.DDSketch, b => Persistence.FromByteArray<DDSketch>(b), FilledSketchOfNumbers()),
                // Both representations, which are different layouts behind one id.
                (StructureId.HyperLogLogPlus, b => Persistence.FromByteArray<HyperLogLogPlus>(b), FilledHllPlus(20)),
                (StructureId.HyperLogLogPlus, b => Persistence.FromByteArray<HyperLogLogPlus>(b), FilledHllPlus(200)),
                (StructureId.QuotientFilter, b => Persistence.FromByteArray<QuotientFilter>(b), FilledQuotient()),
                (StructureId.ThetaSketch, b => Persistence.FromByteArray<ThetaSketch>(b), FilledTheta()),
                (StructureId.SimHashSignature, b => Persistence.FromByteArray<SimHashSignature>(b),
                    SimHash.Signature(new[] { "a", "b", "c", "d" }).ToByteArray()),
                (StructureId.CountSketch, b => Persistence.FromByteArray<CountSketch>(b), FilledCountSketch()),
                (StructureId.InvertibleBloomLookupTable, b => Persistence.FromByteArray<InvertibleBloomLookupTable>(b), FilledIblt()),
                (StructureId.BloomierFilter, b => Persistence.FromByteArray<BloomierFilter>(b), FilledBloomier()),
                (StructureId.HeavyKeeper, b => Persistence.FromByteArray<HeavyKeeper>(b), FilledHeavyKeeper()),
                (StructureId.VarOpt, b => Persistence.FromByteArray<VarOpt>(b), FilledVarOpt()),
                (StructureId.UltraLogLog, b => Persistence.FromByteArray<UltraLogLog>(b), FilledUltraLogLog()),
                (StructureId.Grafite, b => Persistence.FromByteArray<Grafite>(b), FilledGrafite()),
                (StructureId.InfiniFilter, b => Persistence.FromByteArray<InfiniFilter>(b), FilledInfiniFilter()),
                (StructureId.MementoFilter, b => Persistence.FromByteArray<MementoFilter>(b), FilledMementoFilter()),
                (StructureId.PrivateCountMinSketch, b => Persistence.FromByteArray<PrivateCountMinSketch>(b),
                    FilledPrivateSketch()),
                (StructureId.DpswSketch, b => Persistence.FromByteArray<DpswSketch>(b), FilledDpsw()),
                (StructureId.SublimeCountMinSketch, b => Persistence.FromByteArray<SublimeCountMinSketch>(b),
                    FilledSublime()),
                (StructureId.SetSketch, b => Persistence.FromByteArray<SetSketch>(b), FilledSetSketch()),
                (StructureId.TupleSketch, b => Persistence.FromByteArray<TupleSketch>(b), FilledTuple()),
            };

            foreach (var (id, read, clean) in payloads)
            {
                for (int i = 4; i < clean.Length; i++)
                {
                    var corrupted = (byte[])clean.Clone();
                    corrupted[i] ^= 0x01;

                    Assert.ThrowsExactly<InvalidDataException>(() => read(corrupted),
                        $"{id}: a flipped bit at offset {i} was not caught");
                }
            }

            AssertSweepCoversEveryStructure("corruption", payloads.Select(pl => pl.Id));
        }

        // Small on purpose, as with the other payloads here: the sweep above flips a
        // bit at every offset of every payload one at a time.
        private static byte[] FilledSublime()
        {
            var sketch = EmptySublime();
            for (var i = 0; i < 40; i++) sketch.Add(Key($"w{i % 5}"));
            return sketch.ToByteArray();
        }

        private static byte[] FilledSetSketch()
        {
            var sketch = new SetSketch(8);
            for (var i = 0; i < 40; i++) sketch.Add(Key($"w{i}"));
            return sketch.ToByteArray();
        }

        private static byte[] FilledTuple()
        {
            var sketch = new TupleSketch(16);
            for (var i = 0; i < 40; i++) sketch.Add(Key($"w{i}"), 1.0 + i);
            return sketch.ToByteArray();
        }

        private static byte[] FilledPrivateSketch()
        {
            var f = new PrivateCountMinSketch(8, 2, 0.5, seed: 3);
            for (var i = 0; i < 40; i++) f.Add(Key($"item-{i % 8}"));
            return f.ToByteArray();
        }

        private static byte[] FilledDpsw()
        {
            // Small on purpose: the distance-one sweep flips every bit of the payload
            // one at a time, and a window holds a private sketch per checkpoint per
            // substream, so a realistic one runs to hundreds of kilobytes.
            var f = new DpswSketch(
                window: 16, rho: 4.0, alpha: 0.6, width: 4, depth: 2, seed: 3);
            for (var i = 0; i < 20; i++) f.Add(Key($"item-{i % 5}"));
            return f.ToByteArray();
        }

        private static byte[] FilledMementoFilter()
        {
            var f = new MementoFilter(64, 6, initialCapacity: 8);
            for (ulong i = 0; i < 150; i++) f.Add(i * 5);
            return f.ToByteArray();
        }

        private static byte[] FilledInfiniFilter()
        {
            var f = new InfiniFilter(initialCapacity: 8, fingerprintBits: 6);
            for (int i = 0; i < 120; i++) f.Add(Key($"item-{i}"));
            return f.ToByteArray();
        }

        private static byte[] FilledGrafite()
        {
            var keys = Enumerable.Range(0, 60).Select(i => (ulong)(i * 37));
            return Grafite.Build(keys, 0.05, 8, seed: 11).ToByteArray();
        }

        private static byte[] FilledUltraLogLog()
        {
            var sketch = new UltraLogLog(5);
            for (int i = 0; i < 200; i++)
            {
                sketch.Add(Key($"item-{i}"));
            }
            return sketch.ToByteArray();
        }

        private static byte[] FilledVarOpt()
        {
            var sample = new VarOpt(5, seed: 7);
            for (int i = 0; i < 60; i++)
            {
                sample.Add(Key($"item-{i}"), 1.0 + (i % 8));
            }
            return sample.ToByteArray();
        }

        private static byte[] FilledHeavyKeeper()
        {
            var hk = new HeavyKeeper(5, 16, seed: 7);
            for (int i = 0; i < 60; i++)
            {
                hk.Add(Key($"item-{i % 8}"));
            }
            return hk.ToByteArray();
        }

        private static byte[] Filled<T>(T filter) where T : IFilter, IBinaryPersistable<T>
        {
            for (int i = 0; i < 40; i++) filter.Add(Key($"w{i}"));
            return filter.ToByteArray();
        }

        // The cuckoo filter is the one structure here that is not an IFilter; its Add
        // returns whether there was room rather than the filter.
        private static byte[] FilledCuckoo()
        {
            var f = new CuckooBloomFilter(200, 0.01);
            for (int i = 0; i < 40; i++) f.Add(Key($"w{i}"));
            return f.ToByteArray();
        }

        private static byte[] FilledHll()
        {
            var h = new HyperLogLog(64);
            for (int i = 0; i < 40; i++) h.Add(Key($"w{i}"));
            return h.ToByteArray();
        }

        // Loaded past its retained size so the payload carries a lowered theta rather
        // than the exact-count case.
        private static byte[] FilledBloomier()
        {
            return BloomierFilter.Build(
                Enumerable.Range(0, 40).Select(i =>
                    new System.Collections.Generic.KeyValuePair<byte[], ulong>(Key($"w{i}"), (ulong)i)),
                8).ToByteArray();
        }

        private static byte[] FilledIblt()
        {
            var table = new InvertibleBloomLookupTable(8, 8);
            for (var i = 0; i < 6; i++)
            {
                var key = new byte[8];
                key[0] = (byte)i;
                table.Add(key);
            }

            return table.ToByteArray();
        }

        private static byte[] FilledCountSketch()
        {
            var sketch = new CountSketch(0.5, 0.5);
            for (var i = 0; i < 40; i++)
            {
                sketch.Add(Key($"w{i}"), i % 5);
            }

            return sketch.ToByteArray();
        }

        private static byte[] FilledTheta()
        {
            var sketch = new ThetaSketch(16);
            for (var i = 0; i < 200; i++)
            {
                sketch.Add(Key($"w{i}"));
            }

            return sketch.ToByteArray();
        }

        // Not an IFilter: its Add returns the filter but it has no TestAndAdd.
        private static byte[] FilledQuotient()
        {
            var filter = new QuotientFilter(100, 0.01);
            for (var i = 0; i < 40; i++)
            {
                filter.Add(Key($"w{i}"));
            }

            return filter.ToByteArray();
        }

        private static byte[] FilledHllPlus(int items)
        {
            // Precision 4 gives 16 registers, so 200 items is comfortably dense and 20
            // is comfortably sparse.
            var estimator = new HyperLogLogPlus(4);
            for (var i = 0; i < items; i++)
            {
                estimator.Add(Key($"w{i}"));
            }

            return estimator.ToByteArray();
        }

        // Takes numbers rather than bytes, so it shares no helper with the rest.
        private static byte[] FilledSketchOfNumbers()
        {
            var sketch = new DDSketch(0.1);
            for (var i = 1; i <= 40; i++)
            {
                sketch.Add(i);
            }

            return sketch.ToByteArray();
        }

        // Not an IFilter: the set is fixed at construction, so there is no Add.
        private static byte[] FilledFuse()
        {
            return BinaryFuseFilter.Build(
                Enumerable.Range(0, 40).Select(i => Key($"w{i}"))).ToByteArray();
        }

        private static byte[] FilledSketch()
        {
            var s = new CountMinSketch(0.1, 0.5);
            for (int i = 0; i < 40; i++) s.Add(Key($"w{i % 5}"));
            return s.ToByteArray();
        }

        private static byte[] FilledSignature()
        {
            return MinHash.Signature(new[] { "a", "b", "c", "d" }, 8).ToByteArray();
        }

        private static byte[] FilledTopK()
        {
            var t = new TopK(0.1, 0.5, 3);
            for (int i = 0; i < 40; i++) t.Add(Key($"w{i % 5}"));
            return t.ToByteArray();
        }
    }
}
