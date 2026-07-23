param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $projectRoot 'CodexChannelLauncher.csproj'
$iconBuilder = Join-Path $projectRoot 'tools\build-icon.ps1'
$publishDir = Join-Path $projectRoot 'artifacts\publish'
$distDir = Join-Path $projectRoot 'dist'
$iconPreview = Join-Path $projectRoot 'artifacts\icon-preview-32.png'
$finalName = [Text.Encoding]::UTF8.GetString(
    [Convert]::FromBase64String('Q29kZXjlpJrlvIDlmaguZXhl'))
$finalExe = Join-Path $distDir $finalName
$stagedExe = "$finalExe.new"
$backupExe = "$finalExe.previous"

function Assert-PublishedIconMatches {
    param(
        [string]$ExpectedPreview,
        [string]$PublishedExe
    )

    Add-Type -AssemblyName System.Drawing
    $expectedBitmap = [System.Drawing.Bitmap]::new($ExpectedPreview)
    $actualIcon = [System.Drawing.Icon]::ExtractAssociatedIcon($PublishedExe)
    if ($null -eq $actualIcon) {
        $expectedBitmap.Dispose()
        throw "Published executable does not contain an associated icon."
    }

    try {
        $actualBitmap = $actualIcon.ToBitmap()
        try {
            if ($expectedBitmap.Width -ne $actualBitmap.Width -or
                $expectedBitmap.Height -ne $actualBitmap.Height) {
                throw "Published executable icon dimensions do not match the source icon."
            }

            for ($y = 0; $y -lt $expectedBitmap.Height; $y++) {
                for ($x = 0; $x -lt $expectedBitmap.Width; $x++) {
                    if ($expectedBitmap.GetPixel($x, $y).ToArgb() -ne
                        $actualBitmap.GetPixel($x, $y).ToArgb()) {
                        throw "Published executable icon does not match the source icon."
                    }
                }
            }
        }
        finally {
            $actualBitmap.Dispose()
        }
    }
    finally {
        $expectedBitmap.Dispose()
        $actualIcon.Dispose()
    }
}

function Notify-ShellItemChanged {
    param([string]$Path)

    if (-not ('LauncherShellChangeNotifier' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class LauncherShellChangeNotifier
{
    private const uint ShcneUpdateItem = 0x00002000;
    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;
    private const uint ShcnfPathW = 0x0005;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        string item1,
        IntPtr item2);

    public static void Notify(string path)
    {
        SHChangeNotify(ShcneUpdateItem, ShcnfPathW, path, IntPtr.Zero);
        SHChangeNotify(ShcneAssocChanged, ShcnfIdList, null, IntPtr.Zero);
    }
}
'@
    }

    [LauncherShellChangeNotifier]::Notify($Path)
}

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

$publishedExe = Join-Path $publishDir 'CodexChannelLauncher.exe'
Remove-Item -LiteralPath $stagedExe -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $backupExe -Force -ErrorAction SilentlyContinue

try {
    Copy-Item -LiteralPath $publishedExe -Destination $stagedExe
    Assert-PublishedIconMatches -ExpectedPreview $iconPreview -PublishedExe $stagedExe

    if (Test-Path -LiteralPath $finalExe -PathType Leaf) {
        [System.IO.File]::Replace($stagedExe, $finalExe, $backupExe, $true)
    }
    else {
        [System.IO.File]::Move($stagedExe, $finalExe)
    }

    Assert-PublishedIconMatches -ExpectedPreview $iconPreview -PublishedExe $finalExe
    Notify-ShellItemChanged -Path $finalExe
}
finally {
    Remove-Item -LiteralPath $stagedExe -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $backupExe -Force -ErrorAction SilentlyContinue
}

Write-Host "Published with verified icon: $finalExe"
