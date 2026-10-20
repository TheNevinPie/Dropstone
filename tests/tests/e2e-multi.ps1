# Two instances installing at the same time must not corrupt the payload.
. "$PSScriptRoot\..\lib\e2e.ps1"

$sb = New-AppSandbox -Name "multi"
$stage = Join-Path $sb.Root "stage"
New-Item -ItemType Directory -Path $stage -Force | Out-Null
for ($i = 1; $i -le 3000; $i++) { Set-Content -LiteralPath (Join-Path $stage ("f{0}.dat" -f $i)) -Value "x" }
$zip = Join-Path $sb.Root "payload\multi.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Fastest, $false)
Remove-Item $stage -Recurse -Force
Set-AppPreset -Root $sb.Root -PayloadPath $zip

$pA = Start-App -Sandbox $sb
$pB = Start-App -Sandbox $sb
$bothAlive = (-not $pA.HasExited) -and (-not $pB.HasExited)
Record-Result -Id "T20a" -Test "a second instance is refused instead of running concurrently" -Pass (-not $bothAlive) -Severity "High" `
    -Detail ("two instances alive at the same time = $bothAlive (a single-instance guard is expected)")

if ($bothAlive) {
    Invoke-UiButton -ProcessId $pA.Id -Name "Install" | Out-Null
    Start-Sleep -Milliseconds 1500
    Invoke-UiButton -ProcessId $pB.Id -Name "Install" | Out-Null
    Start-Sleep -Seconds 40
    Stop-App -ProcessId $pA.Id
    Stop-App -ProcessId $pB.Id
    $payload = @(Get-ChildItem (Join-Path $sb.Install "payload") -File -ErrorAction SilentlyContinue).Count
    $ok = ($payload -eq 3000)
    Record-Result -Id "T20" -Test "concurrent installs produce a complete payload" -Pass $ok -Severity "High" `
        -Detail ("payload files = $payload of 3000")
} else {
    Record-Result -Id "T20" -Test "concurrent installs produce a complete payload" -Pass $true -Severity "High" `
        -Detail "skipped: the second instance was refused, so the race cannot happen"
}

