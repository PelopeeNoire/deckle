# R3 — Interpolateurs HyperHDR `infinite-color-engine` (2026-05-15)

Recherche menée en sous-agent, archivée pour J5 (infra Playground
animations). HyperHDR upstream SHA `a6fa8a2` au 2026-05-15.

## Verdict synthétique

**Correction d'hypothèse importante.** Le `InfiniteStepperInterpolator`
**n'est pas** un modèle masse-ressort inertiel comme j'avais supposé.
C'est un **lerp à durée fixe** avec courbe gamma adaptative — un
ease-in-out déguisé, qui arrive à l'heure puis s'arrête net. Pas
d'inertie, pas d'accompagnement.

**Le vrai spring-damper Euler semi-implicite vit dans
`InfiniteHybridInterpolator` (YUV) et `InfiniteHybridRgbInterpolator`
(RGB).** Pour le feeling « atmosphère qui respire, suit avec un peu
de retard sans saccade », **c'est `InfiniteHybridRgbInterpolator`
qu'on porte en V1.** L'intégration de la vélocité est ce qui fait la
différence entre « LED qui rattrape l'image » et « LED qui
l'accompagne ».

## Six interpolateurs identifiés (pas quatre)

| # | Classe | Modèle math | Espace | Vrai ressort ? |
|---|---|---|---|---|
| 1 | `InfiniteRgbInterpolator` | Lerp fixe + courbe gamma adaptative | RGB direct | Non |
| 2 | `InfiniteStepperInterpolator` | Lerp fixe sans smoothing factor | RGB direct | Non |
| 3 | `InfiniteExponentialInterpolator` | Lerp asymptotique 1er ordre | RGB direct | Non |
| 4 | `InfiniteYuvInterpolator` | Lerp fixe + limit vitesse Y | BT.709 (YUV) | Non |
| 5 | `InfiniteHybridInterpolator` | Spring-damper Euler + clamp vitesse Y | BT.709 | **Oui** |
| 6 | `InfiniteHybridRgbInterpolator` | Spring-damper Euler pur | RGB direct | **Oui** |

## Modèle du spring-damper HyperHDR

Mise à jour par tick (Euler semi-implicite) :

```
acc  = stiffness * (target - cur) - damping * vel
vel += acc * dt
step = vel * dt
cur += step
```

Défauts upstream : `stiffness=150`, `damping=26` → ω₀ ≈ 12.2 rad/s,
ζ ≈ 1.06 (**légèrement suramorti**, pas d'overshoot visible, vraie
inertie). Clamp `vel=0` aux bornes physiques `[minBrightness, 1]`
pour empêcher le rebond aux limites RGB.

## Tuning pour Hue (différent du upstream)

Le `_initialDuration` de 150 ms upstream est calibré pour capture
60 Hz. Pour Hue (bridge cap ~10 Hz côté Zigbee, lampes encore moins)
le rapport recommande :

- τ → 300-600 ms (plutôt que 150 ms)
- `stiffness` → 30-60 (plutôt que 150)

Cible : rendu « atmosphère doux », pas « réactif gaming ».

## Note colorimétrique — opportunité d'amélioration

HyperHDR fait toute son interpolation **en gamma sRGB direct** sans
linéarisation. La matrice `matrix_srgb_to_bt709` (`ColorSpace.h:82-86`)
est appliquée brute sur les float3 non linéarisés — colorimétriquement
faux (Y BT.709 est défini sur RGB linéaire) mais c'est l'état du code
upstream.

**Pour Deckle**, deux choix :
- **(a)** Répliquer tel quel pour feeling identique au upstream.
- **(b)** Profiter de `Deckle.Composition.Primitives.ColorSpace` (déjà
  OKLCh ↔ linear sRGB) pour faire l'interp **en linear sRGB**
  (correct) ou **en OKLCh** (perceptuellement uniforme — l'idéal pour
  ambient lighting).

**Recommandation R3 :** porter d'abord la variante RGB pour valider
le feeling, puis bouger l'espace en V2 si pertinent. Le ressort en
OKLCh sur L et a,b indépendamment donnerait probablement le meilleur
rendu « respire » possible.

## Coût d'implémentation C#

Tight-loop friendly, `Span<Rgb>` ou `Vector3[]` réutilisé, zéro
allocation par tick. Struct interne par lampe : 3 `Vector3` (current,
target, velocity) + 4 floats. `System.Numerics.Vector3` couvre toutes
les opérations. **Estimation : 120-180 lignes C#** pour une classe
`SpringInterpolator` gérant N lampes.

## Sources (permalinks SHA `a6fa8a2`)

- [InfiniteInterpolator.h (base abstraite)](https://github.com/awawa-dev/HyperHDR/blob/a6fa8a2d6e51734785f4331a2d758c4a0247fea0/include/infinite-color-engine/InfiniteInterpolator.h)
- [InfiniteStepperInterpolator.cpp](https://github.com/awawa-dev/HyperHDR/blob/a6fa8a2d6e51734785f4331a2d758c4a0247fea0/sources/infinite-color-engine/InfiniteStepperInterpolator.cpp)
- [InfiniteExponentialInterpolator.cpp](https://github.com/awawa-dev/HyperHDR/blob/a6fa8a2d6e51734785f4331a2d758c4a0247fea0/sources/infinite-color-engine/InfiniteExponentialInterpolator.cpp)
- [InfiniteHybridInterpolator.cpp (spring-damper YUV)](https://github.com/awawa-dev/HyperHDR/blob/a6fa8a2d6e51734785f4331a2d758c4a0247fea0/sources/infinite-color-engine/InfiniteHybridInterpolator.cpp)
- [**InfiniteHybridRgbInterpolator.cpp (recommandé pour Deckle V1)**](https://github.com/awawa-dev/HyperHDR/blob/a6fa8a2d6e51734785f4331a2d758c4a0247fea0/sources/infinite-color-engine/InfiniteHybridRgbInterpolator.cpp)
- [ColorSpace.h (matrices BT.709, helpers OKLab)](https://github.com/awawa-dev/HyperHDR/blob/a6fa8a2d6e51734785f4331a2d758c4a0247fea0/include/infinite-color-engine/ColorSpace.h)
