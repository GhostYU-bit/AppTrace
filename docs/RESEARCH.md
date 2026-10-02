# AppTrace — Research Notes

> **Historical — not normative.** These are the spike notes taken while the
> technology and attribution approach were chosen. They record what the upstream
> projects do and which *concepts* were adopted or rejected at the time. The
> normative description of the engine's current behaviour is
> [`ATTRIBUTION_ENGINE_V2.md`](ATTRIBUTION_ENGINE_V2.md).

Everything below was read from the upstream sources at the commits that were
cloned locally, not from blog posts or summaries. Line numbers refer to those
checkouts.

| Project | Repository | License | Local checkout |
| --- | --- | --- | --- |
| Bulk Crap Uninstaller (BCU) | https://github.com/BCUninstaller/Bulk-Crap-Uninstaller | Apache-2.0 (`Licence.txt`) | `_research/bcu` |
| Jharu | https://github.com/riponcm/Jharu | Apache-2.0 (`LICENSE`) | `_research/jharu` |

Both are Apache-2.0. **No source code was copied into AppTrace.** The notes below
record *concepts* that were adopted, adapted, or deliberately rejected. Where
AppTrace uses a similar idea, the upstream file is cited so a reader can compare.
Attribution for the ideas is recorded in [`NOTICE`](../NOTICE).

---

## 1. Bulk Crap Uninstaller

BCU is a C#/WPF uninstaller whose junk-removal subsystem answers a question
adjacent to AppTrace's: *"is this filesystem or registry location left over from
an application we know about?"*

### 1.1 Confidence is a signed score that maps to a small enum

`source/UninstallTools/Junk/Confidence/ConfidenceLevel.cs` defines five levels
with explicit numeric values in ascending order of "safe to remove":

```text
Unknown = 0, Bad = 5, Questionable = 7, Good = 9, VeryGood = 12
```

`ConfidenceCollection.GetRawConfidence()` is a plain sum of the individual
`ConfidenceRecord.Change` values, and `GetConfidence()` buckets that sum:

```text
empty -> Unknown;  < 0 -> Bad;  < 2 -> Questionable;  < 5 -> Good;  else VeryGood
```

Files: `Junk/Confidence/ConfidenceCollection.cs` (`:24-46`),
`Junk/Confidence/ConfidenceRecord.cs` (`:8-38`).

**Adopted.** AppTrace uses the same shape: individual evidence records carry a
signed weight, the candidate's score is the sum, and a small documented rule set
maps evidence plus score onto a coarse classification. AppTrace never publishes
the number as a percentage, exactly as BCU never shows its raw sum.

**Adapted.** BCU's levels describe *removal safety*; AppTrace's describe
*ownership confidence*. They are different questions, so the enums are different
(`CONFIRMED / HIGH / MEDIUM / LOW / SHARED / AMBIGUOUS / UNKNOWN`) and the
thresholds are not comparable.

### 1.2 Evidence can subtract confidence

`Junk/Confidence/ConfidenceRecords.cs` (`:15-53`) is a table of named records
with explicit weights, and several are negative:

```text
IsUninstallerRegistryKey +20   ExplicitConnection +4    CompanyNameMatch +4
AllSubdirsMatched +4           IsEmptyFolder +4         ProductNamePerfectMatch +2
IsStoreApp -10                 DirectoryStillUsed -7    ProgramNameIsStillUsed -4
PublisherIsStillUsed -4        QuestionableDirectoryName -3
CompanyNameDidNotMatch -2      ItemNameEqualsCompanyName -2
UsedBySimilarNamedApp -2       DirectlyInsideKnownFolder -1
```

**Adopted.** This is the single most useful idea in BCU for AppTrace's purpose.
Evidence that *argues against* an attribution is first-class and machine
readable. AppTrace's `Evidence` record carries `SupportsAttribution`, and the
negative weights (`SharedPublisherDirectory -18`, `MultipleCandidateOwners -15`,
`ConflictingApplicationMatch -25`) are what turn a naive "best match wins" into
a defensible `SHARED`/`AMBIGUOUS` outcome.

`DirectoryStillUsed -7` (in `Junk/Finders/JunkCreatorBase.cs:42-45`) deserves
special mention: BCU penalises a candidate location when *another* installed
application's install location is a prefix of it. AppTrace implements the same
protective instinct as `ConflictingApplicationMatch` and resolves it during
candidate selection rather than as a post-hoc penalty.

