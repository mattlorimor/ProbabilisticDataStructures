using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The guarantee a frequency sketch actually makes, as opposed to the mechanics
    /// of adding and counting.
    /// <para>
    /// Count-Min's contract is two-sided: it never reports less than the truth, and
    /// with probability at least 1 - delta it reports no more than the truth plus
    /// epsilon times the total count. Only the first half was covered, and that half
    /// is the trivial one -- a sketch that returned ulong.MaxValue for every query
    /// passed it. Nothing measured the ceiling, which is the half the sizing math
    /// exists to deliver.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestSketchBounds
    {
        /// <summary>Overestimate allowed, as a fraction of the total count.</summary>
        private const double Epsilon = 0.001;

        /// <summary>
        /// Probability of exceeding that overestimate. This is a failure probability:
        /// a smaller delta is a deeper sketch.
        /// </summary>
        private const double Delta = 0.01;

        private const int DistinctItems = 20000;

        private static byte[] Key(int i) => Encoding.UTF8.GetBytes($"item-{i}");

        /// <summary>
        /// A skewed stream: a few items appear often, most appear once. Frequency
        /// estimation is uninteresting on a uniform stream, and the paper's bound is
        /// stated against the total count, which a skewed stream concentrates.
        /// </summary>
        private static Dictionary<int, ulong> BuildStream(CountMinSketch cms)
        {
            var truth = new Dictionary<int, ulong>(DistinctItems);
            for (int i = 0; i < DistinctItems; i++)
            {
                var times = (ulong)Math.Max(1, 1000 / (i + 1));
                var key = Key(i);
                for (ulong t = 0; t < times; t++)
                {
                    cms.Add(key);
                }
                truth[i] = times;
            }
            return truth;
        }

        [TestMethod]
        public void TestCountMinSketchStaysWithinItsErrorBound()
        {
            var cms = new CountMinSketch(Epsilon, Delta);
            var truth = BuildStream(cms);

            var total = cms.TotalCount();
            var allowed = Epsilon * total;

            var violations = 0;
            var overcounted = 0;
            var overcountSum = 0.0;

            foreach (var (i, times) in truth)
            {
                var estimate = cms.Count(Key(i));

                Assert.IsGreaterThanOrEqualTo(times, estimate,
                    $"item-{i} was added {times} times but the sketch reported " +
                    $"{estimate}. A Count-Min Sketch must never undercount.");

                var overcount = estimate - times;
                overcountSum += overcount;
                if (overcount > 0) overcounted++;
                if (overcount > allowed) violations++;
            }

            var perRowMean = (double)total / cms.Width;
            var meanOvercount = overcountSum / truth.Count;
            var violationRate = (double)violations / truth.Count;

            Console.WriteLine($"width={cms.Width} depth={cms.Depth} total={total}");
            Console.WriteLine($"allowed (eps*N)={allowed:F2}  per-row mean (N/w)={perRowMean:F3}");
            Console.WriteLine($"mean overcount={meanOvercount:F3}  ratio to per-row mean={meanOvercount / perRowMean:F4}");
            Console.WriteLine($"overcounted={overcounted}/{truth.Count}  violations={violations} rate={violationRate:P4}");

            Assert.IsGreaterThan(0, overcounted,
                "No item was overcounted at all, so the error bound is satisfied " +
                "trivially and this test proves nothing about the sizing math.");

            Assert.IsLessThanOrEqualTo(Delta, violationRate,
                $"{violations} of {truth.Count} estimates exceeded the truth by more " +
                $"than epsilon*N ({allowed:F2}), a rate of {violationRate:P4} against " +
                $"a configured failure probability of {Delta:P2}.");
        }

        [TestMethod]
        public void TestCountMinSketchGeometryMatchesThePaper()
        {
            var cms = new CountMinSketch(Epsilon, Delta);

            Assert.AreEqual((uint)Math.Ceiling(Math.E / Epsilon), cms.Width,
                "Width must be ceil(e/epsilon). The epsilon*N bound follows from " +
                "that width by Markov's inequality; any other width silently " +
                "changes the error the sketch delivers.");

            Assert.AreEqual((uint)Math.Ceiling(Math.Log(1 / Delta)), cms.Depth,
                "Depth must be ceil(ln(1/delta)). Each row is an independent chance " +
                "to miss the bound, so the depth is what makes delta true.");
        }

        /// <summary>
        /// TopK sits on a Count-Min Sketch, so it inherits that sketch's overestimate.
        /// The condition under which it still answers correctly is a gap: if the k-th
        /// and (k+1)-th frequencies differ by more than epsilon*N, no amount of
        /// overcounting inside the bound can reorder them.
        /// <para>
        /// The existing exactness test sizes the sketch so wide that the stream is
        /// counted exactly, which exercises the heap but never the sketch. Here the
        /// stream has ten times more distinct items than the sketch has columns, so
        /// collisions are forced by pigeonhole and the gap is doing the work.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestTopKRecoversTheTrueTopKWhenTheGapExceedsTheErrorBound()
        {
            const uint K = 10;
            const int TailItems = 30000;

            // Head frequencies descend by 100, well clear of epsilon*N below.
            var head = new Dictionary<string, int>();
            for (int i = 0; i < K; i++)
            {
                head[$"head-{i}"] = 2000 - (i * 100);
            }

            var stream = new List<string>();
            foreach (var (name, times) in head)
            {
                for (int t = 0; t < times; t++) stream.Add(name);
            }
            for (int i = 0; i < TailItems; i++)
            {
                stream.Add($"tail-{i}");
            }

            var rand = new Random(17);
            for (int i = stream.Count - 1; i > 0; i--)
            {
                int j = rand.Next(i + 1);
                (stream[i], stream[j]) = (stream[j], stream[i]);
            }

            var topK = new TopK(Epsilon, Delta, K);
            // Same geometry, same stream: used only to show the sketch really is
            // under collision pressure, so a pass here is not the exact case again.
            var witness = new CountMinSketch(Epsilon, Delta);
            foreach (var name in stream)
            {
                var key = Encoding.UTF8.GetBytes(name);
                topK.Add(key);
                witness.Add(key);
            }

            var total = stream.Count;
            var allowed = Epsilon * total;
            var minHeadGap = 100;

            var inflatedTail = 0;
            for (int i = 0; i < TailItems; i++)
            {
                if (witness.Count(Encoding.UTF8.GetBytes($"tail-{i}")) > 1) inflatedTail++;
            }

            Console.WriteLine($"N={total} allowed(eps*N)={allowed:F2} minHeadGap={minHeadGap}");
            Console.WriteLine($"distinct={head.Count + TailItems} width={witness.Width} depth={witness.Depth}");
            Console.WriteLine($"tail items overcounted by the witness sketch: {inflatedTail}/{TailItems}");

            Assert.IsGreaterThan(0, inflatedTail,
                "No tail item was overcounted, so the sketch counted this stream " +
                "exactly and the gap condition was never tested.");

            Assert.IsLessThan(minHeadGap, allowed,
                $"The test is only meaningful while epsilon*N ({allowed:F2}) stays " +
                $"below the gap between adjacent head frequencies ({minHeadGap}).");

            var got = topK.Elements()
                .Select(e => Encoding.UTF8.GetString(e.Data.Span))
                .ToHashSet();

            Assert.IsTrue(got.SetEquals(head.Keys.ToHashSet()),
                $"missing {string.Join(", ", head.Keys.Except(got))}, " +
                $"unexpected {string.Join(", ", got.Except(head.Keys))}. Every head " +
                $"item leads the tail by far more than epsilon*N ({allowed:F2}), so " +
                "the overestimate cannot account for a swap.");

            var freqs = topK.Elements().Select(e => e.Freq).ToArray();
            CollectionAssert.AreEqual(freqs.OrderBy(x => x).ToArray(), freqs,
                "Elements() must be ascending by frequency.");
        }
    }
}
