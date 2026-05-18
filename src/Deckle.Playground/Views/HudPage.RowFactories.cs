using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deckle.Playground;

// ─── HudPage — row factories ────────────────────────────────────────────────
//
// Programmatic Slider + NumberBox composites used to build every tuning
// row in the Expanders. Separated from the rest of the page so the
// generic UI plumbing (paired-value sync guard, label formatting,
// direction toggle, layout grid) lives in one place and the
// section-specific methods (HudPage.Expanders.cs) stay focused on
// what each knob means.
//
// Step frequencies are deliberately coarse so user-driven values land
// on round numbers instead of float-precision artefacts (the original
// playground used (max-min)/1000 which gave 0.2676 / 0.6783 / etc.).
// Each call-site picks the right step for its range — see the comment
// at the top of HudPage.Expanders.cs.

public sealed partial class HudPage
{
    // Expander header hosts the section title and an aligned-right
    // "Reset" HyperlinkButton. The HyperlinkButton intercepts the click
    // (its routed Click marks Handled) so tapping Reset doesn't collapse
    // the Expander it sits in.
    private StackPanel NewExpander(string title, Action resetAction, bool expanded = true)
    {
        var content = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var headerGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleTb = new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(titleTb, 0);
        headerGrid.Children.Add(titleTb);

        var resetBtn = new HyperlinkButton
        {
            Content = "Reset",
            VerticalAlignment = VerticalAlignment.Center,
            // Tight padding so the Reset label doesn't bloat the header.
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, -4, 8, -4),
        };
        ToolTipService.SetToolTip(resetBtn, "Reset this section to defaults");
        resetBtn.Click += (_, _) => resetAction();
        Grid.SetColumn(resetBtn, 1);
        headerGrid.Children.Add(resetBtn);

        var expander = new Expander
        {
            Header = headerGrid,
            Content = content,
            IsExpanded = expanded,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 4),
        };
        TuningStack.Children.Add(expander);
        return content;
    }

    // Slider + NumberBox composite. Both share the same value source ;
    // the `_syncing` guard prevents the ValueChanged feedback loop.
    // Rebuild-triggering rows push through RequestRebuild so the
    // CanvasDevice doesn't thrash mid-drag.
    private void AddFloatRow(
        StackPanel stack, string label,
        double min, double max, double step, double value,
        Action<double> setter,
        bool rebuild = false)
    {
        // Clean up float→double promotion noise in the displayed
        // default. TuningModel stores floats ; promoting to double for
        // Slider.Value / NumberBox.Value exposes binary-precision
        // artefacts (0.4f shows as 0.4000006, 0.013f as 0.01300027).
        // Rounding to the step's decimal count strips the noise without
        // altering semantics.
        int digits = Math.Max(0, (int)Math.Ceiling(-Math.Log10(step)));
        double displayValue = Math.Round(value, digits);

        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = displayValue,
            StepFrequency = step,
            SmallChange = step,
            LargeChange = step * 10,
            VerticalAlignment = VerticalAlignment.Center,
            IsThumbToolTipEnabled = false,
        };
        var numberBox = new NumberBox
        {
            Value = displayValue,
            Minimum = min, Maximum = max,
            SmallChange = step,
            LargeChange = step * 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 120,
        };

        slider.ValueChanged += (_, e) =>
        {
            if (_syncing) return;
            _syncing = true;
            try
            {
                numberBox.Value = e.NewValue;
                setter(e.NewValue);
                if (rebuild) RequestRebuild();
            }
            finally { _syncing = false; }
        };
        numberBox.ValueChanged += (_, e) =>
        {
            if (_syncing) return;
            if (double.IsNaN(e.NewValue)) return;
            _syncing = true;
            try
            {
                double clamped = Math.Clamp(e.NewValue, min, max);
                slider.Value = clamped;
                setter(clamped);
                if (rebuild) RequestRebuild();
            }
            finally { _syncing = false; }
        };

        stack.Children.Add(WrapRow(label, slider, numberBox));
    }

    private void AddIntRow(
        StackPanel stack, string label,
        int min, int max, int value,
        Action<int> setter,
        bool rebuild = false)
    {
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = value,
            StepFrequency = 1,
            SmallChange = 1, LargeChange = 10,
            VerticalAlignment = VerticalAlignment.Center,
            IsThumbToolTipEnabled = false,
        };
        var numberBox = new NumberBox
        {
            Value = value,
            Minimum = min, Maximum = max,
            SmallChange = 1, LargeChange = 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 120,
        };

        slider.ValueChanged += (_, e) =>
        {
            if (_syncing) return;
            _syncing = true;
            try
            {
                int iv = (int)Math.Round(e.NewValue);
                numberBox.Value = iv;
                setter(iv);
                if (rebuild) RequestRebuild();
            }
            finally { _syncing = false; }
        };
        numberBox.ValueChanged += (_, e) =>
        {
            if (_syncing) return;
            if (double.IsNaN(e.NewValue)) return;
            _syncing = true;
            try
            {
                int iv = Math.Clamp((int)Math.Round(e.NewValue), min, max);
                slider.Value = iv;
                setter(iv);
                if (rebuild) RequestRebuild();
            }
            finally { _syncing = false; }
        };

        stack.Children.Add(WrapRow(label, slider, numberBox));
    }

    private void AddToggleRow(
        StackPanel stack, string label,
        bool value, Action<bool> setter,
        bool rebuild = false)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = value,
            OnContent = "on",
            OffContent = "off",
        };
        toggle.Toggled += (_, _) =>
        {
            setter(toggle.IsOn);
            if (rebuild) RequestRebuild();
        };

        var grid = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var labelTb = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(labelTb, 0);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(labelTb);
        grid.Children.Add(toggle);
        stack.Children.Add(grid);
    }

    // Rotation direction is semantically ±1. A slider would smuggle a
    // hidden speed multiplier via fractional values. ToggleSwitch snaps
    // strictly to -1 or +1.
    private void AddDirectionRow(
        StackPanel stack, string label,
        float value, Action<float> setter)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = value >= 0f,
            OnContent = "CW (+1)",
            OffContent = "CCW (-1)",
        };
        toggle.Toggled += (_, _) =>
        {
            setter(toggle.IsOn ? 1f : -1f);
            RequestRebuild();
        };

        var grid = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var labelTb = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(labelTb, 0);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(labelTb);
        grid.Children.Add(toggle);
        stack.Children.Add(grid);
    }

    private static Grid WrapRow(string label, Slider slider, NumberBox numberBox)
    {
        var grid = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelTb = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(labelTb, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(numberBox, 2);
        grid.Children.Add(labelTb);
        grid.Children.Add(slider);
        grid.Children.Add(numberBox);
        return grid;
    }
}
