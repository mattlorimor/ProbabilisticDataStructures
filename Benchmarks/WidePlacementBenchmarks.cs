using System;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using ProbabilisticDataStructures;

namespace Benchmarks
{
    /// <summary>
    /// Which structure, and so which bit placement, a <see cref="WidePlacementBenchmarks"/>
    /// case runs.
    /// </summary>
    public enum PlacementCase
    {
        /// <summary><see cref="BloomFilter"/> just below 2^27 positions: 32-bit placement.</summary>
        Narrow,

        /// <summary><see cref="BloomFilter"/> at 2^27 positions: 64-bit placement.</summary>
        Wide,

        /// <summary>
        /// <see cref="BloomFilter64"/> at the same size and <c>k</c>, for reference. It
        /// places the way <see cref="Wide"/> does.
        /// </summary>
        Wide64,
    }

    /// <summary>
    /// What a filter pays for 64-bit placement: <c>Add</c> and <c>Test</c> on a filter just
    /// below 2^27 positions, which places with 32-bit values, against one at 2^27, which
    /// places with 64-bit values and hashes a second time to get the second one.
    /// <para>
    /// The two sizes differ by a few positions, so the filters share <c>k</c> and, to
    /// within a part in 10^7, <c>m</c>: what differs between the cases is how the
    /// positions are derived and nothing about how far apart they land. A filter is 16 MB,
    /// which is why this stops at the threshold rather than benchmarking well above it.
    /// </para>
    /// <para>
    /// Each case runs in its own process and builds only the structure it names, filled to
    /// the item count it was sized for so that a miss walks as many probes as it would in
    /// use. The items are a fixed batch of distinct random byte arrays, so the probes land
    /// all over the filter rather than on the same cache lines each time. Add re-adds the
    /// batch, which writes the same bits again and costs what a first add does: the
    /// filter's bits are stored unconditionally. OperationsPerInvoke reports the cost per
    /// operation.
    /// </para>
    /// <para>
    /// To compare the derivation itself with a fixed 64-bit mixer, see
    /// <see cref="SecondValueDerivationBenchmarks"/>, and for where the difference
    /// between the two placements goes, <see cref="PlacementKernelBenchmarks"/>.
    /// </para>
    /// </summary>
    [MemoryDiagnoser]
    public class WidePlacementBenchmarks
    {
        private const int Batch = 4096;
        private const double FpRate = 0.01;
        private const ulong WideFrom = 1UL << 27;

        private byte[][] _hits = null!;
        private byte[][] _misses = null!;
        private IFilter _filter = null!;

        [Params(PlacementCase.Narrow, PlacementCase.Wide, PlacementCase.Wide64)]
        public PlacementCase Case { get; set; }

        /// <summary>A short key, then a record long enough that the item's own hash dominates.</summary>
        [Params(16, 1024)]
        public int ItemSize { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            // The largest item count whose filter is still below 2^27 positions, and the
            // smallest whose filter is not.
            var narrowN = ItemsFor(WideFrom, atLeast: false);
            var wideN = ItemsFor(WideFrom, atLeast: true);

            uint n;
            switch (Case)
            {
                case PlacementCase.Narrow:
                    n = narrowN;
                    _filter = new BloomFilter(n, FpRate);
                    CheckSize(Utils.OptimalM(n, FpRate), below: true);
                    break;
                case PlacementCase.Wide:
                    n = wideN;
                    _filter = new BloomFilter(n, FpRate);
                    CheckSize(Utils.OptimalM(n, FpRate), below: false);
                    break;
                default:
                    n = wideN;
                    _filter = new BloomFilter64(n, FpRate);
                    CheckSize(Utils.OptimalM64(n, FpRate), below: false);
                    break;
            }

            // Fill to the count the filter was sized for, so a miss stops where it would
            // in a filter that is in use rather than at the first probe of an empty one.
            var counter = new byte[8];
            for (ulong i = 0; i < n; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(counter, i);
                _filter.Add(counter);
            }

            _hits = Items(seed: 1);
            _misses = Items(seed: 2);
            foreach (var item in _hits)
            {
                _filter.Add(item);
            }
        }

        [Benchmark(Baseline = true, OperationsPerInvoke = Batch)]
        public void Add()
        {
            var filter = _filter;
            var items = _hits;
            for (int i = 0; i < items.Length; i++)
            {
                filter.Add(items[i]);
            }
        }

        /// <summary>Every probe is set, so the test walks all of <c>k</c>.</summary>
        [Benchmark(OperationsPerInvoke = Batch)]
        public int Contains_Hit()
        {
            var filter = _filter;
            var items = _hits;
            var found = 0;
            for (int i = 0; i < items.Length; i++)
            {
                if (filter.Test(items[i]))
                {
                    found++;
                }
            }
            return found;
        }

