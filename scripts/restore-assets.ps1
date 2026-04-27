# restore-assets.ps1
#
# Reconstruit les dossiers `native/whisper/`, `native/mingw/` et `models/`
# qui étaient derrière des symlinks shootés lors du cleanup des worktrees.
#
# - DLLs whisper.cpp       : copiées depuis whisper.cpp\build\bin\
# - DLLs MinGW runtime     : copiées depuis le scoop MinGW
# - Modèles Whisper        : téléchargés depuis HuggingFace
# - Silero VAD             : téléchargé depuis HuggingFace
#
# Idempotent : saute ce qui est déjà présent avec la bonne taille.

$ErrorActionPreference = 'Stop'

$Repo        = Split-Path -Parent $PSScriptRoot
$NativeDir   = Join-Path $Repo 'native'
$WhisperDir  = Join-Path $NativeDir 'whisper'
$MingwDir    = Join-Path $NativeDir 'mingw'
$ModelsDir   = Join-Path $Repo 'models'
$WhisperBin  = Join-Path $Repo 'whisper.cpp\build\bin'
# Scoop respects the SCOOP env var when installed in a non-default location;
# fall back to the per-user default %USERPROFILE%\scoop otherwise.
$ScoopRoot   = if ($env:SCOOP) { $env:SCOOP } else { Join-Path $env:USERPROFILE 'scoop' }
$ScoopMingw  = Join-Path $ScoopRoot 'apps\mingw\current\bin'

function Step($msg) { Write-Host "`n[restore] $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "         $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "         $msg" -ForegroundColor Yellow }

Step 'create folders'
foreach ($d in @($WhisperDir, $MingwDir, $ModelsDir)) {
    if (-not (Test-Path $d)) {
        New-Item -ItemType Directory -Path $d -Force | Out-Null
        Ok "created $d"
    } else {
        Ok "exists  $d"
    }
}

Step 'copy whisper.cpp DLLs'
$whisperDlls = @(
    'libwhisper.dll', 'ggml.dll', 'ggml-base.dll',
    'ggml-cpu.dll',   'ggml-vulkan.dll'
)
foreach ($name in $whisperDlls) {
    $src = Join-Path $WhisperBin $name
    $dst = Join-Path $WhisperDir $name
    if (-not (Test-Path $src)) {
        Warn "MISSING source $src — rebuild whisper.cpp needed"
        continue
    }
    Copy-Item $src $dst -Force
    Ok "$name"
}

Step 'copy MinGW runtime DLLs'
$mingwDlls = @(
    'libgcc_s_seh-1.dll', 'libstdc++-6.dll', 'libwinpthread-1.dll'
)
foreach ($name in $mingwDlls) {
    $src = Join-Path $ScoopMingw $name
    $dst = Join-Path $MingwDir $name
    if (-not (Test-Path $src)) {
        Warn "MISSING source $src"
        continue
    }
    Copy-Item $src $dst -Force
    Ok "$name"
}

# Download helper — curl.exe (intégré à Win10/11, bien plus rapide
# qu'Invoke-WebRequest sur gros fichiers). Barre de progression native.
function Download($url, $dst, $expectedMinBytes) {
    if ((Test-Path $dst) -and ((Get-Item $dst).Length -ge $expectedMinBytes)) {
        Ok "already present $(Split-Path $dst -Leaf) ($([math]::Round((Get-Item $dst).Length/1MB,1)) MB)"
        return
    }
    Write-Host "         downloading $(Split-Path $dst -Leaf) ..."
    & curl.exe -L --fail --retry 3 --progress-bar -o $dst $url
    if ($LASTEXITCODE -ne 0) { throw "curl failed for $url" }
    Ok "downloaded $(Split-Path $dst -Leaf) ($([math]::Round((Get-Item $dst).Length/1MB,1)) MB)"
}

Step 'download Whisper models'
Download `
    'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin' `
    (Join-Path $ModelsDir 'ggml-base.bin') `
    130MB

Download `
    'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin' `
    (Join-Path $ModelsDir 'ggml-large-v3.bin') `
    3000MB

Step 'download Silero VAD'
Download `
    'https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v5.1.2.bin' `
    (Join-Path $ModelsDir 'ggml-silero-v5.1.2.bin') `
    700KB

Download `
    'https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v6.2.0.bin' `
    (Join-Path $ModelsDir 'ggml-silero-v6.2.0.bin') `
    700KB

Step 'done'
Write-Host "         native/whisper/ : $(Get-ChildItem $WhisperDir | Measure-Object).Count DLL(s)"
Write-Host "         native/mingw/   : $(Get-ChildItem $MingwDir | Measure-Object).Count DLL(s)"
Write-Host "         models/         : $(Get-ChildItem $ModelsDir | Measure-Object).Count file(s)"
Write-Host "`nNext : scripts\publish.ps1" -ForegroundColor Cyan
