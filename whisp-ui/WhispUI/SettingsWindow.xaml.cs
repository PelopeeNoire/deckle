using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
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

        // Caption buttons : backgrounds transparents (Mica visible), foreground
        // calé sur le thème courant. ActualThemeChanged met à jour en live si
        // l'utilisateur bascule light/dark dans les Settings Windows.
        UpdateCaptionButtonColors(RootGrid.ActualTheme);
        RootGrid.ActualThemeChanged += (s, _) =>
            UpdateCaptionButtonColors(((FrameworkElement)s).ActualTheme);

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

    // ── Caption buttons : couleurs calées sur le thème ─────────────────────
    //
    // Le contrôle TitleBar natif ne propage pas toujours le thème aux caption
    // buttons système quand ExtendsContentIntoTitleBar est actif. On les pose
    // explicitement : backgrounds transparents (Mica visible), foreground
    // adapté au thème (pattern doc Microsoft Learn "Color and transparency in
    // caption buttons").

    private void UpdateCaptionButtonColors(ElementTheme theme)
    {
        var tb = AppWindow.TitleBar;

        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;

        if (theme == ElementTheme.Dark)
        {
            tb.ButtonForegroundColor         = Colors.White;
            tb.ButtonHoverForegroundColor     = Colors.White;
            tb.ButtonPressedForegroundColor   = Colors.White;
            tb.ButtonHoverBackgroundColor     = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
            tb.ButtonPressedBackgroundColor   = Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF);
            tb.ButtonInactiveForegroundColor  = Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
        }
        else
        {
            tb.ButtonForegroundColor         = Colors.Black;
            tb.ButtonHoverForegroundColor     = Colors.Black;
            tb.ButtonPressedForegroundColor   = Colors.Black;
            tb.ButtonHoverBackgroundColor     = Color.FromArgb(0x33, 0x00, 0x00, 0x00);
            tb.ButtonPressedBackgroundColor   = Color.FromArgb(0x66, 0x00, 0x00, 0x00);
            tb.ButtonInactiveForegroundColor  = Color.FromArgb(0x99, 0x00, 0x00, 0x00);
        }
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
            App.Log?.LogVerbose($"[SETTINGS] {pageType.Name} déjà courante — ignoré");
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
