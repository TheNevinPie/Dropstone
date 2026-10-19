using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Dropstone.Services;

internal static class WmiWatcher
{
    public const string FilterName = "DropstoneVersionsChanged";
    public const string ConsumerName = "DropstoneRunSync";

    public static (bool Ok, string? Warning) Ensure(
        string? versionsDir,
        IProgress<string>? progress = null,
        string? taskName = null)
    {
        if (string.IsNullOrWhiteSpace(versionsDir))
        {
            return (false,
                "The event watch was not created because the LeviLauncher versions folder was not known at install time. The payload will still be topped up at logon.");
        }

        progress?.Report("Enabling event watch (approve the Windows prompt)...");
        return RunElevated(versionsDir, taskName);
    }

    public static (bool Ok, string? Warning) Remove(IProgress<string>? progress = null)
    {
        progress?.Report("Removing event watch...");
        return RunElevated(versionsDir: null, taskName: null);
    }

    private static string BuildScript(string resultPath, string? versionsDir, string? taskName)
    {
        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine($"$result = '{EscapeForPowerShell(resultPath)}'");
        script.AppendLine(
            "function Save([string]$message) { try { Set-Content -LiteralPath $result -Value $message -Encoding ASCII } catch { } }");
        script.AppendLine("try {");
        script.AppendLine("  $ns = 'root\\subscription'");
        script.AppendLine(
            "  Get-WmiObject -Namespace $ns -Class __FilterToConsumerBinding -ErrorAction SilentlyContinue | Where-Object { ($_.Filter -like '*DropstoneVersionsChanged*') -or ($_.Consumer -like '*DropstoneRunSync*') } | ForEach-Object { $_.Delete() }");
        script.AppendLine(
            "  Get-WmiObject -Namespace $ns -Class __EventFilter -Filter \"Name='DropstoneVersionsChanged'\" -ErrorAction SilentlyContinue | ForEach-Object { $_.Delete() }");
        script.AppendLine(
            "  Get-WmiObject -Namespace $ns -Class CommandLineEventConsumer -Filter \"Name='DropstoneRunSync'\" -ErrorAction SilentlyContinue | ForEach-Object { $_.Delete() }");

        if (!string.IsNullOrWhiteSpace(versionsDir))
        {
            var query = BuildQuery(versionsDir);
            var schtasks = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
            var commandLine = $"{schtasks} /run /tn \"{taskName ?? ScheduledTask.TaskName}\"";

            script.AppendLine("  $filter = Set-WmiInstance -Namespace $ns -Class __EventFilter -Arguments @{");
            script.AppendLine("    Name = 'DropstoneVersionsChanged'");
            script.AppendLine("    EventNamespace = 'root/cimv2'");
            script.AppendLine("    QueryLanguage = 'WQL'");
            script.AppendLine($"    Query = '{EscapeForPowerShell(query)}'");
            script.AppendLine("  }");
            script.AppendLine("  $consumer = Set-WmiInstance -Namespace $ns -Class CommandLineEventConsumer -Arguments @{");
            script.AppendLine("    Name = 'DropstoneRunSync'");
            script.AppendLine($"    CommandLineTemplate = '{EscapeForPowerShell(commandLine)}'");
            script.AppendLine("  }");
            script.AppendLine(
                "  Set-WmiInstance -Namespace $ns -Class __FilterToConsumerBinding -Arguments @{ Filter = $filter; Consumer = $consumer } | Out-Null");
        }

        script.AppendLine("  Save 'OK'");
        script.AppendLine("} catch {");
        script.AppendLine("  Save ('FAIL: ' + $_.Exception.Message)");
        script.AppendLine("}");
        return script.ToString();
    }

    private static string BuildQuery(string versionsDir)
    {
        var wqlPath = versionsDir
            .Replace("\\", "\\\\")
            .Replace("'", "\\'");

        return
            $"SELECT * FROM __InstanceModificationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_Directory' AND TargetInstance.Name = '{wqlPath}'";
    }

    private static (bool Ok, string? Warning) RunElevated(string? versionsDir, string? taskName)
    {
        var id = Guid.NewGuid().ToString("N");
        var scriptPath = Path.Combine(Path.GetTempPath(), $"DropstoneWmi_{id}.ps1");
        var resultPath = Path.Combine(Path.GetTempPath(), $"DropstoneWmi_{id}.txt");

        try
        {
            File.WriteAllText(
                scriptPath,
                BuildScript(resultPath, versionsDir, taskName),
                Encoding.Unicode);

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
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);

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
            }

            if (!File.Exists(resultPath))
            {
                return (false, "The event watch step did not report a result.");
            }

            var result = File.ReadAllText(resultPath).Trim();
            if (string.Equals(result, "OK", StringComparison.Ordinal))
            {
                return (true, null);
            }

            return (false, string.IsNullOrWhiteSpace(result)
                ? "The event watch step failed."
                : result);
        }
        catch (Exception exception)
        {
            return (false, $"The event watch step failed: {exception.Message}");
        }
        finally
        {
            TryDelete(scriptPath);
            TryDelete(resultPath);
        }
    }

    private static string EscapeForPowerShell(string value) => value.Replace("'", "''");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The temporary file is harmless if it cannot be deleted.
        }
    }
}
