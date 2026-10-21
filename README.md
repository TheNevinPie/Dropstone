# Dropstone

**Automatic payload deployment for Minecraft Bedrock installations managed by LeviLauncher.**

Dropstone is a lightweight Windows utility that detects LeviLauncher installations and automatically deploys your own modding files into Minecraft version folders. When a new version is installed, Dropstone synchronizes your configured payload so your custom files don't have to be copied manually after every game update.

[**Download Latest Release**](https://github.com/TheNevinPie/Dropstone/releases) · [Report a Bug](https://github.com/TheNevinPie/Dropstone/issues)

---

## Features

- **Automatic installation detection** — Locates LeviLauncher using known installation paths, registry entries, running processes, and launcher configuration.
- **Persistent file deployment** — Keeps your configured payload available across newly installed Minecraft versions.
- **Automatic update detection** — Watches the configured `versions` directory for changes and triggers synchronization when new version folders are created.
- **Safe-by-default synchronization** — Leaves existing files of the same size untouched unless forced or hash verification is enabled.
- **Integrity verification** — Verifies copied files and removes incomplete copies.
- **Custom payload support** — Deploy files from a local `.zip`, `.rar`, or individual `.dll`, or configure a remote HTTP/HTTPS payload URL.
- **Configurable installation** — Use a `config.ini` preset to customize the installer and simplify repeat deployments.
- **Manual synchronization** — Run `SyncNow.cmd` whenever you need to synchronize the payload yourself.
- **Clean uninstall** — Removes Dropstone's components and tracked deployed files without intentionally removing unrelated files.
- **Portable distribution** — A self-contained executable that does not require a separate .NET or Windows App Runtime installation.

## How It Works

Dropstone installs a small synchronization worker that manages the deployment of your chosen payload.

1. **Detect:** Identifies LeviLauncher and resolves its Minecraft `versions` directory.
2. **Configure:** Lets you choose an installation folder and supply a payload, or uses a deployment preset.
3. **Deploy:** Copies the payload into the Dropstone installation directory and synchronizes it with eligible Minecraft version folders.
4. **Monitor:** Registers a per-user scheduled task and a Windows Management Instrumentation (WMI) event watch to detect relevant changes.
5. **Synchronize:** When a new version becomes available, deploys missing or stale payload files while preserving their relative directory structure.
6. **Maintain:** Allows you to replace the payload or uninstall Dropstone by launching the installer again.

Newly created version folders may take a short time to become ready. Dropstone waits for the Minecraft executable to appear before targeting a folder, avoiding premature writes while the launcher is still creating the installation.

### Synchronization Behaviour

Dropstone uses a conservative synchronization strategy to avoid unnecessarily overwriting files.

| File condition | Default behaviour |
| --- | --- |
| Destination file is missing | Copies the payload file and records the deployment. |
| Destination file has a different size | Replaces it as a potentially stale or incomplete copy. |
| Destination file has the same size | Leaves it untouched. |
| `-Force` is enabled | Overwrites destination files with payload files. |
| `-VerifyHash` is enabled | Compares SHA-256 hashes and replaces files whose contents differ. |

Every copied file is verified after writing. If verification fails, the incomplete copy is removed rather than left behind.

The initial installation applies the selected payload with forced copying because the user explicitly selected that payload for deployment.

### Installation Path Safety

The configured `versions` directory is authoritative.

If the directory configured in `config.ini` no longer exists, Dropstone stops synchronization instead of silently switching to a different LeviLauncher installation. Check `sync_log.txt` for details, then use **Replace payload** to reconfigure the installation.

Automatic detection is used when no authoritative directory has been configured.

---

## Getting Started

### Requirements

- Windows 10 version 1809 or newer, x64.
- LeviLauncher installed with Minecraft Bedrock version folders.
- No separate .NET or Windows App Runtime installation is required for the published executable.

### Installation

1. Download the latest release from the [GitHub Releases page](https://github.com/TheNevinPie/Dropstone/releases).
2. Extract the release ZIP.
3. Optionally customize `config.ini` next to `Dropstone.exe`.
4. Run `Dropstone.exe`.
5. Confirm the detected LeviLauncher installation or locate it manually.
6. Choose the Dropstone installation directory and provide your payload.
7. Review the configuration and click **Install**.

The default installation directory is:

```text
%LOCALAPPDATA%\Dropstone
```

During installation, Windows may display an elevation prompt for registering the WMI event watch. If you decline it, installation can still complete, but automatic update detection falls back to logon-triggered synchronization.

### Supported Payloads

Dropstone accepts:

- `.zip` archives
- `.rar` archives
- A single `.dll` file
- A payload supplied through a configured HTTP/HTTPS URL

Archives can contain files at their root or inside one top-level directory. A single wrapper directory is flattened during extraction, while paths within the payload are preserved during deployment.

Dropstone only synchronizes into detected Minecraft version folders containing `Minecraft.Windows.exe` or `Minecraft.Win10.DX11.exe`.

---

## Configuration

Dropstone supports an optional `config.ini` beside `Dropstone.exe` for preconfiguring the installation experience.

### Example `config.ini`

```ini
installdir=%LOCALAPPDATA%\Dropstone
launcherexe=C:\path\to\LeviLauncher.exe
versionsdir=C:\path\to\levilauncher\versions

payloadmode=ask
payloadpath=payload.zip
payloadurl=https://example.com/payload.zip

title=Dropstone
description=Automatically deploy your custom Minecraft Bedrock files.
installedtitle=Dropstone Installed
installeddescription=Your payload is configured and ready to synchronize.
```

Replace the example paths and URL with your own values.

### Configuration Options

| Key | Description |
| --- | --- |
| `installdir` | Installation directory. Environment variables are expanded. |
| `launcherexe` | Optional path to `LeviLauncher.exe`, allowing detection to be skipped. |
| `versionsdir` | Optional path to the LeviLauncher `versions` directory. |
| `payloadmode` | Payload selection mode: `ask`, `local`, `dll`, or `url`. |
| `payloadpath` | Local archive or `.dll` path. Relative paths are resolved against the preset file. |
| `payloadurl` | Absolute HTTP/HTTPS URL used in `url` mode. |
| `title` | Heading displayed on the one-click installation screen. |
| `description` | Description displayed on the one-click installation screen. |
| `installedtitle` | Heading displayed when the installed tool is launched again. |
| `installeddescription` | Description displayed on the installed screen. |

### One-Click Installation

When `installdir` and a valid payload are configured together, Dropstone can display a one-click installation screen instead of the full configuration wizard.

A valid payload configuration is one of the following:

- `payloadmode=local` with an existing `payloadpath`.
- `payloadmode=dll` with an existing `payloadpath`.
- `payloadmode=url` with a valid `payloadurl`.

The screen presents the configured installation and payload details behind a single **Install** button.

The one-click screen still requires user confirmation and does not bypass payload validation or Windows security prompts.

The installed-screen title and description are saved in `install.json`, so they remain available on subsequent launches even if the original deployment preset is no longer present.

**Note:** The preset beside `Dropstone.exe` is different from the `config.ini` generated in the installation directory. The generated file stores the resolved launcher and `versions` paths used by the synchronization worker.

---

## Background Synchronization

Dropstone uses two mechanisms to maintain deployments.

### Scheduled Task

The per-user scheduled task, named `Dropstone Sync`, performs synchronization at logon and after relevant application package deployment events.

It runs under the current user account and uses the permissions required for its work.

### Version Directory Watch

Dropstone registers a WMI event subscription that monitors changes in the configured `versions` directory.

The subscription consists of:

- `DropstoneVersionsChanged` — Watches for child changes in the versions directory.
- `DropstoneRunSync` — Triggers the `Dropstone Sync` task.
- `__FilterToConsumerBinding` — Connects the event filter to its consumer.

This mechanism helps detect newly created or renamed version folders without requiring the user to launch Dropstone manually.

Registering the event subscription requires administrator approval. The subscription is removed during a normal uninstall.

### Manual Synchronization

To run a synchronization manually, launch:

```text
%LOCALAPPDATA%\Dropstone\SyncNow.cmd
```

If Dropstone was installed in a custom directory, use the corresponding installation path.

---

## Installed Files

The installation directory contains the payload, synchronization worker, and state files.

| File or directory | Purpose |
| --- | --- |
| `payload\` | Extracted payload files. |
| `Sync.ps1` | Single-pass synchronization worker. |
| `StartHidden.vbs` | Runs synchronization in the background and returns its exit code. |
| `LaunchWithSync.vbs` | Synchronizes before launching LeviLauncher. |
| `SyncNow.cmd` | Manually triggers synchronization. |
| `config.ini` | Resolved launcher and versions directory configuration. |
| `install.json` | Installer state and installed-screen settings. |
| `deployed.json` | Tracks deployed files and their target locations. |
| `install.log` | Installer activity, including the initial deployment. |
| `sync_log.txt` | Synchronization activity and errors. |

### Synchronization Exit Codes

| Exit code | Meaning |
| --- | --- |
| `0` | Synchronization succeeded. |
| `1` | Fatal error, such as a missing payload directory. |
| `2` | The versions directory could not be resolved; nothing was modified. |
| `3` | Synchronization finished, but some files could not be copied. |
| `4` | Synchronization was skipped because another instance was already running. |

The scheduled task waits for the synchronization process and reports its exit code. Use the task history and `sync_log.txt` when investigating a failed deployment.

---

## Replacing a Payload

Launch `Dropstone.exe` again after installation and choose **Replace payload**.

Use this option when you want to deploy a different set of files or when the configured LeviLauncher installation has moved.

Replacement reconfigures the selected payload and applies it to eligible version folders. Review the payload carefully before applying it, especially when it contains files that already exist in a game installation.

---

## Uninstalling

1. Launch `Dropstone.exe` again.
2. Select **Uninstall**.
3. Allow Dropstone to remove its registered background components and tracked deployed files.

Uninstallation removes the Dropstone scheduled task, WMI event subscription, worker files, payload directory, configuration, state files, and logs.

Dropstone removes files it deployed into Minecraft version folders according to its deployment records. Files it did not deploy are left alone.

The installation directory itself is deleted only if it is empty afterward. If it contains unrelated files, the directory and those files remain.

**Recommendation:** Keep backups of important files and review custom payloads before installation, replacement, or removal.

---

## Payload Limits and Restrictions

Dropstone applies validation and size limits to payloads.

- A downloaded payload URL is limited to 2 GB.
- An archive may expand to at most 1 GB.
- An individual archive entry may be at most 512 MB.
- Downloads have a bounded timeout and can be cancelled by closing the download window.
- Only one Dropstone instance can run at a time.
- Synchronization only targets version folders containing a recognized Minecraft executable.

These limits help prevent unexpectedly large payloads from consuming excessive disk space and reduce the risk of incomplete deployments.

---

## Building from Source

### Prerequisites

- Windows 10 version 1809 or newer, x64.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

### Build

Clone the repository and run the build script from PowerShell:

```powershell
git clone https://github.com/TheNevinPie/Dropstone.git
cd Dropstone

.\build.ps1
```

The build script publishes the application and produces a release ZIP containing the executable and deployment preset.

Expected outputs:

```text
publish\Dropstone.exe
publish\config.ini
release\Dropstone-<version>-win-x64.zip
```

The published executable bundles the .NET runtime and Windows App SDK components required by the application. This increases the executable size but removes the need for separate runtime installations.

---

## Tests

Dropstone includes an executable stability test suite under `tests\`.

The tests use a sandboxed fake LeviLauncher environment instead of writing into a real Minecraft `versions` directory.

Run the quick suite:

```powershell
.\tests\Run-Tests.ps1 -Quick
```

Run the full suite, including end-to-end installer tests:

```powershell
.\tests\Run-Tests.ps1 -Full
```

See [`tests/README.md`](tests/README.md) for testing details and safety rules.

---

## Repository Structure

```text
Dropstone/
├── src/
│   └── Dropstone/
│       └── Worker/              # Background synchronization components
├── packaging/
│   └── config.ini               # Deployment preset template
├── tests/                       # Stability and integration tests
├── build.ps1                    # Build and release packaging
└── README.md
```

---

## Security and Responsible Use

Dropstone is a generic file-deployment utility designed to help users manage their own modding files and customizations.

- Review every payload before deploying it.
- Only use files you trust and have permission to distribute or modify.
- Keep backups of important files before replacing existing payloads.
- Verify that your modifications are compatible with the Minecraft version being used.
- Do not use Dropstone to distribute malware, pirated software, license-bypass files, or other infringing content.

**Dropstone does not bundle third-party game files or payloads.** You are responsible for the content you supply and for complying with its applicable licenses and terms.

Dropstone is not affiliated with, endorsed by, or sponsored by LeviLauncher, Mojang Studios, Microsoft, or NVIDIA.

---

## License

See the repository's `LICENSE` file for applicable licensing terms.
