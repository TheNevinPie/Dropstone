# Versions-folder resolution: pinned path, stale pin, invalid pin, unicode pin.
. "$PSScriptRoot\..\lib\common.ps1"

# ---------------------------------------------------------------- T5 unicode pin
$sb = New-Sandbox "resolve-unicode"
$uni = Join-Path $sb.Root "Jörg Müller\vérsions"
New-Item -ItemType Directory -Path "$uni\1.0.0" -Force | Out-Null
Set-Content -LiteralPath "$uni\1.0.0\Minecraft.Windows.exe" -Value "stub"
Set-Content -LiteralPath "$($sb.Payload)\a.dll" -Value "p"
Set-Content -LiteralPath "$($sb.Install)\config.ini" -Value ("watchDir=" + $uni) -Encoding UTF8
$r = Invoke-Sync $sb -Quiet
$deployed = Test-Path -LiteralPath "$uni\1.0.0\a.dll"
$cfg = Get-Content -LiteralPath "$($sb.Install)\config.ini" -Raw
$ok = $deployed -and ($cfg -match [regex]::Escape($uni))
Record-Result -Id "T5" -Test "unicode path survives the config.ini round-trip" -Pass $ok -Severity "High" `
    -Detail ("deployed into the unicode path = $deployed ; config.ini watchDir = " + ((Get-PinnedWatchDir $sb)))
Assert-Sandboxed $sb | Out-Null
Remove-Item $uni -Recurse -Force -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- T6 invalid pin must fail loudly
$sb = New-Sandbox "resolve-invalid"
Set-Content -LiteralPath "$($sb.Payload)\a.dll" -Value "p"
Invoke-Sync $sb -Quiet | Out-Null
$firstOk = Test-Path -LiteralPath "$($sb.Versions)\1.0.0\a.dll"
Remove-Item (Join-Path $sb.Root "levilauncher") -Recurse -Force   # the pinned tree disappears
$r = Invoke-Sync $sb -Quiet
$pin = Get-PinnedWatchDir $sb
$repointed = ($pin -and $pin -ne $sb.Versions)
$wroteElsewhere = (Test-Path -LiteralPath (Join-Path $sb.FakeRoaming "levilauncher.exe\versions\1.0.0\a.dll"))
$ok = ($r.Exit -ne 0) -and (-not $repointed) -and (-not $wroteElsewhere)
Record-Result -Id "T6" -Test "invalid watchDir fails loudly (no silent re-target)" -Pass $ok -Severity "Blocker" `
    -Detail ("first sync deployed = $firstOk ; after the pinned tree vanished: exit=$($r.Exit) pin='$pin' re-pointed=$repointed wrote-elsewhere=$wroteElsewhere")
Assert-Sandboxed $sb | Out-Null

# ---------------------------------------------------------------- T7 a stale pin must be reported
$sb = New-Sandbox "resolve-stale"
Set-Content -LiteralPath "$($sb.Payload)\a.dll" -Value "p"
$live = Join-Path $sb.Root "moved\versions\1.0.0"
New-Item -ItemType Directory -Path $live -Force | Out-Null
Set-Content -LiteralPath "$live\Minecraft.Windows.exe" -Value "stub"
Invoke-Sync $sb -Quiet | Out-Null
$inOld = Test-Path -LiteralPath "$($sb.Versions)\1.0.0\a.dll"
$inNew = Test-Path -LiteralPath "$live\a.dll"
# the pinned folder stays authoritative (no silent re-target) but the worker
# has to tell the user that the launcher moved somewhere else
$liveConfig = Join-Path $sb.FakeRoaming "levilauncher.exe\config.json"
@{ base_root = (Join-Path $sb.Root "moved") } | ConvertTo-Json | Set-Content -LiteralPath $liveConfig
$r = Invoke-Sync $sb -Quiet
$log = ""
if (Test-Path "$($sb.Install)\sync_log.txt") { $log = Get-Content "$($sb.Install)\sync_log.txt" -Raw }
$warned = $log -match "differs from the launcher"
$ok = $inOld -and (-not $inNew) -and $warned
Record-Result -Id "T7" -Test "a stale watchDir is used but reported" -Pass $ok -Severity "High" `
    -Detail ("payload went to the pinned tree = $inOld ; to the live tree = $inNew ; the difference was reported in the log = $warned")
Assert-Sandboxed $sb | Out-Null

# ---------------------------------------------------------------- T10 no versions folder at all
$sb = New-Sandbox "resolve-none"
Set-Content -LiteralPath "$($sb.Payload)\a.dll" -Value "p"
Remove-Item (Join-Path $sb.Root "levilauncher") -Recurse -Force
Remove-Item (Join-Path $sb.Root "AppData") -Recurse -Force
# The worker must not fall back to a *running* LeviLauncher, so the test asks
# for auto-detection to be disabled. Older workers do not have that switch and
# would reach the real launcher - in that case the test is skipped instead of
# being allowed to write outside the sandbox.
$hasSwitch = $false
try { $hasSwitch = (Get-Command "$($sb.Install)\Sync.ps1").Parameters.ContainsKey("NoAutoDetect") } catch { }
if ($hasSwitch) {
    $env:APPDATA = Join-Path $sb.Root "empty"
    $env:LOCALAPPDATA = Join-Path $sb.Root "empty"
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$($sb.Install)\Sync.ps1" -Quiet -NoAutoDetect 2>&1
    $code = $LASTEXITCODE
    Restore-Env
    Record-Result -Id "T10" -Test "no versions folder -> non-zero exit" -Pass ($code -ne 0) -Severity "High" `
        -Detail ("exit code = $code (expected non-zero)")
} else {
    Record-Result -Id "T10" -Test "no versions folder -> non-zero exit" -Pass $false -Severity "High" `
        -Detail "SKIPPED (unsafe): the worker has no -NoAutoDetect switch, so the test would fall through to the real LeviLauncher. Add the switch to make the resolution strict."
}

# ---------------------------------------------------------------- T17d version folder without the game exe
$sb = New-Sandbox "resolve-nogame"
Set-Content -LiteralPath "$($sb.Payload)\a.dll" -Value "p"
Remove-Item -LiteralPath "$($sb.Versions)\1.0.0\Minecraft.Windows.exe"
$r = Invoke-Sync $sb -Quiet -Extra @("-SettleSeconds", "0")
$deployed = Test-Path -LiteralPath "$($sb.Versions)\1.0.0\a.dll"
$logged = $r.Out -match "skip" -or (Test-Path "$($sb.Install)\sync_log.txt")
$ok = (-not $deployed) -and $logged
Record-Result -Id "T17d" -Test "version folder without the game exe is reported" -Pass $ok -Severity "Medium" `
    -Detail ("deployed = $deployed (must be False) ; the skip was reported in the output/log = $logged")
Assert-Sandboxed $sb | Out-Null
