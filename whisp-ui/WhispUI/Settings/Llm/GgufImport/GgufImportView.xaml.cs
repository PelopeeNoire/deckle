using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

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

    public GgufImportView()
    {
        InitializeComponent();
    }

    internal void Initialize(OllamaService service)
    {
        _service = service;
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
    /// Valide les champs et lance l'import. Retourne true en cas de succès,
    /// false si la validation échoue ou si l'import échoue.
    /// Appelé par GgufImportDialog.PrimaryButtonClick avec un deferral.
    /// </summary>
    public async Task<bool> TryImportAsync()
    {
        if (_service == null) return false;

        string modelName = NameBox.Text.Trim();
        string ggufPath = PathBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(modelName) || string.IsNullOrWhiteSpace(ggufPath))
        {
            ShowError("Missing fields", "Model name and GGUF file path are required.");
            return false;
        }

        if (!Regex.IsMatch(modelName, @"^[a-zA-Z0-9][a-zA-Z0-9._-]*(:[a-zA-Z0-9._-]+)?$"))
        {
            ShowError("Invalid model name",
                "Use only letters, digits, dots, dashes, and underscores. "
                + "Optional :tag suffix (e.g. my-model:latest).");
            return false;
        }

        if (!File.Exists(ggufPath))
        {
            ShowError("File not found", $"GGUF file not found: {ggufPath}");
            return false;
        }

        // Show progress — indéterminée + texte de préparation dès l'affichage,
        // avant même que le premier Report() ne tombe. Évite l'aspect "barre
        // vide / séparateur" au démarrage du hash.
        ProgressBarControl.IsIndeterminate = true;
        ProgressBarControl.Value = 0;
        ProgressBarControl.Visibility = Visibility.Visible;
        StatusText.Text = "Preparing...";
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

            await _service.ImportGgufAsync(modelName, ggufPath, tmpl, sys, progress);

            ProgressBarControl.Visibility = Visibility.Collapsed;
            StatusText.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (Exception ex)
        {
            ProgressBarControl.Visibility = Visibility.Collapsed;
            StatusText.Visibility = Visibility.Collapsed;
            ShowError("Import failed", ex.Message);
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
