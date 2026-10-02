# AppTrace — Attribution Engine V2

**Status:** normative. This is the authoritative description of how AppTrace
attributes storage today, as implemented and as tested. If this document and the
code disagree, the code that the test suite exercises is correct and this document
is a bug.

It describes the engine as an architecture, not as a project history:

> The engine does X because Y.

The design rationale, the rejected approaches and the task-by-task chronology live
separately in [`ATTRIBUTION_HISTORY.md`](ATTRIBUTION_HISTORY.md). That document is
**historical and non-normative** — it explains how the current design was reached,
and it must never be used to reconstruct current behaviour. The regression harness
that protects this behaviour is described in [`EVALUATION.md`](EVALUATION.md).

Companion documents: [`ARCHITECTURE.md`](ARCHITECTURE.md) (stack, repository layout,
data model, measurement and safety), [`RESEARCH.md`](RESEARCH.md) (historical
research notes).

---

## 1. What the engine answers, and the invariant behind it

For each filesystem location AppTrace asks two separate questions:

* **Who is responsible for the bytes?** — an *ownership* assertion; only this can be
  accepted, classified and sized.
* **What other application does the content concern?** — a *relationship*
  assertion (`RelatedTo`); it owns zero bytes.

The governing invariant is:

> **Evidence must earn the strength of the claim it supports.**

A record that shows what a path *looks like* cannot, on its own, prove *who owns*
it. The engine therefore never reduces a location to `Path → App → Confidence`: it
keeps the machine-readable evidence for and against every candidate, and the report
renders that evidence directly.

A second invariant protects honesty:

> **An honest `UNKNOWN` is preferred to a confidently wrong attribution.**

Fewer confident claims is not a failure; a confident claim that is wrong is. This is
why the engine is biased toward false negatives and why `UNKNOWN` is a first-class
result (§11), not a gap to be filled by guessing.

---

## 2. The pipeline

The exact order in which the engine runs, per directory
(`AttributionEngine.Evaluate`):

```text
Installed-application discovery            (uninstall registry + MSIX registration)
        ↓
Identity and provenance enrichment         (identity indexes; static Windows anchors)
        ↓
Filesystem scan and path semantics         (measurement; PathSemantics per segment)
        ↓
Detectors, in this fixed order
        1. Declared install location
        2. Inherited ownership            (an ancestor is owned)
        3. Provenance anchors             (Windows registrations)
        4. Directory name
        5. Data-root product boundary
        6. Publisher / vendor namespace
        7. Context                        (parent / child / known-application path)
        8. Executable metadata
        9. Bounding                       (shared / conflicting / system-managed)
        ↓
Candidate generation                       (BuildCandidates: score, sort, cap)
        ↓
Ownership acceptance                       (DecideOwnership, against ContainerBoundaryOf)
        ↓
Classification                             (ClassifyLocation → CONFIRMED…UNKNOWN)
        ↓
Relationship resolution                    (AppendRelatedApplications; after ownership)
        ↓
Subtree closure / residual accounting      (AppTraceScanner)
        ↓
Measurement and reporting                  (FootprintReport → TextReporter / JsonReporter)
```

Detector order matters in three places:

* **Inherited ownership runs early**, so an ancestor's claim is on the table before
  name evidence and the two can be compared rather than the name silently winning.
* **Provenance runs before name evidence**, because an independent registration must
  be able to *propose* a candidate whose own name would otherwise be suppressed by
  position (§6).
* **Bounding runs last**, so contradiction and ambiguity records are computed against
  the candidate set the other detectors produced.

Each detector may emit evidence for several applications, because a location
legitimately has several plausible owners. Nothing stores a pre-written explanation;
the "why" is rendered from the evidence records.

---

## 3. Evidence

### 3.1 Kinds

Every evidence record has exactly one **kind**, which decides what question it
answers and therefore the ceiling it may support. The mapping lives in one place,
`EvidenceClassification`, so a new evidence type cannot be added without a
deliberate kind.

| Kind | Question it answers | Can it establish ownership? |
| --- | --- | --- |
| **Identity** | What does this name or content appear to be? | No — corroborates only |
| **Provenance** | Is the application independently anchored to this path? | Yes, with identity corroboration for HIGH |
| **Structure** | What does this path tell us about the storage layout? | No — never |
| **Relationship** | Is this data owned by X, or merely about X? | No — never scored |
| **Contradiction** | What argues against this claim? | No — gates or caps |

There is deliberately **no `Accounting` kind**. Measurement completeness
(inaccessible files, skipped reparse points, lower-bound sizes, scan errors) is
orthogonal to attribution and must never become ownership evidence.

`Structure` in particular "describes the location, never the owner": *being under
`AppData`* is true of almost every path AppTrace inspects, so it can never raise
ownership confidence. It exists to make candidate generation and path reading
explicit and auditable.

### 3.2 Type → kind mapping

