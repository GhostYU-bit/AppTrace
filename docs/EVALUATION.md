# AppTrace — Attribution Evaluation Harness

How AppTrace measures whether an attribution change made the engine **more
correct** or merely **different**.

This is the practical companion to
[`ATTRIBUTION_ENGINE_V2.md`](ATTRIBUTION_ENGINE_V2.md) §J. It exists so that any
future change to the attribution engine can be answered with evidence:

> Which known cases did this fix, which did it break, and did the number of
> confidently wrong ownership claims go up or down?

---

## The two fixtures

| File | What it is | What it measures |
| --- | --- | --- |
| [`attribution-corpus.json`](../tests/AppTrace.Core.Tests/Evaluation/attribution-corpus.json) | 24 synthetic cases: real failure patterns, known-good cases, and structural invariants | Rule-level behaviour. Deterministic and machine-independent. |
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

  // What Phase 0 does today. Asserted, so a change cannot pass unnoticed.
  "current": {
    "owner": "Vivaldi",
    "classification": "High",
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

Measured after Task 06 (Ownership Propagation & Relationship Model).

```text
Corpus
  total cases                           26   (24 + 2 synthetic relationship cases)
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

History, so the trend is visible:

| | Task 03 | Task 04 | Task 05 | Task 06 |
| --- | --- | --- | --- | --- |
| Corpus: desired met | 9 | 9 | 21 | **23 of 26** |
| Corpus: wrong-owner HIGH/CONFIRMED | 11 of 20 | 0 of 4 | 0 of 4 | 0 of 4 |
| Corpus: unsupported claims | 9 | 9 | 1 | **1** |
| Real machine: wrong-owner HIGH/CONFIRMED | 2 of 22 | 1 of 15 | 1 of 15 | 1 of 15 |
| Real machine: unsupported claims | 2 | 2 | 1 | **1** |

Task 06 changes no ownership answer on either set. That is the intended result: it
adds a *second* kind of claim alongside ownership rather than re-deciding the first.
The two corpus cases it resolves are the synthetic relationship pair it introduces.

Four things are worth understanding before reading those as a score:

* **The corpus grew by two cases.** `REL-SUBJECT-DATA-SYNTHETIC` (a generic
  `OwnerApp\Recommendations\Related Product` shape, proving `Owns` + `RelatedTo`
  with no product-specific rule) and `REL-NO-OWNER-NO-RELATION` (proving no
  relationship is invented where nothing owns the path). Percentages before and
  after Task 06 are therefore over different denominators.
* **The two real NVIDIA cases remain unresolved, and not because the model is
  missing.** The corpus fixture lacks any evidence that NVIDIA App owns the
  ancestor: `NVIDIA App 11.0.9.251` normalizes to `nvidia`, which is one word of a
  one-word name and is not treated as product identity at that depth; and its only
  registered install location is under `Program Files`. The real machine agrees —
  that ancestor is `UNKNOWN` there too. Task 07 provenance is what would close it.
* **No relationship is reported on the real machine, and that is correct.** The
  scan finds only two subject-store directories (`Recommendations`) and neither has
  an accepted owner, so there is nothing for a related application to be relative
  to. Reporting one anyway would be exactly the fabricated claim §10 forbids.
* **The one remaining unsupported claim** is
  `C:\Program Files (x86)\Oxford University Press`, CONFIRMED because the product
  registers that publisher-level directory as its install location. It is a
  *provenance* defect, not a confidence, semantics or relationship one.

`RelatedTo` never affects `ExclusiveSizeBytes`. The accounting invariant
`sum(ExclusiveSizeBytes) == TotalMeasuredBytes` is asserted as before, and the
end-to-end check on a real scan passes.

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
