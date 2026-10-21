# Copy semantics: replace, exit codes, repair of damaged files, reserved names.
. "$PSScriptRoot\..\lib\common.ps1"

# ---------------------------------------------------------------- T1 replace semantics
# The installer performs a replace by removing the previously deployed files
# and syncing with -Force. This is the mechanism behind "Replace payload".
$sb = New-Sandbox "copy-replace"
Set-Content -LiteralPath "$($sb.Payload)\a.dll" -Value "v1-payload"
Invoke-Sync $sb -Quiet | Out-Null
Reset-Payload $sb
Set-Content -LiteralPath "$($sb.Payload)\a.dll" -Value "v2-payload"
Set-Content -LiteralPath "$($sb.Payload)\b.dll" -Value "v2-new"
$r = Invoke-Sync $sb -Quiet -Extra @("-Force")
$deployed = (Get-Content -LiteralPath "$($sb.Versions)\1.0.0\a.dll" -Raw).Trim()
$ok = ($deployed -eq "v2-payload") -and (Test-Path -LiteralPath "$($sb.Versions)\1.0.0\b.dll")
Record-Result -Id "T1" -Test "a forced sync replaces an already deployed file" -Pass $ok -Severity "Blocker" `
    -Detail ("deployed a.dll = '$deployed' (expected 'v2-payload') ; new file b.dll deployed = " + (Test-Path -LiteralPath "$($sb.Versions)\1.0.0\b.dll"))
Assert-Sandboxed $sb | Out-Null

# ---------------------------------------------------------------- T2 copy failure -> non-zero exit
$sb = New-Sandbox "copy-failure"
Set-Content -LiteralPath "$($sb.Payload)\a.dll" -Value "p"
& icacls "$($sb.Versions)\1.0.0" /deny "$env:USERNAME`:(OI)(CI)(W,D)" /T /Q | Out-Null
$r = Invoke-Sync $sb -Quiet
$deployed = Test-Path -LiteralPath "$($sb.Versions)\1.0.0\a.dll"
& icacls "$($sb.Versions)\1.0.0" /remove:d "$env:USERNAME" /T /Q | Out-Null
Record-Result -Id "T2" -Test "copy failure -> non-zero exit" -Pass ($r.Exit -ne 0) -Severity "High" `
    -Detail ("deployed = $deployed ; exit code = $($r.Exit) (expected non-zero)")
Assert-Sandboxed $sb | Out-Null

# ---------------------------------------------------------------- T17b truncated file is repaired
$sb = New-Sandbox "copy-truncated"
Set-Content -LiteralPath "$($sb.Payload)\game.dll" -Value "COMPLETE-PAYLOAD-CONTENT"
Set-Content -LiteralPath "$($sb.Versions)\1.0.0\game.dll" -Value "TRUNCATED"
Invoke-Sync $sb -Quiet | Out-Null
$after = (Get-Content -LiteralPath "$($sb.Versions)\1.0.0\game.dll" -Raw).Trim()
Record-Result -Id "T17b" -Test "truncated deployed file is repaired" -Pass ($after -eq "COMPLETE-PAYLOAD-CONTENT") -Severity "High" `
    -Detail ("file still contains '$after' (a truncated file must be re-copied)")
Assert-Sandboxed $sb | Out-Null

# ---------------------------------------------------------------- T17c changed file is refreshed
$sb = New-Sandbox "copy-changed"
Set-Content -LiteralPath "$($sb.Payload)\game.dll" -Value "AAAA"
Set-Content -LiteralPath "$($sb.Versions)\1.0.0\game.dll" -Value "BBBB"
Invoke-Sync $sb -Quiet -Extra @("-VerifyHash") | Out-Null
$after = (Get-Content -LiteralPath "$($sb.Versions)\1.0.0\game.dll" -Raw).Trim()
Record-Result -Id "T17c" -Test "changed deployed file is refreshed (hash verified)" -Pass ($after -eq "AAAA") -Severity "High" `
    -Detail ("file still contains '$after' (-VerifyHash must detect the difference)")
Assert-Sandboxed $sb | Out-Null

# ---------------------------------------------------------------- T17e locked target -> non-zero exit
$sb = New-Sandbox "copy-locked"
Set-Content -LiteralPath "$($sb.Payload)\a.dll" -Value "p"
& icacls "$($sb.Versions)\1.0.0" /deny "$env:USERNAME`:(OI)(CI)(W,D)" /T /Q | Out-Null
$r = Invoke-Sync $sb -Quiet
& icacls "$($sb.Versions)\1.0.0" /remove:d "$env:USERNAME" /T /Q | Out-Null
Record-Result -Id "T17e" -Test "locked version folder -> non-zero exit" -Pass ($r.Exit -ne 0) -Severity "High" `
    -Detail ("exit code = $($r.Exit) (expected non-zero)")
Assert-Sandboxed $sb | Out-Null

# ---------------------------------------------------------------- T17f missing payload folder
$sb = New-Sandbox "copy-nopayload"
Remove-Item -LiteralPath "$($sb.Payload)" -Recurse -Force
$r = Invoke-Sync $sb -Quiet
Record-Result -Id "T17f" -Test "missing payload folder -> non-zero exit" -Pass ($r.Exit -ne 0) -Severity "Medium" `
    -Detail ("exit code = $($r.Exit)")

