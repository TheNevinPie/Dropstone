# Bedrock AutoCopy

A portable WinUI 3 installer for a small Windows tool that keeps **your own payload files** present in LeviLauncher version folders.

The installer ships **tooling only**. It does not include, download, or promote any game files, DLLs, or third-party payloads. You choose a local `.zip` or `.rar` archive to deploy.

## How it works

1. The installer detects LeviLauncher (known install paths, uninstall registry entries, running processes, and the launcher's `config.json`).
2. It checks whether AutoCopy is already installed.
3. On a fresh install it asks for an install folder, then for a payload archive.
4. The archive is extracted to `<install folder>\payload` and the worker files are copied next to it.
5. A per-user scheduled task runs a single-pass sync at logon and after app package deployment events. A manual `SyncNow.cmd` is also provided.
6. On the next launch, the installer offers **Replace payload** or **Uninstall** instead.

The sync copies payload files into each LeviLauncher version folder that does not already contain them, preserving relative paths. Existing files are not overwritten, so it behaves as an idempotent top-up.

## Requirements

- Windows 10 version 1809 or newer, x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build (the portable publish includes the runtime)
- LeviLauncher for the installed tool to find version folders

## Build

```powershell
.\build.ps1
```

The portable build is written to `publish\`. Run `publish\BedrockAutoCopyInstaller.exe`.

For a smaller framework-dependent build:

```powershell
.\build.ps1 -FrameworkDependent
```

## Install

1. Run `BedrockAutoCopyInstaller.exe`.
2. Confirm the detected LeviLauncher path (or locate `LeviLauncher.exe` manually).
3. Choose the install folder. The default is `%LOCALAPPDATA%\Bedrock-AutoCopy`.
4. Choose a `.zip` or `.rar` payload archive.
5. Click **Install**. The installer extracts the payload, registers the task, applies the payload to existing versions, and closes.

## Installed files

| Item | Purpose |
| --- | --- |
| `payload\` | Extracted payload files |
| `Sync.ps1` | Single-pass, idempotent sync |
| `StartHidden.vbs` | Hidden runner used by the scheduled task |
| `LaunchWithSync.vbs` | Syncs, then starts LeviLauncher |
| `SyncNow.cmd` | Manual sync |
| `config.ini` | Resolved versions folder and launcher path |
| `install.json` | Installer state used to detect an existing install |
| `sync_log.txt` | Created when the sync has something to report |

The scheduled task is named `Bedrock AutoCopy Sync`. It runs as the current user with least privilege.

## Uninstall

Run the installer again and choose **Uninstall**. This removes the scheduled task, the install folder, the payload, and the installer settings. LeviLauncher itself is not touched.

## Payload notes

- Archives may contain files at the root or inside one top-level folder; a single wrapper folder is flattened.
- Relative paths inside the archive are preserved when syncing.
- Files that already exist in a version folder are skipped unless `Sync.ps1 -Force` is run manually.
- The sync only targets version folders that contain `Minecraft.Windows.exe` or `Minecraft.Win10.DX11.exe`.

## Repository layout

```
src/BedrockAutoCopyInstaller/   WinUI 3 installer
  Worker/                            files copied into the install folder
build.ps1                            portable publish script
```

## Policy note

This repository contains a generic local file-deployment installer. It is not affiliated with LeviLauncher, Mojang, Microsoft, or NVIDIA, and it does not bundle or link to game files or other third-party content.

GitHub's Acceptable Use Policies and Terms of Service prohibit unlawful content, malware, and intellectual-property infringement. Tooling that copies user-provided local files is acceptable on its own; do not use or distribute this project to deliver pirated software, license-bypass files, or other infringing content. You are responsible for the payload you deploy and for complying with the licenses that apply to it.
