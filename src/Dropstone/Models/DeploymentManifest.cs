using System.Text.Json;
using Dropstone.Services;

namespace Dropstone.Models;

/// <summary>
/// Records which payload files were deployed into which LeviLauncher version
/// folders. The worker writes it (deployed.json next to Sync.ps1); the
/// installer uses it to replace a payload correctly and to remove exactly the
/// files that Dropstone deployed during an uninstall.
/// </summary>
public sealed class DeploymentManifest
{
    public int Schema { get; set; } = 1;
    public string? GeneratedAt { get; set; }
    public string? Target { get; set; }
    public List<DeploymentVersion> Versions { get; set; } = new();

    public static DeploymentManifest? Read(string installDir)
    {
        var path = Path.Combine(installDir, "deployed.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DeploymentManifest>(
                File.ReadAllText(path),
                JsonDefaults.Options);
        }
        catch
        {
            return null;
        }
    }

    public void Delete(string installDir)
    {
        try
        {
            var path = Path.Combine(installDir, "deployed.json");
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

    /// <summary>All relative payload paths this manifest knows about.</summary>
    public IReadOnlyCollection<string> AllFiles() =>
        Versions.SelectMany(v => v.Files).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

public sealed class DeploymentVersion
{
    public string Path { get; set; } = string.Empty;
    public List<string> Files { get; set; } = new();
}
