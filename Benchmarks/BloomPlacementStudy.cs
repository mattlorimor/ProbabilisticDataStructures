using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Threading.Tasks;
using ProbabilisticDataStructures;

namespace Benchmarks
{
    /// <summary>
    /// Measures whether <see cref="BloomFilter"/> delivers the false positive rate it was
    /// sized for, against <see cref="BloomFilter64"/> at the same <c>m</c> and <c>k</c>.
    /// </summary>
    /// <remarks>
    /// An accuracy study rather than a benchmark. Run it with, for example:
    /// <code>
    ///   dotnet run -c Release --project Benchmarks -- study bloom-placement 0.75 0.01 100
    /// </code>
    /// The arguments are the size, as a fraction of 2^32 bits or as a whole number of
    /// bits; the false positive rate the filters are sized for; the queries per trial,
    /// in millions; and optionally the number of trials and the query threads. A filter
    /// at 0.75 of 2^32 bits is about 400 MB, and the study builds two.
    /// <para>
    /// The item count is chosen so that the sizing formula lands on the requested
    /// size, and both filters are sized from it, so they share <c>m</c> and <c>k</c>.
    /// Members and queries are keyed by trial and index and never overlap, so every
    /// positive answer to a query is a false positive.
    /// </para>
    /// <para>
    /// With <c>kernels</c> as a sixth argument it also runs three placement kernels
    /// over a bare bit array at the same <c>m</c>, <c>k</c> and <c>n</c>, which is how
    /// the cause is isolated: the 32-bit kernel exactly as the 32-bit filters compute
    /// it, the same 32-bit base values combined in 64-bit arithmetic so that nothing
    /// wraps, and 64-bit base values as <see cref="BloomFilter64"/> derives them.
    /// </para>
    /// <para>
    /// The predictions printed beside each measurement are the rate a uniform placement
    /// gives, and the rate the 32-bit kernel gives if its only defect is how a 32-bit
    /// value folds onto <c>m</c> positions: the first <c>2^32 mod m</c> positions have
    /// one more preimage than the rest. Both treat the <c>k</c> positions as
    /// independent, which double hashing does not quite make them.
    /// </para>
    /// <para>
    /// With <c>structures</c> in place of the rate, and a number of items in millions
    /// after it, it instead builds each 32-bit structure that reduces positions modulo a
    /// size near the one given, adds that many items, and reports where their bits
    /// landed, read back from the payload each writes:
    /// <code>
    ///   dotnet run -c Release --project Benchmarks -- study bloom-placement 0.4 structures 20
    /// </code>
    /// </para>
    /// </remarks>
    public static class BloomPlacementStudy
    {
        private const double TwoTo32 = 4294967296.0;

        public static void Run(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine(
                    "usage: study bloom-placement <m as a fraction of 2^32, or bits> " +
                    "<fp rate> <queries per trial, millions> [trials] [threads] [kernels]");
                return;
            }

            var size = double.Parse(args[0], CultureInfo.InvariantCulture);
            if (args[1] == "structures")
            {
                Structures(size <= 1.0 ? size * TwoTo32 : size,
                    (uint)(double.Parse(args[2], CultureInfo.InvariantCulture) * 1_000_000));
                return;
            }

            var fpRate = double.Parse(args[1], CultureInfo.InvariantCulture);
            var queries = (long)(double.Parse(args[2], CultureInfo.InvariantCulture) * 1_000_000);
            var trials = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 1;
            var threads = args.Length > 4
                ? int.Parse(args[4], CultureInfo.InvariantCulture)
                : Math.Max(1, Environment.ProcessorCount / 4);
            var kernels = args.Length > 5 && args[5] == "kernels";

            var targetM = size <= 1.0 ? size * TwoTo32 : size;
            var bitsPerItem = Math.Log(Defaults.FILL_RATIO) * Math.Log(1 - Defaults.FILL_RATIO)
                / Math.Abs(Math.Log(fpRate));
            var n = (uint)Math.Floor(targetM * bitsPerItem);

            Console.WriteLine(
                $"# requested m {targetM:N0} ({targetM / TwoTo32:F4} of 2^32), fp {fpRate}, " +
                $"n {n:N0}, {queries:N0} queries x {trials} trials, {threads} threads");

            for (var t = 0; t < trials; t++)
            {
                RunTrial(t, n, fpRate, queries, threads, kernels);
            }
        }

