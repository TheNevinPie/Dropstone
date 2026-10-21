# Dropstone

> **Stable release.** Dropstone 1.0.0 is the first stable release. Review any payload before deploying it and keep backups of files you care about.

A portable WinUI 3 installer for a small Windows tool that keeps **your own payload files** present in LeviLauncher version folders.

The installer ships **tooling only**. It does not include, download, or promote any game files, DLLs, or third-party payloads. You choose a local `.zip`/`.rar` archive or a single `.dll`, or configure a payload URL.

## Download

Get the latest release from [github.com/TheNevinPie/Dropstone/releases](https://github.com/TheNevinPie/Dropstone/releases).

## How it works

1. Dropstone detects LeviLauncher (known install paths, uninstall registry entries, running processes, and the launcher's `config.json`).
2. It checks whether Dropstone is already installed under `%LOCALAPPDATA%\Dropstone`.
3. On a fresh install it asks for an install folder, then for a payload (archive or `.dll`).
4. The payload is extracted (or the `.dll` copied) to `<install folder>\payload` and the worker files are copied next to it.
5. A per-user scheduled task runs a single-pass sync at logon and after app package deployment events. A WMI event watch also runs the sync whenever the LeviLauncher `versions` folder changes, so a newly installed version folder is topped up within seconds. A manual `SyncNow.cmd` is also provided.
6. On the next launch, the installer offers **Replace payload** or **Uninstall** instead.

The sync copies payload files into each LeviLauncher version folder that does not already contain them, preserving relative paths. If a version folder was just created and the game executable is not there yet, the sync waits up to two minutes for the launcher to finish that folder. Other version folders are patched as soon as their own game executable appears; the sync no longer waits for every new folder before copying to any of them.

### What the sync does with a file that already exists

| Situation | Behaviour |
| --- | --- |
| The file does not exist | It is copied (and recorded in `deployed.json`). |
| The file exists with a **different size** | It is treated as an incomplete or stale copy and is **repaired**. |
| The file exists with the **same size** | It is left alone - a foreign file is never overwritten. |
| `Sync.ps1 -Force` | Every payload file is overwritten. |
| `Sync.ps1 -VerifyHash` | Files of the same size are compared by SHA256 and replaced when they differ. |

A copy is verified after it was written; an incomplete copy is deleted right away, so a truncated file can never survive a sync. The installer applies the payload with `-Force`, because the user picked that payload in that moment.

### The `versions` folder is never re-targeted silently

The folder configured in `config.ini` (`watchDir`) is **authoritative**. If it no longer exists, the sync stops with an error instead of looking for a different LeviLauncher and copying the payload there. The worker only searches for a folder when none is configured, and it only accepts a folder that actually contains a game version. When the launcher has moved, `sync_log.txt` says so - run **Replace payload** to point the sync at the new folder.

### Exit codes of `Sync.ps1`

| Code | Meaning |
| --- | --- |
| 0 | Success. |
| 1 | Fatal error (payload folder missing, ...). |
| 2 | The `versions` folder could not be resolved - nothing was touched. |
| 3 | Finished, but some files could not be copied. |
| 4 | Skipped because another sync was already running. |

The scheduled task waits for the sync and reports its exit code, so the task history and `sync_log.txt` show what actually happened.

## Deployment presets

A `config.ini` placed next to `Dropstone.exe` can prefill the installer for faster deployments. A release ZIP contains `Dropstone.exe` and a commented `config.ini` template.

```ini
installdir=%LOCALAPPDATA%\Dropstone
launcherexe=C:\path\to\LeviLauncher.exe
versionsdir=C:\path\to\levilauncher\versions
payloadmode=ask
payloadpath=payload.zip
payloadurl=https://example.com/payload.zip
```

| Key | Meaning |
| --- | --- |
| `installdir` | Default install folder. Environment variables are expanded. |
| `launcherexe` | Optional LeviLauncher executable to skip detection. |
| `versionsdir` | Optional LeviLauncher `versions` folder. |
| `payloadmode` | `ask`, `local`, `dll`, or `url`. |
| `payloadpath` | Payload archive or `.dll` for `local`/`dll` mode. Relative paths resolve against the config file. |
| `payloadurl` | Absolute `http`/`https` payload URL for `url` mode. |
| `title` | Heading on the one-click install screen. |
| `description` | Description on the one-click install screen. |
| `installedtitle` | Heading on the installed screen. Saved into the install folder so later launches show it. |
| `installeddescription` | Description on the installed screen. Also saved into the install folder. |

The preset only prefills the UI; the user still confirms the install. Presets do not bypass the payload/security rules. URL downloads are limited to 2 GB and run with a 10-minute timeout.

When `installdir` is set together with a valid payload (`payloadmode=local`/`dll` with an existing `payloadpath`, or `payloadmode=url` with a valid `payloadurl`), Dropstone skips the wizard and opens a one-click install screen that lists the chosen folders and payload behind a single **Install** button. Windows may still ask for administrator approval to register the sync task.

The `title`/`description` keys customize that one-click screen, and `installedtitle`/`installeddescription` customize the screen shown on later launches. The installed text is written into `install.json` inside the install folder, so it survives without `config.ini` next to the installed EXE.

The `config.ini` in the install folder is different: it is generated by Dropstone for the sync worker (`watchdir` and `launcherexe`) and should not be confused with the deployment preset next to the EXE.

## Requirements

- Windows 10 version 1809 or newer, x64
- No .NET or Windows App Runtime installation needed: the published EXE bundles both runtimes
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build
- LeviLauncher for the installed tool to find version folders

## Build

```powershell
.\build.ps1
```

The build publishes `publish\Dropstone.exe` and `publish\config.ini`, then creates `release\Dropstone-<version>-win-x64.zip` containing those two files. The EXE is fully self-contained: the .NET runtime and the Windows App SDK are bundled into the single file, which extracts to a temporary folder on launch. This makes the EXE larger (about 85 MB) but removes all runtime prerequisites.

## Install

1. Download and extract the latest release ZIP.
2. Optionally edit `config.ini` next to `Dropstone.exe`.
3. Run `Dropstone.exe` and confirm the detected LeviLauncher path (or locate `LeviLauncher.exe` manually).
4. Choose the install folder. The default is `%LOCALAPPDATA%\Dropstone`.
5. Choose a payload archive or `.dll`, or use the payload configured in `config.ini`.
6. Click **Install**. During the install Windows shows one elevation prompt ("Windows PowerShell") for the event watch; Dropstone extracts the payload, registers the task, applies the payload to existing versions, and shows the result. The window stays open until you click **Close**. If the prompt is declined, Dropstone finishes anyway and falls back to logon-triggered syncs.

## Installed files

| Item | Purpose |
| --- | --- |
| `payload\` | Extracted payload files |
| `Sync.ps1` | Single-pass, idempotent sync |
| `StartHidden.vbs` | Hidden runner used by the scheduled task (waits for the sync and returns its exit code) |
| `LaunchWithSync.vbs` | Syncs, then starts LeviLauncher |
| `SyncNow.cmd` | Manual sync |
| `config.ini` | Resolved versions folder and launcher path |
| `install.json` | Installer state used to detect an existing install |
| `deployed.json` | The payload files that were deployed, and where (used by Replace and Uninstall) |
| `install.log` | What the installer did, including the first sync |
| `sync_log.txt` | The worker's log |

The scheduled task is named `Dropstone Sync`. It runs as the current user with least privilege.

### Uninstall

Uninstall removes exactly the items that Dropstone created inside the install folder (`payload\`, the worker files, `config.ini`, `install.json`, `deployed.json`, the logs) and the files it deployed into the version folders. The install folder itself is only deleted when it is empty afterwards - if you pointed Dropstone at a folder that already had other files, those files and the folder stay. Files in the version folders that Dropstone never deployed are left alone as well.

## Event watch

To react to newly installed LeviLauncher versions immediately, Dropstone creates a permanent WMI event subscription while installing:

- `__EventFilter` `DropstoneVersionsChanged` watches the versions folder for child changes (including the staging-folder rename that completes an install).
- `CommandLineEventConsumer` `DropstoneRunSync` runs the `Dropstone Sync` task.
- A `__FilterToConsumerBinding` connects the two.

Creating this subscription requires administrator rights, which is why the installer shows one elevation prompt. The watch lives in `root\subscription` and is removed again on uninstall. If the versions folder later moves, run **Replace payload** in Dropstone to point the watch at the new location.

## Uninstall

Run Dropstone again and choose **Uninstall**. This removes the scheduled task, the event watch, the install folder, the payload, and the installer settings. LeviLauncher itself is not touched.

## Payload notes

- Archives may contain files at the root or inside one top-level folder; a single wrapper folder is flattened.
- Relative paths inside the archive are preserved when syncing.
- An archive may expand to at most 1 GB, and a single entry to at most 512 MB. A small archive that would expand to gigabytes is rejected.
- A URL payload may be at most 2 GB and has to finish within 30 minutes. The window stays closable while it downloads; closing it cancels the download.
- Only one Dropstone can run at a time. A second instance tells you and closes.
- The sync only targets version folders that contain `Minecraft.Windows.exe` or `Minecraft.Win10.DX11.exe`.

## Tests

`tests\` contains an executable stability suite. It runs against a sandboxed fake LeviLauncher and never writes into a real `versions` folder.

```powershell
.\tests\Run-Tests.ps1 -Quick   # worker tests, no elevation, no GUI
.\tests\Run-Tests.ps1 -Full    # everything, including the end-to-end installer tests
```

See `tests\README.md` for the details and the safety rules.

## Repository layout

```
src/Dropstone/                  WinUI 3 installer
  Worker/                       files copied into the install folder
packaging/config.ini            deployment preset template for releases
build.ps1                       portable publish and release ZIP script
```

## Policy note

This repository contains a generic local file-deployment installer. It is not affiliated with LeviLauncher, Mojang, Microsoft, or NVIDIA, and it does not bundle or link to game files or other third-party content.

GitHub's Acceptable Use Policies and Terms of Service prohibit unlawful content, malware, and intellectual-property infringement. Tooling that copies user-provided local files is acceptable on its own; do not use or distribute this project to deliver pirated software, license-bypass files, or other infringing content. You are responsible for the payload you deploy and for complying with the licenses that apply to it.
