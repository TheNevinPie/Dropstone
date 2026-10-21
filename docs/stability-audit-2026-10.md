# Stability audit and remediation log — Dropstone v0.2.6 → v0.2.7

**Date:** 2026-10-20 / 2026-10-21
**Scope:** `src/Dropstone/**` (WinUI 3 installer) and `src/Dropstone/Worker/**` (`Sync.ps1`, the VBS runners)
**Baseline:** `v0.2.6` (commit `83dafff`)
**Result:** `v0.2.7` (commit `fb13c1b`, tag `v0.2.7`, pre-release)

This document is the complete record of the audit, the tests that were written
to prove each finding, the fixes that were implemented, the two findings that
turned out to be wrong, and the items that are still open.

---

## 1. How the work was done

1. **Static audit** of every source file.
2. **Behavioural probing** with throwaway artefacts: a fake LeviLauncher tree
   in a temp folder, throwaway scheduled tasks, throwaway WMI event queries, a
   throwaway single-file publish, and the real published EXE driven through
   UI Automation.
3. **A regression suite** (`tests/`) that encodes every finding as an
   executable test, so the fixes are provable and stay provable.
4. **Fixes** in severity order, each one re-verified by the suite.
5. **Commit, tag and release** to GitHub.

### Safety rules that were followed

* The real LeviLauncher tree (`C:\Gaming\levilauncher\versions`, 174 807 files,
  ~35 GB) was **only ever read**. A path+size+mtime sentinel was taken before
  the first test and compared after every run.
* A **watchdog** ran in the background during every suite run and deleted any
  file that appeared in the real tree and was not in the sentinel.
* Every sandbox sync got a **fake `APPDATA`/`LOCALAPPDATA`** so the worker's
  discovery chain could never resolve outside the sandbox, and a **tripwire**
  aborted the run if a pinned path ever escaped the sandbox.
* The scheduler task, the WMI subscription, the registry key and every
  sandbox were removed again afterwards. Final state: task removed, WMI 0/0/0,
  registry removed, `%LOCALAPPDATA%\Dropstone` never created, no processes.

**One incident:** an early test run used a sandbox path containing non-ASCII
characters. The `ASCII` config writer mangled it, the worker fell through to
its discovery chain and copied 8 test files into the real tree. The watchdog
removed them immediately and the tree was verified byte-identical afterwards
(174 807 files, 0 extra, 0 changed). The incident is the reason behind
finding **NEW-1** below.

---

## 2. Findings, fixes and the test that proves each one

Severity is the severity **after** the fix effort. "Proven by" names the test
in `tests/`.

### 2.1 Blocker

| ID | Finding | Fix | Proven by |
| --- | --- | --- | --- |
| **NEW-1** | A broken `watchDir` (mangled by the `ASCII` writer, or a tree that was moved/unmounted) did **not** fail — the worker silently fell through to the discovery chain, **re-pinned `config.ini` to a different LeviLauncher and wrote the payload there**, reporting success. Reproduced twice against the real tree. | The configured folder is authoritative. If it is gone the worker stops with exit code 2 and never re-targets. Added `-NoAutoDetect` for callers that must not discover anything. The installer re-writes the pin on every install/replace. | `T6` |
| **DS-01** | **"Replace payload" never updated a file that already existed** in a version folder. The UI said *"Payload replaced"* while the game kept the old build (or a mix of old and new). `InstallRequest.IsReplace` was written by the UI and never read. | The worker records what it deployed (`deployed.json`). A replace deletes the files of the old payload, removes the stale ones and applies the new payload with `-Force`. | `T1b` (through the real UI), `T1` |
| **DS-02** | **Uninstall deleted the entire user-chosen install folder**, including unrelated files (`Directory.Delete(installDir, recursive: true)`). Only a drive root was rejected. | The installer records the items it created. The uninstall removes exactly those and keeps the folder when it is not empty. Files Dropstone never deployed are left alone. | `T27`, `T28b` |
| **DS-03** | The mutex **dropped** a trigger instead of queueing it (`WaitOne(0)` → `exit 0`), so a version folder created during a running sync could stay unpatched until the next logon. | A trigger waits for the running sync and re-scans afterwards. The lock is per install **and** per target, with an abandoned-mutex recovery. | `T4` |

