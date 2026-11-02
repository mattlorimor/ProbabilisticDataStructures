using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The size of a quotient filter's payload, which the filter worked out in an int.
    /// </summary>
    /// <remarks>
    /// <c>WriteTo</c> copied the table into a byte array sized <c>data.Length * 8</c> in an
    /// <c>int</c>, so a table of 2^28 words or more -- two gibibytes -- made a negative
    /// length, and the write failed with an OverflowException from the allocation. The
    /// constructor builds such tables: 2^31 slots at five bits of remainder is 268,435,458
    /// words, and so is 2^30 slots at thirteen. They are what a filter for 1.6 billion items
    /// at 3% is, or for 805 million at 0.012%. Such a filter can be built and used and
    /// cannot be written, since its payload would not fit an envelope, and that is a thing
    /// the writer says clearly (see <see cref="TestPayloadWriterGrowth"/>) rather than as an
    /// overflow. The table is too large to build in a test, so the arithmetic that sizes it
    /// is reached directly.
    /// </remarks>
    [TestClass]
    public class TestQuotientFilterPayloadSize
    {
        /// <summary>
        /// The most a payload can hold, restated: the largest array less the envelope's
        /// header and checksum.
        /// </summary>
        private const long LargestPayload = 2_147_483_591L - 14 - 4;

        /// <summary>
        /// The payload of a table is three counts, the byte run's length, and the words:
        /// sixteen bytes and eight a word. This is the most words that fits.
        /// </summary>
        private const int LargestTable = (int)((LargestPayload - 16) / 8);

        /// <summary>
        /// A table's bytes are its words times eight, in 64 bits. The rows past 2^28
        /// words are the ones that wrapped, and the first of them is exactly 2^31 bytes.
        /// </summary>
        [TestMethod]
        [DataRow(1, 8L)]
        [DataRow(1_000, 8_000L)]
        [DataRow(268_435_455, 2_147_483_640L)]
        [DataRow(268_435_456, 2_147_483_648L)]
        [DataRow(268_435_458, 2_147_483_664L)]
        [DataRow(int.MaxValue, 17_179_869_176L)]
        public void TestATablesBytesAreCountedInSixtyFourBits(int words, long bytes)
        {
            Assert.AreEqual(bytes, QuotientFilter.BytesOf(words));
        }

        /// <summary>
        /// A table whose payload would pass the longest an envelope holds is refused
        /// before anything is allocated for it, with the writer's exception: the size it
        /// would have been and the size one can be. The largest table that fits is not.
        /// </summary>
        [TestMethod]
        public void TestATableTooLargeToWriteIsRefusedNamingTheSize()
        {
            QuotientFilter.CheckWritable(LargestTable);
            QuotientFilter.CheckWritable(1);

            var ex = Assert.ThrowsExactly<InvalidOperationException>(
                () => QuotientFilter.CheckWritable(LargestTable + 1));
            StringAssert.Contains(ex.Message, (16 + 8L * (LargestTable + 1)).ToString("N0"),
                "the refusal should say how large the payload would have been");
            StringAssert.Contains(ex.Message, LargestPayload.ToString("N0"),
                "the refusal should say how large a payload can be");

            Assert.ThrowsExactly<InvalidOperationException>(
                () => QuotientFilter.CheckWritable(int.MaxValue));
        }

        /// <summary>
        /// Which filters the constructor builds are affected, worked out from its own
        /// sizing and not by building them: a filter is a table of 2^q slots of r + 3 bits
        /// and two spare words, and the first row of each pair fits an envelope and the
        /// second does not. This passes before the fix as well; it says who the fix is for.
        /// </summary>
        [TestMethod]
        [DataRow(1_610_612_736u, 0.07, 31, 4, true)]
        [DataRow(1_610_612_736u, 0.04, 31, 5, false)]
        [DataRow(805_306_368u, 0.0003, 30, 12, true)]
        [DataRow(805_306_368u, 0.0002, 30, 13, false)]
        [DataRow(805_306_368u, 0.00005, 30, 15, false)]
        [DataRow(100_000u, 0.01, 18, 7, true)]
        public void TestWhichConstructedFiltersCanBeWritten(
            uint n, double rate, int quotientBits, int remainderBits, bool writable)
        {
            var (q, r) = QuotientFilter.Split(n, rate);
            Assert.AreEqual((uint)quotientBits, q);
            Assert.AreEqual((uint)remainderBits, r);

            var words = (((1L << quotientBits) * (remainderBits + 3)) >> 6) + 2;
            Assert.AreEqual(writable, words <= LargestTable);
        }
    }
}
