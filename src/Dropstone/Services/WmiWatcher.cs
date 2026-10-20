using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Dropstone.Models;

namespace Dropstone.Services;

internal static class WmiWatcher
{
    public const string FilterPrefix = "DropstoneVersionsChanged";
    public const string ConsumerPrefix = "DropstoneRunSync";

    public static (bool Ok, string? Warning) Ensure(
        string? versionsDir,
        IProgress<ProgressUpdate>? progress = null,
        string? installDir = null)
    {
        if (string.IsNullOrWhiteSpace(versionsDir))
        {
            return (false,
                "The event watch was not created because the LeviLauncher versions folder was not known at install time. The payload will still be topped up at logon.");
        }

        progress?.Report(ProgressUpdate.Indeterminate("Enabling event watch (approve the Windows prompt)..."));
        return RunElevated(versionsDir, installDir);
    }

    public static (bool Ok, string? Warning) Remove(
        IProgress<ProgressUpdate>? progress = null,
        string? installDir = null)
    {
        progress?.Report(ProgressUpdate.Indeterminate("Removing event watch..."));
        return RunElevated(versionsDir: null, installDir);
    }

    private static string ObjectSuffix(string? installDir)
    {
        var scope = installDir ?? "default";
        var bytes = Encoding.UTF8.GetBytes(scope.ToLowerInvariant());
        var hash = System.Security.Cryptography.MD5.HashData(bytes);
        return Convert.ToHexString(hash)[..8];
    }

    private static string BuildScript(string resultPath, string? versionsDir, string? installDir)
    {
        var suffix = ObjectSuffix(installDir);
        var filterName = $"{FilterPrefix}_{suffix}";
        var consumerName = $"{ConsumerPrefix}_{suffix}";

        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine($"$result = '{EscapeForPowerShell(resultPath)}'");
        script.AppendLine(
            "function Save([string]$message) { try { Set-Content -LiteralPath $result -Value $message -Encoding ASCII } catch { } }");
        script.AppendLine("try {");
        script.AppendLine("  $ns = 'root\\subscription'");
        // remove our own objects, plus the legacy globally named ones
        script.AppendLine($"  $filterName = '{filterName}'");
        script.AppendLine($"  $consumerName = '{consumerName}'");
        script.AppendLine(
            "  Get-WmiObject -Namespace $ns -Class __FilterToConsumerBinding -ErrorAction SilentlyContinue | Where-Object { ($_.Filter -like ('*' + $filterName + '*')) -or ($_.Consumer -like ('*' + $consumerName + '*')) } | ForEach-Object { $_.Delete() }");
        script.AppendLine(
            "  Get-WmiObject -Namespace $ns -Class __EventFilter -Filter (\"Name='\" + $filterName + \"'\") -ErrorAction SilentlyContinue | ForEach-Object { $_.Delete() }");
        script.AppendLine(
            "  Get-WmiObject -Namespace $ns -Class CommandLineEventConsumer -Filter (\"Name='\" + $consumerName + \"'\") -ErrorAction SilentlyContinue | ForEach-Object { $_.Delete() }");

        if (!string.IsNullOrWhiteSpace(versionsDir))
        {
            var query = BuildQuery(versionsDir);
            var schtasks = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
            var commandLine = $"{schtasks} /run /tn \"{ScheduledTask.TaskName}\"";

            script.AppendLine("  $filter = Set-WmiInstance -Namespace $ns -Class __EventFilter -Arguments @{");
            script.AppendLine($"    Name = '{filterName}'");
            script.AppendLine("    EventNamespace = 'root/cimv2'");
            script.AppendLine("    QueryLanguage = 'WQL'");
            script.AppendLine($"    Query = '{EscapeForPowerShell(query)}'");
            script.AppendLine("  }");
            script.AppendLine("  $consumer = Set-WmiInstance -Namespace $ns -Class CommandLineEventConsumer -Arguments @{");
            script.AppendLine($"    Name = '{consumerName}'");
            script.AppendLine($"    CommandLineTemplate = '{EscapeForPowerShell(commandLine)}'");
            script.AppendLine("  }");
            script.AppendLine(
                "  Set-WmiInstance -Namespace $ns -Class __FilterToConsumerBinding -Arguments @{ Filter = $filter; Consumer = $consumer } | Out-Null");
        }

        script.AppendLine("  Save 'OK'");
        script.AppendLine("  exit 0");
        script.AppendLine("} catch {");
        script.AppendLine("  Save ('FAIL: ' + $_.Exception.Message)");
        script.AppendLine("  exit 1");
        script.AppendLine("}");
        return script.ToString();
    }

    private static string BuildQuery(string versionsDir)
    {
        // WQL string literals escape a backslash with a second backslash.
        var wqlPath = versionsDir
            .Replace("\\", "\\\\")
            .Replace("'", "\\'");

        return
            $"SELECT * FROM __InstanceModificationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_Directory' AND TargetInstance.Name = '{wqlPath}'";
    }

    private static (bool Ok, string? Warning) RunElevated(string? versionsDir, string? installDir)
    {
        var id = Guid.NewGuid().ToString("N");
        var resultPath = Path.Combine(Path.GetTempPath(), $"DropstoneWmi_{id}.txt");

        try
        {
            var script = BuildScript(resultPath, versionsDir, installDir);
            // The script travels as an encoded command instead of a file in the
            // temporary folder: there is no file that another process could
            // swap between writing and executing it.
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            var powershell = Path.Combine(
                Environment.SystemDirectory,
                @"WindowsPowerShell\v1.0\powershell.exe");

            var startInfo = new ProcessStartInfo
            {
                FileName = powershell,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-WindowStyle");
            startInfo.ArgumentList.Add("Hidden");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(encoded);

            Process? process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                return (false,
                    "The event watch was skipped because the Windows elevation prompt was declined. The payload will still be topped up at logon.");
            }

            if (process is null)
            {
                return (false, "The event watch step could not be started.");
            }

            using (process)
            {
                if (!process.WaitForExit(120_000))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // The process may already be exiting.
                    }

                    return (false, "The event watch step timed out.");
                }

                // The exit code decides the outcome. The result file only
                // carries the message for the user.
                if (process.ExitCode == 0)
                {
                    return (true, null);
                }
            }

            var detail = "The event watch step failed.";
            try
            {
                if (File.Exists(resultPath))
                {
                    var text = File.ReadAllText(resultPath).Trim();
                    if (text.Length > 0)
                    {
                        detail = text;
                    }
                }
            }
            catch
            {
                // Ignore.
            }

            return (false, detail);
        }
        catch (Exception exception)
        {
            return (false, $"The event watch step failed: {exception.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(resultPath))
                {
                    File.Delete(resultPath);
                }
            }
            catch
            {
                // The temporary file is harmless if it cannot be deleted.
            }
        }
    }

    private static string EscapeForPowerShell(string value) => value.Replace("'", "''");
}
