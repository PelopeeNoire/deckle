namespace Deckle.Lighting.Ambient;

// Container POCO grouping every Ambient-Light-scoped section under a
// single node persisted at <UserDataRoot>/modules/ambient/settings.json.
// Each module owns its own settings POCO; the consumer code reads from
// AmbientSettingsService.Instance.Current.
//
// J0c shipped only the master Enabled toggle. J3 step 2 adds the
// minimal Hue bridge state we need to skip the link-button dance on
// every app start : the bridge IP / id, the application username, and
// the last selected light group. The Entertainment v2 DTLS pre-shared
// key (clientkey) is NOT persisted — the REST path doesn't need it,
// and storing it would land a PSK in a JSON file we'd rather keep
// unsensitive. Subsequent milestones add the active mode (Realistic /
// Game), the game-specific profiles, and the monitor source override.
public sealed class AmbientSettings
{
    // Master toggle for the Ambient Light module. When false, the
    // AmbientEngine never starts — no screen capture, no Hue traffic,
    // zero CPU cost. Wiring of the runtime toggle happens in J3
    // (minimal end-to-end pipeline) ; until then the field is persisted
    // but has no consumer.
    public bool Enabled { get; set; } = false;

    // ── Hue bridge persistence (J3 step 2) ─────────────────────────
    //
    // Populated by the Playground after a successful Pair + group
    // selection. On subsequent app starts the Playground (and later
    // the AmbientPage) restores the HueBridgeClient from these values
    // without prompting the user to press the bridge's link button
    // again — the bridge keeps the username valid until manually
    // revoked from the Hue mobile app.
    //
    // All four properties default to null ; null means "not paired".
    // Cleared together when the user explicitly re-pairs (the new pair
    // overwrites them).

    /// <summary>LAN address of the paired bridge (e.g. 192.168.1.11).</summary>
    public string? HueBridgeIp { get; set; }

    /// <summary>Bridge serial / identifier (e.g. 001788FFFE3A2C18).</summary>
    public string? HueBridgeId { get; set; }

    /// <summary>Application username (CLIP API key) issued by the bridge
    /// at pairing. Sent on every REST call as the auth segment of the
    /// URL. Sensitive but recoverable — the user can revoke from the Hue
    /// mobile app.</summary>
    public string? HueUsername { get; set; }

    /// <summary>CLIP v1 id of the last group the user selected (e.g.
    /// "1", "5"). Used to pre-select the group in the Playground combo
    /// after restoring from settings.</summary>
    public string? HueLastGroupId { get; set; }

    // ── Monitor selection (J9 scaffolding) ─────────────────────────
    //
    // Win32 device name of the monitor the user picked as the capture
    // source (e.g. "\\\\.\\DISPLAY1"). Null = follow the primary, which
    // is the V0 default and matches the current ScreenCaptureService
    // behaviour. The UI selector lands in J9 ; the persistence field is
    // pre-wired here so the J9 patch is purely UI + capture-service
    // wiring, no settings migration. ScreenCaptureInterop.EnumerateMonitors
    // exposes the candidate list.

    /// <summary>Selected capture source. Null = primary monitor (default,
    /// matches MonitorFromPoint(0,0) used by the current capture service).
    /// Non-null = a Win32 device name like "\\\\.\\DISPLAY1" obtained from
    /// <c>ScreenCaptureInterop.EnumerateMonitors</c>.</summary>
    public string? SelectedMonitorDeviceName { get; set; }

    // ── Mode selection (J6 scaffolding) ────────────────────────────
    //
    // J3 step 2 ships only the Game / Ambilight behaviour — full
    // saturation, direct mapping of the analysed average. J6 adds
    // Realistic (low saturation, diegetic-light heuristic). The enum
    // and the property are pre-wired so J6 lands as a behavioural
    // patch with no settings migration ; Game stays the default until
    // Louis tunes Realistic.

    /// <summary>Active mode preset. Defaults to <see cref="AmbientMode.Game"/>.
    /// Switching the mode (via the Playground or the Settings page)
    /// invokes <see cref="AmbientSettingsService.ApplyPreset(AmbientMode)"/>
    /// which copies the preset's tuning snapshot onto the other knobs.
    /// Touching any tuning slider in the Playground silently switches
    /// the mode to <see cref="AmbientMode.Custom"/>.</summary>
    public AmbientMode Mode { get; set; } = AmbientMode.Game;

    // ── Multi-light zones (J4) ─────────────────────────────────────
    //
    // When the connected output is an IMultiLightOutput and the engine
    // is told to run in multi-light mode, it pushes one colour per
    // zone — top / bottom / left / right border of the screen — and
    // every light assigned to a zone via <see cref="LightZones"/>
    // receives that colour. Lights mapped to <see cref="LightZone.None"/>
    // (or absent from the map entirely) are not driven by the engine
    // and keep whatever state the bridge last gave them.
    //
    // When <see cref="UseMultiLight"/> is false (default), the engine
    // falls back to the single-colour group push regardless of what
    // <see cref="LightZones"/> holds — useful for A/B comparison
    // without losing the zone assignments.

