using System;

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
    /// </remarks>
    public class PrivateCountMinSketch
    {
        private readonly double[][] matrix;
        private readonly uint width;
        private readonly uint depth;
        private readonly double rho;
        private ulong count;

        private Func<ReadOnlySpan<byte>, ulong> Hash { get; set; }

        /// <summary>
        /// Builds a sketch of the given shape, seeded with noise for the given privacy
        /// budget.
        /// </summary>
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
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();

            // The l2-sensitivity of a Count-Min Sketch is the square root of twice its
            // depth, because two neighbouring streams differ in one record, and that
            // record moves one counter per row in each of them. The Gaussian mechanism
            // at that sensitivity wants a variance of sensitivity squared over twice
            // the budget, which is depth over the budget.
            var deviation = Math.Sqrt(depth / rho);

            var random = seed.HasValue
                ? new SeededRandom(seed.Value)
                : SeededRandom.Unpredictable();

            this.matrix = new double[depth][];
            for (var i = 0; i < depth; i++)
            {
                this.matrix[i] = new double[width];
                for (var j = 0; j < width; j++)
                {
                    this.matrix[i][j] = deviation * StandardNormal(ref random);
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
            var kernel = Utils.HashKernel(data, this.Hash);

            for (uint i = 0; i < this.depth; i++)
            {
                this.matrix[i][ColumnOf(kernel, i)]++;
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
            var kernel = Utils.HashKernel(data, this.Hash);
            var smallest = double.MaxValue;

            for (uint i = 0; i < this.depth; i++)
            {
                smallest = Math.Min(smallest, this.matrix[i][ColumnOf(kernel, i)]);
            }

            return smallest;
        }

        private uint ColumnOf(in HashKernelReturnValue kernel, uint row) =>
            (uint)((kernel.LowerBaseHash + (kernel.UpperBaseHash * row)) % this.width);

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
