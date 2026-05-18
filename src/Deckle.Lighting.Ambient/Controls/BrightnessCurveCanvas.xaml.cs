using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Deckle.Lighting.Ambient.Controls;

// Small square widget that plots the brightness response curve used by
// AmbientEngine.ApplyBrightnessCurve. Two polylines overlap inside a
// 160×160 canvas — a grey dashed diagonal as the linear reference,
// and the live accent curve recomputed every time the CurveType or
// the Gamma (parameter) dependency property changes.
//
// Four curve shapes are supported : Linear (passes through the
// reference diagonal), Gamma (power law), SCurve (logistic
// normalised to the corners), Logarithmic (lifts the bottom of the
// range). The Gamma DP doubles as a generic parameter — it carries
// the gamma exponent for Gamma curves and the steepness k for
// SCurves. Linear and Logarithmic ignore it.
//
// Axes : X = input max channel (0 → 255 left-to-right), Y = pushed
// bri (0 bottom → 254 top). No labels — the consumer places captions
// outside the widget.
//
// The accent curve is greyed when the widget is set as "muted" by
// the consumer (Opacity = 0.4 via XAML) — useful for curves whose
// parameter the user can't tune (Linear, Logarithmic).
public sealed partial class BrightnessCurveCanvas : UserControl
{
    private const int SampleCount = 64;
    private const double PlotPadding  = 4.0;

    public BrightnessCurveCanvas()
    {
        InitializeComponent();
        Loaded += (_, _) => RebuildCurves();
        SizeChanged += (_, _) => RebuildCurves();
    }

    // ── Curve parameter DP ───────────────────────────────────────────
    //
    // Generic shape parameter. Gamma exponent for CurveType.Gamma,
    // logistic steepness k for CurveType.SCurve. Ignored by Linear
    // and Logarithmic. Defensive clamps in RebuildCurves catch NaN /
    // ≤ 0 values from misconfigured callers.

    public static readonly DependencyProperty GammaProperty =
        DependencyProperty.Register(
            nameof(Gamma),
            typeof(double),
            typeof(BrightnessCurveCanvas),
            new PropertyMetadata(1.0, OnAnyVisualChanged));

    public double Gamma
    {
        get => (double)GetValue(GammaProperty);
        set => SetValue(GammaProperty, value);
    }

    // ── Curve type DP ────────────────────────────────────────────────
    //
    // Selects the shape the canvas plots. Stored as int so the DP
    // system doesn't choke on the enum default — consumers set it
    // through the typed CurveType property below.

    public static readonly DependencyProperty CurveTypeProperty =
        DependencyProperty.Register(
            nameof(CurveType),
            typeof(BrightnessCurveType),
            typeof(BrightnessCurveCanvas),
            new PropertyMetadata(BrightnessCurveType.Gamma, OnAnyVisualChanged));

    public BrightnessCurveType CurveType
    {
        get => (BrightnessCurveType)GetValue(CurveTypeProperty);
        set => SetValue(CurveTypeProperty, value);
    }

    private static void OnAnyVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
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

        // Linear reference (γ = 1) from bottom-left to top-right.
        // Always drawn so the eye can compare the accent curve to
        // the baseline even when the active curve happens to be
        // Linear (the reference will sit underneath, perfectly
        // matched).
        var reference = new Polyline
        {
            Stroke = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            StrokeThickness = 1.0,
            StrokeDashArray = new DoubleCollection { 3.0, 3.0 },
        };
        reference.Points.Add(new Point(PlotPadding, h - PlotPadding));
        reference.Points.Add(new Point(w - PlotPadding, PlotPadding));
        PlotCanvas.Children.Add(reference);

        // Accent curve sampled along the selected shape.
        var curve = new Polyline
        {
            Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };

        double param = double.IsNaN(Gamma) || Gamma <= 0 ? 1.0 : Gamma;

        for (int i = 0; i <= SampleCount; i++)
        {
            double ratio = (double)i / SampleCount;
            double yNorm = SampleCurve(CurveType, param, ratio); // [0, 1]
            double x = PlotPadding + ratio * (w - 2 * PlotPadding);
            double y = (h - PlotPadding) - yNorm * (h - 2 * PlotPadding);
            curve.Points.Add(new Point(x, y));
        }

        PlotCanvas.Children.Add(curve);
    }

    // Same shape definitions as AmbientEngine.ApplyBrightnessCurve, in
    // a normalised [0, 1] → [0, 1] form. Kept here rather than shared
    // with the engine because the canvas is a pure visualisation —
    // splitting the math out would couple the UI control to the
    // engine assembly for one short switch.
    private static double SampleCurve(BrightnessCurveType type, double param, double x)
    {
        switch (type)
        {
            case BrightnessCurveType.Linear:
                return x;

            case BrightnessCurveType.Gamma:
                return System.Math.Pow(x, param);

            case BrightnessCurveType.SCurve:
                double k = System.Math.Max(0.01, param);
                double a = 1.0 / (1.0 + System.Math.Exp(0.5 * k));
                double b = 1.0 / (1.0 + System.Math.Exp(-0.5 * k));
                double raw = 1.0 / (1.0 + System.Math.Exp(-k * (x - 0.5)));
                return (raw - a) / (b - a);

            case BrightnessCurveType.Logarithmic:
                return System.Math.Log10(1.0 + 9.0 * x);

            default:
                return x;
        }
    }
}
