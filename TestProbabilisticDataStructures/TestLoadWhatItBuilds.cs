using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Whatever a constructor builds, its reader loads.
    /// </summary>
    /// <remarks>
    /// FORMAT.md promises that a payload this library writes reads back. From 3.2.0
    /// until this was written, many readers bounded what a payload could make them
    /// allocate by refusing sizes above a fixed ceiling -- mostly one million, with
    /// messages saying that was "beyond anything this library builds" -- while the
    /// public constructors built more. A structure saved without complaint, and its
    /// own payload was then refused. Nothing here had ever built one past the
    /// ceiling and read it back, so nothing noticed.
    /// <para>
    /// The fix gives each structure one size limit that its constructor and its reader
    /// share. The tests below come in three kinds. A structure built just past the old
    /// ceiling round-trips, wherever that is cheap enough to run in the suite. Where it
    /// is not, a small payload has its size field raised past the old ceiling, and the
    /// reader must get past its size check and refuse the payload for not holding what
    /// it claims -- which is the check a legitimate payload of that size passes. And
    /// the roster at the end holds every structure to one or the other, or to an
    /// exemption whose reason is itself tested.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestLoadWhatItBuilds
    {
        /// <summary>
        /// The ceiling most readers refused counts above until this was fixed: the
        /// value <c>PersistenceFormat.MaxNestedCount</c> had. Written out rather than
        /// read from that constant, because these tests are about the sizes that used
        /// to be refused, and those do not move if the constant later does.
        /// </summary>
        private const uint OldCap = 1_000_000;

        /// <summary>The payload begins after the fixed-size envelope header.</summary>
        private const int PayloadStart = 14;

        /// <summary>
        /// What a reader says when a payload claims more than its bytes hold. A reader
        /// checks this before it allocates for a count it was told, which is what lets
        /// its size limit rise to the constructor's without letting a few bytes ask
        /// for an enormous allocation.
        /// </summary>
        private const string ShortOfBytes = "bytes remain";

        private static byte[] Key(long i)
        {
            var key = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(key, i);
            return key;
        }

        /// <summary>
        /// Writes a structure, reads it back, and requires the copy to write the same
        /// bytes. One comparison covers every field of state, which is the oracle the
        /// round-trip tests elsewhere use.
        /// </summary>
        private static T AssertRoundTrips<T>(T structure) where T : IBinaryPersistable<T>
        {
            var bytes = structure.ToByteArray();
            var restored = Persistence.FromByteArray<T>(bytes);
            Assert.IsTrue(bytes.AsSpan().SequenceEqual(restored.ToByteArray()),
                $"a {typeof(T).Name} read back does not write the bytes it was read from");
            return restored;
        }

        /// <summary>
        /// Overwrites a u32 within the payload and repairs the checksum, so that what
        /// refuses the result is a reader's own check rather than the CRC.
        /// </summary>
        private static byte[] PokeUInt32(byte[] original, int payloadOffset, uint value)
        {
            var bytes = (byte[])original.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(PayloadStart + payloadOffset), value);

            var crc = new Crc32();
            crc.Append(bytes.AsSpan(4, bytes.Length - 8));
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(bytes.Length - 4), crc.GetCurrentHashAsUInt32());
            return bytes;
        }

        /// <summary>
        /// Asserts a reader got past its size check on a count and refused the payload
        /// only because the bytes behind that count are not there.
        /// </summary>
        private static void AssertRefusedOnlyForMissingBytes<T>(byte[] payload)
            where T : IBinaryPersistable<T>
        {
            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<T>(payload));
            StringAssert.Contains(ex.Message, ShortOfBytes,
                $"the {typeof(T).Name} reader refused a size it should accept: {ex.Message}");
        }

        // ---- Built past the old ceiling, and read back ---------------------------

        /// <summary>
        /// Two million items at 1% need 2^20 buckets, the first power of two past the
        /// old ceiling. About 5.5 MiB.
        /// </summary>
        [TestMethod]
        public void TestCuckooFilterPastTheOldBucketCeilingRoundTrips()
        {
            var filter = new CuckooBloomFilter(2_000_000, 0.01, seed: 1);
            for (var i = 0; i < 1000; i++) filter.Add(Key(i));
            Assert.IsGreaterThan(OldCap, filter.BucketCount());

            var restored = AssertRoundTrips(filter);
            Assert.IsTrue(restored.Test(Key(7)));
        }

        /// <summary>
        /// A theta sketch sizes its buffer by what it holds, so a large k costs nothing
        /// until values arrive. The constructor never refused this; the reader did.
        /// </summary>
        [TestMethod]
        public void TestThetaSketchPastTheOldCeilingRoundTrips()
        {
            var sketch = new ThetaSketch(OldCap + 1);
            for (var i = 0; i < 100; i++) sketch.Add(Key(i));

            var restored = AssertRoundTrips(sketch);
            Assert.AreEqual(100UL, restored.Count());
        }

        /// <summary>The tuple sketch's retained size, as for the theta sketch.</summary>
        [TestMethod]
        public void TestTupleSketchPastTheOldCeilingRoundTrips()
        {
            var sketch = new TupleSketch(OldCap + 1);
            for (var i = 0; i < 100; i++) sketch.Add(Key(i), 1.0 + i);

            var restored = AssertRoundTrips(sketch);
            Assert.AreEqual(100UL, restored.Count());
        }

        /// <summary>
        /// A signature one value longer than the old ceiling. About 7.6 MiB.
        /// </summary>
        [TestMethod]
        public void TestMinHashSignaturePastTheOldCeilingRoundTrips()
        {
            var signature = MinHash.Signature(new[] { "alpha", "beta", "gamma" }, (int)OldCap + 1);

            var restored = AssertRoundTrips(signature);
            Assert.AreEqual((int)OldCap + 1, restored.Length);
        }

        /// <summary>
        /// The constructor pre-sizes its heap to k, so this holds about 48 MiB whatever
        /// is added to it.
        /// </summary>
        [TestMethod]
        public void TestTopKPastTheOldCeilingRoundTrips()
        {
            var topK = new TopK(0.01, 0.01, OldCap + 1);
            for (var i = 0; i < 100; i++) topK.Add(Key(i % 10));

            var restored = AssertRoundTrips(topK);
            Assert.HasCount(10, restored.Elements());
        }

        /// <summary>
        /// The sample keeps k + 1 slots in each of its regions from the start, about
        /// 38 MiB here, and a sample read back allocates the same.
        /// </summary>
        [TestMethod]
        public void TestVarOptPastTheOldCeilingRoundTrips()
        {
            var sample = new VarOpt(OldCap + 1, 1);
            for (var i = 0; i < 100; i++) sample.Add(Key(i), 1.0 + i);

            var restored = AssertRoundTrips(sample);
            Assert.AreEqual(100u, restored.SampleCount);
        }

        /// <summary>
        /// HeavyKeeper's k pre-sizes its heap as TopK's does, about 48 MiB.
        /// </summary>
        [TestMethod]
        public void TestHeavyKeeperPastTheOldTrackedCeilingRoundTrips()
        {
            var keeper = new HeavyKeeper(OldCap + 1, 1024, seed: 1);
            for (var i = 0; i < 100; i++) keeper.Add(Key(i % 10));

            var restored = AssertRoundTrips(keeper);
            Assert.HasCount(10, restored.Elements());
        }

        /// <summary>
        /// The old ceiling applied to width times depth, so at the default depth of two
        /// a width of half the ceiling plus one is the first one refused. About 9.5 MiB.
        /// </summary>
        [TestMethod]
        public void TestHeavyKeeperPastTheOldBucketCeilingRoundTrips()
        {
            var keeper = new HeavyKeeper(10, OldCap / 2 + 1, seed: 1);
            for (var i = 0; i < 100; i++) keeper.Add(Key(i % 10));
            Assert.IsGreaterThan((ulong)OldCap, (ulong)keeper.Width * keeper.Depth);

            AssertRoundTrips(keeper);
        }

        /// <summary>
        /// The reader refused more than sixty-four rows, which ceil(ln(1/delta)) passes
        /// at a delta of 1e-28 -- an ordinary double. The rows are tiny.
        /// </summary>
        [TestMethod]
        public void TestSublimeCountMinSketchPastTheOldRowCeilingRoundTrips()
        {
            var sketch = new SublimeCountMinSketch(1e-28);
            for (var i = 0; i < 100; i++) sketch.Add(Key(i));
            Assert.IsGreaterThan(64, sketch.Depth);

            var restored = AssertRoundTrips(sketch);
            Assert.AreEqual(sketch.Count(Key(5)), restored.Count(Key(5)));
        }

        // ---- Too heavy to build here, so the reader's size check is exercised -----

        /// <summary>
        /// Structural, because building one past the old ceiling is not a suite test:
        /// a sketch of 2^26 + 1 registers holds 640 MiB of arrays and writes a 128 MiB
        /// payload. So a small sketch's register count is raised past the old ceiling,
        /// and the reader must accept the count and refuse the payload only because the
        /// registers behind it are missing -- which a real payload of that size has.
        /// </summary>
        [TestMethod]
        public void TestSetSketchReaderTakesARegisterCountPastTheOldCeiling()
        {
            // f64 b, f64 a, then the register count.
            const int RegisterCountOffset = 16;

            var bytes = new SetSketch(16).ToByteArray();
            AssertRefusedOnlyForMissingBytes<SetSketch>(
                PokeUInt32(bytes, RegisterCountOffset, (1u << 26) + 1));
        }

        /// <summary>
        /// Structural: a filter past the old ceiling in words of low bits takes about
        /// 1.6 million keys and 420 MiB at its peak to build. Raising a small filter's
        /// count instead shows the reader no longer refuses the size itself.
        /// </summary>
        [TestMethod]
        public void TestGrafiteReaderTakesLowWordsPastTheOldCeiling()
        {
            // Five u64 (r, c1, c2, keys, range), three u32 (low bits, codes, high bit
            // count) and two u64 (first and last code), then the low word count.
            const int LowWordsOffset = 68;

            var bytes = SmallGrafite().ToByteArray();
            AssertRefusedOnlyForMissingBytes<Grafite>(
                PokeUInt32(bytes, LowWordsOffset, OldCap + 1));
        }

        /// <summary>
        /// Structural: a filter past the old ceiling in words of high bits takes about
        /// twenty-two million keys and 2.3 GiB at its peak to build.
        /// </summary>
        [TestMethod]
        public void TestGrafiteReaderTakesHighWordsPastTheOldCeiling()
        {
            const int LowWordsOffset = 68;

            var bytes = SmallGrafite().ToByteArray();
            var lowWords = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(PayloadStart + LowWordsOffset));
            var highWordsOffset = LowWordsOffset + 4 + (int)lowWords * 8;

            AssertRefusedOnlyForMissingBytes<Grafite>(
                PokeUInt32(bytes, highWordsOffset, OldCap + 1));
        }

        /// <summary>
        /// Structural: a scalable filter reaches a million contained filters only with
        /// a tightening ratio near one, and then holds about 236 MiB. A small one's
        /// count is raised instead.
        /// </summary>
        [TestMethod]
        public void TestScalableFilterReaderTakesAFilterCountPastTheOldCeiling()
        {
            // f64 r, f64 fp, f64 p, u32 hint, then the filter count at version 1; at
            // version 7 the growth factor comes between the hint and the count.
            const int FilterCountOffset = 8 + 8 + 8 + 4;

            foreach (var (growth, offset) in new[] { (1u, FilterCountOffset), (2u, FilterCountOffset + 4) })
            {
                var filter = new ScalableBloomFilter(100, 0.01, 0.8, growth);
                for (var i = 0; i < 500; i++) filter.Add(Key(i));

                AssertRefusedOnlyForMissingBytes<ScalableBloomFilter>(
                    PokeUInt32(filter.ToByteArray(), offset, OldCap + 1));
            }
        }

        private static Grafite SmallGrafite() =>
            Grafite.Build(Enumerable.Range(0, 1000).Select(i => (ulong)i * 97), 0.01, 16, seed: 7);

        // ---- One limit, shared by the constructor and the reader ------------------
        //
        // Each test below holds a structure's constructor and reader to the same
        // limit from both sides: one past it, both refuse; at it, the reader accepts.
        // The reader is shown accepting the limit itself wherever that is cheap --
        // because it sizes what it allocates by what the payload holds, a payload
        // claiming the limit costs nothing, or is refused only for the bytes it is
        // missing. And each limit is pinned to the arithmetic it comes from, worked out
        // here independently, so that moving the shared symbol moves a test too.

        /// <summary>
        /// Asserts a reader refused a payload, and that the message names the reason.
        /// </summary>
        private static void AssertRefused<T>(byte[] payload, string expected)
            where T : IBinaryPersistable<T>
        {
            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<T>(payload));
            StringAssert.Contains(ex.Message, expected);
        }

        /// <summary>What every reader says when a size is past its constructor's limit.</summary>
        private const string PastTheLimit = "beyond anything this library builds";

        /// <summary>
        /// Structural for the table itself: the largest the filter allows is 2^31
        /// entries, gigabytes of fingerprints, and a filter read back allocates its
        /// table whole whether or not anything is in it -- the payload holds only the
        /// occupied entries, so there is nothing to size by. The constructor and the
        /// reader both ask <see cref="CuckooBloomFilter.GeometryProblem"/>, so the limit
        /// is checked there at both of its edges, and each side is shown refusing one
        /// table past it.
        /// </summary>
        [TestMethod]
        public void TestCuckooFilterLimitIsShared()
        {
            // The largest power of two a uint numbers, since the constructor's tables
            // are power-of-two bucket counts of four entries each.
            Assert.IsTrue(BitOperations.IsPow2(CuckooBloomFilter.MaxEntries));
            Assert.IsLessThanOrEqualTo((ulong)uint.MaxValue, CuckooBloomFilter.MaxEntries);
            Assert.IsGreaterThan((ulong)uint.MaxValue, 2 * CuckooBloomFilter.MaxEntries);

            // Both of the limit's edges. In entries: 2^29 buckets of four is exactly
            // the limit, and 2^30 is past it. In words: 2^31 entries at 63 bits fit one
            // array of 64-bit words, and at 64 bits they need one more than it holds.
            Assert.IsNull(CuckooBloomFilter.GeometryProblem(1UL << 29, 4, 63));
            Assert.IsNotNull(CuckooBloomFilter.GeometryProblem(1UL << 30, 4, 1));
            Assert.IsNull(CuckooBloomFilter.GeometryProblem(CuckooBloomFilter.MaxEntries, 1, 1));
            Assert.IsNotNull(CuckooBloomFilter.GeometryProblem(CuckooBloomFilter.MaxEntries + 1, 1, 1));
            Assert.IsNotNull(CuckooBloomFilter.GeometryProblem(1UL << 29, 4, 64));

            // The constructor, past each. 2^29 * 3.8 items is the most whose buckets
            // round up to 2^29; one more rounds to 2^30, which master built with an
            // occupancy map of no entries at all that failed on its first add. And at
            // 1e-19 the fingerprints are 64 bits, too many words at 2^29 buckets.
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new CuckooBloomFilter(2_040_109_466, 0.5));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new CuckooBloomFilter(2_000_000_000, 1e-19));

            // The reader, one table past the limit: u32 bucket count at 0, four entries.
            var bytes = new CuckooBloomFilter(1000, 0.01).ToByteArray();
            AssertRefused<CuckooBloomFilter>(PokeUInt32(bytes, 0, 1u << 30), PastTheLimit);
        }

        /// <summary>
        /// A sketch at the limit costs only what it holds, so the whole promise runs
        /// here: built at the limit, written, read back.
        /// </summary>
        [TestMethod]
        public void TestThetaSketchLimitIsShared()
        {
            // Twice the limit fits one array, and twice one more does not.
            var max = ThetaSketch.MaxNominalEntries;
            Assert.IsLessThanOrEqualTo((long)Array.MaxLength, 2L * max);
            Assert.IsGreaterThan((long)Array.MaxLength, 2L * (max + 1));

            var sketch = new ThetaSketch(max);
            for (var i = 0; i < 100; i++) sketch.Add(Key(i));
            AssertRoundTrips(sketch);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ThetaSketch(max + 1));
            AssertRefused<ThetaSketch>(
                PokeUInt32(sketch.ToByteArray(), 0, max + 1), "not a size this library builds");
        }

        /// <summary>The tuple sketch, as the theta sketch.</summary>
        [TestMethod]
        public void TestTupleSketchLimitIsShared()
        {
            var max = TupleSketch.MaxNominalEntries;
            Assert.IsLessThanOrEqualTo((long)Array.MaxLength, 2L * max);
            Assert.IsGreaterThan((long)Array.MaxLength, 2L * (max + 1));

            var sketch = new TupleSketch(max);
            for (var i = 0; i < 100; i++) sketch.Add(Key(i), 1.0 + i);
            AssertRoundTrips(sketch);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new TupleSketch(max + 1));
            AssertRefused<TupleSketch>(
                PokeUInt32(sketch.ToByteArray(), 0, max + 1), "not a size this library builds");
        }

        /// <summary>
        /// Structural at the limit itself: a signature that long is sixteen gigabytes.
        /// The reader accepts the length and refuses the payload only for the values it
        /// does not hold.
        /// </summary>
        [TestMethod]
        public void TestMinHashSignatureLimitIsShared()
        {
            // Its values are one array.
            Assert.AreEqual(Array.MaxLength, MinHashSignature.MaxValues);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => MinHash.Signature(new[] { "a" }, MinHashSignature.MaxValues + 1));

            var bytes = MinHash.Signature(new[] { "a", "b" }, 8).ToByteArray();
            AssertRefused<MinHashSignature>(
                PokeUInt32(bytes, 0, (uint)MinHashSignature.MaxValues + 1),
                "not a signature this library builds");
            AssertRefusedOnlyForMissingBytes<MinHashSignature>(
                PokeUInt32(bytes, 0, (uint)MinHashSignature.MaxValues));
        }

        /// <summary>
        /// The most a heap holds is the largest prime an array can hold, because the
        /// dictionary beside it sizes its tables to primes. Found here by trial division
        /// rather than read back from the constant.
        /// </summary>
        private static void AssertIsTheLargestPrimeAnArrayHolds(int value)
        {
            static bool IsPrime(long n)
            {
                if (n < 2) return false;
                for (long d = 2; d * d <= n; d++)
                {
                    if (n % d == 0) return false;
                }
                return true;
            }

            Assert.IsTrue(IsPrime(value), $"{value} is not prime");
            Assert.IsLessThanOrEqualTo(Array.MaxLength, value);
            for (long n = value + 1L; n <= Array.MaxLength; n++)
            {
                Assert.IsFalse(IsPrime(n), $"{n} is a larger prime that an array holds");
            }
        }

        /// <summary>
        /// The reader sizes its heap by the elements the payload holds, so a payload
        /// claiming the largest k reads back here at no cost.
        /// </summary>
        [TestMethod]
        public void TestTopKLimitIsShared()
        {
            AssertIsTheLargestPrimeAnArrayHolds(ElementHeap.MaxCapacity);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new TopK(0.1, 0.5, ElementHeap.MaxCapacity + 1u));

            var topK = new TopK(0.1, 0.5, 3);
            for (var i = 0; i < 40; i++) topK.Add(Key(i % 5));
            var bytes = topK.ToByteArray();

            // u32 k at 0.
            AssertRefused<TopK>(PokeUInt32(bytes, 0, ElementHeap.MaxCapacity + 1u), PastTheLimit);

            var widest = Persistence.FromByteArray<TopK>(
                PokeUInt32(bytes, 0, ElementHeap.MaxCapacity));
            CollectionAssert.AreEqual(
                topK.Elements().Select(e => e.Freq).ToArray(),
                widest.Elements().Select(e => e.Freq).ToArray());
            Assert.AreEqual((uint)ElementHeap.MaxCapacity,
                BinaryPrimitives.ReadUInt32LittleEndian(widest.ToByteArray().AsSpan(PayloadStart)));
        }

        /// <summary>
        /// Three limits: k, as TopK's, and the width and depth of the bucket arrays.
        /// Structural at the widest and deepest tables, which are tens of gigabytes;
        /// the reader accepts each and refuses the payload only for the buckets it does
        /// not hold.
        /// </summary>
        [TestMethod]
        public void TestHeavyKeeperLimitsAreShared()
        {
            // Each array of buckets is an array, and so is the array of them.
            Assert.AreEqual((uint)Array.MaxLength, HeavyKeeper.MaxWidth);
            Assert.AreEqual((uint)Array.MaxLength, HeavyKeeper.MaxDepth);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new HeavyKeeper(ElementHeap.MaxCapacity + 1u, 4));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new HeavyKeeper(4, HeavyKeeper.MaxWidth + 1));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new HeavyKeeper(4, 4, HeavyKeeper.MaxDepth + 1));

            var keeper = new HeavyKeeper(3, 64, seed: 1);
            for (var i = 0; i < 40; i++) keeper.Add(Key(i % 5));
            var bytes = keeper.ToByteArray();

            // u32 k at 0, u32 width at 4, u32 depth at 8.
            AssertRefused<HeavyKeeper>(PokeUInt32(bytes, 0, ElementHeap.MaxCapacity + 1u), PastTheLimit);
            AssertRefused<HeavyKeeper>(PokeUInt32(bytes, 4, HeavyKeeper.MaxWidth + 1), PastTheLimit);
            AssertRefused<HeavyKeeper>(PokeUInt32(bytes, 8, HeavyKeeper.MaxDepth + 1), PastTheLimit);

            var widest = Persistence.FromByteArray<HeavyKeeper>(
                PokeUInt32(bytes, 0, ElementHeap.MaxCapacity));
            CollectionAssert.AreEqual(
                keeper.Elements().Select(e => e.Freq).ToArray(),
                widest.Elements().Select(e => e.Freq).ToArray());

            AssertRefusedOnlyForMissingBytes<HeavyKeeper>(PokeUInt32(bytes, 4, HeavyKeeper.MaxWidth));
            AssertRefusedOnlyForMissingBytes<HeavyKeeper>(PokeUInt32(bytes, 8, HeavyKeeper.MaxDepth));
        }

        /// <summary>
        /// Structural at the limit: a sample allocates its regions whole, k + 1 slots
        /// each, whether read back or built, so one at the limit is tens of gigabytes.
        /// What shows the reader accepting the limit is a sampling payload, which holds
        /// exactly k items by construction: with k raised to the limit, the size check
        /// passes and the check that a sampling reservoir is full is what refuses it.
        /// </summary>
        [TestMethod]
        public void TestVarOptLimitIsShared()
        {
            // Each region is an array of k + 1 slots.
            Assert.AreEqual((long)Array.MaxLength, VarOpt.MaxK + 1L);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new VarOpt(VarOpt.MaxK + 1, 1));
            // Where k + 1 wraps to nothing, and master built a sample with no room.
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new VarOpt(uint.MaxValue, 1));

            var sample = new VarOpt(4, 1);
            for (var i = 0; i < 100; i++) sample.Add(Key(i), 1.0 + i);
            var bytes = sample.ToByteArray();

            // u32 k at 0.
            AssertRefused<VarOpt>(PokeUInt32(bytes, 0, VarOpt.MaxK + 1), PastTheLimit);
            AssertRefused<VarOpt>(PokeUInt32(bytes, 0, VarOpt.MaxK), "is sampling with");
        }

        /// <summary>
        /// Structural throughout: a filter at either limit is beyond any suite, and so is
        /// the key set that would make <see cref="Grafite.Build"/> refuse for too many
        /// high bits -- hundreds of millions of keys. The reader accepts each count at
        /// its limit and refuses the payload only for the words it does not hold.
        /// <para>
        /// The high-word limit is a compile-time constant, so its value is pinned by
        /// <see cref="TestFormatDocumentsEveryLimit"/> against the number FORMAT.md
        /// derives, rather than by an assertion the compiler can evaluate.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestGrafiteLimitsAreShared()
        {
            // The low parts are one array.
            Assert.AreEqual((uint)Array.MaxLength, Grafite.MaxLowWords);

            const int LowWordsOffset = 68;
            var bytes = SmallGrafite().ToByteArray();
            var lowWords = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(PayloadStart + LowWordsOffset));
            var highWordsOffset = LowWordsOffset + 4 + (int)lowWords * 8;

            AssertRefused<Grafite>(
                PokeUInt32(bytes, LowWordsOffset, Grafite.MaxLowWords + 1), PastTheLimit);
            AssertRefused<Grafite>(
                PokeUInt32(bytes, highWordsOffset, Grafite.MaxHighWords + 1), PastTheLimit);

            AssertRefusedOnlyForMissingBytes<Grafite>(
                PokeUInt32(bytes, LowWordsOffset, Grafite.MaxLowWords));
            AssertRefusedOnlyForMissingBytes<Grafite>(
                PokeUInt32(bytes, highWordsOffset, Grafite.MaxHighWords));

            // The reduced universe, a u64 at 0, is at most the hash's modulus less one. The
            // builder refuses a request that wants more (TestGrafite holds the edge), the
            // reader a payload that carries more, and a payload at the limit, which is what
            // earlier releases wrote when they capped, reads. (A constant, so its value is
            // pinned by the FORMAT.md table and not by an assertion the compiler evaluates.)
            var past = PokeUInt32(PokeUInt32(bytes, 0, 0xFFFFFFFF), 4, 0x1FFFFFFF);
            AssertRefused<Grafite>(past, PastTheLimit);
            var atTheLimit = PokeUInt32(PokeUInt32(bytes, 0, 0xFFFFFFFE), 4, 0x1FFFFFFF);
            Assert.AreEqual(Grafite.MaxUniverse, Persistence.FromByteArray<Grafite>(atTheLimit).R);

            // The split, a u32 at 40, is at most the builder's widest, which is the largest
            // universe over a single code. The limit is worked from the builder's own
            // split, so it is held to the filter that builder makes there, which reads; one
            // bit more is refused, and so is the width of a code, which the reader once took
            // and which no payload this library wrote carries. (Worked by hand: the largest
            // universe, 2^61 - 2, has its highest bit at 60.)
            const int LowBitsOffset = 40;
            var widest = Grafite.BuildAtUniverse(new ulong[] { 1 }, Grafite.MaxUniverse, 1, seed: 1);
            Assert.AreEqual(Grafite.MaxLowBits, widest.LowBits);
            Assert.AreEqual(60, Grafite.MaxLowBits);
            var widestBytes = widest.ToByteArray();
            Assert.AreEqual(
                Grafite.MaxLowBits, Persistence.FromByteArray<Grafite>(widestBytes).LowBits);
            AssertRefused<Grafite>(
                PokeUInt32(widestBytes, LowBitsOffset, (uint)Grafite.MaxLowBits + 1), PastTheLimit);
            AssertRefused<Grafite>(PokeUInt32(widestBytes, LowBitsOffset, 64), PastTheLimit);

            // And no other input to the builder splits wider: two codes over the same
            // universe split one bit narrower.
            Assert.AreEqual(
                Grafite.MaxLowBits - 1,
                Grafite.BuildAtUniverse(new ulong[] { 1, 2 }, Grafite.MaxUniverse, 1, seed: 1).LowBits);
        }

        /// <summary>
        /// Structural at the limit, where a sketch is twenty gigabytes of arrays.
        /// </summary>
        [TestMethod]
        public void TestSetSketchLimitIsShared()
        {
            // The registers are an array.
            Assert.AreEqual(Array.MaxLength, SetSketch.MaxRegisters);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new SetSketch(SetSketch.MaxRegisters + 1));

            // f64 b, f64 a, then the register count.
            var bytes = new SetSketch(16).ToByteArray();
            AssertRefused<SetSketch>(
                PokeUInt32(bytes, 16, (uint)SetSketch.MaxRegisters + 1), "an array holds");
            AssertRefusedOnlyForMissingBytes<SetSketch>(
                PokeUInt32(bytes, 16, (uint)SetSketch.MaxRegisters));
        }

        /// <summary>
        /// The deepest sketch is small, so it is built and read back here: the smallest
        /// delta whose reciprocal is a finite double gives exactly the limit.
        /// </summary>
        [TestMethod]
        public void TestSublimeCountMinSketchLimitIsShared()
        {
            Assert.AreEqual((int)Math.Ceiling(Math.Log(double.MaxValue)), SublimeCountMinSketch.MaxDepth);

            var deepest = new SublimeCountMinSketch(1e-308);
            Assert.AreEqual(SublimeCountMinSketch.MaxDepth, deepest.Depth);
            for (var i = 0; i < 100; i++) deepest.Add(Key(i));
            AssertRoundTrips(deepest);

            // Smaller still, the reciprocal is infinite and there is no row count.
            Assert.IsTrue(double.IsPositiveInfinity(1 / double.Epsilon));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new SublimeCountMinSketch(double.Epsilon));

            // f64 delta, f64 growth exponent, f64 size factor, then the row count.
            AssertRefused<SublimeCountMinSketch>(
                PokeUInt32(deepest.ToByteArray(), 24, (uint)SublimeCountMinSketch.MaxDepth + 1),
                "rows");

            // The width of a row is the largest power of two an int holds. The
            // constructor starts a row there and no wider, and the reader takes it and no
            // wider: past it, the payload is refused as past the limit, and at it only for
            // what a row of a billion counters costs to read. TestSublimeWidth holds the
            // sketch's own refusal to widen past it.
            var widthOffset = 24 + sizeof(uint);
            AssertRefused<SublimeCountMinSketch>(
                PokeUInt32(deepest.ToByteArray(), widthOffset, (uint)SublimeCountMinSketch.MaxWidth * 2),
                "no larger than");
            var atTheLimit = Assert.ThrowsExactly<InvalidDataException>(() =>
                Persistence.FromByteArray<SublimeCountMinSketch>(
                    PokeUInt32(deepest.ToByteArray(), widthOffset, (uint)SublimeCountMinSketch.MaxWidth)));
            Assert.IsInstanceOfType<ReadBudgetExceededException>(atTheLimit.InnerException,
                "a payload at the widest row should be refused for what it costs, not its size");
        }

        /// <summary>
        /// A row is one array, and the depth is the rows the smallest delta with a
        /// reciprocal gives. The deepest sketch is small, so it is built and read back;
        /// the widest is gigabytes a row, so its side is the constructor's refusal one
        /// past, the reader's refusals, and at the limit the cost it is refused for.
        /// </summary>
        [TestMethod]
        public void TestCountMinSketchLimitsAreShared()
        {
            Assert.AreEqual((uint)Array.MaxLength, CountMinSketch.MaxWidth);
            // The same depth as the Sublime sketch, whose limit is worked out separately.
            Assert.AreEqual((uint)SublimeCountMinSketch.MaxDepth, CountMinSketch.MaxDepth);

            Assert.AreEqual("epsilon", Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new CountMinSketch(Math.E / (Array.MaxLength + 0.5), 0.5)).ParamName);
            Assert.AreEqual("delta", Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new CountMinSketch(0.5, double.Epsilon)).ParamName);

            var deepest = new CountMinSketch(0.5, 1e-308);
            Assert.AreEqual(CountMinSketch.MaxDepth, deepest.Depth);
            for (var i = 0; i < 100; i++) deepest.Add(Key(i));
            AssertRoundTrips(deepest);

            // f64 epsilon, f64 delta, u32 width, u32 depth.
            var bytes = new CountMinSketch(0.5, 0.5).ToByteArray();
            AssertRefused<CountMinSketch>(PokeUInt32(bytes, 16, CountMinSketch.MaxWidth + 1), PastTheLimit);
            AssertRefused<CountMinSketch>(PokeUInt32(bytes, 20, CountMinSketch.MaxDepth + 1), PastTheLimit);

            var atTheLimit = Assert.ThrowsExactly<InvalidDataException>(() =>
                Persistence.FromByteArray<CountMinSketch>(
                    PokeUInt32(bytes, 16, CountMinSketch.MaxWidth)));
            Assert.IsInstanceOfType<ReadBudgetExceededException>(atTheLimit.InnerException,
                "a payload at the widest row should be refused for what it costs, not its size");

            // At the deepest, the size is accepted and the rows it claims are not there.
            AssertRefused<CountMinSketch>(
                PokeUInt32(bytes, 20, CountMinSketch.MaxDepth), "Payload ended");

            // The edges of what the constructor's two checks accept, which are reached
            // here without the arrays: the widest row is the largest array, and the
            // deepest sketch is the smallest delta that has a reciprocal.
            Assert.AreEqual((uint)Array.MaxLength, Guard.SketchWidth(Array.MaxLength, 1.0, "epsilon"));
            Assert.AreEqual(1u, Guard.SketchWidth(1.0, 1.0, "epsilon"));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => Guard.SketchWidth(Array.MaxLength + 1.0, 1.0, "epsilon"));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => Guard.SketchWidth(0.0, 1.0, "epsilon"));
            Assert.AreEqual(CountMinSketch.MaxDepth, Guard.SketchRows(5.6e-309, "delta"));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => Guard.SketchRows(5.5e-309, "delta"));
            Assert.AreEqual(1u, Guard.SketchRows(0.9999, "delta"));
        }

        /// <summary>
        /// The rows and the counters in each are arrays. Structural at the limit, where a
        /// sketch is gigabytes of noise: the constructor's refusal one past, the reader's
        /// the same, and the cost a payload at the limit is refused for.
        /// </summary>
        [TestMethod]
        public void TestPrivateCountMinSketchLimitsAreShared()
        {
            Assert.AreEqual((uint)Array.MaxLength, PrivateCountMinSketch.MaxWidth);
            Assert.AreEqual((uint)Array.MaxLength, PrivateCountMinSketch.MaxDepth);

            Assert.AreEqual("width", Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new PrivateCountMinSketch(PrivateCountMinSketch.MaxWidth + 1, 1, 1.0)).ParamName);
            Assert.AreEqual("depth", Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new PrivateCountMinSketch(1, PrivateCountMinSketch.MaxDepth + 1, 1.0)).ParamName);

            // u32 width, u32 depth.
            var bytes = new PrivateCountMinSketch(64, 2, 1.0, seed: 1).ToByteArray();
            AssertRefused<PrivateCountMinSketch>(
                PokeUInt32(bytes, 0, PrivateCountMinSketch.MaxWidth + 1), PastTheLimit);
            AssertRefused<PrivateCountMinSketch>(
                PokeUInt32(bytes, 4, PrivateCountMinSketch.MaxDepth + 1), PastTheLimit);

            foreach (var offset in new[] { 0, 4 })
            {
                var atTheLimit = Assert.ThrowsExactly<InvalidDataException>(() =>
                    Persistence.FromByteArray<PrivateCountMinSketch>(
                        PokeUInt32(bytes, offset, (uint)Array.MaxLength)));
                Assert.IsInstanceOfType<ReadBudgetExceededException>(atTheLimit.InnerException,
                    "a payload at the limit should be refused for what it costs, not its size");
            }
        }

        /// <summary>
        /// The slots are one array of references, sixteen gigabytes at the limit. The
        /// constructor refuses one past it, the reader refuses a payload that claims
        /// one past it, and a payload at it is refused for what the slots cost.
        /// </summary>
        [TestMethod]
        public void TestInverseBloomFilterLimitIsShared()
        {
            Assert.AreEqual((uint)Array.MaxLength, InverseBloomFilter.MaxCapacity);
            Assert.AreEqual("capacity", Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new InverseBloomFilter(InverseBloomFilter.MaxCapacity + 1)).ParamName);

            // u32 capacity, then u32 occupied.
            var bytes = new InverseBloomFilter(100).ToByteArray();
            AssertRefused<InverseBloomFilter>(
                PokeUInt32(bytes, 0, InverseBloomFilter.MaxCapacity + 1), PastTheLimit);

            var atTheLimit = Assert.ThrowsExactly<InvalidDataException>(() =>
                Persistence.FromByteArray<InverseBloomFilter>(
                    PokeUInt32(bytes, 0, InverseBloomFilter.MaxCapacity)));
            Assert.IsInstanceOfType<ReadBudgetExceededException>(atTheLimit.InnerException,
                "a payload at the limit should be refused for what it costs, not its size");
        }

        /// <summary>
        /// A substream's checkpoints are a list with a slot for each item, so the
        /// longest substream is the longest list. The constructor refuses a window and
        /// factor that make more, before building the list; the reader refuses a payload
        /// that claims more, and one that claims exactly that is refused for what the
        /// list would cost. (<see cref="TestDpswSketch"/> holds the sizing rule to its
        /// edge, one item either side.)
        /// <para>
        /// The sketches a window holds are private sketches, so its width and depth are
        /// limited by theirs, and by the same symbols rather than a second pair: the
        /// constructor refuses one past each, naming its own parameter; the reader refuses
        /// a payload that claims one past each, as beyond anything built, and reads the
        /// limit itself.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestDpswSketchLimitIsShared()
        {
            Assert.AreEqual(Array.MaxLength, DpswSketch.MaxSubstreamSize);
            Assert.AreEqual("window", Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new DpswSketch(1L << 40, 1.0, beta: 0.9)).ParamName);

            // u64 window, f64 rho, f64 alpha, then u32 substream size at 24.
            var bytes = new DpswSketch(100, 1.0, seed: 1).ToByteArray();
            AssertRefused<DpswSketch>(
                PokeUInt32(bytes, 24, (uint)DpswSketch.MaxSubstreamSize + 1), PastTheLimit);

            // A window the low word of which is 2^32 - 1 can have a substream at the limit.
            var wide = PokeUInt32(bytes, 0, uint.MaxValue);
            var atTheLimit = Assert.ThrowsExactly<InvalidDataException>(() =>
                Persistence.FromByteArray<DpswSketch>(
                    PokeUInt32(wide, 24, (uint)DpswSketch.MaxSubstreamSize)));
            Assert.IsInstanceOfType<ReadBudgetExceededException>(atTheLimit.InnerException,
                "a payload at the limit should be refused for what it costs, not its size");

            // The shape of the sketches it holds is the private sketch's, the same symbols.
            Assert.AreEqual("width", Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new DpswSketch(100, 1.0, width: PrivateCountMinSketch.MaxWidth + 1)).ParamName);
            Assert.AreEqual("depth", Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new DpswSketch(100, 1.0, depth: PrivateCountMinSketch.MaxDepth + 1)).ParamName);

            // The u32 width at 28 and the u32 depth at 32, after the substream size. Written
            // at two budgets, because the deepest shape is also refused by the guard on the
            // noise of the leanest checkpoint, which at a budget of 1 sees a depth past the
            // limit first and refuses it for that.
            foreach (var rho in new[] { 1.0, 1e9 })
            {
                var shaped = new DpswSketch(100, rho, seed: 1).ToByteArray();

                AssertRefused<DpswSketch>(
                    PokeUInt32(shaped, 28, PrivateCountMinSketch.MaxWidth + 1), PastTheLimit);
                AssertRefused<DpswSketch>(
                    PokeUInt32(shaped, 32, PrivateCountMinSketch.MaxDepth + 1), PastTheLimit);
                AssertRefused<DpswSketch>(PokeUInt32(shaped, 28, uint.MaxValue), PastTheLimit);
                AssertRefused<DpswSketch>(PokeUInt32(shaped, 32, uint.MaxValue), PastTheLimit);
            }

            // At the limit the reader accepts, and writes back what it read: a window that
            // has begun no substream holds no sketch, so there is nothing to allocate. The
            // budget is generous so that the deepest shape is not refused for its noise.
            var generous = new DpswSketch(100, 1e9, seed: 1).ToByteArray();
            foreach (var offset in new[] { 28, 32 })
            {
                var limit = PokeUInt32(generous, offset, (uint)Array.MaxLength);
                var restored = Persistence.FromByteArray<DpswSketch>(limit);
                Assert.IsTrue(limit.AsSpan().SequenceEqual(restored.ToByteArray()),
                    "a window of the largest shape does not write the bytes it was read from");
            }
        }

        /// <summary>
        /// There is no constructor argument here: a scalable filter grows by adding
        /// filters to a list until the list refuses, so its limit is the list's.
        /// Structural at the limit, where the list alone would be sixteen gigabytes of
        /// references.
        /// </summary>
        [TestMethod]
        public void TestScalableFilterLimitIsShared()
        {
            Assert.AreEqual(Array.MaxLength, ScalableBloomFilter.MaxFilters);

            // At version 7 the growth factor sits between the hint and the count.
            const int FilterCountOffset = 8 + 8 + 8 + 4;
            foreach (var (growth, offset) in new[] { (1u, FilterCountOffset), (2u, FilterCountOffset + 4) })
            {
                var filter = new ScalableBloomFilter(100, 0.01, 0.8, growth);
                for (var i = 0; i < 500; i++) filter.Add(Key(i));
                var bytes = filter.ToByteArray();

                AssertRefused<ScalableBloomFilter>(
                    PokeUInt32(bytes, offset, (uint)ScalableBloomFilter.MaxFilters + 1),
                    PastTheLimit);
                AssertRefusedOnlyForMissingBytes<ScalableBloomFilter>(
                    PokeUInt32(bytes, offset, (uint)ScalableBloomFilter.MaxFilters));
            }
        }

        /// <summary>
        /// The counters are one array, so the largest counting filter is the most counters
        /// one holds at the width asked for, 2,147,483,591 at eight bits. Structural at the
        /// limit, where the table is two gigabytes: the constructor's side is its sizing,
        /// reached with the first item count to size past the limit, and the reader's is
        /// a small payload with its count raised, which is refused one counter past the
        /// limit as beyond anything built and at the limit only for the table it lacks.
        /// </summary>
        [TestMethod]
        public void TestCountingFilterLimitIsShared()
        {
            // The counters are an array.
            Assert.AreEqual((long)Array.MaxLength, Buckets.MaxDataBytes);

            // The first item count to size past the limit is refused, against n.
            var n = TestBucketSizes.FirstItemCountSizedAbove((ulong)Array.MaxLength, 0.01);
            Assert.AreEqual("n", Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new CountingBloomFilter(n, 8, 0.01)).ParamName);

            // u32 m, u32 k, u8 placement, u64 count, then the buckets: u32 count, u8
            // width, bytes.
            var bytes = new CountingBloomFilter(1000, 8, 0.01).ToByteArray();
            AssertBucketLimitIsShared<CountingBloomFilter>(bytes, 0, 17);
        }

        /// <summary>
        /// The same for the stable filter's cells, which are its own parameter.
        /// </summary>
        [TestMethod]
        public void TestStableFilterLimitIsShared()
        {
            Assert.AreEqual((long)Array.MaxLength, Buckets.MaxDataBytes);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new StableBloomFilter((uint)Array.MaxLength + 1, 8, 0.01, seed: 1));

            // u32 m, u32 k, u32 p, u8 placement, then the buckets.
            var bytes = new StableBloomFilter(1000, 8, 0.01, seed: 1).ToByteArray();
            AssertBucketLimitIsShared<StableBloomFilter>(bytes, 0, 13);
        }

        /// <summary>
        /// A reader past the limit says so, and at it asks only for the table. The
        /// payload's two counts are raised together because a filter checks them against
        /// each other.
        /// </summary>
        private static void AssertBucketLimitIsShared<T>(byte[] eightBit, int mOffset, int bucketCountOffset)
            where T : IBinaryPersistable<T>
        {
            var limit = (uint)Buckets.MaxCount(8);
            Assert.AreEqual((uint)Array.MaxLength, limit);

            AssertRefused<T>(
                PokeUInt32(PokeUInt32(eightBit, mOffset, limit + 1), bucketCountOffset, limit + 1),
                PastTheLimit);

            var ex = Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<T>(
                PokeUInt32(PokeUInt32(eightBit, mOffset, limit), bucketCountOffset, limit)));
            StringAssert.Contains(ex.Message, "were stored",
                "a payload at the limit should be refused for its table, not its size");
            Assert.DoesNotContain(PastTheLimit, ex.Message);
        }

        /// <summary>
        /// The quotient filter was the other way round: its reader's limits were the
        /// right ones, and its constructor built past them -- a million items at 5e-14
        /// split a 64-bit hash into 66 bits and wrote a payload its own reader refused.
        /// The constructor now refuses that, so the widest split it does build reads
        /// back: all sixty-four bits, at a small table and at a million items (about
        /// 12 MiB).
        /// </summary>
        [TestMethod]
        public void TestQuotientFilterAtItsWidestSplitRoundTrips()
        {
            // Six items need three quotient bits, and 2^-61 the widest remainder a slot
            // holds: 64 in all.
            var finest = Math.ScaleB(1.0, -QuotientFilter.MaxRemainderBits);
            var (q, r) = QuotientFilter.Split(6, finest);
            Assert.AreEqual((uint)QuotientFilter.HashBits, q + r);

            var small = new QuotientFilter(6, finest);
            small.Add(Key(1));
            AssertRoundTrips(small);

            // A million items need 21 quotient bits, and 1.2e-13 needs 43.
            var large = new QuotientFilter(1_000_000, 1.2e-13);
            for (var i = 0; i < 100; i++) large.Add(Key(i));
            var restored = AssertRoundTrips(large);
            Assert.IsTrue(restored.Test(Key(7)));
        }

        /// <summary>
        /// The constructor's sizing is reachable on its own through
        /// <see cref="QuotientFilter.Split"/>, so its edges are checked without
        /// allocating the tables they describe -- the largest is gigabytes. Every split it
        /// returns is inside what the reader accepts, and one past the reader's own
        /// limits is refused. The reader takes a 32-bit quotient as well, one more than
        /// the constructor builds, because earlier constructors wrote a wrapped one-slot
        /// table that way; that payload is pinned by its own fixture.
        /// </summary>
        [TestMethod]
        public void TestQuotientFilterLimitIsShared()
        {
            // The largest table: q = 31, and one item more is refused.
            Assert.AreEqual((uint)QuotientFilter.MaxQuotientBits,
                QuotientFilter.Split(QuotientFilter.MaxItems, 0.5).QuotientBits);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => QuotientFilter.Split(QuotientFilter.MaxItems + 1, 0.5));
            Assert.IsLessThanOrEqualTo((ulong)uint.MaxValue, 1UL << QuotientFilter.MaxQuotientBits);
            Assert.IsGreaterThan((ulong)uint.MaxValue, 1UL << (QuotientFilter.MaxQuotientBits + 1));

            // The widest split: all of the hash at a million items, and one bit more is
            // refused.
            var (q, r) = QuotientFilter.Split(1_000_000, Math.ScaleB(1.0, -43));
            Assert.AreEqual((uint)QuotientFilter.HashBits, q + r);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => QuotientFilter.Split(1_000_000, Math.ScaleB(1.0, -44)));

            // The widest remainder: what a 64-bit word holds beside three metadata bits.
            Assert.AreEqual((uint)QuotientFilter.MaxRemainderBits,
                QuotientFilter.Split(1, Math.ScaleB(1.0, -QuotientFilter.MaxRemainderBits)).RemainderBits);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => QuotientFilter.Split(1, Math.ScaleB(1.0, -QuotientFilter.MaxRemainderBits - 1)));

            // The reader, one past each of its limits: u32 quotient bits at 0, u32
            // remainder bits at 4.
            var bytes = new QuotientFilter(100, 0.01).ToByteArray();
            AssertRefused<QuotientFilter>(
                PokeUInt32(bytes, 0, (uint)QuotientFilter.MaxQuotientBits + 2), "quotient bits");

            // One bit past the hash, exactly: the remainder that, with this filter's
            // quotient, makes sixty-five.
            var quotientBits = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(PayloadStart));
            AssertRefused<QuotientFilter>(
                PokeUInt32(bytes, 4, (uint)QuotientFilter.HashBits - quotientBits + 1), "not a split of a");
        }

        // ---- Exemptions, and the tests that check their reasons --------------------

        /// <summary>
        /// Exempt: one pair of constants already bounds the precision for both the
        /// constructor and the reader, and the sparse count the reader caps cannot get
        /// near the cap -- the sparse form converts to dense past a size set by the
        /// precision, and is largest at the widest precision.
        /// </summary>
        [TestMethod]
        public void TestHyperLogLogPlusCannotReachItsReaderCaps()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new HyperLogLogPlus(HyperLogLogPlus.MaxPrecision + 1));

            var widest = new HyperLogLogPlus(HyperLogLogPlus.MaxPrecision);
            Assert.IsLessThan((int)PersistenceFormat.MaxNestedCount, widest.MaxSparseEntries);

            for (var i = 0; i < 1000; i++) widest.Add(Key(i));
            AssertRoundTrips(widest);
        }

        /// <summary>
        /// Exempt: the partition count is the hash count, ceil(log2(1 / rate)), and for
        /// any rate whose reciprocal is a finite double that is at most
        /// ceil(log2(double.MaxValue)) -- far below the reader's cap. A rate whose
        /// reciprocal is infinite is refused by the constructor rather than built.
        /// </summary>
        [TestMethod]
        public void TestPartitionedFilterCannotReachItsPartitionCap()
        {
            var bound = Math.Ceiling(Math.Log2(double.MaxValue));
            Assert.IsLessThan((double)PersistenceFormat.MaxNestedCount, bound);

            var tightest = new PartitionedBloomFilter(1, 1e-308);
            Assert.AreEqual((uint)bound, tightest.K());
            tightest.Add(Key(1));
            AssertRoundTrips(tightest);

            Assert.Throws<SystemException>(() => new PartitionedBloomFilter(1, double.Epsilon));
        }

        /// <summary>
        /// Exempt: the arrays a 64-bit filter's buckets are split into hold 2^30 buckets
        /// each, so the reader's cap on how many there are is reached only by a filter
        /// whose bits alone are more than any payload holds -- an envelope carries at
        /// most int.MaxValue bytes. Below that, nothing caps it: one past the old
        /// ceiling in buckets reads back.
        /// </summary>
        [TestMethod]
        public void TestBloomFilter64CannotReachItsArrayCap()
        {
            var bytesAtTheCap = (ulong)PersistenceFormat.MaxNestedCount * Buckets64.maxArraySize / 8;
            Assert.IsGreaterThan((ulong)int.MaxValue, bytesAtTheCap);

            var filter = new BloomFilter64(OldCap, 0.01);
            for (var i = 0; i < 100; i++) filter.Add(Key(i));
            Assert.IsGreaterThan((ulong)OldCap, filter.Capacity());
            AssertRoundTrips(filter);
        }

        /// <summary>
        /// A table of 2^21 slots, about 3 MiB, well past the million the readers once
        /// stopped at, and read back whole.
        /// </summary>
        [TestMethod]
        public void TestInfiniFilterAtAWideTableRoundTrips()
        {
            var filter = new InfiniFilter(1u << 20, fingerprintBits: 8);
            for (var i = 0; i < 100; i++) filter.Add(Key(i));
            Assert.IsGreaterThan((ulong)OldCap, filter.Capacity());

            var restored = AssertRoundTrips(filter);
            Assert.IsTrue(restored.Test(Key(7)));
        }

        /// <summary>The range filter's table, as above.</summary>
        [TestMethod]
        public void TestMementoFilterAtAWideTableRoundTrips()
        {
            var filter = new MementoFilter(256, fingerprintBits: 8, initialCapacity: 1u << 20);
            for (ulong i = 0; i < 100; i++) filter.Add(i * 7919);
            Assert.IsGreaterThan((ulong)OldCap, filter.Capacity());

            var restored = AssertRoundTrips(filter);
            Assert.IsTrue(restored.Test(7919));
        }

        /// <summary>
        /// A table's slot count is a uint holding 2^q, so q is at most 31; the constructor
        /// starts no wider, an expansion stops there, and the reader takes no more. The
        /// largest table is gigabytes, so each side is checked where it can be: the
        /// constructor through its sizing, the expansion in <see cref="TestExpandingTableLimits"/>
        /// with a lower limit, and the reader on payloads that name the widths. The other
        /// two things the reader caps are still beyond the constructor: the fingerprint's
        /// range is the same on both sides, and the chain would need more items than
        /// <c>Added</c> can count to reach its cap.
        /// </summary>
        [TestMethod]
        public void TestInfiniFilterLimitIsShared()
        {
            Assert.AreEqual(typeof(uint),
                typeof(InfiniSegment).GetProperty(nameof(InfiniSegment.Slots), BindingFlags.Instance | BindingFlags.NonPublic)!.PropertyType);
            Assert.IsLessThanOrEqualTo((ulong)uint.MaxValue, 1UL << InfiniFilter.MaxQuotientBits);
            Assert.IsGreaterThan((ulong)uint.MaxValue, 1UL << (InfiniFilter.MaxQuotientBits + 1));

            Assert.AreEqual(InfiniFilter.MaxQuotientBits, InfiniFilter.InitialQuotientBits(uint.MaxValue));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new InfiniFilter(1, 1));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new InfiniFilter(1, 33));

            AssertRefused<InfiniFilter>(
                TestExpandingTableLimits.OnePayload(
                    StructureId.InfiniFilter, InfiniFilter.MaxQuotientBits + 1, 4, includeWords: true),
                "between 2 and 31");
            var atTheLimit = Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<InfiniFilter>(
                TestExpandingTableLimits.OnePayload(
                    StructureId.InfiniFilter, InfiniFilter.MaxQuotientBits, 4, includeWords: false)));
            Assert.IsInstanceOfType<ReadBudgetExceededException>(atTheLimit.InnerException);

            var filter = new InfiniFilter(1, fingerprintBits: 2);
            for (var i = 0; i < 1 << 16; i++) filter.Add(Key(i));
            AssertChainGrowsByFours(filter.Segments.Count, 1 << 16);
            AssertRoundTrips(filter);
        }

        /// <summary>The range filter, which shares the substrate and the limit.</summary>
        [TestMethod]
        public void TestMementoFilterLimitIsShared()
        {
            Assert.AreEqual(typeof(uint),
                typeof(MementoSegment).GetProperty(nameof(MementoSegment.Slots), BindingFlags.Instance | BindingFlags.NonPublic)!.PropertyType);
            Assert.IsLessThanOrEqualTo((ulong)uint.MaxValue, 1UL << MementoFilter.MaxQuotientBits);
            Assert.IsGreaterThan((ulong)uint.MaxValue, 1UL << (MementoFilter.MaxQuotientBits + 1));

            Assert.AreEqual(MementoFilter.MaxQuotientBits, MementoFilter.InitialQuotientBits(uint.MaxValue));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MementoFilter(256, 1));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MementoFilter(256, 33));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MementoFilter((1UL << 32) + 1));

            AssertRefused<MementoFilter>(
                TestExpandingTableLimits.OnePayload(
                    StructureId.MementoFilter, MementoFilter.MaxQuotientBits + 1, 4, includeWords: true),
                "between 2 and 31");
            var atTheLimit = Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<MementoFilter>(
                TestExpandingTableLimits.OnePayload(
                    StructureId.MementoFilter, MementoFilter.MaxQuotientBits, 4, includeWords: false)));
            Assert.IsInstanceOfType<ReadBudgetExceededException>(atTheLimit.InnerException);

            var filter = new MementoFilter(2, fingerprintBits: 2, initialCapacity: 1);
            for (var i = 0; i < 1 << 16; i++) filter.Add((ulong)i * 7919);
            AssertChainGrowsByFours(filter.Segments.Count, 1 << 16);
            AssertRoundTrips(filter);
        }

        /// <summary>
        /// The chain of tables is at most one longer than log4 of the items: measured
        /// at the fastest growth, sixteen thousand items gave five tables and four
        /// million gave eight. Sixty-four would need 4^63 items.
        /// </summary>
        private static void AssertChainGrowsByFours(int tables, int items)
        {
            Assert.IsGreaterThan(1, tables, "the chain never grew, so this measured nothing");
            Assert.IsLessThanOrEqualTo(Math.Log(items, 4) + 1, (double)tables);
        }

        /// <summary>
        /// Exempt: the value width is bounded to the same range by the builder and the
        /// reader, and nothing else in the reader is capped. The widest values read
        /// back.
        /// </summary>
        [TestMethod]
        public void TestBloomierFilterCannotReachItsReaderCaps()
        {
            var pairs = Enumerable.Range(0, 100)
                .Select(i => new System.Collections.Generic.KeyValuePair<byte[], ulong>(Key(i), (ulong)i))
                .ToArray();
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BloomierFilter.Build(pairs, 0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BloomierFilter.Build(pairs, 41));
            AssertRoundTrips(BloomierFilter.Build(pairs, 40));
        }

        /// <summary>
        /// Exempt: UltraLogLog's precision is bounded by the same pair of constants on
        /// both sides, and its registers are a run the payload holds. A precision whose
        /// registers pass the old ceiling reads back.
        /// </summary>
        [TestMethod]
        public void TestUltraLogLogCannotReachItsReaderCaps()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new UltraLogLog(UltraLogLog.MaxPrecision + 1));

            var wide = new UltraLogLog(20);
            for (var i = 0; i < 1000; i++) wide.Add(Key(i));
            AssertRoundTrips(wide);
        }

        /// <summary>
        /// Exempt: a SimHash signature is one 64-bit value, with no size anywhere in it.
        /// </summary>
        [TestMethod]
        public void TestSimHashSignatureHasNoSize()
        {
            Assert.HasCount(PayloadStart + sizeof(ulong) + sizeof(uint),
                new SimHashSignature(42).ToByteArray());
        }

        // Exempt, each: the reader caps no size at all -- every count it reads is the
        // length of data that follows it, or is checked against the structure's own
        // shape. Each is built past the old ceiling in its largest dimension and read
        // back, which is what would fail if a cap were added in the old style.

        /// <summary>About 9.6 million bits.</summary>
        [TestMethod]
        public void TestBloomFilterReadsPastTheOldCeiling()
        {
            var filter = new BloomFilter(OldCap, 0.01);
            for (var i = 0; i < 100; i++) filter.Add(Key(i));
            Assert.IsGreaterThan(OldCap, filter.Capacity());
            AssertRoundTrips(filter);
        }

        /// <summary>About 9.6 million four-bit buckets.</summary>
        [TestMethod]
        public void TestCountingBloomFilterReadsPastTheOldCeiling()
        {
            var filter = new CountingBloomFilter(OldCap, 4, 0.01);
            for (var i = 0; i < 100; i++) filter.Add(Key(i));
            Assert.IsGreaterThan(OldCap, filter.Capacity());
            AssertRoundTrips(filter);
        }

        /// <summary>About 9.6 million bits.</summary>
        [TestMethod]
        public void TestDeletableBloomFilterReadsPastTheOldCeiling()
        {
            var filter = new DeletableBloomFilter(OldCap, 1000, 0.01);
            for (var i = 0; i < 100; i++) filter.Add(Key(i));
            Assert.IsGreaterThan(OldCap, filter.Capacity());
            AssertRoundTrips(filter);
        }

        /// <summary>A million and one eight-bit cells.</summary>
        [TestMethod]
        public void TestStableBloomFilterReadsPastTheOldCeiling()
        {
            var filter = new StableBloomFilter(OldCap + 1, 8, 0.01, seed: 1);
            for (var i = 0; i < 100; i++) filter.Add(Key(i));
            Assert.IsGreaterThan(OldCap, filter.Cells());
            AssertRoundTrips(filter);
        }

        /// <summary>A million and one slots.</summary>
        [TestMethod]
        public void TestInverseBloomFilterReadsPastTheOldCeiling()
        {
            var filter = new InverseBloomFilter(OldCap + 1);
            for (var i = 0; i < 100; i++) filter.Add(Key(i));
            Assert.IsGreaterThan(OldCap, filter.Capacity());
            AssertRoundTrips(filter);
        }

        /// <summary>A million and one counters in a row.</summary>
        [TestMethod]
        public void TestCountMinSketchReadsPastTheOldCeiling()
        {
            var sketch = new CountMinSketch(Math.E / (OldCap + 1), 0.5);
            for (var i = 0; i < 100; i++) sketch.Add(Key(i));
            Assert.IsGreaterThan(OldCap, sketch.Width);
            AssertRoundTrips(sketch);
        }

        /// <summary>A little over a million counters in a row.</summary>
        [TestMethod]
        public void TestCountSketchReadsPastTheOldCeiling()
        {
            var sketch = new CountSketch(0.000999, 0.5);
            for (var i = 0; i < 100; i++) sketch.Add(Key(i));
            Assert.IsGreaterThan(OldCap, sketch.Width());
            AssertRoundTrips(sketch);
        }

        /// <summary>A million and one keys, so more than a million fingerprints.</summary>
        [TestMethod]
        public void TestBinaryFuseFilterReadsPastTheOldCeiling()
        {
            var filter = BinaryFuseFilter.Build(Enumerable.Range(0, (int)OldCap + 1).Select(i => Key(i)));
            Assert.IsGreaterThan(OldCap, filter.ArrayLengthChosen);
            AssertRoundTrips(filter);
        }

        /// <summary>
        /// Two values a hundred orders of magnitude apart at a fine accuracy span more
        /// than a million buckets.
        /// </summary>
        [TestMethod]
        public void TestDDSketchReadsPastTheOldCeiling()
        {
            var sketch = new DDSketch(0.0001);
            sketch.Add(1.0);
            sketch.Add(1e100);
            Assert.IsGreaterThan((long)OldCap, (long)sketch.BucketsAllocated());
            AssertRoundTrips(sketch);
        }

        /// <summary>More than a million cells.</summary>
        [TestMethod]
        public void TestInvertibleBloomLookupTableReadsPastTheOldCeiling()
        {
            var table = new InvertibleBloomLookupTable(OldCap, 8);
            for (var i = 0; i < 100; i++) table.Add(Key(i));
            Assert.IsGreaterThan(OldCap, table.Cells());
            AssertRoundTrips(table);
        }

        /// <summary>A million and one counters in a row.</summary>
        [TestMethod]
        public void TestPrivateCountMinSketchReadsPastTheOldCeiling()
        {
            var sketch = new PrivateCountMinSketch(OldCap + 1, 1, 1.0, seed: 1);
            for (var i = 0; i < 100; i++) sketch.Add(Key(i));
            AssertRoundTrips(sketch);
        }

        /// <summary>Its sketches a million and one counters wide.</summary>
        [TestMethod]
        public void TestDpswSketchReadsPastTheOldCeiling()
        {
            var sketch = new DpswSketch(100, 1.0, width: OldCap + 1, depth: 1, seed: 1);
            for (var i = 0; i < 10; i++) sketch.Add(Key(i));
            AssertRoundTrips(sketch);
        }

        /// <summary>A million and one registers would not be a power of two; 2^20 is.</summary>
        [TestMethod]
        public void TestHyperLogLogReadsPastTheOldCeiling()
        {
            var estimator = new HyperLogLog(1u << 20);
            for (var i = 0; i < 100; i++) estimator.Add(Key(i));
            Assert.IsGreaterThan(OldCap, 1u << 20);
            AssertRoundTrips(estimator);
        }

        // ---- The roster -------------------------------------------------------------

        /// <summary>
        /// Every structure either declares the limits its constructor and reader share
        /// or is exempt, with the reason and the test that checks the reason.
        /// </summary>
        /// <remarks>
        /// The roster is <see cref="StructureId"/>, which a structure cannot leave itself
        /// off of, so a new one fails here until it is placed. A declared structure names
        /// two kinds of test: one building it past where its reader used to stop, and one
        /// holding its constructor and reader to one limit. An exemption names the test
        /// that checks its reason, so that when the reason stops being true -- a constructor
        /// learns to build past a cap, say -- the exemption fails as loudly as a gap would.
        /// Each named test has to exist and be a test, so an entry pointing at something
        /// renamed fails rather than silently vouching for nothing.
        /// </remarks>
        [TestMethod]
        public void TestEveryStructureLoadsWhatItsConstructorBuilds()
        {
            var declared = Declared;

            const string NoCap =
                "its reader caps no size: every count it reads is the length of data " +
                "that follows, or is checked against the structure's own shape";

            var exempt = new (StructureId Id, string Why, string VerifiedBy)[]
            {
                (StructureId.HyperLogLogPlus,
                    "its precision is bounded by one pair of constants on both sides, and " +
                    "its sparse form converts to dense long before the reader's cap",
                    nameof(TestHyperLogLogPlusCannotReachItsReaderCaps)),
                (StructureId.PartitionedBloomFilter,
                    "its partition count is ceil(log2(1 / rate)), which no rate with a " +
                    "finite reciprocal brings near the reader's cap",
                    nameof(TestPartitionedFilterCannotReachItsPartitionCap)),
                (StructureId.BloomFilter64,
                    "its arrays hold 2^30 buckets each, so the reader's cap on their number " +
                    "is reached only by a filter no payload can hold",
                    nameof(TestBloomFilter64CannotReachItsArrayCap)),
                (StructureId.BloomierFilter,
                    "its value width is bounded to the same range by the builder and the " +
                    "reader, and nothing else is capped",
                    nameof(TestBloomierFilterCannotReachItsReaderCaps)),
                (StructureId.UltraLogLog,
                    "its precision is bounded by one pair of constants on both sides",
                    nameof(TestUltraLogLogCannotReachItsReaderCaps)),
                (StructureId.SimHashSignature,
                    "it is one 64-bit value and has no size",
                    nameof(TestSimHashSignatureHasNoSize)),
                (StructureId.BloomFilter, NoCap, nameof(TestBloomFilterReadsPastTheOldCeiling)),
                (StructureId.DeletableBloomFilter, NoCap,
                    nameof(TestDeletableBloomFilterReadsPastTheOldCeiling)),
                (StructureId.CountSketch, NoCap, nameof(TestCountSketchReadsPastTheOldCeiling)),
                (StructureId.BinaryFuseFilter, NoCap,
                    nameof(TestBinaryFuseFilterReadsPastTheOldCeiling)),
                (StructureId.DDSketch, NoCap, nameof(TestDDSketchReadsPastTheOldCeiling)),
                (StructureId.InvertibleBloomLookupTable, NoCap,
                    nameof(TestInvertibleBloomLookupTableReadsPastTheOldCeiling)),
                (StructureId.HyperLogLog, NoCap, nameof(TestHyperLogLogReadsPastTheOldCeiling)),
            };

            var named = declared
                .SelectMany(d => d.BuiltLarge.Append(d.SharedLimit))
                .Concat(exempt.Select(e => e.VerifiedBy));

            foreach (var test in named)
            {
                var method = typeof(TestLoadWhatItBuilds).GetMethod(test);
                Assert.IsNotNull(method, $"the roster names {test}, which is not a test here");
                Assert.IsNotEmpty(
                    method.GetCustomAttributes(typeof(TestMethodAttribute), false),
                    $"the roster names {test}, which is not a test method");
            }

            foreach (var (id, builtLarge, _) in declared)
            {
                Assert.IsNotEmpty(builtLarge,
                    $"{id} declares a limit and names nothing built large and read back");
            }

            StructureRoster.AssertCoversEveryStructure(
                "load what it builds",
                declared.Select(d => d.Id),
                exempt.Select(e => (e.Id, e.Why)).ToArray());
        }

        /// <summary>
        /// The structures with a size limit their constructor and reader share, each with
        /// the tests that build it large and read it back -- past where its reader used
        /// to stop, or for the quotient filter, whose reader was right, at the widest its
        /// constructor now builds -- and the test that holds both sides to the limit.
        /// </summary>
        private static readonly (StructureId Id, string[] BuiltLarge, string SharedLimit)[]
            Declared =
            {
                (StructureId.CuckooBloomFilter,
                    new[] { nameof(TestCuckooFilterPastTheOldBucketCeilingRoundTrips) },
                    nameof(TestCuckooFilterLimitIsShared)),
                (StructureId.ThetaSketch,
                    new[] { nameof(TestThetaSketchPastTheOldCeilingRoundTrips) },
                    nameof(TestThetaSketchLimitIsShared)),
                (StructureId.TupleSketch,
                    new[] { nameof(TestTupleSketchPastTheOldCeilingRoundTrips) },
                    nameof(TestTupleSketchLimitIsShared)),
                (StructureId.MinHashSignature,
                    new[] { nameof(TestMinHashSignaturePastTheOldCeilingRoundTrips) },
                    nameof(TestMinHashSignatureLimitIsShared)),
                (StructureId.TopK,
                    new[] { nameof(TestTopKPastTheOldCeilingRoundTrips) },
                    nameof(TestTopKLimitIsShared)),
                (StructureId.VarOpt,
                    new[] { nameof(TestVarOptPastTheOldCeilingRoundTrips) },
                    nameof(TestVarOptLimitIsShared)),
                (StructureId.HeavyKeeper,
                    new[]
                    {
                        nameof(TestHeavyKeeperPastTheOldTrackedCeilingRoundTrips),
                        nameof(TestHeavyKeeperPastTheOldBucketCeilingRoundTrips),
                    },
                    nameof(TestHeavyKeeperLimitsAreShared)),
                (StructureId.Grafite,
                    new[]
                    {
                        nameof(TestGrafiteReaderTakesLowWordsPastTheOldCeiling),
                        nameof(TestGrafiteReaderTakesHighWordsPastTheOldCeiling),
                    },
                    nameof(TestGrafiteLimitsAreShared)),
                (StructureId.SetSketch,
                    new[] { nameof(TestSetSketchReaderTakesARegisterCountPastTheOldCeiling) },
                    nameof(TestSetSketchLimitIsShared)),
                (StructureId.SublimeCountMinSketch,
                    new[] { nameof(TestSublimeCountMinSketchPastTheOldRowCeilingRoundTrips) },
                    nameof(TestSublimeCountMinSketchLimitIsShared)),
                (StructureId.CountMinSketch,
                    new[] { nameof(TestCountMinSketchReadsPastTheOldCeiling) },
                    nameof(TestCountMinSketchLimitsAreShared)),
                (StructureId.PrivateCountMinSketch,
                    new[] { nameof(TestPrivateCountMinSketchReadsPastTheOldCeiling) },
                    nameof(TestPrivateCountMinSketchLimitsAreShared)),
                (StructureId.InverseBloomFilter,
                    new[] { nameof(TestInverseBloomFilterReadsPastTheOldCeiling) },
                    nameof(TestInverseBloomFilterLimitIsShared)),
                (StructureId.DpswSketch,
                    new[] { nameof(TestDpswSketchReadsPastTheOldCeiling) },
                    nameof(TestDpswSketchLimitIsShared)),
                (StructureId.ScalableBloomFilter,
                    new[] { nameof(TestScalableFilterReaderTakesAFilterCountPastTheOldCeiling) },
                    nameof(TestScalableFilterLimitIsShared)),
                (StructureId.InfiniFilter,
                    new[] { nameof(TestInfiniFilterAtAWideTableRoundTrips) },
                    nameof(TestInfiniFilterLimitIsShared)),
                (StructureId.MementoFilter,
                    new[] { nameof(TestMementoFilterAtAWideTableRoundTrips) },
                    nameof(TestMementoFilterLimitIsShared)),
                (StructureId.CountingBloomFilter,
                    new[] { nameof(TestCountingBloomFilterReadsPastTheOldCeiling) },
                    nameof(TestCountingFilterLimitIsShared)),
                (StructureId.StableBloomFilter,
                    new[] { nameof(TestStableBloomFilterReadsPastTheOldCeiling) },
                    nameof(TestStableFilterLimitIsShared)),
                (StructureId.QuotientFilter,
                    new[] { nameof(TestQuotientFilterAtItsWidestSplitRoundTrips) },
                    nameof(TestQuotientFilterLimitIsShared)),
            };

        /// <summary>
        /// FORMAT.md's table of sizes states every limit as a number, for someone
        /// decoding a payload without this library. A number in a document has no build
        /// to fail, so it is held here to the symbol the code shares, in both
        /// directions: every row is a limit the code has, at the value the code has, and
        /// every structure that declares a limit has a row.
        /// </summary>
        [TestMethod]
        public void TestFormatDocumentsEveryLimit()
        {
            var expected = new Dictionary<(string Structure, string Size), ulong>
            {
                [("CuckooBloomFilter", "entries, `m` × `b`")] = CuckooBloomFilter.MaxEntries,
                [("CountingBloomFilter", "bytes of counters, `m` × `b` bits, rounded up")] =
                    (ulong)Buckets.MaxDataBytes,
                [("StableBloomFilter", "bytes of cells, `m` × `d` bits, rounded up")] =
                    (ulong)Buckets.MaxDataBytes,
                [("ThetaSketch", "`k`")] = ThetaSketch.MaxNominalEntries,
                [("TupleSketch", "`k`")] = TupleSketch.MaxNominalEntries,
                [("MinHashSignature", "`k`")] = (ulong)MinHashSignature.MaxValues,
                [("TopK", "`k`")] = ElementHeap.MaxCapacity,
                [("HeavyKeeper", "`k`")] = ElementHeap.MaxCapacity,
                [("HeavyKeeper", "width")] = HeavyKeeper.MaxWidth,
                [("HeavyKeeper", "depth")] = HeavyKeeper.MaxDepth,
                [("CountMinSketch", "width")] = CountMinSketch.MaxWidth,
                [("CountMinSketch", "depth")] = CountMinSketch.MaxDepth,
                [("PrivateCountMinSketch", "width")] = PrivateCountMinSketch.MaxWidth,
                [("PrivateCountMinSketch", "depth")] = PrivateCountMinSketch.MaxDepth,
                [("InverseBloomFilter", "capacity")] = InverseBloomFilter.MaxCapacity,
                [("DpswSketch", "items in a substream")] = (ulong)DpswSketch.MaxSubstreamSize,
                [("DpswSketch", "width")] = PrivateCountMinSketch.MaxWidth,
                [("DpswSketch", "depth")] = PrivateCountMinSketch.MaxDepth,
                [("VarOpt", "`k`")] = VarOpt.MaxK,
                [("Grafite", "reduced universe `r`")] = Grafite.MaxUniverse,
                [("Grafite", "words of low bits")] = Grafite.MaxLowWords,
                [("Grafite", "words of high bits")] = Grafite.MaxHighWords,
                [("Grafite", "low bits of a code")] = (ulong)Grafite.MaxLowBits,
                [("SetSketch", "`m`")] = (ulong)SetSketch.MaxRegisters,
                [("SublimeCountMinSketch", "rows")] = (ulong)SublimeCountMinSketch.MaxDepth,
                [("SublimeCountMinSketch", "counters in a row")] = (ulong)SublimeCountMinSketch.MaxWidth,
                [("ScalableBloomFilter", "filter count")] = (ulong)ScalableBloomFilter.MaxFilters,
                [("InfiniFilter", "quotient bits")] = (ulong)InfiniFilter.MaxQuotientBits,
                [("MementoFilter", "quotient bits")] = (ulong)MementoFilter.MaxQuotientBits,
                [("MementoFilter", "bits of a slot")] = (ulong)MementoSegment.MaxSlotBits,
                [("QuotientFilter", "quotient bits")] = QuotientFilter.MaxQuotientBits,
                [("QuotientFilter", "quotient + remainder bits")] = QuotientFilter.HashBits,
                [("QuotientFilter", "remainder bits")] = QuotientFilter.MaxRemainderBits,
            };

            using var resource = typeof(TestLoadWhatItBuilds).Assembly
                .GetManifestResourceStream("TestProbabilisticDataStructures.FORMAT.md");
            Assert.IsNotNull(resource, "FORMAT.md is not embedded in the test assembly");
            var specification = new StreamReader(resource).ReadToEnd();

            var documented = Regex.Matches(specification,
                    @"^\| `(?<structure>\w+)` \| (?<size>[^|]+?) \| (?<limit>[0-9,]+) \|",
                    RegexOptions.Multiline)
                .ToDictionary(
                    m => (m.Groups["structure"].Value, m.Groups["size"].Value),
                    m => ulong.Parse(m.Groups["limit"].Value.Replace(",", "")));

            Assert.IsNotEmpty(documented,
                "no rows of the sizes table were found in FORMAT.md, so nothing was checked");

            foreach (var (row, stated) in documented)
            {
                Assert.IsTrue(expected.TryGetValue(row, out var inCode),
                    $"FORMAT.md states a limit for {row.Item1}'s {row.Item2}, which the " +
                    "code does not have, or which is named differently here");
                Assert.AreEqual(inCode, stated,
                    $"FORMAT.md states {row.Item1}'s {row.Item2} as {stated:N0}, and the " +
                    $"code's limit is {inCode:N0}");
            }

            foreach (var row in expected.Keys)
            {
                Assert.IsTrue(documented.ContainsKey(row),
                    $"FORMAT.md's sizes table has no row for {row.Item1}'s {row.Item2}");
            }

            CollectionAssert.AreEquivalent(
                Declared.Select(d => d.Id.ToString()).ToArray(),
                expected.Keys.Select(k => k.Structure).Distinct().ToArray(),
                "the structures with a documented limit are not the ones that declare one");
        }
    }
}
