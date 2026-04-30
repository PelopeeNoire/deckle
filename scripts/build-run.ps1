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
# via the shared interactive picker (scripts/_menu.psm1) and prompts.
# Both are for terminal use; VS Code Run should stay no-arg.
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

$ProjectDir = Join-Path $RepoRoot 'src\Deckle'
$Csproj     = Join-Path $ProjectDir 'Deckle.csproj'
$ExePath    = Join-Path $ProjectDir "bin\x64\$Configuration\net10.0-windows10.0.19041.0\Deckle.exe"

if (-not (Test-Path $Csproj)) { throw "csproj not found at $Csproj — is '$RepoRoot' a Deckle repo?" }

# =============================================================================
# MSBuild configuration
# -----------------------------------------------------------------------------
# `dotnet build` is broken on Deckle due to the XamlCompiler MSB3073 bug,
# so we must use the Visual Studio MSBuild Framework (MSBuildRuntimeType=Full).
#
# Resolution order:
#   1. -MsBuild parameter (explicit override)
#   2. DECKLE_MSBUILD env var (recommended for non-standard VS install paths;
#      set once with: setx DECKLE_MSBUILD "<path\to\MSBuild.exe>")
#   3. vswhere.exe (standard VS install under Program Files)
#   4. error with instructions
# =============================================================================
function Resolve-MsBuild {
    param([string]$Explicit)

    if ($Explicit) {
        if (-not (Test-Path $Explicit)) { throw "MSBuild not found: $Explicit" }
        return $Explicit
    }

    if ($env:DECKLE_MSBUILD) {
        if (-not (Test-Path $env:DECKLE_MSBUILD)) {
            throw "DECKLE_MSBUILD points to a missing file: $($env:DECKLE_MSBUILD)"
        }
        return $env:DECKLE_MSBUILD
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
  - env var DECKLE_MSBUILD (persistent: setx DECKLE_MSBUILD "<path>")
  - standard Visual Studio install detectable by vswhere
"@
}

$MsBuildExe = Resolve-MsBuild -Explicit $MsBuild
Write-Host "MSBuild: $MsBuildExe" -ForegroundColor DarkGray

# 1. Kill running instance (otherwise the .exe is locked)
Get-Process -Name Deckle -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Killing Deckle PID $($_.Id)" -ForegroundColor Yellow
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
