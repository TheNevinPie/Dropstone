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

    public static IReadOnlyList<DeployedPayloadFile> Scan(string installDir, string? versionsDir)
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
