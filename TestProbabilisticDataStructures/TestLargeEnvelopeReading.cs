using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// The large form of the envelope read as it streams (#226, #227): priced in a first
    /// pass over a stream that can seek, which reads the fields a structure's price depends
    /// on and seeks over the rest, then read in a second, which takes each byte once and
    /// checks the running checksum at the end; refused by name on a stream that cannot
    /// seek.
    /// </summary>
    /// <remarks>
    /// No test can build a payload past 2 GiB, so the large form is reached below it through
    /// the threshold the writer reads (<see cref="LargeForm"/>), and a payload longer than
    /// that through a stream that claims one (<see cref="Sparse"/>). What shows the read is
    /// streamed rather than held is what a held read could not do: it moves back to the
    /// payload's first byte once, between a price that read no run and a read that takes
    /// the payload in the pieces its reader asks for, and it allocates its payload's size
    /// less than the ordinary envelope's read of the same structure. The reader streams only
    /// the large form, so the ordinary envelope is held to the opposite where it can be.
    /// </remarks>
    [TestClass]
    public class TestLargeEnvelopeReading
    {
        private const string NotReached = "checksum was not reached";

        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        /// <summary>
        /// A stream that can only be read forward, as a pipe or a network stream is, which
        /// says how far into its bytes it has been read.
        /// </summary>
        internal sealed class OneWay(byte[] bytes) : Stream
        {
            private readonly MemoryStream inner = new(bytes, writable: false);

            /// <summary>How many of its bytes have been read.</summary>
            internal long Consumed => this.inner.Position;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => this.inner.Read(buffer, offset, count);
            public override int Read(Span<byte> buffer) => this.inner.Read(buffer);
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>
        /// One thing asked of a <see cref="Recording"/> stream: a read of the bytes from
        /// <see cref="From"/> to <see cref="To"/>, through the array overload or the span
        /// one, or a move from <see cref="From"/> to <see cref="To"/>.
        /// </summary>
        internal readonly record struct Asked(bool IsMove, long From, long To, bool Array)
        {
            internal long Count => this.To - this.From;
        }

        /// <summary>
        /// A stream that can seek, over bytes, recording every read and every move it is
        /// asked for, in order.
        /// </summary>
        /// <remarks>
        /// Over a <see cref="MemoryStream"/> rather than one itself, because a subclass's
        /// span read falls back to <see cref="Stream"/>'s, which calls the array overload,
        /// and every span read would be recorded twice and as the wrong kind.
        /// </remarks>
        internal sealed class Recording(byte[] bytes) : Stream
        {
            private readonly MemoryStream inner = new(bytes, writable: false);

            internal List<Asked> Log { get; } = new();

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => this.inner.Length;
            public override long Position
            {
                get => this.inner.Position;
                set
                {
                    this.Log.Add(new Asked(true, this.inner.Position, value, false));
                    this.inner.Position = value;
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var from = this.inner.Position;
                var n = this.inner.Read(buffer, offset, count);
                this.Log.Add(new Asked(false, from, from + n, true));
                return n;
            }

            public override int Read(Span<byte> buffer)
            {
                var from = this.inner.Position;
                var n = this.inner.Read(buffer);
                this.Log.Add(new Asked(false, from, from + n, false));
                return n;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                var from = this.inner.Position;
                var to = this.inner.Seek(offset, origin);
                this.Log.Add(new Asked(true, from, to, false));
                return to;
            }

            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>
        /// A stream that can seek and claims <paramref name="length"/> bytes: the ones given
        /// at its start, and zeros after them that are never held. So a payload longer than
        /// the suite can build is read as far as its reader goes, and no further.
        /// </summary>
        internal sealed class Sparse(byte[] start, long length) : Stream
        {
            private long position;

            /// <summary>The furthest byte any read has reached, the end of the last it took.</summary>
            internal long Furthest { get; private set; }

            /// <summary>
            /// Whether reads end with the bytes given, while the length claimed stays what it
            /// is: a stream that reports more than it gives.
            /// </summary>
            internal bool GivesOnlyWhatItHolds { get; init; }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => length;
            public override long Position
            {
                get => this.position;
                set => this.position = value;
            }

            public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                var end = this.GivesOnlyWhatItHolds ? start.Length : length;
                var n = (int)Math.Min(buffer.Length, Math.Max(0, end - this.position));
                for (var i = 0; i < n; i++)
                {
                    var at = this.position + i;
                    buffer[i] = at < start.Length ? start[at] : (byte)0;
                }

                this.position += n;
                this.Furthest = Math.Max(this.Furthest, this.position);
                return n;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                this.position = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => this.position + offset,
                    _ => length + offset,
                };
                return this.position;
            }

            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>The large header's own checksum worked out again, after an edit to a field it covers.</summary>
        private static byte[] SealHeader(byte[] envelope)
        {
            var bytes = (byte[])envelope.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(LargeForm.HeaderFieldsEnd),
                Crc32.HashToUInt32(bytes.AsSpan(4, LargeForm.HeaderFieldsEnd - 4)));
            return bytes;
        }

        /// <summary>
        /// An ordinary envelope's payload wrapped in the large form, as FORMAT.md lays it out,
        /// at the version it was written at: so a stored payload of a layout no writer writes
        /// now is read in the large form too. One already in the large form is as it is.
        /// </summary>
        private static byte[] Enlarge(byte[] ordinary)
        {
            if (LargeForm.IsLarge(ordinary))
            {
                return ordinary;
            }

            var payload = ordinary.AsSpan(14, ordinary.Length - 18);
            var large = new byte[PersistenceFormat.LargeEnvelopeLength + payload.Length];
            ordinary.AsSpan(0, 10).CopyTo(large);
            BinaryPrimitives.WriteUInt16LittleEndian(large.AsSpan(4),
                (ushort)(LargeForm.VersionField(ordinary) | PersistenceFormat.LargeFormFlag));
            BinaryPrimitives.WriteUInt64LittleEndian(large.AsSpan(10), (ulong)payload.Length);
            payload.CopyTo(large.AsSpan(PersistenceFormat.LargeHeaderLength));
            return LargeForm.Reseal(large);
        }

        /// <summary>
        /// Every structure's payload in the large form, from a stream that cannot seek, is
        /// refused by name, for the stream, saying to copy it to one that can, having read
        /// nothing past the large header. The same structure reads from such a stream in the
        /// ordinary envelope, which is held as it always was, and in the large form from a
        /// stream that can seek. The roster is <see cref="StructureId"/>, through the budget
        /// samples.
        /// </summary>
        /// <remarks>
        /// The large form is priced in one pass over the stream and read in a second, which
        /// has to seek back between them (#226). Refused rather than buffered: a payload in
        /// the large form is past 2 GiB, and a reader that copied it to memory to read it
        /// would hold what the large form is for not holding.
        /// </remarks>
        [TestMethod]
        public void TestTheLargeFormFromAStreamThatCannotSeekIsRefusedByName()
        {
            var samples = TestReadBudget.Samples.Value;

            foreach (var (id, sample) in samples)
            {
                var large = TestReadBudget.InTheLargeForm(sample).Bytes;

                var oneWay = new OneWay(large);
                var refused = Assert.ThrowsExactly<NotSupportedException>(
                    () => sample.ReadStream(oneWay, ReadOptions.Unlimited),
                    $"{id} in the large form was read from a stream that cannot seek");
                StringAssert.Contains(refused.Message, "cannot seek", $"{id}: {refused.Message}");
                StringAssert.Contains(refused.Message, "Copy it to a file", $"{id}: {refused.Message}");
                StringAssert.Contains(refused.Message, id.ToString(), $"{id}: {refused.Message}");
                Assert.AreEqual((long)PersistenceFormat.LargeHeaderLength, oneWay.Consumed,
                    $"{id} was read past its large header before it was refused for the stream");

                CollectionAssert.AreEqual(sample.Bytes, sample.Write(sample.ReadStream(new OneWay(sample.Bytes), ReadOptions.Unlimited)),
                    $"{id} in the ordinary envelope did not read from a stream that cannot seek");

                using var seekable = new MemoryStream(large, writable: false);
                CollectionAssert.AreEqual(sample.Bytes, sample.Write(sample.ReadStream(seekable, ReadOptions.Unlimited)),
                    $"{id} in the large form did not read from a stream that can seek");
            }

            StructureRoster.AssertCoversEveryStructure("large form from a stream that cannot seek", samples.Keys);
        }

        /// <summary>
        /// The refusal for a stream that cannot seek comes after the large header's checksum
        /// and before the hash it names is checked: a damaged header is refused as damaged
        /// from any stream, and a payload whose hash cannot be resolved is refused for the
        /// stream from one that cannot seek, and for its hash from one that can.
        /// </summary>
        /// <remarks>
        /// After the checksum, because nothing in the header is used before it has matched,
        /// the flag that says it is the large form aside. Before the hash, because no hash
        /// makes a stream that cannot seek readable in the large form.
        /// </remarks>
        [TestMethod]
        public void TestTheStreamIsRefusedAfterTheHeaderChecksumAndBeforeTheHash()
        {
            var filter = new BloomFilter(200, 0.01);
            filter.SetHash(data => (ulong)data.Length * 0x9E3779B97F4A7C15UL);
            for (var i = 0; i < 40; i++) filter.Add(Key($"w{i}"));
            var large = LargeForm.WriteLarge(filter);

            var unseekable = Assert.ThrowsExactly<NotSupportedException>(() => BloomFilter.ReadFrom(new OneWay(large)),
                "a payload whose hash cannot be resolved, on a stream that cannot seek, was refused for its hash first");
            StringAssert.Contains(unseekable.Message, "cannot seek", unseekable.Message);

            using var seekable = new MemoryStream(large, writable: false);
            var unresolved = Assert.ThrowsExactly<InvalidDataException>(() => BloomFilter.ReadFrom(seekable));
            StringAssert.Contains(unresolved.Message, "SetHash", unresolved.Message);

            var damaged = (byte[])large.Clone();
            damaged[6] ^= 1;
            var header = Assert.ThrowsExactly<InvalidDataException>(() => BloomFilter.ReadFrom(new OneWay(damaged)),
                "a damaged large header on a stream that cannot seek was refused for the stream first");
            StringAssert.Contains(header.Message, "header checksum does not match", header.Message);
        }

        /// <summary>
        /// What a structure's price is let off reading: the structures whose price reads all
        /// of their payload, with the reason, held to it so that the reason going stale fails.
        /// </summary>
        private static readonly Dictionary<StructureId, string> PricedFromEveryByte = new()
        {
            [StructureId.SublimeCountMinSketch] =
                "its packed counters are tuned to the counts, so its price reads every count to " +
                "price them; a format version recording the tuning is to let it seek over them",
        };

        /// <summary>
        /// Every structure in the large form is read in two passes over its stream, with one
        /// move back to the payload's first byte between them. The first, the price, reads
        /// only fields, none longer than the widest, and no byte of a run, and less than the
        /// whole payload. The second, the read, takes every byte of the payload once, in
        /// order, in the pieces its reader asks for: fields, and runs of exactly the length
        /// their prefixes give. Then the checksum, once.
        /// </summary>
        /// <remarks>
        /// A reader that held the payload would read it in one piece the length of the
        /// payload, which no prefix gives, and never move back. A price that read a run, or
        /// copied one, would read more than the widest field at once, or the bytes of a run.
        /// The runs are those the second pass reads through the array overload, which is how
        /// a run is read; a run holding a nested envelope is left out of the first pass's
        /// check, because the price looks into it, at its header and fields, to price what it
        /// holds. The roster is <see cref="StructureId"/>, through the budget samples, which
        /// are sized so the bulk of each payload is its runs or its counted fields.
        /// <para>
        /// A price reads the fields its structure's price depends on, so a structure whose
        /// bulk is fields its price needs reads them all: <see cref="PricedFromEveryByte"/>.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestTheLargeFormIsPricedInAPassThatSeeksOverRunsAndReadInOneThatTakesEachByteOnce()
        {
            var samples = TestReadBudget.Samples.Value;
            var start = (long)PersistenceFormat.LargeHeaderLength;
            long runBytes = 0, movedOver = 0, payloadBytes = 0;

            foreach (var reason in PricedFromEveryByte.Values)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(reason), "a structure is let off without a reason");
            }

            foreach (var (id, sample) in samples)
            {
                var large = TestReadBudget.InTheLargeForm(sample).Bytes;
                var end = (long)large.Length - 4;
                var payload = end - start;
                payloadBytes += payload;

                var stream = new Recording(large);
                CollectionAssert.AreEqual(sample.Bytes, sample.Write(sample.ReadStream(stream, ReadOptions.Unlimited)),
                    $"{id} read from a recording stream is not the structure that wrote it");
                var log = stream.Log;

                var backs = Enumerable.Range(0, log.Count).Where(i => log[i].IsMove && log[i].To == start).ToList();
                Assert.HasCount(1, backs,
                    $"{id} moved back to its payload's first byte {backs.Count} times, so it was not priced and then read");

                // The second pass: the payload in order, each byte once, then the checksum.
                var read = log.Skip(backs[0] + 1).ToList();
                Assert.IsFalse(read.Any(a => a.IsMove), $"{id} moved within the stream while it was read");

                var runs = new List<Asked>();
                var at = start;
                foreach (var piece in read.Where(a => a.From < end))
                {
                    Assert.AreEqual(at, piece.From, $"{id} was read at {piece.From} after the read before it ended at {at}");
                    if (piece.Array)
                    {
                        var prefix = BinaryPrimitives.ReadUInt32LittleEndian(large.AsSpan((int)piece.From - 4));
                        Assert.AreEqual((long)prefix, piece.Count,
                            $"{id} read {piece.Count:N0} bytes at once at {piece.From}, which is no run its prefix gives: it was held");
                        runs.Add(piece);
                    }
                    else
                    {
                        Assert.IsLessThanOrEqualTo((long)PayloadSource.WidestField, piece.Count,
                            $"{id} read a field of {piece.Count} bytes at {piece.From}");
                    }
                    at = piece.To;
                }
                Assert.AreEqual(end, at, $"{id} was read to {at}, and its payload ends at {end}");

                var tail = read.Where(a => a.From >= end).ToList();
                Assert.HasCount(1, tail, $"{id}'s checksum was read {tail.Count} times");
                Assert.AreEqual(4L, tail[0].Count, $"{id}'s checksum was read as {tail[0].Count} bytes");

                // The first pass: fields only, past every run, and less than the payload.
                var priced = 0L;
                var bulk = runs.Where(r => !large.AsSpan((int)r.From, 4).SequenceEqual(PersistenceFormat.Magic)).ToList();
                foreach (var asked in log.Take(backs[0]).Where(a => a.IsMove || a.From >= start))
                {
                    if (asked.IsMove)
                    {
                        Assert.IsGreaterThanOrEqualTo(asked.From, asked.To, $"{id}'s price moved back from {asked.From} to {asked.To}");
                        movedOver += asked.Count;
                        continue;
                    }

                    Assert.IsFalse(asked.Array, $"{id}'s price read {asked.Count:N0} bytes at {asked.From} as a run is read");
                    Assert.IsLessThanOrEqualTo((long)PayloadSource.WidestField, asked.Count,
                        $"{id}'s price read {asked.Count:N0} bytes at once at {asked.From}");
                    Assert.IsFalse(bulk.Any(r => asked.From < r.To && r.From < asked.To),
                        $"{id}'s price read the bytes at {asked.From} of a run, rather than moving past it");
                    priced += asked.Count;
                }

                runBytes += bulk.Sum(r => r.Count);
                Console.WriteLine(
                    $"[TWO-PASS] {id}: payload {payload:N0}, priced from {priced:N0}, runs {bulk.Count:N0} of {bulk.Sum(r => r.Count):N0} bytes");

                if (PricedFromEveryByte.ContainsKey(id))
                {
                    Assert.AreEqual(payload, priced,
                        $"{id} is let off pricing from less than its whole payload, and priced from {priced:N0} of {payload:N0}");
                }
                else
                {
                    Assert.IsLessThan(payload, priced, $"{id}'s price read all {payload:N0} bytes of its payload");
                }
            }

            // Vacuity: the sweep held something to the claims about runs.
            Console.WriteLine(
                $"[TWO-PASS] all: payloads {payloadBytes:N0}, runs {runBytes:N0}, moved past by the prices {movedOver:N0}");
            Assert.IsGreaterThan(0L, runBytes, "no sample held a run, so nothing was shown to be moved past");
            Assert.IsGreaterThan(payloadBytes / 2, movedOver,
                $"the prices moved past {movedOver:N0} of the {payloadBytes:N0} payload bytes, so they mostly read rather than seeked");
            StructureRoster.AssertCoversEveryStructure("large form read in two passes", samples.Keys);
        }

        /// <summary>
        /// The most a structure's read in the large form may allocate beyond what the
        /// ordinary envelope's read of it allocates less the payload: what streaming adds,
        /// the source the payload is read through and its buffer for a field.
        /// </summary>
        private const long StreamedOverhead = 512;

        /// <summary>
        /// Every structure's read in the large form allocates at least its payload's size less
        /// than the ordinary envelope's read of the same structure, which holds its payload:
        /// the large form is read from the stream, not into an array first. The roster is
        /// <see cref="StructureId"/>, through the budget samples.
        /// </summary>
        /// <remarks>
        /// Each read is the least of five, as <see cref="TestReadBudget"/> measures them, both
        /// priced and unlimited. A structure that holds others saves more than its own
        /// payload, since each envelope inside it is in the large form too and is streamed
        /// from the run that holds it.
        /// </remarks>
        [TestMethod]
        public void TestTheLargeFormIsReadWithoutHoldingItsPayload()
        {
            var samples = TestReadBudget.Samples.Value;
            var swept = new List<StructureId>();

            foreach (var (id, sample) in samples)
            {
                if (sample.SmallBecause is not null)
                {
                    continue;
                }

                var large = TestReadBudget.InTheLargeForm(sample).Bytes;
                var payload = (long)large.Length - PersistenceFormat.LargeEnvelopeLength;

                var held = TestReadBudget.Allocated(() => sample.Read(sample.Bytes, ReadOptions.Unlimited));
                var streamed = TestReadBudget.Allocated(() => sample.Read(large, ReadOptions.Unlimited));
                Console.WriteLine(
                    $"[STREAMED] {id}: payload {payload:N0}, ordinary read {held:N0}, large-form read {streamed:N0}, " +
                    $"saved {held - streamed:N0}");

                Assert.IsGreaterThanOrEqualTo(payload - StreamedOverhead, held - streamed,
                    $"{id}'s read in the large form allocated {streamed:N0} bytes, and in the ordinary envelope " +
                    $"{held:N0}: it held its {payload:N0}-byte payload");
                swept.Add(id);
            }

            StructureRoster.AssertCoversEveryStructure("large form read without holding", swept,
                (StructureId.SimHashSignature, "its payload is one 64-bit value, eight bytes, less than streaming adds"));
        }

        /// <summary>
        /// A large-form payload longer than one array, on a stream that holds it, is read on:
        /// priced and read as far as its reader goes, and refused there, for the bytes left
        /// over, without anything its size allocated and with the stream read no further than
        /// the filter's own bytes. One array was the limit while the payload was held.
        /// </summary>
        /// <remarks>
        /// A Bloom filter's payload under a header claiming more, on a stream that claims as
        /// much and holds zeros past the filter's bytes. Past one array, the old limit, by a
        /// byte, and past a <c>u32</c>, so every offset and count the reader works out is past
        /// an <c>int</c> too.
        /// </remarks>
        [TestMethod]
        [DataRow(0x7FFF_FFC8L)]
        [DataRow(5L << 30)]
        public void TestALargeFormPayloadLongerThanOneArrayIsReadOn(long claimed)
        {
            Assert.IsGreaterThan((long)Array.MaxLength, claimed, "the claim is not past one array");

            var filter = new BloomFilter(200, 0.01);
            for (var i = 0; i < 40; i++) filter.Add(Key($"w{i}"));
            var bytes = LargeForm.WriteLarge(filter);
            var own = bytes.Length - PersistenceFormat.LargeEnvelopeLength;

            var claiming = (byte[])bytes.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(claiming.AsSpan(10), (ulong)claimed);
            var head = SealHeader(claiming)[..^4];
            var total = PersistenceFormat.LargeEnvelopeLength + claimed;

            Sparse stream = null;
            InvalidDataException refused = null;
            var allocated = TestReadBudget.Allocated(() =>
            {
                stream = new Sparse(head, total);
                return refused = Assert.ThrowsExactly<InvalidDataException>(
                    () => BloomFilter.ReadFrom(stream, ReadOptions.Unlimited),
                    $"a Bloom filter under a header claiming {claimed:N0} bytes was read");
            });

            Assert.DoesNotContain("single array", refused.Message, $"a payload past one array was refused for that: {refused.Message}");
            StringAssert.Contains(refused.Message, "left over", refused.Message);
            StringAssert.Contains(refused.Message, $"of {claimed}", refused.Message);
            StringAssert.Contains(refused.Message, NotReached, refused.Message);
            Assert.IsLessThan(64L * 1024, allocated,
                $"the read of a payload claiming {claimed:N0} bytes allocated {allocated:N0}");
            Assert.IsLessThanOrEqualTo((long)PersistenceFormat.LargeHeaderLength + own, stream.Furthest,
                $"the read went to byte {stream.Furthest:N0}, past the filter's own, which end at {PersistenceFormat.LargeHeaderLength + own}");
        }

        /// <summary>
        /// A payload in the large form refused for its shape is read no further than where it
        /// was refused, and its checksum is never read: a Bloom filter whose bit array's run
        /// claims more than the payload holds, its checksums repaired, is refused at the run's
        /// length prefix, and none of the run is read.
        /// </summary>
        /// <remarks>
        /// #227: if parsing fails before the end, the error names the shape problem and says
        /// the checksum was not reached; the reader does not read on. A reader that held the
        /// payload had read all of it before parsing. The run's prefix is the field to break
        /// because a Bloom filter's reader checks its other fields only once it has read them
        /// all, its bit array included.
        /// </remarks>
        [TestMethod]
        public void TestAPayloadRefusedForItsShapeIsReadNoFurther()
        {
            var filter = new BloomFilter(20_000, 0.01);
            for (var i = 0; i < 40; i++) filter.Add(Key($"w{i}"));
            var bytes = (byte[])LargeForm.WriteLarge(filter).Clone();
            var start = PersistenceFormat.LargeHeaderLength;
            var payload = bytes.Length - PersistenceFormat.LargeEnvelopeLength;

            // m, k, the placement and the count, then the bucket table's count and width, then
            // the length prefix of its run.
            const int prefix = 4 + 4 + 1 + 8 + 4 + 1;
            Assert.AreEqual((uint)(payload - prefix - 4), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(start + prefix)),
                "the bit array's run is not where the layout puts it");
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(start + prefix), (uint)payload);
            var stream = new Recording(LargeForm.Reseal(bytes));

            var refused = Assert.ThrowsExactly<InvalidDataException>(() => BloomFilter.ReadFrom(stream));
            StringAssert.Contains(refused.Message, $"while reading {payload} more at offset {prefix + 4}.", refused.Message);
            StringAssert.Contains(refused.Message, NotReached, refused.Message);

            var furthest = stream.Log.Where(a => !a.IsMove).Max(a => a.To);
            Assert.IsLessThanOrEqualTo((long)start + prefix + 4, furthest,
                $"the filter refused at its run's prefix was read to byte {furthest:N0} of {bytes.Length:N0}");
        }

        /// <summary>
        /// A stream that reports more than it gives, ending inside a run of the payload while
        /// its length says the whole envelope is there, is refused for ending before the end of
        /// the payload, and the run is not returned half read.
        /// </summary>
        /// <remarks>
        /// The length a stream reports is what the large form is held to before it is priced,
        /// so only a stream whose length is wrong gets past that and runs out in the middle of
        /// a run. Read straight from the stream into the array that keeps it, the run is
        /// refused there; a read that stopped at the stream's end and went on would hand back
        /// an array with its tail never written.
        /// </remarks>
        [TestMethod]
        public void TestAStreamThatEndsInsideARunIsRefusedForEndingThere()
        {
            var filter = new BloomFilter(20_000, 0.01);
            for (var i = 0; i < 40; i++) filter.Add(Key($"w{i}"));
            var bytes = LargeForm.WriteLarge(filter);

            // The fields and the bit array's prefix, and ten bytes of the bit array.
            var cut = PersistenceFormat.LargeHeaderLength + 26 + 10;
            var stream = new Sparse(bytes[..cut], bytes.Length) { GivesOnlyWhatItHolds = true };

            var refused = Assert.ThrowsExactly<InvalidDataException>(() => BloomFilter.ReadFrom(stream));
            StringAssert.Contains(refused.Message, "before the end of the payload its header claims", refused.Message);
            StringAssert.Contains(refused.Message, NotReached, refused.Message);
        }

        /// <summary>
        /// A source read for its checksum appends every byte it takes, fields and runs alike,
        /// from the first byte of its payload once it is taken back there, and refuses to move
        /// past any: what the checksum covers is exactly what was read.
        /// </summary>
        /// <remarks>
        /// The price before it moves past runs, and may; nothing reading the payload for its
        /// checksum does. A seek there would leave bytes out of the checksum, so it is refused
        /// as a defect rather than made.
        /// </remarks>
        [TestMethod]
        public void TestASourceReadForItsChecksumTakesEveryByteInTurn()
        {
            var bytes = Enumerable.Range(0, 64).Select(i => (byte)(i * 7 + 3)).ToArray();
            using var stream = new MemoryStream(bytes, writable: false);
            var source = new PayloadSource(stream);

            source.Take(4);
            source.Skip(8);
            Assert.AreEqual(12L, source.Position, "a price's field and skip did not move the source");

            var crc = new Crc32();
            source.Restart(0, crc);
            Assert.AreEqual(0L, source.Position, "the source was not taken back to its payload's first byte");
            CollectionAssert.AreEqual(bytes[..4], source.Take(4).ToArray(), "the source did not start again at its first byte");
            var run = new byte[12];
            source.ReadRun(run);
            CollectionAssert.AreEqual(bytes[4..16], run, "the run was not the bytes after the field");
            Assert.AreEqual(16L, source.Position);
            Assert.AreEqual(Crc32.HashToUInt32(bytes.AsSpan(0, 16)), crc.GetCurrentHashAsUInt32(),
                "the checksum does not cover exactly the bytes taken, a field and a run");

            var refused = Assert.ThrowsExactly<InvalidOperationException>(() => source.Skip(8),
                "a source read for its checksum moved past bytes");
            StringAssert.Contains(refused.Message, "checksum", refused.Message);
            Assert.AreEqual(16L, source.Position, "a refused skip moved the source");
        }

        /// <summary>
        /// A large header followed by fewer bytes than the checksum after a payload needs is
        /// refused as past the stream, whatever length it claims, nothing included: at a
        /// length of nothing, three bytes after the header are refused for that and four go on
        /// to the payload, which then has nothing in it for its reader.
        /// </summary>
        /// <remarks>
        /// The bytes after the header less the checksum's four is negative there, and the
        /// comparison of what remains with the length is unsigned, so the first test is what
        /// keeps a stream that ends inside its checksum from passing it.
        /// </remarks>
        [TestMethod]
        public void TestAStreamEndingWithinTheChecksumAfterTheHeaderIsRefusedAsPastIt()
        {
            var filter = new BloomFilter(200, 0.01);
            for (var i = 0; i < 40; i++) filter.Add(Key($"w{i}"));
            var claimed = (byte[])LargeForm.WriteLarge(filter).Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(claimed.AsSpan(10), 0);
            var header = SealHeader(claimed)[..PersistenceFormat.LargeHeaderLength];

            for (var after = 0; after <= 4; after++)
            {
                var bytes = header.Concat(new byte[after]).ToArray();
                var refused = Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<BloomFilter>(bytes),
                    $"a large header claiming nothing, and {after} bytes after it, was read");
                if (after < 4)
                {
                    StringAssert.Contains(refused.Message, TestLargeEnvelope.PastTheStream,
                        $"{after} bytes after the header: {refused.Message}");
                }
                else
                {
                    Assert.DoesNotContain(TestLargeEnvelope.PastTheStream, refused.Message,
                        $"the checksum's four bytes after the header were refused as too few: {refused.Message}");
                }
            }
        }

        /// <summary>
        /// The reader of a streamed payload works out what remains in 64 bits: over a payload
        /// claiming 5 GiB it holds that much, refuses a byte more, takes a count of as many as
        /// remain and refuses one more, and reaches its end by moving past the rest of it.
        /// </summary>
        /// <remarks>
        /// <c>Remaining</c> was an <c>int</c> cast of the bytes left, which was never more than
        /// the largest array while the payload was held: 5 GiB came out as 1 GiB.
        /// </remarks>
        [TestMethod]
        public void TestAReaderOfAStreamedPayloadPastAnIntKnowsWhatRemains()
        {
            const long length = 5L << 30;
            Assert.AreEqual(length, RemainingOver(length), "the reader of a 5 GiB payload did not know it held that much");

            Assert.IsNull(SkipOver(length, length), "a reader could not move past the whole of a 5 GiB payload");
            StringAssert.Contains(SkipOver(length, length + 1)?.Message ?? "", "Payload ended", "a byte past 5 GiB was not refused as the payload ending");

            Assert.IsNull(HoldsOver(length, (ulong)length), "a count of every byte of 5 GiB was refused");
            StringAssert.Contains(HoldsOver(length, (ulong)length + 1)?.Message ?? "", "beyond what it can hold",
                "a count of a byte more than 5 GiB was not refused");
        }

        private static long RemainingOver(long length)
        {
            var reader = new PayloadReader(new PayloadSource(new Sparse(Array.Empty<byte>(), length)), length, Unlimited());
            return reader.Remaining;
        }

        private static InvalidDataException SkipOver(long length, long count)
        {
            var reader = new PayloadReader(new PayloadSource(new Sparse(Array.Empty<byte>(), length)), length, Unlimited());
            try
            {
                reader.Skip(count);
                reader.ExpectEnd();
                return null;
            }
            catch (InvalidDataException e)
            {
                return e;
            }
        }

        private static InvalidDataException HoldsOver(long length, ulong count)
        {
            var reader = new PayloadReader(new PayloadSource(new Sparse(Array.Empty<byte>(), length)), length, Unlimited());
            try
            {
                reader.RequireHolds(count, 1, "bytes");
                return null;
            }
            catch (InvalidDataException e)
            {
                return e;
            }
        }

        private static ReadBudget Unlimited() => new(long.MaxValue, StructureId.BloomFilter);

        /// <summary>
        /// A run read from a streamed payload that has the bytes for it is held to one array:
        /// one claiming a byte more than the largest is refused as more than one holds, and one
        /// claiming the largest passes that and the bounds check, and goes on to be refused by
        /// a budget too small for it, with nothing allocated.
        /// </summary>
        /// <remarks>
        /// Held, no payload could hold a run between the largest array and the largest
        /// <c>int</c>, so the bounds check refused it. Streamed, one could, and the reader would
        /// then have asked for an array no runtime makes.
        /// </remarks>
        [TestMethod]
        public void TestARunOfAStreamedPayloadIsHeldToOneArray()
        {
            const long length = 5L << 30;

            var past = RunOver(length, (uint)Array.MaxLength + 1, new ReadBudget(long.MaxValue, StructureId.BloomFilter));
            StringAssert.Contains(past?.Message ?? "", "cannot be read into a single array", past?.Message);

            InvalidDataException largest = null;
            var allocated = TestReadBudget.Allocated(() =>
                largest = RunOver(length, (uint)Array.MaxLength, new ReadBudget(1 << 20, StructureId.BloomFilter)));
            Assert.IsInstanceOfType<ReadBudgetExceededException>(largest?.InnerException,
                $"a run of the largest array, which the payload holds, was not refused for its budget: {largest?.Message}");
            Assert.IsLessThan(64L * 1024, allocated, $"refusing a run of the largest array allocated {allocated:N0}");
        }

        /// <summary>
        /// A streamed payload whose stream holds a run longer than one array is refused for
        /// that, under the default budget, and not for its cost: the price stops at the run,
        /// as the read does, rather than pricing an array no read could make and naming a
        /// total that no budget would load.
        /// </summary>
        /// <remarks>
        /// A Bloom filter's fields with its bit array's prefix claiming a byte more than the
        /// largest array, under a header claiming exactly the payload that makes, on a stream
        /// that claims it all and holds zeros past the fields. Priced as a run, the array
        /// would be a little over 2 GiB, twice the default budget.
        /// </remarks>
        [TestMethod]
        public void TestARunPastOneArrayInAStreamedPayloadIsRefusedForThatNotPriced()
        {
            var filter = new BloomFilter(200, 0.01);
            for (var i = 0; i < 40; i++) filter.Add(Key($"w{i}"));
            var bytes = LargeForm.WriteLarge(filter);

            // m, k, the placement and the count, then the bucket table's count and width, then
            // the length prefix of its run.
            const int prefix = 4 + 4 + 1 + 8 + 4 + 1;
            var start = PersistenceFormat.LargeHeaderLength;
            var run = (ulong)Array.MaxLength + 1;
            var claimed = (ulong)prefix + 4 + run;

            var head = bytes[..(start + prefix + 4)];
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(start + prefix), (uint)run);
            BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(10), claimed);
            head = SealHeader(head);
            var stream = new Sparse(head, PersistenceFormat.LargeEnvelopeLength + (long)claimed);

            var refused = Assert.ThrowsExactly<InvalidDataException>(() => BloomFilter.ReadFrom(stream),
                "a Bloom filter whose bit array claims more than one array holds was read");
            Assert.IsNotInstanceOfType<ReadBudgetExceededException>(refused.InnerException,
                $"a run no array holds was priced, and refused for its cost: {refused.Message}");
            StringAssert.Contains(refused.Message, "cannot be read into a single array", refused.Message);
        }

        private static InvalidDataException RunOver(long length, uint claim, ReadBudget budget)
        {
            var prefix = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(prefix, claim);
            var reader = new PayloadReader(new PayloadSource(new Sparse(prefix, length)), length, budget);
            try
            {
                reader.ReadBytes();
                return null;
            }
            catch (InvalidDataException e)
            {
                return e;
            }
        }

        /// <summary>
        /// Every structure that writes a run refuses one claiming more than its payload in the
        /// large form holds, at the claims either side of where the held reader's offset
        /// arithmetic wrapped: the int-edge sweep of <see cref="TestPayloadReader"/>, held to the
        /// streamed reader, whose arithmetic is 64-bit and whose edge is the bytes that remain.
        /// </summary>
        /// <remarks>
        /// The runs are found as that sweep finds them, by probing, here at the large form's
        /// offsets and with both checksums repaired. The payloads are the shared samples in
        /// the large form and every stored payload wrapped in it at the version it was written
        /// at, which is how a cuckoo filter's layouts with a run in every entry are reached.
        /// The roster is <see cref="StructureId"/>, with the same exemptions.
        /// </remarks>
        [TestMethod]
        public void TestEveryStructureRefusesARunClaimingMoreThanItsLargeFormHolds()
        {
            var samples = LargeForm.Samples();
            var readers = samples.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Read);

            var payloads = samples
                .Select(s => (s.Id, Name: $"the sample {s.Id} payload", s.Read, Bytes: s.Large))
                .Concat(TestPayloadReader.StoredPayloads(readers).Select(p => (p.Id, p.Name, p.Read, Bytes: Enlarge(p.Bytes))))
                .ToArray();

            var withRuns = new HashSet<StructureId>();
            var failures = new List<string>();
            var claimsTried = 0;
            var start = PersistenceFormat.LargeHeaderLength;

            foreach (var (id, name, read, clean) in payloads)
            {
                Assert.IsTrue(LargeForm.IsLarge(clean), $"{name} is not in the large form");
                var wrong = new List<string>();

                foreach (var at in LargeRunPrefixes(clean, read))
                {
                    var offset = at - start + 4;
                    withRuns.Add(id);

                    foreach (var claim in TestPayloadReader.ClaimsAround(offset))
                    {
                        claimsTried++;
                        var expected = TestPayloadReader.RefusalFor(claim, offset);
                        try
                        {
                            read(Poked(clean, at, (uint)claim));
                            wrong.Add($"offset {offset}, {claim:N0} bytes: read");
                        }
                        catch (InvalidDataException ex) when (ex.Message.Contains(expected, StringComparison.Ordinal))
                        {
                            // Refused for the claim it made.
                        }
                        catch (Exception ex)
                        {
                            wrong.Add($"offset {offset}, {claim:N0} bytes: {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }

                if (wrong.Count > 0)
                {
                    failures.Add($"{name}: {wrong.Count} claims were not refused for what they claim, the first at {wrong[0]}");
                }
            }

            Assert.IsEmpty(failures, Environment.NewLine + string.Join(Environment.NewLine, failures));
            Assert.IsGreaterThan(0, claimsTried, "no run was found in any payload");
            StructureRoster.AssertCoversEveryStructure("run lengths in the large form", withRuns, TestPayloadReader.NoRun);
        }

        /// <summary>A large-form envelope with a u32 overwritten in its payload, both checksums repaired.</summary>
        private static byte[] Poked(byte[] envelope, int at, uint value)
        {
            var bytes = (byte[])envelope.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
            return LargeForm.Reseal(bytes);
        }

        /// <summary>
        /// Where in a large-form envelope the length prefixes of runs are, found as
        /// <see cref="TestPayloadReader"/> finds them in the ordinary one: a u32 claiming more
        /// than the envelope holds is refused as the payload ending, at the offset just after it.
        /// </summary>
        private static List<int> LargeRunPrefixes(byte[] clean, Func<byte[], object> read)
        {
            var found = new List<int>();
            var probe = (uint)clean.Length;
            var payloadEnd = clean.Length - 4;

            for (var at = PersistenceFormat.LargeHeaderLength; at + 4 <= payloadEnd; at++)
            {
                var stated = BinaryPrimitives.ReadUInt32LittleEndian(clean.AsSpan(at));
                if (at + 4 + (long)stated > payloadEnd)
                {
                    continue;
                }

                try
                {
                    read(Poked(clean, at, probe));
                }
                catch (InvalidDataException ex)
                {
                    var offset = at - PersistenceFormat.LargeHeaderLength + 4;
                    if (ex.Message.Contains($"while reading {probe} more at offset {offset}.", StringComparison.Ordinal))
                    {
                        found.Add(at);
                    }
                }
                catch (Exception)
                {
                    // Not a run: some other field, refused or choked on in its own way.
                }
            }

            return found;
        }

        /// <summary>
        /// Envelopes written one after another on one stream, in either form, are read one
        /// after another from it: each read ends exactly where its envelope does, however far
        /// the price of a large one moved through it, so the next begins where it should.
        /// </summary>
        [TestMethod]
        public void TestEnvelopesInTurnOnOneStreamAreReadInTurn()
        {
            var filter = new BloomFilter(2_000, 0.01);
            for (var i = 0; i < 100; i++) filter.Add(Key($"w{i}"));
            var sketch = new CountMinSketch(0.01, 0.01);
            for (var i = 0; i < 100; i++) sketch.Add(Key($"w{i}"));

            using var stream = new MemoryStream();
            var ends = new List<long>();
            LargeForm.Below(0, () => { filter.WriteTo(stream); return 0; });
            ends.Add(stream.Position);
            sketch.WriteTo(stream);
            ends.Add(stream.Position);
            LargeForm.Below(0, () => { sketch.WriteTo(stream); return 0; });
            ends.Add(stream.Position);
            filter.WriteTo(stream);
            ends.Add(stream.Position);

            stream.Position = 0;
            var read = new List<byte[]>();
            read.Add(BloomFilter.ReadFrom(stream).ToByteArray());
            Assert.AreEqual(ends[0], stream.Position, "the first, in the large form, was not read to its end and no further");
            read.Add(CountMinSketch.ReadFrom(stream).ToByteArray());
            Assert.AreEqual(ends[1], stream.Position, "the second, ordinary, was not read to its end and no further");
            read.Add(CountMinSketch.ReadFrom(stream).ToByteArray());
            Assert.AreEqual(ends[2], stream.Position, "the third, in the large form, was not read to its end and no further");
            read.Add(BloomFilter.ReadFrom(stream).ToByteArray());
            Assert.AreEqual(ends[3], stream.Position, "the last, ordinary, was not read to its end and no further");

            CollectionAssert.AreEqual(filter.ToByteArray(), read[0]);
            CollectionAssert.AreEqual(sketch.ToByteArray(), read[1]);
            CollectionAssert.AreEqual(sketch.ToByteArray(), read[2]);
            CollectionAssert.AreEqual(filter.ToByteArray(), read[3]);
        }
    }
}
