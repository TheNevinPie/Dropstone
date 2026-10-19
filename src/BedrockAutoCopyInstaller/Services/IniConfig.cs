using System.Text;

namespace BedrockAutoCopyInstaller.Services;

internal static class IniConfig
{
    public static Dictionary<string, string> Read(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return values;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = trimmed[..separator].Trim().ToLowerInvariant();
            var value = trimmed[(separator + 1)..].Trim().Trim('"');
            values[key] = value;
        }

        return values;
    }

    public static void Write(string path, IReadOnlyDictionary<string, string> values)
    {
        var lines = new List<string>
        {
            "# Bedrock AutoCopy configuration.",
            "# watchDir: the LeviLauncher 'versions' folder.",
            "# launcherExe: path to LeviLauncher.exe."
        };

        foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(pair.Value))
            {
                lines.Add($"{pair.Key}={pair.Value}");
            }
        }

        File.WriteAllLines(path, lines, Encoding.ASCII);
    }
}
