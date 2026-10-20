# A large payload must not leave a half-installed state behind.
. "$PSScriptRoot\..\lib\e2e.ps1"

# 800 payload files x 40 version folders = 32 000 copies. That is more than the
# old fixed 60 s budget could finish - the budget now scales with the work.
$files = 800
$versions = 40
$sb = New-AppSandbox -Name "timeout"

# rebuild the sandbox with many version folders and a many-file payload
Remove-Item $sb.Root -Recurse -Force
foreach ($d in @("run", "install", "payload", "stage", "fake")) { New-Item -ItemType Directory -Path (Join-Path $sb.Root $d) -Force | Out-Null }
for ($v = 1; $v -le $versions; $v++) {
    $d = Join-Path $sb.Root ("versions\{0}.0.0" -f $v)
    New-Item -ItemType Directory -Path $d -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $d "Minecraft.Windows.exe") -Value "stub"
}
for ($i = 1; $i -le $files; $i++) { Set-Content -LiteralPath (Join-Path $sb.Root ("stage\f{0}.dat" -f $i)) -Value "x" }
$zip = Join-Path $sb.Root "payload\big.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $sb.Root "stage"), $zip, [System.IO.Compression.CompressionLevel]::Fastest, $false)
Remove-Item (Join-Path $sb.Root "stage") -Recurse -Force
Copy-Item (Get-PublishedExe) (Join-Path $sb.Root "run\Dropstone.exe") -Force
Set-AppPreset -Root $sb.Root -PayloadPath $zip

$p = Start-App -Sandbox $sb
Invoke-UiButton -ProcessId $p.Id -Name "Install" | Out-Null
$sw = [Diagnostics.Stopwatch]::StartNew()
$res = Wait-AppResult -ProcessId $p.Id -TimeoutSeconds 600
$sw.Stop()
Write-Host ("    install took " + [math]::Round($sw.Elapsed.TotalSeconds) + "s : " + $res.Title + " :: " + ($res.Message -replace "`r`n", " / "))

$deployed = @(Get-ChildItem (Join-Path $sb.Root "versions") -Recurse -File -Filter "f*.dat" -ErrorAction SilentlyContinue)
$touched = @($deployed | ForEach-Object { $_.Directory.Name } | Sort-Object -Unique).Count
$logSize = 0
if (Test-Path (Join-Path $sb.Install "sync_log.txt")) { $logSize = (Get-Item (Join-Path $sb.Install "sync_log.txt")).Length }
$installLog = ""
if (Test-Path (Join-Path $sb.Install "install.log")) { $installLog = (Get-Content (Join-Path $sb.Install "install.log") -Raw) }
$complete = ($deployed.Count -eq ($files * $versions))
$ok = $complete -and ($res.Title -eq "Installed") -and ($installLog -notmatch "did not finish")
Record-Result -Id "T19" -Test "a large payload installs completely" -Pass $ok -Severity "High" `
    -Detail ("deployed $($deployed.Count) of $($files*$versions) files into $touched of $versions folders in " + [math]::Round($sw.Elapsed.TotalSeconds) + "s ; sync_log.txt = $logSize bytes ; title='$($res.Title)'")
Record-Result -Id "T19b" -Test "the sync log is never empty when the install reports a problem" -Pass ($logSize -gt 0) -Severity "Medium" `
    -Detail ("sync_log.txt size = $logSize bytes (a warning that points at the log must find something in it)")
Record-Result -Id "T19c" -Test "the first sync is not cut short" -Pass ($installLog -notmatch "did not finish") -Severity "High" `
    -Detail ("install.log: " + (($installLog -split "`r?`n" | Where-Object { $_ -match "first sync" }) -join " | "))
Stop-App -ProcessId $p.Id

