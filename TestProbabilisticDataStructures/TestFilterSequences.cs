using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The Bloom family under long, keyed operation sequences, held to exact oracles
    /// after every operation (issue #144).
    /// <para>
    /// The rest of the suite builds a structure one way and asks it one kind of
    /// question. Most of the defects this family has shipped needed a particular
    /// sequence instead: counts that survived a Reset, a scalable filter's Reset that
    /// dropped its hash, removals that hid elements still present, a restored stable
    /// filter that restarted its random sequence. And one gap the suite had until this
    /// file: <c>Buckets.Reset()</c> sizes its storage on its own, apart from the
    /// constructor, and an error there fails no test that reads buckets
    /// <c>0..count-1</c>, because nothing reset a filter and then saved, loaded or
    /// merged it.
    /// </para>
    /// <para>
    /// The oracles, each exact:
    /// </para>
    /// <list type="bullet">
    /// <item><b>The twin.</b> A second instance is given the sequence's canonical
    /// history: the same adds and removals, but no Reset (a new instance stands in),
    /// no save and load (nothing stands in), no merge (the other side's adds stand in)
    /// and no TestAndAdd (Test followed by Add stands in). Its persisted bytes must
    /// equal the subject's after every operation. One comparison covers every field,
    /// so Reset, persistence, Merge and TestAndAdd are each held to the operation they
    /// are documented to be equivalent to, mid-sequence and in every order.</item>
    /// <item><b>A model</b> where the structure's semantics can be restated cheaply:
    /// the counting filter's saturating counters, the deletable filter's bits and
    /// collision regions, the inverse filter's slots. Each restates the double-hashing
    /// index rule rather than calling it, and predicts every answer, false positives
    /// included.</item>
    /// <item><b>The guarantee</b>, independent of both: whatever a filter promises
    /// never to forget, it is asked for after every operation, and its count must match
    /// the ledger.</item>
    /// </list>
    /// <para>
    /// The schedule is keyed, not drawn: the operation at step s of trial t comes from
    /// hashing <c>"{structure}/t{t}/s{s}"</c>, so every failure names the step it
    /// happened at and reproduces from that name alone. No generator is shared across
    /// steps. Every run is a fixed number of steps; nothing here reads a clock.
    /// </para>
    /// <para>
    /// What this does not anchor. The models restate the index rule and the papers as
    /// this repository's author read them, so a misreading would be shared; no
    /// reference outside the repository stands behind them. The twin calls the same
    /// <c>Add</c> as the subject, so a defect in <c>Add</c> itself is left to the
    /// per-structure tests for the filters whose state it alone determines. Scalable
    /// growth has no model, only the twin and the no-false-negative promise, and the
    /// stable filter has no oracle for its answers, since it evicts. Its <c>Reset</c>
    /// restores the generator along with the cells, so a reset filter is held to a new
    /// one byte for byte like every other filter here, generator state included.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestFilterSequences
    {
        private static readonly Func<ReadOnlySpan<byte>, ulong> Hash =
            Defaults.GetDefaultHashFunction();

        private static byte[] K(string s) => Encoding.UTF8.GetBytes(s);

        private static ulong Draw(string stepKey, string salt) => Hash(K($"{stepKey}/{salt}"));

        private static int Pick(string stepKey, string salt, int count) =>
            (int)(Draw(stepKey, salt) % (ulong)count);

        private const int Steps = 1500;
        private const int Trials = 3;

        private enum Op
        {
            Add,
            AddAgain,
            TestAndAdd,
            Remove,
            RemoveAbsent,
            Probe,
            Merge,
            MergeIntoFresh,
            MergeResetSide,
            SaveLoad,
            Reset,
        }

        /// <summary>What a structure can do, and what it promises.</summary>
        private sealed class Spec<T> where T : class, IFilter, IBinaryPersistable<T>
        {
            public string Name;
            public Func<T> Fresh;
            public Func<T, byte[], bool> TestAndRemove;
            public Func<T, T> Reset;
            public Func<T, T, T> Merge;
            public Func<T, ulong> Count;

            /// <summary>
            /// Everything added since the last Reset and not removed tests true. False
            /// for the stable filter, which evicts, and the inverse filter, which
            /// displaces; both still promise the element just added.
            /// </summary>
            public bool NeverForgets;

            public Func<T, Tally, IModel> Model;

            /// <summary>The most adds a merge partner is built from.</summary>
            public int MergeSize = 24;
        }

        /// <summary>
        /// An exact restatement of a structure's semantics. Removal is only ever asked
        /// of a held element, or of one the model predicts absent: removing a false
        /// positive is the one thing the deleting filters document as unsafe.
        /// </summary>
        private interface IModel
        {
            bool Test(string key);
            void Add(string key);
            bool Remove(string key);
            void Merge(IReadOnlyList<string> sideAdds);
            void Reset();
            void AssertMatches(object filter, string where);
        }

        /// <summary>What a run exercised, for the vacuity guards.</summary>
        private sealed class Tally
        {
            private readonly Dictionary<string, int> events = new();

            public void Note(string what) => this.events[what] = this.Of(what) + 1;

            public int Of(string what) => this.events.GetValueOrDefault(what);

            public override string ToString() =>
                string.Join(" ", this.events.OrderBy(e => e.Key, StringComparer.Ordinal)
                    .Select(e => $"{e.Key}={e.Value}"));
        }

        private static (Op Op, int Weight)[] Schedule<T>(Spec<T> spec)
            where T : class, IFilter, IBinaryPersistable<T>
        {
            var table = new List<(Op, int)>
            {
                (Op.Add, 400), (Op.AddAgain, 40), (Op.TestAndAdd, 100), (Op.Probe, 80),
                (Op.SaveLoad, 30),
            };
            if (spec.TestAndRemove is not null)
            {
                table.Add((Op.Remove, 260));
                table.Add((Op.RemoveAbsent, 20));
            }
            if (spec.Merge is not null)
            {
                table.Add((Op.Merge, 25));
                table.Add((Op.MergeIntoFresh, 10));
                table.Add((Op.MergeResetSide, 10));
            }
            if (spec.Reset is not null)
            {
                // Rare enough that a filter fills between resets, common enough that
                // every trial resets several times.
                table.Add((Op.Reset, 5));
            }
            return table.ToArray();
        }

        private static Op Choose((Op Op, int Weight)[] table, string stepKey)
        {
            var u = (int)(Draw(stepKey, "op") % (ulong)table.Sum(e => e.Weight));
            foreach (var (op, weight) in table)
            {
                if (u < weight) return op;
                u -= weight;
            }
            throw new InvalidOperationException("unreachable");
        }

        /// <summary>
        /// A merge partner's adds: some new, some already held, so a merge that
        /// unions and a merge that concatenates disagree about the count.
        /// </summary>
        private static List<string> SideAdds(string stepKey, List<string> live, int most)
        {
            var n = 1 + Pick(stepKey, "n", most);
            var adds = new List<string>(n);
            for (int j = 0; j < n; j++)
            {
                adds.Add(live.Count > 0 && Draw(stepKey, $"overlap{j}") % 4 == 0
                    ? live[Pick(stepKey, $"which{j}", live.Count)]
                    : $"{stepKey}/side{j}");
            }
            return adds;
        }

        private static void AssertSameFrame(string where, byte[] expected, byte[] actual,
            string what, Queue<string> recent)
        {
            if (expected.AsSpan().SequenceEqual(actual)) return;

            var first = 0;
            while (first < Math.Min(expected.Length, actual.Length)
                && expected[first] == actual[first])
            {
                first++;
            }
            Assert.Fail($"{where}: {what} Expected {expected.Length} bytes and got " +
                $"{actual.Length}, first differing at byte {first}. Last operations: " +
                string.Join(", ", recent));
        }

        /// <summary>
        /// Runs one keyed sequence against the subject, its twin, its model and its
        /// guarantee, checking all of them after every operation.
        /// </summary>
        private static void Run<T>(Spec<T> spec, string config, int trial, Tally tally)
            where T : class, IFilter, IBinaryPersistable<T>
        {
            var table = Schedule(spec);
            var prefix = $"{spec.Name}[{config}]/t{trial}";
            var subject = spec.Fresh();
            var twin = spec.Fresh();
            var model = spec.Model?.Invoke(subject, tally);

            // The multiset the structure holds by its own contract: everything added
            // since the last Reset, less what was removed.
            var live = new List<string>();
            var recent = new Queue<string>();

            // True while the subject's storage is the allocation a Reset made: a load
            // or a merge into a new filter replaces it.
            var onResetStorage = false;
            var next = 0;

            for (int step = 0; step < Steps; step++)
            {
                var stepKey = $"{prefix}/s{step}";
                var op = Choose(table, stepKey);
                if (live.Count == 0 && op is Op.AddAgain or Op.Remove) op = Op.Add;
                if (model is null && op is Op.RemoveAbsent) op = Op.Probe;

                string key = null;
                var where = $"{prefix} step {step} ({op})";

                try
                {
                    switch (op)
                    {
                        case Op.Add:
                        case Op.AddAgain:
                            key = op == Op.Add
                                ? $"{prefix}/k{next++}"
                                : live[Pick(stepKey, "again", live.Count)];
                            subject.Add(K(key));
                            twin.Add(K(key));
                            model?.Add(key);
                            live.Add(key);
                            break;

                        case Op.TestAndAdd:
                            key = live.Count > 0 && Draw(stepKey, "held") % 2 == 0
                                ? live[Pick(stepKey, "again", live.Count)]
                                : $"{prefix}/k{next++}";
                            var before = model?.Test(key) ?? twin.Test(K(key));
                            Assert.AreEqual(before, subject.TestAndAdd(K(key)),
                                $"{where}: TestAndAdd('{key}') must answer what Test " +
                                "answered just before it.");
                            twin.Add(K(key));
                            model?.Add(key);
                            live.Add(key);
                            break;

                        case Op.Remove:
                            var at = Pick(stepKey, "remove", live.Count);
                            key = live[at];
                            live.RemoveAt(at);
                            var predicted = model?.Remove(key) ?? true;
                            Assert.IsTrue(predicted,
                                $"{where}: the model denies '{key}', which is held. The " +
                                "model is wrong, or the structure's contract is.");
                            Assert.IsTrue(spec.TestAndRemove(subject, K(key)),
                                $"{where}: '{key}' is held, so removing it must succeed.");
                            Assert.IsTrue(spec.TestAndRemove(twin, K(key)));
                            break;

                        case Op.RemoveAbsent:
                            key = $"{stepKey}/absent";
                            if (model.Test(key))
                            {
                                // A false positive: removing it would be the one unsafe
                                // removal, so it is only asked about.
                                tally.Note("predicted false positive");
                                Assert.IsTrue(subject.Test(K(key)),
                                    $"{where}: the model predicts '{key}' collides.");
                                break;
                            }
                            Assert.IsFalse(spec.TestAndRemove(subject, K(key)),
                                $"{where}: '{key}' was never added and its positions are " +
                                "not all occupied, so removing it must report false.");
                            tally.Note("absent removal refused");
                            break;

                        case Op.Probe:
                            key = $"{stepKey}/absent";
                            var expected = model?.Test(key) ?? twin.Test(K(key));
                            if (expected) tally.Note("predicted false positive");
                            Assert.AreEqual(expected, subject.Test(K(key)),
                                $"{where}: '{key}' was never added; the answer is " +
                                "predicted exactly, false positives included.");
                            break;

                        case Op.Merge:
                        case Op.MergeResetSide:
                            var side = spec.Fresh();
                            if (op == Op.MergeResetSide)
                            {
                                foreach (var s in SideAdds($"{stepKey}/discarded", live, spec.MergeSize))
                                {
                                    side.Add(K(s));
                                }
                                side = spec.Reset(side);
                            }
                            var adds = SideAdds(stepKey, live, spec.MergeSize);
                            foreach (var s in adds) side.Add(K(s));

                            if (onResetStorage) tally.Note("merge into a reset filter");
                            subject = spec.Merge(subject, side);
                            foreach (var s in adds) twin.Add(K(s));
                            model?.Merge(adds);
                            live.AddRange(adds);
                            break;

                        case Op.MergeIntoFresh:
                            // The other direction: the subject is the argument, merged
                            // into a filter that has never held anything.
                            subject = spec.Merge(spec.Fresh(), subject);
                            onResetStorage = false;
                            break;

                        case Op.SaveLoad:
                            if (onResetStorage) tally.Note("save and load after reset");
                            subject = Persistence.FromByteArray<T>(subject.ToByteArray());
                            onResetStorage = false;
                            break;

                        case Op.Reset:
                            subject = spec.Reset(subject);
                            var fresh = spec.Fresh().ToByteArray();
                            var reset = subject.ToByteArray();
                            AssertSameFrame(where, fresh, reset,
                                "Reset must return the structure to the state a new one " +
                                "starts in, byte for byte.", recent);
                            twin = spec.Fresh();
                            model?.Reset();
                            live.Clear();
                            onResetStorage = true;
                            break;
                    }
                }
                catch (Exception e) when (e is not AssertFailedException)
                {
                    Assert.Fail($"{where}: threw {e.GetType().Name}: {e.Message} " +
                        "Last operations: " + string.Join(", ", recent));
                }

                tally.Note(op.ToString());
                recent.Enqueue(key is null ? op.ToString() : $"{op}({key})");
                if (recent.Count > 8) recent.Dequeue();

                AssertSameFrame(where, twin.ToByteArray(), subject.ToByteArray(),
                    "the structure differs from its twin, which saw the same history " +
                    "without Reset, persistence, Merge or TestAndAdd.", recent);

                if (spec.Count is not null)
                {
                    Assert.AreEqual((ulong)live.Count, spec.Count(subject),
                        $"{where}: the count must match the ledger of adds and removals " +
                        "since the last Reset.");
                }

                model?.AssertMatches(subject, where);

                if (spec.NeverForgets)
                {
                    // The cheap half every step, the full sweep whenever the structure
                    // was rebuilt or replaced, and periodically.
                    if (key is not null && live.Contains(key))
                    {
                        Assert.IsTrue(subject.Test(K(key)),
                            $"{where}: '{key}' is held and was denied.");
                    }
                    if (op is not (Op.Add or Op.AddAgain or Op.TestAndAdd or Op.Probe
                        or Op.RemoveAbsent) || step % 50 == 0)
                    {
                        foreach (var s in live)
                        {
                            Assert.IsTrue(subject.Test(K(s)),
                                $"{where}: '{s}' is held and was denied.");
                        }
                    }
                }
                else if (op is Op.Add or Op.AddAgain or Op.TestAndAdd)
                {
                    Assert.IsTrue(subject.Test(K(key)),
                        $"{where}: '{key}' was the last thing added and was denied.");
                }
            }
        }

        /// <summary>
        /// A spec runs exactly the operations its type offers. Derived from the public
        /// surface by reflection, so a structure that gains one fails here until its
        /// spec runs it, and a spec cannot quietly stop running one it still has.
        /// </summary>
        private static void AssertSpecRunsWhatTheTypeOffers<T>(Spec<T> spec)
            where T : class, IFilter, IBinaryPersistable<T>
        {
            var offered = typeof(T).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Select(m => m.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var (name, runs) in new[]
            {
                ("Reset", spec.Reset is not null),
                ("Merge", spec.Merge is not null),
                ("TestAndRemove", spec.TestAndRemove is not null),
            })
            {
                Assert.AreEqual(offered.Contains(name), runs,
                    offered.Contains(name)
                        ? $"{typeof(T).Name} offers {name} and its sequence spec does not run it."
                        : $"{typeof(T).Name} has no {name} and its sequence spec runs one.");
            }

            // The only removal the sequences know how to run is TestAndRemove.
            Assert.IsFalse(offered.Contains("Remove"),
                $"{typeof(T).Name} offers Remove, which no sequence runs. Teach the " +
                "schedule about it.");
        }

        private static Tally RunAll<T>(Spec<T> spec, string config)
            where T : class, IFilter, IBinaryPersistable<T>
        {
            AssertSpecRunsWhatTheTypeOffers(spec);

            var tally = new Tally();
            for (int t = 0; t < Trials; t++)
            {
                Run(spec, config, t, tally);
            }
            Console.WriteLine($"{spec.Name}[{config}]: {tally}");
            return tally;
        }

        /// <summary>
        /// The guards every sequence shares: each operation the structure offers was
        /// run, and the orderings the Reset gap needs -- a reset filter then saved, a
        /// reset filter then merged into -- actually occurred.
        /// </summary>
        private static void AssertExercised<T>(Spec<T> spec, string config, Tally tally)
            where T : class, IFilter, IBinaryPersistable<T>
        {
            foreach (var (op, _) in Schedule(spec))
            {
                Assert.IsGreaterThan(0, tally.Of(op.ToString()),
                    $"{spec.Name}[{config}] never ran {op}.");
            }
            if (spec.Reset is not null)
            {
                Assert.IsGreaterThan(0, tally.Of("save and load after reset"),
                    $"{spec.Name}[{config}] never saved a reset filter.");
            }
            if (spec.Reset is not null && spec.Merge is not null)
            {
                Assert.IsGreaterThan(0, tally.Of("merge into a reset filter"),
                    $"{spec.Name}[{config}] never merged into a reset filter.");
            }
        }

        /// <summary>
        /// The double-hashing index rule, restated rather than called: the 64-bit
        /// hash split into 32-bit halves, position i at (lower + upper * i) mod m in
        /// 32-bit arithmetic.
        /// </summary>
        private static uint[] Positions(string key, uint k, uint m)
        {
            var h = Hash(K(key));
            uint lower = (uint)h, upper = (uint)(h >> 32);
            var positions = new uint[k];
            for (uint i = 0; i < k; i++)
            {
                positions[i] = unchecked(lower + upper * i) % m;
            }
            return positions;
        }

        // ------------------------------------------------------------------ the OR filters

        /// <summary>
        /// The OR filters' state is a function of the set added since the last Reset,
        /// and their count of how many adds that was, so the twin alone is an exact
        /// oracle: any operation sequence must land on the bytes its adds alone give.
        /// The coarse configuration (k = 1, a few dozen bits) is where a Reset that
        /// allocates the wrong length is the whole array rather than a rounding.
        /// </summary>
        [TestMethod]
        public void TestBloomFilterSequencesMatchTheirTwin()
        {
            foreach (var (n, p) in new[] { (1000u, 0.01), (20u, 0.5) })
            {
                var spec = new Spec<BloomFilter>
                {
                    Name = nameof(BloomFilter),
                    Fresh = () => new BloomFilter(n, p),
                    Reset = f => f.Reset(),
                    Merge = (a, b) => a.Merge(b),
                    Count = f => f.Count(),
                    NeverForgets = true,
                };
                var config = $"n={n},p={p}";
                AssertExercised(spec, config, RunAll(spec, config));
            }
        }

        [TestMethod]
        public void TestBloomFilter64SequencesMatchTheirTwin()
        {
            foreach (var (n, p) in new[] { (1000ul, 0.01), (20ul, 0.5) })
            {
                var spec = new Spec<BloomFilter64>
                {
                    Name = nameof(BloomFilter64),
                    Fresh = () => new BloomFilter64(n, p),
                    Reset = f => f.Reset(),
                    Merge = (a, b) => a.Merge(b),
                    Count = f => f.Count(),
                    NeverForgets = true,
                };
                var config = $"n={n},p={p}";
                AssertExercised(spec, config, RunAll(spec, config));
            }
        }

        [TestMethod]
        public void TestPartitionedBloomFilterSequencesMatchTheirTwin()
        {
            foreach (var (n, p) in new[] { (1000u, 0.01), (20u, 0.5) })
            {
                var spec = new Spec<PartitionedBloomFilter>
                {
                    Name = nameof(PartitionedBloomFilter),
                    Fresh = () => new PartitionedBloomFilter(n, p),
                    Reset = f => f.Reset(),
                    Merge = (a, b) => a.Merge(b),
                    Count = f => f.Count(),
                    NeverForgets = true,
                };
                var config = $"n={n},p={p}";
                AssertExercised(spec, config, RunAll(spec, config));
            }
        }

        // ------------------------------------------------------------------ counting

        /// <summary>
        /// Saturating counters, restated: an add holds at the maximum, a removal
        /// leaves a counter at the maximum alone, a merge adds and holds.
        /// </summary>
        private sealed class CountingModel : IModel
        {
            private readonly int[] counters;
            private readonly uint m, k;
            private readonly int max;
            private readonly Tally tally;

            public CountingModel(uint m, uint k, int bits, Tally tally)
            {
                this.counters = new int[m];
                this.m = m;
                this.k = k;
                this.max = (1 << bits) - 1;
                this.tally = tally;
            }

            public bool Test(string key) =>
                Positions(key, this.k, this.m).All(i => this.counters[i] > 0);

            public void Add(string key)
            {
                foreach (var i in Positions(key, this.k, this.m))
                {
                    if (this.counters[i] == this.max) this.tally.Note("add to a saturated counter");
                    this.counters[i] = Math.Min(this.counters[i] + 1, this.max);
                }
            }

            public bool Remove(string key)
            {
                if (!this.Test(key)) return false;
                foreach (var i in Positions(key, this.k, this.m))
                {
                    if (this.counters[i] < this.max)
                    {
                        this.counters[i]--;
                    }
                    else
                    {
                        this.tally.Note("removal kept a saturated counter");
                    }
                }
                return true;
            }

            public void Merge(IReadOnlyList<string> sideAdds)
            {
                // The other side's counters as its own adds left them, then summed
                // into these and held at the maximum.
                var side = new int[this.m];
                foreach (var key in sideAdds)
                {
                    foreach (var i in Positions(key, this.k, this.m))
                    {
                        side[i] = Math.Min(side[i] + 1, this.max);
                    }
                }
                for (int i = 0; i < this.m; i++)
                {
                    if (this.counters[i] + side[i] > this.max)
                    {
                        this.tally.Note("merge saturated a counter");
                    }
                    this.counters[i] = Math.Min(this.counters[i] + side[i], this.max);
                }
            }

            public void Reset() => Array.Clear(this.counters);

            public void AssertMatches(object filter, string where)
            {
                var buckets = ((CountingBloomFilter)filter).Buckets;
                for (uint i = 0; i < this.m; i++)
                {
                    if (buckets.Get(i) != this.counters[i])
                    {
                        Assert.Fail($"{where}: counter {i} holds {buckets.Get(i)} and the " +
                            $"model says {this.counters[i]}.");
                    }
                }
            }
        }

        /// <summary>
        /// The counting filter at the widths that behave differently. Four bits never
        /// saturate at this load, so every removal is an exact inverse; two bits
        /// saturate constantly, so the rule that a full counter is never decremented is
        /// exercised under churn, merge and reload; eight bits are the only width where
        /// a merge's sum can pass the byte a counter is cast to, which takes the
        /// smallest table (n = 1, k = 1) and merge partners of hundreds of adds to
        /// reach between resets.
        /// </summary>
        [TestMethod]
        public void TestCountingBloomFilterSequencesMatchTheirModel()
        {
            foreach (var (n, b, p, mergeSize, saturates) in new[]
            {
                (1000u, (byte)4, 0.01, 24, false),
                (200u, (byte)2, 0.1, 24, true),
                (1u, (byte)8, 0.5, 400, true),
            })
            {
                var spec = new Spec<CountingBloomFilter>
                {
                    Name = nameof(CountingBloomFilter),
                    Fresh = () => new CountingBloomFilter(n, b, p),
                    TestAndRemove = (f, key) => f.TestAndRemove(key),
                    Reset = f => f.Reset(),
                    Merge = (a, c) => a.Merge(c),
                    Count = f => f.Count(),
                    NeverForgets = true,
                    Model = (f, tally) => new CountingModel(f.Capacity(), f.K(), b, tally),
                    MergeSize = mergeSize,
                };
                var config = $"n={n},b={b},p={p}";
                var tally = RunAll(spec, config);
                AssertExercised(spec, config, tally);

                if (saturates)
                {
                    Assert.IsGreaterThan(0, tally.Of("removal kept a saturated counter"),
                        $"{config}: no removal met a saturated counter, so the rule " +
                        "that prevents false negatives was never exercised.");
                    Assert.IsGreaterThan(0, tally.Of("merge saturated a counter"),
                        $"{config}: no merge carried a counter past its maximum, so " +
                        "the clamp on a merged sum was never exercised.");
                }
                else
                {
                    Assert.AreEqual(0, tally.Of("add to a saturated counter"),
                        $"{config} is the exact-inverse configuration and saturated.");
                }
            }
        }

        // ------------------------------------------------------------------ deletable

        /// <summary>
        /// Bits and collision regions, restated from the paper: an add that finds a
        /// position already set marks its region; a removal clears only the positions
        /// in regions never marked. Regions are ceil(M / r) bits wide.
        /// </summary>
        private sealed class DeletableModel : IModel
        {
            private readonly bool[] bits;
            private readonly bool[] collided;
            private readonly uint m, k, regionSize;
            private readonly Tally tally;

            public DeletableModel(uint m, uint r, uint k, Tally tally)
            {
                this.bits = new bool[m];
                this.collided = new bool[r];
                this.m = m;
                this.k = k;
                this.regionSize = (m + r - 1) / r;
                this.tally = tally;
            }

            public bool Test(string key) =>
                Positions(key, this.k, this.m).All(i => this.bits[i]);

            public void Add(string key)
            {
                foreach (var i in Positions(key, this.k, this.m))
                {
                    if (this.bits[i]) this.collided[i / this.regionSize] = true;
                    else this.bits[i] = true;
                }
            }

            public bool Remove(string key)
            {
                if (!this.Test(key)) return false;
                var cleared = false;
                foreach (var i in Positions(key, this.k, this.m))
                {
                    if (!this.collided[i / this.regionSize])
                    {
                        this.bits[i] = false;
                        cleared = true;
                    }
                }
                this.tally.Note(cleared ? "removal cleared bits" : "removal cleared nothing");
                return true;
            }

            public void Merge(IReadOnlyList<string> sideAdds) =>
                throw new NotSupportedException("the deletable filter does not merge");

            public void Reset()
            {
                Array.Clear(this.bits);
                Array.Clear(this.collided);
            }

            public void AssertMatches(object filter, string where)
            {
                var f = (DeletableBloomFilter)filter;
                for (uint i = 0; i < this.m; i++)
                {
                    if ((f.Buckets.Get(i) != 0) != this.bits[i])
                    {
                        Assert.Fail($"{where}: bit {i} is {f.Buckets.Get(i)} and the model " +
                            $"says {(this.bits[i] ? 1 : 0)}.");
                    }
                }
                for (uint j = 0; j < this.collided.Length; j++)
                {
                    if ((f.Collisions.Get(j) != 0) != this.collided[j])
                    {
                        Assert.Fail($"{where}: collision region {j} is {f.Collisions.Get(j)} " +
                            $"and the model says {(this.collided[j] ? 1 : 0)}.");
                    }
                }
            }
        }

        /// <summary>
        /// The deletable filter at a realistic size whose data region does not divide
        /// evenly into its regions (the ceil rounding the region index depends on), at
        /// one region (any collision makes everything undeletable), and with more
        /// regions than data bits.
        /// </summary>
        [TestMethod]
        public void TestDeletableBloomFilterSequencesMatchTheirModel()
        {
            foreach (var (n, r, p) in new[] { (1000u, 10u, 0.01), (100u, 1u, 0.1), (20u, 20u, 0.5) })
            {
                var spec = new Spec<DeletableBloomFilter>
                {
                    Name = nameof(DeletableBloomFilter),
                    Fresh = () => new DeletableBloomFilter(n, r, p),
                    TestAndRemove = (f, key) => f.TestAndRemove(key),
                    Reset = f => f.Reset(),
                    Count = f => f.Count(),
                    NeverForgets = true,
                    Model = (f, tally) => new DeletableModel(f.Capacity(), r, f.K(), tally),
                };
                var config = $"n={n},r={r},p={p}";
                var tally = RunAll(spec, config);
                AssertExercised(spec, config, tally);

                Assert.IsGreaterThan(0, tally.Of("removal cleared nothing"),
                    $"{config}: every removal cleared a bit, so collision regions never " +
                    "protected anything.");
                if (r > 1)
                {
                    Assert.IsGreaterThan(0, tally.Of("removal cleared bits"),
                        $"{config}: no removal cleared a bit, so nothing was deletable.");
                }
            }
        }

        // ------------------------------------------------------------------ scalable

        /// <summary>
        /// The scalable filter's state depends on arrival order -- growth happens when
        /// the newest filter's estimated fill crosses a line -- so its twin is the same
        /// adds in the same order. Hinted small so every trial grows several times, and
        /// across save and load. A growing filter's next filter is sized by a factor
        /// the payload has to carry, so a save and a load that dropped it would show up
        /// at the first filter added after the load; a Reset that dropped it, at the
        /// first after the Reset.
        /// </summary>
        [TestMethod]
        public void TestScalableBloomFilterSequencesMatchTheirTwin()
        {
            foreach (var (hint, p, ratio, growth) in new[]
            {
                (50u, 0.01, 0.8, 1u), (10u, 0.5, 0.5, 1u), (10u, 0.5, 0.5, 2u), (5u, 0.01, 0.8, 4u),
            })
            {
                var spec = new Spec<ScalableBloomFilter>
                {
                    Name = nameof(ScalableBloomFilter),
                    Fresh = () => new ScalableBloomFilter(hint, p, ratio, growth),
                    Reset = f => f.Reset(),
                    NeverForgets = true,
                };
                var config = $"hint={hint},p={p},r={ratio},s={growth}";
                AssertExercised(spec, config, RunAll(spec, config));
            }
        }

        // ------------------------------------------------------------------ stable

        /// <summary>
        /// The stable filter forgets by design and draws which cells to decay from a
        /// seeded generator, so there is no model of its answers -- only of its
        /// history. Its twin sees every add in order and is replaced by a new filter
        /// when the subject is reset, so the two must agree byte for byte across any
        /// number of saves and loads; the element just added must test true; and Reset
        /// must return the filter to a new one's state, generator included.
        /// </summary>
        [TestMethod]
        public void TestStableBloomFilterSequencesMatchTheirTwin()
        {
            var configs = new (string Name, Func<StableBloomFilter> Fresh)[]
            {
                ("m=1000,d=2,p=0.01", () => new StableBloomFilter(1000, 2, 0.01, seed: 7)),
                ("m=50,d=1,p=0.5", () => new StableBloomFilter(50, 1, 0.5, seed: 11)),
                ("unstable m=200", () => StableBloomFilter.NewUnstableBloomFilter(200, 0.01)),
            };
            foreach (var (config, fresh) in configs)
            {
                var spec = new Spec<StableBloomFilter>
                {
                    Name = nameof(StableBloomFilter),
                    Fresh = fresh,
                    Reset = f => f.Reset(),
                };
                AssertExercised(spec, config, RunAll(spec, config));
            }
        }

        // ------------------------------------------------------------------ inverse

        /// <summary>
        /// One slot per hash value mod capacity, holding the last element that landed
        /// there: present exactly when it is still the occupant.
        /// </summary>
        private sealed class InverseModel : IModel
        {
            private readonly Dictionary<uint, string> slots = new();
            private readonly uint capacity;
            private readonly Tally tally;

            public InverseModel(uint capacity, Tally tally)
            {
                this.capacity = capacity;
                this.tally = tally;
            }

            private uint Slot(string key) => (uint)(Hash(K(key)) & 0xffffffff) % this.capacity;

            public bool Test(string key) =>
                this.slots.TryGetValue(this.Slot(key), out var held) && held == key;

            public void Add(string key)
            {
                var slot = this.Slot(key);
                if (this.slots.TryGetValue(slot, out var held) && held != key)
                {
                    this.tally.Note("displaced");
                }
                this.slots[slot] = key;
            }

            public bool Remove(string key) =>
                throw new NotSupportedException("the inverse filter does not remove");

            public void Merge(IReadOnlyList<string> sideAdds) =>
                throw new NotSupportedException("the inverse filter does not merge");

            public void Reset() =>
                throw new NotSupportedException("the inverse filter does not reset");

            public void AssertMatches(object filter, string where)
            {
                var f = (InverseBloomFilter)filter;
                foreach (var held in this.slots.Values)
                {
                    Assert.IsTrue(f.Test(K(held)),
                        $"{where}: '{held}' is its slot's last occupant and was denied.");
                }
            }
        }

        /// <summary>
        /// The inverse filter never reports what it has not seen and forgets what is
        /// displaced. Small enough that displacement is constant, and at capacity 1,
        /// where every add displaces.
        /// </summary>
        [TestMethod]
        public void TestInverseBloomFilterSequencesMatchTheirModel()
        {
            foreach (var capacity in new[] { 64u, 1u })
            {
                var spec = new Spec<InverseBloomFilter>
                {
                    Name = nameof(InverseBloomFilter),
                    Fresh = () => new InverseBloomFilter(capacity),
                    Model = (f, tally) => new InverseModel(f.Capacity(), tally),
                };
                var config = $"capacity={capacity}";
                var tally = RunAll(spec, config);
                AssertExercised(spec, config, tally);
                Assert.IsGreaterThan(0, tally.Of("displaced"),
                    $"{config}: nothing was displaced, so forgetting was never tested.");
            }
        }

        // ------------------------------------------------------------------ roster

        /// <summary>
        /// The sweep is named for the Bloom family, so its roster is every persisted
        /// structure whose id says it is one -- a new Bloom variant fails here until it
        /// has a sequence test or a stated reason not to.
        /// </summary>
        [TestMethod]
        public void TestEveryBloomFilterHasASequenceTest()
        {
            var family = Enum.GetValues<StructureId>()
                .Where(id => id.ToString().Contains("BloomFilter", StringComparison.Ordinal))
                .ToArray();

            var tests = typeof(TestFilterSequences).GetMethods()
                .Where(m => m.GetCustomAttributes(typeof(TestMethodAttribute), false).Length > 0)
                .Select(m => m.Name)
                .ToArray();

            var covered = family.Where(id =>
                tests.Contains($"Test{id}SequencesMatchTheirTwin")
                || tests.Contains($"Test{id}SequencesMatchTheirModel"))
                .ToHashSet();

            // Exemptions are claims, and checked: one naming a structure outside the
            // family, or one the sweep covers anyway, has gone stale.
            var exempt = new Dictionary<StructureId, string>
            {
                [StructureId.CuckooBloomFilter] =
                    "a fingerprint table that refuses inserts and evicts at random; its " +
                    "sequence model belongs with the cuckoo and quotient filters'",
            };

            foreach (var (id, why) in exempt)
            {
                Assert.Contains(id, family, $"the exemption for {id} is outside the family");
                Assert.DoesNotContain(id, covered,
                    $"{id} is exempt ({why}) and covered anyway; the exemption is stale");
            }

            var missing = family.Where(id => !covered.Contains(id) && !exempt.ContainsKey(id))
                .ToArray();
            Assert.IsEmpty(missing,
                "the Bloom-family sequence sweep does not reach " +
                string.Join(", ", missing) + ". Give it a sequence test here, or exempt " +
                "it with the reason it cannot have one.");
        }
    }
}
