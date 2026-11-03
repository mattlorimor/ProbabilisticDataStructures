using System;
using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using ProbabilisticDataStructures;

namespace Benchmarks
{
    /// <summary>
    /// What one operation on a <see cref="CuckooBloomFilter"/> costs: <c>Add</c>, and
    /// <c>Test</c> of an item that is a member and of one that is not, for a short key and
    /// for a record long enough that the item's own hash dominates.
    /// <para>
    /// Every operation derives its fingerprint and both buckets before it touches the
    /// table, which is three calls of the hash and two private methods that fill an eight
    /// byte buffer for it. This is the suite that prices those methods: what changes
    /// between two builds of the library shows here as it would in use, and not in a
    /// microbenchmark of one method.
    /// </para>
    /// <para>
    /// The filter is sized for two million items at 1%, which is 2^20 buckets of four ten
    /// bit fingerprints and about 5.7 MB with its occupancy bits: past the 1 MB of L2 a
    /// core has here and well inside L3, so a probe is a cache hit of the slower kind and
    /// not a trip to memory. It is filled with two million distinct keys first, so it is
    /// about half full, which is where it spends its life: most inserts find a free entry
    /// in the first bucket they try.
    /// </para>
    /// <para>
    /// An item is made at the moment it is used, from its index, by <see cref="Write"/>:
    /// the first eight bytes are a function of the index and the rest are the same for
    /// every item. That keeps a batch of half a million operations from needing half a
    /// million arrays, and what the cache sees is the table alone. A batch is large enough
    /// that the buckets it touches do not fit in L2.
    /// </para>
    /// <para>
    /// <c>Add</c> inserts a batch of keys the filter has not seen into a filter built for
    /// that iteration, since a cuckoo filter keeps a copy for every add and one that is
    /// added to again and again fills up and starts refusing. The build is in
    /// <c>IterationSetup</c>, so it is outside the timing, and the filter is seeded, so its
    /// relocations are the same on every run. <c>OperationsPerInvoke</c> reports the cost
    /// per operation.
    /// </para>
    /// </summary>
    [MemoryDiagnoser]
    public class CuckooOperationBenchmarks
    {
        internal const int Batch = 1 << 19;
        internal const uint Capacity = 2_000_000;
        internal const double FpRate = 0.01;

        private const ulong Members = 1;
        private const ulong Strangers = 2;
        private const ulong Newcomers = 3;

        private byte[] _item = null!;
        private CuckooBloomFilter _filter = null!;
        private CuckooBloomFilter _fresh = null!;

        /// <summary>A short key, then a record long enough that the item's own hash dominates.</summary>
        [Params(16, 1024)]
        public int ItemSize { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _item = Item(ItemSize);

            // The filter that is tested.
            _filter = Filled(_item);
            CheckLoad(_filter, 0.4, 0.6);
        }

        /// <summary>
        /// The filter that is added to: filled and nothing more, so the batch goes into a
        /// filter that has not seen it.
        /// </summary>
        [IterationSetup(Target = nameof(Add))]
        public void AddSetup()
        {
            _fresh = Filled(_item);
        }

        [Benchmark(Baseline = true, OperationsPerInvoke = Batch)]
        public int Add()
        {
            var filter = _fresh;
            var item = _item;
            var added = 0;
            for (ulong i = 0; i < Batch; i++)
            {
                Write(item, Newcomers, i);
                if (filter.Add(item))
                {
                    added++;
                }
            }
            return added;
        }

        /// <summary>Every item is a member, so the test ends in the first bucket or the second.</summary>
        [Benchmark(OperationsPerInvoke = Batch)]
        public int Test_Hit()
        {
            var filter = _filter;
            var item = _item;
            var found = 0;
            for (ulong i = 0; i < Batch; i++)
            {
                Write(item, Members, i);
                if (filter.Test(item))
                {
                    found++;
                }
            }
            return found;
        }

