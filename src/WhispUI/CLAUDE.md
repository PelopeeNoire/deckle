# CLAUDE.md — WhispUI

Projet WinUI 3 unpackaged, remplaçant de WhispInteropTest. Lire d'abord [../../CLAUDE.md](../../CLAUDE.md) puis ce fichier. **La doctrine de travail** (réflexion obligatoire, skills, MCP Microsoft Learn, casquettes, custom en dernier recours, zéro valeur magique) est dans le CLAUDE.md racine — elle s'applique intégralement à ce sous-projet.

**WhispInteropTest reste l'app de production** tant que WhispUI n'est pas packagé et validé à froid. Il tourne via la tâche planifiée `Whisp` et doit être tué avant tout test runtime de WhispUI (collision hotkey `RegisterHotKey` err 1409).

---

## Build

`dotnet build` est cassé sur ce projet (bug Microsoft `XamlCompiler.exe` MSB3073, détail dans [../../CLAUDE.md](../../CLAUDE.md)). **Builder uniquement via le `MSBuild.exe` de VS 2026** (MSBuild Framework, `MSBuildRuntimeType=Full`).

Depuis `src/WhispUI/` (PowerShell, sans admin) :

```
& "D:\bin\visual-studio\visual-studio-2026\MSBuild\Current\Bin\amd64\MSBuild.exe" `
    -t:Restore,Build -p:Configuration=Release -p:Platform=x64
```

Sortie : `bin\x64\Release\net10.0-windows10.0.19041.0\WhispUI.exe` (self-contained).

État du csproj :
- `Microsoft.WindowsAppSDK` : `1.8.260317003` (stable officielle).
- `global.json` épingle SDK `10.0.104` : conserver.
- `<EnableMsixTooling>true</EnableMsixTooling>` — force le pipeline Publish à générer `WhispUI.pri` dans `PublishDir`. Sans ça, en WindowsAppSDK 1.8 unpackaged, les `.xbf` embarqués dans le `.pri` sont injoignables et l'app démarre sans aucune fenêtre. Cf. `microsoft/WindowsAppSDK#3451`.

**Scripts** `scripts/build-run.ps1` et `scripts/publish.ps1` (versionnés). `build-run` : tue WhispUI s'il tourne, build via MSBuild VS, lance l'exe. Switches `-Restore`, `-NoRun`, `-Wait`, `-Configuration`, `-MsBuild`. `publish` : même logique, target `Restore;Publish` vers `publish/` (racine repo). MSBuild résolu via `-MsBuild` > `$env:WHISPUI_MSBUILD` > `vswhere`. Chez Louis : `setx WHISPUI_MSBUILD "D:\bin\visual-studio\visual-studio-2026\MSBuild\Current\Bin\amd64\MSBuild.exe"`.

**Rappel feedback** : je ne lance jamais le build — Louis s'en charge.

---

## État actuel — condensé

**V1 atteinte.** Transcription fonctionnelle de bout en bout, HUD complet, LogWindow v3, SettingsWindow skeleton + refacto natif livrés, publish OK. Toutes les briques WinUI sont passées sur le natif Microsoft : `Microsoft.UI.Xaml.Controls.TitleBar`, `NavigationView` adaptatif, `Frame`+`Page`, `SettingsCard` (CommunityToolkit), brushes via `ThemeDictionaries`.

**Étape courante — combat des hallucinations** (voir roadmap ci-dessous). Bug de navigation WhisperPage résolu (détail dans mémoire `project_winui3_slider_bug.md`).

