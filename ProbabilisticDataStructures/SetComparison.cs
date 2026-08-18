using System;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// What two <see cref="SetSketch"/>es say about the sets they summarise.
    /// </summary>
    /// <remarks>
    /// Every quantity here follows from three numbers: the two cardinalities and the
    /// Jaccard similarity. The paper makes that point explicitly, and it is why a
    /// single estimate of the similarity is worth working for -- get it, and union,
    /// intersection, both differences, cosine similarity and both inclusion
    /// coefficients come out of arithmetic rather than out of further estimation.
    /// </remarks>
    public readonly struct SetComparison : IEquatable<SetComparison>
    {
        internal SetComparison(double thisSize, double otherSize, double jaccard)
        {
            this.Size = thisSize;
            this.OtherSize = otherSize;
            this.Jaccard = jaccard;
        }

        /// <summary>The estimated size of the set this sketch holds.</summary>
        public double Size { get; }

        /// <summary>The estimated size of the set the other sketch holds.</summary>
        public double OtherSize { get; }

        /// <summary>
        /// The estimated Jaccard similarity: the size of the intersection over the size
        /// of the union.
        /// </summary>
        public double Jaccard { get; }

        /// <summary>The estimated size of the union.</summary>
        public double UnionSize => (this.Size + this.OtherSize) / (1 + this.Jaccard);

        /// <summary>The estimated size of the intersection.</summary>
        public double IntersectionSize =>
            (this.Size + this.OtherSize) * this.Jaccard / (1 + this.Jaccard);

        /// <summary>
        /// The estimated size of what this sketch holds and the other does not.
        /// </summary>
        public double DifferenceSize =>
            Math.Max(0, (this.Size - (this.OtherSize * this.Jaccard)) / (1 + this.Jaccard));

        /// <summary>
        /// The estimated size of what the other sketch holds and this one does not.
        /// </summary>
        public double OtherDifferenceSize =>
            Math.Max(0, (this.OtherSize - (this.Size * this.Jaccard)) / (1 + this.Jaccard));

        /// <summary>
        /// The estimated cosine similarity: the intersection over the geometric mean of
        /// the two sizes.
        /// </summary>
        /// <remarks>
        /// Nought when either set is empty, there being no direction to compare.
        /// </remarks>
        public double CosineSimilarity
        {
            get
            {
                var scale = Math.Sqrt(this.Size * this.OtherSize);
                return scale > 0 ? this.IntersectionSize / scale : 0;
            }
        }

        /// <summary>
        /// The estimated share of this set that the other also holds.
        /// </summary>
        public double InclusionCoefficient =>
            this.Size > 0 ? this.IntersectionSize / this.Size : 0;

        /// <summary>
        /// The estimated share of the other set that this one also holds.
        /// </summary>
        public double OtherInclusionCoefficient =>
            this.OtherSize > 0 ? this.IntersectionSize / this.OtherSize : 0;

        /// <inheritdoc/>
        public bool Equals(SetComparison other) =>
            this.Size.Equals(other.Size)
            && this.OtherSize.Equals(other.OtherSize)
            && this.Jaccard.Equals(other.Jaccard);

        /// <inheritdoc/>
        public override bool Equals(object? obj) =>
            obj is SetComparison other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() =>
            HashCode.Combine(this.Size, this.OtherSize, this.Jaccard);

        /// <inheritdoc/>
        public static bool operator ==(SetComparison left, SetComparison right) =>
            left.Equals(right);

        /// <inheritdoc/>
        public static bool operator !=(SetComparison left, SetComparison right) =>
            !left.Equals(right);

        /// <inheritdoc/>
        public override string ToString() =>
            $"Jaccard {this.Jaccard:F4} between sets of about {this.Size:F0} and "
            + $"{this.OtherSize:F0}";
    }
}