| Kind | Evidence types (¹ = reserved, see below) |
| --- | --- |
| **Identity** | `ExactDirectoryNameMatch`, `NormalizedNameMatch`, `ExecutableMetadataMatch` |
| **Provenance** | `DeclaredInstallLocation`, `InstallLocationMatch`, `InheritedFromOwner`, `DisplayIconMatch`, `ProvenanceAnchorMatch`, `DiscoveryLocationMatch`¹, `ProductCodeMatch`¹, `RegistryReference`¹ |
| **Structure** | `KnownApplicationPath`, `DataRootProductBoundary`, `KnownPublisherNamespace`, `ParentDirectoryMatch`, `MultipleCandidateOwners`, `SharedPublisherDirectory`, `PublisherMatch`, `ChildDirectoryMatch`¹, `GenericDirectoryName`¹ |
| **Relationship** | `SubjectNameMatch`, `UnknownApplication`¹ |
| **Contradiction** | `PublisherMismatch`, `ConflictingApplicationMatch`¹, `SystemManagedPath`¹ |

Three placements are deliberate judgement calls:

* **`PublisherMatch` and `KnownPublisherNamespace` are Structure, not Identity.** A
  publisher in the path identifies a *vendor namespace*, not a product; it
  corroborates a candidate set without proving one owner.
* **`MultipleCandidateOwners` is Structure, not Contradiction.** Several plausible
  owners is a reason to report `SHARED` or keep descending, never a reason to say one
  of them is wrong. Ambiguity is not opposition.
* **`SubjectNameMatch` is Relationship and is never scored**, which is what keeps
  "the container owns this; product B is its subject" from decaying into "B owns
  this".

¹ **Reserved:** the type, kind and weight are defined, but no production detector
emits it yet, so it never appears in a real attribution. Reserved types are
`DiscoveryLocationMatch`, `ProductCodeMatch`, `RegistryReference`,
`ChildDirectoryMatch`, `GenericDirectoryName`, `UnknownApplication`,
`ConflictingApplicationMatch` and `SystemManagedPath`. In particular, of the three
types classified as decisive contradictions, only `PublisherMismatch` is currently
emitted; the other two are gates that are implemented and tested but not yet fed.

A type added without a mapping falls back to Identity — the most conservative
choice, because Identity alone can never reach HIGH.

### 3.3 Strength, weight and specificity

* **`EvidenceStrength`** (`Weak`, `Moderate`, `Strong`, `Decisive`) is the declared
  qualitative strength of a record.
* **`EvidenceWeights.WeightOf`** is the signed score contribution. Weights only
  *order and threshold* candidates; they never decide the top of the ladder. The
  table is deliberately explicit and coarse, and is reproduced in §3.4.
* **`Specificity`** is `0.0` for non-identifying or generic and `1.0` for fully
  product-specific. Only name-derived identity evidence computes a real value;
  everything else defaults to `1.0`, meaning *"specificity does not apply"*, not
  *"fully specific"*. Specificity can only ever **withhold** strength, never grant
  it (§5).

### 3.4 Weights (normative reference)

| Evidence type | Weight | Kind |
| --- | ---: | --- |
| `DeclaredInstallLocation` | +95 | Provenance |
| `DiscoveryLocationMatch`¹ | +85 | Provenance |
| `InstallLocationMatch` | +70 | Provenance |
| `DisplayIconMatch` | +60 | Provenance |
| `ProductCodeMatch`¹ | +55 | Provenance |
| `ProvenanceAnchorMatch` | +50 | Provenance |
| `RegistryReference`¹ | +45 | Provenance |
| `ExactDirectoryNameMatch` | +40 | Identity |
| `ExecutableMetadataMatch` | +40 | Identity |
| `NormalizedNameMatch` | +30 | Identity |
| `InheritedFromOwner` | +18 | Provenance |
| `PublisherMatch` | +15 | Structure |
| `ParentDirectoryMatch` | +12 | Structure |
| `KnownPublisherNamespace` | +10 | Structure |
| `ChildDirectoryMatch`¹ | +10 | Structure |
| `KnownApplicationPath` | 0 | Structure |
| `DataRootProductBoundary` | 0 | Structure |
| `SubjectNameMatch` | 0 | Relationship |
| `SharedPublisherDirectory` | −18 | Structure |
| `MultipleCandidateOwners` | −15 | Structure |
| `GenericDirectoryName`¹ | −20 | Structure |
| `PublisherMismatch` | −22 | Contradiction |
| `ConflictingApplicationMatch`¹ | −25 | Contradiction |
| `UnknownApplication`¹ | −30 | Relationship |
| `SystemManagedPath`¹ | −100 | Contradiction |

¹ Reserved: weight defined, no production detector emits the type (§3.2).

Numeric thresholds (all in `AttributionEngine`):

