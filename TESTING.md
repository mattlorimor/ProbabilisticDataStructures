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

Whole-file sweeps use Stryker.NET. The config carries the settings; the scope belongs on the
command line, per run:

```
dotnet tool restore
cd TestProbabilisticDataStructures
dotnet stryker --mutate "**/QuotientFilter.cs" --mutate "**/Buckets.cs"
```

Scope every run. Stryker is not part of any build: a single-file sweep takes twenty
minutes to an hour and a half depending on configuration (below), the full library
would generate five thousand mutants, and the output needs human adjudication anyway.
It is a periodic audit for files whose tests have not been through the loop above,
not a gate.

**Stryker and this suite's parallelism do not mix unmanaged.** The test assembly
declares `[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]`, and Stryker
assumes it controls all parallelism — it serializes VsTest at the *container* level,
which in-assembly workers ignore. That breaks both of its modes, silently and in
opposite directions. Four runs on one frozen tree (Buckets.cs scope, 531 tests),
whose true score with every verdict hand-adjudicated is 74.80%:

| coverage-analysis | MSTest workers × Stryker concurrency | reported |
|---|---|---|
| perTest | 10 × 5 | 66.14% — 16 false survivors; hot-path mutants misclassified `static` |
| off     | 10 × 5 | 100.00% — 41 wall-clock timeouts counted as kills, 32 on undetectable mutants |
| perTest | 1 × 5  | 78.74% — zero verdict errors |
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
`|=` was meant), so the countermeasure was dead code. Fixed in PR #3760 (merged
2026-08-14, unreleased as of 4.16.0). The emission is unconditional per framework,
so the fix serializes every session — both failure modes above die with it. **When
the next Stryker release ships: bump `dotnet-tools.json`, drop the `concurrency`
pin from `stryker-config.json`, let `coverage-analysis` return to its default, and
delete the Workers guidance below.** Until then, both workarounds stand.

Two sound configurations:

- **Default, enforced by `stryker-config.json`: `coverage-analysis: off` with
  `concurrency: 1`.** What `dotnet stryker` does in this repo with no extra steps.
  Exact and slow — the Buckets.cs sweep took 1h40, because every surviving mutant
  runs the full suite serially.
- **Fast path for larger sweeps: serialize the workers instead.** Set `Workers = 1`
  on the `Parallelize` attribute in `AssemblyAttributes.cs` for the duration of the
  run and revert after; then per-test coverage is sound and the same sweep took
  19:40. The edit cannot live in the Stryker config — MSTest reads parallelism from
  the assembly, not from anything Stryker controls.

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
real and accepted: exception-message strings (tests assert exception types,
deliberately not wording — the one asserted fragment, "at most 8 bits", is the
exception that proves it), internal guards reachable by no public path (closed with
direct internal tests in `TestBuckets.cs`), and equivalent mutants (`>`→`>=` where
both arms write the same value, `>>`→`>>>` on an unsigned operand). Document these
rather than contorting a test to kill them.

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
- Corruption is tested exhaustively at distance 1 (every single-bit flip must be
  rejected) plus structured hostile payloads for what bit flips can't reach. This is
  stronger than random fuzzing for the same class and fully reproducible.
- Round-trip tests must cross the structure's interesting thresholds. The theta
  sketch's trim-boundary bug survived because every round-trip test used sub-trim
  streams; the first sketch to see real volume was the first to fail.

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
