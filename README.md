# AppTrace

**A read-only Windows application-footprint inspector.**

> What did this application leave on my PC?
>
> Why is this data still here even if the application is gone?

Windows applications scatter their data across Program Files, ProgramData,
AppData, per-user folders and the registry. Windows itself reports only the
nominal installed size, and uninstallers routinely leave gigabytes of residual
data behind with no easy way to tell whose it is.

AppTrace answers that for **one application at a time**, and — this is the point —
it can always explain *why* it believes a directory belongs to that application.

```text
Discord

  AppData\Roaming
    C:\Users\<user>\AppData\Roaming\discord
      412.6 MB  MEDIUM

  Confirmed / high footprint: 0 B
  Possible additional       : 412.6 MB

  WHY
    For Discord: score 50, MEDIUM
      + [ExactDirectoryNameMatch] Directory name "discord" equals the application name "Discord".
      + [KnownPublisherNamespace] Path is inside the "Discord Inc." namespace of "Discord".
      + [KnownApplicationPath] Location is a per-user application data area (AppData\Roaming).
    Not established: identity evidence without provenance cannot exceed MEDIUM.
```

Note what the explanation shows: two of those three records are `Structure` — they
describe *where the directory is*, not *who owns it* — so they corroborate the
name but cannot make the claim confident. Under Phase 0 the same records summed to
`HIGH`. That difference is the whole point of the v0.1.0 evidence model.

The current emphasis is **attribution and explainability, not cleanup.**

## Status — v0.1.0, experimental

**Experimental / early development.** This is the first public development
baseline. Attribution Engine V2 is **not complete**: the evidence model and
confidence semantics from Task 04 are in, but the filesystem semantic layer,
additional provenance sources and the ownership/relationship model are not.

Expect, in particular:

* **Few confident claims.** `HIGH` now requires that the application is
  independently *anchored* to a path (its own install registration) **and** that
  the path's name agrees. A name match alone is capped at `MEDIUM`. This is
  deliberate; see [docs/ATTRIBUTION_ENGINE_V2.md](docs/ATTRIBUTION_ENGINE_V2.md).
* **Many `UNKNOWN` results.** AppTrace prefers an honest `UNKNOWN` over a
  confidently wrong attribution, and most directories on a real machine genuinely
  cannot be attributed from static evidence.
* **Known false positives remain as candidates.** They are no longer *confident*
  claims, but some are still reported as plausible owners at `MEDIUM` until the
  semantic layer lands. The regression corpus records them.

## Safety — read-only

> **AppTrace v0.1.0 is read-only. It does not modify your system.**

It does **not** delete files, uninstall applications, modify the registry, clean
up leftovers, remove orphans, manage startup items, or "optimize" Windows. There
is no destructive code path in the product at all: the complete set of filesystem
operations is directory enumeration and metadata reading, and every registry key
is opened read-only. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) §8.

## Current capabilities

* Discovery of installed applications from the Windows uninstall registry
  (64-bit, 32-bit and per-user hives) plus MSIX/AppX package registration.
* Normalized application identity: name, publisher, version, install location,
  product code.
* Read-only filesystem scanning of the standard application locations.
* Directory sizes with explicit accounting, so no byte is counted twice.
* Evidence-based attribution: each candidate owner carries the machine-readable
  evidence for and against it.
* Evidence-aware classifications (`CONFIRMED` / `HIGH` / `MEDIUM` / `LOW` /
  `SHARED` / `AMBIGUOUS` / `UNKNOWN`) derived from evidence *kind*, not from a
  single additive score.
* Human-readable text output with a `WHY` block per location, and structured
  JSON diagnostics.
* A deterministic regression corpus plus a hand-labelled real-machine evaluation
  harness, both run by the test suite.

## Requirements

* Windows 10 or later
* [.NET 9 SDK](https://dotnet.microsoft.com/download) to build (or the .NET 9
  runtime to run a published build)

## Build, test, run

```powershell
dotnet build AppTrace.sln -c Release

# 104 tests: unit, regression corpus, and the hand-labelled evaluation harness
dotnet test tests/AppTrace.Core.Tests
```

The test project uses **xunit v3 on Microsoft.Testing.Platform**, so it is also an
ordinary executable. If `dotnet test` cannot start its test host — for example in
a restricted environment where the runner cannot open a named pipe — run it
directly; the result is identical:

