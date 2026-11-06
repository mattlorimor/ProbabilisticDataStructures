using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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
        /// A DPSW window's price and its reader hold its count of substreams to the bytes after
        /// it, at the least a substream takes, before they price, charge or allocate anything for
        /// the substreams. A count one more than those bytes could hold is refused for that, with
        /// nothing priced for it, and the count they could hold is priced and read past the
        /// check, so the check is neither missing nor stricter than the bytes.
        /// </summary>
        /// <remarks>
        /// No cut shows this (#332). A cut that ends inside the substreams keeps the writer's
        /// count, the few substreams a window spans, and what a price that skipped the check adds
        /// for it, the list of them and the first one's segment list, is well inside
        /// <see cref="Fixed"/>, so the sweep passes either way. So the count is edited here, in the
        /// sample the sweep cuts, with the checksum resealed: to one more than the bytes after it
        /// could hold, and to the most a u32 can claim. The price of each stops at the count,
        /// having priced what a cut just before the count does. Each is refused for the
        /// substreams it lacks under no budget, under the default one, and under a budget of
        /// exactly its own price, which a reader that charged for the list before checking it
        /// would overrun. It allocates no more than its envelope's cost and <see cref="Fixed"/>
        /// before it is refused, where a sketch of one substream costs more than that. The
        /// count the bytes could hold is the control: it is priced at least the list it claims,
        /// and its read gets past the check and allocates every substream the payload holds before
        /// it finds the rest missing.
        /// </remarks>
        [TestMethod]
        public void TestADpswSubstreamCountItDoesNotHoldIsRefusedBeforeItIsPricedOrAllocated()
        {
            const StructureId Id = StructureId.DpswSketch;
            const string Lacks = "substreams, beyond what it can hold";
            var bytes = TestReadBudget.Samples.Value[Id].Bytes;
            var length = bytes.Length - PayloadStart - ChecksumLength;
            var envelope = PersistenceFormat.EnvelopeCost((ulong)length);

            // FORMAT.md: the window's u64, two f64, three u32 and the u64 of items seen, then the
            // u32 count of substreams. Each substream is its u64 start and u32 count of items
            // before its sketches, so no fewer bytes than those.
            const int CountAt = 3 * sizeof(ulong) + 3 * sizeof(uint) + sizeof(ulong);
            const int AtLeast = sizeof(ulong) + sizeof(uint);
            var most = (uint)((length - CountAt - sizeof(uint)) / AtLeast);

            byte[] Claiming(uint count)
            {
                var edited = (byte[])bytes.Clone();
                BinaryPrimitives.WriteUInt32LittleEndian(edited.AsSpan(PayloadStart + CountAt), count);

                // Cut to its whole length, which reseals its checksum.
                return Cut(edited, length);
            }

            InvalidDataException Refusal(byte[] payload, ReadOptions options, out long allocated)
            {
                InvalidDataException refused = null;
                allocated = NoGCRegion.Allocated(() =>
                {
                    refused = null;
                    try
                    {
                        Persistence.FromByteArray<DpswSketch>(payload, options);
                    }
                    catch (InvalidDataException e)
                    {
                        refused = e;
                    }

                    return refused;
                });
                return refused;
            }

            var before = WalkPrice(Id, bytes, CountAt);
            Assert.AreEqual(CountAt, before.StoppedAt,
                $"premise: a cut just before the count is past the shape check, and its price stops at the count: {before.Why}");

            var fits = Claiming(most);
            var taken = WalkPrice(Id, fits, length);
            Assert.IsGreaterThanOrEqualTo(before.Priced + ReadBudget.ListOf(most, sizeof(long)), taken.Priced,
                $"the {most:N0} substreams the bytes after the count could hold were not priced, and the price stopped: {taken.Why}");
            Assert.IsFalse(taken.Why?.Contains(Lacks, StringComparison.Ordinal) ?? false,
                $"the {most:N0} substreams the bytes after the count could hold were refused as more than they hold: {taken.Why}");
            var read = Refusal(fits, ReadOptions.Unlimited, out var readInto);
            Assert.IsNotNull(read, $"a window claiming {most:N0} substreams, of which it holds the writer's few, was read");
            Assert.IsFalse(read.Message.Contains(Lacks, StringComparison.Ordinal),
                $"the {most:N0} substreams the bytes after the count could hold were refused by the reader as more than they hold: {read.Message}");
            Assert.IsGreaterThan(envelope + Fixed, readInto,
                $"a read past the count allocated {readInto:N0} bytes, within its envelope and {Fixed:N0}, so that bound does not " +
                "show a refused read allocated no substream");

            var problems = new List<string>();
            foreach (var claim in new[] { most + 1, uint.MaxValue })
            {
                var edited = Claiming(claim);
                var (priced, stoppedAt, why) = WalkPrice(Id, edited, length);
                if (priced != before.Priced || stoppedAt != CountAt + sizeof(uint))
                {
                    problems.Add($"claiming {claim:N0} substreams, the price priced {priced - before.Priced:N0} bytes past what a cut " +
                        $"before the count does and stopped at {stoppedAt:N0}: {why}");
                }
                else if (why is null || !why.Contains(Lacks, StringComparison.Ordinal))
                {
                    problems.Add($"claiming {claim:N0} substreams, the price stopped at the count for another reason: {why}");
                }

                (string Name, ReadOptions Options)[] budgets =
                [
                    ("no budget", ReadOptions.Unlimited),
                    ("the default budget", ReadOptions.Default),
                    ("a budget of its own price", new ReadOptions(envelope + priced)),
                ];
                foreach (var (budget, options) in budgets)
                {
                    var refused = Refusal(edited, options, out var allocated);
                    if (refused is null)
                    {
                        problems.Add($"claiming {claim:N0} substreams, the window was read under {budget}");
                        continue;
                    }

                    if (refused.InnerException is ReadBudgetExceededException)
                    {
                        problems.Add($"claiming {claim:N0} substreams, the window was refused for {budget}: {refused.Message}");
                    }
                    else if (!refused.Message.Contains(Lacks, StringComparison.Ordinal))
                    {
                        problems.Add($"claiming {claim:N0} substreams, the window was read into under {budget} and refused " +
                            $"for something else: {refused.Message}");
                    }

                    if (allocated > envelope + Fixed)
                    {
                        problems.Add($"claiming {claim:N0} substreams, the window allocated {allocated:N0} bytes under {budget} " +
                            $"before it was refused, past its envelope's {envelope:N0} and {Fixed:N0}");
                    }
                }
            }

            Assert.IsEmpty(problems, $"at {problems.Count} checks; " + string.Join(" | ", problems));
        }

        /// <summary>
        /// A check that holds a claim to the bytes after it, which no cut of a payload this library
        /// writes can see, with the claim edited past what those bytes hold: the checks it is, as
        /// <see cref="HoldsChecks"/> names them, what they count, as their refusal names it, the
        /// field edited, why no cut sees them, the payload, where the field is and where the check
        /// refuses it, both from FORMAT.md, the most the bytes after that point can hold, and the
        /// largest claim the field can make past the structure's other checks.
        /// </summary>
        internal sealed record EditedClaim(
            StructureId Id, string[] Checks, string Counts, string Field, string Why, Func<byte[]> Sample,
            Func<byte[], int> FieldAt, Func<byte[], int> CheckedAt, Func<byte[], uint> Most, uint Largest);

        /// <summary>
        /// What the read of an edited claim may allocate past the read of a cut just before its
        /// field, the envelope's difference aside: both are refused at once, and differ in their
        /// messages. Measured, every one allocated less than that cut's read, by 56 to 696 bytes,
        /// and a read of the most the bytes hold allocated at least 15,472 bytes more than it.
        /// </summary>
        private const long Refusing = 1 << 10;

        private static int PayloadLength(byte[] envelope) => envelope.Length - PayloadStart - ChecksumLength;

        private static uint PayloadUInt32(byte[] envelope, int at) =>
            BinaryPrimitives.ReadUInt32LittleEndian(envelope.AsSpan(PayloadStart + at));

        /// <summary>
        /// FORMAT.md: u32 k, width and depth, the placement byte from version 9, the f64 decay base
        /// and the u64 generator state and items added, then a u16 fingerprint and a u64 counter for
        /// each of depth × width buckets, and then the u32 element count.
        /// </summary>
        private static int HeavyKeeperElementCountAt(byte[] envelope)
        {
            var version = BinaryPrimitives.ReadUInt16LittleEndian(envelope.AsSpan(4));
            var placement = version >= PersistenceFormat.ColumnPlacementVersion ? 1 : 0;
            var buckets = (long)PayloadUInt32(envelope, 4) * PayloadUInt32(envelope, 8);
            return (int)(3 * sizeof(uint) + placement + sizeof(double) + 2 * sizeof(ulong) + buckets * (sizeof(ushort) + sizeof(ulong)));
        }

        /// <summary>FORMAT.md: u32 k and items added, the nested sketch as a run whose u32 length is at 8, then the u32 element count.</summary>
        private static int TopKElementCountAt(byte[] envelope) => 3 * sizeof(uint) + (int)PayloadUInt32(envelope, 2 * sizeof(uint));

        /// <summary>An element is its data's u32 length, the data, and its u64 frequency, so no fewer bytes than those.</summary>
        private const int ElementBytes = sizeof(uint) + sizeof(ulong);

        /// <summary>The budget sample with k raised as far as the heap goes, so that a count of elements is held only to the bytes.</summary>
        private static byte[] RoomFor(StructureId id) =>
            Edited(TestReadBudget.Samples.Value[id].Bytes, 0, (uint)ElementHeap.MaxCapacity);

        private static readonly EditedClaim[] EditedClaims =
        [
            new(StructureId.HeavyKeeper, ["HeavyKeeper.ReadElementCount"], "elements", "element count",
                "its count of elements is at most k, a few hundred in its budget sample, and the only cuts that hold the " +
                "count come after its buckets, where the bound has room for a heap of k elements on top of them",
                () => RoomFor(StructureId.HeavyKeeper),
                HeavyKeeperElementCountAt, b => HeavyKeeperElementCountAt(b) + sizeof(uint),
                b => (uint)((PayloadLength(b) - HeavyKeeperElementCountAt(b) - sizeof(uint)) / ElementBytes),
                (uint)ElementHeap.MaxCapacity),
            new(StructureId.TopK, ["TopK.ReadElementCount"], "elements", "element count",
                "its count of elements is at most k, a thousand in its budget sample, and the only cuts that hold the " +
                "count come after its nested sketch, where the bound has room for a heap of k elements on top of it",
                () => RoomFor(StructureId.TopK),
                TopKElementCountAt, b => TopKElementCountAt(b) + sizeof(uint),
                b => (uint)((PayloadLength(b) - TopKElementCountAt(b) - sizeof(uint)) / ElementBytes),
                (uint)ElementHeap.MaxCapacity),

            // FORMAT.md: f64 delta, growth exponent and size factor, then u32 depth at 24 and width
            // at 28, which the shape check holds the current rows' counters to.
            new(StructureId.SublimeCountMinSketch, ["SublimeCountMinSketch.CheckShape"], "counters", "depth",
                "its shape check holds the current rows to the bytes after the shape, and every block of counters, the " +
                "current rows' among them, is held again where it begins, to fewer bytes, so a cut the shape check refuses " +
                "is refused at the latest at the current rows, having read only blocks it holds; a depth edited past what " +
                "the current rows can have can still be held by the records' blocks, which are read at it unless the shape " +
                "check refuses it first",
                () => TestReadBudget.Samples.Value[StructureId.SublimeCountMinSketch].Bytes,
                _ => 3 * sizeof(double), _ => 3 * sizeof(double) + 2 * sizeof(uint),
                b => (uint)((PayloadLength(b) - 3 * sizeof(double) - 2 * sizeof(uint)) / ((long)sizeof(ulong) * PayloadUInt32(b, 28))),
                (uint)SublimeCountMinSketch.MaxDepth),
        ];

        public static IEnumerable<object[]> EditedClaimCases =>
            EditedClaims.Select(claim => new object[] { string.Join(", ", claim.Checks) });

        /// <summary>
        /// A check whose claim no cut makes large refuses that claim, edited one past what the bytes
        /// after it hold and to the largest the field can make, before it prices or allocates
        /// anything for it, and lets through the most those bytes hold.
        /// </summary>
        /// <remarks>
        /// <see cref="TestEveryPriceRefusesWhatItsPayloadDoesNotHoldBeforePricingIt"/> and its reader's
        /// twin see a check only by what dropping it costs, and a cut keeps its writer's claim. Where
        /// that claim stays small, what a price and a read that skipped the check would add fits in
        /// the bound at the only cuts that hold it, which come after everything before it and are
        /// bounded by all of that (#344, after #332). So the claim is
        /// edited here, as #332's was, in the sample the sweep cuts, its checksum resealed. The price
        /// of each edited claim stops where the check refuses it, having priced exactly what a cut
        /// just before the field does, and its read is refused for what it lacks under no budget,
        /// under the default one and under exactly its own price, having allocated no more than the
        /// read of that cut, the envelope's difference and <see cref="Refusing"/>. The most the bytes
        /// hold is the control: its price walks past the check and prices more, and its read
        /// allocates past that bound, so the bound can see what the check stops.
        /// </remarks>
        [TestMethod]
        [DynamicData(nameof(EditedClaimCases))]
        public void TestAClaimNoCutCanSeeIsRefusedBeforeItIsPricedOrAllocatedWhenEdited(string checks)
        {
            var claim = EditedClaims.Single(c => string.Join(", ", c.Checks) == checks);
            var read = TestReadBudget.Samples.Value[claim.Id].Read;
            Assert.IsFalse(string.IsNullOrWhiteSpace(claim.Why), $"{checks} is held by an edited claim without saying why no cut sees it");
            var lacks = claim.Counts + ", beyond what it can hold";
            var bytes = claim.Sample();
            var length = PayloadLength(bytes);
            var at = claim.FieldAt(bytes);
            var checkedAt = claim.CheckedAt(bytes);
            var most = claim.Most(bytes);
            var envelope = PersistenceFormat.EnvelopeCost((ulong)length);
            Assert.IsGreaterThan(most, claim.Largest, $"premise: {checks}: the largest claim, {claim.Largest:N0}, is past the most the bytes hold, {most:N0}");

            InvalidDataException Refusal(byte[] payload, ReadOptions options, out long allocated)
            {
                InvalidDataException refused = null;
                allocated = NoGCRegion.Allocated(() =>
                {
                    refused = null;
                    try
                    {
                        read(payload, options);
                    }
                    catch (InvalidDataException e)
                    {
                        refused = e;
                    }

                    return refused;
                });
                return refused;
            }

            var before = WalkPrice(claim.Id, bytes, at);
            Assert.AreEqual(at, before.StoppedAt,
                $"premise: {checks}: a cut just before the field at {at:N0} is past every earlier check, and its price stops there: {before.Why}");
            var cut = Refusal(Cut(bytes, at), ReadOptions.Unlimited, out var cutAllocated);
            Assert.IsNotNull(cut, $"premise: {checks}: a cut just before the field was read");
            var bound = cutAllocated - PersistenceFormat.EnvelopeCost((ulong)at) + envelope + Refusing;

            var fits = Edited(bytes, at, most);
            var taken = WalkPrice(claim.Id, fits, length);
            Assert.IsGreaterThan(checkedAt, taken.StoppedAt,
                $"{checks}: with its {claim.Field} at {most:N0}, the most the bytes hold, it was refused where the check refuses: {taken.Why}");
            Assert.IsGreaterThan(before.Priced, taken.Priced,
                $"{checks}: with its {claim.Field} at {most:N0}, the most the bytes hold, it priced no more than a cut before the field: {taken.Why}");
            Refusal(fits, ReadOptions.Unlimited, out var readInto);
            Assert.IsGreaterThan(bound, readInto,
                $"{checks}: a read with its {claim.Field} at {most:N0}, the most the bytes hold, allocated {readInto:N0} bytes, within the " +
                $"{bound:N0} a refused claim is held to, so that bound does not show the check stopped anything");

            var problems = new List<string>();
            foreach (var value in new[] { most + 1, claim.Largest })
            {
                var edited = Edited(bytes, at, value);
                var (priced, stoppedAt, why) = WalkPrice(claim.Id, edited, length);
                if (priced != before.Priced || stoppedAt != checkedAt)
                {
                    problems.Add($"with its {claim.Field} at {value:N0}, the price priced {priced - before.Priced:N0} bytes past what a cut " +
                        $"before the field does and stopped at {stoppedAt:N0}, not {checkedAt:N0}: {why}");
                    continue;
                }

                if (why is null || !why.Contains(lacks, StringComparison.Ordinal))
                {
                    problems.Add($"with its {claim.Field} at {value:N0}, the price stopped at the check for another reason: {why}");
                    continue;
                }

                // The tightest budget first, and no more reads of a claim once one shows a problem:
                // a reader that charged for a claim of billions before checking it would be refused
                // for a budget, and is not then read under none, where it would allocate them.
                (string Name, ReadOptions Options)[] budgets =
                [
                    ("a budget of its own price", new ReadOptions(envelope + priced)),
                    ("the default budget", ReadOptions.Default),
                    ("no budget", ReadOptions.Unlimited),
                ];
                foreach (var (budget, options) in budgets)
                {
                    var refused = Refusal(edited, options, out var allocated);
                    var problem = refused is null
                        ? $"with its {claim.Field} at {value:N0}, it was read under {budget}"
                        : refused.InnerException is ReadBudgetExceededException
                            ? $"with its {claim.Field} at {value:N0}, it was refused for {budget}: {refused.Message}"
                            : !refused.Message.Contains(lacks, StringComparison.Ordinal)
                                ? $"with its {claim.Field} at {value:N0}, it was refused under {budget} for something else: {refused.Message}"
                                : allocated > bound
                                    ? $"with its {claim.Field} at {value:N0}, it allocated {allocated:N0} bytes under {budget} before it " +
                                      $"was refused, past the {bound:N0} a cut before the field, its envelope and {Refusing:N0} allow"
                                    : null;
                    if (problem is not null)
                    {
                        problems.Add(problem);
                        break;
                    }
                }
            }

            Assert.IsEmpty(problems, $"{checks}, at {problems.Count} checks: " + string.Join(" | ", problems));
        }

        /// <summary>
        /// A nested envelope claiming a payload longer than the run it is stored in is refused as
        /// data, by its outer price and its outer read, under no budget and under the default one,
        /// and a claim the run holds is not refused for that.
        /// </summary>
        /// <remarks>
        /// The outer reader holds the run whole before the nested reader sees any of it, so the
        /// nested envelope's length is the one claim inside a run that no cut can make disagree with
        /// the run, and the price is what holds it to the run, by the run's own reader slicing that
        /// length from it. Unchecked there, a held payload's slice throws
        /// <see cref="ArgumentOutOfRangeException"/>, where every payload the library will not read
        /// is an <see cref="InvalidDataException"/>. The claim is the least the run cannot hold:
        /// the run holds the header, the payload and the checksum's four bytes, so a payload four
        /// bytes longer than its writer's still fits, taking the checksum's bytes, and is refused
        /// by the nested read for its checksum, and one five bytes longer is refused by the price.
        /// The roster is every structure whose price prices a nested one (<see cref="NestsOf"/>).
        /// </remarks>
        [TestMethod]
        public void TestANestedEnvelopeClaimingMoreThanItsRunIsRefusedAsData()
        {
            // FORMAT.md: the nested envelope is a run, its u32 length first, and its own header
            // carries its payload's u32 length at 10.
            (StructureId Id, int RunAt)[] cases =
            [
                (StructureId.TopK, 2 * sizeof(uint)),

                // f64 r, fp and p, the u64 hint and the u32 growth factor at version 14, then the
                // u32 count of filters, then the first filter.
                (StructureId.ScalableBloomFilter, 3 * sizeof(double) + sizeof(ulong) + 2 * sizeof(uint)),
            ];
            CollectionAssert.AreEquivalent(NestsOf().ToArray(), cases.Select(c => c.Id.ToString()).ToArray(),
                "the structures whose price prices a nested one are not the ones this test edits");

            var problems = new List<string>();
            foreach (var (id, runAt) in cases)
            {
                var sample = TestReadBudget.Samples.Value[id];
                var bytes = sample.Bytes;
                var length = PayloadLength(bytes);
                var run = PayloadUInt32(bytes, runAt);
                var lengthAt = runAt + sizeof(uint) + LengthOffset;
                Assert.AreEqual(run - (uint)(PayloadStart + ChecksumLength), PayloadUInt32(bytes, lengthAt),
                    $"premise: {id}: the walk of FORMAT.md's layout finds the nested envelope's length, its run less its header and checksum");

                // The run holds the header and run - 14 bytes after it.
                var held = run - (uint)PayloadStart;
                foreach (var claim in new[] { held, held + 1 })
                {
                    var edited = Edited(bytes, lengthAt, claim);
                    var ended = $"while reading {claim} more at offset {PayloadStart}";
                    string why;
                    try
                    {
                        why = WalkPrice(id, edited, length).Why;
                    }
                    catch (Exception e)
                    {
                        problems.Add($"{id}: a nested envelope claiming {claim:N0} bytes was refused by the price with " +
                            $"{e.GetType().Name}: {e.Message}");
                        continue;
                    }

                    if (claim > held && (why is null || !why.Contains(ended, StringComparison.Ordinal)))
                    {
                        problems.Add($"{id}: a nested envelope claiming {claim:N0} bytes in a run that holds {held:N0} after its header " +
                            $"was not refused by the price for them: {why ?? "it walked to its end"}");
                    }
                    else if (claim == held && why is not null && why.Contains(ended, StringComparison.Ordinal))
                    {
                        problems.Add($"{id}: a nested envelope claiming the {claim:N0} bytes its run holds after its header was refused " +
                            $"by the price for them: {why}");
                    }

                    foreach (var (budget, options) in new[] { ("no budget", ReadOptions.Unlimited), ("the default budget", ReadOptions.Default) })
                    {
                        try
                        {
                            sample.Read(edited, options);
                            problems.Add($"{id}: a nested envelope claiming {claim:N0} bytes was read under {budget}");
                        }
                        catch (InvalidDataException e) when (e.InnerException is ReadBudgetExceededException)
                        {
                            problems.Add($"{id}: a nested envelope claiming {claim:N0} bytes was refused for {budget}: {e.Message}");
                        }
                        catch (InvalidDataException)
                        {
                        }
                        catch (Exception e)
                        {
                            problems.Add($"{id}: a nested envelope claiming {claim:N0} bytes, under {budget}, was refused with " +
                                $"{e.GetType().Name}: {e.Message}");
                        }
                    }
                }
            }

            Assert.IsEmpty(problems, $"{problems.Count} problems: " + string.Join(" | ", problems));
        }

        /// <summary>The structures whose price prices a nested one, from the library's IL.</summary>
        private static IEnumerable<string> NestsOf()
        {
            var nested = typeof(PersistenceFormat).GetMethod("PriceNested", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(nested, "PersistenceFormat has no PriceNested to look for");
            return LibraryCalls().Where(c => nested.Equals(c.Called) && c.Caller.Name == "Price")
                .Select(c => Outermost(c.Caller.DeclaringType).Name).Distinct();
        }

        /// <summary>
        /// Checks held by a test of their own rather than by an edited claim here, each with the
        /// test that kills the mutant dropping it and why that test sees it.
        /// </summary>
        private static readonly Dictionary<string, (string Test, string Why)> HeldElsewhere = new()
        {
            ["CountMinSketch.CheckMatrix: counters"] = ("TestReadBudget.TestACountMinSketchClaimingCountersItDoesNotHoldIsRefusedForThat",
                "it edits the width to rows of megabytes, under a budget the unedited payload would fail"),
            ["CuckooBloomFilter.RequireTable: words of table and generator state"] = (
                "TestCuckooDenseLayout.TestADenseTablePastTheOldCeilingIsRefusedOnlyForTheBytesItLacks",
                "it edits the bucket count to a table past the old ceiling, and asks for the refusal to be for the bytes"),
            ["DpswSketch.Price: substreams"] = ("TestReadsAllocateOnlyWhatTheyHold.TestADpswSubstreamCountItDoesNotHoldIsRefusedBeforeItIsPricedOrAllocated",
                "it edits the substream count, as this test edits its claims (#332)"),
            ["DpswSketch.ReadPayload: substreams"] = ("TestReadsAllocateOnlyWhatTheyHold.TestADpswSubstreamCountItDoesNotHoldIsRefusedBeforeItIsPricedOrAllocated",
                "it edits the substream count, as this test edits its claims (#332)"),
            ["Grafite.ReadHighWords: words of high bits"] = (
                "TestPersistenceHostilePayloads.TestGrafiteAtTheMostBitsOfHighPartsIsRefusedOnlyForTheWordsItLacks",
                "it raises the bits of high parts to the most there are, and asks for the refusal to be for the words"),
            ["Grafite.ReadLowWords: words of low bits"] = ("TestLoadWhatItBuilds.TestGrafiteReaderTakesLowWordsPastTheOldCeiling",
                "it raises the low words' count past the old ceiling, and asks for the refusal to be for the bytes"),
            ["HeavyKeeper.CheckShape: buckets"] = ("TestPersistenceHostilePayloads.TestHeavyKeeperClaimingAbsurdWidthIsRefused",
                "it edits the width to one no payload of its length holds"),
            ["HyperLogLogPlus.Price: stored hashes"] = ("TestReadBudget.TestAHyperLogLogPlusSparseCountItDoesNotHoldIsRefusedForThat",
                "it edits the count of hashes to megabytes of them, under a budget the claim would pass"),
            ["HyperLogLogPlus.ReadPayload: stored hashes"] = ("TestReadBudget.TestAHyperLogLogPlusSparseCountItDoesNotHoldIsRefusedForThat",
                "it edits the count of hashes to megabytes of them, and under no budget"),
            ["InfiniFilter.Price: words"] = ("TestExpandingTableLimits.TestATableAtTheLimitIsRefusedForTheWordsItLacksNotForItsSize",
                "it claims a table at its limit without its words, and asks for the refusal to be for the words"),
            ["InfiniFilter.ReadPayload: words"] = ("TestExpandingTableLimits.TestATableAtTheLimitIsRefusedForTheWordsItLacksNotForItsSize",
                "it claims a table at its limit without its words, and asks for the refusal to be for the words"),
            ["MementoFilter.Price: words"] = ("TestExpandingTableLimits.TestATableAtTheLimitIsRefusedForTheWordsItLacksNotForItsSize",
                "it claims a table at its limit without its words, and asks for the refusal to be for the words"),
            ["MementoFilter.ReadPayload: words"] = ("TestExpandingTableLimits.TestATableAtTheLimitIsRefusedForTheWordsItLacksNotForItsSize",
                "it claims a table at its limit without its words, and asks for the refusal to be for the words"),
            ["MinHashSignature.Price: values"] = ("TestLoadWhatItBuilds.TestMinHashSignatureLimitIsShared",
                "it raises the length to the limit, and asks for the refusal to be for the bytes"),
            ["MinHashSignature.ReadPayload: values"] = ("TestLoadWhatItBuilds.TestMinHashSignatureLimitIsShared",
                "it raises the length to the limit, and asks for the refusal to be for the bytes"),
            ["PersistenceFormat.PriceBuckets64: arrays of buckets"] = ("TestReadBudget.TestABloomFilter64ArrayCountItDoesNotHoldIsRefusedForThat",
                "it edits the array count to a million, under a budget the claim would pass"),
            ["PersistenceFormat.ReadBuckets64: arrays of buckets"] = ("TestReadBudget.TestABloomFilter64ArrayCountItDoesNotHoldIsRefusedForThat",
                "it edits the array count to a million, and under no budget"),
            ["PrivateCountMinSketch.CheckShape: counters"] = ("TestReadBudget.TestAPrivateSketchClaimingCountersItDoesNotHoldIsRefusedForThat",
                "it edits the width to rows of megabytes, alone and in a window, under a budget the unedited payload would fail"),
            ["ScalableBloomFilter.ReadFilterCount: contained filters"] = ("TestPersistenceHostilePayloads.TestAbsurdContainedFilterCountIsRefused",
                "it edits the count of filters to one no payload of its length holds"),
            ["SetSketch.Price: registers"] = ("TestLoadWhatItBuilds.TestSetSketchLimitIsShared",
                "it raises the registers to the limit, and asks for the refusal to be for the bytes"),
            ["SetSketch.ReadPayload: registers"] = ("TestLoadWhatItBuilds.TestSetSketchReaderTakesARegisterCountPastTheOldCeiling",
                "it raises the registers past the old ceiling, and asks for the refusal to be for the bytes"),
            ["SublimeCountMinSketch.RequireCounts: counters"] = (
                "TestReadsAllocateOnlyWhatTheyHold.TestASublimeBlockOfCountersCutShortIsRefusedBeforeItIsPricedOrAllocated",
                "it cuts the payload a byte short of each block and asks for the price to stop at the block"),

            // Seen by the cut sweep alone, because the budget sample's claim is large: its values,
            // keys or items, read or priced unchecked, come to more than the bound at the cuts that
            // hold their count. A sample that came to hold few would leave these unseen.
            ["ThetaSketch.Price: values"] = ("TestReadsAllocateOnlyWhatTheyHold.TestEveryPriceRefusesWhatItsPayloadDoesNotHoldBeforePricingIt",
                "its budget sample holds thousands of values, and their buffer is past the bound at every cut that holds the count"),
            ["ThetaSketch.ReadPayload: values"] = ("TestReadsAllocateOnlyWhatTheyHold.TestEveryReaderRefusesWhatItDoesNotHoldBeforeAllocatingForIt",
                "its budget sample holds thousands of values, and their buffer is past the bound at every cut that holds the count"),
            ["TupleSketch.Price: keys"] = ("TestReadsAllocateOnlyWhatTheyHold.TestEveryPriceRefusesWhatItsPayloadDoesNotHoldBeforePricingIt",
                "its budget sample holds thousands of keys, and their buffers are past the bound at every cut that holds the count"),
            ["TupleSketch.ReadPayload: keys"] = ("TestReadsAllocateOnlyWhatTheyHold.TestEveryReaderRefusesWhatItDoesNotHoldBeforeAllocatingForIt",
                "its budget sample holds thousands of keys, and their buffers are past the bound at every cut that holds the count"),
            ["VarOpt.Price: items"] = ("TestReadsAllocateOnlyWhatTheyHold.TestEveryPriceRefusesWhatItsPayloadDoesNotHoldBeforePricingIt",
                "its budget sample's reservoir holds thousands of items, and its regions are past the bound at every cut that holds the count"),
            ["VarOpt.ReadPayload: items"] = ("TestReadsAllocateOnlyWhatTheyHold.TestEveryReaderRefusesWhatItDoesNotHoldBeforeAllocatingForIt",
                "its budget sample's reservoir holds thousands of items, and its regions are past the bound at every cut that holds the count"),
        };

        /// <summary>
        /// Every check in the library that holds a claimed count to the bytes after it, by
        /// <c>PayloadReader.RequireHolds</c>, named for its structure and method and for what it
        /// counts as its refusal names it, from the library's IL.
        /// </summary>
        private static List<string> HoldsChecks()
        {
            var holds = typeof(PayloadReader).GetMethod("RequireHolds", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(holds, "PayloadReader has no RequireHolds to look for");
            return LibraryCalls().Where(c => holds.Equals(c.Called))
                .Select(c => $"{Outermost(c.Caller.DeclaringType).Name}.{c.Caller.Name}: {c.LastString}")
                .Distinct().OrderBy(name => name, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Every check in the library that holds a claim to the bytes after it is held by an edited
        /// claim (<see cref="EditedClaims"/>) or names the test that holds it
        /// (<see cref="HeldElsewhere"/>), and every entry in either names a check the library has.
        /// </summary>
        /// <remarks>
        /// The roster is the library's IL: every call of <c>PayloadReader.RequireHolds</c>, with the
        /// string it counts, which is the last pushed before the call. A check added to any
        /// structure fails here until something says what holds it: a cut sees a check only when
        /// its writer's claim is large, and nothing else sees it unless a test edits the claim
        /// (#344). Each test named must be a test, so that a rename cannot leave an entry vouching
        /// for nothing, and the scan has to find the edited claims' checks, so that a decoder
        /// finding nothing cannot pass.
        /// </remarks>
        [TestMethod]
        public void TestEveryHoldsCheckIsHeldByAnEditedClaimOrNamesTheTestThatHoldsIt()
        {
            var found = HoldsChecks();
            var edited = EditedClaims.SelectMany(c => c.Checks.Select(check => $"{check}: {c.Counts}")).ToList();
            foreach (var check in edited)
            {
                Assert.Contains(check, found, $"the IL scan did not find {check}, which an edited claim holds, so it finds nothing it shows");
            }

            var unheld = found.Where(check => !edited.Contains(check) && !HeldElsewhere.ContainsKey(check)).ToList();
            Assert.IsEmpty(unheld,
                "these checks are held by no edited claim here and name no test that holds them; a cut sees one only when its " +
                "writer's claim is large, so edit the claim (EditedClaims) or name the test that kills the mutant dropping it " +
                "(HeldElsewhere): " + string.Join(", ", unheld));

            var stale = HeldElsewhere.Keys.Where(check => !found.Contains(check)).Concat(edited.Where(e => HeldElsewhere.ContainsKey(e))).ToList();
            Assert.IsEmpty(stale, "these entries name no check the library has, or are held twice: " + string.Join(", ", stale));

            var tests = typeof(TestReadsAllocateOnlyWhatTheyHold).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods().Where(m => m.GetCustomAttributes(typeof(TestMethodAttribute), true).Length > 0)
                    .Select(m => $"{t.Name}.{m.Name}"))
                .ToHashSet();
            foreach (var (check, (test, why)) in HeldElsewhere)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(why), $"{check} names {test} without saying why it sees the check");
                Assert.Contains(test, tests, $"{check} names {test}, which is not a test");
            }
        }

        private static Type Outermost(Type type)
        {
            while (type.DeclaringType is not null)
            {
                type = type.DeclaringType;
            }

            return type;
        }

        /// <summary>
        /// Every call the library's IL makes, with the method making it and the last string it
        /// pushed before it, decoded an instruction at a time.
        /// </summary>
        private static IEnumerable<(MethodBase Caller, MethodBase Called, string LastString)> LibraryCalls()
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                     BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var type in typeof(PersistenceFormat).Assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
                {
                    var il = method.GetMethodBody()?.GetILAsByteArray();
                    if (il is null)
                    {
                        continue;
                    }

                    string last = null;
                    for (var at = 0; at < il.Length;)
                    {
                        var value = (short)il[at++];
                        if (value == 0xFE)
                        {
                            value = unchecked((short)(0xFE00 | il[at++]));
                        }

                        var op = Instructions.Value[value];
                        if (op.OperandType == OperandType.InlineString)
                        {
                            last = method.Module.ResolveString(BitConverter.ToInt32(il, at));
                        }
                        else if (op.OperandType == OperandType.InlineMethod)
                        {
                            MethodBase called = null;
                            try
                            {
                                called = method.Module.ResolveMethod(
                                    BitConverter.ToInt32(il, at),
                                    method.DeclaringType is { IsGenericType: true } owner ? owner.GetGenericArguments() : null,
                                    method.IsGenericMethod ? method.GetGenericArguments() : null);
                            }
                            catch (ArgumentException)
                            {
                                // Generic over a context this one cannot give; never a check, which is not generic.
                            }

                            if (called is not null)
                            {
                                yield return (method, called, last);
                            }
                        }

                        at += op.OperandType switch
                        {
                            OperandType.InlineNone => 0,
                            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                            OperandType.InlineVar => 2,
                            OperandType.InlineI8 or OperandType.InlineR => 8,
                            OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, at),
                            _ => 4,
                        };
                    }
                }
            }
        }

        private static readonly Lazy<Dictionary<short, OpCode>> Instructions = new(() =>
            typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(field => (OpCode)field.GetValue(null))
                .ToDictionary(op => op.Value));

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
        /// The envelope of <paramref name="whole"/> with the <c>u32</c> at offset
        /// <paramref name="at"/> of its payload set to <paramref name="value"/>, its checksum
        /// made to match.
        /// </summary>
        private static byte[] Edited(byte[] whole, int at, uint value)
        {
            var bytes = (byte[])whole.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(PayloadStart + at), value);
            var crc = new Crc32();
            crc.Append(bytes.AsSpan(4, bytes.Length - 8));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - ChecksumLength), crc.GetCurrentHashAsUInt32());
            return bytes;
        }

        /// <summary>
        /// An invertible table's price refuses a run that disagrees with the table's shape where
        /// its reader does, with the reader's refusal, having priced exactly what the reader
        /// charged before refusing it: counts, key sums or hash sums.
        /// </summary>
        /// <remarks>
        /// No cut makes such a run, since every run a cut holds is the one its writer wrote, so
        /// each is made by one field, the checksum resealed: the cell count, a cell more, so that
        /// the counts disagree; the key size, a byte more, so that the key sums do; and the hash
        /// sums' own length, a word short, so that they do and a word is left over. The reader
        /// checks each run against the shape as it reads it, and charges the array it unpacks
        /// from the run after that check. A price that checked a run later than its reader would
        /// price what comes after, which the reader never charges, and admit the read at more
        /// than it takes. The table is the smallest the constructor builds, so that any array
        /// priced past the reader's charges is a difference of some bytes in a few hundred.
        /// </remarks>
        [TestMethod]
        public void TestAnInvertibleTablesPriceRefusesARunWhereItsReaderDoes()
        {
            const StructureId Id = StructureId.InvertibleBloomLookupTable;
            var table = new InvertibleBloomLookupTable(1, 1);
            table.Add([7]);
            var whole = table.ToByteArray();
            var length = whole.Length - PayloadStart - ChecksumLength;
            var payload = whole.AsSpan(PayloadStart, length);

            // u32 cells and u32 key size; then the counts, key sums and hash sums, each a run
            // whose u32 length comes before it.
            var cells = BinaryPrimitives.ReadUInt32LittleEndian(payload);
            var keySize = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
            var hashesAt = 2 * sizeof(uint) + sizeof(uint) + (int)cells * sizeof(long) + sizeof(uint) + (int)(cells * keySize);
            Assert.AreEqual((uint)(cells * sizeof(ulong)), BinaryPrimitives.ReadUInt32LittleEndian(payload[hashesAt..]),
                "premise: the walk of FORMAT.md's layout finds the hash sums' length");
            Assert.AreEqual(length, hashesAt + sizeof(uint) + (int)cells * sizeof(ulong),
                "premise: the hash sums end where the payload does");

            (string Run, int At, uint Value)[] cases =
            [
                ("the counts", 0, cells + 1),
                ("the key sums", 4, keySize + 1),
                ("the hash sums", hashesAt, (uint)((cells - 1) * sizeof(ulong))),
            ];
            var problems = new List<string>();
            foreach (var (run, at, value) in cases)
            {
                var edited = Edited(whole, at, value);
                var (priced, _, priceRefused) = WalkPrice(Id, edited, length);
                var readRefused = Assert.ThrowsExactly<InvalidDataException>(
                    () => Persistence.FromByteArray<InvertibleBloomLookupTable>(edited, ReadOptions.Unlimited),
                    $"{run}: the payload whose {run} disagree with its shape was read").Message;
                Assert.Contains("do not come to the byte counts", readRefused, $"premise: {run}: the reader refuses the run for the shape");
                if (priceRefused != readRefused)
                {
                    problems.Add($"{run}: its price refused it with \"{priceRefused ?? "nothing"}\", and its reader with \"{readRefused}\"");
                }

                var price = ReadBudget.Sum(PersistenceFormat.EnvelopeCost((ulong)length), priced);
                var exactly = new ReadBudget(price, Id);
                Assert.ThrowsExactly<InvalidDataException>(
                    () => Persistence.FromByteArray<InvertibleBloomLookupTable>(edited, new ReadOptions(exactly)));
                if (exactly.Spent != price)
                {
                    problems.Add($"{run}: priced {price:N0}, its reader charged {exactly.Spent:N0} before refusing it");
                }
            }

            Assert.IsEmpty(problems, $"{problems.Count} problems over the {cases.Length} runs: " + string.Join(" | ", problems));
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
