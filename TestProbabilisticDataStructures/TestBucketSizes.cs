using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The size arithmetic of the packed bucket array, which the counting, stable,
    /// plain, partitioned and deletable Bloom filters all stand on.
    /// </summary>
    /// <remarks>
    /// The array's byte length was worked out as <c>(count * bucketSize + 7) / 8</c> in
    /// <c>uint</c>, so it wrapped as soon as the bits passed 2^32 -- 512 MiB of buckets.
    /// A filter sized past that was built with an array of a few megabytes, or none, and
    /// failed on the first add that landed beyond it, after setting the buckets before it.
    /// Measured on the tree this was written against: 448,089,842 items at 1% in a
    /// <c>BloomFilter</c> allocated no bits at all; 117,623,547 items in a
    /// <c>CountingBloomFilter</c> of four-bit counters allocated 26.8 MB of the 564 MB it
    /// needs, and 200 of 200 adds threw; and a <c>StableBloomFilter</c> of 1,431,655,763
    /// three-bit cells allocated none.
    /// <para>
    /// The structure works wherever its bytes fit one array and its bit offsets are
    /// counted in 64 bits, so that is where it stops: <see cref="Array.MaxLength"/> bytes.
    /// For one-bit buckets that is more than the count type holds and the count is the
    /// limit; from four bits up the array is. Everything here is checked through the
    /// arithmetic, because a bucket array that tests the arithmetic is gigabytes.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestBucketSizes
    {
        /// <summary>
        /// The fewest bytes the array needs, restated in 64 bits. The rows that wrapped
        /// are the first five: the count of buckets whose bits come to exactly 2^32,
        /// to within seven of it, and to well past it.
        /// </summary>
        [TestMethod]
        [DataRow(4_294_967_294u, (byte)1, 536_870_912L)]
        [DataRow(1_073_741_824u, (byte)4, 536_870_912L)]
        [DataRow(1_127_428_565u, (byte)4, 563_714_283L)]
        [DataRow(1_431_655_763u, (byte)3, 536_870_912L)]
        [DataRow(536_870_912u, (byte)8, 536_870_912L)]
        [DataRow(536_870_913u, (byte)8, 536_870_913L)]
        [DataRow(2_147_483_591u, (byte)8, 2_147_483_591L)]
        [DataRow(4_294_967_182u, (byte)4, 2_147_483_591L)]
        [DataRow(4_294_967_295u, (byte)1, 536_870_912L)]
        // These did not wrap, and are the sizes that already worked.
        [DataRow(536_870_911u, (byte)8, 536_870_911L)]
        [DataRow(1_431_655_762u, (byte)3, 536_870_911L)]
        [DataRow(9_585u, (byte)1, 1_199L)]
        [DataRow(100u, (byte)4, 50L)]
        [DataRow(3u, (byte)3, 2L)]
        public void TestTheBytesABucketArrayNeedsAreCountedInSixtyFourBits(
            uint count, byte bucketSize, long bytes)
        {
            Assert.AreEqual(bytes, Buckets.DataLength(count, bucketSize),
                $"{count} buckets of {bucketSize} bits are {(ulong)count * bucketSize} " +
                "bits, which the array must hold whole.");
        }

        /// <summary>
        /// Every size that was sized correctly before is sized the same now: where the
        /// bits stay under 2^32 - 7, the old expression and the new agree. Swept along
        /// the whole width range at counts up to that edge, with the edge itself in it.
        /// </summary>
        [TestMethod]
        public void TestEverySizeThatWasCorrectlyCountedIsCountedTheSame()
        {
            const ulong LastGoodBits = (1UL << 32) - 8;
            var compared = 0;
            var atTheEdge = 0;

            for (byte width = 1; width <= 8; width++)
            {
                var edge = (uint)(LastGoodBits / width);
                foreach (var count in new[] { 1u, 7u, 8u, 9u, 1000u, edge / 3, edge - 1, edge })
                {
                    var was = (count * width + 7) / 8;
                    Assert.AreEqual((long)was, Buckets.DataLength(count, width),
                        $"{count} buckets of {width} bits were {was} bytes and are not now");
                    compared++;
                    if (count == edge)
                    {
                        atTheEdge++;
                    }
                }
            }

            Assert.AreEqual(8, atTheEdge, "the sweep never reached the edge of each width");
            Assert.IsGreaterThan(8, compared);
        }

        /// <summary>
        /// A bucket's first bit is its index times its width, and past a bucket whose bits
        /// begin at 2^32 that does not fit a uint. Get, Set and Increment all go through
        /// it, so a bucket array of more than 512 MiB indexes the wrong bytes without it.
        /// </summary>
        [TestMethod]
        [DataRow(0x2000_0000u, (byte)8, 0x1_0000_0000UL)]
        [DataRow(0x4000_0000u, (byte)4, 0x1_0000_0000UL)]
        [DataRow(1_431_655_766u, (byte)3, 4_294_967_298UL)]
        [DataRow(uint.MaxValue, (byte)8, 34_359_738_360UL)]
        [DataRow(uint.MaxValue, (byte)1, 4_294_967_295UL)]
        [DataRow(7u, (byte)3, 21UL)]
        [DataRow(0u, (byte)8, 0UL)]
        public void TestABucketsFirstBitIsCountedInSixtyFourBits(
            uint bucket, byte bucketSize, ulong offset)
        {
            Assert.AreEqual(offset, Buckets.BitOffset(bucket, bucketSize));
        }

        /// <summary>
        /// The most buckets one array holds at each width: every byte of it, which is
        /// <see cref="Array.MaxLength"/>, in eight bits apiece -- or the count type's
        /// limit, where that comes first. The numbers are worked out here by hand
        /// (2,147,483,591 x 8 = 17,179,868,728 bits, over the width), not read from the
        /// code, and one bucket more is refused naming the parameter it came from.
        /// </summary>
        [TestMethod]
        [DataRow((byte)1, uint.MaxValue)]
        [DataRow((byte)2, uint.MaxValue)]
        [DataRow((byte)3, uint.MaxValue)]
        [DataRow((byte)4, 4_294_967_182u)]
        [DataRow((byte)5, 3_435_973_745u)]
        [DataRow((byte)6, 2_863_311_454u)]
        [DataRow((byte)7, 2_454_266_961u)]
        [DataRow((byte)8, 2_147_483_591u)]
        public void TestAnArrayHoldsNoMoreBucketsThanItsBytesCanBe(byte width, uint largest)
        {
            Assert.AreEqual(2_147_483_591, Array.MaxLength,
                "the limits above were worked out against a different array length");

            Buckets.CheckSize(largest, width, "count");
            Assert.IsLessThanOrEqualTo((long)Array.MaxLength, Buckets.DataLength(largest, width),
                "the largest count does not fit the array");

            if (largest == uint.MaxValue)
            {
                return;
            }

            var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => Buckets.CheckSize(largest + 1, width, "count"));
            Assert.AreEqual("count", ex.ParamName);
            StringAssert.Contains(ex.Message, Array.MaxLength.ToString("N0"),
                "the refusal should name the array length that is the limit");
            Assert.IsGreaterThan((long)Array.MaxLength, Buckets.DataLength(largest + 1, width),
                "one more bucket should not fit the array");
        }

        /// <summary>
        /// The array refuses a size itself, not only through the filters that ask first: a
        /// bucket count one past the limit is refused by the constructor, against its own
        /// count, before the array it would have allocated -- two gibibytes at the widths
        /// that bite.
        /// </summary>
        [TestMethod]
        [DataRow((byte)4, 4_294_967_182u)]
        [DataRow((byte)5, 3_435_973_745u)]
        [DataRow((byte)8, 2_147_483_591u)]
        public void TestTheConstructorRefusesMoreBucketsThanAnArrayHolds(byte width, uint largest)
        {
            var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new Buckets(largest + 1, width));
            Assert.AreEqual("count", ex.ParamName);
        }

        /// <summary>
        /// The widths whose limit is the count type, not the array, are exactly the ones
        /// that never refuse: this keeps the test above from passing on a limit that was
        /// set so high nothing reaches it.
        /// </summary>
        [TestMethod]
        public void TestTheArrayLimitBitesFromFourBitsUp()
        {
            for (byte width = 1; width <= 8; width++)
            {
                var bites = false;
                try
                {
                    Buckets.CheckSize(uint.MaxValue, width, "count");
                }
                catch (ArgumentOutOfRangeException)
                {
                    bites = true;
                }

                Assert.AreEqual(width >= 4, bites,
                    $"{width}-bit buckets: the most the count type holds " +
                    (bites ? "was refused" : "was accepted") + " and that is the wrong way round");
            }
        }

        /// <summary>
        /// A counting filter is refused past the counters a 64-bit table holds,
        /// <c>Buckets64.MaxCount</c>, and the refusal is against <c>n</c>, which is what the
        /// caller chose: the first item count that sizes past it is refused, and the one
        /// before it sizes within it, at four-bit and eight-bit counters alike, since the
        /// table is limited by how many arrays it holds and not by their bytes. Until #232
        /// the counters were one array, and an eight-bit filter of 2,147,483,592 counters,
        /// about 224 million items at 1%, was refused.
        /// </summary>
        /// <remarks>
        /// The item count before the edge is not built, which would take 2^61 bytes: its size
        /// is held to the check the constructor makes, which is what refuses the one at the
        /// edge. A constructor that left the check to the table would refuse naming the
        /// table's parameter rather than <c>n</c>.
        /// </remarks>
        [TestMethod]
        [DataRow((byte)4)]
        [DataRow((byte)8)]
        public void TestACountingFilterBeyondItsTableIsRefusedNamingN(byte b)
        {
            const double Rate = 0.01;
            var n = TestBitPlacement.SmallestCountReaching64(Buckets64.MaxCount + 1, Rate);
            Assert.IsGreaterThan(Buckets64.MaxCount, Utils.OptimalM64(n, Rate));
            Assert.IsLessThanOrEqualTo(Buckets64.MaxCount, Utils.OptimalM64(n - 1, Rate),
                "the item count before this one should still fit");
            Buckets64.CheckSize(Utils.OptimalM64(n - 1, Rate), b, "n");

            var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new CountingBloomFilter(n, b, Rate));
            Assert.AreEqual("n", ex.ParamName);
            StringAssert.Contains(ex.Message, Buckets64.MaxCount.ToString("N0"));
        }

        /// <summary>
        /// A stable filter's cells are its own parameter, and the refusal is against it.
        /// Its cells have been a 64-bit table since #232, so the line is the table's: one
        /// cell past <see cref="Buckets64.MaxCount"/> is refused naming <c>m</c>, by the
        /// constructor and by the factory of an unstable filter, and the count at the line
        /// passes the check both make. Until then two to the 31st eight-bit cells, which
        /// one array cannot hold, was refused, and before that it wrapped to no array.
        /// </summary>
        [TestMethod]
        [DataRow((byte)1)]
        [DataRow((byte)8)]
        public void TestAStableFilterBeyondItsTableIsRefusedNamingM(byte d)
        {
            var past = Buckets64.MaxCount + 1;
            Buckets64.CheckSize(Buckets64.MaxCount, d, "m");
            var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new StableBloomFilter(past, d, 0.01, seed: 1));
            Assert.AreEqual("m", ex.ParamName);
            StringAssert.Contains(ex.Message, Buckets64.MaxCount.ToString("N0"));

            var unstable = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => StableBloomFilter.NewUnstableBloomFilter(past, 0.01));
            Assert.AreEqual("m", unstable.ParamName);
            StringAssert.Contains(unstable.Message, Buckets64.MaxCount.ToString("N0"));
        }

        /// <summary>
        /// A reader never sees a size the constructor refuses, so a payload claiming
        /// one is refused as the beyond-anything-built payload it is, whatever bytes it
        /// holds.
        /// </summary>
        [TestMethod]
        public void TestRestoreRefusesMoreBucketsThanAnArrayHolds()
        {
            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Buckets.Restore(2_147_483_592u, 8, Array.Empty<byte>()));
            StringAssert.Contains(ex.Message, "beyond anything this library builds");

            // The count itself is not the problem at a width that holds it.
            var fits = Assert.ThrowsExactly<InvalidDataException>(
                () => Buckets.Restore(2_147_483_592u, 4, Array.Empty<byte>()));
            Assert.DoesNotContain("beyond anything this library builds", fits.Message);
        }

        /// <summary>
        /// A payload an earlier version wrote for a filter this large holds the table
        /// that version allocated: its byte count worked out in 32 bits, and wrapped. The
        /// table is not the filter it describes, and nothing in it says which buckets it
        /// lost, so it is not read; the refusal says what happened rather than only that
        /// the length is wrong. Each row is the length the old arithmetic gave.
        /// </summary>
        [TestMethod]
        [DataRow(4_294_967_294u, (byte)1, 0)]
        [DataRow(1_431_655_763u, (byte)3, 0)]
        [DataRow(536_873_220u, (byte)8, 2_308)]
        [DataRow(1_127_428_565u, (byte)4, 26_843_371)]
        public void TestRestoreExplainsATableAnEarlierVersionWrappedShort(
            uint count, byte bucketSize, int stored)
        {
            var legacy = (count * bucketSize + 7) / 8;
            Assert.AreEqual((uint)stored, legacy,
                "the row is not the length the old arithmetic gave");

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Buckets.Restore(count, bucketSize, new byte[stored]));
            StringAssert.Contains(ex.Message, "32 bits");
            StringAssert.Contains(ex.Message, count.ToString("N0"));
        }

        /// <summary>
        /// The 64-bit array of arrays has its own limit, and it is a long way off: one
        /// array of 2^30 bytes per 2^30 buckets (which is the format's, see FORMAT.md),
        /// held in an outer array, so at most <see cref="Array.MaxLength"/> of them. A
        /// count past that was built with the outer array's length cast to an int, which
        /// wrapped to a handful of arrays or to a negative number; and no machine holds a
        /// filter a thousandth the size. It is checked here because it is the same
        /// arithmetic, and because the alternative is a count that builds a filter of
        /// one gigabyte for 2^62 bits.
        /// </summary>
        [TestMethod]
        public void TestSixtyFourBitBucketsStopWhereTheirArraysDo()
        {
            var largest = (ulong)Array.MaxLength * (1UL << 30) - 1;

            Buckets64.CheckSize(largest, 1, "count");
            var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => Buckets64.CheckSize(largest + 1, 1, "count"));
            Assert.AreEqual("count", ex.ParamName);
            StringAssert.Contains(ex.Message, Array.MaxLength.ToString("N0"));

            // At the widest bucket the bits are still a ulong: the limit is the arrays.
            Buckets64.CheckSize(largest, 8, "count");
        }

        /// <summary>
        /// A reader past the limit of the 64-bit array is refused for it, whatever shape of
        /// arrays it carries: the arrays it would need are more than there can be, and
        /// without that check their count is worked out from a number that does not fit.
        /// </summary>
        [TestMethod]
        public void TestRestoreRefusesMoreSixtyFourBitBucketsThanItsArraysHold()
        {
            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Buckets64.Restore(Buckets64.MaxCount + 1, 1, Array.Empty<byte[]>()));
            StringAssert.Contains(ex.Message, "beyond anything this library builds");
        }

        /// <summary>
        /// The first item count whose sizing does not fit a ulong is refused as a mistake in
        /// the count, and the one before it is sized. The sizing rounds to a double, so the
        /// first refused count sizes to exactly 2^64, which is the edge a comparison that
        /// let it through would fail on, with an OverflowException from the conversion.
        /// </summary>
        [TestMethod]
        public void TestTheFirstItemCountTooLargeToSizeInBitsIsRefusedNamingN()
        {
            bool Refused(ulong n)
            {
                try
                {
                    Utils.OptimalM64(n, 0.01);
                    return false;
                }
                catch (Exception)
                {
                    return true;
                }
            }

            ulong low = 1;
            ulong high = ulong.MaxValue;
            while (low < high)
            {
                var mid = low + (high - low) / 2;
                if (Refused(mid))
                {
                    high = mid;
                }
                else
                {
                    low = mid + 1;
                }
            }

            Assert.IsTrue(Refused(low), "no item count was refused, so this found no edge");
            Assert.IsFalse(Refused(low - 1));
            Assert.AreEqual("n", Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => Utils.OptimalM64(low, 0.01)).ParamName);
        }

        /// <summary>
        /// A sixty-four-bit Bloom filter asked for more bits than that is refused
        /// against <c>n</c>. The first count is one whose bits are over 2^61 and under
        /// 2^62, where the old arithmetic cast an array length to a negative int and
        /// failed with an OverflowException from inside the allocation; the second is
        /// one whose sizing does not fit a ulong at all, which failed the same way a
        /// step earlier.
        /// </summary>
        [TestMethod]
        [DataRow(300_000_000_000_000_000UL)]
        [DataRow(ulong.MaxValue)]
        public void TestABloomFilter64BeyondItsArraysIsRefusedNamingN(ulong n)
        {
            var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new BloomFilter64(n, 0.01));
            Assert.AreEqual("n", ex.ParamName);
        }

        /// <summary>
        /// Increment took its bucket as a uint, and worked out the bit it began at in a
        /// uint, in a type whose buckets are counted by a ulong. Nothing in the library
        /// calls it, which is how it stayed. It does the same as Get and Set now: the
        /// parameter is as wide as the count.
        /// </summary>
        [TestMethod]
        public void TestSixtyFourBitBucketsIncrementTakesABucketAsWideAsTheCount()
        {
            var increment = typeof(Buckets64).GetMethod(
                nameof(Buckets64.Increment),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(increment);
            Assert.AreEqual(typeof(ulong), increment.GetParameters()[0].ParameterType);
        }

        /// <summary>
        /// The explanation is for the wrapped length and nothing else: a table that is
        /// the wrong length for any other reason is a corrupt payload, and says so
        /// without claiming a history it does not have.
        /// </summary>
        [TestMethod]
        [DataRow(100u, (byte)4, 7)]
        [DataRow(100u, (byte)4, 51)]
        [DataRow(4_294_967_294u, (byte)1, 5)]
        [DataRow(536_873_220u, (byte)8, 2_307)]
        public void TestRestoreDoesNotBlameAnEarlierVersionForAnyOtherLength(
            uint count, byte bucketSize, int stored)
        {
            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => Buckets.Restore(count, bucketSize, new byte[stored]));
            Assert.DoesNotContain("32 bits", ex.Message);
            StringAssert.Contains(ex.Message, "were stored");
        }
    }
}
