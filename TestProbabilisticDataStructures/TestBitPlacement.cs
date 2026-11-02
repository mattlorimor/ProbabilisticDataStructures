using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// How the 32-bit filters turn an item's hash into positions, and the size at which a
    /// new one stops doing it with 32-bit values.
    /// </summary>
    /// <remarks>
    /// The 32-bit filters split a 64-bit hash into two 32-bit halves and take position
    /// <c>i</c> as <c>(low + i * high) mod m</c> in 32-bit arithmetic. Each such value
    /// is a uniform 32-bit number, and reducing one modulo an <c>m</c> that does not
    /// divide 2^32 gives the first <c>2^32 mod m</c> positions one more preimage than the
    /// rest. Below a few hundred million positions the difference is a rounding error;
    /// at three quarters of 2^32 the first quarter of the filter takes half of every
    /// item's bits, and a filter sized for 1% measured 1.42%. The cause, the measurement
    /// and the choice of 2^27 are in the commit that added these tests and in
    /// <c>Benchmarks/BloomPlacementStudy.cs</c>.
    /// <para>
    /// A filter of 2^27 positions or more is built with the 64-bit scheme
    /// <see cref="BloomFilter64"/> uses, and is written at format version 8, which says
    /// so. Everything smaller is placed, and written, exactly as it always was. What a
    /// payload was placed with is read from its version and never from its size: a
    /// version 1 payload of any size was placed with 32-bit values.
    /// </para>
    /// <para>
    /// No filter here is anywhere near the sizes the defect lives at. The placement
    /// itself is checked directly, without a filter, at the sizes the fold is worst; the
    /// constructors are checked at the threshold itself, which is 16 MiB of bits; and
    /// everything about what a filter placed with 64-bit values writes, reads and merges
    /// with is checked on small filters built with that placement through an internal
    /// constructor.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestBitPlacement
    {
        // Written out rather than read from the library, which is what is being checked.
        private const ulong Threshold = 1UL << 27;
        private const ulong TwoTo32 = 1UL << 32;

        private static readonly string[] Words =
            { "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa" };

        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        /// <summary>
        /// The 32-bit scheme, restated: the hash's halves, combined in 32-bit arithmetic.
        /// </summary>
        private static uint Narrow(byte[] data, uint i, uint m)
        {
            var sum = XxHash3.HashToUInt64(data);
            var low = (uint)sum;
            var high = (uint)(sum >> 32);
            return unchecked(low + high * i) % m;
        }

        /// <summary>
        /// The 64-bit scheme, restated: the hash, the hash of its eight little-endian
        /// bytes, combined in 64-bit arithmetic.
        /// </summary>
        private static uint Wide(byte[] data, uint i, uint m)
        {
            var first = XxHash3.HashToUInt64(data);
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, first);
            var second = XxHash3.HashToUInt64(bytes);
            return (uint)(unchecked(first + second * i) % m);
        }

        private static ushort VersionOf(byte[] frame) =>
            BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4));

        /// <summary>
        /// The frame with another format version in its header and the checksum repaired.
        /// </summary>
        private static byte[] Relabeled(byte[] frame, ushort version)
        {
            var bytes = (byte[])frame.Clone();
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), version);
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(bytes.Length - 4),
                Crc32.HashToUInt32(bytes.AsSpan(4, bytes.Length - 8)));
            return bytes;
        }

        /// <summary>
        /// The size threshold, straddled. Every size below it places with 32-bit values,
        /// as every filter always has, and every size from it up with 64-bit ones.
        /// </summary>
        [TestMethod]
        public void TestTheSixtyFourBitSchemeBeginsAtTwoToTheTwentySeven()
        {
            Assert.AreEqual(Placement.Narrow, BitPlacement.For(1), "one position");
            Assert.AreEqual(Placement.Narrow, BitPlacement.For(Threshold - 1), "one below the threshold");
            Assert.AreEqual(Placement.Wide, BitPlacement.For(Threshold), "the threshold");
            Assert.AreEqual(Placement.Wide, BitPlacement.For(Threshold + 1), "one above the threshold");
            Assert.AreEqual(Placement.Wide, BitPlacement.For(uint.MaxValue), "the largest 32-bit filter");
        }

        /// <summary>
        /// At every size the 64-bit scheme covers, an item's positions are spread evenly,
        /// where the 32-bit scheme crowds them into the start of the filter. The statistic
        /// is the share of positions below <c>2^32 mod m</c>, which is exactly where the
        /// fold puts its extra preimages: uniform placement puts <c>r / m</c> of them
        /// there, the fold <c>(floor(2^32 / m) + 1) r / 2^32</c>.
        /// </summary>
        /// <remarks>
        /// The sizes are where the fold is worst within each step of
        /// <c>floor(2^32 / m)</c>, which is where <c>2^32 mod m</c> is half of <c>m</c>:
        /// for the first step above the threshold, a few between, and the last, plus the
        /// size the defect was reported at and one near the top. The placement is the one
        /// the constructors choose for that size, so this fails for any size at which they
        /// would still fold. Each size first checks that the fold there is far enough from
        /// uniform for this sample to see it, so a size at which the two cannot be told
        /// apart cannot pass vacuously.
        /// </remarks>
        [TestMethod]
        public void TestALargeFilterSpreadsItsPositionsEvenly()
        {
            const int Items = 100_000;
            const uint K = 7;
            var hash = Defaults.GetDefaultHashFunction();

            var sizes = new[] { 31.5, 15.5, 7.5, 3.5, 1.5 }
                .Select(steps => (uint)(TwoTo32 / steps))
                .Append((uint)(0.75 * TwoTo32))
                .Append((uint)(0.97 * TwoTo32));

            foreach (var m in sizes)
            {
                var r = TwoTo32 % m;
                var uniform = (double)r / m;
                var folded = (double)(TwoTo32 / m + 1) * r / TwoTo32;
                var samples = (double)Items * K;
                var se = Math.Sqrt(uniform * (1 - uniform) / samples);

                Assert.IsGreaterThan(10 * se, Math.Abs(folded - uniform),
                    $"at m = {m} the fold ({folded:F5}) is too close to uniform ({uniform:F5}) " +
                    "for this sample to tell them apart");

                var placement = BitPlacement.For(m);
                long below = 0;
                for (var t = 0; t < Items; t++)
                {
                    var positions = Positions.Of(Key($"spread-{m}-{t}"), hash, placement, m);
                    for (uint i = 0; i < K; i++)
                    {
                        if (positions[i] < r)
                        {
                            below++;
                        }
                    }
                }

                var share = below / samples;
                Assert.IsLessThan(4 * se, Math.Abs(share - uniform),
                    $"at m = {m} ({(double)m / TwoTo32:F4} of 2^32) a share of {share:F5} of " +
                    $"positions fell below {r}, where uniform placement gives {uniform:F5} " +
                    $"and the 32-bit fold {folded:F5}");
            }
        }

        /// <summary>
        /// Each constructor chooses by the number of positions it reduces modulo -- the
        /// filter for the plain, counting and stable filters, the data region for the
        /// deletable one, a partition for the partitioned one -- and does so at the
        /// threshold, built for real: a filter there is 16 MiB of bits.
        /// </summary>
        /// <remarks>
        /// The item counts straddle the threshold as closely as the sizing allows, which
        /// is about ten bits a step at 1%. The stable filter takes its size directly, so
        /// it straddles exactly. The deletable and partitioned cases each include one
        /// whose filter is past the threshold while what it reduces modulo is not, which
        /// is what tells the right size from the obvious one.
        /// </remarks>
        [TestMethod]
        public void TestEachConstructorChoosesItsPlacementByItsSize()
        {
            var n = SmallestCountReaching(Threshold, 0.01);
            Assert.IsLessThan(Threshold, (ulong)Utils.OptimalM(n - 1, 0.01));

            Assert.AreEqual(Placement.Narrow, new BloomFilter(n - 1, 0.01).Placement, "BloomFilter below");
            Assert.AreEqual(Placement.Wide, new BloomFilter(n, 0.01).Placement, "BloomFilter at");

            Assert.AreEqual(Placement.Narrow, new CountingBloomFilter(n - 1, 1, 0.01).Placement, "CountingBloomFilter below");
            Assert.AreEqual(Placement.Wide, new CountingBloomFilter(n, 1, 0.01).Placement, "CountingBloomFilter at");

            // A filter some bits past the threshold, whose data region is past it with one
            // collision region and short of it by one with enough of them.
            var over = Utils.OptimalM(n + 1, 0.01) - (uint)Threshold;
            Assert.IsGreaterThan(1u, over);
            var deletable = new DeletableBloomFilter(n + 1, 1, 0.01);
            Assert.IsGreaterThanOrEqualTo(Threshold, (ulong)deletable.Capacity());
            Assert.AreEqual(Placement.Wide, deletable.Placement, "DeletableBloomFilter at");
            deletable = new DeletableBloomFilter(n + 1, over + 1, 0.01);
            Assert.AreEqual(Threshold - 1, (ulong)deletable.Capacity());
            Assert.AreEqual(Placement.Narrow, deletable.Placement,
                "DeletableBloomFilter chose by its whole size rather than its data region");
            deletable = null;

            var cells = (uint)Threshold;
            Assert.AreEqual(Placement.Narrow, new StableBloomFilter(cells - 1, 1, 0.01).Placement, "StableBloomFilter below");
            Assert.AreEqual(Placement.Wide, new StableBloomFilter(cells, 1, 0.01).Placement, "StableBloomFilter at");
            Assert.AreEqual(Placement.Wide, StableBloomFilter.NewDefaultStableBloomFilter(cells, 0.01).Placement,
                "NewDefaultStableBloomFilter at");
            Assert.AreEqual(Placement.Narrow, StableBloomFilter.NewUnstableBloomFilter(cells - 1, 0.01).Placement,
                "NewUnstableBloomFilter below");
            Assert.AreEqual(Placement.Wide, StableBloomFilter.NewUnstableBloomFilter(cells, 0.01).Placement,
                "NewUnstableBloomFilter at");

            // A rate of one half gives one hash function, so the partition is the filter.
            var single = SmallestCountReaching(Threshold, 0.5);
            Assert.AreEqual(Placement.Narrow, new PartitionedBloomFilter(single - 1, 0.5).Placement, "PartitionedBloomFilter below");
            var partitioned = new PartitionedBloomFilter(single, 0.5);
            Assert.AreEqual(1u, partitioned.K());
            Assert.AreEqual(Placement.Wide, partitioned.Placement, "PartitionedBloomFilter at");

            // Two hash functions, and a filter nearly twice the threshold in two partitions
            // each short of it.
            partitioned = new PartitionedBloomFilter(SmallestCountReaching(Threshold * 19 / 10, 0.3), 0.3);
            Assert.AreEqual(2u, partitioned.K());
            Assert.IsGreaterThan(Threshold, (ulong)partitioned.Capacity());
            Assert.IsLessThan(Threshold, (ulong)partitioned.Partitions[0].count);
            Assert.AreEqual(Placement.Narrow, partitioned.Placement,
                "PartitionedBloomFilter chose by its whole size rather than a partition's");
        }

        /// <summary>
        /// The bucket array's limit is the same whichever way a filter places: the counting
        /// and stable filters refuse a size past <c>Buckets.MaxCount</c> placed either way,
        /// against the same parameter and with the same words as the public constructor.
        /// </summary>
        /// <remarks>
        /// Through the public constructors this cannot tell the placements apart, because
        /// every size past the limit is far past 2^27 and so places with 64-bit values; a
        /// check skipped for 32-bit placement would pass every public test. The internal
        /// constructor reaches a 32-bit filter of that size, which is what a payload
        /// written before version 8 describes, so it is held here. Each is refused before
        /// anything is allocated.
        /// </remarks>
        [TestMethod]
        public void TestBothPlacementsRefuseWhatTheBucketLimitRefuses()
        {
            var limit = (ulong)Buckets.MaxCount(8);
            var n = SmallestCountReaching(limit + 1, 0.01);
            Assert.IsGreaterThan(Threshold, limit, "the limit is not past the threshold, so this checks nothing about it");

            var counting = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new CountingBloomFilter(n, 8, 0.01));
            Assert.AreEqual("n", counting.ParamName);
            foreach (var placement in new[] { Placement.Narrow, Placement.Wide })
            {
                var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => new CountingBloomFilter(n, 8, 0.01, null, placement),
                    $"CountingBloomFilter placed {placement} built past the bucket limit");
                Assert.AreEqual(counting.ParamName, ex.ParamName);
                Assert.AreEqual(counting.Message, ex.Message);
            }

            var cells = (uint)limit + 1;
            var stable = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new StableBloomFilter(cells, 8, 0.01, seed: 1));
            Assert.AreEqual("m", stable.ParamName);
            foreach (var placement in new[] { Placement.Narrow, Placement.Wide })
            {
                var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => new StableBloomFilter(cells, 8, 0.01, null, 1, placement),
                    $"StableBloomFilter placed {placement} built past the bucket limit");
                Assert.AreEqual(stable.ParamName, ex.ParamName);
                Assert.AreEqual(stable.Message, ex.Message);
            }
        }

        /// <summary>
        /// The smallest item count whose filter, at this rate, has at least this many bits.
        /// </summary>
        private static uint SmallestCountReaching(ulong bits, double fpRate)
        {
            uint low = 1, high = 2;
            while (Utils.OptimalM(high, fpRate) < bits)
            {
                low = high;
                high *= 2;
            }

            while (low < high)
            {
                var mid = low + (high - low) / 2;
                if (Utils.OptimalM(mid, fpRate) >= bits)
                {
                    high = mid;
                }
                else
                {
                    low = mid + 1;
                }
            }

            return low;
        }

        /// <summary>
        /// A filter placing with 64-bit values sets exactly the bits a
        /// <see cref="BloomFilter64"/> of the same size sets, which is the scheme it is
        /// defined to be and the one measured to deliver its rate at every size. A filter
        /// placing with 32-bit values does not, so the comparison can tell them apart.
        /// </summary>
        [TestMethod]
        public void TestAWideFilterSetsTheBitsBloomFilter64Sets()
        {
            var wide = new BloomFilter(500, 0.01, null, Placement.Wide);
            var narrow = new BloomFilter(500, 0.01);
            var reference = new BloomFilter64(500, 0.01);

            Assert.AreEqual(reference.Capacity(), (ulong)wide.Capacity());
            Assert.AreEqual(reference.K(), wide.K());

            for (var i = 0; i < 500; i++)
            {
                var key = Key($"same-{i}");
                wide.Add(key);
                narrow.Add(key);
                reference.Add(key);
            }

            var differ = 0;
            for (uint b = 0; b < wide.Capacity(); b++)
            {
                Assert.AreEqual(reference.Buckets.Get(b), wide.Buckets.Get(b), $"bit {b}");
                if (narrow.Buckets.Get(b) != wide.Buckets.Get(b))
                {
                    differ++;
                }
            }

            Assert.IsGreaterThan(0, differ,
                "the 32-bit filter set the same bits, so this comparison cannot tell the schemes apart");
        }

        /// <summary>
        /// One filter of each kind, empty, placed either way and small, with what it takes
        /// to use it without knowing its type.
        /// </summary>
        private sealed record Kind(
            string Name,
            Func<Placement?, object> Build,
            ushort NarrowVersion,
            Func<object, byte[]> Write,
            Func<byte[], object> Read,
            Func<object, Placement> PlacementOf,
            Action<object, byte[]> Add,
            Func<object, byte[], bool> Test,
            Func<object, IEnumerable<(uint Modulus, uint Index, uint Position, bool Set)>> Cells,
            Func<object, byte[], bool> TestAndAdd,
            Func<object, byte[], bool> TestAndRemove);

        private static IEnumerable<(uint, uint, uint, bool)> Bits(Buckets buckets, uint index = 0)
        {
            for (uint b = 0; b < buckets.count; b++)
            {
                yield return (buckets.count, index, b, buckets.Get(b) != 0);
            }
        }

        private static Kind[] Kinds() => new[]
        {
            new Kind(nameof(BloomFilter),
                p => new BloomFilter(100, 0.01, null, p), 1,
                f => ((BloomFilter)f).ToByteArray(),
                b => Persistence.FromByteArray<BloomFilter>(b),
                f => ((BloomFilter)f).Placement,
                (f, k) => ((BloomFilter)f).Add(k),
                (f, k) => ((BloomFilter)f).Test(k),
                f => Bits(((BloomFilter)f).Buckets),
                (f, k) => ((BloomFilter)f).TestAndAdd(k),
                null),
            new Kind(nameof(CountingBloomFilter),
                p => new CountingBloomFilter(100, 4, 0.01, null, p), 1,
                f => ((CountingBloomFilter)f).ToByteArray(),
                b => Persistence.FromByteArray<CountingBloomFilter>(b),
                f => ((CountingBloomFilter)f).Placement,
                (f, k) => ((CountingBloomFilter)f).Add(k),
                (f, k) => ((CountingBloomFilter)f).Test(k),
                f => Bits(((CountingBloomFilter)f).Buckets),
                (f, k) => ((CountingBloomFilter)f).TestAndAdd(k),
                (f, k) => ((CountingBloomFilter)f).TestAndRemove(k)),
            new Kind(nameof(DeletableBloomFilter),
                p => new DeletableBloomFilter(100, 10, 0.01, null, p), 1,
                f => ((DeletableBloomFilter)f).ToByteArray(),
                b => Persistence.FromByteArray<DeletableBloomFilter>(b),
                f => ((DeletableBloomFilter)f).Placement,
                (f, k) => ((DeletableBloomFilter)f).Add(k),
                (f, k) => ((DeletableBloomFilter)f).Test(k),
                f => Bits(((DeletableBloomFilter)f).Buckets),
                (f, k) => ((DeletableBloomFilter)f).TestAndAdd(k),
                (f, k) => ((DeletableBloomFilter)f).TestAndRemove(k)),
            new Kind(nameof(PartitionedBloomFilter),
                p => new PartitionedBloomFilter(100, 0.01, null, p), 1,
                f => ((PartitionedBloomFilter)f).ToByteArray(),
                b => Persistence.FromByteArray<PartitionedBloomFilter>(b),
                f => ((PartitionedBloomFilter)f).Placement,
                (f, k) => ((PartitionedBloomFilter)f).Add(k),
                (f, k) => ((PartitionedBloomFilter)f).Test(k),
                f => ((PartitionedBloomFilter)f).Partitions.SelectMany((partition, i) => Bits(partition, (uint)i)),
                (f, k) => ((PartitionedBloomFilter)f).TestAndAdd(k),
                null),
            // Seeded, so that its payload is reproducible. Its cells decay, so the cells
            // it reports are checked only for the item just added.
            new Kind(nameof(StableBloomFilter),
                p => new StableBloomFilter(1000, 2, 0.01, null, 7, p), 5,
                f => ((StableBloomFilter)f).ToByteArray(),
                b => Persistence.FromByteArray<StableBloomFilter>(b),
                f => ((StableBloomFilter)f).Placement,
                (f, k) => ((StableBloomFilter)f).Add(k),
                (f, k) => ((StableBloomFilter)f).Test(k),
                f => Bits(((StableBloomFilter)f).cells),
                (f, k) => ((StableBloomFilter)f).TestAndAdd(k),
                null),
        };

        /// <summary>
        /// Every kind of filter placed with 64-bit values puts each item exactly where the
        /// restated scheme says, among the positions it reduces modulo. For the plain,
        /// counting, deletable and partitioned filters the occupied positions are exactly
        /// the items' positions, nothing more; the stable filter decays cells as it goes,
        /// so it is held to the positions of the item it has just added. Placed with
        /// 32-bit values, the same items land elsewhere, so this can tell the two apart.
        /// </summary>
        [TestMethod]
        public void TestEveryWideFilterPutsItsItemsWhereTheSixtyFourBitSchemeSays()
        {
            foreach (var kind in Kinds())
            {
                var wide = kind.Build(Placement.Wide);
                var narrow = kind.Build(Placement.Narrow);
                var expectedWide = new HashSet<(uint, uint)>();
                var expectedNarrow = new HashSet<(uint, uint)>();
                var partitioned = kind.Name == nameof(PartitionedBloomFilter);
                var stable = kind.Name == nameof(StableBloomFilter);

                for (var w = 0; w < 40; w++)
                {
                    var key = Key($"where-{w}");
                    kind.Add(wide, key);
                    kind.Add(narrow, key);

                    var modulus = kind.Cells(wide).First().Modulus;
                    var k = (uint)(stable ? 3 : 7);
                    var mine = Enumerable.Range(0, (int)k)
                        .Select(i => (partitioned ? (uint)i : 0u, Wide(key, (uint)i, modulus)))
                        .ToArray();
                    var theirs = Enumerable.Range(0, (int)k)
                        .Select(i => (partitioned ? (uint)i : 0u, Narrow(key, (uint)i, modulus)))
                        .ToArray();
                    expectedWide.UnionWith(mine);
                    expectedNarrow.UnionWith(theirs);

                    var set = kind.Cells(wide).Where(c => c.Set).Select(c => (c.Index, c.Position)).ToHashSet();
                    foreach (var position in mine)
                    {
                        Assert.IsTrue(set.Contains(position),
                            $"{kind.Name} did not set {position} for {Encoding.ASCII.GetString(key)}");
                    }
                }

                if (stable)
                {
                    continue;
                }

                var occupied = kind.Cells(wide).Where(c => c.Set).Select(c => (c.Index, c.Position)).ToHashSet();
                Assert.IsTrue(occupied.SetEquals(expectedWide),
                    $"{kind.Name} placed with 64-bit values occupies positions no item put there");

                var narrowOccupied = kind.Cells(narrow).Where(c => c.Set).Select(c => (c.Index, c.Position)).ToHashSet();
                Assert.IsTrue(narrowOccupied.SetEquals(expectedNarrow),
                    $"{kind.Name} placed with 32-bit values is not where the restated 32-bit scheme says");
                Assert.IsFalse(narrowOccupied.SetEquals(occupied),
                    $"{kind.Name} put the same items in the same places either way, so this cannot tell them apart");
            }
        }

        /// <summary>
        /// Every operation of a filter placed with 64-bit values places that way, not only
        /// adding and testing: building it with <c>TestAndAdd</c> gives exactly the bytes
        /// <c>Add</c> gives, every item it holds answers as present to <c>TestAndAdd</c>,
        /// and the filters that remove remove every one of them -- the counting filter all
        /// the way back to empty.
        /// </summary>
        [TestMethod]
        public void TestEveryOperationOfAWideFilterPlacesTheSameWay()
        {
            foreach (var kind in Kinds())
            {
                var added = kind.Build(Placement.Wide);
                var tested = kind.Build(Placement.Wide);
                foreach (var word in Words)
                {
                    kind.Add(added, Key(word));
                    kind.TestAndAdd(tested, Key(word));
                }

                CollectionAssert.AreEqual(kind.Write(added), kind.Write(tested),
                    $"{kind.Name} built with TestAndAdd is not the one built with Add");

                foreach (var word in Words)
                {
                    Assert.IsTrue(kind.TestAndAdd(tested, Key(word)),
                        $"{kind.Name} holding {word} does not say so to TestAndAdd");
                }

                if (kind.TestAndRemove is null)
                {
                    continue;
                }

                foreach (var word in Words)
                {
                    Assert.IsTrue(kind.TestAndRemove(added, Key(word)),
                        $"{kind.Name} holding {word} did not find it to remove");
                }

                if (kind.Name == nameof(CountingBloomFilter))
                {
                    CollectionAssert.AreEqual(kind.Write(kind.Build(Placement.Wide)), kind.Write(added),
                        $"{kind.Name} with everything removed is not empty");
                }
            }
        }

        /// <summary>
        /// A filter placed with 64-bit values writes format version 8, which is what says
        /// so to a reader; a filter placed with 32-bit values writes the version it always
        /// did, which a library from before version 8 reads.
        /// </summary>
        [TestMethod]
        public void TestAWideFilterWritesVersionEightAndANarrowOneItsOwnVersion()
        {
            foreach (var kind in Kinds())
            {
                var wide = kind.Build(Placement.Wide);
                var narrow = kind.Build(Placement.Narrow);
                foreach (var word in Words)
                {
                    kind.Add(wide, Key(word));
                    kind.Add(narrow, Key(word));
                }

                Assert.AreEqual((ushort)8, VersionOf(kind.Write(wide)), $"{kind.Name} placed with 64-bit values");
                Assert.AreEqual(kind.NarrowVersion, VersionOf(kind.Write(narrow)), $"{kind.Name} placed with 32-bit values");
                Assert.AreEqual(kind.NarrowVersion, VersionOf(kind.Write(kind.Build(null))),
                    $"{kind.Name} built small through the constructor");
            }
        }

        /// <summary>
        /// A filter placed with 64-bit values reads back placed with 64-bit values, finds
        /// what it held, and writes the same bytes again.
        /// </summary>
        [TestMethod]
        public void TestAWideFilterReadsBackWide()
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
                    Assert.IsTrue(kind.Test(read, Key(word)), $"{kind.Name} read back does not find {word}");
                }
                CollectionAssert.AreEqual(bytes, kind.Write(read), $"{kind.Name} read back writes other bytes");
            }
        }

        /// <summary>
        /// A small payload labelled version 8 is read as placed with 64-bit values, and
        /// the same bytes labelled 1 as placed with 32-bit ones. The version decides, and
        /// the size does not: these filters are far below the threshold.
        /// </summary>
        [TestMethod]
        public void TestTheVersionAndNotTheSizeSaysHowAPayloadWasPlaced()
        {
            foreach (var kind in Kinds().Where(k => k.NarrowVersion == 1))
            {
                var empty = kind.Write(kind.Build(Placement.Narrow));
                Assert.AreEqual(Placement.Narrow, kind.PlacementOf(kind.Read(empty)), $"{kind.Name} at version 1");
                Assert.AreEqual(Placement.Wide, kind.PlacementOf(kind.Read(Relabeled(empty, 8))), $"{kind.Name} at version 8");
            }

            var stable = Kinds().Single(k => k.Name == nameof(StableBloomFilter));
            var cells = stable.Write(stable.Build(Placement.Narrow));
            Assert.AreEqual(Placement.Narrow, stable.PlacementOf(stable.Read(cells)), "StableBloomFilter at version 5");
            Assert.AreEqual(Placement.Wide, stable.PlacementOf(stable.Read(Relabeled(cells, 8))), "StableBloomFilter at version 8");
        }

        /// <summary>
        /// A payload written before version 8 was placed with 32-bit values whatever its
        /// size, and reads back that way: a filter at the threshold, placed with 32-bit
        /// values as a library before version 8 would have built it, writes the version it
        /// always did, reads back placed as it was, still finds what it held, and writes
        /// the same bytes again. Reading it as its size calls for would lose its items.
        /// The filters that merge are then offered a new filter of the same size, placed
        /// with 64-bit values, and refuse it.
        /// </summary>
        /// <remarks>
        /// 16 MiB of bits each, the smallest size the rule could be wrong about.
        /// </remarks>
        [TestMethod]
        public void TestAnOldPayloadOfAnySizeKeepsTheThirtyTwoBitScheme()
        {
            var n = SmallestCountReaching(Threshold, 0.01);
            var single = SmallestCountReaching(Threshold, 0.5);

            var large = new (string Name, Func<object> Build, Func<object> Fresh, ushort Version,
                Func<object, byte[]> Write, Func<byte[], object> Read, Func<object, Placement> PlacementOf,
                Action<object, byte[]> Add, Func<object, byte[], bool> Test, Action<object, object> Merge)[]
            {
                (nameof(BloomFilter),
                    () => new BloomFilter(n, 0.01, null, Placement.Narrow), () => new BloomFilter(n, 0.01), 1,
                    f => ((BloomFilter)f).ToByteArray(), b => Persistence.FromByteArray<BloomFilter>(b),
                    f => ((BloomFilter)f).Placement, (f, k) => ((BloomFilter)f).Add(k), (f, k) => ((BloomFilter)f).Test(k),
                    (into, from) => ((BloomFilter)into).Merge((BloomFilter)from)),
                (nameof(CountingBloomFilter),
                    () => new CountingBloomFilter(n, 1, 0.01, null, Placement.Narrow), () => new CountingBloomFilter(n, 1, 0.01), 1,
                    f => ((CountingBloomFilter)f).ToByteArray(), b => Persistence.FromByteArray<CountingBloomFilter>(b),
                    f => ((CountingBloomFilter)f).Placement, (f, k) => ((CountingBloomFilter)f).Add(k), (f, k) => ((CountingBloomFilter)f).Test(k),
                    (into, from) => ((CountingBloomFilter)into).Merge((CountingBloomFilter)from)),
                (nameof(DeletableBloomFilter),
                    () => new DeletableBloomFilter(n + 1, 1, 0.01, null, Placement.Narrow), () => new DeletableBloomFilter(n + 1, 1, 0.01), 1,
                    f => ((DeletableBloomFilter)f).ToByteArray(), b => Persistence.FromByteArray<DeletableBloomFilter>(b),
                    f => ((DeletableBloomFilter)f).Placement, (f, k) => ((DeletableBloomFilter)f).Add(k), (f, k) => ((DeletableBloomFilter)f).Test(k),
                    null),
                (nameof(PartitionedBloomFilter),
                    () => new PartitionedBloomFilter(single, 0.5, null, Placement.Narrow), () => new PartitionedBloomFilter(single, 0.5), 1,
                    f => ((PartitionedBloomFilter)f).ToByteArray(), b => Persistence.FromByteArray<PartitionedBloomFilter>(b),
                    f => ((PartitionedBloomFilter)f).Placement, (f, k) => ((PartitionedBloomFilter)f).Add(k), (f, k) => ((PartitionedBloomFilter)f).Test(k),
                    (into, from) => ((PartitionedBloomFilter)into).Merge((PartitionedBloomFilter)from)),
                (nameof(StableBloomFilter),
                    () => new StableBloomFilter((uint)Threshold, 1, 0.01, null, 7, Placement.Narrow), () => new StableBloomFilter((uint)Threshold, 1, 0.01), 5,
                    f => ((StableBloomFilter)f).ToByteArray(), b => Persistence.FromByteArray<StableBloomFilter>(b),
                    f => ((StableBloomFilter)f).Placement, (f, k) => ((StableBloomFilter)f).Add(k), (f, k) => ((StableBloomFilter)f).Test(k),
                    null),
            };

            foreach (var kind in large)
            {
                var old = kind.Build();
                foreach (var word in Words)
                {
                    kind.Add(old, Key(word));
                }

                var bytes = kind.Write(old);
                old = null;
                Assert.AreEqual(kind.Version, VersionOf(bytes), $"{kind.Name} placed with 32-bit values");

                var read = kind.Read(bytes);
                Assert.AreEqual(Placement.Narrow, kind.PlacementOf(read), $"{kind.Name} at the threshold, read back");
                foreach (var word in Words)
                {
                    Assert.IsTrue(kind.Test(read, Key(word)), $"{kind.Name} read back does not find {word}");
                }

                var again = kind.Write(read);
                Assert.IsTrue(bytes.AsSpan().SequenceEqual(again), $"{kind.Name} read back writes other bytes");
                bytes = null;
                again = null;

                if (kind.Merge is not null)
                {
                    var fresh = kind.Fresh();
                    Assert.AreEqual(Placement.Wide, kind.PlacementOf(fresh), $"a new {kind.Name} at the threshold");
                    var ex = Assert.ThrowsExactly<ArgumentException>(() => kind.Merge(read, fresh),
                        $"{kind.Name} merged a new filter placed with 64-bit values into an old one");
                    StringAssert.Contains(ex.Message, "placed");
                    ex = Assert.ThrowsExactly<ArgumentException>(() => kind.Merge(fresh, read),
                        $"{kind.Name} merged an old filter placed with 32-bit values into a new one");
                    StringAssert.Contains(ex.Message, "placed");
                }
            }
        }

        /// <summary>
        /// Two filters of the same size placed by different schemes describe different
        /// items with the same bits, so their union describes neither, and a merge is
        /// refused, whichever way round, and leaves the filter it was asked to change as
        /// it was. Two placed the same way still merge, to exactly the filter of
        /// everything both held.
        /// </summary>
        [TestMethod]
        public void TestFiltersPlacedByDifferentSchemesRefuseToMerge()
        {
            var mergeable = new (string Name, Func<Placement?, object> Build, Action<object, object> Merge,
                Action<object, byte[]> Add, Func<object, byte[]> Write)[]
            {
                (nameof(BloomFilter), p => new BloomFilter(200, 0.01, null, p),
                    (a, b) => ((BloomFilter)a).Merge((BloomFilter)b),
                    (f, k) => ((BloomFilter)f).Add(k), f => ((BloomFilter)f).ToByteArray()),
                (nameof(CountingBloomFilter), p => new CountingBloomFilter(200, 4, 0.01, null, p),
                    (a, b) => ((CountingBloomFilter)a).Merge((CountingBloomFilter)b),
                    (f, k) => ((CountingBloomFilter)f).Add(k), f => ((CountingBloomFilter)f).ToByteArray()),
                (nameof(PartitionedBloomFilter), p => new PartitionedBloomFilter(200, 0.01, null, p),
                    (a, b) => ((PartitionedBloomFilter)a).Merge((PartitionedBloomFilter)b),
                    (f, k) => ((PartitionedBloomFilter)f).Add(k), f => ((PartitionedBloomFilter)f).ToByteArray()),
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
                        var key = Key($"union-{i}");
                        kind.Add(i % 2 == 0 ? left : right, key);
                        kind.Add(both, key);
                    }

                    kind.Merge(left, right);
                    CollectionAssert.AreEqual(kind.Write(both), kind.Write(left),
                        $"two {kind.Name}s placed with {placement} merged into something other than their union");
                }
            }
        }
    }
}
