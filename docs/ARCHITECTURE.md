# AppTrace — Architecture

Written after the research spike in [`RESEARCH.md`](RESEARCH.md), and kept short
on purpose: this is a small local Windows utility, not a platform.

> **Scope.** This document describes the system's shape: technology choice,
> repository layout, data model, pipeline structure, measurement, safety and
> determinism. The **normative** description of the attribution engine's current
> behaviour is [`ATTRIBUTION_ENGINE_V2.md`](ATTRIBUTION_ENGINE_V2.md). Where the
> attribution detail below (sections 5, 5a and 6) disagrees with that document,
> the V2 document wins.

## 1. What AppTrace is

> **What is the real disk footprint of this application, where is that data
> located, and why does AppTrace believe each location belongs to that
> application?**

The pipeline the whole design serves:

```text
Installed Application
        -> Candidate filesystem locations
        -> Evidence
        -> Attribution
        -> Confidence / Classification
        -> Human-readable explanation
```

The governing invariant is **evidence first, confidence second**. Attribution is
never reduced internally to `Path -> App -> Confidence`; the evidence that
produced a decision is retained and is what the report renders.

## 2. Technology choice

### Options considered

**C# / modern .NET.** First-class registry access through
`Microsoft.Win32.Registry` (`RegistryKey.OpenBaseKey` with explicit 32/64-bit
views is exactly what uninstall discovery needs). Native version-resource reading
through `FileVersionInfo`. `System.Text.Json` for diagnostic output with no
dependency. Treats `net9.0-windows` as a first-class target, so it can move to
WPF/WinUI later without a rewrite. Single `dotnet build`, no extra toolchain.

**Rust.** Faster traversal, smaller native binary, strong safety, a useful
precedent in Jharu, and a Tauri path for a UI. Costs: the Windows registry and
version-resource crates (`winreg`, `windows-sys`) are lower-level than the .NET
equivalents, and a UI would mean adding the whole Tauri/Node toolchain.

**Node/Electron or Python.** Rejected early: registry and metadata access become
FFI or subprocess work, and the deployment story for a Windows storage utility is
worse than either of the above.

### Decision

**C# on .NET 9, targeting `net9.0-windows`.**

Rationale, in priority order:

1. **Registry fidelity.** The single most important input is the uninstall
   registration data, including the forced 32/64-bit hive views. This is a
   two-line operation in .NET and a manual API dance elsewhere.
2. **Metadata and traversal without dependencies.** `FileVersionInfo` and
   `DirectoryInfo.GetFileSystemInfos` (which returns attributes *and* sizes in one
   call, so `FileAttributes.ReparsePoint` is available without a second stat) cover
   everything the tool needs.
3. **Zero-dependency core.** The whole product depends on one NuGet package family,
   and only in the test project. That matters for a tool that must be auditable by
   a cautious user.
4. **The project is Windows-only by nature.** The `-windows` target expresses
   that in the build rather than in defensive code.

Rust would win on traversal throughput and binary size. Neither is the
bottleneck for this tool: **I/O latency and attribution correctness are.**
AppTrace is explicitly about proving the generic attribution model, so the stack
that gets to a correct, explainable model fastest and with the least incidental
risk was chosen.

This was not a novelty decision, and it is reversible at the seams: `Attribution`
and `Model` contain no Windows-specific API calls at all, so the part of the
system with actual design content could be ported without rewriting its logic.

## 3. Repository layout

