# AppTrace — Attribution Evaluation Harness

How AppTrace measures whether an attribution change made the engine **more
correct** or merely **different**.

This is the practical companion to
[`ATTRIBUTION_ENGINE_V2.md`](ATTRIBUTION_ENGINE_V2.md) §17. It exists so that any
future change to the attribution engine can be answered with evidence:

> Which known cases did this fix, which did it break, and did the number of
> confidently wrong ownership claims go up or down?

---

## The two fixtures

| File | What it is | What it measures |
| --- | --- | --- |
| [`attribution-corpus.json`](../tests/AppTrace.Core.Tests/Evaluation/attribution-corpus.json) | 26 synthetic cases: real failure patterns, known-good cases, and structural invariants | Rule-level behaviour. Deterministic and machine-independent. |
| [`real-machine-labels.json`](../tests/AppTrace.Core.Tests/Evaluation/real-machine-labels.json) | 34 hand-labelled real paths from one Windows machine | Real-world owner precision. |
| [`corpus-golden.json`](../tests/AppTrace.Core.Tests/Evaluation/corpus-golden.json) | The frozen set of corpus cases that are not yet satisfied | Detects both regressions and unrecorded improvements. |

Synthetic **paths** in the corpus do not exist on disk, and the engine's
executable probe is disabled in both fixtures. A verdict can therefore never
depend on what is installed on the machine running the tests.

---

## Running it

```powershell
# everything
dotnet run --project tests/AppTrace.Core.Tests

# just the harnesses
dotnet run --project tests/AppTrace.Core.Tests -- --filter-namespace "*Evaluation*"

# just the corpus, or just the real-machine set
dotnet run --project tests/AppTrace.Core.Tests -- --filter-class "*AttributionCorpusTests*"
dotnet run --project tests/AppTrace.Core.Tests -- --filter-class "*RealMachineEvaluationTests*"
```

Two reports are written next to the test binaries:

```text
tests/AppTrace.Core.Tests/bin/<Config>/net9.0-windows/evaluation/corpus-report.txt
tests/AppTrace.Core.Tests/bin/<Config>/net9.0-windows/evaluation/real-machine-report.txt
```

Each report leads with the number that matters:

```text
*** WRONG-OWNER HIGH/CONFIRMED CLAIMS: 11 of 20 confident claims ***
```

followed by owner precision per classification the engine actually produced, and
a per-case block showing `current` vs `expected` with the evidence behind the
verdict.

---

## How an expectation is expressed

A corpus case separates three things that used to be conflated:

```jsonc
{
  "id": "GOOD-VIVALDI",
  "path": "...",
  "apps": ["app-vivaldi"],
  "reason": "why this case exists",

  // What the engine should eventually do. Reported, never asserted,
  // until the relevant V2 task lands.
  "desired": {
    "owner": "Vivaldi",
    "minClassification": "Medium"     // or allowedClassifications: ["Shared", ...]
  },

  // What the engine does today. Asserted, so a change cannot pass unnoticed.
  "current": {
    "owner": "Vivaldi",
    "classification": "Medium",
    "supportingEvidence": ["ExactDirectoryNameMatch", "KnownApplicationPath"]
  }
}
```

**Expectations are bounds, not labels.** `minClassification: "Medium"` accepts
MEDIUM, HIGH or CONFIRMED. This is deliberate, following the correction in
Task 03: if V2 decides that static evidence no longer justifies HIGH, then
`HIGH → MEDIUM` is **not** a regression. Encode a bound unless the semantics
genuinely require exactly one label, and use `allowedClassifications` for the two
structural outcomes (`Shared`, `Ambiguous`) which are not points on the
strength ladder at all.

---

## Adding a new case

Real-world cases belong in the corpus, not in code. A case that is not a fixture
file will be forgotten; a case in the fixture file is run on every build.

### Adding a corpus case

