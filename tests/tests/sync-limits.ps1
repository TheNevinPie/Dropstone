# Path-length and payload-enumeration limits.
. "$PSScriptRoot\..\lib\common.ps1"

# ---------------------------------------------------------------- T9 deep path
$sb = New-Sandbox "limits-deep"
$rel = (("d" * 55) + "\") + (("e" * 55) + "\") + (("f" * 60) + ".dll")
$full = Join-Path $sb.Payload $rel
New-Item -ItemType Directory -Path (Split-Path -Parent $full) -Force | Out-Null
Set-Content -LiteralPath $full -Value "x"
$r = Invoke-Sync $sb -Quiet
$target = Join-Path "$($sb.Versions)\1.0.0" $rel
$deployed = Test-Path -LiteralPath $target
Record-Result -Id "T9" -Test "deep payload path deploys" -Pass $deployed -Severity "Low" `
    -Detail ("target path length = " + $target.Length + " ; deployed = $deployed ; exit = $($r.Exit)")
Assert-Sandboxed $sb | Out-Null

# ---------------------------------------------------------------- T18 many small files
$sb = New-Sandbox "limits-manyfiles"
for ($i = 1; $i -le 2000; $i++) { Set-Content -LiteralPath (Join-Path $sb.Payload ("f{0}.dat" -f $i)) -Value "x" }
$sw = [Diagnostics.Stopwatch]::StartNew()
$r = Invoke-Sync $sb -Quiet
$sw.Stop()
$copied = @(Get-ChildItem "$($sb.Versions)\1.0.0" -Filter "f*.dat").Count
$ok = ($copied -eq 2000) -and ($r.Exit -eq 0)
Record-Result -Id "T18" -Test "2000-file payload deploys completely" -Pass $ok -Severity "Medium" `
    -Detail ("copied $copied of 2000 in " + [math]::Round($sw.Elapsed.TotalSeconds,1) + "s ; exit = $($r.Exit)")
Assert-Sandboxed $sb | Out-Null