### 1.3 Name matching is fuzzy and forgiving; AppTrace is not

`Junk/Confidence/ConfidenceGenerators.cs` (`:58-99`) matches a directory name to
a product name with the Sift4 string-distance algorithm: distance ≤ 1 is a
"perfect" match, mutual substring containment is a "dodgy" match, and anything
below `shortestLength / 3` is accepted as a match. `TestForSimilarNames`
(`:113-137`) then penalises every match that is not the best one.

**Deliberately rejected.** AppTrace inverts this trade-off. A fuzzy match at
Sift4 distance 1 between two short product names is exactly how unrelated user
data gets confidently assigned to an application. AppTrace uses exact folded
equality plus whole-word, end-anchored overlap — and *nothing else* — so the
model can never claim a directory because two names look similar. This is a
deliberate false-negative budget (see section 4 of the task: "prefer false
negatives over false positives").

The one part worth keeping is the *idea* behind `UsedBySimilarNamedApp`: when
several applications match a name, do not silently pick the best. AppTrace raises
the match itself as ambiguous and withholds the strong identity signal
(`AttributionEngine.CollectNameEvidence`, "only a unique match may act as a
strong identity signal").

### 1.4 A junk result holds exactly one owner and no size

There is no `JunkResult` class. `Junk/Containers/IJunkResult.cs` and
`JunkResultBase.cs` (`:26-28`) hold a single `ApplicationUninstallerEntry
Application`, an `IJunkCreator Source`, and a `ConfidenceCollection`. A grep for
`FileSize|EstimatedSize|Size` under `UninstallTools/Junk` returns nothing:
**BCU's junk results carry no size at all**. Explanation is
`ToLongString()` (`:35-40`), a formatted string that concatenates the application,
the level and the location.

**Deliberately rejected.** AppTrace cannot follow this. The product question is
"what is the real disk footprint", so sizes are central, and a location
frequently has *several* legitimate owners. `FootprintItem` therefore holds a
list of `CandidateOwner`, each with its own evidence, and the size is a property
of the location rather than of the relationship. Likewise, no explanation string
is stored anywhere: `TextReporter` renders the "WHY" block from the evidence
records, so CLI, JSON and any future UI cannot drift apart.

### 1.5 Application discovery and identity normalization

`Factory/RegistryFactory.cs` (`:284-305`) reads `HKLM` and `HKCU`
`SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` plus the `Wow6432Node`
variants on 64-bit processes. Values captured (`:23-40`, `:128-147`) include
`DisplayName`, `DisplayVersion`, `Publisher`, `UninstallString`,
`QuietUninstallString`, `ModifyPath`, `InstallLocation`, `InstallSource`,
`EstimatedSize`, `InstallDate`, `DisplayIcon`, `ParentKeyName`,
`SystemComponent`, `NoRemove`, `WindowsInstaller`, `BundleProviderKey`.

Normalization (`ApplicationUninstallerEntry.cs:79-94`,
`KlocTools/Tools/StringTools.cs:144-182`) strips trailing parenthesised groups,
trailing digits/dots/dashes, a trailing `" v"`, and suffixes such as
`Application`, `Helper`, `CE`. `PublisherTrimmed` drops `(R)` and trailing
`corp/corporation/limited/inc/incorporated`.

**Adopted.** The registry roots and the value set. AppTrace reads
`DisplayName`, `Publisher`, `DisplayVersion`, `InstallLocation`, `DisplayIcon`,
`UninstallString` and adds an MSIX/AppX pass.

**Adopted, extended.** Publisher normalization: AppTrace strips a wider set of
legal forms (including `GmbH`, `S.A.`, `BV`, `Pty`, `S.p.A.`) because a publisher
namespace match is a load-bearing signal for it.

**Partly rejected.** BCU merges duplicate entries with a weighted scoring
function (`ApplicationEntryTools.AreEntriesRelated`, `:32-97`, including fuzzy
`Sift4` comparison). AppTrace instead keys on
`(normalized name, normalized publisher)` and keeps the richer of two entries.
Deterministic and easy to reason about; the loss is that two genuinely different
registrations of the same product stay separate, which is a false negative
AppTrace accepts.

### 1.6 Orphan detection is name-and-publisher containment

`Junk/ProgramFilesOrphans.cs` walks Program Files and asks, for each subdirectory,
whether it is claimed by `ContainsAny` over the set of other install locations,
application names and publishers (`:56`, `:61`, `:96`, `:130-139`), all filtered
to length > 3. Content heuristics add `ExecutablesArePresent -4`,
`FilesArePresent 0`, `IsEmptyFolder +4` and `MoreThan100Files -2`
(`:63-79`, `:105`).

**Adopted as a concept.** "No installed application claims this by name,
publisher or install location" is exactly AppTrace's `UNKNOWN` outcome, and the
empty-directory signal is a genuinely useful leftover hint.

**Deliberately rejected for Phase 0.** The *empty/unclaimed directory* conclusion
is a leftover-cleanup judgement, and AppTrace Phase 0 is read-only attribution.
`UnattributedReason` records why a location stayed `UNKNOWN`, and nothing more is
inferred from it. Also rejected: naive substring `ContainsAny` over publishers,
which is the mechanism behind vendor-directory false positives.

**Notable.** BCU does *not* use executable version metadata
(`CompanyName`/`ProductName`/`FileDescription`) for junk matching. It is used
only when creating entries (`InfoAdders/ExecutableAttributeExtractor.cs:62-79`)
and for startup entries. AppTrace does use it, as a bounded, cached probe
(`Attribution/ExecutableProbe.cs`), because it is the only content-level signal
available to a read-only tool that has no uninstall entry to work from.

### 1.7 Size calculation — and the reparse point gap

`InfoAdders/FastSizeGenerator.cs` prefers a bundled Everything CLI
(`es.exe -size ...`, 40 s timeout, `:82-95`) and falls back to
`Scripting.FileSystemObject.GetFolder(...).Size` (`:67-79`). Parallelism is
per-entry via `ThreadedWorkSpreader` (`MaxThreadsPerDrive = 2`,
`Factory/FactoryThreadedHelpers.cs:19,52-86`), bucketed by physical disk.

**Rejected.** Anything that shells out to a third-party indexer. A read-only tool
that depends on an external binary cannot be trusted to report honestly when that
binary is absent.

**Critical gap, fixed in AppTrace.** The review found **no reparse point or
junction handling anywhere in BCU**: directory enumeration is plain
`GetFiles`/`GetDirectories` with `SearchOption.AllDirectories`
(e.g. `ProgramFilesOrphans.cs:63`), and `IsSystemDirectory` inspects
`FileAttributes.System` but never `FileAttributes.ReparsePoint`
(`UninstallToolsGlobalConfig.cs:268-299`). On a real Windows profile this means
the well-known junctions inside AppData can be followed, double counting
gigabytes and risking traversal loops. AppTrace treats every reparse point as an
opaque leaf and reports it (`Scanning/DirectoryWalker.cs`). This is a
correctness requirement, not a nicety.

### 1.8 Safety classification

`UninstallToolsGlobalConfig.cs` (`:48-54`) blacklists directory *names*
(`Microsoft`, `Temp`, `Programs`, `Common Files`, `Clients`, `Downloads`,
`Windows`, `winsxs`, `WindowsApps`, `Installer`, `DirectX`, …), treats anything
under the Windows directory or under Downloads as a system directory
(`:268-299`), and flags `install, settings, config, configuration, users, data`
as "questionable" (`:31-34`). Deletion goes to the recycle bin
(`Junk/Containers/FileSystemJunk.cs:29-36`) and `Backup()` is a deliberate no-op.

**Adopted, reframed.** The directory-name blacklist becomes AppTrace's
`GenericDirectoryNames` list, and its role changes from "do not delete this" to
"this name may not be used as ownership evidence". `Windows`, `Programs`,
`Common Files`, `Installer`, `Packages` and friends are all in AppTrace's list.

**Rejected.** Recycle-bin deletion, low-confidence confirmation dialogs, and
protected-app flags. AppTrace Phase 0 has no removal path at all, so there is
nothing to gate.

---

## 2. Jharu

Jharu is a Rust + Tauri 2 disk cleaner for developers (macOS and Windows). Its
Rust backend is in `src-tauri/src/`. It is a *cleaner*, not an attribution tool,
and the review confirmed the repository's `docs/` directory contains only
screenshots, so the source and its comments are the only documentation.

### 2.1 Traversal: `jwalk`, deliberately not nested

Everything walks with `jwalk::WalkDir` (rayon-backed). `scanner.rs:124-127`
explains the design choice in a comment worth repeating:

> jwalk already parallelises each walk across rayon's global pool, so spawning a
> thread per rule nests two layers of parallelism and starves that pool — walks
> then return empty and their findings silently disappear.

**Adopted as a principle.** AppTrace's Phase 0 walker is sequential and
single-pass, with an explicit directory and file budget, and the decision is
documented rather than accidental. The specific failure mode (silently empty
results from pool starvation) is the reason performance work is deferred rather
than guessed at.

**Notable gaps.** Walks are unbounded and uncancellable; `max_depth` appears only
in `python.rs`. `top_children` (`scanner.rs:103-121`) re-walks every child, so a
finding costs O(children × subtree). AppTrace measures each directory exactly
once and caches the result (`DirectoryMeasurement`), which is what makes the
partitioning pass affordable.

### 2.2 The best idea in Jharu: a size is a floor, not a fact

`scanner.rs:40-48` documents `read_error` explicitly:

> Set when the directory exists but could not be fully read. Without this an
> unreadable cache would silently vanish from the results instead of telling the
> user that data is there but out of reach.

`measure_dir` keeps the first `read_children_error` from an otherwise-`Ok` entry
(`:66-68`) so an unreadable folder reports as a floor rather than as 0 bytes.

**Adopted, and made a first-class output.** AppTrace's `ScanError` records carry
path, severity and stage, are surfaced in the text report ("the scan could not
read everything"), and appear in full in `--json`. The scan result exposes
`Truncated`, and excluded subtrees are still *measured* so excluding them cannot
shrink the totals.

By contrast `apps.rs:28-40` and `lib.rs:57-69` swallow errors with `.flatten()`.
AppTrace follows the careful modules, not the loose ones.

### 2.3 Reliability flags as a licence not to make a claim

`models.rs:10-33` carries `ModelEntry { size_bytes, exclusive_bytes, paths, …,
note }` and a report-level `usage_tracking_reliable` flag, "which prevents a
'never used' claim when atime is untracked" (`models.rs:337-346`). Ollama blobs
are refcounted so shared layers are excluded from `exclusive_bytes` and the gap
is explained in `note` (`models.rs:196-248`).

**Adopted — this is the single most transferable idea in Jharu.**
`exclusive_bytes` is the right primitive for a tool that must not double count,
and AppTrace's `FootprintItem.ExclusiveSizeBytes` follows it: the sum over all
items equals the total the scan measured, and `FootprintAccountingTests` asserts
that invariant. The reliability-flag idea becomes `Classification.Unknown` plus
`UnattributedReason`: AppTrace states that it does not know, rather than
publishing a confident zero.

### 2.4 Windows application discovery is thin

`apps.rs:166-170` reads only the three Uninstall keys. Per subkey it skips
`SystemComponent == 1`, any key with `ParentKeyName`, and duplicate lowercased
names (`:183-193`). It captures `DisplayVersion`, `UninstallString`,
`InstallLocation` and `EstimatedSize` — but **no `Publisher` at all**, no
`InstallDate`, no `DisplayIcon`, no `App Paths`. Identity is one folded string
(`:68-70`), and matching is substring containment gated on
`normalized_name.len() >= 5` (`:96-107`).

**Adopted.** Skipping `SystemComponent` entries: AppTrace's MSIX pass skips
Windows' own AppX components for the same reason (plus unresolved
`@{ms-resource://…}` display names, which appear as literal garbage otherwise).

**Rejected.** Capturing no publisher is fatal for AppTrace, whose decisive
disambiguation for vendor directories is the publisher namespace. And the
`len >= 5` substring gate is the mechanism behind the false positive
`"Code" matches VSCode and Code Cache` noted in that report — AppTrace uses
whole-word, end-anchored overlap with a minimum length of 3 instead.

### 2.5 App-leftover detection is a single boolean

The premise that `junk.rs` performs leftover detection is wrong: `junk.rs` scans
fixed locations by extension, size and age. App-leftover logic is
`apps.rs:359-383`, returning `LeftoverEntry { label, path, size_bytes, confident }`
where `confident` is documented as "true when matched by bundle id (high
confidence) vs name (lower)". On Windows `bundle_id` is always `None`
(`apps.rs:225`), so **`confident` is always `false` there** and the UI's
auto-selection never fires.

**Deliberately rejected.** A single boolean is not an explanation, and the task
requires the opposite: the user must be able to ask *why*. AppTrace carries
structured evidence, and the CLI renders a WHY block per location. The 1-level
`AppData/Roaming` + `AppData/Local` index (`apps.rs:55-59`, `:79-94`) is also
rejected — no `ProgramData`, no nesting, so a vendor directory can never be
resolved down to its products.

### 2.6 Rules are hard-coded paths; "matching" never happens

`rules.rs:23-29` defines `Rule { id, name, description, category, safety }` with
no patterns, globs or environment expansion. `Rule::paths()` (`:34-149`) is a
`match self.id` returning hard-coded `home.join(...)` values, and the scanner only
checks `path.exists()` (`scanner.rs:133`).

**Adopted, narrowed.** A small declarative idea is good: AppTrace has
`GenericDirectoryNames` and `EvidenceWeights` as explicit tables that are read
by the engine and can be extended by adding a row. This satisfies "design the
evidence system so additional detectors can be added later" without a plugin
framework.

**Rejected.** A 25-entry hard-coded application/path database. The task
explicitly forbids building a large hard-coded application database in Phase 0,
and AppTrace's whole point is a *generic* attribution model.

### 2.7 The trust boundary is in the frontend — a lesson in what not to do

`lib.rs:31-50`: `clean_paths` accepts `paths: Vec<String>` from the webview and
calls `trash::delete()` with no allowlist, denylist, root confinement or
protected-path check. `deep.rs:59-114` computes a `protected: bool`, but it is
enforced only by hiding a button in `DeepClean.tsx:254` / `Treemap.tsx:94`.

**Rejected.** AppTrace's safety property is structural, not advisory: **no
command in the CLI removes, writes, moves or renames anything**, so there is no
path from the reporting layer to the filesystem. The guarantee lives in the
smallest possible surface (a read-only scanner) rather than in a check somebody
could forget.

---

## 3. Summary of what AppTrace took and left

| Idea | Source | Status in AppTrace |
| --- | --- | --- |
| Signed evidence score with explicit weights | BCU `ConfidenceRecords.cs` | Adopted (`Attribution/EvidenceWeights.cs`) |
| Evidence that subtracts confidence | BCU `ConfidenceCollection` | Adopted (`Evidence.SupportsAttribution`) |
| Coarse classification instead of a percentage | BCU `ConfidenceLevel` | Adopted (`Model/Classification.cs`) |
| Generic/"questionable" directory names | BCU `UninstallToolsGlobalConfig.cs` | Adopted and widened (`Attribution/GenericDirectoryNames.cs`) |
| "Another app already uses this path" penalty | BCU `JunkCreatorBase.cs:42-45` | Adopted as `ConflictingApplicationMatch` |
| Registry roots and uninstall value set | BCU `RegistryFactory.cs` | Adopted |
| Fuzzy name matching (Sift4) | BCU `ConfidenceGenerators.cs` | **Rejected** — too many false positives |
| Single-owner result with no size | BCU `JunkResultBase` | **Rejected** — sizes and multiple owners are the point |
| Explanation as a formatted string | BCU `ToLongString()` | **Rejected** — rendered from evidence instead |
| "A size is a floor, not a fact" | Jharu `scanner.rs:40-48` | Adopted (`ScanError`, `ScanResult.Truncated`) |
| Explicit exclusive bytes, refcounted sharing | Jharu `models.rs:196-248` | Adopted (`FootprintItem.ExclusiveSizeBytes`) |
| Reliability flag as a licence not to claim | Jharu `models.rs:30-31` | Adopted (`Unknown` + `UnattributedReason`) |
| Sequential walk, no nested parallelism | Jharu `scanner.rs:124-127` | Adopted |
| Trust boundary enforced in the UI | Jharu `clean_paths` | **Rejected** — read-only by construction |
| Reparse points/junctions unhandled | BCU and Jharu both | **Fixed in AppTrace** (`DirectoryWalker`) |
| Hard-coded application database | Jharu `rules.rs` | **Rejected** — generic model only |

## 4. Reproducing this research

```powershell
git clone --depth 1 https://github.com/BCUninstaller/Bulk-Crap-Uninstaller.git
git clone --depth 1 https://github.com/riponcm/Jharu.git
```

Both licences are Apache-2.0. If AppTrace ever copies a code fragment rather than
an idea, the fragment must carry its upstream notice; see [`NOTICE`](../NOTICE).
