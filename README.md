# Probabilistic Data Structures for C<span>#</span> [![CI](https://github.com/mattlorimor/ProbabilisticDataStructures/actions/workflows/ci.yml/badge.svg)](https://github.com/mattlorimor/ProbabilisticDataStructures/actions/workflows/ci.yml) [![NuGet](https://img.shields.io/nuget/v/MattLorimor.ProbabilisticDataStructures.svg)](https://www.nuget.org/packages/MattLorimor.ProbabilisticDataStructures/)

Twenty-five structures that answer questions about data too large to keep, by keeping
something much smaller and being approximately right.

Each one trades exactness for space. What makes them usable is that the trade is
*specified*: a Bloom filter never says no about something it holds, a DDSketch is within
1% of the true value, a binary fuse filter is wrong 0.39% of the time. This README tries
to be equally specific about **when each one is the wrong choice**, because that is the
part you usually find out later.

Originally a C# port of [Tyler Treat's](https://github.com/tylertreat)
[BoomFilters](https://github.com/tylertreat/BoomFilters), and still owing it the
descriptions of the original eight structures. It has since diverged deliberately —
different hashing, argument validation, a documented persistence format, merging, and
eleven structures the Go library does not have.

> **On compatibility with BoomFilters.** This is a port of the algorithms, not a
> wire-compatible implementation. The two libraries hash with different functions —
> XxHash3 here since 3.0.0, FNV-1a in Go — so a filter built by one cannot be read by the
> other, and their false positives will not agree.

---

## Choosing a structure

### What are you trying to do?

| Your question | Reach for | Section |
| --- | --- | --- |
| Have I seen this before? | `BloomFilter` | [Membership](#membership) |
| What **value** goes with this key? | `BloomierFilter` | [Membership](#membership) |
| …and I need to remove things | `CuckooBloomFilter` | [Membership](#membership) |
| …and I need to remove things *and* combine filters | `QuotientFilter` | [Membership](#membership) |
| …and the set never changes after I build it | `BinaryFuseFilter` | [Membership](#membership) |
| …and the stream never ends, on fixed memory | `StableBloomFilter` | [Membership](#membership) |
| …and I have no idea how big the set is | `ScalableBloomFilter` | [Membership](#membership) |
| …and a false positive would be expensive | `InverseBloomFilter` | [Membership](#membership) |
| How many distinct things are there? | `HyperLogLogPlus` | [Cardinality](#cardinality) |
| How many are in **both** of these sets? | `ThetaSketch` | [Cardinality](#cardinality) |
| **Which** keys do these two sets differ by? | `InvertibleBloomLookupTable` | [Cardinality](#cardinality) |
| How often have I seen this particular thing? | `CountMinSketch` | [Frequency](#frequency) |
| …and it is rare, or I need to subtract | `CountSketch` | [Frequency](#frequency) |
| What are the most common things? | `TopK` | [Frequency](#frequency) |
| How alike are these two **sets**? | `MinHash` | [Similarity](#similarity) |
| Are these two **documents** near-duplicates? | `SimHash` | [Similarity](#similarity) |
| …across a corpus, without comparing every pair | `MinHashIndex`, `SimHashIndex` | [Similarity](#similarity) |
| What does this distribution look like? p50, p99? | `DDSketch` | [Distributions](#distributions) |
| …but only about the **last hour** | `SlidingWindow<T>` | [Recent data](#recent-data) |

### If you only read one thing

**Start with `BloomFilter`.** It is the right answer surprisingly often, it is the
smallest of the membership structures, and the others exist to fix one specific thing it
cannot do. Move off it only when you hit that thing:

```
BloomFilter
  ├── need to delete? ──────────── CuckooBloomFilter
  │     └── and merge too? ─────── QuotientFilter
  ├── set is fixed forever? ────── BinaryFuseFilter   (smaller and faster)
  ├── don't know the size? ─────── ScalableBloomFilter
  ├── stream never ends? ───────── StableBloomFilter  (forgets, allows false negatives)
  └── more than ~500M items? ───── BloomFilter64
```

### What each one costs

All four asked for the same thing — 100,000 items at a 1% false positive rate — and sized
from each structure's own geometry rather than measured off the heap.

| | bytes | bits/item | measured fp | delete | merge |
| --- | --- | --- | --- | --- | --- |
| `BinaryFuseFilter` | 118,784 | **9.5** | **0.392%** | no | no |
| `BloomFilter` | 119,813 | 9.6 | 1.001% | no | yes |
| `CuckooBloomFilter` | 278,528 | 22.3 | 0.009% | yes | no |
| `QuotientFilter` | 327,680 | 26.2 | 0.295% | yes | **yes** |

Two things to read out of that.

**Deletion costs about 2.5× the memory.** Check whether you actually need it before paying
for it — a filter you rebuild periodically is often cheaper than one you edit.

**Three of the four beat the rate you asked for, by a lot.** Only the Bloom filter hits 1%
on the nose; the others land where their fingerprint width or load factor puts them, which
is better than requested but not free — you are paying for accuracy you did not ask for. If
you want a specific rate rather than "at most this", `BloomFilter` is the one that gives it
to you.

Speed is deliberately not in that table. It moves by a factor of two between runs on the
same machine, so the numbers would be worse than useless for choosing; see
[Benchmarks](Benchmarks/README.md) for measured timings and why they are not gated on in
CI. Broadly: `BinaryFuseFilter` is the fastest by a wide margin, and the rest are close
enough that memory and capability should decide.

---

## Installation

```
dotnet add package MattLorimor.ProbabilisticDataStructures
```

Requires .NET 10 or later. See [CHANGELOG.md](CHANGELOG.md) for what changed in each
release.

The package ID carries a prefix, but the assembly and namespace do not:

```C#
using ProbabilisticDataStructures;
```

> **Note on package naming.** An unprefixed `ProbabilisticDataStructures` package also
> exists on nuget.org. It was published in 2018 from this project's source by an account
> unaffiliated with this repository, is not maintained here, and will not receive these
> releases. Current releases ship under the `MattLorimor.` prefix above.

Packages are published to
[NuGet](https://www.nuget.org/packages/MattLorimor.ProbabilisticDataStructures/), and each
release is tagged on the
[releases page](https://github.com/mattlorimor/ProbabilisticDataStructures/releases).

---

## Things that apply to everything

### Hashing

Every constructor takes an optional hash function:

```C#
var filter = new BloomFilter(10000, 0.01, hash: myHashFunction);
```

Omitting it uses the default, a 64-bit XxHash3. A scalable filter passes it to the filters
it adds as it grows, and a top-k to the sketch it holds.

**The hash cannot be replaced once a structure holds anything.** `SetHash` throws in that
case. Everything already stored was placed by the hash in use at the time, and replacing it
moves none of it — so the structure would report items it can no longer find. It would not
look broken, it would look *empty*. `SetHash` stays available before anything is added,
including after `Reset()`.

Three structures do not take one at all. `DDSketch` holds numbers rather than bytes and
hashes nothing. `MinHash` and `SimHash` signatures fix their hash by convention, because a
signature is only comparable against another built the same way.

The default is not resistant to chosen-input attacks. If an adversary controls what you
insert, they can provoke collisions and inflate your observed false positive rate; supply
a keyed hash such as SipHash if that is your threat model.

### Persistence

Every structure writes to a stream and reads back.

```C#
using var file = File.Create("filter.bin");
filter.WriteTo(file);

using var restored = File.OpenRead("filter.bin");
var filter = BloomFilter.ReadFrom(restored);
```

`ToByteArray()` and `Persistence.FromByteArray<T>(bytes)` do the same without a stream.

The layout is specified in [FORMAT.md](FORMAT.md) and is stable: a payload written by any
version is readable by every later one, or is refused with an explanation. Corruption,
truncation, or reading a payload as the wrong structure throws `InvalidDataException`
rather than producing something that answers incorrectly. Every structure has a fixture
checked in that pins its bytes, so a change that would break stored data fails in CI
rather than in your storage.

A payload records **which** hash was in use. Reading one written under a hash you set with
`SetHash` requires you to supply the same function:

```C#
var filter = BloomFilter.ReadFrom(file, myHashFunction);
```

Without it the read fails, deliberately — see above for what a filter restored under the
wrong hash looks like.

### Merging

Structures built separately can be combined, which is what lets you build them across
shards or machines and put them together at the end:

```C#
var merged = shardA.Merge(shardB).Merge(shardC);
```

Available on `BloomFilter`, `BloomFilter64`, `PartitionedBloomFilter`,
`CountingBloomFilter`, `CountMinSketch`, `HyperLogLog`, `HyperLogLogPlus`, `DDSketch`,
`QuotientFilter` and `TopK`. `ThetaSketch` combines through `Union`, `Intersect` and
`Difference` instead, since union is only one of the three things it does.

Both structures must have the same dimensions **and the same hash function**. A merge of
two that hash differently is refused, because the result would answer confidently about
positions neither of them meant.

Not available on `InverseBloomFilter` (a slot holds one element, so a merge would have to
choose between them), `StableBloomFilter` (its contents are a function of the order things
arrived in), `CuckooBloomFilter` (a fingerprint only means anything relative to the bucket
it landed in — use `QuotientFilter` if you need this) or `BinaryFuseFilter` (its whole set
is solved for at construction).

Two caveats worth knowing before you rely on it:

- For the Bloom family, a merged filter's `Count()` is the **sum** of its inputs', which
  overstates the union whenever they shared elements. The bits are correct; the counter is
  a count of additions, not of distinct items. `HyperLogLog`, `HyperLogLogPlus` and
  `ThetaSketch` estimate the true union instead.
- `TopK.Merge` is approximate in a way the others are not: two sketches can disagree about
  what was frequent, and the merged top-k can miss an element that was genuinely in the
  top-k of the union. See the notes on `TopK` below.

### Reproducibility

`StableBloomFilter` and `CuckooBloomFilter` make random choices as part of what they are.
Both accept a seed:

```C#
var filter = new StableBloomFilter(10000, 2, 0.01, seed: 42);
```

Omitting it seeds unpredictably. Every other structure is already deterministic given its
inputs — including `BinaryFuseFilter`, whose construction retries with new seeds internally
but from a fixed sequence, so the same set always builds the same filter and can be shipped
as a build artifact.

A seeded filter stays reproducible across serialization: both store their generator's
position, so a filter read back resumes the sequence rather than restarting it. That
matters most for a filter checkpointed on a schedule, which would otherwise replay the same
choices after every load. The sequence a given seed produces changed in 6.0.0, when these
filters moved off `System.Random` — which will not report its position and so cannot be
stored.

### Thread safety

**None of these are thread-safe.** No operation is synchronized, including read-only ones.
`Test` is not safe to call concurrently with `Add`: the structures mutate their arrays in
place.

This is deliberate. Locking would cost the single-threaded case, which is the common one,
and the right granularity depends on your access pattern rather than the structure's.
Synchronize externally:

```C#
private readonly object _gate = new object();
private readonly BloomFilter _filter = new BloomFilter(100000, 0.01);

public bool Contains(byte[] data)
{
    lock (_gate) { return _filter.Test(data); }
}
```

Concurrent `Test` calls are safe against each other under the default hash, which is a pure
function, so a reader-writer lock is enough. A hash passed to `SetHash` is shared by every
call, so one holding mutable state — a reused `HashAlgorithm`, say — takes that away.

---

## Membership

Eleven structures answer "have I seen this?". They differ in what they do besides.

All of them may report a false positive. Only two — `StableBloomFilter` and
`InverseBloomFilter` — can report a false **negative**, and both say so prominently below,
because it inverts the guarantee everything else here makes.

### `BloomFilter`

The classic one, and the right default. A bit array and *k* hash functions.

**Reach for it when** you know roughly how many items you'll hold, you need membership and
nothing else, and you never remove anything. Deduplicating a batch, guarding an expensive
lookup, checking a blocklist you rebuild rather than edit.

**Look elsewhere if** you need to delete (`CuckooBloomFilter`), the set is fixed forever
(`BinaryFuseFilter` is smaller *and* ten times faster), you don't know the size
(`ScalableBloomFilter`), or the stream is unbounded (`StableBloomFilter`).

```C#
var filter = new BloomFilter(10000, 0.01);
filter.Add(bytes);
bool seen = filter.Test(bytes);
bool wasAlreadyThere = filter.TestAndAdd(bytes);
```

### `BloomFilter64`

The same structure with 64-bit sizing throughout.

**Reach for it when** the filter needs more than 4.29 billion bits — about **448 million
items at 1%**, and fewer at a tighter rate. `BloomFilter`, `CountingBloomFilter`,
`PartitionedBloomFilter` and `DeletableBloomFilter` all size in 32 bits and refuse past
that, naming this structure when they do.

**Look elsewhere otherwise.** Below that ceiling it is the same filter with wider
arithmetic, so `BloomFilter` is the plainer choice. And above it, consider
`ScalableBloomFilter` instead: it grows by adding filters rather than by making one
larger, so it has no single-filter ceiling at all, and it does not need you to know the
size in advance.

Only the Bloom family has this ceiling. `CuckooBloomFilter` and `BinaryFuseFilter` hold
around 2.1 billion entries before they run into .NET's 2 GB array limit, and the sketches —
`HyperLogLogPlus`, `ThetaSketch`, `CountMinSketch`, `TopK`, `DDSketch` — are a few hundred
kilobytes at their largest sensible settings however much data passes through them. Their
counters are already 64-bit.

### `PartitionedBloomFilter`

A classic Bloom filter that gives each hash function its own slice of the bit array rather
than sharing one.

**Reach for it when** you want each hash to touch a disjoint region — which makes the fill
level per partition uniform and is useful if you are reasoning about or parallelising over
the array directly.

**Look elsewhere if** you just want a Bloom filter. At the same size and *k* it is very
slightly worse on false positives than the unpartitioned form, and the difference in
practice is small enough that `BloomFilter` is the simpler choice.

### `CountingBloomFilter`

A Bloom filter whose bits are small counters, so removal is possible.

**Reach for it when** you need to remove things and want the removal to always work.

**Look elsewhere if** you never remove — plain `BloomFilter` is four to eight times
smaller for the same rate, since every bit becomes a counter. Or if you need removal *and*
merging at lower memory, in which case `QuotientFilter`.

A counter that saturates stops being decrementable, so an element added far more often than
the counter width allows may not be fully removable.

```C#
var filter = new CountingBloomFilter(10000, 4, 0.01);
filter.Add(bytes);
bool removed = filter.TestAndRemove(bytes);
```

### `DeletableBloomFilter`

Deletion without counters, by tracking which regions of the filter are collision-free and
only clearing bits it knows are safe.

**Reach for it when** you need *some* deletion at close to a plain Bloom filter's memory,
and can tolerate that a particular deletion may be refused.

**Look elsewhere if** every deletion has to succeed — `CountingBloomFilter` or
`CuckooBloomFilter`.

Described by Rothenberg, Macapuna, Verdi and Magalhaes in
[The Deletable Bloom filter](https://arxiv.org/pdf/1005.0352.pdf).

### `CuckooBloomFilter`

Fingerprints in a table with two candidate buckets per item, relocating entries to make
room.

**Reach for it when** you need membership plus deletion and want the best space and speed
for it. This is the default choice for a deletable filter.

**Look elsewhere if** you need to merge two filters (`QuotientFilter` — a cuckoo
fingerprint only means anything relative to its bucket, so two of them cannot be combined),
or the set is static (`BinaryFuseFilter`).

Inserts can fail when the filter is nearly full: relocation gives up after a bounded number
of attempts, and `Add` returns `false`. That is an expected outcome rather than an error,
and worth handling.

Described by Fan, Andersen, Kaminsky and Mitzenmacher in
[Cuckoo Filter: Practically Better Than Bloom](https://www.cs.cmu.edu/~dga/papers/cuckoo-conext2014.pdf).

### `QuotientFilter`

Fingerprints in a compact table with three metadata bits per slot, which is enough to
recover each entry's full fingerprint.

**Reach for it when** you need to delete **and** merge. That combination is the only reason
to choose it.

**Look elsewhere otherwise.** Against a cuckoo filter it is slightly larger, faster on
misses, slower on hits, and worse at the same nominal rate. If you don't need merging, use
`CuckooBloomFilter`.

Memory per item is not a single number: the table is a power of two and is sized to stay
under 75% load, so where `n` falls decides it. 13.4 bits/item at n = 98,000, and 26.2 at
n = 100,000, for the sake of 2,000 items. Size accordingly if it matters.

Every addition is stored, including a repeat, so it takes as many removals to empty an item
out as it took additions to put it in. Collapsing repeats would mean collapsing two
different items whose fingerprints agree, and removing either would then make the filter
answer no for the other.

Described by Bender et al. in
[Don't Thrash: How to Cache Your Hash on Flash](https://www.vldb.org/pvldb/vol5/p1627_michaelabender_vldb2012.pdf).

### `BinaryFuseFilter`

A filter for a set known in full at construction. There is no `Add` and there cannot be —
building it solves a system of equations over the whole set at once.

**Reach for it when** the set is fixed: a blocklist, a shipped index, a compiled artifact.
It is the smallest and by far the fastest membership structure here.

**Look elsewhere if** anything gets added later. Nothing can.

```C#
var filter = BinaryFuseFilter.Build(items);          // 0.39%, one byte per entry
var tighter = BinaryFuseFilter.Build(items, 0.001);  // widened to meet the rate
bool maybe = filter.Test(item);
```

Measured against a `BloomFilter` sized for **the same 0.39% rate**, over a million keys:
**9.04 bits/item against 11.54**, and **5.4 ns lookups against 50.5 ns** — three memory
accesses and one hash, against a loop over eight hash functions.

That is a different comparison from the table above, which sized every structure for a 1%
*target*. Matched on target they are the same size and the fuse filter is simply more
accurate; matched on delivered accuracy it is 22% smaller. Both are true and neither is the
whole picture, which is why both are here.

The rate comes from fingerprint width rather than being chosen freely:
`BinaryFuseWidth.Eight` gives 2⁻⁸ and `Sixteen` gives 2⁻¹⁶. A target rate picks the
narrower width that meets it; a rate no width can reach is refused rather than quietly
capped. Builds are deterministic, so a filter can ship as a build artifact.

From Graf and Lemire,
[Binary Fuse Filters](https://arxiv.org/abs/2201.01174).

### `BloomierFilter`

An approximate **map**: it stores a value for each key without storing the keys.

**Reach for it when** you are shipping a compiled lookup table — word classes, a routing
table, a feature-flag map — where the keys are fixed and the values are small. It is
smaller than a dictionary because it never stores a key.

**Look elsewhere if** the set changes. Like `BinaryFuseFilter` it is built once and has no
`Add`. And if you need the keys back, this cannot give them to you at all.

```C#
var map = BloomierFilter.Build(pairs, valueBits: 16);

if (map.TryGetValue(key, out var value))
{
    // the value it was built with -- or, once in 256 lookups of a key it
    // never saw, a value belonging to nothing
}
```

**One deliberate departure from the paper.** A Bloomier filter as classically described
returns an *arbitrary value* for a key it was not built from, with no way to tell that from
a real answer — a wrong answer that looks right, which is a sharper edge than any other
structure here has. This stores an 8-bit fingerprint beside each value so an absent key is
rejected instead, at 2^-8 per lookup. It costs one byte per cell and turns the failure back
into the bounded, quotable kind the rest of this library deals in.

A key appearing twice with different values is refused: a map cannot hold both, and since
the filter does not keep keys it could not tell you which one it dropped. A value too wide
for `valueBits` is refused rather than truncated.

From Chazelle, Kilian, Rubinfeld and Tal,
[The Bloomier Filter](https://dl.acm.org/doi/10.5555/982792.982797).

### `ScalableBloomFilter`

Adds new filters with geometrically tightening rates as it fills, so it grows to fit
whatever arrives.

**Reach for it when** you genuinely don't know how large the set will be and memory is not
bounded.

**Look elsewhere if** memory *is* bounded — `StableBloomFilter` or `InverseBloomFilter`
hold a ceiling. Or if you do know the size, since a correctly sized `BloomFilter` is
smaller than a scalable one that grew into the same capacity.

Described by Almeida, Baquero, Preguiça and Hutchison in
[Scalable Bloom Filters](https://haslab.uminho.pt/cbm/files/dbloom.pdf).

### `StableBloomFilter`

Continuously evicts old information to make room for new, so it holds a bounded amount of
the *recent* past.

**Reach for it when** the stream never ends, memory is fixed, and what matters is whether
you saw something recently — deduplicating an unbounded event stream, for instance.

**Look elsewhere if you cannot tolerate false negatives.** This is the important one: a
stable filter forgets, so it will eventually say no about something it did see. In exchange
its false positive rate converges to a fixed constant instead of climbing to 1 the way a
saturated classic filter's does.

```C#
var filter = StableBloomFilter.NewDefaultStableBloomFilter(10000, 0.01);
Console.WriteLine(filter.StablePoint());   // the rate it converges to
```

Described by Deng and Rafiei in
[Approximately Detecting Duplicates for Streaming Data using Stable Bloom Filters](https://webdocs.cs.ualberta.ca/~drafiei/papers/DupDet06Sigmod.pdf).

### `InverseBloomFilter`

"The opposite of a Bloom filter": it may report a false **negative**, and never a false
positive. A fixed-size hash map that does not handle conflicts.

**Reach for it when** a false positive would be costly and duplicates in your stream tend
to arrive close together. If it says it has seen something, it has.

**Look elsewhere if** you need to remember things seen long ago — a later item hashing to
the same slot simply overwrites the earlier one.

[Originally described by Jeff Hodges](https://www.somethingsimilar.com/2012/05/21/the-opposite-of-a-bloom-filter/).
Hodges' original swaps the stored value atomically; this implementation reads and writes in
two steps, so concurrent use can lose or tear an entry. See
[Thread safety](#thread-safety).

---

## Cardinality

### `HyperLogLogPlus`

How many distinct things a stream held. Use this one.

**Reach for it when** you want a distinct count over anything large. It is the most
accurate and most compact option here for that question.

**Look elsewhere if** you need to intersect two of them (`ThetaSketch`), or you need the
exact answer, which no sketch gives.

```C#
var estimator = new HyperLogLogPlus(precision: 14);   // 2^14 registers, ~0.81% error
foreach (var item in stream) estimator.Add(item);
ulong distinct = estimator.Count();
```

Three things differ from `HyperLogLog`:

- **The whole 64-bit hash is used.** The older estimator keeps only the low 32 bits, so
  items whose hashes agree there are one item as far as it can tell. Hashing consecutive
  integers finds such a pair within 67,297 of them.
- **Small counts are exact**, because it keeps the hashes themselves until registers would
  be cheaper. Ten distinct items is `10`, in 107 bytes rather than 16 KB.
- **There is no bad band.** The older estimator switches from linear counting to the raw
  estimate at 2.5*m* and is at its worst where it changes over — 2.44% mean error there
  against a nominal 0.81%, staying above nominal until about 4*m*. This holds 0.6–0.7%
  straight through.

It uses Ertl's estimator rather than HyperLogLog++'s tables of measured bias. The two were
[measured against each other](Benchmarks/README.md#accuracy-studies) and tie, so the
tie-breaker is that one is forty lines and the other is six thousand numbers.

### `HyperLogLog`

The original, kept because replacing it would change the number an existing estimator
answers with — including one read back from a payload written years ago.

**Reach for it when** you have stored `HyperLogLog` payloads to read, or need answers that
match what earlier versions gave.

**Look elsewhere for new work.** Use `HyperLogLogPlus`.

Described by Flajolet, Fusy, Gandouet and Meunier in
[HyperLogLog](http://algo.inria.fr/flajolet/Publications/FlFuGaMe07.pdf).

### `ThetaSketch`

Distinct counts that support union, intersection and difference.

**Reach for it when** you need to ask "how many were in **both**". That is a question two
cardinality estimators cannot answer between them.

**Look elsewhere for plain counting.** At comparable accuracy it costs sixteen times
`HyperLogLogPlus`: 262,144 bytes at 0.37% against 16,384 at 0.43%, over a million items.
It is a trade, not an upgrade.

```C#
ulong both   = a.Intersect(b).Count();
ulong either = a.Union(b).Count();
ulong onlyA  = a.Difference(b).Count();
```

Inclusion–exclusion on two cardinality estimators — `|A| + |B| − |A ∪ B|` — is the usual
workaround and it is worthless when the intersection is small, because each term carries an
error proportional to sets far larger than the number being estimated, and the errors do not
cancel. Two sets of 200,000 sharing 500, mean absolute error over five trials:

| | error | true answer |
| --- | --- | --- |
| `ThetaSketch.Intersect` | **38** | 500 |
| inclusion–exclusion | 1,947 | 500 |

Read the error on an intersection carefully even so: it scales with the size of the *sets*
rather than the intersection, so a small enough intersection between large enough sets is
still beyond reach. It is better arithmetic, not a different kind of answer.

Counts are exact while the sketch holds fewer values than it retains.

### `InvertibleBloomLookupTable`

Recovers **which** keys two sets differ by, where `ThetaSketch` tells you only how many.

**Reach for it when** two replicas mostly agree and you need to exchange just the
difference — set reconciliation. The cost is proportional to the size of the
**difference**, not of the sets, which is unlike everything else here.

**Look elsewhere if** you only need the count (`ThetaSketch` is far smaller), or if the
difference might be much larger than you sized for — see below.

```C#
var mine = new InvertibleBloomLookupTable(expectedDifferences: 20, keySize: 8);
foreach (var key in myKeys) mine.Add(key);
// ...theirs is built the same way, elsewhere, and sent over

if (mine.Subtract(theirs).TryDecode(out var onlyMine, out var onlyTheirs))
{
    // the actual keys, not a count
}
```

Two sets of **100,000 keys differing by ten**, reconciled by a table of **360 bytes**.
That is the whole idea: sizing is against the expected *difference*, and sizing it against
the set size would waste almost all of it.

**It can fail, and says so.** If the difference is larger than the table was sized for,
peeling stalls and `TryDecode` returns `false` rather than a partial answer — a partial
reconciliation that looked complete would be far worse, since you would act on it. Size for
more differences than you expect, and treat `false` as "ask for a bigger table" rather than
as an error.

Keys are combined by exclusive-or, so every key must be the same width, fixed at
construction. A key of the wrong size is refused rather than silently corrupting the table.

From Goodrich and Mitzenmacher,
[Invertible Bloom Lookup Tables](https://arxiv.org/abs/1101.2245).

---

## Frequency

### `CountMinSketch`

How often a particular thing has been seen.

**Reach for it when** you want per-item frequencies over a stream too large to keep counts
for, and you want a bound that **never undercounts**. That one-sided error is the point: if
the count feeds a threshold nothing may slip under, this is the structure that guarantees it.

**Look elsewhere if** you want an unbiased estimate rather than an upper bound, or need to
subtract — that is `CountSketch`, below.

```C#
var sketch = new CountMinSketch(epsilon: 0.001, delta: 0.01);
sketch.Add(bytes);
ulong count = sketch.Count(bytes);
```

Described by Cormode and Muthukrishnan in
[An Improved Data Stream Summary](http://dimacs.rutgers.edu/~graham/pubs/papers/cm-full.pdf).

### `CountSketch`

The same question as `CountMinSketch`, answered without the one-sided bias.

**Reach for it when** you are asking about something *rare* in a stream that carries a lot
of weight, or when you need to **subtract**. Each row hashes an item to a cell and to a
sign, so collisions cancel in expectation instead of accumulating.

**Look elsewhere if** you want a bound that never undercounts. Count-Min's bias is a
*guarantee* — if the count feeds a threshold nothing may slip under, that one-sidedness is
the feature and this gives it up. Count-Min is also smaller for the same accuracy on heavy
hitters.

Matched on shape — about 2,700 columns by 5 rows each — and asked about an item seen ten
times among two million observations:

| | error on the rare item | error on a heavy hitter |
| --- | --- | --- |
| `CountMinSketch` | 700 | small |
| `CountSketch` | **100** | small |

Both are fine about heavy hitters. The difference is entirely about the rare one, because
Count-Min's error grows with the *total weight* of the stream while this one's grows with
its Euclidean norm.

```C#
var sketch = new CountSketch(epsilon: 0.01, delta: 0.01);
sketch.Add(bytes);
sketch.Add(bytes, 500);     // weighted
sketch.Add(bytes, -200);    // and removal, which Count-Min cannot do
long count = sketch.Count(bytes);
```

Two things will surprise you if the docs do not say them. **Estimates can be negative** —
it means the true count is near zero and the noise went the other way. And **epsilon means
something different here**: it bounds error against the stream's L2 norm rather than its
L1, so the two sketches size differently for the same number and cannot be compared at
equal epsilon.

From Charikar, Chen and Farach-Colton,
[Finding Frequent Items in Data Streams](https://www.cs.princeton.edu/courses/archive/spring04/cos598B/bib/CharikarCF.pdf).

### `TopK`

The most frequent elements, kept as a running ranking.

**Reach for it when** you want the heavy hitters themselves — top paths, top talkers, top
search terms — rather than the frequency of something you already have in hand.

**Look elsewhere if** you need the count of a *specific* item, which is `CountMinSketch`
directly, or exact ranking, which this does not give.

```C#
var topK = new TopK(0.001, 0.99, k: 25);
topK.Add(bytes);
Element[] top = topK.Elements();
```

**Merging is approximate here in a way it is not elsewhere.** Two sketches can disagree
about what was frequent, and an element genuinely in the top-k of the union can be missing
from both inputs' top-k and therefore from the merge. Merging shards is still useful; it is
not exact.

---

## Similarity

Two structures, two different questions. Picking by whichever you found first will give you
the wrong one.

**`MinHash` answers about sets** — how much do these two collections overlap, by Jaccard
resemblance. **`SimHash` answers about documents** — how alike are these two weighted term
vectors, by cosine similarity, where a term repeated often counts for more.

One input where they disagree completely: a document of 40 "apple" and 2 "banana" against
one of 2 "apple" and 40 "banana". Same *set*, so MinHash calls them identical — correctly,
for its question. SimHash calls them unrelated — correctly, for its.

### `MinHash`

**Reach for it when** the things you're comparing are genuinely sets and repetition should
not count: tags, shingles, feature sets, permissions.

**Look elsewhere if** frequency matters (`SimHash`), or you need to store many signatures —
a k=128 signature is 1,046 bytes against SimHash's 26.

```C#
float resemblance = MinHash.Similarity(bagA, bagB);          // exact Jaccard
MinHashSignature signature = MinHash.Signature(bag, k: 128); // storable, comparable
float estimate = MinHash.Similarity(signatureA, signatureB);
```

`Similarity(string[], string[])` computes exact Jaccard on the two bags. The signature
overload estimates it, with error falling as `k` rises — that is the one to use when
comparing many documents or storing anything.

### `SimHash`

One 64-bit fingerprint per document, compared by Hamming distance.

**Reach for it when** you're finding near-duplicate *documents* at scale and need the index
to fit. 26 bytes stored, 19.5 ns to compare.

**Look elsewhere if** you need to rank moderately similar documents against each other.
Sixty-four bits distinguishes a near-duplicate from a different document — the job — and is
loose in the middle:

| shared terms | true cosine | estimated |
| --- | --- | --- |
| 95% | 0.95 | **0.97** |
| 90% | 0.90 | **0.90** |
| 80% | 0.80 | 0.74 |
| 50% | 0.50 | 0.67 |

Threshold on Hamming distance — a handful of differing bits means near-duplicate — rather
than treating the similarity as a measurement. A slightly negative similarity means
unrelated rather than opposite: term vectors are non-negative, so a true cosine below zero
is impossible and the value is noise around it.

```C#
var a = SimHash.Signature(termsOfDocumentA);
int differingBits = SimHash.HammingDistance(a, b);
float similarity = SimHash.Similarity(a, b);
```

From Charikar,
[Similarity Estimation Techniques from Rounding Algorithms](https://dl.acm.org/doi/10.1145/509907.509965).

### `MinHashIndex` and `SimHashIndex`

Signatures answer "are these two alike". An index answers "which of these million are
worth asking about", without comparing every pair.

```C#
var index = MinHashIndex.ForThreshold(0.8, signatureLength: 128);
foreach (var (id, signature) in corpus) index.Add(id, signature);

foreach (var candidate in index.Query(query))
{
    // now compare properly -- these are candidates, not answers
}
```

**Reach for them when** you are searching a corpus rather than comparing a pair. That is
the difference between a trillion comparisons and a few hundred.

**Look elsewhere if** you have two things and want to know how alike they are. Use the
signatures directly.

> **These are the only structures here whose failure is a *missing* answer.** Everything
> else errs towards saying yes; an index can fail to offer a pair that really is similar,
> and no amount of checking candidates afterwards recovers it — it was never offered.
> `MinHashIndex.RecallAt(resemblance)` tells you how often that happens, and is worth
> reading before trusting a setting.

`ForThreshold` deliberately errs towards **returning too much**. Only the divisors of the
signature length are available, so the curve lands near the threshold rather than on it,
and rounding the wrong way is expensive: at 128 values and a threshold of 0.8, picking the
nearest configuration regardless of side gives **20% recall at the threshold itself**.
Rounding the other way gives 95% and costs some extra candidates, which you discard.

`SimHashIndex` has a guarantee its sibling cannot offer. Cutting a 64-bit fingerprint into
*b* bands means two fingerprints differing in fewer than *b* bits **must** agree on at
least one band — there are only *b*−1 differing bits to spread across *b* bands, so one
band gets none. Within that distance retrieval is certain rather than probable. Eight bands
guarantees everything within seven bits.

Neither index is persistable, deliberately: an index is derived data, rebuildable from the
signatures you already store, and storing it would mean keeping two things in step.

---

## Distributions

### `DDSketch`

What a stream of numbers looks like: the median, the p99, the shape of the tail.

**Reach for it when** you want quantiles over something too large to sort — latencies,
sizes, durations.

**Look elsewhere if** you want the exact quantile of something small enough to sort, in
which case sort it.

```C#
var sketch = new DDSketch(relativeAccuracy: 0.01);
foreach (var latency in latencies) sketch.Add(latency);

double p99 = sketch.Quantile(0.99);   // within 1% of the true p99
```

Its guarantee is on the **value**, not the rank: `Quantile(0.99)` comes back within 1% of
the real 99th percentile. That is what latency measurement wants — "within 1% of the truth"
rather than "within 1% of the right rank", which says nothing about how wrong the number is
when the tail is steep.

Nothing about it is probabilistic. The counts are exact and the buckets are exact ranges, so
the only error is a bucket's width and the accuracy is a hard bound rather than an
expectation. `Merge` is exact for the same reason.

Negative values and zero are fine, `Min()` and `Max()` are exact rather than bucketed, and
memory grows with the *logarithm* of the dynamic range — a stream spanning 10⁻¹²⁰ to 10¹²⁰
fits in well under a megabyte. It is the only structure here that takes numbers rather than
bytes, and so the only one that never hashes.

From Masson, Rim and Lee,
[DDSketch](https://arxiv.org/abs/1908.10693).

---

## Recent data

Every structure above answers about the whole stream since it was created. `SlidingWindow<T>`
answers about the recent past instead, which is how most of these questions are actually
asked: distinct users today, the p99 of the last five minutes, top paths this hour.

```C#
var window = new SlidingWindow<HyperLogLogPlus>(
    window: TimeSpan.FromHours(1),
    buckets: 60,
    create: () => new HyperLogLogPlus(14),
    merge: (a, b) => a.Merge(b));

window.Current.Add(item);              // writes to the bucket covering now
ulong lastHour = window.Merged().Count();   // combines the buckets still in the window
```

**Reach for it when** the age of the data matters. **Look elsewhere if** it does not — a
plain structure is one object rather than sixty.

It is a wrapper rather than a family of windowed structures because so much of this library
merges exactly. A ring of sub-structures combined on query gets the same answer from one
implementation, instead of a paper's worth of work per structure. What it costs is memory —
one structure per bucket — and precision at the edge: **the window is only as sharp as a
bucket is wide**. Sixty buckets over an hour means the boundary is accurate to a minute.

> **Only window a structure whose merge is exact.** Merging is what makes the answer mean
> anything, so an approximate merge gives a window that is wrong in a way no amount of
> bucketing fixes. `TopK` is the one here that qualifies, and it is **refused by name** —
> an element genuinely in the top-k of the whole window can be absent from every bucket's
> own top-k, so combining them would lose it and nothing about the result would look wrong.
> Window a `CountMinSketch` and take the heavy hitters from that.

Pass a clock to the constructor to test a window's behaviour without waiting for it.

---

## Contributions

Pull requests are welcome, but opening an issue is probably the best place to start if you
have a complex critique or suggestion.

[#18](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/18) tracks what is
missing and, as importantly, what has been considered and deliberately left out.
