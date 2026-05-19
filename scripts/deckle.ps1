# deckle.ps1 — Single interactive entry point for Deckle dev workflows.
#
# Run this with F5 in VSCodium (see .vscode/launch.json) or directly from
# a PowerShell 7+ terminal. The menu groups actions by purpose:
#
#   Build             — daily compile + run loop, per-worktree.
#   Worktree maint    — clean artefacts, gather stats, per-worktree.
#   Setup             — provision UserDataRoot or bootstrap a fresh dev
#                       machine (global, no worktree picker).
#   Maintainer        — publish the native runtime to GitHub Release.
#
# Per-worktree actions prompt for a worktree after the action is picked
# (worktree auto-resolves when only the main repo exists). Global actions
# go straight to a short parameter prompt (or run on defaults). Every
# concrete action delegates to a single-purpose script in scripts/lib/;
# those scripts remain usable on their own CLI for automation.

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ScriptDir = $PSScriptRoot
$LibDir    = Join-Path $ScriptDir 'lib'

Import-Module (Join-Path $LibDir '_menu.psm1') -Force

# Helper used by every per-worktree branch: pick a worktree and bail
# gracefully on Esc (the menu module throws "Cancelled" in that case).
function Get-WorktreeOrReturn {
    try {
        $wt = Select-Worktree -ContextDir $ScriptDir
        Write-Host "Worktree: $wt" -ForegroundColor DarkGray
        return $wt
    } catch {
        Write-Host "Cancelled." -ForegroundColor Yellow
        return $null
    }
}

# Helper for short y/n prompts in global-action sub-flows. Returns
# $true / $false; default applies on bare Enter.
function Read-YesNo {
    param(
        [Parameter(Mandatory)][string]$Question,
        [bool]$Default = $false
    )
    $hint  = if ($Default) { '[Y/n]' } else { '[y/N]' }
    $ans   = Read-Host "$Question $hint"
    if ([string]::IsNullOrWhiteSpace($ans)) { return $Default }
    return ($ans -match '^(y|yes|o|oui)$')
}

# Build the top-level action list. Headers (IsHeader=$true) render as
# section dividers — Up/Down skips them automatically.
$actions = @(
    [pscustomobject]@{ Label = '── Build ──';                       Value = $null;            IsHeader = $true  }
    [pscustomobject]@{ Label = 'Build & run (Debug)';               Value = 'build-debug'                       }
    [pscustomobject]@{ Label = 'Build & run (Release)';             Value = 'build-release'                     }
    [pscustomobject]@{ Label = 'Build only (no run)';               Value = 'build-norun'                       }

    [pscustomobject]@{ Label = '── Worktree maintenance ──';        Value = $null;            IsHeader = $true  }
    [pscustomobject]@{ Label = 'Clean bin/obj';                     Value = 'clean'                             }
    [pscustomobject]@{ Label = 'Stats (LOC, files per module)';     Value = 'stats'                             }

    [pscustomobject]@{ Label = '── Setup ──';                       Value = $null;            IsHeader = $true  }
    [pscustomobject]@{ Label = 'Setup assets (UserDataRoot)';       Value = 'setup-assets'                      }
    [pscustomobject]@{ Label = 'Bootstrap dev environment';         Value = 'bootstrap-dev'                     }

    [pscustomobject]@{ Label = '── Maintainer ──';                  Value = $null;            IsHeader = $true  }
    [pscustomobject]@{ Label = 'Publish native runtime';            Value = 'publish-native'                    }

    [pscustomobject]@{ Label = '';                                  Value = $null;            IsHeader = $true  }
    [pscustomobject]@{ Label = 'Quit';                              Value = 'quit'                              }
)

try {
    $action = Select-Action -Header 'Pick an action (Up/Down, Enter = confirm, Esc = cancel):' -Items $actions
} catch {
    Write-Host "Cancelled." -ForegroundColor Yellow
    return
}

