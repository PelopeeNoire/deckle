# dev-publish.ps1 — Kill > Publish (mise en production)
# À lancer quand les tests sont validés.
# Écrase whisp-ui/publish/ avec la version fraîche.
# La tâche planifiée "Whisp" pointe sur ce dossier — relancer manuellement si besoin.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# 1. Tuer l'instance de test en cours, silencieux si absente
Write-Host ">> Kill..." -ForegroundColor Cyan
taskkill /F /IM WhispInteropTest.exe 2>$null
Start-Sleep -Milliseconds 300

# 2. Publish dans ../publish/ (relatif au projet, soit whisp-ui/publish/)
Write-Host ">> Publish..." -ForegroundColor Cyan
Push-Location "$PSScriptRoot\WhispInteropTest"
dotnet publish -c Release -o ../publish/
$publishResult = $LASTEXITCODE
Pop-Location

if ($publishResult -ne 0) {
    Write-Host "Publish échoué." -ForegroundColor Red
    exit 1
}

Write-Host "Publié dans whisp-ui/publish/. Relance la tâche planifiée Whisp si besoin." -ForegroundColor Green
