using System;
using System.Collections.Generic;
using System.Threading;
using Deckle.Lighting.Hue;
using Deckle.Catalog;
using Deckle.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Deckle.Lighting.Ambient;

// Settings page for the Ambient Light module. Resolved by the Settings
// NavigationView from src/Deckle.Settings/SettingsWindow.xaml via the
// item Tag "Deckle.Lighting.Ambient.AmbientPage, Deckle.Lighting.Ambient".
//
// Surface : master Enabled toggle, Mode selector (Game / Realistic),
// HDR tuning sliders (Exposure / Saturation / Min brightness /
// brightness curve γ with live visualisation), Hue bridge pairing
// expander (Discover / Pair / List groups / Forget). Light zones and
// per-light brightness still live in the Playground for now ; they
// move here in a later pass if Louis decides the UI is worth it.
//
// Persistence : event-handler style (Toggled / SelectionChanged /
// ValueChanged) that mutates AmbientSettings.Current and calls
// AmbientSettingsService.Instance.Save() inline. No view-model layer.
//
// Sync state : three subscriptions wired in Loaded and dropped in
// Unloaded. The Settings Changed event re-syncs the controls from
// settings so a flip from the tray / Playground propagates immediately
// to the ToggleSwitch. The engine StateChanged event drives the
// transient UI (ModeCombo gating). The HuePairingService.BridgeChanged
// event re-syncs the Hue expander row so a re-pair / forget from the
// Playground reflects live. All three are guarded by a _loading flag
// that suppresses the re-fire loop when handlers touch the same
// controls that triggered them.
public sealed partial class AmbientPage : Page
{
    private bool _loading = true;

    // Hue pairing local state. The countdown CTS is owned by this page
    // so the Unloaded handler can cancel an in-flight pair if the user
    // navigates away. _hueIsPairing guards double-clicks on the Pair
    // button. _hueGroupComboSuppress prevents the SelectionChanged
    // handler from firing while ListGroupsAsync is repopulating the
    // combo's Items collection.
    private CancellationTokenSource? _huePairCts;
    private bool _hueIsPairing;
    private IReadOnlyList<HueGroup> _hueGroups = [];
    private bool _hueGroupComboSuppress;

    public AmbientPage()
    {
        InitializeComponent();
        Loaded   += AmbientPage_Loaded;
        Unloaded += AmbientPage_Unloaded;
    }

    private void AmbientPage_Loaded(object sender, RoutedEventArgs e)
    {
        ResyncFromSettings();
        SyncHueBridgeUi();
        ApplyEngineState(AmbientEngine.Current?.State ?? AmbientEngineState.Off);

        AmbientSettingsService.Instance.Changed += OnSettingsChanged;
        HuePairingService.Instance.BridgeChanged += OnHueBridgeChanged;
        if (AmbientEngine.Current is not null)
        {
            AmbientEngine.Current.StateChanged += OnEngineStateChanged;
        }

        _loading = false;
    }

    private void AmbientPage_Unloaded(object sender, RoutedEventArgs e)
    {
        // Cancel any in-flight pair countdown if the user navigates
        // away — PairAsync exits with OperationCanceledException, the
        // catch in OnHuePairClick resets the visuals.
        try { _huePairCts?.Cancel(); } catch { /* best effort */ }
        _huePairCts?.Dispose();
        _huePairCts = null;

        AmbientSettingsService.Instance.Changed -= OnSettingsChanged;
        HuePairingService.Instance.BridgeChanged -= OnHueBridgeChanged;
        if (AmbientEngine.Current is not null)
        {
            AmbientEngine.Current.StateChanged -= OnEngineStateChanged;
        }
        _loading = true;
    }

