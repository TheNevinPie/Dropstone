# "Replace payload" must actually update the files that are already deployed.
. "$PSScriptRoot\..\lib\e2e.ps1"

$sb = New-AppSandbox -Name "replace" -Payload @{ "alpha.dll" = "payload-alpha"; "beta.dll" = "payload-beta" }
$p = Start-App -Sandbox $sb
Invoke-UiButton -ProcessId $p.Id -Name "Install" | Out-Null
$res = Wait-AppResult -ProcessId $p.Id -TimeoutSeconds 180
Write-Host ("    install: " + $res.Title)
$before = (Get-Content (Join-Path $sb.Versions "1.0.0\alpha.dll") -Raw).Trim()
Stop-App -ProcessId $p.Id

# build payload v2 (alpha.dll changed, gamma.dll added) and point the preset at it
$stage = Join-Path $sb.Root "stage2"
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Set-Content -LiteralPath (Join-Path $stage "alpha.dll") -Value "payload-alpha-V2"
Set-Content -LiteralPath (Join-Path $stage "beta.dll") -Value "payload-beta"
Set-Content -LiteralPath (Join-Path $stage "gamma.dll") -Value "payload-gamma"
$zip2 = Join-Path $sb.Root "payload\payload-v2.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip2, [System.IO.Compression.CompressionLevel]::Fastest, $false)
Remove-Item $stage -Recurse -Force
Set-AppPreset -Root $sb.Root -PayloadPath $zip2

$p2 = Start-App -Sandbox $sb
Invoke-UiButton -ProcessId $p2.Id -Name "Replace payload" | Out-Null
Start-Sleep -Seconds 3
$label = Get-UiField -ProcessId $p2.Id -Id "InstallButton"
Invoke-UiButton -ProcessId $p2.Id -Name $label | Out-Null
$res2 = Wait-AppResult -ProcessId $p2.Id -TimeoutSeconds 180
Write-Host ("    replace: " + $res2.Title + " :: " + ($res2.Message -replace "`r`n", " / "))

$after = (Get-Content (Join-Path $sb.Versions "1.0.0\alpha.dll") -Raw).Trim()
$gamma = Test-Path (Join-Path $sb.Versions "1.0.0\gamma.dll")
$installPayload = (Get-Content (Join-Path $sb.Install "payload\alpha.dll") -Raw).Trim()
$ok = ($after -eq "payload-alpha-V2") -and $gamma -and ($installPayload -eq "payload-alpha-V2")
Record-Result -Id "T1b" -Test "Replace payload updates the deployed files" -Pass $ok -Severity "Blocker" `
    -Detail ("title='$($res2.Title)' deployed alpha.dll = '$after' (expected 'payload-alpha-V2') ; gamma.dll deployed = $gamma ; install payload = '$installPayload'")

$marker = Get-Content (Join-Path $sb.Install "install.json") -Raw | ConvertFrom-Json
$titleKept = ($marker.installedTitle -eq "Dropstone stability suite (installed)")
Record-Result -Id "T29" -Test "a replace keeps the persisted installed text" -Pass $titleKept -Severity "High" `
    -Detail ("install.json installedTitle = '" + $marker.installedTitle + "' (expected it to survive the replace)")

Stop-App -ProcessId $p2.Id



