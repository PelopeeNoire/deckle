# bootstrap-dev-env.ps1
#
# Brings a fresh Windows 11 machine up to speed for building and running
# Deckle. Probes what's already installed, then installs the missing pieces
# via winget (OS-level installers) and scoop (dev toolchain). Idempotent:
# safe to re-run.
#
# Out of scope (intentional):
#   - Does NOT build or run Deckle (see scripts/build-run.ps1, and
#     CLAUDE.md non-negotiable).
#   - Does NOT clone whisper.cpp (location is per-dev preference; prints
#     the command instead). Required only for -Full (native rebuild).
#   - Does NOT pull Ollama models (which model is a per-deployment choice;
#     prints the command instead).
#
# Tiers:
#   Default — Tier 1 (managed build) + runtime assets via setup-assets.ps1.
#             Sufficient for 99 % of C#/XAML work.
#   -Full   — Tier 1 + Tier 2 (native recompile toolchain) + Ollama.
#             For maintainers who rebuild whisper.cpp DLLs or test the LLM
#             rewrite path end-to-end.
#
# Usage:
#   scripts\bootstrap-dev-env.ps1                      # default tier + assets
#   scripts\bootstrap-dev-env.ps1 -DryRun              # probe only, no install
#   scripts\bootstrap-dev-env.ps1 -Full                # full toolchain
#   scripts\bootstrap-dev-env.ps1 -SkipAssets          # skip setup-assets.ps1
#   scripts\bootstrap-dev-env.ps1 -Yes                 # no confirmation prompt

