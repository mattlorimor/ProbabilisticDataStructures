using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Reduces a document to a fingerprint whose bits preserve cosine similarity, so
    /// that near-duplicates can be found by comparing fingerprints.
    /// </summary>
    /// <remarks>
    /// Charikar, "Similarity Estimation Techniques from Rounding Algorithms" (2002).
    /// </remarks>
    public static class SimHash
    {
        /// <summary>The width of a fingerprint in bits.</summary>
        private const int Bits = 64;

        /// <summary>
        /// The fingerprint of a bag of terms.
        /// </summary>
        /// <param name="bag">
        /// The document's terms. A term appearing more than once weighs more, which is
        /// the difference from <see cref="MinHash"/>: that treats a bag as a set.
        /// </param>
        /// <returns>The document's fingerprint.</returns>
        public static SimHashSignature Signature(string[] bag)
        {
            ArgumentNullException.ThrowIfNull(bag);

            Span<int> weights = stackalloc int[Bits];

            foreach (var term in bag)
            {
                ArgumentNullException.ThrowIfNull(term, nameof(bag));

                var hash = System.IO.Hashing.XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(term));

                for (var bit = 0; bit < Bits; bit++)
                {
                    weights[bit] += ((hash >> bit) & 1) == 1 ? 1 : -1;
                }
            }

            var fingerprint = 0UL;
            for (var bit = 0; bit < Bits; bit++)
            {
                if (weights[bit] > 0)
                {
                    fingerprint |= 1UL << bit;
                }
            }

            return new SimHashSignature(fingerprint);
        }

        /// <summary>
        /// How many bits two fingerprints differ in.
        /// </summary>
        /// <param name="first">The first fingerprint.</param>
        /// <param name="second">The second fingerprint.</param>
        /// <returns>The number of differing bits, from 0 to 64.</returns>
        public static int HammingDistance(SimHashSignature first, SimHashSignature second)
        {
            ArgumentNullException.ThrowIfNull(first);
            ArgumentNullException.ThrowIfNull(second);

            return BitOperations.PopCount(first.Value ^ second.Value);
        }

        /// <summary>
        /// The estimated cosine similarity of the two documents the fingerprints came
        /// from.
        /// </summary>
        /// <param name="first">The first fingerprint.</param>
        /// <param name="second">The second fingerprint.</param>
        /// <returns>The estimated cosine of the angle between the two term vectors.</returns>
        /// <remarks>
        /// Two random hyperplanes separate a pair of vectors with probability equal to
        /// the angle between them over pi, so the fraction of differing bits estimates
        /// that angle and its cosine estimates the similarity.
        /// </remarks>
        public static float Similarity(SimHashSignature first, SimHashSignature second)
        {
            var differing = HammingDistance(first, second);

            return (float)Math.Cos(Math.PI * differing / Bits);
        }
    }
}
