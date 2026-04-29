using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WhispUI.Llm;
using WhispUI.Localization;
using WhispUI.Logging;

namespace WhispUI.Settings.Llm.GgufImport;

// ─── Wrapper ContentDialog autour de GgufImportView ────────────────────────
//
// API minimale pour l'appelant : une seule méthode statique ShowAsync qui
// instancie le dialogue, l'affiche, et retourne true si un modèle a été
// importé avec succès.
//
// Conçu pour être copiable tel quel dans une autre app WinUI 3 en ne traînant
// qu'une dépendance : OllamaService. La View et le Dialog sont autonomes
// (aucune référence à LlmPage, LlmSettings, SettingsService).

internal static class GgufImportDialog
{
    public static async Task<bool> ShowAsync(XamlRoot root, OllamaService service)
    {
        var view = new GgufImportView();

        bool importedOk = false;

        var dialog = new ContentDialog
        {
            Title = Loc.Get("Gguf_DialogTitle"),
            Content = view,
            PrimaryButtonText = Loc.Get("Gguf_PrimaryButton"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root
        };

        view.Initialize(service, dialog);

        dialog.PrimaryButtonClick += (sender, args) =>
        {
            // Cancel close immediately so the dialog stays open and the UI
            // thread remains free to render progress bar updates.
            args.Cancel = true;

            // Fire-and-forget: TryImportAsync handles its own errors via
            // ShowError. ContinueWith catches anything that slips past — e.g.
            // dialog.Hide() racing with a programmatic close, or a XAML
            // exception during teardown. Sans ce filet, l'exception irait
            // dans TaskScheduler.UnobservedTaskException et perdrait le
            // contexte — log explicite ici pour pouvoir diagnostiquer.
            _ = RunImportAsync().ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception is not null)
                {
                    var ex = t.Exception.GetBaseException();
                    LogService.Instance.Error(LogSource.SetLlm,
                        $"GGUF import task faulted: {ex.GetType().Name}: {ex.Message}");
                }
            }, TaskScheduler.Default);
        };

        async Task RunImportAsync()
        {
            bool ok = await view.TryImportAsync();
            if (ok)
            {
                importedOk = true;
                dialog.Hide();
            }
        }

        await dialog.ShowAsync();
        return importedOk;
    }
}