# ---------------------------------------------------------------- T8 payload file named config.ini
$sb = New-Sandbox "copy-reserved"
Set-Content -LiteralPath "$($sb.Payload)\config.ini" -Value "game-config"
Set-Content -LiteralPath "$($sb.Payload)\a.dll" -Value "p"
Invoke-Sync $sb -Quiet | Out-Null
$deployed = Test-Path -LiteralPath "$($sb.Versions)\1.0.0\config.ini"
Record-Result -Id "T8" -Test "payload file named config.ini is deployed" -Pass $deployed -Severity "Medium" `
    -Detail ("deployed = $deployed (a legitimate payload file must not be dropped)")
Assert-Sandboxed $sb | Out-Null

# ---------------------------------------------------------------- T12 locked payload file is reported
$sb = New-Sandbox "copy-lockedpayload"
Set-Content -LiteralPath "$($sb.Payload)\a.dll" -Value "p"
$locked = "$($sb.Payload)\locked.dll"
Set-Content -LiteralPath $locked -Value "x"
$h = [System.IO.File]::Open($locked, 'Open', 'ReadWrite', 'None')
$r = Invoke-Sync $sb -Quiet
$h.Close()
$deployed = Test-Path -LiteralPath "$($sb.Versions)\1.0.0\locked.dll"
Record-Result -Id "T12" -Test "unreadable payload file is reported" -Pass ($r.Exit -ne 0) -Severity "Medium" `
    -Detail ("deployed = $deployed ; exit code = $($r.Exit)")
Assert-Sandboxed $sb | Out-Null

# ---------------------------------------------------------------- T25b a late game marker is waited for
$sb = New-Sandbox "copy-latemarker"
Set-Content -LiteralPath "$($sb.Payload)\a.dll" -Value "p"
$lateDir = Join-Path $sb.Versions "2.0.0"
New-Item -ItemType Directory -Path $lateDir -Force | Out-Null
$logPath = "$($sb.Install)\sync_log.txt"
Use-SandboxEnv $sb
$p = Start-Process -FilePath "powershell.exe" -WindowStyle Hidden -PassThru `
        -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$($sb.Install)\Sync.ps1`" -Quiet"
$started = $false
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) {
    if ((Test-Path -LiteralPath $logPath) -and ((Get-Content -LiteralPath $logPath -Raw -ErrorAction SilentlyContinue) -match "sync started")) { $started = $true; break }
    Start-Sleep -Milliseconds 100
}
Start-Sleep -Milliseconds 700
Set-Content -LiteralPath (Join-Path $lateDir "Minecraft.Windows.exe") -Value "stub"
$exited = $p.WaitForExit(120000)
if (-not $exited) { $p.Kill(); $p.WaitForExit(10000) | Out-Null }
Restore-Env
$code = $null
try { $code = $p.ExitCode } catch { }
$deployed = Test-Path -LiteralPath (Join-Path $lateDir "a.dll")
Record-Result -Id "T25b" -Test "sync waits for a late game marker and tops up the folder" -Pass ($started -and $exited -and $deployed -and $code -eq 0) -Severity "High" `
    -Detail ("sync started before the marker = $started ; deployed after the marker = $deployed ; exit = $code")
Assert-Sandboxed $sb | Out-Null

# ---------------------------------------------------------------- T25c ready folder is copied while another is still empty
$sb = New-Sandbox "copy-partialwait"
Set-Content -LiteralPath "$($sb.Payload)\a.dll" -Value "p"
$blockedDir = Join-Path $sb.Versions "2.0.0"
New-Item -ItemType Directory -Path $blockedDir -Force | Out-Null
Use-SandboxEnv $sb
$p = Start-Process -FilePath "powershell.exe" -WindowStyle Hidden -PassThru `
        -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$($sb.Install)\Sync.ps1`" -Quiet -SettleSeconds 8"
$watch = [System.Diagnostics.Stopwatch]::StartNew()
$deployed = $false
$deadline = (Get-Date).AddSeconds(5)
while ((Get-Date) -lt $deadline) {
    if (Test-Path -LiteralPath (Join-Path $sb.Versions "1.0.0\a.dll")) { $deployed = $true; break }
    Start-Sleep -Milliseconds 100
}
$elapsed = [int]$watch.Elapsed.TotalSeconds
$exited = $p.WaitForExit(30000)
if (-not $exited) { $p.Kill(); $p.WaitForExit(10000) | Out-Null }
Restore-Env
$code = $null
try { $code = $p.ExitCode } catch { }
$blockedEmpty = -not (Test-Path -LiteralPath (Join-Path $blockedDir "a.dll"))
Record-Result -Id "T25c" -Test "a ready folder is patched without waiting for another recent empty folder" -Pass ($deployed -and $exited -and ($code -eq 0) -and $blockedEmpty) -Severity "High" `
    -Detail ("ready folder patched after ${elapsed}s = $deployed ; empty folder left untouched = $blockedEmpty ; exit = $code")
Assert-Sandboxed $sb | Out-Null
