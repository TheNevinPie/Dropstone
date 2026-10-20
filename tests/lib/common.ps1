# Shared helpers for the Dropstone stability suite.
#
# SAFETY CONTRACT
#   Every helper in this file exists so that a test can never write into a real
#   LeviLauncher versions folder. The two mechanisms are:
#     1. sandboxed APPDATA/LOCALAPPDATA  -> the sync's discovery chain always
#        resolves inside the sandbox;
#     2. Assert-Sandboxed                -> aborts the run the moment any
#        resolved/pinned path escapes the sandbox.

Set-StrictMode -Version Latest

# Tests are separate script files, so shared state has to live in globals.
# The guards below matter: every test file dot-sources this file again, and a
# naive re-initialisation would wipe the results collected so far.
if (-not (Get-Variable -Name DropstoneTestRoot -Scope Global -ErrorAction SilentlyContinue)) {
    $global:DropstoneTestRoot  = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path            # ...\Dropstone\tests
    $global:DropstoneRepoRoot  = (Resolve-Path (Join-Path $global:DropstoneTestRoot "..")).Path  # ...\Dropstone
    $global:DropstoneOrigAppData = $env:APPDATA
    $global:DropstoneOrigLocalAppData = $env:LOCALAPPDATA
}
if (-not (Get-Variable -Name DropstoneResults -Scope Global -ErrorAction SilentlyContinue)) {
    $global:DropstoneResults = New-Object System.Collections.Generic.List[object]
}

function Get-ArtifactsDir {
    $d = Join-Path $global:DropstoneTestRoot "artifacts"
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
    return $d
}

function Get-RepoRoot {
    return $global:DropstoneRepoRoot
}

function Get-PublishedExe {
    $p = Join-Path (Get-RepoRoot) "publish\Dropstone.exe"
    if (Test-Path $p) { return $p }
    throw "publish\Dropstone.exe not found - run .\build.ps1 first."
}

function Get-WorkerSource {
    return (Join-Path (Get-RepoRoot) "src\Dropstone\Worker\Sync.ps1")
}

# ---------------------------------------------------------------- result recording
function Record-Result {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Test,
        [Parameter(Mandatory)][bool]$Pass,
        [string]$Detail = "",
        [string]$Severity = ""
    )
    $global:DropstoneResults.Add([pscustomobject]@{
        Id       = $Id
        Test     = $Test
        Result   = $(if ($Pass) { "PASS" } else { "FAIL" })
        Severity = $Severity
        Detail   = $Detail
    })
    Write-Host ("[{0}] {1,-4} {2,-48} :: {3}" -f $Id, $(if ($Pass) { "PASS" } else { "FAIL" }), $Test, $Detail)
}

function Complete-Results {
    $out = Join-Path (Get-ArtifactsDir) "results.csv"
    $global:DropstoneResults | Export-Csv -LiteralPath $out -NoTypeInformation
    Write-Host ""
    Write-Host "=== SUMMARY ==="
    $global:DropstoneResults | Format-Table -AutoSize -Wrap | Out-String -Width 200 | Write-Host
    $fail = @($global:DropstoneResults | Where-Object Result -eq "FAIL").Count
    $pass = @($global:DropstoneResults | Where-Object Result -eq "PASS").Count
    Write-Host ("PASS=$pass  FAIL=$fail   (results: $out)")
    if ($pass -eq 0 -and $fail -eq 0) { Write-Host "WARNING: no tests were executed"; return 1 }
    return $fail
}

# ---------------------------------------------------------------- sandbox
function New-Sandbox {
    <#
      Builds a sandbox that mirrors a LeviLauncher install:
        <sandbox>\Dropstone\          -> the "install folder" (payload + worker)
        <sandbox>\levilauncher\versions\1.0.0\Minecraft.Windows.exe
        <sandbox>\AppData\Roaming\levilauncher.exe\config.json  (fake launcher cfg)
      The fake launcher config makes the sync's discovery chain resolve inside
      the sandbox, so a broken watchDir can never fall through to a real
      LeviLauncher.
    #>
    param(
        [Parameter(Mandatory)][string]$Name,
        [int]$VersionFolders = 1
    )
    $s = Join-Path (Get-ArtifactsDir) ("sbx-" + $Name)
    if (Test-Path $s) { Remove-Item -Recurse -Force $s -ErrorAction SilentlyContinue }
    $install = Join-Path $s "Dropstone"
    $versions = Join-Path $s "levilauncher\versions"
    New-Item -ItemType Directory -Path "$install\payload" -Force | Out-Null
    for ($i = 1; $i -le $VersionFolders; $i++) {
        $v = Join-Path $versions ("{0}.0.0" -f $i)
        New-Item -ItemType Directory -Path $v -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $v "Minecraft.Windows.exe") -Value "stub"
    }
    # fake APPDATA (both Roaming and Local) so the discovery chain stays inside
    foreach ($base in @("Roaming", "Local")) {
        $root = Join-Path $s "AppData\$base"
        $cfgDir = Join-Path $root "levilauncher.exe"
        New-Item -ItemType Directory -Path (Join-Path $cfgDir "versions\1.0.0") -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $cfgDir "versions\1.0.0\Minecraft.Windows.exe") -Value "stub"
        if ($base -eq "Roaming") {
            @{ base_root = (Join-Path $s "levilauncher") } | ConvertTo-Json |
                Set-Content -LiteralPath (Join-Path $cfgDir "config.json")
        }
    }
    Copy-Item (Get-WorkerSource) "$install\Sync.ps1" -Force
    Set-Content -LiteralPath "$install\config.ini" -Value ("watchDir=" + $versions) -Encoding UTF8

    $global:DropstoneCurrentSandbox = [pscustomobject]@{
        Root        = $s
        Install     = $install
        Versions    = $versions
        FakeRoaming = (Join-Path $s "AppData\Roaming")
        FakeLocal   = (Join-Path $s "AppData\Local")
        Payload     = "$install\payload"
    }
    return $global:DropstoneCurrentSandbox
}

