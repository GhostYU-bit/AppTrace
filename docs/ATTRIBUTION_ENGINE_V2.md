# AppTrace — Attribution Engine V2 Design

**Status:** design spike for Task 02. **No production behaviour was changed.**
Every number in this document was measured on the real machine that produced the
Task 01 scan, using read-only probes. Nothing here is implemented.

Companion documents: [`RESEARCH.md`](RESEARCH.md), [`ARCHITECTURE.md`](ARCHITECTURE.md).

---

## A. Diagnosis

### A.1 The failure is singular, not varied

All seven false positives reported in Task 02 are, mechanically, **the same
failure**. Their evidence is identical:

```text
NormalizedNameMatch   +30
KnownApplicationPath  +8
                      ---
score                  38   -> HIGH
```

Confirmed from `samples/real-machine-scan.json`:

| Case | Path | Owner claimed | Evidence | Score |
| --- | --- | --- | --- | --- |
| A | `…\Local\Doubao\User Data\sandbox_runtime\bases\<id>\node` | Node.js Krypton via nvm-windows | `NormalizedNameMatch`+`KnownApplicationPath` | 38 |
| B | `…\Local\npm-cache\_npx\<id>\node_modules\@types\node` | Node.js Krypton via nvm-windows | same | 38 |
| C | `…\Local\npm-cache\_npx\<id>\node_modules\@anthropic-ai\sdk` | ASUS Aura SDK | same | 38 |
| D | `…\Local\Teardown\promo\tool` | Epson Printer Driver Security Support Tool | same | 38 |
| E | `…\Local\JianyingPro\Apps\6.6.0.12145\QtQuick\Controls\Universal` | Universal Holtek RGB DRAM | same | 38 |
| F | `…\Local\Programs\DSH Desktop\resources\app\node_modules\adm-zip` | 360 Zip | same | 38 |
| G | `…\Local\Amazon Web Services\Amazon WorkSpaces\logs\helper` | ASUS Update Helper | same | 38 |

A temporary probe (written, run, then deleted — the repository is unchanged)
reproduced all seven through the real engine at exactly **score 38, HIGH**.

### A.2 Why the score is always exactly 38

38 is not a coincidence; it is the arithmetic fingerprint of the bug. In
`AttributionEngine.CollectNameEvidence`, a name match yields
`NormalizedNameMatch` at +30. In `CollectContextEvidence`, any path under
`%LOCALAPPDATA%`, `%APPDATA%` or `%LocalLow%` yields `KnownApplicationPath` at
+8. `CollectPublisherEvidence` adds nothing because the publisher token is absent
from the path. No candidate contradicts, so no penalty fires. 30 + 8 = 38, and
`ClassifyCandidate` reaches:

```csharp
if (types.Contains(EvidenceType.NormalizedNameMatch))
    return contradicting.Length == 0 ? Classification.High : Classification.Medium;
```

So **`NormalizedNameMatch` plus the generic context bonus is, by itself,
sufficient for HIGH** — with no independent evidence that the application has
anything to do with the path.

### A.3 Root cause 1 — a single token of a name is treated as the whole name

`IsScopedNameMatch` (`AttributionEngine.cs:365`) accepts an *end-anchored word
run* in **either direction**:

```csharp
return IsAnchoredWordRun(directorySegments, appSegments)
    || IsAnchoredWordRun(appSegments, directorySegments);
```

The second direction is the defect. It means a **one-token directory** matches a
multi-token application name whenever that token happens to be the app's first or
last word:

| Directory | Application | Matched as |
| --- | --- | --- |
| `node` | Node.js **Krypton via nvm-windows** | leading run |
| `sdk` | ASUS Aura **SDK** | trailing run |
| `tool` | Epson Printer Driver Security Support **Tool** | trailing run |
| `helper` | ASUS Update **Helper** | trailing run |
| `Universal` | **Universal** Holtek RGB DRAM | leading run |
| `zip` (from `adm-zip`) | 360 **Zip** | trailing run |

Measured *name coverage* — the fraction of the application's name tokens the
directory actually accounts for — makes the shape explicit:

| Case | Directory | Application | Tokens matched / total | Coverage |
| --- | --- | --- | --- | --- |
| C | `sdk` | ASUS Aura SDK | 1 / 3 | 0.33 |
| G | `helper` | ASUS Update Helper | 1 / 3 | 0.33 |
| E | `Universal` | Universal Holtek RGB DRAM | 1 / 4 | 0.25 |
| A, B | `node` | Node.js Krypton via nvm-windows | 1 / 6 | 0.17 |
| D | `tool` | Epson Printer Driver Security Support Tool | 1 / 6 | 0.17 |
| F | `adm-zip` | 360 Zip | 0 / 2 (trailing-only overlap) | 0.00 |

Every false positive has coverage **≤ 0.33**. Every Task 01 success has coverage
**≥ 0.50**:

| Case | Directory | Application | Coverage |
| --- | --- | --- | --- |
| Vivaldi | `Vivaldi` | Vivaldi | 1.00 |
| Google Chrome | `Chrome` | Google Chrome | 0.50 |
| Microsoft Edge | `Edge` | Microsoft Edge | 0.50 |
| Microsoft OneDrive | `OneDrive` | Microsoft OneDrive | 0.50 |
| Cities: Skylines (game dir) | `Cities_Skylines` | Cities: Skylines | 1.00 |

**Coverage ≥ 0.5 separates all seven false positives from all five successes.**
That is a single structural rule, not seven patches.

### A.4 Root cause 2 — no provenance is required

Nothing in Phase 0 requires that the application has ever been *known to touch*
the path. `InstallLocationMatch`, `DeclaredInstallLocation`,
`ExecutableMetadataMatch`, `RegistryReference`, `ProductCodeMatch` and
`ShortcutReachable` all either do not exist or are absent from these paths. The
identity evidence is a **name coincidence with no corroboration**, and the
context evidence is not about the application at all.

Measured across the whole scan (196 accepted owner-claims inside HIGH/CONFIRMED
locations), this is not a marginal problem:

| Evidence signature behind an accepted HIGH/CONFIRMED claim | Count |
| --- | --- |
| `NormalizedNameMatch` as the **only** supporting record | 64 |
| `NormalizedNameMatch` + `KnownApplicationPath` | 25 |
| `NormalizedNameMatch` + `KnownPublisherNamespace` and/or `ParentDirectoryMatch` | 27 |
| **Any claim resting on `NormalizedNameMatch`** | **116 of 196 (59%)** |
| Any claim resting on `DeclaredInstallLocation` (registry-backed) | 64 |
| Any claim resting on `ExactDirectoryNameMatch` | 30 |
| Any claim resting on `InstallLocationMatch` | 7 |

In other words: **roughly three in five accepted ownership claims are carried by
name resemblance**, and only about one in three has the application's own
registration data behind it. Registry-backed claims and name-only claims are not
in the same epistemic category, and Phase 0 presents them with the same word.

And the distribution by depth is damning:

| Depth | Name-evidence-only HIGH claims |
| --- | --- |
| 1–2 | 21 |
| 3–4 | 20 |
| **5–6** | **59 (59%)** |

The deeper the leaf, the more likely Phase 0 is wrong. That is the signature of a
rule that has no notion of structure: a leaf token such as `sdk` or `node` is
*more* likely to match a product name by accident precisely because deep leaves
are vocabulary (`node`, `sdk`, `bin`, `helper`, `tool`, `Universal`), not brands.

### A.5 Root cause 3 — `KnownApplicationPath` is not evidence about an application

`CollectContextEvidence` grants +8 to every candidate whenever the path is
anywhere under `%LOCALAPPDATA%`, `%APPDATA%` or `%LocalLow%`. But
`%LOCALAPPDATA%\npm-cache` contains tens of thousands of directories, and
`%LOCALAPPDATA%` contains hundreds of vendor trees. A bonus that applies to
essentially every path in the scan cannot discriminate between them.

Worse, it is *correlated* with the error rather than independent of it: the
false positives are deep leaves in application-data areas, exactly where the
bonus always fires. Independent evidence that is perfectly correlated with the
hypothesis adds nothing but confidence inflation.

### A.6 Root cause 4 — ownership and relationship are conflated (Cases 3 and 4)

`…\NVIDIA app\NvBackend\Recommendations\cities_skylines → Cities: Skylines, HIGH`
(score 48, `ExactDirectoryNameMatch` + `KnownApplicationPath`).

This is a *correct name match* producing a *wrong attribution*. The directory is
storage owned by NVIDIA App that happens to be named after a game. Phase 0 has
one relation — ownership — so a name match can only be expressed as a claim that
the named application owns the bytes.

`…\LocalLow\Colossal Order\Cities Skylines II → Cities: Skylines, HIGH` is the
same defect seen from the other side. `Cities: Skylines II` was not in the
candidate set, so the nearest known name won. There is no rule anywhere in Phase
0 that says *"if the best available explanation requires a near-miss, prefer
UNKNOWN"*.

### A.7 Incidental bugs versus architectural weaknesses

Separating these matters, because only the second group justifies a redesign.

**Incidental (fixable in place, no model change):**

1. `IsScopedNameMatch`'s reverse direction lets a *trailing generic word* carry a
   match (`adm-zip` → `360 Zip` matches on the shared token `zip` alone, with the
   leading token `adm`/`360` ignored entirely).
2. `GenericDirectoryNames` is a hand-maintained blacklist. It covers `app`,
   `cache`, `common`, `tools`, `runtime`, `updater` — but not `sdk`, `node`,
   `tool`, `helper`, `universal`, `zip`. **A blacklist cannot converge**: every
   new application adds new vocabulary, and the list is only ever corrected after
   a user reports a false positive.
3. `KnownApplicationPath` is applied without regard to how specific the containing
   directory is.

**Architectural (require the V2 model):**

4. **No provenance requirement.** Similarity can reach HIGH with no independent
   evidence that the application touches the path.