1. Add any application identities the case needs to the top-level `apps` array.
   Give them the real display name, publisher and install location — the engine
   derives install-location evidence from those fields, so a wrong value tests
   the wrong thing.
2. Add the case with `id`, `description`, `path`, `category`, `apps`, `reason`,
   and a `desired` block.
3. Run the harness. It will fail with the engine's actual behaviour for your case.
4. Record that behaviour in the `current` block **exactly as reported**. This is a
   baseline, not an aspiration — copy it, do not adjust it.
5. Add the case id to `corpus-golden.json` under `unresolvedCases` if the
   `desired` outcome is not met yet.
6. Run again and confirm everything is green.

If the case makes the engine *wrong* exactly like a real defect, that is a
successful addition — it is now protected by the suite.

### Adding a real-machine case

1. Confirm the label **independently of the engine**. Acceptable sources: the
   uninstall registry, the directory's own contents, executable version metadata,
   a shortcut target, the installer path. The engine's own output may be used to
   *find* interesting locations, never to decide the answer.
2. Copy the application identities from the scan so the engine sees what a real
   run sees.
3. Write `owner` (`null` when no installed application owns the path),
   `minClassification` or `allowedClassifications`, `confidence` (`high`/`medium`)
   and `reasoning`.
4. If you cannot justify the label, **do not guess**. Set
   `"label": "uncertain"` and explain what is missing. Uncertain cases are
   reported under "cases needing human review" and are excluded from the metrics,
   because a guessed label corrupts the one number this harness protects.

`reasoning` is validated: it must be a real sentence, not a placeholder. A label
without a stated justification is an opinion, not ground truth.

---

## Reading the metrics

| Metric | Meaning |
| --- | --- |
| **Wrong-owner HIGH/CONFIRMED claims** | **The headline.** Confident claims the label says are wrong. May only go down. |
| correct owner | The engine named the labelled owner |
| wrong owner | The engine named a different installed application |
| correct UNKNOWN | The engine correctly refused to attribute |
| false positive | Labelled "no owner", engine named one |
| false negative | Labelled with an owner, engine named none |
| correct owner, confidence differs | Right owner, different strength — acceptable, tracked separately |
| owner precision by classification | Of claims at each level the engine produced, how many were right |

There is deliberately **no single accuracy score**. A wrong HIGH is a different
event in kind from an honest UNKNOWN, and averaging them would hide exactly the
failure this project refuses to ship.

### Human-labelled ground truth

The real-machine metrics count only cases labelled `reviewed`. Cases labelled
`uncertain` are printed with their open question and excluded. The distribution of
`confidence` is reported so a reader can tell how much of the set rests on
high-confidence human judgement.

---

## Current baseline

Measured after Task 07.7 (Single-Product Vendor Namespace Hardening).

```text
Corpus
  total cases                           26
  desired outcome met                   23
  wrong-owner HIGH/CONFIRMED claims      0 of 4 confident claims
  unsupported ownership claims           1   (the Oxford publisher root)
  correct UNKNOWN / refusals            12
  relationship expectations met          2 of 2

Real machine (hand-labelled)
  total cases                           34   (29 reviewed, 5 need review)
  desired outcome met                   27 of 29
  wrong-owner HIGH/CONFIRMED claims      1 of 15 confident claims
  correct owner                         20
  correct UNKNOWN                        7
  unsupported ownership claims           1   (the Oxford publisher root)
  false negatives                        1   (a Steam soundtrack path)
```

These two sets are **deliberately independent of live provenance**. Both are
evaluated through fixtures with executable probing disabled and no provenance index,
so their numbers cannot depend on which registrations happen to exist on the machine
running them. That is what makes them a regression harness rather than a machine
snapshot.

Task 07's effect shows up in a **live scan**, which is what it is for:

```text
Live scan of %LOCALAPPDATA% (198 applications discovered)

  Vivaldi profile directory   -> HIGH   (DisplayIconMatch + 3 anchors)
  JDownloader 2               -> CONFIRMED, previously unattributed
  unknown/unattributed        -> unchanged, no owner lost
  all seven Task 05 refusals  -> still refused
```

