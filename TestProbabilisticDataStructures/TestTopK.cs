using System;
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

        [TestMethod]
        public void TestTopk()
        {
            var topK = TopK.NewTopK(0.001, 0.99, 5);

            topK.Add(BOB_BYTES).Add(BOB_BYTES).Add(BOB_BYTES);
            // + BOB, BOB, BOB
            topK.Add(TYLER_BYTES).Add(TYLER_BYTES).Add(TYLER_BYTES).Add(TYLER_BYTES).Add(TYLER_BYTES);
            //   BOB, BOB, BOB
            // + TYLER, TYLER, TYLER, TYLER, TYLER
            topK.Add(FRED_BYTES);
            //   BOB, BOB, BOB
            //   TYLER, TYLER, TYLER, TYLER, TYLER
            // + FRED
            topK.Add(ALICE_BYTES).Add(ALICE_BYTES).Add(ALICE_BYTES).Add(ALICE_BYTES);
            //   BOB, BOB, BOB
            //   TYLER, TYLER, TYLER, TYLER, TYLER
            //   FRED
            // + ALICE, ALICE, ALICE, ALICE
            topK.Add(JAMES_BYTES);
            //   BOB, BOB, BOB
            //   TYLER, TYLER, TYLER, TYLER, TYLER
            //   FRED
            //   ALICE, ALICE, ALICE, ALICE
            // + JAMES
            topK.Add(FRED_BYTES);
            //   BOB, BOB, BOB
            //   TYLER, TYLER, TYLER, TYLER, TYLER
            // = FRED, FRED
            //   ALICE, ALICE, ALICE, ALICE
            //   JAMES
            topK.Add(SARA_BYTES).Add(SARA_BYTES);
            //   BOB, BOB, BOB
            //   TYLER, TYLER, TYLER, TYLER, TYLER
            //   FRED, FRED
            //   ALICE, ALICE, ALICE, ALICE
            // - JAMES
            // + SARA, SARA

            var addedK = topK.Add(BILL_BYTES);
            //   BOB, BOB, BOB
            //   TYLER, TYLER, TYLER, TYLER, TYLER
            //   - FRED, FRED
            //   ALICE, ALICE, ALICE, ALICE
            //   SARA, SARA
            //   BILL
            Assert.AreSame(topK, addedK);
            // latest one also
            var expected = new ProbabilisticDataStructures.TopK.Element[]{
                new ProbabilisticDataStructures.TopK.Element{Data=BILL_BYTES, Freq=1},
                new ProbabilisticDataStructures.TopK.Element{Data=SARA_BYTES, Freq=2},
                new ProbabilisticDataStructures.TopK.Element{Data=BOB_BYTES, Freq=3},
                new ProbabilisticDataStructures.TopK.Element{Data=ALICE_BYTES, Freq=4},
                new ProbabilisticDataStructures.TopK.Element{Data=TYLER_BYTES, Freq=5},
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
        }
    }
}
