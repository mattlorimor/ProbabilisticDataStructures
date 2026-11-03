using System.Text;
using BenchmarkDotNet.Attributes;
using ProbabilisticDataStructures;

namespace Benchmarks
{
    /// <summary>
    /// What one <c>Add</c> to a <see cref="CountMinSketch"/> costs, and what one
    /// <c>Merge</c> costs, at two widths: one whose five rows are about 108 kB, which sits
    /// in L2, and one of about 11 MB, which does not, so a counter is a cache miss of the
    /// slower kind.
    /// <para>
    /// This is the suite that prices what a counter update does beyond adding, such as
    /// the compare a counter pays to stop at <c>ulong.MaxValue</c> rather than wrap to
    /// nought. An <c>Add</c> adds to a sketch that has not been rebuilt, since a counter's
    /// cost does not depend on its value and a batch of a hundred thousand adds is
    /// nowhere near 2^64. The keys are distinct, so every add lands in a column of its
    /// own in each row. <c>OperationsPerInvoke</c> reports the cost of one add.
    /// </para>
    /// <para>
    /// <c>Merge</c> adds a second sketch of the same dimensions into this one, so its
    /// cost is that of every counter in the sketch, and is reported for the whole merge.
    /// </para>
    /// </summary>
    [MemoryDiagnoser]
    public class CountMinSketchBenchmarks
    {
        private const int Batch = 100_000;

        private byte[][] _keys = null!;
        private CountMinSketch _sketch = null!;
        private CountMinSketch _other = null!;

        /// <summary>A narrow sketch, then one wide enough to leave L2.</summary>
        [Params(0.001, 0.00001)]
        public double Epsilon { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _keys = new byte[Batch][];
            for (var i = 0; i < Batch; i++)
            {
                _keys[i] = Encoding.ASCII.GetBytes(i.ToString());
            }

            _sketch = new CountMinSketch(Epsilon, 0.01);
            _other = new CountMinSketch(Epsilon, 0.01);
            for (var i = 0; i < 1000; i++)
            {
                _other.Add(_keys[i]);
            }
        }

        [Benchmark(OperationsPerInvoke = Batch)]
        public void Add()
        {
            for (var i = 0; i < Batch; i++)
            {
                _sketch.Add(_keys[i]);
            }
        }

        [Benchmark]
        public bool Merge() => _sketch.Merge(_other);
    }
}