**Régressions post-refacto** : à traiter en parallèle de la piste fonctionnelle — démarrage qui flashe une fenêtre, HudWindow qui ne suit pas les bureaux virtuels. Détail dans mémoire `project_roadmap.md` piste *régressions techniques*.

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
- **Lifetime WinUI 3** : toutes les Windows (HUD, LogWindow, SettingsWindow) bloquent leur fermeture via `Closing→Cancel`. Sortie unique = menu Quitter du tray → `QuitApp()` qui libère tray, message host, engine puis `Environment.Exit(0)`.
- **Tray + hotkeys host** : pas une `Microsoft.UI.Xaml.Window`. C'est une **message-only window Win32** (`MessageOnlyHost`, parent `HWND_MESSAGE`) créée dans `App.OnLaunched`. Invisible par construction — pas de flash possible, pas de trick off-screen. `TrayIconManager.Register(hwnd)` et `HotkeyManager` s'attachent dessus via `SetWindowSubclass` exactement comme avant.
- **Délégué `SubclassProc`** : champ d'instance (jamais lambda locale) sinon GC.
- **Pas de `UseWindowsForms`** dans le csproj (conflit XAML WinUI 3).
- **`Window` n'a pas de `Resources` en WinUI 3** (contrairement à WPF) : déclarer les ressources XAML sur le `Grid` racine (`<Grid.Resources>`), pas sur `<Window.Resources>` (erreur WMC0011).
- **Objets UI WinUI 3 créés uniquement sur le thread UI** — y compris `SolidColorBrush`. Tout objet UI instancié depuis un thread de fond lève `COMException` (`RPC_E_WRONG_THREAD`). Pattern : créer brushes/objets dans le constructeur de la Window et les réutiliser dans les handlers d'events venant des threads Record/Transcribe.
- **LogWindow jamais affichée** : ne pas toucher à `LogScrollViewer.UpdateLayout()` tant que la fenêtre n'a pas été montrée au moins une fois (flag `_isVisible`).
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
2. `_logWindow = new LogWindow()` — créée, pas de `Show`
3. `_settingsWindow = new SettingsWindow()` — créée, pas de `Show`
4. `_hudWindow = new HudWindow(...)` — créée, pas de `Show` (constructeur HUD capture HWND + subclass sans visibilité)
5. `_tray = new TrayIconManager()` (callbacks seulement, pas encore `Register`)
6. Branchement events engine → tray + LogWindow + HudWindow
7. `_messageHost = new MessageOnlyHost()` → HWND natif invisible (parent `HWND_MESSAGE`)
8. `_tray.Register(_messageHost.Hwnd)` + `_hotkeyManager = new HotkeyManager(_messageHost.Hwnd, OnHotkey); _hotkeyManager.Register()`
9. Tray callbacks : Logs → `_logWindow.ShowAndActivate()` ; Quitter → `QuitApp()`

**Filets de diagnostic globaux** dans `App` : `Application.UnhandledException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` → `DebugLog` (préfixes `CRASH` / `CRASH-AD` / `CRASH-TS`).

---

## Journal d'implémentation — dossier `docs/`

Les détails techniques par sous-système vivent dans `docs/`. **Ces fichiers sont lus à la demande**, uniquement quand je touche au sous-système concerné — ils ne sont pas chargés en contexte par défaut. Quand une tâche touche un sous-système, lire son fichier `docs/*.md` **avant** de modifier le code.

- [docs/hud.md](docs/hud.md) — spec HudWindow (positioning, backdrop Acrylic, DPI), coloration progressive chrono, fade proximité souris (Raw Input + alpha layered + smoothstep), contrainte ombre layered.
- [docs/logwindow.md](docs/logwindow.md) — TitleBar natif + SearchBox, SelectorBar + CommandBar responsive, modèle de données (5 niveaux, cap 5000), couleurs `ThemeDictionaries`, templates/selector, piège wrap/scroll.
- [docs/pipeline-transcription.md](docs/pipeline-transcription.md) — pipeline monobloc (`new_segment_callback`, plus de chunking externe), instrumentation par segment, defaults whisper.cpp restaurés (piège `entropy_thold` inversé), hot-reload via `SettingsService`.
- [docs/paste.md](docs/paste.md) — re-capture cible au Stop avec filet PID, fix race `HideSync` (rendez-vous synchrone via `ManualResetEventSlim`), refus explicites de `PasteFromClipboard`, bug paste fantôme intermittent.
- [docs/settings.md](docs/settings.md) — NavigationView Auto (natif, 3 modes), TitleBar natif Standard, Frame+Page, SettingsCard CommunityToolkit, GeneralPage 4 sections câblées, WhisperPage 6 sections, persistance JSON portable, restart ciblé.
