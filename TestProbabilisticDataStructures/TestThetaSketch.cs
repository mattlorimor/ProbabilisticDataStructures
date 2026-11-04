using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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

        /// <summary>
        /// The header and eight bytes a value held, which is what the sketch holds once the
        /// write has compacted it: one written uncompacted is counted at what it writes, and
        /// one past its nominal entries at the entries it keeps.
        /// </summary>
        internal static IEnumerable<PayloadState> PayloadStates()
        {
            yield return PayloadState.Of("empty", new ThetaSketch(16));

            // Repeats in the buffer until the write compacts them away.
            var exact = new ThetaSketch(4096);
            for (var i = 0; i < 300; i++) exact.Add(Item(i % 100));
            Assert.IsFalse((bool)typeof(ThetaSketch).GetField("compact",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(exact)!, "the state was compacted before the write");
            yield return PayloadState.Of("exact, repeats uncompacted", exact);

            var estimating = new ThetaSketch(16);
            for (var i = 0; i < 1000; i++) estimating.Add(Item(i));
            yield return PayloadState.Of("past its nominal entries", estimating);
        }

        [TestMethod]
        public void TestANewSketchIsEmpty()
        {
            var sketch = new ThetaSketch(4096);

            Assert.AreEqual(0ul, sketch.Count());
        }

        /// <summary>
        /// A theta sketch's payload is its nominal entries, theta and how many values it
        /// holds, sixteen bytes, then eight bytes a value. A sketch holds up to twice its
        /// nominal entries less one before it trims, and the constructor takes nominal
        /// entries up to half the largest array, so a sketch it builds can hold far more
        /// than a payload does: one of nominal entries 268,435,445 holds that many after
        /// its first trim, the first count past the ceiling. The writer wrote the values
        /// one at a time and never asked how large the payload is, so a sketch claiming
        /// more values than its buffer has was not refused at all; the write ran off the
        /// end of the buffer. At 2^29 values the run is 2^32 bytes, nought in 32 bits: a
        /// count of values times <c>sizeof(ulong)</c> is worked out in 32 bits unless the
        /// count is widened first.
        /// </summary>
        [TestMethod]
        [DataRow(268_435_445, 2_147_483_576L)]
        [DataRow(536_870_912, 4_294_967_312L)]
        public void TestASketchTooLargeToWriteIsRefusedNamingItsWholeSize(int held, long payload)
        {
            Assert.AreEqual(16 + 8L * held, payload, "the row's payload is miscomputed");
            Assert.IsLessThan(2L * ThetaSketch.MaxNominalEntries, (long)held,
                "a sketch the constructor builds cannot hold this many");

            var sketch = new ThetaSketch(16);
            for (var i = 0; i < 40; i++) sketch.Add(Item(i));

            TestPayloadCount.AssertRefusedWhileClaiming(sketch, payload, (sketch, "held", held));
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
        /// <para>
        /// k = 1 is the coarsest legal sketch: it trims at two values, theta is the
        /// larger hash and the survivor the smaller, and the estimator's variance is
        /// infinite -- which is exactly why the rule must be pinned there, because
        /// no behavioral window can be.
        /// </para>
        /// </summary>
        [TestMethod]
        [DataRow(1u)]
        [DataRow(32u)]
        public void TestThetaIsTheKPlusFirstSmallestHash(uint k)
        {
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

        /// <summary>
        /// The retained sizes the continuation tests run at: from the coarsest legal
        /// sketch to just past k = 8, where twice the retained size reaches the sixteen
        /// values the reader and the set operations made room for. Below that they
        /// gave a sketch more room than it may hold; from there on they could not.
        /// <para>
        /// And three sizes beyond it, because from k = 8 on that was an argument and
        /// not a measurement. The buffer starts at sixteen and doubles up to twice the
        /// retained size, so a larger k is where it doubles more than once and where
        /// the cap cuts the last step short: k = 12 grows 16 to 24 in one step, k = 33
        /// goes 16, 32, 64 and then stops at 66, and k = 100 goes 16, 32, 64, 128 and
        /// stops at 200. The sizing rule says none of it can matter once the cap no
        /// longer binds, since the sketch trims only when a full buffer holds twice
        /// the retained size in distinct values, whatever route the buffer took to
        /// that size. These rows hold the rule to that.
        /// </para>
        /// </summary>
        public static IEnumerable<object[]> RetainedSizesAroundTheStartingBuffer =>
            Enumerable.Range(1, 9).Concat(new[] { 12, 33, 100 })
                .Select(k => new object[] { (uint)k });

        private const int ContinuationStreams = 20;

        /// <summary>
        /// How many distinct keys a continuation stream draws on. At the coarse sizes
        /// the same hundred and fifty keys serve, and they still trim more than once.
        /// A sketch trims each time it has seen twice as many distinct keys as it last
        /// did, starting from twice its retained size, so a larger k needs
        /// proportionally more keys to reach its later trims -- and the comparison
        /// can only see the buffer if a trim is still to come after the split.
        /// </summary>
        private static int ContinuationKeys(uint k) => Math.Max(150, 12 * (int)k);

        /// <summary>
        /// Records per stream: every key once, and then the first few again.
        /// </summary>
        private static int ContinuationLength(uint k) => ContinuationKeys(k) + 50;

        /// <summary>
        /// The streams the continuation tests split. Keys repeat, so the part of a
        /// stream after a split revisits keys from before it, and a sketch that
        /// carries on from a split has to recognise repeats of values it was handed
        /// as well as of values it saw arrive.
        /// </summary>
        private static byte[] Record(uint k, int stream, int i) =>
            Encoding.UTF8.GetBytes($"t{stream}-item-{i % ContinuationKeys(k)}");

        /// <summary>
        /// Saving a sketch and reading it back must be invisible to everything it
        /// does afterwards. Each stream is split at every point, the sketch built so
        /// far is written out and read back, the rest of the stream goes to what was
        /// read, and the result is held -- byte for byte, and in its estimate -- to a
        /// sketch that saw the whole stream and was never written anywhere.
        /// <para>
        /// That is a stronger reference than the original carrying on beside its copy,
        /// because writing compacts the original too, and a defect in what compaction
        /// leaves behind would be shared by both and agreed on.
        /// </para>
        /// <para>
        /// The defect this pins was in the buffer, which the payload does not carry.
        /// <c>Add</c> compacts only when the buffer is full, and only a compaction
        /// trims, so a buffer with room past twice the retained size lets the sketch
        /// hold more than that before trimming. It then trims at different moments,
        /// settles on a different theta and keeps a different sample -- a valid
        /// state, and not the one it would have reached unsaved. The reader made room
        /// for at least sixteen values, which is more than twice the retained size
        /// below k = 8. At k = 1 a trim keeps exactly one value whenever it fires, so
        /// there the extra room changed the storage and never the estimate; the
        /// storage is pinned in
        /// <see cref="TestNoPathGivesTheBufferMoreRoomThanTheSketchMayHold"/>.
        /// </para>
        /// </summary>
        [TestMethod]
        [DynamicData(nameof(RetainedSizesAroundTheStartingBuffer))]
        public void TestASketchReadBackGoesOnAsIfItHadNeverBeenSaved(uint k)
        {
            AssertCarriesOnAsTheUnsplitSketch(k, "read back from its own bytes",
                sketch => Persistence.FromByteArray<ThetaSketch>(sketch.ToByteArray()));
        }

        /// <summary>
        /// A set operation's result is rebuilt from scratch, and it was rebuilt with
        /// the same oversized buffer as a sketch read back. A union with an empty
        /// sketch has exactly the state of the sketch it was taken from, so it is held
        /// to that sketch's stream as it carries on, by the same comparison as
        /// <see cref="TestASketchReadBackGoesOnAsIfItHadNeverBeenSaved"/>. The
        /// intersection and difference tests beside it do the same for the other two
        /// operations, which share the rebuild but not the merge in front of it.
        /// </summary>
        [TestMethod]
        [DynamicData(nameof(RetainedSizesAroundTheStartingBuffer))]
        public void TestAUnionWithAnEmptySketchGoesOnAsTheSketchItEquals(uint k)
        {
            AssertCarriesOnAsTheUnsplitSketch(k, "unioned with an empty sketch",
                sketch => sketch.Union(new ThetaSketch(k)));
        }

        /// <summary>
        /// See <see cref="TestAUnionWithAnEmptySketchGoesOnAsTheSketchItEquals"/>.
        /// </summary>
        [TestMethod]
        [DynamicData(nameof(RetainedSizesAroundTheStartingBuffer))]
        public void TestAnIntersectionWithItselfGoesOnAsTheSketchItEquals(uint k)
        {
            AssertCarriesOnAsTheUnsplitSketch(k, "intersected with itself",
                sketch => sketch.Intersect(sketch));
        }

        /// <summary>
        /// See <see cref="TestAUnionWithAnEmptySketchGoesOnAsTheSketchItEquals"/>.
        /// </summary>
        [TestMethod]
        [DynamicData(nameof(RetainedSizesAroundTheStartingBuffer))]
        public void TestADifferenceFromAnEmptySketchGoesOnAsTheSketchItEquals(uint k)
        {
            AssertCarriesOnAsTheUnsplitSketch(k, "less an empty sketch",
                sketch => sketch.Difference(new ThetaSketch(k)));
        }

        /// <summary>
        /// The one set operation that trims as it rebuilds: a union of two exact
        /// sketches of k keys each holds twice the retained size, so it trims on the
        /// spot -- and lands on exactly the state of one sketch fed both key sets,
        /// which trims at the same point. The two are then fed the same records and
        /// must stay identical.
        /// </summary>
        [TestMethod]
        [DynamicData(nameof(RetainedSizesAroundTheStartingBuffer))]
        public void TestAUnionThatTrimsGoesOnAsTheSketchOfBothStreams(uint k)
        {
            const int Streams = 100;
            var diverged = 0;
            string first = null;

            for (var t = 0; t < Streams; t++)
            {
                var left = new ThetaSketch(k);
                var right = new ThetaSketch(k);
                var both = new ThetaSketch(k);
                for (var i = 0; i < k; i++)
                {
                    left.Add(Encoding.UTF8.GetBytes($"t{t}-left-{i}"));
                    both.Add(Encoding.UTF8.GetBytes($"t{t}-left-{i}"));
                }
                for (var i = 0; i < k; i++)
                {
                    right.Add(Encoding.UTF8.GetBytes($"t{t}-right-{i}"));
                    both.Add(Encoding.UTF8.GetBytes($"t{t}-right-{i}"));
                }

                var union = left.Union(right);
                Assert.AreNotEqual(ulong.MaxValue, union.ThetaValue,
                    $"stream {t}: the union must have trimmed while rebuilding, or the " +
                    "path this test exists for never ran.");
                CollectionAssert.AreEqual(both.ToByteArray(), union.ToByteArray(),
                    $"stream {t}: the union must start as the sketch of both key sets, " +
                    "or what follows compares two different states.");

                for (var i = 0; i < ContinuationLength(k); i++)
                {
                    both.Add(Record(k, t, i));
                    union.Add(Record(k, t, i));
                }

                if (!both.ToByteArray().AsSpan().SequenceEqual(union.ToByteArray())
                    || both.Count() != union.Count())
                {
                    diverged++;
                    first ??= $"stream {t}, estimating {union.Count()} against {both.Count()}";
                }
            }

            Assert.AreEqual(0, diverged,
                $"k = {k}: {diverged} of {Streams} unions went on differently from the " +
                $"sketch they started equal to, first {first}. A rebuilt sketch's buffer " +
                "decides when it trims, so it must be sized as a new one's would be.");
        }

        /// <summary>
        /// The mechanism behind the continuation tests, stated directly: no path that
        /// makes a sketch gives its buffer room for more than twice the retained size,
        /// because that is the most it may hold before it trims, and room past it
        /// delays the trim. Asserted through <see cref="ThetaSketch.SizeInBytes"/>,
        /// which is the buffer and nothing else.
        /// <para>
        /// This is what sees the defect at k = 1, where the continuation tests cannot.
        /// And a sketch fed past its trim threshold must have grown to exactly the
        /// full allowance, since only a full allowance trims, so growth that stops
        /// short or overshoots fails here too. k = 9 straddles the growth step: there,
        /// doubling the starting buffer passes twice the retained size, so a growth
        /// rule that forgot its cap would show. The larger sizes are where growth has
        /// to keep going past its first step, and where a rule that stopped doubling
        /// early would leave the buffer short of the allowance.
        /// </para>
        /// </summary>
        [TestMethod]
        [DynamicData(nameof(RetainedSizesAroundTheStartingBuffer))]
        public void TestNoPathGivesTheBufferMoreRoomThanTheSketchMayHold(uint k)
        {
            var allowance = 2ul * k * sizeof(ulong);

            var fed = new ThetaSketch(k);
            var other = new ThetaSketch(k);
            for (var i = 0; i < ContinuationLength(k); i++)
            {
                fed.Add(Record(k, 0, i));
                other.Add(Record(k, 1, i));
            }
            Assert.AreNotEqual(ulong.MaxValue, fed.ThetaValue,
                "the fed sketch must have trimmed, or it never needed its allowance.");
            Assert.AreEqual(allowance, fed.SizeInBytes(),
                $"k = {k}: a sketch that has trimmed must have grown to its full " +
                "allowance and no further.");

            var left = new ThetaSketch(k);
            var right = new ThetaSketch(k);
            for (var i = 0; i < k; i++)
            {
                left.Add(Encoding.UTF8.GetBytes($"left-{i}"));
                right.Add(Encoding.UTF8.GetBytes($"right-{i}"));
            }

            var made = new (string What, ThetaSketch Sketch)[]
            {
                ("a new sketch", new ThetaSketch(k)),
                ("an empty sketch read back",
                    Persistence.FromByteArray<ThetaSketch>(new ThetaSketch(k).ToByteArray())),
                ("a trimmed sketch read back",
                    Persistence.FromByteArray<ThetaSketch>(fed.ToByteArray())),
                ("a union", fed.Union(other)),
                ("an intersection", fed.Intersect(other)),
                ("a difference", fed.Difference(other)),
                ("a union that trims as it rebuilds", left.Union(right)),
            };

            var over = made
                .Where(m => m.Sketch.SizeInBytes() > allowance)
                .Select(m => $"{m.What} ({m.Sketch.SizeInBytes()} bytes)")
                .ToList();

            Assert.IsEmpty(over,
                $"k = {k}, allowance {allowance} bytes: {string.Join(", ", over)}. Room " +
                "past twice the retained size delays a sketch's trims, and every " +
                "estimate after them moves.");
        }

        /// <summary>
        /// The other half of the sizing rule: a sketch read back or rebuilt by a set
        /// operation is sized by what it holds, not by the allowance it may grow into.
        /// One holding no more than a new sketch has room for costs exactly what a new
        /// one does. Otherwise a payload that claims a large k and holds almost
        /// nothing would cost its whole allowance the moment it was read.
        /// </summary>
        [TestMethod]
        public void TestASmallSketchReadBackOrRebuiltCostsWhatANewOneDoes()
        {
            const uint K = 4096;
            var fresh = new ThetaSketch(K).SizeInBytes();

            var left = new ThetaSketch(K);
            var right = new ThetaSketch(K);
            for (var i = 0; i < 5; i++)
            {
                left.Add(Item(i));
                right.Add(Item(100 + i));
            }

            var made = new (string What, ThetaSketch Sketch)[]
            {
                ("an empty sketch read back",
                    Persistence.FromByteArray<ThetaSketch>(new ThetaSketch(K).ToByteArray())),
                ("a small sketch read back",
                    Persistence.FromByteArray<ThetaSketch>(left.ToByteArray())),
                ("a union", left.Union(right)),
                ("an intersection", left.Intersect(right)),
                ("a difference", left.Difference(right)),
            };

            foreach (var (what, sketch) in made)
            {
                Assert.AreEqual(fresh, sketch.SizeInBytes(),
                    $"{what} must cost what a new sketch does, not the " +
                    $"{2ul * K * sizeof(ulong)} bytes it may grow to.");
            }
        }

        /// <summary>
        /// Splits each continuation stream at every point, hands the sketch built so
        /// far to <paramref name="carryOn"/>, feeds the rest of the stream to what
        /// comes back, and holds the result to a sketch that saw the whole stream
        /// unsplit: identical bytes and an identical estimate.
        /// </summary>
        private static void AssertCarriesOnAsTheUnsplitSketch(
            uint k, string how, Func<ThetaSketch, ThetaSketch> carryOn)
        {
            int splits = 0, crossed = 0, diverged = 0;
            string first = null;

            var length = ContinuationLength(k);

            for (var t = 0; t < ContinuationStreams; t++)
            {
                // Built once per stream: the larger sizes replay a long stream from
                // every split, and the keys are the same each time.
                var records = Enumerable.Range(0, length)
                    .Select(i => Record(k, t, i))
                    .ToArray();

                var unsplit = new ThetaSketch(k);
                for (var i = 0; i < length; i++)
                {
                    unsplit.Add(records[i]);
                }
                var expected = unsplit.ToByteArray();

                var prefix = new ThetaSketch(k);
                for (var split = 0; split < length; split++)
                {
                    var before = prefix.ToByteArray();
                    var carried = carryOn(prefix);
                    CollectionAssert.AreEqual(before, carried.ToByteArray(),
                        $"k = {k}, stream {t}, split {split}: a sketch {how} must start as " +
                        "the sketch it came from, or what follows compares two states.");

                    var thetaAtSplit = carried.ThetaValue;
                    for (var i = split; i < length; i++)
                    {
                        carried.Add(records[i]);
                    }

                    splits++;
                    if (thetaAtSplit != ulong.MaxValue && carried.ThetaValue < thetaAtSplit)
                    {
                        crossed++;
                    }

                    if (!carried.ToByteArray().AsSpan().SequenceEqual(expected)
                        || carried.Count() != unsplit.Count())
                    {
                        diverged++;
                        first ??= $"stream {t} split at record {split}, estimating " +
                            $"{carried.Count()} against {unsplit.Count()}";
                    }

                    prefix.Add(records[split]);
                }
            }

            Console.WriteLine($"k = {k}, {how}: {crossed} of {splits} splits fell " +
                $"between two trims, {diverged} went on differently");

            Assert.AreEqual(0, diverged,
                $"k = {k}: {diverged} of {splits} splits went on differently once {how}, " +
                $"first {first}. The buffer is not in the payload, so a sketch rebuilt " +
                "without one must be sized by the rule a sketch growing its own follows.");

            // The comparison can only see the buffer if the split falls between trims:
            // after one, so the sketch handed on is already sampling, and before
            // another, so the buffer is asked to decide when that one fires. Late
            // splits have no trim left, so this asks for a share of them, not all.
            Assert.IsGreaterThan(splits / 3, crossed,
                $"k = {k}: only {crossed} of {splits} splits fell between two trims, so " +
                "too much of this test compared sketches with no trim left to disagree on.");
        }
    }
}
