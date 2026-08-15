using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestBloomierFilter
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        private static KeyValuePair<byte[], ulong>[] Pairs(int count, int from = 0) =>
            Enumerable.Range(from, count)
                .Select(i => new KeyValuePair<byte[], ulong>(Key($"key{i}"), (ulong)(i % 200)))
                .ToArray();

        [TestMethod]
        public void TestEveryKeyGivesBackItsValue()
        {
            var pairs = Pairs(5000);
            var filter = BloomierFilter.Build(pairs, valueBits: 8);

            foreach (var pair in pairs)
            {
                Assert.IsTrue(filter.TryGetValue(pair.Key, out var value),
                    $"a key it was built from was not found");
                Assert.AreEqual(pair.Value, value,
                    $"the value came back wrong for a key it was built from");
            }
        }
        /// <summary>
        /// What stops the test above from passing vacuously: a map that answered yes to
        /// everything with a plausible value would satisfy it perfectly.
        /// <para>
        /// A Bloomier filter as classically described cannot pass this at all -- it
        /// returns an arbitrary value for a key it never saw, with no way to tell that
        /// from a real answer. This stores a fingerprint beside each value so an absent
        /// key is rejected instead, which is what turns "a wrong answer that looks right"
        /// into a bounded false positive.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestKeysItWasNotBuiltFromAreUsuallyRejected()
        {
            var filter = BloomierFilter.Build(Pairs(20_000), valueBits: 8);

            var accepted = 0;
            const int Probes = 200_000;
            for (var i = 0; i < Probes; i++)
            {
                if (filter.TryGetValue(Key($"absent{i}"), out _))
                {
                    accepted++;
                }
            }

            var rate = (double)accepted / Probes;

            Assert.IsLessThan(0.02, rate,
                $"{rate:P2} of absent keys were accepted, against the 0.39% an 8-bit " +
                "fingerprint gives");
            Assert.IsGreaterThan(0.0005, rate,
                $"only {rate:P3} of absent keys were accepted, which is suspiciously " +
                "far below 0.39% -- a lookup that rejects everything would also pass " +
                "the test above only if that test were broken");
        }

        /// <summary>
        /// Values wider than a byte, since a map whose values are all under 256 is a
        /// narrow kind of map.
        /// </summary>
        [TestMethod]
        public void TestValuesOfSeveralWidthsRoundTrip()
        {
            foreach (var bits in new[] { 1, 4, 16, 32, 40 })
            {
                var max = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
                var pairs = Enumerable.Range(0, 2000)
                    .Select(i => new KeyValuePair<byte[], ulong>(
                        Key($"k{i}"), (ulong)i * 7919 % (max + 1)))
                    .ToArray();

                var filter = BloomierFilter.Build(pairs, bits);

                foreach (var pair in pairs)
                {
                    Assert.IsTrue(filter.TryGetValue(pair.Key, out var value), $"{bits} bits");
                    Assert.AreEqual(pair.Value, value, $"a {bits}-bit value came back wrong");
                }
            }
        }

        [TestMethod]
        public void TestAnEmptyMapHoldsNothing()
        {
            var filter = BloomierFilter.Build(Array.Empty<KeyValuePair<byte[], ulong>>(), 8);

            Assert.AreEqual(0u, filter.Count());
            Assert.IsFalse(filter.TryGetValue(Key("anything"), out _));
        }

        [TestMethod]
        public void TestTheSameKeyTwiceWithDifferentValuesIsRefused()
        {
            var conflicting = new[]
            {
                new KeyValuePair<byte[], ulong>(Key("a"), 1),
                new KeyValuePair<byte[], ulong>(Key("a"), 2),
            };

            var ex = Assert.ThrowsExactly<ArgumentException>(
                () => BloomierFilter.Build(conflicting, 8));
            StringAssert.Contains(ex.Message, "twice with different values");

            // The same value twice is not a conflict, just a repeat.
            var repeated = new[]
            {
                new KeyValuePair<byte[], ulong>(Key("a"), 1),
                new KeyValuePair<byte[], ulong>(Key("a"), 1),
            };

            var filter = BloomierFilter.Build(repeated, 8);
            Assert.AreEqual(1u, filter.Count());
            Assert.IsTrue(filter.TryGetValue(Key("a"), out var value));
            Assert.AreEqual(1ul, value);
        }

        [TestMethod]
        public void TestAValueTooWideForTheFilterIsRefused()
        {
            var tooBig = new[] { new KeyValuePair<byte[], ulong>(Key("a"), 256) };

            var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => BloomierFilter.Build(tooBig, 8));
            StringAssert.Contains(ex.Message, "does not fit");

            // Truncating would have stored 0 and answered confidently with it.
            Assert.AreEqual(255ul, BuildOne(255, 8));
        }

        private static ulong BuildOne(ulong value, int bits)
        {
            var filter = BloomierFilter.Build(
                new[] { new KeyValuePair<byte[], ulong>(Key("a"), value) }, bits);
            filter.TryGetValue(Key("a"), out var stored);
            return stored;
        }

        [TestMethod]
        public void TestBadArgumentsAreRefused()
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => BloomierFilter.Build(null!, 8));

            foreach (var bits in new[] { 0, -1, 41, 64 })
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => BloomierFilter.Build(Pairs(10), bits));
            }

            Assert.ThrowsExactly<ArgumentNullException>(
                () => BloomierFilter.Build(new[] { new KeyValuePair<byte[], ulong>(null!, 1) }, 8));

            Assert.ThrowsExactly<ArgumentNullException>(
                () => BloomierFilter.Build(Pairs(10), 8).TryGetValue(null!, out _));
        }

        [TestMethod]
        public void TestRoundTripsThroughPersistence()
        {
            var pairs = Pairs(5000);
            var filter = BloomierFilter.Build(pairs, valueBits: 16);
            var restored = Persistence.FromByteArray<BloomierFilter>(filter.ToByteArray());

            Assert.AreEqual(filter.Count(), restored.Count());
            Assert.AreEqual(filter.ValueBits(), restored.ValueBits());
            Assert.AreEqual(filter.SizeInBytes(), restored.SizeInBytes());

            foreach (var pair in pairs)
            {
                Assert.IsTrue(restored.TryGetValue(pair.Key, out var value));
                Assert.AreEqual(pair.Value, value, "a restored map gave a different value");
            }

            // And agrees about absence, including the false positives.
            for (var i = 0; i < 5000; i++)
            {
                var key = Key($"absent{i}");
                Assert.AreEqual(
                    filter.TryGetValue(key, out var a), restored.TryGetValue(key, out var b));
                Assert.AreEqual(a, b);
            }
        }

        [TestMethod]
        public void TestAnImpossiblePayloadIsRefused()
        {
            var clean = BloomierFilter.Build(Pairs(100), 8).ToByteArray();

            // Payload layout: u32 size, u32 segment length, u32 segment count, u64 seed,
            // u32 value bits. Poking anything earlier gives a valid map that answers
            // differently rather than an invalid one.
            var bad = (byte[])clean.Clone();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                bad.AsSpan(14 + 20), 60);
            var crc = new System.IO.Hashing.Crc32();
            crc.Append(bad.AsSpan(4, bad.Length - 8));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                bad.AsSpan(bad.Length - 4), crc.GetCurrentHashAsUInt32());

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<BloomierFilter>(bad));
            StringAssert.Contains(ex.Message, "bits");
        }

    }
}
