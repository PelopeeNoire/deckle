using System;
using Microsoft.UI.Xaml.Controls;

namespace WhispUI.Settings.Llm;

// ─── Rewrite shortcuts section of LlmPage ──────────────────────────────────
//
// Two ComboBoxes, one per manual rewrite shortcut:
//   Primary rewrite   (Shift+Win+`)  — profile name is required (defaults to first)
//   Secondary rewrite (Ctrl+Win+`)   — optional, "(None)" leaves the shortcut unbound
//
// The list depends on the defined profiles — Reload() must be called by the
// host after any mutation of the Profiles section so newly-added / renamed
// profiles show up here.

public sealed partial class LlmShortcutSlotsSection : UserControl
{
    // Sentinel shown in the secondary combo to mean "shortcut pressed but no
    // rewrite". Stored as null in settings — not as this string.
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

        // Primary: every defined profile, default to the first if the saved
        // name no longer matches any profile (e.g. user renamed it).
        PrimaryRewriteProfileCombo.Items.Clear();
        int primaryIndex = -1;
        for (int i = 0; i < s.Profiles.Count; i++)
        {
            PrimaryRewriteProfileCombo.Items.Add(new ComboBoxItem { Content = s.Profiles[i].Name });
            if (string.Equals(s.Profiles[i].Name, s.PrimaryRewriteProfileName, StringComparison.OrdinalIgnoreCase))
                primaryIndex = i;
        }
        PrimaryRewriteProfileCombo.SelectedIndex = primaryIndex >= 0 ? primaryIndex
            : (s.Profiles.Count > 0 ? 0 : -1);

        // Secondary: same list prefixed with "(None)". Default to (None) when
        // the saved name is null/blank or no longer resolves.
        SecondaryRewriteProfileCombo.Items.Clear();
        SecondaryRewriteProfileCombo.Items.Add(new ComboBoxItem { Content = NoneSentinel });
        int secondaryIndex = 0; // (None) at index 0
        for (int i = 0; i < s.Profiles.Count; i++)
        {
            SecondaryRewriteProfileCombo.Items.Add(new ComboBoxItem { Content = s.Profiles[i].Name });
            if (!string.IsNullOrWhiteSpace(s.SecondaryRewriteProfileName) &&
                string.Equals(s.Profiles[i].Name, s.SecondaryRewriteProfileName, StringComparison.OrdinalIgnoreCase))
                secondaryIndex = i + 1;
        }
        SecondaryRewriteProfileCombo.SelectedIndex = secondaryIndex;

        _loading = false;
    }

    private void PrimaryRewriteProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PrimaryRewriteProfileCombo.SelectedItem is not ComboBoxItem item) return;
        SettingsService.Instance.Current.Llm.PrimaryRewriteProfileName = item.Content?.ToString() ?? "";
        SettingsService.Instance.Save();
    }

    private void SecondaryRewriteProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SecondaryRewriteProfileCombo.SelectedItem is not ComboBoxItem item) return;
        string? content = item.Content?.ToString();
        SettingsService.Instance.Current.Llm.SecondaryRewriteProfileName =
            string.Equals(content, NoneSentinel, StringComparison.Ordinal) ? null : content;
        SettingsService.Instance.Save();
    }
}
