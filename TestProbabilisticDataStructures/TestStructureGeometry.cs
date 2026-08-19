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
        /// A cuckoo filter's fingerprint must be log2(2b/epsilon) bits, where b is the
        /// bucket size: a lookup compares against 2b stored fingerprints, so the chance
        /// one matches by accident is 2b/2^bits. Through 6.0.1 this was rounded up to
        /// whole bytes, which delivered a rate far better than asked for and charged
        /// memory for it -- 82 times better at 60% more fingerprint memory for
        /// epsilon=0.01. The filter now stores exactly the bits the formula needs, so
        /// the delivered rate should sit just under the requested one rather than far
        /// beneath it.
        /// <para>
        /// Both rounding steps in this filter's sizing swallow errors at most inputs,
        /// so the rows straddle them deliberately. Dropping the 0.95 load factor is
        /// invisible at n=10000 and n=100000, where headroom and no headroom both round
        /// to the same power of two; n=16000 is 8192 buckets against 4096.
        /// </para>
        /// </summary>
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

            var expectedBits = (uint)Math.Clamp(
                Math.Ceiling(Math.Log2(2.0 * b / fpRate)), 1, 64);

            Assert.AreEqual(expectedBits, f.FingerprintBits,
                $"n={n} p={fpRate}: the fingerprint must be exactly " +
                $"ceil(log2(2b/eps)) = {expectedBits} bits.");

            var needed = Math.Ceiling(n / (b * 0.95));
            var expectedM = (uint)Math.Pow(2, Math.Ceiling(Math.Log2(needed)));

            Assert.AreEqual(expectedM, f.M,
                $"n={n} p={fpRate}: bucket count must be the next power of two at or " +
                $"above n/(b*loadFactor) = {needed}. Sizing this from the fingerprint " +
                "width instead of the item count is what once made a 100,000-item " +
                "filter allocate 122 MB.");

            var delivered = 2.0 * b / Math.Pow(2, f.FingerprintBits);
            Console.WriteLine($"n={n} p={fpRate}: {f.FingerprintBits} bits, m={f.M}, " +
                $"delivered rate={delivered:E2}, overshoot={fpRate / delivered:F1}x");

            Assert.IsLessThanOrEqualTo(fpRate, delivered,
                $"n={n} p={fpRate}: the filter's nominal rate 2b/2^bits is " +
                $"{delivered:E2}, which is worse than the rate it was asked for.");

            // And no longer wasteful about it: ceil of a log2 can overshoot by at most
            // one bit, which is a factor of two, so anything beyond that would mean the
            // width is not being taken from the formula at all.
            Assert.IsLessThanOrEqualTo(2.0, fpRate / delivered,
                $"n={n} p={fpRate}: delivering {delivered:E2} is more than twice as " +
                "good as requested, which is memory spent on accuracy nobody asked " +
                "for -- rounding a bit width up can never cost more than one bit.");
        }


        /// <summary>
        /// The memory the packing was for. A byte-aligned filter stored the next whole
        /// byte above the width the formula asks for; at epsilon=0.01 that is 16 bits
        /// where 10 will do, so the fingerprint array was 60% larger than the math
        /// requires. Packed, the array is the bits themselves.
        /// </summary>
        [TestMethod]
        [DataRow(10000u, 0.01, 10u)]
        [DataRow(10000u, 0.001, 13u)]
        [DataRow(100000u, 0.01, 10u)]
        public void TestCuckooFingerprintStorageIsTheBitsAndNotTheBytes(
            uint n, double fpRate, uint expectedBits)
        {
            var f = new CuckooBloomFilter(n, fpRate);
            var entries = (ulong)f.M * f.B;

            var needed = (entries * expectedBits + 7) / 8;
            var byteAligned = entries * ((expectedBits + 7) / 8);

            Console.WriteLine($"n={n} p={fpRate}: {entries} entries x {expectedBits} bits " +
                $"= {needed} bytes; byte-aligned would be {byteAligned}; " +
                $"actual {f.FingerprintBytes()}");

            // One spare word, so that an entry ending at a word boundary can be read
            // by the two-word path without a bounds check on every access.
            Assert.IsLessThanOrEqualTo(needed + 8, f.FingerprintBytes(),
                $"n={n} p={fpRate}: {entries} entries of {expectedBits} bits need " +
                $"{needed} bytes and the filter is using {f.FingerprintBytes()}.");

            Assert.IsGreaterThan(f.FingerprintBytes(), byteAligned,
                $"n={n} p={fpRate}: packing must actually save something against the " +
                $"byte-aligned {byteAligned} bytes, or there is nothing to justify it.");
        }

        /// <summary>
        /// Count-Min width is ceil(e/epsilon) and depth is ceil(ln(1/delta)) -- Theorem
        /// 1 of Cormode and Muthukrishnan, "An Improved Data Stream Summary: The
        /// Count-Min Sketch and its Applications" (2005). The e is Euler's constant,
        /// not a typo for 2: the error bound is proved through Markov's inequality
        /// with e as the base that makes ln(1/delta) rows suffice. A sketch built with
        /// 2/epsilon columns is 26% narrower, overcounts proportionally more, and
        /// passes every behavioral bound in this suite regardless, because the bound
        /// tests carry the slack the loose inequality forces on them. Only this
        /// restatement notices.
        /// <para>
        /// The last two rows straddle the ceilings: e/0.0271 = 100.3 rounds up to 101
        /// where e/0.0272 = 99.94 rounds to 100, and ln(1/0.05) = 2.996 stays at
        /// depth 3 where ln(1/0.049) = 3.016 forces 4. An implementation that floors,
        /// rounds, or drops the ceiling entirely agrees with ceil on almost every
        /// input; these are chosen from the inputs on which it cannot.
        /// </para>
        /// </summary>
        [TestMethod]
        [DataRow(0.01, 0.01, 272u, 5u)]
        [DataRow(0.001, 0.02, 2719u, 4u)]
        [DataRow(0.1, 0.001, 28u, 7u)]
        [DataRow(0.0271, 0.05, 101u, 3u)]
        [DataRow(0.0272, 0.049, 100u, 4u)]
        public void TestCountMinGeometryMatchesThePaper(
            double epsilon, double delta, uint expectedWidth, uint expectedDepth)
        {
            var cms = new CountMinSketch(epsilon, delta);

            Assert.AreEqual((uint)Math.Ceiling(Math.E / epsilon), cms.Width,
                $"e={epsilon}: width must be ceil(e/epsilon), with e Euler's constant.");
            Assert.AreEqual(expectedWidth, cms.Width,
                $"e={epsilon}: the formula evaluated by hand gives {expectedWidth}.");

            Assert.AreEqual((uint)Math.Ceiling(Math.Log(1 / delta)), cms.Depth,
                $"d={delta}: depth must be ceil(ln(1/delta)), the natural logarithm.");
            Assert.AreEqual(expectedDepth, cms.Depth,
                $"d={delta}: the formula evaluated by hand gives {expectedDepth}.");
        }

        /// <summary>
        /// The fuse filter's segment geometry is the reference implementation's
        /// arithmetic -- binary_fuse_calculate_segment_length and _size_factor in
        /// FastFilter's binaryfusefilter.h, verified against the source 2026-08-18:
        /// for arity 3, a segment length of 2^floor(ln(n)/ln(3.33) + 2.25) capped at
        /// 2^18, and a size factor of max(1.125, 0.875 + 0.25 ln(10^6)/ln(n)). The
        /// constants are empirical fits from the paper's authors; nothing rederives
        /// them, so nothing but this restatement would notice 3.33 becoming 3.
        /// <para>
        /// n = 3565 is chosen because ln(3565)/ln(3.33) has fractional part 0.80,
        /// inside the [0.75, 1) band where floor(x + 2.25) and floor(x + 2.0)
        /// disagree; every rounder n in this list is blind to that edit.
        /// </para>
        /// <para>
        /// n = 1 and n = 4 pin the small-set path, where the segment subtraction
        /// underflows by design and the clamp turns the wreckage into one segment.
        /// The restatement reproduces the underflow rather than special-casing it,
        /// because the reference behaves this way and the implementation documents
        /// itself as following the reference.
        /// </para>
        /// </summary>
        [TestMethod]
        [DataRow(1u)]
        [DataRow(4u)]
        [DataRow(1000u)]
        [DataRow(3565u)]
        [DataRow(65536u)]
        [DataRow(1000000u)]
        public void TestBinaryFuseGeometryMatchesTheReferenceImplementation(uint n)
        {
            var keys = new System.Collections.Generic.List<byte[]>();
            for (uint i = 0; i < n; i++)
            {
                keys.Add(System.Text.Encoding.ASCII.GetBytes($"geometry-{i}"));
            }
            var f = BinaryFuseFilter.Build(keys);

            var segmentLength = n == 0
                ? 4u
                : Math.Min(262144u, 1u << (int)Math.Floor(Math.Log(n) / Math.Log(3.33) + 2.25));
            var factor = n <= 1
                ? 0
                : Math.Max(1.125, 0.875 + (0.25 * Math.Log(1000000.0) / Math.Log(n)));
            var capacity = n <= 1 ? 0 : (uint)Math.Round(n * factor);

            uint arrayLength;
            unchecked
            {
                var initialSegments = ((capacity + segmentLength - 1) / segmentLength) - 2;
                arrayLength = (initialSegments + 2) * segmentLength;
            }
            var segmentCount = (arrayLength + segmentLength - 1) / segmentLength;
            segmentCount = segmentCount <= 2 ? 1u : segmentCount - 2;
            arrayLength = (segmentCount + 2) * segmentLength;

            Assert.AreEqual(segmentLength, f.SegmentLengthChosen,
                $"n={n}: segment length must be 2^floor(ln(n)/ln(3.33) + 2.25).");
            Assert.AreEqual(segmentCount, f.SegmentCountChosen,
                $"n={n}: segment count must follow the reference derivation.");
            Assert.AreEqual(arrayLength, f.ArrayLengthChosen,
                $"n={n}: array length must be (segments + 2) segment lengths.");
        }

        /// <summary>
        /// Each filter a scalable Bloom filter adds is built to the rate p0*r^i --
        /// section 3 of Almeida, Baquero, Preguica and Hutchison, "Scalable Bloom
        /// Filters" (2007), where the compounded rate telescopes to at most
        /// p0/(1-r). The series is only observable through the geometry the rate
        /// buys, so the assertion goes through OptimalK and OptimalM, which the
        /// tests above hold to their own papers. With p0 = 0.05 and r = 0.8, k
        /// crosses a ceiling boundary between filters 2 and 3 (log2(1/rate) passes
        /// 5), so a mutation that stops tightening -- r^i frozen at r, or applied to
        /// the wrong exponent -- shifts the transition and fails.
        /// <para>
        /// The paper also grows each successive filter's capacity geometrically.
        /// This implementation adds fixed-size filters instead and only tightens the
        /// rates; the union bound the series exists for cares about the rates alone,
        /// so that is what this pins.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestScalableFilterRatesFollowTheTighteningSeries()
        {
            var s = new ScalableBloomFilter(1000, 0.05, 0.8);
            for (int i = 0; i < 5000; i++)
            {
                s.Add(System.Text.Encoding.ASCII.GetBytes($"series-{i}"));
            }

            Assert.IsGreaterThanOrEqualTo(4, s.Filters.Count,
                "5,000 additions against a 1,000-item hint must force at least four " +
                "filters, or the series below is asserted on air.");

            double compounded = 0;
            for (int i = 0; i < s.Filters.Count; i++)
            {
                var rate = 0.05 * Math.Pow(0.8, i);
                compounded += rate;

                Assert.AreEqual(Utils.OptimalK(rate), s.Filters[i].PartitionCount,
                    $"filter {i} must be built with k for the tightened rate 0.05*0.8^{i}.");
                Assert.AreEqual(Utils.OptimalM(1000, rate), s.Filters[i].BitCount,
                    $"filter {i} must be sized for the tightened rate 0.05*0.8^{i}.");
            }

            Assert.IsLessThan(0.05 / (1 - 0.8), compounded,
                "the compounded rate must stay below p0/(1-r), the geometric series " +
                "bound the tightening exists to enforce.");
        }

        /// <summary>
        /// An invertible Bloom lookup table can only list its contents while it has
        /// more than c4 = 1.295 cells per entry -- the 2-core threshold for four
        /// hashes, Table 1 of Goodrich and Mitzenmacher, "Invertible Bloom Lookup
        /// Tables" (2011). The 1.5 the sizing uses is that threshold plus margin.
        /// Nothing else fails fast if the margin is edited away: listing degrades
        /// probabilistically, at scale, in someone else's diff.
        /// </summary>
        [TestMethod]
        public void TestIbltProvisioningClearsThePeelingThreshold()
        {
            Assert.IsGreaterThanOrEqualTo(1.295, InvertibleBloomLookupTable.CellsPerDifference,
                "cells per difference must clear the four-hash 2-core threshold " +
                "c4 = 1.295, below which listEntries stops succeeding with high " +
                "probability.");

            foreach (var d in new uint[] { 3, 50, 1000 })
            {
                var t = new InvertibleBloomLookupTable(d, 8);
                Assert.AreEqual((uint)Math.Ceiling(d * 1.5), t.CellCount,
                    $"d={d}: the table must provision ceil(1.5d) cells.");
            }

            Assert.AreEqual(4u, new InvertibleBloomLookupTable(2, 8).CellCount,
                "two differences ask for three cells, but four hashes need four " +
                "distinct cells to land in, so the hash count is the floor.");
        }
    }
}
