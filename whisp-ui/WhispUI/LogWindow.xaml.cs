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

// ─── Log levels ──────────────────────────────────────────────────────────────
// Verbose : background noise (heartbeats, per-segment dumps, clipboard plumbing)
//           — only visible in "All" filter.
// Info    : normal workflow events (recording, return codes, text, copy, paste).
// Step    : rare verified milestones (model loaded, end-to-end OK).
// Warning : non-fatal issues (focus loss, empty buffers, repetition detected).
// Error   : failures (init errors, transcription failures, mic unavailable).
public enum LogLevel { Verbose, Info, Step, Warning, Error }

// SelectorBar mode — single active at a time (native exclusive selection).
internal enum LogFilterMode { All, Steps, Critical }

// Level-based template selector — 5 templates per wrap dimension. Instantiated
// twice in XAML resources (NoWrapSelector / WrapSelector), swapped on toggle.
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

// ─── Log window ──────────────────────────────────────────────────────────────
//
// Custom title bar (ExtendsContentIntoTitleBar) with centered search field.
// Mica + system theme (light/dark auto, no forced RequestedTheme).
// SelectorBar All/Activity/Errors.
// CommandBar: Copy/Save/Clear (buttons) + Auto-scroll/Word wrap (toggles).
// Live search via AutoSuggestBox.
//
// Model:
//   _entries : full buffer (cap 5000)
//   _visible : displayed subset (level filter + search)
// Copy/Save operate on _visible — the user copies what they see.

public sealed partial class LogWindow : Window
{
    internal sealed class LogEntry
    {
        public DateTimeOffset Timestamp { get; }
        public string Source { get; }
        public string Message { get; }
        public LogLevel Level { get; }

        /// <summary>Formatted display text, computed once at creation.</summary>
        public string Text { get; }

        public LogEntry(string source, string message, LogLevel level)
        {
            Timestamp = DateTimeOffset.Now;
            Source = source;
            Message = message;
            Level = level;
            Text = source.Length > 0
                ? $"{Timestamp:HH:mm:ss.fff} [{Source}] {Message}"
                : $"{Timestamp:HH:mm:ss.fff} {Message}";
        }
    }

    private readonly List<LogEntry> _entries = new();
    private readonly ObservableCollection<LogEntry> _visible = new();
    private readonly IntPtr _hwnd;
    private bool _isVisible;

    // App icons — same assets as TrayIconManager via IconAssets.
    // Single source of truth: changing an .ico propagates to tray + beacon + window icon.
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

