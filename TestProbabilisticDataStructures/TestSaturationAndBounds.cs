using System;
using System.Linq;
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
        /// longer tell how many elements they stand for. A saturated counter is
        /// therefore never decremented: the element becomes permanently unremovable
        /// and its space is never reclaimed.
        /// <para>
        /// The alternative -- resuming the count from the ceiling -- reaches zero
        /// while elements that need the counter are still present, which is a false
        /// negative. Deleting without introducing those is the whole of what a
        /// counting filter adds to a plain one, so leaking space is the lesser cost.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestCountingFilterSaturatedCountersAreNotDecremented()
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

            // Count tracks insertions, not counter state, so it keeps climbing past
            // the ceiling the counters stopped at.
            Assert.AreEqual((uint)adds, f.Count());
            Assert.IsTrue(f.Test(a));

            // Every counter is saturated, so no number of removals clears it.
            for (int i = 0; i < adds + max; i++)
            {
                f.TestAndRemove(a);
            }

            Assert.IsTrue(f.Test(a),
                $"counters saturated at {max} after {adds} additions, so they can no " +
                "longer be safely decremented and the element stays present.");
        }

        /// <summary>
        /// The property that distinguishes a counting filter from a plain one:
        /// removing elements must not make the remaining ones disappear. Narrow
        /// counters make this sharp, because they saturate at ordinary loads -- with
        /// one bit per counter every shared bucket saturates immediately.
        /// <para>
        /// Decrementing saturated counters broke this badly. At one bit per counter
        /// 745 of 1000 surviving elements became unfindable, and at two bits, 42.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestCountingFilterRemovalNeverHidesRemainingElements()
        {
            foreach (byte bitsPerCounter in new byte[] { 1, 2, 4, 8 })
            {
                const int count = 2000;
                var f = new CountingBloomFilter(count, bitsPerCounter, 0.01);

                for (int i = 0; i < count; i++)
                {
                    f.Add(Key($"b{bitsPerCounter}-item-{i}"));
                }

                for (int i = 0; i < count / 2; i++)
                {
                    f.TestAndRemove(Key($"b{bitsPerCounter}-item-{i}"));
                }

                var missing = Enumerable.Range(count / 2, count / 2)
                    .Count(i => !f.Test(Key($"b{bitsPerCounter}-item-{i}")));

                Assert.AreEqual(0, missing,
                    $"{bitsPerCounter}-bit counters: {missing} elements disappeared " +
                    "after removing unrelated ones.");
            }
        }

        /// <summary>
        /// A zero-bit bucket holds no value and allocates no storage, so the first
        /// read indexed an empty array and threw IndexOutOfRangeException. It is
        /// rejected where it is passed instead.
        /// </summary>
        [TestMethod]
        public void TestZeroWidthBucketIsRejected()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new CountingBloomFilter(100, 0, 0.01));
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