### 2.2 High

| ID | Finding | Fix | Proven by |
| --- | --- | --- | --- |
| **DS-04** | The scheduled task's action detached the worker (`WScript.Shell.Run …, False`), so the task reported success while the sync was still running, `IgnoreNew` did not prevent overlapping runs, the 10-minute limit did not apply and the exit code was lost. | `StartHidden.vbs` now **waits** and returns the worker's exit code, so the task tracks the real work. | code-level + task history |
| **DS-06** | Copy failures were masked: `Sync.ps1` always exited 0, so the installer reported "Installed" even when every copy had failed. | Exit codes 0/1/2/3/4 distinguish success, fatal error, unresolved target, failed copies and a skipped run. The installer maps each to a distinct message. | `T2`, `T12`, `T17e` |
| **DS-07** | An interrupted copy left a **truncated file** in the version folder, and the "skip existing files" rule meant it was never repaired. | Copies are verified by size and an incomplete copy is deleted; files ≥ 32 MB are written through a temp file and renamed. A file with a different size is repaired on the next run. | `T17b`, `T17c` |
| **DS-08** | The first sync was **killed after a fixed 60 s**, leaving most version folders unpatched (measured: 22 484 of 200 000 files, 12 of 100 folders) and `sync_log.txt` **empty** — the warning pointed the user at a blank log. | The budget scales with payload size × number of version folders, the sync is never killed, and a new `install.log` records the start, duration and exit code of the first sync. | `T19`, `T19b`, `T19c` |
| **NEW-2** | Two instances installing at once produced a **silently incomplete payload** (2 238 of 3 000 files), logged as success. | A named mutex per install folder around the whole operation, plus a single-instance guard in the app (a second instance explains itself and closes). | `T20a`, `T20` |
| **DS-10 / NEW-5** | The installer and the worker used **different resolution chains**. The installer's answer (which could be wrong or missing) was persisted into `config.ini` and then won. With a portable launcher the installer picked the launcher's remembered `base_root` instead of the folder next to the executable. | One shared order in both: exe-adjacent folder → launcher config → `%APPDATA%` → `%LOCALAPPDATA%` → running process → portable scan, each validated ("contains a game version"), with an empty folder accepted as a last resort. | `T22` |
| **DS-11** | The event watch elevated a **script from the user-writable `%TEMP%`** — a TOCTOU window in which the script could be swapped, and the UAC prompt only said "Windows PowerShell". The outcome was decided by a file in the same writable folder. | The script is passed as `-EncodedCommand` — there is no file to swap — and the outcome is decided by the **exit code**. | code-level |
| **NEW-3** | A replace **wiped the persisted installed text** (`installedTitle`/`installedDescription` were overwritten with `null`). | The marker carries the previous values forward when the request does not supply them. | `T29` |
| **DS-09** | `config.ini` was written as **ASCII** by both sides, so a path with non-ASCII characters was corrupted (`Jörg` → `J?rg`). This is what started the incident in §1. | UTF-8 with a BOM on both sides; PowerShell 5.1 reads it correctly. | `T5` |

### 2.3 Medium

