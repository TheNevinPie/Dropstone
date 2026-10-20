using Dropstone.Models;

namespace Dropstone.Services;

internal static class DeployedPayloadScanner
{
    private static readonly string[] VersionMarkers =
    {
        "Minecraft.Windows.exe",
        "Minecraft.Win10.DX11.exe"
    };

    private static readonly string[] ExcludedNames =
    {
        "config.ini",
        "sync_log.txt",
        "install.json",
        "stop.flag"
    };

    /// <summary>
    /// Lists the payload files that Dropstone deployed into the LeviLauncher
    /// version folders. The deployment manifest is the source of truth, so
    /// files that were deployed by an older payload are found as well.
    /// </summary>
    public static IReadOnlyList<DeployedPayloadFile> Scan(string installDir, string? versionsDir)
    {
        var manifest = DeploymentManifest.Read(installDir);
        if (manifest is not null)
        {
            var fromManifest = ScanFromManifest(manifest);
            if (fromManifest.Count > 0)
            {
                return fromManifest;
            }
        }

        return ScanFromPayload(installDir, versionsDir);
    }

    private static IReadOnlyList<DeployedPayloadFile> ScanFromManifest(DeploymentManifest manifest)
    {
        var results = new List<DeployedPayloadFile>();
        var grouped = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var version in manifest.Versions)
        {
            if (string.IsNullOrWhiteSpace(version.Path))
            {
                continue;
            }

            foreach (var file in version.Files)
            {
                var deployed = Path.Combine(version.Path, file);
                if (!File.Exists(deployed))
                {
                    continue;
                }

                if (!grouped.TryGetValue(file, out var locations))
                {
                    locations = new List<string>();
                    grouped[file] = locations;
                }

                locations.Add(deployed);
            }
        }

        foreach (var pair in grouped)
        {
            results.Add(new DeployedPayloadFile
            {
                RelativePath = pair.Key,
                Locations = pair.Value
            });
        }

        return results;
    }

    /// <summary>
    /// Fallback for installations that predate the deployment manifest: look for
    /// the current payload's files inside the version folders.
    /// </summary>
    private static IReadOnlyList<DeployedPayloadFile> ScanFromPayload(string installDir, string? versionsDir)
    {
        var payloadDirectory = Path.Combine(installDir, "payload");
        if (!Directory.Exists(payloadDirectory) ||
            string.IsNullOrWhiteSpace(versionsDir) ||
            !Directory.Exists(versionsDir))
        {
            return Array.Empty<DeployedPayloadFile>();
        }

        var versionFolders = Directory
            .EnumerateDirectories(versionsDir)
            .Where(IsVersionFolder)
            .ToList();
        if (versionFolders.Count == 0)
        {
            return Array.Empty<DeployedPayloadFile>();
        }

        var results = new List<DeployedPayloadFile>();
        foreach (var payloadFile in Directory.EnumerateFiles(payloadDirectory, "*", SearchOption.AllDirectories))
        {
            if (ExcludedNames.Contains(Path.GetFileName(payloadFile), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(payloadDirectory, payloadFile);
            var locations = versionFolders
                .Select(folder => Path.Combine(folder, relativePath))
                .Where(File.Exists)
                .ToList();
            if (locations.Count > 0)
            {
                results.Add(new DeployedPayloadFile
                {
                    RelativePath = relativePath,
                    Locations = locations
                });
            }
        }

        return results;
    }

    public static int Remove(IEnumerable<DeployedPayloadFile> files, IProgress<ProgressUpdate>? progress)
    {
        progress?.Report(new ProgressUpdate
        {
            Message = "Removing deployed payload files...",
            Percent = 92
        });

        var removed = 0;
        foreach (var file in files)
        {
            foreach (var location in file.Locations)
            {
                try
                {
                    File.Delete(location);
                    removed++;
                }
                catch
                {
                    // Removing deployed files is best effort.
                }
            }
        }

        return removed;
    }

    private static bool IsVersionFolder(string folder) =>
        VersionMarkers.Any(marker => File.Exists(Path.Combine(folder, marker)));
}
