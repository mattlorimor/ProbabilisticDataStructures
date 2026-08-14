using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Text;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// MinHash computes the similarity between two bags of words, as the resemblance
    /// defined by Broder in On the resemblance and containment of documents:
    ///
    /// http://gatekeeper.dec.com/ftp/pub/dec/SRC/publications/broder/positano-final-wpnums.pdf
    ///
    /// The resemblance of two sets is their Jaccard index: the size of their
    /// intersection over the size of their union. This can be used to cluster or
    /// compare documents by splitting the corpus into a bag of words.
    /// </summary>
    /// <remarks>
    /// The result is exact, not estimated. Broder's estimator is worth its error when
    /// a set is too large to hold or a signature has to be computed once and reused
    /// across many comparisons; neither applies to an API that is handed both bags in
    /// full and compares them once. Estimating here would cost accuracy and time and
    /// buy nothing.
    /// <para>
    /// Prior versions did neither. They generated hash permutations and never used
    /// them -- the loop over them ignored its own index -- and returned agreement over
    /// element positions, which for bags without repeats is the Sorensen-Dice
    /// coefficient rather than the resemblance. The two are related as D = 2J / (1 + J),
    /// so results were consistently too high: sets of one third resemblance were
    /// reported at one half.
    /// </para>
    /// </remarks>
    public static class MinHash
    {
        /// <summary>
        /// Returns the resemblance of two bags: the number of distinct words in both,
        /// over the number of distinct words in either. Repeated words count once.
        /// </summary>
        /// <param name="bag1">The first bag.</param>
        /// <param name="bag2">The second bag.</param>
        /// <returns>
        /// The resemblance, from 0 for bags sharing no word to 1 for bags with the
        /// same distinct words. Two empty bags resemble each other exactly and give 1.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Either bag, or any word in either bag, is null.
        /// </exception>
        public static float Similarity(string[] bag1, string[] bag2)
        {
            ArgumentNullException.ThrowIfNull(bag1);
            ArgumentNullException.ThrowIfNull(bag2);

            var first = new HashSet<string>(bag1, StringComparer.Ordinal);
            var second = new HashSet<string>(bag2, StringComparer.Ordinal);

            // Both empty is 0 / 0. Defined as 1 rather than left as NaN, which is what
            // it used to produce: two bags with the same distinct words -- none --
            // resemble each other exactly, and every other equal pair returns 1.
            if (first.Count == 0 && second.Count == 0)
            {
                return 1f;
            }

            var intersection = 0;
            foreach (var word in first)
            {
                if (second.Contains(word))
                {
                    intersection++;
                }
            }

            // Inclusion-exclusion, so the union is never materialized.
            var union = first.Count + second.Count - intersection;
            return (float)intersection / union;
        }

        /// <summary>
        /// Reduces a bag to a fixed-size signature, from which its resemblance to
        /// another bag can be estimated without either bag.
        /// </summary>
        /// <param name="bag">The bag to reduce.</param>
        /// <param name="k">
        /// The number of hash functions, and so the length of the signature. The error
        /// is roughly one over its square root: 128 gives about 9%, 1024 about 3%.
        /// </param>
        /// <returns>The signature.</returns>
        /// <exception cref="ArgumentNullException">
        /// The bag, or a word in it, is null.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="k"/> is not positive.
        /// </exception>
        /// <remarks>
        /// This is what the estimator is for. Comparing n documents pairwise by
        /// <see cref="Similarity(string[], string[])"/> is n squared full comparisons,
        /// each proportional to the size of both bags; with signatures it is n
        /// reductions and then n squared comparisons of k numbers.
        /// <para>
        /// Signatures are comparable across processes and across versions of this
        /// library, because the k hash functions are a fixed convention rather than
        /// something chosen per call. A signature can be stored and compared later
        /// against one computed anywhere else.
        /// </para>
        /// </remarks>
        public static MinHashSignature Signature(string[] bag, int k)
        {
            ArgumentNullException.ThrowIfNull(bag);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

            var values = new ulong[k];
            for (int i = 0; i < k; i++)
            {
                values[i] = ulong.MaxValue;
            }

            // Distinct words only: a signature describes a set, and a repeated word
            // would take the same minimum again without changing anything.
            var seen = new HashSet<string>(bag, StringComparer.Ordinal);

            // Long enough for most words without renting, and grown only if one is not.
            Span<byte> buffer = stackalloc byte[256];

            foreach (var word in seen)
            {
                ArgumentNullException.ThrowIfNull(word, nameof(bag));

                var needed = Encoding.UTF8.GetByteCount(word);
                var bytes = needed <= buffer.Length ? buffer[..needed] : new byte[needed];
                Encoding.UTF8.GetBytes(word, bytes);

                for (int i = 0; i < k; i++)
                {
                    // Seeding one hash function is what makes the k of them, so that
                    // nothing about which functions were used has to be written down.
                    var h = XxHash3.HashToUInt64(bytes, i);
                    if (h < values[i])
                    {
                        values[i] = h;
                    }
                }
            }

            return new MinHashSignature(values);
        }

        /// <summary>
        /// Estimates the resemblance of the two bags the signatures were taken from, as
        /// the fraction of positions at which they agree.
        /// </summary>
        /// <param name="signature1">The first signature.</param>
        /// <param name="signature2">The second signature.</param>
        /// <returns>
        /// The estimated resemblance, from 0 to 1. Two signatures of empty bags agree
        /// everywhere and give 1, matching what the exact overload returns for them.
        /// </returns>
        /// <exception cref="ArgumentNullException">Either signature is null.</exception>
        /// <exception cref="ArgumentException">
        /// The signatures are different lengths, so they were not built with the same
        /// hash functions and there is no position-by-position comparison to make.
        /// </exception>
        public static float Similarity(MinHashSignature signature1, MinHashSignature signature2)
        {
            ArgumentNullException.ThrowIfNull(signature1);
            ArgumentNullException.ThrowIfNull(signature2);

            if (signature1.Length != signature2.Length)
            {
                throw new ArgumentException(
                    $"Signatures are {signature1.Length} and {signature2.Length} long. " +
                    "Only signatures of the same length share hash functions, and only " +
                    "those can be compared.", nameof(signature2));
            }

            var first = signature1.Values;
            var second = signature2.Values;
            var agreeing = 0;

            for (int i = 0; i < first.Length; i++)
            {
                if (first[i] == second[i])
                {
                    agreeing++;
                }
            }

            return (float)agreeing / first.Length;
        }
    }
}