```text
AppTrace.sln
Directory.Build.props            shared build settings
src/
  AppTrace.Core/                 the entire product logic (class library)
    Model/                       domain types, no I/O
      AppIdentity.cs             normalized application identity
      Evidence.cs                EvidenceType, EvidenceStrength, EvidenceSource, Evidence
      Classification.cs          the confidence buckets
      FootprintItem.cs           FootprintItem + CandidateOwner
      LocationCategory.cs        location categories + ScanError
      TextNormalizer.cs          name/path normalization, byte formatting
    Discovery/
      KnownFolders.cs            the scan scope and path categorization
      UninstallRegistry.cs       read-only registry discovery (+ AppX packages)
    Scanning/
      DirectoryWalker.cs         measurement, reparse-point safety, error capture
      AppTraceScanner.cs         traversal, partitioning, accounting
      ScanOptions.cs             ScanLimits, ScanOptions, ScanResult
    Attribution/
      AttributionEngine.cs       detectors, scoring, classification, stop decisions
      EvidenceWeights.cs         the explicit weight table
      GenericDirectoryNames.cs   names that may never carry ownership
      ExecutableProbe.cs         bounded version-resource and signer probe
      AuthenticodeSigner.cs      embedded-certificate publisher read
    Reporting/
      FootprintReport.cs         app-centric aggregation and totals
      TextReporter.cs            human-readable output, renders WHY from evidence
      JsonReporter.cs            structured diagnostic output
  AppTrace.Cli/                  the `apptrace` executable
    Program.cs                   commands: scan, apps, roots
    CommandLine.cs               ~80-line argument parser
tests/
  AppTrace.Core.Tests/           xunit v3 on Microsoft.Testing.Platform
    Fixtures.cs                  synthetic AppIdentity + engine evaluation helpers
    TempTree.cs                  disposable synthetic directory trees
    AttributionRulesTests.cs     the evidence -> classification rules
    InstallLocationAttributionTests.cs  install-location anchoring
    FilesystemScanningTests.cs   errors, reparse points, accounting invariants
docs/
  RESEARCH.md                    what was learned from BCU and Jharu
  ARCHITECTURE.md                this file
```

Five collaborating pieces, as the task suggested, and no more: discovery,
scanning, attribution, evidence (a data type, not a service), and reporting. There
is no dependency injection container, no plugin loader, no repository abstraction,
and no interface that has only one implementation. The one interface-free
seam that exists — `DirectoryWalker` being injectable into `AppTraceScanner` — is
there because a test needs it.

## 4. Core data model

```text
AppIdentity            one installed application
  Id, DisplayName, NormalizedName
  Publisher, NormalizedPublisher, Version
  InstallLocation, NormalizedInstallLocation
  DisplayIcon, UninstallString, RegistrySource, ProductCode
  RegistryRoot, DiscoveryKind          (uninstall registry vs MSIX)
  PackageFamilyNames[]                 package family names; empty = none observed

FootprintItem          one accounted filesystem location
  Path, DirectoryName, Category, Depth, ParentPath
  SizeBytes            exclusive: bytes this item alone accounts for
  MeasuredSizeBytes    recursive size of the directory on disk
  ExclusiveSizeBytes   == SizeBytes, stated explicitly for the accounting contract
  FileCount
  CandidateOwners[]    every application that was considered, with its evidence
  Classification       of the location as a whole
  StopReason           why recursion stopped here
  UnattributedReason   why no owner was accepted, when that is the answer

CandidateOwner         one claimed relationship
  AppId, AppDisplayName
  Relation             Owns | RelatedTo
  Evidence[]           belongs to this relationship, not to the location
  RelationshipEvidence[]  why the content is ABOUT this application; never scored
  Score                internal diagnostic only
  Accepted             is this one of the owners AppTrace stands behind
  Classification       CONFIRMED / HIGH / MEDIUM / LOW

Evidence               one machine-readable reason
  Type                 EvidenceType (extensible by adding one)
  Kind                 Identity / Provenance / Structure / Relationship / Contradiction
  Description          the specific values that matched
  Strength             Weak / Moderate / Strong / Decisive
  Source               Registry / Filesystem / ExecutableMetadata / Authenticode /
                       PackageIdentity / PathHeuristic / Derived
  SupportsAttribution  false means this record argues against the claim
  Weight               signed contribution to Score
  Specificity          how much of a product's name a match accounts for
```

**Ownership and relationship are separate claims.** *Ownership answers who is
responsible for the bytes. RelatedTo answers what other application the content
concerns.* Only an `Owns` candidate can be accepted, and only an `Owns` candidate
carries a classification or receives exclusive bytes. `RelatedTo` exists so that a
file that belongs to one application while being *about* another can be described
honestly instead of being handed to the wrong owner or left UNKNOWN.

Evidence hangs off the *candidate relationship*, not off the location. The same
directory yields different evidence for different applications — a folder named
`Acrobat` under `Adobe` is strong identity evidence for Acrobat and only
namespace context for Photoshop — so hoisting evidence to the location would
destroy the explanation.

## 5. Attribution pipeline

Before attribution runs, provenance discovery reads the static Windows
registrations once and indexes them (see section 5a). The engine then runs a fixed
list of detectors over one directory, each of which may emit evidence for any
number of applications, because a location legitimately has several plausible
owners.

