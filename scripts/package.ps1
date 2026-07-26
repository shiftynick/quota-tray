[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

if ($Runtime -notmatch '^win-(x64|arm64)$') {
    throw "Unsupported runtime '$Runtime'. Use win-x64 or win-arm64."
}

if ($Configuration -notin @('Debug', 'Release')) {
    throw "Unsupported configuration '$Configuration'. Use Debug or Release."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $repoRoot "artifacts\QuotaTray-$Runtime"
$archivePath = Join-Path $repoRoot "artifacts\QuotaTray-$Runtime.zip"

if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse
}

dotnet publish (Join-Path $repoRoot 'src\QuotaTray\QuotaTray.csproj') `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    --output $outputRoot

Copy-Item (Join-Path $repoRoot 'README.md') $outputRoot
Copy-Item (Join-Path $repoRoot 'LICENSE') $outputRoot
Get-ChildItem -LiteralPath $outputRoot -Filter '*.pdb' |
    Remove-Item

if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath
}

Compress-Archive -Path (Join-Path $outputRoot '*') -DestinationPath $archivePath
Get-FileHash -Algorithm SHA256 $archivePath |
    ForEach-Object { "$($_.Hash.ToLowerInvariant())  $(Split-Path -Leaf $archivePath)" } |
    Set-Content -Encoding ascii "$archivePath.sha256"

Write-Host "Created $archivePath"
