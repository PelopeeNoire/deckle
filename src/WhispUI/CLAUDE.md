# CLAUDE.md — WhispUI

App WinUI 3 unpackaged, point d'entrée unique du projet transcription
côté UI.

WhispInteropTest (WinForms) tourne encore via la tâche planifiée
`Whisp` tant que WhispUI n'est pas packagé. Il doit être tué avant
tout test runtime de WhispUI — collision `RegisterHotKey` err 1409.

Roadmap : source unique en mémoire `project_roadmap.md`
(`C:\Users\Louis\.claude\projects\d--projects-ai-transcription\memory\`).
À relire en début de session.

---

## Build

`dotnet build` est cassé sur ce projet (bug `XamlCompiler.exe` MSB3073,
détail dans le CLAUDE.md racine). Builder via `MSBuild.exe` de VS 2026
(MSBuild Framework, `MSBuildRuntimeType=Full`).

Depuis `src/WhispUI/`, PowerShell sans admin :

```
& "D:\bin\visual-studio\visual-studio-2026\MSBuild\Current\Bin\amd64\MSBuild.exe" `
    -t:Restore,Build -p:Configuration=Release -p:Platform=x64
```

Sortie : `bin\x64\Release\net10.0-windows10.0.19041.0\WhispUI.exe`
(self-contained).

État du csproj :

- `Microsoft.WindowsAppSDK` : `1.8.260317003` (stable officielle).
- `global.json` épingle SDK `10.0.104` — conserver.
- `<EnableMsixTooling>true</EnableMsixTooling>` force le pipeline
  Publish à générer `WhispUI.pri` dans `PublishDir`. Sans ça, en
  WindowsAppSDK 1.8 unpackaged, les `.xbf` embarqués dans le `.pri`
  sont injoignables et l'app démarre sans fenêtre. Cf.
  `microsoft/WindowsAppSDK#3451`.

Scripts `scripts/build-run.ps1` et `scripts/publish.ps1` (versionnés).
`build-run` tue WhispUI s'il tourne, build via MSBuild VS, lance
l'exe — switches `-Restore`, `-NoRun`, `-Wait`, `-Configuration`,
`-MsBuild`. `publish` : target `Restore;Publish` vers `publish/`
(racine repo). MSBuild résolu via `-MsBuild` > `$env:WHISPUI_MSBUILD`
> `vswhere`. Chez Louis :
`setx WHISPUI_MSBUILD "D:\bin\visual-studio\visual-studio-2026\MSBuild\Current\Bin\amd64\MSBuild.exe"`.

---

## Pièges WinUI 3 connus

- **`AllowUnsafeBlocks` obligatoire** dans le csproj — sans, `SYSLIB1062`
  / `CS0227` sur `LibraryImport`.
- **WhispInteropTest doit être tué** avant tout test — collision
  `RegisterHotKey` err 1409.
- **Lifetime WinUI 3** : toutes les Windows (HUD, LogWindow,
  SettingsWindow) bloquent leur fermeture via `Closing→Cancel`. Sortie
  unique = menu Quitter du tray → `QuitApp()` qui libère tray, message
  host, engine puis `Environment.Exit(0)`.
- **Tray + hotkeys host** : pas une `Microsoft.UI.Xaml.Window`.
  Message-only window Win32 (`MessageOnlyHost`, parent `HWND_MESSAGE`)
  créée dans `App.OnLaunched`. Invisible par construction — pas de
  flash possible, pas de trick off-screen.
  `TrayIconManager.Register(hwnd)` et `HotkeyManager` s'attachent
  dessus via `SetWindowSubclass`.
- **Délégué `SubclassProc`** : champ d'instance, jamais lambda locale
  (GC).
- **Pas de `UseWindowsForms`** dans le csproj — conflit XAML WinUI 3.
- **`Window` n'a pas de `Resources` en WinUI 3** : déclarer les
  ressources XAML sur le `Grid` racine (`<Grid.Resources>`), pas sur
  `<Window.Resources>` (erreur WMC0011).
- **Objets UI WinUI 3 uniquement sur le thread UI** — y compris
  `SolidColorBrush`. Tout objet UI instancié depuis un thread de fond
  lève `COMException` (`RPC_E_WRONG_THREAD`). Pattern : créer
  brushes/objets dans le constructeur de la Window et les réutiliser
  dans les handlers venant des threads Record/Transcribe.
- **LogWindow jamais affichée** : pas de
  `LogScrollViewer.UpdateLayout()` tant que la fenêtre n'a pas été
  montrée au moins une fois (flag `_isVisible`).
- **Caption buttons Tall** : `ExtendsContentIntoTitleBar=true` seul ne
  force pas la hauteur Tall — ajouter explicitement
  `AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall`.
  Vrai aussi avec le contrôle `Microsoft.UI.Xaml.Controls.TitleBar`
  natif.

---

## HudWindow — rappel spec

Window WinUI 3, ~320×64, bas-centre via `DisplayArea.Primary.WorkArea`,
`OverlappedPresenter` non resizable, `ExtendsContentIntoTitleBar=true`.

- **Show** : `MoveAndResize` puis `ShowWindow(SW_SHOWNOACTIVATE)` +
  `SetWindowPos(HWND_TOP, SWP_NOACTIVATE|SWP_NOMOVE|SWP_NOSIZE)`.
  Jamais `SetForegroundWindow`.
- **Hide** : `ShowWindow(SW_HIDE)`.
- Créée une fois dans `OnLaunched`, jamais détruite (Closing→Cancel).
- Handlers UI marshalés via `DispatcherQueue.TryEnqueue` (events
  `WhispEngine` viennent de threads de fond).

Détails (coloration progressive, fade proximité, ombre layered,
régressions notification) dans [docs/hud.md](docs/hud.md).

---

## Lifetime — `App.xaml.cs` cible

Ordre `OnLaunched` :

1. `_engine = new WhispEngine()`
2. `_logWindow = new LogWindow()` — créée, pas de `Show`
3. `_settingsWindow = new SettingsWindow()` — créée, pas de `Show`
4. `_hudWindow = new HudWindow(...)` — créée, pas de `Show` (le
   constructeur HUD capture HWND + subclass sans visibilité)
5. `_tray = new TrayIconManager()` — callbacks seulement, pas encore
   `Register`
6. Branchement events engine → tray + LogWindow + HudWindow
7. `_messageHost = new MessageOnlyHost()` → HWND natif invisible
   (parent `HWND_MESSAGE`)
8. `_tray.Register(_messageHost.Hwnd)` +
   `_hotkeyManager = new HotkeyManager(_messageHost.Hwnd, OnHotkey); _hotkeyManager.Register()`
9. Tray callbacks : Logs → `_logWindow.ShowAndActivate()` ;
   Quitter → `QuitApp()`

Filets de diagnostic globaux dans `App` :
`Application.UnhandledException`, `AppDomain.UnhandledException`,
`TaskScheduler.UnobservedTaskException` → `DebugLog` (préfixes `CRASH`
/ `CRASH-AD` / `CRASH-TS`).

---

## Journal d'implémentation — dossier `docs/`

Les détails par sous-système vivent dans `docs/`. Fichiers lus à la
demande, uniquement quand je touche au sous-système concerné — pas
chargés en contexte par défaut. Avant modification d'un sous-système,
lire son fichier `docs/*.md`.

- [docs/hud.md](docs/hud.md) — spec HudWindow (positioning, backdrop
  Acrylic, DPI), coloration progressive chrono, fade proximité souris
  (Raw Input + alpha layered + smoothstep), contrainte ombre layered.
- [docs/logwindow.md](docs/logwindow.md) — TitleBar natif + SearchBox,
  SelectorBar + CommandBar responsive, modèle de données (5 niveaux,
  cap 5000), couleurs `ThemeDictionaries`, templates/selector, piège
  wrap/scroll.
- [docs/pipeline-transcription.md](docs/pipeline-transcription.md) —
  pipeline monobloc (`new_segment_callback`, plus de chunking externe),
  instrumentation par segment, defaults whisper.cpp restaurés (piège
  `entropy_thold` inversé), hot-reload via `SettingsService`.
- [docs/paste.md](docs/paste.md) — re-capture cible au Stop avec filet
  PID, fix race `HideSync` (rendez-vous synchrone via
  `ManualResetEventSlim`), refus explicites de `PasteFromClipboard`,
  bug paste fantôme intermittent.
- [docs/settings.md](docs/settings.md) — NavigationView Auto (natif, 3
  modes), TitleBar natif Standard, Frame+Page, SettingsCard
  CommunityToolkit, GeneralPage 4 sections câblées, WhisperPage 6
  sections, persistance JSON portable, restart ciblé.
