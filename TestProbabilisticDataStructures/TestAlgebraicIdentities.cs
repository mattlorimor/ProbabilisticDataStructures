using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Exact algebraic identities: merging the sketches of two streams must produce,
    /// byte for byte, the sketch of the concatenated stream, and a structure whose
    /// state is a function of the multiset it was shown must not care about order or
    /// duplicates.
    /// <para>
    /// The oracle is the persisted form. Byte equality of two payloads checks every
    /// field of state at once -- counters, extremes, bookkeeping like a total count --
    /// including parts no behavioral test reads. And unlike the statistical bounds
    /// elsewhere in this suite, these identities are exact: there is no tolerance to
    /// hide inside, so any divergence is a defect and every failure reproduces.
    /// </para>
    /// <para>
    /// Deliberately absent, with reasons. StableBloomFilter and CuckooBloomFilter
    /// carry a random generator's position in their state, so two differently-built
    /// instances are not meant to be byte-equal. TopK's heap remembers eviction
    /// history. ScalableBloomFilter's growth boundaries depend on arrival order.
    /// InverseBloomFilter displaces by design. ThetaSketch turned out to be
    /// order-dependent in a way worth recording: its lazy double-width buffer makes
    /// the final theta depend on when compactions fired, so the same set ingested in
    /// two orders produces equal-length, different-byte states. Its exact
    /// characterization lives in TestModelBased instead, stated in terms that hold
    /// whatever theta it landed on.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestAlgebraicIdentities
    {
        private static byte[] K(string s) => Encoding.UTF8.GetBytes(s);

        /// <summary>600 unique, 100 shared with B, 50 duplicated.</summary>
        private static List<string> StreamA()
        {
            var a = new List<string>();
            for (int i = 0; i < 600; i++) a.Add($"a-{i}");
            for (int i = 0; i < 100; i++) a.Add($"shared-{i}");
            for (int i = 0; i < 50; i++) a.Add($"a-{i}");
            return a;
        }

        /// <summary>400 unique, the same 100 shared. The overlap matters: it is what
        /// separates a merge that unions from a merge that concatenates.</summary>
        private static List<string> StreamB()
        {
            var b = new List<string>();
            for (int i = 0; i < 400; i++) b.Add($"b-{i}");
            for (int i = 0; i < 100; i++) b.Add($"shared-{i}");
            return b;
        }

        private static List<string> Shuffled(List<string> xs, int seed)
        {
            var copy = new List<string>(xs);
            var rand = new Random(seed);
            for (int i = copy.Count - 1; i > 0; i--)
            {
                int j = rand.Next(i + 1);
                (copy[i], copy[j]) = (copy[j], copy[i]);
            }
            return copy;
        }

        private static void AssertSameBytes(string what, byte[] expected, byte[] actual)
        {
            CollectionAssert.AreEqual(expected, actual,
                $"{what}: the two states must be identical byte for byte. A behavioral " +
                "test would only notice this divergence if it happened to query the " +
                "part of the state that moved.");
        }

        [TestMethod]
        public void TestBloomFilterMergeIsTheFilterOfTheCombinedStream()
        {
            var A = StreamA(); var B = StreamB();
            var sa = new BloomFilter(2000, 0.01); foreach (var s in A) sa.Add(K(s));
            var sb = new BloomFilter(2000, 0.01); foreach (var s in B) sb.Add(K(s));
            var c = new BloomFilter(2000, 0.01); foreach (var s in A.Concat(B)) c.Add(K(s));

            sa.Merge(sb);
            AssertSameBytes("BloomFilter merge", c.ToByteArray(), sa.ToByteArray());
        }

        /// <summary>
        /// The 64-bit and partitioned variants merge through the same OR mechanics as
        /// the classic filter, but through their own code paths -- Buckets64 for one,
        /// a partition loop for the other -- so each is held to the identity
        /// separately. The README's byte-for-byte claim extends exactly as far as the
        /// structures listed in this file.
        /// </summary>
        [TestMethod]
        public void TestBloomVariantMergesAreTheFilterOfTheCombinedStream()
        {
            var A = StreamA(); var B = StreamB();

            var sa64 = new BloomFilter64(2000, 0.01); foreach (var s in A) sa64.Add(K(s));
            var sb64 = new BloomFilter64(2000, 0.01); foreach (var s in B) sb64.Add(K(s));
            var c64 = new BloomFilter64(2000, 0.01); foreach (var s in A.Concat(B)) c64.Add(K(s));
            sa64.Merge(sb64);
            AssertSameBytes("BloomFilter64 merge", c64.ToByteArray(), sa64.ToByteArray());

            var sap = new PartitionedBloomFilter(2000, 0.01); foreach (var s in A) sap.Add(K(s));
            var sbp = new PartitionedBloomFilter(2000, 0.01); foreach (var s in B) sbp.Add(K(s));
            var cp = new PartitionedBloomFilter(2000, 0.01); foreach (var s in A.Concat(B)) cp.Add(K(s));
            sap.Merge(sbp);
            AssertSameBytes("PartitionedBloomFilter merge", cp.ToByteArray(), sap.ToByteArray());
        }

        /// <summary>
        /// Counter addition commutes with stream concatenation -- while no counter
        /// saturates. At the width used here nothing gets near the cap; the merge
        /// method documents its own behavior at saturation.
        /// </summary>
        [TestMethod]
        public void TestCountingBloomFilterMergeIsTheFilterOfTheCombinedStream()
        {
            var A = StreamA(); var B = StreamB();
            var sa = CountingBloomFilter.NewDefaultCountingBloomFilter(2000, 0.01);
            var sb = CountingBloomFilter.NewDefaultCountingBloomFilter(2000, 0.01);
            var c = CountingBloomFilter.NewDefaultCountingBloomFilter(2000, 0.01);
            foreach (var s in A) sa.Add(K(s));
            foreach (var s in B) sb.Add(K(s));
            foreach (var s in A.Concat(B)) c.Add(K(s));

            sa.Merge(sb);
            AssertSameBytes("CountingBloomFilter merge", c.ToByteArray(), sa.ToByteArray());
        }

        /// <summary>
        /// Byte equality here also covers the item-count bookkeeping: TotalCount is in
        /// the payload, so a merge that added the matrices but forgot the counts would
        /// fail this test while passing every frequency query.
        /// </summary>
        [TestMethod]
        public void TestCountMinSketchMergeIsTheSketchOfTheCombinedStream()
        {
            var A = StreamA(); var B = StreamB();
            var sa = new CountMinSketch(0.01, 0.01); foreach (var s in A) sa.Add(K(s));
            var sb = new CountMinSketch(0.01, 0.01); foreach (var s in B) sb.Add(K(s));
            var c = new CountMinSketch(0.01, 0.01); foreach (var s in A.Concat(B)) c.Add(K(s));

            sa.Merge(sb);
            AssertSameBytes("CountMinSketch merge", c.ToByteArray(), sa.ToByteArray());
        }

        [TestMethod]
        public void TestCountSketchMergeIsTheSketchOfTheCombinedStream()
        {
            var A = StreamA(); var B = StreamB();
            var sa = new CountSketch(0.02, 0.01); foreach (var s in A) sa.Add(K(s));
            var sb = new CountSketch(0.02, 0.01); foreach (var s in B) sb.Add(K(s));
            var c = new CountSketch(0.02, 0.01); foreach (var s in A.Concat(B)) c.Add(K(s));

            sa.Merge(sb);
            AssertSameBytes("CountSketch merge", c.ToByteArray(), sa.ToByteArray());
        }

        /// <summary>
        /// Register-wise max commutes with union. The overlap between the streams is
        /// what gives this teeth: those items reach both sketches, and any merge that
        /// did something other than max -- sum, most recently, either consistently --
        /// diverges on exactly the registers they share.
        /// </summary>
        [TestMethod]
        public void TestHyperLogLogMergeIsTheSketchOfTheCombinedStream()
        {
            var A = StreamA(); var B = StreamB();
            var sa = new HyperLogLog(1024); foreach (var s in A) sa.Add(K(s));
            var sb = new HyperLogLog(1024); foreach (var s in B) sb.Add(K(s));
            var c = new HyperLogLog(1024); foreach (var s in A.Concat(B)) c.Add(K(s));

            Assert.IsTrue(sa.Merge(sb));
            AssertSameBytes("HyperLogLog merge", c.ToByteArray(), sa.ToByteArray());
        }

        /// <summary>
        /// The sparse and dense representations have separate merge paths, so both are
        /// held to the identity separately. The dense case straddles the conversion:
        /// each side is built past the sparse threshold so the merge runs register
        /// against register.
        /// </summary>
        [TestMethod]
        public void TestHyperLogLogPlusMergeIsTheSketchOfTheCombinedStream()
        {
            var A = StreamA(); var B = StreamB();
            var sa = new HyperLogLogPlus(14); foreach (var s in A) sa.Add(K(s));
            var sb = new HyperLogLogPlus(14); foreach (var s in B) sb.Add(K(s));
            var c = new HyperLogLogPlus(14); foreach (var s in A.Concat(B)) c.Add(K(s));
            AssertSameBytes("HyperLogLogPlus merge (sparse)",
                c.ToByteArray(), sa.Merge(sb).ToByteArray());

            var big = Enumerable.Range(0, 9000).Select(i => $"x-{i}").ToList();
            var da = new HyperLogLogPlus(10); foreach (var s in big.Take(5000)) da.Add(K(s));
            var db = new HyperLogLogPlus(10); foreach (var s in big.Skip(4000)) db.Add(K(s));
            var dc = new HyperLogLogPlus(10); foreach (var s in big) dc.Add(K(s));
            AssertSameBytes("HyperLogLogPlus merge (dense)",
                dc.ToByteArray(), da.Merge(db).ToByteArray());
        }

        /// <summary>
        /// Bucket counts add, and the payload also holds the exact minimum and maximum,
        /// so a merge that combined the stores but not the extremes fails here while
        /// answering every quantile between them correctly.
        /// </summary>
        [TestMethod]
        public void TestDDSketchMergeIsTheSketchOfTheCombinedStream()
        {
            // Both global extremes live in the other sketch, deliberately. The first
            // version of this test had the value 1 in both streams, and a merge that
            // forgot to propagate the other side's minimum passed it: the minimum
            // never needed propagating. An identity test is only as strong as the
            // asymmetry of its inputs.
            var sa = new DDSketch(0.01); for (int i = 0; i < 500; i++) sa.Add(100 + (i * 7) % 1000);
            var sb = new DDSketch(0.01); for (int i = 0; i < 500; i++) sb.Add(1 + (i * 13) % 3000);
            var c = new DDSketch(0.01);
            for (int i = 0; i < 500; i++) c.Add(100 + (i * 7) % 1000);
            for (int i = 0; i < 500; i++) c.Add(1 + (i * 13) % 3000);

            sa.Merge(sb);
            AssertSameBytes("DDSketch merge", c.ToByteArray(), sa.ToByteArray());
        }

        /// <summary>
        /// The strongest of the merge identities, because a quotient filter's layout is
        /// physical: runs, clusters, and three metadata bits per slot. Byte equality
        /// says the merged filter rebuilt exactly the canonical layout the combined
        /// stream produces, multiplicity included.
        /// </summary>
        [TestMethod]
        public void TestQuotientFilterMergeIsTheFilterOfTheCombinedStream()
        {
            var A = StreamA(); var B = StreamB();
            var sa = new QuotientFilter(2000, 0.01); foreach (var s in A) sa.Add(K(s));
            var sb = new QuotientFilter(2000, 0.01); foreach (var s in B) sb.Add(K(s));
            var c = new QuotientFilter(2000, 0.01); foreach (var s in A.Concat(B)) c.Add(K(s));

            AssertSameBytes("QuotientFilter merge", c.ToByteArray(), sa.Merge(sb).ToByteArray());
        }

        /// <summary>
        /// A MinHash signature is the per-position minimum over the set, and minimum
        /// distributes over union. This is the identity that makes signatures mergeable
        /// without the underlying sets -- the property the MinHash index relies on.
        /// </summary>
        [TestMethod]
        public void TestMinHashSignatureOfAUnionIsTheElementwiseMin()
        {
            var bagA = StreamA().Distinct().ToArray();
            var bagB = StreamB().Distinct().ToArray();
            var union = bagA.Concat(bagB).Distinct().ToArray();

            var sa = MinHash.Signature(bagA, 128);
            var sb = MinHash.Signature(bagB, 128);
            var su = MinHash.Signature(union, 128);

            for (int i = 0; i < 128; i++)
            {
                Assert.AreEqual(Math.Min(sa.Values[i], sb.Values[i]), su.Values[i],
                    $"position {i}: the union's signature must be the elementwise " +
                    "minimum of the two signatures. If it is not, signatures cannot " +
                    "stand in for the sets they summarize.");
            }
        }

        /// <summary>
        /// A structure whose state is a function of the multiset it was shown must
        /// land on identical bytes whatever order the multiset arrived in. For the
        /// quotient filter this is a statement about its physical layout: shifts and
        /// cluster rebuilds along two different histories must converge.
        /// </summary>
        [TestMethod]
        public void TestInsertionOrderDoesNotChangeTheState()
        {
            var A = StreamA();
            var A2 = Shuffled(A, 41);

            var f1 = new BloomFilter(2000, 0.01); foreach (var s in A) f1.Add(K(s));
            var f2 = new BloomFilter(2000, 0.01); foreach (var s in A2) f2.Add(K(s));
            AssertSameBytes("BloomFilter order", f1.ToByteArray(), f2.ToByteArray());

            var q1 = new QuotientFilter(2000, 0.01); foreach (var s in A) q1.Add(K(s));
            var q2 = new QuotientFilter(2000, 0.01); foreach (var s in A2) q2.Add(K(s));
            AssertSameBytes("QuotientFilter order", q1.ToByteArray(), q2.ToByteArray());

            var keys = Enumerable.Range(0, 500).Select(i => $"k{i:D7}").ToList();
            var i1 = new InvertibleBloomLookupTable(100, 8); foreach (var s in keys) i1.Add(K(s));
            var i2 = new InvertibleBloomLookupTable(100, 8); foreach (var s in Shuffled(keys, 43)) i2.Add(K(s));
            AssertSameBytes("IBLT order", i1.ToByteArray(), i2.ToByteArray());
        }

        /// <summary>
        /// Add and remove are exact inverses in these structures, so removing
        /// everything -- in a different order than it went in -- must land on the
        /// empty state to the byte. Residue after a full unwind is how counter drift
        /// and sign errors show themselves.
        /// </summary>
        [TestMethod]
        public void TestRemovingEverythingRestoresTheEmptyState()
        {
            var keys = Enumerable.Range(0, 500).Select(i => $"k{i:D7}").ToList();
            var fresh = new InvertibleBloomLookupTable(100, 8);
            var used = new InvertibleBloomLookupTable(100, 8);
            foreach (var s in keys) used.Add(K(s));
            foreach (var s in Shuffled(keys, 47)) used.Remove(K(s));
            AssertSameBytes("IBLT unwind", fresh.ToByteArray(), used.ToByteArray());

            var A = StreamA();
            var cf = CountingBloomFilter.NewDefaultCountingBloomFilter(2000, 0.01);
            var cfFresh = CountingBloomFilter.NewDefaultCountingBloomFilter(2000, 0.01);
            foreach (var s in A) cf.Add(K(s));
            foreach (var s in Shuffled(A, 53)) cf.TestAndRemove(K(s));
            AssertSameBytes("CountingBloomFilter unwind",
                cfFresh.ToByteArray(), cf.ToByteArray());
        }

        /// <summary>
        /// A cardinality estimator must be blind to repetition: showing it the same
        /// stream twice is showing it the same set.
        /// </summary>
        [TestMethod]
        public void TestReAddingTheSameStreamChangesNothing()
        {
            var A = StreamA();
            var once = new HyperLogLog(1024); foreach (var s in A) once.Add(K(s));
            var twice = new HyperLogLog(1024); foreach (var s in A.Concat(A)) twice.Add(K(s));
            AssertSameBytes("HyperLogLog duplicates", once.ToByteArray(), twice.ToByteArray());

            var p1 = new HyperLogLogPlus(12); foreach (var s in A) p1.Add(K(s));
            var p2 = new HyperLogLogPlus(12); foreach (var s in A.Concat(A)) p2.Add(K(s));
            AssertSameBytes("HyperLogLogPlus duplicates", p1.ToByteArray(), p2.ToByteArray());
        }
    }
}
