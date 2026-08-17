---
name: Bug report
about: Something behaves differently from what is documented
---

**What happened, and what did you expect instead?**

**Why do you believe this is a defect rather than the documented error rate?**
<!-- Worth a moment's thought, because it is the question that decides whether this is
     a bug at all. These structures are allowed to be wrong: a Bloom filter says yes to
     things it never saw, a sketch's counts are estimates, an UltraLogLog's answer moves
     by a percent or so either way. What is *not* allowed is being wrong in a direction
     or at a rate the documentation does not claim -- a false negative from a filter, an
     estimate outside its stated bound, an answer that changes across a save and reload.
     If you are not sure which side of that line you are on, say so and open it anyway;
     working that out is the useful part. -->

**Minimal reproduction**
<!-- Code we can run. If the structure takes a seed, please pass one -- without it the
     behaviour is not reproducible and we will be chasing a different sequence than you
     were. -->

```csharp
```

**Versions**
- `MattLorimor.ProbabilisticDataStructures`:
- Target framework (e.g. net10.0):
- OS:

**Which structure?**

**Anything else**
<!-- The scale it appears at, whether it survives a round trip through WriteTo/ReadFrom,
     whether it happens with a different seed. Any of those narrow it a lot. -->
