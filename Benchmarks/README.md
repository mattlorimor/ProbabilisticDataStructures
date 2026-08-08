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

## Baseline

Captured before any optimization work, on an Apple Silicon Mac running .NET 10 with
`--job short`. Absolute times are machine-specific; the **allocation** figures are not,
and are the interesting part.

### Hash kernel

| Method | DataSize | Mean | Allocated |
| --- | --- | --- | --- |
| `HashKernel` | 8 | 282.0 ns | **80 B** |
| `HashKernel` | 64 | 383.6 ns | **80 B** |
| `HashKernel` | 1024 | 1,483.4 ns | **80 B** |

### Membership tests

| Method | Mean | Ratio | Allocated |
| --- | --- | --- | --- |
| `Bloom_Hit` | 277.3 ns | 1.00 | 80 B |
| `Counting_Hit` | 283.0 ns | 1.02 | 80 B |
| `Partitioned_Hit` | 280.7 ns | 1.01 | 80 B |
| `Scalable_Hit` | 285.0 ns | 1.03 | 80 B |
| `Stable_Hit` | 279.1 ns | 1.01 | 80 B |
| `Deletable_Hit` | 283.6 ns | 1.02 | 80 B |
| `Cuckoo_Hit` | 871.2 ns | **3.14** | **320 B** |

## What the baseline shows

**Allocation is constant at 80 B per operation regardless of input size**, because it
comes from `HashAlgorithm.ComputeHash` allocating a fresh digest array on every call,
not from the data being hashed.

**Every filter except Cuckoo allocates exactly the same 80 B as the raw hash kernel.**
The filter logic itself allocates nothing — the entire per-operation allocation is
hashing. That makes the hash kernel the single point worth optimizing, and it bounds
the win: no filter can allocate less than the kernel does.

**Cuckoo is a 4x outlier** because `GetComponents` computes the hash three times — once
directly, then again inside each of two `ComputeHashSum32` calls — plus a LINQ
`Take(...).ToArray()` for the fingerprint. Four allocations, three MD5 computations.
It is a separate problem from the shared kernel and deserves its own fix.

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
