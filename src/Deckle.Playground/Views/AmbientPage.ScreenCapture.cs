using System;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Deckle.Lighting.Ambient;
using Deckle.Logging;
using Deckle.Vision;

namespace Deckle.Playground;

// ─── AmbientPage — screen capture (J1) ──────────────────────────────────────
//
// Local Screen capture toggle exposed in the Ambient page's Screen
// capture card. Distinct from the canonical capture owned by the App-
// side AmbientEngine — Louis can flip this on to sample frames + FPS
// counter without engaging the full Hue pipeline. The FPS counter
// ticks once a second, sampling FrameCount on the UI thread (no
// per-frame DispatcherQueue marshalling).
//
// Pipeline lifecycle is independent : the canonical AmbientEngine
// drives Hue through its own ScreenCaptureService when running, and
// this local instance only matters for isolated sampler tests.

public sealed partial class AmbientPage
{
    private void OnScreenCaptureToggleClick(object sender, RoutedEventArgs e)
    {
        _log.Verbose(LogSource.Screen,
            $"playground toggle | running={_screenCapture is { IsRunning: true }}");

        if (_screenCapture is { IsRunning: true })
        {
            StopScreenCaptureIfRunning();
            return;
        }

        StartScreenCaptureService();
    }

    private bool StartScreenCaptureService()
    {
        if (_screenCapture is { IsRunning: true }) return true;

        try
        {
            _screenCapture ??= new ScreenCaptureService();
            _screenCapture.Stopped += OnScreenCaptureStopped;
            var targetMonitor = AmbientSettingsService.Instance.Current.SelectedMonitorDeviceName;
            _screenCapture.Start(targetMonitor);

            _screenCaptureLastSampledFrames = 0;
            _screenCaptureLastSampleTimestamp = Stopwatch.GetTimestamp();
            ScreenCaptureToggleIcon.Glyph = ScreenCaptureGlyphStop;
            ScreenCaptureToggleLabel.Text = "Stop";
            ScreenCaptureStatusText.Text = "Running";
            ScreenCaptureStatusDot.Fill = GetThemeBrush("SystemFillColorSuccessBrush");
            ScreenCaptureFramesText.Text = "0";
            ScreenCaptureFpsText.Text = "—";
            _screenCaptureFpsTimer.Start();
            // Now that a live source is up, the preview timer needs
            // to tick to pump samples onto the cells.
            EvaluatePreviewTimerState();
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(LogSource.Screen,
                $"Playground toggle aborted — {ex.GetType().Name}: {ex.Message}");
            ScreenCaptureStatusText.Text = $"Failed: {ex.Message}";
            ScreenCaptureStatusDot.Fill = GetThemeBrush("SystemFillColorCriticalBrush");
            StopScreenCaptureIfRunning();
            return false;
        }
    }

    private void StopScreenCaptureIfRunning()
    {
        _screenCaptureFpsTimer.Stop();

        if (_screenCapture is null) return;

        _screenCapture.Stopped -= OnScreenCaptureStopped;
        _screenCapture.Dispose();
        _screenCapture = null;

        if (ScreenCaptureToggleButton is not null)
        {
            ScreenCaptureToggleIcon.Glyph = ScreenCaptureGlyphStart;
            ScreenCaptureToggleLabel.Text = "Start";
            ScreenCaptureStatusText.Text = "Stopped";
            ScreenCaptureStatusDot.Fill = GetThemeBrush("SystemFillColorNeutralBrush");
        }
        // Live source gone — re-evaluate the preview timer so the
        // page goes idle (timer stopped, cells blanked) when the
        // canonical AmbientEngine isn't also running.
        EvaluatePreviewTimerState();
    }

    private void OnScreenCaptureStopped()
    {
        // Fires on the capture service's worker thread when the loop
        // exits unexpectedly (sustained ACCESS_LOST recreate failure —
        // display disconnected, signed-out, etc.). Marshal back to the
        // UI thread to update the button + status, then run the same
        // teardown as a user-driven Stop.
        DispatcherQueue.TryEnqueue(() => StopScreenCaptureIfRunning());
    }

    private void OnScreenCaptureFpsTick(object? sender, object e)
    {
        if (_screenCapture is not { IsRunning: true }) return;

        long currentFrames = _screenCapture.FrameCount;
        long currentTimestamp = Stopwatch.GetTimestamp();
        long deltaFrames = currentFrames - _screenCaptureLastSampledFrames;
        long deltaMs = (currentTimestamp - _screenCaptureLastSampleTimestamp) * 1000 / Stopwatch.Frequency;

        double fps = deltaMs > 0 ? deltaFrames * 1000.0 / deltaMs : 0.0;

        ScreenCaptureFramesText.Text = currentFrames.ToString();
        ScreenCaptureFpsText.Text = $"{fps:F1}";

        _screenCaptureLastSampledFrames = currentFrames;
        _screenCaptureLastSampleTimestamp = currentTimestamp;
    }
}
