# Last-resort safety net. Removes any file that appears inside the real
# LeviLauncher versions tree and is not part of the pre-test snapshot.
# Run it in the background while the suite executes.
param(
    [string]$RealVersions = "C:\Gaming\levilauncher\versions",
    [string]$Snapshot = (Join-Path $PSScriptRoot "..\artifacts\real-versions-before.csv"),
    [string]$StopFile = (Join-Path $PSScriptRoot "..\artifacts\STOP_WATCHDOG"),
    [string]$LogFile = (Join-Path $PSScriptRoot "..\artifacts\watchdog.log")
)
$ErrorActionPreference = "SilentlyContinue"
$root = $RealVersions.TrimEnd('\')
$known = @{}
if (Test-Path $Snapshot) { Import-Csv $Snapshot | ForEach-Object { $known[$_.rel] = $true } }
"watchdog started $(Get-Date -Format o)" | Out-File -FilePath $LogFile -Encoding ASCII
while (-not (Test-Path $StopFile)) {
    Get-ChildItem $root -Directory -Force | ForEach-Object {
        $ver = $_
        Get-ChildItem $ver.FullName -File -Force | ForEach-Object {
            $rel = "\" + $ver.Name + "\" + $_.Name
            if (-not $known.ContainsKey($rel)) {
                Remove-Item -LiteralPath $_.FullName -Force
                "REMOVED $rel at $(Get-Date -Format o)" | Out-File -FilePath $LogFile -Append -Encoding ASCII
            }
        }
    }
    Start-Sleep -Milliseconds 700
}
"watchdog stopped $(Get-Date -Format o)" | Out-File -FilePath $LogFile -Append -Encoding ASCII
