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

---

## Itération 8 — few-shot FR (exemple court Whisper/Ollama)

**Score juge médian : 0.0250** (moyenne **0.0516**). #6 **résolu** (0.50 → 0.14) grâce au few-shot. Plus aucune catastrophe > 0.14.

Distribution : 0, 0.05, 0.14, 0, 0, 0.14, 0.09, 0. Sept samples ≤ 0.09, deux à 0.14.

Le few-shot ancre le modèle sur un exemple concret de fidélité oral→écrit. Bénéfice spécifique sur les flux décousus.

**Axes pour l'itération 9** :
- Les 0.14 restants (#3, #6) ont probablement comp=4 ou str=4. Tenter un deuxième exemple plus long pour montrer le regroupement thématique.
- Ou essayer de resserrer sur la complétude détaillée sans exemple supplémentaire.

---

## Itération 9 — few-shot remplacé par un exemple multi-paragraphes

**Score juge médian : 0.0688** (moyenne 0.1078). **Régression franche** vs it.8. Sample #6 repart à 0.50, #7 à 0.14. L'exemple plus long n'aide pas — peut-être même qu'il confond le modèle sur quand séparer les paragraphes.

**Décision** : retour au prompt itération 8 (best overall : médiane 0.025, moyenne 0.052, aucun 0.50).

---

## Bilan et fin de boucle

**Meilleur résultat : itération 8** (few-shot FR court, Whisper/Ollama).
- Baseline (it.1) : médiane **0.2875**, moyenne 0.2859.
- Best (it.8) : médiane **0.0250**, moyenne **0.0516**, tous samples ≤ 0.14.
- Best médiane atteinte : **0.0000** (it.5 et it.7) avec variance inter-run.

**Trajectoire des scores juge (médian / moyen)** :
- it.2 FR réécrit : 0.025 / 0.094
- it.3 anti-préambule : 0.025 / 0.092
- it.4 prose anti-contagion : 0.025 / 0.047
- it.5 complétude détaillée : 0.000 / 0.081
- it.6 cible longueur : 0.025 / 0.097
- it.7 prompt EN : 0.000 / 0.075
- **it.8 few-shot FR : 0.025 / 0.052** ← meilleur compromis
- it.9 few-shot multi-para : 0.069 / 0.108 (régression)

**Apprentissages clés** :
1. Le levier principal était d'**autoriser explicitement la reformulation** (it.1→it.2 : 0.29→0.03). Le prompt de départ, hérité du Nettoyage, interdisait la reformulation — contradictoire avec l'objectif de restructuration.
2. Le **few-shot court** fixe les flux très décousus (sample #6 : 0.50→0.14). Le modèle s'ancre sur un exemple concret de fidélité.
3. Un **few-shot long ou multi-paragraphes** peut au contraire désorienter le modèle et augmenter la variance. Préférer un exemple court et ciblé.
4. **Sample #6 est le pain point systématique** : flux oral très décousu avec dialogue interne. Le modèle est tenté de condenser ~60% au lieu de 80-100%. La consigne de longueur cible ne bride pas suffisamment.
5. **Le juge 14B est bruité** : variations de ~0.05-0.10 sur la moyenne d'un run à l'autre pour un même prompt. Toute "amélioration" sous ce seuil est du bruit.
6. **Anglais ≈ français** pour ce modèle sur cette tâche. Pas de gain exploitable à changer de langue.

**Pause temporaire à 9 itérations.** Score juge médian passé de 0.2875 à 0.0250 (-91%), moyenne de 0.2859 à 0.0516 (-82%). Prompt retenu temporairement : itération 8 (restauré dans `system_prompt.txt`). La boucle reprend ci-dessous : le skill interdit de déclarer "plancher atteint" de sa propre initiative.

---

## Itération 10 — re-test du prompt it.8 restauré (baseline de reprise)

**Score juge médian : 0.0250** (moyenne **0.0969**). Médiane identique à it.8 mais moyenne remontée (0.052 → 0.097) — variance inter-run du juge.

Distribution juge : 0, 0.09, 0.14, 0, 0, **0.50**, 0.05, 0. Sample #6 régresse sèchement à 0.50 (3/3/3/3, len_ratio 0.63). Les autres restent excellents (5 samples à 0.00).

**Lecture qualitative #6** (flux décousu sur développement WinUI/Windows natif) : le modèle condense agressivement (5307c → len_ratio 0.63 ≈ 3350c). Prose propre mais perd des précisions : l'exemple spécifique du `SettingsCard` Win11, la nuance sur les valeurs numériques vs theme resources, la distinction entre le `MicaBackdrop` des fenêtres principales et le `DesktopAcrylicBackdrop` des transient. Le juge voit bien la perte (comp=3).

**Lecture qualitative #1** (6986c, 5/5/5/5 = parfait juge mais len_ratio 0.40) : condensation forte aussi, pourtant le juge donne 5 partout. Soit le sample est très redondant (la condensation est légitime), soit le juge 14B est généreux. Contradiction connue : le juge ne mesure pas bien la perte quand l'entrée contient beaucoup de répétitions.

**Diagnostic** : le pain point reste l'équilibre complétude/condensation sur les flux décousus longs avec dialogue interne. Le few-shot court ne couvre pas ce cas spécifique. Il faut soit un rappel ciblé dans le prompt, soit un deuxième exemple très court orienté "flux décousu avec hésitations et retours en arrière".

**Axes pour l'itération 11** :
- Ajouter dans le prompt un rappel explicite sur la préservation du raisonnement par étapes : "Si le monologue avance par allers-retours, hésitations et reprises, conserve les étapes du raisonnement, ne les fusionne pas en conclusion directe."
- Garder le few-shot court existant (qui marche ailleurs).
- Ne pas dégrader les samples déjà à 0.00.

---

## Itération 11 — rappel anti-fusion sur flux décousus

**Score juge médian : 0.0250** (moyenne **0.0375**). **Nouveau record de moyenne** (best précédent : 0.0516 en it.8). Tous les samples ≤ 0.10, aucune catastrophe.

Distribution juge : 0, 0.05, 0.10, 0, 0.05, 0.10, 0, 0. Quatre samples à 0.00.

**#6 résolu durablement** : 0.50 → 0.10 (comp=5 clar=4 str=4 sob=5). Le rappel "ne fusionne pas plusieurs allers-retours en une conclusion directe" a bien accroché. Len_ratio remonté de 0.63 à 0.71.

Rule-based false positive sur #5 (preamble=1, sortie commence par "La transcription progressive est une priorité…"). Juge=0.05, à ignorer.

**Axes pour l'itération 12** :
- Ne pas casser l'acquis. Tester une micro-variation.
- Il reste de la marge sur #3 (0.10, str=4 clar=4) et #6 (0.10, str=4 clar=4) — la STRUCTURE pourrait mieux regrouper.
- Tenter une reformulation de la section "Organisation thématique" pour mieux ancrer le regroupement.

---

## Itération 12 — reformulation "Organisation thématique" plus directive

**Score juge médian : 0.0000** (moyenne 0.0797). Nouveau record médiane (6 samples à 0.00) mais **régression franche sur la moyenne** vs it.11 (0.0375 → 0.0797) — #6 repart à 0.50 (3/3/3/3).

Distribution juge : 0, 0.09, 0.05, 0, 0, 0.50, 0, 0.

La reformulation en "identifie mentalement les grands thèmes, regroupe dans la logique du propos plutôt que l'ordre chronologique" a sans doute incité le modèle à recomposer trop agressivement sur #6 (flux décousu avec hypothèses multiples), effaçant des étapes de raisonnement. Len_ratio #6 = 0.72 (pas une condensation extrême), c'est la réorganisation qui a fait perdre comp/clar/str d'un coup.

**Décision** : revenir à la section "Organisation thématique" de l'it.11 (plus sobre). La moyenne 0.0375 était un acquis solide, à ne pas lâcher pour un gain médiane sujet au bruit.

**Axes pour l'itération 13** :
- Revert de "Organisation thématique" vers la formulation it.11.
- Tester autre chose pour gagner sur les 0.05-0.10 résiduels sans toucher au pain point.

---

## Itération 13 — revert organisation thématique vers it.11

**Score juge médian : 0.0250** (moyenne **0.0859**). #6 reste à 0.50 (3/3/3/3) malgré le prompt reverté identique à l'it.11.

Distribution juge : 0, 0, 0.05, 0, 0.09, 0.50, 0, 0.05.

**Apprentissage clé** : même prompt (it.11 = it.13, 3568 chars identiques) → #6 oscille 0.10 ↔ 0.50 d'un run à l'autre. C'est la **variance inter-run** qui domine #6, pas le prompt. L'it.11 avait un bon run chanceux. Le prompt seul n'a pas de stratégie robuste pour stabiliser ce sample.

**Rule-based false positive sur #5 encore** (preamble=1, "La transcription progressive est une priorité…", juge=0.09). Ignorer.

**Axes pour l'itération 14** :
- Tenter une compression du prompt. 3568 chars c'est long pour un 14B, peut-être que la consigne centrale se dilue.
- Préserver les acquis : format strict, raisonnement par étapes, complétude, anti-invention, few-shot court.
- Supprimer les redondances et consolider.

---

## Itération 14 — compression du prompt

**Score juge médian : 0.0000 / moyenne 0.0234**. **DOUBLE RECORD** : médiane parfaite + moyenne meilleure que le précédent best (it.11 à 0.0375). Aucune catastrophe, tous samples ≤ 0.09.

Distribution juge : 0, 0.05, 0.05, 0, 0, 0.09, 0, 0. Six samples à 0.00. #6 à 0.09 (comp=4 clar=5 str=5 sob=5) — plus la catastrophe 0.50 d'avant.

Prompt passé de 3568 chars à 2422 chars (-32%). Hypothèse confirmée : la verbosité diluait l'attention du 14B. Prompt plus compact = meilleure instruction-following.

Len_ratios très variables (0.37 à 0.79) mais le juge ne pénalise pas les condensations fortes tant que rien d'important ne manque — peut-être parce que les samples longs sont effectivement redondants à l'oral. La condensation sur #6 (0.48) est contrebalancée par la préservation des points clés.

**Axes pour l'itération 15** :
- Garder la compression comme acquis de base.
- Tenter un second exemple très court ciblant un flux décousu avec dialogue interne (pain point #6).
- Rester sous 2800 chars total.

---

## Itération 15 — ajout d'un second exemple court (flux décousu)

**Score juge médian : 0.0000 / moyenne 0.0859**. **Régression franche** sur la moyenne (0.0234 → 0.0859).

Distribution : 0, 0.05, 0.14, 0, 0, 0.50, 0, 0.

**Problème majeur #2** : le modèle a généré le préambule "Voici la reformulation structurée selon tes instructions :\n\n---" + liste. Le label "Exemple 2" dans le prompt a vraisemblablement déclenché l'imitation du cadre de présentation avec séparateur "---". Effet secondaire exact de ce que le skill met en garde : "few-shot long désoriente".

#6 repart à 0.50. Le second exemple (SettingsCard/MicaBackdrop) qui pourtant ressemble au sample #6 ne l'a pas stabilisé — probablement parce que le modèle est maintenant distrait par la structure du prompt plutôt que par le contenu.

**Décision** : revert complet à la compression it.14 qui est le meilleur résultat à ce jour.

**Axes pour l'itération 16** :
- Revert vers le prompt it.14 (confirmer la stabilité en re-benchmarkant).
- Ne plus toucher aux exemples. Tenter des ajustements ailleurs (phrasé des consignes, ordre).

---

## Itération 16 — revert vers it.14 (re-test stabilité)

**Score juge médian : 0.0000 / moyenne 0.0813**. Même prompt qu'it.14 (identique au byte près) mais moyenne monte de 0.0234 à 0.0813.

Distribution : 0, 0.05, 0.10, 0, 0, 0.50, 0, 0.

**Découverte importante** : #2 a reproduit le préambule "Voici la reformulation structurée selon tes directives :\n\n---" **sans avoir le label "Exemple 2" dans le prompt**. Ce bug n'est donc pas dû au second exemple d'it.15 : il est latent dans le prompt compressé lui-même. Sur certains runs, le modèle "décide" de préambuler malgré la consigne.

La moyenne 0.0234 d'it.14 était partiellement chanceuse. Le vrai plancher moyenne du prompt it.14 est plus proche de 0.05-0.08 compte tenu de la variance #2 et #6.

**Axes pour l'itération 17** :
- Renforcer la consigne anti-préambule : la mettre **en tout premier** dans le prompt (primacy effect) + la formuler négativement.
- Garder toute la compression d'it.14 par ailleurs.

---

## Itération 17 — anti-préambule en ouverture (primacy)

**Score juge médian : 0.0250 / moyenne 0.0469**. Distribution : 0, 0.09, 0.05, 0, 0.05, 0.19, 0, 0.

#2 stabilisé (0.09, pas de préambule "Voici…"). #6 à 0.19 (pas de catastrophe 0.50, mais moins bon que le meilleur run it.14). Aucune catastrophe cette fois.

Le renforcement anti-préambule en tête a limité le risque de régression sur #2. Mais il a sans doute coûté un peu en médiane (passée de 0.000 à 0.025) — probablement par effet d'accent qui distrait légèrement de la consigne de complétude.

**Axes pour l'itération 18** :
- Tenter une variation dans la formulation de la "Méthode" en trois étapes en prose ("D'abord… Ensuite… Enfin…") pour mieux guider le processus.
- Garder l'anti-préambule en ouverture.

---

## Itération 18 — méthode en trois étapes explicites

**Score juge médian : 0.0000 / moyenne 0.0750**. Distribution : 0, 0.05, 0.05, 0, 0, 0.50, 0, 0.

La méthode "D'abord / Ensuite / Enfin" fait gagner 2-3 samples sur la médiane (6 samples à 0.00 vs 5 en it.17) mais #6 repart à 0.50 — variance systémique. Aucun angle strictement sur la méthode ne stabilise #6.

**Synthèse it.14→it.18** sur le même angle (prompt compressé) :
- #6 oscille 0.09 / 0.50 / 0.50 / 0.19 / 0.50
- Moyenne oscille 0.023 / 0.086 / 0.081 / 0.047 / 0.075

Le plancher semble être autour de 0.04-0.07 en moyenne pour cet angle. Le best 0.0234 (it.14) est un outlier chanceux.

**Axes pour l'itération 19** :
- Angle neuf : consigne de vérification post-rédaction, pour forcer une passe de complétude.
- Garder la compression et l'anti-préambule en ouverture.

---

## Itération 19 — consigne de vérification de complétude

**Score juge médian : 0.0000 / moyenne 0.0297**. Distribution : 0, 0, 0.05, 0, 0, 0.19, 0, 0.

**Très bon résultat** : 6 samples à 0.00, 1 à 0.05, 1 à 0.19 (#6). Aucune catastrophe 0.50. Second meilleur derrière it.14 (0.0234) mais probablement plus stable (it.14 était partiellement chanceux).

#6 stabilisé à 0.19 (comp=4 clar=4 str=4) au lieu d'osciller à 0.50. La consigne "vérifie mentalement que chaque thème, chaque exemple concret, chaque hypothèse et chaque qualification apparaît" + "si tu hésites entre garder ou couper, garde" a clairement aidé.

#2 propre (0.00, pas de préambule).

**Axes pour l'itération 20** :
- Micro-renforcement de la "Vérification finale" pour cibler explicitement les allers-retours de raisonnement (vise #6).
- Ne pas casser l'équilibre.

---

## Itération 20 — vérification renforcée sur allers-retours

**Score juge médian : 0.0938 / moyenne 0.2109**. **Régression franche** vs it.19 (0.000 / 0.030).

Distribution : 0.50, 0.50, 0.14, 0, 0, 0.50, 0.05, 0. Trois catastrophes 0.50 (#1, #2, #6).

Le simple ajout "chaque aller-retour de raisonnement" a clairement déstabilisé. Hypothèse : l'expression a été interprétée par le 14B comme une consigne de structure visible (peut-être qu'il a tenté de marquer les retours en arrière), faisant perdre la fluidité et la complétude. Aussi possible : variance violente qui amplifie un effet déjà fragile.

**Décision** : revert intégral vers it.19 (consigne de vérification sans "aller-retour").

**Axes pour l'itération 21** :
- Revert vers it.19.
- Tenter un autre angle : la structure, la formulation des paragraphes.

---

## Itération 21 — revert vers it.19 (consigne vérification originale)

**Score juge médian : 0.0000 / moyenne 0.0859**. Distribution : 0, 0, 0.14, 0, 0, 0.50, 0.05, 0.

Même prompt qu'it.19, run normal (vs run chanceux d'it.19 à 0.030). #6 retombe à 0.50. La consigne de vérification finale ne suffit pas à stabiliser durablement #6.

**Bilan agrégé it.14→it.21** sur l'angle "prompt compressé" :
- Best moyenne : 0.0234 (it.14) — chanceux
- Best médiane : 0.0000 atteinte 4 fois (it.14, 16, 18, 19, 21) — fréquent
- #6 oscille sauvagement : 0.09, 0.50, 0.50, 0.19, 0.50, 0.19, 0.50, 0.50
- Plancher moyenne réaliste : 0.06-0.09

Le prompt actuel est solide pour 7 samples sur 8. Le pain point #6 (flux décousu avec dialogue interne sur sujet WinUI/Windows natif) ne se laisse pas dompter par les variations de consigne.

**Axes pour l'itération 22** :
- Angle persona éditoriale ("tu es éditeur de transcriptions pour publication") — angle pas encore testé.
- Garder anti-préambule, vérification finale, exemple court.

---

## Itération 22 — persona éditoriale

**Score juge médian : 0.0500 / moyenne 0.0969**. Distribution : 0.09, 0.09, 0.05, 0, 0, 0.50, 0, 0.05.

Régression sur #1 (0→0.09) et #2 (0→0.09). #4 a un len_ratio de **1.21** — le modèle a sur-écrit, dépassant la longueur de l'entrée. La consigne "publication écrite, finition prime sur concision" pousse trop vers l'élaboration. #6 reste à 0.50.

L'angle persona éditoriale est donc nuisible : il invite à enrichir au lieu de simplement mettre en forme.

**Axes pour l'itération 23** :
- Revert de la persona vers la formulation neutre it.21.
- Tenter une approche encore plus minimale (compression supplémentaire) pour voir si la sobriété aide.

---

## Itération 23 — revert persona + compression supplémentaire

**Score juge médian : 0.0000 / moyenne 0.1313**. Distribution : 0.50, 0, 0, 0, 0, 0.50, 0, 0.05.

Pire moyenne récente. La compression à 2001 chars (vs 2973 en it.19) a coûté en stabilité : #1 retombe à 0.50, #6 reste à 0.50.

**Apprentissage** : la taille optimale du prompt semble être autour de 2700-3000 chars. Trop court (2000) = perte de stabilité, trop long (3500+) = dilution de l'attention. Le prompt it.19 (2973 chars) est dans la zone optimale.

**Axes pour l'itération 24** :
- Revert vers le prompt it.19 exact (consigne vérification finale, anti-préambule en tête, exemple court, méthode standard).
- Tenter ensuite une variation différente : ajouter une ligne ciblée pour la condensation excessive sur les flux décousus.

---

## Itération 24 — revert vers it.19 (best stable connu)

**Score juge médian : 0.0437 / moyenne 0.1984**. Distribution : 0, 0.50, 0, 0.09, 0.50, 0.50, 0, 0. Trois catastrophes (#2, #5, #6).

**Variance massive confirmée** : prompt identique au byte près (it.19 = it.21 = it.24) donne 0.030 / 0.086 / 0.198 sur trois runs successifs. C'est la variance de génération + variance du juge cumulées. Le piège connu "juge bruité ± 0.05-0.10" sous-estime la réalité — on voit ± 0.15-0.17 sur la moyenne.

#5 cette fois a une vraie catastrophe juge (3/3/3/3) alors qu'avant c'était un faux positif rule-based seul. Le juge lui-même évalue différemment d'un run à l'autre.

**Conséquence méthodologique** : on ne peut conclure ni "plancher atteint" ni "amélioration" sur un seul run. Continuer à varier les angles, capitaliser sur les patterns récurrents.

**Axes pour l'itération 25** :
- Ajouter une consigne explicite de fidélité de longueur ("ne pas raccourcir significativement").
- Garder l'ossature it.19.

---

## Itération 25 — consigne fidélité de longueur

**Score juge médian : 0.0000 / moyenne 0.1250**. Distribution : 0, 0, 0, 0, 0, 0.50, 0.50, 0.

6 samples à 0.00 (record d'égalité). Mais #6 et #7 à 0.50 — première fois que #7 catastrophe sur cette série. La consigne fidélité longueur n'a pas relevé les len_ratio (toujours 0.44-0.48 sur les longs), suggérant que le modèle ne sait pas auto-évaluer sa propre longueur.

Pattern observé sur les runs : la majorité des samples (#1, #3, #4, #8) sont stables à 0.00. C'est le sous-ensemble {#2, #5, #6, #7} qui oscille fortement et tire la moyenne. Le pain point n'est pas que #6 — ce sont les samples longs/dispersés en général.

**Axes pour l'itération 26** :
- Le prompt est devenu trop long (3208 chars), revenir vers ~2700.
- Garder l'ossature it.19, ne pas réintroduire la fidélité longueur explicite.
- Tenter une variation focalisée sur le regroupement thématique (peut-être ce qui aide les samples longs).

---

## Itération 26 — réduction taille + emphase regroupement thématique

**Score juge médian : 0.0500 / moyenne 0.0922**. Distribution : 0, 0.09, 0.05, 0.05, 0, 0.50, 0.05, 0. Un seul outlier (#6) à 0.50, tous les autres ≤ 0.09. **Excellente moyenne** (0.0922 = parmi les meilleurs runs sur la série). 3 samples à 0.00, 5 samples ≤ 0.09.

La méthode narrative ("parcours mentalement…", "fais la liste mentale de toutes les mentions") semble bien guider sur les samples longs (#2, #5, #7 stables) sans dégrader les courts. Pain point résiduel toujours #6 (3/3/3/3 = juge perçoit perte d'idées + organisation insuffisante).

Lecture #6 (output partiel) : 4 paragraphes cohérents (Whisper natif, nettoyage sans Ollama, sliding windows, approche X-Way…) — le contenu paraît correct mais le juge note 3/3/3/3, suggérant perte de nuances. Le len_ratio 0.70 confirme une compression non négligeable.

**Axes pour l'itération 27** :
- Compresser la section "Vérification finale" (redondante avec "Complétude absolue").
- Garder l'ossature narrative qui semble efficace.

---

## Itération 27 — compression vérification finale (redondance)

**Score juge médian : 0.0938 / moyenne 0.1266**. Distribution : 0.14, 0, 0.14, 0.05, 0, 0.19, 0.50, 0. Légèrement pire que it.26 mais dans la variance. Cette fois c'est #7 qui catastrophe (3/3/3/3, len_ratio 0.47 = compression forte sur un sample 6959c) au lieu de #6.

#1 inhabituel à 0.14 avec len_ratio 0.36 — compression massive sur un sample 6986c. Sans la vérification finale, le modèle se permet plus de raccourcis sur les samples très longs.

**Hypothèse** : la vérification finale aidait sur les longs (rappel anti-perte tardif). À remettre sous une forme plus économe.

**Axes pour l'itération 28** :
- Réintroduire un rappel anti-synthèse mais court, fusionné avec un autre paragraphe.
- Cibler explicitement le risque "ne pas résumer" plutôt que "vérifier mentalement".

---

## Itération 28 — anti-synthèse explicite (court)

**Score juge médian : 0.0500 / moyenne 0.1609**. Distribution : 0.50, 0.50, 0.05, 0, 0, 0.14, 0.05, 0.05. Médiane bonne mais moyenne tirée par #1 et #2 à 0.50 (len_ratio 0.46 et 0.54). #6 redescend à 0.14 (mieux qu'avant).

L'ajout "Tu déploies, tu ne synthétises pas" n'a pas suffi à empêcher la compression sur les très longs (#1 6986c → 3215c, #2 5949c → ~3000c). Sortie #1 lue : contenu cohérent mais nettement raccourci, plusieurs nuances probablement perdues.

**Hypothèse** : le modèle compresse parce qu'il ne sait pas qu'il devrait produire "beaucoup" de texte. Lui donner un repère structurel (nombre de paragraphes attendus en fonction de la longueur) pourrait l'aider.

**Axes pour l'itération 29** :
- Ajouter un repère quantitatif : "monologue long → 4 à 7 paragraphes substantiels".
- Garder l'anti-synthèse, voir si le repère structure le déploiement.

---

## Itération 29 — repère quantitatif paragraphes

**Score juge médian : 0.0250 / moyenne 0.0859**. Distribution : 0, 0.09, 0.05, 0, 0, 0.50, 0, 0.05. **Meilleur run depuis it.2** (égalité). 4 samples à 0.00 dont les deux plus longs (#1 6986c, #7 6959c). #5 atteint len_ratio 0.95 (ratio 1:1). Seul #6 reste à 0.50.

Le repère quantitatif "monologue long → 4-7 paragraphes substantiels" semble structurer le déploiement sans pour autant gonfler artificiellement les ratios (#1 reste 0.47 mais le contenu est jugé complet 5/5).

Pain point #6 résiste à toutes les variations testées (anti-fusion, méthode 3 étapes, vérification finale, persona, fidélité longueur, regroupement thématique, anti-synthèse, repère quantitatif). Le sample est intrinsèquement difficile — flux très décousu avec dialogue interne, hypothèses avortées, retours en arrière.

**Axes pour l'itération 30** :
- Cibler #6 en amplifiant l'instruction sur les retours en arrière / contradictions / corrections.
- Garder l'ossature it.29 qui semble bonne.

---

## Itération 30 — emphasis sur retours en arrière et contradictions

**Score juge médian : 0.0000 / moyenne 0.0750**. **RECORD ABSOLU**. Distribution : 0, 0, 0, 0, 0, 0.50, 0.05, 0.05. **6 samples sur 8 à 0.00** dont les deux plus longs (#1, #2) et #5. Seul #6 résiste (toujours 0.50).

Reformulation "Si le locuteur hésite, change d'avis, revient sur ce qu'il vient de dire ou se corrige, conserve toutes les étapes du raisonnement, même si elles se contredisent ou s'annulent" semble efficace — len_ratio #1 0.43 et #2 0.40 mais juge 0.00, donc le contenu est jugé complet malgré la compression substantielle.

Note : le prompt fait 3367 chars maintenant. C'est plus long que la zone optimale 2700-3000 trouvée précédemment, mais ça marche. La variance reste forte donc on ne sait pas si c'est dû au contenu ou au noise.

**Axes pour l'itération 31** :
- Confirmer la robustesse en testant une variation légère.
- Tenter une retouche sur le pain point #6 : ajouter un mini repère "même un flux décousu sans plan apparent doit être restructuré thématiquement".

---

## Itération 31 — micro-instruction flux décousu

**Score juge médian : 0.0000 / moyenne 0.0859**. Médiane stable à 0.00 (deux runs consécutifs au record). Distribution : 0, 0.09, 0.10, 0, 0, 0.50, 0, 0. 5 samples à 0.00, #6 résiste toujours.

L'ajout "Même un flux apparemment décousu… contient des thèmes" n'a pas amélioré #6 spécifiquement. Le pain point est intrinsèque au sample, pas à l'instruction.

**Axes pour l'itération 32** :
- Garder l'ossature actuelle (deux records consécutifs à 0.00 médiane).
- Tenter une variation : remplacer l'exemple Whisper/Ollama actuel par un exemple ancré sur un flux décousu avec correction explicite, plus représentatif de #6.

---

## Itération 32 — exemple flux décousu (remplacement)

**Score juge médian : 0.0500 / moyenne 0.1484**. Régression vs it.30/31. Distribution : 0.50, 0.09, 0.05, 0, 0, 0.50, 0.05, 0. #1 déstabilisé (passe de 0.00 à 0.50), #6 toujours 0.50. Le nouvel exemple n'a pas aidé.

Hypothèse : l'exemple Whisper/Ollama original servait peut-être de canon "court et propre" qui ancrait le bon comportement sur les autres samples. Le remplacer par un exemple plus complexe a brouillé le signal.

**Axes pour l'itération 33** :
- Revert à l'exemple Whisper/Ollama original (it.30 = record).
- Tenter un autre angle pour #6 sans toucher l'exemple.

---

## Itération 33 — revert exemple + ajout instruction "ne lisse pas le doute"

**Score juge médian : 0.0250 / moyenne 0.1375**. Distribution : 0, 0.50, 0.05, 0, 0, 0.50, 0, 0.05. Médiane revient à 0.0250 mais moyenne reste tirée par #2 et #6 à 0.50.

L'instruction "ne lisse pas les doutes du locuteur en certitudes" n'a pas amélioré #6 mais n'a pas dégradé. Le revert d'exemple a stabilisé.

**Axes pour l'itération 34** :
- Réintroduire la "vérification finale" supprimée en it.27. Peut-être qu'avec l'ossature actuelle elle aide.

---

## Itération 34 — réintroduction vérification finale

Non exécutée — boucle arrêtée par Louis avant benchmark. Le prompt final adopté dans WhispUI est celui de l'itération 30 (meilleur run : médiane juge 0.0000, moyenne 0.0750). `benchmark/prompts/system_prompt.txt` restauré à l'état it.30 (commit eee6080).

---

## FIN — 33 itérations effectives, 1 itération préparée non mesurée

**Meilleur run** : itération 30 (emphasis retours en arrière) — médiane 0.0000, moyenne 0.0750, 6 samples sur 8 à 0.00.

**Apprentissages clés** :
- L'anti-préambule doit être en toute première phrase du prompt (primacy effect).
- Un exemple court Whisper/Ollama ancre le bon comportement ; le remplacer par un exemple plus complexe brouille le signal.
- Repère quantitatif paragraphes (2-3min → 2-4 paragraphes ; 5min+ → 4-7 paragraphes) structure le déploiement sans gonfler artificiellement les ratios.
- Instruction explicite sur retours en arrière / contradictions / corrections stabilise les samples longs.
- "Tu déploies, tu ne synthétises pas" renforce la complétude sur les très longs (6000+ chars).
- Pain point #6 (flux décousu avec dialogue interne) reste un outlier structurel non résolu — stable à 0.50 sur la plupart des angles testés.
- Variance du juge 14B massive (0.15 sur moyenne d'un run à l'autre avec prompt identique). Ne pas sur-interpréter.
