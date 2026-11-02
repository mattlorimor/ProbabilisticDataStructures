# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **`ReadOptions`, a memory budget for reading.** Every structure gains
  `ReadFrom(Stream, ReadOptions)` and `ReadFrom(Stream, Func<ReadOnlySpan<byte>, ulong>,
  ReadOptions)`, and `Persistence.FromByteArray<T>` the same two. The budget is the most
  a read may allocate, payload and structure together and nested structures included;
  `ReadOptions.Default` is `ReadOptions.DefaultMemoryBudget`, one gibibyte, and
  `ReadOptions.Unlimited` sets none. A payload over budget is refused before the
  allocation it would have needed, with an `InvalidDataException` whose inner exception is
  a new `ReadBudgetExceededException` carrying the bytes needed and the budget.
  `InvalidDataException` is sealed, so the refusal cannot be a subclass of it; existing
  `catch` blocks catch it unchanged.
  <br><br>
  `IBinaryPersistable<TSelf>` gains the two overloads as static *virtual* members whose
  default throws `NotSupportedException`. A type outside this library implementing the
  interface goes on compiling and loading without them, and a budgeted read of it says so
  rather than quietly reading without a budget.

- **`ScalableBloomFilter` can grow its contained filters.** A new constructor,
  `ScalableBloomFilter(uint hint, double fpRate, double r, uint growth, Func<ReadOnlySpan<byte>, ulong>? hash = null)`,
  builds each contained filter for `growth` times the items of the one before: filter i
  holds hint × growthⁱ, at fp × rⁱ as before. That is the growth Almeida et al. describe
  beside the tightening. Until now every contained filter was built for the hint, so the
  number of filters grew with the items added, every `Test` asked each of them, each new
  filter cost more bits per item than the last, and the tightened rate passed what a
  double holds after a few thousand filters, where `Add` threw. Measured at a hint of
  1,000, a rate of 1% and r = 0.8:

  | Growth | Stops at | Filters there | Bits per item past 1M items | Negative `Test`, 1M items | False positives, 1M / 100M items |
  | --- | --- | --- | --- | --- | --- |
  | 1 | 3.16 million items | 3,161 | 245, rising to 748 | 23.7 µs | 4.04% / unreachable |
  | 2 | 249 million | 18 | 14–18, up to 35 just after a filter is added | 0.20 µs | 3.46% / 3.73% |
  | 4 | 332 million | 10 | 13–14, up to 58 just after a filter is added | 0.12 µs | 2.74% / 3.39% |

  The compound bound, fpRate / (1 − r), is 5% there and holds at every factor: it is the
  sum of the rates, which growth does not change. A growing filter stops when its next
  contained filter would need more bits than one addresses, 4,294,967,295. A filter that
  grows writes format version 7, which records the factor; one with a factor of 1 still
  writes version 1, readable by earlier libraries. Payloads from earlier versions read as
  filters with a factor of 1, and go on growing that way. (#171)

### Changed

- **The package's minimum `System.IO.Hashing` rises from 10.0.11 to 10.0.14.** A project
  that pins an earlier version of that package has to raise it to restore this one. (#142)

- **Reading is now limited to one gibibyte by default, and that is a breaking change.**
  `ReadFrom(Stream)`, `ReadFrom(Stream, hash)` and `Persistence.FromByteArray<T>` read
  under `ReadOptions.Default`. A payload whose read needs more than 1,073,741,824 bytes —
  which loaded in 6.2.0 — is now refused, and needs the overload that takes
  `ReadOptions`: a Bloom filter whose bits come to more than about half a gigabyte, a
  quotient or cuckoo filter whose table is that large however little it holds, a large
  count-min sketch, and so on. A read costs roughly the structure plus its payload, and
  [FORMAT.md](FORMAT.md#read-budget) says what each structure's read is charged. Under
  Semantic Versioning this calls for a new major version.
  <br><br>
  The reason is that a reader must allocate what a payload says before it can fill it,
  and many structures are far larger than their payloads: a cuckoo filter writes only its
  occupied entries, a quotient filter's header names its table outright. Once every reader
  loaded everything its constructor builds (below), a few dozen bytes could ask for tens
  of gigabytes. Each reader now charges what it is about to allocate against the budget
  first, from the fields that size it.
  <br><br>
  Some readers allocate less than they did, which makes more room under the budget.
  Restoring a bit array no longer allocates an empty one of the same size to compare
  against and throw away: a Bloom filter with a 1.2 MB payload allocated 3.6 MB to read
  and now allocates 2.4 MB. A DDSketch store is sized once from the run it reads, where
  it was re-centred every time the next bucket passed its edge: a dense sketch with a
  160 KB payload allocated 3.7 MB to read and now allocates 480 KB. TopK and HeavyKeeper
  size their heaps by the elements a payload holds rather than by `k`. What is read back
  is unchanged. The stored format does not change.

- **A new `ScalableBloomFilter` grows its contained filters by a factor of 2 unless told
  otherwise, and that is a breaking change.** The constructors that take no growth factor,
  and `NewDefaultScalableBloomFilter`, now build each contained filter for twice the items
  of the one before (above). Once a filter has grown, its `Capacity()`, its memory and its
  payload differ from 6.2.0's: right after it adds a filter it holds up to twice the
  space its items need, where a filter growing by 1 held at most one hint's worth spare.
  Its payloads are format version 7, which 6.2.0 and earlier refuse as written by a later
  version. Pass a growth factor of 1 for the filter as it was; it builds, answers and
  writes exactly what 6.2.0 did. Stored filters are unaffected: they read with a factor of
  1 whatever the default. Under the default read budget a growing filter loads up to
  about 62 million items at a hint of 1,000, 1% and r = 0.8, where one growing by 1
  loaded up to about 2.83 million; both need `ReadOptions` past that. (#171)

- **`ScalableBloomFilter.Capacity()` returns `ulong`, and that is a breaking change.** It
  returned a `uint` and added up its contained filters' bits in one, so once they came to
  more than 4,294,967,295 between them it reported the total wrapped to a small number.
  No contained filter can pass that alone, since each is a 32-bit filter, but together
  they did, in 6.2.0 as well: at 1% and r = 0.8, a filter with a hint of 10,000 wrapped
  after about 13.3 million items, and one with a hint of 100,000 after about 40.5
  million. A growing filter gets there sooner. At a growth of 2, a hint of 1,000, 1% and
  r = 0.8, its eighteen filters hold 4,460,676,502 bits, which the old arithmetic
  reported as 165,709,206. Code that keeps the result in a `uint` needs a wider variable
  or a cast. The method is not on `IFilter`, and no other structure's `Capacity()`
  changes type: each of those is one filter, which its own type already holds. (#171)

- **The 32-bit Bloom filters count in 64 bits, and save at format version 8.**
  `Count()` on `BloomFilter`, `CountingBloomFilter`, `DeletableBloomFilter` and
  `PartitionedBloomFilter` returns a `ulong` where it returned a `uint`. This is a
  breaking change, as `ScalableBloomFilter.Capacity()` already was: a caller assigning the
  count to a `uint` needs a cast or a wider variable. The count was a `uint` that wrapped
  to zero after 2^32 adds; it now crosses 2^32, and for the two that remove, comes back
  down across it. A count that stopped at its maximum instead would have under-reported
  once a removal took it down.
  <br><br>
  Every save of those four and of `StableBloomFilter`, whatever its size, is now
  [format version 8](FORMAT.md#format-versions): the layout each wrote before, with a
  placement byte after the fields that size the filter (0 for 32-bit values, 1 for 64-bit
  ones) and the count, where there is one, as a `u64`. **Releases up to 6.2.0 refuse
  every such payload** as written by a later version, small filters included. Payloads
  that earlier releases wrote still read, with their 32-bit placement and their count, and
  save again as version 8. A placement byte naming neither scheme is refused with what it
  holds. A `ScalableBloomFilter` keeps its own version, 1 or 7, and its contained filters,
  each in its own envelope, are version 8.

### Fixed

- **`InvertibleBloomLookupTable.TryDecode` could report success with a wrong answer.** It
  called a table empty once every count was zero, but two keys on opposite sides that land
  in the same cells cancel each other's counts while both stay in the key and hash sums.
  The decode then returned true holding part of the difference, or none of it. In 6.2.0, a
  table sized for eight differences holding four each way did so in 89 of 5,000 decodes,
  every one a partial listing presented as complete; a table sized for two had four
  cells, which every key fills, and reported any difference of one key each way as no
  difference at all. (Since the sizing fix below, a table sized for two has 16 cells.)
  The check was the same from the day the type was added, so 6.0.0 through 6.2.0 are all
  affected.

  A table is now empty only when every count, key sum and hash sum is zero — the rule
  Eppstein et al.'s decode uses — so those decodes return false, which is the answer the
  structure promises when it cannot finish. A decode that was right before is unchanged:
  recovering everything leaves every field at zero. The stored format does not change.
  (#146)

- **`MementoFilter.TestAndRemove` could remove the only record of a different key**, which
  then tested absent though it had been added and never removed: a false negative, which
  a filter must never produce. A box that has lived through an expansion has given up
  fingerprint bits, so it stands for more blocks than a younger box does, and one removal
  can match several boxes. The removal took the first match in its run, which is the
  oldest and shortest, and that could be another key's only record. It now takes the
  longest matching fingerprint, which is what the Memento paper specifies for deletion.
  Across the chain of tables the newest is still asked first. That order already took the
  longest match, because a match remembers its address as well as its fingerprint, and a
  test now holds it there.
  <br><br>
  It is rare at the default parameters, but it does happen. Measured over five
  deterministic sequences of 3,000,000 adds and removals at each of three key densities,
  growing a default filter through eight or nine expansions, 6.2.0 lost 39 records and the
  fix lost none. Some of those keys tested absent at the end, and for the rest a later
  removal of the key found nothing. Short fingerprints lose far sooner: the two-bit filter
  in the regression test loses a key after four adds and one removal.
  <br><br>
  `MementoFilter` shipped in 6.2.0, so 6.2.0 is affected. The persistence format does not
  change, and payloads written by 6.2.0 load as before. A filter that has already lost a
  record this way cannot get it back: the fix prevents further loss and nothing more.
  (#145)

- **A `ThetaSketch` or `TupleSketch` read back from storage, or built by `Union`,
  `Intersect` or `Difference`, now goes on exactly as the sketch it stands for would.**
  Below a nominal size of 8 it did not. A sketch may hold twice its nominal size before
  it trims, and while it is being fed it trims only when its buffer fills, but the
  reader and the set operations gave the rebuilt sketch room for at least sixteen
  values, so its later trims came at different moments. Fed the same input afterwards,
  a sketch saved and restored at k = 2 to 7 ended on a different estimate from one never
  saved in roughly a third to four fifths of the streams measured. At k = 1 a restored
  sketch agreed with an unsaved one in every stream measured but held eight times the
  buffer it needed, and from k = 8 up estimates and payloads were unaffected. So was any
  sketch that was read or combined and then given no more input.
  <br><br>
  Every path now sizes the buffer by one rule, capped at what the sketch may hold.
  The payload does not change: the buffer's size was never stored, so every payload
  already written reads into a sketch that carries on as if it had never been saved.
  The defect shipped with `ThetaSketch` in 6.0.0 and with `TupleSketch` in 6.2.0. (#147)

- **A `TupleSketch` folding fractional values with `Sum` now ends on the same bits
  whether or not it was saved or combined on the way.** Its summaries, and so `Total()`,
  could differ in the last bits between a sketch that was saved and read back, or unioned
  with or differenced from an empty one, and a sketch that never was, once its nominal
  size was 9 or more. The sort that gathers a key's values before folding them was
  `Array.Sort`, which is not stable once the buffer is past a small partition: it can
  swap equal keys. Saving or combining makes a sketch compact at moments it otherwise
  would not, so a key's values were folded in a different order, and floating-point
  addition is not associative. Every summary was within rounding of the true total
  throughout, and whole numbers, `Min` and `Max` were never affected. `TupleSketch`
  shipped in 6.2.0, so 6.2.0 is affected.
  <br><br>
  The sort is now a stable merge sort over the keys and their summaries together. A
  compaction merges the entries the last one left, which are already in order, with
  what has arrived since, so a key's summary is the left-to-right fold of its values in
  the order they arrived, wherever the compactions fell. A saved or combined sketch ends
  on the same bytes as one that never was, at every nominal size. The payload does not
  change, since only sorted, distinct keys were ever written, so every payload already
  written reads as before. Adding the same fractional values in a *different* order
  still rounds differently, as it must; that is floating-point addition, and no sort
  changes it.
  <br><br>
  Adds are no slower. In a BenchmarkDotNet short job (new in `Benchmarks/`; fractional
  `Sum` values in bursts of repeated keys, mean per add, two passes on one machine), a
  sketch of 4096 went from 25.5 and 26.1 ns to 17.2 and 18.3 ns on a stream that keeps
  its buffer refilling, because the entries a compaction left are merged rather than
  sorted again. Everywhere else, which is the smaller sizes and every size on a stream of
  mostly new keys that the threshold turns away, the two were within about the ten
  percent that separates one run from the next, at 5 to 11 ns. Nothing is allocated per
  add before or after: the merge's scratch comes from the stack, or from the shared
  array pool for the larger sketches.

- **A `VarOpt` merge no longer writes a sample its own reader refuses.** Merging a sample
  that had begun sampling into an empty one keeping the same number of items fed the
  other's items in one at a time. All of them fit, so the result never started sampling,
  yet it counted every item the other side had seen: `WriteTo` succeeded and `ReadFrom`
  refused the payload. The result also slipped past `Merge`'s own rule, because it looked
  exact, so it could go on to be merged into a sample keeping more items, which then held
  its threshold items as exact ones.

  The result is now the other sample, threshold region and all. That is what the paper
  requires: in the full version's Lemma 4, when the inner samples together fit in k there
  is a single input, it keeps exactly k, and the merged sample is identical to it. Apache
  DataSketches calls the same state "pseudo-exact" and resolves it the same way.

  A payload 6.2.0 wrote from such a merge is still refused, now with a message naming the
  cause. It does not record which of its items stood in for dropped ones, so restoring it
  would be a guess, and 6.2.0's own reader refused it as well. Merge the original samples
  again.
  <br><br>
  **Samples that went on from such a merge do load, and their estimates are unbiased.**
  The merged result held exactly the input's items at exactly their adjusted weights, so
  it gave the input's own estimates. What was wrong was its count of items seen, which no
  estimate reads, and its record of which items stood at the threshold. Its payload
  became loadable once it, or a sample it was merged into, began sampling. Every step
  that can do that treats adjusted weights as weights, and an unbiased estimate of an
  unbiased estimate is unbiased, as the paper's §3 notes. Measured on 6.2.0 over
  4,000,000 seeded trials per route at each of four weight mixes, every subset sum and
  every item averaged to its true weight within sampling error. The worst was 3.5
  standard errors, against 4.0 on master's own correct paths, and the weights summed to
  the true total within rounding.
  <br><br>
  Every route but one leaves a sound sample. One more `Add`, a merge into a sample keeping
  the same number of items or fewer, or another sample merged into it: each is the
  paper's recurrence with every input keeping at least as many items as the result. The
  sample is then distributed as if the merge had never happened, and its variances
  matched master's.
  <br><br>
  **One route can cost precision: merging the result into a sample keeping more items**,
  which 6.2.0 allowed because the result looked exact. That sample holds the input's
  threshold items as exact ones, at the input's threshold. While its own threshold is
  below the input's, which the weights decide rather than chance, it keeps the input
  whole and samples everything else with the slots left over. Across three weight mixes,
  its summed per-item variance was 15%, 22% and 30% above that of a same-size sample
  rebuilt from the original data, and the average variance over subsets of any size is
  higher by the same factor. Single subsets measured 10% to 53% worse. The exception was
  the input's own stream, whose total such a sample holds exactly. Later additions that
  lift its threshold past the input's make it sound again.
  <br><br>
  Anyone who, on 6.2.0, merged a sample that had begun sampling into an empty sample
  keeping the same number of items, then merged the result into one keeping more, is
  affected. Such a payload cannot be told from a sound one by its bytes. Where the items'
  original weights are known, it can: a sound sample reports each item at its own weight
  or at one shared threshold, and this one also reports some at a second, higher value,
  the input's threshold. That showed in every damaged trial and in no sound one. To
  recover the lost precision, rebuild the sample from the original data, or merge the
  original samples again with this version. (#148)

- **A `VarOpt` sample saved before it has begun sampling comes back exactly as it was.**
  Until then it holds every item in the order it arrived, and the transition to sampling
  heaps them from that order, but the reader re-heaped them. Write, read and write gave
  different bytes. With mixed weights that tie, a sample saved in that phase and then
  continued went on to sample different items from one never saved: after 54 of 640 saves
  at k = 8, over 40 streams with weights tied among 1, 2 and 3. Unit weights never
  diverged, because re-heaping items that all tie moves nothing, and distinct weights
  never sampled differently in any measured stream, only held the same items in another
  order. The reader now re-heaps only a sample that has begun sampling.

  Every 6.2.0 payload still loads. One written before sampling began restores in the
  order it was written, which is the order that sample held, and is pinned by a new
  fixture. One written while sampling was already a heap and reads exactly as before.
  (#148)

- **A repeated item could change `HyperLogLogPlus`'s answer.** When the sparse form was
  exactly full, the next add converted it to the dense one before anything checked
  whether that hash was already held, so a repeat traded the exact count for an
  estimate: 32 distinct items at precision 8 counted 32, and adding one of them again
  made it 31. A repeat converted the estimator at every precision from 4 to 18, and
  moved the count at every one but 9 and 17, where the estimate happened to land on the
  exact count. A repeat now leaves the estimator exactly as it was, merged in or added,
  and only a new item converts it.
  <br><br>
  **At precisions 4 to 6 this deliberately departs from the paper, and behaves as 6.2.0
  did.** Heule, Nunkesser and Hall (section 5.3) convert once the sparse list would need
  more than the dense registers' 6m bits, which with eight-byte hashes and one-byte
  registers is `m / 8` hashes: 2, 4 and 8 at those precisions. The estimator holds 16
  there instead, so every count up to 16 stays exact where an estimate from so few
  registers is off by 6% to 26%. That is the root-mean-square relative error of the
  dense form, measured on this implementation over counts 2 to 16 with 100,000
  deterministic trials each: 12% to 26% at precision 4, 9% to 18% at 5 and 6% to 14% at
  6. The textbook `1.04 / sqrt(m)`, 26% down to 13% over those precisions, is about the
  worst count at each rather than a typical one. The price is at most 112 bytes over
  the dense registers, at precision 4 (128 bytes against 16), and 96 and 64 at
  precisions 5 and 6. 6.2.0 held 16 there too, though by accident — its first buffer
  was 16 hashes and never shrank to the cap the code declared — so the rule is now
  written down where it decides rather than left to a buffer size. From precision 7 up
  the floor changes nothing and the estimator follows the paper.
  <br><br>
  Stored estimators read exactly as before, and the layout and format version are
  unchanged. The only payload that differs is the one a repeat used to force: an
  estimator that sees a repeat at its conversion point now writes the sparse form it was
  in, rather than a dense one. (#149)

- **A small `InvertibleBloomLookupTable` could not decode the difference it was sized for.**
  The constructor gave a table 1.5 cells per expected difference, never fewer than four,
  on the strength of an asymptotic threshold that says nothing about small tables. Sized
  for two, three or four differences, a table never decoded them, in none of 20,000 trials
  each; sized for twenty it decoded them two times in three, and for a hundred, 98.9% of
  the time. Before the #146 fix those failures often passed for success; after it they
  returned false, which is what made the shortfall visible.

  A table sized for *d* differences now lists all of them with probability at least 0.99,
  at every *d* from one up. It gets the larger of 1.5*d* cells and 1.4*d* + 13, so a table
  sized for one difference has 15 cells, for ten 27, and for a hundred 153. From 129
  differences up that is 1.5*d*, which is what it always was, and nothing gets fewer cells
  than before. The 0.99 is the figure Eppstein, Goodrich, Uyeda and Varghese use for the
  same question (SIGCOMM 2011, section 6.1, Figure 7); the sizes are measured on this
  implementation, because neither it nor Goodrich and Mitzenmacher's paper analyses small
  tables. The lowest rate measured at any size from 1 to 260 differences was 99.5%.

  **If you exchange tables between processes,** note that two tables must have the same
  number of cells to be subtracted, and `Subtract` refuses a pair that do not. A table built
  by an earlier version for fewer than 129 differences has fewer cells than one built now
  for the same number, so build both sides with the same version of the library. The
  exception `Subtract` throws for such a pair now names that as a likely cause.
  <br><br>
  **Stored tables still read, at the size they were written with.** The cell count is in
  the payload and the format does not change, so a table written by any earlier version
  reads and decodes exactly as before. That includes the shortfall: a stored table sized
  for fewer than 129 differences keeps its old, smaller cell count, and with it the old
  chance of failing to decode the difference it was sized for. Reading it does not
  resize it. Only a table built by this version gets the new size, so rebuild a small
  stored table to get it. (#150)

- **An `InvertibleBloomLookupTable` too large to exist is now refused as an argument.**
  The cell count was worked out in `double` and cast to `uint`, and the three arrays were
  allocated from it unchecked. Nothing was ever sized too small, because on .NET 10 the
  cast saturates instead of wrapping. But a table that could not exist failed wherever
  the allocator happened to refuse it: an `OverflowException` for a length past an `int`,
  an `OutOfMemoryException` for the band below it, and neither named `expectedDifferences`
  or `keySize`. The counts are allocated first, so they had taken their memory by then:
  300 million differences with 8-byte keys held 3.6 GB for a moment before failing on the
  keys.
  <br><br>
  The constructor now throws `ArgumentOutOfRangeException`, before allocating anything,
  when an array of the table could not exist: when `expectedDifferences` needs more cells
  than an array has elements (more than 1,431,655,727 differences), or when the cells
  times `keySize` come to more bytes than an array holds (2,147,483,591). The message
  names the limit and how far to come down; with 8-byte keys it is 178,956,965
  differences. A table that could be allocated before still can be, and the stored format
  does not change. Reading a table was already bounded, by the byte counts of the arrays
  the payload carries, and a test now holds it there.
  <br><br>
  `Subtract` and `ReadFrom` also build their result at its own size, where they used to
  build a table sized for one difference and replace its arrays. That table was
  allocated only to be thrown away, and the small-table sizing had made it larger than
  it was. The tables they return, and every byte written from them, are the same.

- **`StableBloomFilter.Reset` now puts the random generator back where it began.** Its
  documentation promised the filter's original state, and it delivered all of it but
  one thing: the cells cleared and the generator stayed wherever the adds had left it. A
  reset filter differed from a new one with the same seed in the generator state stored in
  its payload, and made different random choices from then on, so the stream that took a
  filter to some state did not take its reset self to the same state. (#144)
  <br><br>
  **A reset filter now repeats its original random sequence.** For a seeded filter that
  is the sequence its seed gives. For one built without a seed it is the unpredictable
  sequence it was built with, so resetting it again repeats that sequence rather than
  drawing a new one; build a new filter for fresh randomness. This holds for a filter that
  was saved and loaded in between.
  <br><br>
  That needed the payload to say where the generator began, and it held only where the
  generator had got to. **`StableBloomFilter` therefore writes a new format version**,
  which adds that starting state as one more field, so a release before this one cannot
  read a stable filter written by this one — the compatibility this library promises runs
  backwards, not forwards. `CuckooBloomFilter` makes the same change, next, and every
  other structure writes the version it always did.
  <br><br>
  Payloads written by earlier versions still read, and nothing in them changes. They hold
  no starting state to go back to, and the seed is not in them, so a filter read from one
  takes the generator position it was read at as its beginning: `Reset` returns it there,
  emptied, and writing it out again records that same point rather than the one it has
  reached since. A payload written before 6.0.0 holds no generator position at all, so
  its beginning is the unpredictable one the filter was given on reading, as before. The
  layout is in [FORMAT.md](FORMAT.md).

- **Every structure now refuses a format version it never defined, and says so.** A
  payload's version belongs to the structure that wrote it: the stable filter has a
  version 5 and the Bloom filter has only ever written 1. The readers did not hold to
  that. They checked one library-wide ceiling, so a `BloomFilter` payload labelled 2, 3,
  4 or 5 was accepted, and each structure's reader accepted every version up to the
  highest any structure had defined. FORMAT.md promised a payload is "refused with an
  explanation" otherwise.
  <br><br>
  **What actually happened, measured by relabelling.** A valid payload of every structure,
  and of each older layout of the three that have them, was given each version from 1
  to 5 that its structure never wrote, with the checksum repaired so the version was the
  only difference. Nothing crashed. For every structure except the stable filter, the
  cuckoo filter and `SetSketch`, no reader looks at the version at all, so the payload
  was read as exactly the structure it always was, with no complaint: that is most of
  them. The three that do branch on the version read the layout nearest below the
  version they were handed, and were refused only when that layout happened not to fit
  the bytes (a field short or left over, a fingerprint or register out of range), never
  because of the version, and without saying anything about it. One case fitted: a
  version 2 cuckoo filter holding nothing, labelled 4 or 5, loaded as a filter with
  2-bit fingerprints in place of its 16-bit ones, because with no entries the packed
  layout has nothing to disagree with.
  <br><br>
  **Who is affected.** No payload this library wrote, since it never writes a version its
  structure did not define, and every stored fixture still reads. A payload has to have
  its header rewritten with the checksum recomputed to carry such a version, or to come
  from something that is not this library, so the exposure is narrow; it is still a
  guess where the format promised none.
  <br><br>
  Each structure now reads the versions it has defined and no others. The refusal is an
  `InvalidDataException` that names the structure, the version it was handed and the
  versions it does read. Version 0 and versions above the highest were already refused;
  their message changed too, since version 0 used to be described as written by a later
  version. The versions each structure reads are in one table, which FORMAT.md's table of
  versions is held to. Adding a version to a structure is one new constant and one more
  entry in that table, and the highest supported version follows from it.

- **`CuckooBloomFilter.Reset` now puts the random generator back where it began.** It had
  the same gap `StableBloomFilter.Reset` had, with the same documentation promising the
  filter's original state: the entries cleared and the generator stayed wherever the adds
  had left it. A reset filter differed from a new one with the same seed in the generator
  state stored in its payload, and chose different entries to evict from then on, so the
  stream that took a filter to some state did not take its reset self to the same state.
  (#144)
  <br><br>
  **A reset filter now repeats its original random sequence.** For a seeded filter that
  is the sequence its seed gives. For one built without a seed it is the unpredictable
  sequence it was built with, so resetting it again repeats that sequence rather than
  drawing a new one; build a new filter for fresh randomness. This holds for a filter that
  was saved and loaded in between.
  <br><br>
  The payload held only where the generator had got to, so **`CuckooBloomFilter` writes a
  new format version too**: it adds the starting state as one more field, and a release
  before this one cannot read a cuckoo filter written by this one — the compatibility this
  library promises runs backwards, not forwards. Every structure but the stable and cuckoo
  filters writes the version it always did.
  <br><br>
  Payloads written by earlier versions still read, in every format version the cuckoo
  filter has written, and nothing in them changes. As for the stable filter, a filter read
  from one takes the generator position it was read at as its beginning: `Reset` returns
  it there, emptied, and writing it out again records that same point rather than the one
  it has reached since. A payload written before 6.0.0 holds no generator position at all,
  so its beginning is the unpredictable one the filter was given on reading, as before.
  The layout is in [FORMAT.md](FORMAT.md), whose cuckoo section had also still described
  fingerprints written as whole bytes, which stopped being true when 6.1.0 packed them at
  their exact bit width; it now describes every version the reader accepts.

- **The `QuotientFilter` constructor now refuses sizes that cannot work, where it used to
  build a filter that answered wrongly.** Three limits went unchecked, and past any of them
  there was no error: the constructor returned, and the filter was broken from the first
  call. Each now throws an `ArgumentOutOfRangeException` naming the parameter and the most,
  or the finest, that would have worked. Everything the constructor now builds saves and
  loads.
  <br><br>
  **A false positive rate finer than the hash can hold.** A fingerprint is the top q + r
  bits of a 64-bit hash, where q is the width the item count gives and r the width the rate
  gives, so q + r cannot pass 64. Past it the shift was by a negative count, which C# masks
  to six bits, and the fingerprint collapsed to a few top bits. For 1,000 items a rate of
  1e-15 was fine, and 1e-16 or finer built a filter that answered yes for every key: all
  20,000 absent keys tested present after 1,000 adds, a false positive rate of 1.00. The
  finest rate for an item count is 2^-(64 - q), which is 2^-53 for 1,000 items.
  <br><br>
  **A rate finer than a slot can hold.** A slot is the remainder and three bits of
  bookkeeping, read and written through one 64-bit word, so r cannot pass 61 whatever q
  is. A wider slot wrote by shifting a ulong by 64, which also wraps to no shift, and put
  the whole value into the next word. A filter sized for three items at 2^-62 lost one of
  them; one sized for one item at 2^-63 lost its only item, and a second `Add` never
  returned. A rate so small that its reciprocal overflows (1e-320, say) was cast to a
  `uint` from infinity and built a two-bit slot, which lost every item. The finest rate is
  never finer than 2^-61.
  <br><br>
  **More items than the largest table is sized for.** The table has 2^q slots counted in a
  `uint`, and 2^32 is one more than one holds, so q cannot pass 31 and the most items is
  three quarters of 2^31, which is 1,610,612,736. Asked for more, the shift wrapped:
  2,000,000,000 items built a table of one slot, which threw "the filter is full" on the
  second `Add`, and more than 3,221,225,472 built one of two.
  <br><br>
  `QuotientFilter` shipped in 6.0.0, so every release since is affected. The persistence
  format does not change, and a payload any of them wrote still reads: the one case the
  constructor used to produce and no longer can is a quotient of 32 bits, written with the
  bytes of its one slot, and it reads back as the one-slot filter it was. The reader already
  refused a wider quotient and still does.

- **A structure built past its reader's size ceiling now reads back.** Readers bound what
  a payload can make them allocate, and many did it by refusing any size above a fixed
  ceiling — a million for most, 2^26 registers for `SetSketch`, 64 rows for
  `SublimeCountMinSketch` — as "beyond anything this library builds". The constructors
  built past it without complaint, so a structure saved fine and its own payload was then
  refused: a `ThetaSketch` or `TupleSketch` retaining more than a million; a `TopK`,
  `HeavyKeeper` or `VarOpt` with k above a million; a `HeavyKeeper` with more than a
  million buckets; a `CuckooBloomFilter` for 1,992,295 items or more, whose buckets round
  up past a million; a `MinHash.Signature` longer than a million; a `Grafite` filter of
  more than a million words of either kind; a `SetSketch` of more than 2^26 registers; a
  `SublimeCountMinSketch` at a delta below e^-64, about 1.6e-28, which needs a 65th row;
  and a `ScalableBloomFilter` grown past a million filters. Every release since persistence arrived in 3.2.0 is affected,
  for each structure it had.
  <br><br>
  Each structure now has one limit for each size it records, shared by its constructor
  and its reader, so whatever a constructor builds its reader loads. A limit is where the
  structure itself stops working — mostly the largest .NET array — rather than a round
  number, and [FORMAT.md](FORMAT.md#sizes) lists them. Readers also check a count against
  the bytes actually present before allocating for it, so a payload claiming a large size
  costs what it holds rather than what it claims. A cuckoo filter's table and a `VarOpt`
  sample's regions have to be allocated whole, so for those a payload can ask for as
  large a structure as the constructor would build, and no larger.
  <br><br>
  **Some constructors now refuse sizes they used to accept, each one a size that did not
  work**, and now say why with an `ArgumentOutOfRangeException`:
  `CuckooBloomFilter` past 2,040,109,465 items, which built a filter with no room to mark
  an entry occupied that failed on its first add, or at a rate tight enough that its
  fingerprints outgrow one array; `ThetaSketch` and `TupleSketch` retaining more than
  1,073,741,795, which from 2^30 threw an `OverflowException` and from 2^31 failed on the
  first add; `VarOpt` keeping more than 2,147,483,590, which at `uint.MaxValue` built a
  sample with no room in it; `SublimeCountMinSketch` at a delta whose reciprocal is
  infinite, which threw an `OverflowException`; and `TopK` and `HeavyKeeper` past
  2,147,483,587 tracked, `HeavyKeeper` arrays, `SetSketch` registers and
  `MinHash.Signature` lengths past what an array holds, each of which failed inside the
  runtime with an allocation error. `Grafite.Build` now refuses a key set needing more
  high bits than an `int` addresses — hundreds of millions of keys — whose count wrapped
  negative. The theta and tuple sketches' buffers also doubled, and their unions summed,
  in an `int` that overflowed for retained sizes past 2^29; both are now computed in a
  `long`, and a union no longer allocates past the size it trims to.
  <br><br>
  The stored format does not change. Payloads that were refused for their size now load,
  and every other refusal stands.

- **A `BloomFilter`, `CountingBloomFilter` or `StableBloomFilter` sized past 2^32 bits was
  built with a table too short for it, and failed on an add.** The bucket array's byte
  count was worked out in 32 bits, so it wrapped at 512 MiB of one-bit buckets, 128 MiB
  of eight-bit ones. `new BloomFilter(448_089_842, 0.01)` allocated no bits at all and its
  first `Add` threw `IndexOutOfRangeException`. `new CountingBloomFilter(117_623_547, 4,
  0.01)` allocated 26.8 MB of the 564 MB it needs, and an add that landed past the end
  threw after setting the counters before it, leaving the filter half updated: in 200 of
  200 adds. A `StableBloomFilter` of 1,431,655,763 three-bit cells got none. The counting
  filter starts to do this at about 112 million items at 1% with four-bit counters, and at
  about 56 million with eight-bit ones. Every release is affected, for each filter it had.
  A bucket's position in the table was also a `uint` product that wrapped at the same
  size, so even a table of the right length would have been read and written at the wrong
  bytes.
  <br><br>
  The sizes are now worked out in 64 bits, and a filter works up to the most its table can
  be: one .NET array, 2,147,483,591 bytes. For a `BloomFilter`, `PartitionedBloomFilter`
  and `DeletableBloomFilter`, whose buckets are one bit, that is more than their 32-bit
  count addresses, so nothing changes about how large they can be. A
  `CountingBloomFilter` or `StableBloomFilter` of four-bit buckets or wider stops at
  4,294,967,182 buckets at four bits and 2,147,483,591 at eight, and the constructor now
  refuses a size past that with an `ArgumentOutOfRangeException` naming `n` or `m`. No size
  that worked before is refused. `BloomFilter64` and its 64-bit arrays are refused at a
  count whose arrays would not fit the array that holds them, about 2.3e18 bits, where an
  array count cast to an `int` wrapped, and `Utils.OptimalM64` says that a bit count
  beyond a `ulong` is a mistake in the item count where it threw an `OverflowException`.
  <br><br>
  **A payload written by an earlier version for a filter this large is now refused.** It
  holds the wrapped table, and the count it records is the filter's real one: a Bloom
  filter of 4,294,967,294 buckets stored with no bytes. Nothing in it says which buckets
  were lost, and the filter that wrote it could not use it, so reading it would be
  guessing; the reader says what happened and the filter has to be built again from the
  data. Payloads of sizes that did not wrap read as they always did. The format does not
  change, and [FORMAT.md](FORMAT.md#sizes) says which length is refused as the wrapped one.

- **Writing a structure whose payload passed a gibibyte got slower with every field, and
  one that passed two gibibytes failed without saying why.** The buffer a payload is built
  in doubled in an `int`, which wraps negative from 1 GiB up, so from there it was exactly
  as long as what had been written, and each further write -- a count, a seed, the next
  nested structure -- reallocated the whole buffer and copied the payload into it. Measured
  with a payload of 1,363,148,812 bytes, a four-byte write took 0.4 s, and so did each
  one after it. The length a write needed was summed in an `int` as well, so a payload past
  the largest array failed in whichever way a negative length happens to fail.
  <br><br>
  The buffer now doubles up to the largest payload the format holds and reaches it in one
  copy, so what follows costs nothing. A payload longer than 2,147,483,573 bytes -- the
  largest array, 2,147,483,591, less the eighteen bytes of envelope it shares the array
  with -- makes `WriteTo` throw an `InvalidOperationException` naming the size it would have
  been and the size it can be, and the structure is unchanged. The format does not change
  and does not grow: a structure that large can be built and used and cannot be written.
  An envelope that claims a payload length between the largest array and `int.MaxValue` is
  now refused as one that cannot be read into an array, where it used to fail inside the
  allocation with an `OutOfMemoryException`.

- **A `ScalableBloomFilter` at its ceiling threw an `OverflowException` from inside the
  sizing arithmetic, with no hint of why. It now refuses the add with an
  `InvalidOperationException` that says why, before anything changes.** A filter growing by
  1 adds a filter at a tighter false positive rate each time the last one fills, the i-th at
  the rate times r^i, and the rate runs out: below 1 / `double.MaxValue`, about 5.6e-309, a
  filter's hash count cannot be worked out. At a rate of 1% and a ratio of 0.8 that is the
  3,162nd filter, which a hint of a thousand reaches after 3,156,212 items (measured), and
  at a ratio of 0.9 the 6,694th. A growing filter would have thrown an
  `ArgumentOutOfRangeException` for a parameter the caller never passed, recommending
  `ScalableBloomFilter` itself. Every release is affected, for the growth it had. The failing
  add changed nothing, since the filter is built before it is listed, so the filter that threw
  could still be queried and written.
  <br><br>
  The refusal is one `InvalidOperationException` for every way the next filter can fail: a
  rate with no finite reciprocal, a capacity past what a contained filter is sized for, or
  more bits than one addresses. It names the filter that could not be built, the reason, what
  the filter holds, and what would hold more (a larger hint or growth factor, a ratio nearer
  one, or a `BloomFilter64`); the filter is as it was and goes on answering. Where the rate is
  what runs out, at a growth factor of 1, the ceiling is `floor(ln(rate × double.MaxValue) /
  ln(1 / r)) + 1` filters, 3,161 at 1% and 0.8 and 6,693 at 0.9, which the class
  documentation gives; a growing filter stops sooner than that, at a few dozen filters,
  unless its ratio is small. A constructor given a first rate whose reciprocal is not a
  number, so that not even the first filter can be built, now throws an
  `ArgumentOutOfRangeException` naming `fpRate`. (#171)

- **A `SublimeCountMinSketch` row wider than about 285 million counters read and wrote the
  wrong place, and threw `IndexOutOfRangeException` for most keys.** A row's counters sit in
  chunks of one cache line, and a counter was found by the bit its chunk begins at,
  `chunk * 512` in an `int`, which wraps at 2^22 chunks. A sketch built with a size factor
  of 1e-9, which starts each row at 2^30 counters and 963 MiB, threw from its first `Add`
  and from `Count`; at 2^29 about half its keys worked and the rest threw, and an `Add`
  that threw on a later row had already incremented an earlier one. Every release since
  `SublimeCountMinSketch` shipped in 6.2.0 is affected. At the default growth a row reaches
  that width after 8e16 keys, but a growth exponent of 0.9 gets there at about 2.5e9. The
  positions are now 64-bit, and a row works up to 2^30 counters, the widest an `int` holds.
  <br><br>
  Past that, growth is refused. The doubling was an `int` product that went negative from
  2^30, after the expansion had already put the old rows on its list of records and after
  the add had counted its key. An `Add` that would widen a row past 2^30 now throws an
  `InvalidOperationException` first, naming the width and the ways round it, and changes
  nothing; the sketch can still be queried and written. A size factor that asked for a row
  of 2^31 counters or more was quietly clamped to 2^30; the constructor now refuses it,
  naming `sizeFactor`. No width that worked is refused. The stored format does not change
  and a payload claiming a width past 2^30 is refused with that limit named.

- **An `InfiniFilter` or `MementoFilter` that grew to 2^31 slots failed obscurely on its
  next expansion, and their readers accepted tables that cannot exist.** A table's slot
  count was `1u << quotientBits`, and C# takes a shift count mod 32, so a table of 2^32
  slots was built with one. The tables double to 2^28 slots at about a hundred million
  items, so the doubling into 2^32 comes at about 1.6 billion: the add that triggered it
  threw `IndexOutOfRangeException` part way through moving the entries (the old table was
  kept, so the filter survived, with an error that said nothing of why). Separately, the
  readers accepted 32 to 40 bits of address, building a segment of 1 to 256 slots that
  disagreed with its own claim; no writer produced such a payload. Every release since
  `InfiniFilter` and `MementoFilter` shipped in 6.2.0 is affected.
  <br><br>
  A table now has at most 31 bits of address, the same limit and the same reason as
  `QuotientFilter`'s: the slot count is a 32-bit number holding 2^q. The add that would
  expand past it throws an `InvalidOperationException` first, naming the table's size and
  the limit, and changes nothing; the filter can still be queried and written, and removing
  keys makes room. The readers refuse more than 31 bits of address and say what the limit
  is. A bigger table is not a matter of wider arithmetic: it could not be written (at the
  narrowest fingerprint it is three gibibytes of payload, in an envelope that holds two) and
  the expansion into it lists every entry it moves, 1.6 billion at sixteen bytes. An initial
  capacity past three quarters of 2^31 starts the first table at the widest, as it did,
  now by a rule and not by a saturating conversion. No size that worked is refused.

- **A `MementoFilter` whose slots came to 64 bits stored no key at all, and one past 64 lost
  almost every key.** A slot is three bits of metadata and a field of the fingerprint, a bit
  for its age counter and the memento, so `fingerprintBits` + the range's bits + 4. Its mask
  was `(1UL << bits) - 1`, and a `ulong` shifts by its count mod 64, so at exactly 64 bits
  the mask was zero and every write was masked away: `Add` counted the key and stored
  nothing, and `Test` answered no. `new MementoFilter(1UL << 32, 28)` and `new
  MementoFilter(1UL << 28, 32)` each found 0 of 200 keys added. At 65 bits it found 1 of 200;
  at 66 an add threw `InvalidOperationException` from an insert that found no slot to write.
  Every release since `MementoFilter` shipped in 6.2.0 is affected. `InfiniFilter` has the
  same shape and cannot reach it: its slot is the fingerprint and four bits, 36 at most.
  <br><br>
  The mask is right for 64 bits, and a filter with a slot of 64 bits holds its keys, finds
  them, removes them and reads back. The constructor refuses a slot past 64, since it would
  straddle three words, with an `ArgumentOutOfRangeException` naming `fingerprintBits` and
  giving the widest fingerprint for the range and the widest range for the fingerprint. No
  size that worked is refused: every slot that held a key is 63 bits or fewer.
  <br><br>
  **A payload an earlier version wrote for a slot of 64 bits is now refused.** It says the
  table holds entries and every word of it is zero, which is what the dropped writes left;
  nothing in it can be recovered, and reading it would give a filter that says it holds keys
  and denies each of them. The reader says where it came from, and the filter has to be
  built again from its keys. A table of slots wider than 64 bits, empty or not, is refused
  for its width. Payloads of narrower slots read as they always did.

- **`QuotientFilter.WriteTo` threw an `OverflowException` on filters its own constructor
  builds.** It sized the byte array for the table as `data.Length * 8` in an `int`, which is
  negative from 2^28 words, two gibibytes of table. A filter of 2^31 slots with five or more
  bits of remainder, or 2^30 with thirteen or more, is that large: about 805 million items or
  more at 1%, 700 million at 0.02%. The reader compared the bytes it was given with the same
  product. Every release since persistence arrived is affected, for each size it could build.
  <br><br>
  The length is computed in 64 bits in both, and a filter whose payload would pass the
  longest an envelope holds, 2,147,483,573 bytes, now fails to write with the
  `InvalidOperationException` the payload writer gives for any structure too large to write,
  naming the size it would be and the size one can be, before anything is allocated. The
  filter is untouched. The format does not grow to carry a larger table: such a filter can be
  built and used and cannot be saved. Filters whose payload fits write as they did.

- **`HyperLogLog` overestimated large counts, and from about 5.7 billion distinct items
  returned 0.** It applied the original paper's large-range correction, -2^32 ln(1 -
  E/2^32), once its raw estimate E passed 2^32/30, about 143 million. The correction
  assumes the raw estimate counts distinct 32-bit hash values and adds back the items
  that collisions hid. The registers hide none: an item whose 32 bits match another's
  moves them exactly as it would have under a wider hash, unless its run of zeros reaches
  the end of the 32 bits. So the correction inflated every estimate it touched — by 3.2%
  at 2^28 distinct items, 13% at 2^30, 29% at 2^31, 84% at 2^32 and 2.6 times at 5.5
  billion, the same at every register count up to 2^24 (above that, linear counting
  covered the start of the band). Once the raw estimate passed 2^32 the logarithm's
  argument was negative, and the NaN converted to a count of 0 (on .NET 9 and later,
  which recent releases target): for every sketch from about 5.7 billion items, 2^32.4,
  and for any sketch whose registers were all full. The correction arrived with
  `HyperLogLog` itself, in 2015, so every release is affected.
  <br><br>
  The correction is gone. What 32 bits do limit is how long a run of zeros a register can
  record, and a register at that cap is now counted with Ertl's tau term, as
  `HyperLogLogPlus` counts its own. The estimate is now close to its nominal error up to
  about 2^33 distinct items with 16 registers or more; at 2^14 registers its mean is within
  0.25% of the truth from 2^20 to 2^34. Past 2^33 the registers fill and the error grows,
  and a sketch whose every register is full reports the largest estimate it can make,
  about 2^32 × ln m — 11 billion with 16 registers, 42 billion with 2^14 — rather than 0.
  A non-empty sketch never counts 0 now: the count is at least the number of registers
  set. That also corrects the smallest sketches, where one register counted 0 after an
  item and two registers could count 1 after two.
  <br><br>
  A sketch of one register could not be added to at all, in any release. Its register
  index was taken with a 32-bit shift by 32, which C# performs as a shift by 0, so the
  index was the whole hash and `Add` threw `IndexOutOfRangeException` for nearly every
  item.
  <br><br>
  The stored format does not change, and neither does anything stored: a payload any
  version wrote reads back holding the same registers, writes back the same bytes, and
  estimates by the corrected rule. Below about 143 million distinct items, where the old
  correction did not engage, estimates move only where a register has already reached the
  cap, which is rare there, and by at most 0.03%.

- **Large 32-bit Bloom filters had a higher false positive rate than they were sized
  for.** `BloomFilter`, `CountingBloomFilter`, `DeletableBloomFilter`,
  `StableBloomFilter` and `PartitionedBloomFilter` took an item's positions as
  `(low + i * high) mod m` from the two 32-bit halves of its hash, in 32-bit arithmetic.
  Each such value is a 32-bit number, and reducing it modulo an `m` that does not divide
  2^32 gives the first `2^32 mod m` positions one more preimage than the rest, so those
  fill faster and queries land on them as often. A `BloomFilter` of three quarters of
  2^32 bits sized for 1% measured 1.42%, and 0.17% sized for 0.1%, where a
  `BloomFilter64` of the same size measured 1.00% and 0.100%. The excess is a sawtooth
  in `m`: zero at every power of two, highest where `2^32 mod m` is half of `m`, and
  shrinking with the square of the filter's size. It was 3.7% of the rate at 966 million
  bits, 1.1% at 507 million and 0.24% at 261 million; below 2^27 bits it is too small
  to measure. The partitioned filter reduces per partition, so it is its partitions'
  size that counts. The placement is the one these filters have always used, so every
  release is affected.
  <br><br>
  A new filter whose positions are reduced modulo 2^27 or more — 16 MiB of bits, about
  14 million items at 1% — now places them with 64-bit values, exactly as
  `BloomFilter64` does, and delivers its rate at every size. Every smaller filter is
  placed exactly as before. Each save records which it was, in a placement byte of
  [format version 8](FORMAT.md#format-versions); see Changed.
  <br><br>
  **A filter read back keeps the placement its payload was written with.** The payload
  decides, never the size: a payload from an earlier release was placed with 32-bit
  values whatever its size, so it reads back as the filter it was, false positive rate
  included, and saved again it records that placement. To have the rate it was sized for, add its items to a new filter. The same
  rule refuses a merge between two filters of the same size placed differently, such as
  an old payload read back and a new filter, with an `ArgumentException` saying so,
  since the same item sets different bits in each.

- **`DDSketch` filed a value its 32-bit bucket index could not hold in the last bucket
  there is, and answered with that bucket for all of them.** A bucket is numbered by an
  `int`, and the cast saturated. At a relative accuracy of 1e-10 an `int` reaches 2^31
  buckets of 2e-10, so only values from about 0.65 to 1.54 have a bucket: a sketch of
  {1000, 2000, 1e6} answered 1.536 at every quantile. On a sketch that already held a
  value, the same index failed with an `OverflowException` inside the store's allocation,
  as did any two values further apart than one array of buckets holds.
  <br><br>
  `Add` now refuses a value whose bucket is not a 32-bit index, or is further from the
  buckets in use than one array holds, 2,147,483,591, with an `ArgumentOutOfRangeException`
  that names the range the accuracy reaches. The sketch is unchanged. `Merge` refuses a
  merge that would span more, before it changes either store. Every double has a bucket
  from an accuracy of about 1.74e-7; below that the range narrows, to 0.014 – 73 at 1e-9.
  The constructor and the reader refuse an accuracy below about 5.6e-17, where
  `(1+a)/(1-a)` is exactly 1 and no value has a bucket of its own. No accuracy and value
  that was bucketed correctly stops being.
  <br><br>
  With the ends of the range now reachable on purpose, the store's own arithmetic at them
  is 64-bit: its window, which sits partly below `int.MinValue` around an index there, and
  the quantile walk and merge, whose loops would otherwise wrap past `int.MaxValue`. The
  stored format does not change.

- **`TopK` counted the items added in a 32-bit field that wrapped to zero at 2^32 adds, and
  wrote it that way.** The count is internal and nothing read it, but a structure that had
  seen four billion items stored a count of none. The sketch inside has counted in 64 bits
  all along, so the count is now read from it, and the payload's 32-bit field holds that
  saturated at 4,294,967,295. A reader takes the count from the sketch and does not use the
  field, so a payload any version wrote reads the same, and one written below 2^32 items
  is byte for byte what it was.

- **`Grafite.SizeInBytes()` worked out its answer in an `int`, which wrapped.** It returns
  a `ulong`, but added the words of low and high bits and multiplied by eight before
  widening, so a filter of 2 GiB reported a size near 2^64 and one of 4 GiB reported none.
  It is 64-bit throughout now. The signature is unchanged.
  <br><br>
  `Grafite.Build` built a filter at a weaker rate than it was asked for, and said nothing.
  The keys are hashed into a universe of `n × maxRangeSize / falsePositiveRate`, and the
  hash works modulo 2^61 − 1, so a request that wanted a wider universe was built at
  2^61 − 2: four keys at 1% for ranges of 2^60 delivered 1, as `FalsePositiveRate` reported
  and nothing else did. It now refuses, with an `ArgumentOutOfRangeException` naming
  `maxRangeSize` and the largest range that works for that many keys at that rate (about
  5.8e15 for four keys at 1%, 2.3e10 for a million), or, when not even a range of one key
  fits, naming `falsePositiveRate` and the finest rate those keys can have, a little over
  `n / 2^61`. This is the rule `QuotientFilter` follows for a rate finer than it can
  deliver. Requests under the limit build as they did, to the same filter for a given seed.
  <br><br>
  A payload an earlier release wrote for a request that is now refused carries a universe
  of exactly 2^61 − 2 and still reads, and answers as it did: measured on 300 keys over all
  64 bits, no false negative and no point false positive. A payload with a universe past
  that, which no release wrote, is refused. FORMAT.md's table of sizes has the row.

- **`CountMinSketch`, `CountSketch` and `PrivateCountMinSketch` failed inside the
  allocation of a row instead of refusing a size they could not build.** A row is one
  array, which holds 2,147,483,591 counters. `CountMinSketch` allowed a width up to
  2^32 − 1 and failed with an `OutOfMemoryException` just past the limit and an
  `OverflowException` from 2^31; `CountSketch`, whose width is 1/ε² and not e/ε, shared
  the guard written for the other, so an epsilon of 1e-5 (ten billion counters) saturated
  the cast and failed the same way; `PrivateCountMinSketch` had no upper bound on its
  width or depth. A delta whose reciprocal is infinite, below about 5.56e-309, has no
  `ln(1/δ)` and was converted to a row count anyway. An epsilon of infinity had a width of
  none, and built a sketch that divided by zero on its first add.
  <br><br>
  Each is now an `ArgumentOutOfRangeException` naming the parameter, before anything is
  allocated: a width past 2,147,483,591 for all three, a depth past 710 for the first two
  (the `SublimeCountMinSketch` limit, for the same reason) and past 2,147,483,591 for the
  private sketch, and an epsilon with no width. The readers of `CountMinSketch` and
  `PrivateCountMinSketch` refuse a payload that claims more as beyond anything built,
  before it can ask for a row; `CountSketch`'s reader already checks its cells against its
  dimensions. FORMAT.md's table of sizes has the four new rows. No size that built before
  stops building.

- **`InverseBloomFilter`, `HyperLogLog` and `DpswSketch` failed inside an allocation, or
  saturated, instead of refusing a size they could not build.** `InverseBloomFilter`'s
  slots are one array of references, which holds 2,147,483,591: a capacity past that was
  an `OutOfMemoryException`, and from 2^31 an `OverflowException`. `HyperLogLog(2^31)`
  was an `OverflowException`, since the registers are an array of bytes and the largest
  power of two one holds is 2^30; `NewDefaultHyperLogLog` with an error of 1e-5, which
  wants 2^34, cast the count to a `uint` that saturates and complained that 4,294,967,295
  is not a power of two. `DpswSketch` sized a substream, the window to the power of the
  substream factor, with a cast to an `int` that saturates, so a window of 2^40 at a factor
  of 0.9 (2^36 items) was built as a substream of 2^31 − 1 whose checkpoints are a list of
  that many.
  <br><br>
  Each is now an `ArgumentOutOfRangeException` naming the parameter (`capacity`, `m`, `e`,
  `window`), before anything is allocated. A substream may hold at most 2,147,483,591
  items, the longest list; at the default factor of 0.7 that is a window of
  21,445,114,182,289. The readers of `InverseBloomFilter` and `DpswSketch` refuse a
  payload that claims more, as beyond anything built, so FORMAT.md's table of sizes has
  both rows. `HyperLogLog`'s reader needs no check: a payload carries every register it
  claims, and cannot carry 2^31 of them. No size that built before stops building.

- **`BinaryFuseFilter.Build` and `BloomierFilter.Build` failed inside the allocation of
  their array when the set was too large for one.** The fingerprints, or the cells, are one
  array of bytes, which holds 2,147,483,591: from one byte too many the build failed with
  an `OverflowException` (from 2^31) or an `OutOfMemoryException`, after hashing the whole
  set. That is 1,908,641,337 distinct keys at 8-bit fingerprints and 954,204,160 at 16;
  954,204,160, 636,136,107 and 318,068,053 for values of up to 8, 16 and 40 bits. A set with
  more is now an `ArgumentOutOfRangeException` naming `items` or `pairs`, which says how
  many keys it would have held, before any array of the build is allocated. The README's
  and CAPACITY.md's "builds up to" are those numbers, where they said memory. No size that
  built before stops building.

- **`BloomierFilter.Build` refused a legitimate map when two different keys had the same
  64-bit hash, and said a key had appeared twice.** The filter knows a key by its 64-bit
  hash alone and keeps no keys, so two different keys that hash alike are one key to it. If
  they have the same value that costs nothing, and it never did. If they have different
  values the map cannot hold both, and the build is refused, which it always was; but the
  message said "a key appears twice with different values", which for two different keys is
  false and sends the caller looking for a repeat that is not there. The chance is
  `n(n−1) / 2^65` for `n` keys: 0.03% at 100 million, 2.4% at 954 million, the most a map
  of 8-bit values builds, and 39% at 2^32. The `ArgumentException` now says both ways two
  entries can share a hash, gives the odds, and says what to do: build again with a
  different `hash`, which moves the collision. The table of odds is in the XML docs of
  `Build` and in CAPACITY.md. The format is unchanged; widening the identity would change
  it, and has not been done.

- **`DpswSketch` built with a width or depth no `PrivateCountMinSketch` holds, and failed
  on its first `Add`.** Every sketch a window holds is a private sketch, whose row is one
  array and whose rows are one array, so each of the width and the depth is at most
  2,147,483,591. A window given more constructed, and the failure came when the first item
  began the first substream, as that sketch's `ArgumentOutOfRangeException` naming that
  type. A depth past the limit was worse at a small budget: the guard on the noise of the
  window's leanest checkpoint divides the depth by the budget that checkpoint is left, so
  it refused the depth first, as an `ArgumentOutOfRangeException` naming `alpha`.
  <br><br>
  The constructor now refuses a width or depth past the limit with an
  `ArgumentOutOfRangeException` naming `width` or `depth` and saying what the limit is,
  before the checkpoints are built, from the same symbols the private sketch uses so that
  the two cannot drift. The reader refuses a payload that claims either past it, as beyond
  anything built; it loaded one that held no substreams, since no inner sketch was there to
  refuse the size. FORMAT.md's table of sizes has the two new rows. No size that built
  before stops building: the limit is the sketch's own.

- **`DDSketch.WriteTo` failed with an `OverflowException`, or ran off the end of an array,
  for a sign of 2^28 buckets or more.** A sign's run of buckets was copied into a byte array
  sized `buckets * 8` in an `int`, which is negative from 268,435,456 buckets (two gibibytes
  of counts) and wraps to a small positive length from 2^29, so the loop that filled it
  failed with an `ArgumentOutOfRangeException` instead. The constructor allows a run of
  2,147,483,591 buckets, so a sketch could be built and used that could not be written, and
  said so as an overflow. The length is now counted in 64 bits, and a sketch whose payload
  would pass 2,147,483,573 bytes, the most an envelope holds, throws the
  `InvalidOperationException` every structure too large to write throws, naming the size it
  would be and the size one can be, before anything is allocated and with the sketch
  unchanged. That is 268,435,439 buckets between both signs, since the two share a payload.
  Every payload that wrote before is byte-identical, and the reader needed no change: it
  divides the bytes it is given by eight and multiplies nothing.

- **`DDSketch.Quantile` answered infinity for a value in the top binade of the doubles, and
  zero for the smallest at the coarsest accuracy.** A bucket reports 2·γⁱ/(γ+1), and written
  that way the working leaves the range of a double before the answer does: twice γⁱ is
  infinite from 2^1023 (8.99e307) up, and γⁱ itself just past `double.MaxValue`, though the
  answer, (1−a)·γⁱ, is never above the largest value its bucket holds. A sketch holding
  `double.MaxValue` answered `PositiveInfinity` at every accuracy, and `NegativeInfinity` for
  its negative. At the other end, at an accuracy of 0.99 the smallest 29 multiples of
  `double.Epsilon` answered zero, which is not within 99% of anything positive.
  <br><br>
  An infinite answer is now worked out again from two halves of the power, and if that
  overflows too the bucket reports `double.MaxValue`, which is within the accuracy of every
  value it can hold since none is larger; a zero is `double.Epsilon`, likewise. Every other
  answer is the one it was, bit for bit: the stored indices and counts are untouched, so
  nothing persisted changes, and of 249,408 answers swept across 12 accuracies and both
  signs the only ones that moved were the 34,734 infinite and the 58 zero. Among the subnormals the
  answer is still gamma^i rounded to a multiple of 4.9e-324 and then rounded again, which
  can leave it up to a unit (0.75 at worst, measured) outside the accuracy; it is not
  corrected, because a bucket there that answered within the accuracy must keep its answer.

- **A payload whose byte run claimed almost `int.MaxValue` bytes was refused with an
  `ArgumentOutOfRangeException`, not the `InvalidDataException` every other payload the
  library will not read gets.** The reader checked a run against the payload as
  `offset + count > length` in an `int`, and a claim within the offset of `int.MaxValue`
  wrapped that sum negative, passed as short of the end, and failed in `Slice`. A run that
  begins at offset 12 wrapped from a claim of 2,147,483,636, and one at offset 1,000 from
  2,147,482,648; a claim of 2,147,483,584 did not wrap and was refused properly, while
  2,147,483,647 was not. It needs a payload edited on purpose with its checksum repaired,
  and it reached every structure that reads a run of bytes: the Bloom filters, the
  counting, deletable, partitioned, stable, inverse and cuckoo filters (for payloads of
  the layouts before the packed one), `ScalableBloomFilter`, `TopK`, `HeavyKeeper`,
  `VarOpt`, `CountSketch`, the HyperLogLog family, `UltraLogLog`, `DDSketch`, the binary
  fuse, Bloomier and quotient filters, and `InvertibleBloomLookupTable`. The check is now
  made against the bytes that remain, which cannot wrap. The check was the same from 3.2.0
  through 6.2.0, and no payload this library wrote is refused that was not before. The
  other arithmetic in the payload reader and writer was audited for the same pattern and
  holds: the writer's growth was already worked out in 64 bits.

- **`SublimeCountMinSketch` could not read, widen or fold back a row holding counters of
  2^63 or more, and some rows of large counters could not be read at all.** Three separate
  defects, all in 6.2.0, and none reachable by a stream: a counter of 2^63 takes 2^63 adds.
  A payload can hold one, though, since the writer writes every `u64` and the reader checks
  no counter against the total, and a sketch holding one wrote a payload that its own
  reader could not read.
  <br><br>
  Choosing how to pack counters starts by tallying how many have each bit length, and the
  tally had slots for lengths 0 to 63. A counter of 2^63 or more is 64 bits long, so
  reading a payload with one, the `Add` that doubled the rows, and the retune of a crowded
  sketch threw `IndexOutOfRangeException`. The tally has a slot for 64.
  <br><br>
  When no tuning met the search's bound it ended on 16 counters of 32 bits, which no
  512-bit chunk holds, and the array refused it with an `ArgumentOutOfRangeException` naming
  `countersPerChunk`. A row of 1,024 counters reached it with every counter at 2^27 or
  more, and with sixteen of them at 2^54, which take on the order of 10^11 and 10^16 adds
  to a row that narrow. It now ends on the widest stub a chunk
  of 16 counters can keep a pool beside, 27 bits, and the tails arrays hold the rest. Such
  a sketch is slow to add to, since it lays itself out again on every add, but it is no
  longer a payload that cannot be read.
  <br><br>
  The fold back after removals added a counter to its copy and took off the record in signed
  `long`s, so a counter that came out at 2^63 or more was negative and folded to nought:
  no exception, but a key counted 2^63 times reported as not counted. It now sums in 128
  bits and stops at the largest count rather than wrapping.
  <br><br>
  A sketch with no counter that large is unchanged, in memory and on the wire: nothing the
  reader took before is refused, the payload holds counts rather than their packing, and
  the format is as it was.

- **A read refused for its budget named a number that did not load it.**
  `ReadBudgetExceededException.RequiredBytes` was the running total at the allocation
  that failed, so a payload was refused at its first allocation the budget did not cover,
  and refused again, for more, under exactly the budget it had named. A 1.89 GB
  `BloomFilter64` was refused needing 1,893,049,288 bytes, then under that at
  1,893,049,496, then at 3,786,098,576, and loaded at about 3.8 GB. Every read is now
  priced whole before it allocates anything a payload claims: each structure works out,
  from the payload's header and the fields that size it, everything its reader will
  allocate — the payload, every array, every copy of a run, and every structure nested
  inside it, read from the nested envelopes where they lie in the outer payload — and the
  budget is compared with that once. `RequiredBytes` is that total whatever the budget
  was, a budget of it loads the payload the first time, and the message names it once,
  digits grouped, in the call that reads it: `BloomFilter64.ReadFrom(stream, new
  ReadOptions(…))`. A payload too large for its budget to be held is priced from the
  stream without being held, its runs skipped. What each structure is charged is
  unchanged, so every figure in the README's table and in CAPACITY.md stands.

## [6.2.0] - 2026-08-19

### Added

- **`SetSketchVariant.SetSketch2`**, the paper's second construction, selectable on any
  `SetSketch`. Where the default draws an element's run of hash values as exponential
  spacings, this cuts the exponential's domain into one interval per register and draws a
  point from each. That leaves the registers correlated rather than independent, so the
  estimators — all derived under independence — become approximations here where they are
  exact for the default.
  <br><br>
  **Reach for it for small-set accuracy, not for speed.** Measured over 200 keyed streams
  at 256 registers, whose nominal relative error is 6.25%, the second construction's
  spread is 4.47% against the first's 7.12% at ten elements, 4.16% against 5.91% at a
  hundred, and the advantage has gone by ten thousand (6.54% against 6.35%). The paper's
  claim that the correlation helps small sets holds, clearly and monotonically.
  <br><br>
  The paper's *other* claim, that this construction is faster, holds here too, though it
  took a second pass to earn. Testing an interval's start before spending a draw — sound,
  since a point never falls below its interval — cuts random draws by 9.2%, but done
  literally it costs two logarithms per interval and measured about 5% *slower*. Those
  logarithms are avoidable: the test `ValueFor(gamma_i) <= L` unfolds to
  `m - i < m * exp(-a * b^-L)`, whose right-hand side depends on nothing but the bound,
  so it is computed when the bound moves — once every m updates — and each interval costs
  one comparison. That turns the 5% deficit into roughly 6% quicker over 500,000
  additions (median 37.9ms against 40.2ms over nine alternating runs, reproduced).
  <br><br>
  The merge stays exact under the correlated construction — register for register, the
  sketch that adding both sets from the start would have built — and merging *across*
  constructions is refused, since no single sketch draws its runs both ways. Sketches
  using the default are unaffected in every respect, including their persisted bytes.
  <br><br>
  A sketch using the second construction writes a **new format version**, so a release
  before this one cannot read it — the compatibility this library promises runs
  backwards, not forwards. A sketch using the default writes the version it always
  wrote, byte for byte, and stays readable by any earlier release: the version is raised
  only for payloads that actually carry the new field, rather than for every `SetSketch`
  to record a choice most of them did not make. (#121, #127)

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
  was -46 and the fifth percentile -6150.
  <br><br>
  Both persist, and the seed never does. The counters already carry their noise, so a
  payload of them reveals no more than the live structure does, while anyone holding the
  seed could regenerate that noise, subtract it off, and recover the exact counts. A
  `PrivateCountMinSketch` read back can be queried *and* can keep counting, because
  adding touches no randomness. A `DpswSketch` read back draws a fresh unpredictable
  generator for the substreams it goes on to build — sound because those cover records
  disjoint from everything already counted, and privacy composes in parallel over
  disjoint data — so it is resumable but no longer reproducible from its original seed.
  Both readers refuse a payload whose counters are all exact integers, the shape of a
  sketch written with no noise at all. (#104, #120)

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

[Unreleased]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v6.2.0...HEAD
[6.2.0]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v6.1.0...v6.2.0
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
