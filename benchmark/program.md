# Program — Optimisation du prompt Restructuration

## Objectif

Optimiser le system prompt dans `system_prompt.txt` pour que le modèle Ministral 14B
instruct produise une restructuration fidèle et lisible de transcriptions vocales françaises longues.

## Contraintes

Le prompt doit faire respecter ces règles au modèle :

1. **Zéro perte d'idée** — chaque concept, détail, nuance présent dans l'entrée doit se retrouver dans la sortie. C'est la contrainte reine.
2. **Suppression du bruit oral** — hésitations, répétitions, faux départs, remplissages ("euh", "du coup", "voilà"), phrases avortées.
3. **Restructuration en paragraphes** — les idées sont réorganisées par thème/logique, pas forcément dans l'ordre chronologique de parole.
4. **Français écrit clair** — le registre oral est transformé en prose écrite lisible, sans jargon inutile.
5. **Pas de préambule** — le modèle commence directement par le texte restructuré.
6. **Pas de markdown** — pas de bullet, header, bold, code block. Texte brut en paragraphes.
7. **Pas d'invention** — aucune idée, concept ou information ajouté qui n'existe pas dans l'entrée.

## Stratégies à explorer

- Insister sur la complétude (toutes les idées doivent être présentes) vs la concision
- Tester des instructions de structuration (par thème, par ordre logique)
- Ajouter un exemple few-shot court montrant entrée orale → sortie restructurée
- Varier la formulation : "restructure" vs "réécris" vs "transforme en texte écrit"
- Tester des rappels anti-perte ("vérifie que chaque idée de l'entrée est dans ta sortie")
- Reformuler en instructions impératives courtes

## Ce qui ne doit PAS changer

- Le prompt doit rester en français
- Le prompt ne doit pas dépasser 500 mots (coût en tokens)
- Le format de sortie reste du texte brut en paragraphes
