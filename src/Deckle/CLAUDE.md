# CLAUDE.md — Deckle

App WinUI 3 unpackaged, point d'entrée unique du projet transcription
côté UI.

Avant un test runtime, tuer toute instance déjà en cours (Deckle ou
prototype antérieur) — deux processus qui appellent `RegisterHotKey`
sur la même combinaison se collisionnent (`err 1409`).

Roadmap et état d'avancement : tenus dans la mémoire de session de
l'agent (Claude Code auto-memory). À relire en début de session.

---

## Build

`dotnet build` est cassé sur ce projet (bug `XamlCompiler.exe` MSB3073,
détail dans le CLAUDE.md racine). Builder via `MSBuild.exe` de VS 2026
(MSBuild Framework, `MSBuildRuntimeType=Full`).

Depuis `src/Deckle/`, PowerShell sans admin (remplacer
`<msbuild-path>` par le chemin local du `MSBuild.exe` Framework livré
avec Visual Studio 2026, par défaut sous
`<vs-install>\MSBuild\Current\Bin\amd64\MSBuild.exe`) :

```
& "<msbuild-path>" `
    -t:Restore,Build -p:Configuration=Release -p:Platform=x64
```

Sortie : `bin\x64\Release\net10.0-windows10.0.19041.0\Deckle.exe`
(self-contained).

État du csproj :

- `Microsoft.WindowsAppSDK` : `1.8.260317003` (stable officielle).
- `global.json` épingle SDK `10.0.104` — conserver.
- `<EnableMsixTooling>true</EnableMsixTooling>` force le pipeline
  Publish à générer `Deckle.pri` dans `PublishDir`. Sans ça, en
  WindowsAppSDK 1.8 unpackaged, les `.xbf` embarqués dans le `.pri`
  sont injoignables et l'app démarre sans fenêtre. Cf.
  `microsoft/WindowsAppSDK#3451`.

Scripts `scripts/build-run.ps1` et `scripts/publish-unpackaged.ps1`
(versionnés). `build-run` tue Deckle s'il tourne, build via MSBuild
VS, lance l'exe — switches `-Restore`, `-NoRun`, `-Wait`,
`-Configuration`, `-MsBuild`. `publish-unpackaged` : target
`Restore;Publish` vers `publish/` (racine repo) — sortie folder
unpackaged sans installer ni MSIX, modèles et DLLs natives non
embarqués (téléchargés au first run). MSBuild résolu via `-MsBuild`
> `$env:DECKLE_MSBUILD` > `vswhere`. Pour court-circuiter `vswhere`
(et accélérer le démarrage du script), définir une fois pour toutes
la variable d'environnement utilisateur :

```
setx DECKLE_MSBUILD "<msbuild-path>"
```

---

## Pièges WinUI 3 connus

- **`AllowUnsafeBlocks` obligatoire** dans le csproj — sans, `SYSLIB1062`
  / `CS0227` sur `LibraryImport`.
- **Tuer toute instance précédente** avant tout test — collision
  `RegisterHotKey` err 1409 si une autre instance tient déjà la
  combinaison.
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
régressions notification) dans [docs/reference--hud--1.0.md](docs/reference--hud--1.0.md).

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

## Règles UX non négociables

### Clipboard — 2 états maximum par transcription

Le clipboard porte au plus deux contenus successifs sur la durée d'une
transcription : (1) la transcription brute Whisper, (2) le texte
réécrit par le LLM (si profil actif). **Jamais d'accumulation token par
token, jamais d'incréments mot par mot.** L'historique du presse-papier
système doit rester propre — un utilisateur qui ouvre l'historique
clipboard après une transcription voit `raw` et `rewrite`, pas
`raw1`, `raw1+w2`, `raw1+w2+w3`, …

Conséquence pour un éventuel streaming LLM : si on streame, on
**remplace** l'objet clipboard en place (ou on le supprime puis on
ré-ajoute), pas d'append. Granularité acceptable : phrase entière (sur
détection de point), ou intervalle régulier (toutes les ~5 s), pas
token par token. La règle prime sur le gain de latence perçue.

### Pré-chargement VAD au hotkey (idée notée)

Le VAD whisper.cpp prend ~5 % du temps audio quel que soit le backend
GPU/CPU — confirmé sur 700+ runs de télémétrie. Une piste plausible :
pré-charger le contexte VAD dès le démarrage de l'enregistrement
(hotkey reçu), libérer au stop. À tester avec mesure avant/après une
fois l'instrumentation en place. Pas implémenté pour l'instant.

---

## Télémétrie — source unique

Tout ce que Deckle observe au runtime passe par
`Logging.TelemetryService.Instance` — c'est le hub central, pas un
détail d'implémentation. Quatre canaux :

- `Log(source, message, level, feedback)` — événements applicatifs.
- `Latency(payload)` — un row `LatencyPayload` par transcription terminée.
- `Corpus(payload)` — texte brut Whisper pour benchmark, gated par setting.
- `Microphone(payload)` — résumé RMS par recording, gated par setting.

