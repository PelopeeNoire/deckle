# LogWindow — structure et modele de donnees

## Structure generale

Fenetre classique (`OverlappedPresenter`, min/max/resize), Close->Cancel+Hide, theme systeme, `SystemBackdrop = MicaBackdrop`. `PreferredMinimumWidth=400`, `PreferredMinimumHeight=300`.

## Title bar

`Microsoft.UI.Xaml.Controls.TitleBar` natif (Windows App SDK 1.8). Caption buttons **Tall** via `AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall`. `ExtendsContentIntoTitleBar=true` + `SetTitleBar(AppTitleBar)`.

Icone app : beacon enregistrement (rouge) / idle (gris) via `SetRecordingState`, reconstruit un `ImageIconSource` complet a chaque bascule (muter `ImageSource` in-place ne propage pas visuellement). Icone fenetre (`AppWindow.SetIcon`) suit le meme etat.

La recherche (`AutoSuggestBox`) est dans `TitleBar.Content`, pas dans un slot custom. Pattern Win11 Settings. Pas de swap icone/field, l'AutoSuggestBox retrecit naturellement jusqu'a `MinWidth=240`.

## Command bar

Sous la title bar, 2 zones :
- **Gauche** — `SelectorBar` Full / Filtered / Critical
- **Droite** — `CommandBar` avec `IsDynamicOverflowEnabled` (true par defaut) + `DynamicOverflowOrder` pour l'overflow responsif. Groupe 2 (Copy/Save/Clear/Sep) migre en premier, puis groupe 1 (AutoScroll/Wrap).

Actions : Copy (E8C8) / Save (E74E) / Clear (E74D) + Auto scroll (EC8F toggle, on par defaut) / Wrap (E751 toggle).

## Modele de donnees

`enum LogLevel { Verbose, Info, Step, Warning, Error }` (public). API thread-safe (marshal via `DispatcherQueue`).

Deux collections :
- `_entries` — `List<LogEntry>`, tampon complet
- `_visible` — `ObservableCollection<LogEntry>` bindee a `LogItems.ItemsSource`

Filtre = `Matches()` qui combine selector (All = tout, Steps = masque Verbose, Critical = Warning + Error) + recherche live (`IndexOf` case-insensitive). `ApplyFilter()` rebuild `_visible`.

Cap 5000 entrees. Sur overflow, retire la plus vieille des deux collections (ref equality, `LogEntry` est une classe). Copy/Save operent sur `_visible` — l'utilisateur copie ce qu'il voit.

## Couleurs — ThemeDictionaries XAML

Bindees via `ThemeResource` dans les DataTemplates XAML (`Grid.Resources > ThemeDictionaries`). Theme switch runtime automatique.

- **Verbose** — herite de `TextFillColorPrimaryBrush` (blanc/noir selon theme)
- **Info** — `LogInfoBrush` custom (bleu semantique Fluent `#005FB7` light / `#60CDFF` dark, pas l'accent systeme)
- **Step** — `SystemFillColorSuccessBrush` (vert)
- **Warning** — `SystemFillColorCautionBrush` (orange)
- **Error** — `SystemFillColorCriticalBrush` (rouge)

## Templates et selector

10 DataTemplates (5 niveaux x 2 dimensions wrap). `LogLevelTemplateSelector` (classe C#, herite `DataTemplateSelector`) instancie deux fois en XAML (NoWrapSelector / WrapSelector).

Piege WinUI 3 : `ItemsControl.ItemTemplateSelector` n'est pas honore a l'execution (seul `ListViewBase` le respecte). Contournement : `ItemTemplate` pointe sur un `ContentControl` wrapper dont le `ContentTemplateSelector` est le bon selector. Le toggle Wrap swap `ItemTemplate` entre `NoWrapRoot` et `WrapRoot`.

## Wrap — piege scroll horizontal

Le toggle swap le template **et** bascule `HorizontalScrollBarVisibility` entre `Auto` et `Disabled`. Sans ca, `TextWrapping="Wrap"` ne s'applique pas — le `ScrollViewer` mesure son contenu en largeur infinie tant que le scroll horizontal est autorise.

Shift+molette -> scroll horizontal via `AddHandler(PointerWheelChangedEvent, ..., handledEventsToo: true)` car le ScrollViewer marque l'event handled pour son propre scroll vertical.

## Filets de diagnostic globaux

Dans `App` : `Application.UnhandledException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` -> `DebugLog` (prefixes `CRASH` / `CRASH-AD` / `CRASH-TS`).