    // Pulls the current persisted state into the controls. Called on
    // first load and on every Changed event. Guarded by _loading so
    // the handlers don't re-fire Save during the assignment loop.
    private void ResyncFromSettings()
    {
        bool prevLoading = _loading;
        _loading = true;
        try
        {
            var s = AmbientSettingsService.Instance.Current;

            EnabledToggle.IsOn = s.Enabled;

            ComboBoxItem? toSelect = null;
            foreach (var item in ModeCombo.Items)
            {
                if (item is ComboBoxItem cbi && cbi.Tag is string tag && tag == s.Mode.ToString())
                {
                    toSelect = cbi;
                    break;
                }
            }
            ModeCombo.SelectedItem = toSelect ?? ModeCombo.Items[0];

            // Pair completeness drives the NotPaired InfoBar. The
            // criteria mirror AmbientEngine.StartAsync's validation
            // so a user who toggles ON in this state sees the InfoBar
            // BEFORE clicking (forewarned) and also AFTER the auto-
            // revert (still incomplete).
            bool paired = !string.IsNullOrEmpty(s.HueBridgeIp)
                       && !string.IsNullOrEmpty(s.HueBridgeId)
                       && !string.IsNullOrEmpty(s.HueUsername)
                       && !string.IsNullOrEmpty(s.HueLastGroupId);
            NotPairedInfoBar.IsOpen = !paired;
        }
        finally
        {
            _loading = prevLoading;
        }
    }

    private void OnSettingsChanged()
    {
        DispatcherQueue.TryEnqueue(ResyncFromSettings);
    }

    private void OnEngineStateChanged(AmbientEngineState state)
    {
        DispatcherQueue.TryEnqueue(() => ApplyEngineState(state));
    }

    // Surfaces the engine's transition state on the page. The previous
    // pass surfaced a ProgressRing for the transient Starting /
    // Stopping states, but the ring stayed stuck in one runtime
    // observation and the feature offered marginal value while a Hue
    // pair takes only ~300–800 ms ; it has been retired. The
    // ApplyEngineState pass is kept for the ModeCombo gating, which is
    // genuinely useful : changing Mode mid-Running silently desyncs
    // the radios from the pipeline shape, so we lock the combo while
    // the engine runs.
    private void ApplyEngineState(AmbientEngineState state)
    {
        ModeCombo.IsEnabled = state != AmbientEngineState.Running;
    }

