<#
.SYNOPSIS
    Runs a long command detached, without inheriting the caller's console
    handles. A one-time scheduled task is used so the job runs in the task
    service session - that keeps it alive when the calling shell exits and
    avoids the "the tool waits for the inherited stdout handle" problem.

.EXAMPLE
    .\Invoke-Detached.ps1 -Script "C:\...\Run-Tests.ps1" -Arguments "-Full" -Log "C:\...\run.log"
#>
param(
    [Parameter(Mandatory)][string]$Script,
    [string]$Arguments = "",
    [Parameter(Mandatory)][string]$Log,
    [string]$TaskName = "DropstoneTestJob"
)
$ErrorActionPreference = "Continue"

# the wrapper lives in a space free folder so the task action needs no quoting
$wrapper = Join-Path $env:TEMP "dropstone-job.cmd"
$exitFile = Join-Path $env:TEMP "dropstone-job.exit"
$body = @(
    '@echo off',
    ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $Script + '" ' + $Arguments + ' > "' + $Log + '" 2>&1'),
    ('echo %ERRORLEVEL% > "' + $exitFile + '"')
)
Set-Content -LiteralPath $wrapper -Value $body -Encoding ASCII
Remove-Item (Join-Path $env:TEMP "dropstone-job.exit") -Force -ErrorAction SilentlyContinue

try { & schtasks.exe /Delete /TN $TaskName /F 2>&1 | Out-Null } catch { }
$create = & schtasks.exe /Create /TN $TaskName /TR $wrapper /SC ONCE /ST 23:59 /F 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Output ("could not create the job task: " + ($create | Out-String))
    exit 1
}
& schtasks.exe /Run /TN $TaskName | Out-Null
Write-Output ("started '" + $TaskName + "' -> " + $Log)
