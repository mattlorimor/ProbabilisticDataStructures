using System;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Linq;

namespace Benchmarks
{
    /// <summary>
    /// Settles whether HyperLogLog++'s bias-correction tables beat the estimator
    /// <see cref="ProbabilisticDataStructures.HyperLogLogPlus"/> actually uses.
    /// </summary>
    /// <remarks>
    /// This is an accuracy study rather than a benchmark, so it does not run under
    /// BenchmarkDotNet. Run it with:
    /// <code>
    ///   dotnet run -c Release --project Benchmarks -- study hll-bias 14
    /// </code>
    /// <para>
    /// It derives the tables the way the paper did -- measuring the raw estimator's
    /// bias over many streams at known cardinalities -- rather than embedding the
    /// published ones. That is deliberate. The published tables are empirical data, and
    /// data reproduced incorrectly would be invisible here: the estimator would simply
    /// be quietly worse in the band the tables exist to fix. Deriving them makes the
    /// comparison checkable, and against this library's own hash function besides.
    /// </para>
    /// <para>
    /// Both estimators are measured over the same bare register array rather than
    /// through the library, so that what is compared is the estimator alone. Going
    /// through <c>HyperLogLogPlus</c> would fold in its sparse representation, which is
    /// exact at small cardinalities and would flatter whichever estimator it was paired
    /// with.
    /// </para>
    /// <para>
    /// The tables are trained on one set of streams and evaluated on another. Evaluating
    /// on the training streams would let them score against data they were fitted to,
    /// which is the one result that would be worthless.
    /// </para>
    /// </remarks>
    public sealed class HyperLogLogBiasStudy
    {
        private const int TrainingStreams = 50;
        private const int EvaluationStreams = 60;
        private const int TablePoints = 200;
        private const double AlphaInfinity = 0.5 / 0.693147180559945309417232121458;

        private readonly int precision;
        private readonly int m;
        private readonly int q;
        private readonly double alphaM;

        public HyperLogLogBiasStudy(int precision)
        {
            if (precision < 4 || precision > 18)
            {
                throw new ArgumentOutOfRangeException(nameof(precision), precision,
                    "Precision must be between 4 and 18.");
            }

            this.precision = precision;
            this.m = 1 << precision;
            this.q = 64 - precision;
            this.alphaM = 0.7213 / (1.0 + 1.079 / this.m);
        }

        public static void Run(string[] args)
        {
            var precision = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 14;
            new HyperLogLogBiasStudy(precision).Execute();
        }

        public void Execute()
        {
            var checkpoints = new int[TablePoints];
            var maxTraining = 5 * this.m;
            for (var j = 0; j < TablePoints; j++)
            {
                checkpoints[j] = (int)((this.m * 0.1) + (j * (maxTraining - (this.m * 0.1)) / (TablePoints - 1.0)));
            }

            // Kept per stream rather than summed. Choosing the threshold from averaged
            // values hides linear counting's per-stream variance -- the error of a mean
            // is far smaller than the mean of the errors -- and pushes the threshold
            // well above where it belongs.
            var rawPerStream = new double[TablePoints, TrainingStreams];
            var linearPerStream = new double[TablePoints, TrainingStreams];
            var rawTotal = new double[TablePoints];

            for (var stream = 0; stream < TrainingStreams; stream++)
            {
                var registers = new byte[this.m];
                var next = 0;

                for (long i = 1; i <= maxTraining && next < TablePoints; i++)
                {
                    this.Observe(registers, Key(stream, i));

                    while (next < TablePoints && i == checkpoints[next])
                    {
                        var (raw, zeros) = this.RawEstimate(registers);
                        rawPerStream[next, stream] = raw;
                        linearPerStream[next, stream] = zeros > 0
                            ? this.m * Math.Log((double)this.m / zeros)
                            : raw;
                        rawTotal[next] += raw;
                        next++;
                    }
                }
            }

            var rawTable = new double[TablePoints];
            var biasTable = new double[TablePoints];
            for (var j = 0; j < TablePoints; j++)
            {
                rawTable[j] = rawTotal[j] / TrainingStreams;
                biasTable[j] = rawTable[j] - checkpoints[j];
            }

            var threshold = this.ChooseThreshold(
                checkpoints, rawPerStream, linearPerStream, rawTable, biasTable);

            Console.WriteLine(
                $"precision {this.precision}, m={this.m}, nominal error " +
                $"{1.04 / Math.Sqrt(this.m):P2}");
            Console.WriteLine(
                $"  tables trained on {TrainingStreams} streams, {TablePoints} points, " +
                $"bias {biasTable.Min():F0}..{biasTable.Max():F0}");
            Console.WriteLine($"  linear-counting threshold chosen at n={threshold}");
            Console.WriteLine();

            var ratios = new[]
            {
                0.125, 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 2.25, 2.5,
                2.75, 3.0, 3.5, 4.0, 5.0, 6.0, 8.0, 12.0, 20.0,
            };

            var points = ratios.Select(r => (long)(r * this.m)).ToArray();
            var ertlError = new double[points.Length];
            var tableError = new double[points.Length];

            for (var stream = 0; stream < EvaluationStreams; stream++)
            {
                var registers = new byte[this.m];
                var next = 0;

                for (long i = 1; i <= points[^1] && next < points.Length; i++)
                {
                    // Offset well past the training streams, so nothing here was fitted.
                    this.Observe(registers, Key(1_000_000 + stream, i));

                    while (next < points.Length && i == points[next])
                    {
                        var n = points[next];
                        ertlError[next] += Math.Abs(this.Ertl(registers) - n) / n;
                        tableError[next] +=
                            Math.Abs(this.WithTables(registers, rawTable, biasTable, threshold) - n) / n;
                        next++;
                    }
                }
            }

            Console.WriteLine($"mean |error| over {EvaluationStreams} held-out streams");
            Console.WriteLine($"{"n",10} {"n/m",6} {"Ertl",9} {"tables",9}");

            for (var j = 0; j < points.Length; j++)
            {
                Console.WriteLine(
                    $"{points[j],10} {ratios[j],6:F2} " +
                    $"{ertlError[j] / EvaluationStreams,9:P2} " +
                    $"{tableError[j] / EvaluationStreams,9:P2}");
            }

            var ertlMean = ertlError.Sum() / EvaluationStreams / points.Length;
            var tableMean = tableError.Sum() / EvaluationStreams / points.Length;

            Console.WriteLine();
            Console.WriteLine($"mean       Ertl {ertlMean:P3}   tables {tableMean:P3}");
            Console.WriteLine(
                $"worst      Ertl {ertlError.Max() / EvaluationStreams:P3}   " +
                $"tables {tableError.Max() / EvaluationStreams:P3}");
        }

