using System;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Public members that no test referenced, plus the Top-K behavior that only
    /// appears once more distinct elements arrive than the structure retains.
    /// </summary>
    [TestClass]
    public class TestUncoveredApi
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        /// <summary>
        /// Epsilon and Delta report the accuracy parameters the sketch was built
        /// with, including across a Reset.
        /// </summary>
        [TestMethod]
        public void TestCountMinSketchReportsItsAccuracyParameters()
        {
            var cms = new CountMinSketch(0.001, 0.99);

            Assert.AreEqual(0.001, cms.Epsilon());
            Assert.AreEqual(0.99, cms.Delta());

            cms.Add(Key("a"));
            cms.Reset();

            Assert.AreEqual(0.001, cms.Epsilon(), "Reset clears counts, not configuration");
            Assert.AreEqual(0.99, cms.Delta(), "Reset clears counts, not configuration");
        }

        /// <summary>
        /// HashKernelFromSum splits a 64-bit hash into the lower and upper halves the
        /// filters derive their k probes from. It is the seam every filter's hashing
        /// runs through, so its bit layout is pinned explicitly.
        /// </summary>
        [TestMethod]
        public void TestHashKernelFromSumSplitsTheValue()
        {
            var kernel = Utils.HashKernelFromSum(0xAABBCCDD_11223344UL);

            Assert.AreEqual(0x11223344u, kernel.LowerBaseHash, "lower half is the low 32 bits");
            Assert.AreEqual(0xAABBCCDDu, kernel.UpperBaseHash, "upper half is the high 32 bits");
        }

        [TestMethod]
        public void TestHashKernelFromSumHandlesBoundaryValues()
        {
            var zero = Utils.HashKernelFromSum(0UL);
            Assert.AreEqual(0u, zero.LowerBaseHash);
            Assert.AreEqual(0u, zero.UpperBaseHash);

            var max = Utils.HashKernelFromSum(ulong.MaxValue);
            Assert.AreEqual(uint.MaxValue, max.LowerBaseHash);
            Assert.AreEqual(uint.MaxValue, max.UpperBaseHash);
        }

        /// <summary>
        /// Top-K retains only the k most frequent elements. This adds more distinct
        /// elements than k so that the heap has to evict, which the existing Top-K
        /// test never forced.
        /// </summary>
        [TestMethod]
        public void TestTopKEvictsLeastFrequentBeyondK()
        {
            var topK = new TopK(0.001, 0.99, 3);

            // Frequencies chosen so the expected survivors are unambiguous.
            foreach (var (word, times) in new[]
            {
                ("alpha", 10), ("bravo", 8), ("charlie", 6), ("delta", 4), ("echo", 2),
            })
            {
                for (int i = 0; i < times; i++)
                {
                    topK.Add(Key(word));
                }
            }

            var elements = topK.Elements();
            Assert.HasCount(3, elements);

            var words = elements.Select(e => Encoding.ASCII.GetString(e.Data)).ToArray();
            CollectionAssert.AreEquivalent(
                new[] { "charlie", "bravo", "alpha" }, words,
                "the three most frequent elements should survive and the rest be evicted");

            // Elements come back in ascending frequency order.
            var freqs = elements.Select(e => e.Freq).ToArray();
            CollectionAssert.AreEqual(freqs.OrderBy(f => f).ToArray(), freqs,
                "Elements() should return ascending frequency order");
        }

        /// <summary>
        /// Reset returns Top-K to its initial state, including releasing the elements
        /// the heap was holding.
        /// </summary>
        [TestMethod]
        public void TestTopKResetClearsRetainedElements()
        {
            var topK = new TopK(0.001, 0.99, 3);
            foreach (var word in new[] { "alpha", "bravo", "charlie", "delta" })
            {
                topK.Add(Key(word));
            }

            Assert.IsGreaterThan(0, topK.Elements().Length);

            var returned = topK.Reset();

            Assert.AreSame(topK, returned, "Reset returns the same instance for chaining");
            Assert.IsEmpty(topK.Elements());
            Assert.AreEqual(0u, topK.N);
        }
    }
}
