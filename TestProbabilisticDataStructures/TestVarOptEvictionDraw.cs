using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The light item a VarOpt eviction removes: how much of the generator one eviction
    /// takes.
    /// </summary>
    /// <remarks>
    /// When a contest's drop falls to the light region, VarOpt evicts one of its items,
    /// chosen from the generator's next value. A payload stores where the generator stands,
    /// so a restored sample resumes its sequence, and that position means the same thing
    /// only while every eviction takes exactly one value. A change that took none or two
    /// would leave every twin test agreeing, because both twins would make it.
    /// </remarks>
    [TestClass]
    public class TestVarOptEvictionDraw
    {
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
