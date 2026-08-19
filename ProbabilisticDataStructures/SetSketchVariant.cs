namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Which of the paper's two constructions a <see cref="SetSketch"/> uses to draw an
    /// element's ascending run of hash values.
    /// </summary>
    /// <remarks>
    /// The two agree on everything a register means, so they share every estimator,
    /// the merge, and the payload. They differ only in how the run is drawn, and that
    /// difference decides whether the registers are statistically independent.
    /// </remarks>
    public enum SetSketchVariant
    {
        /// <summary>
        /// Exponential spacings: each value is the one before it plus a draw whose rate
        /// depends on how many registers remain. This makes the registers statistically
        /// independent, which is the assumption every estimator in the paper is derived
        /// under -- so for this variant the estimators are exact rather than
        /// approximate. The default, and the one to reach for unless a measurement says
        /// otherwise.
        /// </summary>
        SetSketch1 = 1,

        /// <summary>
        /// Sampling from disjoint intervals: the exponential's domain is cut into m
        /// pieces and one point is drawn from each. Cheaper per element, because each
        /// point is a single draw needing no running total, but the registers are
        /// correlated -- exactly one point comes from every interval -- so the
        /// estimators become approximations rather than exact.
        /// </summary>
        SetSketch2 = 2,
    }
}
