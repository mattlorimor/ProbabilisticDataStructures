using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Two structures here make random choices as part of what they are: the stable
    /// filter decrements randomly chosen cells to make room, and the cuckoo filter
    /// evicts a randomly chosen entry when both of an item's buckets are full. Both can
    /// now be seeded, which is what makes their behavior assertable at all rather than
    /// only describable.
    /// </summary>
    [TestClass]
    public class TestSeededRandomness
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        private static string Fingerprint<T>(T structure) where T : IBinaryPersistable<T>
        {
            return Convert.ToHexString(
                System.IO.Hashing.XxHash3.Hash(structure.ToByteArray()));
        }

        private static StableBloomFilter FilledStable(int? seed)
        {
            var f = new StableBloomFilter(1000, 2, 0.01, seed: seed);
            for (int i = 0; i < 3000; i++)
            {
                f.Add(Key($"w{i}"));
            }

            return f;
        }

        /// <summary>
        /// Enough items that both of some item's buckets fill and the eviction path
        /// runs. A load near the stated capacity never reaches the random choice at all,
        /// so this goes well past it: 100 items sizes the filter at 32 buckets of 4, and
        /// the evictions start once those 128 entries begin to collide.
        /// </summary>
        private static CuckooBloomFilter FilledCuckoo(int? seed)
        {
            var f = new CuckooBloomFilter(100, 0.01, seed: seed);
            for (int i = 0; i < 3000; i++)
            {
                f.Add(Key($"w{i}"));
            }

            return f;
        }

        [TestMethod]
        public void TestTheSameSeedGivesTheSameStableFilter()
        {
            Assert.AreEqual(Fingerprint(FilledStable(7)), Fingerprint(FilledStable(7)));
        }

        [TestMethod]
        public void TestADifferentSeedGivesADifferentStableFilter()
        {
            Assert.AreNotEqual(Fingerprint(FilledStable(7)), Fingerprint(FilledStable(8)),
                "the seed made no difference, so the random decrements are not being seeded");
        }

        [TestMethod]
        public void TestTheSameSeedGivesTheSameCuckooFilter()
        {
            Assert.AreEqual(Fingerprint(FilledCuckoo(7)), Fingerprint(FilledCuckoo(7)));
        }

        [TestMethod]
        public void TestADifferentSeedGivesADifferentCuckooFilter()
        {
            Assert.AreNotEqual(Fingerprint(FilledCuckoo(7)), Fingerprint(FilledCuckoo(8)),
                "the seed made no difference, so the eviction choice is not being seeded");
        }

        /// <summary>
        /// Leaving the seed out still gives an unpredictable filter, so seeding is a
        /// thing a caller asks for rather than something they now have by default.
        /// </summary>
        [TestMethod]
        public void TestOmittingTheSeedLeavesTheFilterUnpredictable()
        {
            var fingerprints = Enumerable.Range(0, 4)
                .Select(_ => Fingerprint(FilledStable(null)))
                .Distinct()
                .Count();

            Assert.IsGreaterThan(1, fingerprints,
                "four unseeded filters over 3000 adds came out identical");
        }

        /// <summary>
        /// A seeded filter still behaves like the filter it is. Seeding fixes which
        /// cells are decremented, not whether the decay happens.
        /// </summary>
        [TestMethod]
        public void TestSeedingDoesNotChangeWhatTheFilterDoes()
        {
            var seeded = FilledStable(7);
            var unseeded = FilledStable(null);

            // The stable filter forgets: after 3000 adds into 1000 cells, the earliest
            // items are gone from both, and the most recent are in both.
            Assert.IsFalse(seeded.Test(Key("w0")), "seeded filter should have forgotten w0");
            Assert.IsFalse(unseeded.Test(Key("w0")), "unseeded filter should have forgotten w0");
            Assert.IsTrue(seeded.Test(Key("w2999")), "seeded filter should hold the newest item");
            Assert.IsTrue(unseeded.Test(Key("w2999")), "unseeded filter should hold the newest item");

            // Their stable points agree, being a function of the parameters rather than
            // of the choices made along the way.
            Assert.AreEqual(unseeded.StablePoint(), seeded.StablePoint());
        }

        /// <summary>
        /// And a seeded cuckoo filter keeps its guarantee: everything it accepted is
        /// still findable, however the evictions fell out.
        /// </summary>
        [TestMethod]
        public void TestASeededCuckooFilterStillFindsWhatItAccepted()
        {
            var f = new CuckooBloomFilter(100, 0.01, seed: 7);

            var accepted = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 3000; i++)
            {
                if (f.Add(Key($"w{i}")))
                {
                    accepted.Add($"w{i}");
                }
            }

            Assert.IsGreaterThan(0, accepted.Count);
            foreach (var word in accepted)
            {
                Assert.IsTrue(f.Test(Key(word)), $"{word} was accepted and cannot be found");
            }
        }
        /// <summary>
        /// A filter that is written out and read back resumes the draw sequence it was
        /// partway through, rather than starting it over.
        /// <para>
        /// Storing the seed alone would not do this. The bits come back correct either
        /// way, but a filter re-seeded on read sits at the start of the sequence while
        /// the original sits wherever its adds left it, so the two answer differently
        /// from then on. What makes that worth guarding rather than tolerating is the
        /// case persistence exists for: a filter checkpointed on a schedule would replay
        /// the same first draws after every load, and the stable filter's bound on its
        /// false positive rate assumes its decay is spread across cells rather than
        /// aimed at the same ones after every restart.
        /// </para>
        /// <para>
        /// Asserted against a filter that was never written out, because two filters
        /// restored from the same payload agree under either design -- that comparison
        /// is the one this defect passes.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestARestoredStableFilterResumesItsDrawSequence()
        {
            var original = FilledStable(seed: 42);
            var restored = Persistence.FromByteArray<StableBloomFilter>(original.ToByteArray());

            Assert.AreEqual(Fingerprint(original), Fingerprint(restored),
                "the restore differed before either filter was used again");

            for (int i = 3000; i < 9000; i++)
            {
                var key = Key($"w{i}");
                original.Add(key);
                restored.Add(key);
            }

            Assert.AreEqual(Fingerprint(original), Fingerprint(restored),
                "the restored filter decayed different cells from the original");
        }

        [TestMethod]
        public void TestARestoredCuckooFilterResumesItsDrawSequence()
        {
            var original = FilledCuckoo(seed: 42);
            var restored = Persistence.FromByteArray<CuckooBloomFilter>(original.ToByteArray());

            Assert.AreEqual(Fingerprint(original), Fingerprint(restored),
                "the restore differed before either filter was used again");

            for (int i = 3000; i < 9000; i++)
            {
                var key = Key($"w{i}");
                original.Add(key);
                restored.Add(key);
            }

            Assert.AreEqual(Fingerprint(original), Fingerprint(restored),
                "the restored filter evicted different entries from the original");
        }

        /// <summary>
        /// The case above, run the way it would actually arise: a long-lived filter
        /// checkpointed repeatedly rather than round-tripped once. A filter re-seeded on
        /// each read would draw the same numbers after every one of these loads.
        /// </summary>
        [TestMethod]
        public void TestCheckpointingRepeatedlyDoesNotReplayTheSameDraws()
        {
            var uninterrupted = FilledStable(seed: 42);
            var checkpointed = FilledStable(seed: 42);

            for (int round = 0; round < 20; round++)
            {
                // Save and reload between every batch, as a long-running process would.
                checkpointed = Persistence.FromByteArray<StableBloomFilter>(
                    checkpointed.ToByteArray());

                for (int i = 0; i < 500; i++)
                {
                    var key = Key($"r{round}-{i}");
                    uninterrupted.Add(key);
                    checkpointed.Add(key);
                }
            }

            Assert.AreEqual(Fingerprint(uninterrupted), Fingerprint(checkpointed),
                "twenty checkpoints left the filter somewhere the uninterrupted one never went");
        }

        /// <summary>
        /// A payload written before 6.0.0 carries no generator state, and is read rather
        /// than refused: every cell is exactly right, and only the sequence of cells the
        /// filter will decay next is unrecoverable, because it was never written down.
        /// </summary>
        [TestMethod]
        public void TestAPayloadWithoutAStoredGeneratorStateStillReads()
        {
            var v1 = ReadFixture("stablebloomfilter-v1.bin");
            Assert.AreEqual(1, v1[4] | (v1[5] << 8), "fixture is no longer a version 1 payload");

            var f = Persistence.FromByteArray<StableBloomFilter>(v1);
            Assert.AreEqual(1000u, f.Cells());

            // And it keeps working, which is the part that needs a generator at all.
            for (int i = 0; i < 2000; i++)
            {
                f.Add(Key($"later{i}"));
            }
        }

        private static byte[] ReadFixture(string name)
        {
            using var stream = typeof(TestSeededRandomness).Assembly.GetManifestResourceStream(
                $"TestProbabilisticDataStructures.fixtures.{name}")
                ?? throw new InvalidOperationException($"fixture {name} is not embedded");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }

    }
}
