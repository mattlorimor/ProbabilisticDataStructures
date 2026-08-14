using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;


namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestMinHash
    {
        /// <summary>
        /// Resemblance is the Jaccard index: distinct words in both bags over distinct
        /// words in either. These are exact values, not a range, because the result is
        /// computed rather than estimated.
        /// <para>
        /// The last assertion used to allow anything from 0.5 to 0.7, which is wide
        /// enough to admit the Sorensen-Dice coefficient the implementation actually
        /// returned. 500 of the same 1000 words resemble them at exactly 0.5; Dice puts
        /// the same pair at 0.667, and the range accepted both.
        /// </para>
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

            // A bag resembles itself exactly.
            Assert.AreEqual(1.0f, MinHash.Similarity(bag.ToArray(), bag.ToArray()));

            var dict = Words.Dictionary(1000);
            var bag2 = new List<string>();
            for (int i = 0; i < 1000; i++)
            {
                bag2.Add(i.ToString());
            }

            // Nothing in common.
            Assert.AreEqual(0.0f, MinHash.Similarity(dict, bag2.ToArray()));

            // 500 words drawn from the same 1000: the intersection is 500 and the
            // union is 1000, so the resemblance is exactly one half.
            var bag3 = Words.Dictionary(500);
            Assert.AreEqual(0.5f, MinHash.Similarity(dict, bag3));
        }

        /// <summary>
        /// The definition, stated on small bags where the answer can be read off.
        /// Prior versions returned the Sorensen-Dice coefficient, which is related as
        /// D = 2J / (1 + J) and so is consistently higher: the first case below was
        /// reported as 0.5.
        /// </summary>
        [TestMethod]
        public void TestMinHashSimilarityIsTheJaccardIndex()
        {
            // {a,b} and {b,c} share one word out of three distinct.
            Assert.AreEqual(1f / 3f, MinHash.Similarity(
                new[] { "a", "b" }, new[] { "b", "c" }), 1e-6);

            // Three shared, five distinct.
            Assert.AreEqual(0.6f, MinHash.Similarity(
                new[] { "a", "b", "c", "d" }, new[] { "a", "b", "c", "e" }), 1e-6);

            // A subset: three shared, six distinct.
            Assert.AreEqual(0.5f, MinHash.Similarity(
                new[] { "a", "b", "c" }, new[] { "a", "b", "c", "d", "e", "f" }), 1e-6);

            // Repeats are not words in their own right; both bags hold {a, b}.
            Assert.AreEqual(1.0f, MinHash.Similarity(
                new[] { "a", "a", "a", "b" }, new[] { "a", "b" }));
        }

        /// <summary>
        /// Agreement with the definition computed directly, over random bags, so that
        /// the assertions above are not the only shapes covered.
        /// </summary>
        [TestMethod]
        public void TestMinHashAgreesWithJaccardComputedDirectly()
        {
            var rand = new Random(1);

            for (int trial = 0; trial < 2000; trial++)
            {
                var bag1 = Enumerable.Range(0, rand.Next(1, 40))
                    .Select(_ => $"w{rand.Next(50)}").ToArray();
                var bag2 = Enumerable.Range(0, rand.Next(1, 40))
                    .Select(_ => $"w{rand.Next(50)}").ToArray();

                var set1 = bag1.ToHashSet();
                var set2 = bag2.ToHashSet();
                var expected = (float)set1.Intersect(set2).Count() / set1.Union(set2).Count();

                Assert.AreEqual(expected, MinHash.Similarity(bag1, bag2), 1e-6,
                    $"trial {trial}: {{{string.Join(",", bag1)}}} vs {{{string.Join(",", bag2)}}}");
            }
        }

        /// <summary>
        /// Two empty bags hold the same distinct words -- none -- so they resemble each
        /// other exactly. The division is 0 / 0 and used to return NaN.
        /// </summary>
        [TestMethod]
        public void TestMinHashHandlesEmptyBags()
        {
            Assert.AreEqual(1.0f, MinHash.Similarity(new string[0], new string[0]));
            Assert.AreEqual(0.0f, MinHash.Similarity(new[] { "a" }, new string[0]));
            Assert.AreEqual(0.0f, MinHash.Similarity(new string[0], new[] { "a" }));
        }

        /// <summary>
        /// Null bags threw NullReferenceException, which every other entry point in
        /// the library stopped doing in 3.0.1.
        /// </summary>
        [TestMethod]
        public void TestMinHashRejectsNull()
        {
            Assert.ThrowsExactly<ArgumentNullException>(
                () => MinHash.Similarity(null!, new[] { "a" }));
            Assert.ThrowsExactly<ArgumentNullException>(
                () => MinHash.Similarity(new[] { "a" }, null!));
        }

    }
}
