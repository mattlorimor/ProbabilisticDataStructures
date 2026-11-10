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
    /// <b>The whole 64-bit hash is used.</b> The older one takes its register index and
    /// its run of zeros from 32 bits, so its registers reach the longest run they can
    /// record as the count nears a few times 2^32, and past about 2^33 its estimate
    /// degrades until every register is full. At 64 bits that happens around 2^64, and
    /// the estimate stays good far past where the older one gives up.
    /// </para>
    /// <para>
    /// <b>Small cardinalities are stored sparsely and counted exactly.</b> An estimator
    /// holding a few thousand items keeps the hashes themselves rather than a register
    /// array, which is both smaller and exact. It switches to registers once the hashes
    /// would take more room than the registers do, which is Heule, Nunkesser and Hall's
    /// rule (section 5.3), with one deliberate exception: it always holds at least 16
    /// hashes first. At precisions 4 to 6 the paper's rule would switch after 2, 4 or 8
    /// distinct items; holding 16 keeps every count up to 16 exact there, at a cost of at
    /// most 112 bytes more than the registers (at precision 4; 96 at 5 and 64 at 6). From
    /// precision 7 up, the sparse form never costs more than the dense one.
    /// </para>
    /// <para>
    /// <b>The dense estimate uses Ertl's estimator</b> rather than the raw estimate with
    /// corrections bolted on either end. See <see cref="EstimateDense"/> for why.
    /// </para>
    /// </remarks>
    public class HyperLogLogPlus : IBinaryPersistable<HyperLogLogPlus>, IPayloadBody
    {
        /// <summary>
        /// The smallest precision worth having. Below 16 registers the relative error is
        /// above 25% and the estimate says very little.
        /// </summary>
        private const uint MinPrecision = 4;

        /// <summary>
        /// The largest precision this builds. 2^18 registers gives a relative error near
        /// 0.2%, 1.04 / 512, which is the smallest <see cref="NewDefault"/> accepts, and the
        /// register array is a quarter of a megabyte.
        /// </summary>
        internal const uint MaxPrecision = 18;

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
        /// The dense registers, so tests can put the estimator into a crafted state
        /// and hold Count() to Ertl's printed formulas. Null while sparse.
        /// </summary>
        internal byte[]? DenseRegisterState => this.registers;

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
            : this(hash ?? Defaults.GetDefaultHashFunction(), Checked(precision))
        {
            this.sparse = new ulong[this.InitialSparseEntries];
        }

        /// <summary>
        /// An estimator at a precision already checked, with neither a sparse buffer nor
        /// registers: a read gives it the one its payload holds. Made through the public
        /// constructor, every read made a fresh sparse buffer and dropped it, charged nowhere
        /// (#346).
        /// </summary>
        private HyperLogLogPlus(Func<ReadOnlySpan<byte>, ulong> hash, uint precision)
        {
            this.precision = precision;
            this.m = 1u << (int)precision;
            this.Hash = hash;
        }

        /// <summary>Refuses a precision the constructor does not build at, and returns it.</summary>
        private static uint Checked(uint precision)
        {
            if (precision < MinPrecision || precision > MaxPrecision)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(precision), precision,
                    $"Precision must be between {MinPrecision} and {MaxPrecision}. " +
                    "Below that the estimate says almost nothing, and above it the " +
                    "registers cost more than the accuracy is worth.");
            }

            return precision;
        }

        private const int InitialSparseCapacity = 16;

        /// <summary>
        /// The size of a freshly built sparse buffer: small, so an estimator that sees a
        /// handful of items costs a handful of bytes, and never past the cap. The buffer
        /// only grows toward the cap, so one that started above it would convert wherever
        /// it happened to fill rather than at the cap, which is how the two once came to
        /// disagree. The cap is floored at this size, so the minimum changes nothing
        /// today; it is here so the cap stays the one thing that decides.
        /// </summary>
        private int InitialSparseEntries => InitialSparseEntriesAt(this.m);

        /// <summary>
        /// <see cref="InitialSparseEntries"/> for an estimator of <paramref name="m"/>
        /// registers, for a price that has no estimator.
        /// </summary>
        private static int InitialSparseEntriesAt(uint m) =>
            Math.Min(InitialSparseCapacity, MaxSparseEntriesAt(m));

        /// <summary>
        /// Creates an estimator whose relative error is about the one given.
        /// </summary>
        /// <remarks>
        /// The registers are <c>(1.04 / errorRate)^2</c>, rounded up to a power of two, and
        /// at least 2^4. The most are 2^18, whose error is 1.04 / 512, about 0.00203, and a
        /// smaller error is refused rather than met with a larger one. Releases up to 6.2.0
        /// built the largest estimator for it. For a smaller error, use
        /// <see cref="UltraLogLog"/>.
        /// </remarks>
        /// <param name="errorRate">The relative error wanted, such as 0.01 for 1%.</param>
        /// <returns>An estimator with enough registers to deliver it.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The error is not between 0 and 1, or is smaller than the largest estimator
        /// delivers, about 0.00203.
        /// </exception>
        public static HyperLogLogPlus NewDefault(double errorRate)
        {
            Guard.ValidRelativeAccuracy(errorRate, nameof(errorRate));

            var registers = Math.Pow(1.04 / errorRate, 2);
            var precision = Math.Ceiling(Math.Log2(registers));

            // Compared before the cast to a uint, which saturates, and refused as
            // HyperLogLog.NewDefaultHyperLogLog refuses an error it cannot honour. A
            // precision clamped to the largest would deliver a larger error than was asked,
            // and nothing would say so.
            if (!(precision <= MaxPrecision))
            {
                var smallest = 1.04 / Math.Sqrt(1u << (int)MaxPrecision);
                throw new ArgumentOutOfRangeException(nameof(errorRate), errorRate,
                    $"A relative error of {errorRate} needs about {registers:E2} registers, and " +
                    $"an estimator has at most 2^{MaxPrecision}, whose error is about " +
                    $"{smallest:G3}: the smallest this delivers. Use a larger error, or an " +
                    "UltraLogLog, which has more registers.");
            }

            return new HyperLogLogPlus(Math.Max((uint)precision, MinPrecision));
        }

        /// <summary>
        /// Adds data to the estimator. Returns the estimator to allow for chaining.
        /// </summary>
        /// <param name="data">The data to add.</param>
        /// <returns>The estimator.</returns>
        public HyperLogLogPlus Add(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            return this.Add(data.AsSpan());
        }

        /// <inheritdoc cref="Add(byte[])"/>
        public HyperLogLogPlus Add(ReadOnlySpan<byte> data)
        {
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
                // the largest buffer allowed. Only a hash not already among them can
                // push the sparse form past it: the paper decides on the list with the
                // new hash merged in, and a repeat merges to nothing, so it never
                // converts. This is the only place converting happens: it is what
                // costs the exact count, so a new item is the only thing entitled to
                // force it.
                if (this.sparseCount == this.sparse.Length)
                {
                    if (Array.BinarySearch(this.sparse, 0, this.sparseCount, hash) >= 0)
                    {
                        return;
                    }

                    this.ConvertToDense();
                    this.SetRegister(hash);
                    return;
                }
            }

            this.sparse[this.sparseCount++] = hash;
            this.sparseIsCompact = false;
        }

        /// <summary>
        /// The most hashes the sparse form may hold: as many as fit in the bytes the
        /// registers would take, and never fewer than <see cref="InitialSparseCapacity"/>.
        /// </summary>
        /// <remarks>
        /// Heule, Nunkesser and Hall keep the sparse list until it "would require more
        /// memory than the dense representation of the registers (i.e., 6m bits)"
        /// (section 5.3), and their Figure 6 converts when |sparse_list| &gt; m * 6 bits.
        /// A register here is a byte and a held hash all eight bytes of it, so their rule
        /// is m / 8 hashes.
        /// <para>
        /// This deviates from the paper at precisions 4 to 6, deliberately. The rule gives
        /// 2, 4 and 8 hashes there; holding 16 instead keeps every count up to 16 exact,
        /// where an estimate from 16 to 64 registers is off by 6% to 26%. That is the
        /// dense form's root-mean-square relative error over counts 2 to 16, measured:
        /// 12% to 26% at precision 4, 9% to 18% at 5 and 6% to 14% at 6, so the textbook
        /// 1.04 / sqrt(m) is about the worst count at each rather than a typical one.
        /// The price is at most 112 bytes over the dense registers, at precision 4 (128
        /// against 16), 96 at precision 5 and 64 at 6. It is also what 6.0.0 through
        /// 6.2.0 did, since their first buffer was 16 hashes and only ever grew. From
        /// precision 7 the floor changes nothing and the cap is the paper's.
        /// </para>
        /// </remarks>
        internal int MaxSparseEntries => MaxSparseEntriesAt(this.m);

        private static int MaxSparseEntriesAt(uint m) =>
            Math.Max(InitialSparseCapacity, (int)(m / sizeof(ulong)));

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
        /// <para>
        /// The two were measured against each other rather than assumed. Tables were
        /// derived by the paper's own procedure, given a threshold chosen to minimise
        /// their error, and evaluated on streams they were not trained on. Mean absolute
        /// error across nineteen cardinalities from 0.125m to 20m:
        /// </para>
        /// <code>
        ///   precision    Ertl     tables
        ///          10   2.127%    2.158%
        ///          12   1.095%    1.087%
        ///          14   0.563%    0.569%
        ///          16   0.262%    0.269%
        /// </code>
        /// <para>
        /// A tie: the gaps are a percent or two of each other, well inside the noise of
        /// sixty streams, and the sign changes with the precision. The worst point of
        /// each agrees to three digits at every precision.
        /// </para>
        /// <para>
        /// Since accuracy does not choose between them, everything else does. This is
        /// forty lines and no data, works at any precision without being trained for it,
        /// and has no threshold to place. The tables are six thousand measured numbers
        /// plus a threshold per precision, all of which have to be right for the
        /// estimator to be better in the band they exist for, and none of which announce
        /// themselves when wrong. Re-run the comparison with
        /// <c>dotnet run -c Release --project Benchmarks -- study hll-bias 14</c>.
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
        /// large-range correction. <see cref="HyperLogLog"/> uses it too, for the
        /// registers its 32-bit hash saturates.
        /// </summary>
        /// <param name="x">The fraction of registers that are not saturated.</param>
        internal static double Tau(double x)
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
            this.sparse = new ulong[this.InitialSparseEntries];
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
        public void WriteTo(Stream stream) => PersistenceFormat.Write(stream, this);

        EnvelopeHeader IPayloadBody.WritePayload(PayloadWriter payload)
        {
            // Compacted when counted, so the count and the write see the same entries;
            // the second pass finds it compact already.
            if (this.sparse is not null && !this.sparseIsCompact)
            {
                this.CompactSparse();
            }

            payload.WriteUInt32(this.precision);

            if (this.sparse is not null)
            {
                payload.WriteByte(0);
                payload.WriteUInt32((uint)this.sparseCount);

                if (payload.BeginBlock((long)this.sparseCount * sizeof(ulong)))
                {
                    for (var i = 0; i < this.sparseCount; i++)
                    {
                        payload.WriteUInt64(this.sparse[i]);
                    }
                }
            }
            else
            {
                payload.WriteByte(1);
                payload.WriteBytes(this.registers!);
            }

            return new(StructureId.HyperLogLogPlus, PersistenceFormat.Identify(this.Hash));
        }

        /// <summary>
        /// Reads an estimator written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The estimator that was written.</returns>
        public static HyperLogLogPlus ReadFrom(Stream stream)
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
        public static HyperLogLogPlus ReadFrom(Stream stream, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, null, options);
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
        public static HyperLogLogPlus ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, hash, options);
        }

        private static HyperLogLogPlus Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.HyperLogLogPlus);
            return PersistenceFormat.Read(
                stream, StructureId.HyperLogLogPlus, budget, Price, hash, PersistenceFormat.CheckResolvable,
                ReadPayload);
        }

        /// <summary>
        /// Reads the payload of the envelope <c>Read</c> opened, once the envelope has
        /// been checked. See <see cref="PayloadParse{T}"/>.
        /// </summary>
        private static HyperLogLogPlus ReadPayload(
            ref PayloadReader reader, ushort version, HashId hashId,
            Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            var budget = reader.Budget;

            var precision = reader.ReadUInt32();
            CheckPrecision(precision);

            var representation = reader.ReadByte();
            budget.Charge(ObjectCost);
            var estimator = new HyperLogLogPlus(PersistenceFormat.ResolveOrThrow(hashId, hash), precision);

            if (representation == 0)
            {
                var held = ReadHeld(ref reader);

                // The hashes are read one at a time below, so the payload has to hold them
                // before their buffer is charged or allocated, as the price checks before it
                // prices them, so that a payload without them is refused for that under any
                // budget.
                reader.RequireHolds(held, sizeof(ulong), "stored hashes");

                // Room for what was written, and at least a fresh buffer's worth, which
                // never exceeds the cap -- a buffer above it with room to spare would
                // move the conversion point. A payload holding exactly the cap comes back
                // full, so its first new item converts it and a repeat does not.
                budget.Charge(SparseCost(estimator.m, held));
                var buffer = new ulong[Math.Max(estimator.InitialSparseEntries, (int)held)];
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

        /// <summary>What a read charges for the estimator itself, measured at 56 bytes (#346).</summary>
        private static long ObjectCost => ReadBudget.Object(56);

        /// <summary>
        /// Prices what <see cref="Read"/> allocates beyond the envelope: the estimator, and the
        /// sparse buffer or the copy of the dense registers. See <see cref="PayloadPrice"/>.
        /// </summary>
        internal static void Price(ref PayloadReader reader, ushort version)
        {
            var precision = reader.ReadUInt32();
            CheckPrecision(precision);

            var representation = reader.ReadByte();
            reader.Budget.Price(ObjectCost);
            if (representation == 0)
            {
                var held = ReadHeld(ref reader);
                reader.RequireHolds(held, sizeof(ulong), "stored hashes");
                reader.Budget.Price(SparseCost(1u << (int)precision, held));
            }
            else if (representation == 1)
            {
                reader.SkipBytes();
            }
        }

        private static void CheckPrecision(uint precision)
        {
            if (precision < MinPrecision || precision > MaxPrecision)
            {
                throw new InvalidDataException(
                    $"Estimator has a precision of {precision}, and this library builds " +
                    $"them between {MinPrecision} and {MaxPrecision}.");
            }
        }

        private static uint ReadHeld(ref PayloadReader reader)
        {
            var held = reader.ReadUInt32();

            if (held > PersistenceFormat.MaxNestedCount)
            {
                throw new InvalidDataException(
                    $"Estimator claims {held} stored hashes, beyond anything this " +
                    "library builds.");
            }
            return held;
        }

        /// <summary>
        /// The sparse buffer of an estimator of <paramref name="m"/> registers read back
        /// holding <paramref name="held"/> hashes.
        /// </summary>
        private static long SparseCost(uint m, uint held) => ReadBudget.ArrayOf(
            (ulong)Math.Max(InitialSparseEntriesAt(m), (long)held), sizeof(ulong));
    }
}
