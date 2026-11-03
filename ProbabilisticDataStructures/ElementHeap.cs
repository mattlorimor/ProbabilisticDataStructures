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

        /// <summary>
        /// The most elements a heap can hold, and so the largest k that
        /// <see cref="TopK"/> and <see cref="HeavyKeeper"/> can track.
        /// </summary>
        /// <remarks>
        /// The heap is a list and its index a dictionary, both grown as elements arrive
        /// and never holding more than k. A list holds at most
        /// <see cref="Array.MaxLength"/>. A dictionary sizes its tables to primes, and the
        /// largest it will grow to is the largest prime an array can hold, which is this
        /// -- four short of the list's limit. Asked for more, either one fails with an
        /// allocation error rather than an explanation.
        /// <para>
        /// Both structures' constructors and both their readers refuse past this, so
        /// whatever either constructor builds, its reader loads. The readers used to
        /// stop at a million.
        /// </para>
        /// </remarks>
        internal const int MaxCapacity = 0x7FFFFFC3;

        /// <summary>
        /// An upper bound on what a heap sized for <paramref name="count"/> elements, and
        /// then given them, allocates beyond their data: the list's array, the index's
        /// bucket and entry tables, and an <see cref="Element"/> each.
        /// </summary>
        /// <remarks>
        /// A dictionary sizes its tables to the next prime it uses at or above the count,
        /// never more than about twice it; an entry is a hash, a link, a
        /// <see cref="ReadOnlyMemory{T}"/> key and an int value, which pad to 32 bytes.
        /// The data the elements hold are runs a reader copies, and charges, itself.
        /// </remarks>
        internal static long RestoreCost(ulong count)
        {
            var tableSize = count == 0 ? 0 : 2 * count + 7;
            return ReadBudget.Sum(
                ReadBudget.ArrayOf(count, sizeof(long)),
                ReadBudget.ArrayOf(tableSize, sizeof(int)),
                ReadBudget.ArrayOf(tableSize, 32),
                ReadBudget.Times(count, ElementSize),
                2 * ReadBudget.SmallObject);
        }

        /// <summary>
        /// What one <see cref="Element"/> occupies: an object header and method table, a
        /// <see cref="ReadOnlyMemory{T}"/> and a frequency.
        /// </summary>
        private const long ElementSize = 16 + 16 + sizeof(ulong);

        /// <summary>
        /// An empty heap, which grows as elements are pushed: what a structure is built and
        /// reset with.
        /// </summary>
        /// <remarks>
        /// k bounds the heap through the comparisons <see cref="insert"/> and
        /// <see cref="isTop"/> make, not through its capacity, so nothing is allocated for
        /// k until elements arrive. Both used to be sized to k when a structure was built,
        /// on every reset and for every merge, which at a large k was gigabytes before the
        /// first add.
        /// </remarks>
        internal ElementHeap()
            : this(0)
        {
        }

        /// <summary>
        /// A heap with room for <paramref name="capacity"/> elements before it grows, for a
        /// reader or a merge that knows how many it is about to push.
        /// </summary>
        internal ElementHeap(int capacity)
        {
            this.Heap = new List<Element>(capacity);
            this.positions = new Dictionary<ReadOnlyMemory<byte>, int>(capacity, ByteContentComparer.Instance);
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

        internal void insert(ReadOnlySpan<byte> data, UInt64 freq, uint k)
        {
            if (this.positions.GetAlternateLookup<ReadOnlySpan<byte>>()
                    .TryGetValue(data, out var index))
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
                Data = data.ToArray(),
                Freq = freq,
            });
        }

        /// <summary>
        /// Whether the element is currently tracked, and at what frequency. Reads
        /// through the same index every reordering keeps in step with the heap.
        /// </summary>
        internal bool TryGetFrequency(ReadOnlySpan<byte> data, out UInt64 freq)
        {
            if (this.positions.GetAlternateLookup<ReadOnlySpan<byte>>()
                    .TryGetValue(data, out var index))
            {
                freq = this.Heap[index].Freq;
                return true;
            }

            freq = 0;
            return false;
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
        private sealed class ByteContentComparer
            : IEqualityComparer<ReadOnlyMemory<byte>>,
              IAlternateEqualityComparer<ReadOnlySpan<byte>, ReadOnlyMemory<byte>>
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

            // The alternate half: lets the index be searched with a span, so an item
            // already in the heap can have its frequency raised without an array being
            // made just to ask the question. Create is called only when the dictionary
            // genuinely needs to store a key, which is the one place a copy is
            // unavoidable -- Elements() hands these arrays back to callers.
            public bool Equals(ReadOnlySpan<byte> alternate, ReadOnlyMemory<byte> other)
            {
                return alternate.SequenceEqual(other.Span);
            }

            public int GetHashCode(ReadOnlySpan<byte> alternate)
            {
                var hash = new HashCode();
                hash.AddBytes(alternate);
                return hash.ToHashCode();
            }

            public ReadOnlyMemory<byte> Create(ReadOnlySpan<byte> alternate)
            {
                return alternate.ToArray();
            }
        }
    }
}