5. **No notion of identity specificity.** All name matches are the same kind of
   evidence; there is no concept of *"this token is a brand"* versus *"this token
   is vocabulary"*.
6. **Additive scoring across categories.** Five weak signals can outvote one
   strong contradiction, because every record is summed into one integer.
   `MeetsOwnershipBar` is a partial patch over this, not a fix.
7. **Relationships collapse into ownership.** No way to say *"owned by NVIDIA,
   named after Cities: Skylines"*.
8. **Candidates are generated from every path × every application.** Candidate
   generation and scoring are the same step, so implausible candidates enter
   scoring at all.
9. **No use of provenance sources AppTrace already has.** `DisplayIcon` (present
   on 128 of 244 uninstall entries on this machine), App Paths, services,
   scheduled tasks and shortcuts are all unread.
10. **Ownership does not propagate.** Recursion descends into an already-attributed
    subtree and re-evaluates leaves from scratch with no memory of whose scope
    they are in.

### A.8 What Phase 0 got right and V2 must not break

* `FootprintItem → CandidateOwner[] → Evidence[]` with per-relationship evidence.
* Explanations rendered from structured evidence, never stored as strings.
* Registry-declared install locations as a decisive anchor (the 47
  `DeclaredInstallLocation` claims are the trustworthy half of the results).
* Vendor-namespace handling and the `PublisherMismatch` signal.
* The byte-accounting contract (`sum(exclusive) == total measured`).
* Reparse-point safety, error reporting, read-only guarantees.
* Refusing to close a subtree on weak evidence (`MeetsOwnershipBar`).

The Vivaldi / Chrome / Edge / OneDrive / WPS Office / Trae results are correct and
must stay correct. Measured coverage for each is ≥ 0.5, so the V2 rules proposed
below preserve them — this is checked explicitly in the regression corpus (§J).

---

## B. Evidence Source Survey

All counts measured on one Windows 11 development machine (115 visible
applications, 244 uninstall entries including updates and components). The raw
probe transcript is **deliberately not part of this repository**: it is a full
inventory of an individual machine, and publishing it would leak an unnecessary
software and profile fingerprint. Every figure below is reproducible on your own
machine by re-running the equivalent read-only probe.

### B.0 The headline gap

| Uninstall registry field | Entries with it | Coverage |
| --- | --- | --- |
| `DisplayName` | 244 | 100% |
| `UninstallString` | 230 | 94% |
| `EstimatedSize` | 181 | 74% |
| **`DisplayIcon`** | **128** | **52%** |
| **`InstallLocation`** | **77** | **32%** |

**Only 32% of registered applications declare an install location.** Add/Remove
Programs is therefore not a sufficient universe, and the current architecture
inherits that gap directly. This single table justifies multi-source discovery
and identity enrichment more than any theoretical argument. The gap is worst where
it matters most — `HKLM`, the hive holding normal per-machine applications
(§B.1).

### B.1 Registry sources

| Source | What it tells us | Reliability | Cost | First run | False-positive risk | Recommended use |
| --- | --- | --- | --- | --- | --- | --- |
| Uninstall keys — `InstallLocation` | An exact directory the app claims | High; occasionally a shared vendor parent | Already read | Yes | Vendor-parent claims (already handled by `DeclaredInstallLocation`) | **Decisive anchor** (keep) |
| Uninstall keys — `DisplayIcon` | A path to an icon, usually the app's own `.exe` | Medium-high; sometimes an `.ico`, an index suffix (`file.exe,0`), or an uninstaller | Already read, unused | Yes | Low if the path is validated to exist and to be an `.exe` | **Executable discovery** — 128 entries of leverage already in hand |
| Uninstall keys — `UninstallString` / `QuietUninstallString` | The uninstaller's path, which sits in the app's install tree | Medium; often `msiexec.exe` with a product code | Already read, unused | Yes | Medium — an uninstaller is *in* the tree but is not the app | **Path-anchor evidence** after stripping arguments; never identity |
| `App Paths` | `HKLM/HKCU …\CurrentVersion\App Paths\<exe>` → executable path | High; this is the Start-Run and shell resolution contract | 45 entries, ~20 ms | Yes | Low | **Executable discovery.** 45 here, including `chrome.exe`, `Photoshop.exe`, and MSIX apps in `WindowsApps` |
| `SOFTWARE\Classes\Applications` | Registered applications and their shell verbs | **Weak — measured**: 86 subkeys but only **12 have `shell\open\command`**, and no `…\Application` subkey exists at all | Small | Yes | Medium | **Drop.** 86% carry no command, so the key is mostly a name list with no path |
| `RegisteredApplications` + `…\Capabilities` | App name, publisher, associations | **Misleading — measured**: HKLM has 16 entries and `ApplicationCompany` was **empty on every one sampled**; `ApplicationName` is an *indirect resource string* (`@C:\WINDOWS\explorer.exe,-6020`), not a name. HKCU has 313, of which ~96% are `AppX<hash>` IDs with `ms-resource://` URIs; of the first 40 only **4 resolved** | Small | Yes | High (unresolvable strings look like names) | **Drop.** Resolving resource strings is real work for almost no path evidence |
| File associations / `shell\open\command` | Which executable opens a file type | **Extreme conflation — measured**: 797 extension keys, 351 with a resolvable command, collapsing to only **54 distinct executables** (`WScript.exe`, `msiexec.exe`, `notepad.exe`, …) | Large | Yes | Very high — one `.exe` "owns" hundreds of types | **Drop** |
| Protocol handlers | The handler for a scheme | High for the specific claim, but **measured sparse**: `http`/`https` → `msedge.exe`, `steam` → `Steam\steam.exe -- "%1"`; **`mailto` missing**; `ms-settings` present with an **empty** command; `ms-windows-store` missing | Tiny | Yes | Low | **Niche**: a store/ecosystem anchor only, never a general source |
| COM registration (`CLSID` → `InprocServer32`/`LocalServer32`) | A DLL/EXE registered by a product | **Dead end — confirmed twice**: 400 sampled of 7,717 keys; 366 had a server path, of which **294 (80%) pointed into `C:\Windows`**, 53 (14.5%) into `Program Files`, 19 were relative DLL names | Large | Yes | Medium | **Drop from the plan.** 80% is OS infrastructure with zero app attribution |
| Services (`Win32_Service.PathName`) | **56** services whose executable lives outside `C:\Windows` | High for the executable path | ~200 ms via CIM | Yes | Very low | **Strong provenance**: `AlibabaProtect.exe`, `ArmouryCrate.Service.exe`, `SGuardSvc64.exe` |
| Scheduled tasks (`Get-ScheduledTask` actions) | **96** tasks whose executable lives outside `C:\Windows` | High for the executable path | ~1 s | Yes | Very low | **Strong provenance**, and it reaches *portable* apps (`D:\下载\DriverBoosterPro_…\DriverBooster.exe`) that the uninstall registry missed entirely |
| Startup `Run` keys | **18** entries pointing at executables | High | Tiny | Yes | Very low | **Strong provenance** |
| `Installer\Products` / `UserData` | MSI product → install source, package cache, features | High for MSI products | Medium; parts may need admin | Yes | Low | **Provenance for MSI apps**; explains `Package Cache` residuals |
| MSIX `AppModel\Repository\Packages` | Package family name, publisher, install root | High — **measured: 144 of 172** `%LOCALAPPDATA%\Packages` directories match an installed package family name **exactly** | Already read | Yes | Low | **Discovery + deterministic `%LOCALAPPDATA%\Packages` mapping** |

Measured per-hive uninstall coverage, which is worse than the aggregate suggests:

| Hive | Entries with `DisplayName` | With `InstallLocation` | Gap |
| --- | --- | --- | --- |
| `HKLM` | 150 | 39 | 74% |
| `HKLM\WOW6432Node` | 140 | 45 | 68% |
| `HKCU` | 19 | 10 | 47% |

### B.1a Three measured results that change priorities

**MSIX package-data mapping is essentially solved and cheap.** 189 AppX packages
were seen (140 with an `InstallLocation` under `WindowsApps`), and
`%LOCALAPPDATA%\Packages` holds 172 directories of which **144 (84%) match an
installed `PackageFamilyName` exactly**. The 28 that do not are OS/legacy folders
(`ActiveSync`) and Chromium profile hashes (`cr.sb.cdm*`). Two caveats: the AppX
list **mixes roots** (`C:\Windows\SystemApps\…` and `…\WindowsApps\…`), so they must
be distinguished, and the publisher ID suffix (`cw5n1h2txyewy`, `8wekyb3d8bbwe`) is
already part of the family name and needs no hashing.

**Half the registry sources I expected to use are dead ends, and measuring was the
only way to know.** `Classes\Applications` (86 subkeys, 12 with a command),
`RegisteredApplications` (`ApplicationCompany` empty, `ApplicationName` an
unresolvable resource string), file associations (797 keys → 54 executables), and
COM/CLSID (80% into `C:\Windows`) all measured as low-yield or actively
misleading. They are struck from the plan in §K. This is a useful negative result:
the value is concentrated in a small number of *path-bearing* sources, not in the
breadth of the registry.

**Protocol handlers are real but sparse, and `mailto` is missing entirely** — a
reminder that shell registration is user state, not an inventory.

### B.2 Executable metadata

Measured over 100 top-level executables sampled from `Program Files` and
`Program Files (x86)`:

| Field | Non-empty | Reliability as identity evidence |
| --- | --- | --- |
| `ProductVersion`, `FileVersion` | 87% | High mechanically, **low identity value** — distinguishes two installs of one product, never identifies it |
| `ProductName` | 86% | **Medium**, and frequently generic or wrong (see below) |
| `FileDescription` | 86% | Medium; often a friendly one-line description ("Google Chrome") — sometimes *better* than `ProductName` |
| `CompanyName` | 83% | Medium-high; legal suffix present on **48%** of samples (normalization already strips it), empty on 17% |
| `OriginalFilename` | **69%** | The weakest field measured — do not depend on it |

**Measured cases where metadata actively misleads:**

