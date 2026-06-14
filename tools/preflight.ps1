<#
.SYNOPSIS
    Release-surface sync check for Faust, Lord of Investigation.

.DESCRIPTION
    Run from the repo root before any chore(release) commit. Verifies:
      1. Faust.csproj <Version> == thunderstore.toml versionNumber
      2. Root CHANGELOG.md (full/GitHub) has an entry for that version
      3. Package CHANGELOG.md (concise/Thunderstore) has an entry for that version
      4. thunderstore.toml description is within Thunderstore's 250-char limit
      5. Working tree status (informational)

    Exits 1 if any hard check fails, 0 otherwise.

.EXAMPLE
    pwsh tools/preflight.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$failures = @()

$csprojPath        = Join-Path $repoRoot 'Faust\Faust\Faust.csproj'
$tomlPath          = Join-Path $repoRoot 'Faust\Faust\thunderstore.toml'
$rootChangelogPath = Join-Path $repoRoot 'CHANGELOG.md'
$pkgChangelogPath  = Join-Path $repoRoot 'Faust\Faust\CHANGELOG.md'

# --- 1. Version parity ---
$csprojVersion = ([xml](Get-Content $csprojPath -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$tomlRaw = Get-Content $tomlPath -Raw
$tomlVersion = if ($tomlRaw -match 'versionNumber\s*=\s*"([^"]+)"') { $Matches[1] } else { $null }

if (-not $csprojVersion) { $failures += "Could not read <Version> from Faust.csproj" }
if (-not $tomlVersion)   { $failures += "Could not read versionNumber from thunderstore.toml" }
if ($csprojVersion -and $tomlVersion -and $csprojVersion -ne $tomlVersion) {
    $failures += "VERSION MISMATCH: csproj=$csprojVersion vs thunderstore.toml=$tomlVersion"
}
Write-Host "Version: csproj=$csprojVersion  toml=$tomlVersion"

# --- 2+3. Changelog entries for the current version ---
$version = $csprojVersion
if ($version) {
    $escaped = [regex]::Escape($version)
    if ((Get-Content $rootChangelogPath -Raw) -notmatch "##\s*\[?$escaped\]?") {
        $failures += "Root CHANGELOG.md (full/GitHub) has NO entry for $version"
    } else { Write-Host "Root CHANGELOG.md: entry for $version found" }

    if ((Get-Content $pkgChangelogPath -Raw) -notmatch "##\s*\[?$escaped\]?") {
        $failures += "Package CHANGELOG.md (Thunderstore) has NO entry for $version"
    } else { Write-Host "Package CHANGELOG.md: entry for $version found" }
}

# --- 4. Thunderstore description length (hard limit 250) ---
if ($tomlRaw -match 'description\s*=\s*"([^"]*)"') {
    $len = $Matches[1].Length
    if ($len -gt 250) { $failures += "thunderstore.toml description is $len chars (limit 250)" }
    else { Write-Host "Thunderstore description: $len/250 chars" }
}

# --- 4b. Doc hygiene (WARNINGS only — see docs/DOC_STYLE.md) ---
# Catches the drift we cleaned up in 0.16.1: cross-cutting facts restated per entry/section,
# per-entry boilerplate footers, and an over-stuffed Thunderstore page. Never blocks a release.
$warnings = @()
$pkgReadmePath = Join-Path $repoRoot 'Faust\Faust\README.md'
function Get-MatchCount([string]$file, [string]$pattern) {
    if (-not (Test-Path $file)) { return 0 }
    return ([regex]::Matches((Get-Content $file -Raw), $pattern, 'IgnoreCase')).Count
}
$boiler = @(
    @{ label = 'package CHANGELOG';   file = $pkgChangelogPath; pattern = 'pre-1\.0 testing release'; max = 1 },
    @{ label = 'package CHANGELOG';   file = $pkgChangelogPath; pattern = "haven't been validated";    max = 0 },
    @{ label = 'Thunderstore README'; file = $pkgReadmePath;    pattern = 'pre-1\.0';                  max = 2 },
    @{ label = 'Thunderstore README'; file = $pkgReadmePath;    pattern = 'Shadow Realm Discord';      max = 2 }
)
foreach ($b in $boiler) {
    $n = Get-MatchCount $b.file $b.pattern
    if ($n -gt $b.max) {
        $warnings += "$($b.label): '$($b.pattern)' appears $n times (keep <= $($b.max) — say cross-cutting facts once)"
    }
}
if (Test-Path $pkgReadmePath) {
    # Budget PROSE lines only — tables, screenshots, and headings are legitimate and not counted,
    # so the check targets actual over-explaining rather than a table-heavy (but clean) page.
    $proseLines = (Get-Content $pkgReadmePath | Where-Object {
        $t = $_.Trim()
        $t -ne '' -and $t -notmatch '^\|' -and $t -notmatch '^!\[' -and $t -notmatch '^#' -and $t -notmatch '^---' -and $t -notmatch '^>'
    }).Count
    if ($proseLines -gt 120) {
        $warnings += "Thunderstore README has $proseLines prose lines (soft budget ~120 — tables/screenshots excluded; trim the explaining)"
    }
}

# --- 5. Working tree (informational) ---
try {
    $dirty = git -C $repoRoot status --porcelain
    if ($dirty) { Write-Host "NOTE: working tree has uncommitted changes:`n$dirty" }
    else { Write-Host "Working tree clean." }
} catch { Write-Host "NOTE: git status unavailable." }

# --- Doc-hygiene warnings (non-blocking) ---
if ($warnings.Count -gt 0) {
    Write-Host ""
    Write-Host "DOC-HYGIENE WARNINGS (non-blocking — see docs/DOC_STYLE.md):" -ForegroundColor Yellow
    $warnings | ForEach-Object { Write-Host "  ! $_" -ForegroundColor Yellow }
}

# --- Result ---
if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "PREFLIGHT FAILED:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host ""
    Write-Host "Reminder: READMEs (root + package) need a manual staleness pass too."
    exit 1
}

Write-Host ""
Write-Host "PREFLIGHT OK. Manual reminder: scan both READMEs (root=GitHub, package=Thunderstore) for staleness, confirm the BCH contract doc matches any wire/command change, and keep all docs to docs/DOC_STYLE.md (say cross-cutting facts once; no per-entry boilerplate)." -ForegroundColor Green
exit 0
