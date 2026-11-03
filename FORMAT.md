# Persistence format

This document specifies the byte layout that `WriteTo` produces and `ReadFrom` accepts.

The format is **stable**. A payload written by any version of this library is readable
by every later version, or is refused with an explanation. It is never guessed at. The
test suite reads payloads checked in as fixtures at the version that introduced each
format version, so a change that would break stored data fails in CI rather than in
somebody's storage.

All multi-byte integers are **little-endian**, on every platform. Floating point values
are IEEE 754 binary64 in the same byte order.

## Envelope

Every structure is written inside the same envelope.

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | Magic, the ASCII bytes `PDS\0` |
| 4 | 2 | Format version (see below) |
| 6 | 2 | Structure id |
| 8 | 2 | Hash id |
| 10 | 4 | Payload length in bytes |
| 14 | *n* | Payload |
| 14 + *n* | 4 | CRC-32 of bytes 4 through 14 + *n* |

The checksum covers the header as well as the payload, so a corrupted length or
structure id is caught by the same check rather than being acted on first. The magic is
outside it: a payload whose magic is wrong is not this format at all, and there is
nothing to checksum.

### Format versions

The version travels in each payload rather than being a property of the library, so a
structure whose layout changes bumps only its own.

| Version | First written by | Structures | Added |
| --- | --- | --- | --- |
| 1 | 3.1.0 | every structure | the layouts below, without the fields marked as later |
| 2 | 6.0.0 | `StableBloomFilter`, `CuckooBloomFilter` | the random generator's state |
| 3 | 6.1.0 | `CuckooBloomFilter` | fingerprints packed at their exact bit width |
| 4 | 6.2.0 | `SetSketch` | the construction, written only for the second one |
| 5 | the release after 6.2.0 | `StableBloomFilter` | the state the random generator began in |
| 6 | the release after 6.2.0 | `CuckooBloomFilter` | the state the random generator began in |
| 7 | the release after 6.2.0 | `ScalableBloomFilter` | the growth factor, written only by a filter that grows |
| 8 | the release after 6.2.0 | `BloomFilter`, `CountingBloomFilter`, `DeletableBloomFilter`, `PartitionedBloomFilter`, `StableBloomFilter` | a placement byte, and the item count as a `u64`; written by every save |
| 9 | the release after 6.2.0 | `InverseBloomFilter`, `CountMinSketch`, `HeavyKeeper`, `PrivateCountMinSketch`, `DpswSketch` | a placement byte, in each private sketch body for `DpswSketch`; written by every save |

The table is also the list of what each structure reads. A version belongs to the
structures named against it, and a structure's reader reads those versions and no
others. A `BloomFilter` payload labelled version 2 is refused although version 2
exists, because the Bloom filter has never written it: the header does not describe a
payload this format produced, and reading it anyway would be guessing which layout it
meant. The refusal names the structure, the version it was handed and the versions that
structure reads. A number is given to one change and not reused for another, so a new
version adds one row here and changes what only the structures named on it read.

A structure writes the version its own layout needs, and the rest go on writing the
version they always did. Version 2 exists because the stable and cuckoo filters
gained a stored random-generator state in 6.0.0. Raising every structure's version
together would have made every payload unreadable to 5.x in order to record a change
that most of them did not have.

A version 1 payload of either filter still reads. It carries no generator state, so the
restored filter resumes with an unpredictable one: its cells or fingerprints are exactly
right, and only the sequence of choices it will make next is unrecoverable, because it
was never written down.

Version 5 exists because `StableBloomFilter.Reset` returns the filter to its original
state, and the generator is part of that. A version 2 payload holds where the generator
had got to, which cannot say where it started, so version 5 adds a second `u64` holding
where it began. A reader older than version 5 refuses such a payload as written by a
later version: the compatibility this format promises runs backwards, not forwards.

A version 1 or 2 payload of the stable filter still reads, and it has no original state
to give. Such a filter takes the generator position it was read at as its original
state, so a `Reset` returns it there: emptied, with the generator where the payload put
it. For version 1 that position is the unpredictable one the filter was given on
reading. Writing the filter out records that same point, not the position it has reached
since, so a save and a load never moves where a `Reset` goes. What the payload cannot
say is the seed the filter was built with, and nothing is guessed at to replace it.

