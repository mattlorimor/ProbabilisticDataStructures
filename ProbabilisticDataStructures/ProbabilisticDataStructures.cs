using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ProbabilisticDataStructures
{
    public static class ProbabilisticDataStructures
    {
        const double FILL_RATIO = 0.5;

        /// <summary>
        /// Calculates the optimal Bloom filter size, m, based on the number of items and
        /// the desired rate of false positives.
        /// </summary>
        /// <param name="n">Number of items.</param>
        /// <param name="fpRate">Desired false positive rate.</param>
        /// <returns>The optimal BloomFilter size, m.</returns>
        public static int OptimalM(int n, double fpRate)
        {
            var optimalM = Math.Ceiling((double)n / ((Math.Log(FILL_RATIO) *
                Math.Log(1 - FILL_RATIO)) / Math.Abs(Math.Log(fpRate))));
            return Convert.ToInt32(optimalM);
        }

        /// <summary>
        /// Calculates the optimal number of hash functions to use for a Bloom filter
        /// based on the desired rate of false positives.
        /// </summary>
        /// <param name="fpRate">Desired false positive rate.</param>
        /// <returns>The optimal number of hash functions, k.</returns>
        public static int OptimalK(double fpRate)
        {
            var optimalK = Math.Ceiling(Math.Log(1 / fpRate, 2));
            return Convert.ToInt32(optimalK);
        }

        //public static Tuple<int, int> HashKernel(byte[] data)
        //{

        //}

        private static string CreateMD5(byte[] inputBytes)
        {
            // Use input string to calculate MD5 hash
            MD5 md5 = System.Security.Cryptography.MD5.Create();
            byte[] hashBytes = md5.ComputeHash(inputBytes);

            // Convert the byte array to hexadecimal string
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < hashBytes.Length; i++)
            {
                sb.Append(hashBytes[i].ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
