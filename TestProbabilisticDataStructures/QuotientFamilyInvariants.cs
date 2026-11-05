using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Structural invariant checkers for the quotient-style filters and the cuckoo
    /// filter, run after every operation in the sequence tests.
    /// </summary>
    /// <remarks>
    /// The quotient filter, and the Infini and Memento segments built on the same
    /// table, find a run by walking back to the nearest unshifted slot and forward
    /// again pairing occupied quotients with runs. Those walks are while-true loops,
    /// and whether they terminate -- and whether they land on the right run -- rests
    /// entirely on three bits per slot being mutually consistent. A behavioural test
    /// sees a violation only once some later query happens to walk through it.
    /// <para>
    /// So the table is decoded here independently of the library's walks: one pass
    /// round the circle from a cluster start, with a queue of occupied quotients
    /// waiting for their runs. Every slot's three bits are checked against what that
    /// pass says they must be, and the pass yields every stored entry paired with its
    /// owning quotient, which the caller then holds to an exact model. The slot
    /// unpacking is written out again here rather than borrowed, so that a packing
    /// defect in the library is not shared by its checker.
    /// </para>
    /// <para>
    /// What is read: the quotient filter through its persisted payload, whose layout
    /// FORMAT.md freezes; the segments and the cuckoo table through the internal
    /// arrays they already expose to tests. Nothing here lives in the library.
    /// </para>
    /// </remarks>
    internal static class QuotientFamilyInvariants
    {
        private const ulong Occupied = 1;
        private const ulong Continuation = 2;
        private const ulong Shifted = 4;
        private const int EnvelopeHeader = 14;

        /// <summary>One slot of a table packed end to end at a fixed width.</summary>
        internal static ulong Slot(ulong[] words, int bitsPerSlot, uint index)
        {
            var bit = (long)index * bitsPerSlot;
            var word = (int)(bit >> 6);
            var offset = (int)(bit & 63);
            UInt128 pair = words[word];
            if (word + 1 < words.Length)
            {
                pair |= (UInt128)words[word + 1] << 64;
            }
            var mask = bitsPerSlot >= 64 ? ulong.MaxValue : (1UL << bitsPerSlot) - 1;
            return (ulong)(pair >> offset) & mask;
        }

        /// <summary>
        /// The bits after the last slot are never part of any slot, so they must still
        /// be zero. A write that spills past its slot shows up here first.
        /// </summary>
        private static void SpareBitsAreClear(string what, ulong[] words, int bitsPerSlot, uint slots)
        {
            var used = (long)slots * bitsPerSlot;
            for (var bit = used; bit < (long)words.Length * 64; bit++)
            {
                if (((words[bit >> 6] >> (int)(bit & 63)) & 1) != 0)
                {
                    Assert.Fail($"{what}: bit {bit} lies past the last slot (which ends at " +
                        $"bit {used}) and is set; a slot write spilled past its slot.");
                }
            }
        }

        /// <summary>
        /// Decodes a quotient table's metadata from first principles and returns every
        /// entry with the quotient that owns it, in cluster order.
        /// </summary>
        /// <param name="what">Names the table in failure messages.</param>
        /// <param name="slots">The slot count, a power of two.</param>
        /// <param name="slot">Reads one slot: metadata in the low three bits.</param>
        /// <param name="sortedRuns">Whether a run's fields must be non-decreasing.</param>
        /// <param name="nonZeroFields">Whether a stored field may never be zero.</param>
        internal static List<(uint Quotient, ulong Field)> DecodeTable(
            string what, uint slots, Func<uint, ulong> slot, bool sortedRuns, bool nonZeroFields)
        {
            // Start somewhere a cluster cannot be running through: an empty slot, or an
            // entry sitting in its own canonical slot. A table with neither has no
            // cluster start, and every backward walk in the library circles forever.
            var anchor = -1L;
            for (var i = 0u; i < slots; i++)
            {
                var v = slot(i);
                if ((v & 7) == 0 || (v & Shifted) == 0)
                {
                    anchor = i;
                    break;
                }
            }
            if (anchor < 0)
            {
                Assert.Fail($"{what}: every one of its {slots} slots holds a shifted " +
                    "entry, so no cluster has a start and the backward walks never stop.");
            }

            var pending = new Queue<uint>();
            var entries = new List<(uint Quotient, ulong Field)>();
            var inRun = false;
            var owner = 0u;
            var last = 0UL;

            for (var k = 0u; k < slots; k++)
            {
                var i = (uint)((anchor + k) % slots);
                var v = slot(i);
                var field = v >> 3;

                if ((v & Occupied) != 0)
                {
                    pending.Enqueue(i);
                }

                if ((v & 7) == 0)
                {
                    if (field != 0)
                    {
                        Assert.Fail($"{what}: slot {i} holds no entry but carries field " +
                            $"{field}; an emptied slot must be cleared whole.");
                    }
                    if (pending.Count > 0)
                    {
                        Assert.Fail($"{what}: slot {i} is empty while quotient " +
                            $"{pending.Peek()} is marked occupied and its run has not " +
                            "begun. A run starts inside its own cluster or not at all.");
                    }
                    inRun = false;
                    continue;
                }

                if ((v & Continuation) == 0)
                {
                    if (pending.Count == 0)
                    {
                        Assert.Fail($"{what}: slot {i} starts a run, but no occupied " +
                            "quotient is waiting for one: a run with no owner.");
                    }
                    owner = pending.Dequeue();
                }
                else if (!inRun)
                {
                    Assert.Fail($"{what}: slot {i} is marked a continuation, but the " +
                        "slot before it is empty or ends its cluster.");
                }
                else if (sortedRuns && field < last)
                {
                    Assert.Fail($"{what}: the run of quotient {owner} is out of order at " +
                        $"slot {i} ({field} after {last}); lookups stop at the first " +
                        "larger value and would miss what follows.");
                }

                var shifted = (v & Shifted) != 0;
                if (shifted != (i != owner))
                {
                    Assert.Fail($"{what}: slot {i} holds an entry of quotient {owner}, so " +
                        $"its shifted bit must be {i != owner}, and it is {shifted}.");
                }
                if (nonZeroFields && field == 0)
                {
                    Assert.Fail($"{what}: slot {i} stores a zero field, which the age " +
                        "counter's terminating one makes impossible.");
                }

                entries.Add((owner, field));
                inRun = true;
                last = field;
            }

            if (pending.Count > 0)
            {
                Assert.Fail($"{what}: quotient {pending.Peek()} is marked occupied and " +
                    "owns no run.");
            }

            return entries;
        }

        /// <summary>
        /// Checks a quotient filter and returns its stored fingerprints, quotient above
        /// remainder, sorted. The payload is returned too, as the state's identity.
        /// </summary>
        internal static ulong[] CheckQuotientFilter(QuotientFilter filter, out byte[] payload)
        {
            payload = filter.ToByteArray();
            var p = payload.AsSpan(EnvelopeHeader);
            var quotientBits = (int)BinaryPrimitives.ReadUInt32LittleEndian(p);
            var remainderBits = (int)BinaryPrimitives.ReadUInt32LittleEndian(p[4..]);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(p[8..]);
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(p[12..]);

            var words = new ulong[length / 8];
            for (var w = 0; w < words.Length; w++)
            {
                words[w] = BinaryPrimitives.ReadUInt64LittleEndian(p[(16 + 8 * w)..]);
            }

            var slots = 1u << quotientBits;
            var bits = remainderBits + 3;
            Assert.AreEqual((((long)slots * bits) >> 6) + 2, words.LongLength,
                "quotient filter: the table is not the length its shape implies.");
            SpareBitsAreClear("quotient filter", words, bits, slots);

            var entries = DecodeTable("quotient filter", slots,
                i => Slot(words, bits, i), sortedRuns: true, nonZeroFields: false);
            Assert.AreEqual(count, (uint)entries.Count,
                "quotient filter: the count disagrees with the entries the table holds.");

            return entries
                .Select(e => ((ulong)e.Quotient << remainderBits) | e.Field)
                .OrderBy(f => f)
                .ToArray();
        }

        /// <summary>
        /// One entry of an Infini or Memento segment, restated as what it remembers of
        /// its hash: the low <see cref="Length"/> bits, which must equal
        /// <see cref="Suffix"/> for a hash to match it.
        /// </summary>
        internal readonly record struct Remembered(int Segment, ulong Suffix, int Length, int Age, ulong Memento);

        /// <summary>
        /// The unary age and the fingerprint inside a fluid field of F + 1 bits.
        /// Restated: the terminator is the highest set bit, the age is how far below
        /// bit F it sits, and the fingerprint is everything under it.
        /// </summary>
        private static (int Age, ulong Fingerprint, int Remaining) Fluid(
            string what, ulong fluid, int fingerprintBits)
        {
            if (fluid == 0 || fluid >= (1UL << (fingerprintBits + 1)))
            {
                Assert.Fail($"{what}: fluid fingerprint {fluid} is not an age counter " +
                    $"and fingerprint of {fingerprintBits} + 1 bits.");
            }
            var top = 63 - BitOperations.LeadingZeroCount(fluid);
            return (fingerprintBits - top, fluid & ((1UL << top) - 1), top);
        }

        /// <summary>
        /// Checks every segment of an InfiniFilter and returns what each entry
        /// remembers. Policy is not restated -- when to expand is the filter's choice --
        /// only what must hold whatever it chose.
        /// </summary>
        internal static List<Remembered> CheckInfiniFilter(InfiniFilter filter)
        {
            var result = new List<Remembered>();
            var total = 0UL;

            Assert.IsGreaterThanOrEqualTo(1, filter.Segments.Count, "infini: no tables.");
            Assert.IsGreaterThanOrEqualTo(2, filter.Segments[0].QuotientBits - (int)filter.Expansions,
                "infini: the active table is smaller than its expansions allow.");

            for (var s = 0; s < filter.Segments.Count; s++)
            {
                var segment = filter.Segments[s];
                var what = $"infini segment {s}";
                var q = segment.QuotientBits;
                var f = segment.FingerprintBits;
                var bits = f + 1 + 3;

                Assert.AreEqual(1u << q, segment.Slots, $"{what}: slot count.");
                Assert.AreEqual((((long)segment.Slots * bits) >> 6) + 2, segment.Data.LongLength,
                    $"{what}: the table is not the length its shape implies.");
                SpareBitsAreClear(what, segment.Data, bits, segment.Slots);

                var entries = DecodeTable(what, segment.Slots,
                    i => Slot(segment.Data, bits, i), sortedRuns: true, nonZeroFields: true);
                Assert.AreEqual(segment.Count, (uint)entries.Count,
                    $"{what}: the count disagrees with the entries the table holds.");

                foreach (var (quotient, field) in entries)
                {
                    var (age, fingerprint, remaining) = Fluid(what, field, f);
                    if (s == 0 && age > filter.Expansions)
                    {
                        Assert.Fail($"{what}: an entry is {age} expansions old, and the " +
                            $"active table has expanded only {filter.Expansions} times.");
                    }
                    result.Add(new Remembered(s, quotient | (fingerprint << q), q + remaining, age, 0));
                }
                total += segment.Count;
            }

            Assert.AreEqual(filter.Count(), total,
                "infini: the item count disagrees with the entries the tables hold; one " +
                "addition is one entry, through every expansion and shed.");
            return result;
        }

        /// <summary>
        /// Checks every segment of a MementoFilter -- the table, then each run's
        /// keepsake boxes -- and returns one remembered entry per stored memento.
        /// </summary>
        internal static List<Remembered> CheckMementoFilter(MementoFilter filter)
        {
            var result = new List<Remembered>();

            for (var s = 0; s < filter.Segments.Count; s++)
            {
                var segment = filter.Segments[s];
                var what = $"memento segment {s}";
                var q = segment.QuotientBits;
                var f = segment.FingerprintBits;
                var bits = f + 1 + segment.MementoBits + 3;

                Assert.AreEqual(filter.MementoBits, segment.MementoBits, $"{what}: memento width.");
                Assert.AreEqual(1u << q, segment.Slots, $"{what}: slot count.");
                Assert.AreEqual((((long)segment.Slots * bits) >> 6) + 2, segment.Data.LongLength,
                    $"{what}: the table is not the length its shape implies.");
                SpareBitsAreClear(what, segment.Data, bits, segment.Slots);

                // Runs here are in the codec's order, not sorted: a box spills across
                // slots whose values fall as well as rise.
                var entries = DecodeTable(what, segment.Slots,
                    i => Slot(segment.Data, bits, i), sortedRuns: false, nonZeroFields: false);
                Assert.AreEqual(segment.Count, (uint)entries.Count,
                    $"{what}: the count disagrees with the slots the table uses.");

                for (var start = 0; start < entries.Count;)
                {
                    var quotient = entries[start].Quotient;
                    var end = start;
                    while (end < entries.Count && entries[end].Quotient == quotient)
                    {
                        end++;
                    }
                    var fields = entries.Skip(start).Take(end - start).Select(e => e.Field).ToList();
                    start = end;

                    var boxes = segment.Codec.Decode(fields);
                    CollectionAssert.AreEqual(fields, segment.Codec.Encode(boxes),
                        $"{what}: the run of quotient {quotient} is not the canonical " +
                        "encoding of the boxes it decodes to.");

                    var previous = 0UL;
                    foreach (var box in boxes)
                    {
                        if (box.Fingerprint <= previous)
                        {
                            Assert.Fail($"{what}: quotient {quotient}'s boxes are not in " +
                                "strictly increasing fingerprint order.");
                        }
                        previous = box.Fingerprint;

                        var (age, fingerprint, remaining) = Fluid(what, box.Fingerprint, f);
                        if (s == 0 && age > filter.Expansions)
                        {
                            Assert.Fail($"{what}: a box is {age} expansions old, and the " +
                                $"active table has expanded only {filter.Expansions} times.");
                        }

                        if (box.Mementos.Count == 0)
                        {
                            Assert.Fail($"{what}: an empty box was stored.");
                        }
                        for (var m = 1; m < box.Mementos.Count; m++)
                        {
                            if (box.Mementos[m] < box.Mementos[m - 1])
                            {
                                Assert.Fail($"{what}: a box's mementos are out of order.");
                            }
                        }
                        foreach (var memento in box.Mementos)
                        {
                            result.Add(new Remembered(s, quotient | (fingerprint << q), q + remaining, age, memento));
                        }
                    }
                }
            }

            Assert.AreEqual(filter.Count(), (ulong)result.Count,
                "memento: the key count disagrees with the mementos the boxes hold.");
            return result;
        }

        /// <summary>
        /// Holds remembered entries to a multiset of live keys: there must be a
        /// one-to-one assignment of entries to keys in which every entry's remembered
        /// bits are the low bits of its key's hash (and, for Memento, its memento is the
        /// key's). That is stronger than "every key is found": two keys leaning on one
        /// shared entry pass the lookup check and fail this one, which is the state a
        /// wrong-entry deletion leaves behind before anyone has asked the right query.
        /// </summary>
        /// <remarks>
        /// The sets of keys an entry can stand for are nested or disjoint -- each is
        /// "hashes ending in these bits" -- so assigning the most specific entries
        /// first, each to any free key it matches, finds a perfect assignment whenever
        /// one exists.
        /// </remarks>
        internal static void AssignEntriesToKeys(
            string what, List<Remembered> entries, IReadOnlyList<(ulong Hash, ulong Memento)> keys)
        {
            Assert.AreEqual(keys.Count, entries.Count,
                $"{what}: {entries.Count} entries are stored for {keys.Count} live keys.");

            var taken = new bool[keys.Count];
            var lengths = entries.Select(e => e.Length).Distinct().OrderByDescending(l => l).ToArray();
            foreach (var length in lengths)
            {
                var mask = length >= 64 ? ulong.MaxValue : (1UL << length) - 1;
                var free = new Dictionary<(ulong, ulong), Stack<int>>();
                for (var k = 0; k < keys.Count; k++)
                {
                    if (taken[k])
                    {
                        continue;
                    }
                    var key = (keys[k].Hash & mask, keys[k].Memento);
                    if (!free.TryGetValue(key, out var stack))
                    {
                        free[key] = stack = new Stack<int>();
                    }
                    stack.Push(k);
                }

                foreach (var entry in entries.Where(e => e.Length == length))
                {
                    if (!free.TryGetValue((entry.Suffix, entry.Memento), out var stack) || stack.Count == 0)
                    {
                        Assert.Fail($"{what}: an entry in segment {entry.Segment} remembers " +
                            $"the low {length} bits {entry.Suffix} (age {entry.Age}, memento " +
                            $"{entry.Memento}) and no live key is left for it to stand for: " +
                            "either it was invented, or the key it belonged to lost it and " +
                            "is now leaning on someone else's entry.");
                    }
                    taken[stack.Pop()] = true;
                }
            }
        }

        /// <summary>
        /// A cuckoo filter's contents as the multiset the model predicts: each stored
        /// fingerprint with the unordered pair of buckets it may live in, derived from
        /// the bucket it is in. Entries are unpacked independently of the filter, and an
        /// entry the map marks empty has to hold nothing, nor the map anything past the last
        /// entry: a payload is the whole table (#230), so what an empty entry held would be
        /// written, and its reader refuses it.
        /// </summary>
        internal static Dictionary<(ulong Fingerprint, ulong Low, ulong High), int> CheckCuckooFilter(
            CuckooBloomFilter filter, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            var m = filter.M;
            var b = filter.B;
            var bits = (int)filter.FingerprintBits;
            Assert.AreEqual(0UL, m & (m - 1), "cuckoo: bucket count is not a power of two.");

            bool Marked(ulong e) => (filter.Occupied[e / 64] >> (int)(e % 64) & 1) != 0;

            var contents = new Dictionary<(ulong, ulong, ulong), int>();
            var held = 0UL;
            for (var i = 0UL; i < m; i++)
            {
                for (var j = 0u; j < b; j++)
                {
                    var e = i * b + j;
                    var f = Slot(filter.Fingerprints, bits, checked((uint)e));
                    if (!Marked(e))
                    {
                        Assert.AreEqual(0UL, f, $"cuckoo: entry {e} is empty and holds a fingerprint.");
                        continue;
                    }
                    held++;
                    var key = CuckooPlacement(f, i, m, bits, hash);
                    contents[key] = contents.GetValueOrDefault(key) + 1;
                }
            }

            for (var e = m * b; e < (ulong)filter.Occupied.Length * 64; e++)
            {
                Assert.IsFalse(Marked(e), $"cuckoo: the map marks place {e}, past the last entry.");
            }

            Assert.AreEqual(filter.Count(), held,
                "cuckoo: the count disagrees with the occupied entries.");
            return contents;
        }

        /// <summary>
        /// The unordered bucket pair of a fingerprint found in a given bucket: the
        /// partner is the bucket XOR the hash of the fingerprint's bytes, reduced by the
        /// bucket count, a power of two, in 64 bits.
        /// </summary>
        internal static (ulong, ulong, ulong) CuckooPlacement(
            ulong fingerprint, ulong bucket, ulong m, int bits, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, fingerprint);
            var partner = (bucket ^ hash(bytes[..((bits + 7) / 8)])) & (m - 1);
            return (fingerprint, Math.Min(bucket, partner), Math.Max(bucket, partner));
        }

        /// <summary>
        /// What the model says a key contributes to a cuckoo filter, restated from the
        /// paper's partial-key scheme as this library documents it: the first bucket is the
        /// hash of the key's digest, all 64 bits of it, reduced by the bucket count.
        /// </summary>
        internal static (ulong, ulong, ulong) CuckooKey(
            ReadOnlySpan<byte> key, ulong m, int bits, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            Span<byte> digest = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(digest, hash(key));
            var mask = bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;
            var f = BinaryPrimitives.ReadUInt64LittleEndian(digest) & mask;
            var i1 = hash(digest) & (m - 1);
            return CuckooPlacement(f, i1, m, bits, hash);
        }
    }
}
