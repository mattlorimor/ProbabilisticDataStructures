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
two numbers differ. The default's refusal of a larger one names the bytes to pass: the
whole of what the read is charged, worked out before anything is allocated, and a budget
of that reads it. `any` means every size the constructor builds reads back by default.

**Saves up to** is the largest that `WriteTo` can write, and for most structures it is
the same as what they build, which the table says as `as built`. A structure is written
into one `byte[]`, wrapped in a frame that is another, and read back from a third, so the
payload is at most 2,147,483,573 bytes: `Array.MaxLength` less the 18 bytes of envelope.
The 32-bit length in the envelope is not what limits it. A structure built larger than
that works and cannot be saved, and `WriteTo` of one throws an `InvalidOperationException`
naming the size it would be and the size one can be, with the structure unchanged; the
figure in this column is for that; it is
worked out from the payload each structure writes, so it follows the layouts in
[FORMAT.md](FORMAT.md). A read is charged at least twice the payload, which is why the
default budget reads payloads of up to about half a gigabyte.

**`≈`** marks a number rounded to the digits shown. A read is charged a few bytes more or
less according to how each array rounds up to eight, and the suite works the number out
with that slack in hand, so it is documented to the digits the slack cannot move.

**`†`** marks a number that can only be measured at the scale it describes. It is the
measured one, with the settings given, at commit 426348d, and the suite holds it to an
estimate worked out from reads it can afford.

## Where a limit refuses

Four ceilings were silent defects when this document was first written. Each now stops
the structure with an exception that names the limit, before anything changes, and the
"Builds up to" figures are the sizes that refuse nothing.

- **A bucket array is one .NET array.** `Buckets` sizes it in 64 bits, up to the
  2,147,483,591 bytes an array holds, and the filters refuse more against the parameter
  that chose it. For buckets of one to three bits that is more than a 32-bit count
  addresses, so the count is the limit; from four bits up it is the array:
  4,294,967,182 four-bit counters, or 2,147,483,591 eight-bit ones. Versions that sized the
  array in 32 bits built a filter whose bits passed 2^32 with an array too short for them,
  without an error, and it failed on `Add`; a payload written then is refused when read,
  with that said.
- **A table of 2^32 slots is not a table.** `InfiniFilter` and `MementoFilter` double their
  table when three quarters full, up to 2^31 slots, and the `Add` that would double it
  past that throws, with the filter as it was.
- **A Sublime row is at most 2^30 counters.** The `Add` that would double it past that
  throws, with the sketch as it was.
- **A Memento slot is at most 64 bits.** The constructor refuses a fingerprint and a range
  that make it wider, and a slot of exactly 64 bits holds its keys.

## How the 32-bit filters place their bits

`BloomFilter`, `CountingBloomFilter`, `DeletableBloomFilter`, `StableBloomFilter` and
`PartitionedBloomFilter` turn an item's hash into `k` positions. Until the release after
6.2.0 every one of them did it with 32-bit values: position `i` was
`(low + i · high) mod m`, with `low` and `high` the hash's two 32-bit halves and the sum
taken in 32-bit arithmetic. That gives the first `2^32 mod m` positions one more of the
2^32 values than the rest, so they fill faster, and a query lands on them as often as an
item did. The false positive rate rose with it, in a sawtooth over the size: not at all
where `m` is a power of two, and most where `2^32 mod m` is half of `m`. This was measured
at master 596071f by the investigation of issue 172, and is not rebuilt by the suite,
which cannot make a filter of three billion bits: a filter sized for 1% measured 1.419% ±
0.006% at m = 0.75 · 2^32 bits, where `BloomFilter64` at the same m and k measured 1.003%,
and sized for 0.1% it measured 0.173%.

From the release after 6.2.0, a new filter whose positions are reduced modulo 2^27 or
more — 16 MiB of bits, about 14 million items at 1%, and for the partitioned filter each
partition — places them with 64-bit values, exactly as `BloomFilter64` does. At m = 0.75 ·
2^32 sized for 1% it measured 1.004%, as `BloomFilter64` did. A smaller filter is placed
as before. Below 2^27 the excess is at most about 0.07% of the rate at 1%, too small to
measure. Every save of these filters, whatever its size, is written at format version 8,
which records the placement in a byte of its own and the item count in 64 bits, and which
6.2.0 and earlier refuse. A filter read from a payload written before the release after
6.2.0 keeps the 32-bit placement, and the rate that goes with it, whatever its size, and
records it when saved again. It does not merge with a new filter of the same size; to have
the rate it was sized for, add its items to a new filter.

