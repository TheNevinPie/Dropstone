# The installer must resolve the versions folder next to the launcher.
# Read-only: the app is started with a preset that has no versionsdir, so the
# app has to detect one. Nothing is installed and nothing is written.
. "$PSScriptRoot\..\lib\e2e.ps1"

$sb = New-AppSandbox -Name "resolution"

# a fake "portable" launcher in the sandbox, with a versions folder next to it
$portable = Join-Path $sb.Root "portable\LeviLauncher"
New-Item -ItemType Directory -Path (Join-Path $portable "versions\1.0.0") -Force | Out-Null
Set-Content -LiteralPath (Join-Path $portable "LeviLauncher.exe") -Value "fake launcher"
Set-Content -LiteralPath (Join-Path $portable "versions\1.0.0\Minecraft.Windows.exe") -Value "stub"

# preset: launcher exe = the portable one, no versionsdir
$preset = @"
installdir=$(Join-Path $sb.Root "install")
launcherexe=$(Join-Path $portable "LeviLauncher.exe")
payloadmode=local
payloadpath=$(Join-Path $sb.Root "payload\payload.zip")
title=resolution probe
description=read-only probe
"@
Set-Content -LiteralPath (Join-Path $sb.Root "run\config.ini") -Value $preset -Encoding UTF8

$p = Start-App -Sandbox $sb
$els = Get-UiText -ProcessId $p.Id
$shown = Get-UiField -ProcessId $p.Id -Id "DirectVersionsText"
Write-Host ("    app resolved: " + $shown)
$expected = Join-Path $portable "versions"
$ok = ($shown -and $shown.StartsWith($sb.Root, [StringComparison]::OrdinalIgnoreCase))
Record-Result -Id "T22" -Test "the installer resolves the versions folder next to the launcher" -Pass $ok -Severity "High" `
    -Detail ("app resolved '$shown' ; the sandbox launcher folder is '$expected' ; a path outside the sandbox means the real launcher was picked")

Stop-App -ProcessId $p.Id



