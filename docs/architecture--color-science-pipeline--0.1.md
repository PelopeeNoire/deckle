# Architecture — Pipeline color science (ambient lighting)

Doc canonique de la passe color science menée sur la pipeline ambient lighting livrée par les commits `77c6d75`, `a6b905b`, `f4f77c0` sur main. Couvre la cause du bug Night Owl `#011627 → turquoise`, les décisions math motivées, les fichiers touchés, les anti-patterns écartés, et le report explicite de l'extension HDR Composition à V2.

Lié à [reference--hud--1.0.md](reference--hud--1.0.md) pour le contexte HUD (axe 3 reporté y est cross-référencé), à [research--hue-entertainment-v2--2026-05-15.md](research--hue-entertainment-v2--2026-05-15.md) et [research--hdr-graphics-capture--2026-05-15.md](research--hdr-graphics-capture--2026-05-15.md) qui ont préparé le chantier ambient, et à la nomenclature documentaire `D:/references/nomenclature--documentation--0.3.md`.

---

## Context

Le pipeline ambient produit un rendu chromatique incorrect sur les bleus profonds. VS Code Night Owl `#011627` rend turquoise sur lampe Hue Play / Iris / E14 (Gamut C) au lieu de bleu. Lecture du code en read-only confirme la cause directe sans ambigüité.

`HueColorMath.RgbToHueXyBri` ([HueColorMath.cs:36-69](../src/Deckle.Lighting/Hue/HueColorMath.cs)) convertit le RGB sRGB en xy CIE 1931 correctement (gamma decode, matrice Philips Wide Gamut D65, projection X/(X+Y+Z)) puis envoie le résultat au pont. Le pont reçoit une chromaticité brute, non clippée vers son triangle Gamut C, et applique son propre gamut mapping propriétaire qui projette les points hors triangle sur l'arête la plus proche. Pour `#011627` la math donne : linear `(0.0003, 0.0071, 0.0179)` → `X=0.00420, Y=0.00569, Z=0.01816` → xy `(0.150, 0.203)`. Ce point est juste à gauche du blue corner Gamut C `(0.1532, 0.0475)` et le pont le projette sur l'arête B-G, où `x≈0.15` mappe sur un mix high-G low-B → rendu turquoise.

Deux biais latents secondaires apparaissent à la lecture, sans être responsables du bug Night Owl statique (un fond uni n'est pas affecté par les biais d'averaging) mais affectant les scènes complexes. Premièrement, l'averaging arithmétique en sRGB gamma-encoded dans `FrameSampler.ReadGridBGRA8` ([FrameSampler.cs:275-302](../src/Deckle.Vision/FrameSampler.cs)) et `AmbientEngine.SampleZone` ([AmbientEngine.cs:1011-1029](../src/Deckle.Lighting.Ambient/AmbientEngine.cs)) — deux étages cascadés qui amplifient les mid-tones. Le commentaire de tête de `SampleZone` (ligne 968) acknowledge déjà cette dette en la qualifiant de « J5 turf, behind a Playground toggle ». Deuxièmement, `ApplySaturationBoost` ([AmbientEngine.cs:862-897](../src/Deckle.Lighting.Ambient/AmbientEngine.cs)) opère en HSV, ce qui souffre exactement de l'asymétrie luminance yellow/blue déjà documentée comme raison d'avoir migré la stroke conique HUD en OKLCh ([ColorSpace.cs:8-18](../src/Deckle.Composition/Primitives/ColorSpace.cs)).

La passe groupe ces corrections en une PR cohérente, traite un overlay debug `#bfa600` visible runtime mais introuvable en capture, et documente la décision de reporter l'extension HDR Composition à V2.

---

## Décisions tranchées

### Décision A — Gamut mapping client-side, nearest-edge projection

Nouvelle méthode `HueColorMath.ClipToGamutC(HueXy) → HueXy`. Si le point xy est in-triangle Gamut C, identité. Sinon, projeter sur le point le plus proche du triangle via clamp paramétrique `t ∈ [0, 1]` sur chacune des trois arêtes (Red↔Green, Green↔Blue, Blue↔Red), retenir la projection à plus petite distance euclidienne 2D dans le plan xy. Les corners Gamut C sont `R=(0.6915, 0.3083)`, `G=(0.17, 0.7)`, `B=(0.1532, 0.0475)` (référence Philips Hue developer docs). Appelée à la sortie de `RgbToHueXyBri`, avant retour au caller. `HueBridgeClient` continue d'envoyer xy brut au pont, qui continue son clip propriétaire mais maintenant sur un point déjà in-gamut, donc identité côté pont.

