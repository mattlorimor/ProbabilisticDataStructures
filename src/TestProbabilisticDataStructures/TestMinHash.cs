using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;


namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestMinHash
    {
        /// <summary>
        /// Ensures that MinHash returns the correct similarity ratio.
        /// </summary>
        [TestMethod]
        public void TestMinHashSimilarity()
        {
            var bag = new List<string>{
                "bob",
                "alice",
                "frank",
                "tyler",
                "sara"
            };

            var simRatio = MinHash.Similarity(bag.ToArray(), bag.ToArray());
            Assert.AreEqual(1.0, simRatio);

            var dict = Words.Dictionary(1000);
            var bag2 = new List<string>();
            for (int i = 0; i < 1000; i++)
            {
                bag2.Add(i.ToString());
            }

            simRatio = MinHash.Similarity(dict, bag2.ToArray());
            Assert.AreEqual(0.0, simRatio);

            var bag3 = Words.Dictionary(500);
            simRatio = MinHash.Similarity(dict, bag3);
            if (simRatio > 0.7 || simRatio < 0.5)
            {
                Assert.Fail(string.Format("Expected between 0.5 and 0.7, got {0}", simRatio));
            }
        }
    }
}
