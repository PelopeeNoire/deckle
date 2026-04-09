# Prompt de reprise — Passe contenu Settings (EN + descriptions + ordre)

## Ce que tu dois faire

Repasse complètement le **contenu éditorial** de la fenêtre Settings de WhispUI : traduction française → anglais, rédaction de descriptions pour chaque page et chaque section, reconsidération de l'ordre d'apparition des pages, des sections et des cards. L'objectif n'est pas cosmétique — c'est que quelqu'un qui ouvre Settings la première fois comprenne *ce qu'il règle et pourquoi* sans avoir à deviner.

Ce n'est pas du refacto de code. Tu touches exclusivement aux `Text=`, `Header=`, `Description=`, `PlaceholderText=`, `ToolTip=`, et à l'ordre des éléments XAML (et leurs handlers C# si tu renommes des `x:Name`, mais essaie de ne pas). Pas de nouveau code fonctionnel. Pas de nouveau contrôle sauf si un texte *demande* un composant que le XAML n'a pas encore (ex. un sous-titre explicatif là où il n'y en a pas).

## Contexte — état actuel (2026-04-09)

- **SettingsWindow** vient d'être refactorée : NavigationView natif adaptatif (Left / LeftCompact / LeftMinimal), `NavigationView.AutoSuggestBox` slot (mais Louis n'est pas convaincu du placement, voir plus bas), `Frame + Page`, pas de sticky header/footer, auto-save partout, TitleBar natif `Standard` hauteur. Trois pages : `GeneralPage`, `WhisperPage`, `LlmPage`. Item footer « Logs » qui ouvre la LogWindow (pas une page).
- **WhisperPage** est câblée sur `SettingsService` avec 6 sections : Transcription, Filtrage de la parole (VAD), Décodage, Seuils de confiance, Filtres de sortie, Contexte et segmentation. Toutes en auto-save, reset-par-setting au hover, `MarkRestartPending` sur Model+UseGpu avec InfoBar « Restart required ». Bug de navigation WhisperPage **résolu** (slider XAML → code-behind).
- **GeneralPage** est un mock : items non câblés (Démarrer avec Windows, Minimiser au démarrage, Thème, HUD apparence, Raccourci). Ne pas brancher — juste retraduire et réordonner/regrouper si pertinent.
- **LlmPage** est un placeholder vide avec un texte d'attente.
- **Tout est en français.** Cible : **tout en anglais** pour matcher le ton first-party Microsoft.

## Ce que Louis a explicitement demandé

Traduction EN intégrale. Descriptions de section **et** descriptions de page, parce qu'actuellement on a des intitulés peu parlants. Exemple cité : « Chemins (avancé) » — le mot « avancé » entre parenthèses ne veut rien dire en soi, c'est un raccourci paresseux. Il faut une phrase qui dit ce que fait concrètement la section et *pour qui*. Même exercice pour toutes les autres sections et pour les trois pages.

Reconsidérer **l'ordre d'apparition** : ordre des pages dans la NavView, ordre des sections dans chaque page, ordre des cards dans chaque section. Aujourd'hui l'ordre est le résultat d'un empilement chronologique, pas d'une réflexion UX.

Louis te laisse réfléchir activement — c'est *ton* travail de designer + product. Utilise les skills.

## Skills à invoquer activement

**Obligatoire** avant d'écrire quoi que ce soit :

