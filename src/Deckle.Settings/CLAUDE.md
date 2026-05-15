# CLAUDE.md — Deckle.Settings

Shell UI Settings de l'app. Héberge la `SettingsWindow` (NavigationView Auto adaptatif + Frame de pages), les pages owned (General, Recording, Diagnostics), les dialogs de consentement (corpus logging, paste opt-in, autorewrite rules), la racine de persistance (`SettingsService` pour les settings non-modulaires) et le registry de delegates `SettingsHost` que les modules métier consomment pour appeler des actions côté shell (theme broadcast, level window propagation, restart, accès parent-window pour les dialogs cross-module).

Les pages modulaires (`WhisperPage` dans `Deckle.Whisp`, `LlmPage` dans `Deckle.Llm`, et la future `AmbientPage` dans `Deckle.Lighting.Ambient`) ne vivent pas ici — elles sont possédées par leur module respectif et résolues via `Type.GetType(tag)` à partir du `Tag` du `NavigationViewItem` (par exemple `Tag="Deckle.Whisp.WhisperPage, Deckle.Whisp"`).

L'architecture détaillée (Auto adaptive behavior, structure des 5 pages canoniques, footer Logs, `FolderPickerCard` pattern unique, `SettingsExpander` parent pour groupes de sliders denses, conventions de header H1 par page, auto-save partout pas de Cancel/Save global, persistance per-module avec 5 services) vit dans [docs/reference--settings-architecture--2.0.md](../../docs/reference--settings-architecture--2.0.md). Lire la fiche avant de toucher à la structure d'une page ou à un pattern de contrôle Settings.

## Patterns non négociables

**Auto-save partout.** Aucune page Settings n'a de bouton Save ou Cancel. Chaque contrôle propage sa valeur au ViewModel à chaque changement, et le ViewModel pousse au `SettingsService` correspondant qui sérialise immédiatement (debounce léger via `JsonSettingsStore`). Conséquence : pas de modèle « dirty », pas de prompt « unsaved changes » à la fermeture, pas de Cancel button. Cohérent avec le pattern Windows 11 Settings (l'app system).

**NavigationView Auto.** `PaneDisplayMode="Auto"` laisse le contrôle gérer la transition Left (≥1008 dip) → LeftCompact (641-1007) → LeftMinimal (≤640). Pas de breakpoint custom, pas de toggle manuel. C'est le comportement adaptive natif documenté Microsoft Learn.

**SettingsCard et SettingsExpander.** Tous les contrôles de réglage sont enveloppés dans `SettingsCard` (toggle simple, slider, ComboBox) ou `SettingsExpander` (groupe de réglages liés ou liste éditable). C'est le pattern Win11 Settings et c'est ce qui produit l'espacement, le sectionnement et les animations canoniques. Aucun `StackPanel` ou `Grid` custom enveloppant un contrôle de réglage à la racine d'une page — toujours `SettingsCard`.

**FolderPickerCard pattern.** Pour les chemins de dossier (modèles, corpus, native runtime), un `SettingsCard` qui combine un `TextBlock` du chemin actuel + bouton `Browse...` qui ouvre `FolderPicker` + bouton `Reset` qui restaure la valeur par défaut. Le code de ce pattern est factorisé dans `Deckle.Settings/Controls/FolderPickerCard.xaml(.cs)`. Réutiliser cette factorisation, ne pas dupliquer la mécanique dans chaque page.

**Header H1 par page.** Chaque page commence par un `TextBlock` style `TitleLargeTextBlockStyle` qui annonce le nom de la section (« General », « Recording », « Transcription », « Rewriting », « Diagnostics »). Pas de header sticky au scroll. Pas de sub-tab dans une page. Pas de breadcrumb. La hiérarchie reste plate sur deux niveaux : NavigationViewItem → page.

## SettingsHost registry

`SettingsHost` est une classe statique de delegates que l'app branche au boot et que les pages Settings invoquent pour les actions côté shell. Le pattern évite à `Deckle.Settings` d'avoir une référence sur le projet hôte tout en permettant à n'importe quelle page Settings d'appeler `ApplyTheme`, `ApplyLevelWindow`, `RestartApp`, `GetSettingsWindow` (pour le `hwnd` parent des dialogs), `OpenSetupWizard`. L'app pose ces hooks dans `App.OnLaunched` avant la première instanciation de fenêtre Settings.

C'est un pattern intentionnel — le registry n'est pas un Service Locator déguisé. Les delegates sont nominaux (un par capability), pas un dictionnaire de strings. Ajouter un hook signifie ajouter un champ statique typé sur `SettingsHost`, à brancher au boot, à appeler explicitement depuis la page qui en a besoin.

## Persistance per-module

Depuis l'extraction des modules en mai 2026, chaque module possède son fichier de settings sous `<UserDataRoot>/modules/<moduleId>/settings.json`. Les services concernés sont `WhispSettingsService`, `LlmSettingsService`, `CaptureSettingsService` (dans `Deckle.Audio`), `TelemetrySettingsService`, et la future `AmbientSettingsService` (dans `Deckle.Lighting.Ambient`). Le `SettingsService` historique dans ce module ne porte plus que les sections cross-modulaires (Appearance, Paste, Startup, AutoRewriteRules) sous `<UserDataRoot>/settings.json`.

La migration de l'ancien fichier combiné vers le layout per-module vit dans `SettingsBootstrap.MigrateLegacyToPerModule()`. Cette méthode tourne en tout premier dans `App.OnLaunched`, avant qu'un service ne touche son fichier — sinon le service écrirait des defaults et la migration verrait une cible déjà existante. Elle gère aussi le renommage de la section JSON `recording → capture` (héritage 2026-05-02), le dispatch de la clé JSON `capture` vers le module id `audio` (2026-05-15 rename), et la migration de dossier `modules/capture/ → modules/audio/` pour les utilisateurs déjà passés en per-module. Toute future migration de module suit ce pattern via `MigrateModuleFolder` et l'ajustement du dispatch.
