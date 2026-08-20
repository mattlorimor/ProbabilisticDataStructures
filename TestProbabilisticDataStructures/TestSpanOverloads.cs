using System;
using System.Collections.Generic;
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
        private static Type AssertSpanPathMatches<T>(
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

            return typeof(T);
        }

        /// <summary>
        /// The same, for a structure whose keys are a fixed width. One packed buffer of
        /// equal-length keys, so the slices differ in provenance and offset but not in
        /// length -- which is all the table will accept.
        /// </summary>
        private static Type AssertFixedWidthSpanPathMatches<T>(
            string name, int keySize, Func<T> create, Action<T, byte[]> viaArray,
            Action<T, byte[], int, int> viaSpan)
            where T : IBinaryPersistable<T>
        {
            var buffer = new byte[Items * keySize];
            for (int i = 0; i < Items; i++)
            {
                BitConverter.TryWriteBytes(buffer.AsSpan(i * keySize, keySize), (long)i * 2654435761L);
            }

            var arrayFed = create();
            var spanFed = create();
            for (int i = 0; i < Items; i++)
            {
                viaArray(arrayFed, buffer.AsSpan(i * keySize, keySize).ToArray());
                viaSpan(spanFed, buffer, i * keySize, keySize);
            }

            CollectionAssert.AreEqual(arrayFed.ToByteArray(), spanFed.ToByteArray(),
                $"{name}: feeding the same {Items} keys as spans left a different " +
                "state than feeding them as arrays.");

            return typeof(T);
        }

        [TestMethod]
        public void TestSpanAndArrayPathsLeaveIdenticalState()
        {
            var covered = new[]
            {
            AssertSpanPathMatches("BloomFilter", () => new BloomFilter(1000, 0.01),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l))),

            AssertSpanPathMatches("BloomFilter64", () => new BloomFilter64(1000, 0.01),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l))),

            AssertSpanPathMatches("PartitionedBloomFilter",
                () => new PartitionedBloomFilter(1000, 0.01),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l))),

            AssertSpanPathMatches("CountingBloomFilter",
                () => CountingBloomFilter.NewDefaultCountingBloomFilter(1000, 0.01),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l))),

            AssertSpanPathMatches("DeletableBloomFilter",
                () => new DeletableBloomFilter(1000, 100, 0.01),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l))),

            AssertSpanPathMatches("CuckooBloomFilter",
                () => new CuckooBloomFilter(1000, 0.01, seed: 9),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l))),

            AssertSpanPathMatches("QuotientFilter", () => new QuotientFilter(1000, 0.01),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l))),

            AssertSpanPathMatches("InverseBloomFilter", () => new InverseBloomFilter(500),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l))),

            AssertSpanPathMatches("CountMinSketch", () => new CountMinSketch(0.001, 0.01),
                (s, k) => s.Add(k), (s, b, o, l) => s.Add(b.AsSpan(o, l))),

            AssertSpanPathMatches("CountSketch", () => new CountSketch(0.01, 0.01),
                (s, k) => s.Add(k, 3), (s, b, o, l) => s.Add(b.AsSpan(o, l), 3)),

            AssertSpanPathMatches("HyperLogLog", () => new HyperLogLog(1024),
                (s, k) => s.Add(k), (s, b, o, l) => s.Add(b.AsSpan(o, l))),

            AssertSpanPathMatches("HyperLogLogPlus", () => new HyperLogLogPlus(12),
                (s, k) => s.Add(k), (s, b, o, l) => s.Add(b.AsSpan(o, l))),

            AssertSpanPathMatches("ThetaSketch", () => new ThetaSketch(256),
                (s, k) => s.Add(k), (s, b, o, l) => s.Add(b.AsSpan(o, l))),

            AssertSpanPathMatches("TopK", () => new TopK(0.001, 0.01, 20),
                (s, k) => s.Add(k), (s, b, o, l) => s.Add(b.AsSpan(o, l))),

            AssertSpanPathMatches("HeavyKeeper", () => new HeavyKeeper(20, 512, seed: 3),
                (s, k) => s.Add(k), (s, b, o, l) => s.Add(b.AsSpan(o, l))),

            AssertSpanPathMatches("VarOpt", () => new VarOpt(20, seed: 3),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l))),

            AssertSpanPathMatches("UltraLogLog", () => new UltraLogLog(10),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l))),

            AssertSpanPathMatches("InfiniFilter", () => new InfiniFilter(64, 8),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l))),

            AssertSpanPathMatches("StableBloomFilter",
                () => new StableBloomFilter(1000, 4, 0.01, seed: 5),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l))),

            AssertSpanPathMatches("ScalableBloomFilter",
                () => new ScalableBloomFilter(100, 0.01, 0.8),
                (f, k) => f.Add(k), (f, b, o, l) => f.Add(b.AsSpan(o, l))),

            AssertSpanPathMatches("SetSketch", () => new SetSketch(64),
                (s, k) => s.Add(k), (s, b, o, l) => s.Add(b.AsSpan(o, l))),

            AssertSpanPathMatches("TupleSketch", () => new TupleSketch(256),
                (s, k) => s.Add(k, 1.5), (s, b, o, l) => s.Add(b.AsSpan(o, l), 1.5)),

            AssertSpanPathMatches("SublimeCountMinSketch",
                () => new SublimeCountMinSketch(0.01, 0.5, 1.0),
                (s, k) => s.Add(k), (s, b, o, l) => s.Add(b.AsSpan(o, l))),

            // The two noisy sketches are seeded, so the noise either side of the seam
            // is the same draw and a difference in the payload is a difference in the
            // counts underneath it.
            AssertSpanPathMatches("PrivateCountMinSketch",
                () => new PrivateCountMinSketch(64, 4, 1.0, seed: 3),
                (s, k) => s.Add(k), (s, b, o, l) => s.Add(b.AsSpan(o, l))),

            AssertSpanPathMatches("DpswSketch",
                () => new DpswSketch(
                    window: 128, rho: 4.0, alpha: 0.6, width: 8, depth: 2, seed: 3),
                (s, k) => s.Add(k), (s, b, o, l) => s.Add(b.AsSpan(o, l))),

            AssertFixedWidthSpanPathMatches("InvertibleBloomLookupTable", 8,
                () => new InvertibleBloomLookupTable(64, 8),
                (t, k) => t.Add(k), (t, b, o, l) => t.Add(b.AsSpan(o, l))),
            };

            StructureRoster.AssertCoversEveryType(
                "span equivalence", StructureRoster.WithSpanOverloads, covered,
                (typeof(BinaryFuseFilter),
                    "is built once from the whole key set and has no Add, so there are " +
                    "no two feeding paths to compare. Its span surface is Test, which " +
                    "TestSpanAndArrayQueriesAgree covers."),
                (typeof(BloomierFilter),
                    "likewise: built by Build and queried by TryGetValue, with nothing " +
                    "to feed it incrementally."));
        }

        /// <summary>
        /// A span query, as a delegate. A span cannot travel through <see cref="Func{T,
        /// TResult}"/>, so the sweeps below need their own delegate types to hold one.
        /// </summary>
        private delegate object SpanQuery<T>(T structure, ReadOnlySpan<byte> data);

        private delegate void SpanMutation<T>(T structure, ReadOnlySpan<byte> data);

        /// <summary>
        /// Queries that answer without changing anything must answer identically
        /// through either overload, sliced.
        /// </summary>
        [TestMethod]
        public void TestSpanAndArrayQueriesAgree()
        {
            var bloom = Filled(new BloomFilter(1000, 0.01), (f, k) => f.Add(k));
            var bloom64 = Filled(new BloomFilter64(1000, 0.01), (f, k) => f.Add(k));
            var counting = Filled(new CountingBloomFilter(1000, 4, 0.01), (f, k) => f.Add(k));
            var deletable = Filled(new DeletableBloomFilter(1000, 100, 0.01), (f, k) => f.Add(k));
            var partitioned = Filled(new PartitionedBloomFilter(1000, 0.01), (f, k) => f.Add(k));
            var scalable = Filled(new ScalableBloomFilter(100, 0.01, 0.8), (f, k) => f.Add(k));
            var stable = Filled(new StableBloomFilter(1000, 4, 0.01, seed: 5), (f, k) => f.Add(k));
            var inverse = Filled(new InverseBloomFilter(500), (f, k) => f.Add(k));
            var cuckoo = Filled(new CuckooBloomFilter(1000, 0.01, seed: 9), (f, k) => f.Add(k));
            var quotient = Filled(new QuotientFilter(1000, 0.01), (f, k) => f.Add(k));
            var infini = Filled(new InfiniFilter(64, 8), (f, k) => f.Add(k));
            var cms = Filled(new CountMinSketch(0.001, 0.01), (s, k) => s.Add(k));
            var countSketch = Filled(new CountSketch(0.01, 0.01), (s, k) => s.Add(k, 3));
            var keeper = Filled(new HeavyKeeper(20, 512, seed: 3), (s, k) => s.Add(k));
            var sublime = Filled(new SublimeCountMinSketch(0.01, 0.5, 1.0), (s, k) => s.Add(k));
            var priv = Filled(new PrivateCountMinSketch(64, 4, 1.0, seed: 3), (s, k) => s.Add(k));
            var dpsw = Filled(
                new DpswSketch(window: 128, rho: 4.0, alpha: 0.6, width: 8, depth: 2, seed: 3),
                (s, k) => s.Add(k));

            var half = Enumerable.Range(0, Items).Where(i => i % 2 == 0).ToArray();
            var fuse = BinaryFuseFilter.Build(half.Select(KeyAt));
            var bloomier = BloomierFilter.Build(
                // Eight value bits, so the values have to stay under 256.
                half.Select(i => new KeyValuePair<byte[], ulong>(KeyAt(i), (ulong)(i % 200))), 8);

            var covered = new[]
            {
                AssertQueryAgrees("BloomFilter.Test", bloom,
                    (f, k) => f.Test(k), (f, s) => f.Test(s)),
                AssertQueryAgrees("BloomFilter64.Test", bloom64,
                    (f, k) => f.Test(k), (f, s) => f.Test(s)),
                AssertQueryAgrees("CountingBloomFilter.Test", counting,
                    (f, k) => f.Test(k), (f, s) => f.Test(s)),
                AssertQueryAgrees("DeletableBloomFilter.Test", deletable,
                    (f, k) => f.Test(k), (f, s) => f.Test(s)),
                AssertQueryAgrees("PartitionedBloomFilter.Test", partitioned,
                    (f, k) => f.Test(k), (f, s) => f.Test(s)),
                AssertQueryAgrees("ScalableBloomFilter.Test", scalable,
                    (f, k) => f.Test(k), (f, s) => f.Test(s)),
                AssertQueryAgrees("StableBloomFilter.Test", stable,
                    (f, k) => f.Test(k), (f, s) => f.Test(s)),
                AssertQueryAgrees("InverseBloomFilter.Test", inverse,
                    (f, k) => f.Test(k), (f, s) => f.Test(s)),
                AssertQueryAgrees("CuckooBloomFilter.Test", cuckoo,
                    (f, k) => f.Test(k), (f, s) => f.Test(s)),
                AssertQueryAgrees("QuotientFilter.Test", quotient,
                    (f, k) => f.Test(k), (f, s) => f.Test(s)),
                AssertQueryAgrees("InfiniFilter.Test", infini,
                    (f, k) => f.Test(k), (f, s) => f.Test(s)),
                AssertQueryAgrees("BinaryFuseFilter.Test", fuse,
                    (f, k) => f.Test(k), (f, s) => f.Test(s)),

                AssertQueryAgrees("CountMinSketch.Count", cms,
                    (s, k) => s.Count(k), (s, p) => s.Count(p)),
                AssertQueryAgrees("CountSketch.Count", countSketch,
                    (s, k) => s.Count(k), (s, p) => s.Count(p)),
                AssertQueryAgrees("HeavyKeeper.Count", keeper,
                    (s, k) => s.Count(k), (s, p) => s.Count(p)),
                AssertQueryAgrees("SublimeCountMinSketch.Count", sublime,
                    (s, k) => s.Count(k), (s, p) => s.Count(p)),

                // The noisy pair need no special treatment, which is worth saying
                // because it looks as though they should. Their noise is drawn once at
                // construction and lives in the counters -- that is what stops repeated
                // queries from averaging it away -- so a query is an ordinary read and
                // two calls must return the identical double, not merely a close one.
                AssertQueryAgrees("PrivateCountMinSketch.Count", priv,
                    (s, k) => s.Count(k), (s, p) => s.Count(p)),
                AssertQueryAgrees("DpswSketch.Count", dpsw,
                    (s, k) => s.Count(k), (s, p) => s.Count(p)),

                AssertQueryAgrees("BloomierFilter.TryGetValue", bloomier,
                    (f, k) => { var ok = f.TryGetValue(k, out var v); return (ok, v); },
                    (f, s) => { var ok = f.TryGetValue(s, out var v); return (ok, v); }),
            };

            StructureRoster.AssertCoversEveryType(
                "pure span queries", StructureRoster.WithPureSpanQueries, covered);
        }

        /// <summary>
        /// Queries that answer *and* change the structure must do both identically.
        /// </summary>
        /// <remarks>
        /// Agreeing on every answer is only half of this. Two paths can return the same
        /// value at every step and leave the structure holding different things, and a
        /// filter that answers correctly while holding the wrong thing does not fail
        /// here -- it fails later, somewhere else, for no visible reason. So each pair
        /// is driven step for step and then compared through its payload, the same
        /// oracle the equivalence sweep uses.
        /// </remarks>
        [TestMethod]
        public void TestSpanAndArrayMutatingQueriesAgree()
        {
            var covered = new[]
            {
                AssertMutatingQueryAgrees("BloomFilter.TestAndAdd",
                    () => new BloomFilter(1000, 0.01),
                    (f, k) => f.TestAndAdd(k), (f, s) => f.TestAndAdd(s)),
                AssertMutatingQueryAgrees("BloomFilter64.TestAndAdd",
                    () => new BloomFilter64(1000, 0.01),
                    (f, k) => f.TestAndAdd(k), (f, s) => f.TestAndAdd(s)),
                AssertMutatingQueryAgrees("PartitionedBloomFilter.TestAndAdd",
                    () => new PartitionedBloomFilter(1000, 0.01),
                    (f, k) => f.TestAndAdd(k), (f, s) => f.TestAndAdd(s)),
                AssertMutatingQueryAgrees("ScalableBloomFilter.TestAndAdd",
                    () => new ScalableBloomFilter(100, 0.01, 0.8),
                    (f, k) => f.TestAndAdd(k), (f, s) => f.TestAndAdd(s)),
                AssertMutatingQueryAgrees("InverseBloomFilter.TestAndAdd",
                    () => new InverseBloomFilter(500),
                    (f, k) => f.TestAndAdd(k), (f, s) => f.TestAndAdd(s)),
                AssertMutatingQueryAgrees("StableBloomFilter.TestAndAdd",
                    () => new StableBloomFilter(1000, 4, 0.01, seed: 5),
                    (f, k) => f.TestAndAdd(k), (f, s) => f.TestAndAdd(s)),

                AssertMutatingQueryAgrees("CountingBloomFilter.TestAndAdd",
                    () => new CountingBloomFilter(1000, 4, 0.01),
                    (f, k) => f.TestAndAdd(k), (f, s) => f.TestAndAdd(s)),
                AssertMutatingQueryAgrees("CountingBloomFilter.TestAndRemove",
                    () => Filled(new CountingBloomFilter(1000, 4, 0.01), (f, k) => f.Add(k)),
                    (f, k) => f.TestAndRemove(k), (f, s) => f.TestAndRemove(s)),

                AssertMutatingQueryAgrees("DeletableBloomFilter.TestAndAdd",
                    () => new DeletableBloomFilter(1000, 100, 0.01),
                    (f, k) => f.TestAndAdd(k), (f, s) => f.TestAndAdd(s)),
                AssertMutatingQueryAgrees("DeletableBloomFilter.TestAndRemove",
                    () => Filled(new DeletableBloomFilter(1000, 100, 0.01), (f, k) => f.Add(k)),
                    (f, k) => f.TestAndRemove(k), (f, s) => f.TestAndRemove(s)),

                // Its TestAndAdd answers with a pair -- whether the key was there, and
                // whether room was found for it -- so both halves are compared.
                AssertMutatingQueryAgrees("CuckooBloomFilter.TestAndAdd",
                    () => new CuckooBloomFilter(1000, 0.01, seed: 9),
                    (f, k) => f.TestAndAdd(k), (f, s) => f.TestAndAdd(s)),
                AssertMutatingQueryAgrees("CuckooBloomFilter.TestAndRemove",
                    () => Filled(new CuckooBloomFilter(1000, 0.01, seed: 9), (f, k) => f.Add(k)),
                    (f, k) => f.TestAndRemove(k), (f, s) => f.TestAndRemove(s)),

                AssertMutatingQueryAgrees("QuotientFilter.TestAndRemove",
                    () => Filled(new QuotientFilter(1000, 0.01), (f, k) => f.Add(k)),
                    (f, k) => f.TestAndRemove(k), (f, s) => f.TestAndRemove(s)),
                AssertMutatingQueryAgrees("InfiniFilter.TestAndRemove",
                    () => Filled(new InfiniFilter(64, 8), (f, k) => f.Add(k)),
                    (f, k) => f.TestAndRemove(k), (f, s) => f.TestAndRemove(s)),

                // These two answer with themselves, so there is no value to compare and
                // the state is the whole of it.
                AssertMutationAgrees("SublimeCountMinSketch.Remove",
                    () => Filled(new SublimeCountMinSketch(0.01, 0.5, 1.0), (s, k) => s.Add(k)),
                    (s, k) => s.Remove(k), (s, p) => s.Remove(p)),
                AssertFixedWidthMutationAgrees("InvertibleBloomLookupTable.Remove", 8,
                    () => new InvertibleBloomLookupTable(64, 8),
                    (t, k) => t.Remove(k), (t, p) => t.Remove(p)),
            };

            StructureRoster.AssertCoversEveryType(
                "mutating span queries", StructureRoster.WithMutatingSpanQueries, covered);
        }

        /// <summary>Adds every key to a structure and hands it back.</summary>
        private static T Filled<T>(T structure, Action<T, byte[]> add)
        {
            for (int i = 0; i < Items; i += 2)
            {
                add(structure, KeyAt(i));
            }
            return structure;
        }

        /// <summary>
        /// Asks one structure the same question through both overloads and requires the
        /// same answer. Safe on one instance because the query changes nothing.
        /// </summary>
        private static Type AssertQueryAgrees<T>(
            string name, T structure, Func<T, byte[], object> viaArray, SpanQuery<T> viaSpan)
        {
            var (buffer, slices) = PackedKeys();

            for (int i = 0; i < Items; i++)
            {
                Assert.AreEqual(
                    viaArray(structure, KeyAt(i)),
                    viaSpan(structure, buffer.AsSpan(slices[i].Offset, slices[i].Length)),
                    $"{name} answered differently for key {i} as a span than as an array");
            }

            return typeof(T);
        }

        /// <summary>
        /// Drives two structures the same way, one through each overload, and requires
        /// both the answers and the states they end in to match.
        /// </summary>
        private static Type AssertMutatingQueryAgrees<T>(
            string name, Func<T> create, Func<T, byte[], object> viaArray, SpanQuery<T> viaSpan)
            where T : IBinaryPersistable<T>
        {
            var (buffer, slices) = PackedKeys();
            var arrayDriven = create();
            var spanDriven = create();

            for (int i = 0; i < Items; i++)
            {
                Assert.AreEqual(
                    viaArray(arrayDriven, KeyAt(i)),
                    viaSpan(spanDriven, buffer.AsSpan(slices[i].Offset, slices[i].Length)),
                    $"{name} answered differently for key {i} as a span than as an array");
            }

            CollectionAssert.AreEqual(arrayDriven.ToByteArray(), spanDriven.ToByteArray(),
                $"{name}: the two paths agreed on every answer and still left the " +
                "structure holding different things.");

            return typeof(T);
        }

        /// <summary>
        /// The same, for a mutator that answers with the structure itself. There is no
        /// value to compare, so the state is the whole of the check.
        /// </summary>
        private static Type AssertMutationAgrees<T>(
            string name, Func<T> create, Action<T, byte[]> viaArray, SpanMutation<T> viaSpan)
            where T : IBinaryPersistable<T>
        {
            var (buffer, slices) = PackedKeys();
            var arrayDriven = create();
            var spanDriven = create();

            for (int i = 0; i < Items; i++)
            {
                viaArray(arrayDriven, KeyAt(i));
                viaSpan(spanDriven, buffer.AsSpan(slices[i].Offset, slices[i].Length));
            }

            CollectionAssert.AreEqual(arrayDriven.ToByteArray(), spanDriven.ToByteArray(),
                $"{name}: driving the two overloads the same way left different states.");

            return typeof(T);
        }

        /// <summary>The same again, for a structure that will only take a fixed width.</summary>
        private static Type AssertFixedWidthMutationAgrees<T>(
            string name, int keySize, Func<T> create,
            Action<T, byte[]> viaArray, SpanMutation<T> viaSpan)
            where T : IBinaryPersistable<T>
        {
            var buffer = new byte[Items * keySize];
            for (int i = 0; i < Items; i++)
            {
                BitConverter.TryWriteBytes(buffer.AsSpan(i * keySize, keySize), (long)i * 2654435761L);
            }

            var arrayDriven = create();
            var spanDriven = create();
            for (int i = 0; i < Items; i++)
            {
                viaArray(arrayDriven, buffer.AsSpan(i * keySize, keySize).ToArray());
                viaSpan(spanDriven, buffer.AsSpan(i * keySize, keySize));
            }

            CollectionAssert.AreEqual(arrayDriven.ToByteArray(), spanDriven.ToByteArray(),
                $"{name}: driving the two overloads the same way left different states.");

            return typeof(T);
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
