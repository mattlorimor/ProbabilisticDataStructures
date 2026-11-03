using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// A deletable filter's data region and its collision regions are the two halves of
    /// one filter of <c>m</c> bits, so together they come to no more than a 32-bit filter
    /// addresses, and a reader holds a payload to that.
    /// <para>
    /// It once did not. The reader worked a region's size out in 32 bits, as
    /// <c>(m + collisions - 1) / collisions</c>, and a payload whose two regions came to
    /// more than 2^32 wrapped the numerator, which put the size at zero. The first add
    /// that met a bit already set then divided by it and threw
    /// <see cref="DivideByZeroException"/>. No constructor builds such a filter; the
    /// payload is a crafted or damaged one, and at more than 512 MiB it is one that only
    /// a raised or unlimited budget reads. A payload that size cannot be built in the
    /// suite, so these call the shape check the reader and the pricing both call.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestDeletableRegionSum
    {
        /// <summary>The phrase the refusal uses for the sum, as distinct from its other refusals.</summary>
        private const string TheSum = "together";

        private static void AssertRefusedForTheSum(uint m, uint buckets, uint collisions)
        {
            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => DeletableBloomFilter.CheckRegions(m, buckets, collisions));
            StringAssert.Contains(ex.Message, TheSum);
        }

        /// <summary>
        /// The payload the audit read under an unlimited budget: a data region of
        /// 4,294,967,295 bits and two collision regions. The sum is 2^32 + 1, which a
        /// 32-bit numerator wraps to 1.
        /// </summary>
        [TestMethod]
        public void TestRegionsThatTogetherPassWhatAFilterAddressesAreRefused()
        {
            AssertRefusedForTheSum(uint.MaxValue, uint.MaxValue, 2);
        }

        /// <summary>
        /// Either side of the line, where a comparison one off or a sum that wraps shows:
        /// 2^32 exactly wraps to zero in 32 bits, and 2^32 - 1 is the most a constructor
        /// can size a filter to, which splits into any data region and collision regions
        /// that add up to it.
        /// </summary>
        [TestMethod]
        public void TestTheRegionSumIsRefusedExactlyPastWhatSizingAllows()
        {
            AssertRefusedForTheSum(uint.MaxValue, uint.MaxValue, 1);
            AssertRefusedForTheSum(uint.MaxValue - 1, uint.MaxValue - 1, 2);
            AssertRefusedForTheSum(1, 1, uint.MaxValue);
            AssertRefusedForTheSum(uint.MaxValue, uint.MaxValue, uint.MaxValue);

            DeletableBloomFilter.CheckRegions(uint.MaxValue - 1, uint.MaxValue - 1, 1);
            DeletableBloomFilter.CheckRegions(uint.MaxValue - 2, uint.MaxValue - 2, 2);
            DeletableBloomFilter.CheckRegions(1, 1, uint.MaxValue - 1);
            DeletableBloomFilter.CheckRegions(1, 1, 1);
        }

        /// <summary>
        /// The limit is the constructor's: its two regions are the m that sizing returns,
        /// and sizing refuses an m past <c>uint.MaxValue</c>. The largest m it returns is
        /// found by bisection on the item count, and a filter of it, split as a payload
        /// would be, passes; the same construction at one more item is refused by
        /// sizing before any filter exists.
        /// </summary>
        [TestMethod]
        public void TestTheLargestFilterTheConstructorSizesIsReadable()
        {
            const double rate = 0.01;
            uint low = 1, high = uint.MaxValue;
            while (low < high)
            {
                var middle = low + (high - low + 1) / 2;
                try
                {
                    Utils.OptimalM(middle, rate);
                    low = middle;
                }
                catch (ArgumentOutOfRangeException)
                {
                    high = middle - 1;
                }
            }

            var m = Utils.OptimalM(low, rate);
            Assert.IsLessThanOrEqualTo(uint.MaxValue, (ulong)m);
            Assert.IsGreaterThan(uint.MaxValue - 1_000_000_000u, m, "the bisection found a small filter");
            Assert.Throws<ArgumentOutOfRangeException>(() => Utils.OptimalM(low + 1, rate));

            foreach (var r in new uint[] { 1, 2, 1000, m / 2, m - 1 })
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
            foreach (var (n, r, rate) in new[] { (100u, 10u, 0.01), (1000u, 1u, 0.1), (20u, 20u, 0.5) })
            {
                var filter = new DeletableBloomFilter(n, r, rate);
                Assert.AreEqual((ulong)Utils.OptimalM(n, rate), filter.Buckets.count + (ulong)filter.Collisions.count);
                DeletableBloomFilter.CheckRegions(filter.Buckets.count, filter.Buckets.count, filter.Collisions.count);
            }
        }
    }
}
