using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace WhispUI;

// ─── Niveaux de log ───────────────────────────────────────────────────────────
// Step  : étapes importantes du workflow, marquées explicitement par le code
//         pour la vue Filtered (ce qui s'est passé proprement).
// Critical (selector) = Warning + Error.
// LogWarning/LogStep sont exposés mais pas encore appelés par WhispEngine —
// l'API est prête pour la passe debug à venir.
// Verbose : bruit de fond (heartbeats, dumps par segment, plomberie clipboard…) —
//           visible uniquement en mode Full.
// Info    : étapes du déroulé qu'on veut voir en Filtered (lancements, codes
//           retour, textes, copies, paste).
// Step    : jalons rares et vérifiés (modèle chargé, bout en bout OK).
public enum LogLevel { Verbose, Info, Step, Warning, Error }

// Mode du SelectorBar — un seul actif à la fois (sélection exclusive native).
internal enum LogFilterMode { All, Steps, Critical }

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

    private SolidColorBrush _verboseBrush = null!;
    private SolidColorBrush _infoBrush  = null!;
    private SolidColorBrush _stepBrush  = null!;
    private SolidColorBrush _warnBrush  = null!;
    private SolidColorBrush _errorBrush = null!;

    // Icônes app — mêmes assets que TrayIconManager via IconAssets.
    // Source de vérité unique : changer un .ico le propage tray + beacon + window icon.
    private BitmapImage? _iconIdle;
    private BitmapImage? _iconRecording;
    private string? _iconIdlePath;
    private string? _iconRecordingPath;

    private LogFilterMode _filterMode = LogFilterMode.All;
    private string _currentSearch = "";
    private bool _isRecording;

    public LogWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        // Brushes via theme resources Windows. Re-résolus à chaque theme switch
        // (cf. OnThemeChanged), avec rebuild des entrées existantes pour qu'elles
        // adoptent les nouvelles couleurs.
        RefreshBrushes();

        LogItems.ItemsSource  = _visible;
        // Wrap désactivé par défaut : on veut voir le déroulement ligne par ligne,
        // sans repli, pour mieux suivre l'ordonnancement. Le toggle XAML est déjà
        // IsChecked="False".
        LogItems.ItemTemplate = (DataTemplate)RootGrid.Resources["NoWrapTemplate"];
        LogScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;

        // Icônes app : résolues une fois, partagées avec le tray.
        LoadAppIcons();
        AppIconBeacon.Source = _iconIdle;
        if (_iconIdlePath is not null) AppWindow.SetIcon(_iconIdlePath);

        // Title bar custom : étend le contenu dans la zone titre.
        // Toute la grid AppTitleBar est marquée comme drag region (pour pouvoir
        // déplacer la fenêtre depuis n'importe quelle zone vide de la barre).
        // Le SearchBox au centre est ensuite explicitement marqué comme zone
        // passthrough via InputNonClientPointerSource (cf. SetupDragPassthrough),
        // sinon il est intercepté par la title bar et ne reçoit plus de clics.
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SetTitleBar(AppTitleBar);

        // Le passthrough doit être recalculé après chaque layout de la SearchBox
        // (taille variable selon la largeur de la fenêtre).
        SearchBox.SizeChanged += (_, _) => SetupDragPassthrough();
        SearchBox.Loaded      += (_, _) => SetupDragPassthrough();

        // Réactivité au theme switch système : re-résoudre les brushes
        // (theme resources sont snapshots à l'instant t, pas live), rebuild
        // les entrées existantes pour qu'elles adoptent les nouvelles couleurs,
        // et mettre à jour les couleurs des caption buttons système (qui ne
        // sont pas auto-themées quand on personnalise la title bar).
        RootGrid.ActualThemeChanged += (_, _) => OnThemeChanged();
        RefreshTitleBarButtonColors();

        // Mica : fond translucide qui prend les couleurs du thème système.
        // Win11 requis (OK ici) ; sinon fallback transparent.
        SystemBackdrop = new MicaBackdrop();

        // Sélection initiale du SelectorBar.
        LevelSelector.SelectedItem = LevelFull;

        Title = "WhispUI Logs";
        // Format ~1:2 (vertical) — deux carrés empilés. Tient sur un écran 4K.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(960, 1440));

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

    public void LogVerbose(string message) => AppendEntry(message, LogLevel.Verbose);
    public void Log(string message)        => AppendEntry(message, LogLevel.Info);
    public void LogStep(string message)    => AppendEntry(message, LogLevel.Step);
    public void LogWarning(string message) => AppendEntry(message, LogLevel.Warning);
    public void LogError(string message)   => AppendEntry(message, LogLevel.Error);

    public void Clear()
    {
        if (DispatcherQueue.HasThreadAccess) ClearAll();
        else DispatcherQueue.TryEnqueue(ClearAll);
    }

    // Beacon "icône d'app" (rouge enregistrement / gris idle). Appelé depuis
    // le StatusChanged de WhispEngine via App.xaml.cs. Thread-safe.
    public void SetRecordingState(bool isRecording)
    {
        if (DispatcherQueue.HasThreadAccess) ApplyRecordingState(isRecording);
        else DispatcherQueue.TryEnqueue(() => ApplyRecordingState(isRecording));
    }

    private void ApplyRecordingState(bool isRecording)
    {
        _isRecording = isRecording;
        AppIconBeacon.Source = isRecording ? _iconRecording : _iconIdle;

        // Icône de fenêtre (titlebar Windows + taskbar + alt-tab) : suit le
        // même état. AppWindow.SetIcon attend un chemin .ico sur disque.
        var path = isRecording ? _iconRecordingPath : _iconIdlePath;
        if (path is not null) AppWindow.SetIcon(path);
    }

    private void LoadAppIcons()
    {
        _iconIdlePath      = IconAssets.ResolvePath(recording: false);
        _iconRecordingPath = IconAssets.ResolvePath(recording: true);

        if (_iconIdlePath is not null)
            _iconIdle = new BitmapImage(new Uri(_iconIdlePath));
        else
            DebugLog.Write("LOGWIN", "icône idle introuvable");

        if (_iconRecordingPath is not null)
            _iconRecording = new BitmapImage(new Uri(_iconRecordingPath));
        else
            DebugLog.Write("LOGWIN", "icône recording introuvable");
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

    // ── Theme switch : re-résolution brushes + rebuild entries ───────────────

    private void RefreshBrushes()
    {
        var res = Application.Current.Resources;
        // Verbose = blanc primary (couleur par défaut) — bruit / durée de vie
        // (heartbeats, dumps par segment, plomberie clipboard). C'est le gros du
        // volume, masqué en Filtered.
        _verboseBrush = (SolidColorBrush)res["TextFillColorPrimaryBrush"];
        // Info = bleu sémantique Fluent (SystemFillColorAttention canonique).
        // On NE prend PAS le brush de la theme dictionary : chez certains users,
        // la chaîne de fallback la fait résoudre sur l'accent système (couleur
        // de personnalisation Windows), ce qui casse la sémantique. On force
        // donc les valeurs hex officielles, theme-aware via OnThemeChanged.
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        var infoColor = isDark
            ? Color.FromArgb(0xFF, 0x60, 0xCD, 0xFF)   // dark : #60CDFF
            : Color.FromArgb(0xFF, 0x00, 0x5F, 0xB7);  // light : #005FB7
        _infoBrush  = new SolidColorBrush(infoColor);
        // Step = vert succès — distinctif et indépendant de l'accent système
        // (Attention/Accent peut être gris chez l'utilisateur).
        _stepBrush  = (SolidColorBrush)res["SystemFillColorSuccessBrush"];
        _warnBrush  = (SolidColorBrush)res["SystemFillColorCautionBrush"];
        _errorBrush = (SolidColorBrush)res["SystemFillColorCriticalBrush"];
    }

    private void OnThemeChanged()
    {
        RefreshBrushes();

        // Reconstruit les entrées avec les nouvelles brushes. LogEntry est
        // immutable (record-like) donc on remplace les instances et on rejoue
        // ApplyFilter qui re-peuple _visible depuis _entries.
        var rebuilt = _entries
            .Select(e => new LogEntry(e.Text, e.Level, BrushForLevel(e.Level)))
            .ToList();
        _entries.Clear();
        _entries.AddRange(rebuilt);
        ApplyFilter();

        // L'icône beacon n'est pas thémée (asset .ico unique par état),
        // donc rien à faire dessus. Caption buttons → repilotés à la main.
        RefreshTitleBarButtonColors();
    }

    private void RefreshTitleBarButtonColors()
    {
        // Les caption buttons système (min/max/close) ne s'auto-thèment pas
        // quand on customise la title bar — il faut les piloter à la main.
        var tb = AppWindow.TitleBar;
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;

        var fg       = dark ? Colors.White : Colors.Black;
        var inactive = dark ? Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A)
                             : Color.FromArgb(0xFF, 0x60, 0x60, 0x60);
        var hoverBg  = dark ? Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
                             : Color.FromArgb(0x10, 0x00, 0x00, 0x00);
        var pressBg  = dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)
                             : Color.FromArgb(0x20, 0x00, 0x00, 0x00);

        tb.ButtonBackgroundColor         = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor         = fg;
        tb.ButtonInactiveForegroundColor = inactive;
        tb.ButtonHoverBackgroundColor    = hoverBg;
        tb.ButtonHoverForegroundColor    = fg;
        tb.ButtonPressedBackgroundColor  = pressBg;
        tb.ButtonPressedForegroundColor  = fg;
    }

    // ── Title bar : drag passthrough pour la SearchBox ───────────────────────

    private void SetupDragPassthrough()
    {
        if (SearchBox.ActualWidth <= 0 || SearchBox.ActualHeight <= 0) return;
        if (RootGrid.XamlRoot is null) return;

        var scale = RootGrid.XamlRoot.RasterizationScale;
        var transform = SearchBox.TransformToVisual(null);
        var bounds = transform.TransformBounds(
            new Windows.Foundation.Rect(0, 0, SearchBox.ActualWidth, SearchBox.ActualHeight));

        var rect = new Windows.Graphics.RectInt32(
            (int)(bounds.X * scale),
            (int)(bounds.Y * scale),
            (int)(bounds.Width * scale),
            (int)(bounds.Height * scale));

        try
        {
            var nonClient = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            nonClient.SetRegionRects(NonClientRegionKind.Passthrough, new[] { rect });
        }
        catch (Exception ex)
        {
            DebugLog.Write("LOGWIN", $"passthrough err: {ex.Message}");
        }
    }

    // ── Implémentation ────────────────────────────────────────────────────────

    private SolidColorBrush BrushForLevel(LogLevel level) => level switch
    {
        LogLevel.Error   => _errorBrush,
        LogLevel.Warning => _warnBrush,
        LogLevel.Step    => _stepBrush,
        LogLevel.Verbose => _verboseBrush,
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
        // Filtre niveau :
        //   All      → tout passe (y compris Verbose)
        //   Steps    → déroulé principal : Info + Step + Warning + Error (Verbose masqué)
        //   Critical → Warning + Error uniquement
        bool levelOk = _filterMode switch
        {
            LogFilterMode.All      => true,
            LogFilterMode.Steps    => e.Level != LogLevel.Verbose,
            LogFilterMode.Critical => e.Level == LogLevel.Warning || e.Level == LogLevel.Error,
            _                      => true,
        };
        if (!levelOk) return false;

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
        var sel = sender.SelectedItem;
        _filterMode = sel == LevelCritical ? LogFilterMode.Critical
                    : sel == LevelFiltered ? LogFilterMode.Steps
                    : LogFilterMode.All;
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
