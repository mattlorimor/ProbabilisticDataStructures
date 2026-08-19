using System;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// SetSketch implements the sketch of Otmar Ertl, SetSketch: Filling the Gap between
    /// MinHash and HyperLogLog (VLDB 2021).
    /// </summary>
    /// <remarks>
    /// MinHash and HyperLogLog answer different questions and are usually kept as two
    /// structures. HyperLogLog counts distinct elements in very little room but can say
    /// nothing about how two sets relate beyond what inclusion-exclusion can wring out
    /// of three cardinalities. MinHash compares sets well but spends four or eight bytes
    /// on every one of its components and estimates cardinality poorly.
    /// <para>
    /// SetSketch is one structure that does both, and a dial between them. Its registers
    /// hold the largest value of <c>floor(1 - log_b h)</c> seen so far, where h is an
    /// exponentially distributed hash of an element. The base b is the dial: as b falls
    /// towards one the registers grow finer and the sketch behaves like MinHash, and as
    /// it rises towards two they coarsen towards HyperLogLog. Cardinality estimation
    /// barely notices the difference; similarity estimation improves markedly as b
    /// falls.
    /// </para>
    /// <para>
    /// At the paper's own configuration -- 4,096 registers, b = 1.001 -- the sketch
    /// takes 8 kB, estimates cardinality to about 1.6%, and estimates Jaccard
    /// similarity about as well as a MinHash with the same number of components, which
    /// would take two to four times the room.
    /// </para>
    /// <para>
    /// Registers combine under maximum, so sketches merge, and the merge is both
    /// idempotent and commutative. That is not a convenience: the paper shows the
    /// maximum is the <em>only</em> operation with those properties that also allows
    /// cardinality to be estimated at all.
    /// </para>
    /// </remarks>
    public class SetSketch : IBinaryPersistable<SetSketch>
    {
        /// <summary>
        /// The register values, each in the range nought to q + 1.
        /// </summary>
        private readonly ushort[] registers;

        private readonly int m;
        private readonly double b;
        private readonly double a;
        private readonly int q;
        private readonly double lnBase;

        /// <summary>Which construction draws this sketch's runs of hash values.</summary>
        private readonly SetSketchVariant variant;

        /// <summary>
        /// How many intervals must remain for SetSketch2's next point to be worth
        /// drawing, given the lower bound. Kept alongside the bound rather than
        /// recomputed per point; see <see cref="SetLowerBound"/> for the derivation.
        /// </summary>
        private double intervalRemainingFloor;

        /// <summary>
        /// A value no register is below.
        /// </summary>
        /// <remarks>
        /// This is what makes an insertion cost constant time rather than time
        /// proportional to the number of registers. Register values only ever rise, so
        /// once every register is at least this high, any hash value too large to reach
        /// it cannot change anything and the element can be abandoned early.
        /// </remarks>
        private int lowerBound;

        /// <summary>
        /// How many register updates remain before the lower bound is worked out again.
        /// </summary>
        /// <remarks>
        /// Maintaining the true minimum as registers change would need a heap or a
        /// histogram alongside. The paper instead rescans every m updates, which costs
        /// time proportional to the registers but only once per m of them, so it adds a
        /// constant to each.
        /// </remarks>
        private int updatesUntilRescan;

        /// <summary>
        /// A lazily shuffled permutation of the register indices, and the pass each of
        /// its entries was last written on.
        /// </summary>
        /// <remarks>
        /// Each element assigns its hash values to registers in a random order, and
        /// almost every element abandons that order after a step or two. Shuffling all
        /// m indices per element would undo the constant-time insertion the lower bound
        /// buys, so the shuffle is done lazily and, rather than being cleared between
        /// elements, is stamped with a pass number: an entry from an earlier pass is
        /// read as though it were still in its starting place.
        /// </remarks>
        private readonly uint[] permutation;
        private readonly uint[] permutationPass;
        private uint pass;

        /// <summary>
        /// How many hash values have been drawn across every insertion so far.
        /// </summary>
        /// <remarks>
        /// Kept so that the constant-time claim can be asserted as work rather than as
        /// wall-clock time, which would depend on the machine and on what else it was
        /// doing. An insertion that stopped skipping would still give every right
        /// answer; it would just draw m values per element instead of a couple.
        /// </remarks>
        private long drawn;

        private Func<ReadOnlySpan<byte>, ulong> Hash { get; set; }

        /// <summary>
        /// The number of registers in the paper's own worked configuration.
        /// </summary>
        internal const int DefaultRegisters = 4096;

        /// <summary>
        /// The base the paper recommends when similarity matters, and the one that
        /// makes this a MinHash rather than a HyperLogLog.
        /// </summary>
        internal const double DefaultBase = 1.001;

        /// <summary>
        /// The rate of the exponential hash values.
        /// </summary>
        /// <remarks>
        /// This sets where the register values sit, and has to be large enough that a
        /// set of one element does not want a register below nought. The paper puts
        /// twenty as a good choice in almost every case: even with a million registers
        /// and the finest base, the chance of wanting a negative register is under a
        /// quarter of a percent.
        /// </remarks>
        internal const double DefaultRate = 20;

        /// <summary>
        /// The largest register value, chosen so that a register fits two bytes.
        /// </summary>
        internal const int DefaultQ = 65534;

        /// <summary>
        /// Creates a sketch with the paper's worked configuration: 4,096 registers and
        /// a base of 1.001, which is 8 kB and estimates both cardinality and similarity
        /// well.
        /// </summary>
        /// <param name="hash">
        /// The hash function to use, or null for the default.
        /// </param>
        public SetSketch(Func<ReadOnlySpan<byte>, ulong>? hash = null)
            : this(DefaultRegisters, DefaultBase, DefaultRate, DefaultQ, hash)
        {
        }

        /// <summary>
        /// Creates a sketch with the given number of registers and the paper's other
        /// defaults.
        /// </summary>
        /// <param name="registers">
        /// How many registers to keep. The relative error of a cardinality estimate is
        /// about one over its square root.
        /// </param>
        /// <param name="hash">
        /// The hash function to use, or null for the default.
        /// </param>
        public SetSketch(int registers, Func<ReadOnlySpan<byte>, ulong>? hash = null)
            : this(registers, DefaultBase, DefaultRate, DefaultQ, hash)
        {
        }

        /// <summary>
        /// Creates a sketch, choosing every parameter.
        /// </summary>
        /// <param name="registers">How many registers to keep.</param>
        /// <param name="b">
        /// The base. Towards one the sketch behaves like MinHash and compares sets
        /// well; towards two it behaves like HyperLogLog and needs fewer distinct
        /// register values to cover the same range of cardinalities. Above two the
        /// approximations the estimators rest on start to fray, so it is refused.
        /// </param>
        /// <param name="a">The rate of the exponential hash values.</param>
        /// <param name="q">
        /// The largest register value. Together with the base this fixes the largest
        /// cardinality the sketch can represent, which is about b to this power over
        /// the rate.
        /// </param>
        /// <param name="hash">
        /// The hash function to use, or null for the default.
        /// </param>
        /// <param name="variant">
        /// Which of the paper's two constructions draws the runs of hash values.
        /// The default keeps the registers independent, which is what makes the
        /// estimators exact rather than approximate; see <see cref="SetSketchVariant"/>.
        /// </param>
        public SetSketch(
            int registers, double b, double a, int q,
            Func<ReadOnlySpan<byte>, ulong>? hash = null,
            SetSketchVariant variant = SetSketchVariant.SetSketch1)
        {
            if (variant != SetSketchVariant.SetSketch1
                && variant != SetSketchVariant.SetSketch2)
            {
                throw new ArgumentOutOfRangeException(nameof(variant),
                    variant, "The paper describes two constructions, and this is " +
                    "neither of them.");
            }
            if (registers < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(registers),
                    "A sketch needs at least one register.");
            }
            if (double.IsNaN(b) || b <= 1 || b > 2)
            {
                throw new ArgumentOutOfRangeException(nameof(b),
                    $"The base is {b}. At one the register values would not move with " +
                    "the cardinality at all, and the estimators here rest on " +
                    "approximations the paper only claims up to two.");
            }
            if (double.IsNaN(a) || a <= 0 || double.IsInfinity(a))
            {
                throw new ArgumentOutOfRangeException(nameof(a),
                    "The rate of the hash values has to be a positive number.");
            }
            if (q < 1 || q > ushort.MaxValue - 1)
            {
                throw new ArgumentOutOfRangeException(nameof(q),
                    $"The largest register value is {q}. A register holds two bytes, " +
                    $"and has to hold one more than this, so it may not exceed " +
                    $"{ushort.MaxValue - 1}.");
            }

            this.m = registers;
            this.b = b;
            this.a = a;
            this.q = q;
            this.lnBase = Math.Log(b);
            this.registers = new ushort[registers];
            this.permutation = new uint[registers];
            this.permutationPass = new uint[registers];
            this.pass = 0;
            this.updatesUntilRescan = registers;
            this.variant = variant;
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();
            this.SetLowerBound(0);
        }

        /// <summary>How many registers the sketch keeps.</summary>
        public int Registers => this.m;

        /// <summary>The base, which trades similarity accuracy against range.</summary>
        public double Base => this.b;

        /// <summary>The rate of the exponential hash values.</summary>
        public double Rate => this.a;

        /// <summary>The largest value a register may hold, less one.</summary>
        public int MaxRegisterValue => this.q;

        /// <summary>Which of the paper's two constructions this sketch uses.</summary>
        public SetSketchVariant Variant => this.variant;

        /// <summary>How many bytes the registers occupy.</summary>
        public long SizeInBytes => (long)this.m * sizeof(ushort);

        /// <summary>
        /// The hash function in use.
        /// </summary>
        internal Func<ReadOnlySpan<byte>, ulong> HashFunction => this.Hash;

        /// <summary>
        /// The registers themselves, so that tests can check two sketches hold the same
        /// state rather than merely agreeing about what they estimate.
        /// </summary>
        internal ushort[] RegisterValues => this.registers;

        /// <summary>
        /// How many hash values every insertion so far has drawn between them.
        /// </summary>
        internal long HashValuesDrawn => this.drawn;

        /// <summary>
        /// Adds the data to the sketch.
        /// </summary>
        /// <param name="data">The data to add.</param>
        /// <returns>The sketch.</returns>
        public SetSketch Add(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            return this.Add(data.AsSpan());
        }

        /// <inheritdoc cref="Add(byte[])"/>
        /// <remarks>
        /// An element draws an ascending run of exponentially distributed values and
        /// hands them to registers in a random order. Because the run ascends, the
        /// first value too large to beat the lower bound ends the element: everything
        /// after it is larger still. That is what turns an insertion that looks like it
        /// must touch every register into one that usually touches one.
        /// </remarks>
        public SetSketch Add(ReadOnlySpan<byte> data)
        {
            var random = new SeededRandom(this.Hash(data));
            BeginPass();

            var x = 0.0;
            for (var i = 0; i < this.m; i++)
            {
                if (this.variant == SetSketchVariant.SetSketch1)
                {
                    this.drawn++;
                    x += Exponential(ref random) / (this.a * (this.m - i));
                }
                else
                {
                    // The i-th point cannot fall below the i-th interval's start, and
                    // the register value a point earns falls as the point rises. So if
                    // the interval's start alone already fails to beat the bound,
                    // nothing drawn from this interval or any later one can beat it,
                    // and the element is finished without spending a draw at all.
                    // This is where SetSketch2's speed comes from: the bound is a
                    // deterministic function of the interval, and SetSketch1 has no
                    // equivalent -- its next point is not known until it is drawn.
                    // SetLowerBound has already done the logarithms, so what is left
                    // here is one comparison.
                    if (this.StopsAtInterval(i))
                    {
                        return this;
                    }

                    this.drawn++;
                    x = this.IntervalPoint(ref random, i);
                }

                var k = ValueFor(x);
                if (k <= this.lowerBound)
                {
                    return this;
                }

                Update(NextIndex(ref random, i), k);
            }

            return this;
        }

        /// <summary>
        /// SetSketch2's draw: one point from the i-th of the m disjoint intervals the
        /// exponential's domain is cut into.
        /// </summary>
        /// <remarks>
        /// The paper puts the boundaries at gamma_j = ln(1 + j/(m-j)) / a, which is
        /// ln(m/(m-j)) / a, and draws x_j from an exponential truncated to
        /// [gamma_(j-1), gamma_j). Writing that out, the truncated draw's rate over the
        /// i-th interval is ln((m-i)/(m-i-1)) and the mass it spans is 1/(m-i), so
        /// inverting its distribution leaves one expression:
        /// <code>
        ///   x = ( ln(m/(m-i)) - ln(1 - u/(m-i)) ) / a
        /// </code>
        /// <para>
        /// At i = m-1 the last interval runs to infinity, and the reference
        /// implementation draws it as gamma + Exp(a) rather than as a truncated
        /// exponential. It does not need a separate case here: at i = m-1 the term
        /// 1 - u/(m-i) is exactly 1 - u, so the expression above already <i>is</i>
        /// gamma + Exp(a). One line covers all m intervals, and that agreement is
        /// checked as a test rather than asserted here.
        /// </para>
        /// <para>
        /// Successive points still ascend, because the i-th lies inside the i-th
        /// interval and the intervals ascend. That is what lets the caller stop at the
        /// first value too large to matter, exactly as it does for SetSketch1.
        /// </para>
        /// </remarks>
        private double IntervalPoint(ref SeededRandom random, int i)
        {
            // One less the uniform value for the same reason Exponential does it: the
            // generator's range includes nought, and at i = m-1 a u of exactly one
            // would ask for the logarithm of nought.
            return PointInInterval(this.m, this.a, i, 1 - random.NextDouble());
        }

        /// <summary>
        /// The point SetSketch2 draws from the i-th interval, given a uniform value.
        /// Separated from the draw so that a test can hold it to the paper's two-case
        /// definition at chosen inputs rather than to itself.
        /// </summary>
        internal static double PointInInterval(int m, double a, int i, double u)
        {
            var remaining = (double)(m - i);
            return (Math.Log(m / remaining) - Math.Log(1 - (u / remaining))) / a;
        }

        /// <summary>
        /// Where the i-th of SetSketch2's m intervals begins: the paper's
        /// gamma_i = ln(1 + i/(m-i)) / a, written as ln(m/(m-i)) / a. Every point drawn
        /// from this interval or a later one is at least this large.
        /// </summary>
        internal static double IntervalStart(int m, double a, int i) =>
            Math.Log(m / (double)(m - i)) / a;

        /// <summary>
        /// Whether SetSketch2 stops at the i-th interval, decided the slow and literal
        /// way: ask what value the interval's start would earn and compare it to the
        /// bound. Exists so a test can hold <see cref="StopsAtInterval"/> to it.
        /// </summary>
        internal bool LiterallyStopsAtInterval(int i) =>
            this.ValueFor(IntervalStart(this.m, this.a, i)) <= this.lowerBound;

        /// <summary>
        /// Whether SetSketch2 stops at the i-th interval. The insert path calls this
        /// rather than repeating the comparison, so that the sweep holding it to
        /// <see cref="LiterallyStopsAtInterval"/> is testing the rule the insert path
        /// actually applies. When the loop kept its own copy, relaxing that copy from
        /// strict to loose changed the sketch and passed every test: two copies agreed
        /// with each other while the sweep proved nothing about the one that ran.
        /// </summary>
        internal bool StopsAtInterval(int i) => this.m - i < this.intervalRemainingFloor;

        /// <summary>Sets the bound directly, so a test can sweep it.</summary>
        internal void ForceLowerBound(int value) => this.SetLowerBound(value);

        /// <summary>
        /// Estimates how many distinct elements have been added.
        /// </summary>
        /// <remarks>
        /// The paper's closed-form estimator rather than the maximum-likelihood one it
        /// derives alongside: they agree almost exactly, and this one is a sum.
        /// </remarks>
        public double Cardinality()
        {
            var sum = 0.0;
            var empty = true;
            foreach (var register in this.registers)
            {
                if (register != 0)
                {
                    empty = false;
                }
                sum += Math.Pow(this.b, -(double)register);
            }

            // Every register still at its starting value means nothing was ever added.
            // The estimator would report a small positive number here, because it is
            // derived for sets of at least one element and knows nothing of empty ones.
            if (empty)
            {
                return 0;
            }

            return this.m * (1 - (1 / this.b)) / (this.a * this.lnBase * sum);
        }

        /// <summary>
        /// Combines this sketch with another, so that it holds what both held.
        /// </summary>
        /// <remarks>
        /// Register by register, the larger wins. The result is exactly the sketch that
        /// would have been built by adding both sets to one sketch from the start --
        /// not an approximation of it -- and adding the same set twice changes nothing.
        /// </remarks>
        /// <param name="other">The sketch to merge into this one.</param>
        /// <returns>True if successful.</returns>
        public bool Merge(SetSketch other)
        {
            ArgumentNullException.ThrowIfNull(other);
            RequireComparable(other, nameof(other));

            for (var i = 0; i < this.m; i++)
            {
                if (other.registers[i] > this.registers[i])
                {
                    this.registers[i] = other.registers[i];
                }
            }

            RescanLowerBound();
            return true;
        }

        /// <summary>
        /// Estimates the Jaccard similarity between the two sets: the size of their
        /// intersection over the size of their union.
        /// </summary>
        /// <param name="other">The sketch to compare with.</param>
        public double Jaccard(SetSketch other) => Compare(other).Jaccard;

        /// <summary>
        /// Estimates how the two sets relate: their sizes, their similarity, and
        /// everything that follows from those.
        /// </summary>
        /// <remarks>
        /// This is the paper's own estimator rather than the obvious one. The obvious
        /// one merges the sketches, estimates the size of the union, and recovers the
        /// intersection by inclusion and exclusion -- which works, but throws away
        /// everything except three cardinalities. The paper instead looks at the
        /// registers pairwise and counts how many are larger, smaller, and equal, then
        /// asks which similarity would most likely have produced those three counts.
        /// It dominates inclusion and exclusion, and it is better than MinHash's own
        /// estimator when the sets differ in size, because MinHash counts only the
        /// registers that match and this uses the direction of the ones that do not.
        /// </remarks>
        /// <param name="other">The sketch to compare with.</param>
        public SetComparison Compare(SetSketch other)
        {
            ArgumentNullException.ThrowIfNull(other);
            RequireComparable(other, nameof(other));

            var mine = Cardinality();
            var theirs = other.Cardinality();

            if (mine <= 0 || theirs <= 0)
            {
                // Nothing in one of them, so nothing in common.
                return new SetComparison(mine, theirs, 0);
            }

            var larger = 0;
            var smaller = 0;
            var equal = 0;
            for (var i = 0; i < this.m; i++)
            {
                if (this.registers[i] > other.registers[i])
                {
                    larger++;
                }
                else if (this.registers[i] < other.registers[i])
                {
                    smaller++;
                }
                else
                {
                    equal++;
                }
            }

            var u = mine / (mine + theirs);
            var v = theirs / (mine + theirs);

            return new SetComparison(mine, theirs,
                MostLikelyJaccard(u, v, larger, smaller, equal));
        }

        /// <summary>
        /// The similarity that best explains how many registers came out larger,
        /// smaller and equal.
        /// </summary>
        /// <remarks>
        /// The paper shows this is strictly concave over the range a similarity may
        /// take given the two sizes, for every base this class allows, so a search that
        /// only ever narrows the bracket will find the maximum. Golden section rather
        /// than the paper's Brent: it needs no derivative, cannot be led astray by a
        /// flat region, and one logarithm per step is not worth economising.
        /// </remarks>
        private double MostLikelyJaccard(double u, double v, int larger, int smaller, int equal)
        {
            // A similarity cannot exceed the smaller set's share of the larger one.
            var high = Math.Min(u / v, v / u);

            if (larger == 0 && smaller == 0)
            {
                // Every register agrees, which is what identical sets look like. The
                // likelihood rises all the way to the end of the range.
                return high;
            }

            if (equal == 0)
            {
                // No register agrees, which is what disjoint sets look like.
                return 0;
            }

            var low = 0.0;
            var phi = (Math.Sqrt(5) - 1) / 2;

            var c = high - (phi * (high - low));
            var d = low + (phi * (high - low));
            var fc = LogLikelihood(c, u, v, larger, smaller, equal);
            var fd = LogLikelihood(d, u, v, larger, smaller, equal);

            // Enough steps to shrink the bracket by more than a factor of a billion,
            // which is far below the error of the estimate it is refining.
            for (var step = 0; step < 100 && high - low > 1e-12; step++)
            {
                if (fc > fd)
                {
                    high = d;
                    d = c;
                    fd = fc;
                    c = high - (phi * (high - low));
                    fc = LogLikelihood(c, u, v, larger, smaller, equal);
                }
                else
                {
                    low = c;
                    c = d;
                    fc = fd;
                    d = low + (phi * (high - low));
                    fd = LogLikelihood(d, u, v, larger, smaller, equal);
                }
            }

            return Math.Clamp((low + high) / 2, 0, 1);
        }

        /// <summary>
        /// How likely the observed register comparisons are, if the similarity were
        /// this.
        /// </summary>
        private double LogLikelihood(
            double j, double u, double v, int larger, int smaller, int equal)
        {
            var pLarger = CollisionTerm(u - (v * j));
            var pSmaller = CollisionTerm(v - (u * j));
            var pEqual = 1 - pLarger - pSmaller;

            var total = 0.0;

            if (larger > 0)
            {
                if (pLarger <= 0)
                {
                    return double.NegativeInfinity;
                }
                total += larger * Math.Log(pLarger);
            }

            if (smaller > 0)
            {
                if (pSmaller <= 0)
                {
                    return double.NegativeInfinity;
                }
                total += smaller * Math.Log(pSmaller);
            }

            if (equal > 0)
            {
                if (pEqual <= 0)
                {
                    return double.NegativeInfinity;
                }
                total += equal * Math.Log(pEqual);
            }

            return total;
        }

        /// <summary>
        /// The chance that one sketch's register beats the other's, as a function of
        /// how much of one set lies outside the other.
        /// </summary>
        /// <remarks>
        /// The paper's p_b. Written through the logarithm of one plus a small quantity,
        /// because at the bases this sketch is interesting at the quantity is of order
        /// a thousandth and taking its logarithm the direct way would throw away most
        /// of the precision in it.
        /// </remarks>
        private double CollisionTerm(double x) =>
            -double.LogP1(-x * (this.b - 1) / this.b) / this.lnBase;

        /// <summary>
        /// Restores the sketch to its original state.
        /// </summary>
        public SetSketch Reset()
        {
            Array.Clear(this.registers);
            Array.Clear(this.permutationPass);
            this.pass = 0;
            this.updatesUntilRescan = this.m;
            this.SetLowerBound(0);
            return this;
        }

        /// <summary>
        /// Writes the sketch to a stream.
        /// </summary>
        /// <remarks>
        /// The registers and the four parameters that give them meaning. The lower
        /// bound the insert path keeps is not written: it is an accelerator, any value
        /// no register is below will do, and the registers themselves say what it
        /// should be.
        /// </remarks>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = new PayloadWriter();
            payload.WriteDouble(this.b);
            payload.WriteDouble(this.a);
            payload.WriteUInt32((uint)this.m);
            payload.WriteUInt32((uint)this.q);

            // The variant is written only when it is not the default one, and the
            // version bumped only then. A sketch built the way every sketch was built
            // before this variant existed still writes the bytes it always wrote, and
            // still loads in a library too old to know there was a choice. Bumping
            // unconditionally would make every SetSketch payload unreadable to those
            // libraries to record a change that the sketches in them did not make.
            var version = PersistenceFormat.DefaultVersion;
            if (this.variant != SetSketchVariant.SetSketch1)
            {
                payload.WriteByte((byte)this.variant);
                version = PersistenceFormat.SetSketchVariantVersion;
            }

            foreach (var register in this.registers)
            {
                payload.WriteUInt16(register);
            }

            PersistenceFormat.Write(
                stream,
                StructureId.SetSketch,
                PersistenceFormat.Identify(this.Hash),
                payload.WrittenSpan,
                version);
        }

        /// <summary>
        /// Reads a sketch written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        public static SetSketch ReadFrom(Stream stream)
        {
            return Read(stream, null);
        }

        /// <summary>
        /// Reads a sketch written by <see cref="WriteTo"/>, installing a hash function.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function the sketch was written with.</param>
        public static SetSketch ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            return Read(stream, hash);
        }

        private static SetSketch Read(Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(
                stream, StructureId.SetSketch, out var hashId, out var version);
            var reader = new PayloadReader(payload);

            var b = reader.ReadDouble();
            var a = reader.ReadDouble();
            var m = reader.ReadUInt32();
            var q = reader.ReadUInt32();

            // Payloads written before the second variant existed carry no variant byte
            // and are the first variant by definition, which is what keeps them
            // readable rather than merely tolerated.
            var variant = SetSketchVariant.SetSketch1;
            if (version >= PersistenceFormat.SetSketchVariantVersion)
            {
                var stored = reader.ReadByte();
                if (stored != (byte)SetSketchVariant.SetSketch2)
                {
                    throw new InvalidDataException(
                        $"Sketch names construction {stored}. A payload at version " +
                        $"{version} carries a variant byte, and the only one it is " +
                        "written with is the second construction -- the first writes " +
                        "no byte at all.");
                }
                variant = SetSketchVariant.SetSketch2;
            }

            if (double.IsNaN(b) || b <= 1 || b > 2)
            {
                throw new InvalidDataException(
                    $"Sketch claims a base of {b}. At one the register values would " +
                    "not move with the cardinality at all, and this library only " +
                    "writes bases up to two.");
            }
            if (double.IsNaN(a) || a <= 0 || double.IsInfinity(a))
            {
                throw new InvalidDataException(
                    $"Sketch claims a hash rate of {a}, and a rate is a positive " +
                    "number.");
            }
            if (m == 0 || m > 1 << 26)
            {
                throw new InvalidDataException(
                    $"Sketch claims {m} registers. A sketch has at least one, and this " +
                    "many would be half a gigabyte of them.");
            }
            if (q == 0 || q > ushort.MaxValue - 1)
            {
                throw new InvalidDataException(
                    $"Sketch claims a ceiling of {q}, and a register holds two bytes " +
                    "and has to hold one more than the ceiling.");
            }

            var sketch = new SetSketch(
                (int)m, b, a, (int)q,
                PersistenceFormat.ResolveOrThrow(hashId, hash),
                variant);

            for (var i = 0; i < m; i++)
            {
                var register = reader.ReadUInt16();
                if (register > q + 1)
                {
                    throw new InvalidDataException(
                        $"Register {i} holds {register}, above the {q + 1} this " +
                        "sketch's ceiling allows. It was not written by a sketch with " +
                        "these parameters.");
                }
                sketch.registers[i] = register;
            }

            reader.ExpectEnd();

            sketch.RescanLowerBound();
            return sketch;
        }

        /// <summary>
        /// Sets the hashing function used in the sketch.
        /// </summary>
        /// <param name="h">The hash function to use.</param>
        public void SetHash(Func<ReadOnlySpan<byte>, ulong> h)
        {
            ArgumentNullException.ThrowIfNull(h);
            Guard.HashMayBeReplaced(IsEmpty(), nameof(SetSketch));
            this.Hash = h;
        }

        /// <summary>
        /// Whether nothing has been added.
        /// </summary>
        private bool IsEmpty()
        {
            foreach (var register in this.registers)
            {
                if (register != 0)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Refuses a sketch whose registers cannot be compared with these.
        /// </summary>
        private void RequireComparable(SetSketch other, string paramName)
        {
            Guard.SameHashFunction(this.Hash, other.Hash, paramName);

            if (this.m != other.m || this.b != other.b
                || this.a != other.a || this.q != other.q)
            {
                throw new ArgumentException(
                    $"The sketches were built differently -- {this.m} registers at " +
                    $"base {this.b}, rate {this.a} and ceiling {this.q} against " +
                    $"{other.m}, {other.b}, {other.a} and {other.q}. A register only " +
                    "means the same thing in two sketches that agree on all four.",
                    paramName);
            }

            // The merge's promise is that the result is the sketch adding both sets to
            // one sketch would have built. Across variants there is no such sketch --
            // one sketch draws its runs one way or the other, not both -- so the
            // promise cannot be kept and the merge is refused rather than quietly
            // returning something that only looks right.
            if (this.variant != other.variant)
            {
                throw new ArgumentException(
                    $"One sketch is {this.variant} and the other {other.variant}. " +
                    "Merging promises the sketch that adding both sets to a single " +
                    "sketch would have built, and no single sketch draws its hash " +
                    "values both ways.",
                    paramName);
            }
        }

        /// <summary>
        /// The register value a hash value of this size would produce.
        /// </summary>
        /// <remarks>
        /// This is the paper's <c>floor(1 - log_b x)</c>, held to the range a register
        /// can express. The reference implementation avoids the logarithm by searching
        /// a precomputed table of every power of the base, which at the default
        /// ceiling is 65,536 doubles -- half a megabyte against an eight kilobyte
        /// sketch. One logarithm per element is the better trade here.
        /// </remarks>
        private int ValueFor(double x)
        {
            if (x <= 0)
            {
                return this.q + 1;
            }

            var k = Math.Floor(1 - (Math.Log(x) / this.lnBase));
            if (k <= 0)
            {
                return 0;
            }
            if (k >= this.q + 1)
            {
                return this.q + 1;
            }
            return (int)k;
        }

        /// <summary>
        /// One exponentially distributed value with rate one.
        /// </summary>
        private static double Exponential(ref SeededRandom random)
        {
            // One less the uniform value, so that the argument is never zero: the
            // generator's range includes nought, and the logarithm of nought is not a
            // number this can recover from.
            return -Math.Log(1 - random.NextDouble());
        }

        /// <summary>
        /// Raises a register, and works out the lower bound again when enough have
        /// risen.
        /// </summary>
        private void Update(int index, int k)
        {
            if (k <= this.registers[index])
            {
                return;
            }

            this.registers[index] = (ushort)k;

            this.updatesUntilRescan--;
            if (this.updatesUntilRescan <= 0)
            {
                RescanLowerBound();
            }
        }

        private void RescanLowerBound()
        {
            var min = int.MaxValue;
            foreach (var register in this.registers)
            {
                if (register < min)
                {
                    min = register;
                }
            }

            this.updatesUntilRescan = this.m;
            this.SetLowerBound(min);
        }

        /// <summary>
        /// Records the lower bound, and with it the point past which SetSketch2 stops
        /// drawing.
        /// </summary>
        /// <remarks>
        /// SetSketch2 can tell whether an interval is worth drawing from before it
        /// spends any randomness, because a point never falls below its interval's
        /// start. Doing that literally costs two logarithms an interval -- one for the
        /// start, one inside <see cref="ValueFor"/> -- which measured slower than the
        /// draw it saved. It does not have to be done literally.
        /// <para>
        /// Writing L for the bound, the i-th interval is worth drawing from while
        /// <c>ValueFor(gamma_i) &gt; L</c>. For a bound the register range can actually
        /// hold, that unfolds:
        /// </para>
        /// <code>
        ///   ValueFor(x) &lt;= L   &lt;=&gt;   floor(1 - ln x / ln b) &lt;= L
        ///                      &lt;=&gt;   1 - ln x / ln b &lt; L + 1
        ///                      &lt;=&gt;   x &gt; b^-L
        /// </code>
        /// <para>
        /// and substituting gamma_i = ln(m / (m - i)) / a turns the test into one
        /// comparison against a quantity that depends on nothing but the bound:
        /// </para>
        /// <code>
        ///   stop  &lt;=&gt;  m - i  &lt;  m * exp(-a * b^-L)
        /// </code>
        /// <para>
        /// So the two logarithms per interval become one exponential per change of
        /// bound, which happens once every m updates rather than once per interval.
        /// A bound at or above the ceiling is the one case the algebra does not cover:
        /// every register is saturated, <see cref="ValueFor"/> is clamped to the
        /// ceiling and so can never exceed the bound, and no interval is worth drawing
        /// from. Positive infinity says exactly that.
        /// </para>
        /// </remarks>
        private void SetLowerBound(int value)
        {
            this.lowerBound = value;
            this.intervalRemainingFloor = value >= this.q + 1
                ? double.PositiveInfinity
                : this.m * Math.Exp(-this.a * Math.Pow(this.b, -value));
        }

        /// <summary>
        /// Starts a fresh shuffle of the register indices.
        /// </summary>
        private void BeginPass()
        {
            this.pass++;
            if (this.pass == 0)
            {
                // Once in four billion elements the stamp wraps, and entries from the
                // very first pass would be mistaken for current ones.
                Array.Clear(this.permutationPass);
                this.pass = 1;
            }
        }

        /// <summary>
        /// The next register index in this element's shuffle.
        /// </summary>
        /// <remarks>
        /// One step of Fisher-Yates, reading an entry not written on this pass as
        /// though it still held its own index.
        /// </remarks>
        private int NextIndex(ref SeededRandom random, int i)
        {
            var k = i + (int)random.NextBelow((uint)(this.m - i));

            var atK = this.permutationPass[k] == this.pass ? this.permutation[k] : (uint)k;
            var atI = this.permutationPass[i] == this.pass ? this.permutation[i] : (uint)i;

            this.permutation[k] = atI;
            this.permutationPass[k] = this.pass;

            return (int)atK;
        }
    }
}
