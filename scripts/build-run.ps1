[CmdletBinding()]
param(
    # Kept for backward-compat with existing launch.json profiles; the
    # script now always passes `-restore` to MSBuild (cheap no-op when
    # the assets are current).
    [switch]$Restore,
    [switch]$NoRun,
    [switch]$Wait,
    [ValidateSet('Release','Debug')]
    [string]$Configuration = 'Release',
    # Explicit path to MSBuild.exe (takes priority over env + vswhere).
    [string]$MsBuild,
    # Build a specific repo or worktree instead of the one containing this
    # script. Accepts any path — main repo or any git worktree root.
    [string]$Target,
    # Interactive picker: lists the main repo + all linked worktrees via
    # `git worktree list` and prompts for a choice. Overrides -Target.
    [switch]$Pick
)

$ErrorActionPreference = 'Stop'
$ScriptDir  = $PSScriptRoot                                  # scripts/

# =============================================================================
# RepoRoot resolution
# -----------------------------------------------------------------------------
# Default: build the repo containing this script copy — the VS Code "Run"
# flow (PowerShell extension on the open file) naturally picks the
# worktree currently being edited.
#
# Override: -Target "<path>" picks any path. -Pick lists the worktrees
# and prompts. Both are for terminal use; VS Code Run should stay no-arg.
# =============================================================================
function Select-WorktreeInteractive {
    param([string]$ContextDir)

    Push-Location $ContextDir
    try {
        $raw = git worktree list --porcelain 2>$null
    } finally {
        Pop-Location
    }
    if (-not $raw) { throw "git worktree list failed - not a git repo?" }

    # Parse porcelain output into (path, branch) tuples.
    $entries = @()
    $curPath = $null
    $curBranch = $null
    foreach ($line in $raw) {
        if ($line -like 'worktree *') {
            if ($curPath) {
                $entries += [pscustomobject]@{ Path = $curPath; Branch = ($curBranch ?? '(detached)') }
            }
            $curPath = $line.Substring(9)
            $curBranch = $null
        } elseif ($line -like 'branch *') {
            $curBranch = ($line.Substring(7)) -replace '^refs/heads/', ''
        }
    }
    if ($curPath) {
        $entries += [pscustomobject]@{ Path = $curPath; Branch = ($curBranch ?? '(detached)') }
    }
    if ($entries.Count -eq 0) { throw "No worktrees found" }

    # Pre-format each line with branch badge + path. We MUST truncate so the
    # rendered line fits on a single terminal row — the cursor math below
    # assumes 1 label = 1 physical line, and a wrapped line pushes CursorTop
    # by 2, which poisons `$top = $bottom - $labels.Count` and causes the
    # previous selection to stay visible above the new one (ghost entries).
    # Elide the path's prefix so the tail (worktree folder name, usually the
    # distinctive part) stays legible.
    $maxLineLen = [Console]::WindowWidth - 5  # "  > " prefix + trailing gap
    $labels = foreach ($e in $entries) {
        $branch  = "{0,-28}" -f "[$($e.Branch)]"
        $path    = $e.Path
        $budget  = $maxLineLen - $branch.Length - 1  # -1 for the separating space
        if ($budget -lt 4) {
            $path = [char]0x2026  # single ellipsis char, path blown by a huge branch label
        } elseif ($path.Length -gt $budget) {
            $path = ([char]0x2026) + $path.Substring($path.Length - ($budget - 1))
        }
        "$branch $path"
    }

    $selected = 0
    $header   = "Pick a worktree (Up/Down, Enter = confirm, Esc = cancel):"

    Write-Host ""
    Write-Host $header -ForegroundColor Cyan

    # Render each line once so the buffer grows naturally, then capture the
    # final cursor position. Going the other way (capture then reserve via
    # Write-Host "") breaks when the buffer scrolls on near-bottom terminals.
    for ($i = 0; $i -lt $labels.Count; $i++) {
        $prefix = if ($i -eq $selected) { '  > ' } else { '    ' }
        if ($i -eq $selected) {
            Write-Host ($prefix + $labels[$i]) -ForegroundColor Green
        } else {
            Write-Host ($prefix + $labels[$i])
        }
    }
    $bottom = [Console]::CursorTop
    $top    = [Math]::Max(0, $bottom - $labels.Count)

    [Console]::CursorVisible = $false
    try {
        while ($true) {
            $key = [Console]::ReadKey($true)
            $prev = $selected
            switch ($key.Key) {
                'UpArrow'   { if ($selected -gt 0)                   { $selected-- } }
                'DownArrow' { if ($selected -lt $entries.Count - 1)  { $selected++ } }
                'Enter'     {
                    [Console]::SetCursorPosition(0, $bottom)
                    return $entries[$selected].Path
                }
                'Escape'    {
                    [Console]::SetCursorPosition(0, $bottom)
                    throw "Cancelled"
                }
            }
            if ($selected -eq $prev) { continue }

            # Repaint just the two lines that changed (prev and new).
            foreach ($i in @($prev, $selected)) {
                [Console]::SetCursorPosition(0, $top + $i)
                $prefix = if ($i -eq $selected) { '  > ' } else { '    ' }
                $line   = $prefix + $labels[$i]
                $pad    = [Console]::WindowWidth - $line.Length - 1
                if ($pad -gt 0) { $line += (' ' * $pad) }
                if ($i -eq $selected) {
                    Write-Host $line -ForegroundColor Green -NoNewline
                } else {
                    Write-Host $line -NoNewline
                }
            }
        }
    } finally {
        [Console]::CursorVisible = $true
    }
}