        /// <summary>
        /// Items that were never added: both buckets are searched and, bar the filter's
        /// false positives, neither holds the fingerprint.
        /// </summary>
        [Benchmark(OperationsPerInvoke = Batch)]
        public int Test_Miss()
        {
            var filter = _filter;
            var item = _item;
            var found = 0;
            for (ulong i = 0; i < Batch; i++)
            {
                Write(item, Strangers, i);
                if (filter.Test(item))
                {
                    found++;
                }
            }
            return found;
        }

        /// <summary>
        /// A filter sized for <see cref="Capacity"/> items with that many keys in it. The
        /// keys are the members: <see cref="Test_Hit"/> asks for the first
        /// <see cref="Batch"/> of them.
        /// </summary>
        private static CuckooBloomFilter Filled(byte[] item)
        {
            var filter = new CuckooBloomFilter(Capacity, FpRate, seed: 1);
            for (ulong i = 0; i < Capacity; i++)
            {
                Write(item, Members, i);
                if (!filter.Add(item))
                {
                    throw new InvalidOperationException("The filter refused an item it was sized for.");
                }
            }
            return filter;
        }

        /// <summary>
        /// A benchmark of a filter that is not as full as it claims would price a different
        /// path, so a sizing change that moved it fails here.
        /// </summary>
        internal static void CheckLoad(CuckooBloomFilter filter, double from, double to)
        {
            var load = (double)filter.Count() / ((double)filter.BucketCount() * 4);
            if (load < from || load > to)
            {
                throw new InvalidOperationException(
                    $"The filter is {load:P0} full, not between {from:P0} and {to:P0}.");
            }
        }

        /// <summary>
        /// An item's buffer: the bytes past the first eight are random and fixed.
        /// </summary>
        internal static byte[] Item(int size)
        {
            var item = new byte[size];
            new Random(12345).NextBytes(item);
            return item;
        }

        /// <summary>
        /// Makes item <paramref name="index"/> of the stream <paramref name="stream"/>: its
        /// first eight bytes are a bijection of the index, so no two items of a stream
        /// repeat and no two streams meet within the sizes used here.
        /// </summary>
        internal static void Write(byte[] item, ulong stream, ulong index)
        {
            var z = stream * 0x9E3779B97F4A7C15UL + index * 0xD1B54A32D192ED03UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            BinaryPrimitives.WriteUInt64LittleEndian(item, z);
        }
    }

    /// <summary>
    /// <c>Add</c> to a <see cref="CuckooBloomFilter"/> that is nearly full, where most
    /// inserts find both buckets taken and relocate other fingerprints, and each step of
    /// that hashes the fingerprint it displaced. The same filter as
    /// <see cref="CuckooOperationBenchmarks"/>, filled to nine tenths of its entries
    /// instead of half; short items only, since the relocation loop never sees the item.
    /// </summary>
    [MemoryDiagnoser]
    public class CuckooEvictionBenchmarks
    {
        private const int Batch = 1 << 16;
        private const double Load = 0.9;

        private byte[] _item = null!;
        private CuckooBloomFilter _filter = null!;

        [GlobalSetup]
        public void Setup()
        {
            _item = CuckooOperationBenchmarks.Item(16);
        }

        [IterationSetup]
        public void IterationSetup()
        {
            _filter = new CuckooBloomFilter(
                CuckooOperationBenchmarks.Capacity, CuckooOperationBenchmarks.FpRate, seed: 1);
            var fill = (ulong)(_filter.BucketCount() * 4 * Load);
            for (ulong i = 0; i < fill; i++)
            {
                CuckooOperationBenchmarks.Write(_item, 1, i);
                if (!_filter.Add(_item))
                {
                    throw new InvalidOperationException("The filter refused an item below its target load.");
                }
            }
            CuckooOperationBenchmarks.CheckLoad(_filter, Load - 0.01, Load + 0.01);
        }

        [Benchmark(OperationsPerInvoke = Batch)]
        public int Add_NearFull()
        {
            var filter = _filter;
            var item = _item;
            var added = 0;
            for (ulong i = 0; i < Batch; i++)
            {
                CuckooOperationBenchmarks.Write(item, 3, i);
                if (filter.Add(item))
                {
                    added++;
                }
            }
            return added;
        }
    }
}
