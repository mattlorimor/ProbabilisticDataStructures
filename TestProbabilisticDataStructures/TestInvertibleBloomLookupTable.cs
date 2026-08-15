using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestInvertibleBloomLookupTable
    {
        private static byte[] Key(long i)
        {
            var key = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(key, i);
            return key;
        }

        private static long Value(byte[] key) => BinaryPrimitives.ReadInt64LittleEndian(key);

        [TestMethod]
        public void TestAnEmptyTableDecodesToNothing()
        {
            var table = new InvertibleBloomLookupTable(expectedDifferences: 100, keySize: 8);

            Assert.IsTrue(table.TryDecode(out var present, out var absent));
            Assert.HasCount(0, present);
            Assert.HasCount(0, absent);
        }

        [TestMethod]
        public void TestWhatWasAddedComesBackOut()
        {
            var table = new InvertibleBloomLookupTable(expectedDifferences: 100, keySize: 8);
            for (var i = 0; i < 50; i++)
            {
                table.Add(Key(i));
            }

            Assert.IsTrue(table.TryDecode(out var present, out var absent),
                "a table holding 50 keys, sized for 100, would not decode");

            CollectionAssert.AreEquivalent(
                Enumerable.Range(0, 50).Select(i => (long)i).ToArray(),
                present.Select(Value).ToArray());
            Assert.HasCount(0, absent);
        }
        /// <summary>
        /// The whole reason for the structure: two sets of a hundred thousand keys that
        /// differ by ten, reconciled by exchanging a table sized for ten.
        /// <para>
        /// Nothing else here can do this. A <see cref="ThetaSketch"/> will tell you the
        /// two sets differ by about ten; this hands you the ten keys.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestTwoLargeSetsDifferingSlightlyAreReconciledCheaply()
        {
            const int Shared = 100_000;

            var mine = new InvertibleBloomLookupTable(expectedDifferences: 20, keySize: 8);
            var theirs = new InvertibleBloomLookupTable(expectedDifferences: 20, keySize: 8);

            for (var i = 0; i < Shared; i++)
            {
                mine.Add(Key(i));
                theirs.Add(Key(i));
            }

            // Five only I have, five only they have.
            for (var i = 0; i < 5; i++)
            {
                mine.Add(Key(1_000_000 + i));
                theirs.Add(Key(2_000_000 + i));
            }

            var difference = mine.Subtract(theirs);

            Assert.IsTrue(difference.TryDecode(out var onlyMine, out var onlyTheirs),
                "a difference of ten would not decode from a table sized for twenty");

            CollectionAssert.AreEquivalent(
                Enumerable.Range(0, 5).Select(i => 1_000_000L + i).ToArray(),
                onlyMine.Select(Value).ToArray());
            CollectionAssert.AreEquivalent(
                Enumerable.Range(0, 5).Select(i => 2_000_000L + i).ToArray(),
                onlyTheirs.Select(Value).ToArray());

            // And the table that carried the answer is tiny next to the sets it came
            // from -- which is the property, not a nicety.
            Assert.IsLessThan(1000ul, difference.SizeInBytes(),
                $"the table took {difference.SizeInBytes()} bytes to reconcile two sets " +
                $"of {Shared}");
        }

        /// <summary>
        /// Sized for too little, it says so rather than returning part of the answer.
        /// A partial reconciliation that looked complete would be far worse than a
        /// refusal, since the caller would act on it.
        /// </summary>
        [TestMethod]
        public void TestTooManyDifferencesFailToDecodeRatherThanMislead()
        {
            var mine = new InvertibleBloomLookupTable(expectedDifferences: 10, keySize: 8);
            var theirs = new InvertibleBloomLookupTable(expectedDifferences: 10, keySize: 8);

            for (var i = 0; i < 1000; i++)
            {
                mine.Add(Key(i));
            }

            for (var i = 0; i < 1000; i++)
            {
                theirs.Add(Key(500_000 + i));
            }

            Assert.IsFalse(mine.Subtract(theirs).TryDecode(out _, out _),
                "a table sized for ten differences claimed to have decoded two thousand");
        }

        [TestMethod]
        public void TestSubtractingIdenticalSetsLeavesNothing()
        {
            var mine = new InvertibleBloomLookupTable(20, 8);
            var theirs = new InvertibleBloomLookupTable(20, 8);

            for (var i = 0; i < 5000; i++)
            {
                mine.Add(Key(i));
                theirs.Add(Key(i));
            }

            Assert.IsTrue(mine.Subtract(theirs).TryDecode(out var added, out var removed));
            Assert.HasCount(0, added);
            Assert.HasCount(0, removed);
        }

        /// <summary>
        /// Adding and removing the same key leaves the table as it was, which is what
        /// makes subtraction work at all.
        /// </summary>
        [TestMethod]
        public void TestRemovingUndoesAdding()
        {
            var table = new InvertibleBloomLookupTable(20, 8);

            table.Add(Key(1));
            table.Add(Key(2));
            table.Remove(Key(1));

            Assert.IsTrue(table.TryDecode(out var added, out var removed));
            CollectionAssert.AreEquivalent(new[] { 2L }, added.Select(Value).ToArray());
            Assert.HasCount(0, removed);

            // And removing something never added leaves it owed, which is how a
            // subtracted table represents "they have this and I do not".
            table.Remove(Key(9));
            Assert.IsTrue(table.TryDecode(out _, out var owed));
            CollectionAssert.AreEquivalent(new[] { 9L }, owed.Select(Value).ToArray());
        }

        [TestMethod]
        public void TestBadArgumentsAreRefused()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new InvertibleBloomLookupTable(0, 8));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new InvertibleBloomLookupTable(20, 0));

            var table = new InvertibleBloomLookupTable(20, 8);
            Assert.ThrowsExactly<ArgumentNullException>(() => table.Add(null!));

            // Keys are combined by exclusive-or, so a short one would corrupt the table
            // rather than merely be odd.
            var ex = Assert.ThrowsExactly<ArgumentException>(() => table.Add(new byte[4]));
            StringAssert.Contains(ex.Message, "same width");

            Assert.ThrowsExactly<ArgumentException>(
                () => table.Subtract(new InvertibleBloomLookupTable(20, 4)));
            Assert.ThrowsExactly<ArgumentException>(
                () => table.Subtract(new InvertibleBloomLookupTable(50, 8)));
            Assert.ThrowsExactly<ArgumentNullException>(() => table.Subtract(null!));
        }

        /// <summary>
        /// A table is meant to be sent somewhere, so a restored one has to subtract
        /// against a table built by whoever received it.
        /// </summary>
        [TestMethod]
        public void TestRoundTripsAndStillReconciles()
        {
            var mine = new InvertibleBloomLookupTable(20, 8);
            var theirs = new InvertibleBloomLookupTable(20, 8);

            for (var i = 0; i < 10_000; i++)
            {
                mine.Add(Key(i));
                theirs.Add(Key(i));
            }

            mine.Add(Key(999_001));
            theirs.Add(Key(999_002));

            var shipped = Persistence.FromByteArray<InvertibleBloomLookupTable>(mine.ToByteArray());

            Assert.AreEqual(mine.Cells(), shipped.Cells());
            Assert.AreEqual(mine.KeySize(), shipped.KeySize());

            Assert.IsTrue(shipped.Subtract(theirs).TryDecode(out var onlyMine, out var onlyTheirs),
                "a restored table would not reconcile");
            CollectionAssert.AreEquivalent(new[] { 999_001L }, onlyMine.Select(Value).ToArray());
            CollectionAssert.AreEquivalent(new[] { 999_002L }, onlyTheirs.Select(Value).ToArray());
        }

        [TestMethod]
        public void TestAnImpossiblePayloadIsRefused()
        {
            var clean = new InvertibleBloomLookupTable(20, 8).Add(Key(1)).ToByteArray();

            var bad = (byte[])clean.Clone();
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(14 + 4), 0);
            var crc = new System.IO.Hashing.Crc32();
            crc.Append(bad.AsSpan(4, bad.Length - 8));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                bad.AsSpan(bad.Length - 4), crc.GetCurrentHashAsUInt32());

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<InvertibleBloomLookupTable>(bad));
            StringAssert.Contains(ex.Message, "byte");
        }

    }
}
