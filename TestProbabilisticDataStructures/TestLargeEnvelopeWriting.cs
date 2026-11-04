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
        /// The most a refusal before anything is built may allocate: the count, the
        /// exception and its message, probed at 1,848 bytes for a run refused in the ordinary
        /// envelope, 2,464 in the large form, and 3,304 to 4,320 for <c>ToByteArray</c>
        /// called through reflection. This is fifteen times the most of them, and the
        /// payloads refused are a megabyte and more.
        /// </summary>
        private const long RefusalAllocates = 64 * 1024;

        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

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
        /// Past what one array holds, at the threshold that ships, <c>WriteTo</c> writes the
        /// large form rather than refusing: its header goes out declaring the whole length as a
        /// <c>u64</c>, before the body runs. A count sketch is made to claim a table of
        /// 268,435,446 cells, a payload of 2,147,483,580 bytes, seven past the ordinary
        /// envelope's ceiling, with every run in it short of the longest; its body writes the
        /// cells it really holds, so the write ends short of the count and is refused for that,
        /// with the header in the stream. No payload that long is built.
        /// </summary>
        [TestMethod]
        public void TestAWriteToPastOneArrayIsStreamedInTheLargeFormAtTheRealThreshold()
        {
            const uint width = 268_435_446;
            const long claimed = 12 + 8L * width;
            Assert.IsGreaterThan((long)PersistenceFormat.MaxPayloadLength, claimed);
            Assert.IsLessThanOrEqualTo((long)int.MaxValue, 8L * width, "the claimed run is past the longest");

            var sketch = new CountSketch(0.5, 0.5);
            for (var i = 0; i < 40; i++) sketch.Add(Key($"c{i}"), i % 5 - 2);
            var before = sketch.ToByteArray();
            var real = before.Length - 18L;

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
                    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    var thrown = Assert.ThrowsExactly<TargetInvocationException>(() => toByteArray.Invoke(null, arguments),
                        $"{where} of a payload past one array was not refused");
                    var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
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

    }
}
