using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Pins behavior at the edges of a counter's range, which no test previously
    /// reached.
    /// </summary>
    [TestClass]
    public class TestSaturationAndBounds
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        /// <summary>
        /// A counting filter's counters saturate, and once they do the filter can no
        /// longer tell how many times an element was added. Removals then clear it
        /// after at most max steps rather than after as many steps as there were
        /// adds.
        /// <para>
        /// This is inherent to counting Bloom filters rather than a defect, but it is
        /// a sharp edge: Count() keeps rising while the counters stop, so the two
        /// disagree. Callers who delete must not assume adds and removes balance.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestCountingFilterCountersSaturate()
        {
            const byte bitsPerCounter = 4;
            const int max = (1 << bitsPerCounter) - 1;   // 15
            const int adds = 40;

            var f = new CountingBloomFilter(100, bitsPerCounter, 0.01);
            var a = Key("a");
            for (int i = 0; i < adds; i++)
            {
                f.Add(a);
            }

            // Count tracks insertions, not counter state, so it keeps climbing.
            Assert.AreEqual((uint)adds, f.Count());
            Assert.IsTrue(f.Test(a));

            var removals = 0;
            while (f.Test(a) && removals <= adds)
            {
                f.TestAndRemove(a);
                removals++;
            }

            Assert.IsFalse(f.Test(a), "element should be removable");
            Assert.IsLessThanOrEqualTo(max, removals,
                $"counters cap at {max}, so clearing the element should take at most that " +
                $"many removals, not the {adds} additions performed.");
        }

        /// <summary>
        /// Buckets store their maximum in a byte, so a bucket size wider than eight
        /// bits allocates the extra space but cannot use the extra range: the
        /// maximum clamps to 255.
        /// <para>
        /// This pins current behavior rather than endorsing it. Buckets.cs carries a
        /// TODO questioning whether the clamp is correct; whichever way that is
        /// resolved, it should be a deliberate change with this test updated, not a
        /// silent one.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestBucketSizeWiderThanAByteClampsToByteMax()
        {
            var narrow = new Buckets(10, 8);
            Assert.AreEqual((byte)255, narrow.MaxBucketValue());

            var wide = new Buckets(10, 16);
            Assert.AreEqual((byte)255, wide.MaxBucketValue(),
                "a 16-bit bucket allocates 16 bits but its maximum still clamps to 255.");

            // The clamp is a cap, not a failure: the bucket still round-trips values
            // inside the reachable range.
            wide.Set(0, 200);
            Assert.AreEqual(200u, wide.Get(0));
        }
    }
}
