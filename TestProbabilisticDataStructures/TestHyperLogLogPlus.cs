using System;
using System.Buffers.Binary;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// What <see cref="HyperLogLogPlus"/> does that <see cref="HyperLogLog"/> does not.
    /// <para>
    /// It exists to be better than something already here, so most of these compare the
    /// two directly rather than checking it against its own nominal error in isolation.
    /// A structure that is merely within its own error bound has not earned its place
    /// next to one that already was.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestHyperLogLogPlus
    {
        private const uint Precision = 14;
        private const uint M = 1u << (int)Precision;

        /// <summary>The relative error a dense estimator of this size should deliver.</summary>
        private static readonly double Nominal = 1.04 / Math.Sqrt(M);

        /// <summary>
        /// A distinct item per number, cheaply and identically on every platform.
        /// </summary>
        private static byte[] Item(long i)
        {
            var key = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(key, i);
            return key;
        }

        private static HyperLogLogPlus Filled(long n, long from = 0)
        {
            var estimator = new HyperLogLogPlus(Precision);
            for (var i = from; i < from + n; i++)
            {
                estimator.Add(Item(i));
            }

            return estimator;
        }

        private static double RelativeError(ulong estimate, long truth)
        {
            return Math.Abs((double)estimate - truth) / truth;
        }

        /// <summary>
        /// The reason this structure exists.
        /// <para>
        /// The older estimator uses linear counting below 2.5m and the raw estimate
        /// above it, and is at its worst where it changes over. At exactly 2.5m its
        /// error is not merely larger, it is a systematic overestimate: measured over 20
        /// independent streams it averages +2.44% against a nominal 0.81%, and stays
        /// above nominal until about 4m. That band is what HyperLogLog++ was written to
        /// fix.
        /// </para>
        /// <para>
        /// If this ever fails because <see cref="HyperLogLog"/> got better, that is good
        /// news rather than a broken test -- but it should be moved or removed
        /// deliberately, not by loosening the bound until it passes again.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestTheOlderEstimatorsWorstBandIsGone()
        {
            const long n = (long)(2.5 * M);

            var older = new HyperLogLog(M);
            var plus = new HyperLogLogPlus(Precision);

            for (long i = 0; i < n; i++)
            {
                var item = Item(i);
                older.Add(item);
                plus.Add(item);
            }

            var olderError = RelativeError(older.Count(), n);
            var plusError = RelativeError(plus.Count(), n);

            Assert.IsGreaterThan(Nominal, olderError,
                $"HyperLogLog is no longer above its nominal error at 2.5m ({olderError:P2}), " +
                "so this comparison no longer demonstrates anything");

            Assert.IsLessThan(Nominal, plusError,
                $"at 2.5m the new estimator gave {plusError:P2}, outside its nominal " +
                $"{Nominal:P2}, which is the band it exists to fix");

            Assert.IsLessThan(olderError / 4, plusError,
                $"at 2.5m: HyperLogLog {olderError:P2}, HyperLogLogPlus {plusError:P2}");
        }

        /// <summary>
        /// The band is not a single point, so this walks across it.
        /// </summary>
        [TestMethod]
        public void TestTheEstimateStaysWithinNominalAcrossTheBand()
        {
            foreach (var multiple in new[] { 1.0, 1.5, 2.0, 2.25, 2.5, 2.75, 3.0, 4.0 })
            {
                var n = (long)(multiple * M);
                var error = RelativeError(Filled(n).Count(), n);

                Assert.IsLessThan(Nominal, error,
                    $"at {multiple}m the error was {error:P2}, outside the nominal {Nominal:P2}");
            }
        }

        /// <summary>
        /// And well outside it, where the older estimator is already fine.
        /// </summary>
        [TestMethod]
        public void TestTheEstimateHoldsAcrossOrdersOfMagnitude()
        {
            foreach (var n in new long[] { 100, 1000, 10000, 100000, 1000000 })
            {
                var error = RelativeError(Filled(n).Count(), n);

                Assert.IsLessThan(2 * Nominal, error,
                    $"at n={n} the error was {error:P2}");
            }
        }

        /// <summary>
        /// The older estimator keeps only the low 32 bits of the hash, so two items
        /// whose hashes agree there are one item as far as it can tell. This is not a
        /// tail risk at scale: collisions in a 32-bit space start arriving in the tens
        /// of thousands, and by a hundred million items they cost a systematic
        /// undercount no number of registers can fix.
        /// <para>
        /// The pair below was found by hashing consecutive integers until two collided
        /// in the low 32 bits, which took 67,297 of them.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestTheWholeHashIsUsed()
        {
            var first = Item(15055);
            var second = Item(67297);

            var hash = System.IO.Hashing.XxHash3.HashToUInt64;

            Assert.AreEqual((uint)hash(first), (uint)hash(second),
                "the pinned pair no longer collides in the low 32 bits");
            Assert.AreNotEqual(hash(first), hash(second),
                "the pinned pair collides in all 64 bits, so it demonstrates nothing");

            var older = new HyperLogLog(M);
            older.Add(first);
            older.Add(second);
            Assert.AreEqual(1ul, older.Count(),
                "HyperLogLog no longer conflates the pair, so this test is obsolete");

            var plus = new HyperLogLogPlus(Precision);
            plus.Add(first);
            plus.Add(second);
            Assert.AreEqual(2ul, plus.Count(),
                "two distinct items were counted as one");
        }

        /// <summary>
        /// Below the point where registers would be cheaper, the estimator keeps the
        /// hashes themselves, and a count of distinct hashes is a count of distinct
        /// items rather than an estimate of one.
        /// </summary>
        [TestMethod]
        public void TestSmallCardinalitiesAreCountedExactly()
        {
            foreach (var n in new long[] { 0, 1, 2, 10, 100, 1000, 2048 })
            {
                Assert.AreEqual((ulong)n, Filled(n).Count(),
                    $"a sparse estimator of {n} items did not count them exactly");
            }
        }

        /// <summary>
        /// Duplicates do not accumulate. The sparse form holds hashes, so a stream that
        /// repeats itself has to compact rather than grow.
        /// </summary>
        [TestMethod]
        public void TestRepeatedItemsDoNotFillTheSparseForm()
        {
            var estimator = new HyperLogLogPlus(Precision);

            for (var round = 0; round < 500; round++)
            {
                for (var i = 0; i < 100; i++)
                {
                    estimator.Add(Item(i));
                }
            }

            Assert.AreEqual(100ul, estimator.Count());
            Assert.IsTrue(estimator.IsSparse,
                "50,000 adds of 100 distinct items pushed the estimator dense");
        }

        /// <summary>
        /// The sparse form is only worth having if it never costs more than the registers
        /// it stands in for, which is what decides when it gives way.
        /// </summary>
        [TestMethod]
        public void TestTheSparseFormNeverCostsMoreThanTheDenseOne()
        {
            var estimator = new HyperLogLogPlus(Precision);

            Assert.IsLessThan(M, estimator.SizeInBytes(),
                "an empty estimator already costs a full register array");

            for (var i = 0; i < 4000; i++)
            {
                estimator.Add(Item(i));
                Assert.IsLessThanOrEqualTo((ulong)M, estimator.SizeInBytes(),
                    $"after {i + 1} items the estimator cost more than its registers would");
            }

            Assert.IsFalse(estimator.IsSparse, "4000 items should have gone dense");
        }

        /// <summary>
        /// Crossing from exact counting to estimation must not move the answer more than
        /// the estimate's own error allows. A jump there would show up as a step in any
        /// chart drawn from a growing estimator.
        /// </summary>
        [TestMethod]
        public void TestTheAnswerDoesNotJumpWhenTheRepresentationChanges()
        {
            var estimator = new HyperLogLogPlus(Precision);
            var previous = 0ul;

            for (var i = 1; i <= 4000; i++)
            {
                estimator.Add(Item(i));
                var current = estimator.Count();

                Assert.IsGreaterThanOrEqualTo(previous, current,
                    $"the estimate fell from {previous} to {current} at {i} items");

                Assert.IsLessThan(3 * Nominal, RelativeError(current, i),
                    $"at {i} items, across the change of representation, the estimate " +
                    $"was {current}");

                previous = current;
            }
        }

        [TestMethod]
        public void TestMergeCombinesBothStreams()
        {
            // Every combination of representations, since they merge differently.
            foreach (var (left, right, name) in new[]
            {
                (500L, 500L, "sparse into sparse"),
                (500L, 50000L, "dense into sparse"),
                (50000L, 500L, "sparse into dense"),
                (50000L, 50000L, "dense into dense"),
            })
            {
                var a = Filled(left);
                var b = Filled(right, from: 1_000_000);

                a.Merge(b);

                var total = left + right;
                Assert.IsLessThan(2 * Nominal, RelativeError(a.Count(), total),
                    $"{name}: merging {left} and {right} gave {a.Count()}");

                // The one merged in is untouched, so it can be merged elsewhere too.
                Assert.IsLessThan(2 * Nominal, RelativeError(b.Count(), right),
                    $"{name}: the merged-in estimator changed");
            }
        }

        /// <summary>
        /// Overlapping streams count their union, not their sum, which is the property
        /// that makes merging worth anything.
        /// </summary>
        [TestMethod]
        public void TestMergingOverlappingStreamsCountsTheUnion()
        {
            var a = Filled(30000);
            var b = Filled(30000, from: 15000);

            a.Merge(b);

            Assert.IsLessThan(2 * Nominal, RelativeError(a.Count(), 45000),
                $"the union of two 30,000-item streams overlapping by half gave {a.Count()}");
        }

        [TestMethod]
        public void TestMergeRejectsMismatchedEstimators()
        {
            var ex = Assert.ThrowsExactly<ArgumentException>(
                () => new HyperLogLogPlus(14).Merge(new HyperLogLogPlus(12)));
            StringAssert.Contains(ex.Message, "precision must match");

            Func<ReadOnlySpan<byte>, ulong> custom = d => (ulong)d.Length;
            var other = new HyperLogLogPlus(14);
            other.SetHash(custom);

            Assert.ThrowsExactly<ArgumentException>(() => new HyperLogLogPlus(14).Merge(other));
            Assert.ThrowsExactly<ArgumentNullException>(() => new HyperLogLogPlus(14).Merge(null!));
        }

        [TestMethod]
        public void TestResetEmptiesTheEstimator()
        {
            var estimator = Filled(50000);
            Assert.IsFalse(estimator.IsSparse);

            estimator.Reset();

            Assert.AreEqual(0ul, estimator.Count());
            Assert.IsTrue(estimator.IsSparse, "a reset estimator should be sparse again");

            estimator.Add(Item(1));
            Assert.AreEqual(1ul, estimator.Count());
        }

        [TestMethod]
        public void TestTheHashCannotBeReplacedOnceAnythingIsCounted()
        {
            Func<ReadOnlySpan<byte>, ulong> custom = d => (ulong)d.Length;

            var fresh = new HyperLogLogPlus(Precision);
            fresh.SetHash(custom);

            var used = new HyperLogLogPlus(Precision);
            used.Add(Item(1));
            Assert.ThrowsExactly<InvalidOperationException>(() => used.SetHash(custom));

            // And still refused once dense, where emptiness is a property of the
            // registers rather than of a list of hashes.
            var dense = Filled(50000);
            Assert.ThrowsExactly<InvalidOperationException>(() => dense.SetHash(custom));

            // A reset estimator is empty again, so it may be re-hashed.
            dense.Reset();
            dense.SetHash(custom);
        }

        [TestMethod]
        public void TestTheConstructorRejectsAnUnusablePrecision()
        {
            foreach (var bad in new uint[] { 0, 1, 3, 19, 32, 100 })
            {
                var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => new HyperLogLogPlus(bad));
                Assert.AreEqual("precision", ex.ParamName);
            }

            _ = new HyperLogLogPlus(4);
            _ = new HyperLogLogPlus(18);
        }

        [TestMethod]
        public void TestNewDefaultPicksEnoughRegisters()
        {
            var estimator = HyperLogLogPlus.NewDefault(0.01);

            Assert.IsLessThanOrEqualTo(0.01, 1.04 / Math.Sqrt(estimator.M()),
                "the estimator cannot deliver the error it was asked for");

            foreach (var bad in new[] { 0.0, 1.0, -1.0, double.NaN })
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => HyperLogLogPlus.NewDefault(bad));
            }
        }

        [TestMethod]
        public void TestAddRejectsNull()
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => new HyperLogLogPlus(Precision).Add(null!));
        }

        /// <summary>
        /// Both representations round trip, and a sparse payload stays small: writing
        /// the dense form regardless would undo the thing the sparse form is for.
        /// </summary>
        [TestMethod]
        public void TestBothRepresentationsRoundTrip()
        {
            var sparse = Filled(1000);
            var sparseBytes = sparse.ToByteArray();
            Assert.IsLessThan(M, (uint)sparseBytes.Length,
                "a sparse payload is as large as a dense one");

            var restoredSparse = Persistence.FromByteArray<HyperLogLogPlus>(sparseBytes);
            Assert.AreEqual(1000ul, restoredSparse.Count());
            Assert.IsTrue(restoredSparse.IsSparse, "a sparse payload came back dense");

            // And keeps counting, exactly, and then across the change of representation.
            for (var i = 1000; i < 5000; i++)
            {
                restoredSparse.Add(Item(i));
            }

            Assert.IsLessThan(2 * Nominal, RelativeError(restoredSparse.Count(), 5000));

            var dense = Filled(50000);
            var restoredDense = Persistence.FromByteArray<HyperLogLogPlus>(dense.ToByteArray());

            Assert.IsFalse(restoredDense.IsSparse, "a dense payload came back sparse");
            Assert.AreEqual(dense.Count(), restoredDense.Count(),
                "a restored dense estimator gave a different answer");

            for (var i = 50000; i < 60000; i++)
            {
                dense.Add(Item(i));
                restoredDense.Add(Item(i));
            }

            Assert.AreEqual(dense.Count(), restoredDense.Count(),
                "a restored estimator diverged as counting continued");
        }

        [TestMethod]
        public void TestAnEmptyEstimatorRoundTrips()
        {
            var restored = Persistence.FromByteArray<HyperLogLogPlus>(
                new HyperLogLogPlus(Precision).ToByteArray());

            Assert.AreEqual(0ul, restored.Count());
            restored.Add(Item(1));
            Assert.AreEqual(1ul, restored.Count());
        }

        /// <summary>
        /// A payload that does not describe an estimator this library builds is refused.
        /// The checksum is repaired after each edit, so a guard has to catch it.
        /// </summary>
        [TestMethod]
        public void TestAnImpossiblePayloadIsRefused()
        {
            var clean = Filled(50000).ToByteArray();

            var badPrecision = (byte[])clean.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(badPrecision.AsSpan(14), 25);
            RepairChecksum(badPrecision);
            AssertRefused(badPrecision, "precision");

            var badRepresentation = (byte[])clean.Clone();
            badRepresentation[14 + 4] = 7;
            RepairChecksum(badRepresentation);
            AssertRefused(badRepresentation, "representation");

            // Sparse hashes are written sorted and distinct, so that the count is the
            // number of distinct items rather than of whatever was in the buffer.
            var sparse = Filled(50).ToByteArray();
            var unsorted = (byte[])sparse.Clone();
            var firstHash = BinaryPrimitives.ReadUInt64LittleEndian(unsorted.AsSpan(14 + 9));
            BinaryPrimitives.WriteUInt64LittleEndian(unsorted.AsSpan(14 + 9 + 8), firstHash);
            RepairChecksum(unsorted);
            AssertRefused(unsorted, "increasing order");
        }

        private static void AssertRefused(byte[] payload, string expected)
        {
            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<HyperLogLogPlus>(payload));
            StringAssert.Contains(ex.Message, expected);
        }

        private static void RepairChecksum(byte[] bytes)
        {
            var crc = new System.IO.Hashing.Crc32();
            crc.Append(bytes.AsSpan(4, bytes.Length - 8));
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(bytes.Length - 4), crc.GetCurrentHashAsUInt32());
        }
    }
}
