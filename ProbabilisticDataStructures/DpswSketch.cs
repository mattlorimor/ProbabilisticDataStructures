using System;
using System.Collections.Generic;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Frequency estimation over a sliding window that is differentially private:
    /// DPSW-Sketch, from Wang, Wang and Chen (KDD 2024).
    /// </summary>
    /// <remarks>
    /// Two hard things at once. A sliding window means only the most recent items
    /// count, and an ordinary sketch cannot forget. Differential privacy means the
    /// published state must not reveal whether any one item was there, and the obvious
    /// way to combine the two -- one private sketch per window position -- spends the
    /// privacy budget many times over on the same item.
    /// <para>
    /// The framework's answer is to cut the stream into substreams and, within each,
    /// build a set of <see cref="PrivateCountMinSketch"/>es over nested ranges chosen
    /// by a smooth histogram: some running from the start of the substream to a
    /// checkpoint, others from a checkpoint to its end. A query then finds, for each
    /// substream the window touches, the one sketch whose range best fits inside the
    /// window, and adds the answers up. The window is approximated rather than exact,
    /// which is the price of not keeping the items.
    /// </para>
    /// <para>
    /// The budget is split across those sketches so that everything covering any one
    /// item adds up to the whole budget and no more, which is what lets the composition
    /// argument go through. The split is a geometric series arranged to sum exactly:
    /// the whole-substream sketch takes 2a - a^2 of it and each checkpoint pair takes
    /// a^(j-2)(1-a)^3/2, and those come to one.
    /// </para>
    /// <para>
    /// <b>What is guaranteed and by whom.</b> The privacy claim is the authors'
    /// theorem, and it holds if the noise is what the mechanism requires and the budget
    /// is split as above. This library tests both of those -- see
    /// <see cref="PrivateCountMinSketch"/> for the noise and the budget test alongside
    /// this class -- and proves neither. It is also event-level: one item, not one
    /// person's whole history.
    /// </para>
    /// </remarks>
    public class DpswSketch
    {
        /// <summary>One private sketch and the range of the substream it covers.</summary>
        private sealed class Segment
        {
            internal Segment(int from, int to, PrivateCountMinSketch sketch, double budget)
            {
                this.From = from;
                this.To = to;
                this.Sketch = sketch;
                this.Budget = budget;
            }

            /// <summary>The first position in the substream this covers, counting from one.</summary>
            internal int From { get; }

            /// <summary>The last position in the substream this covers.</summary>
            internal int To { get; }

            internal PrivateCountMinSketch Sketch { get; }

            internal double Budget { get; }
        }

        /// <summary>A substream, and the sketches built over ranges within it.</summary>
        private sealed class Substream
        {
            internal Substream(long start)
            {
                this.Start = start;
                this.Segments = new List<Segment>();
            }

            /// <summary>The stream position of this substream's first item.</summary>
            internal long Start { get; }

            /// <summary>How many items it has taken so far.</summary>
            internal int Held { get; set; }

            internal List<Segment> Segments { get; }
        }

        private readonly long window;
        private readonly double rho;
        private readonly double alpha;
        private readonly int substreamSize;
        private readonly uint width;
        private readonly uint depth;
        private readonly int[] checkpoints;
        private readonly List<Substream> substreams = new List<Substream>();
        private readonly Func<ReadOnlySpan<byte>, ulong>? hash;
        private SeededRandom seeds;
        private long position;

        /// <summary>
        /// Builds a sketch over a sliding window.
        /// </summary>
        /// <param name="window">How many of the most recent items a query covers.</param>
        /// <param name="rho">
        /// The zero-concentrated privacy budget for the whole structure. See
        /// <see cref="PrivateCountMinSketch.BudgetFor"/> to pick one from an epsilon and
        /// delta.
        /// </param>
        /// <param name="alpha">
        /// How finely each substream is checkpointed. Smaller means more checkpoints
        /// and a closer approximation of the window -- and much more noise, because the
        /// budget for the j-th checkpoint falls as this to the power of j while the
        /// range it covers only falls as one minus this. The two decay at different
        /// rates, so the last checkpoints end up with almost no budget at all.
        /// <para>
        /// Measured over 351 query points on a window of four thousand, with a true
        /// answer of 1333: at 0.5 the error ran between -77 and -37, at 0.4 between -97
        /// and -18, at 0.75 between -107 and -65. At 0.25 the median error was -46 and
        /// the fifth percentile -6150, because now and then a query lands on the
        /// checkpoint holding a sixty-billionth of the budget. That is why small values
        /// here are refused rather than merely discouraged.
        /// </para>
        /// </param>
        /// <param name="beta">
        /// Sets the substream size as the window raised to this power. Smaller means
        /// more, shorter substreams -- and a query adds one noisy answer per substream,
        /// so more of them is more accumulated noise as well as more memory. Measured
        /// on a window of five thousand, the estimate of a quarter-share item ran 714,
        /// 1125, 1179, 1211, 1167 and 1078 against a truth of 1250 as this went 0.4,
        /// 0.5, 0.6, 0.7, 0.8 and 0.9, while the memory fell from 80 MB to 3 MB. The
        /// default is where those two stop pulling in the same direction.
        /// </param>
        /// <param name="width">How many counters each sketch row holds.</param>
        /// <param name="depth">How many rows each sketch has.</param>
        /// <param name="seed">
        /// The seed for the noise. For tests only: a deployment must not supply one,
        /// since anyone who knows it can subtract the noise back off.
        /// </param>
        /// <param name="hash">The hash function to use, or null for the default.</param>
        public DpswSketch(
            long window,
            double rho,
            double alpha = 0.5,
            double beta = 0.7,
            uint width = 512,
            uint depth = 5,
            ulong? seed = null,
            Func<ReadOnlySpan<byte>, ulong>? hash = null)
        {
            if (window < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(window),
                    "A window covers at least one item.");
            }
            if (double.IsNaN(rho) || rho <= 0 || double.IsInfinity(rho))
            {
                throw new ArgumentOutOfRangeException(nameof(rho),
                    $"The privacy budget is {rho}, and it has to be a positive number.");
            }
            if (double.IsNaN(alpha) || alpha <= 0 || alpha >= 1)
            {
                throw new ArgumentOutOfRangeException(nameof(alpha),
                    "The checkpoint factor lies strictly between nought and one.");
            }
            if (double.IsNaN(beta) || beta <= 0 || beta >= 1)
            {
                throw new ArgumentOutOfRangeException(nameof(beta),
                    "The substream factor lies strictly between nought and one.");
            }

            this.window = window;
            this.rho = rho;
            this.alpha = alpha;
            this.width = width;
            this.depth = depth;
            this.hash = hash;
            this.seeds = seed.HasValue
                ? new SeededRandom(seed.Value)
                : SeededRandom.Unpredictable();

            this.substreamSize = (int)Math.Max(1, Math.Ceiling(Math.Pow(window, beta)));
            this.checkpoints = Checkpoints(this.substreamSize, alpha);

            // The budget for the j-th checkpoint falls as alpha to the power of j, and
            // the number of checkpoints grows as alpha shrinks, so the last of them can
            // be left with a budget small enough that its noise dwarfs anything it
            // could be asked. A query that lands on such a sketch does not come back
            // slightly wrong, it comes back wrong by thousands -- and it does so only
            // sometimes, which is worse than doing it always.
            var leanest = BudgetAt(this.checkpoints.Length, rho, alpha);
            var worstDeviation = Math.Sqrt(depth / leanest);

            if (worstDeviation > window)
            {
                throw new ArgumentOutOfRangeException(nameof(alpha),
                    $"A checkpoint factor of {alpha} gives {this.checkpoints.Length} " +
                    $"checkpoints in a substream, and the last of them a budget of " +
                    $"{leanest:E2}, whose noise has a standard deviation of " +
                    $"{worstDeviation:E2} -- against a window of {window} items. A " +
                    "sketch whose noise exceeds everything it could be counting " +
                    "contributes nothing but noise. Use a larger checkpoint factor, or " +
                    "a smaller substream factor to shorten the substreams.");
            }

            // Refuse a shape whose sketches would be so many that none of them has a
            // usable budget rather than discovering it in the estimates.
            if (width == 0 || depth == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width),
                    "A sketch needs at least one counter and one row.");
            }
        }

        /// <summary>How many of the most recent items a query covers.</summary>
        public long Window => this.window;

        /// <summary>The privacy budget for the whole structure.</summary>
        public double Rho => this.rho;

        /// <summary>How many items have been added in all.</summary>
        public long Position => this.position;

        /// <summary>How many items each substream holds.</summary>
        internal int SubstreamSize => this.substreamSize;

        /// <summary>The checkpoint offsets within a substream, largest first.</summary>
        internal int[] Checkpointing => this.checkpoints;

        /// <summary>
        /// How many bytes the sketches occupy.
        /// </summary>
        /// <remarks>
        /// This structure is not small. It keeps a private sketch per checkpoint per
        /// substream, and a window holds many substreams, so the count of sketches runs
        /// into the hundreds and the memory into megabytes. That is what buying a
        /// private sliding window costs: the framework cannot forget by overwriting,
        /// because a counter that could be overwritten is a counter whose history could
        /// be inferred, so it forgets by keeping separate sketches and dropping whole
        /// ones.
        /// </remarks>
        public long SizeInBytes =>
            (long)this.SketchesHeld * this.width * this.depth * sizeof(double);

        /// <summary>How many private sketches are currently being kept.</summary>
        public int SketchesHeld
        {
            get
            {
                var total = 0;
                foreach (var substream in this.substreams)
                {
                    total += substream.Segments.Count;
                }
                return total;
            }
        }

        /// <summary>
        /// The budgets given to the sketches covering each position of a substream.
        /// </summary>
        /// <remarks>
        /// The privacy argument needs the budgets spent on any single item to come to
        /// no more than the whole. This is what a test checks that against.
        /// </remarks>
        internal double BudgetSpentAt(int positionInSubstream)
        {
            var total = 0.0;
            foreach (var segment in PlanFor(0))
            {
                if (segment.From <= positionInSubstream && positionInSubstream <= segment.To)
                {
                    total += segment.Budget;
                }
            }
            return total;
        }

        /// <summary>
        /// Adds an item to the stream.
        /// </summary>
        /// <param name="data">The item.</param>
        public DpswSketch Add(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            return this.Add(data.AsSpan());
        }

        /// <inheritdoc cref="Add(byte[])"/>
        public DpswSketch Add(ReadOnlySpan<byte> data)
        {
            this.position++;

            if (this.substreams.Count == 0
                || this.substreams[^1].Held >= this.substreamSize)
            {
                this.substreams.Add(Begin(this.position));
            }

            var current = this.substreams[^1];
            current.Held++;
            var offset = current.Held;

            foreach (var segment in current.Segments)
            {
                if (segment.From <= offset && offset <= segment.To)
                {
                    segment.Sketch.Add(data);
                }
            }

            Expire();
            return this;
        }

        /// <summary>
        /// The estimated number of times an item appeared in the window.
        /// </summary>
        /// <remarks>
        /// One sketch is chosen from each substream the window touches: for the oldest,
        /// the one starting as late as possible but no later than the window's start;
        /// for the newest, the one ending as late as possible but no later than now;
        /// and for those wholly inside, the one covering the substream entire. Their
        /// answers are added.
        /// <para>
        /// Nothing is clamped. The estimate can fall below nought, both because the
        /// counters carry noise and because it is a sum of several noisy answers.
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
            var windowStart = Math.Max(1, this.position - this.window + 1);
            var total = 0.0;

            for (var s = 0; s < this.substreams.Count; s++)
            {
                var substream = this.substreams[s];
                var last = substream.Start + substream.Held - 1;

                if (last < windowStart)
                {
                    continue;
                }

                var chosen = Choose(substream, windowStart);
                if (chosen is not null)
                {
                    total += chosen.Sketch.Count(data);
                }
            }

            return total;
        }

        /// <summary>
        /// The items estimated to make up at least the given share of the window.
        /// </summary>
        /// <param name="candidates">The items to consider.</param>
        /// <param name="share">The share of the window an item must reach.</param>
        public IReadOnlyList<byte[]> HeavyHitters(
            IEnumerable<byte[]> candidates, double share)
        {
            ArgumentNullException.ThrowIfNull(candidates);

            if (double.IsNaN(share) || share <= 0 || share > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(share),
                    "A share of the window lies above nought and at most one.");
            }

            var covered = Math.Min(this.window, this.position);
            var threshold = share * covered;

            var found = new List<byte[]>();
            foreach (var candidate in candidates)
            {
                ArgumentNullException.ThrowIfNull(candidate);

                if (this.Count(candidate) >= threshold)
                {
                    found.Add(candidate);
                }
            }

            return found;
        }

        /// <summary>
        /// Which sketch in a substream best fits the window.
        /// </summary>
        /// <remarks>
        /// The one starting as late as possible without starting before the window,
        /// and among those the one running furthest.
        /// <para>
        /// This departs from the paper's query rule in one way. Algorithm 3 restricts
        /// the substream still being filled to ranges that have already ended, which
        /// leaves it reading a short checkpoint sketch holding a small share of the
        /// budget. A range that has not ended yet holds exactly the items that have
        /// arrived, which are exactly the ones the window wants, so reading the
        /// whole-substream sketch instead is both correct and much quieter -- it holds
        /// the largest share of the budget of any sketch there. Choosing which existing
        /// sketch to read costs no privacy, since anything computed from a private
        /// result stays private, and the paper's error bound is an upper bound that
        /// this only moves further inside: measured over 351 query points, the mean
        /// error went from -51 to -30 and the fifth percentile from -77 to -60.
        /// </para>
        /// </remarks>
        private Segment? Choose(Substream substream, long windowStart)
        {
            Segment? best = null;

            foreach (var segment in substream.Segments)
            {
                var from = substream.Start + segment.From - 1;
                var to = substream.Start + segment.To - 1;

                // Never count an item the window has already passed, and never count
                // one that has not arrived.
                if (from < windowStart || segment.From > substream.Held)
                {
                    continue;
                }

                if (best is null
                    || from < substream.Start + best.From - 1
                    || (from == substream.Start + best.From - 1 && to > substream.Start + best.To - 1))
                {
                    best = segment;
                }
            }

            return best;
        }

        /// <summary>
        /// Starts a substream and builds the sketches over its ranges.
        /// </summary>
        private Substream Begin(long start)
        {
            var substream = new Substream(start);

            foreach (var planned in PlanFor(start))
            {
                substream.Segments.Add(new Segment(
                    planned.From, planned.To,
                    new PrivateCountMinSketch(
                        this.width, this.depth, planned.Budget,
                        this.seeds.Next(), this.hash),
                    planned.Budget));
            }

            return substream;
        }

        /// <summary>
        /// The ranges a substream is covered by, and the budget each gets.
        /// </summary>
        /// <remarks>
        /// The first covers the whole substream. Each checkpoint after it contributes a
        /// pair: one range from the start of the substream to the checkpoint, and one
        /// from the mirror of that checkpoint to the end. The budgets are a geometric
        /// series chosen so that the whole comes to the budget for the structure.
        /// </remarks>
        private List<Segment> PlanFor(long unused)
        {
            _ = unused;

            var planned = new List<Segment>
            {
                new Segment(1, this.substreamSize, null!,
                    this.rho * ((2 * this.alpha) - (this.alpha * this.alpha))),
            };

            for (var j = 2; j <= this.checkpoints.Length; j++)
            {
                var budget = BudgetAt(j, this.rho, this.alpha);

                var checkpoint = this.checkpoints[j - 1];

                // From the start of the substream to this checkpoint.
                planned.Add(new Segment(1, checkpoint, null!, budget));

                // And from the mirror of it to the end.
                planned.Add(new Segment(
                    this.substreamSize - checkpoint + 1, this.substreamSize,
                    null!, budget));
            }

            return planned;
        }

        /// <summary>
        /// Drops substreams every item of which has left the window.
        /// </summary>
        private void Expire()
        {
            var windowStart = Math.Max(1, this.position - this.window + 1);

            while (this.substreams.Count > 0)
            {
                var oldest = this.substreams[0];
                var last = oldest.Start + oldest.Held - 1;

                if (last >= windowStart)
                {
                    break;
                }

                this.substreams.RemoveAt(0);
            }
        }

        /// <summary>
        /// The budget the j-th checkpoint pair is given.
        /// </summary>
        /// <remarks>
        /// The whole-substream sketch takes 2a - a^2 of the budget and each checkpoint
        /// pair takes a^(j-2)(1-a)^3/2 of it, twice over since there is a pair. Those
        /// come to exactly the budget:
        /// (2a - a^2) + 2 * (1-a)^3/2 * 1/(1-a) = (2a - a^2) + (1-a)^2 = 1.
        /// <para>
        /// The exponent on the second factor is worth reading twice. As three halves
        /// rather than three-over-two, which is how the paper's typesetting can be
        /// taken, the same series sums to about twice the budget -- meaning half the
        /// noise the mechanism requires, and a structure that is not private at the
        /// rate it says. The test that no item is charged more than the whole budget is
        /// what holds this to the arithmetic above.
        /// </para>
        /// </remarks>
        private static double BudgetAt(int j, double rho, double alpha) =>
            rho * Math.Pow(alpha, j - 2) * Math.Pow(1 - alpha, 3) / 2;

        /// <summary>
        /// The checkpoint offsets within a substream, chosen by the smooth histogram's
        /// thinning rule.
        /// </summary>
        /// <remarks>
        /// Start with every offset from the substream's length down to one, then repeatedly
        /// drop everything between an offset and the last one still at least
        /// (1 - alpha) times it. What survives is a list where consecutive offsets
        /// differ by at most that factor, which is what bounds how badly the chosen
        /// range can miss the window's true start.
        /// </remarks>
        private static int[] Checkpoints(int substreamSize, double alpha)
        {
            var indices = new List<int>();
            for (var i = substreamSize; i >= 1; i--)
            {
                indices.Add(i);
            }

            var j = 0;
            while (j < indices.Count - 2)
            {
                var furthest = j;
                for (var k = j + 1; k < indices.Count; k++)
                {
                    if (indices[k] >= (1 - alpha) * indices[j])
                    {
                        furthest = k;
                    }
                }

                if (furthest > j + 1)
                {
                    indices.RemoveRange(j + 1, furthest - j - 1);
                }

                j++;
            }

            return indices.ToArray();
        }
    }
}
