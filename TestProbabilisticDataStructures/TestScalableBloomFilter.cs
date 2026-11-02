using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;
using System;
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

            Assert.AreEqual(f.Filters.Sum(x => (long)x.Capacity()), (long)f.Capacity());
            Assert.IsGreaterThan(first, f.Capacity(),
                "adding filters should increase the total capacity");
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

        /// <summary>
        /// The number of filters a scalable filter can hold, worked out here from the
        /// rates rather than read from the code. The first is at the rate asked for and
        /// each next is a factor r tighter, and a Bloom filter at a rate needs the
        /// reciprocal of it for its hash count, which stops being a number when the rate
        /// is below 1 / double.MaxValue. So the i-th (counting from nought) can be built
        /// while fp * r^i is at least that, which is i &lt;= ln(fp * double.MaxValue) /
        /// ln(1 / r).
        /// </summary>
        private static int CeilingFor(double fpRate, double r) =>
            (int)Math.Floor(Math.Log(fpRate * double.MaxValue) / Math.Log(1 / r)) + 1;

        /// <summary>
        /// Adds distinct keys until one is refused, returning the refusal and how many
        /// filters the filter held. A hint of one makes each filter fill after a couple of
        /// keys, so the ceiling is a few thousand adds away rather than millions.
        /// </summary>
        private static (Exception Refusal, int Filters) AddUntilRefused(ScalableBloomFilter filter)
        {
            for (var i = 0; i < 1_000_000; i++)
            {
                try
                {
                    filter.Add(BitConverter.GetBytes(i));
                }
                catch (Exception e)
                {
                    return (e, filter.Filters.Count);
                }
            }

            Assert.Fail("a million adds never reached the ceiling");
            return (null, 0);
        }

        /// <summary>
        /// A scalable filter tightens each new filter's rate by r, and the rate runs out:
        /// fp * r^i falls to where its reciprocal is not a number, and the filter then
        /// threw an OverflowException from inside the sizing arithmetic. At a rate of 1%
        /// and a ratio of 0.8 that is the 3,162nd filter -- 3.16 million items at a hint of
        /// a thousand, measured -- and at a ratio of 0.9 the 6,694th. The refusal says what
        /// the ceiling is and what to do about it. The ratios here are the ones whose
        /// ceilings a suite can reach: the filters hold a partition per hash function, and
        /// a thousand filters of a thousand partitions are a million objects.
        /// </summary>
        [TestMethod]
        [DataRow(0.01, 0.5)]
        [DataRow(0.01, 0.3)]
        [DataRow(0.3, 0.5)]
        [DataRow(1e-100, 0.5)]
        [DataRow(1e-308, 0.8)]
        public void TestAScalableFilterThatRunsOutOfRatesIsRefusedNamingTheCeiling(
            double fpRate, double r)
        {
            var ceiling = CeilingFor(fpRate, r);
            var filter = new ScalableBloomFilter(1, fpRate, r);

            var (refusal, filters) = AddUntilRefused(filter);

            Assert.IsInstanceOfType<InvalidOperationException>(refusal,
                $"the ceiling was reported as {refusal.GetType().Name}: {refusal.Message}");
            Assert.AreEqual(ceiling, filters,
                "the filter did not stop at the number of filters its rates allow");
            StringAssert.Contains(refusal.Message, ceiling.ToString(),
                "the refusal should name the ceiling");
            StringAssert.Contains(refusal.Message, "BloomFilter64",
                "the refusal should name what to use instead");
        }

        /// <summary>
        /// The two ceilings the changelog gives, so that the formula above is held to
        /// numbers someone can check by hand. They were measured too: a thousand-item
        /// hint at 1% and 0.8 threw on the 3,156,212th add, holding 3,161 filters.
        /// </summary>
        [TestMethod]
        public void TestTheCeilingAtOnePercentIsThreeThousandOneHundredAndSixtyOne()
        {
            Assert.AreEqual(3161, CeilingFor(0.01, 0.8));
            Assert.AreEqual(6693, CeilingFor(0.01, 0.9));
        }

        /// <summary>
        /// A refused add changes nothing: the filter is the same bytes it was, answers
        /// every query it did, and the refusal is the same the next time. The ceiling is
        /// reached when a new filter is wanted, which is before the add stores anything.
        /// </summary>
        [TestMethod]
        public void TestARefusedAddLeavesTheFilterAsItWas()
        {
            var filter = new ScalableBloomFilter(1, 0.01, 0.5);
            var (refusal, _) = AddUntilRefused(filter);
            Assert.IsInstanceOfType<InvalidOperationException>(refusal);

            var before = filter.ToByteArray();
            var key = Encoding.ASCII.GetBytes("one more");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => filter.Add(key));
                Assert.ThrowsExactly<InvalidOperationException>(() => filter.TestAndAdd(key));
            }

            CollectionAssert.AreEqual(before, filter.ToByteArray(),
                "a refused add changed the filter");
            Assert.IsFalse(filter.Test(key), "a refused add left its key in the filter");
            Assert.IsTrue(filter.Test(BitConverter.GetBytes(0)),
                "a key added before the ceiling is no longer found");
        }

        /// <summary>
        /// The filter at its ceiling still writes and reads, and reads back at the ceiling:
        /// the limit is the rates', not a state of the object.
        /// </summary>
        [TestMethod]
        public void TestAFilterAtItsCeilingRoundTripsAndStillRefuses()
        {
            var filter = new ScalableBloomFilter(1, 0.01, 0.5);
            AddUntilRefused(filter);

            var restored = Persistence.FromByteArray<ScalableBloomFilter>(filter.ToByteArray());
            Assert.AreEqual(filter.Filters.Count, restored.Filters.Count);
            Assert.ThrowsExactly<InvalidOperationException>(
                () => restored.Add(Encoding.ASCII.GetBytes("more")));
        }

        /// <summary>
        /// A first rate whose reciprocal is not a number cannot build even one filter, and
        /// said so with an OverflowException from the sizing. That is a mistake in the
        /// rate, and it is reported against it.
        /// </summary>
        [TestMethod]
        public void TestARateWhoseReciprocalIsNotANumberIsRefusedNamingTheRate()
        {
            Assert.IsTrue(double.IsInfinity(1 / 1e-320));

            var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new ScalableBloomFilter(10, 1e-320, 0.8));
            Assert.AreEqual("fpRate", ex.ParamName);
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
