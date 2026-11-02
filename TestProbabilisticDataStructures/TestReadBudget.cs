using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// A read allocates no more than its budget allows, and says so before it would.
    /// </summary>
    /// <remarks>
    /// Once every reader loaded everything its constructor builds, a few bytes could ask a
    /// reader for gigabytes: a cuckoo filter's table, a VarOpt sample's regions, a
    /// quotient filter's slots are allocated whole, whatever the payload holds. The read
    /// budget bounds that. Each reader charges what it is about to allocate against one
    /// allowance for the whole read, before allocating it.
    /// <para>
    /// The charges are only worth anything if they are honest upper bounds, so the sweep
    /// here finds what each structure's read is charged -- the smallest budget it loads
    /// under, by bisection through the public overloads -- and holds the read's measured
    /// allocation to that plus <see cref="Slack"/>. A charge that is missing or too small
    /// fails there, on a structure large enough that what is missing cannot hide in the
    /// slack. Then a header claiming a size larger than the budget is refused, and the
    /// refused read is shown to have allocated less than the budget, which is what
    /// "before allocating" means.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestReadBudget
    {
        /// <summary>
        /// What a read may allocate beyond what it was charged: the fixed objects a read
        /// makes whatever it holds -- the envelope's header and checksum buffers, the
        /// checksum's state, the structure's own object and its small members -- which
        /// are not worth a charge each.
        /// </summary>
        private const long Slack = 4096;

        /// <summary>
        /// The smallest measured allocation a sample may have, so that a charge missing
        /// from it would be larger than the slack it could hide in.
        /// </summary>
        private const long LargeEnough = 16 * Slack;

        /// <summary>The payload begins after the fixed-size envelope header.</summary>
        private const int PayloadStart = 14;

        private static readonly Func<ReadOnlySpan<byte>, ulong> DefaultHash =
            Defaults.GetDefaultHashFunction();

        private static byte[] Key(long i)
        {
            var key = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(key, i);
            return key;
        }

        /// <summary>
        /// One structure to read: its bytes, how to read them under options, with and
        /// without a supplied hash, and how to write what was read.
        /// </summary>
        private sealed record Sample(
            string Name,
            byte[] Bytes,
            Func<byte[], ReadOptions, object> Read,
            Func<byte[], ReadOptions, object> ReadWithHash,
            Func<object, byte[]> Write,
            string RefusesAHashBecause = null,
            string SmallBecause = null);

        private static Sample Of<T>(
            string name, T structure, string refusesAHashBecause = null, string smallBecause = null)
            where T : IBinaryPersistable<T> =>
            new(name,
                structure.ToByteArray(),
                (bytes, options) => Persistence.FromByteArray<T>(bytes, options),
                (bytes, options) => Persistence.FromByteArray<T>(bytes, DefaultHash, options),
                restored => ((T)restored).ToByteArray(),
                refusesAHashBecause,
                smallBecause);

        private static T Filled<T>(T structure, Action<T, byte[]> add, int count)
        {
            for (var i = 0; i < count; i++) add(structure, Key(i));
            return structure;
        }

        /// <summary>
        /// A sample of every structure, sized so its read allocates well past the slack.
        /// Built once per run, on first use.
        /// </summary>
        private static readonly Lazy<Dictionary<StructureId, Sample>> Samples = new(() =>
            new Dictionary<StructureId, Sample>
            {
                [StructureId.BloomFilter] = Of("BloomFilter",
                    Filled(new BloomFilter(100_000, 0.01), (f, k) => f.Add(k), 5000)),
                [StructureId.BloomFilter64] = Of("BloomFilter64",
                    Filled(new BloomFilter64(100_000, 0.01), (f, k) => f.Add(k), 5000)),
                [StructureId.CountingBloomFilter] = Of("CountingBloomFilter",
                    Filled(new CountingBloomFilter(50_000, 4, 0.01), (f, k) => f.Add(k), 5000)),
                [StructureId.DeletableBloomFilter] = Of("DeletableBloomFilter",
                    Filled(new DeletableBloomFilter(100_000, 1000, 0.01), (f, k) => f.Add(k), 5000)),
                [StructureId.PartitionedBloomFilter] = Of("PartitionedBloomFilter",
                    Filled(new PartitionedBloomFilter(100_000, 0.01), (f, k) => f.Add(k), 5000)),
                [StructureId.ScalableBloomFilter] = Of("ScalableBloomFilter",
                    Filled(new ScalableBloomFilter(2000, 0.01, 0.8), (f, k) => f.Add(k), 30_000)),
                [StructureId.StableBloomFilter] = Of("StableBloomFilter",
                    Filled(new StableBloomFilter(200_000, 8, 0.01, seed: 1), (f, k) => f.Add(k), 5000)),
                [StructureId.InverseBloomFilter] = Of("InverseBloomFilter",
                    Filled(new InverseBloomFilter(20_000), (f, k) => f.Add(k), 5000)),
                [StructureId.CuckooBloomFilter] = Of("CuckooBloomFilter",
                    Filled(new CuckooBloomFilter(100_000, 0.01, seed: 1), (f, k) => f.Add(k), 5000)),
                [StructureId.CountMinSketch] = Of("CountMinSketch",
                    Filled(new CountMinSketch(0.0001, 0.01), (s, k) => s.Add(k), 5000)),
                [StructureId.HyperLogLog] = Of("HyperLogLog",
                    Filled(new HyperLogLog(1u << 16), (s, k) => s.Add(k), 5000)),
                [StructureId.TopK] = Of("TopK",
                    Filled(new TopK(0.001, 0.01, 1000), (t, k) => t.Add(k), 5000)),
                [StructureId.MinHashSignature] = Of("MinHashSignature",
                    MinHash.Signature(new[] { "alpha", "beta", "gamma" }, 20_000)),
                [StructureId.BinaryFuseFilter] = Of("BinaryFuseFilter",
                    BinaryFuseFilter.Build(Enumerable.Range(0, 50_000).Select(i => Key(i)))),
                [StructureId.DDSketch] = Of("DDSketch", DenseDDSketch(),
                    refusesAHashBecause: "it holds values rather than bytes, and hashes nothing"),
                [StructureId.HyperLogLogPlus] = Of("HyperLogLogPlus",
                    Filled(new HyperLogLogPlus(16), (s, k) => s.Add(k), 50_000)),
                [StructureId.QuotientFilter] = Of("QuotientFilter",
                    Filled(new QuotientFilter(50_000, 0.001), (f, k) => f.Add(k), 5000)),
                [StructureId.ThetaSketch] = Of("ThetaSketch",
                    Filled(new ThetaSketch(4096), (s, k) => s.Add(k), 100_000)),
                [StructureId.SimHashSignature] = Of("SimHashSignature", new SimHashSignature(42),
                    smallBecause: "it is one 64-bit value, and its payload is all it allocates"),
                [StructureId.CountSketch] = Of("CountSketch",
                    Filled(new CountSketch(0.01, 0.05), (s, k) => s.Add(k), 5000)),
                [StructureId.InvertibleBloomLookupTable] = Of("InvertibleBloomLookupTable",
                    Filled(new InvertibleBloomLookupTable(10_000, 8), (t, k) => t.Add(k), 5000)),
                [StructureId.BloomierFilter] = Of("BloomierFilter",
                    BloomierFilter.Build(Enumerable.Range(0, 20_000)
                        .Select(i => new KeyValuePair<byte[], ulong>(Key(i), (ulong)i % 65536)), 16)),
                [StructureId.HeavyKeeper] = Of("HeavyKeeper",
                    Filled(new HeavyKeeper(500, 4096, seed: 1), (h, k) => h.Add(k), 20_000)),
                [StructureId.VarOpt] = Of("VarOpt",
                    Filled(new VarOpt(5000, 1), (v, k) => v.Add(k), 20_000),
                    refusesAHashBecause: "it keeps items rather than hashing them"),
                [StructureId.UltraLogLog] = Of("UltraLogLog",
                    Filled(new UltraLogLog(16), (s, k) => s.Add(k), 5000)),
                [StructureId.Grafite] = Of("Grafite",
                    Grafite.Build(Enumerable.Range(0, 20_000).Select(i => (ulong)i * 1009), 0.01, 64, seed: 7),
                    refusesAHashBecause: "it holds numbers rather than bytes, and hashes nothing"),
                [StructureId.InfiniFilter] = Of("InfiniFilter",
                    Filled(new InfiniFilter(10_000), (f, k) => f.Add(k), 20_000)),
                [StructureId.MementoFilter] = Of("MementoFilter", FilledMemento(),
                    refusesAHashBecause: "it holds numbers rather than bytes, and hashes nothing"),
                [StructureId.SublimeCountMinSketch] = Of("SublimeCountMinSketch",
                    Filled(new SublimeCountMinSketch(0.01), (s, k) => s.Add(k), 200_000)),
                [StructureId.SetSketch] = Of("SetSketch",
                    Filled(new SetSketch(16_384), (s, k) => s.Add(k), 5000)),
                [StructureId.TupleSketch] = Of("TupleSketch",
                    Filled(new TupleSketch(4096), (s, k) => s.Add(k, 1.0), 100_000)),
                [StructureId.PrivateCountMinSketch] = Of("PrivateCountMinSketch",
                    Filled(new PrivateCountMinSketch(10_000, 5, 1.0, seed: 1), (s, k) => s.Add(k), 5000)),
                [StructureId.DpswSketch] = Of("DpswSketch",
                    Filled(new DpswSketch(2000, 1.0, width: 1024, depth: 3, seed: 1), (s, k) => s.Add(k), 5000)),
            });

        private static DDSketch DenseDDSketch()
        {
            // One value per bucket across several thousand of them, so the stored run
            // is dense: every bucket a reader restores holds a count.
            var sketch = new DDSketch(0.0005);
            for (var i = 0; i < 20_000; i++) sketch.Add(Math.Pow(1.001, i));
            return sketch;
        }

        private static MementoFilter FilledMemento()
        {
            var filter = new MementoFilter(64, 8, initialCapacity: 10_000);
            for (ulong i = 0; i < 20_000; i++) filter.Add(i * 7919);
            return filter;
        }

        public static IEnumerable<object[]> Structures =>
            Enum.GetValues<StructureId>().Select(id => new object[] { id.ToString() });

        /// <summary>Whether a read was refused for its budget rather than anything else.</summary>
        private static bool RefusedForBudget(Func<object> read, out ReadBudgetExceededException why)
        {
            try
            {
                read();
                why = null;
                return false;
            }
            catch (InvalidDataException e) when (e.InnerException is ReadBudgetExceededException budget)
            {
                why = budget;
                return true;
            }
        }

        /// <summary>
        /// The smallest budget a read loads under, by bisection: a read's charges do not
        /// depend on its budget, so it loads under every budget at or above them and
        /// under none below.
        /// </summary>
        private static long Need(Func<ReadOptions, object> read)
        {
            long low = 0, high = 1L << 40;
            Assert.IsFalse(RefusedForBudget(() => read(new ReadOptions(high)), out _),
                "the read was refused even at a terabyte");

            while (low < high)
            {
                var mid = low + (high - low) / 2;
                if (RefusedForBudget(() => read(new ReadOptions(mid)), out _))
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }
            return low;
        }

        /// <summary>
        /// What a read allocates on this thread: the least of several measurements.
        /// </summary>
        /// <remarks>
        /// The counter is exact for what a read allocates, but a collection that lands
        /// mid-read -- and the rest of the suite runs in parallel, so one can -- retires
        /// this thread's allocation context, and the counter can then report a few
        /// kilobytes the read did not ask for. Measured identically twice, a read came to
        /// 240,200 and 248,248 bytes. That noise only ever adds, so the least of a few
        /// repeats is the read's own allocation.
        /// </remarks>
        private static long Allocated(Func<object> read, int repeats = 5)
        {
            var least = long.MaxValue;
            for (var i = 0; i < repeats; i++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var result = read();
                var after = GC.GetAllocatedBytesForCurrentThread();
                GC.KeepAlive(result);
                least = Math.Min(least, after - before);
            }
            return least;
        }

        /// <summary>
        /// Every structure: refused one byte below what its read is charged, read at it,
        /// and allocating no more than it was charged plus the slack.
        /// </summary>
        [TestMethod]
        [DynamicData(nameof(Structures))]
        public void TestEveryStructureReadsWithinAnHonestBudget(string structure)
        {
            var id = Enum.Parse<StructureId>(structure);
            Assert.IsTrue(Samples.Value.TryGetValue(id, out var sample),
                $"{id} has no sample to read under a budget. Add one.");

            AssertHonest(id, sample);
            if (Further.Value.TryGetValue(id, out var further))
            {
                foreach (var (why, more) in further)
                {
                    Assert.IsFalse(string.IsNullOrWhiteSpace(why), $"{id} has a further sample without a reason");
                    AssertHonest(id, more);
                }
            }
        }

        /// <summary>
        /// Further samples, for charges the first sample of a structure makes too small to
        /// see: each is there so that one charge in particular is larger than the slack,
        /// and says which.
        /// </summary>
        private static readonly Lazy<Dictionary<StructureId, (string Why, Sample Sample)[]>> Further = new(() =>
            new Dictionary<StructureId, (string, Sample)[]>
            {
                [StructureId.HyperLogLogPlus] = new[]
                {
                    ("the sparse buffer: the first sample is dense",
                        Of("HyperLogLogPlus sparse",
                            Filled(new HyperLogLogPlus(18), (s, k) => s.Add(k), 30_000))),
                },
                [StructureId.PartitionedBloomFilter] = new[]
                {
                    ("the array of partitions and an object each, at the most partitions there are",
                        Of("PartitionedBloomFilter at k = 1022",
                            new PartitionedBloomFilter(1000, 2.2250738585072014E-308))),
                },
                [StructureId.ScalableBloomFilter] = new[]
                {
                    // Growth 1: a filter that adds every filter at its hint is the one
                    // that reaches thousands of them.
                    ("the list of contained filters and what each nested read costs, over two thousand of them",
                        Of("ScalableBloomFilter of many filters",
                            Filled(new ScalableBloomFilter(10, 0.01, 0.999, 1), (f, k) => f.Add(k), 20_000))),
                },
                [StructureId.Grafite] = new[]
                {
                    ("the words of high bits and their select index",
                        Of("Grafite of 200,000 keys",
                            Grafite.Build(Enumerable.Range(0, 200_000).Select(i => (ulong)i * 1009), 0.01, 64, seed: 7),
                            refusesAHashBecause: "it holds numbers rather than bytes, and hashes nothing")),
                },
                [StructureId.DpswSketch] = new[]
                {
                    ("the checkpoints, rebuilt from a substream of twenty thousand items",
                        Of("DpswSketch over a wide window",
                            Filled(new DpswSketch(200_000, 1.0, width: 64, depth: 1, seed: 1), (s, k) => s.Add(k), 1000))),
                },
            });

        private static void AssertHonest(StructureId id, Sample sample)
        {
            var bytes = sample.Bytes;

            // Warmed first, so that what is measured is the read and not the first call's
            // type initialization.
            sample.Read(bytes, ReadOptions.Unlimited);

            var need = Need(options => sample.Read(bytes, options));
            Assert.IsGreaterThan((long)bytes.Length, need,
                $"{sample.Name}'s read was charged {need} bytes for a {bytes.Length}-byte payload; " +
                "a read allocates at least its payload, so it is not being charged");

            // One byte short, the read gets through every charge but its last, so the
            // refusal names the whole of what the read needs.
            Assert.IsTrue(RefusedForBudget(() => sample.Read(bytes, new ReadOptions(need - 1)), out var why),
                $"{sample.Name} loaded a byte under what it was charged");
            Assert.AreEqual(need, why!.RequiredBytes,
                $"{sample.Name}'s refusal one byte short named {why.RequiredBytes} bytes needed");
            Assert.AreEqual(need - 1, why.MemoryBudget);

            var restored = sample.Read(bytes, new ReadOptions(need));
            CollectionAssert.AreEqual(bytes, sample.Write(restored),
                $"{sample.Name} read at its budget does not write the bytes it was read from");

            var allocated = Allocated(() => sample.Read(bytes, new ReadOptions(need)));
            Console.WriteLine(
                $"[READ-BUDGET] {sample.Name}: payload {bytes.Length}, charged {need}, allocated {allocated}");

            Assert.IsLessThanOrEqualTo(need + Slack, allocated,
                $"{sample.Name}'s read allocated {allocated} bytes and was charged {need}: its charges " +
                $"are not an upper bound, by {allocated - need}");

            if (sample.SmallBecause is null)
            {
                Assert.IsGreaterThan(LargeEnough, allocated,
                    $"{sample.Name}'s sample allocates {allocated} bytes, too few for a missing charge " +
                    "to show past the slack. Make it larger.");
            }
            else
            {
                Assert.IsLessThan(LargeEnough, allocated,
                    $"{sample.Name} is exempt from being large because {sample.SmallBecause}, and its " +
                    $"read allocates {allocated} bytes");
            }

            // The overload that takes a hash keeps to the same budget.
            if (sample.RefusesAHashBecause is null)
            {
                Assert.IsTrue(RefusedForBudget(
                        () => sample.ReadWithHash(bytes, new ReadOptions(need - 1)), out _),
                    $"{sample.Name}'s hash overload loaded a byte under its budget");
                Assert.IsFalse(RefusedForBudget(
                        () => sample.ReadWithHash(bytes, new ReadOptions(need)), out _),
                    $"{sample.Name}'s hash overload was refused at its budget");
            }
            else
            {
                var refusal = Assert.ThrowsExactly<InvalidDataException>(
                    () => sample.ReadWithHash(bytes, ReadOptions.Unlimited),
                    $"{sample.Name} is said to refuse a hash because {sample.RefusesAHashBecause}");
                Assert.IsNull(refusal.InnerException,
                    $"{sample.Name}'s refusal of a hash was a budget refusal: {refusal.Message}");
            }
        }

        /// <summary>
        /// The sweep's samples are keyed by <see cref="StructureId"/> and checked against
        /// it, so a structure added without one fails here by name.
        /// </summary>
        [TestMethod]
        public void TestEveryStructureHasABudgetSample()
        {
            StructureRoster.AssertCoversEveryStructure("read budget", Samples.Value.Keys);
        }

        // ---- A header claiming more than the budget is refused, before allocating --

        /// <summary>
        /// The budget these refusals are read under: well above the payloads poked, and
        /// well below what the poked sizes would allocate.
        /// </summary>
        private const long SmallBudget = 1L << 20;

        private static byte[] Poke(byte[] original, int payloadOffset, uint value)
        {
            var bytes = (byte[])original.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(PayloadStart + payloadOffset), value);

            var crc = new Crc32();
            crc.Append(bytes.AsSpan(4, bytes.Length - 8));
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(bytes.Length - 4), crc.GetCurrentHashAsUInt32());
            return bytes;
        }

        /// <summary>
        /// A small payload whose header claims a size the budget does not cover is
        /// refused for its budget, and the refused read allocated less than the budget:
        /// it was refused before the allocation, not by it. The refusal names at least
        /// <paramref name="claimed"/>, what the claimed size would have allocated, worked
        /// out here from the size itself -- so a charge that undercounts the allocation
        /// it stands for fails here, whatever the budget.
        /// </summary>
        private static void AssertRefusedBeforeAllocating<T>(byte[] poked, long claimed)
            where T : IBinaryPersistable<T>
        {
            var options = new ReadOptions(SmallBudget);
            ReadBudgetExceededException why = null;
            var allocated = Allocated(() =>
            {
                RefusedForBudget(() => Persistence.FromByteArray<T>(poked, options), out why);
                return new object();
            });

            Assert.IsNotNull(why, $"a {typeof(T).Name} claiming more than the budget was not refused for it");
            Assert.AreEqual(SmallBudget, why.MemoryBudget);
            Assert.IsGreaterThan(SmallBudget, why.RequiredBytes);
            Assert.IsGreaterThanOrEqualTo(claimed, why.RequiredBytes,
                $"a {typeof(T).Name} claiming {claimed} bytes of allocation was charged {why.RequiredBytes}");
            Assert.IsLessThan(SmallBudget, allocated,
                $"a {typeof(T).Name} refused for its budget had already allocated {allocated} bytes");
        }

        /// <summary>A cuckoo filter's table is allocated whole, whatever it holds.</summary>
        [TestMethod]
        public void TestACuckooTableLargerThanTheBudgetIsRefused()
        {
            // u32 bucket count at 0: 2^22 buckets of four 10-bit fingerprints, 20 MiB.
            var bytes = Filled(new CuckooBloomFilter(1000, 0.01, seed: 1), (f, k) => f.Add(k), 10).ToByteArray();
            // 2^24 entries of ten bits.
            AssertRefusedBeforeAllocating<CuckooBloomFilter>(Poke(bytes, 0, 1u << 22), (4L << 22) * 10 / 8);
        }

        /// <summary>A VarOpt sample's regions are allocated whole, k + 1 slots each.</summary>
        [TestMethod]
        public void TestAVarOptSampleLargerThanTheBudgetIsRefused()
        {
            // u32 k at 0, on a sample that has not begun sampling: five regions of 2^21
            // slots, 80 MiB.
            var bytes = Filled(new VarOpt(100, 1), (v, k) => v.Add(k), 10).ToByteArray();
            AssertRefusedBeforeAllocating<VarOpt>(Poke(bytes, 0, 1u << 21), 5 * 8 * ((1L << 21) + 1));
        }

        /// <summary>A quotient filter's header names its table outright.</summary>
        [TestMethod]
        public void TestAQuotientTableLargerThanTheBudgetIsRefused()
        {
            // u32 quotient bits at 0: 2^24 slots of ten bits, 20 MiB.
            var bytes = Filled(new QuotientFilter(100, 0.01), (f, k) => f.Add(k), 10).ToByteArray();
            // A hundred items at 1% need seven remainder bits, so slots of ten.
            AssertRefusedBeforeAllocating<QuotientFilter>(Poke(bytes, 0, 24), (1L << 24) * 10 / 8);
        }

        /// <summary>The counting filter's scratch buffer is sized by its hash count.</summary>
        [TestMethod]
        public void TestACountingFilterHashCountLargerThanTheBudgetIsRefused()
        {
            // u32 m, then u32 k at 4: 2^24 four-byte indexes, 64 MiB.
            var bytes = new CountingBloomFilter(1000, 4, 0.01).ToByteArray();
            AssertRefusedBeforeAllocating<CountingBloomFilter>(Poke(bytes, 4, 1u << 24), 4L << 24);
        }

        /// <summary>The deletable filter's scratch buffer, likewise.</summary>
        [TestMethod]
        public void TestADeletableFilterHashCountLargerThanTheBudgetIsRefused()
        {
            var bytes = new DeletableBloomFilter(1000, 100, 0.01).ToByteArray();
            AssertRefusedBeforeAllocating<DeletableBloomFilter>(Poke(bytes, 4, 1u << 24), 4L << 24);
        }

        /// <summary>The stable filter's scratch buffer, likewise.</summary>
        [TestMethod]
        public void TestAStableFilterHashCountLargerThanTheBudgetIsRefused()
        {
            var bytes = new StableBloomFilter(1000, 3, 0.01, seed: 1).ToByteArray();
            AssertRefusedBeforeAllocating<StableBloomFilter>(Poke(bytes, 4, 1u << 24), 4L << 24);
        }

        /// <summary>An inverse filter's slots are allocated whole, occupied or not.</summary>
        [TestMethod]
        public void TestAnInverseFilterCapacityLargerThanTheBudgetIsRefused()
        {
            // u32 capacity at 0: 2^22 slots, 32 MiB of references.
            var bytes = Filled(new InverseBloomFilter(100), (f, k) => f.Add(k), 10).ToByteArray();
            AssertRefusedBeforeAllocating<InverseBloomFilter>(Poke(bytes, 0, 1u << 22), 8L << 22);
        }

        /// <summary>A count-min sketch's rows are allocated before they are read.</summary>
        [TestMethod]
        public void TestACountMinSketchWiderThanTheBudgetIsRefused()
        {
            // f64 epsilon, f64 delta, then u32 width at 16: rows of 2^22 counters.
            var bytes = new CountMinSketch(0.1, 0.1).ToByteArray();
            // Delta 0.1 is ceil(ln 10), three rows.
            AssertRefusedBeforeAllocating<CountMinSketch>(Poke(bytes, 16, 1u << 22), 3 * (8L << 22));
        }

        /// <summary>
        /// A size so large its cost does not fit a long -- every row of a matrix two
        /// billion by two billion, the most a private sketch allows of each -- is refused
        /// rather than wrapping to a small one. (A Count-Min Sketch cannot reach this
        /// any more: its depth is at most 710, which its reader checks first.)
        /// </summary>
        [TestMethod]
        public void TestACostTooLargeToCountIsRefusedRatherThanWrapped()
        {
            // u32 width at 0 and u32 depth at 4, both the most an array holds.
            var bytes = new PrivateCountMinSketch(64, 2, 1.0, seed: 1).ToByteArray();
            AssertRefusedBeforeAllocating<PrivateCountMinSketch>(
                Poke(Poke(bytes, 0, (uint)Array.MaxLength), 4, (uint)Array.MaxLength),
                long.MaxValue);
        }

        /// <summary>A private sketch's rows, likewise.</summary>
        [TestMethod]
        public void TestAPrivateSketchWiderThanTheBudgetIsRefused()
        {
            // u32 width at 0.
            var bytes = new PrivateCountMinSketch(64, 2, 1.0, seed: 1).ToByteArray();
            AssertRefusedBeforeAllocating<PrivateCountMinSketch>(Poke(bytes, 0, 1u << 22), 2 * (8L << 22));
        }

        /// <summary>A Sublime sketch's rows, likewise.</summary>
        [TestMethod]
        public void TestASublimeSketchWiderThanTheBudgetIsRefused()
        {
            // Three f64, u32 depth at 24, u32 width at 28. Unexpanded, so no records
            // constrain the width: rows of 2^24 counters.
            var bytes = Filled(new SublimeCountMinSketch(0.1), (s, k) => s.Add(k), 10).ToByteArray();
            // Delta 0.1 is three rows.
            AssertRefusedBeforeAllocating<SublimeCountMinSketch>(Poke(bytes, 28, 1u << 24), 3 * (8L << 24));
        }

        /// <summary>HLL++'s sparse buffer is sized by the count it claims.</summary>
        [TestMethod]
        public void TestAHyperLogLogPlusSparseCountLargerThanTheBudgetIsRefused()
        {
            // u32 precision, u8 representation, then u32 held at 5: eight MiB of hashes.
            var bytes = Filled(new HyperLogLogPlus(14), (s, k) => s.Add(k), 10).ToByteArray();
            AssertRefusedBeforeAllocating<HyperLogLogPlus>(Poke(bytes, 5, 1_000_000), 8L * 1_000_000);
        }

        /// <summary>A 64-bit filter's array of arrays is sized by its count.</summary>
        [TestMethod]
        public void TestABloomFilter64ArrayCountLargerThanTheBudgetIsRefused()
        {
            // u64 m, u32 k, u64 count, then the buckets' u64 count, u8 size and u32
            // array count at 29: a million references.
            var bytes = new BloomFilter64(1000, 0.01).ToByteArray();
            AssertRefusedBeforeAllocating<BloomFilter64>(Poke(bytes, 29, 1_000_000), 8L * 1_000_000);
        }

        // ---- The default, the options, and the refusal --------------------------------

        /// <summary>
        /// The overloads without options read under the default budget: a header claiming
        /// more than it is refused, naming the default. Two gibibytes of slots, claimed
        /// by a few dozen bytes.
        /// </summary>
        [TestMethod]
        public void TestReadingWithoutOptionsAppliesTheDefaultBudget()
        {
            var bytes = Poke(Filled(new InverseBloomFilter(100), (f, k) => f.Add(k), 10).ToByteArray(),
                0, 1u << 28);

            Assert.IsTrue(RefusedForBudget(() => Persistence.FromByteArray<InverseBloomFilter>(bytes), out var why));
            Assert.AreEqual(ReadOptions.DefaultMemoryBudget, why!.MemoryBudget);

            using var stream = new MemoryStream(bytes);
            Assert.IsTrue(RefusedForBudget(() => InverseBloomFilter.ReadFrom(stream, DefaultHash), out why));
            Assert.AreEqual(ReadOptions.DefaultMemoryBudget, why!.MemoryBudget);
        }

        /// <summary>
        /// The refusal is an <see cref="InvalidDataException"/>, which existing callers
        /// already catch, distinguishable from corruption by its inner exception, and it
        /// says what was needed, what was allowed, and how to allow more.
        /// </summary>
        [TestMethod]
        public void TestTheRefusalNamesItsNeedTheBudgetAndTheOverload()
        {
            var bytes = Filled(new BloomFilter(10_000, 0.01), (f, k) => f.Add(k), 100).ToByteArray();

            var refusal = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<BloomFilter>(bytes, new ReadOptions(1000)));
            var why = Assert.IsInstanceOfType<ReadBudgetExceededException>(refusal.InnerException);

            Assert.AreEqual(1000, why.MemoryBudget);
            Assert.IsGreaterThan(1000L, why.RequiredBytes);
            StringAssert.Contains(refusal.Message, why.RequiredBytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
            StringAssert.Contains(refusal.Message, "1,000");
            StringAssert.Contains(refusal.Message, "ReadOptions");
            StringAssert.Contains(refusal.Message, "BloomFilter");
        }

        /// <summary>
        /// The default is a named gibibyte, the unlimited options set no limit, and a
        /// negative budget is no budget at all.
        /// </summary>
        [TestMethod]
        public void TestTheOptions()
        {
            Assert.AreEqual(ReadOptions.DefaultMemoryBudget, ReadOptions.Default.MemoryBudget);
            Assert.IsFalse(ReadOptions.Default.IsUnlimited);
            Assert.IsTrue(ReadOptions.Unlimited.IsUnlimited);
            Assert.AreEqual(long.MaxValue, ReadOptions.Unlimited.MemoryBudget);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReadOptions(-1));
            Assert.AreEqual(0L, new ReadOptions(0).MemoryBudget);
        }

        /// <summary>
        /// The default is stated, as a number, in both documents a caller reads before
        /// loading a file: FORMAT.md, embedded in this assembly, and the README beside it.
        /// The constant is a compile-time one, so this is also what pins its value -- a
        /// change to it fails here until the documents say so.
        /// </summary>
        [TestMethod]
        public void TestTheDefaultBudgetIsDocumented()
        {
            var stated = ReadOptions.DefaultMemoryBudget.ToString(
                "N0", System.Globalization.CultureInfo.InvariantCulture);

            foreach (var document in new[] { "FORMAT.md", "README.md" })
            {
                using var resource = typeof(TestReadBudget).Assembly
                    .GetManifestResourceStream("TestProbabilisticDataStructures." + document);
                Assert.IsNotNull(resource, $"{document} is not embedded in the test assembly");
                StringAssert.Contains(new StreamReader(resource).ReadToEnd(), stated,
                    $"{document} does not state the default read budget");
            }
        }

        /// <summary>
        /// A type outside the library implementing the interface without the budgeted
        /// reads still compiles and loads, and refuses a budgeted read rather than
        /// quietly ignoring the budget.
        /// </summary>
        [TestMethod]
        public void TestATypeWithoutABudgetedReadRefusesOne()
        {
            var bytes = new Outsider().ToByteArray();
            Assert.IsNotNull(Persistence.FromByteArray<Outsider>(bytes));
            Assert.ThrowsExactly<NotSupportedException>(
                () => Persistence.FromByteArray<Outsider>(bytes, ReadOptions.Unlimited));
            Assert.ThrowsExactly<NotSupportedException>(
                () => Persistence.FromByteArray<Outsider>(bytes, DefaultHash, ReadOptions.Unlimited));
        }

        /// <summary>An implementation of the interface as it was before the budget.</summary>
        private sealed class Outsider : IBinaryPersistable<Outsider>
        {
            public void WriteTo(Stream stream) => stream.WriteByte(1);

            public static Outsider ReadFrom(Stream stream)
            {
                stream.ReadByte();
                return new Outsider();
            }

            public static Outsider ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash) =>
                ReadFrom(stream);
        }
    }
}
