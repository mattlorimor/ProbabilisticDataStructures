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
    /// Operation sequences against exact models, with the structural invariants checked
    /// after every operation (issue 144), for the quotient-style and cuckoo filters.
    /// </summary>
    /// <remarks>
    /// Exhaustive small scope: at the smallest legal tables, with a
    /// hash chosen so that keys <i>are</i> fingerprints, every operation is applied to
    /// every reachable state, breadth first, deduplicated on the persisted bytes --
    /// which is sound because those bytes are the whole state, so two sequences
    /// reaching the same bytes have the same futures. That covers every sequence up to
    /// the depth reached, not a sample of them. Long random sequences: the same checks
    /// on realistic tables, driven by keyed streams and a literal-seeded schedule,
    /// with Reset, Merge and save/load mid-sequence.
    /// <para>
    /// The models are the guarantees each filter makes. The quotient and cuckoo
    /// filters are fingerprint filters with exact abstractions, so every answer --
    /// false positives included -- is predicted. Infini and Memento shorten
    /// fingerprints as they grow, on a schedule that is the filter's own choice, so
    /// their model is the guarantee that survives any schedule: a one-to-one
    /// assignment of stored entries to live keys, each entry remembering only bits
    /// its key's hash really has.
    /// </para>
    /// <para>
    /// The work is bounded by explicit transition budgets, not clocks: an exploration
    /// that outgrows its budget fails by count.
    /// </para>
    /// <para>
    /// What is sequenced is not a list anyone has to remember. The roster tests at the
    /// end derive the filters that delete an entry, and the operations each one offers,
    /// from the library's public surface, and fail when either has outgrown the table
    /// of what these sequences apply.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestQuotientFamilySequences
    {
        private static byte[] K(string s) => Encoding.UTF8.GetBytes(s);

        private static byte[] Id(int id)
        {
            var bytes = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)id);
            return bytes;
        }

        private static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

        private sealed class BytesComparer : IEqualityComparer<byte[]>
        {
            internal static readonly BytesComparer Instance = new BytesComparer();

            public bool Equals(byte[] x, byte[] y) => Same(x, y);

            public int GetHashCode(byte[] bytes)
            {
                var hash = new HashCode();
                hash.AddBytes(bytes);
                return hash.ToHashCode();
            }
        }

        /// <summary>
        /// Every state reachable in at most <paramref name="maxDepth"/> operations,
        /// breadth first. <paramref name="step"/> applies one operation to a copy of a
        /// state, checks everything, and returns the successor, or null when the
        /// operation does not apply there.
        /// </summary>
        private static (int States, long Transitions, int Depth, bool Closed) Explore<TModel>(
            byte[] root, TModel rootModel, int operations, int maxDepth, long budget,
            Func<byte[], TModel, int, (byte[] Bytes, TModel Model)?> step)
        {
            var seen = new HashSet<byte[]>(BytesComparer.Instance) { root };
            var frontier = new List<(byte[] Bytes, TModel Model)> { (root, rootModel) };
            var transitions = 0L;
            var depth = 0;

            while (frontier.Count > 0 && depth < maxDepth)
            {
                depth++;
                var next = new List<(byte[] Bytes, TModel Model)>();
                foreach (var (bytes, model) in frontier)
                {
                    for (var op = 0; op < operations; op++)
                    {
                        var successor = step(bytes, model, op);
                        if (successor is null)
                        {
                            continue;
                        }
                        if (++transitions > budget)
                        {
                            Assert.Fail($"the exploration passed its budget of {budget} " +
                                $"transitions at depth {depth}; the state space is larger " +
                                "than it was when the budget was probed.");
                        }
                        if (seen.Add(successor.Value.Bytes))
                        {
                            next.Add(successor.Value);
                        }
                    }
                }
                frontier = next;
            }

            return (seen.Count, transitions, depth, frontier.Count == 0);
        }

        // ---------------------------------------------------------------- quotient

        /// <summary>
        /// Every sequence of adds, removes (of present and absent keys), resets and
        /// self-merges at the smallest tables the constructor builds, with the hash
        /// replaced so that a key is its own fingerprint and the alphabet is every
        /// fingerprint there is. After every operation: the table decodes cleanly, its
        /// contents are exactly the model multiset, every one of the fingerprints
        /// answers exactly, the payload round-trips byte for byte, and the layout is a
        /// function of the contents alone -- the claim Remove's remarks make for
        /// rebuilding a cluster instead of shifting it.
        /// </summary>
        [TestMethod]
        public void TestQuotientFilterEveryShortSequenceAtTheSmallestTables()
        {
            // (n, rate) -> (quotient bits, remainder bits): (1, .5) -> (1, 1);
            // (3, .5) -> (2, 1); (3, .25) -> (2, 2); (4, .5) -> (3, 1). A shape with
            // no depth limit is explored to closure -- every sequence of every length;
            // the one with a limit has too many states for that.
            var shapes = new (uint N, double Rate, int Depth, long Budget)[]
            {
                (1, 0.5, int.MaxValue, 1000),
                (3, 0.5, int.MaxValue, 20000),
                (3, 0.25, int.MaxValue, 400000),
                (4, 0.5, 5, 500000),
            };

            foreach (var (n, rate, maxDepth, budget) in shapes)
            {
                var remainderBits = (int)Math.Max(1, Math.Ceiling(Math.Log2(1.0 / rate)));
                var quotientBits = (int)Math.Max(1, Math.Ceiling(Math.Log2(n / 0.75)));
                var width = quotientBits + remainderBits;
                var alphabet = 1 << width;
                var slots = 1 << quotientBits;
                Func<ReadOnlySpan<byte>, ulong> hash =
                    k => (ulong)BinaryPrimitives.ReadUInt16LittleEndian(k) << (64 - width);

                var root = new QuotientFilter(n, rate, hash);
                Assert.AreEqual((uint)slots, root.Capacity(), "premise: the table shape.");

                var layouts = new Dictionary<string, byte[]>
                {
                    [string.Join(",", new int[alphabet])] = root.ToByteArray(),
                };

                (byte[], int[])? Step(byte[] bytes, int[] model, int op)
                {
                    var filter = Persistence.FromByteArray<QuotientFilter>(bytes, hash);
                    model = (int[])model.Clone();
                    var held = model.Sum();
                    var what = $"q={quotientBits} r={remainderBits} op {op} on [{string.Join(",", model)}]";

                    if (op < alphabet)
                    {
                        if (held == slots)
                        {
                            Assert.ThrowsExactly<InvalidOperationException>(() => filter.Add(Id(op)), what);
                        }
                        else
                        {
                            filter.Add(Id(op));
                            model[op]++;
                        }
                    }
                    else if (op < 2 * alphabet)
                    {
                        var fp = op - alphabet;
                        Assert.AreEqual(model[fp] > 0, filter.TestAndRemove(Id(fp)),
                            $"{what}: removal must succeed exactly when the fingerprint is held.");
                        if (model[fp] > 0)
                        {
                            model[fp]--;
                        }
                    }
                    else if (op == 2 * alphabet)
                    {
                        filter.Reset();
                        Array.Clear(model);
                    }
                    else
                    {
                        if (2 * held > slots)
                        {
                            Assert.ThrowsExactly<InvalidOperationException>(() => filter.Merge(filter), what);
                        }
                        else
                        {
                            filter.Merge(filter);
                            for (var i = 0; i < alphabet; i++)
                            {
                                model[i] *= 2;
                            }
                        }
                    }

                    var stored = QuotientFamilyInvariants.CheckQuotientFilter(filter, out var payload);
                    var expected = Enumerable.Range(0, alphabet)
                        .SelectMany(i => Enumerable.Repeat((ulong)i, model[i])).ToArray();
                    if (!expected.AsSpan().SequenceEqual(stored))
                    {
                        Assert.Fail($"{what}: the table holds [{string.Join(",", stored)}] " +
                            $"where the model holds [{string.Join(",", expected)}].");
                    }
                    Assert.AreEqual((uint)expected.Length, filter.Count(), what);
                    for (var i = 0; i < alphabet; i++)
                    {
                        if ((model[i] > 0) != filter.Test(Id(i)))
                        {
                            Assert.Fail($"{what}: fingerprint {i} answered {model[i] == 0}.");
                        }
                    }

                    var key = string.Join(",", model);
                    if (layouts.TryGetValue(key, out var earlier))
                    {
                        Assert.IsTrue(Same(earlier, payload),
                            $"{what}: the same contents reached a different layout by a " +
                            "different history.");
                    }
                    else
                    {
                        layouts[key] = payload;
                        Assert.IsTrue(Same(payload,
                            Persistence.FromByteArray<QuotientFilter>(payload, hash).ToByteArray()),
                            $"{what}: the payload does not round-trip byte for byte.");
                    }

                    return (payload, model);
                }

                var (states, transitions, depth, closed) = Explore(
                    root.ToByteArray(), new int[alphabet], 2 * alphabet + 2, maxDepth, budget, Step);

                Console.WriteLine($"quotient q={quotientBits} r={remainderBits}: {states} states, " +
                    $"{transitions} transitions, depth {depth}{(closed ? " (closed)" : "")}, " +
                    $"{layouts.Count} distinct contents");

                Assert.AreEqual(layouts.Count, states,
                    "two distinct states hold the same contents; the canonical-layout check " +
                    "should have caught that first.");
                if (maxDepth == int.MaxValue)
                {
                    Assert.IsTrue(closed, "premise: explored to closure.");
                    // Vacuity: closure must include the full table, where no slot is
                    // empty and every walk relies on the unshifted anchor alone.
                    Assert.IsTrue(layouts.Keys.Any(k => k.Split(',').Sum(int.Parse) == slots),
                        "premise: the exploration reached a full table.");
                }
            }
        }

        /// <summary>
        /// Long churn against the fingerprint multiset, every operation checked: fills
        /// to the brim and past it, drains, resets, merges another filter and itself,
        /// and swaps itself for its own reloaded payload, with every answer predicted
        /// -- false positives and false-positive removals included.
        /// </summary>
        [TestMethod]
        public void TestQuotientFilterLongSequencesAgainstTheFingerprintModel()
        {
            var cases = new (uint N, double Rate, int Steps, int Seed)[]
            {
                (12, 0.25, 4000, 101),
                (48, 0.25, 4000, 102),
                (200, 0.5, 3000, 103),
                (500, 0.01, 4000, 104),
            };
            var hash = Defaults.GetDefaultHashFunction();

            foreach (var (n, rate, steps, seed) in cases)
            {
                var remainderBits = (int)Math.Max(1, Math.Ceiling(Math.Log2(1.0 / rate)));
                var quotientBits = (int)Math.Max(1, Math.Ceiling(Math.Log2(n / 0.75)));
                var slots = 1 << quotientBits;
                ulong Fp(string s) => hash(K(s)) >> (64 - (quotientBits + remainderBits));

                var filter = new QuotientFilter(n, rate);
                var model = new Dictionary<ulong, int>();
                var live = new List<string>();
                var schedule = new Random(seed);
                var next = 0;
                var counts = new Dictionary<string, int>();
                var filling = true;

                string Fresh() => $"s{seed}-k{next++}";
                void Count(string what) => counts[what] = counts.GetValueOrDefault(what) + 1;
                void ModelAdd(string s)
                {
                    model[Fp(s)] = model.GetValueOrDefault(Fp(s)) + 1;
                    live.Add(s);
                }
                void ModelRemove(ulong fp)
                {
                    if (--model[fp] == 0)
                    {
                        model.Remove(fp);
                    }
                    live.RemoveAt(live.FindIndex(s => Fp(s) == fp));
                }

                for (var step = 0; step < steps; step++)
                {
                    var what = $"n={n} seed={seed} step {step}";
                    if (live.Count == slots) filling = false;
                    if (live.Count <= slots / 8) filling = true;
                    var roll = schedule.Next(1000);

                    if (roll < 3)
                    {
                        filter.Reset();
                        model.Clear();
                        live.Clear();
                        Count("reset");
                    }
                    else if (roll < 33)
                    {
                        var payload = filter.ToByteArray();
                        filter = Persistence.FromByteArray<QuotientFilter>(payload);
                        Assert.IsTrue(Same(payload, filter.ToByteArray()), $"{what}: round trip.");
                        Count("reload");
                    }
                    else if (roll < 60)
                    {
                        var other = new QuotientFilter(n, rate);
                        var added = new List<string>();
                        for (var i = schedule.Next(slots / 4 + 1); i > 0; i--)
                        {
                            added.Add(Fresh());
                            other.Add(K(added[^1]));
                        }
                        if (live.Count + added.Count > slots)
                        {
                            Assert.ThrowsExactly<InvalidOperationException>(() => filter.Merge(other), what);
                            Count("merge refused");
                        }
                        else
                        {
                            filter.Merge(other);
                            added.ForEach(ModelAdd);
                            Count("merge");
                        }
                    }
                    else if (roll < 65)
                    {
                        if (2 * live.Count > slots)
                        {
                            Assert.ThrowsExactly<InvalidOperationException>(() => filter.Merge(filter), what);
                        }
                        else
                        {
                            filter.Merge(filter);
                            foreach (var s in live.ToArray())
                            {
                                ModelAdd(s);
                            }
                            Count("self-merge");
                        }
                    }
                    else if (filling ? roll < 750 : roll < 300)
                    {
                        var s = live.Count > 0 && schedule.Next(8) == 0
                            ? live[schedule.Next(live.Count)]
                            : Fresh();
                        if (live.Count == slots)
                        {
                            Assert.ThrowsExactly<InvalidOperationException>(() => filter.Add(K(s)), what);
                            Count("add refused");
                        }
                        else
                        {
                            filter.Add(K(s));
                            ModelAdd(s);
                            Count("add");
                        }
                    }
                    else if (live.Count > 0 && schedule.Next(5) > 0)
                    {
                        var s = live[schedule.Next(live.Count)];
                        Assert.IsTrue(filter.TestAndRemove(K(s)), $"{what}: removing held '{s}'.");
                        ModelRemove(Fp(s));
                        Count("remove");
                    }
                    else
                    {
                        var s = Fresh();
                        var collides = model.ContainsKey(Fp(s));
                        Assert.AreEqual(collides, filter.TestAndRemove(K(s)),
                            $"{what}: removing absent '{s}' must succeed exactly when its " +
                            "fingerprint collides with a held one.");
                        if (collides)
                        {
                            ModelRemove(Fp(s));
                            Count("false-positive removal");
                        }
                        else
                        {
                            Count("absent removal");
                        }
                    }

                    var stored = QuotientFamilyInvariants.CheckQuotientFilter(filter, out _);
                    var expected = model.SelectMany(kv => Enumerable.Repeat(kv.Key, kv.Value))
                        .OrderBy(f => f).ToArray();
                    if (!expected.AsSpan().SequenceEqual(stored))
                    {
                        Assert.Fail($"{what}: the table's contents differ from the model's.");
                    }
                    for (var i = 0; i < 3 && live.Count > 0; i++)
                    {
                        var s = live[schedule.Next(live.Count)];
                        Assert.IsTrue(filter.Test(K(s)), $"{what}: held '{s}' not found.");
                    }
                    for (var i = 0; i < 3; i++)
                    {
                        var probe = $"s{seed}-probe-{step}-{i}";
                        Assert.AreEqual(model.ContainsKey(Fp(probe)), filter.Test(K(probe)),
                            $"{what}: '{probe}' answered against its fingerprint's presence.");
                    }
                }

                Console.WriteLine($"quotient n={n} slots={slots}: " +
                    string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
                foreach (var needed in new[] { "add refused", "false-positive removal", "merge", "reset", "reload", "remove" })
                {
                    Assert.IsTrue(counts.ContainsKey(needed),
                        $"n={n}: the schedule never produced a {needed}, so it was not tested.");
                }
            }
        }

        // ------------------------------------------------------------------ cuckoo

        /// <summary>
        /// The cuckoo filter's exact abstraction: whatever its kicks did, it holds the
        /// multiset of (fingerprint, bucket pair) of what it accepted. That predicts
        /// every lookup and every removal, false positives included, and a refused
        /// insert must leave the table bit-for-bit as it was. A reloaded filter must
        /// go on to behave exactly as its never-saved twin, kick choices included.
        /// </summary>
        [TestMethod]
        public void TestCuckooFilterLongSequencesAgainstThePlacementModel()
        {
            var cases = new (uint N, double Rate, int Steps, int Seed)[]
            {
                (8, 0.25, 3000, 201),
                (30, 0.25, 3000, 202),
                (100, 0.01, 3000, 203),
            };
            var hash = Defaults.GetDefaultHashFunction();

            foreach (var (n, rate, steps, seed) in cases)
            {
                var filter = new CuckooBloomFilter(n, rate, seed: seed);
                var twin = new CuckooBloomFilter(n, rate, seed: seed);
                var m = filter.M;
                var bits = (int)filter.FingerprintBits;
                var capacity = (int)(filter.M * filter.B);
                (ulong, uint, uint) Of(string s) => QuotientFamilyInvariants.CuckooKey(K(s), m, bits, hash);

                var model = new Dictionary<(ulong, uint, uint), int>();
                var live = new List<string>();
                var schedule = new Random(seed);
                var next = 0;
                var counts = new Dictionary<string, int>();
                var filling = true;

                string Fresh() => $"s{seed}-k{next++}";
                void Count(string what) => counts[what] = counts.GetValueOrDefault(what) + 1;
                void ModelAdd(string s)
                {
                    model[Of(s)] = model.GetValueOrDefault(Of(s)) + 1;
                    live.Add(s);
                }
                void ModelRemove((ulong, uint, uint) placement)
                {
                    if (--model[placement] == 0)
                    {
                        model.Remove(placement);
                    }
                    live.RemoveAt(live.FindIndex(s => Of(s) == placement));
                }
                bool Both(Func<CuckooBloomFilter, bool> op)
                {
                    var answer = op(filter);
                    Assert.AreEqual(answer, op(twin), "the reloaded filter and its twin disagree.");
                    return answer;
                }

                for (var step = 0; step < steps; step++)
                {
                    var what = $"n={n} seed={seed} step {step}";
                    if (live.Count >= capacity) filling = false;
                    if (live.Count <= capacity / 8) filling = true;
                    var roll = schedule.Next(1000);

                    if (roll < 3)
                    {
                        filter.Reset();
                        twin.Reset();
                        model.Clear();
                        live.Clear();
                        Count("reset");
                    }
                    else if (roll < 40)
                    {
                        var payload = filter.ToByteArray();
                        filter = Persistence.FromByteArray<CuckooBloomFilter>(payload);
                        Assert.IsTrue(Same(payload, filter.ToByteArray()), $"{what}: round trip.");
                        Count("reload");
                    }
                    else if (filling ? roll < 750 : roll < 350)
                    {
                        var s = live.Count > 0 && schedule.Next(8) == 0
                            ? live[schedule.Next(live.Count)]
                            : Fresh();
                        var fingerprints = (ulong[])filter.Fingerprints.Clone();
                        var occupancy = filter.Occupied.RawData.ToArray();
                        var kicksBefore = filter.KickSteps;
                        var member = false;
                        bool accepted;
                        if (schedule.Next(4) == 0)
                        {
                            member = model.ContainsKey(Of(s));
                            var result = filter.TestAndAdd(K(s));
                            var twinResult = twin.TestAndAdd(K(s));
                            Assert.AreEqual(result.WasAlreadyAMember, twinResult.WasAlreadyAMember);
                            Assert.AreEqual(result.Added, twinResult.Added);
                            Assert.AreEqual(member, result.WasAlreadyAMember,
                                $"{what}: TestAndAdd's membership answer.");
                            accepted = result.Added;
                        }
                        else
                        {
                            accepted = Both(f => f.Add(K(s)));
                        }

                        var unchanged = fingerprints.AsSpan().SequenceEqual(filter.Fingerprints)
                            && occupancy.AsSpan().SequenceEqual(filter.Occupied.RawData);
                        if (member)
                        {
                            Assert.IsFalse(accepted, $"{what}: a member was added again.");
                            Assert.IsTrue(unchanged, $"{what}: TestAndAdd of a member changed the table.");
                            Count("test-and-add member");
                        }
                        else if (accepted)
                        {
                            ModelAdd(s);
                            Count(filter.KickSteps > kicksBefore ? "add after kicks" : "add");
                        }
                        else
                        {
                            Assert.IsTrue(unchanged,
                                $"{what}: a refused insert must leave the table exactly as it was.");
                            Count("add refused");
                            filling = false;
                        }
                    }
                    else if (live.Count > 0 && schedule.Next(5) > 0)
                    {
                        var s = live[schedule.Next(live.Count)];
                        Assert.IsTrue(Both(f => f.TestAndRemove(K(s))), $"{what}: removing held '{s}'.");
                        ModelRemove(Of(s));
                        Count("remove");
                    }
                    else
                    {
                        var s = Fresh();
                        var collides = model.ContainsKey(Of(s));
                        Assert.AreEqual(collides, Both(f => f.TestAndRemove(K(s))),
                            $"{what}: removing absent '{s}' must succeed exactly when it " +
                            "collides with a held fingerprint in its own bucket pair.");
                        if (collides)
                        {
                            ModelRemove(Of(s));
                            Count("false-positive removal");
                        }
                    }

                    var contents = QuotientFamilyInvariants.CheckCuckooFilter(filter, hash);
                    Assert.IsTrue(contents.Count == model.Count && contents.All(kv => model.GetValueOrDefault(kv.Key) == kv.Value),
                        $"{what}: the table holds a different (fingerprint, bucket pair) multiset " +
                        "from what was accepted and not removed.");
                    Assert.IsTrue(Same(filter.ToByteArray(), twin.ToByteArray()),
                        $"{what}: the filter has diverged from its never-reloaded twin.");
                    for (var i = 0; i < 3 && live.Count > 0; i++)
                    {
                        var s = live[schedule.Next(live.Count)];
                        Assert.IsTrue(filter.Test(K(s)), $"{what}: held '{s}' not found.");
                    }
                    for (var i = 0; i < 3; i++)
                    {
                        var probe = $"s{seed}-probe-{step}-{i}";
                        Assert.AreEqual(model.ContainsKey(Of(probe)), filter.Test(K(probe)),
                            $"{what}: '{probe}' answered against the placement model.");
                    }
                }

                Console.WriteLine($"cuckoo n={n} entries={capacity} bits={bits}: " +
                    string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
                // "add after kicks" is the vacuity guard on relocation itself: a kick
                // loop that never re-homes anything refuses cleanly every time, and
                // the placement model, which is load-agnostic, accepts that.
                foreach (var needed in new[] { "add refused", "add after kicks", "reload", "remove", "reset" })
                {
                    Assert.IsTrue(counts.ContainsKey(needed),
                        $"n={n}: the schedule never produced a {needed}, so it was not tested.");
                }
            }
        }

        /// <summary>
        /// Every short sequence on a two-bucket cuckoo filter, with a hash designed so
        /// that the alphabet holds both kinds of bucket pair (a fingerprint whose
        /// partner is its own bucket, and one whose partner is the other), a zero
        /// fingerprint, and keys that agree in fingerprint but not in pair. The table
        /// holds only as many entries as its buckets do, so the deeper sequences are
        /// mostly kicks and refusals. The persisted bytes carry the generator, so
        /// states reached by different kick choices stay distinct.
        /// </summary>
        [TestMethod]
        public void TestCuckooFilterEveryShortSequenceInATinyTable()
        {
            // (fingerprint, first bucket) per key. The hash of a fingerprint byte is
            // the byte itself, so an odd fingerprint pairs buckets 0 and 1 and an
            // even one pairs a bucket with itself.
            var design = new (byte F, uint I1)[] { (1, 0), (3, 1), (2, 0), (4, 1), (5, 0), (0, 1), (2, 1) };
            var alphabet = design.Length;
            Func<ReadOnlySpan<byte>, ulong> hash = k => k.Length switch
            {
                2 => design[BinaryPrimitives.ReadUInt16LittleEndian(k)].F
                    | ((ulong)BinaryPrimitives.ReadUInt16LittleEndian(k) << 8),
                8 => design[k[1]].I1,
                _ => k[0],
            };

            var root = new CuckooBloomFilter(5, 0.5, hash, seed: 7);
            Assert.AreEqual(2u, root.M, "premise: two buckets.");
            Assert.AreEqual(4u, root.FingerprintBits, "premise: four-bit fingerprints.");
            var placements = Enumerable.Range(0, alphabet)
                .Select(id => QuotientFamilyInvariants.CuckooKey(Id(id), root.M, 4, hash)).ToArray();
            var refused = 0;
            var relocated = 0;

            (byte[], int[])? Step(byte[] bytes, int[] model, int op)
            {
                var filter = Persistence.FromByteArray<CuckooBloomFilter>(bytes, hash);
                model = (int[])model.Clone();
                var what = $"cuckoo op {op} on [{string.Join(",", model)}]";

                if (op < alphabet)
                {
                    var fingerprints = (ulong[])filter.Fingerprints.Clone();
                    var occupancy = filter.Occupied.RawData.ToArray();
                    var kicksBefore = filter.KickSteps;
                    if (filter.Add(Id(op)))
                    {
                        model[op]++;
                        if (filter.KickSteps > kicksBefore)
                        {
                            relocated++;
                        }
                    }
                    else
                    {
                        Assert.IsTrue(fingerprints.AsSpan().SequenceEqual(filter.Fingerprints)
                            && occupancy.AsSpan().SequenceEqual(filter.Occupied.RawData),
                            $"{what}: a refused insert changed the table.");
                        refused++;
                    }
                }
                else if (op < 2 * alphabet)
                {
                    var id = op - alphabet;
                    var holder = Enumerable.Range(0, alphabet)
                        .FirstOrDefault(j => model[j] > 0 && placements[j] == placements[id], -1);
                    Assert.AreEqual(holder >= 0, filter.TestAndRemove(Id(id)),
                        $"{what}: removal must succeed exactly when the placement is held.");
                    if (holder >= 0)
                    {
                        model[holder]--;
                    }
                }
                else
                {
                    filter.Reset();
                    Array.Clear(model);
                }

                var contents = QuotientFamilyInvariants.CheckCuckooFilter(filter, hash);
                var expected = new Dictionary<(ulong, uint, uint), int>();
                for (var id = 0; id < alphabet; id++)
                {
                    if (model[id] > 0)
                    {
                        expected[placements[id]] = expected.GetValueOrDefault(placements[id]) + model[id];
                    }
                }
                if (contents.Count != expected.Count || contents.Any(kv => expected.GetValueOrDefault(kv.Key) != kv.Value))
                {
                    Assert.Fail($"{what}: the table's placements differ from the model's.");
                }
                for (var id = 0; id < alphabet; id++)
                {
                    if (expected.ContainsKey(placements[id]) != filter.Test(Id(id)))
                    {
                        Assert.Fail($"{what}: key {id} answered against the placement model.");
                    }
                }

                return (filter.ToByteArray(), model);
            }

            var (states, transitions, depth, closed) = Explore(
                root.ToByteArray(), new int[alphabet], 2 * alphabet + 1, 7, 600_000, Step);

            Console.WriteLine($"cuckoo M=2 B=4: {states} states, {transitions} transitions, " +
                $"depth {depth}{(closed ? " (closed)" : "")}, {refused} refused inserts, " +
                $"{relocated} accepted by relocation");
            Assert.IsGreaterThan(0, refused, "premise: the exploration reached refusals.");
            Assert.IsGreaterThan(0, relocated,
                "premise: some accepted insert was placed by relocation; without one the " +
                "kick loop is untested, since a loop that never re-homes anything only " +
                "ever refuses, cleanly.");
        }

        // ------------------------------------------------------------------ infini

        /// <summary>
        /// Hashes for the Infini alphabet, chosen rather than drawn: pairs that share a
        /// quotient and differ in the first fingerprint bit an expansion spends, pairs
        /// identical until a bit only a later expansion reaches, and the last slot, so
        /// clusters wrap. Low bits address an Infini table, so the low bits are what is
        /// designed.
        /// </summary>
        private static readonly ulong[] InfiniAlphabet =
        {
            0b000000, 0b000100, 0b010000, 0b000011, 0b001011, 0b100111, 0b000010,
        };

        /// <summary>
        /// Every sequence of adds and removes up to the depth reached, from a four-slot
        /// table with two-bit fingerprints -- the smallest the constructor builds --
        /// through its first expansions, with every state saved and reloaded on the
        /// way. Each state is held to the assignment model and its every lookup to the
        /// entries the independent decode found.
        /// </summary>
        [TestMethod]
        public void TestInfiniFilterEveryShortSequenceThroughItsFirstExpansions()
        {
            Func<ReadOnlySpan<byte>, ulong> hash =
                k => InfiniAlphabet[BinaryPrimitives.ReadUInt16LittleEndian(k)];
            var alphabet = InfiniAlphabet.Length;
            var root = new InfiniFilter(initialCapacity: 1, fingerprintBits: 2, hash: hash);
            var expansionsSeen = 0u;
            var refusedAbsent = 0;

            (byte[], int[])? Step(byte[] bytes, int[] model, int op)
            {
                var filter = Persistence.FromByteArray<InfiniFilter>(bytes, hash);
                model = (int[])model.Clone();
                var what = $"op {op} on [{string.Join(",", model)}]";

                if (op < alphabet)
                {
                    filter.Add(Id(op));
                    model[op]++;
                }
                else if (op < 2 * alphabet)
                {
                    var id = op - alphabet;
                    if (model[id] > 0)
                    {
                        Assert.IsTrue(filter.TestAndRemove(Id(id)), $"{what}: removing a held key.");
                        model[id]--;
                    }
                    else if (filter.Test(Id(id)))
                    {
                        // Removing an absent key that answers yes takes someone else's
                        // entry; the API warns against it and the model cannot follow.
                        return null;
                    }
                    else
                    {
                        Assert.IsFalse(filter.TestAndRemove(Id(id)), $"{what}: removing an absent key.");
                        Assert.IsTrue(Same(bytes, filter.ToByteArray()), $"{what}: a refused removal changed the filter.");
                        refusedAbsent++;
                    }
                }
                else
                {
                    filter.Reset();
                    Array.Clear(model);
                }

                CheckInfini(what, filter, model, hash, alphabet);
                expansionsSeen = Math.Max(expansionsSeen, filter.ExpansionCount());
                var payload = filter.ToByteArray();
                return (payload, model);
            }

            var (states, transitions, depth, closed) = Explore(
                root.ToByteArray(), new int[alphabet], 2 * alphabet + 1, 8, 1_000_000, Step);

            Console.WriteLine($"infini F=2: {states} states, {transitions} transitions, depth {depth}" +
                $"{(closed ? " (closed)" : "")}, up to {expansionsSeen} expansions, " +
                $"{refusedAbsent} refused absent removals");
            Assert.IsGreaterThanOrEqualTo(2u, expansionsSeen, "premise: the exploration crossed expansions.");
        }

        private static void CheckInfini(
            string what, InfiniFilter filter, int[] model, Func<ReadOnlySpan<byte>, ulong> hash, int alphabet)
        {
            var entries = QuotientFamilyInvariants.CheckInfiniFilter(filter);
            var keys = new List<(ulong, ulong)>();
            for (var id = 0; id < alphabet; id++)
            {
                for (var c = 0; c < model[id]; c++)
                {
                    keys.Add((hash(Id(id)), 0));
                }
            }
            QuotientFamilyInvariants.AssignEntriesToKeys(what, entries, keys);

            for (var id = 0; id < alphabet; id++)
            {
                var h = hash(Id(id));
                var matches = entries.Any(e => (h & Mask(e.Length)) == e.Suffix);
                if (matches != filter.Test(Id(id)))
                {
                    Assert.Fail($"{what}: key {id}'s lookup disagrees with the entries the table holds.");
                }
            }
        }

        private static ulong Mask(int length) => length >= 64 ? ulong.MaxValue : (1UL << length) - 1;

        /// <summary>
        /// Long churn through many expansions, voids and sheds down the chain, with
        /// removals interleaved throughout and resets and reloads mid-sequence. Small
        /// fingerprints make entries go void quickly, which is where the chain gets
        /// exercised.
        /// </summary>
        [TestMethod]
        public void TestInfiniFilterLongSequencesWithExpansionAndDeletion()
        {
            var cases = new (uint Initial, int Bits, int Steps, int Seed)[]
            {
                (1, 2, 3000, 301),
                (4, 3, 3000, 302),
                (4, 4, 4000, 303),
            };
            var hash = Defaults.GetDefaultHashFunction();

            foreach (var (initial, bits, steps, seed) in cases)
            {
                var filter = new InfiniFilter(initial, bits);
                var live = new List<string>();
                var schedule = new Random(seed);
                var next = 0;
                var counts = new Dictionary<string, int>();
                var maxChain = 0;

                string Fresh() => $"s{seed}-k{next++}";
                void Count(string what) => counts[what] = counts.GetValueOrDefault(what) + 1;

                for (var step = 0; step < steps; step++)
                {
                    var what = $"initial={initial} F={bits} seed={seed} step {step}";
                    var roll = schedule.Next(1000);

                    if (roll < 3)
                    {
                        filter.Reset();
                        live.Clear();
                        Count("reset");
                    }
                    else if (roll < 25)
                    {
                        var payload = filter.ToByteArray();
                        filter = Persistence.FromByteArray<InfiniFilter>(payload);
                        Assert.IsTrue(Same(payload, filter.ToByteArray()), $"{what}: round trip.");
                        Count("reload");
                    }
                    else if (roll < 650)
                    {
                        var s = live.Count > 0 && schedule.Next(8) == 0
                            ? live[schedule.Next(live.Count)]
                            : Fresh();
                        filter.Add(K(s));
                        live.Add(s);
                        Count("add");
                    }
                    else if (live.Count > 0 && schedule.Next(10) > 0)
                    {
                        var index = schedule.Next(live.Count);
                        Assert.IsTrue(filter.TestAndRemove(K(live[index])),
                            $"{what}: removing held '{live[index]}'.");
                        live.RemoveAt(index);
                        Count("remove");
                    }
                    else
                    {
                        var s = Fresh();
                        if (!filter.Test(K(s)))
                        {
                            Assert.IsFalse(filter.TestAndRemove(K(s)), $"{what}: removing absent '{s}'.");
                            Count("absent removal");
                        }
                    }

                    var entries = QuotientFamilyInvariants.CheckInfiniFilter(filter);
                    QuotientFamilyInvariants.AssignEntriesToKeys(what, entries,
                        live.Select(s => (hash(K(s)), 0UL)).ToList());
                    for (var i = 0; i < 3 && live.Count > 0; i++)
                    {
                        var s = live[schedule.Next(live.Count)];
                        Assert.IsTrue(filter.Test(K(s)), $"{what}: held '{s}' not found.");
                    }
                    for (var i = 0; i < 3; i++)
                    {
                        var h = hash(K($"s{seed}-probe-{step}-{i}"));
                        Assert.AreEqual(entries.Any(e => (h & Mask(e.Length)) == e.Suffix),
                            filter.Test(K($"s{seed}-probe-{step}-{i}")),
                            $"{what}: a probe's lookup disagrees with the decoded entries.");
                    }
                    maxChain = Math.Max(maxChain, filter.ChainLength());
                }

                Console.WriteLine($"infini initial={initial} F={bits}: expansions={filter.ExpansionCount()} " +
                    $"maxChain={maxChain} " +
                    string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
                Assert.IsGreaterThanOrEqualTo(2, maxChain,
                    "premise: entries went void and were shed down the chain.");
            }
        }

        // ----------------------------------------------------------------- memento

        /// <summary>
        /// Long churn on small Memento filters over a small key universe, so blocks
        /// are shared and short fingerprints collide, through expansions with removals
        /// interleaved. Every stored memento must be assignable to a live key whose
        /// block hash it remembers truly, and every live key must be found, by point
        /// and by range.
        /// </summary>
        [TestMethod]
        public void TestMementoFilterLongSequencesWithExpansionAndDeletion()
        {
            var cases = new (ulong Range, int Bits, uint Initial, ulong Universe, int Steps, int Seed)[]
            {
                (2, 2, 1, 256, 3000, 401),
                (4, 3, 4, 1024, 3000, 402),
                (16, 4, 16, 8192, 3000, 403),
            };
            var longestChain = 0;

            foreach (var (range, bits, initial, universe, steps, seed) in cases)
            {
                var filter = new MementoFilter(range, bits, initial);
                var mementoBits = filter.MementoBits;
                var live = new List<ulong>();
                var schedule = new Random(seed);
                var counts = new Dictionary<string, int>();
                var maxChain = 0;

                void Count(string what) => counts[what] = counts.GetValueOrDefault(what) + 1;
                (ulong, ulong) Identity(ulong key) =>
                    (MementoFilter.HashPrefix(key >> mementoBits), key & ((1UL << mementoBits) - 1));

                for (var step = 0; step < steps; step++)
                {
                    var what = $"range={range} F={bits} initial={initial} seed={seed} step {step}";
                    var roll = schedule.Next(1000);

                    if (roll < 3)
                    {
                        filter.Reset();
                        live.Clear();
                        Count("reset");
                    }
                    else if (roll < 25)
                    {
                        var payload = filter.ToByteArray();
                        filter = Persistence.FromByteArray<MementoFilter>(payload);
                        Assert.IsTrue(Same(payload, filter.ToByteArray()), $"{what}: round trip.");
                        Count("reload");
                    }
                    else if (roll < 650)
                    {
                        var key = (ulong)schedule.NextInt64((long)universe);
                        filter.Add(key);
                        live.Add(key);
                        Count("add");
                    }
                    else if (live.Count > 0)
                    {
                        var index = schedule.Next(live.Count);
                        Assert.IsTrue(filter.TestAndRemove(live[index]),
                            $"{what}: removing held key {live[index]}.");
                        live.RemoveAt(index);
                        Count("remove");
                    }

                    var entries = QuotientFamilyInvariants.CheckMementoFilter(filter);
                    QuotientFamilyInvariants.AssignEntriesToKeys(what, entries,
                        live.Select(Identity).ToList());
                    for (var i = 0; i < 3 && live.Count > 0; i++)
                    {
                        var key = live[schedule.Next(live.Count)];
                        Assert.IsTrue(filter.Test(key), $"{what}: held key {key} not found.");
                        Assert.IsTrue(filter.TestRange(key == 0 ? 0 : key - 1, key + 1),
                            $"{what}: a range around held key {key} answered empty.");
                    }
                    maxChain = Math.Max(maxChain, filter.ChainLength());
                }

                Console.WriteLine($"memento range={range} F={bits}: expansions={filter.ExpansionCount()} " +
                    $"maxChain={maxChain} " +
                    string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
                Assert.IsGreaterThanOrEqualTo(2u, filter.ExpansionCount(), "premise: the filter expanded.");
                longestChain = Math.Max(longestChain, maxChain);
            }

            // Removal has to choose between tables only once there is more than one, and
            // a filter that never grew a chain would pass this test without ever being
            // asked to.
            Assert.IsGreaterThanOrEqualTo(2, longestChain,
                "premise: some case grew a second table, so a removal had to choose between tables.");
        }

        /// <summary>
        /// Every short sequence on the smallest Memento filter -- four slots, two-bit
        /// fingerprints, one memento bit -- over keys whose block hashes are chosen
        /// rather than drawn: a pair agreeing on every bit an aged box remembers and
        /// not the next, a block sharing a quotient but not a fingerprint, two keys in
        /// one block, and a block in the last slot so clusters wrap. Every state is
        /// held to the box invariants and the assignment model, and every live key must
        /// be found by point and by range.
        /// </summary>
        [TestMethod]
        public void TestMementoFilterEveryShortSequenceThroughItsFirstExpansion()
        {
            ulong Low(ulong p, int bits) => MementoFilter.HashPrefix(p) & ((1UL << bits) - 1);
            var prefixes = Enumerable.Range(0, 4096).Select(p => (ulong)p).ToArray();
            var a = prefixes[0];
            var b = prefixes.First(p => Low(p, 4) == Low(a, 4) && Low(p, 5) != Low(a, 5));
            var c = prefixes.First(p => Low(p, 2) == Low(a, 2) && Low(p, 4) != Low(a, 4));
            var d = prefixes.First(p => Low(p, 2) == 3 && Low(p, 4) != Low(a, 4) && Low(p, 4) != Low(c, 4));
            var e = prefixes.First(p => Low(p, 2) != Low(a, 2) && Low(p, 2) != 3);
            var keys = new[] { a << 1, (a << 1) | 1, b << 1, c << 1, d << 1, e << 1 };
            var alphabet = keys.Length;
            var hashes = keys.Select(k => (MementoFilter.HashPrefix(k >> 1), k & 1)).ToArray();

            var root = new MementoFilter(maxRangeSize: 2, fingerprintBits: 2, initialCapacity: 1);
            var expansionsSeen = 0u;

            (byte[], int[])? Step(byte[] bytes, int[] model, int op)
            {
                var filter = Persistence.FromByteArray<MementoFilter>(bytes);
                model = (int[])model.Clone();
                var what = $"memento op {op} on [{string.Join(",", model)}] keys [{string.Join(",", keys)}]";

                if (op < alphabet)
                {
                    filter.Add(keys[op]);
                    model[op]++;
                }
                else if (op < 2 * alphabet)
                {
                    var id = op - alphabet;
                    if (model[id] == 0)
                    {
                        return null;
                    }
                    Assert.IsTrue(filter.TestAndRemove(keys[id]), $"{what}: removing a held key.");
                    model[id]--;
                }
                else
                {
                    filter.Reset();
                    Array.Clear(model);
                }

                var entries = QuotientFamilyInvariants.CheckMementoFilter(filter);
                var live = new List<(ulong, ulong)>();
                for (var id = 0; id < alphabet; id++)
                {
                    for (var n = 0; n < model[id]; n++)
                    {
                        live.Add(hashes[id]);
                    }
                    if (model[id] > 0 && !(filter.Test(keys[id]) && filter.TestRange(keys[id], keys[id] + 1)))
                    {
                        Assert.Fail($"{what}: held key {keys[id]} is not found.");
                    }
                }
                QuotientFamilyInvariants.AssignEntriesToKeys(what, entries, live);
                expansionsSeen = Math.Max(expansionsSeen, filter.ExpansionCount());
                return (filter.ToByteArray(), model);
            }

            var (states, transitions, depth, closed) = Explore(
                root.ToByteArray(), new int[alphabet], 2 * alphabet + 1, 8, 300_000, Step);

            Console.WriteLine($"memento F=2 M=1: {states} states, {transitions} transitions, depth {depth}" +
                $"{(closed ? " (closed)" : "")}, up to {expansionsSeen} expansions");
            Assert.IsGreaterThanOrEqualTo(1u, expansionsSeen, "premise: the exploration crossed an expansion.");
        }

        /// <summary>
        /// Removing a key takes the matching box with the longest fingerprint, so a key
        /// added before an expansion keeps its only record when a key added after it is
        /// removed.
        /// </summary>
        /// <remarks>
        /// A box that has lived through an expansion has given up fingerprint bits, so
        /// it stands for more blocks than a younger box does. This is the guarantee
        /// reduced to its shortest sequence: a key added before the first expansion, a
        /// key added after it whose block hash agrees with the first's on every bit the
        /// older box still remembers but not on the next, and a removal of the second.
        /// The older box is the shorter fingerprint and so stands for both keys; the
        /// younger stands only for the second. Removing the second must take the
        /// younger. Taking the older leaves the first key with nothing that answers for
        /// it, which is a false negative.
        /// <para>
        /// The rule is the Memento paper's for deletion (section 5), and Infini's
        /// segment removal follows it for the same reason. The keys are found by
        /// searching for the hash relation rather than written as literals, so the
        /// premises below fail if the hash or the geometry moves. The regression test
        /// in TestMementoFilter holds the same scenario with literal keys, and the
        /// sweep above reaches it among everything else it covers.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestMementoRemovalTakesTheLongestMatchingFingerprint()
        {
            // maxRangeSize 2: one memento bit. Two fingerprint bits and four slots,
            // the smallest table, so the first expansion comes at the fourth add.
            var filter = new MementoFilter(maxRangeSize: 2, fingerprintBits: 2, initialCapacity: 1);
            Assert.AreEqual(4UL, filter.Capacity(), "premise: four slots.");

            // Before the expansion a block is addressed by hash bits 0-1 and
            // fingerprinted by bits 2-3; afterwards bits 0-2 and 3-4. So the older
            // box remembers bits 0-3 of its hash, and the younger bits 0-4.
            var prefixes = Enumerable.Range(0, 4096).Select(p => (ulong)p).ToArray();
            ulong Low(ulong p, int bits) => MementoFilter.HashPrefix(p) & ((1UL << bits) - 1);
            var older = prefixes[0];
            var younger = prefixes.First(p => Low(p, 4) == Low(older, 4) && Low(p, 5) != Low(older, 5));
            var fillers = prefixes
                .Where(p => Low(p, 2) != Low(older, 2))
                .GroupBy(p => Low(p, 2)).Select(g => g.First()).Take(2).ToArray();
            Assert.HasCount(2, fillers, "premise: two fillers in other slots.");

            var x = older << 1;
            var y = younger << 1;
            Console.WriteLine($"Add({x}), Add({fillers[0] << 1}), Add({fillers[1] << 1}), " +
                $"Add({y}), TestAndRemove({y}), Test({x})");
            filter.Add(x);
            filter.Add(fillers[0] << 1);
            filter.Add(fillers[1] << 1);
            Assert.AreEqual(0u, filter.ExpansionCount(), "premise: no expansion yet.");
            filter.Add(y);
            Assert.AreEqual(1u, filter.ExpansionCount(), "premise: y arrived after one expansion.");

            Assert.IsTrue(filter.TestAndRemove(y), "y was added, so removing it must succeed.");
            Assert.IsTrue(filter.Test(x),
                $"removing {y} must take the matching box with the longest fingerprint, " +
                $"the younger box that is {y}'s alone. Key {x} was never removed, and its " +
                $"block hash agrees with {y}'s on the bits its older box remembers; it " +
                "tests absent, so the older, shorter box that was its only record was " +
                "taken instead.");
        }

        // ------------------------------------------------------------------ roster

        /// <summary>
        /// Every structure this class sequences, the tests that carry it, and the
        /// operations those tests apply to it. The two tests below hold this table to
        /// the library, so it cannot quietly fall behind it.
        /// </summary>
        private static readonly (Type Structure, string[] Tests, string[] Operations)[] Sequenced =
        {
            (typeof(QuotientFilter),
                new[]
                {
                    nameof(TestQuotientFilterEveryShortSequenceAtTheSmallestTables),
                    nameof(TestQuotientFilterLongSequencesAgainstTheFingerprintModel),
                },
                new[] { "Add", "Merge", "Reset", "TestAndRemove" }),
            (typeof(CuckooBloomFilter),
                new[]
                {
                    nameof(TestCuckooFilterLongSequencesAgainstThePlacementModel),
                    nameof(TestCuckooFilterEveryShortSequenceInATinyTable),
                },
                new[] { "Add", "Reset", "TestAndAdd", "TestAndRemove" }),
            (typeof(InfiniFilter),
                new[]
                {
                    nameof(TestInfiniFilterEveryShortSequenceThroughItsFirstExpansions),
                    nameof(TestInfiniFilterLongSequencesWithExpansionAndDeletion),
                },
                new[] { "Add", "Reset", "TestAndRemove" }),
            (typeof(MementoFilter),
                new[]
                {
                    nameof(TestMementoFilterEveryShortSequenceThroughItsFirstExpansion),
                    nameof(TestMementoFilterLongSequencesWithExpansionAndDeletion),
                    nameof(TestMementoRemovalTakesTheLongestMatchingFingerprint),
                },
                new[] { "Add", "Reset", "TestAndRemove" }),
        };

        /// <summary>
        /// The sweep is about filters that delete by removing a stored entry, so its
        /// roster is every persisted structure offering <c>TestAndRemove</c>, derived
        /// from the library's public surface. A new one fails here until it has a
        /// sequence test or a stated reason not to.
        /// </summary>
        [TestMethod]
        public void TestTheSequenceSweepReachesEveryFilterThatDeletesAnEntry()
        {
            var roster = typeof(QuotientFilter).Assembly.GetTypes()
                .Where(t => t.IsPublic && !t.IsAbstract)
                .Where(t => t.GetInterfaces().Any(i =>
                    i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IBinaryPersistable<>)))
                .Where(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Any(m => m.Name == "TestAndRemove"))
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToArray();

            StructureRoster.AssertCoversEveryType(
                "quotient-family sequence",
                roster,
                Sequenced.Select(s => s.Structure),
                (typeof(CountingBloomFilter),
                    "removal decrements a counter rather than removing a stored entry; " +
                    "its model is the Bloom family's saturating counters"),
                (typeof(DeletableBloomFilter),
                    "removal clears a region's collision mark rather than removing a " +
                    "stored entry; its model is the Bloom family's bits and regions"));

            // A named test that has lost its attribute vouches for nothing.
            foreach (var (structure, tests, _) in Sequenced)
            {
                Assert.IsNotEmpty(tests, $"{structure.Name} names no test that sequences it.");
                foreach (var name in tests)
                {
                    var method = typeof(TestQuotientFamilySequences).GetMethod(name);
                    Assert.IsNotNull(method, $"{structure.Name} names {name}, which does not exist.");
                    Assert.IsNotNull(method.GetCustomAttribute<TestMethodAttribute>(),
                        $"{structure.Name} names {name}, which is not a test.");
                }
            }
        }

        /// <summary>
        /// Every public operation a sequenced filter offers is either applied by its
        /// sequences or classified as something that is not an operation on contents,
        /// so a structure that gains one fails here until a sequence reaches it.
        /// </summary>
        [TestMethod]
        public void TestEveryOperationTheSequencedFiltersOfferIsAccountedFor()
        {
            // Queries, which read the structure and leave it alone. The sequences
            // observe through them rather than apply them.
            var queries = new[]
            {
                "BitsPerSlot", "BucketCount", "Capacity", "ChainLength", "Count",
                "ExpansionCount", "SizeInBytes", "Test", "TestRange",
            };
            // Not operations on contents. Persistence is how every state here is saved
            // and reloaded; SetHash is configuration, whose own sweep is TestSetHash.
            var setHash = "SetHash";
            var writeTo = "WriteTo";

            var offeredAnywhere = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (structure, _, operations) in Sequenced)
            {
                var offered = structure
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => !m.IsSpecialName)
                    .Select(m => m.Name)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                offeredAnywhere.UnionWith(offered);

                var unaccounted = offered
                    .Except(queries).Except(operations).Except(new[] { setHash, writeTo })
                    .ToArray();
                Assert.IsEmpty(unaccounted,
                    $"{structure.Name} offers {string.Join(", ", unaccounted)}, which no " +
                    "sequence here applies and which is not classified as a query. Sequence " +
                    "it, or classify it with the reason it needs no sequence.");

                var stale = operations.Except(offered).ToArray();
                Assert.IsEmpty(stale,
                    $"{structure.Name} is sequenced through {string.Join(", ", stale)}, " +
                    "which it no longer offers; the table is out of date.");

                if (offered.Contains(setHash))
                {
                    Assert.Contains(structure, StructureRoster.WithSetHash,
                        $"{structure.Name} offers SetHash, which is exempt here because " +
                        "TestSetHash sweeps every structure that does; it is not in that sweep's roster.");
                }
            }

            // A classification nobody uses has gone stale like any other exemption.
            var classified = queries.Concat(new[] { setHash, writeTo }).ToArray();
            var unused = classified.Where(name => !offeredAnywhere.Contains(name)).ToArray();
            Assert.IsEmpty(unused,
                $"{string.Join(", ", unused)} is classified as not an operation, but no " +
                "sequenced structure offers it; the classification is out of date.");
        }
    }
}
