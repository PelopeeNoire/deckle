using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using System.Collections.ObjectModel;
using System.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Data;
using WinRT.Interop;

namespace WhispUI;

// ─── Niveaux de log ───────────────────────────────────────────────────────────
// Step  : étapes importantes du workflow, marquées explicitement par le code
//         pour la vue Activity (ce qui s'est passé proprement).
// Errors (selector) = Warning + Error.
// LogWarning/LogStep sont exposés mais pas encore appelés par WhispEngine —
// l'API est prête pour la passe debug à venir.
// Verbose : bruit de fond (heartbeats, dumps par segment, plomberie clipboard…) —
//           visible uniquement en mode All.
// Info    : étapes du déroulé qu'on veut voir en Activity (lancements, codes
//           retour, textes, copies, paste).
// Step    : jalons rares et vérifiés (modèle chargé, bout en bout OK).
public enum LogLevel { Verbose, Info, Step, Warning, Error }

// Mode du SelectorBar — un seul actif à la fois (sélection exclusive native).
internal enum LogFilterMode { All, Steps, Critical }

// Selector par niveau — 5 templates par dimension wrap. Instancié deux fois
// dans les ressources XAML (NoWrapSelector / WrapSelector), swap au toggle.
public sealed class LogLevelTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Verbose { get; set; }
    public DataTemplate? Info    { get; set; }
    public DataTemplate? Step    { get; set; }
    public DataTemplate? Warning { get; set; }
    public DataTemplate? Error   { get; set; }

    protected override DataTemplate SelectTemplateCore(object item) => Pick(item);

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        => Pick(item);

    private DataTemplate Pick(object item)
    {
        if (item is LogWindow.LogEntry e)
        {
            return e.Level switch
            {
                LogLevel.Verbose => Verbose!,
                LogLevel.Info    => Info!,
                LogLevel.Step    => Step!,
                LogLevel.Warning => Warning!,
                LogLevel.Error   => Error!,
                _                => Info!,
            };
        }
        return Info!;
    }
}

