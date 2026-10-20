# Mutex behaviour: a trigger that arrives during a running sync must not be lost,
# and the mutex must not block an unrelated install.
. "$PSScriptRoot\..\lib\common.ps1"

function New-BigPayload {
    param($Sandbox, [int]$Megabytes = 400, [string]$Name = "big.bin")
    $path = Join-Path $Sandbox.Payload $Name
    $fs = [System.IO.File]::Create($path)
    $buf = New-Object byte[] (1MB)
    (New-Object Random 3).NextBytes($buf)
    for ($i = 0; $i -lt $Megabytes; $i++) { $fs.Write($buf, 0, $buf.Length) }
    $fs.Close()
    return $path
}

# ---------------------------------------------------------------- T4 a trigger during a running sync is not lost
$sb = New-Sandbox "mutex-trigger"
Set-Content -LiteralPath "$($sb.Payload)\small.dll" -Value "p"
New-BigPayload -Sandbox $sb -Megabytes 900 | Out-Null
Use-SandboxEnv $sb
$baseline = Measure-Command { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$($sb.Install)\Sync.ps1" -Quiet 2>&1 | Out-Null }
Restore-Env
Write-Host ("    (empty-sync baseline = " + [math]::Round($baseline.TotalSeconds, 2) + "s)")

Use-SandboxEnv $sb
$p1 = Start-Process -FilePath "powershell.exe" -WindowStyle Hidden -PassThru `
        -ArgumentList "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "$($sb.Install)\Sync.ps1", "-Quiet"
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline -and -not $p1.HasExited) {
    # wait until sync #1 is demonstrably copying
    if (@(Get-ChildItem "$($sb.Versions)\1.0.0" -Filter "big.bin" -ErrorAction SilentlyContinue).Count -gt 0) { break }
    Start-Sleep -Milliseconds 50
}
$running = -not $p1.HasExited
# a new version folder appears while sync #1 is still running
New-Item -ItemType Directory -Path "$($sb.Versions)\2.0.0" -Force | Out-Null
Set-Content -LiteralPath "$($sb.Versions)\2.0.0\Minecraft.Windows.exe" -Value "stub"
$sw = [Diagnostics.Stopwatch]::StartNew()
$r2 = Invoke-Sync $sb -Quiet
$sw.Stop()
$p1.WaitForExit(300000) | Out-Null
$patched = Test-Path -LiteralPath "$($sb.Versions)\2.0.0\small.dll"
$ok = $running -and $patched
Record-Result -Id "T4" -Test "a trigger during a running sync is not lost" -Pass $ok -Severity "Blocker" `
    -Detail ("sync#1 was running when sync#2 fired = $running ; sync#2 exit=$($r2.Exit) in " + [math]::Round($sw.Elapsed.TotalSeconds,1) + "s ; new version folder patched = $patched")
Assert-Sandboxed $sb | Out-Null

# ---------------------------------------------------------------- T11 the mutex is scoped per install
$sbA = New-Sandbox "mutex-a"
$sbB = New-Sandbox "mutex-b"
Set-Content -LiteralPath "$($sbA.Payload)\a.dll" -Value "p"
Set-Content -LiteralPath "$($sbB.Payload)\a.dll" -Value "p"
New-BigPayload -Sandbox $sbA -Megabytes 600 | Out-Null
Copy-Item "$($sbA.Payload)\big.bin" "$($sbB.Payload)\big.bin" -Force
Use-SandboxEnv $sbA
$p = Start-Process -FilePath "powershell.exe" -WindowStyle Hidden -PassThru `
        -ArgumentList "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "$($sbA.Install)\Sync.ps1", "-Quiet"
Start-Sleep -Milliseconds 1500
$sw = [Diagnostics.Stopwatch]::StartNew()
$rB = Invoke-Sync $sbB -Quiet
$sw.Stop()
$p.WaitForExit(300000) | Out-Null
$patchedB = Test-Path -LiteralPath "$($sbB.Versions)\1.0.0\a.dll"
$ok = $patchedB -and ($sw.Elapsed.TotalSeconds -gt 1.0)
Record-Result -Id "T11" -Test "the mutex is scoped per install, not per machine" -Pass $ok -Severity "Medium" `
    -Detail ("install B took " + [math]::Round($sw.Elapsed.TotalSeconds,1) + "s and deployed = $patchedB (B must not be blocked by A)")
Assert-Sandboxed $sbA | Out-Null
Assert-Sandboxed $sbB | Out-Null