| Executable's folder | `ProductName` | `CompanyName` |
| --- | --- | --- |
| `CH Aurora` | `Updater` | `Updater` |
| `CH Aurora` | `SteamdeckHelper` | — |
| `Microsoft Update Health Tools` | `Microsoft® Windows® Operating System` | `Microsoft Corporation` |
| `Windows Defender` | `Microsoft® Windows® Operating System` | `Microsoft Corporation` |
| `dotnet` | `.NET` | — |
| `DoubaoIME` (`unins000.exe`) | `豆包输入法…` + ~55 trailing pad spaces | — |
| `FeverGames` (`benchmark.exe`) | `镇魔曲测评程序` | — |

Two conclusions follow directly from this table:

* **The folder name and `ProductName` disagree often enough that neither can
  override the other.** `CH Aurora`'s updater calls itself `Updater`; a Chinajoy
  benchmark lives in `FeverGames`. Metadata is **corroboration**, and a
  *disagreement* is a contradiction signal rather than a reason to prefer either
  side.
* **`ProductName` can be a platform, not a product** (`Microsoft® Windows®
  Operating System`, `.NET`, `Updater`). These need the same specificity treatment
  as directory names — a generic `ProductName` must not confer identity.

**Confirmed negative result: directories carry no version metadata.**
`(Get-Item 'C:\Program Files').VersionInfo` returns empty strings. There is no
directory-level equivalent of an executable's version resource, so metadata can
never describe a *directory*; it can only describe a binary inside one.

**Key measured caveat:** metadata describes the *binary*, not the *directory*.
A single `.exe` in a directory proves the binary is there; it does not prove the
directory is that application's storage. Metadata is therefore **corroborating
identity evidence** and a **provenance anchor for the executable's own tree**,
never a standalone ownership claim.

### B.3 Digital signatures — measured on this machine

Two independent samples were measured, and they agree:

| Approach | Sample | Total | Per file | Notes |
| --- | --- | --- | --- | --- |
| `Get-AuthenticodeSignature` (full chain + revocation) | 120 | 19,330 ms | **161 ms** (mean) | Network-bound. Median was only **24.0 ms** with the first call at 221.9 ms and the last at 12.7 ms — chain/CRL caching dominates, so the cost is a cold-start penalty, not a steady rate |
| `X509Certificate.CreateFromSignedFile` (embedded certificate only) | 120 | 513 ms | **4.3 ms** | Offline, no chain building. ~37× faster |
| Second sample, same two approaches | 60–77 | — | 143.5 ms vs **4.0 ms** | Confirms the ratio (~36×) |

Status distribution: **Valid 103, NotSigned 14, HashMismatch 3** (n=120);
**Valid 66, NotSigned 9, UnknownError 2**, 68 with a signer certificate (n=77).
Distinct signer organisations observed (29 distinct subjects in the second sample):

```text
CN=Adobe Inc.                       46
CN=ASUSTeK COMPUTER INC.            21
CN=Blackmagic Design Pty Ltd        10
CN=Kakao Corp.                       7
CN=北京春田知韵科技有限公司            5
CN="NetEase (Hangzhou) Network Co.   4
CN=SEIKO EPSON CORPORATION           2
CN=Stichting Blender Foundation      2
E=chris@cheathappens.com             3
```

Conclusions:

* **Practical to retrieve.** The 4.0–4.3 ms embedded-certificate read makes signing
  evidence affordable at the scale of "one representative binary per attributed
  directory".
* **Full verification is not affordable** (144–161 ms mean) and is unnecessary: for
  attribution we need the *claimed signer*, not a trust decision. AppTrace makes no
  security claim, so it reads the certificate and reports "signed by X" rather than
  asserting validity.
* **Catalog signing is the sharpest trap, and it is measured.** The fast path
  reported *no signature* on **7 of 60 files that full verification called
  `Valid`**, always one-directionally, and every miss was a catalog-signed Windows
  binary (`wab.exe`, `wabmig.exe`, `iediagcmd.exe`, `ExtExport.exe`,
  `setup_wm.exe`). **The fast path is therefore a pre-filter for a signer
  organisation, never an authority on "is this signed".** A gate built on it would
  silently misclassify OS components as unsigned — exactly the kind of quiet
  wrongness this project exists to avoid.
* **Signer identity is usually the true publisher, but not always the
  *product owner*.** The subjects above are real product owners, yet measured
  counter-examples exist: `CN=Microsoft Windows` signs binaries sitting in
  third-party folders (`Microsoft Update Health Tools`, `Windows Defender`), and
  `CH Aurora`'s `Updater.exe` is signed by *Shanghai Hudun Information Technology
  Co., Ltd.* — matching neither the folder name nor its own `ProductName`. No
  reseller-as-subject (DigiCert/Sectigo) was observed.
* **`HashMismatch` (3 of 120) and `UnknownError` (2 of 77) occur.** Signature
  reading must be best-effort evidence, never an invariant.
* **14 of 120 were unsigned.** Unsigned is normal (small vendors, open source,
  portable tools) and must never penalise an attribution — only its absence is
  neutral.
* **The email-form subject (`E=chris@cheathappens.com`) shows the subject is not
  always a company name**, so parsing must handle `CN`/`O`/`E` and degrade
  gracefully.

**Recommended use:** signer organisation is **identity corroboration** — it can
confirm a publisher match and help resolve a publisher-namespace candidate set —
and is explicitly **not** sufficient for ownership on its own.

### B.4 Shortcuts

Start Menu and Desktop `.lnk` files encode `TargetPath` and `WorkingDirectory`,
which map an application identity to the directory containing its launcher.

Measured on this machine: **227 `.lnk` files** across the Start Menu and Public
Desktop (`ProgramData` 125, user Start Menu 69, Public Desktop 33). Of a
60-shortcut sample: **100% resolved to an existing file**, **15% pointed at a
non-`.exe` target**, and **13% (8 of 60) pointed at an updater, uninstaller or
launcher** — e.g. `卸载360 Zip.lnk → 360zip\Uninstaller.exe`,
`Uninstall Advanced Renamer.lnk → unins000.exe`,
`Visual Studio Installer.lnk → …\Installer\setup.exe`,
`EA app 更新程序.lnk → EAUpdater.exe`. Targets were almost all distinct (59
distinct from 60), so a shortcut is close to a one-to-one app→directory mapping.

* Measured value: moderate. A shortcut most often points into the app's install
  tree, which the uninstall registry frequently already gives.
* The real gain is for applications with **no `InstallLocation`** (74% of `HKLM`
  entries) and for **portable** applications with no uninstall entry at all — a
  shortcut is often the only static evidence that such a program exists.
* False-positive risk is measured and modest but real: ~13% of targets are
  updaters/uninstallers/launchers, so the target must be filtered against
  updater-name vocabulary before it becomes a provenance anchor. Note that a
  launcher *is* legitimate evidence for the app's own tree, whereas an uninstaller
  is evidence for the tree but not for identity.
* Cost: `WScript.Shell` COM enumeration of a few hundred `.lnk` files, tens of ms.

**Recommended use:** provenance anchor for the target's directory, with the
target classified (main binary / launcher / updater / uninstaller) before use.

### B.5 Package systems — architecture answer

**Yes: discovery should become multi-source.** The measured 32% `InstallLocation`
coverage makes this an evidence-based conclusion rather than a preference. The
recommended shape is a small set of `IApplicationSource` providers feeding one
merged identity, each reporting its own provenance:

* **Uninstall registry** — the baseline (already present).
* **MSIX/AppX repository** — already present, currently filtered to reduce noise;
  needs to be reconnected to `%LOCALAPPDATA%\Packages`, which the measured 144/172
  exact family-name match shows is a solved problem.
* **App Paths + scheduled tasks + services + Run keys + shortcuts + `DisplayIcon`**
  — the highest measured value per unit of effort (45 + 96 + 56 + 18 + 227 + 128
  measured anchors).
* **Steam** — measured present and genuinely useful, with two traps that show why
  ecosystem data must be read from *manifests*, not from directories.
  * Working evidence: `HKLM\SOFTWARE\WOW6432Node\Valve\Steam` → `InstallPath = C:\Program Files (x86)\Steam`; `steamapps\libraryfolders.vdf` (301 bytes) declares 1 library and an `apps {228980, 255710}` list; `appmanifest_228980.acf` gives `appid`, `name = "Steamworks Common Redistributables"`, `installdir`, `LauncherPath`, `SizeOnDisk`, `LastOwner`.
  * **Trap 1: a directory that looks like a library proves nothing.** `D:\SteamLibrary` exists but has **no `libraryfolders.vdf` and zero `appmanifest_*.acf`** files. Any rule of the form "this is under a Steam library, therefore it is a game" would be wrong.
  * **Trap 2: declared ≠ installed.** The vdf lists app **255710**, but there is **no `appmanifest_255710.acf`**. Steam's library metadata can name an app that is not on disk.
  * What it adds over Add/Remove Programs: a stable numeric `appid` (immune to display-name localisation), `installdir` pinning the exact directory leaf, `LastOwner` for per-user attribution, and **coverage ARP omits entirely** — appid 228980 occupies 122 MB with no Add/Remove Programs entry at all.
  * Measured payoff on *this* machine is small (2 manifests), so it is a later task, not V2 core. That is a statement about this machine, not about the source.
* **Epic** — measured present: `HKLM\SOFTWARE\WOW6432Node\Epic Games`, `EpicGames`, `HKCU\Epic Games`, plus `C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests` and `LauncherInstalled.dat`. Manifest-per-app, same shape of evidence as Steam.
* **Other stores (GOG, Origin/EA, Blizzard)** — registry keys exist here, but
  `C:\ProgramData\GOG.com\Games` was **not found**, so existence of a key again
  proves nothing about installed content.
* **Other stores generally** — only worth adding once the provider abstraction
  exists and the marginal cost is small.

What must **not** happen: a curated "application X lives at path Y" database. The
rule stays *ecosystem structure*, not *application inventory*.

