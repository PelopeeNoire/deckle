using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Deckle.Logging;

namespace Deckle.Settings;

// ── FolderPickerCard ────────────────────────────────────────────────────────
//
// Read-only display of a folder path with two actions: Pick (FolderPicker)
// and Open (Explorer). Models PowerToys' General → Settings Backup pattern.
// Path is exposed as a TwoWay DependencyProperty so callers can bind it to
// a ViewModel property and let auto-save handle persistence.
//
// When Path is empty, the TextBlock falls back to DefaultPath in the same
// secondary styling — same UX as the legacy TextBox PlaceholderText, but
// without the misleading affordance of an editable input field.
public sealed partial class FolderPickerCard : UserControl
{
    private static readonly LogService _log = LogService.Instance;

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(
            nameof(Header), typeof(string), typeof(FolderPickerCard),
            new PropertyMetadata(null));

    public string? Header
    {
        get => (string?)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(
            nameof(Description), typeof(string), typeof(FolderPickerCard),
            new PropertyMetadata(null));

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public static readonly DependencyProperty HeaderIconProperty =
        DependencyProperty.Register(
            nameof(HeaderIcon), typeof(IconElement), typeof(FolderPickerCard),
            new PropertyMetadata(null));

    public IconElement? HeaderIcon
    {
        get => (IconElement?)GetValue(HeaderIconProperty);
        set => SetValue(HeaderIconProperty, value);
    }

    public static readonly DependencyProperty PathProperty =
        DependencyProperty.Register(
            nameof(Path), typeof(string), typeof(FolderPickerCard),
            new PropertyMetadata(string.Empty, OnPathOrDefaultChanged));

    public string Path
    {
        get => (string)GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    public static readonly DependencyProperty DefaultPathProperty =
        DependencyProperty.Register(
            nameof(DefaultPath), typeof(string), typeof(FolderPickerCard),
            new PropertyMetadata(string.Empty, OnPathOrDefaultChanged));

    public string DefaultPath
    {
        get => (string)GetValue(DefaultPathProperty);
        set => SetValue(DefaultPathProperty, value);
    }

    public event EventHandler? PathChanged;

    public FolderPickerCard()
    {
        InitializeComponent();
        RefreshDisplay();
    }

    private static void OnPathOrDefaultChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FolderPickerCard card) card.RefreshDisplay();
    }

    // The TextBlock displays Path when set, otherwise falls back to
    // DefaultPath in the same secondary brush. We tweak Opacity so the
    // fallback reads as a placeholder rather than a real value.
    private void RefreshDisplay()
    {
        string effective = string.IsNullOrEmpty(Path) ? (DefaultPath ?? string.Empty) : Path;
        PathTextBlock.Text = effective;
        PathTextBlock.Opacity = string.IsNullOrEmpty(Path) ? 0.6 : 1.0;
        PathTooltip.Content = effective;
    }

    // FolderPicker (WindowsAppSDK 1.7+ namespace Microsoft.Windows.Storage.Pickers).
    // Takes the AppWindow.Id of the parent Settings window via SettingsHost,
    // avoiding the legacy WinRT.Interop.InitializeWithWindow dance and the
    // elevation breakage that comes with the UWP-heritage picker.
    private async void PickButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = SettingsHost.GetSettingsWindow?.Invoke()
                ?? throw new InvalidOperationException("Settings window not initialized");

            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(window.AppWindow.Id)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };

            var result = await picker.PickSingleFolderAsync();
            if (result is null) return;

            Path = result.Path;
            PathChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Error(LogSource.SetGeneral,
                $"FolderPickerCard pick failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Open the effective path in Explorer. We open the fallback DefaultPath
    // when Path is empty — that's the actual location data lands in (e.g.
    // <UserDataRoot>\telemetry\), not a placeholder.
    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        string target = string.IsNullOrEmpty(Path) ? (DefaultPath ?? string.Empty) : Path;
        if (string.IsNullOrEmpty(target)) return;

        try
        {
            Directory.CreateDirectory(target);
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _log.Error(LogSource.SetGeneral,
                $"FolderPickerCard open failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
