namespace Dropstone.Models;

public sealed class InstalledMarker
{
    public string InstallDir { get; set; } = string.Empty;
    public string? PayloadArchive { get; set; }
    public int PayloadFileCount { get; set; }
    public string? LauncherExe { get; set; }
    public string? VersionsDir { get; set; }
    public DateTimeOffset InstalledAt { get; set; }
}
