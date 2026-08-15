using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestSignatureIndexes
    {
        /// <summary>A document sharing a given fraction of its terms with a base one.</summary>
        private static string[] Document(int id, int terms = 200, int sharedWith = -1, int shared = 0)
        {
            if (sharedWith < 0)
            {
                return Enumerable.Range(0, terms).Select(i => $"doc{id}-term{i}").ToArray();
            }

            return Enumerable.Range(0, shared).Select(i => $"doc{sharedWith}-term{i}")
                .Concat(Enumerable.Range(0, terms - shared).Select(i => $"doc{id}-term{i}"))
                .ToArray();
        }

        [TestMethod]
        public void TestASignatureFindsItself()
        {
            var index = new MinHashIndex(bands: 16, rows: 8);
            var signature = MinHash.Signature(Document(1), 128);

            index.Add("doc1", signature);

            CollectionAssert.AreEquivalent(new[] { "doc1" }, index.Query(signature).ToArray());
        }
        /// <summary>
        /// The point: a near-duplicate is found without comparing against everything.
        /// </summary>
        [TestMethod]
        public void TestANearDuplicateIsFoundAmongManyDocuments()
        {
            var index = MinHashIndex.ForThreshold(0.7, signatureLength: 128);

            // A thousand unrelated documents.
            for (var i = 0; i < 1000; i++)
            {
                index.Add($"doc{i}", MinHash.Signature(Document(i), 128));
            }

            // One that shares 90% of its terms with doc7.
            var nearDuplicate = MinHash.Signature(Document(9999, sharedWith: 7, shared: 180), 128);
            var candidates = index.Query(nearDuplicate);

            Assert.Contains("doc7", candidates.ToArray(),
                "the near-duplicate's neighbour was not offered as a candidate");

            // And it did not simply return everything, which would be a correct but
            // useless index.
            Assert.IsLessThan(50, candidates.Count,
                $"the index returned {candidates.Count} of 1000 documents, which is not " +
                "narrowing anything down");
        }

        /// <summary>
        /// The failure mode is a <b>missing</b> answer, which is new here -- everything
        /// else in this library errs towards saying yes. Recall is a number the caller
        /// has to look at, so it is exposed rather than buried, and this pins its shape.
        /// </summary>
        [TestMethod]
        public void TestRecallRisesSteeplyAroundTheThreshold()
        {
            var index = MinHashIndex.ForThreshold(0.8, signatureLength: 128);

            // At the threshold itself, recall must already be high. Choosing the
            // configuration by nearest steep point regardless of side gave 20% here,
            // which is a silently lossy index sold as a threshold of 0.8.
            Assert.IsGreaterThan(0.9, index.RecallAt(0.8),
                $"pairs at exactly the 0.8 threshold are returned only " +
                $"{index.RecallAt(0.8):P0} of the time");

            // Above it, near certainty.
            Assert.IsGreaterThan(0.99, index.RecallAt(0.95),
                $"pairs at 0.95 are returned only {index.RecallAt(0.95):P0} of the time");

            // Well below it, few -- or the index is not narrowing anything down.
            Assert.IsLessThan(0.15, index.RecallAt(0.5),
                $"pairs at 0.5 resemblance are returned {index.RecallAt(0.5):P0} of the " +
                "time, which is not a threshold at 0.8");

            // And it is monotonic, or it is not a threshold at all.
            var previous = 0.0;
            for (var s = 0.0; s <= 1.0; s += 0.05)
            {
                var recall = index.RecallAt(s);
                Assert.IsGreaterThanOrEqualTo(previous, recall, $"recall fell at {s}");
                previous = recall;
            }
        }

        [TestMethod]
        public void TestTheIndexRefusesSignaturesItCannotHold()
        {
            var index = new MinHashIndex(16, 8);

            var ex = Assert.ThrowsExactly<ArgumentException>(
                () => index.Add("a", MinHash.Signature(Document(1), 64)));
            StringAssert.Contains(ex.Message, "128 values");

            Assert.ThrowsExactly<ArgumentNullException>(() => index.Add(null!, MinHash.Signature(Document(1), 128)));
            Assert.ThrowsExactly<ArgumentNullException>(() => index.Add("a", null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => index.Query(null!));

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MinHashIndex(0, 8));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MinHashIndex(8, 0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MinHashIndex.ForThreshold(0, 128));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MinHashIndex.ForThreshold(1, 128));
        }

        /// <summary>
        /// The SimHash index has a guarantee its MinHash counterpart cannot: splitting a
        /// 64-bit fingerprint into b bands means two fingerprints differing in fewer than
        /// b bits must agree on at least one band, by the pigeonhole principle. So within
        /// that distance retrieval is <b>certain</b> rather than probable.
        /// </summary>
        [TestMethod]
        public void TestSimHashNeighboursWithinTheBandCountAreAlwaysFound()
        {
            var index = new SimHashIndex(bands: 8);
            var reference = SimHash.Signature(Document(1));
            index.Add("doc1", reference);

            // Every fingerprint within 7 bits of the reference, by construction.
            for (var bit = 0; bit < 64; bit++)
            {
                var flipped = new SimHashSignature(reference.Value ^ (1UL << bit));
                Assert.Contains("doc1", index.Query(flipped).ToArray(),
                    $"a fingerprint one bit away was not found when flipping bit {bit}");
            }

            // Seven flips is still guaranteed with eight bands.
            var sevenAway = reference.Value;
            for (var bit = 0; bit < 7; bit++)
            {
                sevenAway ^= 1UL << (bit * 9);
            }

            Assert.Contains("doc1", index.Query(new SimHashSignature(sevenAway)).ToArray(),
                "a fingerprint seven bits away was not found by an eight-band index");
        }

        [TestMethod]
        public void TestSimHashIndexNarrowsDownACorpus()
        {
            var index = new SimHashIndex(bands: 8);

            for (var i = 0; i < 2000; i++)
            {
                index.Add($"doc{i}", SimHash.Signature(Document(i)));
            }

            var nearDuplicate = SimHash.Signature(Document(9999, sharedWith: 7, shared: 190));
            var candidates = index.Query(nearDuplicate);

            Assert.Contains("doc7", candidates.ToArray(), "the near-duplicate was not found");
            Assert.IsLessThan(100, candidates.Count,
                $"the index offered {candidates.Count} of 2000 documents");
        }

        [TestMethod]
        public void TestSimHashIndexRefusesABandCountItCannotUse()
        {
            foreach (var bad in new[] { 0, 3, 5, 7, 65 })
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SimHashIndex(bad));
            }

            _ = new SimHashIndex(1);
            _ = new SimHashIndex(64);

            var index = new SimHashIndex(8);
            Assert.ThrowsExactly<ArgumentNullException>(() => index.Add("a", null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => index.Add(null!, SimHash.Signature(Document(1))));
            Assert.ThrowsExactly<ArgumentNullException>(() => index.Query(null!));
        }

    }
}
