# Archive expansion must be bounded (zip bomb) and long paths must work.
. "$PSScriptRoot\..\lib\e2e.ps1"

$sb = New-AppSandbox -Name "bomb"
$stage = Join-Path $sb.Root "stage"
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# 2 GB of zeros -> compresses to a few MB
$big = Join-Path $stage "bomb.bin"
$fs = [System.IO.File]::Create($big)
$chunk = New-Object byte[] (8MB)
for ($i = 0; $i -lt 256; $i++) { $fs.Write($chunk, 0, $chunk.Length) }
$fs.Close()

$rel = (("d" * 60) + "\") * 4 + ("leaf" + ("x" * 120) + ".dll")
$deep = Join-Path $stage $rel
New-Item -ItemType Directory -Path (Split-Path -Parent $deep) -Force | Out-Null
Set-Content -LiteralPath $deep -Value "deep"

$zip = Join-Path $sb.Root "payload\hostile.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Fastest, $false)
$zipSize = (Get-Item $zip).Length
Remove-Item $stage -Recurse -Force
Set-AppPreset -Root $sb.Root -PayloadPath $zip
Write-Host ("    archive = " + [math]::Round($zipSize/1MB,2) + " MB expanding to 2 GB")

$p = Start-App -Sandbox $sb
Invoke-UiButton -ProcessId $p.Id -Name "Install" | Out-Null
$res = Wait-AppResult -ProcessId $p.Id -TimeoutSeconds 600
Write-Host ("    result: " + $res.Title + " :: " + ($res.Message -replace "`r`n", " / "))

$payloadDir = Join-Path $sb.Install "payload"
$extracted = 0
if (Test-Path $payloadDir) {
    $m = Get-ChildItem $payloadDir -Recurse -File -Force | Measure-Object Length -Sum
    $extracted = $m.Sum
}
$ratio = if ($zipSize -gt 0) { $extracted / $zipSize } else { 0 }
# the archive expands to 2 GB - the installer has to refuse it
$rejected = ($extracted -lt 1GB) -or ($res.Title -ne "Installed")
Record-Result -Id "T21" -Test "archive expansion is bounded" -Pass $rejected -Severity "Medium" `
    -Detail ("archive " + [math]::Round($zipSize/1MB,2) + " MB -> extracted " + [math]::Round($extracted/1GB,2) + " GB (ratio " + [math]::Round($ratio,1) + "x) ; title='$($res.Title)' ; an archive above 1 GB must be refused")

$deepOut = Join-Path $payloadDir $rel
$deepOk = Test-Path -LiteralPath $deepOut
Write-Host ("    (the hostile archive is refused on purpose, so its deep path is not extracted: extracted = $deepOk)")
Stop-App -ProcessId $p.Id

# the deep path is checked with a benign archive (the hostile one is refused)
$benign = New-AppSandbox -Name "deep" -Payload @{ "a.dll" = "a" }
$stage2 = Join-Path $benign.Root "stage2"
New-Item -ItemType Directory -Path $stage2 -Force | Out-Null
$deep2 = Join-Path $stage2 $rel
New-Item -ItemType Directory -Path (Split-Path -Parent $deep2) -Force | Out-Null
Set-Content -LiteralPath $deep2 -Value "deep"
$zip2 = Join-Path $benign.Root "payload\deep.zip"
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage2, $zip2, [System.IO.Compression.CompressionLevel]::Fastest, $false)
Remove-Item $stage2 -Recurse -Force
Set-AppPreset -Root $benign.Root -PayloadPath $zip2

$p2 = Start-App -Sandbox $benign
Invoke-UiButton -ProcessId $p2.Id -Name "Install" | Out-Null
$res2 = Wait-AppResult -ProcessId $p2.Id -TimeoutSeconds 180
$deepOut2 = Join-Path $benign.Install "payload\$rel"
$deepOk2 = Test-Path -LiteralPath $deepOut2
$deployed2 = Test-Path -LiteralPath (Join-Path $benign.Versions "1.0.0\$rel")
Record-Result -Id "T24" -Test "a 400+ character payload path is handled" -Pass ($deepOk2 -and $deployed2) -Severity "Low" `
    -Detail ("deep path length = " + $deepOut2.Length + " ; extracted = $deepOk2 ; deployed into the version folder = $deployed2 ; title='$($res2.Title)'")
Stop-App -ProcessId $p2.Id

