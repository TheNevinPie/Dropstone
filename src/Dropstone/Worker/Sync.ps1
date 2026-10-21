# Dropstone - single-pass payload sync
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
#   -VerifyHash          also compare SHA256 of files that are already deployed
#   -NoSave              do not write the resolved path back to config.ini
#   -NoAutoDetect        never auto-detect the versions folder; fail if it is
#                        unknown (used by the installer, and by the test suite)
#   -LockTimeoutSeconds  how long to wait for a running sync to finish
#   -SettleSeconds       how long to wait for the game executable when a
#                        version folder was just created (default 120)
#
# Versions folder resolution order:
#   1. -VersionsDir parameter
#   2. watchDir= in config.ini next to this script  (authoritative: if the
#      folder is gone the sync fails instead of silently targeting a different
#      LeviLauncher)
#   3. <launcherExe folder>\versions
#   4. base_root in %APPDATA%\levilauncher.exe\config.json -> <base_root>\versions
#   5. %APPDATA%\levilauncher.exe\versions
#   6. %LOCALAPPDATA%\levilauncher.exe\versions
#   7. a running LeviLauncher process
#   8. a bounded scan of drive roots for "levilauncher.exe\versions"/"levilauncher\versions"
#
# Exit codes:
#   0  success
#   1  fatal error (payload folder missing, ...)
#   2  the versions folder could not be resolved (nothing was touched)
#   3  finished, but some files could not be copied
#   4  skipped because another sync was running

param(
    [string]$VersionsDir = "",
    [string]$FilesDir = "",
    [switch]$Quiet,
    [switch]$Force,
    [switch]$VerifyHash,
    [switch]$NoSave,
    [switch]$NoAutoDetect,
    [int]$LockTimeoutSeconds = 900,
    [int]$SettleSeconds = 120
)

$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $FilesDir) { $FilesDir = Join-Path $scriptDir "payload" }
$logFile      = Join-Path $scriptDir "sync_log.txt"
$configFile   = Join-Path $scriptDir "config.ini"
$manifestFile = Join-Path $scriptDir "deployed.json"

function Write-Log([string]$Message) {
    try {
        if ((Test-Path -LiteralPath $logFile) -and ((Get-Item -LiteralPath $logFile).Length -gt 262144)) {
            $tail = @(Get-Content -LiteralPath $logFile -Tail 200 -Encoding UTF8)
            Set-Content -LiteralPath $logFile -Value $tail -Encoding UTF8
        }
        $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
        Add-Content -LiteralPath $logFile -Value "[$ts] $Message" -Encoding UTF8
    } catch { }
}

function Write-Info([string]$Message) {
    if (-not $Quiet) { Write-Host $Message }
}

function Show-MessageBox([string]$Message) {
    if ($Quiet) { return }
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show($Message, "Dropstone", 0, 16) | Out-Null
    } catch {
        Write-Host $Message
    }
}

function Get-NameHash([string]$Text) {
    try {
        $md5 = [System.Security.Cryptography.MD5]::Create()
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text.ToLowerInvariant())
        $hash = $md5.ComputeHash($bytes)
        $md5.Dispose()
        return (($hash | ForEach-Object { $_.ToString("x2") }) -join "").Substring(0, 16)
    } catch {
        return ([math]::Abs($Text.GetHashCode())).ToString("x")
    }
}

function Get-ConfigValue([string]$Key) {
    if (-not (Test-Path -LiteralPath $configFile)) { return $null }
    foreach ($line in (Get-Content -LiteralPath $configFile -Encoding UTF8 -ErrorAction SilentlyContinue)) {
        $pattern = "^\s*" + [regex]::Escape($Key) + "\s*=\s*(.+?)\s*$"
        if ($line -match $pattern) { return $Matches[1].Trim('"') }
    }
    return $null
}

