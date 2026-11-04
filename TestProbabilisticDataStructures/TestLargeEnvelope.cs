using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Writes the large form of the envelope at sizes the suite can hold, by lowering the
    /// threshold the writer reads, on the calling thread only, and reads what it writes.
    /// </summary>
    /// <remarks>
    /// The large form is for payloads past 2 GiB, and no test can build one. The writer
    /// reads <see cref="PersistenceFormat.LargeFormThreshold"/> itself, so a lowered one
    /// sends a small payload down the path a large one takes, through the code that ships.
    /// </remarks>
    internal static class LargeForm
    {
        /// <summary>Where the large header's own checksum begins: after the fields it covers.</summary>
        internal const int HeaderFieldsEnd = 18;

        /// <summary>
        /// Runs <paramref name="write"/> with every payload longer than
        /// <paramref name="threshold"/> bytes written in the large form, on this thread,
        /// and puts the threshold back afterwards.
        /// </summary>
        internal static T Below<T>(long threshold, Func<T> write)
        {
            var before = PersistenceFormat.LoweredLargeFormThreshold;
            PersistenceFormat.LoweredLargeFormThreshold = threshold;
            try
            {
                return write();
            }
            finally
            {
                PersistenceFormat.LoweredLargeFormThreshold = before;
            }
        }

        /// <summary>Writes a structure of any type through its own <c>WriteTo</c>.</summary>
        internal static byte[] Write(object structure)
        {
            var writeTo = structure.GetType().GetMethod("WriteTo", new[] { typeof(Stream) })
                ?? throw new InvalidOperationException($"{structure.GetType().Name} has no WriteTo(Stream)");

            using var stream = new MemoryStream();
            writeTo.Invoke(structure, new object[] { stream });
            return stream.ToArray();
        }

        /// <summary>Writes a structure with the threshold lowered to <paramref name="threshold"/>.</summary>
        internal static byte[] WriteLarge(object structure, long threshold = 0) =>
            Below(threshold, () => Write(structure));

        internal static ushort VersionField(byte[] envelope) =>
            BinaryPrimitives.ReadUInt16LittleEndian(envelope.AsSpan(4));

        /// <summary>Whether the version field of an envelope carries the large form's flag.</summary>
        internal static bool IsLarge(byte[] envelope) =>
            (VersionField(envelope) & PersistenceFormat.LargeFormFlag) != 0;

        /// <summary>The format version an envelope names, without the flag.</summary>
        internal static ushort Layout(byte[] envelope) =>
            (ushort)(VersionField(envelope) & ~PersistenceFormat.LargeFormFlag);

        /// <summary>
        /// How many envelopes in the large form a payload holds inside its own: a magic
        /// past its first byte, with the flag set in the version after it.
        /// </summary>
        internal static int NestedLarge(byte[] envelope)
        {
            var found = 0;
            for (var i = 1; i + 6 <= envelope.Length; i++)
            {
                if (envelope.AsSpan(i, 4).SequenceEqual(PersistenceFormat.Magic)
                    && (BinaryPrimitives.ReadUInt16LittleEndian(envelope.AsSpan(i + 4)) & PersistenceFormat.LargeFormFlag) != 0)
                {
                    found++;
                }
            }
            return found;
        }

        /// <summary>
        /// A large-form envelope with both checksums worked out again as FORMAT.md lays
        /// them out: the header's over bytes 4 through 17, and the final one over those
        /// bytes and then the payload. An edit made before resealing is then the only
        /// thing wrong with it.
        /// </summary>
        internal static byte[] Reseal(byte[] envelope)
        {
            var bytes = (byte[])envelope.Clone();
            var crc = new Crc32();
            crc.Append(bytes.AsSpan(4, HeaderFieldsEnd - 4));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(HeaderFieldsEnd), crc.GetCurrentHashAsUInt32());

            var header = PersistenceFormat.LargeHeaderLength;
            crc.Append(bytes.AsSpan(header, bytes.Length - header - 4));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), crc.GetCurrentHashAsUInt32());
            return bytes;
        }

        /// <summary>
        /// Every structure's sample payload, read, and written again both ways: in the
        /// ordinary envelope, and with the threshold at <paramref name="threshold"/>.
        /// </summary>
        /// <remarks>
        /// Written from the structure read back rather than built again under the lowered
        /// threshold, because the samples' own builders look into the bytes they write at
        /// the ordinary envelope's offsets.
        /// </remarks>
        internal static (StructureId Id, Func<byte[], object> Read, byte[] Ordinary, byte[] Large)[] Samples(
            long threshold = 0) =>
            TestPersistenceAllStructures.SamplePayloads()
                .Select(sample =>
                {
                    var structure = sample.Read(sample.Bytes);
                    return (sample.Id, sample.Read, Write(structure), WriteLarge(structure, threshold));
                })
                .ToArray();
    }

    /// <summary>
    /// The large form of the envelope: written exactly when a payload is past the
    /// ordinary one's ceiling, read back as the structure that wrote it, and refused, with
    /// nothing returned, when any bit of it is wrong.
    /// </summary>
    /// <remarks>
    /// The ceiling is lowered for these tests through the threshold the writer reads
    /// (<see cref="LargeForm"/>). In the large form the header has a checksum of its own,
    /// checked before any field in it is used, and the payload's checksum follows the
    /// payload and is compared once the structure has read it (#223, #224, #227).
    /// </remarks>
    [TestClass]
    public class TestLargeEnvelope
    {
        // What each refusal says, by the check that made it. A fragment rather than the
        // sentence, as TESTING.md asks, because one exception type stands for all of them.
        private const string NotThisFormat = "not the expected marker";
        private const string HeaderChecksum = "header checksum does not match";
        private const string EndChecksum = "checksum at the end of the payload does not match";
        private const string NotReached = "checksum was not reached";
        private const string OrdinaryChecksum = "Payload checksum does not match";

        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        /// <summary>
        /// Unlowered, the threshold is the ordinary envelope's ceiling, so a payload that
        /// fits one is written in one; lowered, it is lowered for the thread that lowered
        /// it and no other, since the suite runs its tests side by side and every other
        /// test's payloads must stay as they are.
        /// </summary>
        [TestMethod]
        public void TestTheThresholdIsTheOrdinaryCeilingUnlessLoweredOnThisThread()
        {
            Assert.IsNull(PersistenceFormat.LoweredLargeFormThreshold);
            Assert.AreEqual((long)PersistenceFormat.MaxPayloadLength, PersistenceFormat.LargeFormThreshold);

            LargeForm.Below(5, () =>
            {
                Assert.AreEqual(5L, PersistenceFormat.LargeFormThreshold);

                long elsewhere = -1;
                var other = new Thread(() => elsewhere = PersistenceFormat.LargeFormThreshold);
                other.Start();
                other.Join();
                Assert.AreEqual((long)PersistenceFormat.MaxPayloadLength, elsewhere,
                    "a threshold lowered on one thread was seen on another");
                return 0;
            });

            Assert.AreEqual((long)PersistenceFormat.MaxPayloadLength, PersistenceFormat.LargeFormThreshold,
                "the threshold was not put back");
        }

        /// <summary>
        /// Every structure, written in the large form, reads back as the structure that
        /// wrote it, and writes the same large form again. The roster is
        /// <see cref="StructureId"/>, through the shared sample payloads.
        /// </summary>
        /// <remarks>
        /// With the threshold at nothing every payload is written in the large form, the
        /// envelopes nested in a scalable filter or a top-k included, so those are read
        /// through the nested envelope's own check as well; the test requires that one
        /// was. A structure that holds no other carries in the large form exactly the
        /// payload it carries in the ordinary envelope: only the envelope differs.
        /// </remarks>
        [TestMethod]
        public void TestEveryStructureRoundTripsInTheLargeForm()
        {
            var samples = LargeForm.Samples();
            var nested = 0;

            foreach (var (id, read, ordinary, large) in samples)
            {
                Assert.IsFalse(LargeForm.IsLarge(ordinary),
                    $"{id} was written in the large form with no threshold lowered");
                Assert.IsTrue(LargeForm.IsLarge(large),
                    $"{id} was not written in the large form with the threshold at nothing");
                Assert.AreEqual(LargeForm.Layout(ordinary), LargeForm.Layout(large),
                    $"{id} names one format version in the ordinary envelope and another in the large form");

                if (LargeForm.NestedLarge(large) > 0)
                {
                    nested++;
                }
                else
                {
                    CollectionAssert.AreEqual(
                        ordinary[14..^4], large[PersistenceFormat.LargeHeaderLength..^4],
                        $"{id}'s payload in the large form is not the payload of its ordinary envelope");
                }

                var restored = read(large);
                CollectionAssert.AreEqual(ordinary, LargeForm.Write(restored),
                    $"{id} read from the large form is not the structure that wrote it");
                CollectionAssert.AreEqual(large, LargeForm.WriteLarge(restored),
                    $"{id} read from the large form does not write the same large form again");
            }

            Assert.IsGreaterThan(0, nested,
                "no sample held an envelope in the large form inside its own, so no nested one was read");
            StructureRoster.AssertCoversEveryStructure("large-form round trip", samples.Select(s => s.Id));
        }

        /// <summary>
        /// The large form is written exactly when the payload is past the threshold: a
        /// payload of the threshold's own length is written in the ordinary envelope, byte
        /// for byte as with no threshold at all, and one a byte longer than it in the large
        /// form, which reads back.
        /// </summary>
        /// <remarks>
        /// Straddles the comparison, which is the rounding here: with the threshold one
        /// byte under a payload, a structure that holds others is written in the large
        /// form while the envelopes it holds, shorter than it, stay ordinary, so the
        /// read-back reads the two forms inside one another.
        /// </remarks>
        [TestMethod]
        public void TestTheLargeFormIsWrittenExactlyPastTheThreshold()
        {
            var swept = new List<StructureId>();
            var mixed = 0;

            foreach (var (id, read, bytes) in TestPersistenceAllStructures.SamplePayloads())
            {
                var structure = read(bytes);
                var ordinary = LargeForm.Write(structure);
                long length = ordinary.Length - PersistenceFormat.EnvelopeLength;

                CollectionAssert.AreEqual(ordinary, LargeForm.WriteLarge(structure, length),
                    $"{id}: a payload of exactly the threshold, {length} bytes, was not written in the ordinary envelope");

                var past = LargeForm.WriteLarge(structure, length - 1);
                Assert.IsTrue(LargeForm.IsLarge(past),
                    $"{id}: a payload a byte past the threshold was not written in the large form");
                CollectionAssert.AreEqual(ordinary, LargeForm.Write(read(past)),
                    $"{id} written a byte past the threshold does not read back as the structure");

                if (past.AsSpan(1).IndexOf(PersistenceFormat.Magic) >= 0)
                {
                    Assert.AreEqual(0, LargeForm.NestedLarge(past),
                        $"{id}'s nested envelopes are shorter than it and were written in the large form");
                    mixed++;
                }

                swept.Add(id);
            }

            Assert.IsGreaterThan(0, mixed,
                "no sample held an ordinary envelope inside a large one, so the two were never read together");
            StructureRoster.AssertCoversEveryStructure("large-form threshold", swept);
        }

        /// <summary>
        /// Every single-bit flip of a payload in the large form is refused, for every
        /// structure, with no structure returned, and by the check whose bytes it lies in.
        /// </summary>
        /// <remarks>
        /// TESTING.md's corruption at distance 1, over every bit rather than one a byte.
        /// Where a flip lies says what must refuse it:
        /// <list type="bullet">
        /// <item>the magic: not this format;</item>
        /// <item>the rest of the header, and its own checksum: the header checksum, before
        /// any field it covers is used, so a flipped structure id or length is never acted
        /// on;</item>
        /// <item>the flag itself: the header is then read as an ordinary one, whose
        /// checksum covers different bytes and fails;</item>
        /// <item>the payload: either the structure's reader, which refuses it naming its
        /// shape and saying the checksum was not reached, or the checksum at the end,
        /// once the reader has read it all;</item>
        /// <item>the final checksum: the checksum at the end.</item>
        /// </list>
        /// Each structure must have flips refused at the end and nowhere sooner, which are
        /// the bytes no check but the end one reads -- a fingerprint, a register -- and so
        /// the proof that the end check exists. The sweep as a whole must also have
        /// payload flips refused before the end, so a reader that checked the checksum
        /// first, as the ordinary envelope's does, fails here too.
        /// </remarks>
        [TestMethod]
        public void TestEveryBitFlipInTheLargeFormIsRefused()
        {
            var samples = LargeForm.Samples();
            var notReached = 0;

            foreach (var (id, read, _, clean) in samples)
            {
                Assert.IsTrue(LargeForm.IsLarge(clean), $"the {id} sample is not in the large form");
                var payloadEnd = clean.Length - 4;
                var atTheEnd = 0;

                for (var i = 0; i < clean.Length; i++)
                {
                    for (var bit = 0; bit < 8; bit++)
                    {
                        var corrupted = (byte[])clean.Clone();
                        corrupted[i] ^= (byte)(1 << bit);

                        var ex = Assert.ThrowsExactly<InvalidDataException>(() => read(corrupted),
                            $"{id}: the flip of bit {bit} at offset {i} was not refused");
                        var message = ex.Message;
                        var where = $"{id}: the flip of bit {bit} at offset {i}";

                        if (i < 4)
                        {
                            StringAssert.Contains(message, NotThisFormat, $"{where}, in the magic, was refused otherwise: {message}");
                        }
                        else if (i == 5 && bit == 7)
                        {
                            StringAssert.Contains(message, OrdinaryChecksum, $"{where}, the flag, was refused otherwise: {message}");
                        }
                        else if (i < PersistenceFormat.LargeHeaderLength)
                        {
                            StringAssert.Contains(message, HeaderChecksum, $"{where}, in the header, was refused otherwise: {message}");
                        }
                        else if (i < payloadEnd)
                        {
                            if (message.Contains(NotReached, StringComparison.Ordinal))
                            {
                                notReached++;
                            }
                            else
                            {
                                StringAssert.Contains(message, EndChecksum,
                                    $"{where}, in the payload, was refused neither at the end nor naming the checksum unreached: {message}");
                                atTheEnd++;
                            }
                        }
                        else
                        {
                            StringAssert.Contains(message, EndChecksum, $"{where}, in the final checksum, was refused otherwise: {message}");
                            Assert.DoesNotContain(NotReached, message, $"{where} was read as unreached: {message}");
                        }
                    }
                }

                Assert.IsGreaterThan(0, atTheEnd,
                    $"no flip in {id}'s payload was left to the checksum at the end, so the sweep never showed that check refusing one");
            }

            Assert.IsGreaterThan(0, notReached,
                "no flip in any payload was refused before the checksum at its end, so a reader that checks it first would pass");
            StructureRoster.AssertCoversEveryStructure("large-form corruption", samples.Select(s => s.Id));
        }

        /// <summary>
        /// A field of the large header is used only once the header's checksum holds: a
        /// structure id or length changed is refused for the checksum, and the same change
        /// with the checksums repaired is refused for what it says.
        /// </summary>
        [TestMethod]
        public void TestAHeaderFieldIsUsedOnlyOnceTheHeaderChecksumHolds()
        {
            var filter = new BloomFilter(200, 0.01);
            for (var i = 0; i < 40; i++) filter.Add(Key($"w{i}"));
            var bytes = LargeForm.WriteLarge(filter);

            var relabelled = (byte[])bytes.Clone();
            BinaryPrimitives.WriteUInt16LittleEndian(relabelled.AsSpan(6), (ushort)StructureId.CountMinSketch);
            var unsealed = Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<BloomFilter>(relabelled));
            StringAssert.Contains(unsealed.Message, HeaderChecksum);
            var sealedId = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<BloomFilter>(LargeForm.Reseal(relabelled)));
            StringAssert.Contains(sealedId.Message, "was read as");

            // A length past the stream that one array could hold: believed, it is priced, and the
            // budget refuses it.
            var longer = (byte[])bytes.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(longer.AsSpan(10), (ulong)Array.MaxLength);
            var unsealedLength = Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<BloomFilter>(longer));
            StringAssert.Contains(unsealedLength.Message, HeaderChecksum);
            Assert.IsNull(unsealedLength.InnerException, "a length was acted on before its header's checksum held");
            var sealedLength = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<BloomFilter>(LargeForm.Reseal(longer)));
            Assert.IsInstanceOfType<ReadBudgetExceededException>(sealedLength.InnerException,
                $"a length of the largest array, believed, was not refused for its cost: {sealedLength.Message}");
        }

        /// <summary>
        /// Every structure's payload in the large form with one byte more than its
        /// structure reads, its length and checksums repaired, is refused for the byte
        /// left over, before the checksum, which is not reached.
        /// </summary>
        /// <remarks>
        /// The envelope checks that the payload was read to its end, for every structure,
        /// after the structure's reader returns and before the checksum: a structure is
        /// returned only from a payload it read exactly. Most readers check it themselves
        /// too; one whose reader does not is held to it by the envelope alone, and this
        /// sweep is what shows the envelope does.
        /// </remarks>
        [TestMethod]
        public void TestAPayloadLongerThanItsStructureReadsIsRefusedInTheLargeForm()
        {
            var samples = LargeForm.Samples();
            var header = PersistenceFormat.LargeHeaderLength;

            foreach (var (id, read, _, clean) in samples)
            {
                var longer = new byte[clean.Length + 1];
                clean.AsSpan(0, clean.Length - 4).CopyTo(longer);
                BinaryPrimitives.WriteUInt64LittleEndian(longer.AsSpan(10), (ulong)(clean.Length - header - 4 + 1));

                var ex = Assert.ThrowsExactly<InvalidDataException>(() => read(LargeForm.Reseal(longer)),
                    $"{id} in the large form was read with a byte left over");
                StringAssert.Contains(ex.Message, "left over", $"{id} was refused for something else: {ex.Message}");
                StringAssert.Contains(ex.Message, NotReached, $"{id} was refused for a byte left over as if after its checksum");
            }

            StructureRoster.AssertCoversEveryStructure("large-form leftover", samples.Select(s => s.Id));
        }

        /// <summary>
        /// A length in the large header is refused for what it is before anything is priced
        /// from it, where nothing can read it: past what a stream holds, and past what one
        /// array holds, which is what a reader holding the payload reads it into. One the
        /// reader can hold goes on to be priced, and refused for its cost. Both checksums
        /// are repaired each time, so the length is the only thing wrong.
        /// </summary>
        /// <remarks>
        /// Straddles each limit by one, and none of them allocates: the largest array is
        /// refused under the default budget before the read makes room for it.
        /// </remarks>
        [TestMethod]
        public void TestALargeFormLengthNothingCanHoldIsRefusedForThat()
        {
            var filter = new BloomFilter(200, 0.01);
            for (var i = 0; i < 40; i++) filter.Add(Key($"w{i}"));
            var bytes = LargeForm.WriteLarge(filter);

            InvalidDataException Claiming(ulong length)
            {
                var claimed = (byte[])bytes.Clone();
                BinaryPrimitives.WriteUInt64LittleEndian(claimed.AsSpan(10), length);
                return Assert.ThrowsExactly<InvalidDataException>(
                    () => Persistence.FromByteArray<BloomFilter>(LargeForm.Reseal(claimed)),
                    $"a large-form payload claiming {length:N0} bytes was read");
            }

            var stream = Claiming((ulong)long.MaxValue + 1);
            StringAssert.Contains(stream.Message, "more than a stream can hold");
            Assert.IsNull(stream.InnerException, $"a length past a stream's was priced: {stream.Message}");

            foreach (var length in new[] { (ulong)long.MaxValue, (ulong)Array.MaxLength + 1 })
            {
                var array = Claiming(length);
                StringAssert.Contains(array.Message, "single array", $"{length:N0} bytes: {array.Message}");
                Assert.IsNull(array.InnerException, $"a length of {length:N0} bytes, past one array, was priced: {array.Message}");
            }

            var held = Claiming((ulong)Array.MaxLength);
            Assert.IsInstanceOfType<ReadBudgetExceededException>(held.InnerException,
                $"a length one array holds was not priced: {held.Message}");
        }

        /// <summary>
        /// A large-form payload written with a hash set through <c>SetHash</c>, read without
        /// one, is refused for that under any budget, as the ordinary envelope's is: the
        /// header names the hash, and once its own checksum has vouched for it, that is
        /// checked before anything is priced.
        /// </summary>
        [TestMethod]
        public void TestALargeFormPayloadWithAHashThatCannotBeResolvedIsRefusedForThatFirst()
        {
            var filter = new BloomFilter(200, 0.01);
            filter.SetHash(data => (ulong)data.Length * 0x9E3779B97F4A7C15UL);
            for (var i = 0; i < 40; i++) filter.Add(Key($"w{i}"));
            var bytes = LargeForm.WriteLarge(filter);
            Assert.IsTrue(LargeForm.IsLarge(bytes));

            foreach (var budget in new[] { 0L, ReadOptions.DefaultMemoryBudget })
            {
                var ex = Assert.ThrowsExactly<InvalidDataException>(
                    () => Persistence.FromByteArray<BloomFilter>(bytes, new ReadOptions(budget)));
                StringAssert.Contains(ex.Message, "SetHash", $"under {budget} bytes it was refused for something else: {ex.Message}");
                Assert.IsNull(ex.InnerException, $"under {budget} bytes the hash was priced before it was checked: {ex.Message}");
            }
        }

        /// <summary>
        /// A large-form payload refused for its budget is refused before its checksum, and
        /// says so, while the inner exception stays the
        /// <see cref="ReadBudgetExceededException"/> a caller tells a budget refusal by:
        /// under a budget too small for the payload alone, which is priced from the stream
        /// and never held, and under one a byte short of the whole read, where the payload
        /// is held and priced and the refusal comes as the price is settled.
        /// </summary>
        /// <remarks>
        /// What is priced comes from fields the checksum has not vouched for, so a refusal
        /// for its cost may be damage the checksum would have named.
        /// </remarks>
        [TestMethod]
        public void TestALargeFormRefusalForItsBudgetSaysTheChecksumWasNotReached()
        {
            var filter = new BloomFilter(20_000, 0.01);
            for (var i = 0; i < 40; i++) filter.Add(Key($"w{i}"));
            var bytes = LargeForm.WriteLarge(filter);
            var payload = bytes.Length - PersistenceFormat.LargeEnvelopeLength;

            var unheld = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<BloomFilter>(bytes, new ReadOptions(payload / 2)));
            var why = Assert.IsInstanceOfType<ReadBudgetExceededException>(unheld.InnerException,
                $"a refusal under half the payload is not a budget refusal: {unheld.Message}");
            StringAssert.Contains(unheld.Message, NotReached, "a payload priced from the stream did not say its checksum was not reached");

            var held = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<BloomFilter>(bytes, new ReadOptions(why.RequiredBytes - 1)));
            Assert.IsInstanceOfType<ReadBudgetExceededException>(held.InnerException,
                $"a refusal a byte short of the read is not a budget refusal: {held.Message}");
            StringAssert.Contains(held.Message, NotReached, "a payload held and priced did not say its checksum was not reached");
            Assert.IsGreaterThanOrEqualTo(PersistenceFormat.EnvelopeCost((ulong)payload), why.RequiredBytes - 1,
                "a byte short of the read is less than holding the payload costs, so the second path was not taken");
            Assert.IsLessThan(PersistenceFormat.EnvelopeCost((ulong)payload), (long)(payload / 2),
                "half the payload is enough to hold it, so the first path was not taken");

            Assert.IsNotNull(Persistence.FromByteArray<BloomFilter>(bytes, new ReadOptions(why.RequiredBytes)),
                "the large form does not read under the total its refusal named");
        }

        /// <summary>
        /// The flag sits outside every format version a reader reads, and above all of
        /// them, which is what makes a released reader refuse the large form as written by
        /// a later version.
        /// </summary>
        /// <remarks>
        /// The suite cannot run a released library. What it can hold is what those readers
        /// check first: every one from 3.2.0 to 6.2.0, as this one does, checks the magic,
        /// then refuses a version of 0 or above its own highest before it reads the
        /// structure id. Every version they read is in the table of readable versions,
        /// which names every version this format has defined, so a flagged version above
        /// the whole table is above each of theirs. And no version in the table has the
        /// flag's bit, so a version is never mistaken for the large form or the reverse.
        /// </remarks>
        [TestMethod]
        public void TestTheFlaggedVersionIsAboveEveryVersionInTheTable()
        {
            var table = PersistenceFormat.ReadableVersionTable;
            var highest = table.Values.SelectMany(versions => versions).Max();

            // The Decision on #223: the field's high bit, one bit and the highest of sixteen.
            var flag = (uint)PersistenceFormat.LargeFormFlag;
            Assert.AreEqual(1, System.Numerics.BitOperations.PopCount(flag), "the flag is not one bit");
            Assert.AreEqual(15, System.Numerics.BitOperations.Log2(flag), "the flag is not the version field's high bit");

            foreach (var (id, versions) in table)
            {
                foreach (var version in versions)
                {
                    Assert.AreEqual(0, version & PersistenceFormat.LargeFormFlag,
                        $"{id} reads version {version}, which has the large form's flag in it");

                    var flagged = (ushort)(version | PersistenceFormat.LargeFormFlag);
                    Assert.IsGreaterThan(highest, flagged,
                        $"{id}'s version {version} flagged is {flagged}, not above version {highest}, " +
                        "so a reader of that version would not refuse it as later");
                }
            }

            StructureRoster.AssertCoversEveryStructure("flagged versions", table.Keys);
        }

        /// <summary>
        /// The stored large-form payload reads as the filter its ordinary twin holds, and
        /// is what the writer writes for that filter.
        /// </summary>
        /// <remarks>
        /// The fixture was assembled outside the library: the payload of
        /// <c>bloomfilter-v8-narrow.bin</c>, wrapped by a script in the large form as
        /// FORMAT.md lays it out, its checksums worked out by Python's zlib. So the second
        /// half holds the writer to the layout and the checksum algorithm, not to itself.
        /// </remarks>
        [TestMethod]
        public void TestAStoredLargeFormPayloadReadsAndIsWhatTheWriterWrites()
        {
            var large = ReadFixture("bloomfilter-v8-large.bin");
            var ordinary = ReadFixture("bloomfilter-v8-narrow.bin");

            Assert.IsTrue(LargeForm.IsLarge(large), "the stored large-form payload is not in the large form");
            Assert.AreEqual(PersistenceFormat.PlacementVersion, LargeForm.Layout(large));

            var filter = Persistence.FromByteArray<BloomFilter>(large);
            CollectionAssert.AreEqual(ordinary, filter.ToByteArray(),
                "the stored large-form payload does not read as the filter its ordinary twin holds");

            CollectionAssert.AreEqual(large, LargeForm.WriteLarge(Persistence.FromByteArray<BloomFilter>(ordinary)),
                "the writer's large form is not the one assembled from FORMAT.md outside the library");
        }

        internal static byte[] ReadFixture(string name)
        {
            using var resource = typeof(TestLargeEnvelope).Assembly
                .GetManifestResourceStream($"TestProbabilisticDataStructures.fixtures.{name}")
                ?? throw new InvalidOperationException($"fixture {name} is not embedded");

            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            return buffer.ToArray();
        }
    }
}
