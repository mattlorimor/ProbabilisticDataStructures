using System;
using System.Buffers.Binary;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestSlidingWindow
    {
        private static byte[] Item(long i)
        {
            var key = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(key, i);
            return key;
        }

        /// <summary>A clock the test moves by hand, since the behaviour is about time.</summary>
        private sealed class TestClock
        {
            internal DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

            internal void Advance(TimeSpan by) => this.Now += by;
        }

        [TestMethod]
        public void TestWhatWasAddedIsVisible()
        {
            var clock = new TestClock();
            var window = new SlidingWindow<HyperLogLogPlus>(
                TimeSpan.FromMinutes(60), buckets: 60,
                () => new HyperLogLogPlus(12), (a, b) => a.Merge(b), () => clock.Now);

            for (var i = 0; i < 500; i++)
            {
                window.Current.Add(Item(i));
            }

            Assert.AreEqual(500ul, window.Merged().Count());
        }
        /// <summary>
        /// Precision 14, so that every count these tests take stays inside the
        /// estimator's exact sparse form -- it keeps hashes rather than registers below
        /// 2,048 distinct items, and the assertions here are exact equalities.
        /// <para>
        /// Precision 12 was the first choice and was wrong: its sparse form ends at 512,
        /// so a test counting 600 items compared an exact expectation against an estimate
        /// and failed at 607. The window was right; the test was asserting the wrong kind
        /// of thing.
        /// </para>
        /// </summary>
        private static SlidingWindow<HyperLogLogPlus> Estimator(TestClock clock, int minutes = 60, int buckets = 60)
        {
            return new SlidingWindow<HyperLogLogPlus>(
                TimeSpan.FromMinutes(minutes), buckets,
                () => new HyperLogLogPlus(14), (a, b) => a.Merge(b), () => clock.Now);
        }

        /// <summary>
        /// The whole point: what happened long enough ago stops counting.
        /// </summary>
        [TestMethod]
        public void TestWhatFallsOutOfTheWindowStopsCounting()
        {
            var clock = new TestClock();
            var window = Estimator(clock);

            for (var i = 0; i < 500; i++)
            {
                window.Current.Add(Item(i));
            }

            Assert.AreEqual(500ul, window.Merged().Count());

            // Past the end of the window, so none of it is left.
            clock.Advance(TimeSpan.FromMinutes(61));
            Assert.AreEqual(0ul, window.Merged().Count(),
                "items an hour past a one-hour window were still counted");

            // And the window still works afterwards, rather than being spent.
            for (var i = 1000; i < 1300; i++)
            {
                window.Current.Add(Item(i));
            }

            Assert.AreEqual(300ul, window.Merged().Count());
        }

        /// <summary>
        /// A window is not a switch: as it rolls, the oldest bucket leaves and the rest
        /// stay, so the count falls gradually rather than all at once.
        /// </summary>
        [TestMethod]
        public void TestTheWindowRollsRatherThanResetting()
        {
            var clock = new TestClock();
            var window = Estimator(clock, minutes: 60, buckets: 6);

            // A hundred distinct items per ten-minute bucket, advancing *between* writes
            // rather than after the last -- one more step and the first bucket would
            // already have rolled off, which is what the window is for.
            for (var bucket = 0; bucket < 6; bucket++)
            {
                if (bucket > 0)
                {
                    clock.Advance(TimeSpan.FromMinutes(10));
                }

                for (var i = 0; i < 100; i++)
                {
                    window.Current.Add(Item((bucket * 1000) + i));
                }
            }

            Assert.AreEqual(600ul, window.Merged().Count(),
                "six buckets of a hundred did not come to six hundred");

            // Each further step drops exactly one bucket's worth, rather than the whole
            // window going at once.
            for (var expected = 500ul; ; expected -= 100)
            {
                clock.Advance(TimeSpan.FromMinutes(10));

                Assert.AreEqual(expected, window.Merged().Count(),
                    $"the window should have held {expected} after rolling");

                if (expected == 0)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// The wrapper works over anything that merges exactly, not just one structure.
        /// </summary>
        [TestMethod]
        public void TestItWindowsOtherStructuresToo()
        {
            var clock = new TestClock();
            var window = new SlidingWindow<CountMinSketch>(
                TimeSpan.FromMinutes(10), buckets: 10,
                () => new CountMinSketch(0.01, 0.01), (a, b) => { a.Merge(b); return a; },
                () => clock.Now);

            for (var i = 0; i < 50; i++)
            {
                window.Current.Add(Item(1));
            }

            Assert.AreEqual(50ul, window.Merged().Count(Item(1)));

            clock.Advance(TimeSpan.FromMinutes(11));
            Assert.AreEqual(0ul, window.Merged().Count(Item(1)));
        }

        /// <summary>
        /// <see cref="TopK"/> is refused by name, because its merge is approximate and a
        /// window built on it would drop elements as buckets rolled without anything
        /// about the result looking wrong.
        /// </summary>
        [TestMethod]
        public void TestAStructureThatDoesNotMergeExactlyIsRefused()
        {
            var ex = Assert.ThrowsExactly<ArgumentException>(() => new SlidingWindow<TopK>(
                TimeSpan.FromMinutes(10), 10, () => new TopK(0.001, 0.01, 10),
                (a, b) => a.Merge(b)));

            StringAssert.Contains(ex.Message, "approximate");
            StringAssert.Contains(ex.Message, "CountMinSketch");
        }

        [TestMethod]
        public void TestBadArgumentsAreRefused()
        {
            var clock = new TestClock();

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SlidingWindow<HyperLogLogPlus>(
                TimeSpan.Zero, 10, () => new HyperLogLogPlus(12), (a, b) => a.Merge(b)));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SlidingWindow<HyperLogLogPlus>(
                TimeSpan.FromMinutes(-1), 10, () => new HyperLogLogPlus(12), (a, b) => a.Merge(b)));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SlidingWindow<HyperLogLogPlus>(
                TimeSpan.FromMinutes(10), 0, () => new HyperLogLogPlus(12), (a, b) => a.Merge(b)));

            Assert.ThrowsExactly<ArgumentNullException>(() => new SlidingWindow<HyperLogLogPlus>(
                TimeSpan.FromMinutes(10), 10, null!, (a, b) => a.Merge(b)));
            Assert.ThrowsExactly<ArgumentNullException>(() => new SlidingWindow<HyperLogLogPlus>(
                TimeSpan.FromMinutes(10), 10, () => new HyperLogLogPlus(12), null!));

            // The precision is what the caller asked for, and is worth being able to read.
            var window = Estimator(clock, minutes: 60, buckets: 6);
            Assert.AreEqual(TimeSpan.FromMinutes(10), window.BucketWidth);
            Assert.AreEqual(6, window.Buckets);
        }

        /// <summary>
        /// A window that has never been written to holds nothing, rather than counting
        /// its empty buckets as part of the first window.
        /// </summary>
        [TestMethod]
        public void TestAFreshWindowHoldsNothing()
        {
            Assert.AreEqual(0ul, Estimator(new TestClock()).Merged().Count());
        }

    }
}
