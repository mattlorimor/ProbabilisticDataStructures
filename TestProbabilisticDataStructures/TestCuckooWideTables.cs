using System;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// A <see cref="CuckooBloomFilter"/> past 2^31 entries (#231): its sizes are 64 bits, its
    /// entries are numbered in 64 bits, and its buckets are chosen from a 64-bit index.
    /// </summary>
    /// <remarks>
    /// A table past 2^31 entries is gigabytes, and no test here builds one. What can be held
    /// at small scale is the arithmetic that places an entry, at the indices where 32 bits
    /// wrap, each through the function the filter itself calls; the sizing, through the
    /// function the constructor calls; and the sizes a payload carries, read back from a
    /// small table. The reader's limit, past the old one, is in
    /// <see cref="TestCuckooDenseLayout"/> and <see cref="TestLoadWhatItBuilds"/>, and the
    /// writer's count of the largest table in <see cref="TestTheStreamSavesAsBuilt"/>.
    /// <para>
    /// The pair of buckets is anchored to Fan, Andersen, Kaminsky and Mitzenmacher, "Cuckoo
    /// Filter: Practically Better Than Bloom", CoNEXT 2014, section 3.1, equations (1) and (2)
    /// and Algorithm 1, read on 2026-11-05: h1(x) = hash(x), h2(x) = h1(x) XOR hash(x's
    /// fingerprint), and an entry in bucket i has its other bucket at i XOR hash(fingerprint).
    /// The paper does not say how wide an index is; that it is the hash's 64 bits, reduced by
    /// a power-of-two bucket count, is this library's.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestCuckooWideTables
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        /// <summary>
        /// An entry's number is its bucket times the entries in each, plus its place, in 64
        /// bits. At 2^30 buckets of four, the first entry of the next bucket is 2^32, which 32
        /// bits number as nought: the table's first entry, by construction. The others are
        /// the last entry of the largest table a <c>u32</c> bucket count names, and the largest
        /// number the arithmetic reaches without wrapping a <c>u64</c>.
        /// </summary>
        [TestMethod]
        public void TestAnEntryIsNumberedInSixtyFourBits()
        {
            Assert.AreEqual(1UL << 32, CuckooBloomFilter.EntryNumber(1UL << 30, 0, 4));
            Assert.AreEqual((1UL << 33) + 3, CuckooBloomFilter.EntryNumber(1UL << 31, 3, 4));
            Assert.AreEqual((1UL << 34) - 1, CuckooBloomFilter.EntryNumber(uint.MaxValue, 3, 4));
            Assert.AreEqual(ulong.MaxValue, CuckooBloomFilter.EntryNumber((1UL << 62) - 1, 3, 4));

            // And where nothing wraps, the numbering the table has always had.
            Assert.AreEqual(0UL, CuckooBloomFilter.EntryNumber(0, 0, 4));
            Assert.AreEqual(4UL * 31 + 2, CuckooBloomFilter.EntryNumber(31, 2, 4));
        }

        /// <summary>
        /// An entry's fingerprint begins at bit entry × width of the packed words, worked out in
        /// 64 bits. At ten bits, entry 429,496,730 begins at bit 2^32 + 4, which 32 bits put at
        /// bit 4, in the table's first word: word 2^26, bit 4, by construction. The map is a
        /// table of one-bit entries, and entry 2^32 + 65 of it is bit 1 of word 2^26 + 1, which
        /// 32 bits put in word 1. The last entry of the largest ten-bit table, 2^31 buckets of
        /// four, is the last bits of its last word.
        /// </summary>
        [TestMethod]
        public void TestAFingerprintIsAddressedInSixtyFourBits()
        {
            Assert.AreEqual((1 << 26, 4), CuckooBloomFilter.BitOf(429_496_730, 10));
            Assert.AreEqual((1 << 26) + 1, CuckooBloomFilter.BitOf((1UL << 32) + 65, 1).Word);
            Assert.AreEqual(1, CuckooBloomFilter.BitOf((1UL << 32) + 65, 1).Offset);
            Assert.AreEqual((1_342_177_279, 54), CuckooBloomFilter.BitOf((1UL << 33) - 1, 10));
            Assert.AreEqual(((1 << 28) - 1, 63), CuckooBloomFilter.BitOf((1UL << 34) - 1, 1));

            // And where nothing wraps, the packing the table has always had.
            Assert.AreEqual((0, 0), CuckooBloomFilter.BitOf(0, 10));
            Assert.AreEqual((1, 6), CuckooBloomFilter.BitOf(7, 10));
            Assert.AreEqual((3, 0), CuckooBloomFilter.BitOf(3, 64));
        }

        /// <summary>
        /// A bucket is the index reduced by the bucket count, a power of two, which is the
        /// index's low bits. Past 2^32 buckets the bits above the low 32 choose too: an index
        /// of 2^32 + 5 among 2^33 buckets is bucket 2^32 + 5, which 32 bits would put in bucket
        /// 5. At up to 2^32 buckets it is the bucket the low 32 bits of the index give, which
        /// is where every entry stored before the index was 64 bits lives.
        /// </summary>
        [TestMethod]
        public void TestABucketIsChosenByEveryBitTheTableHas()
        {
            var index = (1UL << 32) + 5;
            Assert.AreEqual((1UL << 32) + 5, CuckooBloomFilter.BucketOf(index, 1UL << 33));
            Assert.AreEqual(5UL, CuckooBloomFilter.BucketOf(index, 1UL << 32));
            Assert.AreEqual(0UL, CuckooBloomFilter.BucketOf(index, 1));

            foreach (var buckets in new ulong[] { 1, 2, 32, 1UL << 31, 1UL << 32, 1UL << 33, 1UL << 40, 1UL << 63 })
            {
                foreach (var i in new ulong[] { 0, 5, 0xDEAD_BEEF_0123_4567, ulong.MaxValue })
                {
                    Assert.AreEqual(i % buckets, CuckooBloomFilter.BucketOf(i, buckets), $"{i} among {buckets}");
                    if (buckets <= 1UL << 32)
                    {
                        Assert.AreEqual((uint)i % buckets, CuckooBloomFilter.BucketOf(i, buckets),
                            $"{i} among {buckets} is not where its low 32 bits put it");
                    }
                }
            }
        }

        /// <summary>
        /// An item's two buckets are the paper's: the hash of the item, and that XOR the hash of
        /// its fingerprint, each all 64 bits the hash gives (section 3.1, equation 1); and the
        /// other bucket of either is found from it and the fingerprint alone (equation 2). The
        /// hash is worked out here as the paper states it, over the item's digest and the
        /// fingerprint's bytes, and at least one item's index has a bit above the low 32 set,
        /// which an index of 32 bits would have dropped.
        /// </summary>
        [TestMethod]
        public void TestAnItemsBucketsAreThePapersInSixtyFourBits()
        {
            static ulong Hash(ReadOnlySpan<byte> data) => XxHash64.HashToUInt64(data);

            var filter = new CuckooBloomFilter(1000, 0.01, Hash, seed: 1);
            var high = 0;
            for (var i = 0; i < 64; i++)
            {
                var item = Key($"wide-{i}");
                var digest = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(digest, Hash(item));
                var fingerprint = Hash(item) & ((1UL << (int)filter.FingerprintBits) - 1);
                var fingerprintBytes = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(fingerprintBytes, fingerprint);
                var ofFingerprint = Hash(fingerprintBytes.AsSpan(0, (int)((filter.FingerprintBits + 7) / 8)));

                var components = filter.GetComponents(item);
                Assert.AreEqual(fingerprint, components.Fingerprint, $"item {i}: the fingerprint");
                Assert.AreEqual(Hash(digest), components.Hash1, $"item {i}: h1(x) = hash(x)");
                Assert.AreEqual(components.Hash1 ^ ofFingerprint, components.Hash2, $"item {i}: h2(x) = h1(x) XOR hash(f)");
                Assert.AreEqual(components.Hash1, components.Hash2 ^ ofFingerprint, $"item {i}: the other bucket of h2(x)");
                if (components.Hash1 >> 32 != 0)
                {
                    high++;
                }
            }

            Assert.IsGreaterThan(0, high, "no index had a bit above the low 32, so their width was never seen");
        }

        /// <summary>
        /// The constructor sizes in 64 bits: the item after the old ceiling, 2,040,109,465 at
        /// 1%, gets 2^30 buckets, where it was refused, and 2^32 items, which no <c>uint</c>
        /// holds, get 2^31; both are tables the reader takes. Sized by the function the
        /// constructor calls, without building either.
        /// </summary>
        [TestMethod]
        public void TestTheConstructorSizesTablesPastTheOldCeiling()
        {
            var past = CuckooBloomFilter.Size(2_040_109_466, 0.01);
            Assert.AreEqual(1UL << 30, past.Buckets);
            Assert.AreEqual(10u, past.Bits);
            Assert.IsNull(CuckooBloomFilter.GeometryProblem(past.Buckets, 4, past.Bits));
            Assert.AreEqual(1UL << 29, CuckooBloomFilter.Size(2_040_109_465, 0.01).Buckets, "the old ceiling moved");

            var wide = CuckooBloomFilter.Size(1UL << 32, 0.01);
            Assert.AreEqual(1UL << 31, wide.Buckets);
            Assert.IsNull(CuckooBloomFilter.GeometryProblem(wide.Buckets, 4, wide.Bits));

            // And the smallest, which a filter of one item gets.
            Assert.AreEqual(1UL, CuckooBloomFilter.Size(1, 0.01).Buckets);
            Assert.AreEqual(1UL, new CuckooBloomFilter(1, 0.01).BucketCount());
        }

        /// <summary>
        /// The capacity is a <c>u64</c> in the payload and in the filter: a small table whose
        /// capacity is set past what a <c>uint</c> holds reads, reports it whole, and saves it
        /// again. The capacity is what the filter was sized for and nothing checks it against
        /// the table, so it is the one size a small payload can carry past 2^32.
        /// </summary>
        [TestMethod]
        public void TestACapacityPastWhatAUintHoldsReadsAndSavesWhole()
        {
            var bytes = new CuckooBloomFilter(100, 0.01, seed: 1).ToByteArray();
            var wide = (byte[])bytes.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(wide.AsSpan(14 + 24), (1UL << 32) + 7);
            BinaryPrimitives.WriteUInt32LittleEndian(wide.AsSpan(wide.Length - 4),
                Crc32.HashToUInt32(wide.AsSpan(4, wide.Length - 8)));

            var read = Persistence.FromByteArray<CuckooBloomFilter>(wide);
            Assert.AreEqual((1UL << 32) + 7, read.Capacity());
            CollectionAssert.AreEqual(wide, read.ToByteArray(), "the capacity did not save as it was read");
            Assert.AreEqual(100UL, Persistence.FromByteArray<CuckooBloomFilter>(bytes).Capacity());
        }
    }
}
