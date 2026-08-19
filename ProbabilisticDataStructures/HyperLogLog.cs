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
using System.IO;
using System.Numerics;
using System.Linq;
using System.Security.Cryptography;

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
    public class HyperLogLog : IBinaryPersistable<HyperLogLog>
    {
        private static double Exp32 = Math.Pow(2, 32);

        /// <summary>
        /// Counter registers
        /// </summary>
        private byte[] Registers { get; set; }

        /// <summary>
        /// Whether any register has been set, which is what decides if the hash
        /// can still be replaced. Derived rather than tracked, so that an
        /// estimator read back from a payload answers this the way the one that
        /// wrote it would.
        /// </summary>
        private bool IsEmpty
        {
            get
            {
                foreach (var register in this.Registers)
                {
                    if (register != 0)
                    {
                        return false;
                    }
                }

                return true;
            }
        }
        /// <summary>
        /// Number of registers
        /// </summary>
        internal uint M { get; set; }

        /// <summary>
        /// The raw registers, so tests can put the estimator into a crafted state
        /// and hold Count() to the paper's printed formulas.
        /// </summary>
        internal byte[] RegisterState => this.Registers;
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
        private Func<ReadOnlySpan<byte>, ulong> Hash { get; set; } = null!;

        /// <summary>
        /// Creates a new HyperLogLog with m registers. Returns an error if m isn't a
        /// power of two.
        /// </summary>
        /// <param name="m">Number of registers (must be a power of two)</param>
        /// <param name="hash">
        /// The hash function to use, or null for the default. Passing it here is the
        /// only way to have one hash cover everything the structure will ever hold:
        /// once anything has been added, the hash can no longer be replaced.
        /// </param>
        public HyperLogLog(uint m, Func<ReadOnlySpan<byte>, ulong>? hash = null)
        {
            // Zero passes the power-of-two test below, because 0 - 1 underflows to
            // all ones and 0 & anything is 0. It would build an estimator with no
            // registers, which then indexes an empty array.
            Guard.ValidItemCount(m, nameof(m));

            if ((m & (m - 1)) != 0)
            {
                throw new ArgumentException(String.Format("{0} is not a power of two", m));
            }

            this.Registers = new byte[m];
            this.M = m;

            // Exact by construction. Computing this as Ceiling(Log(m, 2)) is not:
            // for m = 2^29 the floating-point logarithm lands just above 29, so the
            // ceiling returns 30. That leaves k = 32 - b too small, and the register
            // index derived from it can exceed the register array.
            this.B = (uint)BitOperations.Log2(m);
            this.Alpha = CalculateAlpha(m);
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();
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
            ArgumentNullException.ThrowIfNull(data);

            return this.Add(data.AsSpan());
        }

        /// <inheritdoc cref="Add(byte[])"/>
        public HyperLogLog Add(ReadOnlySpan<byte> data)
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
            ArgumentNullException.ThrowIfNull(other);

            if (this.M != other.M)
            {
                throw new ArgumentException("Number of registers must match", nameof(other));
            }

            Guard.SameHashFunction(this.Hash, other.Hash, nameof(other));

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
        /// Writes this estimator to a stream, in the format documented in FORMAT.md.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = new PayloadWriter();
            payload.WriteUInt32(this.M);
            payload.WriteBytes(this.Registers);

            PersistenceFormat.Write(
                stream,
                StructureId.HyperLogLog,
                PersistenceFormat.Identify(this.Hash),
                payload.WrittenSpan);
        }

        /// <summary>
        /// Reads an estimator written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The estimator that was written.</returns>
        public static HyperLogLog ReadFrom(Stream stream)
        {
            return Read(stream, null);
        }

        /// <summary>
        /// Reads an estimator written by <see cref="WriteTo"/>, using the supplied hash
        /// function rather than the one named in the payload.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function the estimator was written with.</param>
        /// <returns>The estimator that was written.</returns>
        public static HyperLogLog ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            return Read(stream, hash);
        }

        private static HyperLogLog Read(Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(stream, StructureId.HyperLogLog, out var hashId);
            var reader = new PayloadReader(payload);

            var m = reader.ReadUInt32();
            var registers = reader.ReadBytes();
            reader.ExpectEnd();

            if (m == 0 || (m & (m - 1)) != 0)
            {
                throw new InvalidDataException(
                    $"Estimator has {m} registers, which is not a power of two. The " +
                    "register index is taken from the top bits of a hash, so a count " +
                    "that is not a power of two cannot be indexed.");
            }

            if (registers.Length != m)
            {
                throw new InvalidDataException(
                    $"Estimator has {m} registers by its own account and {registers.Length} " +
                    "stored.");
            }

            // Built through the constructor, so that b and alpha are derived exactly the
            // way a fresh estimator derives them, rather than trusted from the payload.
            // b was computed wrongly before 3.1.0, and storing it would have kept that.
            var hyperLogLog = new HyperLogLog(m)
            {
                Hash = PersistenceFormat.ResolveOrThrow(hashId, hash),
            };

            registers.CopyTo(hyperLogLog.Registers, 0);
            return hyperLogLog;
        }

        /// <summary>
        /// Sets the hashing function used in the filter.
        /// </summary>
        /// <param name="h">The hash function to use.</param>
        public void SetHash(Func<ReadOnlySpan<byte>, ulong> h)
        {
            ArgumentNullException.ThrowIfNull(h);
            Guard.HashMayBeReplaced(this.IsEmpty, nameof(HyperLogLog));

            this.Hash = h;
        }

        /// <summary>
        /// Returns a 32-bit hash value for the given data.
        /// </summary>
        /// <param name="data">Data</param>
        /// <returns>32-bit hash value</returns>
        private uint CalculateHash(ReadOnlySpan<byte> data)
        {
            return (uint)(this.Hash(data) & 0xffffffff);
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
