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
    /// A structure too large to write as one array is refused by its <c>ToByteArray</c>
    /// before anything is built for the payload, naming the size the payload would have
    /// been (#216, #229); one with a run longer than any payload holds is refused for that
    /// run by its <c>WriteTo</c> as well.
    /// </summary>
    /// <remarks>
    /// The structures here cannot be built at the sizes that matter, so a small one is made
    /// to claim a large shape: the private fields that size its table are set by
    /// reflection, as <see cref="CapacityClaims"/> does with a Sublime sketch's width. A
    /// write that sizes its payload from the layout before building anything sees that
    /// shape and refuses it; one that builds first fails at the building, and none of the
    /// shapes chosen here can be built, so no run of this needs the memory. A payload past
    /// one array is written by <c>WriteTo</c> in the large form, streamed (#225), so a
    /// shape claimed past one array is asked of <c>ToByteArray</c> and never of
    /// <c>WriteTo</c>, which would set about writing it.
    /// <para>
    /// Each refusal is held to three things: the exception the ceiling throws, naming the
    /// payload's whole size and the most one can be, or naming the run; nothing written to
    /// a stream; and nothing allocated for the payload. Afterwards the shape is put back and
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
        /// ε = 0.0001 and δ = 1% build. At 2^32 cells the product of the two 32-bit
        /// dimensions is nought in 32 bits, so a count that multiplied them there would
        /// call the table empty. None of these is a byte array's; the first two are written to
        /// a stream in the large form (#225), and from 268,435,449 cells the cells are a run
        /// longer than one array, the longest a payload holds since a reader reads a run into
        /// one, so the last three are refused for that, by <c>WriteTo</c> as well. The second
        /// and third straddle that bound.
        /// </summary>
        [TestMethod]
        [DataRow(1u, 268_435_446u, 2_147_483_580L)]
        [DataRow(1u, 268_435_448u, 2_147_483_596L)]
        [DataRow(1u, 268_435_449u, 2_147_483_604L)]
        [DataRow(5u, 100_000_000u, 4_000_000_012L)]
        [DataRow(4u, 1_073_741_824u, 34_359_738_380L)]
        public void TestACountSketchTooLargeToWriteIsRefusedBeforeItsCellsAreStaged(
            uint depth, uint width, long payload)
        {
            Assert.AreEqual(12 + 8L * depth * width, payload, "the row's payload is miscomputed");

            var sketch = new CountSketch(0.5, 0.5);
            for (var i = 0; i < 40; i++) sketch.Add(Key(i), i % 5 - 2);

            var run = 8L * depth * width;
            if (run > PayloadWriter.LongestRun)
            {
                AssertRunRefusedWhileClaiming(sketch, run, (sketch, "width", width), (sketch, "depth", depth));
            }
            else
            {
                AssertRefusedWhileClaiming(sketch, payload, (sketch, "width", width), (sketch, "depth", depth));
            }
        }

        /// <summary>
        /// A Count-Min sketch's payload is its two parameters, its dimensions, a placement
        /// byte and its count, thirty-three bytes, then the cells at eight bytes each with no
        /// run around them. The writer wrote them a cell at a time, so only the buffer's
        /// growth checked a size. 268,435,443 cells in one row is the first past the
        /// ceiling, and 2^32 is where the product of the dimensions is nought in 32 bits.
        /// </summary>
        [TestMethod]
        [DataRow(1u, 268_435_443u, 2_147_483_577L)]
        [DataRow(4u, 1_073_741_824u, 34_359_738_401L)]
        public void TestACountMinSketchTooLargeToWriteIsRefusedNamingItsWholeSize(
            uint depth, uint width, long payload)
        {
            Assert.AreEqual(33 + 8L * depth * width, payload, "the row's payload is miscomputed");

            var sketch = new CountMinSketch(0.5, 0.5);
            for (var i = 0; i < 40; i++) sketch.Add(Key(i));

            AssertRefusedWhileClaiming(sketch, payload, (sketch, "Width", width), (sketch, "Depth", depth));
        }

        /// <summary>
        /// A top-k's payload holds its sketch whole, envelope and all, inside a run, so the
        /// sketch is counted as part of it. The corner is a sketch that fits a payload of its
        /// own and does not fit inside the top-k: 268,435,442 cells is a sketch payload of
        /// 2,147,483,569 bytes, four under the ceiling, and the top-k around it adds its own
        /// eight, the run's prefix, the sketch's envelope and its heap. A top-k past the
        /// ceiling by its sketch alone holds that sketch, 2,147,483,577 bytes, four past the
        /// ceiling, in the large form's envelope, eight bytes longer than the ordinary one,
        /// as its own write would frame it, and that envelope is a run of 2,147,483,603 bytes,
        /// longer than one array: no reader could read it, so it is refused for the run, by
        /// <c>WriteTo</c> too. The two rows straddle that choice; a sketch nested in the large
        /// form is written once runs are widened (#228).
        /// </summary>
        [TestMethod]
        [DataRow(268_435_442u, 2_147_483_630L)]
        [DataRow(268_435_443u, 2_147_483_646L)]
        public void TestATopKTooLargeToWriteIsRefusedNamingItsWholeSize(uint width, long payload)
        {
            // K and N, the run's prefix and the sketch's envelope, the sketch, the heap's
            // count, and each element's prefix, bytes and frequency.
            var elements = new[] { "a", "bb" };
            var sketchPayload = 33 + 8L * width;
            var envelope = sketchPayload > PersistenceFormat.MaxPayloadLength
                ? PersistenceFormat.LargeEnvelopeLength
                : PersistenceFormat.EnvelopeLength;
            Assert.AreEqual(
                8 + 4 + envelope + sketchPayload + 4 + elements.Sum(e => 4 + e.Length + 8), payload,
                "the row's payload is miscomputed");

            var topK = new TopK(0.5, 0.5, 3);
            foreach (var element in elements) topK.Add(System.Text.Encoding.ASCII.GetBytes(element));
            var sketch = (CountMinSketch)typeof(TopK).GetProperty("Cms", Fields)!.GetValue(topK)!;

            var nested = envelope + sketchPayload;
            if (nested > PayloadWriter.LongestRun)
            {
                AssertRunRefusedWhileClaiming(topK, nested, (sketch, "Width", width), (sketch, "Depth", 1u));
            }
            else
            {
                AssertRefusedWhileClaiming(topK, payload, (sketch, "Width", width), (sketch, "Depth", 1u));
            }
        }

        /// <summary>
        /// A payload that fits is built once, into a buffer of the length counted, and framed:
        /// what a write allocates is the payload twice, and little else. Before the count,
        /// this sketch's write allocated 240,432 bytes: its cells staged in an array of their
        /// own, and a writer's buffer started small and grown to hold them. A writer grown by
        /// doubling, as one is that is not given the counted length, allocates more again.
        /// Measured as the least of five, since a collection landing mid-write can add a
        /// retired allocation context's worth (TESTING.md, on the read budget).
        /// </summary>
        [TestMethod]
        public void TestAPayloadThatFitsIsBuiltOnceAtItsCountedLength()
        {
            var sketch = new CountSketch(0.01, 0.5);
            for (var i = 0; i < 40; i++) sketch.Add(Key(i), i);

            var payload = sketch.ToByteArray().Length - (long)PersistenceFormat.EnvelopeLength;
            Assert.AreEqual(12 + 8L * 10_000, payload, "the sketch is not the shape the bound assumes");

            var allocated = Enumerable.Range(0, 5).Min(_ =>
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                sketch.WriteTo(Stream.Null);
                return GC.GetAllocatedBytesForCurrentThread() - before;
            });

            // The payload's buffer and the frame, which is the payload and the envelope.
            var twice = 2 * payload + PersistenceFormat.EnvelopeLength;
            Assert.IsGreaterThanOrEqualTo(twice, allocated, "the write allocated less than it builds");
            Assert.IsLessThan(twice + WriteOverhead, allocated,
                $"the write allocated {allocated:N0} bytes against the {twice:N0} it builds");
        }

        /// <summary>
        /// What a write allocates besides the payload's buffer and the frame: their array
        /// headers and the counter, probed at 166 bytes. This is about twenty-five times
        /// that, and a twentieth of the one more copy of this payload it is there to catch.
        /// </summary>
        private const long WriteOverhead = 4096;

        /// <summary>
        /// An invertible Bloom lookup table's payload is its cell count and key size, then
        /// three runs: the counts and hash sums at eight bytes a cell, the key sums at the
        /// key size. The writer staged the first and third in a <c>byte[]</c> each before any
        /// size was checked, which past 268,435,448 cells is an array no runtime holds, so
        /// with keys under eight bytes, the only tables the constructor builds that large,
        /// the write failed in the allocation. <c>new InvertibleBloomLookupTable(200_000_000,
        /// 4)</c> is 300,000,000 cells. At 2^29 cells, which one-byte keys reach, a run of
        /// eight bytes a cell is 2^32 bytes, nought in 32 bits: a cell count times
        /// <c>sizeof(long)</c> is worked out in 32 bits unless one side is widened first,
        /// because the constant converts to the cell count's <c>uint</c>. The first row's
        /// runs are each within the longest a payload holds, one array, so only a byte array
        /// refuses it; in the others the counts, the first run, are longer than one array, and
        /// <c>WriteTo</c> refuses them for that too. The first two straddle that bound.
        /// </summary>
        [TestMethod]
        [DataRow(1, 268_435_448u, 4_563_402_636L)]
        [DataRow(1, 268_435_449u, 4_563_402_653L)]
        [DataRow(4, 300_000_000u, 6_000_000_020L)]
        [DataRow(1, 536_870_912u, 9_126_805_524L)]
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

            var counts = 8L * cells;
            if (counts > PayloadWriter.LongestRun)
            {
                AssertRunRefusedWhileClaiming(table, counts, (table, "cells", cells));
            }
            else
            {
                AssertRefusedWhileClaiming(table, payload, (table, "cells", cells));
            }
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

            AssertRefusedWhileClaiming(sketch, payload, (sketch, "m", Array.MaxLength));
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


























        };

        private const string NoBody =
            "not yet an IPayloadBody, so its WriteTo writes without counting first (#216)";

        /// <summary>
        /// The structures whose payload is one length whatever they hold, so that their
        /// states cannot reach two. Each is checked: its states must write one length, or
        /// its line here is stale, and must differ in what they write, or nothing they hold
        /// was varied.
        /// </summary>
        private static readonly (StructureId Id, string Why)[] OneLength =
        {
            (StructureId.SimHashSignature, "a fingerprint, one ulong whatever it was built from"),
        };

        /// <summary>
        /// Every structure with a body is counted by its own <c>WriteTo</c>, once, before it
        /// writes, at exactly the length of the payload it then writes, in every state its
        /// test class gives, at the version that state says it writes. A null stream is
        /// refused before anything is counted.
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
            var payloads = new Dictionary<StructureId, HashSet<string>>();
            var states = 0;

            foreach (var state in AllStates())
            {
                var counts = new List<(IPayloadBody Body, long Length)>();
                byte[] bytes;
                ArgumentNullException refused;
                PersistenceFormat.Counted = (body, length) => counts.Add((body, length));
                try
                {
                    refused = Assert.ThrowsExactly<ArgumentNullException>(() => state.WriteTo(null));
                    Assert.IsEmpty(counts, $"{state.Name}: counted before the stream was checked");
                    bytes = state.Write();
                }
                finally
                {
                    PersistenceFormat.Counted = null;
                }

                var id = (StructureId)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6));
                var name = $"{id}, {state.Name}";
                Assert.AreEqual("stream", refused.ParamName, $"{name}: a null stream refused by another name");
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
                if (!payloads.TryGetValue(id, out var held))
                {
                    payloads[id] = held = new HashSet<string>(StringComparer.Ordinal);
                }

                held.Add(Convert.ToHexString(bytes));
                states++;
            }

            Assert.IsGreaterThan(0, states, "no states were found, so nothing was counted");

            // A structure whose states all write one length could be counted by a constant,
            // unless its layout is one length: then its states differ in what they hold.
            foreach (var (id, seen) in lengths)
            {
                var oneLength = OneLength.Where(o => o.Id == id).Select(o => o.Why).SingleOrDefault();
                if (oneLength is null)
                {
                    Assert.IsGreaterThan(1, seen.Count,
                        $"every state of {id} writes {seen.First():N0} bytes, so nothing the count " +
                        "depends on was varied");
                    continue;
                }

                Assert.HasCount(1, seen,
                    $"{id} is exempt from two lengths as {oneLength}, and its states write " +
                    $"{seen.Count}: delete its line in OneLength");
                Assert.IsGreaterThan(1, payloads[id].Count,
                    $"{id} is exempt from two lengths as {oneLength}, and its states all write " +
                    "the same bytes, so nothing they hold was varied");
            }

            foreach (var (id, why) in OneLength)
            {
                Assert.IsTrue(lengths.ContainsKey(id),
                    $"{id} is exempt from two lengths as {why}, and gives no states");
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
        /// Every write a body can make, given to a counter and to a writer that stores: the
        /// counter's length is the stored payload's, its own buffer holds nothing, and the
        /// runs and blocks begun on it are counted there and not asked to be filled. The
        /// bulk writes are given more than the counter's scratch holds, so one that copied
        /// into it would throw.
        /// </summary>
        [TestMethod]
        public void TestACounterCountsWhatTheSameWritesStore()
        {
            var bytes = new byte[100_000];
            var counter = PayloadWriter.Counter();
            var writer = new PayloadWriter();
            var filled = new List<bool>();

            foreach (var payload in new[] { counter, writer })
            {
                payload.WriteByte(1);
                payload.WriteUInt16(2);
                payload.WriteUInt32(3);
                payload.WriteUInt64(4);
                payload.WriteInt64(-5);
                payload.WriteDouble(6.5);
                payload.WriteBytes(bytes);
                payload.WriteRaw(bytes);

                var run = payload.BeginRun(2 * sizeof(ulong));
                if (run)
                {
                    payload.WriteUInt64(7);
                    payload.WriteUInt64(8);
                }

                var block = payload.BeginBlock(sizeof(long));
                if (block)
                {
                    payload.WriteInt64(9);
                }

                filled.Add(run);
                filled.Add(block);
            }

            Assert.IsTrue(counter.IsCounting);
            Assert.IsFalse(writer.IsCounting);
            CollectionAssert.AreEqual(new[] { false, false, true, true }, filled,
                "a counter is to count runs and blocks, and a writer to fill them");
            Assert.AreEqual(1 + 2 + 4 + 8 + 8 + 8 + (4 + 100_000) + 100_000 + (4 + 16) + 8,
                writer.WrittenSpan.Length);
            Assert.AreEqual(writer.WrittenSpan.Length, writer.Length);
            Assert.AreEqual(writer.Length, counter.Length, "the counter counted another payload");
            Assert.AreEqual(0, counter.WrittenSpan.Length, "a counter stored something");
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
        /// Has the structure claim a shape whose payload is past what one array holds,
        /// asserts its <c>ToByteArray</c> refuses it, and puts the shape back.
        /// </summary>
        /// <param name="structure">The structure written.</param>
        /// <param name="payload">The size the refusal must name.</param>
        /// <param name="shape">
        /// The members that size its table, by name, each on the object that holds it --
        /// the structure, or a structure inside it -- with the value it is to claim.
        /// </param>
        /// <remarks>
        /// <c>ToByteArray</c>, because a payload past one array is written by <c>WriteTo</c>
        /// in the large form, streamed (#225), and a byte array is the one thing that cannot
        /// hold it (#229). <c>WriteTo</c> of a claimed shape is not tried: it would set about
        /// writing a payload of the size claimed. A shape with one run past the longest a
        /// payload holds is refused for that instead, by both, and is
        /// <see cref="AssertRunRefusedWhileClaiming"/>'s.
        /// </remarks>
        internal static void AssertRefusedWhileClaiming<T>(
            T structure, long payload, params (object Owner, string Member, object Claimed)[] shape)
            where T : IBinaryPersistable<T>
        {
            WhileClaiming(structure, shape, () =>
            {
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var ex = Assert.ThrowsExactly<InvalidOperationException>(
                    () => structure.ToByteArray(),
                    $"a {typeof(T).Name} whose payload would be {payload:N0} bytes was made a byte array");
                var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

                StringAssert.Contains(ex.Message, payload.ToString("N0"),
                    "the refusal should name the size the whole payload would be");
                StringAssert.Contains(ex.Message, LargestPayload.ToString("N0"),
                    "the refusal should name the size one can be");
                StringAssert.Contains(ex.Message, "WriteTo(Stream)",
                    "the refusal should point to the stream a payload this long is written to");
                Assert.IsLessThan(RefusalAllocates, allocated,
                    $"the refusal allocated {allocated:N0} bytes: the payload was built, or " +
                    "begun, before its size was checked");
            });
        }

        /// <summary>
        /// Has the structure claim a shape with a run longer than the longest a payload
        /// holds, asserts both its <c>WriteTo</c> and its <c>ToByteArray</c> refuse it for
        /// that run, before anything is written or built, and puts the shape back.
        /// </summary>
        /// <param name="structure">The structure written.</param>
        /// <param name="run">The run's length, which the refusal must name.</param>
        /// <param name="shape">As <see cref="AssertRefusedWhileClaiming"/> takes it.</param>
        internal static void AssertRunRefusedWhileClaiming<T>(
            T structure, long run, params (object Owner, string Member, object Claimed)[] shape)
            where T : IBinaryPersistable<T>
        {
            Assert.IsGreaterThan(PayloadWriter.LongestRun, run, "the row's run is not past the longest");

            WhileClaiming(structure, shape, () =>
            {
                using var stream = new MemoryStream();
                foreach (var (how, write) in new (string, Action)[]
                {
                    ("WriteTo", () => structure.WriteTo(stream)),
                    ("ToByteArray", () => structure.ToByteArray()),
                })
                {
                    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    var ex = Assert.ThrowsExactly<InvalidOperationException>(write,
                        $"{how} of a {typeof(T).Name} holding a run of {run:N0} bytes was not refused");
                    var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

                    StringAssert.Contains(ex.Message, $"a run of {run:N0} bytes",
                        $"{how}'s refusal should name the run: {ex.Message}");
                    StringAssert.Contains(ex.Message, PayloadWriter.LongestRun.ToString("N0"),
                        $"{how}'s refusal should name the longest run: {ex.Message}");
                    Assert.AreEqual(0L, stream.Length, $"{how} wrote to the stream before refusing");
                    Assert.IsLessThan(RefusalAllocates, allocated,
                        $"{how}'s refusal allocated {allocated:N0} bytes: the payload was built, or " +
                        "begun, before its run was checked");
                }
            });
        }

        private static void WhileClaiming<T>(
            T structure, (object Owner, string Member, object Claimed)[] shape, Action refuse)
            where T : IBinaryPersistable<T>
        {
            var before = structure.ToByteArray();
            var members = shape
                .Select(s => (s.Owner, Member: s.Owner.GetType().GetMember(s.Member, Fields | BindingFlags.Public).Single()))
                .ToArray();
            var held = members.Select(m => Get(m.Member, m.Owner)).ToArray();

            for (var i = 0; i < shape.Length; i++) Set(members[i].Member, members[i].Owner, shape[i].Claimed);

            try
            {
                refuse();
            }
            finally
            {
                for (var i = 0; i < shape.Length; i++) Set(members[i].Member, members[i].Owner, held[i]);
            }

            CollectionAssert.AreEqual(before, structure.ToByteArray(),
                "the structure writes differently after a refused write");
        }

        private static object Get(MemberInfo member, object owner) =>
            member is FieldInfo field ? field.GetValue(owner) : ((PropertyInfo)member).GetValue(owner);

        private static void Set(MemberInfo member, object owner, object value)
        {
            if (member is FieldInfo field)
            {
                field.SetValue(owner, value);
            }
            else
            {
                ((PropertyInfo)member).SetValue(owner, value);
            }
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
    internal sealed record PayloadState(
        string Name, IPayloadBody Structure, ushort Version, Func<byte[]> Write, Action<Stream> WriteTo)
    {
        internal static PayloadState Of<T>(
            string name, T structure, ushort version = PersistenceFormat.DefaultVersion)
            where T : IBinaryPersistable<T>, IPayloadBody =>
            new(name, structure, version, () => structure.ToByteArray(), stream => structure.WriteTo(stream));
    }
}
