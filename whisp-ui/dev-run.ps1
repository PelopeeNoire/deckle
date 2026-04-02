# dev-run.ps1 — Kill > Build > Run (test local)
# Lance la version de build pour tester une modification.
# Quand les tests sont OK : lancer dev-publish.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# 1. Tuer l'instance en cours (build ou publish), silencieux si absente
Write-Host ">> Kill..." -ForegroundColor Cyan
taskkill /F /IM WhispInteropTest.exe 2>$null
Start-Sleep -Milliseconds 300   # laisser le process se terminer proprement

# 2. Build
Write-Host ">> Build..." -ForegroundColor Cyan
Push-Location "$PSScriptRoot\WhispInteropTest"
dotnet build -c Release
$buildResult = $LASTEXITCODE
Pop-Location

if ($buildResult -ne 0) {
    Write-Host "Build échoué — abandon." -ForegroundColor Red
    exit 1
}

# 3. Lancer l'exe de build en arrière-plan
$exe = "$PSScriptRoot\WhispInteropTest\bin\Release\net10.0-windows\WhispInteropTest.exe"
Write-Host ">> Lancement : $exe" -ForegroundColor Cyan
Start-Process $exe

Write-Host "Whisp lancé (tray). Teste, puis lance dev-publish.ps1 quand c'est OK." -ForegroundColor Green
