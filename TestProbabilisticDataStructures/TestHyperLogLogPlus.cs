using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
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

        /// <summary>
        /// Every precision the constructor accepts, asked of the constructor rather than
        /// typed out, so the coarsest corner is wherever its guard puts it.
        /// </summary>
        private static List<uint> LegalPrecisions()
        {
            var legal = new List<uint>();
            for (uint precision = 0; precision <= 64; precision++)
            {
                try
                {
                    _ = new HyperLogLogPlus(precision);
                    legal.Add(precision);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Not one it builds.
                }
            }

            Assert.Contains(4u, legal,
                "precision 4 is the coarsest corner these sweeps exist to reach, and the " +
                "constructor no longer accepts it");
            return legal;
        }

        /// <summary>
        /// Runs a check at every legal precision and fails once, naming every precision
        /// it failed at, so a defect confined to the coarse corner reads as one.
        /// </summary>
        private static void AtEveryPrecision(Func<uint, string> check)
        {
            var failures = new List<string>();
            foreach (var precision in LegalPrecisions())
            {
                var failure = check(precision);
                if (failure is not null)
                {
                    failures.Add($"precision {precision}: {failure}");
                }
            }

            Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
        }

        /// <summary>
        /// The most distinct items a new estimator holds before it goes dense, read from
        /// the estimator itself rather than from any rule about where that should be.
        /// </summary>
        private static int MostHeldSparsely(uint precision)
        {
            var probe = new HyperLogLogPlus(precision);
            var held = 0;

            while (true)
            {
                probe.Add(Item(held));
                if (!probe.IsSparse)
                {
                    return held;
                }

                held++;
            }
        }

        /// <summary>
        /// A distinct count is blind to repetition, so an item the estimator already
        /// holds changes nothing -- including when it arrives with the sparse form
        /// exactly full, the one moment a new item would change the representation.
        /// It did not used to be blind there: a full buffer converted on any further add,
        /// before anything asked whether the hash was already held, so a repeat traded
        /// the exact count for an estimate. That is #149, where 32 became 31 at
        /// precision 8.
        /// <para>
        /// The conversion point comes from the estimator itself, so this fails for a
        /// repeat and not for where the point sits, which the next test owns. The count
        /// alone does not see it everywhere: probed before the fix, a repeat at the point
        /// converted at every precision but at some left the estimate equal to the exact
        /// count it replaced. So the representation and the bytes are what is held, with
        /// the count beside them. Every held item is repeated, the smallest and largest
        /// hashes included, because a membership check that misses one end of the sorted
        /// buffer converts on exactly one of them.
        /// </para>
        /// <para>
        /// Merging in a sparse estimator of items already held is the same repeat by the
        /// other path that adds hashes one at a time.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestARepeatAtTheConversionPointChangesNothing()
        {
            AtEveryPrecision(precision =>
            {
                var held = MostHeldSparsely(precision);

                var full = new HyperLogLogPlus(precision);
                for (var i = 0; i < held; i++)
                {
                    full.Add(Item(i));
                }

                var before = full.ToByteArray();

                for (var i = 0; i < held; i++)
                {
                    full.Add(Item(i));

                    if (!full.IsSparse)
                    {
                        return $"repeating item {i} of the {held} held converted the " +
                            $"estimator to dense, and the count went from {held} to " +
                            $"{full.Count()}";
                    }

                    if (full.Count() != (ulong)held)
                    {
                        return $"repeating item {i} moved the count from {held} to " +
                            $"{full.Count()}";
                    }
                }

                if (!before.AsSpan().SequenceEqual(full.ToByteArray()))
                {
                    return "repeating every held item changed the state";
                }

                var same = new HyperLogLogPlus(precision);
                for (var i = held - 1; i >= 0; i--)
                {
                    same.Add(Item(i));
                }

                full.Merge(same);

                if (!full.IsSparse || !before.AsSpan().SequenceEqual(full.ToByteArray()))
                {
                    return "merging in only items already held changed the state";
                }

                // And it really was the conversion point: one item it does not hold is
                // enough. Without this, a point found too early would let every repeat
                // above pass without ever arriving at a full buffer.
                full.Add(Item(held));
                return full.IsSparse
                    ? $"the state the repeats were tested in, holding {held}, was not the " +
                      "conversion point"
                    : null;
            });
        }

        /// <summary>
        /// Where the sparse form gives way, restated from the paper rather than read back
        /// from the implementation. Heule, Nunkesser and Hall, "HyperLogLog in Practice"
        /// (EDBT 2013), section 5.3: the sparse list is kept until it "would require more
        /// memory than the dense representation of the registers (i.e., 6m bits)", and
        /// Figure 6 converts at line 22 when |sparse_list| &gt; m * 6 bits, measured after
        /// the new hash has been merged into the list (lines 17 to 20). Their registers
        /// are six bits; here a register is one byte and a held hash all eight of the
        /// hash's bytes (section 5.3.1 allows p' up to 64, "at which points the full hash
        /// code is kept"). So the list may hold m / 8 hashes, and the next distinct one
        /// converts. Checked against the paper as Google Research publishes it,
        /// 2026-11-01.
        /// <para>
        /// At every precision, because the coarse ones are where it was wrong. The buffer
        /// started at sixteen hashes and grows only toward the cap, never shrinks to it,
        /// so below precision 7 the conversion came wherever that first buffer filled
        /// rather than at the cap. The cap itself was floored at four, which precision 4
        /// alone discriminates: m / 8 is two there, and four at precision 5, where the
        /// floor and the rule agree.
        /// </para>
        /// <para>
        /// Three ways in, because three places build a sparse buffer: a new estimator,
        /// one read back part-filled, whose buffer the reader sizes, and one reset after
        /// going dense.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestTheSparseFormHoldsAsManyHashesAsTheRegistersHaveBytes()
        {
            AtEveryPrecision(precision =>
            {
                var registers = 1 << (int)precision;
                var cap = registers / sizeof(ulong);

                var part = new HyperLogLogPlus(precision);
                for (var i = 0; i < cap / 2; i++)
                {
                    part.Add(Item(i));
                }

                return ConvertsAt(new HyperLogLogPlus(precision), 0, cap, "new")
                    ?? ConvertsAt(Persistence.FromByteArray<HyperLogLogPlus>(part.ToByteArray()),
                        cap / 2, cap, $"read back holding {cap / 2}")
                    ?? ConvertsAt(DenseThenReset(precision), 0, cap, "reset after going dense");
            });
        }

        private static string ConvertsAt(
            HyperLogLogPlus estimator, int alreadyHeld, int cap, string where)
        {
            for (var i = alreadyHeld; i < cap; i++)
            {
                estimator.Add(Item(i));
                if (!estimator.IsSparse)
                {
                    return $"{where}, went dense at {i + 1} distinct items, short of the " +
                        $"{cap} hashes the registers' bytes would hold";
                }
            }

            if (estimator.Count() != (ulong)cap)
            {
                return $"{where}, holding {cap} items counted {estimator.Count()}";
            }

            estimator.Add(Item(cap));
            return estimator.IsSparse
                ? $"{where}, still sparse at {cap + 1} distinct items, more hashes than " +
                  $"the registers' {cap * sizeof(ulong)} bytes would hold"
                : null;
        }

        private static HyperLogLogPlus DenseThenReset(uint precision)
        {
            var estimator = new HyperLogLogPlus(precision);
            for (var i = 0; estimator.IsSparse; i++)
            {
                estimator.Add(Item(i));
            }

            return estimator.Reset();
        }

        /// <summary>
        /// The promise above, at every precision rather than at 14 alone, and in each of
        /// the ways a sparse buffer gets built. At 14 it holds with room to spare. At 4 it
        /// did not hold before anything was added: a new estimator's buffer cost 128
        /// bytes against the 16 its registers would, so the sparse form began life eight
        /// times the size of the thing it exists to undercut.
        /// </summary>
        [TestMethod]
        public void TestTheSparseFormNeverCostsMoreThanTheDenseOneAtAnyPrecision()
        {
            AtEveryPrecision(precision =>
            {
                var registers = 1ul << (int)precision;

                // One item, which is below the conversion point at any precision on any
                // reading of the rule, so the reader's own sizing is what is measured.
                var one = new HyperLogLogPlus(precision);
                one.Add(Item(0));

                return NeverCostsMore(new HyperLogLogPlus(precision), 0, registers, "new")
                    ?? NeverCostsMore(Persistence.FromByteArray<HyperLogLogPlus>(one.ToByteArray()),
                        1, registers, "read back holding one")
                    ?? NeverCostsMore(DenseThenReset(precision), 0, registers,
                        "reset after going dense");
            });
        }

        private static string NeverCostsMore(
            HyperLogLogPlus estimator, int alreadyHeld, ulong registers, string where)
        {
            for (var i = alreadyHeld; estimator.IsSparse; i++)
            {
                if (estimator.SizeInBytes() > registers)
                {
                    return $"{where}, holding {i} items the sparse form cost " +
                        $"{estimator.SizeInBytes()} bytes, against {registers} for the registers";
                }

                if ((ulong)i > registers)
                {
                    return $"{where}, still sparse after more distinct items than there " +
                        "are registers";
                }

                estimator.Add(Item(i));
            }

            return null;
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

        /// <summary>
        /// Ertl's sigma function, restated from equation 33 of "New cardinality
        /// estimation algorithms for HyperLogLog sketches" (2017), verified against
        /// the paper 2026-08-18: sigma(x) = x + sum over k of x^(2^k) * 2^(k-1),
        /// diverging at x = 1. This is the zero-register correction that replaces
        /// linear counting in the dense estimator.
        /// </summary>
        private static double ErtlSigma(double x)
        {
            if (x == 1.0)
            {
                return double.PositiveInfinity;
            }

            double z = x, y = 1.0, previous;
            do
            {
                x *= x;
                previous = z;
                z += x * y;
                y += y;
            }
            while (previous != z);
            return z;
        }

        /// <summary>
        /// Ertl's tau function in the numerically stable form of equation 37:
        /// tau(x) = (1 - x - sum over k of (1 - x^(2^-k))^2 * 2^-k) / 3. This is the
        /// saturated-register correction that replaces the classic large-range
        /// correction.
        /// </summary>
        private static double ErtlTau(double x)
        {
            if (x == 0.0 || x == 1.0)
            {
                return 0.0;
            }

            double y = 1.0, z = 1 - x, previous;
            do
            {
                x = Math.Sqrt(x);
                previous = z;
                y *= 0.5;
                z -= Math.Pow(1 - x, 2) * y;
            }
            while (previous != z);
            return z / 3.0;
        }

        /// <summary>
        /// The full estimator of Ertl's equation 32, from the register histogram:
        /// alpha_inf * m^2 / (m*sigma(C0/m) + sum C_k 2^-k + m*tau(1 - C_(q+1)/m) * 2^-q),
        /// with alpha_inf = 1/(2 ln 2). Summed here in the opposite order from the
        /// implementation's Horner fold, deliberately: agreement then says the fold
        /// computes the printed series, not merely something self-consistent.
        /// </summary>
        private static double ErtlEstimate(byte[] registers, int q)
        {
            var m = (double)registers.Length;
            var counts = new int[q + 2];
            foreach (var r in registers)
            {
                counts[r]++;
            }

            var denominator = m * ErtlTau(1 - (counts[q + 1] / m)) * Math.Pow(2, -q);
            for (var k = q; k >= 1; k--)
            {
                denominator += counts[k] * Math.Pow(2, -k);
            }
            denominator += m * ErtlSigma(counts[0] / m);

            return 1.0 / (2.0 * Math.Log(2)) * registers.Length * registers.Length / denominator;
        }

        /// <summary>
        /// An estimator forced dense: 9,000 distinct items cannot fit the sparse
        /// form's budget at precision 12, so the registers exist and can be crafted.
        /// </summary>
        private static HyperLogLogPlus DenseEstimator()
        {
            var hll = new HyperLogLogPlus(12);
            for (int i = 0; i < 9000; i++)
            {
                hll.Add(Encoding.ASCII.GetBytes($"dense-{i}"));
            }
            Assert.IsFalse(hll.IsSparse,
                "9,000 distinct items must force the dense form, or nothing below " +
                "tests dense at all.");
            return hll;
        }

        /// <summary>
        /// The dense estimator against Ertl's printed pipeline on four crafted
        /// register states: a mixed state that exercises only the harmonic series, a
        /// half-zeros state where sigma carries the answer, a state with saturated
        /// registers present (whose tau contribution at q = 52 sits twelve orders
        /// below the harmonic term -- the state where tau actually carries weight
        /// needs precision 18 and lives in the next test), and the empty state,
        /// which must answer exactly zero because sigma(1) diverges.
        /// <para>
        /// No behavioral test can check this estimator against anything but itself:
        /// its whole claim is to be less biased than the raw formula, and bias this
        /// small only shows against the closed form. The alpha_inf constant is
        /// 1/(2 ln 2) = 0.7213475...; the truncated 0.7213 an older HLL formula uses
        /// moves the mixed row by one count in fifteen thousand, and only exact
        /// agreement with the restated series notices.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestDenseEstimateIsErtlsPrintedFormula()
        {
            var hll = DenseEstimator();
            var registers = hll.DenseRegisterState!;
            var q = 64 - 12;

            for (int i = 0; i < registers.Length; i++)
            {
                registers[i] = (byte)(1 + (i % 7));
            }
            Assert.AreEqual((ulong)Math.Round(ErtlEstimate(registers, q)), hll.Count(),
                "mixed registers: the estimate must be equation 32 to the digit.");

            for (int i = 0; i < registers.Length; i++)
            {
                registers[i] = (byte)(i % 2 == 0 ? 0 : 3);
            }
            var zeros = ErtlEstimate(registers, q);
            Assert.IsGreaterThan(0, Array.IndexOf(registers, (byte)0) + 1,
                "the sigma row must actually contain zero registers.");
            Assert.AreEqual((ulong)Math.Round(zeros), hll.Count(),
                "half-zero registers: sigma must carry the estimate, per equation 33.");

            for (int i = 0; i < registers.Length; i++)
            {
                registers[i] = (byte)(i % 8 == 0 ? q + 1 : 2);
            }
            Assert.IsGreaterThan(0, Array.IndexOf(registers, (byte)(q + 1)) + 1,
                "the saturation row must actually contain saturated registers.");
            Assert.AreEqual((ulong)Math.Round(ErtlEstimate(registers, q)), hll.Count(),
                "saturated registers present: the estimate must still be equation 32.");

            Array.Clear(registers);
            Assert.AreEqual(0UL, hll.Count(),
                "all-zero registers: sigma(1) diverges and the estimate must be " +
                "exactly zero, not merely small.");
        }

        /// <summary>
        /// The state where tau genuinely carries the estimate. At precision 12 the
        /// tau term is scaled by 2^-52 and contributes twelve orders of magnitude
        /// below the harmonic term -- dropping it entirely is invisible to Count()
        /// there, a fact the first mutation run against these anchors demonstrated.
        /// At precision 18 (q = 46), 194,000 registers at value 46 and the remaining
        /// 68,144 saturated at 47 put tau at 10% of the denominator with the
        /// estimate at 1.62e19, still inside ulong: dropping the tau term moves the
        /// answer to 1.80e19, and feeding tau the count at q instead of q+1 moves it
        /// comparably. The tolerance is 4,096 counts -- two ulps of a double at that
        /// magnitude, against a mutation displacement of 1.8e18.
        /// </summary>
        [TestMethod]
        public void TestTauCarriesTheEstimateWhenRegistersSaturate()
        {
            var hll = new HyperLogLogPlus(18);
            for (int i = 0; i < 40000; i++)
            {
                hll.Add(Encoding.ASCII.GetBytes($"tau-{i}"));
            }
            Assert.IsFalse(hll.IsSparse,
                "40,000 distinct items must force the dense form at precision 18.");

            var registers = hll.DenseRegisterState!;
            var q = 64 - 18;
            for (int i = 0; i < registers.Length; i++)
            {
                registers[i] = (byte)(i < 194000 ? q : q + 1);
            }

            var m = (double)registers.Length;
            var tauTerm = m * ErtlTau(1 - ((m - 194000) / m)) * Math.Pow(2, -q);
            var harmonic = 194000 * Math.Pow(2, -q);
            var expected = 1.0 / (2.0 * Math.Log(2)) * m * m / (tauTerm + harmonic);

            Assert.IsGreaterThan(0.05, tauTerm / (tauTerm + harmonic),
                "tau must carry at least 5% of the denominator, or this row pins " +
                "the harmonic series a second time and the tau mutations survive.");
            Assert.IsLessThan((double)ulong.MaxValue, expected,
                "the crafted estimate must fit a ulong, or saturation hides the " +
                "comparison entirely.");

            Assert.IsLessThanOrEqualTo(4096.0, Math.Abs(hll.Count() - expected),
                "with saturated registers dominating, the estimate must be " +
                "equation 32 with tau evaluated at 1 - C_(q+1)/m, to within two " +
                "ulps of a double at 1.6e19.");
        }
    }
}
