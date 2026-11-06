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
    /// A run of bytes that claims more than the payload holds is refused as the payload
    /// ending, whatever it claims.
    /// </summary>
    /// <remarks>
    /// The reader checked a run against the payload as <c>offset + count &gt; length</c>
    /// in an <c>int</c>. A claim within the offset of <c>int.MaxValue</c> made that sum
    /// wrap negative, so it passed as "not past the end", and the read then failed in
    /// <c>Slice</c> with an <see cref="ArgumentOutOfRangeException"/> -- not the
    /// <see cref="InvalidDataException"/> every other payload the library will not read
    /// gets. The window is the width of the offset: a run at offset 12 wraps from a claim
    /// of 2,147,483,636, one at offset 1,000 from 2,147,482,648. Below it the sum did not
    /// wrap and the refusal was right, which is why a payload claiming a mere
    /// 2,147,483,584 was refused properly and one claiming 2,147,483,647 was not.
    /// <para>
    /// The unit tests hold the reader to the claims on either side of that edge, at
    /// several offsets, and to the exact fit on the other side of the bound. The sweep
    /// holds every structure to it through its own payload, and finds the runs itself
    /// rather than being told where they are.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestPayloadReader
    {
        private const string FixturePrefix = "TestProbabilisticDataStructures.fixtures.";

        /// <summary>The payload begins after the fixed-size envelope header.</summary>
        private const int PayloadStart = 14;

        /// <summary>The length prefix of a run, which is a u32.</summary>
        private const int PrefixLength = 4;

        private static ReadBudget Unlimited() => new(long.MaxValue, StructureId.BloomFilter);

        /// <summary>
        /// A payload of a u32 claim, preceded by <paramref name="before"/> bytes and
        /// followed by <paramref name="after"/>, none of which the claim can be right
        /// about when it is large.
        /// </summary>
        private static byte[] Payload(int before, uint claim, int after)
        {
            var bytes = new byte[before + PrefixLength + after];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(before), claim);
            return bytes;
        }

        /// <summary>
        /// What refuses a run claiming <paramref name="claim"/> bytes whose length prefix
        /// ends at <paramref name="offset"/>, of a payload that does not hold them: more than
        /// one array holds, past <see cref="Array.MaxLength"/>, which a run is read into and
        /// so is checked first; otherwise the payload ending, which is where the offset
        /// arithmetic wrapped.
        /// </summary>
        /// <remarks>
        /// The two meet at <see cref="Array.MaxLength"/>, not at <c>int.MaxValue</c>: a run
        /// between them used to pass the first check, be refused by the second only because
        /// no held payload is that long, and, read from a stream rather than held, reach an
        /// allocation no array can make. So below an offset of 57 the claims that wrapped are
        /// all past one array, and the wrap is reached at the offsets above it.
        /// </remarks>
        internal static string RefusalFor(long claim, int offset) => claim > Array.MaxLength
            ? "cannot be read into a single array"
            : $"while reading {claim} more at offset {offset}.";

        /// <summary>
        /// Reads past <paramref name="skipped"/> bytes and then a run. A method of its
        /// own because a reader cannot be captured by a lambda.
        /// </summary>
        private static byte[] ReadRunAfter(byte[] payload, int skipped)
        {
            var reader = new PayloadReader(payload, Unlimited());
            for (var i = 0; i < skipped; i++)
            {
                reader.ReadByte();
            }

            return reader.ReadBytes();
        }

        /// <summary>
        /// The claims either side of the edge for a run whose length prefix ends at
        /// <paramref name="offset"/>: the largest that does not wrap, the smallest that
        /// does, and the largest there is.
        /// </summary>
        internal static int[] ClaimsAround(int offset)
        {
            var lastSafe = int.MaxValue - offset;

            // The edge, worked out in 64 bits so the test does not have the defect it is
            // looking for: the first claim whose sum with the offset passes int.MaxValue.
            Assert.AreEqual((long)int.MaxValue + 1, (long)offset + (lastSafe + 1),
                "the claim meant to wrap does not");
            Assert.AreEqual((long)int.MaxValue, (long)offset + lastSafe,
                "the claim meant to be the last that does not wrap is not");

            return new[] { lastSafe, lastSafe + 1, int.MaxValue }.Distinct().ToArray();
        }

        /// <summary>
        /// The claims a probe of a DDSketch payload found on either side of the edge --
        /// 2,147,483,584 refused as it should be, and 2,147,483,647 not -- and the ones
        /// between them, on a payload that holds eight bytes after the prefix. Every one
        /// is refused: as the payload ending, or, past one array, as more than one holds.
        /// </summary>
        [TestMethod]
        [DataRow(9)]
        [DataRow(1_000_000)]
        [DataRow(2_147_483_584)]
        [DataRow(2_147_483_643)]
        [DataRow(2_147_483_644)]
        [DataRow(2_147_483_645)]
        [DataRow(2_147_483_646)]
        [DataRow(int.MaxValue)]
        public void TestARunClaimingMoreThanTheRestOfThePayloadIsRefused(int claim)
        {
            var payload = Payload(0, (uint)claim, 8);

            var ex = Assert.ThrowsExactly<InvalidDataException>(
                () => ReadRunAfter(payload, 0),
                $"a run claiming {claim:N0} bytes of a twelve-byte payload was not refused as one");

            StringAssert.Contains(ex.Message, RefusalFor(claim, 4), ex.Message);
        }

        /// <summary>
        /// The edge moves with the offset, and the claims either side of it are refused
        /// wherever it is. The offsets are the ones a run is found at: right after a
        /// length, and further in.
        /// </summary>
        [TestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(7)]
        [DataRow(1_000)]
        public void TestTheClaimsEitherSideOfTheEdgeAreRefusedAtEveryOffset(int skipped)
        {
            var payload = Payload(skipped, 0, 8);
            var offset = skipped + PrefixLength;

            foreach (var claim in ClaimsAround(offset))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(skipped), (uint)claim);

                var ex = Assert.ThrowsExactly<InvalidDataException>(
                    () => ReadRunAfter(payload, skipped),
                    $"a run at offset {offset} claiming {claim:N0} bytes was not refused as " +
                    "the payload ending");

                StringAssert.Contains(ex.Message, RefusalFor(claim, offset), ex.Message);
            }
        }

        /// <summary>
        /// The bound is exact: a run that fills the rest of the payload is read, and one
        /// byte more is the payload ending. Without this a check refusing a little too
        /// much would pass everything above.
        /// </summary>
        [TestMethod]
        public void TestARunThatFillsTheRestOfThePayloadIsReadAndOneByteMoreIsNot()
        {
            var fits = Payload(0, 8, 8);
            for (var i = 0; i < 8; i++)
            {
                fits[PrefixLength + i] = (byte)(i + 1);
            }

            var reader = new PayloadReader(fits, Unlimited());
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, reader.ReadBytes());
            Assert.AreEqual(0, reader.Remaining, "the run did not take the rest of the payload");
            reader.ExpectEnd();

            var one = Payload(0, 9, 8);
            var ex = Assert.ThrowsExactly<InvalidDataException>(() => ReadRunAfter(one, 0));
            StringAssert.Contains(ex.Message, "while reading 9 more at offset 4.");
        }

        /// <summary>
        /// A claim a u32 can make that an <c>int</c> cannot is refused, as it always was.
        /// It is next to the claims above because it is the other side of the same
        /// <c>int.MaxValue</c>.
        /// </summary>
        /// <remarks>
        /// The reason is asserted, not only the type. Cast to an <c>int</c> the claim is
        /// negative, which the bounds check refuses too, as the payload ending; so with the
        /// guard for this removed the exception is the same type and only the reason
        /// tells the two apart. That mutation survived the type alone.
        /// </remarks>
        [TestMethod]
        [DataRow(0x7FFF_FFC8L)]
        [DataRow(0x7FFF_FFFFL)]
        [DataRow(0x8000_0000L)]
        [DataRow(0xFFFF_FFFFL)]
        public void TestARunClaimingMoreThanAnArrayHoldsIsRefused(long claim)
        {
            var payload = Payload(0, (uint)claim, 8);

            var ex = Assert.ThrowsExactly<InvalidDataException>(() => ReadRunAfter(payload, 0));
            StringAssert.Contains(ex.Message, "cannot be read into a single array");
        }

        /// <summary>
        /// The largest array is the edge: a run claiming as many bytes as one holds goes on
        /// to be refused as the payload ending, and one byte more as more than an array
        /// holds. The first rows above are that byte more and the largest <c>int</c>, which
        /// the reader let through until a run could be read from a stream.
        /// </summary>
        [TestMethod]
        public void TestARunOfTheLargestArrayGoesOnToTheBoundsCheck()
        {
            Assert.AreEqual(0x7FFF_FFC7, Array.MaxLength, "the largest array is not the one the rows above straddle");

            var largest = Assert.ThrowsExactly<InvalidDataException>(
                () => ReadRunAfter(Payload(0, (uint)Array.MaxLength, 8), 0));
            StringAssert.Contains(largest.Message, $"while reading {Array.MaxLength} more at offset 4.", largest.Message);

            var past = Assert.ThrowsExactly<InvalidDataException>(
                () => ReadRunAfter(Payload(0, (uint)Array.MaxLength + 1, 8), 0));
            StringAssert.Contains(past.Message, "cannot be read into a single array", past.Message);
        }

        /// <summary>
        /// An empty run is read as the one empty array the runtime keeps, and is charged and
        /// priced nothing, from a held payload and from a stream alike (#329); a run of one
        /// byte is an array of its own, and is charged, priced and allocated as one. The reads
        /// are counted in a no-GC region.
        /// </summary>
        /// <remarks>
        /// From a held payload an empty run was already that array, since an empty span copied
        /// out is it, but was charged an array's header for it, 24 bytes; from a stream it was a
        /// new array of nothing, allocated and charged at 24. The run of one byte is the other
        /// direction, so that a cost of nothing for every run fails.
        /// </remarks>
        [TestMethod]
        public void TestAnEmptyRunIsTheEmptyArrayAndCostsNothing()
        {
            foreach (var length in new[] { 0, 1 })
            {
                var cost = length == 0 ? 0 : ReadBudget.ArrayOf(1, 1);
                var payload = Payload(0, (uint)length, length + 3);
                foreach (var fromStream in new[] { false, true })
                {
                    var how = $"a run of {length} read from {(fromStream ? "a stream" : "a held payload")}";
                    PayloadSource source = null;
                    ReadBudget budget = null;
                    byte[] run = null;
                    var allocated = NoGCRegion.Allocated(
                        () => run = ReadRunFrom(payload, source, budget),
                        first: () =>
                        {
                            budget = Unlimited();
                            source = fromStream ? new PayloadSource(new MemoryStream(payload, writable: false)) : null;
                        });

                    Assert.HasCount(length, run, $"{how} came back {run.Length} bytes long");
                    Assert.AreEqual(length == 0, ReferenceEquals(Array.Empty<byte>(), run),
                        $"{how} {(length == 0 ? "was not" : "was")} the runtime's empty array");
                    Assert.AreEqual(cost, budget.Charged, $"{how} was charged {budget.Charged} bytes");
                    Assert.AreEqual(cost, allocated, $"{how} allocated {allocated} bytes");

                    var priced = Unlimited();
                    Assert.AreEqual(length, PriceRunFrom(payload, fromStream, priced));
                    Assert.AreEqual(cost, priced.Charged, $"the price of {how} was {priced.Charged} bytes");
                }
            }
        }

        /// <summary>
        /// Reads the run at the start of <paramref name="payload"/>: from
        /// <paramref name="source"/>, a stream over it, or from the payload held when that is
        /// null.
        /// </summary>
        private static byte[] ReadRunFrom(byte[] payload, PayloadSource source, ReadBudget budget)
        {
            var reader = source is null
                ? new PayloadReader(payload, budget)
                : new PayloadReader(source, payload.Length, budget);
            return reader.ReadBytes();
        }

        /// <summary>Prices the run at the start of <paramref name="payload"/>, held or from a stream over it.</summary>
        private static int PriceRunFrom(byte[] payload, bool fromStream, ReadBudget budget)
        {
            var reader = fromStream
                ? new PayloadReader(new PayloadSource(new MemoryStream(payload, writable: false)), payload.Length, budget)
                : new PayloadReader(payload, budget);
            return reader.PriceBytes();
        }

        /// <summary>
        /// Every structure that writes a run refuses one that claims more than its payload
        /// holds, at the claims either side of where the offset arithmetic wrapped: as the
        /// payload ending, or, for a claim past one array, as more than one holds.
        /// </summary>
        /// <remarks>
        /// The roster is <see cref="StructureId"/>: a structure is swept if a payload of
        /// it holds a run, and exempted, with its reason, if none does. The exemption is
        /// checked in the same breath, so one naming a structure that has since gained a
        /// run fails as loudly as a structure missing from the sweep.
        /// <para>
        /// Where the runs are is found rather than listed. A payload is probed at every
        /// offset with a u32 claiming more than the whole payload, checksum repaired, and
        /// a run's length prefix is where the refusal is the reader's "payload ended"
        /// naming that claim at that offset -- the offset in the message is the end of the
        /// prefix, so it picks out exactly one place. That needs no claim large enough to
        /// wrap, so it finds the runs just as well on a reader that mishandles them, and
        /// it needs no layout kept here, which for this many structures would be a second
        /// copy of FORMAT.md to drift. A count, or a field of any other kind, is refused
        /// for a different reason and is not mistaken for one.
        /// </para>
        /// <para>
        /// The payloads are the samples the corruption sweep uses and the stored payloads
        /// the version sweep reads. The second is not decoration: a cuckoo filter
        /// writes no run today, and reads one from every entry of the layouts before it,
        /// which only a payload of those layouts reaches.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestEveryStructureRefusesARunClaimingMoreThanThePayloadHolds()
        {
            var samples = TestPersistenceAllStructures.SamplePayloads();
            var readers = samples
                .GroupBy(s => s.Id)
                .ToDictionary(g => g.Key, g => g.First().Read);

            var payloads = samples
                .Select(s => (s.Id, Name: $"the sample {s.Id} payload", s.Read, s.Bytes))
                .Concat(StoredPayloads(readers))
                .ToArray();

            var withRuns = new HashSet<StructureId>();
            var failures = new List<string>();
            var claimsTried = 0;

            foreach (var (id, name, read, clean) in payloads)
            {
                var wrong = new List<string>();

                foreach (var at in RunPrefixes(clean, read))
                {
                    var offset = at - PayloadStart + PrefixLength;
                    withRuns.Add(id);

                    foreach (var claim in ClaimsAround(offset))
                    {
                        claimsTried++;
                        var expected = RefusalFor(claim, offset);

                        try
                        {
                            read(Poked(clean, at, (uint)claim));
                            wrong.Add($"offset {offset}, {claim:N0} bytes: read");
                        }
                        catch (InvalidDataException ex) when (ex.Message.Contains(expected))
                        {
                            // Refused for the claim it made.
                        }
                        catch (Exception ex)
                        {
                            wrong.Add($"offset {offset}, {claim:N0} bytes: {ex.GetType().Name}");
                        }
                    }
                }

                if (wrong.Count > 0)
                {
                    failures.Add($"{name}: {wrong.Count} claims were not refused for what they claim, " +
                        $"the first at {wrong[0]}");
                }
            }

            // Collected rather than stopped at the first, so that a red run says how far
            // the defect reaches.
            Assert.IsEmpty(failures, Environment.NewLine + string.Join(Environment.NewLine, failures));

            // The sweep tried something: a roster of structures none of which was probed
            // would be exempt from every check below and pass them all.
            Assert.IsGreaterThan(0, claimsTried, "no run was found in any payload");

            StructureRoster.AssertCoversEveryStructure("run lengths", withRuns, NoRun);
        }

        /// <summary>
        /// The structures no payload of which holds a run, each with what it holds instead:
        /// the exemptions of every sweep that probes for runs, in either form of the
        /// envelope, and checked by each, so one that gains a run fails both.
        /// </summary>
        internal static readonly (StructureId Id, string Why)[] NoRun =
        {
            (StructureId.CountMinSketch,
                "its counters are u64 cells in a matrix the header sizes; no byte run"),
            (StructureId.MinHashSignature,
                "a u32 count of u64 values, each a field; no byte run"),
            (StructureId.SimHashSignature, "one u64 value; no byte run"),
            (StructureId.ThetaSketch, "a u32 count of u64 values, each a field; no byte run"),
            (StructureId.TupleSketch,
                "a u32 count of u64 values and of f64 summaries, each a field; no byte run"),
            (StructureId.SetSketch, "u16 registers a header sizes, each a field; no byte run"),
            (StructureId.SublimeCountMinSketch,
                "u64 counters in rows and records a header counts; no byte run"),
            (StructureId.PrivateCountMinSketch,
                "f64 cells in a matrix the header sizes; no byte run"),
            (StructureId.DpswSketch,
                "its sketches are written as bodies of fields within the window's own payload; " +
                "no byte run"),
            (StructureId.Grafite, "u64 words a u32 counts, each a field; no byte run"),
            (StructureId.InfiniFilter, "u64 words a u32 counts, each a field; no byte run"),
            (StructureId.MementoFilter, "u64 words a u32 counts, each a field; no byte run"),
        };

        /// <summary>
        /// The frame with a u32 overwritten at an offset within it, and the checksum
        /// repaired, so that what refuses it is the reader and not the CRC.
        /// </summary>
        private static byte[] Poked(byte[] frame, int at, uint value)
        {
            var bytes = (byte[])frame.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(bytes.Length - 4),
                Crc32.HashToUInt32(bytes.AsSpan(4, bytes.Length - 8)));
            return bytes;
        }

        /// <summary>
        /// Where in the frame the length prefixes of runs are: the offsets at which a u32
        /// claiming more than the frame holds is refused as the payload ending, at the
        /// offset just after it.
        /// </summary>
        internal static List<int> RunPrefixes(byte[] clean, Func<byte[], object> read)
        {
            var found = new List<int>();

            // More than any payload of this length can hold, and nowhere near the
            // edge, so the answer does not depend on the arithmetic being tested.
            var probe = (uint)clean.Length;

            // A prefix is followed by the run it counts, which a payload that was
            // written holds, so one that points past the end is not a prefix. That
            // spares a read at the offsets inside the data of a large structure.
            var payloadEnd = clean.Length - 4;

            for (var at = PayloadStart; at + PrefixLength <= payloadEnd; at++)
            {
                var stated = BinaryPrimitives.ReadUInt32LittleEndian(clean.AsSpan(at));
                if (at + PrefixLength + (long)stated > payloadEnd)
                {
                    continue;
                }

                try
                {
                    read(Poked(clean, at, probe));
                }
                catch (InvalidDataException ex)
                {
                    var offset = at - PayloadStart + PrefixLength;
                    if (ex.Message.Contains($"while reading {probe} more at offset {offset}."))
                    {
                        found.Add(at);
                    }
                }
                catch (Exception)
                {
                    // Not a run: some other field, refused or choked on in its own way.
                }
            }

            return found;
        }

        /// <summary>
        /// Every stored payload, each with the reader of its structure. Read from the
        /// embedded fixtures themselves, so one added is swept without being named.
        /// </summary>
        internal static IEnumerable<(StructureId Id, string Name, Func<byte[], object> Read, byte[] Bytes)>
            StoredPayloads(Dictionary<StructureId, Func<byte[], object>> readers)
        {
            var assembly = typeof(TestPayloadReader).Assembly;
            var names = assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(FixturePrefix, StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.IsNotEmpty(names, "no fixtures are embedded, so none was swept");

            foreach (var name in names)
            {
                using var resource = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"resource {name} is not embedded");
                using var buffer = new MemoryStream();
                resource.CopyTo(buffer);
                var bytes = buffer.ToArray();

                var id = (StructureId)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6));
                Assert.IsTrue(readers.ContainsKey(id),
                    $"{name} holds {id}, which no sample payload reads");

                yield return (id, $"the stored {name[FixturePrefix.Length..]}", readers[id], bytes);
            }
        }
    }
}