    /// <summary>Master switch for the multi-light pipeline. False keeps
    /// the legacy "one average → group action" behaviour. True activates
    /// per-zone sampling driven by <see cref="LightZones"/>, but only
    /// if the connected driver exposes
    /// <see cref="Deckle.Lighting.IMultiLightOutput"/> ; otherwise the
    /// engine logs a warning and falls back to single-colour push.</summary>
    public bool UseMultiLight { get; set; } = false;

    /// <summary>Per-light zone assignment, keyed by the driver's opaque
    /// light id (Hue : CLIP v1 integer-as-string). Empty by default ;
    /// the assignment UI populates it as the user picks a zone in the
    /// combo box of each light. Light ids missing from the map default
    /// to <see cref="LightZone.None"/> at sampling time, so a newly
    /// added bulb is skipped silently rather than tinted to an
    /// arbitrary edge.</summary>
    public Dictionary<string, LightZone> LightZones { get; set; } = new();

    /// <summary>Per-light brightness multiplier in [0, 1], keyed by the
    /// driver's opaque light id. 1.0 = full intensity (the sampled
    /// colour pushed verbatim), 0.5 = half (R/G/B halved before push,
    /// which also halves Hue's derived <c>bri</c>), 0 = effectively off
    /// (the off-threshold clamp kicks in and Hue receives <c>on:false</c>).
    /// Light ids missing from the map default to 1.0 — a newly added
    /// bulb runs at full brightness until the user adjusts its slider.
    /// Stored separately from <see cref="LightZones"/> so the user can
    /// dim a single lamp without losing its zone assignment.</summary>
    public Dictionary<string, double> LightBrightness { get; set; } = new();

    // ── HDR tuning (this branch) ───────────────────────────────────
    //
    // Four user-tunable sliders exposed in AmbientPage. Analogous to
    // the basic colour-grading panel of a video editor (exposure /
    // saturation / lift / response curve). Defaults aim at a "Hue
    // Sync-like" presence out of the box on HDR displays — bright and
    // saturated. Each setting is read on every tick by AmbientEngine
    // via the host so changes apply live without restarting the
    // pipeline.
    //
    // Why these four :
    //   - Exposure compensates for scRGB content peaking well below
    //     the display's reported MaxLuminance on a typical scene,
    //     which leaves the post-tone-map output dim. +1 EV roughly
    //     doubles brightness, restoring "Hue Sync" presence.
    //   - Saturation boost compensates for the de-saturation that
    //     happens when spatially averaging bright + dark pixels (the
    //     average drifts toward grey). Applied in OKLCh so hue stays
    //     stable and perceived luminance doesn't drift across the
    //     hue wheel.
    //   - Min brightness compensates for HueColorMath deriving bri
    //     from max(R,G,B) — a mid-tone scene like (60, 40, 80) gives
    //     bri ≈ 31 %, dim enough that the lamp's diffuser swallows
    //     the colour. A floor of ~180 keeps the chromaticity
    //     readable on the lamp without manual scene-by-scene
    //     adjustment.
    //   - Brightness curve gamma squashes the bottom of the bri range
    //     via a power law on max(R,G,B). The linear default reads as
    //     visibly lit in a dark room on dim scenes (max ≈ 25/255 still
    //     pushes bri ≈ 25). gamma > 1 stretches the bottom of the
    //     range without touching saturated highlights : (max/255)^γ ×
    //     254. gamma = 1.0 disables. Applied as a uniform RGB scale
    //     so xy chromaticity stays invariant — only bri drops.

    /// <summary>Exposure compensation in EV (stops of light) applied
    /// in linear-light before the tone-map. 0 = no change (default),
    /// +1 doubles brightness, -1 halves it. Range of practical
    /// interest [-2, +2]. Tuned in AmbientPage.</summary>
    public double ExposureEv { get; set; } = 0.0;

    /// <summary>Chroma multiplier applied to each sampled colour
    /// before push. 1.0 = no change (default), 2.0 = double
    /// saturation, 0.0 = greyscale. Range of practical interest
    /// [0, 2]. Applied in HSV-S to keep hue stable.</summary>
    public double SaturationBoost { get; set; } = 1.0;

    /// <summary>Floor for the bri value pushed to Hue, in the bridge's
    /// 0–254 range. The derived bri (max-channel based) is raised to
    /// this floor when the lamp is on (i.e. above OffThreshold), so
    /// mid-tone scenes don't dim the lamp below readability. 0
    /// disables the floor, 254 forces full brightness for any non-
    /// dark scene. Default 180 ≈ 70 % — bright enough to colour the
    /// room, dim enough to follow the screen's intent. Tuned in
    /// AmbientPage.</summary>
    public int MinBrightness { get; set; } = 180;

    /// <summary>Shape of the brightness response curve applied to the
    /// max-channel before deriving the Hue <c>bri</c> value. Lets the
    /// user pick between four canonical responses without piling more
    /// sliders — a single parameter (<see cref="BrightnessCurveParam"/>)
    /// is reinterpreted by each curve. See the enum members for
    /// details.</summary>
    public BrightnessCurveType BrightnessCurveType { get; set; } = BrightnessCurveType.Gamma;

