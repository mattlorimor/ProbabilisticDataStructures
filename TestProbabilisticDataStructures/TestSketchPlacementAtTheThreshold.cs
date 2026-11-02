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
    /// The sketches' placement at the width it changes, built and read for real.
    /// </summary>
    /// <remarks>
    /// A row of 2^27 counters is a gigabyte, and the payload of one is another, so each
    /// test here holds a gigabyte or two and runs on its own, after the tests that run in
    /// parallel, and collects what it built before it builds the next. The payloads from
    /// before version 9 are assembled here from FORMAT.md's version 1 layouts and served a
    /// read at a time, so that the test never holds one as well as the reader does.
    /// Every structure is one row deep, the least the width can be reached with.
    /// <para>
    /// At a width that is a power of two the two schemes put the first row's column in the
    /// same place, so the payloads here are at 2^27 + 29, where they differ for every word.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestSketchPlacementAtTheThreshold
    {
        private const uint Threshold = 1u << 27;

        /// <summary>Just past the threshold, and not a power of two.</summary>
        private const uint Past = Threshold + 29;

        private const int Header = 14;

        private static readonly string[] Words =
            { "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa" };

        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        private static ulong Hash(byte[] data) => XxHash3.HashToUInt64(data);

        /// <summary>The 32-bit scheme's first column, restated: the hash's low 32 bits modulo the width.</summary>
        private static uint NarrowFirst(ulong hash, uint m) => (uint)hash % m;

        /// <summary>The 64-bit scheme's first column, restated: the whole hash modulo the width.</summary>
        private static uint WideFirst(ulong hash, uint m) => (uint)(hash % m);

        /// <summary>An epsilon whose width, <c>ceil(e / epsilon)</c>, is <paramref name="width"/>.</summary>
        private static double EpsilonFor(uint width)
        {
            var epsilon = Math.E / (width - 0.5);
            Assert.AreEqual((double)width, Math.Ceiling(Math.E / epsilon), $"no epsilon here gives a width of {width}");
            return epsilon;
        }

        /// <summary>
        /// Builds something large, asks it one thing, and lets it go before the next is built,
        /// so that no two of them are held at once.
        /// </summary>
        private static void Once<T>(Func<T> build, Action<T> check) where T : class
        {
            Collect();
            var built = build();
            check(built);
            built = null;
            Collect();
        }

        private static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        /// <summary>
        /// The words' first columns at <see cref="Past"/> under each scheme, with the check
        /// that makes the payloads below able to tell the schemes apart: every word's column
        /// is different under the two, and no two words share one under the 32-bit scheme.
        /// </summary>
        private static uint[] NarrowColumns()
        {
            var narrow = Words.Select(w => NarrowFirst(Hash(Key(w)), Past)).ToArray();
            var wide = Words.Select(w => WideFirst(Hash(Key(w)), Past)).ToArray();
            Assert.AreEqual(Words.Length, narrow.Distinct().Count(), "two words share a column, so their counts would be summed");
            Assert.IsTrue(narrow.Zip(wide).All(p => p.First != p.Second),
                "a word has the same column under both schemes, so reading it either way would find it");
            return narrow;
        }

        /// <summary>
        /// Each constructor chooses by its width, and does so at the threshold: one column
        /// short of it is placed with 32-bit values and the threshold itself with 64-bit
        /// ones. The count-min sketch and HeavyKeeper add a structure past the threshold in
        /// counters, two rows of half of it, which is what tells the width from the size.
        /// The top-k is its sketch's choice.
        /// </summary>
        [TestMethod]
        [DoNotParallelize]
        public void TestEachConstructorChoosesItsPlacementByItsWidth()
        {
            const uint Half = Threshold / 2 + 1;

            Once(() => new CountMinSketch(EpsilonFor(Threshold - 1), 0.5), s =>
            {
                Assert.AreEqual((Threshold - 1, 1u), (s.Width, s.Depth));
                Assert.AreEqual(Placement.Narrow, s.Placement, "CountMinSketch below");
            });
            Once(() => new CountMinSketch(EpsilonFor(Threshold), 0.5), s =>
            {
                Assert.AreEqual((Threshold, 1u), (s.Width, s.Depth));
                Assert.AreEqual(Placement.Wide, s.Placement, "CountMinSketch at");
            });
            Once(() => new CountMinSketch(EpsilonFor(Half), 0.2), s =>
            {
                Assert.AreEqual((Half, 2u), (s.Width, s.Depth));
                Assert.AreEqual(Placement.Narrow, s.Placement, "CountMinSketch chose by its counters rather than its width");
            });

            Once(() => new PrivateCountMinSketch(Threshold - 1, 1, 1.0, seed: 1), s =>
                Assert.AreEqual(Placement.Narrow, s.Placement, "PrivateCountMinSketch below"));
            Once(() => new PrivateCountMinSketch(Threshold, 1, 1.0, seed: 1), s =>
                Assert.AreEqual(Placement.Wide, s.Placement, "PrivateCountMinSketch at"));

            Once(() => new HeavyKeeper(1, Threshold - 1, 1, seed: 1), s =>
                Assert.AreEqual(Placement.Narrow, s.Placement, "HeavyKeeper below"));
            Once(() => new HeavyKeeper(1, Threshold, 1, seed: 1), s =>
                Assert.AreEqual(Placement.Wide, s.Placement, "HeavyKeeper at"));
            Once(() => new HeavyKeeper(1, Half, 2, seed: 1), s =>
                Assert.AreEqual(Placement.Narrow, s.Placement, "HeavyKeeper chose by its buckets rather than its width"));

            Once(() => new InverseBloomFilter(Threshold - 1), f =>
                Assert.AreEqual(Placement.Narrow, f.Placement, "InverseBloomFilter below"));
            Once(() => new InverseBloomFilter(Threshold), f =>
                Assert.AreEqual(Placement.Wide, f.Placement, "InverseBloomFilter at"));

            Once(() => new TopK(EpsilonFor(Threshold), 0.5, 3), t =>
            {
                Assert.AreEqual(Threshold, t.Sketch.Width);
                Assert.AreEqual(Placement.Wide, t.Sketch.Placement, "TopK at");
            });
        }

        /// <summary>
        /// A count-min sketch written before version 9 was placed with 32-bit values
        /// whatever its width, and reads back that way: one past the threshold, as a library
        /// before version 9 would have built and written it, reads back placed as it was and
        /// still counts what it held. Reading it as its width calls for would find none of
        /// it. Offered a new sketch of the same width, which is placed with 64-bit values, it
        /// refuses to merge, either way round.
        /// </summary>
        [TestMethod]
        [DoNotParallelize]
        public void TestAnOldCountMinSketchOfAnyWidthKeepsTheThirtyTwoBitScheme()
        {
            var columns = NarrowColumns();
            var epsilon = EpsilonFor(Past);

            // f64 epsilon, f64 delta, u32 width, u32 depth, u64 count, then the row.
            var fields = new Payload().F64(epsilon).F64(0.5).U32(Past).U32(1).U64(55).ToArray();
            var runs = new List<(long, byte[])> { (0, fields) };
            for (var i = 0; i < Words.Length; i++)
            {
                runs.Add((fields.Length + 8L * columns[i], new Payload().U64((ulong)i + 1).ToArray()));
            }

            Collect();
            var old = CountMinSketch.ReadFrom(
                new SparseFrame(1, StructureId.CountMinSketch, fields.Length + 8L * Past, runs), ReadOptions.Unlimited);
            Collect();

            Assert.AreEqual(Placement.Narrow, old.Placement, "a sketch past the threshold, read back from version 1");
            for (var i = 0; i < Words.Length; i++)
            {
                Assert.AreEqual((ulong)i + 1, old.Count(Key(Words[i])), $"the sketch read back does not count {Words[i]}");
            }

            var fresh = new CountMinSketch(epsilon, 0.5);
            Assert.AreEqual(Past, fresh.Width);
            Assert.AreEqual(Placement.Wide, fresh.Placement, "a new sketch past the threshold");

            var ex = Assert.ThrowsExactly<ArgumentException>(() => old.Merge(fresh),
                "an old sketch placed with 32-bit values merged a new one placed with 64-bit values");
            StringAssert.Contains(ex.Message, "placed");
            ex = Assert.ThrowsExactly<ArgumentException>(() => fresh.Merge(old),
                "a new sketch placed with 64-bit values merged an old one placed with 32-bit values");
            StringAssert.Contains(ex.Message, "placed");

            old = null;
            fresh = null;
            Collect();
        }

        /// <summary>
        /// The same for the private sketch, whose noise is a half on each counter it
        /// counted in and nothing on the rest: the reader asks only that one counter not
        /// be an integer.
        /// </summary>
        [TestMethod]
        [DoNotParallelize]
        public void TestAnOldPrivateSketchOfAnyWidthKeepsTheThirtyTwoBitScheme()
        {
            var columns = NarrowColumns();

            // u32 width, u32 depth, f64 rho, u64 count, then the row of f64 counters.
            var fields = new Payload().U32(Past).U32(1).F64(1.0).U64(55).ToArray();
            var runs = new List<(long, byte[])> { (0, fields) };
            for (var i = 0; i < Words.Length; i++)
            {
                runs.Add((fields.Length + 8L * columns[i], new Payload().F64(0.5 + i + 1).ToArray()));
            }

            Collect();
            var old = PrivateCountMinSketch.ReadFrom(
                new SparseFrame(1, StructureId.PrivateCountMinSketch, fields.Length + 8L * Past, runs), ReadOptions.Unlimited);
            Collect();

            Assert.AreEqual(Placement.Narrow, old.Placement, "a private sketch past the threshold, read back from version 1");
            for (var i = 0; i < Words.Length; i++)
            {
                Assert.AreEqual(0.5 + i + 1, old.Count(Key(Words[i])), $"the private sketch read back does not count {Words[i]}");
            }

            old = null;
            Collect();
        }

        /// <summary>
        /// The same for HeavyKeeper: one array past the threshold, holding the ten words
        /// at their 32-bit buckets with their fingerprints, each counted as many times as
        /// its place in the list, and all ten tracked.
        /// </summary>
        [TestMethod]
        [DoNotParallelize]
        public void TestAnOldHeavyKeeperOfAnyWidthKeepsTheThirtyTwoBitScheme()
        {
            var columns = NarrowColumns();

            // u32 k, u32 width, u32 depth, f64 decay, u64 generator, u64 n; the fingerprints,
            // u16 each; the counters, u64 each; then the elements.
            var fields = new Payload().U32(10).U32(Past).U32(1).F64(1.08).U64(99).U64(55).ToArray();
            var counters = fields.Length + 2L * Past;
            var elements = new Payload().U32((uint)Words.Length);
            for (var i = 0; i < Words.Length; i++)
            {
                elements.Bytes(Key(Words[i])).U64((ulong)i + 1);
            }

            var runs = new List<(long, byte[])> { (0, fields) };
            for (var i = 0; i < Words.Length; i++)
            {
                var fingerprint = (ushort)(Hash(Key(Words[i])) >> 48);
                runs.Add((fields.Length + 2L * columns[i], new Payload().U16(fingerprint).ToArray()));
                runs.Add((counters + 8L * columns[i], new Payload().U64((ulong)i + 1).ToArray()));
            }

            var tail = elements.ToArray();
            runs.Add((counters + 8L * Past, tail));

            Collect();
            var old = HeavyKeeper.ReadFrom(
                new SparseFrame(1, StructureId.HeavyKeeper, counters + 8L * Past + tail.Length, runs), ReadOptions.Unlimited);
            Collect();

            Assert.AreEqual(Placement.Narrow, old.Placement, "a HeavyKeeper past the threshold, read back from version 1");
            Assert.HasCount(Words.Length, old.Elements());
            for (var i = 0; i < Words.Length; i++)
            {
                Assert.AreEqual((ulong)i + 1, old.Count(Key(Words[i])), $"the HeavyKeeper read back does not count {Words[i]}");
            }

            old = null;
            Collect();
        }

        /// <summary>
        /// The same for the inverse filter, whose payload holds only its occupied slots and
        /// so is small at any capacity; the gigabyte is the slots it reads into.
        /// </summary>
        [TestMethod]
        [DoNotParallelize]
        public void TestAnOldInverseFilterOfAnyCapacityKeepsTheThirtyTwoBitScheme()
        {
            var columns = NarrowColumns();

            // u32 capacity, u32 occupied, then each occupied slot's u32 index and its bytes,
            // in order of index.
            var payload = new Payload().U32(Past).U32((uint)Words.Length);
            foreach (var (word, slot) in Words.Zip(columns).OrderBy(p => p.Second))
            {
                payload.U32(slot).Bytes(Key(word));
            }

            var body = payload.ToArray();
            Collect();
            var old = InverseBloomFilter.ReadFrom(
                new SparseFrame(1, StructureId.InverseBloomFilter, body.Length, new[] { (0L, body) }), ReadOptions.Unlimited);
            Collect();

            Assert.AreEqual(Placement.Narrow, old.Placement, "an inverse filter past the threshold, read back from version 1");
            foreach (var word in Words)
            {
                Assert.IsTrue(old.Test(Key(word)), $"the inverse filter read back does not hold {word}");
            }

            old = null;
            Collect();
        }

        /// <summary>Little-endian fields, as FORMAT.md lays them out.</summary>
        private sealed class Payload
        {
            private readonly List<byte> bytes = new();

            public Payload U16(ushort v) => this.Put(2, s => BinaryPrimitives.WriteUInt16LittleEndian(s, v));
            public Payload U32(uint v) => this.Put(4, s => BinaryPrimitives.WriteUInt32LittleEndian(s, v));
            public Payload U64(ulong v) => this.Put(8, s => BinaryPrimitives.WriteUInt64LittleEndian(s, v));
            public Payload F64(double v) => this.Put(8, s => BinaryPrimitives.WriteDoubleLittleEndian(s, v));

            public Payload Bytes(byte[] v)
            {
                this.U32((uint)v.Length);
                this.bytes.AddRange(v);
                return this;
            }

            public byte[] ToArray() => this.bytes.ToArray();

            private Payload Put(int size, SpanAction write)
            {
                var b = new byte[size];
                write(b);
                this.bytes.AddRange(b);
                return this;
            }

            private delegate void SpanAction(Span<byte> span);
        }

        /// <summary>
        /// One envelope whose payload is zeros but for a few runs written over them, served
        /// a read at a time: the reader allocates and fills its own copy of the payload,
        /// and nothing else holds one.
        /// </summary>
        private sealed class SparseFrame : Stream
        {
            private readonly byte[] header = new byte[Header];
            private readonly byte[] checksum = new byte[4];
            private readonly long length;
            private readonly (long At, byte[] Bytes)[] runs;
            private long position;

            public SparseFrame(ushort version, StructureId id, long length, IEnumerable<(long At, byte[] Bytes)> runs)
            {
                this.length = length;
                this.runs = runs.OrderBy(r => r.At).ToArray();
                for (var i = 0; i < this.runs.Length; i++)
                {
                    Assert.IsLessThanOrEqualTo(length, this.runs[i].At + this.runs[i].Bytes.Length, "a run is past the payload");
                    Assert.IsTrue(i == 0 || this.runs[i - 1].At + this.runs[i - 1].Bytes.Length <= this.runs[i].At, "two runs overlap");
                }

                PersistenceFormat.Magic.CopyTo(this.header);
                BinaryPrimitives.WriteUInt16LittleEndian(this.header.AsSpan(4), version);
                BinaryPrimitives.WriteUInt16LittleEndian(this.header.AsSpan(6), (ushort)id);
                BinaryPrimitives.WriteUInt16LittleEndian(this.header.AsSpan(8), (ushort)HashId.XxHash3_64);
                BinaryPrimitives.WriteUInt32LittleEndian(this.header.AsSpan(10), checked((uint)length));

                var crc = new Crc32();
                crc.Append(this.header.AsSpan(4));
                var chunk = new byte[1 << 20];
                for (long at = 0; at < length; at += chunk.Length)
                {
                    var n = (int)Math.Min(chunk.Length, length - at);
                    this.Fill(at, chunk.AsSpan(0, n));
                    crc.Append(chunk.AsSpan(0, n));
                }

                BinaryPrimitives.WriteUInt32LittleEndian(this.checksum, crc.GetCurrentHashAsUInt32());
            }

            /// <summary>The payload from <paramref name="at"/>, as far as <paramref name="into"/> holds.</summary>
            private void Fill(long at, Span<byte> into)
            {
                into.Clear();
                foreach (var (start, bytes) in this.runs)
                {
                    var from = Math.Max(start, at);
                    var to = Math.Min(start + bytes.Length, at + into.Length);
                    if (from < to)
                    {
                        bytes.AsSpan((int)(from - start), (int)(to - from)).CopyTo(into[(int)(from - at)..]);
                    }
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var total = Header + this.length + 4;
                var n = (int)Math.Min(count, total - this.position);
                var into = buffer.AsSpan(offset, Math.Max(n, 0));
                var done = 0;
                while (done < into.Length)
                {
                    var p = this.position + done;
                    int k;
                    if (p < Header)
                    {
                        k = (int)Math.Min(Header - p, into.Length - done);
                        this.header.AsSpan((int)p, k).CopyTo(into[done..]);
                    }
                    else if (p < Header + this.length)
                    {
                        k = (int)Math.Min(Header + this.length - p, into.Length - done);
                        this.Fill(p - Header, into.Slice(done, k));
                    }
                    else
                    {
                        k = (int)Math.Min(total - p, into.Length - done);
                        this.checksum.AsSpan((int)(p - Header - this.length), k).CopyTo(into[done..]);
                    }
                    done += k;
                }

                this.position += done;
                return done;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => this.position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
