using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The registers a SetSketch hands an element's values to, pinned at register counts
    /// that are not powers of two.
    /// </summary>
    /// <remarks>
    /// An element draws an ascending run of values from a generator seeded with its hash
    /// and hands them to the registers in a shuffled order, one step of Fisher-Yates for
    /// each value. A stored sketch's registers are where its elements' shuffles sent their
    /// values, so a sketch read back merges with, compares with and counts the same
    /// elements as one built now only while each element's shuffle is the one it was. The
    /// persistence fixtures pin the shuffle at 512 registers. There a step's 32-bit
    /// multiply-and-shift agrees with a 64-bit one at the first step, and at later steps,
    /// whose bounds are not powers of two, parts from it in fewer than one draw in 2^24.
    /// Off a power of two it parts from the first step on, in about one draw in 2^33 / m,
    /// and nothing else in the suite noticed the one draw being swapped for the other.
    /// <para>
    /// The order expected here is worked out from a plain Fisher-Yates over an array of
    /// every register index, not the sketch's lazy one, over the library's generator and
    /// the 32-bit draw restated. Each step takes two values from the element's generator:
    /// the first for the hash value it hands on, the second for the register, which is
    /// i + floor(top32(value) * (m - i) / 2^32) swapped into place. The register values are
    /// read off the sketch. An element's run ascends, so the values it leaves fall along its
    /// walk, and sorting them gives each step its value; what is pinned is the register
    /// each step went to. Steps whose values tie are interchangeable, and so are the
    /// sketches they make.
    /// </para>
    /// <para>
    /// Each sketch holds one element and nothing else, so its lower bound is nought and the
    /// element walks nearly every register before its run passes one, which is where the
    /// sketch stops it.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestSetSketchShuffle
    {
        /// <summary>
        /// The 32-bit draw, restated: the top 32 bits of the value, multiplied by the bound
        /// and shifted down.
        /// </summary>
        internal static uint Narrow(ulong next, uint bound) =>
            (uint)(((ulong)(uint)(next >> 32) * bound) >> 32);

        /// <summary>
        /// A 64-bit draw: the high half of the value's 128-bit product with the bound. The
        /// sketch does not draw this way; the tests use it to show they would notice if it
        /// did.
        /// </summary>
        internal static uint Wide(ulong next, uint bound) => (uint)Math.BigMul(next, bound, out _);

        /// <summary>A hash that is the element itself, so an element is the generator's seed.</summary>
        private static readonly Func<ReadOnlySpan<byte>, ulong> AsSeed = BinaryPrimitives.ReadUInt64LittleEndian;

        /// <summary>
        /// The elements pinned at each size, and the first step at which a 64-bit draw would
        /// send the element somewhere else, or -1 if it never does. At 7 registers a draw
        /// parts from the 32-bit one about once in a billion, so no element there can tell
        /// them apart, and element 0 cannot at 1,000 or 12,345 either. Each larger size has
        /// an element that parts at the first step, which decides the register that gets the
        /// element's highest value, and one that parts a few steps in, each the first such
        /// found by searching the elements in order. At 1,000,003 registers even element 0
        /// parts, 16,947 steps into its walk. The test checks each of these before relying
        /// on it.
        /// </summary>
        private static readonly (int M, ulong Element, int PartsAt)[] Pinned =
        {
            (7, 0, -1), (7, 1, -1), (7, 2, -1),
            (1000, 0, -1), (1000, 6_922_392, 0), (1000, 11_392, 73),
            (12345, 0, -1), (12345, 446_578, 0), (12345, 11_470, 11),
            (1_000_003, 0, 16_947), (1_000_003, 1253, 0), (1_000_003, 577, 9),
        };

        /// <summary>
        /// The registers an element's first <paramref name="steps"/> steps go to, in order,
        /// with each step's register drawn by <paramref name="draw"/>.
        /// </summary>
        internal static int[] Walk(ulong element, int m, int steps, Func<ulong, uint, uint> draw)
        {
            var order = new int[m];
            for (var j = 0; j < m; j++)
            {
                order[j] = j;
            }

            var random = new SeededRandom(element);
            var walk = new int[steps];
            for (var i = 0; i < steps; i++)
            {
                random.Next();
                var k = i + (int)draw(random.Next(), (uint)(m - i));
                (order[i], order[k]) = (order[k], order[i]);
                walk[i] = order[i];
            }

            return walk;
        }

        /// <summary>
        /// The first step at which the 32-bit draw and a 64-bit one send an element to
        /// different registers, or -1 if they agree at all <paramref name="m"/>.
        /// </summary>
        private static int FirstParting(ulong element, int m)
        {
            var random = new SeededRandom(element);
            for (var i = 0; i < m; i++)
            {
                random.Next();
                var value = random.Next();
                if (Narrow(value, (uint)(m - i)) != Wide(value, (uint)(m - i)))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// A sketch of <paramref name="m"/> registers holding one element, and the registers
        /// it would have if the element's walk were the one each draw gives, with the
        /// values the sketch holds handed out along that walk in descending order.
        /// </summary>
        internal static (ushort[] Actual, ushort[] Narrow, ushort[] Wide) Registers(
            int m, ulong element, SetSketchVariant variant)
        {
            var sketch = new SetSketch(m, SetSketch.DefaultBase, SetSketch.DefaultRate, SetSketch.DefaultQ, AsSeed, variant);
            var bytes = new byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, element);
            sketch.Add(bytes);

            var actual = sketch.RegisterValues.ToArray();
            var values = actual.Where(v => v != 0).OrderByDescending(v => v).ToArray();
            ushort[] Along(int[] walk)
            {
                var expected = new ushort[m];
                for (var i = 0; i < walk.Length; i++)
                {
                    expected[walk[i]] = values[i];
                }

                return expected;
            }

            return (actual,
                Along(Walk(element, m, values.Length, Narrow)),
                Along(Walk(element, m, values.Length, Wide)));
        }

        /// <summary>
        /// Whether a sketch of 1,000,003 registers still sends element 1,253's first value to
        /// the register the 32-bit draw names, where a 64-bit draw would name the next one:
        /// the defect CAPACITY.md describes, checked to be there.
        /// </summary>
        internal static bool ShufflesWithThe32BitDraw()
        {
            var (actual, narrow, wide) = Registers(1_000_003, 1253, SetSketchVariant.SetSketch1);
            return FirstParting(1253, 1_000_003) == 0
                && actual.SequenceEqual(narrow) && !narrow.SequenceEqual(wide);
        }

        /// <summary>
        /// Every value of every pinned element goes to the register a plain Fisher-Yates
        /// over the 32-bit draw names, in both constructions, at sizes that are not powers
        /// of two.
        /// </summary>
        [TestMethod]
        public void TestAnElementsValuesGoWhereItsShuffleSendsThem()
        {
            var failures = new List<string>();
            foreach (var (m, element, partsAt) in Pinned)
            {
                Assert.AreEqual(partsAt, FirstParting(element, m),
                    $"m = {m:N0}, element {element:N0}: the first step at which a 64-bit draw parts from " +
                    "the 32-bit one is no longer the one recorded, so the element no longer tests what it is here for");

                foreach (var variant in new[] { SetSketchVariant.SetSketch1, SetSketchVariant.SetSketch2 })
                {
                    var (actual, narrow, wide) = Registers(m, element, variant);
                    var walked = narrow.Count(v => v != 0);
                    if (!actual.SequenceEqual(narrow))
                    {
                        var at = Enumerable.Range(0, m).First(j => actual[j] != narrow[j]);
                        failures.Add($"{variant}, m = {m:N0}, element {element:N0}: register {at:N0} holds " +
                            $"{actual[at]}, where the shuffle puts {narrow[at]}");
                    }

                    // The pin sees a 64-bit draw where the element can tell, and only there:
                    // its walk parts from the 32-bit one before the element's run ends, and
                    // the registers that walk would fill are not these.
                    if (partsAt >= 0)
                    {
                        Assert.IsTrue(partsAt < walked && !narrow.SequenceEqual(wide),
                            $"{variant}, m = {m:N0}, element {element:N0}: a 64-bit draw parts at step {partsAt} " +
                            $"of a walk of {walked:N0} and fills the same registers, so this element cannot " +
                            "tell the two draws apart");
                    }
                    else
                    {
                        CollectionAssert.AreEqual(narrow, wide,
                            $"{variant}, m = {m:N0}, element {element:N0}: the two draws never part, " +
                            "and yet fill different registers");
                    }

                    // The element walked most of the sketch, so most of the shuffle is pinned.
                    Assert.IsGreaterThan(m * 9 / 10, walked,
                        $"{variant}, m = {m:N0}, element {element:N0}: the element filled only {walked:N0} registers");
                }
            }

            Assert.IsEmpty(failures, "an element's values did not go where its shuffle sends them:\n" +
                string.Join("\n", failures));
        }
    }
}
