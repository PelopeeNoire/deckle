# CLAUDE.md — Deckle (app hôte)

L'app hôte WinUI 3 unpackaged qui rassemble les modules `Deckle.*`. Point d'entrée unique du projet côté UI. La responsabilité de ce module se limite à la composition : lifecycle de l'app, fenêtres longue vie (HUD, LogWindow, SettingsWindow, PlaygroundWindow), tray system, hotkeys globaux, branchement des modules métier via leurs interfaces de host. Aucune logique métier n'est censée vivre ici en dehors des handlers d'événements et des adapters de bridge — quand on en ajoute, c'est presque toujours un signal qu'elle aurait dû atterrir dans un module spécifique.

Avant tout test runtime, tuer toute instance déjà en cours (Deckle ou prototype antérieur). Deux processus qui appellent `RegisterHotKey` sur la même combinaison se collisionnent avec `err 1409`.

## Build

`dotnet build` est cassé sur ce projet à cause du bug `XamlCompiler.exe` MSB3073 — détail et contournement documentés dans le CLAUDE.md racine. Le build passe par `MSBuild.exe` de Visual Studio 2026 (MSBuild Framework, `MSBuildRuntimeType=Full`).

Depuis `src/Deckle/`, PowerShell sans admin, remplacer `<msbuild-path>` par le chemin local du `MSBuild.exe` Framework livré avec Visual Studio 2026 (par défaut sous `<vs-install>\MSBuild\Current\Bin\amd64\MSBuild.exe`) :

```
& "<msbuild-path>" `
    -t:Restore,Build -p:Configuration=Release -p:Platform=x64