if ($Pick) {
    $RepoRoot = Select-WorktreeInteractive -ContextDir $ScriptDir
} elseif ($Target) {
    if (-not (Test-Path $Target)) { throw "Target not found: $Target" }
    $RepoRoot = (Get-Item $Target).FullName
} else {
    $RepoRoot = Split-Path $ScriptDir
}

Write-Host "Repo: $RepoRoot" -ForegroundColor DarkGray

$ProjectDir = Join-Path $RepoRoot 'src\WhispUI'
$Csproj     = Join-Path $ProjectDir 'WhispUI.csproj'
$ExePath    = Join-Path $ProjectDir "bin\x64\$Configuration\net10.0-windows10.0.19041.0\WhispUI.exe"

if (-not (Test-Path $Csproj)) { throw "csproj not found at $Csproj — is '$RepoRoot' a WhispUI repo?" }

# =============================================================================
# Worktree junctions for gitignored folders
# -----------------------------------------------------------------------------
# `native/` (whisper.cpp DLLs, MinGW runtime) and `models/` (Whisper .bin)
# are gitignored, so a fresh git worktree doesn't have them. The csproj
# references `..\..\native\*.dll` with PreserveNewest, so an empty path
# silently produces an exe without the transcription engine.
#
# When running from a worktree, resolve the main repo via
# `git rev-parse --git-common-dir` and junction the missing folders.
# No-op when running from the main repo or when git is unavailable.
# =============================================================================
function Sync-WorktreeJunctions {
    param([string]$RepoRoot)

    $needed = @('native', 'models')
    $missing = @($needed | Where-Object { -not (Test-Path (Join-Path $RepoRoot $_)) })
    if ($missing.Count -eq 0) { return }

    Push-Location $RepoRoot
    try {
        $commonDir = git rev-parse --git-common-dir 2>$null
    } catch {
        return
    } finally {
        Pop-Location
    }
    if (-not $commonDir) { return }

    if (-not [System.IO.Path]::IsPathRooted($commonDir)) {
        $commonDir = Join-Path $RepoRoot $commonDir
    }
    $mainRepo = (Get-Item (Split-Path $commonDir)).FullName

    if ($mainRepo -eq (Get-Item $RepoRoot).FullName) {
        # Main repo — the folders are genuinely missing, not a worktree gap.
        return
    }

    foreach ($folder in $missing) {
        $source = Join-Path $mainRepo $folder
        $target = Join-Path $RepoRoot $folder
        if (-not (Test-Path $source)) {
            Write-Host "Worktree junction skipped ($folder not in main repo): $source" -ForegroundColor Yellow
            continue
        }
        Write-Host "Creating junction: $target -> $source" -ForegroundColor Cyan
        New-Item -ItemType Junction -Path $target -Value $source | Out-Null
    }
}

