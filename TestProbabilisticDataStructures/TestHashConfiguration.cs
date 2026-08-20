using System;
using System.Collections.Generic;
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

        /// <summary>
        /// A hash handed to a structure as it is built is the one it holds, and the one
        /// its payload names. For several structures here, construction is the only
        /// place a hash can be installed at all: they expose no SetHash.
        /// </summary>
        [TestMethod]
        public void TestEveryStructureUsesAHashGivenToItsConstructor()
        {
            var built = new (Type Type, Func<byte[]> Payload)[]
            {
                (typeof(BloomFilter), () => new BloomFilter(100, 0.01, Alternate).ToByteArray()),
                (typeof(BloomFilter64), () => new BloomFilter64(100, 0.01, Alternate).ToByteArray()),
                (typeof(CountingBloomFilter), () => new CountingBloomFilter(100, 4, 0.01, Alternate).ToByteArray()),
                (typeof(DeletableBloomFilter), () => new DeletableBloomFilter(100, 10, 0.01, Alternate).ToByteArray()),
                (typeof(PartitionedBloomFilter), () => new PartitionedBloomFilter(100, 0.01, Alternate).ToByteArray()),
                (typeof(StableBloomFilter), () => new StableBloomFilter(100, 2, 0.01, Alternate).ToByteArray()),
                (typeof(InverseBloomFilter), () => new InverseBloomFilter(64, Alternate).ToByteArray()),
                (typeof(CuckooBloomFilter), () => new CuckooBloomFilter(100, 0.01, Alternate).ToByteArray()),
                (typeof(CountMinSketch), () => new CountMinSketch(0.01, 0.01, Alternate).ToByteArray()),
                (typeof(HyperLogLog), () => new HyperLogLog(64, Alternate).ToByteArray()),
                (typeof(CountSketch), () => new CountSketch(0.05, 0.01, Alternate).ToByteArray()),
                (typeof(HyperLogLogPlus), () => new HyperLogLogPlus(14, Alternate).ToByteArray()),
                (typeof(QuotientFilter), () => new QuotientFilter(100, 0.01, Alternate).ToByteArray()),
                (typeof(UltraLogLog), () => new UltraLogLog(10, Alternate).ToByteArray()),
                (typeof(InfiniFilter), () => new InfiniFilter(64, 8, Alternate).ToByteArray()),
                (typeof(SetSketch), () => new SetSketch(8, Alternate).ToByteArray()),
                (typeof(SublimeCountMinSketch), () => new SublimeCountMinSketch(
                    0.5, 0.5, ValeCounterArray.DefaultCountersPerChunk, Alternate).ToByteArray()),
                (typeof(HeavyKeeper), () => new HeavyKeeper(10, 64, seed: 1, hash: Alternate).ToByteArray()),
                (typeof(PrivateCountMinSketch),
                    () => new PrivateCountMinSketch(8, 2, 0.5, seed: 1, hash: Alternate).ToByteArray()),
                (typeof(DpswSketch), () => new DpswSketch(
                    window: 16, rho: 4.0, alpha: 0.6, width: 4, depth: 2,
                    seed: 3, hash: Alternate).ToByteArray()),
                (typeof(BinaryFuseFilter), () => BinaryFuseFilter.Build(
                    new[] { Key("a"), Key("b"), Key("c") }, BinaryFuseWidth.Eight, Alternate).ToByteArray()),
                (typeof(BloomierFilter), () => BloomierFilter.Build(
                    new[] { new KeyValuePair<byte[], ulong>(Key("a"), 1UL) }, 8, Alternate).ToByteArray()),
                (typeof(InvertibleBloomLookupTable),
                    () => new InvertibleBloomLookupTable(10, 8, Alternate).ToByteArray()),

                // The composites have to pass it down to what they hold.
                (typeof(ScalableBloomFilter), () => new ScalableBloomFilter(50, 0.01, 0.8, Alternate).ToByteArray()),
                (typeof(TopK), () => new TopK(0.01, 0.01, 5, Alternate).ToByteArray()),
            };

            foreach (var (type, payload) in built)
            {
                Assert.AreEqual(Custom, RecordedHashId(payload()),
                    $"a {type.Name} built with a hash of its own recorded the default instead");
            }

            StructureRoster.AssertCoversEveryType(
                "constructor hash",
                StructureRoster.TakingAHashWhenBuilt,
                built.Select(b => b.Type));
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

        /// <summary>
        /// Once a structure holds anything, its hash is settled: everything already in
        /// it was placed by the old one, and replacing it moves none of it.
        /// </summary>
        [TestMethod]
        public void TestEveryStructureRefusesToReplaceTheHashOnceItHoldsSomething()
        {
            var occupied = new (Type Type, Action Replace)[]
            {
                Holding(new BloomFilter(100, 0.01), f => f.Add(Key("a")), f => f.SetHash(Alternate)),
                Holding(new BloomFilter64(100, 0.01), f => f.Add(Key("a")), f => f.SetHash(Alternate)),
                Holding(new CountingBloomFilter(100, 4, 0.01), f => f.Add(Key("a")), f => f.SetHash(Alternate)),
                Holding(new DeletableBloomFilter(100, 10, 0.01), f => f.Add(Key("a")), f => f.SetHash(Alternate)),
                Holding(new PartitionedBloomFilter(100, 0.01), f => f.Add(Key("a")), f => f.SetHash(Alternate)),
                Holding(new StableBloomFilter(100, 2, 0.01), f => f.Add(Key("a")), f => f.SetHash(Alternate)),
                Holding(new InverseBloomFilter(64), f => f.Add(Key("a")), f => f.SetHash(Alternate)),
                Holding(new CuckooBloomFilter(100, 0.01), f => f.Add(Key("a")), f => f.SetHash(Alternate)),
                Holding(new CountMinSketch(0.01, 0.01), f => f.Add(Key("a")), f => f.SetHash(Alternate)),
                Holding(new HyperLogLog(64), f => f.Add(Key("a")), f => f.SetHash(Alternate)),
                Holding(new ScalableBloomFilter(50, 0.01, 0.8), f => f.Add(Key("a")), f => f.SetHash(Alternate)),
                Holding(new CountSketch(0.05, 0.01), f => f.Add(Key("a")), f => f.SetHash(Alternate)),
                Holding(new HyperLogLogPlus(14), f => f.Add(Key("a")), f => f.SetHash(Alternate)),
                Holding(new QuotientFilter(100, 0.01), f => f.Add(Key("a")), f => f.SetHash(Alternate)),
                Holding(new ThetaSketch(64), f => f.Add(Key("a")), f => f.SetHash(Alternate)),
                Holding(new UltraLogLog(10), f => f.Add(Key("a")), f => f.SetHash(Alternate)),
                Holding(new InfiniFilter(64, 8), f => f.Add(Key("a")), f => f.SetHash(Alternate)),
                Holding(new SetSketch(8), f => f.Add(Key("a")), f => f.SetHash(Alternate)),
                Holding(new TupleSketch(16), f => f.Add(Key("a"), 1.0), f => f.SetHash(Alternate)),
                Holding(
                    new SublimeCountMinSketch(0.5, 0.5, ValeCounterArray.DefaultCountersPerChunk),
                    f => f.Add(Key("a")),
                    f => f.SetHash(Alternate)),
            };

            foreach (var (type, replace) in occupied)
            {
                Assert.ThrowsExactly<InvalidOperationException>(replace,
                    $"a {type.Name} holding something allowed its hash to be replaced");
            }

            StructureRoster.AssertCoversEveryType(
                "replacing the hash",
                StructureRoster.WithSetHash,
                occupied.Select(o => o.Type));
        }

        /// <summary>
        /// Builds a structure, puts something in it, and hands back the attempt to
        /// replace its hash afterwards, paired with the type so a failure names it.
        /// </summary>
        private static (Type Type, Action Replace) Holding<T>(
            T structure, Action<T> fill, Action<T> replace)
        {
            fill(structure);
            return (typeof(T), () => replace(structure));
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
