using System;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The span overloads must be the array overloads, exactly. Two claims, tested
    /// separately: that feeding a structure through spans leaves it in the identical
    /// state (byte for byte, via the persisted payload -- the same oracle the merge
    /// identities use), and that the pure queries allocate nothing, which is the
    /// entire reason the overloads exist.
    /// <para>
    /// The equivalence tests feed spans that are <em>slices of a larger buffer</em>
    /// rather than whole arrays. A span overload that quietly did `span.ToArray()`
    /// and forwarded would pass a whole-array test while defeating the purpose; more
    /// importantly, an implementation that ignored the slice's bounds and hashed the
    /// backing array would too. Slicing is how the overload will actually be called.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestSpanOverloads
    {
        private const int Items = 400;

        /// <summary>One buffer holding every key end to end, plus their offsets.</summary>
        private static (byte[] Buffer, (int Offset, int Length)[] Slices) PackedKeys()
        {
            var keys = Enumerable.Range(0, Items)
                .Select(i => Encoding.UTF8.GetBytes($"key-{i}-{new string('x', i % 17)}"))
                .ToArray();
            var buffer = new byte[keys.Sum(k => k.Length)];
            var slices = new (int, int)[keys.Length];
            var at = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                keys[i].CopyTo(buffer, at);
                slices[i] = (at, keys[i].Length);
                at += keys[i].Length;
            }
            return (buffer, slices);
        }

        private static byte[] KeyAt(int i)
        {
            var (buffer, slices) = PackedKeys();
            return buffer.AsSpan(slices[i].Offset, slices[i].Length).ToArray();
        }

        /// <summary>
        /// Drives a structure twice -- once through arrays, once through slices of a
        /// packed buffer -- and requires the two final states to be identical.
        /// </summary>
        private static void AssertSpanPathMatches<T>(
            string name, Func<T> create, Action<T, byte[]> viaArray,
            Action<T, byte[], int, int> viaSpan)
            where T : IBinaryPersistable<T>
        {
            var (buffer, slices) = PackedKeys();

            var arrayFed = create();
            for (int i = 0; i < Items; i++)
            {
                viaArray(arrayFed, KeyAt(i));
            }

            var spanFed = create();
            for (int i = 0; i < Items; i++)
            {
                viaSpan(spanFed, buffer, slices[i].Offset, slices[i].Length);
            }

            CollectionAssert.AreEqual(arrayFed.ToByteArray(), spanFed.ToByteArray(),
                $"{name}: feeding the same {Items} keys as spans left a different " +
                "state than feeding them as arrays. The overloads must be the same " +
                "operation, not merely a similar one.");
        }

        [TestMethod]
        public void TestSpanAndArrayPathsLeaveIdenticalState()
        {
            AssertSpanPathMatches("BloomFilter", () => new BloomFilter(1000, 0.01),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l)));

            AssertSpanPathMatches("BloomFilter64", () => new BloomFilter64(1000, 0.01),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l)));

            AssertSpanPathMatches("PartitionedBloomFilter",
                () => new PartitionedBloomFilter(1000, 0.01),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l)));

            AssertSpanPathMatches("CountingBloomFilter",
                () => CountingBloomFilter.NewDefaultCountingBloomFilter(1000, 0.01),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l)));

            AssertSpanPathMatches("DeletableBloomFilter",
                () => new DeletableBloomFilter(1000, 100, 0.01),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l)));

            AssertSpanPathMatches("CuckooBloomFilter",
                () => new CuckooBloomFilter(1000, 0.01, seed: 9),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l)));

            AssertSpanPathMatches("QuotientFilter", () => new QuotientFilter(1000, 0.01),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l)));

            AssertSpanPathMatches("InverseBloomFilter", () => new InverseBloomFilter(500),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l)));

            AssertSpanPathMatches("CountMinSketch", () => new CountMinSketch(0.001, 0.01),
                (s, k) => s.Add(k), (s, b, o, l) => s.Add(b.AsSpan(o, l)));

            AssertSpanPathMatches("CountSketch", () => new CountSketch(0.01, 0.01),
                (s, k) => s.Add(k, 3), (s, b, o, l) => s.Add(b.AsSpan(o, l), 3));

            AssertSpanPathMatches("HyperLogLog", () => new HyperLogLog(1024),
                (s, k) => s.Add(k), (s, b, o, l) => s.Add(b.AsSpan(o, l)));

            AssertSpanPathMatches("HyperLogLogPlus", () => new HyperLogLogPlus(12),
                (s, k) => s.Add(k), (s, b, o, l) => s.Add(b.AsSpan(o, l)));

            AssertSpanPathMatches("ThetaSketch", () => new ThetaSketch(256),
                (s, k) => s.Add(k), (s, b, o, l) => s.Add(b.AsSpan(o, l)));

            AssertSpanPathMatches("TopK", () => new TopK(0.001, 0.01, 20),
                (s, k) => s.Add(k), (s, b, o, l) => s.Add(b.AsSpan(o, l)));

            AssertSpanPathMatches("StableBloomFilter",
                () => new StableBloomFilter(1000, 4, 0.01, seed: 5),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l)));

            AssertSpanPathMatches("ScalableBloomFilter",
                () => new ScalableBloomFilter(100, 0.01, 0.8),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l)));
        }

        /// <summary>
        /// Queries must answer identically through either overload, sliced.
        /// </summary>
        [TestMethod]
        public void TestSpanAndArrayQueriesAgree()
        {
            var (buffer, slices) = PackedKeys();

            var bloom = new BloomFilter(1000, 0.01);
            var cms = new CountMinSketch(0.001, 0.01);
            var cs = new CountSketch(0.01, 0.01);
            var qf = new QuotientFilter(1000, 0.01);
            var inverse = new InverseBloomFilter(500);
            for (int i = 0; i < Items; i += 2)
            {
                var k = KeyAt(i);
                bloom.Add(k); cms.Add(k); cs.Add(k); qf.Add(k); inverse.Add(k);
            }

            for (int i = 0; i < Items; i++)
            {
                var k = KeyAt(i);
                var span = buffer.AsSpan(slices[i].Offset, slices[i].Length);

                Assert.AreEqual(bloom.Test(k), bloom.Test(span), $"BloomFilter.Test at {i}");
                Assert.AreEqual(cms.Count(k), cms.Count(span), $"CountMinSketch.Count at {i}");
                Assert.AreEqual(cs.Count(k), cs.Count(span), $"CountSketch.Count at {i}");
                Assert.AreEqual(qf.Test(k), qf.Test(span), $"QuotientFilter.Test at {i}");
                Assert.AreEqual(inverse.Test(k), inverse.Test(span), $"InverseBloomFilter.Test at {i}");
            }

            var fuse = BinaryFuseFilter.Build(
                Enumerable.Range(0, Items).Where(i => i % 2 == 0).Select(KeyAt).ToArray());
            for (int i = 0; i < Items; i++)
            {
                Assert.AreEqual(fuse.Test(KeyAt(i)),
                    fuse.Test(buffer.AsSpan(slices[i].Offset, slices[i].Length)),
                    $"BinaryFuseFilter.Test at {i}");
            }

            var bloomier = BloomierFilter.Build(
                Enumerable.Range(0, Items).ToDictionary(KeyAt, i => (ulong)(i % 200)), 8);
            for (int i = 0; i < Items; i++)
            {
                var found = bloomier.TryGetValue(KeyAt(i), out var fromArray);
                var foundSpan = bloomier.TryGetValue(
                    buffer.AsSpan(slices[i].Offset, slices[i].Length), out var fromSpan);
                Assert.AreEqual(found, foundSpan, $"BloomierFilter.TryGetValue at {i}");
                Assert.AreEqual(fromArray, fromSpan, $"BloomierFilter value at {i}");
            }
        }

        /// <summary>
        /// The point of the overloads. A query taken from a slice must not allocate at
        /// all -- no array to hand in, and none made internally either. Measured over
        /// enough iterations that a per-call allocation of even a few bytes is far
        /// outside what unrelated runtime noise contributes.
        /// </summary>
        [TestMethod]
        public void TestSpanQueriesDoNotAllocate()
        {
            const int Iterations = 20000;
            var (buffer, slices) = PackedKeys();

            var bloom = new BloomFilter(1000, 0.01);
            var cms = new CountMinSketch(0.001, 0.01);
            var qf = new QuotientFilter(1000, 0.01);
            for (int i = 0; i < Items; i += 2)
            {
                var k = KeyAt(i);
                bloom.Add(k); cms.Add(k); qf.Add(k);
            }

            // Warm every path first, so JIT and any one-time setup are not measured.
            for (int i = 0; i < 200; i++)
            {
                var s = buffer.AsSpan(slices[i % Items].Offset, slices[i % Items].Length);
                bloom.Test(s); cms.Count(s); qf.Test(s);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();

            var sink = 0;
            for (int i = 0; i < Iterations; i++)
            {
                var slice = slices[i % Items];
                var s = buffer.AsSpan(slice.Offset, slice.Length);
                if (bloom.Test(s)) sink++;
                sink += (int)cms.Count(s);
                if (qf.Test(s)) sink++;
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Console.WriteLine($"{Iterations * 3} span queries allocated {allocated} bytes (sink={sink})");

            Assert.IsLessThanOrEqualTo(1024L, allocated,
                $"{Iterations * 3} span queries allocated {allocated} bytes. These " +
                "overloads exist so that a caller with data already in a buffer can " +
                "query without allocating; anything proportional to the call count " +
                "means something on the path is still making an array.");
        }
    }
}