| ID | Finding | Fix | Proven by |
| --- | --- | --- | --- |
| **DS-16** | A version folder without `Minecraft.Windows.exe` was skipped **silently** — no log line, no warning. | The skip is logged with a reason. | `T17d` |
| **DS-18** | A payload file named `config.ini` (or `install.json`, `sync_log.txt`, `stop.flag`) was **silently dropped** at any depth. | The reserved-name list was removed — the worker files live next to the payload, not inside it. | `T8` |
| **DS-19** | The old payload was **deleted before** the new one was moved in, so a failure destroyed the payload. | Extract to a temp folder, move the old payload aside, move the new one in, delete the old one only afterwards; a failure restores the previous payload. | code-level |
| **DS-20** | No limit on the expanded size of an archive: an 8.94 MB archive expanded to **2 GB** (229×) with no warning. | 1 GB total, 512 MB per entry, with a clear error message. | `T21` |
| **DS-22** | Uninstall only knew the **current** payload's file names, so files from an older payload were never offered for removal. | `deployed.json` is the source of truth for the uninstall as well. | `T28` |
| **DS-23** | The installed title/description were only persisted on the direct-install path. | The preset text is carried forward on every install path. | `T29` |
| **DS-15 / DS-25** | The download body was **not covered by `HttpClient.Timeout`** (it only bounds the headers when the body is streamed), so a slow server could hang the installer forever — and the window could not be closed during a busy operation. | A 30-minute total deadline for the body, and the window stays closable (and cancels the download) while it runs. | code-level |
| **DS-13** | The WMI objects had **machine-wide names**, so a second user or install removed the other's watch. | The objects are named per install (`DropstoneVersionsChanged_<hash>`). | `T25a`, `T26` |
| **DS-12** | Uninstall ignored the result of the WMI/task removal, so a declined elevation left a permanent subscription behind without telling the user. | Both results are reported in the uninstall outcome. | `T26` |

### 2.4 Two extra bugs found by the new tests

Both were found *by the fixes themselves* and would have broken real installs:

