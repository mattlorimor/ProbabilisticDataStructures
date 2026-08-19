using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// DDSketch's contract, which is unlike anything else here in two ways: it takes
    /// numbers rather than bytes and never hashes them, and its guarantee is on the
    /// <b>value</b> it returns rather than on a rank or a probability.
    /// <para>
    /// That guarantee is what makes this testable exactly. Every other structure here
    /// needs statistical slack in its tests -- a false positive rate near some figure, a
    /// cardinality within some percentage. This one either holds or it does not: for
    /// every quantile of every stream, the returned value is within the relative
    /// accuracy of the true one. So the tests assert that, not an approximation of it.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestDDSketch
    {
        /// <summary>
        /// The value at a quantile, computed exactly by sorting.
        /// </summary>
        /// <remarks>
        /// Uses the same rank convention the sketch does -- rank q(n-1), and the element
        /// at the floor of it -- because a test that used a different one would be
        /// comparing against a neighbouring element and calling the difference error.
        /// </remarks>
        private static double ExactQuantile(double[] values, double q)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            var rank = q * (sorted.Length - 1);
            return sorted[(int)Math.Floor(rank)];
        }

        private static readonly double[] Quantiles =
            { 0, 0.01, 0.1, 0.25, 0.5, 0.75, 0.9, 0.95, 0.99, 0.999, 1 };

        /// <summary>
        /// The guarantee, over a distribution shaped like the thing this is for:
        /// latencies, which are lognormal and span orders of magnitude.
        /// </summary>
        [TestMethod]
        public void TestEveryQuantileIsWithinTheRelativeAccuracy()
        {
            foreach (var accuracy in new[] { 0.01, 0.001 })
            {
                var random = new Random(7);
                var values = Enumerable.Range(0, 100000)
                    .Select(_ => Math.Exp(random.NextDouble() * 12))
                    .ToArray();

                var sketch = new DDSketch(accuracy);
                foreach (var value in values)
                {
                    sketch.Add(value);
                }

                Assert.AreEqual((ulong)values.Length, sketch.Count());

                foreach (var q in Quantiles)
                {
                    var exact = ExactQuantile(values, q);
                    var estimate = sketch.Quantile(q);
                    var error = Math.Abs(estimate - exact) / Math.Abs(exact);

                    Assert.IsLessThanOrEqualTo(accuracy, error,
                        $"q={q} at accuracy {accuracy}: got {estimate}, exact {exact}, " +
                        $"relative error {error}");
                }
            }
        }

        /// <summary>
        /// A tighter accuracy has to actually be tighter, or the parameter is decorative.
        /// </summary>
        [TestMethod]
        public void TestATighterAccuracyGivesACloserAnswer()
        {
            var random = new Random(11);
            var values = Enumerable.Range(0, 50000)
                .Select(_ => Math.Exp(random.NextDouble() * 10)).ToArray();

            var loose = new DDSketch(0.05);
            var tight = new DDSketch(0.0005);

            foreach (var value in values)
            {
                loose.Add(value);
                tight.Add(value);
            }

            var looseWorst = Quantiles.Max(q =>
                Math.Abs(loose.Quantile(q) - ExactQuantile(values, q)) / ExactQuantile(values, q));
            var tightWorst = Quantiles.Max(q =>
                Math.Abs(tight.Quantile(q) - ExactQuantile(values, q)) / ExactQuantile(values, q));

            Assert.IsLessThan(looseWorst, tightWorst,
                $"the tighter sketch was no better: {tightWorst} against {looseWorst}");
        }

        /// <summary>
        /// A stream of one value is the smallest case where a quantile means anything,
        /// and every quantile of it is that value.
        /// </summary>
        [TestMethod]
        public void TestASingleValueIsEveryQuantile()
        {
            var sketch = new DDSketch(0.01);
            sketch.Add(42.0);

            foreach (var q in Quantiles)
            {
                Assert.IsLessThanOrEqualTo(0.01, Math.Abs(sketch.Quantile(q) - 42.0) / 42.0,
                    $"q={q} of a single 42 was not 42");
            }
        }

        /// <summary>
        /// There is no quantile of nothing. Returning zero or NaN would be a number a
        /// caller could plot without noticing it means "no data".
        /// </summary>
        [TestMethod]
        public void TestAnEmptySketchHasNoQuantiles()
        {
            var sketch = new DDSketch(0.01);

            Assert.AreEqual(0ul, sketch.Count());
            Assert.ThrowsExactly<InvalidOperationException>(() => sketch.Quantile(0.5));
        }

        /// <summary>
        /// The smallest and largest values are kept exactly rather than bucketed. They
        /// cost two doubles, they are asked for constantly, and a bucketed answer to
        /// "what was the worst latency" is a worse answer than the one already recorded.
        /// </summary>
        [TestMethod]
        public void TestTheExtremesAreExact()
        {
            var random = new Random(13);
            var values = Enumerable.Range(0, 10000)
                .Select(_ => random.NextDouble() * 1000).ToArray();

            var sketch = new DDSketch(0.01);
            foreach (var value in values)
            {
                sketch.Add(value);
            }

            Assert.AreEqual(values.Min(), sketch.Min());
            Assert.AreEqual(values.Max(), sketch.Max());
        }

        [TestMethod]
        public void TestQuantileRejectsAValueOutsideZeroToOne()
        {
            var sketch = new DDSketch(0.01);
            sketch.Add(1.0);

            foreach (var bad in new[] { -0.1, 1.1, 2.0, double.NaN })
            {
                var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => sketch.Quantile(bad), $"a quantile of {bad} should be refused");
                Assert.AreEqual("q", ex.ParamName);
            }
        }

        /// <summary>
        /// The relative accuracy sets the bucket ratio, which only makes sense strictly
        /// between zero and one.
        /// </summary>
        [TestMethod]
        public void TestTheSketchRejectsAnImpossibleAccuracy()
        {
            foreach (var bad in new[] { 0.0, 1.0, -0.5, 2.0, double.NaN })
            {
                var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => new DDSketch(bad));
                Assert.AreEqual("relativeAccuracy", ex.ParamName);
            }
        }

        /// <summary>
        /// A stream is numbers, and these are not numbers. Accepting them would put a
        /// sketch into a state where every later answer is NaN and nothing says why.
        /// </summary>
        [TestMethod]
        public void TestAddRejectsValuesThatAreNotNumbers()
        {
            var sketch = new DDSketch(0.01);

            foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => sketch.Add(bad));
            }

            Assert.AreEqual(0ul, sketch.Count(), "a refused value was still counted");
        }
        /// <summary>
        /// Negative values are not an error to refuse. A stream of temperatures or
        /// deltas is as real as a stream of latencies, and the guarantee has to hold
        /// across the sign: the relative error is on the magnitude, and the ordering has
        /// to put the most negative value first.
        /// </summary>
        [TestMethod]
        public void TestNegativeValuesAreHeldToTheSameGuarantee()
        {
            var random = new Random(17);
            var values = Enumerable.Range(0, 50000)
                .Select(_ => -Math.Exp(random.NextDouble() * 10)).ToArray();

            var sketch = new DDSketch(0.01);
            foreach (var value in values)
            {
                sketch.Add(value);
            }

            foreach (var q in Quantiles)
            {
                var exact = ExactQuantile(values, q);
                var error = Math.Abs(sketch.Quantile(q) - exact) / Math.Abs(exact);
                Assert.IsLessThanOrEqualTo(0.01, error,
                    $"q={q}: got {sketch.Quantile(q)}, exact {exact}");
            }
        }

        /// <summary>
        /// A stream crossing zero is where the ordering is easiest to get wrong: the
        /// negative buckets run backwards relative to the positive ones, and zero sits
        /// between them belonging to neither.
        /// </summary>
        [TestMethod]
        public void TestAStreamAcrossZeroIsOrderedCorrectly()
        {
            var random = new Random(19);
            var values = Enumerable.Range(0, 60000)
                .Select(i => i % 3 == 0 ? 0.0 : (random.NextDouble() - 0.5) * 2000)
                .ToArray();

            var sketch = new DDSketch(0.01);
            foreach (var value in values)
            {
                sketch.Add(value);
            }

            foreach (var q in Quantiles)
            {
                var exact = ExactQuantile(values, q);
                var estimate = sketch.Quantile(q);

                if (exact == 0)
                {
                    Assert.AreEqual(0.0, estimate, $"q={q} should be exactly zero");
                    continue;
                }

                Assert.AreEqual(Math.Sign(exact), Math.Sign(estimate),
                    $"q={q}: got {estimate} for an exact value of {exact}, wrong side of zero");

                var error = Math.Abs(estimate - exact) / Math.Abs(exact);
                Assert.IsLessThanOrEqualTo(0.01, error, $"q={q}: got {estimate}, exact {exact}");
            }

            // The quantiles have to be non-decreasing, which is the property a mishandled
            // sign flip breaks without necessarily breaking any single answer.
            var previous = double.NegativeInfinity;
            for (var q = 0.0; q <= 1.0; q += 0.001)
            {
                var value = sketch.Quantile(q);
                Assert.IsLessThanOrEqualTo(value, previous, $"quantiles went backwards at q={q}");
                previous = value;
            }
        }

        /// <summary>
        /// Merging is what makes a sketch useful across processes: each host sketches its
        /// own stream and the answers combine without anyone shipping raw values.
        /// <para>
        /// Asserted against the exact quantiles of both streams together, not against a
        /// sketch of them -- a merge that was consistently wrong in the same way as a
        /// direct sketch would pass that and fail this.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestMergeGivesTheSketchOfBothStreams()
        {
            var random = new Random(23);
            var first = Enumerable.Range(0, 30000)
                .Select(_ => Math.Exp(random.NextDouble() * 8)).ToArray();
            var second = Enumerable.Range(0, 20000)
                .Select(_ => -Math.Exp(random.NextDouble() * 6)).ToArray();

            var a = new DDSketch(0.01);
            foreach (var v in first) a.Add(v);

            var b = new DDSketch(0.01);
            foreach (var v in second) b.Add(v);

            a.Merge(b);

            var combined = first.Concat(second).ToArray();
            Assert.AreEqual((ulong)combined.Length, a.Count());
            Assert.AreEqual(combined.Min(), a.Min());
            Assert.AreEqual(combined.Max(), a.Max());

            foreach (var q in Quantiles)
            {
                var exact = ExactQuantile(combined, q);
                var error = Math.Abs(a.Quantile(q) - exact) / Math.Abs(exact);
                Assert.IsLessThanOrEqualTo(0.01, error,
                    $"q={q} after merge: got {a.Quantile(q)}, exact {exact}");
            }

            // The sketch merged in is untouched, so it can be merged somewhere else too.
            Assert.AreEqual((ulong)second.Length, b.Count());
        }

        /// <summary>
        /// Merging into an empty sketch, and merging an empty one in, both have to work:
        /// they are what a fold over a collection of sketches starts and ends with.
        /// </summary>
        [TestMethod]
        public void TestMergingWithAnEmptySketchWorksEitherWay()
        {
            var values = Enumerable.Range(1, 1000).Select(i => (double)i).ToArray();

            var filled = new DDSketch(0.01);
            foreach (var v in values) filled.Add(v);

            var intoEmpty = new DDSketch(0.01).Merge(filled);
            Assert.AreEqual((ulong)values.Length, intoEmpty.Count());
            Assert.AreEqual(values.Min(), intoEmpty.Min());
            Assert.AreEqual(values.Max(), intoEmpty.Max());

            var emptyIn = filled.Merge(new DDSketch(0.01));
            Assert.AreEqual((ulong)values.Length, emptyIn.Count());

            foreach (var q in Quantiles)
            {
                var exact = ExactQuantile(values, q);
                Assert.IsLessThanOrEqualTo(0.01,
                    Math.Abs(intoEmpty.Quantile(q) - exact) / exact, $"q={q}");
            }
        }

        /// <summary>
        /// Two sketches with different accuracies bucket differently, so their counts
        /// describe different things and cannot be added.
        /// </summary>
        [TestMethod]
        public void TestMergeRejectsADifferentAccuracy()
        {
            var a = new DDSketch(0.01);
            var b = new DDSketch(0.02);

            var ex = Assert.ThrowsExactly<ArgumentException>(() => a.Merge(b));
            StringAssert.Contains(ex.Message, "relative accuracy must match");
        }

        [TestMethod]
        public void TestMergeRejectsNull()
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => new DDSketch(0.01).Merge(null!));
        }

        /// <summary>
        /// The buckets are contiguous across the range of indices seen, so memory goes
        /// with the <b>log</b> of the dynamic range rather than the range itself. That
        /// distinction is the whole reason this is safe to point at arbitrary data: a
        /// store proportional to the range would need 10^240 slots for the stream below.
        /// <para>
        /// Asserted as a ratio rather than against a fixed number of buckets. A fixed
        /// number says nothing about which of the two behaviours is happening, and has
        /// to be rewritten whenever the growth policy changes.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestMemoryGrowsWithTheLogOfTheDynamicRange()
        {
            static DDSketch OverExponents(int limit)
            {
                var sketch = new DDSketch(0.01);
                for (var exponent = -limit; exponent <= limit; exponent++)
                {
                    sketch.Add(Math.Pow(10, exponent));
                    sketch.Add(-Math.Pow(10, exponent));
                }

                return sketch;
            }

            var narrow = OverExponents(60);
            var wide = OverExponents(120);

            // Doubling the exponent range doubles the buckets. Anything proportional to
            // the range itself would be 10^60 times larger here, not twice.
            Assert.IsLessThanOrEqualTo(3 * narrow.BucketsAllocated(), wide.BucketsAllocated(),
                $"widening the range from 10^60 to 10^120 took {narrow.BucketsAllocated()} " +
                $"buckets to {wide.BucketsAllocated()}, which is not logarithmic growth");

            // And in absolute terms it stays small: 240 orders of magnitude, either
            // sign, inside a megabyte.
            Assert.IsLessThan(131072, wide.BucketsAllocated(),
                $"10^-120 to 10^120 took {wide.BucketsAllocated()} buckets");

            // The guarantee still holds at both ends of that range.
            Assert.IsLessThanOrEqualTo(0.01,
                Math.Abs(wide.Quantile(1) - 1e120) / 1e120, "the largest value came back wrong");
            Assert.IsLessThanOrEqualTo(0.01,
                Math.Abs(wide.Quantile(0) - -1e120) / 1e120, "the smallest value came back wrong");
        }

        [TestMethod]
        public void TestTheExtremesOfAnEmptySketchAreRefused()
        {
            var sketch = new DDSketch(0.01);

            Assert.ThrowsExactly<InvalidOperationException>(() => sketch.Min());
            Assert.ThrowsExactly<InvalidOperationException>(() => sketch.Max());
        }

        /// <summary>
        /// A restored sketch answers <b>identically</b>, not merely closely. The counts
        /// are exact integers and the buckets are exact ranges, so there is nothing in
        /// this structure that a round trip is entitled to approximate.
        /// </summary>
        [TestMethod]
        public void TestRoundTripsThroughPersistenceExactly()
        {
            var random = new Random(29);
            var sketch = new DDSketch(0.005);
            for (var i = 0; i < 50000; i++)
            {
                sketch.Add((random.NextDouble() - 0.3) * Math.Exp(random.NextDouble() * 9));
            }

            sketch.Add(0);

            var restored = Persistence.FromByteArray<DDSketch>(sketch.ToByteArray());

            Assert.AreEqual(sketch.Count(), restored.Count());
            Assert.AreEqual(sketch.Min(), restored.Min());
            Assert.AreEqual(sketch.Max(), restored.Max());
            Assert.AreEqual(sketch.RelativeAccuracy(), restored.RelativeAccuracy());

            for (var q = 0.0; q <= 1.0; q += 0.001)
            {
                Assert.AreEqual(sketch.Quantile(q), restored.Quantile(q),
                    $"the restored sketch differed at q={q}");
            }

            // And it keeps working, rather than merely reading back.
            sketch.Add(12345.0);
            restored.Add(12345.0);
            Assert.AreEqual(sketch.Quantile(0.99), restored.Quantile(0.99));
        }

        [TestMethod]
        public void TestAnEmptySketchRoundTrips()
        {
            var restored = Persistence.FromByteArray<DDSketch>(new DDSketch(0.01).ToByteArray());

            Assert.AreEqual(0ul, restored.Count());
            Assert.ThrowsExactly<InvalidOperationException>(() => restored.Quantile(0.5));

            restored.Add(5.0);
            Assert.AreEqual(5.0, restored.Min());
        }

        /// <summary>
        /// A merged sketch is the case where the two stores are most likely to disagree
        /// with the recorded totals, because merging is the only thing that writes into
        /// a store at an index it has never seen.
        /// </summary>
        [TestMethod]
        public void TestAMergedSketchRoundTrips()
        {
            var a = new DDSketch(0.01);
            var b = new DDSketch(0.01);

            for (var i = 1; i <= 5000; i++) a.Add(i);
            for (var i = 1; i <= 5000; i++) b.Add(-i);

            var restored = Persistence.FromByteArray<DDSketch>(a.Merge(b).ToByteArray());

            Assert.AreEqual(10000ul, restored.Count());
            for (var q = 0.0; q <= 1.0; q += 0.01)
            {
                Assert.AreEqual(a.Quantile(q), restored.Quantile(q), $"differed at q={q}");
            }
        }

        /// <summary>
        /// The sketch never hashes anything, so its payload records that rather than
        /// naming a hash it does not use. Reading it needs no hash argument, and the
        /// overload that takes one refuses rather than pretending to apply it.
        /// </summary>
        [TestMethod]
        public void TestThePayloadRecordsThatNoHashIsUsed()
        {
            var sketch = new DDSketch(0.01);
            sketch.Add(1.0);

            var bytes = sketch.ToByteArray();

            // Hash id 2, "none", in the envelope's hash field.
            Assert.AreEqual(2, bytes[8] | (bytes[9] << 8),
                "the payload does not record that the sketch uses no hash");

            Assert.IsNotNull(Persistence.FromByteArray<DDSketch>(bytes));

            Func<ReadOnlySpan<byte>, ulong> custom = d => 1UL;
            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<DDSketch>(bytes, custom));
            StringAssert.Contains(ex.Message, "does not hash");
        }

        /// <summary>
        /// A payload that does not describe a sketch this library builds is refused. The
        /// checksum is repaired after each edit, so a guard is what has to catch it.
        /// </summary>
        [TestMethod]
        public void TestAnInconsistentPayloadIsRefused()
        {
            var sketch = new DDSketch(0.01);
            for (var i = 1; i <= 100; i++) sketch.Add(i);
            var clean = sketch.ToByteArray();

            // An accuracy outside the range that defines a sketch at all.
            var badAccuracy = (byte[])clean.Clone();
            System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(
                badAccuracy.AsSpan(14), 1.5);
            RepairChecksum(badAccuracy);
            AssertRefused(badAccuracy, "relative accuracy");

            // A total that the buckets do not add up to, which would make every quantile
            // land at the wrong rank without anything looking obviously wrong.
            var badCount = (byte[])clean.Clone();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                badCount.AsSpan(14 + 8), 999999);
            RepairChecksum(badCount);
            AssertRefused(badCount, "do not add up");
        }

        private static void AssertRefused(byte[] payload, string expected)
        {
            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<DDSketch>(payload));
            StringAssert.Contains(ex.Message, expected);
        }

        private static void RepairChecksum(byte[] bytes)
        {
            var crc = new System.IO.Hashing.Crc32();
            crc.Append(bytes.AsSpan(4, bytes.Length - 8));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(bytes.Length - 4), crc.GetCurrentHashAsUInt32());
        }


        /// <summary>
        /// The bucket pipeline held to the paper's printed formulas -- Masson, Rim
        /// and Lee, "DDSketch" (VLDB 2019), section 2, verified against the paper
        /// 2026-08-18: gamma = (1+a)/(1-a), a value x lands in bucket
        /// i = ceil(log_gamma(x)) covering (gamma^(i-1), gamma^i], and the bucket
        /// answers 2*gamma^i/(gamma+1). At a = 0.5 every one of those is exact in
        /// floating point (gamma is exactly 3), so the expected answers are literals
        /// and the assertions are equality.
        /// <para>
        /// No behavioral test can pin this: the mapping and its inverse share any
        /// offset, so a sketch that buckets by floor, or shifts every index by one
        /// and reports the bucket below, still answers within its accuracy bound --
        /// self-consistently wrong the same way on both sides. Only evaluating the
        /// printed formulas at hand-checked points notices. The rows at 8, 9 and
        /// 9.0001 straddle the gamma^2 = 9 boundary: the interval is right-closed,
        /// so 9 answers with bucket 2 and 9.0001 with bucket 3, and a mapping that
        /// flips ceil to floor moves 10 from 13.5 to 4.5 while leaving 9 alone.
        /// </para>
        /// </summary>
        [TestMethod]
        [DataRow(0.5, 0.5)]
        [DataRow(2.0, 1.5)]
        [DataRow(8.0, 4.5)]
        [DataRow(9.0, 4.5)]
        [DataRow(9.0001, 13.5)]
        [DataRow(10.0, 13.5)]
        [DataRow(27.0, 13.5)]
        [DataRow(100.0, 121.5)]
        [DataRow(-10.0, -13.5)]
        public void TestBucketsAnswerThePapersMidpoint(double value, double expected)
        {
            var sketch = new DDSketch(0.5);
            sketch.Add(value);

            Assert.AreEqual(expected, sketch.Quantile(0.5),
                $"a lone {value} lives in bucket ceil(log3(|{value}|)) and every " +
                $"quantile of it must answer the bucket's 2*3^i/4 = {expected}, " +
                "mirrored for negatives, exactly.");
        }

        /// <summary>
        /// The quantile walk in bucket order, at the same hand-checked points: 2, 10
        /// and 100 land in buckets 1, 3 and 5, so the median must be bucket 3's
        /// 13.5 -- not an interpolation, not a neighbor. A walk that misorders
        /// buckets or miscounts ranks lands on 1.5 or 121.5, both far outside any
        /// tolerance this could have hidden behind.
        /// </summary>
        [TestMethod]
        public void TestTheQuantileWalkAnswersTheMiddleBucket()
        {
            var sketch = new DDSketch(0.5);
            sketch.Add(2);
            sketch.Add(10);
            sketch.Add(100);

            Assert.AreEqual(13.5, sketch.Quantile(0.5),
                "the median of one value each in buckets 1, 3 and 5 must be bucket " +
                "3's midpoint.");
        }
    }
}
