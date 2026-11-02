#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Long, keyed operation sequences over the sketches and samplers, held to exact
    /// sequence properties rather than to one scenario each (#144).
    /// </summary>
    /// <remarks>
    /// Most defects this library has shipped needed a particular sequence to appear: a
    /// theta sketch that wrote a payload its own reader refused after a particular run of
    /// adds, filters that did not resume their random sequence after a load, resets that
    /// left bookkeeping behind. The single-scenario tests elsewhere build a structure one
    /// way and ask it one kind of question. These run several thousand operations --
    /// adds, removes, queries, merges, resets -- drawn from a keyed stream, and hold the
    /// whole run to properties that are exact:
    /// <list type="bullet">
    /// <item>
    /// <b>Saving changes nothing.</b> A twin that is written out and read back at keyed
    /// points along the way, and carries on from what it read, must end byte-identical
    /// to a twin that was never written out, and must answer every query along the way
    /// the same. This is the property the restored random generators once broke, and it
    /// is stronger than a round trip: it checks every piece of live state that a reader
    /// reconstructs instead of reading -- lower bounds, buffer capacities, heap layouts,
    /// expansion limits -- against what the live structure actually holds, at every
    /// point the sequence reaches.
    /// </item>
    /// <item>
    /// <b>Reset is a fresh instance.</b> After a reset, the rest of the sequence applied
    /// to the reset structure must end byte-identical to the same operations applied to
    /// a newly built one. A reset that leaves something behind is invisible right after
    /// the reset and appears only in what the structure does next.
    /// </item>
    /// <item>
    /// <b>Merge is the union, in any order.</b> For the sketches whose merge is exact, a
    /// stream dealt across several shards -- each saved and restored along the way --
    /// and folded back together in a keyed order and tree shape must be byte-identical
    /// to the sketch of the whole stream. One comparison covers commutativity,
    /// associativity and the empty identity.
    /// </item>
    /// </list>
    /// <para>
    /// Every sequence is a pure function of its seed: operation <c>i</c> of sequence
    /// <c>s</c> is decided by hashing <c>"seq{s}-{stream}-{i}"</c>, as everywhere else in
    /// this suite. A failure names its seed and, by a cheap search over the failing
    /// sequence, the shortest prefix and the single checkpoint that still reproduce it.
    /// That is not a property-testing library's shrinking (TESTING.md declines one) --
    /// just enough to turn a red sweep into a repro.
    /// </para>
    /// <para>
    /// Parameters are chosen at the coarse end, so that the thresholds each structure
    /// has -- sparse to dense, buffer trims, expansions and contractions, the exact
    /// phase ending, substreams rolling over -- are crossed many times per sequence
    /// rather than once per suite.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestOperationSequences
    {
        // ---------------------------------------------------------------------------
        // The bounds of the sweep. Work, not wall clock: each is a count.
        // ---------------------------------------------------------------------------

        private const int Seeds = 20;
        private const int Length = 2000;

        private static readonly Func<ReadOnlySpan<byte>, ulong> Mix =
            Defaults.GetDefaultHashFunction();

        private static ulong Draw(int seq, string stream, int i) =>
            Mix(Encoding.UTF8.GetBytes($"seq{seq}-{stream}-{i}"));

        internal enum Kind { Add, Remove, Reset, Merge, Query }

        /// <summary>One operation. What Key and Value mean is up to each structure.</summary>
        internal readonly record struct Op(Kind Kind, int Key, double Value, int Index);

        // ---------------------------------------------------------------------------
        // The roster.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// The sweep is named for every sketch and sampler, so it is held to
        /// <see cref="StructureId"/> with each exemption's reason checked.
        /// </summary>
        [TestMethod]
        public void TestTheSequenceSweepReachesEveryStructure()
        {
            var library = typeof(BloomFilter).Assembly;

            // Built once from a whole input and never changed afterwards. Checked rather
            // than asserted: a type that grows an Add has an incremental path, and the
            // exemption has gone stale.
            var builtOnce = new[]
            {
                StructureId.BinaryFuseFilter, StructureId.BloomierFilter, StructureId.Grafite,
                StructureId.MinHashSignature, StructureId.SimHashSignature,
            };
            foreach (var id in builtOnce)
            {
                var type = library.GetType($"ProbabilisticDataStructures.{id}", throwOnError: true)!;
                Assert.IsFalse(
                    type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Any(m => m.Name is "Add" or "Remove" or "Merge" or "Reset"),
                    $"{id} is exempt as built-once, but it now has an incremental " +
                    "operation, so a sequence over it means something. Cover it.");
            }

            // The filters are the other files' slices of #144, which bring membership
            // models (no false negatives, fingerprint multisets) this harness does not
            // have. Which structures those are is what those files say they sequence,
            // not a list kept here: a filter they stop sequencing is then a gap in this
            // roster, and one this file also runs is a stale exemption, both by name.
            var filters = TestFilterSequences.SequencedStructures
                .Select(id => (Id: id, File: nameof(TestFilterSequences)))
                .Concat(TestQuotientFamilySequences.SequencedStructures
                    .Select(id => (Id: id, File: nameof(TestQuotientFamilySequences))))
                .GroupBy(f => f.Id)
                .Select(g => g.First())
                .ToArray();
            foreach (var (id, file) in filters)
            {
                var type = library.GetType($"ProbabilisticDataStructures.{id}", throwOnError: true)!;
                Assert.IsTrue(
                    type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Any(m => m.Name == "Test"),
                    $"{file} sequences {id}, which is exempt here as a filter, but it has no " +
                    "membership query to model, so it is a sketch and a sequence over it " +
                    "belongs in this sweep.");
            }

            StructureRoster.AssertCoversEveryStructure(
                "operation sequence",
                Specs.Select(s => s.Id).Distinct(),
                builtOnce.Select(id => (id, "built once from a whole input; no operation " +
                        "changes it afterwards, so there is no sequence to run"))
                    .Concat(filters.Select(f => (f.Id, $"a filter, sequenced by {f.File} " +
                        "against a membership model")))
                    .ToArray());
        }

        // ---------------------------------------------------------------------------
        // The public surface.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Operations by name, whatever their shape. The rest are found by signature, so
        /// an operation named otherwise still shows up: it returns the type, or it takes
        /// another instance of it.
        /// </summary>
        private static readonly string[] OperationNames =
            { "Add", "Remove", "Merge", "Reset", "Union", "Intersect", "Difference", "Subtract" };

        /// <summary>
        /// Methods that take another instance of the type and change neither, so the
        /// signature rule would take them for operations. Each is checked to be matched
        /// by it on some sequenced type, so a classification nobody needs goes stale.
        /// </summary>
        private static readonly (string Name, string Why)[] Comparisons =
        {
            ("Jaccard", "estimates how two sketches relate from their registers and changes neither"),
            ("Compare", "estimates how two sketches relate from their registers and changes neither"),
        };

        /// <summary>
        /// Operations a type offers that its specs do not sequence, each with the existing
        /// test that covers it as a single scenario. Sequencing Theta and Tuple
        /// <c>Intersect</c> and <c>Difference</c> is not cheap: the generator would need
        /// both as operations, the models would need to follow what each does to the
        /// events they read, and the tuple model would need the summaries' folds too. The
        /// scenarios named here cover what they do.
        /// </summary>
        private static readonly (Type Structure, string Operation, Type TestClass, string Test)[] CoveredElsewhere =
        {
            (typeof(ThetaSketch), nameof(ThetaSketch.Intersect),
                typeof(TestModelBased), nameof(TestModelBased.TestThetaSketchValuesAreExactlyTheHashesBelowTheta)),
            (typeof(ThetaSketch), nameof(ThetaSketch.Difference),
                typeof(TestModelBased), nameof(TestModelBased.TestThetaSketchValuesAreExactlyTheHashesBelowTheta)),
            (typeof(TupleSketch), nameof(TupleSketch.Intersect),
                typeof(TestTupleSketch), nameof(TestTupleSketch.TestSetOperationsFoldTheSummariesTheyShould)),
            (typeof(TupleSketch), nameof(TupleSketch.Difference),
                typeof(TestTupleSketch), nameof(TestTupleSketch.TestSetOperationsFoldTheSummariesTheyShould)),
        };

        private static bool IsOperation(MethodInfo method, Type type) =>
            OperationNames.Contains(method.Name)
            || method.ReturnType == type
            || method.GetParameters().Any(p => p.ParameterType == type);

        /// <summary>
        /// A spec runs what its type offers, as the Bloom file's check does for its
        /// filters. The operations are derived from the public surface by reflection, so
        /// a sketch that gains one fails here until a spec sequences it or a test that
        /// covers it as a single scenario is named, and a spec cannot quietly stop
        /// running one it still has.
        /// </summary>
        [TestMethod]
        public void TestEverySpecRunsWhatItsTypeOffers()
        {
            var comparisonsMatched = new HashSet<string>(StringComparer.Ordinal);

            foreach (var group in Specs.GroupBy(s => s.Structure))
            {
                var type = group.Key;
                var runs = group.SelectMany(s => s.Runs).ToHashSet(StringComparer.Ordinal);
                var methods = type
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => !m.IsSpecialName && IsOperation(m, type))
                    .Select(m => m.Name)
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var (name, _) in Comparisons.Where(c => methods.Contains(c.Name)))
                {
                    comparisonsMatched.Add(name);
                }
                var offered = methods.Except(Comparisons.Select(c => c.Name)).ToArray();

                var covered = CoveredElsewhere.Where(c => c.Structure == type).ToArray();

                var stale = runs.Except(offered).ToArray();
                Assert.IsEmpty(stale,
                    $"{type.Name}'s specs run {string.Join(", ", stale)}, which it does not " +
                    "offer; the spec is out of date.");

                var unaccounted = offered.Except(runs).Except(covered.Select(c => c.Operation)).ToArray();
                Assert.IsEmpty(unaccounted,
                    $"{type.Name} offers {string.Join(", ", unaccounted)}, which none of its " +
                    "specs sequences and no single-scenario test is named as covering. " +
                    "Sequence it, or name the test that covers it in CoveredElsewhere.");

                foreach (var (_, operation, testClass, test) in covered)
                {
                    Assert.Contains(operation, offered,
                        $"{type.Name}.{operation} is covered elsewhere, but {type.Name} no " +
                        "longer offers it; the entry is out of date.");
                    Assert.DoesNotContain(operation, runs,
                        $"{type.Name}.{operation} is covered elsewhere and sequenced anyway; " +
                        "the entry is out of date.");

                    // A named test that has lost its attribute vouches for nothing.
                    var method = testClass.GetMethod(test);
                    Assert.IsNotNull(method,
                        $"{type.Name}.{operation} names {testClass.Name}.{test}, which does not exist.");
                    Assert.IsNotNull(method.GetCustomAttribute<TestMethodAttribute>(),
                        $"{type.Name}.{operation} names {testClass.Name}.{test}, which is not a test.");
                }
            }

            // Every entry names a type that is sequenced, or it was never reachable.
            var sequenced = Specs.Select(s => s.Structure).ToHashSet();
            var orphaned = CoveredElsewhere.Where(c => !sequenced.Contains(c.Structure))
                .Select(c => $"{c.Structure.Name}.{c.Operation}").ToArray();
            Assert.IsEmpty(orphaned,
                $"{string.Join(", ", orphaned)} is covered elsewhere, but no spec sequences " +
                "its type; the entry is out of date.");

            var unusedComparisons = Comparisons.Select(c => c.Name).Except(comparisonsMatched).ToArray();
            Assert.IsEmpty(unusedComparisons,
                $"{string.Join(", ", unusedComparisons)} is classified as a comparison, but " +
                "no sequenced type offers a method that the signature rule would take for an " +
                "operation by that name; the classification is out of date.");
        }

        // ---------------------------------------------------------------------------
        // The properties.
        // ---------------------------------------------------------------------------

        [TestMethod]
        public void TestSavingMidSequenceChangesNothing()
        {
            AssertProperty(spec => spec.CheckSavingChangesNothing);

            var vacuous = Specs.Select(s => (s.Label, Why: s.CheckThresholdsCrossed()))
                .Where(v => v.Why is not null).Select(v => $"{v.Label}: {v.Why}").ToArray();
            Assert.IsEmpty(vacuous,
                "these configurations never crossed the thresholds the sweep claims to " +
                "save across:\n  " + string.Join("\n  ", vacuous));
        }

        [TestMethod]
        public void TestResetLeavesAFreshInstance()
        {
            AssertProperty(spec => spec.CheckResetIsFresh, s => s.HasReset);
        }

        [TestMethod]
        public void TestRemovingEverythingLeavesAFreshInstance()
        {
            AssertProperty(spec => spec.CheckUnwindIsFresh, s => s.HasUndo);
        }

        [TestMethod]
        public void TestMergingShardsInAnyOrderIsTheSketchOfTheWholeStream()
        {
            AssertProperty(spec => spec.CheckShardedMerge, s => s.MergeIsUnion);
        }

        [TestMethod]
        public void TestSequencesAgreeWithTheirExactModels()
        {
            foreach (var (_, tally) in IbltTallies) { tally.Decodes = 0; tally.Cancelled = 0; }
            AssertProperty(spec => spec.CheckModel, s => s.HasModel);

            // A table's model is only checked when it decodes, so each table has to decode,
            // and decode something, often enough to mean anything. And a decode that
            // refuses is only evidence about the success check if there was something for
            // the check to see through, so the small tables must have shown that too.
            foreach (var (label, tally) in IbltTallies)
            {
                Console.WriteLine($"{label}: non-empty decodes checked {tally.Decodes}, " +
                    $"refused with only cancelled counts left {tally.Cancelled}");
                Assert.IsGreaterThanOrEqualTo(tally.MinDecodes, tally.Decodes,
                    $"{label} decoded a non-empty difference too rarely for its model to " +
                    "have been tested");
                Assert.IsGreaterThanOrEqualTo(tally.MinCancelled, tally.Cancelled,
                    $"{label} too rarely refused a table whose counts had all cancelled, " +
                    "so its success check was not tested against the case it exists for");
            }
        }

        /// <summary>
        /// Small scope, exhaustively: for every count of distinct items up to past each
        /// sparse-to-dense threshold, re-adding any one of them, or merging in a copy,
        /// must leave a cardinality sketch's bytes alone. Thresholds are single points,
        /// which random sequences step over rather than onto.
        /// </summary>
        [TestMethod]
        public void TestRepeatingAnItemChangesNoCardinalitySketch()
        {
            AssertProperty(spec => seq => seq == 0 ? spec.CheckDuplicatesAreInvisible() : null,
                s => s.DuplicateBlind);
        }

        private static void AssertProperty(
            Func<ISpec, Func<int, string?>> check, Func<ISpec, bool>? applies = null)
        {
            var failures = new List<string>();
            var ran = 0;
            foreach (var spec in Specs.Where(s => applies is null || applies(s)))
            {
                ran++;
                for (var seq = 0; seq < Seeds; seq++)
                {
                    var failure = check(spec)(seq);
                    if (failure is null) continue;
                    failures.Add($"{spec.Label}, seq {seq}: {failure}");
                    break;
                }
            }

            Assert.IsGreaterThan(0, ran, "the property applies to no structure at all");
            Assert.IsEmpty(failures,
                $"{failures.Count} of {ran} configurations failed:\n  " +
                string.Join("\n  ", failures));
        }

        // ---------------------------------------------------------------------------
        // The structures. One or more configurations each, at coarse parameters.
        // ---------------------------------------------------------------------------

        private static byte[] K(int key) => Encoding.UTF8.GetBytes($"key-{key}");

        /// <summary>
        /// The generator state a structure with a seeded generator holds, read from the
        /// payload layout FORMAT.md freezes. Reset deliberately does not rewind the
        /// generator (see HeavyKeeper.Reset), so the fresh instance a reset is compared
        /// with has to start where the generator stands.
        /// </summary>
        private static ulong GeneratorState(byte[] frame, int payloadOffset) =>
            BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(14 + payloadOffset));

        /// <summary>Successful, non-trivial decodes checked, per table: the model's vacuity guard.</summary>
        private sealed class Tally(int minDecodes, int minCancelled)
        {
            public int Decodes;

            /// <summary>
            /// Refusals whose leftovers had cancelled to zero counts in every cell: the
            /// shape in which a success check that reads only the counts is wrong.
            /// </summary>
            public int Cancelled;

            // Floors, probed and then set well under what the keyed sequences reach.
            public int MinDecodes { get; } = minDecodes;
            public int MinCancelled { get; } = minCancelled;
        }

        // Declared before Specs, which fills it as it builds.
        private static readonly List<(string Label, Tally Tally)> IbltTallies = new();

        internal static readonly ISpec[] Specs = BuildSpecs().ToArray();

        private static IEnumerable<ISpec> BuildSpecs()
        {
            foreach (var m in new uint[] { 16, 64 })
            {
                yield return new Spec<HyperLogLog>
                {
                    Id = StructureId.HyperLogLog, Label = $"HyperLogLog(m={m})",
                    Fresh = () => new HyperLogLog(m),
                    Add = (s, op) => s.Add(K(op.Key)),
                    Query = (s, op) => s.Count().ToString(),
                    Reset = s => s.Reset(),
                    Merge = (a, b) => { a.Merge(b); return a; },
                    MergeIsUnion = true, MergeIsIdempotent = true, DuplicateBlind = true,
                };
            }

            // Both precisions convert to dense, and reset back to sparse, many times a
            // sequence. Where the conversion falls is the estimator's to decide -- the
            // paper's rule, floored at the hashes it always holds first at the smallest
            // precisions -- so nothing here restates it: the sweeps below find the
            // conversion by watching Phase, and a guard fails if they never see one.
            foreach (var p in new uint[] { 4, 8 })
            {
                yield return new Spec<HyperLogLogPlus>
                {
                    Id = StructureId.HyperLogLogPlus, Label = $"HyperLogLogPlus(p={p})",
                    Fresh = () => new HyperLogLogPlus(p),
                    Add = (s, op) => s.Add(K(op.Key)),
                    Query = (s, op) => s.Count().ToString(),
                    Reset = s => s.Reset(),
                    Merge = (a, b) => a.Merge(b),
                    MergeIsUnion = true, MergeIsIdempotent = true, DuplicateBlind = true,
                    Universe = 60,
                    Phase = s => s.IsSparse ? "sparse" : "dense",
                    MinPhases = 2,
                };
            }

            foreach (var p in new uint[] { 3, 6 })
            {
                yield return new Spec<UltraLogLog>
                {
                    Id = StructureId.UltraLogLog, Label = $"UltraLogLog(p={p})",
                    Fresh = () => new UltraLogLog(p),
                    Add = (s, op) => s.Add(K(op.Key)),
                    Query = (s, op) => s.Count().ToString(),
                    Reset = s => s.Reset(),
                    Merge = (a, b) => a.Merge(b),
                    MergeIsUnion = true, MergeIsIdempotent = true, DuplicateBlind = true,
                };
            }

            foreach (var variant in new[] { SetSketchVariant.SetSketch1, SetSketchVariant.SetSketch2 })
            {
                yield return new Spec<SetSketch>
                {
                    Id = StructureId.SetSketch, Label = $"SetSketch({variant}, m=16, b=1.2)",
                    Fresh = () => new SetSketch(16, 1.2, 4, 62, variant: variant),
                    Add = (s, op) => s.Add(K(op.Key)),
                    Query = (s, op) => s.Cardinality().ToString("R"),
                    Reset = s => s.Reset(),
                    Merge = (a, b) => { a.Merge(b); return a; },
                    MergeIsUnion = true, MergeIsIdempotent = true, DuplicateBlind = true,
                };
            }

            foreach (var e in new[] { 0.5, 0.05 })
            {
                yield return new Spec<CountMinSketch>
                {
                    Id = StructureId.CountMinSketch, Label = $"CountMinSketch(eps={e})",
                    Fresh = () => new CountMinSketch(e, e),
                    Add = (s, op) => s.Add(K(op.Key)),
                    Query = (s, op) => s.Count(K(op.Key)).ToString(),
                    Reset = s => s.Reset(),
                    Merge = (a, b) => { a.Merge(b); return a; },
                    MergeIsUnion = true,
                    Model = (s, events) => NeverUndercounts(events, k => s.Count(K(k))),
                };
            }

            // Signed updates, removals included: the table is linear in its input.
            yield return new Spec<CountSketch>
            {
                Id = StructureId.CountSketch, Label = "CountSketch(0.5, 0.5)",
                Fresh = () => new CountSketch(0.5, 0.5),
                Add = (s, op) => s.Add(K(op.Key), CountSketchDelta(op)),
                Remove = (s, op) => s.Add(K(op.Key), -1),
                RemoveVia = nameof(CountSketch.Add),
                Query = (s, op) => s.Count(K(op.Key)).ToString(),
                Merge = (a, b) => a.Merge(b),
                MergeIsUnion = true,
                Delta = op => op.Kind == Kind.Remove ? -1 : CountSketchDelta(op),
                Undo = (s, key, net) => s.Add(K(key), -net),
            };

            yield return new Spec<PrivateCountMinSketch>
            {
                Id = StructureId.PrivateCountMinSketch, Label = "PrivateCountMinSketch(8x2)",
                Fresh = () => new PrivateCountMinSketch(8, 2, 0.5, seed: 11),
                Add = (s, op) => s.Add(K(op.Key)),
                Query = (s, op) => s.Count(K(op.Key)).ToString("R"),
            };

            // One counter wide, and a growth exponent of 0.9, so the first doubling comes
            // at 108 keys and the next at 234, 508 and 1101 -- where the default exponent
            // would put the first at 68 squared, beyond any sequence here. Removals come
            // in phases, light then heavy, so the stream climbs through doublings and
            // falls back through foldings within one sequence.
            yield return new Spec<SublimeCountMinSketch>
            {
                Id = StructureId.SublimeCountMinSketch, Label = "SublimeCountMinSketch(1 wide, g=0.9)",
                Fresh = () => new SublimeCountMinSketch(0.5, 0.9, ValeCounterArray.DefaultCountersPerChunk),
                Add = (s, op) => s.Add(K(op.Key)),
                Remove = (s, op) => s.Remove(K(op.Key)),
                Query = (s, op) => s.Count(K(op.Key)).ToString(),
                Reset = s => s.Reset(),
                RemoveRateAt = i => (i / 400) % 2 == 0 ? 150 : 800,
                // Measured: widths 1, 2 and 4 are reached (resets cut the climb short of
                // 508). The first version of this configuration used the default
                // exponent, never doubled at all, and passed every property vacuously.
                Phase = s => $"width {s.Width}",
                MinPhases = 3,
                Model = (s, events) => NeverUndercounts(events, k => s.Count(K(k))),
                // Expansion copies each row onto itself and keeps a record of what it
                // copied, so that folding back can subtract it out: taking everything
                // out has to leave no trace of the doublings it went through.
                Undo = (s, key, net) =>
                {
                    for (var i = 0; i < net; i++) s.Remove(K(key));
                    return s;
                },
            };

            foreach (var alpha in new[] { 0.99, 0.05 })
            {
                yield return new Spec<DDSketch>
                {
                    Id = StructureId.DDSketch, Label = $"DDSketch(alpha={alpha})",
                    Fresh = () => new DDSketch(alpha),
                    Add = (s, op) => { s.Add(SignedValue(op)); return s; },
                    Query = (s, op) => s.Count() == 0 ? "empty" : s.Quantile((op.Key % 11) / 10.0).ToString("R"),
                    Merge = (a, b) => a.Merge(b),
                    MergeIsUnion = true,
                    Phase = s => $"{s.BucketsAllocated()} buckets",
                    MinPhases = 2,
                };
            }

            yield return new Spec<HeavyKeeper>
            {
                Id = StructureId.HeavyKeeper, Label = "HeavyKeeper(k=4, 16x2)",
                Fresh = () => new HeavyKeeper(4, 16, 2, 1.08, seed: 5),
                Add = (s, op) => s.Add(K(op.Key)),
                Query = (s, op) => s.Count(K(op.Key)) + "|" + Describe(s.Elements()),
                Reset = s => s.Reset(),
                FreshAfterReset = s => new HeavyKeeper(4, 16, 2, 1.08,
                    seed: GeneratorState(s.ToByteArray(), payloadOffset: 20)),
                Phase = s => s.Elements().Length < 4 ? "heap filling" : "heap full",
                MinPhases = 2,
            };

            yield return new Spec<TopK>
            {
                Id = StructureId.TopK, Label = "TopK(k=4, eps=0.1)",
                Fresh = () => new TopK(0.1, 0.1, 4),
                Add = (s, op) => s.Add(K(op.Key)),
                Query = (s, op) => Describe(s.Elements()),
                Reset = s => s.Reset(),
                Merge = (a, b) => a.Merge(b),
                Phase = s => s.Elements().Length < 4 ? "heap filling" : "heap full",
                MinPhases = 2,
            };

            // A short window over small sketches, so substreams roll over constantly.
            // Its noise generator is not written (DpswSketch.WriteTo), so a window read
            // back draws different noise for every substream it starts afterwards: two
            // twins cannot agree on noisy counts, only on the layout the noise sits in.
            yield return new Spec<DpswSketch>
            {
                Id = StructureId.DpswSketch, Label = "DpswSketch(window=64, 8x2)",
                Fresh = () => new DpswSketch(window: 64, rho: 1.0, alpha: 0.5, width: 8, depth: 2, seed: 3),
                Add = (s, op) => s.Add(K(op.Key)),
                Query = (s, op) => $"{s.Position}|{s.SketchesHeld}",
                Canon = s => Encoding.UTF8.GetBytes(
                    $"len={s.ToByteArray().Length} pos={s.Position} held={s.SketchesHeld}"),
                QueryIsNoisy = true,
                Phase = s => $"{s.SketchesHeld} sketches",
                MinPhases = 2,
            };

            // A k small enough that the exact phase ends and restarts (with reset) many
            // times. The configurations reach different state: with unit weights every
            // item ties, so the exact phase's arrival order can never matter and the saves
            // cross the phases cleanly; with weights from a coarse grid ties occur, so the
            // order the exact phase is held in decides which items are sampled later, and
            // merges, which bring in threshold weights, are possible.
            yield return new Spec<VarOpt>
            {
                Id = StructureId.VarOpt, Label = "VarOpt(k=8)",
                Fresh = () => new VarOpt(8, seed: 7),
                Add = (s, op) => s.Add(K(op.Key)),
                Query = (s, op) => s.SampleCount + "|" + s.TotalWeight.ToString("R"),
                Reset = s => s.Reset(),
                FreshAfterReset = s => new VarOpt(8, seed: GeneratorState(s.ToByteArray(), payloadOffset: 4)),
                Phase = s => s.LightCount == 0 ? "exact" : "sampling",
                MinPhases = 2,
            };
            yield return new Spec<VarOpt>
            {
                Id = StructureId.VarOpt, Label = "VarOpt(k=8, mixed weights)",
                Fresh = () => new VarOpt(8, seed: 7),
                Add = (s, op) => s.Add(K(op.Key), op.Value),
                Query = (s, op) => s.SampleCount + "|" + s.TotalWeight.ToString("R"),
                Reset = s => s.Reset(),
                FreshAfterReset = s => new VarOpt(8, seed: GeneratorState(s.ToByteArray(), payloadOffset: 4)),
                Merge = (a, b) => a.Merge(b),
                Phase = s => s.LightCount == 0 ? "exact" : "sampling",
                MinPhases = 2,
            };

            foreach (var k in new uint[] { 1, 4, 16 })
            {
                yield return new Spec<ThetaSketch>
                {
                    Id = StructureId.ThetaSketch, Label = $"ThetaSketch(k={k})",
                    Fresh = () => new ThetaSketch(k),
                    Add = (s, op) => s.Add(K(op.Key)),
                    Query = (s, op) => s.Count().ToString(),
                    Merge = (a, b) => a.Union(b),
                    MergeVia = nameof(ThetaSketch.Union),
                    Model = ThetaModel,
                    Phase = s => s.ThetaValue == ulong.MaxValue ? "exact" : "sampling",
                    MinPhases = 2,
                };
            }

            foreach (var (k, policy) in new[] { (4u, SummaryPolicy.Sum), (16u, SummaryPolicy.Max) })
            {
                yield return new Spec<TupleSketch>
                {
                    Id = StructureId.TupleSketch, Label = $"TupleSketch(k={k}, {policy})",
                    Fresh = () => new TupleSketch(k, policy),
                    Add = (s, op) => s.Add(K(op.Key), op.Value),
                    Query = (s, op) => s.Count() + "|" + s.Total().ToString("R"),
                    Merge = (a, b) => a.Union(b),
                    MergeVia = nameof(TupleSketch.Union),
                    Model = (s, events) => TupleModel(s, events, policy),
                    Phase = s => s.ThetaValue == ulong.MaxValue ? "exact" : "sampling",
                    MinPhases = 2,
                };
            }

            // Keys of exactly the table's key width. The table is linear, so the sum of
            // two tables -- a minus (empty minus b) -- is the table of both streams.
            //
            // Several sizes of table. The one the constructor gives for a difference of 8
            // is the table as shipped, but it is roomy enough that keys on opposite sides
            // seldom cancel in every cell, and cancelling is what a decode's success check
            // exists to see through. So smaller tables, which the sizing rule would not
            // choose, hold the same promises at the coarse end: the cells an earlier
            // version gave for the same difference, and the fewest there can be, where
            // every key fills every cell whatever its hash.
            //
            // Only the smallest sees the cancelled shape often enough to rely on; the
            // others are held to decoding a good many non-empty differences.
            var tables = new (string Label, Func<InvertibleBloomLookupTable> Fresh, int MinDecodes, int MinCancelled)[]
            {
                ("InvertibleBloomLookupTable(d=8)",
                    () => new InvertibleBloomLookupTable(8, 8), 100, 0),
                ("InvertibleBloomLookupTable(12 cells)",
                    () => InvertibleBloomLookupTable.WithCells(12, 8), 100, 0),
                ("InvertibleBloomLookupTable(4 cells)",
                    () => InvertibleBloomLookupTable.WithCells(4, 8), 10, 8),
            };
            foreach (var (label, fresh, minDecodes, minCancelled) in tables)
            {
                var tally = new Tally(minDecodes, minCancelled);
                IbltTallies.Add((label, tally));
                yield return new Spec<InvertibleBloomLookupTable>
                {
                    Id = StructureId.InvertibleBloomLookupTable, Label = label,
                    Fresh = fresh,
                    KeyBytes = key => Encoding.ASCII.GetBytes($"k{key:D7}"),
                    Add = (s, op) => s.Add(Encoding.ASCII.GetBytes($"k{op.Key:D7}")),
                    Remove = (s, op) => s.Remove(Encoding.ASCII.GetBytes($"k{op.Key:D7}")),
                    RemovesAbsentKeys = true,
                    Query = (s, op) => s.TryDecode(out var a, out var r)
                        ? $"{a.Count}+{r.Count}" : "undecodable",
                    Merge = (a, b) => a.Subtract(fresh().Subtract(b)),
                    MergeVia = nameof(InvertibleBloomLookupTable.Subtract),
                    MergeIsUnion = true,
                    Model = (s, events) => IbltModel(s, events, tally),
                    Undo = (s, key, net) =>
                    {
                        var bytes = Encoding.ASCII.GetBytes($"k{key:D7}");
                        for (var i = 0; i < net; i++) s.Remove(bytes);
                        for (var i = 0; i < -net; i++) s.Add(bytes);
                        return s;
                    },
                    // Distinct keys, removals as frequent as adds, and small merges: the
                    // difference wanders around the table's capacity, decoding and failing
                    // to in turn, with keys on both sides of it.
                    RemoveRate = 520,
                    Phase = s => s.TryDecode(out _, out _) ? "decodable" : "undecodable",
                    MinPhases = 2,
                    Universe = 100_000,
                    HotKeys = 0,
                    SideMax = 3,
                };
            }
        }

        private static long CountSketchDelta(Op op) => (long)(op.Value * 4) - 20;

        private static string Describe(Element[] elements) =>
            string.Join(",", elements.Select(e => $"{Encoding.UTF8.GetString(e.Data.Span)}:{e.Freq}"));

        /// <summary>
        /// A value spread across signs and twenty-odd binary orders of magnitude, with
        /// zeros, so every DDSketch store and the zero count all move.
        /// </summary>
        private static double SignedValue(Op op)
        {
            var bits = (ulong)op.Key * 0x9E3779B97F4A7C15UL ^ BitConverter.DoubleToUInt64Bits(op.Value);
            return (bits % 7) switch
            {
                0 => 0.0,
                1 or 2 => -Math.Pow(2, (int)((bits >> 8) % 24) - 12) * (1 + op.Value / 32),
                _ => Math.Pow(2, (int)((bits >> 8) % 24) - 12) * (1 + op.Value / 32),
            };
        }

        // ---------------------------------------------------------------------------
        // Exact models, for the structures whose state is not byte-comparable across
        // histories but is still exactly characterized.
        // ---------------------------------------------------------------------------

        /// <summary>An item-level event the model sees: what actually reached the structure.</summary>
        internal readonly record struct Event(Kind Kind, byte[] Key, double Value);

        /// <summary>
        /// Whatever theta the sketch landed on, it holds exactly the input hashes below
        /// it (TestModelBased's characterization, held here at every checkpoint of a long
        /// sequence of adds, unions and round trips).
        /// </summary>
        private static string? ThetaModel(ThetaSketch sketch, IReadOnlyList<Event> events)
        {
            var hash = Defaults.GetDefaultHashFunction();
            var theta = sketch.ThetaValue;
            var expected = events.Select(e => hash(e.Key)).Where(h => h < theta)
                .Distinct().OrderBy(h => h).ToArray();
            var held = sketch.ValuesHeld;
            return expected.SequenceEqual(held) ? null
                : $"holds {held.Length} values where exactly {expected.Length} input hashes " +
                  $"lie below its theta {theta}";
        }

        /// <summary>
        /// The tuple sketch's keys are exactly the input hashes below theta, and -- the
        /// part theta has no counterpart for -- each key's summary is the fold of every
        /// value that key was added with. A key below the final theta was below every
        /// theta before it, so none of its values was ever discarded.
        /// </summary>
        private static string? TupleModel(
            TupleSketch sketch, IReadOnlyList<Event> events, SummaryPolicy policy)
        {
            var hash = Defaults.GetDefaultHashFunction();
            var theta = sketch.ThetaValue;
            var folded = new SortedDictionary<ulong, double>();
            foreach (var e in events)
            {
                var h = hash(e.Key);
                if (h >= theta) continue;
                folded[h] = !folded.TryGetValue(h, out var had) ? e.Value : policy switch
                {
                    SummaryPolicy.Min => Math.Min(had, e.Value),
                    SummaryPolicy.Max => Math.Max(had, e.Value),
                    _ => had + e.Value,
                };
            }

            var keys = sketch.KeysHeld;
            var summaries = sketch.SummariesHeld;
            if (!folded.Keys.SequenceEqual(keys))
            {
                return $"holds {keys.Length} keys where exactly {folded.Count} input " +
                       $"hashes lie below its theta {theta}";
            }
            var i = 0;
            foreach (var (h, want) in folded)
            {
                if (summaries[i] != want)
                {
                    return $"key {h} summarizes to {summaries[i]} where its values fold to {want}";
                }
                i++;
            }
            return null;
        }

        /// <summary>
        /// A Count-Min estimate is a minimum over counters that every occurrence of the
        /// key incremented, so -- whatever else collided into them -- it never falls
        /// below the key's true count. Not probabilistic: one undercount is a defect.
        /// </summary>
        private static string? NeverUndercounts(IReadOnlyList<Event> events, Func<int, ulong> count)
        {
            var truth = new Dictionary<string, long>();
            foreach (var e in events)
            {
                var k = Encoding.UTF8.GetString(e.Key);
                truth[k] = truth.GetValueOrDefault(k) + (e.Kind == Kind.Remove ? -1 : 1);
            }
            foreach (var (k, want) in truth)
            {
                var got = count(int.Parse(k.AsSpan(4)));
                if ((long)got < want)
                {
                    return $"{k} is held {want} times and counts {got}";
                }
            }
            return null;
        }

        /// <summary>
        /// When the table says it decoded, the listing is exactly the net difference:
        /// every key added more than removed, in added; every key removed more than
        /// added, in removed. Decoding may fail -- that is the structure being honest
        /// -- but it may not succeed with the wrong answer.
        /// </summary>
        private static string? IbltModel(
            InvertibleBloomLookupTable table, IReadOnlyList<Event> events, Tally tally)
        {
            if (!table.TryDecode(out var added, out var removed))
            {
                // Refusing is honest. What matters is whether the refusal had something to
                // see through: the table with the keys peeling did list taken back out is
                // what a success check is shown, and if its counts are all zero while keys
                // remain, a check reading only the counts would call that a success.
                var rest = Persistence.FromByteArray<InvertibleBloomLookupTable>(table.ToByteArray());
                foreach (var key in added) rest.Remove(key);
                foreach (var key in removed) rest.Add(key);
                if (CountsAllZero(rest)) tally.Cancelled++;
                return null;
            }

            var net = new Dictionary<string, int>();
            foreach (var e in events)
            {
                var k = Encoding.ASCII.GetString(e.Key);
                net[k] = net.GetValueOrDefault(k) + (e.Kind == Kind.Remove ? -1 : 1);
            }

            var wantAdded = net.Where(kv => kv.Value > 0).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal);
            var wantRemoved = net.Where(kv => kv.Value < 0).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal);
            var gotAdded = added.Select(Encoding.ASCII.GetString).OrderBy(k => k, StringComparer.Ordinal);
            var gotRemoved = removed.Select(Encoding.ASCII.GetString).OrderBy(k => k, StringComparer.Ordinal);

            if (net.Values.Any(v => v > 1 || v < -1))
            {
                return "decoded a difference holding a key more than once, which no pure cell can list";
            }
            if (!wantAdded.SequenceEqual(gotAdded) || !wantRemoved.SequenceEqual(gotRemoved))
            {
                // Name the mechanism when it is the one #146 was: the keys left unlisted,
                // put in a table of their own, cancel to zero counts in every cell -- so a
                // success check that reads only the counts calls the peel complete.
                var unlistedAdded = wantAdded.Except(gotAdded).ToArray();
                var unlistedRemoved = wantRemoved.Except(gotRemoved).ToArray();
                var invented = gotAdded.Except(wantAdded).Concat(gotRemoved.Except(wantRemoved)).Any();
                var residue = InvertibleBloomLookupTable.WithCells(table.CellCount, 8);
                foreach (var k in unlistedAdded) residue.Add(Encoding.ASCII.GetBytes(k));
                foreach (var k in unlistedRemoved) residue.Remove(Encoding.ASCII.GetBytes(k));
                var cancels = !invented && residue.TryDecode(out var a2, out var r2) && a2.Count + r2.Count == 0;
                return $"decoded {added.Count} added and {removed.Count} removed where the net " +
                       $"difference is {wantAdded.Count()} added and {wantRemoved.Count()} removed" +
                       (cancels ? "; the unlisted keys cancel to zero counts in every cell" : "");
            }
            if (added.Count + removed.Count > 0) tally.Decodes++;
            return null;
        }

        /// <summary>
        /// Whether every cell's count is zero, read from the payload layout FORMAT.md
        /// freezes: after the envelope, the cell count, the key size, and the counts as a
        /// length and then a signed 64-bit value per cell.
        /// </summary>
        private static bool CountsAllZero(InvertibleBloomLookupTable table)
        {
            const int CountsOffset = 14 + 4 + 4 + 4;
            var bytes = table.ToByteArray();
            for (var cell = 0; cell < table.CellCount; cell++)
            {
                if (BinaryPrimitives.ReadInt64LittleEndian(
                        bytes.AsSpan(CountsOffset + cell * sizeof(long))) != 0)
                {
                    return false;
                }
            }
            return true;
        }

        // ---------------------------------------------------------------------------
        // The engine.
        // ---------------------------------------------------------------------------

        internal interface ISpec
        {
            StructureId Id { get; }
            Type Structure { get; }

            /// <summary>The public operations this configuration's delegates run.</summary>
            IReadOnlyCollection<string> Runs { get; }
            string Label { get; }
            bool HasReset { get; }
            bool MergeIsUnion { get; }
            bool HasModel { get; }
            bool HasUndo { get; }
            bool DuplicateBlind { get; }
            string? CheckSavingChangesNothing(int seq);
            string? CheckUnwindIsFresh(int seq);
            string? CheckDuplicatesAreInvisible();
            string? CheckThresholdsCrossed();
            string? CheckResetIsFresh(int seq);
            string? CheckShardedMerge(int seq);
            string? CheckModel(int seq);
        }

        private sealed class Spec<T> : ISpec where T : class, IBinaryPersistable<T>
        {
            public required StructureId Id { get; init; }
            public required string Label { get; init; }
            public required Func<T> Fresh { get; init; }
            public required Func<T, Op, T> Add { get; init; }
            public Func<T, Op, T>? Remove { get; init; }

            /// <summary>
            /// Whether a removal with nothing live removes a key never added, rather than
            /// turning into an add. Only for a structure that reports negative differences.
            /// </summary>
            public bool RemovesAbsentKeys { get; init; }
            public required Func<T, Op, string> Query { get; init; }
            public Func<T, T>? Reset { get; init; }
            public Func<T, T>? FreshAfterReset { get; init; }
            public Func<T, T, T>? Merge { get; init; }
            public bool MergeIsUnion { get; init; }

            /// <summary>
            /// The public method the Merge delegate calls, where it is not named Merge:
            /// a theta or tuple union, an IBLT's subtraction. Declared beside the
            /// delegate so that the surface check can tell which operation it sequences.
            /// </summary>
            public string MergeVia { get; init; } = "Merge";

            /// <summary>
            /// The public method the Remove delegate calls, where it is not named Remove:
            /// a Count Sketch removes by adding a negative count.
            /// </summary>
            public string RemoveVia { get; init; } = "Remove";
            public bool MergeIsIdempotent { get; init; }
            public Func<T, IReadOnlyList<Event>, string?>? Model { get; init; }
            public Func<T, byte[]> Canon { get; init; } = s => s.ToByteArray();
            public bool QueryIsNoisy { get; init; }
            public bool DuplicateBlind { get; init; }

            /// <summary>How far an operation moves its key's net count.</summary>
            public Func<Op, long> Delta { get; init; } = op => op.Kind == Kind.Remove ? -1 : 1;

            /// <summary>Takes a key's net count back out, where the structure can.</summary>
            public Func<T, int, long, T>? Undo { get; init; }
            public Func<int, byte[]> KeyBytes { get; init; } = K;

            /// <summary>Per mille of operations that remove, where removal exists.</summary>
            public int RemoveRate { get; init; } = 250;

            /// <summary>The removal rate at operation i, where it varies by phase.</summary>
            public Func<int, int>? RemoveRateAt { get; init; }

            /// <summary>
            /// Which side of its thresholds a structure is on, read from a restored twin
            /// (which is compact, so reading it changes nothing). The vacuity guard: a
            /// sweep that never crossed a threshold says nothing about crossing one.
            /// </summary>
            public Func<T, string>? Phase { get; init; }
            public int MinPhases { get; init; }
            private readonly HashSet<string> phasesSeen = new();

            public string? CheckThresholdsCrossed()
            {
                if (this.Phase is null) return null;
                lock (this.phasesSeen)
                {
                    return this.phasesSeen.Count >= this.MinPhases ? null
                        : $"saw only {this.phasesSeen.Count} of the {this.MinPhases} phases " +
                          $"it should pass through ({string.Join(", ", this.phasesSeen)})";
                }
            }
            public int Universe { get; init; } = 600;

            /// <summary>How many keys take three eighths of the adds: the heavy hitters.</summary>
            public int HotKeys { get; init; } = 8;

            /// <summary>The most adds a merged-in sketch carries.</summary>
            public int SideMax { get; init; } = 40;

            public bool HasReset => this.Reset is not null;
            public bool HasModel => this.Model is not null;
            public bool HasUndo => this.Undo is not null;

            public Type Structure => typeof(T);

            public IReadOnlyCollection<string> Runs
            {
                get
                {
                    var runs = new List<string> { "Add" };
                    if (this.Reset is not null) runs.Add("Reset");
                    if (this.Merge is not null) runs.Add(this.MergeVia);
                    if (this.Remove is not null) runs.Add(this.RemoveVia);
                    return runs;
                }
            }

            // --- generation ---------------------------------------------------------

            /// <summary>
            /// The sequence for a seed. Removals are drawn from what is live, so that a
            /// structure whose removal contract is "only what was added" gets the
            /// sequences its contract allows.
            /// </summary>
            public List<Op> Generate(int seq, int length, bool addsOnly = false)
            {
                var ops = new List<Op>(length);
                var live = new List<int>();
                var negative = new List<int>();
                for (var i = 0; i < length; i++)
                {
                    var d = Draw(seq, "op", i);
                    var roll = (int)(d % 1000);
                    var key = ((d >> 10) & 7) < 3 && this.HotKeys > 0
                        ? (int)((d >> 20) % (ulong)this.HotKeys)
                        : (int)((d >> 20) % (ulong)this.Universe);
                    // Quarter-integers, so sums of thousands of them are exact in double
                    // precision: a difference between twins is then a difference in what
                    // was folded, never in the order the rounding happened.
                    var value = (((d >> 40) % 64) + 1) / 4.0;

                    Kind kind;
                    if (!addsOnly && this.Reset is not null && roll < 3) kind = Kind.Reset;
                    else if (!addsOnly && this.Merge is not null && roll < 15) kind = Kind.Merge;
                    else if (roll < 45) kind = Kind.Query;
                    else if (!addsOnly && this.Remove is not null
                        && roll < 45 + (this.RemoveRateAt?.Invoke(i) ?? this.RemoveRate)) kind = Kind.Remove;
                    else kind = Kind.Add;

                    if (kind == Kind.Remove)
                    {
                        if (live.Count > 0)
                        {
                            var at = (int)((d >> 44) % (ulong)live.Count);
                            key = live[at];
                            live[at] = live[^1];
                            live.RemoveAt(live.Count - 1);
                        }
                        else if (!this.RemovesAbsentKeys)
                        {
                            kind = Kind.Add;
                        }
                        else
                        {
                            // Nothing live: remove a key never added, which the
                            // structure must report as a negative difference.
                            key = this.Universe + i;
                            negative.Add(key);
                        }
                    }
                    else if (kind == Kind.Add && negative.Count > 0 && ((d >> 50) & 1) == 0)
                    {
                        // Half the adds, while any are outstanding, put back a key that
                        // was removed without being added, so the difference stays small.
                        key = negative[^1];
                        negative.RemoveAt(negative.Count - 1);
                        ops.Add(new Op(kind, key, value, i));
                        continue;
                    }
                    if (kind == Kind.Add) live.Add(key);
                    if (kind == Kind.Merge) live.AddRange(this.SideOps(seq, i).Select(op => op.Key));
                    if (kind == Kind.Reset) { live.Clear(); negative.Clear(); }

                    ops.Add(new Op(kind, key, value, i));
                }
                return ops;
            }

            /// <summary>
            /// The adds behind the sketch a Merge operation merges in: a short keyed
            /// stream of its own, overlapping the main one.
            /// </summary>
            private List<Op> SideOps(int seq, int index)
            {
                var n = 1 + (int)(Draw(seq, $"side{index}-n", 0) % (ulong)this.SideMax);
                var ops = new List<Op>(n);
                for (var j = 0; j < n; j++)
                {
                    var d = Draw(seq, $"side{index}", j);
                    ops.Add(new Op(Kind.Add, (int)(d % (ulong)this.Universe),
                        (((d >> 40) % 64) + 1) / 4.0, j));
                }
                return ops;
            }

            /// <summary>
            /// The sketch a Merge operation merges in, sometimes itself saved and
            /// restored so the merge reads an input that came back from bytes.
            /// </summary>
            private T Side(int seq, int index, List<Event>? events)
            {
                var side = this.Fresh();
                foreach (var op in this.SideOps(seq, index))
                {
                    side = this.Add(side, op);
                    events?.Add(new Event(Kind.Add, this.KeyBytes(op.Key), op.Value));
                }
                if (Draw(seq, $"side{index}-rt", 0) % 3 == 0)
                {
                    side = Persistence.FromByteArray<T>(side.ToByteArray());
                }
                return side;
            }

            private T Apply(T s, Op op, int seq, List<Event>? events, List<string>? answers)
            {
                switch (op.Kind)
                {
                    case Kind.Add:
                        events?.Add(new Event(Kind.Add, this.KeyBytes(op.Key), op.Value));
                        return this.Add(s, op);
                    case Kind.Remove:
                        events?.Add(new Event(Kind.Remove, this.KeyBytes(op.Key), op.Value));
                        return this.Remove!(s, op);
                    case Kind.Reset:
                        events?.Clear();
                        return this.Reset!(s);
                    case Kind.Merge:
                        return this.Merge!(s, this.Side(seq, op.Index, events));
                    default:
                        answers?.Add(this.Query(s, op));
                        return s;
                }
            }

            /// <summary>Saves at roughly one operation in sixty, and always right after a
            /// reset or a merge -- the transitions no single-scenario test saves across.</summary>
            private static bool IsCheckpoint(int seq, Op op) =>
                op.Kind is Kind.Reset or Kind.Merge || Draw(seq, "ckpt", op.Index) % 61 == 0;

            // --- property: saving changes nothing ----------------------------------

            public string? CheckSavingChangesNothing(int seq)
            {
                var ops = this.Generate(seq, Length);
                var checkpoints = ops.Where(op => IsCheckpoint(seq, op)).Select(op => op.Index).ToHashSet();
                var failure = this.RunTwins(seq, ops, checkpoints);
                if (failure is null)
                {
                    return null;
                }

                // Find the shortest prefix, and a single checkpoint, that still fail.
                foreach (var single in checkpoints.OrderBy(c => c))
                {
                    var one = new HashSet<int> { single };
                    if (this.RunTwins(seq, ops, one) is null) continue;
                    int lo = single + 1, hi = ops.Count;
                    while (lo < hi)
                    {
                        var mid = (lo + hi) / 2;
                        if (this.RunTwins(seq, ops.Take(mid).ToList(), one) is null) lo = mid + 1;
                        else hi = mid;
                    }
                    var shortest = ops.Take(lo).ToList();
                    return $"{failure}\n    minimal: save once after op {single}, stop after op " +
                           $"{lo - 1} -> {this.RunTwins(seq, shortest, one)}\n    ops {single - 5}..{lo - 1}: " +
                           string.Join(" ", shortest.Skip(Math.Max(0, single - 5)).Select(Show));
                }
                return failure + " (needs more than one checkpoint to reproduce)";
            }

            private static string Show(Op op) => op.Kind switch
            {
                Kind.Add => $"+{op.Key}/{op.Value}",
                Kind.Remove => $"-{op.Key}",
                Kind.Reset => "RESET",
                Kind.Merge => $"MERGE#{op.Index}",
                _ => "Q",
            };

            private string? RunTwins(int seq, List<Op> ops, HashSet<int> checkpoints)
            {
                var never = this.Fresh();
                var saved = this.Fresh();
                var neverAnswers = new List<string>();
                var savedAnswers = new List<string>();

                foreach (var op in ops)
                {
                    never = this.Apply(never, op, seq, null, neverAnswers);
                    saved = this.Apply(saved, op, seq, null, savedAnswers);

                    if (!this.QueryIsNoisy && neverAnswers.Count > 0
                        && neverAnswers[^1] != savedAnswers[^1])
                    {
                        return $"after op {op.Index} the saved twin answered " +
                               $"'{savedAnswers[^1]}' where the never-saved twin answered " +
                               $"'{neverAnswers[^1]}'";
                    }
                    neverAnswers.Clear();
                    savedAnswers.Clear();

                    if (checkpoints.Contains(op.Index))
                    {
                        byte[] bytes;
                        try
                        {
                            bytes = saved.ToByteArray();
                            saved = Persistence.FromByteArray<T>(bytes);
                        }
                        catch (Exception ex)
                        {
                            return $"the payload written after op {op.Index} does not read " +
                                   $"back: {ex.GetType().Name}: {ex.Message}";
                        }
                        if (this.Phase is not null)
                        {
                            lock (this.phasesSeen) this.phasesSeen.Add(this.Phase(saved));
                        }
                        var again = saved.ToByteArray();
                        if (!again.AsSpan().SequenceEqual(bytes))
                        {
                            return $"written after op {op.Index}, read, and written again, " +
                                   $"the payload changed ({bytes.Length} -> {again.Length} " +
                                   $"bytes, first difference at {FirstDifference(bytes, again)})";
                        }
                    }
                }

                var expected = this.Canon(never);
                var actual = this.Canon(saved);
                return expected.AsSpan().SequenceEqual(actual) ? null
                    : $"after {ops.Count} ops the twin saved at {checkpoints.Count} points " +
                      $"ended {Summarize(actual)} where the never-saved twin ended " +
                      $"{Summarize(expected)}";
            }

            // --- property: reset leaves a fresh instance ---------------------------

            public string? CheckResetIsFresh(int seq)
            {
                var ops = this.Generate(seq, Length);
                var resets = ops.Count(op => op.Kind == Kind.Reset);
                if (resets == 0)
                {
                    return null;
                }

                var used = this.Fresh();
                T? fresh = null;
                var compared = 0;
                foreach (var op in ops)
                {
                    if (op.Kind == Kind.Reset)
                    {
                        // Each reset closes the span the previous one opened.
                        if (fresh is not null)
                        {
                            var failure = Compare(op.Index);
                            if (failure is not null) return failure;
                        }
                        used = this.Reset!(used);
                        fresh = this.FreshAfterReset is null ? this.Fresh() : this.FreshAfterReset(used);
                        continue;
                    }
                    used = this.Apply(used, op, seq, null, null);
                    if (fresh is not null)
                    {
                        fresh = this.Apply(fresh, op, seq, null, null);
                    }
                }
                if (compared == 0 && fresh is null) return null;
                return Compare(ops.Count);

                string? Compare(int at)
                {
                    compared++;
                    var want = this.Canon(fresh!);
                    var got = this.Canon(used);
                    return want.AsSpan().SequenceEqual(got) ? null
                        : $"by op {at} a reset structure, carried on, ended {Summarize(got)} " +
                          $"where a fresh one given the same operations ended {Summarize(want)}";
                }
            }

            // --- property: sharded merge is the union --------------------------------

            public string? CheckShardedMerge(int seq)
            {
                var ops = this.Generate(seq, Length, addsOnly: true);
                var shards = 2 + (seq % 4);
                var whole = this.Fresh();
                var parts = Enumerable.Range(0, shards).Select(_ => this.Fresh()).ToList();

                foreach (var op in ops)
                {
                    whole = this.Apply(whole, op, seq, null, null);
                    // The last shard is left empty on odd seeds: the identity element has
                    // to fold in like any other.
                    var j = (int)(Draw(seq, "shard", op.Index) % (ulong)(shards - (seq & 1)));
                    parts[j] = this.Apply(parts[j], op, seq, null, null);
                    if (Draw(seq, "shard-ckpt", op.Index) % 97 == 0)
                    {
                        parts[j] = Persistence.FromByteArray<T>(parts[j].ToByteArray());
                    }
                }

                // Fold in a keyed order and tree shape: pick two, merge one into the other
                // in a keyed direction, put the result back.
                var fold = 0;
                while (parts.Count > 1)
                {
                    var a = (int)(Draw(seq, "fold-a", fold) % (ulong)parts.Count);
                    var b = (int)(Draw(seq, "fold-b", fold) % (ulong)(parts.Count - 1));
                    if (b >= a) b++;
                    var (into, other) = Draw(seq, "fold-dir", fold) % 2 == 0
                        ? (parts[a], parts[b]) : (parts[b], parts[a]);
                    var merged = this.Merge!(into, other);
                    parts.RemoveAt(Math.Max(a, b));
                    parts.RemoveAt(Math.Min(a, b));
                    parts.Add(merged);
                    fold++;
                }

                var want = this.Canon(whole);
                var got = this.Canon(parts[0]);
                if (!want.AsSpan().SequenceEqual(got))
                {
                    return $"{shards} shards folded together ended {Summarize(got)} where the " +
                           $"sketch of the whole stream is {Summarize(want)}";
                }

                if (this.MergeIsIdempotent)
                {
                    var copy = Persistence.FromByteArray<T>(want);
                    var twice = this.Merge!(Persistence.FromByteArray<T>(want), copy);
                    var bytes = this.Canon(twice);
                    if (!want.AsSpan().SequenceEqual(bytes))
                    {
                        return $"merged with a copy of itself, the sketch of the whole stream " +
                               $"became {Summarize(bytes)} from {Summarize(want)}";
                    }
                }
                return null;
            }

            // --- property: removing everything leaves a fresh instance ---------------

            public string? CheckUnwindIsFresh(int seq)
            {
                var ops = this.Generate(seq, Length);
                var net = new Dictionary<int, long>();
                var s = this.Fresh();
                foreach (var op in ops)
                {
                    s = this.Apply(s, op, seq, null, null);
                    switch (op.Kind)
                    {
                        case Kind.Reset: net.Clear(); break;
                        case Kind.Add or Kind.Remove:
                            net[op.Key] = net.GetValueOrDefault(op.Key) + this.Delta(op);
                            break;
                        case Kind.Merge:
                            foreach (var side in this.SideOps(seq, op.Index))
                            {
                                net[side.Key] = net.GetValueOrDefault(side.Key) + this.Delta(side);
                            }
                            break;
                    }
                }

                var moved = net.Values.Count(v => v != 0);
                foreach (var key in net.Keys.OrderBy(k => Draw(seq, "undo", k)))
                {
                    s = this.Undo!(s, key, net[key]);
                }

                var want = this.Canon(this.Fresh());
                var got = this.Canon(s);
                return want.AsSpan().SequenceEqual(got) ? null
                    : $"after {ops.Count} ops, taking back the net count of all {moved} keys " +
                      $"left {Summarize(got)} where a fresh structure is {Summarize(want)}";
            }

            // --- property: repeating an item changes nothing (small scope) -----------

            public string? CheckDuplicatesAreInvisible()
            {
                const int Distinct = 48;
                var failing = new List<int>();
                var phases = new HashSet<string>();
                for (var n = 0; n <= Distinct; n++)
                {
                    var built = this.Build(n);
                    if (this.Phase is not null) phases.Add(this.Phase(built));
                    var bytes = this.Canon(built);
                    var changed = false;
                    for (var j = 0; j < n && !changed; j++)
                    {
                        var again = this.Add(this.Build(n), new Op(Kind.Add, j, 1, n));
                        changed = !bytes.AsSpan().SequenceEqual(this.Canon(again));
                    }
                    if (!changed && this.Merge is not null && n > 0)
                    {
                        var merged = this.Merge(this.Build(n), Persistence.FromByteArray<T>(bytes));
                        changed = !bytes.AsSpan().SequenceEqual(this.Canon(merged));
                    }
                    if (changed) failing.Add(n);
                }
                // A sweep that stayed on one side of the threshold it is named for has
                // checked nothing about the threshold.
                if (this.Phase is not null && phases.Count < this.MinPhases)
                {
                    return $"the sweep never left {string.Join(", ", phases)}, so it did " +
                           "not straddle the point where the sketch changes form";
                }
                return failing.Count == 0 ? null
                    : $"repeating an item, or merging a copy, changes the sketch at " +
                      $"{string.Join(", ", failing)} distinct only";
            }

            /// <summary>The first n distinct keys, added in order to a fresh instance.</summary>
            private T Build(int n)
            {
                var s = this.Fresh();
                for (var i = 0; i < n; i++)
                {
                    s = this.Add(s, new Op(Kind.Add, i, 1, i));
                }
                return s;
            }

            // --- property: exact model ----------------------------------------------

            public string? CheckModel(int seq)
            {
                var ops = this.Generate(seq, Length);
                var events = new List<Event>();
                var s = this.Fresh();
                foreach (var op in ops)
                {
                    s = this.Apply(s, op, seq, events, null);
                    var checkpoint = IsCheckpoint(seq, op);
                    if (checkpoint)
                    {
                        s = Persistence.FromByteArray<T>(s.ToByteArray());
                    }
                    if (checkpoint || op.Kind == Kind.Query)
                    {
                        var failure = this.Model!(s, events);
                        if (failure is not null)
                        {
                            return $"after op {op.Index}{(checkpoint ? ", restored" : "")}: {failure}";
                        }
                    }
                }
                var final = this.Model!(s, events);
                return final is null ? null : $"at the end: {final}";
            }
        }

        private static int FirstDifference(byte[] a, byte[] b)
        {
            var n = Math.Min(a.Length, b.Length);
            for (var i = 0; i < n; i++)
            {
                if (a[i] != b[i]) return i;
            }
            return n;
        }

        private static string Summarize(byte[] bytes) =>
            $"[{bytes.Length} bytes, hash {Mix(bytes):x16}]";
    }
}
