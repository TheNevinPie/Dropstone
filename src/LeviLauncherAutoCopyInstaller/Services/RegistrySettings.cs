using Microsoft.Win32;

namespace LeviLauncherAutoCopyInstaller.Services;

internal static class RegistrySettings
{
    public const string KeyPath = @"Software\LeviLauncher-AutoCopy";

    public static void Save(string installDir)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath)
            ?? throw new InvalidOperationException("Could not create the installer settings key.");
        key.SetValue("InstallDir", installDir, RegistryValueKind.String);
    }

    public static void Delete()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
        }
        catch
        {
            // The install directory is still removed even if cleanup fails.
        }
    }
}