| Constant | Value | Meaning |
| --- | ---: | --- |
| `StopScoreThreshold` | 45 | Score at which a single accepted owner closes its subtree |
| `NamespaceEvidenceBar` | 20 | Score at which an application may be reported as a co-owner while descending |
| `TieScoreWindow` | 20 | Candidates this close are treated as equally plausible |
| `MinimumCorroboratingSpecificity` | 0.50 | Name coverage needed for identity to corroborate provenance into HIGH |
| `MinimumRelationshipTokens` | 2 | Words of a product name a `RelatedTo` entry must account for |
| `MaxCandidatesPerLocation` | 16 | Cap on candidates per directory |
| `MinimumUsefulTokenLength` | 3 | Shortest token that enters an identity index |

### 3.5 Contradictions are gates, not weights

A contradiction's **`ContradictionKind`** decides how it argues:

* **Decisive** (`PublisherMismatch`, and the reserved `ConflictingApplicationMatch`
  and `SystemManagedPath`) forbids the claim outright. No accumulation of weak
  supporting evidence may outvote it. The gate is checked before any score is
  consulted, and it is also honoured by ownership acceptance, so a location can never
  be `UNKNOWN` in one view and owned in another. Only `PublisherMismatch` is emitted
  today; the other two are implemented and tested gates awaiting a detector.
* **Limiting** (everything else) only caps confidence through its weight.

---

## 4. Candidate generation vs ownership acceptance

These are separate concepts and remain separate in the code:

* **A candidate** is an application worth *evaluating* for a path. `BuildCandidates`
  scores each application's evidence, drops purely-negative notes, sorts by score,
  and caps the list at `MaxCandidatesPerLocation`.
* **An owner** is a candidate AppTrace stands behind. A candidate does **not** become
  an owner merely by existing, or because its score is high.

Acceptance (`DecideOwnership`) requires *all* of:

1. a positive score;
2. at least one supporting record (structure alone never establishes ownership);
3. `MeetsOwnershipBar` — i.e. the candidate's own classification reaches HIGH or
   CONFIRMED — *or*, when nothing at this location meets that bar, a weaker
   `NamespaceEvidenceBar` score so a plausible co-owner can still be reported while
   the scan keeps descending;
4. no decisive contradiction;
5. a score within `TieScoreWindow` of the best candidate — unless the candidate
   declared this exact directory as its install root at a container boundary (§6.4).

Accepting several candidates is a feature: it is how `SHARED` and `AMBIGUOUS` are
represented without inventing a winner.

`MeetsOwnershipBar` is *derived from* the classification ladder (`ClassifyCandidate`
reaches HIGH or CONFIRMED), so "may be accepted as a confident owner" and "may claim
confident ownership" can never drift apart.

**Establishment is a separate, score-based decision.** When exactly one candidate is
accepted, its subtree is closed (`OwnershipEstablished`) if its score reaches
`StopScoreThreshold` (45) — not if its classification does. A location whose
classification is MEDIUM but whose single owner scores ≥ 45 is therefore *fully
accounted for* while the report honestly states MEDIUM. This is intentional: the
file-attribution decision ("this whole tree is this application's bytes") and the
confidence decision ("how sure are we of the owner") are different questions, and the
report shows both.

---

## 5. Identity specificity: a name is not an owner

Directory-name similarity is **not** sufficient ownership proof. The engine reads a
name only under strict rules, because a wrong confident claim is worse than a missing
one.

### 5.1 How names are compared

`TextNormalizer.Fold` lower-cases and drops non-alphanumerics. A directory name is
compared to application names in exactly two ways:

* **folded equality** — an exact match (`Discord`, `discord.exe`, `NOTEPAD++`); or
* **end-anchored whole-word overlap** — the directory's words are a leading or
  trailing run of the application's words, or vice versa (`Acrobat DC` against
  `Adobe Acrobat DC`).

Plain substring containment is **not** used. `google` is a substring of
`googlechrome`, so containment would let Google Chrome claim `AppData\Local\Google`
and, through it, every sibling product's data.

### 5.2 Coverage, the specificity floor

`Specificity` for a name match is **name coverage**: the fraction of the
application's name tokens the directory name accounts for. Measured values:

| Directory | Application | Coverage |
| --- | --- | ---: |
| `Vivaldi` | Vivaldi | 1.00 |
| `Chrome` | Google Chrome | 0.50 |
| `OneDrive` | Microsoft OneDrive | 0.50 |
| `helper` | ASUS Update Helper | 0.50 |
| `sdk` | ASUS Aura SDK | 0.33 |
| `Universal` | Universal Holtek RGB DRAM | 0.25 |
| `node` | Node.js Krypton via nvm-windows | 0.20 |
| `tool` | Epson Printer Driver Security Support Tool | 0.17 |

`0.50` (`MinimumCorroboratingSpecificity`) is the floor at which identity may
corroborate a provenance anchor into HIGH, and the floor at which a name may propose
a candidate at all. It is **a discriminator fitted to the cases we have, not a
statistical law**, and it is only ever used to *withhold* strength.

