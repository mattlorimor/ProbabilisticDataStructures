using System;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using ProbabilisticDataStructures;

namespace Benchmarks
{
    /// <summary>
    /// How the placement kernel derives a filter's base values.
    /// </summary>
    public enum KernelVariant
    {
        /// <summary>
        /// <c>Utils.HashKernel</c> and 32-bit position arithmetic: what a filter below 2^27
        /// positions does.
        /// </summary>
        Narrow,

        /// <summary>
        /// <c>Utils.HashKernel128</c> and 64-bit position arithmetic: what a filter of 2^27
        /// positions or more does, and <see cref="BloomFilter64"/> always has.
        /// </summary>
        Wide,

        /// <summary>
        /// The same values as <see cref="Wide"/>, so the same bits, from a different
        /// implementation: the second hash call is made on an eight-byte buffer held in an
        /// inline array rather than a <c>stackalloc</c>, in a method that can be inlined.
        /// </summary>
        WideSameDerivation,

        /// <summary>
        /// 64-bit position arithmetic with the second value derived by SplitMix64's
        /// finalizer on the first, which is a different derivation and different bits.
        /// </summary>
        WideMixer,
    }

    /// <summary>
    /// The placement kernel in place: what hashing an item, deriving its second value and
    /// computing and probing its positions costs together, as a filter does, for each way
    /// of deriving the values.
    /// <para>
    /// <see cref="WidePlacementBenchmarks"/> measures the filters, and finds the 64-bit
    /// placement dearer than its second hash call measured alone
    /// (<see cref="SecondValueDerivationBenchmarks"/>) would explain. This takes the
    /// kernel out of the filter to say where the difference is: the same hash delegate and
    /// the same loop over <c>k</c> positions, into a bare bit array of the size and density
    /// of the filters, with only the kernel changing. The bit array is not
    /// <c>Buckets</c>, which is slower to read, so these figures are lower than the
    /// filters' and the differences between the variants are what to read.
    /// </para>
    /// <para>
    /// The array is filled at random to half density, which is the density a filter sized
    /// for its items has, so a test of an item that was never added stops after about two
    /// probes. The items that are added are put in under the variant being measured.
    /// </para>
    /// </summary>
    [MemoryDiagnoser]
    public class PlacementKernelBenchmarks
    {
        private const int Batch = 4096;
        private const uint K = 7;

        private Func<ReadOnlySpan<byte>, ulong> _hash = null!;
        private ulong[] _bits = null!;
        private byte[][] _hits = null!;
        private byte[][] _misses = null!;
        private uint _m;

        [Params(KernelVariant.Narrow, KernelVariant.Wide, KernelVariant.WideSameDerivation, KernelVariant.WideMixer)]
        public KernelVariant Variant { get; set; }

        [Params(16, 1024)]
        public int ItemSize { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            // The same shape as Defaults.GetDefaultHashFunction.
            _hash = DefaultHash;
            _m = (1U << 27) - 5;

            var state = 12345UL;
            _bits = new ulong[(_m + 63) / 64];
            for (int i = 0; i < _bits.Length; i++)
            {
                state += 0x9E3779B97F4A7C15UL;
                _bits[i] = SplitMix64(state);
            }

            _hits = Items(seed: 1);
            _misses = Items(seed: 2);

            // The two ways of making the same values have to make the same values.
            foreach (var item in _hits)
            {
                var library = Utils.HashKernel128(item, _hash);
                var first = _hash(item);
                if (first != library.LowerBaseHash || SecondFromBuffer(first) != library.UpperBaseHash)
                {
                    throw new InvalidOperationException(
                        "The same-derivation variant does not make HashKernel128's values.");
                }
            }

            foreach (var item in _hits)
            {
                Add(item);
            }
        }

        private static ulong DefaultHash(ReadOnlySpan<byte> data) => XxHash3.HashToUInt64(data);

        [InlineArray(8)]
        private struct EightBytes
        {
            private byte _element;
        }

        private ulong SecondFromBuffer(ulong first)
        {
            EightBytes buffer = default;
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, first);
            return _hash(buffer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong SplitMix64(ulong x)
        {
            var z = x;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Derive(ReadOnlySpan<byte> item, out ulong first, out ulong step)
        {
            switch (Variant)
            {
                case KernelVariant.Narrow:
                    var narrow = Utils.HashKernel(item, _hash);
                    first = narrow.LowerBaseHash;
                    step = narrow.UpperBaseHash;
                    break;
                case KernelVariant.Wide:
                    var wide = Utils.HashKernel128(item, _hash);
                    first = wide.LowerBaseHash;
                    step = wide.UpperBaseHash;
                    break;
                case KernelVariant.WideSameDerivation:
                    first = _hash(item);
                    step = SecondFromBuffer(first);
                    break;
                default:
                    first = _hash(item);
                    step = SplitMix64(first + 0x9E3779B97F4A7C15UL);
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint Position(ulong first, ulong step, uint i) =>
            Variant == KernelVariant.Narrow
                ? ((uint)first + (uint)step * i) % _m
                : (uint)((first + step * i) % _m);

        private void Add(ReadOnlySpan<byte> item)
        {
            Derive(item, out var first, out var step);
            for (uint i = 0; i < K; i++)
            {
                var position = Position(first, step, i);
                _bits[position >> 6] |= 1UL << (int)(position & 63);
            }
        }

        private bool Test(ReadOnlySpan<byte> item)
        {
            Derive(item, out var first, out var step);
            for (uint i = 0; i < K; i++)
            {
                var position = Position(first, step, i);
                if ((_bits[position >> 6] & (1UL << (int)(position & 63))) == 0)
                {
                    return false;
                }
            }
            return true;
        }

        [Benchmark(Baseline = true, OperationsPerInvoke = Batch)]
        public void Add_Batch()
        {
            var items = _hits;
            for (int i = 0; i < items.Length; i++)
            {
                Add(items[i]);
            }
        }

        [Benchmark(OperationsPerInvoke = Batch)]
        public int Contains_Hit()
        {
            var items = _hits;
            var found = 0;
            for (int i = 0; i < items.Length; i++)
            {
                if (Test(items[i]))
                {
                    found++;
                }
            }
            return found;
        }

        [Benchmark(OperationsPerInvoke = Batch)]
        public int Contains_Miss()
        {
            var items = _misses;
            var found = 0;
            for (int i = 0; i < items.Length; i++)
            {
                if (Test(items[i]))
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
                    var z = SplitMix64(state);
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
    }
}
