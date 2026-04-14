# Journal — benchmark Restructuration

Boucle d'optimisation du prompt Restructuration (Ministral 14B via Ollama). Signal primaire : score juge LLM (médiane des 8 samples du corpus). Objectif : passer d'un nettoyage strict (point de départ) à une vraie restructuration thématique tout en préservant la complétude absolue.

Contexte complet dans `loop_prompt_restructuration.md`. Prompt de départ dans `system_prompt_initial.txt` (copie du prompt Nettoyage optimisé — il sera insuffisant pour la restructuration par construction : il interdit la reformulation).

---

## Itération 1 — baseline (prompt Nettoyage copié tel quel)

**Score juge médian : 0.2875** (moyenne 0.2859). Rule-based médiane 0.0000 (aucune catastrophe, aucun préambule, ratios de longueur 0.95-0.99 = quasi-copie).

Scores juge par sample :
- #1 (6986→?): comp=4 clar=3 str=3 sob=5 → 0.2875
- #2 (5949→?): juge ≈ 0.29
- #3 (2052→?): comp=3 clar=2 str=2 sob=5 → 0.4750 (cas le plus mauvais)
- #4 (2364→?): comp=5 clar=4 str=3 sob=5 → 0.1500
- #5 (2610→?): comp=3 clar=3 str=3 sob=3 → 0.5000 (cas le plus mauvais)
- #6 (5307→?): comp=4 clar=3 str=2 sob=5 → 0.3375
- #7 (6959→?): comp=5 clar=4 str=3 sob=5 → 0.1500
- #8 (3264→?): comp=5 clar=4 str=4 sob=5 → 0.1000 (meilleur)

**Pattern observé** :
- SOBRIÉTÉ quasi toujours à 5 — le prompt actuel n'invente rien. Bon.
- STRUCTURE souvent 2-3 — le prompt ne regroupe pas par sujet, il reste linéaire. **Axe principal d'amélioration.**
- COMPLÉTUDE 3-5 — parfois perte d'idées (samples #3 et #5). À surveiller.
- CLARTÉ 3-4 — prose fluide mais pas transformée en écrit soigné.

Détails complets dans `last_report.json` (première sauvegarde du baseline).

