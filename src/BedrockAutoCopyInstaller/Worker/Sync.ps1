# Bedrock AutoCopy - single-pass payload sync
#
# Copies the files from the "payload" folder next to this script into every
# LeviLauncher version folder that does not have them yet, preserving relative
# paths. Runs once and exits; nothing stays resident in the background.
#
# No payload files are shipped with this tool. Place your own files in the
# "payload" folder (the installer extracts your archive there).
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File Sync.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File Sync.ps1 -Quiet
#
# Options:
#   -VersionsDir <path>  explicit LeviLauncher "versions" folder (highest priority)
#   -FilesDir <path>     payload folder (default: "payload" next to this script)
#   -Quiet               no console output and no dialogs (for scheduled runs)
#   -Force               overwrite files that already exist in a version folder
#   -NoSave              do not write the resolved path back to config.ini
#
# Versions folder resolution order:
#   1. -VersionsDir parameter
#   2. watchDir= in config.ini next to this script
#   3. base_root in %APPDATA%\levilauncher.exe\config.json (the launcher's own
#      setting) -> <base_root>\versions
#   4. %APPDATA%\levilauncher.exe\versions
#   5. %LOCALAPPDATA%\levilauncher.exe\versions
#   6. a running LeviLauncher process
#   7. a bounded scan of drive roots for "levilauncher.exe\versions"/"levilauncher\versions"
#   8. a folder-picker dialog (only when running interactively)

param(
    [string]$VersionsDir = "",
    [string]$FilesDir = "",
    [switch]$Quiet,
    [switch]$Force,
    [switch]$NoSave
)

$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $FilesDir) { $FilesDir = Join-Path $scriptDir "payload" }
$logFile    = Join-Path $scriptDir "sync_log.txt"
$configFile = Join-Path $scriptDir "config.ini"

$mutex = New-Object System.Threading.Mutex($false, "Local\BedrockAutoCopySync")
if (-not $mutex.WaitOne(0)) { exit 0 }

function Write-Log([string]$Message) {
    try {
        if ((Test-Path -LiteralPath $logFile) -and ((Get-Item -LiteralPath $logFile).Length -gt 262144)) {
            $tail = @(Get-Content -LiteralPath $logFile -Tail 200)
            Set-Content -LiteralPath $logFile -Value $tail -Encoding ASCII
        }
        $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
        Add-Content -LiteralPath $logFile -Value "[$ts] $Message" -Encoding ASCII
    } catch { }
}

function Write-Info([string]$Message) {
    if (-not $Quiet) { Write-Host $Message }
}

function Show-MessageBox([string]$Message) {
    if ($Quiet) { return }
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show($Message, "Bedrock AutoCopy", 0, 16) | Out-Null
    } catch {
        Write-Host $Message
    }
}

function Get-ConfigValue([string]$Key) {
    if (-not (Test-Path -LiteralPath $configFile)) { return $null }
    foreach ($line in Get-Content -LiteralPath $configFile) {
        $pattern = "^\s*" + [regex]::Escape($Key) + "\s*=\s*(.+?)\s*$"
        if ($line -match $pattern) { return $Matches[1].Trim('"') }
    }
    return $null
}

function Set-ConfigValue([string]$Key, [string]$Value) {
    $data = [ordered]@{}
    if (Test-Path -LiteralPath $configFile) {
        foreach ($line in Get-Content -LiteralPath $configFile) {
            $m = [regex]::Match($line, '^\s*([A-Za-z0-9_]+)\s*=\s*(.*?)\s*$')
            if ($m.Success) { $data[$m.Groups[1].Value.ToLower()] = $m.Groups[2].Value.Trim('"') }
        }
    }
    $data[$Key.ToLower()] = $Value
    $out = @(
        "# Bedrock AutoCopy configuration.",
        "# watchDir: your LeviLauncher 'versions' folder.",
        "# launcherExe: path to LeviLauncher.exe (used by the launcher shortcut)."
    )
    foreach ($k in $data.Keys) { $out += "$k=$($data[$k])" }
    Set-Content -LiteralPath $configFile -Value $out -Encoding ASCII
}

