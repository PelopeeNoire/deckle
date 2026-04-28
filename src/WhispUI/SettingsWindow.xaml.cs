using System.Linq;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;
using WhispUI.Interop;
using WhispUI.Logging;
using WhispUI.Shell;

namespace WhispUI;

// ─── Fenêtre Settings ─────────────────────────────────────────────────────────
//
// Mica + thème système, TitleBar natif (icône + titre). NavigationView en
// PaneDisplayMode=Auto qui gère lui-même la bascule Left / LeftCompact /
// LeftMinimal et son propre burger natif. La recherche est dans le slot
// canonique NavigationView.AutoSuggestBox (pattern Microsoft Learn).
//
// Navigation : Tag sur chaque item = nom complet du type de Page, résolu via
// Type.GetType dans OnNavSelectionChanged (pattern du sample officiel
// Microsoft Learn §"Code example").
//
// Auto-save partout, donc pas de Cancel/Save global. Close → cache, ne
// détruit pas (créée une fois dans App.OnLaunched).

public sealed partial class SettingsWindow : Window
{
    private static readonly LogService _log = LogService.Instance;
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
        // rester alignés avec le contenu interactif (AutoSuggestBox hébergé
        // par NavigationView juste en dessous).
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;

        // Mica disabled across all windows — investigating a 1-2 s lag on
        // move/resize affecting Settings, Log, Setup, and even other apps
        // (VS, etc.) when WhispUI is running. Hypothesis: cumulative DWM
        // compositing cost across multiple Mica surfaces (we instantiate
        // 4 Mica windows at boot). With backdrop=null the window keeps
        // its system theme background; visual polish loss is the trade-off
        // until we identify the root cause. Cf. WinUI issue #5148, #10820.
        // SystemBackdrop = new MicaBackdrop();

        // NavigationView : quand le mode bascule en Minimal (hamburger visible),
        // le pane toggle button occupe ~48 px en haut de la zone contenu.
        // On pousse le Frame vers le bas pour que le titre de page ne chevauche
        // pas le hamburger (pattern Windows Terminal Settings).
        Nav.DisplayModeChanged += OnNavDisplayModeChanged;

        // Sélection initiale → déclenche SelectionChanged → navigation vers
        // GeneralPage. Un seul chemin de navigation, pas de double-nav.
        Nav.SelectedItem = Nav.MenuItems[0];

        Title = "WhispUI Settings";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(960, 1440));

        var presenter = OverlappedPresenter.Create();
        presenter.IsMinimizable = true;
        presenter.IsMaximizable = true;
        presenter.IsResizable   = true;
        // Min cohérent avec les breakpoints NavigationView Auto (640/1008).
        // On descend sous 640 pour exposer le mode LeftMinimal natif.
        presenter.PreferredMinimumWidth  = 320;
        presenter.PreferredMinimumHeight = 400;
        AppWindow.SetPresenter(presenter);

        // Close → cache, ne détruit pas. Réutilisée via le tray.
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            AppWindow.Hide();
        };
    }

    public void ShowAndActivate(string? pageTag = null)
    {
        if (AppWindow.Presenter is OverlappedPresenter op &&
            op.State == OverlappedPresenterState.Minimized)
        {
            op.Restore();
        }

        // Si un tag de page est demandé, sélectionner l'item nav correspondant.
        // La sélection déclenche OnNavSelectionChanged → navigation Frame.
        if (pageTag is not null)
        {
            foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
            {
                if (item.Tag as string == pageTag)
                {
                    Nav.SelectedItem = item;
                    break;
                }
            }
        }

        AppWindow.Show();
        this.Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
    }

    // ── NavigationView : marge contenu selon le DisplayMode ──────────────
    //
    // En mode Minimal, le pane toggle button (hamburger) est rendu en haut de
    // la zone contenu et occupe ~48 px. On décale le Frame vers le bas pour
    // que le titre H1 de la page ne soit pas à la même hauteur que le burger.
    // Pattern identique à Windows Terminal Settings.

    private void OnNavDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        PageFrame.Margin = sender.DisplayMode == NavigationViewDisplayMode.Minimal
            ? new Thickness(0, 48, 0, 0)
            : new Thickness(0);
    }

    // ── NavigationView : swap de page ────────────────────────────────────────
    //
    // Canonical Microsoft Learn pattern (sample §"Code example"): the item's Tag
    // carries the full Page type name, resolved by Type.GetType.
    // Keeps CurrentSourcePageType != pageType to avoid redundant re-Navigate
    // on initial setup.

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        _log.Verbose(LogSource.Settings, $"selection changed | item={(args.SelectedItem as NavigationViewItem)?.Content}");

        if (args.SelectedItem is not NavigationViewItem item)
        {
            _log.Verbose(LogSource.Settings, "selection ignored | reason=not-navview-item");
            return;
        }
        if (item.Tag is not string tag)
        {
            _log.Warning(LogSource.Settings, $"nav impossible | reason=no-tag | item={item.Content}");
            return;
        }
        if (tag == "logs") return;

        var pageType = Type.GetType(tag);
        if (pageType is null)
        {
            _log.Error(LogSource.Settings, $"nav failed | reason=type-not-found | tag={tag}");
            return;
        }

        if (PageFrame.CurrentSourcePageType == pageType)
        {
            _log.Verbose(LogSource.Settings, $"nav skipped | reason=already-current | page={pageType.Name}");
            return;
        }

        _log.Info(LogSource.Settings, $"Navigate to {pageType.Name}");
        try
        {
            bool ok = PageFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
            if (!ok)
            {
                _log.Error(LogSource.Settings, $"navigate failed | page={pageType.Name} | reason=frame-returned-false");
            }
            else
            {
                _log.Success(LogSource.Settings, $"Navigated to {pageType.Name}");
            }
        }
        catch (Exception ex)
        {
            _log.Error(LogSource.Settings, $"navigate threw | page={pageType.Name} | error={ex.GetType().Name}: {ex.Message}");
            _log.Error(LogSource.Settings, ex.StackTrace ?? "(no stack)");
        }
    }

    // Footer item "Logs": SelectsOnInvoked=False so no SelectionChanged,
    // we go through ItemInvoked to capture the click and delegate to App
    // which opens the shared LogWindow.
    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        var item = args.InvokedItemContainer as NavigationViewItem;
        _log.Verbose(LogSource.Settings, $"item invoked | content={item?.Content} | tag={item?.Tag}");
        if (item?.Tag as string == "logs")
        {
            _log.Info(LogSource.Settings, "Open logs from footer");
            OnShowLogsRequested?.Invoke();
        }
    }
}
