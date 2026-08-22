# ProbabilisticDataStructures

Probabilistic data structures for C#. Managed IL throughout — no `unsafe`, no pointers,
no hardware intrinsics — targeting `net10.0` with one dependency, `System.IO.Hashing`.

## Read these before writing anything

- **[TESTING.md](TESTING.md) governs test work.** Read it before adding or changing a
  test rather than after. It is the longest document here because testing is the hard
  part of this repository, and most of it was learned by getting it wrong first.
- **[CONTRIBUTING.md](CONTRIBUTING.md)** has the build and verify commands and the short
  form of the same rules.
- **[FORMAT.md](FORMAT.md)** specifies the persistence byte layout. It is held to the
  code by `TestFormatDocumentation`, so it fails loudly rather than drifting quietly.

## Verifying a change

CONTRIBUTING.md has the commands. Two traps worth naming here, because both have
produced a confidently reported result that was measured against the wrong binary:

- **Read the build's exit code, unpiped.** Analyzers do not run on a project MSBuild
  thinks is up to date, so an incremental `-warnaserror` build can pass on code that
  will not compile from scratch.
- **`dotnet test --no-build` silently reuses the previous binary.** If the build failed,
  the test result you are reading belongs to the last thing that compiled. Confirm the
  build exited zero *before* reading any test output.

## Mutation

Use `scripts/mutate.sh` rather than editing by hand. It restores the tree, applies one
edit, builds, runs the tests and prints a verdict, and it refuses to start on a dirty
tree — because the restore is `git checkout --`, which discards every uncommitted change
in the target directory and not only the deliberate break. **Commit before mutating.**

A test is not finished until it has been watched failing for the reason it names.

## Rosters

A sweep named "every X" must derive its roster from something a new case cannot omit
itself from — the `StructureId` enum, or reflection over the public surface. A
hand-written list means "every X someone remembered", and nothing fails when the next
structure is added. Exemptions are written claims with a stated reason, and the reason is
verified: a stale exemption fails as loudly as a gap. See TESTING.md.

For the same reason, do not put a count in a comment or a doc. It is a roster maintained
by hand, and one here was wrong for several releases before anyone read it closely.

## Workflow

Branch, open a PR, wait for CI, and **wait to be asked before merging**. Never push,
merge, or tag unprompted. Scan the branch diff for secrets before pushing.

CI runs the suite on five runners and all five gate merges: x64 and arm64 on both Linux
and Windows, plus arm64 macOS. The arm64 labels pin an image version because there is no
`-latest` form of them, so they need bumping by hand when an image is retired.

## Layout

```
ProbabilisticDataStructures/      the library
TestProbabilisticDataStructures/  the suite; fixtures/ holds stored payloads
Benchmarks/                       BenchmarkDotNet
scripts/mutate.sh                 step 3 of the TESTING.md loop
```

Tests see `internal` members: `Defaults.cs` declares `InternalsVisibleTo`.

## Measuring on macOS

BSD `grep` does not handle `\b` reliably, and substring matches corrupt naive coverage
scans — `BloomFilter` matches inside `BloomFilter64`. Use a real parser for anything you
intend to report a number from.
