using System;
using Microsoft.UI.Xaml.Controls;

namespace WhispUI.Settings.Llm;

// ─── Shortcut slots section of LlmPage ──────────────────────────────────────
//
// Two ComboBoxes, one per manual rewrite slot:
//   Slot A (Ctrl+Win+`)       — profile name is required (defaults to first)
//   Slot B (Ctrl+Shift+Win+`) — optional, "(None)" leaves the slot unbound
//
// The list depends on the defined profiles — Reload() must be called by the
// host after any mutation of the Profiles section so newly-added / renamed
// profiles show up here.

public sealed partial class LlmShortcutSlotsSection : UserControl
{
    // Sentinel shown in Slot B to mean "shortcut pressed but no rewrite".
    // Stored as null in settings — not as this string.
    private const string NoneSentinel = "(None)";

    private bool _loading;

    public LlmShortcutSlotsSection()
    {
        InitializeComponent();
    }

    public void Reload()
    {
        _loading = true;
        var s = SettingsService.Instance.Current.Llm;

        // Slot A: every defined profile, default to the first if the saved
        // name no longer matches any profile (e.g. user renamed it).
        SlotAProfileCombo.Items.Clear();
        int slotAIndex = -1;
        for (int i = 0; i < s.Profiles.Count; i++)
        {
            SlotAProfileCombo.Items.Add(new ComboBoxItem { Content = s.Profiles[i].Name });
            if (string.Equals(s.Profiles[i].Name, s.SlotAProfileName, StringComparison.OrdinalIgnoreCase))
                slotAIndex = i;
        }
        SlotAProfileCombo.SelectedIndex = slotAIndex >= 0 ? slotAIndex
            : (s.Profiles.Count > 0 ? 0 : -1);

        // Slot B: same list prefixed with "(None)". Default to (None) when
        // the saved name is null/blank or no longer resolves.
        SlotBProfileCombo.Items.Clear();
        SlotBProfileCombo.Items.Add(new ComboBoxItem { Content = NoneSentinel });
        int slotBIndex = 0; // (None) at index 0
        for (int i = 0; i < s.Profiles.Count; i++)
        {
            SlotBProfileCombo.Items.Add(new ComboBoxItem { Content = s.Profiles[i].Name });
            if (!string.IsNullOrWhiteSpace(s.SlotBProfileName) &&
                string.Equals(s.Profiles[i].Name, s.SlotBProfileName, StringComparison.OrdinalIgnoreCase))
                slotBIndex = i + 1;
        }
        SlotBProfileCombo.SelectedIndex = slotBIndex;

        _loading = false;
    }

    private void SlotAProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SlotAProfileCombo.SelectedItem is not ComboBoxItem item) return;
        SettingsService.Instance.Current.Llm.SlotAProfileName = item.Content?.ToString() ?? "";
        SettingsService.Instance.Save();
    }

    private void SlotBProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SlotBProfileCombo.SelectedItem is not ComboBoxItem item) return;
        string? content = item.Content?.ToString();
        SettingsService.Instance.Current.Llm.SlotBProfileName =
            string.Equals(content, NoneSentinel, StringComparison.Ordinal) ? null : content;
        SettingsService.Instance.Save();
    }
}
