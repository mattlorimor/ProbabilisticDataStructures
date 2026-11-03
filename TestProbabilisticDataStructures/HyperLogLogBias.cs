using System;
using System.Numerics;
using System.Reflection;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// What CAPACITY.md says about the bias of HyperLogLog's raw estimator, worked out
    /// without the library, so that the claims can hold the library to it.
    /// </summary>
    /// <remarks>
    /// Two derivations, neither of which passes through <c>HyperLogLog.cs</c>.
    /// <para>
    /// <b>The constant.</b> Flajolet, Fusy, Gandouet and Meunier define it as
    /// alpha_m = (m · ∫₀^∞ (log2((2 + u) / (1 + u)))^m du)⁻¹, and print 0.673, 0.697 and 0.709
    /// for m = 16, 32 and 64. <see cref="ExactAlpha"/> integrates that, and
    /// <c>TestHyperLogLogBias</c> holds it to the three printed values. The limit is
    /// 1 / (2 ln 2), and the paper's 0.7213 for large m is its four-digit rounding.
    /// </para>
    /// <para>
    /// <b>The band.</b> Hash n = λm items into m registers, and (Poissonised) each register
    /// receives Poisson(λ) of them independently, so a register's value M has
    /// P(M ≤ r) = exp(−λ 2^−r) for r ≥ 0 when the hash is as long as it likes. The raw estimate
    /// α_m m² / Σ 2^−M_j is then α_m m / E[2^−M] to the first order, and
    /// <see cref="RawRatio"/> is that over n. It is the same model
    /// <see cref="TestHyperLogLogRange.PoissonSketch"/> samples from, but evaluated as a sum
    /// and not drawn, and the expected state it describes is what
    /// <see cref="FillExpected"/> hands to the library to count.
    /// </para>
    /// </remarks>
    internal static class HyperLogLogBias
    {
        /// <summary>The limit of alpha_m as the register count grows, 1 / (2 ln 2).</summary>
        internal static readonly double LimitAlpha = 1.0 / (2.0 * Math.Log(2.0));

        /// <summary>
        /// ln(1 + x) without losing the digits of a small x to the addition, which the
        /// integral below needs at 2^30 registers.
        /// </summary>
        internal static double Log1P(double x)
        {
            var y = 1.0 + x;
            return y == 1.0 ? x : Math.Log(y) * x / (y - 1.0);
        }

        /// <summary>
        /// alpha_m from its definition in the paper, by Simpson's rule on t = m u, so that
        /// the integrand is exp(−t / (2 ln 2)) at every large m and one step size serves
        /// them all. The integrand is 1e-100 at the upper limit of the range.
        /// </summary>
        internal static double ExactAlpha(double m)
        {
            // log2((2 + u) / (1 + u)) = 1 + log2(1 - u / (2 (1 + u))), written so that nothing is
            // lost to subtracting 1 from a number a billionth away from it.
            double Integrand(double t)
            {
                var u = t / m;
                var h = Log1P(-u / (2.0 * (1.0 + u))) / Math.Log(2.0);
                return Math.Exp(m * Log1P(h));
            }

            const int Steps = 60_000;
            const double Upper = 120.0;
            var step = Upper / Steps;
            var sum = Integrand(0) + Integrand(Upper);
            for (var i = 1; i < Steps; i++)
            {
                sum += (i % 2 == 1 ? 4.0 : 2.0) * Integrand(i * step);
            }

            // alpha = 1 / (m ∫ f(u) du) and ∫ f(u) du = (1 / m) ∫ f(t / m) dt.
            return 1.0 / (sum * step / 3.0);
        }

        /// <summary>
        /// E[2^−M] for a register that receives Poisson(<paramref name="lambda"/>) items under a
        /// hash with no end: Σ over r ≥ 0 of 2^−r (F(r) − F(r − 1)), with F(r) = exp(−λ 2^−r)
        /// and F(−1) = 0.
        /// </summary>
        internal static double ExpectedInverseValue(double lambda)
        {
            var sum = 0.0;
            var previous = 0.0;
            for (var r = 0; r <= 100; r++)
            {
                var weight = Math.ScaleB(1.0, -r);
                var cumulative = Math.Exp(-lambda * weight);
                sum += weight * (cumulative - previous);
                previous = cumulative;
            }

            return sum;
        }

        /// <summary>
        /// The raw estimate over the truth, α_m m / E[2^−M] / (λ m), for a given alpha.
        /// </summary>
        internal static double RawRatio(double lambda, double alpha) =>
            alpha / (lambda * ExpectedInverseValue(lambda));

        /// <summary>
        /// Puts <paramref name="registers"/> in the state the model expects after
        /// λ · registers.Length items, for a hash with <paramref name="q"/> bits below the
        /// register index. The registers with value at most r are the first
        /// round(m F(r)) of them, so every boundary is within half a register of its
        /// expectation, and the rest are at the cap, q + 1.
        /// </summary>
        internal static void FillExpected(byte[] registers, double lambda, int q)
        {
            var start = 0;
            for (var r = 0; r <= q; r++)
            {
                var end = (int)Math.Round(registers.Length * Math.Exp(-lambda * Math.ScaleB(1.0, -r)));
                Array.Fill(registers, (byte)r, start, end - start);
                start = end;
            }

            Array.Fill(registers, (byte)(q + 1), start, registers.Length - start);
        }

        private static readonly MethodInfo CalculateAlpha = typeof(HyperLogLog)
            .GetMethod("CalculateAlpha", BindingFlags.NonPublic | BindingFlags.Static)!;

        /// <summary>
        /// The constant the library uses at <paramref name="m"/> registers, from the function
        /// that sets it, so that no sketch of the size has to be built.
        /// </summary>
        internal static double LibraryAlpha(uint m) => (double)CalculateAlpha.Invoke(null, new object[] { m })!;

        /// <summary>
        /// The library's alpha over alpha_m from the integral, less one: how far low the
        /// raw estimate runs from the constant alone.
        /// </summary>
        internal static double AlphaBias(uint m) => (LibraryAlpha(m) / ExactAlpha(m)) - 1.0;

        /// <summary>
        /// How far from the truth, as a fraction of it, <c>Count()</c> is on the state the
        /// model expects after λm distinct items, at <paramref name="m"/> registers.
        /// </summary>
        internal static double CountBias(uint m, double lambda)
        {
            var hll = new HyperLogLog(m);
            FillExpected(hll.RegisterState, lambda, 32 - BitOperations.Log2(m));
            var n = lambda * m;
            return (hll.Count() - n) / n;
        }

        /// <summary>
        /// The same for <see cref="HyperLogLogPlus"/>, at its largest precision, whose registers
        /// are cut off 64 − p bits down.
        /// </summary>
        internal static double PlusBias(HyperLogLogPlus dense, double lambda)
        {
            var registers = dense.DenseRegisterState!;
            FillExpected(registers, lambda, 64 - BitOperations.Log2((uint)registers.Length));
            var n = lambda * registers.Length;
            return (dense.Count() - n) / n;
        }
    }
}
