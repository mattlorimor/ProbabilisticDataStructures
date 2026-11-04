using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// A Sublime sketch's payload stores, before each state's counters, the tuning its
    /// reader packs them at, so that a read is priced from the fields and seeks over the
    /// counters, and a payload whose counters call for another tuning is refused (#280).
    /// </summary>
    /// <remarks>
    /// A read is priced in full before it allocates (TESTING.md, "A refusal's number is held
    /// to loading"), and the large form prices it in a first pass that seeks over the bulk
    /// of a payload (#226). What a Sublime read allocates for each state is the packed rows,
    /// at the tuning its counters call for, and until version 10 nothing but the counters
    /// said which tuning that was: the price read every one of them, which was all of the
    /// payload.
    /// <para>
    /// What is charged for the packed rows is the worst case at a tuning, every chunk on a
    /// tails array, so it depends on how many counters share a chunk and on nothing else
    /// the counters hold. The tuning is therefore enough to price exactly, and the stub width
    /// is stored with it so that the reader packs at what is stored. It is held to what the
    /// counters call for, so a price from the fields and what the reader charges are the
    /// same sum: these tests edit one field of a payload the library wrote, reseal the
    /// checksum, and ask for that.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestSublimeStoredTuning
    {
        private const int PayloadStart = 14;

        /// <summary>Where the u32 count of records lies: after three f64, two u32 and three u64.</summary>
        private const int RecordCountAt = 3 * 8 + 2 * 4 + 3 * 8;

        /// <summary>The version FORMAT.md gives the layout these tests edit.</summary>
        private const ushort StoredTuningVersion = 10;

        private static byte[] Key(long i)
        {
            var key = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(key, i);
            return key;
        }

        /// <summary>
        /// The read-budget sweep's Sublime sample: three doublings, so four states, each
        /// tuned differently (measured: 57, 51, 46 and 42 counters to a chunk), so that a
        /// price that took one state's tuning for another's would not come to the same sum.
        /// </summary>
        private static readonly Lazy<SublimeCountMinSketch> Sample = new(() =>
        {
            var sketch = new SublimeCountMinSketch(0.01);
            for (var i = 0; i < 200_000; i++)
            {
                sketch.Add(Key(i));
            }
            return sketch;
        });

        private static int RecordsOf(SublimeCountMinSketch sketch) =>
            ((List<ValeCounterArray[]>)typeof(SublimeCountMinSketch)
                .GetField("records", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(sketch)!).Count;

        /// <summary>
        /// A stream over some bytes that can seek, as a file can, and counts what is read
        /// from it and what is sought past.
        /// </summary>
        private sealed class Counting(byte[] bytes) : Stream
        {
            private readonly MemoryStream inner = new(bytes, writable: false);

            internal long BytesRead { get; private set; }
            internal long SoughtPast { get; private set; }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => this.inner.Length;
            public override long Position
            {
                get => this.inner.Position;
                set => this.inner.Position = value;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var n = this.inner.Read(buffer, offset, count);
                this.BytesRead += n;
                return n;
            }

            public override int Read(Span<byte> buffer)
            {
                var n = this.inner.Read(buffer);
                this.BytesRead += n;
                return n;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                var from = this.inner.Position;
                var to = this.inner.Seek(offset, origin);
                this.SoughtPast += Math.Max(0, to - from);
                return to;
            }

            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private static bool RefusedForBudget(Func<object> read, out ReadBudgetExceededException why)
        {
            try
            {
                read();
                why = null;
                return false;
            }
            catch (InvalidDataException e) when (e.InnerException is ReadBudgetExceededException budget)
            {
                why = budget;
                return true;
            }
        }

        /// <summary>The total a payload is refused for needing under a budget of nothing.</summary>
        private static long Total(byte[] bytes)
        {
            Assert.IsTrue(RefusedForBudget(
                    () => Persistence.FromByteArray<SublimeCountMinSketch>(bytes, new ReadOptions(0)), out var why),
                "a Sublime sketch loaded under a budget of nothing");
            return why!.RequiredBytes;
        }

        /// <summary>What a read is charged, and what its reader then charges, with nothing to stop it.</summary>
        private static (long Charged, long Spent) Charges(byte[] bytes)
        {
            var unlimited = new ReadBudget(long.MaxValue, StructureId.SublimeCountMinSketch);
            Persistence.FromByteArray<SublimeCountMinSketch>(bytes, new ReadOptions(unlimited));
            return (unlimited.Charged, unlimited.Spent);
        }

        /// <summary>One byte of the payload set to <paramref name="value"/>, and the checksum resealed.</summary>
        private static byte[] Edited(byte[] original, int at, byte value)
        {
            var bytes = (byte[])original.Clone();
            bytes[at] = value;
            var crc = new Crc32();
            crc.Append(bytes.AsSpan(4, bytes.Length - 8));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), crc.GetCurrentHashAsUInt32());
            return bytes;
        }

        /// <summary>
        /// Where each state's tuning lies in an ordinary envelope at version 10, found by
        /// FORMAT.md's table of the layout and not by the library: after each record's u32
        /// width, and after the records for the current rows, two bytes, then that state's
        /// counters. The walk ends exactly at the checksum, or the table is not the layout.
        /// </summary>
        private static List<(string State, int At, int Width)> Tunings(byte[] envelope)
        {
            Assert.AreEqual(StoredTuningVersion, LargeForm.VersionField(envelope),
                "the sketch was not written at the version that stores its tuning");

            var payload = envelope.AsSpan(PayloadStart, envelope.Length - PayloadStart - 4);
            var depth = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload[24..]);
            var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload[28..]);
            var records = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload[RecordCountAt..]);

            var found = new List<(string, int, int)>();
            var at = RecordCountAt + 4;
            for (var r = 0; r < records; r++)
            {
                var recordWidth = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload[at..]);
                Assert.AreEqual(width >> (records - r), recordWidth, $"record {r} is not where the table puts it");
                found.Add(($"record {r}", PayloadStart + at + 4, recordWidth));
                at += 4 + 2 + 8 * depth * recordWidth;
            }

            found.Add(("the current rows", PayloadStart + at, width));
            at += 2 + 8 * depth * width;
            Assert.AreEqual(payload.Length, at, "the table of the layout does not end where the payload does");
            return found;
        }

        /// <summary>
        /// The price of a Sublime payload at version 10 reads its fields and seeks over every
        /// counter, and comes to what its reader charges. Before version 10 it read every
        /// counter to work out the tuning they call for, and a payload of that version still
        /// is priced that way, at what its reader charges too.
        /// </summary>
        /// <remarks>
        /// Read under a budget of nothing, from a stream that can seek, the payload is longer
        /// than the budget, so it is priced from the stream a field at a time and refused, as
        /// the first pass of a large read prices it. What the stream gives up is the header
        /// and every field that is not a counter; what it is sought past is the counters,
        /// which are more than ninety-nine hundredths of the payload. The total refused for is
        /// then held to the read's charges with nothing limiting it: a price that stopped at
        /// the first field read little too, and would name less.
        /// </remarks>
        [TestMethod]
        public void TestASublimeSketchIsPricedFromItsStoredTuningWithoutReadingItsCounters()
        {
            var sketch = Sample.Value;
            var bytes = sketch.ToByteArray();
            var payload = bytes.Length - PersistenceFormat.EnvelopeLength;

            var records = RecordsOf(sketch);
            var counters = 0L;
            for (var r = 1; r <= records; r++)
            {
                counters += (long)sketch.Depth * (sketch.Width >> r);
            }
            counters += (long)sketch.Depth * sketch.Width;
            var counterBytes = counters * sizeof(ulong);
            var fields = payload - counterBytes;

            Assert.IsGreaterThanOrEqualTo(2, records, "the sample has too few records for their prices to be told apart");
            Assert.IsGreaterThan(0.99, (double)counterBytes / payload,
                $"the counters are {counterBytes} of the {payload}-byte payload, too little for reading them to show");

            var stream = new Counting(bytes);
            Assert.IsTrue(RefusedForBudget(
                    () => SublimeCountMinSketch.ReadFrom(stream, new ReadOptions(0)), out var why),
                "the sample loaded under a budget of nothing");
            Console.WriteLine(
                $"[SUBLIME-PRICE] payload {payload}, fields {fields}, read {stream.BytesRead}, sought past " +
                $"{stream.SoughtPast}, total {why!.RequiredBytes}");

            Assert.AreEqual(PayloadStart + fields, stream.BytesRead,
                $"the price read {stream.BytesRead} bytes of a {bytes.Length}-byte envelope whose header and fields " +
                $"are {PayloadStart + fields}: it read {stream.BytesRead - PayloadStart - fields} bytes of counters");
            Assert.AreEqual(counterBytes, stream.SoughtPast,
                $"the price sought past {stream.SoughtPast} bytes, and the counters are {counterBytes}");

            var (charged, spent) = Charges(bytes);
            Assert.AreEqual(charged, why.RequiredBytes,
                $"the price named {why.RequiredBytes} bytes and the read was charged {charged}");
            Assert.AreEqual(charged, spent,
                $"the read was priced at {charged} bytes and its reader charged {spent}");

            // The large form's first pass is this price (#226), after the large header: refused
            // under nothing, it has read the same fields and sought past the same counters.
            var large = LargeForm.WriteLarge(sketch);
            var largeStream = new Counting(large);
            Assert.IsTrue(RefusedForBudget(
                    () => SublimeCountMinSketch.ReadFrom(largeStream, new ReadOptions(0)), out var largeWhy),
                "the sample in the large form loaded under a budget of nothing");
            Console.WriteLine(
                $"[SUBLIME-PRICE] large form: read {largeStream.BytesRead}, sought past {largeStream.SoughtPast}, " +
                $"total {largeWhy!.RequiredBytes}");
            Assert.AreEqual(PersistenceFormat.LargeHeaderLength + fields, largeStream.BytesRead,
                $"the large form's first pass read {largeStream.BytesRead} bytes, and its header and fields are " +
                $"{PersistenceFormat.LargeHeaderLength + fields}");
            Assert.AreEqual(counterBytes, largeStream.SoughtPast,
                $"the large form's first pass sought past {largeStream.SoughtPast} bytes, and the counters are {counterBytes}");
        }

        /// <summary>
        /// A payload of version 1 stores no tuning, so its price reads every counter for the
        /// tuning they call for, as it always did, and comes to what its reader charges; the
        /// same sketch at version 10 is priced from its fields at the same sum, but for the
        /// four bytes of tuning its payload holds besides.
        /// </summary>
        [TestMethod]
        public void TestASublimeSketchOfEitherVersionIsPricedAtWhatItsReaderCharges()
        {
            var earlier = ReadFixture("sublimecountminsketch-v1.bin");
            var stored = ReadFixture("sublimecountminsketch-v10.bin");

            var stream = new Counting(earlier);
            Assert.IsTrue(RefusedForBudget(
                    () => SublimeCountMinSketch.ReadFrom(stream, new ReadOptions(0)), out var why),
                "the version 1 fixture loaded under a budget of nothing");
            Assert.AreEqual(earlier.Length - 4, stream.BytesRead,
                "the price of a version 1 payload read other than every byte before its checksum");
            Assert.AreEqual(0, stream.SoughtPast, "the price of a version 1 payload sought past something");

            var (charged, spent) = Charges(earlier);
            Assert.AreEqual(charged, why!.RequiredBytes, "the version 1 fixture's price is not its charge");
            Assert.AreEqual(charged, spent, "the version 1 fixture's reader charged other than its price");

            var (storedCharged, storedSpent) = Charges(stored);
            Assert.AreEqual(storedCharged, Total(stored), "the version 10 fixture's price is not its charge");
            Assert.AreEqual(storedCharged, storedSpent, "the version 10 fixture's reader charged other than its price");

            Assert.AreEqual(stored.Length - 4, earlier.Length, "the fixtures do not differ by two states' tunings");
            Assert.AreEqual(
                PersistenceFormat.EnvelopeCost((ulong)(stored.Length - PersistenceFormat.EnvelopeLength))
                    - PersistenceFormat.EnvelopeCost((ulong)(earlier.Length - PersistenceFormat.EnvelopeLength)),
                storedCharged - charged,
                "the two fixtures hold the same counters, and are priced other than for their lengths");
        }

        /// <summary>
        /// A payload whose counters call for another tuning than the one it stores is refused
        /// for that, for each state: the stub width moved either way, which leaves the price
        /// where it was, and the counters to a chunk moved either way, which moves it by
        /// exactly what those packed rows would cost.
        /// </summary>
        /// <remarks>
        /// Each edit is a tuning a chunk can hold, so only the counters can say it is wrong.
        /// The price comes from the field, so the payload names a total worked out from what
        /// it stores, and under that total it is refused for its tuning rather than its
        /// budget: a price that read the counters instead would not move, and a reader that
        /// took the stored tuning without asking the counters would read the payload. The
        /// unedited payload reads under its own total, so a reader refusing everything fails
        /// too.
        /// </remarks>
        [TestMethod]
        public void TestASublimeSketchWhoseCountersDoNotFitItsStoredTuningIsRefused()
        {
            var sketch = Sample.Value;
            var bytes = sketch.ToByteArray();
            var states = Tunings(bytes);
            var total = Total(bytes);

            var read = Persistence.FromByteArray<SublimeCountMinSketch>(bytes, new ReadOptions(total));
            CollectionAssert.AreEqual(bytes, read.ToByteArray(), "the unedited sample does not read back as itself");

            var tunings = new HashSet<(byte, byte)>();
            foreach (var (state, at, width) in states)
            {
                var chunkCounters = bytes[at];
                var stub = bytes[at + 1];
                tunings.Add((chunkCounters, stub));

                foreach (var (offset, value) in new[]
                {
                    (at, (byte)(chunkCounters - 1)), (at, (byte)(chunkCounters + 1)),
                    (at + 1, (byte)(stub - 1)), (at + 1, (byte)(stub + 1)),
                })
                {
                    var edited = Edited(bytes, offset, value);
                    var what = offset == at
                        ? $"{state} stored at {value} counters to a chunk rather than {chunkCounters}"
                        : $"{state} stored with {value}-bit stubs rather than {stub}";

                    var expected = offset == at
                        ? sketch.Depth * (ValeCounterArray.RestoreCost(width, value)
                            - ValeCounterArray.RestoreCost(width, chunkCounters))
                        : 0;
                    var named = Total(edited);
                    Assert.AreEqual(total + expected, named,
                        $"{what} named {named} bytes, and the unedited payload {total}");

                    foreach (var options in new[] { new ReadOptions(named), ReadOptions.Unlimited })
                    {
                        var refusal = Assert.ThrowsExactly<InvalidDataException>(
                            () => Persistence.FromByteArray<SublimeCountMinSketch>(edited, options),
                            $"{what} was read");
                        Assert.IsNotInstanceOfType<ReadBudgetExceededException>(refusal.InnerException,
                            $"{what} was refused for its budget, under the total it named");
                        StringAssert.Contains(refusal.Message, "its counters call for",
                            $"{what} was refused for something else");
                    }
                }
            }

            Assert.AreEqual(states.Count, tunings.Count,
                "the sample's states share a tuning, so a price taking one's for another's would not show");
        }

        /// <summary>
        /// A stored tuning that no chunk holds is refused for that, by the price as well as
        /// the read, before what follows it is priced: a chunk of no counters, which would
        /// divide by nought, of one, priced at a chunk per counter, and either side of each
        /// bound, the counters to a chunk and the stub width both. Inside each bound the
        /// tuning is one a chunk holds, and is refused only for not being the one the
        /// counters call for.
        /// </summary>
        /// <remarks>
        /// The budget is one byte short of what the unedited payload names, the pattern of
        /// TestReadBudget's shape refusals. A price that went on past a tuning it did not
        /// check would price the rows at it, and a chunk of one counter, or of fifteen, costs
        /// more than the unedited rows: refused for the budget instead. The first record's
        /// tuning and the current rows' are both edited, the first state and the last.
        /// </remarks>
        [TestMethod]
        public void TestASublimeTuningNoChunkHoldsIsRefusedBeforeItIsPriced()
        {
            var bytes = Sample.Value.ToByteArray();
            var states = Tunings(bytes);
            var budget = new ReadOptions(Total(bytes) - 1);

            foreach (var (state, at, _) in new[] { states[0], states[^1] })
            {
                foreach (var (offset, value, holds) in new (int, byte, bool)[]
                {
                    (at, 0, false), (at, 1, false),
                    (at, ValeCounterArray.MinCountersPerChunk - 1, false), (at, ValeCounterArray.MinCountersPerChunk, true),
                    (at, ValeCounterArray.MaxCountersPerChunk, true), (at, ValeCounterArray.MaxCountersPerChunk + 1, false),
                    (at, 255, false),
                    (at + 1, 0, false),
                    (at + 1, ValeCounterArray.MinStubBits - 1, false), (at + 1, ValeCounterArray.MinStubBits, true),
                    (at + 1, ValeCounterArray.MaxStubBits, true), (at + 1, ValeCounterArray.MaxStubBits + 1, false),
                    (at + 1, 255, false),
                })
                {
                    var edited = Edited(bytes, offset, value);
                    var what = $"{state} stored with its {(offset == at ? "counters to a chunk" : "stub width")} at {value}";

                    var refusal = Assert.ThrowsExactly<InvalidDataException>(
                        () => Persistence.FromByteArray<SublimeCountMinSketch>(
                            edited, holds ? new ReadOptions(Total(edited)) : budget),
                        $"{what} was read");
                    Assert.IsNotInstanceOfType<ReadBudgetExceededException>(refusal.InnerException,
                        $"{what} was refused for its budget, not its tuning: {refusal.Message}");
                    StringAssert.Contains(refusal.Message, holds ? "its counters call for" : "A chunk holds between",
                        $"{what} was refused for something else");
                }
            }
        }

        private static byte[] ReadFixture(string name)
        {
            using var resource = typeof(TestSublimeStoredTuning).Assembly
                .GetManifestResourceStream($"TestProbabilisticDataStructures.fixtures.{name}")
                ?? throw new InvalidOperationException($"fixture {name} is not embedded");
            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            return buffer.ToArray();
        }
    }
}
