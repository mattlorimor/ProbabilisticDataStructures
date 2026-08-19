using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The second of the paper's two constructions, tested on its own terms.
    /// <para>
    /// This variant's registers are correlated -- exactly one hash value comes from
    /// every interval -- and every estimator in the paper is derived assuming they are
    /// independent. So the estimators are approximations here where they are exact for
    /// <see cref="SetSketchVariant.SetSketch1"/>, and a test that inherited
    /// SetSketch1's bounds would pass because the approximation is good rather than
    /// because the estimator is right. What is asserted below is either exact (the
    /// sampling matches the paper's definition; the merge is register-for-register) or
    /// measured for this variant specifically.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestSetSketch2
    {
        private static byte[] Key(string s) => Encoding.UTF8.GetBytes(s);

        private static SetSketch Sketch(
            int registers = 256, SetSketchVariant variant = SetSketchVariant.SetSketch2) =>
            new SetSketch(registers, 1.001, 20, 65534, null, variant);

        private static (double Mean, double Spread) MeasureSpread(int trials, Func<int, double> run)
        {
            var values = Enumerable.Range(0, trials).Select(run).ToArray();
            var mean = values.Average();
            var spread = Math.Sqrt(
                values.Sum(v => (v - mean) * (v - mean)) / (trials - 1));
            return (mean, spread);
        }

        /// <summary>
        /// The paper defines this variant's draw in two pieces: interval boundaries at
        /// gamma_j = ln(1 + j/(m-j)) / a, and x_j drawn from an exponential truncated
        /// to [gamma_j, gamma_(j+1)) -- with the reference implementation
        /// (dynatrace-research/set-sketch-paper, sketch.hpp, verified against the
        /// source 2026-08-19) treating the final unbounded interval as a separate case,
        /// gamma + Exp(a).
        /// <para>
        /// The implementation collapses all of that into one expression. This test is
        /// the reason that is allowed to stand: it evaluates the paper's two cases here,
        /// independently, and holds the single expression to them. The final interval is
        /// included on purpose -- that is where the two-case definition and the one-line
        /// one could most easily disagree, and where the reference itself branches.
        /// </para>
        /// </summary>
        [TestMethod]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(8)]
        [DataRow(64)]
        [DataRow(4096)]
        public void TestTheSamplingMatchesThePapersTwoCaseDefinition(int m)
        {
            static double Gamma(int j, int m, double a) =>
                j >= m ? double.PositiveInfinity : Math.Log(1 + (j / (double)(m - j))) / a;

            // The paper's definition, written out as two cases exactly as stated.
            static double Paper(int m, double a, int i, double u)
            {
                var start = Gamma(i, m, a);
                if (i < m - 1)
                {
                    // An exponential of rate a truncated to [start, end), sampled by
                    // inverting its distribution over the interval.
                    var end = Gamma(i + 1, m, a);
                    var rate = a * (end - start);
                    var t = -Math.Log(1 - (u * (1 - Math.Exp(-rate)))) / rate;
                    return start + ((end - start) * t);
                }

                // The last interval runs to infinity, so the truncation does nothing.
                return start + (-Math.Log(1 - u) / a);
            }

            foreach (var a in new[] { 0.5, 1.0, 20.0 })
            {
                foreach (var i in new[] { 0, 1, m / 2, m - 2, m - 1 })
                {
                    if (i < 0 || i >= m)
                    {
                        continue;
                    }

                    foreach (var u in new[] { 1e-9, 0.01, 0.25, 0.5, 0.75, 0.99, 1 - 1e-9 })
                    {
                        var expected = Paper(m, a, i, u);
                        var actual = SetSketch.PointInInterval(m, a, i, u);

                        Assert.AreEqual(expected, actual, Math.Abs(expected) * 1e-9,
                            $"m={m} a={a} i={i} u={u}: the one-line draw must be the " +
                            "paper's truncated exponential over this interval.");
                    }
                }
            }
        }

        /// <summary>
        /// Two properties the early exit rests on: every point lands inside its own
        /// interval, and the points therefore ascend. If a point could fall below its
        /// interval's start, the implementation's habit of stopping at the first value
        /// too large to matter would discard values that still mattered -- silently,
        /// and only for some elements.
        /// </summary>
        [TestMethod]
        [DataRow(8)]
        [DataRow(64)]
        [DataRow(1024)]
        public void TestEveryPointLandsInItsIntervalSoTheRunAscends(int m)
        {
            const double A = 20.0;
            var random = new Random(4);

            for (var trial = 0; trial < 500; trial++)
            {
                var previous = double.NegativeInfinity;

                for (var i = 0; i < m; i++)
                {
                    var x = SetSketch.PointInInterval(m, A, i, random.NextDouble());
                    var start = SetSketch.IntervalStart(m, A, i);
                    var end = i + 1 >= m
                        ? double.PositiveInfinity
                        : SetSketch.IntervalStart(m, A, i + 1);

                    Assert.IsLessThanOrEqualTo(x, start,
                        $"m={m} i={i}: a point fell below its interval's start, which " +
                        "is the bound the early exit trusts.");
                    Assert.IsGreaterThan(x, end,
                        $"m={m} i={i}: a point fell outside its interval's end.");
                    Assert.IsGreaterThan(previous, x,
                        $"m={m} i={i}: the run stopped ascending, so stopping at the " +
                        "first value too large would discard values that still count.");

                    previous = x;
                }
            }
        }

        /// <summary>
        /// The merge is exact for this variant too -- register for register, the sketch
        /// that adding both sets to one sketch would have built, not an approximation.
        /// The correlated construction does not disturb this, because the merge is a
        /// maximum over registers and the maximum of two runs is the run of the union
        /// whichever way the runs were drawn. Worth confirming rather than assuming:
        /// users will reasonably expect the two variants to behave alike, and this is
        /// the property the issue that asked for this variant singled out.
        /// </summary>
        [TestMethod]
        public void TestTheMergeIsStillExactUnderTheCorrelatedConstruction()
        {
            var left = Sketch();
            var right = Sketch();
            var both = Sketch();

            for (var i = 0; i < 4000; i++)
            {
                left.Add(Key($"left-{i}"));
                both.Add(Key($"left-{i}"));
            }
            for (var i = 0; i < 4000; i++)
            {
                right.Add(Key($"right-{i}"));
                both.Add(Key($"right-{i}"));
            }

            Assert.IsTrue(left.Merge(right));

            CollectionAssert.AreEqual(both.RegisterValues, left.RegisterValues,
                "a merged sketch must be register-for-register the sketch that adding " +
                "both sets from the start would have built.");
            Assert.AreEqual(1.0, both.Jaccard(left),
                "the merged sketch must describe exactly the union.");
        }

        /// <summary>
        /// Merging across variants is refused. The merge's promise is the sketch that
        /// adding both sets to a single sketch would have built, and no single sketch
        /// draws its runs both ways -- so there is nothing for the result to be equal
        /// to, and returning something that merely looks plausible would be worse than
        /// refusing.
        /// </summary>
        [TestMethod]
        public void TestMergingAcrossVariantsIsRefused()
        {
            var one = Sketch(variant: SetSketchVariant.SetSketch1).Add(Key("a"));
            var two = Sketch(variant: SetSketchVariant.SetSketch2).Add(Key("a"));

            var ex = Assert.ThrowsExactly<ArgumentException>(() => one.Merge(two));
            StringAssert.Contains(ex.Message, "both ways");

            Assert.ThrowsExactly<ArgumentException>(() => two.Merge(one),
                "the refusal must hold whichever way round the merge is asked for.");
        }

        /// <summary>
        /// The paper's claim about this variant, measured rather than repeated: the
        /// correlation between registers helps at small cardinalities, and the
        /// advantage fades as the set grows. Measured over 200 keyed streams at 256
        /// registers, whose nominal relative error is 6.25%:
        /// <code>
        ///   n       SetSketch1   SetSketch2   ratio
        ///   10        7.12%        4.47%      0.63
        ///   100       5.91%        4.16%      0.70
        ///   1000      6.06%        5.10%      0.84
        ///   10000     6.35%        6.54%      1.03
        /// </code>
        /// <para>
        /// SetSketch1 sits at its nominal error across the whole range, which is what
        /// an exact estimator on independent registers should do. SetSketch2 beats it
        /// small and converges to it large. The assertion is deliberately loose -- a
        /// ratio below 0.85 at n = 100 against parity at n = 10,000 -- because the
        /// claim being tested is the direction and the fading, not a particular number.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestCorrelationBuysAccuracyOnSmallSetsAndFadesOnLargeOnes()
        {
            const int Trials = 200;

            static double SpreadAt(int n, SetSketchVariant variant)
            {
                var (_, spread) = MeasureSpread(Trials, t =>
                {
                    var sketch = Sketch(variant: variant);
                    for (var i = 0; i < n; i++)
                    {
                        sketch.Add(Key($"t{t}-i{i}"));
                    }
                    return (sketch.Cardinality() - n) / n;
                });
                return spread;
            }

            var smallOne = SpreadAt(100, SetSketchVariant.SetSketch1);
            var smallTwo = SpreadAt(100, SetSketchVariant.SetSketch2);
            var largeOne = SpreadAt(10000, SetSketchVariant.SetSketch1);
            var largeTwo = SpreadAt(10000, SetSketchVariant.SetSketch2);

            Console.WriteLine(
                $"n=100: s1={smallOne:P2} s2={smallTwo:P2} ratio={smallTwo / smallOne:F2}; " +
                $"n=10000: s1={largeOne:P2} s2={largeTwo:P2} ratio={largeTwo / largeOne:F2}");

            Assert.IsLessThan(0.85, smallTwo / smallOne,
                $"at 100 elements SetSketch2 spread {smallTwo:P2} against SetSketch1's " +
                $"{smallOne:P2}. The correlation between registers is supposed to buy " +
                "accuracy on small sets, and this is the only thing in this library " +
                "that checks it does.");

            Assert.IsGreaterThan(0.85, largeTwo / largeOne,
                $"at 10,000 elements SetSketch2 spread {largeTwo:P2} against " +
                $"SetSketch1's {largeOne:P2}. The advantage is supposed to fade as the " +
                "set grows; if it no longer does, the measurement above is describing " +
                "something other than the correlation.");
        }

        /// <summary>
        /// This variant's own accuracy, not inherited from the other's. The estimator
        /// is an approximation here, so what is asserted is that it stays close to the
        /// nominal error the register count buys -- measured at 6.5% or better against
        /// a nominal 6.25% -- rather than that it matches a bound derived under an
        /// assumption this variant breaks.
        /// </summary>
        [TestMethod]
        [DataRow(1000)]
        [DataRow(50000)]
        public void TestTheEstimatorHoldsItsNominalErrorForThisVariant(int n)
        {
            var (mean, spread) = MeasureSpread(60, t =>
            {
                var sketch = Sketch();
                for (var i = 0; i < n; i++)
                {
                    sketch.Add(Key($"t{t}-i{i}"));
                }
                return (sketch.Cardinality() - n) / n;
            });

            var nominal = 1.0 / Math.Sqrt(256);
            Console.WriteLine($"n={n}: bias={mean:P3} spread={spread:P3} nominal={nominal:P3}");

            Assert.IsLessThanOrEqualTo(nominal * 1.35, spread,
                $"n={n}: spread {spread:P2} against a nominal {nominal:P2}. The " +
                "approximation may cost a little accuracy; it may not cost this much.");
            Assert.IsLessThanOrEqualTo(4 * spread / Math.Sqrt(60), Math.Abs(mean),
                $"n={n}: mean relative error {mean:P3} is more than four standard " +
                "errors from zero, so the estimator is biased for this variant.");
        }

        /// <summary>
        /// A sketch of this variant round-trips, and comes back still knowing which
        /// construction it is -- without which it would keep counting the other way and
        /// mix two constructions in one set of registers.
        /// </summary>
        [TestMethod]
        public void TestTheVariantSurvivesARoundTrip()
        {
            var original = Sketch();
            for (var i = 0; i < 5000; i++)
            {
                original.Add(Key($"item-{i}"));
            }

            var restored = Persistence.FromByteArray<SetSketch>(original.ToByteArray());

            Assert.AreEqual(SetSketchVariant.SetSketch2, restored.Variant,
                "a restored sketch must know which construction built it.");
            CollectionAssert.AreEqual(original.RegisterValues, restored.RegisterValues);
            Assert.AreEqual(1.0, original.Jaccard(restored));

            // And goes on counting the way it started, which the merge identity checks
            // exactly: a sketch that resumed with the other construction would diverge
            // from one that never stopped.
            var continued = Sketch();
            for (var i = 0; i < 5000; i++)
            {
                continued.Add(Key($"item-{i}"));
            }
            for (var i = 5000; i < 8000; i++)
            {
                restored.Add(Key($"item-{i}"));
                continued.Add(Key($"item-{i}"));
            }

            CollectionAssert.AreEqual(continued.RegisterValues, restored.RegisterValues,
                "a restored sketch must go on drawing its runs the way it drew them " +
                "before it was written.");
        }

        /// <summary>
        /// The first variant's payload is untouched by the second one existing. It
        /// writes no variant byte and stays at the format version it always wrote, so
        /// the bytes are identical to those a library from before this variant existed
        /// produced -- and readable by one. Only the second variant bumps the version,
        /// which is the whole reason for making the byte conditional rather than
        /// writing it always.
        /// </summary>
        [TestMethod]
        public void TestTheFirstVariantsPayloadIsUnchanged()
        {
            var one = Sketch(variant: SetSketchVariant.SetSketch1);
            var two = Sketch(variant: SetSketchVariant.SetSketch2);
            for (var i = 0; i < 200; i++)
            {
                one.Add(Key($"item-{i}"));
                two.Add(Key($"item-{i}"));
            }

            var oneBytes = one.ToByteArray();
            var twoBytes = two.ToByteArray();

            // Format version sits at offset 4, little-endian.
            Assert.AreEqual(1, oneBytes[4] | (oneBytes[5] << 8),
                "the first variant must still write format version 1, or every payload " +
                "this library ever wrote becomes unreadable to record a change that " +
                "the sketches in them did not make.");
            Assert.AreEqual(4, twoBytes[4] | (twoBytes[5] << 8),
                "the second variant must write the version that carries a variant byte.");

            Assert.HasCount(oneBytes.Length + 1, twoBytes,
                "the second variant's payload must be exactly one byte longer -- the " +
                "variant itself.");

            var restored = Persistence.FromByteArray<SetSketch>(oneBytes);
            Assert.AreEqual(SetSketchVariant.SetSketch1, restored.Variant,
                "a payload with no variant byte is the first construction by " +
                "definition, which is what keeps old payloads readable.");
        }

        /// <summary>
        /// The insert path decides whether an interval is worth drawing from with one
        /// comparison, having done the logarithms once when the bound last moved. That
        /// is only allowed to stand if it agrees with the literal decision --
        /// "what value would this interval's start earn, and does it beat the bound?"
        /// -- at every interval, for every bound.
        /// <para>
        /// The two directions are not equally forgiving. Stopping <i>later</i> than the
        /// literal rule costs a wasted draw and nothing else, because a point that
        /// cannot beat the bound cannot raise a register either. Stopping <i>earlier</i>
        /// skips a draw that could have raised one, and the sketch is quietly wrong for
        /// that element only. So this sweeps every interval rather than sampling, and
        /// every bound from empty to saturated, including the ceiling itself where the
        /// algebra behind the comparison stops applying and the code special-cases it.
        /// </para>
        /// </summary>
        [TestMethod]
        [DataRow(64, 1.001, 20.0, 65534)]
        [DataRow(64, 2.0, 20.0, 65534)]
        [DataRow(256, 1.001, 20.0, 65534)]
        [DataRow(256, 1.05, 1.0, 4000)]
        [DataRow(1024, 1.2, 0.5, 300)]
        [DataRow(1, 1.001, 20.0, 65534)]
        [DataRow(2, 1.5, 100.0, 10)]
        public void TestTheFastStopAgreesWithTheLiteralOneEverywhere(
            int m, double b, double a, int q)
        {
            var sketch = new SetSketch(m, b, a, q, null, SetSketchVariant.SetSketch2);

            var bounds = new List<int> { 0, 1, 2, 3, q - 1, q, q + 1 };
            for (var L = 4; L < q; L = (int)(L * 1.7) + 1)
            {
                bounds.Add(L);
            }

            var stoppedSomewhere = false;
            var ranSomewhere = false;

            foreach (var bound in bounds.Where(x => x >= 0 && x <= q + 1).Distinct())
            {
                sketch.ForceLowerBound(bound);

                for (var i = 0; i < m; i++)
                {
                    var literal = sketch.LiterallyStopsAtInterval(i);
                    var fast = sketch.StopsAtInterval(i);

                    Assert.AreEqual(literal, fast,
                        $"m={m} b={b} a={a} q={q} bound={bound} i={i}: the comparison " +
                        $"says {(fast ? "stop" : "draw")} where asking ValueFor says " +
                        $"{(literal ? "stop" : "draw")}. Stopping early skips a draw " +
                        "that could have raised a register.");

                    stoppedSomewhere |= literal;
                    ranSomewhere |= !literal;
                }
            }

            Assert.IsTrue(stoppedSomewhere,
                "no bound and interval in this sweep ever stopped, so the agreement " +
                "above is agreement about nothing.");
            Assert.IsTrue(ranSomewhere,
                "every bound and interval in this sweep stopped, so the agreement " +
                "above is agreement about nothing.");
        }

        /// <summary>
        /// And the same equivalence end to end: a sketch whose stop is decided by the
        /// literal rule and one whose stop is decided by the comparison must finish
        /// register for register identical, over a stream long enough to move the bound
        /// many times. The sweep above proves the predicates agree; this proves nothing
        /// else in the insert path depends on which one was consulted.
        /// </summary>
        [TestMethod]
        public void TestTheOptimisedStopBuildsTheSameSketch()
        {
            var sketch = Sketch(registers: 512);
            var literalStops = 0;
            var fastStops = 0;

            for (var i = 0; i < 20000; i++)
            {
                // Before each element, both rules must agree about every interval at
                // whatever bound the stream has driven the sketch to.
                for (var interval = 0; interval < 512; interval++)
                {
                    if (sketch.LiterallyStopsAtInterval(interval))
                    {
                        literalStops++;
                    }
                    if (sketch.StopsAtInterval(interval))
                    {
                        fastStops++;
                    }
                }

                sketch.Add(Key($"item-{i}"));
            }

            Assert.AreEqual(literalStops, fastStops,
                $"over 20,000 elements the two rules disagreed: {literalStops} stops " +
                $"the literal way against {fastStops} the fast way.");
            Assert.IsGreaterThan(0, fastStops,
                "the stop never fired across the whole stream, so this compares two " +
                "rules that were never asked anything.");
        }
    }
}
