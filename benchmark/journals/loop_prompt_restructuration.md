# Prompt pour boucle d'optimisation — Restructuration

Tu es dans une boucle d'optimisation de prompt. Ton objectif : améliorer itérativement le system prompt dans `benchmark/system_prompt.txt` pour que Ministral 14B (Ollama local) **restructure** au mieux des transcriptions vocales françaises longues — pas seulement les nettoyer.

## Contexte du projet

Working directory : `d:/projects/ai/transcription`. C'est un utilitaire de transcription vocale locale (WhispUI, WinUI 3). Après transcription par Whisper, un LLM local réécrit le texte. Trois profils existent : Nettoyage, Restructuration, Prompt. **Tu optimises le prompt Restructuration**.

Le profil Nettoyage est déjà optimisé (40 itérations précédentes, score rule-based 0.0000). Son prompt — que tu prends comme point de départ — dit au modèle de copier les mots exacts du locuteur. **Résultat : il nettoie mais ne restructure pas**. C'est précisément ça qu'on doit changer ici sans casser la complétude.

## Ce que Louis veut pour la Restructuration

Le texte d'entrée est une transcription orale brute, souvent longue (2 à 10 minutes de parole), qui part dans tous les sens — la personne passe d'un sujet à l'autre, revient en arrière, répète, se corrige. Ce qu'il veut en sortie :

- **Organisation en paragraphes par sujet.** Regrouper ce qui va ensemble, même si c'était dispersé dans le flux oral.
- **Reformulation autorisée** pour passer de l'oral à l'écrit fluide. Changer la structure des phrases, fusionner, découper — OK.
- **Suppression des redondances** : quand la personne dit la même chose trois fois de manière différente, garder une seule formulation qui préserve le sens.
- **MAIS : complétude absolue.** Chaque idée, chaque nuance, chaque demande, chaque intention doit être présente dans la sortie. Zéro perte sémantique. C'est la contrainte non négociable.
- **Vocabulaire et style du locuteur préservés.** Si la personne dit "enlever", on garde "enlever" (pas "supprimer"). Son registre, son ton, ses tics de langage quand ils portent du sens — on les garde.
- **Pas d'invention.** Aucune idée, conclusion ou interprétation que le locuteur n'a pas dite.
- **Prose en paragraphes uniquement.** Pas de listes, pas de bullets, pas de titres, pas de markdown structurel. Bold/italiques tolérés pour l'emphase si vraiment pertinents.
- **Aucun préambule** ("Voici…", "Bien sûr…", "D'accord…", "La version restructurée est…"). La sortie commence directement par le premier mot du contenu.

**La peur principale** : qu'en autorisant la reformulation, le 14B perde des nuances par concision excessive, ou en invente. La complétude prime sur l'élégance.

## Le benchmark

Le dossier `benchmark/` contient :

- `system_prompt.txt` — le prompt que tu modifies à chaque itération (seul fichier de prompt modifié).
- `benchmark.py` — envoie les 8 samples du corpus à Ollama, produit un score **juge LLM** (signal primaire) et un `last_report.json` détaillé.
- `corpus.json` — 8 transcriptions orales brutes (150s à 513s, 2052 à 6986 caractères).
- `config.ini` — `ministral-3:14b`, température 0.3, contexte 32K.
- `journal_restructuration.md` — ton journal. Tu y notes chaque itération.
- `system_prompt_initial.txt` — le prompt de départ (= prompt Nettoyage optimisé) pour référence.

### Scoring — lire attentivement

**Le juge LLM est le signal primaire.** Contrairement au benchmark Nettoyage précédent, la métrique "novel_words" (mots nouveaux dans la sortie) n'est plus un signal fiable : en restructuration, reformuler = créer des mots nouveaux, c'est normal et voulu. Seul un jugement sémantique peut mesurer complétude et sobriété.

Le juge est un second appel Ministral 14B qui compare entrée et sortie sur 4 critères notés 1-5 :
- **COMPLÉTUDE 35 %** — toutes les idées présentes ?
- **SOBRIÉTÉ 25 %** — rien inventé ?
- **CLARTÉ 20 %** — bien écrit, fluide ?
- **STRUCTURE 20 %** — bien organisé en paragraphes thématiques ?

Score juge : 0.0 (parfait) → 1.0 (terrible). C'est la médiane des 8 samples qui ressort en `SCORE=X.XXXX` à la fin du run.

