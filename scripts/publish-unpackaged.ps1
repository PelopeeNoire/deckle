[CmdletBinding()]
param(
    [ValidateSet('Release','Debug')]
    [string]$Configuration = 'Release',
    [switch]$Open,
    # Skip launching the published exe after publish (default = launch).
    [switch]$NoRun,
    # Block until the published process exits (implies launch).
    [switch]$Wait,
    # Explicit path to MSBuild.exe (takes priority over env + vswhere).
    [string]$MsBuild
)

$ErrorActionPreference = 'Stop'
$ScriptDir  = $PSScriptRoot                                  # scripts/
$RepoRoot   = Split-Path $ScriptDir                          # repo root
$ProjectDir = Join-Path $RepoRoot 'src\WhispUI'
$Csproj     = Join-Path $ProjectDir 'WhispUI.csproj'
$PublishDir = Join-Path $RepoRoot 'publish'

# =============================================================================
# MSBuild configuration
# -----------------------------------------------------------------------------
# `dotnet build` is broken on WhispUI due to the XamlCompiler MSB3073 bug,
# so we must use the Visual Studio MSBuild Framework (MSBuildRuntimeType=Full).
#
# Resolution: -MsBuild > $env:WHISPUI_MSBUILD > vswhere > error.
# Set once with: setx WHISPUI_MSBUILD "<path\to\MSBuild.exe>"
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

# 1. Kill running instance (otherwise binaries are locked)
Get-Process -Name WhispUI -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Killing WhispUI PID $($_.Id)" -ForegroundColor Yellow
    $_ | Stop-Process -Force
}

# 2. Clean previous output
if (Test-Path $PublishDir) {
    Write-Host "Cleaning $PublishDir" -ForegroundColor DarkGray
    Remove-Item $PublishDir -Recurse -Force
}

# 3. Publish via VS MSBuild (Restore + Publish, Framework target)
Write-Host "Publish ($Configuration x64) -> $PublishDir" -ForegroundColor Cyan
& $MsBuildExe $Csproj `
    '-t:Restore;Publish' `
    "-p:Configuration=$Configuration" `
    '-p:Platform=x64' `
    "-p:PublishDir=$PublishDir\" `
    '-v:m' '-nologo'
if ($LASTEXITCODE -ne 0) { throw "MSBuild Publish failed (code $LASTEXITCODE)" }

Write-Host "OK: $PublishDir" -ForegroundColor Green

# Post-publish summary. Release bundles managed assemblies into WhispUI.exe
# (PublishSingleFile=true in the csproj). Native DLLs and Whisper models are
# NOT shipped here — the first-run wizard downloads them into <UserDataRoot>
# on first launch. Expect a much smaller artifact than pre-restructure
# (~50-100 MB instead of ~250 MB), top-level files limited to the exe + the
# WinAppSDK runtime + .pri + Assets.
$exe = Join-Path $PublishDir 'WhispUI.exe'
if (Test-Path $exe) {
    $exeSize = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    $total   = [math]::Round(((Get-ChildItem $PublishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
    $files   = (Get-ChildItem $PublishDir -File).Count
    Write-Host ("  WhispUI.exe: {0} MB | dir total: {1} MB | top-level files: {2}" -f $exeSize, $total, $files) -ForegroundColor DarkGray
}

if ($Open) { Start-Process explorer.exe $PublishDir }

# Launch the published exe so the publish-run loop matches build-run.ps1.
# Skip with -NoRun when producing a release artifact without testing it.
if (-not $NoRun) {
    if (-not (Test-Path $exe)) { throw "Exe not found after publish: $exe" }
    Write-Host "Run $exe" -ForegroundColor Green
    $proc = Start-Process -FilePath $exe -PassThru
    if ($Wait) { $proc.WaitForExit() }
}
