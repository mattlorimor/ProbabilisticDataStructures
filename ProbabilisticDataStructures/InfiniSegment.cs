using System;
using System.Collections.Generic;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// One quotient filter in an InfiniFilter's chain: the paper's "basic
    /// InfiniFilter".
    /// </summary>
    /// <remarks>
    /// This is an ordinary quotient filter -- the same three metadata bits, the same
    /// runs and clusters -- with one change to what a slot holds. Instead of a
    /// fixed-width remainder, the slot holds a unary age counter followed by whatever
    /// fingerprint bits remain. The counter says how many expansions ago the entry was
    /// inserted, and it is self-delimiting, so an entry can be read without knowing in
    /// advance how long its fingerprint is. The counter terminates in a one rather
    /// than a zero, so that no entry is ever encoded as zero and that value stays
    /// free to mean "no entry here" -- which the Memento range filter needs of this
    /// substrate.
    /// <para>
    /// The point of that arrangement is expansion. Doubling a quotient filter needs one
    /// more bit of address per entry, and the only place to find it without going back
    /// to the original keys is the fingerprint. Sacrificing a bit from every entry on
    /// every expansion would shorten all fingerprints alike; here the sacrifice is
    /// recorded per entry, so an entry inserted after the last expansion still carries
    /// its full fingerprint. Since each expansion doubles the capacity, most entries
    /// are always young, and the average fingerprint stays long.
    /// </para>
    /// </remarks>
    internal sealed class InfiniSegment
    {
        private const ulong OccupiedBit = 1;
        private const ulong ContinuationBit = 2;
        private const ulong ShiftedBit = 4;
        private const int MetadataBits = 3;

        /// <summary>
        /// How many bits of the hash address a slot: the filter holds 2^this slots.
        /// </summary>
        internal int QuotientBits { get; private set; }

        /// <summary>
        /// Every slot read this segment has ever performed, so the work-bound
        /// tests can hold the metadata walks to the work the paper's invariants
        /// imply, rather than to a wall clock.
        /// </summary>
        internal long SlotReads { get; private set; }

        /// <summary>
        /// The fingerprint length a freshly inserted entry gets. The age counter and
        /// fingerprint together always occupy this many bits plus one.
        /// </summary>
        internal int FingerprintBits { get; private set; }

        /// <summary>
        /// The slots, 2^<see cref="QuotientBits"/>, and the entries held in them. Both are
        /// 64-bit, since a table can have 2^32 slots or more (#231): it is as wide as one array
        /// holds its words, which <see cref="InfiniFilter.QuotientBitsLimitFor(int)"/> works out.
        /// </summary>
        internal ulong Slots { get; private set; }

        /// <inheritdoc cref="Slots"/>
        internal ulong Count { get; set; }

        internal ulong[] Data { get; set; } = null!;

        private ulong slotMaskIndex;
        private int bitsPerSlot;
        private ulong slotMask;
        private int fieldBits;

        /// <summary>
        /// The words a table of 2^<paramref name="quotientBits"/> slots is held in: the
        /// slots end to end, each the fingerprint's field and the metadata, and two words
        /// more for the reads and writes that straddle the end. In 64 bits, for every width
        /// the reader lets through.
        /// </summary>
        internal static long Words(int quotientBits, int fingerprintBits) =>
            (((1L << quotientBits) * (fingerprintBits + 1 + MetadataBits)) >> 6) + 2;

        /// <summary>
        /// The most words a table may have, since a table is one array of them:
        /// <see cref="Array.MaxLength"/>, or less on a thread whose
        /// <see cref="LoweredLongestTable"/> is set.
        /// </summary>
        internal static long LongestTable => LoweredLongestTable ?? Array.MaxLength;

        /// <summary>
        /// A lower bound for <see cref="LongestTable"/> on the calling thread, so that the suite
        /// can show a table stop where its array is full at sizes it can build; null otherwise,
        /// as it is in every thread but a test's.
        /// </summary>
        [ThreadStatic]
        internal static long? LoweredLongestTable;

        /// <summary>
        /// Where a slot's bits begin in a table of slots this wide: the word, and the bit
        /// within it. Every read and write of a slot is placed by this, which is static so that
        /// a test can ask it of a slot in a table too large to build.
        /// </summary>
        internal static (int Word, int Offset) Locate(ulong slot, int bitsPerSlot)
        {
            var bit = slot * (ulong)bitsPerSlot;
            return ((int)(bit >> 6), (int)(bit & 63));
        }

        /// <summary>
        /// The slot after <paramref name="slot"/> in a table whose slot indices are masked by
        /// <paramref name="mask"/>, wrapping from the last to the first. Static, as
        /// <see cref="Locate"/> is.
        /// </summary>
        internal static ulong Next(ulong slot, ulong mask) => (slot + 1) & mask;

        /// <summary>The slot before, as <see cref="Next(ulong, ulong)"/>.</summary>
        internal static ulong Prev(ulong slot, ulong mask) => (slot - 1) & mask;

        /// <summary>
        /// The quotient an entry has once its table of 2^<paramref name="quotientBits"/> slots
        /// doubles: its own, with the fingerprint's low bit, which it gives up, as the new top
        /// bit of its address. Static, as <see cref="Locate"/> is; the doubling places every
        /// entry it moves by it.
        /// </summary>
        internal static ulong Doubled(ulong quotient, ulong fingerprint, int quotientBits) =>
            quotient + ((fingerprint & 1) == 1 ? 1UL << quotientBits : 0UL);

        /// <summary>
        /// Where an entry that has gone void lands in the table down the chain of
        /// 2^<paramref name="quotientBits"/> slots: the low bits of the address it had, and
        /// what is left of that address above them, which it carries as its fingerprint.
        /// Static, as <see cref="Locate"/> is; every void entry is moved by it.
        /// </summary>
        internal static (ulong Quotient, ulong Carried) Shed(ulong address, int quotientBits) =>
            (address & ((1UL << quotientBits) - 1), address >> quotientBits);

        /// <summary>The words of this segment's table, from its two widths.</summary>
        internal long TableWords => Words(this.QuotientBits, this.FingerprintBits);

        internal InfiniSegment(int quotientBits, int fingerprintBits)
        {
            Shape(quotientBits, fingerprintBits);
        }

        private void Shape(int quotientBits, int fingerprintBits)
        {
            this.QuotientBits = quotientBits;
            this.FingerprintBits = fingerprintBits;
            this.Slots = 1UL << quotientBits;
            this.slotMaskIndex = this.Slots - 1;

            // The age counter and the fingerprint share one field of F + 1 bits: a
            // counter of a ones and a terminating zero leaves F - a bits of
            // fingerprint, so the field width never changes as entries age.
            this.fieldBits = fingerprintBits + 1;
            this.bitsPerSlot = this.fieldBits + MetadataBits;
            this.slotMask = (1UL << this.bitsPerSlot) - 1;

            // The filter stops its tables short of this (InfiniFilter.QuotientBitsLimitFor), and
            // its reader refuses a table past it, so this names the limit to a caller that
            // passed it rather than failing in the allocation.
            var words = Words(quotientBits, fingerprintBits);
            if (words > LongestTable)
            {
                throw new ArgumentOutOfRangeException(nameof(quotientBits), quotientBits,
                    $"A table of {this.Slots:N0} slots of {this.bitsPerSlot} bits is {words:N0} " +
                    $"words, and a table is one array, which holds at most {LongestTable:N0}.");
            }
            this.Data = new ulong[words];
        }

        /// <summary>
        /// A segment of a stored shape and count, its table allocated and every word of it
        /// nought, for the reader to read the stored words straight into (#313), and then to
        /// check its metadata.
        /// </summary>
        internal static InfiniSegment Restore(int quotientBits, int fingerprintBits, ulong count) =>
            new(quotientBits, fingerprintBits) { Count = count };

        /// <summary>
        /// Empties the table where it is, leaving it as a new one of its shape would be: no
        /// entries, every word nought, and no slot read counted. It allocates nothing.
        /// </summary>
        internal void Clear()
        {
            Array.Clear(this.Data);
            this.Count = 0;
            this.SlotReads = 0;
        }

        /// <summary>
        /// How full the segment is, between zero and one.
        /// </summary>
        internal double Load => (double)this.Count / this.Slots;

        /// <summary>
        /// Encodes an age and a fingerprint into the shared field: the counter in the
        /// high bits, the fingerprint in what is left.
        /// </summary>
        internal ulong EncodeField(int age, ulong fingerprint)
        {
            // A counter of age a is a zeros followed by a one, occupying a + 1 bits,
            // which is what makes it parsable without a length being stored.
            //
            // The terminator is a one rather than a zero so that every field has a set
            // bit somewhere, and therefore no entry is ever encoded as zero. That
            // leaves zero free to mean something else -- which is what the Memento
            // range filter needs of this substrate, where a vacant zero field marks
            // the boundary of a variable-length group. Costs nothing here, and cannot
            // be changed once payloads written in this format exist.
            var remaining = this.FingerprintBits - age;
            return (1UL << remaining) | (fingerprint & ((1UL << remaining) - 1));
        }

        /// <summary>
        /// How many expansions ago an entry was inserted, read from the leading ones
        /// of its field.
        /// </summary>
        internal int AgeOf(ulong field)
        {
            var age = 0;
            for (var bit = this.fieldBits - 1; bit >= 0; bit--)
            {
                if ((field & (1UL << bit)) != 0)
                {
                    break;
                }
                age++;
            }
            return age;
        }

        /// <summary>
        /// The fingerprint an entry still carries, which is shorter the older it is.
        /// </summary>
        internal ulong FingerprintOf(ulong field, int age)
        {
            var remaining = this.FingerprintBits - age;
            return remaining <= 0 ? 0 : field & ((1UL << remaining) - 1);
        }

        /// <summary>
        /// An entry that has spent every fingerprint bit it had. It cannot be given to
        /// an expanded filter, because there is nothing left to sacrifice for the
        /// address bit an expansion needs.
        /// </summary>
        internal bool IsVoid(ulong field) => AgeOf(field) >= this.FingerprintBits;

        /// <summary>
        /// The quotient and fingerprint a hash yields in this segment.
        /// </summary>
        /// <remarks>
        /// The address is the low bits and the fingerprint the bits above it, which is
        /// the opposite way round from the usual quotient filter. It has to be: an
        /// expansion needs the next bit up, and taking it from the bottom of the
        /// fingerprint is what makes an entry's address a growing prefix of its hash
        /// rather than something that has to be recomputed from the key.
        /// </remarks>
        internal (ulong Quotient, ulong Fingerprint) Split(ulong hash) =>
            Split(hash, this.QuotientBits, this.FingerprintBits);

        /// <summary>
        /// The quotient and fingerprint a hash yields in a table of these widths, which
        /// <see cref="Split(ulong)"/> is. Static, as <see cref="Locate"/> is.
        /// </summary>
        internal static (ulong Quotient, ulong Fingerprint) Split(ulong hash, int quotientBits, int fingerprintBits) =>
            (hash & ((1UL << quotientBits) - 1), (hash >> quotientBits) & ((1UL << fingerprintBits) - 1));

        /// <summary>
        /// Whether the segment holds anything matching the hash.
        /// </summary>
        internal bool Holds(ulong hash)
        {
            var (quotient, _) = Split(hash);
            if (!IsOccupied(quotient))
            {
                return false;
            }

            var slot = FindRunStart(quotient);
            do
            {
                if (Matches(ReadField(slot), hash))
                {
                    return true;
                }
                slot = Next(slot);
            }
            while (IsContinuation(slot));

            return false;
        }

        /// <summary>
        /// Whether a stored entry could stand for this hash. An entry matches on as
        /// many fingerprint bits as it has left, so an older entry matches more
        /// readily -- that is the price of having expanded.
        /// </summary>
        private bool Matches(ulong field, ulong hash)
        {
            var age = AgeOf(field);
            var remaining = this.FingerprintBits - age;
            if (remaining <= 0)
            {
                // A void entry has no fingerprint at all, so anything landing in its
                // run matches it.
                return true;
            }

            var expected = (hash >> this.QuotientBits) & ((1UL << remaining) - 1);
            return FingerprintOf(field, age) == expected;
        }

        /// <summary>
        /// Inserts an entry with the given age and fingerprint at a quotient.
        /// </summary>
        internal void Insert(ulong quotient, int age, ulong fingerprint)
        {
            InsertField(quotient, EncodeField(age, fingerprint));
            this.Count++;
        }

        /// <summary>
        /// Places a field at a quotient without touching the count, so that a cluster
        /// can be taken apart and put back together without the count drifting.
        /// </summary>
        internal void InsertField(ulong quotient, ulong field)
        {
            if (IsSlotEmpty(quotient))
            {
                WriteSlot(quotient, OccupiedBit | (field << MetadataBits));
                return;
            }

            var hadRun = IsOccupied(quotient);
            SetOccupied(quotient);

            var runStart = FindRunStart(quotient);
            var at = runStart;

            if (hadRun)
            {
                // Runs are kept ordered by field so that a scan can stop early, and so
                // that a delete looking for the longest fingerprint has somewhere
                // predictable to look.
                while (true)
                {
                    if (ReadField(at) >= field)
                    {
                        break;
                    }

                    var next = Next(at);
                    if (!IsContinuation(next))
                    {
                        at = next;
                        break;
                    }

                    at = next;
                }
            }

            var entry = (field << MetadataBits)
                | (hadRun && at != runStart ? ContinuationBit : 0)
                | (at != quotient ? ShiftedBit : 0);

            var displacingRunStart = hadRun && at == runStart;
            var slot = at;
            var firstStep = true;
            var steps = 0UL;

            while (true)
            {
                // The shift walks forward until it finds somewhere empty. In a table
                // with no empty slot left there is nowhere, and the walk would circle
                // forever, so a full table is refused rather than spun on.
                if (steps++ > this.Slots)
                {
                    throw new InvalidOperationException(
                        $"A table of {this.Slots} slots is full and cannot take " +
                        "another entry. Its owner is meant to expand it before it " +
                        "reaches that state.");
                }

                var existing = ReadSlot(slot);
                var empty = (existing & (OccupiedBit | ContinuationBit | ShiftedBit)) == 0;

                WriteSlot(slot, (existing & OccupiedBit) | (entry & ~OccupiedBit));

                if (empty)
                {
                    return;
                }

                entry = (existing & ~OccupiedBit) | ShiftedBit;

                if (firstStep && displacingRunStart)
                {
                    entry |= ContinuationBit;
                }

                firstStep = false;
                slot = Next(slot);
            }
        }

        /// <summary>
        /// Removes one entry matching the hash, choosing the one with the longest
        /// fingerprint.
        /// </summary>
        /// <remarks>
        /// Which match is removed is not a detail. A short fingerprint stands for more
        /// keys than a long one, so removing a short match could take away the only
        /// record of a key that was never deleted -- a false negative, which this
        /// structure may not produce. Removing the longest match leaves the shorter
        /// ones behind, and they go on answering for whatever else they stood for.
        /// </remarks>
        internal bool Remove(ulong hash)
        {
            var (quotient, _) = Split(hash);
            if (!IsOccupied(quotient))
            {
                return false;
            }

            var runStart = FindRunStart(quotient);
            var slot = runStart;
            var found = false;
            var bestSlot = 0UL;
            var bestAge = int.MaxValue;

            do
            {
                var field = ReadField(slot);
                if (Matches(field, hash))
                {
                    var age = AgeOf(field);
                    if (age < bestAge)
                    {
                        bestAge = age;
                        bestSlot = slot;
                        found = true;
                    }
                }
                slot = Next(slot);
            }
            while (IsContinuation(slot));

            if (!found)
            {
                return false;
            }

            RebuildClusterWithout(quotient, ReadField(bestSlot));
            this.Count--;
            return true;
        }

        /// <summary>
        /// Takes a cluster apart, drops one entry, and puts the rest back.
        /// </summary>
        /// <remarks>
        /// Shifting entries back one at a time is faster and much easier to get
        /// subtly wrong: the shifted and continuation bits of every entry after the
        /// hole have to be recomputed against runs that may have moved or emptied.
        /// Reinserting the survivors lets the insert path work that out, which is code
        /// that is exercised on every addition rather than only on deletes.
        /// </remarks>
        private void RebuildClusterWithout(ulong quotient, ulong field)
        {
            var start = quotient;
            while (IsShifted(start))
            {
                start = Prev(start);
            }

            var length = 1UL;
            var slot = start;
            while (true)
            {
                var next = Next(slot);
                if (IsSlotEmpty(next) || !IsShifted(next))
                {
                    break;
                }
                slot = next;
                length++;
            }

            // Pair each entry in the cluster with the quotient that owns it, which is
            // possible only because runs appear in the order their quotients do. A list holds
            // no more than an array does, and a cluster of more is refused here by name rather
            // than wrapping the list's capacity negative; a table of 2^31 slots could not hold
            // one, at three quarters full, and only a hash that sends most keys to one place
            // gives a wider table one.
            var entries = new List<(ulong Quotient, ulong Field)>(checked((int)length));
            var owner = start;
            var cursor = start;

            for (var seen = 0UL; seen < length;)
            {
                while (!IsOccupied(owner))
                {
                    owner = Next(owner);
                }

                entries.Add((owner, ReadField(cursor)));
                cursor = Next(cursor);
                seen++;

                while (seen < length && IsContinuation(cursor))
                {
                    entries.Add((owner, ReadField(cursor)));
                    cursor = Next(cursor);
                    seen++;
                }

                owner = Next(owner);
            }

            var wipe = start;
            for (var i = 0UL; i < length; i++)
            {
                WriteSlot(wipe, 0);
                wipe = Next(wipe);
            }

            var dropped = false;
            foreach (var (owningQuotient, held) in entries)
            {
                if (!dropped && owningQuotient == quotient && held == field)
                {
                    dropped = true;
                    continue;
                }

                InsertField(owningQuotient, held);
            }
        }

        /// <summary>
        /// Refuses a table whose metadata is inconsistent, or whose entries are not its
        /// count, before any run in it is walked. See <see cref="QuotientTable.CheckMetadata"/>.
        /// </summary>
        internal void CheckMetadata(int table) =>
            QuotientTable.CheckMetadata(this.Data, this.bitsPerSlot, this.Slots, this.Count, table);

        /// <summary>
        /// Every entry the segment holds, paired with the quotient that owns it, read from
        /// the table as the walk reaches it.
        /// </summary>
        /// <remarks>
        /// Nothing may change the segment while the walk is under way, since it reads the
        /// slots in place. The filter's doubling is the walk that matters, and it reads
        /// the old table and writes only the new one. It used to list the entries first,
        /// 16 bytes each beside both tables: 12.9 GB at the doubling into 2^31 slots, and 103 GB
        /// at the doubling into 2^34, as wide as a table can now be (#231).
        /// </remarks>
        internal IEnumerable<(ulong Quotient, ulong Field)> Entries()
        {
            for (var quotient = 0UL; quotient < this.Slots; quotient++)
            {
                if (!IsOccupied(quotient))
                {
                    continue;
                }

                var at = FindRunStart(quotient);

                while (true)
                {
                    yield return (quotient, ReadField(at));

                    var next = Next(at);
                    if (!IsContinuation(next))
                    {
                        break;
                    }

                    at = next;
                }
            }
        }

        private ulong Next(ulong slot) => Next(slot, this.slotMaskIndex);

        private ulong Prev(ulong slot) => Prev(slot, this.slotMaskIndex);

        internal bool IsOccupied(ulong slot) => (ReadSlot(slot) & OccupiedBit) != 0;

        private bool IsContinuation(ulong slot) => (ReadSlot(slot) & ContinuationBit) != 0;

        private bool IsShifted(ulong slot) => (ReadSlot(slot) & ShiftedBit) != 0;

        internal ulong ReadField(ulong slot) => ReadSlot(slot) >> MetadataBits;

        private bool IsSlotEmpty(ulong slot) =>
            (ReadSlot(slot) & (OccupiedBit | ContinuationBit | ShiftedBit)) == 0;

        private void SetOccupied(ulong slot) =>
            WriteSlot(slot, ReadSlot(slot) | OccupiedBit);

        /// <summary>
        /// Where the run belonging to a quotient begins.
        /// </summary>
        /// <remarks>
        /// Each walk stops after one lap of the table, and throws, as the quotient filter's
        /// does. See <see cref="QuotientTable.Lapped"/>.
        /// <para>
        /// Internal so that the tests can run it on a table they have broken on purpose,
        /// and ask it for a quotient no run belongs to.
        /// </para>
        /// </remarks>
        internal ulong FindRunStart(ulong quotient)
        {
            var slot = quotient;
            while (IsShifted(slot))
            {
                slot = Prev(slot);
                if (slot == quotient)
                {
                    throw QuotientTable.Lapped(quotient, this.Slots);
                }
            }

            var start = slot;
            var runner = slot;
            while (runner != quotient)
            {
                do
                {
                    slot = Next(slot);
                    if (slot == start)
                    {
                        throw QuotientTable.Lapped(quotient, this.Slots);
                    }
                }
                while (IsContinuation(slot));

                do
                {
                    runner = Next(runner);
                    if (runner == start)
                    {
                        throw QuotientTable.Lapped(quotient, this.Slots);
                    }
                }
                while (!IsOccupied(runner));
            }

            return slot;
        }

        private ulong ReadSlot(ulong index)
        {
            this.SlotReads++;

            var (word, offset) = Locate(index, this.bitsPerSlot);

            var value = this.Data[word] >> offset;
            var taken = 64 - offset;

            if (taken < this.bitsPerSlot)
            {
                value |= this.Data[word + 1] << taken;
            }

            return value & this.slotMask;
        }

        /// <summary>
        /// Writes one slot, metadata in the low three bits. Internal so that a test can
        /// break a table on purpose, which nothing else does.
        /// </summary>
        internal void WriteSlot(ulong index, ulong value)
        {
            value &= this.slotMask;

            var (word, offset) = Locate(index, this.bitsPerSlot);

            this.Data[word] = (this.Data[word] & ~(this.slotMask << offset)) | (value << offset);

            var taken = 64 - offset;
            if (taken < this.bitsPerSlot)
            {
                var mask = (1UL << (this.bitsPerSlot - taken)) - 1;
                this.Data[word + 1] = (this.Data[word + 1] & ~mask) | (value >> taken);
            }
        }
    }
}
