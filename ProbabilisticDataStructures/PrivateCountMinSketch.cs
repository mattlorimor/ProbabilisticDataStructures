using System;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// A Count-Min Sketch whose counters are seeded with Gaussian noise, so that what it
    /// holds reveals almost nothing about whether any one record was in the stream.
    /// </summary>
    /// <remarks>
    /// This is the private Count-Min Sketch of Zhao et al., as used by Wang, Wang and
    /// Chen in DPSW-Sketch (KDD 2024). Every counter starts at a draw from a normal
    /// distribution rather than at nought, and counting proceeds exactly as a
    /// <see cref="CountMinSketch"/> does. The noise is added once, at construction, not
    /// per query: a caller who asks the same question twice gets the same answer, which
    /// is what stops repeated queries from averaging the noise away.
    /// <para>
    /// <b>What the guarantee is.</b> The sketch satisfies rho-zero-concentrated
    /// differential privacy at the event level: adding, removing or changing any single
    /// record changes the distribution of the whole sketch by a bounded amount.
    /// Concretely, each counter is seeded from a normal distribution of variance
    /// depth / rho, which is the Gaussian mechanism at the l2-sensitivity of a
    /// Count-Min Sketch. Smaller rho means more noise and a stronger guarantee.
    /// </para>
    /// <para>
    /// <b>What this library verifies, and what it does not.</b> The tests here hold the
    /// noise to its stated distribution -- its mean, its variance, its shape, and how
    /// its variance scales with rho and with the depth. They do not prove the privacy
    /// theorem, and no test could: that is a proof about the mechanism, and it is the
    /// authors', not this library's. What the tests can do, and do, is catch the
    /// implementation failing to deliver the distribution the proof assumes. A
    /// mis-scaled mechanism gives a structure that looks like it works and protects
    /// nobody.
    /// </para>
    /// <para>
    /// <b>What it does not protect against.</b> The guarantee is event-level: one
    /// record. A user who appears a thousand times is not protected a thousandfold --
    /// they are protected as one record, a thousand times over, which is much weaker.
    /// The guarantee also assumes the counters are all a caller ever sees; it says
    /// nothing about a system that also publishes the exact stream length, or the same
    /// stream sketched again under a fresh seed.
    /// </para>
    /// <para>
    /// <b>Where an item is counted.</b> A sketch 2^27 columns wide or wider places its
    /// counts with 64-bit hash values, and a narrower one with 32-bit values, exactly as a
    /// <see cref="CountMinSketch"/> does and for the same reason. Every save is format
    /// version 9, which records the placement; a sketch read back keeps the scheme its
    /// payload was placed with.
    /// </para>
    /// </remarks>
    public class PrivateCountMinSketch : IBinaryPersistable<PrivateCountMinSketch>
    {
        private readonly double[][] matrix;
        private readonly uint width;
        private readonly uint depth;
        private readonly double rho;
        private readonly Placement placement;
        private ulong count;

        private Func<ReadOnlySpan<byte>, ulong> Hash { get; set; }

        /// <summary>
        /// How an item's hash becomes the column it is counted in, in each row.
        /// </summary>
        internal Placement Placement => this.placement;

        /// <summary>
        /// The most counters a row can hold, which is the most an array holds. The
        /// constructor refuses more and the reader refuses a payload that claims more.
        /// </summary>
        internal static uint MaxWidth => (uint)Array.MaxLength;

        /// <summary>
        /// The most rows a sketch can have: they are held in one array.
        /// </summary>
        internal static uint MaxDepth => (uint)Array.MaxLength;

        /// <summary>
        /// Builds a sketch of the given shape, seeded with noise for the given privacy
        /// budget.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The width or the depth is zero, or more than one array holds, 2,147,483,591
        /// (and a sketch of that size is gigabytes of noise); or the budget is not a
        /// positive finite number.
        /// </exception>
        /// <param name="width">How many counters each row holds.</param>
        /// <param name="depth">How many rows, and so how many counters an item touches.</param>
        /// <param name="rho">
        /// The zero-concentrated differential privacy budget. Smaller is more private
        /// and less accurate. Use <see cref="BudgetFor"/> to pick one from the epsilon
        /// and delta a policy is written in terms of.
        /// </param>
        /// <param name="seed">
        /// The seed for the noise. Supplying one makes a sketch reproducible, which is
        /// what the tests need -- and what a deployment must not do, since anyone who
        /// knows the seed can subtract the noise back off.
        /// </param>
        /// <param name="hash">The hash function to use, or null for the default.</param>
        public PrivateCountMinSketch(
            uint width, uint depth, double rho,
            ulong? seed = null,
            Func<ReadOnlySpan<byte>, ulong>? hash = null)
            : this(width, depth, rho, seed, hash, null)
        {
        }

        /// <summary>
        /// Builds a sketch as the public constructor does, placing its counts as
        /// <paramref name="placement"/> says, or as its width calls for when that is null.
        /// </summary>
        /// <remarks>
        /// The choice is made by width, and a sketch wide enough for it to matter is a
        /// gigabyte of noise a row. This lets a test have a small sketch place its counts
        /// the way a wide one will, and a <see cref="DpswSketch"/> pass on what its test
        /// asks of it.
        /// </remarks>
        internal PrivateCountMinSketch(
            uint width, uint depth, double rho,
            ulong? seed,
            Func<ReadOnlySpan<byte>, ulong>? hash,
            Placement? placement)
            : this(width, depth, rho,
                seed.HasValue ? new SeededRandom(seed.Value) : SeededRandom.Unpredictable(),
                hash, placement)
        {
        }

        /// <summary>
        /// Builds a sketch as the public constructor does, drawing its noise from
        /// <paramref name="noise"/>: a <see cref="DpswSketch"/> gives each of its sketches a
        /// stream of its own, which a seed alone cannot say.
        /// </summary>
        internal PrivateCountMinSketch(
            uint width, uint depth, double rho,
            SeededRandom noise,
            Func<ReadOnlySpan<byte>, ulong>? hash,
            Placement? placement)
        {
            if (width == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width),
                    "A sketch with no counters in a row divides by zero.");
            }
            if (depth == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(depth),
                    "A sketch with no rows reports every item as seen infinitely often.");
            }
            // A row is one array and the rows are one array, so neither can pass what an
            // array holds. Width had no upper guard and failed in the allocation.
            if (width > MaxWidth)
            {
                throw new ArgumentOutOfRangeException(nameof(width), width,
                    $"A row of {width} counters is more than the {MaxWidth:N0} one array holds.");
            }
            if (depth > MaxDepth)
            {
                throw new ArgumentOutOfRangeException(nameof(depth), depth,
                    $"{depth} rows is more than the {MaxDepth:N0} one array holds.");
            }
            if (double.IsNaN(rho) || rho <= 0 || double.IsInfinity(rho))
            {
                throw new ArgumentOutOfRangeException(nameof(rho),
                    $"The privacy budget is {rho}. It has to be a positive number: at " +
                    "nought the noise would be infinite and at infinity there would be " +
                    "none, and neither is a sketch.");
            }

            this.width = width;
            this.depth = depth;
            this.rho = rho;
            this.placement = placement ?? PlacementFor(width);
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();

            // The l2-sensitivity of a Count-Min Sketch is the square root of twice its
            // depth, because two neighbouring streams differ in one record, and that
            // record moves one counter per row in each of them. The Gaussian mechanism
            // at that sensitivity wants a variance of sensitivity squared over twice
            // the budget, which is depth over the budget.
            var deviation = Math.Sqrt(depth / rho);

            this.NoiseIncrement = noise.Increment;

            this.matrix = new double[depth][];
            for (var i = 0; i < depth; i++)
            {
                this.matrix[i] = new double[width];
                for (var j = 0; j < width; j++)
                {
                    this.matrix[i][j] = deviation * StandardNormal(ref noise);
                }
            }
        }

        /// <summary>How many counters each row holds.</summary>
        public uint Width => this.width;

        /// <summary>How many rows the sketch keeps.</summary>
        public uint Depth => this.depth;

        /// <summary>The zero-concentrated privacy budget the noise was drawn for.</summary>
        public double Rho => this.rho;

        /// <summary>
        /// The standard deviation of the noise on each counter.
        /// </summary>
        public double NoiseDeviation => Math.Sqrt(this.depth / this.rho);

        /// <summary>How many items have been added.</summary>
        public ulong TotalCount() => this.count;

        /// <summary>
        /// The counters themselves, for tests that check the noise is what it claims.
        /// </summary>
        internal double[][] Counters => this.matrix;

        /// <summary>
        /// What the generator the noise was drawn from stepped by, for a test to see which
        /// stream each sketch drew from; nought for a sketch read back, which was given its
        /// noise and drew none.
        /// </summary>
        internal ulong NoiseIncrement { get; }

        /// <summary>
        /// The epsilon of the (epsilon, delta)-differential privacy that a given
        /// zero-concentrated budget provides.
        /// </summary>
        /// <remarks>
        /// A rho-zCDP mechanism is also (rho + 2 sqrt(rho ln(1/delta)), delta)-DP. Most
        /// policies are written in epsilon and delta, and this is the bridge to them.
        /// </remarks>
        /// <param name="rho">The zero-concentrated budget.</param>
        /// <param name="delta">The delta to convert at.</param>
        public static double EpsilonFor(double rho, double delta)
        {
            if (double.IsNaN(rho) || rho <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(rho),
                    "The budget has to be a positive number.");
            }
            if (double.IsNaN(delta) || delta <= 0 || delta >= 1)
            {
                throw new ArgumentOutOfRangeException(nameof(delta),
                    "Delta is a probability short of certainty, so it lies strictly " +
                    "between nought and one.");
            }

            return rho + (2 * Math.Sqrt(rho * Math.Log(1 / delta)));
        }

        /// <summary>
        /// The zero-concentrated budget that delivers a given epsilon and delta.
        /// </summary>
        /// <remarks>
        /// The inverse of <see cref="EpsilonFor"/>, solved as a quadratic in the square
        /// root of the budget. Policies are written in epsilon and delta; the mechanism
        /// wants rho.
        /// </remarks>
        /// <param name="epsilon">The epsilon the policy asks for.</param>
        /// <param name="delta">The delta the policy asks for.</param>
        public static double BudgetFor(double epsilon, double delta)
        {
            if (double.IsNaN(epsilon) || epsilon <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(epsilon),
                    "Epsilon has to be a positive number.");
            }
            if (double.IsNaN(delta) || delta <= 0 || delta >= 1)
            {
                throw new ArgumentOutOfRangeException(nameof(delta),
                    "Delta is a probability short of certainty, so it lies strictly " +
                    "between nought and one.");
            }

            // epsilon = rho + 2 sqrt(rho L) with L = ln(1/delta). Writing r for the
            // square root of rho gives r^2 + 2r sqrt(L) - epsilon = 0.
            var l = Math.Log(1 / delta);
            var root = -Math.Sqrt(l) + Math.Sqrt(l + epsilon);
            return root * root;
        }

        /// <summary>
        /// Adds an item to the sketch.
        /// </summary>
        /// <param name="data">The item.</param>
        public PrivateCountMinSketch Add(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            return this.Add(data.AsSpan());
        }

        /// <inheritdoc cref="Add(byte[])"/>
        public PrivateCountMinSketch Add(ReadOnlySpan<byte> data)
        {
            var columns = ColumnsOf(data, this.Hash, this.placement, this.width);

            for (uint i = 0; i < this.depth; i++)
            {
                this.matrix[i][columns[i]]++;
            }

            this.count++;
            return this;
        }

        /// <summary>
        /// The estimated number of times an item was added.
        /// </summary>
        /// <remarks>
        /// The smallest of the counters the item touches, as in a plain Count-Min
        /// Sketch. Two things change once the counters carry noise. The estimate is no
        /// longer an upper bound -- it can fall below the truth, and for an item that
        /// was never added it can fall below nought -- and taking the minimum of several
        /// noisy counters pulls it downwards, because the minimum of several draws is
        /// below their average.
        /// <para>
        /// Nothing is clamped here. Clamping would be free of privacy cost, since
        /// anything computed from a private result stays private, but it would hide the
        /// noise from a caller who needs to see it to know what they are holding. Clamp
        /// at the point of use if a negative frequency is meaningless there.
        /// </para>
        /// </remarks>
        /// <param name="data">The item to estimate.</param>
        public double Count(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            return this.Count(data.AsSpan());
        }

        /// <inheritdoc cref="Count(byte[])"/>
        public double Count(ReadOnlySpan<byte> data)
        {
            var columns = ColumnsOf(data, this.Hash, this.placement, this.width);
            var smallest = double.MaxValue;

            for (uint i = 0; i < this.depth; i++)
            {
                smallest = Math.Min(smallest, this.matrix[i][columns[i]]);
            }

            return smallest;
        }

        /// <summary>
        /// The placement a new sketch <paramref name="width"/> columns wide is built with.
        /// </summary>
        /// <remarks>
        /// The constructor's choice, reachable on its own so that the widths at which it
        /// changes can be checked without drawing the noise for sketches of those widths:
        /// 64-bit values from <see cref="BitPlacement.WideFrom"/> columns, and 32-bit ones,
        /// as every sketch always had, below it.
        /// </remarks>
        internal static Placement PlacementFor(uint width) => BitPlacement.For(width);

        /// <summary>
        /// The columns <paramref name="data"/> is counted in under
        /// <paramref name="placement"/>, in a sketch <paramref name="width"/> wide: row
        /// <c>i</c> takes the <c>i</c>th.
        /// </summary>
        /// <remarks>
        /// What <see cref="Add(ReadOnlySpan{byte})"/> and
        /// <see cref="Count(ReadOnlySpan{byte})"/> use, reachable without a sketch so that
        /// a test can ask it at widths no test can allocate.
        /// </remarks>
        internal static Positions ColumnsOf(
            ReadOnlySpan<byte> data, Func<ReadOnlySpan<byte>, ulong> hash, Placement placement, uint width) =>
            Positions.Of(data, hash, placement, width);

        /// <summary>
        /// Writes this sketch to a stream, in the format documented in FORMAT.md.
        /// </summary>
        /// <remarks>
        /// <b>The seed is not written, and must never be.</b> The counters are safe to
        /// write because they already carry the noise: a payload of them reveals no
        /// more than the live sketch does. The seed is the opposite -- anyone holding
        /// it can regenerate the noise, subtract it back off, and recover the exact
        /// counts, which is the entire guarantee gone. So a sketch read back has no
        /// seed at all. It can be queried, and it can keep counting, because adding
        /// touches no randomness; what it cannot do is tell anyone where its noise
        /// came from.
        /// </remarks>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = new PayloadWriter();
            this.WriteBody(payload);

            PersistenceFormat.Write(
                stream,
                StructureId.PrivateCountMinSketch,
                PersistenceFormat.Identify(this.Hash),
                payload.WrittenSpan,
                PersistenceFormat.ColumnPlacementVersion);
        }

        /// <summary>
        /// Reads a sketch written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The sketch that was written, with no seed.</returns>
        public static PrivateCountMinSketch ReadFrom(Stream stream)
        {
            return Read(stream, null, ReadOptions.Default);
        }

        /// <summary>
        /// Reads a structure written by <see cref="WriteTo"/>, allocating no more than the
        /// options' memory budget allows. <see cref="ReadFrom(Stream)"/> reads under
        /// <see cref="ReadOptions.Default"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="options">The most memory the read may allocate.</param>
        /// <returns>The structure that was written.</returns>
        /// <exception cref="InvalidDataException">
        /// Reading the payload would allocate more than the budget allows, among the other
        /// reasons a payload is refused. Its inner exception is then a
        /// <see cref="ReadBudgetExceededException"/>.
        /// </exception>
        public static PrivateCountMinSketch ReadFrom(Stream stream, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, null, options);
        }

        /// <summary>
        /// Reads a sketch written by <see cref="WriteTo"/>, using the supplied hash
        /// function rather than the one named in the payload.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function the sketch was written with.</param>
        /// <returns>The sketch that was written, with no seed.</returns>
        public static PrivateCountMinSketch ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            return Read(stream, hash, ReadOptions.Default);
        }

        /// <summary>
        /// Reads a structure as
        /// <see cref="ReadFrom(Stream, Func{ReadOnlySpan{byte}, ulong})"/> does, allocating
        /// no more than the options' memory budget allows.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function, as for the overload without options.</param>
        /// <param name="options">The most memory the read may allocate.</param>
        /// <returns>The structure that was written.</returns>
        /// <exception cref="InvalidDataException">
        /// Reading the payload would allocate more than the budget allows, among the other
        /// reasons a payload is refused. Its inner exception is then a
        /// <see cref="ReadBudgetExceededException"/>.
        /// </exception>
        public static PrivateCountMinSketch ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, hash, options);
        }

        private static PrivateCountMinSketch Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.PrivateCountMinSketch);
            var payload = PersistenceFormat.Read(
                stream, StructureId.PrivateCountMinSketch, out var hashId, out var version, budget, Price,
                hash, PersistenceFormat.CheckResolvable);
            var reader = new PayloadReader(payload, budget);

            return ReadBody(ref reader, PersistenceFormat.ResolveOrThrow(hashId, hash), version);
        }

        /// <summary>
        /// Prices what <see cref="Read"/> allocates beyond the envelope. See
        /// <see cref="PayloadPrice"/>.
        /// </summary>
        internal static void Price(ref PayloadReader reader, ushort version)
        {
            PriceBody(ref reader, version);
        }

        /// <summary>
        /// Prices what <see cref="ReadBody"/> allocates, the matrix, and moves past the
        /// body, laid out as format version <paramref name="version"/> lays it out. Returns
        /// the shape it claims, for a price that checks it as its reader does.
        /// </summary>
        internal static (uint Width, uint Depth) PriceBody(ref PayloadReader reader, ushort version)
        {
            var width = reader.ReadUInt32();
            var depth = reader.ReadUInt32();
            BitPlacement.Read(ref reader, version, PersistenceFormat.ColumnPlacementVersion);
            var rho = reader.ReadDouble();
            reader.ReadUInt64();
            CheckShape(ref reader, width, depth, rho);

            reader.Budget.Price(MatrixCost(width, depth));

            // The shape check has held the counters to the bytes that remain, so this is
            // at most what remains and cannot wrap.
            reader.Skip((long)width * depth * sizeof(double));
            return (width, depth);
        }

        /// <summary>
        /// Checks the shape a body claims, as both the read and its price do, before the
        /// matrix is priced or allocated.
        /// </summary>
        private static void CheckShape(ref PayloadReader reader, uint width, uint depth, double rho)
        {
            if (width == 0 || depth == 0)
            {
                throw new InvalidDataException(
                    $"Sketch has a {depth} by {width} matrix. A sketch with no rows " +
                    "reports every item as seen infinitely often, and one with no " +
                    "columns divides by zero.");
            }
            // The constructor's own limits, so that every sketch it builds reads back and
            // a few bytes cannot ask for a row that no array could hold.
            if (width > MaxWidth)
            {
                throw new InvalidDataException(
                    $"Sketch claims rows {width} counters wide, beyond anything this library " +
                    $"builds: a row is one array, of at most {MaxWidth}.");
            }
            if (depth > MaxDepth)
            {
                throw new InvalidDataException(
                    $"Sketch claims {depth} rows, beyond anything this library builds: " +
                    $"they are one array, of at most {MaxDepth}.");
            }
            if (double.IsNaN(rho) || rho <= 0 || double.IsInfinity(rho))
            {
                throw new InvalidDataException(
                    $"Sketch claims a privacy budget of {rho}. It has to be a positive " +
                    "number: at nought the noise would be infinite and at infinity " +
                    "there would be none, and neither is a sketch.");
            }

            // Every counter is written, so the matrix is checked against the bytes behind
            // it before a row is priced or allocated: a few bytes claiming millions of
            // counters had the reader allocate their rows before it found them missing,
            // and the price skip them as width * depth * 8, which wraps past 2^63. The
            // product of two u32 cannot wrap a ulong, and the check divides. The price
            // makes it so that such a payload is refused for this rather than for its
            // budget, and the read makes it too, since a price that throws does not stop
            // the read.
            reader.RequireHolds((ulong)width * depth, sizeof(double), "counters");
        }

        /// <summary>A matrix of <paramref name="depth"/> rows of <paramref name="width"/> counters, and the sketch.</summary>
        private static long MatrixCost(uint width, uint depth) => ReadBudget.Sum(
            ReadBudget.ArrayOf(depth, sizeof(long)),
            ReadBudget.Times(depth, ReadBudget.ArrayOf(width, sizeof(double))),
            ReadBudget.SmallObject);

        /// <summary>
        /// Reads the body of a sketch from a payload already positioned at one, laid out as
        /// format version <paramref name="version"/> lays it out, so that
        /// <see cref="DpswSketch"/> can read the sketches it holds without each one
        /// carrying its own frame.
        /// </summary>
        /// <remarks>
        /// The placement comes from the body, never from the width: a body before version 9
        /// was placed with 32-bit values whatever its width, and one at version 9 says how
        /// it was placed, so each of a window's sketches reads as it was written.
        /// </remarks>
        internal static PrivateCountMinSketch ReadBody(
            ref PayloadReader reader, Func<ReadOnlySpan<byte>, ulong> hash, ushort version)
        {
            var width = reader.ReadUInt32();
            var depth = reader.ReadUInt32();
            var placement = BitPlacement.Read(ref reader, version, PersistenceFormat.ColumnPlacementVersion);
            var rho = reader.ReadDouble();
            var count = reader.ReadUInt64();
            CheckShape(ref reader, width, depth, rho);

            // Each row is allocated before it is read.
            reader.Budget.Charge(MatrixCost(width, depth));
            var matrix = new double[depth][];
            var anyNoise = false;

            for (var i = 0; i < depth; i++)
            {
                matrix[i] = new double[width];
                for (var j = 0; j < width; j++)
                {
                    var counter = reader.ReadDouble();
                    if (double.IsNaN(counter) || double.IsInfinity(counter))
                    {
                        throw new InvalidDataException(
                            $"Counter [{i}][{j}] is {counter}. A counter is a noise " +
                            "draw plus a count, and neither can be infinite or " +
                            "not a number.");
                    }

                    matrix[i][j] = counter;
                    anyNoise |= counter != Math.Floor(counter);
                }
            }

            // A counter is a draw from a continuous distribution plus a whole number of
            // hits, so it is non-integral with probability one, and stays so for the
            // sketch's whole life. A payload whose every counter is an exact integer is
            // therefore not one this mechanism produced: it is a plain Count-Min Sketch
            // wearing this one's name, and it protects nobody. Reading it would hand
            // back a structure whose entire contract is silently false, so it is
            // refused here rather than believed.
            if (!anyNoise)
            {
                throw new InvalidDataException(
                    $"Every one of the {(long)depth * width} counters is an exact " +
                    "integer, which is what a sketch written without noise looks like. " +
                    "A private sketch's counters are a continuous draw plus a count, " +
                    "so at least one is non-integral with probability one. Refusing " +
                    "rather than handing back a sketch that guarantees nothing.");
            }

            return new PrivateCountMinSketch(width, depth, rho, count, matrix, hash, placement);
        }

        /// <summary>
        /// Used only by the read path, which supplies counters that already carry their
        /// noise. The public constructor draws that noise, which is exactly what
        /// restoring a sketch must not do.
        /// </summary>
        private PrivateCountMinSketch(
            uint width, uint depth, double rho, ulong count,
            double[][] matrix, Func<ReadOnlySpan<byte>, ulong> hash, Placement placement)
        {
            this.width = width;
            this.depth = depth;
            this.rho = rho;
            this.count = count;
            this.matrix = matrix;
            this.Hash = hash;
            this.placement = placement;
        }

        /// <summary>
        /// Writes the body of this sketch into a payload already being written, so that
        /// <see cref="DpswSketch"/> can hold sketches without each carrying its own
        /// frame. The seed is no more written here than it is by <see cref="WriteTo"/>.
        /// The layout is format version 9's, with the placement byte, so whatever holds it
        /// is written at version 9 too.
        /// </summary>
        internal void WriteBody(PayloadWriter payload)
        {
            payload.WriteUInt32(this.width);
            payload.WriteUInt32(this.depth);
            BitPlacement.Write(payload, this.placement);
            payload.WriteDouble(this.rho);
            payload.WriteUInt64(this.count);

            for (var i = 0; i < this.depth; i++)
            {
                for (var j = 0; j < this.width; j++)
                {
                    payload.WriteDouble(this.matrix[i][j]);
                }
            }
        }

        /// <summary>
        /// One draw from the standard normal distribution.
        /// </summary>
        /// <remarks>
        /// The polar form of the Box-Muller transform. It is exact rather than
        /// approximate -- no sum of uniforms standing in for a bell -- which matters
        /// because the privacy argument is about this distribution and not about
        /// something shaped roughly like it.
        /// </remarks>
        private static double StandardNormal(ref SeededRandom random)
        {
            double x, y, squared;

            do
            {
                x = (2 * random.NextDouble()) - 1;
                y = (2 * random.NextDouble()) - 1;
                squared = (x * x) + (y * y);
            }
            while (squared >= 1 || squared == 0);

            return x * Math.Sqrt(-2 * Math.Log(squared) / squared);
        }
    }
}
