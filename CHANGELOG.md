# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [3.1.0] - Unreleased

### Fixed

- **Filter constructors now validate their arguments and throw
  `ArgumentOutOfRangeException`** instead of failing later, or not at all.

  Three problems, all of which reported something true about the internals and
  nothing about the mistake:

  - A false positive rate of 0, 1, a negative, a value above 1, or `NaN` surfaced as
    an `OverflowException` from a numeric conversion inside the sizing math. A rate of
    exactly 1 was worse: it constructed successfully and produced a filter with zero
    bits and zero hash functions, which silently reported every element as present.

  - Sizing a filter for zero items constructed successfully and then threw
    `DivideByZeroException` on first use, far from the cause. This affected
    `BloomFilter`, `BloomFilter64`, `CountingBloomFilter`, `PartitionedBloomFilter`
    and `CuckooBloomFilter`.

  - `DeletableBloomFilter` splits its bits between a data region and a collision
    region. Passing a collision count at or above the filter's size underflowed the
    `uint` subtraction `m - r`, so `new DeletableBloomFilter(0, 10, 0.01)` silently
    allocated roughly **512 MB** and reported a capacity above four billion.

  Valid arguments are unaffected. `HyperLogLog` and `TopK` already validated theirs;
  this brings the rest of the library in line.

- **`HyperLogLog` threw `IndexOutOfRangeException` at 2^29 registers.** `b` splits the
  hash into a register index and the bits `rho` scans, so it has to be exactly
  `log2(m)`. Deriving it as `Ceiling(Log(m, 2))` is not exact: at `m = 2^29` the
  floating-point logarithm lands just above 29 and the ceiling returns 30, leaving the
  register index able to exceed the array. It is now derived with `BitOperations.Log2`.
  Separately, a register count of zero passed the power-of-two check, because `0 - 1`
  underflows to all ones; it is now rejected. The estimator itself is unchanged.

- **`DeletableBloomFilter` threw `IndexOutOfRangeException` for any collision region
  count that is a multiple of eight**, including `8`, `16`, `32` and `64`. The region
  size was rounded down, so the trailing bits of the data region mapped to region index
  `r` -- one past the last collision bucket. Whether that was fatal depended on
  something unrelated: the collision bitmap allocates whole bytes, and for most values
  of `r` the stray index landed in the leftover padding bits and went unnoticed, but a
  multiple of eight leaves no padding and the write ran off the array. Passing an `r`
  larger than the `m - r` data bits rounded the region size down to zero and threw
  `DivideByZeroException` on the first `Add`. The region size is now rounded up, which
  keeps every index inside the array and non-zero. Stored filter contents are
  unaffected; only which region a bit is attributed to changes.

## [3.0.1] - 2026-08-13

### Fixed

- **Passing `null` as data now throws `ArgumentNullException` again.** In 3.0.0 it
  silently succeeded and was indistinguishable from an empty array, because a null
  array converts to an empty span and hashes to the same value.

  This was a regression. Version 2.x threw, because hashing went through
  `HashAlgorithm.ComputeHash`, which rejects null. Moving to span-based hashing
  removed the check without anyone noticing, and nothing in the test suite passed
  null to anything.

  It matters more than a missing argument check usually would: without it, a caller's
  null bug does not surface at the call site. It quietly inserts a phantom element
  that collides with empty input and produces a wrong answer later, elsewhere.

  Empty input remains valid and distinct from null.

### Changed

- **`Buckets` and `Buckets64` now reject a bucket size wider than 8 bits** with
  `ArgumentOutOfRangeException`, rather than accepting it and capping the value at 255.

  A bucket's maximum is stored in a byte, so a wider bucket allocated the extra space
  without being able to hold a larger value -- a 16-bit bucket cost twice the memory
  for no additional range. The bit packing itself handles wider buckets correctly; only
  the maximum could not.

  This resolves a long-standing `TODO` questioning whether the cap was intended. It is:
  upstream Go BoomFilters stores its maximum in a `uint8` too, where the same
  expression wraps to 255 rather than clamping. Both implementations have always
  capped at 255, so this rejects a request neither could honor.

  These are internal types. Reaching the limit requires passing a bucket size above 8
  to `CountingBloomFilter` or `StableBloomFilter`, which would previously have wasted
  memory silently.

## [3.0.0] - 2026-08-13

### Changed

- **The default hash is now XxHash3 rather than MD5, and `SetHash` takes a
  `Func<ReadOnlySpan<byte>, ulong>` instead of a `HashAlgorithm`.**

  Membership tests are roughly **24x faster**, and the Cuckoo filter **67x** because it
  hashes three times per operation. `Bloom_Hit` goes from 249.7 ns to 10.46 ns. Hashing
  was the entire cost of a filter operation; MD5 is built to resist collision attacks,
  which a filter does not need and paid for on every probe.

  This changes where every element lands, so filters persisted by earlier versions
  cannot be read. It also changes the `SetHash` signature on every filter: supply a
  function returning 64 bits rather than a `HashAlgorithm`. A `byte[]` converts to
  `ReadOnlySpan<byte>` implicitly, so call sites passing arrays are unaffected.

  XxHash3 is not resistant to chosen-input attacks: an adversary controlling inserted
  data can provoke collisions and inflate the observed false-positive rate. MD5 was no
  better in this respect, only slower. Callers needing that property should supply a
  keyed hash such as SipHash through `SetHash`.

