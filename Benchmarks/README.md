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

Apple Silicon Mac, .NET 10, `--job short`. "Before" is the original
`HashAlgorithm.ComputeHash` path; "after" is hashing into a stack buffer via
`TryComputeHash`. Absolute times are machine-specific; the **allocation** figures are
not, and are the interesting part.

### Hash kernel

| Method | DataSize | Mean before | Mean after | Allocated before | Allocated after |
| --- | --- | --- | --- | --- | --- |
| `HashKernel` | 8 | 282.0 ns | 254.0 ns | 80 B | **0 B** |
| `HashKernel` | 64 | 383.6 ns | 348.7 ns | 80 B | **0 B** |
| `HashKernel` | 1024 | 1,483.4 ns | 1,451.1 ns | 80 B | **0 B** |

### Membership tests

| Method | Mean before | Mean after | Allocated before | Allocated after |
| --- | --- | --- | --- | --- |
| `Bloom_Hit` | 277.3 ns | 249.7 ns | 80 B | **0 B** |
| `Counting_Hit` | 283.0 ns | 250.5 ns | 80 B | **0 B** |
| `Partitioned_Hit` | 280.7 ns | 251.0 ns | 80 B | **0 B** |
| `Scalable_Hit` | 285.0 ns | 255.6 ns | 80 B | **0 B** |
| `Stable_Hit` | 279.1 ns | 248.1 ns | 80 B | **0 B** |
| `Deletable_Hit` | 283.6 ns | 252.3 ns | 80 B | **0 B** |
| `Cuckoo_Hit` | 871.2 ns | 755.1 ns | 320 B | **32 B** |

## What the numbers show

**Allocation on the hot path is gone.** The original 80 B per operation was constant
regardless of input size, because it came from `HashAlgorithm.ComputeHash` returning a
freshly allocated digest on every call rather than from the data being hashed. Hashing
into a stack buffer removes it entirely, and Gen0 collections with it.

**Time improved by roughly 10% at small inputs and less at large ones**, which is what
should be expected: removing an allocation matters most when the allocation is a
meaningful share of the work. At 1024-byte inputs the MD5 computation dominates and the
gain shrinks to about 2%. The timing gain is modest relative to the ~5% noise floor
documented below; it is believable mainly because it appears consistently across every
benchmark. The allocation result is the unambiguous one.

**Cuckoo still allocates, but far less.** Its digests now go into stack buffers and the
LINQ `Take(...).ToArray()` is a direct copy, taking it from 320 B to 32 B. What remains
is the fingerprint array itself, which cannot go away while it is stored in a bucket.
It continues to compute three hashes per operation; reducing that would change the
index values it derives, so it is not a performance question alone.

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
