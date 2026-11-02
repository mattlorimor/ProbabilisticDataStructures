using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// How the count-min sketches, HeavyKeeper and the inverse Bloom filter turn an item's
    /// hash into the column, bucket or slot it is counted or held in, and the width at
    /// which a new one stops doing it with 32-bit values.
    /// </summary>
    /// <remarks>
    /// Each of them reduced a 32-bit value modulo its width: the count-min sketches and
    /// HeavyKeeper take <c>(low + i * high) mod 2^32</c> for row <c>i</c>, the inverse
    /// filter the hash's low 32 bits. Reducing a uniform 32-bit value modulo a width that
    /// does not divide 2^32 gives the first <c>2^32 mod w</c> columns one more of the
    /// 2^32 values than the rest, which is the defect #180 fixed in the Bloom family:
    /// a heavier column collects more of other items' counts, so the keys there are
    /// overcounted by more than the bound assumes, and the inverse filter overwrites its
    /// low slots sooner. The spread is <c>1 / floor(2^32 / w)</c>: 3% just above 2^27,
    /// 25% at 2^29.8, 50% from a third of 2^32 up, which these structures all reach.
    /// <para>
    /// A sketch 2^27 columns wide or wider is built with the 64-bit scheme the Bloom
    /// family uses from there, and a narrower one is placed exactly as it always was.
    /// Every save is format version 9, which records the placement in a byte of its own.
    /// What a payload was placed with is read from that byte, or, before version 9, is
    /// the 32-bit scheme, and never from its width.
    /// </para>
    /// <para>
    /// A row of 2^27 counters is a gigabyte, so the column rule is checked directly at
    /// the widths the fold is worst, without a sketch; the constructors and the readers
    /// are checked at the threshold itself, built and read for real one at a time; and
    /// everything about what a sketch placed with 64-bit values counts, writes, reads and
    /// merges with is checked on small sketches built with that placement through an
    /// internal constructor.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestSketchPlacement
    {
        // Written out rather than read from the library, which is what is being checked.
        private const ulong Threshold = 1UL << 27;
        private const ulong TwoTo32 = 1UL << 32;
        private const ushort PlacementVersion = 9;
        private const int Header = 14;

        private static readonly string[] Words =
            { "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa" };

        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        private static ulong Hash(byte[] data) => XxHash3.HashToUInt64(data);

        /// <summary>
        /// The 32-bit scheme, restated: the hash's halves, combined in 32-bit arithmetic.
        /// This is the rule every one of these structures used before, at every width.
        /// </summary>
        private static uint Narrow(ulong hash, uint i, uint m) =>
            unchecked((uint)hash + (uint)(hash >> 32) * i) % m;

        /// <summary>
        /// The 64-bit scheme, restated: the hash, the hash of its eight little-endian
        /// bytes, combined in 64-bit arithmetic.
        /// </summary>
        private static uint Wide(ulong hash, uint i, uint m)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, hash);
            var second = XxHash3.HashToUInt64(bytes);
            return (uint)(unchecked(hash + second * i) % m);
        }

        /// <summary>
        /// The one slot of the inverse filter, restated: the low 32 bits of the hash
        /// modulo the capacity under the 32-bit scheme, and all 64 under the 64-bit one.
        /// </summary>
        private static uint Slot(Placement placement, ulong hash, uint m) =>
            placement == Placement.Wide ? (uint)(hash % m) : (uint)hash % m;

        private static uint Restated(Placement placement, ulong hash, uint i, uint m) =>
            placement == Placement.Wide ? Wide(hash, i, m) : Narrow(hash, i, m);

        private static ushort VersionOf(byte[] frame) =>
            BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4));

        // ---- The structures ----------------------------------------------------------

        /// <summary>
        /// One kind of structure that places by width: its static choice and column rule,
        /// which reach widths no test can allocate, and a small one of it, placed either
        /// way, with what it takes to use it without knowing its type.
        /// </summary>
        internal sealed record Kind(
            string Name,
            uint Rows,
            Func<uint, Placement> PlacementFor,
            Func<byte[], Placement, uint, uint, uint> ColumnAt,
            Func<Placement?, object> Build,
            int PlacementOffset,
            Func<object, byte[]> Write,
            Func<byte[], object> Read,
            Func<object, Placement> PlacementOf,
            Action<object, byte[]> Add,
            Func<object, byte[], double> Answer,
            Func<object, uint> Width,
            Func<object, object, IEnumerable<(uint Row, uint Column)>> Touched);

        private static IEnumerable<(uint, uint)> Nonzero(ulong[][] rows)
        {
            for (uint i = 0; i < rows.Length; i++)
            {
                for (uint j = 0; j < rows[i].Length; j++)
                {
                    if (rows[i][j] != 0)
                    {
                        yield return (i, j);
                    }
                }
            }
        }

        private static IEnumerable<(uint, uint)> Changed(double[][] rows, double[][] untouched)
        {
            for (uint i = 0; i < rows.Length; i++)
            {
                for (uint j = 0; j < rows[i].Length; j++)
                {
                    if (rows[i][j] != untouched[i][j])
                    {
                        yield return (i, j);
                    }
                }
            }
        }

        /// <summary>
        /// The structures, each built small at a width that is not a power of two: at a
        /// power of two the two schemes agree on the first row, and the inverse filter's
        /// only slot, so a test there could not tell them apart. The private sketch and
        /// HeavyKeeper are seeded, so that the same build is the same structure.
        /// </summary>
        internal static Kind[] Kinds() => new[]
        {
            new Kind(nameof(CountMinSketch), 5,
                CountMinSketch.PlacementFor,
                (key, p, w, row) => CountMinSketch.ColumnsOf(key, Defaults.GetDefaultHashFunction(), p, w)[row],
                p => new CountMinSketch(0.01, 0.01, null, p), 24,
                s => ((CountMinSketch)s).ToByteArray(),
                b => Persistence.FromByteArray<CountMinSketch>(b),
                s => ((CountMinSketch)s).Placement,
                (s, k) => ((CountMinSketch)s).Add(k),
                (s, k) => ((CountMinSketch)s).Count(k),
                s => ((CountMinSketch)s).Width,
                (s, _) => Nonzero(((CountMinSketch)s).Matrix)),
            new Kind(nameof(PrivateCountMinSketch), 4,
                PrivateCountMinSketch.PlacementFor,
                (key, p, w, row) => PrivateCountMinSketch.ColumnsOf(key, Defaults.GetDefaultHashFunction(), p, w)[row],
                p => new PrivateCountMinSketch(61, 4, 1.0, 7, null, p), 8,
                s => ((PrivateCountMinSketch)s).ToByteArray(),
                b => Persistence.FromByteArray<PrivateCountMinSketch>(b),
                s => ((PrivateCountMinSketch)s).Placement,
                (s, k) => ((PrivateCountMinSketch)s).Add(k),
                (s, k) => ((PrivateCountMinSketch)s).Count(k),
                s => ((PrivateCountMinSketch)s).Width,
                (s, empty) => Changed(((PrivateCountMinSketch)s).Counters, ((PrivateCountMinSketch)empty).Counters)),
            new Kind(nameof(HeavyKeeper), 2,
                HeavyKeeper.PlacementFor,
                (key, p, w, row) => HeavyKeeper.BucketsOf(Hash(key), Defaults.GetDefaultHashFunction(), p, w)[row],
                p => new HeavyKeeper(5, 61, 2, 1.08, 7, null, p), 12,
                s => ((HeavyKeeper)s).ToByteArray(),
                b => Persistence.FromByteArray<HeavyKeeper>(b),
                s => ((HeavyKeeper)s).Placement,
                (s, k) => ((HeavyKeeper)s).Add(k),
                (s, k) => ((HeavyKeeper)s).Count(k),
                s => ((HeavyKeeper)s).Width,
                (s, _) => Nonzero(((HeavyKeeper)s).Counters)),
            new Kind(nameof(InverseBloomFilter), 1,
                InverseBloomFilter.PlacementFor,
                (key, p, w, _) => InverseBloomFilter.SlotOf(Hash(key), p, w),
                p => new InverseBloomFilter(61, null, p), 4,
                s => ((InverseBloomFilter)s).ToByteArray(),
                b => Persistence.FromByteArray<InverseBloomFilter>(b),
                s => ((InverseBloomFilter)s).Placement,
                (s, k) => ((InverseBloomFilter)s).Add(k),
                (s, k) => ((InverseBloomFilter)s).Test(k) ? 1 : 0,
                s => ((InverseBloomFilter)s).Capacity(),
                (s, _) => ((InverseBloomFilter)s).OccupiedSlots().Select(slot => (0u, slot))),
        };

        /// <summary>
        /// Every structure in the library that carries a placement is held to it, here or
        /// by the Bloom family's tests. The roster is the library's types, by reflection,
        /// so a structure that gains a placement without a test of it is named here.
        /// </summary>
        [TestMethod]
        public void TestEveryStructureThatPlacesIsHeldToItsPlacement()
        {
            var placing = typeof(BloomFilter).Assembly.GetTypes()
                .Where(t => t.GetProperty("Placement", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    ?.PropertyType == typeof(Placement))
                .Select(t => t.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            var held = Kinds().Select(k => k.Name)
                .Concat(TestBitPlacement.KindNames())
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.IsNotEmpty(placing, "no type carries a placement, so this checks nothing");
            CollectionAssert.AreEqual(placing, held,
                "the structures that carry a placement are not the ones the placement tests hold to it");
        }

        // ---- The rule, without a structure --------------------------------------------

        /// <summary>
        /// The width threshold, straddled, for each structure's own choice. Every width
        /// below it places with 32-bit values, as every one of these always has, and every
        /// width from it up with 64-bit ones, to the most an array holds.
        /// </summary>
        [TestMethod]
        public void TestEachSketchChoosesTheSixtyFourBitSchemeFromTwoToTheTwentySeven()
        {
            foreach (var kind in Kinds())
            {
                Assert.AreEqual(Placement.Narrow, kind.PlacementFor(1), $"{kind.Name}: one column");
                Assert.AreEqual(Placement.Narrow, kind.PlacementFor((uint)Threshold - 1), $"{kind.Name}: one below the threshold");
                Assert.AreEqual(Placement.Wide, kind.PlacementFor((uint)Threshold), $"{kind.Name}: the threshold");
                Assert.AreEqual(Placement.Wide, kind.PlacementFor((uint)Threshold + 1), $"{kind.Name}: one above the threshold");
                Assert.AreEqual(Placement.Wide, kind.PlacementFor((uint)Array.MaxLength), $"{kind.Name}: the widest an array holds");
            }
        }

        /// <summary>
        /// At every width the 64-bit scheme covers, a new structure's columns are spread
        /// evenly, where the 32-bit scheme crowds them into the start of each row. The
        /// statistic is the share of columns below <c>2^32 mod w</c>, which is exactly where
        /// the fold puts its extra preimages: an even spread puts <c>r / w</c> of them there,
        /// the fold <c>(floor(2^32 / w) + 1) r / 2^32</c>.
        /// </summary>
        /// <remarks>
        /// The widths are where the fold is worst within each step of
        /// <c>floor(2^32 / w)</c>, where <c>2^32 mod w</c> is half of <c>w</c>: the first
        /// step above the threshold, three between, and the last an array reaches, at
        /// 2.5. Each structure takes the placement its own choice gives at that width, and
        /// its own column rule, through every row it has, so this fails for any structure
        /// that would still fold there. 700,000 columns are drawn at each width, and each
        /// width first checks that the fold there is far enough from even for that sample
        /// to see it, so a width at which the two cannot be told apart cannot pass
        /// vacuously.
        /// </remarks>
        [TestMethod]
        public void TestAWideSketchSpreadsItsColumnsEvenly()
        {
            const int Samples = 700_000;
            var widths = new[] { 31.5, 15.5, 7.5, 3.5, 2.5 }.Select(steps => (uint)(TwoTo32 / steps)).ToArray();
            Assert.IsTrue(widths.All(w => w >= Threshold && w <= Array.MaxLength), "a width here is out of the range it is meant to cover");

            foreach (var kind in Kinds())
            {
                var items = Samples / (int)kind.Rows;
                foreach (var w in widths)
                {
                    var r = TwoTo32 % w;
                    var even = (double)r / w;
                    var folded = (double)(TwoTo32 / w + 1) * r / TwoTo32;
                    var drawn = (double)items * kind.Rows;
                    var se = Math.Sqrt(even * (1 - even) / drawn);

                    Assert.IsGreaterThan(10 * se, Math.Abs(folded - even),
                        $"at w = {w} the fold ({folded:F5}) is too close to even ({even:F5}) for this sample to tell them apart");

                    var placement = kind.PlacementFor(w);
                    long below = 0;
                    for (var t = 0; t < items; t++)
                    {
                        var key = Key($"spread-{w}-{t}");
                        for (uint row = 0; row < kind.Rows; row++)
                        {
                            if (kind.ColumnAt(key, placement, w, row) < r)
                            {
                                below++;
                            }
                        }
                    }

                    var share = below / drawn;
                    Assert.IsLessThan(4 * se, Math.Abs(share - even),
                        $"{kind.Name} at w = {w} ({(double)w / TwoTo32:F4} of 2^32): a share of {share:F5} of " +
                        $"its columns fell below {r}, where an even spread gives {even:F5} and the 32-bit " +
                        $"fold {folded:F5} ({(share - even) / se:F1} standard errors from even)");
                }
            }
        }

        /// <summary>
        /// Below the threshold a new structure places exactly as every one of these always
        /// did, with the rule restated from the code before the change: so a sketch below
        /// 2^27 counts in the same cells, and writes the same counters, as it did. The widths
        /// are the one just below the threshold, a width where the fold is worst below it,
        /// and the small widths the tests build.
        /// </summary>
        [TestMethod]
        public void TestBelowTheThresholdEachSketchPlacesAsItAlwaysDid()
        {
            foreach (var kind in Kinds())
            {
                foreach (var w in new[] { (uint)Threshold - 1, (uint)(TwoTo32 / 32.5), 272u, 61u })
                {
                    var placement = kind.PlacementFor(w);
                    Assert.AreEqual(Placement.Narrow, placement, $"{kind.Name} at w = {w}");

                    for (var t = 0; t < 2000; t++)
                    {
                        var key = Key($"below-{t}");
                        var hash = Hash(key);
                        for (uint row = 0; row < kind.Rows; row++)
                        {
                            var expected = kind.Rows == 1 ? (uint)hash % w : Narrow(hash, row, w);
                            Assert.AreEqual(expected, kind.ColumnAt(key, placement, w, row),
                                $"{kind.Name} at w = {w} does not place below-{t}, row {row}, as it always did");
                        }
                    }
                }
            }
        }

        // ---- What a structure placed with 64-bit values does ---------------------------

        /// <summary>
        /// Every structure placed with 64-bit values counts or holds each item exactly where
        /// the restated scheme says, among its columns, and nowhere else; and placed with
        /// 32-bit values, exactly where that scheme says. The two differ, so this can tell
        /// them apart. For the private sketch "where" is the cells that moved from its
        /// noise, against a twin with the same seed that was given nothing.
        /// </summary>
        [TestMethod]
        public void TestEveryWideSketchCountsWhereTheSixtyFourBitSchemeSays()
        {
            foreach (var kind in Kinds())
            {
                var sets = new Dictionary<Placement, HashSet<(uint, uint)>>();
                foreach (var placement in new[] { Placement.Wide, Placement.Narrow })
                {
                    var subject = kind.Build(placement);
                    var expected = new HashSet<(uint, uint)>();
                    var w = kind.Width(subject);
                    for (var i = 0; i < 40; i++)
                    {
                        var key = Key($"where-{i}");
                        kind.Add(subject, key);
                        var hash = Hash(key);
                        for (uint row = 0; row < kind.Rows; row++)
                        {
                            expected.Add((row, kind.Rows == 1 ? Slot(placement, hash, w) : Restated(placement, hash, row, w)));
                        }
                    }

                    var touched = kind.Touched(subject, kind.Build(placement)).ToHashSet();
                    Assert.IsTrue(touched.SetEquals(expected),
                        $"{kind.Name} placed {placement} counted in cells other than the ones the restated scheme gives");
                    sets[placement] = touched;
                }

                Assert.IsFalse(sets[Placement.Wide].SetEquals(sets[Placement.Narrow]),
                    $"{kind.Name} counted the same items in the same cells either way, so this cannot tell them apart");
            }
        }

        /// <summary>
        /// Every new save of these structures is format version 9, whatever its width or
        /// placement, and its placement byte says which scheme placed it: 0 for 32-bit
        /// values, 1 for 64-bit ones. A small structure from the public constructor is no
        /// exception.
        /// </summary>
        [TestMethod]
        public void TestEveryNewSaveIsVersionNineAndRecordsItsPlacement()
        {
            foreach (var kind in Kinds())
            {
                foreach (var (built, byteValue, what) in new[]
                         { (Placement.Wide, (byte)1, "placed with 64-bit values"), (Placement.Narrow, (byte)0, "placed with 32-bit values") })
                {
                    var subject = kind.Build(built);
                    foreach (var word in Words)
                    {
                        kind.Add(subject, Key(word));
                    }

                    var bytes = kind.Write(subject);
                    Assert.AreEqual(PlacementVersion, VersionOf(bytes), $"{kind.Name} {what}");
                    Assert.AreEqual(byteValue, bytes[Header + kind.PlacementOffset], $"{kind.Name} {what}: its placement byte");
                }

                var small = kind.Build(null);
                Assert.AreEqual(Placement.Narrow, kind.PlacementOf(small), $"{kind.Name} built small");
                var smallBytes = kind.Write(small);
                Assert.AreEqual(PlacementVersion, VersionOf(smallBytes), $"{kind.Name} built small");
                Assert.AreEqual((byte)0, smallBytes[Header + kind.PlacementOffset], $"{kind.Name} built small: its placement byte");
            }
        }

        /// <summary>
        /// A structure placed with 64-bit values reads back placed with 64-bit values,
        /// answers as it did for everything it was given, and writes the same bytes again.
        /// </summary>
        [TestMethod]
        public void TestAWideSketchReadsBackWide()
        {
            foreach (var kind in Kinds())
            {
                var wide = kind.Build(Placement.Wide);
                foreach (var word in Words)
                {
                    kind.Add(wide, Key(word));
                }

                var bytes = kind.Write(wide);
                var read = kind.Read(bytes);

                Assert.AreEqual(Placement.Wide, kind.PlacementOf(read), $"{kind.Name} read back");
                foreach (var word in Words)
                {
                    Assert.AreEqual(kind.Answer(wide, Key(word)), kind.Answer(read, Key(word)),
                        $"{kind.Name} read back answers otherwise for {word}");
                }
                CollectionAssert.AreEqual(bytes, kind.Write(read), $"{kind.Name} read back writes other bytes");
            }
        }

        /// <summary>
        /// The placement byte, and not the width, says how a version 9 payload was placed:
        /// these structures are far below the threshold, and a payload whose byte is
        /// swapped reads back the other way, looking for its items where they were not
        /// counted. A byte naming no scheme is refused with what it holds. A payload from
        /// before version 9 has no byte, was placed with 32-bit values, reads back that way
        /// and saves again as version 9.
        /// </summary>
        [TestMethod]
        public void TestThePlacementByteAndNotTheWidthSaysHowAPayloadWasPlaced()
        {
            foreach (var kind in Kinds())
            {
                var narrowSketch = kind.Build(Placement.Narrow);
                var wideSketch = kind.Build(Placement.Wide);
                foreach (var word in Words)
                {
                    kind.Add(narrowSketch, Key(word));
                    kind.Add(wideSketch, Key(word));
                }

                var narrow = kind.Write(narrowSketch);
                var wide = kind.Write(wideSketch);
                var at = Header + kind.PlacementOffset;

                Assert.AreEqual(Placement.Narrow, kind.PlacementOf(kind.Read(narrow)), $"{kind.Name} placed with 32-bit values");
                Assert.AreEqual(Placement.Wide, kind.PlacementOf(kind.Read(wide)), $"{kind.Name} placed with 64-bit values");

                foreach (var (bytes, original, swapped, expected) in new[]
                         { (narrow, narrowSketch, (byte)1, Placement.Wide), (wide, wideSketch, (byte)0, Placement.Narrow) })
                {
                    var read = kind.Read(WithByte(bytes, at, swapped));
                    Assert.AreEqual(expected, kind.PlacementOf(read), $"{kind.Name} with its placement byte swapped");
                    Assert.IsTrue(Words.Any(word => kind.Answer(read, Key(word)) != kind.Answer(original, Key(word))),
                        $"{kind.Name} read the other way still answers as it did for every word, so the byte decided nothing");
                }

                foreach (var unknown in new byte[] { 2, 255 })
                {
                    var ex = Assert.ThrowsExactly<InvalidDataException>(
                        () => kind.Read(WithByte(narrow, at, unknown)),
                        $"{kind.Name} read a placement byte of {unknown}, which names no scheme");
                    StringAssert.Contains(ex.Message, "placement");
                    StringAssert.Contains(ex.Message, unknown.ToString());
                }

                var earlier = Downgraded(narrow, kind.PlacementOffset);
                Assert.AreEqual((ushort)1, VersionOf(earlier));
                var old = kind.Read(earlier);
                Assert.AreEqual(Placement.Narrow, kind.PlacementOf(old), $"{kind.Name} at version 1");
                foreach (var word in Words)
                {
                    Assert.AreEqual(kind.Answer(narrowSketch, Key(word)), kind.Answer(old, Key(word)),
                        $"{kind.Name} at version 1 answers otherwise for {word}");
                }
                CollectionAssert.AreEqual(narrow, kind.Write(old),
                    $"{kind.Name} read at version 1 does not save as the version 9 payload of the same structure");
            }
        }

        /// <summary>
        /// Two count-min sketches of the same width placed by different schemes count the
        /// same item in different columns, so their sum describes neither, and a merge is
        /// refused, whichever way round, and leaves the sketch it was asked to change as it
        /// was. A top-k merges through its sketch and is refused the same way. Two placed
        /// the same way still merge, to exactly the sketch of everything both held: for a
        /// top-k, its sketch, since its heap is rebuilt from what either was tracking and
        /// is not the heap of the whole stream.
        /// </summary>
        [TestMethod]
        public void TestSketchesPlacedByDifferentSchemesRefuseToMerge()
        {
            var mergeable = new (string Name, Func<Placement?, object> Build, Action<object, object> Merge,
                Action<object, byte[]> Add, Func<object, byte[]> Write, Func<object, byte[]> Counts)[]
            {
                (nameof(CountMinSketch), p => new CountMinSketch(0.01, 0.01, null, p),
                    (a, b) => ((CountMinSketch)a).Merge((CountMinSketch)b),
                    (s, k) => ((CountMinSketch)s).Add(k), s => ((CountMinSketch)s).ToByteArray(),
                    s => ((CountMinSketch)s).ToByteArray()),
                (nameof(TopK), p => new TopK(0.01, 0.01, 5, null, p),
                    (a, b) => ((TopK)a).Merge((TopK)b),
                    (s, k) => ((TopK)s).Add(k), s => ((TopK)s).ToByteArray(),
                    s => ((TopK)s).Sketch.ToByteArray()),
            };

            foreach (var kind in mergeable)
            {
                var wide = kind.Build(Placement.Wide);
                var narrow = kind.Build(Placement.Narrow);
                kind.Add(wide, Key("left"));
                kind.Add(narrow, Key("right"));
                var wideBefore = kind.Write(wide);
                var narrowBefore = kind.Write(narrow);

                var ex = Assert.ThrowsExactly<ArgumentException>(() => kind.Merge(wide, narrow),
                    $"{kind.Name} placed with 64-bit values merged one placed with 32-bit values");
                StringAssert.Contains(ex.Message, "placed");
                ex = Assert.ThrowsExactly<ArgumentException>(() => kind.Merge(narrow, wide),
                    $"{kind.Name} placed with 32-bit values merged one placed with 64-bit values");
                StringAssert.Contains(ex.Message, "placed");

                CollectionAssert.AreEqual(wideBefore, kind.Write(wide), $"the refused merge changed the {kind.Name}");
                CollectionAssert.AreEqual(narrowBefore, kind.Write(narrow), $"the refused merge changed the {kind.Name}");

                foreach (var placement in new[] { Placement.Wide, Placement.Narrow })
                {
                    var left = kind.Build(placement);
                    var right = kind.Build(placement);
                    var both = kind.Build(placement);
                    for (var i = 0; i < 60; i++)
                    {
                        var key = Key($"union-{i % 13}");
                        kind.Add(i % 2 == 0 ? left : right, key);
                        kind.Add(both, key);
                    }

                    kind.Merge(left, right);
                    CollectionAssert.AreEqual(kind.Counts(both), kind.Counts(left),
                        $"two {kind.Name}s placed with {placement} merged into something other than their union");
                }
            }
        }

        // ---- The structures that hold sketches -----------------------------------------

        /// <summary>
        /// A top-k holds its count-min sketch in an envelope of its own, so the sketch
        /// carries its own version and placement byte and the top-k's layout is unchanged:
        /// it writes version 1 whichever way its sketch is placed, around a sketch at
        /// version 9 whose byte says how it was placed. It reads back with its sketch placed
        /// as it was, its elements and counts as they were, and writes the same bytes again;
        /// and a top-k whose sketch is at version 1 reads back placed with 32-bit values.
        /// </summary>
        [TestMethod]
        public void TestATopKKeepsItsSketchsPlacement()
        {
            foreach (var (placement, byteValue) in new[] { (Placement.Wide, (byte)1), (Placement.Narrow, (byte)0) })
            {
                var topK = new TopK(0.01, 0.01, 4, null, placement);
                for (var i = 0; i < Words.Length; i++)
                {
                    for (var j = 0; j <= i; j++)
                    {
                        topK.Add(Key(Words[i]));
                    }
                }

                var bytes = topK.ToByteArray();
                Assert.AreEqual((ushort)1, VersionOf(bytes), $"a top-k placed {placement} changed its own version");

                // u32 k, u32 n, then the sketch's u32 length and its envelope.
                var nested = bytes.AsSpan(Header + 12, (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(Header + 8))).ToArray();
                Assert.AreEqual(PlacementVersion, VersionOf(nested), $"the sketch of a top-k placed {placement}");
                Assert.AreEqual(byteValue, nested[Header + 24], $"the sketch of a top-k placed {placement}: its placement byte");
                CollectionAssert.AreEqual(nested, topK.Sketch.ToByteArray(),
                    "the top-k's nested sketch is not the sketch it holds, written on its own");

                var read = Persistence.FromByteArray<TopK>(bytes);
                Assert.AreEqual(placement, read.Sketch.Placement, $"a top-k placed {placement}, read back");
                CollectionAssert.AreEqual(
                    topK.Elements().Select(e => (Encoding.ASCII.GetString(e.Data.Span), e.Freq)).ToArray(),
                    read.Elements().Select(e => (Encoding.ASCII.GetString(e.Data.Span), e.Freq)).ToArray(),
                    $"a top-k placed {placement} read back holds other elements");
                foreach (var word in Words)
                {
                    Assert.AreEqual(topK.Sketch.Count(Key(word)), read.Sketch.Count(Key(word)),
                        $"a top-k placed {placement} read back counts {word} otherwise");
                }
                CollectionAssert.AreEqual(bytes, read.ToByteArray(), $"a top-k placed {placement} read back writes other bytes");

                if (placement == Placement.Narrow)
                {
                    var earlier = Downgraded(nested, 24);
                    var outer = Nest(bytes, earlier);
                    var old = Persistence.FromByteArray<TopK>(outer);
                    Assert.AreEqual(Placement.Narrow, old.Sketch.Placement, "a top-k whose sketch is at version 1");
                    CollectionAssert.AreEqual(bytes, old.ToByteArray(),
                        "a top-k whose sketch is at version 1 does not save as the same top-k with its sketch at version 9");
                }
            }
        }

        /// <summary>
        /// A DPSW window writes its private sketches inline, each as the private sketch's
        /// own body, so each body carries its sketch's placement byte and the window is at
        /// version 9 with them. A window whose sketches are placed with 64-bit values reads
        /// back with every one of them so, answering as it did; each body's byte is read on
        /// its own, so swapping one changes that sketch and no other; a byte naming no
        /// scheme is refused; and a window from before version 9 reads back placed with
        /// 32-bit values and saves again as version 9.
        /// </summary>
        [TestMethod]
        public void TestADpswWindowKeepsEachSketchsPlacement()
        {
            DpswSketch Build(Placement? placement)
            {
                var window = new DpswSketch(128, 1.0, 0.5, 0.7, 13, 2, 20260818, null, placement);
                for (var i = 0; i < 256; i++)
                {
                    window.Add(Key($"item-{i % 20}"));
                }
                return window;
            }

            // A body is u32 width, u32 depth, the placement byte, f64 rho, u64 count and
            // the counters.
            const int Body = 4 + 4 + 1 + 8 + 8 + (13 * 2 * 8);

            foreach (var (placement, byteValue) in new[] { (Placement.Wide, (byte)1), (Placement.Narrow, (byte)0) })
            {
                var window = Build(placement);
                var bytes = window.ToByteArray();
                Assert.AreEqual(PlacementVersion, VersionOf(bytes), $"a window placed {placement}");
                Assert.IsTrue(window.Sketches().All(s => s.Placement == placement), $"a window built placed {placement}");

                var bodies = window.SketchesHeld;
                var offsets = BodyOffsets(bytes, Body).ToArray();
                Assert.HasCount(bodies, offsets, "the bodies are not where this test expects them");
                foreach (var offset in offsets)
                {
                    Assert.AreEqual(byteValue, bytes[offset + 8], $"a body of a window placed {placement}: its placement byte");
                }

                var read = Persistence.FromByteArray<DpswSketch>(bytes);
                Assert.IsTrue(read.Sketches().All(s => s.Placement == placement), $"a window placed {placement}, read back");
                for (var i = 0; i < 20; i++)
                {
                    Assert.AreEqual(window.Count(Key($"item-{i}")), read.Count(Key($"item-{i}")),
                        $"a window placed {placement} read back answers otherwise for item-{i}");
                }
                CollectionAssert.AreEqual(bytes, read.ToByteArray(), $"a window placed {placement} read back writes other bytes");

                if (placement != Placement.Narrow)
                {
                    continue;
                }

                // One body swapped: that sketch reads the other way, and the rest do not.
                var swapped = Persistence.FromByteArray<DpswSketch>(WithByte(bytes, offsets[3] + 8, 1));
                CollectionAssert.AreEqual(
                    Enumerable.Range(0, bodies).Select(i => i == 3 ? Placement.Wide : Placement.Narrow).ToArray(),
                    swapped.Sketches().Select(s => s.Placement).ToArray(),
                    "swapping one body's placement byte did not change that sketch alone");

                foreach (var unknown in new byte[] { 2, 255 })
                {
                    var ex = Assert.ThrowsExactly<InvalidDataException>(
                        () => Persistence.FromByteArray<DpswSketch>(WithByte(bytes, offsets[^1] + 8, unknown)),
                        $"a window read a body whose placement byte is {unknown}");
                    StringAssert.Contains(ex.Message, "placement");
                    StringAssert.Contains(ex.Message, unknown.ToString());
                }

                var earlier = DowngradedBodies(bytes, offsets);
                Assert.AreEqual((ushort)1, VersionOf(earlier));
                var old = Persistence.FromByteArray<DpswSketch>(earlier);
                Assert.IsTrue(old.Sketches().All(s => s.Placement == Placement.Narrow), "a window at version 1");
                for (var i = 0; i < 20; i++)
                {
                    Assert.AreEqual(window.Count(Key($"item-{i}")), old.Count(Key($"item-{i}")),
                        $"a window at version 1 answers otherwise for item-{i}");
                }
                CollectionAssert.AreEqual(bytes, old.ToByteArray(),
                    "a window read at version 1 does not save as the version 9 payload of the same window");
            }
        }

        /// <summary>
        /// Where each body of a version 9 window starts, in the frame: after the window's
        /// header and each substream's start and count, a body at a time.
        /// </summary>
        private static IEnumerable<int> BodyOffsets(byte[] frame, int body)
        {
            const int Head = 8 + 8 + 8 + 4 + 4 + 4 + 8;
            var substreams = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(Header + Head));
            var payloadEnd = frame.Length - 4;
            var bodies = (payloadEnd - Header - Head - 4 - 12 * (int)substreams) / body;
            var plan = bodies / (int)substreams;
            var at = Header + Head + 4;
            for (var s = 0; s < substreams; s++)
            {
                at += 12;
                for (var p = 0; p < plan; p++)
                {
                    yield return at;
                    at += body;
                }
            }

            Assert.AreEqual(payloadEnd, at, "the window's payload does not end after its last body");
        }

        /// <summary>
        /// The frame with one byte changed and the checksum repaired, so that the byte is
        /// the only thing wrong with it.
        /// </summary>
        private static byte[] WithByte(byte[] frame, int offset, byte value)
        {
            var bytes = (byte[])frame.Clone();
            bytes[offset] = value;
            return Resealed(bytes);
        }

        private static byte[] Resealed(byte[] bytes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(bytes.Length - 4),
                Crc32.HashToUInt32(bytes.AsSpan(4, bytes.Length - 8)));
            return bytes;
        }

        /// <summary>
        /// The payload a release before version 9 wrote for the structure a version 9
        /// payload placed with 32-bit values holds: the same fields without the placement
        /// byte, at version 1, with the length and checksum to match.
        /// </summary>
        internal static byte[] Downgraded(byte[] frame, int placementOffset) =>
            DowngradedAt(frame, new[] { Header + placementOffset });

        /// <summary>A window's frame with the placement byte taken out of every body, at version 1.</summary>
        private static byte[] DowngradedBodies(byte[] frame, IEnumerable<int> bodies) =>
            DowngradedAt(frame, bodies.Select(b => b + 8));

        private static byte[] DowngradedAt(byte[] frame, IEnumerable<int> placements)
        {
            var drop = placements.ToHashSet();
            foreach (var at in drop)
            {
                Assert.AreEqual((byte)0, frame[at], "only a structure placed with 32-bit values was ever written before version 9");
            }

            var result = new List<byte>(frame.Length);
            for (var i = 0; i < frame.Length - 4; i++)
            {
                if (!drop.Contains(i))
                {
                    result.Add(frame[i]);
                }
            }

            result.AddRange(new byte[4]);
            var bytes = result.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), (uint)(bytes.Length - Header - 4));
            return Resealed(bytes);
        }

        /// <summary>
        /// A top-k's frame with its nested sketch replaced by another envelope, and the
        /// lengths and checksum to match.
        /// </summary>
        private static byte[] Nest(byte[] topK, byte[] sketch)
        {
            var runLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(topK.AsSpan(Header + 8));
            var result = new List<byte>();
            result.AddRange(topK.AsSpan(0, Header + 8).ToArray());
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)sketch.Length);
            result.AddRange(length);
            result.AddRange(sketch);
            result.AddRange(topK.AsSpan(Header + 12 + runLength).ToArray());
            var bytes = result.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), (uint)(bytes.Length - Header - 4));
            return Resealed(bytes);
        }
    }
}
