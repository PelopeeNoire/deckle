<#
.SYNOPSIS
    Interactive launcher for the WhispUI benchmark suite.

.DESCRIPTION
    Auto-discovers Python benchmarks in this folder (any *_bench.py plus
    the legacy benchmark.py / autoresearch.py aliases). Presents them in
    an arrow-key navigable menu with live filtering. After picking one,
    asks for the optional flags relevant to that bench and runs it.

    No external module dependency — uses [Console]::ReadKey() like
    scripts/build-run.ps1's Select-WorktreeInteractive (kept lean on
    purpose). See benchmark/AGENT.md for the bench-side conventions.

.NOTES
    Up / Down  : move selection
    Enter      : run selected bench
    Escape / Q : quit (Esc clears active filter first if any)
    Letters    : type to filter the list (case-insensitive substring)
    Backspace  : delete last filter char
    R          : refresh menu (re-scan + re-read status)
#>

$ErrorActionPreference = 'Stop'
$BenchmarkDir = $PSScriptRoot

# ─── Discovery ──────────────────────────────────────────────────────────────

function Get-DocstringSummary {
    param([string]$Path)
    # Read the first triple-quoted block and return its first non-empty line.
    $text = Get-Content $Path -Raw -Encoding UTF8
    if ($text -match '(?s)("""|'''')(.*?)(?:\1)') {
        $body = $Matches[2].Trim()
        $first = ($body -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1)
        if ($first) { return $first.Trim() }
    }
    return '(no docstring)'
}

function Get-Benches {
    # All *_bench.py except templates (leading underscore) + the legacy
    # entrypoints. Sorted: legacy aliases first (most-used), then *_bench.
    $files = @()
    $files += Get-ChildItem -Path $BenchmarkDir -Filter '*_bench.py' -File |
              Where-Object { $_.Name -notmatch '^_' }
    foreach ($legacy in 'benchmark.py', 'autoresearch.py') {
        $p = Join-Path $BenchmarkDir $legacy
        if (Test-Path $p) { $files += Get-Item $p }
    }
    # Dedup by full path (in case a *_bench.py is also matched by name)
    $seen = @{}
    $entries = foreach ($f in $files) {
        if ($seen.ContainsKey($f.FullName)) { continue }
        $seen[$f.FullName] = $true
        [pscustomobject]@{
            Name        = $f.BaseName
            FileName    = $f.Name
            Path        = $f.FullName
            Description = Get-DocstringSummary $f.FullName
        }
    }
    # Stable order: legacy first (benchmark.py, autoresearch.py), then alpha
    $legacyOrder = @{ 'benchmark' = 0; 'autoresearch' = 1 }
    $entries | Sort-Object @(
        @{ Expression = { if ($legacyOrder.ContainsKey($_.Name)) { $legacyOrder[$_.Name] } else { 99 } } },
        @{ Expression = 'Name' }
    )
}

# ─── Status header ──────────────────────────────────────────────────────────

function Get-StatusLines {
    $lines = @()

    $promptFile = Join-Path $BenchmarkDir 'config/prompts/whisper_initial_prompt.txt'
    if (Test-Path $promptFile) {
        $p = (Get-Content $promptFile -Raw -Encoding UTF8).Trim()
        $words = if ($p) { ($p -split '\s+').Count } else { 0 }
        $lines += "  Whisper prompt : $($p.Length) chars, $words words"
    }

    try {
        $branch = git -C $BenchmarkDir branch --show-current 2>$null
        if ($branch) { $lines += "  Branch         : $branch" }
    } catch {
        # Not a git repo or git not on PATH — silently skip
    }

    $lastRun = Join-Path $BenchmarkDir 'reports/last_whisper_run.json'
    if (Test-Path $lastRun) {
        try {
            $data = Get-Content $lastRun -Raw -Encoding UTF8 | ConvertFrom-Json
            $lines += "  Last whisper   : $($data.timestamp) — $($data.samples) samples, $($data.errors) errors"
        } catch {
            # Don't crash if the file is malformed — just skip.
        }
    }

    $lines
}

function Show-Header {
    Clear-Host
    Write-Host ''
    Write-Host '  ============================================' -ForegroundColor Cyan
    Write-Host '   WhispUI benchmark launcher' -ForegroundColor Cyan
    Write-Host '  ============================================' -ForegroundColor Cyan
    Write-Host ''
    foreach ($l in Get-StatusLines) {
        Write-Host $l -ForegroundColor DarkGray
    }
    Write-Host ''
}

# ─── Interactive selector ───────────────────────────────────────────────────

function Format-Label {
    param($Entry, [int]$MaxLen)
    $name = '{0,-22}' -f $Entry.Name
    $desc = $Entry.Description
    $budget = $MaxLen - $name.Length - 3
    if ($budget -lt 8) {
        $desc = ''
    } elseif ($desc.Length -gt $budget) {
        $desc = $desc.Substring(0, $budget - 1) + [char]0x2026
    }
    if ($desc) { "$name   $desc" } else { $name }
}

function Select-BenchInteractive {
    param([object[]]$Entries)
    if ($Entries.Count -eq 0) {
        throw 'No benchmarks discovered (looked for *_bench.py / benchmark.py / autoresearch.py)'
    }

    $filter   = ''
    $selected = 0

    Write-Host '  Pick a benchmark (Up/Down, Enter = run, Esc = back, type to filter):' -ForegroundColor Cyan
    Write-Host ''

    # Reserve space for the menu, the filter line, and the hover detail.
    # We'll repaint these lines in place; capturing $top/$bottom anchors
    # the cursor math to absolute buffer positions.
    $maxLines = $Entries.Count + 3   # filter line + N entries + hover line + spacer
    for ($i = 0; $i -lt $maxLines; $i++) { Write-Host '' }
    $bottom = [Console]::CursorTop
    $top    = [Math]::Max(0, $bottom - $maxLines)

    $lastWidth  = [Console]::WindowWidth
    $lastHeight = [Console]::WindowHeight

    [Console]::CursorVisible = $false
    try {
        while ($true) {
            # Filter the list against the current filter string.
            $matches = if ($filter) {
                @($Entries | Where-Object { $_.Name -match [regex]::Escape($filter) -or $_.Description -match [regex]::Escape($filter) })
            } else {
                @($Entries)
            }
            if ($matches.Count -eq 0) {
                $matches = @($Entries)  # Fallback so we never lock the user out
                $filterStatus = "Filter: '$filter' (no match — showing all)"
                $filterColor  = 'DarkYellow'
            } elseif ($filter) {
                $filterStatus = "Filter: '$filter' ($($matches.Count) match$( if ($matches.Count -ne 1) { 'es' } ))"
                $filterColor  = 'Yellow'
            } else {
                $filterStatus = '(no filter — type to narrow)'
                $filterColor  = 'DarkGray'
            }
            if ($selected -ge $matches.Count) { $selected = $matches.Count - 1 }
            if ($selected -lt 0)              { $selected = 0 }

            # Repaint the entire reserved block. This is cheap (Entries.Count is
            # tiny) and avoids any ghost-text fragility around resize / filter.
            $width = [Console]::WindowWidth
            $maxLen = $width - 5  # "  > " prefix + trailing gap

            # Line 0: filter status
            [Console]::SetCursorPosition(0, $top)
            $padded = ('  ' + $filterStatus).PadRight($width - 1).Substring(0, [Math]::Min($width - 1, $width - 1))
            Write-Host $padded -ForegroundColor $filterColor -NoNewline

            # Lines 1..N: entries (or blank if filtered out)
            for ($i = 0; $i -lt $Entries.Count; $i++) {
                [Console]::SetCursorPosition(0, $top + 1 + $i)
                if ($i -lt $matches.Count) {
                    $entry  = $matches[$i]
                    $prefix = if ($i -eq $selected) { '  > ' } else { '    ' }
                    $line   = $prefix + (Format-Label -Entry $entry -MaxLen $maxLen)
                    $line   = $line.PadRight($width - 1).Substring(0, [Math]::Min($line.Length, $width - 1))
                    if ($i -eq $selected) {
                        Write-Host $line -ForegroundColor Green -NoNewline
                    } else {
                        Write-Host $line -NoNewline
                    }
                } else {
                    Write-Host (' ' * ($width - 1)) -NoNewline
                }
            }

            # Line N+1: hover detail (full description of selected)
            [Console]::SetCursorPosition(0, $top + 1 + $Entries.Count)
            $hover = if ($matches.Count -gt 0) {
                $sel = $matches[$selected]
                "    $($sel.FileName) — $($sel.Description)"
            } else { '' }
            if ($hover.Length -gt $width - 1) {
                $hover = $hover.Substring(0, $width - 2) + [char]0x2026
            }
            Write-Host $hover.PadRight($width - 1) -ForegroundColor DarkGray -NoNewline

            # Line N+2: bottom hint
            [Console]::SetCursorPosition(0, $top + 2 + $Entries.Count)
            Write-Host ''.PadRight($width - 1) -NoNewline

            # Read input
            $key = [Console]::ReadKey($true)

            # Resize check — if the window size changed, force a full redraw
            # next iteration by rewriting the header. We bail out of the
            # menu, the caller will re-enter on the next loop.
            if ([Console]::WindowWidth -ne $lastWidth -or [Console]::WindowHeight -ne $lastHeight) {
                [Console]::SetCursorPosition(0, $bottom)
                return [pscustomobject]@{ Action = 'Resize' }
            }

            switch ($key.Key) {
                'UpArrow'   { if ($selected -gt 0)                  { $selected-- } }
                'DownArrow' { if ($selected -lt $matches.Count - 1) { $selected++ } }
                'Enter' {
                    [Console]::SetCursorPosition(0, $bottom)
                    if ($matches.Count -gt 0) {
                        return [pscustomobject]@{ Action = 'Run'; Entry = $matches[$selected] }
                    }
                }
                'Escape' {
                    if ($filter) {
                        $filter = ''
                    } else {
                        [Console]::SetCursorPosition(0, $bottom)
                        return [pscustomobject]@{ Action = 'Quit' }
                    }
                }
                'Backspace' { if ($filter.Length -gt 0) { $filter = $filter.Substring(0, $filter.Length - 1) } }
                default {
                    $ch = $key.KeyChar
                    if ($ch -eq 'r' -or $ch -eq 'R') {
                        if (-not $filter) {
                            [Console]::SetCursorPosition(0, $bottom)
                            return [pscustomobject]@{ Action = 'Refresh' }
                        }
                    }
                    if ($ch -eq 'q' -or $ch -eq 'Q') {
                        if (-not $filter) {
                            [Console]::SetCursorPosition(0, $bottom)
                            return [pscustomobject]@{ Action = 'Quit' }
                        }
                    }
                    if ($ch -and [int]$ch -ge 32 -and [int]$ch -lt 127) {
                        $filter += [string]$ch
                        $selected = 0
                    }
                }
            }
        }
    } finally {
        [Console]::CursorVisible = $true
    }
}

# ─── Per-bench arg prompts ──────────────────────────────────────────────────

function Read-OptionalArg {
    param([string]$Prompt, [string]$Default = '')
    Write-Host -NoNewline "  $Prompt " -ForegroundColor Cyan
    if ($Default) {
        Write-Host -NoNewline "[$Default] " -ForegroundColor DarkGray
    }
    $reply = Read-Host
    if ([string]::IsNullOrWhiteSpace($reply)) { return $Default }
    return $reply.Trim()
}

function Get-BenchArgs {
    param($Entry)
    $extra = @()
    switch -Wildcard ($Entry.Name) {
        'whisper_bench' {
            $bracket = Read-OptionalArg -Prompt 'Bracket [relecture/lissage/affinage/arrangement, blank=all]:'
            if ($bracket) { $extra += @('--bracket', $bracket) }
            $limit = Read-OptionalArg -Prompt 'Limit (max samples):'
            if ($limit) { $extra += @('--limit', $limit) }
        }
        'benchmark' {
            $bracket = Read-OptionalArg -Prompt 'Bracket [blank=all]:'
            if ($bracket) { $extra += @('--bracket', $bracket) }
        }
        'autoresearch' {
            $maxExp = Read-OptionalArg -Prompt 'Max experiments:' -Default '5'
            $runs   = Read-OptionalArg -Prompt 'Runs per experiment:' -Default '2'
            $extra += @('--max-experiments', $maxExp, '--runs-per-experiment', $runs)
        }
        default {
            $limit = Read-OptionalArg -Prompt 'Limit (max items):'
            if ($limit) { $extra += @('--limit', $limit) }
        }
    }
    return $extra
}

function Invoke-Bench {
    param($Entry, [string[]]$ExtraArgs)
    Write-Host ''
    Write-Host "  --- Running $($Entry.FileName) ---" -ForegroundColor Cyan
    $cmd = @($Entry.Path) + $ExtraArgs + @('--verbose')
    Write-Host "  python $($cmd -join ' ')" -ForegroundColor DarkGray
    Write-Host ''

    $start = Get-Date
    try {
        & python @cmd
        $exit = $LASTEXITCODE
    } catch {
        Write-Host "  ERROR: $_" -ForegroundColor Red
        $exit = 1
    }
    $elapsed = (Get-Date) - $start

    Write-Host ''
    if ($exit -eq 0) {
        Write-Host "  Done in $($elapsed.ToString('hh\:mm\:ss'))" -ForegroundColor Green
    } else {
        Write-Host "  Exit code $exit after $($elapsed.ToString('hh\:mm\:ss'))" -ForegroundColor Red
    }
    Write-Host ''
    Write-Host '  Press Enter to return to the menu...' -ForegroundColor DarkGray
    [Console]::ReadKey($true) | Out-Null
}

# ─── Main loop ──────────────────────────────────────────────────────────────

Push-Location $BenchmarkDir
try {
    while ($true) {
        Show-Header
        $entries = Get-Benches
        $result  = Select-BenchInteractive -Entries $entries

        switch ($result.Action) {
            'Run' {
                $extra = Get-BenchArgs -Entry $result.Entry
                Invoke-Bench -Entry $result.Entry -ExtraArgs $extra
            }
            'Refresh' { continue }
            'Resize'  { continue }
            'Quit' {
                Write-Host ''
                Write-Host '  Bye.' -ForegroundColor Cyan
                Write-Host ''
                break
            }
        }
    }
} finally {
    Pop-Location
}
