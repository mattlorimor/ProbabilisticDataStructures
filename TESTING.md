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

## Mutation testing

Manual, targeted mutation is part of every test commit (step 3 of the loop). Whole-file
sweeps use Stryker.NET. The config carries the settings; the scope belongs on the
command line, per run:

```
dotnet tool restore
cd TestProbabilisticDataStructures
dotnet stryker --mutate "**/QuotientFilter.cs" --mutate "**/Buckets.cs"
```

Scope every run. Stryker is not part of any build: with coverage analysis off -- see
below for why it must be -- a four-file sweep takes about seventy minutes, the full
library would generate five thousand mutants, and the output needs human adjudication
anyway. It is a periodic audit for files whose tests have not been through the loop
above, not a gate.

Two hard-won caveats:

- **`coverage-analysis` must stay `off`.** With it on, Stryker's per-test coverage
  attribution misfires on this MSTest/.NET setup and reports mutants as "survived"
  that the suite demonstrably kills — a hand-verified survivor list showed mutants
  failing 8 pre-existing tests while marked green. Off is slower (every mutant runs
  tests until first failure) and truthful.
- **Survivors are leads, not verdicts.** Hand-apply each interesting survivor and run
  the suite before believing it. Expected, acceptable survivors: mutations to
  exception-message strings (tests assert exception types, deliberately not wording)
  and equivalent mutants (document the equivalence argument in the commit rather
  than contorting a test to kill them).

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

## Running

```
dotnet test                              # full suite
dotnet test -c Release                   # what CI runs, -warnaserror
dotnet run -c Release --project Benchmarks -- study <name> <args>   # accuracy studies
```

Accuracy studies (estimator comparisons, bias-table derivations) live in
`Benchmarks/` and are dispatched by name so their results are reproducible; see
`HyperLogLogBiasStudy.cs` for the shape.