### B.5a Ranked usefulness, by measurement

Everything above ranked by how much directory→application attribution it actually
buys per unit of effort:

| Rank | Source | Why |
| --- | --- | --- |
| 1 | **Ecosystem manifests** (Steam/Epic `appmanifest`/`.acf`) | Direct `installdir` pinning, stable app ids, covers apps ARP omits entirely |
| 2 | **App Paths + services + scheduled tasks + Run keys + shortcuts + `DisplayIcon`** | 45 + 56 + 96 + 18 + 227 + 128 measured executable anchors, essentially all product-owned |
| 3 | **`%LOCALAPPDATA%\Packages` ↔ `PackageFamilyName`** | 84% exact match; the only route to per-user Store app data |
| 4 | **Uninstall keys** | Authoritative when present, but only 26–32% carry an `InstallLocation` |
| 5 | **Executable metadata + signer** | Good corroboration, **unsafe as a key** (§B.2, §B.3) |
| 6 | **CLSID, file associations, `RegisteredApplications`, `Classes\Applications`** | Measured low-yield or misleading — see §B.1a |

The shape of this ranking is itself the finding: **the value is concentrated in
sources that carry a path, and the registry's breadth does not help.** Four
registry surfaces that a reasonable design would have included turned out to be
dead ends, and only measurement showed it.

### B.6 Where Windows does not preserve provenance

To be explicit about the limits, because this shapes what UNKNOWN must mean.
Measured on this machine:

* **No creation record.** NTFS does not record which process created a directory.
  No readable per-directory "installed by" attribute exists.
* **Ownership/ACLs are not informative.** Of 20 directories sampled under
  `%LOCALAPPDATA%`, **18 were owned by the user and 2 by
  `BUILTIN\Administrators`** — ownership reflects the writing *user*, not the
  writing application, and so carries almost no attribution signal.
* **The USN journal is readable without elevation** — `fsutil usn queryjournal C:`
  succeeded in this non-elevated session (`First Usn 0x…865e000`). This is more
  accessible than expected, but it remains a **change log, not an ownership
  record**: it can say *"something changed here at time T"*, never *"application X
  owns this"*. It becomes useful only when correlated with process activity, which
  is runtime observation.
* **Compatibility junctions exist and must never be followed.** Measured: **3
  reparse points directly under `%LOCALAPPDATA%`**, including
  `Application Data → C:\Users\<user>\AppData\Local` — a **self-referential loop**
  — plus `History` and `Temporary Internet Files` pointing into
  `%LOCALAPPDATA%\Microsoft\Windows`. Following `Application Data` would recurse
  forever; Phase 0's "measure but never follow" rule is load-bearing, and V2 must
  keep it.
* **Most AppData folders are created by the app itself at first run**, not by the
  installer, so the install location often legitimately does not contain them.
  This is why `AppData\Local\Vivaldi` cannot be proven from the registry alone and
  why name-plus-provenance, not name alone, is the honest ceiling.

---

## C. Evidence Taxonomy

> **Implemented in Task 04.** The `EvidenceKind` enum, its mapping in
> `EvidenceClassification`, `Evidence.Specificity`, the `KnownApplicationPath`
> correction and contradiction gating now exist in production code. See the
> "Task 04 as built" section at the end of this document for what was and was not
> implemented.

The Phase 0 categories (`SupportsAttribution`, `Strength`, `EvidenceSource`)
survive, but they are flat: they cannot express *what kind of claim* a record
makes. V2 adds one dimension — **claim kind** — because the false positives are
precisely a category error (a *structure* record was allowed to behave like an
*identity* record).

```text
EvidenceKind:
    Identity        answers  WHO is this?
    Provenance      answers  WHAT created or registered this path?
    Structure       answers  WHAT KIND of storage is this?   (renamed from Context)
    Relationship    answers  Is this OWNED BY X, or merely ABOUT X?
    Contradiction   answers  What argues AGAINST this claim?
```

**Design correction: there is no `Accounting` kind.** An earlier draft of this
document proposed one, to carry the "size is a floor" idea into the evidence
model. That was wrong, and Task 04 removed it. Measurement completeness —
inaccessible files, skipped reparse points, lower-bound sizes, scan errors —
describes *how trustworthy a number is*, not *who owns a path*. Making it an
attribution evidence kind would let a measurement difficulty influence an
ownership claim, which is exactly the category of error this taxonomy exists to
prevent. Measurement quality and attribution confidence stay orthogonal.

### C.0 Structure explains a path but does not prove an owner

> **Structure does not establish application ownership.**

This is the single rule that Task 04 enforces mechanically. A record that says
*"this is a per-user application data location"* is true of nearly every path
AppTrace inspects, so it cannot distinguish between them. It may explain a
directory to a human reader, and it may reduce confidence, but it can never raise
it — and since Task 04 it cannot, because the classification ladder reads evidence
*kind* rather than the summed score.

### C.1 Semantics and the rule each kind carries

| Kind | Meaning | May propose a candidate? | May raise confidence? | May establish ownership? |
| --- | --- | --- | --- | --- |
| **Identity** | This path's name/metadata names a specific product | Yes | Yes, as corroboration | Only alongside provenance (see §G) |
| **Provenance** | The application's own registration anchors it to this path | Yes | Yes, strongly | Yes, in the forms listed in §G |
| **Structure** | The path is a known *kind* of storage, or a namespace | **No** | **No** — only by *reducing* | **Never** |
| **Relationship** | An ancestor is owned by an application and this child is named after another | Yes, as a *relation* | N/A | Produces `RELATED_TO`, not `OWNS` |
| **Contradiction** | A different, specifically-identified application owns this scope | No | Kills (decisive) or limits | Never |

Two deliberate changes from Task 02's suggested taxonomy:

* **`Context` → `Structure`.** "Context" invites records like *"is under
  AppData"*, which is where Phase 0 went wrong. "Structure" names what the layer
  actually knows: the *kind* of storage. It is a property of the path, and it may
  never raise confidence on its own.
* **`Relationship` promoted to first-class**, because Cases 3 and 4 cannot be
  expressed without it.

### C.2 Specificity — the missing property of identity evidence

Identity evidence needs one more field:

```text
Evidence.Specificity : 0..1   // how much of the product's identity this names
```

Not a new hand-maintained list. Computed from **measured, deterministic facts**:

1. **Name coverage** — the fraction of the application's name tokens the
   directory accounts for (§A.3). This alone separates all observed cases.
2. **Token document frequency within the installed corpus** — how many installed
   applications' names contain the matched token. Measured on this machine:

   | Token | Document frequency (of 115 apps) |
   | --- | --- |
   | `nvidia` | 20 |
   | `microsoft`, `adobe` | 8 |
   | `asus`, `container` | 5 |
   | `sdk` | 2 |
   | `node`, `chrome`, `vivaldi`, `acrobat`, `skylines`, `telemetry` | 1 |

3. **Positional role** — whether the matched run includes the application's
   *head* token (the brand: `Chrome`, `Vivaldi`, `Acrobat`) or only a *tail* token
   (`SDK`, `Tool`, `Helper`).

**Honest finding, and it matters:** document frequency alone is **not**
sufficient on a real machine. `node` has document frequency 1 of 115 — the same as
`vivaldi` — because npm-based tooling registers as one application. A
specificity model built only on corpus statistics would have kept FP-A and FP-B.
What actually separates them is **coverage plus the provenance requirement**, with
document frequency as a *third* corroborating signal that helps most on machines
with more installed software. Curated generic vocabulary remains useful as a small
seed list, but **V2 must not depend on it growing**, and must never rely on it to
catch a false positive that the structural rules already catch.

---

## D. Candidate Generation

### D.1 The rule that replaces path × application

> **A candidate is generated only by evidence that mentions the application, or
> by the application's own registered provenance — never by similarity alone.**

Concretely, candidate generation becomes an **index lookup**, not a scan over all
applications:

| Generator | Key | Yield |
| --- | --- | --- |
| Declared install location | path → apps whose registered location is an ancestor | 77 entries here |
| Executable anchor (DisplayIcon, App Paths, services, tasks, Run, shortcuts) | directory of each registered binary → app | **45 + 96 + 56 + 18 measured** |
| Publisher namespace | directory name token → apps with that publisher | existing |
| Product code / registry reference | GUID or product token found in the path | new |
| Fully-qualified name equality | folded directory name → apps with that **exact** name | high precision, low recall |

Name *similarity* is no longer a generator. It becomes a **validator** (§D.3).

### D.2 Indexes to build once per scan

```text
installLocationIndex : normalized path      -> AppIdentity[]
binaryDirectoryIndex : normalized directory -> AppIdentity[]   // from all anchors
publisherTokenIndex  : folded token         -> AppIdentity[]
exactNameIndex       : folded name          -> AppIdentity[]
nameTokenIndex       : folded token         -> AppIdentity[]   // for validation only
```

Every lookup is O(1)-ish. The `nameTokenIndex` replaces the O(paths × apps) name
loop with an O(tokens in the directory name) lookup, which is also a scalability
win: candidate generation becomes proportional to *path depth*, not to the number
of installed applications.

### D.3 Validation, which is where similarity lives now

For each generated candidate, in order:

1. **Ancestor gate.** Walk up the path. If an ancestor is *asserted* as owned by
   application X:
   * a candidate equal to X → **ownership** continues;
   * a candidate different from X → downgrade to a **relationship** (§F), unless
     that candidate carries *decisive* provenance of its own (its own registered
     install location, or its own binary in this directory).
2. **Coverage gate.** Name-derived evidence requires:
   * coverage ≥ 0.5 (verified to separate all observed cases), **and**
   * the matched run must include at least one token that is *not* trailing
     generic vocabulary — i.e. the run must reach the application's head token or
     a token with document frequency ≤ a small bound.
3. **Provenance gate.** See §G: no candidate reaches HIGH without at least one
   provenance record.