This is how low-information vocabulary such as `node`, `sdk`, `helper`, `tool`,
`Universal` and `zip` is prevented from regaining product identity: not by a
product-name blacklist or word list, but by **information/specificity**. None of
those words appears in any list in the engine.

### 5.3 Other name guards

* **Generic directory names** (`GenericDirectoryNames`) can never produce name
  evidence. The list holds only names that are meaningless *at every depth*
  (`Common`, `Resources`, `bin`, `data`, …); it deliberately excludes words whose
  meaning depends on position, which belong to `PathSemantics` instead (§6).
* **Uniqueness** — a name match only becomes a strong identity signal when it picks
  out exactly one installed application, so `AppX Extended` cannot take the directory
  that plain `AppX` also matches.
* **Near misses are rejected.** A directory that says *more* than the product's name
  does, where the extra is not explained by the application's own display name, is a
  different thing: `Cities Skylines II` is not `Cities: Skylines`. A missing
  candidate means UNKNOWN, not the nearest installed application.

---

## 6. Filesystem semantics and ownership boundaries

### 6.1 Structure kinds

`PathSemantics` answers one question per segment: *is this segment part of an
established storage structure whose children are content rather than applications?*
`StructureKind` is deliberately small and contains only **reusable storage concepts**,
never a list of "generic words":

`Unknown`, `PackageDependencyTree` (`node_modules`, `site-packages`),
`PackageManagerCache` (`npm-cache`, `_npx`, `_cacache`, `pip`, `nuget`),
`ApplicationRuntime` (`runtime`, `jre`, `cef`, `webview2`),
`ComponentFramework` (`QtQuick`, `qt`, `electron`, `chromium`),
`ApplicationProfile` (`User Data`), `ApplicationCache` (`Cache`, `GPUCache`,
`DXCache`), `Logs`, `Temporary`, `ApplicationUpdateTree` (`update`, `packages`,
`SquirrelTemp`), `SubjectData` (`Recommendations`, `Catalog`, `Wishlist`, …).

Two entries are deliberately absent:

* `sdk`, `helper`, `node`, `tool`, `universal`, `zip`, `client`, `service`,
  `manager` — ambiguous vocabulary, handled by specificity (§5.2), not by a
  blacklist.
* `vendor` — it is a dependency directory in some ecosystems but also one of the
  commonest vendor-namespace names in `Program Files`; the two are indistinguishable
  by name. Vendor namespaces are handled as ownership boundaries (§6.4), where the
  evidence actually exists.

### 6.2 The two structural rules

> **Structure suppresses identity.** Once an anchor appears in the ancestry, the
> segments beneath it are content. `node_modules\…\sdk` cannot propose ASUS Aura SDK
> however good the name looks.

> **Structure is positional.** An anchor governs what is inside it, and suppresses
> its own name only when it is itself nested more than one level below the scan root.
> So `Program Files\Cache` may be the product `Cache`, while
> `Vendor\Product\Cache\data` may not.

The positional rule is what makes a deep segment "a component of whatever contains
it". That reasoning may be **lifted** for a candidate that something *other than its
name* already proposed (a registration, an ancestor, or a data-root boundary) — but
only when the name still accounts for at least half of what the directory says. The
*semantic* suppression inside `node_modules` or a package cache may **never** be
lifted: a package name is never a product name, however it was proposed.

### 6.3 Data-root boundaries

`PathSemantics.BoundaryUnderDataRoot` names where a product's own data namespace may
begin, relative to a known application-data root (`ProgramData`, `LocalAppData`,
`Roaming`, `LocalLow`):

* depth 1 below the root → `Product` (`ProgramData\LGHUB`, `Roaming\LarkShell`);
* depth 2 → `VendorProduct` (`ProgramData\IObit\Driver Booster`);
* deeper → `None`.

`CollectDataRootBoundaryEvidence` emits `DataRootProductBoundary` for applications
the name detector already proposed at such a boundary. It is `Structure` worth **0**:
it makes the candidate-generation decision explicit in WHY, but it cannot propose an
application on its own and cannot move a candidate up the ladder. It is skipped
inside an established structure, so a cache or dependency tree gains nothing from
sitting under `AppData`.

### 6.4 Install locations and containers

Vendors routinely register a shared parent as the `InstallLocation` of every product
beneath it (`C:\Program Files\Google`, `C:\Program Files\Adobe`), so the claim is
split:

* the path **is** the declared install location → `DeclaredInstallLocation`,
  decisive, `CONFIRMED`;
* the path is **inside** a declared install location → `InstallLocationMatch`,
  strong, `HIGH` at most.

`ContainerBoundaryOf` recognises a directory as a **container** — never established
for a single product — in three name-independent shapes:

1. **co-declared install root** — two or more installed products register *this exact
   directory* as their install location;
2. **shared vendor namespace** — the directory name equals a publisher name carried
   by two or more installed applications;
