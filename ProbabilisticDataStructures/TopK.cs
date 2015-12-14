using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// TopK uses a Count-Min Sketch to calculate the top-K frequent elements in a
    /// stream.
    /// </summary>
    public class TopK
    {
        private CountMinSketch cms { get; set; }
        private uint k { get; set; }
        internal uint n { get; set; }
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
            this.cms = new CountMinSketch(epsilon, delta);
            this.k = k;
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
            this.cms.Add(data);
            this.n++;

            var freq = this.cms.Count(data);
            if (this.isTop(freq))
            {
                this.insert(data, freq);
            }

            return this;
        }

        /// <summary>
        /// Returns the top-k elements from lowest to highest frequency.
        /// </summary>
        /// <returns>The top-k elements from lowest to highest frequency</returns>
        public Element[] Elements()
        {
            if (this.elements.Len() == 0)
            {
                return new Element[0];
            }

            return this.elements.elementHeap
                .OrderBy(x => x.Freq)
                .ToArray();
        }

        /// <summary>
        /// Restores the TopK to its original state. It returns itself to allow for
        /// chaining.
        /// </summary>
        /// <returns>The TopK</returns>
        public TopK Reset()
        {
            this.cms.Reset();
            this.elements = new ElementHeap((int)k);
            this.n = 0;
            return this;
        }

        /// <summary>
        /// Indicates if the given frequency falls within the top-k heap.
        /// </summary>
        /// <param name="freq">The frequency to check</param>
        /// <returns>Whether or not the frequency falls within the top-k heap</returns>
        private bool isTop(UInt64 freq)
        {
            if (this.elements.Len() < this.k)
            {
                return true;
            }

            return freq >= this.elements.elementHeap[0].Freq;
        }

        /// <summary>
        /// Adds the data to the top-k heap. If the data is already an element, the
        /// frequency is updated. If the heap already has k elements, the element with
        /// the minimum frequency is removed.
        /// </summary>
        /// <param name="data">The data to insert</param>
        /// <param name="freq">The frequency to associate with the data</param>
        private void insert(byte[] data, UInt64 freq)
        {
            for (int i = 0; i < this.elements.elementHeap.Count; i++)
            {
                var element = this.elements.elementHeap[i];
                if (Enumerable.SequenceEqual(data, element.Data))
                {
                    // Element already in top-k.
                    element.Freq = freq;
                    return;
                }
            }

            if (this.elements.Len() == this.k)
            {
                // Remove minimum-frequency element.
                this.elements.Pop();
            }

            // Add element to top-k.
            this.elements.Push(new Element
            {
                Data = data,
                Freq = freq,
            });
        }

        internal class ElementHeap
        {
            internal List<Element> elementHeap { get; set; }

            internal int Len()
            {
                return this.elementHeap.Count;
            }

            internal bool Less(int i, int j)
            {
                return this.elementHeap[i].Freq < this.elementHeap[j].Freq;
            }

            internal void Swap(int i, int j)
            {
                var temp = this.elementHeap[i];
                elementHeap[i] = elementHeap[j];
                elementHeap[j] = temp;
            }

            internal void Push(Element e)
            {
                this.elementHeap.Add(e);
                this.up(this.Len() - 1);
            }

            internal Element Pop()
            {
                var elementToRemove = this.elementHeap[0];
                this.elementHeap.Remove(elementToRemove);
                return elementToRemove;
            }

            internal void up(int j)
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

            internal void down(int i, int n)
            {
                while (true)
                {
                    var j1 = 2 * i + 1;
                    if (j1 >= n || j1 < 0)
                    {
                        // j1 < - after int overflow
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

            internal ElementHeap(int k)
            {
                this.elementHeap = new List<Element>(k);
            }
        }

        public class Element
        {
            public byte[] Data { get; set; }
            public UInt64 Freq { get; set; }
        }
    }
}
