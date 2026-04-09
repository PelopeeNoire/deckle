# HUD — spec, fade proximité souris, ombre layered

## Spec Window

Window WinUI 3, ~320×64, bas-centre via `DisplayArea.Primary.WorkArea`, `OverlappedPresenter`
non resizable, `ExtendsContentIntoTitleBar=true`.

- **Show :** `MoveAndResize` puis `ShowWindow(SW_SHOWNOACTIVATE)` + `SetWindowPos(HWND_TOP, SWP_NOACTIVATE|SWP_NOMOVE|SWP_NOSIZE)`. **Jamais `SetForegroundWindow`**.
- **Hide :** `ShowWindow(SW_HIDE)`.
- Créée une fois dans `OnLaunched`, jamais détruite (Closing→Cancel).
- Handlers marshalés via `DispatcherQueue.TryEnqueue` (events `WhispEngine` viennent de threads de fond).

Layout Figma : chrono Bitcount Single Light + beacon/ProgressRing dans Mica arrondi. Chrono câblé `MM.SS.cc` via `CompositionTarget.Rendering` (cadence vsync, pas de `DispatcherTimer` → pas de jitter), fige à la transition record→transcribe.

## Coloration progressive des chiffres

Chaque chiffre du chrono qui change au moins une fois passe en rouge `SystemFillColorCriticalBrush` (theme resource Windows, suit light/dark) et y reste jusqu'au prochain `ShowRecording`. Le `0` initial reste neutre tant qu'il n'a pas bougé.

Les Run idle ne portent pas de Foreground local — ils héritent de `ClockText.Foreground = {ThemeResource TextFillColorPrimaryBrush}`, donc auto-réactif au thème. Reset = `ClearValue(TextElement.ForegroundProperty)` sur chaque Run nommé.

**Theme switch runtime** : `RootGrid.ActualThemeChanged` re-résout le brush critical et le réapplique aux chiffres déjà allumés (un Foreground assigné en code ne suit pas un `ThemeResource` binding, il faut le réassigner manuellement).

## Fade proximité souris — approche event-driven

