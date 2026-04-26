---
name: benchmark-loop
description: Boucle d'optimisation itérative d'un prompt de réécriture par bracket de cleanup (Ministral 14B local + agent juge — pas de LLM judge dans le code). Orchestre l'itération prompt → benchmark → diagnostic → nouveau prompt → commit, en pilotant la continuité via ScheduleWakeup (batchs de 2-3 itérations par turn). Utiliser quand Louis invoque `/benchmark-loop <bracket>` avec bracket ∈ {relecture, lissage, affinage, arrangement, whisper}.
allowed-tools: Read, Write, Edit, Bash, Grep, Glob, ScheduleWakeup
---

# Benchmark-loop — optimisation itérative d'un prompt par bracket

## Périmètre

Une seule chose à la fois : améliorer itérativement le prompt cible pour le bracket choisi, en observant les outputs (rule-based metrics + lecture qualitative) et en capitalisant chaque itération dans le journal `benchmark/results.tsv`.

Invocation : `/benchmark-loop <bracket>` où bracket ∈ {`relecture`, `lissage`, `affinage`, `arrangement`, `whisper`}.

Résolution des fichiers en fonction du bracket :

| Cible | Chemin |
|---|---|
| Prompt à optimiser (bracket LLM) | `benchmark/config/prompts/<bracket>_system_prompt.txt` |
| Prompt à optimiser (Whisper) | `benchmark/config/prompts/whisper_initial_prompt.txt` |
| Corpus utilisé par le bench | `benchmark/telemetry/corpus-<bracket>/corpus.jsonl` |
| Bench LLM | `python rewrite_bench.py --bracket <bracket> --verbose` |
| Bench Whisper | `python whisper_bench.py --bracket <bracket> --verbose` |
| Dernier rapport LLM | `benchmark/reports/last_rewrite_run.{json,txt}` |
| Dernier rapport Whisper | `benchmark/reports/last_whisper_run.{json,txt}` |
| Journal d'itérations (toutes brackets) | `benchmark/results.tsv` |
| Convention bench / utility | `benchmark/AGENT.md` |
| Plan de la nuit (cadre conceptuel) | `C:\Users\Louis\.claude\plans\reprise-autoresearch-sur-vectorized-tulip.md` |

**Avant la première itération** : lire le plan ci-dessus en entier, le `results.tsv` (trajectoire passée, dernier état), et le prompt courant du bracket.

## Règle absolue : pas de LLM judge

Le benchmark tourne **toujours** avec `--skip-judge` (défaut de `rewrite_bench.py`). Aucun appel API Anthropic, aucun appel à un Ollama judge. **C'est l'agent (la session Claude Code en cours) qui juge** en lisant les outputs et en scorant qualitativement contre la grille.

Si tu vois `--judge claude` ou un import `anthropic` apparaître dans une commande, refuse — ce n'est pas le pattern.

## Règles immuables

