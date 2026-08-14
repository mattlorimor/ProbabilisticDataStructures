using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Payloads written by the version that introduced each structure's layout, checked
    /// in byte-for-byte. The format is documented as stable, and this is what makes that
    /// a promise rather than an intention: a change that stops old data being readable
    /// fails here rather than in somebody's storage.
    /// <para>
    /// If one of these fails, the fix is not to regenerate the fixture. It is either to
    /// keep reading the old layout or to raise the format version and read both.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestPersistenceFormatStability
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        private static readonly string[] Words =
            { "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa" };

        [TestMethod]
        public void TestStoredBloomFilter64StillReads()
        {
            var f = BloomFilter64.ReadFrom(Fixture("bloomfilter64-v1.bin"));

            Assert.AreEqual(959ul, f.Capacity());
            Assert.AreEqual(7u, f.K());
            Assert.AreEqual(10ul, f.Count());
            AssertAllPresent(f.Test);
        }

        [TestMethod]
        public void TestStoredCountingBloomFilterStillReads()
        {
            var f = CountingBloomFilter.ReadFrom(Fixture("countingbloomfilter-v1.bin"));

            Assert.AreEqual(959u, f.Capacity());
            Assert.AreEqual(7u, f.K());
            Assert.AreEqual(10u, f.Count());
            AssertAllPresent(f.Test);

            // The counters came back, not just the occupancy: each word removes once
            // and is gone.
            Assert.IsTrue(f.TestAndRemove(Key("alpha")));
            Assert.IsFalse(f.Test(Key("alpha")));
        }

        [TestMethod]
        public void TestStoredDeletableBloomFilterStillReads()
        {
            var f = DeletableBloomFilter.ReadFrom(Fixture("deletablebloomfilter-v1.bin"));

            Assert.AreEqual(949u, f.Capacity());
            Assert.AreEqual(7u, f.K());
            Assert.AreEqual(10u, f.Count());
            AssertAllPresent(f.Test);

            // Region size is recomputed on read rather than stored, so this also
            // exercises that the recomputation agrees with what wrote the payload.
            Assert.IsTrue(f.TestAndRemove(Key("alpha")));
        }

        [TestMethod]
        public void TestStoredPartitionedBloomFilterStillReads()
        {
            var f = PartitionedBloomFilter.ReadFrom(Fixture("partitionedbloomfilter-v1.bin"));

            Assert.AreEqual(959u, f.Capacity());
            Assert.AreEqual(7u, f.K());
            Assert.AreEqual(10u, f.Count());
            AssertAllPresent(f.Test);
        }

        [TestMethod]
        public void TestStoredScalableBloomFilterStillReads()
        {
            var f = ScalableBloomFilter.ReadFrom(Fixture("scalablebloomfilter-v1.bin"));

            Assert.AreEqual(1053u, f.Capacity());
            Assert.AreEqual(7u, f.K());

            for (int i = 0; i < 100; i++)
            {
                Assert.IsTrue(f.Test(Key($"s{i}")), $"the stored filter no longer finds s{i}");
            }

            // The contained filters came back in order with their fill ratios intact,
            // so it still grows rather than stopping where it was written.
            for (int i = 100; i < 400; i++)
            {
                f.Add(Key($"s{i}"));
            }

            Assert.IsGreaterThan(1053u, f.Capacity(), "the restored filter did not grow");
        }

        [TestMethod]
        public void TestStoredStableBloomFilterStillReads()
        {
            var f = StableBloomFilter.ReadFrom(Fixture("stablebloomfilter-v1.bin"));

            Assert.AreEqual(1000u, f.Cells());
            Assert.AreEqual(3u, f.K());
            Assert.AreEqual(35u, f.P());
            AssertAllPresent(f.Test);
        }

        [TestMethod]
        public void TestStoredInverseBloomFilterStillReads()
        {
            var f = InverseBloomFilter.ReadFrom(Fixture("inversebloomfilter-v1.bin"));

            Assert.AreEqual(64u, f.Capacity());

            // Nine of the ten survived being written; the tenth was displaced by a
            // later word before the filter was ever stored, which is what this filter
            // does. Pinning the exact number keeps a restore that quietly loses one
            // more from passing.
            Assert.AreEqual(9, Words.Count(w => f.Test(Key(w))));
        }

        [TestMethod]
        public void TestStoredCuckooBloomFilterStillReads()
        {
            var f = CuckooBloomFilter.ReadFrom(Fixture("cuckoobloomfilter-v1.bin"));

            Assert.AreEqual(100u, f.Capacity());
            Assert.AreEqual(10u, f.Count());
            AssertAllPresent(f.Test);

            // Fingerprints landed where the relocation logic expects them, so it still
            // takes inserts.
            for (int i = 0; i < 50; i++)
            {
                f.Add(Key($"later{i}"));
            }
        }

        [TestMethod]
        public void TestStoredHyperLogLogStillReads()
        {
            var h = HyperLogLog.ReadFrom(Fixture("hyperloglog-v1.bin"));

            // b and alpha are derived on read rather than stored, so this pins that the
            // derivation still agrees with what wrote the registers.
            Assert.AreEqual(18ul, h.Count());
        }

        [TestMethod]
        public void TestStoredTopKStillReads()
        {
            var t = TopK.ReadFrom(Fixture("topk-v1.bin"));

            var elements = t.Elements()
                .Select(e => (Encoding.ASCII.GetString(e.Data.Span), e.Freq))
                .ToArray();

            CollectionAssert.AreEqual(
                new[] { ("t2", 3ul), ("t3", 4ul), ("t4", 5ul) }, elements);
        }

        [TestMethod]
        public void TestStoredMinHashSignatureStillReads()
        {
            var signature = MinHashSignature.ReadFrom(Fixture("minhashsignature-v1.bin"));

            Assert.AreEqual(16, signature.Length);
            Assert.AreEqual(31797598974978550ul, signature.Values[0]);
            Assert.AreEqual(661719492639586765ul, signature.Values[15]);

            // A stored signature has to still compare against one computed now, which
            // is the whole reason to store one.
            var recomputed = MinHash.Signature(
                new[] { "alpha", "beta", "gamma", "delta", "epsilon" }, 16);

            Assert.AreEqual(1.0f, MinHash.Similarity(signature, recomputed),
                "a signature stored by an earlier version no longer matches one " +
                "computed now for the same bag");
        }

        /// <summary>
        /// And that what this library writes today is still what it wrote then. The
        /// tests above would keep passing if writing changed and reading kept up with
        /// it; this pins the bytes themselves.
        /// <para>
        /// The stable and cuckoo filters are absent because neither is reproducible:
        /// one decrements randomly chosen cells and the other evicts a randomly chosen
        /// entry, from unseeded generators. Their payloads are still read above, which
        /// is the half of the promise that matters -- stored data keeps working.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestWritingStillProducesTheStoredBytes()
        {
            var bloom64 = new BloomFilter64(100, 0.01);
            var counting = new CountingBloomFilter(100, 4, 0.01);
            var deletable = new DeletableBloomFilter(100, 10, 0.01);
            var partitioned = new PartitionedBloomFilter(100, 0.01);
            var inverse = new InverseBloomFilter(64);

            foreach (var word in Words)
            {
                var key = Key(word);
                bloom64.Add(key);
                counting.Add(key);
                deletable.Add(key);
                partitioned.Add(key);
                inverse.Add(key);
            }

            AssertBytes("bloomfilter64-v1.bin", bloom64.ToByteArray());
            AssertBytes("countingbloomfilter-v1.bin", counting.ToByteArray());
            AssertBytes("deletablebloomfilter-v1.bin", deletable.ToByteArray());
            AssertBytes("partitionedbloomfilter-v1.bin", partitioned.ToByteArray());
            AssertBytes("inversebloomfilter-v1.bin", inverse.ToByteArray());

            var scalable = new ScalableBloomFilter(20, 0.01, 0.8);
            for (int i = 0; i < 100; i++)
            {
                scalable.Add(Key($"s{i}"));
            }

            AssertBytes("scalablebloomfilter-v1.bin", scalable.ToByteArray());

            var hll = new HyperLogLog(64);
            for (int i = 0; i < 20; i++)
            {
                hll.Add(Key($"h{i}"));
            }

            AssertBytes("hyperloglog-v1.bin", hll.ToByteArray());

            var topK = new TopK(0.1, 0.5, 3);
            for (int i = 0; i < 5; i++)
            {
                for (int j = 0; j <= i; j++)
                {
                    topK.Add(Key($"t{i}"));
                }
            }

            AssertBytes("topk-v1.bin", topK.ToByteArray());

            AssertBytes("minhashsignature-v1.bin", MinHash.Signature(
                new[] { "alpha", "beta", "gamma", "delta", "epsilon" }, 16).ToByteArray());
        }

        /// <summary>
        /// The structure id in every stored payload is the one the format assigns, which
        /// is what stops a payload being read as the wrong structure. Reading the byte
        /// directly rather than through the enum, so that renumbering the enum shows up
        /// here rather than passing quietly.
        /// </summary>
        [TestMethod]
        public void TestStoredStructureIdsAreUnchanged()
        {
            var expected = new (string Fixture, int Id)[]
            {
                ("bloomfilter-v1.bin", 1),
                ("bloomfilter64-v1.bin", 2),
                ("countingbloomfilter-v1.bin", 3),
                ("deletablebloomfilter-v1.bin", 4),
                ("partitionedbloomfilter-v1.bin", 5),
                ("scalablebloomfilter-v1.bin", 6),
                ("stablebloomfilter-v1.bin", 7),
                ("inversebloomfilter-v1.bin", 8),
                ("cuckoobloomfilter-v1.bin", 9),
                ("countminsketch-v1.bin", 10),
                ("hyperloglog-v1.bin", 11),
                ("topk-v1.bin", 12),
                ("minhashsignature-v1.bin", 13),
            };

            foreach (var (fixture, id) in expected)
            {
                var bytes = ReadFixture(fixture);
                Assert.AreEqual(id, bytes[6] | (bytes[7] << 8),
                    $"{fixture} no longer carries structure id {id}");
                Assert.AreEqual(1, bytes[4] | (bytes[5] << 8),
                    $"{fixture} is no longer format version 1");
            }
        }

        private static void AssertAllPresent(Func<byte[], bool> test)
        {
            foreach (var word in Words)
            {
                Assert.IsTrue(test(Key(word)), $"the stored structure no longer finds {word}");
            }
        }

        private static void AssertBytes(string fixture, byte[] written)
        {
            CollectionAssert.AreEqual(ReadFixture(fixture), written,
                $"the bytes written for {fixture} have changed since the format was introduced");
        }

        private static Stream Fixture(string name)
        {
            return new MemoryStream(ReadFixture(name), writable: false);
        }

        private static byte[] ReadFixture(string name)
        {
            using var resource = typeof(TestPersistenceFormatStability).Assembly
                .GetManifestResourceStream($"TestProbabilisticDataStructures.fixtures.{name}")
                ?? throw new InvalidOperationException($"fixture {name} is not embedded");

            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            return buffer.ToArray();
        }
    }
}
