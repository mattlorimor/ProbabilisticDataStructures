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
    /// The bounds below record today's behavior, not a target. Every filter operation
    /// currently allocates because <see cref="HashAlgorithm.ComputeHash(byte[])"/>
    /// returns a freshly allocated digest on each call. They should drop to zero once
    /// the hash path writes into a caller-provided buffer; tighten them when it does.
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
        /// per-thread, so this stays correct under MSTest's parallel execution.
        /// </para>
        /// </summary>
        private static long BytesPerOperation(Action operation)
        {
            for (int i = 0; i < Warmup; i++)
            {
                operation();
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Measured; i++)
            {
                operation();
            }
            var after = GC.GetAllocatedBytesForCurrentThread();

            return (after - before) / Measured;
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
            using var md5 = MD5.Create();

            var bytes = BytesPerOperation(() => Utils.HashKernel(data, md5));

            Assert.IsLessThanOrEqualTo(80, bytes,
                $"Utils.HashKernel allocated {bytes} B per call, above the recorded 80 B.");
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
            using var md5 = MD5.Create();

            var smallBytes = BytesPerOperation(() => Utils.HashKernel(small, md5));
            var largeBytes = BytesPerOperation(() => Utils.HashKernel(large, md5));

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

            Assert.IsLessThanOrEqualTo(80, bytes,
                $"BloomFilter.Test allocated {bytes} B per call, above the recorded 80 B.");
        }

        [TestMethod]
        public void TestBloomFilterAddAllocation()
        {
            var data = Key("allocation-probe");
            var f = new BloomFilter(10000, 0.01);

            var bytes = BytesPerOperation(() => f.Add(data));

            Assert.IsLessThanOrEqualTo(80, bytes,
                $"BloomFilter.Add allocated {bytes} B per call, above the recorded 80 B.");
        }

        [TestMethod]
        public void TestBloomFilterTestAndAddAllocation()
        {
            var data = Key("allocation-probe");
            var f = new BloomFilter(10000, 0.01);

            var bytes = BytesPerOperation(() => f.TestAndAdd(data));

            Assert.IsLessThanOrEqualTo(80, bytes,
                $"BloomFilter.TestAndAdd allocated {bytes} B per call, above the recorded 80 B.");
        }

        /// <summary>
        /// The Cuckoo filter is bounded separately and higher. Its GetComponents
        /// computes the hash three times -- once directly and again inside each of two
        /// ComputeHashSum32 calls -- and builds the fingerprint with a LINQ
        /// Take(...).ToArray(), so it allocates four times what the others do. The
        /// bound records that rather than endorsing it.
        /// </summary>
        [TestMethod]
        public void TestCuckooFilterTestAllocation()
        {
            var data = Key("allocation-probe");
            var f = new CuckooBloomFilter(10000, 0.01);
            f.Add(data);

            var bytes = BytesPerOperation(() => f.Test(data));

            Assert.IsLessThanOrEqualTo(320, bytes,
                $"CuckooBloomFilter.Test allocated {bytes} B per call, above the recorded 320 B.");
        }
    }
}