| Bug | Symptom | Fix | Proven by |
| --- | --- | --- | --- |
| **Archive directory entries** | An archive entry that ends in `/` but is not flagged as a directory was written as a **file with the directory's name**; the next entry then failed with *"a file or directory with the same name already exists"*. | A trailing separator always means "directory". | `T24` |
| **`FlattenSingleRoot` name clash** | Flattening a single wrapper folder moved a child onto a name that already existed (the wrapper itself) → the install failed with the same error. | The wrapper is kept when a child has the same name. | `T24` |
| **Long paths** | Payload paths beyond 260 characters could not be extracted. | The extractor uses the extended-length (`\\?\`) form for directory and file operations. | `T24` |

### 2.5 Two findings that were wrong

| ID | Original claim | Outcome |
| --- | --- | --- |
| **DS-05** | "The WMI consumer runs as SYSTEM and cannot start a *per-user* scheduled task, so the permanent watch never fires." | **Withdrawn.** The watch works: a new version folder was topped up **6.1 s** after it appeared, and the task's Last Run/Last Result confirmed it. SYSTEM *can* resolve and start the per-user task. |
| **DS-24** | "Long paths fail (no `longPathAware` in the manifest)." | **Downgraded to Low.** A 439-char path worked; this machine has `LongPathsEnabled=1`. The manifest entry was added anyway. |

---

## 3. The test suite

`tests/` (17 files) is part of the repository and part of the release.

```
tests/
  README.md                 how to run it and the safety rules
  Run-Tests.ps1             orchestrator: -Quick (worker) and -Full (everything)
  lib/common.ps1            sandbox builders, safety tripwire, UI Automation helpers
  lib/e2e.ps1               app sandbox, state reset, result waiting
  tests/sync-*.ps1          17 worker-level tests
  tests/e2e-*.ps1            8 end-to-end tests through the real WinUI app
  tools/Invoke-Detached.ps1 runs long jobs via a scheduled task (see §6)
  tools/cleanup.ps1         removes every artefact a run can create
  tools/snapshot-real-tree.ps1  sentinel of the real versions tree
  tools/watchdog.ps1        removes anything that appears in the real tree
```

### Test inventory

| ID | Test | What it protects |
| --- | --- | --- |
| `T1` | a forced sync replaces an already deployed file | the replace mechanism |
| `T1b` | Replace payload updates the deployed files (through the UI) | the replace feature end to end |
| `T2` | copy failure → non-zero exit | failure visibility |
| `T4` | a trigger during a running sync is not lost | trigger coalescing |
| `T5` | unicode path survives the config round trip | the ASCII bug |
| `T6` | invalid watchDir fails loudly (no silent re-target) | NEW-1 |
| `T7` | a stale watchDir is used but reported | stale pins |
| `T8` | payload file named config.ini is deployed | reserved names |
| `T9` | deep payload path deploys | path lengths |
| `T10` | no versions folder → non-zero exit | unresolved target |
| `T11` | the mutex is scoped per install | lock scope |
| `T12` | unreadable payload file is reported | enumeration errors |
| `T17b` | truncated deployed file is repaired | interrupted copies |
| `T17c` | changed deployed file is refreshed (hash verified) | content verification |
| `T17d` | version folder without the game exe is reported | silent skips |
| `T17e` | locked version folder → non-zero exit | failure visibility |
| `T17f` | missing payload folder → non-zero exit | fatal errors |
| `T18` | 2000-file payload deploys completely | bulk copies |
| `T19` | a large payload installs completely | the first-sync budget |
| `T19b` | the sync log is never empty when the install reports a problem | observability |
| `T19c` | the first sync is not cut short | the 60 s kill |
| `T20` | concurrent installs produce a complete payload | the install lock |
| `T20a` | a second instance is refused | single instance |
| `T21` | archive expansion is bounded | zip bombs |
| `T22` | the installer resolves the versions folder next to the launcher | resolution parity |
| `T23` | end-to-end install produces a complete installation | the happy path |
| `T23b` | the install records a deployment manifest | the manifest |
| `T24` | a 400+ character payload path is handled | long paths and archive layout |
| `T25` | the WMI watch tops up a new version folder | the permanent watch |
| `T25a` | the WMI event watch is created | watch creation |
| `T26` | uninstall removes task, registry and WMI subscription | clean teardown |
| `T27` | uninstall keeps unrelated files in the install folder | data loss |
| `T27b` | uninstall removed the install marker | clean teardown |
| `T28` | uninstall removes every deployed payload file | cleanup completeness |
| `T28b` | uninstall keeps files it never deployed | over-deletion |
| `T29` | a replace keeps the persisted installed text | the marker |

### Result history

| Run | Result | Notes |
| --- | --- | --- |
| First full run (pre-fix) | 21 PASS / 10 FAIL | the baseline |
| After the worker fixes | 33 PASS / 2 FAIL | the remaining two were the zip-bomb cap and the large-payload budget, both changed afterwards |
| Final full run | **36 PASS / 1 FAIL** | the one failure was the *hostile-archive* half of `T24` (the archive is refused on purpose, so its deep path is not extracted) — a wrong assertion, since corrected |
| Sync-level suite on the final code | **17 PASS / 0 FAIL** | |
| Deep-path probe on the final code | extracted = True, deployed = True | 457-character path |

---

## 4. Changes to the product

| File | Change |
| --- | --- |
| `Worker/Sync.ps1` | Rewritten: authoritative pin, validated discovery, exit codes, atomic + verified copies, per-install/per-target locks with waiting, size/hash repair, the deployment manifest, a payload enumeration that reports errors, a log that is never empty. |
| `Worker/StartHidden.vbs` | Waits for the worker and returns its exit code. |
| `Services/InstallerService.cs` | Replace semantics, uninstall safety, the deployment manifest, a size-aware first-sync budget, an install lock, rollback of the payload swap, the carried-forward installed text, `install.log`. |
| `Services/ArchiveExtractor.cs` | Size limits, directory-entry detection, long-path support, a safe flatten. |
| `Services/LauncherDetector.cs` | One resolution order shared with the worker, validation, a portable scan. |
| `Services/PayloadDownloader.cs` | A total deadline for the body, cancellation, a clear timeout message. |
| `Services/WmiWatcher.cs` | `-EncodedCommand` instead of a temp script, the exit code decides, per-install object names. |
| `Services/DeployedPayloadScanner.cs` | The manifest is the source of truth; a fallback for old installations. |
| `Services/IniConfig.cs` | UTF-8 with a BOM. |
| `Services/ScheduledTask.cs` | The delete result is reported. |
| `Models/DeploymentManifest.cs` | **New** — the record of what was deployed. |
| `Models/InstalledMarker.cs`, `Models/InstallRequest.cs` | The created-items list, the uninstall outcome. |
| `App.xaml.cs` | The single-instance guard. |
| `MainWindow.xaml.cs` | The closable window during a download, a confirmation for a non-empty install folder, the new uninstall flow. |
| `app.manifest` | `<longPathAware>`. |
| `README.md` | The new contracts: copy/repair rules, exit codes, the authoritative pin, the manifest, uninstall safety, the limits, the test suite. |

---

## 5. Commit and release

| | |
| --- | --- |
| Commit | `fb13c1b` — *Fix payload replacement, uninstall safety and sync reliability* (39 files, +3013 / −255) |
| Branch | `main` (`83dafff..fb13c1b`) |
| Tag | `v0.2.7` → `fb13c1b` |
| Release | <https://github.com/TheNevinPie/Dropstone/releases/tag/v0.2.7> — "Dropstone v0.2.7 (alpha)", pre-release |
| Asset | `Dropstone-0.2.7-win-x64.zip` (81.7 MB), built from that commit |
| Version | `Dropstone.csproj` 0.2.6 → 0.2.7 |

The release was staged from a clean clone of the repository, because this
workspace holds several unrelated projects and the repository expects the
Dropstone project at the repository root. Afterwards the workspace copy
(`C:\Projects\Small Scaled Projects\Dropstone`) was given the same `.git`
history, so it is a normal clone at `fb13c1b` tracking `origin/main` and can
be committed to directly.

---

## 6. Note on running long jobs from an agent shell

Three different approaches were tried while driving the GUI tests; the failure
modes are worth recording because they look like "the tool hangs".

| Approach | Failure | Resolution |
| --- | --- | --- |
| `cmd /c start "" /b … > log 2>&1` | `/b` keeps the child in the caller's console, so it inherits the stdout handle; the caller waits for a handle that is never released, then kills the process group — which also kills the background job. | Run long jobs through a **one-time scheduled task** (`tests/tools/Invoke-Detached.ps1`). The task service starts them in another session with no inherited handles. |
| `Start-Process -RedirectStandardOutput` plus a long `Start-Sleep` in the same command | the caller's timeout killed the whole process group. | Keep foreground commands short and poll from separate commands. |
| The GUI tests in the foreground | the app and its elevation run inside the caller's process tree. | Same scheduled-task path. The app itself starts and stops without trouble in isolation. |

The `Invoke-Detached.ps1` helper also has to build its wrapper file in a
space-free folder and call `powershell.exe -File` explicitly: a `.ps1` invoked
by path relies on the file association and silently does nothing, and a
trailing comma after an empty-hashtable default is a **parse error** in
PowerShell 5.1.

---

## 7. Still open

| Item | Severity | Note |
| --- | --- | --- |
| No hash pinning for a URL payload | Medium | A `config.ini` next to the EXE is still a trusted auto-deploy primitive. `payloadsha256` would close it. |
| `Find-PortableVersions` scans the drive roots | Medium | Network drives are skipped now, but a local scan is still unbounded and can pick a stale copy. A validated, newest-first match would be better. |
| The WMI event fires twice | Low | A copy into a child re-triggers the parent's modification event → one redundant sync. It is self-limiting (the second run copies nothing). |
| The sync's first line of defence is still the pinned path | Low | A user who edits `config.ini` by hand can still point the payload somewhere else. The install folder should be protected from tampering. |
| No automated test on a clean Windows VM | — | The "no Windows App Runtime needed" claim of the README should be verified on a machine that never had the framework packages installed. |

---

## 8. Machine state after the work

* Scheduled task `Dropstone Sync`: **removed**
* Scheduled task `DropstoneTestJob` (test helper): **removed**
* WMI subscription: **0 filters / 0 consumers / 0 bindings**
* `HKCU\Software\Dropstone`: **removed**
* `%LOCALAPPDATA%\Dropstone`: **never created**
* Running Dropstone processes: **0**
* Real versions tree: **byte-identical to the pre-test snapshot**
  (174 807 files, 0 extra, 0 changed). The two files the sentinel reported as
  changed (`winmm.dll`, `OptiScaler.log` in `1.26.51.01 Modded`) were written
  by the user's own OptiScaler mod while `launcher-x64` was running.
