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

| Version | Written by | Structures |
| --- | --- | --- |
| 1 | 3.1.0 onwards | every structure except the two below |
| 2 | 6.0.0 onwards | `StableBloomFilter`, `CuckooBloomFilter` |

Version 2 exists because those two gained a stored random-generator state in 6.0.0.
Raising every structure's version together would have made every payload unreadable to
5.x in order to record a change that eleven of them did not have.

A version 1 payload of either filter still reads. It carries no generator state, so the
restored filter resumes with an unpredictable one: its cells or fingerprints are exactly
right, and only the sequence of choices it will make next is unrecoverable, because it
was never written down.

A reader refuses a payload whose magic does not match, whose version is `0` or greater
than the version it knows, whose structure id is not the type being read, or whose
checksum does not match. It also refuses one that does not consume exactly its stated
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

The data length is checked against what the count and bucket width imply.

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
cannot silently disagree with itself about hashing. It can be pulled out of the outer
payload and read on its own. And the outer structure can change what it holds without
the inner layout being part of that change.

Things that are not structures in their own right — a `Buckets`, a top-k's heap — are
inlined, because there is nothing to read them as.

## Payloads

### `BloomFilter` (id 1)

```
u32     m, the filter's capacity in bits
u32     k, the number of hash functions
u32     count, the number of items added
Buckets the bit array
```

The bucket count must equal `m`.

### `CountMinSketch` (id 10)

```
f64     epsilon
f64     delta
u32     width
u32     depth
u64     total count
u64     depth * width cells, row by row
```

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

```
u32     m
u32     k
u32     count
Buckets the counter array, whose bucket width is the counter width
```

### `DeletableBloomFilter` (id 4)

```
u32     m, the data region in bits
u32     k
u32     count
Buckets the data region
Buckets the collision regions, one bucket per region
```

The region size is **not** stored. See [What is not stored](#what-is-not-stored).

### `PartitionedBloomFilter` (id 5)

```
u32     m, the total capacity in bits
u32     k
u32     s, the size of each partition in bits
u32     count
u32     partition count
Buckets one per partition
```

There is one partition per hash function, and a payload whose partition count differs
from `k` is refused. Each partition must hold `s` buckets.

### `ScalableBloomFilter` (id 6)

```
f64     r, the tightening ratio
f64     fp, the target false positive rate
f64     p, the fill ratio at which a new filter is added
u32     hint
u32     filter count
nested  PartitionedBloomFilter, one per contained filter, in order
```

Order matters: each contained filter targets a rate tightened from the last, and the
final one is the one additions go to.

### `StableBloomFilter` (id 7)

```
u32     m, the number of cells
u32     k
u32     p, the number of cells decremented per add
Buckets the cells
u64     the random generator's state    (version 2 onwards)
```

The maximum cell value follows from the cell width the cells carry.

The generator's state is the last field, and version 1 payloads simply end before it.
See [The random generator's state](#the-random-generators-state).

### `InverseBloomFilter` (id 8)

```
u32     capacity
u32     occupied slot count
        per occupied slot:
u32       slot index
bytes     the stored data
```

Only occupied slots are written. This filter stores the data rather than hashing it, so
a full-length run of empty slots would be most of a large, mostly idle filter's payload.
A slot index at or beyond the capacity is refused.

### `CuckooBloomFilter` (id 9)

```
u32     m, the number of buckets
u32     b, entries per bucket
u32     f, the fingerprint size in bytes
u32     count
u32     n, the capacity in items
u32     occupied entry count
        per occupied entry:
u32       bucket index
u32       entry index
bytes     the fingerprint
u64     the random generator's state    (version 2 onwards)
```

Occupied entries only, for the same reason as above: a filter sized for a load it has
not reached is mostly empty slots.

The generator's state is the last field, and version 1 payloads simply end before it.
See [The random generator's state](#the-random-generators-state).

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
u32     n, the number of items added
nested  CountMinSketch
u32     element count
        per element:
bytes     the element's data
u64       its recorded frequency
```

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

The estimator holds hashes while there are few enough of them for that to cost less
than the registers would, so the payload has to say which it holds. Always writing the
dense form would make a payload of a nearly-empty estimator as large as a full one,
which is the thing the sparse form exists to avoid: ten items take 107 bytes rather than
16,393.

The sparse hashes are written strictly increasing, and a payload where they are not is
refused. They are the estimator's count as well as its contents — a repeated or
unordered run would report more distinct items than it holds.

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
payload claiming more than `2k` values is refused.

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

A payload with no rows or no columns is refused, as is one whose cells do not come to
`width * depth * 8` bytes.

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

## The random generator's state

`StableBloomFilter` chooses which cells to decay and `CuckooBloomFilter` chooses which
entry to evict, one draw per add in each. Both store their generator's position, so a
restored filter resumes the sequence it was partway through.

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