        private static void RunTrial(int trial, uint n, double fpRate, long queries, int threads, bool kernels)
        {
            var clock = Stopwatch.StartNew();
            var narrow = new BloomFilter(n, fpRate);
            var wide = new BloomFilter64(n, fpRate);
            var m = narrow.Capacity();
            var k = narrow.K();

            if (wide.Capacity() != m || wide.K() != k)
            {
                throw new InvalidOperationException(
                    $"the filters disagree on their geometry: {m}/{k} and {wide.Capacity()}/{wide.K()}");
            }

            Parallel.Invoke(
                () => Fill(narrow, trial, n),
                () => Fill(wide, trial, n));

            var narrowHits = Count(narrow.Test, trial, queries, threads);
            var wideHits = Count(wide.Test, trial, queries, threads);

            var (uniform, folded) = Predict(m, k, n);
            Report(trial, "BloomFilter", m, k, n, narrowHits, queries, uniform, folded, clock.Elapsed);
            Report(trial, "BloomFilter64", m, k, n, wideHits, queries, uniform, folded, clock.Elapsed);

            narrow = null;
            wide = null;
            GC.Collect();

            if (!kernels)
            {
                return;
            }

            foreach (var kernel in new[] { Kernel.Narrow, Kernel.NoWrap, Kernel.Wide })
            {
                clock.Restart();
                var bits = new ulong[(m + 63) / 64];
                var key = new byte[10];

                for (uint i = 0; i < n; i++)
                {
                    Key(key, 0x4D, trial, i);
                    Place(kernel, key, m, k, bits, set: true);
                }

                long hits = 0;
                var counts = new long[threads];
                Parallel.For(0, threads, w =>
                {
                    Span<byte> q = stackalloc byte[10];
                    long local = 0;
                    for (long j = w; j < queries; j += threads)
                    {
                        Key(q, 0x51, trial, (ulong)j);
                        if (Place(kernel, q, m, k, bits, set: false))
                        {
                            local++;
                        }
                    }
                    counts[w] = local;
                });
                foreach (var c in counts)
                {
                    hits += c;
                }

                Report(trial, $"kernel {kernel}", m, k, n, hits, queries, uniform, folded, clock.Elapsed);
            }
        }

        /// <summary>
        /// Builds each 32-bit structure whose positions are reduced modulo a size near
        /// <paramref name="target"/>, adds <paramref name="items"/> items, and reads its
        /// bits back out of the payload it writes: the share of set bits that land in
        /// the first <c>2^32 mod m</c> positions says which placement put them there.
        /// </summary>
        /// <remarks>
        /// Uniform placement puts a share of <c>r/m</c> there, where <c>r</c> is
        /// <c>2^32 mod m</c>; the 32-bit fold puts <c>(floor(2^32/m) + 1) r / 2^32</c>.
        /// Reading the bits through the payload keeps this to the public surface, and
        /// FORMAT.md says where each structure's bits sit.
        /// </remarks>
        private static void Structures(double target, uint items)
        {
            var perItem01 = Math.Log(Defaults.FILL_RATIO) * Math.Log(1 - Defaults.FILL_RATIO)
                / Math.Abs(Math.Log(0.01));
            var perItem50 = Math.Log(Defaults.FILL_RATIO) * Math.Log(1 - Defaults.FILL_RATIO)
                / Math.Abs(Math.Log(0.5));
            var n01 = (uint)Math.Floor(target * perItem01);

            Console.WriteLine($"# structures near m {target:N0} ({target / TwoTo32:F4} of 2^32), {items:N0} items each");

            var bloom = new BloomFilter(n01, 0.01);
            Measure("BloomFilter", bloom, bloom.WriteTo, items, 12);
            bloom = null;

            var counting = new CountingBloomFilter(n01, 1, 0.01);
            Measure("CountingBloomFilter b=1", counting, counting.WriteTo, items, 12);
            counting = null;

            var deletable = new DeletableBloomFilter(n01, 100, 0.01);
            Measure("DeletableBloomFilter", deletable, deletable.WriteTo, items, 12);
            deletable = null;

            var stable = StableBloomFilter.NewDefaultStableBloomFilter((uint)target, 0.01);
            Measure("StableBloomFilter d=1", stable, stable.WriteTo, items, 12);
            stable = null;

            if (target * 2 < TwoTo32)
            {
                var stable2 = new StableBloomFilter((uint)target, 2, 0.01, null, 1);
                Measure("StableBloomFilter d=2", stable2, stable2.WriteTo, items, 12);
                stable2 = null;
            }

            // One partition, so the partition is the whole filter: a rate of one half
            // is what gives a partitioned filter a single hash function.
            var partitioned = new PartitionedBloomFilter((uint)Math.Floor(target * perItem50), 0.5);
            Measure("PartitionedBloomFilter k=1", partitioned, partitioned.WriteTo, items, 20);
        }

