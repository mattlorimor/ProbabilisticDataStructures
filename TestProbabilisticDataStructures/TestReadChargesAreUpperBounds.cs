using System;
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
        private static IEnumerable<(string Read, long Charged, long Allocated, object Structure)> Measured(TestReadBudget.Sample sample)
        {
            foreach (var form in new[] { sample, TestReadBudget.InTheLargeForm(sample) })
            {
                var bytes = form.Bytes;
                var structure = form.Read(bytes, ReadOptions.Unlimited);
                var charged = Charged(form);

                MemoryStream stream = null;
                (string How, Func<object> Read, Action First)[] reads =
                [
                    ("as a byte array", () => form.Read(bytes, ReadOptions.Unlimited), null),
                    ("from a stream", () => form.ReadStream(stream, ReadOptions.Unlimited),
                        () => stream = new MemoryStream(bytes, writable: false)),
                ];
                foreach (var (how, read, first) in reads)
                {
                    var allocated = TestReadBudget.LeastAllocated(read, Repeats, first);
                    Console.WriteLine($"[UPPER-BOUND] {form.Name} {how}: charged {charged:N0}, allocated {allocated:N0}, over by {charged - allocated:N0}");
                    yield return ($"{form.Name} read {how}", charged, allocated, structure);
                }
            }
        }

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

            foreach (var (read, charged, allocated, restored) in Measured(TestReadBudget.Samples.Value[id]))
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
                foreach (var (read, charged, allocated, _) in Measured(more))
                {
                    if (allocated > charged)
                    {
                        problems.Add($"{read} allocated {allocated:N0} bytes and was charged {charged:N0}, {allocated - charged:N0} short");
                    }
                }
            }

            Assert.IsEmpty(problems, $"at {problems.Count} checks: " + string.Join(" | ", problems));
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
