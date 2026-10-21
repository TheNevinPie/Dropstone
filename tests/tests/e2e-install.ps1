# Full install through the real UI: payload, worker files, config, task, WMI watch.
. "$PSScriptRoot\..\lib\e2e.ps1"

$sb = New-AppSandbox -Name "install" -Payload @{ "alpha.dll" = "payload-alpha"; "beta.dll" = "payload-beta" }
$p = Start-App -Sandbox $sb
$clicked = Invoke-UiButton -ProcessId $p.Id -Name "Install"
$res = Wait-AppResult -ProcessId $p.Id -TimeoutSeconds 180
Write-Host ("    result: " + $res.Title + " :: " + ($res.Message -replace "`r`n", " / "))

$payloadFiles = @(Get-ChildItem (Join-Path $sb.Install "payload") -File -ErrorAction SilentlyContinue)
$workerFiles = @("Sync.ps1", "StartHidden.vbs", "LaunchWithSync.vbs", "SyncNow.cmd")
$workerPresent = (@($workerFiles | Where-Object { -not (Test-Path (Join-Path $sb.Install $_)) }).Count -eq 0)
$configOk = Test-Path (Join-Path $sb.Install "config.ini")
$markerOk = Test-Path (Join-Path $sb.Install "install.json")
$deployed = @(Get-ChildItem (Join-Path $sb.Versions "1.0.0") -File | Where-Object { $_.Name -like "*.dll" }).Count
$task = Get-TaskState
$wmi = Get-WmiState

$ok = $clicked -and ($res.Title -eq "Installed") -and ($payloadFiles.Count -eq 2) -and $workerPresent -and
      $configOk -and $markerOk -and ($deployed -eq 2) -and $task.Present -and ($wmi.Consumers -ge 1)
Record-Result -Id "T23" -Test "end-to-end install produces a complete installation" -Pass $ok -Severity "Blocker" `
    -Detail ("clicked=$clicked title='$($res.Title)' payload files=$($payloadFiles.Count) worker files=$workerPresent config=$configOk install.json=$markerOk deployed=$deployed task=$($task.Present) wmi=$($wmi.Filters)/$($wmi.Consumers)/$($wmi.Bindings)")

$manifestOk = Test-Path (Join-Path $sb.Install "deployed.json")
Record-Result -Id "T23b" -Test "the install records a deployment manifest" -Pass $manifestOk -Severity "Blocker" `
    -Detail ("deployed.json present = $manifestOk (the manifest is what makes replace/uninstall correct)")

Start-Sleep -Seconds 3
$p.Refresh()
$stayedOpen = -not $p.HasExited
$guidanceOk = ($res.Message -like "*worker is now installed*") -and ($res.Message -like "*replace the payload or uninstall*")
Record-Result -Id "T30" -Test "the install result stays open and explains the installed worker" -Pass ($stayedOpen -and $guidanceOk) -Severity "Medium" `
    -Detail ("process alive 3s after result = $stayedOpen ; worker guidance = $guidanceOk")

$closeClicked = Invoke-UiButton -ProcessId $p.Id -Name "Close"
$exited = $p.WaitForExit(10000)
Record-Result -Id "T30b" -Test "the result screen Close button exits the app" -Pass ($closeClicked -and $exited) -Severity "Medium" `
    -Detail ("Close invoked = $closeClicked ; app exited = $exited")
if (-not $exited) { Stop-App -ProcessId $p.Id }

$markerPath = Join-Path $sb.Install "install.json"
$marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
$marker.installedDescription = ""
$marker | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $markerPath -Encoding UTF8

$p2 = Start-App -Sandbox $sb
$installedText = Get-UiField -ProcessId $p2.Id -Id "InstalledDescriptionText"
$defaultTextOk = ($installedText -like "*worker is installed*") -and ($installedText -like "*replace the payload or uninstall*")
Record-Result -Id "T30c" -Test "the installed screen explains the worker when no custom text is set" -Pass $defaultTextOk -Severity "Medium" `
    -Detail ("InstalledDescriptionText = '$installedText'")
Invoke-UiButton -ProcessId $p2.Id -Name "Close" | Out-Null
if (-not $p2.WaitForExit(10000)) { Stop-App -ProcessId $p2.Id }


