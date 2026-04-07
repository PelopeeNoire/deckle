[CmdletBinding()]
param(
    [switch]$Restore,
    [switch]$NoRun,
    [switch]$Wait,
    [ValidateSet('Release','Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$ProjectDir = $PSScriptRoot
$Csproj     = Join-Path $ProjectDir 'WhispUI.csproj'
$MsBuild    = 'D:\bin\visual-studio\visual-studio-2026\MSBuild\Current\Bin\amd64\MSBuild.exe'
$ExePath    = Join-Path $ProjectDir "bin\x64\$Configuration\net10.0-windows10.0.19041.0\WhispUI.exe"

# 1. Tuer l'instance en cours (sinon le .exe est verrouille)
Get-Process -Name WhispUI -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Killing WhispUI PID $($_.Id)" -ForegroundColor Yellow
    $_ | Stop-Process -Force
}

# 2. Build via MSBuild VS 2026 (bug XamlCompiler MSB3073 interdit dotnet build CLI)
$targets = if ($Restore) { 'Restore;Build' } else { 'Build' }
Write-Host "Build ($targets, $Configuration x64)..." -ForegroundColor Cyan
& $MsBuild $Csproj "-t:$targets" "-p:Configuration=$Configuration" '-p:Platform=x64' '-v:m' '-nologo'
if ($LASTEXITCODE -ne 0) { throw "MSBuild a echoue (code $LASTEXITCODE)" }

# 3. Lancer
if ($NoRun) { return }
if (-not (Test-Path $ExePath)) { throw "Exe introuvable : $ExePath" }
Write-Host "Run $ExePath" -ForegroundColor Green
$proc = Start-Process -FilePath $ExePath -PassThru
if ($Wait) { $proc.WaitForExit() }
