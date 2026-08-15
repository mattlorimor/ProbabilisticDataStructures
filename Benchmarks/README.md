# Benchmarks

[BenchmarkDotNet](https://benchmarkdotnet.org/) suites for this library, used to
measure optimization work rather than argue about it.

## Running

Benchmarks require a Release build; BenchmarkDotNet refuses to run otherwise.

```
# everything (slow)
dotnet run -c Release --project Benchmarks -- --filter '*'

# one suite
dotnet run -c Release --project Benchmarks -- --filter '*HashKernel*'

# faster, less precise — fine for spotting large regressions
dotnet run -c Release --project Benchmarks -- --filter '*' --job short
```

Results are written to `BenchmarkDotNet.Artifacts/` (git-ignored).

## Suites

| Suite | What it covers |
| --- | --- |
| `HashKernelBenchmarks` | `Utils.HashKernel` / `HashKernel128` in isolation, across input sizes |
| `FilterTestBenchmarks` | `Test` across every filter type — read-only, so state does not drift |
| `FilterAddBenchmarks` | `Add` / `TestAndAdd` throughput, batched with `OperationsPerInvoke` |

`Add` mutates the filter, so those benchmarks rebuild it in `[IterationSetup]` and
insert a fixed batch per invocation. `OperationsPerInvoke` reports per-item cost and
amortizes the setup, keeping it out of the measurement.

## Results

Apple Silicon Mac, .NET 10, `--job short`. Absolute times are machine-specific.

### Membership tests

"MD5" is the previous default hash with allocation already removed; "XxHash3" is the
current default.

| Method | MD5 | XxHash3 | Speedup |
| --- | --- | --- | --- |
| `Bloom_Hit` | 249.7 ns | **10.46 ns** | 24x |
| `Counting_Hit` | 250.5 ns | **10.23 ns** | 24x |
| `Partitioned_Hit` | 251.0 ns | **10.56 ns** | 24x |
| `Scalable_Hit` | 255.6 ns | **10.56 ns** | 24x |
| `Deletable_Hit` | 252.3 ns | **10.12 ns** | 25x |
| `Stable_Hit` | 248.1 ns | **5.45 ns** | 46x |
| `Cuckoo_Hit` | 755.1 ns | **11.24 ns** | **67x** |

Nothing allocates except the Cuckoo filter's 32-byte fingerprint, which is stored in a
bucket and cannot be avoided.

## What the numbers show

**The hash was the entire cost of an operation.** Removing the per-call allocation
earlier bought about 10%; replacing MD5 with a non-cryptographic hash bought 24x. MD5 is
built to resist collision attacks, which a filter does not need and pays for on every
probe.

**Cuckoo gains the most** because it hashes three times per operation where the others
hash once, so it was paying the MD5 cost three times over.

**Bloom_Miss is now faster than Bloom_Hit** (3.39 ns against 10.46 ns). A miss
short-circuits at the first unset bit, and with hashing no longer dominating, that
difference finally shows up in the measurement.

## Why these are not run in CI

Benchmarks are for when you are optimizing and can control the machine. They do not
gate pull requests, for two measured reasons.

**The timings are too noisy to gate on.** In the baseline above — a quiet local machine,
not a shared runner — `HashKernel` at 1024 bytes measured 1,483.4 ns ± 135.01, or 9%.
`Bloom_Hit`, `Stable_Hit`, `Partitioned_Hit`, `Counting_Hit` and `Deletable_Hit` land
within 2% of each other while almost certainly doing identical work; that spread is the
noise floor. A threshold loose enough to avoid false failures on a shared CI runner
would sail past a real 10% regression, and a tighter one would fire constantly until
somebody switched it off.

**They are slow.** The two `--job short` runs above took 40s and 62s for 14 benchmarks.
Default precision is roughly an order of magnitude longer, which is 15–30 minutes added
to every pull request.

What *is* worth gating on is allocation, because it is a property of the code rather
than the machine and reproduces exactly. That lives in
`TestProbabilisticDataStructures/TestAllocations.cs` as ordinary unit tests using
`GC.GetAllocatedBytesForCurrentThread`, which run in about 35 ms as part of the normal
suite. Update the bounds there when the hash path changes.

## Accuracy studies

Not everything measured here is a timing. An accuracy study answers "which of these is
closer to the truth" rather than "how fast", so it does not run under BenchmarkDotNet
and is dispatched separately.

```
dotnet run -c Release --project Benchmarks -- study hll-bias 14
```

### `hll-bias` — HyperLogLog++'s tables against the estimator we ship

`HyperLogLogPlus` uses Ertl's estimator rather than HyperLogLog++'s tables of measured
bias. This is what that decision rests on.

The study derives the tables the way the paper did — measuring the raw estimator's bias
over many streams at known cardinalities — rather than embedding the published ones. The
published tables are empirical data, and data reproduced incorrectly would be invisible:
the estimator would simply be quietly worse in the band the tables exist to fix.
Deriving them makes the comparison checkable, and against this library's own hash
besides.

The tables are trained on one set of streams and scored on another, given a threshold
chosen to minimise their own error, and both estimators are measured over the same bare
register array so that what is compared is the estimator alone.

Mean absolute error across nineteen cardinalities from 0.125m to 20m, sixty held-out
streams each:

| precision | Ertl | tables |
| --- | --- | --- |
| 10 | 2.127% | 2.158% |
| 12 | 1.095% | **1.087%** |
| 14 | 0.563% | 0.569% |
| 16 | 0.262% | 0.269% |

A tie. The gaps are one to three percent of each other, inside the noise of sixty
streams, the sign changes with the precision, and the worst point of each agrees to
three digits at every precision.

So accuracy does not choose between them and everything else does. Ertl is forty lines
and no data, works at any precision without being trained for it, and has no threshold
to place. The tables are six thousand measured numbers plus a threshold per precision,
all of which have to be right for the estimator to be better in the band they exist for,
and none of which announce themselves when wrong.

One thing the study taught along the way: the threshold matters more than the tables. An
earlier version picked it by first crossover, which landed it where the two estimators
happen to cross and left the choice unstable exactly there — 3.04% error at that
cardinality against Ertl's 0.48%. Choosing it to minimise error over the training range,
using per-stream errors rather than the error of the means, removed that entirely. The
tables were never the problem; the switch between them and linear counting was.
