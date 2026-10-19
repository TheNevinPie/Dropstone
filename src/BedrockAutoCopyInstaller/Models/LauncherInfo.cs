namespace BedrockAutoCopyInstaller.Models;

public sealed record LauncherInfo(string? ExePath, string? VersionsDir)
{
    public bool HasAnyData =>
        !string.IsNullOrWhiteSpace(ExePath) || !string.IsNullOrWhiteSpace(VersionsDir);
}
