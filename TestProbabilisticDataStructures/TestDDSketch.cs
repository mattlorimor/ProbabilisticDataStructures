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

            // Counts that wrap past 2^64 and so add up to a total the payload claims. Each
            // of these passed the check above, because the sum it compares was taken
            // modulo 2^64, and left a sketch that disagreed with itself: a store whose
            // counts wrapped to zero reports itself empty while its bounds and counts are
            // still set. Each is refused for what it is, by a phrase "do not add up" is
            // not, so that this cannot be credited to the check above catching it.
            var shape = TwoBucketsEachSideAndAZero();
            const ulong half = 1UL << 63;
            const ulong quarter = 1UL << 62;
            const ulong eighth = 1UL << 61;

            // One sign's own counts wrap, to a store total of zero. The claimed total is
            // what the other sign and the zero count come to, so the sum of the three is
            // exactly what the payload says and only the store's own count can refuse it.
            AssertRefused(
                Forged(shape, 3, 1, new[] { half, half }, new ulong[] { 1, 1 }),
                "positive buckets hold more than");
            AssertRefused(
                Forged(shape, 3, 1, new ulong[] { 1, 1 }, new[] { half, half }),
                "negative buckets hold more than");

            // Neither store wraps and the sum of the three does. The first addition wraps
            // (positive and negative are 2^63 each) and the zero count lands on the wrapped
            // value, which is the total the payload claims.
            AssertRefused(
                Forged(shape, 5, 5, new[] { quarter, quarter }, new[] { quarter, quarter }),
                "values between them");

            // The first addition does not wrap and the second does, so that checking only
            // the two stores against each other would let it through:
            // 2^63 + 2^62 + 2^62 = 2^64.
            AssertRefused(
                Forged(shape, 0, quarter, new[] { quarter, quarter }, new[] { eighth, eighth }),
                "values between them");

            // The wrap is between the negative store and the zero count, 2^63 + 2^63, with
            // the positive store's two left over as the claimed total. A sum that added
            // those two in 64 bits before adding the positive store would let it through.
            AssertRefused(
                Forged(shape, 2, half, new ulong[] { 1, 1 }, new[] { quarter, quarter }),
                "values between them");
        }

        /// <summary>
        /// The most a count holds, 2^64 - 1, is not too many. An honest sketch has a total
        /// of at most that, so the refusal of totals that wrap must stop one short of
        /// refusing a full one. Each case is the edge of one guard: a store whose counts
        /// come to exactly that, and a sum of the three that does with the last addition
        /// being a store and then the zero count.
        /// </summary>
        [TestMethod]
        public void TestATotalOfExactlyTheLargestCountIsRead()
        {
            var shape = TwoBucketsEachSideAndAZero();
            const ulong half = 1UL << 63;
            const ulong quarter = 1UL << 62;
            const ulong eighth = 1UL << 61;

            var cases = new (string Name, byte[] Payload)[]
            {
                ("one store", Forged(
                    shape, ulong.MaxValue, 0, new[] { half, half - 1 }, new ulong[] { 0, 0 })),

                ("two stores", Forged(
                    shape, ulong.MaxValue, 0,
                    new[] { quarter, quarter }, new[] { quarter, quarter - 1 })),

                ("the zero count", Forged(
                    shape, ulong.MaxValue, quarter - 1,
                    new[] { quarter, quarter }, new[] { eighth, eighth })),
            };

            foreach (var (name, payload) in cases)
            {
                var sketch = Persistence.FromByteArray<DDSketch>(payload);
                Assert.AreEqual(ulong.MaxValue, sketch.Count(), name);
            }
        }

        /// <summary>
        /// Merging two sketches whose counts together pass 2^64 - 1 is refused and leaves
        /// the sketch as it was, where the sum used to wrap: the total went round to a
        /// small number and the quantiles with it. An honest sketch cannot get here, since
        /// it would take 2^64 adds; a payload can, since a count is only a number in it.
        /// </summary>
        [TestMethod]
        public void TestMergingCountsThatWouldPassTheLargestCountChangesNothing()
        {
            const ulong half = 1UL << 63;

            // Which side each sketch's weight is on. When they differ, every store's own
            // total and every bucket is well within a count, and only the total is not, so
            // a check of the stores or the buckets alone would pass them.
            var cases = new (string Name, Func<ulong, DDSketch> Into, Func<ulong, DDSketch> From)[]
            {
                ("positive into positive", HeavyPositive, HeavyPositive),
                ("negative into positive", HeavyPositive, HeavyNegative),
                ("zero into negative", HeavyNegative, HeavyZeros),
                ("positive into zero", HeavyZeros, HeavyPositive),
            };

            foreach (var (name, into, from) in cases)
            {
                var sketch = into(half);
                var other = from(half);
                var before = sketch.ToByteArray();
                var otherBefore = other.ToByteArray();

                var ex = Assert.ThrowsExactly<ArgumentException>(() => sketch.Merge(other), name);
                StringAssert.Contains(ex.Message, "a sketch can count", name);

                CollectionAssert.AreEqual(before, sketch.ToByteArray(),
                    $"{name}: a merge that was refused changed the sketch it was merging into");
                CollectionAssert.AreEqual(otherBefore, other.ToByteArray(),
                    $"{name}: a merge that was refused changed the sketch it was merging from");

                // The same pair one short is the most that merges, so the refusal is not
                // turning away a full sketch.
                var merged = into(half).Merge(from(half - 1));
                Assert.AreEqual(ulong.MaxValue, merged.Count(), name);

                // And a full sketch takes no more, however small the one that brings it.
                var one = SingleValue(5);
                var full = merged.ToByteArray();
                var ex2 = Assert.ThrowsExactly<ArgumentException>(() => merged.Merge(one), name);
                StringAssert.Contains(ex2.Message, "a sketch can count", name);
                CollectionAssert.AreEqual(full, merged.ToByteArray(),
                    $"{name}: a refused merge into a full sketch changed it");
            }
        }

        /// <summary>
        /// A sketch of <c>1, 2, -1, -2</c> and <c>0</c>, at an accuracy coarse enough
        /// (gamma is 3) that 1 and 2 are in neighbouring buckets: two buckets for each
        /// sign and a zero, for <see cref="Forged"/> to rewrite the counts of.
        /// </summary>
        private static DDSketch TwoBucketsEachSideAndAZero()
        {
            var sketch = new DDSketch(0.5);
            foreach (var value in new double[] { 1, 2, -1, -2, 0 })
            {
                sketch.Add(value);
            }

            return sketch;
        }

        private static DDSketch SingleValue(double value)
        {
            var sketch = new DDSketch(0.5);
            sketch.Add(value);
            return sketch;
        }

        // A sketch holding that many of one value: one bucket on one sign, or the zero
        // count. Made by rewriting the counts of one that holds a single value, which is
        // the only way to a count this large without adding that many times.
        private static DDSketch HeavyPositive(ulong count) =>
            Persistence.FromByteArray<DDSketch>(Forged(
                SingleValue(5), count, 0, new[] { count }, Array.Empty<ulong>()));

        private static DDSketch HeavyNegative(ulong count) =>
            Persistence.FromByteArray<DDSketch>(Forged(
                SingleValue(-5), count, 0, Array.Empty<ulong>(), new[] { count }));

        private static DDSketch HeavyZeros(ulong count) =>
            Persistence.FromByteArray<DDSketch>(Forged(
                SingleValue(0), count, count, Array.Empty<ulong>(), Array.Empty<ulong>()));

        /// <summary>
        /// The payload of <paramref name="shape"/> with the recorded total, the zero count
        /// and the counts of each store's buckets replaced, and the checksum repaired. The
        /// shape fixes everything else: where each store starts, how many buckets it has
        /// and the smallest and largest value.
        /// </summary>
        private static byte[] Forged(
            DDSketch shape, ulong count, ulong zeroCount, ulong[] positive, ulong[] negative)
        {
            // Offsets from FORMAT.md: after the envelope's 14 bytes, an f64 accuracy, then
            // the total and the zero count, then the smallest and largest, then the stores.
            const int countAt = 14 + 8;
            const int zeroCountAt = countAt + 8;
            const int storesAt = zeroCountAt + 8 + 8 + 8;

            var bytes = shape.ToByteArray();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                bytes.AsSpan(countAt), count);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                bytes.AsSpan(zeroCountAt), zeroCount);

            // Each store is its first index, then its counts as a length-prefixed run.
            var at = storesAt;
            foreach (var counts in new[] { positive, negative })
            {
                var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(at + 4));
                Assert.AreEqual(counts.Length * sizeof(ulong), length,
                    "the shape's store is not as many buckets as the counts given for it");

                for (var i = 0; i < counts.Length; i++)
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                        bytes.AsSpan(at + 8 + (i * sizeof(ulong))), counts[i]);
                }

                at += 8 + length;
            }

            RepairChecksum(bytes);
            return bytes;
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


        // ---- A bucket index is a 32-bit integer ---------------------------------------

        /// <summary>
        /// The log of the bucket ratio, as the sketch works it out. Restated here from
        /// the paper's gamma = (1+a)/(1-a) rather than read back from the sketch.
        /// </summary>
        private static double LogGammaOf(double accuracy)
        {
            return Math.Log((1 + accuracy) / (1 - accuracy));
        }

        /// <summary>
        /// A value whose bucket index an int cannot hold is refused, where it used to
        /// be filed in the last bucket an int has. At a relative accuracy of 1e-10 an
        /// int counts 2^31 buckets of 2e-10 each, so only values from about 0.65 to
        /// 1.54 have a bucket: {1000, 2000, 1e6} all landed in the one at int.MaxValue
        /// and every quantile of them answered 1.536.
        /// </summary>
        [TestMethod]
        public void TestAValueTheBucketIndexCannotHoldIsRefusedNotSaturated()
        {
            // On an empty sketch, where it used to be accepted without a word.
            var values = new[] { 1000.0, 2000.0, 1e6, -1000.0, 1e-3, -1e-3 };
            foreach (var value in values)
            {
                var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => new DDSketch(1e-10).Add(value),
                    $"{value} has no bucket at an accuracy of 1e-10");
                Assert.AreEqual("value", ex.ParamName);
            }

            // And on one that holds something: used to fail in the allocation instead.
            var sketch = new DDSketch(1e-10);
            sketch.Add(1.0);
            foreach (var value in values)
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => sketch.Add(value));
            }

            // Refusing changed nothing: the one value that was added is still all there is.
            Assert.AreEqual(1UL, sketch.Count());
            Assert.AreEqual(1.0, sketch.Min());
            Assert.AreEqual(1.0, sketch.Max());
            Assert.AreEqual(1.0, sketch.Quantile(0.5), 1e-9);
        }

        /// <summary>
        /// The range ends exactly where an int does. The values below sit half a bucket
        /// inside and half a bucket outside each end, so a limit that is a bucket early
        /// or late, or on the wrong side of a sign, is on the wrong side of one of them.
        /// </summary>
        [TestMethod]
        public void TestTheIndexRangeEndsWhereAnIntEnds()
        {
            const double accuracy = 1e-9;
            var logGamma = LogGammaOf(accuracy);

            // ln(v) / ln(gamma) is the index before it is rounded up, so the exponent is
            // that quotient: -0.5 past an end rounds to the end's own bucket, and 0.5 past
            // it to the first bucket an int does not have.
            var cases = new (double Exponent, bool Held)[]
            {
                (int.MaxValue - 0.5, true),
                (int.MaxValue + 0.5, false),
                (int.MinValue - 0.5, true),
                (int.MinValue - 1.5, false),
            };

            foreach (var (exponent, held) in cases)
            {
                var value = Math.Exp(exponent * logGamma);
                var sketch = new DDSketch(accuracy);

                if (held)
                {
                    sketch.Add(value);
                    Assert.AreEqual(1UL, sketch.Count());
                    Assert.AreEqual(value, sketch.Quantile(0.5), value * 2 * accuracy,
                        $"a value in the bucket at index {Math.Ceiling(exponent)} came back wrong");
                }
                else
                {
                    Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                        () => sketch.Add(value),
                        $"a value at exponent {exponent} is past the end of the indices");
                    Assert.AreEqual(0UL, sketch.Count());
                }
            }
        }

        /// <summary>
        /// Every positive double has a bucket once the accuracy is coarse enough that an
        /// int reaches the smallest of them: 744.44 / ln(gamma) buckets below one, so
        /// ln(gamma) of 3.47e-7 and an accuracy of about 1.74e-7. Each extreme goes in a
        /// sketch of its own, since one store holding both is an array of billions. Below
        /// the line the smallest is refused while the largest, 709.78 / ln(gamma), still
        /// has a bucket: the line is drawn by the index, not by the accuracy alone.
        /// </summary>
        [TestMethod]
        public void TestEveryDoubleHasABucketFromAboutOneAndAHalfTenMillionth()
        {
            foreach (var accuracy in new[] { 1.8e-7, 1e-6, 1e-3, 0.5 })
            {
                foreach (var value in new[]
                    { double.Epsilon, double.MaxValue, -double.Epsilon, -double.MaxValue })
                {
                    var sketch = new DDSketch(accuracy);
                    sketch.Add(value);
                    Assert.AreEqual(1UL, sketch.Count(), $"{value} at an accuracy of {accuracy}");
                    Assert.AreEqual(value, sketch.Min());
                }
            }

            var tooFine = new DDSketch(1.7e-7);
            foreach (var value in new[] { double.Epsilon, -double.Epsilon })
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => tooFine.Add(value));
            }

            tooFine.Add(double.MaxValue);
            tooFine.Add(-double.MaxValue);
            Assert.AreEqual(2UL, tooFine.Count());
        }

        /// <summary>
        /// A range of buckets no array could hold is refused, where it used to fail inside
        /// the allocation of the store with an <see cref="OverflowException"/>. Indices
        /// at both ends of the int range are 2^32 buckets apart, and an array holds fewer
        /// than 2^31. The sketch is left as it was.
        /// </summary>
        [TestMethod]
        public void TestARangeNoArrayCouldHoldIsRefusedNotOverflowed()
        {
            const double accuracy = 1e-9;
            var logGamma = LogGammaOf(accuracy);
            var top = Math.Exp((int.MaxValue - 0.5) * logGamma);
            var bottom = Math.Exp((int.MinValue - 0.5) * logGamma);

            var sketch = new DDSketch(accuracy);
            sketch.Add(top);

            // Index 0 is 2^31 buckets below it, which is one more than an array holds
            // with room to spare.
            var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => sketch.Add(1.0));
            Assert.AreEqual("value", ex.ParamName);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => sketch.Add(bottom));
            Assert.AreEqual(1UL, sketch.Count());
            Assert.AreEqual(top, sketch.Quantile(0.5), top * 2 * accuracy);

            // The same from the other side, and for the negative store.
            var fromBelow = new DDSketch(accuracy);
            fromBelow.Add(bottom);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fromBelow.Add(top));
            var negative = new DDSketch(accuracy);
            negative.Add(-top);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => negative.Add(-1.0));
        }

        /// <summary>
        /// The store's limit is the largest array, to the bucket. A run of exactly that
        /// many buckets is a 16 GiB allocation, so the accepting side is held to the
        /// predicate the store asks, and the refusing side is run for real: two values
        /// whose buckets are one more than that apart are refused with the first already
        /// in, at a cost of a 64-slot array.
        /// </summary>
        [TestMethod]
        public void TestARunOfBucketsFitsUpToTheLargestArray()
        {
            Assert.IsTrue(DDSketch.SpanFits(Array.MaxLength));
            Assert.IsFalse(DDSketch.SpanFits(Array.MaxLength + 1L));
            Assert.IsTrue(DDSketch.SpanFits(1));

            const double accuracy = 1e-9;
            var logGamma = LogGammaOf(accuracy);
            const long lowIndex = -1_073_741_800;

            // Indices lowIndex and lowIndex + MaxLength: a run of MaxLength + 1 buckets.
            var sketch = new DDSketch(accuracy);
            sketch.Add(Math.Exp((lowIndex - 0.5) * logGamma));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => sketch.Add(Math.Exp((lowIndex + Array.MaxLength - 0.5) * logGamma)));
            Assert.AreEqual(1UL, sketch.Count());
        }

        /// <summary>
        /// Merging two sketches whose buckets are too far apart for one array is refused
        /// before either is changed, rather than after one store has taken the merge and
        /// the other has overflowed.
        /// </summary>
        [TestMethod]
        public void TestMergingBucketsNoArrayCouldHoldChangesNothing()
        {
            const double accuracy = 1e-9;
            var top = Math.Exp((int.MaxValue - 0.5) * LogGammaOf(accuracy));

            // One store at a time is too far apart, with the other store compatible, so
            // each store's check is the only thing standing between the merge and the
            // overflow. Then both at once.
            var cases = new (double[] Into, double[] From)[]
            {
                (new[] { top }, new[] { 1.0 }),
                (new[] { -top }, new[] { -1.0 }),
                (new[] { top, -1.0 }, new[] { 1.0, -top }),

                // The positive stores merge without trouble and the negative ones do not:
                // the case where checking one store at a time leaves the first changed.
                (new[] { 5.0, -top }, new[] { 6.0, -1.0 }),
            };

            foreach (var (into, from) in cases)
            {
                var high = new DDSketch(accuracy);
                foreach (var value in into) high.Add(value);
                var low = new DDSketch(accuracy);
                foreach (var value in from) low.Add(value);

                var before = high.ToByteArray();
                Assert.ThrowsExactly<ArgumentException>(() => high.Merge(low),
                    $"{string.Join(", ", into)} merged with {string.Join(", ", from)}");
                CollectionAssert.AreEqual(before, high.ToByteArray(),
                    "a merge that was refused changed the sketch it was merging into");
            }
        }

        /// <summary>
        /// A sketch whose last bucket is the last index an int has can be merged. The
        /// merge walks the other store's indices, and a walk in ints that ends at
        /// int.MaxValue wraps to int.MinValue and never ends; the timeout is what turns
        /// that into a failure.
        /// </summary>
        [TestMethod]
        [Timeout(15000)]
        public void TestMergingTheLastBucketAnIntHasEnds()
        {
            const double accuracy = 1e-9;
            var top = Math.Exp((int.MaxValue - 0.5) * LogGammaOf(accuracy));
            var bottom = Math.Exp((int.MinValue - 0.5) * LogGammaOf(accuracy));

            foreach (var value in new[] { top, -top, bottom, -bottom })
            {
                var source = new DDSketch(accuracy);
                source.Add(value);
                source.Add(value);

                var merged = new DDSketch(accuracy).Merge(source);

                CollectionAssert.AreEqual(source.ToByteArray(), merged.ToByteArray(),
                    $"merging {value} into an empty sketch did not give the sketch it came from");
                Assert.AreEqual(2UL, merged.Count());
            }
        }

        /// <summary>
        /// An accuracy so fine that (1+a)/(1-a) is exactly 1 has no bucket ratio: every
        /// value is infinitely many buckets from the next, and the index of any value but
        /// one is infinity. It cannot work at all, so it is refused where it was built.
        /// The finest that can, 6e-17, has a ratio of one part in 4.5 quadrillion, and is
        /// still built.
        /// </summary>
        [TestMethod]
        public void TestAnAccuracyFinerThanAnyBucketRatioIsRefused()
        {
            foreach (var bad in new[] { 5.5e-17, 1e-20, 1e-300, double.Epsilon })
            {
                var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => new DDSketch(bad), $"an accuracy of {bad}");
                Assert.AreEqual("relativeAccuracy", ex.ParamName);
            }

            var finest = new DDSketch(6e-17);
            finest.Add(1.0);
            Assert.AreEqual(1.0, finest.Quantile(0.5), 1e-9);
        }

        /// <summary>
        /// The reader holds a payload to what the constructor builds, so an accuracy no
        /// sketch is built at is refused as a payload and not as an argument.
        /// </summary>
        [TestMethod]
        public void TestAPayloadWithAnAccuracyNoSketchIsBuiltAtIsRefused()
        {
            var sketch = new DDSketch(0.01);
            for (var i = 1; i <= 100; i++) sketch.Add(i);

            var bytes = sketch.ToByteArray();
            System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(
                bytes.AsSpan(14), 1e-20);
            RepairChecksum(bytes);

            AssertRefused(bytes, "relative accuracy");
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
