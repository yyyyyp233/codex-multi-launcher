param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $projectRoot 'CodexChannelLauncher.csproj'
$iconBuilder = Join-Path $projectRoot 'tools\build-icon.ps1'
$publishDir = Join-Path $projectRoot 'artifacts\publish'
$distDir = Join-Path $projectRoot 'dist'
$finalName = [Text.Encoding]::UTF8.GetString(
    [Convert]::FromBase64String('Q29kZXjlpJrlvIDlmaguZXhl'))
$finalExe = Join-Path $distDir $finalName

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

& $iconBuilder -ProjectRoot $projectRoot

dotnet publish $project `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Copy-Item -LiteralPath (Join-Path $publishDir 'CodexChannelLauncher.exe') -Destination $finalExe -Force
Write-Host "Published: $finalExe"