## How the sketches place their columns

`CountMinSketch`, `PrivateCountMinSketch` and `HeavyKeeper` count an item in one column of
each row, and `InverseBloomFilter` holds it in one slot. Until the release after 6.2.0 each
chose it as the 32-bit filters chose their positions, reducing a 32-bit value modulo the
width, which gives the first `2^32 mod w` columns `1 / floor(2^32 / w)` more of the stream
than the rest: about 3% more just past 2^27 columns, a quarter more at 2^29.8, and half as
much again from a third of 2^32 to the widest row. A heavier column collects more of other
items' counts, so the items counted there are overcounted by more than the bound assumes,
and the inverse filter overwrites what its first slots hold sooner.

From the release after 6.2.0 a new one 2^27 columns, buckets or slots wide or wider places
with 64-bit values, as the 32-bit filters do from there, and a narrower one is placed as
before. A count-min sketch is that wide from an `ε` of about 2.03e-8, and a row of it is a
gigabyte, twice that to read, so it reads back only with a budget raised to match. Every
save of these is written at format version 9, which records the placement in a byte of
its own. One read from a payload written before keeps the 32-bit placement whatever its
width, and records it when saved again; a count-min sketch read so does not merge with a
new one of the same width.

## Sized for n items

### `BloomFilter`

`n` is the number of items the filter is sized for. At `n` it has the false positive rate
you asked for; past it the filter keeps working and the rate climbs, with no error to say
so. The size is `m ≈ 1.44 · n · log2(1/fp)` bits: 9.6 bits per item at 1%, 14.4 at 0.1%,
using 7 hash functions at 1% and 10 at 0.1%.

`m` is a `uint`. `Utils.OptimalM` refuses a filter of more than 4,294,967,295 bits, which
is 448,089,842 items at 1%, and that is the figure in the table. For more items use
`BloomFilter64`, which sizes in 64 bits.
`ScalableBloomFilter` does not reach much further: each filter it adds is a 32-bit filter.

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
the same false positive rate. Because the bucket array is `m · b` bits and is one array,
the largest `n` falls in proportion to `b` from four bits up: the table gives 4-bit and
8-bit counters at 1%, and a constructor asked for more refuses, naming `n`. The array is
as large as the table, so the largest build is more than a payload holds and the
"Saves up to" figure is lower than the "Builds up to" one. A counter that reaches its
maximum stays there rather than wrapping.

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
or 2 bytes (0.0015%). The fingerprints are one array, which holds 2,147,483,591 bytes, so
the most distinct keys are 1,908,641,337 at 8 bits and 954,204,160 at 16; a set with more
is refused, naming `items`, before the fingerprints are allocated. A read is charged
twice the table, which is about 2.25 bytes a key for 8-bit fingerprints.

It is not refused before the items are hashed, though. The build puts the hash of every item
it is given into one list, duplicates and all, and holds that list and a sorted copy of it,
at least 16 bytes an item, before it can refuse: the distinct keys are counted only then.
That is 30.5 GB at the 1,908,641,337 keys of the 8-bit limit, and more for a set past it.
And a list holds at most 2,147,483,591 hashes, so a build given more items than that fails
with an `OutOfMemoryException`, however few of them are distinct.

### `BloomierFilter`

Built once from the whole set of keys and their values, at about the same cells per key. A
cell holds an 8-bit fingerprint and a value of `v` bits, rounded up to whole bytes: 2
bytes at `v = 8`, and 6 at `v = 40`, the widest. The cells are one array, so the most
distinct keys are 954,204,160 at `v` up to 8, 636,136,107 up to 16 and 318,068,053 up to
40; a set with more is refused, naming `pairs`, before the cells are allocated. A read is
charged twice the table.

A key is known to the map by its 64-bit hash alone, and the keys are not kept, so two
different keys with one hash are one key to it. With the same value that costs nothing; with
different values the build is refused, with an `ArgumentException` that says the keys
collided. The chance is chance, and grows with the square of the size: the expected number
of colliding pairs among `n` keys is `n(n−1) / 2^65`, so the chance some two collide is

