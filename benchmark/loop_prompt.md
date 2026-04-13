# Prompt pour boucle d'optimisation — Restructuration

Tu es dans une boucle d'optimisation de prompt. Ton objectif : améliorer itérativement le system prompt dans `benchmark/system_prompt.txt` pour que le modèle Ministral 14B (via Ollama local) restructure au mieux des transcriptions vocales françaises longues.

## Contexte

Working directory : `d:/projects/ai/transcription`. Le dossier `benchmark/` contient :
- `system_prompt.txt` — le prompt à optimiser (c'est le seul fichier que tu modifies, avec journal.md)
- `benchmark.py` — lance les 8 samples du corpus via Ollama, produit un score rule-based et un `last_report.json` avec les sorties complètes
- `corpus.json` — 8 transcriptions orales brutes de longueurs variées (150s à 500s de parole)
- `journal.md` — ton journal d'évaluation, tu y notes tes observations à chaque itération
- `system_prompt_initial.txt` — le prompt de départ pour référence
- `config.ini` — config Ollama (modèle `ministral-3:14b`, température 0.3, contexte 32K)

Le score rule-based (0.0 parfait → 1.0 terrible) mesure : absence de préambule (35%), ratio de longueur entrée/sortie (40%), mots nouveaux/hallucinations (25%). C'est un signal grossier. Ta lecture qualitative des sorties est ce qui compte vraiment.

## Ce que tu fais à chaque itération

1. **Lance le benchmark** : `cd d:/projects/ai/transcription/benchmark && python benchmark.py --skip-judge --verbose` (timeout 600s, ~4 min pour 8 samples)

2. **Lis les résultats** : ouvre `last_report.json` et lis 2-3 sorties complètes (champ `output_text` dans `details[]`). Compare chaque sortie à l'entrée correspondante (champ `text` dans `corpus.json`, même `id`).

3. **Évalue la qualité** sur ces critères, par ordre d'importance :
   - **Complétude des idées** (critique) : TOUTES les idées, concepts, intentions, demandes et nuances de l'entrée doivent être dans la sortie. Aucune perte. Compare idée par idée.
   - **Fidélité de l'intention** : le ressenti, la direction voulue, les demandes du locuteur sont préservés tels quels.
   - **Absence d'invention** : rien ajouté qui n'était pas dans l'entrée. Pas d'interprétation, pas de conclusion inventée.
   - **Ton naturel** : le texte sonne comme la personne qui parle, en version écrite. Direct, naturel, fluide. PAS de registre académique ou distant.
   - **Forme** : prose en paragraphes. PAS de listes ni bullet points. Bold/italiques OK pour l'emphase.
   - Les préambules ("Voici le texte...", "D'accord", "Bien sûr") sont toujours mauvais.

4. **Note dans journal.md** : numéro d'itération, score médian, observations qualitatives, problèmes repérés, axes d'amélioration pour le prochain prompt.

5. **Écris un nouveau prompt** dans `system_prompt.txt`. Moins de 200 mots, en français, autonome et complet. Corrige les problèmes identifiés. Si tu n'as pas d'amélioration claire, tente une approche différente (reformulation, few-shot, instructions structurées, rappels anti-perte, angle complètement différent).

6. **Git commit** : `git add benchmark/system_prompt.txt benchmark/journal.md && git commit -m "bench: iteration N — description courte"`.

## Règles

- Ne touche QUE `system_prompt.txt` et `journal.md`. Rien d'autre.
- Si un prompt empire les choses (score ET qualité), reviens au prompt précédent et essaie un autre angle.
- Varie tes stratégies au fil des itérations. Ne reste pas bloqué sur la même approche.
- En cas d'erreur Ollama (timeout, connexion), attends 30s et réessaie une fois. Si ça échoue encore, note dans le journal et passe à autre chose.
- Pas besoin de tout lire à chaque itération : 2-3 samples suffisent pour évaluer, en alternant lesquels tu lis.

## Limites

Arrête-toi après 40 itérations. Note "FIN — 40 itérations atteintes" dans le journal avec un résumé du meilleur score obtenu et le prompt final.

## Lancement

Commence par lire `journal.md` pour voir où on en est (il y a déjà 2 itérations de baseline), puis lance une itération.
