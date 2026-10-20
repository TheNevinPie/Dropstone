# Dropstone stability suite

Executable regression tests for the Dropstone installer and the `Sync.ps1` worker.

Every test runs against a **sandboxed fake LeviLauncher tree**. The suite is
designed so that it can never write into a real LeviLauncher `versions` folder:

1. Each sandbox gets a fake `APPDATA`/`LOCALAPPDATA` containing a fake
   `levilauncher.exe\config.json`, so the sync's auto-discovery chain always
   resolves **inside the sandbox**.
2. After every operation the suite asserts that the pinned `watchdir` still
   points inside the sandbox (`Assert-Sandboxed`). If it ever escapes, the run
   aborts immediately. This is the guard for the *silent wrong-target write*
   bug (see `tests/sync-resolve.ps1`, `T6`).
3. `tools/watchdog.ps1` is an optional last-resort net that removes any file
   that appears inside the real versions tree and was not in the pre-test
   snapshot. Run it with `-Watchdog` (default on for the full suite).
4. `tools/snapshot-real-tree.ps1` records a sentinel of the real tree so the
   suite can prove at the end that nothing was modified.

## Running

```powershell
# sync-level tests only (fast, no elevation, no GUI)
.\Run-Tests.ps1 -Quick

# everything, including the end-to-end installer tests (needs elevation for
# the WMI event watch; the app itself is driven through UI Automation)
.\Run-Tests.ps1 -Full
```

Results are written to `tests\artifacts\results.csv` and summarised on screen.

## Test inventory

| File | Covers |
| --- | --- |
| `tests/sync-resolve.ps1` | versions-folder resolution: pinned path, stale pin, invalid pin, unicode pin, missing tree |
| `tests/sync-copy.ps1` | copy semantics: replace, exit codes, truncated files, changed files, locked targets, reserved names |
| `tests/sync-mutex.ps1` | trigger coalescing and mutex scope |
| `tests/sync-limits.ps1` | deep paths, payload size/lock behaviour |
| `tests/e2e-install.ps1` | full install through the real UI: task, WMI, first sync, manifest |
| `tests/e2e-replace.ps1` | "Replace payload" must actually update deployed files |
| `tests/e2e-uninstall.ps1` | uninstall must not delete unrelated files and must remove its own artefacts |
| `tests/e2e-watch.ps1` | the permanent WMI watch must fire on a new version folder |
| `tests/e2e-timeout.ps1` | a large payload must not leave a half-installed state |
| `tests/e2e-multi.ps1` | two instances must not corrupt the payload |
| `tests/e2e-bomb.ps1` | archive expansion must be bounded |
| `tests/e2e-resolution.ps1` | the app must resolve the same versions folder as the worker |

## Safety

* The suite **never** installs into `%LOCALAPPDATA%\Dropstone`; it always uses
  a temp sandbox and a deployment preset (`config.ini` next to a copy of the
  EXE) that pins `installdir`, `versionsdir` and `payloadpath`.
* `tools/cleanup.ps1` removes every artefact the suite creates (scheduled
  task, WMI subscription, registry key, sandboxes). Run it afterwards if a
  test was interrupted.
* The real LeviLauncher tree is only ever **read** (snapshot/compare).

## Notes

* The end-to-end tests need UI Automation, which works on a normal desktop
  session. Run them from an interactive session, not from a service.
* Elevation is obtained silently on machines with
  `ConsentPromptBehaviorAdmin=5`; on stricter policies the WMI test will
  simply report the declined elevation and be skipped.
