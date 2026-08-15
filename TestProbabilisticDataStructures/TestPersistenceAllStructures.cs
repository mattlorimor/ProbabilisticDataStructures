using System;
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
            Assert.AreEqual(0ul, RoundTrip(new BloomFilter64(1000, 0.01)).Count());
            Assert.AreEqual(0u, RoundTrip(new CountingBloomFilter(1000, 4, 0.01)).Count());
            Assert.AreEqual(0u, RoundTrip(new DeletableBloomFilter(1000, 10, 0.01)).Count());
            Assert.AreEqual(0u, RoundTrip(new PartitionedBloomFilter(1000, 0.01)).Count());
            Assert.AreEqual(0u, RoundTrip(new CuckooBloomFilter(1000, 0.01)).Count());
            Assert.AreEqual(0ul, RoundTrip(new HyperLogLog(1024)).Count());

            Assert.IsFalse(RoundTrip(new StableBloomFilter(1000, 2, 0.01)).Test(Key("x")));
            Assert.IsFalse(RoundTrip(new InverseBloomFilter(500)).Test(Key("x")));
            Assert.IsFalse(RoundTrip(new ScalableBloomFilter(100, 0.01, 0.8)).Test(Key("x")));
            Assert.HasCount(0, RoundTrip(new TopK(0.001, 0.01, 10)).Elements());

            Assert.AreEqual(0ul, RoundTrip(new CountMinSketch(0.01, 0.01)).TotalCount());
            Assert.AreEqual(0u, RoundTrip(BinaryFuseFilter.Build(Array.Empty<byte[]>())).Count());
            Assert.AreEqual(0ul, RoundTrip(new DDSketch(0.01)).Count());
            Assert.AreEqual(0ul, RoundTrip(new HyperLogLogPlus(14)).Count());
            Assert.AreEqual(0u, RoundTrip(new QuotientFilter(1000, 0.01)).Count());
            Assert.AreEqual(0ul, RoundTrip(new ThetaSketch(4096)).Count());

            // A signature of an empty bag is the one case here that is not all zeroes:
            // every position holds the identity ulong.MaxValue, which a restore that
            // defaulted the array instead of reading it would turn into 0.
            var empty = RoundTrip(MinHash.Signature(Array.Empty<string>(), 8));
            Assert.AreEqual(1f, MinHash.Similarity(empty, MinHash.Signature(Array.Empty<string>(), 8)));
        }

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
            var theta = new ThetaSketch(4096); theta.SetHash(custom);
            foreach (var w in Present) theta.Add(Key(w));
            var quotient = new QuotientFilter(1000, 0.01); quotient.SetHash(custom);
            foreach (var w in Present) quotient.Add(Key(w));
            var hllPlus = new HyperLogLogPlus(14); hllPlus.SetHash(custom);
            foreach (var w in Present) hllPlus.Add(Key(w));

            AssertRefusesThenAccepts(theta.ToByteArray(), b => Persistence.FromByteArray<ThetaSketch>(b), b => Persistence.FromByteArray<ThetaSketch>(b, custom));
            AssertRefusesThenAccepts(quotient.ToByteArray(), b => Persistence.FromByteArray<QuotientFilter>(b), b => Persistence.FromByteArray<QuotientFilter>(b, custom));
            AssertRefusesThenAccepts(hllPlus.ToByteArray(), b => Persistence.FromByteArray<HyperLogLogPlus>(b), b => Persistence.FromByteArray<HyperLogLogPlus>(b, custom));
            AssertRefusesThenAccepts(fuse.ToByteArray(), b => Persistence.FromByteArray<BinaryFuseFilter>(b), b => Persistence.FromByteArray<BinaryFuseFilter>(b, custom));
            AssertRefusesThenAccepts(bloom.ToByteArray(), b => Persistence.FromByteArray<BloomFilter>(b), b => Persistence.FromByteArray<BloomFilter>(b, custom));
            AssertRefusesThenAccepts(sketch.ToByteArray(), b => Persistence.FromByteArray<CountMinSketch>(b), b => Persistence.FromByteArray<CountMinSketch>(b, custom));
            AssertRefusesThenAccepts(bloom64.ToByteArray(), b => Persistence.FromByteArray<BloomFilter64>(b), b => Persistence.FromByteArray<BloomFilter64>(b, custom));
            AssertRefusesThenAccepts(counting.ToByteArray(), b => Persistence.FromByteArray<CountingBloomFilter>(b), b => Persistence.FromByteArray<CountingBloomFilter>(b, custom));
            AssertRefusesThenAccepts(deletable.ToByteArray(), b => Persistence.FromByteArray<DeletableBloomFilter>(b), b => Persistence.FromByteArray<DeletableBloomFilter>(b, custom));
            AssertRefusesThenAccepts(partitioned.ToByteArray(), b => Persistence.FromByteArray<PartitionedBloomFilter>(b), b => Persistence.FromByteArray<PartitionedBloomFilter>(b, custom));
            AssertRefusesThenAccepts(stable.ToByteArray(), b => Persistence.FromByteArray<StableBloomFilter>(b), b => Persistence.FromByteArray<StableBloomFilter>(b, custom));
            AssertRefusesThenAccepts(inverse.ToByteArray(), b => Persistence.FromByteArray<InverseBloomFilter>(b), b => Persistence.FromByteArray<InverseBloomFilter>(b, custom));
            AssertRefusesThenAccepts(cuckoo.ToByteArray(), b => Persistence.FromByteArray<CuckooBloomFilter>(b), b => Persistence.FromByteArray<CuckooBloomFilter>(b, custom));
            AssertRefusesThenAccepts(hll.ToByteArray(), b => Persistence.FromByteArray<HyperLogLog>(b), b => Persistence.FromByteArray<HyperLogLog>(b, custom));

            // The composite one matters most: its contained filters each name the hash
            // too, so a scalable filter cannot come back half converted.
            AssertRefusesThenAccepts(scalable.ToByteArray(), b => Persistence.FromByteArray<ScalableBloomFilter>(b), b => Persistence.FromByteArray<ScalableBloomFilter>(b, custom));
        }

        private static void AssertRefusesThenAccepts(
            byte[] bytes, Func<byte[], object> withoutHash, Func<byte[], object> withHash)
        {
            Assert.ThrowsExactly<InvalidDataException>(() => withoutHash(bytes));
            Assert.IsNotNull(withHash(bytes));
        }

        /// <summary>
        /// Reading one structure as another is refused by the structure id, which every
        /// payload carries. Without it the bytes of one would be read as the fields of
        /// another and produce something that answers confidently and wrongly.
        /// </summary>
        [TestMethod]
        public void TestEveryStructureRefusesToBeReadAsAnother()
        {
            var payloads = new (string Name, byte[] Bytes)[]
            {
                ("BloomFilter", new BloomFilter(1000, 0.01).ToByteArray()),
                ("BloomFilter64", new BloomFilter64(1000, 0.01).ToByteArray()),
                ("CountingBloomFilter", new CountingBloomFilter(1000, 4, 0.01).ToByteArray()),
                ("DeletableBloomFilter", new DeletableBloomFilter(1000, 10, 0.01).ToByteArray()),
                ("PartitionedBloomFilter", new PartitionedBloomFilter(1000, 0.01).ToByteArray()),
                ("ScalableBloomFilter", new ScalableBloomFilter(100, 0.01, 0.8).ToByteArray()),
                ("StableBloomFilter", new StableBloomFilter(1000, 2, 0.01).ToByteArray()),
                ("InverseBloomFilter", new InverseBloomFilter(500).ToByteArray()),
                ("CuckooBloomFilter", new CuckooBloomFilter(1000, 0.01).ToByteArray()),
                ("CountMinSketch", new CountMinSketch(0.01, 0.01).ToByteArray()),
                ("HyperLogLog", new HyperLogLog(1024).ToByteArray()),
                ("TopK", new TopK(0.001, 0.01, 10).ToByteArray()),
                ("MinHashSignature", MinHash.Signature(new[] { "a" }, 8).ToByteArray()),
                ("BinaryFuseFilter", BinaryFuseFilter.Build(new[] { Key("a") }).ToByteArray()),
                ("DDSketch", FilledSketchOfNumbers()),
                ("HyperLogLogPlus", new HyperLogLogPlus(14).Add(Key("a")).ToByteArray()),
                ("QuotientFilter", new QuotientFilter(1000, 0.01).Add(Key("a")).ToByteArray()),
                ("ThetaSketch", new ThetaSketch(4096).Add(Key("a")).ToByteArray()),
                ("SimHashSignature", SimHash.Signature(new[] { "a" }).ToByteArray()),
            };

            // Read every payload as a BloomFilter; only its own may succeed.
            foreach (var (name, bytes) in payloads)
            {
                if (name == "BloomFilter")
                {
                    Assert.IsNotNull(Persistence.FromByteArray<BloomFilter>(bytes));
                    continue;
                }

                var ex = Assert.ThrowsExactly<InvalidDataException>(
                    () => Persistence.FromByteArray<BloomFilter>(bytes),
                    $"a {name} payload was accepted as a BloomFilter");
                StringAssert.Contains(ex.Message, name);
            }

            // Every payload carries a distinct structure id, or the check above is
            // weaker than it looks.
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
            var payloads = new (string Name, Func<byte[], object> Read, byte[] Bytes)[]
            {
                ("BloomFilter64", b => Persistence.FromByteArray<BloomFilter64>(b), Filled(new BloomFilter64(200, 0.01))),
                ("CountingBloomFilter", b => Persistence.FromByteArray<CountingBloomFilter>(b), Filled(new CountingBloomFilter(200, 4, 0.01))),
                ("DeletableBloomFilter", b => Persistence.FromByteArray<DeletableBloomFilter>(b), Filled(new DeletableBloomFilter(200, 10, 0.01))),
                ("PartitionedBloomFilter", b => Persistence.FromByteArray<PartitionedBloomFilter>(b), Filled(new PartitionedBloomFilter(200, 0.01))),
                ("StableBloomFilter", b => Persistence.FromByteArray<StableBloomFilter>(b), Filled(new StableBloomFilter(200, 2, 0.01))),
                ("InverseBloomFilter", b => Persistence.FromByteArray<InverseBloomFilter>(b), Filled(new InverseBloomFilter(50))),
                ("CuckooBloomFilter", b => Persistence.FromByteArray<CuckooBloomFilter>(b), FilledCuckoo()),
                ("HyperLogLog", b => Persistence.FromByteArray<HyperLogLog>(b), FilledHll()),
                ("ScalableBloomFilter", b => Persistence.FromByteArray<ScalableBloomFilter>(b), Filled(new ScalableBloomFilter(50, 0.01, 0.8))),
                ("TopK", b => Persistence.FromByteArray<TopK>(b), FilledTopK()),
                ("BloomFilter", b => Persistence.FromByteArray<BloomFilter>(b), Filled(new BloomFilter(200, 0.01))),
                ("CountMinSketch", b => Persistence.FromByteArray<CountMinSketch>(b), FilledSketch()),
                ("MinHashSignature", b => Persistence.FromByteArray<MinHashSignature>(b), FilledSignature()),
                ("BinaryFuseFilter", b => Persistence.FromByteArray<BinaryFuseFilter>(b), FilledFuse()),
                ("DDSketch", b => Persistence.FromByteArray<DDSketch>(b), FilledSketchOfNumbers()),
                // Both representations, which are different layouts behind one id.
                ("HyperLogLogPlus sparse", b => Persistence.FromByteArray<HyperLogLogPlus>(b), FilledHllPlus(20)),
                ("HyperLogLogPlus dense", b => Persistence.FromByteArray<HyperLogLogPlus>(b), FilledHllPlus(200)),
                ("QuotientFilter", b => Persistence.FromByteArray<QuotientFilter>(b), FilledQuotient()),
                ("ThetaSketch", b => Persistence.FromByteArray<ThetaSketch>(b), FilledTheta()),
                ("SimHashSignature", b => Persistence.FromByteArray<SimHashSignature>(b),
                    SimHash.Signature(new[] { "a", "b", "c", "d" }).ToByteArray()),
            };

            foreach (var (name, read, clean) in payloads)
            {
                for (int i = 4; i < clean.Length; i++)
                {
                    var corrupted = (byte[])clean.Clone();
                    corrupted[i] ^= 0x01;

                    Assert.ThrowsExactly<InvalidDataException>(() => read(corrupted),
                        $"{name}: a flipped bit at offset {i} was not caught");
                }
            }
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
