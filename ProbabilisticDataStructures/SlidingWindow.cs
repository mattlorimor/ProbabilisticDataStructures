using System;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Keeps a structure over a moving window of recent time, by holding one of them per
    /// time bucket and combining the live ones on demand.
    /// </summary>
    /// <typeparam name="T">The structure being windowed.</typeparam>
    /// <remarks>
    /// Everything else here answers about the whole stream since it was created. This
    /// answers "in the last hour", which is how most of these questions are actually
    /// asked: distinct users today, top paths this minute, the p99 of the last five.
    /// <para>
    /// It is a wrapper rather than a family of windowed structures because 5.1.0 and
    /// 6.0.0 made most of the library mergeable, and a ring of sub-structures merged on
    /// query gets the same answer for one implementation instead of a paper's worth of
    /// work per structure. What it costs is memory -- one structure per bucket -- and
    /// precision at the edge: the window is only as sharp as a bucket is wide.
    /// </para>
    /// <para>
    /// <b>Only use this over a structure whose merge is exact.</b> Merging is what makes
    /// the window's answer mean anything, so a structure that merges approximately gives
    /// a window that is wrong in a way no amount of bucketing fixes.
    /// <see cref="TopK"/> is the one in this library that does, and is refused by name.
    /// </para>
    /// </remarks>
    public class SlidingWindow<T>
        where T : class
    {
        private readonly TimeSpan bucketWidth;
        private readonly int buckets;
        private readonly Func<T> create;
        private readonly Func<T, T, T> merge;
        private readonly Func<DateTimeOffset> clock;

        private readonly T[] ring;
        private readonly long[] ages;

        /// <summary>
        /// Creates a window of the given length, divided into buckets.
        /// </summary>
        /// <param name="window">How far back the window reaches.</param>
        /// <param name="buckets">
        /// How many buckets to divide it into. The window's edge is only as sharp as one
        /// bucket, and memory is one structure per bucket, so this is the trade and it is
        /// the caller's to make.
        /// </param>
        /// <param name="create">Makes a fresh, empty structure for a new bucket.</param>
        /// <param name="merge">
        /// Combines two structures. Must be exact -- see the remarks on this type.
        /// </param>
        /// <param name="clock">
        /// Where the current time comes from, or null for the system clock. Supplying one
        /// is what makes a window's behaviour testable without waiting for it.
        /// </param>
        public SlidingWindow(
            TimeSpan window,
            int buckets,
            Func<T> create,
            Func<T, T, T> merge,
            Func<DateTimeOffset>? clock = null)
        {
            ArgumentNullException.ThrowIfNull(create);
            ArgumentNullException.ThrowIfNull(merge);

            if (window <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(window), window, "A window covers a positive span of time.");
            }

            if (buckets < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(buckets), buckets, "A window needs at least one bucket.");
            }

            // Refused by name rather than left to be discovered. TopK.Merge is documented
            // as approximate -- two sketches can disagree about what was frequent, and an
            // element in the top-k of the union can be missing from both inputs' top-k --
            // so a window built on it would drop elements as buckets rolled, and nothing
            // about the result would look wrong.
            if (typeof(T) == typeof(TopK))
            {
                throw new ArgumentException(
                    "A sliding window cannot be built over TopK. Its merge is " +
                    "approximate: an element genuinely in the top-k of the whole window " +
                    "can be absent from every bucket's own top-k, so combining the " +
                    "buckets would lose it silently. Window a CountMinSketch instead and " +
                    "take the heavy hitters from that.",
                    nameof(T));
            }

            this.bucketWidth = TimeSpan.FromTicks(Math.Max(1, window.Ticks / buckets));
            this.buckets = buckets;
            this.create = create;
            this.merge = merge;
            this.clock = clock ?? (() => DateTimeOffset.UtcNow);

            this.ring = new T[buckets];
            this.ages = new long[buckets];

            var now = this.CurrentBucket();
            for (var i = 0; i < buckets; i++)
            {
                this.ring[i] = create();

                // Older than any live bucket, so an untouched slot is expired rather
                // than counted as part of the first window.
                this.ages[i] = now - buckets;
            }
        }

        /// <summary>
        /// The structure covering the current moment, to add to.
        /// </summary>
        /// <remarks>
        /// Reading this rolls the window: whichever bucket now covers the present is
        /// emptied first if it was holding something from a previous lap.
        /// </remarks>
        public T Current
        {
            get
            {
                var bucket = this.CurrentBucket();
                var slot = (int)(((bucket % this.buckets) + this.buckets) % this.buckets);

                if (this.ages[slot] != bucket)
                {
                    this.ring[slot] = this.create();
                    this.ages[slot] = bucket;
                }

                return this.ring[slot];
            }
        }

        /// <summary>
        /// A structure combining every bucket still inside the window.
        /// </summary>
        /// <returns>
        /// A new structure. Buckets that have fallen out of the window are not included,
        /// and neither is anything written to them before they did.
        /// </returns>
        public T Merged()
        {
            var newest = this.CurrentBucket();
            var oldest = newest - this.buckets + 1;

            var combined = this.create();

            for (var slot = 0; slot < this.buckets; slot++)
            {
                if (this.ages[slot] >= oldest && this.ages[slot] <= newest)
                {
                    combined = this.merge(combined, this.ring[slot]);
                }
            }

            return combined;
        }

        /// <summary>
        /// Which bucket the present falls in, counted from the epoch so that the number
        /// increases forever and expiry is a comparison rather than bookkeeping.
        /// </summary>
        private long CurrentBucket()
        {
            return this.clock().UtcTicks / this.bucketWidth.Ticks;
        }

        /// <summary>How wide each bucket is, which is the window's precision.</summary>
        public TimeSpan BucketWidth => this.bucketWidth;

        /// <summary>How many buckets the window is divided into.</summary>
        public int Buckets => this.buckets;
    }
}
