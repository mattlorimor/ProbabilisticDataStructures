# Security

## Reporting a vulnerability

Report vulnerabilities privately through GitHub's security advisories:
[Report a vulnerability](https://github.com/mattlorimor/ProbabilisticDataStructures/security/advisories/new).
Please do not open a public issue for anything you believe is exploitable.

Include what you would want to receive: the affected structure and version, a
minimal reproduction, and what an attacker gains. You will get an acknowledgement
within a few days and an honest assessment of severity and timeline once the
report is understood. This library has a single maintainer; "within a few days"
is a commitment, "same day" would be a guess.

## Supported versions

| Version | Supported |
| ------- | --------- |
| Latest release (6.x) | Yes |
| Earlier majors | No — fixes land in the current major only |

## Scope

Two properties of this library are design decisions, not vulnerabilities:

- **Hashing is not cryptographic.** The structures use non-cryptographic hash
  functions for speed. An adversary who controls the input stream can craft
  collisions and degrade a filter's accuracy. If untrusted parties feed your
  filters, that is a threat model the README's guidance on hash configuration
  exists for — reports about the existence of collisions will be closed as
  by-design.
- **Probabilistic answers are probabilistic.** False positives at the documented
  rate are the contract, not a defect.

Bugs that make a structure violate its *documented* guarantees — reading past a
buffer on a hostile payload, a persistence reader accepting corrupt data, an
error bound that does not hold — are exactly what this process is for.