| Keys | Chance two share a hash |
| --- | --- |
| 1 million | 0.000003% |
| 10 million | 0.0003% |
| 100 million | 0.03% |
| 318 million, the most at 40-bit values | 0.27% |
| 636 million, the most at 16-bit values | 1.1% |
| 954 million, the most at 8-bit values | 2.4% |
| 2^32, which no map builds | 39% |

and the chance of a refusal is at most that, since the values must differ as well. Building
again with a different hash function moves the collision. Widening the hash would change
the stored format, and has not been done.

### `Grafite`

Built once from the whole set of keys, for ranges up to `L` wide at a false positive rate
`fp`. The keys are folded into a universe `n · L / fp` wide and stored as Elias-Fano:
about `log2(L/fp)` bits a key for the low parts, and between 2 and 3 for the high parts.
A wider range, or a smaller rate, costs more bits a key and so lowers the number a gibibyte
reads.

The universe is at most 2^61 − 2, the modulus of the hash less one, so `n · L / fp` has to
stay under 2^61. A request that needs more is refused with an `ArgumentOutOfRangeException`,
naming `maxRangeSize` and saying the largest range that works for that many keys at that
rate. That is `L < 2^61 · fp / n`, to within the rounding of a double: for four keys at 1%,
ranges up to about 5.8e15; for a million keys at 1%, about 2.3e10. When not even a range of
one key fits, `n / fp` of 2^61 and over, it names `falsePositiveRate` and the finest rate
those keys can have, a little over `n / 2^61`. Earlier releases built such a filter at the
largest universe instead, and delivered a weaker rate than they were asked for without
saying; one they wrote still reads, and answers as it did.

The high parts are addressed with an `int`, so a filter has at most 2,147,483,647 bits of
them. How many depends on where the keys' largest code falls in the universe, which
depends on the keys, so the table gives the worst case: filters of those sizes always
build and read, and many key sets do better.

The build holds the keys twice over before it knows whether they fit: the distinct keys in
one sorted set and their codes in another, 48 bytes a key each, about 96 between them,
before the high parts are counted. At the 838 million keys of the worst case that is 80 GB,
and a set past it is refused only after.

### `InvertibleBloomLookupTable`

`n` is `expectedDifferences`: the number of keys by which the two sets it reconciles may
differ, and past which a table cannot decode them. It has 1.5 cells for each (and a few
more for a small table), each cell holding a count, a hash sum and a sum of the keys, so
the key width matters twice: the cells times the key size has to fit one array, which is
what limits the build, and wider keys make a larger table to read.

## Grows as items are added

### `ScalableBloomFilter`

`hint` is how many items the first contained filter is sized for, and `growth` is how many
times as many each next one is sized for: each contained filter is sized for `growth`
times as many items as the last. A growth factor of 2 is what a filter built without one
takes, and a payload written before the factor existed reads back with a growth factor of
1. A new filter is started when the last reaches a fill ratio of one half, which takes a
little under what it is sized for, so the filter holds about as many items as its last
filter is sized for, and a growth factor of 2 doubles that with every filter. Each new
filter is also sized for a rate `r` times the last one's, so that the compound rate stays
bounded: a filter is larger for the tighter rate, and there are more of them the closer
`r` is to 1.

What ends the filter, and so what the growth factor does to the first number, depends on
that factor. With a growth factor of 2 or more the filters double until the next filter
would need more bits than a contained filter addresses, which is the ceiling in the
table; and the `Add` that needs it throws `InvalidOperationException` with the filter as it
was. With a growth factor of 1 every filter is sized for `hint` items, and what ends the
filter is the tightened rate, not the bits: it comes after about 3,160 filters, when the
rate of the next has no finite reciprocal to size it from, at a small fraction of the
items. So the table gives both, at the same `hint`, rate and `r`.

Reading costs about four times the bytes of the filters, because each is a payload inside a
payload. With a growth factor of 1 the filters get larger as their rate tightens, and the
default budget is reached before the ceiling. With a factor of 2 the last filters are the
largest by far, and the default budget is reached a number of doublings before the
ceiling: the read stops at the filter before the one that passes it, which is why the
number in the table is a sum of whole filters and not a round one.

