using System;
using System.Collections.Generic;
using System.Numerics;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// An array of variable-length counters, laid out as the VALE encoding of Eslami,
    /// Bercea, Pagh and Dayan, Sublime: Sublinear Error and Space for Unbounded Skewed
    /// Streams (SIGMOD 2026), section 4.1.
    /// </summary>
    /// <remarks>
    /// The counters are cut into <em>chunks</em> of one cache line each. A chunk holds,
    /// in order: an overflows bitmap with one bit per counter, that counter's fixed-width
    /// stubs, and an extension pool. A counter small enough to fit its stub costs nothing
    /// beyond it. A counter too large sets its overflow bit and puts its high bits into
    /// the pool as an extension, encoded by <see cref="ValeCounter"/>.
    /// <para>
    /// Extensions are stored in counter order and carry no lengths, so finding the
    /// i-th counter's extension means skipping as many extensions as there are
    /// overflowing counters before it. The paper does that with rank and select: rank
    /// over the overflows bitmap gives the number to skip, and select over a bitmap of
    /// the pool's delimiters gives the position to skip to. The delimiter bitmap is the
    /// reason for base three -- two adjacent set bits occur nowhere but a delimiter, so
    /// <c>pool &amp; (pool &gt;&gt; 1)</c>, masked to fragment boundaries, marks every
    /// delimiter in a word at once.
    /// </para>
    /// <para>
    /// A pool is finite, so a chunk that draws an unlucky number of heavy hitters can
    /// run out of room. The paper's answer is a <em>tails</em> array: the chunk sets a
    /// mode flag, abandons the pool, and keeps its counters' high bits in a plain array
    /// off to the side. That costs memory, but only for the few chunks that need it,
    /// and section 4.2's retuning exists partly to keep their number small.
    /// </para>
    /// <para>
    /// This is not a transliteration of the authors' implementation. Their shifts and
    /// counter reads are hand-unrolled per pool width and lean on prefetching, on
    /// conditional moves, and on lookup tables that turn a stub update into a single
    /// addition -- optimisations 1 through 5 of their section 4.4, all of which trade
    /// clarity for cache behaviour that a managed runtime would not deliver anyway. The
    /// layout, the encoding and the rank-and-select workflow are theirs; the code is
    /// written to be read.
    /// </para>
    /// </remarks>
    internal sealed class ValeCounterArray
    {
        /// <summary>
        /// A chunk is one cache line.
        /// </summary>
        internal const int ChunkBits = 512;

        private const int WordBits = 64;
        private const int WordsPerChunk = ChunkBits / WordBits;

        /// <summary>
        /// The low bit of every fragment, used to keep the delimiter bitmap to one bit
        /// per fragment.
        /// </summary>
        private const ulong FragmentLowBits = 0x5555555555555555UL;

        /// <summary>
        /// The bounds the paper's retuning keeps its two parameters within.
        /// </summary>
        internal const int MinCountersPerChunk = 16;
        internal const int MaxCountersPerChunk = 92;
        internal const int MinStubBits = 4;
        internal const int MaxStubBits = 32;

        /// <summary>
        /// The parameters to start from, before any workload has been seen.
        /// </summary>
        internal const int DefaultCountersPerChunk = 68;
        internal const int DefaultStubBits = 5;

        /// <summary>
        /// The smallest extension pool a chunk may be given.
        /// </summary>
        /// <remarks>
        /// The authors need 48 bits here because a chunk in tails mode stores a raw
        /// pointer in the space the pool used to occupy. This port keeps its tails in a
        /// managed side table and so needs none, but the floor is kept: it is what the
        /// paper's own tuning enforces, and dropping it would quietly buy space the
        /// published measurements never claimed.
        /// </remarks>
        internal const int MinPoolFragments = 24;

        private readonly ulong[] _words;
        private readonly int _counterCount;
        private readonly int _countersPerChunk;
        private readonly int _stubBits;
        private readonly ulong _stubMask;

        /// <summary>Where a chunk's stubs begin, in bits from the chunk's start.</summary>
        private readonly int _stubBase;

        /// <summary>Where a chunk's extension pool begins.</summary>
        private readonly int _poolBase;

        private readonly int _poolFragments;
        private readonly int _poolBits;
        private readonly int _poolWords;

        /// <summary>Where a chunk's tails flag sits, just above its pool.</summary>
        private readonly int _modeBit;

        /// <summary>
        /// The high bits of the counters in chunks that outgrew their pools, by chunk.
        /// </summary>
        private readonly Dictionary<int, ulong[]> _tails = new Dictionary<int, ulong[]>();

        /// <summary>
        /// Scratch space for the pool being worked on, so that reading and writing a
        /// counter allocates nothing.
        /// </summary>
        private readonly ulong[] _pool;

        /// <summary>
        /// Builds an array of counters, all nought.
        /// </summary>
        /// <param name="counterCount">How many counters the array holds.</param>
        /// <param name="countersPerChunk">How many counters share a cache line.</param>
        /// <param name="stubBits">The width of a counter's fixed part.</param>
        internal ValeCounterArray(int counterCount, int countersPerChunk, int stubBits)
        {
            if (counterCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(counterCount),
                    "An array needs at least one counter.");
            }
            if (countersPerChunk < MinCountersPerChunk || countersPerChunk > MaxCountersPerChunk)
            {
                throw new ArgumentOutOfRangeException(nameof(countersPerChunk),
                    $"A chunk holds between {MinCountersPerChunk} and " +
                    $"{MaxCountersPerChunk} counters.");
            }
            if (stubBits < MinStubBits || stubBits > MaxStubBits)
            {
                throw new ArgumentOutOfRangeException(nameof(stubBits),
                    $"A stub is between {MinStubBits} and {MaxStubBits} bits wide.");
            }

            // The bitmap takes a bit per counter and each stub takes its width, so the
            // fixed part of a chunk is counters * (stubBits + 1). One further bit is
            // set aside for the tails flag, and what is left over is the pool, rounded
            // down to whole fragments.
            var fixedBits = countersPerChunk * (stubBits + 1);
            var poolFragments = (ChunkBits - 1 - fixedBits) / 2;
            if (poolFragments < MinPoolFragments)
            {
                throw new ArgumentOutOfRangeException(nameof(countersPerChunk),
                    $"{countersPerChunk} counters of {stubBits} bits leave room for " +
                    $"{Math.Max(0, poolFragments)} extension fragments in a " +
                    $"{ChunkBits}-bit chunk, short of the {MinPoolFragments} a chunk " +
                    "is required to keep.");
            }

            _counterCount = counterCount;
            _countersPerChunk = countersPerChunk;
            _stubBits = stubBits;
            _stubMask = (1UL << stubBits) - 1;
            _stubBase = countersPerChunk;
            _poolBase = fixedBits;
            _poolFragments = poolFragments;
            _poolBits = poolFragments * 2;
            _poolWords = (_poolBits + WordBits - 1) / WordBits;
            _modeBit = _poolBase + _poolBits;

            ChunkCount = (counterCount + countersPerChunk - 1) / countersPerChunk;
            _words = new ulong[ChunkCount * WordsPerChunk];
            _pool = new ulong[_poolWords];
        }

        /// <summary>How many counters the array holds.</summary>
        internal int Count => _counterCount;

        /// <summary>How many counters share a chunk.</summary>
        internal int CountersPerChunk => _countersPerChunk;

        /// <summary>The width of a counter's fixed part.</summary>
        internal int StubBits => _stubBits;

        /// <summary>How many chunks the counters are spread over.</summary>
        internal int ChunkCount { get; }

        /// <summary>How many fragments a chunk's extension pool holds.</summary>
        internal int PoolFragments => _poolFragments;

        /// <summary>
        /// How many chunks have outgrown their pools and fallen back to a tails array.
        /// </summary>
        /// <remarks>
        /// The paper retunes when this passes a small fraction of the chunks, since a
        /// chunk on tails has stopped paying for what it uses.
        /// </remarks>
        internal int ChunksWithTails => _tails.Count;

        /// <summary>
        /// How many bytes the counters occupy, tails arrays included.
        /// </summary>
        internal long SizeInBytes =>
            (long)_words.Length * sizeof(ulong)
            + (long)_tails.Count * _countersPerChunk * sizeof(ulong);

        /// <summary>
        /// Reads a counter.
        /// </summary>
        internal ulong Get(int index)
        {
            var chunk = ChunkOf(index, out var i);
            var at = chunk * ChunkBits;

            var stub = ReadBits(_words, at + _stubBase + i * _stubBits, _stubBits);
            if (ReadBits(_words, at + i, 1) == 0)
            {
                return stub;
            }

            return ValeCounter.Rebuild(stub, HighBitsOf(chunk, i), _stubBits);
        }

        /// <summary>
        /// Writes a counter.
        /// </summary>
        internal void Set(int index, ulong value)
        {
            var chunk = ChunkOf(index, out var i);
            var at = chunk * ChunkBits;

            WriteBits(_words, at + _stubBase + i * _stubBits, _stubBits,
                ValeCounter.StubOf(value, _stubBits));

            var overflow = ValeCounter.OverflowOf(value, _stubBits);
            var wants = ValeCounter.Overflows(value, _stubBits);
            var had = ReadBits(_words, at + i, 1) != 0;

            // A counter that neither had nor wants high bits leaves the pool alone,
            // which is the case the encoding exists to make cheap.
            if (had || wants)
            {
                SetHighBits(chunk, i, overflow, had, wants);
            }

            WriteBits(_words, at + i, 1, wants ? 1UL : 0UL);
        }

        /// <summary>
        /// Adds one to a counter.
        /// </summary>
        /// <remarks>
        /// A stub with room left is incremented where it lies. This is the paper's
        /// point that most insertions stop at the stub, without its trick of
        /// precomputing each stub's word and offset to make that a single addition.
        /// </remarks>
        internal void Increment(int index)
        {
            var chunk = ChunkOf(index, out var i);
            var stubAt = chunk * ChunkBits + _stubBase + i * _stubBits;

            var stub = ReadBits(_words, stubAt, _stubBits);
            if (stub != _stubMask)
            {
                WriteBits(_words, stubAt, _stubBits, stub + 1);
                return;
            }

            Set(index, Get(index) + 1);
        }

        /// <summary>
        /// Takes one from a counter, which stays at nought if it is already there.
        /// </summary>
        internal void Decrement(int index)
        {
            var chunk = ChunkOf(index, out var i);
            var at = chunk * ChunkBits;
            var stubAt = at + _stubBase + i * _stubBits;

            var stub = ReadBits(_words, stubAt, _stubBits);
            if (stub != 0)
            {
                WriteBits(_words, stubAt, _stubBits, stub - 1);
                return;
            }

            var value = Get(index);
            if (value != 0)
            {
                Set(index, value - 1);
            }
        }

        /// <summary>
        /// Which chunk a counter lives in, and where in that chunk.
        /// </summary>
        private int ChunkOf(int index, out int within)
        {
            if (index < 0 || index >= _counterCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index),
                    $"This array holds {_counterCount} counters.");
            }

            var chunk = index / _countersPerChunk;
            within = index - chunk * _countersPerChunk;
            return chunk;
        }

        /// <summary>
        /// The high bits of a counter known to have overflowed its stub.
        /// </summary>
        private ulong HighBitsOf(int chunk, int i)
        {
            if (_tails.TryGetValue(chunk, out var tails))
            {
                return tails[i];
            }

            ReadPool(chunk);
            return ValeCounter.DecodeExtension(
                _pool, ExtensionStart(Rank(chunk, i)), _poolBits, out _);
        }

        /// <summary>
        /// Replaces a counter's high bits, moving the extensions after it as the space
        /// its own extension needs changes.
        /// </summary>
        private void SetHighBits(int chunk, int i, ulong overflow, bool had, bool wants)
        {
            if (_tails.TryGetValue(chunk, out var tails))
            {
                tails[i] = overflow;
                return;
            }

            ReadPool(chunk);
            var start = ExtensionStart(Rank(chunk, i));
            var used = TotalPoolBits();

            var was = 0;
            if (had)
            {
                ValeCounter.DecodeExtension(_pool, start, _poolBits, out was);
            }

            var wanted = wants
                ? ValeCounter.ExtensionLength(overflow) * ValeCounter.FragmentBits
                : 0;

            if (used - was + wanted > _poolBits)
            {
                // The pool cannot hold this. Everything in it is decanted into a tails
                // array, which has to happen before the pool is disturbed.
                MoveToTails(chunk, used)[i] = overflow;
                return;
            }

            if (had)
            {
                CloseGap(start, was);
            }

            if (wants)
            {
                OpenGap(start, wanted);
                ValeCounter.WriteExtension(_pool, start, overflow);
            }

            WritePool(chunk);
        }

        /// <summary>
        /// Empties a chunk's pool into a tails array and flags the chunk as using one.
        /// </summary>
        /// <remarks>
        /// The extensions are in counter order but say nothing about which counters
        /// they belong to; that is recovered by walking the overflows bitmap alongside
        /// them, the r-th extension belonging to the r-th counter whose bit is set.
        /// </remarks>
        private ulong[] MoveToTails(int chunk, int used)
        {
            var tails = new ulong[_countersPerChunk];

            var at = 0;
            var rank = 0;
            while (at < used)
            {
                var value = ValeCounter.DecodeExtension(_pool, at, used, out var bits);
                tails[CounterWithRank(chunk, rank)] = value;
                at += bits;
                rank++;
            }

            _tails[chunk] = tails;

            Array.Clear(_pool);
            WritePool(chunk);
            WriteBits(_words, chunk * ChunkBits + _modeBit, 1, 1);

            return tails;
        }

        /// <summary>
        /// How many of the counters before this one in its chunk have extensions, which
        /// is how many extensions to skip to reach its own.
        /// </summary>
        private int Rank(int chunk, int i)
        {
            var word = chunk * WordsPerChunk;

            var rank = 0;
            for (var w = 0; w < i / WordBits; w++)
            {
                // Only words lying wholly below the counter are counted, and the bitmap
                // starts the chunk, so no stub bits are ever caught up in this.
                rank += BitOperations.PopCount(_words[word + w]);
            }

            var partial = _words[word + i / WordBits] & ((1UL << (i % WordBits)) - 1);
            return rank + BitOperations.PopCount(partial);
        }

        /// <summary>
        /// Which counter in a chunk owns the extension at a given rank.
        /// </summary>
        private int CounterWithRank(int chunk, int rank)
        {
            var word = chunk * WordsPerChunk;

            var remaining = rank;
            for (var w = 0; w * WordBits < _countersPerChunk; w++)
            {
                var bits = _words[word + w];
                var here = BitOperations.PopCount(bits);
                if (remaining < here)
                {
                    return w * WordBits + SelectBit(bits, remaining);
                }
                remaining -= here;
            }

            throw new InvalidOperationException(
                $"Chunk {chunk} holds fewer than {rank + 1} extensions, so the " +
                "overflows bitmap and the extension pool disagree.");
        }

        /// <summary>
        /// Where in the pool the extension at a given rank begins.
        /// </summary>
        /// <remarks>
        /// This is the paper's rank-and-select workflow. Extensions are packed from the
        /// start of the pool, so the one at rank nought begins there, and any other
        /// begins just past the delimiter of the one before it. Rather than walk the
        /// fragments, a word of the pool is turned into a bitmap of its delimiters --
        /// each of which, and nothing else, is two adjacent set bits -- and the
        /// delimiter wanted is selected from it.
        /// </remarks>
        private int ExtensionStart(int rank)
        {
            if (rank == 0)
            {
                return 0;
            }

            var remaining = rank - 1;
            for (var w = 0; w < _poolWords; w++)
            {
                var delimiters = DelimitersOf(_pool[w]);
                var here = BitOperations.PopCount(delimiters);
                if (remaining < here)
                {
                    return w * WordBits + SelectBit(delimiters, remaining)
                        + ValeCounter.FragmentBits;
                }
                remaining -= here;
            }

            throw new InvalidOperationException(
                $"The pool holds fewer than {rank} extensions, so the overflows bitmap " +
                "claims an extension the pool does not have.");
        }

        /// <summary>
        /// Marks the low bit of every delimiter in a word of the pool.
        /// </summary>
        /// <remarks>
        /// Anding a word with itself shifted down one leaves a bit wherever two set
        /// bits were adjacent; masking to fragment boundaries drops the pairs that
        /// straddle two fragments. A delimiter is the only fragment with both bits set,
        /// so what survives is exactly one bit per delimiter. Fragments never straddle
        /// words, so no delimiter is missed at a word's edge.
        /// </remarks>
        private static ulong DelimitersOf(ulong word) =>
            word & (word >> 1) & FragmentLowBits;

        /// <summary>
        /// How much of the pool is in use, which is where the last delimiter ends.
        /// </summary>
        private int TotalPoolBits()
        {
            for (var w = _poolWords - 1; w >= 0; w--)
            {
                if (_pool[w] != 0)
                {
                    return w * WordBits + WordBits - BitOperations.LeadingZeroCount(_pool[w]);
                }
            }

            return 0;
        }

        /// <summary>
        /// Moves everything from a position up, to make room for an extension.
        /// </summary>
        /// <remarks>
        /// A fragment at a time. The authors shift whole words, in a routine written
        /// out separately for pools of one, two and more words; the last of those reads
        /// one word past the end of the pool it was given.
        /// </remarks>
        private void OpenGap(int at, int amount)
        {
            for (var b = _poolBits - amount - ValeCounter.FragmentBits;
                 b >= at;
                 b -= ValeCounter.FragmentBits)
            {
                ValeCounter.SetFragment(_pool, b + amount, ValeCounter.FragmentAt(_pool, b));
            }

            for (var b = at; b < at + amount; b += ValeCounter.FragmentBits)
            {
                ValeCounter.SetFragment(_pool, b, 0);
            }
        }

        /// <summary>
        /// Closes the space an extension occupied, drawing what follows it down.
        /// </summary>
        private void CloseGap(int at, int amount)
        {
            for (var b = at; b + amount < _poolBits; b += ValeCounter.FragmentBits)
            {
                ValeCounter.SetFragment(_pool, b, ValeCounter.FragmentAt(_pool, b + amount));
            }

            for (var b = Math.Max(at, _poolBits - amount);
                 b < _poolBits;
                 b += ValeCounter.FragmentBits)
            {
                ValeCounter.SetFragment(_pool, b, 0);
            }
        }

        /// <summary>
        /// Lifts a chunk's pool into the scratch buffer, where it starts at bit nought.
        /// </summary>
        /// <remarks>
        /// The pool does not begin on a word boundary in the chunk -- the bitmap and
        /// the stubs before it are sized by the tuning, not by the machine. Working on
        /// a copy that does keeps every fragment aligned, which is what the delimiter
        /// trick needs.
        /// </remarks>
        private void ReadPool(int chunk)
        {
            var at = chunk * ChunkBits + _poolBase;
            for (var w = 0; w < _poolWords; w++)
            {
                var width = Math.Min(WordBits, _poolBits - w * WordBits);
                _pool[w] = ReadBits(_words, at + w * WordBits, width);
            }
        }

        private void WritePool(int chunk)
        {
            var at = chunk * ChunkBits + _poolBase;
            for (var w = 0; w < _poolWords; w++)
            {
                var width = Math.Min(WordBits, _poolBits - w * WordBits);
                WriteBits(_words, at + w * WordBits, width, _pool[w]);
            }
        }

        /// <summary>
        /// The position of the n-th set bit, counting from nought.
        /// </summary>
        private static int SelectBit(ulong word, int n)
        {
            for (var i = 0; i < n; i++)
            {
                word &= word - 1;
            }

            return word == 0 ? WordBits : BitOperations.TrailingZeroCount(word);
        }

        /// <summary>
        /// Reads a field of up to sixty-four bits, which may straddle two words.
        /// </summary>
        private static ulong ReadBits(ulong[] words, int at, int width)
        {
            if (width == 0)
            {
                return 0;
            }

            var w = at / WordBits;
            var shift = at % WordBits;
            var mask = width == WordBits ? ulong.MaxValue : (1UL << width) - 1;

            var value = words[w] >> shift;
            if (shift + width > WordBits)
            {
                value |= words[w + 1] << (WordBits - shift);
            }

            return value & mask;
        }

        /// <summary>
        /// Writes a field of up to sixty-four bits, which may straddle two words.
        /// </summary>
        private static void WriteBits(ulong[] words, int at, int width, ulong value)
        {
            if (width == 0)
            {
                return;
            }

            var w = at / WordBits;
            var shift = at % WordBits;
            var mask = width == WordBits ? ulong.MaxValue : (1UL << width) - 1;

            value &= mask;
            words[w] = (words[w] & ~(mask << shift)) | (value << shift);

            if (shift + width > WordBits)
            {
                var written = WordBits - shift;
                words[w + 1] = (words[w + 1] & ~(mask >> written)) | (value >> written);
            }
        }
    }
}
