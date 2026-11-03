using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Model-based tests: run the structure against an exact reference model and hold
    /// every answer to it, false positives included.
    /// <para>
    /// The statistical tests elsewhere allow the structure a tolerance. These allow
    /// none, because for fingerprint-based filters the abstraction is exact: a
    /// quotient filter answers true for x if and only if some stored item shares x's
    /// fingerprint, and the fingerprint function is a pure function of the hash. A
    /// model that tracks the fingerprint multiset therefore predicts every answer the
    /// filter will ever give -- which items are found, which absent items collide
    /// into false positives, and what each deletion leaves behind. Any deviation in
    /// either direction is a defect, not bad luck.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestModelBased
    {
        private static byte[] K(string s) => Encoding.UTF8.GetBytes(s);

        /// <summary>
        /// The quotient filter under sustained add/remove churn, checked against the
        /// fingerprint multiset after every phase. The fingerprint math is restated
        /// from the constructor's documented sizing (r = ceil(log2(1/p)) remainder
        /// bits, q = ceil(log2(n / 0.75)) quotient bits, fingerprint = top q + r bits
        /// of the hash) rather than read from the filter, so the model is an
        /// independent oracle: if either side drifts, they disagree.
        /// <para>
        /// Every answer is checked in both directions. Members must be found, and --
        /// the half no behavioral test states -- an absent key must answer true
        /// exactly when its fingerprint collides with a stored one. The filter's
        /// false positives are predictable, so they are predicted.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestQuotientFilterAgreesWithItsFingerprintModelUnderChurn()
        {
            const uint N = 2000;
            const double FpRate = 0.01;
            var filter = new QuotientFilter(N, FpRate);

            var remainderBits = (int)Math.Max(1, Math.Ceiling(Math.Log2(1.0 / FpRate)));
            var quotientBits = (int)Math.Max(1, Math.Ceiling(Math.Log2(N / 0.75)));
            var hash = Defaults.GetDefaultHashFunction();
            ulong Fp(string s)
            {
                return hash(K(s)) >> (64 - (quotientBits + remainderBits));
            }

            // Multiset: the filter preserves multiplicity, so the model must too.
            var model = new Dictionary<ulong, int>();
            void ModelAdd(ulong fp)
            {
                model[fp] = model.GetValueOrDefault(fp) + 1;
            }
            void ModelRemove(ulong fp)
            {
                if (model[fp] == 1) model.Remove(fp); else model[fp]--;
            }

            var live = new List<string>();
            var rand = new Random(29);
            var next = 0;
            var falsePositivesPredicted = 0;

            void CheckEverything(string phase)
            {
                foreach (var s in live)
                {
                    Assert.IsTrue(filter.Test(K(s)),
                        $"{phase}: '{s}' is in the filter's model but was not found.");
                }
                for (int i = 0; i < 500; i++)
                {
                    var probe = $"absent-{phase}-{i}";
                    var expected = model.ContainsKey(Fp(probe));
                    if (expected) falsePositivesPredicted++;
                    Assert.AreEqual(expected, filter.Test(K(probe)),
                        $"{phase}: '{probe}' has {(expected ? "a" : "no")} fingerprint " +
                        "collision with the stored items, so the filter must answer " +
                        $"{expected}. A fingerprint filter's false positives are not " +
                        "random; they are exactly the collisions.");
                }
            }

            // Fill to a realistic load in bursts, with removals between them, so runs
            // and clusters form, shift, and break repeatedly.
            for (int phase = 0; phase < 6; phase++)
            {
                for (int i = 0; i < 250; i++)
                {
                    var s = $"item-{next++}";
                    filter.Add(K(s));
                    ModelAdd(Fp(s));
                    live.Add(s);
                }
                for (int i = 0; i < 75 && live.Count > 0; i++)
                {
                    var idx = rand.Next(live.Count);
                    var s = live[idx];
                    Assert.IsTrue(filter.TestAndRemove(K(s)),
                        $"phase {phase}: removing '{s}', which is present, must succeed.");
                    ModelRemove(Fp(s));
                    live.RemoveAt(idx);
                }
                CheckEverything($"phase{phase}");
            }

            // A duplicate needs its own removal, and removing one copy leaves the
            // other. A fresh key, added exactly twice: a churned key might already
            // have been removed, which would make "the other copy" a fiction.
            var dup = "duplicate-key";
            filter.Add(K(dup)); ModelAdd(Fp(dup)); live.Add(dup);
            filter.Add(K(dup)); ModelAdd(Fp(dup)); live.Add(dup);
            Assert.IsTrue(filter.TestAndRemove(K(dup)));
            ModelRemove(Fp(dup)); live.Remove(dup);
            Assert.IsTrue(filter.Test(K(dup)),
                "one copy of a twice-added item was removed; the other must remain.");

            CheckEverything("final");
            Console.WriteLine($"held={live.Count} distinct fingerprints={model.Count} " +
                $"predicted false positives among probes={falsePositivesPredicted}");
        }

        /// <summary>
        /// The theta sketch's exact characterization: whatever theta the sketch lands
        /// on, its values must be exactly the input hashes below theta -- sorted,
        /// distinct, none lost, none invented -- and its estimate must be the held
        /// count divided by theta's fraction of the hash space.
        /// <para>
        /// Stated this way because byte-level identities do not hold for this
        /// structure: the lazy double-width buffer sets theta at compaction time, so
        /// the same set ingested in two orders lands on different-but-equally-valid
        /// states (measured: equal-length payloads, different bytes). The
        /// characterization is the invariant underneath -- it holds for every valid
        /// state, so it also holds across Union, Intersect and Difference, whose
        /// outputs get the same check against set arithmetic on the inputs.
        /// </para>
        /// <para>
        /// The sketch's internals are private, so the test reads them through the
        /// persisted payload, whose layout is a frozen public contract:
        /// nominalEntries u32, theta u64, held u32, then held sorted u64 values.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestThetaSketchValuesAreExactlyTheHashesBelowTheta()
        {
            var hash = Defaults.GetDefaultHashFunction();

            var itemsA = Enumerable.Range(0, 700).Select(i => $"a-{i}").ToList();
            var itemsB = Enumerable.Range(0, 500).Select(i => $"b-{i}")
                .Concat(itemsA.Take(200)).ToList();

            var sa = new ThetaSketch(256); foreach (var s in itemsA) sa.Add(K(s));
            var sb = new ThetaSketch(256); foreach (var s in itemsB) sb.Add(K(s));

            var hashesA = itemsA.Select(s => hash(K(s))).ToHashSet();
            var hashesB = itemsB.Select(s => hash(K(s))).ToHashSet();

            CheckCharacterization("direct", sa, hashesA);
            CheckCharacterization("union", sa.Union(sb), hashesA.Union(hashesB).ToHashSet());
            CheckCharacterization("intersect", sa.Intersect(sb), hashesA.Intersect(hashesB).ToHashSet());
            CheckCharacterization("difference", sa.Difference(sb), hashesA.Except(hashesB).ToHashSet());

            // Below nominal entries nothing is ever discarded, so the sketch is exact
            // and must say so: theta still at the top of the hash space.
            var small = new ThetaSketch(256);
            for (int i = 0; i < 100; i++) small.Add(K($"s-{i}"));
            var (_, smallTheta, smallHeld, _) = Parse(small.ToByteArray());
            Assert.AreEqual(ulong.MaxValue, smallTheta,
                "Nothing was discarded, so theta must still sit at the top of the " +
                "hash space and the count must be exact.");
            Assert.AreEqual(100u, smallHeld);
            Assert.AreEqual(100ul, small.Count());
        }

        private static void CheckCharacterization(
            string what, ThetaSketch sketch, HashSet<ulong> inputHashes)
        {
            var (_, theta, held, values) = Parse(sketch.ToByteArray());

            var expected = inputHashes.Where(h => h < theta).OrderBy(h => h).ToArray();

            Console.WriteLine($"{what}: theta={theta} held={held} expected={expected.Length} " +
                $"missing={expected.Except(values).Count()} extra={values.Except(expected).Count()}");
            Assert.HasCount((int)held, values, $"{what}: held disagrees with the payload");
            CollectionAssert.AreEqual(expected, values,
                $"{what}: the sketch's values must be exactly the input hashes below " +
                $"its theta ({theta}). {expected.Length} qualify; a missing one is a " +
                "lost sample, an extra one is an invented item, and either quietly " +
                "reweights every estimate this sketch will ever give.");

            var predicted = (ulong)Math.Round(held / (theta / 18446744073709551616.0));
            Assert.AreEqual(predicted, sketch.Count(),
                $"{what}: the estimate must be held divided by theta's fraction of " +
                "the hash space -- that arithmetic is the entire estimator.");
        }

        /// <summary>Payload: nominalEntries u32, theta u64, held u32, held x u64.</summary>
        private static (uint Nominal, ulong Theta, uint Held, ulong[] Values) Parse(byte[] frame)
        {
            const int Header = 14;
            var p = frame.AsSpan(Header);
            var nominal = BinaryPrimitives.ReadUInt32LittleEndian(p);
            var theta = BinaryPrimitives.ReadUInt64LittleEndian(p[4..]);
            var held = BinaryPrimitives.ReadUInt32LittleEndian(p[12..]);
            var values = new ulong[held];
            for (int i = 0; i < held; i++)
            {
                values[i] = BinaryPrimitives.ReadUInt64LittleEndian(p[(16 + 8 * i)..]);
            }
            return (nominal, theta, held, values);
        }

        /// <summary>
        /// The cuckoo filter under churn, at the level its API guarantees: whatever
        /// it accepted and has not removed, it finds. This is the invariant its
        /// relocation logic once broke -- items displaced during an insert were lost,
        /// and every small test stayed green because nothing forced enough
        /// relocations to matter. Byte-level modeling is out of reach here (the
        /// filter's state includes a random generator's position), so the model
        /// tracks membership only, and only what the filter confirmed accepting.
        /// </summary>
        [TestMethod]
        public void TestCuckooFilterNeverForgetsWhatItHoldsUnderChurn()
        {
            var filter = new CuckooBloomFilter(2000, 0.01, seed: 71);
            var live = new List<string>();
            var rand = new Random(31);
            var next = 0;
            var refused = 0;

            for (int phase = 0; phase < 8; phase++)
            {
                for (int i = 0; i < 300; i++)
                {
                    var s = $"item-{next++}";
                    if (filter.Add(K(s))) live.Add(s); else refused++;
                }
                for (int i = 0; i < 150 && live.Count > 0; i++)
                {
                    var idx = rand.Next(live.Count);
                    Assert.IsTrue(filter.TestAndRemove(K(live[idx])),
                        $"phase {phase}: '{live[idx]}' was accepted and not yet " +
                        "removed, so removing it must succeed.");
                    live.RemoveAt(idx);
                }
                foreach (var s in live)
                {
                    Assert.IsTrue(filter.Test(K(s)),
                        $"phase {phase}: '{s}' was accepted and never removed. A " +
                        "cuckoo filter must not lose items to its own relocations.");
                }
            }

            Assert.AreEqual((uint)live.Count, filter.Count(),
                "the filter's count must match the accepted-minus-removed ledger");

            foreach (var s in Shuffled(live, 37)) filter.TestAndRemove(K(s));
            Assert.AreEqual(0u, filter.Count(),
                "removing everything must leave the filter holding nothing");
            Console.WriteLine($"churned {next} items, {refused} refused at capacity");
        }

        /// <summary>
        /// The counting filter's contract under the discipline its documentation
        /// requires -- only remove what was added: no member is ever denied, however
        /// the adds and removes interleave.
        /// </summary>
        [TestMethod]
        public void TestCountingBloomFilterNeverDeniesAMemberUnderChurn()
        {
            var filter = CountingBloomFilter.NewDefaultCountingBloomFilter(2000, 0.01);
            var live = new List<string>();
            var rand = new Random(59);
            var next = 0;

            for (int phase = 0; phase < 8; phase++)
            {
                for (int i = 0; i < 250; i++)
                {
                    var s = $"item-{next++}";
                    filter.Add(K(s));
                    live.Add(s);
                }
                for (int i = 0; i < 125 && live.Count > 0; i++)
                {
                    var idx = rand.Next(live.Count);
                    Assert.IsTrue(filter.TestAndRemove(K(live[idx])),
                        $"phase {phase}: removing the member '{live[idx]}' must succeed.");
                    live.RemoveAt(idx);
                }
                foreach (var s in live)
                {
                    Assert.IsTrue(filter.Test(K(s)),
                        $"phase {phase}: '{s}' is a member and was denied. Counters " +
                        "that another item's removal drove to zero are the classic " +
                        "way a counting filter manufactures false negatives.");
                }
            }
        }

        /// <summary>
        /// A Memento filter answering under a lowered block limit still finds every range that
        /// holds a key, held to the keys themselves as the model: whatever was added and not
        /// removed. Past the limit a range is not walked, and the answer must then be
        /// "possibly" while the filter holds anything and a certain no once it holds nothing.
        /// The ranges are of three kinds, every one deterministic: around a live key and wide
        /// enough to pass the limit, which is where a cap could lose a key; anywhere and as
        /// wide; and narrow ones under the limit, which are walked as before. The tallies
        /// require each outcome the cap decides to have happened many times, and walked
        /// ranges that hold nothing to have been answered no, so the cap is not simply
        /// answering yes to everything.
        /// </summary>
        [TestMethod]
        public void TestMementoFilterFindsEveryRangeHoldingAKeyUnderItsBlockLimit()
        {
            const ulong limit = 8;
            const ulong blockWidth = 64;
            var filter = new MementoFilter(blockWidth, 24, initialCapacity: 64) { MaxInteriorBlocks = limit };
            var live = new List<ulong>();
            var rand = new Random(83);
            int cappedHolding = 0, cappedPossibly = 0, walkedHolding = 0, walkedNo = 0;

            bool Holds(ulong low, ulong high) => live.Exists(k => k >= low && k <= high);

            ulong Interior(ulong low, ulong high)
            {
                var span = (high / blockWidth) - (low / blockWidth);
                return span > 1 ? span - 1 : 0;
            }

            // Wide ranges span at least twelve blocks, past the limit; narrow ones at most seven.
            (ulong, ulong) Draw()
            {
                switch (rand.Next(4))
                {
                    case 0 when live.Count > 0:
                        var key = live[rand.Next(live.Count)];
                        var below = (ulong)rand.Next(0, (int)blockWidth * 2000);
                        var above = (ulong)rand.Next((int)blockWidth * 12, (int)blockWidth * 2000);
                        return (key > below ? key - below : 0, key + above);
                    case 1:
                        var start = (ulong)rand.NextInt64(1L << 30);
                        return (start, start + (ulong)rand.Next((int)blockWidth * 12, (int)blockWidth * 4000));
                    case 2 when live.Count > 0:
                        var near = live[rand.Next(live.Count)];
                        var under = (ulong)rand.Next(0, (int)blockWidth * 3);
                        return (near > under ? near - under : 0, near + (ulong)rand.Next(0, (int)blockWidth * 3));
                    default:
                        var from = (ulong)rand.NextInt64(1L << 30);
                        return (from, from + (ulong)rand.Next(0, (int)blockWidth * 6));
                }
            }

            for (var phase = 0; phase < 8; phase++)
            {
                for (var i = 0; i < 200; i++)
                {
                    var key = (ulong)rand.NextInt64(1L << 30);
                    filter.Add(key);
                    live.Add(key);
                }
                for (var i = 0; i < 100; i++)
                {
                    var at = rand.Next(live.Count);
                    Assert.IsTrue(filter.TestAndRemove(live[at]));
                    live.RemoveAt(at);
                }

                for (var q = 0; q < 300; q++)
                {
                    var (low, high) = Draw();
                    var answer = filter.TestRange(low, high);
                    var holds = Holds(low, high);
                    if (holds)
                    {
                        Assert.IsTrue(answer,
                            $"phase {phase}: [{low}, {high}] holds a live key and was answered no");
                    }

                    if (Interior(low, high) > limit)
                    {
                        Assert.IsTrue(answer,
                            $"phase {phase}: [{low}, {high}] is past the limit, in a filter holding " +
                            $"{live.Count} keys, and must answer possibly");
                        if (holds) cappedHolding++; else cappedPossibly++;
                    }
                    else if (holds)
                    {
                        walkedHolding++;
                    }
                    else if (!answer)
                    {
                        walkedNo++;
                    }
                }
            }

            foreach (var key in live)
            {
                Assert.IsTrue(filter.TestAndRemove(key));
            }
            live.Clear();
            Assert.AreEqual(0UL, filter.Count());

            var emptyCapped = 0;
            for (var q = 0; q < 600; q++)
            {
                var (low, high) = Draw();
                Assert.IsFalse(filter.TestRange(low, high), $"[{low}, {high}] in a filter holding nothing");
                if (Interior(low, high) > limit) emptyCapped++;
            }

            Console.WriteLine($"capped holding {cappedHolding}, capped possibly {cappedPossibly}, " +
                $"walked holding {walkedHolding}, walked no {walkedNo}, empty capped {emptyCapped}");
            Assert.IsGreaterThan(100, cappedHolding, "too few ranges past the limit held a key to show none was lost");
            Assert.IsGreaterThan(100, cappedPossibly, "too few ranges past the limit held nothing");
            Assert.IsGreaterThan(100, walkedNo, "too few walked ranges answered no, so the filter may be answering yes to everything");
            Assert.IsGreaterThan(100, walkedHolding, "too few walked ranges held a key");
            Assert.IsGreaterThan(100, emptyCapped, "too few ranges past the limit were asked of the empty filter");
        }

        private static List<string> Shuffled(List<string> xs, int seed)
        {
            var copy = new List<string>(xs);
            var rand = new Random(seed);
            for (int i = copy.Count - 1; i > 0; i--)
            {
                int j = rand.Next(i + 1);
                (copy[i], copy[j]) = (copy[j], copy[i]);
            }
            return copy;
        }
    }
}
