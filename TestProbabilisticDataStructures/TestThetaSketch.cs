using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    [TestClass]
    public class TestThetaSketch
    {
        private static byte[] Item(long i)
        {
            var key = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(key, i);
            return key;
        }

        [TestMethod]
        public void TestANewSketchIsEmpty()
        {
            var sketch = new ThetaSketch(4096);

            Assert.AreEqual(0ul, sketch.Count());
        }
        /// <summary>
        /// While the sketch is holding fewer values than it is allowed to, it has kept
        /// every distinct item it saw, so the count is exact rather than estimated.
        /// </summary>
        [TestMethod]
        public void TestCountsAreExactBelowTheRetainedSize()
        {
            var sketch = new ThetaSketch(4096);

            for (var i = 0; i < 1000; i++)
            {
                sketch.Add(Item(i));
            }

            Assert.AreEqual(1000ul, sketch.Count());
        }

        [TestMethod]
        public void TestRepeatedItemsAreCountedOnce()
        {
            var sketch = new ThetaSketch(4096);

            for (var round = 0; round < 10; round++)
            {
                for (var i = 0; i < 100; i++)
                {
                    sketch.Add(Item(i));
                }
            }

            Assert.AreEqual(100ul, sketch.Count());
        }

        /// <summary>
        /// Past the point where it can keep everything, the sketch keeps a bounded
        /// sample and estimates from it. Both halves matter: an estimate that stayed
        /// accurate by keeping every value would not be a sketch.
        /// </summary>
        [TestMethod]
        public void TestBeyondTheRetainedSizeItEstimatesWithinItsError()
        {
            const uint K = 4096;
            const long N = 1_000_000;

            var sketch = new ThetaSketch(K);
            for (long i = 0; i < N; i++)
            {
                sketch.Add(Item(i));
            }

            Assert.IsLessThanOrEqualTo(2 * K, sketch.Retained(),
                "the sketch kept more values than it is allowed to");

            // The standard error of a theta sketch is 1/sqrt(k), so three of them is a
            // bound that holds without being loose enough to hide a broken estimator.
            var error = Math.Abs((double)sketch.Count() - N) / N;
            Assert.IsLessThan(3.0 / Math.Sqrt(K), error,
                $"estimated {sketch.Count()} for {N}, a relative error of {error:P2}");
        }

        private static ThetaSketch Filled(uint k, long from, long count)
        {
            var sketch = new ThetaSketch(k);
            for (var i = from; i < from + count; i++)
            {
                sketch.Add(Item(i));
            }

            return sketch;
        }

        private static void AssertNear(long truth, ulong estimate, uint k, string what)
        {
            var error = Math.Abs((double)estimate - truth) / truth;
            Assert.IsLessThan(3.0 / Math.Sqrt(k), error,
                $"{what}: estimated {estimate} for {truth}, a relative error of {error:P2}");
        }

        [TestMethod]
        public void TestUnionCountsEverythingInEither()
        {
            const uint K = 4096;

            var a = Filled(K, 0, 100_000);
            var b = Filled(K, 50_000, 100_000);

            AssertNear(150_000, a.Union(b).Count(), K, "union of overlapping sets");

            // The operands are unchanged, so either can be combined again elsewhere.
            AssertNear(100_000, a.Count(), K, "left operand after union");
            AssertNear(100_000, b.Count(), K, "right operand after union");

            // Disjoint sets add up, and a set with itself does not.
            AssertNear(200_000, Filled(K, 0, 100_000).Union(Filled(K, 500_000, 100_000)).Count(),
                K, "union of disjoint sets");
            AssertNear(100_000, a.Union(a).Count(), K, "union of a set with itself");
        }

        /// <summary>
        /// The operation <see cref="HyperLogLog"/> cannot do at all.
        /// </summary>
        [TestMethod]
        public void TestIntersectionCountsWhatIsInBoth()
        {
            const uint K = 4096;

            var a = Filled(K, 0, 100_000);
            var b = Filled(K, 50_000, 100_000);

            AssertNear(50_000, a.Intersect(b).Count(), K, "intersection of halves");

            // Disjoint sets share nothing, and that has to come back as nothing rather
            // than as a small number.
            Assert.AreEqual(0ul, Filled(K, 0, 100_000).Intersect(Filled(K, 500_000, 100_000)).Count(),
                "disjoint sets were given a non-empty intersection");

            // A set intersected with itself is itself.
            AssertNear(100_000, a.Intersect(a).Count(), K, "intersection of a set with itself");

            // And it is symmetric.
            Assert.AreEqual(a.Intersect(b).Count(), b.Intersect(a).Count(),
                "intersection depended on which side it was called from");
        }

        /// <summary>
        /// The case the whole structure is for: a small intersection between large sets.
        /// <para>
        /// Two cardinality estimators can be talked into answering this by
        /// inclusion-exclusion -- |A| + |B| - |A union B| -- and the answer is worthless,
        /// because each of those three terms carries an error proportional to sets that
        /// are hundreds of times larger than the number being estimated. The errors do
        /// not cancel. A theta sketch estimates the intersection directly, at the
        /// sampling rate it already applies to everything else.
        /// </para>
        /// <para>
        /// Compared against <see cref="HyperLogLogPlus"/> rather than
        /// <see cref="HyperLogLog"/>, so the comparison is against the best cardinality
        /// estimator here rather than the weakest.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestASmallIntersectionBeatsInclusionExclusion()
        {
            const uint K = 16384;
            const long SetSize = 200_000;
            const long Shared = 500;

            double thetaError = 0;
            double inclusionExclusionError = 0;
            const int Trials = 3;

            for (var trial = 0; trial < Trials; trial++)
            {
                var baseA = trial * 10_000_000L;
                var baseB = baseA + SetSize - Shared;

                var thetaA = new ThetaSketch(K);
                var thetaB = new ThetaSketch(K);
                var hllA = new HyperLogLogPlus(14);
                var hllB = new HyperLogLogPlus(14);
                var hllUnion = new HyperLogLogPlus(14);

                for (var i = 0L; i < SetSize; i++)
                {
                    var a = Item(baseA + i);
                    var b = Item(baseB + i);

                    thetaA.Add(a);
                    thetaB.Add(b);
                    hllA.Add(a);
                    hllB.Add(b);
                    hllUnion.Add(a);
                    hllUnion.Add(b);
                }

                var direct = (double)thetaA.Intersect(thetaB).Count();
                var indirect = (double)hllA.Count() + hllB.Count() - hllUnion.Count();

                thetaError += Math.Abs(direct - Shared);
                inclusionExclusionError += Math.Abs(indirect - Shared);
            }

            thetaError /= Trials;
            inclusionExclusionError /= Trials;

            Assert.IsLessThan(inclusionExclusionError / 5, thetaError,
                $"intersecting directly was off by {thetaError:F0} against " +
                $"inclusion-exclusion's {inclusionExclusionError:F0}, for a true " +
                $"intersection of {Shared}. The direct answer is supposed to be the " +
                "reason this structure exists.");

            // And the direct answer is actually usable, not merely better.
            Assert.IsLessThan(Shared, thetaError,
                $"the direct estimate was off by {thetaError:F0} on an intersection of " +
                $"{Shared}, so it is no more usable than the arithmetic it replaces");
        }

        [TestMethod]
        public void TestDifferenceCountsWhatIsOnlyInTheFirst()
        {
            const uint K = 4096;

            var a = Filled(K, 0, 100_000);
            var b = Filled(K, 50_000, 100_000);

            AssertNear(50_000, a.Difference(b).Count(), K, "difference of overlapping sets");

            // Order matters, unlike union and intersection.
            AssertNear(50_000, b.Difference(a).Count(), K, "difference the other way");

            // Subtracting a set from itself leaves nothing, and subtracting a disjoint
            // set leaves everything.
            Assert.AreEqual(0ul, a.Difference(a).Count(), "a set minus itself was not empty");
            AssertNear(100_000, a.Difference(Filled(K, 500_000, 100_000)).Count(),
                K, "difference of disjoint sets");
        }

        [TestMethod]
        public void TestSketchesThatCannotBeCombinedAreRefused()
        {
            var a = new ThetaSketch(4096);

            var ex = Assert.ThrowsExactly<ArgumentException>(
                () => a.Union(new ThetaSketch(1024)));
            StringAssert.Contains(ex.Message, "retained size must match");

            Assert.ThrowsExactly<ArgumentException>(() => a.Intersect(new ThetaSketch(1024)));
            Assert.ThrowsExactly<ArgumentException>(() => a.Difference(new ThetaSketch(1024)));

            var custom = new ThetaSketch(4096);
            custom.SetHash(d => (ulong)d.Length);
            Assert.ThrowsExactly<ArgumentException>(() => a.Union(custom));

            Assert.ThrowsExactly<ArgumentNullException>(() => a.Union(null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => a.Intersect(null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => a.Difference(null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => a.Add(null!));
        }

        [TestMethod]
        public void TestTheHashIsSettledOnceAnythingIsAdded()
        {
            Func<ReadOnlySpan<byte>, ulong> custom = d => (ulong)d.Length;

            var fresh = new ThetaSketch(4096);
            fresh.SetHash(custom);

            var used = new ThetaSketch(4096);
            used.Add(Item(1));
            Assert.ThrowsExactly<InvalidOperationException>(() => used.SetHash(custom));

            Assert.ThrowsExactly<ArgumentNullException>(() => fresh.SetHash(null!));
        }

        [TestMethod]
        public void TestTheConstructorRejectsAnEmptySketch()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ThetaSketch(0));
        }

        /// <summary>
        /// A restored sketch has to answer identically and keep combining, since the
        /// whole point of one is being combined with sketches from elsewhere.
        /// </summary>
        /// <summary>
        /// A sketch that has crossed its trim threshold must still round-trip. It did
        /// not: Add checked the hash against theta, then compacted to make room, and
        /// compacting can lower theta -- so the pending hash was stored unchecked, at
        /// or above the new theta. The persistence reader rightly refuses such a
        /// value, which left the sketch writing bytes it could not read back. Every
        /// round-trip test used streams below the trim threshold, so the first
        /// sketch to see real volume was the first to hit it.
        /// </summary>
        [TestMethod]
        public void TestASketchThatHasTrimmedStillRoundTrips()
        {
            var sketch = new ThetaSketch(256);
            for (int i = 0; i < 700; i++)
            {
                sketch.Add(Encoding.UTF8.GetBytes($"a-{i}"));
            }

            var bytes = sketch.ToByteArray();
            var restored = Persistence.FromByteArray<ThetaSketch>(bytes);

            Assert.AreEqual(sketch.Count(), restored.Count(),
                "the restored sketch must estimate what the original does");
            CollectionAssert.AreEqual(bytes, restored.ToByteArray(),
                "writing the restored sketch must reproduce the bytes it was read from");
        }

        /// <summary>
        /// Payloads written before the trim-boundary fix can carry exactly one value
        /// at or above theta -- the pending hash of the Add whose compaction lowered
        /// theta past it. Each trim discarded any such value a previous trim let in
        /// before possibly admitting its own, so at most one survives, and the sort
        /// puts it last. Roughly half of the sketches that ever trimmed carry one,
        /// so the reader must accept those bytes; but the value was never a valid
        /// sample, so it is dropped, and writing the restored sketch produces the
        /// corrected payload.
        /// </summary>
        [TestMethod]
        public void TestAStoredValueAtThetaIsDroppedNotFatal()
        {
            var clean = new ThetaSketch(256);
            for (int i = 0; i < 700; i++)
            {
                clean.Add(Encoding.UTF8.GetBytes($"a-{i}"));
            }
            var cleanBytes = clean.ToByteArray();

            var restored = Persistence.FromByteArray<ThetaSketch>(
                WithTrailingValues(cleanBytes, 1));

            Assert.AreEqual(clean.Count(), restored.Count(),
                "dropping the out-of-range value must leave the estimate the " +
                "corrected sketch gives");
            CollectionAssert.AreEqual(cleanBytes, restored.ToByteArray(),
                "writing the restored sketch must produce the corrected payload");
        }

        /// <summary>
        /// The tolerance is exactly one trailing value, because that is all the old
        /// writer could produce. Two is not a payload this library ever wrote.
        /// </summary>
        [TestMethod]
        public void TestTwoStoredValuesAtThetaAreRefused()
        {
            var clean = new ThetaSketch(256);
            for (int i = 0; i < 700; i++)
            {
                clean.Add(Encoding.UTF8.GetBytes($"a-{i}"));
            }

            Assert.ThrowsExactly<InvalidDataException>(() =>
                Persistence.FromByteArray<ThetaSketch>(
                    WithTrailingValues(clean.ToByteArray(), 2)));
        }

        /// <summary>
        /// Rebuilds a frame with extra values at or above theta appended, sorted last,
        /// the way the pre-fix writer would have laid its one such value out.
        /// </summary>
        private static byte[] WithTrailingValues(byte[] frame, int extras)
        {
            const int Header = 14;
            var p = frame.AsSpan(Header);
            var nominal = BinaryPrimitives.ReadUInt32LittleEndian(p);
            var theta = BinaryPrimitives.ReadUInt64LittleEndian(p[4..]);
            var held = BinaryPrimitives.ReadUInt32LittleEndian(p[12..]);

            var payload = new PayloadWriter();
            payload.WriteUInt32(nominal);
            payload.WriteUInt64(theta);
            payload.WriteUInt32(held + (uint)extras);
            for (int i = 0; i < held; i++)
            {
                payload.WriteUInt64(
                    BinaryPrimitives.ReadUInt64LittleEndian(p[(16 + 8 * i)..]));
            }
            for (int i = 0; i < extras; i++)
            {
                payload.WriteUInt64(theta + (ulong)i);
            }

            using var stream = new MemoryStream();
            PersistenceFormat.Write(
                stream, StructureId.ThetaSketch, HashId.XxHash3_64, payload.WrittenSpan);
            return stream.ToArray();
        }

        [TestMethod]
        public void TestRoundTripsThroughPersistence()
        {
            const uint K = 4096;

            var exact = Filled(K, 0, 1000);
            var restoredExact = Persistence.FromByteArray<ThetaSketch>(exact.ToByteArray());
            Assert.AreEqual(1000ul, restoredExact.Count(), "an exact sketch stopped being exact");

            var estimated = Filled(K, 0, 500_000);
            var restored = Persistence.FromByteArray<ThetaSketch>(estimated.ToByteArray());

            Assert.AreEqual(estimated.Count(), restored.Count());
            Assert.AreEqual(estimated.Retained(), restored.Retained());

            // And still combines, against a sketch that was never written out.
            var other = Filled(K, 250_000, 500_000);
            Assert.AreEqual(estimated.Union(other).Count(), restored.Union(other).Count(),
                "a restored sketch unioned differently");
            Assert.AreEqual(estimated.Intersect(other).Count(), restored.Intersect(other).Count(),
                "a restored sketch intersected differently");

            // And keeps counting.
            estimated.Add(Item(9_000_000));
            restored.Add(Item(9_000_000));
            Assert.AreEqual(estimated.Count(), restored.Count());
        }

        [TestMethod]
        public void TestAnEmptySketchRoundTrips()
        {
            var restored = Persistence.FromByteArray<ThetaSketch>(new ThetaSketch(4096).ToByteArray());

            Assert.AreEqual(0ul, restored.Count());
            restored.Add(Item(1));
            Assert.AreEqual(1ul, restored.Count());
        }

        [TestMethod]
        public void TestAnImpossiblePayloadIsRefused()
        {
            var clean = Filled(4096, 0, 500_000).ToByteArray();

            // A theta above the top of the hash range is not a sampling rate.
            var badRetained = (byte[])clean.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(badRetained.AsSpan(14 + 12), 999_999);
            RepairChecksum(badRetained);
            AssertRefused(badRetained, "retains");

            // The values are written increasing, and a payload where they are not did
            // not come from a sketch -- its count would be of something other than
            // distinct values.
            var unsorted = (byte[])clean.Clone();
            var first = BinaryPrimitives.ReadUInt64LittleEndian(unsorted.AsSpan(14 + 16));
            BinaryPrimitives.WriteUInt64LittleEndian(unsorted.AsSpan(14 + 24), first);
            RepairChecksum(unsorted);
            AssertRefused(unsorted, "increasing order");
        }

        private static void AssertRefused(byte[] payload, string expected)
        {
            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<ThetaSketch>(payload));
            StringAssert.Contains(ex.Message, expected);
        }

        private static void RepairChecksum(byte[] bytes)
        {
            var crc = new System.IO.Hashing.Crc32();
            crc.Append(bytes.AsSpan(4, bytes.Length - 8));
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(bytes.Length - 4), crc.GetCurrentHashAsUInt32());
        }

        /// <summary>
        /// Memory is this structure's weak side against <see cref="HyperLogLogPlus"/>,
        /// which is the trade it asks a caller to make, so it should not be paying more
        /// than the values themselves cost.
        /// <para>
        /// Asserted against the sketch's own storage rather than by measuring the heap.
        /// A heap measurement was written here first and was wrong to keep: one run of it
        /// reported the sketch occupying <b>minus</b> 128 bytes, so a test built on it
        /// would have passed or failed on collection timing rather than on anything about
        /// the sketch. Storage here is one array of one primitive type, so it can be
        /// stated exactly instead of measured badly.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestMemoryIsProportionalToTheValuesKept()
        {
            const uint K = 4096;

            var sketch = new ThetaSketch(K);
            for (var i = 0; i < 1_000_000; i++)
            {
                sketch.Add(Item(i));
            }

            // A sketch never holds more than twice what it retains, at eight bytes each,
            // and nothing per value besides. That is what keeping the values in a buffer
            // rather than a hash set buys, and the hash set cost three times as much.
            Assert.AreEqual(2ul * K * sizeof(ulong), sketch.SizeInBytes());

            // An empty sketch has not already paid for the values it might hold.
            Assert.IsLessThan(1024ul, new ThetaSketch(K).SizeInBytes());
        }


        /// <summary>
        /// Theta after a trim must be exactly the (k+1)-th smallest distinct hash of
        /// the stream, and the survivors exactly the k below it. This is the
        /// unbiased KMV estimator -- DataSketches' "better estimator" (K-1)/V(K),
        /// Beyer et al. 2007 -- written with K = k+1: retain k values strictly below
        /// the (k+1)-th order statistic and estimate k divided by that statistic's
        /// fraction of the hash space.
        /// <para>
        /// The characterization tests cannot see this: "the values held are exactly
        /// the input hashes below theta" is just as true of a sketch that sets theta
        /// to the k-th smallest and keeps k-1 -- self-consistent, biased by one part
        /// in k, and invisible to every behavioral test at any realistic k. The
        /// order statistic has to be computed by something that is not the trim
        /// code, which is what the independent sort below is.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestThetaIsTheKPlusFirstSmallestHash()
        {
            const uint k = 32;
            var sketch = new ThetaSketch(k);

            var hashes = new ulong[2 * k];
            for (var i = 0; i < 2 * k; i++)
            {
                var key = System.Text.Encoding.ASCII.GetBytes($"order-statistic-{i}");
                hashes[i] = sketch.Hash(key);
                sketch.Add(key);
            }

            var sorted = hashes.Distinct().OrderBy(h => h).ToArray();
            Assert.HasCount((int)(2 * k), sorted,
                "the 64 keys must hash distinctly, or the stream never reaches the " +
                "trim and theta is asserted on air.");

            Assert.AreEqual(sorted[k], sketch.ThetaValue,
                "theta must be the (k+1)-th smallest hash the stream produced -- " +
                "the k-th leaves a sample the stated rate was never applied to.");
            CollectionAssert.AreEqual(sorted.Take((int)k).ToArray(), sketch.ValuesHeld,
                "the survivors must be exactly the k hashes below theta, in order.");

            var expected = (ulong)Math.Round(
                k / (sorted[k] / 18446744073709551616.0));
            Assert.AreEqual(expected, sketch.Count(),
                "the estimate must be k over theta's fraction of the hash space, " +
                "the unbiased (K-1)/V(K) form.");
        }
    }
}
