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
        /// pieces and one point is drawn from each. The registers are correlated --
        /// exactly one point comes from every interval -- so the estimators become
        /// approximations rather than exact.
        /// <para>
        /// Two things follow, and only one of them is in the paper's billing. It is
        /// somewhat faster, because a point never falls below its interval's start, so
        /// an interval can be ruled out before any randomness is spent on it. More
        /// usefully, the correlation buys real accuracy on small sets: at 256 registers
        /// its relative error is around 4.5% at ten elements against the other
        /// construction's 7.1%, and the advantage is gone by ten thousand.
        /// </para>
        /// </summary>
        SetSketch2 = 2,
    }
}
