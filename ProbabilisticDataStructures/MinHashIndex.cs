using System;
using System.Collections.Generic;
using System.IO.Hashing;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Finds which stored signatures are worth comparing against a query, so that
    /// near-duplicate search does not have to compare every pair.
    /// </summary>
    /// <remarks>
    /// The banding scheme from Rajaraman and Ullman, <i>Mining of Massive Datasets</i>,
    /// chapter 3.
    /// <para>
    /// A signature is cut into <c>bands</c> bands of <c>rows</c> values each, and each
    /// band is hashed into a bucket. Two signatures are candidates if <b>any</b> band
    /// matches exactly. Since a band matches with probability s^rows for resemblance s,
    /// at least one of them matches with probability 1 - (1 - s^rows)^bands -- an
    /// S-curve, which rises steeply near a threshold the two parameters put wherever you
    /// want it.
    /// </para>
    /// <para>
    /// <b>This is the first structure here whose failure is a missing answer.</b>
    /// Everything else may say yes when it should say no. An index may fail to return a
    /// pair that really is similar, and no amount of checking the candidates afterwards
    /// recovers it -- it was never offered. Choose the threshold below the similarity you
    /// actually care about, and read <see cref="RecallAt"/> before assuming a setting is
    /// safe.
    /// </para>
    /// <para>
    /// It returns <b>candidates</b>, not answers. Compare them properly with
    /// <see cref="MinHash.Similarity(MinHashSignature, MinHashSignature)"/>; the point is
    /// turning a billion comparisons into a few hundred, not skipping them.
    /// </para>
    /// </remarks>
    public class MinHashIndex
    {
        private readonly int bands;
        private readonly int rows;
        private readonly Dictionary<ulong, List<string>> buckets = new Dictionary<ulong, List<string>>();

        /// <summary>
        /// Creates an index over signatures of <c>bands * rows</c> values.
        /// </summary>
        /// <param name="bands">How many bands each signature is cut into.</param>
        /// <param name="rows">How many values are in each band.</param>
        public MinHashIndex(int bands, int rows)
        {
            if (bands < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bands), bands, "An index needs at least one band.");
            }

            if (rows < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rows), rows, "A band needs at least one row.");
            }

            this.bands = bands;
            this.rows = rows;
        }

        /// <summary>
        /// Creates an index tuned to retrieve pairs at or above a resemblance, choosing
        /// the bands and rows that put the S-curve's steep part near it.
        /// </summary>
        /// <param name="threshold">
        /// The resemblance to tune for. Pairs above it are usually returned and pairs
        /// below it usually are not, with "usually" softening either side of the
        /// threshold rather than switching at it.
        /// </param>
        /// <param name="signatureLength">
        /// The length of the signatures to be indexed. The bands must divide it.
        /// </param>
        /// <returns>An index tuned for that threshold.</returns>
        public static MinHashIndex ForThreshold(double threshold, int signatureLength)
        {
            if (!(threshold > 0 && threshold < 1))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(threshold), threshold,
                    "A resemblance threshold sits strictly between zero and one.");
            }

            if (signatureLength < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(signatureLength), signatureLength,
                    "A signature has at least one value.");
            }

            // The curve's steep point is near (1/bands)^(1/rows), and only the divisors
            // of the signature length are available, so it lands near the threshold
            // rather than on it. Which side it lands on matters a great deal.
            //
            // Rounding the steep point *above* the threshold means a pair at the
            // threshold is usually missed -- at 128 values and a threshold of 0.8, the
            // nearest configuration by distance alone gives 20% recall there. Rounding it
            // below costs extra candidates, which the caller then discards. This index
            // errs towards returning too much, because too much is work and too little is
            // a silently wrong answer.
            var bestBands = 0;
            var bestRows = 0;
            var bestDistance = double.MaxValue;

            for (var candidate = 1; candidate <= signatureLength; candidate++)
            {
                if (signatureLength % candidate != 0)
                {
                    continue;
                }

                var rows = signatureLength / candidate;
                var steepAt = Math.Pow(1.0 / candidate, 1.0 / rows);

                if (steepAt > threshold)
                {
                    continue;
                }

                var distance = threshold - steepAt;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestBands = candidate;
                    bestRows = rows;
                }
            }

            // A threshold below anything the divisors reach: take the most permissive
            // configuration available, which is one value per band.
            if (bestBands == 0)
            {
                bestBands = signatureLength;
                bestRows = 1;
            }

            return new MinHashIndex(bestBands, bestRows);
        }

        /// <summary>
        /// The chance a pair of the given resemblance is offered as a candidate.
        /// </summary>
        /// <param name="resemblance">The Jaccard resemblance of the pair.</param>
        /// <returns>The probability the index returns it.</returns>
        /// <remarks>
        /// Worth reading before trusting a setting. This is the number that says how much
        /// the index will silently miss.
        /// </remarks>
        public double RecallAt(double resemblance)
        {
            return 1 - Math.Pow(1 - Math.Pow(resemblance, this.rows), this.bands);
        }

        /// <summary>
        /// Adds a signature under an identifier.
        /// </summary>
        /// <param name="id">What to return when this signature is a candidate.</param>
        /// <param name="signature">The signature to index.</param>
        public void Add(string id, MinHashSignature signature)
        {
            ArgumentNullException.ThrowIfNull(id);
            ArgumentNullException.ThrowIfNull(signature);
            this.RequireCorrectLength(signature);

            foreach (var band in this.BandsOf(signature))
            {
                if (!this.buckets.TryGetValue(band, out var members))
                {
                    members = new List<string>();
                    this.buckets[band] = members;
                }

                if (!members.Contains(id))
                {
                    members.Add(id);
                }
            }
        }

        /// <summary>
        /// The identifiers worth comparing against this signature.
        /// </summary>
        /// <param name="signature">The signature to look for neighbours of.</param>
        /// <returns>
        /// Candidates, in no particular order. Some will not be similar; compare them
        /// properly. Some genuinely similar signatures may be missing -- see the remarks
        /// on this type.
        /// </returns>
        public IReadOnlyCollection<string> Query(MinHashSignature signature)
        {
            ArgumentNullException.ThrowIfNull(signature);
            this.RequireCorrectLength(signature);

            var found = new HashSet<string>();

            foreach (var band in this.BandsOf(signature))
            {
                if (this.buckets.TryGetValue(band, out var members))
                {
                    foreach (var member in members)
                    {
                        found.Add(member);
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// One bucket key per band: the hash of that band's values, mixed with the band's
        /// position so that two bands holding the same values do not collide.
        /// </summary>
        private ulong[] BandsOf(MinHashSignature signature)
        {
            // Materialised rather than yielded: Values is a ReadOnlySpan, which cannot
            // live across a yield boundary.
            var values = signature.Values;
            var keys = new ulong[this.bands];

            for (var band = 0; band < this.bands; band++)
            {
                var bytes = new byte[(this.rows + 1) * sizeof(ulong)];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)band);

                for (var row = 0; row < this.rows; row++)
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                        bytes.AsSpan((row + 1) * sizeof(ulong)),
                        values[(band * this.rows) + row]);
                }

                keys[band] = XxHash3.HashToUInt64(bytes);
            }

            return keys;
        }

        private void RequireCorrectLength(MinHashSignature signature)
        {
            if (signature.Length != this.bands * this.rows)
            {
                throw new ArgumentException(
                    $"This index is {this.bands} bands of {this.rows}, so it holds " +
                    $"signatures of {this.bands * this.rows} values, and this one has " +
                    $"{signature.Length}.",
                    nameof(signature));
            }
        }

        /// <summary>The number of bands each signature is cut into.</summary>
        public int Bands() => this.bands;

        /// <summary>The number of values in each band.</summary>
        public int Rows() => this.rows;

        /// <summary>The number of identifiers indexed.</summary>
        public int Count()
        {
            var all = new HashSet<string>();
            foreach (var bucket in this.buckets.Values)
            {
                foreach (var member in bucket)
                {
                    all.Add(member);
                }
            }

            return all.Count;
        }
    }
}