3. **single-product vendor namespace** — the directory is directly inside a scan
   root (`Depth == 1`), its name could identify a product, no application claims it
   as its own install location, the directory is not one of any application's own
   names (display name or `ProductCode`), and an installed application's own *product
   boundary* demonstrably lies below it. That boundary is the directory Windows
   registers as the application's install location, or a directory its own
   executable is anchored in — and only anchors whose `ExecutableRole` is
   `MainApplication`. The first segment below must pass
   `GenericDirectoryNames.CanIdentifyAProduct` and be `StructureKind.Unknown`, which
   refuses `Vendor\Updater`, `Vendor\cache` and a bare version number.

Shape 3 exists because shape 2 alone made the boundary a function of *what else
happens to be installed*: removing one sibling could silently promote a vendor
directory to a product root. Shape 3 asks a question about **one** application — *is
this application's boundary strictly below this directory?* — whose answer is a
property of the filesystem and that application's registration, so installing or
removing a sibling cannot change it. The rule never counts products.

At a container boundary every product that declared the root is accepted as a
co-owner, `OwnershipEstablished` is forced false, and the scanner descends to resolve
each `Vendor\Product` child on its own evidence. The rule is generic: which products
declare a directory is a registry fact, and "Adobe" or "Tencent" is not special.

### 6.5 Publisher-mismatch and system-managed signals

If the directory name is another publisher's namespace, a product from a different
publisher is a sibling rather than the owner, and is recorded as
`PublisherMismatch` — a decisive contradiction (§3.5), a gate rather than a weight.
The reserved `SystemManagedPath` type is defined to mark Windows-managed or
AppTrace-protected locations on the same terms, but no detector emits it yet.

---

## 7. Provenance

Provenance is the engine's strongest *independent* evidence: a static Windows
registration that shows the application actually **reaches** a path, rather than a
path that merely looks like the application.

### 7.1 The epistemic rule

A registration proves only what it says:

> `DisplayIcon` proves the uninstall entry references a file; a service proves it
> launches a file; a task proves it runs one; a Run key proves Windows starts one; a
> shortcut proves it targets one; App Paths proves Windows resolves a name to one.

**None of them proves that an application owns every ancestor of a referenced file,
and none makes a registration's own name into product identity.**

### 7.2 Sources (all implemented)

Discovery runs **once per scan** and indexes the anchors; attribution then queries
the index. It never enumerates services or tasks per directory.

| Source (`ProvenanceSource`) | What it proves |
| --- | --- |
| `DisplayIcon` | The uninstall entry references this executable or resource |
| `AppPath` | Windows registers this executable name at this path |
| `Service` | This service launches this executable |
| `ScheduledTask` | This task launches or references this executable |
| `RunKey` | Windows starts this executable for this user or machine |
| `Shortcut` | This shortcut targets this executable or path |

Each anchor carries an **`ExecutableRole`** — `MainApplication`, `Launcher`,
`Updater`, `Uninstaller`, `Service`, `Helper` — so an uninstaller or updater is not
read as the product's own executable. Only `MainApplication` anchors may prove that a
product boundary lies below a directory (§6.4).

### 7.3 Linkage is conservative and ordered

A registration is linked to an installed application only when one of these holds,
and the reason is recorded in the explanation:

1. the file sits inside the application's own registered install location — no name
   resemblance involved;
2. the file's own version metadata names the application, by whole words only;
3. the file name matches the application's name exactly and the name is not a single
   generic token.

Anything else is refused and counted. A bare executable file name is not product
identity — `electron.exe` carries `CompanyName = "GitHub, Inc."` while living inside
DaVinci Resolve — so discovery volume is not attribution success.

### 7.4 Correlated provenance is bounded

`CollectProvenanceAnchorEvidence` groups anchors by
`(application, normalized physical target)` and emits **exactly one scored record per
physical target**. The record's description still enumerates every registration
surface that reached it, including how many times (`App Paths, shortcut (x3)`), so
the multiplicity stays visible in WHY without creating unbounded independent
confidence. Distinct binaries of one product — a main executable and a service —
remain separate scored facts. Deduplication uses stable properties only:
application, normalized target path, role, registration.

Weights: `DisplayIconMatch` +60, `ProvenanceAnchorMatch` +50 — both `Provenance`.
Being `Provenance`, either can carry a claim only together with corroborating
identity (§10).

### 7.5 Failure tolerance

One unreadable key, one malformed command, one unparseable task, one broken shortcut
or one file that disappeared after registration costs an anchor and a diagnostic,
never the scan.

---

## 8. Ownership propagation

Ownership established at an ancestor is evidence about its descendants. Real
application trees are mostly children named for their content (`User Data`,
`Default`, `Cache`, `logs`, `NvBackend`), so re-deriving the same owner from each
child's own name would lose most of the tree. The scanner carries an `OwnedAncestor`
assertion down, and `CollectInheritedOwnershipEvidence` proposes that owner for the
child with an `InheritedFromOwner` record at **+18**.

It **proposes rather than decides**, and is deliberately weak, so:

