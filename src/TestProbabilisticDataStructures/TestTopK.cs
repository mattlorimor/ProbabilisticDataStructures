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
            // latest one also
            var expected = new ProbabilisticDataStructures.Element[]{
                new ProbabilisticDataStructures.Element{Data=BILL_BYTES, Freq=1},
                new ProbabilisticDataStructures.Element{Data=SARA_BYTES, Freq=2},
                new ProbabilisticDataStructures.Element{Data=BOB_BYTES, Freq=3},
                new ProbabilisticDataStructures.Element{Data=ALICE_BYTES, Freq=4},
                new ProbabilisticDataStructures.Element{Data=TYLER_BYTES, Freq=5},
            };

            var actual = topK.Elements();

            Assert.AreEqual(5, actual.Length);

            for (int i = 0; i < actual.Length; i++)
            {
                var element = actual[i];
                Assert.IsTrue(Enumerable.SequenceEqual(element.Data, expected[i].Data));
                // freq check
                Assert.AreEqual(expected[i].Freq, element.Freq);
            }

            var resetK = topK.Reset();
            Assert.AreSame(topK, resetK);

            Assert.AreEqual(0, topK.Elements().Length);
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