Le score rule-based (préambule, length_ratio, novel_words) reste calculé et visible dans le rapport — il sert de filet pour détecter les catastrophes (sortie vide, préambule évident, hallucination massive), pas de signal principal.

**Ta lecture qualitative des sorties compte autant que le score juge.** Le juge est lui-même un 14B — il se trompe. Lis 2-3 sorties à chaque itération, compare idée par idée à l'entrée, cherche les nuances perdues ou ajoutées.

## Ce que tu fais à chaque itération

1. **Lance le benchmark** : `cd d:/projects/ai/transcription/benchmark && python benchmark.py --verbose` (timeout 900s, ~7-10 min pour 8 samples + 8 appels juge).

2. **Lis les résultats** : ouvre `last_report.json`. Regarde le `judge_score` de chaque sample, les détails par critère, et lis 2-3 `output_text` complets. Compare à l'entrée correspondante (champ `text` dans `corpus.json`, même `id`).

3. **Évalue qualitativement** — par ordre d'importance :
   - **Complétude** : compare idée par idée entrée ↔ sortie. Qu'est-ce qui manque ?
   - **Structure** : les paragraphes regroupent-ils vraiment par sujet ? Ou c'est juste du linéaire découpé ?
   - **Fidélité du vocabulaire et du registre** : le locuteur se reconnaîtrait-il ?
   - **Absence d'invention** : y a-t-il des formulations ou conclusions qui n'étaient pas dans l'entrée ?
   - **Forme** : pas de listes, pas de préambule, pas de markdown structurel.

4. **Note dans `journal_restructuration.md`** : numéro d'itération, score juge médian + moyen, scores par critère, observations qualitatives concrètes (avec exemples courts de nuances perdues ou inventées), axes pour le prochain prompt.

5. **Écris un nouveau prompt** dans `system_prompt.txt`. Moins de 300 mots, en français de préférence (l'anglais est OK si ça marche mieux). Autonome et complet. Corrige les problèmes identifiés. Varie les stratégies : reformulation, few-shot, instructions structurées, rappels anti-perte, angles complètement différents. Ne reste pas bloqué sur une même approche.

6. **Git commit** : `git add benchmark/system_prompt.txt benchmark/journal_restructuration.md && git commit -m "bench-restruct: iteration N — description courte"`.

## Règles

- Ne touche QUE `system_prompt.txt` et `journal_restructuration.md`. Rien d'autre.
- Si un prompt empire les choses (score ET qualité), reviens au meilleur prompt précédent et essaie un autre angle.
- Varie tes stratégies. Essaie aussi des prompts en anglais si tu bloques.
- En cas d'erreur Ollama (timeout, connexion), attends 30s et réessaie une fois. Si ça échoue encore, note dans le journal et passe à autre chose.
- Pas besoin de tout relire à chaque itération : 2-3 samples suffisent pour évaluer, alterne lesquels tu lis pour couvrir les 8 au fil du temps (samples courts et longs se comportent différemment).

## Arbitrages attendus

Tu vas rencontrer des tensions :

- **Complétude vs concision** — favorise la complétude. Un texte un peu redondant mais complet vaut mieux qu'un texte élégant qui perd des nuances.
- **Structure vs ordre du discours** — regroupe par sujet même si ça casse l'ordre d'origine, tant que toutes les idées sont préservées.
- **Fidélité vs fluidité** — reformule pour la fluidité écrite mais garde le vocabulaire spécifique et les intentions du locuteur.
- **Score juge vs qualité réelle** — le juge est un 14B, pas une vérité. Si ton œil voit des problèmes que le juge rate, fais confiance à ton œil.

## Limites

Arrête-toi après 40 itérations, ou dès que le score juge médian stagne 5 itérations d'affilée sous 0.15 sans amélioration. Note "FIN — N itérations" dans le journal avec un résumé du meilleur score, le prompt final et les apprentissages clés.

## Lancement

Commence par :
1. Lire `journal_restructuration.md` (vide au début — écris l'itération 1 en te basant sur le prompt de départ).
2. Lancer `python benchmark.py --verbose` pour obtenir le score baseline du prompt de départ.
3. Ouvrir 2-3 sorties dans `last_report.json`, les comparer aux entrées correspondantes.
4. Diagnostiquer : qu'est-ce qui manque de la restructuration attendue ? Trop de copie littérale ? Pas de regroupement thématique ?
5. Écrire l'itération 2 avec une modification ciblée. Committer. Recommencer.

Bonne boucle.
