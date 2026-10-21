using System.Diagnostics;
using System.Text.Json;
using Dropstone.Models;

namespace Dropstone.Services;

public sealed class InstallerService
{
    private static readonly string[] WorkerFiles =
    {
        "Sync.ps1",
        "StartHidden.vbs",
        "LaunchWithSync.vbs",
        "SyncNow.cmd"
    };

    /// <summary>
    /// Everything the installer creates inside the install folder. The uninstall
    /// removes exactly these items - never the whole folder - so a user who
    /// pointed Dropstone at an existing folder does not lose their files.
    /// </summary>
    private static readonly string[] CreatedItems =
    {
        "payload",
        "deployed.json",
        "install.json",
        "config.ini",
        "sync_log.txt",
        "install.log",
        "Sync.ps1",
        "StartHidden.vbs",
        "LaunchWithSync.vbs",
        "SyncNow.cmd"
    };

    public InstallOutcome Install(
        InstallRequest request,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var installDir = NormalizeInstallDir(request.InstallDir);
        var archivePath = Path.GetFullPath(request.ArchivePath);

        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("The selected payload file no longer exists.", archivePath);
        }

        using var installLock = AcquireInstallLock(installDir);

        Directory.CreateDirectory(installDir);

        // A replace is an install on top of an existing Dropstone installation.
        var existingManifest = DeploymentManifest.Read(installDir);
        var isReplace = existingManifest is not null ||
                        File.Exists(Path.Combine(installDir, "install.json"));

        Report(progress, "Extracting payload...", 8);
        var payloadDirectory = Path.Combine(installDir, "payload");
        var temporaryPayload = Path.Combine(installDir, ".payload.tmp");
        if (Directory.Exists(temporaryPayload))
        {
            Directory.Delete(temporaryPayload, recursive: true);
        }

        Directory.CreateDirectory(temporaryPayload);
        try
        {
            ArchiveExtractor.Extract(archivePath, temporaryPayload, cancellationToken);
            ArchiveExtractor.FlattenSingleRoot(temporaryPayload);
        }
        catch
        {
            TryDeleteDirectory(temporaryPayload);
            throw;
        }

        var newPayloadFiles = Directory
            .EnumerateFiles(temporaryPayload, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(temporaryPayload, file))
            .ToList();

        // Files that the previous payload deployed and that are not part of the
        // new payload are removed from the version folders, so a replace really
        // replaces instead of merging.
        var staleWarnings = new List<string>();
        if (isReplace && existingManifest is not null)
        {
            staleWarnings.AddRange(RemoveStaleDeployedFiles(existingManifest, newPayloadFiles, installDir));
        }

        // Swap the payload. The old folder is only removed once the new one is
        // in place, so a failure leaves the previous payload usable.
        var previousPayload = Path.Combine(installDir, ".payload.previous");
        TryDeleteDirectory(previousPayload);
        var hadPreviousPayload = Directory.Exists(payloadDirectory);
        if (hadPreviousPayload)
        {
            Directory.Move(payloadDirectory, previousPayload);
        }

        try
        {
            Directory.Move(temporaryPayload, payloadDirectory);
        }
        catch
        {
            if (hadPreviousPayload && !Directory.Exists(payloadDirectory))
            {
                TryMoveBack(previousPayload, payloadDirectory);
            }

            TryDeleteDirectory(temporaryPayload);
            throw;
        }

        TryDeleteDirectory(previousPayload);

        Report(progress, "Installing the Dropstone worker...", 30);
        CopyWorkerFiles(installDir);

        Report(progress, "Writing configuration...", 45);
        var configPath = Path.Combine(installDir, "config.ini");
        var config = IniConfig.Read(configPath);
        var versionsDir = FirstNonEmpty(request.VersionsDir, config.GetValueOrDefault("watchdir"));
        var launcherExe = FirstNonEmpty(request.LauncherExe, config.GetValueOrDefault("launcherexe"));
        if (!string.IsNullOrWhiteSpace(versionsDir))
        {
            config["watchdir"] = versionsDir;
        }
        if (!string.IsNullOrWhiteSpace(launcherExe))
        {
            config["launcherexe"] = launcherExe;
        }
        IniConfig.Write(configPath, config);

        Report(progress, "Registering scheduled task...", 58);
        ScheduledTask.Register(installDir);

        var (_, eventWatchWarning) = WmiWatcher.Ensure(versionsDir, progress, installDir);

