# Benchmark — Optimisation des prompts de réécriture

Système de test automatisé pour les system prompts utilisés par WhispUI avec Ollama.

## Structure

```
benchmark/
├── config.ini          ← CONFIGURER ICI (modèle, profil, température)
├── corpus.json         ← SAMPLES ICI (ajouter des transcriptions à tester)
├── system_prompt.txt   ← PROMPT À TESTER (lu par le benchmark, écrit par autoresearch)
├── benchmark.py        ⚙ moteur de scoring (ne pas modifier sauf refonte)
├── autoresearch.py     ⚙ boucle d'optimisation (ne pas modifier sauf refonte)
├── launch.ps1          ⚙ launcher console interactif
└── README.md
```

Les artefacts de run (`results.tsv`, `*.log`, `last_report.json`, `autoresearch_report.txt`) sont dans le `.gitignore` — ils sont régénérés à chaque lancement.

## Workflow

**Lancer un benchmark simple** (tester le prompt actuel) :

```powershell
cd benchmark
python benchmark.py --verbose
```

**Lancer l'autoresearch** (optimisation automatique du prompt) :

```powershell
python autoresearch.py
```

Les paramètres par défaut viennent de `config.ini`. On peut les override en ligne de commande :

```powershell
python autoresearch.py --max-experiments 5 --runs-per-experiment 2
```

## Que configurer

**`config.ini`** — tous les paramètres du benchmark et de l'autoresearch :
- `profile` : nom du profil WhispUI testé (nettoyage, restructuration, prompt)
- `model` : modèle Ollama cible (celui qui reçoit le prompt)
- `judge_model` : modèle Ollama juge (celui qui évalue la qualité)
- `temperature`, `num_ctx_k` : paramètres de génération
- `designer_model` : modèle qui génère les variantes dans l'autoresearch
- `max_experiments`, `runs_per_experiment` : budget d'expérimentation

**`corpus.json`** — les transcriptions de test. Chaque sample :
```json
{"id": 11, "duration_sec": 90, "profile": "nettoyage", "text": "..."}
```
Les IDs doivent être uniques et croissants. Ajouter à la suite des existants.

**`system_prompt.txt`** — le prompt à tester. Deux usages :
- En mode benchmark simple : le mettre à la main, lancer `benchmark.py`
- En mode autoresearch : il est écrasé automatiquement par chaque expérience. Le meilleur prompt trouvé reste dans ce fichier à la fin.

## Que ne pas toucher

`benchmark.py` et `autoresearch.py` sont le moteur. Pas besoin d'y toucher pour changer de profil ou de modèle — tout passe par `config.ini`.

## Résultats

Après un autoresearch, les résultats sont dans :
- `results.tsv` : tableau de toutes les expériences (score, keep/discard)
- `autoresearch_report.txt` : rapport lisible avec les prompts testés
- `last_report.json` : détail du dernier run par sample (pour debug)
- `system_prompt.txt` : le meilleur prompt trouvé (prêt à copier dans AppSettings.cs)
