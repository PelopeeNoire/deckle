# publish-native-runtime.ps1
#
# Builds a versioned zip of the 8 native DLLs Deckle's first-run wizard
# downloads at install time. The wizard fetches this zip from a GitHub
# Release of the Deckle repo (URL hardcoded in NativeRuntime.CurrentBundle)
# and extracts it into <UserDataRoot>\native\.
#
# Bundle layout (flat, matches NativeRuntime.CopyFromFolder semantics):
#   libwhisper.dll, ggml.dll, ggml-base.dll, ggml-cpu.dll, ggml-vulkan.dll
#   libgcc_s_seh-1.dll, libstdc++-6.dll, libwinpthread-1.dll
#   PROVENANCE.txt   — human-readable build metadata
#   SHA256SUMS       — `sha256sum -c` compatible
#
# Sources:
#   - whisper DLLs : <WhisperRepo>\build\bin\ (cmake -DGGML_VULKAN=ON)
#   - MinGW DLLs   : <scoop>\apps\mingw\current\bin\
#
# Recipe for rebuilding whisper.cpp lives in
# src/Deckle/docs/reference--native-runtime--1.0.md.

[CmdletBinding()]
param(
    # Bundle version, format X.Y.Z. Independent from the app version —
    # X.Y tracks whisper.cpp upstream minor, Z is a local rebuild counter.
    # See the recipe doc for the versioning scheme.
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    # Path to a local whisper.cpp clone with build/bin/ already populated
    # by `cmake --build`. Required — there is no in-repo whisper.cpp clone
    # anymore (deleted as part of this same chantier).
    [Parameter(Mandatory)]
    [string]$WhisperRepo,

    # Output directory for the produced zip. Defaults to a fresh subfolder
    # under the system temp dir.
    [string]$OutDir,

    # Also publish the zip as a GitHub Release `native-v$Version` via gh.
    # Requires gh CLI authenticated against the repo's remote.
    [switch]$Publish,

    # Optional release notes file passed to `gh release create --notes-file`.
    # Without it, gh's --generate-notes is used.
    [string]$Notes
)

$ErrorActionPreference = 'Stop'

# ── Catalog ──────────────────────────────────────────────────────────────────
#
# Single source of truth for the bundle is
# src/Deckle/Setup/NativeRuntime.cs RequiredDllNames. The two PowerShell
# scripts that produce or consume the bundle (this one and setup-assets.ps1)
# duplicate the list with a comment until extracted into a shared module.
# Any divergence is a bug.
$WhisperDlls = @(
    'libwhisper.dll', 'ggml.dll', 'ggml-base.dll',
    'ggml-cpu.dll',   'ggml-vulkan.dll'
)
$MingwDlls = @(
    'libgcc_s_seh-1.dll', 'libstdc++-6.dll', 'libwinpthread-1.dll'
)

function Step($msg) { Write-Host "`n[publish] $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "           $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "           $msg" -ForegroundColor Yellow }

# ── Resolve sources ──────────────────────────────────────────────────────────

$WhisperBin = Join-Path $WhisperRepo 'build\bin'
if (-not (Test-Path $WhisperBin)) {
    throw "whisper.cpp build output not found: $WhisperBin (cmake --build build needed first)"
}

$ScoopRoot  = if ($env:SCOOP) { $env:SCOOP } else { Join-Path $env:USERPROFILE 'scoop' }
$ScoopMingw = Join-Path $ScoopRoot 'apps\mingw\current\bin'
if (-not (Test-Path $ScoopMingw)) {
    throw "MinGW Scoop install not found: $ScoopMingw (run: scoop install mingw)"
}

if (-not $OutDir) {
    $OutDir = Join-Path ([System.IO.Path]::GetTempPath()) "deckle-native-$Version"
}
if (Test-Path $OutDir) {
    Warn "OutDir exists, cleaning: $OutDir"
    Remove-Item $OutDir -Recurse -Force
}
$null = New-Item -ItemType Directory -Path $OutDir

