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
    /// forever -- and it passes BigCrush. Picking an index asks little of a generator,
    /// and this asks little in return; how evenly an index is picked is decided by
    /// <see cref="NextBelow"/>, not by the generator.
    /// </para>
    /// <para>
    /// <b>Streams.</b> The state steps by an odd increment at every draw, and that is
    /// SplitMix's splittable form: a stream is its state and its increment, and two
    /// streams with the same increment are one sequence read from two places. Every
    /// generator here steps by <see cref="Gamma"/> unless it is built with another, and
    /// only the private sketches of a <see cref="DpswSketch"/> are, each with one of
    /// <see cref="SplitIncrement"/>'s, so that no two of a window's sketches draw the
    /// same noise.
    /// </para>
    /// </remarks>
    internal struct SeededRandom
    {
        private const ulong Gamma = 0x9E3779B97F4A7C15;

        /// <summary>The low 62 bits, which <see cref="SplitIncrement"/> spreads an ordinal over.</summary>
        private const ulong Low62 = (1UL << 62) - 1;

        /// <summary>
        /// Alternating bits across the low 62, the remedy for an increment with too few
        /// changes between adjacent bits.
        /// </summary>
        private const ulong Alternating62 = 0x2AAA_AAAA_AAAA_AAAA;

        /// <summary>
        /// The generator's entire position in its sequence. Persisted, and restored on
        /// read, so a filter continues rather than starting over.
        /// </summary>
        internal ulong State;

        /// <summary>
        /// The increment, held as its exclusive or with <see cref="Gamma"/>, so that a
        /// generator given none -- the struct's default included -- steps by Gamma, as
        /// every generator here always has, rather than by nought, which never moves.
        /// </summary>
        private readonly ulong split;

        internal SeededRandom(ulong state) => this.State = state;

        /// <summary>
        /// A generator whose state steps by <paramref name="increment"/> instead of
        /// <see cref="Gamma"/>: one stream of those <see cref="SplitIncrement"/> numbers.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The increment is even, so its stream would not visit every state.
        /// </exception>
        internal SeededRandom(ulong state, ulong increment)
        {
            if ((increment & 1) == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(increment), increment,
                    "A stream's increment is odd, so that it visits every state before it repeats.");
            }

            this.State = state;
            this.split = increment ^ Gamma;
        }

        /// <summary>What the state steps by at every draw.</summary>
        internal ulong Increment => this.split ^ Gamma;

        /// <summary>
        /// The increment of the <paramref name="ordinal"/>-th stream split from one
        /// parent: odd, and different for every ordinal below 2^62.
        /// </summary>
        /// <remarks>
        /// The paper gives each stream split from another an increment of its own, mixed
        /// from a draw of the parent, so two streams share one only by chance. These are
        /// numbered instead, so that they cannot. Two streams stepping by different
        /// increments never pass through two consecutive states in common -- the steps
        /// between them differ -- so what two can share is single draws, scattered, by
        /// chance, as any two generators can; with the same increment they share every draw
        /// from where one meets the other.
        /// <para>
        /// The ordinal is spread over 62 bits by a bijection, a Weyl step by Gamma and this
        /// generator's own mix, each step invertible modulo 2^62, then shifted up two bits
        /// with the lowest set, so that every increment is odd. Spread rather than used as
        /// it stands, because increments that are small multiples of one another give
        /// streams that meet at a stride: one stepping by three times another's passes
        /// through every third state of it. The paper's implementation,
        /// Java's SplittableRandom, also asks an increment for at least 24 changes between
        /// adjacent bits, which a mixed one lacks about once in 67 ordinals, and mends one
        /// that has fewer by an exclusive or with alternating bits, which gives it at least
        /// 41, since the two counts sum to 64.
        /// That is done here to the 62 bits, and the second bit is set to say so. A mended
        /// increment then differs from every unmended one in that bit and from every other
        /// mended one in the rest, so the increments stay distinct. Java's mending can
        /// map two values to one, which costs it nothing, since its increments are distinct
        /// only by chance anyway.
        /// </para>
        /// <para>
        /// An ordinal of 2^62 or more is reduced modulo 2^62 and can repeat one below it.
        /// A window builds fewer sketches for a substream than twice the items the substream
        /// holds, so the ordinals it numbers them by reach that only as its position nears
        /// 2^61.
        /// </para>
        /// </remarks>
        internal static ulong SplitIncrement(ulong ordinal)
        {
            var z = ((ordinal + 1) * Gamma) & Low62;
            z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & Low62;
            z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & Low62;
            z ^= z >> 31;

            var increment = (z << 2) | 1;
            if (ulong.PopCount(increment ^ (increment >> 1)) < 24)
            {
                increment = ((z ^ Alternating62) << 2) | 3;
            }

            return increment;
        }

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
            var z = this.State += this.split ^ Gamma;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            return z ^ (z >> 31);
        }

        /// <summary>
        /// Returns a value in [0, bound), for a bound of at least one.
        /// </summary>
        /// <remarks>
        /// Lemire's multiply-and-shift rather than a modulo: it costs a multiply instead
        /// of a division. It has no rejection step, so it is exact only for a bound that
        /// is a power of two. For any other, each value is drawn by floor(2^32 / bound)
        /// of the 2^32 inputs or by one more. That puts every value within 2^-32 of
        /// 1/bound, but the two counts differ by a factor of 1 + 1 / floor(2^32 / bound):
        /// close to one for a small bound, and two once the bound passes 2^31. The
        /// stable filter's cell count reaches that range and VarOpt's light region comes
        /// close to it, which is why the one draws its decay and the other its evictions
        /// with a 64-bit multiply of their own, <see cref="StableBloomFilter.DecayStart"/>
        /// and <see cref="VarOpt.EvictionSlot"/>, and not with this.
        /// <para>
        /// Changing this changes more than seeded sequences. <see cref="SetSketch"/>
        /// shuffles each element's registers with a generator seeded from the element's
        /// hash, so this decides which registers the elements of a stored sketch went
        /// to, and a sketch read back has to go on agreeing with it. That is also why a
        /// very large sketch whose register count is not a power of two estimates
        /// cardinality slightly low, which <see cref="SetSketch"/> documents.
        /// </para>
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
