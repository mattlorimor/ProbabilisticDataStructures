using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The signature is the approximate half of MinHash, and the half Broder's estimator
    /// is actually for: it reduces a bag once so that it can be compared against many
    /// others without either bag being held.
    /// </summary>
    [TestClass]
    public class TestMinHashSignature
    {
        private static double Resemblance(string[] a, string[] b)
        {
            var first = a.ToHashSet(StringComparer.Ordinal);
            var second = b.ToHashSet(StringComparer.Ordinal);
            return (double)first.Intersect(second).Count() / first.Union(second).Count();
        }

        private static string[] Bag(int from, int count) =>
            Enumerable.Range(from, count).Select(i => $"w{i}").ToArray();

        /// <summary>
        /// The estimate has to track the exact answer, and the error has to fall off as
        /// one over the square root of the signature's length, which is what choosing a
        /// length is choosing between.
        /// </summary>
        [TestMethod]
        public void TestErrorFallsOffWithSignatureLength()
        {
            var rand = new Random(4);

            foreach (var k in new[] { 64, 256, 1024 })
            {
                double total = 0;
                const int trials = 200;

                for (int t = 0; t < trials; t++)
                {
                    var overlap = rand.Next(0, 300);
                    var first = Bag(0, 300);
                    var second = Bag(300 - overlap, 300);

                    var estimate = MinHash.Similarity(
                        MinHash.Signature(first, k), MinHash.Signature(second, k));

                    total += Math.Abs(estimate - Resemblance(first, second));
                }

                var mean = total / trials;
                var bound = 1.0 / Math.Sqrt(k);

                Assert.IsLessThan(bound, mean,
                    $"k={k}: mean error {mean:F4} should sit inside {bound:F4}");
            }
        }

        /// <summary>
        /// The cases where the answer is not approximate at all.
        /// </summary>
        [TestMethod]
        public void TestTheCertainCasesAreCertain()
        {
            // Identical bags agree at every position, whatever the hash functions do.
            Assert.AreEqual(1.0f, MinHash.Similarity(
                MinHash.Signature(Bag(0, 200), 128), MinHash.Signature(Bag(0, 200), 128)));

            // Two empty bags resemble each other exactly, as they do through the exact
            // overload.
            Assert.AreEqual(1.0f, MinHash.Similarity(
                MinHash.Signature(new string[0], 128), MinHash.Signature(new string[0], 128)));

            // Repeats are not words in their own right.
            Assert.AreEqual(1.0f, MinHash.Similarity(
                MinHash.Signature(new[] { "a", "a", "a" }, 128),
                MinHash.Signature(new[] { "a" }, 128)));
        }

        /// <summary>
        /// A signature is only worth having if it can be compared against one computed
        /// elsewhere, which means the hash functions behind it cannot vary between calls
        /// or between runs.
        /// </summary>
        [TestMethod]
        public void TestSignaturesAreReproducible()
        {
            var bag = Bag(0, 300);

            var first = MinHash.Signature(bag, 128);
            var second = MinHash.Signature(bag.Reverse().ToArray(), 128);

            // Same bag in a different order is the same set, so the same signature.
            CollectionAssert.AreEqual(first.Values.ToArray(), second.Values.ToArray());
        }

        /// <summary>
        /// Which is only true if the values themselves are fixed, not merely consistent
        /// within a run. These were computed by the version that introduced the API, and
        /// are checked in for the same reason the format fixtures are: a change to the
        /// hash functions would silently invalidate every signature anyone has stored.
        /// </summary>
        [TestMethod]
        public void TestSignatureValuesAreFixedAcrossVersions()
        {
            var signature = MinHash.Signature(new[] { "alpha", "beta", "gamma" }, 8);

            CollectionAssert.AreEqual(
                new ulong[]
                {
                    31797598974978550,
                    3797849647461737319,
                    817913262285776957,
                    9941410565574083867,
                    4336690955595802856,
                    4204099280478280475,
                    13938889153069728686,
                    3678171872308680658,
                },
                signature.Values.ToArray(),
                "the hash functions behind a signature have changed, which invalidates " +
                "every signature anyone has stored");
        }

        [TestMethod]
        public void TestSignatureRoundTripsThroughPersistence()
        {
            var signature = MinHash.Signature(Bag(0, 500), 256);
            var restored = Persistence.FromByteArray<MinHashSignature>(signature.ToByteArray());

            Assert.AreEqual(signature.Length, restored.Length);
            CollectionAssert.AreEqual(signature.Values.ToArray(), restored.Values.ToArray());
            Assert.AreEqual(1.0f, MinHash.Similarity(signature, restored));
        }

        [TestMethod]
        public void TestSignaturesOfDifferentLengthsCannotBeCompared()
        {
            var ex = Assert.ThrowsExactly<ArgumentException>(() => MinHash.Similarity(
                MinHash.Signature(Bag(0, 10), 64), MinHash.Signature(Bag(0, 10), 128)));

            StringAssert.Contains(ex.Message, "same length");
        }

        [TestMethod]
        public void TestSignatureRejectsBadArguments()
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => MinHash.Signature(null!, 128));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MinHash.Signature(Bag(0, 10), 0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MinHash.Signature(Bag(0, 10), -1));

            Assert.ThrowsExactly<ArgumentNullException>(
                () => MinHash.Similarity(null!, MinHash.Signature(Bag(0, 10), 8)));
            Assert.ThrowsExactly<ArgumentNullException>(
                () => MinHash.Similarity(MinHash.Signature(Bag(0, 10), 8), null!));
        }

        /// <summary>
        /// Reading a signature as another structure, or another structure as a
        /// signature, is refused by the structure id like everything else.
        /// </summary>
        [TestMethod]
        public void TestASignatureIsNotAnotherStructure()
        {
            var signature = MinHash.Signature(Bag(0, 10), 64).ToByteArray();

            Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<BloomFilter>(signature));
            Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<MinHashSignature>(
                    new BloomFilter(100, 0.01).ToByteArray()));
        }
    }
}
