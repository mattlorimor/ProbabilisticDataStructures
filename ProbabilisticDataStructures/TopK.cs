using System;
using System.IO;
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
        public TopK(double epsilon, double delta, uint k)
        {
            Guard.ValidItemCount(k, nameof(k));

            this.Cms = new CountMinSketch(epsilon, delta);
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
                payload.WriteBytes(element.Data);
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
