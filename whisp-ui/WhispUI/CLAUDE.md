# CLAUDE.md — WhispUI

Projet WinUI 3 unpackaged, remplaçant de WhispInteropTest. Lire d'abord [../../CLAUDE.md](../../CLAUDE.md) puis ce fichier. **La doctrine de travail** (réflexion obligatoire, skills, MCP Microsoft Learn, casquettes, custom en dernier recours, zéro valeur magique) est dans le CLAUDE.md racine — elle s'applique intégralement à ce sous-projet.

**WhispInteropTest reste l'app de production** tant que WhispUI n'est pas packagé et validé à froid. Il tourne via la tâche planifiée `Whisp` et doit être tué avant tout test runtime de WhispUI (collision hotkey `RegisterHotKey` err 1409).

---

## Build

`dotnet build` est cassé sur ce projet (bug Microsoft `XamlCompiler.exe` MSB3073, détail dans [../../CLAUDE.md](../../CLAUDE.md)). **Builder uniquement via le `MSBuild.exe` de VS 2026** (MSBuild Framework, `MSBuildRuntimeType=Full`).

Depuis `whisp-ui/WhispUI/` (PowerShell, sans admin) :

```
& "D:\bin\visual-studio\visual-studio-2026\MSBuild\Current\Bin\amd64\MSBuild.exe" `
    -t:Restore,Build -p:Configuration=Release -p:Platform=x64
```

Sortie : `bin\x64\Release\net10.0-windows10.0.19041.0\WhispUI.exe` (self-contained).

État du csproj :
- `Microsoft.WindowsAppSDK` : `1.8.260317003` (stable officielle).
- `global.json` épingle SDK `10.0.104` : conserver.
- `<EnableMsixTooling>true</EnableMsixTooling>` — force le pipeline Publish à générer `WhispUI.pri` dans `PublishDir`. Sans ça, en WindowsAppSDK 1.8 unpackaged, les `.xbf` embarqués dans le `.pri` sont injoignables et l'app démarre sans aucune fenêtre. Cf. `microsoft/WindowsAppSDK#3451`.

**Scripts** `whisp-ui/build-run.ps1` et `whisp-ui/publish.ps1` (un cran au-dessus du projet, non versionnés — chemins machine). `build-run` : tue WhispUI s'il tourne, build via MSBuild VS, lance l'exe. Switches `-Restore`, `-NoRun`, `-Wait`, `-Configuration`, `-MsBuild`. `publish` : même logique, target `Restore;Publish` vers `whisp-ui/publish/`. MSBuild résolu via `-MsBuild` > `$env:WHISPUI_MSBUILD` > `vswhere`. Chez Louis : `setx WHISPUI_MSBUILD "D:\bin\visual-studio\visual-studio-2026\MSBuild\Current\Bin\amd64\MSBuild.exe"`.

**Rappel feedback** : je ne lance jamais le build — Louis s'en charge.

---

## État actuel — condensé

**V1 atteinte.** Transcription fonctionnelle de bout en bout, HUD complet, LogWindow v3, SettingsWindow skeleton + refacto natif livrés, publish OK. Toutes les briques WinUI sont passées sur le natif Microsoft : `Microsoft.UI.Xaml.Controls.TitleBar`, `NavigationView` adaptatif, `Frame`+`Page`, `SettingsCard` (CommunityToolkit), brushes via `ThemeDictionaries`.

**Étape courante — combat des hallucinations** (voir roadmap ci-dessous). Dépend d'un bloquant : le **bug de navigation runtime vers WhisperPage** (détail dans `docs/settings.md` section *Bug runtime récurrent*), à fixer en premier.

**Régressions post-refacto** : trois à traiter en parallèle de la piste fonctionnelle — démarrage qui flashe une fenêtre, HudWindow qui apparaît trop tôt, HudWindow qui ne suit pas les bureaux virtuels. Détail dans `docs/hud.md` et mémoire `project_roadmap.md` piste *régressions techniques*.

---

## Roadmap

