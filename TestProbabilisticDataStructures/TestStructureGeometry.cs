using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Every structure here derives its size from the accuracy it was asked for, and
    /// that derivation is the one part no behavioral test can check.
    /// <para>
    /// The bounds these structures promise are proved through Markov's inequality and
    /// are correspondingly loose, so a structure built two or three times smaller than
    /// the formula says still lands inside its stated error most of the time. It is
    /// quietly worse -- more collisions, more overcounting, a wider spread -- while
    /// every observable behavior stays intact. Measuring the outcome cannot see that.
    /// Only comparing the size against the closed form can.
    /// </para>
    /// <para>
    /// So these tests restate each formula from its paper rather than reading it back
    /// from the implementation. Restating it is the entire point: a test that asked
    /// the code what size it chose would agree with any answer.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestStructureGeometry
    {
        /// <summary>
        /// m = -n*ln(p) / (ln 2)^2, the size that minimizes the false-positive rate for
        /// a filter holding n items at rate p.
        /// <para>
        /// The implementation writes this through the fill ratio as
        /// n / ((ln f * ln(1-f)) / |ln p|), which is the same expression only while
        /// f = 0.5: ln(0.5) * ln(0.5) is (ln 2)^2. That equivalence is invisible at the
        /// call site and would not survive someone making the fill ratio configurable.
        /// </para>
        /// </summary>
        [TestMethod]
        [DataRow(1000u, 0.01)]
        [DataRow(1000u, 0.001)]
        [DataRow(100000u, 0.01)]
        [DataRow(100000u, 0.0001)]
        [DataRow(10u, 0.5)]
        public void TestOptimalMIsTheTextbookBloomSize(uint n, double fpRate)
        {
            var lnTwoSquared = Math.Log(2) * Math.Log(2);
            var expected = (uint)Math.Ceiling(n * Math.Abs(Math.Log(fpRate)) / lnTwoSquared);

            Assert.AreEqual(expected, Utils.OptimalM(n, fpRate),
                $"n={n} p={fpRate}: m must be -n*ln(p)/(ln 2)^2. A filter sized below " +
                "this exceeds the false-positive rate it was asked for, and one sized " +
                "above it wastes memory to no benefit.");
        }

        /// <summary>
        /// The 64-bit variant must agree with the 32-bit one wherever both can answer,
        /// or the same request produces filters of different accuracy depending only on
        /// which type the caller reached for.
        /// </summary>
        [TestMethod]
        [DataRow(1000u, 0.01)]
        [DataRow(100000u, 0.001)]
        public void TestOptimalM64AgreesWithOptimalM(uint n, double fpRate)
        {
            Assert.AreEqual((ulong)Utils.OptimalM(n, fpRate), Utils.OptimalM64(n, fpRate),
                $"n={n} p={fpRate}: the 32- and 64-bit sizing must not diverge in the " +
                "range they share.");
        }

        /// <summary>
        /// k = log2(1/p). This follows from m above: the optimal k is (m/n) ln 2, and
        /// substituting m leaves |ln p| / ln 2.
        /// </summary>
        [TestMethod]
        [DataRow(0.01)]
        [DataRow(0.001)]
        [DataRow(0.0001)]
        [DataRow(0.5)]
        public void TestOptimalKIsLogBaseTwoOfOneOverP(double fpRate)
        {
            var expected = (uint)Math.Ceiling(Math.Abs(Math.Log(fpRate)) / Math.Log(2));

            Assert.AreEqual(expected, Utils.OptimalK(fpRate),
                $"p={fpRate}: k must be log2(1/p), which is the k that minimizes the " +
                "false-positive rate at the m OptimalM returns.");
        }

        /// <summary>
        /// Count Sketch bounds error against the L2 norm rather than the L1 norm, which
        /// is why its width is 1/epsilon^2 and not e/epsilon. The two are easy to
        /// confuse, and confusing them hands one sketch several times the other's
        /// memory for the same epsilon.
        /// </summary>
        [TestMethod]
        [DataRow(0.01, 0.01)]
        [DataRow(0.001, 0.01)]
        [DataRow(0.01, 0.001)]
        public void TestCountSketchGeometryMatchesThePaper(double epsilon, double delta)
        {
            var cs = new CountSketch(epsilon, delta);

            Assert.AreEqual((uint)Math.Ceiling(1.0 / (epsilon * epsilon)), cs.Width(),
                $"eps={epsilon}: Count Sketch width must be 1/eps^2. Its error is " +
                "bounded against the L2 norm, so the width is quadratic in epsilon " +
                "where a Count-Min Sketch's is linear.");

            Assert.AreEqual((uint)Math.Ceiling(Math.Log(1 / delta)), cs.Depth(),
                $"delta={delta}: depth must be ln(1/delta), one row per independent " +
                "chance to miss the bound.");
        }

        /// <summary>
        /// A HyperLogLog's relative standard error is 1.04/sqrt(m), so a request for
        /// error e needs m = (1.04/e)^2 registers, rounded up to a power of two because
        /// the register index is taken from the top bits of the hash.
        /// </summary>
        /// <para>
        /// The error values matter. Rounding up to a power of two absorbs the 1.04
        /// entirely at most of them -- at e=0.01, (1.04/e)^2 = 10816 and (1/e)^2 =
        /// 10000 both round to 16384, so the constant could be dropped without moving
        /// the answer. e=0.032 and e=0.016 straddle a power-of-two boundary, where it
        /// is the difference between 2048 registers and 1024.
        /// </para>
        [TestMethod]
        [DataRow(0.1)]
        [DataRow(0.05)]
        [DataRow(0.032)]
        [DataRow(0.016)]
        [DataRow(0.01)]
        [DataRow(0.005)]
        public void TestHyperLogLogRegisterCountMatchesTheRequestedError(double e)
        {
            var hll = HyperLogLog.NewDefaultHyperLogLog(e);

            var needed = Math.Pow(1.04 / e, 2);
            var expected = (uint)Math.Pow(2, Math.Ceiling(Math.Log(needed, 2)));

            Assert.AreEqual(expected, hll.M,
                $"e={e}: m must be the next power of two at or above (1.04/e)^2. " +
                "Fewer registers widen the spread of the estimate without changing " +
                "anything a single count would reveal.");

            Assert.AreEqual(0u, expected & (expected - 1),
                $"e={e}: m must be a power of two -- the register index is the top " +
                "bits of the hash, so a non-power-of-two would not address evenly.");

            var achieved = 1.04 / Math.Sqrt(hll.M);
            Assert.IsLessThanOrEqualTo(e, achieved,
                $"e={e}: m={hll.M} gives a relative standard error of {achieved:F5}, " +
                "which is worse than the error that was asked for.");
        }

        /// <summary>
        /// A cuckoo filter's fingerprint must be at least log2(2b/epsilon) bits, where
        /// b is the bucket size: a lookup compares against 2b stored fingerprints, so
        /// the chance one of them matches by accident is 2b/2^f.
        /// <para>
        /// This implementation rounds that up to whole bytes, which is a storage
        /// decision rather than an accuracy one, and it overshoots substantially. At
        /// epsilon=0.01 the formula asks for 10 bits and the filter stores 16, so the
        /// rate it actually delivers is around 80 times better than requested and the
        /// fingerprint array is 60% larger than the math requires. That is a defensible
        /// trade, but it should be a visible one -- the test prints the overshoot, and
        /// pins the direction so byte rounding can never land the filter on the wrong
        /// side of the rate it promised.
        /// </para>
        /// </summary>
        /// <para>
        /// Both rounding steps swallow errors at most inputs, so the rows are chosen to
        /// straddle them. Byte rounding hides a missing factor of 2 in the fingerprint
        /// everywhere except p=0.0001, where it is 3 bytes against 2. Rounding the
        /// bucket count to a power of two hides the 0.95 load factor everywhere except
        /// n=16000, where it is 8192 buckets against 4096 -- at 10000 and 100000 both
        /// the headroom and no headroom give the same answer.
        /// </para>
        [TestMethod]
        [DataRow(10000u, 0.01)]
        [DataRow(10000u, 0.001)]
        [DataRow(10000u, 0.0001)]
        [DataRow(16000u, 0.01)]
        [DataRow(100000u, 0.01)]
        public void TestCuckooFingerprintAndBucketCountMatchTheFormulas(uint n, double fpRate)
        {
            var f = new CuckooBloomFilter(n, fpRate);
            var b = f.B;

            var neededBits = Math.Ceiling(Math.Log2(2.0 * b / fpRate));
            var expectedF = (uint)Math.Clamp(Math.Ceiling(neededBits / 8), 1, 8);

            Assert.AreEqual(expectedF, f.F,
                $"n={n} p={fpRate}: the fingerprint must cover log2(2b/eps) = " +
                $"{neededBits} bits, which is {expectedF} byte(s).");

            // Buckets: enough for n items at b per bucket, with headroom, rounded up to
            // a power of two because the index arithmetic requires it.
            var needed = Math.Ceiling(n / (b * 0.95));
            var expectedM = (uint)Math.Pow(2, Math.Ceiling(Math.Log2(needed)));

            Assert.AreEqual(expectedM, f.M,
                $"n={n} p={fpRate}: bucket count must be the next power of two at or " +
                $"above n/(b*loadFactor) = {needed}. Sizing this from the fingerprint " +
                "width instead of the item count is what once made a 100,000-item " +
                "filter allocate 122 MB.");

            var delivered = 2.0 * b / Math.Pow(2, 8 * f.F);
            Console.WriteLine($"n={n} p={fpRate}: f={f.F} byte(s), m={f.M}, " +
                $"delivered rate={delivered:E2}, overshoot={fpRate / delivered:F1}x");

            Assert.IsLessThanOrEqualTo(fpRate, delivered,
                $"n={n} p={fpRate}: the filter's nominal rate 2b/2^f is {delivered:E2}, " +
                "which is worse than the rate it was asked for. Rounding the " +
                "fingerprint to whole bytes may only ever help.");
        }
    }
}
