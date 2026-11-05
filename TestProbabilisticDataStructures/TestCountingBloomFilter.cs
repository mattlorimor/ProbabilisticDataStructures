using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;
using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestCountingBloomFilter
    {
        /// <summary>
        /// States whose payload <see cref="TestPayloadCount"/> holds the count to. The length
        /// is the bucket table's, from its count and width, so two of each, and counters
        /// raised, which write the same length.
        /// </summary>
        internal static System.Collections.Generic.IEnumerable<PayloadState> PayloadStates()
        {
            yield return PayloadState.Of(
                "10 at 50%, 4 bits, empty", new CountingBloomFilter(10, 4, 0.5),
                PersistenceFormat.Bloom64Version);

            var filled = new CountingBloomFilter(200, 8, 0.01);
            for (var i = 0; i < 40; i++) filled.Add(Encoding.ASCII.GetBytes($"w{i % 10}"));
            yield return PayloadState.Of(
                "200 at 1%, 8 bits, counted", filled, PersistenceFormat.Bloom64Version);

            var odd = new CountingBloomFilter(37, 3, 0.1);
            odd.Add(Encoding.ASCII.GetBytes("a"));
            yield return PayloadState.Of(
                "37 at 10%, 3 bits, a table that ends inside a byte", odd,
                PersistenceFormat.Bloom64Version);
        }

        private static byte[] A_BYTES = Encoding.ASCII.GetBytes("a");
        private static byte[] B_BYTES = Encoding.ASCII.GetBytes("b");
        private static byte[] C_BYTES = Encoding.ASCII.GetBytes("c");
        private static byte[] X_BYTES = Encoding.ASCII.GetBytes("x");

        /// <summary>
        /// A read allocates a scratch buffer of a position for each hash function, eight bytes
        /// each since positions are 64-bit (#232), and is charged for it to the byte: the
        /// version 14 fixture with its hash count edited from 7 to the most a filter has
        /// allocates more by exactly what it is charged more.
        /// </summary>
        [TestMethod]
        public void TestTheScratchBufferIsChargedWhatItAllocates()
        {
            // Version 14: u64 m, then the u32 hash count at 8.
            const int HashCount = 8;
            var seven = TestPersistenceFormatStability.ReadFixture("countingbloomfilter-v14-narrow.bin");
            Assert.AreEqual(7u, BinaryPrimitives.ReadUInt32LittleEndian(seven.AsSpan(14 + HashCount)), "premise: the hash count");
            var most = TestPersistenceHostilePayloads.PokeUInt32(seven, HashCount, Utils.MaxHashCount);

            static (long Charged, long Allocated) Read(byte[] bytes)
            {
                var budget = new ReadBudget(long.MaxValue, StructureId.CountingBloomFilter);
                Persistence.FromByteArray<CountingBloomFilter>(bytes, new ReadOptions(budget));
                return (budget.Spent, TestReadBudget.Allocated(() => Persistence.FromByteArray<CountingBloomFilter>(bytes)));
            }

            var (fewer, more) = (Read(seven), Read(most));
            Assert.IsGreaterThanOrEqualTo(8L * (Utils.MaxHashCount - 7), more.Allocated - fewer.Allocated,
                "premise: the buffer is eight bytes a position");
            Assert.AreEqual(more.Allocated - fewer.Allocated, more.Charged - fewer.Charged,
                "the read was charged other than the scratch buffer it allocated");
        }

        /// <summary>
        /// A counting filter's payload is its twenty-one bytes of fields and the thirteen of
        /// its table's header, then each of the table's arrays in a run of its own (#232). A
        /// table that takes the payload past the most one can be is gigabytes, so its arrays
        /// are claimed by reflection on a small filter: 2,048 of one array of a mebibyte,
        /// which is 2,147,491,874 bytes of payload, past the most by 8,301. The run's lengths
        /// are counted from the arrays, as the writer writes them, so the count covers each.
        /// </summary>
        [TestMethod]
        public void TestACountingFilterTooLargeToWriteIsRefusedNamingItsWholeSize()
        {
            var piece = new byte[1 << 20];
            var claimed = Enumerable.Repeat(piece, 2048).ToArray();
            var payload = 8 + 4 + 1 + 8 + 8 + 1 + 4 + claimed.Sum(array => 4L + array.Length);
            Assert.AreEqual(2_147_491_874L, payload, "the payload is miscomputed");
            Assert.IsGreaterThan(PersistenceFormat.MaxPayloadLength, payload, "the claim fits a payload");

            var f = new CountingBloomFilter(10, 8, 0.5);
            f.Add(A_BYTES);

            TestPayloadCount.AssertRefusedWhileClaiming(f, payload, ((object)f.Buckets, "Data", (object)claimed));
        }

        /// <summary>
        /// Ensures that Capacity() returns the number of bits, m, in the Bloom filter.
        /// </summary>
        [TestMethod]
        public void TestCountingCapacity()
        {
            var f = CountingBloomFilter.NewDefaultCountingBloomFilter(100, 0.1);
            var capacity = f.Capacity();

            Assert.AreEqual(480u, capacity);
        }

        /// <summary>
        /// Ensures that K() returns the number of hash functions in the Bloom Filter.
        /// </summary>
        [TestMethod]
        public void TestCountingK()
        {
            var f = CountingBloomFilter.NewDefaultCountingBloomFilter(100, 0.1);
            var k = f.K();

            Assert.AreEqual(4u, k);
        }

        /// <summary>
        /// Ensures that Count returns the number of items added to the filter.
        /// </summary>
        [TestMethod]
        public void TestCountingCount()
        {
            var f = CountingBloomFilter.NewDefaultCountingBloomFilter(100, 0.1);
            for (uint i = 0; i < 10; i++)
            {
                f.Add(Encoding.ASCII.GetBytes(i.ToString()));
            }

            for (int i = 0; i < 5; i++)
            {
                f.TestAndRemove(Encoding.ASCII.GetBytes(i.ToString()));
            }

            var count = f.Count();
            Assert.AreEqual(5u, count);
        }

        /// <summary>
        /// Ensures that Test, Add, and TestAndAdd behave correctly.
        /// </summary>
        [TestMethod]
        public void TestCountingTestAndAdd()
        {
            var f = CountingBloomFilter.NewDefaultCountingBloomFilter(100, 0.01);

            // 'a' is not in the filter.
            if (f.Test(A_BYTES))
            {
                Assert.Fail("'a' should not be a member");
            }

            var addedF = f.Add(A_BYTES);
            Assert.AreSame(f, addedF, "Returned BloomFilter should be the same instance");

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

            for (int i = 0; i < 1000000; i++)
            {
                f.TestAndAdd(Encoding.ASCII.GetBytes(i.ToString()));
            }

            // 'x' should be a false positive.
            if (!f.Test(X_BYTES))
            {
                Assert.Fail("'x' should be a member");
            }
        }

        /// <summary>
        /// Ensures that TestAndRemove behaves correctly.
        /// </summary>
        [TestMethod]
        public void TestCountingTestAndRemove()
        {
            var f = CountingBloomFilter.NewDefaultCountingBloomFilter(100, 0.01);

            // 'a' is not in the filter.
            if (f.TestAndRemove(A_BYTES))
            {
                Assert.Fail("'a' should not be a member");
            }

            f.Add(Encoding.ASCII.GetBytes("a"));

            // 'a' is now in the filter.
            if (!f.TestAndRemove(A_BYTES))
            {
                Assert.Fail("'a' should be a member");
            }

            // 'a' is no longer in the filter.
            if (f.TestAndRemove(A_BYTES))
            {
                Assert.Fail("'a' should not be a member");
            }
        }

        /// <summary>
        /// Ensures that Reset sets every bit to zero and the count is zero.
        /// </summary>
        [TestMethod]
        public void TestCountingReset()
        {
            var f = CountingBloomFilter.NewDefaultCountingBloomFilter(100, 0.1);
            for (int i = 0; i < 1000; i++)
            {
                f.Add(Encoding.ASCII.GetBytes(i.ToString()));
            }

            var resetF = f.Reset();
            Assert.AreSame(f, resetF, "Returned CountingBloomFilter should be the same instance");

            for (uint i = 0; i < f.Buckets.count; i++)
            {
                if (f.Buckets.Get(i) != 0)
                {
                    Assert.Fail("Expected all bits to be unset");
                }
            }

            Assert.AreEqual(0u, f.Count());
        }

        [TestMethod]
        public void BenchmarkCountingAdd()
        {
            var n = 100000;
            var f = CountingBloomFilter.NewDefaultCountingBloomFilter(100000, 0.1);
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
        public void BenchmarkCountingTest()
        {
            var n = 100000;
            var f = CountingBloomFilter.NewDefaultCountingBloomFilter(100000, 0.1);
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
        public void BenchmarkCountingTestAndAdd()
        {
            var n = 100000;
            var f = CountingBloomFilter.NewDefaultCountingBloomFilter(100000, 0.1);
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

        [TestMethod]
        public void BenchmarkCountingTestAndRemove()
        {
            var n = 100000;
            var f = CountingBloomFilter.NewDefaultCountingBloomFilter(100000, 0.1);
            var data = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                data[i] = Encoding.ASCII.GetBytes(i.ToString());
            }

            for (int i = 0; i < n; i++)
            {
                f.TestAndRemove(data[i]);
            }
        }
    }
}
