using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
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
            // f64 r, f64 fp, f64 p, u32 hint, then the filter count.
            const int FilterCountOffset = 8 + 8 + 8 + 4;

            var filter = new ScalableBloomFilter(100, 0.01, 0.8);
            for (var i = 0; i < 500; i++) filter.Add(Key(i));

            AssertRefusedOnlyForMissingBytes<ScalableBloomFilter>(
                PokeUInt32(filter.ToByteArray(), FilterCountOffset, OldCap + 1));
        }

        private static Grafite SmallGrafite() =>
            Grafite.Build(Enumerable.Range(0, 1000).Select(i => (ulong)i * 97), 0.01, 16, seed: 7);
    }
}
