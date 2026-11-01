using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// A structure reads the format versions it has defined and refuses every other.
    /// </summary>
    /// <remarks>
    /// The version in a payload's header is a property of the structure that wrote it:
    /// the stable filter has a version the Bloom filter never had. A reader that accepts
    /// anything up to the highest version anywhere in the library reads a payload its
    /// structure never wrote as though it were that structure's newest layout, which is
    /// the guess the format promises never to make.
    /// <para>
    /// These tests hold each structure to its own set. The sets live in one table in
    /// <c>PersistenceFormat</c>; the tests derive their rosters from
    /// <see cref="StructureId"/> and from the stored fixtures rather than listing
    /// structures, so a structure added without an entry, or a version defined without
    /// a stored payload, fails here by name.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestPersistenceFormatVersions
    {
        private const string FixturePrefix = "TestProbabilisticDataStructures.fixtures.";

        private static ushort VersionOf(byte[] frame) =>
            BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4));

        /// <summary>
        /// Every structure says which versions it reads. The roster is the structure
        /// list itself, so one added without an entry is named here.
        /// </summary>
        [TestMethod]
        public void TestEveryStructureStatesWhichVersionsItReads()
        {
            var table = PersistenceFormat.ReadableVersionTable;

            StructureRoster.AssertCoversEveryStructure("readable versions", table.Keys);

            foreach (var (id, versions) in table)
            {
                Assert.IsNotEmpty(versions, $"{id} reads no version at all");
                CollectionAssert.AreEqual(
                    versions.OrderBy(v => v).Distinct().ToArray(), versions,
                    $"the versions {id} reads are not listed once each, in order");
                Assert.IsFalse(versions.Contains((ushort)0),
                    $"{id} reads version 0, which is reserved and never written");
                Assert.IsLessThanOrEqualTo(
                    PersistenceFormat.MaxSupportedVersion, versions[^1],
                    $"{id} reads a version above the highest the library supports");
            }

            Assert.AreEqual(
                table.Values.Max(versions => versions[^1]),
                PersistenceFormat.MaxSupportedVersion,
                "the highest supported version is not the highest any structure reads");
        }

        /// <summary>
        /// A structure with no entry cannot be read at all, rather than inheriting a
        /// set, so forgetting one is a failure on the first payload and not a quiet
        /// acceptance.
        /// </summary>
        [TestMethod]
        public void TestAStructureWithNoEntryCannotBeRead()
        {
            var absent = (StructureId)ushort.MaxValue;

            var ex = Assert.ThrowsExactly<InvalidOperationException>(
                () => PersistenceFormat.ReadableVersions(absent));
            StringAssert.Contains(ex.Message, absent.ToString());
        }

        /// <summary>
        /// The words naming the versions a structure reads carry every one of them, in
        /// order. The wording is not pinned, only what it says.
        /// </summary>
        [TestMethod]
        public void TestTheDescriptionOfVersionsNamesEachOfThem()
        {
            foreach (var versions in new ushort[][] { new ushort[] { 1 }, new ushort[] { 1, 2 }, new ushort[] { 1, 2, 5 }, new ushort[] { 1, 4, 5, 9 } })
            {
                var said = Regex.Matches(PersistenceFormat.DescribeVersions(versions), @"\d+")
                    .Select(m => ushort.Parse(m.Value))
                    .ToArray();

                CollectionAssert.AreEqual(versions, said,
                    $"describing {{{string.Join(", ", versions)}}} named other versions");
            }
        }

        /// <summary>
        /// Every format version a structure defines has a stored payload that reads. A
        /// version defined without one, or a stored payload at a version no structure
        /// defined, fails here. The roster is the table of versions on one side and the
        /// embedded fixtures on the other, so neither can be left out by hand.
        /// </summary>
        [TestMethod]
        public void TestEveryDefinedVersionHasAStoredPayloadThatReads()
        {
            var readers = TestPersistenceAllStructures.SamplePayloads()
                .GroupBy(p => p.Id)
                .ToDictionary(g => g.Key, g => g.First().Read);

            // Stored payloads that are refused on purpose, each with the reason, which
            // is checked: an exemption that reads after all has gone stale.
            var refusedByDesign = new Dictionary<string, string>
            {
                ["varopt-badmerge-v1.bin"] =
                    "written by 6.2.0 after a merge that left it holding a wrong sample, " +
                    "and refused with its cause rather than read as if it were sound",
            };

            var names = typeof(TestPersistenceFormatVersions).Assembly
                .GetManifestResourceNames()
                .Where(n => n.StartsWith(FixturePrefix, StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.IsNotEmpty(names, "no fixtures are embedded, so nothing below is checked");

            var stored = new HashSet<(StructureId Id, ushort Version)>();

            foreach (var name in names)
            {
                var file = name[FixturePrefix.Length..];
                var bytes = ReadResource(name);
                var id = (StructureId)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6));
                var version = VersionOf(bytes);

                Assert.IsTrue(Enum.IsDefined(id), $"{file} holds structure id {(ushort)id}, which is not one");
                CollectionAssert.Contains(PersistenceFormat.ReadableVersions(id).ToList(), version,
                    $"{file} is {id} at version {version}, which {id} never defined");

                if (refusedByDesign.TryGetValue(file, out var why))
                {
                    Assert.IsFalse(string.IsNullOrWhiteSpace(why), $"{file} is exempt without a reason");
                    Assert.ThrowsExactly<InvalidDataException>(() => readers[id](bytes),
                        $"{file} is exempt as refused by design and now reads; the reason is out of date");
                    continue;
                }

                Assert.IsNotNull(readers[id](bytes), $"the stored {file} no longer reads");
                stored.Add((id, version));
            }

            foreach (var file in refusedByDesign.Keys)
            {
                CollectionAssert.Contains(
                    names.Select(n => n[FixturePrefix.Length..]).ToList(), file,
                    $"{file} is exempt but there is no such fixture");
            }

            var unstored = PersistenceFormat.ReadableVersionTable
                .SelectMany(entry => entry.Value.Select(v => (Id: entry.Key, Version: v)))
                .Where(pair => !stored.Contains(pair))
                .Select(pair => $"{pair.Id} version {pair.Version}")
                .ToArray();

            Assert.IsEmpty(unstored,
                "a format version is defined and nothing stored proves it still reads: " +
                string.Join(", ", unstored) + ". Check in the payload that version wrote.");
        }

        private static byte[] ReadResource(string name)
        {
            using var resource = typeof(TestPersistenceFormatVersions).Assembly
                .GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"resource {name} is not embedded");

            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            return buffer.ToArray();
        }
    }
}
