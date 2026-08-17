using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Tests for <see cref="HeavyKeeper"/>, against Gong, Yang et al., "HeavyKeeper:
    /// An Accurate Algorithm for Finding Top-k Elephant Flows" (USENIX ATC 2018).
    /// </summary>
    /// <remarks>
    /// One departure from the paper as printed, recorded here because a test depends
    /// on it. Algorithm 1's Optimization II increments a non-heap flow's counter only
    /// while C &lt; nmin, and Optimization I admits a flow only when its estimate is
    /// exactly nmin + 1 -- conditions that together deadlock: a counter could reach
    /// nmin and never exceed it, so nothing could ever join a full heap. The authors'
    /// reference implementation (papergitkeeper/heavy-keeper-project, heavykeeper.h)
    /// uses C &lt;= nmin, under which a genuine flow steps to exactly nmin + 1 and is
    /// admitted -- the only reading consistent with the paper's own Theorem 1.
    /// <see cref="TestAdmissionHappensAtExactlyHeapMinPlusOne"/> fails under the
    /// printed inequality and passes under the implemented one.
    /// </remarks>
    [TestClass]
    public class TestHeavyKeeper
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        /// <summary>
        /// A structure with no room for tracked flows answers nothing, and the heap
        /// would index its empty root on the first admission decision.
        /// </summary>
        [TestMethod]
        public void TestZeroKIsRefused()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new HeavyKeeper(0, 1024));
        }

        /// <summary>
        /// A zero-width array holds no buckets, and every insertion indexes one.
        /// </summary>
        [TestMethod]
        public void TestZeroWidthIsRefused()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new HeavyKeeper(10, 0));
        }

        /// <summary>
        /// A structure with no arrays holds nothing at all.
        /// </summary>
        [TestMethod]
        public void TestZeroDepthIsRefused()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new HeavyKeeper(10, 1024, depth: 0));
        }

        /// <summary>
        /// The decay base must exceed one: the decay probability is b^-C, so b = 1
        /// decays every mismatch with certainty regardless of the counter -- no bucket
        /// could hold anything against competition -- and b below one decays *more*
        /// readily as a flow grows, which inverts the structure's entire premise.
        /// </summary>
        [TestMethod]
        public void TestDecayBaseAtOrBelowOneIsRefused()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new HeavyKeeper(10, 1024, decay: 1.0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new HeavyKeeper(10, 1024, decay: 0.9));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new HeavyKeeper(10, 1024, decay: double.NaN));
        }

        /// <summary>
        /// A single flow is counted exactly. It is admitted to the heap on its first
        /// arrival (the heap has room), after which every fingerprint match increments
        /// unconditionally, and nothing else exists to decay it.
        /// </summary>
        [TestMethod]
        public void TestASingleFlowIsCountedExactly()
        {
            var hk = new HeavyKeeper(10, 1024, seed: 1);

            for (var i = 0; i < 500; i++)
            {
                hk.Add(Key("the-only-flow"));
            }

            Assert.AreEqual(500UL, hk.Count(Key("the-only-flow")));

            var elements = hk.Elements();
            Assert.HasCount(1, elements);
            Assert.AreEqual(500UL, elements[0].Freq);
            CollectionAssert.AreEqual(Key("the-only-flow"), elements[0].Data.ToArray());
        }

        /// <summary>
        /// With fewer flows than the heap tracks and no two flows contesting a bucket,
        /// every count is exact: every flow is heap-resident from its first arrival,
        /// so Optimization II never withholds an increment, and no bucket ever sees a
        /// mismatched fingerprint, so nothing is ever decayed.
        /// </summary>
        [TestMethod]
        public void TestDistinctFlowsUnderCapacityAreAllExact()
        {
            var hk = new HeavyKeeper(64, 4096, seed: 2);

            // 40 flows with counts 10 through 49, interleaved rather than one flow
            // at a time, so the heap's minimum moves while counting is in progress.
            // 50 rounds, because the largest flow needs 49 of them.
            for (var round = 0; round < 50; round++)
            {
                for (var f = 0; f < 40; f++)
                {
                    if (round < 10 + f)
                    {
                        hk.Add(Key($"flow-{f}"));
                    }
                }
            }

            // The premise the exactness claim rests on: no two of these flows landed
            // in the same bucket of the same array. The seed and width above were
            // chosen so that they do not; if a change to hashing breaks that, this
            // guard fails the test before the assertions below mislead anyone.
            Assert.IsTrue(NoBucketIsContested(hk,
                Enumerable.Range(0, 40).Select(f => $"flow-{f}")),
                "Two test flows contest a bucket; the exactness premise is void.");

            for (var f = 0; f < 40; f++)
            {
                Assert.AreEqual((ulong)(10 + f), hk.Count(Key($"flow-{f}")),
                    $"flow-{f} should be exact.");
            }
        }

        /// <summary>
        /// The heap admits a new flow at exactly heap-minimum plus one -- the paper's
        /// Theorem 1 -- and not before. This is the test that distinguishes the
        /// authors' reference implementation (increment while C &lt;= nmin) from
        /// Algorithm 1 as printed (C &lt; nmin), under which the flow would stall at
        /// nmin forever and this test would hang at the first assertion.
        /// </summary>
        [TestMethod]
        public void TestAdmissionHappensAtExactlyHeapMinPlusOne()
        {
            var hk = new HeavyKeeper(3, 4096, seed: 3);

            // Fill the heap: three flows at 50, 60, 70. The heap minimum is 50.
            for (var i = 0; i < 50; i++) hk.Add(Key("resident-a"));
            for (var i = 0; i < 60; i++) hk.Add(Key("resident-b"));
            for (var i = 0; i < 70; i++) hk.Add(Key("resident-c"));

            Assert.IsTrue(NoBucketIsContested(hk, new[]
                { "resident-a", "resident-b", "resident-c", "newcomer" }),
                "Test flows contest a bucket; counts below would not be exact.");

            // A newcomer grows one arrival at a time. Through its 50th arrival its
            // estimate is at most the heap minimum, and it must not be admitted.
            for (var i = 0; i < 50; i++)
            {
                hk.Add(Key("newcomer"));
            }
            Assert.IsFalse(
                hk.Elements().Any(e => e.Data.Span.SequenceEqual(Key("newcomer"))),
                "Admitted at the heap minimum; Theorem 1 admits only at nmin + 1.");

            // The 51st arrival steps its counter to exactly nmin + 1, which is the
            // one moment Optimization I admits it -- expelling resident-a.
            hk.Add(Key("newcomer"));

            var elements = hk.Elements();
            Assert.IsTrue(
                elements.Any(e => e.Data.Span.SequenceEqual(Key("newcomer"))),
                "Not admitted at nmin + 1.");
            Assert.AreEqual(51UL,
                elements.Single(e => e.Data.Span.SequenceEqual(Key("newcomer"))).Freq);
            Assert.IsFalse(
                elements.Any(e => e.Data.Span.SequenceEqual(Key("resident-a"))),
                "The heap grew instead of expelling its minimum.");
        }

        /// <summary>
        /// No flow is ever over-counted -- the paper's Theorem 2, and the property
        /// that separates this structure from Count-Min, whose errors are all in the
        /// other direction. Holds exactly when no two flows share a bucket with the
        /// same fingerprint, which the test verifies rather than assumes.
        /// </summary>
        [TestMethod]
        public void TestNoFlowIsEverOverCounted()
        {
            var hk = new HeavyKeeper(8, 256, seed: 4);
            var truth = new Dictionary<string, ulong>();

            // A contested workload: 8 elephants and 300 mice through 256 buckets, so
            // buckets are shared and decay is constantly at work.
            for (var round = 0; round < 100; round++)
            {
                for (var e = 0; e < 8; e++)
                {
                    var key = $"elephant-{e}";
                    hk.Add(Key(key));
                    truth[key] = truth.GetValueOrDefault(key) + 1;
                }

                for (var m = 0; m < 3; m++)
                {
                    var key = $"mouse-{round}-{m}";
                    hk.Add(Key(key));
                    truth[key] = truth.GetValueOrDefault(key) + 1;
                }
            }

            Assert.IsTrue(NoFingerprintCollisionAmong(hk, truth.Keys),
                "Two flows share a bucket and a fingerprint; Theorem 2's premise is " +
                "void for this stream.");

            var underCounted = 0;
            foreach (var (key, count) in truth)
            {
                var reported = hk.Count(Key(key));
                Assert.IsLessThanOrEqualTo(count, reported,
                    $"{key} reported {reported} of a true {count}: over-counted.");
                if (reported < count)
                {
                    underCounted++;
                }
            }

            // Vacuity guard: if nothing was ever under-counted, no bucket was ever
            // contested and the interesting half of the theorem went untested.
            Assert.IsGreaterThan(0, underCounted,
                "No flow was under-counted; the workload never exercised decay.");
        }

        /// <summary>
        /// The ten largest flows of a skewed stream are identified, every one of
        /// them, through a flood of two hundred times as many mice.
        /// </summary>
        [TestMethod]
        public void TestElephantsSurviveAMouseFlood()
        {
            var hk = new HeavyKeeper(10, 2048, seed: 5);

            // Ten elephants of 200..1100 arrivals, interleaved with 2,000 mice of one
            // arrival each, mixed so mice keep arriving while elephants grow.
            var stream = new List<string>();
            for (var e = 0; e < 10; e++)
            {
                for (var i = 0; i < 200 + e * 100; i++)
                {
                    stream.Add($"elephant-{e}");
                }
            }
            for (var m = 0; m < 2000; m++)
            {
                stream.Add($"mouse-{m}");
            }

            // Deterministic shuffle, so every run sees the same interleaving.
            var order = new Random(42);
            for (var i = stream.Count - 1; i > 0; i--)
            {
                var j = order.Next(i + 1);
                (stream[i], stream[j]) = (stream[j], stream[i]);
            }

            foreach (var key in stream)
            {
                hk.Add(Key(key));
            }

            var reported = hk.Elements()
                .Select(e => Encoding.ASCII.GetString(e.Data.Span))
                .ToArray();

            for (var e = 0; e < 10; e++)
            {
                Assert.Contains($"elephant-{e}", reported,
                    $"elephant-{e} was lost among the mice.");
            }
        }

        /// <summary>
        /// A flow held at no bucket reports zero -- mice are invisible by design,
        /// which is the documented difference from Count-Min, where every flow
        /// reports at least its true count.
        /// </summary>
        [TestMethod]
        public void TestAFlowEvictedFromItsBucketsReportsZero()
        {
            // One bucket, one array: everything contests the same cell. The mouse
            // arrives once; the elephant then hammers the bucket until the mouse's
            // counter of one is decayed (probability 1/1.08 per arrival) and the cell
            // is claimed. Twenty arrivals make survival odds below 1 in 10^20; the
            // seed makes the outcome exact rather than merely certain.
            var hk = new HeavyKeeper(2, 1, depth: 1, seed: 6);

            hk.Add(Key("mouse"));
            for (var i = 0; i < 20; i++)
            {
                hk.Add(Key("elephant"));
            }

            Assert.AreEqual(0UL, hk.Count(Key("mouse")),
                "A flow whose bucket was taken should report nothing, not something.");
            Assert.IsGreaterThan(0UL, hk.Count(Key("elephant")),
                "The elephant should hold the bucket it fought for.");
        }

        /// <summary>
        /// Everything the structure is round-trips: two writes of the same structure
        /// are identical, and a reload answers exactly as the original does.
        /// </summary>
        [TestMethod]
        public void TestRoundTripPreservesEverything()
        {
            var hk = new HeavyKeeper(5, 512, depth: 2, decay: 1.08, seed: 7);
            for (var i = 0; i < 300; i++)
            {
                hk.Add(Key($"flow-{i % 25}"));
            }

            using var stream = new MemoryStream();
            hk.WriteTo(stream);
            stream.Position = 0;
            var restored = HeavyKeeper.ReadFrom(stream);

            for (var i = 0; i < 25; i++)
            {
                Assert.AreEqual(hk.Count(Key($"flow-{i}")), restored.Count(Key($"flow-{i}")),
                    $"flow-{i} answers differently after the round trip.");
            }

            var before = hk.Elements();
            var after = restored.Elements();
            Assert.AreEqual(before.Length, after.Length);
            for (var i = 0; i < before.Length; i++)
            {
                CollectionAssert.AreEqual(
                    before[i].Data.ToArray(), after[i].Data.ToArray());
                Assert.AreEqual(before[i].Freq, after[i].Freq);
            }

            using var first = new MemoryStream();
            using var second = new MemoryStream();
            hk.WriteTo(first);
            restored.WriteTo(second);
            CollectionAssert.AreEqual(first.ToArray(), second.ToArray(),
                "The restored structure writes different bytes than its source.");
        }

        /// <summary>
        /// The decay generator resumes mid-sequence after a round trip rather than
        /// starting over. A structure checkpointed on a schedule must not replay the
        /// same decay decisions after every load -- the same reasoning, and the same
        /// test shape, as the stable filter's.
        /// </summary>
        [TestMethod]
        public void TestDecayDrawsResumeRatherThanRestartAfterAReload()
        {
            var continuous = new HeavyKeeper(4, 8, depth: 2, seed: 8);
            var checkpointed = new HeavyKeeper(4, 8, depth: 2, seed: 8);

            // A contested phase, so decay draws are actually consumed.
            for (var i = 0; i < 200; i++)
            {
                continuous.Add(Key($"first-{i % 40}"));
                checkpointed.Add(Key($"first-{i % 40}"));
            }

            using var stream = new MemoryStream();
            checkpointed.WriteTo(stream);
            stream.Position = 0;
            var reloaded = HeavyKeeper.ReadFrom(stream);

            // Both continue through a second contested phase. If the reload reset the
            // generator to its seed, its decay decisions replay the first phase's
            // draws and the states diverge.
            for (var i = 0; i < 200; i++)
            {
                continuous.Add(Key($"second-{i % 40}"));
                reloaded.Add(Key($"second-{i % 40}"));
            }

            using var a = new MemoryStream();
            using var b = new MemoryStream();
            continuous.WriteTo(a);
            reloaded.WriteTo(b);
            CollectionAssert.AreEqual(a.ToArray(), b.ToArray(),
                "A reloaded structure diverged from one that was never written out.");
        }

        /// <summary>
        /// Reset restores the freshly-constructed state: nothing tracked, nothing
        /// counted, and the structure counts correctly afterwards.
        /// </summary>
        [TestMethod]
        public void TestResetRestoresTheEmptyState()
        {
            var hk = new HeavyKeeper(5, 512, seed: 9);
            for (var i = 0; i < 100; i++)
            {
                hk.Add(Key($"flow-{i % 10}"));
            }

            hk.Reset();

            Assert.HasCount(0, hk.Elements());
            Assert.AreEqual(0UL, hk.Count(Key("flow-3")));

            for (var i = 0; i < 30; i++)
            {
                hk.Add(Key("after-reset"));
            }
            Assert.AreEqual(30UL, hk.Count(Key("after-reset")));
        }

        /// <summary>
        /// While the heap has room, a flow is tracked from its very first arrival --
        /// there is nothing to compete with, so there is nothing to wait for. Kills
        /// the mutant that drops the heap-has-room clause and makes every flow wait
        /// for the organic nmin + 1 step, which is one arrival too late.
        /// </summary>
        [TestMethod]
        public void TestAFlowIsTrackedOnFirstSightWhileTheHeapHasRoom()
        {
            var hk = new HeavyKeeper(10, 1024, seed: 11);

            hk.Add(Key("first"));
            hk.Add(Key("second"));
            hk.Add(Key("third"));

            var reported = hk.Elements()
                .Select(e => Encoding.ASCII.GetString(e.Data.Span))
                .ToArray();
            Assert.HasCount(3, reported);
            Assert.Contains("first", reported);
            Assert.Contains("second", reported);
            Assert.Contains("third", reported);
        }

        /// <summary>
        /// The decay probability is b^-C, not merely "less likely as C grows". At
        /// b = 2 a count of one decays half the time and a count of three an eighth
        /// of the time, and 200 independently seeded trials of each are held to
        /// binomial three-sigma bands around those rates. The bands come from the
        /// formula, not from running the code, so an implementation that decays at
        /// some other rate -- always, never, or at the wrong exponent -- lands
        /// outside them.
        /// </summary>
        [TestMethod]
        public void TestDecayFollowsItsStatedProbability()
        {
            // One bucket, one array: the intruder's arrival is guaranteed to contest
            // the holder's bucket, so every trial is one Bernoulli draw at exactly
            // the counter value the trial built.
            var decaysAtOne = 0;
            var decaysAtThree = 0;
            for (ulong t = 0; t < 200; t++)
            {
                var atOne = new HeavyKeeper(2, 1, depth: 1, decay: 2.0, seed: t);
                atOne.Add(Key("holder"));
                atOne.Add(Key("intruder"));
                if (atOne.Counters[0][0] == 0
                    || atOne.Count(Key("intruder")) == 1)
                {
                    // Decayed from one to zero -- and possibly claimed by the
                    // intruder in the same arrival, which is the same decay.
                    decaysAtOne++;
                }

                var atThree = new HeavyKeeper(2, 1, depth: 1, decay: 2.0, seed: t + 1000);
                for (var i = 0; i < 3; i++)
                {
                    atThree.Add(Key("holder"));
                }
                atThree.Add(Key("intruder"));
                if (atThree.Counters[0][0] == 2)
                {
                    decaysAtThree++;
                }
            }

            // Binomial three-sigma bands: 200 trials at p = 1/2 give 100 +/- 21.2,
            // at p = 1/8 give 25 +/- 14.0.
            Assert.IsGreaterThanOrEqualTo(79, decaysAtOne,
                $"C=1 decayed {decaysAtOne}/200; b^-1 = 1/2 predicts about 100.");
            Assert.IsLessThanOrEqualTo(122, decaysAtOne,
                $"C=1 decayed {decaysAtOne}/200; b^-1 = 1/2 predicts about 100.");
            Assert.IsGreaterThanOrEqualTo(11, decaysAtThree,
                $"C=3 decayed {decaysAtThree}/200; b^-3 = 1/8 predicts about 25.");
            Assert.IsLessThanOrEqualTo(39, decaysAtThree,
                $"C=3 decayed {decaysAtThree}/200; b^-3 = 1/8 predicts about 25.");
        }

        /// <summary>
        /// A fingerprint collider does not ride the incumbent's count into the heap.
        /// Admission demands an estimate of exactly nmin + 1 -- Optimization I,
        /// justified by Theorem 1: without collisions, one arrival moves an estimate
        /// by at most one, so an estimate that arrives far above the heap minimum
        /// was stolen, not earned. The staging finds a genuine collider by scanning
        /// keys through the structure's own addressing.
        /// </summary>
        [TestMethod]
        public void TestAFingerprintColliderIsNotAdmittedOnAStolenCount()
        {
            var hk = new HeavyKeeper(2, 4, depth: 2, seed: 12);

            // Find two distinct keys with identical (bucket, fingerprint) addresses
            // in every array: a full collision, the exact case Optimization I exists
            // for. Four buckets and sixteen fingerprint bits put one pair in roughly
            // every million, so a few thousand keys make one nearly certain.
            string incumbent = null, collider = null;
            var seen = new Dictionary<string, string>();
            for (var i = 0; i < 20000 && collider is null; i++)
            {
                var key = $"candidate-{i}";
                var address = string.Join(";", hk.MappingOf(Key(key))
                    .Select(m => $"{m.Array}:{m.Bucket}:{m.Fingerprint}"));
                if (seen.TryGetValue(address, out var holder))
                {
                    incumbent = holder;
                    collider = key;
                }
                else
                {
                    seen[address] = key;
                }
            }
            Assert.IsNotNull(collider,
                "No colliding pair within 20,000 keys; the premise could not be " +
                "staged.");

            // The incumbent earns a large count; a filler fills the heap so that
            // admission is actually contested.
            for (var i = 0; i < 50; i++)
            {
                hk.Add(Key(incumbent));
            }
            for (var i = 0; i < 30; i++)
            {
                hk.Add(Key("filler"));
            }

            // One arrival by the collider. Its fingerprint matches the incumbent's
            // buckets, so its estimate reads the incumbent's count -- far beyond
            // nmin + 1 -- and Theorem 2's no-overestimation guarantee is exactly
            // what a fingerprint collision forfeits.
            hk.Add(Key(collider));

            var reported = hk.Elements()
                .Select(e => Encoding.ASCII.GetString(e.Data.Span))
                .ToArray();
            Assert.IsFalse(reported.Contains(collider),
                "A collider was admitted on a count it never earned.");
            Assert.Contains(incumbent, reported);
        }

        /// <summary>
        /// The estimate is the largest count among a flow's buckets, not the
        /// smallest: the paper chooses the maximum precisely because a flow's
        /// buckets erode unevenly -- whichever bucket suffered least contested
        /// competition is the closest to the truth. The staging arranges exactly
        /// that: a flow whose array-0 bucket is contested by a collider while its
        /// array-1 bucket is untouched, so only the maximum recovers the true count.
        /// </summary>
        [TestMethod]
        public void TestTheEstimateComesFromTheHealthiestBucket()
        {
            var hk = new HeavyKeeper(4, 64, depth: 2, seed: 13);

            // Find a collider sharing the flow's array-0 bucket -- with a different
            // fingerprint, so its arrivals decay rather than match -- while landing
            // elsewhere in array 1.
            var flow = "the-flow";
            var flowMap = hk.MappingOf(Key(flow)).ToArray();
            string collider = null;
            for (var i = 0; i < 20000 && collider is null; i++)
            {
                var m = hk.MappingOf(Key($"candidate-{i}")).ToArray();
                if (m[0].Bucket == flowMap[0].Bucket
                    && m[0].Fingerprint != flowMap[0].Fingerprint
                    && m[1].Bucket != flowMap[1].Bucket)
                {
                    collider = $"candidate-{i}";
                }
            }
            Assert.IsNotNull(collider, "No one-sided collider within 20,000 keys.");

            for (var i = 0; i < 10; i++)
            {
                hk.Add(Key(flow));
            }
            for (var i = 0; i < 10; i++)
            {
                hk.Add(Key(collider));
            }

            // Premise guards, through the internals: the contested bucket must have
            // actually eroded while still holding the flow's fingerprint, and the
            // clean bucket must be untouched -- otherwise both estimators would
            // agree and the test would discriminate nothing.
            var eroded = hk.Counters[0][flowMap[0].Bucket];
            Assert.AreEqual(flowMap[0].Fingerprint, hk.Fingerprints[0][flowMap[0].Bucket],
                "The collider took the bucket outright; the staging wants erosion, " +
                "not eviction -- adjust the arrival counts.");
            Assert.IsLessThan(10UL, eroded,
                "The contested bucket never eroded; nothing distinguishes max from min.");
            Assert.AreEqual(10UL, hk.Counters[1][flowMap[1].Bucket],
                "The clean bucket was touched; the premise is void.");

            Assert.AreEqual(10UL, hk.Count(Key(flow)),
                "The estimate should come from the untouched bucket.");
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// True when no two of the named flows map to the same bucket of the same
        /// array. The tests above state premises about collisions, and a premise
        /// should be checked, not hoped for -- through the same addressing the
        /// structure itself uses.
        /// </summary>
        private static bool NoBucketIsContested(HeavyKeeper hk, IEnumerable<string> keys)
        {
            var seen = new Dictionary<(uint Array, uint Bucket), string>();
            foreach (var key in keys)
            {
                foreach (var (array, bucket, _) in hk.MappingOf(Key(key)))
                {
                    if (seen.TryGetValue((array, bucket), out var holder) && holder != key)
                    {
                        return false;
                    }
                    seen[(array, bucket)] = key;
                }
            }
            return true;
        }

        /// <summary>
        /// True when no two of the named flows map to the same bucket of the same
        /// array with the same fingerprint, which is Theorem 2's precondition.
        /// </summary>
        private static bool NoFingerprintCollisionAmong(
            HeavyKeeper hk, IEnumerable<string> keys)
        {
            var seen = new Dictionary<(uint Array, uint Bucket, ushort Fp), string>();
            foreach (var key in keys)
            {
                foreach (var (array, bucket, fp) in hk.MappingOf(Key(key)))
                {
                    if (seen.TryGetValue((array, bucket, fp), out var holder)
                        && holder != key)
                    {
                        return false;
                    }
                    seen[(array, bucket, fp)] = key;
                }
            }
            return true;
        }

    }
}
