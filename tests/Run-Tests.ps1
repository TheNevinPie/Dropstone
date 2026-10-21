<#
.SYNOPSIS
    Runs the Dropstone stability suite.

.EXAMPLE
    .\Run-Tests.ps1 -Quick          # sync-level only; leaves the live install untouched
.EXAMPLE
    .\Run-Tests.ps1 -Full           # everything; removes the live task, WMI watch, and registry key
.EXAMPLE
    .\Run-Tests.ps1 -Full -NoWatchdog
#>
param(
    [switch]$Full,
    [switch]$Quick,
    [switch]$NoWatchdog,
    [switch]$NoCleanup,
    [string]$RealVersions = "C:\Gaming\levilauncher\versions"
)
$ErrorActionPreference = "Continue"
$here = $PSScriptRoot
. "$here\lib\common.ps1"

$artifacts = Get-ArtifactsDir
$results = @()

function Invoke-TestFile {
    param([string]$Path)
    Write-Host ""
    Write-Host ("########## " + (Split-Path -Leaf $Path) + " ##########")
    try {
        & $Path
    } catch {
        Write-Host ("   ERROR: " + $_.Exception.Message) -ForegroundColor Red
        Record-Result -Id (Split-Path -Leaf $Path) -Test "test file crashed" -Pass $false -Detail $_.Exception.Message
    }
}

Write-Host "=== Dropstone stability suite ==="
Write-Host ("    mode: " + $(if ($Full) { "FULL" } elseif ($Quick) { "QUICK" } else { "QUICK" }))
Write-Host ("    artifacts: " + $artifacts)
if ($Full) {
    Write-Host "    WARNING: full mode removes the live Dropstone task, WMI watch, and registry key; reinstall afterward."
}

# ---------------------------------------------------------------- safety net
$watchdog = $null
$watchdogScript = Join-Path $here "tools\watchdog.ps1"
$snapshot = Join-Path $artifacts "real-versions-before.csv"
if (-not $NoWatchdog -and (Test-Path $RealVersions)) {
    Write-Host ""
    Write-Host "=== recording a sentinel of the real versions tree ==="
    & (Join-Path $here "tools\snapshot-real-tree.ps1") -RealVersions $RealVersions -OutFile $snapshot
    Remove-Item (Join-Path $artifacts "STOP_WATCHDOG") -Force -ErrorAction SilentlyContinue
    $watchdog = Start-Process -FilePath "powershell.exe" -WindowStyle Hidden -PassThru `
        -ArgumentList "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$watchdogScript`"", "-RealVersions", "`"$RealVersions`"", "-Snapshot", "`"$snapshot`""
    Write-Host "    watchdog started (pid " + $watchdog.Id + ")"
}

# ---------------------------------------------------------------- test selection
$syncTests = @(
    "tests\sync-resolve.ps1",
    "tests\sync-copy.ps1",
    "tests\sync-mutex.ps1",
    "tests\sync-limits.ps1"
)
$e2eTests = @(
    "tests\e2e-install.ps1",
    "tests\e2e-replace.ps1",
    "tests\e2e-uninstall.ps1",
    "tests\e2e-watch.ps1",
    "tests\e2e-resolution.ps1",
    "tests\e2e-bomb.ps1",
    "tests\e2e-timeout.ps1",
    "tests\e2e-multi.ps1"
)

# clean any state left over from an earlier run
$cleanupArgs = @{ RealVersions = $RealVersions; KeepSandboxes = $true }
if (-not $Full) { $cleanupArgs.KeepLiveState = $true }
& (Join-Path $here "tools\cleanup.ps1") @cleanupArgs | Out-Null

foreach ($t in $syncTests) { Invoke-TestFile (Join-Path $here $t) }
if ($Full) {
    foreach ($t in $e2eTests) { Invoke-TestFile (Join-Path $here $t) }
}

# ---------------------------------------------------------------- teardown
if ($watchdog) {
    Write-Host ""
    Write-Host "=== stopping the watchdog ==="
    Set-Content -Path (Join-Path $artifacts "STOP_WATCHDOG") -Value "stop"
    Start-Sleep -Seconds 2
    Stop-Process -Id $watchdog.Id -Force -ErrorAction SilentlyContinue
    $log = Join-Path $artifacts "watchdog.log"
    if (Test-Path $log) {
        $removals = @(Get-Content $log | Where-Object { $_ -like "REMOVED*" })
        Write-Host ("    files the watchdog removed from the real tree: " + $removals.Count)
        $removals | ForEach-Object { Write-Host ("      " + $_) }
    }
}
if (-not $NoCleanup) {
    Write-Host ""
    Write-Host "=== cleanup ==="
    $cleanupArgs = @{ RealVersions = $RealVersions }
    if (-not $Full) { $cleanupArgs.KeepLiveState = $true }
    & (Join-Path $here "tools\cleanup.ps1") @cleanupArgs | Out-Null
}

# ---------------------------------------------------------------- verdict
Write-Host ""
Write-Host "=== real tree verification ==="
if (Test-Path $snapshot) {
    $before = Import-Csv $snapshot
    $root = $RealVersions.TrimEnd('\')
    $after = Get-ChildItem $root -Recurse -File -Force -ErrorAction SilentlyContinue |
                Select-Object @{n='rel';e={$_.FullName.Substring($root.Length)}}, Length, LastWriteTimeUtc
    $beforeSet = @{}; foreach ($b in $before) { $beforeSet["$($b.rel)|$($b.Length)"] = $true }
    $extra = @($after | Where-Object { -not $beforeSet.ContainsKey("$($_.rel)|$($_.Length)") })
    Write-Host ("    files before = " + $before.Count + " ; now = " + $after.Count)
    Write-Host ("    extra/modified = " + $extra.Count)
    $extra | Select-Object -First 10 | ForEach-Object { Write-Host ("      + " + $_.rel) }
    Write-Host ("    >>> REAL TREE UNTOUCHED = " + ($after.Count -eq $before.Count -and $extra.Count -eq 0))
}

$fail = Complete-Results
Write-Host ""
if ($fail -eq 0) { Write-Host "ALL TESTS PASSED" } else { Write-Host ("$fail TEST(S) FAILED") }
exit $fail
