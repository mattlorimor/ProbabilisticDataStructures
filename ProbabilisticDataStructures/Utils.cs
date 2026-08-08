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
        /// derived.  The result will be the same regardless of the endianness of the
        /// architecture.
        /// </summary>
        /// <param name="data">The data bytes to hash.</param>
        /// <param name="algorithm">The hashing algorithm to use.</param>
        /// <returns>A HashKernel</returns>
        public static HashKernelReturnValue HashKernel(byte[] data, HashAlgorithm algorithm)
        {
            Span<byte> sum = stackalloc byte[MaxStackHashSize];
            if (algorithm.TryComputeHash(data, sum, out int written))
            {
                return HashKernelFromHashBytes(sum.Slice(0, written));
            }

            // Digest larger than the stack buffer, which only a non-standard
            // HashAlgorithm produces. Fall back to the allocating path.
            return HashKernelFromHashBytes(algorithm.ComputeHash(data));
        }

        /// <summary>
        /// Returns the upper and lower base hash values from which the k hashes are
        /// derived using the given hash bytes directly.  The result will be the
        /// same regardless of the endianness of the architecture.  Used by a unit
        /// test to confirm the calculation is compatible with the HashKernel from
        /// https://github.com/tylertreat/BoomFilters running in Go.
        /// </summary>
        /// <param name="hashBytes">The hash bytes.</param>
        /// <returns>A HashKernel</returns>
        public static HashKernelReturnValue HashKernelFromHashBytes(byte[] hashBytes)
        {
            return HashKernelFromHashBytes((ReadOnlySpan<byte>)hashBytes);
        }

        /// <summary>
        /// Returns the upper and lower base hash values from which the k hashes are
        /// derived using the given hash bytes directly.  Identical to the array
        /// overload, but accepts a span so callers can hash into a buffer they own
        /// rather than allocating one per call.
        /// </summary>
        /// <param name="hashBytes">The hash bytes.</param>
        /// <returns>A HashKernel</returns>
        public static HashKernelReturnValue HashKernelFromHashBytes(ReadOnlySpan<byte> hashBytes)
        {
            return HashKernelReturnValue.Create(
                HashBytesToUInt32(hashBytes, 0),
                HashBytesToUInt32(hashBytes, 4)
                );
        }

        /// <summary>
        /// Returns the upper and lower base hash values from which the k hashes are
        /// derived.
        /// </summary>
        /// <param name="data">The data bytes to hash.</param>
        /// <param name="algorithm">The hashing algorithm to use.</param>
        /// <returns>A HashKernel</returns>
        public static HashKernel128ReturnValue HashKernel128(byte[] data, HashAlgorithm algorithm)
        {
            Span<byte> sum = stackalloc byte[MaxStackHashSize];
            if (!algorithm.TryComputeHash(data, sum, out int written))
            {
                // Digest larger than the stack buffer, which only a non-standard
                // HashAlgorithm produces. Fall back to the allocating path.
                byte[] allocated = algorithm.ComputeHash(data);
                return HashKernel128ReturnValue.Create(
                    HashBytesToUInt64(allocated, 0),
                    HashBytesToUInt64(allocated, 8)
                    );
            }

            return HashKernel128ReturnValue.Create(
                HashBytesToUInt64(sum.Slice(0, written), 0),
                HashBytesToUInt64(sum.Slice(0, written), 8)
                );
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