### `InfiniFilter`

A quotient-style table that doubles when three quarters full, so `n` is the items added,
and the table after `n` items has 2^q slots for the smallest `q` that holds them at that
load. A slot is the fingerprint, its age counter and 3 bits of bookkeeping: `F + 4` bits.
Fingerprints that have aged out of the table move to smaller tables beneath it, which are
a sliver of the main one.

A table of 2^31 slots is the largest that works, which is the figure in the table; the
`Add` that would double it throws, with the filter as it was. A read is charged three times
the table, which at 8 or 16 bits is the table of 2^27 slots. The fingerprint width does
not move that number, because the table doubles at the same load whatever the width, and
2^28 slots do not fit either way.

### `MementoFilter`

The same table, with each slot also holding the memento that lets it answer a range
query: `F + 1 + m + 3` bits for a range up to 2^m wide, so a wider range costs more per
slot. The items counted are entries, one for each block of `range` consecutive keys, so
keys that fall in blocks already present add nothing. A range of 256 reads back a table of
2^27 slots, and a range of 65,536 only 2^26. A slot is held in a 64-bit word, so the
constructor refuses a fingerprint and a range that make it wider.

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

The regions are allocated whole when the sample is made, before it holds anything, at 40
bytes for each unit of `k`: 80 GiB at the largest `k`, 2,147,483,590.

## Takes unlimited items

### `StableBloomFilter`

`m` cells of `d` bits each, holding the most recent items and forgetting the rest, so the
items it takes are unlimited and what you choose is the cells. The packed array is `m · d`
bits and is one array, so from four bits up the most cells falls in proportion to `d`, as
the rows of the table show. Past that the constructor refuses, naming `m`.

### `HyperLogLog`

`m` registers, a power of two, one byte each; the error is `1.04 / √m`. It takes the
register index and the run of zeros from 32 bits of the hash, so a register records a run
of at most 32 − log2 m zeros. One hash value in 2^32 caps any given register, so after n
distinct items each register has reached that cap with probability 1 − e^(−n/2^32),
whatever m is. A register holds what a wider hash would have given it, cut off at the cap,
so collisions in those 32 bits do not bias the estimate, and `Count()` accounts for the
capped registers. With 16 registers or more the estimate stays close to its nominal error
up to about 2^33 distinct items, 8,589,934,592. Past that the error grows, to about 1.5–2
times nominal at 2^34 with 1,024 registers or more, and a sketch whose every register is
capped reports its ceiling, about 2^32 × ln m: 11,280,826,639 with 16 registers,
29,737,154,114 with 1,024 and 41,673,492,339 with 16,384. Below 16 registers the ceiling is
under 2^33; with one register it is 1,490,120,207. It never reports 0 once anything has
been added. `IsSaturated()` is true when every register is capped, which is when `Count()`
is a floor and not an estimate. It does not warn sooner: the error grows from about 2^33
distinct items, and a sketch is not likely to be full until later still, and the more
registers it has the later that is. A caller who wants the earlier warning compares
`Count()` with 2^33. Earlier releases, measured on the estimator of master 596071f and not
rebuilt by the suite, overestimated past about 143 million distinct items, by +3.2% at 2^28 and
+13% at 2^30, and returned 0 from about 5.7 billion (2^32.4). For more than a few billion
distinct items use `HyperLogLogPlus` or `UltraLogLog`, which use 64 bits.

### `HyperLogLogPlus`

Registers 2^p with `p` at most 18, in a sparse form while small. Counts are 64-bit. The
largest is about half a megabyte, so every one reads back by default.

### `UltraLogLog`

Registers 2^p with `p` at most 26, one byte each, and a 64-bit hash. Every precision reads
back by default: a read is charged three times the registers.

### `SetSketch`

`m` two-byte registers, which a read is charged 12 bytes a register for (the registers and
the two arrays that order their updates, and the payload they came in).

