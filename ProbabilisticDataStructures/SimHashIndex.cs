using System;
using System.Collections.Generic;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Finds which stored SimHash fingerprints are worth comparing against a query.
    /// </summary>
    /// <remarks>
    /// The bit-sampling counterpart of <see cref="MinHashIndex"/>, and it has a guarantee
    /// its sibling cannot offer. A 64-bit fingerprint cut into <c>b</c> bands means two
    /// fingerprints differing in fewer than <c>b</c> bits must agree on at least one band
    /// -- there are only <c>b - 1</c> differing bits to spread over <c>b</c> bands, so one
    /// band gets none. Within that distance retrieval is <b>certain</b>, not probable.
    /// <para>
    /// Past it, retrieval falls away quickly, so choose the band count from the Hamming
    /// distance you consider a near-duplicate. Eight bands guarantees everything within
    /// seven bits, which on a 64-bit fingerprint is a cosine similarity of about 0.95.
    /// </para>
    /// <para>
    /// As with <see cref="MinHashIndex"/>, this returns <b>candidates</b>. Compare them
    /// with <see cref="SimHash.HammingDistance"/>; the index exists to make that
    /// comparison cheap, not to replace it.
    /// </para>
    /// </remarks>
    public class SimHashIndex
    {
        private const int FingerprintBits = 64;

        private readonly int bands;
        private readonly int bandBits;
        private readonly Dictionary<ulong, List<string>> buckets = new Dictionary<ulong, List<string>>();

        /// <summary>
        /// Creates an index cutting each fingerprint into the given number of bands.
        /// </summary>
        /// <param name="bands">
        /// How many bands, which must divide 64. Anything differing in fewer than this
        /// many bits is guaranteed to be returned.
        /// </param>
        public SimHashIndex(int bands)
        {
            if (bands < 1 || bands > FingerprintBits || FingerprintBits % bands != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bands), bands,
                    "The band count has to divide 64 evenly, so that every band is the " +
                    "same width and the guarantee holds for all of them.");
            }

            this.bands = bands;
            this.bandBits = FingerprintBits / bands;
        }

        /// <summary>
        /// The Hamming distance within which retrieval is certain.
        /// </summary>
        public int GuaranteedWithin() => this.bands - 1;

        /// <summary>
        /// Adds a fingerprint under an identifier.
        /// </summary>
        /// <param name="id">What to return when this fingerprint is a candidate.</param>
        /// <param name="signature">The fingerprint to index.</param>
        public void Add(string id, SimHashSignature signature)
        {
            ArgumentNullException.ThrowIfNull(id);
            ArgumentNullException.ThrowIfNull(signature);

            foreach (var key in this.BandsOf(signature.Value))
            {
                if (!this.buckets.TryGetValue(key, out var members))
                {
                    members = new List<string>();
                    this.buckets[key] = members;
                }

                if (!members.Contains(id))
                {
                    members.Add(id);
                }
            }
        }

        /// <summary>
        /// The identifiers worth comparing against this fingerprint.
        /// </summary>
        /// <param name="signature">The fingerprint to look for neighbours of.</param>
        /// <returns>Candidates, in no particular order.</returns>
        public IReadOnlyCollection<string> Query(SimHashSignature signature)
        {
            ArgumentNullException.ThrowIfNull(signature);

            var found = new HashSet<string>();

            foreach (var key in this.BandsOf(signature.Value))
            {
                if (this.buckets.TryGetValue(key, out var members))
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
        /// One bucket key per band: the band's bits, tagged with which band they came
        /// from so that identical bits in different positions do not collide.
        /// </summary>
        private ulong[] BandsOf(ulong fingerprint)
        {
            var keys = new ulong[this.bands];
            var mask = this.bandBits == 64 ? ulong.MaxValue : (1UL << this.bandBits) - 1;

            for (var band = 0; band < this.bands; band++)
            {
                var bits = (fingerprint >> (band * this.bandBits)) & mask;
                keys[band] = (bits << 8) | (uint)band;
            }

            return keys;
        }

        /// <summary>The number of bands each fingerprint is cut into.</summary>
        public int Bands() => this.bands;

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
