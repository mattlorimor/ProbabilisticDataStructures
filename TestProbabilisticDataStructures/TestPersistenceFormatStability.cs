using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Payloads written by the version that introduced each structure's layout, checked
    /// in byte-for-byte. The format is documented as stable, and this is what makes that
    /// a promise rather than an intention: a change that stops old data being readable
    /// fails here rather than in somebody's storage.
    /// <para>
    /// If one of these fails, the fix is not to regenerate the fixture. It is either to
    /// keep reading the old layout or to raise the format version and read both.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestPersistenceFormatStability
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        private static readonly string[] Words =
            { "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa" };

        [TestMethod]
        public void TestStoredBloomFilter64StillReads()
        {
            var f = BloomFilter64.ReadFrom(Fixture("bloomfilter64-v1.bin"));

            Assert.AreEqual(959ul, f.Capacity());
            Assert.AreEqual(7u, f.K());
            Assert.AreEqual(10ul, f.Count());
            AssertAllPresent(f.Test);
        }

        [TestMethod]
        public void TestStoredCountingBloomFilterStillReads()
        {
            var f = CountingBloomFilter.ReadFrom(Fixture("countingbloomfilter-v1.bin"));

            Assert.AreEqual(959u, f.Capacity());
            Assert.AreEqual(7u, f.K());
            Assert.AreEqual(10u, f.Count());
            AssertAllPresent(f.Test);

            // The counters came back, not just the occupancy: each word removes once
            // and is gone.
            Assert.IsTrue(f.TestAndRemove(Key("alpha")));
            Assert.IsFalse(f.Test(Key("alpha")));
        }

        [TestMethod]
        public void TestStoredDeletableBloomFilterStillReads()
        {
            var f = DeletableBloomFilter.ReadFrom(Fixture("deletablebloomfilter-v1.bin"));

            Assert.AreEqual(949u, f.Capacity());
            Assert.AreEqual(7u, f.K());
            Assert.AreEqual(10u, f.Count());
            AssertAllPresent(f.Test);

            // Region size is recomputed on read rather than stored, so this also
            // exercises that the recomputation agrees with what wrote the payload.
            Assert.IsTrue(f.TestAndRemove(Key("alpha")));
        }

        [TestMethod]
        public void TestStoredPartitionedBloomFilterStillReads()
        {
            var f = PartitionedBloomFilter.ReadFrom(Fixture("partitionedbloomfilter-v1.bin"));

            Assert.AreEqual(959u, f.Capacity());
            Assert.AreEqual(7u, f.K());
            Assert.AreEqual(10u, f.Count());
            AssertAllPresent(f.Test);
        }

        [TestMethod]
        public void TestStoredScalableBloomFilterStillReads()
        {
            var f = ScalableBloomFilter.ReadFrom(Fixture("scalablebloomfilter-v1.bin"));

            Assert.AreEqual(1053u, f.Capacity());
            Assert.AreEqual(7u, f.K());

            for (int i = 0; i < 100; i++)
            {
                Assert.IsTrue(f.Test(Key($"s{i}")), $"the stored filter no longer finds s{i}");
            }

            // The contained filters came back in order with their fill ratios intact,
            // so it still grows rather than stopping where it was written.
            for (int i = 100; i < 400; i++)
            {
                f.Add(Key($"s{i}"));
            }

            Assert.IsGreaterThan(1053u, f.Capacity(), "the restored filter did not grow");
        }

        [TestMethod]
        public void TestStoredStableBloomFilterStillReads()
        {
            var f = StableBloomFilter.ReadFrom(Fixture("stablebloomfilter-v1.bin"));

            Assert.AreEqual(1000u, f.Cells());
            Assert.AreEqual(3u, f.K());
            Assert.AreEqual(35u, f.P());
            AssertAllPresent(f.Test);
        }

        /// <summary>
        /// The version 2 layout, which added the stored generator state in 6.0.0. The
        /// version 1 fixture above is not replaced by this one: both have to keep
        /// reading, which is the whole of what raising the version bought.
        /// </summary>
        [TestMethod]
        public void TestStoredStableBloomFilterAtVersionTwoStillReads()
        {
            var f = StableBloomFilter.ReadFrom(Fixture("stablebloomfilter-v2.bin"));

            Assert.AreEqual(1000u, f.Cells());
            Assert.AreEqual(3u, f.K());
            Assert.AreEqual(35u, f.P());
            AssertAllPresent(f.Test);
        }

        /// <summary>
        /// A filter written before the fingerprints were packed. It restores at eight
        /// bits per stored byte, which is not a compatibility mode but the same filter
        /// it always was: the fingerprint value is the same low bits of the same
        /// digest, and hashing it to find its partner bucket sees the same bytes.
        /// <para>
        /// Membership alone would not prove that. A filter that lost its width and
        /// answered true to everything would pass an all-present check, so this also
        /// pins the reconstructed width and requires the filter to still reject.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestStoredCuckooBloomFilterAtVersionTwoStillReads()
        {
            var f = CuckooBloomFilter.ReadFrom(Fixture("cuckoobloomfilter-v2.bin"));

            Assert.AreEqual(100u, f.Capacity());
            Assert.AreEqual(16u, f.FingerprintBits,
                "the stored filter recorded two bytes, so it must come back as a " +
                "sixteen-bit filter -- a narrower one would compute different " +
                "fingerprints for the same items and find none of them");
            AssertAllPresent(f.Test);

            var absent = 0;
            for (var i = 0; i < 500; i++)
            {
                if (!f.Test(Key($"never-added-{i}"))) absent++;
            }

            Assert.IsGreaterThan(400, absent,
                $"only {absent} of 500 unseen items were rejected. A restored filter " +
                "that says yes to everything would pass a membership check while " +
                "holding nothing.");
        }

        [TestMethod]
        public void TestStoredBinaryFuseFilterStillReads()
        {
            var f = BinaryFuseFilter.ReadFrom(Fixture("binaryfusefilter-v1.bin"));

            Assert.AreEqual(10u, f.Count());
            Assert.AreEqual(BinaryFuseWidth.Eight, f.Width());
            AssertAllPresent(f.Test);
        }

        /// <summary>
        /// Both of the estimator's representations, which are different payload layouts
        /// behind one structure id, so both have to keep reading.
        /// </summary>
        [TestMethod]
        public void TestStoredHyperLogLogPlusStillReads()
        {
            var sparse = HyperLogLogPlus.ReadFrom(Fixture("hyperloglogplus-sparse-v1.bin"));
            Assert.AreEqual(10ul, sparse.Count(), "the sparse form counts exactly");
            Assert.IsTrue(sparse.IsSparse);

            var dense = HyperLogLogPlus.ReadFrom(Fixture("hyperloglogplus-dense-v1.bin"));
            Assert.IsFalse(dense.IsSparse);
            Assert.AreEqual(14u, dense.Precision());

            // Within the nominal error for 2^14 registers over 50,000 items.
            Assert.IsLessThan(0.02, Math.Abs((double)dense.Count() - 50000) / 50000);
        }

        /// <summary>
        /// Loaded past its retained size, so the fixture pins the sampled case rather
        /// than the exact one -- theta itself has to come back, or every later estimate
        /// is scaled wrongly.
        /// </summary>
        [TestMethod]
        public void TestStoredBloomierFilterStillReads()
        {
            var filter = BloomierFilter.ReadFrom(Fixture("bloomierfilter-v1.bin"));

            Assert.AreEqual(10u, filter.Count());
            Assert.AreEqual(8, filter.ValueBits());

            // Each word maps to its position, so a map that read back its cells but lost
            // its seed would answer with the wrong values rather than with none.
            for (var i = 0; i < Words.Length; i++)
            {
                Assert.IsTrue(filter.TryGetValue(Key(Words[i]), out var value), Words[i]);
                Assert.AreEqual((ulong)i, value, $"{Words[i]} came back with the wrong value");
            }
        }

        [TestMethod]
        public void TestStoredInvertibleBloomLookupTableStillReads()
        {
            var table = InvertibleBloomLookupTable.ReadFrom(Fixture("iblt-v1.bin"));

            Assert.AreEqual(15u, table.Cells());
            Assert.AreEqual(8, table.KeySize());

            // The keys come back out, which is the whole point -- a table that read back
            // its counts but not its xored keys would decode to nothing and look fine.
            Assert.IsTrue(table.TryDecode(out var held, out var owed));
            Assert.HasCount(6, held);
            Assert.HasCount(0, owed);
        }

        [TestMethod]
        public void TestStoredCountSketchStillReads()
        {
            var sketch = CountSketch.ReadFrom(Fixture("countsketch-v1.bin"));

            Assert.AreEqual(100u, sketch.Width());
            Assert.AreEqual(5u, sketch.Depth());

            // Signed cells, which is the thing this sketch stores that a Count-Min one
            // does not, so a payload read back as unsigned would come out wrong here.
            Assert.AreEqual(-500L, sketch.Count(Key("removed")));
            Assert.AreEqual(300L, sketch.Count(Key("alpha")));
        }

        [TestMethod]
        public void TestStoredHeavyKeeperStillReads()
        {
            var keeper = HeavyKeeper.ReadFrom(Fixture("heavykeeper-v1.bin"));

            Assert.AreEqual(200UL, keeper.N);
            Assert.HasCount(5, keeper.Elements());

            // Twelve flows through thirty-two buckets: the ones that held on read
            // back at or near their true counts, and flow-4 -- evicted from every
            // bucket by the contest -- reads back as zero, which is this structure's
            // honest answer for the evicted. A reader that lost the fingerprints
            // would zero everyone; one that lost the counters would zero no one.
            Assert.AreEqual(17UL, keeper.Count(Key("flow-0")));
            Assert.AreEqual(17UL, keeper.Count(Key("flow-7")));
            Assert.AreEqual(16UL, keeper.Count(Key("flow-11")));
            Assert.AreEqual(0UL, keeper.Count(Key("flow-4")));
            Assert.AreEqual(1UL, keeper.Count(Key("flow-5")));
        }

        [TestMethod]
        public void TestStoredMementoFilterStillReads()
        {
            var filter = MementoFilter.ReadFrom(Fixture("mementofilter-v1.bin"));

            Assert.AreEqual(800UL, filter.Count());
            Assert.AreEqual(64UL, filter.MaxRangeSize);
            Assert.AreEqual(1024UL, filter.Capacity());

            // Keys nine apart, so both the keys and the gaps between them are known.
            for (ulong i = 0; i < 800; i++)
            {
                Assert.IsTrue(filter.Test(i * 9),
                    $"stored filter no longer holds {i * 9}");
            }

            // The eight positions between the first two keys are empty, and the
            // mementos are exact, so the filter says so rather than guessing.
            Assert.IsFalse(filter.TestRange(1, 8),
                "The gap between the first two keys reads as occupied, which means " +
                "the mementos did not survive the round trip.");
        }

        [TestMethod]
        public void TestStoredInfiniFilterStillReads()
        {
            var filter = InfiniFilter.ReadFrom(Fixture("infinifilter-v1.bin"));

            Assert.AreEqual(2000UL, filter.Count());
            Assert.AreEqual(7u, filter.ExpansionCount());

            // Two tables: four-bit fingerprints send the oldest entries down the
            // chain, so this fixture pins the multi-table layout rather than only the
            // simple case.
            Assert.AreEqual(2, filter.ChainLength());
            Assert.AreEqual(4224UL, filter.Capacity());

            // Every item still answers, including those that were moved between
            // tables -- a reader that lost the second table would fail here rather
            // than merely reading something.
            for (var i = 0; i < 2000; i++)
            {
                Assert.IsTrue(filter.Test(Key($"item-{i}")),
                    $"stored filter no longer holds item-{i}");
            }
        }

        [TestMethod]
        public void TestStoredTupleSketchStillReads()
        {
            var sketch = TupleSketch.ReadFrom(Fixture("tuplesketch-v1.bin"));

            Assert.AreEqual(SummaryPolicy.Sum, sketch.Policy);
            Assert.AreEqual(308U, sketch.Retained());

            // Five thousand distinct keys, each added four times at two apiece, so the
            // total should come out at eight times the count.
            Assert.AreEqual(5_000.0, sketch.Count(), 5_000 * 0.15,
                "the stored sketch no longer estimates the keys put into it");
            Assert.AreEqual(sketch.Count() * 8.0, sketch.Total(), 8.0,
                "the stored sketch's summaries no longer match its keys");
        }

        [TestMethod]
        public void TestStoredSetSketchStillReads()
        {
            var sketch = SetSketch.ReadFrom(Fixture("setsketch-v1.bin"));

            Assert.AreEqual(512, sketch.Registers);
            Assert.AreEqual(1.01, sketch.Base);
            Assert.AreEqual(20.0, sketch.Rate);
            Assert.AreEqual(40_000, sketch.MaxRegisterValue);

            // Twenty thousand elements, held to the error 512 registers implies.
            Assert.AreEqual(20_000.0, sketch.Cardinality(), 20_000 * 0.15,
                "the stored sketch no longer estimates what was put into it");

            // And it still compares: a sketch of the same elements is identical to it.
            var rebuilt = new SetSketch(512, 1.01, 20, 40_000);
            for (var i = 0; i < 20_000; i++)
            {
                rebuilt.Add(Key($"item-{i}"));
            }
            Assert.AreEqual(1.0, sketch.Jaccard(rebuilt),
                "the stored sketch disagrees with one built the same way now");
        }

        [TestMethod]
        public void TestStoredSublimeCountMinSketchStillReads()
        {
            var sketch = SublimeCountMinSketch.ReadFrom(
                Fixture("sublimecountminsketch-v1.bin"));

            Assert.AreEqual(128, sketch.Width);
            Assert.AreEqual(4, sketch.Depth);
            Assert.AreEqual(5500UL, sketch.TotalCount());

            // Forty flows, each added 150 times and the first five hundred additions
            // taken back out again.
            for (var i = 0; i < 40; i++)
            {
                Assert.IsTrue(sketch.Count(Key($"flow-{i}")) >= 137,
                    $"stored sketch reports {sketch.Count(Key($"flow-{i}"))} for " +
                    $"flow-{i}, below what was put in");
            }

            // The record of the expansion came back, so the sketch can still fold.
            for (var i = 0; i < 3000; i++)
            {
                sketch.Remove(Key($"flow-{i % 40}"));
            }
            Assert.AreEqual(64, sketch.Width,
                "the stored sketch no longer carries the record it needs to fold back");
        }

        [TestMethod]
        public void TestStoredGrafiteStillReads()
        {
            var filter = Grafite.ReadFrom(Fixture("grafite-v1.bin"));

            Assert.AreEqual(500UL, filter.Count());

            // Keys spaced 1009 apart, so the stored keys and the gaps between them
            // are both known exactly. A reader that lost the hash parameters, the
            // reduced universe or a word of the encoding would move these answers.
            Assert.IsTrue(filter.Test(0));
            Assert.IsTrue(filter.Test(1009));
            Assert.IsFalse(filter.Test(504500), "past the last key");
            Assert.IsFalse(filter.Test(1, 100), "a gap between two keys");
            Assert.IsTrue(filter.Test(2000, 2100), "a range holding the third key");

            // Not one of the 1008 non-keys below the second key reads as present, so
            // the filter's answers here are the keys themselves rather than noise.
            var positives = 0;
            for (ulong candidate = 1; candidate < 1009; candidate++)
            {
                if (filter.Test(candidate))
                {
                    positives++;
                }
            }
            Assert.AreEqual(0, positives);
        }

        [TestMethod]
        public void TestStoredUltraLogLogStillReads()
        {
            var sketch = UltraLogLog.ReadFrom(Fixture("ultraloglog-v1.bin"));

            Assert.AreEqual(8u, sketch.Precision());
            Assert.AreEqual(256u, sketch.M());

            // 10,000 distinct elements, estimated at this precision as 10,988. The
            // exact number is what matters here rather than its accuracy: it is a
            // function of every register in the payload, so a reader that dropped a
            // register, shifted the encoding, or mixed up the two low bits would
            // land somewhere else.
            Assert.AreEqual(10988UL, sketch.Count());
        }

        [TestMethod]
        public void TestStoredVarOptStillReads()
        {
            var sample = VarOpt.ReadFrom(Fixture("varopt-v1.bin"));

            Assert.AreEqual(202UL, sample.N);
            Assert.AreEqual(6u, sample.SampleCount);

            // The whole point of the structure is that this number is exact, so a
            // reader that rounded it, or that lost the threshold region's total,
            // would show up here rather than in a tolerance.
            Assert.AreEqual(1350.0, sample.TotalWeight, 0.0);

            // Two items outweighed the threshold and are held as themselves; the
            // other four share it. A reader that mixed the regions would report the
            // threshold for the whale, or 500 for a light item.
            var byName = sample.Samples().ToDictionary(
                e => Encoding.ASCII.GetString(e.Data.Span), e => e.Weight);

            Assert.AreEqual(500.0, byName["whale"], 0.0);
            Assert.AreEqual(250.0, byName["kraken"], 0.0);
            Assert.AreEqual(150.0, byName["flow-4"], 0.0);
            Assert.AreEqual(150.0, byName["flow-5"], 0.0);
            Assert.AreEqual(150.0, byName["flow-6"], 0.0);
            Assert.AreEqual(150.0, byName["flow-10"], 0.0);
        }

        [TestMethod]
        public void TestStoredSimHashSignatureStillReads()
        {
            var signature = SimHashSignature.ReadFrom(Fixture("simhashsignature-v1.bin"));

            // The fingerprint the current implementation computes for the same words, so
            // a change to the hash or the weighting fails here rather than in somebody's
            // stored index.
            Assert.AreEqual(SimHash.Signature(Words).Value, signature.Value);
            Assert.AreEqual(1f, SimHash.Similarity(signature, SimHash.Signature(Words)));
        }

        [TestMethod]
        public void TestStoredThetaSketchStillReads()
        {
            var sketch = ThetaSketch.ReadFrom(Fixture("thetasketch-v1.bin"));

            // It trims to 16 and then refills, so where it stops depends on the stream.
            Assert.AreEqual(30u, sketch.Retained());

            // 210 is what this sketch estimates for the 200 items it was given, and
            // pinning the estimate rather than the truth is the point: a theta that came
            // back wrong would scale this and nothing else would notice.
            Assert.AreEqual(210ul, sketch.Count());

            // And still combines with a sketch built now.
            var other = new ThetaSketch(16);
            for (var i = 100; i < 300; i++)
            {
                other.Add(Key($"w{i}"));
            }

            Assert.IsGreaterThan(0ul, sketch.Intersect(other).Count(),
                "the stored sketch shares nothing with a set it overlaps");
        }

        [TestMethod]
        public void TestStoredQuotientFilterStillReads()
        {
            var filter = QuotientFilter.ReadFrom(Fixture("quotientfilter-v1.bin"));

            Assert.AreEqual(10u, filter.Count());
            AssertAllPresent(filter.Test);

            // The metadata came back, not just the remainders: removing still finds the
            // right entry and leaves the rest of the cluster alone.
            Assert.IsTrue(filter.TestAndRemove(Key("alpha")));
            Assert.IsFalse(filter.Test(Key("alpha")));
            Assert.IsTrue(filter.Test(Key("beta")));
        }

        [TestMethod]
        public void TestStoredDDSketchStillReads()
        {
            var sketch = DDSketch.ReadFrom(Fixture("ddsketch-v1.bin"));

            Assert.AreEqual(100ul, sketch.Count());
            Assert.AreEqual(0.01, sketch.RelativeAccuracy());
            Assert.AreEqual(1.0, sketch.Min());
            Assert.AreEqual(100.0, sketch.Max());

            // The buckets came back, not just the totals: the median of 1..100 is 50,
            // within the accuracy the sketch was built with.
            Assert.IsLessThanOrEqualTo(0.01, Math.Abs(sketch.Quantile(0.5) - 50.0) / 50.0);
        }

        [TestMethod]
        public void TestStoredInverseBloomFilterStillReads()
        {
            var f = InverseBloomFilter.ReadFrom(Fixture("inversebloomfilter-v1.bin"));

            Assert.AreEqual(64u, f.Capacity());

            // Nine of the ten survived being written; the tenth was displaced by a
            // later word before the filter was ever stored, which is what this filter
            // does. Pinning the exact number keeps a restore that quietly loses one
            // more from passing.
            Assert.AreEqual(9, Words.Count(w => f.Test(Key(w))));
        }

        [TestMethod]
        public void TestStoredCuckooBloomFilterStillReads()
        {
            var f = CuckooBloomFilter.ReadFrom(Fixture("cuckoobloomfilter-v1.bin"));

            Assert.AreEqual(100u, f.Capacity());
            Assert.AreEqual(10u, f.Count());
            AssertAllPresent(f.Test);

            // Fingerprints landed where the relocation logic expects them, so it still
            // takes inserts.
            for (int i = 0; i < 50; i++)
            {
                f.Add(Key($"later{i}"));
            }
        }

        [TestMethod]
        public void TestStoredHyperLogLogStillReads()
        {
            var h = HyperLogLog.ReadFrom(Fixture("hyperloglog-v1.bin"));

            // b and alpha are derived on read rather than stored, so this pins that the
            // derivation still agrees with what wrote the registers.
            Assert.AreEqual(18ul, h.Count());
        }

        [TestMethod]
        public void TestStoredTopKStillReads()
        {
            var t = TopK.ReadFrom(Fixture("topk-v1.bin"));

            var elements = t.Elements()
                .Select(e => (Encoding.ASCII.GetString(e.Data.Span), e.Freq))
                .ToArray();

            CollectionAssert.AreEqual(
                new[] { ("t2", 3ul), ("t3", 4ul), ("t4", 5ul) }, elements);
        }

        [TestMethod]
        public void TestStoredMinHashSignatureStillReads()
        {
            var signature = MinHashSignature.ReadFrom(Fixture("minhashsignature-v1.bin"));

            Assert.AreEqual(16, signature.Length);
            Assert.AreEqual(31797598974978550ul, signature.Values[0]);
            Assert.AreEqual(661719492639586765ul, signature.Values[15]);

            // A stored signature has to still compare against one computed now, which
            // is the whole reason to store one.
            var recomputed = MinHash.Signature(
                new[] { "alpha", "beta", "gamma", "delta", "epsilon" }, 16);

            Assert.AreEqual(1.0f, MinHash.Similarity(signature, recomputed),
                "a signature stored by an earlier version no longer matches one " +
                "computed now for the same bag");
        }

        /// <summary>
        /// And that what this library writes today is still what it wrote then. The
        /// tests above would keep passing if writing changed and reading kept up with
        /// it; this pins the bytes themselves.
        /// <para>
        /// The stable and cuckoo filters are absent because neither is reproducible:
        /// one decrements randomly chosen cells and the other evicts a randomly chosen
        /// entry, from unseeded generators. Their payloads are still read above, which
        /// is the half of the promise that matters -- stored data keeps working.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestWritingStillProducesTheStoredBytes()
        {
            var bloom64 = new BloomFilter64(100, 0.01);
            var counting = new CountingBloomFilter(100, 4, 0.01);
            var deletable = new DeletableBloomFilter(100, 10, 0.01);
            var partitioned = new PartitionedBloomFilter(100, 0.01);
            var inverse = new InverseBloomFilter(64);

            foreach (var word in Words)
            {
                var key = Key(word);
                bloom64.Add(key);
                counting.Add(key);
                deletable.Add(key);
                partitioned.Add(key);
                inverse.Add(key);
            }

            AssertBytes("bloomfilter64-v1.bin", bloom64.ToByteArray());
            AssertBytes("countingbloomfilter-v1.bin", counting.ToByteArray());
            AssertBytes("deletablebloomfilter-v1.bin", deletable.ToByteArray());
            AssertBytes("partitionedbloomfilter-v1.bin", partitioned.ToByteArray());
            AssertBytes("inversebloomfilter-v1.bin", inverse.ToByteArray());

            var scalable = new ScalableBloomFilter(20, 0.01, 0.8);
            for (int i = 0; i < 100; i++)
            {
                scalable.Add(Key($"s{i}"));
            }

            AssertBytes("scalablebloomfilter-v1.bin", scalable.ToByteArray());

            var hll = new HyperLogLog(64);
            for (int i = 0; i < 20; i++)
            {
                hll.Add(Key($"h{i}"));
            }

            AssertBytes("hyperloglog-v1.bin", hll.ToByteArray());

            var topK = new TopK(0.1, 0.5, 3);
            for (int i = 0; i < 5; i++)
            {
                for (int j = 0; j <= i; j++)
                {
                    topK.Add(Key($"t{i}"));
                }
            }

            AssertBytes("topk-v1.bin", topK.ToByteArray());

            AssertBytes("minhashsignature-v1.bin", MinHash.Signature(
                new[] { "alpha", "beta", "gamma", "delta", "epsilon" }, 16).ToByteArray());

            // Seeded, so the generator's stored position is fixed and the whole payload
            // is reproducible. Neither of these could be pinned before 6.0.0: their
            // generators were unseeded and nothing they did reached the payload.
            var stable = new StableBloomFilter(1000, 2, 0.01, seed: 7);
            foreach (var word in Words)
            {
                stable.Add(Key(word));
            }

            AssertBytes("stablebloomfilter-v2.bin", stable.ToByteArray());

            // The build is deterministic -- a fixed seed sequence over a fixed set --
            // which is what lets its bytes be pinned at all.
            AssertBytes("binaryfusefilter-v1.bin",
                BinaryFuseFilter.Build(Words.Select(Key)).ToByteArray());

            var sketch = new DDSketch(0.01);
            for (var i = 1; i <= 100; i++)
            {
                sketch.Add(i);
            }

            AssertBytes("ddsketch-v1.bin", sketch.ToByteArray());

            var sparseHll = new HyperLogLogPlus(14);
            foreach (var word in Words)
            {
                sparseHll.Add(Key(word));
            }

            AssertBytes("hyperloglogplus-sparse-v1.bin", sparseHll.ToByteArray());

            var quotient = new QuotientFilter(100, 0.01);
            foreach (var word in Words)
            {
                quotient.Add(Key(word));
            }

            AssertBytes("quotientfilter-v1.bin", quotient.ToByteArray());

            var theta = new ThetaSketch(16);
            for (var i = 0; i < 200; i++)
            {
                theta.Add(Key($"w{i}"));
            }

            // v1b, not v1: fixing the trim-boundary defect (an Add whose compaction
            // lowered theta past the pending hash still stored it) changed the state
            // this stream produces -- transient out-of-range values altered when later
            // compactions fired. Same format, second writer generation. The v1 fixture
            // stays for the read-side test: bytes 6.0.0 wrote must load forever.
            AssertBytes("thetasketch-v1b.bin", theta.ToByteArray());

            AssertBytes("simhashsignature-v1.bin", SimHash.Signature(Words).ToByteArray());

            var countSketch = new CountSketch(0.1, 0.01);
            countSketch.Add(Key("alpha"), 300);
            countSketch.Add(Key("removed"), -500);

            AssertBytes("countsketch-v1.bin", countSketch.ToByteArray());

            var keeper = new HeavyKeeper(5, 32, depth: 2, decay: 1.08, seed: 99);
            for (var i = 0; i < 200; i++)
            {
                keeper.Add(Key($"flow-{i % 12}"));
            }
            AssertBytes("heavykeeper-v1.bin", keeper.ToByteArray());

            var memento = new MementoFilter(
                maxRangeSize: 64, fingerprintBits: 8, initialCapacity: 16);
            for (ulong i = 0; i < 800; i++)
            {
                memento.Add(i * 9);
            }

            AssertBytes("mementofilter-v1.bin", memento.ToByteArray());

            var infini = new InfiniFilter(initialCapacity: 16, fingerprintBits: 4);
            for (var i = 0; i < 2000; i++)
            {
                infini.Add(Key($"item-{i}"));
            }

            AssertBytes("infinifilter-v1.bin", infini.ToByteArray());

            var tuple = new TupleSketch(256);
            for (var i = 0; i < 20_000; i++)
            {
                tuple.Add(Key($"user-{i % 5000}"), 2.0);
            }

            AssertBytes("tuplesketch-v1.bin", tuple.ToByteArray());

            var setSketch = new SetSketch(512, 1.01, 20, 40_000);
            for (var i = 0; i < 20_000; i++)
            {
                setSketch.Add(Key($"item-{i}"));
            }

            AssertBytes("setsketch-v1.bin", setSketch.ToByteArray());

            var sublime = new SublimeCountMinSketch(0.02);
            for (var i = 0; i < 6000; i++)
            {
                sublime.Add(Key($"flow-{i % 40}"));
            }
            for (var i = 0; i < 500; i++)
            {
                sublime.Remove(Key($"flow-{i % 40}"));
            }

            AssertBytes("sublimecountminsketch-v1.bin", sublime.ToByteArray());

            AssertBytes("grafite-v1.bin", Grafite.Build(
                Enumerable.Range(0, 500).Select(i => (ulong)(i * 1009)),
                0.02, 32, seed: 2024).ToByteArray());

            var ultra = new UltraLogLog(8);
            for (var i = 0; i < 10000; i++)
            {
                ultra.Add(Key($"element-{i}"));
            }

            AssertBytes("ultraloglog-v1.bin", ultra.ToByteArray());

            var varopt = new VarOpt(6, seed: 99);
            for (var i = 0; i < 200; i++)
            {
                varopt.Add(Key($"flow-{i % 12}"), 1.0 + (i % 5));
            }
            varopt.Add(Key("whale"), 500.0);
            varopt.Add(Key("kraken"), 250.0);

            AssertBytes("varopt-v1.bin", varopt.ToByteArray());

            var iblt = new InvertibleBloomLookupTable(10, 8);
            for (var i = 0; i < 6; i++)
            {
                var key = new byte[8];
                key[0] = (byte)i;
                iblt.Add(key);
            }

            AssertBytes("iblt-v1.bin", iblt.ToByteArray());

            AssertBytes("bloomierfilter-v1.bin", BloomierFilter.Build(
                Words.Select((w, i) => new System.Collections.Generic.KeyValuePair<byte[], ulong>(Key(w), (ulong)i)),
                8).ToByteArray());

            var denseHll = new HyperLogLogPlus(14);
            for (var i = 0; i < 50000; i++)
            {
                denseHll.Add(Key($"n{i}"));
            }

            AssertBytes("hyperloglogplus-dense-v1.bin", denseHll.ToByteArray());

            // The words go in first so they are all findable, then a load heavy enough
            // to make buckets collide and the relocation path run -- but short of
            // saturating the filter, which would refuse inserts rather than relocate.
            // The stored state is 22 draws along, so it pins a generator that moved
            // rather than one still sitting at its seed.
            var cuckoo = new CuckooBloomFilter(100, 0.01, seed: 7);
            foreach (var word in Words)
            {
                cuckoo.Add(Key(word));
            }

            for (int i = 0; i < 100; i++)
            {
                cuckoo.Add(Key($"w{i}"));
            }

            // v3, not v2: packing the fingerprints at their exact bit width changed
            // both the width recorded and the bytes each entry occupies. The v2
            // fixture stays for the read-side test -- bytes 6.0.1 wrote must load
            // forever, and they restore at eight bits per stored byte, which is the
            // same filter they always were.
            AssertBytes("cuckoobloomfilter-v3.bin", cuckoo.ToByteArray());
        }

        /// <summary>
        /// The structure id in every stored payload is the one the format assigns, which
        /// is what stops a payload being read as the wrong structure. Reading the byte
        /// directly rather than through the enum, so that renumbering the enum shows up
        /// here rather than passing quietly.
        /// </summary>
        [TestMethod]
        public void TestStoredStructureIdsAreUnchanged()
        {
            var expected = new (string Fixture, int Id, int Version)[]
            {
                ("bloomfilter-v1.bin", 1, 1),
                ("bloomfilter64-v1.bin", 2, 1),
                ("countingbloomfilter-v1.bin", 3, 1),
                ("deletablebloomfilter-v1.bin", 4, 1),
                ("partitionedbloomfilter-v1.bin", 5, 1),
                ("scalablebloomfilter-v1.bin", 6, 1),
                ("stablebloomfilter-v1.bin", 7, 1),
                ("inversebloomfilter-v1.bin", 8, 1),
                ("cuckoobloomfilter-v1.bin", 9, 1),
                ("countminsketch-v1.bin", 10, 1),
                ("hyperloglog-v1.bin", 11, 1),
                ("topk-v1.bin", 12, 1),
                ("minhashsignature-v1.bin", 13, 1),
                ("stablebloomfilter-v2.bin", 7, 2),
                ("cuckoobloomfilter-v2.bin", 9, 2),
                ("cuckoobloomfilter-v3.bin", 9, 3),
                ("binaryfusefilter-v1.bin", 14, 1),
                ("ddsketch-v1.bin", 15, 1),
                ("hyperloglogplus-sparse-v1.bin", 16, 1),
                ("hyperloglogplus-dense-v1.bin", 16, 1),
                ("quotientfilter-v1.bin", 17, 1),
                ("thetasketch-v1.bin", 18, 1),
                ("thetasketch-v1b.bin", 18, 1),
                ("simhashsignature-v1.bin", 19, 1),
                ("countsketch-v1.bin", 20, 1),
                ("iblt-v1.bin", 21, 1),
                ("bloomierfilter-v1.bin", 22, 1),
                ("heavykeeper-v1.bin", 23, 1),
                ("varopt-v1.bin", 24, 1),
                ("ultraloglog-v1.bin", 25, 1),
                ("grafite-v1.bin", 26, 1),
                ("infinifilter-v1.bin", 27, 1),
                ("mementofilter-v1.bin", 28, 1),
            };

            foreach (var (fixture, id, version) in expected)
            {
                var bytes = ReadFixture(fixture);
                Assert.AreEqual(id, bytes[6] | (bytes[7] << 8),
                    $"{fixture} no longer carries structure id {id}");
                Assert.AreEqual(version, bytes[4] | (bytes[5] << 8),
                    $"{fixture} is no longer format version {version}");
            }

            // The version travels per payload, so the eleven structures that did not
            // change layout must still be writing version 1. Raising all of them
            // together would have made every payload unreadable to 5.x to record a
            // change that eleven of them did not have.
            Assert.AreEqual(1, new BloomFilter(100, 0.01).ToByteArray()[4],
                "an unchanged structure started writing a later format version");
            Assert.AreEqual(2, new StableBloomFilter(100, 2, 0.01).ToByteArray()[4],
                "the stable filter is not writing the version its layout needs");
        }

        private static void AssertAllPresent(Func<byte[], bool> test)
        {
            foreach (var word in Words)
            {
                Assert.IsTrue(test(Key(word)), $"the stored structure no longer finds {word}");
            }
        }

        private static void AssertBytes(string fixture, byte[] written)
        {
            CollectionAssert.AreEqual(ReadFixture(fixture), written,
                $"the bytes written for {fixture} have changed since the format was introduced");
        }

        private static Stream Fixture(string name)
        {
            return new MemoryStream(ReadFixture(name), writable: false);
        }

        private static byte[] ReadFixture(string name)
        {
            using var resource = typeof(TestPersistenceFormatStability).Assembly
                .GetManifestResourceStream($"TestProbabilisticDataStructures.fixtures.{name}")
                ?? throw new InvalidOperationException($"fixture {name} is not embedded");

            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            return buffer.ToArray();
        }
    }
}