    /// <summary>Auxiliary parameter for the active brightness curve.
    /// Default 1.8 maps to a γ = 1.8 power law under
    /// <see cref="BrightnessCurveType.Gamma"/>, which is the legacy
    /// shipping curve. Range and meaning vary per curve type :
    /// Gamma = exponent in [1.0, 3.0] ; SCurve = steepness in
    /// [1.0, 5.0] ; Linear / Logarithmic ignore this value.</summary>
    public double BrightnessCurveParam { get; set; } = 1.8;

    /// <summary>Sum-of-absolute-channel-deltas threshold that gates
    /// pushes — if the new tuned-and-smoothed colour differs from the
    /// last pushed one by less than this on the 0-765 scale, the
    /// push is dropped. 0 disables the gate, useful when smoothing
    /// already does the heavy lifting. Default 6 (raised from the
    /// legacy 3) damps the residual quantisation noise without
    /// killing perceptible motion. Tuned in the Playground.</summary>
    public int ChangeThreshold { get; set; } = 6;

    /// <summary>Exponential moving average factor applied to the colour
    /// pushed to each light, in [0, 1]. 1.0 disables the filter
    /// (instantaneous response, legacy behaviour). Lower values trade
    /// reactivity for stability — 0.30 (the default) damps over
    /// roughly 3-5 frames at 15 Hz, which is enough to swallow the
    /// jitter of small moving reflections in a globally dark scene
    /// without dulling real scene changes. Below ~0.10 the lamp lags
    /// perceptibly during fast cuts. Applied per-light in multi
    /// mode, on the single group colour in group mode. Range of
    /// practical interest [0.05, 1.0]. Tuned in the Playground.</summary>
    public double SmoothingAlpha { get; set; } = 0.30;
}

/// <summary>How <see cref="AmbientEngine"/> derives the colour pushed
/// to the lights. The active value lives in <see cref="AmbientSettings.Mode"/>.
/// Game / Movie / Ambient carry preset tunings ; Custom is the
/// implicit mode the user lands on as soon as they move a slider in
/// the Playground, signalling "I'm tuning my own thing — don't
/// overwrite my values when a preset changes".</summary>
public enum AmbientMode
{
    /// <summary>Game / Ambilight — direct mapping with vivid saturation
    /// and quick response. Default for desktop play sessions.</summary>
    Game,

    /// <summary>Movie — softened saturation, longer smoothing, slight
    /// negative EV. Reads as ambient mood lighting for cinematic
    /// content where fast colour changes would distract.</summary>
    Movie,

    /// <summary>Ambient — heavy smoothing, low saturation, dark-friendly
    /// curve. Tuned for general-purpose room lighting that follows
    /// the screen without ever feeling like it competes with the
    /// content.</summary>
    Ambient,

    /// <summary>Custom — every tuning knob lives where the user put it.
    /// The Playground sets this implicitly the moment any slider is
    /// touched, so a preset switch never silently overwrites a hand-
    /// calibrated setup.</summary>
    Custom,
}

/// <summary>Shape of the brightness response curve applied to the
/// max-channel of the tuned sRGB output before the Hue <c>bri</c>
/// value is derived. Each shape reinterprets the auxiliary
/// <see cref="AmbientSettings.BrightnessCurveParam"/> slider so the
/// user can swap responses without losing the slider's footprint.</summary>
public enum BrightnessCurveType
{
    /// <summary>Direct pass-through : <c>bri = max</c>. Param ignored.
    /// Use when smoothing alone is enough to keep the lamp readable
    /// on dim scenes and any non-linear squash would feel uneven.</summary>
    Linear,

    /// <summary>Power-law : <c>bri = (max/255)^γ × 254</c> with γ =
    /// param. γ = 1.0 collapses to Linear. γ &gt; 1 squashes the
    /// bottom of the range — a scene with max = 25/255 pushes bri
    /// ≈ 4 at γ = 1.8 instead of bri ≈ 25 linear, which reads as
    /// dim in a dark room. Saturated highlights are untouched.
    /// Range of practical interest [1.0, 3.0]. Default shape.</summary>
    Gamma,

    /// <summary>Logistic S-curve centered on 0.5 with steepness = param :
    /// <c>bri = 1 / (1 + exp(-k × (x - 0.5)))</c> remapped so the
    /// endpoints stay at 0 and 254. Pushes mid-tones away from grey
    /// in both directions — dim scenes get darker, bright scenes
    /// brighter — useful when the screen content is high-contrast.
    /// Param range [1.0, 5.0] ; 1.0 reads almost-linear, 5.0
    /// reads almost-hard-step.</summary>
    SCurve,

    /// <summary>Logarithmic : <c>bri = log(1 + 9 × x) / log(10) × 254</c>.
    /// Pushes the bottom of the range up — even very dim scenes
    /// stay clearly lit. Param ignored. Use when the room is bright
    /// enough that subtle scenes would otherwise read as "lamp
    /// off".</summary>
    Logarithmic,
}
