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
    /// all: the adjusted weights sum to exactly the weight that went in. The price is
    /// that k items answer every question, so any question known in advance is served
    /// far better per byte by the structure dedicated to it.
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
    /// Two samples built with the same k merge by feeding one sample's items, at
    /// their adjusted weights, through the other's ordinary insertion path -- the
    /// paper's own recurrence for distributed sampling, valid precisely because the
    /// k's are equal. DataSketches instead lets k float between inputs, at the cost
    /// of machinery (marked items, a gadget that is not itself a VarOpt sample, a
    /// k-reduction pass) that exists only to repair the cases floating k creates.
    /// This implementation refuses unequal k's and needs none of it.
    /// </para>
    /// </remarks>
    public class VarOpt : IBinaryPersistable<VarOpt>
    {
        /// <summary>
        /// Items heavier than the threshold, held with exact weights. A binary
        /// min-heap on the weights, so the lightest heavy item is always at index
        /// zero, ready to be contested.
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
        /// The light region's total weight, maintained exactly across every drop:
        /// when a contestant is eliminated, the survivors inherit its weight rather
        /// than the stream losing it. The threshold is this divided by the count,
        /// and is never stored, so the total never drifts.
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
        /// The number of items currently held: min(k, items added).
        /// </summary>
        public uint SampleCount => (uint)(this.HeavyCount + this.LightCount);

        /// <summary>
        /// The total weight of everything ever added. This is bookkeeping, not
        /// estimation: dropping an item hands its weight to the survivors, so the
        /// total is preserved exactly.
        /// </summary>
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
        /// true weight; summing all of them returns <see cref="TotalWeight"/>
        /// exactly.
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
        /// adjusted weight, exactly as if the adjusted weights were original ones.
        /// The recurrence requires the two k's to be equal -- with equal k's a
        /// sample that has started sampling contributes exactly k items, so the
        /// union always has more than k and the threshold does its job; with
        /// unequal k's the union can stay under k while pretending sampled items
        /// are exact, which is the case DataSketches builds its marked-item
        /// machinery to repair.
        /// </remarks>
        /// <param name="other">The sample to merge into this one.</param>
        public VarOpt Merge(VarOpt other)
        {
            ArgumentNullException.ThrowIfNull(other);
            if (other.k != this.k)
            {
                throw new ArgumentException(
                    $"Cannot merge a sample keeping {other.k} items into one " +
                    $"keeping {this.k}. The merge feeds one sample through the " +
                    "other's insertion path, which is only a valid VarOpt sample " +
                    "of the combined stream when the k's are equal.",
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

            for (var i = 0; i < count; i++)
            {
                Insert(items[i], weights[i]);
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
                var victim = (int)this.random.NextBelow((uint)this.LightCount);
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

            // The survivors inherit the dropped item's weight: this is what keeps
            // the total exact and every survivor's adjusted weight at the threshold.
            this.LightWeight = candidateWeight;
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
                var child = (2 * slot) + 1;
                if (child >= this.HeavyCount)
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
            return Read(stream, null);
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
            return Read(stream, hash);
        }

        private static VarOpt Read(Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(stream, StructureId.VarOpt, out var hashId);

            if (hash is not null)
            {
                throw new InvalidDataException(
                    "A VarOpt sample does not hash anything -- it keeps the items " +
                    "themselves -- so it cannot be read with a supplied hash " +
                    "function. Read it with the overload that takes none.");
            }

            if (hashId != HashId.None)
            {
                throw new InvalidDataException(
                    $"Payload names hash function {(ushort)hashId}, and a VarOpt " +
                    "sample does not hash anything. It was not written by this " +
                    "structure.");
            }

            var reader = new PayloadReader(payload);

            var k = reader.ReadUInt32();
            var randomState = reader.ReadUInt64();
            var n = reader.ReadUInt64();
            var heavyCount = reader.ReadUInt32();
            var lightCount = reader.ReadUInt32();
            var lightWeight = reader.ReadDouble();

            if (k == 0)
            {
                throw new InvalidDataException(
                    "Structure keeps no samples, and a sample of nothing answers " +
                    "nothing; this library never builds one.");
            }
            if (k > PersistenceFormat.MaxNestedCount)
            {
                throw new InvalidDataException(
                    $"Structure claims room for {k} sampled items, beyond anything " +
                    "this library builds.");
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
                if (heavyCount != n)
                {
                    throw new InvalidDataException(
                        $"Structure claims {n} items seen but holds {heavyCount} " +
                        "without having started sampling, and an exact sample " +
                        "holds everything it has seen.");
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
            // Ordered here rather than trusted from the payload.
            restored.Heapify();

            for (uint i = 0; i < lightCount; i++)
            {
                restored.LightItems[restored.LightCount] = reader.ReadBytes();
                restored.LightCount++;
            }

            reader.ExpectEnd();

            return restored;
        }
    }
}
