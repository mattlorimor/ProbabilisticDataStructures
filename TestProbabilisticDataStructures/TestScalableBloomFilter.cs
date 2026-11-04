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

        /// <summary>
        /// A filter at a growth factor of 1 builds every contained filter for its hint, and
        /// holds as many of them as its rate tightens to before its ceiling, so a large hint
        /// passes the most a payload can be in filters it is allowed to hold. Its payload is
        /// its own thirty-two bytes, then each filter whole in a run: a prefix, an envelope
        /// and the filter's own payload. The filters are claimed as one built filter many
        /// times over, which writes the same bytes as that many filters of its size and
        /// builds one: 1,793 of a filter for a million items at 1%, each 1,198,222 bytes, is
        /// the first count past the ceiling, well short of the 3,161 filters a tightening
        /// ratio of 0.8 allows.
        /// </summary>
        [TestMethod]
        public void TestAFilterAtGrowthOneTooLargeToWriteIsRefusedNamingItsWholeSize()
        {
            const int filters = 1_793;
            var f = new ScalableBloomFilter(1_000_000, 0.01, 0.8, 1);
            var inner = f.Filters[0].ToByteArray().Length - (long)PersistenceFormat.EnvelopeLength;
            var payload = 32 + filters * (sizeof(uint) + PersistenceFormat.EnvelopeLength + inner);

            Assert.AreEqual(2_148_451_524L, payload, "the filter is not the size the row assumes");
            Assert.IsGreaterThan(PersistenceFormat.MaxPayloadLength, payload,
                "the claimed filters fit a payload, so there is nothing to refuse");
            Assert.IsLessThanOrEqualTo(PersistenceFormat.MaxPayloadLength,
                payload - (sizeof(uint) + PersistenceFormat.EnvelopeLength + inner),
                "one filter fewer is past the ceiling too, so the row is not the first past it");

            var claimed = Enumerable.Repeat(f.Filters[0], filters).ToList();
            TestPayloadCount.AssertRefusedWhileClaiming(f, payload, (f, "Filters", claimed));
        }

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

        /// <summary>
        /// The capacity check refuses exactly the capacities past what a contained filter
        /// is sized for, uint.MaxValue items, and accepts everything up to it. At
        /// practical rates the bits check binds first -- a billion items at 1% already
        /// need more than 2^32 bits -- so the rows above never reach this one, and a check
        /// refusing at half the ceiling survived the whole suite. At a rate of about 0.9
        /// an item costs under a quarter of a bit, so even uint.MaxValue items fit and
        /// the capacity alone decides.
        /// <para>
        /// The rows straddle 2^32 three ways: 65,535 * 65,537 is uint.MaxValue exactly and
        /// 65,536^2 is one past it; 2^31, at hint 1 and growth 2, is the first capacity a
        /// check at half the ceiling refuses, and 2^32 is the next; and hint 3 at growth 2
        /// reaches 3 * 2^30, between the two halves, then 3 * 2^31. The filter count is
        /// set through the list rather than grown into, since only its length sizes the
        /// next filter, and NextCapacity is asked directly so that nothing of 2^32 items
        /// is built.
        /// </para>
        /// </summary>
        [TestMethod]
        [DataRow(65_535u, 65_537u, 1, 4_294_967_295UL, true)]
        [DataRow(65_536u, 65_536u, 1, 4_294_967_296UL, false)]
        [DataRow(1u, 2u, 31, 2_147_483_648UL, true)]
        [DataRow(1u, 2u, 32, 4_294_967_296UL, false)]
        [DataRow(3u, 2u, 30, 3_221_225_472UL, true)]
        [DataRow(3u, 2u, 31, 6_442_450_944UL, false)]
        public void TestTheCapacityCeilingIsExactlyWhatAContainedFilterIsSizedFor(
            uint hint, uint growth, int index, ulong capacity, bool accepted)
        {
            const double Fp = 0.9, R = 0.99;
            var f = new ScalableBloomFilter(hint, Fp, R, growth);
            f.Filters = Enumerable.Repeat(f.Filters[0], index).ToList();
            var rate = Fp * Math.Pow(R, index);

            // The bits check cannot be what decides: at this rate even the largest
            // capacity a contained filter is sized for fits in its bits.
            Assert.IsLessThan(uint.MaxValue, Utils.OptimalM(uint.MaxValue, rate),
                "the bits check binds at this rate, so this row does not isolate the capacity check");

            if (accepted)
            {
                Assert.AreEqual(capacity, (ulong)f.NextCapacity(rate),
                    $"{hint} * {growth}^{index} is a capacity a contained filter holds");
            }
            else
            {
                var ex = Assert.ThrowsExactly<InvalidOperationException>(() => f.NextCapacity(rate),
                    $"{hint} * {growth}^{index} = {capacity:N0} is past what a contained filter holds");
                StringAssert.Contains(ex.Message, "more items than");
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
        /// a thousand, measured -- and at a ratio of 0.9 the 6,694th. The count is a closed
        /// form, restated here, so the filter must stop at exactly it. It is the wall of a
        /// growth factor of one, which builds every filter for the hint, so the growth is
        /// passed explicitly: the default grows, and reaches a wall of its own first.
        /// The ratios here are the ones whose ceilings a suite can reach: the filters hold
        /// a partition per hash function, and a thousand filters of a thousand partitions
        /// are a million objects.
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
            var filter = new ScalableBloomFilter(1, fpRate, r, growth: 1);

            var (refusal, filters) = AddUntilRefused(filter);

            Assert.IsInstanceOfType<InvalidOperationException>(refusal,
                $"the ceiling was reported as {refusal.GetType().Name}: {refusal.Message}");
            Assert.AreEqual(ceiling, filters,
                "the filter did not stop at the number of filters its rates allow");
            StringAssert.Contains(refusal.Message, ceiling.ToString(),
                "the refusal should name the ceiling");
            StringAssert.Contains(refusal.Message, "no finite reciprocal",
                "the refusal should say it is the rate that ran out");
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
        /// The filter at its ceiling still writes and reads, and reads back at the ceiling:
        /// the limit is the rates', not a state of the object. That a refused add leaves
        /// the filter byte for byte as it was, over repeated attempts of Add and
        /// TestAndAdd, is held by TestAnAddPastTheCeilingIsRefusedBeforeAnythingChanges.
        /// </summary>
        [TestMethod]
        public void TestAFilterAtItsCeilingRoundTripsAndStillRefuses()
        {
            var filter = new ScalableBloomFilter(1, 0.01, 0.5, growth: 1);
            var (refusal, _) = AddUntilRefused(filter);
            Assert.IsInstanceOfType<InvalidOperationException>(refusal);

            var restored = Persistence.FromByteArray<ScalableBloomFilter>(filter.ToByteArray());
            Assert.AreEqual(filter.Filters.Count, restored.Filters.Count);
            Assert.ThrowsExactly<InvalidOperationException>(
                () => restored.Add(Encoding.ASCII.GetBytes("more")));
        }

        /// <summary>
        /// A hint whose first filter needs more bits than a contained filter addresses was
        /// refused by that filter's constructor, naming its own parameter, <c>n</c>, and
        /// recommending ScalableBloomFilter, which is what the caller was using (#220). It
        /// is the hint that is too large, so it is the hint that is named, with the
        /// largest that builds at that rate.
        /// <para>
        /// The largest is worked out here by bisection over <see cref="Utils.OptimalM"/>,
        /// the sizing the contained filter's constructor calls, and the refused hint is
        /// the one past it. Building at the largest itself is half a gigabyte of bits, so
        /// that it is sized is asked of the sizing rather than built. The rates differ so
        /// that a figure fixed at one rate's largest, 448,089,842 at 1%, fails the others.
        /// </para>
        /// </summary>
        [TestMethod]
        [DataRow(0.01)]
        [DataRow(0.001)]
        [DataRow(1e-100)]
        public void TestAHintTooLargeForTheFirstFilterIsRefusedNamingTheHint(double fpRate)
        {
            var largest = (uint)CapacityRows.LargestSized(fpRate);
            Assert.IsLessThan(uint.MaxValue, largest, "at this rate every hint fits, so nothing is refused");
            _ = Utils.OptimalM(largest, fpRate);

            foreach (var hint in new[] { largest + 1, uint.MaxValue })
            {
                var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => new ScalableBloomFilter(hint, fpRate, 0.8));
                Assert.AreEqual("hint", ex.ParamName, $"a hint of {hint:N0} at {fpRate} was refused as another argument");
                Assert.AreEqual(hint, ex.ActualValue);
                StringAssert.Contains(ex.Message, largest.ToString("N0"),
                    "the refusal should give the largest hint that builds at this rate");
                StringAssert.Contains(ex.Message, "BloomFilter64",
                    "the refusal should name the filter that sizes in 64 bits");
                Assert.IsFalse(ex.Message.Contains("ScalableBloomFilter", StringComparison.Ordinal),
                    $"the refusal recommends the structure that refused: {ex.Message}");
            }
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
