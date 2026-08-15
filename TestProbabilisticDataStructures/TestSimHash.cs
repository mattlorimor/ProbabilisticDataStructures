using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestSimHash
    {
        [TestMethod]
        public void TestIdenticalDocumentsGiveIdenticalSignatures()
        {
            var words = new[] { "the", "quick", "brown", "fox" };

            var a = SimHash.Signature(words);
            var b = SimHash.Signature(new[] { "the", "quick", "brown", "fox" });

            Assert.AreEqual(a.Value, b.Value);
            Assert.AreEqual(0, SimHash.HammingDistance(a, b));
            Assert.AreEqual(1f, SimHash.Similarity(a, b));
        }
        private static string[] Document(string prefix, int terms) =>
            Enumerable.Range(0, terms).Select(i => $"{prefix}-term-{i}").ToArray();

        /// <summary>
        /// What stops the test above from passing vacuously: a signature that returned
        /// the same value for everything would satisfy it perfectly.
        /// <para>
        /// Two random hyperplane fingerprints of unrelated documents differ in about half
        /// their bits, which is an estimated cosine of zero.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestUnrelatedDocumentsAreNotSimilar()
        {
            var a = SimHash.Signature(Document("alpha", 200));
            var b = SimHash.Signature(Document("beta", 200));

            var distance = SimHash.HammingDistance(a, b);
            Assert.IsGreaterThan(20, distance,
                $"unrelated documents differed in only {distance} of 64 bits");
            Assert.IsLessThan(44, distance,
                $"unrelated documents differed in {distance} of 64 bits, which is not " +
                "the roughly half that random hyperplanes give");

            var similarity = SimHash.Similarity(a, b);
            Assert.IsLessThan(0.4f, similarity,
                $"unrelated documents were {similarity:F2} similar");
        }

        /// <summary>
        /// The property near-duplicate detection actually rests on: fingerprints have to
        /// get closer as the documents do, not merely differ when the documents differ.
        /// </summary>
        [TestMethod]
        public void TestMoreOverlapMeansCloserFingerprints()
        {
            const int Terms = 400;
            var original = Document("doc", Terms);
            var reference = SimHash.Signature(original);

            var previousDistance = -1;

            // From an exact copy down to nothing in common, replacing more of the
            // document each time.
            foreach (var shared in new[] { 400, 380, 300, 200, 100, 0 })
            {
                var variant = original.Take(shared)
                    .Concat(Document("other", Terms - shared))
                    .ToArray();

                var distance = SimHash.HammingDistance(reference, SimHash.Signature(variant));

                Assert.IsGreaterThanOrEqualTo(previousDistance, distance,
                    $"sharing {shared} of {Terms} terms gave a distance of {distance}, " +
                    "no further than a document sharing more");

                previousDistance = distance;
            }

            // And the ends are where they should be.
            Assert.AreEqual(0, SimHash.HammingDistance(reference, SimHash.Signature(original)));
            Assert.IsGreaterThan(0.9f,
                SimHash.Similarity(reference, SimHash.Signature(
                    original.Take(380).Concat(Document("other", 20)).ToArray())),
                "a document sharing 95% of its terms was not found similar");
        }

        /// <summary>
        /// The difference from <see cref="MinHash"/>, on one input where the two
        /// disagree completely.
        /// <para>
        /// These documents contain the same <b>set</b> of terms and nothing like the same
        /// document. MinHash answers about sets, so it calls them identical, correctly for
        /// the question it is answering. SimHash weighs a term by how often it appears, so
        /// it calls them unrelated, correctly for the question <b>it</b> is answering.
        /// Neither is wrong; they are not the same question.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestTermFrequencyMattersWhereItDoesNotForMinHash()
        {
            var mostlyApples = Enumerable.Repeat("apple", 40)
                .Concat(Enumerable.Repeat("banana", 2)).ToArray();
            var mostlyBananas = Enumerable.Repeat("apple", 2)
                .Concat(Enumerable.Repeat("banana", 40)).ToArray();

            // Same set of terms, so the sets resemble each other exactly.
            Assert.AreEqual(1f, MinHash.Similarity(mostlyApples, mostlyBananas),
                "the two documents no longer hold the same set of terms");

            var simHash = SimHash.Similarity(
                SimHash.Signature(mostlyApples), SimHash.Signature(mostlyBananas));

            Assert.IsLessThan(0.4f, simHash,
                $"SimHash called them {simHash:F2} similar, so it is not weighing terms " +
                "by frequency and answers the same question MinHash already answers");
        }

        /// <summary>
        /// And the weighting is proportionate rather than a switch: shifting the balance
        /// a little moves the fingerprint a little.
        /// </summary>
        [TestMethod]
        public void TestShiftingTheBalanceMovesTheFingerprint()
        {
            var reference = SimHash.Signature(
                Enumerable.Repeat("apple", 20).Concat(Enumerable.Repeat("banana", 20)).ToArray());

            var slight = SimHash.Signature(
                Enumerable.Repeat("apple", 22).Concat(Enumerable.Repeat("banana", 18)).ToArray());

            var heavy = SimHash.Signature(
                Enumerable.Repeat("apple", 38).Concat(Enumerable.Repeat("banana", 2)).ToArray());

            Assert.IsLessThanOrEqualTo(
                SimHash.HammingDistance(reference, heavy),
                SimHash.HammingDistance(reference, slight),
                "a small change in frequency moved the fingerprint as far as a large one");
        }

        /// <summary>
        /// A fingerprint is only useful if the same document gives the same one
        /// everywhere, so the hash and the way its bits are used are fixed by convention
        /// rather than chosen. Changing either silently invalidates every fingerprint
        /// anyone has stored, and nothing about the change would announce itself: the new
        /// fingerprints would be perfectly self-consistent and would match none of the
        /// old ones.
        /// <para>
        /// These values were computed by this implementation, not chosen. If they change,
        /// the question is whether the change was meant, not whether the numbers should
        /// be updated.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestSignatureValuesAreFixedAcrossVersions()
        {
            Assert.AreEqual(
                0x2878FFF5B795B1D0UL,
                SimHash.Signature(new[] { "alpha", "beta", "gamma", "delta", "epsilon" }).Value);

            // And the weighting is part of the convention, not just the hash.
            Assert.AreEqual(
                0xBE6903B5F625AB5AUL,
                SimHash.Signature(new[] { "alpha", "alpha", "alpha", "beta", "gamma" }).Value);
        }

        /// <summary>
        /// A document with no terms has no term vector, and every bit is as unset as
        /// every other. It is a defined answer rather than a failure, and two of them
        /// compare as identical because they are.
        /// </summary>
        [TestMethod]
        public void TestAnEmptyDocumentHasAnEmptyFingerprint()
        {
            var empty = SimHash.Signature(Array.Empty<string>());

            Assert.AreEqual(0UL, empty.Value);
            Assert.AreEqual(1f, SimHash.Similarity(empty, SimHash.Signature(Array.Empty<string>())));
        }

        [TestMethod]
        public void TestNullArgumentsAreRefused()
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => SimHash.Signature(null!));
            Assert.ThrowsExactly<ArgumentNullException>(
                () => SimHash.Signature(new[] { "alpha", null! }));

            var signature = SimHash.Signature(new[] { "alpha" });
            Assert.ThrowsExactly<ArgumentNullException>(
                () => SimHash.HammingDistance(signature, null!));
            Assert.ThrowsExactly<ArgumentNullException>(
                () => SimHash.Similarity(null!, signature));
        }

        [TestMethod]
        public void TestASignatureRoundTripsThroughPersistence()
        {
            var original = SimHash.Signature(Document("doc", 200));
            var restored = Persistence.FromByteArray<SimHashSignature>(original.ToByteArray());

            Assert.AreEqual(original.Value, restored.Value);
            Assert.AreEqual(1f, SimHash.Similarity(original, restored));

            // A stored fingerprint still compares against one computed now, which is the
            // only reason to store one.
            var near = SimHash.Signature(
                Document("doc", 190).Concat(Document("other", 10)).ToArray());
            Assert.AreEqual(
                SimHash.HammingDistance(original, near),
                SimHash.HammingDistance(restored, near));
        }

        /// <summary>
        /// A signature's hash is not a caller's to choose, since two signatures are only
        /// comparable when both were built the same way. A payload naming a different one
        /// is refused rather than read.
        /// </summary>
        [TestMethod]
        public void TestASignatureIsNotAnotherStructure()
        {
            var bytes = SimHash.Signature(new[] { "alpha" }).ToByteArray();

            // Its own structure id, distinct from the MinHash signature it sits beside.
            Assert.AreNotEqual(
                bytes[6] | (bytes[7] << 8),
                MinHash.Signature(new[] { "alpha" }, 8).ToByteArray()[6]);

            Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<MinHashSignature>(bytes));

            var poked = (byte[])bytes.Clone();
            poked[8] = 0;
            var crc = new System.IO.Hashing.Crc32();
            crc.Append(poked.AsSpan(4, poked.Length - 8));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                poked.AsSpan(poked.Length - 4), crc.GetCurrentHashAsUInt32());

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<SimHashSignature>(poked));
            StringAssert.Contains(ex.Message, "XxHash3");
        }

    }
}
