<#
.SYNOPSIS
    Launcher interactif pour les benchmarks de prompt nettoyage.
.DESCRIPTION
    Menu console pour lancer autoresearch, un benchmark isolé, ou consulter les résultats.
    Revient au menu après chaque action. Ctrl+C pour quitter.
#>

$ErrorActionPreference = "Stop"
$BenchmarkDir = $PSScriptRoot

function Show-Header {
    Clear-Host
    Write-Host ""
    Write-Host "  ============================================" -ForegroundColor Cyan
    Write-Host "   BENCHMARK — Prompt Nettoyage Transcription" -ForegroundColor Cyan
    Write-Host "  ============================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  Dossier : $BenchmarkDir" -ForegroundColor DarkGray
    Write-Host ""
}

function Show-Status {
    # Prompt actuel
    $promptFile = Join-Path $BenchmarkDir "config/prompts/system_prompt.txt"
    if (Test-Path $promptFile) {
        $prompt = Get-Content $promptFile -Raw -Encoding UTF8
        $words = ($prompt -split '\s+').Count
        $chars = $prompt.Length
        Write-Host "  Prompt actuel : $words mots, $chars chars" -ForegroundColor DarkGray
    }

    # Derniers résultats
    $resultsFile = Join-Path $BenchmarkDir "data/reports/results.tsv"
    if (Test-Path $resultsFile) {
        $lines = Get-Content $resultsFile -Encoding UTF8 | Where-Object { $_ -and $_ -notmatch "^experiment" }
        if ($lines.Count -gt 0) {
            Write-Host "  Résultats : $($lines.Count) expérience(s) enregistrée(s)" -ForegroundColor DarkGray
        }
    }

    # Git
    $branch = git -C $BenchmarkDir branch --show-current 2>$null
    if ($branch) {
        Write-Host "  Branche : $branch" -ForegroundColor DarkGray
    }

    Write-Host ""
}

function Show-Menu {
    Write-Host "  Actions disponibles :" -ForegroundColor White
    Write-Host ""
    Write-Host "    [1] Autoresearch complet (10 exp, 3 runs)" -ForegroundColor Yellow
    Write-Host "    [2] Autoresearch léger   (5 exp, 2 runs)" -ForegroundColor Yellow
    Write-Host "    [3] Benchmark seul       (1 run, prompt actuel)" -ForegroundColor Yellow
    Write-Host "    [4] Benchmark x3         (3 runs, prompt actuel)" -ForegroundColor Yellow
    Write-Host "    [5] Voir les résultats   (results.tsv)" -ForegroundColor Green
    Write-Host "    [6] Voir le rapport      (autoresearch_report.txt)" -ForegroundColor Green
    Write-Host "    [7] Voir le prompt actuel" -ForegroundColor Green
    Write-Host "    [Q] Quitter" -ForegroundColor DarkGray
    Write-Host ""
}

function Run-Command {
    param([string]$Description, [string[]]$Args)

    Write-Host ""
    Write-Host "  --- $Description ---" -ForegroundColor Cyan
    Write-Host "  Commande : python $($Args -join ' ')" -ForegroundColor DarkGray
    Write-Host ""

    $startTime = Get-Date

    try {
        & python @Args
        $exitCode = $LASTEXITCODE
    }
    catch {
        Write-Host "  ERREUR : $_" -ForegroundColor Red
        $exitCode = 1
    }

    $elapsed = (Get-Date) - $startTime
    Write-Host ""

    if ($exitCode -eq 0) {
        Write-Host "  Terminé en $($elapsed.ToString('hh\:mm\:ss'))" -ForegroundColor Green
    }
    else {
        Write-Host "  Terminé avec erreur (exit code $exitCode) en $($elapsed.ToString('hh\:mm\:ss'))" -ForegroundColor Red
    }
}

function Show-FileContent {
    param([string]$FileName, [string]$Label)

    $path = Join-Path $BenchmarkDir $FileName
    if (Test-Path $path) {
        Write-Host ""
        Write-Host "  --- $Label ---" -ForegroundColor Cyan
        Write-Host ""
        Get-Content $path -Encoding UTF8 | ForEach-Object { Write-Host "  $_" }
    }
    else {
        Write-Host ""
        Write-Host "  Fichier introuvable : $FileName" -ForegroundColor Red
    }
}

# ─── Boucle principale ──────────────────────────────────────────────────────

Push-Location $BenchmarkDir

try {
    while ($true) {
        Show-Header
        Show-Status
        Show-Menu

        $choice = Read-Host "  Choix"

        switch ($choice.Trim().ToUpper()) {
            "1" {
                Run-Command -Description "Autoresearch complet (10 exp, 3 runs)" `
                            -Args @("autoresearch.py", "--max-experiments", "10", "--runs-per-experiment", "3")
            }
            "2" {
                Run-Command -Description "Autoresearch léger (5 exp, 2 runs)" `
                            -Args @("autoresearch.py", "--max-experiments", "5", "--runs-per-experiment", "2")
            }
            "3" {
                Run-Command -Description "Benchmark seul (1 run)" `
                            -Args @("benchmark.py", "--verbose")
            }
            "4" {
                Run-Command -Description "Benchmark x3 (3 runs, prompt actuel)" `
                            -Args @("benchmark.py", "--verbose")
                Run-Command -Description "Benchmark run 2/3" -Args @("benchmark.py")
                Run-Command -Description "Benchmark run 3/3" -Args @("benchmark.py")
            }
            "5" {
                Show-FileContent -FileName "data/reports/results.tsv" -Label "Résultats (data/reports/results.tsv)"
            }
            "6" {
                Show-FileContent -FileName "data/reports/autoresearch_report.txt" -Label "Rapport Autoresearch"
            }
            "7" {
                Show-FileContent -FileName "config/prompts/system_prompt.txt" -Label "Prompt actuel (config/prompts/system_prompt.txt)"
            }
            "Q" {
                Write-Host ""
                Write-Host "  Au revoir." -ForegroundColor Cyan
                Write-Host ""
                break
            }
            default {
                Write-Host "  Choix invalide." -ForegroundColor Red
            }
        }

        if ($choice.Trim().ToUpper() -ne "Q") {
            Write-Host ""
            Write-Host "  Appuie sur Entrée pour revenir au menu..." -ForegroundColor DarkGray
            Read-Host | Out-Null
        }
    }
}
finally {
    Pop-Location
}
