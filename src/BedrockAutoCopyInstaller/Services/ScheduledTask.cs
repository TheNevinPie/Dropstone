using System.Diagnostics;
using System.Security;
using System.Text;

namespace BedrockAutoCopyInstaller.Services;

internal static class ScheduledTask
{
    public const string TaskName = "Bedrock AutoCopy Sync";

    public static void Register(string installDir)
    {
        var userId = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var channel = "Microsoft-Windows-AppXDeploymentServer/Operational";
        var xpath = "*[System[(EventID=400 or EventID=603 or EventID=617)]]";
        var query =
            $"<QueryList><Query Id=\"0\" Path=\"{channel}\"><Select Path=\"{channel}\">{xpath}</Select></Query></QueryList>";
        var arguments =
            $"\"{Path.Combine(installDir, "StartHidden.vbs")}\" \"{Path.Combine(installDir, "Sync.ps1")}\" -Quiet";

        var xml = TaskXmlTemplate
            .Replace("__USER__", Escape(userId))
            .Replace("__QUERY__", Escape(query))
            .Replace("__ARGS__", Escape(arguments));

        var xmlPath = Path.Combine(
            Path.GetTempPath(),
            $"BedrockAutoCopySync_{Guid.NewGuid():N}.xml");

        try
        {
            File.WriteAllText(xmlPath, xml, Encoding.Unicode);
            var (exitCode, output) = Run(
                "/Create",
                "/TN", TaskName,
                "/XML", xmlPath,
                "/F");

            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The scheduled task could not be created (schtasks exit code {exitCode}).{Environment.NewLine}{output}");
            }
        }
        finally
        {
            try
            {
                File.Delete(xmlPath);
            }
            catch
            {
                // The temporary XML file is harmless if it cannot be deleted.
            }
        }
    }

    public static void Delete()
    {
        try
        {
            Run("/Delete", "/TN", TaskName, "/F");
        }
        catch
        {
            // Uninstall should continue when the task is already gone.
        }
    }

    private static (int ExitCode, string Output) Run(params string[] arguments)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start schtasks.exe.");

        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, $"{standardOutput}{Environment.NewLine}{standardError}".Trim());
    }

    private static string Escape(string value) =>
        SecurityElement.Escape(value) ?? string.Empty;

    private const string TaskXmlTemplate = """
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>One-shot top-up of the Bedrock AutoCopy payload at logon and after app package deployment events. Source: https://github.com/TheNevinPie/Bedrock-AutoCopy</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>__USER__</UserId>
      <Delay>PT30S</Delay>
    </LogonTrigger>
    <EventTrigger>
      <Enabled>true</Enabled>
      <Subscription>__QUERY__</Subscription>
      <Delay>PT60S</Delay>
    </EventTrigger>
  </Triggers>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>wscript.exe</Command>
      <Arguments>__ARGS__</Arguments>
    </Exec>
  </Actions>
  <Principals>
    <Principal id="Author">
      <UserId>__USER__</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
</Task>
""";
}
