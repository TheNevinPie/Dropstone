namespace Dropstone.Models;

public sealed class DeployedPayloadFile
{
    public required string RelativePath { get; init; }

    public required IReadOnlyList<string> Locations { get; init; }
}