A large sketch whose register count is not a power of two estimates cardinality slightly
low. An element's first value goes to a register drawn with a 32-bit multiply-and-shift, which
gives each of `m` registers `floor(2^32 / m)` of the 2^32 values or one more, so for
`r = 2^32 mod m` the registers take first values at two rates. Once a sketch holds many
more elements than registers, the estimate falls short by about `r · (m − r) / 2^64`, at
most about `(m / 2^33)²`, which is nothing at a power of two. That is under 2% of the
estimate's standard error, `1 / √m`, up to 2^24 registers, as large as that error from
about 88.5 million, and at its largest 4% of the estimate, at 0.4 · 2^32 registers, about
1.72 billion. These are worked out from the two rates. A scaled-down simulation of the
draw reproduced them, found the shortfall smaller in a sketch holding fewer elements, and
found no change in Jaccard similarity. Past about 2^24 registers, choose a power of two.
The draw is not changed, because it decides which registers the elements of a stored
sketch went to, and a sketch read back has to go on agreeing with one built now.

### `CountMinSketch`

Counters are 64-bit, so a row holds counts to 18 quintillion and the sketch takes any
number of items. What grows with them is the error: an estimate is at most `ε · N` over
the truth for `N` items added, so a sketch for a stream you expect to reach `N` wants `ε`
about the error you can accept divided by `N`. Width is `⌈e/ε⌉` and depth `⌈ln(1/δ)⌉`,
5 at δ = 1%. A read charges 16 bytes a counter.

A row is one array, so the widest is 2,147,483,591, an `ε` of about 1.27e-9, and the
constructor refuses a finer one with an `ArgumentOutOfRangeException` naming `epsilon`. The
depth is at most 710, for a `δ` of about 5.6e-309, whose reciprocal is the largest a
double holds: a smaller `δ` is refused, naming `delta`. A payload that claims more of
either is refused as beyond anything built.

### `CountSketch`

The same, with width `⌈1/ε²⌉`, so a rate ten times finer costs a hundred times the
width, and counters of 8 bytes that are signed. A read charges 24 bytes a counter. The
widest row is the same 2,147,483,591, an `ε` of about 2.16e-5, and the depth is limited
the same way.

### `PrivateCountMinSketch`

You choose the width and depth directly, and the privacy budget `ρ` sets the noise and
not the size. A read charges 16 bytes a counter. A row is one array and the rows are one
array, so each is at most 2,147,483,591; a constructor given more refuses it, naming
`width` or `depth`, and so does the reader.

### `SublimeCountMinSketch`

The width doubles as items are added: from `w` to `2w` when the count reaches `(w · s)^(1/g)`
for the growth exponent `g` and size factor `s`, so a larger `g` grows faster. At the default
`g` of one half and `s` of one the width is about the square root of the items: a width of
2^14 from 67,108,864 items and 2^15 from 268,435,456. At `g` of 0.9 a sketch reaches a
width of 2^22 at 10,568,984 items. The width is a signed 32-bit integer, so it cannot pass
2^30, and an `Add` that would double it past that throws, with the sketch as it was.

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

Two limits sit beneath that, and `Add` refuses a value that passes either, with an
`ArgumentOutOfRangeException` and the sketch unchanged. A bucket is numbered by a 32-bit
integer, so a value has a bucket only if `|ln v| < 2^31 · ln((1+α)/(1−α))`: every double
does from an accuracy of about 1.74e-7, and below that the range narrows, to 0.014 – 73 at
1e-9 and 0.65 – 1.54 at 1e-10. And the buckets of one sign are one array, so the span from
the lowest bucket in use to the highest is at most 2,147,483,591, which is 16 GiB of
counts. The constructor refuses an accuracy below about 5.6e-17, where
`(1+α)/(1−α)` rounds to exactly 1 and no value has a bucket of its own.

### `DpswSketch`

Takes any number of items, and what it holds is a window of the most recent `window`
of them, as a set of sketches whose number grows slowly with the window. The table gives the
window it reads back at with the default width and depth, which is a measurement.

A substream holds `window^β` items (β of 0.7 unless you say otherwise) and the checkpoints of
one are a list with a slot for each, so `window^β` is at most 2,147,483,591, the longest
list. The constructor refuses a window and β that make more, naming `window`. At the
default β that is a window of 21,445,114,182,289, which is the table's figure for what is
built; memory is what stops a real one long before.

The sketches of a window are at most as wide and as deep as a `PrivateCountMinSketch` is, 2,147,483,591
each: the constructor refuses a width or a depth past that, naming it, before anything is built, and
so does the reader.
