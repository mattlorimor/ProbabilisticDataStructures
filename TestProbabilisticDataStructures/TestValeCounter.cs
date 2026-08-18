using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Tests for the variable-length counter encoding behind Sublime (Eslami, Bercea,
    /// Pagh and Dayan, SIGMOD 2026), section 4.1.
    /// </summary>
    /// <remarks>
    /// Extensions carry no lengths. Where one ends is known only from the delimiter,
    /// so a mistake in the encoding does not produce a value that is obviously wrong --
    /// it produces a pool that still decodes, into the wrong counters. Round-tripping
    /// across every shape of value, and packing several extensions together, is what
    /// catches that.
    /// </remarks>
    [TestClass]
    public class TestValeCounter
    {
        /// <summary>
        /// A pool large enough for any extension a 64-bit count can produce.
        /// </summary>
        private const int PoolBits = 512;

        /// <summary>
        /// The fragments an overflow is written as, in the order they are stored.
        /// </summary>
        private static byte[] Fragments(ulong overflow)
        {
            var pool = new ulong[PoolBits / 64];
            var bits = ValeCounter.WriteExtension(pool, 0, overflow);

            var fragments = new byte[bits / ValeCounter.FragmentBits];
            for (var f = 0; f < fragments.Length; f++)
            {
                fragments[f] = ValeCounter.FragmentAt(
                    pool, f * ValeCounter.FragmentBits);
            }
            return fragments;
        }

        /// <summary>
        /// A pool holding the given fragments, one after another.
        /// </summary>
        private static ulong[] PoolOf(params byte[] fragments)
        {
            var pool = new ulong[PoolBits / 64];
            for (var f = 0; f < fragments.Length; f++)
            {
                ValeCounter.SetFragment(
                    pool, f * ValeCounter.FragmentBits, fragments[f]);
            }
            return pool;
        }
        /// <summary>
        /// The paper's own worked example, which pins the encoding rather than merely
        /// checking it is self-consistent.
        /// </summary>
        /// <remarks>
        /// Section 4.1: a counter holding 21 in its stub and 5 above it stores that 5
        /// as the fragments drawn 01, 10, 11 -- the base-three digits two and one,
        /// least significant first, then the delimiter -- and decoding gives
        /// 3^0 * 2 + 3^1 * 1 = 5.
        /// <para>
        /// The drawn fragments are least significant bit first, so the one drawn 01
        /// holds the value two and the one drawn 10 holds one; the stored values are
        /// therefore 2, 1, 3. Reading the drawings the other way round gives an
        /// encoding that is perfectly self-consistent -- it round-trips, it packs, it
        /// passes every other test in this file -- and disagrees with every other
        /// implementation of the paper. Section 5 is what settles the direction: a stub
        /// holding 21 is drawn 111111 101010, and incrementing it is drawn
        /// 111111 011010, which is 22 only if the leftmost bit drawn is the lowest.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestThePapersWorkedExample()
        {
            var fragments = Fragments(5);

            CollectionAssert.AreEqual(
                new byte[] { 2, 1, ValeCounter.Delimiter }, fragments,
                "The paper encodes an overflow of five as the digits two then one, "
                + "drawn 01 10 and stored as 2 1, then the delimiter.");

            var decoded = ValeCounter.DecodeExtension(
                PoolOf(fragments), 0, PoolBits, out var bits);
            Assert.AreEqual(5UL, decoded);
            Assert.AreEqual(3 * ValeCounter.FragmentBits, bits);

            // And the whole counter, given a five-bit stub holding 21.
            Assert.AreEqual(21UL + (5UL << 5), ValeCounter.Rebuild(21, 5, 5));
        }

        /// <summary>
        /// Every overflow value up to a few thousand survives the round trip, which
        /// covers every extension length the encoding produces at that scale.
        /// </summary>
        [TestMethod]
        public void TestEveryOverflowRoundTrips()
        {
            for (ulong overflow = 0; overflow <= 5000; overflow++)
            {
                var fragments = Fragments(overflow);
                var decoded = ValeCounter.DecodeExtension(
                    PoolOf(fragments), 0, PoolBits, out var bits);

                Assert.AreEqual(overflow, decoded,
                    $"An overflow of {overflow} came back as {decoded}.");
                Assert.AreEqual(fragments.Length * ValeCounter.FragmentBits, bits,
                    $"Reading an overflow of {overflow} consumed {bits} bits where " +
                    $"it wrote {fragments.Length * ValeCounter.FragmentBits}.");
                Assert.AreEqual(fragments.Length, ValeCounter.ExtensionLength(overflow),
                    $"The predicted length for {overflow} disagrees with the encoding.");
            }
        }

        /// <summary>
        /// Extensions packed one after another are read back separately, which is the
        /// property the delimiter exists for and the reason a pool needs no index.
        /// </summary>
        [TestMethod]
        public void TestExtensionsPackedTogetherStaySeparate()
        {
            var values = new ulong[] { 0, 1, 2, 3, 8, 26, 27, 500, 6561, 99999 };

            var pool = new ulong[PoolBits / 64];
            var written = 0;
            foreach (var value in values)
            {
                written += ValeCounter.WriteExtension(pool, written, value);
            }

            var at = 0;
            foreach (var value in values)
            {
                var decoded = ValeCounter.DecodeExtension(
                    pool, at, PoolBits, out var bits);
                Assert.AreEqual(value, decoded,
                    $"The extension at bit {at} should hold {value}.");
                at += bits;
            }

            Assert.AreEqual(written, at,
                "Reading every extension should consume the pool exactly; a mismatch " +
                "means one of them was measured wrongly and the next began in the " +
                "middle of it.");
        }

        /// <summary>
        /// Powers of three and their neighbours are where an extension gains a digit,
        /// so they are where an off-by-one in the length shows up.
        /// </summary>
        [TestMethod]
        public void TestTheLengthBoundaries()
        {
            var expected = new (ulong Overflow, int Fragments)[]
            {
                (0, 2), (1, 2), (2, 2),
                (3, 3), (8, 3),
                (9, 4), (26, 4),
                (27, 5), (80, 5),
                (81, 6),
            };

            foreach (var (overflow, fragments) in expected)
            {
                Assert.AreEqual(fragments, ValeCounter.ExtensionLength(overflow),
                    $"An overflow of {overflow} should take {fragments} fragments, " +
                    "the delimiter included.");
                Assert.AreEqual(fragments, Fragments(overflow).Length);
            }
        }

        /// <summary>
        /// A counter that has not overflowed keeps its whole value in the stub, and
        /// one that has splits at the stub's width.
        /// </summary>
        [TestMethod]
        public void TestTheSplitBetweenStubAndExtension()
        {
            const int stubBits = 4;

            for (ulong count = 0; count < 16; count++)
            {
                Assert.IsFalse(ValeCounter.Overflows(count, stubBits),
                    $"{count} fits in four bits and should not overflow.");
                Assert.AreEqual(count, ValeCounter.StubOf(count, stubBits));
            }

            Assert.IsTrue(ValeCounter.Overflows(16, stubBits),
                "Sixteen is the first count a four-bit stub cannot hold.");

            for (ulong count = 16; count < 4096; count++)
            {
                var stub = ValeCounter.StubOf(count, stubBits);
                var overflow = ValeCounter.OverflowOf(count, stubBits);

                Assert.AreEqual(count, ValeCounter.Rebuild(stub, overflow, stubBits),
                    $"{count} did not survive being split and put back together.");
            }
        }

        /// <summary>
        /// A stub always fits the field it is stored in.
        /// </summary>
        /// <remarks>
        /// Rebuilding hides a stub that is one bit too wide, because that extra bit
        /// carries the same value as the bottom of the overflow and the two are
        /// combined with an or. A sketch would not hide it: the stub goes into a fixed
        /// field, and a value too wide for it is silently truncated on the way in. The
        /// property worth asserting is the one the field depends on, not the one
        /// arithmetic happens to preserve.
        /// </remarks>
        [TestMethod]
        public void TestAStubFitsItsField()
        {
            foreach (var stubBits in new[] { 1, 2, 4, 8, 15 })
            {
                var ceiling = 1UL << stubBits;

                for (ulong count = 0; count < 3000; count++)
                {
                    var stub = ValeCounter.StubOf(count, stubBits);
                    Assert.IsTrue(stub < ceiling,
                        $"A {stubBits}-bit stub holding {count} came out as {stub}, " +
                        $"which needs more than {stubBits} bits and would be " +
                        "truncated when stored.");
                }
            }
        }

        /// <summary>
        /// The delimiter is the one fragment that carries no digit, which is what
        /// makes base three rather than base four the right choice.
        /// </summary>
        [TestMethod]
        public void TestOnlyTheDelimiterEndsAnExtension()
        {
            // Every digit-bearing fragment appears in some encoding, and none of them
            // is the delimiter.
            var seen = new HashSet<byte>();
            for (ulong overflow = 0; overflow < 100; overflow++)
            {
                var fragments = Fragments(overflow);
                Assert.AreEqual(ValeCounter.Delimiter, fragments[^1],
                    $"The extension for {overflow} does not end in the delimiter.");

                for (var i = 0; i < fragments.Length - 1; i++)
                {
                    Assert.AreNotEqual(ValeCounter.Delimiter, fragments[i],
                        $"The extension for {overflow} contains a delimiter before " +
                        "its end, which would cut it short when read back.");
                    seen.Add(fragments[i]);
                }
            }

            Assert.AreEqual(3, seen.Count,
                "Three of the four fragment patterns carry digits; the fourth is the " +
                "delimiter. Using fewer would waste the space base three buys.");
        }

        /// <summary>
        /// A pool whose last extension has lost its delimiter is refused rather than
        /// read as though it ended where the pool does.
        /// </summary>
        [TestMethod]
        public void TestAnUnterminatedExtensionIsRefused()
        {
            var pool = PoolOf(2, 1);

            Assert.ThrowsExactly<InvalidOperationException>(
                () => ValeCounter.DecodeExtension(pool, 0, 2 * ValeCounter.FragmentBits, out _),
                "Without a delimiter there is no way to know the value ended, so " +
                "returning one would be inventing it.");
        }
    }
}
