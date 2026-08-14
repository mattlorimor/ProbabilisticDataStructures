using System;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The hash a structure uses is settled when it is built. Replacing it afterwards
    /// is refused, because everything already stored was placed by the old one and
    /// replacing it moves none of it.
    /// </summary>
    [TestClass]
    public class TestHashConfiguration
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        /// <summary>
        /// A different hash from the default, so that a structure built with it differs
        /// from one that ignored it.
        /// </summary>
        private static ulong Alternate(ReadOnlySpan<byte> data)
        {
            return System.IO.Hashing.XxHash64.HashToUInt64(data);
        }

        /// <summary>
        /// The hash a payload records: 0 for one the caller supplied, 1 for the default.
        /// Reading it out of the header is an exact check that a structure is holding
        /// the hash it was given, rather than an inference from how it behaves.
        /// </summary>
        private static int RecordedHashId(byte[] payload) => payload[8] | (payload[9] << 8);

        private const int Custom = 0;
        private const int Default = 1;

        [TestMethod]
        public void TestEveryStructureUsesAHashGivenToItsConstructor()
        {
            Assert.AreEqual(Custom, RecordedHashId(new BloomFilter(100, 0.01, Alternate).ToByteArray()));
            Assert.AreEqual(Custom, RecordedHashId(new BloomFilter64(100, 0.01, Alternate).ToByteArray()));
            Assert.AreEqual(Custom, RecordedHashId(new CountingBloomFilter(100, 4, 0.01, Alternate).ToByteArray()));
            Assert.AreEqual(Custom, RecordedHashId(new DeletableBloomFilter(100, 10, 0.01, Alternate).ToByteArray()));
            Assert.AreEqual(Custom, RecordedHashId(new PartitionedBloomFilter(100, 0.01, Alternate).ToByteArray()));
            Assert.AreEqual(Custom, RecordedHashId(new StableBloomFilter(100, 2, 0.01, Alternate).ToByteArray()));
            Assert.AreEqual(Custom, RecordedHashId(new InverseBloomFilter(64, Alternate).ToByteArray()));
            Assert.AreEqual(Custom, RecordedHashId(new CuckooBloomFilter(100, 0.01, Alternate).ToByteArray()));
            Assert.AreEqual(Custom, RecordedHashId(new CountMinSketch(0.01, 0.01, Alternate).ToByteArray()));
            Assert.AreEqual(Custom, RecordedHashId(new HyperLogLog(64, Alternate).ToByteArray()));

            // The composites have to pass it down to what they hold.
            Assert.AreEqual(Custom, RecordedHashId(new ScalableBloomFilter(50, 0.01, 0.8, Alternate).ToByteArray()));
            Assert.AreEqual(Custom, RecordedHashId(new TopK(0.01, 0.01, 5, Alternate).ToByteArray()));
        }

        [TestMethod]
        public void TestOmittingTheHashStillGivesTheDefault()
        {
            Assert.AreEqual(Default, RecordedHashId(new BloomFilter(100, 0.01).ToByteArray()));
            Assert.AreEqual(Default, RecordedHashId(new ScalableBloomFilter(50, 0.01, 0.8).ToByteArray()));
            Assert.AreEqual(Default, RecordedHashId(new TopK(0.01, 0.01, 5).ToByteArray()));
        }

        /// <summary>
        /// A scalable filter adds filters as it grows, long after its constructor has
        /// run, so the hash has to be carried rather than only applied at the start.
        /// </summary>
        [TestMethod]
        public void TestScalableFilterGivesItsHashToFiltersAddedLater()
        {
            var f = new ScalableBloomFilter(20, 0.01, 0.8, Alternate);
            for (int i = 0; i < 500; i++)
            {
                f.Add(Key($"item-{i}"));
            }

            Assert.IsGreaterThan(1, f.Filters.Count, "the filter should have grown");
            foreach (var contained in f.Filters)
            {
                Assert.AreEqual(Custom, RecordedHashId(contained.ToByteArray()),
                    "a filter added while growing did not inherit the hash");
            }

            // And still finds everything, which it would not if a later filter had been
            // built with a different hash from the one its contents were added under.
            for (int i = 0; i < 500; i++)
            {
                Assert.IsTrue(f.Test(Key($"item-{i}")));
            }
        }

        /// <summary>
        /// The defect this closes: replacing the hash of a populated filter left it
        /// reporting items it could no longer find. 500 added, 500 reported, none
        /// findable, and nothing raised.
        /// </summary>
        [TestMethod]
        public void TestReplacingTheHashOfAPopulatedStructureIsRefused()
        {
            var bloom = new BloomFilter(1000, 0.01);
            for (int i = 0; i < 500; i++)
            {
                bloom.Add(Key($"item-{i}"));
            }

            Assert.ThrowsExactly<InvalidOperationException>(() => bloom.SetHash(Alternate));

            // And is left as it was, rather than half converted.
            Assert.AreEqual(500, Enumerable.Range(0, 500).Count(i => bloom.Test(Key($"item-{i}"))));
            Assert.AreEqual(500u, bloom.Count());
        }

        [TestMethod]
        public void TestEveryStructureRefusesToReplaceThHashOnceItHoldsSomething()
        {
            var bloom = new BloomFilter(100, 0.01); bloom.Add(Key("a"));
            var bloom64 = new BloomFilter64(100, 0.01); bloom64.Add(Key("a"));
            var counting = new CountingBloomFilter(100, 4, 0.01); counting.Add(Key("a"));
            var deletable = new DeletableBloomFilter(100, 10, 0.01); deletable.Add(Key("a"));
            var partitioned = new PartitionedBloomFilter(100, 0.01); partitioned.Add(Key("a"));
            var stable = new StableBloomFilter(100, 2, 0.01); stable.Add(Key("a"));
            var inverse = new InverseBloomFilter(64); inverse.Add(Key("a"));
            var cuckoo = new CuckooBloomFilter(100, 0.01); cuckoo.Add(Key("a"));
            var sketch = new CountMinSketch(0.01, 0.01); sketch.Add(Key("a"));
            var hll = new HyperLogLog(64); hll.Add(Key("a"));
            var scalable = new ScalableBloomFilter(50, 0.01, 0.8); scalable.Add(Key("a"));

            Assert.ThrowsExactly<InvalidOperationException>(() => bloom.SetHash(Alternate));
            Assert.ThrowsExactly<InvalidOperationException>(() => bloom64.SetHash(Alternate));
            Assert.ThrowsExactly<InvalidOperationException>(() => counting.SetHash(Alternate));
            Assert.ThrowsExactly<InvalidOperationException>(() => deletable.SetHash(Alternate));
            Assert.ThrowsExactly<InvalidOperationException>(() => partitioned.SetHash(Alternate));
            Assert.ThrowsExactly<InvalidOperationException>(() => stable.SetHash(Alternate));
            Assert.ThrowsExactly<InvalidOperationException>(() => inverse.SetHash(Alternate));
            Assert.ThrowsExactly<InvalidOperationException>(() => cuckoo.SetHash(Alternate));
            Assert.ThrowsExactly<InvalidOperationException>(() => sketch.SetHash(Alternate));
            Assert.ThrowsExactly<InvalidOperationException>(() => hll.SetHash(Alternate));
            Assert.ThrowsExactly<InvalidOperationException>(() => scalable.SetHash(Alternate));
        }

        /// <summary>
        /// Still allowed while a structure holds nothing, which is the case the old API
        /// was always safe for.
        /// </summary>
        [TestMethod]
        public void TestReplacingTheHashOfAnEmptyStructureIsAllowed()
        {
            var bloom = new BloomFilter(100, 0.01);
            bloom.SetHash(Alternate);
            bloom.Add(Key("a"));
            Assert.IsTrue(bloom.Test(Key("a")));
            Assert.AreEqual(Custom, RecordedHashId(bloom.ToByteArray()));

            // Emptied structures count as empty, so a filter can be repurposed.
            var reset = new BloomFilter(100, 0.01);
            reset.Add(Key("a"));
            reset.Reset();
            reset.SetHash(Alternate);
            Assert.AreEqual(Custom, RecordedHashId(reset.ToByteArray()));
        }

        /// <summary>
        /// Emptiness is derived from what a structure holds rather than tracked, so a
        /// structure read back from a payload answers it the way the one that wrote it
        /// would rather than looking untouched.
        /// </summary>
        [TestMethod]
        public void TestARestoredStructureIsNotTreatedAsEmpty()
        {
            var stable = new StableBloomFilter(1000, 2, 0.01);
            stable.Add(Key("a"));
            var restoredStable = Persistence.FromByteArray<StableBloomFilter>(stable.ToByteArray());
            Assert.ThrowsExactly<InvalidOperationException>(() => restoredStable.SetHash(Alternate));

            var inverse = new InverseBloomFilter(64);
            inverse.Add(Key("a"));
            var restoredInverse = Persistence.FromByteArray<InverseBloomFilter>(inverse.ToByteArray());
            Assert.ThrowsExactly<InvalidOperationException>(() => restoredInverse.SetHash(Alternate));

            var hll = new HyperLogLog(64);
            hll.Add(Key("a"));
            var restoredHll = Persistence.FromByteArray<HyperLogLog>(hll.ToByteArray());
            Assert.ThrowsExactly<InvalidOperationException>(() => restoredHll.SetHash(Alternate));

            var bloom = new BloomFilter(100, 0.01);
            bloom.Add(Key("a"));
            var restoredBloom = Persistence.FromByteArray<BloomFilter>(bloom.ToByteArray());
            Assert.ThrowsExactly<InvalidOperationException>(() => restoredBloom.SetHash(Alternate));
        }

        [TestMethod]
        public void TestSetHashRejectsNull()
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => new BloomFilter(100, 0.01).SetHash(null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => new ScalableBloomFilter(50, 0.01, 0.8).SetHash(null!));
        }
    }
}