switch ($action) {

    # ----- Build branches — per-worktree ---------------------------------
    'build-debug' {
        $wt = Get-WorktreeOrReturn
        if ($null -eq $wt) { return }
        & (Join-Path $LibDir 'build-run.ps1') -Target $wt -Configuration Debug
    }
    'build-release' {
        $wt = Get-WorktreeOrReturn
        if ($null -eq $wt) { return }
        & (Join-Path $LibDir 'build-run.ps1') -Target $wt -Configuration Release
    }
    'build-norun' {
        $wt = Get-WorktreeOrReturn
        if ($null -eq $wt) { return }
        & (Join-Path $LibDir 'build-run.ps1') -Target $wt -Configuration Release -NoRun
    }

    # ----- Worktree maintenance ------------------------------------------
    'clean' {
        $wt = Get-WorktreeOrReturn
        if ($null -eq $wt) { return }
        & (Join-Path $LibDir 'clean.ps1') -Target $wt
    }
    'stats' {
        $wt = Get-WorktreeOrReturn
        if ($null -eq $wt) { return }
        $detailed = Read-YesNo -Question 'Detailed breakdown by sub-namespace?' -Default $false
        if ($detailed) {
            & (Join-Path $LibDir 'stats.ps1') -Target $wt -Detailed
        } else {
            & (Join-Path $LibDir 'stats.ps1') -Target $wt
        }
    }

    # ----- Setup — global (no worktree picker) ---------------------------
    'setup-assets' {
        # Three source modes for the native runtime — mirrors the script's
        # own ParameterSets. The user picks one; models always download.
        $modeItems = @(
            [pscustomobject]@{ Label = 'From GitHub Release (recommended, no whisper.cpp clone needed)'; Value = 'release'  }
            [pscustomobject]@{ Label = 'From local whisper.cpp build tree';                              Value = 'repo'     }
            [pscustomobject]@{ Label = 'Models only (skip native runtime)';                              Value = 'models'   }
        )
        try {
            $mode = Select-Action -Header 'Setup assets — native runtime source:' -Items $modeItems
        } catch {
            Write-Host "Cancelled." -ForegroundColor Yellow
            return
        }
        $assetsScript = Join-Path $LibDir 'setup-assets.ps1'
        switch ($mode) {
            'release' {
                $version = Read-Host 'Bundle version (X.Y.Z, e.g. 1.0.0)'
                if ([string]::IsNullOrWhiteSpace($version)) { Write-Host 'No version, aborting.' -ForegroundColor Yellow; return }
                & $assetsScript -FromRelease $version
            }
            'repo' {
                $repo = Read-Host 'Path to whisper.cpp clone (leave empty to use $env:DECKLE_WHISPER_REPO or sibling clone)'
                if ([string]::IsNullOrWhiteSpace($repo)) {
                    & $assetsScript
                } else {
                    & $assetsScript -WhisperRepo $repo
                }
            }
            'models' {
                & $assetsScript
            }
        }
    }
    'bootstrap-dev' {
        $dryRun = Read-YesNo -Question 'Dry-run first (probe + plan, no install)?' -Default $true
        $full   = Read-YesNo -Question 'Include Tier 2 (native recompile toolchain + Ollama)?' -Default $false
        $args   = @()
        if ($dryRun) { $args += '-DryRun' }
        if ($full)   { $args += '-Full' }
        & (Join-Path $LibDir 'bootstrap-dev-env.ps1') @args
    }

    # ----- Maintainer ----------------------------------------------------
    'publish-native' {
        $version = Read-Host 'Bundle version to publish (X.Y.Z)'
        if ([string]::IsNullOrWhiteSpace($version)) { Write-Host 'No version, aborting.' -ForegroundColor Yellow; return }
        $repo = Read-Host 'Path to whisper.cpp clone (with build\bin\ output)'
        if ([string]::IsNullOrWhiteSpace($repo)) { Write-Host 'No whisper.cpp path, aborting.' -ForegroundColor Yellow; return }
        $publish = Read-YesNo -Question 'Push to GitHub Release (uses gh CLI)?' -Default $false
        $args = @('-Version', $version, '-WhisperRepo', $repo)
        if ($publish) { $args += '-Publish' }
        & (Join-Path $LibDir 'publish-native-runtime.ps1') @args
    }

    'quit' {
        Write-Host "Bye." -ForegroundColor DarkGray
    }
}
