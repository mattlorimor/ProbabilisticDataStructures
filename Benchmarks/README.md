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
