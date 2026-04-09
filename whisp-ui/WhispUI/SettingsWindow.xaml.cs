using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;

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

        SystemBackdrop = new MicaBackdrop();

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
    //
    // Pattern canonique Microsoft Learn (sample §"Code example") : le Tag de
    // l'item porte le nom complet du type de Page, résolu par Type.GetType.
    // Garde CurrentSourcePageType != pageType pour éviter un re-Navigate
    // redondant au setup initial.

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        App.Log?.LogVerbose($"[SETTINGS] SelectionChanged fired, item={(args.SelectedItem as NavigationViewItem)?.Content}");

        if (args.SelectedItem is not NavigationViewItem item)
        {
            App.Log?.LogVerbose("[SETTINGS] SelectedItem n'est pas un NavigationViewItem — ignoré");
            return;
        }
        if (item.Tag is not string tag)
        {
            App.Log?.LogWarning($"[SETTINGS] Item '{item.Content}' sans Tag — nav impossible");
            return;
        }
        if (tag == "logs") return;

        var pageType = Type.GetType(tag);
        if (pageType is null)
        {
            App.Log?.LogError($"[SETTINGS] Type introuvable pour tag '{tag}'");
            return;
        }

        if (PageFrame.CurrentSourcePageType == pageType)
        {
            App.Log?.LogVerbose($"[SETTINGS] {pageType.Name} déjà courante — pas de re-nav");
            return;
        }

        App.Log?.Log($"[SETTINGS] Navigate → {pageType.Name}");
        try
        {
            bool ok = PageFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
            if (!ok)
            {
                App.Log?.LogError($"[SETTINGS] Navigate({pageType.Name}) a retourné false");
            }
            else
            {
                App.Log?.LogStep($"[SETTINGS] {pageType.Name} navigation OK");
            }
        }
        catch (Exception ex)
        {
            App.Log?.LogError($"[SETTINGS] Navigate({pageType.Name}) THREW {ex.GetType().Name}: {ex.Message}");
            App.Log?.LogError(ex.StackTrace ?? "(no stack)");
            // Fallback fichier — au cas où la LogWindow elle-même est dans un état pourri.
            DebugLog.Write("SETTINGS", $"Navigate({pageType.Name}) THREW: {ex}");
        }
    }

    // Item footer "Logs" : SelectsOnInvoked=False donc pas de SelectionChanged,
    // on passe par ItemInvoked pour récupérer le clic et déléguer à App qui
    // ouvre la LogWindow partagée.
    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        var item = args.InvokedItemContainer as NavigationViewItem;
        App.Log?.LogVerbose($"[SETTINGS] ItemInvoked: {item?.Content} (tag={item?.Tag})");
        if (item?.Tag as string == "logs")
        {
            App.Log?.Log("[SETTINGS] Ouverture LogWindow via footer");
            OnShowLogsRequested?.Invoke();
        }
    }
}