Rejected candidates are **retained as `CandidateOwner` records marked
`Rejected` with their evidence**, so the WHY output can say *"ASUS Aura SDK was
considered and rejected because the folder name `sdk` accounts for 1 of 3 name
tokens and ASUS Aura SDK registers no directory here."* Explainability of
rejection is worth as much as explainability of acceptance.

### D.4 Cost note

Phase 0's `CollectNameEvidence` and `CollectPublisherEvidence` both loop over all
applications for every directory; `MaxCandidatesPerLocation = 16` exists only to
bound the memory that produces. Indexed generation removes the cause instead of
capping the symptom, and is a prerequisite for ever attributing 100k+ directories
without the memory blowup that already caused one out-of-memory failure during
Task 01 development.

---

## E. Filesystem Semantic Layer

### E.1 Purpose and the boundary

The layer answers exactly one question:

> **What kind of storage structure is this path part of?**

It explains **why a name must not be read as a brand**, and it can therefore only
*reduce* confidence or *change the relation*. It never creates an owner.

**The boundary, stated as rules:**

| In scope (structure knowledge) | Out of scope (inventory knowledge) |
| --- | --- |
| `node_modules` is a dependency tree; the directory below it is a package, not an application | "Package `@anthropic-ai/sdk` belongs to Application X" |
| `_npx`/`_cacache` are package-manager caches | "npm's cache belongs to Node.js Krypton via nvm-windows" |
| `User Data\Default\Cache` is a Chromium profile cache | "Directory `Default` belongs to Google Chrome" |
| `steamapps\common\<Game>` is a game install | "This game is installed, therefore it exists" |
| `SquirrelTemp`, `app-<version>` reveal an Electron/Squirrel update tree | "Version 1.2.3 belongs to App Y" |
| `sbx`/sandbox runtime layouts are runtime state | — |
| `DXCache`/`GLCache` are GPU driver caches | — |

A rule earns its place only if it is **product-independent**: it holds for every
application of that shape, on any machine. Anything that names a specific product
belongs in identity discovery, not here.

### E.2 Minimal rule set

```text
SemanticKind (proposed, minimal):
    PackageDependencyTree      node_modules, site-packages, vendor/, .venv
    PackageManagerCache        npm-cache, _npx, _cacache, pip cache, .nuget/packages,
                               .gradle, .m2, .cargo/registry, go/pkg/mod
    ApplicationProfile         User Data, <Profile>, Default, Local Storage,
                               IndexedDB, Network, Preferences
    ApplicationCache           Cache, Code Cache, GPUCache, ShaderCache, DXCache,
                               GLCache, NV_Cache, Crashpad, CrashDumps
    ApplicationRuntime         sandbox_runtime, sbx, runtime, jre, python, node
    ApplicationUpdateTree      SquirrelTemp, app-<version>, Update.exe, packages
    GameInstall                steamapps/common/<x>, <x>_Data
    GamePlatformData           steamapps, userdata/<id>/<appid>
    VendorNamespace            (existing concept — a publisher's directory)
    ContainerOrRoot            the scan roots themselves
    Temporary                  Temp, tmp
    Logs                       logs, Logs
    Unknown                    everything else
```

Each rule is a name matcher plus, where useful, a positional condition (most fire
only when the segment is *not* the first token of the path below the scan root,
so a product genuinely called `Cache` at `Program Files\Cache` is not
misclassified).

### E.3 Worked example 1 — the npm case (false positive C)

```text
C:\Users\<user>\AppData\Local\npm-cache\_npx\1e7f6d9597241db0\node_modules\@anthropic-ai\sdk
```

| Segment | Semantic reading | Effect |
| --- | --- | --- |
| `AppData\Local` | per-user application data (structure) | no candidate, no credit |
| `npm-cache` | `PackageManagerCache` | **suppresses** name-as-brand for everything below |
| `_npx` | `PackageManagerCache` (npx scratch) | confirms the reading |
| `1e7f6d…` | opaque id, no identity | nothing |
| `node_modules` | `PackageDependencyTree` | below here, names are **packages**, not applications |
| `@anthropic-ai` | npm scope | `Structure` |
| `sdk` | package name inside a dependency tree | **`Structure`, not `Identity`** |

**V2 verdict:** no ownership candidate at all. The correct explanation is:

```text
Path:  …\npm-cache\_npx\<id>\node_modules\@anthropic-ai\sdk
Classification: UNKNOWN
Structure: npm package-manager cache containing a dependency tree
WHY
  Structure
  + `npm-cache` is an npm package-manager cache root
  + `_npx` is npx's scratch space
  + `node_modules` is a dependency tree; `@anthropic-ai/sdk` is a package, not an application
  Identity
  - `sdk` accounts for 1 of 3 name tokens of "ASUS Aura SDK"; no provenance links ASUS Aura SDK to this path
Interpretation
  This is npm package cache content. AppTrace cannot attribute it to an
  installed application, and it is not evidence about ASUS Aura SDK.
```

Note what changed: Phase 0 said *"ASUS Aura SDK, HIGH"*. V2 says *"npm cache,
UNKNOWN"*, and explains the reasoning. The false HIGH is gone **and** the answer is
more useful.

### E.4 Worked example 2 — the NVIDIA case (Case 3)

```text
C:\Users\<user>\AppData\Local\NVIDIA Corporation\NVIDIA app\NvBackend\Recommendations\cities_skylines
```

| Segment | Semantic reading | Effect |
| --- | --- | --- |
| `NVIDIA Corporation` | vendor namespace (publisher token match) | candidates: NVIDIA products |
| `NVIDIA app` | exact product name | **provenance-backed ownership** if `NVIDIA App` registers this tree, or its binary is present |
| `NvBackend` | product component, vendor-internal | ownership continues |
| `Recommendations` | structure: product-generated content store | ownership continues |
| `cities_skylines` | leaf named after a **different** installed application (or game) | **relationship**, not ownership |

**V2 verdict:**

```text
Filesystem owner:  NVIDIA App            OWNED_BY (HIGH)
Related to:        Cities: Skylines      RELATED_TO
Classification:    HIGH (ownership by NVIDIA App); the named game is a relation
WHY
  Ownership
  + Ancestor `NVIDIA app` is a product-specific directory in the NVIDIA Corporation namespace
  + Registered/declared by NVIDIA App
  + `Recommendations` is product-generated content, not a directory the game writes to
  Relationship
  + Leaf name matches "Cities: Skylines"
  Contradictions
  - Cities: Skylines registers no directory under this path
Interpretation
  NVIDIA App data about Cities: Skylines. The game does not own these bytes.
```

### E.5 Worked example 3 — the Colossal Order case (Case 4)

```text
C:\Users\<user>\AppData\LocalLow\Colossal Order\Cities Skylines II
```

`Colossal Order` is the *developer*, not the publisher of the installed
`Cities: Skylines` (Paradox Interactive), and `Cities Skylines II` is a different
product that is **not installed**.

| Signal | Value |
| --- | --- |
| Name coverage against `Cities: Skylines` | `cities skylines ii` vs `cities skylines` — the directory is a **superset**, not a sub-run |
| Provenance | none: no install location, no binary, no registry reference |
| Contradiction | the leaf explicitly names a *different* product (`II`) |

**V2 verdict:** `UNKNOWN`, with an explicit note:

```text
Classification: UNKNOWN
WHY
  Identity
  + Parent `Colossal Order` is a game developer namespace
  - Leaf names "Cities Skylines II", which is not an installed application
  - Closest installed name "Cities: Skylines" is a different product (sequel marker present)
  Provenance
  - No installed application registers a directory at or above this path
Interpretation
  This is data for "Cities Skylines II", which AppTrace did not find installed.
  AppTrace will not reassign it to "Cities: Skylines".
```

This is the single most important behavioural rule in V2, and it needs an
explicit **near-miss detector**: when the best available candidate requires
*excluding* part of the directory name to match, the outcome is `UNKNOWN`, never
the nearest known name.

---

## F. Ownership / Relationship Model

### F.1 Answer: yes, distinguish — with exactly two relations, not seven

Task 02 suggested `OWNS / RELATED_TO / SHARED_BY / GENERATED_FOR / CACHE_OF /
DEPENDENCY_OF / UNKNOWN`. Most of these are not needed, because they are either
expressible as structure or as a property of the *bytes* rather than the relation:

* `CACHE_OF` and `DEPENDENCY_OF` are **structural kinds** (§E). "This directory is
  a cache" and "this directory is a dependency tree" are facts about the path that
  constrain *who may be credited*, and they do not need a distinct relation to an
  application.
* `GENERATED_FOR` is the only genuinely new semantic content in the NVIDIA case,
  and it is better captured as **a relation plus a reason** than as a separate
  relation type: NVIDIA App owns it; the reason it carries the game's name is
  recorded as structure (`Recommendations` content store) plus a relationship.
* `SHARED_BY` is already the `SHARED` classification with multiple accepted
  owners. It is a property of the location, not a distinct relation type.

Proposed minimal model — **two relations, one of which is optional**:

```text
OwnershipAssertion
    Path
    AppId
    Relation      : Owns | RelatedTo
    Classification
    Evidence[]
    StructureKind?         // why: what kind of storage this is
    BytesCovered           // accounting link (see §I.5)
    SupersededBy?          // a finer assertion further down (e.g. a product
                           // directory inside a vendor namespace)
```

and on the per-candidate side, replacing `CandidateOwner`:

```text
CandidateRelation
    AppId
    ProposedRelation : Owns | RelatedTo
    Evidence[]                      // now carrying EvidenceKind
    Specificity                     // 0..1
    Accepted / Rejected + RejectionReason
    Classification
```

### F.2 Rules that decide which relation applies

| Condition | Relation |
| --- | --- |
| Provenance names the app for this path or an ancestor, and no decisive counter-evidence | `Owns` |
| An ancestor is owned by app X, and this path's name matches app Y ≠ X, with no provenance for Y | `RelatedTo` (X owns, Y is named) |
| Structure says the segment is a package/dependency/cache name | no ownership; `RelatedTo` at most, and only if provenance exists |
| Several apps own it (genuine concurrent use) | `Owns` for each, location classified `SHARED` |
| Only a name match, no provenance | `RelatedTo`, classification `MEDIUM` at best |

