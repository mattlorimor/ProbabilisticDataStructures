/*
Original work Copyright 2013 Eric Lesh
Modified work Copyright 2015 Tyler Treat
Modified work Copyright 2015 Matthew Lorimor

Permission is hereby granted, free of charge, to any person obtaining
a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// implements the HyperLogLog cardinality estimation algorithm as
    /// described by Flajolet, Fusy, Gandouet, and Meunier in HyperLogLog: the
    /// analysis of a near-optimal cardinality estimation algorithm:
    ///
    /// http://algo.inria.fr/flajolet/Publications/FlFuGaMe07.pdf
    ///
    /// HyperLogLog is a probabilistic algorithm which approximates the number of
    /// distinct elements in a multiset. It works by hashing values and calculating
    /// the maximum number of leading zeros in the binary representation of each
    /// hash. If the maximum number of leading zeros is n, the estimated number of
    /// distinct elements in the set is 2^n. To minimize variance, the multiset is
    /// split into a configurable number of registers, the maximum number of leading
    /// zeros is calculated in the numbers in each register, and a harmonic mean is
    /// used to combine the estimates.
    ///
    /// For large or unbounded data sets, calculating the exact cardinality is
    /// impractical. HyperLogLog uses a fraction of the memory while providing an
    /// accurate approximation. For counting element frequency, refer to the
    /// Count-Min Sketch.
    /// </summary>
    public class HyperLogLog
    {
        private static double Exp32 = Math.Pow(2, 32);

        /// <summary>
        /// Counter registers
        /// </summary>
        private byte[] Registers { get; set; }
        /// <summary>
        /// Number of registers
        /// </summary>
        internal uint M { get; set; }
        /// <summary>
        /// Number of bits to calculate register
        /// </summary>
        private uint B { get; set; }
        /// <summary>
        /// Bias-correction constant
        /// </summary>
        private double Alpha { get; set; }
        /// <summary>
        /// Hash algorithm
        /// </summary>
        private HashAlgorithm Hash { get; set; }

        /// <summary>
        /// Creates a new HyperLogLog with m registers. Returns an error if m isn't a
        /// power of two.
        /// </summary>
        /// <param name="m">Number of registers (must be a power of two)</param>
        public HyperLogLog(uint m)
        {
            if ((m & (m - 1)) != 0)
            {
                throw new ArgumentException(String.Format("{0} is not a power of two", m));
            }

            this.Registers = new byte[m];
            this.M = m;
            this.B = (uint)Math.Ceiling(Math.Log(m, 2));
            this.Alpha = CalculateAlpha(m);
            this.Hash = Defaults.GetDefaultHashAlgorithm();
        }

        /// <summary>
        /// Creates a new HyperLogLog optimized for the specified standard error.
        /// Throws an ArgumentException if the number of registers can't be calculated
        /// for the provided accuracy.
        /// </summary>
        /// <param name="e">Desired standard error</param>
        /// <returns>The HyperLogLog optimized for the standard error</returns>
        public static HyperLogLog NewDefaultHyperLogLog(double e)
        {
            var m = Math.Pow(1.04 / e, 2);
            return new HyperLogLog((uint)Math.Pow(2, Math.Ceiling(Math.Log(m, 2))));
        }

        /// <summary>
        /// Will add the data to the set. Returns the HyperLogLog to allow for chaining.
        /// </summary>
        /// <param name="data">The data to add</param>
        /// <returns>The HyperLogLog</returns>
        public HyperLogLog Add(byte[] data)
        {
            var hash = CalculateHash(data);
            var k = 32 - this.B;
            var r = CalculateRho(hash << (int)this.B, k);
            var j = hash >> (int)k;

            if (r > this.Registers[j])
            {
                this.Registers[j] = r;
            }

            return this;
        }

        /// <summary>
        /// Returns the approximated cardinality of the set.
        /// </summary>
        /// <returns>The approximated cardinality of the set</returns>
        public UInt64 Count()
        {
            var sum = 0.0;
            var m = (double)this.M;
            foreach (var val in this.Registers)
            {
                sum += 1.0 / Math.Pow(2.0, val);
            }
            var estimate = this.Alpha * m * m / sum;
            if (estimate <= 5.0 / 2.0 * m)
            {
                // Small range correction
                var v = 0;
                foreach (var r in this.Registers)
                {
                    if (r == 0)
                    {
                        v++;
                    }
                }
                if (v > 0)
                {
                    estimate = m * Math.Log(m / v);
                }
            }
            else if (estimate > 1.0 / 30.0 * Exp32)
            {
                // Large range correction
                estimate = -Exp32 * Math.Log(1 - estimate / Exp32);
            }
            return (UInt64)estimate;
        }

        /// <summary>
        /// Combines this HyperLogLog with another. Returns an error if the number of
        /// registers in the two HyperLogLogs are not equal.
        /// </summary>
        /// <param name="other">The HyperLogLog to merge</param>
        /// <returns>Whether or not the merge was successful</returns>
        public bool Merge(HyperLogLog other)
        {
            if (this.M != other.M)
            {
                throw new ArgumentException("Number of registers must match");
            }

            for (int i = 0; i < other.Registers.Count(); i++)
            {
                var r = other.Registers[i];
                if (r > this.Registers[i])
                {
                    this.Registers[i] = r;
                }
            }

            return true;
        }

        /// <summary>
        /// Restores the HyperLogLog to its original state. It returns itself to allow
        /// for chaining.
        /// </summary>
        /// <returns>The HyperLogLog</returns>
        public HyperLogLog Reset()
        {
            this.Registers = new byte[this.M];
            return this;
        }

        /// <summary>
        /// Sets the hashing function used in the filter.
        /// </summary>
        /// <param name="h">The HashAlgorithm to use.</param>
        public void SetHash(HashAlgorithm h)
        {
            this.Hash = h;
        }

        /// <summary>
        /// Returns a 32-bit hash value for the given data.
        /// </summary>
        /// <param name="data">Data</param>
        /// <returns>32-bit hash value</returns>
        private uint CalculateHash(byte[] data)
        {
            var hash = new Hash(this.Hash);
            hash.ComputeHash(data);
            var sum = hash.Sum();
            return Utils.ToBigEndianUInt32(sum);
        }

        /// <summary>
        /// Calculates the bias-correction constant alpha based on the number of
        /// registers, m.
        /// </summary>
        /// <param name="m">Number of registers</param>
        /// <returns>Calculated bias-correction constant, alpha</returns>
        private static double CalculateAlpha(uint m)
        {
            switch (m)
            {
                case 16:
                    return 0.673;
                case 32:
                    return 0.697;
                case 64:
                    return 0.709;
                default:
                    return 0.7213 / (1.0 + 1.079 / m);
            }
        }

        /// <summary>
        /// Calculates the position of the leftmost 1-bit.
        /// </summary>
        /// <param name="val">The value to check</param>
        /// <param name="max"></param>
        /// <returns>The position of the leftmost 1-bit</returns>
        private static byte CalculateRho(uint val, uint max)
        {
            var r = 1;
            while ((val & 0x80000000) == 0 && r <= max)
            {
                r++;
                val <<= 1;
            }
            return (byte)r;
        }

        // TODO: Implement these later.
        // WriteDataTo
        // ReadDataFrom
    }
}
