[CmdletBinding()]
param(
    [switch]$Restore,
    [switch]$NoRun,
    [switch]$Wait,
    [ValidateSet('Release','Debug')]
    [string]$Configuration = 'Release',
    # Explicit path to MSBuild.exe (takes priority over env + vswhere).
    [string]$MsBuild
)

$ErrorActionPreference = 'Stop'
$ScriptDir  = $PSScriptRoot                                  # scripts/
$RepoRoot   = Split-Path $ScriptDir                          # repo root
$ProjectDir = Join-Path $RepoRoot 'src\WhispUI'
$Csproj     = Join-Path $ProjectDir 'WhispUI.csproj'
$ExePath    = Join-Path $ProjectDir "bin\x64\$Configuration\net10.0-windows10.0.19041.0\WhispUI.exe"

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
$targets = if ($Restore) { 'Restore;Build' } else { 'Build' }
Write-Host "Build ($targets, $Configuration x64)..." -ForegroundColor Cyan
& $MsBuildExe $Csproj "-t:$targets" "-p:Configuration=$Configuration" '-p:Platform=x64' '-v:m' '-nologo'
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed (code $LASTEXITCODE)" }

# 3. Run
if ($NoRun) { return }
if (-not (Test-Path $ExePath)) { throw "Exe not found: $ExePath" }
Write-Host "Run $ExePath" -ForegroundColor Green
$proc = Start-Process -FilePath $ExePath -PassThru
if ($Wait) { $proc.WaitForExit() }
