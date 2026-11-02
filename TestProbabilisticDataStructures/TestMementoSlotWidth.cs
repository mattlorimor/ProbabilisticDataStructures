using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// How wide a slot of a <see cref="MementoFilter"/> can be.
    /// </summary>
    /// <remarks>
    /// A slot is three bits of metadata and a field of <c>fingerprint + 1 + memento</c>
    /// bits, held in a ulong, and its mask was <c>(1UL &lt;&lt; bits) - 1</c>. C# takes a
    /// shift of a ulong mod 64, so for a slot of exactly 64 bits the mask was
    /// <c>(1 &lt;&lt; 0) - 1</c>, zero, and every write was masked to nothing. The filter
    /// counted each key and stored none: <c>Test</c> found no key it had been given.
    /// Measured on the tree this was written against, 200 keys added and tested:
    /// <code>
    ///   range 2^16, 8-bit fingerprint    slots of 28 bits   found 200 of 200
    ///   range 2^32, 24-bit fingerprint   slots of 60 bits   found 200 of 200
    ///   range 2^32, 28-bit fingerprint   slots of 64 bits   found   0 of 200   (Count 200)
    ///   range 2^28, 32-bit fingerprint   slots of 64 bits   found   0 of 200
    ///   range 2^32, 29-bit fingerprint   slots of 65 bits   found   1 of 200
    ///   range 2^32, 30-bit fingerprint   slots of 66 bits   InvalidOperationException, a table of
    ///                                                       2,048 slots that "is full"
    /// </code>
    /// A ulong holds a slot of 64 bits, so that is the widest the structure can work at
    /// once the mask is right; beyond it a slot would straddle three words and the
    /// arithmetic that reads and writes one holds a slot in two. So 64 bits is the limit,
    /// and the filter refuses past it.
    /// </remarks>
    [TestClass]
    public class TestMementoSlotWidth
    {
        private const ulong Golden = 0x9E3779B97F4A7C15UL;

        /// <summary>
        /// Every width the constructor accepts holds what it is given: two hundred keys
        /// in one block (the smallest keys, which share a prefix whenever the range is wide)
        /// and two hundred spread over the whole key space, all found, none of a set of
        /// absent keys found beyond the false positives a fingerprint of that size
        /// allows, every one removable, and the payload read back byte for byte. The
        /// widths at the top, where the slot is 64 bits or one under, are the ones whose
        /// mask was wrong or whose arithmetic is closest to it.
        /// </summary>
        [TestMethod]
        [DataRow(16, 8)]
        [DataRow(32, 24)]
        [DataRow(28, 28)]
        [DataRow(1, 32)]
        [DataRow(32, 27)]
        [DataRow(32, 28)]
        [DataRow(28, 32)]
        [DataRow(30, 30)]
        [DataRow(31, 29)]
        public void TestEverySlotWidthTheConstructorAcceptsHoldsItsKeys(int rangeBits, int fingerprintBits)
        {
            var range = 1UL << rangeBits;
            foreach (var spread in new[] { false, true })
            {
                var filter = new MementoFilter(range, fingerprintBits);
                ulong KeyOf(int i) => spread ? (ulong)i * Golden : (ulong)i * 7919;

                for (var i = 0; i < 200; i++)
                {
                    filter.Add(KeyOf(i));
                }

                var lost = Enumerable.Range(0, 200).Count(i => !filter.Test(KeyOf(i)));
                Assert.AreEqual(0, lost,
                    $"{lost} of 200 keys were not found in a filter with a {rangeBits}-bit range " +
                    $"and a {fingerprintBits}-bit fingerprint ({(spread ? "spread" : "one block")})");

                var falsePositives = Enumerable.Range(1000, 500).Count(i => filter.Test(KeyOf(i)));
                Assert.IsLessThanOrEqualTo(5, falsePositives);

                var bytes = filter.ToByteArray();
                var restored = Persistence.FromByteArray<MementoFilter>(bytes);
                CollectionAssert.AreEqual(bytes, restored.ToByteArray());
                Assert.AreEqual(0, Enumerable.Range(0, 200).Count(i => !restored.Test(KeyOf(i))));

                for (var i = 0; i < 200; i++)
                {
                    Assert.IsTrue(filter.TestAndRemove(KeyOf(i)), $"key {i} could not be removed");
                }

                Assert.AreEqual(0UL, filter.Count());
            }
        }

        /// <summary>
        /// A slot wider than a ulong is refused by the constructor, against the fingerprint
        /// width, with the width it came to and what to lower. The rows are 65 bits, the
        /// first width past, and 66, 67 and 68, the widest the parameters allow.
        /// </summary>
        [TestMethod]
        [DataRow(32, 29, 65)]
        [DataRow(32, 30, 66)]
        [DataRow(29, 32, 66)]
        [DataRow(32, 32, 68)]
        public void TestASlotWiderThanSixtyFourBitsIsRefusedNamingTheFingerprint(
            int rangeBits, int fingerprintBits, int slotBits)
        {
            var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new MementoFilter(1UL << rangeBits, fingerprintBits));
            Assert.AreEqual("fingerprintBits", ex.ParamName);
            StringAssert.Contains(ex.Message, slotBits.ToString());
            StringAssert.Contains(ex.Message, "64");
        }

        /// <summary>
        /// The reader refuses a table of slots wider than 64 bits, empty or not: no
        /// version built one that could hold a key, and an empty table of them is not a
        /// filter that works. Its message says what a slot holds.
        /// </summary>
        [TestMethod]
        [DataRow(30, 32, 66)]
        [DataRow(29, 32, 65)]
        [DataRow(32, 32, 68)]
        public void TestAPayloadWithSlotsWiderThanSixtyFourBitsIsRefused(
            int fingerprintBits, int mementoBits, int slotBits)
        {
            var payload = TestExpandingTableLimits.OnePayload(
                StructureId.MementoFilter, 4, fingerprintBits, includeWords: true, mementoBits);

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<MementoFilter>(payload));
            StringAssert.Contains(ex.Message, $"slots of {slotBits} bits");
        }

        /// <summary>
        /// The widest slot, 64 bits, is a filter: the payload of an empty one reads, and
        /// the filter it reads into takes a key and finds it.
        /// </summary>
        [TestMethod]
        public void TestAPayloadWithSlotsOfSixtyFourBitsReadsWhenItIsAFilter()
        {
            var empty = new MementoFilter(1UL << 32, 28, initialCapacity: 4).ToByteArray();
            var restored = Persistence.FromByteArray<MementoFilter>(empty);
            CollectionAssert.AreEqual(empty, restored.ToByteArray());

            restored.Add(12345);
            Assert.IsTrue(restored.Test(12345));
        }

        /// <summary>
        /// An InfiniFilter's slot is the fingerprint and four bits, so even at the widest
        /// fingerprint it is 36 bits and the mask cannot wrap there. Held so that the
        /// pattern is known not to be there, with a filter that has to hold its keys across
        /// expansions to show it.
        /// </summary>
        [TestMethod]
        public void TestAnInfiniFilterAtItsWidestFingerprintHoldsItsKeys()
        {
            var filter = new InfiniFilter(16, fingerprintBits: 32);
            for (var i = 0; i < 5000; i++)
            {
                filter.Add(BitConverter.GetBytes(i));
            }

            Assert.AreEqual(0, Enumerable.Range(0, 5000).Count(i => !filter.Test(BitConverter.GetBytes(i))));
            Assert.IsGreaterThan(0u, filter.ExpansionCount());
        }
    }
}
