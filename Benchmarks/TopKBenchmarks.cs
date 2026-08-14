using System;
using System.Text;
using BenchmarkDotNet.Attributes;
using ProbabilisticDataStructures;

namespace Benchmarks
{
    /// <summary>
    /// Adds into a top-k across a range of k. Deciding whether an arriving element is
    /// already held used to be a scan of the whole heap, which is the only cost here
    /// that grows with k; this is what shows whether it still does.
    /// <para>
    /// The stream cycles through more distinct keys than the heap can hold, so every
    /// key keeps the same frequency as every other. That keeps isTop true and sends
    /// every add through to the lookup, which is the path in question. A stream with a
    /// heavy head instead is rejected at isTop most of the time and never reaches it.
    /// </para>
    /// </summary>
    [MemoryDiagnoser]
    public class TopKAddBenchmarks
    {
        private const int Batch = 200_000;
        private const int DistinctKeys = 20_000;

        private byte[][] _keys = null!;
        private TopK _topK = null!;
        private int _cursor;

        [Params(10, 100, 1_000, 5_000)]
        public uint K { get; set; }

        [GlobalSetup]
        public void GlobalSetup()
        {
            _keys = new byte[DistinctKeys][];
            for (int i = 0; i < DistinctKeys; i++)
            {
                // Long enough that comparing two of them costs something, as the keys
                // a caller actually has do.
                _keys[i] = Encoding.ASCII.GetBytes(
                    $"entity-identifier-{i:D6}-with-realistic-length");
            }
        }

        /// <summary>
        /// A fresh structure per iteration, filled past k so the heap is full and the
        /// lookup is doing the work it would in a running system. OperationsPerInvoke
        /// divides by the batch, so the reported figure is per add and the setup stays
        /// out of the measurement.
        /// </summary>
        [IterationSetup]
        public void IterationSetup()
        {
            _topK = new TopK(0.0001, 0.001, K);
            _cursor = 0;

            for (int i = 0; i < K * 2 && i < DistinctKeys; i++)
            {
                _topK.Add(_keys[i]);
            }
        }

        [Benchmark(OperationsPerInvoke = Batch)]
        public void Add()
        {
            for (int i = 0; i < Batch; i++)
            {
                _topK.Add(_keys[_cursor++ % DistinctKeys]);
            }
        }
    }
}
