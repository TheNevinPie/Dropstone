using System.Diagnostics;
using System.Text.Json;
using LeviLauncherAutoCopyInstaller.Models;
using Microsoft.Win32;

namespace LeviLauncherAutoCopyInstaller.Services;

public static class LauncherDetector
{
    private static readonly string[] ProcessNames = { "LeviLauncher", "launcher-x64" };

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

        var versionsDir = FindVersionsFromLauncherConfig() ?? FindDefaultVersionsDir();
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

    private static string? FindVersionsDir(string exePath)
    {
        var fromConfig = FindVersionsFromLauncherConfig();
        if (fromConfig is not null)
        {
            return fromConfig;
        }

        var exeDirectory = Path.GetDirectoryName(exePath);
        if (!string.IsNullOrWhiteSpace(exeDirectory))
        {
            var besideExe = Path.Combine(exeDirectory, "versions");
            if (Directory.Exists(besideExe))
            {
                return besideExe;
            }
        }

        return FindDefaultVersionsDir();
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
                    var versionsDir = Path.Combine(baseRoot, "versions");
                    if (Directory.Exists(versionsDir))
                    {
                        return versionsDir;
                    }
                }
            }
        }
        catch
        {
            // A malformed launcher config is not fatal; fall back to defaults.
        }

        return null;
    }

    private static string? FindDefaultVersionsDir()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            var appDataVersions = Path.Combine(appData, "levilauncher.exe", "versions");
            if (Directory.Exists(appDataVersions))
            {
                return appDataVersions;
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var localVersions = Path.Combine(localAppData, "levilauncher.exe", "versions");
            if (Directory.Exists(localVersions))
            {
                return localVersions;
            }
        }

        return null;
    }
}
