using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
        /// How many bytes FORMAT.md gives a run's length in the ordinary envelope and in the
        /// large form, read from its table of payload primitives alone: the <c>bytes</c> row
        /// names the primitive each form's length is, and that primitive's row its size.
        /// </summary>
        internal static (int Ordinary, int Large) DocumentedRunPrefix()
        {
            var spec = TestFormatDocumentation.Specification();
            var bytes = Regex.Match(spec, @"^\| `bytes` \| (?<text>[^|]+?) \|\r?$", RegexOptions.Multiline);
            Assert.IsTrue(bytes.Success, "FORMAT.md's table of primitives has no row for `bytes`");
            var forms = Regex.Match(bytes.Groups["text"].Value,
                @"`(?<ordinary>u\d+)` in the ordinary envelope and a `(?<large>u\d+)` in \[the large form\]");
            Assert.IsTrue(forms.Success,
                $"FORMAT.md's `bytes` row does not say what a run's length is in each form: '{bytes.Groups["text"].Value}'");

            int Size(string primitive)
            {
                var row = Regex.Match(spec, $@"^\| `{primitive}` \| (?<n>\d+) bytes?\b", RegexOptions.Multiline);
                Assert.IsTrue(row.Success, $"FORMAT.md's table of primitives has no size for `{primitive}`");
                return int.Parse(row.Groups["n"].Value);
            }

            return (Size(forms.Groups["ordinary"].Value), Size(forms.Groups["large"].Value));
        }

        /// <summary>
        /// The large form's envelope around <paramref name="payload"/>, as FORMAT.md lays it
        /// out, for the structure whose ordinary envelope is <paramref name="ordinary"/>: the
        /// same magic, structure and hash, the version flagged, a <c>u64</c> length, and each
        /// checksum taken in one call over the bytes it covers.
        /// </summary>
        internal static byte[] Enveloped(byte[] ordinary, byte[] payload)
        {
            var bytes = new byte[22 + payload.Length + 4];
            ordinary.AsSpan(0, 10).CopyTo(bytes);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(ordinary.AsSpan(4)) | 0x8000));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(10), (ulong)payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(18), Crc32.HashToUInt32(bytes.AsSpan(4, 14)));
            payload.CopyTo(bytes, 22);

            var covered = new byte[14 + payload.Length];
            bytes.AsSpan(4, 14).CopyTo(covered);
            payload.CopyTo(covered, 14);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(22 + payload.Length), Crc32.HashToUInt32(covered));
            return bytes;
        }

        /// <summary>The structure an envelope names.</summary>
        internal static StructureId StructureOf(byte[] envelope) =>
            (StructureId)BinaryPrimitives.ReadUInt16LittleEndian(envelope.AsSpan(6));

        /// <summary>A reader for every structure, from the shared sample payloads.</summary>
        internal static Dictionary<StructureId, Func<byte[], object>> Readers() =>
            TestPersistenceAllStructures.SamplePayloads()
                .GroupBy(s => s.Id)
                .ToDictionary(g => g.Key, g => g.First().Read);

        // The run prefixes of each ordinary envelope probed so far, by its bytes: probing
        // reads a payload once per offset, and several sweeps ask for the same samples.
        private static readonly ConcurrentDictionary<string, int[]> Probed = new();

        /// <summary>
        /// Where in an ordinary envelope the length prefixes of its payload's runs are, as
        /// <see cref="TestPayloadReader"/> finds them, by probing rather than from a layout.
        /// </summary>
        internal static int[] RunPrefixes(byte[] ordinary, Func<byte[], object> read) =>
            Probed.GetOrAdd(Convert.ToHexString(SHA256.HashData(ordinary)),
                _ => TestPayloadReader.RunPrefixes(ordinary, read).OrderBy(at => at).ToArray());

        /// <summary>
        /// The payload the large form carries for the structure whose ordinary envelope is
        /// <paramref name="ordinary"/>, worked out from that envelope and FORMAT.md alone: the
        /// ordinary payload with each run's length widened from the ordinary envelope's
        /// primitive to the large form's, and nothing else changed. With
        /// <paramref name="nestedLarge"/>, an envelope held in a run is rewritten in the large
        /// form the same way, its own runs widened, as it is written when its own payload is
        /// past the threshold too; without, it is kept as the ordinary envelope it is.
        /// </summary>
        /// <param name="runs">How many runs this payload holds, nested ones not counted.</param>
        /// <param name="nested">How many nested envelopes were rewritten, at any depth.</param>
        internal static byte[] Widened(
            byte[] ordinary, IReadOnlyDictionary<StructureId, Func<byte[], object>> readers, bool nestedLarge,
            out int runs, out int nested)
        {
            var (narrow, wide) = DocumentedRunPrefix();
            Assert.AreEqual(4, narrow, "FORMAT.md's ordinary run length is not the u32 the ordinary payloads carry");

            var prefixes = RunPrefixes(ordinary, readers[StructureOf(ordinary)]);
            var payload = new MemoryStream();
            var at = 14;
            nested = 0;

            foreach (var prefix in prefixes)
            {
                Assert.IsGreaterThanOrEqualTo(at, prefix, "a run was found inside the run before it");
                payload.Write(ordinary, at, prefix - at);

                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(ordinary.AsSpan(prefix));
                var run = ordinary.AsSpan(prefix + narrow, length).ToArray();
                if (nestedLarge && IsEnvelope(run))
                {
                    run = Enveloped(run, Widened(run, readers, nestedLarge: true, out _, out var deeper));
                    nested += 1 + deeper;
                }

                var widened = new byte[wide];
                BinaryPrimitives.WriteUInt64LittleEndian(widened, (ulong)run.Length);
                payload.Write(widened);
                payload.Write(run);
                at = prefix + narrow + length;
            }

            payload.Write(ordinary, at, ordinary.Length - 4 - at);
            runs = prefixes.Length;
            return payload.ToArray();
        }

        /// <summary>
        /// Whether a run holds one whole ordinary envelope: the magic, and a length that
        /// accounts for the rest of the run.
        /// </summary>
        internal static bool IsEnvelope(ReadOnlySpan<byte> run) =>
            run.Length >= 18
            && run[..4].SequenceEqual(PersistenceFormat.Magic)
            && (BinaryPrimitives.ReadUInt16LittleEndian(run[4..]) & PersistenceFormat.LargeFormFlag) == 0
            && BinaryPrimitives.ReadUInt32LittleEndian(run[10..]) + 18L == run.Length;

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
        internal const string PastTheStream = "after the large form's header";

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
        /// was. What the large form carries, byte for byte, is
        /// <see cref="TestEveryRunInTheLargeFormCarriesAU64Length"/>'s to check.
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
        /// Every structure's payload in the large form is its ordinary payload with each
        /// run's length a <c>u64</c>, and nothing else changed: worked out outside the writer,
        /// from the ordinary envelope and FORMAT.md's table of primitives alone, and compared
        /// byte for byte. With the threshold at nothing the envelopes nested in a run are in
        /// the large form too, and are widened the same way inside. The roster is
        /// <see cref="StructureId"/>, through the shared sample payloads.
        /// </summary>
        /// <remarks>
        /// The Decision on #228: in the large form every length-prefixed run, nested
        /// envelopes included, has an eight-byte length in place of the four-byte one, so a
        /// payload in it differs from the ordinary one by four bytes a run. The runs are found
        /// as <see cref="TestPayloadReader"/> finds them, by probing the ordinary payload, so
        /// no layout is kept here beside FORMAT.md, and the width is the one its table gives.
        /// The sweep must have widened runs and rewritten nested envelopes, or it shows
        /// nothing about either.
        /// </remarks>
        [TestMethod]
        public void TestEveryRunInTheLargeFormCarriesAU64Length()
        {
            var readers = LargeForm.Readers();
            var samples = LargeForm.Samples();
            var runs = 0;
            var nested = 0;

            foreach (var (id, _, ordinary, large) in samples)
            {
                var payload = LargeForm.Widened(ordinary, readers, nestedLarge: true, out var held, out var rewritten);
                CollectionAssert.AreEqual(LargeForm.Enveloped(ordinary, payload), large,
                    $"{id} in the large form, {large.Length:N0} bytes, is not its ordinary payload of " +
                    $"{ordinary.Length - 18:N0} bytes with the length of each of its {held} runs a u64");
                runs += held;
                nested += rewritten;
            }

            Assert.IsGreaterThan(0, runs, "no sample held a run, so no length was shown widened");
            Assert.IsGreaterThan(0, nested, "no sample held a nested envelope, so none was shown widened inside");
            StructureRoster.AssertCoversEveryStructure("u64 runs in the large form", samples.Select(s => s.Id));
        }

        /// <summary>
        /// The count declares the large form at its length with <c>u64</c> runs, exactly, and
        /// chooses the form by its length with <c>u32</c> ones: a payload a byte past the
        /// threshold by its <c>u32</c> count is declared, and written, at its <c>u64</c> count;
        /// one whose <c>u32</c> count is within the threshold is written in the ordinary
        /// envelope, byte for byte as with no threshold, even where its <c>u64</c> count is not.
        /// </summary>
        /// <remarks>
        /// Straddled at the ordinary payload's length <c>n</c>, with <c>r</c> runs: at
        /// <c>n − 1</c> the large form, declaring <c>n + 4r</c>; at <c>n</c>, <c>n + 4r − 1</c>
        /// and <c>n + 4r</c> the ordinary envelope. Between the first two of those the
        /// <c>u32</c> count fits and the <c>u64</c> count does not, so a count that chose the
        /// form by its <c>u64</c> length fails there, and one that declared its <c>u32</c>
        /// length fails at <c>n − 1</c>. A nested envelope is shorter than the structure
        /// holding it, so it stays ordinary at each of these. The second count is never the
        /// shorter, so the form it was chosen by holds.
        /// </remarks>
        [TestMethod]
        public void TestTheLargeFormDeclaresItsU64CountExactlyEitherSideOfTheThreshold()
        {
            var readers = LargeForm.Readers();
            var swept = new List<StructureId>();
            var straddled = 0;

            foreach (var (id, read, bytes) in TestPersistenceAllStructures.SamplePayloads())
            {
                var structure = read(bytes);
                var ordinary = LargeForm.Write(structure);
                long n = ordinary.Length - PersistenceFormat.EnvelopeLength;
                var widened = LargeForm.Widened(ordinary, readers, nestedLarge: false, out var runs, out _);
                long wide = n + 4L * runs;
                Assert.AreEqual(wide, widened.LongLength, $"{id}'s {runs} runs did not widen its payload by four bytes each");

                var past = LargeForm.WriteLarge(structure, n - 1);
                Assert.IsTrue(LargeForm.IsLarge(past), $"{id} a byte past the threshold by its u32 count was not written in the large form");
                Assert.AreEqual((ulong)wide, BinaryPrimitives.ReadUInt64LittleEndian(past.AsSpan(10)),
                    $"{id} a byte past the threshold declared other than its count with u64 runs, {wide:N0}; its count with u32 runs is {n:N0}");
                Assert.AreEqual(PersistenceFormat.LargeEnvelopeLength + wide, past.LongLength,
                    $"{id} in the large form is not the length its header declares");

                foreach (var threshold in new[] { n, wide - 1, wide }.Where(t => t >= n).Distinct())
                {
                    CollectionAssert.AreEqual(ordinary, LargeForm.WriteLarge(structure, threshold),
                        $"{id} under a threshold of {threshold:N0}, its count with u32 runs {n:N0} within it and with u64 runs " +
                        $"{wide:N0}, was not written in the ordinary envelope");
                }

                if (runs > 0)
                {
                    straddled++;
                }

                swept.Add(id);
            }

            Assert.IsGreaterThan(0, straddled, "no sample held a run, so its u32 and u64 counts never differed");
            StructureRoster.AssertCoversEveryStructure("the large form's count", swept);
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

            // A length past what the stream holds: believed, it is refused for the bytes that
            // are not there, before anything is priced from it. A streamed read holds no
            // payload, so a length costs nothing, and the stream is what it is held to.
            var longer = (byte[])bytes.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(longer.AsSpan(10), (ulong)Array.MaxLength);
            var unsealedLength = Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<BloomFilter>(longer));
            StringAssert.Contains(unsealedLength.Message, HeaderChecksum);
            Assert.IsNull(unsealedLength.InnerException, "a length was acted on before its header's checksum held");
            var sealedLength = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<BloomFilter>(LargeForm.Reseal(longer)));
            StringAssert.Contains(sealedLength.Message, PastTheStream,
                $"a length past the stream, believed, was not refused for that: {sealedLength.Message}");
            Assert.IsNull(sealedLength.InnerException, $"a length past the stream was priced: {sealedLength.Message}");
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
        /// Every structure's payload in the ordinary envelope with one byte more than its
        /// structure reads, its length and checksum repaired, is refused for the byte left
        /// over, as it is in the large form.
        /// </summary>
        /// <remarks>
        /// The ordinary envelope has no check of its own that the payload was read to its end,
        /// so each structure's reader makes it. <c>PrivateCountMinSketch</c>'s did not, and read
        /// such a payload as if the byte were not there, while the same payload in the large
        /// form was refused by the envelope's check.
        /// </remarks>
        [TestMethod]
        public void TestAPayloadLongerThanItsStructureReadsIsRefusedInTheOrdinaryEnvelope()
        {
            var samples = LargeForm.Samples();

            foreach (var (id, read, ordinary, _) in samples)
            {
                var longer = new byte[ordinary.Length + 1];
                ordinary.AsSpan(0, ordinary.Length - 4).CopyTo(longer);
                BinaryPrimitives.WriteUInt32LittleEndian(longer.AsSpan(10),
                    (uint)(ordinary.Length - PersistenceFormat.EnvelopeLength + 1));

                var ex = Assert.ThrowsExactly<InvalidDataException>(() => read(ResealOrdinary(longer)),
                    $"{id} in the ordinary envelope was read with a byte left over");
                StringAssert.Contains(ex.Message, "left over", $"{id} was refused for something else: {ex.Message}");
            }

            StructureRoster.AssertCoversEveryStructure("ordinary leftover", samples.Select(s => s.Id));
        }

        /// <summary>
        /// An ordinary envelope with its checksum worked out again as FORMAT.md lays it out,
        /// over bytes 4 through the payload's end, so that an edit made before is the only
        /// thing wrong with it.
        /// </summary>
        private static byte[] ResealOrdinary(byte[] envelope)
        {
            var bytes = (byte[])envelope.Clone();
            var crc = new Crc32();
            crc.Append(bytes.AsSpan(4, bytes.Length - 8));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), crc.GetCurrentHashAsUInt32());
            return bytes;
        }

        /// <summary>
        /// A length in the large header is refused before anything is priced from it when
        /// nothing can hold it, past what any stream holds, or when this stream does not,
        /// under any budget. One array is no longer a limit: the payload is streamed, not
        /// held (#226). Both checksums are repaired each time, so the length is the only
        /// thing wrong.
        /// </summary>
        /// <remarks>
        /// Straddles each limit by one: the largest a stream holds and a byte more, and the
        /// payload's own length and a byte more, which is the first that leaves the stream
        /// short of its checksum. Between them lie the largest array and a byte past it,
        /// where a reader that held the payload refused or priced the length, and neither
        /// is reached now. A length past one array that the stream does hold is read on;
        /// <see cref="TestLargeEnvelopeReading"/> shows that, on a stream that claims one.
        /// </remarks>
        [TestMethod]
        public void TestALargeFormLengthIsHeldToTheStreamBeforeItIsPriced()
        {
            var filter = new BloomFilter(200, 0.01);
            for (var i = 0; i < 40; i++) filter.Add(Key($"w{i}"));
            var bytes = LargeForm.WriteLarge(filter);
            var payload = (ulong)(bytes.Length - PersistenceFormat.LargeEnvelopeLength);

            byte[] Claiming(ulong length)
            {
                var claimed = (byte[])bytes.Clone();
                BinaryPrimitives.WriteUInt64LittleEndian(claimed.AsSpan(10), length);
                return LargeForm.Reseal(claimed);
            }

            InvalidDataException Refused(ulong length, ReadOptions options) => Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<BloomFilter>(Claiming(length), options),
                $"a large-form payload claiming {length:N0} bytes was read");

            var stream = Refused((ulong)long.MaxValue + 1, ReadOptions.Default);
            StringAssert.Contains(stream.Message, "more than a stream can hold");
            Assert.IsNull(stream.InnerException, $"a length past a stream's was priced: {stream.Message}");

            foreach (var length in new[] { (ulong)long.MaxValue, (ulong)Array.MaxLength + 1, (ulong)Array.MaxLength, payload + 1 })
            {
                // Under nothing, too: a length that had been priced would be refused for its
                // budget there, naming a total that no budget could load.
                foreach (var options in new[] { ReadOptions.Default, new ReadOptions(0) })
                {
                    var past = Refused(length, options);
                    StringAssert.Contains(past.Message, PastTheStream,
                        $"{length:N0} bytes under {options.MemoryBudget:N0}: {past.Message}");
                    Assert.IsNull(past.InnerException,
                        $"a length of {length:N0} bytes, past the stream, was priced under {options.MemoryBudget:N0}: {past.Message}");
                }
            }

            Assert.IsNotNull(Persistence.FromByteArray<BloomFilter>(Claiming(payload)),
                "the payload's own length, which the stream holds with its checksum, was not read");
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
        /// <see cref="ReadBudgetExceededException"/> a caller tells a budget refusal by,
        /// under every budget short of the read: nothing, half the payload, and a byte
        /// short of the whole. Each names the same total, which loads, and none holds the
        /// payload to price it.
        /// </summary>
        /// <remarks>
        /// What is priced comes from fields the checksum has not vouched for, so a refusal
        /// for its cost may be damage the checksum would have named. The large form is
        /// priced from its stream in a first pass, which seeks over the runs (#226), so a
        /// read refused a byte short of the whole, where a reader holding the payload had
        /// it in memory by then, has allocated less than half of it.
        /// </remarks>
        [TestMethod]
        public void TestALargeFormRefusalForItsBudgetSaysTheChecksumWasNotReached()
        {
            var filter = new BloomFilter(20_000, 0.01);
            for (var i = 0; i < 40; i++) filter.Add(Key($"w{i}"));
            var bytes = LargeForm.WriteLarge(filter);
            var payload = bytes.Length - PersistenceFormat.LargeEnvelopeLength;

            var first = Assert.ThrowsExactly<InvalidDataException>(
                () => Persistence.FromByteArray<BloomFilter>(bytes, new ReadOptions(0)));
            var total = Assert.IsInstanceOfType<ReadBudgetExceededException>(first.InnerException,
                $"a refusal under nothing is not a budget refusal: {first.Message}").RequiredBytes;
            Assert.IsGreaterThan((long)payload, total, "the read is priced at less than the filter's bits, so it is not priced");

            foreach (var budget in new[] { 0L, payload / 2, total - 1 })
            {
                InvalidDataException refused = null;
                var allocated = TestReadBudget.Allocated(() => refused = Assert.ThrowsExactly<InvalidDataException>(
                    () => Persistence.FromByteArray<BloomFilter>(bytes, new ReadOptions(budget))));

                var why = Assert.IsInstanceOfType<ReadBudgetExceededException>(refused.InnerException,
                    $"a refusal under {budget:N0} bytes is not a budget refusal: {refused.Message}");
                Assert.AreEqual(total, why.RequiredBytes, $"refused under {budget:N0} bytes, the read named another total");
                StringAssert.Contains(refused.Message, NotReached, $"refused under {budget:N0} bytes, it did not say its checksum was not reached");
                Assert.IsLessThan(payload / 2L, allocated,
                    $"refused under {budget:N0} bytes, the read allocated {allocated:N0}, as if it held its {payload:N0}-byte payload");
            }

            Assert.IsNotNull(Persistence.FromByteArray<BloomFilter>(bytes, new ReadOptions(total)),
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
