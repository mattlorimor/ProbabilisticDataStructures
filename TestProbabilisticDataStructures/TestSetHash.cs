using System;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// SetHash lets a caller supply their own hash function. It had one call site in
    /// the whole suite, which passed the default function and so demonstrated
    /// nothing -- neither that a supplied function is used, nor that a filter still
    /// behaves correctly with one.
    /// </summary>
    [TestClass]
    public class TestSetHash
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        /// <summary>A deliberately different hash, to show the filter actually uses it.</summary>
        private static ulong AlternateHash(ReadOnlySpan<byte> data) => XxHash64.HashToUInt64(data);

        /// <summary>
        /// A filter using a supplied hash must still satisfy the guarantee that
        /// matters: everything added is found.
        /// </summary>
        [TestMethod]
        public void TestSuppliedHashStillFindsEverythingAdded()
        {
            var f = new BloomFilter(5000, 0.01);
            f.SetHash(AlternateHash);

            for (int i = 0; i < 5000; i++)
            {
                f.Add(Key($"item-{i}"));
            }

            var missing = Enumerable.Range(0, 5000).Count(i => !f.Test(Key($"item-{i}")));
            Assert.AreEqual(0, missing, $"{missing} false negatives with a supplied hash function");
        }

        /// <summary>
        /// The supplied function has to actually be used. Two filters differing only
        /// by hash should set different bits, so their fill patterns diverge.
        /// <para>
        /// Comparing fill ratios is an indirect probe, but the alternative -- reading
        /// bucket state -- reaches past the public surface. If SetHash were ignored,
        /// both filters would be bit-for-bit identical and these would match exactly.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestSuppliedHashChangesWhichBitsAreSet()
        {
            var withDefault = new BloomFilter(1000, 0.01);
            var withAlternate = new BloomFilter(1000, 0.01);
            withAlternate.SetHash(AlternateHash);

            for (int i = 0; i < 500; i++)
            {
                var data = Key($"item-{i}");
                withDefault.Add(data);
                withAlternate.Add(data);
            }

            // Both should be plausibly filled...
            Assert.IsGreaterThan(0.0, withDefault.FillRatio());
            Assert.IsGreaterThan(0.0, withAlternate.FillRatio());

            // ...but a different hash chooses different bits, so at least one probe
            // should disagree across a decent sample of absent elements.
            var disagreements = Enumerable.Range(0, 2000)
                .Count(i => withDefault.Test(Key($"absent-{i}")) != withAlternate.Test(Key($"absent-{i}")));

            Assert.IsGreaterThan(0, disagreements,
                "the two filters agreed on every probe, which suggests SetHash was ignored " +
                "and both are using the same hash function.");
        }

        /// <summary>
        /// Every structure exposing SetHash accepts one and keeps working. This is a
        /// breadth check rather than a deep one: the point is that no implementation
        /// was missed when the signature changed, so the roster it sweeps is derived
        /// from the library's own surface rather than typed out here.
        /// </summary>
        [TestMethod]
        public void TestEveryStructureAcceptsASuppliedHash()
        {
            var a = Key("alpha");

            var covered = new (Type Type, Action Exercise)[]
            {
                Accepting(new BloomFilter(100, 0.01), f => { f.Add(a); Assert.IsTrue(f.Test(a)); }),
                Accepting(new BloomFilter64(100, 0.01), f => { f.Add(a); Assert.IsTrue(f.Test(a)); }),
                Accepting(CountingBloomFilter.NewDefaultCountingBloomFilter(100, 0.01),
                    f => { f.Add(a); Assert.IsTrue(f.Test(a)); }),
                Accepting(new PartitionedBloomFilter(100, 0.01), f => { f.Add(a); Assert.IsTrue(f.Test(a)); }),
                Accepting(new DeletableBloomFilter(100, 10, 0.01), f => { f.Add(a); Assert.IsTrue(f.Test(a)); }),
                Accepting(StableBloomFilter.NewDefaultStableBloomFilter(100, 0.01),
                    f => { f.Add(a); Assert.IsTrue(f.Test(a)); }),
                Accepting(new InverseBloomFilter(100), f => { f.Add(a); Assert.IsTrue(f.Test(a)); }),
                Accepting(new CuckooBloomFilter(100, 0.01), f => { f.Add(a); Assert.IsTrue(f.Test(a)); }),
                Accepting(ScalableBloomFilter.NewDefaultScalableBloomFilter(0.01),
                    f => { f.Add(a); Assert.IsTrue(f.Test(a)); }),
                Accepting(new QuotientFilter(100, 0.01), f => { f.Add(a); Assert.IsTrue(f.Test(a)); }),
                Accepting(new InfiniFilter(64, 8), f => { f.Add(a); Assert.IsTrue(f.Test(a)); }),

                // These have no Test, so exercising the query they do have is the
                // available check.
                Accepting(new CountMinSketch(0.001, 0.99),
                    f => { f.Add(a); Assert.IsGreaterThanOrEqualTo(1UL, f.Count(a)); }),
                Accepting(HyperLogLog.NewDefaultHyperLogLog(0.01),
                    f => { f.Add(a); Assert.IsGreaterThan(0UL, f.Count()); }),
                Accepting(new HyperLogLogPlus(14),
                    f => { f.Add(a); Assert.IsGreaterThan(0UL, f.Count()); }),
                Accepting(new CountSketch(0.05, 0.01),
                    f => { f.Add(a); Assert.IsGreaterThanOrEqualTo(1L, f.Count(a)); }),
                Accepting(new ThetaSketch(64),
                    f => { f.Add(a); Assert.IsGreaterThan(0UL, f.Count()); }),
                Accepting(new UltraLogLog(10),
                    f => { f.Add(a); Assert.IsGreaterThan(0UL, f.Count()); }),
                Accepting(new SetSketch(64),
                    f => { f.Add(a); Assert.IsGreaterThan(0.0, f.Cardinality()); }),
                Accepting(new TupleSketch(16),
                    f => { f.Add(a, 1.0); Assert.IsGreaterThan(0UL, f.Count()); }),
                Accepting(new SublimeCountMinSketch(0.5, 0.5, ValeCounterArray.DefaultCountersPerChunk),
                    f => { f.Add(a); Assert.IsGreaterThanOrEqualTo(1UL, f.Count(a)); }),
            };

            foreach (var (_, exercise) in covered)
            {
                exercise();
            }

            StructureRoster.AssertCoversEveryType(
                "supplied hash",
                StructureRoster.WithSetHash,
                covered.Select(c => c.Type));
        }

        /// <summary>
        /// Installs the alternate hash on a freshly built structure and hands back the
        /// check that it still works, paired with the type so a failure names it.
        /// </summary>
        /// <remarks>
        /// SetHash is a shape rather than an interface -- twenty structures declare it
        /// and none of them share a base that does -- so it is called here through the
        /// same signature the roster is derived from. That keeps the two in step: a
        /// type the roster finds is a type this can drive.
        /// </remarks>
        private static (Type Type, Action Exercise) Accepting<T>(T structure, Action<T> exercise)
        {
            typeof(T).GetMethod("SetHash", new[] { typeof(Func<ReadOnlySpan<byte>, ulong>) })!
                .Invoke(structure, new object[] { (Func<ReadOnlySpan<byte>, ulong>)AlternateHash });
            return (typeof(T), () => exercise(structure));
        }
    }
}
