# HudWindow — spec et implementation

## Spec Window

Window WinUI 3, 314x78 DIPs, positionnee via `DisplayArea.Primary.WorkArea`. Centre horizontalement par design (miroir des HUD natifs Win11 — volume, luminosite, capture ecran) ; l'ancrage vertical est configurable via `Settings.Overlay.Position` (TopCenter ou BottomCenter, default BottomCenter). Les anciennes valeurs de coin (TopLeft / BottomRight / ...) eventuellement presentes dans un `settings.json` legacy sont normalisees vers TopCenter/BottomCenter par `StartsWith("Top")`. `OverlappedPresenter` non resizable, `IsAlwaysOnTop=true`, `ExtendsContentIntoTitleBar=true`, `hasTitleBar: false`.

- **Show** : `MoveAndResize` (recalcule DPI a chaque show) puis `ShowWindow(SW_SHOWNOACTIVATE)` + `SetWindowPos(HWND_TOP, SWP_NOACTIVATE|SWP_NOMOVE|SWP_NOSIZE)`. **Jamais `SetForegroundWindow`**.
- **Hide** : `ShowWindow(SW_HIDE)`.
- Creee une fois dans `OnLaunched`, jamais detruite (Closing->Cancel).
- Handlers marshales via `DispatcherQueue.TryEnqueue` (events `WhispEngine` viennent de threads de fond).
- Overlay desactivable dans Settings (`Overlay.Enabled`), verifie au `ShowRecording`.

**Backdrop** : `DesktopAcrylicBackdrop` (materiau canonique des fenetres transient Win11). Signal DWM `DWMSBT_TRANSIENTWINDOW` pose explicitement (intention correcte cote doc, meme si l'ombre Shell riche des menus systeme n'est pas accessible aux WinUI 3 unpackaged — valide runtime 2026-04-09).

Layout Figma : chrono Bitcount Single Light + beacon/ProgressRing. Chrono cable `MM.SS.cc` via `CompositionTarget.Rendering` (cadence vsync, pas de `DispatcherTimer` -> pas de jitter), fige a la transition record->transcribe.

## Coloration progressive des chiffres

Chaque chiffre du chrono qui change au moins une fois passe en rouge `SystemFillColorCriticalBrush` (theme resource Windows, suit light/dark) et y reste jusqu'au prochain `ShowRecording`. Le `0` initial reste neutre tant qu'il n'a pas bouge.

Les Run idle ne portent pas de Foreground local — ils heritent de `ClockText.Foreground = {ThemeResource TextFillColorPrimaryBrush}`, auto-reactif au theme. Reset = `ClearValue(TextElement.ForegroundProperty)` sur chaque Run nomme.

**Theme switch runtime** : `RootGrid.ActualThemeChanged` re-resout le brush critical et le reapplique aux chiffres deja allumes (un Foreground assigne en code ne suit pas un `ThemeResource` binding, il faut le reassigner manuellement).

## Fade proximite souris — approche event-driven

Via Raw Input (`RegisterRawInputDevices` + `RIDEV_INPUTSINK`) interceptee par subclass HWND (`SubclassProc` en champ d'instance, ID `0x48554450`).

Alpha layered global (`WS_EX_LAYERED` + `SetLayeredWindowAttributes(LWA_ALPHA)`) qui couvre Acrylic + content, mappe a la distance curseur via smoothstep :
- `NEAR_RADIUS_DIP=10` -> alpha MIN (40, estompe)
- `FAR_RADIUS_DIP=128` -> alpha MAX (255, pleine)
- Entre les deux : `t²(3-2t)`, courbe douce sans cassure aux bords.

Pas de polling, pas d'animation timer — la fluidite vient de la frequence des `WM_INPUT` (~125 Hz). Activable/desactivable via `Settings.Overlay.FadeOnProximity`.

**Subclass WM_NCACTIVATE** : force `wParam=TRUE` en permanence pour que DWM peigne la HUD comme active (ombre portee "Active Window" riche au lieu de l'ombre inactive aplatie).

Styles etendus poses au constructeur : `WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT`.

## Ombre Shell — contrainte layered

`WS_EX_LAYERED` desactive par design l'ombre DWM Shell systeme riche. C'est une contrainte Win32 de base, aucune API DWM ne la contourne. Compromis actuel : ombre plate (inactive forcee a active via `WM_NCACTIVATE` ci-dessus ameliore un peu). Voir roadmap (`project_roadmap.md`, piste regressions) pour les voies de sortie exploratoires.
