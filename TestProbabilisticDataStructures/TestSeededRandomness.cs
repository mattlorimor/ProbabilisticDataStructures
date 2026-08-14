using System;
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
        /// runs. The filter is sized far more generously than its stated capacity --
        /// 100 items gives 1024 buckets of 4 -- so a load near the stated figure never
        /// reaches the random choice at all.
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
    }
}
