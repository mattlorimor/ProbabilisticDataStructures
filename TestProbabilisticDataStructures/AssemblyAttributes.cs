using Microsoft.VisualStudio.TestTools.UnitTesting;

// Every test constructs its own filter instance, and the only shared state is a set of
// static byte arrays used as test input that are never mutated. Methods are therefore
// safe to run in parallel.
[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]
