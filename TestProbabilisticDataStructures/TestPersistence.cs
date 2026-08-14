using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Round-tripping is asserted by behavior rather than by comparing fields: a
    /// restored structure has to answer every query the way the original did. Equal
    /// fields are the means, not the promise, and a structure can have all of them
    /// equal and still answer differently if its hash is not the one it was built with.
    /// </summary>
    [TestClass]
    public class TestPersistence
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        private static readonly string[] Present =
            Enumerable.Range(0, 2000).Select(i => $"present-{i}").ToArray();

        private static readonly string[] Absent =
            Enumerable.Range(0, 2000).Select(i => $"absent-{i}").ToArray();

        [TestMethod]
        public void TestBloomFilterRoundTripsThroughAStream()
        {
            var original = new BloomFilter(2000, 0.01);
            foreach (var word in Present)
            {
                original.Add(Key(word));
            }

            using var stream = new MemoryStream();
            original.WriteTo(stream);
            stream.Position = 0;
            var restored = BloomFilter.ReadFrom(stream);

            Assert.AreEqual(original.Capacity(), restored.Capacity());
            Assert.AreEqual(original.K(), restored.K());
            Assert.AreEqual(original.Count(), restored.Count());

            // Every query, both the ones that are in and the ones that are not. The
            // false positives have to agree too: a filter that answers differently on
            // those is a different filter, even where the answer is allowed to be yes.
            foreach (var word in Present.Concat(Absent))
            {
                Assert.AreEqual(original.Test(Key(word)), restored.Test(Key(word)),
                    $"restored filter disagreed about {word}");
            }
        }

        [TestMethod]
        public void TestCountMinSketchRoundTripsThroughAStream()
        {
            var original = new CountMinSketch(0.001, 0.01);
            var rand = new Random(11);
            for (int i = 0; i < 20000; i++)
            {
                original.Add(Key($"k{rand.Next(500)}"));
            }

            using var stream = new MemoryStream();
            original.WriteTo(stream);
            stream.Position = 0;
            var restored = CountMinSketch.ReadFrom(stream);

            Assert.AreEqual(original.TotalCount(), restored.TotalCount());
            Assert.AreEqual(original.Epsilon(), restored.Epsilon());
            Assert.AreEqual(original.Delta(), restored.Delta());

            for (int i = 0; i < 500; i++)
            {
                Assert.AreEqual(original.Count(Key($"k{i}")), restored.Count(Key($"k{i}")),
                    $"restored sketch disagreed about the count of k{i}");
            }

            // A restored sketch has to keep working, not merely read back. Merging it
            // with one of the same shape exercises the dimensions it was restored with.
            restored.Merge(new CountMinSketch(0.001, 0.01));
            Assert.AreEqual(original.TotalCount(), restored.TotalCount());
        }

        [TestMethod]
        public void TestRoundTripThroughByteArray()
        {
            var original = new BloomFilter(500, 0.01);
            original.Add(Key("a"));

            var restored = Persistence.FromByteArray<BloomFilter>(original.ToByteArray());

            Assert.IsTrue(restored.Test(Key("a")));
            Assert.AreEqual(original.Capacity(), restored.Capacity());
        }

        /// <summary>
        /// An empty structure is a shape the payload has to handle as readily as a full
        /// one, and is the one most likely to be written by accident.
        /// </summary>
        [TestMethod]
        public void TestEmptyStructuresRoundTrip()
        {
            var bloom = Persistence.FromByteArray<BloomFilter>(
                new BloomFilter(1000, 0.01).ToByteArray());
            Assert.AreEqual(0u, bloom.Count());
            Assert.IsFalse(bloom.Test(Key("anything")));

            var sketch = Persistence.FromByteArray<CountMinSketch>(
                new CountMinSketch(0.01, 0.01).ToByteArray());
            Assert.AreEqual(0ul, sketch.TotalCount());
            Assert.AreEqual(0ul, sketch.Count(Key("anything")));
        }

        /// <summary>
        /// A structure's answers depend entirely on its hash, and a delegate cannot be
        /// written down. Reading one that used a custom hash without supplying it would
        /// not fail on its own: the structure would answer no to everything and read as
        /// empty rather than as wrong. It is refused instead.
        /// </summary>
        [TestMethod]
        public void TestCustomHashMustBeSuppliedWhenReading()
        {
            Func<ReadOnlySpan<byte>, ulong> custom = data => (ulong)data.Length * 2654435761UL;

            var original = new BloomFilter(1000, 0.01);
            original.SetHash(custom);
            original.Add(Key("a"));

            var bytes = original.ToByteArray();

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<BloomFilter>(bytes));
            StringAssert.Contains(ex.Message, "SetHash");

            var restored = Persistence.FromByteArray<BloomFilter>(bytes, custom);
            Assert.IsTrue(restored.Test(Key("a")));
        }

        /// <summary>
        /// The default hash is named in the payload, so reading needs no ceremony. It is
        /// named by algorithm rather than as "the default", because the default is not
        /// fixed for all time: this library's was MD5 until 3.0.0.
        /// </summary>
        [TestMethod]
        public void TestDefaultHashNeedsNoArgumentWhenReading()
        {
            var original = new BloomFilter(1000, 0.01);
            original.Add(Key("a"));

            var restored = Persistence.FromByteArray<BloomFilter>(original.ToByteArray());
            Assert.IsTrue(restored.Test(Key("a")));
        }

        /// <summary>
        /// Setting the default back explicitly is still the default, and a filter that
        /// has been through SetHash to get there should not be treated as carrying a
        /// hash that cannot be named.
        /// </summary>
        [TestMethod]
        public void TestReinstatedDefaultHashIsStillRecognised()
        {
            var original = new BloomFilter(1000, 0.01);
            original.SetHash(Defaults.GetDefaultHashFunction());
            original.Add(Key("a"));

            var restored = Persistence.FromByteArray<BloomFilter>(original.ToByteArray());
            Assert.IsTrue(restored.Test(Key("a")));
        }

        [TestMethod]
        public void TestReadingAStructureAsTheWrongTypeIsRefused()
        {
            var bytes = new BloomFilter(1000, 0.01).ToByteArray();

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<CountMinSketch>(bytes));
            StringAssert.Contains(ex.Message, "BloomFilter");
        }

        [TestMethod]
        public void TestReadingSomethingElseEntirelyIsRefused()
        {
            var notAPayload = Encoding.ASCII.GetBytes("this is not a filter, it is a sentence");

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<BloomFilter>(notAPayload));
            StringAssert.Contains(ex.Message, "marker");
        }

        /// <summary>
        /// The checksum covers the header as well as the payload, so a corrupted length
        /// or structure id is caught by the same check rather than being acted on first.
        /// </summary>
        [TestMethod]
        public void TestCorruptionIsCaught()
        {
            var original = new BloomFilter(1000, 0.01);
            foreach (var word in Present.Take(200))
            {
                original.Add(Key(word));
            }

            var clean = original.ToByteArray();

            // Every byte after the magic, one at a time, is enough to fail on. Flipping
            // a bit rather than replacing the byte, so the value always changes.
            for (int i = 4; i < clean.Length; i++)
            {
                var corrupted = (byte[])clean.Clone();
                corrupted[i] ^= 0x01;

                Assert.ThrowsExactly<InvalidDataException>(
                    () => Persistence.FromByteArray<BloomFilter>(corrupted),
                    $"a flipped bit at offset {i} was not caught");
            }
        }

        [TestMethod]
        public void TestTruncationIsCaught()
        {
            var clean = new BloomFilter(1000, 0.01).ToByteArray();

            foreach (var length in new[] { 0, 1, 13, 14, clean.Length / 2, clean.Length - 1 })
            {
                Assert.ThrowsExactly<InvalidDataException>(
                    () => Persistence.FromByteArray<BloomFilter>(clean.Take(length).ToArray()),
                    $"a payload truncated to {length} bytes was not caught");
            }
        }

        /// <summary>
        /// A payload from a later version may mean its bytes differently, so it is
        /// refused rather than read under this version's assumptions.
        /// </summary>
        [TestMethod]
        public void TestALaterFormatVersionIsRefused()
        {
            var bytes = new BloomFilter(1000, 0.01).ToByteArray();
            bytes[4] = 99;   // format version, little-endian at offset 4

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<BloomFilter>(FixChecksum(bytes)));
            StringAssert.Contains(ex.Message, "version 99");
        }

        /// <summary>
        /// As above for the hash: an identifier this version does not know names a hash
        /// it cannot install, and installing the wrong one answers no to everything.
        /// </summary>
        [TestMethod]
        public void TestAnUnknownHashIdIsRefused()
        {
            var bytes = new BloomFilter(1000, 0.01).ToByteArray();
            bytes[8] = 99;   // hash id, little-endian at offset 8

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<BloomFilter>(FixChecksum(bytes)));
            StringAssert.Contains(ex.Message, "does not know");
        }

        /// <summary>
        /// Payloads written by the version that introduced the format, checked in
        /// byte-for-byte. The format is documented as stable, and this is what makes
        /// that a promise rather than an intention: a change that stops old data being
        /// readable fails here, rather than in somebody's stored data.
        /// <para>
        /// If this test fails, the fix is not to regenerate the fixture. It is either to
        /// keep reading the old layout or to raise the format version and read both.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestPayloadsWrittenByTheIntroducingVersionStillRead()
        {
            var bloom = BloomFilter.ReadFrom(Fixture("bloomfilter-v1.bin"));

            Assert.AreEqual(959u, bloom.Capacity());
            Assert.AreEqual(7u, bloom.K());
            Assert.AreEqual(5u, bloom.Count());

            foreach (var word in new[] { "alpha", "beta", "gamma", "delta", "epsilon" })
            {
                Assert.IsTrue(bloom.Test(Key(word)),
                    $"the stored filter no longer finds {word}");
            }

            Assert.IsFalse(bloom.Test(Key("zeta")));

            var sketch = CountMinSketch.ReadFrom(Fixture("countminsketch-v1.bin"));

            Assert.AreEqual(55ul, sketch.TotalCount());
            Assert.AreEqual(0.01, sketch.Epsilon());
            Assert.AreEqual(0.01, sketch.Delta());

            // item{i} was added i + 1 times.
            for (int i = 0; i < 10; i++)
            {
                Assert.AreEqual((ulong)(i + 1), sketch.Count(Key($"item{i}")),
                    $"the stored sketch no longer counts item{i} correctly");
            }
        }

        /// <summary>
        /// And that what this library writes today is still what it wrote then. The test
        /// above would keep passing if writing changed but reading kept up with it;
        /// this pins the bytes themselves.
        /// </summary>
        [TestMethod]
        public void TestWritingStillProducesTheStoredBytes()
        {
            var bloom = new BloomFilter(100, 0.01);
            foreach (var word in new[] { "alpha", "beta", "gamma", "delta", "epsilon" })
            {
                bloom.Add(Key(word));
            }

            CollectionAssert.AreEqual(ReadFixture("bloomfilter-v1.bin"), bloom.ToByteArray(),
                "the bytes written for this filter have changed since the format was introduced");

            var sketch = new CountMinSketch(0.01, 0.01);
            for (int i = 0; i < 10; i++)
            {
                for (int j = 0; j <= i; j++)
                {
                    sketch.Add(Key($"item{i}"));
                }
            }

            CollectionAssert.AreEqual(ReadFixture("countminsketch-v1.bin"), sketch.ToByteArray(),
                "the bytes written for this sketch have changed since the format was introduced");
        }

        private static Stream Fixture(string name)
        {
            return new MemoryStream(ReadFixture(name), writable: false);
        }

        private static byte[] ReadFixture(string name)
        {
            using var resource = typeof(TestPersistence).Assembly
                .GetManifestResourceStream($"TestProbabilisticDataStructures.fixtures.{name}")
                ?? throw new InvalidOperationException($"fixture {name} is not embedded");

            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            return buffer.ToArray();
        }

        /// <summary>
        /// Recomputes the trailing CRC so that a deliberate edit to the header is tested
        /// on its own terms rather than being caught as corruption first.
        /// </summary>
        private static byte[] FixChecksum(byte[] frame)
        {
            var crc = System.IO.Hashing.Crc32.HashToUInt32(
                frame.AsSpan(4, frame.Length - 8));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                frame.AsSpan(frame.Length - 4), crc);
            return frame;
        }
    }
}
