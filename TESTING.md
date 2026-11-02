# Testing

How tests are written in this repository, and why they are written that way. Most of
this was learned by being wrong: during the 6.x hardening passes, the tests were wrong
more often than the code was, and every principle below exists because its absence let
a defect through — usually a defect in a test.

## The loop

New structures are built red/green: write the test, watch it fail for the stated
reason, implement, watch it pass. Tests added to existing code follow the equivalent
loop in the other direction:

1. **Probe** — measure what the code actually does before asserting anything.
2. **Pin** — turn the measurement into an assertion with stated, justified slack.
3. **Mutate** — break the implementation deliberately and watch the new test fail.
4. **Restore** — and record the mutation table in the commit message.

Step 3 is not optional. A test that has never been seen to fail proves nothing; twice
in this repository a test that *named* a constant could not detect that constant
changing (see "Straddle every rounding"). The commit history doubles as the record of
which defects each test was proven against — including the mutations that *survived*
and why that was acceptable.

**Commit before step 3.** The loop asks you to break the implementation on purpose and
put it back, and the obvious way to put it back — `git checkout -- <dir>` — discards
everything uncommitted in that directory, not only the deliberate break. Work that has
not been committed yet is exactly what you are most likely to be mutating, because you
just wrote it. This was learned twice in one session, both times losing a nearly
finished change to its own verification step. Commit the work, then mutate, and revert
the single file you touched rather than the tree.

## Principles

### Pair every bound with a vacuity guard

A tolerance-based assertion passes when the mechanism under test never engaged. The
Count-Min bound test refuses to pass if no estimate was overcounted; the DDSketch
boundary test requires that most inputs actually saturated the bound; the CountSketch
test requires collisions. The pattern: assert the bound, *and* assert the scenario
exercised the thing the bound protects against. `TestSketchBounds.cs` shows the shape.

A refinement from DDSketch: when the bound is saturated by construction (its worst
case error is exactly the promised accuracy), assert *near-saturation* too — a result
far better than promised means the structure is finer than asked for, which is a
sizing defect wearing accuracy's clothes.

**Guard both directions when the scenario can fail either way.** A sweep asserting that
a stop rule fires correctly is worthless if the rule never fired, and equally worthless
if it fired every time; the SetSketch2 sweep fails on both. One-sided vacuity guards
are the common case, but a rule with two outcomes needs two.

**And check the assertion is as strong as its name.** `DpswSketch`'s round trip draws a
fresh generator, so a test asserted that a restored window's noise differs from a
seeded twin's — and it did, and it would have done just as well against a *fixed*
constant, because a constant differs from the twin's consumed state too. The test was
named for unpredictability and proved only difference. What proves the property is two
reads of one payload diverging from each other. When a test's name claims more than
"these numbers differ", ask which mutation it would actually catch, and try that
mutation.

### Probe, then pin

Never invent an assertion constant. Run the measurement first, print the numbers, and
set bounds from measured values plus stated slack (spread tests use a few standard
errors of the estimator, `sd/sqrt(2(T-1))` for a spread). The commit message quotes
the measurements so the bound's provenance survives.

### Straddle every rounding

A constant that sits upstream of a rounding step is invisible to any test whose
inputs don't straddle the boundary. This bit three separate times:

- HyperLogLog's `1.04`: power-of-two register rounding absorbed it at every "nice"
  error value — `(1.04/0.01)² = 10816` and `(1/0.01)² = 10000` both round to 16384.
  Only `e = 0.032` and `0.016` straddle a boundary.
- The cuckoo filter's `0.95` load factor: power-of-two bucket rounding absorbed it at
  n = 10,000 *and* 100,000. Only n = 16,000 discriminates.
- The counting-filter merge clamp: with 4-bit counters the byte cast can never wrap,
  so only 8-bit counters expose the defect.

When a formula ends in `Math.Ceiling`, `Power2`, or a byte cast, choose test inputs
on both sides of the step, and say in a comment which inputs discriminate and why.

### Run the promise at its coarsest legal corner

The parameters a suite reaches for are the ones a caller would reach for, and those are
where a one-unit error is a rounding nuisance. At the coarsest corner the guards allow,
the same error is the whole answer. SetSketch's default base of 1.001 hid two
off-by-ones worth 19% to 100% at coarse bases; at γ = 199 a DDSketch bucket off by one
is a factor-199 answer.

So each promise runs at the edge as well as the middle: HyperLogLog at m = 16, its
plus at precision 4, DDSketch at α = 0.99, the theta sketches at k = 1, Count-Min and
Count Sketch at ε = δ = 0.5. Two things worth knowing turned up on the way:

- **A contract can be unreachable at sensible parameters.** Count-Min's "overcounts by
  more than εN with probability at most δ" had never been exercised anywhere, because
  a uniform stream cannot reach εN. It needs the coarsest sketch *and* a deliberately
  skewed stream before there is anything to measure.
- **A corner can be where no behavioural window exists at all.** The theta estimator's
  variance at k = 1 is infinite, so nothing distributional can be asserted there. The
  exact rule — θ is the (k+1)-th smallest hash — is the only possible test, which makes
  the corner the strongest place to state it rather than the weakest.

Where a coarse corner passes on first pinning, say so in the commit message. The pass
that added these found no defects, and that is a result worth having recorded rather
than an absence of one.

### Deterministic randomness only