* a descendant with its own registration wins outright (`CONFIRMED`) and the ancestor
  does not inherit into it;
* a shared vendor namespace withholds inheritance;
* a decisive contradiction forbids the claim regardless of inheritance;
* a subject store is **not** a boundary — ownership continues, and entries inside it
  may additionally be `RelatedTo`.

A structural descendant — a cache, a logs directory, a dependency tree — is
deliberately **not** a boundary: `PathSemantics` governs whether a child's *name* may
claim identity, not whether the parent still owns the bytes.
`Vivaldi\User Data\Default\Cache` remains Vivaldi's footprint. This is why an
ordinary descendant lands at MEDIUM rather than HIGH: the bytes are attributed and
the tree is closed correctly, while the confidence stays honest.

---

## 9. Relationships: `Owns` and `RelatedTo`

`CandidateRelation` has exactly two members:

* **`Owns`** — who is responsible for the bytes. Only an `Owns` candidate may be
  accepted, classified, or receive exclusive bytes.
* **`RelatedTo`** — what other application the content concerns. It is never
  accepted, never classified, never scored, and **owns zero bytes**.

`AppendRelatedApplications` runs **only after ownership is decided**, because a
relationship is a statement about a location something else already owns; without an
owner there is nothing for the related application to be relative to, and inventing
one would trade an honest UNKNOWN for an unsupported claim. It requires all of:

1. an accepted owner exists;
2. the entry sits inside a subject-data store (`StructureKind.SubjectData`), looking
   *through* anonymous intermediates such as content hashes or indices
   (`Recommendations\<hash>\cities_skylines`);
3. a whole-word name match against an installed application;
4. not a near miss, and past the specificity floor;
5. **at least two words** of the product's name.

Rule 5 is what keeps `node`, `sdk`, `helper`, `tool` and `zip` from becoming
relationships: each is exactly one word of some product's name, and a relationship
carries no size and no score, so a reader cannot weigh it. Being wrong here is worse
than saying nothing.

Relationships are reported separately from owner precision and are never counted in
`owners` — a relationship can never be mistaken for an attribution.

---

## 10. Classification

### 10.1 Per candidate

Evaluated in order by `ClassifyCandidate`. Evidence **kind** decides the ceiling; the
score only orders and corroborates within it.

| Condition | Result |
| --- | --- |
| No supporting evidence | `UNKNOWN` |
| A decisive contradiction is present | `UNKNOWN` — gate short-circuits |
| `DeclaredInstallLocation` present | `CONFIRMED` |
| Provenance **and** corroborating identity (specificity ≥ 0.50) | `HIGH` |
| Provenance without corroborating identity | `MEDIUM` |
| Identity without provenance | `MEDIUM` |
| No identity/provenance, but ≥ 2 supporting records incl. one Moderate, nothing contradicting | `MEDIUM` |
| Anything else with supporting evidence | `LOW` |

"Corroborating identity" means an `Identity` record of type
`ExactDirectoryNameMatch`, `NormalizedNameMatch` or `ExecutableMetadataMatch` whose
specificity reaches 0.50; Structure records (`KnownApplicationPath`,
`DataRootProductBoundary`, `KnownPublisherNamespace`, `ParentDirectoryMatch`,
`ChildDirectoryMatch`) can never raise a claim.

### 10.2 Per location

| Condition | Result |
| --- | --- |
| No accepted owner | `UNKNOWN` |
| Exactly one accepted owner | that owner's class |
| ≥ 2 accepted owners tied at the top score | `SHARED` |
| ≥ 2 accepted owners, one leading | `AMBIGUOUS` |

### 10.3 The bucket meanings

These describe evidence quality and ownership certainty, not numeric score ranges:

| Classification | Meaning |
| --- | --- |
| `CONFIRMED` | The application's own registration names this exact directory. |
| `HIGH` | The application is independently anchored to this path **and** the name specifically agrees. |
| `MEDIUM` | Anchored but not identified, or identified but not anchored. The honest ceiling for identity without provenance. |
| `LOW` | Weak or structural support only. A hint, not a fact. |
| `SHARED` | Several installed applications legitimately share this location. A terminal, correct answer. |
| `AMBIGUOUS` | Ownership exists but one owner cannot be decided from the evidence. Also terminal. |
| `UNKNOWN` | No installed application is defensibly the owner. |

`SHARED` and `AMBIGUOUS` are outcomes, not failures: "three Adobe products could own
this" is the correct answer for a directory three Adobe products genuinely share.

---

## 11. `UNKNOWN` is a first-class result

> Current observable evidence does not justify a defensible owner.

`UNKNOWN` is deliberately preserved whenever evidence is insufficient, and reducing
`UNKNOWN` volume is **not itself an optimization objective**. Most directories on a
real machine genuinely cannot be attributed from static evidence:

* a directory created by an application at first run, which no static source
  mentions;
* a shared runtime or framework tree, where `SHARED` is the correct answer;
* data named after a *different* product (recommendation stores, plugin caches,
  per-game profiles), where the relationship model expresses what is known and
  UNKNOWN expresses what is not;
