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
    /// What the equivalence tests can and cannot see is worth stating, because the
    /// array overloads forward into the span bodies. Two paths sharing a body cannot
    /// disagree about content, so a defect in that shared body -- truncating the
    /// input, say -- moves both paths together and shows up here as agreement.
    /// Mutation confirms it: truncating a span implementation passes these tests and
    /// fails seven others in the suite, which is the right division of labour. Every
    /// pre-existing test drives the shared bodies through the array API.
    /// </para>
    /// <para>
    /// What these tests own is the seam: that each array overload forwards to its
    /// span counterpart faithfully, and that the two never drift apart if someone
    /// later gives them separate bodies. Forwarding with a wrong offset, or an array
    /// path that does the work twice, both fail here and nowhere else. The spans fed
    /// are slices of a packed buffer rather than whole arrays so that memory
    /// provenance differs between the two runs as well as the call shape.
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

            AssertSpanPathMatches("HeavyKeeper", () => new HeavyKeeper(20, 512, seed: 3),
                (s, k) => s.Add(k), (s, b, o, l) => s.Add(b.AsSpan(o, l)));

            AssertSpanPathMatches("VarOpt", () => new VarOpt(20, seed: 3),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l)));

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
