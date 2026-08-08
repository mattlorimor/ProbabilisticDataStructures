using System.Text;
using BenchmarkDotNet.Attributes;
using ProbabilisticDataStructures;

namespace Benchmarks
{
    /// <summary>
    /// Membership tests across the filter types. Test is read-only, so each
    /// invocation is independent and the filter's state does not drift over the
    /// course of a run.
    /// </summary>
    [MemoryDiagnoser]
    public class FilterTestBenchmarks
    {
        private const uint N = 100_000;
        private const double FpRate = 0.01;

        private byte[] _present = null!;
        private byte[] _absent = null!;

        private BloomFilter _bloom = null!;
        private CountingBloomFilter _counting = null!;
        private PartitionedBloomFilter _partitioned = null!;
        private CuckooBloomFilter _cuckoo = null!;
        private ScalableBloomFilter _scalable = null!;
        private StableBloomFilter _stable = null!;
        private DeletableBloomFilter _deletable = null!;

        [GlobalSetup]
        public void Setup()
        {
            _present = Encoding.ASCII.GetBytes("benchmark-key-present");
            _absent = Encoding.ASCII.GetBytes("benchmark-key-absent");

            _bloom = new BloomFilter(N, FpRate);
            _counting = CountingBloomFilter.NewDefaultCountingBloomFilter(N, FpRate);
            _partitioned = new PartitionedBloomFilter(N, FpRate);
            _cuckoo = new CuckooBloomFilter(N, FpRate);
            _scalable = ScalableBloomFilter.NewDefaultScalableBloomFilter(FpRate);
            _stable = StableBloomFilter.NewDefaultStableBloomFilter(N, FpRate);
            _deletable = new DeletableBloomFilter(N, 100, FpRate);

            // Populate so Test walks the full k-probe path rather than short-circuiting
            // on the first unset bit.
            _bloom.Add(_present);
            _counting.Add(_present);
            _partitioned.Add(_present);
            _cuckoo.Add(_present);
            _scalable.Add(_present);
            _stable.Add(_present);
            _deletable.Add(_present);
        }

        [Benchmark(Baseline = true)]
        public bool Bloom_Hit() => _bloom.Test(_present);

        [Benchmark]
        public bool Bloom_Miss() => _bloom.Test(_absent);

        [Benchmark]
        public bool Counting_Hit() => _counting.Test(_present);

        [Benchmark]
        public bool Partitioned_Hit() => _partitioned.Test(_present);

        [Benchmark]
        public bool Cuckoo_Hit() => _cuckoo.Test(_present);

        [Benchmark]
        public bool Scalable_Hit() => _scalable.Test(_present);

        [Benchmark]
        public bool Stable_Hit() => _stable.Test(_present);

        [Benchmark]
        public bool Deletable_Hit() => _deletable.Test(_present);
    }

    /// <summary>
    /// Insertion throughput. Add mutates the filter, so each invocation works on a
    /// filter built in IterationSetup and inserts a fixed batch. OperationsPerInvoke
    /// reports the per-item cost and amortizes the setup across the batch, which
    /// keeps IterationSetup overhead out of the measurement.
    /// </summary>
    [MemoryDiagnoser]
    public class FilterAddBenchmarks
    {
        private const int Batch = 10_000;
        private const uint N = 100_000;
        private const double FpRate = 0.01;

        private byte[][] _items = null!;
        private BloomFilter _bloom = null!;
        private PartitionedBloomFilter _partitioned = null!;
        private CuckooBloomFilter _cuckoo = null!;

        [GlobalSetup]
        public void Setup()
        {
            _items = new byte[Batch][];
            for (int i = 0; i < Batch; i++)
            {
                _items[i] = Encoding.ASCII.GetBytes(i.ToString());
            }
        }

        [IterationSetup]
        public void IterationSetup()
        {
            _bloom = new BloomFilter(N, FpRate);
            _partitioned = new PartitionedBloomFilter(N, FpRate);
            _cuckoo = new CuckooBloomFilter(N, FpRate);
        }

        [Benchmark(Baseline = true, OperationsPerInvoke = Batch)]
        public void Bloom_Add()
        {
            for (int i = 0; i < Batch; i++)
            {
                _bloom.Add(_items[i]);
            }
        }

        [Benchmark(OperationsPerInvoke = Batch)]
        public void Bloom_TestAndAdd()
        {
            for (int i = 0; i < Batch; i++)
            {
                _bloom.TestAndAdd(_items[i]);
            }
        }

        [Benchmark(OperationsPerInvoke = Batch)]
        public void Partitioned_Add()
        {
            for (int i = 0; i < Batch; i++)
            {
                _partitioned.Add(_items[i]);
            }
        }

        [Benchmark(OperationsPerInvoke = Batch)]
        public void Cuckoo_Add()
        {
            for (int i = 0; i < Batch; i++)
            {
                _cuckoo.Add(_items[i]);
            }
        }
    }
}