Provenance yield for that machine, and the number that matters:

```text
DisplayIcon      198 records -> 76 anchors,  99 resource-only or unresolvable
App Paths         92 records -> 27 anchors,  12 malformed
Services         798 with ImagePath -> 56 non-system -> 14 anchors
Scheduled tasks  212 tasks, 98 Exec actions   (source unavailable under the
                                               sandbox these tests ran in)
Run keys          20 records -> 10 anchors
Shortcuts        277 found -> 272 resolved -> 127 anchors

208 registrations resolved to a path and linked to no application.
```

The last line is the honest one. **Discovery volume is not attribution success**: the
only meaningful figure is how many anchors could be *defensibly* linked to an
installed application, and most registrations on a real machine cannot be.

### Why a fixture harness and a live scan disagree

They measure different things. The fixture sets answer "is the attribution logic
right?" against fixed inputs, so a Task 07 source cannot move them without changing
the logic — which is exactly the property that makes them useful. A live scan answers
"what does AppTrace find on this machine?", where provenance genuinely adds owners.
Both numbers are reported because neither alone is the whole picture.

### Task 07.5 field evaluation

A read-only full-machine evaluation carried out between Task 07 and Task 07.6 changed
no code and is **not** a baseline in the table above, because it was a live scan rather
than a fixture run. Its nine findings — uneven install-versus-data-root attribution,
missing candidate generation as the dominant UNKNOWN cause, no near-threshold or
semantic-suppression misses, the vendor-root takeover defect, correlated-provenance
amplification, the reporting reconciliation defect, the display-name corruption, and
the shared storefront root — are recorded in
[ATTRIBUTION_HISTORY.md](ATTRIBUTION_HISTORY.md) together with the Task 07.6
corrections they motivated.

### Task 09 signer field evaluation

Task 09 added `SignerPublisherMatch` (embedded Authenticode publisher, `Identity`,
+25). It leaves the two fixture baselines above **unchanged**, by construction: both
run with executable probing disabled, so no signer is ever read there. The gates were
re-measured afterwards and are identical:

```text
corpus wrong-owner HIGH/CONFIRMED       0 of 4   (unchanged)
real-machine wrong-owner HIGH/CONFIRMED 1 of 15  (unchanged)
```

A read-only field probe over the executables the bounded probe would actually visit
(`Program Files`, `Program Files (x86)`, `Local\Programs`) measured:

```text
candidate executables                     125
embedded signer observed                  107  (86%)
signer publisher shared by >1 installed
  product (signer alone cannot choose)     32
agreement with the directory's registered
  application publisher                  11 agree / 29 disagree / 67 no app claim
cost per read                             ~2.4 ms warm, ~7 ms cold
```

