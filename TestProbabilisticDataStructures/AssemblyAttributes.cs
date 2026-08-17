using Microsoft.VisualStudio.TestTools.UnitTesting;

// Every test constructs its own filter instance, and the only shared state is a set of
// static byte arrays used as test input that are never mutated. Methods are therefore
// safe to run in parallel.
//
// Safe for `dotnet test`, that is. Stryker's per-test coverage collector cannot
// tolerate in-assembly parallelism (TESTING.md, "Mutation testing"): for the fast
// Stryker configuration, set Workers = 1 here for the run and revert after.
[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]
