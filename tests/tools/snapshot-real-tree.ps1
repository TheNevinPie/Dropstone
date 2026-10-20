# Records a sentinel of the real LeviLauncher tree so the suite can prove that
# it never modified it. The snapshot is a CSV of path|size|mtime per file.
param(
    [string]$RealVersions = "C:\Gaming\levilauncher\versions",
    [string]$OutFile = (Join-Path $PSScriptRoot "..\artifacts\real-versions-before.csv")
)
$ErrorActionPreference = "Stop"
$outDir = Split-Path -Parent $OutFile
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
$root = $RealVersions.TrimEnd('\')
$items = Get-ChildItem $root -Recurse -File -Force -ErrorAction SilentlyContinue |
    Select-Object @{n='rel';e={$_.FullName.Substring($root.Length)}}, Length, LastWriteTimeUtc
$items | Export-Csv -LiteralPath $OutFile -NoTypeInformation
Write-Host ("snapshot: " + $items.Count + " files -> " + $OutFile)
