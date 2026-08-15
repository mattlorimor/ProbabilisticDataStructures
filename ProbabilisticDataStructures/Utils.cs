using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Sizing calculations and hashing helpers shared by the filter implementations.
    /// </summary>
    public static class Utils
    {
        /// <summary>
        /// Size of the stack buffer used when hashing. 64 bytes holds the largest
        /// digest any standard <see cref="HashAlgorithm"/> produces (SHA-512); a
        /// larger one falls back to the allocating path rather than growing the
        /// stack frame.
        /// </summary>
        private const int MaxStackHashSize = 64;

        /// <summary>
        /// Calculates the optimal Bloom filter size, m, based on the number of items and
        /// the desired rate of false positives.
        /// </summary>
        /// <param name="n">Number of items.</param>
        /// <param name="fpRate">Desired false positive rate.</param>
        /// <returns>The optimal BloomFilter size, m.</returns>
        public static uint OptimalM(uint n, double fpRate)
        {
            var optimalM = Math.Ceiling((double)n / ((Math.Log(Defaults.FILL_RATIO) *
                Math.Log(1 - Defaults.FILL_RATIO)) / Math.Abs(Math.Log(fpRate))));

            // A 32-bit filter addresses at most uint.MaxValue bits, which is about 448
            // million items at 1% and fewer at a tighter rate. Left to the conversion
            // this arrives as an OverflowException from inside the sizing arithmetic,
            // which says something true about the machinery and nothing about the
            // mistake or about the two structures that would work.
            if (optimalM > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(n), n,
                    $"Holding {n} items at a false positive rate of {fpRate} needs " +
                    $"{optimalM:N0} bits, and a 32-bit filter addresses at most " +
                    $"{uint.MaxValue:N0}. Use BloomFilter64, which sizes in 64 bits, or " +
                    "ScalableBloomFilter, which grows by adding filters rather than by " +
                    "making one larger.");
            }

            return Convert.ToUInt32(optimalM);
        }

        /// <summary>
        /// Calculates the optimal Bloom filter size, m, based on the number of items and
        /// the desired rate of false positives.
        /// </summary>
        /// <param name="n">Number of items.</param>
        /// <param name="fpRate">Desired false positive rate.</param>
        /// <returns>The optimal BloomFilter size, m.</returns>
        public static ulong OptimalM64(ulong n, double fpRate)
        {
            var optimalM = Math.Ceiling((double)n / ((Math.Log(Defaults.FILL_RATIO) *
                Math.Log(1 - Defaults.FILL_RATIO)) / Math.Abs(Math.Log(fpRate))));
            return Convert.ToUInt64(optimalM);
        }

        /// <summary>
        /// Calculates the optimal number of hash functions to use for a Bloom filter
        /// based on the desired rate of false positives.
        /// </summary>
        /// <param name="fpRate">Desired false positive rate.</param>
        /// <returns>The optimal number of hash functions, k.</returns>
        public static uint OptimalK(double fpRate)
        {
            var optimalK = Math.Ceiling(Math.Log(1 / fpRate, 2));
            return Convert.ToUInt32(optimalK);
        }

        /// <summary>
        /// Returns the upper and lower base hash values from which the k hashes are
        /// derived. The result is the same regardless of the endianness of the
        /// architecture.
        /// </summary>
        /// <param name="data">The data bytes to hash.</param>
        /// <param name="hash">The hash function to use.</param>
        /// <returns>A HashKernel</returns>
        public static HashKernelReturnValue HashKernel(byte[] data, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            return HashKernelFromSum(hash(data));
        }

        /// <summary>
        /// Splits a 64-bit hash into the lower and upper base values.
        /// </summary>
        /// <param name="sum">The 64-bit hash value.</param>
        /// <returns>A HashKernel</returns>
        public static HashKernelReturnValue HashKernelFromSum(ulong sum)
        {
            return HashKernelReturnValue.Create(
                (uint)(sum & 0xffffffff),
                (uint)((sum >> 32) & 0xffffffff));
        }

        /// <summary>
        /// Returns the upper and lower base hash values from which the k hashes are
        /// derived, for filters large enough to need a 128-bit kernel.
        /// </summary>
        /// <remarks>
        /// The hash function supplies 64 bits, so the second value is derived by
        /// hashing the first. This keeps the two halves independent without
        /// requiring callers to provide a 128-bit hash.
        /// </remarks>
        /// <param name="data">The data bytes to hash.</param>
        /// <param name="hash">The hash function to use.</param>
        /// <returns>A HashKernel128</returns>
        public static HashKernel128ReturnValue HashKernel128(byte[] data, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            ulong lower = hash(data);

            Span<byte> lowerBytes = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(lowerBytes, lower);
            ulong upper = hash(lowerBytes);

            return HashKernel128ReturnValue.Create(lower, upper);
        }

        /// <summary>
        /// Returns the upper and lower base hash values for a digest the caller has
        /// already computed.
        /// </summary>
        /// <remarks>
        /// A unit test pins this against Go BoomFilters' convention: given the same
        /// digest bytes, this produces the same lower and upper values Go's
        /// hashKernel derives from a 64-bit sum. That is a statement about the
        /// extraction arithmetic only -- the two libraries hash with different
        /// functions, so their filters are not interchangeable.
        /// </remarks>
        /// <param name="hashBytes">The digest bytes.</param>
        /// <returns>A HashKernel</returns>
        public static HashKernelReturnValue HashKernelFromHashBytes(byte[] hashBytes)
        {
            return HashKernelFromHashBytes((ReadOnlySpan<byte>)hashBytes);
        }

        /// <summary>
        /// Span overload of <see cref="HashKernelFromHashBytes(byte[])"/>.
        /// </summary>
        /// <param name="hashBytes">The digest bytes.</param>
        /// <returns>A HashKernel</returns>
        public static HashKernelReturnValue HashKernelFromHashBytes(ReadOnlySpan<byte> hashBytes)
        {
            return HashKernelReturnValue.Create(
                HashBytesToUInt32(hashBytes, 0),
                HashBytesToUInt32(hashBytes, 4));
        }

        /// <summary>
        /// Returns the uint represented by the given hash bytes, starting at
        /// byte <paramref name="offset"/>.  The result will be the same
        /// regardless of the endianness of the architecture.
        /// </summary>
        /// <param name="hashBytes"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static uint HashBytesToUInt32(byte[] hashBytes, int offset = 0)
        {
            return HashBytesToUInt32((ReadOnlySpan<byte>)hashBytes, offset);
        }

        /// <summary>
        /// Returns the uint represented by the given hash bytes, starting at
        /// byte <paramref name="offset"/>.  The result will be the same
        /// regardless of the endianness of the architecture.
        /// </summary>
        /// <param name="hashBytes"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static uint HashBytesToUInt32(ReadOnlySpan<byte> hashBytes, int offset = 0)
        {
            return
                ((uint)hashBytes[offset]) |
                ((uint)hashBytes[offset + 1]) << 8 |
                ((uint)hashBytes[offset + 2]) << 16 |
                ((uint)hashBytes[offset + 3]) << 24;
        }

        /// <summary>
        /// Returns the ulong represented by the given hash bytes, starting at
        /// byte <paramref name="offset"/>.  The result will be the same
        /// regardless of the endianness of the architecture.
        /// </summary>
        /// <param name="hashBytes"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static ulong HashBytesToUInt64(byte[] hashBytes, int offset = 0)
        {
            return HashBytesToUInt64((ReadOnlySpan<byte>)hashBytes, offset);
        }

        /// <summary>
        /// Returns the ulong represented by the given hash bytes, starting at
        /// byte <paramref name="offset"/>.  The result will be the same
        /// regardless of the endianness of the architecture.
        /// </summary>
        /// <param name="hashBytes"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static ulong HashBytesToUInt64(ReadOnlySpan<byte> hashBytes, int offset = 0)
        {
            return
                ((ulong)hashBytes[offset]) |
                ((ulong)hashBytes[offset + 1]) << 8 |
                ((ulong)hashBytes[offset + 2]) << 16 |
                ((ulong)hashBytes[offset + 3]) << 24 |
                ((ulong)hashBytes[offset + 4]) << 32 |
                ((ulong)hashBytes[offset + 5]) << 40 |
                ((ulong)hashBytes[offset + 6]) << 48 |
                ((ulong)hashBytes[offset + 7]) << 56;
        }

        /// <summary>
        /// Compute the hash for the provided bytes.
        /// </summary>
        /// <param name="inputBytes">The bytes to hash.</param>
        /// <param name="hashAlgorithm">The hashing algorithm to use.</param>
        /// <returns>The hash string of the bytes.</returns>
        public static string ComputeHashAsString(byte[] inputBytes, HashAlgorithm hashAlgorithm)
        {
            // Compute the hash of the input byte array.
            byte[] data = hashAlgorithm.ComputeHash(inputBytes);

            // Create a new StringBuilder to collect the bytes and create a string.
            StringBuilder sb = new StringBuilder();

            // Loop through each byte of the hashed data and format each one as a
            // hexadecimal string.
            for (int i = 0; i < data.Length; i++)
            {
                sb.Append(data[i].ToString("X2"));
            }

            // Return the hexadecimal string.
            return sb.ToString();
        }
    }

    /// <summary>
    /// The pair of 32-bit base hash values from which a filter's k hashes are derived.
    /// </summary>
    public struct HashKernelReturnValue
    {
        /// <summary>
        /// The upper base hash value.
        /// </summary>
        public uint UpperBaseHash { get; private set; }
        /// <summary>
        /// The lower base hash value.
        /// </summary>
        public uint LowerBaseHash { get; private set; }

        /// <summary>
        /// Creates a new <see cref="HashKernelReturnValue"/>.
        /// </summary>
        /// <param name="lowerBaseHash">The lower base hash value.</param>
        /// <param name="upperBaseHash">The upper base hash value.</param>
        /// <returns>A HashKernelReturnValue.</returns>
        public static HashKernelReturnValue Create(uint lowerBaseHash, uint upperBaseHash)
        {
            return new HashKernelReturnValue
            {
                UpperBaseHash = upperBaseHash,
                LowerBaseHash = lowerBaseHash
            };
        }
    }

    /// <summary>
    /// The pair of 64-bit base hash values from which a filter's k hashes are derived,
    /// for filters large enough to need a 128-bit kernel.
    /// </summary>
    public struct HashKernel128ReturnValue
    {
        /// <summary>
        /// The upper base hash value.
        /// </summary>
        public ulong UpperBaseHash { get; private set; }
        /// <summary>
        /// The lower base hash value.
        /// </summary>
        public ulong LowerBaseHash { get; private set; }
        /// <summary>
        /// Creates a new <see cref="HashKernel128ReturnValue"/>.
        /// </summary>
        /// <param name="lowerBaseHash">The lower base hash value.</param>
        /// <param name="upperBaseHash">The upper base hash value.</param>
        /// <returns>A HashKernel128ReturnValue.</returns>
        public static HashKernel128ReturnValue Create(ulong lowerBaseHash, ulong upperBaseHash)
        {
            return new HashKernel128ReturnValue
            {
                UpperBaseHash = upperBaseHash,
                LowerBaseHash = lowerBaseHash,
            };
        }
    }
}