$ZipName    = "deckle-native-$Version.zip"
$ZipPath    = Join-Path $OutDir $ZipName
$StagingDir = Join-Path $OutDir 'staging'
$null = New-Item -ItemType Directory -Path $StagingDir

# ── Stage DLLs + compute per-file SHA256 ─────────────────────────────────────

Step "Stage DLLs to $StagingDir"

$Sha256ByName = [ordered]@{}
function Stage-Dll($srcDir, $name) {
    $src = Join-Path $srcDir $name
    if (-not (Test-Path $src)) { throw "MISSING source $src" }
    $dst = Join-Path $StagingDir $name
    Copy-Item $src $dst
    $hash = (Get-FileHash $dst -Algorithm SHA256).Hash.ToLower()
    $Sha256ByName[$name] = $hash
    $size = [math]::Round((Get-Item $dst).Length / 1MB, 2)
    Ok ("{0,-22} {1,8} MB  sha256={2}" -f $name, $size, $hash)
}

foreach ($n in $WhisperDlls) { Stage-Dll $WhisperBin  $n }
foreach ($n in $MingwDlls)   { Stage-Dll $ScoopMingw $n }

# ── Gather provenance metadata ───────────────────────────────────────────────

Step 'Gather provenance'

# whisper.cpp upstream version — read from CMakeLists.txt `project(... VERSION X.Y.Z)`.
$cmakeLists = Join-Path $WhisperRepo 'CMakeLists.txt'
$WhisperVersion = 'unknown'
if (Test-Path $cmakeLists) {
    $match = (Select-String -Path $cmakeLists -Pattern 'project\([^)]*VERSION\s+([^\s)]+)' | Select-Object -First 1)
    if ($match) { $WhisperVersion = $match.Matches[0].Groups[1].Value }
}

# whisper.cpp commit — only available when WhisperRepo is an actual git clone.
$WhisperCommit = 'unknown (source extraction, not a git clone)'
if (Test-Path (Join-Path $WhisperRepo '.git')) {
    $rev = & git -C $WhisperRepo rev-parse HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $rev) { $WhisperCommit = $rev.Trim() }
}

# Compiler — call cc.exe --version, take first line.
$CcExe = Join-Path $ScoopMingw 'cc.exe'
$CompilerLine = 'unknown'
if (Test-Path $CcExe) {
    $line = (& $CcExe --version 2>$null | Select-Object -First 1)
    if ($line) { $CompilerLine = $line }
}

# Vulkan SDK version — Scoop installs as <root>\apps\vulkan\<version>\, so the
# resolved 'current' shim points at the version dir; reading the symlink
# target is fragile, so we just read the version line from sdk_version.txt
# if present, else fall back to the env var path tail.
$VulkanSdk = if ($env:VULKAN_SDK) {
    $verFile = Join-Path $env:VULKAN_SDK 'sdk_version.txt'
    if (Test-Path $verFile) {
        (Get-Content $verFile -First 1).Trim()
    } else {
        # Resolve symlink target if any; otherwise print the literal path tail.
        $resolved = (Get-Item $env:VULKAN_SDK -ErrorAction SilentlyContinue).Target
        if ($resolved) { Split-Path -Leaf $resolved } else { Split-Path -Leaf $env:VULKAN_SDK }
    }
} else { 'unknown (VULKAN_SDK not set)' }

# CMake version
$CMakeLine = 'unknown'
$cmakeOut = (& cmake --version 2>$null | Select-Object -First 1)
if ($cmakeOut) { $CMakeLine = ($cmakeOut -replace '^cmake version\s+', '') }

# Ninja version
$NinjaLine = 'unknown'
$ninjaOut = (& ninja --version 2>$null)
if ($ninjaOut) { $NinjaLine = $ninjaOut.Trim() }

# Build flags from CMakeCache
$CMakeCache = Join-Path $WhisperRepo 'build\CMakeCache.txt'
$VulkanFlags = @()
if (Test-Path $CMakeCache) {
    $VulkanFlags = (Select-String -Path $CMakeCache -Pattern '^GGML_VULKAN[A-Z_]*:BOOL=' |
                    ForEach-Object { $_.Line })
}

