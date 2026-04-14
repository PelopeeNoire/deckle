# Benchmark — Optimisation des prompts de réécriture

Système de test automatisé pour les system prompts utilisés par WhispUI avec Ollama.

## Structure

```
benchmark/
├── benchmark.py               moteur de scoring
├── autoresearch.py            boucle d'optimisation autonome
├── build_corpus.py            construction corpus depuis raw_*.txt
├── fix_encoding.py            utilitaire encodage
├── launch.ps1                 launcher console interactif
├── config/
│   └── config.ini             modèle, profil, température, chemins
├── data/
│   ├── corpus.json            samples de test
│   ├── raw_prompts.txt        source pour build_corpus (profil Prompt)
│   └── raw_restructuration.txt
├── prompts/
│   ├── system_prompt.txt      prompt à tester (lu par benchmark, écrit par autoresearch)
│   └── system_prompt_initial.txt
├── reports/                   artefacts de run (gitignorés)
│   ├── last_report.json       détail du dernier run par sample
│   ├── results.tsv            historique des expériences
│   └── autoresearch_report.txt
├── journals/                  journaux de boucle et prompts de cadrage
│   ├── journal.md                     Nettoyage
│   ├── journal_restructuration.md     Restructuration
│   ├── loop_prompt.md                 prompt de boucle Nettoyage
│   ├── loop_prompt_restructuration.md prompt de boucle Restructuration
│   └── program.md
├── logs/                      logs bruts (gitignorés)
└── archive/                   backups et snapshots (tsv/txt/json)
```

## Workflow

**Benchmark simple** (tester le prompt actuel) :

```powershell
cd benchmark
python benchmark.py --verbose
```

**Autoresearch** (optimisation automatique) :

```powershell
python autoresearch.py
```

Paramètres par défaut dans `config/config.ini`. Override possible en ligne de commande :

```powershell
python autoresearch.py --max-experiments 5 --runs-per-experiment 2
```

**Boucle d'optimisation assistée** — voir skill `benchmark-loop` (`.claude/skills/benchmark-loop/SKILL.md`). Invocation : `/benchmark-loop <profil>` avec profil ∈ {nettoyage, restructuration, prompt}.

## Configuration

`config/config.ini` :
- `profile` — nom du profil WhispUI testé (nettoyage, restructuration, prompt)
- `model` — modèle Ollama cible
- `judge_model` — modèle Ollama juge
- `temperature`, `num_ctx_k` — paramètres de génération
- `corpus`, `prompt` — chemins relatifs (par défaut `data/corpus.json`, `prompts/system_prompt.txt`)
- `designer_model`, `max_experiments`, `runs_per_experiment` — autoresearch

`data/corpus.json` — transcriptions de test, IDs uniques et croissants.

`prompts/system_prompt.txt` — prompt à tester. En autoresearch, il est écrasé à chaque expérience ; le meilleur reste à la fin.

## Ne pas toucher sans raison

`benchmark.py`, `autoresearch.py`, `build_corpus.py` : moteur. Changer de profil ou de modèle passe par `config.ini`.