**Source unique de vérité** : mémoire `project_roadmap.md` (sous `C:\Users\Louis\.claude\projects\d--projects-ai-transcription\memory\`). La lire avant toute décision d'ordre.

**Piste fonctionnelle** (ordre strict) :
1. Combat des hallucinations (bloqué par bug nav WhisperPage)
2. Transcription progressive (exploratoire)
3. Réécriture LLM (Ollama)
4. Contour animé HUD
5. Finition Settings (dégrisage, FR→EN, GeneralPage réelle, LlmPage réelle)
6. Package et partage

**Piste régressions techniques** (en parallèle) : bootstrap silencieux, HudWindow comportement notification, ombre HudWindow (voies A/B), finitions SettingsWindow, icône tray, Quitter qui ne tue pas le process.

Règle : ne pas sauter à la réécriture LLM tant que la transcription n'est pas fiable. Ne pas travailler sur le contour animé ou les finitions Settings tant que le pipeline fonctionnel n'est pas complet end-to-end.

---

## Pièges WinUI 3 connus — à ne jamais oublier

- **`AllowUnsafeBlocks` obligatoire** dans le csproj : sans, `SYSLIB1062` / `CS0227` sur `LibraryImport`.
- **WhispInteropTest doit être tué** avant tout test (collision hotkey `RegisterHotKey` err 1409).
- **Lifetime WinUI 3** : ne JAMAIS laisser fermer `AnchorWindow` (Closing→Cancel). Toutes les Windows (HUD, LogWindow, SettingsWindow) suivent la même règle. Sortie unique = menu Quitter du tray → `Application.Current.Exit()`.
- **Délégué `SubclassProc`** : champ d'instance (jamais lambda locale) sinon GC.
- **Pas de `UseWindowsForms`** dans le csproj (conflit XAML WinUI 3).
- **`Window` n'a pas de `Resources` en WinUI 3** (contrairement à WPF) : déclarer les ressources XAML sur le `Grid` racine (`<Grid.Resources>`), pas sur `<Window.Resources>` (erreur WMC0011).
- **Objets UI WinUI 3 créés uniquement sur le thread UI** — y compris `SolidColorBrush`. Tout objet UI instancié depuis un thread de fond lève `COMException` (`RPC_E_WRONG_THREAD`). Pattern : créer brushes/objets dans le constructeur de la Window et les réutiliser dans les handlers d'events venant des threads Record/Transcribe.
- **LogWindow jamais affichée** : ne pas toucher à `LogScrollViewer.UpdateLayout()` tant que la fenêtre n'a pas été montrée au moins une fois (flag `_isVisible`).
- **Accessibilité constructeur public** : si `AnchorWindow` (public) prend un `TrayIconManager` en paramètre, `TrayIconManager` doit être `public` (CS0051).
- **Caption buttons Tall sur title bar custom** : `ExtendsContentIntoTitleBar=true` seul ne force pas la hauteur Tall des caption buttons système — ajouter explicitement `AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall`. Vrai aussi avec le contrôle `Microsoft.UI.Xaml.Controls.TitleBar` natif — il ne gère pas cette hauteur tout seul.

---

## HudWindow — rappel spec

Window WinUI 3, ~320×64, bas-centre via `DisplayArea.Primary.WorkArea`, `OverlappedPresenter` non resizable, `ExtendsContentIntoTitleBar=true`.

- **Show** : `MoveAndResize` puis `ShowWindow(SW_SHOWNOACTIVATE)` + `SetWindowPos(HWND_TOP, SWP_NOACTIVATE|SWP_NOMOVE|SWP_NOSIZE)`. **Jamais `SetForegroundWindow`**.
- **Hide** : `ShowWindow(SW_HIDE)`.
- Créée une fois dans `OnLaunched`, jamais détruite (Closing→Cancel).
- Handlers UI marshalés via `DispatcherQueue.TryEnqueue` (events `WhispEngine` viennent de threads de fond).

Détails (coloration progressive, fade proximité, ombre layered, régressions notification) dans [docs/hud.md](docs/hud.md).

---

## Lifetime — `App.xaml.cs` cible

Ordre `OnLaunched` :

1. `_engine = new WhispEngine()`
2. `_logWindow = new LogWindow()` hors écran + `Show(false)`
3. `_hudWindow = new HudWindow(...)` hors écran + `Show(false)`
4. `_tray = new TrayIconManager()`
5. `_anchor = new AnchorWindow(...)` hors écran + `Show(false)`
6. Dans `AnchorWindow.OnRootLoaded` : `_tray.Register`, `_hotkeyManager.Register`, `SW_HIDE`
7. Branchement events engine → tray + LogWindow + HudWindow
8. Tray callbacks : Logs → `_logWindow.ShowAndActivate()` ; Quitter → `Application.Current.Exit()`

**Filets de diagnostic globaux** dans `App` : `Application.UnhandledException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` → `DebugLog` (préfixes `CRASH` / `CRASH-AD` / `CRASH-TS`).

---

## Journal d'implémentation — dossier `docs/`

Les détails techniques par sous-système vivent dans `docs/`. **Ces fichiers sont lus à la demande**, uniquement quand je touche au sous-système concerné — ils ne sont pas chargés en contexte par défaut. Quand une tâche touche un sous-système, lire son fichier `docs/*.md` **avant** de modifier le code.

- [docs/hud.md](docs/hud.md) — spec HudWindow, coloration progressive chrono, fade proximité souris (Raw Input + alpha layered), contrainte ombre layered + voies A/B, régressions bureau virtuel, contour animé exploratoire, tâches cosmétiques chrono.
- [docs/logwindow.md](docs/logwindow.md) — refonte v3 (TitleBar natif, SelectorBar, CommandBar), modèle de données, couleurs `ThemeDictionaries`, wrap, debug logs A→Z (5 niveaux Verbose / Info / Step / Warning / Error + 3 modes de filtre).
- [docs/pipeline-transcription.md](docs/pipeline-transcription.md) — refonte monobloc (`new_segment_callback`, plus de chunking externe), instrumentation par segment, defaults whisper.cpp restaurés (piège `entropy_thold` inversé), hot-reload via `SettingsService`, cartographie logs à actualiser.
- [docs/paste.md](docs/paste.md) — re-capture cible au Stop avec filet PID, fix race `HideSync` (rendez-vous synchrone via `ManualResetEventSlim`), refus explicites de `PasteFromClipboard`, bug paste fantôme intermittent.
- [docs/settings.md](docs/settings.md) — TitleBar natif, NavigationView 2 breakpoints, Frame+Page, SettingsCard CommunityToolkit, WhisperPage câblée, persistance JSON portable, **bug nav WhisperPage bloquant**, greying non-scope, finitions restantes, références Figma.