### F.3 Why this is not a graph database

These are two relations on one entity type, held in a flat list. The "graph"
intuition is genuinely useful conceptually — `Application ──installed_at──>
Directory`, `Binary ──signed_by──> Publisher` — but the storage is a
`List<OwnershipAssertion>` plus the existing candidate list. No traversal engine,
no query language, no database. The smallest useful implementation is:

* the scan produces **ownership assertions** as a separate output stream (they do
  not participate in byte accounting, so they can exist for directories that emit
  no `FootprintItem`);
* relations are attached to the item/assertion that carries them;
* the reporter renders them as the two-block WHY shown in §E.4.

---

## G. Confidence Model

### G.1 Replace additive-everything with gates plus bounded scoring

Answer to Task 02 §16: **(C) gating rules plus scoring**, with two refinements:

* scoring is **per evidence kind**, and a kind's contribution is **capped**, so
  weak signals cannot accumulate into a strong claim;
* several **hard gates** run before any score is consulted.

This is what fixes "five weak signals should not outweigh one strong
contradiction": a contradiction is a *gate*, not a −25.

### G.2 The gates, in order

```text
G1  STRUCTURE EXCLUSION
    If the path is inside a PackageManagerCache / PackageDependencyTree /
    GamePlatformData structure, name-derived identity is disabled entirely.
    -> no ownership candidate may be generated from a name.

G2  CONTRADICTION
    If a specifically identified, differently-published application owns the
    nearest owned ancestor, the candidate cannot be an owner.
    -> relation becomes RelatedTo; ownership requires DECISIVE provenance.

G3  PROVENANCE
    Ownership above LOW requires at least one provenance record that names the
    application for this path (or an ancestor it owns).

G4  SPECIFICITY
    Identity-by-name is admitted only when name coverage >= 0.5 AND the matched
    run reaches a token that is not trailing generic vocabulary.
    Failing that, name evidence is recorded with Specificity < threshold and may
    only corroborate an already-provenanced candidate.

G5  BOUNDED SCORING WITHIN KIND
    Within Identity and within Provenance, evidence is summed with per-kind caps.
    Structure may only subtract. Contradictions were already handled by G2.
```

### G.3 Classification requirements

Redefined so that each level means something a cautious user can act on:

| Classification | Required | Explicitly not sufficient |
| --- | --- | --- |
| **CONFIRMED** | The application's own registration names this exact directory (`InstallLocation`, MSI product, MSIX package root), **and** no contradiction. A future runtime observation (Task 02 §13) may also reach this | A name match, however exact; a publisher namespace; a vendor directory |
| **HIGH** | A provenance anchor **plus** identity corroboration (exact/scoped name match with coverage ≥ 0.5, executable metadata, or signer agreement), with no contradiction | A name match plus generic context (this is exactly what produced all seven false positives) |
| **MEDIUM** | A single provenance anchor alone; or an exact-name match with no provenance but unambiguous structure | A one-token match against a multi-token name (`sdk`, `node`, `helper`) |
| **LOW** | Identity evidence below the specificity threshold, or structure-only indications | — may never close a subtree or be presented as an owner |
| **UNKNOWN** | No provenance, or the best explanation requires excluding part of the name (near-miss), or a structure that forbids identity | — |
| **SHARED** | Two or more applications each meet the HIGH bar for the same location, at equal strength | Several weak candidates |
| **AMBIGUOUS** | One candidate leads but a second also meets the MEDIUM bar, and no provenance resolves them | A single weak candidate |

**The operative sentence for `HIGH`:** *the application is independently known to
touch this path, and the name agrees.* Not *"the names look alike."*

### G.4 Consequences to accept

* Recall will fall. Measured: 116 of 196 accepted HIGH/CONFIRMED claims involve
  `NormalizedNameMatch`. Some of those are correct — `Microsoft\Edge` and
  `Microsoft\OneDrive` are correct at coverage 0.50 and would survive — but an
  unknown fraction are the false-positive class. The rest become MEDIUM/UNKNOWN,
  which is the intended trade under *"a false UNKNOWN is preferable to a false
  HIGH."*
* Some genuinely correct attributions will become MEDIUM until provenance
  enrichment supplies an anchor. **This is why identity enrichment (App Paths,
  services, tasks, shortcuts, `DisplayIcon`) is not an optional extra — it is what
  buys the recall back, honestly.**

---

## H. Architecture Proposal

```text
Application Discovery  (multi-source)
        |
        v
Identity Enrichment    (executables, signers, shortcuts, registered paths)
        |
        v
Index Build            (installLocation, binaryDirectory, publisher, exactName, nameToken)
        |
        v
Filesystem Scan        (unchanged: measurement, reparse safety, accounting)
        |
        v
Path Semantic Analysis (SegmentSemantics[] for the directory's ancestry)
        |
        v
Candidate Generation   (index lookups only -- never path x application)
        |
        v
Validation & Evidence Collection
        |     G1 structure exclusion
        |     G4 specificity / coverage
        v
Contradiction Checks   (G2: ancestor ownership, publisher mismatch, near-miss)
        |
        v
Provenance Gate        (G3)
        |
        v
Ownership / Relationship Resolution  (Owns vs RelatedTo)
        |
        v
Bounded Scoring & Classification     (G5)
        |
        v
WHY Explanation  (rendered from evidence, including rejected candidates)
```

Changes from the pipeline Task 02 sketched:

* **Path Semantic Analysis runs before candidate generation**, not after. Some
  structures must *prevent* candidates from existing (G1); discovering the
  structure afterwards means generating and then discarding nonsense.
* **Contradiction checks are a stage, not a detector.** A contradiction is a gate;
  in Phase 0 it was a −25 that could be outvoted.
* **Ownership/Relationship resolution precedes classification**, because the
  relation determines which classification ladder applies.

### H.1 Proposed types

| Type | Status | Note |
| --- | --- | --- |
| `EvidenceKind` | **new** | the six kinds of §C |
| `Evidence.Specificity` | **new** | 0..1, computed, never hand-listed |
| `StructureKind` | **new** | §E.2 |
| `SegmentSemantics` | **new** | one per path segment; computed once per directory from an ancestor cache |
| `OwnershipAssertion` | **new** | §F.1; carries `Owns`/`RelatedTo` |
| `ApplicationProvenance` | **new** | the set of paths/binaries a discovery source tied to an app |
| `CandidateRelation` | **replaces `CandidateOwner`** | adds `ProposedRelation`, `Specificity`, `RejectionReason` |
| `FootprintItem` | **kept** | add `OwnershipAssertions[]` and `SegmentSemantics[]` |
| `AppIdentity` | **extended** | add `Executables[]`, `SignerOrganization`, `RegisteredPaths[]`, `Sources[]` |
| `EvidenceWeights` | **kept, re-scoped** | weights become per-kind and capped, not global |
| Byte accounting | **unchanged** | see §I.5 |

---

## I. Migration From Phase 0

Incremental, in dependency order. Nothing is deleted before its replacement
passes the regression corpus.

### I.1 Types that remain unchanged

`FootprintItem` (shape), `ScanError`, `LocationCategory`, `Classification`,
`ScanLimits`/`ScanOptions`/`ScanResult`, `DirectoryWalker`, the entire accounting
model, `TextNormalizer`, `GenericDirectoryNames`, and the CLI surface.

### I.2 Types that change

* `Evidence` — add `Kind` and `Specificity`. Existing records map mechanically:
  `DeclaredInstallLocation`, `InstallLocationMatch`, `ProductCodeMatch`,
  `RegistryReference`, `ExecutableMetadataMatch`, name matches → **Identity or
  Provenance**; `KnownApplicationPath`, `KnownPublisherNamespace`,
  `SharedPublisherDirectory` → **Structure**; `ParentDirectoryMatch` → **Structure**
  (demoted — it was already the weakest idea); `PublisherMismatch`,
  `ConflictingApplicationMatch`, `MultipleCandidateOwners` → **Contradiction**.
* `CandidateOwner` → `CandidateRelation`.
* `Classification` — the enum is unchanged, but **every threshold is redefined per
  §G.3**. This is the behavioural change of V2 and the reason migration must be
  staged behind the corpus.

### I.3 Detectors: keep, change, remove

| Detector | Verdict |
| --- | --- |
| `CollectDeclaredInstallLocationEvidence` | **keep** — the decisive anchor |
| `CollectPublisherEvidence` | **keep**, re-kinded to Structure |
| `AddBoundingEvidence` (`SharedPublisherDirectory`) | **keep** |
| `CollectVendorNamespaceEvidence` | **keep** |
| `CollectContextEvidence` → split | **split**: the `KnownApplicationPath` bonus is **removed**; the `PublisherMismatch` half is **promoted** to a contradiction gate |
| `CollectNameEvidence` | **replace** — index-driven, coverage-gated, specificity-scored |
| `IsScopedNameMatch` bidirectional form | **remove** the reverse direction (the `adm-zip` → `360 Zip` mechanism), keep the forward anchored run |
| `ParentDirectoryMatch` bonus | **remove** as a confidence booster; **promote** to the ancestor ownership gate (G2) |
| `ExecutableProbe` | **keep and extend** — becomes a provenance anchor (binary present in the directory), plus signer read |
| `CollectExecutableMetadataEvidence` | **keep**, re-kinded to Identity evidence |
| New: structure analysis, index generation, contradiction stage, provenance gate | **add** (§K) |

### I.4 Weights

The existing weights remain useful **as relative strengths within a kind**, not as
a single global total. Concretely:

* keep the *ordering* (`DeclaredInstallLocation` > `InstallLocationMatch` >
  `ProductCodeMatch` > `ExactDirectoryNameMatch` > `NormalizedNameMatch` >
  publisher namespace);
