---
name: benchmark-loop
description: Boucle d'optimisation itérative d'un system prompt via le benchmark local (Ministral 14B + juge LLM). Orchestre l'itération prompt → benchmark → diagnostic → nouveau prompt → commit, en pilotant la continuité via ScheduleWakeup (batchs de 2-3 itérations par turn). Utiliser quand Louis invoque `/benchmark-loop <profil>` avec profil ∈ {nettoyage, restructuration, prompt}.
allowed-tools: Read, Write, Edit, Bash, Grep, Glob, ScheduleWakeup
---

# Benchmark-loop — optimisation itérative d'un system prompt

## Périmètre

Une seule chose à la fois : améliorer itérativement `benchmark/prompts/system_prompt.txt` pour le profil cible, en mesurant le **score juge LLM** (médiane des 8 samples) et en capitalisant chaque itération dans le journal correspondant.

Invocation : `/benchmark-loop <profil>` où profil ∈ {`nettoyage`, `restructuration`, `prompt`}.

Résolution des fichiers en fonction du profil :

| Fichier | Chemin |
|---|---|
| Loop prompt (cadre fonctionnel complet) | `benchmark/journals/loop_prompt<_<profil> si restructuration ou prompt>.md` |
| Journal d'itérations | `benchmark/journals/journal<_<profil> si restructuration ou prompt>.md` |
| System prompt (cible) | `benchmark/prompts/system_prompt.txt` |
| Config benchmark | `benchmark/config/config.ini` |
| Corpus | `benchmark/data/corpus.json` |
| Dernier rapport | `benchmark/reports/last_report.json` |

Convention legacy : pour Nettoyage, les fichiers s'appellent `journal.md` et `loop_prompt.md` (sans suffixe).

**Avant la première itération** : lire `loop_prompt_<profil>.md` (cadre fonctionnel, contraintes, signal juge), le journal existant (trajectoire, dernier score, axes restants), et `system_prompt.txt` (état actuel).

## Règles immuables

- Ne modifier QUE deux fichiers par itération : `benchmark/prompts/system_prompt.txt` et le journal du profil.
- `config/config.ini` n'est touché que sur instruction explicite de Louis.
- Ne jamais arrêter la boucle de sa propre initiative. Critères d'arrêt définis plus bas — tant qu'ils ne sont pas atteints, continuer.
- En cas de régression franche (score ET qualité pires sur 2 itérations successives), revenir au meilleur prompt connu via `git show <sha>:benchmark/prompts/system_prompt.txt` et changer d'angle.
- Ne jamais lancer `build-run.ps1`, `MSBuild.exe`, ou tout build WhispUI. Ce skill ne touche pas au code de l'app.

## Commande benchmark

Toujours depuis le dossier `benchmark/`, timeout 900s :

```
cd d:/projects/ai/transcription/benchmark && python benchmark.py --verbose
```

Le rapport est écrit dans `benchmark/reports/last_report.json`. Structure :

```
r['details'][i] avec clés : id, rule_score, judge_score, catastrophe, rule, judge, input_len, output_len, length_ratio, output_text, elapsed_sec
r['judge_median'], r['judge_mean'] — agrégats
```

Le score qui ressort sur stdout `SCORE=X.XXXX` à la fin est la médiane juge sur les 8 samples (0.0 parfait → 1.0 terrible).

## Protocole par itération

Pour chaque itération :

1. **Lancer le benchmark** (commande ci-dessus).
2. **Lire 2-3 `output_text`** dans `reports/last_report.json` et comparer idée par idée au sample correspondant dans `data/corpus.json` (même `id`). Alterner quels samples on lit d'une itération à l'autre pour couvrir les 8 au fil du temps. Samples courts (2000-3500c) et longs (5000-7000c) se comportent différemment.
3. **Évaluer qualitativement** dans l'ordre : complétude (qu'est-ce qui manque ?), structure (regroupement thématique effectif ou juste découpage linéaire ?), fidélité vocabulaire/registre, absence d'invention, forme (pas de listes, pas de préambule, pas de markdown).
4. **Noter dans le journal** : numéro d'itération, score juge médian + moyen, scores par critère si marquants, observations qualitatives concrètes **avec exemples courts** de nuances perdues ou inventées, axes pour le prochain prompt.
5. **Écrire le nouveau `system_prompt.txt`** : <300 mots, FR préférablement (EN possible), autonome et complet. Corriger les problèmes identifiés. **Varier les stratégies** (reformulation, few-shot court, instructions structurées, rappels anti-perte, angle radicalement différent). Ne pas rester bloqué sur une même approche.
6. **Committer** :
   ```
   git add benchmark/prompts/system_prompt.txt benchmark/journals/journal<_<profil>>.md
   git commit -m "bench-<profil>: iteration N — résumé court"
   ```