        private static void Measure(string what, IFilter filter, Action<System.IO.Stream> write, uint items, int header)
        {
            var clock = Stopwatch.StartNew();
            var key = new byte[10];
            for (uint i = 0; i < items; i++)
            {
                Key(key, 0x4D, 0, i);
                filter.Add(key);
            }

            using var buffer = new System.IO.MemoryStream();
            write(buffer);
            var frame = buffer.GetBuffer();

            // The envelope, the structure's leading u32 fields, then a Buckets: u32 count,
            // u8 bits per bucket, u32 data length, data.
            var at = 14 + header;
            var count = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(at));
            var width = frame[at + 4];
            var data = frame.AsSpan(at + 9, (int)BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(at + 5)));

            var q = (ulong)(TwoTo32 / count);
            var r = (ulong)TwoTo32 - q * count;
            long low = 0, all = 0;
            for (ulong b = 0; b < count; b++)
            {
                var offset = b * width;
                var value = (data[(int)(offset >> 3)] >> (int)(offset & 7)) & ((1 << width) - 1);
                if (value != 0)
                {
                    all++;
                    if (b < r)
                    {
                        low++;
                    }
                }
            }

            var share = (double)low / all;
            var se = Math.Sqrt(share * (1 - share) / all);
            Console.WriteLine(
                $"{what,-28} positions {count,13:N0} ({count / TwoTo32:F4})  set {all,12:N0}  " +
                $"share below 2^32 mod m {share:F5} +/- {se:F5}  uniform {(double)r / count:F5}  " +
                $"folded {(q + 1) * r / TwoTo32:F5}  [{clock.Elapsed.TotalSeconds:F0}s]");
        }

        private enum Kernel
        {
            /// <summary>32-bit base values, 32-bit arithmetic: the 32-bit filters exactly.</summary>
            Narrow,
            /// <summary>The same 32-bit base values, combined in 64 bits so nothing wraps.</summary>
            NoWrap,
            /// <summary>64-bit base values as BloomFilter64 derives them.</summary>
            Wide,
        }

        private static bool Place(Kernel kernel, ReadOnlySpan<byte> key, ulong m, uint k, ulong[] bits, bool set)
        {
            var sum = XxHash3.HashToUInt64(key);
            var wideUpper = kernel == Kernel.Wide ? HashOfSum(sum) : 0;

            for (uint i = 0; i < k; i++)
            {
                var position = kernel switch
                {
                    Kernel.Narrow => (ulong)(((uint)sum + (uint)(sum >> 32) * i) % (uint)m),
                    Kernel.NoWrap => ((ulong)(uint)sum + (sum >> 32) * i) % m,
                    _ => (sum + wideUpper * i) % m,
                };

                var word = position >> 6;
                var mask = 1UL << (int)(position & 63);
                if (set)
                {
                    bits[word] |= mask;
                }
                else if ((bits[word] & mask) == 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The second 64-bit base value, as <see cref="Utils.HashKernel128(ReadOnlySpan{byte}, Func{ReadOnlySpan{byte}, ulong})"/>
        /// derives it: the hash of the first one's little-endian bytes.
        /// </summary>
        private static ulong HashOfSum(ulong sum)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, sum);
            return XxHash3.HashToUInt64(bytes);
        }

        private static void Key(Span<byte> key, byte tag, int trial, ulong index)
        {
            key[0] = tag;
            key[1] = (byte)trial;
            BinaryPrimitives.WriteUInt64LittleEndian(key[2..], index);
        }

        private static void Fill(IFilter filter, int trial, uint n)
        {
            var key = new byte[10];
            for (uint i = 0; i < n; i++)
            {
                Key(key, 0x4D, trial, i);
                filter.Add(key);
            }
        }

        private static long Count(Func<byte[], bool> test, int trial, long queries, int threads)
        {
            var counts = new long[threads];
            Parallel.For(0, threads, w =>
            {
                var key = new byte[10];
                long local = 0;
                for (long j = w; j < queries; j += threads)
                {
                    Key(key, 0x51, trial, (ulong)j);
                    if (test(key))
                    {
                        local++;
                    }
                }
                counts[w] = local;
            });

            long hits = 0;
            foreach (var c in counts)
            {
                hits += c;
            }
            return hits;
        }

        /// <summary>
        /// The rate a uniform placement gives, and the rate the 32-bit kernel gives when
        /// its only defect is the fold of a 32-bit value onto <paramref name="m"/>
        /// positions, each treating the <paramref name="k"/> positions as independent.
        /// </summary>
        private static (double Uniform, double Folded) Predict(ulong m, uint k, uint n)
        {
            var inserts = (double)n * k;
            var uniform = Math.Pow(1 - Math.Exp(inserts * Math.Log(1 - 1.0 / m)), k);

            var q = (ulong)(TwoTo32 / m);
            var r = (ulong)TwoTo32 - q * m;
            var heavy = (q + 1) / TwoTo32;
            var light = q / TwoTo32;
            var setHeavy = 1 - Math.Exp(inserts * Math.Log(1 - heavy));
            var setLight = 1 - Math.Exp(inserts * Math.Log(1 - light));
            var hit = r * heavy * setHeavy + (m - r) * light * setLight;

            return (uniform, Math.Pow(hit, k));
        }

        private static void Report(
            int trial, string what, ulong m, uint k, uint n, long hits, long queries,
            double uniform, double folded, TimeSpan elapsed)
        {
            var rate = (double)hits / queries;
            var se = Math.Sqrt(rate * (1 - rate) / queries);
            Console.WriteLine(
                $"t{trial} {what,-16} m {m,13:N0} ({m / TwoTo32:F4})  k {k,2}  n {n,13:N0}  " +
                $"fp {rate * 100:F4}% +/- {se * 100:F4}%  " +
                $"uniform {uniform * 100:F4}%  folded {folded * 100:F4}%  " +
                $"ratio {rate / uniform:F4}  [{elapsed.TotalSeconds:F0}s]");
        }
    }
}
