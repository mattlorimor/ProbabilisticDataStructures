using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;
using System.Linq;
using System.Text;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestScalableBloomFilter
    {
        private static byte[] A_BYTES = Encoding.ASCII.GetBytes("a");
        private static byte[] B_BYTES = Encoding.ASCII.GetBytes("b");
        private static byte[] C_BYTES = Encoding.ASCII.GetBytes("c");
        private static byte[] X_BYTES = Encoding.ASCII.GetBytes("x");

        [TestMethod]
        public void TestNewDefaultScalableBloomFilter()
        {
            var f = ScalableBloomFilter.NewDefaultScalableBloomFilter(0.1);

            Assert.AreEqual(0.1, f.FP);
            Assert.AreEqual(10000u, f.Hint);
            Assert.AreEqual(0.8, f.R);
        }

        /// <summary>
        /// Capacity is the sum over the contained filters. This used to be asserted as
        /// the constant 15, which required a tightening ratio of 1 so that all three
        /// filters came out the same size -- a ratio the constructor now rejects,
        /// because it leaves the compound false positive rate unbounded. Summing the
        /// parts states the contract directly and does not depend on the sizing math.
        /// </summary>
        [TestMethod]
        public void TestScalableBloomCapacity()
        {
            var f = new ScalableBloomFilter(1, 0.1, 0.8);
            var first = f.Capacity();

            f.AddFilter();
            f.AddFilter();

            Assert.AreEqual((ulong)f.Filters.Sum(x => (long)x.Capacity()), f.Capacity());
            Assert.IsGreaterThan(first, f.Capacity(),
                "adding filters should increase the total capacity");
        }

        /// <summary>
        /// The capacity is the bits of every contained filter added up, and a growing
        /// filter's filters pass what a uint counts well before any one of them does:
        /// at a growth of 2, a hint of 1,000, 1% and r = 0.8, the eighteen filters at the
        /// ceiling hold 4,460,676,502 bits between them, the largest 2,291,218,187.
        /// <para>
        /// Built cheaply: the sum runs over the list of contained filters, so one filter
        /// of 9,585,059 bits listed 449 times -- the fewest copies that pass
        /// uint.MaxValue -- totals 4,303,691,491 bits for one filter's megabyte. A total
        /// kept in 32 bits wraps to 8,724,195, a saturated one stops at 4,294,967,295,
        /// and a checked one throws; each is told apart from the sum.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestCapacityCountsPastWhatAUintHolds()
        {
            var f = new ScalableBloomFilter(1_000_000, 0.01, 0.8);
            var filter = f.Filters[0];
            Assert.AreEqual(9_585_059u, filter.Capacity(), "the contained filter is not the size this test was built around");

            const int Copies = 449;
            f.Filters = Enumerable.Repeat(filter, Copies).ToList();

            const ulong Expected = 449UL * 9_585_059UL;
            Assert.IsGreaterThan((ulong)uint.MaxValue, Expected,
                "the total does not pass what a uint holds, so nothing below is checked");
            Assert.IsLessThan((ulong)uint.MaxValue, Expected - 9_585_059UL,
                "one copy fewer should not pass a uint, or the count is not the smallest that does");

            Assert.AreEqual(Expected, f.Capacity(), "the capacity is not the sum of the contained filters' bits");
        }

        // Ensures that K returns the number of hash functions used in each Bloom filter.
        [TestMethod]
        public void TestScalableBloomK()
        {
            var f = new ScalableBloomFilter(10, 0.1, 0.8);

            var k = f.K();
            Assert.AreEqual(4u, k);
        }

        /// <summary>
        /// Ensures that FillRatio returns the average fill ratio of the contained
        /// filters.
        /// </summary>
        [TestMethod]
        public void TestScalableFillRatio()
        {
            var f = new ScalableBloomFilter(100, 0.1, 0.8);
            f.SetHash(ProbabilisticDataStructures.Defaults.GetDefaultHashFunction());
            for (int i = 0; i < 200; i++)
            {
                f.Add(Encoding.ASCII.GetBytes(i.ToString()));
            }

            var fillRatio = f.FillRatio();
            if (fillRatio > 0.5)
            {
                Assert.Fail(string.Format("Expected less than or equal to 0.5, got {0}", fillRatio));
            }
        }

        /// <summary>
        /// Ensures that Test, Add, and TestAndAdd behave correctly.
        /// </summary>
        [TestMethod]
        public void TestScalableBloomTestAndAdd()
        {
            var f = new ScalableBloomFilter(1000, 0.01, 0.8);

            // 'a' is not in the filter.
            if (f.Test(A_BYTES))
            {
                Assert.Fail("'a' should not be a member");
            }

            var addedF = f.Add(A_BYTES);
            Assert.AreSame(f, addedF, "Returned ScalableBloomFilter should be the same instance");

            // 'a' is now in the filter.
            if (!f.Test(A_BYTES))
            {
                Assert.Fail("'a' should be a member");
            }

            // 'a' is still in the filter.
            if (!f.TestAndAdd(A_BYTES))
            {
                Assert.Fail("'a' should be a member");
            }

            // 'b' is not in the filter.
            if (f.TestAndAdd(B_BYTES))
            {
                Assert.Fail("'b' should not be a member");
            }

            // 'a' is still in the filter.
            if (!f.Test(A_BYTES))
            {
                Assert.Fail("'a' should be a member");
            }

            // 'b' is now in the filter.
            if (!f.Test(B_BYTES))
            {
                Assert.Fail("'b' should be a member");
            }

            // 'c' is not in the filter.
            if (f.Test(C_BYTES))
            {
                Assert.Fail("'c' should not be a member");
            }

            for (int i = 0; i < 10000; i++)
            {
                f.TestAndAdd(Encoding.ASCII.GetBytes(i.ToString()));
            }

            // 'x' should not be a false positive.
            if (f.Test(X_BYTES))
            {
                Assert.Fail("'x' should be a member");
            }
        }

        /// <summary>
        /// Ensures that Reset sets every bit to zero.
        /// </summary>
        [TestMethod]
        public void TestScalableBloomReset()
        {
            var f = new ScalableBloomFilter(10, 0.1, 0.8);
            for (int i = 0; i < 1000; i++)
            {
                f.Add(Encoding.ASCII.GetBytes(i.ToString()));
            }

            var count = f.Filters.Count;
            Assert.IsGreaterThan(1, count, $"Expected more than 1 filter, got {count}");

            var resetF = f.Reset();
            Assert.AreSame(f, resetF, "Returned ScalableBloomFilter should be the same instance");

            count = f.Filters.Count;
            Assert.AreEqual(1, count, $"Expected 1 filter, got {count}");

            foreach(var partition in f.Filters[0].Partitions)
            {
                for (uint i = 0; i < partition.count; i++)
                {
                    if (partition.Get(i) != 0)
                    {
                        Assert.Fail("Expected all bits to be unset");
                    } 
                }
            }
        }

        /// <summary>
        /// The constructors that are not given a growth factor all take the one default,
        /// so choosing a different default is a change to that constant and nothing else.
        /// </summary>
        [TestMethod]
        public void TestTheConstructorsWithoutAGrowthFactorShareOneDefault()
        {
            Assert.AreEqual(ScalableBloomFilter.DefaultGrowth, new ScalableBloomFilter(100, 0.01, 0.8).Growth);
            Assert.AreEqual(ScalableBloomFilter.DefaultGrowth,
                new ScalableBloomFilter(100, 0.01, 0.8, ProbabilisticDataStructures.Defaults.GetDefaultHashFunction()).Growth);
            Assert.AreEqual(ScalableBloomFilter.DefaultGrowth, ScalableBloomFilter.NewDefaultScalableBloomFilter(0.01).Growth);

            // A factor given is the factor kept, including one.
            Assert.AreEqual(1u, new ScalableBloomFilter(100, 0.01, 0.8, 1).Growth);
            Assert.AreEqual(4u, new ScalableBloomFilter(100, 0.01, 0.8, 4).Growth);
        }

        /// <summary>
        /// A filter that cannot add the contained filter it needs refuses the add that
        /// needed it, before anything changes, and says why. There are three ways to
        /// get there, each reached here through real adds at the parameters that reach
        /// it soonest (measured: the adds that succeed and the filters built are the
        /// probe's, against today's sizing).
        /// <list type="bullet">
        /// <item>The rate tightens past what sizing can use: at a growth of one with
        /// hint 1, a rate of one half and r = 0.5, filter 1,023 is built for 2^-1024,
        /// whose reciprocal is not a finite double. This is the wall a filter of hint
        /// 1,000 at 1% and r = 0.8 meets after about 3.16 million items. A growing
        /// filter meets it too when r is small enough: at r = 1e-300 the second filter's
        /// rate is 5e-301 and the third's underflows to zero.</item>
        /// <item>The next filter needs more bits than a contained filter addresses: a
        /// billion items at 0.8% need about ten billion.</item>
        /// <item>The next filter's capacity is more items than a contained filter is
        /// sized for: 1,000 times five million.</item>
        /// </list>
        /// One type stands for all three, so each is told apart by the condition its
        /// message names.
        /// </summary>
        [TestMethod]
        [DataRow(1u, 0.5, 0.5, 1u, 2046, 1023, "no finite reciprocal")]
        [DataRow(1u, 0.5, 1e-300, 2u, 5, 2, "no finite reciprocal")]
        [DataRow(1000u, 0.01, 0.8, 1_000_000u, 950, 1, "more bits than")]
        [DataRow(1000u, 0.01, 0.8, 5_000_000u, 950, 1, "more items than")]
        public void TestAnAddPastTheCeilingIsRefusedBeforeAnythingChanges(
            uint hint, double fp, double r, uint growth, int succeed, int filters, string condition)
        {
            var f = new ScalableBloomFilter(hint, fp, r, growth);
            for (int i = 0; i < succeed; i++)
            {
                f.Add(Encoding.ASCII.GetBytes($"w{i}"));
            }

            Assert.AreEqual(filters, f.Filters.Count, "the wall is not where it was measured to be");
            var before = f.ToByteArray();
            var next = Encoding.ASCII.GetBytes($"w{succeed}");

            var ex = Assert.ThrowsExactly<InvalidOperationException>(() => f.Add(next));
            StringAssert.Contains(ex.Message, condition);
            Assert.ThrowsExactly<InvalidOperationException>(() => f.TestAndAdd(next));

            CollectionAssert.AreEqual(before, f.ToByteArray(), "a refused add changed the filter");
            for (int i = 0; i < succeed; i++)
            {
                Assert.IsTrue(f.Test(Encoding.ASCII.GetBytes($"w{i}")), $"w{i} was lost at the ceiling");
            }
        }

        [TestMethod]
        public void BenchmarkScalableBloomAdd()
        {
            var n = 100000;
            var f = new ScalableBloomFilter(100000, 0.1, 0.8);
            var data = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                data[i] = Encoding.ASCII.GetBytes(i.ToString());
            }

            for (int i = 0; i < n; i++)
            {
                f.Add(data[i]);
            }
        }

        [TestMethod]
        public void BenchmarkScalableBloomTest()
        {
            var n = 100000;
            var f = new ScalableBloomFilter(100000, 0.1, 0.8);
            var data = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                data[i] = Encoding.ASCII.GetBytes(i.ToString());
            }

            for (int i = 0; i < n; i++)
            {
                f.Test(data[i]);
            }
        }

        [TestMethod]
        public void BenchmarkScalableBloomTestAndAdd()
        {
            var n = 100000;
            var f = new ScalableBloomFilter(100000, 0.1, 0.8);
            var data = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                data[i] = Encoding.ASCII.GetBytes(i.ToString());
            }

            for (int i = 0; i < n; i++)
            {
                f.TestAndAdd(data[i]);
            }
        }
    }
}
