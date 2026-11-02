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
    /// <remarks>
    /// <b>Its range is set by its 32-bit hash.</b> The register index and the run of
    /// zeros both come from the low 32 bits of the hash, so with m = 2^b registers a
    /// register can record a run of at most 32 - b zeros, and holds at most 33 - b.
    /// Items whose 32 bits collide do not bias the estimate: such an item moves the
    /// registers exactly as it would have under a wider hash, unless its run of zeros
    /// reaches the end of the 32 bits. The cap is what limits it. Each register reaches
    /// it with probability 1 - exp(-n/2^32) after n distinct items, whatever m is, and
    /// <see cref="Count"/> accounts for the registers that have. The estimate is close
    /// to its nominal error of 1.04/sqrt(m) up to about 2^33 distinct items, about 8.6
    /// billion, with 16 registers or more; past that it degrades as the registers fill,
    /// and it cannot exceed about 2^32 * ln(m), which below 16 registers is short of
    /// 2^33 itself. <see cref="HyperLogLogPlus"/> and
    /// <see cref="UltraLogLog"/> use the whole 64-bit hash and have no such limit in
    /// practice.
    /// </remarks>
    public class HyperLogLog : IBinaryPersistable<HyperLogLog>
    {
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
        /// The most registers an estimator has: the registers are one array of bytes, and
        /// the largest power of two an array holds is 2^30. The constructor refuses a
        /// count past it; the reader needs no check of its own, since a payload carries
        /// every register it claims and cannot carry 2^31 of them.
        /// </summary>
        internal static uint MaxRegisters => 1u << BitOperations.Log2((uint)Array.MaxLength);

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
        /// <exception cref="ArgumentException">
        /// The count is not a power of two.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The count is zero, or is 2^31, which is more than one array holds. The most is
        /// 2^30, an estimator of a gibibyte.
        /// </exception>
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

            // The one power of two a uint holds past the limit, 2^31, used to fail in the
            // allocation with an OverflowException.
            if (m > MaxRegisters)
            {
                throw new ArgumentOutOfRangeException(nameof(m), m,
                    $"{m} registers is more than one array holds. The registers are an " +
                    $"array of bytes, and the most that is a power of two is {MaxRegisters}.");
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
        /// <exception cref="ArgumentOutOfRangeException">
        /// The error needs more registers than an estimator has, 2^30, which is anything
        /// below about 3.2e-5.
        /// </exception>
        public static HyperLogLog NewDefaultHyperLogLog(double e)
        {
            var m = Math.Pow(1.04 / e, 2);
            var registers = Math.Pow(2, Math.Ceiling(Math.Log(m, 2)));

            // Checked before the cast to a uint, which saturates: an error of 1e-5 wants
            // 2^34 registers, and the complaint was that 4294967295 is not a power of two.
            if (!(registers <= MaxRegisters))
            {
                throw new ArgumentOutOfRangeException(nameof(e), e,
                    $"A standard error of {e} needs about {registers:E2} registers, and an " +
                    $"estimator has at most {MaxRegisters}. Use a larger error.");
            }

            return new HyperLogLog((uint)registers);
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
            this.AddHash(CalculateHash(data));
            return this;
        }

        /// <summary>
        /// Records one 32-bit hash: its top b bits pick the register, and the
        /// position of the first set bit in the rest is what the register keeps the
        /// largest of.
        /// </summary>
        /// <remarks>
        /// Internal so that tests can drive the register update with chosen hash
        /// values, which is how a sketch of billions of distinct items is reached
        /// without billions of calls to the hash. <see cref="Add(ReadOnlySpan{byte})"/>
        /// goes through it, so the update a test drives is the one that ships.
        /// </remarks>
        internal void AddHash(uint hash)
        {
            var k = 32 - this.B;
            var r = CalculateRho(hash << (int)this.B, k);

            // Shifted as 64 bits: with a single register k is 32, and C# masks a 32-bit
            // shift by 32 to a shift by nothing, which would index by the whole hash.
            var j = (uint)((ulong)hash >> (int)k);

            if (r > this.Registers[j])
            {
                this.Registers[j] = r;
            }
        }

        /// <summary>
        /// Returns the approximated cardinality of the set.
        /// </summary>
        /// <returns>
        /// The approximated cardinality of the set. It is never less than the number of
        /// registers that have been set, so it is never 0 once anything has been added,
        /// and never more than the ceiling described in the remarks.
        /// </returns>
        /// <remarks>
        /// This is Flajolet et al.'s raw harmonic-mean estimate with linear counting at
        /// or below 5m/2, as in their paper, except at the large end. There the paper
        /// corrects the raw estimate for collisions in the 32-bit hash space, with
        /// -2^32 ln(1 - E/2^32) once E passes 2^32/30, and this does not. The registers
        /// already absorb collisions, so that correction inflated every estimate it
        /// touched: by 3% at 2^28 distinct items and 13% at 2^30, and once the raw
        /// estimate passed 2^32 it took the logarithm of a negative number and returned
        /// 0. What 32 bits do limit is the run of zeros a register can record (see the
        /// class remarks). A register at that cap is counted by Ertl's tau term, the one
        /// <see cref="HyperLogLogPlus"/> uses for its own saturated registers: what such
        /// registers are expected to contribute, given the fraction of registers that
        /// have reached the cap. A sketch with no register at the cap estimates exactly
        /// as the paper's raw estimator does.
        /// <para>
        /// A sketch whose every register is at the cap can say only that it has seen a
        /// great many items, and the estimate would be infinite. It saturates instead:
        /// a full sketch reports what a sketch one register short of full reports, which
        /// is the largest estimate it can give, about 2^32 * ln(m) -- 1.1e10 at 16
        /// registers, 4.2e10 at 2^14 and 8.9e10 at 2^30. A sketch reporting its ceiling
        /// has probably seen at least that many items, and may have seen far more.
        /// </para>
        /// </remarks>
        public UInt64 Count()
        {
            var m = (double)this.M;

            // rho is counted over the q bits of the hash below the register index, so
            // a register holds at most q + 1. One there is saturated: it saw a run of
            // zeros as long as the hash has room for, and says only that the run was
            // at least that long.
            var q = 32 - (int)this.B;

            var sum = 0.0;
            var zeros = 0;
            var saturated = 0;
            foreach (var val in this.Registers)
            {
                if (val > q)
                {
                    saturated++;
                    continue;
                }

                sum += 1.0 / Math.Pow(2.0, val);
                if (val == 0)
                {
                    zeros++;
                }
            }

            if (saturated == this.M)
            {
                // Every register is saturated, so the sum below would be zero and the
                // estimate infinite. A full sketch answers as one a register short of
                // full does, which is the most this sketch can answer.
                saturated--;
                sum = 1.0 / Math.Pow(2.0, q);
            }

            // Large range: a saturated register stands for every run of q + 1 zeros
            // or more, not for a run of exactly q + 1, so its plain contribution of
            // 2^-(q+1) is too large. Ertl's tau gives the contribution such registers
            // are expected to make, from the fraction of them that are not saturated.
            // With none saturated it is zero, and the sum is the paper's.
            sum += m * HyperLogLogPlus.Tau((m - saturated) / m) / Math.Pow(2.0, q);

            var estimate = this.Alpha * m * m / sum;
            if (estimate <= 5.0 / 2.0 * m && zeros > 0)
            {
                // Small range correction
                estimate = m * Math.Log(m / zeros);
            }

            // Every register that has been set was set by a distinct hash, so this
            // many distinct items were certainly added. Only one or two registers can
            // estimate below it, and one of them estimates below one.
            var occupied = this.M - (uint)zeros;
            return Math.Max((UInt64)estimate, occupied);
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
            return Read(stream, null, ReadOptions.Default);
        }

        /// <summary>
        /// Reads a structure written by <see cref="WriteTo"/>, allocating no more than the
        /// options' memory budget allows. <see cref="ReadFrom(Stream)"/> reads under
        /// <see cref="ReadOptions.Default"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="options">The most memory the read may allocate.</param>
        /// <returns>The structure that was written.</returns>
        /// <exception cref="InvalidDataException">
        /// Reading the payload would allocate more than the budget allows, among the other
        /// reasons a payload is refused. Its inner exception is then a
        /// <see cref="ReadBudgetExceededException"/>.
        /// </exception>
        public static HyperLogLog ReadFrom(Stream stream, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, null, options);
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
            return Read(stream, hash, ReadOptions.Default);
        }

        /// <summary>
        /// Reads a structure as
        /// <see cref="ReadFrom(Stream, Func{ReadOnlySpan{byte}, ulong})"/> does, allocating
        /// no more than the options' memory budget allows.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function, as for the overload without options.</param>
        /// <param name="options">The most memory the read may allocate.</param>
        /// <returns>The structure that was written.</returns>
        /// <exception cref="InvalidDataException">
        /// Reading the payload would allocate more than the budget allows, among the other
        /// reasons a payload is refused. Its inner exception is then a
        /// <see cref="ReadBudgetExceededException"/>.
        /// </exception>
        public static HyperLogLog ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, hash, options);
        }

        private static HyperLogLog Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.HyperLogLog);
            var payload = PersistenceFormat.Read(stream, StructureId.HyperLogLog, out var hashId, budget);
            var reader = new PayloadReader(payload, budget);

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
            budget.Charge(ReadBudget.ArrayOf(m, 1));
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
