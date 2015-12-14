using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;
using ProbabilisticDataStructures;
using System.Security.Cryptography;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestInverseBloomFilter
    {
        private static byte[] A_BYTES = Encoding.ASCII.GetBytes("a");
        private static byte[] B_BYTES = Encoding.ASCII.GetBytes("b");
        private static byte[] C_BYTES = Encoding.ASCII.GetBytes("c");
        private static byte[] D_BYTES = Encoding.ASCII.GetBytes("d");
        private static byte[] X_BYTES = Encoding.ASCII.GetBytes("x");

        /// <summary>
        /// Ensures that Capacity returns the correct filter size.
        /// </summary>
        [TestMethod]
        public void TestInverseCapacity()
        {
            var f = new InverseBloomFilter(100);

            var capacity = f.Capacity();
            Assert.AreEqual(100u, capacity);
        }

        /// <summary>
        /// Ensures that TestAndAdd behaves correctly.
        /// </summary>
        [TestMethod]
        public void TestInverseTestAndAdd()
        {
            var f = new InverseBloomFilter(3);

            if (f.TestAndAdd(A_BYTES))
            {
                Assert.Fail("'a' should not be a member");
            }

            if (!f.Test(A_BYTES))
            {
                Assert.Fail("'a' should be a member");
            }

            // 'd' hashes to the same index as 'a'
            if (f.TestAndAdd(D_BYTES))
            {
                Assert.Fail("'d' should not be a member");
            }

            // 'a' was swapped out.
            if (f.TestAndAdd(A_BYTES))
            {
                Assert.Fail("'a' should not be a member");
            }

            if (!f.Test(A_BYTES))
            {
                Assert.Fail("'a' should be a member");
            }

            // 'b' hashes to another index
            if (f.TestAndAdd(B_BYTES))
            {
                Assert.Fail("'b' should not be a member");
            }

            if (!f.Test(B_BYTES))
            {
                Assert.Fail("'b' should be a member");
            }

            // 'a' should still be a member.
            if (!f.Test(A_BYTES))
            {
                Assert.Fail("'a' should be a member");
            }

            if (f.Test(C_BYTES))
            {
                Assert.Fail("'c' should not be a member");
            }

            var addedC = f.Add(C_BYTES);
            Assert.AreSame(f, addedC, "Returned InverseBloomFilter should be the same instance");

            if (!f.Test(C_BYTES))
            {
                Assert.Fail("'c' should be a member");
            }
        }
    }

    [TestClass]
    public class BenchmarkInverseBloomFilter
    {
        private InverseBloomFilter f;
        private int n;
        private byte[][] data;

        [TestInitialize()]
        public void Testinitialize()
        {
            n = 100000;
            f = new InverseBloomFilter((uint)n);
            data = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                data[i] = Encoding.ASCII.GetBytes(i.ToString());
            }
        }

        [TestCleanup()]
        public void TestCleanup()
        {
            f = null;
            n = 0;
            data = null;
        }

        [TestMethod]
        public void BenchmarkInverseAdd()
        {
            for (int i = 0; i < n; i++)
            {
                f.Add(data[i]);
            }
        }

        [TestMethod]
        public void BenchmarkInverseTest()
        {
            for (int i = 0; i < n; i++)
            {
                f.Test(data[i]);
            }
        }

        [TestMethod]
        public void BenchmarkInverseTestAndAdd()
        {
            for (int i = 0; i < n; i++)
            {
                f.TestAndAdd(data[i]);
            }
        }
    }
}
