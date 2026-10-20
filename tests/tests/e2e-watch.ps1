# The permanent WMI watch must fire when a new version folder appears.
. "$PSScriptRoot\..\lib\e2e.ps1"

$sb = New-AppSandbox -Name "watch" -Payload @{ "alpha.dll" = "payload-alpha" }
$p = Start-App -Sandbox $sb
Invoke-UiButton -ProcessId $p.Id -Name "Install" | Out-Null
$res = Wait-AppResult -ProcessId $p.Id -TimeoutSeconds 180
Write-Host ("    install: " + $res.Title + " :: " + ($res.Message -replace "`r`n", " / "))
$wmi = Get-WmiState
$ok = ($wmi.Consumers -ge 1)
Record-Result -Id "T25a" -Test "the WMI event watch is created" -Pass $ok -Severity "High" `
    -Detail ("filter/consumer/binding = $($wmi.Filters)/$($wmi.Consumers)/$($wmi.Bindings)")
Stop-App -ProcessId $p.Id

if ($wmi.Consumers -ge 1) {
    $before = Get-TaskState
    New-Item -ItemType Directory -Path (Join-Path $sb.Versions "2.0.0") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $sb.Versions "2.0.0\Minecraft.Windows.exe") -Value "stub"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $patched = $false
    while ($sw.Elapsed.TotalSeconds -lt 60) {
        Start-Sleep -Seconds 2
        if (Test-Path (Join-Path $sb.Versions "2.0.0\alpha.dll")) { $patched = $true; break }
    }
    $sw.Stop()
    $after = Get-TaskState
    Record-Result -Id "T25" -Test "the WMI watch tops up a new version folder" -Pass $patched -Severity "High" `
        -Detail ("payload reached the new version folder = $patched after " + [math]::Round($sw.Elapsed.TotalSeconds,1) + "s ; task last run '" + $before.LastRun + "' -> '" + $after.LastRun + "'")
} else {
    Record-Result -Id "T25" -Test "the WMI watch tops up a new version folder" -Pass $false -Severity "High" `
        -Detail "skipped: the WMI watch was not created (elevation declined?)"
}