Chaque appel construit un `TelemetryEvent` (timestamp + kind + session +
payload + texte précompilé pour LogWindow) et le dispatche aux
`ITelemetrySink` enregistrés. Les deux sinks de prod sont
`JsonlFileSink` (persiste dans `<storage>/{app,latency,microphone,corpus}.jsonl`)
et `LogWindow` (display live).

**Invariant — single source of truth.** Toute observation runtime *doit*
emprunter ce chemin. Pas de `Console.WriteLine` parallèle, pas d'écriture
fichier hors sink, pas de mesure cachée dans une variable locale qui ne
remonte nulle part. Si une métrique mérite d'exister, elle mérite de
passer par le payload structuré (pour le JSONL) ET par le texte
précompilé (pour LogWindow).

**Quand on ajoute une étape mesurée :**

1. **Respecter la nomenclature** — durées en `ms` (entier) ou `sec`
   (1 décimale), tokens en `tok`, caractères en `chars`. Suffixe
   `_ms`/`_sec`/`_tok`/`_chars` obligatoire dans le nom du champ. La
   table canonique vit dans
   [docs/reference--logging-inventory--1.0.md](docs/reference--logging-inventory--1.0.md)
   section "Vocabulaire de mesures" — la **lire avant** d'inventer
   un nom. Toute divergence est une régression et doit être
   refusée en review.
2. Étendre le `record` payload approprié dans
   [Logging/TelemetryEvent.cs](Logging/TelemetryEvent.cs) avec un
   `JsonPropertyName` snake_case (le sink JSONL le sérialise
   automatiquement, sans nouveau code).
3. Mettre à jour le `text` précompilé dans `TelemetryService.<Kind>()`
   pour que la ligne LogWindow reflète la nouvelle info — au minimum
   un résumé compact, le détail complet vit dans le JSONL.
4. Passer la mesure de `[à instrumenter]` à `[logué]` dans
   [docs/reference--logging-inventory--1.0.md](docs/reference--logging-inventory--1.0.md),
   section concernée. Mettre à jour le gabarit standard si la ligne
   Verbose change.
5. Vérifier que les consommateurs existants ne cassent pas (recherche
   sur `payload.<Field>` ou `<PayloadType>(...)` dans tout le projet).

L'inventaire `reference--logging-inventory--1.0.md` est la **doc
canonique** : quoi mesurer, à quel niveau, dans quel format. Avant
d'ajouter ou de modifier un log, le lire. Sera promu un jour en
document de nomenclature séparé ; tant que ce n'est pas fait, c'est
ce fichier qui fait foi.

---

## Journal d'implémentation — dossier `docs/`

Les détails par sous-système vivent dans `docs/`. Fichiers lus à la
demande, uniquement quand je touche au sous-système concerné — pas
chargés en contexte par défaut. Avant modification d'un sous-système,
lire son fichier `docs/*.md`.

- [docs/reference--hud--1.0.md](docs/reference--hud--1.0.md) — spec HudWindow
  (positioning, backdrop Acrylic, DPI), coloration progressive chrono,
  fade proximité souris (Raw Input + alpha layered + smoothstep),
  contrainte ombre layered.
- [docs/reference--logwindow--1.0.md](docs/reference--logwindow--1.0.md) —
  TitleBar natif + SearchBox, SelectorBar + CommandBar responsive,
  modèle de données (5 niveaux, cap 5000), couleurs `ThemeDictionaries`,
  templates/selector, piège wrap/scroll.
- [docs/reference--pipeline-transcription--1.0.md](docs/reference--pipeline-transcription--1.0.md) —
  pipeline monobloc (`new_segment_callback`, plus de chunking externe),
  instrumentation par segment, defaults whisper.cpp restaurés (piège
  `entropy_thold` inversé), hot-reload via `SettingsService`.
- [docs/reference--paste-behavior--1.0.md](docs/reference--paste-behavior--1.0.md) —
  re-capture cible au Stop avec filet PID, fix race `HideSync`
  (rendez-vous synchrone via `ManualResetEventSlim`), refus explicites
  de `PasteFromClipboard`, bug paste fantôme intermittent.
- [docs/reference--settings-architecture--1.0.md](docs/reference--settings-architecture--1.0.md) —
  NavigationView Auto (natif, 3 modes), TitleBar natif Standard,
  Frame+Page, SettingsCard CommunityToolkit, GeneralPage 4 sections
  câblées, WhisperPage 6 sections, persistance JSON portable,
  restart ciblé.
- [docs/reference--logging-inventory--1.0.md](docs/reference--logging-inventory--1.0.md)
  — inventaire normatif des mesures par étape (vocabulaire d'unités,
  niveaux de sévérité, gabarits standards, recap UserFeedback). Référence
  unique avant d'ajouter ou de modifier un log.