Sync-WorktreeJunctions -RepoRoot $RepoRoot

# =============================================================================
# MSBuild configuration
# -----------------------------------------------------------------------------
# `dotnet build` is broken on WhispUI due to the XamlCompiler MSB3073 bug,
# so we must use the Visual Studio MSBuild Framework (MSBuildRuntimeType=Full).
#
# Resolution order:
#   1. -MsBuild parameter (explicit override)
#   2. WHISPUI_MSBUILD env var (recommended for non-standard VS install paths;
#      set once with: setx WHISPUI_MSBUILD "<path\to\MSBuild.exe>")
#   3. vswhere.exe (standard VS install under Program Files)
#   4. error with instructions
# =============================================================================
function Resolve-MsBuild {
    param([string]$Explicit)

    if ($Explicit) {
        if (-not (Test-Path $Explicit)) { throw "MSBuild not found: $Explicit" }
        return $Explicit
    }

    if ($env:WHISPUI_MSBUILD) {
        if (-not (Test-Path $env:WHISPUI_MSBUILD)) {
            throw "WHISPUI_MSBUILD points to a missing file: $($env:WHISPUI_MSBUILD)"
        }
        return $env:WHISPUI_MSBUILD
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -prerelease -products * `
            -requires Microsoft.Component.MSBuild `
            -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
        if ($found -and (Test-Path $found)) { return $found }
    }

    throw @"
MSBuild.exe not found. Configure one of the following:
  - parameter -MsBuild "<path\MSBuild.exe>"
  - env var WHISPUI_MSBUILD (persistent: setx WHISPUI_MSBUILD "<path>")
  - standard Visual Studio install detectable by vswhere
"@
}

$MsBuildExe = Resolve-MsBuild -Explicit $MsBuild
Write-Host "MSBuild: $MsBuildExe" -ForegroundColor DarkGray

# 1. Kill running instance (otherwise the .exe is locked)
Get-Process -Name WhispUI -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Killing WhispUI PID $($_.Id)" -ForegroundColor Yellow
    $_ | Stop-Process -Force
}

# 2. Build via VS MSBuild (XamlCompiler MSB3073 bug prevents dotnet build CLI)
# Use the `-restore` FLAG (not `-t:Restore;Build`). The flag triggers a
# separate evaluation phase before Build, so the WindowsAppSDK targets
# (CompileXaml etc.) get imported from the freshly-regenerated
# .nuget.g.targets. `-t:Restore;Build` runs both in a single evaluation
# and silently skips CompileXaml in a fresh worktree -> CS5001 +
# CS0103 InitializeComponent errors.
# -restore is a no-op if assets are already current, so we always pass it.
Write-Host "Build (Build, $Configuration x64)..." -ForegroundColor Cyan
& $MsBuildExe $Csproj '-restore' '-t:Build' "-p:Configuration=$Configuration" '-p:Platform=x64' '-v:m' '-nologo'
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed (code $LASTEXITCODE)" }

# 3. Run
if ($NoRun) { return }
if (-not (Test-Path $ExePath)) { throw "Exe not found: $ExePath" }
Write-Host "Run $ExePath" -ForegroundColor Green
$proc = Start-Process -FilePath $ExePath -PassThru
if ($Wait) { $proc.WaitForExit() }
