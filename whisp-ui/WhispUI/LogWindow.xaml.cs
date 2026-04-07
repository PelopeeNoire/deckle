using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace WhispUI;

// ─── Fenêtre de logs en temps réel ───────────────────────────────────────────
//
// Fenêtre classique Windows : minimisable, maximisable, fermable.
// Le Close CANCEL + Hide — l'instance reste vivante car WhispEngine y branche
// ses events via App.OnLaunched ; la détruire briserait ces abonnements.
// Thème sombre forcé (convention dev tools, lisibilité uniforme).
//
// Thread-safety : Log/LogError marshalent via DispatcherQueue. Les brushes
// sont créés sur le thread UI (constructeur) — piège WinUI 3.

public sealed partial class LogWindow : Window
{
    private sealed record LogEntry(string Text, SolidColorBrush Color);

    private readonly ObservableCollection<LogEntry> _entries = new();
    private readonly IntPtr _hwnd;
    private bool _isVisible;

    // Couleurs hardcodées : le contenu force RequestedTheme=Dark (cf. XAML),
    // donc on a besoin de tons clairs pour le texte normal et rouge vif pour l'erreur.
    private readonly SolidColorBrush _normalBrush;
    private readonly SolidColorBrush _errorBrush;

    public LogWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        _normalBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xE6, 0xE6, 0xE6));
        _errorBrush  = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B));

        LogItems.ItemsSource  = _entries;
        LogItems.ItemTemplate = (DataTemplate)RootGrid.Resources["NoWrapTemplate"];

        Title = "Whisp — Logs";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 560));

        // Fenêtre classique : min, max, resize. Pas de chrome custom.
        var presenter = OverlappedPresenter.Create();
        presenter.IsMinimizable = true;
        presenter.IsMaximizable = true;
        presenter.IsResizable   = true;
        AppWindow.SetPresenter(presenter);

        // Close → cache, ne détruit pas. L'instance est réutilisée via le tray.
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            _isVisible = false;
            AppWindow.Hide();
        };
    }

    // ── API publique (thread-safe) ────────────────────────────────────────────

    public void Log(string message)      => AppendEntry(message, isError: false);
    public void LogError(string message) => AppendEntry(message, isError: true);

    public void Clear()
    {
        if (DispatcherQueue.HasThreadAccess) _entries.Clear();
        else DispatcherQueue.TryEnqueue(() => _entries.Clear());
    }

    // Ouverture depuis le tray : restore si minimisée, affiche, active.
    public void ShowAndActivate()
    {
        _isVisible = true;

        if (AppWindow.Presenter is OverlappedPresenter op &&
            op.State == OverlappedPresenterState.Minimized)
        {
            op.Restore();
        }

        AppWindow.Show();
        this.Activate();
    }

    // ── Implémentation ────────────────────────────────────────────────────────

    private void AppendEntry(string message, bool isError)
    {
        var entry = new LogEntry(message, isError ? _errorBrush : _normalBrush);
        if (DispatcherQueue.HasThreadAccess) AddEntrySafe(entry);
        else DispatcherQueue.TryEnqueue(() => AddEntrySafe(entry));
    }

    private void AddEntrySafe(LogEntry entry)
    {
        _entries.Add(entry);

        const int MaxEntries = 5000;
        while (_entries.Count > MaxEntries) _entries.RemoveAt(0);

        if (!_isVisible) return;
        if (AutoScrollToggle?.IsChecked != true) return;

        try
        {
            LogScrollViewer.UpdateLayout();
            LogScrollViewer.ScrollToVerticalOffset(LogScrollViewer.ScrollableHeight);
        }
        catch (Exception ex)
        {
            DebugLog.Write("LOGWIN", $"scroll err: {ex.Message}");
        }
    }

    // ── Handlers boutons ──────────────────────────────────────────────────────

    private void OnClearClick(object sender, RoutedEventArgs e) => _entries.Clear();

    private void OnWrapToggleClick(object sender, RoutedEventArgs e)
    {
        string key = WrapToggle.IsChecked == true ? "WrapTemplate" : "NoWrapTemplate";
        LogItems.ItemTemplate = (DataTemplate)RootGrid.Resources[key];
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var entry in _entries) sb.AppendLine(entry.Text);

        var dp = new DataPackage();
        dp.SetText(sb.ToString());
        Clipboard.SetContent(dp);
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker();
            // WinUI 3 unpackaged : le picker a besoin du HWND parent.
            InitializeWithWindow.Initialize(picker, _hwnd);
            picker.SuggestedFileName = $"whisp-logs-{DateTime.Now:yyyyMMdd-HHmmss}";
            picker.FileTypeChoices.Add("Texte", new List<string> { ".txt" });

            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null) return;

            var sb = new StringBuilder();
            foreach (var entry in _entries) sb.AppendLine(entry.Text);
            await FileIO.WriteTextAsync(file, sb.ToString());
        }
        catch (Exception ex)
        {
            DebugLog.Write("LOGWIN", $"save err: {ex.Message}");
        }
    }
}