**Axes pour l'itération 2** :
- Autoriser explicitement la reformulation oral→écrit (rompt la contrainte "copy the speaker's words").
- Demander un regroupement par sujet (pas juste découpage linéaire en paragraphes).
- Maintenir l'interdiction d'invention (SOBRIÉTÉ est actuellement l'acquis fort, ne pas le perdre).
- Garder vocabulaire spécifique + registre du locuteur même en reformulant.

---

## Itération 2 — prompt FR réécrit, reformulation autorisée, regroupement par sujet

**Score juge médian : 0.0250** (moyenne 0.0938). **Bond énorme** vs baseline (0.2875). Rule-based médiane 0.1194 (novel_words monte à ~0.4-0.5, attendu : reformulation = mots nouveaux).

Scores juge par sample :
- #1 (6986c): 5/5/5/5 → 0.00 (parfait)
- #2 (5949c): 3/3/3/3 → 0.50 ⚠️ **préambule "Voici la reformulation structurée…" + liste à puces** en fin
- #3 (2052c): 5/4/4/5 → 0.10
- #4 (2364c): 5/5/5/5 → 0.00
- #5 (2610c): 5/5/5/5 → 0.00
- #6 (5307c): 5/4/5/5 → 0.05
- #7 (6959c): 5/4/4/5 → 0.10
- #8 (3264c): 5/5/5/5 → 0.00

**Pattern observé** :
- Sobriété/complétude restent hautes (5 partout sauf #2).
- Sample #2 est la seule catastrophe : le modèle a généré un préambule ET une liste (probablement parce que le locuteur décrit une commande "qui fasse X, résume Y, commit Z"). La consigne anti-liste / anti-préambule a été écrasée par la structure du discours source.
- length_ratio souvent <0.85 : le modèle **condense**. Sur textes courts OK, à surveiller sur longs (risque de perte).
- #2 a length_ratio 0.71 et score 0.50 → la condensation + forme parasite = perte de complétude perçue.

**Axes pour l'itération 3** :
- Rappel explicite anti-préambule avec exemples ("Voici…", "La reformulation…", "---").
- Interdire toute liste même si le discours source contient une énumération — convertir en prose.
- Ajouter : ne jamais commenter sa propre tâche.

---

## Itération 3 — renforcement anti-préambule + anti-liste + anti-séparateur

**Score juge médian : 0.0250** (identique, moyenne 0.0922). Sample #2 résolu (0.50→0.05) mais #7 régresse (0.10→0.50) — utilise `---` comme séparateurs de sections.

Pattern : quand le prompt dit "jamais de séparateur ---", le modèle l'inclut quand même sur sample #7 (6959c, long). Problème du 14B avec textes longs : il réintroduit de la structure visuelle malgré l'interdiction. length_ratio=0.62 (condensation).

**Axes pour l'itération 4** :
- Renforcer l'interdiction des `---` en la mettant en premier, pas dans une liste de rappels.
- Insister sur : paragraphes séparés par simple ligne vide, jamais autre chose.
- Traiter la complétude sur textes longs comme sujet spécifique.

---

## Itération 4 — format strict en ouverture, prose continue

**Score juge médian : 0.0250** (stable). Moyenne **0.0469** (vs 0.0922 en it.3). Plus aucune catastrophe.

Distribution juge : 0, 0, 0.14, 0, 0, 0.05, 0.14, 0.05.

Samples à 0.14 (#3, #7) : comp=4 str=4 clar=5 sob=5 — quasi parfaits, perte d'un point sur complétude et structure.

Rule-based false positive sur #5 (preamble=1) alors que juge=0 — la sortie commence par "La transcription progressive est une priorité…" (pas un préambule, mais le détecteur rule-based se trompe). À ignorer.

**Axes pour l'itération 5** :
- Réécrire le prompt en enlevant le risque de contagion de forme (fait).
- Essayer une approche plus directive sur la complétude avec un exemple explicite de ce qu'est "perdre une nuance".

---

## Itération 5 — directive complétude détaillée (exemples, qualifications, chiffres)

**Score juge médian : 0.0000** (nouveau record !). Moyenne 0.0813.

Distribution : 0, 0.50, 0.05, 0, 0, 0.10, 0, 0. Cinq samples à 0.00. Mais #2 régresse à 0.50 (3/3/3/3) avec len_ratio 0.58 — condensation excessive.

**Analyse #2** : prose propre mais trop synthétique. 5949c→3446c. Des expressions comme "autrefois", "sans dépendre d'un environnement spécifique" sentent l'interprétation ajoutée. La consigne "sur les textes longs, ne condense pas" n'est pas assez opérationnelle.

**Axes pour l'itération 6** :
- Donner une cible de longueur plus concrète (proche de l'entrée, pas moitié).
- Interdire explicitement les termes interprétatifs ajoutés ("autrefois", "désormais", "en résumé").
- Garder le cadre itération 5 (il a donné la médiane 0.0000).

---

## Itération 6 — cible longueur 80-100% + interdiction adverbes interprétatifs

**Score juge médian : 0.0250** (moyenne 0.0969). Régression vs it.5 (0.00 / 0.081). Cette fois c'est #6 qui tombe à 0.50 (3/3/3/3) avec len_ratio 0.59. La consigne "80-100%" n'a pas empêché la condensation : len_ratios 0.50, 0.52, 0.54, 0.59, 0.85, 0.86, 0.87, 0.89.

La variance sample-par-sample d'un run à l'autre suggère du bruit du juge ± de la génération. Ordre de grandeur du bruit observé : ~0.025 sur médiane, ~0.03 sur moyenne.

**Observation** : on touche au plancher atteignable avec ce cadre FR + Ministral 14B. Le seul angle encore non exploré est l'anglais (recommandé par loop_prompt si blocage).

**Axes pour l'itération 7** :
- Essayer un prompt en anglais pour Ministral (parfois meilleure instruction-following).
- Conserver toutes les contraintes mais varier la forme.

---

## Itération 7 — prompt en anglais

**Score juge médian : 0.0000** (moyenne 0.0750). Sept samples ≤ 0.05, seul #6 reste à 0.50 (systématique).

Le prompt EN donne des résultats équivalents au FR (médiane 0.00 dans les deux cas). Le pain point reste sample #6 : flux oral très décousu ("D'accord, donc si je comprends bien..."), beaucoup de dialogue interne, le modèle condense fortement. len_ratio 0.57-0.59 systématique sur #6.

**Axes pour l'itération 8** :
- Tenter un few-shot court pour montrer la "bonne" longueur et fidélité.
- Revenir au FR (Louis préfère le français).