## Continuité via ScheduleWakeup — RÈGLE CENTRALE

**Ne PAS enchaîner les 40 itérations dans un seul turn.** C'est ce qui a fait échouer la session du matin (arrêt prématuré, pas de reprise possible).

Protocole :

- Faire **2-3 itérations par turn**, puis appeler `ScheduleWakeup`.
- `delaySeconds` entre **60 et 180** — on reste ainsi dans le cache prompt (TTL 5 min = 300s), les turns suivants sont rapides et peu coûteux.
- `prompt` passé à ScheduleWakeup = la commande `/benchmark-loop <profil>` **verbatim**, pour ré-entrer dans ce skill au tick suivant.
- `reason` = "iter N/40, <axe testé dans le batch>" — sera affiché à Louis.
- Ordre de grandeur : **~13-15 turns pour 40 itérations**.
- La continuité est assurée par les commits git et le journal — Louis peut interrompre à tout moment, on reprend là où on s'est arrêté en lisant le journal.

**Au début de chaque turn** : lire le journal pour savoir où en est la boucle (numéro de la dernière itération, score courant, axes déjà explorés).

## Critères d'arrêt

Arrêter uniquement si **un** de ces critères est atteint :

- **40 itérations atteintes** (60 si Louis le précise explicitement au lancement).
- **Stagnation** : médiane juge < 0.15 pendant **5 itérations consécutives sans amélioration**.
- **Instruction explicite de Louis** (message, interruption).

Ne jamais déclarer "plancher atteint" de sa propre initiative. La variance du juge (~0.05-0.10 sur moyenne) peut faire croire à une stagnation qui n'en est pas une.

## Pièges connus (capitalisation)

- **Juge 14B bruité** — variance ~0.05-0.10 sur moyenne d'un run à l'autre. Toute "amélioration" sous ce seuil = bruit. Ne pas sur-interpréter une baisse de 0.025 sur la médiane.
- **Contagion de forme** — quand le discours source contient une énumération, le modèle génère des listes malgré l'interdiction. Renforcer explicitement la consigne anti-liste dans le prompt.
- **Condensation sur textes longs** — `length_ratio < 0.7` sur samples 6000+ chars = perte de nuances. Une consigne "80-100%" ne suffit pas. Préférer un few-shot court qui montre concrètement la bonne fidélité oral→écrit.
- **Rule-based false positives** — le détecteur de préambule se trompe parfois (ex. phrase thématique d'ouverture prise pour un "Voici…"). Toujours vérifier avec le `judge_score` correspondant avant d'incriminer le prompt.
- **Few-shot long** — exemples multi-paragraphes désorientent le 14B plus qu'ils n'aident. Préférer un exemple court et ciblé (3 phrases orales → 2 phrases écrites restructurées).
- **Pain point récurrent** — un sample systématiquement mauvais (ex. sample #6 en Restructuration : flux décousu avec dialogue interne) peut nécessiter une stratégie spécifique. Un few-shot ancré sur ce type de discours aide souvent.

## Échec Ollama

En cas de timeout/connexion : attendre 30s, réessayer une fois. Si encore ko, noter l'échec dans le journal (itération marquée "crash ollama") et laisser la main à Louis sans continuer la boucle.

## Amorce de session

Au premier tick (`/benchmark-loop <profil>` lancé par Louis) :

1. Lire `benchmark/journals/loop_prompt<_<profil>>.md` en entier.
2. Lire le journal existant pour identifier la dernière itération et son score.
3. Lire `benchmark/prompts/system_prompt.txt`.
4. Vérifier que `config/config.ini` a le bon `profile=` — sinon alerter Louis (ne pas modifier sans instruction).
5. Enchaîner 2-3 itérations, puis `ScheduleWakeup`.
