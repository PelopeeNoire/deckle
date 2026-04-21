using System;
using Microsoft.UI.Xaml;
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

        // Primary: every defined profile. Match the saved slot first by stable
        // ProfileId (survives renames), then fall back to ProfileName for
        // pre-migration configs.
        PrimaryRewriteProfileCombo.Items.Clear();
        int primaryIndex = ResolveSlotIndex(s, s.PrimaryRewriteProfileId, s.PrimaryRewriteProfileName);
        for (int i = 0; i < s.Profiles.Count; i++)
            PrimaryRewriteProfileCombo.Items.Add(new ComboBoxItem { Content = s.Profiles[i].Name });
        PrimaryRewriteProfileCombo.SelectedIndex = primaryIndex >= 0 ? primaryIndex
            : (s.Profiles.Count > 0 ? 0 : -1);

        // Secondary: same list prefixed with "(None)". Default to (None) when
        // nothing resolves.
        SecondaryRewriteProfileCombo.Items.Clear();
        SecondaryRewriteProfileCombo.Items.Add(new ComboBoxItem { Content = NoneSentinel });
        for (int i = 0; i < s.Profiles.Count; i++)
            SecondaryRewriteProfileCombo.Items.Add(new ComboBoxItem { Content = s.Profiles[i].Name });
        int secondaryIndex = ResolveSlotIndex(s, s.SecondaryRewriteProfileId, s.SecondaryRewriteProfileName);
        SecondaryRewriteProfileCombo.SelectedIndex = secondaryIndex >= 0 ? secondaryIndex + 1 : 0;

        _loading = false;
    }

    private static int ResolveSlotIndex(LlmSettings s, string? id, string? name)
    {
        if (!string.IsNullOrEmpty(id))
        {
            for (int i = 0; i < s.Profiles.Count; i++)
                if (s.Profiles[i].Id == id) return i;
        }
        if (!string.IsNullOrWhiteSpace(name))
        {
            for (int i = 0; i < s.Profiles.Count; i++)
                if (string.Equals(s.Profiles[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return i;
        }
        return -1;
    }

    private void PrimaryRewriteProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PrimaryRewriteProfileCombo.SelectedItem is not ComboBoxItem item) return;
        var s = SettingsService.Instance.Current.Llm;
        string name = item.Content?.ToString() ?? "";
        s.PrimaryRewriteProfileName = name;
        s.PrimaryRewriteProfileId = s.Profiles.Find(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;
        SettingsService.Instance.Save();
    }

    private void SecondaryRewriteProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SecondaryRewriteProfileCombo.SelectedItem is not ComboBoxItem item) return;
        string? content = item.Content?.ToString();
        var s = SettingsService.Instance.Current.Llm;
        bool none = string.Equals(content, NoneSentinel, StringComparison.Ordinal);
        s.SecondaryRewriteProfileName = none ? null : content;
        s.SecondaryRewriteProfileId = none ? null : s.Profiles.Find(p =>
            string.Equals(p.Name, content, StringComparison.OrdinalIgnoreCase))?.Id;
        SettingsService.Instance.Save();
    }

    // Scope: Primary/Secondary rewrite slot bindings only. The Profiles list
    // itself stays untouched — resetting the shortcut picks should not wipe
    // user-authored profiles.
    private void ResetSection_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new LlmSettings();
        var s = SettingsService.Instance.Current.Llm;
        s.PrimaryRewriteProfileName   = defaults.PrimaryRewriteProfileName;
        s.PrimaryRewriteProfileId     = defaults.PrimaryRewriteProfileId;
        s.SecondaryRewriteProfileName = defaults.SecondaryRewriteProfileName;
        s.SecondaryRewriteProfileId   = defaults.SecondaryRewriteProfileId;
        SettingsService.Instance.Save();
        Reload();
    }
}
