using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The number of hash functions a Bloom-family payload may claim: at least one, and no
    /// more than <c>OptimalK</c> can return.
    /// <para>
    /// Every constructor gets its hash count from <c>Utils.OptimalK</c>, which gives at
    /// least 1 and, since it takes the reciprocal of the false positive rate, at most
    /// what it gives at the smallest rate whose reciprocal is a finite double. The readers
    /// took anything from 0 to 4,294,967,295. Nothing is allocated from the count by
    /// <c>BloomFilter</c> or <c>BloomFilter64</c>, so the read budget never saw it, and the
    /// cost came on every call: <c>Add</c> runs the count of position computations, and
    /// <c>Test</c> as many on a member. The filters with scratch space allocate an array
    /// of it. A count of zero sets no bit and tests none, so every item is "present".
    /// </para>
    /// <para>
    /// Nothing here depends on a ceiling written out as a number: the test finds the
    /// smallest rate that builds by trying the constructors, and holds the library's
    /// ceiling to what the constructors give there.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestHashCountLimit
    {
        /// <summary>The payload begins after the fixed-size envelope header.</summary>
        private const int PayloadStart = 14;

        private const string NoHashFunctions = "no hash functions";
        private const string TooManyHashFunctions = "hash functions, more than";

        /// <summary>
        /// How to make, change and read the payload of one filter that stores a hash count.
        /// </summary>
        /// <param name="Sample">A valid payload.</param>
        /// <param name="SampleK">The hash count in it.</param>
        /// <param name="Offsets">
        /// Where the payload holds that count, as offsets into the payload. A partitioned
        /// filter holds it twice, as its hash count and as its partition count.
        /// </param>
        /// <param name="BuiltAt">The payload of a filter built for a false positive rate.</param>
        /// <param name="ReadK">Reads a payload and reports its filter's hash count.</param>
        private sealed record Shape(
            Func<byte[]> Sample, uint SampleK, int[] Offsets, Func<double, byte[]> BuiltAt,
            Func<byte[], uint> ReadK);

        private static Shape Of<T>(
            Func<T> sample, int[] offsets, Func<double, T> builtAt, Func<T, uint> k)
            where T : IBinaryPersistable<T> =>
            new(() => sample().ToByteArray(), k(sample()), offsets,
                rate => builtAt(rate).ToByteArray(),
                bytes => k(Persistence.FromByteArray<T>(bytes)));

        /// <summary>
        /// The filters that store a hash count, by type, and where. The offsets are checked
        /// against the count each sample reports, so a layout that moves fails here and
        /// not by poking the wrong field.
        /// </summary>
        private static readonly Dictionary<Type, Shape> Shapes = new()
        {
            [typeof(BloomFilter)] = Of(
                () => new BloomFilter(1000, 0.01), new[] { 4 },
                rate => new BloomFilter(1, rate), f => f.K()),
            [typeof(BloomFilter64)] = Of(
                () => new BloomFilter64(1000, 0.01), new[] { 8 },
                rate => new BloomFilter64(1, rate), f => f.K()),
            [typeof(CountingBloomFilter)] = Of(
                () => new CountingBloomFilter(1000, 4, 0.01), new[] { 8 },
                rate => new CountingBloomFilter(1, 4, rate), f => f.K()),
            [typeof(DeletableBloomFilter)] = Of(
                () => new DeletableBloomFilter(1000, 10, 0.01), new[] { 8 },
                rate => new DeletableBloomFilter(1, 10, rate), f => f.K()),
            [typeof(PartitionedBloomFilter)] = Of(
                () => new PartitionedBloomFilter(1000, 0.01), new[] { 8, 29 },
                rate => new PartitionedBloomFilter(1, rate), f => f.K()),
            // The stable filter's constructor halves the count, so it reaches the ceiling
            // only through the factory that builds an ordinary Bloom filter out of cells.
            [typeof(StableBloomFilter)] = Of(
                () => new StableBloomFilter(1000, 3, 0.01, seed: 1), new[] { 8 },
                rate => StableBloomFilter.NewUnstableBloomFilter(2000, rate), f => f.K()),
        };

        /// <summary>The types a hash-count sweep covers, for the sizes table's test.</summary>
        internal static IEnumerable<string> StructuresThatStoreAHashCount() =>
            Shapes.Keys.Select(t => t.Name);

        private static byte[] Poke(byte[] original, int[] offsets, uint value)
        {
            var bytes = (byte[])original.Clone();
            foreach (var offset in offsets)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(PayloadStart + offset), value);
            }
            Reseal(bytes);
            return bytes;
        }

        /// <summary>Recomputes the trailing CRC of an envelope over bytes 4 up to it.</summary>
        private static void Reseal(Span<byte> envelope)
        {
            var crc = new Crc32();
            crc.Append(envelope[4..^4]);
            BinaryPrimitives.WriteUInt32LittleEndian(envelope[^4..], crc.GetCurrentHashAsUInt32());
        }

        /// <summary>
        /// What is wrong with a read that should have been refused for a hash count, or null
        /// when it was refused for that. A sweep gathers these across every filter, so one
        /// run names each reader that fails rather than the first.
        /// </summary>
        private static string Unrefused(Func<byte[], uint> read, byte[] bytes, string expected, string what)
        {
            try
            {
                read(bytes);
                return $"{what}: read, and should have been refused";
            }
            catch (InvalidDataException e)
            {
                return e.Message.Contains(expected, StringComparison.Ordinal)
                    ? null
                    : $"{what}: refused for another reason: {e.Message}";
            }
        }

        /// <summary>
        /// The smallest false positive rate a constructor takes, found by trying them: the
        /// doubles above zero are ordered as their bits are, so bisection on the bits finds
        /// the first that builds. A rate below it fails in the constructor, as its
        /// reciprocal is infinite.
        /// </summary>
        private static double SmallestRateThatBuilds()
        {
            long fails = 1;
            long builds = BitConverter.DoubleToInt64Bits(0.5);
            Assert.Throws<OverflowException>(() => new BloomFilter(1, BitConverter.Int64BitsToDouble(fails)));
            _ = new BloomFilter(1, BitConverter.Int64BitsToDouble(builds));

            while (builds - fails > 1)
            {
                var middle = fails + (builds - fails) / 2;
                try
                {
                    _ = new BloomFilter(1, BitConverter.Int64BitsToDouble(middle));
                    builds = middle;
                }
                catch (OverflowException)
                {
                    fails = middle;
                }
            }
            return BitConverter.Int64BitsToDouble(builds);
        }

        // ------------------------------------------------------------------ the ceiling

        /// <summary>
        /// The ceiling is <c>OptimalK</c> at the smallest rate that builds, derived by
        /// trying: the library's value, found by stepping up from the reciprocal of the
        /// largest double, must be what the constructors give at the rate bisection finds.
        /// </summary>
        [TestMethod]
        public void TestTheCeilingIsWhatTheConstructorsGiveAtTheSmallestRateThatBuilds()
        {
            var smallest = SmallestRateThatBuilds();
            Assert.Throws<OverflowException>(() => new BloomFilter(1, Math.BitDecrement(smallest)));
            Assert.IsTrue(double.IsFinite(1 / smallest));
            Assert.IsTrue(double.IsInfinity(1 / Math.BitDecrement(smallest)));

            Assert.AreEqual(new BloomFilter(1, smallest).K(), Utils.MaxHashCount);
            Assert.AreEqual(Utils.OptimalK(smallest), Utils.MaxHashCount);
            Assert.IsGreaterThan(1u, Utils.MaxHashCount);

            // Nothing a constructor takes has more: the count falls as the rate rises, and
            // no rate below the smallest builds. A sweep of the range, both ends included.
            for (var rate = smallest; rate < 1; rate *= 3.7)
            {
                Assert.IsLessThanOrEqualTo(Utils.MaxHashCount, Utils.OptimalK(rate), $"rate {rate:R}");
            }
            Assert.AreEqual(1u, Utils.OptimalK(0.5));

            // The same ceiling by arithmetic written differently, as the sketches' depth
            // limit is checked: the base-2 logarithm of the largest double, rounded up.
            Assert.AreEqual((uint)Math.Ceiling(Math.Log2(double.MaxValue)), Utils.MaxHashCount);
        }

        /// <summary>
        /// Read what the constructors build, at the ceiling: every filter that stores a
        /// hash count, built at the smallest rate that builds, writes and reads back with
        /// the count it had.
        /// </summary>
        [TestMethod]
        public void TestEveryFilterBuiltAtTheCeilingReadsBack()
        {
            var smallest = SmallestRateThatBuilds();
            foreach (var (type, shape) in Shapes)
            {
                var bytes = shape.BuiltAt(smallest);
                Assert.AreEqual(Utils.MaxHashCount, shape.ReadK(bytes),
                    $"{type.Name} built at the smallest rate that builds, written and read back");
            }
        }

        // ------------------------------------------------------------------ the readers

        [TestMethod]
        public void TestEveryReaderOfAHashCountFindsItWhereTheOffsetsSay()
        {
            foreach (var (type, shape) in Shapes)
            {
                var bytes = shape.Sample();
                foreach (var offset in shape.Offsets)
                {
                    Assert.AreEqual(shape.SampleK,
                        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(PayloadStart + offset)),
                        $"{type.Name} does not hold its hash count at payload offset {offset}");
                }
                Assert.AreEqual(shape.SampleK, shape.ReadK(bytes), type.Name);
            }
        }

        /// <summary>
        /// A filter of no hash functions sets no bit and tests none: it answers present to
        /// every item, and an add only counts. The stable filter's reader has always said so;
        /// the rest took it.
        /// </summary>
        [TestMethod]
        public void TestEveryReaderOfAHashCountRefusesNone()
        {
            var problems = Shapes
                .Select(s => Unrefused(s.Value.ReadK, Poke(s.Value.Sample(), s.Value.Offsets, 0),
                    NoHashFunctions, $"{s.Key.Name} with a hash count of 0"))
                .OfType<string>()
                .ToList();
            Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
        }

        /// <summary>
        /// One past the ceiling, and the sizes that matter above it: a count whose scratch
        /// array is a few megabytes, one just over the default read budget, and the largest
        /// there is. The refusal is the hash count's, so it comes before the read budget
        /// prices an array of that many, and before one is allocated.
        /// </summary>
        [TestMethod]
        public void TestEveryReaderOfAHashCountRefusesMoreThanAnyConstructorBuilds()
        {
            var problems = (
                from s in Shapes
                from k in new[] { Utils.MaxHashCount + 1, 1u << 20, 1u << 28, uint.MaxValue }
                select Unrefused(s.Value.ReadK, Poke(s.Value.Sample(), s.Value.Offsets, k),
                    TooManyHashFunctions, $"{s.Key.Name} with a hash count of {k:N0}"))
                .OfType<string>()
                .ToList();
            Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
        }

        /// <summary>
        /// A count of the ceiling itself is not refused for its size: of the filters whose
        /// count is not tied to data in the payload, the sample with its count raised to it
        /// reads. A partitioned filter's count is its partition count, and holds the
        /// partitions present to it, so it is covered by the build at the ceiling above.
        /// </summary>
        [TestMethod]
        public void TestTheCeilingItselfIsNotRefused()
        {
            foreach (var (type, shape) in Shapes.Where(s => s.Key != typeof(PartitionedBloomFilter)))
            {
                var bytes = Poke(shape.Sample(), shape.Offsets, Utils.MaxHashCount);
                Assert.AreEqual(Utils.MaxHashCount, shape.ReadK(bytes), type.Name);
            }
        }

        // ------------------------------------------------------------------ the roster

        private static IEnumerable<Type> FiltersThatReportAHashCount() =>
            typeof(BloomFilter).Assembly.GetExportedTypes()
                .Where(t => t.IsClass && !t.IsAbstract
                    && t.GetInterfaces().Any(i => i.IsGenericType
                        && i.GetGenericTypeDefinition() == typeof(IBinaryPersistable<>))
                    && t.GetMethod("K", Type.EmptyTypes) is { } k && k.ReturnType == typeof(uint));

        /// <summary>
        /// A filter that reports a hash count and is not swept above is exempt with a reason
        /// and a test that checks it. Derived from the public surface, so a filter that
        /// gains a <c>K()</c> fails here until its payload is held to the count.
        /// </summary>
        private static readonly (Type Type, string Why, string VerifiedBy)[] Exempt =
        {
            (typeof(ScalableBloomFilter),
                "it stores no hash count of its own: it holds partitioned filters, each read by " +
                "its own reader, which holds it to the count",
                nameof(TestScalableFilterRefusesAContainedFilterWithNoHashFunctions)),
        };

        [TestMethod]
        public void TestEveryFilterThatReportsAHashCountIsHeldToTheLimit()
        {
            var reporting = FiltersThatReportAHashCount().ToHashSet();
            Assert.IsNotEmpty(reporting, "the reflection found no filter with a K()");

            var covered = Shapes.Keys.Concat(Exempt.Select(e => e.Type)).ToHashSet();
            CollectionAssert.AreEquivalent(
                reporting.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                covered.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                "the filters that report a hash count are not the ones swept or exempted: a " +
                "filter that gained a K() needs its payload held to the limit, and an entry " +
                "for one that lost it has gone stale");

            foreach (var (type, why, test) in Exempt)
            {
                Assert.IsFalse(Shapes.ContainsKey(type), $"{type.Name} is swept, so its exemption is stale");
                Assert.IsFalse(string.IsNullOrWhiteSpace(why), $"{type.Name} is exempt without a reason");
                var method = typeof(TestHashCountLimit).GetMethod(test);
                Assert.IsNotNull(method, $"the exemption for {type.Name} names {test}, which is not a test here");
                Assert.IsNotEmpty(method.GetCustomAttributes(typeof(TestMethodAttribute), false),
                    $"the exemption for {type.Name} names {test}, which is not a test method");
            }
        }

        /// <summary>
        /// The reason the scalable filter is exempt: its hash counts are its contained
        /// filters', and a payload that nests one with none is refused for it. The
        /// contained filter is a nested envelope with a checksum of its own, so both are
        /// resealed.
        /// </summary>
        [TestMethod]
        public void TestScalableFilterRefusesAContainedFilterWithNoHashFunctions()
        {
            var outer = new ScalableBloomFilter(100, 0.01, 0.8).ToByteArray();
            Assert.IsNotNull(Persistence.FromByteArray<ScalableBloomFilter>(outer));

            var found = outer.AsSpan(PayloadStart).IndexOf("PDS\0"u8);
            Assert.IsGreaterThan(0, found, "no nested envelope in the scalable filter's payload");
            var nested = PayloadStart + found;
            var nestedLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(outer.AsSpan(nested - 4));

            var partitioned = new PartitionedBloomFilter(100, 0.01);
            foreach (var offset in new[] { 8, 29 })
            {
                Assert.AreEqual(partitioned.K(),
                    BinaryPrimitives.ReadUInt32LittleEndian(outer.AsSpan(nested + PayloadStart + offset)),
                    "the contained filter holds its hash count where the partitioned filter does");
            }

            var bytes = (byte[])outer.Clone();
            foreach (var offset in new[] { 8, 29 })
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(nested + PayloadStart + offset), 0);
            }
            Reseal(bytes.AsSpan(nested, nestedLength));
            Reseal(bytes);

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<ScalableBloomFilter>(bytes));
            StringAssert.Contains(ex.Message, NoHashFunctions);
        }
    }
}
