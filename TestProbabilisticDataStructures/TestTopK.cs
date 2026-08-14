using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;
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
                .Select(e => Encoding.ASCII.GetString(e.Data))
                .OrderBy(x => x, System.StringComparer.Ordinal)
                .ToArray();
            CollectionAssert.AreEqual(expectedNames, actualNames,
                "the five most frequent elements, and no others");

            var resetK = topK.Reset();
            Assert.AreSame(topK, resetK);

            Assert.IsEmpty(topK.Elements());
            Assert.AreEqual(0u, topK.N);
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
