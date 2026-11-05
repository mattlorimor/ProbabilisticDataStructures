using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The large form of the envelope written as its payload is produced (#225), runs held
    /// to what a reader takes, and a byte array refused for a payload past one (#229).
    /// </summary>
    /// <remarks>
    /// No test can build a payload past 2 GiB, so the large form is reached below it through
    /// the threshold the writer reads (<see cref="LargeForm"/>), and what shows that it is
    /// streamed rather than held is what a held writer could not do: the bytes reach the
    /// stream while the body is still writing, in pieces no longer than the buffer, with
    /// nothing the size of the payload allocated. The writer streams only the large form,
    /// so each of these holds the ordinary envelope to the opposite, and the stored
    /// fixtures hold it to its bytes.
    /// </remarks>
    [TestClass]
    public class TestLargeEnvelopeWriting
    {
        /// <summary>
        /// The buffers the streamed payloads are cut by: the widest field and sizes that are
        /// not a multiple of it, so pieces end at every offset a field can, and the default.
        /// </summary>
        private static readonly int?[] Buffers = { 8, 9, 13, 64, 1000, null };

        /// <summary>
        /// The most a refusal before anything is built may allocate: the count, the
        /// exception and its message, probed at 1,848 bytes for a run refused in the ordinary
        /// envelope, 2,464 in the large form, and 3,304 to 4,320 for <c>ToByteArray</c>
        /// called through reflection. This is fifteen times the most of them, and the
        /// payloads refused are a megabyte and more.
        /// </summary>
        private const long RefusalAllocates = 64 * 1024;

        /// <summary>
        /// What a large-form write allocates besides its buffer: the counter, the streaming
        /// writer and the running checksum, probed at 240 bytes for every payload measured,
        /// where the ordinary envelope allocated its payload twice and 266 to 270 bytes more.
        /// This is seventeen times that, and the payloads it is measured against are more
        /// than thirty times the buffer and it together.
        /// </summary>
        private const long LargeWriteOverhead = 4096;

        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        /// <summary>
        /// Every structure, written in the large form through buffers of every size above,
        /// is the large form of its ordinary payload, as FORMAT.md lays it out with each
        /// checksum taken in one call over what it covers. The roster is
        /// <see cref="StructureId"/>, through the shared sample payloads.
        /// </summary>
        /// <remarks>
        /// The threshold is a byte under each payload, so the structure is written in the
        /// large form and anything nested in it, being shorter, in the ordinary one, and the
        /// payload is then the ordinary envelope's with the length of each of its runs a
        /// <c>u64</c> (#228), as <see cref="LargeForm.Widened"/> works it out from FORMAT.md.
        /// With the threshold at nothing the
        /// nested envelopes are streamed too: every buffer must give the same bytes there,
        /// and they must read back. A buffer of eight bytes cuts a sample into hundreds of
        /// pieces, so a piece left out of the running checksum, or written out of turn, is
        /// in every payload. The sweep requires that payloads were written in more than one
        /// piece and that runs longer than the buffer were written as they are.
        /// </remarks>
        [TestMethod]
        public void TestEveryStructureStreamedInPiecesIsTheLargeFormOfItsOrdinaryPayload()
        {
            var swept = new List<StructureId>();
            var readers = LargeForm.Readers();
            var pieced = 0;
            var direct = 0;

            foreach (var (id, read, bytes) in TestPersistenceAllStructures.SamplePayloads())
            {
                var structure = read(bytes);
                var ordinary = LargeForm.Write(structure);
                var payload = ordinary[14..^4];
                var expected = LargeForm.Enveloped(ordinary, LargeForm.Widened(ordinary, readers, nestedLarge: false, out _, out _));
                byte[] atNothing = null;

                foreach (var size in Buffers)
                {
                    var recording = new RecordingStream();
                    WithBuffer(size, () => LargeForm.Below(payload.Length - 1, () => WriteTo(structure, recording)));
                    CollectionAssert.AreEqual(expected, recording.ToArray(),
                        $"{id} streamed through a buffer of {size?.ToString() ?? "the default"} is not the large form of its payload");

                    var writes = recording.Writes;
                    Assert.AreEqual(PersistenceFormat.LargeHeaderLength, writes[0], $"{id}: the header was not written first, whole");
                    Assert.AreEqual(4, writes[^1], $"{id}: the checksum was not written last, alone");
                    Assert.DoesNotContain(0, writes, $"{id}: an empty write was made");
                    if (writes.Count > 3)
                    {
                        pieced++;
                    }

                    if (size is int buffer && writes.Skip(1).SkipLast(1).Any(w => w > buffer))
                    {
                        direct++;
                    }

                    var nothing = WithBuffer(size, () => LargeForm.WriteLarge(structure));
                    atNothing ??= nothing;
                    CollectionAssert.AreEqual(atNothing, nothing,
                        $"{id} with every envelope streamed writes other bytes through a buffer of {size?.ToString() ?? "the default"}");
                }

                CollectionAssert.AreEqual(ordinary, LargeForm.Write(read(atNothing)),
                    $"{id} with every envelope streamed does not read back as the structure that wrote it");
                swept.Add(id);
            }

            Assert.IsGreaterThan(0, pieced, "no payload was written in more than one piece");
            Assert.IsGreaterThan(0, direct, "no run longer than the buffer was written as it is");
            StructureRoster.AssertCoversEveryStructure("streamed large form", swept);
        }

        /// <summary>
        /// A large-form write allocates its buffer and nothing the size of its payload, for a
        /// payload that is one run and for one written a field at a time; the ordinary
        /// envelope, on the same structures and the same measurement, allocates the payload
        /// twice, as it always has.
        /// </summary>
        /// <remarks>
        /// Measured as the least of five, each in a no-GC region, since a background
        /// collection running beside a write adds kilobytes to its count (TESTING.md, on the
        /// read budget; <see cref="NoGCRegion"/>). Each payload
        /// is more than sixteen times the bound, so a copy of it could not hide in the slack;
        /// and the large write must be seen allocating its buffer, so a measurement that sees
        /// nothing cannot pass.
        /// </remarks>
        [TestMethod]
        public void TestALargeWriteAllocatesItsBufferAndNotItsPayload()
        {
            var filter = new BloomFilter(2_000_000, 0.01);
            for (var i = 0; i < 1000; i++) filter.Add(Key($"a{i}"));
            var sketch = new CountMinSketch(0.00005, 0.01);
            for (var i = 0; i < 1000; i++) sketch.Add(Key($"a{i}"));

            foreach (var (name, write, payload) in new (string, Action<Stream>, long)[]
            {
                ("a Bloom filter, one run", filter.WriteTo, filter.ToByteArray().Length - 18L),
                ("a count-min sketch, a field a cell", sketch.WriteTo, sketch.ToByteArray().Length - 18L),
            })
            {
                Assert.AreEqual(0, GC.GetGeneration(new byte[PayloadWriter.StreamBufferLength]),
                    "a buffer of that length goes on the large object heap");
                var bound = PayloadWriter.StreamBufferLength + LargeWriteOverhead;
                Assert.IsGreaterThan(16 * bound, payload, $"{name}'s payload is too small for the bound to tell");

                var ordinary = LeastAllocated(() => write(Stream.Null));
                var large = LargeForm.Below(0, () => LeastAllocated(() => write(Stream.Null)));

                Assert.IsGreaterThanOrEqualTo(2 * payload, ordinary,
                    $"{name} in the ordinary envelope allocated {ordinary:N0} bytes, less than its payload twice");
                Assert.IsGreaterThanOrEqualTo((long)PayloadWriter.StreamBufferLength, large,
                    $"{name} in the large form was not seen allocating its buffer: {large:N0} bytes");
                Assert.IsLessThan(bound, large,
                    $"{name} in the large form allocated {large:N0} bytes against a payload of {payload:N0}");
            }
        }

        /// <summary>
        /// In the large form the bytes reach the stream while the body is still writing, the
        /// header first; in the ordinary envelope nothing does until the body has finished,
        /// so a write refused there leaves nothing.
        /// </summary>
        /// <remarks>
        /// The body looks at the stream as it returns, the last thing it does. A writer that
        /// held the payload, or held it to the end, has written nothing at that point; one
        /// that streams has written all but what its buffer still holds, which is a small
        /// part of 800,000 bytes, so it must have written most of them.
        /// </remarks>
        [TestMethod]
        public void TestTheLargeFormReachesTheStreamWhileTheBodyIsWriting()
        {
            const int fields = 100_000;
            var payload = 8L * fields;

            var streamed = new RecordingStream();
            var large = new Paced(streamed, fields);
            LargeForm.Below(0, () =>
            {
                PersistenceFormat.Write(streamed, large);
                return 0;
            });

            Assert.IsGreaterThan(PersistenceFormat.LargeHeaderLength + payload / 2, large.SeenAtTheEnd,
                $"the body had written {payload:N0} bytes and the stream held {large.SeenAtTheEnd:N0}");
            Assert.AreEqual(PersistenceFormat.LargeHeaderLength, streamed.Writes[0], "the header was not written first");
            Assert.AreEqual(PersistenceFormat.LargeEnvelopeLength + payload, streamed.Written);

            var held = new RecordingStream();
            var ordinary = new Paced(held, fields);
            PersistenceFormat.Write(held, ordinary);

            Assert.AreEqual(0L, ordinary.SeenAtTheEnd, "an ordinary payload reached the stream before its body had finished");
            Assert.HasCount(1, held.Writes, "an ordinary envelope was not written in one call");
            Assert.AreEqual(PersistenceFormat.EnvelopeLength + payload, held.Written);
        }

        /// <summary>
        /// A real structure in the large form goes to the stream in pieces no longer than the
        /// buffer: a count-min sketch, whose cells are written a field at a time, so that every
        /// piece is the buffer's and none is a run written as it is. It reads back.
        /// </summary>
        [TestMethod]
        public void TestACountMinSketchInTheLargeFormIsWrittenABufferAtATime()
        {
            var sketch = new CountMinSketch(0.0001, 0.01);
            for (var i = 0; i < 1000; i++) sketch.Add(Key($"b{i}"));
            var ordinary = sketch.ToByteArray();
            var payload = ordinary.Length - 18L;

            var recording = new RecordingStream();
            LargeForm.Below(0, () =>
            {
                sketch.WriteTo(recording);
                return 0;
            });

            var pieces = recording.Writes.Skip(1).SkipLast(1).ToArray();
            Assert.IsGreaterThanOrEqualTo(payload / PayloadWriter.StreamBufferLength, (long)pieces.Length,
                $"a payload of {payload:N0} bytes was written in {pieces.Length} pieces");
            Assert.IsTrue(pieces.All(p => p > 0 && p <= PayloadWriter.StreamBufferLength),
                $"a piece was empty or longer than the buffer: {string.Join(", ", pieces.Distinct())}");
            Assert.AreEqual(payload, pieces.Sum(p => (long)p));
            CollectionAssert.AreEqual(ordinary, Persistence.FromByteArray<CountMinSketch>(recording.ToArray()).ToByteArray(),
                "the sketch written a buffer at a time does not read back as itself");
        }

        /// <summary>
        /// Past what one array holds, at the threshold that ships, <c>WriteTo</c> writes the
        /// large form rather than refusing: its header goes out declaring the whole length as a
        /// <c>u64</c>, before the body runs. A count sketch is made to claim a table of
        /// 268,435,446 cells, a payload of 2,147,483,580 bytes in the ordinary envelope, seven
        /// past its ceiling, and four more in the large form, where the cells' run has a
        /// <c>u64</c> length (#228); its body writes the cells it really holds, so the write
        /// ends short of the count and is refused for that, with the header in the stream. No
        /// payload that long is built.
        /// </summary>
        [TestMethod]
        public void TestAWriteToPastOneArrayIsStreamedInTheLargeFormAtTheRealThreshold()
        {
            const uint width = 268_435_446;
            const long ordinaryLength = 12 + 8L * width;
            const long claimed = ordinaryLength + 4;
            Assert.IsGreaterThan((long)PersistenceFormat.MaxPayloadLength, ordinaryLength);

            var sketch = new CountSketch(0.5, 0.5);
            for (var i = 0; i < 40; i++) sketch.Add(Key($"c{i}"), i % 5 - 2);
            var before = sketch.ToByteArray();

            // What the body writes in the large form: the cells it holds, their run's length a u64.
            var real = before.Length - 18L + 4;

            var fields = typeof(CountSketch);
            var widthField = fields.GetField("width", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var depthField = fields.GetField("depth", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var heldWidth = widthField.GetValue(sketch)!;
            var heldDepth = depthField.GetValue(sketch)!;
            widthField.SetValue(sketch, width);
            depthField.SetValue(sketch, 1u);

            byte[] left;
            try
            {
                using var stream = new MemoryStream();
                var ex = Assert.ThrowsExactly<InvalidOperationException>(() => sketch.WriteTo(stream));
                StringAssert.Contains(ex.Message, $"counted a payload of {claimed:N0} bytes",
                    $"a payload past one array was refused before it was written: {ex.Message}");
                StringAssert.Contains(ex.Message, $"wrote {real:N0} bytes", ex.Message);
                left = stream.ToArray();
            }
            finally
            {
                widthField.SetValue(sketch, heldWidth);
                depthField.SetValue(sketch, heldDepth);
            }

            Assert.HasCount(PersistenceFormat.LargeHeaderLength, left, "more than the header reached the stream");
            Assert.AreEqual(BinaryPrimitives.ReadUInt16LittleEndian(before.AsSpan(4)) | 0x8000,
                (int)BinaryPrimitives.ReadUInt16LittleEndian(left.AsSpan(4)), "the header does not flag the version");
            Assert.AreEqual((ushort)StructureId.CountSketch, BinaryPrimitives.ReadUInt16LittleEndian(left.AsSpan(6)));
            Assert.AreEqual((ulong)claimed, BinaryPrimitives.ReadUInt64LittleEndian(left.AsSpan(10)), "the header does not declare the count");
            Assert.AreEqual(Crc32.HashToUInt32(left.AsSpan(4, 14)), BinaryPrimitives.ReadUInt32LittleEndian(left.AsSpan(18)),
                "the header's own checksum is not over its fields");
            Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<CountSketch>(left));
            CollectionAssert.AreEqual(before, sketch.ToByteArray(), "the sketch writes differently after the refused write");
        }

        /// <summary>
        /// Every public <c>ToByteArray</c> refuses a payload past one array before writing
        /// anything, with an <c>InvalidOperationException</c> that names the payload's size,
        /// counted, and the most one array holds, and points to the <c>Stream</c> overload.
        /// The roster is the library's public surface, by reflection: a <c>ToByteArray</c>
        /// this test does not know how to call fails it by name.
        /// </summary>
        /// <remarks>
        /// A count-min sketch is made to claim 268,435,443 cells in one row, a payload of
        /// 2,147,483,577 bytes, four past the ceiling, holding no run at all, so the refusal
        /// can only be the array's. Nothing is built for it: the refusal allocates less than
        /// <see cref="RefusalAllocates"/>, and the sketch writes as before once its shape is
        /// put back. A type outside the library, which has no body to count, is written
        /// through its own <c>WriteTo</c> as it always was.
        /// </remarks>
        [TestMethod]
        public void TestEveryToByteArrayRefusesAPayloadPastOneArrayBeforeWritingIt()
        {
            const uint width = 268_435_443;
            const long claimed = 33 + 8L * width;
            Assert.IsGreaterThan((long)PersistenceFormat.MaxPayloadLength, claimed);

            var found = typeof(BloomFilter).Assembly.GetExportedTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .Where(m => m.Name == "ToByteArray")
                .ToArray();
            Assert.IsNotEmpty(found, "the library has no ToByteArray, so nothing was swept");

            foreach (var method in found)
            {
                Assert.IsTrue(method.IsStatic && method.IsGenericMethodDefinition && method.GetParameters().Length == 1,
                    $"{method.DeclaringType!.Name}.{method} is a ToByteArray this test does not know how to call: cover it");
                var toByteArray = method.MakeGenericMethod(typeof(CountMinSketch));
                var where = $"{method.DeclaringType!.Name}.{method.Name}";

                var sketch = new CountMinSketch(0.5, 0.5);
                for (var i = 0; i < 40; i++) sketch.Add(Key($"d{i}"));
                var before = sketch.ToByteArray();
                var shape = typeof(CountMinSketch);
                var widthMember = shape.GetProperty("Width", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;
                var depthMember = shape.GetProperty("Depth", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;
                var heldWidth = widthMember.GetValue(sketch)!;
                var heldDepth = depthMember.GetValue(sketch)!;
                widthMember.SetValue(sketch, width);
                depthMember.SetValue(sketch, 1u);

                try
                {
                    var arguments = new object[] { sketch };
                    TargetInvocationException thrown = null;
                    var allocated = NoGCRegion.Allocated(() => thrown = Assert.ThrowsExactly<TargetInvocationException>(
                        () => toByteArray.Invoke(null, arguments), $"{where} of a payload past one array was not refused"));
                    var ex = Assert.IsInstanceOfType<InvalidOperationException>(thrown.InnerException,
                        $"{where} refused a payload past one array with something else: {thrown.InnerException}");

                    StringAssert.Contains(ex.Message, claimed.ToString("N0"), $"{where} did not name the payload's size: {ex.Message}");
                    StringAssert.Contains(ex.Message, PersistenceFormat.MaxPayloadLength.ToString("N0"),
                        $"{where} did not name the most one array holds: {ex.Message}");
                    StringAssert.Contains(ex.Message, "WriteTo(Stream)", $"{where} did not point to the Stream overload: {ex.Message}");
                    Assert.IsLessThan(RefusalAllocates, allocated,
                        $"{where} allocated {allocated:N0} bytes refusing: the payload was built, or begun, first");
                }
                finally
                {
                    widthMember.SetValue(sketch, heldWidth);
                    depthMember.SetValue(sketch, heldDepth);
                }

                CollectionAssert.AreEqual(before, (byte[])toByteArray.Invoke(null, new object[] { sketch })!,
                    $"{where}: the sketch writes differently after the refusal");
                CollectionAssert.AreEqual(new byte[] { 7 },
                    (byte[])method.MakeGenericMethod(typeof(Outsider)).Invoke(null, new object[] { new Outsider() })!,
                    $"{where} of a type outside the library is not what its WriteTo writes");
            }
        }

        /// <summary>
        /// The longest run a payload holds is <see cref="Array.MaxLength"/> bytes, unless
        /// lowered, and lowered only on the thread that lowered it. A reader of a run refuses
        /// one past it as more than one array, and goes past that check at it: the writer's
        /// bound is the reader's.
        /// </summary>
        /// <remarks>
        /// The reader's half is held on a Bloom filter, whose bit array is a run read by
        /// <see cref="PayloadReader.ReadBytes"/>, with its length prefix rewritten and the
        /// checksum repaired, so the length is the only thing wrong. A run is read into one
        /// array, so the bound is the largest array, not the largest <c>int</c>: a run
        /// between them passed the reader's check and then could not be allocated.
        /// </remarks>
        [TestMethod]
        public void TestTheLongestRunIsTheLongestAReaderTakes()
        {
            Assert.IsNull(PayloadWriter.LoweredLongestRun);
            Assert.AreEqual((long)Array.MaxLength, PayloadWriter.LongestRun);

            WithLongestRun(5, () =>
            {
                Assert.AreEqual(5L, PayloadWriter.LongestRun);
                long elsewhere = -1;
                var other = new Thread(() => elsewhere = PayloadWriter.LongestRun);
                other.Start();
                other.Join();
                Assert.AreEqual((long)Array.MaxLength, elsewhere, "a bound lowered on one thread was seen on another");
                return 0;
            });
            Assert.AreEqual((long)Array.MaxLength, PayloadWriter.LongestRun, "the bound was not put back");

            var bytes = new BloomFilter(100, 0.01).ToByteArray();
            InvalidDataException Claiming(uint length)
            {
                var claimed = (byte[])bytes.Clone();
                BinaryPrimitives.WriteUInt32LittleEndian(claimed.AsSpan(14 + 22), length);
                BinaryPrimitives.WriteUInt32LittleEndian(claimed.AsSpan(claimed.Length - 4), Crc32.HashToUInt32(claimed.AsSpan(4, claimed.Length - 8)));
                return Assert.ThrowsExactly<InvalidDataException>(
                    () => Persistence.FromByteArray<BloomFilter>(claimed, ReadOptions.Unlimited));
            }

            StringAssert.Contains(Claiming((uint)Array.MaxLength + 1).Message, "single array");
            Assert.DoesNotContain("single array", Claiming((uint)Array.MaxLength).Message, "a run of the longest was refused as too long");
        }

        /// <summary>
        /// In the ordinary envelope, a run longer than the longest is refused by the count,
        /// naming its length and the bound, before anything is written or built, by
        /// <c>WriteTo</c> and by <c>ToByteArray</c>; a run of exactly the longest is written,
        /// and reads back. In the large form, whose runs carry a <c>u64</c> length (#228), the
        /// bound does not hold: the run a byte past it is written, by both, and reads back.
        /// </summary>
        /// <remarks>
        /// A Bloom filter's payload holds one run, its bit array. The bound straddles it by a
        /// byte. The bound is the most a reader takes into one array, and the ordinary
        /// envelope's payload is held in one; the large form is read from its stream, and in
        /// it a run is bounded by what its reader can price and its structure can hold. The
        /// filter is large enough that building its payload before refusing it would pass
        /// <see cref="RefusalAllocates"/> many times over.
        /// </remarks>
        [TestMethod]
        public void TestARunPastTheLongestIsRefusedBeforeAnythingIsWritten()
        {
            var filter = new BloomFilter(1_000_000, 0.01);
            for (var i = 0; i < 100; i++) filter.Add(Key($"e{i}"));
            var ordinary = filter.ToByteArray();
            long run = filter.Buckets.RawData.Length;
            Assert.IsGreaterThan(16 * RefusalAllocates, run);

            foreach (var threshold in new long?[] { null, 0 })
            {
                var form = threshold is null ? "the ordinary envelope" : "the large form";

                var written = WithLongestRun(run, () => Under(threshold, () => Write(filter)));
                Assert.AreEqual(threshold is not null, LargeForm.IsLarge(written), $"{form} was not the form written");
                CollectionAssert.AreEqual(ordinary, Persistence.FromByteArray<BloomFilter>(written).ToByteArray(),
                    $"in {form}, a run of exactly the longest does not read back");
            }

            var larges = WithLongestRun(run - 1, () =>
            {
                // A stream of its own for each attempt at the measurement, so that what one
                // attempt wrote is not what the next is held to.
                MemoryStream stream = null;
                InvalidOperationException ex = null;
                var allocated = NoGCRegion.Allocated(
                    () => ex = Assert.ThrowsExactly<InvalidOperationException>(() => filter.WriteTo(stream),
                        "in the ordinary envelope, a run a byte past the longest was written"),
                    first: () => stream = new MemoryStream());
                StringAssert.Contains(ex.Message, run.ToString("N0"), $"the refusal did not name the run: {ex.Message}");
                StringAssert.Contains(ex.Message, (run - 1).ToString("N0"), $"the refusal did not name the bound: {ex.Message}");
                Assert.AreEqual(0L, stream.Length, "a run past the longest was refused after writing began");
                Assert.IsLessThan(RefusalAllocates, allocated, $"the refusal allocated {allocated:N0} bytes");

                var array = Assert.ThrowsExactly<InvalidOperationException>(() => filter.ToByteArray(),
                    "in the ordinary envelope, ToByteArray wrote a run a byte past the longest");
                StringAssert.Contains(array.Message, run.ToString("N0"));

                return new[] { Under(0, () => Write(filter)), Under(0, () => filter.ToByteArray()) };
            });

            foreach (var large in larges)
            {
                Assert.IsTrue(LargeForm.IsLarge(large), "the filter under a threshold of nothing was not written in the large form");
                CollectionAssert.AreEqual(ordinary, Persistence.FromByteArray<BloomFilter>(large).ToByteArray(),
                    "in the large form, a run a byte past the ordinary envelope's longest does not read back");
            }

            CollectionAssert.AreEqual(ordinary, filter.ToByteArray(), "the filter writes differently after the refusals");
        }

        /// <summary>
        /// A structure held inside another is a run of its whole envelope. In the ordinary
        /// envelope it is held to the longest run in the count, before anything is written: a
        /// top-k whose count-min sketch's envelope is a byte past the bound is refused naming
        /// that envelope's length, with nothing in the stream, while the sketch on its own,
        /// which holds no run, is written under the same bound. In the large form, whose runs
        /// carry a <c>u64</c> length (#228), the same top-k is written, and reads back.
        /// </summary>
        /// <remarks>
        /// The count of a nested structure is the count of its own envelope and payload, which
        /// is then counted as one run, so this is the check that sees it: the nested
        /// structure's own payload holds no run.
        /// </remarks>
        [TestMethod]
        public void TestANestedStructurePastTheLongestRunIsRefusedInTheCount()
        {
            var topK = new TopK(0.001, 0.01, 3);
            foreach (var element in new[] { "a", "bb", "ccc", "a" }) topK.Add(Key(element));
            var sketch = (CountMinSketch)typeof(TopK).GetProperty("Cms", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(topK)!;
            var expected = Write(topK);

            foreach (var threshold in new long?[] { null, 0 })
            {
                var form = threshold is null ? "the ordinary envelope" : "the large form";
                var nested = (long)Under(threshold, () => Write(sketch)).Length;

                Assert.IsNotNull(WithLongestRun(nested, () => Under(threshold, () => Write(topK))),
                    $"in {form}, a top-k whose sketch's envelope is exactly the longest run was not written");

                WithLongestRun(nested - 1, () => Under(threshold, () =>
                {
                    if (threshold is not null)
                    {
                        var large = Write(topK);
                        Assert.IsTrue(LargeForm.IsLarge(large), "the top-k under a threshold of nothing was not in the large form");
                        Assert.AreEqual(1, LargeForm.NestedLarge(large), "the top-k's sketch was not in the large form inside it");
                        CollectionAssert.AreEqual(large, Write(Persistence.FromByteArray<TopK>(large)),
                            "in the large form, a top-k whose sketch's envelope is a byte past the longest run does not read back");
                        return 0;
                    }

                    using var stream = new MemoryStream();
                    var ex = Assert.ThrowsExactly<InvalidOperationException>(() => topK.WriteTo(stream),
                        $"in {form}, a top-k whose sketch's envelope is a byte past the longest run was written");
                    StringAssert.Contains(ex.Message, nested.ToString("N0"), $"in {form}: {ex.Message}");
                    Assert.AreEqual(0L, stream.Length, $"in {form}, the nested run was refused after writing began");

                    Assert.IsNotNull(Write(sketch), $"in {form}, the sketch alone, which holds no run, was refused");
                    return 0;
                }));
            }
        }

        /// <summary>
        /// A structure nested in one written in the large form is written straight into the
        /// outer stream, a buffer at a time, with nothing its size held: a top-k whose
        /// count-min sketch's envelope is about two megabytes allocates its streaming buffer and
        /// little else, with the sketch's envelope in either form, and no write to the stream
        /// is longer than the buffer. What it writes reads back.
        /// </summary>
        /// <remarks>
        /// A nested envelope was written to a <see cref="MemoryStream"/> of its own, copied out
        /// with <c>ToArray</c>, and appended to the outer payload as one run, so the large form
        /// held it twice over, and wrote it in one piece; past one array, which a run in the
        /// large form can be (#228), no <see cref="MemoryStream"/> holds it at all
        /// (<see cref="TestTheWindowWhereANestedEnvelopeOutgrewItsMemoryStreamIsWritten"/>).
        /// The sketch's envelope is in the large form with the threshold at nothing, and
        /// ordinary with it a byte under the top-k's payload. Measured as the least of five,
        /// and the envelope is more than sixteen times the bound.
        /// </remarks>
        [TestMethod]
        public void TestANestedEnvelopeInTheLargeFormIsWrittenStraightIntoTheOuterStream()
        {
            var topK = new TopK(0.00005, 0.01, 10);
            for (var i = 0; i < 1000; i++) topK.Add(Key($"g{i % 50}"));
            var nested = (long)Cms(topK).ToByteArray().Length;
            var payload = topK.ToByteArray().Length - (long)PersistenceFormat.EnvelopeLength;
            var bound = PayloadWriter.StreamBufferLength + LargeWriteOverhead;
            Assert.IsGreaterThan(16L * bound, nested, "the sketch is too small for the bound to tell");
            var expected = Write(topK);

            foreach (var (threshold, form) in new[] { (0L, "in the large form"), (payload - 1, "ordinary") })
            {
                var allocated = LargeForm.Below(threshold, () => LeastAllocated(() => topK.WriteTo(Stream.Null)));
                Console.WriteLine($"[NESTED WRITE] sketch {form}: envelope {nested:N0}, top-k's write allocated {allocated:N0}");
                Assert.IsLessThan(bound, allocated,
                    $"a top-k whose sketch's envelope, {form}, is {nested:N0} bytes allocated {allocated:N0} writing the large form");

                var recording = new RecordingStream();
                LargeForm.Below(threshold, () => WriteTo(topK, recording));
                Assert.IsTrue(LargeForm.IsLarge(recording.ToArray()), $"the top-k with its sketch {form} was not in the large form");
                var longest = recording.Writes.Max();
                Assert.IsLessThanOrEqualTo(PayloadWriter.StreamBufferLength, longest,
                    $"with its sketch {form}, the top-k made a write of {longest:N0} bytes, longer than the buffer: the envelope went out whole");
                CollectionAssert.AreEqual(expected, Write(Persistence.FromByteArray<TopK>(recording.ToArray())),
                    $"the top-k with its sketch {form}, streamed, does not read back");
            }
        }

        /// <summary>
        /// The window #225 left, straddled at the threshold that ships: a top-k whose sketch's
        /// envelope was past one array and within <c>int.MaxValue</c> passed the count's run
        /// check, then failed in the <see cref="MemoryStream"/> that held the nested envelope,
        /// after the outer header had gone to the stream. Now the sketch's envelope is written
        /// into the outer stream: the large header declaring the count with <c>u64</c> runs,
        /// the top-k's fields, the run's <c>u64</c> length, and the sketch's own header,
        /// declaring its payload, all reach the stream, whichever side of either edge.
        /// </summary>
        /// <remarks>
        /// No payload that size is built. A count-min sketch is made to claim one row of the
        /// width given, a payload of 33 bytes and eight a cell; its body writes the cells it
        /// holds and then reads past them, throwing <see cref="IndexOutOfRangeException"/>,
        /// by which point everything asserted on has gone to the stream. A buffer of eight
        /// bytes sends each field on as it is written.
        /// <list type="bullet">
        /// <item>268,435,442 cells: a sketch payload of 2,147,483,569 bytes, which the ordinary
        /// envelope holds, so its envelope is 2,147,483,587 bytes, within one array.</item>
        /// <item>268,435,443: 2,147,483,577, in the large form, an envelope of 2,147,483,603,
        /// past one array: the window's lower edge.</item>
        /// <item>268,435,448: an envelope of 2,147,483,643, within <c>int.MaxValue</c>, and
        /// 268,435,449: 2,147,483,651, past it: the upper edge, where #225's count refused.</item>
        /// </list>
        /// The note on #228 gives the window as sketch payloads of 2,147,483,566 to
        /// 2,147,483,621 bytes. Below 2,147,483,574 the sketch's envelope is the ordinary one,
        /// eighteen bytes, which one array holds, so the window opens there.
        /// </remarks>
        [TestMethod]
        [DataRow(268_435_442u)]
        [DataRow(268_435_443u)]
        [DataRow(268_435_448u)]
        [DataRow(268_435_449u)]
        public void TestTheWindowWhereANestedEnvelopeOutgrewItsMemoryStreamIsWritten(uint width)
        {
            var elements = new[] { "a", "bb" };
            var topK = new TopK(0.5, 0.5, 3);
            foreach (var element in elements) topK.Add(Key(element));

            var sketchPayload = 33 + 8L * width;
            var nestedLarge = sketchPayload > PersistenceFormat.MaxPayloadLength;
            var envelope = nestedLarge ? PersistenceFormat.LargeEnvelopeLength : PersistenceFormat.EnvelopeLength;
            var run = envelope + sketchPayload;

            // K and N, the run's u64 length and the run, the heap's count, and each element's
            // u64 length, bytes and frequency.
            var counted = 8 + 8 + run + 4 + elements.Sum(e => 8 + e.Length + 8);
            Assert.IsGreaterThan((long)PersistenceFormat.MaxPayloadLength, counted, "the top-k is not past the ordinary envelope");

            var left = WrittenClaimingSketch(topK, width, 1);
            Assert.IsTrue(LargeForm.IsLarge(left), "the top-k's header is not the large form's");
            Assert.AreEqual((ulong)counted, BinaryPrimitives.ReadUInt64LittleEndian(left.AsSpan(10)), "the header does not declare the count");

            // The sketch's run begins after K and N, and its envelope after the run's length.
            var at = PersistenceFormat.LargeHeaderLength + 8;
            var nestedHeader = nestedLarge ? PersistenceFormat.LargeHeaderLength : 14;
            Assert.IsGreaterThan(at + 8 + nestedHeader, left.Length,
                $"the stream holds {left.Length} bytes, and the sketch's header ends at {at + 8 + nestedHeader}");
            Assert.AreEqual((ulong)run, BinaryPrimitives.ReadUInt64LittleEndian(left.AsSpan(at)), "the run's length is not the sketch's envelope's as a u64");

            var inner = left.AsSpan(at + 8);
            Assert.IsTrue(inner[..4].SequenceEqual(PersistenceFormat.Magic), "the sketch's envelope does not follow its run's length");
            Assert.AreEqual(nestedLarge, (BinaryPrimitives.ReadUInt16LittleEndian(inner[4..]) & PersistenceFormat.LargeFormFlag) != 0,
                "the sketch's envelope is not in the form its payload calls for");
            Assert.AreEqual((ushort)StructureId.CountMinSketch, BinaryPrimitives.ReadUInt16LittleEndian(inner[6..]));
            Assert.AreEqual((ulong)sketchPayload,
                nestedLarge ? BinaryPrimitives.ReadUInt64LittleEndian(inner[10..]) : BinaryPrimitives.ReadUInt32LittleEndian(inner[10..]),
                "the sketch's header does not declare its payload");
            if (nestedLarge)
            {
                Assert.AreEqual(Crc32.HashToUInt32(inner[4..18]), BinaryPrimitives.ReadUInt32LittleEndian(inner[18..]),
                    "the sketch's large header does not carry its own checksum");
            }
        }

        /// <summary>
        /// What a top-k's <c>WriteTo</c> sends to the stream, a field at a time, with its
        /// count-min sketch claiming <paramref name="depth"/> rows of <paramref name="width"/>
        /// cells, before the sketch's body reads past the cells it holds and throws
        /// <see cref="IndexOutOfRangeException"/>; the sketch's shape is then put back.
        /// </summary>
        internal static byte[] WrittenClaimingSketch(TopK topK, uint width, uint depth)
        {
            var sketch = Cms(topK);
            var shape = typeof(CountMinSketch);
            var widthMember = shape.GetProperty("Width", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;
            var depthMember = shape.GetProperty("Depth", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;
            var heldWidth = widthMember.GetValue(sketch)!;
            var heldDepth = depthMember.GetValue(sketch)!;
            widthMember.SetValue(sketch, width);
            depthMember.SetValue(sketch, depth);

            var recording = new RecordingStream();
            try
            {
                WithBuffer(8, () => Assert.ThrowsExactly<IndexOutOfRangeException>(() => topK.WriteTo(recording),
                    $"a top-k claiming a sketch of {depth} rows of {width:N0} cells was not refused, or did not reach the cells it does not hold"));
            }
            finally
            {
                widthMember.SetValue(sketch, heldWidth);
                depthMember.SetValue(sketch, heldDepth);
            }

            return recording.ToArray();
        }

        private static CountMinSketch Cms(TopK topK) =>
            (CountMinSketch)typeof(TopK).GetProperty("Cms", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(topK)!;

        /// <summary>
        /// A payload longer than a stream holds, whose length no envelope could declare, is
        /// refused by the count by name, with nothing written; one of exactly
        /// <c>long.MaxValue</c> bytes is counted, its header written declaring that, and the
        /// write goes on to the body.
        /// </summary>
        /// <remarks>
        /// The count is a <c>long</c>, so a payload past <c>long.MaxValue</c> would wrap to a
        /// length nothing could be framed under. No structure here reaches it; a body that
        /// counts two blocks does, and throws if it is ever asked to write.
        /// </remarks>
        [TestMethod]
        public void TestAPayloadPastWhatAStreamHoldsIsRefusedByTheCount()
        {
            foreach (var (first, second, field) in new[]
            {
                (long.MaxValue, 1L, false),
                (long.MaxValue, long.MaxValue, false),
                (1L, long.MaxValue, false),
                (long.MaxValue - 4, 8L, true),
            })
            {
                var what = field ? "an eight-byte field" : $"a block of {second}";
                using var stream = new MemoryStream();
                var ex = Assert.ThrowsExactly<InvalidOperationException>(
                    () => PersistenceFormat.Write(stream, new Vast(first, second, field)),
                    $"a payload of {first} bytes and {what} was not refused by its count");
                StringAssert.Contains(ex.Message, long.MaxValue.ToString("N0"), ex.Message);
                StringAssert.Contains(ex.Message, "more than a stream can hold", ex.Message);
                Assert.AreEqual(0L, stream.Length, "a payload no envelope can declare was written");
            }

            using var exactly = new MemoryStream();
            Assert.ThrowsExactly<NotSupportedException>(
                () => PersistenceFormat.Write(exactly, new Vast(long.MaxValue - 1, 1, false)),
                "a payload of exactly long.MaxValue bytes was not counted and written");
            var header = exactly.ToArray();
            Assert.HasCount(PersistenceFormat.LargeHeaderLength, header);
            Assert.AreEqual((ulong)long.MaxValue, BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(10)));
        }

        /// <summary>
        /// A body that writes other than it counted, in the large form, is refused where that
        /// is found: a write past the count at that write, which is not made, and a payload
        /// short of it or at another version once the body returns. The header and the pieces
        /// already written are left in the stream and named, with no checksum after them, and
        /// a reader refuses what is there. In the ordinary envelope the same body writes
        /// nothing (<see cref="TestPayloadCount.TestABodyThatWritesOtherThanItCountedIsRefused"/>).
        /// </summary>
        /// <remarks>
        /// A hundred bytes counted, through a buffer of sixteen. Written a byte at a time, six
        /// pieces, 96 bytes, have gone out when the 101st byte, the end, or the version is
        /// found wrong, and four are still held. Written as one run, longer than the buffer,
        /// it goes out whole if it is short, and not at all if it is long.
        /// </remarks>
        [TestMethod]
        [DataRow(101, 1, "was not made", false, 96)]
        [DataRow(99, 1, "wrote 99 bytes", false, 96)]
        [DataRow(100, 4, "version 4", false, 96)]
        [DataRow(101, 1, "was not made", true, 0)]
        [DataRow(99, 1, "wrote 99 bytes", true, 99)]
        public void TestALargeBodyThatWritesOtherThanItCountedLeavesWhatAReaderRefuses(
            int written, int versionWritten, string named, bool asOneRun, int streamed)
        {
            using var stream = new MemoryStream();
            var ex = WithBuffer(16, () => LargeForm.Below(0, () => Assert.ThrowsExactly<InvalidOperationException>(
                () => PersistenceFormat.Write(stream, new Uneven(100, written, (ushort)versionWritten, asOneRun)))));

            StringAssert.Contains(ex.Message, "counted a payload of 100 bytes, version 1", ex.Message);
            StringAssert.Contains(ex.Message, named, ex.Message);
            StringAssert.Contains(ex.Message, $"the first {streamed} bytes of the payload", ex.Message);

            var left = stream.ToArray();
            Assert.HasCount(PersistenceFormat.LargeHeaderLength + streamed, left, "the stream does not hold the header and the pieces written");
            Assert.AreEqual(100UL, BinaryPrimitives.ReadUInt64LittleEndian(left.AsSpan(10)), "the header does not declare the count");
            for (var i = 0; i < streamed; i++)
            {
                Assert.AreEqual((byte)i, left[PersistenceFormat.LargeHeaderLength + i], $"byte {i} of the payload is not the one written");
            }

            var refused = Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<CountSketch>(left));
            StringAssert.Contains(refused.Message, "ended", refused.Message);
        }

        /// <summary>
        /// A large-form write cut short by its stream, wherever, leaves the beginning of the
        /// envelope it was writing, which a reader refuses for its length; and a caller that
        /// writes on to the same stream buries it in front of a whole envelope, which a reader
        /// of the first refuses too, by the checksum one of its two checksums carries or by
        /// what the bytes after the cut say. This is the partial envelope <c>WriteTo</c>'s
        /// documentation warns of (#225).
        /// </summary>
        [TestMethod]
        public void TestALargeWriteCutShortLeavesWhatAReaderRefuses()
        {
            var filter = new BloomFilter(20_000, 0.01);
            for (var i = 0; i < 400; i++) filter.Add(Key($"f{i}"));
            var whole = LargeForm.WriteLarge(filter);
            var next = filter.ToByteArray();

            foreach (var cut in new[] { 10, PersistenceFormat.LargeHeaderLength, PersistenceFormat.LargeHeaderLength + 1000, whole.Length - 4, whole.Length - 1 })
            {
                var stream = new CutAfter(cut);
                WithBuffer(256, () => LargeForm.Below(0, () => Assert.ThrowsExactly<IOException>(() => filter.WriteTo(stream))));
                var left = stream.ToArray();
                CollectionAssert.AreEqual(whole[..cut], left, $"cut at {cut}, the stream does not hold the beginning of the envelope");

                var truncated = Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<BloomFilter>(left),
                    $"cut at {cut}, what was left was read");
                StringAssert.Contains(truncated.Message, "ended", $"cut at {cut}: {truncated.Message}");

                Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<BloomFilter>(left.Concat(next).ToArray()),
                    $"cut at {cut}, what was left was read with a whole envelope after it");
            }
        }

        private static byte[] Write(object structure) => LargeForm.Write(structure);

        private static int WriteTo(object structure, Stream stream)
        {
            structure.GetType().GetMethod("WriteTo", new[] { typeof(Stream) })!.Invoke(structure, new object[] { stream });
            return 0;
        }

        private static T Under<T>(long? threshold, Func<T> write) =>
            threshold is long lowered ? LargeForm.Below(lowered, write) : write();

        private static T WithBuffer<T>(int? size, Func<T> write)
        {
            var before = PayloadWriter.LoweredStreamBufferLength;
            PayloadWriter.LoweredStreamBufferLength = size;
            try
            {
                return write();
            }
            finally
            {
                PayloadWriter.LoweredStreamBufferLength = before;
            }
        }

        private static T WithLongestRun<T>(long bound, Func<T> write)
        {
            var before = PayloadWriter.LoweredLongestRun;
            PayloadWriter.LoweredLongestRun = bound;
            try
            {
                return write();
            }
            finally
            {
                PayloadWriter.LoweredLongestRun = before;
            }
        }

        private static long LeastAllocated(Action write) =>
            Enumerable.Range(0, 5).Min(_ => NoGCRegion.Allocated(write));

        /// <summary>
        /// A body of eight-byte fields that looks at the stream it is being written to as it
        /// returns from writing them.
        /// </summary>
        private sealed class Paced(RecordingStream sink, int fields) : IPayloadBody
        {
            internal long SeenAtTheEnd { get; private set; } = -1;

            public EnvelopeHeader WritePayload(PayloadWriter payload)
            {
                for (var i = 0; i < fields; i++) payload.WriteUInt64((ulong)i);
                if (!payload.IsCounting)
                {
                    this.SeenAtTheEnd = sink.Written;
                }

                return new(StructureId.CountSketch, HashId.XxHash3_64);
            }
        }

        /// <summary>
        /// A body that counts some bytes and writes another number of them, at another version
        /// or not, a byte at a time or as one run with no prefix.
        /// </summary>
        private sealed class Uneven(int counted, int written, ushort versionWritten, bool asOneRun) : IPayloadBody
        {
            public EnvelopeHeader WritePayload(PayloadWriter payload)
            {
                var bytes = payload.IsCounting ? counted : written;
                if (asOneRun)
                {
                    payload.WriteRaw(Enumerable.Range(0, bytes).Select(i => (byte)i).ToArray());
                }
                else
                {
                    for (var i = 0; i < bytes; i++) payload.WriteByte((byte)i);
                }

                return payload.IsCounting
                    ? new(StructureId.CountSketch, HashId.XxHash3_64)
                    : new(StructureId.CountSketch, HashId.XxHash3_64, versionWritten);
            }
        }

        /// <summary>
        /// A body that counts a block and then another, or an eight-byte field, and throws if
        /// it is asked to write.
        /// </summary>
        private sealed class Vast(long first, long second, bool field) : IPayloadBody
        {
            public EnvelopeHeader WritePayload(PayloadWriter payload)
            {
                if (!payload.IsCounting)
                {
                    throw new NotSupportedException("a payload this long is not written");
                }

                payload.BeginBlock(first);
                if (field)
                {
                    payload.WriteUInt64(0);
                }
                else
                {
                    payload.BeginBlock(second);
                }

                return new(StructureId.CountSketch, HashId.XxHash3_64);
            }
        }

        /// <summary>A type outside the library, persisted as one byte.</summary>
        private sealed class Outsider : IBinaryPersistable<Outsider>
        {
            public void WriteTo(Stream stream) => stream.WriteByte(7);

            public static Outsider ReadFrom(Stream stream) => new();

            public static Outsider ReadFrom(Stream stream, Func<ReadOnlySpan<byte>, ulong> hash) => new();
        }

        /// <summary>
        /// A stream that cannot seek and records the length of every write made to it.
        /// </summary>
        private sealed class RecordingStream : Stream
        {
            private readonly MemoryStream bytes = new();

            internal List<int> Writes { get; } = new();

            internal long Written => this.bytes.Length;

            internal byte[] ToArray() => this.bytes.ToArray();

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                this.Writes.Add(buffer.Length);
                this.bytes.Write(buffer);
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }

        /// <summary>
        /// A stream that takes <c>accepted</c> bytes and then fails, as a full disk does: the
        /// write that would pass it writes what fits and throws.
        /// </summary>
        private sealed class CutAfter(int accepted) : Stream
        {
            private readonly MemoryStream bytes = new();

            internal byte[] ToArray() => this.bytes.ToArray();

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                var room = accepted - (int)this.bytes.Length;
                this.bytes.Write(buffer[..Math.Min(room, buffer.Length)]);
                if (buffer.Length > room)
                {
                    throw new IOException("There is not enough space on the disk.");
                }
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
