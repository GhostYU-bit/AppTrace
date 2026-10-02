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

Measured after Task 05 (Filesystem Semantics & Candidate Generation V2).

```text
Corpus
  total cases                           24
  desired outcome met                   21
  wrong-owner HIGH/CONFIRMED claims      0 of 4 confident claims
  unsupported ownership claims           1   (down from 9)
  correct UNKNOWN / refusals            11   (up from 3)

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

| | Task 03 | Task 04 | Task 05 |
| --- | --- | --- | --- |
| Corpus: desired met | 9 | 9 | **21** |
| Corpus: wrong-owner HIGH/CONFIRMED | 11 of 20 | 0 of 4 | 0 of 4 |
| Corpus: unsupported claims | 9 | 9 | **1** |
| Real machine: wrong-owner HIGH/CONFIRMED | 2 of 22 | 1 of 15 | 1 of 15 |
| Real machine: unsupported claims | 2 | 2 | **1** |

Three things are worth understanding before reading those as a score:

* **Task 04 lowered confidence; Task 05 removed candidates.** That is the intended
  division of labour. After Task 04 the seven generic-token false positives were
  still accepted owners, merely at MEDIUM. After Task 05 they are refused outright,
  which is why "desired outcome met" jumps while "wrong-owner HIGH/CONFIRMED" does
  not move — the latter was already fixed.
* **The one remaining unsupported claim** is
  `C:\Program Files (x86)\Oxford University Press`, CONFIRMED because the product
  registers that publisher-level directory as its install location. It is a
  *provenance* defect, not a confidence or semantics one, so it needs Task 07's
  additional sources or a dedicated rule.
* **One correct owner was traded away.** The Steam soundtrack path under
  `steamapps\common\Cities_Skylines` was previously attributed by name to
  `Cities: Skylines`. Its naming directory is four levels below the scan root, so
  Task 05 no longer treats that name as product identity. This is the deliberate
  direction of the change: fewer unsupported ownership claims in exchange for some
  recall.

A measurement defect was also fixed during Task 05. `Met` required `OwnerMet`, so a
*correct refusal* could never be reported as met; and `WrongOwner` did not require
that an owner had been named, so a case with no accepted owner was counted as both a
wrong owner and a false negative. Both are corrected, and the evaluator's
completeness assertion now balances in both halves of the set.

---

## What this harness does not do

* It does not change attribution behaviour. During Task 03 the production code was
  not modified at all.
* It does not snapshot whole scans or maintain a golden output file per run.
* It does not score relation expectations yet: the V2 relationship model does not
  exist, so cases that ask for one are reported as unresolved rather than failed.
* It does not treat filesystem-measurement completeness (inaccessible files,
  skipped reparse points, lower-bound sizes) as attribution evidence. Those belong
  to measurement quality and are deliberately not part of any attribution score.