- **`product-management:write-spec`** ou **`product-management:brainstorm`** — cadrer ce qui va où, établir un critère d'ordre (par fréquence d'usage ? par criticité ? par dépendance d'apprentissage ?), sortir de l'instinct.
- **`design:design-system`** — vérifier la cohérence avec les conventions de Settings Windows 11 : une section a-t-elle toujours un header ? une description ? une icône ? Quelle voix éditoriale (impératif ? déclaratif ? « You can… » vs « Control the… ») ?
- **`design:design-critique`** — passer une fois au-dessus des propositions pour repérer les intitulés qui mentent ou qui promettent plus que ce que le réglage fait vraiment.
- **MCP Microsoft Learn** (`microsoft_docs_search`, `microsoft_code_sample_search`) — chercher les guidelines de voix Fluent/Windows 11 pour Settings pages, et comparer avec le contenu réel de Settings Windows 11 (dans l'app, pas dans les docs). **Important** : la voix Microsoft pour Settings est *directe, deuxième personne implicite, présent, pas de jargon technique sauf strictement nécessaire*. Exemple : « Open WhispUI automatically when you sign in », pas « Automatic WhispUI startup on session opening ».
- **`engineering:documentation`** — pour la rédaction proprement dite si besoin de rigueur sur la cohérence tone/structure.

## Contraintes de contenu

**Règle de voix** : seconde personne implicite, présent, direct, concis. Tu décris *ce que le réglage fait pour l'utilisateur*, pas *ce que le paramètre technique contrôle*. Exemple :

- Mauvais : « Threshold for the VAD speech detection algorithm »
- Bon : « Set how confident WhispUI must be before it considers a sound to be speech. Higher values filter out more background noise but may miss quiet speech. »

**Descriptions de section** : une phrase, deux max. Doit répondre à « pourquoi cette section existe et quand j'y toucherais ». Pas de « advanced », « experimental » en parenthèses — si c'est avancé, dis-le dans la phrase (« For power users who want to fine-tune… »).

**Descriptions de page** : un paragraphe court en tête de page, sous le titre H1, avant la première section. Doit donner le cadre mental de la page entière.

**Descriptions de card** : une phrase qui dit *ce que fait le réglage* et *quand tu voudrais y toucher*. Pas de tautologie (« Enable X » / description: « Enables X »).

**Terminologie technique** : garder les termes techniques *exacts* quand ils sont les vrais noms des paramètres whisper.cpp (Temperature, Logprob threshold, VAD, etc.) — un utilisateur averti doit pouvoir faire le lien avec la doc whisper.cpp. Mais l'explication autour doit être en langage clair.

## Réflexion sur l'ordre

Règle de base : **fréquence d'usage et prérequis d'apprentissage** décroissants. Ce qu'un nouvel utilisateur touche en premier, en haut. Ce qui nécessite de comprendre le reste, en bas. Exemple de question à se poser pour WhisperPage :

- Un nouvel utilisateur touchera-t-il « Transcription model » avant « Logprob threshold » ? Oui → Transcription en haut, Seuils de confiance en bas. (Déjà le cas.)
- La section VAD est-elle un prérequis des Seuils de confiance, ou l'inverse ? VAD est autonome, Seuils dépendent de la compréhension du pipeline. Donc VAD avant Seuils. (Déjà le cas.)
- « Chemins » contient un seul champ (dossier des modèles) qui n'est touché qu'une fois à l'install. Doit-il vraiment être dans un SettingsExpander séparé en première position, ou tout en bas, ou dans une page « Storage » dédiée ? **À trancher.**

Pour l'ordre des **pages** dans la NavView : General d'abord (classique), puis Whisper (cœur du produit), puis LLM (futur, encore vide). Peut-être que ça reste comme ça, mais vérifie qu'il n'y a pas une meilleure organisation — par exemple, une page « Recording » qui regroupe hotkey + HUD + démarrage pourrait avoir plus de sens qu'une page « General » fourre-tout.

## Question ouverte de la session précédente — à traiter si tu veux

Louis n'est pas convaincu du placement de l'**AutoSuggestBox** dans le slot `NavigationView.AutoSuggestBox`. Trois options avaient été évoquées :

- **A.** Search globale dans la `TitleBar.Content`, résultats flottants, clic → navigation + `StartBringIntoView()` sur la card cible. C'est ce que fait Settings Win11. Demande d'indexer les cards (dico `{card → mots-clés}`).
- **B.** Un onglet « Search results » dédié dans la NavView.
- **C.** Search dans la sidebar, items contextuels sous chaque page. (Déconseillé.)

Tu peux proposer une recommandation dans ta passe contenu si ça s'impose, mais **ne la code pas** sans validation de Louis. Réserve ça à une session suivante.

## Livrables attendus de la session

1. **Propositions de renommage / traduction** en anglais pour chaque page, section, card, placeholder, tooltip. Livré sous forme de diff XAML, pas de tableau.
2. **Descriptions de page** ajoutées en tête de chaque Page (nouveau `TextBlock` sous le titre H1, style `BodyTextBlockStyle`, couleur `TextFillColorSecondaryBrush`).
3. **Propositions d'ordre** pour les pages de la NavView, les sections de chaque page, les cards de chaque section, argumentées brièvement dans la réponse (pas dans le code).
4. **Justifications éditoriales** — pour chaque renommage non-trivial, dire *pourquoi* ce choix de mot. Louis veut comprendre ta logique, pas subir ton goût.
5. **Rapport éditorial** sous forme de message récap à la fin : ce qui a changé, les arbitrages pris, les doutes qui restent, et les éventuels points où tu n'as pas tranché seul.

## Ce que tu ne fais PAS

- Pas de build — Louis build lui-même.
- Pas de nouveau câblage fonctionnel sur GeneralPage ou LlmPage.
- Pas de nouveaux contrôles (pas de InfoBar, pas de TeachingTip, pas de Expander supplémentaire) sauf si un texte *exige* un élément qui n'existe pas encore.
- Pas de refonte de `SettingsWindow.xaml` (NavView, TitleBar, Frame) — c'est le refacto de la session précédente, stable. Tu peux toucher à l'ordre des `NavigationViewItem` et à leurs `Content`, pas à la structure.
- Pas de commit automatique à la fin — attends le go de Louis.

## Fichiers à lire en entrée (ordre suggéré)

1. `CLAUDE.md` (racine) — doctrine projet
2. `whisp-ui/WhispUI/CLAUDE.md` — doctrine WhispUI, pièges WinUI 3, build
3. `whisp-ui/WhispUI/docs/settings.md` — état Settings, bugs résolus, finitions restantes
4. `whisp-ui/WhispUI/Settings/GeneralPage.xaml` — 3 sections mock
5. `whisp-ui/WhispUI/Settings/WhisperPage.xaml` — 6 sections câblées
6. `whisp-ui/WhispUI/Settings/LlmPage.xaml` — placeholder
7. `whisp-ui/WhispUI/SettingsWindow.xaml` — NavView, 3 items + footer Logs

Et en complément, si tu as des doutes de voix : comparer avec quelques pages de Settings Windows 11 ouvertes sur la machine ou via le MCP Microsoft Learn (recherche « Windows Settings app writing guidance »).

## Souvenir à charger

Lire ces mémoires avant de commencer :
- `project_roadmap.md`
- `project_whisper_settings.md`
- `project_winui3_slider_bug.md`
- `feedback_microsoft_first.md`
- `feedback_tokens_ciblé.md`
