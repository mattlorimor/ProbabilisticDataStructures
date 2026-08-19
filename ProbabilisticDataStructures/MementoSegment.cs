using System;
using System.Collections.Generic;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// One table in a <see cref="MementoFilter"/>'s chain.
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
    internal sealed class MementoSegment
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

        internal int MementoBits { get; private set; }

        internal uint Slots { get; private set; }

        /// <summary>
        /// Every slot read this segment has ever performed, so the work-bound
        /// tests can hold the metadata walks to the work the paper's invariants
        /// imply, rather than to a wall clock.
        /// </summary>
        internal long SlotReads { get; private set; }
        internal uint Count { get; set; }
        internal ulong[] Data { get; set; } = null!;

        private uint slotMaskIndex;
        private int bitsPerSlot;
        private ulong slotMask;
        private int fieldBits;
        private ulong mementoMask;

        internal MementoSegment(int quotientBits, int fingerprintBits, int mementoBits)
        {
            Shape(quotientBits, fingerprintBits, mementoBits);
        }

        private void Shape(int quotientBits, int fingerprintBits, int mementoBits)
        {
            this.QuotientBits = quotientBits;
            this.FingerprintBits = fingerprintBits;
            this.MementoBits = mementoBits;
            this.Slots = 1u << quotientBits;
            this.slotMaskIndex = this.Slots - 1;

            // The age counter and the fingerprint share one field of F + 1 bits: a
            // counter of a ones and a terminating zero leaves F - a bits of
            // fingerprint, so the field width never changes as entries age.
            // The fluid fingerprint sits above the memento, so an expansion can take
            // the fingerprint's low bit without ever touching the key's own bits.
            this.fieldBits = fingerprintBits + 1 + mementoBits;
            this.mementoMask = (1UL << mementoBits) - 1;
            this.bitsPerSlot = this.fieldBits + MetadataBits;
            this.slotMask = (1UL << this.bitsPerSlot) - 1;
            this.Data = new ulong[(((long)this.Slots * this.bitsPerSlot) >> 6) + 2];
            this.Codec = new KeepsakeCodec(fingerprintBits, mementoBits);
        }

        /// <summary>
        /// Restores a segment from stored fields without rebuilding its table.
        /// </summary>
        internal static MementoSegment Restore(
            int quotientBits, int fingerprintBits, int mementoBits, uint count,
            ulong[] data)
        {
            var segment = new MementoSegment(quotientBits, fingerprintBits, mementoBits)
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
        internal ulong EncodeField(int age, ulong fingerprint, ulong memento)
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
            var fluid = (1UL << remaining) | (fingerprint & ((1UL << remaining) - 1));
            return (fluid << this.MementoBits) | (memento & this.mementoMask);
        }

        /// <summary>
        /// How many expansions ago an entry was inserted, read from the leading ones
        /// of its field.
        /// </summary>
        internal int AgeOf(ulong field)
        {
            var age = 0;
            for (var bit = this.fieldBits - 1; bit >= this.MementoBits; bit--)
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
            var fluid = field >> this.MementoBits;
            return remaining <= 0 ? 0 : fluid & ((1UL << remaining) - 1);
        }

        /// <summary>
        /// The key's own low bits: where it sits inside its block.
        /// </summary>
        internal ulong MementoOf(ulong field) => field & this.mementoMask;

        /// <summary>
        /// An entry that has spent every fingerprint bit it had. It cannot be given to
        /// an expanded filter, because there is nothing left to sacrifice for the
        /// address bit an expansion needs.
        /// </summary>
        internal bool IsVoid(ulong field) => AgeOf(field) >= this.FingerprintBits;

        /// <summary>
        /// The codec that packs this table's runs.
        /// </summary>
        internal KeepsakeCodec Codec { get; private set; } = null!;

        /// <summary>
        /// The fluid fingerprint an entry of this age and fingerprint carries: the
        /// age counter and the fingerprint together, without a memento beneath them.
        /// </summary>
        internal ulong FluidFingerprint(int age, ulong fingerprint)
        {
            var remaining = this.FingerprintBits - age;
            return (1UL << remaining) | (fingerprint & ((1UL << remaining) - 1));
        }

        /// <summary>
        /// How many expansions ago an entry with this fluid fingerprint was inserted.
        /// </summary>
        internal int AgeOfFluid(ulong fluid)
        {
            var age = 0;
            for (var bit = this.FingerprintBits; bit >= 0; bit--)
            {
                if ((fluid & (1UL << bit)) != 0)
                {
                    break;
                }
                age++;
            }
            return age;
        }

        /// <summary>
        /// The fingerprint left inside a fluid fingerprint of this age.
        /// </summary>
        internal ulong FingerprintOfFluid(ulong fluid, int age)
        {
            var remaining = this.FingerprintBits - age;
            return remaining <= 0 ? 0 : fluid & ((1UL << remaining) - 1);
        }

        /// <summary>
        /// Whether a box's fluid fingerprint could stand for this hash. An older box
        /// matches on fewer bits, which is the price of having expanded.
        /// </summary>
        internal bool FingerprintMatches(ulong fluid, ulong hash)
        {
            var age = AgeOfFluid(fluid);
            var remaining = this.FingerprintBits - age;
            if (remaining <= 0)
            {
                return true;
            }

            var expected = (hash >> this.QuotientBits) & ((1UL << remaining) - 1);
            return FingerprintOfFluid(fluid, age) == expected;
        }

        /// <summary>
        /// Places one memento into the block a quotient names, joining the box already
        /// there when the fluid fingerprint matches.
        /// </summary>
        internal void InsertMemento(
            uint quotient, int age, ulong fingerprint, ulong memento)
        {
            var fluid = FluidFingerprint(age, fingerprint);
            var boxes = this.Codec.Decode(ReadRun(quotient));

            var index = boxes.FindIndex(b => b.Fingerprint == fluid);
            if (index < 0)
            {
                var box = new KeepsakeBox { Fingerprint = fluid };
                box.Mementos.Add(memento);
                var at = boxes.FindIndex(b => b.Fingerprint > fluid);
                boxes.Insert(at < 0 ? boxes.Count : at, box);
            }
            else
            {
                var mementos = boxes[index].Mementos;
                var position = mementos.FindIndex(m => m > memento);
                mementos.Insert(position < 0 ? mementos.Count : position, memento);
            }

            RewriteRun(quotient, this.Codec.Encode(boxes));
        }

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
        /// Whether the segment holds a key in this block whose position within it
        /// falls between low and high.
        /// </summary>
        /// <remarks>
        /// The fingerprint decides whether an entry belongs to the block being asked
        /// about; the memento then decides whether that key is inside the range. A
        /// filter that stopped at the fingerprint would answer "possibly" for every
        /// range touching an occupied block, however far its keys are from the range.
        /// </remarks>
        internal bool HasMementoBetween(ulong hash, ulong low, ulong high)
        {
            var (quotient, _) = Split(hash);
            if (!IsOccupied(quotient))
            {
                return false;
            }

            foreach (var box in this.Codec.Decode(ReadRun(quotient)))
            {
                if (!FingerprintMatches(box.Fingerprint, hash))
                {
                    continue;
                }

                // The box holds its keys in order, so the ends rule most ranges out
                // before any of the middle is looked at.
                var mementos = box.Mementos;
                if (mementos[^1] < low || mementos[0] > high)
                {
                    continue;
                }

                foreach (var memento in mementos)
                {
                    if (memento >= low && memento <= high)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Inserts an entry with the given age and fingerprint at a quotient.
        /// </summary>
        internal void Insert(uint quotient, int age, ulong fingerprint, ulong memento)
        {
            InsertField(quotient, EncodeField(age, fingerprint, memento));
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
                // The run's order is the encoding's order, not a sort. A keepsake box
                // spills across several slots whose values fall as well as rise -- the
                // zero marker is a fall by design -- so anything that reordered them
                // would take a run apart. New slots go on the end.
                while (true)
                {
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

        /// <summary>
        /// The slot values of the run belonging to a quotient, in order.
        /// </summary>
        internal List<ulong> ReadRun(uint quotient)
        {
            var fields = new List<ulong>();
            if (!IsOccupied(quotient))
            {
                return fields;
            }

            var slot = FindRunStart(quotient);
            do
            {
                fields.Add(ReadField(slot));
                slot = Next(slot);
            }
            while (IsContinuation(slot));

            return fields;
        }

        /// <summary>
        /// Replaces a quotient's run with the given slot values, which may be more or
        /// fewer than it held before.
        /// </summary>
        /// <remarks>
        /// Editing a keepsake box changes how many slots it needs, so a run has to be
        /// able to grow and shrink. Rather than shifting neighbours around a hole of
        /// changing size -- where every shifted and continuation bit after it has to be
        /// recomputed -- the whole cluster is taken apart and put back through the
        /// ordinary insert path, which is exercised on every addition rather than only
        /// here.
        /// </remarks>
        internal void RewriteRun(uint quotient, IReadOnlyList<ulong> fields)
        {
            var start = quotient;
            while (IsShifted(start))
            {
                start = Prev(start);
            }

            var length = IsSlotEmpty(start) && !IsOccupied(start) ? 0u : 1u;
            if (length > 0)
            {
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
            }

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
            this.Count -= length;

            // The replaced run's slots go back in the position its quotient dictates,
            // and every other run returns unchanged.
            var placed = false;
            foreach (var (owningQuotient, held) in entries)
            {
                if (owningQuotient == quotient)
                {
                    if (!placed)
                    {
                        placed = true;
                        foreach (var replacement in fields)
                        {
                            InsertField(quotient, replacement);
                            this.Count++;
                        }
                    }
                    continue;
                }

                InsertField(owningQuotient, held);
                this.Count++;
            }

            if (!placed)
            {
                foreach (var replacement in fields)
                {
                    InsertField(quotient, replacement);
                    this.Count++;
                }
            }
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
            this.SlotReads++;

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
