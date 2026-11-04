using System;
using System.IO.Hashing;
using BenchmarkDotNet.Attributes;
using ProbabilisticDataStructures;

namespace Benchmarks
{
    /// <summary>
    /// Isolates the hash kernel, which every filter operation goes through.
    /// <para>
    /// HashAlgorithm.ComputeHash allocates a fresh digest array on every call, so
    /// these numbers are the per-operation allocation floor for the whole library:
    /// no filter can allocate less than this per Add or Test.
    /// </para>
    /// </summary>
    [MemoryDiagnoser]
    public class HashKernelBenchmarks
    {
        private Func<ReadOnlySpan<byte>, ulong> _hash = null!;
        private byte[] _data = null!;

        /// <summary>Input sizes spanning a short key and a realistic record.</summary>
        [Params(8, 64, 1024)]
        public int DataSize { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _hash = d => XxHash3.HashToUInt64(d);
            _data = new byte[DataSize];
            for (int i = 0; i < DataSize; i++)
            {
                _data[i] = (byte)(i * 31);
            }
        }

        /// <summary>
        /// The 32-bit kernel: the hash split into its halves, as <c>SublimeCountMinSketch</c>
        /// and placement below 2^27 positions do. The library's helper for it is internal,
        /// so the split is written out here; it is two casts.
        /// </summary>
        [Benchmark(Baseline = true)]
        public (uint Lower, uint Upper) HashKernel()
        {
            var sum = _hash(_data);
            return ((uint)sum, (uint)(sum >> 32));
        }

        [Benchmark]
        public HashKernel128ReturnValue HashKernel128() => Utils.HashKernel128(_data, _hash);
    }
}
