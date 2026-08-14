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
| 4 | 2 | Format version (currently 1) |
| 6 | 2 | Structure id |
| 8 | 2 | Hash id |
| 10 | 4 | Payload length in bytes |
| 14 | *n* | Payload |
| 14 + *n* | 4 | CRC-32 of bytes 4 through 14 + *n* |

The checksum covers the header as well as the payload, so a corrupted length or
structure id is caught by the same check rather than being acted on first. The magic is
outside it: a payload whose magic is wrong is not this format at all, and there is
nothing to checksum.

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

Ids are assigned once and never reused, including for structures that no longer exist.

### Hash ids

| Id | Hash |
| --- | --- |
| 0 | A function supplied through `SetHash`, which cannot be recorded |
| 1 | 64-bit XxHash3, the default since 3.0.0 |

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

## What is not stored

Values a structure derives from what is stored are recomputed rather than written. The
deletable filter's region size is the example that matters: it is a function of the
filter's dimensions, that function was **wrong** before 3.1.0, and persisting the
computed value would have carried the defect into stored data where no fix could reach
it.

Scratch buffers are not stored either. They hold nothing between calls.
