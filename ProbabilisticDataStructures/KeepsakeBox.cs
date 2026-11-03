using System;
using System.Collections.Generic;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// The keys of one block that share a fingerprint, as
    /// <see cref="MementoFilter"/> stores them: a fingerprint and the sorted positions
    /// of those keys within their block.
    /// </summary>
    internal sealed class KeepsakeBox
    {
        /// <summary>
        /// The fluid fingerprint shared by every key in the box. Never zero -- that
        /// value is reserved to mark where a box's contents overflow into following
        /// slots.
        /// </summary>
        internal ulong Fingerprint { get; set; }

        /// <summary>
        /// The mementos, ascending. One per key, so a box holding a memento twice
        /// stands for the same key added twice.
        /// </summary>
        internal List<ulong> Mementos { get; } = new List<ulong>();
    }

    /// <summary>
    /// Packs keepsake boxes into slots and reads them back.
    /// </summary>
    /// <remarks>
    /// A run holds several boxes of different lengths, and their boundaries are not
    /// written down anywhere. They are recovered instead from a value that cannot
    /// occur naturally: a fingerprint of zero, which no real box can carry because
    /// every age counter ends in a set bit. A zero in the slot after a box's first
    /// marks that the box is too long for the simple layouts and continues into the
    /// slots that follow.
    /// <para>
    /// The paper delimits boxes by any *decrease* in fingerprint, which makes storing
    /// them in increasing order an invariant the encoding depends on. Keying on zero
    /// alone costs nothing and removes that dependency, so a run whose boxes are out
    /// of order still reads back correctly. The filter keeps them ordered regardless,
    /// so that a lookup can stop early.
    /// </para>
    /// <para>
    /// The layouts, for a box holding l keys:
    /// </para>
    /// <para>
    /// l = 1: one slot, the fingerprint beside the single memento.
    /// </para>
    /// <para>
    /// l = 2: two slots, the fingerprint repeated beside each memento.
    /// </para>
    /// <para>
    /// l &gt; 2: the fingerprint beside the smallest memento, then a slot whose zero
    /// fingerprint marks the overflow and whose memento field carries the largest. The
    /// keys between them follow as a bit-packed run: a count, then that many mementos,
    /// laid end to end across the remaining slots without regard for slot boundaries.
    /// Holding the smallest and largest at the front means most ranges can be excluded
    /// without reading the packed part at all.
    /// </para>
    /// </remarks>
    internal sealed class KeepsakeCodec
    {
        private readonly int mementoBits;
        private readonly int fieldBits;
        private readonly ulong mementoMask;

        internal KeepsakeCodec(int fingerprintBits, int mementoBits)
        {
            this.mementoBits = mementoBits;
            this.fieldBits = fingerprintBits + 1 + mementoBits;
            this.mementoMask = (1UL << mementoBits) - 1;
        }

        /// <summary>
        /// Packs a run's boxes into slot values.
        /// </summary>
        internal List<ulong> Encode(IReadOnlyList<KeepsakeBox> boxes)
        {
            var fields = new List<ulong>();

            foreach (var box in boxes)
            {
                var mementos = box.Mementos;
                if (mementos.Count == 0)
                {
                    continue;
                }

                if (mementos.Count == 1)
                {
                    fields.Add(Pack(box.Fingerprint, mementos[0]));
                    continue;
                }

                if (mementos.Count == 2)
                {
                    fields.Add(Pack(box.Fingerprint, mementos[0]));
                    fields.Add(Pack(box.Fingerprint, mementos[1]));
                    continue;
                }

                // The smallest and the largest go up front, the second of them behind
                // a zero fingerprint so that the reader knows more is coming.
                fields.Add(Pack(box.Fingerprint, mementos[0]));
                fields.Add(Pack(0, mementos[^1]));

                var writer = new BitRun(this.fieldBits);
                WriteCount(ref writer, (ulong)(mementos.Count - 2));
                for (var i = 1; i < mementos.Count - 1; i++)
                {
                    writer.Write(mementos[i], this.mementoBits);
                }
                fields.AddRange(writer.Fields());
            }

            return fields;
        }

        /// <summary>
        /// Reads back the boxes a run holds.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// A box's count claims more keys than the rest of the run has bits for, or the run
        /// ends inside it. No run this codec writes does, and the reader refuses a payload
        /// holding one (<see cref="Check"/>), so this is a backstop that a filter the reader
        /// admitted cannot reach.
        /// </exception>
        internal List<KeepsakeBox> Decode(IReadOnlyList<ulong> fields)
        {
            var boxes = new List<KeepsakeBox>();
            Walk(fields, boxes);
            return boxes;
        }

        /// <summary>
        /// Refuses a run that <see cref="Decode"/> would refuse, for the same reason, without
        /// building its boxes. The reader runs it on every run of a table it reads, and the
        /// keys of a box cost far more held one to a list entry than packed in the run, so
        /// this walks the boxes' boundaries and skips the keys between.
        /// </summary>
        internal void Check(IReadOnlyList<ulong> fields) => Walk(fields, null);

        /// <summary>
        /// The one walk over a run's boxes, which <see cref="Decode"/> collects into
        /// <paramref name="boxes"/> and <see cref="Check"/> does not.
        /// </summary>
        private void Walk(IReadOnlyList<ulong> fields, List<KeepsakeBox>? boxes)
        {
            var at = 0;

            while (at < fields.Count)
            {
                var fingerprint = fields[at] >> this.mementoBits;
                var box = boxes is null ? null : new KeepsakeBox { Fingerprint = fingerprint };
                box?.Mementos.Add(fields[at] & this.mementoMask);
                at++;

                if (at < fields.Count)
                {
                    var nextFingerprint = fields[at] >> this.mementoBits;

                    if (nextFingerprint == 0)
                    {
                        // The overflow marker: this box holds more than two keys.
                        var largest = fields[at] & this.mementoMask;
                        at++;

                        var reader = new BitRun(this.fieldBits, fields, at);
                        var middle = ReadCount(ref reader);
                        if (box is null)
                        {
                            reader.Skip(middle * this.mementoBits);
                        }
                        else
                        {
                            for (var i = 0L; i < middle; i++)
                            {
                                box.Mementos.Add(reader.Read(this.mementoBits));
                            }
                            box.Mementos.Add(largest);
                        }

                        // No more than the run's remaining slots: the count was held to them.
                        at += (int)reader.FieldsConsumed();
                    }
                    else if (nextFingerprint == fingerprint)
                    {
                        box?.Mementos.Add(fields[at] & this.mementoMask);
                        at++;
                    }
                }

                if (box is not null)
                {
                    boxes!.Add(box);
                }
            }
        }

        /// <summary>
        /// How many slots a set of boxes will occupy once packed.
        /// </summary>
        internal int SlotsNeeded(IReadOnlyList<KeepsakeBox> boxes) => Encode(boxes).Count;

        private ulong Pack(ulong fingerprint, ulong memento) =>
            (fingerprint << this.mementoBits) | (memento & this.mementoMask);

        /// <summary>
        /// Writes a count in memento-sized pieces, so that the usual small count costs
        /// as much as one memento and a rare large one still fits.
        /// </summary>
        private void WriteCount(ref BitRun writer, ulong count)
        {
            var escape = this.mementoMask;
            while (count >= escape)
            {
                writer.Write(escape, this.mementoBits);
                count -= escape;
            }
            writer.Write(count, this.mementoBits);
        }

        /// <summary>
        /// Reads a count written by <see cref="WriteCount"/>, refusing one that claims more
        /// keys than the rest of the run has bits for.
        /// </summary>
        /// <remarks>
        /// Every key the count claims is a memento still to be read, so a count is at most
        /// the bits left over the memento width. It is held to that after every piece: the
        /// total only grows and the bits left only shrink, so a count over the bound part
        /// way through is over it at the end, and stopping there keeps a run of escape pieces
        /// from adding up past what a ulong holds. A run that ends inside the count is
        /// refused by the read. A count that passes is no more than the run's bits, so it is
        /// a long, where it was cast to an int that a count of 2^31 or more made negative.
        /// </remarks>
        private long ReadCount(ref BitRun reader)
        {
            var escape = this.mementoMask;
            var total = 0UL;
            while (true)
            {
                var piece = reader.Read(this.mementoBits);
                total += piece;

                var room = reader.BitsLeft / this.mementoBits;
                if (total > (ulong)room)
                {
                    throw new InvalidDataException(
                        $"A box claims {(piece == escape ? "at least " : "")}{total:N0} keys between " +
                        $"its smallest and its largest, more keys than its run holds: the " +
                        $"{reader.BitsLeft:N0} bits left in the run have room for {room:N0} of " +
                        $"{this.mementoBits} bits. This library writes no such box.");
                }

                if (piece != escape)
                {
                    return (long)total;
                }
            }
        }

        /// <summary>
        /// A cursor over a run of slots treated as one stream of bits, so that values
        /// can straddle a slot boundary rather than wasting the space at the end of
        /// each one. A struct, so that a read of a run allocates nothing for it.
        /// </summary>
        private struct BitRun
        {
            private readonly int width;
            private readonly List<ulong>? words;
            private readonly IReadOnlyList<ulong>? source;
            private readonly int origin;

            // A long: a run's bits are its slots times the field width, and a run as long
            // as a table of 2^31 slots passes an int's worth.
            private long position;

            internal BitRun(int width)
            {
                this.width = width;
                this.words = new List<ulong>();
                this.source = null;
                this.origin = 0;
                this.position = 0;
            }

            internal BitRun(int width, IReadOnlyList<ulong> source, int origin)
            {
                this.width = width;
                this.words = null;
                this.source = source;
                this.origin = origin;
                this.position = 0;
            }

            /// <summary>The bits of the run after the cursor.</summary>
            internal readonly long BitsLeft =>
                ((long)(this.source!.Count - this.origin) * this.width) - this.position;

            internal void Write(ulong value, int bits)
            {
                for (var i = 0; i < bits; i++)
                {
                    var bit = (value >> i) & 1;
                    var word = (int)(this.position / this.width);
                    var offset = (int)(this.position % this.width);

                    while (this.words!.Count <= word)
                    {
                        this.words.Add(0);
                    }

                    this.words[word] |= bit << offset;
                    this.position++;
                }
            }

            /// <summary>
            /// Reads the next value, refusing one that runs past the end of the run rather
            /// than reading zeros there.
            /// </summary>
            internal ulong Read(int bits)
            {
                if (bits > this.BitsLeft)
                {
                    throw new InvalidDataException(
                        "A box holding more than two keys has a count that runs past the end of " +
                        "its run, so where its keys end cannot be known. This library writes no " +
                        "such box.");
                }

                var value = 0UL;
                for (var i = 0; i < bits; i++)
                {
                    var word = this.origin + (int)(this.position / this.width);
                    var offset = (int)(this.position % this.width);

                    value |= ((this.source![word] >> offset) & 1) << i;
                    this.position++;
                }
                return value;
            }

            /// <summary>Moves past bits the caller has held to <see cref="BitsLeft"/>.</summary>
            internal void Skip(long bits) => this.position += bits;

            internal readonly List<ulong> Fields() => this.words!;

            internal readonly long FieldsConsumed() =>
                (this.position + this.width - 1) / this.width;
        }
    }
}
