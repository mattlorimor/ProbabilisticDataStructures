### What does this change, and why?

<!-- What the change does, and what it is for. If it fixes something, say what was
     wrong rather than only what is now right -- the next reader is usually trying to
     understand the defect, not the patch. -->

### How do you know it works?

<!-- Not "the tests pass" but what the tests would have caught. If you added a bound,
     an error rate, or an estimate, say what you measured and against what. If you
     broke the implementation on purpose to prove a test bites (see TESTING.md), the
     mutation table belongs in the commit message; a pointer to it here is enough. -->

### Anything you are unsure about?

<!-- Survivors of a mutation you decided were equivalent, a tolerance you picked by
     feel, a case you could not reach. This section being empty on a non-trivial
     change is usually a sign it was not looked for. Honest uncertainty here is worth
     more than a clean checklist. -->

---

- [ ] Tests were watched failing before they were watched passing (`TESTING.md`)
- [ ] `dotnet clean -c Release && dotnet build -c Release -warnaserror; echo "exit: $?"` — read the exit code, not the summary
- [ ] `dotnet test -c Release` passes
- [ ] If the persistence format changed: payloads written by earlier versions still read, and a fixture pins the new one (`FORMAT.md`)
- [ ] If a public API changed: the README says so, and `CHANGELOG.md` has an entry

<!-- New to the repository? CONTRIBUTING.md is a two-minute read and explains why this
     asks for more than most projects do. None of it is meant to be a barrier: open the
     PR, and ask if something here does not fit what you are doing. -->
