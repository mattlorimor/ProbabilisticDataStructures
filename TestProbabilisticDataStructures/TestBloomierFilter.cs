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

        /// <summary>
        /// States whose payload <see cref="TestPayloadCount"/> holds the count to: the cells
        /// are one run, sized by the pairs and the value's width, so no pairs and two counts
        /// of them, at two widths.
        /// </summary>
        internal static IEnumerable<PayloadState> PayloadStates()
        {
            foreach (var valueBits in new[] { 8, 13 })
            {
                foreach (var count in new[] { 0, 10, 1_000 })
                {
                    yield return PayloadState.Of($"{valueBits} bits, {count} pairs",
                        BloomierFilter.Build(Pairs(count), valueBits));
                }
            }
        }

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

        // ---- Two keys the filter cannot tell apart ----------------------------------------

        /// <summary>
        /// A key is identified by its 64-bit hash alone, and the keys themselves are not
        /// kept. Two different keys with the same hash and different values therefore
        /// cannot both be held, and the build is refused: about 2.4% of builds at the 954
        /// million keys 8-bit values allow, and 39% at 2^32. It used to say that "a key
        /// appears twice with different values", which for two different keys is false and
        /// sends the caller looking for a repeat that is not there. The message now says
        /// what happened, how likely it is, and what to do. A hash that sends both keys to
        /// one value stands in for the chance.
        /// </summary>
        [TestMethod]
        public void TestTwoKeysWithOneHashAreRefusedWithAnExplanationOfTheCollision()
        {
            var distinct = new[]
            {
                new KeyValuePair<byte[], ulong>(Key("a"), 1),
                new KeyValuePair<byte[], ulong>(Key("b"), 2),
            };

            var ex = Assert.ThrowsExactly<ArgumentException>(
                () => BloomierFilter.Build(distinct, 8, hash: data => 42UL));
            Assert.AreEqual("pairs", ex.ParamName);
            StringAssert.Contains(ex.Message, "different keys",
                "a refusal that two different keys share a hash should say so");
            StringAssert.Contains(ex.Message, "64-bit hash");
            StringAssert.Contains(ex.Message, "hash function",
                "the way out is a different hash function, and the message should say so");
            StringAssert.Contains(ex.Message, "1 and 2",
                "the two values it could not both keep should be named");

            // Distinct keys that share a hash and a value are one entry, as they always were:
            // either answers with the value, and nothing is lost.
            var sameValue = new[]
            {
                new KeyValuePair<byte[], ulong>(Key("a"), 7),
                new KeyValuePair<byte[], ulong>(Key("b"), 7),
            };
            var filter = BloomierFilter.Build(sameValue, 8, hash: data => 42UL);
            Assert.AreEqual(1u, filter.Count());
            Assert.IsTrue(filter.TryGetValue(Key("a"), out var value));
            Assert.AreEqual(7UL, value);
        }

        /// <summary>
        /// The same key twice is still the case the refusal was written for, and the
        /// message still says it, beside the other possibility.
        /// </summary>
        [TestMethod]
        public void TestTheRefusalNamesBothWaysTwoEntriesCanShareAHash()
        {
            var conflicting = new[]
            {
                new KeyValuePair<byte[], ulong>(Key("a"), 1),
                new KeyValuePair<byte[], ulong>(Key("a"), 2),
            };

            var ex = Assert.ThrowsExactly<ArgumentException>(
                () => BloomierFilter.Build(conflicting, 8));
            StringAssert.Contains(ex.Message, "twice with different values");
            StringAssert.Contains(ex.Message, "different keys");
        }

        // ---- The cells are one array ------------------------------------------------------

        /// <summary>
        /// The cells are one array of bytes, which holds 2,147,483,591: about 954 million
        /// keys at 8-bit values, 636 million at 16 and 318 million at 40. Past that the
        /// build is refused, naming the pairs, where it used to fail inside the
        /// allocation. The sizing step is reached without the keys, since building a map
        /// this large is not a test.
        /// </summary>
        [TestMethod]
        public void TestMoreKeysThanOneArrayHoldsAreRefusedBeforeAnythingIsAllocated()
        {
            var cases = new[]
            {
                (8, 1_000_000_000u),
                (16, 800_000_000u),
                (40, 400_000_000u),
                (8, 2_000_000_000u),
            };

            foreach (var (valueBits, keys) in cases)
            {
                var filter = (BloomierFilter)System.Runtime.CompilerServices.RuntimeHelpers
                    .GetUninitializedObject(typeof(BloomierFilter));
                typeof(BloomierFilter).GetField(
                    "valueBits", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .SetValue(filter, valueBits);
                var shape = typeof(BloomierFilter).GetMethod(
                    "Shape", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

                var ex = Assert.ThrowsExactly<System.Reflection.TargetInvocationException>(
                    () => shape.Invoke(filter, new object[] { keys })).InnerException!;
                Assert.IsInstanceOfType<ArgumentOutOfRangeException>(ex,
                    $"{keys:N0} keys at {valueBits} bits failed with {ex.GetType().Name}: {ex.Message}");
                Assert.AreEqual("pairs", ((ArgumentOutOfRangeException)ex).ParamName);
            }
        }

        /// <summary>
        /// The most keys at each width of value are held to the fuse sizing restated from
        /// the paper's parameters (<see cref="CapacityRows.FuseCells"/>), as the binary
        /// fuse filter's are: at the most the cells fit one array and with one more they do
        /// not, the filter's own search agrees, and the refusal names the most.
        /// </summary>
        [TestMethod]
        public void TestTheMostKeysAreTheMostWhoseCellsAreOneArray()
        {
            foreach (var valueBits in new[] { 8, 16, 40 })
            {
                var stride = (ulong)((8 + valueBits + 7) / 8);
                var most = BloomierFilter.MaxKeys(valueBits);

                Assert.IsLessThanOrEqualTo((ulong)Array.MaxLength, CapacityRows.FuseCells(most) * stride,
                    $"{most:N0} keys of {valueBits} bits should fit one array");
                Assert.IsGreaterThan((ulong)Array.MaxLength, CapacityRows.FuseCells(most + 1UL) * stride,
                    $"{most + 1UL:N0} keys of {valueBits} bits should not");
                Assert.AreEqual((long)(CapacityRows.FuseCells(most) * stride),
                    BloomierFilter.CellBytes(most, valueBits));

                var filter = (BloomierFilter)System.Runtime.CompilerServices.RuntimeHelpers
                    .GetUninitializedObject(typeof(BloomierFilter));
                typeof(BloomierFilter).GetField(
                    "valueBits", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .SetValue(filter, valueBits);
                var shape = typeof(BloomierFilter).GetMethod(
                    "Shape", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                var ex = Assert.ThrowsExactly<System.Reflection.TargetInvocationException>(
                    () => shape.Invoke(filter, new object[] { most + 1 })).InnerException!;
                Assert.IsInstanceOfType<ArgumentOutOfRangeException>(ex);
                StringAssert.Contains(ex.Message, most.ToString("N0"));
            }
        }

        /// <summary>
        /// The limit is on distinct keys, so a set of exactly the limit builds however often
        /// its keys repeat with their values, and one more key is refused, naming the pairs
        /// and the limit. The limit is lowered through the build the public one calls.
        /// </summary>
        [TestMethod]
        public void TestTheKeyLimitCountsDistinctKeys()
        {
            const uint limit = 100;
            var atTheLimit = Pairs((int)limit);
            var repeated = atTheLimit.Concat(atTheLimit).Concat(atTheLimit.Take(10)).ToArray();

            var built = BloomierFilter.Build(repeated, 8, null, limit);
            Assert.AreEqual(limit, built.Count());

            var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => BloomierFilter.Build(Pairs((int)limit + 1), 8, null, limit));
            Assert.AreEqual("pairs", ex.ParamName);
            StringAssert.Contains(ex.Message, $"more than {limit:N0} distinct keys");
        }

        /// <summary>
        /// A set with too many keys and a key given two values is refused for whichever the
        /// pairs reach first: the limit is applied as the keys arrive, so the pairs are not
        /// read past it to find a conflict further on. Before the limit was applied that way
        /// a conflict anywhere was refused, since the count was compared only once every pair
        /// had been collected.
        /// </summary>
        [TestMethod]
        public void TestTooManyKeysAndAConflictAreRefusedForWhicheverComesFirst()
        {
            const uint limit = 3;
            var conflict = new KeyValuePair<byte[], ulong>(Key("key0"), 99);

            var tooManyFirst = Pairs(4).Append(conflict).ToArray();
            Assert.AreEqual("pairs", Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => BloomierFilter.Build(tooManyFirst, 8, null, limit)).ParamName);

            var conflictFirst = Pairs(2).Append(conflict).Concat(Pairs(2, from: 2)).ToArray();
            StringAssert.Contains(Assert.ThrowsExactly<ArgumentException>(
                () => BloomierFilter.Build(conflictFirst, 8, null, limit)).Message,
                "twice with different values");
        }
    }
}