Streams are keyed by trial index (`$"t{t}-item-{i}"`), never drawn from a seeded RNG
shared across assertions and never from time. Every reported number reproduces
exactly; a failure is a defect, not a bad draw. Where an RNG is unavoidable (shuffle
orders, churn schedules), it is `new Random(<literal>)`.

### Test the distribution, not one draw

A probabilistic structure promises a distribution. One count landing close proves
nothing — the estimator is unbiased, so any single draw can land anywhere. The spread
tests run 40–80 independent keyed streams and assert the two moments **separately**,
because they fail separately:

- Replacing HyperLogLog's alpha with the m=16 constant leaves the spread at ratio
  0.92–0.98 while running every trial ~6.5% low. Only the mean sees it.
- Fully correlating SimHash's hyperplanes leaves the mean within one standard error
  while the spread explodes from 3.8 to 30. Only the variance sees it.

### Prefer exact oracles to tolerances

Wherever an exact statement exists, test it instead of a bound — there is no
tolerance for a defect to hide inside, and every failure reproduces:

- **Algebraic identities** (`TestAlgebraicIdentities.cs`): merging the sketches of
  two streams equals the sketch of the concatenated stream *byte for byte*, via the
  persisted payload. One comparison covers every field of state, including
  bookkeeping no behavioral test reads. Order-invariance, unwind-to-empty, and
  duplicate-blindness get the same treatment.
- **Model-based oracles** (`TestModelBased.cs`): the quotient filter is churned
  against an independently-computed fingerprint multiset, and *every* answer must
  match — including the false positives, which are predicted individually. A
  fingerprint filter's false positives are not noise; they are the collisions.
- **Characterizations**: when byte-level identity is out of reach (the theta sketch
  is order-dependent by design), state the invariant that holds for every valid
  state — "the values are exactly the input hashes below theta" — and hold every
  operation's output to it. This test found a shipped bug on its first run.

**An identity test is only as strong as the asymmetry of its inputs.** The DDSketch
merge identity originally had the value 1 in both streams, so a merge that forgot to
propagate the other side's minimum passed — the minimum never needed propagating.
Construct inputs so that every field the identity covers *must* move.

### Pin the geometry to the paper

The sizing formulas (`TestStructureGeometry.cs`) are restated from their papers, not
read back from the implementation. This is the only defense against a specific defect
class: the bounds these structures promise come through Markov-style inequalities and
are loose, so a sketch 2.7× too narrow or two rows too shallow still lands inside its
stated error most of the time. Both of those mutations were invisible to every
empirical test in the suite; only the formula assertions caught them. Measuring the
outcome cannot see a quietly-degraded structure. Comparing the size against the
closed form can.

### Anchor outside your own reading

Every test above shares an author with the code it checks. Round trips, merge
identities, model oracles, worked examples derived while implementing -- each is
an internal anchor: if the paper was misread, the test was written from the same
misreading, and the two agree with each other instead of with the paper. This is
not hypothetical. Sublime's counter encoding was caught with digits 1 and 2
swapped behind a worked-example test derived from the same wrong reading, seven
of its eight tests passing; the persistence corruption tests for three
structures died at the checksum while asserting they tested the guards behind
it; DPSW's budget series summed to twice the budget under a misread exponent
with every utility test green.

The defence is a second derivation that did not pass through this repository:
values the paper itself prints, a reference implementation's arithmetic, or --
weakest but real -- the golden bytes this library wrote in an earlier version,
which anchor today's reading to yesterday's. The external-anchor tests (the
sizing rules in `TestStructureGeometry`, the estimator pipelines in
`TestHyperLogLogInternals`, `TestHyperLogLogPlus` and `TestDDSketch`, the
order-statistic pins in the theta sketches) restate printed formulas and
evaluate them at hand-checked points, and each header says which source the
numbers came from and when it was checked against that source.

Two adjudications from the pass that added these are worth keeping:

- **A self-consistent offset can survive everything but the stored bytes.**
  Shifting every DDSketch bucket index up by one and the reported midpoint down
  to match passes 844 of 846 tests -- including the new anchor rows, which see
  the same right answers. The two that fail are the persistence fixtures.
  Behavioral equivalence is not format equivalence: the fixtures own the paper's
  index convention, and regenerating them to green a build is how that ownership
  would be silently signed away.

- **A term can be real code and still be untestable where you first look.**
  HLL++'s tau correction rides 2^-(64-p): at precision 12, deleting it entirely
  survived, twelve orders of magnitude below the harmonic term. The state where
  tau carries ten percent of the denominator needs precision 18 and 194,000
  registers parked at value 46. A survivor is not always an equivalent mutant --
  sometimes it is a test parameterized where the mutation cannot matter.

Where no external source exists, no external anchor is possible, and the honest
move is to say so rather than dress an internal test as one. KeepsakeBox, the
Buckets bit-packing, the Bloomier filter's encoding and the inverse Bloom
filter are this repository's own designs: their golden fixtures pin that the
format is stable, not that it is right, because there is nothing outside the
repository for them to be right against. The quotient filter's model oracle
recomputes every fingerprint independently but was written by the same hand as
the filter; the stable filter is anchored to its own measured behavior, which
catches the formula disagreeing with the mechanism but not both agreeing on a
misreading. These are the structures where a second reader adds the most.

### A test hook must run the code it vouches for

Internal hooks let a test reach a rule the public surface cannot. A hook that
*reimplements* that rule instead of calling it proves only that two copies agree, and
the copy that ships is not the one under test.

