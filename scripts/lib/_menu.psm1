# _menu.psm1 — Interactive arrow-key menu utilities
#
# Single source of truth for the cursor-driven picker pattern used by the
# scripts in this folder. Imported by deckle.ps1 (top-level launcher),
# build-run.ps1 (-Pick), clean.ps1 (-Pick), and any future script that
# needs the same flow.
#
# Two public functions:
#   - Select-Worktree : picks a worktree from `git worktree list`. Returns
#                       the chosen path. Skips and returns automatically
#                       when there is only one worktree.
#   - Select-Action   : picks one entry from a list of (Label, Value)
#                       pairs. Returns the chosen Value (or $null on Esc).
#                       Items with IsHeader=$true are rendered as
#                       non-selectable section dividers (Up/Down skips
#                       them); useful for grouping a flat list visually.
#
# Both honour the same key bindings: Up/Down to navigate, Enter to confirm,
# Esc to cancel. The line truncation logic preserves the meaningful tail
# of long paths and labels so the rendered menu always fits the terminal
# width (a wrapped line throws off the cursor math used to repaint the
# previous selection).

Set-StrictMode -Version Latest

# Internal: does this pscustomobject have an IsHeader property set to true?
# Wrapped in a helper because Set-StrictMode trips on direct prop access
# when the prop is absent.
function Test-MenuHeader {
    param($Item)
    $prop = $Item.PSObject.Properties['IsHeader']
    return ($null -ne $prop) -and [bool]$prop.Value
}

# Internal: render a single label line, padded to the terminal width so
# a previous longer line gets fully overwritten. Headers render in
# DarkCyan without a cursor prefix; selectable items use '  > ' / '    '.
function Write-MenuLine {
    param(
        [int]$Row,
        [string]$Label,
        [bool]$Selected,
        [bool]$IsHeader
    )

    if ($IsHeader) {
        $line = '  ' + $Label
    } else {
        $prefix = if ($Selected) { '  > ' } else { '    ' }
        $line   = $prefix + $Label
    }
    $pad = [Console]::WindowWidth - $line.Length - 1
    if ($pad -gt 0) { $line += (' ' * $pad) }

    [Console]::SetCursorPosition(0, $Row)
    if ($IsHeader) {
        Write-Host $line -ForegroundColor DarkCyan -NoNewline
    } elseif ($Selected) {
        Write-Host $line -ForegroundColor Green -NoNewline
    } else {
        Write-Host $line -NoNewline
    }
}

# Internal: drives the arrow-key loop over an array of items. Each item
# is a hashtable @{ Label = '...'; IsHeader = $false/$true }. Returns the
# selected index, or -1 if the user cancelled with Esc. Up/Down skip
# header rows automatically.
function Invoke-MenuLoop {
    param(
        [string]$Header,
        [object[]]$Items,
        [int]$Default = 0
    )

    if ($Items.Count -eq 0) { return -1 }

    # Indices of selectable (non-header) items. If empty, nothing to pick.
    $selectableIdx = @()
    for ($i = 0; $i -lt $Items.Count; $i++) {
        if (-not $Items[$i].IsHeader) { $selectableIdx += $i }
    }
    if ($selectableIdx.Count -eq 0) { return -1 }

    # Clamp Default to a valid selectable index.
    $selected = if ($selectableIdx -contains $Default) { $Default } else { $selectableIdx[0] }

    Write-Host ""
    Write-Host $Header -ForegroundColor Cyan

    # Render every label once so the buffer grows naturally; capturing the
    # final cursor position after the fact is more reliable than reserving
    # rows up front (which breaks when the buffer scrolls near the bottom).
    for ($i = 0; $i -lt $Items.Count; $i++) {
        $it = $Items[$i]
        if ($it.IsHeader) {
            Write-Host ('  ' + $it.Label) -ForegroundColor DarkCyan
        } else {
            $prefix = if ($i -eq $selected) { '  > ' } else { '    ' }
            if ($i -eq $selected) {
                Write-Host ($prefix + $it.Label) -ForegroundColor Green
            } else {
                Write-Host ($prefix + $it.Label)
            }
        }
    }
    $bottom = [Console]::CursorTop
    $top    = [Math]::Max(0, $bottom - $Items.Count)

    [Console]::CursorVisible = $false
    try {
        while ($true) {
            $key  = [Console]::ReadKey($true)
            $prev = $selected
            switch ($key.Key) {
                'UpArrow' {
                    # Step up through selectables only.
                    $pos = $selectableIdx.IndexOf($selected)
                    if ($pos -gt 0) { $selected = $selectableIdx[$pos - 1] }
                }
                'DownArrow' {
                    $pos = $selectableIdx.IndexOf($selected)
                    if ($pos -lt $selectableIdx.Count - 1) { $selected = $selectableIdx[$pos + 1] }
                }
                'Enter'  {
                    [Console]::SetCursorPosition(0, $bottom)
                    return $selected
                }
                'Escape' {
                    [Console]::SetCursorPosition(0, $bottom)
                    return -1
                }
            }
            if ($selected -eq $prev) { continue }
            Write-MenuLine -Row ($top + $prev)     -Label $Items[$prev].Label     -Selected $false -IsHeader $false
            Write-MenuLine -Row ($top + $selected) -Label $Items[$selected].Label -Selected $true  -IsHeader $false
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

    $items = foreach ($e in $entries) {
        [pscustomobject]@{
            Label    = (Format-WorktreeLabel -Branch $e.Branch -Path $e.Path)
            IsHeader = $false
        }
    }
    $idx = Invoke-MenuLoop -Header 'Pick a worktree (Up/Down, Enter = confirm, Esc = cancel):' -Items $items
    if ($idx -lt 0) { throw "Cancelled" }
    return $entries[$idx].Path
}

# Public: list a set of action labels and return the selected value.
# Items must be an array of [pscustomobject] with at least Label and
# Value (Value can be any type — string, hashtable, scriptblock).
# Items with IsHeader=$true render as non-selectable section dividers
# (Up/Down skips them). Throws "Cancelled" on Esc.
function Select-Action {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]                 [string]$Header,
        [Parameter(Mandatory)][AllowEmptyCollection()] $Items,
        [int]$Default = 0
    )

    if ($Items.Count -eq 0) { throw "No items to select from" }

    # Normalise: every item ends up with Label + IsHeader. Missing
    # IsHeader defaults to false (regular selectable entry).
    $normalised = foreach ($it in $Items) {
        [pscustomobject]@{
            Label    = [string]$it.Label
            IsHeader = (Test-MenuHeader $it)
        }
    }

    $idx = Invoke-MenuLoop -Header $Header -Items $normalised -Default $Default
    if ($idx -lt 0) { throw "Cancelled" }
    return $Items[$idx].Value
}

Export-ModuleMember -Function Select-Worktree, Select-Action
