[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string[]]$AdditionalForbiddenPattern = @()
)

$ErrorActionPreference = 'Stop'
$environmentPatterns = $env:CODEX_LAUNCHER_FORBIDDEN_PATTERNS
if (-not [string]::IsNullOrWhiteSpace($environmentPatterns)) {
    $AdditionalForbiddenPattern += @($environmentPatterns -split ';' | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    })
}

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}

$root = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Repository root does not exist."
}

$excludedTopLevel = @(
    '.git', '.vs', 'bin', 'obj', 'artifacts', 'dist', 'TestResults', 'coverage'
)
$forbiddenFileNames = @(
    'auth.json', '.env', '.env.local', '.env.production'
)
$forbiddenExtensions = @(
    '.exe', '.dll', '.pdb', '.zip', '.7z', '.pfx', '.p12', '.snk', '.pem', '.key',
    '.sqlite', '.db'
)
$textExtensions = @(
    '.cs', '.xaml', '.csproj', '.slnx', '.ps1', '.md', '.json', '.yml', '.yaml',
    '.toml', '.xml', '.manifest', '.editorconfig', '.gitattributes', '.gitignore'
)
$allowedHosts = @(
    'github.com', 'openai.com', 'microsoft.com', 'schemas.microsoft.com',
    'dotnet.microsoft.com', 'img.shields.io', 'localhost', '127.0.0.1', '::1'
)

function Test-IsExcludedPath {
    param([string]$Path)

    $relative = $Path.Substring($root.Length).TrimStart('\', '/')
    if ([string]::IsNullOrWhiteSpace($relative)) {
        return $false
    }

    $segments = $relative -split '[\\/]'
    return @($segments | Where-Object { $excludedTopLevel -contains $_ }).Count -gt 0
}

function Get-CandidateFiles {
    $gitDirectory = Join-Path $root '.git'
    if (Test-Path -LiteralPath $gitDirectory -PathType Container) {
        $relativeFiles = & git -C $root ls-files --cached --others --exclude-standard
        if ($LASTEXITCODE -ne 0) {
            throw "git ls-files failed."
        }

        return @($relativeFiles | ForEach-Object {
            [System.IO.Path]::GetFullPath((Join-Path $root $_))
        } | Where-Object {
            (Test-Path -LiteralPath $_ -PathType Leaf) -and
            -not (Test-IsExcludedPath -Path $_)
        })
    }

    return @(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
        -not (Test-IsExcludedPath -Path $_.FullName)
    } | ForEach-Object FullName)
}

function Test-AllowedHost {
    param([string]$HostName)

    $ipAddress = $null
    if ([Net.IPAddress]::TryParse($HostName.Trim('[', ']'), [ref]$ipAddress) -and
        [Net.IPAddress]::IsLoopback($ipAddress)) {
        return $true
    }

    if ($HostName.EndsWith('.invalid', [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    foreach ($allowed in $allowedHosts) {
        if ($HostName.Equals($allowed, [StringComparison]::OrdinalIgnoreCase) -or
            $HostName.EndsWith('.' + $allowed, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

$findings = [System.Collections.Generic.List[string]]::new()
$files = Get-CandidateFiles
foreach ($path in $files) {
    $relative = $path.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
    $name = [System.IO.Path]::GetFileName($path)
    $extension = [System.IO.Path]::GetExtension($path)

    if ($forbiddenFileNames -contains $name -or
        $name.StartsWith('.env.', [StringComparison]::OrdinalIgnoreCase)) {
        $findings.Add("forbidden authentication file: $relative")
    }

    if ($forbiddenExtensions -contains $extension.ToLowerInvariant()) {
        $findings.Add("forbidden binary or local-data file: $relative")
    }

    if (-not ($textExtensions -contains $extension.ToLowerInvariant()) -and
        $name -notin @('.gitignore', '.gitattributes', '.editorconfig')) {
        continue
    }

    $content = [System.IO.File]::ReadAllText($path)
    if ($content -match '(?i)(?<![A-Za-z0-9_])[A-Z]:\\(?:Users|Documents|Projects|Work|Source|Repos)\\') {
        $findings.Add("fixed local Windows path: $relative")
    }

    if ($content -match '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----') {
        $findings.Add("private key material: $relative")
    }

    if ($content -match '(?im)(?:api[_-]?key|access[_-]?token|secret|password)\s*[\"'']?\s*[:=]\s*[\"''][A-Za-z0-9_./+=-]{20,}[\"'']') {
        $findings.Add("possible configured credential: $relative")
    }

    foreach ($match in [regex]::Matches($content, '(?i)https?://[^\s\"''<>()]+')) {
        $candidateUrl = $match.Value.TrimEnd('.', ',', ';')
        $uri = $null
        if ([Uri]::TryCreate($candidateUrl, [UriKind]::Absolute, [ref]$uri) -and
            -not (Test-AllowedHost $uri.Host)) {
            $findings.Add("non-public or unreviewed URL host in ${relative}: $($uri.Host)")
        }
    }

    foreach ($pattern in $AdditionalForbiddenPattern) {
        if (-not [string]::IsNullOrWhiteSpace($pattern) -and
            $content.IndexOf($pattern, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $findings.Add("caller-supplied forbidden content: $relative")
        }
    }
}

$gitRoot = Join-Path $root '.git'
if ((Test-Path -LiteralPath $gitRoot -PathType Container) -and $AdditionalForbiddenPattern.Count -gt 0) {
    $savedErrorPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & git -C $root rev-parse --verify HEAD 2> $null | Out-Null
    $hasHead = $LASTEXITCODE -eq 0
    $ErrorActionPreference = $savedErrorPreference
    if ($hasHead) {
        $history = (& git -C $root log --all --format=fuller --patch --no-ext-diff -- .) -join "`n"
        foreach ($pattern in $AdditionalForbiddenPattern) {
            if (-not [string]::IsNullOrWhiteSpace($pattern) -and
                $history.IndexOf($pattern, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $findings.Add('caller-supplied forbidden content exists in Git history')
            }
        }
    }
}

$uniqueFindings = @($findings | Sort-Object -Unique)
if ($uniqueFindings.Count -gt 0) {
    Write-Error ("Repository audit failed:`n - " + ($uniqueFindings -join "`n - "))
    exit 1
}

Write-Host "Repository audit passed: $($files.Count) candidate files checked."
