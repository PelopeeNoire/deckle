using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using WinRT.Interop;

namespace WhispUI;

// ─── Fenêtre Settings ─────────────────────────────────────────────────────────
//
// Famille visuelle calquée sur LogWindow v3 : Mica + thème système, title bar
// custom 48px en 3 colonnes (drag gauche / search centre / réserve 138 caption),
// Close→Cancel+Hide (jamais détruite, créée une fois dans App.OnLaunched).
//
// Layout principal : NavigationView Left (responsive natif Auto → Compact →
// Minimal selon largeur). Le contenu interne est un Grid sticky-header /
// scrollable / sticky-footer qui se réutilise pour les 3 pages (General,
// Whisper, LLM Rewriting). Les pages sont des UIElement placeholders pour
// l'instant — le contenu réel sera ajouté plus tard.

public sealed partial class SettingsWindow : Window
{
    private readonly IntPtr _hwnd;

    private BitmapImage? _iconIdle;
    private string? _iconIdlePath;

    // Placeholders de pages — instanciés une fois, swap via PageContent.Content.
    private readonly UIElement _pageGeneral;
    private readonly UIElement _pageWhisper;
    private readonly UIElement _pageLlm;

    // Callback injecté par App pour ouvrir la LogWindow partagée depuis l'item
    // footer "Logs" de la NavigationView. Laissé null = item sans effet.
    public Action? OnShowLogsRequested { get; set; }

    public SettingsWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        // Icône app — partagée avec tray / LogWindow.
        _iconIdlePath = IconAssets.ResolvePath(recording: false);
        if (_iconIdlePath is not null)
        {
            _iconIdle = new BitmapImage(new Uri(_iconIdlePath));
            AppIconBeacon.Source = _iconIdle;
            AppWindow.SetIcon(_iconIdlePath);
        }

        // Title bar custom : même pattern que LogWindow (extend + Tall + drag
        // sur la grid entière, passthrough recalculé pour la SearchBox).
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SetTitleBar(AppTitleBar);

        SearchBox.SizeChanged += (_, _) => SetupDragPassthrough();
        SearchBox.Loaded      += (_, _) => SetupDragPassthrough();

        RootGrid.ActualThemeChanged += (_, _) => RefreshTitleBarButtonColors();
        RefreshTitleBarButtonColors();

        SystemBackdrop = new MicaBackdrop();

        // Placeholders de pages — chacun expliqué en français pour rappeler
        // que la vraie spec produit viendra dans une étape ultérieure.
        _pageGeneral  = BuildPlaceholder("Paramètres généraux à venir.");
        _pageWhisper  = BuildPlaceholder("Configuration Whisper à venir (modèle, threads, langue, paramètres avancés).");
        _pageLlm      = BuildPlaceholder("Réécriture LLM à venir (provider, modèle, prompt système, hotkey).");

        // Sélection initiale.
        Nav.SelectedItem = NavGeneral;
        PageContent.Content = _pageGeneral;
        PageTitle.Text = "General";

        Title = "WhispUI Settings";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));

        var presenter = OverlappedPresenter.Create();
        presenter.IsMinimizable = true;
        presenter.IsMaximizable = true;
        presenter.IsResizable   = true;
        // Min : sous ce seuil le NavigationView passe Minimal et le contenu
        // reste lisible. La SearchBox disparaît à 650 (cf. OnRootSizeChanged).
        presenter.PreferredMinimumWidth  = 480;
        presenter.PreferredMinimumHeight = 400;
        AppWindow.SetPresenter(presenter);

        // Close → cache, ne détruit pas. Réutilisée via le tray.
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            AppWindow.Hide();
        };
    }

    private static UIElement BuildPlaceholder(string text)
    {
        return new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        };
    }

    public void ShowAndActivate()
    {
        if (AppWindow.Presenter is OverlappedPresenter op &&
            op.State == OverlappedPresenterState.Minimized)
        {
            op.Restore();
        }

        AppWindow.Show();
        this.Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
    }

    // ── NavigationView : swap de page ────────────────────────────────────────

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        var tag = item.Tag as string;

        (UIElement page, string title) = tag switch
        {
            "general"  => (_pageGeneral,  "General"),
            "whisper"  => (_pageWhisper,  "Whisper configuration"),
            "llm"      => (_pageLlm,      "LLM Rewriting"),
            _          => (_pageGeneral,  "General"),
        };

        PageContent.Content = page;
        PageTitle.Text = title;
    }

    // Item footer "Logs" : SelectsOnInvoked=False donc pas de SelectionChanged,
    // on passe par ItemInvoked pour récupérer le clic et déléguer à App qui
    // ouvre la LogWindow partagée.
    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item &&
            item.Tag as string == "logs")
        {
            OnShowLogsRequested?.Invoke();
        }
    }

    // ── Boutons header / footer (stubs) ──────────────────────────────────────

    private void OnResetAllClick(object sender, RoutedEventArgs e)
    {
        // TODO : reset des paramètres de la page courante quand le contenu
        // réel sera branché.
        DebugLog.Write("SETTINGS", "Reset all (stub)");
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        AppWindow.Hide();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // TODO : persistance quand le contenu réel sera branché.
        DebugLog.Write("SETTINGS", "Save (stub)");
        AppWindow.Hide();
    }

    // ── Theme : caption buttons ──────────────────────────────────────────────

    private void RefreshTitleBarButtonColors()
    {
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

    // ── Drag passthrough pour la SearchBox (calque LogWindow) ────────────────

    private void ClearDragPassthrough()
    {
        try
        {
            var nonClient = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            nonClient.SetRegionRects(NonClientRegionKind.Passthrough, Array.Empty<Windows.Graphics.RectInt32>());
        }
        catch (Exception ex)
        {
            DebugLog.Write("SETTINGS", $"passthrough clear err: {ex.Message}");
        }
    }

    private void SetupDragPassthrough()
    {
        if (SearchBox.Visibility == Visibility.Collapsed)
        {
            ClearDragPassthrough();
            return;
        }
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
            DebugLog.Write("SETTINGS", $"passthrough err: {ex.Message}");
        }
    }

    // ── Responsive : SearchBox masquée sous 650 px (calque LogWindow) ───────

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var newSearchVis = e.NewSize.Width >= 650
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (SearchBox.Visibility != newSearchVis)
        {
            SearchBox.Visibility = newSearchVis;
            SetupDragPassthrough();
        }
    }
}
