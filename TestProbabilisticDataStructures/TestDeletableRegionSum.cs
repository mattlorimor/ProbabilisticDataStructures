using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// A deletable filter's data region and its collision regions are the two halves of
    /// one filter of <c>m</c> bits, so together they come to no more than the bits a filter
    /// holds, and a reader holds a payload to that. Since the filter was sized in 64 bits
    /// (#232) that is <see cref="Buckets64.MaxCount"/>.
    /// <para>
    /// It once did not. The reader worked a region's size out in 32 bits, as
    /// <c>(m + collisions - 1) / collisions</c>, and a payload whose two regions came to
    /// more than 2^32 wrapped the numerator, which put the size at zero. The first add
    /// that met a bit already set then divided by it and threw
    /// <see cref="DivideByZeroException"/>. The size is now worked out in 64 bits, from
    /// regions held to a sum that cannot wrap. No constructor builds a filter past the
    /// line, and a payload that size cannot be built in the suite, so these call the shape
    /// check the reader and the pricing both call.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestDeletableRegionSum
    {
        /// <summary>The phrase the refusal uses for the sum, as distinct from its other refusals.</summary>
        private const string TheSum = "together";

        private static void AssertRefusedForTheSum(ulong m, ulong buckets, ulong collisions)
        {
            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => DeletableBloomFilter.CheckRegions(m, buckets, collisions));
            StringAssert.Contains(ex.Message, TheSum);
        }

        /// <summary>
        /// The payload the audit read under an unlimited budget, a data region of
        /// 4,294,967,295 bits and two collision regions, now describes a filter: the sum,
        /// 2^32 + 1, wraps nothing in 64 bits, and a region's size is half the data region,
        /// rounded up. The same shape at the 64-bit line is refused.
        /// </summary>
        [TestMethod]
        public void TestRegionsThatTogetherPassWhatAFilterAddressesAreRefused()
        {
            DeletableBloomFilter.CheckRegions(uint.MaxValue, uint.MaxValue, 2);
            AssertRefusedForTheSum(Buckets64.MaxCount, Buckets64.MaxCount, 2);
        }

        /// <summary>
        /// Either side of the line, where a comparison one off or a sum that wraps shows:
        /// <see cref="Buckets64.MaxCount"/> is the most a constructor can size a filter to,
        /// which splits into any data region and collision regions that add up to it, and
        /// regions whose sum passes 2^64 wrap it to something small, which must not pass.
        /// </summary>
        [TestMethod]
        public void TestTheRegionSumIsRefusedExactlyPastWhatSizingAllows()
        {
            var most = Buckets64.MaxCount;
            AssertRefusedForTheSum(most, most, 1);
            AssertRefusedForTheSum(most - 1, most - 1, 2);
            AssertRefusedForTheSum(1, 1, most);
            AssertRefusedForTheSum(most, most, most);
            AssertRefusedForTheSum(most + 1, most + 1, 1);
            // 2^63 and 2^63 come to 2^64, which wraps to nought, and 1 and 2^64 - 1 to nought too.
            AssertRefusedForTheSum(1UL << 63, 1UL << 63, 1UL << 63);
            AssertRefusedForTheSum(1, 1, ulong.MaxValue);
            AssertRefusedForTheSum(ulong.MaxValue, ulong.MaxValue, 1);

            DeletableBloomFilter.CheckRegions(most - 1, most - 1, 1);
            DeletableBloomFilter.CheckRegions(most - 2, most - 2, 2);
            DeletableBloomFilter.CheckRegions(1, 1, most - 1);
            DeletableBloomFilter.CheckRegions(1, 1, 1);
        }

        /// <summary>
        /// The limit is the constructor's: its two regions are the m that sizing returns,
        /// and the constructor refuses an m past what a 64-bit table holds. The largest m it
        /// accepts is found by bisection on the item count, and a filter of it, split as a
        /// payload would be, passes; the same construction at one more item is refused,
        /// naming n, before any filter exists.
        /// </summary>
        [TestMethod]
        public void TestTheLargestFilterTheConstructorSizesIsReadable()
        {
            const double rate = 0.01;
            var first = TestBitPlacement.SmallestCountReaching64(Buckets64.MaxCount + 1, rate);
            var m = Utils.OptimalM64(first - 1, rate);
            Assert.IsLessThanOrEqualTo(Buckets64.MaxCount, m);
            // The sizing is a double, and doubles near 2^61 are 512 apart.
            Assert.IsGreaterThan(Buckets64.MaxCount - 1024, m, "the bisection found a small filter");
            var refused = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DeletableBloomFilter(first, 10, rate));
            Assert.AreEqual("n", refused.ParamName);

            foreach (var r in new ulong[] { 1, 2, 1000, m / 2, m - 1 })
            {
                DeletableBloomFilter.CheckRegions(m - r, m - r, r);
            }
        }

        /// <summary>
        /// The invariant the check restates, on a filter small enough to build: the two
        /// regions of a deletable filter are the m that sizing returned.
        /// </summary>
        [TestMethod]
        public void TestTheRegionsOfABuiltFilterAreItsSize()
        {
            foreach (var (n, r, rate) in new[] { (100UL, 10UL, 0.01), (1000UL, 1UL, 0.1), (20UL, 20UL, 0.5) })
            {
                var filter = new DeletableBloomFilter(n, r, rate);
                Assert.AreEqual(Utils.OptimalM64(n, rate), filter.Buckets.count + filter.Collisions.count);
                DeletableBloomFilter.CheckRegions(filter.Buckets.count, filter.Buckets.count, filter.Collisions.count);
            }
        }
    }
}