* sequels, forks and renamed products, where a near miss is refused rather than
  guessed.

There is no per-directory creation record on disk: NTFS does not store which process
created a directory, ACL owners reflect the writing *user*, and the USN journal
records *that* something changed, not *which application* owns the result. For any
directory that no installed application registered, that contains no application
binary, and that is not inside a recognised structure, the information required to
attribute it does not exist on disk. The correct behaviour is to say `UNKNOWN` and
explain why.

---

## 12. Subtree closure and residual accounting

`AppTraceScanner` turns the per-directory attribution into an accounted tree:

* a directory whose ownership is **established** — `DecideOwnership` reports
  `OwnershipEstablished` for exactly one accepted owner whose score reaches 45, and
  never for a container or for several accepted owners — becomes one item owning its
  whole subtree, and the scan does not descend into it;
* a directory the scan **descends into** does not become an owner item; its children
  are itemised and the leftover becomes a single **residual** item on the parent;
* a scan root is never attributed (it is a container) but is still accounted for, so
  loose files and excluded subtrees cannot vanish from the totals;
* a container boundary is never established, so the scan always descends through it.

Overlapping *paths* are expected; overlapping *bytes* are impossible.

---

## 13. Measurement and reporting

Attribution and measurement are orthogonal: measurement completeness is never
attribution evidence (§3.1), and attribution never changes a size.

The accounting invariant, asserted by the test suite:

```text
sum(item.ExclusiveSizeBytes) == ScanResult.TotalMeasuredBytes
```

and every item's `ExclusiveSizeBytes <= MeasuredSizeBytes`.

`FootprintReport.Build` partitions the scan by `Classification` into four mutually
exclusive, exhaustive buckets that sum to `TotalMeasuredBytes` exactly:

| Bucket | Classifications |
| --- | --- |
| `ConfidentBytes` | `CONFIRMED`, `HIGH` |
| `PossibleBytes` | `MEDIUM`, `LOW` |
| `SharedBytes` | `SHARED`, `AMBIGUOUS` |
| `UnattributedBytes` | `UNKNOWN` |

Because the buckets are a partition of `Classification`, an item that stays UNKNOWN
while carrying an accepted candidate can no longer fall into no bucket at all. Every
bucket reconciles with the per-classification accounting.

The text report renders a `WHY` block per location directly from the evidence
records: supporting and contradicting records, each candidate's classification, and
the `StopReason` explaining why the scan did or did not close the directory. The JSON
output exposes the same records for structured inspection. The internal score is a
diagnostic and is **never** published as a percentage: there is no defensible
probabilistic model, so no percentage is offered.

---

## 14. Snapshot semantics

A scan is an **independent snapshot of the machine as it exists at that moment**.
Every verdict is derived from what the current scan observed and nothing else.

```text
Scan T1  →  filesystem / registry / provenance observed at T1  →  Report T1

             machine changes

Scan T2  →  filesystem / registry / provenance observed at T2  →  Report T2
```

The attribution engine must not use T1 as evidence for T2 — no remembered owner, no
"this was installed last time". Previous scans may be used *externally* to evaluate a
change, but they are comparison data, **not hidden ownership evidence**.

Historical observations may later become an explicit evidence source for orphan
detection, but that requires its own staleness, conflict and lifecycle semantics.
Until such a source exists, historical knowledge must never silently contaminate
current-state ownership.

When comparing two scans, an engine-caused classification change must be separated
from an underlying filesystem change: a path present in only one scan is a
machine-state difference and is never credited to the engine change.

---

## 15. Safety

The V2 core is **read-only**, and read-only is structural rather than a policy that
could be forgotten. The complete set of filesystem operations in `AppTrace.Core` is
enumeration and metadata reading:

```text
DirectoryInfo.GetFileSystemInfos()      enumerate
FileSystemInfo.Attributes               read attributes
FileInfo.Length                         read size
File.GetAttributes()                    read attributes
Directory.EnumerateFiles(..., "*.exe")  enumerate
FileVersionInfo.GetVersionInfo()        read version resource
```

There is no `Delete`, `Move`, `Create`, `Write`, `Rename`, `SetAttributes`, no
process launch, and no registry write (`OpenSubKey` is called with
`RegistryKeyPermissionCheck.ReadSubTree`; `CreateSubKey` and `SetValue` appear
nowhere). No CLI command performs remediation, and none is planned.

Additional guarantees:

* **Reparse points are never followed by default.** Following a junction inside
  AppData both double-counts and can loop forever. Each is measured as an opaque
  leaf, the decision is recorded as an informational `ScanError`, and
  `--follow-reparse-points` exists only to make the override explicit.
* **Inaccessible paths are reported, never faked.** Unreadable directories produce a
  `ScanError` with a stage, and the affected size is a floor, not a fact.
