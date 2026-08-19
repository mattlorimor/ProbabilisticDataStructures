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

## Mutation testing

Manual, targeted mutation is part of every test commit (step 3 of the loop). Whole-file
sweeps use Stryker.NET. The config carries the settings; the scope belongs on the
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

**Survivors are leads, not verdicts — and so are Timeouts.** Hand-apply each and run
the suite before believing either. The residue that survives adjudication here is
real and accepted: exception-message strings (tests assert exception types,
deliberately not wording — the one asserted fragment, "at most 8 bits", is the
exception that proves it), internal guards reachable by no public path (closed with
direct internal tests in `TestBuckets.cs`), and equivalent mutants (`>`→`>=` where
both arms write the same value, `>>`→`>>>` on an unsigned operand). Document these
rather than contorting a test to kill them.

## Persistence

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
