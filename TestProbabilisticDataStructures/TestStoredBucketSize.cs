using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// A bucket width read from a payload is the payload's to get wrong, so one that no
    /// bucket has is refused as the payload it came from: an
    /// <see cref="InvalidDataException"/>, as every payload the library will not read is.
    /// </summary>
    /// <remarks>
    /// <see cref="Buckets"/> and <see cref="Buckets64"/> restored a stored width through
    /// the constructors' own guard, an <see cref="System.ArgumentOutOfRangeException"/>
    /// about a parameter no caller passed. A payload with its width edited and its checksum
    /// resealed reached it in every structure built on them. A single flipped bit did not,
    /// because the ordinary envelope's checksum is checked first; the large form's is
    /// checked at the end, and its corruption sweep found it
    /// (<see cref="TestLargeEnvelope.TestEveryBitFlipInTheLargeFormIsRefused"/>).
    /// </remarks>
    [TestClass]
    public class TestStoredBucketSize
    {
        /// <summary>
        /// The restores, and the check the price makes where the run lies, refuse a stored
        /// width of none or more than eight bits as the payload's.
        /// </summary>
        /// <remarks>
        /// Straddles both ends of the widths there are, one to eight: zero and nine, and
        /// the widest a byte claims.
        /// </remarks>
        [TestMethod]
        [DataRow((byte)0)]
        [DataRow((byte)9)]
        [DataRow((byte)255)]
        public void TestARestoredWidthNoBucketHasIsThePayloadsFault(byte bucketSize)
        {
            var restore = Assert.ThrowsExactly<InvalidDataException>(
                () => Buckets.Restore(8, bucketSize, new byte[8]));
            StringAssert.Contains(restore.Message, $"{bucketSize} bits");

            var price = Assert.ThrowsExactly<InvalidDataException>(
                () => Buckets.CheckRestorable(8, bucketSize, 8));
            StringAssert.Contains(price.Message, $"{bucketSize} bits");

            var restore64 = Assert.ThrowsExactly<InvalidDataException>(
                () => Buckets64.Restore(8, bucketSize, new[] { new byte[8] }));
            StringAssert.Contains(restore64.Message, $"{bucketSize} bits");
        }

        /// <summary>
        /// The widths there are still restore, at both ends: the refusal is of the widths
        /// no bucket has, and not of the edges.
        /// </summary>
        [TestMethod]
        [DataRow((byte)1)]
        [DataRow((byte)8)]
        public void TestARestoredWidthABucketHasStillRestores(byte bucketSize)
        {
            Assert.AreEqual(bucketSize, Buckets.Restore(8, bucketSize, new byte[bucketSize]).BucketSize);
            Buckets.CheckRestorable(8, bucketSize, bucketSize);
            Assert.AreEqual(bucketSize, Buckets64.Restore(8, bucketSize, new[] { new byte[bucketSize] }).BucketSize);
        }

        /// <summary>
        /// Through the public read, in the ordinary envelope, the suite's hostile pattern:
        /// one field of a valid payload edited and the checksum resealed. The field is the
        /// width of a Bloom filter's buckets and a 64-bit Bloom filter's, the two bucket
        /// types.
        /// </summary>
        [TestMethod]
        [DataRow((byte)0)]
        [DataRow((byte)9)]
        public void TestAPayloadNamingAWidthNoBucketHasIsRefusedAsThePayloads(byte bucketSize)
        {
            var bloom = new BloomFilter(200, 0.01);
            var bloom64 = new BloomFilter64(200, 0.01);
            for (var i = 0; i < 40; i++)
            {
                bloom.Add(Encoding.ASCII.GetBytes($"w{i}"));
                bloom64.Add(Encoding.ASCII.GetBytes($"w{i}"));
            }

            // The envelope's 14 bytes; then m, k, placement, count and bucket count, or m,
            // k, count and the 64-bit table's bucket count.
            var plain = Edited(bloom.ToByteArray(), 14 + 4 + 4 + 1 + 8 + 4, bucketSize);
            var wide = Edited(bloom64.ToByteArray(), 14 + 8 + 4 + 8 + 8, bucketSize);

            var ex = Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<BloomFilter>(plain));
            StringAssert.Contains(ex.Message, $"{bucketSize} bits");
            var ex64 = Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<BloomFilter64>(wide));
            StringAssert.Contains(ex64.Message, $"{bucketSize} bits");
        }

        private static byte[] Edited(byte[] envelope, int at, byte value)
        {
            Assert.AreEqual((byte)1, envelope[at], "the bucket width is not where this test expects it");
            var bytes = (byte[])envelope.Clone();
            bytes[at] = value;
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(bytes.Length - 4), Crc32.HashToUInt32(bytes.AsSpan(4, bytes.Length - 8)));
            return bytes;
        }
    }
}
