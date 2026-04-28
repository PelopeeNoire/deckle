# setup-userdata.ps1
#
# Peuple le dossier UserData de l'app à partir des artefacts du repo —
# DLLs natives + modèles. Sert de "first-run wizard manuel" tant que le
# vrai wizard WinUI n'est pas en place : l'app retrouve tout sous le
# canonical %LOCALAPPDATA%\WhispUI\ et démarre sans walk-up dev.
#
# Cible par défaut : %LOCALAPPDATA%\WhispUI\
# Override         : -DataRoot, ou env var WHISP_DATA_ROOT
#
# Sources attendues (lancer restore-assets.ps1 d'abord si manquantes) :
#   <repo>\native\whisper\*.dll
#   <repo>\native\mingw\*.dll
#   <repo>\models\*.bin
#
# Idempotent : skip les fichiers déjà présents avec la même taille.

[CmdletBinding()]
param(
    # Override programmable du root cible. Priorité la plus haute, devant
    # WHISP_DATA_ROOT, devant %LOCALAPPDATA%\WhispUI\.
    [string]$DataRoot
)

$ErrorActionPreference = 'Stop'

$Repo        = Split-Path -Parent $PSScriptRoot
$RepoWhisper = Join-Path $Repo 'native\whisper'
$RepoMingw   = Join-Path $Repo 'native\mingw'
$RepoModels  = Join-Path $Repo 'models'

# Résolution du root cible — même ordre que AppPaths.ResolveUserDataRoot
# côté C# (param > env > default %LOCALAPPDATA%).
if ($DataRoot)               { $TargetRoot = $DataRoot }
elseif ($env:WHISP_DATA_ROOT) { $TargetRoot = $env:WHISP_DATA_ROOT }
else                          { $TargetRoot = Join-Path $env:LOCALAPPDATA 'WhispUI' }

$TargetNative    = Join-Path $TargetRoot 'native'
$TargetModels    = Join-Path $TargetRoot 'models'
$TargetSettings  = Join-Path $TargetRoot 'settings'
$TargetTelemetry = Join-Path $TargetRoot 'telemetry'

function Step($msg) { Write-Host "`n[setup] $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "         $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "         $msg" -ForegroundColor Yellow }

Step "target root: $TargetRoot"

# 1. Vérifier que les sources existent. Si le repo n'a pas encore native/
# et models/ peuplés, restore-assets.ps1 est le préalable canonique.
$missingSources = @()
if (-not (Test-Path $RepoWhisper)) { $missingSources += $RepoWhisper }
if (-not (Test-Path $RepoMingw))   { $missingSources += $RepoMingw }
if (-not (Test-Path $RepoModels))  { $missingSources += $RepoModels }
if ($missingSources.Count -gt 0) {
    Warn 'missing source folders:'
    foreach ($m in $missingSources) { Warn "  $m" }
    throw 'Run scripts\restore-assets.ps1 first to populate native/ and models/.'
}

# 2. Créer la racine et les sous-dossiers attendus par AppPaths côté code.
# settings/ et telemetry/ sont créés vides — l'app les peuplera au runtime.
Step 'create target folders'
foreach ($d in @($TargetNative, $TargetModels, $TargetSettings, $TargetTelemetry)) {
    if (-not (Test-Path $d)) {
        New-Item -ItemType Directory -Path $d -Force | Out-Null
        Ok "created $d"
    } else {
        Ok "exists  $d"
    }
}

# Copy idempotente — pattern restore-assets : skip si même taille déjà en
# place. Pas un hash check (coûteux sur 3 GB), suffisant pour du dev.
function CopyIdempotent($src, $dst) {
    $name = Split-Path $src -Leaf
    if ((Test-Path $dst) -and ((Get-Item $dst).Length -eq (Get-Item $src).Length)) {
        Ok "skip  $name (same size)"
        return
    }
    Copy-Item $src $dst -Force
    $size = [math]::Round((Get-Item $dst).Length / 1MB, 1)
    Ok "copy  $name ($size MB)"
}

# 3. Natives — fusion whisper/ + mingw/ dans un seul UserData\native\.
# C'est ce que NativeMethods.ResolveNativeLibrary attend : un dossier
# unique qui contient libwhisper.dll + ses ggml-*.dll + le runtime MinGW.
Step "copy native DLLs to $TargetNative"
foreach ($dll in (Get-ChildItem $RepoWhisper -Filter '*.dll')) {
    CopyIdempotent $dll.FullName (Join-Path $TargetNative $dll.Name)
}
foreach ($dll in (Get-ChildItem $RepoMingw -Filter '*.dll')) {
    CopyIdempotent $dll.FullName (Join-Path $TargetNative $dll.Name)
}

# 4. Modèles — copie tous les .bin du repo vers UserData\models\.
# WhispEngine pointe sur ggml-large-v3.bin par défaut ; les autres .bin
# (base, silero) sont copiés aussi pour permettre le swap depuis Settings.
Step "copy models to $TargetModels"
foreach ($bin in (Get-ChildItem $RepoModels -Filter '*.bin')) {
    CopyIdempotent $bin.FullName (Join-Path $TargetModels $bin.Name)
}

Step 'done'
$nativeCount = (Get-ChildItem $TargetNative -File | Measure-Object).Count
$modelCount  = (Get-ChildItem $TargetModels -File | Measure-Object).Count
Write-Host "         native\  : $nativeCount file(s)"
Write-Host "         models\  : $modelCount file(s)"
Write-Host "`nNext : build + run the app — it should find everything under $TargetRoot" -ForegroundColor Cyan
