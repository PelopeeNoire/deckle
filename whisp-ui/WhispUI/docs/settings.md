# SettingsWindow — refacto natif + WhisperPage câblée

Refacto natif livré et validé runtime 2026-04-09. Plan d'origine : `C:\Users\Louis\.claude\plans\bubbly-swimming-thacker.md`.

## TitleBar natif

`Microsoft.UI.Xaml.Controls.TitleBar` natif (Windows App SDK 1.8). Caption buttons Tall **obligatoirement réactivés** via `AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall` — le contrôle TitleBar ne gère **pas** cette hauteur tout seul. Icône app via `ImageIconSource` nommé.

## NavigationView adaptatif — 2 breakpoints

- **≥960 `PaneDisplayMode=Left`** (240, inline, figé, pas de hamburger)
- **<960 `LeftCompact`** (rail 48, hamburger dans la TitleBar via `PaneToggleRequested` → `Nav.IsPaneOpen`, overlay au déploiement)

`LeftMinimal` supprimé (inatteignable, `PreferredMinimumWidth=480`). Bascule live côté code-behind dans `ApplyAdaptiveLayout(width)`. Search box : full ≥580, icône loupe sinon (expand au clic).

Contenu : `NavigationView.MenuItems` = General / Whisper / LLM Rewriting. `FooterMenuItems` = Logs (`SelectsOnInvoked=False`, délègue à `App` qui ouvre la LogWindow partagée).

## Navigation Frame + Page

`<Frame x:Name="PageFrame" />` à la place de l'ancien `ContentPresenter`. Les Pages vivent sous `Settings/` (`GeneralPage`, `WhisperPage`, `LlmPage`) et portent chacune leur propre `ScrollViewer` + padding interne.

Toutes ont `NavigationCacheMode.Required` pour préserver l'état. `PageFrame.Navigate(type, null, new EntranceNavigationTransitionInfo())` avec garde `CurrentSourcePageType != pageType` contre les re-Navigate redondants au setup.

## SettingsCard CommunityToolkit

Package NuGet `CommunityToolkit.WinUI.Controls.SettingsControls 8.2.250402`. Pattern canonique Microsoft Learn appliqué à la lettre :

- Ressource `SettingsCardSpacing=4`
- Style `SettingsSectionHeaderTextBlockStyle` (`BodyStrongTextBlockStyle` + `Margin 1,30,0,6`)
- `StackPanel MaxWidth=1000` dans un `Grid` wrapper (workaround bug `microsoft-ui-xaml#3842`)

## GeneralPage — démos non branchées

Trois sections de démo non branchées : Démarrage, Apparence, Raccourcis. **À trier / remplacer** par du contenu réel (voir roadmap R4 et piste fonctionnelle 5).

