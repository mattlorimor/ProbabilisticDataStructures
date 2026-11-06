using System;
using System.Buffers.Binary;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// How the buffer a payload is built in grows, and where it stops.
    /// </summary>
    /// <remarks>
    /// The buffer doubled when it filled, as <c>buffer.Length * 2</c> in an <c>int</c>.
    /// That is fine to a gibibyte and wrong after it: the product wraps negative, the
    /// larger-of that and the length needed chose the length needed, and from then on the
    /// buffer was exactly full, so each further write -- four bytes of a count, say --
    /// allocated a new buffer and copied the whole payload into it. Measured on the tree
    /// this was written against, with a payload of 1,363,148,812 bytes in the buffer, a
    /// write of four more bytes took 0.4 s, and so did the next, and the one after. A
    /// structure that writes a thousand small fields after its large run is a thousand
    /// copies of it. And a write that took the payload past the largest array did not fail
    /// as a payload too large to write: the length it summed wrapped, and it failed in
    /// whichever way that happened to fail.
    /// <para>
    /// The growth is a function of three numbers, so it is checked as one. A buffer
    /// of two gibibytes is not something a test builds, and the arithmetic does not
    /// need one.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestPayloadWriterGrowth
    {
        /// <summary>
        /// The most a payload can hold: the largest array, less the envelope's header
        /// and checksum that the same array has to hold again when it is written out.
        /// Worked out here from the two eighteen bytes, not read from the code.
        /// </summary>
        private const long Largest = 2_147_483_591L - 14 - 4;

        /// <summary>
        /// While doubling fits, the buffer doubles; when a write needs more than that
        /// it grows to hold exactly the write; and when a write fits it does not grow.
        /// Past the point where doubling would not fit, it grows to the largest it can
        /// be, which is what lets the rest of the writes through without another copy.
        /// </summary>
        [TestMethod]
        [DataRow(256, 256, 1, 512)]
        [DataRow(256, 200, 100, 512)]
        [DataRow(1_000, 1_000, 5_000, 6_000)]
        [DataRow(1_000, 10, 5, 1_000)]
        [DataRow(1_000, 900, 100, 1_000)]
        [DataRow(1_000, 900, 101, 2_000)]
        [DataRow(1 << 29, 1 << 29, 1, 1 << 30)]
        // From here doubling wraps or does not fit.
        [DataRow(1 << 30, 1 << 30, 1, 2_147_483_573)]
        [DataRow(1_363_148_812, 1_363_148_812, 4, 2_147_483_573)]
        [DataRow(2_000_000_000, 2_000_000_000, 4, 2_147_483_573)]
        [DataRow(600_000_000, 600_000_000, 700_000_000, 1_300_000_000)]
        public void TestTheBufferDoublesAndIsClampedToTheLargest(
            int capacity, int length, int count, int expected)
        {
            Assert.AreEqual(expected, PayloadWriter.GrownCapacity(capacity, length, count));
        }

        /// <summary>
        /// The quadratic crawl itself, replayed on the arithmetic: a buffer full at
        /// 1,363,148,812 bytes, then a thousand writes of four bytes. Each time the length
        /// needed passes the capacity the buffer is reallocated, and the count of
        /// reallocations is what is asserted, because it is what the copying scales with.
        /// </summary>
        [TestMethod]
        public void TestSmallWritesToALargeBufferDoNotReallocateEachTime()
        {
            long capacity = 1_363_148_812;
            long length = capacity;
            var reallocations = 0;

            for (var i = 0; i < 1000; i++)
            {
                if (length + 4 > capacity)
                {
                    capacity = PayloadWriter.GrownCapacity((int)capacity, (int)length, 4);
                    reallocations++;
                }

                length += 4;
            }

            Assert.AreEqual(1, reallocations,
                "every small write after a large one reallocated the whole buffer");
        }

        /// <summary>
        /// A write that would take the payload past the largest array is refused, with
        /// an exception that says how large the payload would have been and how large
        /// one can be. The second row is the one that wrapped: the length needed does not
        /// fit an int, and was a negative number.
        /// </summary>
        [TestMethod]
        [DataRow(2_147_483_573, 2_147_483_573, 1, 2_147_483_574L)]
        [DataRow(1 << 30, int.MaxValue - 2, 10, 2_147_483_655L)]
        [DataRow(2_000_000_000, 2_000_000_000, 147_483_574, 2_147_483_574L)]
        public void TestAWriteBeyondTheLargestPayloadIsRefusedNamingTheSize(
            int capacity, int length, int count, long wouldBe)
        {
            var ex = Assert.ThrowsExactly<InvalidOperationException>(
                () => PayloadWriter.GrownCapacity(capacity, length, count));

            StringAssert.Contains(ex.Message, wouldBe.ToString("N0"),
                "the refusal should say how large the payload would have been");
            StringAssert.Contains(ex.Message,
                $"{wouldBe:N0} bytes as a byte array would hold it, in the ordinary form of the envelope",
                "the refusal should say which form the size it names is for (#290)");
            StringAssert.Contains(ex.Message, Largest.ToString("N0"),
                "the refusal should say how large a payload can be");
        }

        /// <summary>
        /// The most a payload can hold is exactly the largest write that is not refused:
        /// one byte more is the first row of the test above.
        /// </summary>
        [TestMethod]
        public void TestAWriteTheLargestPayloadHoldsIsNotRefused()
        {
            Assert.AreEqual((int)Largest,
                PayloadWriter.GrownCapacity(
                    1_363_148_812, 1_363_148_812, (int)Largest - 1_363_148_812));
        }

        /// <summary>
        /// A length of exactly the largest array is not too large to read into one: it gets
        /// past the check of its size. From a stream that cannot seek, which does not say how
        /// much it holds, it gets as far as the memory it would cost, which the default budget
        /// refuses before anything is allocated; held, it is refused for the bytes it lacks
        /// first (#347). Past it, above, is refused for its size.
        /// </summary>
        [TestMethod]
        public void TestALengthOfExactlyTheLargestArrayIsRefusedForItsCostNotItsSize()
        {
            var bytes = new BloomFilter(100, 0.01).ToByteArray();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), (uint)Array.MaxLength);

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => BloomFilter.ReadFrom(new TestHeldEnvelopeLengths.OneWay(bytes)));
            Assert.IsInstanceOfType<ReadBudgetExceededException>(ex.InnerException,
                $"the length should have reached the budget: {ex.Message}");

            var held = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<BloomFilter>(bytes));
            Assert.IsNull(held.InnerException, $"held, the length was refused for its budget: {held.Message}");
            Assert.Contains("too few for the", held.Message,
                $"held, the length should have got past its size to the bytes it lacks: {held.Message}");
        }

        /// <summary>
        /// FORMAT.md states the largest payload and the largest length a reader takes, as
        /// numbers, for someone decoding without this library. A number in a document has
        /// no build to fail, so it is held to the code here.
        /// </summary>
        [TestMethod]
        public void TestFormatStatesTheLargestPayloadAndTheLargestLengthRead()
        {
            using var resource = typeof(TestPayloadWriterGrowth).Assembly
                .GetManifestResourceStream("TestProbabilisticDataStructures.FORMAT.md");
            Assert.IsNotNull(resource, "FORMAT.md is not embedded in the test assembly");
            var specification = new StreamReader(resource).ReadToEnd();

            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            Assert.AreEqual(Largest, PersistenceFormat.MaxPayloadLength);
            StringAssert.Contains(specification, Largest.ToString("N0", invariant));
            StringAssert.Contains(specification, Array.MaxLength.ToString("N0", invariant));
            Assert.DoesNotContain("2,147,483,647 bytes", specification,
                "FORMAT.md still gives int.MaxValue as the largest payload");
        }

        /// <summary>
        /// The reader of the envelope refused a payload length above what an array
        /// holds, but let one between the largest array and int.MaxValue through to
        /// the allocation, which failed inside the runtime with an OutOfMemoryException
        /// rather than as a payload that cannot be read. A header can claim it, so it
        /// is the reader's to refuse, as it refuses the larger ones.
        /// </summary>
        [TestMethod]
        [DataRow(int.MaxValue)]
        [DataRow(2_147_483_592)]
        public void TestAPayloadLengthThatNoArrayHoldsIsRefusedNotAllocated(int claimed)
        {
            var bytes = new BloomFilter(100, 0.01).ToByteArray();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), (uint)claimed);

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<BloomFilter>(bytes, ReadOptions.Unlimited));
            StringAssert.Contains(ex.Message, "single array");
        }
    }
}