SetSketch2 decides whether an interval is worth drawing from before spending any
randomness. A sweep held that decision to the literal, slow version of it at every
interval and every bound — and passed while a mutation loosened the comparison in the
insert loop from `<` to `<=` and changed the sketch, because the loop had the
comparison written out and the hook had it written out again. The fix is one line: the
loop calls the hook. Then the sweep covers the rule that runs.

The tell is that the hook's body duplicates an expression rather than delegating. When
adding one, check the production path calls it — a mutation applied to the *loop* and
not the hook is the cheapest way to find out.

### A sweep named "every X" must derive its roster

A test that sweeps the whole library is only as complete as its list, and a list typed
out by hand is written by whoever last remembered to extend it. It agrees with itself
perfectly: it is named "every structure" and it means "every structure someone thought
of". Nothing fails when the next one is added.

Six sweeps here made that claim and none of them was true. The empty round trip reached
26 of 33 structures, the corruption sweep and the read-as-another sweep 30, and the
three hash sweeps 12 of 25, 11 of 20 and 11 of 20. The gaps tracked the release: the
structures 6.2.0 added were the ones absent, and `BloomFilter` itself had fallen out of
the empty sweep at some earlier point without anyone noticing.

The sharpest instance was the read-as-another sweep, which ends by asserting that no two
structures share a persistence id — computed across its own roster. Three structures were
missing from that roster, so a duplicate id handed to any of them would have passed the
test written to catch exactly that. A completeness check downstream of an incomplete list
is not a check.

The fix is to derive the roster from something a new structure cannot omit itself from.
`StructureId` is that for persistence: a structure that is not in it cannot be persisted
at all. For the hash sweeps there is no enum, so the roster comes from the library's
public surface by reflection — the types declaring `SetHash`, and the types with a
constructor or static factory taking a hash — filtered to those that persist, which is
what keeps the hash plumbing out. A structure that cannot be swept is exempted *with its
reason*, and the exemption is checked in turn: one naming a structure the sweep does
cover has gone stale and fails as loudly as a gap.

The proof that this works is not that the sweeps pass. It is that adding a member to
`StructureId` fails all four persistence sweeps by name, and dropping the persistable
filter from the reflected roster fails the constructor sweep on the hash plumbing it
was there to exclude.

The same treatment applies past the test suite. FORMAT.md is the one artefact here
written for someone who does not have the library — it exists so a payload can be
decoded from the bytes alone — and it had drifted the same way, silently, because a
document has no build to fail. Two structures shipped in 6.2.0 with no section at all
and a third had a section about its variant byte that never said what the rest of its
payload holds. It is now embedded in the test assembly and held to `StructureId`: every
structure has a section, and every section's heading names the id a reader will find in
the envelope. A section under the wrong id is worse than a missing one, because someone
decoding by hand gets an answer.

Prose counts belong in the same category. `PersistenceFormat.DefaultVersion` explained
that two filters write version 2 and "the other eleven" version 1 — true at thirteen
structures, wrong from the next release onward. A count in a comment is a roster
maintained by hand, and the fix is to remove the number rather than correct it.

Not every sweep can be one loop. `TestPersistenceHostilePayloads` refuses payloads whose
fields are internally absurd, and what counts as absurd differs per structure because the
fields differ — there is nothing to iterate. Its roster is a map from each structure to
the test that carries it, and both halves are checked: the structures come from
`StructureId`, and each named test must exist and be a test, so an entry pointing at
something renamed or un-attributed fails rather than silently vouching for nothing. That
sweep had drifted the *other* way from the rest — the newest structures were the
best-covered, and fifteen of the oldest had no field-level test at all, guards and all.

The rosters also disagree with your own audit, which is the point of deriving them. The
span-equivalence sweep was short by six, and adding those six turned up two more the
audit had passed over — `BinaryFuseFilter` and `BloomierFilter`, which are built once
from a whole key set and have no incremental path to compare. They are exempt with that
reason, and the exemption is checked. The roster found them; reading the file had not.

Filling the six sweeps found no defects in the library — every structure did honour a
constructor hash, did refuse to replace one once occupied, and did catch corruption
anywhere in its payload. That is the point worth keeping: the sweeps were not wrong
about the structures they reached, they were silent about the ones they did not.

### Bound the work, not the wall clock

The quotient-style filters walk their metadata in while-true loops whose
termination rests on the three-bit invariants, and the fuse builder retries
construction until it succeeds. Defects there do not fail -- they spin, or they
quietly do table-sized work per operation while answering every query
correctly. A wall clock cannot adjudicate either kind: the Stryker section
below shows Timeouts mostly measuring the machine, and this suite's machine
sleeps mid-run. `TestBoundedWork` counts instead: internal work counters (slot
reads, eviction kicks, build attempts) with probed-plus-slack ceilings and
vacuity floors, so a scan that wanders past its cluster or a budget that stops
being the budget turns into a deterministic red assertion at full parallelism.

Instrumenting also measures what was previously only asserted to be
deliberate: a Memento add rewrites its whole cluster through the ordinary
insert path, and that costs a probed 44.7 slot reads per add at load 0.11 and
2,269 just under the 0.75 expansion threshold, where the filter spends its
working life. The choice is documented in the source and now has a price tag
and a regression pin. Two structures need no counters: the Vale walks are all
for-loops bounded by the pool, and HLL++'s sigma/tau converge by
floating-point fixed-point with the one divergent input guarded and
anchor-pinned. Memento and Infini segment counters reset when expansion
rebuilds a segment, so work is only ever measured on expansion-free spans.

