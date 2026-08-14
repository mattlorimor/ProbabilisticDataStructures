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
        /// Every filter exposing SetHash should accept one and keep working. This is a
        /// breadth check rather than a deep one: the point is that no implementation
        /// was missed when the signature changed.
        /// </summary>
        [TestMethod]
        public void TestEveryFilterAcceptsASuppliedHash()
        {
            var a = Key("alpha");

            var bloom = new BloomFilter(100, 0.01);
            bloom.SetHash(AlternateHash);
            bloom.Add(a);
            Assert.IsTrue(bloom.Test(a));

            var bloom64 = new BloomFilter64(100, 0.01);
            bloom64.SetHash(AlternateHash);
            bloom64.Add(a);
            Assert.IsTrue(bloom64.Test(a));

            var counting = CountingBloomFilter.NewDefaultCountingBloomFilter(100, 0.01);
            counting.SetHash(AlternateHash);
            counting.Add(a);
            Assert.IsTrue(counting.Test(a));

            var partitioned = new PartitionedBloomFilter(100, 0.01);
            partitioned.SetHash(AlternateHash);
            partitioned.Add(a);
            Assert.IsTrue(partitioned.Test(a));

            var deletable = new DeletableBloomFilter(100, 10, 0.01);
            deletable.SetHash(AlternateHash);
            deletable.Add(a);
            Assert.IsTrue(deletable.Test(a));

            var stable = StableBloomFilter.NewDefaultStableBloomFilter(100, 0.01);
            stable.SetHash(AlternateHash);
            stable.Add(a);
            Assert.IsTrue(stable.Test(a));

            var inverse = new InverseBloomFilter(100);
            inverse.SetHash(AlternateHash);
            inverse.Add(a);
            Assert.IsTrue(inverse.Test(a));

            var cuckoo = new CuckooBloomFilter(100, 0.01);
            cuckoo.SetHash(AlternateHash);
            cuckoo.Add(a);
            Assert.IsTrue(cuckoo.Test(a));

            var scalable = ScalableBloomFilter.NewDefaultScalableBloomFilter(0.01);
            scalable.SetHash(AlternateHash);
            scalable.Add(a);
            Assert.IsTrue(scalable.Test(a));

            // These have no Test, so exercising Add is the available check.
            var cms = new CountMinSketch(0.001, 0.99);
            cms.SetHash(AlternateHash);
            cms.Add(a);
            Assert.IsGreaterThanOrEqualTo(1UL, cms.Count(a));

            var hll = HyperLogLog.NewDefaultHyperLogLog(0.01);
            hll.SetHash(AlternateHash);
            hll.Add(a);
            Assert.IsGreaterThan(0UL, hll.Count());
        }
    }
}
