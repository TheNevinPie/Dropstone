using System.Diagnostics;
using System.Text.Json;
using Dropstone.Models;
using Microsoft.Win32;

namespace Dropstone.Services;

public static class LauncherDetector
{
    private static readonly string[] ProcessNames = { "LeviLauncher", "launcher-x64" };

    private static readonly string[] VersionMarkers =
    {
        "Minecraft.Windows.exe",
        "Minecraft.Win10.DX11.exe"
    };

    public static LauncherInfo? Detect()
    {
        foreach (var exePath in GetKnownExePaths())
        {
            if (File.Exists(exePath))
            {
                return FromExe(exePath);
            }
        }

        var registryExe = FindInRegistry();
        if (!string.IsNullOrWhiteSpace(registryExe))
        {
            return FromExe(registryExe);
        }

        var runningExe = FindRunningExe();
        if (!string.IsNullOrWhiteSpace(runningExe))
        {
            return FromExe(runningExe);
        }

        var versionsDir = FindVersionsDir(exePath: null);
        return versionsDir is null ? null : new LauncherInfo(null, versionsDir);
    }

    public static LauncherInfo FromExe(string exePath)
    {
        var fullPath = Path.GetFullPath(exePath);
        return new LauncherInfo(fullPath, FindVersionsDir(fullPath));
    }

    private static IEnumerable<string> GetKnownExePaths()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "LeviMC", "LeviLauncher", "LeviLauncher.exe");
            yield return Path.Combine(programFiles, "LeviLauncher", "LeviLauncher.exe");
        }

        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return Path.Combine(programFilesX86, "LeviLauncher", "LeviLauncher.exe");
        }

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Programs", "LeviLauncher", "LeviLauncher.exe");
            yield return Path.Combine(localAppData, "LeviMC", "LeviLauncher", "LeviLauncher.exe");
        }
    }

    private static string? FindInRegistry()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall is null)
                    {
                        continue;
                    }

                    foreach (var subKeyName in uninstall.GetSubKeyNames())
                    {
                        using var subKey = uninstall.OpenSubKey(subKeyName);
                        if (subKey is null)
                        {
                            continue;
                        }

                        var displayName = subKey.GetValue("DisplayName") as string;
                        if (displayName is null ||
                            displayName.IndexOf("LeviLauncher", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        var installLocation = subKey.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(installLocation))
                        {
                            var candidate = Path.Combine(installLocation, "LeviLauncher.exe");
                            if (File.Exists(candidate))
                            {
                                return candidate;
                            }
                        }

                        var displayIcon = subKey.GetValue("DisplayIcon") as string;
                        if (!string.IsNullOrWhiteSpace(displayIcon))
                        {
                            var candidate = displayIcon.Split(',')[0].Trim().Trim('"');
                            if (File.Exists(candidate) &&
                                candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                return candidate;
                            }
                        }
                    }
                }
                catch
                {
                    // Registry access can be denied; keep probing other locations.
                }
            }
        }

        return null;
    }

    private static string? FindRunningExe()
    {
        foreach (var processName in ProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        return path;
                    }
                }
                catch
                {
                    // Access to the process module may be denied.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the LeviLauncher 'versions' folder. The order matches the
    /// worker's resolution order (Sync.ps1) so both agree on the same folder:
    ///   1. the folder next to the launcher executable - the executable is the
    ///      ground truth for a portable installation
    ///   2. the launcher's own config.json (base_root)
    ///   3. %APPDATA%\levilauncher.exe\versions
    ///   4. %LOCALAPPDATA%\levilauncher.exe\versions
    ///   5. a running LeviLauncher process
    ///   6. a bounded scan of the drive roots (portable installs)
    /// A folder that already contains a game version wins over one that is
    /// still empty, but an empty folder is accepted when nothing better exists.
    /// </summary>
    private static string? FindVersionsDir(string? exePath)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(exePath))
        {
            var exeDirectory = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrWhiteSpace(exeDirectory))
            {
                AddCandidate(candidates, Path.Combine(exeDirectory, "versions"));
            }
        }

        AddCandidate(candidates, FindVersionsFromLauncherConfig());

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            AddCandidate(candidates, Path.Combine(appData, "levilauncher.exe", "versions"));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            AddCandidate(candidates, Path.Combine(localAppData, "levilauncher.exe", "versions"));
        }

        var runningExe = FindRunningExe();
        if (!string.IsNullOrWhiteSpace(runningExe))
        {
            var runningDirectory = Path.GetDirectoryName(runningExe);
            if (!string.IsNullOrWhiteSpace(runningDirectory))
            {
                AddCandidate(candidates, Path.Combine(runningDirectory, "versions"));
            }
        }

        AddCandidate(candidates, FindPortableVersions());

        foreach (var candidate in candidates)
        {
            if (LooksLikeVersionsDir(candidate))
            {
                return candidate;
            }
        }

        return candidates.FirstOrDefault();
    }

    private static void AddCandidate(List<string> candidates, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar);
            if (!candidates.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(trimmed);
            }
        }
        catch
        {
            // Ignore unusable candidates.
        }
    }

    private static bool LooksLikeVersionsDir(string path)
    {
        try
        {
            foreach (var child in Directory.EnumerateDirectories(path))
            {
                foreach (var marker in VersionMarkers)
                {
                    if (File.Exists(Path.Combine(child, marker)))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Ignore.
        }

        return false;
    }

    private static string? FindVersionsFromLauncherConfig()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            return null;
        }

        var configPath = Path.Combine(appData, "levilauncher.exe", "config.json");
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (document.RootElement.TryGetProperty("base_root", out var baseRootElement))
            {
                var baseRoot = baseRootElement.GetString();
                if (!string.IsNullOrWhiteSpace(baseRoot))
                {
                    return Path.Combine(baseRoot, "versions");
                }
            }
        }
        catch
        {
            // A malformed launcher config is not fatal; fall back to defaults.
        }

        return null;
    }

    /// <summary>
    /// Bounded scan of the drive roots for a portable LeviLauncher. Mirrors
    /// Find-PortableVersions in Sync.ps1.
    /// </summary>
    private static string? FindPortableVersions()
    {
        var skip = new[]
        {
            "Windows", "Program Files", "Program Files (x86)", "ProgramData",
            "$Recycle.Bin", "System Volume Information", "Recovery"
        };

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType == DriveType.Network)
            {
                continue;
            }

            var root = drive.RootDirectory.FullName;
            var bases = new List<string> { root };

            DirectoryInfo[] topDirectories;
            try
            {
                topDirectories = new DirectoryInfo(root)
                    .GetDirectories()
                    .Where(directory => !skip.Contains(
                        directory.Name,
                        StringComparer.OrdinalIgnoreCase))
                    .ToArray();
            }
            catch
            {
                continue;
            }

            bases.AddRange(topDirectories.Select(directory => directory.FullName));
            foreach (var top in topDirectories)
            {
                try
                {
                    bases.AddRange(top.GetDirectories().Select(directory => directory.FullName));
                }
                catch
                {
                    // Ignore unreadable folders.
                }
            }

            foreach (var basePath in bases)
            {
                foreach (var folderName in new[] { "levilauncher.exe", "levilauncher" })
                {
                    var candidate = Path.Combine(basePath, folderName, "versions");
                    if (Directory.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }
}
