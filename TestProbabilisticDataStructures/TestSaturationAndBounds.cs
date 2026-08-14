using System;
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
        /// A bucket's maximum value is held in a byte, so eight bits is the widest
        /// bucket that can be fully used. Wider ones are rejected rather than
        /// accepted and silently capped: the bit packing would allocate the extra
        /// space, but the value could still never exceed 255, so the memory would be
        /// paid for and unreachable.
        /// <para>
        /// Upstream Go reaches the same 255 ceiling by different means -- its max
        /// field is a uint8, so the expression wraps rather than clamping -- so this
        /// rejects a request that has never been honorable in either implementation.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestBucketSizeWiderThanAByteIsRejected()
        {
            var widest = new Buckets(10, 8);
            Assert.AreEqual((byte)255, widest.MaxBucketValue());

            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new Buckets(10, 9));
            StringAssert.Contains(ex.Message, "at most 8 bits");

            Assert.Throws<ArgumentOutOfRangeException>(() => new Buckets64(10, 16));
        }

        /// <summary>
        /// Every bucket width up to the limit round-trips values across its full
        /// range, including the boundary where a bucket spans two bytes.
        /// </summary>
        [TestMethod]
        public void TestSupportedBucketWidthsRoundTrip()
        {
            for (byte bits = 1; bits <= 8; bits++)
            {
                var b = new Buckets(16, bits);
                var max = b.MaxBucketValue();

                b.Set(0, max);
                b.Set(3, max);

                Assert.AreEqual((uint)max, b.Get(0), $"bucketSize {bits} lost its value");
                Assert.AreEqual((uint)max, b.Get(3), $"bucketSize {bits} lost a spanning value");
                Assert.AreEqual(0u, b.Get(1), $"bucketSize {bits} bled into a neighbour");
            }
        }
    }
}
