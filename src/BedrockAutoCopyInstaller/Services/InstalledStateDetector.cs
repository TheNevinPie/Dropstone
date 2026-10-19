using System.Text.Json;
using BedrockAutoCopyInstaller.Models;
using Microsoft.Win32;

namespace BedrockAutoCopyInstaller.Services;

public static class InstalledStateDetector
{
    public static string DefaultInstallDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bedrock-AutoCopy");

    public static InstalledMarker? Detect()
    {
        var candidates = new List<string>();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistrySettings.KeyPath);
            if (key?.GetValue("InstallDir") is string installDir &&
                !string.IsNullOrWhiteSpace(installDir))
            {
                candidates.Add(installDir);
            }
        }
        catch
        {
            // Ignore registry errors and fall back to the default location.
        }

        candidates.Add(DefaultInstallDir);

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var markerPath = Path.Combine(candidate, "install.json");
            if (!File.Exists(markerPath))
            {
                continue;
            }

            try
            {
                var marker = JsonSerializer.Deserialize<InstalledMarker>(
                    File.ReadAllText(markerPath),
                    JsonDefaults.Options);

                if (marker is not null)
                {
                    marker.InstallDir = Path.GetFullPath(candidate);
                    return marker;
                }
            }
            catch
            {
                // A damaged marker should not prevent a fresh install.
            }
        }

        return null;
    }
}
