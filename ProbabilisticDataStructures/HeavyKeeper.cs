using System;
using System.Collections.Generic;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// HeavyKeeper finds the top-k most frequent elements of a stream, as described
    /// by Gong, Yang et al. in HeavyKeeper: An Accurate Algorithm for Finding Top-k
    /// Elephant Flows (USENIX ATC 2018).
    /// </summary>
    /// <remarks>
    /// Where <see cref="TopK"/> counts every element in a Count-Min sketch and asks
    /// which counts are large, HeavyKeeper fights for buckets: each bucket holds one
    /// element's fingerprint and count, and an arrival that finds someone else's
    /// fingerprint decays the incumbent with probability b^-C -- easy to evict while
    /// the count is small, nearly impossible once it is large. Frequent elements
    /// entrench; rare ones pass through without leaving a mark. The cost of that
    /// bargain is one-sided error in the opposite direction from Count-Min's: absent
    /// fingerprint collisions an estimate never exceeds the truth, and an element
    /// that lost its buckets reports zero rather than small.
    /// <para>
    /// One departure from the paper as printed, recorded here because behavior
    /// depends on it: Algorithm 1 increments a non-tracked element's counter only
    /// while C &lt; nmin, and admits to the tracking heap only at exactly nmin + 1 --
    /// conditions that together deadlock, since a counter could reach nmin and never
    /// exceed it. The authors' reference implementation uses C &lt;= nmin, the only
    /// reading consistent with the paper's own Theorem 1, and so does this one.
    /// </para>
    /// </remarks>
    public class HeavyKeeper : IBinaryPersistable<HeavyKeeper>
    {
        /// <summary>
        /// Fingerprint fields, one array per row: who holds each bucket.
        /// </summary>
        internal ushort[][] Fingerprints { get; set; }
        /// <summary>
        /// Counter fields, one array per row: how entrenched each holder is.
        /// </summary>
        internal ulong[][] Counters { get; set; }
        /// <summary>
        /// Buckets per array.
        /// </summary>
        internal uint Width { get; set; }
        /// <summary>
        /// Number of arrays.
        /// </summary>
        internal uint Depth { get; set; }
        /// <summary>
        /// The decay base b: a mismatched arrival decays a bucket with probability
        /// b^-C.
        /// </summary>
        private double decay;
        /// <summary>
        /// Number of elements tracked.
        /// </summary>
        private uint k;
        /// <summary>
        /// Number of items added.
        /// </summary>
        internal ulong N { get; set; }
        /// <summary>
        /// The random source behind decay decisions. Persisted, so a structure read
        /// back resumes its draw sequence rather than replaying it.
        /// </summary>
        private SeededRandom random;
        /// <summary>
        /// The tracked top-k elements.
        /// </summary>
        private ElementHeap elements;
        /// <summary>
        /// Hash function.
        /// </summary>
        private Func<ReadOnlySpan<byte>, ulong> Hash { get; set; }

        /// <summary>
        /// The hash function in use, so that a caller holding this structure can
        /// record which one it was built with.
        /// </summary>
        internal Func<ReadOnlySpan<byte>, ulong> HashFunction => this.Hash;

        /// <summary>
        /// Creates a new HeavyKeeper tracking the k most frequent elements, holding
        /// depth arrays of width buckets each.
        /// </summary>
        /// <param name="k">Number of top elements to track.</param>
        /// <param name="width">
        /// Buckets per array. More buckets mean fewer elements contesting each one;
        /// the paper's error bound falls off as 1/width.
        /// </param>
        /// <param name="depth">
        /// Number of arrays. Each element is estimated by the best of its d buckets,
        /// so extra arrays are extra chances to hold a bucket unContested. The paper
        /// uses two.
        /// </param>
        /// <param name="decay">
        /// The decay base b. A mismatched arrival decays a bucket's count with
        /// probability b^-C, so a base near one -- the paper suggests 1.08 -- decays
        /// small counts readily and large counts almost never.
        /// </param>
        /// <param name="seed">
        /// Seed for the decay draws, or null to seed unpredictably. Two structures
        /// with the same seed fed the same stream make the same decisions.
        /// </param>
        /// <param name="hash">
        /// The hash function to use, or null for the default. Passing it here is the
        /// only way to have one hash cover everything the structure will ever hold:
        /// once anything has been added, the hash can no longer be replaced.
        /// </param>
        public HeavyKeeper(uint k, uint width, uint depth = 2, double decay = 1.08,
            ulong? seed = null, Func<ReadOnlySpan<byte>, ulong>? hash = null)
        {
            Guard.ValidItemCount(k, nameof(k));
            Guard.ValidItemCount(width, nameof(width));
            Guard.ValidItemCount(depth, nameof(depth));
            Guard.ValidDecayBase(decay, nameof(decay));

            this.k = k;
            this.Width = width;
            this.Depth = depth;
            this.decay = decay;
            this.random = seed is null
                ? SeededRandom.Unpredictable()
                : new SeededRandom(seed.Value);
            this.elements = new ElementHeap((int)k);
            this.Fingerprints = new ushort[depth][];
            this.Counters = new ulong[depth][];
            for (var j = 0; j < depth; j++)
            {
                this.Fingerprints[j] = new ushort[width];
                this.Counters[j] = new ulong[width];
            }
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();
        }

        /// <summary>
        /// Adds the data to the structure. Returns the HeavyKeeper to allow for
        /// chaining.
        /// </summary>
        /// <param name="data">The data to add.</param>
        /// <returns>The HeavyKeeper.</returns>
        public HeavyKeeper Add(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            return this.Add(data.AsSpan());
        }

        /// <inheritdoc cref="Add(byte[])"/>
        public HeavyKeeper Add(ReadOnlySpan<byte> data)
        {
            var kernel = Utils.HashKernel(data, this.Hash);
            var fingerprint = FingerprintOf(kernel);

            // Tracked elements always earn their increments; everyone else is held
            // to the heap minimum, so a fingerprint collision with a large incumbent
            // cannot ride that incumbent's count into the heap.
            var tracked = this.elements.TryGetFrequency(data, out var trackedFreq);
            var nmin = this.elements.Len() == 0 ? 0UL : this.elements.Heap[0].Freq;

            ulong best = 0;
            for (uint j = 0; j < this.Depth; j++)
            {
                var bucket = this.BucketIndex(kernel, j);
                var count = this.Counters[j][bucket];

                if (count == 0)
                {
                    // An empty bucket is claimed outright.
                    this.Fingerprints[j][bucket] = fingerprint;
                    this.Counters[j][bucket] = 1;
                    best = Math.Max(best, 1);
                }
                else if (this.Fingerprints[j][bucket] == fingerprint)
                {
                    // The paper's Algorithm 1 prints this guard as C < nmin, under
                    // which a counter reaches the heap minimum and stalls there
                    // forever while admission waits for nmin + 1. The reference
                    // implementation's C <= nmin is what its Theorem 1 describes.
                    if (tracked || count <= nmin)
                    {
                        count++;
                        this.Counters[j][bucket] = count;
                    }
                    best = Math.Max(best, count);
                }
                else if (this.Decays(count))
                {
                    count--;
                    this.Counters[j][bucket] = count;
                    if (count == 0)
                    {
                        this.Fingerprints[j][bucket] = fingerprint;
                        this.Counters[j][bucket] = 1;
                        best = Math.Max(best, 1);
                    }
                }
            }

            this.N++;

            if (tracked)
            {
                if (best > trackedFreq)
                {
                    this.elements.insert(data, best, this.k);
                }
            }
            else if (this.elements.Len() < this.k || best == nmin + 1)
            {
                // Admission at exactly nmin + 1 is Optimization I: without
                // fingerprint collisions, one arrival moves an estimate by at most
                // one, so anything arriving above that line is a small element
                // wearing a large one's count.
                this.elements.insert(data, best, this.k);
            }

            return this;
        }

        /// <summary>
        /// Returns the estimated count of the data: the largest count among the
        /// buckets it holds, or zero if it holds none.
        /// </summary>
        /// <remarks>
        /// Zero is an answer, not an absence: an element evicted from all of its
        /// buckets has left no trace, which is what makes room for the elements that
        /// matter. Where Count-Min reports at least the truth for everything, this
        /// reports at most the truth, and mostly nothing for rare elements. For the
        /// tracked top-k and their frequencies, use <see cref="Elements"/>.
        /// </remarks>
        /// <param name="data">The data to count.</param>
        /// <returns>The estimated count.</returns>
        public ulong Count(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            return this.Count(data.AsSpan());
        }

        /// <inheritdoc cref="Count(byte[])"/>
        public ulong Count(ReadOnlySpan<byte> data)
        {
            var kernel = Utils.HashKernel(data, this.Hash);
            var fingerprint = FingerprintOf(kernel);

            ulong best = 0;
            for (uint j = 0; j < this.Depth; j++)
            {
                var bucket = this.BucketIndex(kernel, j);
                if (this.Fingerprints[j][bucket] == fingerprint
                    && this.Counters[j][bucket] > best)
                {
                    best = this.Counters[j][bucket];
                }
            }

            return best;
        }

        /// <summary>
        /// Returns the tracked top-k elements from lowest to highest frequency.
        /// </summary>
        /// <returns>The tracked elements, from lowest to highest frequency.</returns>
        public Element[] Elements()
        {
            return this.elements.Elements();
        }

        /// <summary>
        /// Restores the structure to its empty state. It returns itself to allow for
        /// chaining.
        /// </summary>
        /// <remarks>
        /// The decay generator is deliberately not rewound: a structure that replayed
        /// its draw sequence after every reset would aim its decays at the same
        /// buckets each time, for the same reason a reload resumes the sequence
        /// rather than restarting it.
        /// </remarks>
        /// <returns>The HeavyKeeper.</returns>
        public HeavyKeeper Reset()
        {
            for (var j = 0; j < this.Depth; j++)
            {
                Array.Clear(this.Fingerprints[j]);
                Array.Clear(this.Counters[j]);
            }
            this.elements = new ElementHeap((int)this.k);
            this.N = 0;
            return this;
        }

        /// <summary>
        /// The bucket the kernel maps to in the jth array.
        /// </summary>
        private uint BucketIndex(in HashKernelReturnValue kernel, uint j)
        {
            return (kernel.LowerBaseHash + kernel.UpperBaseHash * j) % this.Width;
        }

        /// <summary>
        /// The fingerprint of the hashed element: the top sixteen bits of its hash,
        /// which the bucket index -- built from the lower bits -- does not touch.
        /// </summary>
        private static ushort FingerprintOf(in HashKernelReturnValue kernel)
        {
            return (ushort)(kernel.UpperBaseHash >> 16);
        }

        /// <summary>
        /// One decay decision: true with probability decay^-count.
        /// </summary>
        private bool Decays(ulong count)
        {
            // The probability becomes a threshold over the full 64-bit space, and
            // one draw decides. Conversion saturates for probabilities within
            // rounding of one, which is the correct answer for them.
            var p = Math.Pow(this.decay, -(double)count);
            return this.random.Next() < (ulong)(p * 18446744073709551616.0);
        }

        /// <summary>
        /// The d (array, bucket, fingerprint) addresses the data maps to -- the
        /// addressing above, made visible so a test can verify a premise about
        /// collisions instead of hoping about it.
        /// </summary>
        internal IEnumerable<(uint Array, uint Bucket, ushort Fingerprint)> MappingOf(
            ReadOnlySpan<byte> data)
        {
            var kernel = Utils.HashKernel(data, this.Hash);
            var fingerprint = FingerprintOf(kernel);
            var mapping = new (uint, uint, ushort)[this.Depth];
            for (uint j = 0; j < this.Depth; j++)
            {
                mapping[j] = (j, this.BucketIndex(kernel, j), fingerprint);
            }
            return mapping;
        }

        /// <summary>
        /// Writes this structure to a stream, in the format documented in FORMAT.md.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = new PayloadWriter();
            payload.WriteUInt32(this.k);
            payload.WriteUInt32(this.Width);
            payload.WriteUInt32(this.Depth);
            payload.WriteDouble(this.decay);
            payload.WriteUInt64(this.random.State);
            payload.WriteUInt64(this.N);

            for (var j = 0; j < this.Depth; j++)
            {
                for (var i = 0; i < this.Width; i++)
                {
                    payload.WriteUInt16(this.Fingerprints[j][i]);
                }
            }
            for (var j = 0; j < this.Depth; j++)
            {
                for (var i = 0; i < this.Width; i++)
                {
                    payload.WriteUInt64(this.Counters[j][i]);
                }
            }

            var heap = this.elements.Heap;
            payload.WriteUInt32((uint)heap.Count);
            foreach (var element in heap)
            {
                payload.WriteBytes(element.Data.Span);
                payload.WriteUInt64(element.Freq);
            }

            PersistenceFormat.Write(
                stream,
                StructureId.HeavyKeeper,
                PersistenceFormat.Identify(this.Hash),
                payload.WrittenSpan);
        }

        /// <summary>
        /// Reads a structure written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The structure that was written.</returns>
        public static HeavyKeeper ReadFrom(Stream stream)
        {
            return Read(stream, null);
        }

        /// <summary>
        /// Reads a structure written by <see cref="WriteTo"/>, using the supplied hash
        /// function rather than the one named in the payload.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function the structure was written with.</param>
        /// <returns>The structure that was written.</returns>
        public static HeavyKeeper ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            return Read(stream, hash);
        }

        private static HeavyKeeper Read(Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(stream, StructureId.HeavyKeeper, out var hashId);
            var reader = new PayloadReader(payload);

            var k = reader.ReadUInt32();
            var width = reader.ReadUInt32();
            var depth = reader.ReadUInt32();
            var decay = reader.ReadDouble();
            var randomState = reader.ReadUInt64();
            var n = reader.ReadUInt64();

            if (k == 0)
            {
                throw new InvalidDataException(
                    "Structure tracks no elements, and indexes its empty heap on the " +
                    "first add.");
            }
            if (k > PersistenceFormat.MaxNestedCount)
            {
                throw new InvalidDataException(
                    $"Structure claims room for {k} tracked elements, beyond " +
                    "anything this library builds.");
            }
            if (width == 0 || depth == 0)
            {
                throw new InvalidDataException(
                    "Structure claims arrays with no buckets, and every insertion " +
                    "indexes one.");
            }
            if ((ulong)width * depth > PersistenceFormat.MaxNestedCount)
            {
                throw new InvalidDataException(
                    $"Structure claims {(ulong)width * depth} buckets, beyond " +
                    "anything this library builds.");
            }
            if (double.IsNaN(decay) || double.IsInfinity(decay) || decay <= 1.0)
            {
                throw new InvalidDataException(
                    $"Structure claims a decay base of {decay}, under which no " +
                    "bucket could hold anything; this library never builds one.");
            }

            var restored = new HeavyKeeper(k, width, depth, decay, randomState,
                PersistenceFormat.ResolveOrThrow(hashId, hash))
            {
                N = n,
            };

            for (var j = 0; j < depth; j++)
            {
                for (var i = 0; i < width; i++)
                {
                    restored.Fingerprints[j][i] = reader.ReadUInt16();
                }
            }
            for (var j = 0; j < depth; j++)
            {
                for (var i = 0; i < width; i++)
                {
                    restored.Counters[j][i] = reader.ReadUInt64();
                }
            }

            var elementCount = reader.ReadUInt32();
            if (elementCount > k)
            {
                throw new InvalidDataException(
                    $"Structure holds {elementCount} elements with room for {k}.");
            }
            for (uint i = 0; i < elementCount; i++)
            {
                var data = reader.ReadBytes();
                var freq = reader.ReadUInt64();

                // Pushed rather than assigned into place, so the heap ordering is
                // rebuilt here rather than trusted from the payload.
                restored.elements.Push(new Element { Data = data, Freq = freq });
            }

            reader.ExpectEnd();

            return restored;
        }
    }
}
