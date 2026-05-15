# R1 — Statut HDR de `Windows.Graphics.Capture` (2026-05-15)

Recherche menée en sous-agent, archivée pour J7 (HDR).

## Verdict synthétique

**Attaque WinRT direct, pas de fallback DXGI prévu.**
`Direct3D11CaptureFramePool.Create` accepte explicitement
`DirectXPixelFormat.R16G16B16A16Float` depuis l'origine de l'API
(Windows 10 1803, contrat UAP 6.0). Les frames HDR arrivent en
**scRGB linéaire FP16** (canonical composition color space du DWM),
pas en PQ/BT.2100. L'API WinRT n'expose **aucune** métadonnée HDR sur
la frame elle-même — il faut récupérer transfer function, peak
luminance et white point via `IDXGIOutput6::GetDesc1` en parallèle,
au démarrage et sur `WM_DISPLAYCHANGE`. Suffisant pour de l'ambient
lighting.

## Cible OS

Windows 11 24H2 minimum pour fiabilité HDR (gate Chromium / Snipping
Tool, généralisation Advanced Color, ajout `DirtyRegionMode`).
Optimum sur 25H2 (HDR Screenshot Color Corrector basé WGC).

## Pipeline visé pour Deckle

1. `Direct3D11CaptureFramePool.Create(..., DirectXPixelFormat.R16G16B16A16Float, ...)`
2. Récupérer `IDXGISurface` depuis `frame.Surface`.
3. Downscale agressif (1920×1080 FP16 → grille zones Hue) — pas besoin
   de tone-mapping perceptuel sophistiqué, scRGB est linéaire.
4. `IDXGIOutput6::GetDesc1.MaxLuminance` une fois au démarrage et
   sur `WM_DISPLAYCHANGE` pour l'exposition.
5. Mapping FP16 scRGB → 8-bit sRGB pour Hue avec gestion explicite
   des valeurs > 1.0 (scRGB autorise hors gamut).

## Sources

- [Screen capture (Microsoft Learn)](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture)
- [HDR and Advanced Color (Microsoft Learn)](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/high-dynamic-range)
- [Direct3D11CaptureFramePool Class](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool?view=winrt-26100)
- [Direct3D11CaptureFrame Class](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframe?view=winrt-26100)
- [GraphicsCaptureSession Class](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession?view=winrt-26100)
- [IDXGIOutput6 / DXGI_OUTPUT_DESC1](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_6/nn-dxgi1_6-idxgioutput6)
- [DXGI 1.5 DuplicateOutput1 (fallback)](https://learn.microsoft.com/windows/win32/direct3ddxgi/dxgi-1-5-improvements)
- [DirectX-Graphics-Samples — D3D12HDR](https://github.com/microsoft/DirectX-Graphics-Samples/tree/master/Samples/Desktop/D3D12HDR)