| Detector | Evidence it can emit | Weight |
| --- | --- | --- |
| Declared install location | `DeclaredInstallLocation` (is the location, name agrees) | +95 |
| Declared install location | `InstallLocationMatch` (inside a declared location) | +70 |
| Inherited ownership | `InheritedFromOwner` (an ancestor is owned) | +18 |
| Directory name | `ExactDirectoryNameMatch` / `NormalizedNameMatch` | +40 / +30 |
| Publisher in path | `KnownPublisherNamespace` | +10 |
| Shared vendor namespace | `PublisherMatch` / `SharedPublisherDirectory` | +15 / −18 |
| Context | `KnownApplicationPath` / `ParentDirectoryMatch` / `ChildDirectoryMatch` | 0 / +12 / +10 |
| Executable metadata | `ExecutableMetadataMatch` | +40 |
| Binary signer | `SignerPublisherMatch` (embedded certificate publisher) | +25 |
| Product code | `ProductCodeMatch` | +55 |
| Registry cross-reference | `RegistryReference` (reserved) | +45 |
| Bounding | `MultipleCandidateOwners` / `PublisherMismatch` / `ConflictingApplicationMatch` | −15 / −22 / −25 |
| Provenance (Task 07) | `DisplayIconMatch` / `ProvenanceAnchorMatch` | +60 / +50 |
| Relationship (not scored) | `SubjectNameMatch` | 0 |

Detector order matters in one place: `CollectInheritedOwnershipEvidence` runs
early so an ancestor's ownership is on the table before name evidence, which is what
lets the two be compared rather than the name silently winning.

Then, in order:

0. **Analyse structure and generate candidates.** Path semantics (Task 05) decides
   whether a segment's name may propose an owner at all, and the identity indexes
   decide which applications are compared.
1. **Score** each candidate as the sum of its evidence weights.
2. **Classify** each candidate individually (section 6).
3. **Select** the accepted candidates. All three conditions must hold:
   * positive score;
   * evidence specific to *this* path — `KnownPublisherNamespace` and
     `ParentDirectoryMatch` describe where the directory sits, so they corroborate
     but can never accept on their own;
   * either a strong signal (declared/contained install location, exact name
     match, executable metadata, product code) or a score at or above 45;
   * within 20 points of the best candidate.

   Accepting several candidates is a feature: it is how `SHARED` and `AMBIGUOUS`
   are represented without inventing a winner.
4. **Classify the location** as a group: one accepted owner uses its own class;
   several at the same score is `SHARED`; one leading the others is `AMBIGUOUS`.
5. **Decide whether to stop**:

| Outcome | Stop descending? |
| --- | --- |
| One accepted owner with a strong signal, or score ≥ 45 | Yes — its whole subtree is accounted for and measured |
| Several accepted owners (`SHARED`/`AMBIGUOUS`) | No — descend to separate their data |
| One accepted owner below the ownership bar | No — the claim is too weak to close the subtree |
| No accepted owner | No — look for a tighter boundary |

The evidence record `StopReason` on every item states which of these applied, so
the report can explain why a particular directory was or was not closed.

### Ownership propagation, and its boundaries

Ownership established at an ancestor is evidence about its descendants. A real
application tree is mostly children named for their content — `User Data`,
`Default`, `Cache`, `logs`, `NvBackend` — so requiring each to rediscover the same
owner from its own name would lose most of the tree. The scanner therefore carries
an `OwnedAncestor` assertion down, and `CollectInheritedOwnershipEvidence` proposes
that owner for the child.

It **proposes rather than decides**, and the record is deliberately weak (+18), so
four boundaries hold:

| Boundary | Behaviour |
| --- | --- |
| A descendant registers itself (`DeclaredInstallLocation`) | The descendant wins outright, `CONFIRMED`; the ancestor does not inherit into it |
| The child is a shared vendor namespace | Inheritance is withheld; the products below it are separately owned |
| A decisive contradiction is present | The claim is forbidden however weak or strong the inheritance |
| The child is a subject store (`Recommendations`, …) | Ownership continues; entries inside it may additionally be `RelatedTo` |

A structural descendant — a cache, a logs directory, a dependency tree — is
deliberately **not** a boundary. Task 05's semantics decide whether a child's name
may claim identity; they do not decide whether the parent still owns the bytes.
`Vivaldi\User Data\Default\Cache` remains Vivaldi's footprint.

