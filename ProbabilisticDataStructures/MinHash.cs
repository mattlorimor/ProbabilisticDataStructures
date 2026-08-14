using System;
using System.Collections.Generic;

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
    }
}
