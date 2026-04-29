using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WhispUI.Llm;
using WhispUI.Localization;

namespace WhispUI.Settings.Llm.GgufImport;

// ─── Vue du dialogue d'import GGUF ─────────────────────────────────────────
//
// UserControl contenant le formulaire d'import : nom, chemin GGUF, template
// optionnel, system prompt optionnel, barre de progression, zone d'erreur.
//
// Instancié par GgufImportDialog.ShowAsync qui l'injecte comme Content d'un
// ContentDialog. L'orchestration réelle (hash, push, create) est gérée par
// GgufImportController, séparé pour rester testable sans UI.

public sealed partial class GgufImportView : UserControl
{
    private OllamaService? _service;
    private ContentDialog? _dialog;
    private bool _importing;

    public GgufImportView()
    {
        InitializeComponent();
    }

    internal void Initialize(OllamaService service, ContentDialog dialog)
    {
        _service = service;
        _dialog = dialog;

        // Create disabled until required fields are filled.
        dialog.IsPrimaryButtonEnabled = false;
        NameBox.TextChanged += (_, _) => UpdateCreateEnabled();
        PathBox.TextChanged += (_, _) => UpdateCreateEnabled();

        // Prevent closing during import (ESC, Cancel, or any other path).
        dialog.Closing += (_, args) =>
        {
            if (_importing)
                args.Cancel = true;
        };
    }

    private void UpdateCreateEnabled()
    {
        if (_dialog != null && !_importing)
            _dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(NameBox.Text)
                                           && !string.IsNullOrWhiteSpace(PathBox.Text);
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        var hwnd = WindowNative.GetWindowHandle(App.SettingsWin!);
        InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add(".gguf");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        var file = await picker.PickSingleFileAsync();
        if (file != null)
            PathBox.Text = file.Path;
    }

    /// <summary>
    /// Valide les champs et lance l'import. Retourne true en cas de succès.
    /// Appelé depuis PrimaryButtonClick (sans deferral) pour que le thread UI
    /// reste libre de rendre la ProgressBar.
    /// </summary>
    internal async Task<bool> TryImportAsync()
    {
        if (_service == null) return false;

        string modelName = NameBox.Text.Trim();
        string ggufPath = PathBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(modelName) || string.IsNullOrWhiteSpace(ggufPath))
        {
            ShowError(Loc.Get("Gguf_Error_MissingFields_Title"), Loc.Get("Gguf_Error_MissingFields_Body"));
            return false;
        }

        if (!Regex.IsMatch(modelName, @"^[a-zA-Z0-9][a-zA-Z0-9._-]*(:[a-zA-Z0-9._-]+)?$"))
        {
            ShowError(Loc.Get("Gguf_Error_InvalidName_Title"), Loc.Get("Gguf_Error_InvalidName_Body"));
            return false;
        }

        if (!File.Exists(ggufPath))
        {
            ShowError(Loc.Get("Gguf_Error_FileNotFound_Title"), Loc.Format("Gguf_Error_FileNotFound_Body_Format", ggufPath));
            return false;
        }

        // Lock UI during import.
        _importing = true;
        if (_dialog != null)
            _dialog.IsPrimaryButtonEnabled = false;

        ProgressBarControl.IsIndeterminate = true;
        ProgressBarControl.Value = 0;
        ProgressBarControl.Visibility = Visibility.Visible;
        StatusText.Text = Loc.Get("Gguf_StatusPreparing");
        StatusText.Visibility = Visibility.Visible;
        ErrorBar.IsOpen = false;

        var progress = new Progress<(string Status, double Percent)>(p =>
        {
            StatusText.Text = p.Status;
            if (p.Percent >= 0)
            {
                ProgressBarControl.IsIndeterminate = false;
                ProgressBarControl.Value = p.Percent * 100;
            }
            else
            {
                ProgressBarControl.IsIndeterminate = true;
            }
        });

        try
        {
            string? tmpl = string.IsNullOrWhiteSpace(TemplateBox.Text) ? null : TemplateBox.Text;
            string? sys = string.IsNullOrWhiteSpace(SystemPromptBox.Text) ? null : SystemPromptBox.Text;

            await Task.Run(() => _service.ImportGgufAsync(modelName, ggufPath, tmpl, sys, progress));

            _importing = false;
            ProgressBarControl.Visibility = Visibility.Collapsed;
            StatusText.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (Exception ex)
        {
            _importing = false;
            ProgressBarControl.Visibility = Visibility.Collapsed;
            StatusText.Visibility = Visibility.Collapsed;
            ShowError(Loc.Get("Gguf_Error_ImportFailed_Title"), ex.Message);
            UpdateCreateEnabled();
            return false;
        }
    }

    private void ShowError(string title, string message)
    {
        ErrorBar.Title = title;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