### RelatedTo, and why it is conservative

A path owned by A whose content is specifically about installed application B is
reported as:

```text
A  Owns       this path
B  RelatedTo  this path
```

`AppendRelatedApplications` runs only after ownership is decided, and requires all
of: an accepted owner exists, the entry sits inside a subject-data store (looking
through anonymous intermediates such as content hashes), the entry's name matches B
by whole words, it is not a near miss, it clears the same specificity floor
candidate generation uses, and it accounts for **at least two words** of B's name.
One word is never enough: a relationship carries no size and no score, so a reader
cannot weigh it, and the measured false positives (`node`, `sdk`, `helper`, `tool`,
`zip`) are each exactly one word.

A `RelatedTo` candidate is never accepted, never classified, never scored, and
never receives bytes. Its evidence lives in `RelationshipEvidence`, a separate list
from `Evidence` precisely so it cannot be summed into an ownership claim.

### 5a. Static provenance sources (Task 07)

The question these answer is not *"does this path look like an application"* but
*"can Windows itself show that the application actually reaches it"*. Discovery runs
**once per scan**, indexes the anchors, and attribution then queries the index; it
never enumerates services or tasks per directory.

Every source below proves something narrow, and only the narrow statement is used.

| Source | What it proves | What it does not prove |
| --- | --- | --- |
| `DisplayIcon` | This uninstall entry references this executable or resource | That the application owns the referenced file's ancestors |
| App Paths | Windows registers this executable name at this path | Which installed product the executable belongs to |
| Service `ImagePath` | This service launches this executable | That the application owns the service's other data, or that the service name is product identity |
| Scheduled task action | This task launches or references this executable | That the task name is product identity, or that the task owns anything |
| Run key | Windows starts this executable for this user or machine | That the entry's display name is product identity |
| Shortcut target | This shortcut targets this executable or path | That the shortcut's display name is identity, or that a launcher/updater target is the product |

**What none of them prove:** that an application owns every ancestor directory of a
referenced file, or that a publisher owns every path containing its name. A
registration shows the application *reaches* a path, which is exactly the claim the
ladder treats as provenance.

**Linkage is conservative and ordered.** A registration is linked to an installed
application only when one of these holds, and the reason is recorded in the
explanation:

1. the file sits inside the application's own registered install location — no name
   resemblance involved;
2. the file's own version metadata names the application, by whole words only;
3. the file name matches the application's name exactly and the name is not a single
   generic token.

Anything else is refused and counted. Task 02's measurement is why: a bare
executable file name is not product identity, since `electron.exe` carries
`CompanyName = "GitHub, Inc."` while living inside DaVinci Resolve.

**Discovery is not inventory.** Task 02 measured 45 App Paths, 56 non-Windows
service paths, 96 task paths, 18 Run entries and 227 shortcuts on a modest machine.
Creating an installed application from each would be absurd, so these sources only
ever link to an application discovery already found.

**Failure tolerance.** One unreadable key, one malformed command, one unparseable
task, one broken shortcut or one file that disappeared after registration costs an
anchor and a diagnostic, never the scan.

**Read-only.** Every key is opened with `ReadSubTree`. Nothing is executed except
`schtasks /query /xml ONE`, which is an export: it runs no task and changes nothing.
Shortcuts are resolved through the Windows shell's own COM interface, which needs no
third-party runtime.

### Name matching, and why it is strict

`Fold` lower-cases and drops non-alphanumerics. A directory name is compared to
application names in exactly two ways:

* **folded equality** — an exact match (`Discord`, `discord.exe`, `NOTEPAD++`);
* **end-anchored whole-word overlap** — the directory's words are a leading or
  trailing run of the application's words, or vice versa (`Acrobat DC` against
  `Adobe Acrobat DC`).

Plain substring containment is **not** used, even though it is the obvious first
implementation. `google` is a substring of the folded `googlechrome`, so
containment matching lets Google Chrome claim `AppData\Local\Google` and, through
it, every sibling product's data. This was observed during development and is now
a regression test (`VendorDirectory_IsNotClaimedByAProductWhoseNameMerelyContainsIt`).

Two further guards apply to name evidence:

* a **generic** directory name (`Common`, `Cache`, `Shared`, `Packages`,
  `app-1.2.3`, `1.0.0`) can never produce name evidence at all;
