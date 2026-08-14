using System;
using System.Collections.Generic;
using System.Linq;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// A binary min-heap of elements ordered by frequency, with the least frequent at
    /// the root, alongside an index from an element's data to where it sits.
    /// </summary>
    /// <remarks>
    /// The root is what an arriving element is compared against and what gets evicted
    /// when the heap is full, so the ordering has to hold after every operation. Both
    /// operations that maintain it were wrong before 3.1.0: extraction removed the root
    /// with List.Remove, which slides the array rather than reordering it, and raising
    /// an element's frequency did not re-order at all.
    /// <para>
    /// The index exists because deciding whether an arriving element is already held
    /// was a scan of the whole heap, comparing its data against every element's. That
    /// is the cost that grows with k while nothing else here does: measured on a stream
    /// where every arrival reaches this code, it was around 0.47 ns per element held,
    /// which at k = 1000 was most of the time an add took.
    /// </para>
    /// </remarks>
    internal class ElementHeap
    {
        internal List<Element> Heap { get; set; }

        /// <summary>
        /// Where each element sits in <see cref="Heap"/>, kept in step with it by every
        /// operation that moves an element.
        /// </summary>
        private readonly Dictionary<ReadOnlyMemory<byte>, int> positions;

        internal ElementHeap(int k)
        {
            this.Heap = new List<Element>(k);
            this.positions = new Dictionary<ReadOnlyMemory<byte>, int>(k, ByteContentComparer.Instance);
        }

        internal int Len()
        {
            return this.Heap.Count;
        }

        internal bool Less(int i, int j)
        {
            return this.Heap[i].Freq < this.Heap[j].Freq;
        }

        internal void Swap(int i, int j)
        {
            var temp = this.Heap[i];
            Heap[i] = Heap[j];
            Heap[j] = temp;

            // The index follows the elements. Every path that reorders the heap goes
            // through here, which is what keeps the two from drifting apart.
            this.positions[this.Heap[i].Data] = i;
            this.positions[this.Heap[j].Data] = j;
        }

        internal void Push(Element e)
        {
            this.Heap.Add(e);
            this.positions[e.Data] = this.Len() - 1;
            this.Up(this.Len() - 1);
        }

        internal Element Pop()
        {
            // Standard extraction: take the root, move the last element into its place,
            // and sift that element back down. Removing the root directly would slide
            // every later element down a position, which is not a heap operation and
            // leaves no minimum at the root.
            var min = this.Heap[0];
            var last = this.Len() - 1;

            this.Swap(0, last);
            this.Heap.RemoveAt(last);
            this.positions.Remove(min.Data);
            this.Down(0, this.Len());

            return min;
        }

        internal void Up(int j)
        {
            while (true)
            {
                var i = (j - 1) / 2; // parent
                if (i == j || !this.Less(j, i))
                {
                    break;
                }
                this.Swap(i, j);
                j = i;
            }
        }

        internal void Down(int i, int n)
        {
            while (true)
            {
                var j1 = 2 * i + 1;
                if (j1 >= n || j1 < 0)
                {
                    // j1 < 0 after int overflow
                    break;
                }
                var j = j1; // left child
                var j2 = j1 + 1;
                if (j2 < n && !this.Less(j1, j2))
                {
                    j = j2; // 2*i + 2 // right child
                }
                if (!this.Less(j, i))
                {
                    break;
                }
                this.Swap(i, j);
                i = j;
            }
        }

        internal Element[] Elements()
        {
            if (this.Len() == 0)
            {
                return new Element[0];
            }
            return this.Heap
                .OrderBy(x => x.Freq)
                .ToArray();
        }

        internal void insert(byte[] data, UInt64 freq, uint k)
        {
            if (this.positions.TryGetValue(data, out var index))
            {
                // Element already in top-k. Raising its frequency in place leaves it
                // possibly greater than a child's, so the ordering has to be restored
                // or the root stops being the minimum -- and the root is both what
                // isTop compares against and what Pop evicts.
                //
                // Sifting down is enough: a frequency read from the sketch only ever
                // grows, so an updated element can only sink.
                this.Heap[index].Freq = freq;
                this.Down(index, this.Len());
                return;
            }

            if (this.Len() == k)
            {
                // Remove minimum-frequency element.
                this.Pop();
            }

            // Add element to top-k. The data is copied rather than retained: it is
            // handed back to callers through Elements(), and a caller reusing the
            // buffer they added from would otherwise rewrite every entry in the heap
            // and every key in the index along with them.
            this.Push(new Element
            {
                Data = (byte[])data.Clone(),
                Freq = freq,
            });
        }

        internal bool isTop(UInt64 freq, uint k)
        {
            if (this.Len() < k)
            {
                return true;
            }
            return freq >= this.Heap[0].Freq;
        }

        /// <summary>
        /// Compares by content, which is what identifies an element here. Comparing the
        /// underlying arrays by reference instead would hold the same element twice for
        /// a caller who does not add it from the same buffer each time.
        /// </summary>
        private sealed class ByteContentComparer : IEqualityComparer<ReadOnlyMemory<byte>>
        {
            internal static readonly ByteContentComparer Instance = new ByteContentComparer();

            public bool Equals(ReadOnlyMemory<byte> x, ReadOnlyMemory<byte> y)
            {
                return x.Span.SequenceEqual(y.Span);
            }

            public int GetHashCode(ReadOnlyMemory<byte> obj)
            {
                var hash = new HashCode();
                hash.AddBytes(obj.Span);
                return hash.ToHashCode();
            }
        }
    }
}
