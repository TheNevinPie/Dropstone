namespace Dropstone.Models;

public sealed class ProgressUpdate
{
    public const double IndeterminatePercent = -1;

    public required string Message { get; init; }

    public double Percent { get; init; } = IndeterminatePercent;

    public static ProgressUpdate Indeterminate(string message) =>
        new() { Message = message };
}