function Test-VersionsDir([string]$Path) {
    if ($Path -and (Test-Path -LiteralPath $Path -PathType Container)) { return $Path.TrimEnd('\') }
    return $null
}

function Get-VersionsFromLauncherConfig {
    $cfgPath = Join-Path $env:APPDATA "levilauncher.exe\config.json"
    if (-not (Test-Path -LiteralPath $cfgPath)) { return $null }
    try {
        $json = Get-Content -LiteralPath $cfgPath -Raw | ConvertFrom-Json
    } catch {
        return $null
    }
    $base = $null
    if ($json -and $json.PSObject.Properties["base_root"]) { $base = [string]$json.base_root }
    if ([string]::IsNullOrWhiteSpace($base)) { $base = Join-Path $env:APPDATA "levilauncher.exe" }
    return (Test-VersionsDir (Join-Path $base "versions"))
}

function Find-DefaultVersions {
    $results = @(
        (Test-VersionsDir (Join-Path $env:APPDATA "levilauncher.exe\versions")),
        (Test-VersionsDir (Join-Path $env:LOCALAPPDATA "levilauncher.exe\versions"))
    )
    return ($results | Where-Object { $_ } | Select-Object -First 1)
}

function Find-RunningLauncherVersions {
    $procs = @()
    try {
        $procs = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "levilauncher*" -or $_.Name -like "launcher-x64*" })
    } catch { }
    if ($procs.Count -eq 0) {
        try {
            $procs = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "levilauncher*" -or $_.Name -like "launcher-x64*" })
        } catch { }
    }
    foreach ($proc in $procs) {
        try {
            $exePath = $null
            if ($proc.Path) { $exePath = $proc.Path }
            if (-not $exePath -and $proc.ExecutablePath) { $exePath = $proc.ExecutablePath }
            if (-not $exePath) { continue }
            $exeDir = Split-Path -Parent $exePath
            $found = Test-VersionsDir (Join-Path $exeDir "versions")
            if ($found) { return $found }
        } catch { }
    }
    return $null
}

function Find-PortableVersions {
    $skip = @('Windows', 'Program Files', 'Program Files (x86)', 'ProgramData', '$Recycle.Bin', 'System Volume Information', 'Recovery')
    foreach ($drive in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        if ($drive.Root -notmatch '^[A-Za-z]:\\$') { continue }

        $bases = @($drive.Root)
        $topDirs = @(Get-ChildItem -LiteralPath $drive.Root -Directory -Force -ErrorAction SilentlyContinue | Where-Object { $skip -notcontains $_.Name })
        $bases += $topDirs.FullName
        foreach ($top in $topDirs) {
            $bases += (Get-ChildItem -LiteralPath $top.FullName -Directory -Force -ErrorAction SilentlyContinue).FullName
        }

        foreach ($base in $bases) {
            foreach ($folderName in @("levilauncher.exe", "levilauncher")) {
                $found = Test-VersionsDir (Join-Path $base "$folderName\versions")
                if ($found) { return $found }
            }
        }
    }
    return $null
}

function Select-WatchDirDialog {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
        $dialog.Description = "Select the LeviLauncher 'versions' folder"
        $dialog.ShowNewFolderButton = $false
        if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
            return $dialog.SelectedPath
        }
    } catch { }
    return $null
}

