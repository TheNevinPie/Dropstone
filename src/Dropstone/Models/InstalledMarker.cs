namespace Dropstone.Models;

public sealed class InstalledMarker
{
    public string InstallDir { get; set; } = string.Empty;
    public string? PayloadArchive { get; set; }
    public int PayloadFileCount { get; set; }
    public string? LauncherExe { get; set; }
    public string? VersionsDir { get; set; }
    public string? InstalledTitle { get; set; }
    public string? InstalledDescription { get; set; }
    public DateTimeOffset InstalledAt { get; set; }

    /// <summary>
    /// The items Dropstone created inside the install folder. The uninstall
    /// removes exactly these and never the folder itself.
    /// </summary>
    public List<string> CreatedItems { get; set; } = new();
}
