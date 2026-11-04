using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// A structure too large to write is refused by its <c>WriteTo</c> before anything is
    /// built for the payload, naming the size the payload would have been (#216).
    /// </summary>
    /// <remarks>
    /// The structures here cannot be built at the sizes that matter, so a small one is made
    /// to claim a large shape: the private fields that size its table are set by
    /// reflection, as <see cref="CapacityClaims"/> does with a Sublime sketch's width. A
    /// <c>WriteTo</c> that sizes its payload from the layout before building anything sees
    /// that shape and refuses it; one that builds first fails at the building, and none of
    /// the shapes chosen here can be built, so no run of this needs the memory.
    /// <para>
    /// Each refusal is held to three things: the exception the writer's ceiling throws,
    /// naming the payload's whole size and the most one can be; nothing written to the
    /// stream; and nothing allocated for the payload. Afterwards the shape is put back and
    /// the structure has to write the bytes it wrote before, so the refusal changed nothing.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestPayloadCount
    {
        private const BindingFlags Fields = BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>The most a payload can be: the largest array less the envelope.</summary>
        private const long LargestPayload = 2_147_483_591L - 18;

        /// <summary>
        /// The most a refusal may allocate. The ceiling's exception, its message and the
        /// assertion that catches it were probed at 1,616 bytes, 3,456 the first time; this
        /// is twenty times that. The payloads refused are gigabytes, so anything of theirs
        /// built before the refusal is far past it.
        /// </summary>
        private const long RefusalAllocates = 64 * 1024;

        /// <summary>
        /// A Count Sketch's payload is its two dimensions and the cells as one run, twelve
        /// bytes and eight a cell. The writer staged the run in a <c>byte[]</c> of its own
        /// before any size was checked, so: from 268,435,446 cells, the first past the
        /// ceiling, the refusal came after a two-gibibyte array was built; from 268,435,449
        /// that array is longer than one can be and the write failed with an
        /// <c>OutOfMemoryException</c>; from 2^28 its length is past <c>int.MaxValue</c> and
        /// it failed with an <c>OverflowException</c>. Five rows of 100,000,000 is what
        /// ε = 0.0001 and δ = 1% build.
        /// </summary>
        [TestMethod]
        [DataRow(1u, 268_435_446u, 2_147_483_580L)]
        [DataRow(1u, 268_435_449u, 2_147_483_604L)]
        [DataRow(5u, 100_000_000u, 4_000_000_012L)]
        public void TestACountSketchTooLargeToWriteIsRefusedBeforeItsCellsAreStaged(
            uint depth, uint width, long payload)
        {
            Assert.AreEqual(12 + 8L * depth * width, payload, "the row's payload is miscomputed");

            var sketch = new CountSketch(0.5, 0.5);
            for (var i = 0; i < 40; i++) sketch.Add(Key(i), i % 5 - 2);

            var shape = new[] { "width", "depth" }
                .Select(name => typeof(CountSketch).GetField(name, Fields)!)
                .ToArray();

            AssertRefusedWhileClaiming(sketch, shape, new object[] { width, depth }, payload);
        }

        /// <summary>
        /// An invertible Bloom lookup table's payload is its cell count and key size, then
        /// three runs: the counts and hash sums at eight bytes a cell, the key sums at the
        /// key size. The writer staged the first and third in a <c>byte[]</c> each before any
        /// size was checked, which past 268,435,448 cells is an array no runtime holds, so
        /// with keys under eight bytes, the only tables the constructor builds that large,
        /// the write failed in the allocation. <c>new InvertibleBloomLookupTable(200_000_000,
        /// 4)</c> is 300,000,000 cells.
        /// </summary>
        [TestMethod]
        [DataRow(1, 268_435_449u, 4_563_402_653L)]
        [DataRow(4, 300_000_000u, 6_000_000_020L)]
        public void TestAnInvertibleBloomLookupTableTooLargeToWriteIsRefusedBeforeItsCellsAreStaged(
            int keySize, uint cells, long payload)
        {
            Assert.AreEqual(20 + (16L + keySize) * cells, payload, "the row's payload is miscomputed");

            var table = new InvertibleBloomLookupTable(4, keySize);
            for (var i = 0; i < 3; i++)
            {
                var key = new byte[keySize];
                key[0] = (byte)(i + 1);
                table.Add(key);
            }

            var shape = new[] { typeof(InvertibleBloomLookupTable).GetField("cells", Fields)! };

            AssertRefusedWhileClaiming(table, shape, new object[] { cells }, payload);
        }

        /// <summary>
        /// A SetSketch's payload is its four parameters, a variant byte for the second
        /// construction, and two bytes a register. The writer wrote the registers one field
        /// at a time and only the buffer's growth checked a size: read from the code, a
        /// sketch past the ceiling was refused at the first write over it, after copying two
        /// gibibytes, naming that write's end rather than the payload's. That needs a sketch
        /// of four gibibytes to see, so it is not what fails here. What fails here is the
        /// cause of it: the writer never asked how large the payload is, so a sketch
        /// claiming more registers than it holds was not refused at all. The largest sketch,
        /// a register for every element an array holds, is 4,294,967,206 bytes, twice what a
        /// payload can be; with the variant byte it is one more.
        /// </summary>
        [TestMethod]
        [DataRow(SetSketchVariant.SetSketch1, 4_294_967_206L)]
        [DataRow(SetSketchVariant.SetSketch2, 4_294_967_207L)]
        public void TestASetSketchTooLargeToWriteIsRefusedNamingItsWholeSize(
            SetSketchVariant variant, long payload)
        {
            var variantByte = variant == SetSketchVariant.SetSketch1 ? 0 : 1;
            Assert.AreEqual(24 + variantByte + 2L * Array.MaxLength, payload,
                "the row's payload is miscomputed");

            var sketch = new SetSketch(8, SetSketch.DefaultBase, SetSketch.DefaultRate,
                SetSketch.DefaultQ, null, variant);
            for (var i = 0; i < 40; i++) sketch.Add(Key(i));

            var shape = new[] { typeof(SetSketch).GetField("m", Fields)! };

            AssertRefusedWhileClaiming(sketch, shape, new object[] { Array.MaxLength }, payload);
        }

        /// <summary>
        /// The structures whose <c>WriteTo</c> does not yet count its payload first. Each is
        /// checked: one that has a body has been converted, and its line here is stale.
        /// </summary>
        /// <remarks>
        /// One line each with a blank line between, because the branches converting
        /// structures delete their lines in parallel and git conflicts on adjacent ones.
        /// Converting a structure is deleting its line here, not the blank, and giving it
        /// states (<see cref="PayloadState"/>) in its own test class.
        /// </remarks>
        private static readonly (StructureId Id, string Why)[] NotYetCounted =
        {
            (StructureId.BloomFilter, NoBody),

            (StructureId.CountingBloomFilter, NoBody),

            (StructureId.DeletableBloomFilter, NoBody),

            (StructureId.PartitionedBloomFilter, NoBody),

            (StructureId.ScalableBloomFilter, NoBody),

            (StructureId.StableBloomFilter, NoBody),

            (StructureId.InverseBloomFilter, NoBody),

            (StructureId.CuckooBloomFilter, NoBody),

            (StructureId.HyperLogLog, NoBody),

            (StructureId.MinHashSignature, NoBody),

            (StructureId.BinaryFuseFilter, NoBody),

            (StructureId.DDSketch, NoBody),

            (StructureId.HyperLogLogPlus, NoBody),

            (StructureId.QuotientFilter, NoBody),

            (StructureId.ThetaSketch, NoBody),

            (StructureId.SimHashSignature, NoBody),

            (StructureId.BloomierFilter, NoBody),

            (StructureId.HeavyKeeper, NoBody),

            (StructureId.VarOpt, NoBody),

            (StructureId.UltraLogLog, NoBody),

            (StructureId.Grafite, NoBody),

            (StructureId.InfiniFilter, NoBody),

            (StructureId.MementoFilter, NoBody),

            (StructureId.SublimeCountMinSketch, NoBody),

            (StructureId.TupleSketch, NoBody),

            (StructureId.PrivateCountMinSketch, NoBody),

            (StructureId.DpswSketch, NoBody),
        };

        private const string NoBody =
            "not yet an IPayloadBody, so its WriteTo writes without counting first (#216)";

        /// <summary>
        /// Every structure with a body is counted by its own <c>WriteTo</c>, once, before it
        /// writes, at exactly the length of the payload it then writes, in every state its
        /// test class gives, at the version that state says it writes.
        /// </summary>
        /// <remarks>
        /// The count is watched as <c>WriteTo</c> makes it, through
        /// <see cref="PersistenceFormat.Counted"/>, rather than made again here: a body
        /// counted by a test and never by the write would vouch for a check that does not
        /// run. The real length is what <see cref="CapacityRows"/> takes a payload to be,
        /// the bytes less the envelope's eighteen.
        /// <para>
        /// The roster is <see cref="StructureId"/>. A structure is covered by giving it
        /// states, in a static method returning <see cref="PayloadState"/>s anywhere in this
        /// assembly, which only a structure with a body can, and exempt until then. An
        /// exemption is checked by reflection: a structure exempt for having no body, that
        /// has one, fails as loudly as one that is neither covered nor exempt.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestEveryStructureWithABodyIsCountedByItsWriteToAtTheLengthItWrites()
        {
            var lengths = new Dictionary<StructureId, HashSet<long>>();
            var states = 0;

            foreach (var state in AllStates())
            {
                var counts = new List<(IPayloadBody Body, long Length)>();
                byte[] bytes;
                PersistenceFormat.Counted = (body, length) => counts.Add((body, length));
                try
                {
                    bytes = state.Write();
                }
                finally
                {
                    PersistenceFormat.Counted = null;
                }

                var id = (StructureId)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6));
                var name = $"{id}, {state.Name}";
                Assert.AreEqual(id.ToString(), state.Structure.GetType().Name,
                    $"{name}: the envelope names another structure");

                var own = counts.Where(c => ReferenceEquals(c.Body, state.Structure)).ToArray();
                Assert.HasCount(1, own,
                    $"{name}: its WriteTo did not count its own payload, once, before writing it");
                Assert.AreEqual(bytes.Length - (long)PersistenceFormat.EnvelopeLength, own[0].Length,
                    $"{name}: counted {own[0].Length:N0} bytes and wrote {bytes.Length - 18:N0}");
                Assert.AreEqual(state.Version, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)),
                    $"{name}: written at another version than the state says");

                if (!lengths.TryGetValue(id, out var seen))
                {
                    lengths[id] = seen = new HashSet<long>();
                }

                seen.Add(own[0].Length);
                states++;
            }

            Assert.IsGreaterThan(0, states, "no states were found, so nothing was counted");

            // A structure whose states all write one length could be counted by a constant.
            foreach (var (id, seen) in lengths)
            {
                Assert.IsGreaterThan(1, seen.Count,
                    $"every state of {id} writes {seen.First():N0} bytes, so nothing the count " +
                    "depends on was varied");
            }

            foreach (var (id, why) in NotYetCounted)
            {
                Assert.IsFalse(typeof(IPayloadBody).IsAssignableFrom(TypeOf(id)),
                    $"{id} is exempt as {why}, and it has a body: delete its exemption and " +
                    "give it states");
            }

            StructureRoster.AssertCoversEveryStructure("payload count", lengths.Keys, NotYetCounted);
        }

        /// <summary>
        /// A body that counts one payload and writes another is refused, with nothing
        /// written, whether it is the length or the version that differs: the envelope would
        /// otherwise frame what was written under a size and version nobody checked.
        /// </summary>
        [TestMethod]
        [DataRow(1, PersistenceFormat.DefaultVersion, "wrote 5 bytes")]
        [DataRow(-1, PersistenceFormat.DefaultVersion, "wrote 3 bytes")]
        [DataRow(0, PersistenceFormat.SetSketchVariantVersion, "version 4")]
        public void TestABodyThatWritesOtherThanItCountedIsRefused(
            int moreWritten, int versionWritten, string named)
        {
            var body = new Inconsistent(moreWritten, (ushort)versionWritten);
            using var stream = new MemoryStream();

            var ex = Assert.ThrowsExactly<InvalidOperationException>(
                () => PersistenceFormat.Write(stream, body));

            StringAssert.Contains(ex.Message, "counted a payload of 4 bytes, version 1",
                "the refusal should say what was counted");
            StringAssert.Contains(ex.Message, named, "the refusal should say what was written");
            Assert.AreEqual(0L, stream.Length, "an inconsistent body was written");

            // And the same body agreeing with itself writes.
            PersistenceFormat.Write(stream, new Inconsistent(0, PersistenceFormat.DefaultVersion));
            Assert.AreEqual(4L + PersistenceFormat.EnvelopeLength, stream.Length);
        }

        /// <summary>
        /// Four bytes when counted, and four more or fewer, at another version or not, when
        /// written.
        /// </summary>
        private sealed class Inconsistent(int moreWritten, ushort versionWritten) : IPayloadBody
        {
            public EnvelopeHeader WritePayload(PayloadWriter payload)
            {
                if (payload.IsCounting)
                {
                    payload.WriteUInt32(0);
                    return new(StructureId.CountSketch, HashId.XxHash3_64);
                }

                for (var i = 0; i < 4 + moreWritten; i++) payload.WriteByte(0);
                return new(StructureId.CountSketch, HashId.XxHash3_64, versionWritten);
            }
        }

        /// <summary>
        /// Every state any test class gives: the static methods of this assembly named
        /// <c>PayloadStates</c> and returning <see cref="PayloadState"/>s, so that a
        /// structure's states live with its tests. One misnamed is not found, and its
        /// structure fails the roster as neither covered nor exempt.
        /// </summary>
        private static IEnumerable<PayloadState> AllStates()
        {
            var providers = typeof(TestPayloadCount).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .Where(m => m.Name == "PayloadStates"
                    && m.ReturnType == typeof(IEnumerable<PayloadState>)
                    && m.GetParameters().Length == 0)
                .OrderBy(m => m.DeclaringType!.FullName, StringComparer.Ordinal)
                .ToArray();

            Assert.IsNotEmpty(providers, "no class gives any states");

            return providers.SelectMany(m => (IEnumerable<PayloadState>)m.Invoke(null, null)!);
        }

        private static Type TypeOf(StructureId id) =>
            typeof(BloomFilter).Assembly.GetType($"ProbabilisticDataStructures.{id}", throwOnError: true)!;

        /// <summary>
        /// Has the structure claim a shape, asserts its <c>WriteTo</c> refuses it, and puts
        /// the shape back.
        /// </summary>
        internal static void AssertRefusedWhileClaiming<T>(
            T structure, FieldInfo[] shape, object[] claimed, long payload)
            where T : IBinaryPersistable<T>
        {
            var before = structure.ToByteArray();
            var held = shape.Select(field => field.GetValue(structure)).ToArray();

            for (var i = 0; i < shape.Length; i++) shape[i].SetValue(structure, claimed[i]);

            try
            {
                using var stream = new MemoryStream();
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var ex = Assert.ThrowsExactly<InvalidOperationException>(
                    () => structure.WriteTo(stream),
                    $"a {typeof(T).Name} whose payload would be {payload:N0} bytes was not refused " +
                    "with the writer's ceiling");
                var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

                StringAssert.Contains(ex.Message, payload.ToString("N0"),
                    "the refusal should name the size the whole payload would be");
                StringAssert.Contains(ex.Message, LargestPayload.ToString("N0"),
                    "the refusal should name the size one can be");
                Assert.AreEqual(0L, stream.Length, "a refused write wrote to the stream");
                Assert.IsLessThan(RefusalAllocates, allocated,
                    $"the refusal allocated {allocated:N0} bytes: the payload was built, or " +
                    "begun, before its size was checked");
            }
            finally
            {
                for (var i = 0; i < shape.Length; i++) shape[i].SetValue(structure, held[i]);
            }

            CollectionAssert.AreEqual(before, structure.ToByteArray(),
                "the structure writes differently after a refused write");
        }

        private static byte[] Key(int i) => System.Text.Encoding.ASCII.GetBytes($"item-{i}");
    }

    /// <summary>
    /// A structure with a body, in a state whose payload
    /// <see cref="TestPayloadCount.TestEveryStructureWithABodyIsCountedByItsWriteToAtTheLengthItWrites"/>
    /// holds the count to, and the format version that state is written at.
    /// </summary>
    /// <remarks>
    /// A structure's states are given by a static method in its own test class returning
    /// these, found by reflection. Give the states the layout depends on: the shapes, the
    /// contents a length depends on, and each version the writer chooses between.
    /// </remarks>
    internal sealed record PayloadState(string Name, IPayloadBody Structure, ushort Version, Func<byte[]> Write)
    {
        internal static PayloadState Of<T>(
            string name, T structure, ushort version = PersistenceFormat.DefaultVersion)
            where T : IBinaryPersistable<T>, IPayloadBody =>
            new(name, structure, version, () => structure.ToByteArray());
    }
}
