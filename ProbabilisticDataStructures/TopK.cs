using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
namespace ProbabilisticDataStructures
{
    /// <summary>
    /// TopK uses a Count-Min Sketch to calculate the top-K frequent elements in a
    /// stream.
    /// </summary>
    public class TopK : IBinaryPersistable<TopK>, IPayloadBody
    {
        private CountMinSketch Cms { get; set; }
        private uint K { get; set; }

        /// <summary>
        /// How many items have been added, which is the count of the sketch inside.
        /// </summary>
        /// <remarks>
        /// Read from the sketch rather than kept beside it. This was a <c>uint</c> of
        /// its own, incremented with the sketch's and merged with it, and it wrapped to
        /// zero at 2^32 adds and was written that way while the sketch beside it had
        /// counted in 64 bits all along. There is one count now, so there is nothing for
        /// it to disagree with. The payload still has a 32-bit field for it, which holds
        /// this, saturated at 4,294,967,295 (see <see cref="WriteTo"/>).
        /// </remarks>
        internal ulong N => this.Cms.TotalCount();

        private ElementHeap elements { get; set; }

        /// <summary>
        /// Creates a new TopK backed by a Count-Min sketch whose relative accuracy is
        /// within a factor of epsilon with probability at least 1 - delta, where delta
        /// is the probability of exceeding that bound. It tracks the k-most
        /// frequent elements.
        /// </summary>
        /// <param name="epsilon">Relative-accuracy factor</param>
        /// <param name="delta">Probability of exceeding that overestimate</param>
        /// <param name="k">Number of top elements to track</param>
        /// <returns></returns>
        /// <param name="hash">
        /// The hash function to use, or null for the default. Passing it here is the
        /// only way to have one hash cover everything the structure will ever hold:
        /// once anything has been added, the hash can no longer be replaced.
        /// </param>
        public TopK(double epsilon, double delta, uint k,
            Func<ReadOnlySpan<byte>, ulong>? hash = null)
            : this(epsilon, delta, k, hash, null)
        {
        }

        /// <summary>
        /// Creates a structure as the public constructor does, with its sketch placing its
        /// counts as <paramref name="placement"/> says, or as its width calls for when that
        /// is null.
        /// </summary>
        /// <remarks>
        /// So that a test can have a small top-k hold a sketch placed the way a wide one
        /// will be.
        /// </remarks>
        internal TopK(double epsilon, double delta, uint k,
            Func<ReadOnlySpan<byte>, ulong>? hash, Placement? placement)
        {
            Guard.ValidItemCount(k, nameof(k));
            if (k > ElementHeap.MaxCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(k), k,
                    $"A top-k can track at most {ElementHeap.MaxCapacity} elements, the " +
                    "most its heap and the index beside it can hold.");
            }

            this.Cms = new CountMinSketch(epsilon, delta, hash, placement);
            this.K = k;
            this.elements = new ElementHeap();
        }

        /// <summary>
        /// The sketch the counts are kept in, for a test to see how it places them.
        /// </summary>
        internal CountMinSketch Sketch => this.Cms;

        /// <summary>
        /// Will add the data to the Count-Min Sketch and update the top-k heap if
        /// applicable. Returns the TopK to allow for chaining.
        /// </summary>
        /// <param name="data">The data to add</param>
        /// <returns>The TopK</returns>
        public TopK Add(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            return this.Add(data.AsSpan());
        }

        /// <inheritdoc cref="Add(byte[])"/>
        /// <remarks>
        /// Copies only when the item reaches the heap. Counting is allocation-free,
        /// and an array is materialized just for the elements that make the top-k --
        /// which the heap must keep, since <see cref="Elements"/> hands them back.
        /// </remarks>
        public TopK Add(ReadOnlySpan<byte> data)
        {
            this.Cms.Add(data);

            var freq = this.Cms.Count(data);
            if (this.elements.isTop(freq, this.K))
            {
                elements.insert(data, freq, this.K);
            }

            return this;
        }

