using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Deckle.Logging;

namespace Deckle.Settings;

// ── FolderPickerEditableCard ────────────────────────────────────────────────
//
// Editable variant of FolderPickerCard. Replaces the read-only TextBlock
// with a TextBox the user can type into, while keeping the Pick + Open
// buttons. Useful when the user might import a folder from another machine
// (e.g. a pre-populated Whisper models directory) — the typing path is
// faster than picking. Mirrors PowerToys AdvancedPaste model-path pattern.
//
// Exposes a RightContent slot for callers that need to attach extra
// controls inline (Whisper's per-setting Reset button is the canonical
// example; the slot keeps that affordance inside the card layout instead
// of breaking it out into a sibling element).
public sealed partial class FolderPickerEditableCard : UserControl
{
    private static readonly LogService _log = LogService.Instance;

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(
            nameof(Header), typeof(string), typeof(FolderPickerEditableCard),
            new PropertyMetadata(null));

    public string? Header
    {
        get => (string?)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(
            nameof(Description), typeof(string), typeof(FolderPickerEditableCard),
            new PropertyMetadata(null));

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public static readonly DependencyProperty HeaderIconProperty =
        DependencyProperty.Register(
            nameof(HeaderIcon), typeof(IconElement), typeof(FolderPickerEditableCard),
            new PropertyMetadata(null));

    public IconElement? HeaderIcon
    {
        get => (IconElement?)GetValue(HeaderIconProperty);
        set => SetValue(HeaderIconProperty, value);
    }

    public static readonly DependencyProperty PathProperty =
        DependencyProperty.Register(
            nameof(Path), typeof(string), typeof(FolderPickerEditableCard),
            new PropertyMetadata(string.Empty, OnPathChanged));

    public string Path
    {
        get => (string)GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    public static readonly DependencyProperty DefaultPathProperty =
        DependencyProperty.Register(
            nameof(DefaultPath), typeof(string), typeof(FolderPickerEditableCard),
            new PropertyMetadata(string.Empty));

    public string DefaultPath
    {
        get => (string)GetValue(DefaultPathProperty);
        set => SetValue(DefaultPathProperty, value);
    }

    public static readonly DependencyProperty RightContentProperty =
        DependencyProperty.Register(
            nameof(RightContent), typeof(UIElement), typeof(FolderPickerEditableCard),
            new PropertyMetadata(null));

    public UIElement? RightContent
    {
        get => (UIElement?)GetValue(RightContentProperty);
        set => SetValue(RightContentProperty, value);
    }

    public event EventHandler? PathChanged;

    public FolderPickerEditableCard()
    {
        InitializeComponent();
    }

    private static void OnPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FolderPickerEditableCard card)
            card.PathChanged?.Invoke(card, EventArgs.Empty);
    }

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
        }
        catch (Exception ex)
        {
            _log.Error(LogSource.SetGeneral,
                $"FolderPickerEditableCard pick failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

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
                $"FolderPickerEditableCard open failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
