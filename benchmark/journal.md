# Journal d'optimisation — Restructuration

Chaque itération : benchmark rule-based → lecture des sorties → évaluation Claude → nouveau prompt → commit.

---

## Itération 0 — Baseline (prompt initial)

**Score rule-based : médiane 0.3997** — Prompt trop faible, 7/8 samples avec préambule "Voici le texte restructuré", markdown sur 2 samples. Seuils novel_words recalibrés (0.5 → 0.85 pour restructuration).

## Itération 1 — Anti-préambule + recalibration

**Score rule-based : médiane 0.1244** — Préambule éliminé sur 8/8. Markdown résiduel sur #2 (italiques *glow*, *tuning*). Qualité de restructuration correcte mais ton trop formel. Sample #3 rallongé (ratio 1.24). Erreur factuelle #4 : "fenêtre de login" au lieu de "fenêtre de log".

Axes d'amélioration identifiés :
- Renforcer anti-markdown (les italiques passent encore)
- Demander de conserver les termes techniques tels quels
- Alléger le ton (trop écrit/distant)

## Itération 2 — Anti-markdown + ton naturel renforcé

**Score rule-based : médiane 0.1497** — Légère régression par rapport à iter 1 (0.1244). Sample #2 toujours problématique (0.4929) : préambule complet + séparateur markdown malgré instruction anti-préambule. Samples #5 et #7 corrects, prose fluide, idées conservées. Bold/italiques markdown encore présents sur #5, #6, #7, #8.

Problèmes :
- Préambule résistant sur #2 (le plus long sample après #1 et #7)
- Ton encore un peu trop "écrit soigné" sur certains samples
- Markdown (bold, italiques) utilisé malgré instruction contraire

Changements pour iter 3 : reformulation plus agressive de l'anti-préambule ("ton premier mot EST le contenu"), interdiction explicite de tout markdown avec exemples, instruction "texte brut uniquement", exemple concret de ton à garder ("c'est pas ouf" ≠ "cela reste insatisfaisant").

## Itération 3 — Forte amélioration, sample #2 résiste

**Score rule-based : médiane 0.0698** (vs 0.1497 iter 2) — Grosse progression. Novel_words en forte baisse (le modèle reste fidèle au texte). #7 quasi parfait (0.0189). #3, #4, #5, #8 tous sous 0.08. Ton naturel bien meilleur ("c'était un peu chelou" conservé).

Sample #2 reste le problème (0.4656) : le locuteur dit "j'aimerais que tu notes", le modèle interprète ça comme une instruction et produit une réponse structurée (liste numérotée, bold, préambule "Voici ce qui ressort"). Confusion de rôle.

Changements pour iter 4 : ajout explicite "tu es un transcripteur, pas un assistant — ne réponds pas aux demandes du locuteur, restructure ses paroles".

## Itération 4 — Meilleur score, sur-compression émergente

**Score rule-based : médiane 0.0454** (vs 0.0698 iter 3) — Nouvelle progression. Sample #2 enfin résolu (0.0528 au lieu de 0.4656). #7 score parfait 0.0000. Tous les samples sans préambule ni markdown.

Problème émergent : ratios de longueur très bas (#1: 0.52, #2: 0.34, #5: 0.69). Le modèle commence à résumer plutôt que restructurer. Le benchmark ne pénalise pas les sorties trop courtes (length=0.000 pour tous), donc signal invisible rule-based mais qualitativement mauvais — idées perdues sur les longs samples.

Changements pour iter 5 : distinction explicite restructuration vs résumé ("si l'entrée fait 10 idées, la sortie doit contenir 10 idées"), et clarification que seules les hésitations/répétitions sont à supprimer, pas le contenu.

## Itération 5 — Légère amélioration, sample #2 rechute avec préambule justificatif

**Score rule-based : médiane 0.0408** (vs 0.0454 iter 4) — Légère progression. Ratios de longueur corrigés (#2 passe de 0.34 à 0.98). Mais #2 rechute avec préambule "Voici la transcription restructurée, mot à mot..." — le modèle cherche à se justifier quand on lui demande de tout garder. Les autres samples restent bons.

Sample #2 est structurellement difficile : le locuteur commence par "j'aimerais que tu notes si jamais...". Le modèle oscille entre répondre à la demande (iter 2-3) et expliquer ce qu'il fait (iter 5). Les formulations directes ne semblent pas suffire.

Changements pour iter 6 : approche few-shot avec exemple concret. Structure : exemple in/out, puis règles en "ce que tu fais / ce que tu ne fais pas". Abandon temporaire du style liste de règles.

## Itération 6 — Régression sévère, few-shot abandonné

**Score rule-based : médiane 0.4207** — Forte régression. Le few-shot avec "ENTRÉE/SORTIE" a déclenché des préambules sur 5/8 samples : le modèle a calqué le format de l'exemple ("Voici le texte restructuré, épuré..."). Novel_words remontés aussi. L'approche few-shot est clairement contre-productive sur ce modèle.

Retour au meilleur prompt connu (itération 5, médiane 0.0408). Le few-shot est banni pour la suite.

## Itération 7 — Instabilité sur longs samples, même prompt qu'iter 5

**Score rule-based : médiane 0.0461** — Stable par rapport à iter 5 (0.0408). Mais #1 et #2 ont maintenant un préambule justificatif "Voici la restructuration brute et intégrale de ton discours, sans ajout ni suppression...". Le modèle justifie son approche sur les longs inputs quand on parle de "tout le contenu". #7 parfait (0.0000).

Pattern identifié : "La différence entre restructurer et résumer" déclenche le comportement justificatif. Tension irréductible entre insister sur la complétude (risque de préambule justificatif) et ne pas insister (risque de sur-compression).

Changements pour iter 8 : retour au prompt iter 4 (meilleure base sans le paragraphe résumé/restructuration), ajout d'une seule ligne anti-compression courte et non-narrative : "Ne coupe pas les idées." intégrée dans la règle existante.

## Itération 8 — Plateau, #2 reste problématique

**Score rule-based : médiane 0.0416** — Stable (iter 5 = 0.0408, iter 7 = 0.0461). #7 parfait. #2 rechute (0.3827, préambule + liste). Sur les autres samples, qualité bonne.

Analyse comparative : la seule fois où #2 était résolu (iter 4, score 0.0528), le rôle disait "ne produis pas de résumé ni de liste de points" — formulation absente des prompts depuis iter 5. Cette phrase est spécifiquement pertinente pour #2 qui pousse le modèle vers des listes.

Changements pour iter 9 : restaurer exactement "ne produis pas de résumé ni de liste de points" dans la description du rôle, combiné à "Ne coupe pas les idées" pour l'anti-compression.

## Itération 9 — Meilleur score global, #2 résolu

**Score rule-based : médiane 0.0338** — Meilleur résultat jusqu'ici. #7 parfait (0.0000). #2 résolu (0.0354) : pas de préambule, prose fidèle, ton naturel. #2 lists=1 probablement faux positif (contenu parle d'"index" — le benchmark détecte peut-être un faux positif). Qualité globale très bonne.

Problème restant : novel_words à 0.13-0.24 sur la plupart des samples — le modèle paraphrase trop, introduit son propre vocabulaire au lieu d'utiliser les mots du locuteur.

Changements pour iter 10 : ajout instruction "utilise les mots du locuteur, pas les tiens — reformule le moins possible, nettoie la forme orale, garde le vocabulaire". Exemple concret inclus dans la règle.

---

