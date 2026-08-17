using System;
using System.IO;
using System.Numerics;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// UltraLogLog estimates how many distinct elements a stream held, using around a
    /// quarter less space than <see cref="HyperLogLog"/> for the same accuracy, as
    /// described by Otmar Ertl in UltraLogLog: A Practical and More Space-Efficient
    /// Alternative to HyperLogLog for Approximate Distinct Counting (VLDB 2024).
    /// </summary>
    /// <remarks>
    /// A HyperLogLog register remembers one number: the largest update value that ever
    /// landed on it. Everything else that landed there is discarded, and the discarded
    /// part is not worthless -- knowing that the second and third largest values also
    /// occurred says something about how many elements passed through. UltraLogLog
    /// keeps two bits of it. Each byte-sized register holds the largest update value
    /// in its top six bits and, in the bottom two, whether the values one and two below
    /// it also occurred.
    /// <para>
    /// Those two bits are worth about 25% of the space: the standard error is
    /// sqrt(0.6119/m) against HyperLogLog's sqrt(1.0796/m), so matching a
    /// HyperLogLog's accuracy takes roughly three registers for every four. The
    /// contract is unchanged -- constant-time insertion, idempotent, mergeable, and
    /// mergeable even across precisions, where the finer sketch is folded down to the
    /// coarser one.
    /// </para>
    /// <para>
    /// The estimator is Ertl's FGRA estimator, which is what makes the extra bits pay:
    /// a plain HyperLogLog-style harmonic mean over the top six bits would waste them.
    /// Its four coefficients are not transcribed here but computed from the single
    /// published parameter they derive from, so there is one constant to trust rather
    /// than five; see <see cref="Eta"/>. Only the per-precision scale factors are
    /// tabulated, for the same reason HyperLogLog tabulates its alpha: they correct a
    /// finite-register bias that has no closed form.
    /// </para>
    /// <para>
    /// Ertl also describes a martingale estimator, maintained as registers change,
    /// which is more accurate again -- it is where the paper's headline 28% comes
    /// from. It is deliberately not implemented here, because it is only valid for a
    /// sketch built by insertion alone: merging two sketches leaves no way to carry
    /// it, and an estimate that quietly stops being available once a caller merges is
    /// a worse thing to offer than one that was never there. What this class delivers
    /// is the 24% that survives merging.
    /// </para>
    /// </remarks>
    public class UltraLogLog : IBinaryPersistable<UltraLogLog>
    {
        /// <summary>
        /// The smallest precision the structure is defined for. Below this the
        /// register index leaves too little of the hash to draw update values from.
        /// </summary>
        public const uint MinPrecision = 3;

        /// <summary>
        /// The largest precision, at which the registers alone occupy 64 MB.
        /// </summary>
        public const uint MaxPrecision = 26;

        /// <summary>
        /// The exponent tying a register's update value to its contribution. Every
        /// other coefficient of the estimator follows from this one number.
        /// </summary>
        internal const double Tau = 0.8194911375910897;

        /// <summary>
        /// The variance constant: the relative standard error is sqrt(V/m).
        /// </summary>
        internal const double V = 0.6118931496978437;

        /// <summary>
        /// One byte per register: the largest update value seen, in the top six bits,
        /// and whether the two values below it occurred, in the bottom two. Zero is
        /// the untouched state.
        /// </summary>
        internal byte[] Registers { get; set; }

        /// <summary>
        /// The precision p, with 2^p registers.
        /// </summary>
        internal uint P { get; set; }

        /// <summary>
        /// Hash function.
        /// </summary>
        internal Func<ReadOnlySpan<byte>, ulong> Hash { get; set; }

        /// <summary>
        /// The estimator's four coefficients, derived from <see cref="Tau"/> rather
        /// than transcribed.
        /// </summary>
        /// <remarks>
        /// Ertl gives these as equation (16): each is a ratio of the functions
        /// omega_0..omega_3 evaluated at tau and 2*tau, normalised by their sum. The
        /// paper also prints their values, and the two agree to fifteen digits, which
        /// <c>TestEtaCoefficientsMatchThePublishedValues</c> checks -- deriving them
        /// keeps one published number in the code instead of five, and makes the
        /// derivation itself testable.
        /// </remarks>
        internal static readonly double[] Eta = ComputeEta();

        /// <summary>
        /// Per-precision scale factors, indexed by p - <see cref="MinPrecision"/>.
        /// </summary>
        /// <remarks>
        /// These correct the bias a finite register count introduces, exactly as
        /// HyperLogLog's alpha does, and like alpha they come from numerical work
        /// rather than a closed form. Asymptotically each is m^(1 + 1/tau); the
        /// tabulated values depart from that by 6.5% at p = 3 and by a millionth at
        /// p = 26. Taken from Ertl's reference implementation (hash4j).
        /// </remarks>
        internal static readonly double[] EstimationFactors =
        {
            94.59941722950778, 455.6358404615186, 2159.476860400962,
            10149.51036338182, 47499.52712820488, 221818.76564766388,
            1034754.6840013304, 4824374.384717942, 2.2486750611989766E7,
            1.0479810199493326E8, 4.8837185623048025E8, 2.275794725435168E9,
            1.0604938814719946E10, 4.9417362104242645E10, 2.30276227770117E11,
            1.0730444972228585E12, 5.0001829613164E12, 2.329988778511272E13,
            1.0857295240912981E14, 5.059288069986326E14, 2.3575295235667005E15,
            1.0985627213141412E16, 5.119087674515589E16, 2.3853948339571715E17,
        };

        /// <summary>
        /// Creates an UltraLogLog with 2^precision registers, one byte each.
        /// </summary>
        /// <param name="precision">
        /// The precision p, between <see cref="MinPrecision"/> and
        /// <see cref="MaxPrecision"/>. The relative standard error is
        /// sqrt(0.6119 / 2^p), so p = 12 gives about 1.2% from 4 KB.
        /// </param>
        /// <param name="hash">
        /// The hash function to use, or null for the default.
        /// </param>
        public UltraLogLog(uint precision, Func<ReadOnlySpan<byte>, ulong>? hash = null)
        {
            if (precision < MinPrecision || precision > MaxPrecision)
            {
                throw new ArgumentOutOfRangeException(nameof(precision), precision,
                    $"The precision must be between {MinPrecision} and " +
                    $"{MaxPrecision}. Below that the register index consumes hash " +
                    "bits the update value needs; above it the registers alone would " +
                    "exceed 64 MB.");
            }

            this.P = precision;
            this.Registers = new byte[1u << (int)precision];
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();
        }

        /// <summary>
        /// Creates an UltraLogLog sized for the given relative standard error.
        /// </summary>
        /// <param name="errorRate">
        /// The target relative standard error, such as 0.01 for one percent.
        /// </param>
        /// <param name="hash">
        /// The hash function to use, or null for the default.
        /// </param>
        public static UltraLogLog NewDefault(double errorRate,
            Func<ReadOnlySpan<byte>, ulong>? hash = null)
        {
            if (double.IsNaN(errorRate) || errorRate <= 0.0 || errorRate >= 1.0)
            {
                throw new ArgumentOutOfRangeException(nameof(errorRate), errorRate,
                    "The error rate must be greater than zero and less than one.");
            }

            // m = V / errorRate^2, rounded up to a power of two.
            var registers = V / (errorRate * errorRate);
            var precision = (uint)Math.Ceiling(Math.Log2(Math.Max(registers, 1.0)));
            precision = Math.Clamp(precision, MinPrecision, MaxPrecision);
            return new UltraLogLog(precision, hash);
        }

        /// <summary>
        /// Adds the data to the structure. Returns the UltraLogLog to allow for
        /// chaining.
        /// </summary>
        /// <param name="data">The data to add.</param>
        public UltraLogLog Add(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            return Add(data.AsSpan());
        }

        /// <summary>
        /// Adds the data to the structure. Returns the UltraLogLog to allow for
        /// chaining.
        /// </summary>
        /// <param name="data">The data to add.</param>
        public UltraLogLog Add(ReadOnlySpan<byte> data)
        {
            var p = (int)this.P;
            var hash = this.Hash(data);
            var index = (int)(hash >> (64 - p));

            // The non-index bits are moved to the top and the vacated low bits get a
            // sentinel, so that a run of leading zeros is counted over exactly those
            // bits and cannot run past them.
            var remaining = (hash << p) | (1UL << (p - 1));
            var leadingZeros = BitOperations.LeadingZeroCount(remaining);

            // What a register records is the absolute position of the bit the run
            // stopped at, not the length of the run. Keeping the precision in the
            // stored value is what makes a sketch foldable: a register from a finer
            // sketch means the same thing in a coarser one.
            var position = leadingZeros + p - 1;

            var updated = Unpack(this.Registers[index]) | (1UL << position);
            this.Registers[index] = Pack(updated);
            return this;
        }

        /// <summary>
        /// The estimated number of distinct elements added.
        /// </summary>
        public ulong Count()
        {
            return (ulong)(Estimate() + 0.5);
        }

        /// <summary>
        /// The estimated number of distinct elements, unrounded.
        /// </summary>
        internal double Estimate()
        {
            var offset = (int)(this.P << 2) + 4;

            // Registers below the offset and at the very top carry less information
            // than the plain contribution assumes, so they are counted and corrected
            // together rather than summed directly.
            var belowRange = 0;
            var atFour = 0;
            var atEight = 0;
            var atTen = 0;
            var top0 = 0;
            var top1 = 0;
            var top2 = 0;
            var top3 = 0;
            var sum = 0.0;

            foreach (var register in this.Registers)
            {
                int value = register;
                var normalised = value - offset;
                if (normalised < 0)
                {
                    if (normalised < -8) belowRange++;
                    else if (normalised == -8) atFour++;
                    else if (normalised == -4) atEight++;
                    else if (normalised == -2) atTen++;
                }
                else if (value < 252)
                {
                    sum += Contribution(normalised);
                }
                else
                {
                    if (value == 252) top0++;
                    else if (value == 253) top1++;
                    else if (value == 254) top2++;
                    else top3++;
                }
            }

            if (belowRange > 0 || atFour > 0 || atEight > 0 || atTen > 0)
            {
                sum += SmallRangeCorrection(belowRange, atFour, atEight, atTen);
            }
            if (top0 > 0 || top1 > 0 || top2 > 0 || top3 > 0)
            {
                sum += LargeRangeCorrection(top0, top1, top2, top3);
            }

            return EstimationFactors[this.P - MinPrecision] * Math.Pow(sum, -1.0 / Tau);
        }

        /// <summary>
        /// A register's contribution to the estimate, as a function of how far its
        /// value sits above the precision-dependent offset.
        /// </summary>
        /// <remarks>
        /// Ertl gives this as 2^(-tau*floor(r/4)) * eta_(r mod 4). The factor that
        /// depends only on the precision is constant across registers and is folded
        /// into <see cref="EstimationFactors"/> instead, which is why this is written
        /// in terms of the offset rather than the register value.
        /// </remarks>
        private static double Contribution(int normalised)
        {
            return Eta[normalised & 3] *
                Math.Pow(2.0, -Tau * (3 + (normalised >> 2)));
        }

        /// <summary>
        /// Unpacks a register into the bit set of update values it stands for. Bit
        /// k+1 means update value k occurred.
        /// </summary>
        internal static ulong Unpack(byte register)
        {
            // The top six bits give the position of the highest set bit and the
            // bottom two say whether the positions just below it were set. An empty
            // register shifts its way to zero: 4 << -2 is 4 << 62 once the shift
            // count wraps, which overflows to nothing, so the empty case needs no
            // branch of its own.
            return (4UL | (ulong)(register & 3)) << ((register >> 2) - 2);
        }

        /// <summary>
        /// Packs a bit set of update values back into a register, keeping the largest
        /// and the two below it.
        /// </summary>
        internal static byte Pack(ulong values)
        {
            var shift = BitOperations.LeadingZeroCount(values) + 1;
            return (byte)(((-shift) << 2) | (int)((values << shift) >> 62));
        }

        /// <summary>
        /// Merges another UltraLogLog into this one. The other structure is
        /// unchanged.
        /// </summary>
        /// <remarks>
        /// Registers combine by union of the update values they stand for, so the
        /// result depends only on the set of elements added and not on the order they
        /// were added or merged in.
        /// <para>
        /// A finer sketch may be merged into a coarser one, which folds it down. The
        /// fold is not a matter of taking the largest register of each batch: a
        /// register whose index has non-zero low bits recorded its update value
        /// against a longer index, and those bits would have counted towards the
        /// leading zeros at the coarser precision. Only the first register of each
        /// batch carries over directly; the rest contribute a single bit at the
        /// position their index implies.
        /// </para>
        /// </remarks>
        /// <param name="other">The structure to merge into this one.</param>
        public UltraLogLog Merge(UltraLogLog other)
        {
            ArgumentNullException.ThrowIfNull(other);
            Guard.SameHashFunction(this.Hash, other.Hash, nameof(other));

            if (other.P < this.P)
            {
                throw new ArgumentException(
                    $"Cannot merge a structure of precision {other.P} into one of " +
                    $"precision {this.P}. A coarser sketch cannot be refined into a " +
                    "finer one -- the register indices it never recorded cannot be " +
                    "recovered. Merge the other way round, which folds the finer " +
                    "sketch down.",
                    nameof(other));
            }

            if (other.P == this.P)
            {
                for (var i = 0; i < this.Registers.Length; i++)
                {
                    var merged = Unpack(this.Registers[i]) | Unpack(other.Registers[i]);
                    if (merged != 0)
                    {
                        this.Registers[i] = Pack(merged);
                    }
                }
                return this;
            }

            var shift = (int)(other.P - this.P);
            var batch = 1 << shift;
            for (var i = 0; i < this.Registers.Length; i++)
            {
                var values = Unpack(this.Registers[i]);

                // The first register of the batch shares this register's index
                // exactly, so its update values transfer as they are.
                values |= Unpack(other.Registers[i << shift]);

                // The rest were indexed by extra bits that, at this precision, are
                // part of what the update value counts. A non-zero low index means
                // the leading-zero run stopped there, which is one specific update
                // value rather than a range.
                for (var j = 1; j < batch; j++)
                {
                    if (other.Registers[(i << shift) | j] != 0)
                    {
                        // A non-zero low index means the run of zeros stopped inside
                        // the bits the finer sketch spent on its index, at the
                        // position that index implies.
                        values |= 1UL <<
                            (BitOperations.LeadingZeroCount((ulong)j) + (int)other.P - 1);
                    }
                }

                if (values != 0)
                {
                    this.Registers[i] = Pack(values);
                }
            }
            return this;
        }

        /// <summary>
        /// Restores the structure to its original state. Returns the UltraLogLog to
        /// allow for chaining.
        /// </summary>
        public UltraLogLog Reset()
        {
            Array.Clear(this.Registers);
            return this;
        }

        /// <summary>
        /// The number of registers.
        /// </summary>
        public uint M() => (uint)this.Registers.Length;

        /// <summary>
        /// The precision the structure was built with.
        /// </summary>
        public uint Precision() => this.P;

        /// <summary>
        /// The relative standard error this precision delivers.
        /// </summary>
        public double RelativeError() => Math.Sqrt(V / this.Registers.Length);

        /// <summary>
        /// Sets the hash function, which is refused once anything has been added.
        /// </summary>
        /// <param name="h">The hash function.</param>
        public void SetHash(Func<ReadOnlySpan<byte>, ulong> h)
        {
            ArgumentNullException.ThrowIfNull(h);
            Guard.HashMayBeReplaced(IsEmpty(), nameof(UltraLogLog));
            this.Hash = h;
        }

        private bool IsEmpty()
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

        // The corrections below handle the registers whose plain contribution would
        // be wrong. A register still at zero, or barely above it, has not been hit
        // often enough for the estimator's model to hold; one at the very top has
        // saturated. Ertl's approach is to replace such a register's contribution
        // with its expectation given what the register does tell us, taking the
        // distinct count from a cheap estimator built from the counts alone. The
        // result is one estimator that holds across the whole range rather than a set
        // of estimators with visible seams between them.

        /// <summary>
        /// Psi (equation 19), divided through by the coefficient span.
        /// </summary>
        private static double PsiPrime(double z, double zSquared)
        {
            return (z + Eta23X) * (zSquared + Eta13X) + Eta3012XX;
        }

        /// <summary>
        /// Sigma (equation 20): the corrected contribution of a register still at
        /// zero, as a convergent series summed until it stops growing.
        /// </summary>
        private static double Sigma(double z)
        {
            if (z <= 0.0)
            {
                return Eta[3];
            }
            if (z >= 1.0)
            {
                return double.PositiveInfinity;
            }

            var powZ = z;
            var nextPowZ = powZ * powZ;
            var sum = 0.0;
            var powTau = EtaX;
            while (true)
            {
                var previous = sum;
                var nextNextPowZ = nextPowZ * nextPowZ;
                sum += powTau * (powZ - nextPowZ) * PsiPrime(nextPowZ, nextNextPowZ);
                if (!(sum > previous))
                {
                    return sum / z;
                }
                powZ = nextPowZ;
                nextPowZ = nextNextPowZ;
                powTau *= Pow2Tau;
            }
        }

        /// <summary>
        /// Phi (equation 21): the counterpart of <see cref="Sigma"/> for saturated
        /// registers.
        /// </summary>
        private static double Phi(double z, double zSquared)
        {
            if (z <= 0.0)
            {
                return 0.0;
            }
            if (z >= 1.0)
            {
                return Phi1;
            }

            var previousPowZ = zSquared;
            var powZ = z;
            var nextPowZ = Math.Sqrt(powZ);
            var p = PInitial / (1.0 + nextPowZ);
            var ps = PsiPrime(powZ, previousPowZ);
            var sum = nextPowZ * (ps + ps) * p;
            while (true)
            {
                previousPowZ = powZ;
                powZ = nextPowZ;
                var previous = sum;
                nextPowZ = Math.Sqrt(powZ);
                var nextPs = PsiPrime(powZ, previousPowZ);
                p *= Pow2MinusTau / (1.0 + nextPowZ);
                sum += nextPowZ * ((nextPs + nextPs) - ((powZ + nextPowZ) * ps)) * p;
                if (!(sum > previous))
                {
                    return sum;
                }
                ps = nextPs;
            }
        }

        /// <summary>
        /// The correction for registers at or near zero, which the plain contribution
        /// would over-count.
        /// </summary>
        private double SmallRangeCorrection(int c0, int c4, int c8, int c10)
        {
            long m = this.Registers.Length;
            var alpha = m + (3L * (c0 + c4 + c8 + c10));
            var beta = m - c0 - c4;
            var gamma = (4L * c0) + (2L * c4) + (3L * c8) + c10;

            // The distinct count implied by the counts alone, as the fourth power of
            // the root of a quadratic.
            var quadRoot = (Math.Sqrt((double)((beta * beta) + (4 * alpha * gamma))) - beta) /
                (2.0 * alpha);
            var root = quadRoot * quadRoot;
            var z = root * root;

            var sum = 0.0;
            if (c0 > 0) sum += c0 * Sigma(z);
            if (c4 > 0) sum += c4 * Pow2MinusTauEtaX * PsiPrime(z, z * z);
            if (c8 > 0) sum += c8 * ((z * Pow4MinusTauEta01) + Pow4MinusTauEta1);
            if (c10 > 0) sum += c10 * ((z * Pow4MinusTauEta23) + Pow4MinusTauEta3);
            return sum;
        }

        /// <summary>
        /// The correction for saturated registers, which the plain contribution would
        /// under-count.
        /// </summary>
        private double LargeRangeCorrection(int c0, int c1, int c2, int c3)
        {
            long m = this.Registers.Length;
            var alpha = m + (3L * (c0 + c1 + c2 + c3));
            var beta = c0 + c1 + (2L * (c2 + c3));
            var gamma = m + (2L * c0) + c2 - c3;
            var z = Math.Sqrt((Math.Sqrt((double)((beta * beta) + (4 * alpha * gamma))) - beta) /
                (2.0 * alpha));

            var rootZ = Math.Sqrt(z);
            var sum = Phi(rootZ, z) * (c0 + c1 + c2 + c3);
            sum += z * (1 + rootZ) *
                ((c0 * Eta[0]) + (c1 * Eta[1]) + (c2 * Eta[2]) + (c3 * Eta[3]));
            sum += rootZ *
                (((c0 + c1) * ((z * Pow2MinusTauEta02) + Pow2MinusTauEta2)) +
                 ((c2 + c3) * ((z * Pow2MinusTauEta13) + Pow2MinusTauEta3)));
            return sum * Math.Pow(Pow2MinusTau, 65 - (int)this.P) /
                ((1 + rootZ) * (1 + z));
        }

        private static readonly double Pow2Tau = Math.Pow(2.0, Tau);
        private static readonly double Pow2MinusTau = Math.Pow(2.0, -Tau);
        private static readonly double Pow4MinusTau = Math.Pow(4.0, -Tau);
        private static readonly double EtaX = Eta[0] - Eta[1] - Eta[2] + Eta[3];
        private static readonly double Eta23X = (Eta[2] - Eta[3]) / EtaX;
        private static readonly double Eta13X = (Eta[1] - Eta[3]) / EtaX;
        private static readonly double Eta3012XX =
            ((Eta[3] * Eta[0]) - (Eta[1] * Eta[2])) / (EtaX * EtaX);
        private static readonly double Pow4MinusTauEta23 = Pow4MinusTau * (Eta[2] - Eta[3]);
        private static readonly double Pow4MinusTauEta01 = Pow4MinusTau * (Eta[0] - Eta[1]);
        private static readonly double Pow4MinusTauEta3 = Pow4MinusTau * Eta[3];
        private static readonly double Pow4MinusTauEta1 = Pow4MinusTau * Eta[1];
        private static readonly double Pow2MinusTauEtaX = Pow2MinusTau * EtaX;
        private static readonly double Phi1 = Eta[0] / (Pow2Tau * ((2.0 * Pow2Tau) - 1));
        private static readonly double PInitial = EtaX * (Pow4MinusTau / (2 - Pow2MinusTau));
        private static readonly double Pow2MinusTauEta02 = Pow2MinusTau * (Eta[0] - Eta[2]);
        private static readonly double Pow2MinusTauEta13 = Pow2MinusTau * (Eta[1] - Eta[3]);
        private static readonly double Pow2MinusTauEta2 = Pow2MinusTau * Eta[2];
        private static readonly double Pow2MinusTauEta3 = Pow2MinusTau * Eta[3];

        /// <summary>
        /// Writes the structure to a stream in the library's persistence format.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = new PayloadWriter();
            payload.WriteUInt32(this.P);
            payload.WriteBytes(this.Registers);

            PersistenceFormat.Write(
                stream,
                StructureId.UltraLogLog,
                PersistenceFormat.Identify(this.Hash),
                payload.WrittenSpan);
        }

        /// <summary>
        /// Reads a structure written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The structure that was written.</returns>
        public static UltraLogLog ReadFrom(Stream stream)
        {
            return Read(stream, null);
        }

        /// <summary>
        /// Reads a structure written by <see cref="WriteTo"/>, using the supplied
        /// hash function rather than the one named in the payload.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function the structure was written with.</param>
        /// <returns>The structure that was written.</returns>
        public static UltraLogLog ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            return Read(stream, hash);
        }

        private static UltraLogLog Read(Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(stream, StructureId.UltraLogLog, out var hashId);
            var reader = new PayloadReader(payload);

            var precision = reader.ReadUInt32();
            if (precision < MinPrecision || precision > MaxPrecision)
            {
                throw new InvalidDataException(
                    $"Structure claims precision {precision}, and this library only " +
                    $"builds precisions {MinPrecision} through {MaxPrecision}.");
            }

            var registers = reader.ReadBytes();
            if (registers.Length != 1 << (int)precision)
            {
                throw new InvalidDataException(
                    $"Structure claims precision {precision}, which is " +
                    $"{1 << (int)precision} registers, and carries " +
                    $"{registers.Length}.");
            }

            // A register records the position of a bit that a hash of this
            // precision could actually have stopped at: never below p - 1, and
            // never above 63. Anything else was not written by this library, and
            // would send the estimator outside the range its corrections cover.
            foreach (var register in registers)
            {
                if (register != 0 && (register >> 2) < precision - 1)
                {
                    throw new InvalidDataException(
                        $"Structure holds register value {register}, which records " +
                        $"a bit position below the {precision} index bits and so " +
                        "cannot have come from an insertion at this precision.");
                }
            }

            reader.ExpectEnd();

            var restored = new UltraLogLog(precision,
                PersistenceFormat.ResolveOrThrow(hashId, hash));
            registers.CopyTo(restored.Registers, 0);
            return restored;
        }

        /// <summary>
        /// Ertl's equation (16): the estimator's coefficients as a function of tau.
        /// </summary>
        private static double[] ComputeEta()
        {
            // omega_0..omega_3 from the paper, with base b = 2.
            static double Omega0(double t) =>
                Math.Pow(7, -t) - Math.Pow(8, -t);
            static double Omega1(double t) =>
                Math.Pow(3, -t) - Math.Pow(4, -t) - Math.Pow(7, -t) + Math.Pow(8, -t);
            static double Omega2(double t) =>
                Math.Pow(5, -t) - Math.Pow(6, -t) - Math.Pow(7, -t) + Math.Pow(8, -t);
            static double Omega3(double t) =>
                Math.Pow(7, -t) - Math.Pow(5, -t) + Math.Pow(6, -t) - Math.Pow(8, -t)
                - Math.Pow(3, -t) + Math.Pow(4, -t) + 1.0 - Math.Pow(2, -t);

            var omega = new Func<double, double>[] { Omega0, Omega1, Omega2, Omega3 };

            var normaliser = 0.0;
            foreach (var w in omega)
            {
                var atTau = w(Tau);
                normaliser += atTau * atTau / w(2.0 * Tau);
            }

            var eta = new double[4];
            for (var j = 0; j < 4; j++)
            {
                eta[j] = Math.Log(2.0) / Gamma(Tau) *
                    (omega[j](Tau) / omega[j](2.0 * Tau)) / normaliser;
            }
            return eta;
        }

        /// <summary>
        /// The gamma function, by the Lanczos approximation. Needed only to build
        /// <see cref="Eta"/> once.
        /// </summary>
        private static double Gamma(double x)
        {
            double[] g =
            {
                676.5203681218851, -1259.1392167224028, 771.32342877765313,
                -176.61502916214059, 12.507343278686905, -0.13857109526572012,
                9.9843695780195716e-6, 1.5056327351493116e-7,
            };

            if (x < 0.5)
            {
                return Math.PI / (Math.Sin(Math.PI * x) * Gamma(1.0 - x));
            }

            x -= 1.0;
            var a = 0.99999999999980993;
            var t = x + 7.5;
            for (var i = 0; i < g.Length; i++)
            {
                a += g[i] / (x + i + 1);
            }
            return Math.Sqrt(2.0 * Math.PI) * Math.Pow(t, x + 0.5) * Math.Exp(-t) * a;
        }
    }
}
