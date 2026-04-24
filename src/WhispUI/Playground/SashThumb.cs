using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace WhispUI.Playground;

// ─── Resizable sash control ────────────────────────────────────────────────────
//
// Horizontal-resize divider between the preview and tuning panels. Hosts
// the SizeWestEast cursor (universal "drag me" affordance) and emits a
// horizontal-delta event as the user drags.
//
// Why not Thumb: Microsoft.UI.Xaml.Controls.Primitives.Thumb is sealed in
// WinUI 3 so we cannot subclass it to set ProtectedCursor (the only way
// to change an element's pointer cursor in WinUI 3 — the attached Cursor
// property from WPF is gone). ContentControl is not sealed and gives us
// Background + a Template property for the divider fill; we re-implement
// the drag ourselves via pointer capture, which is a few lines anyway.

public sealed class SashThumb : ContentControl
{
    // ΔX in window-relative coordinates since the last PointerMoved tick.
    // Caller clamps PreviewCol.Width with it — exactly the signature the
    // Thumb-based version used to consume via DragDeltaEventArgs.HorizontalChange.
    public event TypedEventHandler<SashThumb, double>? HorizontalDragDelta;

    private bool   _dragging;
    private double _lastX;

    public SashThumb()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        IsTabStop = false;
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        // CapturePointer routes subsequent pointer events to us regardless of
        // where the pointer is — essential so a fast horizontal drag that
        // outruns the 6 dp sash width doesn't lose the grip.
        if (CapturePointer(e.Pointer))
        {
            _dragging = true;
            _lastX = e.GetCurrentPoint(null).Position.X;
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        double x = e.GetCurrentPoint(null).Position.X;
        double dx = x - _lastX;
        _lastX = x;
        if (dx != 0) HorizontalDragDelta?.Invoke(this, dx);
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
        {
            ReleasePointerCapture(e.Pointer);
            _dragging = false;
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }
}