### Sequences find what scenarios cannot

Many of the defects this library has shipped needed a particular *sequence* to appear: a
refused cuckoo insert that lost an element the filter already held, a `Reset` that left
the item count behind, a restored filter that restarted its random sequence, a payload
the writer produced and its own reader refused after one run of adds. A scenario test
builds a structure one way and asks it one kind of question, so none of those were in its
reach. The planted `Buckets.Reset` sizing error that opened
[#144](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/144) survived
the whole suite for the same reason: nothing reset a filter and then saved, loaded or
merged it.

So the sequence files run long, deterministic sequences of every operation a structure
offers — `Add`, removal, `Merge`, `Reset`, `TestAndAdd`, save and load — in every order,
and hold the structure to an oracle after every step. Each oracle is exact, and a
structure gets the ones its semantics allow:

- **A byte-exact twin**, for any structure whose state is a function of its history. A
  second instance is given the same history by plainer means: a new instance stands in
  for `Reset`, nothing for save and load, the other side's adds for `Merge`, `Test` then
  `Add` for `TestAndAdd`. The persisted bytes are compared, so one comparison covers every
  field, bookkeeping nothing queries included. The Bloom family gets this after every
  operation. So do the sketches, where the twins are a saved instance against one never
  saved, a reset instance against a fresh one, shards merged in a keyed order against the
  whole stream, and a structure with everything removed against a fresh one; those compare
  every answer along the way and the bytes at the end.
- **An exact model or guarantee**, where the semantics can be restated cheaply. The
  counting filter's saturating counters, the deletable filter's bits and collision
  regions, the inverse filter's slots, the quotient filter's fingerprint multiset and the
  cuckoo filter's (fingerprint, bucket pair) multiset predict every answer, false
  positives included. The theta and tuple sketches are held to "exactly the input hashes
  below theta", and the invertible Bloom lookup table to the net difference whenever a
  decode succeeds. Where no such model exists, the guarantee that holds whatever the
  structure chose is used instead: no false negative, a Count-Min estimate never below
  the true count, and for Infini and Memento a one-to-one assignment of stored entries to
  live keys in which each entry remembers only bits its key's hash really has.
- **Exhaustive small scope with an independent invariant checker**, for the quotient,
  Infini, Memento and cuckoo filters. Their metadata walks are while-true loops whose
  termination rests on invariants that a behavioural test sees broken only when a later
  query happens to walk through the damage. `QuotientFamilyInvariants` decodes each table
  from first principles, with its own unpacking and none of the library's walks, and
  checks every slot's bits, the counts and the stored entries after every operation. The
  sweeps apply every operation to every reachable state of tiny tables, breadth first and
  deduplicated on the persisted bytes, which is sound because those bytes are the whole
  state. Where the table is small enough the exploration closes, and then it covers every
  sequence of every length. Structural bugs appear at small sizes, and an exhaustive run
  there beats any sample of it.

Roughly: a structure whose state follows from its history gets the twin; one with an
exact abstraction gets the model as well; one whose layout can be wrong without any
answer showing it gets the checker and the exhaustive runs on top.

**The files**, and how each derives its roster:

- `TestFilterSequences.cs` is the Bloom family. The roster is the `StructureId` members
  whose name contains `BloomFilter`; each is covered by a test named for it, or exempted
  with a reason. Each spec is also held to the public surface by reflection, so a type
  that gains `Reset`, `Merge` or `TestAndRemove` fails until its spec runs it. What the
  file sequences is read off those test names, and `CuckooBloomFilter`'s exemption is
  checked to be one the quotient file sequences.
- `TestOperationSequences.cs` is the sketches and samplers. The roster is all of
  `StructureId`: the built-once structures are exempt, and the exemption is checked by
  reflection to have no `Add`, `Remove`, `Merge` or `Reset`. Each spec is also held to the
  public surface: a sketch that offers an operation no spec sequences fails unless the
  operation names the existing test that covers it, which must exist and carry
  `[TestMethod]`. The filters it leaves to the other files are what those files say they
  sequence, not a list kept here, and the reason names the file.
- `TestQuotientFamilySequences.cs`, with `QuotientFamilyInvariants.cs`, is the quotient,
  Infini, Memento and cuckoo filters. The roster is every public persistable type that
  offers `TestAndRemove`, held to a table of what is sequenced, the tests that carry it
  and the operations they apply. The named tests must exist and carry `[TestMethod]`, and
  every public operation of a sequenced filter must be applied or classified as a query,
  checked in both directions. `CountingBloomFilter` and `DeletableBloomFilter` are exempt:
  their removal does not delete a stored entry, and their model is the Bloom file's, which
  is checked to sequence both.

**The rules**, each an earlier principle applied here:

- *Steps are keyed.* The operation at a step is a pure function of its name — the Bloom
  file hashes `{structure}[{config}]/t{trial}/s{step}`, the sketch file
  `seq{seed}-{stream}-{i}` — so a failure names its step and reproduces from the name
  alone. The one generator is the quotient file's churn schedule, `new Random(<literal>)`
  as "Deterministic randomness only" allows; the exhaustive runs have none.
- *Guard against vacuous runs.* A sequence that never reset, never saved a reset filter or
  never crossed the threshold it is named for says nothing about it, and passes. Each run
  tallies what it did and fails if it did not exercise the thing: every operation its
  schedule offers, a reset followed by a save and by a merge, a counter that saturated, a
  refused insert and an insert that needed kicks, a configuration seen on both sides of
  each of its thresholds (sparse and dense, filling and full, before and after an
  expansion), an exhaustive run that reached a full table or an expansion. The last is
  not decoration: the first Sublime configuration used the default growth exponent, never
  doubled, and passed every property vacuously.
- *Bounds are counts, never a clock.* A step and trial count, a seed count and a length,
  and for every exploration a transition budget that fails by count when the state space
  outgrows it, with a depth where the space does not close. Nothing in these files reads a
  clock; see "Bound the work, not the wall clock".

**Adding a structure.** Put it where its semantics put it: a Bloom-style filter in
`TestFilterSequences.cs`, as a test named `Test<Id>SequencesMatchTheirTwin` or
`...MatchTheirModel`; a filter that deletes a stored entry in the quotient file's table,
with a test that applies it; anything else as `Specs` entries in the sketch file, one per
configuration at a coarse corner, with a `Phase` that shows its thresholds were crossed.
If you do not, `TestTheSequenceSweepReachesEveryStructure` fails by name for any
`StructureId` no file reaches, `TestEveryBloomFilterHasASequenceTest` for one whose name
contains `BloomFilter`, and `TestTheSequenceSweepReachesEveryFilterThatDeletesAnEntry`
for anything with a public `TestAndRemove`. A new operation on a quotient-family filter
fails `TestEveryOperationTheSequencedFiltersOfferIsAccountedFor` until a sequence applies
it or it is classified with a reason, and the Bloom file's
`AssertSpecRunsWhatTheTypeOffers` does the same for `Reset`, `Merge` and removal. Every
exemption is a claim, and is checked like one.

**Accepted limits:**

- The twin calls the same `Add` as the subject, so a defect in `Add` itself is left to the
  per-structure tests for the filters whose state `Add` alone determines.
- Scalable growth has no model, only the twin with adds in order and the
  no-false-negative promise. The stable filter has no oracle for its answers, since it
  evicts; it is held to its twin and to the element just added.
- Infini and Memento shorten fingerprints as they grow, on a schedule that is the filter's
  own choice, and it is not modelled. The assignment guarantee holds whatever the
  schedule, and an entry older than its expansions is caught, but a loss of
  false-positive accuracy is only partly covered.
- `DpswSketch` is checked for layout only, because it draws fresh noise on read. VarOpt's
  total weight and HeavyKeeper's one-sided bound are not modelled; both are held to their
  twins.
- A mutant that contracts a `SublimeCountMinSketch` one removal early survives this
  harness and the scenario tests alike. Counts are preserved exactly, only timing moves,
  and killing it needs a pin on the width schedule that neither layer has.
- Theta and tuple `Intersect` and `Difference` are not interleaved in the sequences, where
  `Union` is. Single-scenario tests cover them, and `TestModelBased` holds a theta
  sketch's output of each to the same characterization. They are named in the sketch
  file's `CoveredElsewhere` with the test that covers each.
- No model is anchored outside this repository; see "Anchor outside your own reading".

**Deliberately not done**, for this technique:

- **An opt-in deep mode.** The default bounds found every defect the sequences have
  found and, with the vacuity guards the files now carry, killed every planted mutant but
  the Sublime one above. Exploratory runs at about 3,000,000 Bloom-family operations and
  at Memento depth 10 found no defect; the one failure was a premise, that the 4-bit
  counting configuration never saturates, which at that length it once did. A mode that
  costs more and finds nothing is not worth carrying.
- **One engine for all the files.** They drive different things: a spec and a twin per
  step, properties over generated sequences, and a breadth-first exploration beside a
  literal-seeded churn. A shared engine would be the lowest common denominator or a
  framework larger than the files it served. What they share is the rules above.
- **A Stryker rerun here.** The `Buckets.cs` sweep in the next section predates these
  files, and its survivor list will look different with them in. Rerunning it on the
  current tree is [#173](https://github.com/mattlorimor/ProbabilisticDataStructures/issues/173).

## Mutation testing

Manual, targeted mutation is part of every test commit (step 3 of the loop).
`scripts/mutate.sh` carries the mechanics; a run is a scratch script naming the edits,
which belong next to the commit they verify:

```bash
source scripts/mutate.sh
mutation_target ProbabilisticDataStructures

MUTATION_FILTER='FullyQualifiedName~TestHyperLogLogInternals' \
run_mutation "alpha 0.673 -> 0.697" \
    ProbabilisticDataStructures/HyperLogLog.cs \
    'return 0.673;' 'return 0.697;'

mutation_done
```

It restores the target between edits, refuses a pattern that does not name exactly one
place, distinguishes a mutation the tests killed from one that would not compile, and
checks the tree still builds at the end.

**It refuses to start on a dirty target.** The restore is `git checkout -- <dir>`, which
discards every uncommitted change in that directory and not only the deliberate break.
That rule was learned three times before it was enforced: twice by losing a nearly
finished change to its own verification step, and once by losing a fix, re-running the
harness, and reporting a mutation table measured against the tree without it. Commit
first — the commit is what the table is describing anyway.

### Several mutants at once

Each mutant waits for the one before it: a build and a run of the whole suite, in one
tree. Two mutants cannot share a tree, but copies of it can run side by side, and a
worker with a long list can ask for them:

```bash
source scripts/mutate.sh
MUTATION_JOBS=4 mutation_target ProbabilisticDataStructures
# run_mutation lines exactly as above
mutation_done
```

Unset or 1 is the serial run above, unchanged. With 2 or more, `mutation_target` sets up
copies and what changes is this:

- **Your tree is never touched.** Each copy is a local `git clone` checked out at HEAD,
  under `$TMPDIR` (`/tmp` when that is unset). It builds the unmodified commit once, so
  that every mutant after it is an incremental build, and then takes the next unclaimed
  mutant from one shared queue until none are left. A shared queue rather than a share
  each, because a mutant that will not compile costs seconds and one that survives costs
  the whole suite. The copies are removed when the run ends, fails or is interrupted.
- **`run_mutation` only records the mutation.** `mutation_done` runs them all, then
  prints the verdicts in the order they were given, in the same format, and the same
  summary. Nothing prints before that. A `MUTATION_FILTER=... run_mutation` still scopes
  that one mutation. An edit that does not apply is reported as it is in the serial run,
  as a `PATTERN ERROR` among the verdicts, since nothing checks it until a copy runs it;
  `run_mutation` itself returns 0.
- **The commit is what is measured, so the guard is wider.** The copies hold the
  committed tests as well as the committed library, so `mutation_target` refuses an
  uncommitted change to *any* tracked file, not only those under the target. Untracked
  files are noted, and they are not in the copies.
- **It costs disk and builds.** Every copy is a clone, a full build of every project in
  the solution and a full set of test outputs, in a directory that may be memory-backed
  (point `TMPDIR` somewhere with room if it is). The first mutant in each copy waits for
  that build, so a list of two or three is slower this way than one at a time.

A build and a test run already use many cores, so copies compete for them instead of
adding to them. The gain is real and well short of the number of copies, and a count
above the machine's CPUs is lowered to it. Measure on your own machine before settling
on a number; a few copies is where to start.

Whole-file sweeps use Stryker.NET, 5.0.0 or later (below). The config carries the
settings; the scope belongs on the command line, per run:

```
dotnet tool restore
cd TestProbabilisticDataStructures
dotnet stryker --mutate "**/QuotientFilter.cs" --mutate "**/Buckets.cs"
```

Scope every run. Stryker is not part of any build, the full library would generate five
thousand mutants, and the output needs human adjudication anyway. It is a periodic
audit for files whose tests have not been through the loop above, not a gate.

Scoping trims the mutants, not the coverage pass that comes first. That pass runs the
whole suite serially under Stryker's instrumentation, and code outside the scope slows
with it, so its cost is set by the heaviest tests anywhere. On the frozen tree below, a
Buckets.cs sweep took twelve minutes end to end. That figure is the frozen tree's, and
it is not what a sweep costs now.

Measured on the current tree (the tree of
[#143](https://github.com/mattlorimor/ProbabilisticDataStructures/pull/143), then 930
tests; one 32-thread machine, Stryker 5.0.0, the config in the repository), the same
one-file sweep did not finish. The initial serialized run of the suite took 8 min 18 s.
The coverage pass after it was stopped at 2 h 39 min with 373 of those tests done, and
no mutant had been tried. The Memento tests dominate the pass, though the Memento
filter never touches `Buckets`: one that takes 15.7 s in an ordinary parallel run took
74 min 35 s under it, and another that takes 8.2 s took 16 min 20 s. So on today's suite
a one-file sweep at the default settings is a matter of hours, not minutes.

**Before 5.0.0, Stryker and this suite's parallelism did not mix.** The test assembly
declares `[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]`, and Stryker
assumes it controls all parallelism — it serializes VsTest at the *container* level,
which in-assembly workers ignore. Through 4.16.0 that broke both of its modes,
silently and in opposite directions. Four runs on one frozen tree (Buckets.cs scope,
531 tests), whose true score with every verdict hand-adjudicated is 74.80%:

| coverage-analysis | MSTest workers × Stryker concurrency | reported |
|---|---|---|
| perTest | 10 × 5 | 66.14% — 16 false survivors; hot-path mutants misclassified `static` |
| off     | 10 × 5 | 100.00% — 41 wall-clock timeouts counted as kills, 32 on undetectable mutants |
| perTest | 1 × 5  | 78.74% — zero false survivors; five detections over the true score (below) |
| off     | 10 × 1 | 74.80% — exact |

The false survivors are not attribution noise: for every one hand-checked, running
exactly the tests Stryker itself listed under `coveredBy` kills the mutant — a
deleted guard `throw` "survived" the two tests that assert that throw. And a Timeout
is a detection only when the mutant hangs the code; at five concurrent full-suite
sessions times ten workers on ten cores, it mostly measures the machine. (The
40.52% / 97.96% comparison an earlier revision of this section drew came from runs
whose suite grew between them and which carried both defects — superseded by the
table above.)

Root cause, found independently upstream as
[stryker-net#3757](https://github.com/stryker-mutator/stryker-net/issues/3757):
Stryker always intended to emit `<DisableParallelization>` for MSTest, but
`DetectTestFrameworks` cleared the MSTest flag instead of setting it (`&= ~` where
`|=` was meant), so the countermeasure was dead code. Fixed in
[stryker-net#3760](https://github.com/stryker-mutator/stryker-net/pull/3760) and
shipped in 5.0.0, which this repository pins. The emission is unconditional per framework, so
every session now runs its tests serially, with no edit to the assembly and no pin on
Stryker's concurrency. The runsettings 4.16.0 and 5.0.0 write for this suite differ in
that one element, `<DisableParallelization>true</DisableParallelization>`.

**The upgrade, rerun on the same frozen tree.** One machine (32 threads), one tree,
and one change per row; every Survived and Timeout verdict 5.0.0 reported was then
checked by applying the mutant by hand and running the whole suite:

| Stryker | config | reported |
|---|---|---|
| 4.16.0 | defaults: perTest, concurrency 16 | 59.06% — 20 false survivors, against 16 at 10 × 5 |
| 5.0.0  | defaults | 78.74% — zero false survivors; five Timeouts on equivalent mutants |
| 5.0.0  | defaults + `additional-timeout: 60000` | 74.80% — exact |

Serial sessions cured the survivors and exposed the clock. The five spurious Timeouts
are equivalent mutants on the hottest paths — the `>`→`>=` and `<`→`<=` clamps in
`Increment`, `Set` and the `Max` setter, and `>>`→`>>>` on a non-negative operand in
`GetBits` — each covered by dozens to hundreds of tests. Run one at a time, those
tests outlast the budget Stryker computes, so it ended each session a few
milliseconds past the budget with every finished test passing. Applied by hand, each
mutant passes the whole suite at normal speed. The perTest 1 × 5 row above reports
the same 78.74%; its excess was never itemised, but it ran the same serialized
sessions, and five spurious Timeouts are exactly the gap.

`stryker-config.json` therefore adds a minute to every computed budget with
`additional-timeout: 60000`. Detection loses nothing by it here: of the eleven genuine
Timeouts, seven are recursion mutants in `GetBits`/`SetBits` that overflow the stack
and crash the test host within a couple of seconds, and the other four fail tests
outright. The margin is not a licence to trust a Timeout — a slower machine or a
heavier test can eat it — so they are adjudicated like survivors, below.

What `dotnet stryker` does in this repo with no extra steps: per-test coverage at
Stryker's default concurrency, every session serialized by Stryker itself, and the
budget margin above. No assembly edit is needed for any sweep.

**A survivor can mean the mutation is owned by a different layer.** Halving Count-Min's
width survives its δ-contract test at any parameters — the bound comes through Markov
and is loose enough to absorb it — and dies five times over against the geometry
restatement. Narrowing a cuckoo filter's fingerprint by a bit slips under the rate the
caller asked for and fails the behavioural ceiling measured beside it. Neither is a
gap; both are the layering working, and running the mutation against each layer in turn
is what shows which one owns it. Record that in the table rather than only the number
killed: "survived the contract, killed by the geometry pin" says something the count
does not.

**Survivors are leads, not verdicts — and so are Timeouts.** Hand-apply each and run
the suite before believing either. The residue that survives adjudication here is
real and accepted: exception-message wording (below), internal guards reachable by no
public path (closed with direct internal tests in `TestBuckets.cs`: the length check
on restore, the zero-width constructor, the geometry guards on `Union` and `Add`, and
the wide-bucket union beside them), and equivalent mutants (`>`→`>=` where both arms
write the same value, `>>`→`>>>` on an unsigned operand). Document these rather than
contorting a test to kill them.

**What is asserted about an exception.** In general its type, and not its wording: a
test that pins a sentence breaks when someone improves the sentence and proves nothing
about the guard. A fragment of the message is asserted where one type stands for
several guards, so that the type alone cannot say which of them fired. The
hostile-payload refusals are the clearest case. Every one is an `InvalidDataException`,
a payload can be absurd in more than one way, and a test that checked only the type
would pass when a different guard caught the payload first, or when a later, incidental
check did. So each names the reason by a short phrase (`AssertRefused(..., expected)`
in `TestPersistenceHostilePayloads.cs`) to tell the refusals apart. The merge
mismatches do the same where the exception names which parameter differed. The fragment
is the condition, not the sentence around it, which is why a mutant that rewords the
rest of a message survives, as it should, and one that deletes the guard does not.

## Persistence

- **A payload full of transcendentally-derived floating point cannot have a
  byte-exact write fixture.** `Math.Log`, `Math.Pow` and `Math.Exp` are not
  guaranteed bit-identical across platforms in .NET — only `Math.Sqrt` and plain
  arithmetic are correctly rounded. The private structures' payloads are mostly
  Gaussian noise drawn through `Math.Log`, and their first write fixture passed on
  macOS and failed on Linux *and* Windows at one counter, one unit apart in the low
  byte of a double. Note which way the luck ran: the `PrivateCountMinSketch` fixture
  passed everywhere, and would have sat in the suite as a latent failure waiting for
  a runtime update. Both were replaced by *layout* pins — the payload's exact length,
  which catches a field added, dropped or a seed quietly written — with reordering
  still caught by the read fixtures, since those decode old bytes into the wrong
  places. Reading is unaffected: it compares and adds stored doubles and touches no
  transcendental, so a stored sketch answers identically everywhere.

- **Bytes read forever; written bytes may change.** Golden fixtures pin both
  directions separately: `thetasketch-v1.bin` is the read-side witness (bytes 6.0.0
  wrote must load forever), `thetasketch-v1b.bin` pins the current writer. A bug fix
  that changes the writer's output regenerates the write fixture and *never* touches
  the read fixture.
- **A structure reads the format versions it defined, and one table says which.**
  `PersistenceFormat` lists them per structure, and `TestPersistenceFormatVersions`
  relabels a valid payload of every structure to each version it never defined, with the
  CRC repaired so the version is the only lie. It asks for the explanation, not just an
  exception: the layout checks further down refuse some of these payloads by accident (a
  stable filter labelled 3 is short a field and dies on its length), so a test that only
  wanted a throw would pass with the version rule deleted. The roster is `StructureId`,
  FORMAT.md's table of versions is held to the same table by `TestFormatDocumentation`,
  and a defined version with no stored fixture fails
  `TestEveryDefinedVersionHasAStoredPayloadThatReads`. Adding a version to a structure
  is one entry in the table and one fixture.
- Corruption is tested exhaustively at distance 1 (every single-bit flip must be
  rejected) plus structured hostile payloads for what bit flips can't reach. This is
  stronger than random fuzzing for the same class and fully reproducible.
- Round-trip tests must cross the structure's interesting thresholds. The theta
  sketch's trim-boundary bug survived because every round-trip test used sub-trim
  streams; the first sketch to see real volume was the first to fail.
- **Round-trip at the sizes the constructor allows, not the sizes a test reaches for.**
  From 3.2.0 until it was fixed, many readers refused sizes above a million as "beyond
  anything this library builds" while the constructors built far more, and every
  round-trip test here sat comfortably below the ceiling. `TestLoadWhatItBuilds` now holds
  each structure to one limit per size, shared by its constructor and its reader, and
  sweeps `StructureId` for it. Where a structure at or past the old ceiling is too heavy
  to build in the suite, the test is structural: a small payload's size field is raised,
  and the reader must refuse it *only* for the bytes it lacks. That is the check a real
  payload of that size passes, so it shows the size check let the claim through, and
  the reader checking bytes before allocating is what makes it cheap.
- **A cost that is charged is held to the cost that is measured.** Every read charges
  what it is about to allocate against a budget, and those charges are only a defence
  if they are upper bounds. `TestReadBudget` finds each structure's charge by bisection
  on the budget -- the smallest it loads under -- and holds the read's allocation, from
  `GC.GetAllocatedBytesForCurrentThread`, to that plus a stated slack, on a sample large
  enough that anything missing could not hide in the slack. Two things learned getting
  there: the counter is exact for what a read allocates, but a collection landing
  mid-read can add a retired allocation context's worth, so the measurement is the least
  of five; and the first undercharge found was in the accounting itself, a `params`
  array allocated by every call that summed the charges.
- **An exemption names the test that checks its reason.** The same sweep exempts the
  structures whose readers cap nothing their constructors can reach, and each exemption
  points at a test asserting why -- the cap is unreachable, or the reader caps nothing.
  An exemption can assert that a known gap is still open while another branch closes
  it. `QuotientFilter` was exempt that way, its test building a filter its reader
  refused; when the constructor fix was merged in, that test failed on the merge, and
  the structure moved into the declared limits. A number written into FORMAT.md is held
  the same way, by a test comparing its table of limits with the code in both
  directions.
- **A table of what each structure holds is recomputed, not copied.** The README's table
  and the notes in CAPACITY.md are held to the library by `TestCapacityDocumentation`.
  Every cell is worked out again from the sizing code and the limit symbols, and from what
  a read is charged as the internal `ReadBudget` reports it for a small structure,
  carried to the size asked about by a line that has to pass through every read it was
  fitted to -- a structure at its limit is gigabytes, and no test builds one. A cell that
  cannot be reached that way is marked and held to an estimate, with the reason it is
  exempt checked in turn. Each sentence of the prose that states a figure is a claim with
  a check, and a figure of four digits in neither the table nor a claim fails. Whatever
  the table says about a defect that is open is a check on the defect: it fails when the
  defect is fixed, which is when the number should change. A bucket array truncated by
  32-bit arithmetic, a table of 2^32 slots that was one, and a width held in a signed
  integer were three such, and the fix that closed them failed those checks on the merge
  until the table, the notes and the checks said what the library now does.

## Deliberately not done

- **Property-based-testing libraries** (FsCheck, CsCheck): the deterministic keyed
  streams above already give reproducibility, and the identity/model tests fail with
  named specifics. Shrinking is the only thing a library would add; not worth the
  dependency.
- **Concurrency stress tests**: the structures are documented not-thread-safe, and
  the supported concurrent pattern (per-thread structures merged on read) is exact
  by the merge identities — no memory-model testing required.
- **Chasing a mutation-score number**: message-string and equivalent-mutant survivors
  are fine. A suite that pins error-message wording is worse than one that doesn't.

## Verifying

```
dotnet test                              # full suite
dotnet test -c Release                   # what CI runs, -warnaserror
dotnet run -c Release --project Benchmarks -- study <name> <args>   # accuracy studies
```

**A clean build is the only build that verifies.** Analyzers do not run on a project
MSBuild considers up to date, so a `-warnaserror` build after a small edit can report
zero errors on code that does not compile from scratch. A CA2014 — a `stackalloc`
inside a loop, growing the stack with the number of entries a payload claimed to hold
— survived several such checks in a row, each reporting success, and appeared the
moment the outputs were removed. Before believing a green build:

```
dotnet clean -c Release && dotnet build -c Release -warnaserror; echo "exit: $?"
```

**And read the exit code, not the summary.** `grep` against the "0 Error(s)" line is
how that same failure was missed a second time: the pattern matches whether the number
is 0 or 1, and the eye supplies the rest. The exit code cannot be misread.

CI builds clean on three platforms and would catch both, but finding it there costs a
push and several minutes; finding it locally costs one command.

Accuracy studies (estimator comparisons, bias-table derivations) live in
`Benchmarks/` and are dispatched by name so their results are reproducible; see
`HyperLogLogBiasStudy.cs` for the shape.
