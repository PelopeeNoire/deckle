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
