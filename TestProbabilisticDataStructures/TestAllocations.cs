using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Guards the per-operation allocation of the hot path.
    /// <para>
    /// These are deliberately assertions rather than benchmarks. Timings on shared CI
    /// runners carry enough noise that a threshold tight enough to catch a real
    /// regression also fires on unrelated ones, but allocation counts are a property
    /// of the code rather than the machine: they reproduce exactly, run in
    /// milliseconds, and need no benchmarking harness.
    /// </para>
    /// <para>
    /// The hash path writes into a stack buffer via
    /// <see cref="HashAlgorithm.TryComputeHash(ReadOnlySpan{byte}, Span{byte}, out int)"/>,
    /// so these operations allocate nothing. Reintroducing
    /// <see cref="HashAlgorithm.ComputeHash(byte[])"/> anywhere on the hot path fails
    /// these tests, which is the point.
    /// </para>
    /// <para>
    /// The Cuckoo filter was bounded rather than zeroed while its fingerprint was a
    /// byte array; since #88 it is a <c>ulong</c>, and its test is held to zero with the
    /// rest.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestAllocations
    {
        private const int Warmup = 1000;
        private const int Measured = 1000;

        /// <summary>
        /// Returns bytes allocated per invocation of <paramref name="operation"/>.
        /// <para>
        /// The warmup matters: without it the measurement captures JIT tiering work
        /// rather than steady-state behavior. GC.GetAllocatedBytesForCurrentThread is
        /// per-thread, so this stays correct under MSTest's parallel execution, and the
        /// operations are counted in a no-GC region, so that a background collection running
        /// beside them cannot add the kilobytes it does to the count (<see cref="NoGCRegion"/>).
        /// </para>
        /// </summary>
        private static long BytesPerOperation(Action operation)
        {
            for (int i = 0; i < Warmup; i++)
            {
                operation();
            }

            var allocated = NoGCRegion.Allocated(() =>
            {
                for (int i = 0; i < Measured; i++)
                {
                    operation();
                }
            });

            return allocated / Measured;
        }

        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        /// <summary>
        /// The hash kernel is the source of every filter's per-operation allocation,
        /// so it is bounded on its own.
        /// </summary>
        [TestMethod]
        public void TestHashKernelAllocation()
        {
            var data = Key("allocation-probe");
            var hash = Defaults.GetDefaultHashFunction();

            var bytes = BytesPerOperation(() => Utils.HashKernel(data, hash));

            Assert.AreEqual(0, bytes,
                $"Utils.HashKernel allocated {bytes} B per call; the hot path must not allocate.");
        }

        /// <summary>
        /// Allocation must not depend on input size. If it does, something is copying
        /// the input rather than hashing it in place.
        /// </summary>
        [TestMethod]
        public void TestHashKernelAllocationDoesNotGrowWithInput()
        {
            var small = new byte[8];
            var large = new byte[8192];
            var hash = Defaults.GetDefaultHashFunction();

            var smallBytes = BytesPerOperation(() => Utils.HashKernel(small, hash));
            var largeBytes = BytesPerOperation(() => Utils.HashKernel(large, hash));

            Assert.AreEqual(smallBytes, largeBytes,
                $"Allocation scaled with input size: {smallBytes} B for 8 bytes of input " +
                $"versus {largeBytes} B for 8192 bytes.");
        }

        [TestMethod]
        public void TestBloomFilterTestAllocation()
        {
            var data = Key("allocation-probe");
            var f = new BloomFilter(10000, 0.01);
            f.Add(data);

            var bytes = BytesPerOperation(() => f.Test(data));

            Assert.AreEqual(0, bytes,
                $"BloomFilter.Test allocated {bytes} B per call; the hot path must not allocate.");
        }

        [TestMethod]
        public void TestBloomFilterAddAllocation()
        {
            var data = Key("allocation-probe");
            var f = new BloomFilter(10000, 0.01);

            var bytes = BytesPerOperation(() => f.Add(data));

            Assert.AreEqual(0, bytes,
                $"BloomFilter.Add allocated {bytes} B per call; the hot path must not allocate.");
        }

        [TestMethod]
        public void TestBloomFilterTestAndAddAllocation()
        {
            var data = Key("allocation-probe");
            var f = new BloomFilter(10000, 0.01);

            var bytes = BytesPerOperation(() => f.TestAndAdd(data));

            Assert.AreEqual(0, bytes,
                $"BloomFilter.TestAndAdd allocated {bytes} B per call; the hot path must not allocate.");
        }

        [TestMethod]
        public void TestInverseBloomFilterTestAllocation()
        {
            var data = Key("allocation-probe");
            var f = new InverseBloomFilter(10000);
            f.Add(data);

            var bytes = BytesPerOperation(() => f.Test(data));

            Assert.AreEqual(0, bytes,
                $"InverseBloomFilter.Test allocated {bytes} B per call; the hot path must not allocate.");
        }

        [TestMethod]
        public void TestHyperLogLogAddAllocation()
        {
            var data = Key("allocation-probe");
            var hll = HyperLogLog.NewDefaultHyperLogLog(0.01);

            var bytes = BytesPerOperation(() => hll.Add(data));

            Assert.AreEqual(0, bytes,
                $"HyperLogLog.Add allocated {bytes} B per call; the hot path must not allocate.");
        }

        /// <summary>
        /// The Cuckoo filter allocates nothing either. This test used to allow it 32 B a
        /// call, for the array its fingerprint was copied into before it was compared with
        /// a bucket's. Since #88 its fingerprints are packed into 64-bit words at their
        /// exact width, and a fingerprint is a <c>ulong</c> taken from the hash's bits, so
        /// it never reaches the heap, and the bound is the other filters' bound.
        /// </summary>
        [TestMethod]
        public void TestCuckooFilterTestAllocation()
        {
            var data = Key("allocation-probe");
            var f = new CuckooBloomFilter(10000, 0.01);
            f.Add(data);

            var bytes = BytesPerOperation(() => f.Test(data));

            Assert.AreEqual(0, bytes,
                $"CuckooBloomFilter.Test allocated {bytes} B per call; the hot path must not allocate.");
        }
    }
}
