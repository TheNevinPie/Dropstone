$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "src\Dropstone\Dropstone.csproj"
$output = Join-Path $PSScriptRoot "publish"
$release = Join-Path $PSScriptRoot "release"
$config = Join-Path $PSScriptRoot "packaging\config.ini"

[xml]$projectXml = Get-Content -LiteralPath $project
$version = $projectXml.Project.PropertyGroup.Version |
    Where-Object { $_ } |
    Select-Object -First 1
if (-not $version) {
    throw "Could not read Version from $project."
}

$releaseZip = Join-Path $release "Dropstone-$version-win-x64.zip"

foreach ($directory in @($output, $release)) {
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
}

$arguments = @(
    "publish",
    $project,
    "-c", "Release",
    "-r", "win-x64",
    "-o", $output
)

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$exe = Join-Path $output "Dropstone.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Publish did not produce Dropstone.exe."
}

Copy-Item -LiteralPath $config -Destination (Join-Path $output "config.ini")
New-Item -ItemType Directory -Path $release -Force | Out-Null
Compress-Archive -LiteralPath $exe, (Join-Path $output "config.ini") -DestinationPath $releaseZip

Write-Host ""
Write-Host "Published to $output"
Write-Host "Release ZIP: $releaseZip"
Write-Host "Run: $exe"
