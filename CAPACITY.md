# How much each structure holds

The [README](README.md#how-much-each-one-holds) has the table. This is what stands behind
it: what each structure counts, what sets the most it can be built for, the most that
reads back under the default budget, the most that can be saved, and how the settings you
choose move each.

The figures here are held to the code by `TestCapacityDocumentation`. It works every
number in the table out again from the library's own sizing code and from what a read of a
small structure is charged, checks each sentence below that states a figure or a rule, and
fails when this document and the library disagree.

## Reading the numbers

**What an item is depends on the structure.** A Bloom filter is *sized for* `n` items and
is only as accurate as promised up to `n`. A scalable filter *grows* as items are added. A
theta sketch *retains* `k` values however many items it sees. A count-min sketch takes
*unlimited* items, and what you choose is how wide it is. The README's tables are grouped
that way, and the heading of each section below says which group the structure is in.

**Builds up to** is the largest the constructor accepts, and that works. Most limits are
one of four things: a 32-bit parameter (`uint`), the longest array .NET allows
(`Array.MaxLength`, 2,147,483,591 elements), a field of the persisted format, or the
arithmetic that turns your settings into a size. Almost all of them are far past what fits
in memory: a structure at its limit is gigabytes, and building one is a matter of having
the machine for it. `memory` means the constructor checks nothing before an allocation
fails.

**Loads by default up to** is the largest that `ReadFrom(stream)` reads back. Every read
has a budget, `ReadOptions.DefaultMemoryBudget`, 1,073,741,824 bytes, and a read is
charged what it allocates: the payload, the copies made of runs within it, and the
structure it builds. That is two or three times the structure, so the default reads a
structure of up to roughly half a gigabyte. A larger one reads back with
`ReadFrom(stream, new ReadOptions(bytes))` or `ReadOptions.Unlimited`, which is why the
two numbers differ. `any` means every size the constructor builds reads back by default.

**Saves up to** is the largest that `WriteTo` can write, and for most structures it is
the same as what they build, which the table says as `as built`. A structure is written
into one `byte[]`, wrapped in a frame that is another, and read back from a third, so the
payload is at most 2,147,483,573 bytes: `Array.MaxLength` less the 18 bytes of envelope.
The 32-bit length in the envelope is not what limits it. A structure built larger than
that works and cannot be saved, which is what the figure in this column is for; it is
worked out from the payload each structure writes, so it follows the layouts in
[FORMAT.md](FORMAT.md). A read is charged at least twice the payload, which is why the
default budget reads payloads of up to about half a gigabyte.

**`≈`** marks a number rounded to the digits shown. A read is charged a few bytes more or
less according to how each array rounds up to eight, and the suite works the number out
with that slack in hand, so it is documented to the digits the slack cannot move.

**`†`** marks a number that can only be measured at the scale it describes. It is the
measured one, with the settings given, at commit 426348d, and the suite holds it to an
estimate worked out from reads it can afford.

## Where master stops working before it should

Three ceilings below are defects, not capacities, and the "Builds up to" figures stop
short of them. Each is checked by the suite, so the numbers change when the defect is
fixed.

- **A bucket array longer than 32 bits of bucket data is truncated.** `Buckets` sizes its
  array with 32-bit arithmetic. A `BloomFilter` sized for 448,089,842 items at 1%, a
  4-bit `CountingBloomFilter` sized for more than 112,022,460, and a 3-bit
  `StableBloomFilter` of more than 1,431,655,762 cells are all built with an array too
  short for their bits, without an error, and fail on `Add`. The tables stop at the last
  size that works.
- **A table of 2^32 slots is a table of one.** `InfiniFilter` and `MementoFilter` double
  their table when three quarters full, and a table of 32 address bits has one slot. The
  tables stop at the last table that does not double past 2^31 slots.
- **`SublimeCountMinSketch` doubles its width in a signed 32-bit integer**, so its
  width cannot pass 2^30.

## The BloomFilter 32-bit band

`BloomFilter`, `DeletableBloomFilter` and a one-bit `StableBloomFilter` place their bits
with a 32-bit kernel; `BloomFilter64` uses a 128-bit one. When such a filter is a large
share of 2^32 bits, the false positive rate it delivers is worse than the one it was
asked for. This was measured at master 596071f by the investigation of issue 172, and is
not rebuilt by the suite, which cannot make a filter of three billion bits: a filter sized
for 1% measured 1.419% ± 0.006% at m = 0.75 · 2^32 bits, where `BloomFilter64` at the same
m and k measured 1.003%, and both were 1.0% at m = 0.125 · 2^32. The band runs from about
0.27 · 2^32 bits, which is about 121 million items at 1%, to nearly the 32-bit limit, and
the worst case is about ×1.42 at 1% and ×1.67 at 0.1%. Nothing in the library refuses
or warns about it. For a filter of more than about 121 million items at 1% use
`BloomFilter64`, which has no band.

## Sized for n items

### `BloomFilter`

`n` is the number of items the filter is sized for. At `n` it has the false positive rate
you asked for; past it the filter keeps working and the rate climbs, with no error to say
so. The size is `m ≈ 1.44 · n · log2(1/fp)` bits: 9.6 bits per item at 1%, 14.4 at 0.1%,
using 7 hash functions at 1% and 10 at 0.1%.

`m` is a `uint`. `Utils.OptimalM` refuses a filter of more than 4,294,967,295 bits, which
is 448,089,842 items at 1%, and the figure in the table is the last of those that builds a
working array. For more items use `BloomFilter64`, which sizes in 64 bits, or
`ScalableBloomFilter`, which grows by adding filters.

A read is charged twice the filter's bytes, so the default budget reads a filter of
about 537 megabytes: nearly all of what the constructor builds, and a filter sized for the
last few percent of its limit needs `ReadOptions`.

### `BloomFilter64`

The same filter sized in 64 bits: `n` is a `ulong`, and the bits are held as arrays of
2^30 bytes, so nothing in the constructor stops it short of memory. The false positive
rate costs the same bits per item as `BloomFilter`: about 1.2 bytes an item at 1%.

A filter can be built larger than it can be saved. The payload of a saved structure is
at most 2,147,483,573 bytes, which is about 1.79 billion items at 1%.

### `CountingBloomFilter`

The same sizing, with `b` bits in each bucket instead of one, so `b` times the memory and
the same false positive rate. Because the bucket array is `m · b` bits, the largest `n`
falls in proportion to `b`: the table gives 4-bit and 8-bit counters at 1%. A counter that
reaches its maximum stays there rather than wrapping.

### `DeletableBloomFilter`

The sizing of `BloomFilter` with `r` of its bits set aside as the collision regions that
make deletes possible. `r` has to be at least 1 and less than the filter's bits, and what
it takes comes out of the data region, so a larger `r` leaves fewer bits for the items.

### `PartitionedBloomFilter`

One array of bits for each of the `k` hash functions, each `m / k` bits, so it takes the
same `n` as `BloomFilter` and no array is long enough to be truncated. A read charges
every partition as an array of its own.

### `CuckooBloomFilter`

The table has 2^k buckets of 4 entries, the smallest power of two that holds `n` items at
95% full: `n / 3.8` buckets, rounded up. So the table, and the memory, doubles each time
`n` passes 3.8 times a power of two. An entry is a fingerprint of `f = ⌈log2(8/fp)⌉`
bits: 10 at 1% and 13 at 0.1%. A bucket therefore costs 5.5 bytes at 1% (four 10-bit
fingerprints and a bit apiece to say which are in use).

Entries are numbered in 32 bits, so a table has at most 2^31 entries, 2^29 buckets, and
the table gives the `n` that fills it. A read allocates the whole table whatever it
holds, so the empty row is the table at 2^27 buckets, the largest that fits a gibibyte;
and each entry the filter holds is written and read at 10 bytes at 1%, so a filter holding
as many items as it was sized for loads up to a smaller `n`. That number does not rise
smoothly with `n`, because the table doubles.

### `QuotientFilter`

The table has 2^q slots, the smallest power of two that holds `n` items at three quarters
full, and each slot is a remainder of `r = ⌈log2(1/fp)⌉` bits (7 at 1%, 10 at 0.1%) and 3
bits of bookkeeping. The memory is `2^q · (r + 3)` bits and doubles each time `n` passes
three quarters of a power of two.

The slot count is a `uint`, so `q` is at most 31 and `n` at most the figure in the table,
whatever the rate; the quotient and remainder together are cut from a 64-bit hash, so a
table that size takes a rate no finer than 2^-33. A read is charged three times the table,
so the default budget reads one of 2^28 slots at 1%.

### `BinaryFuseFilter`

Built once from the whole set, so there is no `n` to guess. The table has about 1.126 cells
per key, rounded up to a whole segment, each cell 1 byte (a false positive rate of 0.39%)
or 2 bytes (0.0015%). Nothing in the builder limits the set but memory. A read is charged
twice the table, which is about 2.25 bytes a key for 8-bit fingerprints.

### `BloomierFilter`

Built once from the whole set of keys and their values, at about the same cells per key. A
cell holds an 8-bit fingerprint and a value of `v` bits, rounded up to whole bytes: 2
bytes at `v = 8`, and 6 at `v = 40`, the widest. The builder limits the set to memory;
a read is charged twice the table.

### `Grafite`

Built once from the whole set of keys, for ranges up to `L` wide at a false positive rate
`fp`. The keys are folded into a universe `n · L / fp` wide and stored as Elias-Fano:
about `log2(L/fp)` bits a key for the low parts, and between 2 and 3 for the high parts.
A wider range, or a smaller rate, costs more bits a key and so lowers the number a gibibyte
reads.

The high parts are addressed with an `int`, so a filter has at most 2,147,483,647 bits of
them. How many depends on where the keys' largest code falls in the universe, which
depends on the keys, so the table gives the worst case: filters of those sizes always
build and read, and many key sets do better.

### `InvertibleBloomLookupTable`

`n` is `expectedDifferences`: the number of keys by which the two sets it reconciles may
differ, and past which a table cannot decode them. It has 1.5 cells for each (and a few
more for a small table), each cell holding a count, a hash sum and a sum of the keys, so
the key width matters twice: the cells times the key size has to fit one array, which is
what limits the build, and wider keys make a larger table to read.

## Grows as items are added

### `ScalableBloomFilter`

`hint` is how many items each contained filter is sized for. A new filter is started
when the last reaches a fill ratio of one half, which takes a little under `hint` items,
so the filter holds about `hint` times as many items as it has filters. Each new filter
is sized for a rate `r` times the last one's, so that the compound rate stays bounded:
the filters get larger, and there are more of them the closer `r` is to 1.

The filter stops growing when the rate of the next filter is so small that the sizing
overflows, which at 1% and `r = 0.8` comes after about 3,160 filters, and the `Add` that needs the next
throws `OverflowException`. The table gives the items added before that. Reading costs
about four times the bytes of the filters, because each is a payload inside a payload,
and the later filters are the largest, so the default budget is reached first. A larger
`hint` makes each filter larger, and both numbers scale with it.

### `InfiniFilter`

A quotient-style table that doubles when three quarters full, so `n` is the items added,
and the table after `n` items has 2^q slots for the smallest `q` that holds them at that
load. A slot is the fingerprint, its age counter and 3 bits of bookkeeping: `F + 4` bits.
Fingerprints that have aged out of the table move to smaller tables beneath it, which are
a sliver of the main one.

A table of 2^31 slots is the largest that works, which is the figure in the table; the
next doubling makes a table of one slot, which is a defect. A read is charged three times
the table, which at 8 or 16 bits is the table of 2^27 slots. The fingerprint width does
not move that number, because the table doubles at the same load whatever the width, and
2^28 slots do not fit either way.

### `MementoFilter`

The same table, with each slot also holding the memento that lets it answer a range
query: `F + 1 + m + 3` bits for a range up to 2^m wide, so a wider range costs more per
slot. The items counted are entries, one for each block of `range` consecutive keys, so
keys that fall in blocks already present add nothing. A range of 256 reads back a table of
2^27 slots, and a range of 65,536 only 2^26.

## Retains k

### `InverseBloomFilter`

`capacity` slots, each holding one item: a new item lands on one slot and replaces what
was there, so it can forget and answer a false negative. Any number of items can be
added; what it holds at once is at most `capacity`. Its slots are an array of references,
so `capacity` is at most `Array.MaxLength`, and every slot is allocated whether or not it
is occupied, so an empty filter reads back at 8 bytes a slot, and a full one at 56
for 8-byte items. It saves only what it holds: a slot that holds an item is written as an
index, a length and the item, so a full filter reaches the ceiling long before an empty
one.

### `ThetaSketch`

Retains the `k` smallest hashes it has seen, however many distinct items that was. It
holds up to twice that less one between trims, in one array, so `k` is at most half of
`Array.MaxLength`. It is written holding up to 2k - 1 values, so the table gives the
largest `k` for which any state it can be in reads back.

### `TupleSketch`

The theta sketch with a summary beside each key, so each value costs a key and a double
to read, and the largest `k` that always reads back is the lower for it.

### `MinHashSignature`

`k` hash values, one array, whatever the size of the set they summarise. A read is charged
16 bytes a value.

### `SimHashSignature`

One 64-bit value however long the document. It has no size to choose.

### `TopK`

Two things limit a top-k. `k` is how many elements it retains, with the most the heap and
its index allow; a read is charged for the elements it holds, not for `k`. And it counts
with a count-min sketch of width `⌈e/ε⌉` and depth `⌈ln(1/δ)⌉`, the second row of the
table, which is charged like any other count-min sketch and nested.

### `VarOpt`

A sample of `k` items out of any number. Its five regions are `k + 1` slots each from the
start, so an empty sample is charged for all of them and a full one also for the items it
keeps, the second row.

## Takes unlimited items

### `StableBloomFilter`

`m` cells of `d` bits each, holding the most recent items and forgetting the rest, so the
items it takes are unlimited and what you choose is the cells. The packed array is `m · d`
bits, so the most cells falls in proportion to `d`, as the three rows of the table show.
Past that the array is truncated.

### `HyperLogLog`

`m` registers, a power of two, one byte each; the error is `1.04 / √m`. It uses 32 bits of
the hash, so it cannot tell apart more than 2^32 distinct items, 4,294,967,296, and its
estimate corrects for collisions in a 32-bit space from about 143 million. A raw estimate
past 2^32 makes `Count()` return 0. Measured at master 596071f by the investigation of
issue 172, not rebuilt by the suite: it overestimates past about 143 million distinct
items, by +3.2% at 2^28 and +13% at 2^30, and returns 0 from 2^33. It suits cardinalities
well below 100 million; for more use `HyperLogLogPlus` or `UltraLogLog`, which use 64 bits.

### `HyperLogLogPlus`

Registers 2^p with `p` at most 18, in a sparse form while small. Counts are 64-bit. The
largest is about half a megabyte, so every one reads back by default.

### `UltraLogLog`

Registers 2^p with `p` at most 26, one byte each, and a 64-bit hash. Every precision reads
back by default: a read is charged three times the registers.

### `SetSketch`

`m` two-byte registers, which a read is charged 12 bytes a register for (the registers and
the two arrays that order their updates, and the payload they came in).

### `CountMinSketch`

Counters are 64-bit, so a row holds counts to 18 quintillion and the sketch takes any
number of items. What grows with them is the error: an estimate is at most `ε · N` over
the truth for `N` items added, so a sketch for a stream you expect to reach `N` wants `ε`
about the error you can accept divided by `N`. Width is `⌈e/ε⌉` and depth `⌈ln(1/δ)⌉`,
5 at δ = 1%. A read charges 16 bytes a counter.

### `CountSketch`

The same, with width `⌈1/ε²⌉`, so a rate ten times finer costs a hundred times the
width, and counters of 8 bytes that are signed. A read charges 24 bytes a counter.

### `PrivateCountMinSketch`

You choose the width and depth directly, and the privacy budget `ρ` sets the noise and
not the size. A read charges 16 bytes a counter.

### `SublimeCountMinSketch`

The width doubles as items are added: from `w` to `2w` when the count reaches `(w · s)^(1/g)`
for the growth exponent `g` and size factor `s`, so a larger `g` grows faster. At the default
`g` of one half and `s` of one the width is about the square root of the items: a width of
2^14 from 67,108,864 items and 2^15 from 268,435,456. At `g` of 0.9 a sketch reaches a
width of 2^22 at 10,568,984 items. The width is a signed 32-bit integer, so it cannot pass
2^30.

What a counter costs to read depends on how the counters pack, which depends on the counts
they hold; that makes the width the read reaches a measurement, which was made at `g` of
0.9 and 0.99, and is 2^21.

### `HeavyKeeper`

Takes any number of items. `k` is how many elements it retains, a `uint` bounded as
`TopK`'s is, and the width and depth are the buckets each element can land in: 20 bytes a bucket to read.

### `DDSketch`

Takes any number of values, and what grows is the bucket span: the range of values
divided by the relative accuracy, `ln(max/min) / ln((1+α)/(1−α))` buckets for each sign.
A value of one sign spans at most every positive double, which at 1% is a few tens of thousands of buckets,
and finer accuracies cost proportionally more. A read charges 24 bytes a bucket of span,
for each sign.

### `DpswSketch`

Takes any number of items, and what it holds is a window of the most recent `window`
of them, as a set of sketches whose number grows slowly with the window. The table gives the
window it reads back at with the default width and depth, which is a measurement.
