using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
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
    public class QuotientFilter : IBinaryPersistable<QuotientFilter>, IPayloadBody
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

        /// <summary>What one entry costs listed: a quotient and a remainder, each 64 bits.</summary>
        private const ulong EntryBytes = 16;

        /// <summary>The bits of hash a fingerprint is cut from.</summary>
        internal const int HashBits = 64;

        /// <summary>
        /// The most bits a table can be indexed by. The table is one array of 64-bit words,
        /// and at 2^35 slots even the narrowest slot, a remainder of one bit and its three
        /// of metadata, comes to 2^31 words and two, more than an array holds. Below that
        /// the two widths still have to fit one array between them (<see cref="FitsOneArray"/>).
        /// </summary>
        /// <remarks>
        /// Up to 6.2.0 the slot count was a uint, which stopped the table at 2^31 slots: at
        /// 32 bits the shift wrapped to a shift by none, and the table came out one slot.
        /// The slot count and the entries are counted in 64 bits now (#231).
        /// </remarks>
        internal const int MaxQuotientBits = 34;

        /// <summary>
        /// The quotient bits a payload of version 1 says for a table of one slot: what the
        /// constructors of 6.2.0 and earlier wrapped a request past their largest table to.
        /// See <see cref="wrapped"/>.
        /// </summary>
        private const int WrappedQuotientBits = 32;

        /// <summary>
        /// The most words a table can have: it is one array.
        /// </summary>
        internal static long MaxTableWords => Array.MaxLength;

        /// <summary>
        /// The most bits a remainder can have. A slot is the remainder and its
        /// metadata, read and written through one 64-bit word, so it can be no wider
        /// than that. A wider one writes by shifting a ulong by 64, which wraps to no
        /// shift at all, and puts the whole value into the next word.
        /// </summary>
        internal const int MaxRemainderBits = 64 - MetadataBits;

        /// <summary>
        /// The most items a filter is sized for: the load factor of the largest table.
        /// </summary>
        internal const ulong MaxItems = (ulong)(LoadFactor * (1UL << MaxQuotientBits));

        private uint quotientBits;
        private uint remainderBits;
        private ulong slots;
        private ulong slotMaskIndex;
        private int bitsPerSlot;
        private ulong slotMask;
        private ulong remainderMask;
        private ulong[] data = null!;
        private ulong count;

        /// <summary>
        /// Whether the table is the one slot a constructor of 6.2.0 or earlier wrapped a
        /// request for 32 quotient bits to, read from a payload of version 1. Such a table
        /// keeps its 32 bits, so that its fingerprints are cut as they were, and is written
        /// back at version 1, as it was read: at version 12, 32 quotient bits are 2^32 slots.
        /// </summary>
        private bool wrapped;

        internal Func<ReadOnlySpan<byte>, ulong> Hash { get; set; } = null!;

        /// <summary>
        /// Every slot read the filter has ever performed. The metadata walks are
        /// while-true loops whose termination rests on the three-bit invariants, so
        /// a defect there does not fail -- it spins, or quietly walks the whole
        /// table per operation while still answering correctly. Wall clocks cannot
        /// adjudicate either (a timeout mostly measures the machine); this counter
        /// can, so the tests bound the work as well as the answers.
        /// </summary>
        internal long SlotReads { get; private set; }

        /// <summary>How many slots the table holds, for the work-bound tests.</summary>
        internal ulong SlotCount => this.slots;

        /// <summary>
        /// Creates a filter sized for the given number of items and false positive rate.
        /// </summary>
        /// <param name="n">The number of items the filter is sized for.</param>
        /// <param name="fpRate">The desired false positive rate.</param>
        /// <param name="hash">The hash function to use, or null for the default.</param>
        /// <remarks>
        /// The item count sets how many bits of the 64-bit hash pick a slot, and the rate
        /// sets how many are kept to tell items apart, so a filter for more items cannot
        /// reach as fine a rate. A fingerprint cannot be wider than the hash it is cut
        /// from, and a filter that was asked for one wider would answer yes for every
        /// key, so the constructor refuses what cannot work instead of building it.
        /// <para>
        /// With q the bits that pick a slot, which the item count gives as the fewest that
        /// keep the table under three quarters full, the finest rate is 2^-(64 - q), and
        /// never finer than 2^-61, which is all a slot can hold beside its three bits of
        /// bookkeeping. The table is one array of 64-bit words, so the most items is three
        /// quarters of 2^34 slots, and past 2^31 slots the array bounds the rate as well: a
        /// remainder of at most 28 bits at 2^32 slots, 12 at 2^33 and 4 at 2^34.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="n"/> is zero or more than the largest table is sized for, or
        /// <paramref name="fpRate"/> is not between zero and one or is finer than a
        /// filter for <paramref name="n"/> items can reach. The message names the most
        /// items, or the finest rate, that would have worked.
        /// </exception>
        public QuotientFilter(ulong n, double fpRate, Func<ReadOnlySpan<byte>, ulong>? hash = null)
        {
            var (quotientBits, remainderBits) = Split(n, fpRate);

            this.Shape(quotientBits, remainderBits);
            this.Hash = hash ?? Defaults.GetDefaultHashFunction();
        }

        /// <summary>
        /// How a fingerprint is split for a given item count and false positive rate:
        /// the quotient bits that pick a slot, and the remainder bits stored there.
        /// </summary>
        /// <remarks>
        /// The constructor's sizing, reachable on its own so that the largest sizes can
        /// be checked without allocating the table they describe. It refuses a size no
        /// filter could work at; every split it returns is one the reader accepts.
        /// </remarks>
        internal static (uint QuotientBits, uint RemainderBits) Split(ulong n, double fpRate)
        {
            Guard.ValidItemCount(n, nameof(n));
            Guard.ValidFalsePositiveRate(fpRate, nameof(fpRate));

            if (n > MaxItems)
            {
                throw new ArgumentOutOfRangeException(nameof(n), n,
                    $"A quotient filter can be sized for at most {MaxItems} items. Its " +
                    $"table has at most 2^{MaxQuotientBits} slots, because it is one array " +
                    "of 64-bit words and the next size up does not fit one even at the " +
                    "narrowest slot, and it is sized to stay three quarters full.");
            }

            // Enough slots that the table stays under its load factor.
            var quotientBits = (uint)Math.Max(1, Math.Ceiling(Math.Log2(n / LoadFactor)));

            // The false positive rate is the chance two fingerprints agree in the
            // remainder, so the remainder's width sets it. It stays a double until it
            // has been checked: a rate small enough that its reciprocal overflows wants
            // an infinite width, and what a cast to uint makes of infinity is the
            // runtime's to decide. This used to be that cast, and the number it gave
            // went on into the slot arithmetic as a two-bit slot.
            var wanted = Math.Ceiling(Math.Log2(1.0 / fpRate));

            // What the remainder can have: what the hash leaves after the quotient, and
            // no more than a slot can carry. Past either, the fingerprint is not cut
            // from the hash any more, and the filter cannot tell one key from another.
            var hashRoom = HashBits - (int)quotientBits;
            var slotRoom = Math.Min(MaxRemainderBits, hashRoom);

            // And no more than one array of words holds at this many slots, which past
            // 2^31 slots is less than either: the reader holds a table to the same rule.
            var room = slotRoom;
            while (room > 0 && !FitsOneArray(1UL << (int)quotientBits, (uint)room))
            {
                room--;
            }

            if (wanted > room)
            {
                var why = room < slotRoom
                    ? $"A table of 2^{quotientBits} slots is one array of 64-bit words, which " +
                      $"holds at most {MaxTableWords} of them, and that leaves room for slots " +
                      $"of {room + MetadataBits} bits: {room} for the remainder and " +
                      $"{MetadataBits} of bookkeeping."
                    : hashRoom <= MaxRemainderBits
                    ? $"The {quotientBits} bits that pick a slot leave {room} of the " +
                      $"hash's {HashBits} for the remainder."
                    : $"A slot holds the remainder and {MetadataBits} bits of bookkeeping " +
                      $"in one {HashBits}-bit word, which leaves {room} for the remainder.";
                var finest = Math.ScaleB(1.0, -room);
                var needs = double.IsFinite(wanted)
                    ? $"{(long)wanted} remainder bits"
                    : "more remainder bits than a hash has";

                throw new ArgumentOutOfRangeException(nameof(fpRate), fpRate,
                    "A false positive rate of " +
                    $"{fpRate.ToString("R", CultureInfo.InvariantCulture)} needs {needs}, " +
                    $"and a filter sized for {n} items can store no more than {room}. " +
                    $"{why} The finest rate such a filter can reach is 2^-{room}, which " +
                    $"is {finest.ToString("R", CultureInfo.InvariantCulture)}.");
            }

            return (quotientBits, (uint)Math.Max(1, wanted));
        }

        /// <summary>
        /// Lays out the table for a given split of the fingerprint.
        /// </summary>
        /// <remarks>
        /// Trusts the split it is given. The constructor refuses what cannot work before
        /// it gets here, and the reader refuses the payloads it will not read.
        /// </remarks>
        private void Shape(uint quotientBits, uint remainderBits, bool wrapped = false)
        {
            this.quotientBits = quotientBits;
            this.remainderBits = remainderBits;
            this.wrapped = wrapped;
            this.slots = SlotsOf(quotientBits, wrapped);
            this.slotMaskIndex = this.slots - 1;
            this.bitsPerSlot = (int)remainderBits + MetadataBits;
            this.slotMask = this.bitsPerSlot >= 64 ? ulong.MaxValue : (1UL << this.bitsPerSlot) - 1;
            this.remainderMask = remainderBits >= 64 ? ulong.MaxValue : (1UL << (int)remainderBits) - 1;

            // Spare words so a slot straddling the end has somewhere to reach.
            this.data = new ulong[TableWords(this.slots, remainderBits)];
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
            return this.slots * (ulong)this.bitsPerSlot / 8;
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

            // The same split names the same table but in one case: a one-slot table read
            // from version 1 at 32 quotient bits, against a table of 2^32 slots. Their
            // quotients mean different slots, and one has none of the other's.
            if (this.slots != other.slots)
            {
                throw new ArgumentException(
                    "Filters must have the same table to be merged: this one has " +
                    $"{this.slots} slots and the other {other.slots}, though both use " +
                    $"{this.quotientBits} quotient bits. One of them is a table of one slot, " +
                    "which a constructor of 6.2.0 or earlier wrapped a request for more items " +
                    "than its largest table into.",
                    nameof(other));
            }

            Guard.SameHashFunction(this.Hash, other.Hash, nameof(other));

            if (this.count + other.count > this.slots)
            {
                throw new InvalidOperationException(
                    $"The merged filter would hold {this.count + other.count} " +
                    $"entries and has {this.slots} slots.");
            }

            // Another filter's table is not written to by inserting into this one, so its
            // entries are read as they are walked and nothing is held. A filter merged
            // into itself is the one case that reads the table it is writing, and each
            // insert shifts the entries after it, so it would meet entries it had
            // already moved: what it reads is taken first, by the cheaper of two ways.
            // Listed, the entries are sixteen bytes each; a copy of the table is its
            // words, at most eight bytes a slot. Past 2^31 slots a filter can hold more
            // entries than one list does, and then the copy is the cheaper by far, so the
            // list is only ever taken for a count that fits one.
            var entries = other.Entries();
            if (ReferenceEquals(this, other))
            {
                if (this.count * EntryBytes < (ulong)this.data.LongLength * sizeof(ulong))
                {
                    var held = new List<(ulong Quotient, ulong Remainder)>((int)this.count);
                    held.AddRange(entries);
                    entries = held;
                }
                else
                {
                    var copy = (QuotientFilter)this.MemberwiseClone();
                    copy.data = (ulong[])this.data.Clone();
                    entries = copy.Entries();
                }
            }

            // At most as many as the other filter held when the merge began, whatever the
            // walk yields. The check above left room for exactly that many, and Insert
            // searches forward for an empty slot with nothing to stop it if there is
            // none, so this is what keeps a walk that goes wrong -- one that meets the
            // entries it has just written, say -- from filling the table and hanging in
            // the insert instead of ending with a wrong answer a test can see.
            using var walk = entries.GetEnumerator();
            for (var left = other.count; left > 0 && walk.MoveNext(); left--)
            {
                var (quotient, remainder) = walk.Current;
                this.Insert(quotient, remainder);
                this.count++;
            }

            return this;
        }

        /// <summary>
        /// Every entry in the table, as the quotient that owns it and the remainder
        /// stored for it, read one at a time as the caller asks for them.
        /// </summary>
        /// <remarks>
        /// The table must not be written to while this is being read.
        /// </remarks>
        private IEnumerable<(ulong Quotient, ulong Remainder)> Entries()
        {
            for (var quotient = 0UL; quotient < this.slots; quotient++)
            {
                if (!this.IsOccupied(quotient))
                {
                    continue;
                }

                var at = this.FindRunStart(quotient);

                while (true)
                {
                    yield return (quotient, this.RemainderAt(at));

                    var next = Next(at, this.slotMaskIndex);
                    if (!this.IsContinuation(next))
                    {
                        break;
                    }

                    at = next;
                }
            }
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
        /// <para>
        /// The cluster's entries are listed, so a cluster of more entries than one list
        /// holds cannot be rebuilt this way. Only a table past 2^31 slots and nearly full
        /// has one, where every operation walks billions of slots; the list is made, and
        /// fails, before anything in the table is changed.
        /// </para>
        /// </remarks>
        private bool Remove(ulong quotient, ulong remainder)
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

            var length = 1UL;
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

            var entries = new List<(ulong Quotient, ulong Remainder)>((int)length);
            var owner = start;
            var cursor = start;

            for (var seen = 0UL; seen < length;)
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
            for (var i = 0UL; i < length; i++)
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
        private bool Holds(ulong quotient, ulong remainder)
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
        private void Insert(ulong quotient, ulong remainder)
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
        /// <para>
        /// Each walk stops after one lap of the table, and throws: on consistent metadata,
        /// asked for an occupied quotient, none of them comes back to where it began, so
        /// one that does would otherwise circle for ever. See
        /// <see cref="QuotientTable.Lapped"/>.
        /// </para>
        /// <para>
        /// Internal so that the tests can run it on a table they have broken on purpose,
        /// and ask it for a quotient no run belongs to.
        /// </para>
        /// </remarks>
        internal ulong FindRunStart(ulong quotient)
        {
            var bucket = quotient;
            while (this.IsShifted(bucket))
            {
                bucket = Prev(bucket, this.slotMaskIndex);
                if (bucket == quotient)
                {
                    throw QuotientTable.Lapped(quotient, this.slots);
                }
            }

            var start = bucket;
            var run = bucket;

            while (bucket != quotient)
            {
                do
                {
                    run = Next(run, this.slotMaskIndex);
                    if (run == start)
                    {
                        throw QuotientTable.Lapped(quotient, this.slots);
                    }
                }
                while (this.IsContinuation(run));

                do
                {
                    bucket = Next(bucket, this.slotMaskIndex);
                    if (bucket == start)
                    {
                        throw QuotientTable.Lapped(quotient, this.slots);
                    }
                }
                while (!this.IsOccupied(bucket));
            }

            return run;
        }

        private static ulong Next(ulong slot, ulong mask) => (slot + 1) & mask;

        private static ulong Prev(ulong slot, ulong mask) => (slot - 1) & mask;

        private bool IsOccupied(ulong slot) => (this.ReadSlot(slot) & OccupiedBit) != 0;

        private bool IsContinuation(ulong slot) => (this.ReadSlot(slot) & ContinuationBit) != 0;

        private bool IsShifted(ulong slot) => (this.ReadSlot(slot) & ShiftedBit) != 0;

        private ulong RemainderAt(ulong slot) => this.ReadSlot(slot) >> MetadataBits;

        /// <summary>
        /// Whether a slot holds no entry, which is all three metadata bits clear.
        /// </summary>
        private bool IsSlotEmpty(ulong slot) =>
            (this.ReadSlot(slot) & (OccupiedBit | ContinuationBit | ShiftedBit)) == 0;

        private void SetOccupied(ulong slot) =>
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
        public ulong Count()
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
        public ulong Capacity()
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
        /// <para>
        /// Every save is format version 12, which counts the entries in 64 bits and which
        /// releases up to 6.2.0 do not read, but one: a table of one slot read from version
        /// 1 at 32 quotient bits is written back at version 1, as it was read, since at
        /// version 12 its 32 quotient bits would say 2^32 slots. A table past one array's
        /// worth of bytes is written in the large form of the envelope.
        /// </para>
        /// </remarks>
        public void WriteTo(Stream stream) => PersistenceFormat.Write(stream, this);

        EnvelopeHeader IPayloadBody.WritePayload(PayloadWriter payload)
        {
            payload.WriteUInt32(this.quotientBits);
            payload.WriteUInt32(this.remainderBits);

            // The wrapped table holds one slot, so its count fits the u32 it was read from.
            if (this.wrapped)
            {
                payload.WriteUInt32((uint)this.count);
            }
            else
            {
                payload.WriteUInt64(this.count);
            }

            // One run of the table's words, sized by the two widths.
            var slots = SlotsOf(this.quotientBits, this.wrapped);
            if (payload.BeginRun(TableWords(slots, this.remainderBits) * sizeof(ulong)))
            {
                foreach (var word in this.data)
                {
                    payload.WriteUInt64(word);
                }
            }

            return new(StructureId.QuotientFilter, PersistenceFormat.Identify(this.Hash),
                this.wrapped ? PersistenceFormat.DefaultVersion : PersistenceFormat.QuotientWideVersion);
        }

        /// <summary>
        /// Reads a filter written by <see cref="WriteTo"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The filter that was written.</returns>
        public static QuotientFilter ReadFrom(Stream stream)
        {
            return Read(stream, null, ReadOptions.Default);
        }

        /// <summary>
        /// Reads a structure written by <see cref="WriteTo"/>, allocating no more than the
        /// options' memory budget allows. <see cref="ReadFrom(Stream)"/> reads under
        /// <see cref="ReadOptions.Default"/>.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="options">The most memory the read may allocate.</param>
        /// <returns>The structure that was written.</returns>
        /// <exception cref="InvalidDataException">
        /// Reading the payload would allocate more than the budget allows, among the other
        /// reasons a payload is refused. Its inner exception is then a
        /// <see cref="ReadBudgetExceededException"/>.
        /// </exception>
        public static QuotientFilter ReadFrom(Stream stream, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, null, options);
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
            return Read(stream, hash, ReadOptions.Default);
        }

        /// <summary>
        /// Reads a structure as
        /// <see cref="ReadFrom(Stream, Func{ReadOnlySpan{byte}, ulong})"/> does, allocating
        /// no more than the options' memory budget allows.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="hash">The hash function, as for the overload without options.</param>
        /// <param name="options">The most memory the read may allocate.</param>
        /// <returns>The structure that was written.</returns>
        /// <exception cref="InvalidDataException">
        /// Reading the payload would allocate more than the budget allows, among the other
        /// reasons a payload is refused. Its inner exception is then a
        /// <see cref="ReadBudgetExceededException"/>.
        /// </exception>
        public static QuotientFilter ReadFrom(
            Stream stream, Func<ReadOnlySpan<byte>, ulong> hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(options);
            return Read(stream, hash, options);
        }

        private static QuotientFilter Read(
            Stream stream, Func<ReadOnlySpan<byte>, ulong>? hash, ReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var budget = ReadBudget.For(options, StructureId.QuotientFilter);
            return PersistenceFormat.Read(
                stream, StructureId.QuotientFilter, budget, Price, hash, PersistenceFormat.CheckResolvable,
                ReadPayload);
        }

        /// <summary>
        /// Reads the payload of the envelope <c>Read</c> opened, once the envelope has
        /// been checked. See <see cref="PayloadParse{T}"/>.
        /// </summary>
        private static QuotientFilter ReadPayload(
            ref PayloadReader reader, ushort version, HashId hashId,
            Func<ReadOnlySpan<byte>, ulong>? hash)
        {
            var budget = reader.Budget;

            var quotientBits = reader.ReadUInt32();
            var remainderBits = reader.ReadUInt32();
            var count = ReadCount(ref reader, version);

            // The table's words are one run, which in the large form can be longer than one
            // array (#228): read in pieces, straight into the table, when the payload is not
            // held, and copied out whole when it is.
            var stored = reader.BeginWords();
            var wrapped = CheckWidths(version, quotientBits, remainderBits);
            var slots = SlotsOf(quotientBits, wrapped);
            var words = TableWords(slots, remainderBits);

            // The table is allocated whole, from the two widths alone, and charged for that
            // first; it is allocated once the count and the bytes carried agree with it, so
            // that a payload claiming a table it does not hold costs nothing to refuse.
            budget.Charge(TableCost(slots, remainderBits));

            if (count > slots)
            {
                throw new InvalidDataException(
                    $"Filter claims {count} entries and has {slots} slots.");
            }

            if (stored != BytesOf(words))
            {
                throw new InvalidDataException(
                    $"Filter has {slots} slots of {remainderBits + MetadataBits} bits, " +
                    $"needing {BytesOf(words)} bytes, and carries {stored}.");
            }

            var filter = Build(quotientBits, remainderBits, wrapped, PersistenceFormat.ResolveOrThrow(hashId, hash));

            for (var word = 0; word < filter.data.Length;)
            {
                var piece = reader.ReadWords();
                for (var at = 0; at < piece.Length; at += sizeof(ulong))
                {
                    filter.data[word++] = BinaryPrimitives.ReadUInt64LittleEndian(piece[at..]);
                }
            }

            reader.ExpectEnd();

            // Every walk of the table rests on its metadata being consistent, and on the count
            // being its entries, which the full check in Add relies on.
            QuotientTable.CheckMetadata(filter.data, filter.bitsPerSlot, filter.slots, count, -1);
            filter.count = count;
            return filter;
        }

        /// <summary>
        /// Prices what <see cref="Read"/> allocates beyond the envelope: the copy of the
        /// stored table, or the piece it is read in from a payload that is not held, and the
        /// table it is unpacked into, sized by the two widths alone. See
        /// <see cref="PayloadPrice"/>.
        /// </summary>
        internal static void Price(ref PayloadReader reader, ushort version)
        {
            var quotientBits = reader.ReadUInt32();
            var remainderBits = reader.ReadUInt32();
            ReadCount(ref reader, version);
            reader.SkipWords();
            reader.ExpectEnd();
            var wrapped = CheckWidths(version, quotientBits, remainderBits);
            reader.Budget.Price(TableCost(SlotsOf(quotientBits, wrapped), remainderBits));
        }

        /// <summary>The count of entries: a u64 from version 12, a u32 before it.</summary>
        private static ulong ReadCount(ref PayloadReader reader, ushort version) =>
            version == PersistenceFormat.DefaultVersion ? reader.ReadUInt32() : reader.ReadUInt64();

        /// <summary>The table a filter of these widths is built with, and the filter.</summary>
        private static long TableCost(ulong slots, uint remainderBits) => ReadBudget.Sum(
            ReadBudget.ArrayOf((ulong)TableWords(slots, remainderBits), sizeof(ulong)),
            ReadBudget.SmallObject);

        /// <summary>
        /// Refuses widths that are not a table this library builds, or once built, and says
        /// whether they are the wrapped one-slot table of version 1.
        /// </summary>
        private static bool CheckWidths(ushort version, uint quotientBits, uint remainderBits)
        {
            // At version 1, 32 as well, which the constructors of 6.2.0 and earlier never
            // meant but wrote: a request for more items than their largest table came out
            // as a quotient of 32 bits, whose 1u << 32 wrapped to one slot, and the filter
            // was written that way, 32 here and the bytes of a one-slot table. It is read
            // as that one slot, the length check holding the payload to that size, so it
            // reads back as the filter it was and nothing is guessed. Wider quotients,
            // which a still larger request produced, were refused when they were written
            // and are refused now. At version 12 every width means its 2^q slots.
            var widest = version == PersistenceFormat.DefaultVersion ? WrappedQuotientBits : MaxQuotientBits;
            if (quotientBits == 0 || quotientBits > widest)
            {
                throw new InvalidDataException(
                    $"Filter has {quotientBits} quotient bits, and a table of version {version} " +
                    $"is indexed by between 1 and {widest} of them.");
            }

            // The constructor stops at the widest remainder a slot can hold, and a payload
            // may still say a little more: the old constructor wrote remainders that wide
            // for the finest rates at the smallest sizes, and what it wrote is read as
            // written rather than refused. A fingerprint wider than the hash is refused,
            // as it always was. The widths are added in 64 bits: in 32, a remainder within the
            // quotient's width of 2^32 wrapped the sum to a few bits and passed, and the
            // slot's width wrapped with it, so a table of eight slots or fewer, two words at
            // its own width and at the wrapped one, read as slots narrower than their
            // metadata, which then lost a key added to them.
            if (remainderBits == 0 || (ulong)quotientBits + remainderBits > HashBits)
            {
                throw new InvalidDataException(
                    $"Filter splits a fingerprint into {quotientBits} quotient and " +
                    $"{remainderBits} remainder bits, which is not a split of a {HashBits}-bit " +
                    "hash.");
            }

            // The constructor's own rule: the table is one array. No table read at version
            // 1 is past it, and one of version 12 past it could not be built to read into.
            var wrapped = version == PersistenceFormat.DefaultVersion && quotientBits == WrappedQuotientBits;
            var slots = SlotsOf(quotientBits, wrapped);
            if (!FitsOneArray(slots, remainderBits))
            {
                throw new InvalidDataException(
                    $"Filter has {slots} slots of {remainderBits + MetadataBits} bits, " +
                    $"{TableWords(slots, remainderBits)} words, and its table is one array, " +
                    $"which holds at most {MaxTableWords} of them.");
            }

            return wrapped;
        }

        /// <summary>
        /// The bytes a table of this many words is written as. In 64 bits: in an int it
        /// wrapped at 2^28 words, two gibibytes, and the filter then failed to write with
        /// an OverflowException from the allocation, or compared a payload's bytes with a
        /// wrapped number.
        /// </summary>
        internal static long BytesOf(long words) => words * sizeof(ulong);

        /// <summary>
        /// The slots a table of these quotient bits has: 2^q, or one for the table a
        /// constructor of 6.2.0 or earlier wrapped to one slot at 32 bits.
        /// </summary>
        private static ulong SlotsOf(uint quotientBits, bool wrapped) =>
            wrapped ? 1 : 1UL << (int)quotientBits;

        /// <summary>
        /// The 64-bit words a table of this many slots occupies: one slot of remainder and
        /// metadata for each, and two spare words for reads that straddle the end. In 64
        /// bits, since 2^34 slots of up to 67 bits do not fit 32.
        /// </summary>
        internal static long TableWords(ulong slots, uint remainderBits) =>
            (long)((slots * (remainderBits + (uint)MetadataBits)) >> 6) + 2;

        /// <summary>
        /// Whether a table of this many slots and this remainder is one array of words,
        /// which every table is. The constructor sizes no remainder past it, and the
        /// reader refuses a payload past it.
        /// </summary>
        internal static bool FitsOneArray(ulong slots, uint remainderBits) =>
            TableWords(slots, remainderBits) <= MaxTableWords;

        /// <summary>
        /// Builds a filter from the two bit widths directly, which is what a payload
        /// carries -- the constructor's item count and rate are only ever a way of
        /// arriving at them.
        /// </summary>
        private static QuotientFilter Build(
            uint quotientBits, uint remainderBits, bool wrapped, Func<ReadOnlySpan<byte>, ulong> hash)
        {
            var filter = new QuotientFilter(1, 0.5, hash);
            filter.Shape(quotientBits, remainderBits, wrapped);
            return filter;
        }

        /// <summary>
        /// The quotient that picks a slot and the remainder stored in it.
        /// </summary>
        private (ulong Quotient, ulong Remainder) Fingerprint(ReadOnlySpan<byte> data) =>
            Cut(this.Hash(data), this.quotientBits, this.remainderBits, this.slotMaskIndex, this.remainderMask);

        /// <summary>
        /// The quotient and the remainder cut from a hash: its top q + r bits, the quotient
        /// above the remainder, each held to the table's mask. The one-slot table of
        /// version 1 has a mask of no bits, so its quotient is always 0.
        /// </summary>
        /// <remarks>
        /// Static and internal so that the cut can be checked at the widths past 2^32 slots,
        /// where a quotient has more bits than a uint holds, which no table in the suite
        /// reaches. Every add, lookup and removal goes through it.
        /// </remarks>
        internal static (ulong Quotient, ulong Remainder) Cut(
            ulong hash, uint quotientBits, uint remainderBits, ulong slotMask, ulong remainderMask)
        {
            var fingerprint = hash >> (64 - (int)(quotientBits + remainderBits));
            return ((fingerprint >> (int)remainderBits) & slotMask, fingerprint & remainderMask);
        }

        private ulong ReadSlot(ulong index)
        {
            this.SlotReads++;

            var bit = index * (ulong)this.bitsPerSlot;
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

        /// <summary>
        /// Writes one slot, metadata in the low three bits. Internal so that a test can
        /// break a table on purpose, which nothing else does.
        /// </summary>
        internal void WriteSlot(ulong index, ulong value)
        {
            value &= this.slotMask;

            var bit = index * (ulong)this.bitsPerSlot;
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
