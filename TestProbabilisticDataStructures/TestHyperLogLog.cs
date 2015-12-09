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
            for (int i = lowB; i < highB; i++)
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
            }
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

        [TestMethod]
        public void TestNewDefaultHyperLogLog()
        {
            var hll = HyperLogLog.NewDefaultHyperLogLog(0.1);

            Assert.AreEqual(128u, hll.m);
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