try {
    if (-not (Test-Path -LiteralPath $FilesDir -PathType Container)) {
        Write-Log "ERROR: payload folder not found: $FilesDir"
        Show-MessageBox "Payload folder not found:`n$FilesDir`n`nRun the installer again and choose a payload archive."
        exit 1
    }

    $payloadRoot = (Resolve-Path -LiteralPath $FilesDir).Path.TrimEnd('\')
    $payloadExcluded = @("config.ini", "sync_log.txt", "install.json", "stop.flag")
    $payload = @(Get-ChildItem -LiteralPath $FilesDir -File -Recurse -ErrorAction SilentlyContinue | Where-Object {
        $payloadExcluded -notcontains $_.Name.ToLower()
    })

    if ($payload.Count -eq 0) {
        Write-Log "No payload files in $FilesDir - nothing to do."
        exit 0
    }

    $targetDir = $null
    $resolvedNow = $false

    if ($VersionsDir) { $targetDir = Test-VersionsDir $VersionsDir }
    if (-not $targetDir) { $targetDir = Test-VersionsDir (Get-ConfigValue "watchDir") }

    if (-not $targetDir) {
        $detected = Get-VersionsFromLauncherConfig
        if (-not $detected) { $detected = Find-DefaultVersions }
        if (-not $detected) { $detected = Find-RunningLauncherVersions }
        if (-not $detected) { $detected = Find-PortableVersions }
        if (-not $detected -and -not $Quiet) { $detected = Select-WatchDirDialog }

        if ($detected -and (Test-Path -LiteralPath $detected -PathType Container)) {
            $targetDir = $detected.TrimEnd('\')
            $resolvedNow = $true
            if (-not $NoSave) { Set-ConfigValue "watchDir" $targetDir }
        }
    }

    if (-not $targetDir) {
        Write-Log "ERROR: no LeviLauncher 'versions' folder found. Set watchDir in config.ini."
        Show-MessageBox "Could not find the LeviLauncher 'versions' folder.`n`nEdit config.ini next to the script and set:`nwatchDir=<your path>"
        exit 1
    }

    $versions = @(Get-ChildItem -LiteralPath $targetDir -Directory -Force -ErrorAction SilentlyContinue)
    $copied = 0
    $failed = 0
    $touchedFolders = 0

    foreach ($version in $versions) {
        $versionDir = $version.FullName
        $hasGame = (Test-Path -LiteralPath (Join-Path $versionDir "Minecraft.Windows.exe")) -or
                   (Test-Path -LiteralPath (Join-Path $versionDir "Minecraft.Win10.DX11.exe"))
        if (-not $hasGame) { continue }

        $folderCopied = 0
        foreach ($file in $payload) {
            $relativePath = $file.FullName.Substring($payloadRoot.Length).TrimStart('\')
            $destPath = Join-Path $versionDir $relativePath
            if ((Test-Path -LiteralPath $destPath) -and -not $Force) { continue }
            try {
                $destDir = Split-Path -Parent $destPath
                if (-not (Test-Path -LiteralPath $destDir -PathType Container)) {
                    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
                }
                Copy-Item -LiteralPath $file.FullName -Destination $destPath -Force:$Force -ErrorAction Stop
                $copied++
                $folderCopied++
            } catch {
                $failed++
                Write-Log "ERROR: failed to copy $relativePath to $versionDir : $($_.Exception.Message)"
            }
        }
        if ($folderCopied -gt 0) { $touchedFolders++ }
    }

    if ($copied -gt 0 -or $failed -gt 0) {
        Write-Log "Sync complete: copied $copied file(s) into $touchedFolders version folder(s) under $targetDir (failed: $failed)."
    } elseif ($resolvedNow) {
        Write-Log "Versions folder resolved to $targetDir - nothing new to copy."
    }

    Write-Info "Bedrock AutoCopy sync finished."
    Write-Info "  versions:  $targetDir"
    Write-Info "  payload:   $FilesDir ($($payload.Count) file(s))"
    Write-Info "  copied:    $copied"
    Write-Info "  failed:    $failed"
    if ($failed -gt 0) { Write-Info "  see $logFile for details" }
    exit 0
} finally {
    $mutex.ReleaseMutex()
}
