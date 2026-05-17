# clean.ps1 — Local workspace cleanup
#
# Removes generated artifacts from a Deckle worktree without touching
# anything tracked by git. All targets are `.gitignore`d and rebuilt by
# the next build — safe to delete and re-create at will.
#
# Targets are opt-in via switches so nothing happens by accident. Today
# only -BinObj is supported (per-module `bin/` and `obj/` under `src/`);
# future categories (asset caches, local logs, …) get their own switch.
# Run with no switch to see the help text.
#
# Symlink guard: skips any reparse-point folder rather than recursing
# into it. PowerShell's `Remove-Item -Recurse` follows symlinks and
# nukes the target — a guard is non-negotiable when scanning blindly.

[CmdletBinding()]
param(
    # Override the target worktree. Defaults to the repo that contains
    # this script copy (so VS Code "Run" on the open file picks the
    # currently-edited worktree).
    [string]$Target,

    # Interactive worktree picker via scripts/_menu.psm1. Overrides
    # -Target. Useful when cleaning from a terminal with several
    # worktrees checked out.
    [switch]$Pick,

    # Remove every `bin/` and `obj/` directory found at the root of
    # each module under <Target>/src/. Currently the only cleanup
    # category — kept as a switch so adding more later (caches, logs)
    # doesn't break this script's contract.
    [switch]$BinObj
)

$ErrorActionPreference = 'Stop'
$ScriptDir = $PSScriptRoot

# =============================================================================
# RepoRoot resolution — mirrors build-run.ps1 so the two scripts behave
# the same way when called from the same context (VS Code Run, terminal,
# launcher.ps1 with -Target).
# =============================================================================
if ($Pick) {
    Import-Module (Join-Path $ScriptDir '_menu.psm1') -Force
    $RepoRoot = Select-Worktree -ContextDir $ScriptDir
} elseif ($Target) {
    if (-not (Test-Path $Target)) { throw "Target not found: $Target" }
    $RepoRoot = (Get-Item $Target).FullName
} else {
    $RepoRoot = Split-Path $ScriptDir
}

Write-Host "Repo: $RepoRoot" -ForegroundColor DarkGray

# Help when called without any cleanup switch — surface what's available
# and exit clean rather than silently doing nothing.
if (-not $BinObj) {
    Write-Host ""
    Write-Host "Nothing to do — no cleanup switch was passed." -ForegroundColor Yellow
    Write-Host "Available cleanup categories:" -ForegroundColor DarkGray
    Write-Host "  -BinObj   Remove bin/ and obj/ from every module under src/" -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "Examples:" -ForegroundColor DarkGray
    Write-Host "  scripts\clean.ps1 -BinObj"             -ForegroundColor DarkGray
    Write-Host "  scripts\clean.ps1 -Pick -BinObj"       -ForegroundColor DarkGray
    return
}

# =============================================================================
# -BinObj — purge per-module bin/ + obj/ at the root of every src/* folder.
# =============================================================================
$SrcDir = Join-Path $RepoRoot 'src'
if (-not (Test-Path $SrcDir)) { throw "src/ not found under $RepoRoot" }

# Compute folder size before deletion — gives a meaningful end-of-run
# tally. Get-ChildItem -Force picks up hidden/system files (e.g. NuGet
# .lock); -ErrorAction SilentlyContinue swallows transient access denials
# on Windows-locked temp files inside obj/.
function Get-FolderSizeBytes {
    param([string]$Path)
    $sum = Get-ChildItem -LiteralPath $Path -Recurse -File -Force -ErrorAction SilentlyContinue |
           Measure-Object -Property Length -Sum
    if ($sum.Sum) { [int64]$sum.Sum } else { [int64]0 }
}

function Format-Size {
    param([int64]$Bytes)
    if     ($Bytes -ge 1GB) { '{0:N1} GB' -f ($Bytes / 1GB) }
    elseif ($Bytes -ge 1MB) { '{0:N1} MB' -f ($Bytes / 1MB) }
    elseif ($Bytes -ge 1KB) { '{0:N1} KB' -f ($Bytes / 1KB) }
    else                    { "$Bytes B" }
}

$modules    = Get-ChildItem -LiteralPath $SrcDir -Directory
$removed    = 0
$skipped    = 0
$totalBytes = [int64]0

Write-Host ""
Write-Host "Cleaning bin/ and obj/ under src/ ..." -ForegroundColor Cyan

foreach ($module in $modules) {
    foreach ($name in @('bin', 'obj')) {
        $dir = Join-Path $module.FullName $name
        if (-not (Test-Path -LiteralPath $dir)) { continue }

        # Symlink / junction guard — don't recurse into a reparse point;
        # PowerShell's Remove-Item would shoot the target on the other
        # side. Skip with a visible warning so the user can investigate.
        $item = Get-Item -LiteralPath $dir -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            Write-Host ("  ! skipped (reparse point): {0}\{1}" -f $module.Name, $name) -ForegroundColor Yellow
            $skipped++
            continue
        }

        $bytes       = Get-FolderSizeBytes -Path $dir
        $totalBytes += $bytes

        Remove-Item -LiteralPath $dir -Recurse -Force
        $removed++
        Write-Host ("  - {0}\{1,-3}  ({2})" -f $module.Name, $name, (Format-Size $bytes)) -ForegroundColor DarkGray
    }
}

Write-Host ""
Write-Host ("Done. {0} folders removed, {1} freed." -f $removed, (Format-Size $totalBytes)) -ForegroundColor Green
if ($skipped -gt 0) {
    Write-Host ("Skipped {0} reparse-point folder(s) — inspect manually." -f $skipped) -ForegroundColor Yellow
}
