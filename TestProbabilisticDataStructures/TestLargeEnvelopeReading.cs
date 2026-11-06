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
        /// at the version it was written at, with its runs widened to the large form's
        /// lengths: so a stored payload of a layout no writer writes now is read in the large
        /// form too. One already in the large form is as it is.
        /// </summary>
        private static byte[] Enlarge(byte[] ordinary, IReadOnlyDictionary<StructureId, Func<byte[], object>> readers) =>
            LargeForm.IsLarge(ordinary)
                ? ordinary
                : LargeForm.Enveloped(ordinary, LargeForm.Widened(ordinary, readers, nestedLarge: false, out _, out _));

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
        /// Every structure in the large form is read in two passes over its stream, with one
        /// move back to the payload's first byte between them. The first, the price, reads
        /// only fields, none longer than the widest, and no byte of a run, and less than the
        /// whole payload. The second, the read, takes every byte of the payload once, in
        /// order, in the pieces its reader asks for: fields, and runs, each whole or in pieces
        /// of at most <see cref="PayloadSource.PieceLength"/>, of exactly the length their
        /// <c>u64</c> prefixes give (#228), and blocks of words with no length before them, in
        /// pieces of at most <see cref="PayloadSource.BlockPieceLength"/> through the span
        /// overload (#289). Then the checksum, once.
        /// </summary>
        /// <remarks>
        /// A reader that held the payload would read it in one piece the length of the
        /// payload, which no prefix gives, and never move back. A price that read a run, or
        /// copied one, would read more than the widest field at once, or the bytes of a run.
        /// The runs are those the second pass reads through the array overload, which is how
        /// a run is read, and the blocks its reads through the span overload longer than a
        /// field, which is how a block is read; the price moves past both. A nested envelope is
        /// read field by field from the outer stream, and
        /// its own runs as runs; a run holding one is left out of the first pass's check,
        /// because the price looks into it, at its header and fields, to price what it
        /// holds. The roster is <see cref="StructureId"/>, through the budget samples, which
        /// are sized so the bulk of each payload is its runs or its counted fields.
        /// <para>
        /// A price reads the fields its structure's price depends on, so a structure whose
        /// bulk were fields its price needs would read them all. <c>SublimeCountMinSketch</c>
        /// was one, let off here with that reason, until version 10 stored the tuning its
        /// packed counters are priced at: its price had read every count to work it out, and
        /// now seeks past them (#280), so no structure is let off. A Sublime payload from
        /// before version 10 still is priced from every byte, and is not one the samples hold,
        /// since they are written at the version each structure writes.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestTheLargeFormIsPricedInAPassThatSeeksOverRunsAndReadInOneThatTakesEachByteOnce()
        {
            var samples = TestReadBudget.Samples.Value;
            var start = (long)PersistenceFormat.LargeHeaderLength;
            long runBytes = 0, movedOver = 0, payloadBytes = 0;

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

                // A run is read whole, as long as its u64 prefix gives, or in pieces of it, none
                // longer than a piece; nothing else is read between the pieces of one run.
                var runs = new List<Asked>();
                var blocks = new List<Asked>();
                var at = start;
                var runLeft = 0L;
                foreach (var piece in read.Where(a => a.From < end))
                {
                    Assert.AreEqual(at, piece.From, $"{id} was read at {piece.From} after the read before it ended at {at}");
                    if (runLeft > 0)
                    {
                        Assert.IsTrue(piece.Array, $"{id} read a field at {piece.From}, inside a run with {runLeft:N0} bytes of it left");
                        Assert.IsLessThanOrEqualTo(Math.Min(runLeft, PayloadSource.PieceLength), piece.Count,
                            $"{id} read {piece.Count:N0} bytes of a run at {piece.From}, past the run or longer than a piece");
                        runLeft -= piece.Count;
                    }
                    else if (piece.Array)
                    {
                        var prefix = (long)BinaryPrimitives.ReadUInt64LittleEndian(large.AsSpan((int)piece.From - 8));
                        Assert.IsTrue(piece.Count == prefix || (piece.Count < prefix && piece.Count <= PayloadSource.PieceLength),
                            $"{id} read {piece.Count:N0} bytes at once at {piece.From}, which is neither the run its u64 prefix " +
                            $"gives, {prefix:N0} bytes, nor a piece of it: it was held");
                        runs.Add(new Asked(false, piece.From, piece.From + prefix, true));
                        runLeft = prefix - piece.Count;
                    }
                    else if (piece.Count > PayloadSource.WidestField)
                    {
                        // Not a field: a piece of a block of words with no length before it, read
                        // through the span overload into a buffer on the stack (#289), and never
                        // longer than a block's piece.
                        Assert.IsLessThanOrEqualTo((long)PayloadSource.BlockPieceLength, piece.Count,
                            $"{id} read {piece.Count:N0} bytes at once at {piece.From} through the span overload, " +
                            "longer than a field and longer than a block's piece: it was held");
                        blocks.Add(piece);
                    }
                    at = piece.To;
                }
                Assert.AreEqual(0L, runLeft, $"{id}'s last run was left with {runLeft:N0} bytes unread");
                Assert.AreEqual(end, at, $"{id} was read to {at}, and its payload ends at {end}");

                var tail = read.Where(a => a.From >= end).ToList();
                Assert.HasCount(1, tail, $"{id}'s checksum was read {tail.Count} times");
                Assert.AreEqual(4L, tail[0].Count, $"{id}'s checksum was read as {tail[0].Count} bytes");

                // The first pass: fields only, past every run, and less than the payload.
                var priced = 0L;
                var bulk = runs.Where(r => !large.AsSpan((int)r.From, 4).SequenceEqual(PersistenceFormat.Magic))
                    .Concat(blocks).ToList();
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

                Assert.IsLessThan(payload, priced, $"{id}'s price read all {payload:N0} bytes of its payload");
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
        /// Every structure's read in the large form allocates at least its ordinary payload's
        /// size less than the ordinary envelope's read of the same structure, which holds it:
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
                var payload = (long)sample.Bytes.Length - PersistenceFormat.EnvelopeLength;

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
            // the length prefix of its run, a u64.
            const int prefix = 4 + 4 + 1 + 8 + 4 + 1;
            Assert.AreEqual((ulong)(payload - prefix - 8), BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(start + prefix)),
                "the bit array's run is not where the layout puts it");
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(start + prefix), (ulong)payload);
            var stream = new Recording(LargeForm.Reseal(bytes));

            var refused = Assert.ThrowsExactly<InvalidDataException>(() => BloomFilter.ReadFrom(stream));
            StringAssert.Contains(refused.Message, $"while reading {payload} more at offset {prefix + 8}.", refused.Message);
            StringAssert.Contains(refused.Message, NotReached, refused.Message);

            var furthest = stream.Log.Where(a => !a.IsMove).Max(a => a.To);
            Assert.IsLessThanOrEqualTo((long)start + prefix + 8, furthest,
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

            // The fields and the bit array's u64 prefix, and ten bytes of the bit array.
            var cut = PersistenceFormat.LargeHeaderLength + 30 + 10;
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
            // the length prefix of its run, a u64.
            const int prefix = 4 + 4 + 1 + 8 + 4 + 1;
            var start = PersistenceFormat.LargeHeaderLength;
            var run = (ulong)Array.MaxLength + 1;
            var claimed = (ulong)prefix + 8 + run;

            var head = bytes[..(start + prefix + 8)];
            BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(start + prefix), run);
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
        /// offsets, with a <c>u64</c> claim where a run's length is one (#228), and with both
        /// checksums repaired. The payloads are the shared samples in the large form and every
        /// stored payload wrapped in it at the version it was written at, its runs widened,
        /// which is how a cuckoo filter's layouts with a run in every entry are reached. The
        /// claims are the int-edge sweep's, and three only a <c>u64</c> can make: past a
        /// <c>u32</c>, and past a <c>long</c>, where a length read as one is negative. A claim
        /// past one array is refused as such by a reader that keeps the run as one, and as the
        /// payload ending by one that reads it in pieces. The roster is
        /// <see cref="StructureId"/>, with the same exemptions.
        /// </remarks>
        [TestMethod]
        public void TestEveryStructureRefusesARunClaimingMoreThanItsLargeFormHolds()
        {
            var samples = LargeForm.Samples();
            var readers = samples.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Read);

            var payloads = samples
                .Select(s => (s.Id, Name: $"the sample {s.Id} payload", s.Read, Bytes: s.Large))
                .Concat(TestPayloadReader.StoredPayloads(readers).Select(p => (p.Id, p.Name, p.Read, Bytes: Enlarge(p.Bytes, readers))))
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
                    var offset = at - start + 8;
                    withRuns.Add(id);

                    var claims = TestPayloadReader.ClaimsAround(offset).Select(c => (ulong)c)
                        .Concat(new[] { (ulong)uint.MaxValue + 1, (ulong)long.MaxValue + 1, ulong.MaxValue });
                    foreach (var claim in claims)
                    {
                        claimsTried++;
                        var ended = $"while reading {claim} more at offset {offset}.";
                        var arrays = claim > (ulong)Array.MaxLength ? "cannot be read into a single array" : ended;
                        try
                        {
                            read(Poked(clean, at, claim));
                            wrong.Add($"offset {offset}, {claim:N0} bytes: read");
                        }
                        catch (InvalidDataException ex) when (ex.Message.Contains(ended, StringComparison.Ordinal)
                            || ex.Message.Contains(arrays, StringComparison.Ordinal))
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

        /// <summary>A large-form envelope with a u64 overwritten in its payload, both checksums repaired.</summary>
        private static byte[] Poked(byte[] envelope, int at, ulong value)
        {
            var bytes = (byte[])envelope.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), value);
            return LargeForm.Reseal(bytes);
        }

        /// <summary>
        /// Where in a large-form envelope the length prefixes of runs are, found as
        /// <see cref="TestPayloadReader"/> finds them in the ordinary one: a u64 claiming more
        /// than the envelope holds is refused as the payload ending, at the offset just after it.
        /// </summary>
        private static List<int> LargeRunPrefixes(byte[] clean, Func<byte[], object> read)
        {
            var found = new List<int>();
            var probe = (ulong)clean.Length;
            var payloadEnd = clean.Length - 4;

            for (var at = PersistenceFormat.LargeHeaderLength; at + 8 <= payloadEnd; at++)
            {
                var stated = BinaryPrimitives.ReadUInt64LittleEndian(clean.AsSpan(at));
                if (stated > (ulong)(payloadEnd - at - 8))
                {
                    continue;
                }

                try
                {
                    read(Poked(clean, at, probe));
                }
                catch (InvalidDataException ex)
                {
                    var offset = at - PersistenceFormat.LargeHeaderLength + 8;
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

        /// <summary>The piece a run is read in while the suite lowers it: eight words.</summary>
        private const int Piece = 64;

        private static T WithPieces<T>(int? piece, Func<T> read)
        {
            var before = PayloadSource.LoweredPieceLength;
            PayloadSource.LoweredPieceLength = piece;
            try
            {
                return read();
            }
            finally
            {
                PayloadSource.LoweredPieceLength = before;
            }
        }

        /// <summary>
        /// The array reads among what a second pass (<see cref="SecondPass"/>) asked of the
        /// stream a payload in the large form, <paramref name="large"/>, is read from, before its
        /// end checksum.
        /// </summary>
        private static List<Asked> ArrayReads(List<Asked> pass, byte[] large) =>
            pass.Where(a => !a.IsMove && a.Array && a.From < large.Length - 4).ToList();

        /// <summary>How many of the reads a second pass asked for were through the span overload.</summary>
        private static int SpanReads(List<Asked> pass) => pass.Count(a => !a.IsMove && !a.Array);

        /// <summary>
        /// Everything the second pass asks of the stream a sample's payload in the large form is
        /// read from, with the piece at <paramref name="piece"/>, or as it ships; the sample has
        /// to read back.
        /// </summary>
        private static List<Asked> SecondPass(TestReadBudget.Sample sample, byte[] large, int? piece)
        {
            var stream = new Recording(large);
            var restored = WithPieces(piece, () => sample.ReadStream(stream, ReadOptions.Unlimited));
            CollectionAssert.AreEqual(sample.Bytes, sample.Write(restored), $"{sample.Name} read in pieces of {piece} is not itself");

            var log = stream.Log;
            var back = log.FindIndex(a => a.IsMove && a.To == PersistenceFormat.LargeHeaderLength);
            Assert.IsGreaterThanOrEqualTo(0, back, $"{sample.Name} was not read in two passes");
            return log.Skip(back + 1).ToList();
        }

        /// <summary>
        /// No run is read from the large form in pieces through the stream's array overload, into
        /// a buffer on the heap: a run read through it is read whole, into the one array its
        /// structure keeps, and claimed a byte past one array at its prefix it is refused as more
        /// than one array holds. Shown with the piece lowered to eight words, below the runs of
        /// the samples, where a run read in pieces through the array overload makes more array
        /// reads than with the piece as it ships. The roster is <see cref="StructureId"/>, through
        /// the budget samples.
        /// </summary>
        /// <remarks>
        /// A run past one array, which the large form's <c>u64</c> lengths allow (#228), cannot
        /// be read through one, and a structure that keeps its run in words, or in more than
        /// one array, can hold one: a quotient filter's table, a count sketch's cells, a
        /// DDSketch's buckets, and an invertible table's counts and hash sums, which #228
        /// names. Each was read in pieces through the array overload, into a buffer of a piece
        /// on the heap, and each is now read through the span overload, from a buffer on the
        /// stack, straight into its storage (#328, #335):
        /// <see cref="TestAQuotientFiltersTableIsReadFromTheLargeFormInPiecesStraightIntoItsTable"/>
        /// and <see cref="TestARunOfWordsIsReadFromTheLargeFormInPiecesStraightIntoItsStorage"/>
        /// hold their pieces. So a run read in pieces through the array overload is one that has
        /// gone back to a buffer on the heap, in one of them or a structure added since. The
        /// lowered piece is seen to take effect: some sample makes more span reads with it than
        /// with the piece as it ships, so that a piece that was never lowered cannot pass the
        /// sweep by showing no array read change.
        /// </remarks>
        [TestMethod]
        public void TestNoRunIsReadFromTheLargeFormInPiecesThroughTheArrayOverload()
        {
            var samples = TestReadBudget.Samples.Value;
            var pieced = new List<string>();
            var whole = new List<(StructureId Id, byte[] Large, long Prefix)>();
            var lowersSpanReads = new List<StructureId>();

            foreach (var (id, sample) in samples)
            {
                var large = TestReadBudget.InTheLargeForm(sample).Bytes;
                var shipped = SecondPass(sample, large, null);
                var lowered = SecondPass(sample, large, Piece);
                if (SpanReads(lowered) > SpanReads(shipped))
                {
                    lowersSpanReads.Add(id);
                }

                var shippedArrays = ArrayReads(shipped, large);
                var loweredArrays = ArrayReads(lowered, large);
                if (loweredArrays.Count != shippedArrays.Count)
                {
                    pieced.Add($"{id}, in {loweredArrays.Count:N0} array reads with the piece lowered and {shippedArrays.Count:N0} " +
                        $"as it ships, the shortest {loweredArrays.Min(a => a.Count):N0} bytes");
                    continue;
                }

                whole.AddRange(loweredArrays.Where(a => a.Count > Piece).Select(a => (id, large, a.From - 8)));
            }

            Assert.IsEmpty(pieced,
                "a run was read in pieces through the stream's array overload, into a buffer on the heap, where a run its structure " +
                $"keeps in storage of its own is read straight into it (#328, #335): {string.Join("; ", pieced)}");
            Assert.IsNotEmpty(lowersSpanReads,
                $"no sample made more span reads with the piece lowered to {Piece} bytes than as it ships, so the piece was not lowered");

            Assert.IsNotEmpty(whole, "no run longer than a piece was read whole, so none was shown kept as one array");
            foreach (var (id, large, prefix) in whole)
            {
                var refused = ClaimedPastOneArray(samples[id], large, prefix);
                StringAssert.Contains(refused.Message, "single array",
                    $"{id}'s run at {prefix + 8}, read whole, was not refused past one array as more than one holds: {refused.Message}");
            }

            StructureRoster.AssertCoversEveryStructure("runs read whole or in pieces", samples.Keys);
        }

        /// <summary>
        /// The refusal of a sample's payload in the large form with the run whose prefix is at
        /// <paramref name="prefix"/> claiming a byte past one array, its checksums repaired.
        /// </summary>
        private static InvalidDataException ClaimedPastOneArray(TestReadBudget.Sample sample, byte[] large, long prefix)
        {
            var claimed = (byte[])large.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(claimed.AsSpan((int)prefix), (ulong)Array.MaxLength + 1);
            return Assert.ThrowsExactly<InvalidDataException>(
                () => sample.Read(LargeForm.Reseal(claimed), ReadOptions.Unlimited),
                $"{sample.Name} with a run claiming a byte past one array was read");
        }

        /// <summary>
        /// The piece a block is read in as it ships (#289). It is on the reader's stack, so it
        /// was chosen small: the shortest power of two from 1 KiB to 64 KiB whose read of a large
        /// count-min sketch from a file stayed within a tenth of a 64 KiB piece's time. A change
        /// to it is a decision about stack use, not something to drift into.
        /// </summary>
        private const int ShippedBlockPiece = 16 * 1024;

        /// <summary>
        /// A count-min sketch's counters, which have no length before them, are read from the
        /// large form in pieces straight into its rows (#289), alone and as the sketch a top-k
        /// holds: through the stream's span overload, every piece as long as a piece but the
        /// last, one after another from the first counter's first byte to the last counter's
        /// last, and nothing else read among them. Shown with the piece as it ships and lowered
        /// to eight words.
        /// </summary>
        /// <remarks>
        /// Read a counter at a time, as they were, the counters asked the stream for eight bytes
        /// each and appended each eight to every checksum carried, the outer one and a nested
        /// one: in #288's round trip past 2 GiB, a top-k holding 2.44 GB of counters took 5.9 s
        /// to load, where a count sketch of 3.0 GB, whose cells #228 reads in pieces, took
        /// 0.9 s. The counters are found as the
        /// bytes the sketch writes after its total count, which nothing else in either payload
        /// repeats, and each sample's are longer than a piece as it ships, so they are read in
        /// more than one at either length.
        /// <para>
        /// The piece as it ships is <see cref="ShippedBlockPiece"/>, stated here rather than
        /// read from the library, so that the test notices it change.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TestACountMinSketchsCountersAreReadFromTheLargeFormInPiecesStraightIntoItsRows()
        {
            var samples = TestReadBudget.Samples.Value;
            foreach (var id in new[] { StructureId.CountMinSketch, StructureId.TopK })
            {
                var sample = samples[id];
                var large = TestReadBudget.InTheLargeForm(sample).Bytes;
                var restored = sample.Read(sample.Bytes, ReadOptions.Unlimited);
                var sketch = restored as CountMinSketch ?? ((TopK)restored).Sketch;

                // The counters as the sketch writes them: the last of its own payload.
                var written = sketch.ToByteArray();
                var counters = written.AsSpan(
                    written.Length - 4 - (int)((long)sketch.Width * sketch.Depth * sizeof(ulong)),
                    (int)((long)sketch.Width * sketch.Depth * sizeof(ulong))).ToArray();
                long at = large.AsSpan().IndexOf(counters);
                Assert.IsGreaterThanOrEqualTo(0L, at, $"{id}'s counters are not in its payload in the large form");
                Assert.AreEqual(at, (long)large.AsSpan().LastIndexOf(counters), $"{id}'s counters appear in its payload twice");
                Assert.IsGreaterThan((long)ShippedBlockPiece, (long)counters.Length,
                    $"{id}'s {counters.Length:N0} bytes of counters are no longer than a piece as it ships");
                var end = at + counters.Length;

                foreach (var piece in new int?[] { null, Piece })
                {
                    var length = (long)(piece ?? ShippedBlockPiece);
                    var reads = SecondPass(sample, large, piece).Where(a => a.From < end && at < a.To).ToList();
                    var expected = new List<Asked>();
                    for (var from = at; from < end; from += length)
                    {
                        expected.Add(new Asked(false, from, Math.Min(from + length, end), false));
                    }

                    Console.WriteLine(
                        $"[COUNTERS] {id}: {counters.Length:N0} bytes at {at:N0}, piece {length:N0}: " +
                        $"{reads.Count:N0} reads, the first {reads[0].Count:N0} bytes");
                    CollectionAssert.AreEqual(expected, reads,
                        $"{id}'s {counters.Length:N0} bytes of counters at {at:N0}, with the piece at {length:N0}, were read in " +
                        $"{reads.Count:N0} reads, the first of {reads[0].Count:N0} bytes through the " +
                        $"{(reads[0].Array ? "array" : "span")} overload, and not in {expected.Count:N0} pieces through the span overload");
                }
            }
        }

        /// <summary>
        /// A <see cref="MementoFilter"/>'s table, whose words have no length before them, is read
        /// from the large form in pieces straight into its segment's table (#327), as an Infini
        /// filter's is (#313): through the stream's span overload, every piece as long as a piece
        /// but the last, one after another from the table's first byte to its last, and nothing
        /// else read among them. Shown with the piece as it ships and lowered to eight words.
        /// </summary>
        /// <remarks>
        /// Before #327 the words were read a word at a time into an array of their own, eight
        /// bytes asked of the stream and appended to the checksum for each, and then copied into
        /// the table. The table is found as the bytes its words are written as, which nothing
        /// else in the payload repeats; the budget sample is one table longer than a piece as it
        /// ships, so it is read in more than one at either length.
        /// </remarks>
        [TestMethod]
        public void TestAMementoFiltersTableIsReadFromTheLargeFormInPiecesStraightIntoItsSegment()
        {
            var sample = TestReadBudget.Samples.Value[StructureId.MementoFilter];
            var large = TestReadBudget.InTheLargeForm(sample).Bytes;
            var filter = (MementoFilter)sample.Read(sample.Bytes, ReadOptions.Unlimited);
            Assert.HasCount(1, filter.Segments, "premise: the sample is not one table");

            var words = filter.Segments[0].Data;
            var table = new byte[words.Length * sizeof(ulong)];
            for (var w = 0; w < words.Length; w++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(w * sizeof(ulong)), words[w]);
            }

            long at = large.AsSpan().IndexOf(table);
            Assert.IsGreaterThanOrEqualTo(0L, at, "the table is not in the payload in the large form");
            Assert.AreEqual(at, (long)large.AsSpan().LastIndexOf(table), "the table appears in the payload twice");
            Assert.IsGreaterThan((long)ShippedBlockPiece, (long)table.Length,
                $"the table's {table.Length:N0} bytes are no longer than a piece as it ships");
            var end = at + table.Length;

            foreach (var piece in new int?[] { null, Piece })
            {
                var length = (long)(piece ?? ShippedBlockPiece);
                var reads = SecondPass(sample, large, piece).Where(a => a.From < end && at < a.To).ToList();
                var expected = new List<Asked>();
                for (var from = at; from < end; from += length)
                {
                    expected.Add(new Asked(false, from, Math.Min(from + length, end), false));
                }

                Console.WriteLine(
                    $"[TABLE] MementoFilter: {table.Length:N0} bytes at {at:N0}, piece {length:N0}: " +
                    $"{reads.Count:N0} reads, the first {reads[0].Count:N0} bytes");
                CollectionAssert.AreEqual(expected, reads,
                    $"the table's {table.Length:N0} bytes at {at:N0}, with the piece at {length:N0}, were read in " +
                    $"{reads.Count:N0} reads, the first of {reads[0].Count:N0} bytes through the " +
                    $"{(reads[0].Array ? "array" : "span")} overload, and not in {expected.Count:N0} pieces through the span overload");
            }
        }

        /// <summary>
        /// An <see cref="InfiniFilter"/>'s table, whose words have no length before them, is read
        /// from the large form in pieces straight into its segment's table (#313), as a count-min
        /// sketch's counters are: through the stream's span overload, every piece as long as a
        /// piece but the last, one after another from the table's first byte to its last, and
        /// nothing else read among them. Shown with the piece as it ships and lowered to eight
        /// words.
        /// </summary>
        /// <remarks>
        /// Before #313 the words were read a word at a time, eight bytes asked of the stream and
        /// appended to the checksum for each. The table is found as the bytes its words are
        /// written as, which nothing else in the payload repeats; the budget sample is one table
        /// longer than a piece as it ships, so it is read in more than one at either length.
        /// </remarks>
        [TestMethod]
        public void TestAnInfiniFiltersTableIsReadFromTheLargeFormInPiecesStraightIntoItsSegment()
        {
            var sample = TestReadBudget.Samples.Value[StructureId.InfiniFilter];
            var large = TestReadBudget.InTheLargeForm(sample).Bytes;
            var filter = (InfiniFilter)sample.Read(sample.Bytes, ReadOptions.Unlimited);
            Assert.HasCount(1, filter.Segments, "premise: the sample is not one table");

            var words = filter.Segments[0].Data;
            var table = new byte[words.Length * sizeof(ulong)];
            for (var w = 0; w < words.Length; w++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(w * sizeof(ulong)), words[w]);
            }

            long at = large.AsSpan().IndexOf(table);
            Assert.IsGreaterThanOrEqualTo(0L, at, "the table is not in the payload in the large form");
            Assert.AreEqual(at, (long)large.AsSpan().LastIndexOf(table), "the table appears in the payload twice");
            Assert.IsGreaterThan((long)ShippedBlockPiece, (long)table.Length,
                $"the table's {table.Length:N0} bytes are no longer than a piece as it ships");
            var end = at + table.Length;

            foreach (var piece in new int?[] { null, Piece })
            {
                var length = (long)(piece ?? ShippedBlockPiece);
                var reads = SecondPass(sample, large, piece).Where(a => a.From < end && at < a.To).ToList();
                var expected = new List<Asked>();
                for (var from = at; from < end; from += length)
                {
                    expected.Add(new Asked(false, from, Math.Min(from + length, end), false));
                }

                Console.WriteLine(
                    $"[TABLE] InfiniFilter: {table.Length:N0} bytes at {at:N0}, piece {length:N0}: " +
                    $"{reads.Count:N0} reads, the first {reads[0].Count:N0} bytes");
                CollectionAssert.AreEqual(expected, reads,
                    $"the table's {table.Length:N0} bytes at {at:N0}, with the piece at {length:N0}, were read in " +
                    $"{reads.Count:N0} reads, the first of {reads[0].Count:N0} bytes through the " +
                    $"{(reads[0].Array ? "array" : "span")} overload, and not in {expected.Count:N0} pieces through the span overload");
            }
        }

        /// <summary>
        /// A <see cref="QuotientFilter"/>'s table, one run of words, is read from the large form
        /// in pieces straight into the table (#328), as an Infini filter's tables and a count-min
        /// sketch's counters are: through the stream's span overload, every piece as long as a
        /// piece but the last, one after another from the table's first byte to its last, and
        /// nothing else read among them. Shown with the piece as it ships and lowered to eight
        /// words. Claimed a byte past one array at its <c>u64</c> prefix, the run is refused for
        /// the bytes the stream lacks, not as more than one array holds, since no array of its
        /// length is made.
        /// </summary>
        /// <remarks>
        /// Before #328 the run was read through the stream's array overload, into a buffer on the
        /// heap as long as a piece of a run (64 KiB) or as the run, whichever was shorter. The
        /// table is found as the bytes its words are written as, which nothing else in the payload
        /// repeats, with the run's length just before them; the budget sample's table is longer
        /// than a piece as it ships, so it is read in more than one at either length.
        /// </remarks>
        [TestMethod]
        public void TestAQuotientFiltersTableIsReadFromTheLargeFormInPiecesStraightIntoItsTable()
        {
            var sample = TestReadBudget.Samples.Value[StructureId.QuotientFilter];
            var large = TestReadBudget.InTheLargeForm(sample).Bytes;
            var filter = (QuotientFilter)sample.Read(sample.Bytes, ReadOptions.Unlimited);

            var words = (ulong[])typeof(QuotientFilter)
                .GetField("data", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(filter)!;
            var table = new byte[words.Length * sizeof(ulong)];
            for (var w = 0; w < words.Length; w++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(w * sizeof(ulong)), words[w]);
            }

            long at = large.AsSpan().IndexOf(table);
            Assert.IsGreaterThanOrEqualTo(0L, at, "the table is not in the payload in the large form");
            Assert.AreEqual(at, (long)large.AsSpan().LastIndexOf(table), "the table appears in the payload twice");
            Assert.AreEqual((ulong)table.Length, BinaryPrimitives.ReadUInt64LittleEndian(large.AsSpan((int)at - 8)),
                "the table is not the run whose u64 length is just before it");
            Assert.IsGreaterThan((long)ShippedBlockPiece, (long)table.Length,
                $"the table's {table.Length:N0} bytes are no longer than a piece as it ships");
            var end = at + table.Length;

            foreach (var piece in new int?[] { null, Piece })
            {
                var length = (long)(piece ?? ShippedBlockPiece);
                var reads = SecondPass(sample, large, piece).Where(a => a.From < end && at < a.To).ToList();
                var expected = new List<Asked>();
                for (var from = at; from < end; from += length)
                {
                    expected.Add(new Asked(false, from, Math.Min(from + length, end), false));
                }

                Console.WriteLine(
                    $"[TABLE] QuotientFilter: {table.Length:N0} bytes at {at:N0}, piece {length:N0}: " +
                    $"{reads.Count:N0} reads, the first {reads[0].Count:N0} bytes");
                CollectionAssert.AreEqual(expected, reads,
                    $"the table's {table.Length:N0} bytes at {at:N0}, with the piece at {length:N0}, were read in " +
                    $"{reads.Count:N0} reads, the first of {reads[0].Count:N0} bytes through the " +
                    $"{(reads[0].Array ? "array" : "span")} overload, and not in {expected.Count:N0} pieces through the span overload");
            }

            var refused = ClaimedPastOneArray(sample, large, at - 8);
            Assert.DoesNotContain("single array", refused.Message,
                $"the table's run, read in pieces, was refused past one array as if read into one: {refused.Message}");
            StringAssert.Contains(refused.Message, $"while reading {(long)Array.MaxLength + 1} more at offset",
                $"the table's run claiming a byte past one array was not refused for the bytes the stream lacks: {refused.Message}");
        }

        /// <summary>
        /// A count sketch's counters, a DDSketch's buckets, and an invertible table's counts and
        /// hash sums, each one run of words, are read from the large form in pieces straight into
        /// the storage their structure keeps (#335), as a quotient filter's table is: through the
        /// stream's span overload, one piece after another from the run's first byte to its last,
        /// every piece as long as a piece but the last, and nothing else read among them. A count
        /// sketch's run is read into its rows one row at a time, so its pieces start again at
        /// each row. Shown with the piece as it ships and lowered to eight words. Claimed a byte
        /// past one array at its <c>u64</c> prefix, each run is refused for the bytes the stream
        /// lacks, not as more than one array holds, since no array of its length is made.
        /// </summary>
        /// <remarks>
        /// Before #335 each run was read through the stream's array overload, into a buffer on
        /// the heap as long as a piece of a run (64 KiB) or as the run, whichever was shorter. The
        /// runs are found where FORMAT.md lays them out, and the layout checks itself: each run
        /// is as long as its <c>u64</c> prefix says, and the last ends where the payload does. An
        /// invertible table's key sums lie between its two runs of words, and are kept as the
        /// array they are read into, so they are not among the runs here. The budget samples' runs
        /// that hold anything, and a count sketch's rows, are longer than a piece as it ships, so
        /// each is read in more than one at either length; the DDSketch sample's negative store is
        /// empty, and is read as nothing.
        /// </remarks>
        [TestMethod]
        [DataRow(nameof(StructureId.CountSketch))]
        [DataRow(nameof(StructureId.DDSketch))]
        [DataRow(nameof(StructureId.InvertibleBloomLookupTable))]
        public void TestARunOfWordsIsReadFromTheLargeFormInPiecesStraightIntoItsStorage(string structure)
        {
            var id = Enum.Parse<StructureId>(structure);
            var sample = TestReadBudget.Samples.Value[id];
            var large = TestReadBudget.InTheLargeForm(sample).Bytes;
            var payloadEnd = (long)large.Length - (PersistenceFormat.LargeEnvelopeLength - PersistenceFormat.LargeHeaderLength);
            const long Header = PersistenceFormat.LargeHeaderLength;

            long LengthAt(long prefix) => (long)BinaryPrimitives.ReadUInt64LittleEndian(large.AsSpan((int)prefix));

            // Each run of words: where its u64 prefix is, and the lengths it is read into storage
            // in, one after another.
            var runs = new List<(long Prefix, long[] Reads)>();
            long last;
            switch (sample.Read(sample.Bytes, ReadOptions.Unlimited))
            {
                case CountSketch sketch:
                {
                    // u32 width, u32 depth, then the cells, row by row.
                    var row = (long)sketch.Width() * sizeof(long);
                    runs.Add((Header + 8, Enumerable.Repeat(row, (int)sketch.Depth()).ToArray()));
                    last = Header + 16 + LengthAt(Header + 8);
                    break;
                }

                case DDSketch:
                {
                    // Five eight-byte fields, then each store: an i32 index and its run.
                    var positive = Header + 40 + 4;
                    var negative = positive + 8 + LengthAt(positive) + 4;
                    runs.Add((positive, [LengthAt(positive)]));
                    runs.Add((negative, [LengthAt(negative)]));
                    last = negative + 8 + LengthAt(negative);
                    break;
                }

                case InvertibleBloomLookupTable table:
                {
                    // u32 cells, u32 key size, then the counts, the key sums and the hash sums.
                    var words = (long)table.Cells() * sizeof(long);
                    var keys = Header + 16 + words;
                    var hashes = keys + 8 + LengthAt(keys);
                    Assert.AreEqual((long)table.Cells() * table.KeySize(), LengthAt(keys), "the key sums are not where FORMAT.md lays them");
                    runs.Add((Header + 8, [words]));
                    runs.Add((hashes, [words]));
                    last = hashes + 8 + LengthAt(hashes);
                    break;
                }

                default:
                    throw new AssertFailedException($"{structure} is not one of the structures this test reads");
            }

            Assert.AreEqual(payloadEnd, last, $"{structure}'s runs do not end where its payload does, so are not where FORMAT.md lays them");
            var read = 0;
            foreach (var (prefix, lengths) in runs)
            {
                Assert.AreEqual(lengths.Sum(), LengthAt(prefix), $"{structure}'s run at {prefix + 8:N0} is not as long as its prefix says");
                if (lengths.Sum() == 0)
                {
                    continue;
                }

                read++;
                Assert.IsGreaterThan((long)ShippedBlockPiece, lengths.Min(),
                    $"{structure}'s run at {prefix + 8:N0} is read into storage {lengths.Min():N0} bytes at a time, no longer than a piece as it ships");
                var start = prefix + 8;
                var end = start + lengths.Sum();

                foreach (var piece in new int?[] { null, Piece })
                {
                    var length = (long)(piece ?? ShippedBlockPiece);
                    var reads = SecondPass(sample, large, piece).Where(a => a.From < end && start < a.To).ToList();
                    var expected = new List<Asked>();
                    var into = start;
                    foreach (var part in lengths)
                    {
                        for (var from = into; from < into + part; from += length)
                        {
                            expected.Add(new Asked(false, from, Math.Min(from + length, into + part), false));
                        }

                        into += part;
                    }

                    Console.WriteLine(
                        $"[WORDS] {structure}: {lengths.Sum():N0} bytes at {start:N0}, piece {length:N0}: " +
                        $"{reads.Count:N0} reads, the first {reads[0].Count:N0} bytes");
                    CollectionAssert.AreEqual(expected, reads,
                        $"{structure}'s run of {lengths.Sum():N0} bytes at {start:N0}, with the piece at {length:N0}, was read in " +
                        $"{reads.Count:N0} reads, the first of {reads[0].Count:N0} bytes through the " +
                        $"{(reads[0].Array ? "array" : "span")} overload, and not in {expected.Count:N0} pieces through the span overload");
                }

                var refused = ClaimedPastOneArray(sample, large, prefix);
                Assert.DoesNotContain("single array", refused.Message,
                    $"{structure}'s run at {start:N0}, read in pieces, was refused past one array as if read into one: {refused.Message}");
                StringAssert.Contains(refused.Message, $"while reading {(long)Array.MaxLength + 1} more at offset",
                    $"{structure}'s run at {start:N0} claiming a byte past one array was not refused for the bytes the stream lacks: {refused.Message}");
            }

            Assert.IsGreaterThan(0, read, $"{structure}'s sample holds no run of words to read");
        }

        /// <summary>
        /// Reading a count-min sketch's counters in pieces (#289) charges what reading them a
        /// counter at a time did, and reads back the sketch that writes the same bytes: in the
        /// ordinary envelope and in the large form, with the piece as it ships and lowered to
        /// eight words, the reader charges exactly its price, and a count-min sketch's price is
        /// its envelope and its rows. Shown at the coarsest sketch, whose counters are shorter
        /// than either piece; at one of 273 counters, 2,184 bytes, whose last lowered piece is a
        /// single counter, the least a piece can hold, so that a read stopping a counter short of
        /// the end shows; at the budget samples, whose counters are longer than a piece as it
        /// ships; at a top-k,
        /// whose sketch is nested; and at the stored fixtures of the version the sketch writes,
        /// which must write back as stored.
        /// </summary>
        /// <remarks>
        /// The counters were read straight from the payload when it was held and through the
        /// source's buffer for a field when it was not, so nothing was charged for them but the
        /// rows. A buffer for the pieces on the heap would be an allocation, and a charge, that
        /// the read did not make before; with the piece lowered there are many more pieces, so a
        /// charge for each would show there if not at the piece as it ships.
        /// </remarks>
        [TestMethod]
        public void TestReadingACountMinSketchInPiecesChargesWhatItDidAndWritesTheSameBytes()
        {
            var samples = TestReadBudget.Samples.Value;
            var sketches = new List<(string Name, byte[] Bytes)>
            {
                ("the coarsest sketch", Filled(new CountMinSketch(0.5, 0.5), 10).ToByteArray()),
                ("a sketch of 273 counters", Filled(new CountMinSketch(0.03, 0.1), 300).ToByteArray()),
                ("the budget sample", samples[StructureId.CountMinSketch].Bytes),
                ("the narrow version 9 fixture", TestLargeEnvelope.ReadFixture("countminsketch-v9-narrow.bin")),
                ("the wide version 9 fixture", TestLargeEnvelope.ReadFixture("countminsketch-v9-wide.bin")),
            };

            var spans = new List<long>();
            foreach (var (name, bytes) in sketches)
            {
                var sketch = CountMinSketch.ReadFrom(new MemoryStream(bytes), ReadOptions.Unlimited);
                var rows = ReadBudget.Sum(
                    ReadBudget.ArrayOf(sketch.Depth, sizeof(long)),
                    ReadBudget.Times(sketch.Depth, ReadBudget.ArrayOf(sketch.Width, sizeof(ulong))));

                var ordinary = bytes;
                var large = LargeForm.WriteLarge(sketch);
                spans.Add((long)sketch.Width * sketch.Depth * sizeof(ulong));

                foreach (var (form, payload, envelope) in new[]
                {
                    ("the ordinary envelope", ordinary,
                        PersistenceFormat.EnvelopeCost((ulong)(ordinary.Length - PersistenceFormat.EnvelopeLength))),
                    ("the large form", large, PersistenceFormat.LargeEnvelopeCost),
                })
                {
                    foreach (var piece in new int?[] { null, Piece })
                    {
                        var budget = new ReadBudget(long.MaxValue, StructureId.CountMinSketch);
                        var read = WithPieces(piece, () => CountMinSketch.ReadFrom(new MemoryStream(payload), new ReadOptions(budget)));
                        var where = $"{name} in {form}, with the piece at {piece ?? PayloadSource.BlockPieceLength}";

                        Assert.AreEqual(envelope + rows, budget.Charged, $"{where} was priced at {budget.Charged}, not its envelope and rows");
                        Assert.AreEqual(budget.Charged, budget.Spent, $"{where} was priced at {budget.Charged} and its reader charged {budget.Spent}");
                        CollectionAssert.AreEqual(payload,
                            form == "the large form" ? LargeForm.WriteLarge(read) : read.ToByteArray(),
                            $"{where} does not write the bytes it was read from");
                    }
                }
            }

            // A top-k's charges are its own as well as its sketch's, so they are held to its price
            // and to themselves at either piece.
            var topK = samples[StructureId.TopK];
            foreach (var bytes in new[] { topK.Bytes, TestReadBudget.InTheLargeForm(topK).Bytes })
            {
                var spent = new List<long>();
                foreach (var piece in new int?[] { null, Piece })
                {
                    var budget = new ReadBudget(long.MaxValue, StructureId.TopK);
                    var read = WithPieces(piece, () => topK.Read(bytes, new ReadOptions(budget)));
                    Assert.AreEqual(budget.Charged, budget.Spent, $"the top-k, with the piece at {piece}, was priced at {budget.Charged} and charged {budget.Spent}");
                    CollectionAssert.AreEqual(bytes, LargeForm.IsLarge(bytes) ? LargeForm.WriteLarge(read) : topK.Write(read),
                        $"the top-k, with the piece at {piece}, does not write the bytes it was read from");
                    spent.Add(budget.Spent);
                }

                Assert.AreEqual(spent[0], spent[1], $"the top-k was charged {spent[0]} with the piece as it ships and {spent[1]} lowered");
            }

            // The counters straddled every piece boundary the claims are about.
            Assert.IsTrue(spans.Any(s => s < Piece), "no sketch's counters were shorter than a lowered piece");
            Assert.IsTrue(spans.Any(s => s % Piece == sizeof(ulong) && s > Piece), "no sketch's counters ended a single counter into a lowered piece");
            Assert.IsTrue(spans.Any(s => s > PayloadSource.BlockPieceLength), "no sketch's counters were longer than a piece as it ships");
        }

        private static CountMinSketch Filled(CountMinSketch sketch, int count)
        {
            for (var i = 0; i < count; i++)
            {
                sketch.Add(Key($"cms-{i}"));
            }

            return sketch;
        }

        /// <summary>
        /// A DDSketch keeps each store's counts in one array, so a run of more counts than an
        /// array holds, which the large form's <c>u64</c> length lets a payload claim (#228), is
        /// refused as that, by name, with nothing its size allocated; a run of exactly as many
        /// counts is a span the store takes, and one of 2^32 counts from the lowest index is
        /// refused rather than read as none.
        /// </summary>
        /// <remarks>
        /// A sketch of one value, in the large form, its positive store's run claiming a count
        /// past one array and its header the payload that makes, on a stream that claims as
        /// much and holds zeros past the run's length. Read unlimited, so unpriced, the run is
        /// begun, with nothing allocated for it, since its words would be read straight into the
        /// window (#335), and refused before the window is made; before #335 a heap piece was
        /// allocated for it too. Without the check
        /// the span passes the store's index check, and the read goes on to make a window of
        /// that many counts, which no array holds. The edges are the span itself, worked out
        /// by the store's own reckoning, since a run of as many counts as an array holds is
        /// seventeen gigabytes to read; from <c>int.MinValue</c>, a span of 2^32 ends at
        /// <c>int.MaxValue</c>, and in an <c>int</c> it is nought.
        /// </remarks>
        [TestMethod]
        public void TestADDSketchRunOfMoreCountsThanOneArrayHoldsIsRefusedAsThat()
        {
            var sketch = new DDSketch(0.01);
            sketch.Add(1.0);
            var bytes = LargeForm.WriteLarge(sketch);

            // Five eight-byte fields, then the positive store's lowest index and its run.
            const int prefix = PersistenceFormat.LargeHeaderLength + 5 * 8 + 4;
            var minIndex = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(prefix - 4));
            var run = (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(prefix));
            Assert.AreEqual(8L, run, "the sketch of one value does not hold one count");
            var counts = (long)Array.MaxLength + 1;
            Assert.IsLessThanOrEqualTo((long)int.MaxValue, minIndex + counts - 1,
                "the claimed span runs past the largest index, which refuses it for that instead");

            var payload = bytes.Length - PersistenceFormat.LargeEnvelopeLength - run + 8 * counts;
            var claiming = (byte[])bytes.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(claiming.AsSpan(prefix), (ulong)(8 * counts));
            BinaryPrimitives.WriteUInt64LittleEndian(claiming.AsSpan(10), (ulong)payload);
            var head = SealHeader(claiming)[..(prefix + 8)];

            InvalidDataException refused = null;
            var allocated = TestReadBudget.Allocated(() => refused = Assert.ThrowsExactly<InvalidDataException>(
                () => DDSketch.ReadFrom(new Sparse(head, PersistenceFormat.LargeEnvelopeLength + payload), ReadOptions.Unlimited),
                $"a DDSketch whose store claims {counts:N0} counts was read"));
            StringAssert.Contains(refused.Message, $"are {counts} counts, more than the one array", refused.Message);
            Assert.IsLessThan(PayloadSource.PieceLength + 64L * 1024, allocated, $"the refusal allocated {allocated:N0} bytes");

            var spanOf = typeof(DDSketch).GetNestedType("Store", System.Reflection.BindingFlags.NonPublic)!
                .GetMethod("SpanOf", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            int SpanOf(int from, long length) => (int)spanOf.Invoke(null, [from, length, "positive"])!;
            void Refused(int from, long length, string why)
            {
                var thrown = Assert.ThrowsExactly<System.Reflection.TargetInvocationException>(() => SpanOf(from, length), why);
                Assert.IsInstanceOfType<InvalidDataException>(thrown.InnerException, why);
                StringAssert.Contains(thrown.InnerException.Message, "more than the one array", why);
            }

            Assert.AreEqual(Array.MaxLength, SpanOf(0, 8L * Array.MaxLength), "a span of one array's counts was refused");
            Refused(0, 8 * counts, "a span of a count past one array was taken");
            Refused(int.MinValue, 8L << 32, "a span of 2^32 counts from the lowest index was taken");
        }

        /// <summary>
        /// A count sketch's, a DDSketch's and an invertible table's price takes a run of words
        /// past one array, which the large form allows and each structure can hold (#228), as
        /// its reader does: it prices exactly the storage the reader reads the words into, and
        /// does not refuse the run as more than one array holds (#335). Each payload claims runs
        /// of more than 2^31 bytes, their lengths where FORMAT.md lays them out and zeros
        /// everywhere else, on a stream that claims them all.
        /// </summary>
        /// <remarks>
        /// A price that refused such a run would not show in a read of a payload this library
        /// writes, or in its refusal. A refused price is set aside, and the read is admitted at
        /// what had been priced by then. Its reader then charges the rest, so a budget too small
        /// for the whole is refused by the reader instead of by the price, naming the same total,
        /// after the payload's fields are read a second time. An invertible table's reader would
        /// first allocate its counts and key sums, gigabytes, before reaching the hash sums. So
        /// the price is walked here on its own, over a reader of the large form.
        /// </remarks>
        [TestMethod]
        [DataRow(nameof(StructureId.CountSketch))]
        [DataRow(nameof(StructureId.DDSketch))]
        [DataRow(nameof(StructureId.InvertibleBloomLookupTable))]
        public void TestAPriceTakesARunOfWordsPastOneArrayAsItsReaderDoes(string structure)
        {
            var id = Enum.Parse<StructureId>(structure);
            var held = new List<(long At, byte[] Bytes)>();
            void Field(long at, ulong value, int width)
            {
                var bytes = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
                held.Add((at, bytes[..width]));
            }

            long length;
            long longest;
            long storage;
            PayloadPrice price;
            switch (id)
            {
                case StructureId.CountSketch:
                {
                    // u32 width, u32 depth, and the cells: three rows of 2^27 counters, 3 GiB.
                    const uint width = 1u << 27, depth = 3;
                    longest = (long)width * depth * sizeof(long);
                    Field(0, width, 4);
                    Field(4, depth, 4);
                    Field(8, (ulong)longest, 8);
                    length = 16 + longest;
                    storage = ReadBudget.Sum(
                        ReadBudget.ArrayOf(depth, sizeof(long)), ReadBudget.Times(depth, ReadBudget.ArrayOf(width, sizeof(long))));
                    price = CountSketch.Price;
                    break;
                }

                case StructureId.DDSketch:
                {
                    // Five eight-byte fields, a positive store of 3 x 10^8 buckets from index 0,
                    // 2.4 GB, and an empty negative store.
                    const long counts = 300_000_000;
                    longest = counts * sizeof(ulong);
                    Field(44, (ulong)longest, 8);
                    length = 52 + longest + 12;
                    storage = ReadBudget.ArrayOf((ulong)counts, sizeof(ulong));
                    price = DDSketch.Price;
                    break;
                }

                case StructureId.InvertibleBloomLookupTable:
                {
                    // u32 cells, u32 key size, then the counts, the key sums and the hash sums:
                    // 3 x 10^8 cells of one-byte keys, 2.4 GB each of counts and of hash sums.
                    const uint cells = 300_000_000;
                    longest = (long)cells * sizeof(long);
                    var keys = 16 + longest;
                    var hashes = keys + 8 + cells;
                    Field(0, cells, 4);
                    Field(4, 1, 4);
                    Field(8, (ulong)longest, 8);
                    Field(keys, cells, 8);
                    Field(hashes, (ulong)longest, 8);
                    length = hashes + 8 + longest;
                    storage = ReadBudget.Sum(
                        ReadBudget.ArrayOf(cells, sizeof(long)), ReadBudget.ArrayOf(cells, 1), ReadBudget.ArrayOf(cells, sizeof(ulong)));
                    price = InvertibleBloomLookupTable.Price;
                    break;
                }

                default:
                    throw new AssertFailedException($"{structure} is not one of the structures this test prices");
            }

            Assert.IsGreaterThan((long)Array.MaxLength, longest, $"premise: {structure}'s run is not past one array");
            var budget = new ReadBudget(long.MaxValue, id);
            var reader = new PayloadReader(new PayloadSource(new Patched(length, held)), length, budget, wide: true, streamed: true);
            price(ref reader, PersistenceFormat.ReadableVersions(id).Max());
            Assert.AreEqual(storage, budget.Charged,
                $"{structure}'s price priced {budget.Charged:N0} bytes, and its reader charges {storage:N0} for the storage it reads the words into");
        }

        /// <summary>
        /// A stream that can seek and claims <paramref name="length"/> bytes: zeros, never held,
        /// but for the bytes <paramref name="held"/> gives at each offset. So a payload whose runs
        /// are longer than the suite can build has each run's length where its layout puts it.
        /// </summary>
        private sealed class Patched(long length, IReadOnlyList<(long At, byte[] Bytes)> held) : Stream
        {
            private long position;

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
                var read = buffer[..(int)Math.Min(buffer.Length, Math.Max(0, length - this.position))];
                read.Clear();
                foreach (var (at, bytes) in held)
                {
                    var from = Math.Max(at, this.position);
                    var to = Math.Min(at + bytes.Length, this.position + read.Length);
                    if (from < to)
                    {
                        bytes.AsSpan((int)(from - at), (int)(to - from)).CopyTo(read[(int)(from - this.position)..]);
                    }
                }

                this.position += read.Length;
                return read.Length;
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

        /// <summary>
        /// A run shorter than a piece is read through a buffer of its own length, not of a
        /// piece's: a small one of each structure #228 names, read from the large form,
        /// allocates less than one piece in all.
        /// </summary>
        [TestMethod]
        public void TestARunShorterThanAPieceIsReadThroughABufferOfItsOwnLength()
        {
            var quotient = new QuotientFilter(100, 0.01);
            var countSketch = new CountSketch(0.5, 0.5);
            var dd = new DDSketch(0.01);
            var table = new InvertibleBloomLookupTable(4, 4);
            for (var i = 0; i < 20; i++)
            {
                quotient.Add(Key($"q{i}"));
                countSketch.Add(Key($"c{i}"), i % 5 - 2);
                dd.Add(1.0 + i);
                table.Add([(byte)(i + 1), 0, 0, 0]);
            }

            foreach (var (name, structure, read) in new (string, object, Func<Stream, object>)[]
            {
                ("QuotientFilter", quotient, s => QuotientFilter.ReadFrom(s, ReadOptions.Unlimited)),
                ("CountSketch", countSketch, s => CountSketch.ReadFrom(s, ReadOptions.Unlimited)),
                ("DDSketch", dd, s => DDSketch.ReadFrom(s, ReadOptions.Unlimited)),
                ("InvertibleBloomLookupTable", table, s => InvertibleBloomLookupTable.ReadFrom(s, ReadOptions.Unlimited)),
            })
            {
                var large = LargeForm.WriteLarge(structure);
                Assert.IsLessThan((long)PayloadSource.PieceLength, (long)large.Length, $"the {name} is not shorter than a piece");
                var allocated = TestReadBudget.Allocated(() => read(new MemoryStream(large, writable: false)));
                Assert.IsLessThan((long)PayloadSource.PieceLength, allocated,
                    $"the {name}, {large.Length:N0} bytes in the large form, allocated {allocated:N0} reading it: a piece for a shorter run");
            }
        }

        /// <summary>
        /// A slice of a payload reads as the payload it is cut from: a run's length as wide,
        /// and held or streamed alike, so that a nested envelope's payload, sliced from the
        /// large form, reads its runs in its own form and in pieces, and a nested envelope
        /// inside it from the stream. Each both ways.
        /// </summary>
        [TestMethod]
        public void TestASliceReadsAsThePayloadItIsCutFrom()
        {
            byte[] wide = [3, 0, 0, 0, 0, 0, 0, 0, 7, 8, 9];
            byte[] narrow = [3, 0, 0, 0, 7, 8, 9];
            Assert.IsTrue(SliceReads(wide, true).SequenceEqual(new byte[] { 7, 8, 9 }), "a slice of the large form read a run's length as a u32");
            Assert.IsTrue(SliceReads(narrow, false).SequenceEqual(new byte[] { 7, 8, 9 }), "a slice of the ordinary envelope read a run's length as a u64");

            foreach (var streamed in new[] { true, false })
            {
                var reader = new PayloadReader(new PayloadSource(new MemoryStream(wide)), wide.Length, Unlimited(), wide: true, streamed: streamed);
                Assert.AreEqual(streamed, reader.Slice(wide.Length).IsStreamed,
                    $"a slice of a reader that is {(streamed ? "" : "not ")}streamed is {(streamed ? "not " : "")}streamed");
            }
        }

        /// <summary>
        /// A piece of a block (#289) is held to what its reader's payload has left, as a word
        /// was, and is the bytes where the reader is, held or streamed: a slice of eight bytes
        /// is refused a piece of nine as ending there, though the stream under it goes on, and
        /// then reads its eight, to its end.
        /// </summary>
        /// <remarks>
        /// A count-min sketch holds its counters to what remains before it reads any, so no
        /// payload of one reaches the refusal; this is the one place it is shown.
        /// </remarks>
        [TestMethod]
        public void TestAPieceOfABlockIsHeldToWhatItsPayloadHasLeft()
        {
            byte[] bytes = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
            foreach (var streamed in new[] { false, true })
            {
                var form = streamed ? "streamed" : "held";
                var reader = streamed
                    ? new PayloadReader(new PayloadSource(new MemoryStream(bytes)), bytes.Length, Unlimited(), wide: true, streamed: true)
                    : new PayloadReader(bytes, Unlimited(), wide: true);
                reader.ReadByte();
                var slice = reader.Slice(8);

                InvalidDataException refused = null;
                try
                {
                    slice.ReadBlock(new byte[9]);
                }
                catch (InvalidDataException e)
                {
                    refused = e;
                }

                Assert.IsNotNull(refused, $"a {form} slice of eight bytes read a piece of nine");
                StringAssert.Contains(refused.Message, "ended", refused.Message);

                var piece = new byte[8];
                slice.ReadBlock(piece);
                CollectionAssert.AreEqual(bytes[1..9], piece, $"a {form} slice read a piece that is not its bytes");
                slice.ExpectEnd();
            }
        }

        private static byte[] SliceReads(byte[] bytes, bool wide)
        {
            var reader = new PayloadReader(bytes, Unlimited(), wide);
            var slice = reader.Slice(bytes.Length);
            return slice.ReadBytes();
        }

        /// <summary>
        /// A top-k whose count-min sketch's envelope is about two megabytes, and that envelope's
        /// length, and its payload's.
        /// </summary>
        private static (TopK TopK, long Nested, long Payload) TopKOfTwoMegabytes()
        {
            var topK = new TopK(0.00005, 0.01, 10);
            for (var i = 0; i < 1000; i++) topK.Add(Key($"g{i % 50}"));
            var sketch = (CountMinSketch)typeof(TopK).GetProperty("Cms",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(topK)!;
            return (topK, sketch.ToByteArray().Length, topK.ToByteArray().Length - (long)PersistenceFormat.EnvelopeLength);
        }

        /// <summary>
        /// A structure nested in the large form is read from the outer reader, field by field
        /// from the outer stream, with nothing its size held: a top-k whose count-min sketch's
        /// envelope is about two megabytes reads it with no read longer than a block's piece,
        /// which the sketch's counters are read in (#289), and
        /// allocates, beyond what the sketch's own read in the large form does, less than a
        /// tenth of that envelope, with the sketch's envelope in either form.
        /// </summary>
        /// <remarks>
        /// The nested envelope was copied out of the outer payload into an array of its own,
        /// the run, and read from a <see cref="MemoryStream"/> over it, the ordinary one's
        /// payload copied again; past one array no run is copied into one (#228). The sketch's
        /// envelope is in the large form with the threshold at nothing, and ordinary with it a
        /// byte under the top-k's payload.
        /// </remarks>
        [TestMethod]
        public void TestANestedEnvelopeInTheLargeFormIsReadFromTheOuterReader()
        {
            var (topK, nested, payload) = TopKOfTwoMegabytes();
            var expected = topK.ToByteArray();
            var sketch = (CountMinSketch)typeof(TopK).GetProperty("Cms",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(topK)!;
            var alone = LargeForm.WriteLarge(sketch);
            var sketchRead = TestReadBudget.Allocated(() => CountMinSketch.ReadFrom(new MemoryStream(alone, writable: false), ReadOptions.Unlimited));

            foreach (var (threshold, form) in new[] { (0L, "in the large form"), (payload - 1, "ordinary") })
            {
                var large = LargeForm.WriteLarge(topK, threshold);
                var stream = new Recording(large);
                CollectionAssert.AreEqual(expected, TopK.ReadFrom(stream, ReadOptions.Unlimited).ToByteArray(),
                    $"the top-k with its sketch {form} does not read back");

                // No read longer than a block's piece, which the sketch's counters are read in
                // (#289), and the envelope is longer than one, so a read that held it would be longer.
                Assert.IsGreaterThan((long)PayloadSource.BlockPieceLength, nested, $"the sketch's envelope is no longer than a block's piece");
                var back = stream.Log.FindIndex(a => a.IsMove && a.To == PersistenceFormat.LargeHeaderLength);
                var widest = stream.Log.Skip(back + 1).Where(a => !a.IsMove).Max(a => a.Count);
                Assert.IsLessThanOrEqualTo((long)PayloadSource.BlockPieceLength, widest,
                    $"with its sketch {form}, the top-k read {widest:N0} bytes at once: its {nested:N0}-byte envelope was held");

                var read = TestReadBudget.Allocated(() => TopK.ReadFrom(new MemoryStream(large, writable: false), ReadOptions.Unlimited));
                Console.WriteLine($"[NESTED] sketch {form}: envelope {nested:N0}, top-k's read {read:N0}, the sketch's alone {sketchRead:N0}");
                Assert.IsLessThan(nested / 10, read - sketchRead,
                    $"with its sketch {form}, the top-k's read allocated {read:N0} bytes, and the sketch's alone " +
                    $"{sketchRead:N0}: its {nested:N0}-byte envelope was copied");
            }
        }

        /// <summary>
        /// A structure nested in the large form is held to its own checksum as it is read from
        /// the outer stream: a byte of a top-k's sketch's cells changed, with the top-k's own
        /// checksums worked out again over the change, is refused for the sketch's checksum,
        /// with the sketch's envelope in either form.
        /// </summary>
        /// <remarks>
        /// A nested envelope keeps its own checksum, so that it can be pulled out and read on
        /// its own (FORMAT.md, Nested structures). Resealed outside, nothing but the sketch's
        /// checksum refuses the change: a cell is a count, and any count reads. This held when
        /// the envelope was read from a stream of its own, and holds read from the outer one,
        /// where the checksum is compared once the sketch has read its payload.
        /// </remarks>
        [TestMethod]
        public void TestANestedEnvelopeInTheLargeFormIsHeldToItsOwnChecksum()
        {
            var topK = new TopK(0.01, 0.01, 4);
            foreach (var element in new[] { "a", "bb", "ccc", "a" }) topK.Add(Key(element));
            var payload = topK.ToByteArray().Length - (long)PersistenceFormat.EnvelopeLength;

            foreach (var (threshold, form) in new[] { (0L, "in the large form"), (payload - 1, "ordinary") })
            {
                var large = (byte[])LargeForm.WriteLarge(topK, threshold).Clone();
                var (at, length) = NestedEnvelope(large);

                // The last byte of the sketch's payload, the last cell's, before its checksum.
                large[at + length - 5] ^= 0x10;
                var refused = Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<TopK>(LargeForm.Reseal(large)),
                    $"a top-k whose sketch, {form}, was changed inside its envelope was read");
                StringAssert.Contains(refused.Message, "does not match", $"with the sketch {form}: {refused.Message}");
                Assert.DoesNotContain("TopK read from it", refused.Message,
                    $"with the sketch {form}, the change was refused by the top-k's checksum, which was resealed: {refused.Message}");
            }
        }

        /// <summary>
        /// A run holding a nested envelope holds that envelope and nothing after it: a top-k in
        /// the large form whose sketch's run is a byte longer than the sketch's envelope, the
        /// byte after the envelope, the run's length and the top-k's checksums repaired, is
        /// refused for the byte left over, with the sketch's envelope in either form.
        /// </summary>
        /// <remarks>
        /// The run's length and the envelope's own both say where it ends. Read through a
        /// stream of its own, the byte after it was never looked at; read from the outer
        /// stream, a reader that went by the envelope's would read the top-k's own fields from
        /// that byte on.
        /// </remarks>
        [TestMethod]
        public void TestANestedRunLongerThanItsEnvelopeIsRefusedInTheLargeForm()
        {
            var topK = new TopK(0.01, 0.01, 4);
            foreach (var element in new[] { "a", "bb", "ccc", "a" }) topK.Add(Key(element));
            var payload = topK.ToByteArray().Length - (long)PersistenceFormat.EnvelopeLength;

            foreach (var (threshold, form) in new[] { (0L, "in the large form"), (payload - 1, "ordinary") })
            {
                var large = LargeForm.WriteLarge(topK, threshold);
                var (at, length) = NestedEnvelope(large);

                // The run's prefix, a u64 in the large form; a u32 in a writer without #228.
                var wide = (long)BinaryPrimitives.ReadUInt64LittleEndian(large.AsSpan(at - 8)) == length;
                var prefix = wide ? 8 : 4;
                Assert.IsTrue(wide || BinaryPrimitives.ReadUInt32LittleEndian(large.AsSpan(at - 4)) == length,
                    $"with the sketch {form}, its envelope is not preceded by its run's length");

                var longer = large[..(at - prefix)]
                    .Concat(wide ? BitConverter.GetBytes((ulong)length + 1) : BitConverter.GetBytes((uint)length + 1))
                    .Concat(large[at..(at + length)])
                    .Concat(new byte[] { 0 })
                    .Concat(large[(at + length)..])
                    .ToArray();
                BinaryPrimitives.WriteUInt64LittleEndian(longer.AsSpan(10), BinaryPrimitives.ReadUInt64LittleEndian(large.AsSpan(10)) + 1);

                var refused = Assert.ThrowsExactly<InvalidDataException>(() => Persistence.FromByteArray<TopK>(LargeForm.Reseal(longer)),
                    $"a top-k whose sketch's run, {form}, held a byte after the sketch's envelope was read");
                StringAssert.Contains(refused.Message, "left over", $"with the sketch {form}: {refused.Message}");
            }
        }

        /// <summary>
        /// Where the one envelope nested in a large-form envelope begins, and how long it is,
        /// by its own header.
        /// </summary>
        private static (int At, int Length) NestedEnvelope(byte[] large)
        {
            var at = large.AsSpan(PersistenceFormat.LargeHeaderLength).IndexOf(PersistenceFormat.Magic) + PersistenceFormat.LargeHeaderLength;
            Assert.IsGreaterThan(PersistenceFormat.LargeHeaderLength - 1, at, "no envelope is nested in the payload");
            var flagged = (BinaryPrimitives.ReadUInt16LittleEndian(large.AsSpan(at + 4)) & PersistenceFormat.LargeFormFlag) != 0;
            var length = flagged
                ? (int)BinaryPrimitives.ReadUInt64LittleEndian(large.AsSpan(at + 10)) + PersistenceFormat.LargeEnvelopeLength
                : (int)BinaryPrimitives.ReadUInt32LittleEndian(large.AsSpan(at + 10)) + PersistenceFormat.EnvelopeLength;
            return (at, length);
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
