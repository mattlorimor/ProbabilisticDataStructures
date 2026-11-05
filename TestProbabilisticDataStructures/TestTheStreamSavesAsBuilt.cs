using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Through <c>WriteTo(Stream)</c> every structure saves as built, limited only by memory:
    /// the stream path refuses nothing a structure can build (#279). A byte array cannot
    /// hold a payload past 2 GiB, and the README's table says how large each structure can
    /// be and still be one; the stream writes such a payload in the large form of the
    /// envelope, where a run's length is a <c>u64</c>.
    /// </summary>
    /// <remarks>
    /// What the stream path refuses is in <see cref="PersistenceFormat"/> and
    /// <see cref="PayloadWriter"/>: a count past what a stream holds, a run past the longest
    /// one a reader takes into an array, which only the ordinary envelope is held to, and a
    /// body that writes other than it counted. No structure at its largest can be built in
    /// the suite, so the tests here meet those refusals the ways the large-form tests do.
    /// <para>
    /// Where a field sizes the payload, a small structure is made to claim its largest
    /// build by setting that field, as <see cref="TestPayloadCount"/> does, and
    /// <c>WriteTo</c> of it is let run as far as the stream: past one array, until the
    /// large form's header is written, which comes before any of the payload, and within
    /// one, until its count, watched through <see cref="PersistenceFormat.Counted"/>, since
    /// the ordinary envelope's buffer would be allocated next at the length claimed. Nothing
    /// may refuse it on the way, and the count has to be the length the layout gives, the
    /// large form's worked out from the ordinary bytes as FORMAT.md lays them out, so that
    /// a count wrapped in 32 bits, which the write would then disagree with, fails here; and
    /// <c>ToByteArray</c> of the same claim has to be refused, so the claim is one only the
    /// stream saves. Where what the structure holds sizes it -- an array, or the items it
    /// keeps -- no field can be set to claim more without allocating it, and the structure
    /// is written for real past a lowered threshold, with the longest run a payload may
    /// hold lowered beneath its own: the bounds a payload past 2 GiB, with a run past one
    /// array, meets at the real ones.
    /// </para>
    /// <para>
    /// That leaves one thing unreached for those: a product over the length of an array
    /// they hold, narrowed to 32 bits, wraps only at a length the suite cannot allocate.
    /// <c>MinHashSignature</c>'s count of its values cast to an <c>int</c> survives both
    /// tests, and the rest of the suite.
    /// </para>
    /// <para>
    /// The corner claimed is the coarsest the constructor takes: each dimension at its
    /// limit symbol. Where a dimension is bounded only by memory -- the rows of a sketch
    /// whose rows may be as many as an array holds -- it is at the rows the README's table
    /// gives, since no corner of it is any coarser for those refusals: a count wrapped in
    /// 64 bits would need a structure holding 2^63 bytes, which no machine's memory comes
    /// near.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TestTheStreamSavesAsBuilt
    {
        private const BindingFlags Members = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

        /// <summary>
        /// The most a claimed write may allocate. Its count builds nothing, and as far as the
        /// stream it allocates a header and an exception: probed at 3,128 to 5,112 bytes for
        /// the claims here, so this is twelve times the most. The payloads claimed are
        /// gigabytes, and the large form's streaming buffer alone is 64 KiB.
        /// </summary>
        private const long CountAllocates = 64 * 1024;

        /// <summary>What a write is stopped with, at its count or at the stream.</summary>
        private sealed class Stopped : Exception
        {
        }

        /// <summary>
        /// A stream that keeps the first write it is given and stops the writer there: the
        /// large form's header, which goes to the stream before any of the payload does.
        /// </summary>
        private sealed class FirstWriteStops : MemoryStream
        {
            public override void Write(byte[] buffer, int offset, int count)
            {
                base.Write(buffer, offset, count);
                throw new Stopped();
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                base.Write(buffer);
                throw new Stopped();
            }

            public override void WriteByte(byte value)
            {
                base.WriteByte(value);
                throw new Stopped();
            }
        }

        /// <summary>A member of a structure, or of something it holds, and the value it is to claim.</summary>
        private sealed record Claim(object Owner, string Member, object Value);

        /// <summary>
        /// A structure's largest build, as a claim on a small one.
        /// </summary>
        /// <param name="Id">The structure.</param>
        /// <param name="Shape">What is claimed, for the messages.</param>
        /// <param name="PastOneArray">Whether the claim's payload is past what a byte array holds.</param>
        /// <param name="Make">A small real structure.</param>
        /// <param name="Claims">The members the claim sets, on the structure given.</param>
        /// <param name="Sized">
        /// The bytes of the payload that those members size, from FORMAT.md's layout, read
        /// off the structure as it stands, in arithmetic that cannot wrap.
        /// </param>
        private sealed record LargestBuild(
            StructureId Id, string Shape, bool PastOneArray, Func<object> Make,
            Func<object, Claim[]> Claims, Func<object, BigInteger> Sized);

        /// <summary>
        /// A structure whose payload is sized by what it holds, which no claim can enlarge
        /// without allocating it; it is written for real by
        /// <see cref="TestEveryStructurePastTheThresholdIsWrittenWithItsLongestRunPastTheBound"/>.
        /// </summary>
        /// <param name="Id">The structure.</param>
        /// <param name="Type">The type that holds the member.</param>
        /// <param name="Member">The arrays or collections that size its payload.</param>
        private sealed record Held(StructureId Id, Type Type, params string[] Member);

        private static byte[] Key(int i) => Encoding.ASCII.GetBytes($"stream-{i}");

        private static T Filled<T>(T structure, Action<T, byte[]> add, int items = 40)
        {
            for (var i = 0; i < items; i++) add(structure, Key(i));
            return structure;
        }

        private static MemberInfo MemberOf(object owner, string member) =>
            (MemberInfo)owner.GetType().GetField(member, Members)
            ?? owner.GetType().GetProperty(member, Members)
            ?? throw new InvalidOperationException($"{owner.GetType().Name} has no member {member}");

        private static object Get(object owner, string member) => MemberOf(owner, member) switch
        {
            FieldInfo field => field.GetValue(owner)!,
            PropertyInfo property => property.GetValue(owner)!,
            _ => throw new InvalidOperationException(member),
        };

        private static void Set(object owner, string member, object value)
        {
            switch (MemberOf(owner, member))
            {
                case FieldInfo field:
                    field.SetValue(owner, value);
                    break;
                case PropertyInfo property:
                    property.SetValue(owner, value);
                    break;
            }
        }

        private static BigInteger Big(object owner, string member) =>
            Get(owner, member) switch
            {
                int v => v,
                uint v => v,
                long v => v,
                ulong v => v,
                byte v => v,
                var other => throw new InvalidOperationException($"{member} is a {other.GetType().Name}"),
            };

        /// <summary>FORMAT.md's bytes of counters: <c>m</c> × <c>b</c> bits, rounded up.</summary>
        private static BigInteger BucketBytes(object buckets) =>
            (Big(buckets, "count") * Big(buckets, "BucketSize") + 7) / 8;

        /// <summary>A table of 2^q slots of <paramref name="slotBits"/> each, and two words after it.</summary>
        private static BigInteger TableBytes(BigInteger quotientBits, BigInteger slotBits) =>
            8 * (((BigInteger.One << (int)quotientBits) * slotBits >> 6) + 2);

        private static IEnumerable<object> Segments(object filter) => ((IEnumerable)Get(filter, "Segments")).Cast<object>();

        private static IEnumerable<object> DpswSketches(object window) =>
            ((IEnumerable)Get(window, "substreams")).Cast<object>()
                .SelectMany(s => ((IEnumerable)Get(s, "Segments")).Cast<object>())
                .Select(s => Get(s, "Sketch"));

        /// <summary>
        /// Every structure whose payload a field sizes, at its largest build: the corner
        /// each limit symbol allows, written out in the shape's name.
        /// </summary>
        private static IEnumerable<LargestBuild> LargestBuilds()
        {
            yield return new(StructureId.StableBloomFilter, "8-bit cells, as many as one array holds", true,
                () => Filled(new StableBloomFilter(200, 8, 0.01, seed: 1), (f, k) => f.Add(k)),
                s => new[] { new Claim(Get(s, "cells"), "count", Buckets.MaxCount(8)) },
                s => BucketBytes(Get(s, "cells")));

            yield return new(StructureId.PartitionedBloomFilter, "2^32 - 1 bits across its partitions", false,
                () => Filled(new PartitionedBloomFilter(200, 0.01), (f, k) => f.Add(k)),
                s =>
                {
                    var partitions = (Buckets[])Get(s, "Partitions");
                    return partitions.Select(p => new Claim(p, "count", uint.MaxValue / (uint)partitions.Length)).ToArray();
                },
                s => ((Buckets[])Get(s, "Partitions")).Aggregate(BigInteger.Zero, (sum, p) => sum + BucketBytes(p)));

            yield return new(StructureId.QuotientFilter, "2^31 slots, quotient and remainder all 64 bits of the hash", true,
                () => Filled(new QuotientFilter(100, 0.01), (f, k) => f.Add(k)),
                s => new[]
                {
                    new Claim(s, "quotientBits", (uint)QuotientFilter.MaxQuotientBits),
                    new Claim(s, "remainderBits", (uint)(QuotientFilter.HashBits - QuotientFilter.MaxQuotientBits)),
                },
                s => TableBytes(Big(s, "quotientBits"), Big(s, "remainderBits") + 3));

            yield return new(StructureId.InvertibleBloomLookupTable, "as many cells as an array holds, of one-byte keys", true,
                () =>
                {
                    var table = new InvertibleBloomLookupTable(4, 1);
                    for (var i = 0; i < 40; i++) table.Add(new[] { (byte)i });
                    return table;
                },
                s => new[]
                {
                    new Claim(s, "cells", InvertibleBloomLookupTable.MaxCells),
                    new Claim(s, "keySize", 1),
                },
                s => Big(s, "cells") * (8 + Big(s, "keySize") + 8));

            yield return new(StructureId.InfiniFilter, "its newest table at 2^31 slots of 32-bit fingerprints", true,
                () => Filled(new InfiniFilter(64, fingerprintBits: 32), (f, k) => f.Add(k)),
                s => new[] { new Claim(Segments(s).Last(), "QuotientBits", InfiniFilter.MaxQuotientBits) },
                s => Segments(s).Aggregate(BigInteger.Zero, (sum, seg) =>
                    sum + TableBytes(Big(seg, "QuotientBits"), Big(seg, "FingerprintBits") + 1 + 3)));

            yield return new(StructureId.MementoFilter, "its newest table at 2^31 slots of 64 bits", true,
                () =>
                {
                    var filter = new MementoFilter(1UL << 32, 28);
                    for (ulong i = 0; i < 40; i++) filter.Add(i * 7919);
                    return filter;
                },
                s => new[] { new Claim(Segments(s).Last(), "QuotientBits", MementoFilter.MaxQuotientBits) },
                s => Segments(s).Aggregate(BigInteger.Zero, (sum, seg) =>
                    sum + TableBytes(Big(seg, "QuotientBits"), Big(seg, "FingerprintBits") + 1 + Big(seg, "MementoBits") + 3)));

            // Written compacted, at most twice k less one values.
            yield return new(StructureId.ThetaSketch, "the largest k, holding twice it less one", true,
                () => Filled(new ThetaSketch(16), (t, k) => t.Add(k), 100),
                s => new[]
                {
                    new Claim(s, "nominalEntries", ThetaSketch.MaxNominalEntries),
                    new Claim(s, "held", (int)(2 * ThetaSketch.MaxNominalEntries - 1)),
                },
                s => 8 * Big(s, "held"));

            yield return new(StructureId.TupleSketch, "the largest k, holding twice it less one", true,
                () => Filled(new TupleSketch(16), (t, k) => t.Add(k, 1.0), 100),
                s => new[]
                {
                    new Claim(s, "nominalEntries", TupleSketch.MaxNominalEntries),
                    new Claim(s, "held", (int)(2 * TupleSketch.MaxNominalEntries - 1)),
                    // The keys sorted so far: all of them, as a sketch written compacted has.
                    new Claim(s, "ordered", (int)(2 * TupleSketch.MaxNominalEntries - 1)),
                },
                s => 16 * Big(s, "held"));

            yield return new(StructureId.SetSketch, "as many registers as an array holds", true,
                () => Filled(new SetSketch(8), (t, k) => t.Add(k)),
                s => new[] { new Claim(s, "m", SetSketch.MaxRegisters) },
                s => 2 * Big(s, "m"));

            yield return new(StructureId.CountMinSketch, "its widest, at its most rows", true,
                () => Filled(new CountMinSketch(0.5, 0.5), (c, k) => c.Add(k)),
                s => new[]
                {
                    new Claim(s, "Width", CountMinSketch.MaxWidth),
                    new Claim(s, "Depth", CountMinSketch.MaxDepth),
                },
                s => 8 * Big(s, "Width") * Big(s, "Depth"));

            yield return new(StructureId.CountSketch, "its widest, at its most rows, one run", true,
                () => Filled(new CountSketch(0.5, 0.5), (c, k) => c.Add(k, 3)),
                s => new[]
                {
                    new Claim(s, "width", CountSketch.MaxWidth),
                    new Claim(s, "depth", CountSketch.MaxDepth),
                },
                s => 8 * Big(s, "width") * Big(s, "depth"));

            // Its rows may be as many as an array holds, so they are the table's five.
            yield return new(StructureId.PrivateCountMinSketch, "its widest, at depth 5", true,
                () => Filled(new PrivateCountMinSketch(64, 5, 1.0, seed: 1), (c, k) => c.Add(k)),
                s => new[] { new Claim(s, "width", PrivateCountMinSketch.MaxWidth) },
                s => 8 * Big(s, "width") * Big(s, "depth"));

            yield return new(StructureId.SublimeCountMinSketch, "its widest, at δ 1% (5 rows)", true,
                () => Filled(new SublimeCountMinSketch(0.01), (c, k) => c.Add(k)),
                s => new[] { new Claim(s, "width", SublimeCountMinSketch.MaxWidth) },
                s => 8 * ((Array)Get(s, "rows")).Length * Big(s, "width"));

            // Its rows may be as many as an array holds, so they are its default two.
            yield return new(StructureId.HeavyKeeper, "its widest, at depth 2", true,
                () => Filled(new HeavyKeeper(5, 64), (h, k) => h.Add(k)),
                s => new[] { new Claim(s, "Width", HeavyKeeper.MaxWidth) },
                s => (2 + 8) * Big(s, "Width") * Big(s, "Depth"));

            yield return new(StructureId.TopK, "its count-min sketch at its widest, at its most rows", true,
                () => Filled(new TopK(0.5, 0.5, 3), (t, k) => t.Add(k)),
                s =>
                {
                    var sketch = Get(s, "Cms");
                    return new[]
                    {
                        new Claim(sketch, "Width", CountMinSketch.MaxWidth),
                        new Claim(sketch, "Depth", CountMinSketch.MaxDepth),
                    };
                },
                // The sketch is nested whole: its 33 bytes of fields and its cells, in the
                // envelope its own payload calls for, the large form's past one array.
                s =>
                {
                    var cells = 8 * Big(Get(s, "Cms"), "Width") * Big(Get(s, "Cms"), "Depth");
                    var envelope = 33 + cells > PersistenceFormat.MaxPayloadLength
                        ? PersistenceFormat.LargeEnvelopeLength
                        : PersistenceFormat.EnvelopeLength;
                    return cells + envelope;
                });

            // Values of both signs, each store's window as long as an array.
            yield return new(StructureId.DDSketch, "a window of as many buckets as an array holds, for each sign", true,
                () =>
                {
                    var sketch = new DDSketch(0.01);
                    sketch.Add(2.0);
                    sketch.Add(-2.0);
                    return sketch;
                },
                s => new[] { "positive", "negative" }.SelectMany(store => new[]
                {
                    new Claim(Get(s, store), "MinIndex", 0),
                    new Claim(Get(s, store), "MaxIndex", Array.MaxLength - 1),
                }).ToArray(),
                s => new[] { "positive", "negative" }.Aggregate(BigInteger.Zero, (sum, store) =>
                    sum + 8 * (Big(Get(s, store), "MaxIndex") - Big(Get(s, store), "MinIndex") + 1)));

            // CAPACITY.md's build past one array: 800 million keys, ranges up to 1,024, 1e-5.
            yield return new(StructureId.Grafite, "800 million keys over ranges up to 1,024 at 1e-5", true,
                () => Grafite.Build(Enumerable.Range(0, 1000).Select(i => (ulong)i * 1009), 0.01, 64, seed: 7),
                s =>
                {
                    const ulong keys = 800_000_000;
                    var high = CapacityRows.GrafiteMostHighBits(keys, 1e-5, 1024, out var low);
                    return new[]
                    {
                        new Claim(s, "CodeCount", (int)keys),
                        new Claim(s, "LowBits", low),
                        new Claim(s, "LastCode", (high - keys - 1) << low),
                    };
                },
                s =>
                {
                    var codes = Big(s, "CodeCount");
                    var low = Big(s, "LowBits");
                    var highBits = (Big(s, "LastCode") >> (int)low) + codes + 1;
                    return 8 * ((codes * low / 64 + 2) + (highBits / 64 + 1));
                });

            yield return new(StructureId.DpswSketch, "every sketch it holds at its widest", true,
                () => Filled(new DpswSketch(100, 1.0, width: 8, depth: 2, seed: 1), (w, k) => w.Add(k), 250),
                s => DpswSketches(s).Select(sketch => new Claim(sketch, "width", PrivateCountMinSketch.MaxWidth)).ToArray(),
                s => DpswSketches(s).Aggregate(BigInteger.Zero, (sum, sketch) =>
                    sum + 8 * Big(sketch, "width") * Big(sketch, "depth")));

            // One 64-bit value: its only build is its largest.
            yield return new(StructureId.SimHashSignature, "its one value", false,
                () => new SimHashSignature(42),
                _ => Array.Empty<Claim>(),
                _ => BigInteger.Zero);
        }

        /// <summary>
        /// The structures whose payload is sized by what they hold, and the arrays and
        /// collections that hold it. A payload counts each from its own length, so its
        /// largest is the largest of those, and no field can claim more.
        /// </summary>
        private static readonly Held[] SizedByWhatTheyHold =
        {
            new(StructureId.BloomFilter, typeof(Buckets), "Data"),
            new(StructureId.BloomFilter64, typeof(Buckets64), "Data"),
            new(StructureId.CountingBloomFilter, typeof(Buckets64), "Data"),
            new(StructureId.DeletableBloomFilter, typeof(Buckets64), "Data"),
            new(StructureId.HyperLogLog, typeof(HyperLogLog), "Registers"),
            new(StructureId.UltraLogLog, typeof(UltraLogLog), "Registers"),
            new(StructureId.HyperLogLogPlus, typeof(HyperLogLogPlus), "registers", "sparse"),
            new(StructureId.BinaryFuseFilter, typeof(BinaryFuseFilter), "fingerprints"),
            new(StructureId.BloomierFilter, typeof(BloomierFilter), "cells"),
            new(StructureId.MinHashSignature, typeof(MinHashSignature), "values"),
            new(StructureId.InverseBloomFilter, typeof(InverseBloomFilter), "Array"),
            new(StructureId.CuckooBloomFilter, typeof(CuckooBloomFilter), "Fingerprints"),
            new(StructureId.VarOpt, typeof(VarOpt), "HeavyItems", "LightItems"),
            new(StructureId.ScalableBloomFilter, typeof(ScalableBloomFilter), "Filters"),
        };

        private static byte[] WriteTo(object structure) => LargeForm.Write(structure);

        private static byte[] ToByteArray(object structure) =>
            (byte[])typeof(Persistence).GetMethod(nameof(Persistence.ToByteArray))!
                .MakeGenericMethod(structure.GetType()).Invoke(null, new[] { structure })!;

        /// <summary>
        /// Every structure, at its largest build, is counted by its own <c>WriteTo</c> with
        /// nothing refused, at the length its layout gives, in the large form where a byte
        /// array refuses it. The roster is <see cref="StructureId"/>: a structure is claimed
        /// here, or is named as sized by what it holds, with the members that hold it, and
        /// is written for real by the sweep past a lowered threshold instead.
        /// </summary>
        [TestMethod]
        public void TestEveryStructureIsCountedAtItsLargestBuildWithoutARefusal()
        {
            var builds = LargestBuilds().ToArray();
            var readers = LargeForm.Readers();
            var past = 0;
            var fits = 0;
            var longRuns = 0;

            foreach (var build in builds)
            {
                var name = $"{build.Id}, {build.Shape}";
                var structure = build.Make();
                Assert.AreEqual(build.Id.ToString(), structure.GetType().Name, $"{name}: made another structure");

                // The small structure's payload in each form, the large one worked out outside
                // the writer: its ordinary payload with each run's length a u64, as FORMAT.md
                // lays it out, any envelope nested in it kept in the ordinary form its size
                // calls for.
                var ordinary = WriteTo(structure);
                long narrowSmall = ordinary.Length - PersistenceFormat.EnvelopeLength;
                long wideSmall = LargeForm.Widened(ordinary, readers, nestedLarge: false, out _, out _).LongLength;
                var sizedSmall = build.Sized(structure);

                var claims = build.Claims(structure);
                var held = claims.Select(c => Get(c.Owner, c.Member)).ToArray();
                long declared = -1;
                Exception thrown = null;
                long allocated;
                byte[] written;
                BigInteger sized;
                PayloadWriter narrow;
                Exception asArray = null;

                foreach (var claim in claims) Set(claim.Owner, claim.Member, claim.Value);
                try
                {
                    sized = build.Sized(structure);
                    narrow = PayloadWriter.Counter();
                    ((IPayloadBody)structure).WritePayload(narrow);

                    // Past one array the write goes on to the large form's header, and is
                    // stopped at the stream; within one, at the count, before the ordinary
                    // envelope's buffer is allocated at the length claimed.
                    // Counted in a no-GC region (#293), with a stream, a hook and a record of
                    // its own for each attempt, since an attempt can be repeated and what one
                    // wrote and declared is not what the next is held to.
                    FirstWriteStops stream = null;
                    allocated = NoGCRegion.Allocated(
                        () =>
                        {
                            try
                            {
                                structure.GetType().GetMethod("WriteTo", new[] { typeof(Stream) })!
                                    .Invoke(structure, new object[] { stream });
                            }
                            catch (TargetInvocationException e)
                            {
                                thrown = e.InnerException;
                            }
                            finally
                            {
                                PersistenceFormat.Counted = null;
                            }
                        },
                        first: () =>
                        {
                            stream?.Dispose();
                            stream = new FirstWriteStops();
                            declared = -1;
                            thrown = null;
                            PersistenceFormat.Counted = (body, length) =>
                            {
                                if (ReferenceEquals(body, structure))
                                {
                                    declared = length;
                                    if (!build.PastOneArray)
                                    {
                                        throw new Stopped();
                                    }
                                }
                            };
                        });

                    using var kept = stream;
                    written = stream.ToArray();

                    if (build.PastOneArray)
                    {
                        try
                        {
                            ToByteArray(structure);
                        }
                        catch (TargetInvocationException e)
                        {
                            asArray = e.InnerException;
                        }
                    }
                }
                finally
                {
                    for (var i = 0; i < claims.Length; i++) Set(claims[i].Owner, claims[i].Member, held[i]);
                }

                Assert.IsInstanceOfType<Stopped>(thrown,
                    $"{name}: the stream path refused it, or failed, before it was written: {thrown}");
                Assert.IsLessThan(CountAllocates, allocated,
                    $"{name}: writing it as far as the stream allocated {allocated:N0} bytes, so something of the payload was built");

                var large = narrow.Length > PersistenceFormat.LargeFormThreshold;
                Assert.AreEqual(build.PastOneArray, large,
                    $"{name}: a payload of {narrow.Length:N0} bytes is {(large ? "past" : "within")} what one array holds, and the claim says otherwise");

                var expected = (large ? wideSmall : narrowSmall) + (sized - sizedSmall);
                Assert.AreEqual(expected, (BigInteger)declared,
                    $"{name}: WriteTo counted {declared:N0} bytes, and the layout gives {expected:N0}");

                if (large)
                {
                    Assert.HasCount(PersistenceFormat.LargeHeaderLength, written,
                        $"{name}: the stream was not given the large form's header first, whole");
                    Assert.IsTrue(LargeForm.IsLarge(written), $"{name}: the header written does not flag the large form");
                    Assert.AreEqual((ushort)build.Id, BinaryPrimitives.ReadUInt16LittleEndian(written.AsSpan(6)),
                        $"{name}: the header written names another structure");
                    Assert.AreEqual((ulong)declared, BinaryPrimitives.ReadUInt64LittleEndian(written.AsSpan(10)),
                        $"{name}: the header written does not declare the count");

                    var refused = Assert.IsInstanceOfType<InvalidOperationException>(asArray,
                        $"{name}: ToByteArray of a payload past one array was not refused: {asArray}");
                    StringAssert.Contains(refused.Message, "WriteTo(Stream)", $"{name}: {refused.Message}");
                    past++;
                }
                else
                {
                    Assert.IsEmpty(written, $"{name}: something was written before its count was made");
                    fits++;
                }

                if (narrow.LongestCounted > Array.MaxLength)
                {
                    longRuns++;
                }

                CollectionAssert.AreEqual(ordinary, WriteTo(structure), $"{name}: the structure writes otherwise once its shape is put back");
            }

            Assert.IsGreaterThan(0, past, "no claim was past one array, so the stream was never asked to carry one");
            Assert.IsGreaterThan(0, fits, "no claim fit one array, so the ordinary envelope's count was never claimed");
            Assert.IsGreaterThan(0, longRuns, "no claim held a run past one array, so the large form's u64 runs were never needed");

            foreach (var held in SizedByWhatTheyHold)
            {
                foreach (var member in held.Member)
                {
                    var found = (MemberInfo)held.Type.GetField(member, Members) ?? held.Type.GetProperty(member, Members);
                    Assert.IsNotNull(found, $"{held.Id} is sized by {held.Type.Name}.{member}, which it does not have");
                    var type = found is FieldInfo f ? f.FieldType : ((PropertyInfo)found).PropertyType;
                    Assert.IsTrue(type.IsArray || typeof(ICollection).IsAssignableFrom(type),
                        $"{held.Id} is named as sized by what it holds in {held.Type.Name}.{member}, a {type.Name}, " +
                        "which is not an array or a collection: claim its largest build instead");
                }
            }

            var sweep = typeof(TestTheStreamSavesAsBuilt).GetMethod(
                nameof(TestEveryStructurePastTheThresholdIsWrittenWithItsLongestRunPastTheBound));
            Assert.IsNotNull(sweep?.GetCustomAttribute<TestMethodAttribute>(),
                "the structures sized by what they hold are written for real by a sweep that is not a test");

            StructureRoster.AssertCoversEveryStructure(
                "largest build through the stream",
                builds.Select(b => b.Id).Distinct(),
                SizedByWhatTheyHold.Select(h => (h.Id,
                    $"sized by what it holds, in {string.Join(" and ", h.Member)}, which no claim lengthens without " +
                    "allocating it: written for real past a lowered threshold and longest run instead")).ToArray());
        }

        /// <summary>
        /// Every structure, its payload a byte past the threshold and its longest run a byte
        /// past the longest the ordinary envelope holds, is written by <c>WriteTo</c> in the
        /// large form, which reads back as the structure. The same bound refuses it in the
        /// ordinary envelope, so it was met. The roster is <see cref="StructureId"/>, through
        /// the shared sample payloads.
        /// </summary>
        /// <remarks>
        /// The thresholds that ship are 2,147,483,573 bytes of payload and a run of one
        /// array; a payload past the first, holding a run past the second, is what a
        /// structure past 2 GiB writes. Lowered to the sample's own sizes on this thread,
        /// they send it down the code that payload takes. A structure that holds no run is
        /// written past the threshold alone.
        /// </remarks>
        [TestMethod]
        public void TestEveryStructurePastTheThresholdIsWrittenWithItsLongestRunPastTheBound()
        {
            var swept = new List<StructureId>();
            var withARun = 0;

            foreach (var (id, read, bytes) in TestPersistenceAllStructures.SamplePayloads())
            {
                var structure = read(bytes);
                var ordinary = WriteTo(structure);
                long length = ordinary.Length - PersistenceFormat.EnvelopeLength;

                var counter = PayloadWriter.Counter();
                ((IPayloadBody)structure).WritePayload(counter);
                Assert.AreEqual(length, counter.Length, $"{id}: the count is not the payload written");
                var longest = counter.LongestCounted;
                long? bound = longest > 0 ? longest - 1 : null;

                if (bound is long shorter)
                {
                    var refused = Assert.ThrowsExactly<TargetInvocationException>(
                        () => WithLongestRun(shorter, () => WriteTo(structure)),
                        $"{id}: its longest run, {longest:N0} bytes, was written in the ordinary envelope past a bound of {shorter:N0}");
                    var ex = Assert.IsInstanceOfType<InvalidOperationException>(refused.InnerException, $"{id}: {refused.InnerException}");
                    StringAssert.Contains(ex.Message, longest.ToString("N0"), $"{id}: the refusal is not for its run: {ex.Message}");
                    withARun++;
                }

                var large = WithLongestRun(bound, () => LargeForm.WriteLarge(structure, length - 1));
                Assert.IsTrue(LargeForm.IsLarge(large), $"{id}: a payload a byte past the threshold was not written in the large form");
                CollectionAssert.AreEqual(ordinary, WriteTo(read(large)),
                    $"{id}: written past the threshold, its run past the bound, it does not read back as the structure");
                swept.Add(id);
            }

            Assert.IsGreaterThan(0, withARun, "no sample held a run, so the longest run was never past the bound");
            StructureRoster.AssertCoversEveryStructure("stream past the threshold and the longest run", swept);
        }

        private static T WithLongestRun<T>(long? bound, Func<T> write)
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
    }
}
