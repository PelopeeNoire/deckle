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

## GeneralPage — 4 sections câblées

Quatre sections fonctionnelles, toutes auto-save via `SettingsService` :

- **Audio input** — énumération waveIn Win32, ComboBox "System default" + devices nommés, `AudioInputDeviceId` (-1 = WAVE_MAPPER).
- **Overlay** — toggle enabled, toggle fade on proximity, position (BottomCenter / BottomRight / TopCenter).
- **Startup** — toggle start minimized.
- **Theme** — ComboBox System / Light / Dark, appliqué live sur toutes les fenêtres via `App.ApplyTheme`.

## WhisperPage — 6 sections câblées

Transcription, VAD, Decodage, Seuils de confiance, Filtres de sortie, Contexte. Auto-save. `MarkRestartPending()` sur Modele + UseGpu (InfoBar "Restart required"). Helper `TryApply(action, body)` qui loggue chaque ecriture en Verbose.

Bug Slider `Minimum` en XAML release : tout Slider dont la range exclut 0 doit etre configure en code-behind post-`InitializeComponent`. Detail et lecon dans memoire `project_winui3_slider_bug.md`.

## LlmPage — skeleton

Placeholder, pas encore branchee.

## Persistance

- `WhispUI-settings.json` a cote de l'exe (mode portable, pas `%LOCALAPPDATA%`).
- Sauvegarde immediate a chaque modif (pas de bouton Appliquer / Sauvegarder).
- `SettingsService` : `Current`, `Save()` debounced 300 ms, `Changed` event, `ResolveModelsDirectory()`.

## Backdrop + presenter

Mica. `OverlappedPresenter` classique (min, max, resize). Close -> Cancel + Hide (reeutilisee via tray). Resize 960x1440 initial.

## Restart cible

`App.RestartApp(pageTag?)` relance l'exe avec `--settings [pageTag]`, `OnLaunched` detecte le flag et rouvre les Settings sur la bonne page.
