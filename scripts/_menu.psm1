# _menu.psm1 — Interactive arrow-key menu utilities
#
# Single source of truth for the cursor-driven picker pattern used by the
# scripts in this folder. Imported by build-run.ps1 (-Pick), launcher.ps1,
# and any future script that needs the same flow.
#
# Two public functions:
#   - Select-Worktree : picks a worktree from `git worktree list`. Returns
#                       the chosen path. Skips and returns automatically
#                       when there is only one worktree.
#   - Select-Action   : picks one entry from a list of (Label, Value)
#                       pairs. Returns the chosen Value (or $null on Esc).
#
# Both honour the same key bindings: Up/Down to navigate, Enter to confirm,
# Esc to cancel. The line truncation logic preserves the meaningful tail
# of long paths and labels so the rendered menu always fits the terminal
# width (a wrapped line throws off the cursor math used to repaint the
# previous selection).

Set-StrictMode -Version Latest

# Internal: render a single highlighted/normal label line, padded to the
# terminal width so a previous longer line gets fully overwritten.
function Write-MenuLine {
    param(
        [int]$Row,
        [string]$Label,
        [bool]$Selected
    )

    $prefix = if ($Selected) { '  > ' } else { '    ' }
    $line   = $prefix + $Label
    $pad    = [Console]::WindowWidth - $line.Length - 1
    if ($pad -gt 0) { $line += (' ' * $pad) }

    [Console]::SetCursorPosition(0, $Row)
    if ($Selected) {
        Write-Host $line -ForegroundColor Green -NoNewline
    } else {
        Write-Host $line -NoNewline
    }
}

# Internal: drives the arrow-key loop over an array of pre-formatted labels.
# Returns the selected index, or -1 if the user cancelled with Esc.
function Invoke-MenuLoop {
    param(
        [string]$Header,
        [string[]]$Labels,
        [int]$Default = 0
    )

    if ($Labels.Count -eq 0) { return -1 }
    $selected = [Math]::Min([Math]::Max(0, $Default), $Labels.Count - 1)

    Write-Host ""
    Write-Host $Header -ForegroundColor Cyan

    # Render every label once so the buffer grows naturally; capturing the
    # final cursor position after the fact is more reliable than reserving
    # rows up front (which breaks when the buffer scrolls near the bottom).
    for ($i = 0; $i -lt $Labels.Count; $i++) {
        $prefix = if ($i -eq $selected) { '  > ' } else { '    ' }
        if ($i -eq $selected) {
            Write-Host ($prefix + $Labels[$i]) -ForegroundColor Green
        } else {
            Write-Host ($prefix + $Labels[$i])
        }
    }
    $bottom = [Console]::CursorTop
    $top    = [Math]::Max(0, $bottom - $Labels.Count)

    [Console]::CursorVisible = $false
    try {
        while ($true) {
            $key  = [Console]::ReadKey($true)
            $prev = $selected
            switch ($key.Key) {
                'UpArrow'   { if ($selected -gt 0)                  { $selected-- } }
                'DownArrow' { if ($selected -lt $Labels.Count - 1)  { $selected++ } }
                'Enter'     {
                    [Console]::SetCursorPosition(0, $bottom)
                    return $selected
                }
                'Escape'    {
                    [Console]::SetCursorPosition(0, $bottom)
                    return -1
                }
            }
            if ($selected -eq $prev) { continue }
            Write-MenuLine -Row ($top + $prev)     -Label $Labels[$prev]     -Selected $false
            Write-MenuLine -Row ($top + $selected) -Label $Labels[$selected] -Selected $true
        }
    } finally {
        [Console]::CursorVisible = $true
    }
}

# Internal: truncate a "[branch] path" line so it fits in the terminal
# width. Keeps the branch label intact and elides the path's prefix so the
# distinctive tail (worktree folder name) stays legible.
function Format-WorktreeLabel {
    param([string]$Branch, [string]$Path)

    $maxLineLen = [Console]::WindowWidth - 5  # "  > " prefix + trailing gap
    $branchLbl  = "{0,-28}" -f "[$Branch]"
    $budget     = $maxLineLen - $branchLbl.Length - 1
    if ($budget -lt 4) {
        $Path = [char]0x2026
    } elseif ($Path.Length -gt $budget) {
        $Path = ([char]0x2026) + $Path.Substring($Path.Length - ($budget - 1))
    }
    "$branchLbl $Path"
}

# Public: list worktrees and let the user pick one. ContextDir must be a
# path inside any worktree of the target repository (used to scope the
# `git worktree list` call). Returns the absolute path of the picked
# worktree, or throws "Cancelled" if the user pressed Esc.
function Select-Worktree {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ContextDir)

    Push-Location $ContextDir
    try {
        $raw = git worktree list --porcelain 2>$null
    } finally {
        Pop-Location
    }
    if (-not $raw) { throw "git worktree list failed - not a git repo?" }

    # Parse porcelain output into (path, branch) tuples.
    $entries   = @()
    $curPath   = $null
    $curBranch = $null
    foreach ($line in $raw) {
        if ($line -like 'worktree *') {
            if ($curPath) {
                $entries += [pscustomobject]@{ Path = $curPath; Branch = ($curBranch ?? '(detached)') }
            }
            $curPath   = $line.Substring(9)
            $curBranch = $null
        } elseif ($line -like 'branch *') {
            $curBranch = ($line.Substring(7)) -replace '^refs/heads/', ''
        }
    }
    if ($curPath) {
        $entries += [pscustomobject]@{ Path = $curPath; Branch = ($curBranch ?? '(detached)') }
    }
    if ($entries.Count -eq 0) { throw "No worktrees found" }

    # Auto-pick when there is nothing to choose between.
    if ($entries.Count -eq 1) { return $entries[0].Path }

    $labels = foreach ($e in $entries) { Format-WorktreeLabel -Branch $e.Branch -Path $e.Path }
    $idx    = Invoke-MenuLoop -Header 'Pick a worktree (Up/Down, Enter = confirm, Esc = cancel):' -Labels $labels
    if ($idx -lt 0) { throw "Cancelled" }
    return $entries[$idx].Path
}

# Public: list a set of action labels and return the selected value.
# Items must be an array of [pscustomobject]@{ Label = '...'; Value = ... }
# (Value can be any type — string, hashtable, scriptblock). Throws
# "Cancelled" on Esc.
function Select-Action {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]                 [string]$Header,
        [Parameter(Mandatory)][AllowEmptyCollection()] $Items,
        [int]$Default = 0
    )

    if ($Items.Count -eq 0) { throw "No items to select from" }

    $labels = foreach ($it in $Items) { [string]$it.Label }
    $idx    = Invoke-MenuLoop -Header $Header -Labels $labels -Default $Default
    if ($idx -lt 0) { throw "Cancelled" }
    return $Items[$idx].Value
}

Export-ModuleMember -Function Select-Worktree, Select-Action
