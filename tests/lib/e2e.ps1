# End-to-end helpers: build a sandbox around a copy of the published EXE and
# drive the real WinUI app through UI Automation.
. "$PSScriptRoot\common.ps1"

function Reset-DropstoneState {
    <#
      Removes everything a previous test may have left behind: the app, the
      registry key, the scheduled task and the WMI subscription. Without this
      the WMI watch of an earlier test would keep firing and would steal the
      sync lock of the next test.
    #>
    Get-Process -Name "Dropstone" -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -like (Join-Path (Get-ArtifactsDir) "app-*") } |
        ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like ((Get-ArtifactsDir) + "*") -and $_.CommandLine -notlike "*-NoLogo*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 300

    Remove-Item "HKCU:\Software\Dropstone" -Recurse -Force -ErrorAction SilentlyContinue
    & schtasks.exe /Delete /TN "Dropstone Sync" /F 2>&1 | Out-Null

    $script = @'
$ns='root\subscription'
Get-WmiObject -Namespace $ns -Class __FilterToConsumerBinding -ErrorAction SilentlyContinue | Where-Object { ($_.Filter -like '*DropstoneVersionsChanged*') -or ($_.Consumer -like '*DropstoneRunSync*') } | ForEach-Object { $_.Delete() }
Get-WmiObject -Namespace $ns -Class __EventFilter -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'Dropstone*' } | ForEach-Object { $_.Delete() }
Get-WmiObject -Namespace $ns -Class CommandLineEventConsumer -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'Dropstone*' } | ForEach-Object { $_.Delete() }
'@
    $path = Join-Path (Get-ArtifactsDir) "reset-wmi.ps1"
    Set-Content -LiteralPath $path -Value $script -Encoding Unicode
    try {
        $p = Start-Process -FilePath "powershell.exe" -Verb RunAs -PassThru -WindowStyle Hidden `
                -ArgumentList "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$path`""
        $p.WaitForExit(120000) | Out-Null
    } catch {
        Write-Host ("    (elevation failed while resetting the WMI state: " + $_.Exception.Message + ")")
    }
    Remove-Item $path -Force -ErrorAction SilentlyContinue
}

function New-AppSandbox {
    <#
      Creates:
        <sbx>\run\Dropstone.exe   + config.ini  (deployment preset)
        <sbx>\install\            (the install folder the preset points at)
        <sbx>\versions\1.0.0\     (the versions tree the preset points at)
        <sbx>\payload\            (the payload the preset points at)
      The preset always pins installdir / versionsdir / payloadpath so the app
      can never resolve the real LeviLauncher.
    #>
    param(
        [Parameter(Mandatory)][string]$Name,
        [hashtable]$Payload
    )
    if (-not $Payload) { $Payload = @{} }
    # a leftover instance would keep the copied EXE locked, and a leftover
    # task / WMI watch would steal the sync lock of this test
    Reset-DropstoneState
    $s = Join-Path (Get-ArtifactsDir) ("app-" + $Name)
    if (Test-Path $s) { Remove-Item -Recurse -Force $s -ErrorAction SilentlyContinue }
    foreach ($d in @("run", "install", "versions\1.0.0", "payload", "fake", "stage")) {
        New-Item -ItemType Directory -Path (Join-Path $s $d) -Force | Out-Null
    }
    Set-Content -LiteralPath (Join-Path $s "versions\1.0.0\Minecraft.Windows.exe") -Value "stub"
    Set-Content -LiteralPath (Join-Path $s "fake\LeviLauncher.exe") -Value "fake launcher"

    if ($Payload.Count -eq 0) { $Payload = @{ "alpha.dll" = "payload-alpha" } }
    $stage = Join-Path $s "stage"
    foreach ($k in $Payload.Keys) { Set-Content -LiteralPath (Join-Path $stage $k) -Value $Payload[$k] }
    $zip = Join-Path $s "payload\payload.zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Fastest, $false)
    Remove-Item $stage -Recurse -Force

    Copy-Item (Get-PublishedExe) (Join-Path $s "run\Dropstone.exe") -Force
    Set-AppPreset -Root $s -PayloadPath $zip

    return [pscustomobject]@{
        Root     = $s
        Run      = (Join-Path $s "run")
        Exe      = (Join-Path $s "run\Dropstone.exe")
        Install  = (Join-Path $s "install")
        Versions = (Join-Path $s "versions")
        Payload  = (Join-Path $s "payload\payload.zip")
        Fake     = (Join-Path $s "fake\LeviLauncher.exe")
        Preset   = (Join-Path $s "run\config.ini")
    }
}