        // Shift+wheel → horizontal scroll. The ListView's internal ScrollViewer
        // marks PointerWheelChanged as handled; AddHandler with
        // handledEventsToo=true to intercept anyway.
        LogItems.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnLogPointerWheel),
            handledEventsToo: true);

        // Click-to-copy + drag-to-select: PointerPressed/Released are marked
        // handled by the ListView for its own selection management, so
        // AddHandler with handledEventsToo=true.
        LogItems.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnLogPointerPressed),
            handledEventsToo: true);
        LogItems.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnLogPointerReleased),
            handledEventsToo: true);

        // App icons: resolved once, shared with tray.
        LoadAppIcons();
        AppTitleBarIcon.ImageSource = _iconIdle;
        if (_iconIdlePath is not null) AppWindow.SetIcon(_iconIdlePath);

        // Native title bar: ExtendsContentIntoTitleBar + SetTitleBar is still
        // required for the TitleBar control to replace the system title bar.
        // Height, drag region, themed caption buttons are handled by the control.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        // Tall caption buttons to stay aligned with the interactive content
        // (SearchBox) in the TitleBar. The TitleBar control manages its own
        // chrome height, but system caption buttons are still driven by
        // AppWindow.TitleBar.PreferredHeightOption.
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;

        // Mica: translucent backdrop that follows system theme colors.
        // Win11 required (OK here); falls back to transparent otherwise.
        SystemBackdrop = new MicaBackdrop();

        // Initial SelectorBar selection.
        LevelSelector.SelectedItem = LevelFull;

        Title = "WhispUI Logs";
        // ~1:2 aspect ratio (vertical) — two stacked squares. Fits on a 4K display.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(960, 1440));

        // Standard window: min, max, resize.
        // Min size: prevents the responsive command bar from being crushed
        // below its tightest threshold (400 px = everything in the More
        // flyout, search hidden).
        var presenter = OverlappedPresenter.Create();
        presenter.IsMinimizable = true;
        presenter.IsMaximizable = true;
        presenter.IsResizable   = true;
        presenter.PreferredMinimumWidth  = 400;
        presenter.PreferredMinimumHeight = 300;
        AppWindow.SetPresenter(presenter);

        // Close → hide, don't destroy. The instance is reused via tray.
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            _isVisible = false;
            AppWindow.Hide();
        };
    }

    // ── Public API (thread-safe) ─────────────────────────────────────────────

    public void LogVerbose(string source, string message) => AppendEntry(source, message, LogLevel.Verbose);
    public void Log(string source, string message)        => AppendEntry(source, message, LogLevel.Info);
    public void LogStep(string source, string message)    => AppendEntry(source, message, LogLevel.Step);
    public void LogWarning(string source, string message) => AppendEntry(source, message, LogLevel.Warning);
    public void LogError(string source, string message)   => AppendEntry(source, message, LogLevel.Error);

    public void Clear()
    {
        if (DispatcherQueue.HasThreadAccess) ClearAll();
        else DispatcherQueue.TryEnqueue(ClearAll);
    }

    // Beacon app icon (red = recording / grey = idle). Called from
    // WhispEngine.StatusChanged via App.xaml.cs. Thread-safe.
    public void SetRecordingState(bool isRecording)
    {
        if (DispatcherQueue.HasThreadAccess) ApplyRecordingState(isRecording);
        else DispatcherQueue.TryEnqueue(() => ApplyRecordingState(isRecording));
    }

    private void ApplyRecordingState(bool isRecording)
    {
        _isRecording = isRecording;
        // Mutating ImageSource in-place on the existing ImageIconSource does not
        // propagate visually to TitleBar (no routed PropertyChanged). Fix:
        // rebuild a complete ImageIconSource and reassign IconSource.
        AppTitleBar.IconSource = new Microsoft.UI.Xaml.Controls.ImageIconSource
        {
            ImageSource = isRecording ? _iconRecording : _iconIdle,
        };

        // Window icon (titlebar + taskbar + alt-tab): follows the same state.
        // AppWindow.SetIcon expects an .ico file path on disk.
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
            DebugLog.Write("LOGWIN", "idle icon not found");

        if (_iconRecordingPath is not null)
            _iconRecording = new BitmapImage(new Uri(_iconRecordingPath));
        else
            DebugLog.Write("LOGWIN", "recording icon not found");
    }

    private void ClearAll()
    {
        _entries.Clear();
        _visible.Clear();
        _itemsPanel = null;
    }

    // Open from tray: restore if minimized, show, activate.
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

        // WinUI 3: Activate() doesn't always bring the window to front when
        // called from a tray callback. SetForegroundWindow from the same
        // process is allowed (AnchorWindow already holds foreground).
        NativeMethods.SetForegroundWindow(_hwnd);
    }

    // ── Implementation ─────────────────────────────────────────────────────────

    private void AppendEntry(string source, string message, LogLevel level)
    {
        var entry = new LogEntry(source, message, level);
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
            // Ref equality (LogEntry is a class) → no collision possible
            // on two entries with the same text.
            _visible.Remove(removed);
        }

        if (Matches(entry)) _visible.Add(entry);

        if (!_isVisible) return;
        if (AutoScrollToggle?.IsChecked != true) return;

        ScrollToBottom();
    }

    private bool Matches(LogEntry e)
    {
        // Level filter:
        //   All      → everything passes (including Verbose)
        //   Steps    → Activity: Info + Step + Warning + Error (Verbose hidden)
        //   Critical → Errors: Warning + Error only
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
        // The ListView's internal ScrollViewer is only available after the first
        // layout pass. We find it in the visual tree and cache it.
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
        // Hide Copy badge during scroll.
        CopyBadge.Visibility = Visibility.Collapsed;

        // Shift not pressed → let the ScrollViewer handle native vertical scroll.
        var mods = e.KeyModifiers;
        if ((mods & VirtualKeyModifiers.Shift) == 0) return;

        var sv = GetListViewScrollViewer();
        if (sv is null) return;

        // In wrap mode, horizontal scroll is disabled: nothing to do.
        if (sv.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled) return;

        var point = e.GetCurrentPoint(LogItems);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;

        // Windows convention: 120 units = one notch. Scroll ~80 px per notch,
        // inverted from delta (delta>0 = wheel up = scroll left).
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

        // In wrap mode: disable horizontal scroll so the ScrollViewer gives
        // finite width to its content (otherwise TextWrapping doesn't know
        // where to break). Attached property on the ListView.
        ScrollViewer.SetHorizontalScrollBarVisibility(LogItems,
            wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);

        // Invalidate the internal ScrollViewer cache (same object, but
        // its visibility changed).
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

    // ── Click-to-copy + drag-to-select + floating badge ────────────────────
    //
    // Hover: "Copy" badge to the right of the hovered line.
    // Simple click (press+release): copies 1 line, "Copied" feedback.
    // Click+drag: visual selection (Extended) of traversed lines,
    //   "Copy selection" badge, copies on release, deselects.

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

        // Position badge to the right of the item under the pointer.
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

                // Native visual selection via SelectRange (Extended mode).
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

        // Copy all selected lines in display order.
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

        // Full deselection — nothing persists.
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

    // ── Copy button (CommandBar) ─────────────────────────────────────────────

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        // Copy all visible entries.
        var sb = new StringBuilder();
        foreach (var entry in _visible) sb.AppendLine(entry.Text);
        CopyToClipboard(sb.ToString());
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker();
            // WinUI 3 unpackaged: the picker needs the parent HWND.
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
