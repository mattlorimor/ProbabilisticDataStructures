using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// A read is charged at least what it allocates: every structure's, in either form of the
    /// envelope, read as a byte array and from a stream, with nothing it allocates left out of
    /// its charges, and no more than <see cref="Over"/> charged that it does not allocate,
    /// unless the structure states why its price can only bound what its read allocates.
    /// </summary>
    /// <remarks>
    /// A budget is a defence only if what it is charged is an upper bound on what the read
    /// allocates (TESTING.md). <see cref="TestReadBudget"/> holds each read to its charges and a
    /// slack of four kilobytes, for the fixed objects a read makes whatever it holds, and every
    /// read was found to allocate more than it was charged, by a few bytes to a few hundred, all
    /// of it inside that slack (#346): the stream <c>FromByteArray</c> makes and the read's own
    /// budget were charged nowhere, a checksum's state was charged as a small object of 64 bytes
    /// where it is 24, and most readers made objects they did not charge, their structure's own
    /// among them, and some made objects and threw them away. So here there is no slack in that
    /// direction. What a read allocates is counted in a no-GC region, the least of
    /// <see cref="Repeats"/>, and what it is charged is the total its refusal names under a
    /// budget of nothing, which is what a budget of it loads.
    /// <para>
    /// The bound the other way is small and stated, so that an overcharge cannot grow unseen
    /// either. Where a price can only bound what its read allocates, because what decides it is
    /// past the fields the price reads, the structure has an allowance on top, with its reason
    /// (<see cref="Allowances"/>), and its sample has to need it. The roster is
    /// <see cref="StructureId"/>, through the read-budget samples, which are held to it. The
    /// further samples, there for charges that grow with a count, are held to the first
    /// direction.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestReadChargesAreUpperBounds
    {
        /// <summary>How many times each read is counted, the least of which is what it allocates.</summary>
        private const int Repeats = 5;

        /// <summary>
        /// The most a read may be charged past what it allocates, an allowance aside.
        /// </summary>
        /// <remarks>
        /// What it covers: a read from a stream is charged the stream <c>FromByteArray</c>
        /// makes, 64 bytes it did not allocate, since nothing tells the two reads apart; an
        /// object charged at <see cref="ReadBudget.SmallObject"/>, the general bound, is charged
        /// up to 40 bytes past its size, a few such objects a read; and a nested read is charged
        /// a margin over its stream and options (<c>PersistenceFormat.NestedReadCost</c>). The
        /// most any read was over when this was set, its allowance aside, was 256 bytes, a
        /// scalable filter's from a stream.
        /// </remarks>
        private const long Over = 512;

        private const string HeapIndex =
            "its heap's index, a dictionary, is charged 2n + 7 slots for n elements, a four-byte bucket and a 32-byte " +
            "entry each, and the runtime sizes it to a prime of its own choosing, at least n: the next it keeps or finds, " +
            "and by Bertrand's postulate there is always a prime below 2n. So the read is charged at most n + 7 slots " +
            "more than the index takes. The runtime's choice is its own and can change between releases, so the price " +
            "does not copy it";

        /// <summary>
        /// The structures whose read may be charged more than <see cref="Over"/> past what it
        /// allocates, why, and how much more, given what was read.
        /// </summary>
        private static readonly Dictionary<StructureId, (string Why, Func<object, long> Allowance)> Allowances = new()
        {
            [StructureId.TopK] = (HeapIndex, read => IndexSlack(((TopK)read).Elements().Length)),
            [StructureId.HeavyKeeper] = (HeapIndex, read => IndexSlack(((HeavyKeeper)read).Elements().Length)),
            [StructureId.DpswSketch] = (
                "every small object a window holds, each segment, substream and list, is charged ReadBudget.SmallObject, " +
                "a deliberately conservative bound for one object, and a window holds hundreds of them: its sample was " +
                "measured 5,576 bytes over, 24 on each segment and substream of 40 bytes and 32 on each list",
                _ => 6 << 10),
            [StructureId.SublimeCountMinSketch] = (
                "ValeCounterArray.RestoreCost charges every chunk's pool as overflowing, a tails array and its place in " +
                "the dictionary of them for every chunk of every row, since from version 10 a price seeks past the " +
                "counters and cannot tell which overflow (FORMAT.md); its sample's pools mostly do not, and it was " +
                "measured 121,856 bytes over",
                _ => 128 << 10),
        };

        /// <summary>What the index of a heap of <paramref name="elements"/> may be charged past what it takes.</summary>
        private static long IndexSlack(int elements) => ((long)elements + 7) * (sizeof(int) + 32);

        public static IEnumerable<object[]> Structures =>
            Enum.GetValues<StructureId>().Select(id => new object[] { id.ToString() });

        /// <summary>The total a read of <paramref name="sample"/> is charged: what its refusal under nothing names.</summary>
        private static long Charged(TestReadBudget.Sample sample)
        {
            var refused = Assert.ThrowsExactly<InvalidDataException>(
                () => sample.Read(sample.Bytes, new ReadOptions(0)), $"{sample.Name} loaded under a budget of nothing");
            Assert.IsInstanceOfType<ReadBudgetExceededException>(refused.InnerException,
                $"{sample.Name} was refused under a budget of nothing for something else: {refused.Message}");
            return ((ReadBudgetExceededException)refused.InnerException).RequiredBytes;
        }

        /// <summary>
        /// What reading <paramref name="sample"/> is charged and allocates, in the ordinary
        /// envelope and the large form, as a byte array and from a stream made before the read,
        /// with what the read returned.
        /// </summary>
        private static IEnumerable<(string Read, bool Large, bool Streamed, long Charged, long Allocated, object Structure)> Measured(
            TestReadBudget.Sample sample)
        {
            foreach (var (form, large) in new[] { (sample, false), (TestReadBudget.InTheLargeForm(sample), true) })
            {
                var bytes = form.Bytes;
                var structure = form.Read(bytes, ReadOptions.Unlimited);
                var charged = Charged(form);

                MemoryStream stream = null;
                (string How, bool Streamed, Func<object> Read, Action First)[] reads =
                [
                    ("as a byte array", false, () => form.Read(bytes, ReadOptions.Unlimited), null),
                    ("from a stream", true, () => form.ReadStream(stream, ReadOptions.Unlimited),
                        () => stream = new MemoryStream(bytes, writable: false)),
                ];
                foreach (var (how, streamed, read, first) in reads)
                {
                    var allocated = TestReadBudget.LeastAllocated(read, Repeats, first);
                    Console.WriteLine($"[UPPER-BOUND] {form.Name} {how}: charged {charged:N0}, allocated {allocated:N0}, over by {charged - allocated:N0}");
                    yield return ($"{form.Name} read {how}", large, streamed, charged, allocated, structure);
                }
            }
        }

        private static readonly ConcurrentDictionary<StructureId, (string Read, bool Large, bool Streamed, long Charged, long Allocated, object Structure)[]>
            MeasuredSamples = new();

        /// <summary>
        /// What <see cref="Measured"/> finds for <paramref name="id"/>'s sample, measured once for
        /// the tests that each look at every structure's.
        /// </summary>
        private static (string Read, bool Large, bool Streamed, long Charged, long Allocated, object Structure)[] MeasuredOnce(StructureId id) =>
            MeasuredSamples.GetOrAdd(id, _ => Measured(TestReadBudget.Samples.Value[id]).ToArray());

        /// <summary>
        /// Every structure's read is charged at least what it allocates, in the ordinary
        /// envelope and the large form, as a byte array and from a stream made before the read,
        /// and no more than <see cref="Over"/> past it, and its allowance, if it has one, which
        /// its read has to need.
        /// </summary>
        [TestMethod]
        [DynamicData(nameof(Structures))]
        public void TestEveryReadIsChargedAtLeastWhatItAllocates(string structure)
        {
            var id = Enum.Parse<StructureId>(structure);
            var allowed = Allowances.TryGetValue(id, out var allowance);
            var problems = new List<string>();

            foreach (var (read, _, _, charged, allocated, restored) in MeasuredOnce(id))
            {
                var over = charged - allocated;
                var most = Over + (allowed ? allowance.Allowance(restored) : 0);
                if (over < 0)
                {
                    problems.Add($"{read} allocated {allocated:N0} bytes and was charged {charged:N0}, {-over:N0} short");
                }
                else if (over > most)
                {
                    problems.Add($"{read} allocated {allocated:N0} bytes and was charged {charged:N0}, {over:N0} over, " +
                        $"past the {most:N0} allowed");
                }
                else if (allowed && over <= Over)
                {
                    problems.Add($"{read} is allowed {most - Over:N0} bytes past {Over:N0} because {allowance.Why}, and was charged " +
                        $"only {over:N0} past what it allocated, so the allowance is out of date");
                }
            }

            Assert.IsEmpty(problems, $"{id}, at {problems.Count} checks: " + string.Join(" | ", problems));
        }

        /// <summary>
        /// The further read-budget samples, each there because a charge grows with a count that
        /// the first sample of its structure keeps small, are charged at least what they
        /// allocate, in either form, as a byte array and from a stream.
        /// </summary>
        /// <remarks>
        /// Held to the one direction: what they are charged past what they allocate grows with
        /// the same counts, by the margin of every small object, and no constant bound fits it.
        /// What one of them allocates uncharged grows with them too, which is what they are for.
        /// </remarks>
        [TestMethod]
        public void TestEveryFurtherSampleIsChargedAtLeastWhatItAllocates()
        {
            var problems = new List<string>();
            foreach (var (_, more) in TestReadBudget.Further.Value.Values.SelectMany(samples => samples))
            {
                foreach (var (read, _, _, charged, allocated, _) in Measured(more))
                {
                    if (allocated > charged)
                    {
                        problems.Add($"{read} allocated {allocated:N0} bytes and was charged {charged:N0}, {allocated - charged:N0} short");
                    }
                }
            }

            Assert.IsEmpty(problems, $"at {problems.Count} checks: " + string.Join(" | ", problems));
        }

        /// <summary>
        /// What every read shares is charged with no slack beyond what the JIT can take away:
        /// the envelope around its structure, in either form, and the objects the read itself
        /// makes. In each form the read charged least past what it allocates is charged nothing
        /// past it as a byte array, and from a stream only the stream it did not make, or past
        /// either by no more than a checksum's state.
        /// </summary>
        /// <remarks>
        /// Every read's charge holds what every read shares, so a charge there past what it
        /// allocates would put every read in that form over by at least as much, inside
        /// <see cref="Over"/>, where nothing else would see it. A checksum's state charged as a
        /// small object, 40 bytes past its size, was that (#346).
        /// <see cref="TestWhatEveryReadMakesOnceIsChargedWhatItAllocates"/> holds each object the
        /// read makes to its size; this holds what the envelope's price makes of them.
        /// <para>
        /// The checksum's state is the margin because a read can allocate less than it did
        /// when it was cold. Run in the whole suite, after it had run reads many times, every
        /// read in the ordinary envelope as a byte array was charged at least 24 bytes past
        /// what it allocated, where run alone the least was nothing; with
        /// <c>DOTNET_JitObjectStackAllocation=0</c> the whole suite met the exact bound. So once
        /// a read is compiled hot, the JIT keeps an object of every read on the stack, taken to
        /// be the checksum: of what every read makes it is the one of that size, and it does not
        /// leave the read. How many reads are hot when they are counted varies from run to run.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestWhatEveryReadSharesIsChargedWithoutSlack()
        {
            var least = new Dictionary<(bool Large, bool Streamed), (long Over, string Read)>();
            foreach (var id in Enum.GetValues<StructureId>())
            {
                foreach (var (read, large, streamed, charged, allocated, _) in MeasuredOnce(id))
                {
                    var over = charged - allocated;
                    if (!least.TryGetValue((large, streamed), out var was) || over < was.Over)
                    {
                        least[(large, streamed)] = (over, read);
                    }
                }
            }

            Assert.HasCount(4, least, "premise: reads were measured in both forms, as a byte array and from a stream");
            foreach (var ((large, streamed), (over, read)) in least)
            {
                var expected = streamed ? PersistenceFormat.ByteArrayStreamCost : 0;
                var what = $"{(large ? "the large form" : "the ordinary envelope")} {(streamed ? "from a stream" : "as a byte array")}";
                Assert.IsGreaterThanOrEqualTo(expected, over,
                    $"a read in {what}, {read}, was charged {over:N0} bytes past what it allocates, less than {expected:N0}");
                Assert.IsLessThanOrEqualTo(expected + PersistenceFormat.ChecksumStateCost, over,
                    $"every read in {what} was charged at least {over:N0} bytes past what it allocates, the least {read}, " +
                    $"where {expected:N0} is what every read shares and a checksum's state what the JIT may keep off the heap");
            }
        }

        /// <summary>
        /// <paramref name="ordinary"/>, an ordinary envelope, with every envelope a run of its
        /// payload holds written again in the large form, by the library, and itself kept
        /// ordinary: <paramref name="nested"/> is how many were.
        /// </summary>
        /// <remarks>
        /// A run that holds an envelope is found as a length followed by that many bytes that
        /// are one whole ordinary envelope, and the envelopes those hold go with it.
        /// </remarks>
        private static byte[] HoldingLargeEnvelopes(byte[] ordinary, out int nested)
        {
            const int header = 14, prefix = sizeof(uint);
            var end = ordinary.Length - sizeof(uint);
            var payload = new MemoryStream();
            var field = new byte[prefix];
            var copied = header;
            nested = 0;
            for (var at = header; at + prefix + PersistenceFormat.EnvelopeLength <= end; at++)
            {
                var length = BinaryPrimitives.ReadUInt32LittleEndian(ordinary.AsSpan(at));
                if (length > end - at - prefix || !LargeForm.IsEnvelope(ordinary.AsSpan(at + prefix, (int)length)))
                {
                    continue;
                }

                var held = ordinary.AsSpan(at + prefix, (int)length).ToArray();
                var structure = TestReadBudget.Samples.Value[LargeForm.StructureOf(held)].Read(held, ReadOptions.Unlimited);
                var large = LargeForm.Below(0, () => PersistenceFormat.ToByteArray((IPayloadBody)structure));
                Assert.IsTrue(LargeForm.IsLarge(large), "premise: an envelope written below a threshold of nothing is in the large form");

                payload.Write(ordinary, copied, at - copied);
                BinaryPrimitives.WriteUInt32LittleEndian(field, (uint)large.Length);
                payload.Write(field);
                payload.Write(large);
                nested++;
                copied = at + prefix + (int)length;
                at = copied - 1;
            }

            payload.Write(ordinary, copied, end - copied);
            var bytes = new byte[PersistenceFormat.EnvelopeLength + payload.Length];
            ordinary.AsSpan(0, header).CopyTo(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), (uint)payload.Length);
            payload.ToArray().CopyTo(bytes, header);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - sizeof(uint)),
                Crc32.HashToUInt32(bytes.AsSpan(4, bytes.Length - 4 - sizeof(uint))));
            return bytes;
        }

        /// <summary>
        /// A structure in the ordinary envelope that holds envelopes in the large form is
        /// charged its price, and at least what it allocates: each envelope it holds is read as a
        /// read nested in it, from the run it was copied from, and is charged no budget or
        /// stream of its own, which the read around it has (#346).
        /// </summary>
        /// <remarks>
        /// No writer makes this: an envelope is in the large form when its payload is past the
        /// threshold, and the payload around it is longer. A reader takes it, since a nested
        /// structure is in whichever form its own payload calls for, whatever form the payload
        /// around it is in (FORMAT.md, The large form), and it is the one read in the large form
        /// that is both nested and held, so the one way to reach what such a read is charged. The
        /// roster is the samples that hold an envelope.
        /// </remarks>
        [TestMethod]
        public void TestALargeEnvelopeHeldByAnOrdinaryOneIsChargedAsANestedRead()
        {
            var holding = new List<StructureId>();
            var problems = new List<string>();

            foreach (var (id, sample) in TestReadBudget.Samples.Value)
            {
                var bytes = HoldingLargeEnvelopes(sample.Bytes, out var nested);
                if (nested == 0)
                {
                    continue;
                }

                holding.Add(id);
                var mixed = sample with { Name = $"{sample.Name} holding what it holds in the large form ({nested:N0} envelopes)", Bytes = bytes };
                var total = Charged(mixed);

                var unlimited = new ReadBudget(long.MaxValue, id);
                mixed.Read(bytes, new ReadOptions(unlimited));
                if (unlimited.Spent != unlimited.Charged)
                {
                    problems.Add($"{mixed.Name} was priced at {unlimited.Charged:N0} bytes and charged {unlimited.Spent:N0}");
                }

                try
                {
                    mixed.Read(bytes, new ReadOptions(total));
                }
                catch (InvalidDataException e)
                {
                    problems.Add($"{mixed.Name} named {total:N0} bytes and was refused under them: {e.Message}");
                }

                MemoryStream stream = null;
                foreach (var (how, read, first) in new (string, Func<object>, Action)[]
                {
                    ("as a byte array", () => mixed.Read(bytes, ReadOptions.Unlimited), null),
                    ("from a stream", () => mixed.ReadStream(stream, ReadOptions.Unlimited),
                        () => stream = new MemoryStream(bytes, writable: false)),
                })
                {
                    var allocated = TestReadBudget.LeastAllocated(read, Repeats, first);
                    Console.WriteLine($"[HELD-LARGE] {mixed.Name} {how}: charged {total:N0}, allocated {allocated:N0}");
                    if (allocated > total)
                    {
                        problems.Add($"{mixed.Name} read {how} allocated {allocated:N0} bytes and was charged {total:N0}, " +
                            $"{allocated - total:N0} short");
                    }
                }
            }

            Assert.IsNotEmpty(holding, "premise: no sample holds an envelope, so none was read holding one in the large form");
            Assert.IsEmpty(problems, $"at {problems.Count} checks: " + string.Join(" | ", problems));
        }

        /// <summary>
        /// An object a read makes is charged its measured size, or a small object, the general
        /// bound for one, where that is larger, as <c>ReadBudget.Object</c> says.
        /// </summary>
        /// <remarks>
        /// The sweep cannot see the floor: an object charged its own size is charged what it
        /// allocates, so it is the bound a size measured on one runtime keeps on another.
        /// </remarks>
        [TestMethod]
        public void TestAnObjectIsChargedItsSizeOrASmallObjectWhicheverIsLarger()
        {
            Assert.AreEqual(ReadBudget.SmallObject, ReadBudget.Object(ReadBudget.SmallObject - 8),
                "an object smaller than a small object was charged less than one");
            Assert.AreEqual(ReadBudget.SmallObject, ReadBudget.Object(ReadBudget.SmallObject),
                "an object the size of a small object was charged other than that");
            Assert.AreEqual(ReadBudget.SmallObject + 8, ReadBudget.Object(ReadBudget.SmallObject + 8),
                "an object larger than a small object was charged other than its size");
        }

        /// <summary>Every allowance says why its structure's price can only bound its read.</summary>
        [TestMethod]
        public void TestEveryAllowanceSaysWhy()
        {
            foreach (var (id, (why, _)) in Allowances)
            {
                Assert.IsTrue(Enum.IsDefined(id), $"an allowance is for {(ushort)id}, which is no structure");
                Assert.IsFalse(string.IsNullOrWhiteSpace(why), $"{id} has an allowance without a reason");
            }
        }

        /// <summary>
        /// What every read makes once, whatever it holds, is charged what it allocates: a
        /// checksum's state, the read's own budget, and the stream a byte array is read through.
        /// </summary>
        /// <remarks>
        /// Each is a constant measured on a 64-bit runtime, and each is in every read's charge, so
        /// a runtime that makes one larger fails here by name, as well as in the sweep. A checksum
        /// was charged as a small object, 64 bytes, and the budget and the stream not at all
        /// (#346). The first of each is made before any is counted, so that what the runtime does
        /// once is not taken for it.
        /// </remarks>
        [TestMethod]
        public void TestWhatEveryReadMakesOnceIsChargedWhatItAllocates()
        {
            var bytes = new byte[16];
            (string What, long Charged, Func<object> Make)[] made =
            [
                ("a checksum's state", PersistenceFormat.ChecksumStateCost, () => new Crc32()),
                ("a read's budget", PersistenceFormat.ReadBudgetCost, () => ReadBudget.For(ReadOptions.Unlimited, StructureId.BloomFilter)),
                ("the stream a byte array is read through", PersistenceFormat.ByteArrayStreamCost, () => new MemoryStream(bytes, writable: false)),
            ];

            foreach (var (what, charged, make) in made)
            {
                make();
                Assert.AreEqual(charged, TestReadBudget.LeastAllocated(make, Repeats, null),
                    $"{what} is charged {charged:N0} bytes, and that is not what one allocates");
            }
        }
    }
}
