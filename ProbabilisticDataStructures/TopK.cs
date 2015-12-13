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
        private uint n { get; set; }
        //private IntervalHeap<Element> elements { get; set; }
        private ElementHeap elements { get; set; }

        /// <summary>
        /// Creates a new TopK backed by a Count-Min sketch whose relative accuracy is
        /// within a factor of epsilon with probability delta. It tracks the k-most
        /// frequent elements.
        /// </summary>
        /// <param name="epsilon"></param>
        /// <param name="delta"></param>
        /// <param name="k"></param>
        /// <returns></returns>
        public static TopK NewTopK(double epsilon, double delta, uint k)
        {
            return new TopK
            {
                cms = new CountMinSketch(epsilon, delta),
                k = k,
                elements = new ElementHeap((int)k)
            };
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

        public Element[] Elements()
        {
            if (this.elements.Len() == 0)
            {
                return new Element[0];
            }

            return this.elements.elementHeap.OrderBy(x => x.Freq).ThenBy(x => x.added).ToArray();

            //var elements = this.elements;
            //var topK = new List<Element>((int)this.k);

            //if (elements.Len() > 0)
            //{
            //    //return topK.OrderBy(x => x.Freq).ToArray();
            //    //foreach (var element in elements.elementHeap)
            //    //{
            //    //    topK.Add(elements.Pop());
            //    //}
            //}

            //return topK.ToArray();
        }

        public TopK Reset()
        {
            this.cms.Reset();
            this.elements = new ElementHeap((int)k);
            this.n = 0;
            return this;
        }

        private bool isTop(UInt64 freq)
        {
            if (this.elements.Len() < this.k)
            {
                return true;
            }

            return freq >= this.Elements()[0].Freq;
        }

        private void insert(byte[] data, UInt64 freq)
        {
            var elements = this.Elements();
            for (int i = 0; i < elements.Count(); i++)
            {
                var element = elements[i];
                if (Enumerable.SequenceEqual(data, element.Data))
                {
                    // Element alread in top-k.
                    elements[i].Freq = freq;
                    this.elements.elementHeap = elements.ToList();
                    return;
                    this.elements.elementHeap[i].Freq = freq;
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
                added = DateTime.UtcNow.Ticks
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
                return this.elementHeap[i].Freq < elementHeap[j].Freq;
            }

            internal void Swap(int i, int j)
            {
                var temp = this.elementHeap[i];
                elementHeap[i] = elementHeap[j];
                elementHeap[j] = temp;
            }

            internal void Push(Element x)
            {
                this.elementHeap.Add(x);
            }

            internal Element Pop()
            {
                var elementToRemove = this.elementHeap
                    .OrderBy(x => x.Freq)
                    .ThenBy(x => x.added)
                    .First();
                this.elementHeap.Remove(elementToRemove);
                return elementToRemove;
                //var n = this.elementHeap.Count - 1;
                //var x = this.elementHeap[n];
                //this.elementHeap = this.elementHeap.Take(n).ToList();
                //return x;
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
            internal long added { get; set; }
        }
    }
}
