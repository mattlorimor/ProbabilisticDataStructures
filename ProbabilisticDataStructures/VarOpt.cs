using System;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// VarOpt keeps a k-item weighted sample of a stream from which the total weight
    /// of any subset can be estimated after the fact, without bias and with optimal
    /// variance, as described by Cohen, Duffield, Kaplan, Lund and Thorup in Stream
    /// Sampling for Variance-Optimal Estimation of Subset Sums (SODA 2009).
    /// </summary>
    /// <remarks>
    /// Every other structure in this library fixes its question at ingest: a filter
    /// answers membership, a sketch answers the frequencies or quantiles it was built
    /// to count. VarOpt fixes nothing. It keeps k of the items themselves, chosen so
    /// that summing the sample's adjusted weights over any predicate -- one written
    /// years after the stream was consumed -- estimates that predicate's true weight.
    /// The estimate is unbiased, the variance is the minimum any k-item sample can
    /// achieve, and the estimate of the whole stream's weight is not an estimate at
    /// all: no weight is lost when an item is dropped, because the survivors inherit
    /// it, so the adjusted weights account for all the weight that went in. That
    /// holds up to the rounding of doubles: <see cref="TotalWeight"/> is exact only
    /// while the weights are integers and every partial sum stays below 2^53, and
    /// says what rounds. The price is that k items answer every question, so any
    /// question known in advance is served far better per byte by the structure
    /// dedicated to it.
    /// <para>
    /// The sample lives in two regions. Items heavier than the current threshold are
    /// held with their exact weights; everything else that survived holds a share of
    /// the threshold instead, because below the threshold survival is luck and the
    /// luck is priced identically. A new arrival either buys its place outright or
    /// contests the lightweights, and exactly one contestant is dropped, chosen so
    /// the drop probabilities sum to one -- the sample size never wavers from
    /// min(k, n). This is the amortized-constant-time implementation the paper gives
    /// as Algorithm 1, the same one Apache DataSketches ships.
    /// </para>
    /// <para>
    /// Samples merge by feeding one sample's items, at their adjusted weights,
    /// through the other's ordinary insertion path -- the paper's own recurrence
    /// for distributed sampling -- unless a sampled input fits outright, when the
    /// result is that input (Lemma 4). The recurrence holds when every input's k
    /// is at least the k of the result, so a sample may be merged into one that
    /// keeps the same number of items or fewer, and a sample that has not yet
    /// begun sampling is plain data that merges into anything. What it forbids is a
    /// result that keeps more items than an input that has sampled: the result
    /// would hold items already chosen by chance as though they were exact.
    /// DataSketches instead lets k float and resolves it afterwards, which is
    /// what its marked items, its gadget -- not itself a VarOpt sample -- and its
    /// k-reduction pass exist to do. Refusing the invalid direction needs none of
    /// that.
    /// </para>
    /// </remarks>
    public class VarOpt : IBinaryPersistable<VarOpt>
    {
        /// <summary>
        /// Items heavier than the threshold, held with exact weights. Once sampling
        /// has begun, a binary min-heap on the weights, so the lightest heavy item
        /// is always at index zero, ready to be contested. Before then, every item
        /// seen, in the order it arrived: that order is what the transition to
        /// sampling heaps from, so it is part of the state.
        /// </summary>
        internal byte[][] HeavyItems { get; set; }
        /// <summary>
        /// The heavy items' exact weights, heap-ordered alongside them.
        /// </summary>
        internal double[] HeavyWeights { get; set; }
        /// <summary>
        /// How many items are heavy.
        /// </summary>
        internal int HeavyCount { get; set; }
        /// <summary>
        /// Items at or below the threshold, in no particular order. Each one's
        /// adjusted weight is the shared threshold, held implicitly.
        /// </summary>
        internal byte[][] LightItems { get; set; }
        /// <summary>
        /// How many items are light. Zero means the sample is still exact.
        /// </summary>
        internal int LightCount { get; set; }
        /// <summary>
        /// The light region's total weight, which no drop loses any of: when a
        /// contestant is eliminated, the survivors inherit its weight rather than the
        /// stream losing it. The threshold is this divided by the count, and is never
        /// stored, so the quotient's rounding never feeds back into the total. The
        /// total is a sum of doubles all the same, exact only while integer partial
        /// sums stay below 2^53; see <see cref="TotalWeight"/>.
        /// </summary>
        internal double LightWeight { get; set; }
        /// <summary>
        /// Number of items added.
        /// </summary>
        internal ulong N { get; set; }
        /// <summary>
        /// The sample size k.
        /// </summary>
        private uint k;
        /// <summary>
        /// The random source behind every drop decision. Persisted, so a structure
        /// read back resumes its draw sequence rather than replaying it.
        /// </summary>
        private SeededRandom random;
        /// <summary>
        /// Scratch space for the contestants of a single insertion, kept between
        /// calls so insertion allocates nothing.
        /// </summary>
        private byte[][] candidateItems;
        private double[] candidateWeights;
        private int candidateCount;

        /// <summary>
        /// The number of items the sample keeps.
        /// </summary>
        public uint K => this.k;

        /// <summary>
        /// The most items a sample can keep.
        /// </summary>
        /// <remarks>
        /// Each region -- exact items, threshold items, and the candidates contested
        /// between them -- is an array of k + 1 slots, allocated whole when the sample
        /// is made, and no array is longer than <see cref="Array.MaxLength"/>. At
        /// uint.MaxValue the k + 1 wrapped to nothing and the constructor built a sample
        /// with no room in it at all.
        /// <para>
        /// The constructor and the reader both refuse past this, so a sample one builds
        /// the other reads. The reader used to stop at a million. It allocates the
        /// regions whole just as the constructor does, because the sample's arithmetic
        /// assumes their size, so a payload can ask for a sample as large as the
        /// constructor would build and no larger.
        /// </para>
        /// </remarks>
        internal static uint MaxK => (uint)Array.MaxLength - 1;

        /// <summary>
        /// The number of items currently held: min(k, items added).
        /// </summary>
        public uint SampleCount => (uint)(this.HeavyCount + this.LightCount);

        /// <summary>
        /// The total weight of everything ever added. This is bookkeeping, not
        /// estimation: dropping an item hands its weight to the survivors, so no drop
        /// loses any of it.
        /// </summary>
        /// <remarks>
        /// The total is a sum of doubles, so it is exact only while the weights are
        /// integers and every partial sum stays below 2^53. Past that the additions
        /// round, and a weight smaller than the spacing of doubles there can be lost
        /// entirely: with unit weights the total stops growing at 2^53, and the
        /// threshold and every subset estimate stop with it. A weight with a fraction
        /// can round as soon as it is added. So can a merge with a sample that has
        /// begun sampling, whose threshold items come in at the threshold, a quotient.
        /// </remarks>
        public double TotalWeight
        {
            get
            {
                var total = this.LightWeight;
                for (var i = 0; i < this.HeavyCount; i++)
                {
                    total += this.HeavyWeights[i];
                }
                return total;
            }
        }

        /// <summary>
        /// The current threshold: the shared adjusted weight of every light item,
        /// zero while the sample is still exact.
        /// </summary>
        internal double Tau =>
            this.LightCount == 0 ? 0.0 : this.LightWeight / this.LightCount;

        /// <summary>
        /// Creates a new VarOpt sample keeping k items.
        /// </summary>
        /// <param name="k">
        /// Number of items to keep. The variance of a subset estimate falls with k;
        /// the paper's bound on any subset's relative error falls off as 1/sqrt(k).
        /// </param>
        /// <param name="seed">
        /// Seed for the drop decisions, or null to seed unpredictably. Two samples
        /// with the same seed fed the same stream make the same decisions.
        /// </param>
        public VarOpt(uint k, ulong? seed = null)
        {
            Guard.ValidItemCount(k, nameof(k));
            if (k > MaxK)
            {
                throw new ArgumentOutOfRangeException(nameof(k), k,
                    $"A sample keeps at most {MaxK} items. Each of its regions is an " +
                    "array with one slot more than it keeps, and an array holds at most " +
                    $"{Array.MaxLength}.");
            }

            this.k = k;
            this.random = seed is null
                ? SeededRandom.Unpredictable()
                : new SeededRandom(seed.Value);
            this.HeavyItems = new byte[k + 1][];
            this.HeavyWeights = new double[k + 1];
            this.LightItems = new byte[k + 1][];
            this.candidateItems = new byte[k + 1][];
            this.candidateWeights = new double[k + 1];
        }

        /// <summary>
        /// Adds the data with weight one. Returns the VarOpt to allow for chaining.
        /// </summary>
        /// <param name="data">The data to add.</param>
        public VarOpt Add(byte[] data)
        {
            return Add(data, 1.0);
        }

        /// <summary>
        /// Adds the data with weight one. Returns the VarOpt to allow for chaining.
        /// The structure keeps items it samples, so the span's contents are copied.
        /// </summary>
        /// <param name="data">The data to add.</param>
        public VarOpt Add(ReadOnlySpan<byte> data)
        {
            return Add(data, 1.0);
        }

        /// <summary>
        /// Adds the data with the given weight. Returns the VarOpt to allow for
        /// chaining.
        /// </summary>
        /// <param name="data">The data to add.</param>
        /// <param name="weight">
        /// The item's weight, which must be positive and finite.
        /// </param>
        public VarOpt Add(byte[] data, double weight)
        {
            ArgumentNullException.ThrowIfNull(data);
            return Add(data.AsSpan(), weight);
        }

        /// <summary>
        /// Adds the data with the given weight. Returns the VarOpt to allow for
        /// chaining. The structure keeps items it samples, so the span's contents
        /// are copied.
        /// </summary>
        /// <param name="data">The data to add.</param>
        /// <param name="weight">
        /// The item's weight, which must be positive and finite.
        /// </param>
        public VarOpt Add(ReadOnlySpan<byte> data, double weight)
        {
            Guard.ValidWeight(weight, nameof(weight));

            this.N++;
            Insert(data.ToArray(), weight);
            return this;
        }

        /// <summary>
        /// The sampled items with their adjusted weights. Summing the weights of the
        /// items matching any predicate is an unbiased estimate of that subset's
        /// true weight; summing all of them gives <see cref="TotalWeight"/>, to within
        /// rounding, since each light item carries the threshold, a quotient.
        /// </summary>
        public WeightedElement[] Samples()
        {
            var samples = new WeightedElement[this.HeavyCount + this.LightCount];
            for (var i = 0; i < this.HeavyCount; i++)
            {
                samples[i] = new WeightedElement
                {
                    Data = this.HeavyItems[i],
                    Weight = this.HeavyWeights[i],
                };
            }
            var tau = this.Tau;
            for (var i = 0; i < this.LightCount; i++)
            {
                samples[this.HeavyCount + i] = new WeightedElement
                {
                    Data = this.LightItems[i],
                    Weight = tau,
                };
            }
            return samples;
        }

        /// <summary>
        /// The estimated total weight of the items matching the predicate: the sum of
        /// adjusted weights over the sampled items it selects. Unbiased for any
        /// predicate, including one conceived long after the stream was consumed.
        /// </summary>
        /// <param name="matches">Which items belong to the subset.</param>
        public double EstimateSubset(Predicate<ReadOnlyMemory<byte>> matches)
        {
            ArgumentNullException.ThrowIfNull(matches);

            var estimate = 0.0;
            for (var i = 0; i < this.HeavyCount; i++)
            {
                if (matches(this.HeavyItems[i]))
                {
                    estimate += this.HeavyWeights[i];
                }
            }
            var tau = this.Tau;
            for (var i = 0; i < this.LightCount; i++)
            {
                if (matches(this.LightItems[i]))
                {
                    estimate += tau;
                }
            }
            return estimate;
        }

        /// <summary>
        /// Merges another VarOpt sample into this one, leaving a valid VarOpt sample
        /// of both streams together. The other sample is unchanged.
        /// </summary>
        /// <remarks>
        /// This is the paper's recurrence for distributed sampling: each of the
        /// other sample's items is fed through the ordinary insertion path at its
        /// adjusted weight, exactly as if the adjusted weights were original ones. The
        /// one exception is the paper's Lemma 4 case, described below, where everything
        /// the other sample holds fits: the result is then that sample itself, and
        /// nothing goes through insertion. The recurrence's condition is that every
        /// input's k is at least the k of the result, so the other sample may keep as
        /// many items as this one or more. A sample that has not begun sampling holds
        /// nothing but original weights and so imposes no condition at all, whatever
        /// its k -- the paper's Lemma 5 makes that case trivial by giving such an input
        /// a threshold of zero.
        /// <para>
        /// The refused direction is a sample that has begun sampling being merged
        /// into one that keeps more items. Its items were chosen by chance and
        /// carry a threshold's worth of weight rather than their own; a result
        /// with room to hold them all would report them as exact, which is a claim
        /// no one measured.
        /// </para>
        /// <para>
        /// The same reasoning covers the one permitted merge whose inputs fit in
        /// the result outright: a sample that has begun sampling, merged into an
        /// empty one keeping the same number of items. The result is the other
        /// sample, threshold and all, which is the paper's Lemma 4 -- when the
        /// inner samples together hold at most k items, there is a single input,
        /// it keeps exactly k, and the resulting sample is identical to it.
        /// </para>
        /// </remarks>
        /// <param name="other">The sample to merge into this one.</param>
        /// <exception cref="ArgumentException">
        /// The other sample has begun sampling and keeps fewer items than this one.
        /// </exception>
        public VarOpt Merge(VarOpt other)
        {
            ArgumentNullException.ThrowIfNull(other);
            // An input that has not begun sampling is holding original weights, so
            // it is data rather than a sample and merges into anything. One that
            // has begun sampling may only be merged into a result that keeps no
            // more items than it does.
            if (other.LightCount > 0 && other.k < this.k)
            {
                throw new ArgumentException(
                    $"Cannot merge a sample keeping {other.k} items into one " +
                    $"keeping {this.k}. The smaller sample has begun sampling, so " +
                    "its items stand in for the ones it dropped; a result with " +
                    "room for all of them would report them as exact. Merge the " +
                    "other way round, or into a sample keeping " +
                    $"{other.k} or fewer.",
                    nameof(other));
            }

            // Snapshot before inserting: merging a sample into itself must read the
            // state as it was, not the state mid-rebuild. The items are copied
            // because insertion takes ownership of what it is given.
            var count = other.HeavyCount + other.LightCount;
            var items = new byte[count][];
            var weights = new double[count];
            for (var i = 0; i < other.HeavyCount; i++)
            {
                items[i] = other.HeavyItems[i].AsSpan().ToArray();
                weights[i] = other.HeavyWeights[i];
            }
            var tau = other.Tau;
            for (var i = 0; i < other.LightCount; i++)
            {
                items[other.HeavyCount + i] = other.LightItems[i].AsSpan().ToArray();
                weights[other.HeavyCount + i] = tau;
            }
            var otherN = other.N;

            if (other.LightCount > 0 && this.SampleCount + count <= this.k)
            {
                // Everything the other sample holds fits, though it has dropped
                // items. The rule above leaves one way for that to happen: this
                // sample is empty and the other keeps exactly as many items. The
                // paper's Lemma 4 settles it -- the result is the other sample,
                // threshold region and all. Feeding the threshold items through
                // insertion here would hold them as exact items, in a sample that
                // claimed to be exact while counting more items than it holds.
                for (var i = 0; i < other.HeavyCount; i++)
                {
                    this.HeavyItems[i] = items[i];
                    this.HeavyWeights[i] = weights[i];
                }
                this.HeavyCount = other.HeavyCount;
                for (var i = 0; i < other.LightCount; i++)
                {
                    this.LightItems[i] = items[other.HeavyCount + i];
                }
                this.LightCount = other.LightCount;
                // The region's total, not the threshold times its size. The total is
                // what a sample holds and the threshold is its quotient, so copying the
                // total carries the other sample's over unchanged, where the product
                // would round.
                this.LightWeight = other.LightWeight;
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    Insert(items[i], weights[i]);
                }
            }
            this.N += otherN;
            return this;
        }

        /// <summary>
        /// Restores the structure to its original state. Returns the VarOpt to allow
        /// for chaining.
        /// </summary>
        public VarOpt Reset()
        {
            Array.Clear(this.HeavyItems);
            Array.Clear(this.HeavyWeights);
            Array.Clear(this.LightItems);
            Array.Clear(this.candidateItems);
            Array.Clear(this.candidateWeights);
            this.HeavyCount = 0;
            this.LightCount = 0;
            this.candidateCount = 0;
            this.LightWeight = 0.0;
            this.N = 0;
            return this;
        }

        /// <summary>
        /// The insertion path shared by adding and merging: place one item, already
        /// owned by this structure, at the given weight.
        /// </summary>
        private void Insert(byte[] item, double weight)
        {
            if (this.LightCount == 0)
            {
                // Exact phase: everything seen is kept as itself.
                this.HeavyItems[this.HeavyCount] = item;
                this.HeavyWeights[this.HeavyCount] = weight;
                this.HeavyCount++;
                if (this.HeavyCount == this.k + 1)
                {
                    TransitionToSampling();
                }
                return;
            }

            // Sampling phase: k items held, one arriving, one of the k + 1 must go.
            this.candidateCount = 0;
            var r = this.LightCount;

            // The threshold the contest would settle at if the contestants turned
            // out to be the light region plus this item: (weight + light total)
            // divided by ((r + 1) - 1) survivors.
            var hypotheticalTau = (weight + this.LightWeight) / r;

            var lighterThanHeap =
                this.HeavyCount == 0 || weight <= this.HeavyWeights[0];
            if (lighterThanHeap && weight < hypotheticalTau)
            {
                // Light: the new item is a contestant from the start.
                this.candidateItems[0] = item;
                this.candidateWeights[0] = weight;
                this.candidateCount = 1;
                GrowCandidates(this.LightWeight + weight, r + 1);
            }
            else if (r == 1)
            {
                // Heavy with a single light item: any two items can settle a
                // contest, so seed one with the lightest heavy and the light region.
                HeapPush(item, weight);
                PopMinToCandidates();
                GrowCandidates(this.candidateWeights[0] + this.LightWeight, 2);
            }
            else
            {
                // Heavy in general: it may still be contested back out of the heap,
                // but the contest starts among the light region alone.
                HeapPush(item, weight);
                GrowCandidates(this.LightWeight, r);
            }
        }

        /// <summary>
        /// The moment the k + 1st exact item arrives: order the heap, and seed the
        /// light region and the contest with the two lightest items, which any
        /// threshold can settle between.
        /// </summary>
        private void TransitionToSampling()
        {
            Heapify();

            this.LightItems[0] = this.HeavyItems[0];
            this.LightCount = 1;
            this.LightWeight = this.HeavyWeights[0];
            RemoveHeapMin();

            PopMinToCandidates();
            GrowCandidates(this.candidateWeights[0] + this.LightWeight, 2);
        }

        /// <summary>
        /// Pulls heavy items into the contest for as long as the lightest of them
        /// would fall at or below the threshold the enlarged contest would settle
        /// at, then settles it.
        /// </summary>
        private void GrowCandidates(double candidateWeight, int candidates)
        {
            while (this.HeavyCount > 0)
            {
                var next = this.HeavyWeights[0];
                // Pull iff next < (candidateWeight + next) / candidates, the
                // threshold the contest would have with it included -- multiplied
                // through, because the division would round.
                if (next * candidates < candidateWeight + next)
                {
                    candidateWeight += next;
                    candidates++;
                    PopMinToCandidates();
                }
                else
                {
                    break;
                }
            }

            Downsample(candidateWeight, candidates);
        }

        /// <summary>
        /// Drops exactly one contestant. Each named contestant is dropped with
        /// probability 1 - weight/threshold, and the light region -- every member
        /// equally below the threshold -- absorbs whatever probability remains; the
        /// masses sum to exactly one by the threshold's construction. Survivors
        /// become the new light region, inheriting the full contested weight.
        /// </summary>
        private void Downsample(double candidateWeight, int candidates)
        {
            var m = this.candidateCount;
            var keep = candidates - 1;

            double u;
            do
            {
                u = this.random.NextDouble();
            } while (u == 0.0);

            // Walk the named contestants accumulating drop probability, in the
            // multiplied-through form of the paper's equation (8).
            var dropped = -1;
            if (m > 0)
            {
                var left = 0.0;
                var right = -candidateWeight * u;
                for (var i = 0; i < m; i++)
                {
                    left += keep * this.candidateWeights[i];
                    right += candidateWeight;
                    if (left < right)
                    {
                        dropped = i;
                        break;
                    }
                }
            }

            if (dropped < 0)
            {
                // The drop fell to the light region, whose members are
                // interchangeable: evict one uniformly.
                var victim = (int)EvictionSlot(this.random.Next(), (uint)this.LightCount);
                this.LightItems[victim] = this.LightItems[this.LightCount - 1];
                this.LightItems[this.LightCount - 1] = null!;
                this.LightCount--;
            }

            for (var i = 0; i < m; i++)
            {
                if (i != dropped)
                {
                    this.LightItems[this.LightCount] = this.candidateItems[i];
                    this.LightCount++;
                }
                this.candidateItems[i] = null!;
            }
            this.candidateCount = 0;

            // The survivors inherit the dropped item's weight: this is what keeps the
            // drop from losing any of it, and every survivor's adjusted weight at the
            // threshold.
            this.LightWeight = candidateWeight;
        }

        /// <summary>
        /// The slot of the light item an eviction removes, for the generator's next 64
        /// bits and a light region of <paramref name="lightCount"/> items: the high half of
        /// their 128-bit product.
        /// </summary>
        /// <remarks>
        /// Each slot is drawn by floor(2^64 / L) of the 2^64 inputs or by one more, so every
        /// light item is within 2^-32 of an equal chance of going, at every size the light
        /// region can reach. The estimate is unbiased because the items there are
        /// interchangeable and each is equally likely to go, and this is what keeps that
        /// true of the draw as well as of the paper.
        /// <para>
        /// Until the release after 6.2.0 the slot came from
        /// <see cref="SeededRandom.NextBelow"/>, which multiplies only the top 32 bits.
        /// That gives each slot floor(2^32 / L) of 2^32 inputs or one more, so once the light
        /// region passed 2^32 / 3 some slots were drawn half as often again as others. Apart
        /// from the item kept most recently, which moves into the next slot drawn, an item
        /// stays where it is until its slot is drawn, so one that settled in a slot drawn
        /// less often stayed longer. In a stream whose items are all light, as they are with
        /// unit weights once sampling begins, that over-represented the oldest part of the
        /// stream, whose estimates came out high, and the rest came out a little low. The
        /// bias grows with k. For the oldest fiftieth of the stream, where it is largest
        /// against the estimate's standard error, it was under a hundredth of that error up
        /// to a k of ten million, one standard error from about 87 million, six at 175
        /// million and hundreds past a billion. Those figures come from the two counts and a
        /// model of the stream, which a scaled-down simulation of the draw reproduced. The
        /// suite does not rebuild them.
        /// </para>
        /// <para>
        /// One input is still drawn per eviction, so after any stream the generator stands
        /// where it did before, and a payload's stored position means what it meant. The
        /// slot is the one the 32-bit draw gave, or the slot after it. It moves in about
        /// L / 2^33 of draws, too few for a small seeded sample to meet one in practice.
        /// Downsample draws through this so that a test can count the inputs that evict
        /// each slot without a sample of that size.
        /// </para>
        /// </remarks>
        internal static uint EvictionSlot(ulong next, uint lightCount)
        {
            return (uint)Math.BigMul(next, lightCount, out _);
        }

        /// <summary>
        /// Pushes an item onto the heavy heap.
        /// </summary>
        private void HeapPush(byte[] item, double weight)
        {
            var slot = this.HeavyCount;
            this.HeavyItems[slot] = item;
            this.HeavyWeights[slot] = weight;
            this.HeavyCount++;

            while (slot > 0)
            {
                var parent = (slot - 1) / 2;
                if (this.HeavyWeights[parent] <= this.HeavyWeights[slot])
                {
                    break;
                }
                Swap(slot, parent);
                slot = parent;
            }
        }

        /// <summary>
        /// Moves the lightest heavy item into the contest.
        /// </summary>
        private void PopMinToCandidates()
        {
            this.candidateItems[this.candidateCount] = this.HeavyItems[0];
            this.candidateWeights[this.candidateCount] = this.HeavyWeights[0];
            this.candidateCount++;
            RemoveHeapMin();
        }

        /// <summary>
        /// Removes the heap's root and restores the ordering.
        /// </summary>
        private void RemoveHeapMin()
        {
            this.HeavyCount--;
            this.HeavyItems[0] = this.HeavyItems[this.HeavyCount];
            this.HeavyWeights[0] = this.HeavyWeights[this.HeavyCount];
            this.HeavyItems[this.HeavyCount] = null!;
            SiftDown(0);
        }

        /// <summary>
        /// Restores the min-heap ordering downward from the given slot.
        /// </summary>
        private void SiftDown(int slot)
        {
            while (true)
            {
                if (!TryLeftChild(slot, this.HeavyCount, out var child))
                {
                    break;
                }
                var sibling = child + 1;
                if (sibling < this.HeavyCount &&
                    this.HeavyWeights[sibling] < this.HeavyWeights[child])
                {
                    child = sibling;
                }
                if (this.HeavyWeights[slot] <= this.HeavyWeights[child])
                {
                    break;
                }
                Swap(slot, child);
                slot = child;
            }
        }

        /// <summary>
        /// Whether <paramref name="slot"/> has a child in a heap of
        /// <paramref name="count"/> entries, and if it has, where the left one is. The
        /// right one, if there is one, is the slot after it.
        /// </summary>
        /// <remarks>
        /// The left child is 2 * slot + 1, worked out in 64 bits. In 32 it wrapped negative
        /// from slot 2^30, which is below every count, so the slot seemed to have a child,
        /// and the sift then read before the start of the arrays. That needs a heap of more
        /// than 2^30 entries, so a k of 2^30 or more, and weights unequal enough to move an
        /// item that far down. The add that did it threw
        /// <see cref="IndexOutOfRangeException"/> and left the sample unusable; with
        /// decreasing weights at k = 2^30 it was the add that began sampling. A heap holds
        /// at most k + 1 entries, never more than <see cref="Array.MaxLength"/>, so no slot
        /// from 2^30 has a child, and a child that is found is below the count, which leaves
        /// room for the slot after it.
        /// <para>
        /// <see cref="SiftDown"/> asks this, and every heap operation that moves an item
        /// down goes through it: the heapify that begins sampling, the one a read does, and
        /// the removal of the lightest heavy item. So a test can ask it about slots that no
        /// sample a test can build reaches.
        /// </para>
        /// </remarks>
        internal static bool TryLeftChild(int slot, int count, out int child)
        {
            var left = (2L * slot) + 1;
            if (left < count)
            {
                child = (int)left;
                return true;
            }
            child = -1;
            return false;
        }

        /// <summary>
        /// Orders the heavy arrays into a min-heap from arbitrary order.
        /// </summary>
        private void Heapify()
        {
            for (var slot = (this.HeavyCount / 2) - 1; slot >= 0; slot--)
            {
                SiftDown(slot);
            }
        }

        private void Swap(int a, int b)
        {
            (this.HeavyItems[a], this.HeavyItems[b]) =
                (this.HeavyItems[b], this.HeavyItems[a]);
            (this.HeavyWeights[a], this.HeavyWeights[b]) =
                (this.HeavyWeights[b], this.HeavyWeights[a]);
        }

        /// <summary>
        /// Writes the structure to a stream in the library's persistence format.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = new PayloadWriter();
            payload.WriteUInt32(this.k);
            payload.WriteUInt64(this.random.State);
            payload.WriteUInt64(this.N);
            payload.WriteUInt32((uint)this.HeavyCount);
            payload.WriteUInt32((uint)this.LightCount);
            payload.WriteDouble(this.LightWeight);

            for (var i = 0; i < this.HeavyCount; i++)
            {
                payload.WriteBytes(this.HeavyItems[i]);
                payload.WriteDouble(this.HeavyWeights[i]);
            }
            for (var i = 0; i < this.LightCount; i++)
            {
                payload.WriteBytes(this.LightItems[i]);
            }

            PersistenceFormat.Write(
                stream,
                StructureId.VarOpt,
                HashId.None,
                payload.WrittenSpan);
        }

        /// <summary>
        /// Reads a structure written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The structure that was written.</returns>
        public static VarOpt ReadFrom(Stream stream)
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
        public static VarOpt ReadFrom(Stream stream, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, null, options);
        }

        /// <summary>
        /// Reads a structure written by <see cref="WriteTo"/>. The structure does
        /// not hash, so supplying a hash function is refused rather than ignored.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">Not used, and refused if supplied.</param>
        /// <returns>The structure that was written.</returns>
        public static VarOpt ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
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
        public static VarOpt ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, hash, options);
        }

        private static VarOpt Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.VarOpt);
            var payload = PersistenceFormat.Read(
                stream, StructureId.VarOpt, out _, budget, Price,
                hash, CheckHash);

            var reader = new PayloadReader(payload, budget);

            var k = reader.ReadUInt32();
            var randomState = reader.ReadUInt64();
            var n = reader.ReadUInt64();
            var heavyCount = reader.ReadUInt32();
            var lightCount = reader.ReadUInt32();
            var lightWeight = reader.ReadDouble();
            CheckCounts(k, n, heavyCount, lightCount, lightWeight);

            // Its five regions, k + 1 slots each, whatever the payload holds: the
            // sample's arithmetic assumes their size.
            budget.Charge(RegionsCost(k));
            var restored = new VarOpt(k, randomState)
            {
                N = n,
                LightWeight = lightWeight,
            };

            for (uint i = 0; i < heavyCount; i++)
            {
                var item = reader.ReadBytes();
                var weight = reader.ReadDouble();
                if (double.IsNaN(weight) || double.IsInfinity(weight) || weight <= 0.0)
                {
                    throw new InvalidDataException(
                        $"Structure holds an item at weight {weight}, which " +
                        "insertion refuses, so nothing this library wrote holds one.");
                }
                restored.HeavyItems[restored.HeavyCount] = item;
                restored.HeavyWeights[restored.HeavyCount] = weight;
                restored.HeavyCount++;
            }
            if (lightCount > 0)
            {
                // A sampling heap is ordered here rather than trusted from the
                // payload; one this library wrote is a heap already, and comes
                // through unchanged. An exact sample is not a heap but its arrival
                // order, which any order can be and which the transition to
                // sampling heaps from, so it is kept as written: re-heaping it
                // changes which of two tied items is contested first.
                restored.Heapify();
            }

            for (uint i = 0; i < lightCount; i++)
            {
                restored.LightItems[restored.LightCount] = reader.ReadBytes();
                restored.LightCount++;
            }

            reader.ExpectEnd();

            return restored;
        }

        /// <summary>
        /// Refuses a supplied hash, and a payload naming any hash but none: this
        /// structure hashes nothing. See <see cref="HashCheck"/>.
        /// </summary>
        private static void CheckHash(HashId recorded, Func<ReadOnlySpan<byte>, ulong>? supplied)
        {
            if (supplied is not null)
            {
                throw new InvalidDataException(
                    "A VarOpt sample does not hash anything -- it keeps the items " +
                    "themselves -- so it cannot be read with a supplied hash " +
                    "function. Read it with the overload that takes none.");
            }

            if (recorded != HashId.None)
            {
                throw new InvalidDataException(
                    $"Payload names hash function {(ushort)recorded}, and a VarOpt " +
                    "sample does not hash anything. It was not written by this " +
                    "structure.");
            }
        }

        /// <summary>
        /// Prices what <see cref="Read"/> allocates beyond the envelope: the five regions,
        /// and the copy of each item. See <see cref="PayloadPrice"/>.
        /// </summary>
        internal static void Price(ref PayloadReader reader, ushort version)
        {
            var k = reader.ReadUInt32();
            reader.ReadUInt64();
            var n = reader.ReadUInt64();
            var heavyCount = reader.ReadUInt32();
            var lightCount = reader.ReadUInt32();
            var lightWeight = reader.ReadDouble();
            CheckCounts(k, n, heavyCount, lightCount, lightWeight);

            reader.Budget.Price(RegionsCost(k));
            for (uint i = 0; i < heavyCount; i++)
            {
                reader.SkipBytes();
                reader.ReadDouble();
            }
            for (uint i = 0; i < lightCount; i++)
            {
                reader.SkipBytes();
            }
        }

        /// <summary>The five regions of a sample of <paramref name="k"/>, k + 1 slots each.</summary>
        private static long RegionsCost(uint k) =>
            ReadBudget.Times(5, ReadBudget.ArrayOf((ulong)k + 1, sizeof(long)));

        /// <summary>
        /// Checks the counts a payload claims, as both the read and its price do before
        /// the regions are allocated.
        /// </summary>
        private static void CheckCounts(uint k, ulong n, uint heavyCount, uint lightCount, double lightWeight)
        {
            if (k == 0)
            {
                throw new InvalidDataException(
                    "Structure keeps no samples, and a sample of nothing answers " +
                    "nothing; this library never builds one.");
            }
            // The constructor's own limit, so that every sample it builds reads back.
            if (k > MaxK)
            {
                throw new InvalidDataException(
                    $"Structure claims room for {k} sampled items, beyond anything " +
                    $"this library builds: it keeps at most {MaxK}.");
            }
            if (heavyCount > k || lightCount > k ||
                heavyCount + (ulong)lightCount > k)
            {
                throw new InvalidDataException(
                    $"Structure holds {heavyCount} exact and {lightCount} threshold " +
                    $"items with room for {k}.");
            }
            if (lightCount == 0)
            {
                // Still exact: every item seen is still held, and no threshold
                // weight can exist because there is no threshold.
                if (heavyCount > n)
                {
                    throw new InvalidDataException(
                        $"Structure claims {n} items seen but holds {heavyCount}, " +
                        "more than it has seen.");
                }
                if (heavyCount < n)
                {
                    // Fewer held than seen, with no threshold to account for the
                    // rest. Version 6.2.0 wrote this after merging a sample that
                    // had begun sampling into one with room for everything it
                    // held. Which items stood in for dropped ones is not in the
                    // payload, so restoring it would be a guess.
                    throw new InvalidDataException(
                        $"Structure claims {n} items seen but holds {heavyCount} " +
                        "without having started sampling, and an exact sample " +
                        "holds everything it has seen. Version 6.2.0 wrote this " +
                        "state after merging a sample that had begun sampling " +
                        "into one with room for everything it held. The payload " +
                        "does not record which items were held at the threshold, " +
                        "so it cannot be restored without guessing; merge the " +
                        "original samples again with this version.");
                }
                if (BitConverter.DoubleToInt64Bits(lightWeight) != 0)
                {
                    throw new InvalidDataException(
                        $"Structure carries a threshold-region weight of " +
                        $"{lightWeight} with no threshold region.");
                }
            }
            else
            {
                // Sampling: the reservoir is full by construction, and the
                // threshold region's weight is a positive finite number.
                if (heavyCount + (ulong)lightCount != k)
                {
                    throw new InvalidDataException(
                        $"Structure is sampling with {heavyCount + (ulong)lightCount} " +
                        $"items held of {k}, and sampling only ever begins once " +
                        "the reservoir is full.");
                }
                if (n <= k)
                {
                    throw new InvalidDataException(
                        $"Structure claims {n} items seen of {k} kept, yet has " +
                        "started sampling, which takes more items than fit.");
                }
                if (double.IsNaN(lightWeight) || double.IsInfinity(lightWeight) ||
                    lightWeight <= 0.0)
                {
                    throw new InvalidDataException(
                        $"Structure carries a threshold-region weight of " +
                        $"{lightWeight}, which no positive weights can sum to.");
                }
            }
        }
    }
}