* a name match only becomes a strong identity signal when it picks out **exactly
  one** installed application, so `AppX Extended` cannot take the directory that
  plain `AppX` also matches.

### Install locations are authoritative but not omnipotent

Vendors routinely register a shared parent directory as the `InstallLocation` of
every product beneath it, so `C:\Program Files\Google` is often declared by
Chrome, Drive and the updater alike. AppTrace therefore splits the claim in two:

* the path **is** the declared install location → `DeclaredInstallLocation`,
  decisive, `CONFIRMED`;
* the path is **inside** a declared install location → `InstallLocationMatch`,
  strong, `HIGH` at most.

A directory named after a publisher that **more than one** installed product
declares as its install location is recognised as a *vendor namespace*. It is
reported as `SHARED` and never confirmed for any single product, whatever the
registry says, and it never stops the descent — the per-product boundary lives in
the subdirectories. This is the mechanism behind AppTrace's headline example:

```text
Google                                     SHARED   (descend)
  Chrome                    Chrome         HIGH
  DriveFS                   Google Drive   CONFIRMED
  GoogleUpdater             shared component, LOW
  CrashReports              no owner       UNKNOWN
```

The engine also keeps a publisher-mismatch signal: if the directory name is
another publisher's namespace, a product from a different publisher is a sibling
rather than the owner, and is recorded as contradicting evidence. Without it a
product resolved from an ancestor folder would inherit ownership of every sibling
product's data beneath it.

## 6. Classification rules

The score exists so the classification is a mechanical function of evidence. It
is reported in JSON as a diagnostic and is **never** shown as a percentage:
there is no defensible probabilistic model.

**Per candidate**, evaluated in order (`AttributionEngine.ClassifyCandidate`):

| Condition | Result |
| --- | --- |
| `DeclaredInstallLocation` present | `CONFIRMED` |
| Install-location evidence **and** the name names the product | `CONFIRMED` |
| Install-location evidence without a corroborating name | `HIGH` |
| The name names the product, plus (≥ 2 supporting records **or** publisher match), nothing contradicting | `HIGH` |
| The name names the product, nothing else | `MEDIUM` |
| A scoped word match on the name, nothing contradicting | `HIGH` |
| A scoped word match on the name, with contradicting records | `MEDIUM` |
| No name signal: ≥ 2 supporting records, none contradicting, at least one Moderate | `MEDIUM` |
| Anything else with supporting evidence | `LOW` |
| No supporting evidence | `UNKNOWN` |

"Naming the product" means exact folded equality, executable metadata agreeing, or
a product code match. A scoped word match (`Chrome` against `Google Chrome`) is
one step weaker: it is already restricted to a case where no other installed
application matches the same directory name, so it can reach `HIGH` on its own,
but it can never reach `CONFIRMED` without the application's own registration data
naming the path.

**Per location**, as a group:

| Condition | Result |
| --- | --- |
| No accepted owner | `UNKNOWN` |
| Exactly one accepted owner | that owner's class |
| ≥ 2 accepted owners tied at the top score | `SHARED` |
| ≥ 2 accepted owners, one leading | `AMBIGUOUS` |

`SHARED` and `AMBIGUOUS` are terminal answers, not failures. Reporting
"three Adobe products could own this" is the correct output for a directory that
three Adobe products genuinely share.

## 7. Size accounting

Three distinct numbers, and the difference between them is the whole design:

| Number | Meaning |
| --- | --- |
| `MeasuredSizeBytes` | recursive size of the directory on disk |
| `SizeBytes` / `ExclusiveSizeBytes` | the part of that subtree this item alone accounts for |
| `ScanResult.TotalMeasuredBytes` | sum of `SizeBytes` over all items |

The invariant, asserted by `FilesystemScanningTests.AssertNoOverlaps`:

```text
sum(item.ExclusiveSizeBytes) == ScanResult.TotalMeasuredBytes
```

and every item's `ExclusiveSizeBytes <= MeasuredSizeBytes`.

Traversal follows these rules:

* a directory AppTrace **stops at** becomes one item owning its whole subtree;
* a directory it **descends into** does not become an owner item; its children are
  itemised and the leftover becomes a single **residual** item on the parent;
* a scan root is never attributed (it is a container) but is still accounted for,
  so loose files at the root and excluded subtrees cannot vanish from the totals;
