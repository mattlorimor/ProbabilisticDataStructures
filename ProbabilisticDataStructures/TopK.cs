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
    public class TopK : IBinaryPersistable<TopK>
    {
        private CountMinSketch Cms { get; set; }
        private uint K { get; set; }
        internal uint N { get; set; }
        private ElementHeap elements { get; set; }

        /// <summary>
        /// Creates a new TopK backed by a Count-Min sketch whose relative accuracy is
        /// within a factor of epsilon with probability delta. It tracks the k-most
        /// frequent elements.
        /// </summary>
        /// <param name="epsilon">Relative-accuracy factor</param>
        /// <param name="delta">Relative-accuracy probability</param>
        /// <param name="k">Number of top elements to track</param>
        /// <returns></returns>
        /// <param name="hash">
        /// The hash function to use, or null for the default. Passing it here is the
        /// only way to have one hash cover everything the structure will ever hold:
        /// once anything has been added, the hash can no longer be replaced.
        /// </param>
        public TopK(double epsilon, double delta, uint k,
            Func<ReadOnlySpan<byte>, ulong>? hash = null)
        {
            Guard.ValidItemCount(k, nameof(k));

            this.Cms = new CountMinSketch(epsilon, delta, hash);
            this.K = k;
            this.elements = new ElementHeap((int)k);
        }

        /// <summary>
        /// Will add the data to the Count-Min Sketch and update the top-k heap if
        /// applicable. Returns the TopK to allow for chaining.
        /// </summary>
        /// <param name="data">The data to add</param>
        /// <returns>The TopK</returns>
        public TopK Add(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            this.Cms.Add(data);
            this.N++;

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
            this.N += other.N;

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

            var rebuilt = new ElementHeap((int)this.K);

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
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = new PayloadWriter();
            payload.WriteUInt32(this.K);
            payload.WriteUInt32(this.N);

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

            PersistenceFormat.Write(
                stream,
                StructureId.TopK,
                PersistenceFormat.Identify(this.Cms.HashFunction),
                payload.WrittenSpan);
        }

        /// <summary>
        /// Reads a structure written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The structure that was written.</returns>
        public static TopK ReadFrom(Stream stream)
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
        public static TopK ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            return Read(stream, hash);
        }

        private static TopK Read(Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(stream, StructureId.TopK, out _);
            var reader = new PayloadReader(payload);

            var k = reader.ReadUInt32();
            var n = reader.ReadUInt32();

            if (k == 0)
            {
                throw new InvalidDataException(
                    "Structure has room for no elements, and indexes its empty heap on " +
                    "the first add.");
            }

            if (k > PersistenceFormat.MaxNestedCount)
            {
                throw new InvalidDataException(
                    $"Structure claims room for {k} elements, beyond anything this " +
                    "library builds.");
            }

            var sketch = PersistenceFormat.ReadNested<CountMinSketch>(ref reader, hash);

            var elementCount = reader.ReadUInt32();
            if (elementCount > k)
            {
                throw new InvalidDataException(
                    $"Structure holds {elementCount} elements with room for {k}.");
            }

            var heap = new ElementHeap((int)k);
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
                N = n,
                elements = heap,
            };
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
            this.elements = new ElementHeap((int)K);
            this.N = 0;
            return this;
        }
    }
}
