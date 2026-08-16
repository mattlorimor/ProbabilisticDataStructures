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
                        .Select(e => Encoding.ASCII.GetString(e.Data.Span))
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
                .Select(e => Encoding.ASCII.GetString(e.Data.Span))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            CollectionAssert.AreEqual(new[] { "aaaa", "bbbb", "cccc" }, names,
                "the heap held the caller's buffer rather than a copy of it");
        }

        /// <summary>
        /// Reset restores a filter to its original state, and a filter in its original
        /// state holds nothing. Three of them emptied their buckets and left the item
        /// count where it was, so a filter reporting itself empty by every other
        /// measure still claimed the items it used to hold.
        /// <para>
        /// The count is not only reported. EstimatedFillRatio is derived from it, and
        /// a partitioned filter's is what a scalable filter consults to decide when to
        /// grow, so a stale count made a freshly emptied filter look 44% full.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestResetEmptiesTheItemCount()
        {
            const int added = 800;

            var bloom = new BloomFilter(1000, 0.01);
            var bloom64 = new BloomFilter64(1000, 0.01);
            var partitioned = new PartitionedBloomFilter(1000, 0.01);
            var counting = new CountingBloomFilter(1000, 4, 0.01);
            var deletable = new DeletableBloomFilter(1000, 10, 0.01);

            for (int i = 0; i < added; i++)
            {
                var key = Key($"item-{i}");
                bloom.Add(key);
                bloom64.Add(key);
                partitioned.Add(key);
                counting.Add(key);
                deletable.Add(key);
            }

            Assert.AreEqual(0u, bloom.Reset().Count());
            Assert.AreEqual(0ul, bloom64.Reset().Count());
            Assert.AreEqual(0u, partitioned.Reset().Count());
            Assert.AreEqual(0u, counting.Reset().Count());
            Assert.AreEqual(0u, deletable.Reset().Count());

            // The count feeds the fill estimate, which is what actually acts on it.
            Assert.AreEqual(0.0, bloom.EstimatedFillRatio());
            Assert.AreEqual(0.0, bloom64.EstimatedFillRatio());
            Assert.AreEqual(0.0, partitioned.EstimatedFillRatio());
        }

        /// <summary>
        /// The heap is indexed by element data so that deciding whether an arrival is
        /// already held does not mean scanning it. That index has to stay in step with
        /// the heap through every reordering, and the way it can quietly fail is an
        /// entry left behind for an element that was evicted: a later arrival of the
        /// same data would then be found at a position holding something else.
        /// <para>
        /// Churning far more distinct elements than the heap can hold, so eviction and
        /// re-arrival happen constantly, and asserting what drift would break.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestTopKHoldsEachElementOnceUnderChurn()
        {
            const uint k = 20;
            var topK = new TopK(0.001, 0.01, k);
            var rand = new Random(3);

            for (int i = 0; i < 50000; i++)
            {
                // A small enough pool that elements are evicted and come back, which is
                // the case a stale index entry would corrupt.
                topK.Add(Key($"e{rand.Next(200)}"));
            }

            var elements = topK.Elements();

            Assert.IsLessThanOrEqualTo((int)k, elements.Length,
                "the heap holds more than k elements");

            var distinct = elements
                .Select(e => Encoding.ASCII.GetString(e.Data.Span))
                .Distinct()
                .Count();

            Assert.AreEqual(elements.Length, distinct,
                "the same element is held more than once");

            // Still ordered, which a misplaced update would break.
            var freqs = elements.Select(e => e.Freq).ToArray();
            CollectionAssert.AreEqual(freqs.OrderBy(f => f).ToArray(), freqs,
                "Elements() is no longer ascending by frequency");
        }

        /// <summary>
        /// Elements are identified by what their data holds, not by which array it
        /// arrived in. Indexing on the array itself would hold the same element twice
        /// for a caller who does not reuse one buffer.
        /// </summary>
        [TestMethod]
        public void TestTopKIdentifiesElementsByContentNotByArray()
        {
            var topK = new TopK(0.001, 0.01, 10);

            for (int i = 0; i < 50; i++)
            {
                // A fresh array holding the same bytes every time.
                topK.Add(Encoding.ASCII.GetBytes("recurring"));
            }

            var elements = topK.Elements();

            Assert.HasCount(1, elements);
            Assert.AreEqual("recurring", Encoding.ASCII.GetString(elements[0].Data.Span));
            Assert.AreEqual(50ul, elements[0].Freq);
        }

        /// <summary>
        /// A cuckoo filter makes room by displacing what is already there, and gives up
        /// after a bounded number of attempts. Whatever it is holding at that moment
        /// was displaced out of a bucket and has nowhere to go: dropping it loses an
        /// element the filter had already accepted, which is a false negative.
        /// <para>
        /// The displacements are undone instead, so a refused insert leaves the filter
        /// holding exactly what it held before. This went unseen because the filter
        /// allocated about thirty-two times the buckets it needed, so the relocation
        /// loop never ran long enough to give up.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestCuckooFilterKeepsWhatItAcceptedWhenAnInsertIsRefused()
        {
            var f = new CuckooBloomFilter(1000, 0.01, seed: 11);
            var accepted = new List<string>();
            var refused = 0;

            // Well past capacity, so inserts start being refused and the rollback runs.
            for (int i = 0; i < 20000; i++)
            {
                var word = $"item-{i}";
                if (f.Add(Key(word)))
                {
                    accepted.Add(word);
                }
                else
                {
                    refused++;
                }
            }

            Assert.IsGreaterThan(0, refused,
                "nothing was refused, so the path this covers never ran");
            Assert.IsGreaterThan(0, accepted.Count);

            var missing = accepted.Count(w => !f.Test(Key(w)));
            Assert.AreEqual(0, missing,
                $"{missing} of {accepted.Count} accepted elements were lost, most of " +
                "them displaced by inserts that were themselves refused");
        }

        /// <summary>
        /// The filter is sized for the load it was asked for rather than for tens of
        /// times that. Buckets hold four entries and fill to about 95%, so the count
        /// follows from n directly.
        /// </summary>
        [TestMethod]
        public void TestCuckooFilterIsSizedForTheLoadItWasAskedFor()
        {
            foreach (uint n in new uint[] { 100, 1000, 10000, 100000 })
            {
                var f = new CuckooBloomFilter(n, 0.01);
                var slots = f.BucketCount() * 4;

                Assert.IsGreaterThanOrEqualTo(n, slots,
                    $"n={n}: the filter has fewer slots than the items it was sized for");

                // Powers of two mean up to a factor of two of slack, and the load
                // factor a little more. Four times the requested load is generous and
                // still catches the thirty-two times this used to allocate.
                Assert.IsLessThanOrEqualTo(n * 4, slots,
                    $"n={n}: {slots} slots for {n} items is more headroom than the " +
                    "sizing should produce");
            }
        }

        /// <summary>
        /// The filter's storage is allocated once, so what it costs does not depend on
        /// how much it holds. Fingerprints used to be individual byte arrays, which meant
        /// a 24-byte object header on two bytes of payload and a bucket-sized array of
        /// references besides: a filter for 100,000 items took 3.8 MB holding nothing and
        /// 11.5 MB holding them, against 291 KB now either way.
        /// </summary>
        [TestMethod]
        public void TestCuckooFilterStorageDoesNotGrowWithLoad()
        {
            var empty = new CuckooBloomFilter(2000, 0.01, seed: 4);
            var loaded = new CuckooBloomFilter(2000, 0.01, seed: 4);

            for (int i = 0; i < 5000; i++)
            {
                loaded.Add(Key($"item-{i}"));
            }

            // Same dimensions, so the same fixed storage, however much is in it.
            Assert.AreEqual(empty.Fingerprints.Length, loaded.Fingerprints.Length);
            Assert.AreEqual(empty.Occupied.count, loaded.Occupied.count);

            // And adding allocates nothing that the filter keeps.
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 5000; i < 5100; i++)
            {
                loaded.Add(Key($"later-{i}"));
            }
            var perAdd = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;

            Assert.IsLessThan(512, perAdd,
                $"Add allocated {perAdd} bytes per call; the filter's storage is fixed, " +
                "so anything here is scratch and should stay small");
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

        /// <summary>
        /// Merging two counting filters clamps each counter sum at the counter
        /// maximum. Two layers make that true: the merge site clamps, and Set clamps
        /// whatever it is handed. For four-bit counters the second layer alone
        /// suffices, so removing the merge-site clamp is invisible there -- sums stay
        /// under 255 and Set catches them. Eight-bit counters have no such backstop:
        /// the sum is cast to byte before Set can see it, and 200 plus 200 arrives as
        /// 144. A wrapped counter then decrements to zero while the elements it
        /// stands for are still present -- a false negative, the one failure a
        /// counting filter's deletion support exists to rule out.
        /// <para>
        /// Found by mutation testing, in two steps: removing the merge-site clamp
        /// passed all 520 tests, and the first draft of this test -- written with the
        /// default four-bit counters -- passed the mutant too, because Set's own
        /// clamp covered it. Only full-width counters expose the cast.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestMergingCountingFiltersClampsCountersAtSaturation()
        {
            // Two hundred copies in each: comfortably below the eight-bit maximum of
            // 255, so neither filter is saturated -- only their sum is.
            var a = new CountingBloomFilter(100, 8, 0.01);
            var b = new CountingBloomFilter(100, 8, 0.01);
            for (int i = 0; i < 200; i++)
            {
                a.Add(Key("crowded"));
                b.Add(Key("crowded"));
            }

            a.Merge(b);

            // Four hundred copies stand behind these counters. A saturated counter is
            // never decremented again, so however many removals arrive, the element
            // must remain: the filter trades reclaimable space for never answering no
            // while copies are outstanding. A counter whose sum wrapped to 144
            // instead reaches zero mid-way through these removals.
            for (int removal = 1; removal <= 150; removal++)
            {
                Assert.IsTrue(a.TestAndRemove(Key("crowded")),
                    $"removal {removal}: four hundred copies were merged in, so the " +
                    "element must still be a member here whatever the counters have " +
                    "been through.");
            }

            Assert.IsTrue(a.Test(Key("crowded")),
                "hundreds of the merged-in copies remain, and the filter answers " +
                "no. A counter that wrapped at merge time has decremented to zero " +
                "out from under them.");
        }
    }
}
