using System;
using System.Collections.Generic;

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
                WriteCount(writer, (ulong)(mementos.Count - 2));
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
        internal List<KeepsakeBox> Decode(IReadOnlyList<ulong> fields)
        {
            var boxes = new List<KeepsakeBox>();
            var at = 0;

            while (at < fields.Count)
            {
                var fingerprint = fields[at] >> this.mementoBits;
                var box = new KeepsakeBox { Fingerprint = fingerprint };
                box.Mementos.Add(fields[at] & this.mementoMask);
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
                        var middle = (int)ReadCount(reader);
                        for (var i = 0; i < middle; i++)
                        {
                            box.Mementos.Add(reader.Read(this.mementoBits));
                        }
                        box.Mementos.Add(largest);
                        at += reader.FieldsConsumed();
                    }
                    else if (nextFingerprint == fingerprint)
                    {
                        box.Mementos.Add(fields[at] & this.mementoMask);
                        at++;
                    }
                }

                boxes.Add(box);
            }

            return boxes;
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
        private void WriteCount(BitRun writer, ulong count)
        {
            var escape = this.mementoMask;
            while (count >= escape)
            {
                writer.Write(escape, this.mementoBits);
                count -= escape;
            }
            writer.Write(count, this.mementoBits);
        }

        private ulong ReadCount(BitRun reader)
        {
            var escape = this.mementoMask;
            var total = 0UL;
            while (true)
            {
                var piece = reader.Read(this.mementoBits);
                total += piece;
                if (piece != escape)
                {
                    return total;
                }
            }
        }

        /// <summary>
        /// A cursor over a run of slots treated as one stream of bits, so that values
        /// can straddle a slot boundary rather than wasting the space at the end of
        /// each one.
        /// </summary>
        private sealed class BitRun
        {
            private readonly int width;
            private readonly List<ulong> words = new List<ulong>();
            private readonly IReadOnlyList<ulong>? source;
            private readonly int origin;
            private int position;

            internal BitRun(int width)
            {
                this.width = width;
            }

            internal BitRun(int width, IReadOnlyList<ulong> source, int origin)
            {
                this.width = width;
                this.source = source;
                this.origin = origin;
            }

            internal void Write(ulong value, int bits)
            {
                for (var i = 0; i < bits; i++)
                {
                    var bit = (value >> i) & 1;
                    var word = this.position / this.width;
                    var offset = this.position % this.width;

                    while (this.words.Count <= word)
                    {
                        this.words.Add(0);
                    }

                    this.words[word] |= bit << offset;
                    this.position++;
                }
            }

            internal ulong Read(int bits)
            {
                var value = 0UL;
                for (var i = 0; i < bits; i++)
                {
                    var word = this.origin + (this.position / this.width);
                    var offset = this.position % this.width;

                    var bit = word < this.source!.Count
                        ? (this.source[word] >> offset) & 1
                        : 0;

                    value |= bit << i;
                    this.position++;
                }
                return value;
            }

            internal List<ulong> Fields() => this.words;

            internal int FieldsConsumed() =>
                (this.position + this.width - 1) / this.width;
        }
    }
}