function Set-AppPreset {
    param([string]$Root, [string]$PayloadPath, [string]$VersionsDir = $null)
    if (-not $VersionsDir) { $VersionsDir = (Join-Path $Root "versions") }
    $preset = @"
installdir=$(Join-Path $Root "install")
launcherexe=$(Join-Path $Root "fake\LeviLauncher.exe")
versionsdir=$VersionsDir
payloadmode=local
payloadpath=$PayloadPath
title=Dropstone stability suite
description=sandboxed run
installedtitle=Dropstone stability suite (installed)
installeddescription=installed by the automated suite
"@
    Set-Content -LiteralPath (Join-Path $Root "run\config.ini") -Value $preset -Encoding UTF8
}

function Start-App {
    param($Sandbox, [int]$WaitSeconds = 12)
    $p = Start-Process -FilePath $Sandbox.Exe -PassThru
    Start-Sleep -Seconds $WaitSeconds
    return $p
}

function Stop-App {
    param($ProcessId)
    Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

function Wait-AppResult {
    param([int]$ProcessId, [int]$TimeoutSeconds = 300)
    for ($i = 0; $i -lt $TimeoutSeconds; $i++) {
        $els = Get-UiText -ProcessId $ProcessId
        $rt = $els | Where-Object { $_.Id -eq "ResultTitle" }
        if ($rt) {
            return [pscustomobject]@{
                Title   = $rt.Name
                Message = (Get-UiField -ProcessId $ProcessId -Id "ResultMessage")
            }
        }
        # an error bar means the operation failed and the app went back to the
        # previous screen
        $err = @($els | Where-Object { $_.Id -match "ErrorBar" })
        if ($err.Count -gt 0) {
            $text = @($els | Where-Object { $_.Type -eq "Text" -and $_.Name } | ForEach-Object { $_.Name })
            return [pscustomobject]@{ Title = "ERROR"; Message = ($text -join " | ") }
        }        Start-Sleep -Seconds 1
    }
    return [pscustomobject]@{ Title = "(timeout)"; Message = "" }
}

function Get-TaskState {
    $o = & schtasks.exe /Query /TN "Dropstone Sync" /FO LIST /V 2>&1 | Out-String
    return [pscustomobject]@{
        Present   = ($o -notmatch "cannot find the file specified")
        LastRun   = ([regex]::Match($o, "Last Run Time:\s*(.+)")).Groups[1].Value.Trim()
        LastResult = ([regex]::Match($o, "Last Result:\s*(\d+)")).Groups[1].Value
        Command   = ([regex]::Match($o, "Task To Run:\s*(.+)")).Groups[1].Value.Trim()
    }
}

function Get-WmiState {
    $f = @(Get-WmiObject -Namespace root\subscription -Class __EventFilter -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "*Dropstone*" })
    $c = @(Get-WmiObject -Namespace root\subscription -Class CommandLineEventConsumer -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "*Dropstone*" })
    $b = @(Get-WmiObject -Namespace root\subscription -Class __FilterToConsumerBinding -ErrorAction SilentlyContinue | Where-Object { $_.Filter -like "*Dropstone*" -or $_.Consumer -like "*Dropstone*" })
    return [pscustomobject]@{ Filters = $f.Count; Consumers = $c.Count; Bindings = $b.Count; Filter = $f; Consumer = $c }
}

function Get-RegistryInstallDir {
    $k = Get-ItemProperty "HKCU:\Software\Dropstone" -ErrorAction SilentlyContinue
    if ($k) { return $k.InstallDir } else { return $null }
}





