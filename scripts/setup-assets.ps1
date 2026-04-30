# setup-assets.ps1
#
# Populates <UserDataRoot>\native\ and <UserDataRoot>\models\ with the
# whisper.cpp DLLs, MinGW C++ runtime, and Whisper models the app needs
# at runtime. This is the canonical setup step — the app reads from
# <UserDataRoot> exclusively (see src/Deckle/AppPaths.cs).
#
# Default <UserDataRoot> resolution mirrors AppPaths.ResolveUserDataRoot
# on the C# side:
#   1. -DataRoot parameter (highest priority)
#   2. DECKLE_DATA_ROOT environment variable
#   3. %LOCALAPPDATA%\Deckle\
#
# Sources:
#   - whisper.cpp DLLs : <repo>\whisper.cpp\build\bin\
#                        (whisper.cpp must be cloned next to the repo and
#                         built with the Vulkan backend beforehand)
#   - MinGW runtime    : %SCOOP%\apps\mingw\current\bin\
#   - Whisper models   : HuggingFace (curl with progress bar, idempotent
#                        on file size to avoid re-downloading 3 GB)
#
# Idempotent: skips files already present at the same size.

[CmdletBinding()]
param(
    # Override programmable du root cible. Priorité la plus haute, devant
    # DECKLE_DATA_ROOT, devant %LOCALAPPDATA%\Deckle\.
    [string]$DataRoot,

    # Also populate <repo>\native\ and <repo>\models\ in addition to the
    # UserDataRoot target. Useful for development inspection or running
    # the app via the dev fallback layout (no longer needed at runtime,
    # kept here for occasional debugging).
    [switch]$AlsoInRepo,

    # Download ggml-large-v3.bin (~3 GB) on top of ggml-base.bin. Off by
    # default to keep first-time setup fast.
    [switch]$WithLarge,

    # Re-copy / re-download even if the destination already exists with
    # the matching size.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# Repo + source paths
$Repo        = Split-Path -Parent $PSScriptRoot
$WhisperBin  = Join-Path $Repo 'whisper.cpp\build\bin'
# Scoop respects the SCOOP env var when installed in a non-default location;
# fall back to the per-user default %USERPROFILE%\scoop otherwise.
$ScoopRoot   = if ($env:SCOOP) { $env:SCOOP } else { Join-Path $env:USERPROFILE 'scoop' }
$ScoopMingw  = Join-Path $ScoopRoot 'apps\mingw\current\bin'

# Target root resolution — same order as AppPaths.ResolveUserDataRoot
if ($DataRoot)                { $TargetRoot = $DataRoot }
elseif ($env:DECKLE_DATA_ROOT) { $TargetRoot = $env:DECKLE_DATA_ROOT }
else                          { $TargetRoot = Join-Path $env:LOCALAPPDATA 'Deckle' }

$TargetNative = Join-Path $TargetRoot 'native'
$TargetModels = Join-Path $TargetRoot 'models'

# Optional repo-side mirrors when -AlsoInRepo is passed
$RepoNative = Join-Path $Repo 'native\whisper'
$RepoMingw  = Join-Path $Repo 'native\mingw'
$RepoModels = Join-Path $Repo 'models'

function Step($msg) { Write-Host "`n[setup] $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "         $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "         $msg" -ForegroundColor Yellow }

# Catalog (matches Deckle.Setup.NativeRuntime.RequiredDllNames)
$WhisperDlls = @(
    'libwhisper.dll', 'ggml.dll', 'ggml-base.dll',
    'ggml-cpu.dll',   'ggml-vulkan.dll'
)
$MingwDlls = @(
    'libgcc_s_seh-1.dll', 'libstdc++-6.dll', 'libwinpthread-1.dll'
)

# Idempotent copy — skip if destination already exists with same size,
# unless -Force was passed. Cheaper than a hash check and good enough
# for a setup script (the only mutation source is this script itself).
function CopyIdempotent($src, $dst) {
    $name = Split-Path $src -Leaf
    if (-not $Force -and (Test-Path $dst) -and ((Get-Item $dst).Length -eq (Get-Item $src).Length)) {
        Ok "skip  $name (same size)"
        return
    }
    Copy-Item $src $dst -Force
    $size = [math]::Round((Get-Item $dst).Length / 1MB, 1)
    Ok "copy  $name ($size MB)"
}

# Idempotent download via curl.exe (built into Win10/11, much faster than
# Invoke-WebRequest for large files, native progress bar). $expectedMinBytes
# is a coarse "is the file present and roughly complete" check.
function Download($url, $dst, $expectedMinBytes) {
    $name = Split-Path $dst -Leaf
    if (-not $Force -and (Test-Path $dst) -and ((Get-Item $dst).Length -ge $expectedMinBytes)) {
        Ok "already present $name ($([math]::Round((Get-Item $dst).Length/1MB,1)) MB)"
        return
    }
    Write-Host "         downloading $name ..."
    & curl.exe -L --fail --retry 3 --progress-bar -o $dst $url
    if ($LASTEXITCODE -ne 0) { throw "curl failed for $url" }
    Ok "downloaded $name ($([math]::Round((Get-Item $dst).Length/1MB,1)) MB)"
}

Step "target root: $TargetRoot"

# 1. Create target folders (and optional repo mirrors).
Step 'create folders'
$folders = @($TargetNative, $TargetModels)
if ($AlsoInRepo) { $folders += @($RepoNative, $RepoMingw, $RepoModels) }
foreach ($d in $folders) {
    if (-not (Test-Path $d)) {
        New-Item -ItemType Directory -Path $d -Force | Out-Null
        Ok "created $d"
    } else {
        Ok "exists  $d"
    }
}

# 2. whisper.cpp DLLs — copy directly into <TargetRoot>\native\.
# Source is whisper.cpp\build\bin (must be built locally with Vulkan).
Step 'copy whisper.cpp DLLs'
foreach ($name in $WhisperDlls) {
    $src = Join-Path $WhisperBin $name
    if (-not (Test-Path $src)) {
        Warn "MISSING source $src — rebuild whisper.cpp needed"
        continue
    }
    CopyIdempotent $src (Join-Path $TargetNative $name)
    if ($AlsoInRepo) { CopyIdempotent $src (Join-Path $RepoNative $name) }
}

# 3. MinGW runtime DLLs — required because ggml-vulkan.dll links against
# the MinGW C++ runtime. Sourced from the Scoop install.
Step 'copy MinGW runtime DLLs'
foreach ($name in $MingwDlls) {
    $src = Join-Path $ScoopMingw $name
    if (-not (Test-Path $src)) {
        Warn "MISSING source $src — install MinGW via Scoop (scoop install mingw)"
        continue
    }
    CopyIdempotent $src (Join-Path $TargetNative $name)
    if ($AlsoInRepo) { CopyIdempotent $src (Join-Path $RepoMingw $name) }
}

# 4. Whisper models — downloaded from HuggingFace into <TargetRoot>\models\.
# ggml-base.bin is the fast default, ggml-large-v3.bin is gated behind
# -WithLarge because it's 3 GB. Silero VAD is always fetched — it's tiny.
Step 'download Whisper models'
$baseDst = Join-Path $TargetModels 'ggml-base.bin'
Download `
    'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin' `
    $baseDst `
    130MB
if ($AlsoInRepo) { CopyIdempotent $baseDst (Join-Path $RepoModels 'ggml-base.bin') }

if ($WithLarge) {
    $largeDst = Join-Path $TargetModels 'ggml-large-v3.bin'
    Download `
        'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin' `
        $largeDst `
        3000MB
    if ($AlsoInRepo) { CopyIdempotent $largeDst (Join-Path $RepoModels 'ggml-large-v3.bin') }
} else {
    Ok 'skipped ggml-large-v3.bin (pass -WithLarge to fetch ~3 GB model)'
}

Step 'download Silero VAD'
$vadDst = Join-Path $TargetModels 'ggml-silero-v6.2.0.bin'
Download `
    'https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v6.2.0.bin' `
    $vadDst `
    700KB
if ($AlsoInRepo) { CopyIdempotent $vadDst (Join-Path $RepoModels 'ggml-silero-v6.2.0.bin') }

# Final summary.
$nativeCount = (Get-ChildItem $TargetNative -File -ErrorAction SilentlyContinue | Measure-Object).Count
$modelCount  = (Get-ChildItem $TargetModels -File -ErrorAction SilentlyContinue | Measure-Object).Count
Step 'done'
Write-Host "         $TargetNative : $nativeCount file(s)"
Write-Host "         $TargetModels : $modelCount file(s)"
Write-Host "`nNext: scripts\launcher.ps1 (Build & run)" -ForegroundColor Cyan
