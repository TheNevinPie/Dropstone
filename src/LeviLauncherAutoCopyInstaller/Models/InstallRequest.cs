namespace LeviLauncherAutoCopyInstaller.Models;

public sealed class InstallRequest
{
    public required string InstallDir { get; init; }
    public required string ArchivePath { get; init; }
    public string? LauncherExe { get; init; }
    public string? VersionsDir { get; init; }
    public bool IsReplace { get; init; }
}

public sealed class InstallOutcome
{
    public int PayloadFileCount { get; init; }
    public string? Warning { get; init; }
}
