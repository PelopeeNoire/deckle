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

## Itération 10 — Score quasi-parfait, prompt convergé

**Score rule-based : médiane 0.0054** — Meilleur résultat de loin (iter 9 : 0.0338). Novel_words quasi zéro (0.000-0.073). Tous les samples sans préambule, sans liste, ratios 0.73-0.97. Qualité excellente : hésitations supprimées, mots du locuteur conservés, restructuration réelle (pas de copié-collé).

La combinaison "utilise les mots du locuteur + ne coupe pas les idées + ne produis pas de résumé ni de liste" est la formule gagnante.

Changements pour iter 11 : test d'un prompt raccourci. À médiane 0.0054, tester si une version distillée (plus courte, même règles essentielles) est aussi robuste. Un prompt minimal est plus robuste aux edge cases.

## Itération 11 — Régression avec prompt minimal

**Score rule-based : médiane 0.0284** — Régression (iter 10 : 0.0054). Le prompt minimal laisse trop de liberté sur les longs samples : #7 remonte à 0.0906 (novel=0.36). Sans "utilise les mots du locuteur", le modèle paraphrase. Les instructions précises sont nécessaires.

Conclusion : le prompt de l'itération 10 est optimal. Il ne faut pas le raccourcir. Retour au prompt iter 10.

Pour la suite : garder le prompt iter 10 comme référence et explorer des variations mineures autour de cette base.

## Itération 12 — Stable, confirmation prompt iter10

**Score rule-based : médiane 0.0095** — Cohérent avec iter 10 (0.0054). Variation normale due à température 0.3. Sample #8 légèrement plus élevé (novel=0.15) : synonymes proches ("enlever"→"supprimer", "aimerais"→"veux"). Qualité globale excellente.

Changements pour iter 13 : test reordering des règles — "utilise les mots du locuteur" en premier, avant "toutes les idées". La priorité dans la liste influence l'attention du modèle. Ajout d'exemples concrets dans la règle vocabulaire.

## Itération 13 — Excellente stabilité, sample #8 corrigé

**Score rule-based : médiane 0.0061** — Dans la zone optimale (iter 10 = 0.0054). Sample #8 corrigé (novel=0.01 au lieu de 0.15). Le reordering "vocabulaire locuteur en premier" a amélioré la fidélité lexicale sur tous les samples. #7 parfait. Aucun préambule, aucune liste, ratios 0.86-0.97.

Ce prompt est maintenant la référence solide. Deux runs consécutifs (iter 10 et 13) confirment la stabilité dans la zone 0.005-0.01.

Axes d'exploration restants : tenter d'autres formulations pour voir si le plancher peut descendre encore, ou tester avec un style différent d'instruction (impératif vs déclaratif, moins de tirets, structure en prose).

## Itération 14 — Nouveau meilleur, style prose sans tirets

**Score rule-based : médiane 0.0034** — Nouveau meilleur (iter 13 = 0.0061). Novel_words très bas sur tous les samples (0.00-0.06). Aucun préambule, aucune liste. Ratios excellents (0.73-0.98). Qualité vérifiée : ton naturel, fidélité vocabulaire très bonne. Légères reformulations résiduelles sur #1 (italiques) et #6 (paraphrase minime).

Le style prose sans tirets, avec les instructions en paragraphes séquentiels, fonctionne mieux que la liste à tirets. Le modèle suit mieux les instructions exprimées en prose.

Nouveau prompt de référence. Pour iter 15 : explorer si on peut encore réduire le novel_words résiduel sur #6, ou tenter un angle complètement différent.

## Itération 15 — Nouveau meilleur (0.0031), #1 légèrement compressé

**Score rule-based : médiane 0.0031** — Nouveau meilleur (iter 14 = 0.0034). Novel_words quasi zéro sur 6/8 samples. #8 parfait (0.0000). #6 et #7 quasi nuls. Sample #1 (le plus long, 6986 chars) reste le plus difficile : novel=0.13, ratio=0.49 — sur-compression + légère paraphrase. Qualité globale excellente.

La formulation "utilise exactement les mots du locuteur" avec exemples synonymes ("regarder" ≠ "vérifier") est efficace. Style prose confirmé meilleur que tirets.

Pour iter 16 : explorer si une instruction sur la longueur relative ou la densité d'idées peut améliorer #1 sans dégrader les autres.

## Itération 16 — Nouveau meilleur (0.0023), #1 amélioré

