# launcher.ps1 — Interactive entry point for daily Deckle development
#
# Two-step menu (Clean opens a third step):
#   1. Pick a worktree (skipped when only the main repo exists).
#   2. Pick an action  : Build & run (Debug/Release), Build only,
#                        Setup assets, Clean…, or Quit.
#   3. (Clean only) Pick what to clean — currently bin/ + obj/.
#
# Delegates to scripts/build-run.ps1, scripts/setup-assets.ps1, or
# scripts/clean.ps1 depending on the choice, always with -Target so the
# worker doesn't re-prompt. No CLI arguments — everything is interactive.
# Keep the workers usable directly from CLI / launch.json profiles; this
# script is purely additive.

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ScriptDir = $PSScriptRoot

# Load the shared menu module. Force re-import so script edits during a
# session are picked up without restarting PowerShell.
Import-Module (Join-Path $ScriptDir '_menu.psm1') -Force

# Step 1 — Pick worktree (auto-resolves when there is only one).
try {
    $worktree = Select-Worktree -ContextDir $ScriptDir
} catch {
    Write-Host "Cancelled." -ForegroundColor Yellow
    return
}
Write-Host "Worktree: $worktree" -ForegroundColor DarkGray

# Step 2 — Pick action.
$actions = @(
    [pscustomobject]@{ Label = 'Build & run (Debug)';         Value = 'build-debug'   }
    [pscustomobject]@{ Label = 'Build & run (Release)';       Value = 'build-release' }
    [pscustomobject]@{ Label = 'Build only (no run)';         Value = 'build-norun'   }
    [pscustomobject]@{ Label = 'Setup assets (UserDataRoot)'; Value = 'setup-assets'  }
    [pscustomobject]@{ Label = 'Clean…';                       Value = 'clean'         }
    [pscustomobject]@{ Label = 'Quit';                         Value = 'quit'          }
)
try {
    $action = Select-Action -Header 'Pick an action (Up/Down, Enter = confirm, Esc = cancel):' -Items $actions
} catch {
    Write-Host "Cancelled." -ForegroundColor Yellow
    return
}

switch ($action) {
    'build-debug' {
        & (Join-Path $ScriptDir 'build-run.ps1') -Target $worktree -Configuration Debug
    }
    'build-release' {
        & (Join-Path $ScriptDir 'build-run.ps1') -Target $worktree -Configuration Release
    }
    'build-norun' {
        & (Join-Path $ScriptDir 'build-run.ps1') -Target $worktree -Configuration Release -NoRun
    }
    'setup-assets' {
        & (Join-Path $ScriptDir 'setup-assets.ps1')
    }
    'clean' {
        # Step 3 — Pick what to clean. Kept as a sub-menu so future
        # cleanup categories slot in alongside bin/obj without bloating
        # the top-level action list.
        $cleanItems = @(
            [pscustomobject]@{ Label = 'Build artifacts (bin/ + obj/ in src/*)'; Value = 'bin-obj' }
            [pscustomobject]@{ Label = 'Back';                                    Value = 'back'    }
        )
        try {
            $cleanChoice = Select-Action -Header 'What to clean? (Up/Down, Enter = confirm, Esc = back):' -Items $cleanItems
        } catch {
            # Esc — treat as "back" without ceremony.
            $cleanChoice = 'back'
        }
        switch ($cleanChoice) {
            'bin-obj' {
                & (Join-Path $ScriptDir 'clean.ps1') -Target $worktree -BinObj
            }
            'back' {
                Write-Host "Back to top." -ForegroundColor DarkGray
            }
        }
    }
    'quit' {
        Write-Host "Bye." -ForegroundColor DarkGray
    }
}
