using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// A reader allocates for what its payload holds, not for what the payload says it holds.
    /// </summary>
    /// <remarks>
    /// A payload's size fields say how much follows them, and a reader that allocates for a
    /// size before the bytes behind it are there lets a few bytes ask for any amount of memory.
    /// The read budget does not stop that on its own. A read is priced first, by a walk of the
    /// payload, and a walk that finds the payload ending early is set aside
    /// (<c>PersistenceFormat.PriceHeld</c>), so that the reader refuses the payload with its own
    /// explanation; what the walk had priced by then is what the read is admitted at. Under
    /// <see cref="ReadOptions.Unlimited"/> that is any amount, and under the default budget
    /// anything up to a gibibyte. So it is the reader that has to check, before it allocates,
    /// that the bytes it is about to allocate for are present.
    /// <para>
    /// The sweep takes a payload this library wrote, whole, and cuts it short: at every length
    /// from nothing to <see cref="EveryByteTo"/> bytes, and from there at lengths a quarter
    /// longer each time, to one byte short, with the envelope's length and checksum made to
    /// match. Each cut is a payload whose size fields claim everything the whole one held, and
    /// which holds only the start of it, and no hand-made field is needed to make one: the
    /// claims are the writer's own. Each must be refused, under no budget and under the default
    /// one, having allocated no more than <see cref="PerByte"/> times the bytes it holds and
    /// <see cref="Fixed"/> besides, counted in a no-GC region. That is the most any refusal here
    /// was measured to cost when its reader checks first, with room; a reader that allocates for
    /// a claim first costs what the claim does. Each must be refused for what it lacks under a
    /// budget of exactly its own price as well, so that what its reader charges before refusing
    /// it is no more than the price the read was admitted at.
    /// </para>
    /// <para>
    /// The roster is <see cref="StructureId"/>, and each structure's payload is its read-budget
    /// sample (<see cref="TestReadBudget.Samples"/>, held to the same roster), which loads and
    /// allocates well past the bound, so that what a claim costs could not hide in it. Where
    /// that sample does not reach a reader's claim, a further sample does, and says which
    /// (<see cref="Further"/>). Where what a reader can check before it allocates leaves a
    /// refusal costing more than the bound, the sample states what it may cost instead and why
    /// (<see cref="Allowances"/>, and the arrays of <see cref="ManyArrays"/>), and some cut has
    /// to cost more than the bound, or the allowance is out of date. Where the sample's read is
    /// sized by a parameter rather than by data, the payload is replaced
    /// (<see cref="Instead"/>), and a witness has to show the parameter's cost.
    /// </para>
    /// <para>
    /// What a size can claim before anything is read is bounded by the size checks every reader
    /// already makes, and those are not swept here: a claim that no payload this library writes
    /// makes, such as a table wider than its shape, is refused by them and tested with them. The
    /// sweep is the ordinary envelope's. The large form's reads go through the same readers and
    /// checks, from a stream rather than a held payload. A structure nested in another, as a
    /// scalable filter's filters and a top-k's sketch are, is a run its outer reader holds whole
    /// before the nested reader sees any of it, so no cut reaches the nested reader; its claims
    /// are swept as its own structure's.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestReadsAllocateOnlyWhatTheyHold
    {
        private const int PayloadStart = 14;
        private const int LengthOffset = 10;
        private const int ChecksumLength = 4;

        /// <summary>
        /// What a refused read may allocate for each byte it was given: the payload it holds, and
        /// as much again twice over for what it built from what it read before it was refused.
        /// The most any sample held to it needed, every cut measured with <see cref="Fixed"/>
        /// taken off, was 3.79, a 64-bit Bloom filter stored in many arrays.
        /// </summary>
        private const long PerByte = 4;

        /// <summary>
        /// What a refused read may allocate whatever it was given: its stream, its envelope's
        /// buffers and checksum, the exception and its message. The most any cut of a sample
        /// held to <see cref="PerByte"/> needed besides was 7,860 bytes, an inverse filter's 4 KiB
        /// of slots, which its capacity names; most need under 4,800.
        /// </summary>
        private const long Fixed = 16L << 10;

        /// <summary>The longest cut at which every length is swept.</summary>
        private const int EveryByteTo = 512;

        /// <summary>
        /// The least a whole sample's read may allocate, so that a claim it makes, allocated
        /// unread, could not hide in <see cref="Fixed"/>.
        /// </summary>
        private const long LargeEnough = 4 * Fixed;

        /// <summary>How many cuts are counted in one no-GC region.</summary>
        private const int PerRegion = 32;

        private static byte[] Key(long i)
        {
            var key = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(key, i);
            return key;
        }

        /// <summary>
        /// A payload to cut, how to read it, and what a refusal of a cut of it may allocate for
        /// each byte the cut holds: <see cref="PerByte"/>, unless <paramref name="CostlyBecause"/>
        /// says why it costs more, which the sweep then checks some cut does.
        /// </summary>
        internal sealed record Swept(
            string Name, byte[] Bytes, Func<byte[], ReadOptions, object> Read,
            string SmallBecause = null, long Allowance = PerByte, string CostlyBecause = null, int? ArrayBits = null);

        private static Swept Of<T>(string name, T structure) where T : IBinaryPersistable<T> =>
            new(name, structure.ToByteArray(), (bytes, options) => Persistence.FromByteArray<T>(bytes, options));

        /// <summary>
        /// The bits <see cref="ManyArrays"/> lowers a table's arrays to: 16 bytes each, so that a
        /// table of a few hundred kilobytes is stored as tens of thousands of arrays.
        /// </summary>
        private const int LoweredArrayBits = 4;

        /// <summary>
        /// Why a table of counters or cells in arrays of 16 bytes costs more to refuse than
        /// <see cref="PerByte"/> a byte. A table of bits has as many arrays for as many bytes, but
        /// seven in eight of them are empty, stored as their lengths and read back as nothing.
        /// Two a byte for the references, one for the payload and two for the arrays read back is
        /// five; measured, 4.67 for counters and 4.56 for cells, and they are allowed six.
        /// </summary>
        private const string FullArraysCost =
            "its array of arrays is 8 bytes for each array, whose length takes 4 in the payload, so its reader can " +
            "know only that the payload holds half a byte per byte of it, and every array is full, so a cut just long " +
            "enough for every length reads arrays of 16 bytes back at 40 each besides";

        /// <summary>
        /// A sample built and read with its 64-bit bucket tables' arrays lowered to
        /// <see cref="LoweredArrayBits"/>, so that the array of arrays its reader allocates
        /// before reading any of them is far larger than <see cref="Fixed"/>, allowed
        /// <paramref name="allowance"/> a byte because <paramref name="costlyBecause"/>.
        /// </summary>
        private static Swept ManyArrays<T>(
            string name, Func<T> build, long allowance = PerByte, string costlyBecause = null)
            where T : IBinaryPersistable<T>
        {
            Buckets64.LoweredArrayBits = LoweredArrayBits;
            try
            {
                var bytes = build().ToByteArray();
                return new(name, bytes, (b, options) =>
                {
                    Buckets64.LoweredArrayBits = LoweredArrayBits;
                    try
                    {
                        return Persistence.FromByteArray<T>(b, options);
                    }
                    finally
                    {
                        Buckets64.LoweredArrayBits = null;
                    }
                },
                Allowance: allowance,
                CostlyBecause: costlyBecause,
                ArrayBits: LoweredArrayBits);
            }
            finally
            {
                Buckets64.LoweredArrayBits = null;
            }
        }

        private static T Filled<T>(T structure, Action<T, byte[]> add, int count)
        {
            for (var i = 0; i < count; i++) add(structure, Key(i));
            return structure;
        }

        /// <summary>
        /// Samples swept beside a structure's budget sample, each for a claim of its reader's
        /// that the budget sample does not make large, and saying which.
        /// </summary>
        private static readonly Dictionary<StructureId, (string Why, Func<Swept> Sample)[]> Further = new()
        {
            [StructureId.VarOpt] =
            [
                ("the budget sample's reservoir is nearly all threshold items, and its regions are as many slots as both " +
                    "kinds of item it claims together, of which these are mostly items at their own weights",
                    () => Of("VarOpt, mostly exact items", Heavy())),
            ],
            [StructureId.HyperLogLogPlus] =
            [
                ("the budget sample is dense, and a sparse estimator's buffer is sized by the count of hashes it claims",
                    () => Of("HyperLogLogPlus, sparse", Filled(new HyperLogLogPlus(18), (s, k) => s.Add(k), 10_000))),
            ],
            [StructureId.BloomFilter64] =
            [
                ("its table is one array, and a table of many is read into an array of arrays sized by their count",
                    () => ManyArrays("BloomFilter64 in many arrays", () => Filled(new BloomFilter64(150_000, 0.01), (f, k) => f.Add(k), 5000))),
            ],
            [StructureId.CountingBloomFilter] =
            [
                ("its table is one array, and a table of many is read into an array of arrays sized by their count",
                    () => ManyArrays("CountingBloomFilter in many arrays", () => Filled(new CountingBloomFilter(50_000, 4, 0.01), (f, k) => f.Add(k), 5000), 6, FullArraysCost)),
            ],
            [StructureId.DeletableBloomFilter] =
            [
                ("its tables are one array each, and a table of many is read into an array of arrays sized by their count",
                    () => ManyArrays("DeletableBloomFilter in many arrays", () => Filled(new DeletableBloomFilter(150_000, 1000, 0.01), (f, k) => f.Add(k), 5000))),
            ],
            [StructureId.PartitionedBloomFilter] =
            [
                ("its partitions are one array each, and a partition of many is read into an array of arrays sized by their count",
                    () => ManyArrays("PartitionedBloomFilter in many arrays", () => Filled(new PartitionedBloomFilter(150_000, 0.01), (f, k) => f.Add(k), 5000))),
            ],
            [StructureId.StableBloomFilter] =
            [
                ("its cells are one array, and cells in many are read into an array of arrays sized by their count",
                    () => ManyArrays("StableBloomFilter in many arrays", () => Filled(new StableBloomFilter(200_000, 8, 0.01, seed: 1), (f, k) => f.Add(k), 5000), 6, FullArraysCost)),
            ],
        };

        /// <summary>
        /// Structures whose samples cost more than <see cref="PerByte"/> a byte to refuse
        /// when its reader checks all it can before allocating, with what it may cost and why.
        /// </summary>
        private static readonly Dictionary<StructureId, (long Allowance, string Why)> Allowances = new()
        {
            // Ten a byte for the regions, one for the payload and 32 for each 12-byte item read
            // back is 13.7; measured, 11.91.
            [StructureId.VarOpt] = (16,
                "a sampling reservoir's five regions take 40 bytes a slot and every slot holds an item, which takes as " +
                "little as its 4-byte length in the payload, so its reader can know only that the payload holds a tenth " +
                "of a byte per byte of regions, and an item of 8 bytes is read back at 32"),
        };

        /// <summary>
        /// Structures whose budget sample is swept as another, because its read allocates for a
        /// parameter that a valid payload of any size names without holding, and a witness: a
        /// valid payload naming it, holding nothing, whose read allocates past the bound.
        /// </summary>
        private static readonly Dictionary<StructureId, (string Why, Func<Swept> Sample, Func<Swept> Witness)> Instead = new()
        {
            [StructureId.InverseBloomFilter] = (
                "every slot of its capacity is allocated and only the occupied ones are stored, so its budget sample's " +
                "twenty thousand slots cost what they do from a payload of any length; swept with few slots and long keys",
                () => Of("InverseBloomFilter of few slots", Filled(new InverseBloomFilter(512), (f, k) => f.Add(LongKey(k)), 2000)),
                () => Of("InverseBloomFilter holding nothing", new InverseBloomFilter(20_000))),
        };

        /// <summary>
        /// A sampling reservoir of 5,000 holding 4,000 items at weights far above its threshold,
        /// so that most of what its regions hold are items at their own weights.
        /// </summary>
        private static VarOpt Heavy()
        {
            var sample = new VarOpt(5000, 1);
            for (var i = 0; i < 4000; i++) sample.Add(Key(i), 1e9);
            for (var i = 4000; i < 24_000; i++) sample.Add(Key(i), 1.0);
            Assert.IsGreaterThan(3 * sample.LightCount, sample.HeavyCount, "premise: the reservoir is mostly items at their own weights");
            Assert.IsGreaterThan(0, sample.LightCount, "premise: the reservoir is sampling");
            return sample;
        }

        private static byte[] LongKey(byte[] key)
        {
            var longer = new byte[256];
            key.CopyTo(longer, 0);
            return longer;
        }

        private static IEnumerable<Swept> SweptFor(StructureId id)
        {
            if (Instead.TryGetValue(id, out var instead))
            {
                yield return instead.Sample();
            }
            else
            {
                Assert.IsTrue(TestReadBudget.Samples.Value.TryGetValue(id, out var sample),
                    $"{id} has no read-budget sample to cut short. Add one there.");
                var swept = new Swept(sample.Name, sample.Bytes, sample.Read, sample.SmallBecause);
                if (Allowances.TryGetValue(id, out var allowance))
                {
                    Assert.IsFalse(string.IsNullOrWhiteSpace(allowance.Why), $"{id} is allowed {allowance.Allowance} bytes a byte without a reason");
                    swept = swept with { Allowance = allowance.Allowance, CostlyBecause = allowance.Why };
                }
                yield return swept;
            }

            if (Further.TryGetValue(id, out var further))
            {
                foreach (var (why, more) in further)
                {
                    Assert.IsFalse(string.IsNullOrWhiteSpace(why), $"{id} has a further sample without a reason");
                    var swept = more();
                    if (Allowances.TryGetValue(id, out var allowance))
                    {
                        swept = swept with { Allowance = allowance.Allowance, CostlyBecause = allowance.Why };
                    }
                    yield return swept;
                }
            }
        }

        public static IEnumerable<object[]> Structures =>
            Enum.GetValues<StructureId>().Select(id => new object[] { id.ToString() });

        /// <summary>
        /// The envelope of <paramref name="whole"/> with its payload cut to its first
        /// <paramref name="held"/> bytes, its length and checksum made to match.
        /// </summary>
        internal static byte[] Cut(byte[] whole, int held)
        {
            var bytes = new byte[PayloadStart + held + ChecksumLength];
            whole.AsSpan(0, PayloadStart + held).CopyTo(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(LengthOffset), (uint)held);
            var crc = new Crc32();
            crc.Append(bytes.AsSpan(4, bytes.Length - 8));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - ChecksumLength), crc.GetCurrentHashAsUInt32());
            return bytes;
        }

        /// <summary>
        /// Where a payload of <paramref name="length"/> bytes is cut: at every length up to
        /// <see cref="EveryByteTo"/>, where the size fields are, and then at lengths a quarter
        /// longer each time, so that a claim made further in is cut near its start too.
        /// </summary>
        internal static IEnumerable<int> Cuts(int length)
        {
            for (var held = 0; held < length; held = held < EveryByteTo ? held + 1 : (int)Math.Min(length, Math.Ceiling(held * 1.25)))
            {
                yield return held;
            }
        }

        private static long BoundFor(long held, long perByte = PerByte) => perByte * held + Fixed;

        /// <summary>
        /// The budgets every cut is read under: none, the default, twice the bound for the bytes
        /// the cut holds, which covers its envelope and everything those bytes allow a read to
        /// cost, so that a cut refused under it for its budget was priced for bytes it does not
        /// hold; and exactly the cut's own price, so that a cut refused under that for its budget
        /// was charged more than its price admitted it at.
        /// </summary>
        /// <remarks>
        /// A price that finds its payload ending early is set aside, and the read admitted at what
        /// it had priced by then, so its reader's charges before it refuses the payload have to
        /// fall within that. A charge past it is drawn on a budget nothing priced, and is refused
        /// for a budget the payload's own price fits. Only a budget of exactly the price sees the
        /// difference: any budget with room to spare absorbs it.
        /// </remarks>
        private static (string Name, Func<int, ReadOptions> For)[] BudgetsFor(StructureId id, Swept sample) =>
        [
            ("no budget", _ => ReadOptions.Unlimited),
            ("the default budget", _ => ReadOptions.Default),
            ("a budget of twice its bound", cut => new ReadOptions(2 * BoundFor(Held(cut), sample.Allowance))),
            ("a budget of exactly its own price", cut => new ReadOptions(PriceOfCut(id, sample, cut))),
        ];

        /// <summary>The bytes of the envelope of a cut to <paramref name="cut"/> bytes of payload.</summary>
        private static long Held(int cut) => PayloadStart + cut + ChecksumLength;

        /// <summary>
        /// What a read of <paramref name="sample"/> cut to <paramref name="cut"/> bytes is priced
        /// at, as a held payload is: its envelope, and its structure's own price walked over the
        /// bytes the cut holds, as far as it gets.
        /// </summary>
        private static long PriceOfCut(StructureId id, Swept sample, int cut)
        {
            Buckets64.LoweredArrayBits = sample.ArrayBits;
            try
            {
                return ReadBudget.Sum(PersistenceFormat.EnvelopeCost((ulong)cut), WalkPrice(id, sample.Bytes, cut).Priced);
            }
            finally
            {
                Buckets64.LoweredArrayBits = null;
            }
        }

        /// <summary>
        /// Every structure's reader, given a payload of its own cut short at each length, refuses
        /// it for what it lacks, never for its budget, having allocated no more than the bound for
        /// the bytes it holds: under no budget, under the default one, under one that covers no
        /// more than those bytes allow, and under exactly its own price; and what its reader
        /// charges before refusing it is its price, no more and no less.
        /// </summary>
        [TestMethod]
        [DynamicData(nameof(Structures))]
        public void TestEveryReaderRefusesWhatItDoesNotHoldBeforeAllocatingForIt(string structure)
        {
            var id = Enum.Parse<StructureId>(structure);
            foreach (var sample in SweptFor(id))
            {
                AssertTheWholeLoads(sample);
                AssertEveryCutIsChargedExactlyItsOwnPrice(id, sample);
                foreach (var (name, options) in BudgetsFor(id, sample))
                {
                    AssertEveryCutIsRefusedWithinTheBound(sample, name, options);
                }
            }
        }

        /// <summary>
        /// A cut's own price, as <see cref="PriceOfCut"/> works it out, is the price its read is
        /// admitted at, and its reader charges all of it before refusing the cut, no more and no
        /// less.
        /// </summary>
        /// <remarks>
        /// Under a byte less than the price, every cut is refused for its budget, and the refusal
        /// names exactly that price. A price worked out past the read's would leave room for a
        /// reader to charge past its price unseen, and one short of it would refuse a reader that
        /// charges no more. Under exactly the price, through the budget a nested read is handed,
        /// whose charges can be read back as <see cref="TestReadBudget"/> reads them, the reader
        /// charges the whole price before it refuses the cut. A reader that charges more is
        /// refused for its budget, which the budget of its own price sees too; a price past what
        /// its reader charges admits the read at more than it takes, which no refusal shows.
        /// </remarks>
        private static void AssertEveryCutIsChargedExactlyItsOwnPrice(StructureId id, Swept sample)
        {
            var length = sample.Bytes.Length - PayloadStart - ChecksumLength;
            var problems = new List<string>();
            var charged = new List<string>();
            var cuts = 0;
            foreach (var cut in Cuts(length))
            {
                cuts++;
                var price = PriceOfCut(id, sample, cut);
                try
                {
                    sample.Read(Cut(sample.Bytes, cut), new ReadOptions(price - 1));
                    problems.Add($"cut to {cut:N0} bytes, it was read under a byte less than its price of {price:N0}");
                }
                catch (InvalidDataException refused) when (refused.InnerException is ReadBudgetExceededException exceeded)
                {
                    if (exceeded.RequiredBytes != price)
                    {
                        problems.Add($"cut to {cut:N0} bytes, under a byte less than its price of {price:N0}, it was refused " +
                            $"needing {exceeded.RequiredBytes:N0}");
                    }
                }
                catch (Exception e)
                {
                    problems.Add($"cut to {cut:N0} bytes, under a byte less than its price of {price:N0}, it was refused " +
                        $"with {e.GetType().Name}: {e.Message}");
                }

                var exactly = new ReadBudget(price, id);
                try
                {
                    sample.Read(Cut(sample.Bytes, cut), new ReadOptions(exactly));
                    charged.Add($"cut to {cut:N0} bytes, it was read under exactly its price of {price:N0}");
                }
                catch (InvalidDataException)
                {
                    // For what it lacks, or for its budget; what it was charged says which.
                }

                if (exactly.Spent != price)
                {
                    charged.Add($"cut to {cut:N0} bytes, priced {price:N0}, its reader charged {exactly.Spent:N0} before refusing it");
                }
            }

            Assert.IsEmpty(problems,
                $"{sample.Name}: a cut's own price is not what its read is admitted at, at {problems.Count} of {cuts} cuts; " +
                "the first: " + string.Join(" | ", problems.Take(3)));
            Assert.IsEmpty(charged,
                $"{sample.Name}: a cut's reader does not charge exactly its price, at {charged.Count} of {cuts} cuts; " +
                "the first: " + string.Join(" | ", charged.Take(3)));
        }

        /// <summary>
        /// The payload loads whole, so that a cut of it lacks nothing but bytes, and its read
        /// allocates past <see cref="LargeEnough"/> unless it says why it cannot, so that what it
        /// claims could not be allocated unread inside the bound.
        /// </summary>
        private static void AssertTheWholeLoads(Swept sample)
        {
            var whole = NoGCRegion.Allocated(() => sample.Read(sample.Bytes, ReadOptions.Unlimited));
            if (sample.SmallBecause is null)
            {
                Assert.IsGreaterThanOrEqualTo(LargeEnough, whole,
                    $"{sample.Name}: the whole payload's read allocates {whole:N0} bytes, too little for a claim of it " +
                    "allocated unread to be told from what a refusal costs");
            }
            else
            {
                Assert.IsLessThan(LargeEnough, whole,
                    $"{sample.Name} is small because {sample.SmallBecause}, and its read allocates {whole:N0} bytes, " +
                    "so the reason is out of date and it is large enough to sweep");
            }
        }

        /// <summary>
        /// Every cut of the sample is refused for what it lacks, not for its budget, and allocated
        /// no more than its bound before it was; and where the sample is allowed more than
        /// <see cref="PerByte"/> a byte, some cut under no budget allocated more than that, so that
        /// the allowance is not out of date.
        /// </summary>
        private static void AssertEveryCutIsRefusedWithinTheBound(Swept sample, string budget, Func<int, ReadOptions> optionsFor)
        {
            Assert.IsGreaterThanOrEqualTo(PerByte, sample.Allowance, $"{sample.Name} is allowed less than every structure");
            Assert.AreEqual(sample.Allowance == PerByte, sample.CostlyBecause is null,
                $"{sample.Name} is allowed {sample.Allowance} bytes a byte, and a reason only for more than {PerByte}");

            var length = sample.Bytes.Length - PayloadStart - ChecksumLength;
            var cuts = Cuts(length).ToArray();
            var refusals = new Exception[cuts.Length];
            var allocated = new long[cuts.Length];
            for (var start = 0; start < cuts.Length; start += PerRegion)
            {
                var end = Math.Min(cuts.Length, start + PerRegion);
                var works = new List<Func<object>>();
                for (var i = start; i < end; i++)
                {
                    var at = i;
                    var payload = Cut(sample.Bytes, cuts[i]);
                    var options = optionsFor(cuts[i]);
                    works.Add(() =>
                    {
                        try
                        {
                            sample.Read(payload, options);
                            refusals[at] = null;
                        }
                        catch (Exception e)
                        {
                            refusals[at] = e;
                        }
                        return null;
                    });
                }

                NoGCRegion.AllocatedEach(works).CopyTo(allocated, start);
            }

            var problems = new List<string>();
            var costliest = 0L;
            for (var i = 0; i < cuts.Length; i++)
            {
                var held = Held(cuts[i]);
                var bound = BoundFor(held, sample.Allowance);
                costliest = Math.Max(costliest, allocated[i] - BoundFor(held));
                if (refusals[i] is not InvalidDataException refusal)
                {
                    problems.Add($"cut to {cuts[i]:N0} bytes, it was {(refusals[i] is null ? "read" : "refused with " + refusals[i].GetType().Name + ": " + refusals[i].Message)}");
                }
                else if (refusal.InnerException is ReadBudgetExceededException)
                {
                    problems.Add($"cut to {cuts[i]:N0} bytes, it was refused for its budget rather than for the bytes it lacks: {refusal.Message}");
                }
                else if (allocated[i] > bound)
                {
                    problems.Add($"cut to {cuts[i]:N0} bytes, it allocated {allocated[i]:N0} before it was refused, past the " +
                        $"{bound:N0} the bytes it holds allow: {refusal.Message}");
                }
            }

            Assert.IsEmpty(problems,
                $"{sample.Name}, of {length:N0} bytes, under {budget}, at {problems.Count} of {cuts.Length} cuts; the first: " +
                string.Join(" | ", problems.Take(3)));

            if (sample.CostlyBecause is not null && budget == "no budget")
            {
                Assert.IsGreaterThan(0, costliest,
                    $"{sample.Name} is allowed {sample.Allowance} bytes a byte because {sample.CostlyBecause}, and no cut " +
                    $"of it allocated more than the {PerByte} every structure is allowed, so the allowance is out of date");
            }
        }

        /// <summary>
        /// Every structure's price, walked over a payload of its own cut short at each length,
        /// prices no more than the bound for the bytes the cut holds. A price checks that the
        /// payload holds what a size claims before it prices the claim, as its reader checks
        /// before it allocates (TESTING.md), so that a short payload is refused for what it lacks
        /// under any budget that holds it, and is never refused for a budget it was priced past
        /// on bytes it does not have.
        /// </summary>
        /// <remarks>
        /// The bound is the one the sample's read is held to, and a price has no allowance of its
        /// own. The prices that were allowed more were defects, and were fixed rather than kept:
        /// Sublime's priced each block of counters before checking the payload held it, and the
        /// 64-bit tables' priced each empty array at 24 bytes, which a read of a held payload
        /// never allocated (#315).
        /// A price that needs an allowance again needs it as the reads have theirs: a stated
        /// reason, and a check that some cut costs that much, which fails once none does.
        /// </remarks>
        [TestMethod]
        [DynamicData(nameof(Structures))]
        public void TestEveryPriceRefusesWhatItsPayloadDoesNotHoldBeforePricingIt(string structure)
        {
            var id = Enum.Parse<StructureId>(structure);
            var price = PriceOf(id);
            foreach (var sample in SweptFor(id))
            {
                var version = BinaryPrimitives.ReadUInt16LittleEndian(sample.Bytes.AsSpan(4));
                var length = sample.Bytes.Length - PayloadStart - ChecksumLength;
                var problems = new List<string>();
                var walked = 0;
                Buckets64.LoweredArrayBits = sample.ArrayBits;
                try
                {
                    foreach (var cut in Cuts(length))
                    {
                        var budget = new ReadBudget(long.MaxValue, id);
                        var reader = new PayloadReader(sample.Bytes.AsSpan(PayloadStart, cut), budget);
                        var stopped = "it walked to its end";
                        try
                        {
                            price(ref reader, version);
                        }
                        catch (InvalidDataException e)
                        {
                            stopped = e.Message;
                        }

                        walked++;
                        var held = PayloadStart + cut + ChecksumLength;
                        var bound = BoundFor(held, sample.Allowance);
                        if (budget.Charged > bound)
                        {
                            problems.Add($"cut to {cut:N0} bytes, it priced {budget.Charged:N0}, past the {bound:N0} the bytes " +
                                $"it holds allow, and then stopped: {stopped}");
                        }
                    }
                }
                finally
                {
                    Buckets64.LoweredArrayBits = null;
                }

                Assert.IsEmpty(problems,
                    $"{sample.Name}'s price, at {problems.Count} of {walked} cuts; the first: " + string.Join(" | ", problems.Take(3)));
            }
        }

        /// <summary>The structure's own price, the walk its read is priced by.</summary>
        private static PayloadPrice PriceOf(StructureId id)
        {
            var type = typeof(BloomFilter).Assembly.GetType("ProbabilisticDataStructures." + id);
            Assert.IsNotNull(type, $"{id} names no type");
            var method = type.GetMethod("Price", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                [typeof(PayloadReader).MakeByRefType(), typeof(ushort)]);
            Assert.IsNotNull(method, $"{id} has no price to walk");
            return (PayloadPrice)Delegate.CreateDelegate(typeof(PayloadPrice), method);
        }

        /// <summary>
        /// The structure's price walked over the first <paramref name="held"/> bytes of the
        /// payload in <paramref name="envelope"/>: what it priced, the offset it stopped at, and
        /// why it stopped, or null if it walked to its end.
        /// </summary>
        private static (long Priced, long StoppedAt, string Why) WalkPrice(StructureId id, byte[] envelope, int held)
        {
            var version = BinaryPrimitives.ReadUInt16LittleEndian(envelope.AsSpan(4));
            var budget = new ReadBudget(long.MaxValue, id);
            var reader = new PayloadReader(envelope.AsSpan(PayloadStart, held), budget);
            try
            {
                PriceOf(id)(ref reader, version);
                return (budget.Charged, held - reader.Remaining, null);
            }
            catch (InvalidDataException e)
            {
                return (budget.Charged, held - reader.Remaining, e.Message);
            }
        }

        /// <summary>
        /// A Sublime sketch's price and its reader hold each block of counters, a record's or
        /// the current rows', to the bytes left where the block begins, before they price, charge
        /// or allocate anything for it. Its shape check holds only the current rows to the bytes
        /// after the shape, which the records before them share, so a cut that passes it can end
        /// inside a block: inside the current rows of a sketch with records, and inside the last
        /// record of one whose first record is a counter wide, the narrowest the constructor builds.
        /// </summary>
        /// <remarks>
        /// Cut a byte short of such a block, the price walks to its counters and prices nothing for
        /// them: it prices what a cut just before the block's tuning does, and a cut holding the
        /// block whole prices more, so the check is neither missing nor stricter than the bytes.
        /// The reader refuses the short cut for the counters it lacks, where it used to allocate the
        /// block and then find it missing. It does so under a budget of exactly what the cut's price
        /// came to as well, which a reader that charged for the block before checking it would
        /// overrun, since a price that stops early is set aside and the read admitted at it. The
        /// blocks are found by walking the payload as FORMAT.md lays it out, and the walk has to end
        /// where the payload does.
        /// </remarks>
        [TestMethod]
        public void TestASublimeBlockOfCountersCutShortIsRefusedBeforeItIsPricedOrAllocated()
        {
            const StructureId Id = StructureId.SublimeCountMinSketch;

            // One row, one counter wide, at a growth exponent of 0.9, which doubles at 108, 234
            // and 508 keys (TestOperationSequences): records of one, two and four counters, then
            // rows of eight.
            var bytes = Filled(new SublimeCountMinSketch(0.5, 0.9, ValeCounterArray.DefaultCountersPerChunk), (s, k) => s.Add(k), 600)
                .ToByteArray();
            Assert.AreEqual(PersistenceFormat.SublimeTuningVersion, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)),
                "premise: the payload stores each block's tuning, which the walk below steps over");

            // Three f64, then the rows and the width, each a u32: the shape. Then the three u64
            // totals and the u32 count of records; each record's u32 width, its tuning, two u8,
            // and its counters, u64 each; and the current rows' tuning and counters.
            const int Shape = 3 * sizeof(double) + 2 * sizeof(uint);
            var payload = bytes.AsSpan(PayloadStart, bytes.Length - PayloadStart - ChecksumLength);
            var depth = BinaryPrimitives.ReadUInt32LittleEndian(payload[(Shape - 8)..]);
            var width = BinaryPrimitives.ReadUInt32LittleEndian(payload[(Shape - 4)..]);
            var records = BinaryPrimitives.ReadUInt32LittleEndian(payload[(Shape + 3 * sizeof(ulong))..]);
            var blocks = new List<(string Name, int Tuning, int End, long Counters)>();
            var at = Shape + 3 * sizeof(ulong) + sizeof(uint);
            for (var r = 0; r < records; r++)
            {
                var recordWidth = BinaryPrimitives.ReadUInt32LittleEndian(payload[at..]);
                at += sizeof(uint);
                blocks.Add(($"record {r}", at, at + 2 + (int)(depth * recordWidth * sizeof(ulong)), depth * recordWidth));
                at = blocks[^1].End;
            }

            blocks.Add(("the current rows", at, at + 2 + (int)(depth * width * sizeof(ulong)), depth * width));
            Assert.AreEqual(payload.Length, blocks[^1].End, "the walk of FORMAT.md's layout does not end where the payload does");

            var reached = new List<string>();
            var problems = new List<string>();
            foreach (var block in blocks)
            {
                var shortOf = block.End - 1;
                var (priced, stoppedAt, why) = WalkPrice(Id, bytes, shortOf);
                if (stoppedAt == Shape)
                {
                    // The cut does not hold the current rows' counters after the shape, and the
                    // shape check refuses it there; that check is tested with it.
                    continue;
                }

                reached.Add(block.Name);
                var before = WalkPrice(Id, bytes, block.Tuning);
                Assert.AreEqual(block.Tuning, before.StoppedAt,
                    $"premise: a cut just before {block.Name}'s tuning is past the shape check, and its price stops at the tuning: {before.Why}");
                var whole = WalkPrice(Id, bytes, block.End);

                if (priced != before.Priced)
                {
                    problems.Add($"{block.Name}: a cut a byte short of its {block.Counters} counters priced {priced - before.Priced:N0} bytes " +
                        $"for them, and stopped: {why}");
                }
                else if (why is null || !why.Contains("counters, beyond what it can hold", StringComparison.Ordinal))
                {
                    problems.Add($"{block.Name}: a cut a byte short of its {block.Counters} counters stopped its price for another reason: {why}");
                }

                if (whole.Priced <= before.Priced)
                {
                    problems.Add($"{block.Name}: a cut holding its {block.Counters} counters priced none of them, and stopped: {whole.Why}");
                }

                var cut = Cut(bytes, shortOf);
                (string Name, ReadOptions Options)[] budgets =
                [
                    ("no budget", ReadOptions.Unlimited),
                    ("a budget of its own price", new ReadOptions(PersistenceFormat.EnvelopeCost((ulong)shortOf) + priced)),
                ];
                foreach (var (budget, options) in budgets)
                {
                    try
                    {
                        Persistence.FromByteArray<SublimeCountMinSketch>(cut, options);
                        problems.Add($"{block.Name}: a cut a byte short of its counters was read, under {budget}");
                    }
                    catch (InvalidDataException refused) when (refused.InnerException is ReadBudgetExceededException)
                    {
                        problems.Add($"{block.Name}: a cut a byte short of its counters was refused for {budget}: {refused.Message}");
                    }
                    catch (InvalidDataException refused) when (!refused.Message.Contains("counters, beyond what it can hold", StringComparison.Ordinal))
                    {
                        problems.Add($"{block.Name}: a cut a byte short of its counters was read into, under {budget}, and refused " +
                            $"where they ran out: {refused.Message}");
                    }
                    catch (InvalidDataException)
                    {
                    }
                }
            }

            Assert.Contains("the current rows", reached, "no cut a byte short of the current rows got past the shape check");
            Assert.IsTrue(reached.Any(name => name.StartsWith("record", StringComparison.Ordinal)),
                $"no cut a byte short of a record got past the shape check, so no record's counters were checked: reached {string.Join(", ", reached)}");
            Assert.IsEmpty(problems, $"at {problems.Count} checks; " + string.Join(" | ", problems));
        }

        /// <summary>
        /// The envelope of <paramref name="whole"/> with <paramref name="extra"/> bytes of nought
        /// after its payload, its length and checksum made to match.
        /// </summary>
        private static byte[] Extended(byte[] whole, int extra)
        {
            var length = whole.Length - PayloadStart - ChecksumLength;
            var bytes = new byte[whole.Length + extra];
            whole.AsSpan(0, PayloadStart + length).CopyTo(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(LengthOffset), (uint)(length + extra));
            var crc = new Crc32();
            crc.Append(bytes.AsSpan(4, bytes.Length - 8));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - ChecksumLength), crc.GetCurrentHashAsUInt32());
            return bytes;
        }

        /// <summary>
        /// An invertible table's price prices the hash sums it unpacks before it checks that the
        /// payload ends after them, where its reader charges them, so a payload holding a byte
        /// past the table is refused for that byte under exactly its own price.
        /// </summary>
        /// <remarks>
        /// The hash sums are the table's last run, so a cut that holds them holds the payload
        /// whole, and no cut the sweep reads can tell where their unpacking is priced. A byte left
        /// over can: a price that checked the end first would stop there, having priced less than
        /// the reader charges before it finds the byte, and the read would be refused for a budget
        /// its own price fits. Under a byte less than that price it is refused for its budget,
        /// naming the price, so the budget the read is held to has no room in it. The table is the
        /// smallest the constructor builds: a charge of any size past the price is past the budget.
        /// </remarks>
        [TestMethod]
        public void TestAnInvertibleTableWithAByteLeftOverIsRefusedForItUnderItsOwnPrice()
        {
            const StructureId Id = StructureId.InvertibleBloomLookupTable;
            var table = new InvertibleBloomLookupTable(1, 1);
            table.Add([7]);
            var whole = table.ToByteArray();
            var length = whole.Length - PayloadStart - ChecksumLength;
            var longer = Extended(whole, 1);

            var (priced, stoppedAt, why) = WalkPrice(Id, longer, length + 1);
            Assert.AreEqual(length, stoppedAt, $"premise: the price walks the table to its end, and stops at the byte left over: {why}");
            Assert.Contains("left over", why ?? "it walked to its end", "premise: the price stops for the byte left over");
            var price = ReadBudget.Sum(PersistenceFormat.EnvelopeCost((ulong)(length + 1)), priced);

            var refused = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<InvertibleBloomLookupTable>(longer, new ReadOptions(price)));
            Assert.IsFalse(refused.InnerException is ReadBudgetExceededException,
                $"under exactly its own price of {price:N0}, it was refused for its budget rather than for the byte left over: {refused.Message}");
            Assert.Contains("left over", refused.Message, "under exactly its own price, it was refused for another reason");

            var under = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<InvertibleBloomLookupTable>(longer, new ReadOptions(price - 1)));
            Assert.IsInstanceOfType<ReadBudgetExceededException>(under.InnerException,
                $"premise: under a byte less than its price of {price:N0}, it is refused for its budget: {under.Message}");
            Assert.AreEqual(price, ((ReadBudgetExceededException)under.InnerException).RequiredBytes,
                "premise: the price worked out here is the one the read is admitted at");
        }

        /// <summary>
        /// Each structure swept with another payload has a witness that its budget sample's cost
        /// is a parameter's: a valid payload holding nothing, which allocates past the bound.
        /// </summary>
        [TestMethod]
        public void TestEveryPayloadSweptInsteadIsSizedByAParameter()
        {
            foreach (var (id, (why, _, witness)) in Instead)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(why), $"{id} is swept as another payload without a reason");
                Assert.IsFalse(Allowances.ContainsKey(id), $"{id} is both swept as another payload and allowed more for its own");
                var sample = witness();
                var whole = NoGCRegion.Allocated(() => sample.Read(sample.Bytes, ReadOptions.Unlimited));
                Assert.IsGreaterThan(BoundFor(sample.Bytes.Length), whole,
                    $"{sample.Name}, a payload of {sample.Bytes.Length:N0} bytes, reads within the bound, so {id}'s " +
                    "budget sample is not sized by a parameter and should be swept as it is");
            }
        }
    }
}