- Ne modifier QU'UN seul fichier prompt par itération : celui du bracket courant. Le journal `results.tsv` se met à jour en append, pas en édition.
- `config/config.ini` n'est touché que sur instruction explicite de Louis.
- Ne jamais arrêter la boucle de sa propre initiative (cf. critères d'arrêt).
- En cas de régression franche (qualité humaine + métriques pires sur 2 itérations successives), revenir au meilleur prompt connu via `git show <sha>:benchmark/config/prompts/<bracket>_system_prompt.txt > /tmp/champion.txt && cp /tmp/champion.txt benchmark/config/prompts/<bracket>_system_prompt.txt` et changer d'angle.
- Ne jamais lancer `build-run.ps1`, `MSBuild.exe`, ou tout build WhispUI. Ce skill ne touche pas au code de l'app — uniquement au dossier `benchmark/`.
- **Pas de nouveaux fichiers Python.** L'outillage (`rewrite_bench.py`, `whisper_bench.py`, `refresh_corpus.py`, `segment_corpus.py`, `compare_runs.py`) suffit. Si un besoin réel émerge, en parler à Louis avant — pas pendant la boucle.

## Commande benchmark

Toujours depuis le dossier `benchmark/`, timeout 900s pour bracket court (relecture / lissage), 1500s pour long (affinage / arrangement / whisper) :

```bash
cd D:/projects/ai/transcription/benchmark && python rewrite_bench.py --bracket <bracket> --verbose 2>&1 | tee logs/iter_<bracket>_<N>.log
```

Pour Whisper :

```bash
cd D:/projects/ai/transcription/benchmark && python whisper_bench.py --bracket <bracket> --verbose 2>&1 | tee logs/iter_whisper_<N>.log
```

Sortie LLM : `reports/last_rewrite_run.json` + `reports/last_rewrite_run.txt`. Le JSON a la structure :

```
results[i] avec clés : bracket, prompt_file, samples, catastrophes, composite_median, details
results[i].details[j] avec clés : id, duration_sec, input_chars, output_chars, length_ratio, rule {novel_words, lists, preamble, length_ratio}, catastrophe, output_text, elapsed_sec
```

Le `SCORE=X.XXXX` que `benchmark.run()` print à la fin est la médiane des composites — sans judge, c'est le ratio des catastrophes (1.0 par catastrophe, None sinon) → effectivement une mesure binaire sample-par-sample. Ne pas s'y fier seul. **C'est la lecture qualitative qui domine.**

## Protocole par itération

Pour chaque itération :

1. **Lancer le benchmark** (commande ci-dessus).
2. **Lire le rapport TXT en entier** (`reports/last_rewrite_run.txt`) puis 3-5 `output_text` complets dans le JSON. Alterner les samples lus entre itérations pour couvrir toute la diversité du corpus.
3. **Évaluer qualitativement** dans cet ordre, en fonction du bracket :
   - **Relecture** : aucun mot ajouté ni retiré, accents et ponctuation corrects, registre intact, ZÉRO mise en forme (pas de `**gras**`).
   - **Lissage / Affinage** : sortie ressemble à *« un discours préparé dans la tête »* — vraies phrases d'écrit, pas de hésitations / rebondissements / réajustements oraux ; nuances, alternatives rejetées, auto-corrections de pensée préservées ; ordre du locuteur strict ; pas de regroupement thématique.
   - **Arrangement** : tout ce qui précède + regroupement thématique des mentions éparpillées ; voix première personne stricte (jamais « le locuteur »).
   - **Whisper** : transcription fidèle au mot près, accents circonflexes corrects (« même », « quand même »), pas de mots inventés (« fichu » et pas « figue »), pas d'espaces en trop.
4. **Noter les patterns d'échec dominants** avec exemples courts (1-2 phrases extraites du sample) — c'est ce qui guidera la prochaine modification.
5. **Écrire la modification ciblée** dans `<bracket>_system_prompt.txt` — UNE seule modification par itération, pas une refonte. Garder le prompt sous 600 mots, FR. Varier les angles (anti-pattern explicite, few-shot court, contrainte chiffrée, reformulation de la règle abstraite).
6. **Append une ligne dans `results.tsv`** avec colonnes : `experiment\tcommit\tbracket\tmetric\tstatus\tdescription`. `metric` = score qualitatif sur 1.0 que tu attribues. `status` ∈ {`baseline`, `keep`, `discard`, `pass<N>-champion`, `champion`}. `description` = quelques mots concrets sur ce que tu as changé et le résultat observé.
7. **Committer** :
   ```bash
   git add benchmark/config/prompts/<bracket>_system_prompt.txt benchmark/results.tsv
   git commit -m "bench-rewrite(<bracket>): iter N — <change concis>"
   ```
8. **Décision** : si la nouvelle itération est meilleure que l'incumbent → keep (l'itération devient l'incumbent). Sinon → `git reset --hard HEAD~1` et noter `discard` dans `results.tsv` (ligne séparée, qui décrit l'angle testé sans incrémenter l'incumbent).

## Continuité via ScheduleWakeup — RÈGLE CENTRALE

**Ne PAS enchaîner les 30+ itérations dans un seul turn.** L'enchaînement épuise le contexte et bloque la reprise.

Protocole :

- Faire **2-3 itérations par turn**, puis appeler `ScheduleWakeup`.
- `delaySeconds` entre **60 et 180** — on reste ainsi dans le cache prompt (TTL 5 min = 300s), les turns suivants sont rapides et peu coûteux.
- `prompt` passé à ScheduleWakeup = la commande `/benchmark-loop <bracket>` **verbatim**, pour ré-entrer dans ce skill au tick suivant.
- `reason` = `iter N/<cap>, <axe testé dans le batch>` — sera affiché à Louis.
- Ordre de grandeur : **~10-15 turns pour 30 itérations**.
- La continuité est assurée par les commits git et le journal `results.tsv` — Louis peut interrompre à tout moment, on reprend là où on s'est arrêté en lisant `results.tsv` au prochain tick.

**Au début de chaque turn** : lire `results.tsv` filtré sur le bracket courant pour savoir où en est la boucle (numéro de la dernière itération, état keep/discard, commits en place).

## Critères d'arrêt

Arrêter uniquement si **un** de ces critères est atteint :

- **Cap d'itérations atteint** : 30 par défaut pour le bracket prioritaire (généralement lissage), 10-15 pour les autres. Si Louis précise un cap explicite au lancement (`/benchmark-loop lissage 40`), suivre.
- **Stagnation qualitative** : 5 itérations consécutives sans amélioration humaine ET sans régression métrique. Pas seulement la métrique — c'est l'observation qualitative qui tranche.
- **Régression métrique brutale** : si tu vois apparaître des `catastrophe: true` qui n'existaient pas, des `length_ratio` < 0.5, ou du `novel_words` > 0.5 — recule à l'incumbent et change d'angle.
- **Instruction explicite de Louis** (message, interruption) — toujours respecter.
- **Cap horaire dur** : 6h00 du matin si la session a démarré la veille au soir. Commit le dernier état stable, écrire un mini-SUMMARY dans `reports/SUMMARY.md`, s'arrêter sans exception.

Ne jamais déclarer « plancher atteint » de sa propre initiative sur la base de la métrique seule. La métrique rule-based ne capte pas le critère humain *« comme un discours préparé »* — c'est la lecture qualitative qui prime.

## Pièges connus

- **Goodhart sur les métriques rule-based** : un prompt peut avoir 0 cata / 0 lists / ratio 0.92 et produire un résultat « trop littéral » qui échoue le test humain. Pivot principal de la nuit du 26→27 avril 2026. La grille de scoring qualitatif domine toujours.
- **Contagion de forme** : quand le discours source contient une énumération (« il faut faire X, Y et Z »), le modèle génère des listes typographiques malgré l'interdiction. Renforcer explicitement avec un exemple concret « X, ensuite Y, et enfin Z » dans le prompt.
- **Sur-réécriture** : si la nouvelle itération est trop poussée (paraphrase, registre élevé, longueur < 0.7), recule. Le novel_words dans `last_rewrite_run.json` est le garde-fou rapide.
- **Sample pain point** : un sample qui reste catastrophe sur plusieurs itérations consécutives mérite une stratégie ciblée. L'isoler avec `--limit 1` après filtrage manuel pour itérer rapidement dessus.
- **Few-shot long** : exemples multi-paragraphes désorientent Ministral 14B plus qu'ils n'aident. Préférer un mini-exemple ciblé (3 phrases orales → 2 phrases d'écrit propre).
- **Variance Ministral à T≥0.30** : à 0.30 le modèle est bruyant, deux runs identiques peuvent diverger (lists 2 → 6, préambules 2 → 4). Si l'output est instable, baisser à 0.15 ou 0.0 pour trancher entre deux variantes.

## Échec Ollama

En cas de timeout / connexion refusée : attendre 30s, réessayer une fois. Si encore ko, noter `ollama-fail` dans `results.tsv` et alerter Louis sans continuer la boucle. Vérifier `ollama ps` côté Louis — un overflow CPU à 32K ctx est un piège connu (plafond actuel 16K).

## Amorce de session — premier tick

À la toute première invocation `/benchmark-loop <bracket>` :

1. Lire `C:\Users\Louis\.claude\plans\reprise-autoresearch-sur-vectorized-tulip.md` (cadre conceptuel complet).
2. Lire `benchmark/AGENT.md` (convention bench vs. utility, judge grid).
3. Lire `benchmark/results.tsv` (trajectoire passée).
4. Lire le prompt cible courant du bracket.
5. Vérifier que `benchmark/telemetry/corpus-<bracket>/corpus.jsonl` existe et n'est pas vide. Si absent → alerter Louis (le corpus doit être préparé via `whisper_bench.py` + `refresh_corpus.py` + `segment_corpus.py` avant le lancement).
6. Si `<bracket>` = `whisper` → lancer 1-2 itérations Whisper bench d'abord (voir Whisper baseline ci-dessous).
7. Vérifier qu'on est sur la branche `autoresearch/llm-rewrite-pivot-lissage-20260427` ou similaire — sinon créer la branche depuis `main` avant la première itération.
8. Enchaîner 2-3 itérations, puis `ScheduleWakeup`.

## Whisper baseline en début de nuit

Si la nuit doit aussi traiter Whisper (artefacts vus en usage : « fichu » → « figue », accents circonflexes manquants), faire 1-2 itérations Whisper bench d'abord, AVANT la passe LLM longue. Sinon les itérations LLM optimisent contre des artefacts Whisper au lieu de leur vrai mandat. Le prompt cible dans ce cas est `whisper_initial_prompt.txt`.

Si appelé via `/benchmark-loop whisper`, faire un mini-cycle (3-5 itérations max) puis basculer automatiquement sur `lissage` (la priorité de la nuit). Annoncer le pivot à Louis dans le message du tick.
