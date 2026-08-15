using System;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// Estimates how many distinct items a stream held, more accurately and in less
    /// space than <see cref="HyperLogLog"/>.
    /// </summary>
    /// <remarks>
    /// Heule, Nunkesser and Hall, "HyperLogLog in Practice" (2013), which is Google's
    /// HyperLogLog++.
    /// <para>
    /// It sits alongside <see cref="HyperLogLog"/> rather than replacing it. Replacing
    /// it would change the number an existing estimator answers with, including one read
    /// back from a payload written years ago, and this library does not change answers
    /// out from under stored data.
    /// </para>
    /// <para>
    /// Three things differ from the older estimator:
    /// </para>
    /// <para>
    /// <b>The whole 64-bit hash is used.</b> The older one indexes into a 32-bit space,
    /// so distinct items start colliding around four billion and it carries a
    /// large-range correction to paper over it. At 64 bits that correction is
    /// unnecessary -- collisions arrive around 2^64 -- and the estimate stays good far
    /// past where the older one gives up.
    /// </para>
    /// <para>
    /// <b>Small cardinalities are stored sparsely and counted exactly.</b> An estimator
    /// holding a few thousand items keeps the hashes themselves rather than a register
    /// array, which is both smaller and exact. It switches to registers once the hashes
    /// would take more room than the registers do, so the sparse form never costs more
    /// than the dense one.
    /// </para>
    /// <para>
    /// <b>The dense estimate uses Ertl's estimator</b> rather than the raw estimate with
    /// corrections bolted on either end. See <see cref="EstimateDense"/> for why.
    /// </para>
    /// </remarks>
    public class HyperLogLogPlus : IBinaryPersistable<HyperLogLogPlus>
    {
        /// <summary>
        /// The smallest precision worth having. Below 16 registers the relative error is
        /// above 25% and the estimate says very little.
        /// </summary>
        private const uint MinPrecision = 4;

        /// <summary>
        /// The largest precision this builds. 2^18 registers gives a relative error near
        /// 0.4%, and the register array is a quarter of a megabyte.
        /// </summary>
        private const uint MaxPrecision = 18;

        /// <summary>
        /// 1 / (2 ln 2), the limit of the classic alpha as the register count grows.
        /// Ertl's estimator uses it at every size rather than the small-m special cases
        /// the original needed.
        /// </summary>
        private const double AlphaInfinity = 0.5 / 0.693147180559945309417232121458;

        private uint precision;
        private uint m;

        /// <summary>
        /// The register array, once the estimator is dense. Null while it is sparse.
        /// </summary>
        private byte[]? registers;

        /// <summary>
        /// Hashes of the items seen, while the estimator is sparse. Holds duplicates
        /// between compactions.
        /// </summary>
        private ulong[]? sparse;

        private int sparseCount;

        /// <summary>
        /// Whether everything in <see cref="sparse"/> up to <see cref="sparseCount"/> is
        /// sorted and distinct, so that counting it needs no work.
        /// </summary>
        private bool sparseIsCompact = true;

        internal Func<ReadOnlySpan<byte>, ulong> Hash { get; set; } = null!;

        /// <summary>
        /// Whether the estimator is holding hashes rather than registers.
        /// </summary>
        internal bool IsSparse => this.sparse is not null;

        /// <summary>
        /// Creates an estimator with the given precision.
        /// </summary>
        /// <param name="precision">
        /// The number of bits of each hash used to pick a register, between 4 and 18.
        /// The estimator has 2^precision registers and a relative error near
        /// 1.04 / sqrt(2^precision).
        /// </param>
        /// <param name="hash">
        /// The hash function to use, or null for the default. Passing it here is the
        /// only way to have one hash cover everything the estimator will ever see: once
        /// anything has been added, the hash can no longer be replaced.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The precision is outside 4 to 18.
        /// </exception>
        public HyperLogLogPlus(uint precision, Func<ReadOnlySpan<byte>, ulong>? hash = null)
        {
            if (precision < MinPrecision || precision > MaxPrecision)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(precision), precision,
                    $"Precision must be between {MinPrecision} and {MaxPrecision}. " +
                    "Below that the estimate says almost nothing, and above it the " +
                    "registers cost more than the accuracy is worth.");
            }

            this.precision = precision;
            this.m = 1u << (int)precision;
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();
            this.sparse = new ulong[InitialSparseCapacity];
        }

        private const int InitialSparseCapacity = 16;

        /// <summary>
        /// Creates an estimator whose relative error is about the one given.
        /// </summary>
        /// <param name="errorRate">The relative error wanted, such as 0.01 for 1%.</param>
        /// <returns>An estimator with enough registers to deliver it.</returns>
        public static HyperLogLogPlus NewDefault(double errorRate)
        {
            Guard.ValidRelativeAccuracy(errorRate, nameof(errorRate));

            var registers = Math.Pow(1.04 / errorRate, 2);
            var precision = (uint)Math.Ceiling(Math.Log2(registers));

            return new HyperLogLogPlus(Math.Clamp(precision, MinPrecision, MaxPrecision));
        }

        /// <summary>
        /// Adds data to the estimator. Returns the estimator to allow for chaining.
        /// </summary>
        /// <param name="data">The data to add.</param>
        /// <returns>The estimator.</returns>
        public HyperLogLogPlus Add(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            this.Observe(this.Hash(data));
            return this;
        }

        /// <summary>
        /// Records a hash, in whichever representation the estimator is currently in.
        /// </summary>
        private void Observe(ulong hash)
        {
            if (this.registers is not null)
            {
                this.SetRegister(hash);
                return;
            }

            if (this.sparseCount == this.sparse!.Length)
            {
                this.CompactSparse();

                // Still full after compaction means the distinct items themselves fill
                // the largest buffer allowed, so the representation has to change. This
                // is the only place that happens: converting is what costs the exact
                // count, so adding an item is the only thing entitled to force it.
                if (this.sparseCount == this.sparse.Length)
                {
                    this.ConvertToDense();
                    this.SetRegister(hash);
                    return;
                }
            }

            this.sparse[this.sparseCount++] = hash;
            this.sparseIsCompact = false;
        }

        /// <summary>
        /// The most hashes the sparse form may hold. Chosen so the hashes never take
        /// more room than the registers they are standing in for.
        /// </summary>
        private int MaxSparseEntries => Math.Max(4, (int)(this.m / sizeof(ulong)));

        /// <summary>
        /// Sorts and deduplicates the sparse buffer, growing it if the distinct items
        /// are what is filling it. Never changes the representation.
        /// </summary>
        private void CompactSparse()
        {
            var buffer = this.sparse!;
            Array.Sort(buffer, 0, this.sparseCount);

            var distinct = 0;
            for (var i = 0; i < this.sparseCount; i++)
            {
                if (i == 0 || buffer[i] != buffer[i - 1])
                {
                    buffer[distinct++] = buffer[i];
                }
            }

            this.sparseCount = distinct;
            this.sparseIsCompact = true;

            // Grow only when the distinct items are actually filling the buffer.
            // Otherwise the compaction alone has made room, and growing would spend
            // memory to hold duplicates.
            if (distinct > buffer.Length / 2 && buffer.Length < this.MaxSparseEntries)
            {
                var grown = Math.Min(buffer.Length * 2, this.MaxSparseEntries);
                Array.Resize(ref buffer, grown);
                this.sparse = buffer;
            }
        }

        /// <summary>
        /// Replaces the stored hashes with the register array they imply.
        /// </summary>
        private void ConvertToDense()
        {
            var buffer = this.sparse!;
            var held = this.sparseCount;

            this.registers = new byte[this.m];
            this.sparse = null;
            this.sparseCount = 0;

            for (var i = 0; i < held; i++)
            {
                this.SetRegister(buffer[i]);
            }
        }

        /// <summary>
        /// Records a hash in the register array: the leading bits pick the register, and
        /// the rest contribute the position of their first set bit.
        /// </summary>
        private void SetRegister(ulong hash)
        {
            var index = (int)(hash >> (64 - (int)this.precision));
            var remaining = hash << (int)this.precision;

            // The count of leading zeros in the bits that are left, plus one. Shifting
            // the used bits out leaves zeros behind, so an all-zero remainder gives
            // exactly the maximum, 64 - precision + 1.
            var rho = remaining == 0
                ? (byte)(64 - this.precision + 1)
                : (byte)(System.Numerics.BitOperations.LeadingZeroCount(remaining) + 1);

            if (rho > this.registers![index])
            {
                this.registers[index] = rho;
            }
        }

        /// <summary>
        /// The estimated number of distinct items added.
        /// </summary>
        /// <returns>
        /// While the estimator is sparse this is exact. Once it is dense it is an
        /// estimate with a relative error near 1.04 / sqrt(m).
        /// </returns>
        public ulong Count()
        {
            if (this.sparse is not null)
            {
                if (!this.sparseIsCompact)
                {
                    this.CompactSparse();
                }

                return (ulong)this.sparseCount;
            }

            return this.EstimateDense();
        }

        /// <summary>
        /// Ertl's estimator, from "New cardinality estimation algorithms for HyperLogLog
        /// sketches" (2017).
        /// </summary>
        /// <remarks>
        /// The original estimator is the raw harmonic mean with two corrections bolted
        /// on: linear counting below one threshold and a logarithmic correction above
        /// another. It is at its worst in the band between them, and the thresholds
        /// themselves are empirical. HyperLogLog++ improves that band with tables of
        /// measured bias.
        /// <para>
        /// This does the same job with neither thresholds nor tables. It accounts for
        /// the registers at each end -- those still zero, and those saturated -- as
        /// terms in the estimate rather than as cases to switch on, which is why it needs
        /// no correction at either extreme and has no band between them to be worst in.
        /// </para>
        /// </remarks>
        private ulong EstimateDense()
        {
            var q = 64 - (int)this.precision;

            // How many registers hold each value. Index 0 is the registers never set,
            // and index q + 1 the ones that saw the longest possible run of zeros.
            var counts = new int[q + 2];
            foreach (var register in this.registers!)
            {
                counts[register]++;
            }

            var registerCount = (double)this.m;
            var z = registerCount * Tau((registerCount - counts[q + 1]) / registerCount);

            for (var k = q; k >= 1; k--)
            {
                z = 0.5 * (z + counts[k]);
            }

            z += registerCount * Sigma(counts[0] / registerCount);

            return (ulong)Math.Round(AlphaInfinity * registerCount * registerCount / z);
        }

        /// <summary>
        /// The correction for registers that are still zero, which is what replaces
        /// linear counting at small cardinalities.
        /// </summary>
        private static double Sigma(double x)
        {
            if (x == 1.0)
            {
                return double.PositiveInfinity;
            }

            var y = 1.0;
            var z = x;
            double previous;

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
        /// The correction for saturated registers, which is what replaces the
        /// large-range correction.
        /// </summary>
        private static double Tau(double x)
        {
            if (x == 0.0 || x == 1.0)
            {
                return 0.0;
            }

            var y = 1.0;
            var z = 1 - x;
            double previous;

            do
            {
                x = Math.Sqrt(x);
                previous = z;
                y *= 0.5;
                z -= (1 - x) * (1 - x) * y;
            }
            while (previous != z);

            return z / 3;
        }

        /// <summary>
        /// Combines another estimator into this one, so that it counts both streams.
        /// </summary>
        /// <param name="other">The estimator to merge in, which is left unchanged.</param>
        /// <returns>This estimator, to allow chaining.</returns>
        /// <exception cref="ArgumentNullException">The other estimator is null.</exception>
        /// <exception cref="ArgumentException">
        /// The estimators have different precisions, or were built with different hash
        /// functions.
        /// </exception>
        public HyperLogLogPlus Merge(HyperLogLogPlus other)
        {
            ArgumentNullException.ThrowIfNull(other);

            if (this.precision != other.precision)
            {
                throw new ArgumentException(
                    "Estimators must have the same precision to be merged: this one has " +
                    $"{this.precision} and the other {other.precision}. Their registers " +
                    "do not correspond, so the precision must match.",
                    nameof(other));
            }

            Guard.SameHashFunction(this.Hash, other.Hash, nameof(other));

            if (other.registers is not null)
            {
                // Registers cannot be turned back into the hashes that set them, so the
                // merged estimator is dense whatever this one was.
                if (this.registers is null)
                {
                    this.ConvertToDense();
                }

                for (var i = 0; i < this.registers!.Length; i++)
                {
                    if (other.registers[i] > this.registers[i])
                    {
                        this.registers[i] = other.registers[i];
                    }
                }

                return this;
            }

            // The other is sparse, so its hashes are still available and can go in
            // whichever way this estimator is holding things.
            for (var i = 0; i < other.sparseCount; i++)
            {
                this.Observe(other.sparse![i]);
            }

            return this;
        }

        /// <summary>
        /// Empties the estimator, returning it to its sparse representation.
        /// </summary>
        /// <returns>The estimator.</returns>
        public HyperLogLogPlus Reset()
        {
            this.registers = null;
            this.sparse = new ulong[InitialSparseCapacity];
            this.sparseCount = 0;
            this.sparseIsCompact = true;
            return this;
        }

        /// <summary>
        /// The number of registers the estimator uses once it is dense.
        /// </summary>
        public uint M()
        {
            return this.m;
        }

        /// <summary>
        /// The precision the estimator was built with.
        /// </summary>
        public uint Precision()
        {
            return this.precision;
        }

        /// <summary>
        /// The estimator's memory footprint in bytes, which depends on which
        /// representation it is currently in.
        /// </summary>
        public ulong SizeInBytes()
        {
            return this.registers is not null
                ? (ulong)this.registers.Length
                : (ulong)this.sparse!.Length * sizeof(ulong);
        }

        /// <summary>
        /// Sets the hashing function used by the estimator.
        /// </summary>
        /// <param name="h">The hash function to use.</param>
        /// <exception cref="InvalidOperationException">
        /// Anything has been added. The hash cannot be replaced then, because everything
        /// already counted was placed by the old one.
        /// </exception>
        public void SetHash(Func<ReadOnlySpan<byte>, ulong> h)
        {
            ArgumentNullException.ThrowIfNull(h);
            Guard.HashMayBeReplaced(this.IsEmptyOfData, nameof(HyperLogLogPlus));
            this.Hash = h;
        }

        private bool IsEmptyOfData
        {
            get
            {
                if (this.sparse is not null)
                {
                    return this.sparseCount == 0;
                }

                foreach (var register in this.registers!)
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
        /// Writes the estimator to a stream.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        /// <remarks>
        /// The payload says which representation it holds. Always writing the dense one
        /// would make a payload of a nearly-empty estimator as large as a full one,
        /// which is the thing the sparse representation exists to avoid.
        /// </remarks>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            if (this.sparse is not null && !this.sparseIsCompact)
            {
                this.CompactSparse();
            }

            var payload = new PayloadWriter();
            payload.WriteUInt32(this.precision);

            if (this.sparse is not null)
            {
                payload.WriteByte(0);
                payload.WriteUInt32((uint)this.sparseCount);

                for (var i = 0; i < this.sparseCount; i++)
                {
                    payload.WriteUInt64(this.sparse[i]);
                }
            }
            else
            {
                payload.WriteByte(1);
                payload.WriteBytes(this.registers!);
            }

            PersistenceFormat.Write(
                stream,
                StructureId.HyperLogLogPlus,
                PersistenceFormat.Identify(this.Hash),
                payload.WrittenSpan);
        }

        /// <summary>
        /// Reads an estimator written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The estimator that was written.</returns>
        public static HyperLogLogPlus ReadFrom(Stream stream)
        {
            return Read(stream, null);
        }

        /// <summary>
        /// Reads an estimator written by <see cref="WriteTo"/>, using the supplied hash
        /// function rather than the one named in the payload.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function the estimator was using.</param>
        /// <returns>The estimator that was written.</returns>
        public static HyperLogLogPlus ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            return Read(stream, hash);
        }

        private static HyperLogLogPlus Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(
                stream, StructureId.HyperLogLogPlus, out var hashId);
            var reader = new PayloadReader(payload);

            var precision = reader.ReadUInt32();

            if (precision < MinPrecision || precision > MaxPrecision)
            {
                throw new InvalidDataException(
                    $"Estimator has a precision of {precision}, and this library builds " +
                    $"them between {MinPrecision} and {MaxPrecision}.");
            }

            var representation = reader.ReadByte();
            var estimator = new HyperLogLogPlus(precision)
            {
                Hash = PersistenceFormat.ResolveOrThrow(hashId, hash),
            };

            if (representation == 0)
            {
                var held = reader.ReadUInt32();

                if (held > PersistenceFormat.MaxNestedCount)
                {
                    throw new InvalidDataException(
                        $"Estimator claims {held} stored hashes, beyond anything this " +
                        "library builds.");
                }

                var buffer = new ulong[Math.Max(InitialSparseCapacity, (int)held)];
                var previous = 0UL;

                for (var i = 0; i < held; i++)
                {
                    var value = reader.ReadUInt64();

                    // Written sorted and distinct. A payload that is not says the hashes
                    // were not the ones this wrote, and its count would be wrong.
                    if (i > 0 && value <= previous)
                    {
                        throw new InvalidDataException(
                            "Estimator's stored hashes are not in increasing order, so " +
                            "they are not the distinct set it would have written.");
                    }

                    buffer[i] = value;
                    previous = value;
                }

                estimator.sparse = buffer;
                estimator.sparseCount = (int)held;
                estimator.sparseIsCompact = true;
            }
            else if (representation == 1)
            {
                var registers = reader.ReadBytes();

                if (registers.Length != estimator.m)
                {
                    throw new InvalidDataException(
                        $"Estimator has {registers.Length} registers where a precision " +
                        $"of {precision} needs {estimator.m}.");
                }

                var maxRho = 64 - precision + 1;
                foreach (var register in registers)
                {
                    if (register > maxRho)
                    {
                        throw new InvalidDataException(
                            $"Estimator holds a register of {register}, above the {maxRho} " +
                            $"a precision of {precision} can produce.");
                    }
                }

                estimator.registers = registers;
                estimator.sparse = null;
                estimator.sparseCount = 0;
            }
            else
            {
                throw new InvalidDataException(
                    $"Estimator claims representation {representation}, and there are " +
                    "only the sparse one and the dense one.");
            }

            reader.ExpectEnd();
            return estimator;
        }
    }
}