* **Budgets bound the work.** 120 000 directories, 4 000 000 files, 500 executable
  probes, and a configurable attribution depth (default 6). Budget exhaustion is
  surfaced as a warning, never swallowed.

---

## 16. Known limitations (deliberately unresolved)

These are **known limitations, not bugs**. Each needs a *new independent evidence
source* or a product decision; none is to be resolved by widening a name/path
heuristic.

* **Generic multi-application containers** such as `…\Local\Programs`, which hold
  many products' installs under one uninformative name. Attribution per product
  requires resolving each child on its own evidence; the container itself cannot be
  assigned.
* **Publisher roots supported only by weak or launcher evidence** — e.g. a directory
  some product registers as its install location at the publisher level. The
  `Program Files (x86)\Oxford University Press` case is `CONFIRMED` because the
  product's own registration names that publisher-level directory. Resolving it needs
  evidence that the registration is publisher-scoped, not a confidence adjustment;
  the regression corpus records it.
* **Products whose registry metadata explicitly declares a publisher-level root** —
  the same class, where the registry itself is the weak link, not the engine.
* **Platform/ecosystem roots such as Steam**, where filesystem ownership and
  user-facing product footprint are not the same concept: a directory containing
  installed applications rather than one application's own files can be attributed
  "correctly" and still mislead an application-centric report.
* **Application-data trees lacking current identity or provenance** — the dominant
  UNKNOWN cause. A data directory whose name corresponds to no discovered installed
  identity stays UNKNOWN rather than being guessed.
* **Package/MSIX identity not yet integrated** — `%LOCALAPPDATA%\Packages` is
  measured but not partitioned into per-package ownership.
* **A known display-name corruption** on one publisher's uninstall entry whose tail
  is not decodable text. Investigation showed the stored registry value itself is
  damaged, not the reader, so the fix would be publisher-specific string repair rather
  than a generic decoding fix; it is left as an open identity-cleanup item.

Adding a heuristic to close any of these requires a concrete, reproducible failure
family and a regression justification (§17). "It would attribute more bytes" is not
one.

---

## 17. Evaluation as the regression gate

Every future change to attribution is judged by
[`EVALUATION.md`](EVALUATION.md), which describes two fixtures and their mechanics:

* `attribution-corpus.json` — synthetic cases encoding real failure patterns,
  known-good cases and structural invariants. Deterministic and machine-independent
  (synthetic paths, executable probing disabled).
* `real-machine-labels.json` — hand-labelled real paths, used as a **field baseline,
  not a machine-independent hardcoded test**.
* `corpus-golden.json` — the frozen set of corpus cases not yet satisfied, so a
  changed outcome is detected whether it is a regression or an unrecorded
  improvement.

The core development rule:

> **New evidence may improve recall only if existing high-confidence precision does
> not regress.**

The headline metric is **wrong-owner HIGH/CONFIRMED claims**, which may only go down.
Expectations are expressed as **bounds** (`minClassification`), so `HIGH → MEDIUM`
is not a regression when the semantics justify it. Tests must never be made to pass
by silently rewriting expected outcomes.

The current automated expectations include at least:

```text
corpus wrong-owner HIGH/CONFIRMED:  0 / 4
Task 05 generic-token false-positive family:  still refused
DaVinci/Electron:  must not become Git-owned
```

Because the machine may change between scans, evaluation must keep distinguishing
engine-caused changes from machine-state changes (§14).

---

## 18. Heuristic freeze and the next evidence source

The path/name heuristic layer is treated as **mature**. Future recall improvements
should normally come from **new independent evidence sources**, not from broader
name/path guessing. A new heuristic after this milestone requires a concrete
reproducible failure family and regression justification.

The non-goals that follow from this — none of which the current engine implements —
are: Authenticode/signer attribution, MSIX/`PackageFamilyName` attribution,
Steam/Epic manifests, runtime observation (ETW/USN), historical attribution, orphan
detection, and any product-specific alias or path database. AppTrace is not a system
cleaner; deletion and cleanup are out of scope.

The recommended next evidence source is **MSIX/`PackageFamilyName` attribution**, or
alternatively **Authenticode signer evidence**: both are independent of path naming,
and both target the two largest known UNKNOWN families (unattributed per-product data
trees and publisher-level roots) without touching the heuristic layer.

---

## 19. Where truth and history live

| Document | Role |
| --- | --- |
| **`ATTRIBUTION_ENGINE_V2.md`** (this file) | Normative: current engine behaviour |
| [`EVALUATION.md`](EVALUATION.md) | Normative: the regression harness and how to add a case |
| [`ATTRIBUTION_HISTORY.md`](ATTRIBUTION_HISTORY.md) | Historical, non-normative: design spike and Task 03–07.7 "as built" |
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | Stack, repository layout, data model, measurement and safety |
| [`RESEARCH.md`](RESEARCH.md) | Historical: research notes on prior art |

When this document and history disagree, this document wins. When this document and
the code disagree, the code wins and this document is corrected.