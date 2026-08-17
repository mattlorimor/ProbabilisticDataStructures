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
        /// The fingerprint length a freshly inserted entry gets. The age counter and
        /// fingerprint together always occupy this many bits plus one.
        /// </summary>
        internal int FingerprintBits { get; private set; }

        internal uint Slots { get; private set; }
        internal uint Count { get; set; }
        internal ulong[] Data { get; set; } = null!;

        private uint slotMaskIndex;
        private int bitsPerSlot;
        private ulong slotMask;
        private int fieldBits;

        internal InfiniSegment(int quotientBits, int fingerprintBits)
        {
            Shape(quotientBits, fingerprintBits);
        }

        private void Shape(int quotientBits, int fingerprintBits)
        {
            this.QuotientBits = quotientBits;
            this.FingerprintBits = fingerprintBits;
            this.Slots = 1u << quotientBits;
            this.slotMaskIndex = this.Slots - 1;

            // The age counter and the fingerprint share one field of F + 1 bits: a
            // counter of a ones and a terminating zero leaves F - a bits of
            // fingerprint, so the field width never changes as entries age.
            this.fieldBits = fingerprintBits + 1;
            this.bitsPerSlot = this.fieldBits + MetadataBits;
            this.slotMask = (1UL << this.bitsPerSlot) - 1;
            this.Data = new ulong[(((long)this.Slots * this.bitsPerSlot) >> 6) + 2];
        }

        /// <summary>
        /// Restores a segment from stored fields without rebuilding its table.
        /// </summary>
        internal static InfiniSegment Restore(
            int quotientBits, int fingerprintBits, uint count, ulong[] data)
        {
            var segment = new InfiniSegment(quotientBits, fingerprintBits)
            {
                Count = count,
            };
            data.CopyTo(segment.Data, 0);
            return segment;
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
        internal (uint Quotient, ulong Fingerprint) Split(ulong hash)
        {
            var quotient = (uint)(hash & this.slotMaskIndex);
            var fingerprint = (hash >> this.QuotientBits) &
                ((1UL << this.FingerprintBits) - 1);
            return (quotient, fingerprint);
        }

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
        internal void Insert(uint quotient, int age, ulong fingerprint)
        {
            InsertField(quotient, EncodeField(age, fingerprint));
            this.Count++;
        }

        /// <summary>
        /// Places a field at a quotient without touching the count, so that a cluster
        /// can be taken apart and put back together without the count drifting.
        /// </summary>
        internal void InsertField(uint quotient, ulong field)
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
            var steps = 0u;

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
            var bestSlot = 0u;
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
        private void RebuildClusterWithout(uint quotient, ulong field)
        {
            var start = quotient;
            while (IsShifted(start))
            {
                start = Prev(start);
            }

            var length = 1u;
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
            // possible only because runs appear in the order their quotients do.
            var entries = new List<(uint Quotient, ulong Field)>((int)length);
            var owner = start;
            var cursor = start;

            for (var seen = 0u; seen < length;)
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
            for (var i = 0u; i < length; i++)
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
        /// Every entry the segment holds, paired with the quotient that owns it.
        /// </summary>
        internal List<(uint Quotient, ulong Field)> Entries()
        {
            var entries = new List<(uint Quotient, ulong Field)>((int)this.Count);

            for (var quotient = 0u; quotient < this.Slots; quotient++)
            {
                if (!IsOccupied(quotient))
                {
                    continue;
                }

                var at = FindRunStart(quotient);

                while (true)
                {
                    entries.Add((quotient, ReadField(at)));

                    var next = Next(at);
                    if (!IsContinuation(next))
                    {
                        break;
                    }

                    at = next;
                }
            }

            return entries;
        }

        private uint Next(uint slot) => (slot + 1) & this.slotMaskIndex;

        private uint Prev(uint slot) => (slot - 1) & this.slotMaskIndex;

        internal bool IsOccupied(uint slot) => (ReadSlot(slot) & OccupiedBit) != 0;

        private bool IsContinuation(uint slot) => (ReadSlot(slot) & ContinuationBit) != 0;

        private bool IsShifted(uint slot) => (ReadSlot(slot) & ShiftedBit) != 0;

        internal ulong ReadField(uint slot) => ReadSlot(slot) >> MetadataBits;

        private bool IsSlotEmpty(uint slot) =>
            (ReadSlot(slot) & (OccupiedBit | ContinuationBit | ShiftedBit)) == 0;

        private void SetOccupied(uint slot) =>
            WriteSlot(slot, ReadSlot(slot) | OccupiedBit);

        /// <summary>
        /// Where the run belonging to a quotient begins.
        /// </summary>
        private uint FindRunStart(uint quotient)
        {
            var slot = quotient;
            while (IsShifted(slot))
            {
                slot = Prev(slot);
            }

            var runner = slot;
            while (runner != quotient)
            {
                do
                {
                    slot = Next(slot);
                }
                while (IsContinuation(slot));

                do
                {
                    runner = Next(runner);
                }
                while (!IsOccupied(runner));
            }

            return slot;
        }

        private ulong ReadSlot(uint index)
        {
            var bit = (ulong)index * (ulong)this.bitsPerSlot;
            var word = (int)(bit >> 6);
            var offset = (int)(bit & 63);

            var value = this.Data[word] >> offset;
            var taken = 64 - offset;

            if (taken < this.bitsPerSlot)
            {
                value |= this.Data[word + 1] << taken;
            }

            return value & this.slotMask;
        }

        private void WriteSlot(uint index, ulong value)
        {
            value &= this.slotMask;

            var bit = (ulong)index * (ulong)this.bitsPerSlot;
            var word = (int)(bit >> 6);
            var offset = (int)(bit & 63);

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
