# Contributing

If you think a change would be useful, make a PR. The only thing I would ask you to
raise first is an oh-man-this-is-changing-everything sort of change, where it is worth
agreeing on the shape before either of us spends a weekend on it.

Everything below is about how this repository is tested, which is unusual enough to be
worth two minutes of your time before you write the tests rather than after.

## Testing is the hard part here

[`TESTING.md`](TESTING.md) is the real document. The short version:

- **Watch every test fail before you watch it pass.** A test that has never been seen
  to fail proves nothing. Twice in this repository a test that *named* a constant
  turned out to be unable to detect that constant changing.
- **Break the implementation on purpose** to prove the new test catches it, then put it
  back, and record what you broke in the commit message. `scripts/mutate.sh` does the
  mechanics; see TESTING.md. Commit *before* you start breaking things — the obvious way
  to undo a deliberate break also discards everything else uncommitted, which is why the
  script refuses to run on a dirty tree rather than trusting anyone to remember.
- **Say what survived.** If you tried a mutation and the suite did not catch it, that
  belongs in the commit message too, along with why you decided it was acceptable. A
  documented survivor is worth more than a tidy story.
- **Measure before you assert.** For anything with an error rate or a bound, probe what
  the code actually does, then pin it with slack you can justify — and pair the bound
  with a check that it is not passing vacuously.

This sounds like a lot. In practice it is the difference between a suite that catches
defects and one that only records that somebody once ran the code, and most of it was
learned here by getting it wrong first.

## Stored data must keep loading

[`FORMAT.md`](FORMAT.md) specifies the byte layout. A payload written by any version is
readable by every later version, or is refused with an explanation — never guessed at.
If you change what a structure stores, bump its format version, keep the old reader
working, and add a fixture pinning the new bytes. The suite reads fixtures checked in at
the version that introduced each format, so a change that would strand somebody's stored
data fails in CI rather than in their storage.

## Practical bits

```
dotnet clean -c Release && dotnet build -c Release -warnaserror; echo "exit: $?"
dotnet test -c Release
```

Read that exit code rather than the build summary. Analyzers do not run on a project
MSBuild thinks is up to date, so an incremental `-warnaserror` build can report success
on code that will not compile from scratch — which is exactly how a real defect got
through several green checks in a row.

New structures follow the pattern of the existing ones: a class with the same shape of
API, span overloads beside the array ones, persistence with a golden fixture, an entry
in the README's decision table, and a `CHANGELOG.md` line.

## Asking is fine

If any of this is unclear, or does not fit what you are trying to do, open the PR and
say so. None of it is meant to keep people out.
