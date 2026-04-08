using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace WhispUI;

// ─── Niveaux de log ───────────────────────────────────────────────────────────
// Filtered (selector) = Warning + Error. Info masqué.
// LogWarning() est exposé mais pas encore appelé par WhispEngine — l'API est
// prête pour la passe debug à venir.
public enum LogLevel { Info, Warning, Error }

// ─── Fenêtre de logs ──────────────────────────────────────────────────────────
//
// Title bar custom (ExtendsContentIntoTitleBar) avec champ de recherche centré.
// Mica + thème système (light/dark auto, pas de RequestedTheme forcé).
// SelectorBar Full/Filtered.
// CommandBar : Copy/Save/Clear (boutons) + Auto scroll/Wrap (toggles).
// Recherche live via AutoSuggestBox.
//
// Modèle :
//   _entries : tampon complet (cap 5000)
//   _visible : sous-ensemble affiché (filtre niveau + recherche)
// Copy/Save opèrent sur _visible — l'utilisateur copie ce qu'il voit.
//
// Brushes : résolus depuis les theme resources Windows une fois en constructeur.
// Si l'utilisateur change le thème système pendant que la fenêtre est ouverte,
// les anciennes entrées gardent leurs brushes — acceptable pour du debug.

public sealed partial class LogWindow : Window
{
    private sealed class LogEntry
    {
        public string Text { get; }
        public LogLevel Level { get; }
        public SolidColorBrush Color { get; }

        public LogEntry(string text, LogLevel level, SolidColorBrush color)
        {
            Text = text;
            Level = level;
            Color = color;
        }
    }

    private readonly List<LogEntry> _entries = new();
    private readonly ObservableCollection<LogEntry> _visible = new();
    private readonly IntPtr _hwnd;
    private bool _isVisible;

    private SolidColorBrush _infoBrush  = null!;
    private SolidColorBrush _warnBrush  = null!;
    private SolidColorBrush _errorBrush = null!;

    private bool _showOnlyAlerts;
    private string _currentSearch = "";

    public LogWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        // Brushes via theme resources Windows : adaptent automatiquement
        // light/dark au moment de l'instanciation. Snapshot constructeur,
        // pas de re-binding sur theme switch (debug window, acceptable).
        _infoBrush  = (SolidColorBrush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        _warnBrush  = (SolidColorBrush)Application.Current.Resources["SystemFillColorCautionBrush"];
        _errorBrush = (SolidColorBrush)Application.Current.Resources["SystemFillColorCriticalBrush"];

        LogItems.ItemsSource  = _visible;
        LogItems.ItemTemplate = (DataTemplate)RootGrid.Resources["NoWrapTemplate"];

        // Title bar custom : étend le contenu dans la zone titre.
        // Drag region limitée au groupe icône+titre à gauche pour que le
        // SearchBox au centre reçoive normalement les clics.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBarLeftDrag);

        // Mica : fond translucide qui prend les couleurs du thème système.
        // Win11 requis (OK ici) ; sinon fallback transparent.
        SystemBackdrop = new MicaBackdrop();

        // Sélection initiale du SelectorBar.
        LevelSelector.SelectedItem = LevelFull;

        Title = "WhispUI — Logs";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 560));

        // Fenêtre classique : min, max, resize.
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

    public void Log(string message)        => AppendEntry(message, LogLevel.Info);
    public void LogWarning(string message) => AppendEntry(message, LogLevel.Warning);
    public void LogError(string message)   => AppendEntry(message, LogLevel.Error);

    public void Clear()
    {
        if (DispatcherQueue.HasThreadAccess) ClearAll();
        else DispatcherQueue.TryEnqueue(ClearAll);
    }

    private void ClearAll()
    {
        _entries.Clear();
        _visible.Clear();
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

        // WinUI 3 : Activate() ne ramène pas toujours la fenêtre au premier
        // plan quand l'appel vient d'un callback tray. SetForegroundWindow
        // depuis le même process est autorisé (l'AnchorWindow détient déjà
        // le foreground).
        NativeMethods.SetForegroundWindow(_hwnd);
    }

    // ── Implémentation ────────────────────────────────────────────────────────

    private SolidColorBrush BrushForLevel(LogLevel level) => level switch
    {
        LogLevel.Error   => _errorBrush,
        LogLevel.Warning => _warnBrush,
        _                => _infoBrush,
    };

    private void AppendEntry(string message, LogLevel level)
    {
        var entry = new LogEntry(message, level, BrushForLevel(level));
        if (DispatcherQueue.HasThreadAccess) AddEntrySafe(entry);
        else DispatcherQueue.TryEnqueue(() => AddEntrySafe(entry));
    }

    private void AddEntrySafe(LogEntry entry)
    {
        _entries.Add(entry);

        const int MaxEntries = 5000;
        while (_entries.Count > MaxEntries)
        {
            var removed = _entries[0];
            _entries.RemoveAt(0);
            // Ref equality (LogEntry est une classe) → pas de collision possible
            // sur deux entrées au même texte.
            _visible.Remove(removed);
        }

        if (Matches(entry)) _visible.Add(entry);

        if (!_isVisible) return;
        if (AutoScrollToggle?.IsChecked != true) return;

        ScrollToBottom();
    }

    private bool Matches(LogEntry e)
    {
        if (_showOnlyAlerts && e.Level == LogLevel.Info) return false;
        if (_currentSearch.Length > 0 &&
            e.Text.IndexOf(_currentSearch, StringComparison.OrdinalIgnoreCase) < 0) return false;
        return true;
    }

    private void ApplyFilter()
    {
        _visible.Clear();
        foreach (var e in _entries)
        {
            if (Matches(e)) _visible.Add(e);
        }
        if (_isVisible && AutoScrollToggle?.IsChecked == true) ScrollToBottom();
    }

    private void ScrollToBottom()
    {
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

    // ── Handlers ──────────────────────────────────────────────────────────────

    private void OnClearClick(object sender, RoutedEventArgs e) => ClearAll();

    private void OnWrapToggleClick(object sender, RoutedEventArgs e)
    {
        bool wrap = WrapToggle.IsChecked == true;
        string key = wrap ? "WrapTemplate" : "NoWrapTemplate";
        LogItems.ItemTemplate = (DataTemplate)RootGrid.Resources[key];

        // En mode wrap : couper le scroll horizontal pour que le ScrollViewer
        // donne une largeur finie à son contenu (sinon TextWrapping ne sait pas
        // où couper, le ScrollViewer mesure son contenu en largeur infinie).
        LogScrollViewer.HorizontalScrollBarVisibility =
            wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    private void OnLevelSelectorChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _showOnlyAlerts = sender.SelectedItem == LevelFiltered;
        ApplyFilter();
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _currentSearch = sender.Text ?? "";
        ApplyFilter();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var entry in _visible) sb.AppendLine(entry.Text);

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
            foreach (var entry in _visible) sb.AppendLine(entry.Text);
            await FileIO.WriteTextAsync(file, sb.ToString());
        }
        catch (Exception ex)
        {
            DebugLog.Write("LOGWIN", $"save err: {ex.Message}");
        }
    }
}