        /// <summary>
        /// Picks the cardinality below which linear counting is used, by minimising the
        /// error the whole decision rule produces over the training streams.
        /// </summary>
        private int ChooseThreshold(
            int[] checkpoints,
            double[,] rawPerStream,
            double[,] linearPerStream,
            double[] rawTable,
            double[] biasTable)
        {
            var best = 0;
            var bestError = double.MaxValue;

            foreach (var candidate in checkpoints)
            {
                var total = 0.0;

                for (var j = 0; j < TablePoints; j++)
                {
                    for (var stream = 0; stream < TrainingStreams; stream++)
                    {
                        var raw = rawPerStream[j, stream];
                        var corrected = raw <= 5.0 * this.m
                            ? raw - EstimateBias(raw, rawTable, biasTable)
                            : raw;
                        var linear = linearPerStream[j, stream];
                        var chosen = linear <= candidate ? linear : corrected;

                        total += Math.Abs(chosen - checkpoints[j]) / checkpoints[j];
                    }
                }

                if (total < bestError)
                {
                    bestError = total;
                    best = candidate;
                }
            }

            return best;
        }

        private double WithTables(byte[] registers, double[] rawTable, double[] biasTable, int threshold)
        {
            var (raw, zeros) = this.RawEstimate(registers);
            var corrected = raw <= 5.0 * this.m ? raw - EstimateBias(raw, rawTable, biasTable) : raw;
            var linear = zeros > 0 ? this.m * Math.Log((double)this.m / zeros) : corrected;

            return linear <= threshold ? linear : corrected;
        }

        /// <summary>
        /// The bias measured near a raw estimate, averaged over its nearest neighbours
        /// in the table, as HyperLogLog++ specifies.
        /// </summary>
        private static double EstimateBias(double estimate, double[] rawTable, double[] biasTable)
        {
            if (estimate <= rawTable[0])
            {
                return biasTable[0];
            }

            if (estimate >= rawTable[^1])
            {
                return biasTable[^1];
            }

            var lower = 0;
            while (lower < TablePoints - 1 && rawTable[lower + 1] < estimate)
            {
                lower++;
            }

            var from = Math.Max(0, lower - 2);
            var to = Math.Min(TablePoints - 1, lower + 3);
            var sum = 0.0;

            for (var j = from; j <= to; j++)
            {
                sum += biasTable[j];
            }

            return sum / (to - from + 1);
        }

        private double Ertl(byte[] registers)
        {
            var counts = new int[this.q + 2];
            foreach (var register in registers)
            {
                counts[register]++;
            }

            var z = this.m * Tau((this.m - (double)counts[this.q + 1]) / this.m);

            for (var k = this.q; k >= 1; k--)
            {
                z = 0.5 * (z + counts[k]);
            }

            z += this.m * Sigma(counts[0] / (double)this.m);

            return AlphaInfinity * this.m * this.m / z;
        }

        private static double Sigma(double x)
        {
            if (x == 1.0)
            {
                return double.PositiveInfinity;
            }

            double y = 1, z = x, previous;
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

        private static double Tau(double x)
        {
            if (x == 0.0 || x == 1.0)
            {
                return 0.0;
            }

            double y = 1, z = 1 - x, previous;
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

        private (double Raw, int Zeros) RawEstimate(byte[] registers)
        {
            var sum = 0.0;
            var zeros = 0;

            foreach (var register in registers)
            {
                sum += 1.0 / (1UL << register);
                if (register == 0)
                {
                    zeros++;
                }
            }

            return (this.alphaM * this.m * this.m / sum, zeros);
        }

        private void Observe(byte[] registers, ulong hash)
        {
            var index = (int)(hash >> (64 - this.precision));
            var remaining = hash << this.precision;
            var rho = remaining == 0
                ? (byte)(this.q + 1)
                : (byte)(System.Numerics.BitOperations.LeadingZeroCount(remaining) + 1);

            if (rho > registers[index])
            {
                registers[index] = rho;
            }
        }

        private static ulong Key(long stream, long i)
        {
            Span<byte> bytes = stackalloc byte[16];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, i);
            BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], stream);
            return XxHash3.HashToUInt64(bytes);
        }
    }
}
