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
    /// An envelope's length is held to the bytes there are before anything is priced or
    /// allocated for it: a payload's to what its stream holds, where the stream can say, in
    /// either form, and a nested envelope's to the run it is stored in.
    /// </summary>
    /// <remarks>
    /// The read budget held an ordinary envelope's length and nothing else did (#347). A
    /// header is the first claim a read meets, and the payload the first thing it allocates,
    /// so a held 128,827-byte <see cref="TopK"/> payload whose header claimed 2^30 bytes was
    /// read into a gibibyte array under no budget, and under the default budget was refused
    /// for its budget, which told its caller to pass a gibibyte rather than that the payload
    /// was cut short. A nested envelope was priced for its claim before its run was checked
    /// to hold it, the same way. The large form held its length to a stream that can seek
    /// already, and the ordinary envelope now does too. A stream that cannot seek does not
    /// say how much it holds, so from one of those the budget is still what stops a length.
    /// <para>
    /// The claims are edits of a payload this library wrote, one field each, the checksums
    /// resealed so that the length is the only lie (TESTING.md, after #340 and #344): one past
    /// what the bytes hold, which is where the check is decided, and the most each form's
    /// length field is let claim. Each is refused for the bytes it lacks under a budget of
    /// what it was priced at, under the default one and under none, having allocated no more
    /// than the read of a claim past what its field may claim, which the check before this one
    /// refuses, and <see cref="Refusing"/> besides for the longer message. The length the bytes hold is the control, so the check
    /// is neither missing nor stricter than the bytes. The tightest budget is read first, and
    /// a claim of gigabytes is read no further once it shows a problem, so that a reader that
    /// allocated for it would be refused for a budget rather than allocate it here.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestHeldEnvelopeLengths
    {
        private const int HeaderLength = 14;
        private const int LengthOffset = 10;
        private const int ChecksumLength = 4;

        /// <summary>
        /// What a refusal of an envelope's length past the bytes held may allocate past the
        /// refusal of a length past what its field may claim, which the header's check makes
        /// just before it: both are refused before anything is priced, with a message naming
        /// the claim, and this one's names the bytes held too.
        /// </summary>
        private const long Refusing = 1 << 10;

        /// <summary>The fragment both forms' refusal of a length past the bytes held says.</summary>
        private const string TooFew = "too few for the";

        /// <summary>What the ordinary header's check says of a length no array holds.</summary>
        private const string PastTheOrdinaryField = "cannot be read into a single array";

        /// <summary>What the large header's check says of a length no stream holds.</summary>
        private const string PastTheLargeField = "more than a stream can hold";

        /// <summary>
        /// Bytes a stream holds before the envelope, which a read from it is positioned past, so
        /// that what the stream holds is counted from its position and not from its start.
        /// </summary>
        private const int Before = 64;

        public static IEnumerable<object[]> Structures =>
            Enum.GetValues<StructureId>().Select(id => new object[] { id.ToString() });

        /// <summary>The ways a held payload is read: as a byte array, and from a stream that can seek.</summary>
        private static (string Name, Func<byte[], ReadOptions, Func<object>> Read)[] HeldReads(TestReadBudget.Sample sample) =>
        [
            ("as a byte array", (bytes, options) => () => sample.Read(bytes, options)),
            ($"from a stream positioned past {Before} bytes", (bytes, options) =>
            {
                var held = new byte[Before + bytes.Length];
                bytes.CopyTo(held, Before);
                var stream = new MemoryStream(held, writable: false);
                return () =>
                {
                    stream.Position = Before;
                    return sample.ReadStream(stream, options);
                };
            }),
        ];

        /// <summary>
        /// The ordinary envelope of <paramref name="whole"/> with its header claiming
        /// <paramref name="claim"/> bytes of payload, and its checksum, at the end of the bytes
        /// as they stand, made to match.
        /// </summary>
        private static byte[] Claiming(byte[] whole, uint claim, int lengthAt = LengthOffset)
        {
            var bytes = (byte[])whole.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(lengthAt), claim);
            return Resealed(bytes);
        }

        /// <summary>An ordinary envelope with its checksum worked out again over the bytes as they stand.</summary>
        private static byte[] Resealed(byte[] bytes)
        {
            var crc = new Crc32();
            crc.Append(bytes.AsSpan(4, bytes.Length - 4 - ChecksumLength));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - ChecksumLength), crc.GetCurrentHashAsUInt32());
            return bytes;
        }

        /// <summary>
        /// A payload in the large form with its header claiming <paramref name="claim"/> bytes
        /// of payload, both checksums made to match.
        /// </summary>
        private static byte[] ClaimingLarge(byte[] whole, ulong claim)
        {
            var bytes = (byte[])whole.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(LengthOffset), claim);
            return LargeForm.Reseal(bytes);
        }

        /// <summary>
        /// Reads <paramref name="read"/>, counting what it allocates, and returns its refusal, or
        /// null if it read.
        /// </summary>
        private static InvalidDataException Refusal(Func<object> read, out long allocated)
        {
            InvalidDataException refused = null;
            allocated = NoGCRegion.Allocated(() =>
            {
                refused = null;
                try
                {
                    return read();
                }
                catch (InvalidDataException e)
                {
                    refused = e;
                    return e;
                }
            });
            return refused;
        }

        /// <summary>
        /// The problem with how <paramref name="read"/> refused a claim of
        /// <paramref name="claim"/> bytes under <paramref name="budget"/>, or null: it has to be
        /// refused for the bytes it lacks, not for its budget, having allocated no more than
        /// <paramref name="bound"/>.
        /// </summary>
        private static string Problem(string what, ulong claim, string budget, Func<object> read, long bound)
        {
            var refused = Refusal(read, out var allocated);
            return refused is null
                ? $"{what} claiming {claim:N0} bytes was read under {budget}"
                : refused.InnerException is ReadBudgetExceededException
                    ? $"{what} claiming {claim:N0} bytes was refused for {budget}: {refused.Message}"
                    : !refused.Message.Contains(TooFew, StringComparison.Ordinal)
                        ? $"{what} claiming {claim:N0} bytes was refused under {budget} for something else, having allocated " +
                          $"{allocated:N0} bytes: {refused.Message}"
                        : allocated > bound
                            ? $"{what} claiming {claim:N0} bytes allocated {allocated:N0} bytes under {budget} before it was " +
                              $"refused, past the {bound:N0} a claim past its field and {Refusing:N0} allow"
                            : null;
        }

        /// <summary>
        /// Reads each claim under each budget, tightest first, and adds what is wrong to
        /// <paramref name="problems"/>. A claim past what a no-GC region counts is read no
        /// further once it shows a problem: a reader that allocated for it would allocate
        /// gigabytes under no budget.
        /// </summary>
        private static void ReadEach(
            string what, IEnumerable<(ulong Claim, Func<ReadOptions, Func<object>> Read, long Price)> claims,
            long bound, List<string> problems)
        {
            foreach (var (claim, read, price) in claims)
            {
                (string Name, ReadOptions Options)[] budgets =
                [
                    ($"a budget of its own price, {price:N0}", new ReadOptions(price)),
                    ("the default budget", ReadOptions.Default),
                    ("no budget", ReadOptions.Unlimited),
                ];
                foreach (var (budget, options) in budgets)
                {
                    var problem = Problem(what, claim, budget, read(options), bound);
                    if (problem is not null)
                    {
                        problems.Add(problem);
                        if (claim > NoGCRegion.Room)
                        {
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Every structure's payload in the ordinary envelope, held, its header claiming one
        /// byte more than the bytes after it hold with the checksum, or the most the field is
        /// let claim, is refused for the bytes it lacks before anything is priced or allocated
        /// for the claim, under any budget, as a byte array and from a stream that can seek; and
        /// the length the bytes hold reads, from a stream holding more after it as well.
        /// </summary>
        /// <remarks>
        /// Nothing is priced before the claim is refused, so its own price is nothing, and a
        /// budget of nothing is where a read that priced the claim first is refused for its
        /// budget. The stream is positioned past bytes before the envelope, so that a check
        /// counting from the stream's start rather than its position lets the claim one past
        /// the bytes through.
        /// </remarks>
        [TestMethod]
        [DynamicData(nameof(Structures))]
        public void TestEveryHeldPayloadClaimingMoreThanItHoldsIsRefusedBeforeItIsPriced(string structure)
        {
            var id = Enum.Parse<StructureId>(structure);
            var sample = TestReadBudget.Samples.Value[id];
            var bytes = sample.Bytes;
            Assert.IsFalse(LargeForm.IsLarge(bytes), $"premise: {id}'s sample is in the ordinary envelope");
            var length = (uint)(bytes.Length - HeaderLength - ChecksumLength);
            var problems = new List<string>();

            foreach (var (how, read) in HeldReads(sample))
            {
                var what = $"{id}'s payload read {how}";
                var holds = Refusal(read(bytes, ReadOptions.Unlimited), out _);
                if (holds is not null)
                {
                    problems.Add($"{what}, claiming the {length:N0} bytes it holds, was refused: {holds.Message}");
                }

                var past = Refusal(read(Claiming(bytes, (uint)Array.MaxLength + 1), ReadOptions.Unlimited), out var pastTheField);
                Assert.Contains(PastTheOrdinaryField, past?.Message ?? "it was read",
                    $"premise: {what} claiming more than one array holds is refused for that");
                var bound = pastTheField + Refusing;

                ReadEach(what,
                    new[] { length + 1, (uint)Array.MaxLength }.Select(claim =>
                        ((ulong)claim, (Func<ReadOptions, Func<object>>)(options => read(Claiming(bytes, claim), options)), 0L)),
                    bound, problems);
            }

            // More after the envelope than it claims is the stream's, not the envelope's.
            var longer = new byte[bytes.Length + Before];
            bytes.CopyTo(longer, 0);
            var refused = Refusal(() => sample.ReadStream(new MemoryStream(longer, writable: false), ReadOptions.Unlimited), out _);
            if (refused is not null)
            {
                problems.Add($"{id}'s payload, from a stream holding {Before} bytes more after it, was refused: {refused.Message}");
            }

            Assert.IsEmpty(problems, $"{id}, at {problems.Count} checks: " + string.Join(" | ", problems));
        }

        /// <summary>
        /// Every structure's payload in the large form, its header claiming one byte more than
        /// the stream holds after it with the checksum, or the most a stream can hold, both
        /// checksums resealed, is refused for the bytes it lacks before anything is priced or
        /// allocated for the claim, under any budget; and the length the bytes hold reads.
        /// </summary>
        /// <remarks>
        /// The large form held its length to a stream that can seek before the ordinary
        /// envelope did, and this was green when it was added (#347): it is there so that the
        /// two forms are held to one rule by one test, and was shown red by dropping the large
        /// form's check. Its read holds no payload, so a claim costs it the price of what the
        /// structure's fields claim rather than an array, and a budget of nothing is where a
        /// read that priced first is refused for its budget.
        /// </remarks>
        [TestMethod]
        [DynamicData(nameof(Structures))]
        public void TestEveryLargeFormPayloadClaimingMoreThanItsStreamHoldsIsRefusedBeforeItIsPriced(string structure)
        {
            var id = Enum.Parse<StructureId>(structure);
            var sample = TestReadBudget.InTheLargeForm(TestReadBudget.Samples.Value[id]);
            var bytes = sample.Bytes;
            var length = (ulong)(bytes.Length - PersistenceFormat.LargeHeaderLength - ChecksumLength);
            var problems = new List<string>();

            foreach (var (how, read) in HeldReads(sample))
            {
                var what = $"{id}'s payload in the large form read {how}";
                var holds = Refusal(read(bytes, ReadOptions.Unlimited), out _);
                if (holds is not null)
                {
                    problems.Add($"{what}, claiming the {length:N0} bytes it holds, was refused: {holds.Message}");
                }

                var past = Refusal(read(ClaimingLarge(bytes, (ulong)long.MaxValue + 1), ReadOptions.Unlimited), out var pastTheField);
                Assert.Contains(PastTheLargeField, past?.Message ?? "it was read",
                    $"premise: {what} claiming more than a stream holds is refused for that");
                var bound = pastTheField + Refusing;

                ReadEach(what,
                    new[] { length + 1, (ulong)long.MaxValue }.Select(claim =>
                        (claim, (Func<ReadOptions, Func<object>>)(options => read(ClaimingLarge(bytes, claim), options)), 0L)),
                    bound, problems);
            }

            Assert.IsEmpty(problems, $"{id}, at {problems.Count} checks: " + string.Join(" | ", problems));
        }

        /// <summary>
        /// The first <paramref name="after"/> bytes of <paramref name="whole"/>'s payload behind
        /// its header, which claims <paramref name="claim"/> bytes, the large header's own
        /// checksum made to match; there is no room for the checksum after the payload.
        /// </summary>
        private static byte[] CutAfterHeader(byte[] whole, int after, ulong claim, bool large)
        {
            var header = large ? PersistenceFormat.LargeHeaderLength : HeaderLength;
            var bytes = whole.AsSpan(0, header + after).ToArray();
            if (!large)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(LengthOffset), (uint)claim);
                return bytes;
            }

            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(LengthOffset), claim);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(LargeForm.HeaderFieldsEnd),
                Crc32.HashToUInt32(bytes.AsSpan(4, LargeForm.HeaderFieldsEnd - 4)));
            return bytes;
        }

        /// <summary>
        /// Every structure's payload, in either form, cut to its header and fewer bytes after it
        /// than its checksum takes, is refused for the bytes it lacks before anything is priced
        /// or allocated, under any budget, whatever the header claims: its writer's length, or
        /// none.
        /// </summary>
        /// <remarks>
        /// Fewer bytes than the checksum's four leave no room for even an empty payload, and the
        /// checksum's four taken from them, as an unsigned number, wrap to more than any length
        /// can claim, so that a check that only compared the two would let every claim through.
        /// The cuts are at nothing after the header, where a header alone is all there is, and at
        /// a byte short of the checksum. The control is the cut at the checksum's four: an empty
        /// payload and its checksum, resealed, which holds what it claims and so is not refused
        /// for lacking it.
        /// </remarks>
        [TestMethod]
        [DynamicData(nameof(Structures))]
        public void TestEveryPayloadCutShortOfItsChecksumIsRefusedBeforeItIsPriced(string structure)
        {
            var id = Enum.Parse<StructureId>(structure);
            var ordinary = TestReadBudget.Samples.Value[id];
            var problems = new List<string>();

            foreach (var (form, sample, large) in new[]
            {
                ("the ordinary envelope", ordinary, false),
                ("the large form", TestReadBudget.InTheLargeForm(ordinary), true),
            })
            {
                var bytes = sample.Bytes;
                var header = large ? PersistenceFormat.LargeHeaderLength : HeaderLength;
                var length = (ulong)(bytes.Length - header - ChecksumLength);

                foreach (var (how, read) in HeldReads(sample))
                {
                    var what = $"{id}'s payload in {form} read {how}";
                    var field = large ? ClaimingLarge(bytes, (ulong)long.MaxValue + 1) : Claiming(bytes, (uint)Array.MaxLength + 1);
                    var past = Refusal(read(field, ReadOptions.Unlimited), out var pastTheField);
                    Assert.Contains(large ? PastTheLargeField : PastTheOrdinaryField, past?.Message ?? "it was read",
                        $"premise: {what}, claiming more than its field may, is refused for that");
                    var bound = pastTheField + Refusing;

                    foreach (var after in new[] { 0, ChecksumLength - 1 })
                    {
                        ReadEach($"{what}, cut to {after} bytes after its header,",
                            new[] { 0UL, length }.Select(claim =>
                                (claim, (Func<ReadOptions, Func<object>>)(options => read(CutAfterHeader(bytes, after, claim, large), options)), 0L)),
                            bound, problems);
                    }

                    // The control: with room for the checksum and nothing else, an empty payload
                    // is held whole, so the read gets past the envelope, and is refused, if it
                    // is, by its structure's reader, not for the bytes it lacks.
                    var empty = CutAfterHeader(bytes, ChecksumLength, 0, large);
                    var refused = Refusal(read(large ? LargeForm.Reseal(empty) : Resealed(empty), ReadOptions.Unlimited), out _);
                    if (refused?.Message.Contains(TooFew, StringComparison.Ordinal) == true)
                    {
                        problems.Add($"{what}, cut to its header and the {ChecksumLength} bytes of its checksum and " +
                            $"claiming nothing, was refused as short of its checksum: {refused.Message}");
                    }
                }
            }

            Assert.IsEmpty(problems, $"{id}, at {problems.Count} checks: " + string.Join(" | ", problems));
        }

        /// <summary>
        /// A stream that can only be read forward, as a network stream or a pipe is, which does
        /// not say how much it holds.
        /// </summary>
        internal sealed class OneWay(byte[] bytes) : Stream
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

        /// <summary>
        /// From a stream that cannot seek, which does not say how much it holds, a length is
        /// still held by the budget, as FORMAT.md says: a payload claiming more than the
        /// default budget is refused for that budget, one claiming a byte more than it holds is
        /// refused where the stream ends, and the length it holds reads.
        /// </summary>
        /// <remarks>
        /// So the check asks a stream how much it holds only where it can answer: asked of this
        /// one, it throws <see cref="NotSupportedException"/>. The payload is the one #347 was
        /// found with, a <see cref="TopK"/>'s, and the claim past the budget is the most the
        /// field is let claim.
        /// </remarks>
        [TestMethod]
        public void TestAPayloadFromAStreamThatCannotSeekIsHeldToItsBudgetInstead()
        {
            var sample = TestReadBudget.Samples.Value[StructureId.TopK];
            var bytes = sample.Bytes;
            var length = (uint)(bytes.Length - HeaderLength - ChecksumLength);

            sample.ReadStream(new OneWay(bytes), ReadOptions.Default);

            var past = Assert.ThrowsExactly<InvalidDataException>(
                () => sample.ReadStream(new OneWay(Claiming(bytes, (uint)Array.MaxLength)), ReadOptions.Default),
                "a payload claiming more than the default budget, from a stream that cannot seek, was read");
            Assert.IsInstanceOfType<ReadBudgetExceededException>(past.InnerException,
                $"a payload claiming more than the default budget, from a stream that cannot seek, was refused for something else: {past.Message}");

            var ended = Assert.ThrowsExactly<InvalidDataException>(
                () => sample.ReadStream(new OneWay(Claiming(bytes, length + 1)), ReadOptions.Default),
                "a payload claiming a byte more than it holds, from a stream that cannot seek, was read");
            Assert.IsNull(ended.InnerException, $"a payload claiming a byte more than it holds was refused for its budget: {ended.Message}");
            Assert.Contains("The stream ended after", ended.Message,
                $"a payload claiming a byte more than it holds, from a stream that cannot seek, was refused for something else: {ended.Message}");
        }

        /// <summary>
        /// Where each nested envelope in an ordinary payload begins: the offset of the run's
        /// length prefix, for each run whose bytes are one whole ordinary envelope.
        /// </summary>
        private static List<int> NestedRuns(byte[] envelope)
        {
            var found = new List<int>();
            var end = envelope.Length - ChecksumLength;
            for (var at = HeaderLength; at + sizeof(uint) + HeaderLength + ChecksumLength <= end; at++)
            {
                var run = BinaryPrimitives.ReadUInt32LittleEndian(envelope.AsSpan(at));
                if (run <= end - at - sizeof(uint) && LargeForm.IsEnvelope(envelope.AsSpan(at + sizeof(uint), (int)run)))
                {
                    found.Add(at);
                }
            }

            return found;
        }

        /// <summary>
        /// The structures whose price calls <c>PersistenceFormat.PriceNested</c>, from the IL of
        /// each one's <c>Price</c>.
        /// </summary>
        /// <remarks>
        /// Matched as the five bytes of the call: <c>call</c>, 0x28, and the method's own
        /// metadata token, which is what a call to a static method in the same assembly is.
        /// Bytes that are not that instruction would have to spell that token by chance, and a
        /// match that is not a call can only add a structure, which then fails the comparison
        /// below for holding no nested envelope, so this can err only towards failing.
        /// </remarks>
        private static List<StructureId> PricesNested()
        {
            var nested = typeof(PersistenceFormat).GetMethod("PriceNested", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(nested, "PersistenceFormat has no PriceNested");
            var call = new byte[5];
            call[0] = 0x28;
            BinaryPrimitives.WriteInt32LittleEndian(call.AsSpan(1), nested.MetadataToken);

            return Enum.GetValues<StructureId>()
                .Where(id => PriceOf(id).Method.GetMethodBody()!.GetILAsByteArray()!.AsSpan().IndexOf(call) >= 0)
                .ToList();
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
        /// The structure's price walked over the payload of <paramref name="envelope"/>, held:
        /// what it priced, and why it stopped, or null if it walked to its end.
        /// </summary>
        private static (long Priced, string Why) WalkPrice(StructureId id, byte[] envelope)
        {
            var version = BinaryPrimitives.ReadUInt16LittleEndian(envelope.AsSpan(4));
            var budget = new ReadBudget(long.MaxValue, id);
            var reader = new PayloadReader(envelope.AsSpan(HeaderLength, envelope.Length - HeaderLength - ChecksumLength), budget);
            try
            {
                PriceOf(id)(ref reader, version);
                return (budget.Charged, null);
            }
            catch (InvalidDataException e)
            {
                return (budget.Charged, e.Message);
            }
        }

        /// <summary>
        /// A nested envelope claiming one byte more than its run holds after its header with its
        /// checksum, or the most the field is let claim, the outer checksum resealed, is refused
        /// for the bytes it lacks: by the outer price before it prices anything for the claim,
        /// and by the outer read, under a budget of its own price, the default one and none,
        /// before anything is charged or allocated for it. The length its run holds is priced
        /// with its payload and reads. The roster is every structure whose budget sample holds a
        /// nested envelope, and it is the structures whose price prices one.
        /// </summary>
        /// <remarks>
        /// The outer reader holds the run whole, and the nested envelope is read from a stream
        /// over it, so its length is held to the run by the same check as a held payload's. The
        /// price looks into the envelope where it lies, and checks it as the read will. A price
        /// that priced the claim before checking it priced a gibibyte for the edit that claims
        /// one, and admitted the read under no budget at that (#347). What the price comes to
        /// before the nested length is used is what it comes to for a nested length past what
        /// one array holds, which the nested header's check refuses just before this one; the
        /// edit is held to exactly that, and the read to what reading that payload allocates and
        /// <see cref="Refusing"/>. The nested checksum is left as written: the read is refused
        /// before it is reached.
        /// </remarks>
        [TestMethod]
        public void TestANestedEnvelopeClaimingMoreThanItsRunHoldsIsRefusedBeforeItIsPriced()
        {
            var holding = Enum.GetValues<StructureId>()
                .Where(id => NestedRuns(TestReadBudget.Samples.Value[id].Bytes).Count > 0)
                .ToList();
            Assert.IsNotEmpty(holding, "premise: no structure's budget sample holds a nested envelope");
            CollectionAssert.AreEquivalent(PricesNested(), holding,
                "the structures whose budget sample holds a nested envelope are not the ones whose price prices one");

            var problems = new List<string>();
            foreach (var id in holding)
            {
                var sample = TestReadBudget.Samples.Value[id];
                var bytes = sample.Bytes;
                var outer = PersistenceFormat.EnvelopeCost((ulong)(bytes.Length - HeaderLength - ChecksumLength));
                var at = NestedRuns(bytes)[0];
                var header = at + sizeof(uint);
                var run = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
                var length = run - HeaderLength - ChecksumLength;
                var what = $"{id}'s nested envelope at {header - HeaderLength:N0}";

                var pastTheField = Claiming(bytes, (uint)Array.MaxLength + 1, header + LengthOffset);
                var (before, stopped) = WalkPrice(id, pastTheField);
                Assert.Contains(PastTheOrdinaryField, stopped ?? "it walked to its end",
                    $"premise: {what}, claiming more than one array holds, stops the price for that");
                var past = Refusal(() => sample.Read(pastTheField, ReadOptions.Unlimited), out var pastAllocated);
                Assert.Contains(PastTheOrdinaryField, past?.Message ?? "it was read",
                    $"premise: {what}, claiming more than one array holds, is refused for that");
                var bound = pastAllocated + Refusing;

                var (whole, walked) = WalkPrice(id, bytes);
                if (walked is not null || whole < before + PersistenceFormat.EnvelopeCost(length))
                {
                    problems.Add($"{what}, claiming the {length:N0} bytes its run holds, was priced {whole - before:N0} bytes " +
                        $"past a claim past its field, not its payload's {PersistenceFormat.EnvelopeCost(length):N0}: {walked ?? "it walked to its end"}");
                }

                var claims = new List<(ulong, Func<ReadOptions, Func<object>>, long)>();
                foreach (var claim in new[] { length + 1, (uint)Array.MaxLength })
                {
                    var edited = Claiming(bytes, claim, header + LengthOffset);
                    var (priced, why) = WalkPrice(id, edited);
                    if (priced != before || why is null || !why.Contains(TooFew, StringComparison.Ordinal))
                    {
                        problems.Add($"{what}, claiming {claim:N0} bytes, was priced {priced - before:N0} bytes past a claim past its field, " +
                            $"and the price stopped: {why ?? "it walked to its end"}");
                        if (ReadBudget.Sum(outer, priced) > NoGCRegion.Room)
                        {
                            continue;
                        }
                    }

                    claims.Add((claim, options => () => sample.Read(edited, options), ReadBudget.Sum(outer, priced)));
                }

                ReadEach(what, claims, bound, problems);
            }

            Assert.IsEmpty(problems, $"at {problems.Count} checks: " + string.Join(" | ", problems));
        }
    }
}