    private void EnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AmbientSettingsService.Instance.Current.Enabled = EnabledToggle.IsOn;
        AmbientSettingsService.Instance.Save();
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ModeCombo.SelectedItem is ComboBoxItem cbi
            && cbi.Tag is string tag
            && Enum.TryParse<AmbientMode>(tag, out var mode))
        {
            // ApplyPreset copies the preset's full tuning snapshot
            // onto Current and saves in one shot. Custom is a special
            // case : it just sets Mode = Custom without touching any
            // other knob, so the Playground's hand-tuned values stay
            // exactly where the user left them.
            AmbientSettingsService.Instance.ApplyPreset(mode);
        }
    }

    // The four HDR slider handlers (ExposureSlider_ValueChanged et al.)
    // and their per-knob / per-section reset companions used to live
    // here. They moved out to the Playground in the V0.3 refactor —
    // the live preview next to them makes empirical tuning so much
    // cheaper that duplicating the controls in Settings was net
    // negative. The Mode preset selector above is what carries the
    // tuning intent in Settings now ; pick Game / Movie / Ambient
    // to apply a snapshot, or open the Playground for the fine-grain
    // knobs.

    // Opens the Playground via the same callback the AmbientEngine
    // uses for its NotPaired InfoBar action. The App wires this slot
    // at boot to its lazy ShowPlaygroundLazy(), so picking the card
    // here brings the Playground forward without forcing a reference
    // from Lighting.Ambient back to the app host.
    private void OpenPlaygroundCard_Click(object sender, RoutedEventArgs e)
    {
        AmbientEngine.OpenPlaygroundRequested?.Invoke();
    }

    private void ConfigureBridgeButton_Click(object sender, RoutedEventArgs e)
    {
        // Expand the Hue bridge expander and scroll it into view so the
        // user lands on the pair flow without manual scrolling. The
        // SettingsExpander.IsExpanded property is two-way bindable and
        // immediately triggers the visual transition.
        HueBridgeExpander.IsExpanded = true;
        HueBridgeExpander.StartBringIntoView();
    }

    // ── Hue pairing handlers ────────────────────────────────────────

    private void OnHueBridgeChanged()
    {
        // BridgeChanged can fire from any thread (Pair runs on a worker
        // task, Forget is direct from UI thread). Marshal to the UI
        // thread because the sync touches XAML elements.
        if (DispatcherQueue.HasThreadAccess) SyncHueBridgeUi();
        else                                 DispatcherQueue.TryEnqueue(SyncHueBridgeUi);
    }

    // Project HuePairingService state into the Hue expander visuals.
    // Idempotent : called on Loaded, on every BridgeChanged, and after
    // every local pair / forget operation. The pair status text (e.g.
    // "Waiting (30 s)") is owned by the individual handlers — this
    // method only touches the steady-state "Paired" / "Not paired"
    // label so it doesn't stomp transient UI mid-pair.
    private void SyncHueBridgeUi()
    {
        var paired = HuePairingService.Instance.PairedBridge;
        var bridge = HuePairingService.Instance.Bridge;

        if (paired is null || bridge is null || !bridge.IsPaired)
        {
            HueBridgeStatusDot.Fill   = GetThemeBrush("SystemFillColorNeutralBrush");
            HueBridgeStatusText.Text  = Loc.Get("AmbientHue_Status_NotPaired");
            HuePairLabel.Text         = Loc.Get("AmbientHue_PairLabel_Pair");
            HueListGroupsButton.IsEnabled = false;
            HueForgetButton.IsEnabled     = false;

            _hueGroupComboSuppress = true;
            HueGroupComboBox.Items.Clear();
            HueGroupComboBox.IsEnabled = false;
            _hueGroupComboSuppress = false;
            return;
        }

        HueBridgeStatusDot.Fill   = GetThemeBrush("SystemFillColorSuccessBrush");
        HueBridgeStatusText.Text  = Loc.Get("AmbientHue_Status_Paired");
        HueBridgeIpTextBox.Text   = paired.InternalIpAddress;
        HuePairLabel.Text         = Loc.Get("AmbientHue_PairLabel_Repair");
        HueListGroupsButton.IsEnabled = true;
        HueForgetButton.IsEnabled     = true;
    }

    private static Brush GetThemeBrush(string resourceKey)
        => (Brush)Application.Current.Resources[resourceKey];

    // Set the transient pair status caption — auto-collapse the
    // surrounding TextBlock when empty so the row disappears entirely
    // (vs leaving a hollow caption slot below the address card).
    private void SetHuePairStatus(string text)
    {
        HuePairStatusText.Text = text;
        HuePairStatusText.Visibility = string.IsNullOrEmpty(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void OnHueDiscoverClick(object sender, RoutedEventArgs e)
    {
        HueDiscoverButton.IsEnabled = false;
        try
        {
            var bridges = await HuePairingService.Instance
                .DiscoverAsync()
                .ConfigureAwait(true);
            if (bridges.Count > 0)
            {
                HueBridgeIpTextBox.Text = bridges[0].InternalIpAddress;
            }
            // Empty bridges list : leave the textbox alone, user types
            // the IP manually (the LogWindow shows the verbose discovery
            // outcome). No status dot change.
        }
        finally
        {
            HueDiscoverButton.IsEnabled = true;
        }
    }

    private async void OnHuePairClick(object sender, RoutedEventArgs e)
    {
        if (_hueIsPairing) return;

        var ip = HueBridgeIpTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(ip))
        {
            SetHuePairStatus(Loc.Get("AmbientHue_PairStatus_IpRequired"));
            return;
        }

        try { _huePairCts?.Cancel(); } catch { /* best effort */ }
        _huePairCts?.Dispose();
        _huePairCts = new CancellationTokenSource();
        _hueIsPairing = true;

        HuePairButton.IsEnabled = false;
        HuePairLabel.Text       = Loc.Get("AmbientHue_PairLabel_Waiting");
        SetHuePairStatus(Loc.Get("AmbientHue_PairStatus_PressLink"));

        var target = new HueBridge(Id: "manual", InternalIpAddress: ip, Port: 443);
        try
        {
            await HuePairingService.Instance
                .PairAsync(target, ct: _huePairCts.Token)
                .ConfigureAwait(true);
            SetHuePairStatus(Loc.Get("AmbientHue_PairStatus_Success"));
            // SyncHueBridgeUi fires via BridgeChanged event and flips
            // the dot to success + label to Re-pair.
        }
        catch (OperationCanceledException)
        {
            SetHuePairStatus(Loc.Get("AmbientHue_PairStatus_Cancelled"));
        }
        catch (TimeoutException)
        {
            SetHuePairStatus(Loc.Get("AmbientHue_PairStatus_TimedOut"));
        }
        catch (Exception ex)
        {
            SetHuePairStatus(Loc.Format("AmbientHue_PairStatus_Failed_Format", ex.Message));
            LogService.Instance.Warning(LogSource.Hue,
                $"Pair from Settings failed — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _hueIsPairing = false;
            HuePairButton.IsEnabled = true;
        }
    }

    private async void OnHueListGroupsClick(object sender, RoutedEventArgs e)
    {
        if (!HuePairingService.Instance.IsPaired) return;

        HueListGroupsButton.IsEnabled = false;
        try
        {
            _hueGroups = await HuePairingService.Instance
                .ListGroupsAsync()
                .ConfigureAwait(true);

            _hueGroupComboSuppress = true;
            HueGroupComboBox.Items.Clear();
            foreach (var g in _hueGroups)
            {
                HueGroupComboBox.Items.Add(new ComboBoxItem
                {
                    Content = g.DisplayLabel,
                    Tag     = g,
                });
            }
            _hueGroupComboSuppress = false;

            HueGroupComboBox.IsEnabled = _hueGroups.Count > 0;
            if (_hueGroups.Count > 0)
            {
                string? lastId = AmbientSettingsService.Instance.Current.HueLastGroupId;
                int preselect = 0;
                if (!string.IsNullOrEmpty(lastId))
                {
                    for (int i = 0; i < _hueGroups.Count; i++)
                    {
                        if (_hueGroups[i].Id == lastId) { preselect = i; break; }
                    }
                }
                HueGroupComboBox.SelectedIndex = preselect;
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning(LogSource.Hue,
                $"Listing groups from Settings failed — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            HueListGroupsButton.IsEnabled = true;
        }
    }

    private void OnHueGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_hueGroupComboSuppress) return;
        if (HueGroupComboBox.SelectedItem is not ComboBoxItem { Tag: HueGroup group }) return;

        // Persist the chosen group so AmbientEngine.StartAsync finds it
        // on the next pipeline start. Symmetric with the Playground
        // OnHueGroupSelectionChanged handler.
        AmbientSettingsService.Instance.Current.HueLastGroupId = group.Id;
        AmbientSettingsService.Instance.Save();
    }

    private async void OnHueForgetClick(object sender, RoutedEventArgs e)
    {
        // Modal confirmation before clearing the pairing — matches
        // Microsoft's official guidance for destructive actions
        // (confirm in a ContentDialog rather than rely on the button
        // colour alone). Wording lives in Strings/en-US/Resources.resw
        // under the AmbientHue_ForgetDialog_* keys.
        var dialog = new ContentDialog
        {
            Title             = Loc.Get("AmbientHue_ForgetDialog_Title"),
            Content           = Loc.Get("AmbientHue_ForgetDialog_Content"),
            PrimaryButtonText = Loc.Get("AmbientHue_ForgetDialog_PrimaryButton"),
            CloseButtonText   = Loc.Get("AmbientHue_ForgetDialog_CloseButton"),
            DefaultButton     = ContentDialogButton.Close,
            XamlRoot          = this.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        HuePairingService.Instance.Forget();
        // SyncHueBridgeUi fires via BridgeChanged event ; we just clear
        // any transient pair status text so the row reads clean.
        SetHuePairStatus("");
        HueBridgeIpTextBox.Text = "";
    }

}
