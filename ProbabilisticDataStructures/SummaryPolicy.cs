namespace ProbabilisticDataStructures
{
    /// <summary>
    /// How a <see cref="TupleSketch"/> folds several values for the same key into the
    /// one summary it keeps.
    /// </summary>
    /// <remarks>
    /// Every policy here is associative and commutative, and that is not a coincidence.
    /// A key's values are folded in whatever order a sort happens to leave them, and
    /// across several compactions, so the same values can end up grouped as
    /// fold(fold(a, b), c) on one run and fold(a, fold(b, c)) on another; a union folds
    /// two sketches' summaries in whichever order the merge reaches them. Only a fold
    /// that does not care gives a key a summary that is well defined at all.
    /// <para>
    /// This is a narrower promise than it may look. It makes each <em>key's summary</em>
    /// independent of the order its values arrived in. It does not make the sketch as a
    /// whole order-independent: which keys survive depends on where the sampling
    /// threshold fell, and that depends on where in the stream the sketch happened to
    /// run out of room. <see cref="ThetaSketch"/> behaves the same way, for the same
    /// reason.
    /// </para>
    /// <para>
    /// The reference implementation of tuple sketches lets a caller supply the fold as
    /// code, over a summary of any type. That cannot survive being written to a stream,
    /// so this offers a fixed set over a single number instead, which is what the
    /// aggregations people actually reach for need.
    /// </para>
    /// </remarks>
    public enum SummaryPolicy
    {
        /// <summary>Adds the values, giving a total per distinct key.</summary>
        Sum = 0,

        /// <summary>Keeps the smallest value seen for the key.</summary>
        Min = 1,

        /// <summary>Keeps the largest value seen for the key.</summary>
        Max = 2,
    }
}
