namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Eight bytes of storage, as an ordinary local.
    /// </summary>
    /// <remarks>
    /// For the eight bytes a value is written into so that it can be hashed: the first
    /// value of a 64-bit placement, in <c>Utils.HashKernel128</c> and in
    /// <c>Positions.OfHash</c>, and the digest and fingerprint of a cuckoo filter.
    /// Each was a <c>stackalloc</c>, and a method with a <c>stackalloc</c> cannot be
    /// inlined and carries a zeroed frame with a stack check, on a path every add and
    /// every test takes. This converts to a <see cref="System.Span{T}"/> as a
    /// <c>stackalloc</c> does and holds the same bytes, so what is hashed is unchanged.
    /// </remarks>
    [System.Runtime.CompilerServices.InlineArray(8)]
    internal struct EightBytes
    {
        private byte element;
    }
}
