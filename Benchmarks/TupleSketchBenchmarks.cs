using System;
using System.Text;
using BenchmarkDotNet.Attributes;
using ProbabilisticDataStructures;

namespace Benchmarks
{
    /// <summary>
    /// Adds into a tuple sketch across a range of nominal sizes, with fractional values
    /// under <see cref="SummaryPolicy.Sum"/>. A sketch compacts when its buffer fills,
    /// and a compaction sorts the keys with their summaries and folds the repeats, so
    /// this is what shows what that sort costs and whether it allocates.
    /// <para>
    /// Each key arrives in a burst, so every compaction has runs of equal keys to fold.
    /// The stream draws on a population of distinct keys that is a multiple of what the
    /// sketch retains, and that multiple is what the benchmark varies. Once the sketch
    /// is sampling, an add is kept only if its hash is below theta, which is about one
    /// in the multiple: a small one keeps the buffer filling and compacting, as a
    /// recurring population of keys does, and a large one is a stream of mostly new
    /// keys, turned away at the threshold, and measures little but the hash.
    /// </para>
    /// </summary>
    [MemoryDiagnoser]
    public class TupleSketchAddBenchmarks
    {
        private const int Batch = 10_000_000;
        private const int ValueCount = 1 << 16;
        private const int Burst = 4;

        private byte[][] _keys = null!;
        private double[] _values = null!;
        private TupleSketch _sketch = null!;

        [Params(16, 256, 4096)]
        public uint K { get; set; }

        /// <summary>How many distinct keys the stream draws on, per key the sketch retains.</summary>
        [Params(2, 16)]
        public int Population { get; set; }

        [GlobalSetup]
        public void GlobalSetup()
        {
            var distinct = checked((int)K * Population);
            _keys = new byte[distinct][];
            for (int i = 0; i < distinct; i++)
            {
                _keys[i] = Encoding.ASCII.GetBytes($"entity-identifier-{i:D7}");
            }

            // Irrational, as fractional amounts are, so adding them in a different
            // order gives different bits.
            _values = new double[ValueCount];
            for (int i = 0; i < ValueCount; i++)
            {
                _values[i] = Math.Sqrt(i + 1);
            }
        }

        /// <summary>
        /// A fresh sketch per iteration, so one invocation always starts from the same
        /// empty state and the buffers' growth is part of what is measured.
        /// OperationsPerInvoke divides by the batch, so the figures are per add.
        /// </summary>
        [IterationSetup]
        public void IterationSetup()
        {
            _sketch = new TupleSketch(K, SummaryPolicy.Sum);
        }

        [Benchmark(OperationsPerInvoke = Batch)]
        public void Add()
        {
            var distinct = _keys.Length;
            for (int i = 0; i < Batch; i++)
            {
                _sketch.Add(_keys[(i / Burst) % distinct], _values[i & (ValueCount - 1)]);
            }
        }
    }
}
