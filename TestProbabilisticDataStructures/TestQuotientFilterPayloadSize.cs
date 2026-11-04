using System;
using System.Reflection;
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
    /// cannot be written, since its table is one run, longer than any run a payload holds,
    /// and that is a thing the writer says clearly rather than as an overflow: its
    /// <c>WriteTo</c> counts the payload from the two widths before anything is built
    /// (#216). The table is too large
    /// to build in a test, so a small filter claims the widths and the count is reached
    /// through them.
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
        /// A filter whose table would pass one array is refused by its <c>ToByteArray</c>
        /// before anything is allocated for it, with the writer's exception naming the payload
        /// (#216), and written by its <c>WriteTo</c> in the large form, where the table's run
        /// has a <c>u64</c> length (#228), the header declaring the count with it. The rows are
        /// filters the constructor builds (<see cref="TestWhichConstructedFiltersFitOneArray"/>),
        /// too large to build here, so a small filter claims the two widths that size its
        /// table. The payload is three counts and the run's length, sixteen bytes in the
        /// ordinary envelope and twenty in the large form, and eight a word; the table's bytes
        /// are past <c>int.MaxValue</c> in each, which is where the count wrapped when it was
        /// an <c>int</c>, and so past one array. At 2^31 slots of thirteen bits they are 2^32
        /// and sixteen, which is sixteen in 32 bits: a run's length worked out in a
        /// <c>uint</c> would call that table nearly empty.
        /// </summary>
        [TestMethod]
        [DataRow(31u, 5u, 268_435_458L)]
        [DataRow(30u, 13u, 268_435_458L)]
        [DataRow(30u, 15u, 301_989_890L)]
        [DataRow(31u, 13u, 536_870_914L)]
        public void TestATableTooLargeForOneArrayIsRefusedAsOneAndWrittenInTheLargeForm(
            uint quotientBits, uint remainderBits, long words)
        {
            Assert.AreEqual(words, (((1L << (int)quotientBits) * (remainderBits + 3)) >> 6) + 2,
                "the row's table is miscounted");

            var filter = new QuotientFilter(100, 0.01);
            for (var i = 0; i < 40; i++) filter.Add(System.Text.Encoding.ASCII.GetBytes($"k{i}"));

            TestPayloadCount.AssertStreamedWhileClaiming(filter, 16 + 8 * words, 20 + 8 * words,
                (filter, "quotientBits", quotientBits), (filter, "remainderBits", remainderBits));
        }

        /// <summary>
        /// Which filters the constructor builds fit one array, worked out from its own sizing
        /// and the writer's own count, and not by building them: a small filter claims each
        /// split, and its payload is counted as <c>WriteTo</c> counts it. The first row of
        /// each pair fits the ordinary envelope and the second does not, and is written in the
        /// large form, its table a run with a <c>u64</c> length (#228). The table's arithmetic
        /// is restated here as well, as a table of 2^q slots of r + 3 bits and two spare words.
        /// </summary>
        [TestMethod]
        [DataRow(1_610_612_736u, 0.07, 31, 4, true)]
        [DataRow(1_610_612_736u, 0.04, 31, 5, false)]
        [DataRow(1_610_612_736u, 0.0002, 31, 13, false)]
        [DataRow(805_306_368u, 0.0003, 30, 12, true)]
        [DataRow(805_306_368u, 0.0002, 30, 13, false)]
        [DataRow(805_306_368u, 0.00005, 30, 15, false)]
        [DataRow(100_000u, 0.01, 18, 7, true)]
        public void TestWhichConstructedFiltersFitOneArray(
            uint n, double rate, int quotientBits, int remainderBits, bool fits)
        {
            var (q, r) = QuotientFilter.Split(n, rate);
            Assert.AreEqual((uint)quotientBits, q);
            Assert.AreEqual((uint)remainderBits, r);

            var words = (((1L << quotientBits) * (remainderBits + 3)) >> 6) + 2;
            var counted = CountedWhileClaiming(q, r);
            Assert.AreEqual(16 + 8 * words, counted, "the count is not the table's bytes and sixteen");
            Assert.AreEqual(fits, counted <= LargestPayload,
                $"the row says the filter fits one array, and its payload of {counted:N0} bytes does not, or the reverse");

            // A table past one array is a run past one array, which the large form carries.
            if (!fits)
            {
                var filter = new QuotientFilter(100, 0.01);
                TestPayloadCount.AssertStreamedWhileClaiming(filter, counted, counted + 4,
                    (filter, "quotientBits", q), (filter, "remainderBits", r));
            }
        }

        /// <summary>
        /// The payload a filter of this split counts, from a small filter claiming it. The
        /// count builds nothing, so no table of that size is allocated.
        /// </summary>
        private static long CountedWhileClaiming(uint quotientBits, uint remainderBits)
        {
            const BindingFlags Fields = BindingFlags.NonPublic | BindingFlags.Instance;
            var filter = new QuotientFilter(100, 0.01);
            typeof(QuotientFilter).GetField("quotientBits", Fields)!.SetValue(filter, quotientBits);
            typeof(QuotientFilter).GetField("remainderBits", Fields)!.SetValue(filter, remainderBits);

            var counter = PayloadWriter.Counter();
            ((IPayloadBody)filter).WritePayload(counter);
            return counter.Length;
        }
    }
}
