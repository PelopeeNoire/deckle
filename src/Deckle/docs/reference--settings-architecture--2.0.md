# SettingsWindow — NavigationView Auto + Frame/Page

État post-refonte 2026-05-04. Cinq pages principales + footer Logs, un
pattern unique de folder picker, SettingsExpander parent pour les
groupes de sliders denses, persistance par module.

## TitleBar natif

`Microsoft.UI.Xaml.Controls.TitleBar` natif (Windows App SDK 1.8).
Caption buttons **Standard** via
`AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard`.
Icône app via `ImageIconSource` nommé. `ExtendsContentIntoTitleBar=true`
+ `SetTitleBar(AppTitleBar)`.

Caption buttons couleurs posées manuellement
(`UpdateCaptionButtonColors`) avec `ActualThemeChanged` pour suivre le
thème live — backgrounds transparents (Mica visible), foreground adapté
light/dark.

## NavigationView adaptatif — PaneDisplayMode=Auto (natif)

Pas de code-behind custom pour les breakpoints. `PaneDisplayMode=Auto`
(défaut WinUI) qui gère seul la bascule :

- **Left** ≥ 1008
- **LeftCompact** 641-1007
- **LeftMinimal** ≤ 640

`PreferredMinimumWidth=320` sur le presenter, donc le mode LeftMinimal
est exposé. Slot `NavigationView.AutoSuggestBox` réservé pour la
recherche live (à activer quand l'inventaire dépasse 15-20 paramètres
visibles d'un coup, NN/G ; pas implémentée à V1 post-refonte).

`DisplayModeChanged` gère uniquement le padding Frame : +48px top en
mode Minimal pour ne pas chevaucher le hamburger (pattern Windows
Terminal Settings).

Contenu :
- `NavigationView.MenuItems` = General → Recording → Transcription →
  Rewriting → Diagnostics.
- `FooterMenuItems` = Logs (`SelectsOnInvoked=False`, clic via
  `ItemInvoked` qui délègue au shell pour ouvrir la LogWindow partagée).

Avant 2026-05-04 : 3 pages (General concentrait Recording et
Diagnostics). Le split a tiré General de 28 réglages / 7 sections vers
6 sections cohérentes, et créé deux pages dédiées pour les surfaces
fonctionnelles distinctes.

## Navigation Frame + Page

`<Frame x:Name="PageFrame" />` dans le slot content du NavigationView.
Navigation via `Type.GetType(tag)` (pattern du sample officiel
Microsoft Learn « Code example »). Garde
`CurrentSourcePageType != pageType` contre les re-Navigate redondants.

Pages : `GeneralPage`, `RecordingPage`, `WhisperPage`, `LlmPage`,
`DiagnosticsPage`. Toutes en `NavigationCacheMode.Required` pour
préserver l'état + un guard `_initializing` autour des sync code-behind
(combos, folder pickers) — empêche les écritures parasites pendant le
`Load()` initial. Le flag se relâche en
`DispatcherQueuePriority.Low` post-layout pour passer les TwoWay
bindings qui appliquent leur valeur initiale après le ctor.

## SettingsCard CommunityToolkit

Package NuGet `CommunityToolkit.WinUI.Controls.SettingsControls`.
Pattern canonique Microsoft Learn : ressource `SettingsCardSpacing=4`,
style `SettingsSectionHeaderTextBlockStyle` (`BodyStrongTextBlockStyle`
+ `Margin 1,30,0,6`), `StackPanel MaxWidth=1000` dans un `Grid` wrapper
(workaround bug `microsoft-ui-xaml#3842`).

## FolderPickerCard — pattern unique pour les chemins

`UserControl` réutilisé partout où un dossier est exposé : Backup
location (General), Telemetry storage (Diagnostics), Models directory
(Whisper, variante éditable). Avant la refonte, trois implémentations
divergentes coexistaient — text label « Set / Change folder / Pick a
folder » selon le lieu, icônes ou pas, TextBox éditable ou pas.

**Layout** : `TextBlock` read-only en `CaptionTextBlockStyle` qui
affiche le path (full-width sous la description, pas serré contre les
boutons). Boutons « Set » + « Open » à droite, **texte uniquement**,
pas d'icônes — décision actée 2026-05-04. Path en
`IsTextSelectionEnabled=True` permet le copier-coller manuel.

**API picker** : `Microsoft.Windows.Storage.Pickers.FolderPicker(WindowId)`.
La nouvelle API qui prend un `WindowId` en constructeur — pas l'ancienne
`Windows.Storage.Pickers.FolderPicker` UWP qui nécessite
`WinRT.Interop.InitializeWithWindow` et casse en élévation. La résolution
de la `Window` traverse `SettingsHost.GetSettingsWindow?.Invoke()` —
le module Settings ne référence pas la fenêtre directement.

**Variante éditable** : `FolderPickerEditableCard` ajoute un `TextBox`
éditable et un slot `RightContent` pour caser un bouton Reset. Utilisée
uniquement pour Models directory. Cas réaliste : cloner un dossier de
modèles depuis un autre poste, et taper le chemin résultant.

**Path resolution** : la card lit `Path` (DependencyProperty TwoWay).
Si vide, affiche `DefaultPath` en placeholder transparent — ne stocke
pas le default dans le setting. Préserve la sémantique « empty = système
choisit le défaut ».

Important : le card lui-même est un `UserControl` minimal (pas de
`SettingsCard` wrapper interne). C'est le consumer qui le pose dans un
`<controls:SettingsCard ContentAlignment="Vertical">` — ça permet de
réutiliser le card dans `SettingsExpander.Items` qui rejette les
UserControls qui wrappent eux-mêmes un SettingsCard.

## Pattern SettingsExpander parent pour groupes de sliders

Quand plusieurs sliders apparentés ont chacun 3-5 lignes de description
(Decoding : Temperature + Fallback step ; Confidence : Entropy +
Logprob + No-speech), un layout horizontal serre le slider à
`MinWidth=180` et tronque la description.

**Pattern retenu** : `SettingsExpander` parent (header + icône) +
sliders enfants en `SettingsCard ContentAlignment="Vertical"`. La
description prend toute la largeur, le slider full-width en dessous.
Staged disclosure NN/G : sliders cachés derrière l'expander, visibles
seulement quand le user les cherche.

Glyphes : `&#xE9CA;` (Tuner) pour Decoding, `&#xE9D9;` (gauge) pour
Confidence. Children sans HeaderIcon — l'identité visuelle est portée
par le parent.

L'`InfoBar` qui dépend d'un slider du groupe
(`TemperatureIncrementWarning`) reste **hors** expander pour rester
visible quand le groupe est replié — un warning « fallback désactivé »
doit s'afficher quel que soit l'état du groupe.

## GeneralPage — 6 sections câblées

Niveau « shell » et configuration globale. Auto-save via
`SettingsService` (settings.json shell).

Ordre :
**Hotkeys** → **Appearance** → **Behaviour** → **Startup** → **Backup &
restore** → **Application data**.

- **Hotkeys** — 3 read-only display : Principal hotkey (`` Win + ` ``,
  active la transcription), Primary rewrite hotkey (`` Shift + Win + ` ``),
  Secondary rewrite hotkey (`` Ctrl + Win + ` ``).
- **Appearance** — ComboBox System / Light / Dark, appliqué live sur
  toutes les fenêtres via `SettingsHost.ApplyTheme`.
- **Behaviour** — auto-paste après transcription
  (`shell.Paste.AutoPasteEnabled`) + overlay HUD (master toggle, fade
  on proximity, animations, screen position). Migré depuis Recording
  le 2026-05-04 — ces réglages décrivent ce que Deckle fait pour
  l'utilisateur, pas le pipeline de capture.
- **Startup** — Autostart (HKCU\…\Run\Deckle, hors AppSettings, géré
  par `AutostartService`) + Warmup on launch.
- **Backup & restore** — `SettingsExpander` PowerToys-style.
  `SettingsBackupService` : snapshot ponctuel
  `settings-YYYYMMDD-HHmmss.json` sous `<ConfigDirectory>/backups/`,
  restore via swap atomique. `BackupDirectory` configurable
  (`FolderPickerCard`) pour pointer vers OneDrive/Drive.
- **Application data** — Data folder display + Open in Explorer button
  + Re-run setup button.

`HyperlinkButton` Reset par section (Appearance, Behaviour, Startup).
Pattern Win11 Settings : restore les défauts de la section seule, pas
toute la page.

## RecordingPage — capture pipeline

Page extraite de General le 2026-05-04 (slice S3). Concentre tout ce
qui relève strictement du pipeline de capture audio.

- **Microphone** — `ComboBox` Audio input device, énumération `waveIn`
  Win32, `AudioInputDeviceId` (-1 = WAVE_MAPPER) + « System default »
  en index 0.
- **Voice level window** — `SettingsExpander` master (Auto-calibration
  toggle dans header) + 3 sliders enfants (Floor MinDbfs, Ceiling
  MaxDbfs, Curve exponent). Les drags poussent live dans
  `AudioLevelMapper` via `SettingsHost.ApplyLevelWindow` — le HUD
  reflète la nouvelle courbe à la sub-window suivante sans restart.

Persistance : `CaptureSettingsService` (capture.json), séparé du shell
depuis la slice C2b (per-module persistence).

## DiagnosticsPage — section Telemetry

Page extraite de General le 2026-05-04 (slice S2). Vocabulaire
interne : *log* = temps réel (LogWindow), *telemetry* = persisté sur
disque (JSONL). « Diagnostics » est l'ombrelle. Structurée pour
accueillir des sections futures (settings de log temps réel : niveaux,
filtrage, capacité du buffer LogWindow) — V1 contient une seule
section Telemetry.

**Telemetry** (5 opt-ins, tous off par défaut) :

1. **Application log to disk** — toggle, persiste l'événementiel dans
   `app.jsonl`. En haut de section par décision Louis.
2. **Microphone telemetry** — toggle + consent dialog (privacy : résumé
   RMS par recording). Glyph microphone (`&#xE720;`).
3. **Latency telemetry** — toggle, mesures de pipeline par run.
4. **Corpus** — `SettingsExpander` master (Corpus toggle dans header +
   consent) + Audio corpus enfant (toggle + consent séparé).
5. **Storage folder** — `FolderPickerCard` pointant vers le dossier où
   sont sérialisés les `.jsonl`.

Pattern consent dialog : re-entry guards (`_suppressMicrophoneToggled`
etc.) — un revert programmatique post-Cancel ne re-ouvre pas le dialog.

Persistance : `TelemetrySettingsService` (telemetry.json).

## WhisperPage — 6 sections câblées

Auto-save. `MarkRestartPending()` sur les settings qui exigent un
restart d'engine (Model, UseGpu) — pousse une `InfoBar` « Restart
required » + footer avec boutons Restart now / Discard.

Ordre des sections post-refonte (2026-05-04) :

1. **Transcription** — Language (ComboBox éditable, langues ISO) →
   Initial prompt (`SettingsExpander` avec `TextBox` enfant) → GPU
   acceleration (toggle) → **Whisper model** (`SettingsExpander` avec
   AutoSuggestBox dans le header + `FolderPickerEditableCard` Models
   directory en enfant). Le model va en dernier parce que (a) il
   déclenche un restart et (b) il porte la storage location pour les
   weights téléchargés. Pas de section Storage séparée — la directory
   vit avec le model qu'elle pointe.
2. **Speech filtering** (VAD) — `SettingsExpander` master + 6 enfants
   (Threshold, Min speech ms, Min silence ms, Max speech sec, Speech
   pad ms, Overlap).
3. **Output filters** — Suppress non-speech tokens, Suppress blank,
   Suppress regex (TextBox dans expander).
4. **Context and segmentation** — Use context, Max tokens.
5. **Decoding** — `SettingsExpander` parent + 2 sliders enfants
   (Temperature, Fallback step).
6. **Confidence thresholds** — `SettingsExpander` parent + 3 sliders
   enfants (Entropy, Logprob, No-speech).

Bouton Reset all en haut de page + reset par-card hover-revealed
(pattern Win11 Settings : opacité 0 par défaut, 1 au PointerEntered de
la card). Pour les expanders qui hébergent un sub-card (Model +
ModelsDirectory), le PointerEntered sur l'expander révèle les deux
resets simultanément.

Bug Slider `Minimum` en XAML release : tout Slider dont la range exclut
0 doit être configuré en code-behind post-`InitializeComponent`. Détail
dans la mémoire `project_winui3_slider_bug.md`.

Persistance : `WhispSettingsService` (whisp.json).

## LlmPage — sections câblées (intouchable)

Sert de référence interne de cohérence — la refonte 2026-05-04 ne l'a
pas touchée.

- **General** — toggle Enabled + Ollama endpoint (auto-save).
- **Models** — état de la connexion Ollama (reachable + liste de
  modèles présents) avec refresh manuel.
- **Profiles** — liste éditable de profils de réécriture
  (`RewriteProfile` : name, model, system prompt, temperature,
  num_ctx_k, top_p, repeat_penalty). Trois profils par défaut alignés
  sur les brackets de cleanup (Lissage / Affinage / Arrangement) avec
  prompts pré-écrits, à utiliser tel quel ou à adapter.
- **Auto-rewrite rules** — pivot RuleMetric (Word count par défaut /
  Duration). Construction impérative en code-behind (pattern documenté
  dans la mémoire `reference_winui_itemsrepeater_datacontext.md` —
  refonte 2026-04-28).
- **Shortcut slots** — Primary (`` Shift + Win + ` ``) et Secondary
  (`` Ctrl + Win + ` ``), opt-in avec sentinel « (None) » stocké comme
  null.

Persistance : `LlmSettingsService` (llm.json).

## SettingsHost — registry de delegates côté shell

Les pages Settings appellent des side-effects qui vivent côté App
(applique le thème live sur toutes les fenêtres, push une nouvelle
level window dans `AudioLevelMapper`, ouvre le first-run wizard…).
Pour découpler `Deckle.Settings` de `Deckle.exe`, le module Settings
expose un `SettingsHost` static avec des delegates wirables :

- `ApplyTheme(string theme)` — appelé quand le user change le thème.
- `ApplyLevelWindow(LevelWindow lw)` — appelé quand un slider voice
  level bouge.
- `OpenSetupWizard()` — appelé quand le user clique « Re-run setup »
  dans GeneralPage.
- `GetSettingsWindow()` — résout la `Window` pour passer un `WindowId`
  au FolderPicker.

L'App-side wire ces delegates dans son `OnLaunched`. Les pages les
invoquent via `SettingsHost.ApplyTheme?.Invoke(...)` — null-safe quand
le shell n'a pas wiré (ex : test isolé du module Settings, ou
intégration partielle).

## Persistance — un service par module

Cinq services indépendants depuis la slice C2b (per-module
persistence). Chacun écrit son propre fichier JSON, débounced 300ms,
atomic write-then-swap.

| Service | Fichier | Contenu |
|---|---|---|
| `SettingsService` | `settings.json` | Shell : Hotkeys, Theme, Behaviour (auto-paste + overlay), Startup, Paths.BackupDirectory |
| `CaptureSettingsService` | `capture.json` | Microphone, Voice level window |
| `TelemetrySettingsService` | `telemetry.json` | Diagnostics opt-ins + storage path |
| `WhispSettingsService` | `whisp.json` | Whisper engine settings |
| `LlmSettingsService` | `llm.json` | Ollama + profiles + rules + shortcuts |

Chaque service expose : `Current` (POCO singleton), `Save()`
(debounced), `Changed` event. Tous sous `AppPaths.ConfigDirectory` (à
côté de l'exe en dev unpackaged, sous `LocalState/config/` en packaged
MSIX).

Sauvegarde immédiate à chaque modif (pas de bouton Appliquer /
Sauvegarder) — pattern Win11 Settings.

## Backdrop + presenter

Mica. `OverlappedPresenter` classique (min, max, resize). Close →
Cancel + Hide (réutilisée via tray). Resize 960×1440 initial.

## Restart cible

`App.RestartApp(pageTag?)` relance l'exe avec `--settings [pageTag]`,
`OnLaunched` détecte le flag et rouvre les Settings sur la bonne page.