### Fixed

- **The Cuckoo filter could report false negatives.** `GetComponents` derived the second
  candidate bucket as `hash(fingerprint)` rather than `i1 XOR hash(fingerprint)`.

  The two candidate buckets in a cuckoo filter are a pair: either index must recover the
  other by XOR with the fingerprint hash. The relocation loop already relied on that,
  computing an element's alternate bucket as `i ^ ComputeHashSum32(f)`, but the pairing
  was never established, so an element moved during relocation could land in a bucket
  that `Test` does not examine. Filling a filter to 5,000 insertions reproduced eight
  elements that were reported as added and then could not be found. A cuckoo filter is
  supposed to avoid false negatives entirely.

- **The Cuckoo filter never used its second bucket for insertion.** `Insert` computed the
  second bucket's free slot into a variable named `ids`, then tested `idx` — the result
  from the *first* bucket, which is always `-1` at that point. The condition could never
  be true, so the write was unreachable and every element that did not fit in its first
  bucket went straight to relocation. Measured impact on capacity is small; the cost is
  wasted relocation work rather than lost slots. No compiler warning fires because `ids`
  is assigned from a method call.

### Changed

- The test word list moved from a 5.75 MB C# array to an embedded `words.txt`. The list
  is unchanged, verified by checksum over all 235,886 entries. This affects the test
  project only and is not part of the package.

- **Cuckoo filter element placement has changed**, which is why this is a major release.
  Both fixes alter which buckets an element occupies. Filters persisted by 2.x cannot be
  read correctly by 3.0.0, and a 3.0.0 filter will not agree with a 2.x one. Nothing else
  in the library is affected; the Bloom-family hash kernel is untouched.

### Added

- Nullable reference type annotations on the library. These ship in the package, so
  consumers get null analysis against this API. Notably, `CuckooBloomFilter`'s bucket
  entries are now typed as nullable, which is what they always were, and
  `Element.Data` defaults to an empty array so reading `TopK.Elements()` needs no
  null check.

- Thread-safety documentation on `IFilter` and in the README. Nothing in this library
  is synchronized, matching the Go original. The non-obvious part is that `Test` is not
  safe to call concurrently with *itself*, because filters reuse a single
  `HashAlgorithm` instance and that type is not thread-safe, so a reader-writer lock is
  not sufficient.

- A regression test that fills a Cuckoo filter well past the point where relocation
  begins and asserts that every element it accepted can still be found. The existing
  Cuckoo tests all used filters small enough that relocation never happened, which is
  why both defects survived.

## [2.0.1] - 2026-08-08

### Added

- A package icon. NuGet renders an embedded icon on the package listing and in Visual
  Studio's package manager; without one the package showed a grey placeholder. The
  design is a bit array with some bits set, which is what a Bloom filter is.

  Shipped as a patch release because 2.0.0 is already published and an icon only
  appears on versions packed after it was added — it is not applied retroactively.

## [2.0.0] - 2026-08-08

This release modernizes the entire build. The library had not been touched since June
2018 and could no longer be built or tested with current .NET tooling.

### Changed — package identity