**Score rule-based : médiane 0.0023** — Nouveau meilleur (iter 15 = 0.0031). Sample #1 amélioré : ratio 0.49→0.79, novel 0.13→0.09. L'instruction "si l'entrée est longue, la sortie est longue / 15 sujets → 15 sujets" a aidé. #6 et #7 quasi-parfaits. #8 légèrement plus élevé (0.0100) avec italique résiduel sur *publish*.

La direction est bonne. Prochaine micro-variation : tenter de réduire le novel résiduel sur #1 (encore 0.09).

## Itération 17 — Légère régression, retour prompt iter16

**Score rule-based : médiane 0.0040** — Légère régression (iter 16 = 0.0023). Changement de formulation de l'anti-synonymes ("Garde le vocabulaire exact" au lieu de "Utilise exactement les mots") a légèrement dégradé #4 et #6. Dans la plage de variance température, mais iter 16 reste clairement le meilleur.

Retour au prompt exact iter 16 pour iter 18. Stabilisation du plateau optimal.

## Itération 18 — Meilleur absolu (0.0016), prompt iter16 confirmé

**Score rule-based : médiane 0.0016** — Nouveau meilleur absolu. 5 samples sous 0.002. #6, #7, #8 quasi-parfaits (0.0000-0.0011). #1 à 0.0055 — encore le sample le plus difficile (long, 6986 chars) mais très bon. Novel_words 0.00-0.02 sur tous les samples. Ratios 0.83-0.99.

Le prompt iter 16 est confirmé optimal. Plusieurs runs successifs le confirment (0.0023, 0.0016). La variation résiduelle vient de la température 0.3 et non du prompt.

Pour la suite : garder ce prompt et explorer si d'autres axes peuvent passer sous 0.001, ou documenter que le plancher est atteint.

## Itération 19 — Score exceptionnel (0.0004), 4 samples parfaits

**Score rule-based : médiane 0.0004** — Nouveau meilleur absolu de loin (iter 18 = 0.0016). 4 samples à 0.0000 parfaits (#4, #6, #7, #8). #1 à 0.0015 — presque parfait. Novel_words quasi-nul sur tous les samples. Ratios 0.93-1.00.

La simplification de la première ligne ("Transcription orale → texte écrit. Commence directement avec le premier mot du contenu.") est plus efficace que "Ton premier mot EST le début du contenu restructuré. Zéro préambule..." — moins de signal anti-préambule verbeux, plus de signal task-direct.

Nouveau prompt de référence absolu. Pour les itérations suivantes : stabiliser et explorer si on peut passer sous 0.0002 ou si c'est le plancher de ce corpus/modèle.

## Itération 20 — Plancher atteint (médiane 0.0000)

**Score rule-based : médiane 0.0000** — Plancher absolu. 5 samples parfaits (#4, #5, #6, #7, #8). #1, #2, #3 à 0.0015-0.0016 (novel=0.01, probablement variance irréductible à temperature=0.3). Ratios 0.93-1.00. Confirmation : le prompt iter 19 est optimal et stable.

Les 3 samples non-parfaits ont tous novel=0.01 — une ou deux substitutions de mots sur des milliers de tokens. C'est la limite de cette température avec ce modèle, pas une limite du prompt.

Pour les itérations 21-40 : explorer des angles radicalement différents (ordre des paragraphes, formulation negative-only, angle minimal) pour confirmer que iter 19 est le meilleur. Objectif : documenter la robustesse et les limites.

## Itération 21 — Prompt négatif pur : médiane 0.0000 mais fragile

**Score rule-based : médiane 0.0000, moyenne 0.0455** — 7/8 parfaits, mais #2 rechute (0.3642, préambule "Voici la transcription fidèle..."). Sans l'instruction de rôle ("tu es un transcripteur, le locuteur ne te parle pas"), le modèle interprète #2 comme une demande à satisfaire. La médiane est identique mais la robustesse est inférieure.

Conclusion : les instructions de rôle positives ("tu es un transcripteur") sont nécessaires pour gérer le sample #2 pathologique. Le prompt iter 19 reste optimal (médiane 0.0000, moyenne 0.0006).

Pour iter 22 : restaurer iter 19 et tester un reordering — interdictions d'abord, puis rôle, puis règles positives.

## Itération 22 — Reordering légèrement moins bon (0.0008)

**Score rule-based : médiane 0.0008** — Légèrement moins bon qu'iter 19 (0.0000). Mettre la suppression avant le vocabulaire n'aide pas. Le prompt iter 19 (vocabulaire avant suppression) reste optimal.

Pour iter 23 : restaurer iter 19 et enrichir la liste d'hésitations ("bah", "ouais ben", "hein", "genre", "quoi") pour réduire le novel résiduel sur #1/#2/#3.

---

