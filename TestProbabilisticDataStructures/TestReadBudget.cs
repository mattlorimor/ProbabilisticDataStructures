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
        internal sealed record Sample(
            string Name,
            byte[] Bytes,
            Func<byte[], ReadOptions, object> Read,
            Func<byte[], ReadOptions, object> ReadWithHash,
            Func<Stream, ReadOptions, object> ReadStream,
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
                (stream, options) => T.ReadFrom(stream, options),
                restored => ((T)restored).ToByteArray(),
                refusesAHashBecause,
                smallBecause);

        /// <summary>
        /// A stream that can only be read forward, as a network stream or a pipe is: a
        /// payload too large to hold is priced from one by reading past its runs rather
        /// than seeking past them.
        /// </summary>
        private sealed class OneWay(byte[] bytes) : Stream
        {
            private readonly MemoryStream inner = new(bytes, writable: false);

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => this.inner.Read(buffer, offset, count);
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private static T Filled<T>(T structure, Action<T, byte[]> add, int count)
        {
            for (var i = 0; i < count; i++) add(structure, Key(i));
            return structure;
        }

        /// <summary>
        /// A sample of every structure, sized so its read allocates well past the slack in
        /// either form of the envelope: in the large form, whose payload is read from the
        /// stream rather than held, a read allocates only what the structure keeps. Built
        /// once per run, on first use.
        /// </summary>
        internal static readonly Lazy<Dictionary<StructureId, Sample>> Samples = new(() =>
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
                    BinaryFuseFilter.Build(Enumerable.Range(0, 60_000).Select(i => Key(i)))),
                [StructureId.DDSketch] = Of("DDSketch", DenseDDSketch(),
                    refusesAHashBecause: "it holds values rather than bytes, and hashes nothing"),
                [StructureId.HyperLogLogPlus] = Of("HyperLogLogPlus",
                    Filled(new HyperLogLogPlus(16), (s, k) => s.Add(k), 50_000)),
                [StructureId.QuotientFilter] = Of("QuotientFilter",
                    Filled(new QuotientFilter(50_000, 0.001), (f, k) => f.Add(k), 5000)),
                [StructureId.ThetaSketch] = Of("ThetaSketch",
                    Filled(new ThetaSketch(8192), (s, k) => s.Add(k), 100_000)),
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
                    Grafite.Build(Enumerable.Range(0, 40_000).Select(i => (ulong)i * 1009), 0.01, 64, seed: 7),
                    refusesAHashBecause: "it holds numbers rather than bytes, and hashes nothing"),
                [StructureId.InfiniFilter] = Of("InfiniFilter",
                    Filled(new InfiniFilter(10_000), (f, k) => f.Add(k), 20_000)),
                [StructureId.MementoFilter] = Of("MementoFilter", FilledMemento(),
                    refusesAHashBecause: "it holds numbers rather than bytes, and hashes nothing"),
                [StructureId.SublimeCountMinSketch] = Of("SublimeCountMinSketch",
                    Filled(new SublimeCountMinSketch(0.01), (s, k) => s.Add(k), 400_000)),
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
        internal static long Allocated(Func<object> read, int repeats = 5)
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

        // ---- A refusal names one total, and that total loads -------------------------

        /// <summary>
        /// Every structure, under any budget too small for it, is refused naming the same
        /// number, and a budget of exactly that number loads it the first time. The
        /// number covers what the read allocates, and is what the read is charged when
        /// nothing limits it.
        /// </summary>
        /// <remarks>
        /// The refusal used to name the running total at the charge that failed, so its
        /// own advice failed: a 1.89 GB <see cref="BloomFilter64"/> payload was refused
        /// needing 1,893,049,288 bytes, refused again under exactly that at 1,893,049,496,
        /// then at 3,786,098,576, and loaded at about 3.8 GB. The budgets tried here span
        /// that ladder: nothing, less than the payload, the payload, half the total, and a
        /// byte short of it. The refused reads below the payload's length are also shown
        /// to have allocated less than that budget, so the total was worked out without
        /// holding the payload it describes.
        /// </remarks>
        [TestMethod]
        [DynamicData(nameof(Structures))]
        public void TestEveryStructureIsRefusedNamingOneTotalThatLoads(string structure)
        {
            var id = Enum.Parse<StructureId>(structure);
            Assert.IsTrue(Samples.Value.TryGetValue(id, out var sample),
                $"{id} has no sample to read under a budget. Add one.");

            AssertOneTotal(id, sample);
            if (Further.Value.TryGetValue(id, out var further))
            {
                foreach (var (_, more) in further)
                {
                    AssertOneTotal(id, more);
                }
            }
        }

        /// <summary>
        /// Every structure's sample, written in the large form, is held to the same two
        /// rules: its charges are an honest bound on what its read allocates, and a
        /// refusal names one total that loads, which its price and its reader's charges
        /// both come to.
        /// </summary>
        /// <remarks>
        /// With the threshold at nothing, the envelopes a scalable filter or a top-k holds
        /// are in the large form too, so the price of a nested large envelope, worked out
        /// where it lies in the outer payload, is held to what reading it charges. A price
        /// that did not recognise the flag would stop at the first nested envelope and
        /// leave the rest to be charged unpriced. The further samples are held to it too:
        /// what reading an envelope in the large form allocates around its payload is
        /// charged once per envelope and is a few hundred bytes, which only a read of
        /// thousands of them, a scalable filter's, carries past the slack.
        /// </remarks>
        [TestMethod]
        [DynamicData(nameof(Structures))]
        public void TestEveryStructureInTheLargeFormIsChargedHonestlyAndNamesOneTotal(string structure)
        {
            var id = Enum.Parse<StructureId>(structure);
            Assert.IsTrue(Samples.Value.TryGetValue(id, out var sample),
                $"{id} has no sample to read under a budget. Add one.");

            var large = InTheLargeForm(sample);
            AssertHonest(id, large);
            AssertOneTotal(id, large);

            if (Further.Value.TryGetValue(id, out var further))
            {
                foreach (var (_, more) in further)
                {
                    var largeMore = InTheLargeForm(more);
                    AssertHonest(id, largeMore);
                    AssertOneTotal(id, largeMore);
                }
            }
        }

        /// <summary>
        /// A sample written again in the large form, and writing in it, so that the checks
        /// that a read writes back the bytes it was read from hold in that form.
        /// </summary>
        internal static Sample InTheLargeForm(Sample sample)
        {
            var bytes = LargeForm.Below(0, () => sample.Write(sample.Read(sample.Bytes, ReadOptions.Unlimited)));
            Assert.IsTrue(LargeForm.IsLarge(bytes), $"{sample.Name} was not written in the large form");
            return sample with
            {
                Name = sample.Name + " in the large form",
                Bytes = bytes,
                Write = restored => LargeForm.Below(0, () => sample.Write(restored)),
            };
        }

        private static void AssertOneTotal(StructureId id, Sample sample)
        {
            var bytes = sample.Bytes;
            var payload = (long)bytes.Length - (LargeForm.IsLarge(bytes)
                ? PersistenceFormat.LargeEnvelopeLength
                : PersistenceFormat.EnvelopeLength);
            sample.Read(bytes, ReadOptions.Unlimited);

            Assert.IsTrue(RefusedForBudget(() => sample.Read(bytes, new ReadOptions(0)), out var first),
                $"{sample.Name} loaded under a budget of nothing");
            var total = first!.RequiredBytes;

            foreach (var budget in new[] { 1, payload / 2, payload, total / 2, total - 1 }.Distinct())
            {
                if (budget < 0 || budget >= total)
                {
                    continue;
                }

                Assert.IsTrue(RefusedForBudget(() => sample.Read(bytes, new ReadOptions(budget)), out var why),
                    $"{sample.Name} loaded under {budget} bytes, below the {total} it was refused for needing");
                Assert.AreEqual(total, why!.RequiredBytes,
                    $"{sample.Name} refused under {budget} bytes named {why.RequiredBytes} needed, " +
                    $"and {total} under none");
                Assert.AreEqual(budget, why.MemoryBudget);
            }

            // From a stream that cannot seek, an ordinary payload's runs are read past
            // rather than skipped, and the total is the same. The large form is priced in
            // a pass of its own before it is read, which has to seek back between the two,
            // so from such a stream it is refused for the stream, under any budget (#226).
            if (LargeForm.IsLarge(bytes))
            {
                var unseekable = Assert.ThrowsExactly<NotSupportedException>(
                    () => sample.ReadStream(new OneWay(bytes), new ReadOptions(0)),
                    $"{sample.Name} was not refused for a stream that cannot seek");
                StringAssert.Contains(unseekable.Message, "seek", unseekable.Message);
            }
            else
            {
                Assert.IsTrue(RefusedForBudget(() => sample.ReadStream(new OneWay(bytes), new ReadOptions(0)), out var oneWay),
                    $"{sample.Name} loaded from a stream that cannot seek under a budget of nothing");
                Assert.AreEqual(total, oneWay!.RequiredBytes,
                    $"{sample.Name} named {oneWay.RequiredBytes} bytes read from a stream that cannot seek, " +
                    $"and {total} from one that can");
            }

            Assert.IsFalse(RefusedForBudget(() => sample.Read(bytes, new ReadOptions(total)), out var again),
                $"{sample.Name} was refused needing {total} bytes, and refused again under exactly that, " +
                $"needing {again?.RequiredBytes}");
            CollectionAssert.AreEqual(bytes, sample.Write(sample.Read(bytes, new ReadOptions(total))),
                $"{sample.Name} read under the total it named does not write the bytes it was read from");

            // What the read is charged with nothing to stop it, through the budget a nested
            // read is handed, since that is the one whose charges can be read back.
            var unlimited = new ReadBudget(long.MaxValue, id);
            sample.Read(bytes, new ReadOptions(unlimited));
            Assert.AreEqual(total, unlimited.Charged,
                $"{sample.Name} named {total} bytes when refused and was charged {unlimited.Charged} " +
                "when nothing limited it");

            // And the reader, charging as it allocates, spends exactly that: the price and
            // the charges are the same sums, so neither has drifted from the other.
            Assert.AreEqual(unlimited.Charged, unlimited.Spent,
                $"{sample.Name} was priced at {unlimited.Charged} bytes and its reader charged " +
                $"{unlimited.Spent}");

            var allocated = Allocated(() => sample.Read(bytes, new ReadOptions(total)));
            Console.WriteLine(
                $"[ONE-TOTAL] {sample.Name}: payload {payload}, total {total}, allocated {allocated}");
            Assert.IsLessThanOrEqualTo(total + Slack, allocated,
                $"{sample.Name}'s read allocated {allocated} bytes and named {total}");

            // Refused below the payload's own length, the read cannot have held the payload
            // to work out the total it named.
            if (sample.SmallBecause is null)
            {
                var under = payload / 2;
                var refused = Allocated(() =>
                {
                    RefusedForBudget(() => sample.Read(bytes, new ReadOptions(under)), out _);
                    return new object();
                });
                Assert.IsLessThan(under, refused,
                    $"{sample.Name} refused under {under} bytes, half its payload, had allocated {refused}");
            }
        }

        /// <summary>
        /// The defect's own shape, small: a 64-bit Bloom filter read under a budget too
        /// small for it, and then under whatever each refusal says it needs. It loads on
        /// the second attempt, as the refusal promises; it took four.
        /// </summary>
        [TestMethod]
        public void TestFollowingTheRefusalLoadsOnTheNextAttempt()
        {
            var bytes = Filled(new BloomFilter64(200_000, 0.01), (f, k) => f.Add(k), 1000).ToByteArray();

            var budget = 1024L;
            var named = new List<long>();
            while (RefusedForBudget(() => Persistence.FromByteArray<BloomFilter64>(bytes, new ReadOptions(budget)), out var why))
            {
                Assert.IsGreaterThan(budget, why.RequiredBytes, "a refusal named no more than it was given");
                named.Add(why.RequiredBytes);
                budget = why.RequiredBytes;
                Assert.IsLessThan(10, named.Count, "a read kept being refused");
            }

            Assert.HasCount(1, named,
                $"a {bytes.Length}-byte filter was refused {named.Count} times following each refusal's " +
                $"advice, naming {string.Join(", then ", named)}");
        }

        /// <summary>
        /// A structure that holds others is priced with them: the total its refusal names
        /// is at least what each structure it holds would name read on its own, together,
        /// and the payload holding them.
        /// </summary>
        /// <remarks>
        /// The nested structures are read from runs inside the outer payload, each its own
        /// envelope, and nothing about them is known until those runs are looked into. A
        /// price taken from the outer header alone, or one that stopped at the first
        /// nested envelope, falls short of this by every structure held.
        /// </remarks>
        [TestMethod]
        public void TestANestedStructureIsPricedWithEverythingItHolds()
        {
            var scalable = Filled(new ScalableBloomFilter(500, 0.01, 0.8), (f, k) => f.Add(k), 10_000);
            Assert.IsGreaterThan(3, scalable.Filters.Count, "the scalable filter never grew");
            AssertPricedWithWhatItHolds<ScalableBloomFilter>(
                scalable.ToByteArray(),
                scalable.Filters.Select(filter => Total<PartitionedBloomFilter>(filter.ToByteArray())));

            // TopK holds one count-min sketch, the first run after k and n.
            var topK = Filled(new TopK(0.0005, 0.01, 50), (t, k) => t.Add(k), 2000);
            var topKBytes = topK.ToByteArray();
            var run = (int)BinaryPrimitives.ReadUInt32LittleEndian(topKBytes.AsSpan(PayloadStart + 8));
            var sketch = topKBytes.AsSpan(PayloadStart + 12, run).ToArray();
            AssertPricedWithWhatItHolds<TopK>(topKBytes, new[] { Total<CountMinSketch>(sketch) });
        }

        /// <summary>
        /// A windowed sketch is priced with the plan its checkpoints lay out, and the price
        /// counts the checkpoints without building them. The count it works out is the
        /// length of the array the read builds, at every size up to a few thousand and at
        /// factors from coarse to fine, and at sizes far past those. Both come from one walk
        /// since the checkpoints stopped being thinned from a list of every offset (#219),
        /// so this holds them together against a change that gives either its own;
        /// <c>TestDpswSketch</c> holds the walk to the rule.
        /// </summary>
        [TestMethod]
        public void TestTheCheckpointCountAPriceUsesIsTheOneTheReadBuilds()
        {
            var alphas = new[] { 0.001, 0.01, 0.05, 0.1, 0.25, 1.0 / 3, 0.5, 0.75, 0.9, 0.999 };
            var compared = 0;
            foreach (var alpha in alphas)
            {
                for (var size = 1; size <= 3000; size += size < 64 ? 1 : 37)
                {
                    Assert.AreEqual(DpswSketch.Checkpoints(size, alpha).Length,
                        DpswSketch.CheckpointCount(size, alpha), $"substream {size}, alpha {alpha}");
                    compared++;
                }
            }

            foreach (var (size, alpha) in new[] { (200_000, 0.5), (65_536, 0.01), (100_003, 0.3) })
            {
                Assert.AreEqual(DpswSketch.Checkpoints(size, alpha).Length,
                    DpswSketch.CheckpointCount(size, alpha), $"substream {size}, alpha {alpha}");
                compared++;
            }

            Assert.IsGreaterThan(1000, compared);
        }

        /// <summary>
        /// A read whose reader charges past its price -- a price that missed something --
        /// still goes no further than its budget: the charges past the price are held to
        /// the limit, as every charge was before reads were priced, and the refusal then
        /// names what had been charged. No structure's read gets here, since the sweep
        /// holds every price to its reader's charges; this is what keeps a mistake in a
        /// future price from being a way past the budget.
        /// </summary>
        [TestMethod]
        public void TestAChargePastThePriceIsStillHeldToTheLimit()
        {
            var budget = new ReadBudget(150, StructureId.BloomFilter);
            budget.Price(100);
            budget.Settle();
            Assert.IsTrue(budget.IsSettled);

            budget.Charge(100);
            Assert.AreEqual(100L, budget.Charged);
            budget.Charge(20);
            Assert.AreEqual(120L, budget.Spent);
            Assert.AreEqual(120L, budget.Charged, "a charge past the price was not counted");
            budget.Charge(30);

            var refusal = Assert.ThrowsExactly<InvalidDataException>(() => budget.Charge(1));
            var why = Assert.IsInstanceOfType<ReadBudgetExceededException>(refusal.InnerException);
            Assert.AreEqual(151L, why.RequiredBytes);
            Assert.AreEqual(150L, why.MemoryBudget);

            var priced = new ReadBudget(150, StructureId.BloomFilter);
            priced.Price(151);
            Assert.ThrowsExactly<InvalidDataException>(priced.Settle);
            Assert.IsFalse(priced.IsSettled);
        }

        private static long Total<T>(byte[] bytes) where T : IBinaryPersistable<T>
        {
            Assert.IsTrue(RefusedForBudget(() => Persistence.FromByteArray<T>(bytes, new ReadOptions(0)), out var why),
                $"a {typeof(T).Name} loaded under a budget of nothing");
            return why!.RequiredBytes;
        }

        private static void AssertPricedWithWhatItHolds<T>(byte[] bytes, IEnumerable<long> held)
            where T : IBinaryPersistable<T>
        {
            var parts = held.ToArray();
            var floor = parts.Sum() + bytes.Length;
            var total = Total<T>(bytes);
            Console.WriteLine(
                $"[NESTED] {typeof(T).Name}: payload {bytes.Length}, {parts.Length} held at {parts.Sum()}, total {total}");
            Assert.IsGreaterThanOrEqualTo(floor, total,
                $"a {typeof(T).Name} holding {parts.Length} structures named {total} bytes, and they " +
                $"name {parts.Sum()} read alone, besides its own {bytes.Length}-byte payload");
            Assert.IsFalse(RefusedForBudget(() => Persistence.FromByteArray<T>(bytes, new ReadOptions(total)), out _),
                $"a {typeof(T).Name} was refused under the {total} bytes it named");
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
            return Reseal(bytes);
        }

        /// <summary>Rewrites the envelope's checksum over a payload edited in place.</summary>
        private static byte[] Reseal(byte[] bytes)
        {
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

        /// <summary>
        /// A small payload whose header names a hash count no filter has is refused for the
        /// count and not for the budget, and before the scratch array of that many is priced
        /// or allocated: the refusal carries no budget exception, and the refused read
        /// allocated less than the budget. The count is held to what a constructor gives,
        /// so the array is never more than a few kilobytes and no count can ask a budget for
        /// much; what a read is charged for it is held to what it allocates, for the count a
        /// payload may carry, by <c>TestEveryStructureReadsWithinAnHonestBudget</c>.
        /// </summary>
        private static void AssertRefusedForItsHashCountBeforeAllocating<T>(byte[] poked)
            where T : IBinaryPersistable<T>
        {
            var options = new ReadOptions(SmallBudget);
            InvalidDataException refused = null;
            var allocated = Allocated(() =>
            {
                refused = Assert.ThrowsExactly<InvalidDataException>(
                    () => Persistence.FromByteArray<T>(poked, options));
                return new object();
            });

            StringAssert.Contains(refused.Message, "hash functions, more than");
            Assert.IsNull(refused.InnerException,
                $"a {typeof(T).Name} naming too many hash functions was refused for its budget");
            Assert.IsLessThan(SmallBudget, allocated,
                $"a {typeof(T).Name} refused for its hash count had already allocated {allocated} bytes");
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

        /// <summary>
        /// The counting filter's scratch buffer is sized by its hash count, which was once
        /// the way a few bytes asked a budget for 64 MiB and is now refused first.
        /// </summary>
        [TestMethod]
        public void TestACountingFilterHashCountLargerThanTheBudgetIsRefused()
        {
            // u32 m, then u32 k at 4: 2^24 four-byte indexes, 64 MiB.
            var bytes = new CountingBloomFilter(1000, 4, 0.01).ToByteArray();
            AssertRefusedForItsHashCountBeforeAllocating<CountingBloomFilter>(Poke(bytes, 4, 1u << 24));
        }

        /// <summary>The deletable filter's scratch buffer, likewise.</summary>
        [TestMethod]
        public void TestADeletableFilterHashCountLargerThanTheBudgetIsRefused()
        {
            var bytes = new DeletableBloomFilter(1000, 100, 0.01).ToByteArray();
            AssertRefusedForItsHashCountBeforeAllocating<DeletableBloomFilter>(Poke(bytes, 4, 1u << 24));
        }

        /// <summary>The stable filter's scratch buffer, likewise.</summary>
        [TestMethod]
        public void TestAStableFilterHashCountLargerThanTheBudgetIsRefused()
        {
            var bytes = new StableBloomFilter(1000, 3, 0.01, seed: 1).ToByteArray();
            AssertRefusedForItsHashCountBeforeAllocating<StableBloomFilter>(Poke(bytes, 4, 1u << 24));
        }

        /// <summary>An inverse filter's slots are allocated whole, occupied or not.</summary>
        [TestMethod]
        public void TestAnInverseFilterCapacityLargerThanTheBudgetIsRefused()
        {
            // u32 capacity at 0: 2^22 slots, 32 MiB of references.
            var bytes = Filled(new InverseBloomFilter(100), (f, k) => f.Add(k), 10).ToByteArray();
            AssertRefusedBeforeAllocating<InverseBloomFilter>(Poke(bytes, 0, 1u << 22), 8L << 22);
        }

        /// <summary>
        /// A count-min sketch's rows are allocated before they are read, so a payload that
        /// claims counters it does not hold is refused for that before any row is
        /// allocated, under the default budget and under none. Its price and its reader
        /// share the check, so the price, which a payload malformed past what it has
        /// priced does not stop, does not stand in for the reader's.
        /// <para>
        /// A row of ten million counters is 80 MB, less than the default budget, so the
        /// budget alone does not stop it. The width is the field to edit: the rows are
        /// allocated one at a time, each read before the next, so a depth claiming rows
        /// that are not there allocates one row past the payload and no more.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestACountMinSketchClaimingCountersItDoesNotHoldIsRefusedBeforeAllocating()
        {
            // f64 epsilon, f64 delta, then u32 width at 16. Delta 0.5 is one row.
            var bytes = new CountMinSketch(0.1, 0.5).ToByteArray();

            foreach (var options in new[] { ReadOptions.Default, ReadOptions.Unlimited })
            {
                AssertRefusedForMissingCounters<CountMinSketch>(Poke(bytes, 16, 10_000_000), options, "width");
            }
        }

        /// <summary>
        /// A size so large its cost does not fit a long -- every row of a matrix two
        /// billion by two billion, the most a private sketch allows of each -- is priced as
        /// the largest long and refused, rather than wrapping to a small one.
        /// <para>
        /// No payload reaches this now, so it is held on the arithmetic every price is made
        /// of. A Count-Min Sketch stopped reaching it when its reader checked its depth,
        /// at most 710, first; a private sketch, the last that did, when its reader checked
        /// that the counters it claims are in the payload before pricing them, and a
        /// payload of four quintillion counters is not one that can be written.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestACostTooLargeToCountIsRefusedRatherThanWrapped()
        {
            var most = (ulong)Array.MaxLength;

            // Each of the three ways a price grows past a long: an array too long to
            // count, a count of things each costing more than a long over that count, and
            // a sum of costs past the largest long. Each wraps to a small or negative
            // number if it is worked out plainly.
            Assert.AreEqual(long.MaxValue, ReadBudget.ArrayOf(ulong.MaxValue, sizeof(double)));
            Assert.AreEqual(long.MaxValue, ReadBudget.Times(most, ReadBudget.ArrayOf(most, sizeof(double))));
            Assert.AreEqual(long.MaxValue, ReadBudget.Sum(long.MaxValue - 1, 2));

            // The matrix such a sketch would claim, priced as its reader prices it.
            var matrix = ReadBudget.Sum(
                ReadBudget.ArrayOf(most, sizeof(long)),
                ReadBudget.Times(most, ReadBudget.ArrayOf(most, sizeof(double))),
                ReadBudget.SmallObject);
            var budget = new ReadBudget(long.MaxValue - 1, StructureId.PrivateCountMinSketch);
            budget.Price(matrix);

            var refusal = Assert.ThrowsExactly<InvalidDataException>(budget.Settle);
            var why = Assert.IsInstanceOfType<ReadBudgetExceededException>(refusal.InnerException);
            Assert.AreEqual(long.MaxValue, why.RequiredBytes);
        }

        /// <summary>
        /// A private sketch's rows are allocated before they are read, so a payload that
        /// claims counters it does not hold is refused for that before any row is
        /// allocated, under the default budget and under none. Its price and its reader
        /// share the check, so the price, which a payload malformed past what it has
        /// priced does not stop, does not stand in for the reader's.
        /// <para>
        /// A row of ten million counters is 80 MB, less than the default budget, so the
        /// budget alone does not stop it. The same claim is made of the first sketch a
        /// DPSW window holds, read through the same body.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestAPrivateSketchClaimingCountersItDoesNotHoldIsRefusedBeforeAllocating()
        {
            const uint Claimed = 10_000_000;

            // u32 width at 0 and u32 depth at 4.
            var priv = new PrivateCountMinSketch(64, 2, 1.0, seed: 1).ToByteArray();

            // u64 window, f64 budget, f64 checkpoint factor, u32 substream size, u32
            // width, u32 depth, u64 items seen, u32 substream count, then the first
            // substream's u64 position and u32 item count, and its first sketch's body,
            // whose u32 width is at 60.
            var window = Filled(
                new DpswSketch(64, 4.0, alpha: 0.6, width: 4, depth: 2, seed: 11), (s, k) => s.Add(k), 90).ToByteArray();

            foreach (var options in new[] { ReadOptions.Default, ReadOptions.Unlimited })
            {
                AssertRefusedForMissingCounters<PrivateCountMinSketch>(Poke(priv, 0, Claimed), options, "width");
                AssertRefusedForMissingCounters<PrivateCountMinSketch>(Poke(priv, 4, Claimed), options, "depth");
                AssertRefusedForMissingCounters<DpswSketch>(Poke(window, 60, Claimed), options, "first sketch's width");
            }
        }

        /// <summary>
        /// The read is refused naming the counters as more than the payload holds, and
        /// allocated less than <see cref="SmallBudget"/> before it was: no row of the
        /// millions of counters claimed.
        /// </summary>
        private static void AssertRefusedForMissingCounters<T>(byte[] poked, ReadOptions options, string edited)
            where T : IBinaryPersistable<T>
        {
            InvalidDataException refusal = null;
            var allocated = Allocated(() =>
            {
                refusal = Assert.ThrowsExactly<InvalidDataException>(
                    () => Persistence.FromByteArray<T>(poked, options),
                    $"a {typeof(T).Name} with its {edited} edited was read");
                return refusal;
            });

            var budget = options.IsUnlimited ? "no budget" : $"a budget of {options.MemoryBudget}";
            Assert.IsLessThan(SmallBudget, allocated,
                $"a {typeof(T).Name} with its {edited} edited, under {budget}, allocated {allocated} bytes " +
                $"before it was refused: {refusal.Message}");
            Assert.IsNotInstanceOfType<ReadBudgetExceededException>(refusal.InnerException,
                $"a {typeof(T).Name} with its {edited} edited was refused for its budget under {budget}");
            StringAssert.Contains(refusal.Message, "counters, beyond what it can hold",
                $"a {typeof(T).Name} with its {edited} edited, under {budget}, was refused for something else");
        }

        /// <summary>
        /// A Sublime sketch's rows, as a count-min sketch's are. Its counters are read as
        /// plain counts into rows allocated first, and packed after, so the plain rows are
        /// what a claim of counters it does not hold allocated. A row of 2^23 counters is
        /// 64 MiB, less than the default budget.
        /// </summary>
        [TestMethod]
        public void TestASublimeSketchClaimingCountersItDoesNotHoldIsRefusedBeforeAllocating()
        {
            // Three f64, u32 depth at 24, u32 width at 28. Unexpanded, so no records
            // constrain the width, and delta 0.5 is one row.
            var bytes = Filled(new SublimeCountMinSketch(0.5), (s, k) => s.Add(k), 10).ToByteArray();

            foreach (var options in new[] { ReadOptions.Default, ReadOptions.Unlimited })
            {
                AssertRefusedForMissingCounters<SublimeCountMinSketch>(Poke(bytes, 28, 1u << 23), options, "width");
            }
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

        // ---- A payload wrong in its shape is refused for that, whatever the budget -----

        /// <summary>
        /// A payload malformed in one field its price reads is refused for that field
        /// under a budget it would also fail, not for the budget: the price runs the
        /// reader's shape checks, in the reader's order, before it prices what follows.
        /// Without them a price walks on past the field and names a total, and the
        /// payload is refused for its shape only once the caller has raised the budget
        /// to that total.
        /// </summary>
        /// <remarks>
        /// The budget is one byte short of what the unedited payload names. A price that
        /// skipped the check would walk the rest of the payload as it would the
        /// unedited one, and so price at least that much.
        /// </remarks>
        private static void AssertRefusedForItsShape<T>(byte[] unedited, byte[] edited, string because)
            where T : IBinaryPersistable<T>
        {
            Assert.IsTrue(
                RefusedForBudget(() => Persistence.FromByteArray<T>(unedited, new ReadOptions(1)), out var whole),
                $"an unedited {typeof(T).Name} was not refused under a budget of one byte");
            var budget = whole.RequiredBytes - 1;

            var e = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<T>(edited, new ReadOptions(budget)));
            Assert.IsNotInstanceOfType<ReadBudgetExceededException>(e.InnerException,
                $"an edited {typeof(T).Name} was refused for its budget, not its shape: {e.Message}");
            StringAssert.Contains(e.Message, because);
        }

        private static byte[] PokeByte(byte[] original, int payloadOffset, byte value)
        {
            var bytes = (byte[])original.Clone();
            bytes[PayloadStart + payloadOffset] = value;
            return Reseal(bytes);
        }

        private static byte[] Poke64(byte[] original, int payloadOffset, ulong value)
        {
            var bytes = (byte[])original.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(PayloadStart + payloadOffset), value);
            return Reseal(bytes);
        }

        /// <summary>A top-k claiming more elements than it tracks.</summary>
        [TestMethod]
        public void TestATopKHoldingMoreThanItTracksIsRefusedForThat()
        {
            var bytes = Filled(new TopK(0.001, 0.01, 50), (s, k) => s.Add(k), 2000).ToByteArray();

            // u32 k, u32 n, then the nested sketch's u32 length and its bytes, then the
            // element count.
            var nested = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(PayloadStart + 8));
            AssertRefusedForItsShape<TopK>(bytes, Poke(bytes, 12 + nested, 1_000_000), "with room for 50");
        }

        /// <summary>A Grafite claiming a universe past the largest it can reduce to.</summary>
        [TestMethod]
        public void TestAGrafiteUniverseTooLargeIsRefusedForThat()
        {
            var bytes = Grafite.Build(
                Enumerable.Range(0, 20_000).Select(i => (ulong)i * 1009), 0.01, 64, seed: 7).ToByteArray();

            // The u64 reduced universe comes first.
            AssertRefusedForItsShape<Grafite>(bytes, Poke64(bytes, 0, ulong.MaxValue), "beyond anything");
        }

        /// <summary>A DPSW window with a substream holding nothing.</summary>
        [TestMethod]
        public void TestADpswSubstreamHoldingNothingIsRefusedForThat()
        {
            var bytes = Filled(
                new DpswSketch(2000, 1.0, width: 1024, depth: 3, seed: 1), (s, k) => s.Add(k), 5000).ToByteArray();

            // u64 window, f64 budget, f64 checkpoint factor, u32 substream size, u32
            // width, u32 depth, u64 items seen, u32 substream count, then the first
            // substream's u64 position and, at 56, its u32 item count.
            AssertRefusedForItsShape<DpswSketch>(bytes, Poke(bytes, 56, 0), "holds 0 of a possible");
        }

        /// <summary>
        /// A private sketch claiming more counters than its payload holds, alone and as
        /// the first sketch a DPSW window holds. Its price checks that the counters are
        /// there before it prices the rows that hold them, so the claim is refused for
        /// that, not priced and refused for a budget that would not have loaded it.
        /// </summary>
        [TestMethod]
        public void TestAPrivateSketchClaimingCountersItDoesNotHoldIsRefusedForThat()
        {
            // u32 width at 0: rows of 2^22 counters, 32 MiB each.
            var priv = Filled(new PrivateCountMinSketch(1024, 3, 1.0, seed: 1), (s, k) => s.Add(k), 100).ToByteArray();
            AssertRefusedForItsShape<PrivateCountMinSketch>(priv, Poke(priv, 0, 1u << 22), "beyond what it can hold");

            // The window's header and the first substream's position and item count, as
            // above, then its first sketch's u32 width at 60.
            var window = Filled(
                new DpswSketch(2000, 1.0, width: 1024, depth: 3, seed: 1), (s, k) => s.Add(k), 5000).ToByteArray();
            AssertRefusedForItsShape<DpswSketch>(window, Poke(window, 60, 1u << 22), "beyond what it can hold");
        }

        /// <summary>
        /// A count-min sketch claiming more counters than its payload holds, as a private
        /// sketch above. Its price checks that the counters are there before it prices the
        /// rows that hold them, so the claim is refused for that, not priced and refused
        /// for a budget that would not have loaded it.
        /// </summary>
        [TestMethod]
        public void TestACountMinSketchClaimingCountersItDoesNotHoldIsRefusedForThat()
        {
            // u32 width at 16: rows of 2^22 counters, 32 MiB each.
            var bytes = Filled(new CountMinSketch(0.001, 0.01), (s, k) => s.Add(k), 100).ToByteArray();
            AssertRefusedForItsShape<CountMinSketch>(bytes, Poke(bytes, 16, 1u << 22), "beyond what it can hold");
        }

        /// <summary>A Sublime sketch claiming more counters than its payload holds, likewise.</summary>
        [TestMethod]
        public void TestASublimeSketchClaimingCountersItDoesNotHoldIsRefusedForThat()
        {
            // u32 width at 28, unexpanded so that no record constrains it.
            var bytes = Filled(new SublimeCountMinSketch(0.1), (s, k) => s.Add(k), 10).ToByteArray();
            AssertRefusedForItsShape<SublimeCountMinSketch>(bytes, Poke(bytes, 28, 1u << 22), "beyond what it can hold");
        }

        /// <summary>
        /// A placement byte naming no scheme, in each structure that records one from
        /// version 9, is refused for that under a budget it would also fail: its price
        /// reads the byte through the check its reader reads it through, and stops there.
        /// The byte follows the fields that size each one, so a price that walked past it
        /// would price everything after it.
        /// </summary>
        [TestMethod]
        public void TestAnUnknownPlacementIsRefusedForThatWhateverTheBudget()
        {
            // f64 epsilon, f64 delta, u32 width, u32 depth, then the byte at 24.
            var countMin = Filled(new CountMinSketch(0.001, 0.01), (s, k) => s.Add(k), 100).ToByteArray();
            AssertRefusedForItsShape<CountMinSketch>(countMin, PokeByte(countMin, 24, 2), "placement 2");

            // u32 width, u32 depth, then the byte at 8.
            var priv = Filled(new PrivateCountMinSketch(1024, 3, 1.0, seed: 1), (s, k) => s.Add(k), 100).ToByteArray();
            AssertRefusedForItsShape<PrivateCountMinSketch>(priv, PokeByte(priv, 8, 2), "placement 2");

            // u32 k, u32 width, u32 depth, then the byte at 12.
            var keeper = Filled(new HeavyKeeper(5, 1024, seed: 1), (s, k) => s.Add(k), 100).ToByteArray();
            AssertRefusedForItsShape<HeavyKeeper>(keeper, PokeByte(keeper, 12, 2), "placement 2");

            // u32 capacity, then the byte at 4.
            var inverse = Filled(new InverseBloomFilter(10_000), (f, k) => f.Add(k), 100).ToByteArray();
            AssertRefusedForItsShape<InverseBloomFilter>(inverse, PokeByte(inverse, 4, 255), "placement 255");

            // The window's header and substream count, 48 bytes; the first substream's
            // u64 start and u32 count; then its first body's u32 width and u32 depth, and
            // the body's byte at 68.
            var window = Filled(
                new DpswSketch(2000, 1.0, width: 1024, depth: 3, seed: 1), (s, k) => s.Add(k), 5000).ToByteArray();
            AssertRefusedForItsShape<DpswSketch>(window, PokeByte(window, 68, 2), "placement 2");

            // u32 k, u32 n, the sketch's u32 length, then its envelope: the sketch's byte is
            // 24 into its payload, which is 14 into the envelope, at 12.
            var topK = Filled(new TopK(0.001, 0.01, 50), (s, k) => s.Add(k), 2000).ToByteArray();
            var edited = (byte[])topK.Clone();
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(edited.AsSpan(PayloadStart + 8));
            var nested = edited.AsSpan(PayloadStart + 12, length).ToArray();
            nested[PayloadStart + 24] = 2;
            Reseal(nested).CopyTo(edited, PayloadStart + 12);
            AssertRefusedForItsShape<TopK>(topK, Reseal(edited), "placement 2");
        }

        /// <summary>A partition whose bucket width disagrees with its data's length.</summary>
        [TestMethod]
        public void TestAPartitionWhoseDataDisagreesWithItsWidthIsRefusedForThat()
        {
            var bytes = Filled(new PartitionedBloomFilter(100_000, 0.01), (s, k) => s.Add(k), 5000).ToByteArray();

            // u32 m, u32 k, u32 s, u8 placement, u64 count, u32 partition count, then the
            // first partition's u32 bucket count and, at 29, its u8 bucket width.
            AssertRefusedForItsShape<PartitionedBloomFilter>(bytes, PokeByte(bytes, 29, 2), "were stored");
        }

        /// <summary>
        /// A cuckoo entry placed in a bucket the table does not have, in a version 2
        /// payload: before version 3 each fingerprint is a run of its own, which the
        /// price walks the entries to price. A packed payload is priced at its table,
        /// which the reader charges before it reads an entry, so a budget refusal comes
        /// first in both.
        /// </summary>
        [TestMethod]
        public void TestACuckooEntryOutsideItsTableIsRefusedForThat()
        {
            using var resource = typeof(TestReadBudget).Assembly.GetManifestResourceStream(
                "TestProbabilisticDataStructures.fixtures.cuckoobloomfilter-v2.bin")
                ?? throw new InvalidOperationException("the version 2 cuckoo fixture is not embedded");
            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            var bytes = buffer.ToArray();

            // u32 m, u32 b, u32 f, u32 count, u32 n, u32 occupied entry count, then the
            // first entry's u32 bucket index.
            AssertRefusedForItsShape<CuckooBloomFilter>(bytes, Poke(bytes, 24, uint.MaxValue), "outside its");
        }

        /// <summary>
        /// A cuckoo bucket count that is not a power of two, on a table larger than the
        /// payload's own: the price refuses it for that, before it prices a table, which is
        /// what keeps a read from allocating one.
        /// </summary>
        [TestMethod]
        public void TestACuckooBucketCountThatIsNotAPowerOfTwoIsRefusedForThat()
        {
            var bytes = Filled(new CuckooBloomFilter(1000, 0.01, seed: 1), (f, k) => f.Add(k), 10).ToByteArray();

            // u32 m at 0, which is 512 unedited; 1,000 buckets is the larger table.
            AssertRefusedForItsShape<CuckooBloomFilter>(bytes, Poke(bytes, 0, 1000), "is not a power of two");
        }

        /// <summary>
        /// A quotient filter claiming a remainder wider than its hash, a table that would
        /// be priced well past the budget. Not a quotient too wide: the slot count is a
        /// 32-bit shift, which wraps a quotient past 32 to a small table.
        /// </summary>
        [TestMethod]
        public void TestAQuotientFilterSplitWiderThanItsHashIsRefusedForThat()
        {
            var bytes = Filled(new QuotientFilter(50_000, 0.001), (f, k) => f.Add(k), 5000).ToByteArray();

            // u32 quotient bits, then the u32 remainder bits.
            AssertRefusedForItsShape<QuotientFilter>(bytes, Poke(bytes, 4, 1_000_000), "not a split of a");
        }

        /// <summary>An infini filter table claiming an address narrower than any it builds.</summary>
        [TestMethod]
        public void TestAnInfiniTableTooNarrowIsRefusedForThat()
        {
            var bytes = Filled(new InfiniFilter(10_000), (f, k) => f.Add(k), 20_000).ToByteArray();

            // u64 items, u32 doublings, u32 table count, then the first table's u32
            // address bits.
            AssertRefusedForItsShape<InfiniFilter>(bytes, Poke(bytes, 16, 1), "bits of address");
        }

        /// <summary>A theta sketch claiming to retain no values, a size none is built at.</summary>
        [TestMethod]
        public void TestAThetaSketchRetainingNothingIsRefusedForThat()
        {
            var bytes = Filled(new ThetaSketch(4096), (s, k) => s.Add(k), 100_000).ToByteArray();

            // The u32 retained size comes first.
            AssertRefusedForItsShape<ThetaSketch>(bytes, Poke(bytes, 0, 0), "not a size this");
        }

        /// <summary>
        /// A Sublime sketch whose totals do not fit together: holding as many keys as the
        /// count at which its rows next double, or doubling past where rows twice as wide
        /// would. Both are read before the counters they precede are priced.
        /// </summary>
        [TestMethod]
        public void TestASublimeSketchWhoseTotalsDoNotFitIsRefusedForThat()
        {
            var bytes = Filled(new SublimeCountMinSketch(0.02), (s, k) => s.Add(k), 6000).ToByteArray();

            // Three f64, u32 depth and u32 width, then the u64 count at 32 and the u64
            // count at which the rows double at 40.
            AssertRefusedForItsShape<SublimeCountMinSketch>(
                bytes, Poke64(bytes, 32, ulong.MaxValue), "fewer keys than");
            AssertRefusedForItsShape<SublimeCountMinSketch>(
                bytes, Poke64(bytes, 40, ulong.MaxValue), "twice as wide");
        }

        // ---- A hash its reader refuses is refused for that, whatever the budget --------

        /// <summary>Where the envelope's header names the hash a payload was written with.</summary>
        private const int HashIdOffset = 8;

        /// <summary>
        /// A payload whose header names a hash its reader will not read it with is
        /// refused for that under any budget, with the refusal it gets under none: under
        /// a budget too small to hold the payload, and under one byte short of the whole
        /// read, where it is held and checksummed. Which hash a payload names is in its
        /// header, and which one it is read with is the caller's to say, so nothing past
        /// the header bears on it. Refused only once the budget had been raised to the
        /// total, the payload would have been priced, and held, for a read that cannot
        /// happen.
        /// </summary>
        /// <remarks>
        /// Two hash ids, both read without a hash supplied, which every reader refuses:
        /// one no version defines, and the one a function set through <c>SetHash</c> is
        /// recorded as. A structure that hashes nothing also refuses a hash supplied to
        /// it, which says nothing about the payload at all.
        /// </remarks>
        [TestMethod]
        [DynamicData(nameof(Structures))]
        public void TestEveryStructureRefusesAHashItCannotUseWhateverTheBudget(string structure)
        {
            var id = Enum.Parse<StructureId>(structure);
            Assert.IsTrue(Samples.Value.TryGetValue(id, out var sample),
                $"{id} has no sample to read under a budget. Add one.");

            Assert.IsTrue(RefusedForBudget(() => sample.Read(sample.Bytes, new ReadOptions(1)), out var whole),
                $"{sample.Name} was not refused under a budget of one byte");
            var budgets = new[] { 1L, whole.RequiredBytes - 1 };

            foreach (var hash in new[] { (ushort)0xBEEF, (ushort)HashId.Custom })
            {
                var bytes = (byte[])sample.Bytes.Clone();
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(HashIdOffset), hash);
                Reseal(bytes);
                AssertRefusedAsUnderNoBudget(
                    $"{sample.Name} naming hash {hash}", options => sample.Read(bytes, options), budgets);
            }

            if (sample.RefusesAHashBecause is not null)
            {
                AssertRefusedAsUnderNoBudget(
                    $"{sample.Name} read with a hash", options => sample.ReadWithHash(sample.Bytes, options), budgets);
            }
        }

        /// <summary>
        /// A payload damaged in the hash id its header names is refused as damaged when it
        /// is held, and not for the hash: the hash is checked once the checksum has vouched
        /// for the header that names it.
        /// </summary>
        [TestMethod]
        public void TestAHeldPayloadDamagedInItsHashIdIsRefusedAsDamaged()
        {
            var bytes = (byte[])Samples.Value[StructureId.BloomFilter].Bytes.Clone();
            bytes[HashIdOffset] ^= 0x40;

            var e = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<BloomFilter>(bytes, ReadOptions.Unlimited));
            StringAssert.Contains(e.Message, "checksum does not match");
        }

        private static void AssertRefusedAsUnderNoBudget(string what, Func<ReadOptions, object> read, long[] budgets)
        {
            var unlimited = Assert.ThrowsExactly<InvalidDataException>(
                () => read(ReadOptions.Unlimited), $"{what} was read");

            foreach (var budget in budgets)
            {
                var e = Assert.ThrowsExactly<InvalidDataException>(() => read(new ReadOptions(budget)));
                Assert.IsNotInstanceOfType<ReadBudgetExceededException>(e.InnerException,
                    $"{what} under {budget:N0} bytes was refused for its budget, where under none: {unlimited.Message}");
                Assert.AreEqual(unlimited.Message, e.Message, $"{what} under {budget:N0} bytes");
            }
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
        /// says what was needed, what was allowed, and how to allow more -- each once, so
        /// that there is one number to act on and it is the one that works.
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
            Assert.AreEqual(refusal.Message, why.Message);

            // The total as a C# literal, digits grouped, inside the call that reads it.
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            var literal = why.RequiredBytes.ToString("N0", invariant).Replace(',', '_');
            Assert.AreEqual(1, Occurrences(refusal.Message, $"BloomFilter.ReadFrom(stream, new ReadOptions({literal}))"),
                refusal.Message);
            Assert.AreEqual(1, Occurrences(refusal.Message, literal), refusal.Message);
            Assert.AreEqual(0, Occurrences(refusal.Message, why.RequiredBytes.ToString("N0", invariant)),
                $"the total is named twice: {refusal.Message}");
            Assert.AreEqual(0, Occurrences(refusal.Message, why.RequiredBytes.ToString(invariant)),
                $"the total is named twice: {refusal.Message}");
            Assert.AreEqual(1, Occurrences(refusal.Message, "1,000"), refusal.Message);
            Assert.AreEqual(1, Occurrences(refusal.Message, "ReadFrom("), refusal.Message);
            Assert.DoesNotContain("at least", refusal.Message);
        }

        private static int Occurrences(string text, string part)
        {
            var count = 0;
            for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            {
                count++;
            }
            return count;
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
