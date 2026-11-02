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

            if (k > ElementHeap.MaxCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(k), k,
                    $"A HeavyKeeper can track at most {ElementHeap.MaxCapacity} elements, " +
                    "the most its heap and the index beside it can hold.");
            }
            if (width > MaxWidth)
            {
                throw new ArgumentOutOfRangeException(nameof(width), width,
                    $"An array of buckets holds at most {MaxWidth}, the most an array " +
                    "can.");
            }
            if (depth > MaxDepth)
            {
                throw new ArgumentOutOfRangeException(nameof(depth), depth,
                    $"A HeavyKeeper holds at most {MaxDepth} arrays of buckets, the most " +
                    "an array of them can.");
            }

            this.k = k;
            this.Width = width;
            this.Depth = depth;
            this.decay = decay;
            this.random = seed is null
                ? SeededRandom.Unpredictable()
                : new SeededRandom(seed.Value);
            this.elements = new ElementHeap((int)k);
            (this.Fingerprints, this.Counters) = Buckets(width, depth);
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();
        }

        /// <summary>
        /// Used only by <see cref="Read"/>, which has checked every argument against the
        /// limits the public constructor applies. The heap starts empty: the reader
        /// sizes it by the elements the payload holds rather than by k, so a large k
        /// costs nothing until it is used.
        /// </summary>
        private HeavyKeeper(Func<ReadOnlySpan<byte>, ulong> hash,
            uint k, uint width, uint depth, double decay, ulong seed)
        {
            this.k = k;
            this.Width = width;
            this.Depth = depth;
            this.decay = decay;
            this.random = new SeededRandom(seed);
            this.elements = new ElementHeap(0);
            (this.Fingerprints, this.Counters) = Buckets(width, depth);
            this.Hash = hash;
        }

        /// <summary>
        /// The most buckets in one array: each array is a .NET array.
        /// </summary>
        /// <remarks>
        /// The constructor and the reader both refuse past this, and past
        /// <see cref="MaxDepth"/>, so a structure one builds the other reads. The
        /// reader used to stop at a million buckets in all, width times depth.
        /// </remarks>
        internal static uint MaxWidth => (uint)Array.MaxLength;

        /// <summary>
        /// The most arrays of buckets: they are held in an array of their own.
        /// </summary>
        internal static uint MaxDepth => (uint)Array.MaxLength;

        private static (ushort[][], ulong[][]) Buckets(uint width, uint depth)
        {
            var fingerprints = new ushort[depth][];
            var counters = new ulong[depth][];
            for (var j = 0; j < depth; j++)
            {
                fingerprints[j] = new ushort[width];
                counters[j] = new ulong[width];
            }
            return (fingerprints, counters);
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
        public static HeavyKeeper ReadFrom(Stream stream, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, null, options);
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
        public static HeavyKeeper ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, hash, options);
        }

        private static HeavyKeeper Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.HeavyKeeper);
            var payload = PersistenceFormat.Read(stream, StructureId.HeavyKeeper, out var hashId, budget, Price);
            var reader = new PayloadReader(payload, budget);

            var k = reader.ReadUInt32();
            var width = reader.ReadUInt32();
            var depth = reader.ReadUInt32();
            var decay = reader.ReadDouble();
            var randomState = reader.ReadUInt64();
            var n = reader.ReadUInt64();
            CheckShape(ref reader, k, width, depth, decay);

            budget.Charge(ArraysCost(width, depth));
            var restored = new HeavyKeeper(PersistenceFormat.ResolveOrThrow(hashId, hash),
                k, width, depth, decay, randomState)
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

            // Sized by what is held, not by k: the heap grows as elements arrive.
            var elementCount = ReadElementCount(ref reader, k);
            budget.Charge(ElementHeap.RestoreCost(elementCount));
            restored.elements = new ElementHeap((int)elementCount);
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

        /// <summary>
        /// Prices what <see cref="Read"/> allocates beyond the envelope: the fingerprint
        /// and counter arrays, the heap, and the copy of each element's data. See
        /// <see cref="PayloadPrice"/>.
        /// </summary>
        internal static void Price(ref PayloadReader reader, ushort version)
        {
            var k = reader.ReadUInt32();
            var width = reader.ReadUInt32();
            var depth = reader.ReadUInt32();
            var decay = reader.ReadDouble();
            reader.ReadUInt64();
            reader.ReadUInt64();
            CheckShape(ref reader, k, width, depth, decay);

            reader.Budget.Price(ArraysCost(width, depth));
            reader.Skip((long)width * depth * (sizeof(ushort) + sizeof(ulong)));

            var elementCount = ReadElementCount(ref reader, k);
            reader.Budget.Price(ElementHeap.RestoreCost(elementCount));
            for (uint i = 0; i < elementCount; i++)
            {
                reader.SkipBytes();
                reader.ReadUInt64();
            }
        }

        /// <summary>
        /// Reads how many elements are held, checked against the room for them and the
        /// bytes behind them, as both the read and its price do before the heap is sized.
        /// Each element is its length, at least, and its frequency.
        /// </summary>
        private static uint ReadElementCount(ref PayloadReader reader, uint k)
        {
            var elementCount = reader.ReadUInt32();
            if (elementCount > k)
            {
                throw new InvalidDataException(
                    $"Structure holds {elementCount} elements with room for {k}.");
            }

            reader.RequireHolds(elementCount, sizeof(uint) + sizeof(ulong), "elements");
            return elementCount;
        }

        /// <summary>
        /// The arrays of <paramref name="depth"/> rows of <paramref name="width"/>
        /// fingerprints and counters.
        /// </summary>
        private static long ArraysCost(uint width, uint depth) => ReadBudget.Sum(
            ReadBudget.Times(2, ReadBudget.ArrayOf(depth, sizeof(long))),
            ReadBudget.Times(depth, ReadBudget.Sum(
                ReadBudget.ArrayOf(width, sizeof(ushort)),
                ReadBudget.ArrayOf(width, sizeof(ulong)))));

        /// <summary>
        /// Checks the shape a payload claims, as both the read and its price do before
        /// the arrays are allocated.
        /// </summary>
        private static void CheckShape(ref PayloadReader reader, uint k, uint width, uint depth, double decay)
        {
            if (k == 0)
            {
                throw new InvalidDataException(
                    "Structure tracks no elements, and indexes its empty heap on the " +
                    "first add.");
            }
            // The constructor's own limits, so that every structure it builds reads back.
            if (k > ElementHeap.MaxCapacity)
            {
                throw new InvalidDataException(
                    $"Structure claims room for {k} tracked elements, beyond " +
                    $"anything this library builds: it tracks at most " +
                    $"{ElementHeap.MaxCapacity}.");
            }
            if (width == 0 || depth == 0)
            {
                throw new InvalidDataException(
                    "Structure claims arrays with no buckets, and every insertion " +
                    "indexes one.");
            }
            if (width > MaxWidth || depth > MaxDepth)
            {
                throw new InvalidDataException(
                    $"Structure claims {depth} arrays of {width} buckets, beyond " +
                    $"anything this library builds: at most {MaxDepth} arrays of " +
                    $"{MaxWidth}.");
            }
            if (double.IsNaN(decay) || double.IsInfinity(decay) || decay <= 1.0)
            {
                throw new InvalidDataException(
                    $"Structure claims a decay base of {decay}, under which no " +
                    "bucket could hold anything; this library never builds one.");
            }

            // Every bucket is written, a fingerprint and a counter, so the arrays are
            // checked against the bytes behind them before they are allocated.
            reader.RequireHolds(
                (ulong)width * depth, sizeof(ushort) + sizeof(ulong), "buckets");
        }
    }
}
