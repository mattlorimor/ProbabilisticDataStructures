using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The generator's streams: what a stream steps by, and the increments
    /// <c>SeededRandom.SplitIncrement</c> gives the streams split from one parent, which
    /// is how a <see cref="DpswSketch"/> keeps any two of its sketches from drawing the
    /// same noise. <see cref="TestDpswSketch"/> holds a window to using them.
    /// </summary>
    [TestClass]
    public class TestSeededRandomStreams
    {
        /// <summary>SplitMix64's own increment, which every generator here stepped by.</summary>
        private const ulong Gamma = 0x9E3779B97F4A7C15;

        /// <summary>
        /// A generator built without an increment steps by Gamma, as every one here always
        /// has, and so does the struct's default, rather than standing still. One built
        /// with an increment steps by that, so two built with the same one are one
        /// sequence at two places: the one a step behind draws next what the other draws
        /// now. An even increment is refused, since its stream would not visit every state.
        /// </summary>
        [TestMethod]
        public void TestAStreamStepsByItsOwnIncrement()
        {
            Assert.AreEqual(Gamma, new SeededRandom(5).Increment);
            Assert.AreEqual(Gamma, default(SeededRandom).Increment);

            var unset = default(SeededRandom);
            var first = unset.Next();
            Assert.AreNotEqual(first, unset.Next(), "a generator given no increment stands still");
            Assert.AreEqual(unchecked(2 * Gamma), unset.State);

            var increment = SeededRandom.SplitIncrement(7);
            Assert.AreNotEqual(Gamma, increment);

            var behind = new SeededRandom(100, increment);
            var ahead = new SeededRandom(unchecked(100 + increment), increment);
            Assert.AreEqual(increment, behind.Increment);

            behind.Next();
            Assert.AreEqual(unchecked(100 + increment), behind.State);
            Assert.AreEqual(ahead.Next(), behind.Next(),
                "two streams with one increment, a step apart, did not draw the same next value");

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SeededRandom(1, 2));
        }

        /// <summary>
        /// The increments are odd, have the 24 changes between adjacent bits Java's
        /// SplittableRandom asks of one, and are all different: over the first 2^18
        /// ordinals, and the last 2^10 below 2^62, where the reduction modulo 2^62 is.
        /// </summary>
        /// <remarks>
        /// Some ordinals mix to too few changes and are mended. Those must be there, or the
        /// mending ran nowhere and is asserted on air, and each must carry the second bit
        /// that keeps it apart from every increment that was not mended, and the 41 changes
        /// at least that mending gives. Measured: 3,976 of the 263,168 mended, the first at
        /// ordinal 30, and the fewest changes 25. Every count is odd: the changes inside the
        /// 62 bits have the parity of their two ends, and the low bit adds one.
        /// </remarks>
        [TestMethod]
        public void TestSplitIncrementsAreOddMixedAndAllDifferent()
        {
            var ordinals = Enumerable.Range(0, 1 << 18).Select(i => (ulong)i)
                .Concat(Enumerable.Range(1, 1 << 10).Select(i => (1UL << 62) - (ulong)i));

            var seen = new Dictionary<ulong, ulong>();
            var mended = 0;
            var fewest = 64;
            foreach (var ordinal in ordinals)
            {
                var increment = SeededRandom.SplitIncrement(ordinal);
                var changes = ulong.PopCount(increment ^ (increment >> 1));
                fewest = Math.Min(fewest, (int)changes);

                Assert.AreEqual(1UL, increment & 1, $"ordinal {ordinal} has an even increment, {increment:X16}");
                Assert.IsGreaterThanOrEqualTo(24UL, changes,
                    $"ordinal {ordinal}'s increment {increment:X16} has {changes} changes between adjacent bits");
                if ((increment & 2) != 0)
                {
                    mended++;
                    Assert.IsGreaterThanOrEqualTo(41UL, changes,
                        $"ordinal {ordinal}'s increment {increment:X16} was mended to only {changes} changes");
                }
                Assert.IsTrue(seen.TryAdd(increment, ordinal),
                    $"ordinals {seen.GetValueOrDefault(increment)} and {ordinal} have one increment, {increment:X16}");
            }

            Console.WriteLine($"[SPLIT] {seen.Count} ordinals, {mended} mended, fewest changes {fewest}");
            Assert.IsGreaterThan(0, mended, "no increment was mended, so the mending was never run");
            Assert.AreEqual(2UL, SeededRandom.SplitIncrement(30) & 2, "ordinal 30, the first mended, was not");
        }
    }
}
