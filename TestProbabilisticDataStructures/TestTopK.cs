using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;
using System;
using System.Text;
using System.Linq;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestTopK
    {
        private static byte[] BOB_BYTES = Encoding.ASCII.GetBytes("bob");
        private static byte[] TYLER_BYTES = Encoding.ASCII.GetBytes("tyler");
        private static byte[] FRED_BYTES = Encoding.ASCII.GetBytes("fred");
        private static byte[] ALICE_BYTES = Encoding.ASCII.GetBytes("alice");
        private static byte[] JAMES_BYTES = Encoding.ASCII.GetBytes("james");
        private static byte[] SARA_BYTES = Encoding.ASCII.GetBytes("sara");
        private static byte[] BILL_BYTES = Encoding.ASCII.GetBytes("bill");

        /// <summary>
        /// Ensures that TopK return the top-k most frequent elements.
        /// </summary>
        [TestMethod]
        public void TestTopk()
        {
            var topK = new TopK(0.001, 0.99, 5);

            topK.Add(BOB_BYTES).Add(BOB_BYTES).Add(BOB_BYTES);
            topK.Add(TYLER_BYTES).Add(TYLER_BYTES).Add(TYLER_BYTES).Add(TYLER_BYTES).Add(TYLER_BYTES);
            topK.Add(FRED_BYTES);
            topK.Add(ALICE_BYTES).Add(ALICE_BYTES).Add(ALICE_BYTES).Add(ALICE_BYTES);
            topK.Add(JAMES_BYTES);
            topK.Add(FRED_BYTES);
            topK.Add(SARA_BYTES).Add(SARA_BYTES);

            var addedK = topK.Add(BILL_BYTES);
            Assert.AreSame(topK, addedK);

            // Counts are tyler 5, alice 4, bob 3, fred 2, sara 2, james 1, bill 1, so
            // the top five are everything above james and bill. This previously
            // expected bill, at a frequency of 1, in place of fred, at 2: the heap
            // evicted by position rather than by frequency, and the test recorded
            // whatever came out. fred and sara tie, so the order between those two is
            // not part of the contract and is not asserted.
            var actual = topK.Elements();

            Assert.HasCount(5, actual);

            var expectedFreqs = new ulong[] { 2, 2, 3, 4, 5 };
            CollectionAssert.AreEqual(expectedFreqs, actual.Select(e => e.Freq).ToArray(),
                "Elements returns the top k ordered from lowest to highest frequency");

            var expectedNames = new[] { "alice", "bob", "fred", "sara", "tyler" };
            var actualNames = actual
                .Select(e => Encoding.ASCII.GetString(e.Data.Span))
                .OrderBy(x => x, System.StringComparer.Ordinal)
                .ToArray();
            CollectionAssert.AreEqual(expectedNames, actualNames,
                "the five most frequent elements, and no others");

            var resetK = topK.Reset();
            Assert.AreSame(topK, resetK);

            Assert.IsEmpty(topK.Elements());
            Assert.AreEqual(0u, topK.N);
        }

        // ---- The count of items added is 64-bit ------------------------------------------

        private const int PayloadStart = 14;

        /// <summary>
        /// A payload of the given structure, edited so that it says it has seen
        /// <paramref name="sketchCount"/> items by its sketch and <paramref name="stored"/>
        /// by its own count, which is how a structure that has been running for four billion
        /// adds is reached without them. Both checksums are repaired, so what the reader
        /// has to say about it is its own.
        /// </summary>
        private static byte[] SeenItems(byte[] payload, ulong sketchCount, uint stored)
        {
            var bytes = (byte[])payload.Clone();

            // u32 k, u32 n, then the sketch as a length-prefixed envelope of its own: a
            // 14-byte header, and in its payload f64 epsilon, f64 delta, u32 width, u32
            // depth, the placement byte and the u64 count.
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(PayloadStart + 4), stored);
            var nestedLength = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(PayloadStart + 8));
            var nested = PayloadStart + 12;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                bytes.AsSpan(nested + PayloadStart + 8 + 8 + 4 + 4 + 1), sketchCount);

            RepairChecksum(bytes.AsSpan(nested, nestedLength));
            RepairChecksum(bytes);
            return bytes;
        }

        private static void RepairChecksum(System.Span<byte> envelope)
        {
            var crc = new System.IO.Hashing.Crc32();
            crc.Append(envelope.Slice(4, envelope.Length - 8));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                envelope.Slice(envelope.Length - 4), crc.GetCurrentHashAsUInt32());
        }

        private static TopK AfterSeeing(ulong items)
        {
            var seed = new TopK(0.01, 0.01, 3);
            seed.Add(BOB_BYTES);
            return Persistence.FromByteArray<TopK>(
                SeenItems(seed.ToByteArray(), items, (uint)System.Math.Min(items, uint.MaxValue)));
        }

        /// <summary>
        /// The count of items passes four billion without wrapping, and is written
        /// saturated and read back exact. It was a <c>uint</c> that went to zero at 2^32
        /// and was written that way, a count of nothing in a structure that had seen four
        /// billion items. The sketch inside has counted them in 64 bits all along, so the
        /// count is read from it.
        /// </summary>
        [TestMethod]
        public void TestTheItemCountDoesNotWrapAtFourBillion()
        {
            var topK = AfterSeeing(uint.MaxValue - 1UL);
            Assert.AreEqual(uint.MaxValue - 1UL, topK.N);

            topK.Add(BOB_BYTES).Add(BOB_BYTES).Add(BOB_BYTES);

            const ulong expected = uint.MaxValue + 2UL;
            Assert.AreEqual(expected, topK.N,
                "three adds from 2^32 - 2 are 2^32 + 1, and a count that wrapped says 1");

            // The stored field is 32 bits, so it holds what fits and no more.
            var written = topK.ToByteArray();
            Assert.AreEqual(uint.MaxValue,
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    written.AsSpan(PayloadStart + 4)),
                "past the 32-bit field the stored count stays at its largest");

            // And what is read back is the real count, from the sketch.
            var restored = Persistence.FromByteArray<TopK>(written);
            Assert.AreEqual(expected, restored.N);
            CollectionAssert.AreEqual(written, restored.ToByteArray(),
                "a structure past four billion items did not write what it read");
        }

        /// <summary>
        /// Merging adds the counts, and past four billion between them it does not wrap.
        /// </summary>
        [TestMethod]
        public void TestMergingCountsPastFourBillionItems()
        {
            var big = AfterSeeing(uint.MaxValue - 1UL);
            var small = AfterSeeing(5);

            big.Merge(small);

            Assert.AreEqual(uint.MaxValue + 4UL, big.N,
                "2^32 - 2 and 5 items are 2^32 + 3");
        }

        /// <summary>
        /// A top-k merged into itself doubles the sketch inside it, and an element held
        /// is read back from that sketch, so its frequency doubles with it and, once the
        /// sketch's counters stop at the largest count, stops there. Wrapped, the 64th
        /// merge read the held element back at nought. Neither the element's frequency nor
        /// the item count falls on the way, and an add after the last leaves both at the
        /// largest.
        /// </summary>
        [TestMethod]
        public void TestSelfMergesNeverLowerAHeldElementsFrequency()
        {
            var topK = new TopK(0.01, 0.01, 3);
            topK.Add(BOB_BYTES);

            var held = topK.Elements().Single();
            CollectionAssert.AreEqual(BOB_BYTES, held.Data.ToArray());
            var frequency = held.Freq;
            var items = topK.N;
            Assert.AreEqual(1UL, frequency);
            Assert.AreEqual(1UL, items);

            for (var merge = 1; merge <= 64; merge++)
            {
                topK.Merge(topK);

                var expected = merge < 64 ? 1UL << merge : ulong.MaxValue;
                var element = topK.Elements().Single();
                CollectionAssert.AreEqual(BOB_BYTES, element.Data.ToArray(),
                    $"the element held after merge {merge}");

                Assert.IsGreaterThanOrEqualTo(frequency, element.Freq,
                    $"merge {merge} lowered the held element's frequency from {frequency} to {element.Freq}");
                Assert.IsGreaterThanOrEqualTo(items, topK.N,
                    $"merge {merge} lowered the item count from {items} to {topK.N}");
                Assert.AreEqual(expected, element.Freq, $"the held element's frequency after merge {merge}");
                Assert.AreEqual(expected, topK.N, $"the item count after merge {merge}");

                frequency = element.Freq;
                items = topK.N;
            }

            Assert.AreEqual(ulong.MaxValue, frequency);
            Assert.AreEqual(ulong.MaxValue, items);

            topK.Add(BOB_BYTES);
            Assert.AreEqual(ulong.MaxValue, topK.Elements().Single().Freq, "an add to an element at the largest");
            Assert.AreEqual(ulong.MaxValue, topK.N, "an add to a count at the largest");
        }

        /// <summary>
        /// Below the field's limit nothing changes: the stored count is the count.
        /// </summary>
        [TestMethod]
        public void TestTheStoredCountIsTheCountBelowTheFieldsLimit()
        {
            var topK = new TopK(0.01, 0.01, 3);
            for (var i = 0; i < 7; i++) topK.Add(BOB_BYTES);

            var written = topK.ToByteArray();
            Assert.AreEqual(7u,
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    written.AsSpan(PayloadStart + 4)));
            Assert.AreEqual(7UL, Persistence.FromByteArray<TopK>(written).N);
        }

        /// <summary>
        /// A payload written before this was fixed reads, and reads with the real count.
        /// Its field is whatever the count was modulo 2^32, which is nothing to rely on, so
        /// it is the sketch's count that is used: here the sketch has seen 2^32 + 3 items
        /// and the field says 3, as a structure of that age wrote it.
        /// </summary>
        [TestMethod]
        public void TestTheCountComesFromTheSketchWhateverTheFieldSays()
        {
            var topK = new TopK(0.01, 0.01, 3);
            for (var i = 0; i < 7; i++) topK.Add(BOB_BYTES);
            var written = topK.ToByteArray();

            var disagreeing = SeenItems(written, 7, 12345);
            Assert.AreEqual(7UL, Persistence.FromByteArray<TopK>(disagreeing).N);

            var wrapped = SeenItems(written, uint.MaxValue + 4UL, 3);
            Assert.AreEqual(uint.MaxValue + 4UL, Persistence.FromByteArray<TopK>(wrapped).N);
        }

        [TestMethod]
        public void BenchmarkTopKAdd()
        {
            var n = 100000;
            var topK = new TopK(0.001, 0.99, 5);
            var data = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                data[i] = Encoding.ASCII.GetBytes(i.ToString());
            }

            for (int i = 0; i < n; i++)
            {
                topK.Add(data[i]);
            }
        }
    }
}
