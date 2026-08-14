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

        [Benchmark(Baseline = true)]
        public HashKernelReturnValue HashKernel() => Utils.HashKernel(_data, _hash);

        [Benchmark]
        public HashKernel128ReturnValue HashKernel128() => Utils.HashKernel128(_data, _hash);
    }
}
