# HudWindow — spec et implementation

## Architecture — shell technique + 2 UserControls

`HudWindow` est devenu un shell technique. Il porte le HWND, les styles etendus, le subclass de proximite, le backdrop, et le dispatcher d'etat. Il n'a plus aucune logique visuelle propre — celle-ci est repartie dans deux UserControls :

- `Controls/HudChrono` (314x78) — chrono Bitcount + status dot + surface de stroke/ombre pour Charging / Recording / Transcribing / Rewriting.
- `Controls/HudMessage` (carte 272x78 dans une fenetre 400x160) — title + subtitle + badge sematique pour Pasted / Copied / Error / UserFeedback. Carte centree dans la fenetre pour que l'ombre composite puisse deborder dans la marge transparente.

`HudWindow.xaml` se reduit a :

```xml
<Grid x:Name="RootGrid">
    <controls:HudChrono  x:Name="Chrono"  Visibility="Visible" />
    <controls:HudMessage x:Name="Message" Visibility="Collapsed" />
</Grid>
```

Pas de `Background` sur le Grid : chaque UserControl porte son propre `CardBackgroundFillColorDefaultBrush`. La fenetre elle-meme reste transparente (acrylic + content alpha-blended).

## State machine

Enum unique `Controls/HudState.cs` :

```
Hidden | Charging | Recording | Transcribing | Rewriting | Message
```

Plus `MessageKind { Success, Critical, Warning, Informational }` et `MessagePayload(Kind, Title, Subtitle, Duration)`.

Toutes les transitions passent par `HudWindow.SetState(next, msg = null)`. Une seule source de verite — chaque appel public (`ShowPreparing`, `ShowRecording`, `SwitchToTranscribing`, `SwitchToRewriting`, `ShowError/Pasted/Copied/UserFeedback`, `Hide`) marshale un appel a `SetState`. Le dispatcher :

1. Coupe les timers (auto-hide message + retract, et fade-in en cours).
2. Toggle `Chrono.Visibility` / `Message.Visibility`.
3. Forward au control concerne (`Chrono.ApplyState(state)` ou `Message.Show(payload)`).
4. Recalcule la taille et la position via `ShowNoActivate` (DPI-aware).
5. Applique l'alpha via `ApplyShowAlpha` : warm pass force, sinon fade-in 150 ms si on passe de Hidden a visible et que `Overlay.Animations` est on, sinon alpha instant. Active la proximite a la fin de la transition.
6. Pour Message : arme `_messageHideTimer` (duration totale) et `_messageRetractTimer` (~800 ms si duration > 1 s).

