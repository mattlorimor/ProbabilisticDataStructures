using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The arithmetic behind the capacity documentation: what a structure's constructor
    /// accepts, what its read is charged, and how both are written into a table cell.
    /// </summary>
    /// <remarks>
    /// Nothing here builds a structure of the sizes it reports. A limit comes from the
    /// library's own sizing code and its limit symbols. A read charge comes from the
    /// internal <see cref="ReadBudget"/>, read off after a real read of a small
    /// structure, and is carried to the size asked about by a line that was fitted to
    /// several small reads and has to pass through every one of them. The line is
    /// exact where a structure is one array, and carries a stated slack where
    /// the reader's charges round each array up to eight bytes.
    /// </remarks>
    internal static class CapacityModel
    {
        /// <summary>The budget every <c>ReadFrom</c> without options reads under.</summary>
        internal const long Budget = ReadOptions.DefaultMemoryBudget;

        /// <summary>
        /// Numbers the rows worked out on the way that the document also states, by name.
        /// The claims about them read them here, so a figure in the prose is the one the
        /// rows used.
        /// </summary>
        internal static System.Collections.Concurrent.ConcurrentDictionary<string, double> Facts { get; } = new();

        /// <summary>
        /// The payload each read measured was written with, by structure and what the read
        /// was charged. A fit of the payload is made over the same measurements as the fit
        /// of the charge, which is how the document's ceiling on what can be saved is found
        /// without writing anything that large.
        /// </summary>
        internal static System.Collections.Concurrent.ConcurrentDictionary<(StructureId, long), long> Payloads { get; } = new();

        internal static long EnvelopeBytes()
        {
            const BindingFlags Constants = BindingFlags.NonPublic | BindingFlags.Static;
            return (int)typeof(PersistenceFormat).GetField("HeaderLength", Constants)!.GetRawConstantValue()!
                + (int)typeof(PersistenceFormat).GetField("ChecksumLength", Constants)!.GetRawConstantValue()!;
        }

        /// <summary>
        /// The most payload a structure can be saved with. The writer, the frame the payload
        /// is wrapped in and the reader are each one array, and the frame is the payload
        /// and its envelope, so the payload is the longest array less the envelope.
        /// </summary>
        internal static readonly long MaxPayload = Array.MaxLength - EnvelopeBytes();

        /// <summary>The cell for a structure whose largest build can be saved.</summary>
        internal const string AsBuilt = "as built";

        /// <summary>
        /// A line over the payloads of the structures a charge line was fitted to. A payload
        /// is exact in the size of what it holds, so the line passes through every one.
        /// </summary>
        internal static ChargeLine PayloadLine(string chargeLine, StructureId id, long slack = 0)
        {
            var points = ChargeLine.Known[chargeLine].Points
                .Select(p => (p.Units, Payloads[(id, p.Charge)]))
                .ToArray();
            return ChargeLine.Fit("payload of " + chargeLine, slack, points);
        }

        // ---- Searching --------------------------------------------------------------

        /// <summary>
        /// The largest <c>x</c> in <c>[low, high]</c> for which <paramref name="ok"/> holds,
        /// for a predicate that holds up to some point and fails after it.
        /// </summary>
        internal static ulong Largest(ulong low, ulong high, Func<ulong, bool> ok)
        {
            Assert.IsTrue(ok(low), $"the search starts at {low:N0}, where the predicate already fails");
            while (low < high)
            {
                var mid = low + (high - low + 1) / 2;
                if (ok(mid))
                {
                    low = mid;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return low;
        }

        // ---- What the packed bucket array allocates -----------------------------------

        private static readonly MethodInfo DataLengthMethod =
            typeof(Buckets).GetMethod("DataLength", BindingFlags.NonPublic | BindingFlags.Static)!;

        /// <summary>
        /// What <c>Buckets</c> allocates for <paramref name="count"/> buckets of
        /// <paramref name="bits"/> bits: its own, private, sizing function.
        /// </summary>
        internal static long LibraryBytes(uint count, byte bits)
        {
            Assert.IsNotNull(DataLengthMethod, "Buckets.DataLength is gone or renamed");
            return (long)DataLengthMethod.Invoke(null, new object[] { count, bits })!;
        }

        /// <summary>The bytes <paramref name="count"/> buckets of <paramref name="bits"/> bits occupy.</summary>
        internal static ulong TrueBytes(ulong count, int bits) => (count * (ulong)bits + 7) / 8;

        /// <summary>
        /// Whether <c>Buckets</c> sizes this many buckets correctly. It does not once
        /// the count times the width passes what 32 bits hold: the array it builds is
        /// then truncated and the filter fails on its first <c>Add</c>.
        /// </summary>
        internal static bool BucketsAreSound(ulong count, int bits) =>
            count <= uint.MaxValue
            && LibraryBytes((uint)count, (byte)bits) == (long)TrueBytes(count, bits);

        // ---- Reading ----------------------------------------------------------------

        /// <summary>
        /// What a real read of <paramref name="structure"/> is charged, in bytes, read off
        /// the internal budget.
        /// </summary>
        internal static long ChargeOf<T>(StructureId id, T structure) where T : IBinaryPersistable<T> =>
            ChargeOf<T>(id, structure.ToByteArray());

        internal static long ChargeOf<T>(StructureId id, byte[] bytes) where T : IBinaryPersistable<T>
        {
            var budget = new ReadBudget(long.MaxValue, id);
            using var stream = new MemoryStream(bytes, writable: false);
            var read = T.ReadFrom(stream, new ReadOptions(budget));
            GC.KeepAlive(read);
            Payloads[(id, budget.Charged)] = bytes.Length - 18;
            return budget.Charged;
        }

        /// <summary>
        /// A read charge that is a straight line in some measure of the structure's size:
        /// <c>Slope * units + Intercept</c>, give or take <c>Slack</c>.
        /// </summary>
        internal sealed class ChargeLine
        {
            private readonly double slope;
            private readonly double slopeError;
            private readonly double intercept;
            private readonly double slack;

            /// <summary>
            /// Every line fitted so far, by name. The document states the bytes a read is
            /// charged for a unit of several structures, and the claims about them read the
            /// slope here, from the fit the numbers were worked out with.
            /// </summary>
            internal static System.Collections.Concurrent.ConcurrentDictionary<string, ChargeLine> Known { get; } = new();

            /// <summary>The measurements the line was fitted to, smallest first.</summary>
            internal (long Units, long Charge)[] Points { get; init; } = Array.Empty<(long, long)>();

            private ChargeLine(double slope, double slopeError, double intercept, double slack)
            {
                this.slope = slope;
                this.slopeError = slopeError;
                this.intercept = intercept;
                this.slack = slack;
            }

            /// <summary>The bytes the read is charged for each unit.</summary>
            internal double Slope => this.slope;

            /// <summary>The bytes a read of no units is charged.</summary>
            internal double Intercept => this.intercept;

            /// <summary>
            /// Fits a line through the first and last of several measurements and requires
            /// it to pass within <paramref name="slack"/> bytes of every one of them. When
            /// the slope is a whole number of bytes per unit to within the slack, as it is
            /// when the unit is what the reader charges for, it is taken as that number and
            /// carries no error; otherwise its error is what the slack can move it by over
            /// the span measured.
            /// </summary>
            internal static ChargeLine Fit(string what, long slack, params (long Units, long Charge)[] points)
            {
                Assert.IsGreaterThanOrEqualTo(3, points.Length, $"{what}: a line needs a point to check it against");
                var sorted = points.OrderBy(p => p.Units).ToArray();
                var first = sorted[0];
                var last = sorted[^1];
                var span = (double)(last.Units - first.Units);
                Assert.IsGreaterThan(0.0, span, $"{what}: the measurements are all one size");

                var slope = (last.Charge - first.Charge) / span;
                var error = 2.0 * slack / span;
                if (Math.Abs(slope - Math.Round(slope)) * span <= 2 * slack)
                {
                    slope = Math.Round(slope);
                    error = 0;
                }

                var offsets = sorted.Select(p => p.Charge - slope * p.Units).OrderBy(x => x).ToArray();
                var intercept = offsets[offsets.Length / 2];

                foreach (var p in sorted)
                {
                    var off = p.Charge - (slope * p.Units + intercept);
                    Assert.IsLessThanOrEqualTo((double)slack, Math.Abs(off),
                        $"{what}: a read of {p.Units:N0} units was charged {p.Charge:N0}, which is {off:N0} bytes " +
                        $"from the line {slope} * units + {intercept}; the charge is not that line in these units");
                }

                var line = new ChargeLine(slope, error, intercept, slack) { Points = sorted };
                Known[what] = line;
                return line;
            }

            /// <summary>The same line, with a wider slack for what cannot be measured here.</summary>
            internal ChargeLine Widen(long extra) =>
                new(this.slope, this.slopeError, this.intercept, this.slack + extra);

            /// <summary>The same line with every unit costing <paramref name="perUnit"/> bytes more.</summary>
            internal ChargeLine Plus(double perUnit, string name)
            {
                var line = new ChargeLine(this.slope + perUnit, this.slopeError, this.intercept, this.slack);
                Known[name] = line;
                return line;
            }

            /// <summary>What a read of this many units is charged, on the line.</summary>
            internal double At(double units) => this.slope * units + this.intercept;

            /// <summary>The most a read of this many units can be charged, given the slack.</summary>
            internal double Upper(double units) =>
                (this.slope + this.slopeError) * units + this.intercept + this.slack;

            /// <summary>The least a read of this many units can be charged, given the slack.</summary>
            internal double Lower(double units) =>
                (this.slope - this.slopeError) * units + this.intercept - this.slack;

            /// <summary>
            /// Whether a read of this many units is charged within the budget whatever the
            /// slack does (<see cref="Surely"/>), or can be (<see cref="Possibly"/>).
            /// </summary>
            internal bool Surely(ulong units, long budget = Budget) => this.Upper(units) <= budget;

            /// <inheritdoc cref="Surely"/>
            internal bool Possibly(ulong units, long budget = Budget) => this.Lower(units) <= budget;
        }

        // ---- Writing a number into a table cell -----------------------------------------

        /// <summary>A number written in full, as the table's exact cells are.</summary>
        internal static string Exact(ulong value) =>
            value.ToString("N0", CultureInfo.InvariantCulture);

        /// <summary>
        /// A number that can only be measured at the scale it describes, written with a
        /// dagger. The measurement is what the document states; the estimate is what the
        /// suite can work out at sizes it can afford, and has to land within
        /// <paramref name="tolerance"/> of it, so that a change to the structure's cost
        /// that moves the number is noticed even though the number itself is not rebuilt.
        /// </summary>
        /// <param name="value">The number as it was measured.</param>
        /// <param name="estimate">What the suite works out from reads it can afford.</param>
        /// <param name="tolerance">How far the two may be apart, as a fraction.</param>
        /// <param name="why">Why it cannot be worked out exactly, as the exemption states it.</param>
        /// <param name="whyHolds">Whether that reason is still true. An exemption is a claim, and is checked.</param>
        internal static string Measured(
            ulong value, Func<double> estimate, double tolerance, string why, Func<bool> whyHolds)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(why), "a measured number is exempt without a reason");
            Assert.IsTrue(whyHolds(), $"the reason a measured number is not worked out exactly has stopped being true: {why}");
            var worked = estimate();
            Assert.IsLessThanOrEqualTo(tolerance, Math.Abs(worked / value - 1),
                $"the measured {value:N0} and the {worked:N0} worked out from small reads are further apart than {tolerance:P0}");
            return Exact(value) + " †";
        }

        /// <summary>
        /// A number written to a few digits, as the cells are whose value the charge
        /// model carries a slack of a few bytes into. The two ends the slack allows have
        /// to write the same, so a number that sits on a rounding edge fails here rather
        /// than being documented as one of two.
        /// </summary>
        internal static string About(ulong low, ulong high, int decimals = 2)
        {
            var a = Approximately(Math.Min(low, high), decimals);
            var b = Approximately(Math.Max(low, high), decimals);
            Assert.AreEqual(a, b,
                $"the model puts this between {low:N0} and {high:N0}, which are not one number to {decimals} " +
                "decimal places; write it to fewer");
            return a;
        }

        internal static string About(ulong value, int decimals = 2) => About(value, value, decimals);

        private static string Approximately(ulong value, int decimals)
        {
            var (unit, name) = value >= 1_000_000_000_000_000_000UL ? (1e18, "quintillion")
                : value >= 1_000_000_000_000_000UL ? (1e15, "quadrillion")
                : value >= 1_000_000_000_000UL ? (1e12, "trillion")
                : value >= 1_000_000_000UL ? (1e9, "billion")
                : value >= 1_000_000UL ? (1e6, "million")
                : (1e3, "thousand");
            var scaled = Math.Round(value / unit, decimals, MidpointRounding.AwayFromZero);
            return "≈ " + scaled.ToString("F" + decimals, CultureInfo.InvariantCulture) + " " + name;
        }
    }
}