The disagreements are the expected ones and are *correct* behaviour: a localized
publisher name (`北京春田知韵科技有限公司` versus the registry's English name), a
subset (`Stichting Blender Foundation` versus `Blender Foundation`), and an
individual signer (`Johannes Schindelin` for Git, whose registered publisher is
`The Git Development Community`). Strict normalized equality deliberately refuses all
three rather than bridging them — the source under-claims by design.

A/B evaluation of 424 real directories with the signer stripped from the same probe
versus present:

```text
signer records emitted                   22
directories whose verdict changed         0
```

No ownership or classification changed, which is the expected result for a source that
is barred from establishing ownership and from choosing among same-publisher products.
Task 09 is therefore justified by **explainable corroboration**, not by recall.

### Task 10 package identity field evaluation

Task 10 added Windows package identity (ATTRIBUTION_ENGINE_V2.md §3.7). Unlike the
signer, this source **may establish ownership**, so it moves what the gates are
evaluated against rather than leaving them alone. The two fixture baselines were
re-measured afterwards and are unchanged:

```text
corpus wrong-owner HIGH/CONFIRMED       0 of 4    (unchanged)
real-machine wrong-owner HIGH/CONFIRMED 1 of 15   (unchanged)
```

The two fixture sets cannot see the new source either: both run with executable
probing disabled and no provenance index, and neither fixture declares package family
names, so no package data root is ever generated there. Package behaviour is covered
instead by `PackageEvidenceTests`, which is machine-independent in the same way.

A read-only A/B on one machine — `apptrace scan --filter "AppData\Local"`, release
build, executable probing **enabled**, so this is a live-machine comparison and not a
fixture run:

```text
                                        Task 09     Task 10
applications discovered                      43          83
  of which MSIX packages                      0          40
locations                                21 892      22 860
scan.durationSeconds                     21.802      22.068
confident bytes (CONFIRMED + HIGH)      5.95 GB     13.18 GB
shared / ambiguous bytes                6.95 GB      6.95 GB
unattributed bytes                     22.93 GB     18.83 GB
```

Package identities and the namespaces they govern:

```text
package registrations read                        47
  kept as their own identity                      40
  reconciled into a classic record                 7
%LOCALAPPDATA%\Packages locations                 967     4.87 GB
  attributed from package identity                 44     3.82 GB
  left UNKNOWN (frameworks, unresolved names)     923     1.05 GB
owners per package data root                        1
```

The attributed half is the point of the task: Windows names the owner, so no inference
is involved. Representative examples that were `UNKNOWN` before:

```text
Microsoft Teams            1 579.0 MB   MSTeams_8wekyb3d8bbwe
Slack                      1 111.9 MB   com.tinyspeck.slackdesktop_8yrtsj140pw4g
ChatGPT                      982.1 MB   OpenAI.Codex_2p2nqsd0c76g0
Armoury Crate                117.7 MB
Windows Web Experience Pack  103.6 MB
```

The unattributed half is deliberately *not* zero: those are data roots of framework
packages, which are excluded at read time, and of packages whose display name Windows
stores as a resource reference. Neither is guessed into a product.

**Reconciliation was not optional, and this is the finding that changed the design.**
A package and an uninstall entry can describe one user-facing application without
agreeing on its name. On this machine the MSIX package `OneDrive` and the uninstall
entry `Microsoft OneDrive` declared the same versioned install root
(`C:\Program Files\Microsoft OneDrive\26.168.0830.0006`), and a merge rule that
required equal display names left both in the application list. The duplicate then broke
attribution *backwards*: with two applications matching the directory name `OneDrive`,
the uniqueness rule (§5.3 of the engine document) suppressed the name match for **both**,
and the application's data tree fell out of attribution.

```text
OneDrive-owned bytes
  Task 09 baseline                              2.22 GB
  name-equality merge only                      1.36 GB     <- regression
  with the structural merge rule                2.22 GB     <- restored

locations whose accepted-owner set changed, Task 09 -> Task 10
  name-equality merge only                        29
  with the structural merge rule                   4         all explained below

shared / ambiguous bytes
  Task 09 baseline                              6.95 GB
  name-equality merge only                      6.09 GB
  with the structural merge rule                6.95 GB
```

The rule that replaced name equality is structural, not a name heuristic: the classic
record's own `DisplayIcon` / `UninstallString` resolves to a file **inside** the package
root, so the two registrations name one installed payload — and it requires exactly one
classic candidate, so an ambiguous pair stays unresolved.

The four remaining owner-set changes were inspected one by one:

```text
LocalLow\Intel\ShaderCache                  lost 3 Intel owners (absent from the
                                            registry in the later scan) - machine state
Local\Microsoft\PowerShell                  gained PowerShell   (newly attributable)
Local\Microsoft\Windows\PowerShell          gained PowerShell   (newly attributable)
Local\Microsoft\PowerShell\7.6.6            gained 15 co-owners (SHARED, not confident)
```

No location lost a *correct* owner to package identity, and no verdict that the
wrong-owner metric counts changed. Machine causes are separated from engine causes as
this document requires; a path present in only one scan is never credited to the change
under evaluation.

Refused and deliberately ambiguous cases, all of which stayed `UNKNOWN` or unresolved:

```text
a package data root with no registered package            no candidate is invented
framework packages (Framework = 1)                        excluded, namespace UNKNOWN
package display name stored as @{...} / ms-resource:...    identity kept, name unresolved
two classic records naming payload in one package root     package kept separate
package root under %WINDIR%                               no install location recorded
```

**Runtime cost.** The two extra registration surfaces are read once during discovery and
folded into an exact-match dictionary, so attributing 967 package locations costs 967
dictionary lookups. Measured wall time moved from 21.802 s to 22.068 s (+1.2%) across
one pair of runs; an earlier pair read 23.696 s (+8.7%), which did not reproduce and is
run-to-run variance on a live machine rather than a cost of the source.

---

## Scans are current-state snapshots

A scan is an **independent snapshot of the machine as it exists at that moment**:

```text
Scan T1  ->  filesystem / registry / provenance observed at T1  ->  Report T1

             machine changes

Scan T2  ->  filesystem / registry / provenance observed at T2  ->  Report T2
```

The attribution engine must not use T1 as evidence for T2. Every verdict is derived
from what the current scan observed and nothing else — no remembered owner, no
"this was installed last time". Previous scans may be used *externally* to evaluate a
change, but they are **comparison data, not hidden ownership evidence**.

## Historical scans are future scope

Historical scan data is not part of the current model, and no task so far has
introduced it.

> Historical scan data, if introduced later, must be modeled as an explicit
> provenance source with its own staleness, conflict, and lifecycle semantics.

The shape it would eventually serve is easy to picture — an application uninstalled
between two scans whose directory remains is an obvious orphan candidate:

```text
T1:  App X installed,  directory X exists
T2:  App X uninstalled, directory X remains
```

Recognising that reliably needs a defined lifetime for the observation, a defined
answer when the two scans disagree, and a defined meaning for a stale row belonging to
something no longer installed. Until those exist, historical knowledge must never
silently contaminate current-state ownership.

## Comparing two scans honestly

The machine may change between scans — the user may be actively freeing disk space —
so a before/after comparison has to separate two different causes:

```text
engine-caused classification change        <- what the change is being judged on
underlying filesystem population change    <- what the machine did on its own
```

Do not credit an engine change merely because `UNKNOWN` bytes fell, total bytes fell,
or a directory disappeared. When a path exists in only one of the two scans, report it
as a **machine-state difference** and do not attribute it to the change under
evaluation. The honest reading is narrower: for paths present in *both* scans, did the
verdict change, and did it become more or less correct?

---

## What this harness does not do

* It does not change attribution behaviour. During Task 03 the production code was
  not modified at all.
* It does not snapshot whole scans or maintain a golden output file per run.
* It does not treat filesystem-measurement completeness (inaccessible files,
  skipped reparse points, lower-bound sizes) as attribution evidence. Those belong
  to measurement quality and are deliberately not part of any attribution score.

## Relationship expectations

A case that expects `RelatedTo` names both the relation and the other application:

```json
"desired": {
  "owner": "Owner App",
  "minClassification": "Medium",
  "relation": "RelatedTo",
  "relatedApplication": "app-relatedproduct"
}
```

`owner` owns the location; `relatedApplication` is what the content is about. Both
are required, because a relation without a named counterparty is not checkable.

The check runs in both directions, which is the part that matters:

* a case that expects a relationship must have it reported;
* **a case that expects none must have none invented.**

That second half is what keeps the model honest. `REL-NO-OWNER-NO-RELATION` exists
solely to assert it: with no accepted owner there is nothing for a related
application to be relative to, so the answer stays `UNKNOWN`.

Note that a related application is never counted in `owners`, never classified, and
never sized. Relationship expectations are reported separately from owner
precision, so a relationship can never be mistaken for an attribution.
