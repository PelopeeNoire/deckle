using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Deckle.Lighting;
using Deckle.Lighting.Ambient;
using Deckle.Lighting.Hue;
using Deckle.Logging;

namespace Deckle.Playground;

// ─── AmbientPage — Hue REST handlers (J2) ───────────────────────────────────
//
// Pair / list groups / test colours / colour rotation. All the network-
// touching work runs through HuePairingService.Instance ; the page
// only owns the local cancellation tokens (so a window close cancels
// in-flight pair / rotation) and the per-session HueRestLightOutput
// pinned to the selected group.

public sealed partial class AmbientPage
{
    // Sync the Hue pairing row visuals from HuePairingService state.
    // The service auto-restores its bridge at first access, so by the
    // time the page reaches us here the bridge is either paired-from-
    // settings or absent. No network call here — first REST request
    // happens when the user clicks List groups or selects a group.
    private void SyncHueUiFromService()
    {
        var paired = HuePairingService.Instance.PairedBridge;
        var bridge = HuePairingService.Instance.Bridge;

        if (paired is null || bridge is null || !bridge.IsPaired)
        {
            HueBridgeIpTextBox.Text = string.Empty;
            HuePairLabel.Text       = "Pair (press link button)";
            HuePairStatusText.Text  = "Not paired";
            HuePairStatusDot.Fill   = GetThemeBrush("SystemFillColorNeutralBrush");
            HueListGroupsButton.IsEnabled = false;
            return;
        }

        var creds = bridge.Credentials!;
        HueBridgeIpTextBox.Text = paired.InternalIpAddress;
        HuePairLabel.Text       = "Re-pair";
        HuePairStatusText.Text  = $"Paired ({creds.UsernameHead}, saved)";
        HuePairStatusDot.Fill   = GetThemeBrush("SystemFillColorSuccessBrush");
        HueListGroupsButton.IsEnabled = true;

        // Auto-populate the groups combo on page open / bridge change.
        if (HueGroupComboBox.Items.Count == 0)
        {
            _ = RefreshHueGroupsAsync();
        }
    }

    private async void OnHueDiscoverClick(object sender, RoutedEventArgs e)
    {
        HueDiscoverButton.IsEnabled = false;
        try
        {
            var bridges = await HuePairingService.Instance
                .DiscoverAsync()
                .ConfigureAwait(true);
            if (bridges.Count == 0) return;

            HueBridgeIpTextBox.Text = bridges[0].InternalIpAddress;
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
            HuePairStatusText.Text = "Bridge IP required";
            HuePairStatusDot.Fill  = GetThemeBrush("SystemFillColorCautionBrush");
            return;
        }

        try { _huePairCts?.Cancel(); } catch { /* best effort */ }
        _huePairCts?.Dispose();
        _huePairCts = new CancellationTokenSource();
        _hueIsPairing = true;

        HuePairButton.IsEnabled = false;
        HuePairLabel.Text       = "Waiting for link button…";
        HuePairStatusText.Text  = "Waiting (30 s)";
        HuePairStatusDot.Fill   = GetThemeBrush("SystemFillColorCautionBrush");

        var target = new HueBridge(Id: "manual", InternalIpAddress: ip, Port: 443);
        try
        {
            var creds = await HuePairingService.Instance
                .PairAsync(target, ct: _huePairCts.Token)
                .ConfigureAwait(true);

            HuePairStatusText.Text = $"Paired ({creds.UsernameHead})";
            HuePairStatusDot.Fill  = GetThemeBrush("SystemFillColorSuccessBrush");
            HuePairLabel.Text      = "Re-pair";
            HueListGroupsButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            HuePairStatusText.Text = "Cancelled";
            HuePairStatusDot.Fill  = GetThemeBrush("SystemFillColorNeutralBrush");
            HuePairLabel.Text      = HuePairingService.Instance.IsPaired ? "Re-pair" : "Pair (press link button)";
        }
        catch (TimeoutException)
        {
            HuePairStatusText.Text = "Timed out — try again";
            HuePairStatusDot.Fill  = GetThemeBrush("SystemFillColorCriticalBrush");
            HuePairLabel.Text      = HuePairingService.Instance.IsPaired ? "Re-pair" : "Pair (press link button)";
        }
        catch (Exception ex)
        {
            HuePairStatusText.Text = $"Failed: {ex.Message}";
            HuePairStatusDot.Fill  = GetThemeBrush("SystemFillColorCriticalBrush");
            HuePairLabel.Text      = HuePairingService.Instance.IsPaired ? "Re-pair" : "Pair (press link button)";
        }
        finally
        {
            _hueIsPairing = false;
            HuePairButton.IsEnabled = true;
        }
    }

    private async void OnHueListGroupsClick(object sender, RoutedEventArgs e)
    {
        await RefreshHueGroupsAsync().ConfigureAwait(true);
    }

