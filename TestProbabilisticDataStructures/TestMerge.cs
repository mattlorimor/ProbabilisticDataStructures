using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Merging is asserted against the filter you would have got by adding everything to
    /// one of them, which is the promise. Comparing internal state instead would pass
    /// for an implementation that combined the bits correctly and the bookkeeping wrong.
    /// </summary>
    [TestClass]
    public class TestMerge
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        private static readonly string[] First =
            Enumerable.Range(0, 500).Select(i => $"a-{i}").ToArray();

        private static readonly string[] Second =
            Enumerable.Range(0, 500).Select(i => $"b-{i}").ToArray();

        private static readonly string[] Absent =
            Enumerable.Range(0, 3000).Select(i => $"absent-{i}").ToArray();

        [TestMethod]
        public void TestBloomFilterMergeMatchesAddingEverythingToOne()
        {
            var left = new BloomFilter(2000, 0.01);
            var right = new BloomFilter(2000, 0.01);
            var combined = new BloomFilter(2000, 0.01);

            foreach (var w in First) { left.Add(Key(w)); combined.Add(Key(w)); }
            foreach (var w in Second) { right.Add(Key(w)); combined.Add(Key(w)); }

            var merged = left.Merge(right);
            Assert.AreSame(left, merged, "Merge returns the receiver, for chaining");

            // Identical on every query, including the false positives: a filter that
            // disagrees there is a different filter even where yes is permitted.
            foreach (var w in First.Concat(Second).Concat(Absent))
            {
                Assert.AreEqual(combined.Test(Key(w)), left.Test(Key(w)),
                    $"merged filter disagreed with the combined one about {w}");
            }
        }

        [TestMethod]
        public void TestBloomFilter64AndPartitionedMergeTheSameWay()
        {
            var left64 = new BloomFilter64(2000, 0.01);
            var right64 = new BloomFilter64(2000, 0.01);
            var combined64 = new BloomFilter64(2000, 0.01);

            var leftP = new PartitionedBloomFilter(2000, 0.01);
            var rightP = new PartitionedBloomFilter(2000, 0.01);
            var combinedP = new PartitionedBloomFilter(2000, 0.01);

            foreach (var w in First)
            {
                left64.Add(Key(w)); combined64.Add(Key(w));
                leftP.Add(Key(w)); combinedP.Add(Key(w));
            }
            foreach (var w in Second)
            {
                right64.Add(Key(w)); combined64.Add(Key(w));
                rightP.Add(Key(w)); combinedP.Add(Key(w));
            }

            left64.Merge(right64);
            leftP.Merge(rightP);

            foreach (var w in First.Concat(Second).Concat(Absent))
            {
                Assert.AreEqual(combined64.Test(Key(w)), left64.Test(Key(w)),
                    $"merged BloomFilter64 disagreed about {w}");
                Assert.AreEqual(combinedP.Test(Key(w)), leftP.Test(Key(w)),
                    $"merged PartitionedBloomFilter disagreed about {w}");
            }
        }

        /// <summary>
        /// A counting filter's counters add rather than max, because a merged filter has
        /// to be removable from as many times as the two inputs together were.
        /// </summary>
        [TestMethod]
        public void TestCountingFilterMergeAddsCounters()
        {
            var left = new CountingBloomFilter(2000, 8, 0.01);
            var right = new CountingBloomFilter(2000, 8, 0.01);
            var combined = new CountingBloomFilter(2000, 8, 0.01);

            // The same elements in both, so the counters genuinely have to sum.
            foreach (var w in First)
            {
                left.Add(Key(w));
                right.Add(Key(w));
                combined.Add(Key(w));
                combined.Add(Key(w));
            }

            left.Merge(right);

            foreach (var w in First.Concat(Absent))
            {
                Assert.AreEqual(combined.Test(Key(w)), left.Test(Key(w)),
                    $"merged filter disagreed about {w}");
            }

            // Each element went in twice, so it comes out twice and not once.
            foreach (var w in First.Take(100))
            {
                Assert.IsTrue(left.TestAndRemove(Key(w)), $"{w} should remove once");
                Assert.IsTrue(left.Test(Key(w)),
                    $"{w} was added twice across the two filters and should survive one removal");
                Assert.IsTrue(left.TestAndRemove(Key(w)), $"{w} should remove twice");
            }
        }

        /// <summary>
        /// A top-k's frequencies come from the merged sketch rather than from adding the
        /// two recorded ones, which would count twice everything both sketches knew.
        /// </summary>
        [TestMethod]
        public void TestTopKMergeReadsFrequenciesFromTheMergedSketch()
        {
            var left = new TopK(0.0001, 0.001, 10);
            var right = new TopK(0.0001, 0.001, 10);
            var combined = new TopK(0.0001, 0.001, 10);

            // Item i appears i+1 times in each, so 2(i+1) across the two.
            for (int i = 0; i < 20; i++)
            {
                for (int j = 0; j <= i; j++)
                {
                    left.Add(Key($"item-{i}"));
                    right.Add(Key($"item-{i}"));
                    combined.Add(Key($"item-{i}"));
                    combined.Add(Key($"item-{i}"));
                }
            }

            left.Merge(right);

            var merged = left.Elements()
                .Select(e => (Encoding.ASCII.GetString(e.Data.Span), e.Freq))
                .ToArray();
            var expected = combined.Elements()
                .Select(e => (Encoding.ASCII.GetString(e.Data.Span), e.Freq))
                .ToArray();

            CollectionAssert.AreEqual(expected, merged,
                "the merged top-k differs from one built by adding both streams to it");

            // Concretely: the most frequent appeared 20 times in each.
            Assert.AreEqual(40ul, merged.Last().Freq,
                "frequencies were added rather than re-read from the merged sketch");
        }

        /// <summary>
        /// A merged top-k is not necessarily the true top-k of the combined stream. Only
        /// elements one of the heaps was holding are candidates, so an element that was
        /// frequent in both but top in neither is invisible to the merge and stays so.
        /// <para>
        /// Pinned because it is a real limit of merging bounded summaries rather than a
        /// shortcut, and because it is exactly the kind of thing that would otherwise be
        /// discovered by someone trusting the result.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestTopKMergeCannotRecoverAnElementNeitherHeapHeld()
        {
            var left = new TopK(0.0001, 0.001, 3);
            var right = new TopK(0.0001, 0.001, 3);

            static void Add(TopK t, string word, int times)
            {
                for (int i = 0; i < times; i++)
                {
                    t.Add(Key(word));
                }
            }

            // Each holds three elements above "shared", but they are different three, so
            // "shared" tops the combined stream at 140 while sitting fourth in both.
            Add(left, "a", 100); Add(left, "b", 90); Add(left, "c", 80); Add(left, "shared", 70);
            Add(right, "d", 100); Add(right, "e", 90); Add(right, "f", 80); Add(right, "shared", 70);

            Assert.IsFalse(
                left.Elements().Any(e => Encoding.ASCII.GetString(e.Data.Span) == "shared"),
                "sanity: shared should be outside the left heap");
            Assert.IsFalse(
                right.Elements().Any(e => Encoding.ASCII.GetString(e.Data.Span) == "shared"),
                "sanity: shared should be outside the right heap");

            left.Merge(right);

            var merged = left.Elements()
                .Select(e => Encoding.ASCII.GetString(e.Data.Span))
                .ToArray();

            Assert.DoesNotContain("shared", merged,
                "the merge recovered an element neither heap was holding, which it " +
                "cannot do; if this now passes, the candidate set has changed");

            // What it does return is the best of what it could see.
            Assert.HasCount(3, merged);
            CollectionAssert.IsSubsetOf(merged, new[] { "a", "b", "c", "d", "e", "f" });
        }

        [TestMethod]
        public void TestMergeRefusesMismatchedDimensions()
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => new BloomFilter(1000, 0.01).Merge(new BloomFilter(2000, 0.01)));
            Assert.ThrowsExactly<ArgumentException>(
                () => new BloomFilter64(1000, 0.01).Merge(new BloomFilter64(2000, 0.01)));
            Assert.ThrowsExactly<ArgumentException>(
                () => new PartitionedBloomFilter(1000, 0.01).Merge(new PartitionedBloomFilter(2000, 0.01)));
            Assert.ThrowsExactly<ArgumentException>(
                () => new CountingBloomFilter(1000, 4, 0.01).Merge(new CountingBloomFilter(2000, 4, 0.01)));

            // Same dimensions, different counter width.
            Assert.ThrowsExactly<ArgumentException>(
                () => new CountingBloomFilter(1000, 4, 0.01).Merge(new CountingBloomFilter(1000, 8, 0.01)));

            Assert.ThrowsExactly<ArgumentException>(
                () => new TopK(0.001, 0.01, 5).Merge(new TopK(0.001, 0.01, 10)));
        }

        /// <summary>
        /// Everything a filter holds sits where its own hash put it, so merging two that
        /// hash differently produces a filter answering about positions neither meant.
        /// Nothing looks wrong afterwards, which is why it is refused.
        /// </summary>
        [TestMethod]
        public void TestMergeRefusesDifferentHashFunctions()
        {
            Func<ReadOnlySpan<byte>, ulong> alternate =
                d => System.IO.Hashing.XxHash64.HashToUInt64(d);

            Assert.ThrowsExactly<ArgumentException>(
                () => new BloomFilter(1000, 0.01).Merge(new BloomFilter(1000, 0.01, alternate)));
            Assert.ThrowsExactly<ArgumentException>(
                () => new BloomFilter(1000, 0.01, alternate).Merge(new BloomFilter(1000, 0.01)));
            Assert.ThrowsExactly<ArgumentException>(
                () => new CountingBloomFilter(1000, 4, 0.01)
                    .Merge(new CountingBloomFilter(1000, 4, 0.01, alternate)));

            // The two that already had Merge never checked this.
            Assert.ThrowsExactly<ArgumentException>(
                () => new CountMinSketch(0.001, 0.01).Merge(new CountMinSketch(0.001, 0.01, alternate)));
            Assert.ThrowsExactly<ArgumentException>(
                () => new HyperLogLog(64).Merge(new HyperLogLog(64, alternate)));

            // The same hash passed to both is fine, however it was written.
            var left = new BloomFilter(1000, 0.01, alternate);
            var right = new BloomFilter(1000, 0.01, alternate);
            left.Add(Key("a"));
            right.Add(Key("b"));
            left.Merge(right);
            Assert.IsTrue(left.Test(Key("a")));
            Assert.IsTrue(left.Test(Key("b")));
        }

        [TestMethod]
        public void TestMergeRejectsNull()
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => new BloomFilter(100, 0.01).Merge(null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => new BloomFilter64(100, 0.01).Merge(null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => new PartitionedBloomFilter(100, 0.01).Merge(null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => new CountingBloomFilter(100, 4, 0.01).Merge(null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => new TopK(0.001, 0.01, 5).Merge(null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => new HyperLogLog(64).Merge(null!));
        }

        /// <summary>
        /// The count of a merged filter is the sum, which overstates the union whenever
        /// the inputs shared elements. Pinned so the overstatement is a documented
        /// property rather than a surprise.
        /// </summary>
        [TestMethod]
        public void TestMergedCountIsTheSumAndSoAnUpperBound()
        {
            var left = new BloomFilter(2000, 0.01);
            var right = new BloomFilter(2000, 0.01);

            foreach (var w in First) { left.Add(Key(w)); right.Add(Key(w)); }

            left.Merge(right);

            Assert.AreEqual(1000u, left.Count(),
                "the count sums, even though the two filters held the same 500 elements");
        }

        /// <summary>
        /// Merging is what a caller does across shards, so a merged filter has to be
        /// usable afterwards rather than only readable.
        /// </summary>
        [TestMethod]
        public void TestAMergedFilterStillWorks()
        {
            var left = new BloomFilter(2000, 0.01);
            var right = new BloomFilter(2000, 0.01);

            foreach (var w in First) left.Add(Key(w));
            foreach (var w in Second) right.Add(Key(w));

            left.Merge(right).Add(Key("added-after"));

            Assert.IsTrue(left.Test(Key("added-after")));
            Assert.IsTrue(left.Test(Key(First[0])));
            Assert.IsTrue(left.Test(Key(Second[0])));

            // And survives a round trip, since a merged filter is a filter.
            var restored = Persistence.FromByteArray<BloomFilter>(left.ToByteArray());
            foreach (var w in First.Concat(Second).Concat(Absent).Take(1000))
            {
                Assert.AreEqual(left.Test(Key(w)), restored.Test(Key(w)));
            }
        }
    }
}
