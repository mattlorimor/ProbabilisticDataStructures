using System;
using System.Collections.Generic;
using System.IO;
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

        private static string Specification()
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

            foreach (Match row in rows)
            {
                var version = ushort.Parse(row.Groups["version"].Value);
                Assert.IsTrue(seenVersions.Add(version),
                    $"FORMAT.md lists format version {version} on more than one row");

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
