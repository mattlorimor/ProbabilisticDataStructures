/*
Original work Copyright 2013 Eric Lesh
Modified work Copyright 2015 Tyler Treat
Modified work Copyright 2015 Matthew Lorimor

Permission is hereby granted, free of charge, to any person obtaining
a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.
*/

using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;
using System.Text;
using System.Threading.Tasks;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestHyperLogLog
    {
        private double geterror(UInt64 actual, UInt64 estimate)
        {
            return ((float)estimate - (float)actual) / (float)actual;
        }

        private void testHyperLogLog(int n, int lowB, int highB)
        {
            var words = Words.Dictionary(n);
            var bad = 0;
            var nWords = (UInt64)words.LongLength;

            var options = new ParallelOptions();
            options.MaxDegreeOfParallelism = 4;
            Parallel.For(lowB, highB, options, i =>
            {
                var m = (uint)Math.Pow(2, i);

                HyperLogLog h = null;
                try
                {
                    h = new HyperLogLog(m);
                }
                catch (Exception)
                {
                    Assert.Fail(string.Format("Can't make HyperLogLog({0})", m));
                }

                foreach (var word in words)
                {
                    h.Add(Encoding.ASCII.GetBytes(word));
                }

                var expectedError = 1.04 / Math.Sqrt(m);
                var actualError = Math.Abs(this.geterror(nWords, h.Count()));

                if (actualError > expectedError)
                {
                    bad++;
                    //Assert.Fail(string.Format("Expected: {0}, Actual: {1}", expectedError, actualError));
                }
            });
        }

        private void benchmarkCount(int registers)
        {
            var n = 100000;
            var words = Words.Dictionary(0);
            var m = (uint)Math.Pow(2, registers);

            var h = new HyperLogLog(m);

            foreach (var word in words)
            {
                h.Add(Encoding.ASCII.GetBytes(word));
            }

            for (int i = 0; i < n; i++)
            {
                h.Count();
            }
        }

        [TestMethod]
        public void TestHyperLogLogSmall()
        {
            this.testHyperLogLog(5, 4, 17);
        }

        [TestMethod]
        public void TestHyperLogLogBig()
        {
            this.testHyperLogLog(0, 4, 17);
        }

        /// <summary>
        /// The registers are one array of bytes, and the largest power of two an array
        /// holds is 2^30, so the next is refused, naming the parameter, before the array
        /// is asked for. It failed in the allocation, with an
        /// <see cref="OverflowException"/>. A count that is not a power of two is still
        /// refused first, and as before.
        /// </summary>
        [TestMethod]
        public void TestMoreRegistersThanAnArrayHoldsAreRefused()
        {
            var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new HyperLogLog(1u << 31));
            Assert.AreEqual("m", ex.ParamName);

            Assert.ThrowsExactly<ArgumentException>(() => new HyperLogLog(uint.MaxValue));
            Assert.ThrowsExactly<ArgumentException>(() => new HyperLogLog((1u << 30) + 1));
        }

        /// <summary>
        /// The limit is the last power of two an array holds, which does build: a
        /// gibibyte of registers, allocated once and not touched, so the cost is the
        /// allocation. The error that rounds up to it builds the same estimator.
        /// </summary>
        [TestMethod]
        public void TestTheLargestEstimatorBuilds()
        {
            var largest = new HyperLogLog(1u << 30);
            Assert.AreEqual(1u << 30, largest.M);
            largest.Add(new byte[] { 1, 2, 3 });
            largest = null;

            // 1.04 / 3.2e-5, squared, is about 1.056e9 registers: 2^30 once rounded up.
            var byError = HyperLogLog.NewDefaultHyperLogLog(3.2e-5);
            Assert.AreEqual(1u << 30, byError.M);
        }

        /// <summary>
        /// An error so small that its register count passes what a register array holds is
        /// refused naming the error. The count was rounded up to a power of two and cast
        /// to a uint, which saturates, so the failure was a complaint that 4294967295 is
        /// not a power of two.
        /// </summary>
        [TestMethod]
        public void TestAnErrorTooSmallForAnyRegisterArrayIsRefused()
        {
            // 1.04 / 1e-5 squared is about 1.1e10 registers, 2^34 once rounded.
            var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => HyperLogLog.NewDefaultHyperLogLog(1e-5));
            Assert.AreEqual("e", ex.ParamName);
        }

        [TestMethod]
        public void TestNewDefaultHyperLogLog()
        {
            var hll = HyperLogLog.NewDefaultHyperLogLog(0.1);

            Assert.AreEqual(128u, hll.M);
        }

        [TestMethod]
        public void BenchmarkHLLCount4()
        {
            this.benchmarkCount(4);
        }

        [TestMethod]
        public void BenchmarkHLLCount5()
        {
            this.benchmarkCount(5);
        }

        [TestMethod]
        public void BenchmarkHLLCount6()
        {
            this.benchmarkCount(6);
        }

        [TestMethod]
        public void BenchmarkHLLCount7()
        {
            this.benchmarkCount(7);
        }

        [TestMethod]
        public void BenchmarkHLLCount8()
        {
            this.benchmarkCount(8);
        }

        [TestMethod]
        public void BenchmarkHLLCount9()
        {
            this.benchmarkCount(9);
        }

        [TestMethod]
        public void BenchmarkHLLCount10()
        {
            this.benchmarkCount(10);
        }
    }
}