* convert contradictions from large negative weights into **gates**;
* cap the total contribution of Identity and of Structure, so §A.2's 38 cannot be
  assembled from simulation of two different kinds.

### I.5 Accounting model

**Unchanged**, and deliberately so — the accounting contract was verified by tests
and is orthogonal to attribution quality. One addition:

> Ownership assertions are emitted **separately** from `FootprintItem`s, because
> ownership can be asserted for a directory that emits no item (all its bytes were
> itemised below it). This avoids disturbing `sum(exclusive) == total measured`
> while still letting an ancestor's ownership govern its children.

### I.6 Preserving Phase 0 behaviour during migration

Each V2 rule arrives behind a per-rule switch that defaults to the Phase 0
behaviour until the corpus (§J) shows no regression. This is what makes the
migration incremental rather than a rewrite, and it means the tool stays usable
throughout.

---

## J. Regression Corpus

Proposed cases. **Not implemented in this task.** Each asserts a *classification*
and, where relevant, the *relation*, so a rule cannot fix one case by breaking
another.

### J.1 Must become UNKNOWN or RELATED_TO (the reported false positives)

| # | Path | Phase 0 said | V2 must say | Deciding rule |
| --- | --- | --- | --- | --- |
| A | `…\Local\Doubao\User Data\sandbox_runtime\bases\<id>\node` | Node.js Krypton, HIGH | UNKNOWN (npm/runtime vocabulary, no provenance) | G1/G3 |
| B | `…\Local\npm-cache\_npx\<id>\node_modules\@types\node` | Node.js Krypton, HIGH | UNKNOWN (dependency tree) | G1 |
| C | `…\Local\npm-cache\_npx\<id>\node_modules\@anthropic-ai\sdk` | ASUS Aura SDK, HIGH | UNKNOWN (dependency tree) | G1/G4 |
| D | `…\Local\Teardown\promo\tool` | Epson … Tool, HIGH | UNKNOWN (coverage 0.17, no provenance) | G4/G3 |
| E | `…\Local\JianyingPro\Apps\…\QtQuick\Controls\Universal` | Universal Holtek, HIGH | UNKNOWN (framework segment; coverage 0.25) | G1/G4 |
| F | `…\Local\Programs\DSH Desktop\resources\app\node_modules\adm-zip` | 360 Zip, HIGH | UNKNOWN (dependency tree; trailing-token match) | G1 + reverse-run removal |
| G | `…\Local\Amazon Web Services\Amazon WorkSpaces\logs\helper` | ASUS Update Helper, HIGH | UNKNOWN (logs; coverage 0.33) | G1/G4 |

### J.2 Must become RELATED_TO, not ownership (Case 3)

| # | Path | Phase 0 said | V2 must say |
| --- | --- | --- | --- |
| H | `…\Local\NVIDIA Corporation\NVIDIA app\NvBackend\Recommendations\cities_skylines` | Cities: Skylines, HIGH (owns) | **NVIDIA App owns**; Cities: Skylines `RelatedTo` |

### J.3 Must stay UNKNOWN despite a near name (Case 4)

| # | Path | Phase 0 said | V2 must say |
| --- | --- | --- | --- |
| I | `…\LocalLow\Colossal Order\Cities Skylines II` | Cities: Skylines, HIGH | UNKNOWN — near-miss, sequel marker, no provenance |

### J.4 Must NOT regress (the Task 01 successes, plus the trustworthy half)

| # | Path | Expected |
| --- | --- | --- |
| V1 | `…\Local\Vivaldi` | Vivaldi, HIGH (coverage 1.00) |
| V2 | `…\Local\Google\Chrome` | Google Chrome, HIGH (coverage 0.50) |
| V3 | `…\Local\Kingsoft\WPS Office` | WPS Office, HIGH |
| V4 | `…\Local\Programs\Trae CN` | TraeCode CN, CONFIRMED |
| V5 | `…\Local\Programs\TRAE SOLO CN` | TraeWork CN, CONFIRMED |
| V6 | `…\Local\Microsoft\Edge` | Microsoft Edge, HIGH |
| V7 | `…\Local\Microsoft\OneDrive` | Microsoft OneDrive, HIGH |
| V8 | `…\Local\Adobe\Adobe Photoshop 2023` | Adobe Photoshop 2023, CONFIRMED |
| V9 | `…\Program Files\Netease\MuMuPlayer` | MuMuPlayer, CONFIRMED |
| V10 | vendor namespace `…\Local\Google` with Chrome + Drive | SHARED (descends, not owned by one) |

### J.5 Rules the corpus must also protect

| # | Case | Expected |
| --- | --- | --- |
| R1 | `…\Program Files\Contoso\Contoso Studio` (one product, publisher dir) | not owned by `Contoso Studio` (publisher ≠ product) |
| R2 | `…\Program Files\Widgetco\Widget`, two Widgetco products | UNKNOWN, descent continues |
| R3 | `…\Program Files\Google` declared by Chrome **and** Drive | SHARED, never CONFIRMED for one |
| R4 | any path whose only evidence is `KnownApplicationPath` | never above LOW |
| R5 | five weak identity records, one contradiction | contradiction wins (gate, not −25) |
| R6 | a directory genuinely named `Cache` at `Program Files\Cache` | not excluded by structure (positional rule) |

### J.6 Corpus mechanics

Store as data, not code: a JSON file of `{path, expectedRelation, expectedClassification, reason}` plus the synthetic `AppIdentity` list, driven by one parameterised test. Then a change to any rule or weight is measured against all cases at once, and the Task 01 scan JSON can be re-scored in bulk to report precision per classification.

---

## K. Recommended Implementation Sequence

Each task is independently testable and leaves the tool working.

### Task 03 — Regression corpus and scoring harness (no behaviour change)

Encode §J as data plus a parameterised test that runs the **existing** engine and
records current outcomes. Add a bulk re-scorer that takes a scan JSON and reports
per-classification precision against a hand-labelled subset. *Deliverable:* a
failing-for-the-right-reasons test suite that measures, and a baseline number.
**Do this first** — every later task needs it to prove it did not regress V1–V10.

### Task 04 — Structural rules and the removal of false context

Introduce `EvidenceKind` and `StructureKind`; re-kind existing evidence; remove the
`KnownApplicationPath` bonus and the `ParentDirectoryMatch` boost; add positional
structure rules for `node_modules`, package-manager caches, logs, temp and
framework segments. *Effect:* A–G cannot be claimed as ownership. *Test:* J.1, J.5
R4/R6.

### Task 05 — Indexed candidate generation, coverage and specificity

Replace the path × application name loop with `nameTokenIndex`/`exactNameIndex`
lookups; remove the reverse direction of `IsScopedNameMatch`; implement name
coverage ≥ 0.5 and the specificity score; retain rejected candidates with reasons.
*Effect:* A–G are no longer even proposed; `adm-zip` → `360 Zip` becomes
impossible. *Test:* J.1, J.4 (no regression), plus a memory/scalability assertion.

### Task 06 — Ancestor ownership gate and the relationship model

Add `OwnershipAssertion`, emit assertions independently of accounting, and
implement the two-relation model with the ancestor gate and the near-miss
detector. *Effect:* H becomes `RelatedTo`; I becomes UNKNOWN. *Test:* J.2, J.3,
J.4 V10.

### Task 07 — Identity enrichment from cheap static sources

Read `DisplayIcon`, App Paths, services, scheduled tasks, Run keys and Start Menu
shortcuts into `ApplicationProvenance` and the `binaryDirectoryIndex`. *Effect:*
recall returns for the two-thirds of applications with no `InstallLocation`,
honestly.
*Test:* new corpus cases; measured delta in attributed bytes and in MEDIUM→HIGH
promotions.

### Task 08 — Gated confidence model

Implement G1–G5 and the redefined classification requirements. *Effect:* the
public meaning of HIGH changes to "independently known to touch this path".
*Test:* full corpus, plus a before/after report of how many claims moved down.

### Task 09 — Signer evidence (optional, if Task 07 shows a publisher gap)

Add the 4.0–4.3 ms offline embedded-certificate read as Identity corroboration
only; never as ownership. Must implement the measured catalog-signing caveat: the
fast path missed 7 of 60 catalog-signed files that full verification accepted, so
a missing embedded signature must **never** be reported as "unsigned".
*Test:* signer agreement/disagreement cases, unsigned binaries neutral,
`HashMismatch`/`UnknownError` non-fatal, catalog-signed files not misreported.
Defer if Task 07 closes the gap.

### Task 10 — MSIX package-data mapping

Replace the Phase 0 `%LOCALAPPDATA%\Packages` exclusion with exact
package-family-name attribution (measured 144 of 172 directories match an
installed package exactly). Small, self-contained, and it removes a deliberate
blind spot rather than adding a new guess.

### Deliberately not scheduled

Steam/other store providers (measured small payoff here, and it is a separate
concern from attribution), COM/CLSID enumeration (measured dead end — 80% of
server paths point into `C:\Windows`), file associations (797 keys → 54
executables), `RegisteredApplications` / `Classes\Applications` (unresolvable
resource strings; 86% of entries carry no command), USN/ETW runtime evidence, and
any GUI work. Each is a separate spike with its own evidence, and four of them are
excluded *because they were measured*, not because they were assumed.

### Measurement caveats

The metadata fraction is from n=100 and the signature fractions from n=77–120, not
the larger samples originally intended: the bounded directory walk found fewer
unique top-level executables than assumed. Treat those percentages as indicative,
not tight. All timings are single-run on a live machine and vary with CRL and
network reachability — the full-verification figure in particular is dominated by
cache state, which is why the median (24 ms) and mean (161 ms) differ by 7×.

---

## Task 04 as built — Evidence Model Foundation & Confidence Semantics

The first production change toward V2. It fixes the *semantics* of evidence and
confidence; it does not add the semantic layer, new provenance sources, or the
relationship model.

### The rule now enforced

> **Evidence must earn the strength of the claim it supports.**