        /// <summary>
        /// Items that were never added: the test stops at the first clear bit, about two
        /// probes into a half-full filter, so the hashing is most of what is measured.
        /// </summary>
        [Benchmark(OperationsPerInvoke = Batch)]
        public int Contains_Miss()
        {
            var filter = _filter;
            var items = _misses;
            var found = 0;
            for (int i = 0; i < items.Length; i++)
            {
                if (filter.Test(items[i]))
                {
                    found++;
                }
            }
            return found;
        }

        private byte[][] Items(ulong seed)
        {
            var items = new byte[Batch][];
            var state = seed * 0x9E3779B97F4A7C15UL;
            for (int i = 0; i < Batch; i++)
            {
                var item = new byte[ItemSize];
                for (int offset = 0; offset < item.Length; offset += 8)
                {
                    state += 0x9E3779B97F4A7C15UL;
                    var z = state;
                    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                    z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                    z ^= z >> 31;
                    var chunk = Math.Min(8, item.Length - offset);
                    for (int b = 0; b < chunk; b++)
                    {
                        item[offset + b] = (byte)(z >> (8 * b));
                    }
                }
                items[i] = item;
            }
            return items;
        }

        /// <summary>
        /// The item count at which the 1% filter first reaches (<paramref name="atLeast"/>)
        /// or last stays under <paramref name="positions"/> positions. Sizing is monotone
        /// in the count, so this is a bisection.
        /// </summary>
        private static uint ItemsFor(ulong positions, bool atLeast)
        {
            uint low = 1;
            uint high = 100_000_000;
            while (low < high)
            {
                var mid = low + (high - low) / 2;
                if (Utils.OptimalM(mid, FpRate) >= positions)
                {
                    high = mid;
                }
                else
                {
                    low = mid + 1;
                }
            }

            // low is the smallest count that reaches the size.
            return atLeast ? low : low - 1;
        }

        private static void CheckSize(ulong m, bool below)
        {
            // A benchmark of the wrong side of the threshold would measure the wrong
            // placement and say nothing, so a sizing change that moved it fails here.
            if ((m < WideFrom) != below)
            {
                throw new InvalidOperationException(
                    $"A filter of {m} positions is on the wrong side of {WideFrom}.");
            }
        }
    }

    /// <summary>
    /// Deriving the second 64-bit placement value, which is the whole of what 64-bit
    /// placement adds to a filter's hashing. Inside this project only: nothing here is in
    /// the library, and it exists to price an alternative before 7.0.0 fixes the
    /// derivation.
    /// <para>
    /// The library derives it as <c>Utils.HashKernel128</c> does: the hash delegate, called on the first value's eight little-endian bytes.
    /// That is timed here against two fixed 64-bit finalizers on the first value:
    /// SplitMix64's, and Murmur3's <c>fmix64</c>. The delegate is the XxHash3 one the
    /// library installs by default, reached the same way, through a delegate over a
    /// static method.
    /// </para>
    /// <para>
    /// Each is timed twice. <c>Throughput</c> derives from a batch of independent first
    /// values, which is what the processor can overlap across consecutive operations of a
    /// loop. <c>Chain</c> feeds each result into the next, which is what it cannot, and
    /// bounds the cost from above. A filter operation is between the two: its second
    /// value depends on its first, but the next operation does not depend on either.
    /// </para>
    /// <para>
    /// The <c>Positions</c> pair prices the other thing 64-bit placement changes: the
    /// <c>k</c> positions are then computed in 64-bit arithmetic and reduced by a 64-bit
    /// remainder rather than a 32-bit one. That cost does not depend on how the second
    /// value is derived, so no mixer saves it.
    /// </para>
    /// </summary>
    [MemoryDiagnoser]
    public class SecondValueDerivationBenchmarks
    {
        private const int Batch = 4096;

        private Func<ReadOnlySpan<byte>, ulong> _hash = null!;
        private ulong[] _firsts = null!;
        private ulong[] _seconds = null!;

        // Fields rather than constants, as a filter's are: a constant divisor is
        // compiled to a multiplication, and a filter's is not constant.
        private uint _k;
        private uint _m;

