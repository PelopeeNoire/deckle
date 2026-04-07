# CLAUDE.md — WhispUI

Projet WinUI 3 unpackaged destiné à remplacer WhispInteropTest. Lire d'abord
[../../CLAUDE.md](../../CLAUDE.md) puis ce fichier.

**WhispInteropTest reste l'app de production** tant que WhispUI n'est pas validé end-to-end.
Il tourne via la tâche planifiée `Whisp` et doit être tué avant tout test runtime de WhispUI
(sinon `RegisterHotKey` échoue avec err 1409).

---

## Build

`dotnet build` est cassé sur ce projet (bug Microsoft `XamlCompiler.exe` MSB3073, voir
`../../CLAUDE.md`). Installer Visual Studio ne le débloque pas — `dotnet build` reste sur
`MSBuildRuntimeType=Core`. **Builder uniquement via le `MSBuild.exe` de VS 2026** (Framework,
`MSBuildRuntimeType=Full`).

Depuis `whisp-ui/WhispUI/` (PowerShell, sans admin) :

```
& "D:\bin\visual-studio\visual-studio-2026\MSBuild\Current\Bin\amd64\MSBuild.exe" `
    -t:Restore,Build -p:Configuration=Release -p:Platform=x64
```

Sortie : `bin\x64\Release\net10.0-windows10.0.19041.0\WhispUI.exe` (self-contained).

État du csproj :
- `Microsoft.WindowsAppSDK` : `1.8.260317003` (stable officielle).
- `global.json` épingle SDK `10.0.104` : conserver.

---

## État actuel

**Étapes 1 → 5 validées runtime. Transcription fonctionnelle de bout en bout.**

Bootstrap (AnchorWindow invisible + ancre lifetime), pipeline complet
(`WhispEngine` + tray + LogWindow + hotkey Alt+\` toggle Start/Stop), HudWindow basique
(chrono figé, beacon/ProgressRing, Mica). Layout HUD à finir (cf. Tâches ouvertes).

**Fix focus régression (post-Étape 5)** : `_pasteTarget` est figé au Start, pas re-capturé
au Stop, pour éviter que le HUD foreground n'écrase la cible. Voir
[WhispEngine.cs:148](WhispEngine.cs#L148) et [App.xaml.cs:88](App.xaml.cs#L88). Résiduel :
`SetForegroundWindow` peut échouer silencieusement (cf. Tâches ouvertes).

**LogWindow v2** : fenêtre classique (`OverlappedPresenter` standard, thème sombre forcé,
Close→Cancel+Hide). Toolbar : Effacer / Copier tout / Enregistrer… (FileSavePicker .txt
horodaté) / Auto-scroll (ON) / Retour ligne (OFF, swap entre `NoWrapTemplate` et
`WrapTemplate` exposés sur `RootGrid.Resources`). Cap 5000 entrées. Tray "Logs" →
`ShowAndActivate`.

Filets de diagnostic dans `App` : `Application.UnhandledException`, `AppDomain.UnhandledException`,
`TaskScheduler.UnobservedTaskException` → `DebugLog` (préfixes `CRASH`/`CRASH-AD`/`CRASH-TS`).

---

## Tâches ouvertes

- **Quitter depuis le tray ne tue pas tout le process** : `Application.Current.Exit()` dans
  `TrayIconManager.OnQuit` ne termine pas proprement. Suspects : threads WhispEngine
  (Record/Transcribe) non background ou bloqués, icône tray non supprimée via `NIM_DELETE`,
  Windows non fermées explicitement avant Exit. À investiguer : `Dispose`/`Shutdown` explicite
  avant `Exit`, ou fallback `Environment.Exit(0)`.
- **Passe debug logs (A+B+C+D)** : instrumenter les 7 étapes — Modèle / En attente / Capture
  audio / Transcription / Réécriture LLM / Copie clipboard / Collage app cible. Helper
  `DescribeHwnd` (HWND + titre + nom exe), retours Win32 (`SetForegroundWindow`, `SendInput`,
  `SetClipboardData`), foreground avant/après paste. Optionnel : enum `WorkflowStep` + event
  `StepChanged` pour préparer un futur flyout stepper.
- **Focus restore incomplet** : `SetForegroundWindow(_pasteTarget)` avant paste échoue parfois.
  Pistes : `AllowSetForegroundWindow` / `AttachThreadInput` / retry avec délai. À instrumenter
  via la passe debug d'abord.
- **Icône tray manquante** : placeholder à remplacer par un vrai .ico.
- **HUD chrono coupé en haut** : malgré `TextLineBounds="Tight"` + `LineHeight="48"` +
  sous-container `Grid 214x30` (taille bbox Figma exacte) qui laisse le glyphe déborder
  dans les paddings, le haut des chiffres reste tronqué. Hypothèse : la cap-height réelle
  de Bitcount Single Light à 48px est plus grande que les ~34 DIPs estimés, ou l'ascent
  WinUI ne s'aligne pas comme prévu avec la baseline Figma. À investiguer : mesurer le
  glyphe natif WinUI (`TextBlock.ActualHeight` après render), ou augmenter `HUD_HEIGHT`
  de quelques DIPs en compensant côté padding pour garder le visuel Figma.
- **HUD espacement chiffres** : visuellement très large (`00 . . 00 . . 00`). À comparer
  avec un screenshot réel Figma — si Figma rend pareil, c'est juste le tracking faible
  (-2.4 px = -5 % = `CharacterSpacing="-50"`) qui ne suffit pas à compenser les advances
  natifs Bitcount. Sinon, vérifier l'unité du `letterSpacing: -5` Figma (% vs px).
- **UX toolbar LogWindow** : grouper les boutons, envisager split-buttons. Après la passe debug.

---

## Pièges connus

- **AllowUnsafeBlocks obligatoire** : sans, SYSLIB1062/CS0227 sur LibraryImport.
- **WhispInteropTest doit être tué** avant tout test (collision hotkey err 1409).
- **Lifetime WinUI 3** : ne JAMAIS laisser fermer AnchorWindow (Closing→Cancel). Toutes les
  Windows futures (HUD, LogWindow) suivent la même règle. Sortie unique = menu Quitter du
  tray → `Application.Current.Exit()`.
- **Délégué SubclassProc** : champ d'instance (jamais lambda locale) sinon GC.
- **Pas de `UseWindowsForms`** dans le csproj (conflit XAML WinUI 3).
- **Window n'a pas de `Resources` en WinUI 3** (contrairement à WPF) : déclarer les ressources
  XAML sur le `Grid` racine (`<Grid.Resources>`), pas sur `<Window.Resources>` (erreur WMC0011).
- **Objets UI WinUI 3 créés uniquement sur le thread UI** — y compris `SolidColorBrush`. Tout
  objet UI instancié depuis un thread de fond lève `COMException` (`RPC_E_WRONG_THREAD`).
  Pattern : créer brushes/objets dans le constructeur de la Window et les réutiliser dans les
  handlers d'events venant des threads Record/Transcribe.
- **LogWindow jamais affichée** : ne pas toucher à `LogScrollViewer.UpdateLayout()` tant que
  la fenêtre n'a pas été montrée au moins une fois (flag `_isVisible`).
- **Accessibilité constructeur public** : si `AnchorWindow` (public) prend un `TrayIconManager`
  en paramètre, `TrayIconManager` doit être `public` (CS0051).

---

## Plan condensé — étapes restantes

Plan complet (avec ancien nom "WispUI") : `C:\Users\Louis\.claude\plans\jazzy-giggling-widget.md`.
Lire ce plan **avant de coder** chaque étape — il contient les justifications. Substituer
mentalement `WispUI` → `WhispUI` partout.

Étapes 1 → 5 : **faites** (cf. État actuel).

**Étape 6 — Niveau audio RMS.** Nouvel événement `WhispEngine.AudioLevel(float rms)` calculé
dans `Record()` (RMS sur PCM16). HUD : ProgressBar réactive.

**Étape 7 — Proximité souris.** Nouveau `MousePoller` (DispatcherTimer 100 ms + GetCursorPos).
HUD : DoubleAnimation Opacity 1.0↔0.25 selon distance. Démarré dans ShowHud, arrêté dans HideHud.

**Étape 8 — Publish + validation end-to-end.** Publish vers `whisp-ui/publish/` via le MSBuild
de VS (cf. section Build, avec `-t:Restore,Publish -p:PublishDir=...`). Tests à froid après
reboot. Tuer WhispInteropTest avant les tests.

**Post-validation :** mettre à jour la tâche planifiée `Whisp` → exe WhispUI ; mettre à jour
`../../CLAUDE.md` (point d'entrée).

---

## HudWindow — rappel spécification

Window WinUI 3, ~320×64, bas-centre via `DisplayArea.Primary.WorkArea`, `OverlappedPresenter`
non resizable, `ExtendsContentIntoTitleBar=true`.

- **Show :** `MoveAndResize` puis `ShowWindow(SW_SHOWNOACTIVATE)` + `SetWindowPos(HWND_TOP,
  SWP_NOACTIVATE|SWP_NOMOVE|SWP_NOSIZE)`. **Jamais `SetForegroundWindow`**.
- **Hide :** `ShowWindow(SW_HIDE)`.
- Créée une fois dans OnLaunched, jamais détruite (Closing→Cancel).
- Tous les handlers UI marshalés via `DispatcherQueue.TryEnqueue` (events WhispEngine viennent
  de threads de fond).

## Lifetime — App.xaml.cs cible

Ordre `OnLaunched` :
1. `_engine = new WhispEngine()`
2. `_logWindow = new LogWindow()` hors écran + Show(false)
3. `_hudWindow = new HudWindow(...)` hors écran + Show(false)
4. `_tray = new TrayIconManager()`
5. `_anchor = new AnchorWindow(...)` hors écran + Show(false)
6. Dans `AnchorWindow.OnRootLoaded` : `_tray.Register`, `_hotkeyManager.Register`, SW_HIDE
7. Branchement events engine → tray + LogWindow + HudWindow
8. Tray callbacks : Logs → `_logWindow.ShowAndActivate()` ; Quitter → `Application.Current.Exit()`
