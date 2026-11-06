using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// A payload holding a byte past everything its structure stores is refused for that byte
    /// under exactly its own price, in either envelope: never for its budget (#343).
    /// </summary>
    /// <remarks>
    /// A read is priced whole before it allocates, and a price that finds its payload malformed
    /// is set aside, the read admitted at what it had priced by then. So a price that checks the
    /// payload's end before it prices its last array stops short of it on such a payload, and a
    /// reader that charges that array before checking the end is then refused for a budget its
    /// own price fits, naming a total under which it would only have been refused as malformed.
    /// <c>InvertibleBloomLookupTable</c>'s price was that way (TESTING.md, "A price prices each
    /// array where its reader charges it"), and so were <c>QuotientFilter</c>'s and
    /// <c>CountSketch</c>'s. A cut that holds a payload's last run holds the payload whole, so no
    /// cut of <c>TestReadsAllocateOnlyWhatTheyHold</c>'s sweep can show where that run is priced;
    /// a byte left over can.
    /// </remarks>
    [TestClass]
    public class TestBytesLeftOver
    {
        /// <summary>
        /// <paramref name="whole"/>, in either envelope, with one byte added after its payload
        /// and its length and checksums made to match, so that the byte is the only fault: the
        /// envelope, where its payload starts, and the payload's length before the byte.
        /// </summary>
        internal static (byte[] Bytes, int Start, int Length) WithAByteLeftOver(byte[] whole)
        {
            var large = LargeForm.IsLarge(whole);
            var start = large ? PersistenceFormat.LargeHeaderLength : PersistenceFormat.EnvelopeLength - 4;
            var length = whole.Length - (large ? PersistenceFormat.LargeEnvelopeLength : PersistenceFormat.EnvelopeLength);
            var bytes = new byte[whole.Length + 1];
            whole.AsSpan(0, start + length).CopyTo(bytes);

            // The checksums cover the header from the version on: the large form's header has
            // one of its own, over its fields, and the end checksum covers those fields and the
            // payload in each form.
            const int fieldsStart = 4;
            var fieldsEnd = large ? LargeForm.HeaderFieldsEnd : start;
            if (large)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(10), (ulong)length + 1);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fieldsEnd),
                    Crc32.HashToUInt32(bytes.AsSpan(fieldsStart, fieldsEnd - fieldsStart)));
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), (uint)length + 1);
            }

            var crc = new Crc32();
            crc.Append(bytes.AsSpan(fieldsStart, fieldsEnd - fieldsStart));
            crc.Append(bytes.AsSpan(start, length + 1));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), crc.GetCurrentHashAsUInt32());
            return (bytes, start, length);
        }

        /// <summary>
        /// A structure whose price checks the payload's end: walked on its own over
        /// <paramref name="whole"/> with a byte left over, its price stops at the byte, for it,
        /// and the read, under exactly the envelope and what the walk priced, is refused for the
        /// byte; under a byte less it is refused for its budget, naming that price, so the price
        /// worked out here is the one the read is admitted at.
        /// </summary>
        internal static void AssertRefusedForTheByteUnderItsOwnPrice(
            StructureId id, PayloadPrice price, Func<byte[], ReadOptions, object> read, byte[] whole)
        {
            var large = LargeForm.IsLarge(whole);
            var form = large ? "in the large form" : "in the ordinary envelope";
            var (longer, start, length) = WithAByteLeftOver(whole);

            var budget = new ReadBudget(long.MaxValue, id);
            var reader = new PayloadReader(longer.AsSpan(start, length + 1), budget, wide: large);
            string why = null;
            try
            {
                price(ref reader, LargeForm.Layout(longer));
            }
            catch (InvalidDataException e)
            {
                why = e.Message;
            }
            Assert.AreEqual(length, length + 1 - reader.Remaining,
                $"premise {form}: the {id}'s price walks its payload to its end, and stops at the byte left over: {why}");
            Assert.Contains("left over", why ?? "it walked to its end", $"premise {form}: the {id}'s price stops for the byte left over");
            var envelope = large ? PersistenceFormat.LargeEnvelopeCost : PersistenceFormat.EnvelopeCost((ulong)(length + 1));
            var priced = ReadBudget.Sum(envelope, budget.Charged);

            var refused = Assert.ThrowsExactly<InvalidDataException>(() => read(longer, new ReadOptions(priced)));
            Assert.IsFalse(refused.InnerException is ReadBudgetExceededException,
                $"{form}, under exactly its own price of {priced:N0}, the {id} was refused for its budget rather than for the byte left over: {refused.Message}");
            Assert.Contains("left over", refused.Message, $"{form}, under exactly its own price, the {id} was refused for another reason");

            var under = Assert.ThrowsExactly<InvalidDataException>(() => read(longer, new ReadOptions(priced - 1)));
            Assert.IsInstanceOfType<ReadBudgetExceededException>(under.InnerException,
                $"premise {form}: under a byte less than its price of {priced:N0}, the {id} is refused for its budget: {under.Message}");
            Assert.AreEqual(priced, ((ReadBudgetExceededException)under.InnerException).RequiredBytes,
                $"premise {form}: the {id}'s price worked out here is the one the read is admitted at");
        }

        /// <summary>
        /// Every structure, its read-budget sample written in each envelope with a byte added
        /// after the payload: refused for the byte with no limit, so the byte is a fault; refused
        /// for its budget under a byte less than the total a refusal under nothing names, naming
        /// it, so that total is the price the read is admitted at; and under exactly that price,
        /// refused for the byte, not for its budget.
        /// </summary>
        /// <remarks>
        /// The roster is <see cref="StructureId"/>, and a structure with no sample in
        /// <see cref="TestReadBudget.Samples"/> fails by name. Each price is found from the read
        /// rather than by walking the structure's price, because most prices do not walk to
        /// their payload's end: they read the fields they price from and skip the rest, and the
        /// reader checks the end. A price that checks it is held to stopping there, for it, by a
        /// test of its own: <see cref="TestQuotientFilter.TestAFilterWithAByteLeftOverIsRefusedForItUnderItsOwnPrice"/>
        /// and <see cref="TestCountSketch.TestASketchWithAByteLeftOverIsRefusedForItUnderItsOwnPrice"/>.
        /// </remarks>
        [TestMethod]
        [DynamicData(nameof(TestReadBudget.Structures), typeof(TestReadBudget))]
        public void TestEveryStructureWithAByteLeftOverIsRefusedForItUnderItsOwnPrice(string structure)
        {
            var id = Enum.Parse<StructureId>(structure);
            Assert.IsTrue(TestReadBudget.Samples.Value.TryGetValue(id, out var sample),
                $"{id} has no sample in TestReadBudget.Samples to add a byte to. Add one.");
            Assert.IsFalse(LargeForm.IsLarge(sample.Bytes), $"premise: {sample.Name} is not in the ordinary envelope");

            foreach (var written in new[] { sample, TestReadBudget.InTheLargeForm(sample) })
            {
                var (longer, _, _) = WithAByteLeftOver(written.Bytes);

                var unlimited = Assert.ThrowsExactly<InvalidDataException>(
                    () => written.Read(longer, ReadOptions.Unlimited), $"{written.Name} with a byte left over was read");
                Assert.Contains("left over", unlimited.Message, $"{written.Name} with a byte left over was refused for something else");

                var nothing = Assert.ThrowsExactly<InvalidDataException>(
                    () => written.Read(longer, new ReadOptions(0)), $"{written.Name} with a byte left over was read under nothing");
                Assert.IsInstanceOfType<ReadBudgetExceededException>(nothing.InnerException,
                    $"{written.Name} with a byte left over, under nothing, was refused for something other than its budget: {nothing.Message}");
                var price = ((ReadBudgetExceededException)nothing.InnerException).RequiredBytes;

                var under = Assert.ThrowsExactly<InvalidDataException>(() => written.Read(longer, new ReadOptions(price - 1)));
                Assert.IsInstanceOfType<ReadBudgetExceededException>(under.InnerException,
                    $"premise: {written.Name} with a byte left over, under a byte less than its price of {price:N0}, was not refused for its budget: {under.Message}");
                Assert.AreEqual(price, ((ReadBudgetExceededException)under.InnerException).RequiredBytes,
                    $"premise: {written.Name} with a byte left over named one total under nothing and another under a byte less than it");

                var at = Assert.ThrowsExactly<InvalidDataException>(() => written.Read(longer, new ReadOptions(price)));
                Assert.IsFalse(at.InnerException is ReadBudgetExceededException,
                    $"{written.Name} with a byte left over, under exactly its own price of {price:N0}, was refused for its budget " +
                    $"rather than for the byte: {at.Message}");
                Assert.Contains("left over", at.Message, $"{written.Name} with a byte left over, under its own price, was refused for another reason");
            }
        }
    }
}
