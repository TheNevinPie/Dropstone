# Uninstall must remove exactly what Dropstone created - and nothing else.
. "$PSScriptRoot\..\lib\e2e.ps1"

$sb = New-AppSandbox -Name "uninstall" -Payload @{ "alpha.dll" = "payload-alpha"; "beta.dll" = "payload-beta" }
$p = Start-App -Sandbox $sb
Invoke-UiButton -ProcessId $p.Id -Name "Install" | Out-Null
Wait-AppResult -ProcessId $p.Id -TimeoutSeconds 180 | Out-Null
Stop-App -ProcessId $p.Id

# fixtures: an unrelated user file in the install folder and a file that was
# never deployed by Dropstone (it must survive: the uninstall only removes
# what it deployed)
Set-Content -LiteralPath (Join-Path $sb.Install "MY-NOTES.txt") -Value "important user data"
Set-Content -LiteralPath (Join-Path $sb.Versions "1.0.0\foreign.dll") -Value "not deployed by Dropstone"

$p2 = Start-App -Sandbox $sb
Invoke-UiButton -ProcessId $p2.Id -Name "Uninstall" | Out-Null
Start-Sleep -Seconds 4
Toggle-UiCheckBox -ProcessId $p2.Id -Name "Also remove*" | Out-Null
Start-Sleep -Seconds 2
$offered = (Get-UiText -ProcessId $p2.Id | Where-Object { $_.Type -eq "CheckBox" } | ForEach-Object { $_.Name }) -join ", "
Invoke-UiButton -ProcessId $p2.Id -Name "Uninstall" | Out-Null
$res = Wait-AppResult -ProcessId $p2.Id -TimeoutSeconds 180
Write-Host ("    uninstall: " + $res.Title + " :: " + ($res.Message -replace "`r`n", " / "))
Write-Host ("    offered files: " + $offered)

$notesAlive = Test-Path (Join-Path $sb.Install "MY-NOTES.txt")
$installGone = -not (Test-Path (Join-Path $sb.Install "install.json"))
$foreign = Test-Path (Join-Path $sb.Versions "1.0.0\foreign.dll")
$alpha = Test-Path (Join-Path $sb.Versions "1.0.0\alpha.dll")
$task = Get-TaskState
$wmi = Get-WmiState
$reg = Get-RegistryInstallDir

$ok = $notesAlive -and $installGone -and (-not $alpha) -and (-not $task.Present) -and
      ($wmi.Filters -eq 0) -and ($wmi.Consumers -eq 0) -and ($wmi.Bindings -eq 0) -and (-not $reg)
Record-Result -Id "T27" -Test "uninstall keeps unrelated files in the install folder" -Pass $notesAlive -Severity "Blocker" `
    -Detail ("MY-NOTES.txt still present = $notesAlive (it must survive)")
Record-Result -Id "T28" -Test "uninstall removes every deployed payload file" -Pass (-not $alpha) -Severity "Medium" `
    -Detail ("deployed alpha.dll still present = $alpha ; a file Dropstone never deployed (foreign.dll) present = $foreign (it must survive) ; offered: $offered")
Record-Result -Id "T26" -Test "uninstall removes task, registry and WMI subscription" -Pass ((-not $task.Present) -and ($wmi.Filters -eq 0) -and ($wmi.Consumers -eq 0) -and ($wmi.Bindings -eq 0) -and (-not $reg)) -Severity "Low" `
    -Detail ("task=$($task.Present) wmi=$($wmi.Filters)/$($wmi.Consumers)/$($wmi.Bindings) registry=$reg")
Record-Result -Id "T27b" -Test "uninstall removed the install marker" -Pass $installGone -Severity "Blocker" `
    -Detail ("install.json still present = $(-not $installGone)")
Record-Result -Id "T28b" -Test "uninstall keeps files it never deployed" -Pass $foreign -Severity "Medium" `
    -Detail ("foreign.dll present = $foreign (Dropstone must not delete files it did not deploy)")

Start-Sleep -Seconds 3
$p2.Refresh()
$stayedOpen = -not $p2.HasExited
$closeHintOk = $res.Message -like "*close this window*"
Record-Result -Id "T31" -Test "the uninstall result stays open with a close hint" -Pass ($stayedOpen -and $closeHintOk) -Severity "Medium" `
    -Detail ("process alive 3s after result = $stayedOpen ; close hint = $closeHintOk")

$closeClicked = Invoke-UiButton -ProcessId $p2.Id -Name "Close"
$exited = $p2.WaitForExit(10000)
Record-Result -Id "T31b" -Test "the uninstall result Close button exits the app" -Pass ($closeClicked -and $exited) -Severity "Medium" `
    -Detail ("Close invoked = $closeClicked ; app exited = $exited")

if (-not $exited) { Stop-App -ProcessId $p2.Id }