        Report(progress, "Saving installer state...", 75);
        RegistrySettings.Save(installDir);

        var previousMarker = ReadMarker(installDir);
        var marker = new InstalledMarker
        {
            InstallDir = installDir,
            PayloadArchive = request.PayloadSource ?? archivePath,
            PayloadFileCount = Directory
                .EnumerateFiles(payloadDirectory, "*", SearchOption.AllDirectories)
                .Count(),
            LauncherExe = launcherExe,
            VersionsDir = versionsDir,
            InstalledTitle = FirstNonEmpty(request.InstalledTitle, previousMarker?.InstalledTitle),
            InstalledDescription = FirstNonEmpty(request.InstalledDescription, previousMarker?.InstalledDescription),
            InstalledAt = DateTimeOffset.Now,
            CreatedItems = CreatedItems.ToList()
        };
        File.WriteAllText(
            Path.Combine(installDir, "install.json"),
            JsonSerializer.Serialize(marker, JsonDefaults.Options));

        Report(progress, "Applying payload to existing versions...", 90);
        WriteInstallLog(
            installDir,
            $"{(isReplace ? "replace" : "install")} - payload {marker.PayloadFileCount} file(s), versions '{versionsDir}'");
        var warning = TryRunFirstSync(installDir, isReplace, versionsDir);
        if (eventWatchWarning is not null)
        {
            warning = warning is null
                ? eventWatchWarning
                : $"{warning} {eventWatchWarning}";
        }

        if (staleWarnings.Count > 0)
        {
            var stale = string.Join(" ", staleWarnings);
            warning = warning is null ? stale : $"{warning} {stale}";
        }