// ─── Fenêtre de logs ──────────────────────────────────────────────────────────
//
// Title bar custom (ExtendsContentIntoTitleBar) avec champ de recherche centré.
// Mica + thème système (light/dark auto, pas de RequestedTheme forcé).
// SelectorBar All/Activity/Errors.
// CommandBar : Copy/Save/Clear (boutons) + Auto-scroll/Word wrap (toggles).
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
    internal sealed class LogEntry
    {
        public string Text { get; }
        public LogLevel Level { get; }

        public LogEntry(string text, LogLevel level)
        {
            Text = text;
            Level = level;
        }
    }

    private readonly List<LogEntry> _entries = new();
    private readonly ObservableCollection<LogEntry> _visible = new();
    private readonly IntPtr _hwnd;
    private bool _isVisible;

    // Icônes app — mêmes assets que TrayIconManager via IconAssets.
    // Source de vérité unique : changer un .ico le propage tray + beacon + window icon.
    private BitmapImage? _iconIdle;
    private BitmapImage? _iconRecording;
    private string? _iconIdlePath;
    private string? _iconRecordingPath;

    private ScrollViewer? _listScrollViewer;
    private ItemsStackPanel? _itemsPanel;

    private LogFilterMode _filterMode = LogFilterMode.All;
    private string _currentSearch = "";
    private bool _isRecording;

    public LogWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        LogItems.ItemsSource = _visible;

        // Shift+molette → scroll horizontal. Le ScrollViewer interne du ListView
        // marque PointerWheelChanged comme handled ; AddHandler avec
        // handledEventsToo=true pour intercepter quand même.
        LogItems.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnLogPointerWheel),
            handledEventsToo: true);

        // Clic-to-copy + drag-to-select : PointerPressed/Released sont marqués
        // handled par le ListView pour sa propre gestion de sélection, donc
        // AddHandler avec handledEventsToo=true.
        LogItems.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnLogPointerPressed),
            handledEventsToo: true);
        LogItems.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnLogPointerReleased),
            handledEventsToo: true);

        // Icônes app : résolues une fois, partagées avec le tray.
        LoadAppIcons();
        AppTitleBarIcon.ImageSource = _iconIdle;
        if (_iconIdlePath is not null) AppWindow.SetIcon(_iconIdlePath);

        // Title bar natif : ExtendsContentIntoTitleBar + SetTitleBar reste requis
        // pour que le contrôle TitleBar remplace la system title bar. Hauteur,
        // drag region, caption buttons themés sont gérés par le contrôle.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        // Caption buttons Tall pour rester alignés avec le contenu interactif
        // (SearchBox) du TitleBar. Le contrôle TitleBar gère la hauteur de son
        // propre chrome, mais les caption buttons système restent pilotés par
        // AppWindow.TitleBar.PreferredHeightOption.
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;

        // Mica : fond translucide qui prend les couleurs du thème système.
        // Win11 requis (OK ici) ; sinon fallback transparent.
        SystemBackdrop = new MicaBackdrop();

        // Sélection initiale du SelectorBar.
        LevelSelector.SelectedItem = LevelFull;

        Title = "Logs";
        // Format ~1:2 (vertical) — deux carrés empilés. Tient sur un écran 4K.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(960, 1440));

        // Fenêtre classique : min, max, resize.
        // Min size : empêche que la barre de commandes responsive soit
        // écrasée en dessous du seuil le plus serré (400 px = tout dans
        // le flyout More, search masquée).
        var presenter = OverlappedPresenter.Create();
        presenter.IsMinimizable = true;
        presenter.IsMaximizable = true;
        presenter.IsResizable   = true;
        presenter.PreferredMinimumWidth  = 400;
        presenter.PreferredMinimumHeight = 300;
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
        // Muter ImageSource in-place sur l'ImageIconSource existant ne propage
        // pas visuellement au TitleBar (pas de PropertyChanged routé). Fix :
        // reconstruire un ImageIconSource complet et réassigner IconSource.
        AppTitleBar.IconSource = new Microsoft.UI.Xaml.Controls.ImageIconSource
        {
            ImageSource = isRecording ? _iconRecording : _iconIdle,
        };

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
        _itemsPanel = null;
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

    private void AppendEntry(string message, LogLevel level)
    {
        var entry = new LogEntry(message, level);
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
        //   Steps    → Activity : Info + Step + Warning + Error (Verbose masqué)
        //   Critical → Errors : Warning + Error uniquement
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
        if (_visible.Count == 0) return;
        try
        {
            LogItems.ScrollIntoView(_visible[_visible.Count - 1]);
        }
        catch (Exception ex)
        {
            DebugLog.Write("LOGWIN", $"scroll err: {ex.Message}");
        }
    }

    // ── Handlers ──────────────────────────────────────────────────────────────

    private ScrollViewer? GetListViewScrollViewer()
    {
        // Le ScrollViewer interne du ListView n'est disponible qu'après le premier
        // layout. On le cherche dans l'arbre visuel et on le cache.
        if (_listScrollViewer is not null) return _listScrollViewer;
        _listScrollViewer = FindDescendant<ScrollViewer>(LogItems);
        return _listScrollViewer;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            var result = FindDescendant<T>(child);
            if (result is not null) return result;
        }
        return null;
    }

    private void OnLogPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        // Cacher le badge Copy pendant le scroll.
        CopyBadge.Visibility = Visibility.Collapsed;

        // Shift non pressé → laisser le ScrollViewer faire son scroll vertical natif.
        var mods = e.KeyModifiers;
        if ((mods & VirtualKeyModifiers.Shift) == 0) return;

        var sv = GetListViewScrollViewer();
        if (sv is null) return;

        // En mode wrap, le scroll horizontal est désactivé : rien à faire.
        if (sv.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled) return;

        var point = e.GetCurrentPoint(LogItems);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;

        // Convention Windows : 120 unités = un cran. On scrolle ~80 px par cran,
        // dans le sens inverse du delta (delta>0 = molette vers le haut = scroll vers la gauche).
        double offset = sv.HorizontalOffset - (delta / 120.0) * 80.0;
        if (offset < 0) offset = 0;
        if (offset > sv.ScrollableWidth) offset = sv.ScrollableWidth;

        sv.ChangeView(offset, null, null, disableAnimation: true);
        e.Handled = true;
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => ClearAll();

    private void OnWrapToggleClick(object sender, RoutedEventArgs e)
    {
        bool wrap = WrapToggle.IsChecked == true;
        string key = wrap ? "WrapSelector" : "NoWrapSelector";
        LogItems.ItemTemplateSelector = (DataTemplateSelector)RootGrid.Resources[key];

        // En mode wrap : couper le scroll horizontal pour que le ScrollViewer
        // donne une largeur finie à son contenu (sinon TextWrapping ne sait pas
        // où couper). Propriété attachée sur le ListView.
        ScrollViewer.SetHorizontalScrollBarVisibility(LogItems,
            wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);

        // Invalider le cache du ScrollViewer interne (le même objet, mais
        // sa visibility change).
        _listScrollViewer = null;
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

    // ── Clic-to-copy + drag-to-select + badge flottant ─────────────────────
    //
    // Hover : badge "Copy" à droite de la ligne survolée.
    // Clic simple (press+release) : copie 1 ligne, feedback "Copied".
    // Clic+drag : sélection visuelle (Extended) des lignes traversées,
    //   badge "Copy selection", copie au relâchement, déselection.

    private bool _isDragging;
    private int _dragStartIndex = -1;

    private void OnLogPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(LogItems).Properties.IsLeftButtonPressed) return;

        _isDragging = true;
        var localY = e.GetCurrentPoint(LogItems).Position.Y;
        var container = FindContainerAtY(localY);
        _dragStartIndex = container?.Content is LogEntry entry
            ? _visible.IndexOf(entry)
            : -1;
    }

    private void OnLogPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var localY = e.GetCurrentPoint(LogItems).Position.Y;
        var container = FindContainerAtY(localY);

        if (container is null)
        {
            if (!_isDragging) CopyBadge.Visibility = Visibility.Collapsed;
            return;
        }

        // Positionner le badge à droite de l'item sous le pointeur.
        var transform = container.TransformToVisual(LogSurface);
        var pos = transform.TransformPoint(default);
        CopyBadgeTransform.Y = pos.Y + (container.ActualHeight - CopyBadge.ActualHeight) / 2;
        CopyBadge.Visibility = Visibility.Visible;

        if (_isDragging && _dragStartIndex >= 0 && container.Content is LogEntry currentEntry)
        {
            int currentIndex = _visible.IndexOf(currentEntry);
            if (currentIndex >= 0)
            {
                int start = Math.Min(_dragStartIndex, currentIndex);
                int end = Math.Max(_dragStartIndex, currentIndex);

                // Sélection visuelle native via SelectRange (Extended mode).
                LogItems.DeselectRange(new ItemIndexRange(0, (uint)_visible.Count));
                LogItems.SelectRange(new ItemIndexRange(start, (uint)(end - start + 1)));

                CopyBadgeText.Text = (end > start) ? "Copy selection" : "Copy";
            }
        }
        else if (!_isDragging)
        {
            if (CopyBadgeText.Text != "Copied") CopyBadgeText.Text = "Copy";
        }
    }

    private void OnLogPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;

        // Copier toutes les lignes sélectionnées, dans l'ordre d'affichage.
        var selected = LogItems.SelectedItems;
        if (selected.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var item in _visible)
            {
                if (selected.Contains(item) && item is LogEntry entry)
                    sb.AppendLine(entry.Text);
            }
            CopyToClipboard(sb.ToString());
            ShowCopiedFeedback();
        }

        // Déselection complète — rien ne persiste.
        if (_visible.Count > 0)
            LogItems.DeselectRange(new ItemIndexRange(0, (uint)_visible.Count));
        _dragStartIndex = -1;
    }

    private void OnLogPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            CopyBadge.Visibility = Visibility.Collapsed;
        }
    }

    private ListViewItem? FindContainerAtY(double localY)
    {
        _itemsPanel ??= FindDescendant<ItemsStackPanel>(LogItems);
        if (_itemsPanel is null) return null;

        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(_itemsPanel);
        for (int i = 0; i < count; i++)
        {
            if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(_itemsPanel, i) is ListViewItem lvi)
            {
                var transform = lvi.TransformToVisual(LogItems);
                var pos = transform.TransformPoint(default);
                if (localY >= pos.Y && localY < pos.Y + lvi.ActualHeight)
                    return lvi;
            }
        }
        return null;
    }

    private static void CopyToClipboard(string text)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }

    private async void ShowCopiedFeedback()
    {
        CopyBadgeText.Text = "Copied";
        CopyBadge.Visibility = Visibility.Visible;
        await Task.Delay(800);
        if (CopyBadgeText.Text == "Copied")
            CopyBadgeText.Text = "Copy";
    }

    // ── Bouton Copy (CommandBar) ─────────────────────────────────────────────

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        // Copie toutes les entrées visibles.
        var sb = new StringBuilder();
        foreach (var entry in _visible) sb.AppendLine(entry.Text);
        CopyToClipboard(sb.ToString());
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker();
            // WinUI 3 unpackaged : le picker a besoin du HWND parent.
            InitializeWithWindow.Initialize(picker, _hwnd);
            picker.SuggestedFileName = $"whisp-logs-{DateTime.Now:yyyyMMdd-HHmmss}";
            picker.FileTypeChoices.Add("Text", new List<string> { ".txt" });

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