Version 6 is the same change for `CuckooBloomFilter`, for the same reason: its `Reset`
returns the filter to its original state, the generator included. Only the cuckoo
filter's version moves, to the next number nothing else had used, and a reader older than
version 6 refuses such a payload as written by a later version. A version 1, 2 or 3
payload of the cuckoo filter still reads, and its filter takes the generator position it
was read at as its original state, exactly as the stable filter's does; the details are
in [The random generator's state](#the-random-generators-state).

Version 7 exists because a `ScalableBloomFilter` can grow its contained filters: the
i-th is built for hint × sⁱ items rather than for the hint every time. Each nested filter
already records its own size, so filters of mixed sizes read back at any version; what a
payload without the factor cannot say is how large the *next* filter should be. Only a
filter that grows writes version 7. One with a growth factor of 1 — every filter built
for the hint, as every scalable filter was before the factor existed — still writes
version 1, so libraries that predate the choice still read it. A version 1 payload is a
filter with a growth factor of 1 by definition, and goes on adding filters for its hint
after it is read; a reader older than version 7 refuses a version 7 payload as written by
a later version.

Version 8 records two things the five structures named on it could not say before: how
their bits were placed, and an item count past 2^32. Each writes, at version 8, the layout
it wrote before -- version 5's for the stable filter, version 1's for the rest -- with a
placement byte after the fields that size the filter, and its item count, where it has
one, widened from a `u32` to a `u64`. Every save of these five is version 8, whatever
its size or placement, so a library older than version 8 reads none of them; a payload
written before version 8 still reads, and saves again as version 8. One number covers
all five, because it names one change.

The placement byte is `0` for 32-bit values and `1` for 64-bit ones, as
[Bit placement](#bit-placement) describes, and a reader refuses any other value, naming
it, rather than guess which scheme a later release meant. It is a byte of its own because
nothing else can carry it. The version cannot, once every save is the same version, and the
size cannot either: a new filter is placed with 64-bit values when the number of
positions it reduces modulo is 2^27 or more, but a payload written before version 8 was
placed with 32-bit values whatever its size, and saved again it must go on saying so.
It sits with the fields that size the filter, before anything that depends on it, and
before the count, which is bookkeeping. A count is 64 bits because a 32-bit one wrapped
to zero after 2^32 adds, and one held at its maximum instead would under-report once a
removal took it down again.

A payload before version 8 has no placement byte and was placed with 32-bit values, and
reads back that way, so a large filter written by an earlier release is the filter it
was, including the false positive rate it had. Two filters of the same size placed
differently set different bits for the same item, so the filters that merge refuse to
merge them. A `ScalableBloomFilter` holds partitioned filters, each in its own
envelope, so its contained filters are written at version 8 inside a scalable filter at
version 1 or 7.

Version 9 is the same change for the structures that reduce a hash to one column of a
row, or one slot: `CountMinSketch`, `PrivateCountMinSketch`, `HeavyKeeper` and
`InverseBloomFilter`. They had placed with 32-bit values at every width, as the Bloom
filters had, and from the same width of 2^27 a new one places with 64-bit values, as
[Bit placement](#bit-placement) describes. Each writes, at version 9, the layout it wrote
at version 1 with a placement byte after the fields that size it, and nothing else
changes. The byte means what version 8's does, and a reader refuses any other value in it
the same way. It is a number of its own because it is a change of its own, made later
and to other structures: a library that reads version 8 and not 9 refuses these payloads
as written by a later version, which is what they are.

A `DpswSketch` holds private sketches inline, each as the `PrivateCountMinSketch` body
below, so its layout changes with that body: at version 9 every body carries its own
placement byte, and the window's own fields are as they were. Each sketch's byte is read
for that sketch, so a window can hold sketches placed either way: one read from a payload
written before version 9 keeps its sketches as they were, and the substreams it begins
afterwards are new sketches, placed as their width calls for. A `TopK` holds its
`CountMinSketch` in an envelope of its own, so the sketch carries its own version and
byte and the top-k's layout is unchanged: its sketch is written at version 9 inside a
top-k at version 1.

A payload before version 9 has no placement byte and was placed with 32-bit values,
whatever its width, and reads back that way, and saves again as version 9. Two
`CountMinSketch`es of the same width placed differently count the same item in different
columns, so they refuse to merge, and so do two `TopK`s holding them.

A reader refuses a payload whose magic does not match, whose version is not one its
structure has defined, whose structure id is not the type being read, or whose
checksum does not match. The versions a structure has not defined include `0`, which is
reserved and never written, and anything above the highest in the table, which a later
release wrote. It also refuses one that does not consume exactly its stated
length, which catches a reader and writer that disagree about a layout while the
checksum still matches.

### Structure ids

| Id | Structure |
| --- | --- |
| 1 | `BloomFilter` |
| 2 | `BloomFilter64` |
| 3 | `CountingBloomFilter` |
| 4 | `DeletableBloomFilter` |
| 5 | `PartitionedBloomFilter` |
| 6 | `ScalableBloomFilter` |
| 7 | `StableBloomFilter` |
| 8 | `InverseBloomFilter` |
| 9 | `CuckooBloomFilter` |
| 10 | `CountMinSketch` |
| 11 | `HyperLogLog` |
| 12 | `TopK` |
| 13 | `MinHashSignature` |
| 14 | `BinaryFuseFilter` |
| 15 | `DDSketch` |
| 16 | `HyperLogLogPlus` |
| 17 | `QuotientFilter` |
| 18 | `ThetaSketch` |
| 19 | `SimHashSignature` |
| 20 | `CountSketch` |
| 21 | `InvertibleBloomLookupTable` |
| 22 | `BloomierFilter` |
| 23 | `HeavyKeeper` |
| 24 | `VarOpt` |
| 25 | `UltraLogLog` |
| 26 | `Grafite` |
| 27 | `InfiniFilter` |
| 28 | `MementoFilter` |
| 29 | `SublimeCountMinSketch` |
| 30 | `SetSketch` |
| 31 | `TupleSketch` |
| 32 | `PrivateCountMinSketch` |
| 33 | `DpswSketch` |

Ids are assigned once and never reused, including for structures that no longer exist.

### Hash ids

| Id | Hash |
| --- | --- |
| 0 | A function supplied through `SetHash`, which cannot be recorded |
| 1 | 64-bit XxHash3, the default since 3.0.0 |
| 2 | None: the structure holds numbers rather than bytes and does not hash |

A structure's answers depend entirely on its hash function, and a delegate cannot be
written to a file. Recording which one was in use is what stops a reader installing a
different one and returning confident nonsense — a filter read back under the wrong hash
does not look broken, it looks empty.

The identifier names the **algorithm**, not "the default", because the default is not
fixed for all time: this library's was MD5 until 3.0.0. A reader that does not recognise
an id refuses the payload rather than substituting whatever its own default happens to
be. A caller who knows better can supply the hash explicitly and read it anyway.

Reading a payload written with hash id `0` requires the caller to supply the same
function through the `ReadFrom` overload that takes one.

## Payload primitives

| Notation | Encoding |
| --- | --- |
| `u8` | 1 byte |
| `u32` | 4 bytes, little-endian |
| `u64` | 8 bytes, little-endian |
| `f64` | 8 bytes, IEEE 754 binary64, little-endian |
| `bytes` | `u32` length, then that many bytes |

`Buckets`, the packed bit array most filters are built on, is written as:

```
u32     bucket count
u8      bits per bucket
bytes   packed data
```

The data length is checked against what the count and bucket width imply: the count
times the width, in bits, rounded up to whole bytes. A reader works that out in 64 bits.
Releases up to and including 6.2.0 worked it out in 32, so a table of 2^32 bits or more
was written with a length that had wrapped; [Sizes](#sizes) says what is done with those.

`Buckets64`, its 64-bit counterpart, holds several arrays because one filter can need
more bytes than a single array holds:

```
u64     bucket count
u8      bits per bucket
u32     array count
bytes   packed data, per array
```

### Nested structures

A structure held by another is written as a `bytes` run holding its **own complete
envelope** — magic, version, structure id, hash id, payload and checksum — rather than
being flattened into the outer payload.

That costs eighteen bytes per nested structure, against payloads measured in tens of
thousands, and buys three things. The inner structure names its own hash, so a composite
cannot silently disagree with itself about hashing. The outer envelope names one too,
the hash of the first structure it holds, and a reader checks both. It can be pulled out of the outer
payload and read on its own. And the outer structure can change what it holds without
the inner layout being part of that change.

Things that are not structures in their own right — a `Buckets`, a top-k's heap — are
inlined, because there is nothing to read them as.

### Sizes

What a constructor builds, its reader reads. Each size a payload records has one limit,
and the structure's constructor and its reader both refuse past it — so no payload this
library wrote is ever refused for its size. Readers once refused sizes above a fixed
ceiling, most of them a million, as "beyond anything this library builds", while the
constructors built more. Those payloads, from any version, now read.

A limit is where the structure itself stops working, not a round number: the most
elements a .NET array holds, 2,147,483,591, or what the structure's own arithmetic can
address.

| Structure | Size | Largest | Why |
| --- | --- | --- | --- |
| `CuckooBloomFilter` | entries, `m` × `b` | 2,147,483,648 | entries are numbered with a `u32`, and the constructor's tables are a power of two; their fingerprints must also fit one array of 64-bit words |
| `ThetaSketch` | `k` | 1,073,741,795 | the sketch holds up to `2k` values in one array before it trims |
| `TupleSketch` | `k` | 1,073,741,795 | the same, for its keys |
| `MinHashSignature` | `k` | 2,147,483,591 | the values are one array |
| `TopK` | `k` | 2,147,483,587 | the heap's index is a dictionary, whose largest table is the largest prime an array holds |
| `HeavyKeeper` | `k` | 2,147,483,587 | as `TopK` |
| `HeavyKeeper` | width | 2,147,483,591 | each array of buckets is one array |
| `HeavyKeeper` | depth | 2,147,483,591 | the arrays are held in one |
| `CountMinSketch` | width | 2,147,483,591 | each row is one array |
| `CountMinSketch` | depth | 710 | `ceil(ln(1 / delta))` at the smallest delta whose reciprocal is a finite double |
| `CountSketch` | width | 2,147,483,591 | each row is one array |
| `CountSketch` | depth | 710 | `ceil(ln(1 / delta))` at the smallest delta whose reciprocal is a finite double |
| `PrivateCountMinSketch` | width | 2,147,483,591 | each row is one array |
| `PrivateCountMinSketch` | depth | 2,147,483,591 | the rows are held in one array |
| `DpswSketch` | items in a substream | 2,147,483,591 | the longest array: set when the checkpoints of a substream were a list with a slot for each item, and kept now that they are found without one |
| `DpswSketch` | width | 2,147,483,591 | the sketches it holds are `PrivateCountMinSketch`es, whose rows are each one array |
| `DpswSketch` | depth | 2,147,483,591 | as `width`: the rows of one of those sketches are held in one array |
| `InverseBloomFilter` | capacity | 2,147,483,591 | the slots are one array of references |
| `VarOpt` | `k` | 2,147,483,590 | each region is an array of `k + 1` slots |
| `Grafite` | reduced universe `r` | 2,305,843,009,213,693,950 | the hash works modulo 2^61 − 1, and the constructor refuses a request that needs more; every release capped it here, so a payload written for a request that is now refused carries exactly this and reads |
| `Grafite` | words of low bits | 2,147,483,591 | they are one array |
| `Grafite` | words of high bits | 33,554,432 | enough for 2,147,483,647 bits, the most an `int` position addresses |
| `SetSketch` | `m` | 2,147,483,591 | the registers are one array |
| `SublimeCountMinSketch` | rows | 710 | `ceil(ln(1 / delta))` at the smallest delta whose reciprocal is a finite double |
| `SublimeCountMinSketch` | counters in a row | 1,073,741,824 | a width is a power of two held in an `int`, and 2^30 is the largest of those; the sketch stops widening there |
| `ScalableBloomFilter` | filter count | 2,147,483,591 | the filters are a list; a filter stops growing sooner, when its next filter would have no finite rate to be sized from, or would be built for more items or bits than a contained filter addresses, which is a limit on growing and not on reading |
| `CountingBloomFilter` | bytes of counters, `m` × `b` bits, rounded up | 2,147,483,591 | the counters are one array; from four-bit counters up that is fewer counters than a `u32` counts |
| `StableBloomFilter` | bytes of cells, `m` × `d` bits, rounded up | 2,147,483,591 | the cells are one array, as the counters are |
| `BloomFilter` | hash functions, `k` | 1,024 | `ceil(log2(1 / rate))` at the smallest false positive rate whose reciprocal is a finite double; the constructor refuses a smaller rate. A reader also refuses none, which sets no bit and tests none |
| `BloomFilter64` | hash functions, `k` | 1,024 | as `BloomFilter` |
| `CountingBloomFilter` | hash functions, `k` | 1,024 | as `BloomFilter`; a scratch buffer of four bytes per hash function is allocated for it |
| `DeletableBloomFilter` | hash functions, `k` | 1,024 | as `CountingBloomFilter` |
| `PartitionedBloomFilter` | hash functions, `k` | 1,024 | as `BloomFilter`; it is also the number of partitions, and the reader checks it first |
| `StableBloomFilter` | hash functions, `k` | 1,024 | as `CountingBloomFilter`; the constructor halves the count, and the factory that builds a plain Bloom filter of cells does not, so the reader accepts the whole of it |
| `InfiniFilter` | quotient bits | 31 | the slot count is a `u32` holding 2^q; a filter stops expanding there |
| `MementoFilter` | quotient bits | 31 | as `InfiniFilter` |
| `MementoFilter` | bits of a slot | 64 | a slot is held in a 64-bit word: three bits of metadata, and a field of the fingerprint, one bit for its age counter and the memento |
| `QuotientFilter` | quotient bits | 31 | the slot count is a `u32` holding 2^q; the reader also reads 32, which earlier constructors wrote as a wrapped one-slot table |
| `QuotientFilter` | quotient + remainder bits | 64 | a fingerprint is cut from a 64-bit hash |
| `QuotientFilter` | remainder bits | 61 | a slot is its remainder and three bits of bookkeeping in one 64-bit word; the reader also reads the wider remainders earlier constructors wrote |

The other filters built on `Buckets` hold one-bit buckets, so the most they hold is what
a `u32` counts, 4,294,967,295, which is 536,870,912 bytes, and their sizing refuses
anything beyond it. The same arithmetic limits the counting and stable filters from four
bits up: at 4 bits it is 4,294,967,182 buckets, and at 8 bits 2,147,483,591.

A `DeletableBloomFilter` keeps its data region and its collision regions out of one `m`
that sizing holds to that same 4,294,967,295, so the two come to no more than that
between them, and a reader refuses a payload whose regions do. Without the refusal the
region size, worked out in 32 bits, wraps to zero past 2^32, and the first add that finds
a bit already set divides by it.

A bucket array of 2^32 bits or more, 512 MiB, was written by 6.2.0 and every release before
it with a byte count that had wrapped in 32 bits: a Bloom filter of 4,294,967,294 buckets
was stored with no bytes, and a counting filter of 1,127,428,565 four-bit counters with
26,843,371 of the 563,714,283 they need. Nothing of the bucket array can be recovered from
such a payload, and the filter that wrote it could not use it either, since an add that
landed past the end of the table threw after setting the buckets before it. A reader
therefore refuses a bucket array whose stored length is exactly the wrapped one, and says
so; it does not read the short table as the filter it was meant to be. Any other length that
disagrees with the count is refused as corrupt. Sizes that did not wrap are unchanged, and
read as they always did.

A quotient filter's table can be larger than a payload holds: 2^31 slots at five bits of
remainder, or 2^30 at thirteen, is 268,435,458 words, and a payload carries at most
268,435,444. Such a filter can be built and used and not written, and `WriteTo` says so
with the `InvalidOperationException` of the envelope's ceiling; releases up to 6.2.0 failed
with an `OverflowException` from computing the table's length in 32 bits.

A size below its limit can still be refused for not being in the payload. Where a reader
allocates for a count, it first checks that the bytes left could hold that many, so what
it allocates follows what the payload actually holds rather than what it claims. A sketch
claiming a large `k` and holding a few values costs a few values.

Some cannot size by the payload, and allocate what a size says: a cuckoo filter writes
only its occupied entries but must hold its whole table, a `VarOpt` sample holds its
regions whole because its arithmetic assumes their size, and a quotient filter's header
names its table outright. A payload can ask those for as large a structure as the
constructor would build. What stops a small one asking is the read budget, below.

The envelope bounds everything else. A payload is written as one array together with the
envelope's eighteen bytes, so it is at most 2,147,483,573 bytes: the largest array,
2,147,483,591, less those. A structure whose payload would be larger can be built but not
written, and `WriteTo` throws an `InvalidOperationException` saying how large the payload
would have been; the structure is untouched. A reader refuses a length field above
2,147,483,591, what one array holds, before allocating for it.

### Read budget

Every read has a budget: the most memory, in bytes, reading one payload may allocate — the
payload itself, the copies made of runs within it, the structure, and any structure nested
inside it, all from one allowance. A reader prices the whole read before allocating
anything a payload claims: from the envelope's length and the fields that size the
structure, read in the order the structure's reader reads them, and for a nested structure
from its own envelope and fields where they lie inside the outer payload. It refuses the
payload when that total is more than the budget. The refusal is an `InvalidDataException`
whose inner exception is a `ReadBudgetExceededException` naming the total and the budget;
the total is the same whatever the budget was, and a budget of it reads the payload.

A payload that fits the budget is read and its checksum checked before it is priced, so a
corrupted one is refused as corrupted. One whose length alone is more than the budget is
never held: its fields are read from the stream and its runs skipped, to name the total,
before its checksum could be checked. A payload that ends early, or whose fields are not
ones its reader accepts, is priced as far as it goes.

The hash id is checked before anything is priced: after the checksum when the payload is
held, and as the header states it when it is not. A payload naming a hash its reader
cannot read it with — one the caller has not supplied and the reader cannot reconstruct,
or any hash at all for a structure that hashes nothing — is refused for that under any
budget.

The total is worked out from the payload's shape, its header and the fields that size what
it holds, so a budget of it reads a payload that is well formed. One damaged past those
fields, or wrong in what it holds, names the total too, and is refused for what is wrong
with it under a budget that covers it.

The default is 1,073,741,824 bytes, one gibibyte (`ReadOptions.DefaultMemoryBudget`).
`ReadFrom` without options reads under it; the overloads taking `ReadOptions` read under
any other, or under none. Since a read holds the payload while it builds the structure, it
costs roughly the structure plus its payload.

The limits in [Sizes](#sizes) are what can be read at all; the budget is what will be read
without asking. Most limits there are far past the default — a structure at one is
gigabytes, and loads only with a budget raised to match. What each read is charged:

| Structure | Charged beyond the payload and the runs it copies |
| --- | --- |
| `BloomFilter`, `BloomFilter64`, `HyperLogLog`, `HyperLogLogPlus` (dense), `UltraLogLog`, `BinaryFuseFilter`, `BloomierFilter`, `SimHashSignature` | the structure's own copy of its registers or bits, where it keeps one |
| `CountingBloomFilter`, `DeletableBloomFilter`, `StableBloomFilter` | a scratch buffer of four bytes per hash function |
| `PartitionedBloomFilter`, `ScalableBloomFilter` | a slot per partition or contained filter; a contained filter is charged as a read of its own |
| `CuckooBloomFilter` | the whole table, `m` × `b` fingerprints of `f` bits, and a bit per entry, whatever the payload holds |
| `QuotientFilter` | the whole table, 2^`q` slots of `r` + 3 bits |
| `VarOpt` | five regions of `k` + 1 slots, eight bytes each |
| `InverseBloomFilter` | a slot of eight bytes per unit of capacity |
| `CountMinSketch`, `CountSketch`, `PrivateCountMinSketch`, `SublimeCountMinSketch` | every row, before it is read; Sublime also its packed rows |
| `TopK`, `HeavyKeeper` | a heap sized by the elements held, not by `k`; HeavyKeeper also its bucket arrays |
| `ThetaSketch`, `TupleSketch`, `MinHashSignature`, `Grafite`, `SetSketch`, `InvertibleBloomLookupTable`, `DDSketch`, `HyperLogLogPlus` (sparse) | arrays sized by the values the payload carries |
| `InfiniFilter`, `MementoFilter` | each table twice: as read, and as the segment built from it |
| `DpswSketch` | its checkpoints, rebuilt from the substream size, and a list of segments per substream; each segment's sketch as above |

## Payloads

### `BloomFilter` (id 1)

At version 8:

```
u32     m, the filter's capacity in bits
u32     k, the number of hash functions
u8      placement, 0 for 32-bit values and 1 for 64-bit ones
u64     count, the number of items added
Buckets the bit array
```

At version 1 there is no placement byte, the bits were placed with 32-bit values, and
the count is a `u32`. The bucket count must equal `m`. Which item a bit stands for is
described in [Bit placement](#bit-placement).

### `CountMinSketch` (id 10)

```
f64     epsilon
f64     delta
u32     width
u32     depth
u8      placement, 0 for 32-bit values and 1 for 64-bit ones   (version 9 only)
u64     total count
u64     depth * width cells, row by row
```

Before version 9 there is no placement byte, and the counts were placed with 32-bit
values. Which column of row `i` an item is counted in is described in
[Bit placement](#bit-placement), with `m` the width.

The stored `width` and `depth` are authoritative, not `epsilon` and `delta`. They are
what the sketch indexes by, and recomputing them from the parameters would silently
relocate every cell if the sizing were ever adjusted. `epsilon` and `delta` are stored
because `Epsilon()` and `Delta()` report them.

### `BloomFilter64` (id 2)

```
u64       m, the filter's capacity in bits
u32       k, the number of hash functions
u64       count, the number of items added
Buckets64 the bit array
```

### `CountingBloomFilter` (id 3)

At version 8:

```
u32     m
u32     k
u8      placement, 0 for 32-bit values and 1 for 64-bit ones
u64     count, the number of items added and not removed
Buckets the counter array, whose bucket width is the counter width
```

At version 1 there is no placement byte, the counters were placed with 32-bit values,
and the count is a `u32`. Which items a counter counts is described in
[Bit placement](#bit-placement).

### `DeletableBloomFilter` (id 4)

At version 8:

```
u32     m, the data region in bits
u32     k
u8      placement, 0 for 32-bit values and 1 for 64-bit ones
u64     count, the number of items added and not removed
Buckets the data region
Buckets the collision regions, one bucket per region
```

At version 1 there is no placement byte, the bits were placed with 32-bit values, and
the count is a `u32`. The region size is **not** stored. See
[What is not stored](#what-is-not-stored). Which item a bit of the data region stands for
is described in [Bit placement](#bit-placement), with `m` the data region.

### `PartitionedBloomFilter` (id 5)

At version 8:

```
u32     m, the total capacity in bits
u32     k
u32     s, the size of each partition in bits
u8      placement, 0 for 32-bit values and 1 for 64-bit ones
u64     count
u32     partition count
Buckets one per partition
```

There is one partition per hash function, and a payload whose partition count differs
from `k` is refused. Each partition must hold `s` buckets. Which item a bit stands for
is described in [Bit placement](#bit-placement), with `m` the partition size `s`. At
version 1 there is no placement byte, the bits were placed with 32-bit values, and the
count is a `u32`.

### `ScalableBloomFilter` (id 6)

```
f64     r, the tightening ratio
f64     fp, the target false positive rate
f64     p, the fill ratio at which a new filter is added
u32     hint, the items the first contained filter is built for
u32     s, the growth factor, at format version 7 only
u32     filter count
nested  PartitionedBloomFilter, one per contained filter, in order
```

Order matters: each contained filter targets a rate tightened from the last, and the
final one is the one additions go to.

Filter i is built for hint × sⁱ items at fp × rⁱ, and s is 1 in a payload without it.
A reader takes each filter's size from its nested payload and does not recompute it:
the sizing goes through floating point that is not bit-identical across platforms, and
the filters are what they were built as. The factor is stored for the filters still to
come. A reader at version 7 rejects a growth factor below 2, since a filter growing by 1
writes version 1, and every reader rejects a payload holding no filters.

### `StableBloomFilter` (id 7)

```
u32     m, the number of cells
u32     k
u32     p, the number of cells decremented per add
u8      placement, 0 for 32-bit values and 1 for 64-bit ones   (version 8 only)
Buckets the cells
u64     the random generator's state    (version 2 onwards)
u64     the state it began in           (version 5 onwards)
```

The maximum cell value follows from the cell width the cells carry. The stable filter has
no item count, so its version 8 is version 5's layout with the placement byte and nothing
else; before version 8 the cells were placed with 32-bit values, as
[Bit placement](#bit-placement) describes.

The state the generator began in is the last field, and payloads before version 5 simply
end before it; version 1 payloads end a field earlier still, before the generator's
state. What a reader does for a field the payload lacks is described in
[The random generator's state](#the-random-generators-state).

### `InverseBloomFilter` (id 8)

```
u32     capacity
u8      placement, 0 for 32-bit values and 1 for 64-bit ones   (version 9 only)
u32     occupied slot count
        per occupied slot:
u32       slot index
bytes     the stored data
```

Only occupied slots are written. This filter stores the data rather than hashing it, so
a full-length run of empty slots would be most of a large, mostly idle filter's payload.
A slot index at or beyond the capacity is refused.

Each entry carries its slot, so the placement does not say where the reader puts what it
holds; it says where the filter looks for an item afterwards. An item's slot is described
in [Bit placement](#bit-placement): position 0, with `m` the capacity. Before version 9
there is no placement byte, and the slots were chosen with 32-bit values.

### `CuckooBloomFilter` (id 9)

```
u32     m, the number of buckets
u32     b, entries per bucket
u32     f, the fingerprint size         (in bytes before version 3, in bits from version 3)
u32     count, the number of items held
u32     n, the capacity in items
u32     occupied entry count
        per occupied entry:
u32       bucket index
u32       entry index
          the fingerprint               (encoded as described below)
u64     the random generator's state    (version 2 onwards)
u64     the state it began in           (version 6 onwards)
```

Occupied entries only, for the same reason as above: a filter sized for a load it has
not reached is mostly empty slots. The writer lists them in ascending order of bucket and
then entry, and a reader places each by the two indexes it carries rather than by its
position in the list. Which entries are occupied is the list itself, with no bitmap
beside it. A bucket index at or beyond `m` or an entry index at or beyond `b` is
refused, as is a payload claiming more occupied entries than `m` buckets of `b` hold. A
reader still allocates the whole table, so it cannot size by what the payload holds; it
refuses a table past the constructor's limit instead (see [Sizes](#sizes)).

`m` must be a power of two, as the constructor always makes it: an item's other bucket
is found by reducing a hash by `m`, and finds the first again from the other end only
for a power of two. `count` must equal the number of entries the table holds, since
each item is one entry, and a bucket and entry listed twice is refused, so the count is
compared with the places filled and not with the length of the list.

How a fingerprint is written, and what `f` counts, depends on the version:

| Version | `f` counts | One fingerprint |
| --- | --- | --- |
| 1, 2 | bytes, from 1 to 8 | `bytes`: a `u32` length that must equal `f`, then that many bytes, least significant first |
| 3 onwards | bits, from 1 to 64 | the fewest whole bytes that hold `f` bits, which is `f` divided by 8 and rounded up, least significant first, with **no length prefix** |

A fingerprint read from a version 1 or 2 payload is widened to `8 * f` bits, and the
filter that comes back is the one that was written: the value is the same low bits of
the same digest, and the bytes hashed to find its partner bucket are the same ones. From
version 3 the filter holds each fingerprint at exactly `f` bits, so a width that is not a
multiple of eight leaves the high bits of the last byte zero, and a fingerprint with a
bit set at or above `f` is refused, since the writer cannot have produced it. A payload
whose width, in bits, is not between 1 and 64 is refused, as is one with no buckets, no
entries per bucket, a bucket count that is not a power of two, or a bucket count beyond
what the reader will allocate for.

The generator's state follows the entries, and version 1 payloads simply end before it.
The state it began in is the last field, and payloads before version 6 simply end before
it. What a reader does for a field the payload lacks is described in
[The random generator's state](#the-random-generators-state).

### `HyperLogLog` (id 11)

```
u32     m, the number of registers
bytes   the registers, one byte each
```

`m` must be a power of two, and the register count must match it. `b` and `alpha` are
**not** stored. See [What is not stored](#what-is-not-stored).

### `TopK` (id 12)

```
u32     k, the number of elements tracked
u32     n, the number of items added, at most 4,294,967,295
nested  CountMinSketch
u32     element count
        per element:
bytes     the element's data
u64       its recorded frequency
```

The nested sketch is a complete `CountMinSketch` envelope, at whichever version it was
written: version 1 from releases before the placement byte, version 9 since. The top-k's
own layout is the same at either, and is version 1.

The number of items added is the nested sketch's count, which is a `u64`. `n` is that
number held to what a `u32` holds: a structure that has seen more than 4,294,967,295 items
writes 4,294,967,295, where releases up to 6.2.0 wrote the number modulo 2^32, which is
zero at exactly that many. A reader takes the number from the sketch and does not use `n`.

The elements are re-heaped on read rather than being trusted to arrive in heap order.
They are the same elements either way and `Elements()` sorts them, so nothing about the
answer depends on the order they were stored in — but a payload that has been edited
cannot leave the heap in a state where the root is not the minimum.

### `MinHashSignature` (id 13)

```
u32     the number of hash functions, k
u64     k minimum values, one per hash function
```

A signature's hash functions are **not** a caller's to choose. They are XxHash3 seeded
with `0` through `k-1`, which is a fixed convention rather than stored state: it is what
lets a signature be compared against one computed by another process or another version.
A payload naming any other hash is refused rather than read, because comparing signatures
built with different functions produces a number that means nothing.

Changing those functions would silently invalidate every stored signature, so they are
fixed in the same sense the rest of this format is, and the test suite pins the values a
known bag produces.

### `BinaryFuseFilter` (id 14)

```
u32     the number of distinct keys the filter was built from
u32     segment length, a power of two
u32     segment count
u64     the seed the peel succeeded on
u8      fingerprint width in bits, 8 or 16
bytes   the fingerprints, width/8 bytes each
```

The segment length mask, the segment count length and the array length are all derived
from the two lengths above rather than stored, so a payload cannot disagree with itself
about its own geometry. A segment length that is not a power of two is refused: it is
used as a mask, and one that is not a power of two would put keys where a lookup does
not go.

The seed matters as much as the fingerprints. It is not a tuning parameter — it is the
seed the construction's peel happened to succeed on, and every position in the filter is
computed from it. A filter read back under a different seed would find nothing.

### `DDSketch` (id 15)

```
f64     the relative accuracy
u64     the total number of values added
u64     how many of them were zero
f64     the smallest value added
f64     the largest value added
store   the positive buckets
store   the negative buckets, indexed by magnitude
```

where a store is

```
i32     the index of its first bucket
bytes   a u64 count per bucket, from that index upward
```

The counts run contiguously from the first occupied bucket, so the store's bounds and
its total follow from the run itself rather than being written alongside it and given
the chance to disagree with it. A payload whose buckets do not add up to the recorded
total is refused: every quantile would land at the wrong rank while each individual
bucket still looked reasonable.

`gamma`, which is `(1+a)/(1-a)`, is derived from the accuracy rather than stored. So is
the log of it. See [What is not stored](#what-is-not-stored).

This is the only structure whose payload names hash id 2. It holds numbers, hashes
nothing, and reading it with a supplied hash function is refused rather than ignored —
a caller passing one has misunderstood what they are reading.

### `HyperLogLogPlus` (id 16)

```
u32     precision, between 4 and 18
u8      which representation follows: 0 sparse, 1 dense
```

then, when sparse,

```
u32     how many hashes
u64     each hash, strictly increasing
```

and when dense,

```
bytes   the registers, one byte each
```

The estimator holds hashes while there are few of them, so the payload has to say which
it holds. Always writing the
dense form would make a payload of a nearly-empty estimator as large as a full one,
which is the thing the sparse form exists to avoid: ten items take 107 bytes rather than
16,393.

The sparse hashes are written strictly increasing, and a payload where they are not is
refused. They are the estimator's count as well as its contents — a repeated or
unordered run would report more distinct items than it holds.

The estimator converts once it would hold more than `max(16, 2^precision / 8)` hashes,
so no payload it writes holds more. `2^precision / 8` is Heule, Nunkesser and Hall's
rule (section 5.3): convert once the hashes, eight bytes each, would cost more than the
registers at a byte each. The floor of 16 is a deliberate departure from it, deciding
only precisions 4 to 6: it keeps counts up to 16 exact there, at the price of hashes
taking up to 112 bytes more than the registers would. Every release since 6.0.0 has held
up to 16 at those precisions. A reader is not obliged to enforce the bound; this one
does not.

The register count is not stored: it is `2^precision`, and a payload whose registers do
not come to that is refused. Registers above `64 - precision + 1` are refused too, since
no hash could have produced them.

### `QuotientFilter` (id 17)

```
u32     quotient bits
u32     remainder bits
u32     the number of entries
bytes   the slot table
```

The slot count is `2^quotient bits`, each slot is `remainder bits + 3` wide, and the
table is those slots packed end to end with one spare word so a slot straddling the end
has somewhere to reach. All of that follows from the two widths, so none of it is
written: a payload cannot then disagree with itself about the shape of its own table.

The three bits per slot are `is_occupied`, `is_continuation` and `is_shifted`. Only the
first belongs to the slot; the other two belong to whatever entry is sitting there, which
is why an entry can be moved without its slot's occupied bit moving with it.

A payload claiming more entries than there are slots is refused, as is one whose table is
not the size its two widths call for.

The quotient bits run from 1 to 32 and the two widths together come to at most 64, the
bits a hash supplies; a payload outside either is refused. The writer never goes past 31
quotient bits, because the slot count is a 32-bit number and 2^32 does not fit it. A
payload at 32 was written by a constructor from 6.2.0 or earlier, which wrapped a request
for more items than the largest table into a table of one slot. Such a payload holds one
slot's bytes, so its slot count is 1, not `2^32`, and it reads back as that one-slot
filter, exactly as it was written. Remainders wider than the writer now allows are read
as written in the same way.

### `ThetaSketch` (id 18)

```
u32     the retained size, k
u64     theta, the threshold below which values are kept
u32     how many values are held
u64     each value, strictly increasing
```

Theta is the sampling rate as well as the threshold, so it is the one field that scales
every answer the sketch gives. A payload whose theta is wrong does not look broken; it
looks like a different cardinality.

The values are written strictly increasing, and a payload where they are not is refused —
they are the sketch's count as well as its contents, so a repeat would be counted twice. A
value at or above theta is refused for the same reason: it was not sampled at the rate the
estimate applies.

A sketch never holds more than twice what it retains, because that is when it trims, so a
payload claiming more than `2k` values is refused. That is also what bounds `k`: the
values are held in one array, so `2k` is at most what an array holds (see
[Sizes](#sizes)).

### `SimHashSignature` (id 19)

```
u64     the fingerprint
```

As with a `MinHashSignature`, the hash is **not** a caller's to choose. It is XxHash3 over
each term's UTF-8 bytes, with a term weighted by how many times it appears, and that is a
fixed convention rather than stored state — it is what lets a fingerprint be compared
against one computed by another process or another version. A payload naming any other
hash is refused rather than read.

Changing the hash, or the way its bits become the fingerprint, would silently invalidate
every stored fingerprint: the new ones would be perfectly self-consistent and would match
none of the old ones. The test suite pins the value a known document produces.

### `CountSketch` (id 20)

```
u32     width, the cells per row
u32     depth, the rows
bytes   an i64 per cell, row by row
```

The cells are **signed**, unlike a `CountMinSketch`'s, because a count sketch adds and
subtracts at each cell rather than only adding. A payload read as unsigned would come back
with every negative cell as an enormous positive one, so they are written as `i64` rather
than reinterpreted.

A payload with no rows or no columns is refused, as is one with a width or a depth past
the limits in [Sizes](#sizes), and one whose cells do not come to `width * depth * 8`
bytes. The limits are checked first, so that the product cannot wrap.

### `InvertibleBloomLookupTable` (id 21)

```
u32     the number of cells
u32     the key size in bytes
bytes   an i64 count per cell
bytes   the xored keys, key size bytes per cell
bytes   a u64 xored key hash per cell
```

The counts are **signed**. A subtracted table is mostly negative — that is how it records
"they have this and I do not" — so reading them back unsigned would turn every one of
those into an enormous positive and the table would decode to nothing.

The three runs must come to `cells * 8`, `cells * keySize` and `cells * 8` bytes
respectively, and a payload where they do not is refused. So is one with fewer cells than a
single key occupies.

### `BloomierFilter` (id 22)

```
u32     the number of distinct keys
u32     segment length, a power of two
u32     segment count
u64     the seed the peel succeeded on
u32     the value width in bits
bytes   the cells, ceil((8 + value bits) / 8) bytes each
```

Each cell holds an 8-bit fingerprint above the value, and a key's three cells combine by
exclusive-or to give both. The seed matters as much as the cells, as it does for a
`BinaryFuseFilter`: every position is computed from it, and a map read back under a
different one would answer with the wrong values rather than with none.

### `HeavyKeeper` (id 23)

```
u32     k, the number of elements tracked
u32     width, buckets per array
u32     depth, the number of arrays
u8      placement, 0 for 32-bit values and 1 for 64-bit ones   (version 9 only)
f64     the decay base b
u64     the decay generator's state
u64     n, the number of items added
u16     the fingerprint fields, depth * width of them, array-major
u64     the counter fields, depth * width of them, array-major
u32     element count
        per element:
bytes     the element's data
u64       its recorded frequency
```

The tracked elements are re-heaped on read rather than being trusted to arrive in heap
order, for the same reason `TopK`'s are. The generator's state is stored so a reloaded
structure resumes its decay sequence rather than replaying it; see below.

An element's bucket in array `j` is described in [Bit placement](#bit-placement), with `m`
the width; before version 9 there is no placement byte, and the buckets were chosen with
32-bit values. Its fingerprint is the top sixteen bits of `h` either way.

### `VarOpt` (id 24)

```
u32     k, the number of items kept
u64     the eviction generator's state
u64     n, the number of items added
u32     the count of items held at their own weights
u32     the count of items held at the threshold
f64     the threshold region's total weight
        per exact item:
bytes     the item's data
f64       its weight
        per threshold item:
bytes     the item's data
```

The threshold itself is **not** stored. It is the region's total weight divided by
the region's size, and storing the quotient instead of the total would let rounding
accumulate across a save-and-reload cycle. The total is stored as it is held, so a save
and a load change none of it. What the structure promises of its total is that no weight
is lost when an item is dropped, because the survivors inherit it. The total is still a
sum of doubles, exact only while the weights are integers and every partial sum stays
below 2^53; a weight with a fraction can round as soon as it is added.

Which reading a payload gets is decided by the threshold-region size: zero means the
sample is still exact and holds everything it has seen, so the reader requires the
held count to equal `n` and the region weight to be zero. Any other value means
sampling has begun, so the reader requires the two regions together to fill `k`
exactly, `n` to exceed `k`, and the region weight to be a positive finite number.
Neither state can be forged into the other without failing one of those.

Version 6.2.0 could write a payload that fails the first reading. Merging a sample
that had begun sampling into one with room for everything it held recorded the
result as exact, holding fewer items than the `n` it counted. Such a payload is
refused, and the refusal says where it came from: it does not record which of its
items were held at the threshold, so it cannot be restored without guessing. The
6.2.0 reader refused it too, so no payload that ever loaded stops loading.

That refusal is not a complete screen, but what passes it gives unbiased estimates. The
6.2.0 merged result held exactly the input's items at exactly their adjusted weights.
Only `n`, which no estimate reads, and its record of which items stood at the threshold
were wrong. If it, or a sample it was merged into, began sampling before being saved,
the payload satisfies every rule above and loads. Each step that can do that treats
adjusted weights as weights, which keeps every estimate unbiased; measured on 6.2.0 over
4,000,000 trials per route, every subset sum averaged to its true weight within sampling
error. One more `Add`, a merge into a sample keeping the same number of items or fewer,
or another sample merged into it, leaves a sound sample, distributed as if the merge had
never happened.

Merging it into a sample keeping more items can cost precision. That sample holds the
input's threshold items as exact ones, at the input's threshold. While its own threshold
is lower than the input's, it keeps the input whole and samples the rest with the slots
left over, and its summed variance measured 15–30% above that of a sample of its size
built from the original data. Nothing in the payload records this. Where the original
weights are known, it shows as items reported at a second value above the threshold.
The remedy is outside the format: rebuild the sample from the original data, or merge
the original samples again with this version.

Once sampling has begun, the items held at their own weights are re-heaped on read
rather than being trusted to arrive in heap order, for the same reason `TopK`'s and
`HeavyKeeper`'s are; a heap this library wrote comes through unchanged. Before
sampling begins they are not a heap but every item seen, in the order it arrived, and
that order is read back as written. It is what the transition to sampling heaps from,
so re-heaping it changes which of two tied items is contested first, and with it what
is sampled later. The 6.2.0 reader re-heaped it.

The generator's state is stored so a reloaded sample resumes its eviction sequence
rather than replaying it; see below.

### `UltraLogLog` (id 25)

```
u32     p, the precision
bytes   the registers, 2^p of them, one byte each
```

A register holds the position of the highest bit a run of zeros stopped at, in its top
six bits, and whether the two positions below it were also reached, in its bottom two.
Zero means untouched.

What is stored is the **absolute** bit position, not the length of the run — the
precision is inside the value rather than subtracted out of it. That is what makes a
sketch foldable: the same element yields the same register value at every precision, so
merging a finer sketch into a coarser one reproduces the coarser sketch exactly rather
than approximating it. A format that stored the run length would be smaller by nothing
and would make folding lossy.

A reader rejects any register recording a position below the precision's index bits,
since no insertion at that precision can produce one, and it would take the estimator
outside the range its corrections cover.

### `Grafite` (id 26)

```
u64     r, the size of the reduced universe
u64     the hash multiplier
u64     the hash addend
u64     the number of keys the filter was built from
u64     the largest range size the rate was promised for
u32     how many low bits of each code are stored verbatim
u32     the number of stored hash codes
u32     how many bits of the high bitvector are in use
u64     the smallest stored code
u64     the largest stored code
u32     the number of words of low bits
u64       each word
u32     the number of words of high bits
u64       each word
```

The hash parameters are stored rather than a seed, because they *are* the filter: the
codes were placed by them and mean nothing without them. A reader rejects a multiplier
of zero, which would collapse the block term and make a query one reduced universe away
from a key collide with it every time — the attack the filter exists to survive.

The codes are held in Elias–Fano: the low bits of each code packed end to end, and the
high bits as a bitvector in which the ith code sets the bit at (high part + i), so the
high parts are recovered by counting rather than stored. The index over that bitvector
is rebuilt on read rather than written down, since it is a function of the bits it
indexes.

A reader also rejects a code at or above the reduced universe, a split wider than a
code, and more codes than keys — none of which hashing can produce.

### `InfiniFilter` (id 27)

```
u64     the number of items added, less those removed
u32     how many times the active table has doubled
u32     the number of tables in the chain
        per table:
u32       address bits, so the table has 2^this slots
u32       fingerprint bits given to a fresh entry
u32       how many entries the table holds
u32       the number of 64-bit words that follow
u64       each word of the table
```

Each slot is three metadata bits — the quotient filter's occupied, shifted and
continuation flags — followed by one field of *fingerprint bits + 1*. That field holds a
unary age counter, being as many zeros as expansions the entry has lived through
followed by a single one, and then whatever fingerprint bits remain. The counter is
self-delimiting, which is what lets a reader recover a fingerprint whose length was
never written down.

The counter terminates in a one rather than a zero so that every field has a set bit,
and therefore **no entry is ever encoded as zero**. Nothing in this filter needs that;
the Memento range filter, which builds on the same tables, needs a value that cannot be
a real entry in order to delimit a variable-length group of keys sharing a fingerprint.
Zero is that value. It is recorded here because it is invisible from the outside and
free only until payloads in this format exist.

The address is the **low** bits of the hash and the fingerprint the bits above it, which
is the opposite of the usual quotient filter arrangement. It has to be: an expansion
takes the next bit up, so an entry's address is a growing prefix of its hash and the
table can double without the original keys.

A reader rejects a chain of no tables, a chain longer than a 64-bit hash could justify,
a table with more than 31 bits of address (releases up to 6.2.0 read up to 40, with a slot
count that wrapped and gave a table of 1 to 256 slots; no writer ever produced one), a
table holding more entries than it has slots, a table whose address and fingerprint
together exceed the hash, and a word count that disagrees with the shape claimed. The
tables are stored newest first, and the count of entries per table is stored rather than
recomputed, because a table's occupancy cannot be derived from its bits alone.

### `MementoFilter` (id 28)

```
u64     the number of keys held
u32     how many times the active table has doubled
u32     the memento width, in bits
u32     the number of tables in the chain
        per table:
u32       address bits, so the table has 2^this slots
u32       fingerprint bits given to a fresh block
u32       how many slots the table uses
u32       the number of 64-bit words that follow
u64       each word of the table
```

Each slot is three metadata bits and then a field of *fingerprint bits + 1 + memento
bits*: a fluid fingerprint, exactly as `InfiniFilter` encodes one, with the key's own
low bits beneath it. The memento sits below the fingerprint rather than above it because
an expansion takes the fingerprint's low bit for the address, and taking a memento bit
would move key data into an address and answer about a key nobody stored.

A run holds *keepsake boxes*: the keys of one block, sharing a fingerprint. Boxes carry
no recorded boundaries. One key sits beside its fingerprint; two repeat the fingerprint;
more than two store the smallest and largest at the front, the second behind a **zero**
fingerprint, and the rest as a bit-packed count and list running across the following
slots without regard for slot boundaries. Zero cannot be a real fingerprint because every
age counter ends in a set bit, which is the property `InfiniFilter`'s encoding reserves.

The count is written in memento-sized pieces, an all-ones piece meaning "add this and
read another", so a small count costs as much as one memento and a large one still fits.

A reader rejects an impossible memento width, a chain of no tables or absurdly many, a
table with more than 31 bits of address (as `InfiniFilter`'s), a table whose slots are wider
than 64 bits, a table with more entries than slots, and a word count disagreeing with the
shape claimed.

A slot of exactly 64 bits — the fingerprint plus the memento width come to 60 — was written
by releases up to and including 6.2.0 with a mask of zero, which dropped every write: the
table counted each key and held none. Such a payload has a positive entry count and no
word with a bit set, which no table holding an entry can have, and a reader refuses it with
that said instead of reading a filter that denies every key it claims to hold. A table of
wider slots was never usable either, and is refused for its width, empty or not.

### `SublimeCountMinSketch` (id 29)

```
f64     delta, the probability the error bound is exceeded
f64     the growth exponent
f64     the size factor
u32     the number of rows
u32     the number of counters in a row
u64     the total count
u64     the count at which the arrays next double
u64     the count at which they next fold back
u32     the number of records of earlier states, oldest first
u32       the number of counters in one row of that record
u64         each of its counters, row by row
u64     each counter of the current arrays, row by row
```

The counters are written as plain `u64` rather than in the packed form the sketch holds
them in. Packing depends on two parameters the sketch retunes as it runs, so packed bits
would only be safely readable by the version that wrote them — and a corrupted payload
would not look corrupt, it would decode into different counters. Every `u64` is a valid
count, so nothing in this layout can be misread as something else. The sketch packs them
again on the way in, at a tuning chosen to suit what it just read.

The records of earlier states are what let the sketch fold back rather than only grow.
Each stands for one doubling and is half the width of the one after it, so a reader can
check the whole chain against the current width: a record claiming a width the doublings
that follow it do not require is refused.

A reader also rejects a delta outside nought to one, a growth exponent outside the same
range, a size factor that is not positive, a row count of zero or past the deepest sketch
the constructor builds (see [Sizes](#sizes)), a width that is not a power of two — the
low bits of a hash pick the counter — and more than thirty records, which would be more
counters than a row may hold. Readers before this limit was shared refused more than
sixty-four rows, which a delta of `1e-28` already needs.

The total count and the count at which the arrays next double are held to the rest. The
arrays double once the total reaches (width × size factor)^(1 / growth exponent), rounded
up, and the add that reaches it sets the next one past it, so a sketch always holds fewer
keys than its doubling count. A reader refuses a total at or past the doubling count, and
a doubling count past the one arrays twice the width would have. It is not held to the
width's own count exactly, because that is a power worked out in floating point, which
is not the same in its last bit on every platform, and a payload written on one is read
on another; twice the width doubles at about twice the count or more, which no such
difference reaches. A doubling count of 2^64 or more is written as the largest `u64` and
stands for one no total reaches, so any total fits beneath it, the largest included.
Every payload 6.2.0 wrote meets both. The count at which the arrays fold back is not
checked: one that does not fit can only bring the next fold earlier or later, and every
fold and doubling sets it afresh.

### `SetSketch` (id 30)

```
f64     b, the base
f64     a, the hash rate
u32     m, the number of registers
u32     q, the largest value a register may hold
u8      the construction, at format version 4 only
u16     each register
```

The lower bound the insert path keeps is not written. It is an accelerator rather than
state: any value no register is below will do, and the registers themselves say what it
should be, so a reader recomputes it rather than trusting a number that could be wrong.

A reader rejects a base at or below one or above two, a rate that is not positive, a
register count of zero or past the constructor's limit (see [Sizes](#sizes)), a ceiling a
two-byte register could not hold one more than, and any register above that ceiling —
which no sketch with these parameters could have written. Readers before that limit was
shared refused more than 2^26 registers, which the constructor had always built.

### `SetSketch` variants

A sketch built with the paper's **second** construction writes one extra byte after the
ceiling, naming the construction, at format version 4. A sketch built with the first
writes no such byte and stays at version 1 — the layout it has always written.

The asymmetry is deliberate. Writing the byte unconditionally would raise the version of
every `SetSketch` payload, making them unreadable to libraries that predate the choice,
to record a decision those sketches did not make. A payload with no variant byte is the
first construction by definition, which is what keeps old data readable rather than
merely tolerated.

A reader at version 4 or above rejects any variant byte other than the second
construction's, since the first writes none.

### `TupleSketch` (id 31)

```
u32     the nominal entry count
u8      the summary policy
u64     theta, the sampling threshold
u32     the number of keys held
u64     each key, in increasing order
f64     each key's summary, in the same order
```

Keys come first and summaries after, rather than interleaved, so that a payload whose
keys are not in increasing order is detectably not one of these without stepping over a
value between each pair. The sketch is compacted before it is written, so the keys are
the distinct retained set rather than whatever order they arrived in.

A reader rejects a nominal entry count this library does not build — zero, or past the
limit in [Sizes](#sizes) — a policy byte naming
a way of folding it does not know, more keys held than twice what the sketch retains,
keys out of increasing order, a key at or above theta — which the sampling that produced
the rest would have discarded — and a summary that is not a number, which the sketch
refuses on the way in and so cannot have written.

### `PrivateCountMinSketch` (id 32)

```
u32     counters per row
u32     rows
u8      placement, 0 for 32-bit values and 1 for 64-bit ones   (version 9 only)
f64     the zero-concentrated privacy budget the noise was drawn for
u64     items added
        per row, per counter:
f64       the counter, which is a noise draw plus a count
```

Columns are chosen as a `CountMinSketch`'s are, as [Bit placement](#bit-placement)
describes; before version 9 there is no placement byte, and they were chosen with 32-bit
values. This is also the body a `DpswSketch` holds, laid out as the window's version lays
it out.

**The seed is not written, and must never be.** The counters are safe to write because
they already carry the noise: a payload of them reveals no more than the live sketch
does. The seed is the opposite — anyone holding it can regenerate the noise, subtract it
back off, and recover the exact counts, which is the whole guarantee gone. A sketch read
back therefore has no seed at all. It can be queried and it can keep counting, because
adding touches no randomness.

The counters are noise, and that noise is drawn through `Math.Log`, which .NET does not
guarantee to be bit-identical across platforms. Two sketches built from the same seed on
different operating systems can therefore differ in the last bit of a counter. This costs
nothing on the read side — loading a payload compares and adds stored doubles and touches
no transcendental function, so a stored sketch answers identically everywhere — but it
does mean these payloads have no byte-exact write fixture, only a pinned layout.

A reader rejects a sketch with no rows or no columns, a budget that is not a positive
finite number, and any counter that is infinite or not a number.

It also rejects a payload whose counters are **all exact integers**. A counter is a draw
from a continuous distribution plus a whole number of hits, so it is non-integral with
probability one, and stays so for the sketch's whole life. A payload without a single
non-integral counter is therefore not one this mechanism produced — it is a plain
`CountMinSketch` wearing this one's name, and reading it would hand back a structure
whose entire contract is silently false. This is the one guard here that is
probabilistic rather than structural: at a single counter it could in principle refuse a
legitimate payload, with probability around 2^-53.

### `DpswSketch` (id 33)

```
u64     the window, in items
f64     the zero-concentrated privacy budget for the whole structure
f64     the checkpoint factor
u32     the substream size, in items
u32     counters per row in every sketch
u32     rows in every sketch
u64     items seen in all
u32     the number of substreams held
        per substream:
u64       the stream position it begins at
u32       how many items it holds
          per planned segment, in plan order:
...         a PrivateCountMinSketch body, exactly as above, at the window's version
```

A window at version 9 holds version 9 bodies, each with the placement byte of its own
sketch, and one at version 1 holds version 1 bodies, which have none. The window has no
placement of its own: each sketch is read as its body says.

**Neither the generator nor the segment plan is written.** The generator is the secret,
for the reason given above. The segment ranges and their budgets are a pure function of
the substream size, the checkpoint factor and the budget — all of which *are* written —
so recomputing them on read means a payload has **no way to express a budget split that
does not sum to the whole**. Only the counters are stored, laid out in the order the
plan produces its segments.

A window read back draws a **fresh unpredictable generator** for the substreams it goes
on to build. This is sound rather than merely convenient: a substream's sketches are all
built at once, when the substream begins, so the new generator is only ever used for
substreams starting after the read, and those cover items disjoint from everything
already written. Differential privacy composes in parallel over disjoint data, taking a
maximum rather than a sum — the same disjointness the smooth histogram already relies
on. The cost is that **reproducibility does not survive a round trip**: a window built
from a fixed seed, written and read back, will not produce the same noise for its later
substreams. That is the price of not writing the secret down, and it is the right way
round.

A reader rejects a window shorter than one item, a budget or checkpoint factor outside
its range, a substream size below one or larger than the window, sketches with no rows
or columns or with more of either than a `PrivateCountMinSketch` holds (see Sizes), a
negative position, substreams that do not follow one another in order, a
substream holding nothing or more than its size, a substream ending past the items the
window claims to have seen, and a nested sketch whose shape disagrees with the window's.
It also applies the same refusal the constructor makes for a checkpoint factor whose
leanest sketch would be drowned in its own noise, so that a payload is not a way around
it — and every nested sketch carries the all-integers guard above.

## Bit placement

This section is also where the columns of the count-min sketches, the buckets of
`HeavyKeeper` and the slots of `InverseBloomFilter` come from, below.

The bits of `BloomFilter`, `CountingBloomFilter`, `DeletableBloomFilter`,
`PartitionedBloomFilter` and `StableBloomFilter` are positions an item's hash was mapped
to, so reading them needs the mapping as well as the layout. Each filter has `k` hash
functions and reduces positions modulo `m`: the bit count for the plain and counting
filters, the data region for the deletable filter, the cell count for the stable filter,
and the partition size `s` for the partitioned filter, whose `i`th position goes in the
`i`th partition. `h` is the 64-bit hash of the item: XxHash3 with seed 0 for hash id 1.

Positions come from 32-bit values in a payload before version 8, and in a version 8
payload whose placement byte is `0`:

```
low  = h & 0xFFFFFFFF
high = h >> 32
position i = ((low + i * high) mod 2^32) mod m,   for i = 0 .. k - 1
```

They come from 64-bit values, as `BloomFilter64`'s always have, in a version 8 payload
whose placement byte is `1`:

```
first  = h
second = the same hash, of first's eight bytes, least significant first
position i = ((first + i * second) mod 2^64) mod m,   for i = 0 .. k - 1
```

The second value is the hash of the first rather than a fixed mixer of it, such as
SplitMix64's or Murmur3's finalizer, which would save a few nanoseconds an operation.
`BloomFilter64` has written payloads this way since before 6.2.0 and cannot change, so
keeping it means every structure that places with 64-bit values shares one derivation.
It also follows the hash a caller supplies, a keyed one included, where a mixer is fixed.
Neither form carries more than 64 bits: the second value is a function of `first` alone
in both, so two items whose first hashes agree take the same positions.

The 32-bit form reduces a 32-bit value modulo `m`, which gives the first `2^32 mod m`
positions one more of the 2^32 values than the rest. Those positions fill faster, and a
query lands on them as often as an item did, so the false positive rate rises: a filter
of three quarters of 2^32 bits sized for 1% measured 1.42%. The excess is zero when `m`
is a power of two and largest when `2^32 mod m` is half of `m`, and it shrinks with the
square of `m`'s share of 2^32. Below 2^27 positions it is too small to measure, which is
why a new filter places with 64-bit values only from there, and keeps the 32-bit form
below it.

The same two forms place the counts of `CountMinSketch` and `PrivateCountMinSketch`, the
buckets of `HeavyKeeper` and the slot of `InverseBloomFilter`, with `m` the width or the
capacity. In a payload before version 9, and in a version 9 payload whose placement byte
is `0`, they come from 32-bit values; in a version 9 payload whose byte is `1`, from
64-bit ones. A sketch counts an item in column `position i` of row `i`, for `i = 0 ..
depth - 1`, and HeavyKeeper's element contests bucket `position j` of array `j`. The
inverse filter holds an item in one slot, `position 0`:

```
32-bit:   slot = (h & 0xFFFFFFFF) mod m
64-bit:   slot = h mod m
```

which needs no second value. It is the 32-bit rule with the whole hash in place of its
low half, rather than another reduction such as a multiply and shift, so the two agree
whenever `m` is a power of two, and a hash function that fills only its low 32 bits still
places as it did, where a multiply and shift would put every item in slot 0.

In a row the 32-bit form gives the first `2^32 mod m` columns one more of the 2^32 values
than the rest, `1 / floor(2^32 / m)` more: about 3% just past 2^27, a quarter at 2^29.8,
and a half from a third of 2^32 to the widest row. A heavier column collects more of
other items' counts, so the items counted there are overcounted by more than the error
bound assumes, a contested HeavyKeeper bucket is contested more often, and the inverse
filter overwrites what its first slots hold sooner. A new sketch, HeavyKeeper or inverse
filter places with 64-bit values from a width or capacity of 2^27, and with 32-bit values
below it, where the spread is at most 1/32. The 64-bit form folds too, by at most `m` in
2^64, which nothing can measure.

## The random generator's state

`StableBloomFilter` chooses which cells to decay on each add, and `CuckooBloomFilter`
chooses which entry to evict when both of an item's buckets are full. Both store their
generator's position, so a restored filter resumes the sequence it was partway through.

Storing the seed alone would have been simpler and is subtly wrong. The bits come back
correct either way, but a filter re-seeded on read sits at the start of its sequence
while the original sits wherever its adds left it. The case that makes this matter is
the one persistence exists for: a filter checkpointed on a schedule would replay the
same first draws after every load, and the stable filter's bound on its false positive
rate assumes its decay is spread across cells rather than aimed at the same ones after
every restart.

That is also why the generator is this library's own SplitMix64 rather than
`System.Random`, which will not say where it is. The sequence for a given seed therefore
changed in 6.0.0 — the seed parameter arrived in 4.0.0, and this is the only breaking
part of the change.

Both filters store a second number, the state their generator began in, because their
`Reset` returns them to their original state and the generator is part of that. Where the
generator has got to is no help: it moves with the draws, and a filter read back from a
payload would have nothing to return to. The beginning is the seed, widened to 64 bits,
for a filter given one; the unpredictable state it was built with for one that was not;
and, for the stable filter only, zero for one made by `NewUnstableBloomFilter`. The stable
filter writes it from version 5 and the cuckoo filter from version 6.

A payload that predates the field does not hold it: version 1 or 2 of the stable filter,
and version 1, 2 or 3 of the cuckoo filter. The reader sets the beginning to the position
it read the generator at — for version 1, which holds no position, the unpredictable one
it gave the filter — so the filter resets to the point it was read at and records that
point when written out again. This is a definition and not a recovery: the seed such a
filter was built with is not in its payload, and a reader that invented one would hand
back a sequence the filter never followed.

## What is not stored

Values a structure derives from what is stored are recomputed rather than written. The
deletable filter's region size is the example that matters: it is a function of the
filter's dimensions, that function was **wrong** before 3.1.0, and persisting the
computed value would have carried the defect into stored data where no fix could reach
it.

`HyperLogLog`'s `b` is the second example, and the same story: it is `log2(m)`, that
derivation was wrong at 2^29 registers before 3.1.0, and an estimator is rebuilt through
its constructor on read so that `b` and `alpha` come out the way a fresh one computes
them.

`DDSketch`'s `gamma` is the third: it is `(1+a)/(1-a)` for the relative accuracy `a`,
which is stored, and deriving it on read means the bucket boundaries a sketch is read
with are exactly the ones a fresh sketch of the same accuracy computes.

Scratch buffers are not stored either. They hold nothing between calls.