- **The package ID is now `MattLorimor.ProbabilisticDataStructures`.**

  Releases from this repository previously had no package of their own. The unprefixed
  `ProbabilisticDataStructures` ID on nuget.org was registered in 2018 by an account
  unaffiliated with this project, which published this project's source; that package is
  not maintained here and will not receive these releases.

  Rather than contest the name, this project ships under an owner-identifying prefix.
  The prefix also makes `MattLorimor.*` eligible for
  [ID prefix reservation](https://learn.microsoft.com/en-us/nuget/nuget-org/id-prefix-reservation),
  which prevents anyone else from publishing under it.

  **Only the package ID changed.** The assembly and namespace remain
  `ProbabilisticDataStructures`, so `using ProbabilisticDataStructures;` and every type
  name are unaffected. Updating means changing the `PackageReference` include, nothing
  more.

### Removed

- **Dropped `net45` and `netstandard2.0`. The library now targets `net10.0` only.**

  This is a hard break, and it is deliberate. If you consume this library from .NET
  Framework, from Unity, from Xamarin/Mono, or from any .NET Core or .NET 5–9
  application — including .NET 8, which remains in support until November 2026 —
  **2.0.0 will not install**. The older, unaffiliated 1.0.1 package targets `net45` and
  `netstandard2.0` and remains on nuget.org, but it is not published by this project and
  nothing here governs it.

  Note that this costs no operating-system coverage. `netstandard2.0` is an API
  specification, not a runtime, and `net10.0` runs on Windows, Linux, and macOS across
  x64 and arm64. What narrows is the range of .NET *versions* that can consume the
  package, not the platforms it runs on.

  The rationale is honest about what it is not: the code in this release would still
  compile against `netstandard2.0` unchanged, so multi-targeting was not blocked by
  anything technical. It was declined because a second target cannot be verified.
  `netstandard2.0` is not executable, so proving that binary works would require an
  additional test target on a runtime that consumes it, and without one the package
  would ship a second assembly that nothing ever runs. One target that is fully
  exercised on three operating systems was judged better than two where only one is
  tested. `net45` is a simpler case: it reached end of support in January 2016.

  If you need continued .NET Framework, Unity, or .NET 8 support, please open an issue.
  Restoring a `netstandard2.0` target is a decision that can be revisited if there is
  real demand; the work is small, and the reason it was skipped is verification cost
  rather than incompatibility.

- Deleted Visual Studio–era build artifacts that no longer serve any purpose:
  `Default.testsettings`, `ProbabilisticDataStructures.vsmdi`, and
  `TestProbabilisticDataStructures/Properties/AssemblyInfo.cs`.

### Changed

- `Defaults.GetDefaultHashAlgorithm()` now calls `MD5.Create()` instead of
  `HashAlgorithm.Create("MD5")`, which is obsolete as of .NET 7 (`SYSLIB0045`) and throws
  under trimming.

  **Hash output is unchanged**, so filters persisted by 1.x remain readable. This is
  verified by the existing hash-kernel tests, which assert hardcoded digest values.

  Those tests pin the byte-extraction arithmetic against Go BoomFilters' convention.
  They do not establish that filters are interchangeable between the two libraries,
  and they never did: Go hashes with FNV-1a where this library uses MD5.

- Migrated the test project from MSTest v1 to MSTest 4. The old test project was a
  legacy-format `.csproj` bound to Visual Studio's test targets, which meant the suite
  could only be run on Windows from inside Visual Studio. **Tests now run cross-platform
  via `dotnet test`.**

- Test methods now execute in parallel, cutting suite runtime from ~13s to ~5s.

- Replaced the hand-maintained `<Compile Include>` list in the test project with SDK
  globbing, so new test files no longer need to be registered by hand.

### Fixed

- The package version is now declared in the repository (`Directory.Build.props`) rather
  than injected by CI from a build counter. Version numbering restarts from a known
  point: the published 1.0.1 package was built on 2018-06-05 from what was tagged
  `v1.0.9`, so the NuGet version line and the Git tag line had not agreed since 2018.

### Added

- Full package metadata: authors, copyright, description, tags, the Apache-2.0 license
  expression, project and repository URLs, and the README as the package readme. The
  unaffiliated 1.0.1 package had shipped with the SDK's placeholder description,
  `"Package Description"`, and no author or license information.

- Generated XML documentation, packed so consumers get IntelliSense against the library
  rather than bare signatures.

- Source Link with symbols published as a `.snupkg`, so consumers can step into this
  code from their own debugger.

- A tag-driven release pipeline that publishes to NuGet via
  [trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing),
  exchanging a short-lived OIDC token for a temporary API key rather than storing a
  long-lived publishing secret. It refuses to publish when the tag disagrees with the
  declared version.

## [1.0.9] - 2018-06-05

- Improved performance by improving the handling of hashes ([#17](https://github.com/mattlorimor/ProbabilisticDataStructures/pull/17)).

## [1.0.8] - 2018-05-13

- Added `BloomFilter64` to support large Bloom filters ([#16](https://github.com/mattlorimor/ProbabilisticDataStructures/pull/16)).

## [1.0.7] - 2018-04-27

- Build for .NET Standard 2.0 in addition to .NET Framework 4.5 ([#14](https://github.com/mattlorimor/ProbabilisticDataStructures/pull/14)).

## [1.0.0] - 2015-12-31

- Initial release: a C# port of [Tyler Treat's](https://github.com/tylertreat)
  [BoomFilters](https://github.com/tylertreat/BoomFilters) Go project.

[3.1.0]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v3.0.1...HEAD
[3.0.1]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v3.0.0...v3.0.1
[3.0.0]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v2.0.1...v3.0.0
[2.0.1]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v2.0.0...v2.0.1
[2.0.0]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v1.0.9...v2.0.0
[1.0.9]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v1.0.8...v1.0.9
[1.0.8]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v1.0.7...v1.0.8
[1.0.7]: https://github.com/mattlorimor/ProbabilisticDataStructures/compare/v1.0.0...v1.0.7
[1.0.0]: https://github.com/mattlorimor/ProbabilisticDataStructures/releases/tag/v1.0.0