    private async Task RefreshHueGroupsAsync()
    {
        if (!HuePairingService.Instance.IsPaired) return;
        if (_hueGroupsFetchInFlight) return;

        _hueGroupsFetchInFlight = true;
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
                int preselectIndex = 0;
                if (!string.IsNullOrEmpty(lastId))
                {
                    for (int i = 0; i < _hueGroups.Count; i++)
                    {
                        if (_hueGroups[i].Id == lastId)
                        {
                            preselectIndex = i;
                            break;
                        }
                    }
                }
                HueGroupComboBox.SelectedIndex = preselectIndex;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(LogSource.Hue,
                $"Listing groups failed — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            HueListGroupsButton.IsEnabled = true;
            _hueGroupsFetchInFlight = false;
        }
    }

    private async void OnHueGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_hueGroupComboSuppress) return;
        var bridge = HuePairingService.Instance.Bridge;
        if (bridge is not { IsPaired: true }) return;

        CancelHueRotationIfRunning();
        if (_hueLightOutput is not null)
        {
            await _hueLightOutput.DisposeAsync().ConfigureAwait(true);
            _hueLightOutput = null;
        }

        _placementLights = null;
        _suggestedZones  = null;
        ClearLightZonesUi();

        if (HueGroupComboBox.SelectedItem is not ComboBoxItem { Tag: HueGroup group })
        {
            SetHueColorButtonsEnabled(false);
            return;
        }

        _hueLightOutput = new HueRestLightOutput(bridge, group.Id);
        try
        {
            await _hueLightOutput.ConnectAsync().ConfigureAwait(true);
            SetHueColorButtonsEnabled(true);
            SetPipelineReady();

            AmbientSettingsService.Instance.Current.HueLastGroupId = group.Id;
            AmbientSettingsService.Instance.Save();

            await ResolveLightsAndBuildPlacementAsync(group).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Warning(LogSource.Hue,
                $"Selecting group failed — {ex.GetType().Name}: {ex.Message}");
            SetHueColorButtonsEnabled(false);
            SetPipelineNotReady();
        }
    }

    private async void OnHueTestColorClick(object sender, RoutedEventArgs e)
    {
        if (_hueLightOutput is null) return;
        if (sender is not Button { Tag: string tag }) return;

        var color = tag switch
        {
            "red"   => LightColor.Red,
            "green" => LightColor.Green,
            "blue"  => LightColor.Blue,
            "white" => LightColor.White,
            "off"   => LightColor.Black,
            _       => LightColor.Black,
        };

        try
        {
            await _hueLightOutput.SetColorAsync(color).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Warning(LogSource.Hue,
                $"Push colour failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async void OnHueTestRotationClick(object sender, RoutedEventArgs e)
    {
        if (_hueLightOutput is null) return;

        CancelHueRotationIfRunning();
        _hueRotationCts = new CancellationTokenSource();
        var ct = _hueRotationCts.Token;

        HueTestRotationButton.IsEnabled = false;
        try
        {
            LightColor[] sequence =
            [
                LightColor.Red,
                LightColor.Green,
                LightColor.Blue,
                LightColor.White,
                LightColor.Black,
            ];

            foreach (var color in sequence)
            {
                await _hueLightOutput.SetColorAsync(color, ct).ConfigureAwait(true);
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _log.Warning(LogSource.Hue,
                $"Test rotation failed — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            HueTestRotationButton.IsEnabled = true;
        }
    }

    private void CancelHueRotationIfRunning()
    {
        try { _hueRotationCts?.Cancel(); } catch { /* best effort */ }
        _hueRotationCts?.Dispose();
        _hueRotationCts = null;
    }

    private void SetHueColorButtonsEnabled(bool enabled)
    {
        foreach (var child in HueColorButtonsPanel.Children)
        {
            if (child is Control c) c.IsEnabled = enabled;
        }
    }

    private void TeardownHueIfActive()
    {
        // The canonical bridge is owned by HuePairingService — both
        // the App-side AmbientEngine and the Settings AmbientPage
        // point at the same instance. Tearing it down here would
        // kill ambient lighting for the whole process — wrong.
        // Forget is an explicit user action (Settings → Forget
        // bridge), not a side-effect of closing a debug window.
        if (_screenCapture is not null)
            _screenCapture.FrameArrived -= OnSamplerFrameArrived;
        if (_frameSampler is not null)
        {
            _frameSampler.DisposeAsync().AsTask();
            _frameSampler = null;
        }
        if (_pipelineStartedCapture)
        {
            try { StopScreenCaptureIfRunning(); } catch { /* best effort */ }
            _pipelineStartedCapture = false;
        }

        try { _huePairCts?.Cancel(); } catch { /* best effort */ }
        _huePairCts?.Dispose();
        _huePairCts = null;

        CancelHueRotationIfRunning();

        _hueLightOutput?.DisposeAsync().AsTask();
        _hueLightOutput = null;
        _hueGroups = [];

        if (HueListGroupsButton is not null)
        {
            _hueGroupComboSuppress = true;
            HueGroupComboBox.Items.Clear();
            HueGroupComboBox.IsEnabled = false;
            _hueGroupComboSuppress = false;
            SetHueColorButtonsEnabled(false);
        }

        SetPipelineNotReady();
    }
}