**Rejet des alternatives.** Projection vers white-point D65 `(0.3127, 0.3290)` déplace les points hors-gamut vers cyan ou violet selon l'arête traversée — pour Night Owl `#011627`, traverse l'arête B-G donc même rendu turquoise, ne résout pas le bug. Gamut hull compression sigmoïde (façon SGCK Spatial Gamut Compression) impose une déformation globale sur toute la scène, sous-justifiée pour une lampe d'ambiance et coûteuse à dériver les paramètres. Nearest-edge minimise ΔE chromaticity par construction et laisse les blues profonds saturer sur le corner blue Hue au lieu de fuir le long de l'arête B-G.

**Trade-off.** Léger hue-shift sur les points significativement hors-gamut. Night Owl `#011627` sera rendu comme « blue Hue corner » — un peu plus violacé qu'un cobalt parfait, mais lisiblement bleu, pas turquoise. Coût CPU : trois produits scalaires et trois clamps par push, négligeable au regard du HTTP round-trip vers le pont qui domine la latence.

**Vérification doctrine.** Aucune primitive Windows native ne couvre xy → Hue Gamut C. WCS (Windows Color System) et Direct2D Color Management sont ICC-profile based, orientés display calibration — pas le clip vers un triangle propriétaire Philips. Code maison justifié.

### Décision B — Linear-light averaging via LUT 256-entry

Le sRGB encode la luminance via une courbe gamma ≈ 2.4. Sommer arithmétiquement des bytes sRGB amplifie les mid-tones par rapport à un averaging en linear-light qui respecte la photométrie. Le bug Night Owl statique n'est pas affecté (fond uni), mais toute scène avec des transitions claire/sombre dérive.

Ajouter `ColorSpace.SrgbToLinear8Lut` (`float[256]` init statique au démarrage via `SrgbToLinear(i / 255f)`, ~1 KB mémoire). Migrer les trois sites d'averaging pour sommer en `float`/`double`, diviser par count, ré-encoder via `LinearToSrgb` (déjà disponible [ColorSpace.cs:70-76](../src/Deckle.Composition/Primitives/ColorSpace.cs)).

**Trois sites concernés.** (1) `FrameSampler.ReadGridBGRA8` — somme en linear-float lue depuis la LUT, division par count, `LinearToSrgb` byte pour les cellules de la grille `Color[]`. (2) `FrameSampler.ReadGridFP16` — option (ii) retenue : LUT sur les bytes sortants du tone-map Hable, symétrie SDR/HDR, biais résiduel sub-perceptual après que le Hable a déjà dominé. (3) `AmbientEngine.SampleZone` — somme via LUT sur la grille `Color[]` déjà en sRGB byte, division, ré-encodage.

**Rejet des alternatives.** `MathF.Pow` per-pixel : 30×17 grille × 4 zones × 15 Hz = ~30 k pow/s, mesurable mais pas catastrophique. LUT économise totalement le math, exactitude bit-perfect pour 8-bit. Approximation `x²` (gamma 2.0) : biais visible sur les mid-tones puisque la vraie gamma sRGB est 2.4 piecewise, ne résout que partiellement le problème. LUT gagne sur tous les axes.

### Décision C — `ApplyMinBrightness` reste en sRGB

`ApplyMinBrightness` ([AmbientEngine.cs:906-918](../src/Deckle.Lighting.Ambient/AmbientEngine.cs)) applique un scale multiplicatif `scale = minBri / max` sur les bytes sRGB pour relever la max-channel à `minBri` tout en préservant la chromaticité. La chromaticité xy après round-trip via `RgbToHueXyBri` (qui re-linearise puis projette) est préservée par construction puisque les ratios R:G:B en sRGB-space sont préservés et que la matrice Philips est linéaire. Le seul biais théorique est sur la perception de luminance, déjà géré par le fait que `bri` Hue est dérivé de `max(R,G,B)` et non de `Y` (decoupling chromaticité/brightness intentional, documenté en commentaire de tête `HueColorMath.cs`). Refonte non justifiée par bénéfice perceptuel ni par cohérence math.

### Décision D — `ApplySaturationBoost` migré en OKLCh

