using System;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WhispUI.Llm;
using WhispUI.Settings.Llm.GgufImport;

namespace WhispUI.Settings.Llm;

// ─── Section Models de LlmPage ─────────────────────────────────────────────
//
// Liste les modèles locaux d'Ollama (via LlmOllamaContext) + boutons Import
// GGUF et Refresh. Remove par modèle avec ContentDialog de confirmation.
//
// Délégation de l'import vers GgufImportDialog (module autonome dans
// Settings/Llm/GgufImport/). Le host écoute RefreshRequested pour relancer
// RefreshOllamaStateAsync côté page après refresh manuel ou import réussi.
//
// Erreurs locales affichées dans l'ErrorBar de la section — la StatusBar
// globale de LlmPage ne sert qu'à l'état de disponibilité d'Ollama.

public sealed partial class LlmModelsSection : UserControl
{
    private LlmOllamaContext? _context;

    public event EventHandler? RefreshRequested;

    public LlmModelsSection()
    {
        InitializeComponent();
    }

    internal void Initialize(LlmOllamaContext context)
    {
        if (_context != null)
            _context.StateChanged -= OnContextStateChanged;

        _context = context;
        _context.StateChanged += OnContextStateChanged;
    }

    private void OnContextStateChanged(object? sender, EventArgs e) => Reload();

    public void Reload()
    {
        ModelsContainer.Children.Clear();

        bool enabled = _context?.Available ?? false;
        ImportGgufButton.IsEnabled = enabled;
        RefreshModelsButton.IsEnabled = enabled;

        if (!enabled) return;

        var models = _context?.Models ?? Array.Empty<OllamaModel>();

        foreach (var model in models)
        {
            string sizeText = model.Size > 0
                ? $"{model.Size / (1024.0 * 1024 * 1024):F1} GB"
                : "";

            var card = new SettingsCard
            {
                Header = model.Name,
                Description = sizeText
            };

            var delBtn = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new FontIcon { Glyph = "\uE74D", FontSize = 14 },
                        new TextBlock { Text = "Remove" }
                    }
                }
            };
            string modelName = model.Name;
            delBtn.Click += async (_, _) =>
            {
                var dialog = new ContentDialog
                {
                    Title = "Remove model",
                    Content = $"Remove \"{modelName}\" from Ollama? This cannot be undone.",
                    PrimaryButtonText = "Remove",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    try
                    {
                        if (_context?.Service != null)
                            await _context.Service.DeleteModelAsync(modelName);
                        RefreshRequested?.Invoke(this, EventArgs.Empty);
                    }
                    catch (Exception ex)
                    {
                        ErrorBar.Title = "Error removing model";
                        ErrorBar.Message = ex.Message;
                        ErrorBar.IsOpen = true;
                    }
                }
            };

            card.Content = delBtn;
            ModelsContainer.Children.Add(card);
        }

        if (models.Count == 0)
        {
            ModelsContainer.Children.Add(new TextBlock
            {
                Text = "No models found in Ollama. Pull a model or import a GGUF file.",
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Margin = new Thickness(1, 4, 0, 0)
            });
        }
    }

    private void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void ImportGguf_Click(object sender, RoutedEventArgs e)
    {
        if (_context?.Service == null || !_context.Available) return;

        bool imported = await GgufImportDialog.ShowAsync(this.XamlRoot, _context.Service);
        if (imported)
            RefreshRequested?.Invoke(this, EventArgs.Empty);
    }
}
