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
    /// Holds the README's table of what each structure holds, and CAPACITY.md behind it,
    /// to the library.
    /// </summary>
    /// <remarks>
    /// A limit written in a document has no build to fail. Every number in the table is
    /// worked out again here, from the library's own sizing code and its limit symbols and
    /// from what a read of a small structure is charged (the internal
    /// <see cref="ReadBudget"/>, read off after the read), and the document must say what
    /// the library does. <c>CapacityRows</c> says how each is worked out.
    /// <para>
    /// Nothing here builds a structure of the size it reports, because the largest are
    /// gigabytes. Where a size is a limit symbol or sizing arithmetic, it is computed. Where
    /// it is a read charge, it is a line fitted to reads of structures a few megabytes
    /// large and carried out, and that line has to pass through every one of the reads it
    /// was fitted to; a charge that is exact in the structure's size gives an exact number,
    /// and one that depends on how arrays round up to eight bytes gives a number to the
    /// digits the slack cannot move, written with a <c>≈</c>. The numbers that only a
    /// build at the scale they describe can give are exempt, written with a <c>†</c>, and
    /// held to an estimate with the reason they are exempt checked in turn.
    /// </para>
    /// <para>
    /// The roster is <see cref="StructureId"/>, so a structure added to the library fails
    /// here until it has a row and a note. The prose is held the same way: a sentence
    /// that states a figure is a claim with a check, and a number of four digits or more
    /// that is neither in the table nor in a claim fails.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestCapacityDocumentation
    {
        private static string Document(string name)
        {
            using var resource = typeof(TestCapacityDocumentation).Assembly
                .GetManifestResourceStream("TestProbabilisticDataStructures." + name);
            Assert.IsNotNull(resource, $"{name} is not embedded in the test assembly");
            return new StreamReader(resource).ReadToEnd();
        }

        /// <summary>
        /// A document with every run of whitespace a single space, so that a sentence
        /// wrapped across lines is the sentence.
        /// </summary>
        private static string Flat(string text) => Regex.Replace(text, @"\s+", " ");

        private const string Heading = "### How much each one holds";

        private static string ReadmeSection()
        {
            var readme = Document("README.md");
            var start = readme.IndexOf(Heading, StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, start, "the README has no section on how much each one holds");
            var end = Regex.Match(readme[(start + Heading.Length)..], @"\n(### |## )").Index;
            return readme.Substring(start, Heading.Length + end);
        }

        private static readonly Dictionary<string, CapacityKind> Tables = new()
        {
            ["#### Sized for n items"] = CapacityKind.Sized,
            ["#### Grows as items are added"] = CapacityKind.Grows,
            ["#### Retains k"] = CapacityKind.Retains,
            ["#### Takes unlimited items"] = CapacityKind.Unlimited,
        };

        private static readonly Regex Row = new(
            @"^\| `(?<structure>\w+)` \| (?<counts>[^|]+?) \| (?<builds>[^|]+?) \| (?<loads>[^|]+?) \| (?<saves>[^|]+?) \|\r?$",
            RegexOptions.Multiline);

        private static IReadOnlyList<(CapacityKind Kind, StructureId Id, string Counts, string Builds, string Loads, string Saves)>
            DocumentedRows()
        {
            var section = ReadmeSection();
            var rows = new List<(CapacityKind, StructureId, string, string, string, string)>();
            var parts = Regex.Split(section, @"(?m)^(#### .*?)\r?$");

            for (var i = 1; i < parts.Length; i += 2)
            {
                var heading = parts[i].TrimEnd();
                Assert.IsTrue(Tables.TryGetValue(heading, out var kind),
                    $"the README's section has a table headed '{heading}', which is not one of the four kinds");

                foreach (Match m in Row.Matches(parts[i + 1]))
                {
                    Assert.IsTrue(Enum.TryParse<StructureId>(m.Groups["structure"].Value, out var id) && Enum.IsDefined(id),
                        $"the README's table lists '{m.Groups["structure"].Value}', which is not a structure");
                    rows.Add((kind, id, m.Groups["counts"].Value, m.Groups["builds"].Value, m.Groups["loads"].Value, m.Groups["saves"].Value));
                }
            }

            Assert.IsNotEmpty(rows,
                "no rows of the table were found in the README, so nothing was checked. The row pattern and the document have diverged.");
            CollectionAssert.AreEquivalent(Tables.Values.ToArray(),
                parts.Where((_, index) => index % 2 == 1).Select(h => Tables[h.TrimEnd()]).ToArray(),
                "the README's section is missing one of its four tables");
            return rows;
        }

        /// <summary>
        /// The rows worked out from the library, once for all the tests here. A row that
        /// cannot be worked out is reported with its name rather than ending the others.
        /// </summary>
        private static readonly Lazy<IReadOnlyList<(CapacityRow Row, string Builds, string Loads, string Saves, string Failure)>> Worked =
            new(() => CapacityRows.All.Select(row =>
            {
                try
                {
                    return (row, row.Builds(), row.Loads(), row.Saves!(), (string)null);
                }
                catch (Exception e)
                {
                    return (row, "", "", "", $"{row.Id} ({row.Counts}): {e.GetType().Name}: {e.Message}");
                }
            }).ToArray());

        /// <summary>
        /// Every row of the README's table is what the library gives, in both of its
        /// numbers, and every structure has one.
        /// </summary>
        [TestMethod]
        public void TestTheTableIsWhatTheLibraryGives()
        {
            var failures = Worked.Value.Where(w => w.Failure is not null).Select(w => w.Failure!).ToArray();
            Assert.IsEmpty(failures, "rows that could not be worked out:\n" + string.Join("\n", failures));

            var documented = DocumentedRows();
            var wrong = new List<string>();

            foreach (var (row, builds, loads, saves, _) in Worked.Value)
            {
                var found = documented.Where(d => d.Id == row.Id && d.Counts == row.Counts).ToArray();
                if (found.Length != 1)
                {
                    wrong.Add($"{row.Id}: the README has {found.Length} rows counting '{row.Counts}', and the library has one. " +
                        $"It should read: | `{row.Id}` | {row.Counts} | {builds} | {loads} | {saves} |");
                    continue;
                }

                var doc = found[0];
                if (doc.Kind != CapacityRows.KindOf(row))
                {
                    wrong.Add($"{row.Id} ({row.Counts}) is listed under {doc.Kind} and belongs under {CapacityRows.KindOf(row)}");
                }

                if (doc.Builds != builds)
                {
                    wrong.Add($"{row.Id} ({row.Counts}) builds up to {builds}, and the README says {doc.Builds}");
                }

                if (doc.Loads != loads)
                {
                    wrong.Add($"{row.Id} ({row.Counts}) loads by default up to {loads}, and the README says {doc.Loads}");
                }

                if (doc.Saves != saves)
                {
                    wrong.Add($"{row.Id} ({row.Counts}) saves up to {saves}, and the README says {doc.Saves}");
                }
            }

            foreach (var d in documented.Where(d => !Worked.Value.Any(w => w.Row.Id == d.Id && w.Row.Counts == d.Counts)))
            {
                wrong.Add($"the README has a row for {d.Id} counting '{d.Counts}', which the library has no row for");
            }

            Assert.IsEmpty(wrong, "the README's table and the library disagree:\n" + string.Join("\n", wrong));

            StructureRoster.AssertCoversEveryStructure(
                "capacity table", documented.Select(d => d.Id));
        }

        /// <summary>
        /// A cell written with a dagger is an exemption: the number was measured at the
        /// scale it describes and cannot be rebuilt here. The row says so, and the set of
        /// them is this one, so an exemption cannot be added to quieten a number that could
        /// have been worked out.
        /// </summary>
        [TestMethod]
        public void TestTheMeasuredNumbersAreTheDeclaredOnes()
        {
            var measured = Worked.Value
                .SelectMany(w => new[] { ("builds", w.Builds), ("loads", w.Loads), ("saves", w.Saves) }
                    .Where(c => c.Item2.EndsWith("†", StringComparison.Ordinal))
                    .Select(c => (Id: w.Row.Id, Column: c.Item1)))
                .OrderBy(x => x.Id).ThenBy(x => x.Column, StringComparer.Ordinal)
                .ToArray();

            CollectionAssert.AreEqual(
                new[]
                {
                    (StructureId.SublimeCountMinSketch, "loads"),
                    (StructureId.DpswSketch, "loads"),
                    (StructureId.DpswSketch, "saves"),
                }.OrderBy(x => x.Item1).ThenBy(x => x.Item2, StringComparer.Ordinal).ToArray(),
                measured.Select(x => (x.Id, x.Column)).ToArray(),
                "the numbers measured at scale, and so exempt, are not the three declared; each has its reason checked where it is written");

            var readme = Flat(ReadmeSection());
            StringAssert.Contains(readme, "†", "the README does not explain its dagger");
        }

        /// <summary>
        /// Every structure has a note saying what its numbers count and what sets them.
        /// </summary>
        [TestMethod]
        public void TestEveryStructureHasANote()
        {
            var headings = Regex.Matches(Document("CAPACITY.md"), @"^### `(?<name>\w+)`\r?$", RegexOptions.Multiline)
                .Select(m => m.Groups["name"].Value)
                .ToArray();

            Assert.IsNotEmpty(headings, "no notes were found in CAPACITY.md. The heading pattern and the document have diverged.");
            Assert.AreEqual(headings.Length, headings.Distinct().Count(), "CAPACITY.md has two notes for one structure");

            StructureRoster.AssertCoversEveryStructure(
                "capacity notes", headings.Select(Enum.Parse<StructureId>));
        }

        /// <summary>
        /// Each sentence in CAPACITY.md that states a figure or a rule is in the document
        /// and is true of the library.
        /// </summary>
        [TestMethod]
        public void TestTheProseSaysWhatTheLibraryDoes()
        {
            // The rules of thumb are the slopes of the fits the rows were worked out with.
            Assert.IsEmpty(Worked.Value.Where(w => w.Failure is not null).ToArray(),
                "rows could not be worked out, so the claims about their fits cannot be read");

            var document = Flat(Document("CAPACITY.md"));
            var wrong = new List<string>();

            var claims = CapacityClaims.All();
            Assert.AreEqual(claims.Count, claims.Select(c => c.Phrase).Distinct().Count(), "a claim is made twice");

            foreach (var claim in claims)
            {
                if (!document.Contains(claim.Phrase, StringComparison.Ordinal))
                {
                    wrong.Add($"CAPACITY.md no longer says: {claim.Phrase}");
                    continue;
                }

                bool holds;
                try
                {
                    holds = claim.Holds();
                }
                catch (Exception e)
                {
                    wrong.Add($"could not check '{claim.Phrase}': {e.GetType().Name}: {e.Message}");
                    continue;
                }

                if (!holds)
                {
                    wrong.Add($"CAPACITY.md says '{claim.Phrase}', and the library does not");
                }
            }

            Assert.IsEmpty(wrong, "CAPACITY.md and the library disagree:\n" + string.Join("\n", wrong));
        }

        /// <summary>
        /// What CAPACITY.md reports as measured at a scale the suite cannot build is in the
        /// document, says that it was measured, and could not have been rebuilt here.
        /// </summary>
        [TestMethod]
        public void TestWhatWasMeasuredCouldNotHaveBeenRebuilt()
        {
            var document = Flat(Document("CAPACITY.md"));
            StringAssert.Contains(document, "measured", "CAPACITY.md does not say anything was measured");

            foreach (var (phrase, why, holds) in CapacityClaims.Measured())
            {
                StringAssert.Contains(document, phrase, "CAPACITY.md no longer reports the measurement");
                Assert.IsFalse(string.IsNullOrWhiteSpace(why), $"'{phrase}' is exempt without a reason");
                Assert.IsTrue(holds(), $"the reason '{phrase}' is exempt has stopped being true: {why}");
            }
        }

        /// <summary>
        /// A number of four digits or more in the prose is either in the table or in a
        /// claim. Without this the prose would be free to state any figure, and a
        /// document with a count in a sentence is a roster maintained by hand.
        /// </summary>
        [TestMethod]
        public void TestNoFigureInTheProseIsUnchecked()
        {
            var figure = new Regex(@"(?<![\w.,])(\d{1,3}(?:,\d{3})+|\d{4,})(?![\w])");

            var table = string.Join(" ", Worked.Value.SelectMany(w => new[] { w.Row.Counts, w.Builds, w.Loads, w.Saves }));
            var allowed = figure.Matches(table).Select(m => m.Value)
                .Concat(CapacityClaims.All().SelectMany(c => figure.Matches(c.Phrase).Select(m => m.Value)))
                .ToHashSet();

            // The README's tables are checked by the rows; only its words are read here.
            var prose = Document("CAPACITY.md") + "\n" + Row.Replace(ReadmeSection(), string.Empty);

            var unverified = figure.Matches(prose).Select(m => m.Value).Where(v => !allowed.Contains(v)).Distinct().ToArray();
            Assert.IsEmpty(unverified,
                "figures in the prose that are neither in the table nor in a claim: " + string.Join(", ", unverified));
        }
    }
}
