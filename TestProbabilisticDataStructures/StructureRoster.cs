using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Checks that a test named for every structure reaches every structure.
    /// </summary>
    /// <remarks>
    /// Several tests in this project sweep the whole library: every structure round
    /// trips while empty, every structure refuses a hash it was not written with,
    /// every structure that can be handed a hash keeps the one it was given. Each was
    /// a list written by hand, and a hand-written list of structures agrees with
    /// itself -- it is named "every structure" and means "every structure whoever last
    /// edited it remembered".
    /// <para>
    /// That is not hypothetical. When this was first written, all six such sweeps were
    /// short, and the gaps tracked the release: the structures added most recently
    /// were the ones missing. One of them ended by asserting that no two structures
    /// share a persistence id -- across a roster three structures short of the full
    /// set, so a duplicate given to any of those three would have passed the test
    /// written to catch precisely that.
    /// </para>
    /// <para>
    /// The rosters here are derived rather than typed out: from <see cref="StructureId"/>,
    /// which a structure cannot leave itself off of because one that is not in it
    /// cannot be persisted at all, and from the library's own public surface. A new
    /// structure fails these until it is either covered or exempted with a reason.
    /// </para>
    /// </remarks>
    internal static class StructureRoster
    {
        private static readonly Assembly Library = typeof(BloomFilter).Assembly;

        private static readonly Type HashFunction = typeof(Func<ReadOnlySpan<byte>, ulong>);

        /// <summary>
        /// Every public structure exposing SetHash, which is the surface the
        /// "hash cannot be replaced once it holds something" rule applies to.
        /// </summary>
        internal static IReadOnlyList<Type> WithSetHash { get; } =
            Library.GetTypes()
                .Where(t => t.IsPublic && !t.IsAbstract && IsAStructure(t))
                .Where(t => t.GetMethod("SetHash", new[] { HashFunction }) is not null)
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToArray();

        /// <summary>
        /// Every public structure that can be handed a hash as it is built, whether
        /// through a constructor or a static factory. This is a wider set than
        /// <see cref="WithSetHash"/>: for several structures, construction is the only
        /// place a hash can be installed at all.
        /// </summary>
        internal static IReadOnlyList<Type> TakingAHashWhenBuilt { get; } =
            Library.GetTypes()
                .Where(t => t.IsPublic && !t.IsAbstract && IsAStructure(t))
                .Where(t =>
                    t.GetConstructors().Any(TakesAHash)
                    || Library.GetTypes()
                        .SelectMany(f => f.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        // ReadFrom takes a hash too, but restoring a structure is not
                        // building one: it is the caller handing back the hash the
                        // payload says was in use. Only builders count here.
                        .Any(m => m.Name != "ReadFrom" && m.ReturnType == t && TakesAHash(m)))
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToArray();

        // What counts as a structure here: the library also holds hash plumbing whose
        // factories take a hash function, and those are not things anyone sweeps.
        // Being persistable is the line, and it is the same line StructureId draws.
        private static bool IsAStructure(Type type) =>
            type.GetInterfaces().Any(i =>
                i.IsGenericType
                && i.GetGenericTypeDefinition() == typeof(IBinaryPersistable<>));

        private static bool TakesAHash(MethodBase method) =>
            method.GetParameters().Any(p => p.ParameterType == HashFunction);

        /// <summary>
        /// Asserts a sweep reached every structure the persistence format knows about.
        /// </summary>
        /// <param name="sweep">Named in the failure, since several sweeps share this.</param>
        /// <param name="covered">The structures the sweep exercised.</param>
        /// <param name="exempt">
        /// Structures the sweep cannot apply to, each with its reason. An exemption is
        /// a claim about the structure rather than a way to quieten this, so it is
        /// checked in turn: one naming a structure the sweep does cover has gone stale
        /// and fails as loudly as a gap.
        /// </param>
        internal static void AssertCoversEveryStructure(
            string sweep,
            IEnumerable<StructureId> covered,
            params (StructureId Id, string Why)[] exempt)
        {
            AssertCovers(
                sweep,
                Enum.GetValues<StructureId>().Select(id => id.ToString()),
                covered.Select(id => id.ToString()),
                exempt.Select(e => (e.Id.ToString(), e.Why)));
        }

        /// <summary>
        /// Asserts a sweep reached every type in one of the rosters above.
        /// </summary>
        internal static void AssertCoversEveryType(
            string sweep,
            IReadOnlyList<Type> roster,
            IEnumerable<Type> covered,
            params (Type Type, string Why)[] exempt)
        {
            AssertCovers(
                sweep,
                roster.Select(t => t.Name),
                covered.Select(t => t.Name),
                exempt.Select(e => (e.Type.Name, e.Why)));
        }

        private static void AssertCovers(
            string sweep,
            IEnumerable<string> roster,
            IEnumerable<string> covered,
            IEnumerable<(string Name, string Why)> exempt)
        {
            var seen = covered.ToHashSet(StringComparer.Ordinal);
            var excuses = exempt.ToArray();
            var excused = excuses.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);

            Assert.AreEqual(excuses.Length, excused.Count,
                $"the {sweep} sweep exempts the same structure twice");

            foreach (var (name, why) in excuses)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(why),
                    $"the {sweep} sweep exempts {name} without saying why");
                Assert.IsFalse(seen.Contains(name),
                    $"the {sweep} sweep exempts {name} and then covers it anyway; " +
                    "the reason given is out of date");
            }

            var known = roster.ToArray();

            var unknown = seen.Concat(excused)
                .Where(name => !known.Contains(name, StringComparer.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.IsEmpty(unknown,
                $"the {sweep} sweep names " + string.Join(", ", unknown) +
                ", which is not in the roster it is checked against. Either the name " +
                "is wrong or the roster is derived from the wrong thing.");

            var missing = known
                .Where(name => !seen.Contains(name) && !excused.Contains(name))
                .ToArray();

            Assert.IsEmpty(missing,
                $"the {sweep} sweep is named for every structure but does not reach " +
                string.Join(", ", missing) +
                ". Cover it, or exempt it here with the reason it cannot apply.");
        }
    }
}
