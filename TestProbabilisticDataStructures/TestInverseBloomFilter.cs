using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;
using ProbabilisticDataStructures;

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
        /// An inverse filter stores the items themselves, so its payload is as long as what
        /// it holds: nine bytes, then each occupied slot's index, length and bytes. A filter
        /// of 523,266 slots, each holding an item of four kibibytes, is the first past the
        /// most a payload can be. The slots are claimed by reflection on a small filter, all
        /// holding one item, which writes the bytes as many items of its length would.
        /// </summary>
        [TestMethod]
        public void TestAnInverseFilterTooLargeToWriteIsRefusedNamingItsWholeSize()
        {
            const int slots = 523_266;
            const int length = 4_096;
            const long each = 2 * sizeof(uint) + length;
            var payload = 9 + slots * each;

            Assert.AreEqual(2_147_483_673L, payload, "the row's payload is miscomputed");
            Assert.IsGreaterThan(PersistenceFormat.MaxPayloadLength, payload,
                "the claimed slots fit a payload, so there is nothing to refuse");
            Assert.IsLessThanOrEqualTo(PersistenceFormat.MaxPayloadLength, payload - each,
                "one slot fewer is past the ceiling too, so the row is not the first past it");

            var f = new InverseBloomFilter(10);
            f.Add(A_BYTES);
            var array = new byte[slots][];
            System.Array.Fill(array, new byte[length]);

            TestPayloadCount.AssertRefusedWhileClaiming(
                f, payload, (f, "capacity", (uint)slots), (f, "Array", array));
        }

        /// <summary>
        /// The slots are one array of references, which holds at most
        /// <c>Array.MaxLength</c> of them. A capacity past that is refused, naming the
        /// parameter, before the array is asked for: until now it failed in the
        /// allocation, with an <see cref="System.OutOfMemoryException"/> just past the
        /// limit and an <see cref="System.OverflowException"/> from 2^31.
        /// </summary>
        [TestMethod]
        public void TestACapacityNoArrayHoldsIsRefusedBeforeAnythingIsAllocated()
        {
            foreach (var capacity in new[]
                { (uint)System.Array.MaxLength + 1, 3_000_000_000u, uint.MaxValue })
            {
                var ex = Assert.ThrowsExactly<System.ArgumentOutOfRangeException>(
                    () => new InverseBloomFilter(capacity), $"a capacity of {capacity}");
                Assert.AreEqual("capacity", ex.ParamName);
            }
        }

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