function Set-ConfigValue([string]$Key, [string]$Value) {
    $data = [ordered]@{}
    if (Test-Path -LiteralPath $configFile) {
        foreach ($line in (Get-Content -LiteralPath $configFile -Encoding UTF8 -ErrorAction SilentlyContinue)) {
            $m = [regex]::Match($line, '^\s*([A-Za-z0-9_]+)\s*=\s*(.*?)\s*$')
            if ($m.Success) { $data[$m.Groups[1].Value.ToLower()] = $m.Groups[2].Value.Trim('"') }
        }
    }
    $data[$Key.ToLower()] = $Value
    $out = @(
        "# Dropstone configuration.",
        "# watchDir: your LeviLauncher 'versions' folder.",
        "# launcherExe: path to LeviLauncher.exe."
    )
    foreach ($k in $data.Keys) { $out += "$k=$($data[$k])" }
    Set-Content -LiteralPath $configFile -Value $out -Encoding UTF8
}

function Test-VersionsDir([string]$Path) {
    if ($Path -and (Test-Path -LiteralPath $Path -PathType Container)) { return $Path.TrimEnd('\') }
    return $null
}

function Test-GameVersionFolder([string]$Path) {
    if (Test-Path -LiteralPath (Join-Path $Path "Minecraft.Windows.exe")) { return $true }
    if (Test-Path -LiteralPath (Join-Path $Path "Minecraft.Win10.DX11.exe")) { return $true }
    return $false
}

function Test-LooksLikeVersionsDir([string]$Path) {
    # A versions folder must contain at least one game version folder, i.e. a
    # sub folder holding Minecraft.Windows.exe or Minecraft.Win10.DX11.exe.
    if (-not $Path) { return $false }
    try {
        foreach ($child in (Get-ChildItem -LiteralPath $Path -Directory -Force -ErrorAction SilentlyContinue)) {
            if (Test-GameVersionFolder $child.FullName) { return $true }
        }
    } catch { }
    return $false
}

function Resolve-Candidate([string]$Path) {
    # a candidate only has to exist here; the "strong" check (does it contain a
    # game version folder?) is applied when the candidates are ranked
    $v = Test-VersionsDir $Path
    if (-not $v) { return $null }
    return $v
}

function Select-BestCandidate([object[]]$Candidates) {
    if (-not $Candidates -or $Candidates.Count -eq 0) { return $null }
    foreach ($candidate in $Candidates) {
        if ($candidate -and (Test-LooksLikeVersionsDir $candidate)) { return $candidate }
    }
    foreach ($candidate in $Candidates) {
        if ($candidate) { return $candidate }
    }
    return $null
}

function Get-VersionsFromLauncherConfig {
    $cfgPath = Join-Path $env:APPDATA "levilauncher.exe\config.json"
    if (-not (Test-Path -LiteralPath $cfgPath)) { return $null }
    try { $json = Get-Content -LiteralPath $cfgPath -Raw | ConvertFrom-Json } catch { return $null }
    $base = $null
    if ($json -and $json.PSObject.Properties["base_root"]) { $base = [string]$json.base_root }
    if ([string]::IsNullOrWhiteSpace($base)) { $base = Join-Path $env:APPDATA "levilauncher.exe" }
    return (Resolve-Candidate (Join-Path $base "versions"))
}

function Get-VersionsFromLauncherExe {
    $exe = Get-ConfigValue "launcherExe"
    if (-not $exe) { return $null }
    $dir = Split-Path -Parent $exe
    if (-not $dir) { return $null }
    return (Resolve-Candidate (Join-Path $dir "versions"))
}

function Find-DefaultVersions {
    $results = @(
        (Resolve-Candidate (Join-Path $env:APPDATA "levilauncher.exe\versions")),
        (Resolve-Candidate (Join-Path $env:LOCALAPPDATA "levilauncher.exe\versions"))
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
            $found = Resolve-Candidate (Join-Path $exeDir "versions")
            if ($found) { return $found }
        } catch { }
    }
    return $null
}

function Find-PortableVersions {
    $skip = @('Windows', 'Program Files', 'Program Files (x86)', 'ProgramData', '$Recycle.Bin', 'System Volume Information', 'Recovery')
    foreach ($drive in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        if ($drive.Root -notmatch '^[A-Za-z]:\\$') { continue }
        if ($drive.DisplayRoot -and $drive.DisplayRoot -like '\\*') { continue }   # skip network drives

        $bases = @($drive.Root)
        $topDirs = @(Get-ChildItem -LiteralPath $drive.Root -Directory -Force -ErrorAction SilentlyContinue | Where-Object { $skip -notcontains $_.Name })
        $bases += $topDirs.FullName
        foreach ($top in $topDirs) {
            $bases += (Get-ChildItem -LiteralPath $top.FullName -Directory -Force -ErrorAction SilentlyContinue).FullName
        }

        foreach ($base in $bases) {
            foreach ($folderName in @("levilauncher.exe", "levilauncher")) {
                $found = Resolve-Candidate (Join-Path $base "$folderName\versions")
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
        if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { return $dialog.SelectedPath }
    } catch { }
    return $null
}

function Enter-SyncLock {
    # A trigger that arrives while another sync is running WAITS for it
    # instead of being dropped, so a version folder that appears during a run
    # is still patched.
    $name = "Local\DropstoneSync_" + (Get-NameHash $scriptDir)
    $m = New-Object System.Threading.Mutex($false, $name)
    $deadline = (Get-Date).AddSeconds($LockTimeoutSeconds)
    $waited = $false
    while ($true) {
        try {
            if ($m.WaitOne(0)) { if ($waited) { Write-Log "Acquired the sync lock after waiting." }; return $m }
        } catch [System.Threading.AbandonedMutexException] {
            # the previous owner died - the lock is ours now
            Write-Log "WARN: the previous sync did not release its lock - continuing."
            return $m
        }
        if ((Get-Date) -ge $deadline) {
            Write-Log "WARN: another sync is still running after $LockTimeoutSeconds s - giving up."
            $m.Dispose()
            return $null
        }
        if (-not $waited) { $waited = $true; Write-Log "Another sync is running - waiting for it to finish." }
        Start-Sleep -Milliseconds 500
    }
}

function Enter-TargetLock([string]$Target) {
    if (-not $Target) { return $null }
    $name = "Local\DropstoneTarget_" + (Get-NameHash $Target)
    $m = New-Object System.Threading.Mutex($false, $name)
    $deadline = (Get-Date).AddSeconds($LockTimeoutSeconds)
    while ($true) {
        try {
            if ($m.WaitOne(0)) { return $m }
        } catch [System.Threading.AbandonedMutexException] {
            return $m
        }
        if ((Get-Date) -ge $deadline) {
            Write-Log "WARN: another sync is still writing to '$Target' - giving up."
            $m.Dispose()
            return $null
        }
        Start-Sleep -Milliseconds 500
    }
}

function Get-FileSha256([string]$Path) {
    try { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash } catch { return $null }
}

function Copy-Atomic {
    # Copies a file into the destination folder and verifies that it arrived
    # completely. Small files are copied directly (the copy is verified
    # afterwards); large files go through a temporary file and a rename, so an
    # interrupted copy can never leave a truncated file behind.
    param([string]$Source, [string]$Destination)
    $sourceInfo = Get-Item -LiteralPath $Source
    if ($sourceInfo.Length -ge 32MB) {
        $temp = $Destination + ".ds-tmp-" + ([guid]::NewGuid().ToString("N"))
        try {
            Copy-Item -LiteralPath $Source -Destination $temp -Force -ErrorAction Stop
            Move-Item -LiteralPath $temp -Destination $Destination -Force -ErrorAction Stop
        } catch {
            try { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue } } catch { }
            throw
        }
    } else {
        Copy-Item -LiteralPath $Source -Destination $Destination -Force -ErrorAction Stop
    }

    $copied = Get-Item -LiteralPath $Destination -Force
    if ($copied.Length -ne $sourceInfo.Length) {
        # an incomplete copy must not survive: the next sync would skip the
        # file because it exists
        Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
        throw "the copy is incomplete ($($copied.Length) of $($sourceInfo.Length) bytes)"
    }
}

function Get-Manifest {
    if (-not (Test-Path -LiteralPath $manifestFile)) { return $null }
    try { return (Get-Content -LiteralPath $manifestFile -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { return $null }
}

function Save-Manifest([object]$Manifest) {
    try {
        $json = $Manifest | ConvertTo-Json -Depth 6
        Set-Content -LiteralPath $manifestFile -Value $json -Encoding UTF8
    } catch {
        Write-Log "WARN: could not write the deployment manifest: $($_.Exception.Message)"
    }
}

function Invoke-SyncCore {
    Write-Log "---- sync started (pid $PID) ----"
    $mutex = $null
    $targetMutex = $null

    try {
        if (-not (Test-Path -LiteralPath $FilesDir -PathType Container)) {
            Write-Log "ERROR: payload folder not found: $FilesDir"
            Show-MessageBox "Payload folder not found:`n$FilesDir`n`nRun the installer again and choose a payload archive."
            return 1
        }

        $payloadRoot = (Resolve-Path -LiteralPath $FilesDir).Path.TrimEnd('\')
        $enumErrors = @()
        $payload = @(Get-ChildItem -LiteralPath $FilesDir -File -Recurse -ErrorAction SilentlyContinue -ErrorVariable enumErrors)
        $unreadable = 0
        foreach ($enumError in $enumErrors) {
            $unreadable++
            Write-Log "ERROR: could not read a payload file: $($enumError.Exception.Message)"
        }

        if ($payload.Count -eq 0) {
            Write-Log "No payload files in $FilesDir - nothing to do."
            return 0
        }

        # ------------------------------------------------------- resolve target
        $targetDir = $null

        if ($VersionsDir) {
            $targetDir = Test-VersionsDir $VersionsDir
            if (-not $targetDir) {
                Write-Log "ERROR: the requested versions folder does not exist: $VersionsDir"
                Show-MessageBox "The LeviLauncher versions folder does not exist:`n$VersionsDir"
                return 2
            }
        }

        if (-not $targetDir) {
            $pinned = Get-ConfigValue "watchDir"
            if ($pinned) {
                # The pinned folder is authoritative. If it is gone we must
                # fail - silently switching to a different LeviLauncher would
                # deploy the payload into the wrong place.
                $targetDir = Test-VersionsDir $pinned
                if (-not $targetDir) {
                    Write-Log "ERROR: the configured versions folder no longer exists: $pinned"
                    Show-MessageBox "The configured LeviLauncher 'versions' folder is gone:`n$pinned`n`nUpdate watchDir in config.ini or reinstall Dropstone."
                    return 2
                }
                # The configured folder is used, but the user is told when the
                # launcher has moved somewhere else.
                $live = Get-VersionsFromLauncherConfig
                if ($live -and $live -ne $targetDir) {
                    Write-Log "WARN: the configured versions folder '$targetDir' differs from the launcher's current folder '$live'. Use 'Replace payload' in Dropstone to point the sync at the new folder."
                }
            }
        }

        if (-not $targetDir) {
            if ($NoAutoDetect) {
                Write-Log "ERROR: no versions folder configured and auto-detection is disabled."
                Show-MessageBox "The LeviLauncher 'versions' folder is not configured.`n`nSet watchDir in config.ini next to the script."
                return 2
            }
            $candidates = @(
                (Get-VersionsFromLauncherExe),
                (Get-VersionsFromLauncherConfig),
                (Resolve-Candidate (Join-Path $env:APPDATA "levilauncher.exe\versions")),
                (Resolve-Candidate (Join-Path $env:LOCALAPPDATA "levilauncher.exe\versions")),
                (Find-RunningLauncherVersions),
                (Find-PortableVersions)
            )
            $detected = Select-BestCandidate $candidates
            if (-not $detected -and -not $Quiet) { $detected = Select-WatchDirDialog }

            if (-not $detected) {
                Write-Log "ERROR: no LeviLauncher 'versions' folder found."
                Show-MessageBox "Could not find the LeviLauncher 'versions' folder.`n`nEdit config.ini next to the script and set:`nwatchDir=<your path>"
                return 2
            }
            $targetDir = $detected.TrimEnd('\')
            if (-not $NoSave) { Set-ConfigValue "watchDir" $targetDir }
        }

        # ------------------------------------------------------------- locks
        $mutex = Enter-SyncLock
        if (-not $mutex) {
            Write-Log "WARN: another sync is holding the lock - this run was skipped."
            return 4
        }
        $targetMutex = Enter-TargetLock $targetDir
        if (-not $targetMutex) {
            Write-Log "WARN: another sync is writing to '$targetDir' - this run was skipped."
            return 4
        }

        # -------------------------------------------------------------- copy
        $manifest = Get-Manifest
        $managedByVersion = @{}
        if ($manifest -and $manifest.versions) {
            foreach ($v in $manifest.versions) { $managedByVersion[[string]$v.path] = @($v.files) }
        }
        # the manifest is merged, never replaced: files deployed by an earlier
        # run stay known, so a later uninstall can still remove them
        $manifestFiles = @{}
        foreach ($key in $managedByVersion.Keys) { $manifestFiles[$key] = @($managedByVersion[$key]) }

        $versions = @(Get-ChildItem -LiteralPath $targetDir -Directory -Force -ErrorAction SilentlyContinue)

        if ($SettleSeconds -gt 0) {
            $settleDeadline = (Get-Date).AddSeconds($SettleSeconds)
            $waitAnnounced = @{}
            while ($true) {
                $waiting = @()
                foreach ($v in $versions) {
                    if (Test-GameVersionFolder $v.FullName) { continue }
                    if (((Get-Date) - $v.CreationTime).TotalMinutes -gt 15) { continue }
                    $waiting += $v
                    if (-not $waitAnnounced.ContainsKey($v.FullName)) {
                        $waitAnnounced[$v.FullName] = $true
                        Write-Log "WAIT: '$($v.FullName)' has no game executable yet - waiting up to $SettleSeconds s for the launcher to finish the folder."
                    }
                }
                if ($waiting.Count -eq 0) { break }
                if ((Get-Date) -ge $settleDeadline) {
                    foreach ($v in $waiting) {
                        Write-Log "WAIT: timed out for '$($v.FullName)' - no game executable after $SettleSeconds s."
                    }
                    break
                }
                Start-Sleep -Milliseconds 1000
                $versions = @(Get-ChildItem -LiteralPath $targetDir -Directory -Force -ErrorAction SilentlyContinue)
            }
        }

        $copied = 0
        $repaired = 0
        $failed = $unreadable
        $skipped = 0
        $touchedFolders = 0

        foreach ($version in $versions) {
            $versionDir = $version.FullName
            $hasGame = Test-GameVersionFolder $versionDir
            if (-not $hasGame) {
                Write-Log "SKIP: '$versionDir' has no Minecraft.Windows.exe / Minecraft.Win10.DX11.exe - not a game version folder."
                continue
            }

            $folderCopied = 0
            $folderFiles = @()
            foreach ($file in $payload) {
                $relativePath = $file.FullName.Substring($payloadRoot.Length).TrimStart('\')
                $destPath = Join-Path $versionDir $relativePath
                $destDir = Split-Path -Parent $destPath
                $needCopy = $false
                $reason = ""

                if (-not (Test-Path -LiteralPath $destPath)) {
                    $needCopy = $true; $reason = "new"
                } elseif ($Force) {
                    $needCopy = $true; $reason = "forced"
                } else {
                    $srcLen = $file.Length
                    $dstLen = 0
                    try { $dstLen = (Get-Item -LiteralPath $destPath -Force).Length } catch { }
                    if ($dstLen -ne $srcLen) {
                        $needCopy = $true; $reason = "size differs ($dstLen -> $srcLen)"
                    } elseif ($VerifyHash) {
                        $srcHash = Get-FileSha256 $file.FullName
                        $dstHash = Get-FileSha256 $destPath
                        if ($srcHash -and $dstHash -and $srcHash -ne $dstHash) {
                            $needCopy = $true; $reason = "content differs"
                        }
                    }
                }

                if (-not $needCopy) {
                    $skipped++
                    Write-Log "SKIP: '$relativePath' already exists in '$versionDir' with the same size - a foreign file is not overwritten."
                    continue
                }

                try {
                    if (-not (Test-Path -LiteralPath $destDir -PathType Container)) {
                        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
                    }
                    Copy-Atomic -Source $file.FullName -Destination $destPath
                    $copied++
                    $folderCopied++
                    if ($reason -ne "new") { $repaired++ }
                    $folderFiles += $relativePath
                } catch {
                    $failed++
                    Write-Log "ERROR: failed to copy $relativePath to $versionDir : $($_.Exception.Message)"
                }
            }
            if ($folderCopied -gt 0) {
                $touchedFolders++
                $existing = @()
                if ($manifestFiles.ContainsKey($versionDir)) { $existing = @($manifestFiles[$versionDir]) }
                $manifestFiles[$versionDir] = @($existing + $folderFiles | Select-Object -Unique)
            }
        }

        if ($manifestFiles.Count -gt 0) {
            $entries = @()
            foreach ($key in $manifestFiles.Keys) {
                $entries += ,([pscustomobject]@{ path = $key; files = @($manifestFiles[$key]) })
            }
            Save-Manifest ([pscustomobject]@{
                schema      = 1
                generatedAt = (Get-Date).ToString("o")
                target      = $targetDir
                versions    = $entries
            })
        }

        Write-Log "Sync complete: copied $copied file(s), repaired $repaired, skipped $skipped, into $touchedFolders version folder(s) under $targetDir (failed: $failed)."

        Write-Info "Dropstone sync finished."
        Write-Info "  versions:  $targetDir"
        Write-Info "  payload:   $FilesDir ($($payload.Count) file(s))"
        Write-Info "  copied:    $copied"
        Write-Info "  repaired:  $repaired"
        Write-Info "  skipped:   $skipped"
        Write-Info "  failed:    $failed"
        if ($failed -gt 0) { Write-Info "  see $logFile for details" }

        if ($failed -gt 0) { return 3 }
        return 0
    } catch {
        Write-Log "ERROR: unexpected failure: $($_.Exception.Message)"
        Show-MessageBox "The Dropstone sync failed:`n$($_.Exception.Message)"
        return 1
    } finally {
        if ($targetMutex) { try { $targetMutex.ReleaseMutex(); $targetMutex.Dispose() } catch { } }
        if ($mutex) { try { $mutex.ReleaseMutex(); $mutex.Dispose() } catch { } }
        Write-Log "---- sync finished ----"
    }
}

$result = Invoke-SyncCore
$code = 1
if ($result -is [int]) {
    $code = [int]$result
} elseif ($result -is [array] -and $result.Count -gt 0) {
    $last = $result[$result.Count - 1]
    if ($last -is [int]) { $code = [int]$last }
} elseif ($result -is [string] -and $result -match '^\d+$') {
    $code = [int]$result
}
exit $code
