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

        Directory.CreateDirectory(installDir);

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

        if (Directory.Exists(payloadDirectory))
        {
            Directory.Delete(payloadDirectory, recursive: true);
        }
        Directory.Move(temporaryPayload, payloadDirectory);

        Report(progress, "Installing logic files...", 30);
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

        var (_, eventWatchWarning) = WmiWatcher.Ensure(versionsDir, progress);

        Report(progress, "Saving installer state...", 75);
        RegistrySettings.Save(installDir);
        var marker = new InstalledMarker
        {
            InstallDir = installDir,
            PayloadArchive = request.PayloadSource ?? archivePath,
            PayloadFileCount = Directory
                .EnumerateFiles(payloadDirectory, "*", SearchOption.AllDirectories)
                .Count(),
            LauncherExe = launcherExe,
            VersionsDir = versionsDir,
            InstalledTitle = request.InstalledTitle,
            InstalledDescription = request.InstalledDescription,
            InstalledAt = DateTimeOffset.Now
        };
        File.WriteAllText(
            Path.Combine(installDir, "install.json"),
            JsonSerializer.Serialize(marker, JsonDefaults.Options));

        Report(progress, "Applying payload to existing versions...", 90);
        var warning = TryRunFirstSync(installDir);
        if (eventWatchWarning is not null)
        {
            warning = warning is null
                ? eventWatchWarning
                : $"{warning} {eventWatchWarning}";
        }

        return new InstallOutcome
        {
            PayloadFileCount = marker.PayloadFileCount,
            Warning = warning
        };
    }

    public void Uninstall(string installDir, IProgress<ProgressUpdate>? progress = null)
    {
        var fullPath = Path.GetFullPath(installDir);

        Report(progress, "Removing scheduled task...", 15);
        ScheduledTask.Delete();

        WmiWatcher.Remove(progress);

        Report(progress, "Removing settings...", 60);
        RegistrySettings.Delete();

        Report(progress, "Deleting files...", 85);
        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
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

    private static string? TryRunFirstSync(string installDir)
    {
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

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return "The first sync could not be started.";
            }

            if (!process.WaitForExit(60_000))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // The process may already be exiting.
                }

                return "The first sync took too long and was stopped. Check sync_log.txt in the install folder.";
            }

            return process.ExitCode == 0
                ? null
                : "The first sync returned an error. Check sync_log.txt in the install folder.";
        }
        catch (Exception exception)
        {
            return $"The first sync could not run: {exception.Message}";
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
