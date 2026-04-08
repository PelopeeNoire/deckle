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

**V1 atteinte. Étapes 1 → 7 validées runtime. Transcription fonctionnelle de bout en bout, HUD complet, prêt pour publication (étape 8).**

**HUD — coloration progressive des chiffres** : chaque chiffre du chrono qui change au moins une fois passe en rouge `SystemFillColorCriticalBrush` (theme resource Windows, suit light/dark) et y reste jusqu'au prochain `ShowRecording`. Le `0` initial reste neutre tant qu'il n'a pas bougé. Les Run idle ne portent pas de Foreground local — ils héritent de `ClockText.Foreground = {ThemeResource TextFillColorPrimaryBrush}`, donc auto-réactif au thème. Reset = `ClearValue(TextElement.ForegroundProperty)` sur chaque Run nommé. **Theme switch runtime** : `RootGrid.ActualThemeChanged` re-résout le brush critical et le réapplique aux chiffres déjà allumés (un Foreground assigné en code ne suit pas un ThemeResource binding, il faut le réassigner manuellement).

Bootstrap (AnchorWindow invisible + ancre lifetime), pipeline complet
(`WhispEngine` + tray + LogWindow + hotkey Alt+\` toggle Start/Stop), HUD complet :
layout Figma (chrono Bitcount Single Light + beacon/ProgressRing dans Mica arrondi),
chrono câblé `MM.SS.cc` via `CompositionTarget.Rendering` (cadence vsync, pas de
`DispatcherTimer` → pas de jitter), fige à la transition record→transcribe.

**Fade proximité souris (étape 7)** : approche event-driven via Raw Input
(`RegisterRawInputDevices` + `RIDEV_INPUTSINK`) interceptée par subclass HWND
(SubclassProc en champ d'instance, ID `0x48554450`). Alpha layered global
(`WS_EX_LAYERED` + `SetLayeredWindowAttributes(LWA_ALPHA)`) qui couvre Mica + content,
mappé à la distance curseur via smoothstep (`NEAR_RADIUS_DIP=10`, `FAR_RADIUS_DIP=200`).
Pas de polling, pas d'animation timer — la fluidité vient de la fréquence des `WM_INPUT`.
Approche inspirée de `D:\projects\environment\taskbar-overlay-cs`.

**Scripts `whisp-ui/build-run.ps1` et `whisp-ui/publish.ps1`** (un cran au-dessus du
projet, non versionnés — chemins machine). `build-run` : tue WhispUI s'il tourne, build
via MSBuild VS, lance l'exe. Switches `-Restore`, `-NoRun`, `-Wait`, `-Configuration`,
`-MsBuild`. `publish` : même logique, target `Restore;Publish` vers `whisp-ui/publish/`,
switches `-Configuration`, `-Open`, `-MsBuild`. MSBuild résolu via `-MsBuild` >
`$env:WHISPUI_MSBUILD` > `vswhere` (cf. section commentée en tête de chaque script).
Chez Louis : `setx WHISPUI_MSBUILD "D:\bin\visual-studio\visual-studio-2026\MSBuild\Current\Bin\amd64\MSBuild.exe"`.

**Cible paste — re-capture au Stop avec filet PID** : `_pasteTarget` est captée au Start
puis **re-capturée au Stop** via `GetForegroundWindow()`. Permet de coller dans le champ
texte courant si l'utilisateur a basculé d'app pendant l'enregistrement. Filet : si le
foreground au Stop appartient au process WhispUI (HUD/LogWindow activé par un clic), on
garde la cible Start — évite le faux positif "collé dans nos propres logs". Voir
[WhispEngine.cs:169](WhispEngine.cs#L169).

**Fix race paste / Hide HUD (rendez-vous synchrone)** : avant, `HudWindow.Hide()` était
déclenché en async via `TranscriptionFinished` après `PasteFromClipboard`. `SendInput`
étant asynchrone (il enfile les frappes dans la queue d'input du thread cible), le `SW_HIDE`
async pouvait redistribuer l'activation pendant que le Ctrl+V était encore en vol — la
LogWindow ouverte à côté pouvait récupérer le focus, le coller atterrissait là, et le log
vert "Bout en bout OK" mentait. Fix : nouveau callback `WhispEngine.OnReadyToPaste` invoqué
synchronement entre `CopyToClipboard` et `PasteFromClipboard` ; câblé dans `App.xaml.cs` à
`HudWindow.HideSync()` qui marshalle le `Hide` sur le thread UI via `DispatcherQueue` et
**bloque l'appelant** sur un `ManualResetEventSlim` jusqu'à ce que `SW_HIDE` soit effectif.
Plus rien dans WhispUI ne touche à l'activation entre `SetForegroundWindow(target)` et la
livraison des frappes. Pas de sleep, pas de polling — rendez-vous explicite par signal OS.
Voir [HudWindow.xaml.cs:255](HudWindow.xaml.cs#L255), [WhispEngine.cs:36](WhispEngine.cs#L36),
[WhispEngine.cs:494](WhispEngine.cs#L494), [App.xaml.cs:81](App.xaml.cs#L81).

**LogWindow v3** : refonte basée sur design Figma (`fAjzDDUpFW1InvLl5xL83R`, node `98:9790`).
Fenêtre classique (`OverlappedPresenter`), Close→Cancel+Hide, **thème système** (plus de
`RequestedTheme="Dark"` forcé), `SystemBackdrop = MicaBackdrop`. Title bar custom
(`ExtendsContentIntoTitleBar=true`) en 3 colonnes : icône+titre draggable à gauche,
`AutoSuggestBox` de recherche centrée, réserve 138px à droite pour les caption buttons
système. Drag region limitée à la colonne gauche (`SetTitleBar(AppTitleBarLeftDrag)`)
pour que le SearchBox reçoive les clics. Sous la title bar, command bar en 2 zones :
`SelectorBar` Full / Filtered à gauche + `CommandBar` (icônes Segoe Fluent) à droite avec
deux groupes séparés par `AppBarSeparator` — édition (Copy E8C8 / Save E74E / Clear E74D)
puis affichage (Auto scroll E70D / Wrap E751 toggles). Cap 5000 entrées. Tray "Logs" →
`ShowAndActivate`.

**Modèle de données** : `enum LogLevel { Info, Warning, Error }` (public). API thread-safe
`Log` / `LogWarning` / `LogError`. Deux collections : `_entries` (List, tampon complet) et
`_visible` (ObservableCollection bindée à `LogItems`). Filtre = `Matches()` qui combine
selector (Filtered masque Info, garde Warning+Error) + recherche live (`IndexOf` case-insensitive).
`ApplyFilter()` rebuild `_visible`. Sur overflow, on retire la plus vieille entrée des deux
collections (LogEntry est une `class`, ref equality, pas de collision). **Copy/Save opèrent
sur `_visible`** — l'utilisateur copie/exporte ce qu'il voit.

**Couleurs** via theme resources Windows résolus une fois en constructeur (snapshot, ne
réagit pas au theme switch runtime — acceptable pour debug) : `TextFillColorPrimaryBrush`
(Info), `SystemFillColorCautionBrush` (Warning), `SystemFillColorCriticalBrush` (Error).
**Wrap** : le toggle swap `NoWrapTemplate`/`WrapTemplate` **et** bascule
`HorizontalScrollBarVisibility` entre `Auto` et `Disabled` — sinon `TextWrapping="Wrap"`
ne s'applique pas (le `ScrollViewer` mesure son contenu en largeur infinie tant que le
scroll horizontal est autorisé). `LogWarning` exposé mais pas encore appelé par `WhispEngine`
— prêt pour la passe debug à venir.

Filets de diagnostic dans `App` : `Application.UnhandledException`, `AppDomain.UnhandledException`,
`TaskScheduler.UnobservedTaskException` → `DebugLog` (préfixes `CRASH`/`CRASH-AD`/`CRASH-TS`).

**Passe debug logs A→Z (faite)** : 5 niveaux côté API (`Verbose` / `Info` / `Step` / `Warning` / `Error`)
et 3 modes de filtre dans le `SelectorBar` (Full / Filtered / Critical). Sémantique couleur :
**Verbose blanc** = bruit / durée de vie (heartbeats RECORD `+Ns capturés`, dumps par segment Whisper,
plomberie clipboard `GlobalAlloc`/`OpenClipboard`, plomberie PASTE `cible attendue`/`SetForegroundWindow`/
`contrôle focusé`, `Mémoire passée au chunk suivant`, `[DONE]` recap timings non vérifié) — masqué en
Filtered ; **Info bleu sémantique Fluent** (`#005FB7` light / `#60CDFF` dark, brush construit en code car
le `SystemFillColorAttentionBrush` de la theme dictionary peut résoudre sur l'accent système chez
certains users) = jalons rares et importants (`Enregistrement démarré`, `Chunk N extrait → pipeline`,
`Chunk final extrait`, `Capture terminée`, `Chunk N → texte recollé`, `Texte copié`, `Ctrl+V envoyé`) ;
**Step vert** = jalons rares et **vérifiés** — exactement deux par session normale : `Modèle chargé en
N ms` (init, vérifié `_ctx ≠ 0`) et `Bout en bout OK — N chars collés dans <cible>` (émis uniquement si
`PasteFromClipboard` retourne `true`). Filtered masque uniquement Verbose. Critical = Warning + Error.

