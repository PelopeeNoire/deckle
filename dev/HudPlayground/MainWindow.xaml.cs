using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WhispUI.Controls;

namespace HudPlayground;

// Hosts the four HudChrono state previews on the left rail. Each
// instance gets pinned to a fixed HudState at Loaded so Louis can
// see all four simultaneously without hotkey gymnastics.
//
// State wiring details:
//   - Charging    — no stroke, greyscale clock, no animation. Static.
//   - Recording   — frozen-rotation stroke with RMS-driven opacity.
//                   The simulated RMS pump (Objectif 4) will feed
//                   UpdateAudioLevel on this instance only.
//   - Transcribing / Rewriting — spinning conic+arc strokes + swipe
//                   reveal on the clock. Both drive themselves via
//                   CompositionTarget.Rendering, no external pump.
//
// The clock value shown under Recording / Transcribing / Rewriting
// will drift from 00.00.00 on each state entry because HudChrono
// restarts its stopwatch on ApplyState(Recording) — that's the real
// behaviour and we want to see it here too.
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 800 × 900 gives enough vertical room for the four stacked
        // HudChrono previews (~100 dip each plus labels and spacing)
        // and enough horizontal room for the right-rail sliders that
        // land in Objectif 4. Positioned top-left by default.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 980));

        // Pin each preview to its state once the control tree is
        // live. HudChrono.ApplyState is safe to call post-Loaded
        // because that's when ProcessingSurfaceHost has a valid
        // compositor attach point. Window.Content is a UIElement in
        // WinUI 3 and UIElement has no Loaded event — cast to
        // FrameworkElement (Grid) to access it.
        if (this.Content is FrameworkElement root)
        {
            root.Loaded += (_, _) =>
            {
                ChargingPreview.ApplyState(HudState.Charging);
                RecordingPreview.ApplyState(HudState.Recording);
                TranscribingPreview.ApplyState(HudState.Transcribing);
                RewritingPreview.ApplyState(HudState.Rewriting);
            };
        }
    }
}