        /// <summary>
        /// Returns the top-k elements from lowest to highest frequency.
        /// </summary>
        /// <returns>The top-k elements from lowest to highest frequency</returns>
        public Element[] Elements()
        {
            return elements.Elements();
        }

        /// <summary>
        /// Combines another top-k into this one, over the union of what both were
        /// tracking.
        /// </summary>
        /// <param name="other">The structure to combine in.</param>
        /// <returns>This structure, so calls can be chained.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// The two track a different number of elements, or their sketches were built
        /// with different dimensions or different hash functions.
        /// </exception>
        /// <remarks>
        /// Frequencies are re-read from the merged sketch rather than added together.
        /// Each structure's recorded frequency is what its own sketch last told it,
        /// and adding two of them counts twice everything both sketches already knew
        /// about. The merged sketch is the only thing that knows the combined count.
        /// <para>
        /// A merged top-k is not necessarily the true top-k of the combined stream. An
        /// element frequent in both but held by neither heap is not a candidate here,
        /// and stays invisible -- the merged sketch knows its count, but nothing asks
        /// the sketch about elements no heap was holding. This is inherent to merging
        /// bounded summaries, not a shortcut taken here.
        /// </para>
        /// </remarks>
        public TopK Merge(TopK other)
        {
            ArgumentNullException.ThrowIfNull(other);

            if (this.K != other.K)
            {
                throw new ArgumentException(
                    $"Cannot merge a top-{other.K} into a top-{this.K}.", nameof(other));
            }

            // Throws if the sketches disagree about dimensions or hashing, before
            // anything here has been changed.
            this.Cms.Merge(other.Cms);

            // Every element either was holding is a candidate, and no others can be:
            // an element neither heap held is one neither considered frequent.
            var candidates = new Dictionary<byte[], ulong>(ByteContentComparer.Instance);

            foreach (var element in this.elements.Heap)
            {
                candidates[element.Data.ToArray()] = 0;
            }

            foreach (var element in other.elements.Heap)
            {
                candidates[element.Data.ToArray()] = 0;
            }

            // Sized by what it is about to hold, which is every candidate up to k.
            var rebuilt = new ElementHeap(Math.Min(candidates.Count, (int)this.K));

            foreach (var data in candidates.Keys
                .OrderByDescending(d => this.Cms.Count(d))
                .Take((int)this.K))
            {
                rebuilt.Push(new Element
                {
                    Data = data,
                    Freq = this.Cms.Count(data),
                });
            }

            this.elements = rebuilt;
            return this;
        }

        /// <summary>
        /// Compares candidate data by content, so that the same element held by both
        /// structures is one candidate rather than two.
        /// </summary>
        private sealed class ByteContentComparer : IEqualityComparer<byte[]>
        {
            internal static readonly ByteContentComparer Instance = new ByteContentComparer();

            public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y.AsSpan());

