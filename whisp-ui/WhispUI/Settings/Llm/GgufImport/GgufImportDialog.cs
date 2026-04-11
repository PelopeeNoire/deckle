using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
        view.Initialize(service);

        bool importedOk = false;

        var dialog = new ContentDialog
        {
            Title = "Import GGUF model",
            Content = view,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            bool ok = await view.TryImportAsync();
            if (!ok)
                args.Cancel = true;
            else
                importedOk = true;
            deferral.Complete();
        };

        await dialog.ShowAsync();
        return importedOk;
    }
}
