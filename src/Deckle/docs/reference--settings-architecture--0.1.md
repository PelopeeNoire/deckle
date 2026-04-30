# SettingsWindow — NavigationView Auto + Frame/Page

## TitleBar natif

`Microsoft.UI.Xaml.Controls.TitleBar` natif (Windows App SDK 1.8). Caption buttons **Standard** via `AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard`. Icône app via `ImageIconSource` nommé. `ExtendsContentIntoTitleBar=true` + `SetTitleBar(AppTitleBar)`.

Caption buttons couleurs posées manuellement (`UpdateCaptionButtonColors`) avec `ActualThemeChanged` pour suivre le thème live — backgrounds transparents (Mica visible), foreground adapté light/dark.

## NavigationView adaptatif — PaneDisplayMode=Auto (natif)

Pas de code-behind custom pour les breakpoints. `PaneDisplayMode=Auto` (défaut WinUI) qui gère seul la bascule :
- **Left** >=1008
- **LeftCompact** 641-1007
- **LeftMinimal** <=640

`PreferredMinimumWidth=320` sur le presenter, donc le mode LeftMinimal est exposé. La recherche est dans le slot canonique `NavigationView.AutoSuggestBox` (pattern Microsoft Learn "NavigationView > Pane"), pas dans la TitleBar.

`DisplayModeChanged` gère uniquement le padding Frame : +48px top en mode Minimal pour ne pas chevaucher le hamburger (pattern Windows Terminal Settings).

Contenu : `NavigationView.MenuItems` = General / Transcription / Rewriting. `FooterMenuItems` = Logs (`SelectsOnInvoked=False`, clic via `ItemInvoked` qui délègue à `App` pour ouvrir la LogWindow partagée).

## Navigation Frame + Page

`<Frame x:Name="PageFrame" />` dans le slot content du NavigationView. Navigation via `Type.GetType(tag)` (pattern du sample officiel Microsoft Learn "Code example"). Garde `CurrentSourcePageType != pageType` contre les re-Navigate redondants.

Pages sous `Settings/` : `GeneralPage`, `WhisperPage`, `LlmPage`. Toutes avec `NavigationCacheMode.Required` pour préserver l'état. `EntranceNavigationTransitionInfo` sur chaque navigation.

## SettingsCard CommunityToolkit

Package NuGet `CommunityToolkit.WinUI.Controls.SettingsControls 8.2.250402`. Pattern canonique Microsoft Learn : ressource `SettingsCardSpacing=4`, style `SettingsSectionHeaderTextBlockStyle` (`BodyStrongTextBlockStyle` + `Margin 1,30,0,6`), `StackPanel MaxWidth=1000` dans un `Grid` wrapper (workaround bug `microsoft-ui-xaml#3842`).

## GeneralPage — sections câblées

Toutes les sections sont auto-save via `SettingsService`. Ordre actuel :
Theme & overlay → Startup → Audio input → Diagnostics → Backup.

- **Theme** — ComboBox System / Light / Dark, appliqué live sur toutes les fenêtres via `App.ApplyTheme`.
- **Overlay** — toggle enabled, toggle fade on proximity, position (BottomCenter / BottomRight / TopCenter).
- **Startup** — toggle start minimized + warmup-on-launch.
- **Audio input** — énumération waveIn Win32, ComboBox "System default" + devices nommés, `AudioInputDeviceId` (-1 = WAVE_MAPPER) + level window calibration (MinDbfs / MaxDbfs / curve exponent + auto-calibration toggle).
- **Diagnostics** — quatre opt-in indépendants : latency JSONL, raw corpus capture, audio corpus WAV (nested), application log to disk, microphone telemetry. Tous off par défaut.
- **Backup** — section PowerToys-style (expander collapsable). `SettingsBackupService` : snapshot ponctuel `settings-YYYYMMDD-HHmmss.json` sous `<ConfigDirectory>/backups/`, liste des backups existants, restore via swap atomique. `BackupDirectory` configurable pour pointer vers OneDrive/Drive.

## WhisperPage — 6 sections câblées

Transcription, VAD, Decodage, Seuils de confiance, Filtres de sortie, Contexte. Auto-save. `MarkRestartPending()` sur Modele + UseGpu (InfoBar "Restart required"). Helper `TryApply(action, body)` qui loggue chaque ecriture en Verbose.

Bug Slider `Minimum` en XAML release : tout Slider dont la range exclut 0 doit etre configure en code-behind post-`InitializeComponent`. Detail et lecon dans memoire `project_winui3_slider_bug.md`.

## LlmPage — sections câblées

- **General** — toggle Enabled + Ollama endpoint (auto-save).
- **Models** — état de la connexion Ollama (reachable + liste de modèles présents) avec refresh manuel.
- **Profiles** — liste éditable de profils de réécriture (`RewriteProfile` : name, model, system prompt, temperature, num_ctx_k, top_p, repeat_penalty). Add / Delete / Reset section. Auto-save via `ProfileViewModel`. Trois profils par défaut alignés sur les brackets de cleanup (Lissage / Affinage / Arrangement) avec prompts pré-écrits tunés via autoresearch — l'exemple par défaut, à utiliser tel quel ou à adapter. Temperature et NumCtxK pré-réglés par bracket. Reset ramène cet exemple complet.
- **Auto-rewrite rules** — pivot RuleMetric (Word count par défaut / Duration). Deux listes mutuellement exclusives, par seuil. Chaque rule pointe vers un profil. Construction impérative en code-behind (pas d'ItemsRepeater + DataTemplate), même pattern que `LlmShortcutSlotsSection` : `Items` peuplé avec `ComboBoxItem`, `SelectedIndex` posé explicitement, `SelectionChanged` mute directement `Settings.AutoRewriteRules[i]` + `Save()`. La précédente architecture VM + binding x:Bind perdait la sélection user à chaque switch metric / add-remove profile / nav (refonte 2026-04-28). Cf. mémoire `reference_winui_itemsrepeater_datacontext.md`.
- **Shortcut slots** — Primary (Shift+Win+`) et Secondary (Ctrl+Win+`), tous deux opt-in avec sentinel "(None)" stocké comme null. Utilisés par les hotkeys de réécriture manuelle.

## Persistance

- `settings.json` sous `AppPaths.ConfigDirectory` : a cote de l'exe en dev unpackaged, sous `LocalState/config/` en packaged MSIX.
- Sauvegarde immediate a chaque modif (pas de bouton Appliquer / Sauvegarder).
- `SettingsService` : `Current`, `Save()` debounced 300 ms, `Changed` event, `ResolveModelsDirectory()` (delegue a `AppPaths.ModelsDirectory` quand pas d'override user).

## Backdrop + presenter

Mica. `OverlappedPresenter` classique (min, max, resize). Close -> Cancel + Hide (reeutilisee via tray). Resize 960x1440 initial.

## Restart cible

`App.RestartApp(pageTag?)` relance l'exe avec `--settings [pageTag]`, `OnLaunched` detecte le flag et rouvre les Settings sur la bonne page.