            public int GetHashCode(byte[] obj)
            {
                var hash = new HashCode();
                hash.AddBytes(obj);
                return hash.ToHashCode();
            }
        }

        /// <summary>
        /// Writes this structure to a stream, in the format documented in FORMAT.md.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        /// <remarks>
        /// The payload's count of items added is 32 bits, so past 4,294,967,295 it holds
        /// that and no more. Nothing reads it back: the count is the sketch's, which is
        /// 64 bits and written whole.
        /// </remarks>
        public void WriteTo(Stream stream) => PersistenceFormat.Write(stream, this);

        EnvelopeHeader IPayloadBody.WritePayload(PayloadWriter payload)
        {
            payload.WriteUInt32(this.K);
            payload.WriteUInt32((uint)Math.Min(this.N, uint.MaxValue));

            // The sketch keeps its own envelope: it is a structure in its own right,
            // names its own hash, and can be pulled out and read on its own. The heap
            // is not, being a handful of elements that mean nothing away from here.
            PersistenceFormat.WriteNested(payload, this.Cms);

            var elements = this.elements.Heap;
            payload.WriteUInt32((uint)elements.Count);
            foreach (var element in elements)
            {
                payload.WriteBytes(element.Data.Span);
                payload.WriteUInt64(element.Freq);
            }

            return new(StructureId.TopK, PersistenceFormat.Identify(this.Cms.HashFunction));
        }

        /// <summary>
        /// Reads a structure written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The structure that was written.</returns>
        public static TopK ReadFrom(Stream stream)
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
        public static TopK ReadFrom(Stream stream, ReadOptions options)
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
        public static TopK ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
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
        public static TopK ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, hash, options);
        }

        private static TopK Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.TopK);

            // The hash this header names is the sketch's, and the sketch's own is checked
            // again as it is read. It is checked here as well, so that a top-k written
            // with a hash it is not read with is refused before the read is priced rather
            // than once its budget has been raised to read it.
            return PersistenceFormat.Read(
                stream, StructureId.TopK, budget, Price, hash, PersistenceFormat.CheckResolvable,
                ReadPayload);
        }

        /// <summary>
        /// Reads the payload of the envelope <c>Read</c> opened, once the envelope has
        /// been checked. See <see cref="PayloadParse{T}"/>.
        /// </summary>
        private static TopK ReadPayload(
            ref PayloadReader reader, ushort version, HashId hashId,
            Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            var budget = reader.Budget;

            var k = reader.ReadUInt32();

            // The count of items added. Read past and not kept: the sketch inside has the
            // real one, in 64 bits, and this field only ever held it saturated.
            _ = reader.ReadUInt32();
            CheckRoom(k);

            var sketch = PersistenceFormat.ReadNested(
                ref reader, hash, StructureId.CountMinSketch, PersistenceFormat.CheckResolvable, CountMinSketch.ReadPayload);

            // Sized by what is held, not by k: the heap grows as elements arrive, so a
            // structure read back behaves as it did, and a large k costs nothing until
            // it is used.
            var elementCount = ReadElementCount(ref reader, k);
            budget.Charge(ElementHeap.RestoreCost(elementCount));
            var heap = new ElementHeap((int)elementCount);
            for (uint i = 0; i < elementCount; i++)
            {
                var data = reader.ReadBytes();
                var freq = reader.ReadUInt64();

                // Pushed rather than assigned into place, so the heap ordering is
                // rebuilt here rather than trusted from the payload. The elements are
                // the same either way, and Elements() sorts them, so nothing about the
                // answer depends on the order they were stored in.
                heap.Push(new Element { Data = data, Freq = freq });
            }

            reader.ExpectEnd();

            return new TopK
            {
                Cms = sketch,
                K = k,
                elements = heap,
            };
        }

        /// <summary>
        /// Prices what <see cref="Read"/> allocates beyond the envelope: the whole of the
        /// nested sketch's read, priced from its envelope where it lies in this payload,
        /// the heap, and the copy of each element's data. See <see cref="PayloadPrice"/>.
        /// </summary>
        internal static void Price(ref PayloadReader reader, ushort version)
        {
            var k = reader.ReadUInt32();
            // The count of items added, which the reader reads past too.
            _ = reader.ReadUInt32();
            CheckRoom(k);

            PersistenceFormat.PriceNested(ref reader, StructureId.CountMinSketch, CountMinSketch.Price);

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

        private static void CheckRoom(uint k)
        {
            if (k == 0)
            {
                throw new InvalidDataException(
                    "Structure has room for no elements, and indexes its empty heap on " +
                    "the first add.");
            }

            // The constructor's own limit, so that every structure it builds reads back.
            if (k > ElementHeap.MaxCapacity)
            {
                throw new InvalidDataException(
                    $"Structure claims room for {k} elements, beyond anything this " +
                    $"library builds: it tracks at most {ElementHeap.MaxCapacity}.");
            }
        }

        /// <summary>
        /// Used only by <see cref="Read"/>, which sets every field itself.
        /// </summary>
        private TopK()
        {
            this.Cms = null!;
            this.elements = null!;
        }

        /// <summary>
        /// Restores the TopK to its original state. It returns itself to allow for
        /// chaining.
        /// </summary>
        /// <returns>The TopK</returns>
        public TopK Reset()
        {
            this.Cms.Reset();
            this.elements = new ElementHeap();
            return this;
        }
    }
}
