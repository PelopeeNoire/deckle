using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;

namespace WhispUI;

// ─── Fenêtre Settings ─────────────────────────────────────────────────────────
//
// Famille visuelle calquée sur LogWindow v3 : Mica + thème système, title bar
// custom 48px en 3 colonnes (drag gauche / search centre / réserve 138 caption),
// Close→Cancel+Hide (jamais détruite, créée une fois dans App.OnLaunched).
//
// Layout principal : NavigationView adaptatif (Left ≥960 / LeftCompact <960).
// Le contenu interne est un Grid sticky-header / Frame de navigation / sticky
// footer. Chaque Page (General, Whisper, LLM) gère son propre ScrollViewer et
// son padding — pattern canonique Microsoft Learn pour une settings page.

public sealed partial class SettingsWindow : Window
{
    private readonly IntPtr _hwnd;

    private BitmapImage? _iconIdle;
    private string? _iconIdlePath;

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
            AppTitleBarIcon.ImageSource = _iconIdle;
            AppWindow.SetIcon(_iconIdlePath);
        }

        // Title bar natif : hauteur/drag/caption gérés par le contrôle.
        // PreferredHeightOption=Tall agrandit les caption buttons système pour
        // rester alignés avec le contenu interactif (SearchBox) du TitleBar.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;

        SystemBackdrop = new MicaBackdrop();

        // Sélection initiale — on navigue d'abord, puis on synchronise la
        // sélection. L'ordre évite un double Navigate si SelectionChanged
        // déclenche une seconde navigation vers le même type.
        PageFrame.Navigate(typeof(Settings.GeneralPage));
        Nav.SelectedItem = NavGeneral;
        PageTitle.Text = "General";

        Title = "WhispUI Settings";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));

        var presenter = OverlappedPresenter.Create();
        presenter.IsMinimizable = true;
        presenter.IsMaximizable = true;
        presenter.IsResizable   = true;
        // Min : avec PaneDisplayMode=Left le pane (280) reste toujours
        // inline ; sous 640 le contenu devient illisible.
        // Min 390 : on veut exposer le breakpoint <390 (LeftMinimal + hamburger
        // titlebar). Hauteur gardée à 400 pour la lisibilité verticale.
        presenter.PreferredMinimumWidth  = 480;
        presenter.PreferredMinimumHeight = 400;
        AppWindow.SetPresenter(presenter);

        // Close → cache, ne détruit pas. Réutilisée via le tray.
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            AppWindow.Hide();
        };

        // Adaptive layout : 4 breakpoints documentés dans le Figma.
        RootGrid.SizeChanged += OnRootSizeChanged;
        ApplyAdaptiveLayout(RootGrid.ActualWidth);
    }

    // ── Adaptive layout ─────────────────────────────────────────────────────
    //
    //   ≥ 960 : PaneDisplayMode=Left (240, inline, figé, pas de hamburger)
    //   < 960 : LeftCompact (48 rail) + hamburger dans la TitleBar pour ouvrir
    //           le pane en overlay temporaire
    //
    // Recherche : full ≥ 580, icône loupe sinon (expand au clic).
    //
    // Pattern canonique Microsoft : PaneDisplayMode bascule live, aucun custom
    // template. Cf. learn.microsoft.com/windows/apps/develop/ui/controls/navigationview.

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyAdaptiveLayout(e.NewSize.Width);

    private void ApplyAdaptiveLayout(double width)
    {
        bool wide = width >= 960;
        Nav.PaneDisplayMode = wide
            ? NavigationViewPaneDisplayMode.Left
            : NavigationViewPaneDisplayMode.LeftCompact;

        // Hamburger dans la TitleBar dès qu'on est en Compact, pour pouvoir
        // ouvrir le pane (sinon le rail 48 ne permet pas d'expand).
        AppTitleBar.IsPaneToggleButtonVisible = !wide;

        bool fullSearch = width >= 580;
        SearchBox.Visibility = fullSearch ? Visibility.Visible : Visibility.Collapsed;
        SearchIconButton.Visibility = fullSearch ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPaneToggleRequested(Microsoft.UI.Xaml.Controls.TitleBar sender, object args)
    {
        Nav.IsPaneOpen = !Nav.IsPaneOpen;
    }

    private void OnSearchIconClick(object sender, RoutedEventArgs e)
    {
        // Expand temporaire : on bascule en full search le temps que l'utilisateur
        // tape. Focus direct pour enchaîner.
        SearchIconButton.Visibility = Visibility.Collapsed;
        SearchBox.Visibility = Visibility.Visible;
        SearchBox.Focus(FocusState.Programmatic);
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

        (Type pageType, string title) = tag switch
        {
            "general"  => (typeof(Settings.GeneralPage), "General"),
            "whisper"  => (typeof(Settings.WhisperPage), "Whisper configuration"),
            "llm"      => (typeof(Settings.LlmPage),     "LLM Rewriting"),
            _          => (typeof(Settings.GeneralPage), "General"),
        };

        // Évite un re-Navigate redondant si on est déjà sur la page cible
        // (SelectionChanged peut se déclencher plusieurs fois lors du setup).
        if (PageFrame.CurrentSourcePageType != pageType)
        {
            PageFrame.Navigate(
                pageType,
                null,
                new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
        }
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

}
