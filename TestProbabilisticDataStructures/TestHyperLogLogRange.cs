using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// HyperLogLog at the far end of its 32-bit hash: the estimate past the point where
    /// the paper's large-range correction used to take over, the registers the hash
    /// saturates, the ceiling a full sketch reports, and the floor that keeps a
    /// non-empty sketch from counting zero.
    /// </summary>
    /// <remarks>
    /// Until this was fixed the estimator applied Flajolet et al.'s large-range
    /// correction, -2^32 ln(1 - E/2^32), from E = 2^32/30. That correction assumes the
    /// raw estimate counts distinct hash values, so that collisions in the 32-bit space
    /// have to be added back. It does not. A 32-bit register is exactly the register an
    /// unbounded hash would have produced, capped at q + 1: the bits past the 32nd are
    /// never read, so items whose 32-bit hashes collide move the registers exactly as
    /// they would have with a wider hash, unless the run of zeros reaches the cap. The
    /// raw estimate therefore already estimates the number of items, and the
    /// correction inflated it: by 3.3% at 2^28 and 13% at 2^30, measured through the
    /// update below, and past a raw estimate of 2^32 the logarithm's argument went
    /// negative and Count() returned 0. The only thing 32 bits change is the cap, which
    /// pushes the other way, and that is what Ertl's tau term accounts for.
    /// </remarks>
    [TestClass]
    public class TestHyperLogLogRange
    {
        // ---- The oracle -----------------------------------------------------------

        private static int Q(uint m) => 32 - BitOperations.Log2(m);

        private static double Alpha(uint m) => m switch
        {
            16 => 0.673,
            32 => 0.697,
            64 => 0.709,
            _ => 0.7213 / (1.0 + (1.079 / m)),
        };

        /// <summary>
        /// The expected contribution 2^-M of a register whose value is at least q + 1,
        /// in units of 2^-q per register, had the hash been wide enough for M to go
        /// past q + 1. In the Poisson model a register receives Poisson(lambda) items
        /// and P(M &lt;= q + i) = exp(-lambda 2^-(q+i)). Writing x = exp(-lambda 2^-q),
        /// the fraction of registers below q + 1, that probability is x^(2^-i), and
        /// <code>
        ///   sum over i &gt;= 1 of 2^-i * (x^(2^-i) - x^(2^-(i-1))).
        /// </code>
        /// Derived here from the model rather than copied from Ertl: it telescopes to
        /// his tau(x) = (1 - x - sum_k (1 - x^(2^-k))^2 2^-k) / 3, and
        /// <see cref="TestTheSaturatedContributionIsErtlsTau"/> holds the two forms to
        /// each other. Checked 2026-11-01 by hand at x = 1/e, where both give 0.1802.
        /// </summary>
        internal static double SaturatedContribution(double x)
        {
            var sum = 0.0;
            var previous = x;
            for (var i = 1; i <= 64; i++)
            {
                var current = Math.Pow(x, Math.Pow(2, -i));
                sum += Math.Pow(2, -i) * (current - previous);
                previous = current;
            }

            return sum;
        }

        /// <summary>
        /// The estimate a register state should give: the paper's raw estimator,
        /// alpha_m m^2 / sum 2^-M_j, with each saturated register's 2^-(q+1) replaced
        /// by what it would have contributed with a wider hash; linear counting at or
        /// below 5m/2 when registers are zero, as in the paper; and no large-range
        /// correction. A sketch with every register saturated estimates as one with a
        /// single register at q instead, which is this library's rule rather than
        /// anyone's paper.
        /// </summary>
        internal static double ExpectedEstimate(byte[] registers)
        {
            var count = (uint)registers.Length;
            var m = (double)count;
            var q = Q(count);

            var plain = 0.0;
            var zeros = 0;
            var saturated = 0;
            foreach (var r in registers)
            {
                if (r >= q + 1)
                {
                    saturated++;
                }
                else
                {
                    plain += Math.Pow(2, -r);
                    zeros += r == 0 ? 1 : 0;
                }
            }

            if (saturated == registers.Length)
            {
                saturated--;
                plain = Math.Pow(2, -q);
            }

            var denominator = plain + (m * SaturatedContribution((m - saturated) / m) * Math.Pow(2, -q));
            var estimate = Alpha(count) * m * m / denominator;
            if (estimate <= 2.5 * m && zeros > 0)
            {
                estimate = m * Math.Log(m / zeros);
            }

            return estimate;
        }

        private static void AssertEstimate(double expected, ulong actual, string because)
        {
            // The oracle sums in its own order, so the last bit or two of the double can
            // differ, and Count() truncates. One count, or a part in 10^12, covers both.
            var tolerance = Math.Max(1.0, expected * 1e-12);
            Assert.IsLessThanOrEqualTo(tolerance, Math.Abs(actual - expected),
                $"{because} Expected {expected:F1}, got {actual}.");
        }

        // ---- Reaching billions of items ---------------------------------------------

        /// <summary>
        /// A sketch as it stands after a stream of about <paramref name="n"/> distinct
        /// items, built with one hash per register through the update Add uses.
        /// </summary>
        /// <remarks>
        /// With Poisson(n) items hashed uniformly, each register independently receives
        /// Poisson(n/m) of them, and the largest rho among those has
        /// P(M &lt;= r) = exp(-(n/m) 2^-r) for r from 0 to q, and M = q + 1 otherwise,
        /// since that is as long a run as the hash has room for. So drawing each
        /// register's M from that distribution and handing the update one hash whose
        /// rho is M builds exactly the sketch of a Poisson-sized stream of n items, for
        /// m updates instead of n.
        /// <para>
        /// Checked outside the suite against the two things it stands in for, at
        /// m = 2^14 and 32 streams each: real Add calls on distinct keys, and n
        /// synthetic hashes fed to the update one at a time. At 2^28 the old estimator
        /// read +3.14% (se 0.16%) from real adds, +2.94% (0.14%) from the sequential
        /// hashes and +3.14% (0.11%) from this, with 6.08%, 6.04% and 6.07% of
        /// registers saturated; the three agreed as closely at 2^24, 2^25, 2^26 and
        /// 2^27.
        /// </para>
        /// </remarks>
        internal static HyperLogLog PoissonSketch(uint m, double n, Random random)
        {
            var q = Q(m);
            var hll = new HyperLogLog(m);
            var lambda = n / m;
            for (uint j = 0; j < m; j++)
            {
                var exponential = -Math.Log(1.0 - random.NextDouble());
                var rho = Math.Ceiling(Math.Log2(lambda / exponential));
                if (rho < 1)
                {
                    continue;
                }

                var r = (int)Math.Min(rho, q + 1);
                var remainder = r <= q ? 1u << (q - r) : 0u;
                hll.AddHash((m == 1 ? 0u : j << q) | remainder);
            }

            return hll;
        }

        private static double SaturatedFraction(HyperLogLog hll, uint m)
        {
            var q = Q(m);
            return hll.RegisterState.Count(r => r == q + 1) / (double)m;
        }

        /// <summary>
        /// The estimate from 2^28 to where the hash runs out, as a distribution over
        /// independent streams: its mean within four standard errors of the truth and
        /// its spread within the window TestSketchBounds holds the small range to.
        /// <para>
        /// Before the fix every row failed on its first point, 2^28, at +3.84%, +3.09%
        /// and +3.21% against bounds of 1.8%, 0.9% and 0.4%. Measured further out over
        /// 4,096 streams at m = 16 and m = 1024, the old estimator ran +15% and +13%
        /// high at 2^30 and +32% and +29% at 2^31, where its spread was already 2.0 and
        /// 1.6 times nominal; at m = 1024 it was +85% at 2^32 and 0 at 2^33 on every
        /// stream, as it was from 2^32.4, about 5.7 billion, at m = 2^14. After it,
        /// every mean here is within 2.3 standard errors of zero --
        /// at most 1.0% at m = 16, 0.24% at m = 1024 and 0.22% at m = 16384 -- and every
        /// spread is 0.94 to 1.13 times nominal.
        /// </para>
        /// <para>
        /// Each row stops where the hash does. At m = 16 the tau term's own bias shows
        /// from 2^32 (+1.1% there over 65,536 streams, a twentieth of the nominal 26%)
        /// and from 2^33 most sketches are full; at m = 1024 the spread is 1.8 times
        /// nominal at 2^34 and full sketches dominate from 2^35.
        /// </para>
        /// </summary>
        [TestMethod]
        [DataRow(16u, 4096, 31)]
        [DataRow(1024u, 256, 33)]
        [DataRow(16384u, 64, 33)]
        public void TestTheEstimateHoldsUntilTheHashRunsOut(uint m, int trials, int topExponent)
        {
            var random = new Random(20261101 + (int)m);
            var nominal = 1.04 / Math.Sqrt(m);

            for (var exponent = 28; exponent <= topExponent; exponent++)
            {
                var n = Math.Pow(2, exponent);
                var errors = new double[trials];
                var saturated = 0.0;
                for (var t = 0; t < trials; t++)
                {
                    var hll = PoissonSketch(m, n, random);
                    errors[t] = (hll.Count() - n) / n;
                    saturated += SaturatedFraction(hll, m) / trials;
                }

                var mean = errors.Average();
                var spread = Math.Sqrt(errors.Sum(e => (e - mean) * (e - mean)) / (trials - 1));
                var standardError = spread / Math.Sqrt(trials);
                var where = $"m={m}, n=2^{exponent}";
                Console.WriteLine($"{where}: mean {mean:P3} (se {standardError:P3}), " +
                    $"spread {spread / nominal:F2} x nominal, saturated {saturated:P2}");

                // The vacuity guard, and a check on the construction: the registers the
                // hash saturates are the fraction the model says, 1 - exp(-n/2^32).
                var predicted = 1 - Math.Exp(-n / Math.Pow(2, 32));
                Assert.AreEqual(predicted, saturated, 0.01,
                    $"{where}: {saturated:P2} of registers saturated, against {predicted:P2}");

                Assert.IsLessThanOrEqualTo(4 * standardError, Math.Abs(mean),
                    $"{where}: the mean relative error was {mean:P3}, more than four " +
                    $"standard errors ({standardError:P3}) from zero.");

                var ratio = spread / nominal;
                Assert.IsTrue(ratio >= 0.75 && ratio <= 1.35,
                    $"{where}: the spread was {ratio:F2} times the nominal {nominal:P3}.");
            }
        }

        // ---- The large range, on crafted states -----------------------------------

        [TestMethod]
        public void TestTheSaturatedContributionIsErtlsTau()
        {
            // Both are series of Math.Pow and Math.Sqrt, and Math.Pow is not bit-identical
            // across platforms, so the two agree to twelve digits rather than to the last.
            foreach (var x in new[] { 0.0, 1.0 / 65536, 1.0 / 16, 0.25, 1 / Math.E, 0.5, 0.9, 1.0 })
            {
                var tau = TestHyperLogLogPlus.ErtlTau(x);
                Assert.AreEqual(tau, SaturatedContribution(x), 1e-13,
                    $"x={x}: the model's expected contribution and Ertl's tau must be one function.");
            }

            Assert.AreEqual(0.1802, SaturatedContribution(1 / Math.E), 5e-5,
                "the value worked by hand at x = 1/e");
        }

        /// <summary>
        /// Past 2^32/30 with nothing saturated, the estimate is the raw estimate. These
        /// are the states the old test pinned to the large-range correction: all
        /// sixteen registers at 27 is a raw 1.445e9, which the correction turned into
        /// 1.76e9, and at 24 it is 1.806e8 just past the 1.43e8 threshold, which the
        /// correction raised by 2.2%. The m = 1024 row is a raw 3.1e9 that the
        /// correction made 5.5e9. Each row is equality, since equal registers make the
        /// harmonic sum exact.
        /// </summary>
        [TestMethod]
        [DataRow(16u, (byte)27)]
        [DataRow(16u, (byte)24)]
        [DataRow(1024u, (byte)22)]
        public void TestTheRawEstimateStandsPastTheOldLargeRangeThreshold(uint m, byte fill)
        {
            var hll = new HyperLogLog(m);
            Array.Fill(hll.RegisterState, fill);
            Assert.IsLessThan(Q(m) + 1, (int)fill, "the row must have nothing saturated");

            var raw = Alpha(m) * m * Math.Pow(2, fill);
            var twoTo32 = Math.Pow(2, 32);
            Assert.IsGreaterThan(twoTo32 / 30, raw,
                "the row must sit where the old large-range correction engaged");
            var corrected = -twoTo32 * Math.Log(1 - (raw / twoTo32));
            Assert.IsGreaterThan(1.02, corrected / raw,
                "the row must be one the old correction moved by more than 2%");

            Assert.AreEqual((ulong)raw, hll.Count(),
                $"m={m}, every register at {fill}: no register is saturated, so there is " +
                "nothing to correct, and the estimate is alpha_m * m * 2^fill.");
        }

        /// <summary>
        /// States where the saturated registers carry a real share of the estimate:
        /// from a fifth to nearly nine tenths of the denominator across the rows.
        /// Counting a saturated register as a plain 2^-(q+1) moves each answer by more
        /// than 10%.
        /// </summary>
        [TestMethod]
        [DataRow(16u, 8, (byte)28)]
        [DataRow(16u, 15, (byte)27)]
        [DataRow(1024u, 700, (byte)21)]
        [DataRow(1024u, 1000, (byte)22)]
        [DataRow(65536u, 40000, (byte)16)]
        public void TestSaturatedRegistersContributeWhatAWiderHashWouldHave(
            uint m, int saturated, byte rest)
        {
            var q = Q(m);
            var hll = new HyperLogLog(m);
            var registers = hll.RegisterState;
            for (var i = 0; i < registers.Length; i++)
            {
                registers[i] = (byte)(i < saturated ? q + 1 : rest);
            }

            var expected = ExpectedEstimate(registers);

            var tauTerm = m * SaturatedContribution((double)(m - saturated) / m) * Math.Pow(2, -q);
            var plain = (m - saturated) * Math.Pow(2, -rest);
            Assert.IsGreaterThan(0.15, tauTerm / (tauTerm + plain),
                "the saturated registers must carry a real share of the denominator, or " +
                "this row pins the harmonic sum again");
            var asPlain = Alpha(m) * m * m / (plain + (saturated * Math.Pow(2, -(q + 1))));
            Assert.IsGreaterThan(0.10, Math.Abs(asPlain - expected) / expected,
                "the row must tell a saturated register from a plain one");

            AssertEstimate(expected, hll.Count(),
                $"m={m}, {saturated} saturated and the rest at {rest}:");
        }

        // ---- A full sketch ----------------------------------------------------------

        /// <summary>
        /// A sketch whose every register is saturated has nothing left to estimate from,
        /// and the estimate would divide by zero; the old pipeline took the logarithm of
        /// a negative number and returned 0. It reports what a sketch one register short
        /// of full reports, which is the largest estimate it can give, and adding more
        /// cannot move it. The row's ceiling is checked against the closed form
        /// alpha_m m^2 2^q / (1 + m tau(1/m)).
        /// </summary>
        [TestMethod]
        [DataRow(1u)]
        [DataRow(2u)]
        [DataRow(16u)]
        [DataRow(1024u)]
        [DataRow(65536u)]
        public void TestAFullSketchReportsItsCeiling(uint m)
        {
            var q = Q(m);
            var full = new HyperLogLog(m);
            for (uint j = 0; j < m; j++)
            {
                // The register index is the top bits, and an all-zero remainder is the
                // longest run there is.
                full.AddHash(m == 1 ? 0u : j << q);
            }
            Assert.IsTrue(full.RegisterState.All(r => r == q + 1), "every register must be saturated");

            var ceiling = Alpha(m) * m * m * Math.Pow(2, q) / (1 + (m * SaturatedContribution(1.0 / m)));
            AssertEstimate(ceiling, full.Count(), $"m={m}: a full sketch must report its ceiling.");

            var oneShort = new HyperLogLog(m);
            Array.Fill(oneShort.RegisterState, (byte)(q + 1));
            oneShort.RegisterState[0] = (byte)q;
            Assert.AreEqual(oneShort.Count(), full.Count(),
                $"m={m}: full must answer as one register short of full does.");

            if (m >= 16)
            {
                // One or two registers say almost nothing, and their ceilings, 1.5e9
                // and 3.1e9, are below it.
                Assert.IsGreaterThan(Math.Pow(2, 32), (double)full.Count(),
                    $"m={m}: the ceiling is past the hash space, not a small number");
            }

            var before = full.Count();
            for (uint i = 0; i < 1000; i++)
            {
                full.AddHash(i * 2654435761u);
            }
            Assert.AreEqual(before, full.Count(), $"m={m}: nothing added to a full sketch moves it");
        }

        // ---- Never zero ---------------------------------------------------------------

        /// <summary>
        /// Every set register was set by a hash, and distinct registers by distinct
        /// hashes, so a sketch with k registers set has certainly seen k distinct
        /// items: Count() is never below that, and so never 0 once anything was added.
        /// <para>
        /// Exhaustive at one, two and four registers, every register taking every value
        /// the hash can give it. Before the fix three things broke it: one register at
        /// 1 estimated 0.69, which truncated to 0; two registers at 1 estimated 1.87,
        /// which truncated to 1; and any state whose raw estimate passed 2^32, every
        /// saturated one among them, took the logarithm of a negative number and
        /// returned 0.
        /// </para>
        /// </summary>
        [TestMethod]
        [DataRow(1u)]
        [DataRow(2u)]
        [DataRow(4u)]
        public void TestNoStateCountsFewerThanItsSetRegisters(uint m)
        {
            var q = Q(m);
            var values = q + 2;
            var hll = new HyperLogLog(m);
            var registers = hll.RegisterState;
            var states = (long)Math.Pow(values, m);
            var checkedNonEmpty = 0L;

            for (long s = 0; s < states; s++)
            {
                var rest = s;
                var occupied = 0u;
                for (var i = 0; i < m; i++)
                {
                    registers[i] = (byte)(rest % values);
                    rest /= values;
                    occupied += registers[i] != 0 ? 1u : 0u;
                }

                var count = hll.Count();
                if (occupied == 0)
                {
                    Assert.AreEqual(0ul, count, $"m={m}: an empty sketch counts zero");
                    continue;
                }

                checkedNonEmpty++;
                if (count < occupied)
                {
                    Assert.Fail($"m={m}, registers [{string.Join(",", registers)}]: counted " +
                        $"{count}, but {occupied} registers are set, so at least {occupied} " +
                        "distinct items were added.");
                }
            }

            Assert.AreEqual(states - 1, checkedNonEmpty, "every state but the empty one was checked");
        }

        /// <summary>
        /// The same floor at every register count up to 2^18, on the states that sit at
        /// its edges: one register set to each value it can hold, every register at each
        /// value, and every register saturated. The old pipeline failed this at every
        /// size, on the saturated state.
        /// </summary>
        [TestMethod]
        public void TestNoRegisterCountCountsZeroWhenSomethingWasAdded()
        {
            for (var b = 0; b <= 18; b++)
            {
                var m = 1u << b;
                var q = Q(m);
                var hll = new HyperLogLog(m);
                var registers = hll.RegisterState;

                for (var v = 1; v <= q + 1; v++)
                {
                    Array.Clear(registers);
                    registers[registers.Length / 2] = (byte)v;
                    Assert.IsGreaterThanOrEqualTo(1ul, hll.Count(),
                        $"m=2^{b}, one register at {v}: one item was certainly added");

                    Array.Fill(registers, (byte)v);
                    Assert.IsGreaterThanOrEqualTo((ulong)m, hll.Count(),
                        $"m=2^{b}, every register at {v}: {m} items were certainly added");
                }
            }
        }

        // ---- Stored sketches ----------------------------------------------------------

        /// <summary>
        /// The fix is to the estimator, not to what is stored: the registers are the
        /// same registers, so the payload is the same version 1 payload, and a sketch
        /// written before the fix reads back holding exactly what it held and simply
        /// estimates by the corrected rule. A sketch deep in the saturated range, and a
        /// full one, round-trip byte for byte and count the same on both sides, and the
        /// old crafted large-range state reads back to its raw estimate.
        /// </summary>
        [TestMethod]
        public void TestSketchesPastTheOldThresholdStillWriteVersionOne()
        {
            var random = new Random(733);
            var full = new HyperLogLog(1024);
            for (uint j = 0; j < 1024; j++)
            {
                full.AddHash(j << Q(1024));
            }

            var oldBand = new HyperLogLog(16);
            Array.Fill(oldBand.RegisterState, (byte)27);

            foreach (var (name, sketch) in new[]
            {
                ("2^33 items", PoissonSketch(1024, Math.Pow(2, 33), random)),
                ("full", full),
                ("old large-range state", oldBand),
            })
            {
                var bytes = sketch.ToByteArray();
                Assert.AreEqual((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)),
                    $"{name}: the format version");
                Assert.AreEqual(14 + 4 + 4 + (int)sketch.M + 4, bytes.Length,
                    $"{name}: the layout is m, then the registers, and nothing else");

                var read = Persistence.FromByteArray<HyperLogLog>(bytes);
                CollectionAssert.AreEqual(sketch.RegisterState, read.RegisterState, $"{name}: registers");
                Assert.AreEqual(sketch.Count(), read.Count(), $"{name}: the estimate survives the round trip");
                CollectionAssert.AreEqual(bytes, read.ToByteArray(), $"{name}: and writes back unchanged");
                Assert.AreNotEqual(0ul, read.Count(), $"{name}: and is not zero");
            }

            Assert.AreEqual((ulong)(0.673 * 16 * Math.Pow(2, 27)), oldBand.Count(),
                "the old large-range state estimates by the raw estimator once read");
        }

        /// <summary>
        /// With one register, b is 0 and the shift that takes the register index from
        /// the top of the hash is a shift by 32, which C# masks to a shift by nothing:
        /// the index was the whole hash, and Add threw IndexOutOfRangeException for any
        /// item whose low 32 bits were not all zero.
        /// </summary>
        [TestMethod]
        public void TestASingleRegisterSketchTakesItems()
        {
            var hll = new HyperLogLog(1);
            for (var i = 0; i < 100; i++)
            {
                hll.Add(Encoding.UTF8.GetBytes($"one-{i}"));
            }

            Assert.AreNotEqual((byte)0, hll.RegisterState[0], "the only register must be set");
            Assert.IsGreaterThanOrEqualTo(1ul, hll.Count(), "a sketch that took items counts at least one");
        }
    }
}
