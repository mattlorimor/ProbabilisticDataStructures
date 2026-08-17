using System;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// The random source behind the choices <see cref="StableBloomFilter"/> and
    /// <see cref="CuckooBloomFilter"/> make, whose whole state is one number.
    /// </summary>
    /// <remarks>
    /// This exists instead of <see cref="Random"/> for one reason: a filter that can be
    /// written to a file and read back has to resume the sequence it was partway
    /// through, and <see cref="Random"/> will not say where it is.
    /// <para>
    /// Seeding a fresh <see cref="Random"/> on read was the obvious alternative and is
    /// worse than it looks. The bits come back correct either way, but the draw sequence
    /// restarts, so a filter checkpointed on a schedule replays the same first draws
    /// after every load. The stable filter's bound on its false positive rate depends on
    /// its decay being spread evenly across cells; decay aimed at the same cells after
    /// every restart is not spread evenly, and the structure stops delivering the thing
    /// it exists to deliver. The failure is invisible to the test one would naturally
    /// write for it, because two filters restored from the same payload do agree -- it
    /// is only against a filter that was never written out that they diverge.
    /// </para>
    /// <para>
    /// The algorithm is SplitMix64 (Steele, Lea and Flood, 2014), which is the one
    /// distributed with the xoshiro generators for seeding them. It is used here in its
    /// own right because it suits the requirement exactly: 64 bits of state, no invalid
    /// state to avoid -- zero is an ordinary seed, unlike xorshift, which is stuck at it
    /// forever -- and it passes BigCrush. Picking an index below a few million asks
    /// little of a generator, and this asks little in return.
    /// </para>
    /// </remarks>
    internal struct SeededRandom
    {
        private const ulong Gamma = 0x9E3779B97F4A7C15;

        /// <summary>
        /// The generator's entire position in its sequence. Persisted, and restored on
        /// read, so a filter continues rather than starting over.
        /// </summary>
        internal ulong State;

        internal SeededRandom(ulong state) => this.State = state;

        /// <summary>
        /// Seeds unpredictably, for a filter that was not given a seed.
        /// </summary>
        internal static SeededRandom Unpredictable()
        {
            Span<byte> bytes = stackalloc byte[8];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            return new SeededRandom(BitConverter.ToUInt64(bytes));
        }

        /// <summary>
        /// Advances the generator and returns the next 64 bits.
        /// </summary>
        internal ulong Next()
        {
            var z = this.State += Gamma;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            return z ^ (z >> 31);
        }

        /// <summary>
        /// Returns a value in [0, bound), for a bound of at least one.
        /// </summary>
        /// <remarks>
        /// Lemire's multiply-and-shift rather than a modulo: it costs a multiply instead
        /// of a division. Its bias is under one part in 2^32, and the rejection loop
        /// that would remove the last of it is not worth having for the job this does --
        /// choosing among at most a few million cells, where a bias that small is far
        /// beneath the filter's own approximation error.
        /// </remarks>
        internal uint NextBelow(uint bound)
        {
            return (uint)(((ulong)(uint)(Next() >> 32) * bound) >> 32);
        }

        /// <summary>
        /// Returns a value in [0, 1), built from the top 53 bits so every result is
        /// exactly representable.
        /// </summary>
        internal double NextDouble()
        {
            return (Next() >> 11) * (1.0 / (1UL << 53));
        }
    }
}