Via Raw Input (`RegisterRawInputDevices` + `RIDEV_INPUTSINK`) interceptée par subclass HWND (`SubclassProc` en champ d'instance, ID `0x48554450`).

Alpha layered global (`WS_EX_LAYERED` + `SetLayeredWindowAttributes(LWA_ALPHA)`) qui couvre Mica + content, mappé à la distance curseur via smoothstep (`NEAR_RADIUS_DIP=10`, `FAR_RADIUS_DIP=200`).

Pas de polling, pas d'animation timer — la fluidité vient de la fréquence des `WM_INPUT`. Approche inspirée de `D:\projects\environment\taskbar-overlay-cs`.

## Ombre Shell manquante — contrainte layered

HudWindow n'a qu'une ombre plate alors que LogWindow (même projet, même plateforme WinUI 3 unpackaged) a une ombre Shell riche.

**Cause confirmée** : `WS_EX_LAYERED` (requis pour le fade proximité via `SetLayeredWindowAttributes LWA_ALPHA`) désactive par design l'ombre DWM Shell système. C'est une contrainte Win32 de base, aucune API DWM ne la contourne (`DWMWA_SYSTEMBACKDROP_TYPE`, `DWMWA_BORDER_COLOR`, corner preference : aucun effet sur une fenêtre layered).

Travaux déjà tentés sans succès : passage à `DesktopAcrylicBackdrop` + signal DWM `DWMSBT_TRANSIENTWINDOW` + interception `WM_NCACTIVATE` forçant wParam=TRUE.

Compromis accepté pour cette itération : ombre plate conservée. Deux voies de sortie propres, mutuellement exclusives :

**Voie A — agrandir le HWND + DropShadow Composition interne.** Garder `WS_EX_LAYERED`, garder le fade, garder la propagation bureaux virtuels. Augmenter la taille physique (ex. 354×118 au lieu de 314×78), contenu visuel centré à 314×78, pourtour 20 px reçoit une `Microsoft.UI.Composition.DropShadow` paramétrable pour coller aux tokens Figma Shell Shadows. Doc clé : *Shadows are not clipped by the implicit clip set on the visual* — mais restent clippées au rect du HWND, d'où l'agrandissement. Custom assumé mais prévisible.

**Voie B — retirer `WS_EX_LAYERED` + fade via Composition.** Utiliser `ElementCompositionPreview.GetElementVisual(RootGrid).Opacity` pour animer l'opacité du contenu XAML sans passer par `SetLayeredWindowAttributes`. Récupère automatiquement l'ombre DWM Shell native. Plus « native Microsoft », mais incertitude à prototyper : est-ce que Composition opacity fade correctement un `DesktopAcrylicBackdrop` (backdrop rendu par DWM hors de l'arbre Composition XAML) ?

**Recommandation** : tester Voie B d'abord (prototype rapide opacity 0→1 sur `RootGrid`). Si le backdrop Acrylic ne suit pas, basculer Voie A.

## Régressions post-refacto — comportement notification

**Symptôme A** : la HudWindow apparaît au démarrage alors qu'aucun enregistrement n'est en cours. Ne doit jamais être visible tant que `WhispEngine` n'a pas émis `RecordingStarted`.

**Symptôme B** : reste sur le bureau virtuel où elle a été lancée au lieu de suivre l'utilisateur entre bureaux. Comportement d'une Window classique, pas d'une notification.

**Cible** : se comporter comme une notification système — visible sur **tous** les bureaux virtuels pendant l'enregistrement, invisible le reste du temps.

**À investiguer** : styles étendus Win32 (`WS_EX_TOOLWINDOW`, `WS_EX_TOPMOST`, `WS_EX_NOACTIVATE`), API `IVirtualDesktopManager` pour épingler la fenêtre à tous les bureaux, ou alternative plus propre (AppNotification du Windows App SDK ?). Regarder comment PowerToys gère ses HUD (Shortcut Guide etc.).

## Contour animé — exploratoire

Idée Louis : transformer la bordure du HUD en indicateur vivant.

- **Pendant transcription / réécriture LLM** : animation type "LED qui tourne autour de la fenêtre" — un point lumineux qui parcourt le périmètre. Pourrait rendre le `ProgressRing` inutile.
- **Pendant enregistrement** : luminosité / éclat de la bordure pilotée par le niveau de voix (RMS PCM16). Voix silencieuse → bordure grise/effacée. Voix qui parle → bordure qui s'éclaircit vers blanc, légère pulsation. Smoothing important.

But : feedback immédiat « le programme me capte / me capte pas », sans regarder les chiffres. Cohérent avec la philosophie du HUD discret.

Implémentation à creuser : Border XAML avec gradient/glow animé, ou couche Composition (Win2D / SpriteVisual) pour la LED qui tourne. À faire **après** que le RMS soit déjà exposé via l'event `AudioLevel` (voir `pipeline-transcription.md`).

**Dépend** de la décision ombre ci-dessus (voie B préférée — retirer `WS_EX_LAYERED`).

## Tâches cosmétiques ouvertes

- **HUD chrono coupé en haut** — malgré `TextLineBounds="Tight"` + `LineHeight="48"` + sous-container `Grid 214x30` (taille bbox Figma exacte), le haut des chiffres reste tronqué. Hypothèse : la cap-height réelle de Bitcount Single Light à 48px est plus grande que les ~34 DIPs estimés, ou l'ascent WinUI ne s'aligne pas comme prévu avec la baseline Figma. À investiguer : mesurer le glyphe natif WinUI (`TextBlock.ActualHeight` après render), ou augmenter `HUD_HEIGHT` de quelques DIPs en compensant côté padding.
- **HUD espacement chiffres** — visuellement très large (`00 . . 00 . . 00`). À comparer avec un screenshot réel Figma — si Figma rend pareil, c'est juste le tracking faible (-2.4 px = -5 % = `CharacterSpacing="-50"`) qui ne suffit pas à compenser les advances natifs Bitcount. Sinon, vérifier l'unité du `letterSpacing: -5` Figma (% vs px).