**`PasteFromClipboard` retourne `bool`** et refuse de coller (Warn avec mode opératoire « le presse-papier
contient le texte — colle manuellement avec Ctrl+V ») dans tous ces cas : `_pasteTarget == 0`, cible
appartient au process WhispUI lui-même (`GetWindowThreadProcessId == GetCurrentProcess().Id` — filet
contre le faux positif « collé dans WhispUI logs »), `SetForegroundWindow` n'a pas réellement ramené
la cible au foreground (vérifié via `GetForegroundWindow()` après sleep, pas via le retour bool),
`GetFocusedClass == null`, `SendInput` partiel. Si tout passe, le récap final devient le Step vert
de bout en bout ; sinon `[DONE]` Verbose timings + le Warn orange explicatif.

Cartographie instrumentée historique : `WhispEngine` expose
maintenant `LogStepLine` + `LogWarningLine` en plus de `LogLine` / `LogErrorLine`. Cartographie
instrumentée : MODEL (path/taille/use_gpu, Step au succès, Warn si fichier absent), HOTKEY
(`DescribeHwnd` cible + Warn si pas de focus clavier), RECORD (heartbeat 5s en Info au lieu
d'une ligne par buffer, Warn sur buffers vides ou retard, Step pour démarrage / chunks 30s /
chunk final / fin), TRANSCRIBE (Step à réception et succès, segments numérotés avec timestamps
+ `no_speech_prob`, texte recollé en Info, Warn paramétré sur hallucination avec pattern
matché, Warn sur répétition heuristique, mémoire `initial_prompt` du chunk suivant en Info),
CLIPBOARD (méthode d'instance, instrumentation `GlobalAlloc`/`OpenClipboard`/`SetClipboardData`
+ re-lecture de vérification post-copie), PASTE (`DescribeHwnd` cible, foreground avant/après,
retours `SetForegroundWindow`/`SendInput`, focus clavier vérifié via `GetFocusedClass`), LLM
(callbacks `onWarn`/`onStep`/`onInfo`, fallback détaillé avec type d'exception). Helper
`Win32Util.DescribeHwnd` (exe / titre / classe focusée). Step coloré en
`SystemFillColorSuccessBrush` (vert) — pas l'accent système qui peut être gris chez l'utilisateur.

---

## Tâches ouvertes

- **Paste "fantôme" intermittent**. Symptôme : le pipeline log vert "Bout en bout OK" +
  PASTE "Ctrl+V envoyé à <cible>", mais rien n'apparaît dans le champ cible. Récurrent,
  pas systématique. Hypothèse Louis : la transcription n'a peut-être pas eu lieu (chunks
  capturés mais pas de texte recollé final), donc le clipboard contiendrait l'ancienne
  valeur ou rien — et le `SendInput` Ctrl+V ne ferait "rien" côté cible. À investiguer
  au prochain occurrence : capturer les logs complets de la session fautive (vérifier
  présence de la ligne TRANSCRIBE "texte recollé" et de la ligne CLIPBOARD "Texte copié
  (N chars)" avec `N > 0`). Si N = 0 ou ligne absente → bug en amont (Whisper / pipeline).
  Si N > 0 mais paste vide → bug `SendInput` ou délivrance, malgré le réordonnancement.
- **Hallucinations : filtrage trop grossier**. `MatchHallucination` rejette tout le chunk
  30s dès qu'un pattern (`Sous-titrage`, `Radio-Canada`, `[BLANK_AUDIO]`, `Sous-titres`, `SRC`)
  apparaît n'importe où dans le texte recollé ([WhispEngine.cs:393](WhispEngine.cs#L393)).
  Conséquence : 25s de parole utile sont jetées si Whisper hallucine 1s en bonus. Cible :
  filtrer **par segment**, pas par chunk — jeter le segment hallucinatoire et garder les
  autres. Idem prendre en compte `no_speech_prob` (déjà loggé) comme critère. Et ne réinjecter
  en `initial_prompt` que les segments qui ont passé un seuil de confiance (sinon une
  hallucination en fin de chunk N contamine le démarrage du chunk N+1 — observé : passage
  spontané au japonais après une phrase anglaise propre).
- **Quitter depuis le tray ne tue pas tout le process** : `Application.Current.Exit()` dans
  `TrayIconManager.OnQuit` ne termine pas proprement. Suspects : threads WhispEngine
  (Record/Transcribe) non background ou bloqués, icône tray non supprimée via `NIM_DELETE`,
  Windows non fermées explicitement avant Exit. À investiguer : `Dispose`/`Shutdown` explicite
  avant `Exit`, ou fallback `Environment.Exit(0)`.
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
- **LogWindow caption buttons** : avec title bar 48px et `ExtendsContentIntoTitleBar=true`,
  les caption buttons système peuvent ressortir à 32px en haut (gap visuel). Si c'est moche,
  ajouter `AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall` après
  `ExtendsContentIntoTitleBar = true`. Laissé en mode auto pour partir simple.
- **LogWindow drag region partielle** : seule la colonne icône+titre est draggable. La bande
  vide entre le SearchBox et les caption buttons ne déplace pas la fenêtre. Fixable via
  `InputNonClientPointerSource` si besoin.

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

**Étape 6 — Niveau audio RMS.** *Reportée post-V1.* Nouvel événement
`WhispEngine.AudioLevel(float rms)` calculé dans `Record()` (RMS sur PCM16).
HUD : ProgressBar réactive.

**Étape 7 — Proximité souris.** **Faite**, mais avec une approche différente du plan
initial (qui prévoyait `MousePoller` polling 100 ms + animation `Opacity` 1.0↔0.25).
Implémentation actuelle : Raw Input event-driven + alpha layered + smoothstep continu
(cf. *État actuel*). Le plan initial reste obsolète sur ce point.

**Étape 8 — Publish + validation end-to-end + autostart Windows + partage.** Publish vers
`whisp-ui/publish/` via le MSBuild de VS (cf. section Build, avec `-t:Restore,Publish
-p:PublishDir=...`). Nettoyage du dépôt avant partage (vérifier qu'aucune info sensible
ne traîne dans ce qui sera distribué — modèles Whisper exclus). Autostart Windows
(tâche planifiée `Whisp` à mettre à jour vers WhispUI.exe, ou clé Run). Tests à froid
après reboot. Tuer WhispInteropTest avant les tests. **C'est l'étape de la prochaine
session de travail.**

**Post-validation :** mettre à jour la tâche planifiée `Whisp` → exe WhispUI ; mettre à jour
`../../CLAUDE.md` (point d'entrée).

**Post-V1 — LogWindow responsive (et future SettingsWindow).** Rendre la `CommandBar` de
LogWindow réactive à la largeur de la fenêtre, en s'appuyant au maximum sur le comportement
standard WinUI : `AutoSuggestBox` qui se replie en icône loupe à gauche, et `AppBarButton`
qui basculent dans le menu *More* (`…`) au fur et à mesure via `PrimaryCommands` /
`SecondaryCommands` et `DefaultLabelPosition`. Si le mécanisme natif ne suffit pas, gérer
manuellement via `SizeChanged` : à un premier seuil, déplacer Copy/Save/Clear en
`SecondaryCommands` (AutoScroll + Wrap restent visibles) ; à un second seuil, basculer
aussi AutoScroll + Wrap dans le menu More. `MinWidth` ~300–400 px. Même schéma à reprendre
pour la future SettingsWindow — penser le pattern une fois, le réutiliser. Vérifier d'abord
si WinUI 3 fournit déjà ce comportement clé en main avant de coder du custom ; sinon
remonter à Louis pour qu'il fasse le design Figma.

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