Phase 0 summed heterogeneous records into one score, so `NormalizedNameMatch`
(+30) and `KnownApplicationPath` (+8) reached HIGH at 38 even though neither
record showed the application ever touched the path. Since Task 04 the
classification ladder reads evidence **kind**; the score only orders and
thresholds within it.

### Evidence kinds and how every existing type maps

One mapping lives in `EvidenceClassification`, so a new evidence type cannot be
added without a deliberate kind.

| Kind | Evidence types |
| --- | --- |
| **Identity** | `ExactDirectoryNameMatch`, `NormalizedNameMatch`, `ExecutableMetadataMatch` |
| **Provenance** | `DeclaredInstallLocation`, `InstallLocationMatch`, `DiscoveryLocationMatch`, `ProductCodeMatch`, `RegistryReference` |
| **Structure** | `KnownApplicationPath`, `KnownPublisherNamespace`, `ParentDirectoryMatch`, `ChildDirectoryMatch`, `GenericDirectoryName`, `MultipleCandidateOwners`, `SharedPublisherDirectory`, `PublisherMatch` |
| **Relationship** | `UnknownApplication` (reserved; no detector emits it yet) |
| **Contradiction** | `PublisherMismatch`, `ConflictingApplicationMatch`, `SystemManagedPath` |

Three of those placements are judgement calls worth recording:

* **`PublisherMatch` is Structure, not Identity.** It is emitted only for a
  directory that several installed products registered as their install location
  and that is named after their shared publisher. It identifies a *vendor
  namespace*, so it corroborates a candidate set without proving one owner.
* **`MultipleCandidateOwners` is Structure, not Contradiction.** Several
  applications being plausible owners is a reason to report SHARED or to keep
  descending, never a reason to say one of them is wrong. Ambiguity is not
  opposition, and forcing it into Contradiction would have made the engine deny
  ownership everywhere a vendor namespace exists.
* **`SharedPublisherDirectory` is Structure.** Same reasoning: it describes the
  location's state.

### Specificity

`Evidence.Specificity` is `0.0` for non-identifying or generic and `1.0` for fully
product-specific. Only name-derived identity evidence computes a value; everything
else defaults to `1.0`, which means *"specificity does not apply here"*, not
*"fully specific"*. Specificity can only ever **withhold** strength, never grant it.

The computation is **name coverage**: the fraction of the application's name
tokens the directory name accounts for. Measured values, taken from the engine's
own normalization:

| Directory | Application | Coverage |
| --- | --- | --- |
| `Vivaldi` | Vivaldi | 1.00 |
| `Chrome` | Google Chrome | 0.50 |
| `Edge` | Microsoft Edge | 0.50 |
| `OneDrive` | Microsoft OneDrive | 0.50 |
| `helper` | ASUS Update Helper | 0.50 |
| `sdk` | ASUS Aura SDK | 0.33 |
| `Universal` | Universal Holtek RGB DRAM | 0.25 |
| `node` | Node.js Krypton via nvm-windows | 0.20 |
| `tool` | Epson Printer Driver Security Support Tool | 0.17 |

`0.50` is used as the floor for identity to corroborate provenance into HIGH. It
is **a discriminator fitted to the cases we have, not a statistical law** — and it
is only used to withhold strength. Note that two values differ from the Task 02
document because `NormalizeDisplayName` drops generic tokens: "ASUS Update Helper"
normalizes to `asus helper` (0.50) and "NVIDIA App 11.0.9.251" loses both its
version and its `app` token. The figures above describe what the engine actually
compares.

### `KnownApplicationPath`

Weight changed from **+8 to 0**, and its kind to Structure. Being under
`AppData\Local`, `AppData\Roaming`, `LocalLow` or `Program Files` says that
applications store data there — true of nearly every path AppTrace inspects. The
record is still emitted so the WHY output can explain a location; it simply no
longer contributes to ownership confidence.

### Contradiction gating

A `ContradictionKind` distinguishes a **decisive** contradiction, which forbids the
claim outright, from a **limiting** one, which only caps confidence. Decisive:
`PublisherMismatch`, `ConflictingApplicationMatch`, `SystemManagedPath`. The gate
is checked before any score is consulted, so no accumulation of weak supporting
evidence can outvote it. Everything else is limiting.

### The classification ladder

| Condition | Result |
| --- | --- |
| A decisive contradiction is present | **UNKNOWN** — the gate short-circuits |
| The application's own registration names this exact directory (`DeclaredInstallLocation`) | **CONFIRMED** |
| Provenance **and** corroborating identity (specificity ≥ 0.50) | **HIGH** |
| Provenance without corroborating identity | **MEDIUM** |
| Identity without provenance | **MEDIUM** |
| Structure only, or supporting records below the above | **LOW** |

`MeetsOwnershipBar` is now *derived from* this ladder, so "may stop descending" and
"may claim confident ownership" can never drift apart: only HIGH and CONFIRMED
close a subtree. CONFIRMED semantics were preserved rather than flattened for
uniformity.

### Measured effect

| Metric | Before | After |
| --- | --- | --- |
| Corpus: wrong-owner HIGH/CONFIRMED | 11 of 20 | **0 of 4** |
| Real machine: wrong-owner HIGH/CONFIRMED | 2 of 22 | **1 of 15** |
| Corpus: HIGH/CONFIRMED claims | 20 | 4 |
| Corpus: desired outcomes met | 9 | 9 |
| Owner changes (corpus) | — | **0** |

Every name-only claim moved HIGH → MEDIUM; no owner was reassigned. On the real
machine the seven reported false positives (`sdk`, `node`, `tool`, `helper`,
`Universal`, `adm-zip` and the NVIDIA recommendation leaf) are now MEDIUM instead
of HIGH. The one remaining confidently-wrong claim is
`Program Files (x86)\Oxford University Press`, which is CONFIRMED because the
product declares that publisher-level directory as its install location — a
*provenance* problem, so it needs Task 05/07, not a confidence adjustment.

### Deliberately not done

No filesystem semantic layer (`node_modules`, `npm-cache`, DXCache, Squirrel,
Steam…), no new provenance sources (App Paths, services, tasks, Run keys,
shortcuts, signer lookup), no `Owns`/`RelatedTo` model, no per-rule feature flags,
no accounting changes. The known false positives are therefore still *candidates* —
they are simply no longer *confident* claims. Removing the candidates entirely is
Task 05's job.



> **Can AppTrace realistically become substantially better at understanding
> application ownership on an existing Windows installation using only local,
> first-run static evidence?**

**Yes, substantially — but the ceiling is "strongly evidenced", not "known".**
The measured evidence says the current engine is not near that ceiling for
reasons that are fixable, and also that the ceiling itself is real and lower than
one might hope.

**What can become reliable.** Ownership can be established with high confidence
whenever an application's own registration *or* an independently registered
artifact points at the path: `InstallLocation` (32% of entries here), App Paths
(45), services (56), scheduled tasks (96), Run keys (18), `DisplayIcon` (128),
shortcuts, MSI product data, MSIX package roots. That is several hundred
independent anchors on a modest machine, each of which is a *provenance* fact
rather than a name resemblance. Combined with structural semantics that suppress
name-reading inside package caches and dependency trees, and with a provenance
gate before HIGH, the observed false-positive class disappears by construction —
not by adding seven blacklist entries. On top of that, a 4.3 ms offline signer read
gives publisher corroboration at a cost that is actually payable.

**What will remain inherently ambiguous.**

* **Directories no static source mentions.** Most AppData folders are created by
  the application at first run, not by the installer. `AppData\Local\Vivaldi` is
  correctly identifiable because the name is distinctive and the publisher
  namespace matches — but a directory named `data` under `AppData\Local\Vendor`
  with no registered binary in it is genuinely undecidable from static evidence.
  UNKNOWN is the honest answer and must stay common.
* **Shared runtime and framework trees.** `Common Files`, shared runtimes and
  vendor namespaces are used by several products; SHARED is correct and a single
  owner would be wrong.
* **Data named after *other* products.** The NVIDIA case is not an edge case; it
  is a normal pattern (recommendation stores, plugin caches, per-game profiles).
  The relationship model makes this expressible and honest, but AppTrace will not
  always know whether the named product or the container owns the bytes.
* **Sequels, forks and renamed products** (the Colossal Order case) will keep
  producing near-misses. The rule is to refuse them, which means accepting recall
  loss on exactly the cases a user might most want answered.

**Where Windows simply does not preserve enough provenance.** There is no
per-directory creation record: NTFS does not store which process created a
directory, ACL owners reflect the writing *user* (measured: 18 of 20 sampled
`%LOCALAPPDATA%` directories owned by the user, 2 by Administrators), and the USN
journal — readable without elevation, as measured — is a change log that records
*that* something changed, not *which application* owns the result. For any
directory that (a) no installed application registered, (b) contains no
application binary, and (c) is not inside a recognized structure, **the
information required to attribute it to an application does not exist on disk**.
No amount of engineering on static evidence can recover it; the correct behaviour
is to say UNKNOWN, and the correct product response is to make UNKNOWN a
first-class, well-explained answer rather than a gap to be filled by guessing.

**What optional runtime observation would add.** Monitoring (ETW file events, a
service's own writes, USN journal deltas correlated with processes) would supply
exactly the missing fact: *which process created or wrote this path*. That converts
a whole class of today's UNKNOWNs into CONFIRMED, and it is the only mechanism
that does so. It should stay optional and later: AppTrace must be useful on first
launch, so runtime evidence can only ever be an additional, higher-confidence
source layered on a complete static baseline — never a prerequisite.

**The honest summary.** Static, first-run evidence can support *"this application
is independently known to touch this path, and the name agrees"* — a claim a
cautious user can act on. It cannot support *"this path belongs to this
application"* when nothing registered it, nothing executable lives in it, and its
name is generic. The strongest attribution the available evidence can support is
therefore: **high precision on a minority of well-registered locations, honest
UNKNOWN on the rest**, with the reasoning always shown. Phase 0's error was not
that it reached for the second claim; it was that it reached for it on the
strength of a folder being called `sdk`.