**Inspiration PowerToys** pour « Démarrer avec Windows » et son mode admin (élévation au démarrage gérée proprement, c'est un bon modèle de référence ouvert).

## WhisperPage — étape 3 câblée

Les 6 sections (Transcription, VAD, Décodage, Seuils de confiance, Filtres de sortie, Contexte) câblées sur `SettingsService` en auto-save. Build OK.

Fichiers clés :
- [AppSettings.cs](../Settings/AppSettings.cs) — Decoding/Confidence en `double` (pas `float`) pour éviter l'affichage `0.200000003`. VAD reste `float` car les sliders formattent.
- [SettingsService.cs](../Settings/SettingsService.cs) — `Current`, `Save()` debounced 300 ms, `Changed` event, `ResolveModelsDirectory()`.
- [WhisperParamsMapper.cs](../Settings/WhisperParamsMapper.cs) — `Apply(ref WhisperFullParams, AppSettings)`. VAD désactivé proprement si modèle Silero absent.
- [WhispEngine.cs](../WhispEngine.cs) — snapshot `SettingsService.Instance.Current` avant chaque `whisper_full`. **`_modelPath` pas encore rebranché** sur `Settings.Transcription.Model` (Louis utilise large-v3 en permanence, pas besoin à chaud).
- [WhisperPage.xaml](../Settings/WhisperPage.xaml) + [.xaml.cs](../Settings/WhisperPage.xaml.cs) — 6 sections, auto-save, reset-per-setting au hover, `MarkRestartPending()` sur Modèle+UseGpu, InfoBar « Restart required ».

## Persistance — décisions validées

- `WhispUI-settings.json` **à côté de l'exe** (Louis contre `%LOCALAPPDATA%` — foutoir). Mode portable OK parce que publish dans `whisp-ui/publish/`, pas Program Files.
- Sauvegarde immédiate à chaque modif (pas de bouton Appliquer / Sauvegarder).
- Reset vers preset « WhispUI recommandé » (pas vers defaults whisper.cpp natifs). Preset à définir empiriquement plus tard ; v0 prend les defaults whisper.cpp comme placeholder.
- Si chemin custom un jour : ajouter méta-champ `customSettingsPath` dans le fichier ; pas avant que Louis en ait besoin.
- Point d'entrée : entrée tray « Paramètres » à côté de « Logs ».

## Bug runtime navigation WhisperPage — RÉSOLU 2026-04-09

**Cause réelle** : `VadMaxSpeechSlider` avec `Minimum="5"` en XAML. Un Slider WinUI 3 s'initialise avec `Minimum=0 Maximum=100 Value=0`. Quand le parser XAML rencontre `Minimum="5"`, RangeBase doit coercer Value (0 → 5), et ce chemin crashe dans `Application.LoadComponent` sous build **release net10.0-windows + trimming** avec `XamlParseException: Failed to assign to property RangeBase.Minimum`. Tentative de workaround `Value="5"` avant `Minimum` : déplace le crash sur `RangeBase.Value` (même chemin de coercion sous le capot).

**Fix appliqué** : sortir `Minimum` et `Value` du XAML, les poser en code-behind juste après `InitializeComponent()`. XAML garde uniquement `Maximum=60 StepFrequency=1 TickFrequency=5 Style`. Code-behind :

```csharp
InitializeComponent();
VadMaxSpeechSlider.Minimum = 5;
VadMaxSpeechSlider.Value   = 5;
```

**Diagnostic** : instrumentation de `SettingsWindow.OnNavSelectionChanged` (try/catch autour de `Frame.Navigate`) + `WhisperPage..ctor` (try/catch autour de `InitializeComponent` et `Loaded`), logs routés vers `LogWindow` via le nouvel accesseur `App.Log` (au lieu de `DebugLog` qui écrit dans `%TEMP%\whisp-debug.log` invisible). La stack trace XAML a pointé directement sur `RangeBase.Minimum`.

**Leçon** : tout nouveau `Slider` dont la range exclut la valeur 0 (default `Value`) doit être configuré en code-behind post-`InitializeComponent`, pas en XAML. Mémoire dédiée : `project_winui3_slider_bug.md`.

**Instrumentation permanente** : `WhisperPage` expose un helper `TryApply(action, body)` qui loggue chaque écriture de setting en Verbose (valeur incluse) et toute exception en Error. Tous les handlers `_ValueChanged` / `_Toggled` / `_TextChanged` / `_SelectionChanged` passent par lui. Les Reset sont loggués en Info. `SettingsWindow` loggue ses `SelectionChanged` et `ItemInvoked` aussi. Utiliser la LogWindow en mode **Full** pour voir le verbose pendant la mise au point.

## Greying des réglages non-scope (à faire)

`IsEnabled=False` + tooltip « Non vérifié — laisser par défaut » sur tout ce qui n'est pas dans le scope immédiat du combat des hallucinations.

**Actif** : Filtrage de la parole (VAD) complet + Modèle + Langue + Prompt initial.
**Grisé** : Seuils de confiance, Décodage, Filtres de sortie (sauf `SuppressNonSpeechTokens` on par défaut), Contexte et segmentation, Chemins avancés.

Objectif : éviter que Louis passe du temps sur des settings dont le branchement n'est pas prouvé.

## Simplifications prises

- Pas de SettingsWindow footer conditionnel pour l'instant — remplacé par une InfoBar locale « Restart required » dans WhisperPage.
- Pas de rebranchement `_modelPath` sur SettingsService — Louis utilise large-v3 en permanence.
- Pas de traduction FR → EN pour l'instant — passe finale de la section Settings.

## Finitions restantes

- Retirer les transitions `EntranceThemeTransition` + `RepositionThemeTransition` de `GeneralPage.xaml` (Louis les a enlevées dans son Figma).
- Masquer Cancel/Save du footer par défaut (doctrine auto-save façon Settings Windows 11). Conserver le code/XAML pour réactivation ultérieure.
- Revoir le positionnement du toggle de nav — Louis pense que dans les Paramètres Windows 11 le toggle est au-dessus des icônes et pousse le contenu à un certain breakpoint. Vérifier Figma DS Windows 11 node `169220-31361`, éventuellement inspecter le NavigationView natif des Settings Windows 11.

## Références Figma

- `fAjzDDUpFW1InvLl5xL83R` node `117-15203` — WhispUI à jour.
- `jabme9frQrDyvFTgrUTHyK` node `169220-31361` — DS Windows UI 3.

## Ce qui est **hors scope** de ce refacto

- Refonte HUD complète (chrono seul, bordure audio, animation chargement, alignement Figma).
- LogWindow CommandBar overflow flyout pleine largeur (limite WinUI connue).
