using System.Numerics;

namespace Deckle.Composition;

// ── Easing ────────────────────────────────────────────────────────────────────
//
// Pure easing primitives reusable across the Deckle family. Lives in
// Deckle.Composition because the swipe wave animator and any future
// reveal / pulse animation pulls its curve shape from here.
//
// Math is pure: no Composition runtime, no allocations, no IO.
//
// Extracted from Controls/HudChrono.xaml.cs on 2026-05-02.
public static class Easing
{
    // Cubic-bezier ease with anchor points P0=(0,0), P3=(1,1) and free
    // control points p1, p2. Given input x on [0, 1], solves Bx(u) = x via
    // Newton-Raphson, then returns By(u). WebKit's UnitBezier formulation —
    // the polynomial coefficients collapse to 3 fused-multiply-adds per
    // sample, and 8 Newton iterations get us well below sub-pixel accuracy
    // for any reasonable control-point layout.
    public static float CubicBezier(float x, Vector2 p1, Vector2 p2)
    {
        float cx = 3f * p1.X;
        float bx = 3f * (p2.X - p1.X) - cx;
        float ax = 1f - cx - bx;
        float cy = 3f * p1.Y;
        float by = 3f * (p2.Y - p1.Y) - cy;
        float ay = 1f - cy - by;

        float u = x;
        for (int i = 0; i < 8; i++)
        {
            float sampleX = ((ax * u + bx) * u + cx) * u - x;
            if (MathF.Abs(sampleX) < 1e-4f) break;
            float dx = (3f * ax * u + 2f * bx) * u + cx;
            if (MathF.Abs(dx) < 1e-6f) break;
            u -= sampleX / dx;
        }
        u = Math.Clamp(u, 0f, 1f);
        return ((ay * u + by) * u + cy) * u;
    }
}
