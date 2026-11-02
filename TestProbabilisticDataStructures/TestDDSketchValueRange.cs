using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// What a bucket reports, at the two ends of the doubles where the arithmetic that
    /// produces it runs out of range.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bucket reports its midpoint, 2 * gamma^i / (gamma + 1), and written that way
    /// the working leaves the range of a double before the answer does. Twice gamma^i
    /// is infinite for every bucket from 2^1023 upward, where the answer, (1 - a) *
    /// gamma^i, is at most the largest value the bucket holds and so is finite: a
    /// quantile over the largest doubles came back as infinity, at every accuracy. And
    /// at the other end, at an accuracy of 0.99 the answer for the smallest doubles was
    /// exactly zero, which is not within 99% of anything positive.
    /// </para>
    /// <para>
    /// The promise on the value is not weakened for being at an extreme, and the
    /// extremes are where a caller who sums or scales what comes back finds an infinity.
    /// So what is held here is the same promise: finite, the sign of the values it
    /// stands for, and within the accuracy. And, because correcting the ends must not
    /// move the middle, that wherever the direct formula gave a finite number other
    /// than zero the answer is that number, bit for bit, and a zero is the smallest
    /// double.
    /// </para>
    /// <para>
    /// The subnormals are the one place the promise is held only to a unit. Their
    /// answer is gamma^i rounded to a multiple of the smallest double, and then rounded
    /// again by the multiplication, which leaves it up to a unit from the midpoint,
    /// and for the first few thousand values a unit is a visible share of the value. It
    /// is measured here rather than corrected: the same sum answers right for most of
    /// those buckets, so there is no telling the wrong ones apart without the bucket's
    /// own smallest and largest doubles, and a bucket that answered within the accuracy
    /// must go on answering as it did.
    /// </para>
    /// <para>
    /// Only a sketch of a single value is built for each case, so the answer is the
    /// bucket's alone and an extreme does not have to share an array with its opposite:
    /// a store of both would be billions of buckets.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestDDSketchValueRange
    {
        /// <summary>
        /// The coarsest legal accuracy, where a subnormal bucket's answer rounds to zero;
        /// 0.5, where gamma is 3; the ones a caller reaches for; and the finest at which
        /// every double has a bucket, which is a little above 1.74e-7.
        /// </summary>
        private static readonly double[] Accuracies =
            { 0.99, 0.5, 0.1, 0.01, 1e-3, 1e-5, 1.8e-7 };

        /// <summary>
        /// The slack on the accuracy, as a fraction of the value. The midpoint sits
        /// exactly at the accuracy from the nearest value its bucket can hold, so the
        /// few roundings that go into computing it could show up as a few units in the
        /// last place of the value. Measured over this roster the answers never exceed
        /// the accuracy at all; 1e-15 is about five units in the last place, left for a
        /// libm whose power function rounds differently.
        /// </summary>
        private const double RoundingSlack = 1e-15;

        /// <summary>
        /// How far outside the accuracy a subnormal answer may be, in units of the
        /// smallest double. Two roundings to a whole unit, each at most half. Measured
        /// over this roster, 0.75 at its worst, at an accuracy of 1e-4 for a value of
        /// 2,547 units.
        /// </summary>
        private const double SubnormalSlack = 1.0;

        /// <summary>
        /// The reciprocal of the smallest positive double, as a power of two, so that a
        /// subnormal can be measured in units of itself without rounding: scaling by a
        /// power of two is exact, and the subnormals are the integers in those units.
        /// </summary>
        private const int UnitsPerEpsilon = 1074;

        /// <summary>
        /// Below this a value is measured in units of the smallest double. Far above the
        /// subnormals, so that the unit slack is a negligible share of the value where
        /// it applies to values that are not subnormal, and far enough below the top
        /// that scaling by 2^1074 does not overflow.
        /// </summary>
        private const double SmallValue = 1e-200;

        /// <summary>
        /// The bucket's answer worked out directly from the paper's definition, the way
        /// it was before: gamma^i, doubled, over gamma + 1.
        /// </summary>
        private static double Direct(double accuracy, double value)
        {
            var gamma = (1 + accuracy) / (1 - accuracy);
            var index = Math.Ceiling(Math.Log(value) / Math.Log(gamma));
            return 2 * Math.Pow(gamma, index) / (gamma + 1);
        }

        /// <summary>
        /// Positive values down from the largest double through the two binades under
        /// it, in steps small enough that every bucket at the coarsest accuracy that
        /// reaches the top is hit, including 2^1023 and the double under the largest.
        /// </summary>
        private static IEnumerable<double> TopValues()
        {
            yield return double.MaxValue;
            yield return double.BitDecrement(double.MaxValue);
            yield return Math.Pow(2, 1023);
            yield return double.BitDecrement(Math.Pow(2, 1023));

            for (var j = 0; j <= 256; j++)
            {
                yield return double.MaxValue * Math.Pow(2, -2.0 * j / 256);
            }
        }

        /// <summary>
        /// Positive values from the smallest double up through the subnormals into the
        /// first normal binades. Every multiple of the smallest double up to 1,500, since
        /// the first thousand or so are the ones where one unit of rounding is a visible
        /// share of the value, and then a geometric climb that crosses into the normals.
        /// </summary>
        private static IEnumerable<double> BottomValues()
        {
            for (var k = 1; k <= 1500; k++)
            {
                yield return k * double.Epsilon;
            }

            for (var x = 1500.0; x < Math.Pow(2, 56); x *= 1.05)
            {
                yield return Math.Round(x) * double.Epsilon;
            }

            yield return BitConverter.Int64BitsToDouble(0x000FFFFFFFFFFFFF);
            yield return BitConverter.Int64BitsToDouble(0x0010000000000000);
        }

        /// <summary>
        /// Positive values across the doubles in between, where nothing was wrong and
        /// nothing may change.
        /// </summary>
        private static IEnumerable<double> MiddleValues()
        {
            for (var x = 1e-300; x < 1e300; x *= 1.37)
            {
                yield return x;
            }
        }

        private static IEnumerable<double> Everything()
        {
            return TopValues().Concat(BottomValues()).Concat(MiddleValues());
        }

        private static double Answer(double accuracy, double value)
        {
            var sketch = new DDSketch(accuracy);
            sketch.Add(value);
            return sketch.Quantile(0.5);
        }

        /// <summary>
        /// Whether the answer is within the accuracy of the value. For a value small
        /// enough to be near the subnormals this is measured in units of the smallest
        /// double, so that nothing is rounded in the comparison, with the slack above
        /// for the answer being a double at all.
        /// </summary>
        private static bool Within(double accuracy, double value, double answer)
        {
            var small = Math.Abs(value) < SmallValue;
            var scale = small ? UnitsPerEpsilon : 0;
            var valueUnits = Math.Abs(Math.ScaleB(value, scale));
            var answerUnits = Math.Abs(Math.ScaleB(answer, scale));
            var error = Math.Abs(answerUnits - valueUnits);
            return error <= (accuracy + RoundingSlack) * valueUnits + (small ? SubnormalSlack : 0);
        }

        [TestMethod]
        public void TestEveryAnswerIsFiniteNonzeroAndWithinTheAccuracy()
        {
            var failures = new List<string>();
            var cases = 0;

            foreach (var accuracy in Accuracies)
            {
                foreach (var magnitude in Everything())
                {
                    foreach (var value in new[] { magnitude, -magnitude })
                    {
                        cases++;
                        var answer = Answer(accuracy, value);
                        if (!double.IsFinite(answer)
                            || answer == 0
                            || Math.Sign(answer) != Math.Sign(value)
                            || !Within(accuracy, value, answer))
                        {
                            failures.Add($"a={accuracy} v={value:R} answered {answer:R}");
                        }
                    }
                }
            }

            Assert.AreEqual(0, failures.Count,
                $"{failures.Count} of {cases} answers were infinite, zero, of the wrong sign " +
                $"or outside the accuracy, the first few: {string.Join("; ", failures.Take(6))}");
        }

        /// <summary>
        /// The corrections are for an answer that was infinite or zero. Wherever the
        /// direct formula gave anything else it was what a stored sketch has always
        /// answered, and it still does, to the bit: the normal numbers, and the
        /// subnormals the corrected formula would have moved by a unit. A zero is the
        /// smallest double, which the accuracy alone would not pin: at 0.99 the next one
        /// up is also within it of the smallest values, so it is held here.
        /// </summary>
        [TestMethod]
        public void TestOnlyAnInfiniteOrZeroDirectAnswerIsReplaced()
        {
            var compared = 0;
            var subnormal = 0;
            var different = new List<string>();
            var total = 0;

            foreach (var accuracy in Accuracies)
            {
                foreach (var magnitude in Everything())
                {
                    foreach (var value in new[] { magnitude, -magnitude })
                    {
                        total++;
                        var direct = Direct(accuracy, Math.Abs(value));
                        if (double.IsInfinity(direct))
                        {
                            continue;
                        }

                        compared++;
                        if (double.IsSubnormal(direct))
                        {
                            subnormal++;
                        }

                        var expected = Math.CopySign(direct == 0 ? double.Epsilon : direct, value);
                        var answer = Answer(accuracy, value);
                        if (BitConverter.DoubleToInt64Bits(answer)
                            != BitConverter.DoubleToInt64Bits(expected))
                        {
                            different.Add($"a={accuracy} v={value:R}: {answer:R} not {expected:R}");
                        }
                    }
                }
            }

            Assert.AreEqual(0, different.Count,
                $"{different.Count} of {compared} answers changed, the first few: " +
                string.Join("; ", different.Take(6)));

            // The comparison engaged on most of the roster, and on a good many subnormals,
            // so an empty or all-normal one cannot pass.
            Assert.IsTrue(compared > total * 0.5,
                $"only {compared} of {total} cases had a direct answer that was not infinite to compare");
            Assert.IsTrue(subnormal >= 1000,
                $"only {subnormal} of the direct answers compared were subnormal");
        }

        /// <summary>
        /// The guard on the two tests above: the roster really does reach each way the
        /// direct formula failed, at the accuracies where it did. Without this a roster
        /// that stopped short of the extremes would pass them both.
        /// </summary>
        [TestMethod]
        public void TestTheRosterReachesWhereTheDirectFormulaFailed()
        {
            // Infinite at the top, whatever the accuracy.
            foreach (var accuracy in Accuracies)
            {
                var overflowed = TopValues().Count(v => double.IsInfinity(Direct(accuracy, v)));
                Assert.IsTrue(overflowed >= 80,
                    $"only {overflowed} of the top values overflowed at an accuracy of {accuracy}");
            }

            // Zero at the bottom of the coarsest accuracy.
            var zeroed = BottomValues().Count(v => Direct(0.99, v) == 0);
            Assert.IsTrue(zeroed >= 20, $"only {zeroed} values were zero at an accuracy of 0.99");
        }

        /// <summary>
        /// The largest value a sketch holds is reported as finite and within the
        /// accuracy at every sign, the specific case that began this: a quantile over
        /// the largest doubles used to come back as infinity.
        /// </summary>
        [TestMethod]
        public void TestTheLargestDoublesAreAnsweredFiniteAtEveryQuantile()
        {
            foreach (var accuracy in Accuracies)
            {
                foreach (var value in new[] { double.MaxValue, -double.MaxValue })
                {
                    var sketch = new DDSketch(accuracy);
                    sketch.Add(value);
                    foreach (var q in new[] { 0.0, 0.5, 1.0 })
                    {
                        var answer = sketch.Quantile(q);
                        Assert.IsTrue(double.IsFinite(answer),
                            $"a={accuracy} q={q} v={value:R}: {answer}");
                        Assert.IsTrue(Within(accuracy, value, answer),
                            $"a={accuracy} q={q} v={value:R}: {answer:R}");
                    }
                }
            }
        }
    }
}
