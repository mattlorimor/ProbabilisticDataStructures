using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The light item a VarOpt eviction removes: how evenly the draw spreads over the light
    /// region, at regions far too large to build a sample of, and how much of the generator
    /// one eviction takes.
    /// </summary>
    /// <remarks>
    /// When a contest's drop falls to the light region, VarOpt evicts one of its items,
    /// chosen from the generator's next value. The items there are interchangeable, each
    /// standing for the threshold, and the estimate is unbiased because each is equally
    /// likely to go. Whether it is depends only on how many of the 2^64 values evict each
    /// slot, which <see cref="TestStableDecayDraw"/>'s boundary search counts exactly
    /// without drawing anything: a slot here is what a decay start is there, one value of a
    /// draw below a bound.
    /// <para>
    /// The 32-bit multiply-and-shift VarOpt drew with takes only the top 32 bits of the
    /// value, so its counts are multiples of 2^32: floor(2^32 / L) of them for a light
    /// region of L, or one more. Off a power of two that is every slot more than one value
    /// from its share, and once L passes 2^32 / 3 some slots were drawn half as often again
    /// as others. The evenness test runs the count on that draw too, restated, and requires
    /// it to come out uneven at every slot it looks at, and both draws to come out exact at
    /// a power of two: that is what shows the count can see an uneven draw at these sizes
    /// rather than passing because it cannot.
    /// </para>
    /// <para>
    /// A payload stores where the generator stands, so a restored sample resumes its
    /// sequence, and that position means the same thing only while every eviction takes
    /// exactly one value. A change that took none or two would leave every twin test
    /// agreeing, because both twins would make it.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestVarOptEvictionDraw
    {
        /// <summary>A signed percentage, with a zero that rounds from either side shown as 0%.</summary>
        private const string Percent = "+0.####%;-0.####%;0%";

        /// <summary>
        /// Light regions that are not powers of two: about 10^8; 178,956,961, the most
        /// 8-byte items a full sample can hold and still be saved; 0.4 * 2^32, where the
        /// 32-bit draw gave half the slots three of its 2^32 values and the other half two;
        /// and the largest sample there is, where it gave 116 slots three.
        /// </summary>
        private static readonly uint[] LargeLightCounts =
        {
            100_000_007, 178_956_961, 1_717_986_918, VarOpt.MaxK,
        };

        /// <summary>
        /// Each slot of the light region is evicted by floor(2^64 / L) of the 2^64 values or
        /// by one more, so every light item is within 2^-32 of an equal chance of going, at
        /// every large light region and not only at a power of two. The 32-bit draw gave
        /// each slot floor(2^32 / L) of 2^32 values or one more, which at 0.4 * 2^32 is a
        /// factor of one and a half between slots.
        /// </summary>
        [TestMethod]
        public void TestAnEvictionTakesEachLightItemEqually()
        {
            var failures = new List<string>();
            foreach (var lightCount in LargeLightCounts)
            {
                var (uneven, seen, worst) = TestStableDecayDraw.StartsOffTheirShare(VarOpt.EvictionSlot, lightCount);
                if (uneven > 0)
                {
                    failures.Add($"L = {lightCount:N0}: {uneven} of {seen} slots more than one value " +
                        $"from 2^64 / L, worst {worst.ToString(Percent)} of its share");
                }

                // The count sees an uneven draw: every sampled slot of the 32-bit one is off
                // its share by a multiple of 2^32 values, which is more than L.
                var narrow = TestStableDecayDraw.StartsOffTheirShare(TestStableDecayDraw.Narrow, lightCount);
                Assert.AreEqual(narrow.Checked, narrow.Uneven,
                    $"L = {lightCount:N0}: the 32-bit draw was counted as even at " +
                    $"{narrow.Checked - narrow.Uneven} slots, so this count cannot see the " +
                    "defect it is looking for");
            }

            // ...and an even one: at a power of two both draws give every slot exactly
            // 2^64 / L values.
            const uint powerOfTwo = 1u << 30;
            foreach (var draw in new Func<ulong, uint, uint>[] { VarOpt.EvictionSlot, TestStableDecayDraw.Narrow })
            {
                var counts = new TestStableDecayDraw.InputCounts(draw, powerOfTwo);
                foreach (var r in TestStableDecayDraw.SampledStarts(powerOfTwo))
                {
                    Assert.AreEqual(BigInteger.Zero, TestStableDecayDraw.Excess(counts.At(r), powerOfTwo, 1),
                        $"at a light region of 2^30, slot {r} was not evicted by exactly 2^34 values");
                }
            }

            Assert.IsEmpty(failures, "a light item is not evicted with an equal chance:\n" +
                string.Join("\n", failures));
        }

        private static byte[] Key(long i) => BitConverter.GetBytes(i);

        /// <summary>
        /// The generator's state as the payload stores it: after the 14-byte envelope header
        /// and the u32 k, as FORMAT.md lays a VarOpt payload out.
        /// </summary>
        private static ulong GeneratorState(VarOpt sample) =>
            BinaryPrimitives.ReadUInt64LittleEndian(sample.ToByteArray().AsSpan(14 + 4));

        /// <summary>
        /// How many values a generator standing where <paramref name="twin"/> does must
        /// draw to reach <paramref name="state"/>, trying no more than
        /// <paramref name="most"/>; -1 if that is not enough. The twin is left at the state
        /// when it is reached.
        /// </summary>
        private static int DrawsTo(ref SeededRandom twin, ulong state, int most)
        {
            var probe = twin;
            for (var drawn = 0; drawn <= most; drawn++)
            {
                if (probe.State == state)
                {
                    twin = probe;
                    return drawn;
                }

                probe.Next();
            }

            return -1;
        }

        /// <summary>
        /// An eviction from the light region takes exactly one value from the generator,
        /// at every size of light region, one included.
        /// </summary>
        /// <remarks>
        /// With every weight one, the transition to sampling pulls every item into the
        /// contest, and from then on each add is a contest between the new item and the
        /// light region, which holds everything kept. The contest takes one value. When the
        /// light region loses, which is exactly when the new item is kept, the eviction
        /// takes one more. So the generator moves one value for an add whose item is
        /// dropped and two for one whose item is kept. A sample of one is included because
        /// its light region has one slot, so its eviction draws a value that cannot change
        /// which item goes: skipping that draw would leave every sample of one as it was
        /// and move every draw after it.
        /// </remarks>
        [TestMethod]
        public void TestAnEvictionTakesOneValueFromTheGenerator()
        {
            var failures = new List<string>();
            foreach (var (k, seed) in new (uint K, ulong Seed)[] { (1, 203), (6, 204), (37, 205) })
            {
                var sample = new VarOpt(k, seed);
                var twin = new SeededRandom(seed);

                // The exact phase keeps every item as itself and draws nothing.
                for (var i = 0; i < k; i++)
                {
                    sample.Add(Key(i), 1.0);
                }

                Assert.AreEqual(twin.State, GeneratorState(sample),
                    $"k = {k}: the sample drew from its generator before it began sampling");

                // The transition is one contest, and an eviction if the light region,
                // seeded with one item, loses it.
                sample.Add(Key(k), 1.0);
                var atTransition = DrawsTo(ref twin, GeneratorState(sample), 2);
                Assert.IsTrue(atTransition is 1 or 2,
                    $"k = {k}: the transition to sampling moved the generator by " +
                    $"{(atTransition < 0 ? "more than 2" : atTransition.ToString())} values, not 1 or 2");

                int kept = 0, dropped = 0;
                for (long i = k + 1; i < k + 2000; i++)
                {
                    var key = Key(i);
                    sample.Add(key, 1.0);
                    var wasKept = sample.Samples().Any(e => e.Data.Span.SequenceEqual(key));
                    var expected = wasKept ? 2 : 1;
                    var moved = DrawsTo(ref twin, GeneratorState(sample), 3);
                    if (moved != expected)
                    {
                        failures.Add($"k = {k}, item {i}: {(wasKept ? "kept" : "dropped")}, and the " +
                            $"generator moved {(moved < 0 ? "more than 3" : moved.ToString())} values, not {expected}");
                        break;
                    }

                    if (wasKept)
                    {
                        kept++;
                    }
                    else
                    {
                        dropped++;
                    }
                }

                // Both kinds of add happened, so both counts were checked.
                Assert.IsTrue(failures.Count > 0 || (kept > 0 && dropped > 0),
                    $"k = {k}: {kept} adds were kept and {dropped} dropped, so one kind was never checked");
            }

            Assert.IsEmpty(failures, "an add moved the generator by the wrong number of values:\n" +
                string.Join("\n", failures));
        }
    }
}
