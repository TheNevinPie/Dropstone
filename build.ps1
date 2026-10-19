$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "src\BedrockAutoCopyInstaller\BedrockAutoCopyInstaller.csproj"
$output = Join-Path $PSScriptRoot "publish"

if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
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

Write-Host ""
Write-Host "Published to $output"
Write-Host "Run: $(Join-Path $output 'BedrockAutoCopyInstaller.exe')"
