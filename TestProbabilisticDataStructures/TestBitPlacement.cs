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
    /// <see cref="BloomFilter64"/> uses. Everything smaller is placed exactly as it always
    /// was. Every save is format version 8, which records the placement in a byte of its
    /// own and the item count in 64 bits. What a payload was placed with is read from that
    /// byte, or, before version 8, is the 32-bit scheme, and never from its size: a
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

        /// <summary>
        /// The 64-bit scheme among a 64-bit count of positions, restated as
        /// <see cref="Wide"/> is, without narrowing the position.
        /// </summary>
        private static ulong Wide64(byte[] data, uint i, ulong m)
        {
            var first = XxHash3.HashToUInt64(data);
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, first);
            var second = XxHash3.HashToUInt64(bytes);
            return unchecked(first + second * i) % m;
        }

        /// <summary>
        /// The positions a 64-bit count of positions gives are the ones a 32-bit count gives,
        /// under both schemes, at every size both can hold: the filters that place through
        /// <see cref="Positions64"/> place exactly as they did through <see cref="Positions"/>,
        /// which their stored payloads were placed with.
        /// </summary>
        /// <remarks>
        /// The sizes straddle the threshold and reach the largest a <c>u32</c> holds, where
        /// the 32-bit scheme's reduction and the 64-bit scheme's narrowing to a <c>uint</c>
        /// are each about to be wrong. The positions are the first few, the tenth and the
        /// last a filter can have, since the step is multiplied by <c>i</c>.
        /// </remarks>
        [TestMethod]
        public void TestPositions64PlaceAsPositionsAtEverySizeAU32Holds()
        {
            var hash = Defaults.GetDefaultHashFunction();
            var sizes = new uint[]
            {
                1, 2, 3, 7, 959, (uint)Threshold - 1, (uint)Threshold, (uint)Threshold + 1,
                (3u << 30) | 7, uint.MaxValue - 1, uint.MaxValue,
            };
            var indices = new uint[] { 0, 1, 2, 6, 9, Utils.MaxHashCount - 1 };
            var compared = 0;

            foreach (var placement in new[] { Placement.Narrow, Placement.Wide })
            {
                foreach (var m in sizes)
                {
                    for (var t = 0; t < 64; t++)
                    {
                        var key = Key($"p64-{m}-{t}");
                        var thirtyTwo = Positions.Of(key, hash, placement, m);
                        var sixtyFour = Positions64.Of(key, hash, placement, m);
                        foreach (var i in indices)
                        {
                            Assert.AreEqual((ulong)thirtyTwo[i], sixtyFour[i], $"{placement}, m = {m}, key {t}, position {i}");
                            compared++;
                        }
                    }
                }
            }

            Assert.AreEqual(2 * sizes.Length * 64 * indices.Length, compared);
        }

        /// <summary>
        /// Placed with 64-bit values among more than 2^32 positions, an item's positions are
        /// the 64-bit scheme's without narrowing: <c>(first + i * second) mod m</c> in 64-bit
        /// arithmetic, restated here from XxHash3, as <see cref="BloomFilter64"/> places them.
        /// Narrowed to 32 bits anywhere, a position passes 2^32 never, and a filter past 2^32
        /// positions uses only its first 2^32.
        /// </summary>
        /// <remarks>
        /// The coarsest size is the most positions a 64-bit table holds, where nearly every
        /// position is past 2^32; 2^32 + 1 is the finest past it, where nearly none is, and a
        /// position below m is checked at every size. Both sides of 2^32 are required to be
        /// reached, so neither a narrowing nor a position that ignored m could pass.
        /// </remarks>
        [TestMethod]
        public void TestPositions64ReachPastTwoToTheThirtyTwo()
        {
            var hash = Defaults.GetDefaultHashFunction();
            var sizes = new[] { TwoTo32, TwoTo32 + 1, 5 * TwoTo32 + 3, (1UL << 40) + 7, Buckets64.MaxCount };
            var indices = new uint[] { 0, 1, 2, 6, 9, Utils.MaxHashCount - 1 };
            var above = 0;
            var below = 0;

            foreach (var m in sizes)
            {
                for (var t = 0; t < 64; t++)
                {
                    var key = Key($"p64-{m}-{t}");
                    var positions = Positions64.Of(key, hash, Placement.Wide, m);
                    foreach (var i in indices)
                    {
                        var position = positions[i];
                        Assert.AreEqual(Wide64(key, i, m), position, $"m = {m}, key {t}, position {i}");
                        Assert.IsLessThan(m, position, $"m = {m}, key {t}, position {i} is past the filter");
                        if (position > uint.MaxValue) above++; else below++;
                    }
                }
            }

            Assert.IsGreaterThan(0, above, "no position passed 2^32, so a narrowing would have passed");
            Assert.IsGreaterThan(0, below, "no position was below 2^32");
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
        /// filter refuses a size past <c>Buckets64.MaxCount</c>, and so does the stable filter,
        /// placed either way, against the same parameter and
        /// with the same words as the public constructor.
        /// </summary>
        /// <remarks>
        /// Through the public constructors this cannot tell the placements apart, because
        /// every size past the limit is far past 2^27 and so places with 64-bit values; a
        /// check skipped for 32-bit placement would pass every public test. The internal
        /// constructor reaches a 32-bit filter of that size, so it is held here. Each is
        /// refused before anything is allocated.
        /// </remarks>
        [TestMethod]
        public void TestBothPlacementsRefuseWhatTheBucketLimitRefuses()
        {
            var limit = Buckets64.MaxCount;
            var n = SmallestCountReaching64(limit + 1, 0.01);
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

            var cells = Buckets64.MaxCount + 1;
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
        /// The choice every constructor of a 64-bit filter makes (#315): 32-bit placement asked
        /// for is kept among as many positions as a <c>u32</c> counts and refused among one more,
        /// naming the parameter; 64-bit placement asked for is kept either side; and with none
        /// asked for, the choice is the one its size calls for, either side of the threshold and
        /// past the edge. Held at the edge itself, which no filter in the suite can be built at:
        /// at 4,294,967,295 positions and 4,294,967,296.
        /// </summary>
        [TestMethod]
        public void TestThirtyTwoBitPlacementIsKeptUpToWhatAUintCountsAndRefusedPastIt()
        {
            const ulong Edge = uint.MaxValue;
            Assert.AreEqual(Placement.Narrow, BitPlacement.Choose(Placement.Narrow, Edge, "placement"),
                "32-bit placement was not kept among as many positions as a uint counts");
            Assert.AreEqual(Placement.Narrow, BitPlacement.Choose(Placement.Narrow, 1, "placement"));
            var past = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => BitPlacement.Choose(Placement.Narrow, Edge + 1, "placement"),
                "32-bit placement was kept among more positions than a uint counts");
            Assert.AreEqual("placement", past.ParamName);
            StringAssert.Contains(past.Message, $"{Edge + 1:N0}", past.Message);

            foreach (var positions in new[] { 1UL, Edge, Edge + 1, ulong.MaxValue })
            {
                Assert.AreEqual(Placement.Wide, BitPlacement.Choose(Placement.Wide, positions, "placement"),
                    $"64-bit placement was not kept among {positions:N0} positions");
            }

            foreach (var positions in new[] { Threshold - 1, Threshold, Edge, Edge + 1 })
            {
                Assert.AreEqual(BitPlacement.For(positions), BitPlacement.Choose(null, positions, "placement"),
                    $"with no placement asked for, {positions:N0} positions were not placed as their size calls for");
            }
        }

        /// <summary>
        /// Every internal constructor of a filter whose table is a 64-bit one, asked to place
        /// with 32-bit values among more positions than a <c>u32</c> counts, refuses it, naming
        /// the placement and the positions, before it allocates anything (#315). The 32-bit
        /// scheme reduces modulo a 32-bit count of positions, so a filter built that way among
        /// 2^32 of them would reduce modulo nought, and among more modulo the few past 2^32,
        /// and its reader refuses a payload that says so
        /// (<c>TestAFilterPlacedWithThirtyTwoBitValuesPastTwoToTheThirtyTwoIsRefused</c>). Each
        /// is asked at the first size its sizing reaches past the edge.
        /// </summary>
        /// <remarks>
        /// The roster is the library's types with a non-public constructor taking a placement
        /// that hold a <see cref="Buckets64"/>, whose positions are counted in 64 bits; the
        /// others that take a placement count theirs in 32, which no size of theirs passes.
        /// Each is asked for one-bit buckets at a rate of one half, one hash function, so that
        /// the partitioned filter's one partition is its filter. Refused, each allocates
        /// nothing; built, each is half a gigabyte of buckets. That they still build with
        /// 32-bit placement where it holds their positions is
        /// <see cref="TestAnOldPayloadOfAnySizeKeepsTheThirtyTwoBitScheme"/>'s, at 2^27.
        /// </remarks>
        [TestMethod]
        public void TestEveryConstructorRefusesThirtyTwoBitPlacementAmongMorePositionsThanAUintCounts()
        {
            const ulong Past = (ulong)uint.MaxValue + 1;
            const double Rate = 0.5;
            Assert.AreEqual(1u, Utils.OptimalK(Rate), "premise: a rate of one half is not one hash function");
            var n = SmallestCountReaching64(Past, Rate);
            var m = Utils.OptimalM64(n, Rate);
            var deletable = SmallestCountReaching64(Past + 1, Rate);
            var builds = new Dictionary<Type, (ulong Positions, Func<object> Build)>
            {
                [typeof(CountingBloomFilter)] = (m, () => new CountingBloomFilter(n, 1, Rate, null, Placement.Narrow)),
                // One collision region, so that the data region, which it places among, is past the edge.
                [typeof(DeletableBloomFilter)] = (Utils.OptimalM64(deletable, Rate) - 1,
                    () => new DeletableBloomFilter(deletable, 1, Rate, null, Placement.Narrow)),
                [typeof(PartitionedBloomFilter)] = (PartitionedBloomFilter.PartitionSize(m, 1),
                    () => new PartitionedBloomFilter(n, Rate, null, Placement.Narrow)),
                [typeof(StableBloomFilter)] = (Past, () => new StableBloomFilter(Past, 1, Rate, null, 1, Placement.Narrow)),
            };

            var roster = typeof(BloomFilter).Assembly.GetTypes()
                .Where(t => t.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(Placement?))))
                .Where(t => t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .Any(f => f.FieldType == typeof(Buckets64) || f.FieldType == typeof(Buckets64[])))
                .Select(t => t.Name).Order().ToArray();
            CollectionAssert.AreEqual(roster, builds.Keys.Select(t => t.Name).Order().ToArray(),
                "the filters whose 64-bit tables a constructor can be asked to place with 32-bit values are not the ones asked here");

            var wrong = new List<string>();
            foreach (var (type, (positions, build)) in builds.OrderBy(b => b.Key.Name))
            {
                Assert.IsGreaterThan((ulong)uint.MaxValue, positions, $"premise: {type.Name} places among no more than a uint counts");
                Assert.IsLessThan(Past + 4, positions, $"premise: {type.Name} is asked far past the edge");
                try
                {
                    GC.KeepAlive(build());
                    wrong.Add($"{type.Name} was built placing {positions:N0} positions with 32-bit values");
                }
                catch (ArgumentOutOfRangeException e) when (e.ParamName == "placement")
                {
                    if (!e.Message.Contains($"{positions:N0}", StringComparison.Ordinal))
                    {
                        wrong.Add($"{type.Name}'s refusal does not name its {positions:N0} positions: {e.Message}");
                    }
                }
            }

            Assert.IsEmpty(wrong, string.Join("\n", wrong));
        }

        /// <summary>
        /// The smallest item count whose filter, sized in 64 bits at this rate, has at least
        /// this many bits.
        /// </summary>
        internal static ulong SmallestCountReaching64(ulong bits, double fpRate)
        {
            ulong low = 1, high = 2;
            while (Utils.OptimalM64(high, fpRate) < bits)
            {
                low = high;
                high *= 2;
            }

            while (low < high)
            {
                var mid = low + (high - low) / 2;
                if (Utils.OptimalM64(mid, fpRate) >= bits)
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
            ushort EarlierVersion,
            int PlacementOffset,
            int CountOffset,
            Func<object, byte[]> Write,
            Func<byte[], object> Read,
            Func<object, Placement> PlacementOf,
            Action<object, byte[]> Add,
            Func<object, byte[], bool> Test,
            Func<object, IEnumerable<(uint Modulus, uint Index, uint Position, bool Set)>> Cells,
            Func<object, byte[], bool> TestAndAdd,
            Func<object, byte[], bool> TestAndRemove)
        {
            /// <summary>
            /// The version every save of the filter is written at: 8, or 14 for the filters
            /// widened to 64-bit sizes (#232), whose offsets above are in that layout.
            /// </summary>
            internal ushort Version { get; init; } = 8;

            /// <summary>
            /// The fields of a version 14 payload, as <see cref="Narrowed"/> reads them, for a
            /// filter written at 14; null for one written at 8.
            /// </summary>
            internal string Layout14 { get; init; }

            /// <summary>
            /// The payload a release before version 8 wrote for the filter a current payload
            /// placed with 32-bit values holds: a version 14 payload is first narrowed to the
            /// version 8 one, whose placement byte and count are where the plain filter's are,
            /// or, for the partitioned and stable filters, four bytes later.
            /// </summary>
            internal byte[] Earlier(byte[] current)
            {
                if (this.Layout14 is null)
                {
                    return Downgraded(current, this.EarlierVersion, this.PlacementOffset, this.CountOffset);
                }

                var placement8 = this.PlacementOffset - 4 - (this.Layout14.StartsWith("mum", StringComparison.Ordinal) ? 4 : 0);
                return Downgraded(Narrowed(current, this.Layout14), this.EarlierVersion,
                    placement8, this.CountOffset < 0 ? -1 : placement8 + 1);
            }
        }

        private static IEnumerable<(uint, uint, uint, bool)> Bits(Buckets buckets, uint index = 0)
        {
            for (uint b = 0; b < buckets.count; b++)
            {
                yield return (buckets.count, index, b, buckets.Get(b) != 0);
            }
        }

        /// <summary>
        /// The bits of a 64-bit table, as <see cref="Bits(Buckets, uint)"/> gives a table's:
        /// the filters here are small, and a count past a <c>uint</c> would throw.
        /// </summary>
        private static IEnumerable<(uint, uint, uint, bool)> Bits(Buckets64 buckets, uint index = 0)
        {
            for (ulong b = 0; b < buckets.count; b++)
            {
                yield return (checked((uint)buckets.count), index, (uint)b, buckets.Get(b) != 0);
            }
        }

        /// <summary>
        /// The names of the filters these tests hold to their placement, for the roster
        /// of every structure that carries one.
        /// </summary>
        internal static IEnumerable<string> KindNames() => Kinds().Select(k => k.Name);

        private static Kind[] Kinds() => new[]
        {
            new Kind(nameof(BloomFilter),
                p => new BloomFilter(100, 0.01, null, p), 1, 8, 9,
                f => ((BloomFilter)f).ToByteArray(),
                b => Persistence.FromByteArray<BloomFilter>(b),
                f => ((BloomFilter)f).Placement,
                (f, k) => ((BloomFilter)f).Add(k),
                (f, k) => ((BloomFilter)f).Test(k),
                f => Bits(((BloomFilter)f).Buckets),
                (f, k) => ((BloomFilter)f).TestAndAdd(k),
                null),
            new Kind(nameof(CountingBloomFilter),
                p => new CountingBloomFilter(100, 4, 0.01, null, p), 1, 12, 13,
                f => ((CountingBloomFilter)f).ToByteArray(),
                b => Persistence.FromByteArray<CountingBloomFilter>(b),
                f => ((CountingBloomFilter)f).Placement,
                (f, k) => ((CountingBloomFilter)f).Add(k),
                (f, k) => ((CountingBloomFilter)f).Test(k),
                f => Bits(((CountingBloomFilter)f).Buckets),
                (f, k) => ((CountingBloomFilter)f).TestAndAdd(k),
                (f, k) => ((CountingBloomFilter)f).TestAndRemove(k)) { Version = 14, Layout14 = "mubqt" },
            new Kind(nameof(DeletableBloomFilter),
                p => new DeletableBloomFilter(100, 10, 0.01, null, p), 1, 12, 13,
                f => ((DeletableBloomFilter)f).ToByteArray(),
                b => Persistence.FromByteArray<DeletableBloomFilter>(b),
                f => ((DeletableBloomFilter)f).Placement,
                (f, k) => ((DeletableBloomFilter)f).Add(k),
                (f, k) => ((DeletableBloomFilter)f).Test(k),
                f => Bits(((DeletableBloomFilter)f).Buckets),
                (f, k) => ((DeletableBloomFilter)f).TestAndAdd(k),
                (f, k) => ((DeletableBloomFilter)f).TestAndRemove(k)) { Version = 14, Layout14 = "mubqtt" },
            new Kind(nameof(PartitionedBloomFilter),
                p => new PartitionedBloomFilter(100, 0.01, null, p), 1, 20, 21,
                f => ((PartitionedBloomFilter)f).ToByteArray(),
                b => Persistence.FromByteArray<PartitionedBloomFilter>(b),
                f => ((PartitionedBloomFilter)f).Placement,
                (f, k) => ((PartitionedBloomFilter)f).Add(k),
                (f, k) => ((PartitionedBloomFilter)f).Test(k),
                f => ((PartitionedBloomFilter)f).Partitions.SelectMany((partition, i) => Bits(partition, (uint)i)),
                (f, k) => ((PartitionedBloomFilter)f).TestAndAdd(k),
                null) { Version = 14, Layout14 = "mumbqn" },
            // Seeded, so that its payload is reproducible. Its cells decay, so the cells
            // it reports are checked only for the item just added.
            new Kind(nameof(StableBloomFilter),
                p => new StableBloomFilter(1000, 2, 0.01, null, 7, p), 5, 16, -1,
                f => ((StableBloomFilter)f).ToByteArray(),
                b => Persistence.FromByteArray<StableBloomFilter>(b),
                f => ((StableBloomFilter)f).Placement,
                (f, k) => ((StableBloomFilter)f).Add(k),
                (f, k) => ((StableBloomFilter)f).Test(k),
                f => Bits(((StableBloomFilter)f).cells),
                (f, k) => ((StableBloomFilter)f).TestAndAdd(k),
                null) { Version = 14, Layout14 = "muubtqq" },
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
        /// Every new save of these filters is written at the filter's format version, 8, or
        /// 14 for the filters widened to 64-bit sizes, whatever its size or placement, and its
        /// placement byte says which scheme placed it: 0 for 32-bit values, 1 for 64-bit ones.
        /// A small filter from the public constructor is no exception.
        /// </summary>
        [TestMethod]
        public void TestEveryNewSaveIsItsVersionAndRecordsItsPlacement()
        {
            foreach (var kind in Kinds())
            {
                var wide = kind.Build(Placement.Wide);
                var narrow = kind.Build(Placement.Narrow);
                var small = kind.Build(null);
                foreach (var word in Words)
                {
                    kind.Add(wide, Key(word));
                    kind.Add(narrow, Key(word));
                    kind.Add(small, Key(word));
                }

                foreach (var (filter, field, what) in new[]
                         { (wide, 1, "placed with 64-bit values"), (narrow, 0, "placed with 32-bit values"), (small, 0, "built small") })
                {
                    var bytes = kind.Write(filter);
                    Assert.AreEqual(kind.Version, VersionOf(bytes), $"{kind.Name} {what}");
                    Assert.AreEqual((byte)field, bytes[Header + kind.PlacementOffset], $"{kind.Name} {what}: its placement byte");
                }
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
        /// The placement byte, and not the size, says how a version 8 payload was placed:
        /// these filters are far below the threshold, and a payload whose byte is swapped
        /// reads back the other way, looking for its items where they were not put. A
        /// byte naming no scheme is refused with what it holds. A payload from before
        /// version 8 has no byte, was placed with 32-bit values, reads back that way and
        /// saves again as version 8.
        /// </summary>
        [TestMethod]
        public void TestThePlacementByteAndNotTheSizeSaysHowAPayloadWasPlaced()
        {
            foreach (var kind in Kinds())
            {
                var narrowFilter = kind.Build(Placement.Narrow);
                var wideFilter = kind.Build(Placement.Wide);
                foreach (var word in Words)
                {
                    kind.Add(narrowFilter, Key(word));
                    kind.Add(wideFilter, Key(word));
                }

                var narrow = kind.Write(narrowFilter);
                var wide = kind.Write(wideFilter);
                var at = Header + kind.PlacementOffset;

                Assert.AreEqual(Placement.Narrow, kind.PlacementOf(kind.Read(narrow)), $"{kind.Name} placed with 32-bit values");
                Assert.AreEqual(Placement.Wide, kind.PlacementOf(kind.Read(wide)), $"{kind.Name} placed with 64-bit values");

                foreach (var (bytes, swapped, expected) in new[]
                         { (narrow, (byte)1, Placement.Wide), (wide, (byte)0, Placement.Narrow) })
                {
                    var read = kind.Read(WithByte(bytes, at, swapped));
                    Assert.AreEqual(expected, kind.PlacementOf(read), $"{kind.Name} with its placement byte swapped");
                    Assert.IsTrue(Words.Any(word => !kind.Test(read, Key(word))),
                        $"{kind.Name} read the other way still finds every word, so the byte decided nothing");
                }

                foreach (var unknown in new byte[] { 2, 255 })
                {
                    var ex = Assert.ThrowsExactly<InvalidDataException>(
                        () => kind.Read(WithByte(narrow, at, unknown)),
                        $"{kind.Name} read a placement byte of {unknown}, which names no scheme");
                    StringAssert.Contains(ex.Message, "placement");
                    StringAssert.Contains(ex.Message, unknown.ToString());
                }

                var earlier = kind.Earlier(narrow);
                var old = kind.Read(earlier);
                Assert.AreEqual(Placement.Narrow, kind.PlacementOf(old), $"{kind.Name} at version {kind.EarlierVersion}");
                foreach (var word in Words)
                {
                    Assert.IsTrue(kind.Test(old, Key(word)), $"{kind.Name} at version {kind.EarlierVersion} does not find {word}");
                }
                CollectionAssert.AreEqual(narrow, kind.Write(old),
                    $"{kind.Name} read at version {kind.EarlierVersion} does not save as the version {kind.Version} payload of the same filter");
            }
        }

        /// <summary>
        /// A payload written before version 8 was placed with 32-bit values whatever its
        /// size, and reads back that way: a filter at the threshold, placed with 32-bit
        /// values as a library before version 8 would have built it and written in that
        /// library's layout, reads back placed as it was, still finds what it held, and
        /// saves again as the version 8 payload of the same filter, its placement byte
        /// saying 32-bit. Reading it as its size calls for would lose its items. The
        /// filters that merge are then offered a new filter of the same size, placed with
        /// 64-bit values, and refuse it.
        /// </summary>
        /// <remarks>
        /// 16 MiB of bits each, the smallest size the rule could be wrong about. The
        /// earlier payload is the version 8 one with its placement byte taken out and its
        /// count narrowed, which is exactly the earlier layout; the fixture tests hold that
        /// transformation to the bytes earlier releases wrote.
        /// </remarks>
        [TestMethod]
        public void TestAnOldPayloadOfAnySizeKeepsTheThirtyTwoBitScheme()
        {
            var n = SmallestCountReaching(Threshold, 0.01);
            var single = SmallestCountReaching(Threshold, 0.5);
            var kinds = Kinds().ToDictionary(k => k.Name);

            var large = new (string Name, Func<object> Build, Func<object> Fresh, Action<object, object> Merge)[]
            {
                (nameof(BloomFilter),
                    () => new BloomFilter(n, 0.01, null, Placement.Narrow), () => new BloomFilter(n, 0.01),
                    (into, from) => ((BloomFilter)into).Merge((BloomFilter)from)),
                (nameof(CountingBloomFilter),
                    () => new CountingBloomFilter(n, 1, 0.01, null, Placement.Narrow), () => new CountingBloomFilter(n, 1, 0.01),
                    (into, from) => ((CountingBloomFilter)into).Merge((CountingBloomFilter)from)),
                (nameof(DeletableBloomFilter),
                    () => new DeletableBloomFilter(n + 1, 1, 0.01, null, Placement.Narrow), () => new DeletableBloomFilter(n + 1, 1, 0.01),
                    null),
                (nameof(PartitionedBloomFilter),
                    () => new PartitionedBloomFilter(single, 0.5, null, Placement.Narrow), () => new PartitionedBloomFilter(single, 0.5),
                    (into, from) => ((PartitionedBloomFilter)into).Merge((PartitionedBloomFilter)from)),
                (nameof(StableBloomFilter),
                    () => new StableBloomFilter((uint)Threshold, 1, 0.01, null, 7, Placement.Narrow), () => new StableBloomFilter((uint)Threshold, 1, 0.01),
                    null),
            };

            foreach (var entry in large)
            {
                var kind = kinds[entry.Name];
                var built = entry.Build();
                foreach (var word in Words)
                {
                    kind.Add(built, Key(word));
                }

                var current = kind.Write(built);
                built = null;
                var earlier = kind.Earlier(current);
                Assert.AreEqual(kind.EarlierVersion, VersionOf(earlier));

                var read = kind.Read(earlier);
                earlier = null;
                Assert.AreEqual(Placement.Narrow, kind.PlacementOf(read), $"{kind.Name} at the threshold, read back");
                foreach (var word in Words)
                {
                    Assert.IsTrue(kind.Test(read, Key(word)), $"{kind.Name} read back does not find {word}");
                }

                var again = kind.Write(read);
                Assert.IsTrue(current.AsSpan().SequenceEqual(again),
                    $"{kind.Name} read back does not save as the version {kind.Version} payload of the same filter");
                current = null;
                again = null;

                if (entry.Merge is not null)
                {
                    var fresh = entry.Fresh();
                    Assert.AreEqual(Placement.Wide, kind.PlacementOf(fresh), $"a new {kind.Name} at the threshold");
                    var ex = Assert.ThrowsExactly<ArgumentException>(() => entry.Merge(read, fresh),
                        $"{kind.Name} merged a new filter placed with 64-bit values into an old one");
                    StringAssert.Contains(ex.Message, "placed");
                    ex = Assert.ThrowsExactly<ArgumentException>(() => entry.Merge(fresh, read),
                        $"{kind.Name} merged an old filter placed with 32-bit values into a new one");
                    StringAssert.Contains(ex.Message, "placed");
                }
            }
        }

        // ---- The item count ---------------------------------------------------------------

        private const ulong TwoToThe32 = 1UL << 32;

        /// <summary>
        /// Sets a filter's item count, which nothing public can, so that a count near 2^32
        /// does not cost 2^32 adds. Through reflection on the private property, so that it
        /// says plainly when the property cannot hold the value.
        /// </summary>
        private static void SetCount(object filter, ulong value)
        {
            var property = filter.GetType().GetProperty("count", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"{filter.GetType().Name} has no count");
            property.SetValue(filter, Convert.ChangeType(value, property.PropertyType));
        }

        /// <summary>
        /// The filters that count their items, with how to read the count and, for those
        /// that remove or merge, how.
        /// </summary>
        private static (string Name, Func<object> Build, Func<object, ulong> CountOf, int CountOffset,
            Func<object, byte[], bool> TestAndRemove, Action<object, object> Merge)[] Counted() => new (string, Func<object>, Func<object, ulong>, int, Func<object, byte[], bool>, Action<object, object>)[]
        {
            (nameof(BloomFilter), () => new BloomFilter(100, 0.01), f => ((BloomFilter)f).Count(), 9,
                null, (a, b) => ((BloomFilter)a).Merge((BloomFilter)b)),
            (nameof(CountingBloomFilter), () => new CountingBloomFilter(100, 4, 0.01), f => ((CountingBloomFilter)f).Count(), 13,
                (f, k) => ((CountingBloomFilter)f).TestAndRemove(k), (a, b) => ((CountingBloomFilter)a).Merge((CountingBloomFilter)b)),
            (nameof(DeletableBloomFilter), () => new DeletableBloomFilter(100, 10, 0.01), f => ((DeletableBloomFilter)f).Count(), 13,
                (f, k) => ((DeletableBloomFilter)f).TestAndRemove(k), null),
            (nameof(PartitionedBloomFilter), () => new PartitionedBloomFilter(100, 0.01), f => ((PartitionedBloomFilter)f).Count(), 21,
                null, (a, b) => ((PartitionedBloomFilter)a).Merge((PartitionedBloomFilter)b)),
        };

        /// <summary>
        /// A count crosses 2^32 rather than wrapping to zero: by Add and by TestAndAdd on
        /// the way up, by TestAndRemove back down for the filters that remove, and by the
        /// sum a merge takes. A 32-bit count wrapped after 2^32 adds, and a count that
        /// stuck there instead would under-report once a removal took it down again.
        /// </summary>
        [TestMethod]
        public void TestACountCrossesTwoToTheThirtyTwo()
        {
            foreach (var kind in Counted())
            {
                var filter = kind.Build();
                var ops = Kinds().Single(k => k.Name == kind.Name);
                SetCount(filter, TwoToThe32 - 1);

                ops.Add(filter, Key("past"));
                Assert.AreEqual(TwoToThe32, kind.CountOf(filter), $"{kind.Name}: Add at 2^32 - 1");
                ops.TestAndAdd(filter, Key("again"));
                Assert.AreEqual(TwoToThe32 + 1, kind.CountOf(filter), $"{kind.Name}: TestAndAdd at 2^32");

                if (kind.TestAndRemove is not null)
                {
                    Assert.IsTrue(kind.TestAndRemove(filter, Key("again")));
                    Assert.AreEqual(TwoToThe32, kind.CountOf(filter), $"{kind.Name}: TestAndRemove at 2^32 + 1");
                    Assert.IsTrue(kind.TestAndRemove(filter, Key("past")));
                    Assert.AreEqual(TwoToThe32 - 1, kind.CountOf(filter), $"{kind.Name}: TestAndRemove at 2^32, across it");
                }

                if (kind.Merge is not null)
                {
                    var into = kind.Build();
                    var from = kind.Build();
                    SetCount(into, TwoToThe32 - 1);
                    ops.Add(from, Key("one"));
                    ops.Add(from, Key("two"));
                    kind.Merge(into, from);
                    Assert.AreEqual(TwoToThe32 + 1, kind.CountOf(into), $"{kind.Name}: a merge summing past 2^32");
                }
            }
        }

        /// <summary>
        /// A count past 2^32 is saved as a u64 where FORMAT.md says it is, and reads back
        /// as itself, so a save and a load never loses the items a filter has counted.
        /// </summary>
        [TestMethod]
        public void TestACountPastTwoToTheThirtyTwoRoundTrips()
        {
            foreach (var kind in Counted())
            {
                var filter = kind.Build();
                var ops = Kinds().Single(k => k.Name == kind.Name);
                foreach (var word in Words)
                {
                    ops.Add(filter, Key(word));
                }

                SetCount(filter, TwoToThe32 + 7);
                var bytes = ops.Write(filter);
                Assert.AreEqual(TwoToThe32 + 7,
                    BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(Header + kind.CountOffset)),
                    $"{kind.Name} did not save its count as a u64 after its placement byte");

                var read = ops.Read(bytes);
                Assert.AreEqual(TwoToThe32 + 7, kind.CountOf(read), $"{kind.Name} read back");
                CollectionAssert.AreEqual(bytes, ops.Write(read), $"{kind.Name} read back writes other bytes");
            }
        }

        private const int Header = 14;

        /// <summary>
        /// The frame with one payload byte changed and the checksum repaired, so that the
        /// byte is the only thing wrong with it.
        /// </summary>
        private static byte[] WithByte(byte[] frame, int offset, byte value)
        {
            var bytes = (byte[])frame.Clone();
            bytes[offset] = value;
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(bytes.Length - 4),
                Crc32.HashToUInt32(bytes.AsSpan(4, bytes.Length - 8)));
            return bytes;
        }

        /// <summary>
        /// The payload a release before version 8 wrote for the filter a version 8 payload
        /// placed with 32-bit values holds: the same fields without the placement byte, the
        /// count, where there is one, back in a u32, at the version that layout had, with
        /// the length and checksum to match.
        /// </summary>
        internal static byte[] Downgraded(byte[] frame, ushort version, int placementOffset, int countOffset)
        {
            var payload = frame.AsSpan(Header, frame.Length - Header - 4);
            Assert.AreEqual((byte)0, payload[placementOffset],
                "only a filter placed with 32-bit values was ever written before version 8");

            var body = new List<byte>(payload.Length);
            body.AddRange(payload[..placementOffset].ToArray());
            if (countOffset < 0)
            {
                body.AddRange(payload[(placementOffset + 1)..].ToArray());
            }
            else
            {
                Assert.AreEqual(placementOffset + 1, countOffset, "the count does not follow the placement byte");
                var count = BinaryPrimitives.ReadUInt64LittleEndian(payload[countOffset..]);
                Assert.IsLessThanOrEqualTo((ulong)uint.MaxValue, count, "a count past 2^32 was never written before version 8");
                var four = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(four, (uint)count);
                body.AddRange(four);
                body.AddRange(payload[(countOffset + 8)..].ToArray());
            }

            var result = new byte[Header + body.Count + 4];
            frame.AsSpan(0, Header).CopyTo(result);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), version);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(10), (uint)body.Count);
            body.CopyTo(result, Header);
            BinaryPrimitives.WriteUInt32LittleEndian(
                result.AsSpan(result.Length - 4),
                Crc32.HashToUInt32(result.AsSpan(4, result.Length - 8)));
            return result;
        }

        /// <summary>
        /// The version 8 payload of the filter a version 14 payload holds: each size narrowed
        /// back to a <c>u32</c> and each <c>Buckets64</c> back to a <c>Buckets</c>, from
        /// FORMAT.md's two layouts and not from the library's reader, with the length and
        /// checksum to match. <paramref name="layout"/> lists the fields: <c>m</c> a size,
        /// a <c>u64</c> at 14 and a <c>u32</c> at 8; <c>u</c> a <c>u32</c>, <c>b</c> a
        /// <c>u8</c> and <c>q</c> a <c>u64</c>, the same at both; <c>t</c> a table; and
        /// <c>n</c> a <c>u32</c> count of the tables that follow.
        /// </summary>
        internal static byte[] Narrowed(byte[] frame, string layout)
        {
            Assert.AreEqual((ushort)14, VersionOf(frame), "only a version 14 payload narrows to version 8");
            var payload = frame.AsSpan(Header, frame.Length - Header - 4);
            var body = new List<byte>(payload.Length);
            var at = 0;

            void Table(ReadOnlySpan<byte> p)
            {
                var count = BinaryPrimitives.ReadUInt64LittleEndian(p[at..]);
                var width = p[at + 8];
                var arrays = BinaryPrimitives.ReadUInt32LittleEndian(p[(at + 9)..]);
                at += 13;
                Assert.IsLessThanOrEqualTo((ulong)uint.MaxValue, count, "a table past 2^32 buckets was never written before version 14");
                var data = new List<byte>();
                for (var i = 0; i < arrays; i++)
                {
                    var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(p[at..]);
                    data.AddRange(p.Slice(at + 4, length).ToArray());
                    at += 4 + length;
                }

                Assert.AreEqual((count * width + 7) / 8, (ulong)data.Count, "the table's arrays are not the bytes its count gives");
                body.AddRange(BitConverter.GetBytes((uint)count));
                body.Add(width);
                body.AddRange(BitConverter.GetBytes((uint)data.Count));
                body.AddRange(data);
            }

            foreach (var field in layout)
            {
                switch (field)
                {
                    case 'm':
                        var size = BinaryPrimitives.ReadUInt64LittleEndian(payload[at..]);
                        Assert.IsLessThanOrEqualTo((ulong)uint.MaxValue, size, "a size past 2^32 was never written before version 14");
                        body.AddRange(BitConverter.GetBytes((uint)size));
                        at += 8;
                        break;
                    case 'u':
                        body.AddRange(payload.Slice(at, 4).ToArray());
                        at += 4;
                        break;
                    case 'b':
                        body.Add(payload[at]);
                        at += 1;
                        break;
                    case 'q':
                        body.AddRange(payload.Slice(at, 8).ToArray());
                        at += 8;
                        break;
                    case 't':
                        Table(payload);
                        break;
                    case 'n':
                        var tables = BinaryPrimitives.ReadUInt32LittleEndian(payload[at..]);
                        body.AddRange(payload.Slice(at, 4).ToArray());
                        at += 4;
                        for (var i = 0; i < tables; i++) Table(payload);
                        break;
                    default:
                        throw new ArgumentException($"no field is written {field}", nameof(layout));
                }
            }

            Assert.AreEqual(payload.Length, at, "the layout does not cover the payload");
            var result = new byte[Header + body.Count + 4];
            frame.AsSpan(0, Header).CopyTo(result);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), 8);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(10), (uint)body.Count);
            body.CopyTo(result, Header);
            BinaryPrimitives.WriteUInt32LittleEndian(
                result.AsSpan(result.Length - 4),
                Crc32.HashToUInt32(result.AsSpan(4, result.Length - 8)));
            return result;
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
