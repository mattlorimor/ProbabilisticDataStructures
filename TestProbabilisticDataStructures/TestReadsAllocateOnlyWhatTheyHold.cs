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
    /// a claim first costs what the claim does.
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
            string SmallBecause = null, long Allowance = PerByte, string CostlyBecause = null);

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
        private static Swept ManyArrays<T>(string name, Func<T> build, long allowance = PerByte, string costlyBecause = null)
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
                CostlyBecause: costlyBecause);
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
        /// Every structure's reader, given a payload of its own cut short at each length, refuses
        /// it having allocated no more than the bound for the bytes it holds, under no budget and
        /// under the default one.
        /// </summary>
        [TestMethod]
        [DynamicData(nameof(Structures))]
        public void TestEveryReaderRefusesWhatItDoesNotHoldBeforeAllocatingForIt(string structure)
        {
            var id = Enum.Parse<StructureId>(structure);
            foreach (var sample in SweptFor(id))
            {
                AssertTheWholeLoads(sample);
                foreach (var options in new[] { ReadOptions.Unlimited, ReadOptions.Default })
                {
                    AssertEveryCutIsRefusedWithinTheBound(sample, options);
                }
            }
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
        /// Every cut of the sample is refused, and allocated no more than its bound before it
        /// was; and where the sample is allowed more than <see cref="PerByte"/> a byte, some cut
        /// under no budget allocated more than that, so that the allowance is not out of date.
        /// </summary>
        private static void AssertEveryCutIsRefusedWithinTheBound(Swept sample, ReadOptions options)
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

            var budget = options.IsUnlimited ? "no budget" : "the default budget";
            var problems = new List<string>();
            var costliest = 0L;
            for (var i = 0; i < cuts.Length; i++)
            {
                var held = PayloadStart + cuts[i] + ChecksumLength;
                var bound = BoundFor(held, sample.Allowance);
                costliest = Math.Max(costliest, allocated[i] - BoundFor(held));
                if (refusals[i] is not InvalidDataException refusal)
                {
                    problems.Add($"cut to {cuts[i]:N0} bytes, it was {(refusals[i] is null ? "read" : "refused with " + refusals[i].GetType().Name + ": " + refusals[i].Message)}");
                }
                else if (options.IsUnlimited && refusal.InnerException is ReadBudgetExceededException)
                {
                    problems.Add($"cut to {cuts[i]:N0} bytes, it was refused for a budget it was not given: {refusal.Message}");
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

            if (sample.CostlyBecause is not null && options.IsUnlimited)
            {
                Assert.IsGreaterThan(0, costliest,
                    $"{sample.Name} is allowed {sample.Allowance} bytes a byte because {sample.CostlyBecause}, and no cut " +
                    $"of it allocated more than the {PerByte} every structure is allowed, so the allowance is out of date");
            }
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