function Use-SandboxEnv {
    param($Sandbox)
    $env:APPDATA = $Sandbox.FakeRoaming
    $env:LOCALAPPDATA = $Sandbox.FakeLocal
}

function Restore-Env {
    $env:APPDATA = $global:DropstoneOrigAppData
    $env:LOCALAPPDATA = $global:DropstoneOrigLocalAppData
}

function Invoke-Sync {
    param(
        $Sandbox,
        [switch]$Quiet,
        [string[]]$Extra = @()
    )
    Use-SandboxEnv $Sandbox
    $a = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "$($Sandbox.Install)\Sync.ps1")
    if ($Quiet) { $a += "-Quiet" }
    if ($Extra) { $a += $Extra }
    $out = & powershell.exe @a 2>&1
    $code = $LASTEXITCODE
    Restore-Env
    Assert-Sandboxed $Sandbox | Out-Null
    return [pscustomobject]@{ Exit = $code; Out = ($out | Out-String) }
}

function Get-PinnedWatchDir {
    param($Sandbox)
    $cfg = Get-Content -LiteralPath "$($Sandbox.Install)\config.ini" -ErrorAction SilentlyContinue
    $line = $cfg | Where-Object { $_ -match '^\s*watchdir\s*=' } | Select-Object -First 1
    if ($line) { return ($line -replace '^\s*watchdir\s*=\s*', '').Trim() }
    return $null
}

function Assert-Sandboxed {
    <#
      SAFETY TRIPWIRE. Throws if the worker ever pinned a path outside the
      sandbox (the silent wrong-target write bug).
    #>
    param($Sandbox)
    $pin = Get-PinnedWatchDir $Sandbox
    if ($pin -and -not $pin.StartsWith($Sandbox.Root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "SAFETY TRIPWIRE: worker pinned watchDir outside the sandbox -> $pin"
    }
    return $pin
}

function Reset-Payload {
    param($Sandbox)
    Remove-Item "$($Sandbox.Payload)\*" -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- UI Automation
function Initialize-Uia {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
}

function Get-AppProcess {
    param([string]$ExeFolder)
    $p = Get-Process -Name "Dropstone" -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -like "$ExeFolder*" } | Select-Object -First 1
    return $p
}

function Get-UiText {
    param([int]$ProcessId)
    Initialize-Uia
    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $proc -or $proc.MainWindowHandle -eq 0) { return @() }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
    if (-not $root) { return @() }
    $els = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                          [System.Windows.Automation.Condition]::TrueCondition)
    $out = @()
    foreach ($e in $els) {
        try {
            $n = $e.Current.Name
            $id = $e.Current.AutomationId
            # elements without a name still matter when they carry an id
            # (for example the InfoBar that shows an error)
            if ($n -or $id) {
                $out += [pscustomobject]@{
                    Type = ($e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '')
                    Name = [string]$n
                    Id   = [string]$id
                }
            }
        } catch { }
    }
    return $out
}

function Invoke-UiButton {
    param([int]$ProcessId, [string]$Name)
    Initialize-Uia
    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $proc -or $proc.MainWindowHandle -eq 0) { return $false }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
    if (-not $root) { return $false }
    $els = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                          [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $els) {
        try {
            if ($e.Current.Name -eq $Name -and $e.Current.ControlType.ProgrammaticName -like '*Button*') {
                $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                return $true
            }
        } catch { }
    }
    return $false
}

function Get-UiField {
    # Safe accessor: returns an empty string when the element is not present.
    param([int]$ProcessId, [string]$Id, [string]$Property = "Name")
    $found = @(Get-UiText -ProcessId $ProcessId | Where-Object { $_.Id -eq $Id })
    if ($found.Count -eq 0) { return "" }
    return [string]$found[0].$Property
}

function Get-UiCheckBoxes {
    param([int]$ProcessId)
    Initialize-Uia
    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $proc -or $proc.MainWindowHandle -eq 0) { return @() }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
    if (-not $root) { return @() }
    $els = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                          [System.Windows.Automation.Condition]::TrueCondition)
    $out = @()
    foreach ($e in $els) {
        try {
            if ($e.Current.ControlType.ProgrammaticName -like '*CheckBox*') {
                $out += [pscustomobject]@{
                    Name  = $e.Current.Name
                    State = $e.Current.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
                    Element = $e
                }
            }
        } catch { }
    }
    return $out
}

function Toggle-UiCheckBox {
    param([int]$ProcessId, [string]$Name)
    foreach ($cb in (Get-UiCheckBoxes -ProcessId $ProcessId)) {
        if ($cb.Name -like $Name) {
            $cb.Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
            return $true
        }
    }
    return $false
}

# ---------------------------------------------------------------- elevation
function Invoke-ElevatedScript {
    <#
      Runs a script elevated. On machines with ConsentPromptBehaviorAdmin=5 a
      Windows binary elevates silently; otherwise a UAC prompt appears and the
      user has to approve it. Returns the exit code of the elevated process.
    #>
    param([string]$ScriptPath)
    $p = Start-Process -FilePath "powershell.exe" -Verb RunAs -PassThru -WindowStyle Hidden `
            -ArgumentList "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$ScriptPath`""
    $p.WaitForExit(180000) | Out-Null
    return $p.ExitCode
}

