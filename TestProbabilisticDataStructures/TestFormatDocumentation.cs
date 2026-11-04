using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Holds FORMAT.md to the format it describes.
    /// </summary>
    /// <remarks>
    /// The specification is the one artefact here written for someone who does not have
    /// the library: it exists so a payload can be decoded from the bytes alone. That
    /// makes a gap in it worse than a gap in a test, and harder to notice, because
    /// nothing stops compiling and no assertion goes red.
    /// <para>
    /// It had drifted. SublimeCountMinSketch and TupleSketch shipped in 6.2.0 with no
    /// section at all, and SetSketch had a section about its variant byte without ever
    /// saying what the rest of its payload holds -- so a reader learnt that the sketch
    /// records which construction built it, and nothing about the registers.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestFormatDocumentation
    {
        // The trailing \r is matched rather than assumed away: the file is checked out
        // with CRLF line endings on Windows, and a pattern anchored with $ alone finds
        // nothing there. That failure is not loud by itself -- a sweep over no sections
        // passes every per-section assertion it makes -- so PayloadSections guards it.
        private static readonly Regex PayloadSection =
            new(@"^### `(?<name>[A-Za-z0-9]+)` \(id (?<id>\d+)\)\r?$", RegexOptions.Multiline);

        internal static string Specification()
        {
            using var resource = typeof(TestFormatDocumentation).Assembly
                .GetManifestResourceStream("TestProbabilisticDataStructures.FORMAT.md");
            Assert.IsNotNull(resource, "FORMAT.md is not embedded in the test assembly");
            using var reader = new StreamReader(resource);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// The payload sections, with the vacuity guard both tests below need: a
        /// pattern matching nothing turns either of them into a loop over an empty
        /// collection, which passes.
        /// </summary>
        private static MatchCollection PayloadSections()
        {
            var sections = PayloadSection.Matches(Specification());

            Assert.IsGreaterThan(0, sections.Count,
                "no payload sections were found in FORMAT.md at all, so every " +
                "assertion made about them below is vacuous. The heading pattern and " +
                "the document have diverged.");

            return sections;
        }

        // One row of the table of format versions: the version, the release that first
        // wrote it, and the structures that define it. The release is not read.
        private static readonly Regex VersionRow =
            new(@"^\| (?<version>\d+) \| [^|]* \| (?<who>[^|]*) \|", RegexOptions.Multiline);

        // One row of the table of structure ids: the id and the structure's name. The
        // header and the rule under it have no digits and no backticks, so they match
        // nothing. The trailing \r is matched for the reason PayloadSection's is.
        private static readonly Regex IdRow =
            new(@"^\| (?<id>\d+) \| `(?<name>[A-Za-z0-9]+)` \|\r?$", RegexOptions.Multiline);

        /// <summary>
        /// The table of format versions says, structure by structure, the same versions
        /// the readers accept. Someone decoding a payload by hand reads that table to
        /// learn which versions exist for the structure in front of them; a table that
        /// has drifted from the code sends them to a layout that was never written, or
        /// tells them a version is unknown that the library reads.
        /// </summary>
        [TestMethod]
        public void TestTheVersionTableIsWhatEachStructureReads()
        {
            var spec = Specification();
            var start = spec.IndexOf("### Format versions", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, start, "FORMAT.md has no table of format versions");
            var end = spec.IndexOf("\n### ", start + 1, StringComparison.Ordinal);
            var section = spec[start..(end < 0 ? spec.Length : end)];

            var rows = VersionRow.Matches(section);
            Assert.IsGreaterThan(0, rows.Count,
                "no rows were found in the table of format versions, so every assertion " +
                "made about it below is vacuous. The row pattern and the document have diverged.");

            var documented = Enum.GetValues<StructureId>()
                .ToDictionary(id => id, _ => new List<ushort>());
            var seenVersions = new HashSet<ushort>();

            // The version field's high bit is the large form's flag, not part of a version,
            // and the section says so where someone reading the table will see it.
            StringAssert.Contains(section, $"`0x{PersistenceFormat.LargeFormFlag:X4}`",
                "FORMAT.md's table of format versions does not say which bit of the field is the large form's flag");

            foreach (Match row in rows)
            {
                var version = ushort.Parse(row.Groups["version"].Value);
                Assert.IsTrue(seenVersions.Add(version),
                    $"FORMAT.md lists format version {version} on more than one row");
                Assert.IsLessThan(PersistenceFormat.LargeFormFlag, version,
                    $"FORMAT.md lists format version {version}, which has the large form's flag in it");

                var who = row.Groups["who"].Value.Trim();
                var named = who == "every structure"
                    ? Enum.GetValues<StructureId>()
                    : who.Split(',')
                        .Select(name => name.Trim().Trim('`'))
                        .Select(name =>
                        {
                            Assert.IsTrue(Enum.TryParse<StructureId>(name, out var id)
                                    && Enum.IsDefined(id),
                                $"the row for format version {version} names '{name}', " +
                                "which is not a structure");
                            return Enum.Parse<StructureId>(name);
                        })
                        .ToArray();

                foreach (var id in named)
                {
                    documented[id].Add(version);
                }
            }

            foreach (var (id, versions) in documented)
            {
                CollectionAssert.AreEqual(
                    PersistenceFormat.ReadableVersions(id).ToArray(),
                    versions.OrderBy(v => v).ToArray(),
                    $"FORMAT.md and the reader of {id} disagree about which format " +
                    "versions it has");
            }
        }

        // One row of an envelope's table: offset, size and field, where an offset or a
        // size may be in terms of the payload's length, *n*.
        private static readonly Regex EnvelopeRow =
            new(@"^\| (?<offset>\d[^|]*?) \| (?<size>[^|]+?) \| (?<field>[^|]+?) \|\r?$", RegexOptions.Multiline);

        // "bytes 4 through 17", or "bytes 22 through 21 + *n*".
        private static readonly Regex ByteRange =
            new(@"bytes (?<from>\d+) through (?<to>\d+)(?<plus> \+ \*n\*)?");

        /// <summary>
        /// The types of the fields FORMAT.md lays out in the first code block after
        /// <paramref name="heading"/>, in order, the first word of each line, with a
        /// <c>Buckets</c> expanded into the fields its own block gives it.
        /// </summary>
        private static List<string> DocumentedFields(string spec, string heading)
        {
            List<string> Block(string after)
            {
                var at = spec.IndexOf(after, StringComparison.Ordinal);
                Assert.IsGreaterThanOrEqualTo(0, at, $"FORMAT.md has no '{after}'");
                var open = spec.IndexOf("```", at, StringComparison.Ordinal);
                var close = spec.IndexOf("```", open + 3, StringComparison.Ordinal);
                Assert.IsTrue(open >= 0 && close > open, $"FORMAT.md has no layout after '{after}'");
                return spec[(open + 3)..close]
                    .Split('\n')
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0)
                    .Select(line => line.Split(' ', 2)[0])
                    .ToList();
            }

            var fields = new List<string>();
            foreach (var type in Block(heading))
            {
                fields.AddRange(type == "Buckets" ? Block("`Buckets`, the packed bit array") : new[] { type });
            }

            Assert.IsNotEmpty(fields, $"FORMAT.md lays out no fields after '{heading}'");
            return fields;
        }

        /// <summary>How many bytes FORMAT.md's table of primitives gives <paramref name="primitive"/>.</summary>
        private static int PrimitiveSize(string spec, string primitive)
        {
            var row = Regex.Match(spec, $@"^\| `{Regex.Escape(primitive)}` \| (?<n>\d+) bytes?\b", RegexOptions.Multiline);
            Assert.IsTrue(row.Success, $"FORMAT.md's table of primitives gives no size for `{primitive}`");
            return int.Parse(row.Groups["n"].Value);
        }

        /// <summary>An offset or a size from an envelope's table, at a payload of <paramref name="n"/> bytes.</summary>
        private static long Evaluate(string term, long n)
        {
            var match = Regex.Match(term.Trim(), @"^(?:(?<k>\d+)(?<plus> \+ \*n\*)?|(?<only>\*n\*))$");
            Assert.IsTrue(match.Success, $"'{term}' is not an offset or size this test can read");
            return match.Groups["only"].Success
                ? n
                : long.Parse(match.Groups["k"].Value) + (match.Groups["plus"].Success ? n : 0);
        }

        /// <summary>
        /// The large form is laid out as FORMAT.md's table of it says: a payload the library
        /// writes in the large form, decoded field by field at the offsets and sizes the
        /// table gives, holds the magic, the flagged version, the structure and hash ids
        /// and its length there, and each checksum is the CRC-32 of exactly the bytes the
        /// table says it covers.
        /// </summary>
        /// <remarks>
        /// The large form is what a reader without this library meets past 2 GiB, and it
        /// differs from the ordinary envelope in the length's width, the header's own
        /// checksum, and what the final checksum covers -- each a place a document can be
        /// wrong while every round trip passes. The payload is written small, under a
        /// lowered threshold, and decoded with nothing but the table.
        /// </remarks>
        [TestMethod]
        public void TestTheLargeFormIsLaidOutAsDocumented()
        {
            var spec = Specification();
            var start = spec.IndexOf("### The large form", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, start, "FORMAT.md has no section on the large form");
            var end = spec.IndexOf("\n#", start + 1, StringComparison.Ordinal);
            var section = spec[start..(end < 0 ? spec.Length : end)];

            var rows = EnvelopeRow.Matches(section).Cast<Match>()
                .Select(m => (Offset: m.Groups["offset"].Value, Size: m.Groups["size"].Value, Field: m.Groups["field"].Value))
                .ToArray();
            Assert.IsGreaterThan(0, rows.Length,
                "no rows were found in the large form's table, so every assertion made about it below is vacuous");

            var filter = new BloomFilter(200, 0.01);
            for (var i = 0; i < 40; i++) filter.Add(System.Text.Encoding.ASCII.GetBytes($"w{i}"));
            var bytes = LargeForm.WriteLarge(filter);
            long n = bytes.Length - PersistenceFormat.LargeEnvelopeLength;
            Assert.IsGreaterThan(0L, n);

            // The rows run one after another from the start to the end of the envelope.
            long at = 0;
            foreach (var (offset, size, name) in rows)
            {
                Assert.AreEqual(at, Evaluate(offset, n), $"the large form's row '{name}' does not start where the one before it ends");
                at += Evaluate(size, n);
            }
            Assert.AreEqual(bytes.LongLength, at, "the large form's rows do not add up to the envelope the library writes");

            (long Offset, long Size, string Field) Row(Func<string, bool> which, string what)
            {
                var found = rows.Where(r => which(r.Field)).ToArray();
                Assert.HasCount(1, found, $"the large form's table has no single row for {what}");
                return (Evaluate(found[0].Offset, n), Evaluate(found[0].Size, n), found[0].Field);
            }

            var magic = Row(f => f.StartsWith("Magic", StringComparison.Ordinal), "the magic");
            Assert.IsTrue(bytes.AsSpan((int)magic.Offset, (int)magic.Size).SequenceEqual(PersistenceFormat.Magic));

            var version = Row(f => f.StartsWith("Format version", StringComparison.Ordinal), "the version");
            var flagText = Regex.Match(version.Field, @"`0x(?<flag>[0-9A-Fa-f]{4})`");
            Assert.IsTrue(flagText.Success, "the large form's version row does not name the flag");
            var flag = Convert.ToUInt16(flagText.Groups["flag"].Value, 16);
            Assert.AreEqual(PersistenceFormat.LargeFormFlag, flag, "FORMAT.md names another flag than the library sets");
            Assert.AreEqual(2L, version.Size);
            var field = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)version.Offset));
            Assert.AreEqual(flag, (ushort)(field & flag), "the documented flag is not set in the version the library writes");
            Assert.AreEqual(PersistenceFormat.PlacementVersion, (ushort)(field & ~flag),
                "the version without the documented flag is not the filter's format version");

            var structure = Row(f => f.StartsWith("Structure id", StringComparison.Ordinal), "the structure id");
            Assert.AreEqual((ushort)StructureId.BloomFilter, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)structure.Offset)));

            var hash = Row(f => f.StartsWith("Hash id", StringComparison.Ordinal), "the hash id");
            Assert.AreEqual((ushort)HashId.XxHash3_64, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)hash.Offset)));

            var length = Row(f => f.StartsWith("Payload length", StringComparison.Ordinal), "the payload length");
            Assert.AreEqual(8L, length.Size, "the large form's length is a u64");
            Assert.AreEqual((ulong)n, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan((int)length.Offset)));

            var payload = Row(f => f.Trim() == "Payload", "the payload");
            Assert.AreEqual((long)PersistenceFormat.LargeHeaderLength, payload.Offset,
                "the large form's payload does not start where the library's large header ends");
            Assert.AreEqual(n, payload.Size);

            // The payload itself, decoded by the filter's section and the tables of primitives
            // alone: each field as wide as its primitive's row says, a `Buckets` as its own
            // block lays it out, and a run's length as wide as the `bytes` row gives the large
            // form's (#228). It has to end where the envelope says the payload does, and its one
            // run has to be the filter's bits.
            var (_, runPrefix) = LargeForm.DocumentedRunPrefix();
            long cursor = payload.Offset;
            var runs = new List<long>();
            foreach (var type in DocumentedFields(spec, "### `BloomFilter` (id 1)"))
            {
                if (type == "bytes")
                {
                    var run = (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan((int)cursor, runPrefix));
                    runs.Add(run);
                    cursor += runPrefix + run;
                }
                else
                {
                    cursor += PrimitiveSize(spec, type);
                }
            }

            Assert.AreEqual(payload.Offset + n, cursor,
                "the large form's payload, decoded by FORMAT.md's layout of the filter, does not end where its length says");
            Assert.HasCount(1, runs, "the filter's layout decoded to other than its one run");
            Assert.AreEqual((long)filter.Buckets.RawData.Length, runs[0],
                "the run the layout decodes is not the filter's bits");

            var checksums = rows.Where(r => r.Field.StartsWith("CRC-32", StringComparison.Ordinal)).ToArray();
            Assert.HasCount(2, checksums, "the large form's table does not have two checksums, the header's and the payload's");
            foreach (var checksum in checksums)
            {
                var crc = new Crc32();
                var ranges = ByteRange.Matches(checksum.Field);
                Assert.IsGreaterThan(0, ranges.Count, $"'{checksum.Field}' does not say which bytes it covers");
                foreach (Match range in ranges)
                {
                    var from = long.Parse(range.Groups["from"].Value);
                    var to = long.Parse(range.Groups["to"].Value) + (range.Groups["plus"].Success ? n : 0);
                    crc.Append(bytes.AsSpan((int)from, (int)(to - from + 1)));
                }

                Assert.AreEqual(
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)Evaluate(checksum.Offset, n))),
                    crc.GetCurrentHashAsUInt32(),
                    $"the checksum at {checksum.Offset} is not the CRC-32 of what FORMAT.md says it covers, '{checksum.Field}'");
            }
        }

        /// <summary>
        /// Every structure the format can write has a section describing what it writes.
        /// </summary>
        [TestMethod]
        public void TestEveryStructureHasItsPayloadDocumented()
        {
            var documented = PayloadSections()
                .Select(m => m.Groups["name"].Value)
                .ToArray();

            Assert.AreEqual(documented.Length, documented.Distinct().Count(),
                "FORMAT.md documents the same structure twice");

            StructureRoster.AssertCoversEveryStructure(
                "payload documentation",
                documented.Select(Enum.Parse<StructureId>));
        }

        /// <summary>
        /// Each section's heading names the id a reader will actually find in the
        /// envelope. A section under the wrong id sends someone decoding by hand to the
        /// wrong layout, which is worse than no section: they get an answer.
        /// </summary>
        [TestMethod]
        public void TestEveryDocumentedIdMatchesTheStructureItNames()
        {
            foreach (Match section in PayloadSections())
            {
                var name = section.Groups["name"].Value;
                var documented = ushort.Parse(section.Groups["id"].Value);

                Assert.AreEqual(
                    (ushort)Enum.Parse<StructureId>(name), documented,
                    $"FORMAT.md documents {name} under id {documented}");
            }
        }

        /// <summary>
        /// The table of structure ids lists every structure, under the id the envelope
        /// carries. It is the first thing someone decoding a payload by hand needs --
        /// the id is how they learn which section to read -- and it stopped short of the
        /// last structures <see cref="StructureId"/> gained, so an envelope holding one of
        /// their ids named a structure that appeared in no table, while the document's own
        /// sections for it were headed with that id. Nothing read the table, and a
        /// document has no build to fail.
        /// </summary>
        /// <remarks>
        /// The roster is the enum, through <see cref="StructureRoster"/>, so a structure
        /// added to it and not to the table fails by name. The rows are the other half:
        /// each must name a structure and carry its id, no name or id may appear twice,
        /// and the pattern that finds them must tolerate the CRLF line endings the file
        /// is checked out with on Windows, which the last assertion holds it to.
        /// </remarks>
        [TestMethod]
        public void TestEveryStructureIsInTheIdTableUnderItsId()
        {
            var spec = Specification();
            var start = spec.IndexOf("### Structure ids", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, start, "FORMAT.md has no table of structure ids");
            var end = spec.IndexOf("\n### ", start + 1, StringComparison.Ordinal);
            var section = spec[start..(end < 0 ? spec.Length : end)];

            var rows = IdRow.Matches(section);
            Assert.IsGreaterThan(0, rows.Count,
                "no rows were found in the table of structure ids, so every assertion " +
                "made about it below is vacuous. The row pattern and the document have diverged.");

            var names = rows.Select(row => row.Groups["name"].Value).ToArray();
            Assert.AreEqual(names.Length, names.Distinct().Count(),
                "FORMAT.md lists the same structure twice in its table of structure ids");

            var ids = rows.Select(row => ushort.Parse(row.Groups["id"].Value)).ToArray();
            Assert.AreEqual(ids.Length, ids.Distinct().Count(),
                "FORMAT.md lists the same id twice in its table of structure ids");

            foreach (Match row in rows)
            {
                var name = row.Groups["name"].Value;
                var listed = ushort.Parse(row.Groups["id"].Value);

                Assert.IsTrue(Enum.GetNames<StructureId>().Contains(name, StringComparer.Ordinal),
                    $"the table of structure ids lists '{name}', which is not a structure");
                Assert.AreEqual(
                    (ushort)Enum.Parse<StructureId>(name), listed,
                    $"FORMAT.md lists {name} under id {listed} in its table of structure ids");
            }

            StructureRoster.AssertCoversEveryStructure(
                "structure id table",
                names.Select(Enum.Parse<StructureId>));

            // The same section with each line ending the other way, as a checkout on the
            // other platform would have it: the rows found must not depend on it.
            var lf = section.Replace("\r\n", "\n");
            var crlf = lf.Replace("\n", "\r\n");
            Assert.AreEqual(rows.Count, IdRow.Matches(lf).Count,
                "the table of structure ids is read differently with LF line endings");
            Assert.AreEqual(rows.Count, IdRow.Matches(crlf).Count,
                "the table of structure ids is read differently with CRLF line endings");
        }
    }
}
