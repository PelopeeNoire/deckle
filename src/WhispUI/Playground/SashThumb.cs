using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace WhispUI.Playground;

// ─── Sash Thumb with resize cursor ─────────────────────────────────────────────
//
// Plain Thumb subclass whose sole job is to set the pointer cursor to the
// horizontal resize shape (SizeWestEast) when hovering the 6 dp divider
// column between the preview and tuning panels. Produces the universal
// "drag me" affordance the user expects on a resizable split.
//
// ProtectedCursor is protected on UIElement — it cannot be set from XAML
// or from outside the class, hence the subclass.

public sealed class SashThumb : Thumb
{
    public SashThumb()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
