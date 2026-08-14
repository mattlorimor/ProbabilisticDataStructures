# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [5.0.0] - Unreleased

### Fixed

- **`CuckooBloomFilter` no longer allocates about thirty-two times the space it needs**,
  which is [#47](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/47).
  An empty filter sized for 100,000 items took **122 MB**, against 124 KB for a
  `BloomFilter` of the same capacity and rate. It now takes 3.8 MB.

  Two errors compounded. The fingerprint size was computed with a natural logarithm
  where the relation needs base two, then converted from bits to bytes by a division
  that floors to zero for every rate this library accepts and was clamped to one — so
  every filter got an eight-bit fingerprint whatever rate was asked for. And the bucket
  count was `Power2(n / f * 8)`, which is not a bucket count: the `8` undoes a division
  that had already floored away, leaving roughly eight buckets per item rather than one
  per four.

  The over-allocation was hiding the fingerprint error, since a filter that empty rarely
  finds anything to collide with. Measured false positive rates did not respond to the
  requested rate at all before; they now do, and beat it.

- **A refused cuckoo insert no longer loses an element the filter already held.** When
  relocation runs out of attempts, the filter is holding a fingerprint it displaced out
  of a bucket. It was dropped, which is a false negative — the one thing this filter,
  like every other here, promises not to produce. The displacements are now undone, so a
  refused insert leaves the filter holding exactly what it held before.

  This was unreachable until the sizing above was fixed: the relocation loop never ran
  long enough to give up.

- **`CuckooBloomFilter.Capacity()` describes what it returns.** It reports the count the
  filter was sized for, and was documented as "the number of items the filter can store"
  while the filter would accept thirty to fifty times that.

### Changed

- **`Element.Data` and `Element.Freq` are read-only to callers**, which is
  [#48](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/48). `Data` is
  now `ReadOnlyMemory<byte>`; use `.Span` or `.ToArray()`.

  `TopK.Elements()` hands back the objects the structure holds rather than copies, so a
  caller writing to the array they were given corrupted it: the same array is the key the
  heap is indexed by, and changing its contents changed what it hashes to without the
  index being rebuilt. The element became unreachable and the next arrival of the same
  data was held a second time.

- **`Buckets` and `Buckets64` are internal**, which is
  [#49](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/49). Every
  member on them already was, so as public types they offered a consumer nothing but a
  name in their completion list.

### Added

- **`MinHash.Signature`**, which is
  [#50](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/50). A bag is
  reduced once to a fixed-size signature, and signatures compare in time proportional to
  their length rather than to the bags behind them — so comparing *n* documents pairwise
  costs *n* reductions rather than *n²* full comparisons.

  ```C#
  var a = MinHash.Signature(documentA, k: 128);
  var b = MinHash.Signature(documentB, k: 128);
  float resemblance = MinHash.Similarity(a, b);
  ```

  This one estimates rather than computes; the error is roughly `1/sqrt(k)`. The exact
  `Similarity(string[], string[])` is unchanged and is still the right call when both
  bags are to hand.

  Signatures are persistable and comparable across processes and across versions, because
  the `k` hash functions are a fixed convention — XxHash3 seeded `0` through `k-1` —
  rather than state that has to be carried alongside. The test suite pins the values a
  known bag produces, since changing them would silently invalidate every stored
  signature.

## [4.0.1] - 2026-08-14

### Changed

- **`TopK.Add` no longer costs more as `k` grows**, which is
  [#7](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/7). Deciding
  whether an arriving element was already held meant comparing its data against every
  element in the heap. That was the only cost here that scaled with `k`, and past a few
  hundred it was most of what an add did. The heap is now indexed by element data.

  | k | before | after |
  | --- | --- | --- |
  | 10 | 32.71 ns | 35.81 ns |
  | 100 | 33.71 ns | 35.35 ns |
  | 1000 | 194.92 ns | 56.70 ns |
  | 5000 | 4153.29 ns | 82.44 ns |

  Measured over a stream that cycles through more distinct keys than the heap holds, so
  every add reaches the lookup rather than being turned away before it. A stream with a
  heavy head is rejected earlier and never reached this. Small `k` pays about three
  nanoseconds for hashing a key instead of comparing a handful, which is the trade.
  Allocation is unchanged.

  Behavior is unchanged, including the exact bytes a top-k writes.

## [4.0.0] - 2026-08-14

### Fixed

- **Replacing a populated structure's hash function is refused** rather than silently
  destroying it. `SetHash` on a filter holding 500 items left it reporting 500 items and
  finding none of them: everything stored had been placed by the old hash, and replacing
  it moves none of it, so every lookup goes somewhere else. Nothing raised. The filter
  did not look broken, it looked empty.

  It now throws `InvalidOperationException` unless the structure holds nothing, which is
  the only state the call was ever safe in. Emptiness is derived from what the structure
  holds rather than tracked, so one read back from a payload is not mistaken for
  untouched.

### Added

- **Every constructor accepts a hash function**, which closes
  [#1](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/1). It is an
  optional trailing parameter, so existing calls are unchanged:

  ```C#
  var filter = new BloomFilter(10000, 0.01, hash: myHashFunction);
  ```

  This is now the only way to use a hash for everything a structure will ever hold. A
  scalable filter carries it to the filters it adds as it grows, and a top-k to the
  sketch it holds.

- **`StableBloomFilter` and `CuckooBloomFilter` accept a seed.** Both make random
  choices as part of what they are -- one decrements randomly chosen cells to make room,
  the other evicts a randomly chosen entry when both of an item's buckets are full --
  and both drew from an unseeded generator, so neither could be asserted against, only
  described.

  ```C#
  var filter = new StableBloomFilter(10000, 2, 0.01, seed: 42);
  ```

  Omitting it still seeds unpredictably. This also makes their persisted bytes
  comparable, which is why the format fixtures for those two could previously only be
  checked on reading.

### Changed

- **`SetHash` is a fallback rather than the way to configure hashing.** Prefer the
  constructor. `SetHash` remains for callers who cannot, and is now valid only before
  anything has been added.

## [3.2.0] - 2026-08-14

### Added

- **Structures can be written to a stream and read back**, which is
  [#2](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/2). `WriteTo`
  and `ReadFrom` on each type, `ToByteArray` and `FromByteArray` for callers with no
  stream to hand, and `IBinaryPersistable<T>` for writing persistence code that does not
  name a structure's type.

  Every structure is covered: the seven Bloom variants, the cuckoo filter, the count-min
  sketch, HyperLogLog and top-k.

  A structure held by another -- a top-k's sketch, a scalable filter's contained filters
  -- keeps its own envelope rather than being flattened into the outer payload. It costs
  eighteen bytes each and means the inner structure names its own hash, can be read on
  its own, and can change without the outer layout changing with it.

  The layout is specified in [FORMAT.md](FORMAT.md) and is **stable**: a payload written
  by any version is readable by every later one or is refused with an explanation, never
  guessed at. Payloads written by this version are checked in as test fixtures, so a
  change that would break stored data fails in CI rather than in somebody's storage.

  A payload is refused if its marker is wrong, its format version is later than the
  reader knows, it holds a different structure than the one being read, or its CRC-32
  does not match. The checksum covers the header as well as the payload, so a corrupted
  length or structure id is caught rather than acted on.

  **The hash function is named in the payload rather than assumed.** A structure's
  answers depend entirely on it, and a delegate cannot be written down; a filter read
  back under the wrong hash does not look broken, it looks empty. The identifier names
  the algorithm rather than "the default", because the default is not fixed for all time
  -- this library's was MD5 until 3.0.0. A structure written while using a hash set
  through `SetHash` can only be read by supplying that function again, and an identifier
  the reader does not recognise is refused rather than substituted.

## [3.1.0] - 2026-08-14

### Fixed

- **Filter constructors now validate their arguments and throw
  `ArgumentOutOfRangeException`** instead of failing later, or not at all.

  Three problems, all of which reported something true about the internals and
  nothing about the mistake:

  - A false positive rate of 0, 1, a negative, a value above 1, or `NaN` surfaced as
    an `OverflowException` from a numeric conversion inside the sizing math. A rate of
    exactly 1 was worse: it constructed successfully and produced a filter with zero
    bits and zero hash functions, which silently reported every element as present.

  - Sizing a filter for zero items constructed successfully and then threw
    `DivideByZeroException` on first use, far from the cause. This affected
    `BloomFilter`, `BloomFilter64`, `CountingBloomFilter`, `PartitionedBloomFilter`
    and `CuckooBloomFilter`.

  - `DeletableBloomFilter` splits its bits between a data region and a collision
    region. Passing a collision count at or above the filter's size underflowed the
    `uint` subtraction `m - r`, so `new DeletableBloomFilter(0, 10, 0.01)` silently
    allocated roughly **512 MB** and reported a capacity above four billion.

  Valid arguments are unaffected. `HyperLogLog` and `TopK` already validated theirs;
  this brings the rest of the library in line.

- **`HyperLogLog` threw `IndexOutOfRangeException` at 2^29 registers.** `b` splits the
  hash into a register index and the bits `rho` scans, so it has to be exactly
  `log2(m)`. Deriving it as `Ceiling(Log(m, 2))` is not exact: at `m = 2^29` the
  floating-point logarithm lands just above 29 and the ceiling returns 30, leaving the
  register index able to exceed the array. It is now derived with `BitOperations.Log2`.
  Separately, a register count of zero passed the power-of-two check, because `0 - 1`
  underflows to all ones; it is now rejected. The estimator itself is unchanged.

- **`DeletableBloomFilter` threw `IndexOutOfRangeException` for any collision region
  count that is a multiple of eight**, including `8`, `16`, `32` and `64`. The region
  size was rounded down, so the trailing bits of the data region mapped to region index
  `r` -- one past the last collision bucket. Whether that was fatal depended on
  something unrelated: the collision bitmap allocates whole bytes, and for most values
  of `r` the stray index landed in the leftover padding bits and went unnoticed, but a
  multiple of eight leaves no padding and the write ran off the array. Passing an `r`
  larger than the `m - r` data bits rounded the region size down to zero and threw
  `DivideByZeroException` on the first `Add`. The region size is now rounded up, which
  keeps every index inside the array and non-zero. Stored filter contents are
  unaffected; only which region a bit is attributed to changes.

- **`CountingBloomFilter` removals no longer hide elements that are still present.**
  A counter that reaches its maximum has stopped tracking how many elements it stands
  for, but removals decremented it anyway, resuming the count from the ceiling. Enough
  of those drove it to zero while elements needing it were still in the filter --
  precisely the false negative that a counting filter exists to avoid. Saturated
  counters are now left alone, which costs space rather than correctness: the elements
  covering them become permanently unremovable.

  The effect scaled with how quickly counters saturate. Removing half of 2000 elements
  left **745 of the 1000 survivors unfindable at 1 bit per counter** and 42 at 2 bits.
  The default 4-bit counters showed none at that load, which is why this went unseen.

  Removing an element that was never added still introduces false negatives. That one
  is inherent -- a filter cannot tell such a removal from a real one -- and is now
  documented rather than fixed.


- **`Reset()` clears the item count** on `BloomFilter`, `BloomFilter64` and
  `PartitionedBloomFilter`. All three emptied their buckets and left the count where it
  was, so a filter that was empty by every other measure still reported the items it
  used to hold. `CountingBloomFilter` and `DeletableBloomFilter` already cleared theirs.

  The count is not only reported: `EstimatedFillRatio()` is derived from it, and a
  partitioned filter's is what a scalable filter consults to decide when to grow. A
  filter emptied after 800 additions reported an estimated fill ratio of **44%**.

- **`CountMinSketch` validates `epsilon` and `delta`.** `delta` is the probability that
  an estimate exceeds the error `epsilon` allows, and the matrix depth `ln(1 / delta)`
  is only positive below one. At one and above the matrix had **no rows**, and a sketch
  with no rows did not fail: `Count` minimises over an empty set of rows and returns its
  initial value, so **every element was reported as having been seen 18446744073709551615
  times**. Values at or below zero, and `NaN`, surfaced as `OverflowException` or
  `DivideByZeroException` from the sizing arithmetic. An `epsilon` small enough to need
  a matrix wider than `uint.MaxValue` is now rejected rather than wrapping.

- **`CountMinSketch.Count(null)` and `Merge(null)` throw `ArgumentNullException`.**
  `Count` hashed the empty span and returned a number; `Merge` threw
  `NullReferenceException`.

- **`CountMinSketch.Merge` throws `ArgumentException` rather than the bare `Exception`**
  on a width or depth mismatch, which could not be caught without catching every
  unrelated failure alongside it. The message now names both dimensions and the
  parameter each follows from.

- **`TopK` returns the top k.** Its min-heap was not one. `Pop` removed the root with
  `List.Remove`, which slides every later element down a position instead of restoring
  the ordering, and an element already in the heap had its frequency raised in place
  with no re-ordering at all. The root therefore stopped being the minimum -- and the
  root is both what an arriving element is compared against and what gets evicted, so
  the structure discarded frequent elements and kept rare ones. `Down`, the sift that
  both paths needed, was present and never called.

  Given a stream with distinct frequencies and a sketch wide enough to count it
  exactly, so that the right answer is unambiguous, **89 of 150 configurations returned
  the wrong set**. All 150 are now correct. Reported frequencies were also stale, since
  an element's count was only refreshed on the path the broken ordering skipped.

- **`TopK` copies the data it stores.** It handed the caller's arrays back through
  `Elements()` without copying, so a caller reusing one buffer to add from found every
  entry holding their last write.

- **`new TopK(epsilon, delta, 0)`** throws `ArgumentOutOfRangeException` rather than
  indexing its empty heap on the first `Add`.

- **`InverseBloomFilter` no longer reports data it never saw.** This is the only filter
  here that stores the data rather than only hashing it, and `Test` answers by comparing
  the stored bytes against the query. It kept the caller's array instead of copying,
  so the caller's next write into that buffer changed what the filter held.

  Reusing a single buffer per record -- ordinary, and the reason callers work in bytes
  at all -- left every written slot pointing at the same array, so a value never added
  could be read straight back out of a slot it was never put in. **38.8% of never-added
  values were reported present**, against a structure whose defining property is that it
  never reports a false positive at all. `Add` and `TestAndAdd` now copy. `Test` is
  unchanged and still does not allocate.

- **`new InverseBloomFilter(0)`** throws `ArgumentOutOfRangeException` rather than
  `DivideByZeroException` on first use.

- **Corrected the claim that the inverse filter is thread-safe.** The README said it
  "uses a CAS-style approach, which makes it thread-safe" while its own thread-safety
  section said no filter in the library is. The second is right: Jeff Hodges' original
  swaps the stored value atomically, and this reads and writes the slot in two steps.
  The README also credited the implementation with FNV-1 hashing, which it has never
  used, and explained the absence of thread safety by a `HashAlgorithm` instance the
  filters stopped holding in 3.0.0. Concurrent `Test` calls are in fact safe against
  each other under the default hashing, which is now what the README says.

- **`ScalableBloomFilter` validates its tightening ratio.** The ratio scales each new
  filter's false positive rate down from the last, and the structure's guarantee -- a
  compound rate bounded by `P0 / (1 - r)` -- is a geometric series that only converges
  below 1. A ratio of exactly 1 was accepted and tightened nothing: asking for 1% and
  adding 20,000 items measured **83%**. The filter kept working and quietly stopped
  honoring the rate requested of it. Ratios at or below 0 and above 1 did throw, but
  from inside `Add`, naming `fpRate` -- a parameter the caller had passed correctly.

- **`ScalableBloomFilter.Reset()` keeps a hash function set with `SetHash`.** It
  rebuilds its list of filters from scratch and did not carry the hash across, silently
  restoring the default. Every other filter's `Reset` preserves it.

- **A bucket width of zero is rejected** with `ArgumentOutOfRangeException` rather than
  throwing `IndexOutOfRangeException` on first use. `new CountingBloomFilter(n, 0, r)`
  allocated no storage and then read from it.

### Changed

- **`MinHash.Similarity` returns the resemblance it documents.** Broder's resemblance is
  the Jaccard index -- distinct words in both bags over distinct words in either. This
  returned the Sørensen–Dice coefficient, `2|A∩B| / (|A|+|B|)`, which is related as
  `D = 2J / (1 + J)` and so is consistently higher: **bags of one third resemblance were
  reported at one half**.

  None of the MinHash machinery contributed to that result. It generated `k` hash
  permutations and never used them, because the loop over them ignored its own index,
  and returned agreement over element positions instead. The same input gave the same
  answer on every run despite the random hashes, which is the tell.

  The result is now computed exactly rather than estimated. Broder's estimator earns its
  error when a set is too large to hold or a signature can be reused across many
  comparisons, and neither applies to a call handed both bags in full; estimating would
  cost accuracy and time and buy nothing. A signature-based API, which is where the
  estimator does pay, is being considered alongside serialization.

  Removing the unused permutations also removed their cost: comparing two 400-word bags
  took 163 ms and now takes about 15 µs.

  Two empty bags now return 1 rather than `NaN`, and a null bag throws
  `ArgumentNullException` rather than `NullReferenceException`.

## [3.0.1] - 2026-08-13

### Fixed

- **Passing `null` as data now throws `ArgumentNullException` again.** In 3.0.0 it
  silently succeeded and was indistinguishable from an empty array, because a null
  array converts to an empty span and hashes to the same value.

  This was a regression. Version 2.x threw, because hashing went through
  `HashAlgorithm.ComputeHash`, which rejects null. Moving to span-based hashing
  removed the check without anyone noticing, and nothing in the test suite passed
  null to anything.

  It matters more than a missing argument check usually would: without it, a caller's
  null bug does not surface at the call site. It quietly inserts a phantom element
  that collides with empty input and produces a wrong answer later, elsewhere.

  Empty input remains valid and distinct from null.

### Changed

- **`Buckets` and `Buckets64` now reject a bucket size wider than 8 bits** with
  `ArgumentOutOfRangeException`, rather than accepting it and capping the value at 255.

  A bucket's maximum is stored in a byte, so a wider bucket allocated the extra space
  without being able to hold a larger value -- a 16-bit bucket cost twice the memory
  for no additional range. The bit packing itself handles wider buckets correctly; only
  the maximum could not.

  This resolves a long-standing `TODO` questioning whether the cap was intended. It is:
  upstream Go BoomFilters stores its maximum in a `uint8` too, where the same
  expression wraps to 255 rather than clamping. Both implementations have always
  capped at 255, so this rejects a request neither could honor.

  These are internal types. Reaching the limit requires passing a bucket size above 8
  to `CountingBloomFilter` or `StableBloomFilter`, which would previously have wasted
  memory silently.

## [3.0.0] - 2026-08-13

### Changed

- **The default hash is now XxHash3 rather than MD5, and `SetHash` takes a
  `Func<ReadOnlySpan<byte>, ulong>` instead of a `HashAlgorithm`.**

  Membership tests are roughly **24x faster**, and the Cuckoo filter **67x** because it
  hashes three times per operation. `Bloom_Hit` goes from 249.7 ns to 10.46 ns. Hashing
  was the entire cost of a filter operation; MD5 is built to resist collision attacks,
  which a filter does not need and paid for on every probe.

  This changes where every element lands, so filters persisted by earlier versions
  cannot be read. It also changes the `SetHash` signature on every filter: supply a
  function returning 64 bits rather than a `HashAlgorithm`. A `byte[]` converts to
  `ReadOnlySpan<byte>` implicitly, so call sites passing arrays are unaffected.

  XxHash3 is not resistant to chosen-input attacks: an adversary controlling inserted
  data can provoke collisions and inflate the observed false-positive rate. MD5 was no
  better in this respect, only slower. Callers needing that property should supply a
  keyed hash such as SipHash through `SetHash`.

### Fixed

- **The Cuckoo filter could report false negatives.** `GetComponents` derived the second
  candidate bucket as `hash(fingerprint)` rather than `i1 XOR hash(fingerprint)`.

  The two candidate buckets in a cuckoo filter are a pair: either index must recover the
  other by XOR with the fingerprint hash. The relocation loop already relied on that,
  computing an element's alternate bucket as `i ^ ComputeHashSum32(f)`, but the pairing
  was never established, so an element moved during relocation could land in a bucket
  that `Test` does not examine. Filling a filter to 5,000 insertions reproduced eight
  elements that were reported as added and then could not be found. A cuckoo filter is
  supposed to avoid false negatives entirely.

- **The Cuckoo filter never used its second bucket for insertion.** `Insert` computed the
  second bucket's free slot into a variable named `ids`, then tested `idx` — the result
  from the *first* bucket, which is always `-1` at that point. The condition could never
  be true, so the write was unreachable and every element that did not fit in its first
  bucket went straight to relocation. Measured impact on capacity is small; the cost is
  wasted relocation work rather than lost slots. No compiler warning fires because `ids`
  is assigned from a method call.

### Changed

- The test word list moved from a 5.75 MB C# array to an embedded `words.txt`. The list
  is unchanged, verified by checksum over all 235,886 entries. This affects the test
  project only and is not part of the package.

- **Cuckoo filter element placement has changed**, which is why this is a major release.
  Both fixes alter which buckets an element occupies. Filters persisted by 2.x cannot be
  read correctly by 3.0.0, and a 3.0.0 filter will not agree with a 2.x one. Nothing else
  in the library is affected; the Bloom-family hash kernel is untouched.

### Added

- Nullable reference type annotations on the library. These ship in the package, so
  consumers get null analysis against this API. Notably, `CuckooBloomFilter`'s bucket
  entries are now typed as nullable, which is what they always were, and
  `Element.Data` defaults to an empty array so reading `TopK.Elements()` needs no
  null check.

- Thread-safety documentation on `IFilter` and in the README. Nothing in this library
  is synchronized, matching the Go original. The non-obvious part is that `Test` is not
  safe to call concurrently with *itself*, because filters reuse a single
  `HashAlgorithm` instance and that type is not thread-safe, so a reader-writer lock is
  not sufficient.

- A regression test that fills a Cuckoo filter well past the point where relocation
  begins and asserts that every element it accepted can still be found. The existing
  Cuckoo tests all used filters small enough that relocation never happened, which is
  why both defects survived.

## [2.0.1] - 2026-08-08

### Added

- A package icon. NuGet renders an embedded icon on the package listing and in Visual
  Studio's package manager; without one the package showed a grey placeholder. The
  design is a bit array with some bits set, which is what a Bloom filter is.

  Shipped as a patch release because 2.0.0 is already published and an icon only
  appears on versions packed after it was added — it is not applied retroactively.

## [2.0.0] - 2026-08-08

This release modernizes the entire build. The library had not been touched since June
2018 and could no longer be built or tested with current .NET tooling.

### Changed — package identity

- **The package ID is now `MattLorimor.ProbabilisticDataStructures`.**

  Releases from this repository previously had no package of their own. The unprefixed
  `ProbabilisticDataStructures` ID on nuget.org was registered in 2018 by an account
  unaffiliated with this project, which published this project's source; that package is
  not maintained here and will not receive these releases.

  Rather than contest the name, this project ships under an owner-identifying prefix.
  The prefix also makes `MattLorimor.*` eligible for
  [ID prefix reservation](https://learn.microsoft.com/en-us/nuget/nuget-org/id-prefix-reservation),
  which prevents anyone else from publishing under it.

  **Only the package ID changed.** The assembly and namespace remain
  `ProbabilisticDataStructures`, so `using ProbabilisticDataStructures;` and every type
  name are unaffected. Updating means changing the `PackageReference` include, nothing
  more.

### Removed

- **Dropped `net45` and `netstandard2.0`. The library now targets `net10.0` only.**

  This is a hard break, and it is deliberate. If you consume this library from .NET
  Framework, from Unity, from Xamarin/Mono, or from any .NET Core or .NET 5–9
  application — including .NET 8, which remains in support until November 2026 —
  **2.0.0 will not install**. The older, unaffiliated 1.0.1 package targets `net45` and
  `netstandard2.0` and remains on nuget.org, but it is not published by this project and
  nothing here governs it.

  Note that this costs no operating-system coverage. `netstandard2.0` is an API
  specification, not a runtime, and `net10.0` runs on Windows, Linux, and macOS across
  x64 and arm64. What narrows is the range of .NET *versions* that can consume the
  package, not the platforms it runs on.

  The rationale is honest about what it is not: the code in this release would still
  compile against `netstandard2.0` unchanged, so multi-targeting was not blocked by
  anything technical. It was declined because a second target cannot be verified.
  `netstandard2.0` is not executable, so proving that binary works would require an
  additional test target on a runtime that consumes it, and without one the package
  would ship a second assembly that nothing ever runs. One target that is fully
  exercised on three operating systems was judged better than two where only one is
  tested. `net45` is a simpler case: it reached end of support in January 2016.

  If you need continued .NET Framework, Unity, or .NET 8 support, please open an issue.
  Restoring a `netstandard2.0` target is a decision that can be revisited if there is
  real demand; the work is small, and the reason it was skipped is verification cost
  rather than incompatibility.

- Deleted Visual Studio–era build artifacts that no longer serve any purpose:
  `Default.testsettings`, `ProbabilisticDataStructures.vsmdi`, and
  `TestProbabilisticDataStructures/Properties/AssemblyInfo.cs`.

### Changed

- `Defaults.GetDefaultHashAlgorithm()` now calls `MD5.Create()` instead of
  `HashAlgorithm.Create("MD5")`, which is obsolete as of .NET 7 (`SYSLIB0045`) and throws
  under trimming.

  **Hash output is unchanged**, so filters persisted by 1.x remain readable. This is
  verified by the existing hash-kernel tests, which assert hardcoded digest values.

  Those tests pin the byte-extraction arithmetic against Go BoomFilters' convention.
  They do not establish that filters are interchangeable between the two libraries,
  and they never did: Go hashes with FNV-1a where this library uses MD5.

- Migrated the test project from MSTest v1 to MSTest 4. The old test project was a
  legacy-format `.csproj` bound to Visual Studio's test targets, which meant the suite
  could only be run on Windows from inside Visual Studio. **Tests now run cross-platform
  via `dotnet test`.**

- Test methods now execute in parallel, cutting suite runtime from ~13s to ~5s.

- Replaced the hand-maintained `<Compile Include>` list in the test project with SDK
  globbing, so new test files no longer need to be registered by hand.

### Fixed

- The package version is now declared in the repository (`Directory.Build.props`) rather
  than injected by CI from a build counter. Version numbering restarts from a known
  point: the published 1.0.1 package was built on 2018-06-05 from what was tagged
  `v1.0.9`, so the NuGet version line and the Git tag line had not agreed since 2018.

### Added

- Full package metadata: authors, copyright, description, tags, the Apache-2.0 license
  expression, project and repository URLs, and the README as the package readme. The
  unaffiliated 1.0.1 package had shipped with the SDK's placeholder description,
  `"Package Description"`, and no author or license information.

- Generated XML documentation, packed so consumers get IntelliSense against the library
  rather than bare signatures.

- Source Link with symbols published as a `.snupkg`, so consumers can step into this
  code from their own debugger.

- A tag-driven release pipeline that publishes to NuGet via
  [trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing),
  exchanging a short-lived OIDC token for a temporary API key rather than storing a
  long-lived publishing secret. It refuses to publish when the tag disagrees with the
  declared version.

## [1.0.9] - 2018-06-05

- Improved performance by improving the handling of hashes ([#17](https://github.com/mattlorimor/ProbabilisticDataStructures/pull/17)).

## [1.0.8] - 2018-05-13

- Added `BloomFilter64` to support large Bloom filters ([#16](https://github.com/mattlorimor/ProbabilisticDataStructures/pull/16)).

## [1.0.7] - 2018-04-27

- Build for .NET Standard 2.0 in addition to .NET Framework 4.5 ([#14](https://github.com/mattlorimor/ProbabilisticDataStructures/pull/14)).

## [1.0.0] - 2015-12-31

- Initial release: a C# port of [Tyler Treat's](https://github.com/tylertreat)
  [BoomFilters](https://github.com/tylertreat/BoomFilters) Go project.

[3.1.0]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v3.0.1...HEAD
[3.0.1]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v3.0.0...v3.0.1
[3.0.0]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v2.0.1...v3.0.0
[2.0.1]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v2.0.0...v2.0.1
[2.0.0]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v1.0.9...v2.0.0
[1.0.9]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v1.0.8...v1.0.9
[1.0.8]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v1.0.7...v1.0.8
[1.0.7]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v1.0.0...v1.0.7
[1.0.0]: https://github.com/mattlorimor/ProbabilisticDataStructures/releases/tag/v1.0.0