        return new InstallOutcome
        {
            PayloadFileCount = marker.PayloadFileCount,
            Warning = warning
        };
    }

    public UninstallOutcome Uninstall(
        string installDir,
        IProgress<ProgressUpdate>? progress = null,
        IReadOnlyList<DeployedPayloadFile>? deployedFiles = null)
    {
        var fullPath = Path.GetFullPath(installDir);
        var warnings = new List<string>();
        var removed = 0;

        Report(progress, "Removing scheduled task...", 15);
        if (!ScheduledTask.Delete())
        {
            warnings.Add("The scheduled task could not be removed.");
        }

        var (watchOk, watchWarning) = WmiWatcher.Remove(progress, fullPath);
        if (!watchOk && watchWarning is not null)
        {
            warnings.Add(watchWarning);
        }

        Report(progress, "Removing settings...", 60);
        RegistrySettings.Delete();

        Report(progress, "Deleting deployed files...", 75);
        if (deployedFiles is { Count: > 0 })
        {
            removed = DeployedPayloadScanner.Remove(deployedFiles, progress);
        }

        Report(progress, "Deleting files...", 85);
        // Files that the manifest knows about but that were not selected are
        // only removed when they still match the payload, so a file the user
        // modified is never deleted behind their back.
        var manifest = DeploymentManifest.Read(fullPath);
        if (manifest is not null)
        {
            var selected = new HashSet<string>(
                deployedFiles?.SelectMany(file => file.Locations) ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            foreach (var version in manifest.Versions)
            {
                foreach (var file in version.Files)
                {
                    var deployed = Path.Combine(version.Path, file);
                    if (!File.Exists(deployed) || selected.Contains(deployed))
                    {
                        continue;
                    }

                    var payload = Path.Combine(fullPath, "payload", file);
                    if (File.Exists(payload) && !SameContent(payload, deployed))
                    {
                        continue;
                    }

                    TryDeleteFile(deployed);
                }
            }
        }

        foreach (var item in CreatedItems)
        {
            var path = Path.Combine(fullPath, item);
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception)
            {
                warnings.Add($"Could not remove '{item}': {exception.Message}");
            }
        }

        // The folder itself is only removed when it is empty afterwards, so a
        // folder the user pointed Dropstone at keeps its other content.
        try
        {
            if (Directory.Exists(fullPath) && !Directory.EnumerateFileSystemEntries(fullPath).Any())
            {
                Directory.Delete(fullPath);
            }
            else if (Directory.Exists(fullPath))
            {
                warnings.Add(
                    $"The install folder was kept because it still contains other files: {fullPath}");
            }
        }
        catch (Exception exception)
        {
            warnings.Add($"Could not remove the install folder: {exception.Message}");
        }

        return new UninstallOutcome
        {
            RemovedFiles = removed,
            Warning = warnings.Count == 0 ? null : string.Join(" ", warnings)
        };
    }

    private static InstalledMarker? ReadMarker(string installDir)
    {
        try
        {
            var path = Path.Combine(installDir, "install.json");
            if (!File.Exists(path))
            {
                return null;
            }

            return System.Text.Json.JsonSerializer.Deserialize<InstalledMarker>(
                File.ReadAllText(path),
                JsonDefaults.Options);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Removes files that a previous payload deployed and that are not part of
    /// the new payload. A file that was modified by the user is kept.
    /// </summary>
    private static IEnumerable<string> RemoveStaleDeployedFiles(
        DeploymentManifest manifest,
        IReadOnlyCollection<string> newPayloadFiles,
        string installDir)
    {
        var warnings = new List<string>();
        foreach (var version in manifest.Versions)
        {
            foreach (var file in version.Files)
            {
                if (newPayloadFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var deployed = Path.Combine(version.Path, file);
                var payload = Path.Combine(installDir, "payload", file);
                if (!File.Exists(deployed))
                {
                    continue;
                }

                if (File.Exists(payload) && !SameContent(payload, deployed))
                {
                    warnings.Add(
                        $"'{file}' was modified in '{version.Path}' and was left in place.");
                    continue;
                }

                TryDeleteFile(deployed);
            }
        }

        return warnings;
    }

    private static bool SameContent(string a, string b)
    {
        try
        {
            var infoA = new FileInfo(a);
            var infoB = new FileInfo(b);
            if (infoA.Length != infoB.Length)
            {
                return false;
            }

            using var streamA = File.OpenRead(a);
            using var streamB = File.OpenRead(b);
            var bufferA = new byte[64 * 1024];
            var bufferB = new byte[64 * 1024];
            while (true)
            {
                var readA = streamA.Read(bufferA, 0, bufferA.Length);
                var readB = streamB.Read(bufferB, 0, bufferB.Length);
                if (readA != readB)
                {
                    return false;
                }

                if (readA == 0)
                {
                    return true;
                }

                if (!bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
                {
                    return false;
                }
            }
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private static void TryMoveBack(string source, string destination)
    {
        try
        {
            if (Directory.Exists(source) && !Directory.Exists(destination))
            {
                Directory.Move(source, destination);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private static IDisposable AcquireInstallLock(string installDir)
    {
        var name = "Local\\DropstoneInstall_" + StableHash(installDir);
        var mutex = new Mutex(false, name);
        var acquired = false;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.FromSeconds(120));
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            throw new InvalidOperationException(
                "Another Dropstone operation is already running for this folder. Try again in a moment.");
        }

        return new MutexReleaser(mutex);
    }

    private static string StableHash(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value.ToLowerInvariant());
        var hash = System.Security.Cryptography.MD5.HashData(bytes);
        return Convert.ToHexString(hash)[..16];
    }

    private sealed class MutexReleaser : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _disposed;

        public MutexReleaser(Mutex mutex) => _mutex = mutex;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // Ignore.
            }

            _mutex.Dispose();
        }
    }

    private static string NormalizeInstallDir(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            throw new InvalidOperationException("Choose an installation folder.");
        }

        var fullPath = Path.GetFullPath(installDir.Trim());
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Choose a subfolder, not the root of a drive.");
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static void CopyWorkerFiles(string installDir)
    {
        var sourceDirectory = Path.Combine(AppContext.BaseDirectory, "Worker");
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The installer's Worker folder is missing: {sourceDirectory}");
        }

        foreach (var fileName in WorkerFiles)
        {
            var source = Path.Combine(sourceDirectory, fileName);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException(
                    $"The installer is missing a required worker file: {fileName}",
                    source);
            }

            File.Copy(source, Path.Combine(installDir, fileName), overwrite: true);
        }
    }

    private static string? TryRunFirstSync(string installDir, bool isReplace, string? versionsDir)
    {
        var started = DateTimeOffset.Now;
        try
        {
            var powershell = Path.Combine(
                Environment.SystemDirectory,
                @"WindowsPowerShell\v1.0\powershell.exe");

            if (!File.Exists(powershell))
            {
                return "The first sync did not run because PowerShell was not found.";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = powershell,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = installDir
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.Combine(installDir, "Sync.ps1"));
            startInfo.ArgumentList.Add("-Quiet");
            // Do not wait forever for a sync that is already running (the
            // scheduled task may be working on the same folder).
            startInfo.ArgumentList.Add("-LockTimeoutSeconds");
            startInfo.ArgumentList.Add("120");
            // The user picked this payload, so it is applied to the version
            // folders. A replace must overwrite the files of the old payload.
            if (isReplace)
            {
                startInfo.ArgumentList.Add("-Force");
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return "The first sync could not be started.";
            }

            WriteInstallLog(installDir, $"first sync started (pid {process.Id})");

            // WaitForExit takes milliseconds, the budget is computed in seconds.
            var timeout = ComputeFirstSyncTimeout(installDir, versionsDir) * 1000;
            if (!process.WaitForExit(timeout))
            {
                // The sync is not killed: it keeps running in the background and
                // the next trigger completes the work. A killed sync used to
                // leave the version folders half patched.
                WriteInstallLog(
                    installDir,
                    $"first sync did not finish within {timeout / 1000} s - it continues in the background");
                return "The first sync is still running in the background; it will finish shortly. " +
                       "Check sync_log.txt in the install folder.";
            }

            var elapsed = (DateTimeOffset.Now - started).TotalSeconds;
            WriteInstallLog(installDir, $"first sync finished after {elapsed:F1} s with exit code {process.ExitCode}");

            return process.ExitCode switch
            {
                0 => null,
                2 => "The LeviLauncher 'versions' folder could not be found. " +
                     "Set watchDir in config.ini in the install folder. Check sync_log.txt for details.",
                3 => "Some payload files could not be copied into the LeviLauncher version folders. " +
                     "Check sync_log.txt in the install folder.",
                4 => "Another sync was already running; it applies the payload to the version folders.",
                _ => "The first sync returned an error. Check sync_log.txt in the install folder."
            };
        }
        catch (Exception exception)
        {
            WriteInstallLog(installDir, $"first sync failed: {exception.Message}");
            return $"The first sync could not run: {exception.Message}";
        }
    }

    /// <summary>
    /// Appends a line to install.log in the install folder. The log makes the
    /// installation traceable when something goes wrong.
    /// </summary>
    private static void WriteInstallLog(string installDir, string message)
    {
        try
        {
            var path = Path.Combine(installDir, "install.log");
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
            // Logging must never break the installation.
        }
    }

    /// <summary>
    /// A generous, size-aware budget. The old fixed 60 s budget killed the sync
    /// half way through a large payload. The budget scales with the number of
    /// files that have to be copied (payload files x version folders).
    /// </summary>
    private static int ComputeFirstSyncTimeout(string installDir, string? versionsDir)
    {
        const int minimumSeconds = 180;
        const int maximumSeconds = 60 * 60;
        try
        {
            var payloadDirectory = Path.Combine(installDir, "payload");
            if (!Directory.Exists(payloadDirectory))
            {
                return minimumSeconds;
            }

            var payloadFiles = Directory
                .EnumerateFiles(payloadDirectory, "*", SearchOption.AllDirectories)
                .ToList();
            var bytes = payloadFiles.Sum(file => new FileInfo(file).Length);

            var versionCount = 1;
            try
            {
                if (!string.IsNullOrWhiteSpace(versionsDir) && Directory.Exists(versionsDir))
                {
                    versionCount = Math.Max(1, Directory.EnumerateDirectories(versionsDir).Count());
                }
            }
            catch
            {
                versionCount = 1;
            }

            // measured rates: ~400 small files/s and ~3 MB/s
            var copies = payloadFiles.Count * versionCount;
            var byCount = (int)(copies / 400);
            var bySize = (int)((bytes * versionCount) / (3 * 1024 * 1024));
            var seconds = Math.Max(minimumSeconds, Math.Max(byCount, bySize) * 2 + 60);
            return Math.Min(seconds, maximumSeconds);
        }
        catch
        {
            return minimumSeconds;
        }
    }

    private static void Report(IProgress<ProgressUpdate>? progress, string message, double percent) =>
        progress?.Report(new ProgressUpdate { Message = message, Percent = percent });

    private static string? FirstNonEmpty(string? primary, string? fallback) =>
        !string.IsNullOrWhiteSpace(primary) ? primary : fallback;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of the temporary extraction folder.
        }
    }
}