`ApplySaturationBoost` convertit RGB → HSV → boost S → RGB. HSV n'est pas perceptuellement uniforme : à `V=0.5`, un yellow `H=60°` a une luminance perçue ≈ 0.93, un blue `H=240°` ≈ 0.07. Un boost de saturation modifie la perception de luminance différemment selon le hue — un boost ×1.5 sur un yellow le rend plus brillant, sur un blue le rend plus sombre. Sur la lampe ambient, ce biais se traduit par des bleus qui paraissent affadis quand on monte le boost pour saisir les rouges.

OKLCh est perceptuellement uniforme par design (Björn Ottosson 2020). À `L` constant, modifier `C` (chroma, équivalent saturation) préserve la luminance perçue sur tout le wheel. Ajouter `ColorSpace.RgbToOklch(byte, byte, byte) → (float L, float C, float h)`, symétrique de `OklchToRgb` ([ColorSpace.cs:38-64](../src/Deckle.Composition/Primitives/ColorSpace.cs)) : pipeline sRGB byte → linear via LUT → cone responses cube root → OKLab → OKLCh. Réécrire `ApplySaturationBoost` comme `RgbToOklch → C *= boost → OklchToRgb`. L'early-out `boost == 1.0` reste en place.

**Alignement doctrinaire.** Le projet a déjà fait le choix OKLCh pour la stroke conique HUD pour exactement cette raison (commentaire `ColorSpace.cs:8-18` cite l'asymétrie yellow ≈ 0.93 luma / blue ≈ 0.07 luma). Cohérence cross-modules.

**Coût.** ~6 trig/cube-root par pixel après averaging zone (≈ 4 zones × 15 Hz = 60 calls/s). Quelques µs/tick, négligeable.

---

## Implémentation

L'implémentation suit la séquence du plan validé. Étape 2 (primitives ColorSpace) précède toutes les autres puisque LUT et `RgbToOklch` y vivent. Étapes 3 à 7 sont indépendantes entre elles et peuvent être commitées séparément.

| Étape | Fichier | Changement |
|---|---|---|
| 2 | `src/Deckle.Composition/Primitives/ColorSpace.cs` | Ajout `SrgbToLinear8Lut` (float[256] static readonly) + `RgbToOklch(byte, byte, byte) → (float, float, float)` |
| 3 | `src/Deckle.Lighting/Hue/HueColorMath.cs` | Ajout constantes Gamut C corners + `ClipToGamutC(HueXy) → HueXy` + appel sortie `RgbToHueXyBri` |
| 4 | `src/Deckle.Vision/FrameSampler.cs:275-302` | `ReadGridBGRA8` somme en linear via LUT, division, `LinearToSrgb` |
| 5 | `src/Deckle.Vision/FrameSampler.cs:304-364` | `ReadGridFP16` option (ii) : LUT sur bytes post-tone-map |
| 6 | `src/Deckle.Lighting.Ambient/AmbientEngine.cs:1011-1029` | `SampleZone` somme en linear via LUT |
| 7 | `src/Deckle.Lighting.Ambient/AmbientEngine.cs:862-897` | `ApplySaturationBoost` en OKLCh via `RgbToOklch` / `OklchToRgb` |

---

## Axe 2 — Contour jaune `#bfa600`

Source identifiée : c'est l'indicateur système Windows dessiné par DWM autour de l'écran capturé par `GraphicsCaptureSession`, documenté explicitement par Microsoft ([Screen capture — Microsoft Learn](https://learn.microsoft.com/windows/uwp/audio-video-camera/screen-capture)) : « a yellow notification border is drawn by the system around the actively captured item ». Couleur fixe « Yellow Gold » (`#bfa600`), apparition à `StartCapture()`, disparition à fermeture de Deckle ou arrêt de la session — comportement observé confirme l'identification. Invisibilité aux screenshots natifs : DWM exclut l'indicateur de `BitBlt` / `PrintWindow` (privacy by design), seuls Snipping Tool et les pickers DWM-aware (PowerToys) le voient.

Suppression possible via `GraphicsCaptureSession.IsBorderRequired = false`, mais cette propriété est gated par la capability `graphicsCaptureWithoutBorder` ([uap11:Capability — Microsoft Learn](https://learn.microsoft.com/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired?view=winrt-28000)), qui ne peut être déclarée que dans un manifest **MSIX**. Deckle reste unpackaged par décision explicite (MEMORY `project_msix_deferred`, 2026-04-30 — pas de cert, pas de StartupTask, distribution source-only). Pas de chemin alternatif documenté pour les apps desktop unpackaged.

**Patch posé.** `ScreenCaptureService.Start` appelle `_session.IsBorderRequired = false` en best-effort, juste après `CreateCaptureSession`. Sur unpackaged, le setter écrit la propriété mais l'OS l'ignore (no-op gracieux, pas d'exception) ; si Deckle est packagé un jour, le même code path commence à cacher le contour automatiquement. La try/catch couvre les builds Windows pré-21H1 où la propriété n'existe pas.

**Limite actuelle assumée.** Le contour reste visible tant que la session ambient est active sur unpackaged Deckle. Comportement attendu et documenté, pas un bug. Re-éval triggers : (i) Microsoft assouplit la capability pour les apps desktop unpackaged via un nouveau mechanism (Settings consent, etc.), (ii) Deckle bascule en MSIX (décision macro hors scope cette passe).

**Vrai fix — chantier dédié.** La voie technique propre pour supprimer le contour sans MSIX est de remplacer `Windows.Graphics.Capture.GraphicsCaptureSession` par `IDXGIOutputDuplication` + `IDXGIOutput5::DuplicateOutput1` (DXGI 1.5). DXGI Duplication n'est pas concernée par l'indicateur système, supporte le HDR FP16 nativement via la format list de `DuplicateOutput1` (incluant `R16G16B16A16_FLOAT`), et c'est ce qu'utilisent HyperHDR, OBS, NVIDIA ShadowPlay et consorts. Coût estimé : 200-400 lignes (refonte `ScreenCaptureService`, adaptation signature `FrameSampler.Process`, gestion `DXGI_ERROR_ACCESS_LOST`, loop thread pull-based en remplacement du `FrameArrived` event). Scope distinct du chantier color science, ouvert en chantier follow-up `feat/capture-backend-dxgi-duplication`.

Le code de cette passe (gamut mapping, linear-light averaging, OKLCh saturation) est portable au futur backend sans changement — toutes les transformations opèrent en aval, sur `ID3D11Texture2D` ou bytes sRGB déjà extraits.

**Out of scope ici.** Le switch DXGI Duplication lui-même. Une fois ce backend livré, le best-effort `IsBorderRequired = false` posé dans cette passe devient redondant et sera retiré au passage.

---

## Axe 3 — HDR highlights HUD (reporté à V2)

L'idée explorée — utiliser le headroom HDR pour scintiller des highlights `> 1.0` scRGB sur les transitions de la stroke conique chrono et les notifications HUD — n'est pas réalisable nativement sur la pile actuelle. `Microsoft.UI.Composition` Windows App SDK `1.8.260317003` ne supporte pas la création de swap chains HDR / scRGB FP16 ; ses brushes (`CompositionSolidColorBrush`, `CompositionLinearGradientBrush`, `CompositionSurfaceBrush`) clippent implicitement à `[0, 1]` sRGB. Issues GitHub [microsoft-ui-xaml#777](https://github.com/microsoft/microsoft-ui-xaml/issues/777) (Allow Composition to scRGB/FP16, ouvert mai 2019) et [microsoft-ui-xaml#67](https://github.com/microsoft/microsoft-ui-xaml/issues/67) (HDR/WCG support proposal, ouvert 2020) confirment l'absence de roadmap.

Seul workaround documenté : `D3D11 SwapChainPanel` interopéré, swap chain HDR10 ou scRGB FP16 alloué manuellement, rendering D3D direct sur cette surface. Coût : abandon des animations déclaratives Composition (`Expression`, `ImplicitAnimations`, `Effects`) sur la surface concernée — la stroke conique chrono et les notifications HUD perdent leur pipeline existant. Disproportionné pour un bénéfice perceptuel sur un overlay 320×64 secondaire.

**Re-éval triggers.** (i) Windows App SDK ajoute un support natif de backdrop HDR sur `Window` ou un brush Composition extended-range. (ii) Un autre composant Deckle nécessite déjà un swap chain custom (ex. visualisation full-screen capture pour debug Ambient, viewport HDR de calibration) — mutualisation devient rentable. Aucun des deux triggers n'est en vue actuellement.

**Documentation cross-référence.** Une note est ajoutée à [reference--hud--1.0.md](reference--hud--1.0.md) à l'étape 10 du plan, pointant vers cette section pour ancrer la contrainte technique avec ses sources.

---

## Vérification empirique

L'évaluation perceptuelle se fait par photo iPhone fixe (manuel ISO/expo, distance et cadrage reproductibles) cadrée lampe + écran dans le même frame, sur trois scènes calibrées avant patch et après chaque étape mesurable (idéalement après étape 3 isolément pour gamut, étape 6 pour averaging, étape 7 pour OKLCh).

**Scène 1 — Night Owl `#011627` plein écran statique.** Critère succès : bleu profond reste bleu sur lampe, pas turquoise. Légère dérive vers blue Hue corner acceptée (un peu plus violacé qu'un cobalt parfait, mais lisiblement bleu).

**Scène 2 — Ciel HDR jour.** Capture Forza Horizon menu plage ou équivalent sur display HDR1000. Critère succès : teinte chaude préservée, pas de dérive cyan, exposure adaptative `_contentPeak` continue de mordre sans crush.

**Scène 3 — Scène jeu HDR sombre.** Cyberpunk 2077 night drive ou équivalent. Critère succès : reste dark avec teinte fidèle, pas d'amplification noise, lampe n'allume pas sur des highlights spéculaires isolés.

**Validation math `ClipToGamutC`.** Avant câblage runtime, écrire 3-4 cas dans une méthode test inline (non commitée) : point in-gamut central D65 (identité), point juste hors blue corner (projection sur arête B-G), point hors red corner (projection sur arête R-G), point central white (identité). Validation visuelle math avant câblage.

---

## Anti-patterns écartés

**Projection vers white-point D65.** Désature le hors-gamut au lieu de le clipper sur le corner le plus proche. Ne résout pas le bug Night Owl puisque la traversée passe par l'arête B-G, même rendu turquoise.

**Gamma 2.0 approximation (`x²`).** Tentation classique pour économiser le `Pow`. Biais visible sur les mid-tones puisque la vraie gamma sRGB est piecewise avec exposant 2.4 hors du toe linéaire. LUT est plus simple et exact.

**Boost saturation en HSV avec correction luminance ad-hoc.** Tentation de compenser l'asymétrie HSV par un facteur correctif sur V. Réinvente OKLCh en pire, perd la symétrie wheel. OKLCh est le bon outil.

**HDR Composition via interop D3D dans la V0.** Disproportionné. Voir axe 3.

**Refonte de la matrice Philips Wide Gamut → sRGB.** Tentation de réinitialiser tout le pipeline sRGB ↔ XYZ. La matrice actuelle est correcte (référencée developer.meethue.com), pas la cause du bug. Toucher uniquement le gamut mapping.

---

## Références liées

- [reference--hud--1.0.md](reference--hud--1.0.md) — note rétro axe 3 reporté
- [research--hue-entertainment-v2--2026-05-15.md](research--hue-entertainment-v2--2026-05-15.md) — protocole Hue Entertainment v2 (V2 pipeline 50 Hz éventuelle)
- [research--hdr-graphics-capture--2026-05-15.md](research--hdr-graphics-capture--2026-05-15.md) — capture FP16 scRGB
- [research--hyperhdr-interpolators--2026-05-15.md](research--hyperhdr-interpolators--2026-05-15.md) — référence HyperHDR pour interpolateurs
- [GitHub microsoft-ui-xaml#777](https://github.com/microsoft/microsoft-ui-xaml/issues/777) — Allow Composition to scRGB/FP16 (axe 3 blocker)
- [GitHub microsoft-ui-xaml#67](https://github.com/microsoft/microsoft-ui-xaml/issues/67) — HDR/WCG support proposal (axe 3 blocker)
- [Philips Hue Developer — Color Conversion Formulas](https://developers.meethue.com/develop/application-design-guidance/color-conversion-formulas-rgb-to-xy-and-back/) — matrice Wide Gamut + Gamut C corners
- [Björn Ottosson — A Perceptual Color Space for Image Processing](https://bottosson.github.io/posts/oklab/) — OKLab / OKLCh
- Nomenclature documentaire `D:/references/nomenclature--documentation--0.3.md`

---

## Points ouverts

Résultats empiriques étape 9 à reporter dans cette section après mesure. Trois lignes attendues, une par scène, format : `Scène N — verdict (lampe/écran cohérents / dérive résiduelle / régression)`.

Identification finale de la source du contour jaune `#bfa600` (étape 8) à documenter une ligne ici une fois la piste résolue.

Si un cas client se présente avec des bleus profonds différents de Night Owl `#011627` qui révéleraient une limite de la nearest-edge projection (par exemple, un client de l'arête B-G très loin du corner blue), reconsidérer une stratégie hybride (nearest-edge avec fallback projection-vers-D65 sous seuil de distance).
