# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **`DpswSketch`** and **`PrivateCountMinSketch`**, frequency estimation over a sliding
  window that is differentially private (Wang, Wang and Chen, KDD 2024). The first
  structures here whose contract is a privacy guarantee rather than an error rate.
  `PrivateCountMinSketch` is a Count-Min Sketch whose counters start at a draw from a
  normal distribution rather than at nought — the Gaussian mechanism at the sketch's
  l2-sensitivity, giving event-level zero-concentrated differential privacy — and the
  noise is drawn once at construction, so repeated queries cannot average it away.
  `DpswSketch` cuts the stream into substreams, builds private sketches over nested
  ranges chosen by a smooth histogram, and answers a window query by summing one sketch
  per substream, splitting the budget so that everything covering any one item comes to
  no more than the whole.
  <br><br>
  The privacy guarantee is the authors' theorem and nothing here proves it. What the
  tests hold is the distribution the theorem assumes — variance of depth over budget to
  within five percent, kurtosis within 0.2 of a Gaussian's three, tail masses within a
  point at one, two and three deviations, variance scaling with the budget and the depth
  and not the width — and the structural condition it rests on, that no item is charged
  more than the whole budget. Estimates are two-sided: unlike a plain Count-Min Sketch
  this can read below the truth, and below nought.
  <br><br>
  Small checkpoint factors are refused rather than merely discouraged. The budget for
  the j-th checkpoint falls as the factor to the power of j while the range it covers
  falls only as one minus the factor, so at 0.25 on a window of four thousand the
  leanest sketch carries noise of half a million: over 351 query points the median error
  was -46 and the fifth percentile -6150. Not yet persistable. (#104)

- **`TupleSketch`**, a `ThetaSketch` that carries a value alongside every distinct key,
  so one pass answers both "how many distinct users" and "what did they spend between
  them". The sampling is what makes both possible at once: a hash is kept when it falls
  below the threshold, which happens with probability equal to the threshold, so the keys
  kept are a uniform sample of the distinct keys and the values riding with them are a
  uniform sample of the per-key totals. Where a theta sketch drops a repeated hash, this
  folds the values the repeats carry, by sum, smallest or largest. Union, intersection
  and difference fold the summaries of keys they share. Note that the total carries much
  more error than the count when values are lopsided — the sample is uniform over keys
  and not weighted by value — measured at up to 18% against the count's half a percent
  when one key in a hundred was worth a thousand times the rest. (#106)

- **`SetSketch`**, one sketch that estimates both how many distinct elements a set holds
  and how much two sets have in common (Ertl, VLDB 2021). `HyperLogLog` does the first in
  very little room and cannot do the second; MinHash does the second and spends four or
  eight bytes on every component. This does both from the same registers, and a base
  parameter dials between them: towards one the registers grow fine and it behaves like
  MinHash, towards two they coarsen towards HyperLogLog, and cardinality estimation barely
  notices either way. Registers combine under maximum, so merging is exact rather than
  approximate — a merged sketch is register-for-register the sketch that adding both sets
  would have built — and adding the same element twice changes nothing. Cardinality error
  ran between 0.4% and 2.4% across seven orders of magnitude against the 1.56% that 4,096
  registers implies. Its joint estimator beat the obvious one built from inclusion and
  exclusion by between 1.2 and 2.1 times at every similarity tried, and beat MinHash's own
  estimator with the same number of components on sets of different sizes — 0.0029, 0.0034
  and 0.0038 against 0.0034, 0.0047 and 0.0056 — because MinHash counts only the registers
  that agree while this also uses which way the disagreeing ones fall. Estimates Jaccard,
  cosine, both inclusion coefficients, and the sizes of the union, intersection and both
  differences. (#105)

- **`SublimeCountMinSketch`**, per-item frequencies for a stream whose length is not known
  in advance (Eslami, Bercea, Pagh and Dayan, SIGMOD 2026). A `CountMinSketch` has to be
  sized before it has seen anything, and its error then grows in step with the stream.
  This one starts at a single cache line per row and doubles as the stream grows, holding
  about the square root of its length, so the error grows as the square root too;
  expansion copies each array onto itself, so a key that hashed to a counter before an
  expansion hashes to that counter or its copy afterwards and its count survives. Paying
  for that in memory would defeat the point, so a count keeps its low bits in a short stub
  and spends on a variable-length extension only when it outgrows one — packed alongside
  its neighbours in the same cache line, in base three so that the one spare two-bit
  pattern can mark where each one ends. The two parameters this depends on are retuned as
  the sketch grows, using Chebyshev's inequality on the spread of extension lengths to
  reject any tuning that would push more than three chunks in a hundred onto their
  fallback. Counters settle at twelve to fourteen bits each against `CountMinSketch`'s
  sixty-four. Given the same bytes and rows on the same skewed stream, it was out by 263
  on average against a fixed sketch's 684 at a hundred thousand events, and 830 against
  2,811 at a million — a margin that widens as the stream runs on. Supports deletions,
  which `CountMinSketch` does not, and folds its arrays back in half once most of a
  stream has been deleted, subtracting out a record of each expansion so that the counts
  gathered before one are not counted twice on the way down. Persists as counts rather
  than as its packed layout, so a payload survives any change to how counters are
  packed. (#103)

- **`MementoFilter`**, range emptiness for a set that keeps changing (Eslami and Dayan,
  SIGMOD 2025). `Grafite` answers the same question but is built once and never updated;
  this one inserts, deletes and grows, which is what makes a range filter usable behind a
  B-tree. Each key is split into a prefix, which names a block, and a memento, the key's
  own low bits giving its position inside that block. Storing the positions rather than
  only the blocks is what lets it answer "empty" for a range that merely brushes occupied
  data. Measured at 0.265% false positives on random empty ranges and 0.213% on ranges
  placed immediately after each key -- the correlated workload that collapses heuristic
  range filters does nothing here, because a memento cannot be gamed by choosing keys.
  Keys sharing a block share a stored fingerprint, so the same 256,000 keys cost 41.0
  bits each spread out and 20.5 packed. (#101)

- **`InfiniFilter`**, a filter that grows with the data instead of being sized for it
  (Dayan, Bercea, Reviriego and Pagh, SIGMOD 2023). Where `ScalableBloomFilter` handles
  growth by stacking filters whose error rates add up, and `QuotientFilter` refuses once
  full, this doubles one table in place: each entry pays for the extra address bit by
  giving up one of its own fingerprint bits, so nothing is rehashed and the original keys
  are never needed. A per-slot age counter means only the entries present for an
  expansion pay for it, which keeps the false positive rate growing with the logarithm of
  the item count rather than with the count — measured at 0.32%, 0.78% and 1.29% across a
  hundredfold increase. Entries that run out of fingerprint move to a smaller table that
  expands and sheds in turn, so the filter can keep growing indefinitely. Supports
  deletes, which `ScalableBloomFilter` does not. (#100)

- **`Grafite`**, a static range filter that answers "is anything in [a, b]?" (Costa,
  Ferragina and Vinciguerra, SIGMOD 2024). Nothing here answered that before, and the
  range filters that do bound their false positive rate empirically, on workloads that
  do not resemble the keys; point the queries near the data, as real ones are, and those
  rates collapse. Grafite's bound is a theorem about its hash and holds for any query
  sequence, including one chosen by someone who has seen the keys but not the seed. Keys
  are numbers rather than bytes, and the filter is built once from a known set like
  `BinaryFuseFilter`. Stored in Elias–Fano at 14.6 bits per key for a 1% rate over
  64-key ranges. Diverges from the authors' reference implementation by splitting a
  query at block boundaries: the reference tests a straddling range as one interval,
  which can report an occupied range empty about once in r such queries. (#99)

- **`UltraLogLog`**, distinct counting that keeps what HyperLogLog discards (Ertl,
  VLDB 2024). A HyperLogLog register remembers only the largest update value that
  landed on it; this one also keeps two bits saying whether the positions just below it
  were reached, which is worth 43% of the memory at equal accuracy against this
  library's `HyperLogLog` — 24% against one that packs its registers into six bits.
  Registers store an absolute bit position rather than a run length, so a sketch built
  at a fine precision and merged into a coarser one is byte for byte the sketch that
  precision would have built: sketches gathered at different precisions combine without
  prior agreement, and a sketch can be shrunk after the fact. The estimator is Ertl's
  FGRA, whose coefficients are computed from the one parameter they derive from rather
  than transcribed. The martingale estimator is deliberately omitted: it does not
  survive a merge. (#98)

- **`VarOpt`**, a weighted sample of a stream that answers questions written after the
  stream is gone (Cohen, Duffield, Kaplan, Lund and Thorup, SODA 2009). Every other
  structure here fixes its question at ingest; this one keeps k items with adjusted
  weights, and summing those weights over any predicate estimates that subset's true
  weight without bias and at the minimum variance any k-item sample can achieve. The
  total is not estimated at all: evictions hand the evicted weight to the survivors, so
  the sampled weights sum to exactly the weight that went in. Merging follows the
  paper's recurrence, which asks that every input keep at least as many items as the
  result rather than that the k's match: a larger sample merges into a smaller one, an
  unfilled sample merges into anything, and only the direction that would report
  sampled items as exact is refused. (#97)

- **`HeavyKeeper`**, the top-k of a stream found by contest rather than accounting
  (Gong, Yang et al., USENIX ATC 2018): buckets hold one element's fingerprint and
  count, mismatched arrivals decay the incumbent with probability b^-C, and the rare
  lose. Errors are one-sided in the opposite direction from Count-Min's — absent
  fingerprint collisions an estimate never exceeds the truth — and tracked heavy
  hitters are counted nearly exactly. It does not merge; that is the price of the
  contest, and `TopK` remains the structure for shards. Implements the authors'
  reference inequality where Algorithm 1 as printed deadlocks admission, a
  discrepancy recorded in the class documentation. (#96)

### Fixed

- Six persistence tests were passing on the checksum rather than on what they claimed to
  check. `SetSketch`, `SublimeCountMinSketch` and `TupleSketch` each had tests that
  corrupted a field and expected the reader to refuse it, but none repaired the trailing
  CRC, so the frame was rejected before the guard was ever reached — and the guard could
  be removed entirely without any of them failing. They now live in the hostile-payload
  suite, which repairs the checksum so that a guard is what has to refuse them.

## [6.1.0] - 2026-08-16

### Added

- Every method that takes a `byte[]` now has a `ReadOnlySpan<byte>` overload beside it
  -- 49 of them. Callers with data already in a buffer can query without copying into
  a fresh array first; pure queries such as `Test` and `Count` allocate nothing at all.
  `InverseBloomFilter` and `TopK` still copy where they retain what they are given, and
  say so in their documentation. (#87)

### Changed

- `CuckooBloomFilter` packs its fingerprints at the exact width the false-positive rate
  requires instead of rounding each up to a whole byte, cutting fingerprint memory by
  around a third at typical rates -- 262 KB to 164 KB for 100,000 items at 1%. The rate
  the filter delivers moves closer to the rate requested as a result (7.8e-3 against a
  requested 1e-2, where byte alignment delivered 1.2e-4): this is the accuracy that was
  asked for, not a regression, and the overshoot can no longer exceed 2x. Payloads
  written by earlier versions still load, restoring at eight bits per stored byte,
  which is the same filter they always were. A cuckoo payload written by 6.1.0 uses a
  new format version, so an earlier release cannot read it -- the compatibility this
  library promises runs backwards, not forwards. (#88)

## [6.0.1] - 2026-08-16

### Fixed

- `ThetaSketch` could write a payload its own reader refused. An `Add` whose
  compaction trimmed the buffer checked the hash against theta before the trim
  lowered it, so one value at or above theta could be stored -- a sample the
  sampling rate was never applied to, and one the persistence reader rightly
  rejects. Roughly half of the sketches that ever crossed their trim threshold
  carried one. The hash is now re-checked after compaction, and the reader
  accepts (and drops) exactly one trailing out-of-range value so that every
  payload 6.0.0 wrote still loads.
- `CountMinSketch` and `TopK` documented `delta` backwards -- as the probability the
  estimate holds rather than the probability it fails. The sizing arithmetic was
  always right; the XML docs now agree with it. A delta of 0.99 is a one-row sketch,
  not a 99%-confident one.

### Added

- `TESTING.md`, documenting the testing methodology the 6.x hardening developed, and
  a Stryker configuration for scoped mutation sweeps.
- README: a table of contents, an introduction for each category of structure, and
  the per-thread-plus-merge concurrency pattern with the exact-merge guarantee that
  backs it.
- Seventy-seven new tests: statistical-guarantee bounds, sizing-formula geometry,
  byte-exact merge identities, model-based oracles, and numerical boundary cases.

## [6.0.0] - 2026-08-15

### Added

- **`SlidingWindow<T>`**, so questions can be asked about recent data rather than about
  everything since the structure was made, which is
  [#75](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/75). It holds one
  structure per time bucket and combines the live ones on demand.

  A wrapper rather than a family of windowed structures, because 5.1.0 and 6.0.0 made most
  of the library merge exactly -- a ring of sub-structures gets the same answer from one
  implementation instead of a paper's worth of work per structure. It costs memory, one
  structure per bucket, and precision at the edge: the window is only as sharp as a bucket
  is wide.

  **`TopK` is refused by name.** Its merge is approximate -- an element genuinely in the
  top-k of the whole window can be absent from every bucket's own top-k -- so a window over
  it would drop elements as buckets rolled and nothing about the result would look wrong.
  The exception says to window a `CountMinSketch` instead.

  The clock is injectable, which is what makes a window's behaviour testable without
  waiting for it.

- **`MinHashIndex` and `SimHashIndex`**, so that the signatures this library produces can
  be searched rather than only compared, which is
  [#74](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/74). Without an
  index, finding near-duplicates among a million documents is a trillion comparisons; #62
  noted at the time that this was probably worth more than another fingerprint.

  **They are the first structures here whose failure is a missing answer.** Everything else
  errs towards saying yes. An index can fail to offer a pair that really is similar, and
  checking the candidates afterwards does not recover it. `RecallAt` exposes how often that
  happens rather than leaving it to be discovered.

  `ForThreshold` errs towards returning too much for the same reason. Only the divisors of
  the signature length are available, so the S-curve lands near the requested threshold
  rather than on it -- and at 128 values and a threshold of 0.8, choosing the nearest
  configuration regardless of side gives **20% recall at the threshold itself**. Choosing
  the other side gives 95% and costs extra candidates the caller discards.

  `SimHashIndex` carries a guarantee its sibling cannot: cutting 64 bits into *b* bands
  means anything differing in fewer than *b* bits must share a band, so retrieval within
  that distance is certain rather than probable.

  Neither is persistable, deliberately -- an index is derived data, rebuildable from the
  signatures already stored. `SimHashSignature`'s constructor is now public, so a
  fingerprint held elsewhere can be wrapped and queried.

- **`BloomierFilter`**, an approximate key-to-value map, which is
  [#78](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/78). Chazelle,
  Kilian, Rubinfeld and Tal (2004), built on the peeling construction `BinaryFuseFilter`
  uses. It stores a value per key without storing the keys, which is why it is smaller
  than a dictionary and why the set is fixed at construction.

  **One deliberate departure from the paper.** The classic form returns an arbitrary value
  for a key it was not built from, with no way to tell that from a real answer -- a wrong
  answer that looks right, which is a sharper edge than anything else here has. This stores
  an 8-bit fingerprint beside each value so an absent key is rejected instead, at 2^-8 per
  lookup. One byte per cell, and the failure becomes the bounded kind the rest of the
  library deals in.

  A key appearing twice with different values is refused, since a map cannot hold both and
  this one could not tell you which it dropped. A value too wide for the declared width is
  refused rather than truncated. Persistence takes structure id 22.

- **`InvertibleBloomLookupTable`**, which recovers *which* keys two sets differ by, and is
  [#76](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/76). Goodrich and
  Mitzenmacher (2011). `ThetaSketch` can tell you two sets differ by about ten; this hands
  you the ten keys.

  Two sets of 100,000 keys differing by ten reconcile through a table of **360 bytes**,
  because the sizing is against the expected difference rather than the set size. Nothing
  else here is sized that way.

  It can fail and says so: if the difference exceeds what the table was sized for, peeling
  stalls and `TryDecode` returns false rather than a partial answer. A partial
  reconciliation that looked complete would be far worse than a refusal, since the caller
  would act on it. Keys are combined by exclusive-or so they must all be the same width,
  fixed at construction, and one of the wrong size is refused rather than quietly
  corrupting the table. Persistence takes structure id 21, with signed counts -- a
  subtracted table is mostly negative.

- **`CountSketch`**, frequency estimation without Count-Min's one-sided bias, which is
  [#77](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/77). Charikar,
  Chen and Farach-Colton (2002). Each row hashes an item to a cell and to a sign, so
  collisions cancel in expectation rather than accumulate.

  Matched on shape against a `CountMinSketch` and asked about an item seen ten times among
  two million observations, Count-Min was off by **700** and this by **100**. Both are
  accurate about a genuine heavy hitter, and there is a test asserting that too -- the
  point is not that Count-Min is worse, it is that Count-Min is worse at one thing.

  It also supports removal, which Count-Min cannot: a negative update is just an update.
  Estimates can come back negative, meaning the true count is near zero and the noise went
  the other way, and its epsilon bounds error against the stream's L2 norm rather than its
  L1 -- so the two cannot be compared at equal epsilon, and the README says so. Persistence
  takes structure id 20, with signed cells.

- **A filter sized past what 32 bits can address now says so.** `BloomFilter`,
  `CountingBloomFilter`, `PartitionedBloomFilter` and `DeletableBloomFilter` address at
  most 4.29 billion bits, which is about 448 million items at 1% and fewer at a tighter
  rate. Asking for more arrived as `OverflowException: Value was either too large or too
  small for a UInt32` from inside the sizing arithmetic -- the same defect 3.0.0 fixed for
  the false positive rate, and the same complaint: it reported something true about the
  machinery and nothing about the mistake. It is now an `ArgumentOutOfRangeException` that
  gives the bits needed against the bits available, and names `BloomFilter64` and
  `ScalableBloomFilter` as the two ways out.

- **`CuckooBloomFilter.SizeInBytes()`**, reporting the storage the filter actually holds.
  `Capacity()` reports how many items it is sized for, which is a different number and was
  the only one available -- so the README's comparison of the membership structures could
  not be written from the public API. `BinaryFuseFilter`, `QuotientFilter`, `ThetaSketch`
  and `HyperLogLogPlus` already had this.

- **`SimHash`** and **`SimHashSignature`**, for near-duplicate detection, which is
  [#62](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/62). Charikar
  (2002). One 64-bit fingerprint per document, compared by Hamming distance.

  It sits beside `MinHash` and answers a different question, which the README now states
  before anything else, because the two are easy to pick between by accident. MinHash
  answers about **sets**, by Jaccard resemblance. SimHash answers about **documents**, by
  cosine similarity over weighted term vectors, where a term repeated often counts for
  more. A document of 40 "apple" and 2 "banana" against one of 2 "apple" and 40 "banana"
  holds the same set, so MinHash calls them identical and SimHash calls them unrelated.
  Both are right about their own question, and the test suite asserts exactly that pair.

  The other difference is size. For one 500-term document a SimHash fingerprint stores in
  **26 bytes** against a k=128 MinHash signature's 1,046, and compares in 19.5 ns against
  37.0 ns.

  Accuracy is documented for what it is rather than advertised: 64 bits distinguishes a
  near-duplicate from a different document, and does not rank two moderately similar
  documents against each other. At 90% shared terms the estimate is 0.90; at 50% it is
  0.67 against a true 0.50. The README says to threshold on Hamming distance rather than
  treat the similarity as a measurement.

  As with a MinHash signature, the hash is fixed by convention rather than chosen, and the
  suite pins the fingerprint a known document produces. Persistence takes structure id 19.

- **`ThetaSketch`**, which estimates distinct counts and can intersect them, and is
  [#61](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/61). Union,
  intersection and difference, all of which produce another sketch.

  Intersection is the point. Two cardinality estimators can be forced to answer "how many
  were in both" by inclusion-exclusion, and the answer is worthless when the intersection
  is small, because each term carries an error proportional to sets far larger than the
  number being estimated and the errors do not cancel. Two sets of 200,000 sharing 500,
  mean absolute error over five trials: **38 for a direct intersection against 1,947 for
  the arithmetic**, on a true answer of 500.

  It is a trade rather than an upgrade, and the README says which to reach for. At
  comparable accuracy over a million items it costs sixteen times the memory of
  `HyperLogLogPlus`: 262,144 bytes at 0.37% against 16,384 at 0.43%.

  Values are kept in a sorted buffer rather than a hash set, which costs the values
  themselves and nothing per value besides -- a hash set spent three times as much on
  entry overhead, which matters precisely because memory is this structure's weak side.
  It also makes the three set operations linear merges rather than lookups. Counts are
  exact while the sketch holds fewer values than it retains. Persistence takes structure
  id 18.

- **`QuotientFilter`**, a membership filter that both deletes and merges, which is
  [#59](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/59). Bender et
  al. (2012). Nothing else here does both: the cuckoo filter deletes but cannot merge,
  and the Bloom family merges but cannot delete.

  Measured at n = 100,000, retained bytes:

  | | bits/item | hit | miss | measured fp | delete | merge |
  | --- | --- | --- | --- | --- | --- | --- |
  | `QuotientFilter` | 26.2 | 63.6 ns | 20.4 ns | 0.284% | yes | **yes** |
  | `CuckooBloomFilter` | 23.6 | 34.6 ns | 40.2 ns | 0.012% | yes | no |
  | `BloomFilter` | 9.6 | 58.9 ns | 21.4 ns | 1.000% | no | yes |

  That comparison is the one [#59](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/59)
  asked for and it only became answerable after 5.2.0, which fixed the cuckoo filter's
  representation. The answer is that **merging is the whole of what it buys**: against a
  cuckoo filter it is slightly larger, faster on misses, slower on hits, and worse at the
  same nominal rate. The README says so plainly rather than leaving it to be discovered.

  Memory per item is not a single number. The table is a power of two and is sized to
  stay under 75% load, so a filter sized just past a power of two costs twice one sized
  just under it: 13.4 bits per item at n = 98,000 against 26.2 at n = 100,000. The false
  positive rate moves the other way, being roughly the load times `2^-remainder bits`.

  Every addition is stored, including a repeat, so it takes as many removals to empty an
  item out as it took additions to put it in. Persistence takes structure id 17.

- **`HyperLogLogPlus`**, a better distinct-count estimator, which is
  [#58](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/58). It sits
  alongside `HyperLogLog` rather than replacing it: replacing it would change the number
  an existing estimator answers with, including one read back from a payload written
  years ago.

  **The whole 64-bit hash is used.** The older estimator keeps only the low 32 bits, so
  two items whose hashes agree there are one item as far as it can tell. Hashing
  consecutive integers finds such a pair within 67,297 of them, and the test suite pins
  it.

  **Small counts are exact**, because the estimator keeps the hashes themselves until
  registers would be cheaper. Ten items is `10`, in 107 bytes rather than 16 KB.

  **The older estimator's worst band is gone.** It switches from linear counting to the
  raw estimate at 2.5m and is at its worst where it changes over — mean absolute error
  over 20 streams at 2^14 registers is 2.44% there against a nominal 0.81%, and it stays
  above nominal until about 4m. The new one holds 0.6-0.7% straight through.

  It reaches that without the paper's tables of measured bias, using Ertl's estimator
  (2017) instead: it accounts for the registers at each end as terms rather than as
  cases to switch on, so it needs no threshold and has no band between thresholds to be
  worst in. Persistence takes structure id 16, and the payload says which representation
  it holds.

- **`DDSketch`**, which answers what a stream of numbers looks like, and is
  [#60](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/60). Masson,
  Rim and Lee (2019). Nothing here answered anything about a distribution before this:
  the median, the p99, the shape of a tail.

  Its guarantee is on the **value** rather than the rank — `Quantile(0.99)` returns
  within the relative accuracy of the true 99th percentile, which is the guarantee
  latency measurement actually wants. Nothing about it is probabilistic: the counts are
  exact and the buckets are exact ranges, so the accuracy is a hard bound rather than an
  expectation, and `Merge` is exact rather than approximate.

  It is the first structure here that takes numbers rather than bytes, and so the first
  that never hashes. There is no `SetHash`, and its payload records hash id 2, `None`,
  rather than naming a hash it does not use — reading one with a supplied hash function
  is refused rather than ignored. Negative values and zero are held; `Min()` and `Max()`
  are exact rather than bucketed; memory grows with the logarithm of the dynamic range.
  Persistence takes structure id 15.

- **`BinaryFuseFilter`**, the first structure here whose set is fixed at construction,
  which is [#57](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/57).
  Graf and Lemire's binary fuse filter (2022). There is no `Add` and there cannot be:
  building it solves a system of equations over the whole set at once.

  Measured over a million keys against a `BloomFilter` at the same false positive rate:

  | | binary fuse | Bloom |
  | --- | --- | --- |
  | bits per entry | **9.04** | 11.54 |
  | lookup | **5.4 ns** | 50.5 ns |

  Three memory accesses and one hash, against a loop over eight hash functions. It does
  not implement `IFilter`, whose contract is mostly about adding, and it has no
  `SetHash` -- the set is hashed during construction, so a hash chosen afterwards could
  not apply to anything the filter already holds. It is passed to `Build` instead.

  The false positive rate is fixed by fingerprint width rather than chosen freely:
  `BinaryFuseWidth.Eight` gives 2^-8 and `Sixteen` gives 2^-16. Passing a target rate
  picks the narrower width meeting it, and a rate no width can reach is refused rather
  than quietly capped. Builds are deterministic, so a filter can ship as a build
  artifact. Persistence takes structure id 14.

### Fixed

- **A restored `StableBloomFilter` or `CuckooBloomFilter` resumes the random sequence it
  was partway through**, rather than starting over. Both filters accepted a `seed` from
  4.0.0 onwards, and neither carried it through serialization: the filter came back with
  a fresh `System.Random`, so a seeded filter stopped being reproducible the moment it
  was written to a file. Nothing tested it.

  Storing the seed would not have been enough. The bits come back correct either way, but
  a filter re-seeded on read sits at the start of its sequence while the original sits
  wherever its adds left it. The case that makes this matter is the one persistence exists
  for: a filter checkpointed on a schedule would replay the same first draws after every
  load, and the stable filter's bound on its false positive rate assumes its decay is
  spread across cells rather than aimed at the same ones after every restart.

  The tests for this assert against a filter that was **never written out**. Two filters
  restored from the same payload agree under either design, so the comparison one would
  naturally write is the comparison this defect passes.

### Changed

- **`StableBloomFilter` and `CuckooBloomFilter` draw from SplitMix64 instead of
  `System.Random`**, whose position cannot be read out of it and so cannot be stored.
  Both filters remain deterministic for a given seed, but **the sequence a seed produces
  has changed**. Code that pinned exact values for a given seed will see different ones;
  code that only relies on a seed being reproducible is unaffected. This is the whole of
  the breaking change.

- **The persistence format's version now travels per payload.** `StableBloomFilter` and
  `CuckooBloomFilter` write version 2, carrying the generator state as a trailing `u64`.
  The other eleven structures still write version 1 and remain readable by 3.1.0 onwards.
  Raising every structure's version together would have made every payload unreadable to
  5.x to record a change that eleven of them did not have.

  Version 1 payloads of both filters still read, and are pinned by fixtures alongside the
  new version 2 ones. Such a filter resumes with an unpredictable generator: its cells or
  fingerprints are exactly right, and only the sequence of choices it will make next is
  unrecoverable, because it was never written down.

### Added

- **Tests for the format guards the corruption sweep cannot reach.** That sweep flips
  single bits and leaves the trailing CRC alone, so every one of its cases dies at the
  checksum — it proved the checksum works and nothing behind it. All six `MaxNestedCount`
  guard sites, the version-zero check and the oversized-length check were unexercised.
  The new tests repair the CRC after editing, so a guard is what has to refuse them.

- **The every-structure persistence sweeps now cover every structure.** `CountMinSketch`
  has `SetHash` but was absent from the hash-substitution sweep; it, `BloomFilter` and
  `MinHashSignature` were absent from the corruption sweep. Adding the signature to the
  read-as-another sweep also makes its structure-id distinctness assertion cover all
  thirteen ids rather than twelve.

## [5.2.0] - 2026-08-14

### Changed

- **`CuckooBloomFilter` stores its fingerprints in one packed array**, which is
  [#52](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/52). They were
  individual `byte[]` instances in a jagged array, so every two-byte fingerprint carried a
  24-byte object header and every bucket was an array of references besides.

  | n = 100,000 at 1% | before | after |
  | --- | --- | --- |
  | empty | 3,847 KB | **291 KB** |
  | holding 100,000 items | 11,501 KB | **291 KB** |
  | `Test` | 43.0 ns | **31.3 ns** |

  Storage is now allocated once and does not depend on load, which is why the two rows
  agree. 5.0.0 fixed the filter's sizing; this fixes what the sizing was spent on.

  Occupancy is tracked in a separate bit per entry rather than by treating an all-zero
  fingerprint as empty. That would be smaller, but a fingerprint comes from a hash and
  can legitimately be all zero, so it would need forcing to something else — which
  changes which bucket an element's alternate index resolves to, and so could not be
  applied to fingerprints already written by an earlier version. Six percent of the
  fingerprint bytes buys reading every payload ever written, unchanged.

- **Relocation no longer allocates per attempt.** Displacing a fingerprint copied it to a
  fresh array, up to 500 times per insert and again to unwind, so an insert into a full
  filter allocated **32 KB**. It now allocates 32 bytes, the same as an insert with room
  to spare.

### Fixed

- **The fingerprint width is capped at eight bytes.** The hash supplies 64 bits, so a
  wider fingerprint was storage reserved for bits that never arrived. Only reachable at
  false positive rates below about 1e-27.

## [5.1.0] - 2026-08-14

### Added

- **`Merge` on `BloomFilter`, `BloomFilter64` and `PartitionedBloomFilter`**, which is
  [#53](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/53). Two
  filters with the same dimensions and hash union by OR-ing their bit arrays, giving
  exactly the filter that adding everything to one of them would have produced.

  Verified by building 20,000 elements across 8 shards and merging: **zero disagreements
  with a single filter built from all of them, over 200,000 absent keys**, and the same
  measured false positive rate.

  The item count becomes the sum, which overstates the union whenever the inputs shared
  elements. There is no way to know how many they shared, so a merged filter's count is
  an upper bound.

- **`Merge` on `CountingBloomFilter`**, which is
  [#54](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/54). Counters
  add rather than max, so a merged filter is removable from as many times as its inputs
  together were. Sums hold at the counter maximum, which keeps the rule 3.1.0
  established -- a saturated counter is never decremented -- and means merging can make
  an element permanently unremovable when neither input had.

- **`Merge` on `TopK`**, which is
  [#55](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/55).
  Frequencies are re-read from the merged sketch rather than added, since each
  structure's recorded frequency is what its own sketch last told it and adding them
  double-counts everything both already knew.

  A merged top-k is not necessarily the true top-k of the combined stream: only elements
  one of the heaps was holding are candidates, so an element frequent in both but top in
  neither stays invisible. That is inherent to merging bounded summaries, and there is a
  test pinning it.

### Fixed

- **`CountMinSketch.Merge` and `HyperLogLog.Merge` check that both were built with the
  same hash function.** Neither did. Everything a structure holds sits where its own hash
  put it, so merging two that hash differently produced something answering confidently
  about positions neither meant -- and nothing about the result looked wrong afterwards.

  `HyperLogLog.Merge` also rejects null rather than throwing `NullReferenceException`.

### Notes

Delegates compare by method and target, so the default hash, a method group, and one
delegate passed to both constructors all compare equal. Two separately written lambdas
with identical bodies do not, and are refused: pass one hash function to both structures
rather than writing it out twice.

The new methods return the receiver so calls can chain, following `Add` and `Reset`.
`CountMinSketch.Merge` and `HyperLogLog.Merge` return a `bool` that is always `true`,
which is left alone rather than changed under anyone.

## [5.0.0] - 2026-08-14

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

[Unreleased]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v6.1.0...HEAD
[6.1.0]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v6.0.1...v6.1.0
[6.0.1]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v6.0.0...v6.0.1
[6.0.0]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v5.2.0...v6.0.0
[5.2.0]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v5.1.0...v5.2.0
[5.1.0]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v5.0.0...v5.1.0
[5.0.0]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v4.0.1...v5.0.0
[4.0.1]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v4.0.0...v4.0.1
[4.0.0]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v3.2.0...v4.0.0
[3.2.0]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v3.1.0...v3.2.0
[3.1.0]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v3.0.1...v3.1.0
[3.0.1]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v3.0.0...v3.0.1
[3.0.0]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v2.0.1...v3.0.0
[2.0.1]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v2.0.0...v2.0.1
[2.0.0]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v1.0.9...v2.0.0
[1.0.9]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v1.0.8...v1.0.9
[1.0.8]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v1.0.7...v1.0.8
[1.0.7]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v1.0.0...v1.0.7
[1.0.0]: https://github.com/mattlorimor/ProbabilisticDataStructures/releases/tag/v1.0.0