$Hostname  = $env:COMPUTERNAME
$BuildDate = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')

# ── Compose PROVENANCE.txt ───────────────────────────────────────────────────

$filesBlock = ''
foreach ($n in ($WhisperDlls + $MingwDlls)) {
    $filesBlock += "{0,-22} sha256={1}`n" -f $n, $Sha256ByName[$n]
}

$prov = @"
Deckle native runtime bundle
============================

Bundle version : native-v$Version
Build date     : $BuildDate
Builder        : $Hostname

Upstream
--------
whisper.cpp    : v$WhisperVersion
commit         : $WhisperCommit

Toolchain
---------
Compiler       : $CompilerLine
Vulkan SDK     : $VulkanSdk
CMake          : $CMakeLine
Generator      : Ninja $NinjaLine

Build flags
-----------
$($VulkanFlags -join "`n")

Files (8)
---------
$filesBlock

Licenses
--------
whisper.cpp / ggml : MIT — https://github.com/ggerganov/whisper.cpp/blob/master/LICENSE
MinGW C++ runtime  : GPL-3 with runtime exception (libgcc, libstdc++) /
                     MIT (libwinpthread). Redistribution permitted as
                     dynamic linkage runtime per the GCC runtime library
                     exception.

Reproduction
------------
See src/Deckle/docs/reference--native-runtime--1.0.md in the Deckle
repository for the full recompilation recipe.
"@

Set-Content -Path (Join-Path $StagingDir 'PROVENANCE.txt') -Value $prov -Encoding UTF8
Ok 'PROVENANCE.txt written'

# ── SHA256SUMS (sha256sum -c compatible) ─────────────────────────────────────

$sumsLines = foreach ($n in ($WhisperDlls + $MingwDlls)) {
    '{0} *{1}' -f $Sha256ByName[$n], $n
}
Set-Content -Path (Join-Path $StagingDir 'SHA256SUMS') -Value $sumsLines -Encoding UTF8
Ok 'SHA256SUMS written'

# ── Zip flat + zip-level SHA256 ──────────────────────────────────────────────

Step "Compress to $ZipName"
Compress-Archive -Path (Join-Path $StagingDir '*') -DestinationPath $ZipPath -Force
$ZipSha256 = (Get-FileHash $ZipPath -Algorithm SHA256).Hash.ToLower()
$ZipBytes  = (Get-Item $ZipPath).Length
$ZipSize   = [math]::Round($ZipBytes / 1MB, 2)
Ok "$ZipName ($ZipSize MB) sha256=$ZipSha256"

Remove-Item $StagingDir -Recurse -Force

# ── Summary — paste-ready block for NativeRuntime.CurrentBundle ──────────────

Step 'Done'
Write-Host @"

  Zip    : $ZipPath
  Size   : $ZipBytes bytes ($ZipSize MB)
  SHA256 : $ZipSha256

  Paste into src/Deckle/Setup/NativeRuntime.cs CurrentBundle (after
  publishing — fill Url with the actual GitHub Release asset URL):

    public static NativeRuntimeBundle CurrentBundle { get; } = new(
        Version:     "$Version",
        Url:         "https://github.com/<owner>/deckle/releases/download/native-v$Version/$ZipName",
        Sha256:      "$ZipSha256",
        SizeBytes:   ${ZipBytes}L,
        DisplayName: "Whisper.cpp + Vulkan runtime");

"@ -ForegroundColor Green

# ── Optional: gh release create ──────────────────────────────────────────────

if ($Publish) {
    Step "Publish via gh release create"
    $tag = "native-v$Version"
    $title = "Native runtime $tag"
    $ghArgs = @('release', 'create', $tag, $ZipPath, '--title', $title)
    if ($Notes) {
        $ghArgs += @('--notes-file', $Notes)
    } else {
        $ghArgs += '--generate-notes'
    }
    & gh @ghArgs
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed (code $LASTEXITCODE)" }
    Ok "Released as $tag"
}