```powershell
dotnet run --project tests/AppTrace.Core.Tests
```

Run the CLI:

```powershell
dotnet run --project src/AppTrace.Cli -- scan
```

A published build produces `apptrace.exe`; the examples below use that form.

```powershell
# Start here: one location, with a WHY block for every attributed path
apptrace scan --filter "AppData\Local" -v

# Everything, human-readable
apptrace scan

# Structured diagnostics, to inspect attribution quality by hand
apptrace scan --json > scan.json

# Discovery only / what would be inspected
apptrace apps
apptrace roots
```

Useful options:

| Option | Effect |
| --- | --- |
| `--json` | Structured output instead of text |
| `--filter <text>` | Restrict the walk to matching scan roots |
| `--depth <n>` | Max attribution depth (default 6) |
| `--top <n>` | Applications shown in text output (default 25) |
| `--all` | Also list applications with no attributed storage |
| `--max-directories`, `--max-files` | Scan budgets |
| `--follow-reparse-points` | **Off by default.** Follow junctions (risks double counting) |
| `-v`, `--verbose` | Full WHY blocks and suppressed scan notes |

## What it inspects

```text
%ProgramFiles%                  %APPDATA%
%ProgramFiles(x86)%             %USERPROFILE%\AppData\LocalLow
%ProgramData%                   registry-declared install locations outside the above
%LOCALAPPDATA%
```

`%LOCALAPPDATA%` is treated as a first-class application location, because
Electron/Squirrel-style applications keep their real payload there rather than in
Program Files. `%LOCALAPPDATA%\Packages` (MSIX package payloads) is measured but
not partitioned yet, and the report says so.

## How attribution works, briefly

Every location is judged independently. AppTrace collects machine-readable
evidence, and **evidence must earn the strength of the claim it supports**:

```text
CONFIRMED   the application's own registration names this exact directory
HIGH        the application is independently anchored to this path AND the name agrees
MEDIUM      anchored but not identified, or identified but not anchored
LOW         weak signals only
SHARED      several installed applications legitimately share this location
AMBIGUOUS   ownership exists but cannot be decided from the evidence
UNKNOWN     no evidence that any installed application owns this
```

Each evidence record has a **kind** — Identity, Provenance, Structure,
Relationship or Contradiction — and the kind decides the ceiling. Structure
explains a path but does not prove an owner, so "this is an AppData folder" can no
longer be added to a name resemblance to produce a confident claim. A decisive
contradiction is a gate, not a weight, and cannot be outvoted by accumulated weak
evidence.

Read [docs/ATTRIBUTION_ENGINE_V2.md](docs/ATTRIBUTION_ENGINE_V2.md) §C and §G for
the taxonomy and the exact rules.

## Philosophy

> **AppTrace prefers an honest `UNKNOWN` over a confidently wrong attribution.**

The tool is designed as if a user will eventually act on its conclusions, so a
confident claim must mean something a cautious person can rely on — and when the
evidence does not support one, AppTrace says so rather than guessing. Attribution
is always local and deterministic: no machine learning, no cloud, no telemetry,
and no path ever leaves your machine.

## Documentation

* [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — stack, data model, attribution
  pipeline, classification, accounting, safety
* [docs/ATTRIBUTION_ENGINE_V2.md](docs/ATTRIBUTION_ENGINE_V2.md) — the V2 design,
  what Task 04 implemented, and what is still to come
* [docs/EVALUATION.md](docs/EVALUATION.md) — the regression corpus and evaluation
  harness, and how to add a new attribution case
* [docs/RESEARCH.md](docs/RESEARCH.md) — what was learned from Bulk Crap
  Uninstaller and Jharu, and what was deliberately not adopted

## Non-goals

AppTrace is not a system cleaner. Deletion, cleanup, uninstallation, orphan
removal, registry cleaning, duplicate finding, temp cleaning and startup
management are out of scope, and there is no plan to add them to the read-only
inspector.

## Licence

Apache-2.0 — see [LICENSE](LICENSE). [NOTICE](NOTICE) records the two
Apache-2.0 projects whose *designs* informed AppTrace; no source code from either
is present in this repository.
