# Program — Optimisation du prompt Nettoyage

## Objectif

Optimiser le system prompt dans `system_prompt.txt` pour que le modèle Ministral 3
(3B instruct Q8) produise un nettoyage fidèle de transcriptions vocales françaises.

## Contraintes

Le prompt doit faire respecter ces règles au modèle :

1. **Pas d'invention** — aucun mot, concept ou explication qui n'existe pas dans l'entrée.
2. **Pas de restructuration** — l'ordre des phrases doit rester identique.
3. **Pas de changement de registre** — le vocabulaire oral (familier, hésitant) est conservé.
4. **Corrections minimales** — ponctuation, accents, répétitions immédiates, mots mal transcrits évidents.
5. **Pas de préambule** — le modèle commence directement par le texte nettoyé.
6. **Pas de markdown** — aucun bullet, header, bold, code block.

## Stratégies à explorer

- Renforcer les contraintes négatives (dire explicitement ce qui est interdit)
- Utiliser des exemples few-shot dans le prompt
- Reformuler en instructions courtes et impératives plutôt qu'en prose
- Ajouter un « rôle » (ex: "Tu es un correcteur orthographique minimal")
- Tester des formulations différentes de la même contrainte
- Varier l'ordre des instructions (contraintes négatives d'abord vs positives d'abord)

## Ce qui ne doit PAS changer

- Le prompt doit rester en français
- Le prompt ne doit pas dépasser 500 mots (coût en tokens)
- Le format de sortie reste du texte brut