`HideSync` : variante bloquante via `ManualResetEventSlim` rendezvous, appelee juste avant `PasteFromClipboard` pour garantir que `SW_HIDE` est effectif avant que `SendInput` envoie le Ctrl+V (sinon la redistribution d'activation peut detourner le paste).

## Palette semantique — 4 kinds, 2 phases

Centralisee dans `Composition/HudPalette.cs`. Chaque kind a une couleur Full (au pic), une couleur Attenuated (apres decay), et un glyph Segoe Fluent porte par le badge 16x16 :

| Kind          | Full       | Attenuated | Glyph    |
|---------------|------------|------------|----------|
| Success       | `#0F7B0F`  | `#345634`  | `\uE65F` |
| Critical      | `#C42B1C`  | `#8C5954`  | `\uEDAE` |
| Warning       | `#9D5D00`  | `#62523B`  | `\uEDB1` |
| Informational | `#005FB7`  | `#455C72`  | `\uEDAD` |

Le hex Critical Full correspond exactement a `SystemFillColorCriticalBrush` Win11. Les autres mappings ne collent pas a une theme resource unique car le decay Attenuated demande une couleur intermediaire specifique — d'ou les hex bruts.

## Anatomie des ombres — regle cardinale

**Toutes les ombres du HUD sont composites multi-couches**. Une seule `DropShadow` produit un rendu plat. Le rendu Win11 vient toujours de l'**empilement** d'au moins deux couches qui jouent des roles distincts :

- **Couche halo / fond** — petit offset (Y proche de 0), grand blur. Diffuse la couleur ambiante autour de la surface.
- **Couche drop / chute** — grand offset Y vers le bas, blur eleve. Cree la perception de hauteur, le "grand recul" par rapport au fond.

Les deux ensemble font la lecture. Si on n'en pose qu'une, la perception de profondeur s'effondre. La regle s'applique aux trois ombres composites du HUD :

- Transcribing : 2 couches (halo + drop, gris).
- Rewriting : 8 couches colorees (1 par arc, distribuees a 45°).
- Message : 2 couches (halo + drop, couleur sematique).

Aucune couche ne peut etre omise. Et la regle s'etend a toute future surface (preview window de transcription progressive a venir).

## Composition pipeline

`Composition/HudComposition.cs` — helper statique, Microsoft.UI.Composition pur, zero Win2D.

**Pattern silhouette** — `DropShadow` prend la silhouette du Visual qu'il decore. Pour obtenir une ombre conformee a un rounded rect sans Win2D : on heberge un `ShapeVisual` rempli avec un brush quasi-invisible (alpha `0x01`, soit 1/255) dans un `LayerVisual`. Le layer rasterise le fill, le shadow lit la silhouette. Le fill est invisible a l'oeil mais suffit a driver la forme.

### `CreateTranscribingStroke(compositor, size)`

Retourne un `ContainerVisual` avec :
- Drop layer : offset `(-12, 12)`, blur `64`, color `#66666647`.
- Halo layer : offset `(-2, 2)`, blur `21`, color `#66666638`.
- Stroke gradient diagonal (StartPoint `(0,0)` -> EndPoint `(1,1)`) de `#757575` a `#BFBFBF`, thickness 1, geometry `RoundedRectangleGeometry` corner radius 8.

### `CreateRewritingStroke(compositor, size)`

Retourne un `ContainerVisual` avec :
- 8 ombres colorees au radius 32, alpha `0x40`, distribuees aux 8 angles (0, 45°, 90°...). Couleurs dans l'ordre : red, amber, lime, green, cyan, blue, violet, magenta.
- 8 arcs trim sur le perimetre du rounded rect — chacun avec sa propre `RoundedRectangleGeometry` car `TrimStart`/`TrimEnd` est une propriete de la geometrie, pas de la shape (geometries non partageables). Epsilon `0.005` ajoute a chaque `TrimEnd` pour masquer la couture entre arcs adjacents.

### `CreateMessageShadow(compositor, size, full, attenuated)`

Retourne `(ContainerVisual host, CompositionPropertySet anim)`. Le PropertySet expose un scalar `Saturation` (1.0 = full, 0.0 = attenuated). Les deux couches partagent la meme couleur lerpee via `ExpressionAnimation` :

```
ColorLerp(props.AttenuatedColor, props.FullColor, props.Saturation)
```

L'animation tourne au niveau Composition, donc **off UI thread**. Couches :
- Drop : offset `(0, 32)`, blur `64`.
- Halo : offset `(0, 2)`, blur `21`.

### `AnimateShadowToAttenuated(compositor, props, duration)`

`ScalarKeyFrameAnimation` sur `props.Saturation`, 1.0 -> 0.0, easing CubicBezier `({0.05, 0.95}, {0.2, 1.0})` (pic puis plateau, mimique la log-decroissance demandee). Duree par defaut 650 ms appelee par `HudMessage.AnimateToAttenuated`.

## Hybrid bleed — 400x160 puis retract a 272x78

Le but du redimensionnement hybride : laisser l'ombre composite deborder loin autour de la carte au pic, puis retracter la fenetre a la taille de la carte une fois que l'ombre est attenuated.

Sequence pour un message :

1. `t = 0` — `SetState(Message)` : fenetre redimensionnee a `HUD_WIDTH_MESSAGE x HUD_HEIGHT_MESSAGE` (400x160). La carte 272x78 est centree dans le grid via `HorizontalAlignment="Center" VerticalAlignment="Center"`. Marge transparente : ~64 px horizontal, ~41 px vertical. L'ombre composite remplit cette marge.
2. `t = 0` — `Message.Show(payload)` cree la composite shadow (full) et lance la `Saturation` animation (650 ms vers attenuated).
3. `t = 650 ms` — animation terminee, ombre en couleur attenuated.
4. `t = 800 ms` — `_messageRetractTimer` tick. `_messageRetracted = true`, `ShowNoActivate()` rappele : la fenetre passe a `HUD_WIDTH_MESSAGE_RETRACTED x HUD_HEIGHT_MESSAGE_RETRACTED` (272x78). La fenetre snap a la taille exacte de la carte. L'ombre attenuated continue d'exister mais est clippee par le bord fenetre — visuellement le halo se "tasse" tandis que la carte reprend sa taille standard.
5. `t = duration` (e.g. 5 s pour Critical) — `_messageHideTimer` tick. `SetState(Hidden)`.

Pour les messages courts (Pasted a 500 ms), le retract est saute (`MessageRetractMinDuration = 1 s`) — la fenetre disparait avant qu'on aie le temps de retracter.

`HudMessage` re-centre son shadow visual a chaque `OuterRoot.SizeChanged` : l'offset baked au moment du `Show` (calcule pour 400x160) deviendrait incorrect apres le retract. La subscription decouple l'ombre des resize de fenetre.

`ShowNoActivate` choisit la taille selon `(_state, _messageRetracted)` :
- `Message && !retracted` -> 400x160
- `Message && retracted`  -> 272x78
- sinon                    -> 314x78 (chrono)

DPI-aware : la valeur DIP est multipliee par `GetDpiForWindow(_hwnd) / 96.0` a chaque show, donc un changement DPI runtime entre deux dictees est absorbe sans relancer l'app.

## Coloration progressive des chiffres (HudChrono)

Conserve verbatim depuis l'ancienne version, mais migre dans `HudChrono` :

Chaque chiffre du chrono qui change au moins une fois passe en rouge `SystemFillColorCriticalBrush` (theme resource Windows, suit light/dark) et y reste jusqu'au prochain `ApplyState(Recording)`. Le `0` initial reste neutre tant qu'il n'a pas bouge.

Les Run idle ne portent pas de Foreground local — ils heritent de `ClockText.Foreground = {ThemeResource TextFillColorPrimaryBrush}`, auto-reactif au theme. Reset = `ClearValue(TextElement.ForegroundProperty)` sur chaque Run nomme.

`ChronoRoot.ActualThemeChanged` re-resout le brush critical et le reapplique aux chiffres deja allumes (un Foreground assigne en code ne suit pas un `ThemeResource` binding, il faut le reassigner manuellement).

Cadence : `CompositionTarget.Rendering` (vsync, pas de `DispatcherTimer` -> pas de jitter). Hookee uniquement en Recording, debranchee en Transcribing/Rewriting (le chrono fige sur la derniere valeur).

## Surface de stroke (HudChrono)

`ProcessingSurfaceHost` est un `Border` transparent IsHitTestVisible=False qui couvre les deux colonnes du chrono. C'est l'attach point de `ElementCompositionPreview.SetElementChildVisual` pour les visuels Composition retournes par `HudComposition.CreateTranscribingStroke` / `CreateRewritingStroke`.

`AttachProcessingVisual` fallback aux dims (314, 78) si `ActualWidth/Height = 0` (premier attach avant layout pass). Le visuel n'est pas auto-resize sur layout suivant — mais Charging/Recording resettent toujours la surface, et l'utilisateur n'atteint Transcribing qu'apres au moins une mesure du chrono.

## Fade proximite souris — approche event-driven

Conserve verbatim. Via Raw Input (`RegisterRawInputDevices` + `RIDEV_INPUTSINK`) interceptee par subclass HWND (`SubclassProc` en champ d'instance, ID `0x48554450`).

Alpha layered global (`WS_EX_LAYERED` + `SetLayeredWindowAttributes(LWA_ALPHA)`) qui couvre Acrylic + content, mappe a la distance curseur via smoothstep :
- `NEAR_RADIUS_DIP=10` -> alpha MIN (40, estompe)
- `FAR_RADIUS_DIP=128` -> alpha MAX (255, pleine)
- Entre les deux : `t²(3-2t)`, courbe douce sans cassure aux bords.

Pas de polling, pas d'animation timer — la fluidite vient de la frequence des `WM_INPUT` (~125 Hz). Activable/desactivable via `Settings.Overlay.FadeOnProximity`.

**Subclass WM_NCACTIVATE** : force `wParam=TRUE` en permanence pour que DWM peigne la HUD comme active (ombre portee "Active Window" riche au lieu de l'ombre inactive aplatie).

Styles etendus poses au constructeur : `WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT`.

Note Message state : `_proximityActive = false` pendant un message, le fade est suspendu (le message doit rester pleinement lisible meme si le curseur est dessus).

## Spec Window (preserve)

`OverlappedPresenter` non resizable, `IsAlwaysOnTop=true`, `ExtendsContentIntoTitleBar=true`, `hasTitleBar: false`. Position centree horizontalement, ancrage vertical configurable via `Settings.Overlay.Position` (TopCenter ou BottomCenter, default BottomCenter). Les anciennes valeurs de coin (TopLeft / BottomRight / ...) eventuellement presentes dans un `settings.json` legacy sont normalisees vers TopCenter/BottomCenter par `StartsWith("Top")`.

- **Show** : `MoveAndResize` (recalcule DPI a chaque show) puis `ShowWindow(SW_SHOWNOACTIVATE)` + `SetWindowPos(HWND_TOP, SWP_NOACTIVATE|SWP_NOMOVE|SWP_NOSIZE)`. **Jamais `SetForegroundWindow`**.
- **Hide** : `ShowWindow(SW_HIDE)`.
- Creee une fois dans `OnLaunched`, jamais detruite (Closing->Cancel).
- Handlers marshales via `DispatcherQueue.TryEnqueue` (events `WhispEngine` viennent de threads de fond).
- Overlay desactivable dans Settings (`Overlay.Enabled`), verifie en tete de `SetState`.

## Boot warm — invisible via layered alpha

`PrimeAndHide()` est appelee une fois dans `App.OnLaunched` apres la creation de la `HudWindow`. Le but : payer le cout de la premiere composition (DComp swap chain, font shaping Bitcount, visual tree DWM) au boot plutot qu'au premier hotkey, pour que le first show reel soit cold-path-free.

Forme actuelle (suivant le pattern propre alpha=0) :

```csharp
SetState(HudState.Charging, bypassGate: true, alphaOverride: 0);
DispatcherQueue.TryEnqueue(Low, () => SetState(HudState.Hidden));
```

Mecanique :
1. `SetState(Charging, alphaOverride: 0)` -> `ShowNoActivate()` (la fenetre est presentee, le compositor tourne) puis `SetAlphaImmediate(0)` -> couche layered transparente, rien n'arrive a l'ecran.
2. Low priority dispatch -> `SetState(Hidden)` -> `SW_HIDE` + `SetAlphaImmediate(MAX_ALPHA)` -> alpha resette pour le prochain show reel.

Effet : aucun flash visible au boot (cf. ancienne note "le flash boot est accepte" — caduque), aucun risque de premiere frame partielle (contour DWM sans contenu) qui aurait pu signaler un demarrage casse. Le warm fait son travail en silence.

Le warm pass active `bypassGate: true` (le Settings `Overlay.Enabled` desactive ne doit pas court-circuiter le warm) et `alphaOverride.HasValue` empeche `ApplyShowAlpha` d'activer la proximite (un curseur present dans la zone HUD au boot pourrait sinon ecraser alpha=0 par smoothstep).

Pas de relocation off-screen — le warm paye le cout de la composition exactement a la position reelle (DPI, work area, ancrage Settings).

## Show real — fade-in 150ms

Tout `Hidden -> visible` (Charging, Recording, Transcribing, Rewriting, Message) declenche un fade-in 150ms cubic ease-out (`1 - (1-t)^3`), aligne avec `WindowSlideAnimator` / `LayeredAlphaAnimator` du sous-systeme overlay. La transition centralisee dans `ApplyShowAlpha` :

- `alphaOverride.HasValue` -> warm pass, alpha force, proximite skippee.
- `!wasShown && AnimationSystemSetting.AreClientAreaAnimationsEnabled()` -> `StartFadeIn(MAX_ALPHA)`. Pendant le fade : `_proximityActive = false`. A la fin : reactivation proximite + appel immediat de `UpdateProximity`.
- Sinon (state switch en deja-visible, ou animations off) -> `SetAlphaImmediate(MAX_ALPHA)` instant + proximite immediate.

Implementation inline (timer dedie `_fadeInTimer`, ~60 fps) plutot que via `LayeredAlphaAnimator` pour conserver `_currentAlpha` comme source de verite unique cote `HudWindow` — l'instance partagee evite les desyncs avec le proximity fade.

Gating via `Settings.Overlay.Animations` (et non `SPI_GETCLIENTAREAANIMATION`) pour la meme raison que le reste du sous-systeme HUD : les transitions HUD sont load-bearing pour suivre quel etat vient de remplacer quel autre, on ne se cale pas automatiquement sur la pref reduce-motion globale Windows.

**Backdrop** : `DesktopAcrylicBackdrop` (materiau canonique des fenetres transient Win11). Signal DWM `DWMSBT_TRANSIENTWINDOW` pose explicitement (intention correcte cote doc, meme si l'ombre Shell riche des menus systeme n'est pas accessible aux WinUI 3 unpackaged — valide runtime 2026-04-09).

## Ombre Shell — contrainte layered

`WS_EX_LAYERED` desactive par design l'ombre DWM Shell systeme riche. C'est une contrainte Win32 de base, aucune API DWM ne la contourne pour une WinUI 3 unpackaged. Les ombres "shell shadows" des menus contextuels Explorer passent par un chemin DWM prive inaccessible.

C'est precisement ce constat qui valide l'approche Composition : on ne peut pas avoir l'ombre Shell, donc on dessine la notre — et au passage on gagne la possibilite de la **colorer** (que le shell system ne ferait pas). Pour les states Transcribing / Rewriting / Message, l'ombre composite est le bon outil.

Pour les states Charging / Recording (pas d'overlay Composition), on garde `WM_NCACTIVATE` -> TRUE force pour ameliorer un peu le rendu DWM par defaut.

## Engine wiring

`App.xaml.cs` dispatch `WhispEngine.StatusChanged` :

```csharp
if (status == "Recording")               _hudWindow.ShowRecording();
else if (status == "Transcribing")       _hudWindow.SwitchToTranscribing();
else if (status.StartsWith("Réécriture") || status.StartsWith("Rewriting"))
    _hudWindow.SwitchToRewriting();
```

Le double prefix `Réécriture` / `Rewriting` est defensif : le sweep FR->EN sur `WhispEngine.cs:858` est differe jusqu'au merge de la branche logs en cours, le dispatcher reste robuste sur les deux chaines en attendant.