[CmdletBinding()]
param(
    # Probe + report only. No install, no env var change, no asset download.
    [switch]$DryRun,

    # Include the native recompile toolchain (MinGW, CMake, Ninja, Vulkan
    # SDK) and Ollama. Required to rebuild whisper.cpp DLLs or to use the
    # local LLM rewrite path. Off by default to keep first setup lean.
    [switch]$Full,

    # Skip the final invocation of scripts/setup-assets.ps1. Use when you
    # plan to provision <UserDataRoot> manually or rely on the first-run
    # wizard.
    [switch]$SkipAssets,

    # Release tag passed to setup-assets.ps1 -FromRelease. Pins which
    # native-vX.Y.Z bundle gets downloaded.
    [string]$AssetsRelease = '1.0.0',

    # Skip the confirmation prompt before installing.
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
$ScriptDir = $PSScriptRoot

# =============================================================================
# Helpers
# =============================================================================

function Write-Section($title) {
    Write-Host ""
    Write-Host "=== $title ===" -ForegroundColor Cyan
}

function Write-Step($msg)  { Write-Host "  $msg" -ForegroundColor Gray }
function Write-Good($msg)  { Write-Host "  [OK]      $msg" -ForegroundColor Green }
function Write-Miss($msg)  { Write-Host "  [MISSING] $msg" -ForegroundColor Yellow }
function Write-Skip($msg)  { Write-Host "  [SKIP]    $msg" -ForegroundColor DarkGray }
function Write-Fail($msg)  { Write-Host "  [FAIL]    $msg" -ForegroundColor Red }

# Returns the captured command output if the command exists and runs cleanly,
# or $null otherwise. Used by every probe — uniform shape avoids the silent
# truthy-empty-string bug from my earlier ad-hoc probe.
#
# Note: $args is a PowerShell automatic variable; a parameter of the same
# name silently breaks splatting. Hence the fixed --version flag inline —
# all probed tools accept it the same way.
function Test-Command([string]$exe) {
    if (-not (Get-Command $exe -ErrorAction SilentlyContinue)) { return $null }
    try {
        $out = & $exe --version 2>$null | Select-Object -First 1
        if ($LASTEXITCODE -ne 0) { return $null }
        return ($out | Out-String).Trim()
    } catch { return $null }
}

# vswhere lives at a fixed installer path (kept under (x86) even when VS
# itself goes 64-bit). Returns the MSBuild path if found, else $null.
function Find-MsBuild {
    if ($env:DECKLE_MSBUILD -and (Test-Path $env:DECKLE_MSBUILD)) {
        return $env:DECKLE_MSBUILD
    }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { return $null }
    $found = & $vswhere -latest -prerelease -products * `
        -requires Microsoft.Component.MSBuild `
        -find 'MSBuild\**\Bin\amd64\MSBuild.exe' 2>$null | Select-Object -First 1
    if ($found -and (Test-Path $found)) { return $found }
    return $null
}

# =============================================================================
# Probe
# =============================================================================

Write-Section "Probing current state"

$state = [ordered]@{
    PowerShell  = $PSVersionTable.PSVersion.ToString()
    Winget      = Test-Command 'winget'
    Git         = Test-Command 'git'
    Gh          = Test-Command 'gh'
    Dotnet      = Test-Command 'dotnet'
    MsBuild     = Find-MsBuild
    Scoop       = Test-Command 'scoop'
    Gcc         = Test-Command 'gcc'
    Cmake       = Test-Command 'cmake'
    Ninja       = Test-Command 'ninja'
    VulkanSdk   = if ($env:VULKAN_SDK -and (Test-Path $env:VULKAN_SDK)) { $env:VULKAN_SDK } else { $null }
    Ollama      = Test-Command 'ollama'
    VsCodium    = Test-Command 'codium'
}

foreach ($k in $state.Keys) {
    $v = $state[$k]
    if ($v) { Write-Good "$k : $v" } else { Write-Miss $k }
}

# Hard requirements before going further: winget, git. They bootstrap
# themselves into a Windows machine before this script can do anything
# useful. If BOTH come up missing, the most likely cause isn't a real
# install gap but a session PATH problem — the PowerShell extension's
# Integrated Console in VS Code / VSCodium notoriously starts a host
# without WindowsApps in PATH, which hides winget. Diagnose first.
if (-not $state.Winget -and -not $state.Git) {
    Write-Host ""
    Write-Host "Both winget and git report missing — this is almost certainly a session" -ForegroundColor Yellow
    Write-Host "PATH issue, not a real install gap." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Try running this script from a regular PowerShell terminal:" -ForegroundColor Yellow
    Write-Host "  - Win+X -> Terminal (Windows Terminal with pwsh.exe), OR" -ForegroundColor DarkGray
    Write-Host "  - Right-click in Explorer -> 'Open in Terminal'" -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "If it still fails there, then winget / git are genuinely missing." -ForegroundColor Yellow
    throw "Aborting — re-run from a fresh PowerShell session."
}
if (-not $state.Winget) {
    throw "winget is required. Install 'App Installer' from the Microsoft Store, then re-run."
}
if (-not $state.Git) {
    throw "git is required. Install via 'winget install --id Git.Git -e', then re-run."
}

# =============================================================================
# Build install plan
# =============================================================================

Write-Section "Install plan"

$plan = New-Object System.Collections.Generic.List[object]

function Add-Plan($name, $why, $cmd) {
    $plan.Add([pscustomobject]@{ Name = $name; Why = $why; Cmd = $cmd })
}

# Tier 1 — managed build

if (-not $state.Gh) {
    Add-Plan 'GitHub CLI' 'GitHub auth for push and PR workflows' {
        winget install --id GitHub.cli -e --accept-source-agreements --accept-package-agreements
    }
}

if (-not $state.Dotnet) {
    # .NET 10 SDK is required both for MSBuild to resolve Microsoft.NET.Sdk
    # and for `dotnet` CLI diagnostics. The VS ManagedDesktop workload does
    # NOT reliably bundle it — install explicitly.
    Add-Plan '.NET 10 SDK' '.NET SDK resolution for MSBuild + dotnet CLI' {
        winget install --id Microsoft.DotNet.SDK.10 -e `
            --accept-source-agreements --accept-package-agreements
    }
}

if (-not $state.MsBuild) {
    # VS 2026 Community + WinUI workload. The override string adds the
    # ManagedDesktop workload (covers .NET WinUI 3) plus the WindowsAppSDK
    # component group (XAML compiler, project templates, runtime). If VS
    # 2026 introduces a dedicated WinUI workload ID, swap the --add line.
    # --quiet --wait --norestart make the installer headless; --norestart
    # avoids the reboot prompt mid-bootstrap.
    Add-Plan 'Visual Studio 2026 Community + WinUI workload' `
        'MSBuild Framework (needed: dotnet build CLI hits XamlCompiler MSB3073, see CLAUDE.md)' {
        # VS 2026 dropped the year suffix from its winget ID (was
        # Microsoft.VisualStudio.2022.Community, now just .Community).
        # ManagedDesktop = .NET desktop workload (WinUI 3 project templates).
        # WindowsAppSDK.Cs = XAML compiler, runtime, packaging.
        # VC.Tools.x86.x64 = MSVC C++ build tools — required by the WinUI
        #   XAML compiler's GetLatestMSVCVersion task even for pure-C# projects
        #   (it enumerates VC\Tools\MSVC\ to locate platform headers).
        $override = '--quiet --wait --norestart ' +
                    '--add Microsoft.VisualStudio.Workload.ManagedDesktop;includeRecommended ' +
                    '--add Microsoft.VisualStudio.ComponentGroup.WindowsAppSDK.Cs ' +
                    '--add Microsoft.VisualStudio.Component.VC.Tools.x86.x64'
        winget install --id Microsoft.VisualStudio.Community -e `
            --accept-source-agreements --accept-package-agreements `
            --override $override
    }
}

# Tier 2 — native recompile (opt-in via -Full)

if ($Full) {
    if (-not $state.Scoop) {
        # Scoop bootstrap installer. Per-user, no UAC, lives in %USERPROFILE%\scoop.
        Add-Plan 'Scoop' 'Per-user package manager for the native toolchain' {
            Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser -Force
            Invoke-RestMethod -Uri 'https://get.scoop.sh' | Invoke-Expression
        }
    }

    # extras bucket carries vulkan; main bucket carries mingw/cmake/ninja
    Add-Plan 'Scoop extras bucket' 'Provides the Vulkan SDK package' {
        scoop bucket add extras 2>$null
    }

    if (-not $state.Gcc) {
        Add-Plan 'MinGW (GCC 15.2.0)'  'C++ toolchain for whisper.cpp Vulkan build' { scoop install mingw }
    }
    if (-not $state.Cmake) {
        Add-Plan 'CMake' 'Build system for whisper.cpp' { scoop install cmake }
    }
    if (-not $state.Ninja) {
        Add-Plan 'Ninja' 'Fast generator used by the whisper.cpp CMake preset' { scoop install ninja }
    }
    if (-not $state.VulkanSdk) {
        Add-Plan 'Vulkan SDK (LunarG)' 'Headers + loader for ggml-vulkan.dll' { scoop install vulkan }
    }

    if (-not $state.Ollama) {
        Add-Plan 'Ollama' 'Local LLM runtime for the rewrite feature' {
            winget install --id Ollama.Ollama -e --accept-source-agreements --accept-package-agreements
        }
    }
}

if ($plan.Count -eq 0) {
    Write-Step "Nothing to install — current state already covers the requested tier."
} else {
    foreach ($item in $plan) {
        Write-Host ("  + {0,-50} {1}" -f $item.Name, $item.Why) -ForegroundColor White
    }
}

if ($DryRun) {
    Write-Section "Dry run — exiting before any install."
    return
}

# =============================================================================
# Confirm
# =============================================================================

if ($plan.Count -gt 0 -and -not $Yes) {
    Write-Host ""
    $reply = Read-Host "Proceed? [y/N]"
    if ($reply -notmatch '^[yY]') {
        Write-Host "Aborted." -ForegroundColor Yellow
        return
    }
}

# =============================================================================
# Execute
# =============================================================================

if ($plan.Count -gt 0) {
    Write-Section "Installing"
    foreach ($item in $plan) {
        Write-Step "→ $($item.Name)"
        try {
            & $item.Cmd
            if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) {
                Write-Fail "$($item.Name) installer exited with code $LASTEXITCODE"
            } else {
                Write-Good $item.Name
            }
        } catch {
            Write-Fail "$($item.Name) — $($_.Exception.Message)"
        }
    }
}

# =============================================================================
# Post-install env vars
# =============================================================================

Write-Section "Environment variables"

# DECKLE_MSBUILD — short-circuits the vswhere lookup in build-run.ps1. Set
# it whenever vswhere now resolves an MSBuild, even if it wasn't set before.
$msb = Find-MsBuild
if ($msb -and ($env:DECKLE_MSBUILD -ne $msb)) {
    [Environment]::SetEnvironmentVariable('DECKLE_MSBUILD', $msb, 'User')
    Write-Good "DECKLE_MSBUILD = $msb (User)"
} elseif ($msb) {
    Write-Skip "DECKLE_MSBUILD already set to current MSBuild"
} else {
    Write-Skip "DECKLE_MSBUILD — no MSBuild detected yet (VS install pending?)"
}

# VULKAN_SDK — scoop's vulkan package does not always set this. Find the
# install root and pin it. Only touch if -Full was requested.
if ($Full -and -not $env:VULKAN_SDK) {
    $scoopVulkan = "$env:USERPROFILE\scoop\apps\vulkan\current"
    if (Test-Path $scoopVulkan) {
        [Environment]::SetEnvironmentVariable('VULKAN_SDK', $scoopVulkan, 'User')
        Write-Good "VULKAN_SDK = $scoopVulkan (User)"
    } else {
        Write-Skip "VULKAN_SDK — scoop vulkan path not found; set manually if you installed elsewhere"
    }
}

# =============================================================================
# Runtime assets
# =============================================================================

if (-not $SkipAssets) {
    Write-Section "Runtime assets"
    $setup = Join-Path $ScriptDir 'setup-assets.ps1'
    if (-not (Test-Path $setup)) {
        Write-Fail "setup-assets.ps1 not found at $setup"
    } else {
        Write-Step "Invoking setup-assets.ps1 -FromRelease $AssetsRelease"
        & $setup -FromRelease $AssetsRelease
    }
}

# =============================================================================
# Next steps
# =============================================================================

Write-Section "Next steps"

if ($Full) {
    Write-Host "  - Clone whisper.cpp for native rebuilds (location is your choice):" -ForegroundColor White
    Write-Host "      git clone https://github.com/ggerganov/whisper.cpp D:\workspace\whisper.cpp" -ForegroundColor DarkGray
    Write-Host "    Recipe: docs/reference--native-runtime--1.0.md" -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "  - Pull an Ollama model for the rewrite feature (pick one):" -ForegroundColor White
    Write-Host "      ollama pull llama3.2:3b      # ~2 GB, fast on CPU-only laptops" -ForegroundColor DarkGray
    Write-Host "      ollama pull phi3:mini        # ~2 GB, comparable" -ForegroundColor DarkGray
}

Write-Host "  - Open a new terminal so DECKLE_MSBUILD / VULKAN_SDK are picked up." -ForegroundColor White
Write-Host "  - Then build + run:" -ForegroundColor White
Write-Host "      scripts\build-run.ps1" -ForegroundColor DarkGray
Write-Host ""
