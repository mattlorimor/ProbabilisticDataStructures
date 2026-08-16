using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// A compact membership filter that supports deletion and merging.
    /// </summary>
    /// <remarks>
    /// Bender et al., "Don't Thrash: How to Cache Your Hash on Flash" (2012).
    /// <para>
    /// Each item contributes a fingerprint, split into a quotient that picks a slot and
    /// a remainder that is stored there. Three metadata bits per slot record enough to
    /// reconstruct which slot a remainder belongs to once collisions have pushed it
    /// along, so the whole fingerprint is recoverable from the table.
    /// </para>
    /// </remarks>
    public class QuotientFilter : IBinaryPersistable<QuotientFilter>
    {
        /// <summary>The load factor the table is sized for.</summary>
        /// <remarks>
        /// A quotient filter's runs lengthen sharply as it fills, so it is sized to stay
        /// below the point where that begins to cost.
        /// </remarks>
        private const double LoadFactor = 0.75;

        private const ulong OccupiedBit = 1;
        private const ulong ContinuationBit = 2;
        private const ulong ShiftedBit = 4;
        private const int MetadataBits = 3;

        private uint quotientBits;
        private uint remainderBits;
        private uint slots;
        private uint slotMaskIndex;
        private int bitsPerSlot;
        private ulong slotMask;
        private ulong remainderMask;
        private ulong[] data = null!;
        private uint count;

        internal Func<ReadOnlySpan<byte>, ulong> Hash { get; set; } = null!;

        /// <summary>
        /// Creates a filter sized for the given number of items and false positive rate.
        /// </summary>
        /// <param name="n">The number of items the filter is sized for.</param>
        /// <param name="fpRate">The desired false positive rate.</param>
        /// <param name="hash">The hash function to use, or null for the default.</param>
        public QuotientFilter(uint n, double fpRate, Func<ReadOnlySpan<byte>, ulong>? hash = null)
        {
            Guard.ValidItemCount(n, nameof(n));
            Guard.ValidFalsePositiveRate(fpRate, nameof(fpRate));

            // The false positive rate is the chance two fingerprints agree in the
            // remainder, so the remainder's width sets it.
            this.remainderBits = (uint)Math.Max(1, Math.Ceiling(Math.Log2(1.0 / fpRate)));

            // Enough slots that the table stays under its load factor.
            this.quotientBits = (uint)Math.Max(1, Math.Ceiling(Math.Log2(n / LoadFactor)));

            this.Shape(this.quotientBits, this.remainderBits);
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();
        }

        /// <summary>
        /// Lays out the table for a given split of the fingerprint.
        /// </summary>
        private void Shape(uint quotientBits, uint remainderBits)
        {
            this.quotientBits = quotientBits;
            this.remainderBits = remainderBits;
            this.slots = 1u << (int)quotientBits;
            this.slotMaskIndex = this.slots - 1;
            this.bitsPerSlot = (int)remainderBits + MetadataBits;
            this.slotMask = this.bitsPerSlot >= 64 ? ulong.MaxValue : (1UL << this.bitsPerSlot) - 1;
            this.remainderMask = remainderBits >= 64 ? ulong.MaxValue : (1UL << (int)remainderBits) - 1;

            // One spare word so a slot straddling the end has somewhere to reach.
            this.data = new ulong[(((long)this.slots * this.bitsPerSlot) >> 6) + 2];
        }

        /// <summary>
        /// Adds data to the filter. Returns the filter to allow for chaining.
        /// </summary>
        /// <param name="data">The data to add.</param>
        /// <returns>The filter.</returns>
        public QuotientFilter Add(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            return this.Add(data.AsSpan());
        }

        /// <inheritdoc cref="Add(byte[])"/>
        public QuotientFilter Add(ReadOnlySpan<byte> data)
        {
            if (this.count == this.slots)
            {
                throw new InvalidOperationException(
                    $"The filter is full: all {this.slots} slots hold an entry. A " +
                    "quotient filter stores one entry per item, so there is nowhere to " +
                    "put another. It was sized for the item count given to the " +
                    "constructor, and has been given more than that.");
            }

            var (quotient, remainder) = this.Fingerprint(data);

            // Repeats are stored rather than collapsed, and the reason is the whole
            // hazard of deleting from a fingerprint filter. Nothing here can tell the
            // same item added twice from two items whose fingerprints agree, so
            // collapsing the first collapses the second -- and then removing one of
            // those two items makes the filter answer no for the other. Storing every
            // addition costs a slot per repeat and keeps removal sound, which is the
            // side to err on.
            this.Insert(quotient, remainder);
            this.count++;

            return this;
        }

        /// <summary>
        /// The table's size in bytes, which is all the filter occupies beyond a handful
        /// of fields.
        /// </summary>
        public ulong SizeInBytes()
        {
            return (ulong)this.slots * (ulong)this.bitsPerSlot / 8;
        }

        /// <summary>
        /// The bits each slot occupies: the remainder, plus the three metadata bits that
        /// record how far the entry has been pushed from the slot it belongs to.
        /// </summary>
        public uint BitsPerSlot()
        {
            return (uint)this.bitsPerSlot;
        }

        /// <summary>
        /// Empties the filter.
        /// </summary>
        /// <returns>The filter.</returns>
        public QuotientFilter Reset()
        {
            Array.Clear(this.data);
            this.count = 0;
            return this;
        }

        /// <summary>
        /// Sets the hashing function used by the filter.
        /// </summary>
        /// <param name="h">The hash function to use.</param>
        /// <exception cref="InvalidOperationException">
        /// Anything has been added. The hash cannot be replaced then, because every
        /// entry already in the table was placed by the old one.
        /// </exception>
        public void SetHash(Func<ReadOnlySpan<byte>, ulong> h)
        {
            ArgumentNullException.ThrowIfNull(h);
            Guard.HashMayBeReplaced(this.count == 0, nameof(QuotientFilter));
            this.Hash = h;
        }

        /// <summary>
        /// Combines another filter into this one, so that it holds both sets.
        /// </summary>
        /// <param name="other">The filter to merge in, which is left unchanged.</param>
        /// <returns>This filter, to allow chaining.</returns>
        /// <remarks>
        /// This is what a quotient filter has that a cuckoo filter does not. A cuckoo
        /// filter's fingerprint only means anything relative to the bucket it landed in,
        /// so there is no way to lift one out and put it somewhere else; a quotient
        /// filter keeps each entry's quotient in its position, so every fingerprint can
        /// be recovered whole and re-placed in another table.
        /// </remarks>
        /// <exception cref="ArgumentNullException">The other filter is null.</exception>
        /// <exception cref="ArgumentException">
        /// The filters split their fingerprints differently, or were built with
        /// different hash functions, so their entries do not mean the same thing.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The combined entries would not fit.
        /// </exception>
        public QuotientFilter Merge(QuotientFilter other)
        {
            ArgumentNullException.ThrowIfNull(other);

            if (this.quotientBits != other.quotientBits
                || this.remainderBits != other.remainderBits)
            {
                throw new ArgumentException(
                    "Filters must split their fingerprints the same way to be merged: " +
                    $"this one uses {this.quotientBits} quotient and " +
                    $"{this.remainderBits} remainder bits, and the other " +
                    $"{other.quotientBits} and {other.remainderBits}. An entry means a " +
                    "different fingerprint under each, so the split must match.",
                    nameof(other));
            }

            Guard.SameHashFunction(this.Hash, other.Hash, nameof(other));

            if ((ulong)this.count + other.count > this.slots)
            {
                throw new InvalidOperationException(
                    $"The merged filter would hold {(ulong)this.count + other.count} " +
                    $"entries and has {this.slots} slots.");
            }

            // Read out in full before writing anything, so that merging a filter into
            // itself doubles what it holds rather than reading entries it is in the
            // middle of moving.
            foreach (var (quotient, remainder) in other.Entries())
            {
                this.Insert(quotient, remainder);
                this.count++;
            }

            return this;
        }

        /// <summary>
        /// Every entry in the table, as the quotient that owns it and the remainder
        /// stored for it.
        /// </summary>
        private List<(uint Quotient, ulong Remainder)> Entries()
        {
            var entries = new List<(uint, ulong)>((int)this.count);

            for (var quotient = 0u; quotient < this.slots; quotient++)
            {
                if (!this.IsOccupied(quotient))
                {
                    continue;
                }

                var at = this.FindRunStart(quotient);

                while (true)
                {
                    entries.Add((quotient, this.RemainderAt(at)));

                    var next = Next(at, this.slotMaskIndex);
                    if (!this.IsContinuation(next))
                    {
                        break;
                    }

                    at = next;
                }
            }

            return entries;
        }

        /// <summary>
        /// Removes data from the filter if it is present.
        /// </summary>
        /// <param name="data">The data to remove.</param>
        /// <returns>Whether the data was present, and so was removed.</returns>
        /// <remarks>
        /// Removing something the filter never held is refused rather than performed.
        /// A quotient filter stores one entry per insertion, so removing a fingerprint
        /// that is only present as a false positive would delete the entry belonging to
        /// whatever real item shares it, and that item would then answer no.
        /// </remarks>
        public bool TestAndRemove(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            return this.TestAndRemove(data.AsSpan());
        }

        /// <inheritdoc cref="TestAndRemove(byte[])"/>
        public bool TestAndRemove(ReadOnlySpan<byte> data)
        {
            var (quotient, remainder) = this.Fingerprint(data);

            if (!this.Remove(quotient, remainder))
            {
                return false;
            }

            this.count--;
            return true;
        }

        /// <summary>
        /// Takes one entry out of the table by rebuilding the cluster it sits in.
        /// </summary>
        /// <remarks>
        /// The alternative is shifting every following entry back one slot and repairing
        /// its metadata as it moves, which is where quotient filter implementations
        /// characteristically go wrong: an entry that lands on its own canonical slot
        /// stops being shifted, and one whose predecessor leaves stops being a
        /// continuation, and neither is visible from the entry alone.
        /// <para>
        /// Emptying the cluster and putting back what remains costs the same walk and
        /// reaches the same layout, because a cluster is exactly the set of entries
        /// whose positions depend on each other. It is the insert path that then has to
        /// be right, and the insert path is exercised by everything else here.
        /// </para>
        /// </remarks>
        private bool Remove(uint quotient, ulong remainder)
        {
            if (!this.IsOccupied(quotient) || !this.Holds(quotient, remainder))
            {
                return false;
            }

            var start = quotient;
            while (this.IsShifted(start))
            {
                start = Prev(start, this.slotMaskIndex);
            }

            var length = 1u;
            var slot = start;
            while (true)
            {
                var next = Next(slot, this.slotMaskIndex);
                if (this.IsSlotEmpty(next) || !this.IsShifted(next))
                {
                    break;
                }

                slot = next;
                length++;
            }

            var entries = new List<(uint Quotient, ulong Remainder)>((int)length);
            var owner = start;
            var cursor = start;

            for (var seen = 0u; seen < length;)
            {
                while (!this.IsOccupied(owner))
                {
                    owner = Next(owner, this.slotMaskIndex);
                }

                entries.Add((owner, this.RemainderAt(cursor)));
                cursor = Next(cursor, this.slotMaskIndex);
                seen++;

                while (seen < length && this.IsContinuation(cursor))
                {
                    entries.Add((owner, this.RemainderAt(cursor)));
                    cursor = Next(cursor, this.slotMaskIndex);
                    seen++;
                }

                owner = Next(owner, this.slotMaskIndex);
            }

            var wipe = start;
            for (var i = 0u; i < length; i++)
            {
                this.WriteSlot(wipe, 0);
                wipe = Next(wipe, this.slotMaskIndex);
            }

            var dropped = false;
            foreach (var (owningQuotient, held) in entries)
            {
                if (!dropped && owningQuotient == quotient && held == remainder)
                {
                    dropped = true;
                    continue;
                }

                this.Insert(owningQuotient, held);
            }

            return true;
        }

        /// <summary>
        /// Whether a quotient's run holds a remainder.
        /// </summary>
        private bool Holds(uint quotient, ulong remainder)
        {
            var at = this.FindRunStart(quotient);

            while (true)
            {
                var held = this.RemainderAt(at);

                if (held == remainder)
                {
                    return true;
                }

                if (held > remainder)
                {
                    return false;
                }

                var next = Next(at, this.slotMaskIndex);
                if (!this.IsContinuation(next))
                {
                    return false;
                }

                at = next;
            }
        }

        /// <summary>
        /// Places a remainder in its quotient's run, shifting whatever is in the way.
        /// </summary>
        /// <remarks>
        /// The occupied bit belongs to the slot and the other two belong to the entry
        /// stored there, so shifting moves the remainder, the continuation bit and the
        /// shifted bit, and leaves every occupied bit where it is.
        /// </remarks>
        private void Insert(uint quotient, ulong remainder)
        {
            if (this.IsSlotEmpty(quotient))
            {
                this.WriteSlot(quotient, OccupiedBit | (remainder << MetadataBits));
                return;
            }

            var hadRun = this.IsOccupied(quotient);
            this.SetOccupied(quotient);

            var runStart = this.FindRunStart(quotient);
            var at = runStart;

            if (hadRun)
            {
                // Runs are kept sorted, so a lookup can stop at the first remainder
                // larger than the one it wants.
                while (true)
                {
                    if (this.RemainderAt(at) >= remainder)
                    {
                        break;
                    }

                    var next = Next(at, this.slotMaskIndex);
                    if (!this.IsContinuation(next))
                    {
                        at = next;
                        break;
                    }

                    at = next;
                }
            }

            var entry = (remainder << MetadataBits)
                | (hadRun && at != runStart ? ContinuationBit : 0)
                | (at != quotient ? ShiftedBit : 0);

            // Displacing the first entry of an existing run makes that entry a
            // continuation of the run the new one now starts.
            var displacingRunStart = hadRun && at == runStart;
            var slot = at;
            var firstStep = true;

            while (true)
            {
                var existing = this.ReadSlot(slot);
                var empty = (existing & (OccupiedBit | ContinuationBit | ShiftedBit)) == 0;

                this.WriteSlot(slot, (existing & OccupiedBit) | (entry & ~OccupiedBit));

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
                slot = Next(slot, this.slotMaskIndex);
            }
        }

        /// <summary>
        /// Where the run for a quotient begins.
        /// </summary>
        /// <remarks>
        /// Walks back to the start of the cluster -- the nearest entry that is not
        /// shifted -- then forward, pairing each occupied quotient with the run that
        /// belongs to it. Runs appear in the same order as the quotients that own them,
        /// which is what makes the pairing possible at all.
        /// </remarks>
        private uint FindRunStart(uint quotient)
        {
            var bucket = quotient;
            while (this.IsShifted(bucket))
            {
                bucket = Prev(bucket, this.slotMaskIndex);
            }

            var run = bucket;

            while (bucket != quotient)
            {
                do
                {
                    run = Next(run, this.slotMaskIndex);
                }
                while (this.IsContinuation(run));

                do
                {
                    bucket = Next(bucket, this.slotMaskIndex);
                }
                while (!this.IsOccupied(bucket));
            }

            return run;
        }

        private static uint Next(uint slot, uint mask) => (slot + 1) & mask;

        private static uint Prev(uint slot, uint mask) => (slot - 1) & mask;

        private bool IsOccupied(uint slot) => (this.ReadSlot(slot) & OccupiedBit) != 0;

        private bool IsContinuation(uint slot) => (this.ReadSlot(slot) & ContinuationBit) != 0;

        private bool IsShifted(uint slot) => (this.ReadSlot(slot) & ShiftedBit) != 0;

        private ulong RemainderAt(uint slot) => this.ReadSlot(slot) >> MetadataBits;

        /// <summary>
        /// Whether a slot holds no entry, which is all three metadata bits clear.
        /// </summary>
        private bool IsSlotEmpty(uint slot) =>
            (this.ReadSlot(slot) & (OccupiedBit | ContinuationBit | ShiftedBit)) == 0;

        private void SetOccupied(uint slot) =>
            this.WriteSlot(slot, this.ReadSlot(slot) | OccupiedBit);

        /// <summary>
        /// Whether the data is probably in the filter.
        /// </summary>
        /// <param name="data">The data to test for.</param>
        /// <returns>Whether the data is probably present.</returns>
        public bool Test(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            return this.Test(data.AsSpan());
        }

        /// <inheritdoc cref="Test(byte[])"/>
        public bool Test(ReadOnlySpan<byte> data)
        {
            var (quotient, remainder) = this.Fingerprint(data);

            if (!this.IsOccupied(quotient))
            {
                return false;
            }

            var at = this.FindRunStart(quotient);

            while (true)
            {
                var held = this.RemainderAt(at);

                if (held == remainder)
                {
                    return true;
                }

                if (held > remainder)
                {
                    return false;
                }

                var next = Next(at, this.slotMaskIndex);
                if (!this.IsContinuation(next))
                {
                    return false;
                }

                at = next;
            }
        }

        /// <summary>
        /// The number of items in the filter.
        /// </summary>
        public uint Count()
        {
            return this.count;
        }

        /// <summary>
        /// The number of slots in the table, which is the most items it can hold.
        /// </summary>
        /// <remarks>
        /// Larger than the item count the filter was sized for: the table is built with
        /// room to spare because a quotient filter's runs lengthen as it fills, and
        /// lookups walk those runs. Filling it to capacity works but is slow, which is
        /// the trade a Bloom filter does not make.
        /// </remarks>
        public uint Capacity()
        {
            return this.slots;
        }

        /// <summary>
        /// Writes the filter to a stream.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        /// <remarks>
        /// The slot count, the bits per slot and the masks all follow from the two bit
        /// widths, so only those are written. A payload cannot then disagree with itself
        /// about the shape of its own table.
        /// </remarks>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = new PayloadWriter();
            payload.WriteUInt32(this.quotientBits);
            payload.WriteUInt32(this.remainderBits);
            payload.WriteUInt32(this.count);

            var bytes = new byte[this.data.Length * sizeof(ulong)];
            for (var i = 0; i < this.data.Length; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(
                    bytes.AsSpan(i * sizeof(ulong)), this.data[i]);
            }

            payload.WriteBytes(bytes);

            PersistenceFormat.Write(
                stream,
                StructureId.QuotientFilter,
                PersistenceFormat.Identify(this.Hash),
                payload.WrittenSpan);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The filter that was written.</returns>
        public static QuotientFilter ReadFrom(Stream stream)
        {
            return Read(stream, null);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>, using the supplied hash
        /// function rather than the one named in the payload.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function the filter was built with.</param>
        /// <returns>The filter that was written.</returns>
        public static QuotientFilter ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            return Read(stream, hash);
        }

        private static QuotientFilter Read(Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var payload = PersistenceFormat.Read(
                stream, StructureId.QuotientFilter, out var hashId);
            var reader = new PayloadReader(payload);

            var quotientBits = reader.ReadUInt32();
            var remainderBits = reader.ReadUInt32();
            var count = reader.ReadUInt32();
            var bytes = reader.ReadBytes();
            reader.ExpectEnd();

            if (quotientBits == 0 || quotientBits > 32)
            {
                throw new InvalidDataException(
                    $"Filter has {quotientBits} quotient bits, and a table is indexed by " +
                    "between 1 and 32 of them.");
            }

            if (remainderBits == 0 || quotientBits + remainderBits > 64)
            {
                throw new InvalidDataException(
                    $"Filter splits a fingerprint into {quotientBits} quotient and " +
                    $"{remainderBits} remainder bits, which is not a split of a 64-bit " +
                    "hash.");
            }

            var filter = Build(quotientBits, remainderBits, PersistenceFormat.ResolveOrThrow(hashId, hash));

            if (count > filter.slots)
            {
                throw new InvalidDataException(
                    $"Filter claims {count} entries and has {filter.slots} slots.");
            }

            if (bytes.Length != filter.data.Length * sizeof(ulong))
            {
                throw new InvalidDataException(
                    $"Filter has {filter.slots} slots of {filter.bitsPerSlot} bits, " +
                    $"needing {filter.data.Length * sizeof(ulong)} bytes, and carries " +
                    $"{bytes.Length}.");
            }

            for (var i = 0; i < filter.data.Length; i++)
            {
                filter.data[i] = BinaryPrimitives.ReadUInt64LittleEndian(
                    bytes.AsSpan(i * sizeof(ulong)));
            }

            filter.count = count;
            return filter;
        }

        /// <summary>
        /// Builds a filter from the two bit widths directly, which is what a payload
        /// carries -- the constructor's item count and rate are only ever a way of
        /// arriving at them.
        /// </summary>
        private static QuotientFilter Build(
            uint quotientBits, uint remainderBits, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            var filter = new QuotientFilter(1, 0.5, hash);
            filter.Shape(quotientBits, remainderBits);
            return filter;
        }

        /// <summary>
        /// The quotient that picks a slot and the remainder stored in it.
        /// </summary>
        private (uint Quotient, ulong Remainder) Fingerprint(ReadOnlySpan<byte> data)
        {
            var hash = this.Hash(data);
            var fingerprint = hash >> (64 - (int)(this.quotientBits + this.remainderBits));

            return ((uint)(fingerprint >> (int)this.remainderBits) & this.slotMaskIndex,
                    fingerprint & this.remainderMask);
        }

        private ulong ReadSlot(uint index)
        {
            var bit = (ulong)index * (ulong)this.bitsPerSlot;
            var word = (int)(bit >> 6);
            var offset = (int)(bit & 63);

            var value = this.data[word] >> offset;
            var taken = 64 - offset;

            if (taken < this.bitsPerSlot)
            {
                value |= this.data[word + 1] << taken;
            }

            return value & this.slotMask;
        }

        private void WriteSlot(uint index, ulong value)
        {
            value &= this.slotMask;

            var bit = (ulong)index * (ulong)this.bitsPerSlot;
            var word = (int)(bit >> 6);
            var offset = (int)(bit & 63);

            this.data[word] = (this.data[word] & ~(this.slotMask << offset)) | (value << offset);

            var taken = 64 - offset;
            if (taken < this.bitsPerSlot)
            {
                var mask = (1UL << (this.bitsPerSlot - taken)) - 1;
                this.data[word + 1] = (this.data[word + 1] & ~mask) | (value >> taken);
            }
        }
    }
}