* a directory already accounted for by the install-location pass is never resolved
  a second time.

Items are therefore sometimes nested paths — a parent's residual alongside an
itemised child — but their byte ranges are disjoint by construction. Overlapping
*paths* are expected; overlapping *bytes* are impossible.

## 8. Safety

Read-only is structural, not a policy that could be forgotten. The complete set
of filesystem operations in `AppTrace.Core` is:

```text
DirectoryInfo.GetFileSystemInfos()    enumerate
FileSystemInfo.Attributes             read attributes
FileInfo.Length                       read size
File.GetAttributes()                  read attributes
Directory.EnumerateFiles(..., "*.exe")  enumerate
FileVersionInfo.GetVersionInfo()      read version resource
```

There is no `Delete`, `Move`, `Create`, `Write`, `Rename`, `SetAttributes`, no
process launch, and no registry write (`OpenSubKey` is called with
`RegistryKeyPermissionCheck.ReadSubTree`; `CreateSubKey` and `SetValue` appear
nowhere). No CLI command performs remediation, and none is planned.

Additional guarantees:

* **Reparse points are never followed.** Following a junction inside AppData both
  double counts and can loop forever. Each one is measured as an opaque leaf, the
  decision is recorded as an informational `ScanError`, and
  `--follow-reparse-points` exists only to make the override explicit. Neither
  upstream project handles this, so it is also a correctness fix (see
  `RESEARCH.md` §1.7).
* **Inaccessible paths are reported, never faked.** Unreadable directories
  produce a `ScanError` with a stage (`enumerate`, `entry`, `registry-open`, …)
  and the affected size is a floor, not a fact. `ScanResult.Truncated` is set when
  a budget stops the scan.
* **Budgets bound the work.** 120 000 directories, 4 000 000 files, and a
  configurable attribution depth (default 6). Budget exhaustion is surfaced as a
  warning, never swallowed.
* **Uncertainty is preferred to a false positive.** `UNKNOWN` is a valid answer,
  and the whole scoring design is biased that way.

## 9. Determinism and testability

The attribution engine is a pure function of an application list, a path and a
small options object. It takes no filesystem dependency it does not need:
executable probing can be disabled (`MaxExecutableProbes = 0`), and every test
that exercises the rules does so with a synthetic application list and a path that
does not need to exist. Tests that do need real directories build them in a
disposable tree (`TempTree`) and assert against `Directory.EnumerateFiles`, so the
expected byte counts come from the filesystem itself rather than from constants
that could drift.

## 10. Performance

The scanner is intentionally sequential. A single measured pass walks each directory
once, caching size, file count and child list per directory, so the partitioning
pass costs no extra I/O. The whole-scan workload is bounded by the file budget.

Known characteristics, not yet addressed:

* measurement is single-threaded; several hundred thousand files take tens of
  seconds on a warm cache;
* sizes are read through `FileSystemInfo.Length` from a directory listing rather
  than a dedicated scan API, which is fine for correctness but leaves throughput
  on the table;
* there is no cancellation token, so `Ctrl+C` abandons the process rather than
  stopping the walk cleanly.

Jharu's comment in `scanner.rs:124-127` is the reason no naive threading was
added: nesting a parallel walk under parallel work silently starves the pool and
produces empty results. When this is addressed it should be measured, not
guessed.

## 11. Explicit non-goals

No deletion, cleanup, uninstall, registry modification, orphan removal, system
optimisation, startup management, treemap, duplicate finder, browser or
developer-cache cleaner, AI attribution, cloud service, telemetry, accounts,
backend, GUI, or large hard-coded application database. AppTrace exists to prove
the generic attribution model, and every item above is a separate decision that
needs its own evidence.

## 12. Extending the model

Adding a detector is meant to be small and local:

1. add a member to `EvidenceType`;
2. add its weight to `EvidenceWeights.WeightOf`;
3. call `Add(...)` from a new `Collect*Evidence` method in `AttributionEngine`;
4. if it should be able to establish ownership on its own, add it to the relevant
   condition in `ClassifyCandidate`.

Nothing else changes: the model, the scanner, the accounting, the text report and
the JSON output all work from `Evidence` records generically. Which evidence types
are live today and which are defined but reserved is stated in
[`ATTRIBUTION_ENGINE_V2.md`](ATTRIBUTION_ENGINE_V2.md), section 3.