```

Sortie : `bin\x64\Release\net10.0-windows10.0.19041.0\Deckle.exe` (self-contained).

Points de vigilance côté csproj. `Microsoft.WindowsAppSDK` est épinglé à `1.8.260317003` (stable officielle). `global.json` épingle SDK `10.0.104` — à conserver. `<EnableMsixTooling>true</EnableMsixTooling>` force le pipeline Publish à générer `Deckle.pri` dans `PublishDir` ; sans ça, en WindowsAppSDK 1.8 unpackaged, les `.xbf` embarqués dans le `.pri` sont injoignables et l'app démarre sans fenêtre (voir [microsoft/WindowsAppSDK#3451](https://github.com/microsoft/WindowsAppSDK/issues/3451)).

Les scripts d'orchestration vivent sous `scripts/`. `scripts/build-run.ps1` tue Deckle s'il tourne, build via MSBuild VS, lance l'exe — switches `-Restore`, `-NoRun`, `-Wait`, `-Configuration`, `-MsBuild`. `scripts/publish-unpackaged.ps1` cible `Restore;Publish` vers `publish/` à la racine du repo (sortie folder unpackaged sans installer ni MSIX, modèles et DLLs natives non embarqués — téléchargés au first run). MSBuild résolu via `-MsBuild`, puis `$env:DECKLE_MSBUILD`, puis `vswhere`. Pour court-circuiter `vswhere` (et accélérer le démarrage du script), définir une fois pour toutes la variable d'environnement utilisateur `DECKLE_MSBUILD` avec `setx DECKLE_MSBUILD "<msbuild-path>"`.

## Pièges WinUI 3 transverses

Ces pièges concernent tout le code WinUI 3 de l'app, pas seulement le module hôte. Ils sont consignés ici parce que c'est ici que la passe d'instrumentation initiale les a tous capturés, mais ils s'appliquent dès qu'un autre module touche au XAML ou aux fenêtres WinUI 3.

`AllowUnsafeBlocks` est obligatoire dans tout csproj qui utilise `LibraryImport`. Sans cette propriété, le compilateur émet `SYSLIB1062` ou `CS0227`.

`UseWindowsForms` est interdit dans tout csproj WinUI 3. Le mix WinUI 3 + Windows Forms casse la résolution XAML.

`Window` n'expose pas de `Resources` directement en WinUI 3. Les ressources XAML se déclarent sur le `Grid` racine via `<Grid.Resources>`, pas sur `<Window.Resources>` (erreur de compilation `WMC0011`).

Tout objet UI WinUI 3 vit uniquement sur le thread UI, y compris `SolidColorBrush`. Tout objet UI instancié depuis un thread de fond lève `COMException` (`RPC_E_WRONG_THREAD`). Le pattern à appliquer : créer les brushes et objets UI dans le constructeur de la `Window` et les réutiliser dans les handlers venant des threads Record ou Transcribe.

Les caption buttons Tall ne se déclenchent pas avec `ExtendsContentIntoTitleBar=true` seul. Il faut ajouter explicitement `AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall`. La règle vaut aussi avec le contrôle `Microsoft.UI.Xaml.Controls.TitleBar` natif.

Le délégué `SubclassProc` Win32 doit être un champ d'instance, jamais une lambda locale (sinon le GC le collecte et le subclass crash). Le pattern est en place dans `MessageOnlyHost`.

Le lifetime des fenêtres WinUI 3 dans Deckle est piloté par `Closing→Cancel`. Toutes les Windows (HUD, LogWindow, SettingsWindow, PlaygroundWindow) bloquent leur fermeture. La sortie unique est le menu Quitter du tray, qui appelle `QuitApp()` (libère tray, message host, engine puis `Environment.Exit(0)`). Conséquence : la `LogWindow` jamais affichée n'a pas de layout initialisé — `LogScrollViewer.UpdateLayout()` ne peut être appelé qu'après que la fenêtre a été montrée au moins une fois (drapeau `_isVisible` en place).

Le tray et les hotkeys globaux ne peuvent pas être hébergés par une `Microsoft.UI.Xaml.Window` : le sous-classage Win32 nécessaire (`SetWindowSubclass`) est incompatible. La solution canonique est une message-only window Win32 (`MessageOnlyHost`, parent `HWND_MESSAGE`) créée dans `App.OnLaunched`. Invisible par construction — pas de flash possible, pas de trick off-screen. `TrayIconManager.Register(hwnd)` et `HotkeyManager` s'attachent dessus.

## HudWindow — rappel spec

`HudWindow` est une `Window` WinUI 3 d'environ 320×64, positionnée bas-centre via `DisplayArea.Primary.WorkArea`, en `OverlappedPresenter` non resizable, avec `ExtendsContentIntoTitleBar=true`. Elle est créée une fois dans `OnLaunched` et n'est jamais détruite. Les handlers UI sont marshalés via `DispatcherQueue.TryEnqueue` car les events `WhispEngine` viennent de threads de fond.

Pour afficher la HUD, la séquence est `MoveAndResize` puis `ShowWindow(SW_SHOWNOACTIVATE)` suivi de `SetWindowPos(HWND_TOP, SWP_NOACTIVATE|SWP_NOMOVE|SWP_NOSIZE)`. Jamais `SetForegroundWindow` — la HUD ne doit pas voler le focus. Pour la masquer, `ShowWindow(SW_HIDE)`. Les détails (coloration progressive du chrono, fade proximité souris via Raw Input et alpha layered avec smoothstep, contrainte d'ombre layered, régressions de notification) vivent dans [docs/reference--hud--1.0.md](../../docs/reference--hud--1.0.md).

## Lifetime — `App.xaml.cs`

L'ordre de `OnLaunched` est sensible parce qu'il croise plusieurs invariants : la migration de settings doit tourner avant qu'un service ne touche son fichier, le `MessageOnlyHost` doit exister avant l'enregistrement des hotkeys, le tray doit avoir ses callbacks branchés avant son `Register`. La séquence canonique est : migration `SettingsBootstrap.MigrateLegacyToPerModule()` en premier, puis registration de `TelemetryGates.Configure` et des sinks de logging, puis first-run gate (wizard si les natives ou modèles manquent), puis instanciation de `WhispEngine`, puis création des fenêtres longue vie (HUD prime, LogWindow et SettingsWindow et PlaygroundWindow restent lazy), puis création du `TrayIconManager` (callbacks seulement, pas encore `Register`), puis branchement des events engine → tray + windows, puis création du `MessageOnlyHost`, puis `tray.Register(messageHost.Hwnd)` et `hotkeyManager.Register()`, puis application du theme persisté et du level window de calibration, puis ouverture conditionnelle de Settings si `--settings` est passé en CLI.

Trois filets de diagnostic globaux sont posés dans le constructeur de `App` : `Application.UnhandledException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`. Tous les trois routent vers `LogService` avec `LogSource.Crash` et un préfixe distinct (`CRASH`, `CRASH-AD`, `CRASH-TS`). Sans ces filets, une exception qui surgit dans un handler `WhispEngine` peut disparaître silencieusement — le pattern existait avant l'unification télémétrie et reste en place pour les cas où le sink principal n'est pas encore inscrit (boot précoce).
