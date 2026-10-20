# Removes every artefact a test run can create:
#   - the "Dropstone Sync" scheduled task
#   - the Dropstone WMI subscription in root\subscription (elevated)
#   - HKCU\Software\Dropstone
#   - the sandboxes under tests\artifacts
param(
    [string]$RealVersions = "C:\Gaming\levilauncher\versions",
    [switch]$KeepSandboxes
)
$ErrorActionPreference = "Continue"
$artifacts = Join-Path $PSScriptRoot "..\artifacts"

Write-Host "=== stopping running Dropstone instances ==="
Get-Process -Name "Dropstone" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host ("   killing pid " + $_.Id + " (" + $_.Path + ")")
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 2

Write-Host "=== removing the scheduled task ==="
& schtasks.exe /Delete /TN "Dropstone Sync" /F 2>&1 | ForEach-Object { Write-Host ("   " + $_) }

Write-Host "=== removing the WMI subscription (elevated) ==="
$script = @'
$ErrorActionPreference='Stop'
$ns='root\subscription'
Get-WmiObject -Namespace $ns -Class __FilterToConsumerBinding -ErrorAction SilentlyContinue | Where-Object { ($_.Filter -like '*DropstoneVersionsChanged*') -or ($_.Consumer -like '*DropstoneRunSync*') } | ForEach-Object { $_.Delete() }
Get-WmiObject -Namespace $ns -Class __EventFilter -Filter "Name='DropstoneVersionsChanged'" -ErrorAction SilentlyContinue | ForEach-Object { $_.Delete() }
Get-WmiObject -Namespace $ns -Class CommandLineEventConsumer -Filter "Name='DropstoneRunSync'" -ErrorAction SilentlyContinue | ForEach-Object { $_.Delete() }
Get-WmiObject -Namespace $ns -Class __EventFilter -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'Dropstone*' } | ForEach-Object { $_.Delete() }
Get-WmiObject -Namespace $ns -Class CommandLineEventConsumer -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'Dropstone*' } | ForEach-Object { $_.Delete() }
'@
$sp = Join-Path $env:TEMP "dropstone-cleanup-wmi.ps1"
Set-Content -LiteralPath $sp -Value $script -Encoding Unicode
try {
    $p = Start-Process -FilePath "powershell.exe" -Verb RunAs -PassThru -WindowStyle Hidden `
            -ArgumentList "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$sp`""
    $p.WaitForExit(120000) | Out-Null
} catch {
    Write-Host ("   elevation failed (declined?): " + $_.Exception.Message)
}
Start-Sleep -Seconds 2
$f = @(Get-WmiObject -Namespace root\subscription -Class __EventFilter -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "Dropstone*" })
$c = @(Get-WmiObject -Namespace root\subscription -Class CommandLineEventConsumer -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "Dropstone*" })
$b = @(Get-WmiObject -Namespace root\subscription -Class __FilterToConsumerBinding -ErrorAction SilentlyContinue | Where-Object { $_.Filter -like "*Dropstone*" -or $_.Consumer -like "*Dropstone*" })
Write-Host ("   remnants: filter=" + $f.Count + " consumer=" + $c.Count + " binding=" + $b.Count)

Write-Host "=== removing the registry key ==="
Remove-Item "HKCU:\Software\Dropstone" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ("   HKCU\Software\Dropstone present = " + [bool](Get-ItemProperty "HKCU:\Software\Dropstone" -ErrorAction SilentlyContinue))

if (-not $KeepSandboxes) {
    Write-Host "=== removing the sandboxes ==="
    Get-ChildItem $artifacts -Directory -Filter "sbx-*" -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    Get-ChildItem $artifacts -Directory -Filter "app-*" -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host "   done"
}

Write-Host "=== final verification ==="
$q = (& schtasks.exe /Query /TN "Dropstone Sync" 2>&1 | Out-String)
Write-Host ("   task            : " + $(if ($q -match "cannot find") { "removed" } else { "STILL PRESENT" }))
Write-Host ("   registry        : " + $(if (Get-ItemProperty "HKCU:\Software\Dropstone" -ErrorAction SilentlyContinue) { "STILL PRESENT" } else { "removed" }))
Write-Host ("   %LOCALAPPDATA%  : " + $(if (Test-Path "$env:LOCALAPPDATA\Dropstone") { "STILL PRESENT" } else { "absent" }))
Write-Host ("   wmi remnants    : " + $f.Count + "/" + $c.Count + "/" + $b.Count)
