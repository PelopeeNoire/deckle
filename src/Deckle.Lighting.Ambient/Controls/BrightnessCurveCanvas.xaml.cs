using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Deckle.Lighting.Ambient.Controls;

// Small square widget that plots the brightness response curve used by
// AmbientEngine.ApplyBrightnessCurve : output bri = (input max / 255)^γ
// × 254. Two polylines overlap inside a 160×160 canvas — a grey dashed
// diagonal as the linear reference (γ = 1.0), and the live accent
// curve recomputed every time the Gamma dependency property changes.
//
// Axes : X = input max channel (0 → 255 left-to-right), Y = pushed bri
// (0 bottom → 254 top). No labels inside the canvas — the consumer
// places "max channel" / "pushed bri" captions outside if it wants
// them, which keeps the widget compact and readable at this size.
//
// Sampling : 64 segments is enough for a smooth curve at this size
// (each segment ≈ 2.5 px on the X axis). The reference and curve
// polylines share the same X grid so the eye can compare offsets at a
// glance — at γ = 1.8, the accent curve sits visibly below the
// reference for most of the range and they meet at (0,0) and (255,254).
//
// Theme resources only — no magic colours. Stroke/background follow the
// active theme + accent automatically. The control reads the
// PlotCanvas's actual width / height at draw time, so resizing the
// XAML <Border> later (e.g. for a larger Playground variant) just
// works.
public sealed partial class BrightnessCurveCanvas : UserControl
{
    private const int SampleCount = 64;
    private const double PlotPadding  = 4.0;

    public BrightnessCurveCanvas()
    {
        InitializeComponent();
        Loaded += (_, _) => RebuildCurves();
    }

    // ── Gamma DP ────────────────────────────────────────────────────
    //
    // The single live input. The DP changed callback redraws only the
    // accent curve ; the reference line is built once on Loaded since
    // it doesn't depend on Gamma. Clamped to a sensible range so a
    // misconfigured caller (γ = 0, γ = NaN) doesn't crash the redraw.

    public static readonly DependencyProperty GammaProperty =
        DependencyProperty.Register(
            nameof(Gamma),
            typeof(double),
            typeof(BrightnessCurveCanvas),
            new PropertyMetadata(1.0, OnGammaChanged));

    public double Gamma
    {
        get => (double)GetValue(GammaProperty);
        set => SetValue(GammaProperty, value);
    }

    private static void OnGammaChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is BrightnessCurveCanvas self) self.RebuildCurves();
    }

    private void RebuildCurves()
    {
        // Width / height come from the canvas's actual layout slot ;
        // before the first measure pass they're 0 (Loaded fires after
        // measure on the standard show path, so 0 means "called too
        // early" — bail and let the next change re-trigger).
        double w = PlotCanvas.ActualWidth;
        double h = PlotCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        PlotCanvas.Children.Clear();

        // Linear reference (γ = 1) from bottom-left to top-right. Drawn
        // with a 1-dip dashed grey stroke so the eye reads it as
        // "baseline" rather than competing with the accent curve.
        var reference = new Polyline
        {
            Stroke = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            StrokeThickness = 1.0,
            StrokeDashArray = new DoubleCollection { 3.0, 3.0 },
        };
        reference.Points.Add(new Point(PlotPadding, h - PlotPadding));
        reference.Points.Add(new Point(w - PlotPadding, PlotPadding));
        PlotCanvas.Children.Add(reference);

        // Accent curve : 64 samples, X mapped to [PlotPadding, w - PlotPadding]
        // and Y inverted because canvas Y grows downward.
        var curve = new Polyline
        {
            Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };

        // Defensive clamp : Gamma = 0 would map every input ratio to 1
        // (constant max), Gamma < 0 flips the curve. Both are
        // misconfiguration, not intent — pin to the documented range.
        double g = double.IsNaN(Gamma) || Gamma <= 0 ? 1.0 : Gamma;

        for (int i = 0; i <= SampleCount; i++)
        {
            double ratio = (double)i / SampleCount;
            double yNorm = System.Math.Pow(ratio, g); // [0, 1]
            double x = PlotPadding + ratio * (w - 2 * PlotPadding);
            double y = (h - PlotPadding) - yNorm * (h - 2 * PlotPadding);
            curve.Points.Add(new Point(x, y));
        }

        PlotCanvas.Children.Add(curve);
    }
}