        [GlobalSetup]
        public void Setup()
        {
            // The same shape as Defaults.GetDefaultHashFunction: a delegate over a static
            // method that calls XxHash3 with seed 0.
            _hash = DefaultHash;
            _k = Utils.OptimalK(0.01);
            _m = (1U << 27) - 5;

            _firsts = new ulong[Batch];
            _seconds = new ulong[Batch];
            var state = 1UL;
            for (int i = 0; i < Batch; i++)
            {
                state += 0x9E3779B97F4A7C15UL;
                _firsts[i] = SplitMix64(state);
                _seconds[i] = Fmix64(_firsts[i]);
            }

            // What is timed has to be what the library does, or the comparison is of
            // something else. Check the delegate form against the library's own kernel.
            var item = new byte[16];
            for (int i = 0; i < item.Length; i++)
            {
                item[i] = (byte)(i * 31);
            }
            var kernel = Utils.HashKernel128(item, _hash);
            if (FromDelegate(kernel.LowerBaseHash) != kernel.UpperBaseHash)
            {
                throw new InvalidOperationException(
                    "The delegate derivation here is not the library's HashKernel128.");
            }
        }

        private static ulong DefaultHash(ReadOnlySpan<byte> data) => XxHash3.HashToUInt64(data);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong FromDelegate(ulong first)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, first);
            return _hash(bytes);
        }

        /// <summary>SplitMix64's output function, applied to <paramref name="x"/> after its increment.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong SplitMix64(ulong x)
        {
            var z = x;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>
        /// Murmur3's 64-bit finalizer. It maps zero to zero, which a derivation of a step
        /// would have to answer for.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Fmix64(ulong k)
        {
            k ^= k >> 33;
            k *= 0xFF51AFD7ED558CCDUL;
            k ^= k >> 33;
            k *= 0xC4CEB9FE1A85EC53UL;
            return k ^ (k >> 33);
        }

        // Applied to the first value plus SplitMix64's increment, as SplitMix64 itself
        // would, so that zero does not map to zero.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong SplitMixSecond(ulong first) => SplitMix64(first + 0x9E3779B97F4A7C15UL);

        [Benchmark(Baseline = true, OperationsPerInvoke = Batch)]
        public ulong Delegate_Throughput()
        {
            var firsts = _firsts;
            ulong sink = 0;
            for (int i = 0; i < firsts.Length; i++)
            {
                sink ^= FromDelegate(firsts[i]);
            }
            return sink;
        }

        [Benchmark(OperationsPerInvoke = Batch)]
        public ulong SplitMix64_Throughput()
        {
            var firsts = _firsts;
            ulong sink = 0;
            for (int i = 0; i < firsts.Length; i++)
            {
                sink ^= SplitMixSecond(firsts[i]);
            }
            return sink;
        }

        [Benchmark(OperationsPerInvoke = Batch)]
        public ulong Fmix64_Throughput()
        {
            var firsts = _firsts;
            ulong sink = 0;
            for (int i = 0; i < firsts.Length; i++)
            {
                sink ^= Fmix64(firsts[i]);
            }
            return sink;
        }

        [Benchmark(OperationsPerInvoke = Batch)]
        public ulong Delegate_Chain()
        {
            var x = _firsts[0];
            for (int i = 0; i < Batch; i++)
            {
                x = FromDelegate(x);
            }
            return x;
        }

        [Benchmark(OperationsPerInvoke = Batch)]
        public ulong SplitMix64_Chain()
        {
            var x = _firsts[0];
            for (int i = 0; i < Batch; i++)
            {
                x = SplitMixSecond(x);
            }
            return x;
        }

        [Benchmark(OperationsPerInvoke = Batch)]
        public ulong Fmix64_Chain()
        {
            var x = _firsts[0];
            for (int i = 0; i < Batch; i++)
            {
                x = Fmix64(x);
            }
            return x;
        }

        /// <summary>
        /// The <c>k</c> positions from two 32-bit values, as the narrow scheme computes
        /// them: 32-bit arithmetic and a 32-bit remainder.
        /// </summary>
        [Benchmark(OperationsPerInvoke = Batch)]
        public uint Positions_Narrow()
        {
            var firsts = _firsts;
            var k = _k;
            var m = _m;
            uint sink = 0;
            for (int j = 0; j < firsts.Length; j++)
            {
                var first = (uint)firsts[j];
                var second = (uint)(firsts[j] >> 32);
                for (uint i = 0; i < k; i++)
                {
                    sink ^= (first + second * i) % m;
                }
            }
            return sink;
        }

        /// <summary>
        /// The <c>k</c> positions from two 64-bit values, as the wide scheme computes
        /// them: 64-bit arithmetic and a 64-bit remainder. The second values are made in
        /// setup, because what is timed is the arithmetic.
        /// </summary>
        [Benchmark(OperationsPerInvoke = Batch)]
        public uint Positions_Wide()
        {
            var firsts = _firsts;
            var seconds = _seconds;
            var k = _k;
            var m = _m;
            uint sink = 0;
            for (int j = 0; j < firsts.Length; j++)
            {
                var first = firsts[j];
                var second = seconds[j];
                for (uint i = 0; i < k; i++)
                {
                    sink ^= (uint)((first + second * i) % m);
                }
            }
            return sink;
        }
    }
}
