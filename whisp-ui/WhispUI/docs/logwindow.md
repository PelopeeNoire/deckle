# LogWindow — refonte v3 + debug logs A→Z

## Structure générale

Refonte basée sur design Figma (`fAjzDDUpFW1InvLl5xL83R`, node `98:9790`). Fenêtre classique (`OverlappedPresenter`), Close→Cancel+Hide, **thème système** (plus de `RequestedTheme="Dark"` forcé), `SystemBackdrop = MicaBackdrop`.

Depuis le refacto natif (2026-04-09) : utilise `Microsoft.UI.Xaml.Controls.TitleBar` natif (Windows App SDK 1.8), drag passthrough custom supprimé, `RefreshTitleBarButtonColors` supprimé. Caption buttons Tall via `AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall`.

## Title bar

Custom (`ExtendsContentIntoTitleBar=true`) en 3 colonnes : icône + titre draggable à gauche, `AutoSuggestBox` de recherche centrée, réserve 138 px à droite pour les caption buttons système. Drag region limitée à la colonne gauche (`SetTitleBar(AppTitleBarLeftDrag)`) pour que la SearchBox reçoive les clics.

SearchBox responsive : single breakpoint 580. Full ≥580, icône loupe sinon (même pattern que SettingsWindow).

## Command bar

Sous la title bar, 2 zones :
- **Gauche** — `SelectorBar` Full / Filtered / Critical
- **Droite** — `CommandBar` (icônes Segoe Fluent) avec deux groupes séparés par `AppBarSeparator` : édition (Copy E8C8 / Save E74E / Clear E74D) puis affichage (Auto scroll E70D / Wrap E751 toggles)

Cap 5000 entrées. Tray « Logs » → `ShowAndActivate`.

## Modèle de données

`enum LogLevel { Verbose, Info, Step, Warning, Error }` (public). API thread-safe.

Deux collections :
- `_entries` — `List`, tampon complet
- `_visible` — `ObservableCollection` bindée à `LogItems`

Filtre = `Matches()` qui combine selector (Filtered masque Verbose, garde le reste ; Critical = Warning + Error seuls) + recherche live (`IndexOf` case-insensitive). `ApplyFilter()` rebuild `_visible`.

Sur overflow, on retire la plus vieille entrée des **deux** collections (`LogEntry` est une `class`, ref equality, pas de collision). **Copy/Save opèrent sur `_visible`** — l'utilisateur copie/exporte ce qu'il voit.

## Couleurs — ThemeDictionaries

Depuis le refacto natif : bindées via `ThemeResource` dans `Application.Resources.ThemeDictionaries` (plus de snapshot constructeur, plus de `RefreshBrushes()` ni `OnThemeChanged`).

- **Verbose** → blanc neutre (hérite de `TextFillColorPrimaryBrush`)
- **Info** → `LogInfoBrush` custom (bleu sémantique Fluent `#005FB7` light / `#60CDFF` dark, brush construit en code car `SystemFillColorAttentionBrush` de la theme dictionary peut résoudre sur l'accent système — chez Louis, gris)
- **Step** → `SystemFillColorSuccessBrush` (vert — **pas** l'accent système qui peut être gris)
- **Warning** → `SystemFillColorCautionBrush` (orange)
- **Error** → `SystemFillColorCriticalBrush` (rouge)

Theme switch runtime OK automatiquement.

## Wrap — piège scroll horizontal

Le toggle swap `NoWrapTemplate`/`WrapTemplate` **et** bascule `HorizontalScrollBarVisibility` entre `Auto` et `Disabled`. Sinon `TextWrapping="Wrap"` ne s'applique pas — le `ScrollViewer` mesure son contenu en largeur infinie tant que le scroll horizontal est autorisé.

## Debug logs A→Z — sémantique des 5 niveaux

3 modes de filtre dans le `SelectorBar` (Full / Filtered / Critical).

**Verbose (blanc)** — bruit / durée de vie. Heartbeats RECORD `+Ns capturés`, dumps par segment Whisper, plomberie clipboard (`GlobalAlloc` / `OpenClipboard`), plomberie PASTE (`cible attendue` / `SetForegroundWindow` / `contrôle focusé`), `[DONE]` recap timings **non vérifié**. Masqué en Filtered.

**Info (bleu)** — jalons rares et importants : `Enregistrement démarré`, `Capture terminée`, `Texte copié`, `Ctrl+V envoyé`.

**Step (vert)** — jalons rares et **vérifiés**. Exactement deux par session normale : `Modèle chargé en N ms` (init, vérifié `_ctx ≠ 0`) et `Bout en bout OK — N chars collés dans <cible>` (émis uniquement si `PasteFromClipboard` retourne `true`).

**Warning (orange)** — refus explicites avec mode opératoire (ex. paste refusé parce que la cible est dans WhispUI lui-même : « le presse-papier contient le texte — colle manuellement avec Ctrl+V »).

**Error (rouge)** — exceptions, fallback critique, CRASH filets globaux.

Filtered masque uniquement Verbose. Critical = Warning + Error.

**À refaire post-pipeline-monobloc** : certains événements historiques n'existent plus (`Chunk N extrait`, `Mémoire passée au chunk suivant`, `Chunk N → texte recollé`). Voir `pipeline-transcription.md` pour la nouvelle cartographie (`RECORD` heartbeat 5 s + capture terminée, `TRANSCRIBE` audio reçu + Verbose par segment via callback + récap final).

## Régressions connues

- **Caption buttons Tall** — déjà appliqué via `AppWindow.TitleBar.PreferredHeightOption`. Si regression : title bar 48 px + `ExtendsContentIntoTitleBar=true` seuls font ressortir les caption buttons à 32 px (gap visuel).
- **Drag region partielle** — seule la colonne icône+titre est draggable. La bande vide entre la SearchBox et les caption buttons ne déplace pas la fenêtre. Fixable via `InputNonClientPointerSource` si besoin.
- **CommandBar overflow flyout pleine largeur** — limite WinUI connue, non résolue.

## Filets de diagnostic globaux

Dans `App` : `Application.UnhandledException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` → `DebugLog` (préfixes `CRASH` / `CRASH-AD` / `CRASH-TS`).
