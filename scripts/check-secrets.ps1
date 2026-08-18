<#
.SYNOPSIS
    Scans the repository for accidentally committed secrets.

.DESCRIPTION
    Reports only the file and line number of a suspicious match.
    It NEVER prints the matched value itself, so running this script
    (including in CI logs) can never leak a secret.

.EXAMPLE
    pwsh ./scripts/check-secrets.ps1
#>

[CmdletBinding()]
param(
    [string]$Path = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

# name -> regex. Keep patterns specific enough to avoid drowning in false positives.
$patterns = [ordered]@{
    'LINE channel access token' = 'channelAccessToken"\s*:\s*"[A-Za-z0-9+/=]{40,}'
    'Generic long secret value' = '"(Secret|Token|ApiKey|Password|ClientSecret)"\s*:\s*"[^"]{16,}"'
    'SQL connection with password' = 'Password\s*=\s*[^;"\s]{6,}'
    'Azure storage key' = 'AccountKey\s*=\s*[A-Za-z0-9+/=]{40,}'
    'AWS access key id' = '\bAKIA[0-9A-Z]{16}\b'
    'Private key block' = '-----BEGIN (RSA |EC |OPENSSH |PGP )?PRIVATE KEY-----'
    'Bearer token literal' = 'Bearer\s+[A-Za-z0-9\-\._~\+/]{30,}'
}

$excludedDirs = @('bin', 'obj', '.git', '.vs', 'node_modules', 'TestResults')
$excludedFiles = @('check-secrets.ps1')

# A scanner that reports the same known-good line on every push is a scanner the
# team stops reading, so a placeholder can be marked at its definition. The marker
# must sit on the flagged line or the one directly above it, and every honoured
# marker is listed below, so silencing a genuine secret still leaves a trail in
# the build log for a reviewer to challenge.
$suppressMarker = 'NOT-A-SECRET'

Write-Host "Scanning: $Path" -ForegroundColor Cyan

# The question this script answers is "did we commit a secret", so inside a git
# work tree it looks at exactly the tracked files. A local .env that .gitignore
# already keeps out of the repository is not a leak, and reporting one trains a
# developer to dismiss the result. Scanning the tracked set also makes a local
# run agree with CI, which only ever sees a fresh checkout.
$tracked = $null
if (Get-Command git -ErrorAction SilentlyContinue) {
    $listed = & git -c core.quotePath=false -C $Path ls-files 2>$null
    if ($LASTEXITCODE -eq 0 -and $listed) {
        $tracked = [System.Collections.Generic.HashSet[string]]::new(
            [string[]]($listed | ForEach-Object { $_ -replace '/', [IO.Path]::DirectorySeparatorChar }),
            [StringComparer]::OrdinalIgnoreCase)
        Write-Host "Tracked files only ($($tracked.Count) under git)." -ForegroundColor DarkGray
    }
}

$files = Get-ChildItem -Path $Path -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
    $relative = $_.FullName.Substring($Path.Length).TrimStart('\', '/')
    $segments = $relative -split '[\\/]'
    ($excludedDirs | Where-Object { $segments -contains $_ }).Count -eq 0 -and
    $excludedFiles -notcontains $_.Name -and
    $_.Length -lt 2MB -and
    ($null -eq $tracked -or $tracked.Contains($relative))
}

$findings = @()
$suppressed = @()

foreach ($file in $files) {
    $relative = $file.FullName.Substring($Path.Length).TrimStart('\', '/')
    $lines = @(Get-Content -LiteralPath $file.FullName -ErrorAction SilentlyContinue)

    foreach ($name in $patterns.Keys) {
        # Not $matches: that name is an automatic variable and assigning to it
        # collides with the engine's own match state.
        $hits = Select-String -Path $file.FullName -Pattern $patterns[$name] -AllMatches -ErrorAction SilentlyContinue
        foreach ($hit in $hits) {
            $onLine = $lines[$hit.LineNumber - 1]
            $above = if ($hit.LineNumber -ge 2) { $lines[$hit.LineNumber - 2] } else { '' }

            if ("$onLine`n$above" -match $suppressMarker) {
                $suppressed += [pscustomobject]@{
                    Rule = $name
                    File = $relative
                    Line = $hit.LineNumber
                }
                continue
            }

            $findings += [pscustomobject]@{
                Rule = $name
                File = $relative
                Line = $hit.LineNumber
            }
        }
    }
}

# Tracked files that should never be committed at all.
$forbidden = @('.env', 'secrets.json', 'appsettings.Local.json')
foreach ($file in $files) {
    if ($forbidden -contains $file.Name) {
        $findings += [pscustomobject]@{
            Rule = 'Forbidden file present'
            File = $file.FullName.Substring($Path.Length).TrimStart('\', '/')
            Line = 0
        }
    }
}

if ($suppressed.Count -gt 0) {
    Write-Host ""
    Write-Host "Suppressed by an explicit $suppressMarker marker ($($suppressed.Count)):" -ForegroundColor DarkYellow
    $suppressed | Sort-Object File, Line | Format-Table -AutoSize
}

if ($findings.Count -eq 0) {
    Write-Host "OK: no secret-like content found." -ForegroundColor Green
    exit 0
}

Write-Host ""
Write-Host "Potential secrets found ($($findings.Count)). Values are intentionally not displayed." -ForegroundColor Red
$findings | Sort-Object File, Line | Format-Table -AutoSize
Write-Host "Review each location manually, then move the value to user-secrets or an environment variable." -ForegroundColor Yellow
exit 1
