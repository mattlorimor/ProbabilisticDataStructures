using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The behavior that distinguishes each structure from the others, as opposed to
    /// the shared Add/Test surface the existing per-filter tests cover.
    /// </summary>
    [TestClass]
    public class TestFilterSemantics
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        /// <summary>
        /// Deleting from a plain Bloom filter is unsafe because bits are shared, which
        /// is the entire reason the deletable variant exists: it tracks which regions
        /// collided and refuses to clear those. Removing one element must therefore
        /// leave the others intact.
        /// </summary>
        [TestMethod]
        public void TestDeletableFilterRemovalDoesNotEraseOtherElements()
        {
            const int count = 2000;
            var f = new DeletableBloomFilter(count, 100, 0.01);

            for (int i = 0; i < count; i++)
            {
                f.Add(Key($"item-{i}"));
            }

            // Remove the first half.
            for (int i = 0; i < count / 2; i++)
            {
                f.TestAndRemove(Key($"item-{i}"));
            }

            // The second half must be untouched. Anything missing here is a false
            // negative introduced by a deletion, which is what the collision regions
            // exist to prevent.
            var missing = Enumerable.Range(count / 2, count / 2)
                .Count(i => !f.Test(Key($"item-{i}")));

            Assert.AreEqual(0, missing,
                $"{missing} elements disappeared after removing unrelated ones.");
        }

        /// <summary>
        /// TestAndRemove reports whether the element was present, and a second removal
        /// of the same element should report that it no longer is.
        /// </summary>
        [TestMethod]
        public void TestDeletableFilterReportsRemovalOfAbsentElement()
        {
            var f = new DeletableBloomFilter(1000, 100, 0.01);
            var a = Key("present");

            Assert.IsFalse(f.TestAndRemove(a), "removing from an empty filter reports absent");

            f.Add(a);
            Assert.IsTrue(f.TestAndRemove(a), "removing a present element reports present");
        }

        /// <summary>
        /// The number of collision regions is a free parameter, so every value has to
        /// work. It did not: the region size was rounded down, which sent the trailing
        /// bits of the data region to region index r -- one past the last collision
        /// bucket. Buckets does not bounds-check, so what happened next depended on
        /// whether the bitmap had padding to absorb the write. A multiple of eight has
        /// none, and Add threw; other values landed in padding and worked by accident.
        /// </summary>
        [TestMethod]
        public void TestDeletableFilterAcceptsAnyRegionCount()
        {
            // Powers of two are the values a caller reaches for first, and are exactly
            // the ones that used to throw.
            uint[] regionCounts = { 1, 2, 3, 7, 8, 10, 16, 32, 64, 100, 128, 1000 };

            foreach (var r in regionCounts)
            {
                const int count = 500;
                var f = new DeletableBloomFilter(count, r, 0.01);

                for (int i = 0; i < count; i++)
                {
                    f.Add(Key($"r{r}-item-{i}"));
                }

                for (int i = 0; i < count / 2; i++)
                {
                    f.TestAndRemove(Key($"r{r}-item-{i}"));
                }

                var missing = Enumerable.Range(count / 2, count / 2)
                    .Count(i => !f.Test(Key($"r{r}-item-{i}")));

                Assert.AreEqual(0, missing,
                    $"r={r}: {missing} elements disappeared after removing unrelated ones.");
            }
        }

        /// <summary>
        /// r is only required to be smaller than the filter's m bits, so it is allowed
        /// to exceed the m - r bits left for data. Rounding the region size down gave
        /// zero in that case and the first Add divided by it.
        /// </summary>
        [TestMethod]
        public void TestDeletableFilterHandlesMoreRegionsThanDataBits()
        {
            // 1000 items at a 1% rate sizes m at 9586 bits, so this leaves 11 for data
            // and asks for more regions than there are bits to put in them.
            var f = new DeletableBloomFilter(1000, 9575, 0.01);
            Assert.IsGreaterThan(0u, f.Capacity(), "the data region should not be empty");

            var a = Key("present");
            f.Add(a);
            Assert.IsTrue(f.Test(a), "an added element must be found");
            Assert.IsTrue(f.TestAndRemove(a), "removing a present element reports present");
        }

        /// <summary>
        /// A scalable filter's guarantee is a compound false positive rate bounded by
        /// P0 / (1 - r). That is the sum of a geometric series, so it only converges
        /// for a ratio strictly between zero and one.
        /// <para>
        /// A ratio of exactly 1 was accepted and never tightened anything: asking for
        /// 1% and adding 20,000 items to a filter hinted at 100 measured **83%**. The
        /// filter kept working and simply stopped honoring the rate requested of it,
        /// which is worse than refusing the argument. Ratios outside the range in the
        /// other direction did throw, but from inside Add and blaming fpRate -- a
        /// parameter the caller had passed correctly.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestScalableFilterRejectsRatiosThatDoNotTighten()
        {
            foreach (var r in new[] { -0.5, 0.0, 1.0, 1.5, 2.0, double.NaN })
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => new ScalableBloomFilter(100, 0.01, r),
                    $"a tightening ratio of {r} does not bound the compound rate");
            }

            // The open interval between them is what the structure is defined on.
            foreach (var r in new[] { 0.001, 0.5, 0.8, 0.999 })
            {
                _ = new ScalableBloomFilter(100, 0.01, r);
            }
        }

        /// <summary>
        /// Reset empties a filter; it does not reconfigure it. The scalable filter
        /// rebuilds its list from scratch, and did so without carrying the hash across,
        /// so a caller who had set their own was quietly returned to the default.
        /// </summary>
        [TestMethod]
        public void TestScalableFilterResetKeepsTheHashFunction()
        {
            var f = new ScalableBloomFilter(100, 0.01, 0.8);

            // Degenerate on purpose: a constant hash puts every key in the same place,
            // so any key reads as present. Nothing else produces that.
            f.SetHash(_ => 12345UL);
            f.Add(Key("a"));
            Assert.IsTrue(f.Test(Key("unrelated")),
                "sanity: with a constant hash every key collides");

            f.Reset();
            f.Add(Key("a"));

            Assert.IsTrue(f.Test(Key("unrelated")),
                "Reset restored the default hash and discarded the one that was set");
        }

        /// <summary>
        /// The inverse filter's one hard guarantee is that it never reports an item it
        /// has not seen. It is the only filter here that stores the data rather than
        /// only hashing it, and it kept the caller's array instead of copying, so the
        /// caller's next write into that buffer changed what the filter held.
        /// <para>
        /// Reusing one buffer per record is ordinary and is the reason callers work in
        /// bytes at all. It left every written slot pointing at the same array, so a
        /// value never added could be read straight back out of a slot it was never
        /// put in: 38.8% of never-added values were reported present.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestInverseFilterNeverReportsUnseenData()
        {
            const uint capacity = 1000;
            var f = new InverseBloomFilter(capacity);
            var buffer = new byte[8];

            for (int i = 0; i < 500; i++)
            {
                Encoding.ASCII.GetBytes($"rec-{i:D3}").CopyTo(buffer, 0);
                f.Add(buffer);
            }

            var falsePositives = 0;
            for (int i = 0; i < 10000; i++)
            {
                var unseen = Encoding.ASCII.GetBytes($"nev-{i:D4}");
                // As a caller would on their next read, before querying.
                unseen.CopyTo(buffer, 0);
                if (f.Test(unseen))
                {
                    falsePositives++;
                }
            }

            Assert.AreEqual(0, falsePositives,
                $"{falsePositives} values that were never added were reported present; " +
                "this filter must never report a false positive.");
        }

        /// <summary>
        /// The same guarantee stated at its smallest: one element, mutated in place
        /// after being added.
        /// </summary>
        [TestMethod]
        public void TestInverseFilterIsUnaffectedByMutatingAddedData()
        {
            var f = new InverseBloomFilter(1000);
            var data = new byte[] { 1, 2, 3 };

            f.Add(data);
            data[0] = 9;

            Assert.IsTrue(f.Test(new byte[] { 1, 2, 3 }),
                "what was added should still be found after the caller reuses its array");
            Assert.IsFalse(f.Test(new byte[] { 9, 2, 3 }),
                "what was never added should not be found");
        }

        /// <summary>
        /// A top-k structure has one job. Given a stream whose frequencies are all
        /// distinct, and a sketch wide enough to count it without error, the answer is
        /// not approximate and there is nothing to be lenient about.
        /// <para>
        /// It got this wrong across most configurations, because its min-heap was not
        /// one. Pop removed the root with List.Remove, which slides every later element
        /// down a position rather than restoring the ordering, and an element already
        /// in the heap had its frequency raised in place with no re-ordering at all.
        /// The root therefore stopped being the minimum, and the root is both what new
        /// elements are compared against and what gets evicted. 89 of these 150
        /// configurations returned the wrong set.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestTopKReturnsTheExactTopKWhenCountsAreExact()
        {
            const int distinct = 200;

            foreach (uint k in new uint[] { 1, 2, 5, 10, 25, 50 })
            {
                for (int seed = 0; seed < 5; seed++)
                {
                    // Item i occurs (distinct - i + 5) times, so every frequency
                    // differs and the correct answer is exactly items 0..k-1.
                    var stream = new List<int>();
                    for (int i = 0; i < distinct; i++)
                    {
                        for (int j = 0; j < distinct - i + 5; j++)
                        {
                            stream.Add(i);
                        }
                    }

                    var rand = new Random(seed);
                    for (int i = stream.Count - 1; i > 0; i--)
                    {
                        int j = rand.Next(i + 1);
                        (stream[i], stream[j]) = (stream[j], stream[i]);
                    }

                    // Wide and deep enough that the sketch counts this stream exactly.
                    var topK = new TopK(0.0001, 0.001, k);
                    foreach (var x in stream)
                    {
                        topK.Add(Key($"i{x:D4}"));
                    }

                    var got = topK.Elements()
                        .Select(e => Encoding.ASCII.GetString(e.Data))
                        .ToHashSet();
                    var want = Enumerable.Range(0, (int)k)
                        .Select(i => $"i{i:D4}")
                        .ToHashSet();

                    Assert.IsTrue(got.SetEquals(want),
                        $"k={k} seed={seed}: missing {string.Join(", ", want.Except(got))}, " +
                        $"unexpected {string.Join(", ", got.Except(want))}");

                    // And ordered from lowest to highest frequency, as documented.
                    var freqs = topK.Elements().Select(e => e.Freq).ToArray();
                    CollectionAssert.AreEqual(freqs.OrderBy(x => x).ToArray(), freqs,
                        $"k={k} seed={seed}: Elements() must be ascending by frequency");
                }
            }
        }

        /// <summary>
        /// Elements() hands the stored arrays back to the caller, and the heap kept the
        /// arrays it was given rather than copying them, so a caller reusing one buffer
        /// to add from would find every entry holding their last write.
        /// </summary>
        [TestMethod]
        public void TestTopKIsUnaffectedByMutatingAddedData()
        {
            var topK = new TopK(0.001, 0.01, 3);
            var buffer = new byte[4];

            foreach (var name in new[] { "aaaa", "bbbb", "cccc" })
            {
                Encoding.ASCII.GetBytes(name).CopyTo(buffer, 0);
                topK.Add(buffer);
            }

            Encoding.ASCII.GetBytes("zzzz").CopyTo(buffer, 0);

            var names = topK.Elements()
                .Select(e => Encoding.ASCII.GetString(e.Data))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            CollectionAssert.AreEqual(new[] { "aaaa", "bbbb", "cccc" }, names,
                "the heap held the caller's buffer rather than a copy of it");
        }

        /// <summary>
        /// The inverse filter is a bounded "recently seen" cache rather than a growing
        /// set: an element whose slot is claimed by a later one is forgotten. False
        /// negatives are therefore expected here, which is the opposite of every other
        /// filter in the library and worth pinning so it is not "fixed" later.
        /// </summary>
        [TestMethod]
        public void TestInverseFilterForgetsDisplacedElements()
        {
            const uint capacity = 100;
            var f = new InverseBloomFilter(capacity);

            const int inserted = 10000;
            for (int i = 0; i < inserted; i++)
            {
                f.Add(Key($"item-{i}"));
            }

            var remembered = Enumerable.Range(0, inserted)
                .Count(i => f.Test(Key($"item-{i}")));

            Assert.IsLessThanOrEqualTo((int)capacity, remembered,
                "an inverse filter holds at most one element per slot, so it cannot " +
                $"remember more than its capacity of {capacity}.");
            Assert.IsGreaterThan(0, remembered, "the most recent insertions should survive");
        }

        /// <summary>
        /// The most recently added element occupies its slot, so it is always found.
        /// </summary>
        [TestMethod]
        public void TestInverseFilterRemembersTheMostRecentElement()
        {
            var f = new InverseBloomFilter(100);
            var last = Key("last-one");

            for (int i = 0; i < 500; i++)
            {
                f.Add(Key($"filler-{i}"));
            }
            f.Add(last);

            Assert.IsTrue(f.Test(last), "the element added most recently must be present");
        }

        /// <summary>
        /// MinHash estimates set similarity. The endpoints are what pin it: identical
        /// bags are fully similar and disjoint bags are not.
        /// </summary>
        [TestMethod]
        public void TestMinHashSimilarityEndpoints()
        {
            var words = Enumerable.Range(0, 200).Select(i => $"word-{i}").ToArray();

            Assert.AreEqual(1.0, MinHash.Similarity(words, words),
                "a bag compared with itself is identical");

            var disjoint = Enumerable.Range(0, 200).Select(i => $"other-{i}").ToArray();
            var similarity = MinHash.Similarity(words, disjoint);
            Assert.IsLessThanOrEqualTo(0.1, similarity,
                $"bags sharing no elements should be near zero, got {similarity}");
        }

        /// <summary>
        /// Partial overlap should land between the endpoints and track the actual
        /// Jaccard similarity reasonably closely.
        /// </summary>
        [TestMethod]
        public void TestMinHashSimilarityTracksOverlap()
        {
            var a = Enumerable.Range(0, 200).Select(i => $"w-{i}").ToArray();
            var b = Enumerable.Range(100, 200).Select(i => $"w-{i}").ToArray();

            // 100 shared of 300 distinct: Jaccard = 1/3.
            var similarity = MinHash.Similarity(a, b);

            Assert.IsGreaterThan(0.15, similarity, $"half-overlapping bags should not read as disjoint, got {similarity}");
            Assert.IsLessThanOrEqualTo(0.55, similarity, $"half-overlapping bags should not read as identical, got {similarity}");
        }

        /// <summary>
        /// Merging estimators should union their observations rather than replace or
        /// double-count them. The existing merge test only checked the return value.
        /// </summary>
        [TestMethod]
        public void TestHyperLogLogMergeUnionsObservations()
        {
            var a = HyperLogLog.NewDefaultHyperLogLog(0.01);
            var b = HyperLogLog.NewDefaultHyperLogLog(0.01);

            const int half = 5000;
            for (int i = 0; i < half; i++)
            {
                a.Add(Key($"a-{i}"));
                b.Add(Key($"b-{i}"));
            }

            Assert.IsTrue(a.Merge(b));

            // The union holds 10000 distinct items; allow generous slack for the
            // estimator's error rather than asserting an exact count.
            var estimate = (double)a.Count();
            Assert.IsGreaterThan(half * 1.5, estimate,
                $"merged estimate {estimate} should reflect both sets, not just one");
            Assert.IsLessThanOrEqualTo(half * 2 * 1.5, estimate,
                $"merged estimate {estimate} should not double-count the union");
        }

        /// <summary>
        /// Count-Min Sketch never undercounts: its estimate for an element is at least
        /// the true frequency, and overshoots only through collisions.
        /// </summary>
        [TestMethod]
        public void TestCountMinSketchNeverUndercounts()
        {
            var cms = new CountMinSketch(0.001, 0.99);
            var expected = new Dictionary<string, ulong>();

            for (int i = 0; i < 500; i++)
            {
                var word = $"word-{i}";
                var times = (ulong)((i % 7) + 1);
                for (ulong t = 0; t < times; t++)
                {
                    cms.Add(Key(word));
                }
                expected[word] = times;
            }

            foreach (var (word, times) in expected)
            {
                Assert.IsGreaterThanOrEqualTo(times, cms.Count(Key(word)),
                    $"'{word}' was added {times} times; a Count-Min Sketch must never " +
                    "report fewer than the true frequency.");
            }

            Assert.AreEqual(expected.Values.Aggregate(0UL, (x, y) => x + y), cms.TotalCount());
        }
    }
}
